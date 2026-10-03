using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using Microsoft.Data.Sqlite;
using S = DndMcp.Domain.Campaign.CampaignValues.KnowledgeStates;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// Gate state for a whole campaign as it stands inside the batch's transaction: every fact, every stored gate (read
/// with <see cref="FactGates.TryParse"/>, so one bad stored gate is skipped rather than failing the write), the party's
/// verdicts, and "in play". What <see cref="RevealChecks"/> and the session checklist evaluate gates with.
///
/// <para>
/// <b>What "the party knows" means here:</b> the party perspective's verdict (<see cref="KnowledgeVerdicts"/>) is
/// Knows and its state is not <c>suspects</c>. A suspected fact is not a known one: a suspected gated fact makes a
/// secret <c>partial</c>, never <c>revealed</c>, and a suspected clue does not complete a route (One Piece §2: a route is
/// complete when the party knows its clues). A soft-deleted fact is known by no one, and neither is an author-only fact
/// outside the author view, whatever its rows say (contract §3.2: author visibility is absolute; the write that gave it a
/// party row was warned): a party row on one never reveals, completes, seeds or partly reveals anything, as the read
/// path's derivation of the same status (<c>ReadGates</c>) has it, so the stored and derived statuses agree.
/// </para>
/// </summary>
internal sealed class GateEvaluation
{
    private readonly WriteBatch _batch;
    private readonly KnowledgeLoader _loader;
    private readonly Dictionary<string, FactRow> _facts;
    private readonly Dictionary<string, KnowledgeVerdict> _party = new(StringComparer.Ordinal);
    private readonly PerspectiveContext _partyContext;
    private IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>>? _entries;
    private IReadOnlyList<EntityRow>? _revealRules;

    public GateEvaluation(WriteBatch batch)
    {
        _batch = batch;
        _loader = new KnowledgeLoader(batch.Connection, batch.Campaign, batch.Transaction);
        _facts = batch.Connection.Query<FactRow>(
                $"SELECT {FactRow.Columns} FROM fact WHERE campaign_id = @campaignId", new { campaignId = batch.Campaign.Id }, batch.Transaction)
            .ToDictionary(f => f.Id, StringComparer.Ordinal);
        Gates = new Dictionary<string, GateSpec>(StringComparer.Ordinal);
        foreach (var fact in _facts.Values.Where(f => !f.IsDeleted && f.Gate is not null).OrderBy(f => f.Seq))
        {
            if (FactGates.TryParse(fact.Gate, out var gate) && gate is not null)
            {
                Gates[fact.Id] = gate;
            }
        }

        _partyContext = PerspectiveContext.For(Perspective.Parse(CampaignValues.PerspectiveKinds.Party), batch.Campaign.Role);
    }

    /// <summary>Every live fact's readable gate (fact ids inside), by fact id, in f:&lt;n&gt; order.</summary>
    public Dictionary<string, GateSpec> Gates { get; }

    public KnowledgeLoader Loader => _loader;

    public FactRow? Fact(string id) => _facts.GetValueOrDefault(id);

    /// <summary>The f:&lt;n&gt; of a fact id (the id when unknown: never expected, since gates store ids of this campaign).</summary>
    public string Ref(string factId) => _facts.TryGetValue(factId, out var fact) ? WriteBatch.Ref(fact) : factId;

    public IReadOnlyList<string> Refs(IEnumerable<string> factIds) => factIds.Select(Ref).ToList();

    /// <summary>In play at the end of <paramref name="asOf"/> (null: now): §3.4.</summary>
    public bool InPlay(string factId, int? asOf) => _facts.TryGetValue(factId, out var fact) && _loader.InPlay(fact, asOf);

    public IReadOnlyList<KnowledgeEntry> Entries(string factId)
    {
        _entries ??= _loader.EntriesForFacts(_facts.Keys);
        return _entries.TryGetValue(factId, out var rows) ? rows : [];
    }

