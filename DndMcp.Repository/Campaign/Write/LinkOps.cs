using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// link and unlink (contract §6). A link is stored as one of three things, decided by its handles:
/// <list type="bullet">
/// <item><c>same_as</c> to another campaign's entity (<c>one-piece/character:keras</c>): a <c>cross_link</c>, one row per
/// pair stored with <c>a_id &lt; b_id</c> (the schema CHECKs it), never shown to a non-author view (the Belmakor
/// firewall).</item>
/// <item><c>leads_to</c> between two beats: a <c>beat_edge</c> (all_of / any_of), which reachability reads.</item>
/// <item>anything else: a <c>relation</c>, one per (from, rel, to): linking again updates it rather than adding a twin.</item>
/// </list>
/// The rel is snake_case (<see cref="CampaignOpValidation.NormalizeRel"/>), and since/until are session numbers stored as
/// the sessions' ids. An unlink of a link that is not there is a warning, not a refusal: the state the caller wanted is
/// the state there is.
/// <para>
/// <b>Joining the party during a session</b> (review C04): a new current <c>member_of</c> link from a character to the
/// campaign's party, written with no <c>since</c> (and no <c>until</c>) while the batch has a session context, gets that
/// session as since, and the result says so. A membership with no since reads as an original member's, so a PC added in session 5 "knew" what
/// the party learned in sessions 1 to 4 wherever attendance was not recorded, and the knowledge check let drafts in his
/// voice use it. Outside any session since stays empty: that is setting up the party, whose members were there from the
/// start.
/// </para>
/// </summary>
internal static class LinkOps
{
    public static void Link(WriteBatch b, OpScope o)
    {
        var s = o.Spec;
        var from = b.RequireEntity(OpScope.Subject, o.Where, "from", s.From!);
        var rel = CampaignOpValidation.NormalizeRel(s.Rel)!;
        CampaignHandle.TryParse(s.To, out var toHandle, out _);
        if (rel == CV.Rels.SameAs && toHandle is CampaignHandle.CrossCampaign cross)
        {
            CrossLink(b, o, from, cross);
            return;
        }

        var to = b.RequireEntity(OpScope.Subject, o.Where, "to", s.To!);
        o.Check(new ResolvedKinds(From: from.Kind, To: to.Kind));
        if (to.Id == from.Id)
        {
            throw o.Fail("from and to are the same entity; a relation needs two.");
        }

        if (rel == CV.Rels.LeadsTo && from.Kind == CV.Kinds.Beat && to.Kind == CV.Kinds.Beat)
        {
            BeatEdge(b, o, from, to);
            return;
        }

        Relation(b, o, from, rel, to);
    }

    public static void Unlink(WriteBatch b, OpScope o)
    {
        var s = o.Spec;
        var from = b.RequireEntity(OpScope.Subject, o.Where, "from", s.From!);
        var rel = CampaignOpValidation.NormalizeRel(s.Rel)!;
        CampaignHandle.TryParse(s.To, out var toHandle, out _);
        string reference;
        bool removed;
        if (rel == CV.Rels.SameAs && toHandle is CampaignHandle.CrossCampaign cross)
        {
            var other = OtherCampaignEntity(b, o, cross);
            reference = $"{from.Handle} same_as {cross.CampaignSlug}/{other.Handle}";
            var (a, z) = Ordered(from.Id, other.Id);
            removed = b.Recorder.Delete("cross_link", [a, z], o.Op);
        }
        else
        {
            var to = b.RequireEntity(OpScope.Subject, o.Where, "to", s.To!);
            reference = $"{from.Handle} {rel} {to.Handle}";
            removed = false;
            if (rel == CV.Rels.LeadsTo && from.Kind == CV.Kinds.Beat && to.Kind == CV.Kinds.Beat &&
                b.Recorder.FindKey("beat_edge", new Dictionary<string, object?> { ["from_beat_id"] = from.Id, ["to_beat_id"] = to.Id }) is { } edge)
            {
                removed = b.Recorder.Delete("beat_edge", edge, o.Op);
            }

            if (!removed && b.Recorder.FindKey("relation", RelationMatch(from.Id, rel, to.Id)) is { } forward)
            {
                removed = b.Recorder.Delete("relation", forward, o.Op);
            }

            // A symmetric relation holds both ways, so "unlink B ally_of A" removes the stored "A ally_of B".
            if (!removed && b.Recorder.FindKey("relation", RelationMatch(to.Id, rel, from.Id)) is { } backward &&
                b.Recorder.Read("relation", backward) is { } row && row["symmetric"] is 1L)
            {
                removed = b.Recorder.Delete("relation", backward, o.Op);
            }
        }

        if (!removed)
        {
            b.Warnings.Add(new WriteWarning(WarningKinds.Nothing, WarningSeverities.Warning,
                $"there is no {reference} link; nothing to unlink.", o.Index));
        }

        b.Applied.Add(new AppliedOp(o.Index, o.Op, reference, removed ? WriteOutcomes.Unlinked : WriteOutcomes.Unchanged, removed ? [rel] : []));
    }

