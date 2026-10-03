using M = DndMcp.Domain.Campaign.CampaignValues.BeatEdgeModes;
using St = DndMcp.Domain.Campaign.CampaignValues.Statuses;

namespace DndMcp.Domain.Campaign;

/// <summary>A story beat as reachability reads it.</summary>
/// <param name="Id">The beat entity's id.</param>
/// <param name="Name">Its name, for ordering "reachable now".</param>
/// <param name="Status">pending, <c>met</c> (it happened: the stored flag) or <c>cut</c> (taken out of the story: archived).</param>
public sealed record BeatNode(string Id, string Name, string Status)
{
    public bool Met => Status == St.BeatMet;

    public bool Cut => Status == St.BeatCut;
}

/// <summary>"<paramref name="From"/> is a prerequisite of <paramref name="To"/>" (a <c>leads_to</c> beat edge).</summary>
/// <param name="Mode"><c>all_of</c> (every one must be met) or <c>any_of</c> (at least one); anything else counts as all_of.</param>
public sealed record BeatLink(string From, string To, string Mode);

/// <summary>What gates a beat, from its counted incoming edges: the "ALL OF →" / "ANY OF →" label.</summary>
public static class BeatGates
{
    public const string None = "none";
    public const string AllOf = "all_of";
    public const string AnyOf = "any_of";
    public const string Mixed = "mixed";
}

/// <summary>One beat's standing.</summary>
/// <param name="BeatId">The beat.</param>
/// <param name="Met">The stored flag, copied.</param>
/// <param name="Reachable">Not met, and its prerequisites satisfied: it shows in "reachable now".</param>
/// <param name="MetCount">Satisfied incoming edges: the "1" in "1 of 4".</param>
/// <param name="TotalCount">Counted incoming edges: the "4".</param>
/// <param name="Gate">One of <see cref="BeatGates"/>.</param>
/// <param name="Blocking">Unmet prerequisite beat ids, in the order their edges were given.</param>
public sealed record BeatProgress(string BeatId, bool Met, bool Reachable, int MetCount, int TotalCount, string Gate, IReadOnlyList<string> Blocking)
{
    /// <summary>Value equality including <see cref="Blocking"/>'s items, so a before/after comparison sees a real change only.</summary>
    public bool Equals(BeatProgress? other) =>
        other is not null && BeatId == other.BeatId && Met == other.Met && Reachable == other.Reachable && MetCount == other.MetCount &&
        TotalCount == other.TotalCount && Gate == other.Gate && Blocking.SequenceEqual(other.Blocking);

    public override int GetHashCode() => HashCode.Combine(BeatId, Met, Reachable, MetCount, TotalCount, Gate, Blocking.Count);
}