    /// <summary>The party perspective's verdict on a fact now.</summary>
    public KnowledgeVerdict PartyVerdict(string factId)
    {
        if (!_party.TryGetValue(factId, out var verdict))
        {
            var visibility = _facts.TryGetValue(factId, out var fact) ? fact.Visibility : CampaignValues.Visibilities.Author;
            verdict = KnowledgeVerdicts.Evaluate(_partyContext, Entries(factId), visibility, _loader.Attendance);
            _party[factId] = verdict;
        }

        return verdict;
    }

    /// <summary>
    /// The party knows the fact (see the class summary: not merely suspects; a deleted or author-only fact is known by no one).
    /// </summary>
    public bool PartyKnows(string factId) =>
        _facts.TryGetValue(factId, out var fact) && !fact.IsDeleted && fact.Visibility != CampaignValues.Visibilities.Author &&
        PartyVerdict(factId) is { Knows: true, State: not S.Suspects };

    /// <summary>The party suspects the fact (never a deleted or author-only one).</summary>
    public bool PartySuspects(string factId) =>
        _facts.TryGetValue(factId, out var fact) && !fact.IsDeleted && fact.Visibility != CampaignValues.Visibilities.Author &&
        PartyVerdict(factId) is { Knows: true, State: S.Suspects };

    /// <summary>Where a gated fact's gate stands, with in-play judged at <paramref name="asOf"/> (null: now).</summary>
    public GateStatus Status(string gatedFactId, int? asOf) =>
        FactGates.Evaluate(Gates[gatedFactId], f => InPlay(f, asOf), PartyKnows, PartyKnows(gatedFactId));

    /// <summary>
    /// The session a knower learned a fact in, from that knower's own row: its number, <see cref="FactGates.KnownWithoutSession"/>
    /// when it knows with no session recorded, null when it does not know it (for the <c>with</c> check).
    /// </summary>
    public int? LearnedSession(string factId, Knower knower)
    {
        var row = Entries(factId).FirstOrDefault(r => r.KnowerKind == knower.Kind && r.KnowerId == knower.CharacterId && r.ValidUntilSession is null);
        if (row is null || !S.AwareStates.Contains(row.State) || !_facts.TryGetValue(factId, out var fact) || fact.IsDeleted)
        {
            return null;
        }

        return row.LearnedSession ?? FactGates.KnownWithoutSession;
    }

    /// <summary>Every live reveal rule of the campaign (kind rule, subtype reveal_rule), in <c>e:&lt;n&gt;</c> order.</summary>
    public IReadOnlyList<EntityRow> RevealRules() => _revealRules ??= _batch.Connection.Query<EntityRow>(
        $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND kind = @rule AND subtype = @subtype AND deleted_at IS NULL ORDER BY seq",
        new { campaignId = _batch.Campaign.Id, rule = CampaignValues.Kinds.Rule, subtype = CampaignValues.Subtypes.RevealRule }, _batch.Transaction).ToList();

    /// <summary>
    /// The fact a reveal rule's <c>until</c> handle names (handles are stored as written, <c>f:12</c> or a code), or null
    /// when it no longer resolves: such a fact can never be in play, so the rule holds.
    /// </summary>
    public string? UntilFactId(string handle) =>
        CampaignHandle.TryParse(handle, out var parsed, out _) ? _batch.Resolver.TryFact(parsed)?.Id : null;

    /// <summary>The secret entity a gated fact is about (its first live <c>about</c> link to a kind secret), as <c>secret:slug</c>.</summary>
    public string? SecretOf(string factId) =>
        _batch.Connection.QueryFirstOrDefault<string?>(
            "SELECT e.kind || ':' || e.slug FROM fact_link fl JOIN entity e ON e.id = fl.entity_id " +
            "WHERE fl.fact_id = @factId AND fl.role = @about AND e.kind = @secret AND e.deleted_at IS NULL ORDER BY e.seq LIMIT 1",
            new { factId, about = CampaignValues.FactLinkRoles.About, secret = CampaignValues.Kinds.Secret }, _batch.Transaction);
}

