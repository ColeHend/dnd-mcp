namespace DndMcp.Domain.Campaign;

/// <summary>A fact that depends, directly or through others, on a superseded one.</summary>
/// <param name="FactId">The dependent fact.</param>
/// <param name="Depth">1 for a direct dependent, 2 for a dependent of one, …</param>
/// <param name="Via">The fact one step closer to the superseded one (the superseded fact itself at depth 1).</param>
public sealed record Dependent(string FactId, int Depth, string Via);

/// <summary>A fact made stale by a superseded fact it depends on.</summary>
/// <param name="FactId">The stale fact.</param>
/// <param name="Superseded">The nearest superseded fact it depends on.</param>
/// <param name="Depth">Steps from that fact (1 = depends on it directly).</param>
public sealed record StaleFact(string FactId, string Superseded, int Depth);

/// <summary>
/// What a correction invalidates: the facts that rest on a superseded one (contract §6, PLAN "superseding a fact returns
/// its dependents").
///
/// <para>
/// <b>Flag, don't fix.</b> Superseding the old One Piece timeline makes "the fragments have been running down for 400
/// years" and everything built on it suspect; it does not make them false. So these functions only list dependents (each
/// once, at its shortest distance, with the fact it came through) and leave the dependents' canon status alone: the
/// author re-checks them. Breadth-first over <c>fact_dependency</c> edges, safe on cycles (a cycle is refused on write,
/// but a read must never hang on one written by hand).
/// </para>
/// </summary>
public static class Supersession
{
    /// <summary>Every fact that depends on <paramref name="factId"/>, nearest first, each once.</summary>
    /// <param name="edges">Every dependency edge: <c>FactId</c> depends on <c>DependsOn</c>.</param>
    public static IReadOnlyList<Dependent> Dependents(string factId, IReadOnlyList<(string FactId, string DependsOn)> edges)
    {
        ArgumentNullException.ThrowIfNull(factId);
        ArgumentNullException.ThrowIfNull(edges);
        var dependentsOf = Index(edges);
        var result = new List<Dependent>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { factId };
        var frontier = new List<(string Fact, int Depth)> { (factId, 0) };
        for (var at = 0; at < frontier.Count; at++)
        {
            var (fact, depth) = frontier[at];
            foreach (var dependent in dependentsOf.GetValueOrDefault(fact) ?? [])
            {
                if (seen.Add(dependent))
                {
                    result.Add(new Dependent(dependent, depth + 1, fact));
                    frontier.Add((dependent, depth + 1));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Every fact that transitively depends on one of <paramref name="supersededIds"/>, with the nearest one, excluding the
    /// superseded facts themselves. Order: nearest first, then edge order.
    /// </summary>
    public static IReadOnlyList<StaleFact> StaleFacts(IEnumerable<string> supersededIds, IReadOnlyList<(string FactId, string DependsOn)> edges)
    {
        ArgumentNullException.ThrowIfNull(supersededIds);
        ArgumentNullException.ThrowIfNull(edges);
        var roots = supersededIds.Distinct(StringComparer.Ordinal).ToList();
        var dependentsOf = Index(edges);
        var result = new List<StaleFact>();
        var seen = new HashSet<string>(roots, StringComparer.Ordinal);
        var frontier = roots.Select(r => (Fact: r, Root: r, Depth: 0)).ToList();
        for (var at = 0; at < frontier.Count; at++)
        {
            var (fact, root, depth) = frontier[at];
            foreach (var dependent in dependentsOf.GetValueOrDefault(fact) ?? [])
            {
                if (seen.Add(dependent))
                {
                    result.Add(new StaleFact(dependent, root, depth + 1));
                    frontier.Add((dependent, root, depth + 1));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Whether adding "<paramref name="factId"/> depends on <paramref name="dependsOn"/>" would close a cycle: the write
    /// path refuses it, because a cycle makes "what rests on this" meaningless.
    /// </summary>
    public static bool WouldCycle(string factId, string dependsOn, IReadOnlyList<(string FactId, string DependsOn)> edges) =>
        factId == dependsOn || Dependents(factId, edges).Any(d => d.FactId == dependsOn);

    private static Dictionary<string, List<string>> Index(IReadOnlyList<(string FactId, string DependsOn)> edges)
    {
        var dependentsOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (fact, dependsOn) in edges)
        {
            if (!dependentsOf.TryGetValue(dependsOn, out var list))
            {
                list = [];
                dependentsOf.Add(dependsOn, list);
            }

            list.Add(fact);
        }

        return dependentsOf;
    }
}
