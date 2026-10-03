using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// Reading stored gates and reveal rules for the read path: in play, "the party knows", gate status, secret status and
/// the active vocabulary rules, all at the scope's point in time.
///
/// <para>
/// <b>Stored gates are read with <see cref="FactGates.TryParse"/>, never <see cref="FactGates.Parse"/>:</b> the schema
/// only checks that a gate is a JSON object, so a hand-edited gate can be unreadable, and a throwing parse would fail
/// every read that touches that fact with a message telling the model to fix a gate it never sent. An unreadable gate is
/// skipped (no vocabulary, no status) and reported to the author view only. Reveal rules likewise go through
/// <see cref="RevealRuleData.TryParse"/>.
/// </para>
/// <para>
/// "In play" and "the party knows" are evaluated as of the scope's session: the fact row is the replayed one (its canon
/// status and established session then), and the party's verdict is the scope's (<see cref="ReadScope.VerdictFor(PerspectiveContext, FactState)"/>),
/// which uses only knowledge rows that applied then and gives no knowledge by visibility, or by a group's row with no
/// session, to a fact established after the session. That is what makes "was the word 'seal' still forbidden in session
/// 10?" answerable after the axe was assembled in session 11, and what keeps a party-visible clue established in session 5
/// from completing a route as of session 4: the route, the gate's readiness, the secret's derived status and the active
/// vocabulary are all as of the session, as every read of the party's is.
/// </para>
/// <para>
/// <b>"The party knows" means the party's verdict is Knows and its state is not <c>suspects</c>, on a fact that is not
/// author-only</b>, the write path's rule (W's <c>GateEvaluation</c>), which stores the secret status this class derives
/// again for reading: the two are shown side by side to the author, so any difference in the rule would show a stored
/// status that disagrees with the derived one for no change in knowledge. A suspected fact is not a known one: a
/// suspected gated fact makes a secret partial, never revealed, and a suspected clue does not complete a route or lift a
/// vocabulary rule. An author-only fact is known to no player-side view whatever its rows say (contract §3.2: author
/// visibility is absolute; W warns when such a row is written), so a party row on one neither reveals a secret, completes
/// a route, seeds one, nor lifts the vocabulary of its gate: the party's reads never show it, and a check that let the
/// party say "seal" because of a row no party read honours would pass a line the story is not ready for.
/// </para>
/// <para>
/// <b>Stored and derived status as of a session.</b> The stored status is a field like any other, replayed with the
/// change log, and W derives it timelessly (what the party knows now) in each batch that changes the knowledge it rests
/// on. A batch with no session is never reversed by the replay, so as of a session before a clue's established session
/// the stored status can already show what that clue gave (a sessionless batch wrote a party-visible clue established in
/// session 5: stored <c>partial</c>), while the derived one, from what the party knew then, does not (<c>hidden</c> as of
/// session 4). The derived status answers "as of n"; the stored one is the record's.
/// </para>
/// </summary>
internal static class ReadGates
{
    /// <summary>A fact's stored gate: (gate, false) when readable (null gate for none), (null, true) when it is not.</summary>
    public static (GateSpec? Gate, bool Unreadable) Parse(FactRow row) =>
        FactGates.TryParse(row.Gate, out var gate) ? (gate, false) : (null, true);

    /// <summary>Whether a fact (by id) is in play at the scope's point in time.</summary>
    public static bool InPlay(ReadScope scope, string factId) =>
        scope.Fact(factId) is { } fact && scope.Loader.InPlay(fact.Row, scope.AsOf);

    /// <summary>
    /// Whether the party knows a fact (by id) at the scope's point in time: Knows, not merely suspects, and not author-only
    /// (class summary).
    /// </summary>
    public static bool PartyKnows(ReadScope scope, string factId) =>
        scope.Fact(factId) is { } fact && fact.Row.Visibility != CampaignValues.Visibilities.Author &&
        scope.VerdictFor(scope.Party, fact) is { Knows: true, State: not CampaignValues.KnowledgeStates.Suspects };

    /// <summary>Whether the party suspects a fact (by id) at the scope's point in time (never an author-only one).</summary>
    public static bool PartySuspects(ReadScope scope, string factId) =>
        scope.Fact(factId) is { } fact && fact.Row.Visibility != CampaignValues.Visibilities.Author &&
        scope.VerdictFor(scope.Party, fact) is { Knows: true, State: CampaignValues.KnowledgeStates.Suspects };