/// <summary>
/// The reveal checks and secret-status derivation of contract §3.4, run once per batch when it finishes, for every
/// writer (campaign_write's <c>known_by</c> and campaign_knowledge alike: both write through <see cref="KnowledgeRows"/>,
/// which notes the rows here).
///
/// <para>
/// <b>Warn and apply, never refuse.</b> The table is the source of truth: players deduce secrets early, and a refusal
/// would sink a whole session-recap batch over one line. So an unmet <c>after</c>, an unmet or separately-landed
/// <c>with</c>, too few routes and an unmet <c>prefer</c> (advisory) are warnings on a write that is kept, and each is
/// appended to the batch's change_log reason, so history shows the reveal was made knowingly.
/// </para>
/// <para>
/// <b>Why at the end of the batch:</b> "land together" means in the same batch or session, so the <c>with</c> check
/// needs every fact the batch reveals; "reachable before the gate" compares the routes before the batch's first change
/// (<see cref="EnsureBaseline"/>, a snapshot taken lazily before the first fact or knowledge change) with the routes
/// after its last.
/// </para>
/// <para>
/// <b>Secret status is derived and stored</b> (§3.4): a secret with at least one gated fact (a fact linked to it
/// <c>about</c> that has a gate) gets <c>revealed</c> / <c>partial</c> / <c>seeded</c> / <c>hidden</c> from what the
/// party knows, written as an ordinary logged update in the same batch, so undoing the reveal's batch puts the old status
/// back (and an undo batch re-derives it, through <see cref="HistoryWriter"/>, for when the undone batch was not the last
/// to touch that knowledge). <c>revealed</c> needs every gated fact known; <c>partial</c> comes with a complete route or
/// ANY gated fact known or suspected (§3.4 says "suspects one"; knowing one of several gated facts is more than that, and
/// reading it as hidden would tell the author the table knows nothing). A status set by hand on such a secret is
/// recomputed, with a warning.
/// </para>
/// </summary>
internal sealed class RevealChecks
{
    private readonly WriteBatch _batch;
    private readonly List<KnowledgeChange> _reveals = [];
    private readonly List<(string FactId, int OpIndex)> _unestablished = [];
    private Dictionary<string, GateStatus>? _baseline;

    public RevealChecks(WriteBatch batch) => _batch = batch;

    /// <summary>
    /// Takes the "before" snapshot of every gate's status, once, before the batch's first change to a fact or to
    /// knowledge of a fact. Callers call it before any such change.
    /// </summary>
    public void EnsureBaseline()
    {
        if (_baseline is not null)
        {
            return;
        }

        var evaluation = new GateEvaluation(_batch);
        _baseline = evaluation.Gates.Keys.ToDictionary(id => id, id => evaluation.Status(id, null), StringComparer.Ordinal);
    }

    /// <summary>Notes an aware row a non-author knower got on a fact: the reveal checks run over these.</summary>
    public void Note(KnowledgeChange change) => _reveals.Add(change);

    /// <summary>
    /// Notes a fact the batch created, or set to canon, ruled or accepted, that has no established session: when the batch
    /// is done, it is warned about if a gate or a reveal rule waits for it to be in play (<see cref="Unestablished"/>).
    /// </summary>
    public void NoteUnestablished(string factId, int opIndex)
    {
        if (_unestablished.All(u => u.FactId != factId))
        {
            _unestablished.Add((factId, opIndex));
        }
    }

    /// <summary>Runs the checks and the derivation (see the class summary).</summary>
    public void Finish()
    {
        if (_reveals.Count == 0 && _baseline is null && !_batch.DerivationNeeded && _batch.SecretStatusSetByHand.Count == 0 &&
            _unestablished.Count == 0)
        {
            return;
        }

        var evaluation = new GateEvaluation(_batch);
        RevealWarnings(evaluation);
        ReachableBeforeGate(evaluation);
        DeriveSecretStatuses(evaluation);
        Unestablished(evaluation);
    }