    /// <summary>
    /// Makes sure the relation (from, rel, to) exists (created with the campaign's default visibility when missing), for
    /// ops that imply one (answer's answered_by). Returns true when it was created.
    /// </summary>
    public static bool EnsureRelation(WriteBatch b, string action, EntityRow from, string rel, EntityRow to)
    {
        if (b.Recorder.FindKey("relation", RelationMatch(from.Id, rel, to.Id)) is not null)
        {
            return false;
        }

        b.Recorder.Insert("relation", new Dictionary<string, object?>(RelationMatch(from.Id, rel, to.Id))
        {
            ["campaign_id"] = b.Campaign.Id,
            ["visibility"] = b.DefaultRelationVisibility(),
        }, action);
        return true;
    }

    private static void Relation(WriteBatch b, OpScope o, EntityRow from, string rel, EntityRow to)
    {
        var s = o.Spec;
        var reference = $"{from.Handle} {rel} {to.Handle}";
        var since = s.Since is { } sinceNumber ? b.RequireSession(OpScope.Subject, o.Where, "since", sinceNumber) : null;
        var until = s.Until is { } untilNumber ? b.RequireSession(OpScope.Subject, o.Where, "until", untilNumber) : null;
        var status = Canonical.OrNull(CV.RelationStatuses.Set, s.Status) ?? CV.RelationStatuses.Current;
        var match = RelationMatch(from.Id, rel, to.Id);
        var key = b.Recorder.FindKey("relation", match);

        // A symmetric relation holds both ways: "link B ally_of A" updates a stored symmetric "A ally_of B" rather than
        // adding its mirror image (which unlink would then have to find twice).
        if (key is null && b.Recorder.FindKey("relation", RelationMatch(to.Id, rel, from.Id)) is { } mirror &&
            b.Recorder.Read("relation", mirror) is { } stored && stored["symmetric"] is 1L)
        {
            key = mirror;
            reference = $"{to.Handle} {rel} {from.Handle}";
        }

        if (key is null)
        {
            // An until given with no since records a stay that is over, not someone joining tonight: since stays as given.
            var joinsNow = since is null && until is null && rel == CV.Rels.MemberOf && to.Id == b.Campaign.PartyId &&
                           from.Kind == CV.Kinds.Character && status == CV.RelationStatuses.Current && b.SessionId is not null &&
                           b.SessionNumber is not null;
            var row = new Dictionary<string, object?>(match)
            {
                ["campaign_id"] = b.Campaign.Id,
                ["label"] = s.Label?.Trim(),
                ["attitude"] = s.Attitude,
                ["symmetric"] = s.Symmetric ?? false,
                ["visibility"] = s.Visibility is null ? b.DefaultRelationVisibility() : Canonical.Of(CV.Visibilities.RowSet, s.Visibility),
                ["status"] = status,
                ["since_session_id"] = joinsNow ? b.SessionId : since,
                ["until_session_id"] = until,
                ["data"] = s.Data is null ? new System.Text.Json.Nodes.JsonObject() : WriteBatch.MergeData(OpScope.Subject, o.Where, "data", null, s.Data),
            };
            b.Recorder.Insert("relation", row, o.Op);
            var given = CampaignOpFields.All.Where(f => f.IsGiven(s) && f.Name is not ("op" or "from" or "rel" or "to")).Select(f => f.Name).ToList();
            if (joinsNow)
            {
                given.Add("since");
                b.Warnings.Add(new WriteWarning(WarningKinds.MemberSince, WarningSeverities.Advisory,
                    $"{from.Handle} joins {to.Handle} as of S{WriteBatch.Number(b.SessionNumber!.Value)}, the session of this write; pass since to change it.",
                    o.Index));
            }

            b.Applied.Add(new AppliedOp(o.Index, o.Op, reference, WriteOutcomes.Linked, given));
            return;
        }

        var current = b.Recorder.Read("relation", key)!;
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (s.Label is not null)
        {
            changes["label"] = s.Label.Trim();
        }

        if (s.Attitude is not null)
        {
            changes["attitude"] = s.Attitude;
        }

        if (s.Symmetric is not null)
        {
            changes["symmetric"] = s.Symmetric;
        }

        if (s.Visibility is not null)
        {
            changes["visibility"] = Canonical.Of(CV.Visibilities.RowSet, s.Visibility);
        }

        if (s.Status is not null)
        {
            changes["status"] = Canonical.Of(CV.RelationStatuses.Set, s.Status);
        }

        if (since is not null)
        {
            changes["since_session_id"] = since;
        }

        if (until is not null)
        {
            changes["until_session_id"] = until;
        }

        if (s.Data is not null)
        {
            changes["data"] = WriteBatch.MergeData(OpScope.Subject, o.Where, "data", current["data"] as string, s.Data);
        }

        var changed = b.Update("relation", key, changes, o.Op);
        b.Applied.Add(new AppliedOp(o.Index, o.Op, reference, changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated, changed));
    }

