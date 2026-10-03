using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: story-web reachability matches the PWA's <c>reachability.ts</c> case for case (these are its
/// <c>reachability.test.ts</c> cases, ported): a beat is reachable when it is not met, every all_of prerequisite is met
/// and at least one any_of prerequisite is (if it has any); cut beats and dangling edges neither satisfy nor block;
/// cycles terminate because <c>met</c> is a stored flag, never derived.
/// </summary>
public sealed class BeatReachabilityTests
{
    private static BeatNode Beat(string id, bool met = false, bool cut = false, string? name = null) =>
        new(id, name ?? id, cut ? "cut" : met ? "met" : "pending");

    /// <summary>"a is a prerequisite of b", all_of unless told otherwise.</summary>
    private static BeatLink Edge(string from, string to, string mode = "all_of") => new(from, to, mode);

    // computeReachability — no dependencies

    [Fact]
    public void Compute_DependencyFreeUnmetBeat_IsReachableWithGateNone()
    {
        var p = BeatReachability.Compute([Beat("a")], [])["a"];

        Assert.Equal(new BeatProgress("a", false, true, 0, 0, BeatGates.None, []), p);
    }

    [Fact]
    public void Compute_AlreadyMetBeat_StaysInTheMapButOutOfReachableNow()
    {
        BeatNode[] beats = [Beat("a", met: true), Beat("b")];

        var p = BeatReachability.Compute(beats, [])["a"];

        Assert.True(p.Met);
        Assert.False(p.Reachable);
        Assert.Equal(["b"], BeatReachability.ReachableNow(beats, []).Select(b => b.Id));
    }

    // computeReachability — allOf gate

    [Fact]
    public void Compute_AllOfHalfMet_ReportsOneOfTwoAndBlocksOnTheUnmetPrerequisite()
    {
        var p = BeatReachability.Compute([Beat("a", met: true), Beat("b"), Beat("c")], [Edge("a", "c"), Edge("b", "c")])["c"];

        Assert.Equal(BeatGates.AllOf, p.Gate);
        Assert.Equal(1, p.MetCount);
        Assert.Equal(2, p.TotalCount);
        Assert.False(p.Reachable);
        Assert.Equal(["b"], p.Blocking);
    }

    [Fact]
    public void Compute_AllOfFullyMet_BecomesReachable()
    {
        var p = BeatReachability.Compute([Beat("a", met: true), Beat("b", met: true), Beat("c")], [Edge("a", "c"), Edge("b", "c")])["c"];

        Assert.True(p.Reachable);
        Assert.Equal(2, p.MetCount);
        Assert.Equal(2, p.TotalCount);
        Assert.Empty(p.Blocking);
    }

    [Fact]
    public void Compute_Blockers_AreInEdgeOrderNotBeatOrder()
    {
        var p = BeatReachability.Compute([Beat("a"), Beat("b"), Beat("c")], [Edge("b", "c"), Edge("a", "c")])["c"];

        Assert.Equal(["b", "a"], p.Blocking);
    }

    // computeReachability — anyOf gate

    [Fact]
    public void Compute_OneAnyOfMet_IsReachable()
    {
        var p = BeatReachability.Compute([Beat("a", met: true), Beat("b"), Beat("c")], [Edge("a", "c", "any_of"), Edge("b", "c", "any_of")])["c"];

        Assert.Equal(BeatGates.AnyOf, p.Gate);
        Assert.True(p.Reachable);
        Assert.Equal(1, p.MetCount);
        Assert.Equal(2, p.TotalCount);
        Assert.Equal(["b"], p.Blocking);
    }

    [Fact]
    public void Compute_NoAnyOfMet_IsBlocked()
    {
        var p = BeatReachability.Compute([Beat("a"), Beat("b"), Beat("c")], [Edge("a", "c", "any_of"), Edge("b", "c", "any_of")])["c"];

        Assert.False(p.Reachable);
        Assert.Equal(0, p.MetCount);
        Assert.Equal(["a", "b"], p.Blocking);
    }

    // computeReachability — mixed gate

    [Theory]
    [InlineData(false, false, 1, "optA|optB")]
    [InlineData(true, true, 2, "optB")]
    public void Compute_Mixed_NeedsTheAllOfSideAndOneAnyOf(bool anyMet, bool reachable, int metCount, string blocking)
    {
        BeatNode[] beats = [Beat("req", met: true), Beat("optA", met: anyMet), Beat("optB"), Beat("target")];
        BeatLink[] edges = [Edge("req", "target"), Edge("optA", "target", "any_of"), Edge("optB", "target", "any_of")];

        var p = BeatReachability.Compute(beats, edges)["target"];

        Assert.Equal(BeatGates.Mixed, p.Gate);
        Assert.Equal(reachable, p.Reachable);
        Assert.Equal(metCount, p.MetCount);
        Assert.Equal(3, p.TotalCount);
        Assert.Equal(blocking.Split('|'), p.Blocking);
    }

    [Fact]
    public void Compute_MixedWithAnyOfSatisfiedButAnAllOfUnmet_IsBlocked()
    {
        var p = BeatReachability.Compute([Beat("req"), Beat("opt", met: true), Beat("target")], [Edge("req", "target"), Edge("opt", "target", "any_of")])["target"];

        Assert.Equal(BeatGates.Mixed, p.Gate);
        Assert.False(p.Reachable);
        Assert.Equal(["req"], p.Blocking);
    }

    // computeReachability — archived (cut) beats and dangling edges