    // Review U14: "in play" needs an established session (§3.4), and only played defaults one (§3.6), so a fact set to
    // canon, ruled or accepted ("that's canon now", the natural word) is never in play: a gate whose after or
    // forbidden_until names it stays closed, its forbidden words stay forbidden, and nothing said so. Judged when the batch
    // is done, so a later op of the same call that gives the session (or the gate) counts. Route clues are not listed: a
    // route is complete when the party knows its clues, whether or not they are in play.
    private void Unestablished(GateEvaluation evaluation)
    {
        foreach (var (factId, opIndex) in _unestablished)
        {
            if (evaluation.Fact(factId) is not { IsDeleted: false, EstablishedSessionId: null } fact ||
                fact.CanonStatus is not (CampaignValues.CanonStatuses.Canon or CampaignValues.CanonStatuses.Ruled or CampaignValues.CanonStatuses.Accepted))
            {
                continue;
            }

            var waiting = new List<string>();
            var gates = false;
            foreach (var (gatedId, gate) in evaluation.Gates)
            {
                var fields = new List<string>();
                if (gate.After?.Contains(factId) == true)
                {
                    fields.Add("after");
                }

                if (gate.Prefer?.Contains(factId) == true)
                {
                    fields.Add("prefer");
                }

                if (gate.ForbiddenUntil?.Contains(factId) == true)
                {
                    fields.Add("forbidden_until");
                }

                if (fields.Count > 0)
                {
                    waiting.Add($"{evaluation.Ref(gatedId)}'s {string.Join(" and ", fields)}");
                    gates = true;
                }
            }

            var rules = false;
            foreach (var rule in evaluation.RevealRules())
            {
                if (RevealRuleData.TryParse(rule.Data, out var data) && data is not null && data.Until.Any(h => evaluation.UntilFactId(h) == factId))
                {
                    waiting.Add($"{WriteBatch.Ref(rule)}'s until");
                    rules = true;
                }
            }

            if (waiting.Count == 0)
            {
                continue;
            }

            var what = gates && rules ? "gates and reveal rules" : gates ? "gates" : "reveal rules";
            _batch.Warnings.Add(new WriteWarning(WarningKinds.NotInPlay, WarningSeverities.Warning,
                $"{fact.SeqHandle} is {fact.CanonStatus} but has no established session, so it is not in play for {what} " +
                $"({string.Join(", ", waiting)}); mark it played or give established_session.", opIndex));
        }
    }

