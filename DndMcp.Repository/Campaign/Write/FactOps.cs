using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using CS = DndMcp.Domain.Campaign.CampaignValues.CanonStatuses;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// The fact op of campaign_write (contract §6): create a fact (statement) or update one (ref), with its links, gate,
/// knowers, dependencies and supersession.
///
/// <para>
/// <b>Gates are stored with fact ids and printed with handles.</b> The op names facts by <c>f:12</c> or a code; each is
/// resolved in this campaign (possibly a fact an earlier op of the call created; never the fact itself) and the gate is
/// stored through <see cref="FactGates.Rewrite"/> with ids, so renumbering or a code change can never re-point it and
/// undo's conflict scan sees the ids. <c>"gate": {}</c> removes the gate (stores NULL).
/// </para>
/// <para>
/// <b>Supersession flags, never fixes.</b> When a fact becomes superseded (<c>superseded_by</c>, <c>supersedes</c>, or
/// canon_status superseded), the result lists every fact that rests on it (<see cref="Supersession.Dependents"/>, with
/// depth and via) and the entities linked to those, and changes none of them: "the fragments have run down for 400 years"
/// is suspect after the timeline correction, not false, and only the author can say which. Re-pointing a fact that is
/// already superseded by another fact is applied with a warning (the earlier replacement silently stops being one), and
/// a fact whose canon_status leaves superseded loses its superseded_by (a canon fact naming a replacement would read as
/// superseded to anything that follows the link).
/// </para>
/// <para>
/// <b>Register codes</b> (§3.1): an explicit code is refused when another entity or fact holds it; <c>auto_code</c>
/// takes the next of a letter; a proposed fact with none gets the next F. Codes never change once assigned.
/// </para>
/// </summary>
internal static class FactOps
{
    public static void Fact(WriteBatch b, OpScope o)
    {
        var s = o.Spec;
        b.Reveals.EnsureBaseline();
        b.DerivationNeeded = true;
        var existing = s.Ref is null ? null : b.RequireFact(OpScope.Subject, o.Where, "ref", s.Ref);
        if (existing is not null)
        {
            b.PlayerText.Fact(existing.Id, o.Index);
        }

        var supersededBy = s.SupersededBy is null ? null : b.RequireFact(OpScope.Subject, o.Where, "superseded_by", s.SupersededBy);
        if (supersededBy is not null && existing is not null && supersededBy.Id == existing.Id)
        {
            throw o.Fail($"a fact cannot supersede itself ({existing.SeqHandle}).");
        }

        var canonGiven = Canonical.OrNull(CS.Set, s.CanonStatus);
        var canon = supersededBy is not null ? CS.Superseded : canonGiven ?? existing?.CanonStatus ?? CS.Canon;
        var code = b.AssignCode(OpScope.Subject, o.Where, s.Code, s.AutoCode, canon, existing?.Code, existing?.SeqHandle);
        string? established;
        if (s.EstablishedSession is { } number)
        {
            established = b.RequireSession(OpScope.Subject, o.Where, "established_session", number);
        }
        else
        {
            // A fact written as played happened in the batch's session unless it says otherwise (§3.6).
            established = canonGiven == CS.Played && existing?.EstablishedSessionId is null ? b.SessionId : existing?.EstablishedSessionId;
        }

        var gateGiven = s.Gate is not null;
        var gate = gateGiven ? GateJson(b, o, s.Gate!, existing?.Id) : existing?.Gate;
        FactRow fact;
        List<string> fields;
        string outcome;
        if (existing is null)
        {
            var row = b.Recorder.Insert("fact", new Dictionary<string, object?>
            {
                ["campaign_id"] = b.Campaign.Id,
                ["code"] = code,
                ["statement"] = s.Statement!.Trim(),
                ["fact_type"] = Canonical.OrNull(CV.FactTypes.Set, s.FactType) ?? CV.FactTypes.Canon,
                ["truth"] = Canonical.OrNull(CV.Truths.Set, s.Truth) ?? CV.Truths.True,
                ["canon_status"] = canon,
                ["confidence"] = Canonical.OrNull(CV.Confidences.Set, s.Confidence) ?? CV.Confidences.Confirmed,
                ["visibility"] = Canonical.OrNull(CV.Visibilities.Set, s.Visibility) ?? CV.Visibilities.Restricted,
                ["gate"] = gate,
                ["established_session_id"] = established,
                ["source"] = s.Source?.Trim(),
                ["superseded_by"] = supersededBy?.Id,
            }, o.Op);
            fact = b.FactById((string)row["id"]!)!;
            b.PlayerText.FactCreated(fact.Id, o.Index);
            fields = CampaignOpFields.All
                .Where(f => f.IsGiven(s) && f.Name is not ("op" or "known_by" or "about" or "links" or "depends_on" or "supersedes"))
                .Select(f => f.Name).ToList();
            outcome = WriteOutcomes.Created;
        }
        else
        {
            var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (s.Statement is not null)
            {
                changes["statement"] = s.Statement.Trim();
            }

            if (s.FactType is not null)
            {
                changes["fact_type"] = Canonical.Of(CV.FactTypes.Set, s.FactType);
            }

            if (s.Truth is not null)
            {
                changes["truth"] = Canonical.Of(CV.Truths.Set, s.Truth);
            }

            changes["canon_status"] = canon;
            if (s.Confidence is not null)
            {
                changes["confidence"] = Canonical.Of(CV.Confidences.Set, s.Confidence);
            }

            if (s.Visibility is not null)
            {
                changes["visibility"] = Canonical.Of(CV.Visibilities.Set, s.Visibility);
            }

            if (s.Source is not null)
            {
                changes["source"] = s.Source.Trim();
            }

            changes["code"] = code;
            changes["established_session_id"] = established;
            if (gateGiven)
            {
                changes["gate"] = gate;
            }

            if (supersededBy is not null)
            {
                changes["superseded_by"] = supersededBy.Id;
            }
            else if (canon != CS.Superseded && existing.SupersededBy is not null)
            {
                // Back in play, a fact is superseded by nothing: one still naming a replacement would read as superseded
                // to anything that follows superseded_by.
                changes["superseded_by"] = null;
            }

            fields = b.Update("fact", existing.Id, changes, o.Op).ToList();
            fact = b.FactById(existing.Id)!;
            outcome = WriteOutcomes.Updated;
        }

        fields.AddRange(Links(b, o, fact));
        fields.AddRange(DependsOn(b, o, fact));
        if (existing is not null && existing.CanonStatus != CS.Superseded && fact.CanonStatus == CS.Superseded)
        {
            b.Warnings.Add(SupersessionWarning(b, o.Index, fact, supersededBy));
        }

        if (supersededBy is not null && existing?.SupersededBy is { } priorBy && priorBy != supersededBy.Id)
        {
            b.Warnings.Add(Repointed(b, o.Index, fact, priorBy, supersededBy));
        }

        if (s.Supersedes is not null)
        {
            var old = b.RequireFact(OpScope.Subject, o.Where, "supersedes", s.Supersedes);
            if (old.Id == fact.Id)
            {
                throw o.Fail($"a fact cannot supersede itself ({fact.SeqHandle}).");
            }

            if (old.SupersededBy is { } prior && prior != fact.Id)
            {
                b.Warnings.Add(Repointed(b, o.Index, old, prior, fact));
            }

            var changed = b.Update("fact", old.Id, new Dictionary<string, object?>
            {
                ["canon_status"] = CS.Superseded,
                ["superseded_by"] = fact.Id,
            }, o.Op);
            if (changed.Count > 0)
            {
                fields.Add("supersedes");
            }

            if (old.CanonStatus != CS.Superseded)
            {
                b.Warnings.Add(SupersessionWarning(b, o.Index, b.FactById(old.Id)!, fact));
            }
        }

        if (canonGiven == CS.Played && fact.EstablishedSessionId is null)
        {
            b.Warnings.Add(new WriteWarning(WarningKinds.NotInPlay, WarningSeverities.Warning,
                $"{fact.SeqHandle} is played but has no established session, so it is not in play for gates and reveal rules; " +
                "give established_session (or write it during a live session).", o.Index));
        }
        else if ((canonGiven is not null || existing is null) && fact.EstablishedSessionId is null &&
                 fact.CanonStatus is CS.Canon or CS.Ruled or CS.Accepted)
        {
            // Only played defaults its established session (§3.6), so "that's canon now" leaves the fact out of play for
            // good; whether a gate is waiting for it is known only once every op of the batch has run (RevealChecks).
            b.Reveals.NoteUnestablished(fact.Id, o.Index);
        }

        var target = KnowledgeTarget.Of(fact);
        for (var i = 0; i < (s.KnownBy?.Count ?? 0); i++)
        {
            var (knowledgeOutcome, _, knower) = b.Knowledge.Write(OpScope.Subject, $"{o.Where}: known_by item {WriteBatch.Number(i + 1)}", target, s.KnownBy![i]!, o.Op, o.Index);
            if (knowledgeOutcome != WriteOutcomes.Unchanged)
            {
                fields.Add("known_by " + knower.Text);
            }
        }

        if (outcome == WriteOutcomes.Updated && fields.Count == 0)
        {
            outcome = WriteOutcomes.Unchanged;
        }

        var printedGate = gateGiven && fact.Gate is not null ? CampaignGates.ToHandles(b.Connection, b.Campaign.Id, fact.Gate, b.Transaction) : null;
        b.Applied.Add(new AppliedOp(o.Index, o.Op, WriteBatch.Ref(fact), outcome, fields.Distinct().ToList(), fact.Code, Gate: printedGate));
    }

