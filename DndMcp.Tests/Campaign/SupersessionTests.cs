using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: superseding a fact lists everything that rests on it, each once at its shortest distance with the fact it
/// came through, safely on cycles; this is the One Piece timeline correction (understand-onepiece.md row 29): the old
/// timeline's two direct dependents at depth 1 and the lineage fact at depth 2 via the fight's date.
/// </summary>
public sealed class SupersessionTests
{
    private static readonly (string FactId, string DependsOn)[] Timeline =
    [
        ("fight-500-900", "timeline-old"),
        ("fragments-400", "timeline-old"),
        ("lineage-covers", "fight-500-900"),
        ("unrelated", "timeline-2026-08-30"),
    ];

    [Fact]
    public void Dependents_Row29TheOldTimeline_DepthOneTwiceThenDepthTwoVia()
    {
        var dependents = Supersession.Dependents("timeline-old", Timeline);

        Assert.Equal(
            [
                new Dependent("fight-500-900", 1, "timeline-old"),
                new Dependent("fragments-400", 1, "timeline-old"),
                new Dependent("lineage-covers", 2, "fight-500-900"),
            ],
            dependents);
    }

    [Fact]
    public void Dependents_ReachableTwoWays_IsListedOnceAtTheShorterDepth()
    {
        (string, string)[] edges = [("b", "a"), ("c", "b"), ("c", "a"), ("d", "c")];

        var dependents = Supersession.Dependents("a", edges);

        Assert.Equal([new Dependent("b", 1, "a"), new Dependent("c", 1, "a"), new Dependent("d", 2, "c")], dependents);
    }

    [Fact]
    public void Dependents_Cycle_TerminatesAndNeverListsTheRoot()
    {
        (string, string)[] edges = [("b", "a"), ("c", "b"), ("a", "c")];

        var dependents = Supersession.Dependents("a", edges);

        Assert.Equal(["b", "c"], dependents.Select(d => d.FactId));
    }

    [Fact]
    public void Dependents_NothingDepends_IsEmpty()
    {
        Assert.Empty(Supersession.Dependents("lineage-covers", Timeline));
    }

    [Fact]
    public void StaleFacts_EverythingThatRestsOnASupersededFact_WithTheNearestOne()
    {
        (string, string)[] edges = [.. Timeline, ("fragments-400", "near-best-case"), ("quote", "fragments-400")];

        var stale = Supersession.StaleFacts(["timeline-old", "near-best-case"], edges);

        Assert.Equal(
            [
                new StaleFact("fight-500-900", "timeline-old", 1),
                new StaleFact("fragments-400", "timeline-old", 1),
                new StaleFact("lineage-covers", "timeline-old", 2),
                new StaleFact("quote", "timeline-old", 2),
            ],
            stale);
    }

    [Fact]
    public void StaleFacts_ASupersededFactThatDependsOnAnother_IsNotAlsoStale()
    {
        (string, string)[] edges = [("b", "a"), ("c", "b")];

        var stale = Supersession.StaleFacts(["a", "b"], edges);

        Assert.Equal([new StaleFact("c", "b", 1)], stale);
    }

    [Theory]
    [InlineData("timeline-old", "lineage-covers", true)]
    [InlineData("timeline-old", "timeline-old", true)]
    [InlineData("lineage-covers", "timeline-old", false)]
    [InlineData("unrelated", "fight-500-900", false)]
    public void WouldCycle_ANewEdgeBackToADependent_IsACycle(string fact, string dependsOn, bool cycle)
    {
        Assert.Equal(cycle, Supersession.WouldCycle(fact, dependsOn, Timeline));
    }
}