    private void RevealWarnings(GateEvaluation evaluation)
    {
        // One check per fact and knower, with the row as the batch last wrote it (in the order first written).
        var latest = new Dictionary<(string Fact, string Knower), KnowledgeChange>();
        var order = new List<(string Fact, string Knower)>();
        foreach (var change in _reveals)
        {
            var key = (change.Target.FactId!, change.Knower.Text);
            if (!latest.ContainsKey(key))
            {
                order.Add(key);
            }

            latest[key] = change;
        }

        foreach (var change in order.Select(k => latest[k]))
        {
            var factId = change.Target.FactId!;
            if (evaluation.LearnedSession(factId, change.Knower) is null)
            {
                // No longer known by the end of the batch.
                continue;
            }

            // A fact with no gate of its own is still checked for "with": another fact's gate may couple it (symmetric).
            var gate = evaluation.Gates.GetValueOrDefault(factId);
            var knower = change.Knower;
            var session = change.LearnedSession;
            var status = gate is null ? null : evaluation.Status(factId, session);
            var fact = evaluation.Ref(factId);
            var at = session is { } s ? $" in S{WriteBatch.Number(s)}" : string.Empty;
            if (status is not null && status.UnmetAfter.Count > 0)
            {
                var unmet = evaluation.Refs(status.UnmetAfter);
                Add(new GateWarning(
                    $"{fact} reached {knower.Text}{at} before its gate's after is met: {string.Join(", ", unmet)} not in play yet (applied anyway).",
                    change.OpIndex, GateConditions.After, fact, knower.Text, unmet, [], session, null, null, [], gate!.Note));
            }

            var partners = FactGates.WithPartners(factId, gate, evaluation.Gates.Select(p => (p.Key, (GateSpec?)p.Value)))
                .Where(p => evaluation.Fact(p) is { IsDeleted: false })
                .ToList();
            if (partners.Count > 0)
            {
                var revealedHere = _reveals.Where(r => r.Knower == knower && r.Aware).Select(r => r.Target.FactId!).ToHashSet(StringComparer.Ordinal);
                var with = FactGates.CheckWith(partners, f => evaluation.LearnedSession(f, knower), session, revealedHere);
                if (!with.Met)
                {
                    var unmet = evaluation.Refs(with.Unmet);
                    var separately = with.LandedSeparately.Select(l => new LandedSeparatelyItem(evaluation.Ref(l.FactId), l.LearnedSession)).ToList();
                    var parts = new List<string>();
                    if (unmet.Count > 0)
                    {
                        parts.Add($"{string.Join(", ", unmet)} {(unmet.Count == 1 ? "has" : "have")} not reached {knower.Text}");
                    }

                    if (separately.Count > 0)
                    {
                        parts.Add("landed separately: " + string.Join(", ", separately.Select(l =>
                            $"{l.Fact} {(l.Session is { } n ? "in S" + WriteBatch.Number(n) : "with no session recorded")}")) +
                            $", {fact} {(session is { } r ? "in S" + WriteBatch.Number(r) : "in this batch")}");
                    }

                    Add(new GateWarning(
                        $"{fact} must land with {string.Join(", ", evaluation.Refs(partners))} (same knower, same session): {string.Join("; ", parts)} (applied anyway).",
                        change.OpIndex, GateConditions.With, fact, knower.Text, unmet, separately, session, null, null, [], gate?.Note));
                }
            }

            if (status is null)
            {
                continue;
            }

            if (status.Routes.Count > 0 && !status.RoutesMet)
            {
                var ids = status.CompleteRouteIds;
                Add(new GateWarning(
                    $"{fact} reached {knower.Text}{at} with {WriteBatch.Number(status.RoutesComplete)} of {WriteBatch.Number(status.MinRoutes)} needed routes complete" +
                    $"{(ids.Count == 0 ? string.Empty : " (" + string.Join(", ", ids) + ")")} (applied anyway).",
                    change.OpIndex, GateConditions.Routes, fact, knower.Text, [], [], session, status.RoutesComplete, status.MinRoutes, ids, gate!.Note));
            }

            if (status.UnmetPrefer.Count > 0)
            {
                var unmet = evaluation.Refs(status.UnmetPrefer);
                Add(new GateWarning(
                    $"Advisory: {fact}'s gate would rather {string.Join(", ", unmet)} were in play first (a \"probably\", not a condition).",
                    change.OpIndex, GateConditions.Prefer, fact, knower.Text, unmet, [], session, null, null, [], gate!.Note));
            }
        }
    }

    private void ReachableBeforeGate(GateEvaluation evaluation)
    {
        if (_baseline is null)
        {
            return;
        }

        foreach (var factId in evaluation.Gates.Keys)
        {
            if (!_baseline.TryGetValue(factId, out var before))
            {
                continue;
            }

            var after = evaluation.Status(factId, null);
            if (!FactGates.BecameReachableBeforeGate(before, after))
            {
                continue;
            }

            var fact = evaluation.Ref(factId);
            var secret = evaluation.SecretOf(factId);
            var unmet = evaluation.Refs(after.UnmetAfter);
            var opIndex = _batch.Knowledge.Changes.LastOrDefault()?.OpIndex;
            Add(new ReachableBeforeGateWarning(
                $"{secret ?? fact} is reachable before the gate: {WriteBatch.Number(after.RoutesComplete)} of {WriteBatch.Number(after.MinRoutes)} needed routes " +
                $"to {fact} are complete while its after is unmet ({string.Join(", ", unmet)}); the party can work it out before the story is ready for it.",
                opIndex, secret, fact, after.RoutesComplete, after.MinRoutes, unmet));
        }
    }