    [Fact]
    public void Compute_CutBeat_IsLeftOutOfTheMapReachableNowAndFurtherOut()
    {
        BeatNode[] beats = [Beat("a"), Beat("gone", cut: true)];

        var map = BeatReachability.Compute(beats, []);

        Assert.False(map.ContainsKey("gone"));
        Assert.Equal(["a"], map.Keys);
        Assert.Equal(["a"], BeatReachability.ReachableNow(beats, []).Select(b => b.Id));
        Assert.Equal(0, BeatReachability.FurtherOutCount(beats, []));
        Assert.Null(BeatReachability.Progress("gone", beats, []));
    }

    [Fact]
    public void Compute_EdgeFromACutBeat_IsIgnoredEntirely()
    {
        var p = BeatReachability.Compute([Beat("ghost", cut: true), Beat("live"), Beat("c")], [Edge("ghost", "c"), Edge("live", "c")])["c"];

        Assert.Equal(1, p.TotalCount);
        Assert.Equal(0, p.MetCount);
        Assert.Equal(["live"], p.Blocking);
        Assert.Equal(BeatGates.AllOf, p.Gate);
    }

    [Fact]
    public void Compute_EveryIncomingEdgeFromCutBeats_LeavesGateNoneAndReachable()
    {
        var p = BeatReachability.Compute([Beat("ghost", cut: true), Beat("c")], [Edge("ghost", "c")])["c"];

        Assert.Equal(BeatGates.None, p.Gate);
        Assert.Equal(0, p.TotalCount);
        Assert.True(p.Reachable);
    }

    [Fact]
    public void Compute_DanglingEdges_AreIgnoredRatherThanThrown()
    {
        var p = BeatReachability.Compute([Beat("a"), Beat("b")], [Edge("nope", "b"), Edge("a", "nope"), Edge("a", "b")])["b"];

        Assert.Equal(1, p.TotalCount);
        Assert.Equal(["a"], p.Blocking);
    }

    // computeReachability — cycles terminate

    [Fact]
    public void Compute_TwoCycle_TerminatesWithBothEndsBlocked()
    {
        BeatNode[] beats = [Beat("a"), Beat("b")];
        BeatLink[] edges = [Edge("a", "b"), Edge("b", "a")];

        var map = BeatReachability.Compute(beats, edges);

        Assert.Equal(2, map.Count);
        Assert.False(map["a"].Reachable);
        Assert.False(map["b"].Reachable);
        Assert.Equal(["b"], map["a"].Blocking);
        Assert.Equal(["a"], map["b"].Blocking);
        Assert.Equal(2, BeatReachability.FurtherOutCount(beats, edges));
    }

    [Fact]
    public void Compute_CycleWithOneSideMet_Opens()
    {
        var map = BeatReachability.Compute([Beat("a", met: true), Beat("b")], [Edge("a", "b"), Edge("b", "a")]);

        Assert.True(map["b"].Reachable);
        Assert.True(map["a"].Met);
        Assert.False(map["a"].Reachable);
    }

    [Fact]
    public void Compute_ThreeCycle_Terminates()
    {
        BeatNode[] beats = [Beat("a"), Beat("b"), Beat("c")];
        BeatLink[] edges = [Edge("a", "b"), Edge("b", "c"), Edge("c", "a")];

        Assert.Equal(3, BeatReachability.Compute(beats, edges).Count);
        Assert.Empty(BeatReachability.ReachableNow(beats, edges));
    }

    // reachableNow / furtherOutCount / beatProgress

    [Fact]
    public void ReachableNow_SortsByName()
    {
        BeatNode[] beats = [Beat("1", name: "Zephyr"), Beat("2", name: "Ashfall"), Beat("3", name: "Mire")];

        Assert.Equal(["Ashfall", "Mire", "Zephyr"], BeatReachability.ReachableNow(beats, []).Select(b => b.Name));
    }

    [Fact]
    public void FurtherOutCount_CountsOnlyUnmetAndUnreachableBeats()
    {
        BeatNode[] beats = [Beat("done", met: true), Beat("open"), Beat("next"), Beat("far")];
        BeatLink[] edges = [Edge("done", "next"), Edge("next", "far")];

        Assert.Equal(["next", "open"], BeatReachability.ReachableNow(beats, edges).Select(b => b.Id));
        Assert.Equal(1, BeatReachability.FurtherOutCount(beats, edges));
    }

    [Fact]
    public void Progress_MatchesTheMapEntryAndIsNullForUnknownIds()
    {
        BeatNode[] beats = [Beat("a", met: true), Beat("b")];
        BeatLink[] edges = [Edge("a", "b")];

        Assert.Equal(BeatReachability.Compute(beats, edges)["b"], BeatReachability.Progress("b", beats, edges));
        Assert.Null(BeatReachability.Progress("missing", beats, edges));
    }

    // Beyond the PWA: the write path's "now reachable" consequence.

    [Fact]
    public void NewlyReachable_MarkingAPrerequisiteMet_ListsTheBeatsItOpens()
    {
        BeatLink[] edges = [Edge("a", "b"), Edge("a", "c", "any_of"), Edge("x", "c", "any_of"), Edge("b", "d")];
        var before = BeatReachability.Compute([Beat("a"), Beat("b"), Beat("c"), Beat("d"), Beat("x")], edges);
        var after = BeatReachability.Compute([Beat("a", met: true), Beat("b"), Beat("c"), Beat("d"), Beat("x")], edges);

        Assert.Equal(["b", "c"], BeatReachability.NewlyReachable(before, after));
        Assert.Empty(BeatReachability.NewlyReachable(after, after));
    }
}