/// <summary>
/// Story-web reachability, a faithful port of the PWA's <c>reachability.ts</c> (DM Command's Story Web), so a campaign
/// imported from the PWA shows the same "reachable now" list here as it did there.
///
/// <para>
/// <b>Why it is total:</b> a beat's <c>met</c> is an explicit stored flag, never derived from its prerequisites. So
/// evaluation is one pass over the edges accumulating per-target counters; nothing follows an edge onward, and a cycle
/// (A → B → A) is just two ordinary edges. There is no visited set, depth cap or topological sort, and none is needed.
/// Deriving <c>met</c> transitively would make cycles hang or need a fixed point, and would silently mark beats as
/// happened that the table never played.
/// </para>
/// <para>
/// A beat is satisfied when every <c>all_of</c> prerequisite is met and, if it has any <c>any_of</c> edges, at least one
/// of those is. Edges from a cut beat, and edges with an endpoint that is not a beat here, neither satisfy nor block and
/// are left out of the counts. Cut beats never appear in the result.
/// </para>
/// </summary>
public static class BeatReachability
{
    /// <summary>Every beat that is not cut, in the order given, with its progress.</summary>
    public static IReadOnlyDictionary<string, BeatProgress> Compute(IReadOnlyList<BeatNode> beats, IReadOnlyList<BeatLink> edges)
    {
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(edges);

        var live = new OrderedDictionary<string, BeatNode>(StringComparer.Ordinal);
        foreach (var beat in beats)
        {
            if (!beat.Cut)
            {
                live[beat.Id] = beat;
            }
        }

        var tallies = new Dictionary<string, Tally>(StringComparer.Ordinal);
        foreach (var id in live.Keys)
        {
            tallies[id] = new Tally();
        }

        foreach (var edge in edges)
        {
            if (!tallies.TryGetValue(edge.To, out var tally) || !live.TryGetValue(edge.From, out var from))
            {
                continue;
            }

            tally.Total++;
            if (from.Met)
            {
                tally.Met++;
            }
            else
            {
                tally.Blocking.Add(from.Id);
            }

            if (edge.Mode == M.AnyOf)
            {
                tally.AnyOf++;
                if (from.Met)
                {
                    tally.AnyOfMet++;
                }
            }
            else
            {
                tally.AllOf++;
                if (from.Met)
                {
                    tally.AllOfMet++;
                }
            }
        }

        var result = new OrderedDictionary<string, BeatProgress>(StringComparer.Ordinal);
        foreach (var (id, beat) in live)
        {
            var tally = tallies[id];
            var satisfied = tally.AllOfMet == tally.AllOf && (tally.AnyOf == 0 || tally.AnyOfMet > 0);
            result[id] = new BeatProgress(id, beat.Met, !beat.Met && satisfied, tally.Met, tally.Total, GateOf(tally), tally.Blocking);
        }

        return result;
    }

    /// <summary>Beats not met, not cut and with their prerequisites satisfied, sorted by name.</summary>
    public static IReadOnlyList<BeatNode> ReachableNow(IReadOnlyList<BeatNode> beats, IReadOnlyList<BeatLink> edges)
    {
        var progress = Compute(beats, edges);
        return beats
            .Where(b => progress.TryGetValue(b.Id, out var p) && p.Reachable)
            .OrderBy(b => b.Name, StringComparer.InvariantCulture)
            .ThenBy(b => b.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One beat's progress, or null when it is unknown or cut.</summary>
    public static BeatProgress? Progress(string id, IReadOnlyList<BeatNode> beats, IReadOnlyList<BeatLink> edges) =>
        Compute(beats, edges).TryGetValue(id, out var progress) ? progress : null;

    /// <summary>Beats neither met nor reachable: the "6 more, further out" tail.</summary>
    public static int FurtherOutCount(IReadOnlyList<BeatNode> beats, IReadOnlyList<BeatLink> edges) =>
        Compute(beats, edges).Values.Count(p => !p.Met && !p.Reachable);

    /// <summary>
    /// Beats reachable in <paramref name="after"/> that were not reachable (or not present) in <paramref name="before"/>,
    /// in <paramref name="after"/>'s order: the "now reachable" consequence of marking a beat met.
    /// </summary>
    public static IReadOnlyList<string> NewlyReachable(IReadOnlyDictionary<string, BeatProgress> before, IReadOnlyDictionary<string, BeatProgress> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return after.Values
            .Where(p => p.Reachable && !(before.TryGetValue(p.BeatId, out var was) && was.Reachable))
            .Select(p => p.BeatId)
            .ToList();
    }

    private static string GateOf(Tally tally) =>
        tally.AllOf > 0 && tally.AnyOf > 0 ? BeatGates.Mixed
        : tally.AllOf > 0 ? BeatGates.AllOf
        : tally.AnyOf > 0 ? BeatGates.AnyOf
        : BeatGates.None;

    private sealed class Tally
    {
        public int AllOf;
        public int AllOfMet;
        public int AnyOf;
        public int AnyOfMet;
        public int Total;
        public int Met;
        public readonly List<string> Blocking = [];
    }
}