    /// <summary>Where a gate stands at the scope's point in time.</summary>
    public static GateStatus Evaluate(ReadScope scope, FactState gated, GateSpec gate)
    {
        scope.LoadFacts(FactGates.References(gate));
        return FactGates.Evaluate(gate, id => InPlay(scope, id), id => PartyKnows(scope, id), PartyKnows(scope, gated.Row.Id));
    }

    /// <summary>The gate with every fact id replaced by its handle (<c>f:12</c>), for printing.</summary>
    public static GateSpec WithHandles(ReadScope scope, GateSpec gate) => FactGates.Rewrite(gate, id => FactHandle(scope, id));

    /// <summary>A fact id's handle (<c>f:12</c>), or "(deleted fact)" for an id that no longer names a fact.</summary>
    public static string FactHandle(ReadScope scope, string factId)
    {
        var seq = scope.Connection.QueryFirstOrDefault<long?>("SELECT seq FROM fact WHERE id = @factId", new { factId });
        return seq is { } s ? "f:" + s.ToString(CultureInfo.InvariantCulture) : "(deleted fact)";
    }

    /// <summary>A gate's status as the author view prints it.</summary>
    public static GateStatusView View(ReadScope scope, FactState gated, GateSpec? gate, bool unreadable)
    {
        var knownToParty = PartyKnows(scope, gated.Row.Id);
        if (gate is null)
        {
            return new GateStatusView(gated.Ref, knownToParty, [], [], 0, 0, [], [], [], false, false, true, false, unreadable);
        }

        var status = Evaluate(scope, gated, gate);
        string H(string id) => FactHandle(scope, id);
        return new GateStatusView(
            gated.Ref,
            knownToParty,
            status.UnmetAfter.Select(H).ToList(),
            status.Routes.Select(r => new RouteView(r.Id, r.Known, r.Needed, r.Total, r.Complete)).ToList(),
            status.RoutesComplete,
            status.MinRoutes,
            status.CompleteRouteIds,
            status.UnmetPrefer.Select(H).ToList(),
            status.MustLandWith.Select(H).ToList(),
            status.ForbiddenActive,
            status.Seeded,
            status.Ready,
            status.ReachableBeforeGate,
            unreadable);
    }

    /// <summary>
    /// A secret entity's status from its gates at the scope's point in time (contract §3.4): its gated facts are the facts
    /// linked <c>about</c> it that have a gate; its clues the facts linked <c>clue_for</c> it. Null when it has no gated
    /// fact (the stored status is then hand-set and stands). As the write path derives it: revealed when the party knows
    /// every gated fact; partial when a route is complete or the party suspects or knows one gated fact of several; seeded
    /// when it knows a seed, route clue or clue_for fact; else hidden. Only readable gates take part (an unreadable one is
    /// listed, flagged, and ignored); with none readable the stored status stands, as the write path leaves it.
    /// </summary>
    public static SecretStatusView? Secret(ReadScope scope, EntityState secret)
    {
        var links = scope.Connection.Query<(string FactId, string Role)>(
            "SELECT fact_id, role FROM fact_link WHERE entity_id = @id AND role IN (@about, @clue)",
            new { id = secret.Row.Id, about = CampaignValues.FactLinkRoles.About, clue = CampaignValues.FactLinkRoles.ClueFor }).ToList();
        scope.LoadFacts(links.Select(l => l.FactId));
        var gated = new List<(FactState Fact, GateSpec? Gate, bool Unreadable)>();
        foreach (var (factId, _) in links.Where(l => l.Role == CampaignValues.FactLinkRoles.About).OrderBy(l => l.FactId, StringComparer.Ordinal))
        {
            if (scope.Fact(factId) is not { } fact || fact.Row.Gate is null)
            {
                continue;
            }

            var (gate, unreadable) = Parse(fact.Row);
            gated.Add((fact, gate, unreadable));
        }

        if (gated.Count == 0)
        {
            return null;
        }

        var views = gated.OrderBy(g => g.Fact.Row.Seq).Select(g => View(scope, g.Fact, g.Gate, g.Unreadable)).ToList();
        var readable = gated.Where(g => g.Gate is not null).ToList();
        var statuses = readable.Select(g => Evaluate(scope, g.Fact, g.Gate!)).ToList();
        if (readable.Count == 0)
        {
            return new SecretStatusView(secret.Row.Status, secret.Row.Status ?? CampaignValues.Statuses.SecretHidden, views);
        }

        var allKnown = readable.All(g => PartyKnows(scope, g.Fact.Row.Id));
        var partHeld = readable.Any(g => PartyKnows(scope, g.Fact.Row.Id) || PartySuspects(scope, g.Fact.Row.Id));
        var anyRoute = statuses.Any(s => s.RoutesComplete > 0);
        var clueKnown = links.Where(l => l.Role == CampaignValues.FactLinkRoles.ClueFor).Any(l => PartyKnows(scope, l.FactId));
        var derived = SecretStatuses.Derive(allKnown, anyRoute || partHeld, statuses.Any(s => s.Seeded) || clueKnown);
        return new SecretStatusView(secret.Row.Status, derived, views);
    }