    /// <summary>
    /// The gate as stored: each fact handle resolved to a live fact of this campaign (never <paramref name="selfId"/>) and
    /// replaced by its id; null for an empty gate (<c>{}</c> removes it).
    /// </summary>
    private static string? GateJson(WriteBatch b, OpScope o, GateSpec gate, string? selfId)
    {
        if (FactGates.IsEmpty(gate))
        {
            return null;
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in FactGates.References(gate))
        {
            var fact = b.RequireFact(OpScope.Subject, o.Where, "gate", handle);
            if (fact.Id == selfId)
            {
                throw o.Fail($"gate names {fact.SeqHandle}, which is this fact; a gate lists other facts.");
            }

            ids[handle] = fact.Id;
        }

        var stored = FactGates.Rewrite(gate, handle => ids[handle]);
        var both = (stored.After ?? []).Intersect(stored.With ?? [], StringComparer.Ordinal).FirstOrDefault();
        if (both is not null)
        {
            throw o.Fail($"gate: {b.FactRefById(both)} is in both after and with (under two handles); a fact that must land with this one cannot also have to come before it.");
        }

        return FactGates.Serialize(stored);
    }

    /// <summary>Adds the <c>about</c> and <c>links</c> fact_link rows that are not there yet (links are only added here).</summary>
    private static List<string> Links(WriteBatch b, OpScope o, FactRow fact)
    {
        var s = o.Spec;
        var wanted = new List<(string Handle, string Role, string Field)>();
        for (var i = 0; i < (s.About?.Count ?? 0); i++)
        {
            wanted.Add((s.About![i], CV.FactLinkRoles.About, $"about item {WriteBatch.Number(i + 1)}"));
        }

        for (var i = 0; i < (s.Links?.Count ?? 0); i++)
        {
            var link = s.Links![i]!;
            wanted.Add((link.Ref!, Canonical.OrNull(CV.FactLinkRoles.Set, link.Role) ?? CV.FactLinkRoles.About, $"links item {WriteBatch.Number(i + 1)} ref"));
        }

        var changed = new List<string>();
        foreach (var (handle, role, field) in wanted)
        {
            var entity = b.RequireEntity(OpScope.Subject, o.Where, field, handle);
            var row = new Dictionary<string, object?> { ["fact_id"] = fact.Id, ["entity_id"] = entity.Id, ["role"] = role };
            if (b.Recorder.FindKey("fact_link", row) is null)
            {
                b.Recorder.Insert("fact_link", row, o.Op);
                changed.Add(role == CV.FactLinkRoles.About && field.StartsWith("about", StringComparison.Ordinal) ? "about" : "links");
            }
        }

        return changed;
    }

