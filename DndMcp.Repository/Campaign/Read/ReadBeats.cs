using Dapper;
using DndMcp.Domain.Campaign;
using K = DndMcp.Domain.Campaign.CampaignValues.Kinds;
using St = DndMcp.Domain.Campaign.CampaignValues.Statuses;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// The story web as the read path shows it to the author (review C03): a beat's prerequisites and the beats it leads to,
/// and whether it can happen next (<see cref="BeatReachability"/>, the write path's own rule and the PWA's).
///
/// <para>
/// <b>Why it is read here.</b> <c>link … leads_to</c> between two beats is stored as a beat edge, not a relation, so no
/// include of campaign_get showed it, and the only traces of a web the author built were the history and the write-time
/// "Now reachable" line. The session_prep prompt sends the model to the beats to see "which are reachable now and which
/// are locked behind which", which nothing could answer. Reachability is computed exactly as the write path computes it
/// (every beat not deleted, a missing status read as pending, edges from a cut beat neither satisfying nor blocking), so
/// a get never disagrees with the consequence a status change printed.
/// </para>
/// <para>
/// <b>Author view only.</b> The web is the author's prep: which beats exist, what they hang on and what is next is the
/// plan, and beat edges have no visibility of their own to say otherwise. Other views get no web and no marker.
/// </para>
/// <para>
/// <b>Point in time:</b> beats (status, deletion) and edges are read as of the scope's session, like every other row.
/// </para>
/// </summary>
internal static class ReadBeats
{
    /// <summary>Every beat's progress at the scope's point in time, by beat id (cut beats are left out, as the rule leaves them).</summary>
    public static IReadOnlyDictionary<string, BeatProgress> Progress(ReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var (nodes, edges) = Web(scope);
        return BeatReachability.Compute(nodes, edges.Select(e => new BeatLink(e.FromBeatId, e.ToBeatId, e.Mode)).ToList());
    }

    /// <summary>One beat's place in the web for the author view; null for any other view or entity.</summary>
    public static BeatView? View(ReadScope scope, EntityState beat)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(beat);
        if (!scope.IsAuthorView || beat.Row.Kind != K.Beat)
        {
            return null;
        }

        var (nodes, edges) = Web(scope);
        var progress = BeatReachability.Compute(nodes, edges.Select(e => new BeatLink(e.FromBeatId, e.ToBeatId, e.Mode)).ToList());
        var id = beat.Row.Id;
        EntityLink LinkOf(string beatId) => scope.Entity(beatId)?.Link ?? new EntityLink(scope.AuthorRef(beatId), K.Beat, "(deleted beat)");

        var after = edges.Where(e => e.ToBeatId == id)
            .Select(e => new BeatEdgeView(LinkOf(e.FromBeatId), e.Mode, scope.Entity(e.FromBeatId)?.Row.Status ?? St.BeatPending))
            .OrderBy(e => e.Beat.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Beat.Ref, StringComparer.Ordinal)
            .ToList();
        var leadsTo = edges.Where(e => e.FromBeatId == id)
            .Select(e => new BeatEdgeView(LinkOf(e.ToBeatId), e.Mode, scope.Entity(e.ToBeatId)?.Row.Status ?? St.BeatPending))
            .OrderBy(e => e.Beat.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Beat.Ref, StringComparer.Ordinal)
            .ToList();
        if (!progress.TryGetValue(id, out var mine))
        {
            // Cut: out of the story, so neither reachable nor blocked, and not counted by the beats it leads to.
            return new BeatView(BeatStandings.Cut, BeatGates.None, 0, 0, [], after, leadsTo);
        }

        var standing = mine.Met ? BeatStandings.Met : mine.Reachable ? BeatStandings.Reachable : BeatStandings.Blocked;
        return new BeatView(standing, mine.Gate, mine.MetCount, mine.TotalCount, mine.Blocking.Select(LinkOf).ToList(), after, leadsTo);
    }

    // The beats that exist at the scope's point in time (status replayed; a missing status is pending) and the edges then.
    private static (List<BeatNode> Nodes, List<BeatEdgeRow> Edges) Web(ReadScope scope)
    {
        var ids = scope.Connection.Query<string>(
            "SELECT id FROM entity WHERE campaign_id = @campaignId AND kind = @beat" + (scope.AsOf is null ? " AND deleted_at IS NULL" : string.Empty) +
            " ORDER BY seq", new { campaignId = scope.Campaign.Id, beat = K.Beat }).ToList();
        scope.LoadEntities(ids);
        var nodes = ids.Select(scope.Entity).OfType<EntityState>()
            .Where(b => b.Row.Kind == K.Beat)
            .Select(b => new BeatNode(b.Row.Id, b.Row.Name, b.Row.Status ?? St.BeatPending))
            .ToList();
        var edges = scope.Connection.Query<BeatEdgeRow>(
            $"SELECT {BeatEdgeRow.Columns} FROM beat_edge WHERE campaign_id = @campaignId", new { campaignId = scope.Campaign.Id }).ToList();
        if (scope.AsOf is { } session)
        {
            edges = AsOfRows.ForEntities<BeatEdgeRow>(scope.Connection, CampaignTables.BeatEdge, edges.Select(e => e.Id), ids, session)
                .Where(e => e.CampaignId == scope.Campaign.Id)
                .ToList();
        }

        return (nodes, edges);
    }
}