    /// <summary>
    /// Every vocabulary rule active at the scope's point in time: gates of this campaign's facts (while forbidden_until
    /// is not in play, or while the party does not know the gated fact) and reveal rules (while an <c>until</c> fact is
    /// not in play). Each rule's source is a handle (<c>f:12</c>, <c>rule:no-name</c>); <paramref name="until"/> gets
    /// each source's lift condition as fact handles.
    /// </summary>
    public static IReadOnlyList<ForbiddenRule> ActiveVocabulary(ReadScope scope, Dictionary<string, IReadOnlyList<string>> until)
    {
        var rules = new List<ForbiddenRule>();
        var gatedIds = scope.Connection.Query<string>(
            "SELECT id FROM fact WHERE campaign_id = @campaignId AND gate IS NOT NULL" + (scope.AsOf is null ? " AND deleted_at IS NULL" : string.Empty) +
            " ORDER BY seq", new { campaignId = scope.Campaign.Id }).ToList();
        scope.LoadFacts(gatedIds);
        foreach (var id in gatedIds)
        {
            if (scope.Fact(id) is not { } fact || Parse(fact.Row) is not { Gate: { } gate } ||
                FactGates.VocabularyRule(gate, fact.Ref) is not { } rule || !Evaluate(scope, fact, gate).ForbiddenActive)
            {
                continue;
            }

            rules.Add(rule);
            until[fact.Ref] = (gate.ForbiddenUntil ?? []).Select(f => FactHandle(scope, f)).ToList();
        }

        var ruleIds = scope.Connection.Query<string>(
            "SELECT id FROM entity WHERE campaign_id = @campaignId AND kind = @kind AND subtype = @subtype" +
            (scope.AsOf is null ? " AND deleted_at IS NULL" : string.Empty) + " ORDER BY seq",
            new { campaignId = scope.Campaign.Id, kind = CampaignValues.Kinds.Rule, subtype = CampaignValues.Subtypes.RevealRule }).ToList();
        scope.LoadEntities(ruleIds);
        var resolver = new HandleResolver(scope.Connection, scope.Campaign.Id);
        foreach (var ruleId in ruleIds)
        {
            // The rule as it stood then (a rule written after the session did not exist yet).
            if (scope.Entity(ruleId) is not { } ruleEntity || ruleEntity.Row.Subtype != CampaignValues.Subtypes.RevealRule)
            {
                continue;
            }

            var (kind, slug, data) = (ruleEntity.Row.Kind, ruleEntity.Row.Slug, ruleEntity.Row.Data);
            if (!RevealRuleData.TryParse(data, out var reveal) || reveal is null)
            {
                continue;
            }

            // until holds handles as written; one that no longer resolves cannot be in play, so the rule stays active.
            bool UntilInPlay(string handle) =>
                CampaignHandle.TryParse(handle, out var parsed, out _) && resolver.TryFact(parsed) is { } row && InPlay(scope, row.Id);

            if (!reveal.IsActive(UntilInPlay))
            {
                continue;
            }

            var source = kind + ":" + slug;
            rules.Add(reveal.ToRule(source));
            until[source] = reveal.Until;
        }

        return rules;
    }
}