    /// <summary>Adds depends_on edges, refusing one that would close a cycle ("what rests on this" must terminate).</summary>
    private static List<string> DependsOn(WriteBatch b, OpScope o, FactRow fact)
    {
        var changed = new List<string>();
        var list = o.Spec.DependsOn;
        if (list is null)
        {
            return changed;
        }

        var edges = Edges(b);
        for (var i = 0; i < list.Count; i++)
        {
            var field = $"depends_on item {WriteBatch.Number(i + 1)}";
            var dependency = b.RequireFact(OpScope.Subject, o.Where, field, list[i]);
            if (dependency.Id == fact.Id)
            {
                throw o.Fail($"{field}: a fact cannot depend on itself ({fact.SeqHandle}).");
            }

            if (Supersession.WouldCycle(fact.Id, dependency.Id, edges))
            {
                throw o.Fail($"{field}: {dependency.SeqHandle} already depends on {fact.SeqHandle}; depending on it would close a cycle.");
            }

            var row = new Dictionary<string, object?> { ["fact_id"] = fact.Id, ["depends_on"] = dependency.Id };
            if (b.Recorder.FindKey("fact_dependency", row) is null)
            {
                b.Recorder.Insert("fact_dependency", row, o.Op);
                edges.Add((fact.Id, dependency.Id));
                changed.Add("depends_on");
            }
        }

        return changed;
    }