    private static void BeatEdge(WriteBatch b, OpScope o, EntityRow from, EntityRow to)
    {
        var mode = Canonical.Of(CV.BeatEdgeModes.Set, o.Spec.Mode!);
        var reference = $"{from.Handle} leads_to {to.Handle}";
        var match = new Dictionary<string, object?> { ["from_beat_id"] = from.Id, ["to_beat_id"] = to.Id };
        var key = b.Recorder.FindKey("beat_edge", match);
        if (key is null)
        {
            b.Recorder.Insert("beat_edge", new Dictionary<string, object?>(match) { ["campaign_id"] = b.Campaign.Id, ["mode"] = mode }, o.Op);
            b.Applied.Add(new AppliedOp(o.Index, o.Op, reference, WriteOutcomes.Linked, ["mode"]));
            return;
        }

        var changed = b.Update("beat_edge", key, new Dictionary<string, object?> { ["mode"] = mode }, o.Op);
        b.Applied.Add(new AppliedOp(o.Index, o.Op, reference, changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated, changed));
    }

    private static void CrossLink(WriteBatch b, OpScope o, EntityRow from, CampaignHandle.CrossCampaign cross)
    {
        var other = OtherCampaignEntity(b, o, cross);
        o.Check(new ResolvedKinds(From: from.Kind, To: other.Kind));
        var reference = $"{from.Handle} same_as {cross.CampaignSlug}/{other.Handle}";
        var (a, z) = Ordered(from.Id, other.Id);
        var match = new Dictionary<string, object?> { ["a_id"] = a, ["b_id"] = z };
        var key = b.Recorder.FindKey("cross_link", match);
        var note = o.Spec.Note?.Trim();
        if (key is null)
        {
            b.Recorder.Insert("cross_link", new Dictionary<string, object?>(match) { ["note"] = string.IsNullOrEmpty(note) ? null : note }, o.Op);
            b.Applied.Add(new AppliedOp(o.Index, o.Op, reference, WriteOutcomes.Linked, note is null ? [] : ["note"]));
            return;
        }

        var changed = note is null ? [] : b.Update("cross_link", key, new Dictionary<string, object?> { ["note"] = note }, o.Op);
        b.Applied.Add(new AppliedOp(o.Index, o.Op, reference, changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated, changed));
    }

    /// <summary>The live entity another campaign's handle names; this campaign's own slug is refused (that link is a relation).</summary>
    private static EntityRow OtherCampaignEntity(WriteBatch b, OpScope o, CampaignHandle.CrossCampaign cross)
    {
        var campaign = HandleResolver.TryCampaign(b.Connection, cross.CampaignSlug, b.Transaction) ??
                       throw o.Fail($"to: no campaign {cross.CampaignSlug}; campaign {{\"action\": \"list\"}} shows the campaigns.");
        if (campaign.Id == b.Campaign.Id)
        {
            throw o.Fail($"to {cross.Text} is in this campaign; name it without the campaign prefix (a same_as inside one campaign is a relation).");
        }

        return new HandleResolver(b.Connection, campaign.Id, b.Transaction).TryEntity(cross.Inner) ??
               throw o.Fail($"to: no entity {cross.Inner.Text} in campaign {campaign.Slug}.");
    }

    private static Dictionary<string, object?> RelationMatch(string fromId, string rel, string toId) => new(StringComparer.Ordinal)
    {
        ["from_id"] = fromId,
        ["rel"] = rel,
        ["to_id"] = toId,
    };

    private static (string A, string B) Ordered(string x, string y) => string.CompareOrdinal(x, y) < 0 ? (x, y) : (y, x);
}