    private void DeriveSecretStatuses(GateEvaluation evaluation)
    {
        if (!_batch.DerivationNeeded && _batch.SecretStatusSetByHand.Count == 0)
        {
            return;
        }

        var secrets = _batch.Connection.Query<EntityRow>(
            $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND kind = @secret AND deleted_at IS NULL ORDER BY seq",
            new { campaignId = _batch.Campaign.Id, secret = CampaignValues.Kinds.Secret }, _batch.Transaction).ToList();
        if (secrets.Count == 0)
        {
            return;
        }

        var links = _batch.Connection.Query<FactLinkRow>(
                $"SELECT {CampaignRows.Prefixed(FactLinkRow.Columns, "fl")} FROM fact_link fl JOIN entity e ON e.id = fl.entity_id " +
                "WHERE e.campaign_id = @campaignId AND e.kind = @secret",
                new { campaignId = _batch.Campaign.Id, secret = CampaignValues.Kinds.Secret }, _batch.Transaction)
            .ToLookup(l => l.EntityId, StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            var gated = links[secret.Id]
                .Where(l => l.Role == CampaignValues.FactLinkRoles.About && evaluation.Gates.ContainsKey(l.FactId))
                .Select(l => l.FactId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (gated.Count == 0)
            {
                continue;
            }

            var clues = links[secret.Id].Where(l => l.Role == CampaignValues.FactLinkRoles.ClueFor).Select(l => l.FactId);
            var statuses = gated.Select(g => evaluation.Status(g, null)).ToList();
            // partial: a complete route, or the party holds part of the secret. §3.4 names "suspects one"; knowing one
            // gated fact of several is more than suspecting it, and must not read as hidden.
            var derived = SecretStatuses.Derive(
                gated.All(evaluation.PartyKnows),
                statuses.Any(s => s.Routes.Any(r => r.Complete)) || gated.Any(f => evaluation.PartyKnows(f) || evaluation.PartySuspects(f)),
                statuses.Any(s => s.Seeded) || clues.Any(evaluation.PartyKnows));
            var secretRef = WriteBatch.Ref(secret);
            if (_batch.SecretStatusSetByHand.TryGetValue(secret.Id, out var setTo))
            {
                _batch.Warnings.Add(new WriteWarning(WarningKinds.DerivedStatus, WarningSeverities.Warning,
                    $"{secretRef}'s status is derived from what the party knows of its gated facts ({string.Join(", ", evaluation.Refs(gated))}): " +
                    $"it was set to {setTo} and is recomputed as {derived}."));
            }

            if (secret.Status != derived)
            {
                _batch.Update("entity", secret.Id, new Dictionary<string, object?> { ["status"] = derived }, _batch.DerivationAction);
                _batch.Consequences.Add(new Consequence(ConsequenceKinds.SecretStatus,
                    $"{secretRef}: {secret.Status ?? "(none)"} → {derived}.", secretRef, []));
            }
        }
    }

    private void Add(WriteWarning warning)
    {
        _batch.Warnings.Add(warning);
        _batch.Recorder.AppendReason("warning: " + warning.Message);
    }
}

/// <summary>
/// Stored gates hold fact ids (so a code change can never re-point one); tools print fact handles. This maps a stored
/// gate back to handles for any reader (Q, the host), the inverse of what the write path stores with
/// <see cref="FactGates.Rewrite"/>.
/// </summary>
public static class CampaignGates
{
    /// <summary>
    /// A stored gate with every fact id replaced by its <c>f:&lt;n&gt;</c> handle; null for no gate or one that cannot be
    /// read (<see cref="FactGates.TryParse"/>: a read never fails on a bad stored gate).
    /// </summary>
    public static GateSpec? ToHandles(SqliteConnection connection, string campaignId, string? storedGate, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!FactGates.TryParse(storedGate, out var gate) || gate is null)
        {
            return null;
        }

        CampaignDatabase.EnsureDapperConfigured();
        var seqs = connection.Query<(string Id, long Seq)>(
                "SELECT id, seq FROM fact WHERE campaign_id = @campaignId", new { campaignId }, transaction)
            .ToDictionary(f => f.Id, f => "f:" + f.Seq.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        return FactGates.Rewrite(gate, id => seqs.GetValueOrDefault(id, id));
    }
}