    private static List<(string FactId, string DependsOn)> Edges(WriteBatch b) =>
        b.Connection.Query<(string FactId, string DependsOn)>(
            // Ordered by the dependent's f:<n>, so dependents at one depth list in the order they were written (ids are
            // only time-ordered to the millisecond).
            "SELECT d.fact_id, d.depends_on FROM fact_dependency d JOIN fact f ON f.id = d.fact_id JOIN fact o ON o.id = d.depends_on " +
            "WHERE f.campaign_id = @campaignId ORDER BY f.seq, o.seq",
            new { campaignId = b.Campaign.Id }, b.Transaction).ToList();

    /// <summary>
    /// A fact already superseded by one fact is now superseded by another. Applied (the author may be correcting which
    /// fact replaces it), but said, because the earlier replacement silently stops being one.
    /// </summary>
    private static WriteWarning Repointed(WriteBatch b, int opIndex, FactRow superseded, string priorId, FactRow now) =>
        new(WarningKinds.SupersessionRepointed, WarningSeverities.Warning,
            $"{superseded.SeqHandle} was superseded by {b.FactRefById(priorId)}; it is now superseded by {now.SeqHandle} instead. " +
            $"Check that {b.FactRefById(priorId)} still says what it should.", opIndex);

    /// <summary>The dependents of a newly superseded fact and the entities linked to them (§6; One Piece row 29).</summary>
    private static SupersessionWarning SupersessionWarning(WriteBatch b, int opIndex, FactRow superseded, FactRow? by)
    {
        var dependents = Supersession.Dependents(superseded.Id, Edges(b))
            .Where(d => b.FactById(d.FactId) is { IsDeleted: false })
            .ToList();
        var items = dependents.Select(d => new DependentItem(b.FactRefById(d.FactId), d.Depth, b.FactRefById(d.Via))).ToList();
        var entities = new List<string>();
        foreach (var dependent in dependents)
        {
            foreach (var entity in b.Connection.Query<EntityRow>(
                         $"SELECT {CampaignRows.Prefixed(EntityRow.Columns, "e")} FROM fact_link fl JOIN entity e ON e.id = fl.entity_id " +
                         "WHERE fl.fact_id = @factId AND e.deleted_at IS NULL ORDER BY e.seq",
                         new { factId = dependent.FactId }, b.Transaction))
            {
                if (!entities.Contains(entity.Handle))
                {
                    entities.Add(entity.Handle);
                }
            }
        }

        var byText = by is null ? string.Empty : $" by {by.SeqHandle}";
        var message = dependents.Count == 0
            ? $"{superseded.SeqHandle} is superseded{byText}; no fact depends on it."
            : $"{superseded.SeqHandle} is superseded{byText}. {WriteBatch.Number(dependents.Count)} fact{(dependents.Count == 1 ? " rests" : "s rest")} on it, left unchanged for you to re-check: " +
              string.Join(", ", items.Select(i => i.Depth == 1 ? i.Fact : $"{i.Fact} (depth {WriteBatch.Number(i.Depth)}, via {i.Via})")) +
              (entities.Count == 0 ? "." : $"; entities to re-check: {string.Join(", ", entities)}.");
        return new SupersessionWarning(message, opIndex, superseded.SeqHandle, by?.SeqHandle, items, entities);
    }
}
