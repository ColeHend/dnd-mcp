using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the One Piece reveal gate ("eating a devil fruit breaks part of Baal's seal") evaluates exactly as the
/// goldens of understand-onepiece.md §3 rows 7-24 say, step by step through T1-T5, as pure inputs: the vocabulary rule
/// lifts with the axe (not the reveal) and comes back for an earlier session; unmet <c>after</c> and <c>with</c> are
/// reported, never refused; <c>with</c> is symmetric and means the same session; <c>prefer</c> is advisory; the routes
/// and the derived secret status progress hidden → seeded → partial → revealed.
/// </summary>
public sealed class OnePieceGateTests
{
    private const string Seal = "seal";
    private const string NadarPlan = "nadar-plan";
    private const string AxeAssembled = "axe-assembled";
    private const string HoldsFruitAndShard = "holds-fruit-and-shard";
    private const string NoUneatenFruits = "no-uneaten-fruits";

    private static readonly GateSpec SealGate = new()
    {
        After = [AxeAssembled, HoldsFruitAndShard],
        With = [NadarPlan],
        Prefer = [NoUneatenFruits],
        ForbiddenTerms = ["seal"],
        ForbiddenUntil = [AxeAssembled],
        PreferredTerms = ["shell", "wrapping", "what keeps it in"],
        Seeds = ["shielded-child"],
        Routes =
        [
            new RouteSpec { Id = "testimonies", Clues = ["t-protector", "t-peaceful", "t-mistaken", "t-dutiful"], MinClues = 4 },
            new RouteSpec { Id = "illusion", Clues = ["illusion-rewatched"], MinClues = 1 },
            new RouteSpec { Id = "temple-arithmetic", Clues = ["breaches-climbing", "fruit-appearances-falling"], MinClues = 2 },
        ],
        MinRoutes = 2,
        Note = "Reveal order — do not break this (canon-core.md:172-195)",
    };

    private static readonly GateSpec NadarGate = new() { With = [Seal], Note = "the two land together or not at all" };

    [Fact]
    public void Evaluate_Row16AsOfSession5_IsHiddenWithNoRouteProgress()
    {
        var world = World.Baseline();

        var status = world.Evaluate(asOf: 5);

        Assert.Equal([0, 0, 0], status.Routes.Select(r => r.Known));
        Assert.Equal([4, 1, 2], status.Routes.Select(r => r.Needed));
        Assert.Equal(0, status.RoutesComplete);
        Assert.Equal(2, status.MinRoutes);
        Assert.False(status.Seeded);
        Assert.Equal("hidden", world.SecretStatus(asOf: 5));
    }

    [Fact]
    public void Evaluate_Row17Baseline_IsSeededByTheShieldedChild()
    {
        var world = World.Baseline();

        var status = world.Evaluate(asOf: 7);

        Assert.True(status.Seeded);
        Assert.Equal(0, status.RoutesComplete);
        Assert.Equal("seeded", world.SecretStatus(asOf: 7));
    }

    [Fact]
    public void Evaluate_Row18AfterT1_TestimoniesOneOfFourStillSeeded()
    {
        var world = World.Baseline().T1();

        var status = world.Evaluate(asOf: 8);

        Assert.Equal(new RouteStatus("testimonies", 1, 4, 4, false), status.Routes[0]);
        Assert.Equal(0, status.RoutesComplete);
        Assert.Equal("seeded", world.SecretStatus(asOf: 8));
    }

    [Fact]
    public void Evaluate_Row19AfterT2_PartialWithTheIllusionRouteAndNotReady()
    {
        var world = World.Baseline().T1().T2();

        var status = world.Evaluate(asOf: 9);

        Assert.Equal(["illusion"], status.CompleteRouteIds);
        Assert.Equal(1, status.RoutesComplete);
        Assert.False(status.Ready);
        Assert.Equal("partial", world.SecretStatus(asOf: 9));
    }

    [Fact]
    public void BecameReachableBeforeGate_Row20TheT3Call_FiresOnTheCallThatCompletesTheSecondRoute()
    {
        var beforeT3 = World.Baseline().T1().T2().Evaluate(asOf: 10);
        var afterT3 = World.Baseline().T1().T2().T3().Evaluate(asOf: 10);
        var beforeT2 = World.Baseline().T1().Evaluate(asOf: 9);
        var afterT2 = World.Baseline().T1().T2().Evaluate(asOf: 9);

        Assert.True(FactGates.BecameReachableBeforeGate(beforeT3, afterT3));
        Assert.Equal([AxeAssembled], afterT3.UnmetAfter);
        Assert.False(FactGates.BecameReachableBeforeGate(beforeT2, afterT2));
        Assert.False(FactGates.BecameReachableBeforeGate(afterT3, afterT3));
    }

    [Fact]
    public void Evaluate_Row21AfterT3_BothRoutesCompleteButTheAxeIsNotAssembled()
    {
        var world = World.Baseline().T1().T2().T3();

        var status = world.Evaluate(asOf: 10);

        Assert.Equal(["illusion", "temple-arithmetic"], status.CompleteRouteIds);
        Assert.True(status.RoutesMet);
        Assert.False(status.Ready);
        Assert.True(status.ReachableBeforeGate);
        Assert.Equal("partial", world.SecretStatus(asOf: 10));
    }

    [Fact]
    public void Evaluate_Row22AfterT4_ReadyAndMustLandWithNadarsPlan()
    {
        var world = World.Baseline().T1().T2().T3().T4();

        var status = world.Evaluate(asOf: 11);

        Assert.True(status.Ready);
        Assert.False(status.ReachableBeforeGate);
        Assert.Equal([NadarPlan], status.MustLandWith);
        Assert.Equal("partial", world.SecretStatus(asOf: 11));
    }

    [Fact]
    public void Evaluate_Row24AfterT5_Revealed()
    {
        var world = World.Baseline().T1().T2().T3().T4().T5();

        var status = world.Evaluate(asOf: 12);

        Assert.Empty(status.MustLandWith);
        Assert.Equal("revealed", world.SecretStatus(asOf: 12));
    }

    [Theory]
    [InlineData("baseline", null, true)]
    [InlineData("t3", null, true)]
    [InlineData("t4", null, false)]
    [InlineData("t4", 10, true)]
    [InlineData("t4", 11, false)]
    [InlineData("t5", null, false)]
    public void Evaluate_Rows7And8ForbiddenVocabulary_LiftsWithTheAxeAndHoldsForEarlierSessions(string step, int? asOf, bool active)
    {
        var world = step switch
        {
            "baseline" => World.Baseline(),
            "t3" => World.Baseline().T1().T2().T3(),
            "t4" => World.Baseline().T1().T2().T3().T4(),
            _ => World.Baseline().T1().T2().T3().T4().T5(),
        };

        Assert.Equal(active, world.Evaluate(asOf).ForbiddenActive);
    }

    [Fact]
    public void Evaluate_ForbiddenWithoutForbiddenUntil_LiftsWhenThePartyKnowsTheGatedFact()
    {
        var gate = new GateSpec { ForbiddenTerms = ["seal"] };

        Assert.True(FactGates.Evaluate(gate, _ => true, _ => true, gatedFactKnownToParty: false).ForbiddenActive);
        Assert.False(FactGates.Evaluate(gate, _ => false, _ => false, gatedFactKnownToParty: true).ForbiddenActive);
        Assert.False(FactGates.Evaluate(new GateSpec { After = ["x"] }, _ => false, _ => false, false).ForbiddenActive);
    }

    [Fact]
    public void RevealChecks_Row9AfterT3RevealingTheSealAlone_WarnsAfterAndWithButNotTheMetCondition()
    {
        var world = World.Baseline().T1().T2().T3();

        var status = world.Evaluate(asOf: 10);
        var with = FactGates.CheckWith(SealGate, world.LearnedByParty, 10, new HashSet<string> { Seal });

        Assert.Equal([AxeAssembled], status.UnmetAfter);
        Assert.DoesNotContain(HoldsFruitAndShard, status.UnmetAfter);
        Assert.Equal([NadarPlan], with.Unmet);
        Assert.Empty(with.LandedSeparately);
    }

    [Fact]
    public void RevealChecks_Row12AfterT4_OnlyTheWithCouplingIsUnmet()
    {
        var world = World.Baseline().T1().T2().T3().T4();

        Assert.Empty(world.Evaluate(asOf: 12).UnmetAfter);
        Assert.Equal([NadarPlan], FactGates.CheckWith(SealGate, world.LearnedByParty, 12, new HashSet<string> { Seal }).Unmet);
    }

    [Fact]
    public void RevealChecks_Row13NadarsPlanAlone_TheCouplingIsSymmetric()
    {
        var world = World.Baseline().T1().T2().T3().T4();

        // Through Nadar's own gate…
        Assert.Equal([Seal], FactGates.CheckWith(NadarGate, world.LearnedByParty, 12, new HashSet<string> { NadarPlan }).Unmet);

        // …and through the seal fact's gate alone, when Nadar's plan has no gate of its own.
        var partners = FactGates.WithPartners(NadarPlan, null, [(Seal, SealGate), (NadarPlan, null), ("other", new GateSpec { With = ["x"] })]);
        Assert.Equal([Seal], partners);
        Assert.Equal([Seal], FactGates.CheckWith(partners, world.LearnedByParty, 12, new HashSet<string> { NadarPlan }).Unmet);
    }

    [Fact]
    public void RevealChecks_Row14NadarsPlanLearnedInSession8_LandedSeparately()
    {
        var world = World.Baseline().T1().T2().T3().T4().Learn(NadarPlan, 8);

        var with = FactGates.CheckWith(SealGate, world.LearnedByParty, 12, new HashSet<string> { Seal });

        Assert.Empty(with.Unmet);
        Assert.Equal([new LandedSeparately(NadarPlan, 8)], with.LandedSeparately);
        Assert.False(with.Met);
    }

    [Fact]
    public void RevealChecks_Row15T5BothInOneCall_OnlyTheAdvisoryPreferRemains()
    {
        var world = World.Baseline().T1().T2().T3().T4();

        var status = world.Evaluate(asOf: 12);
        var with = FactGates.CheckWith(SealGate, world.LearnedByParty, 12, new HashSet<string> { Seal, NadarPlan });

        Assert.Empty(status.UnmetAfter);
        Assert.True(status.RoutesMet);
        Assert.True(with.Met);
        Assert.Equal([NoUneatenFruits], status.UnmetPrefer);
    }

    [Fact]
    public void RevealChecks_Row23T5WithOnlyT2Done_TooFewRoutes()
    {
        var world = World.Baseline().T1().T2().T4();

        var status = world.Evaluate(asOf: 12);

        Assert.Equal(1, status.RoutesComplete);
        Assert.Equal(2, status.MinRoutes);
        Assert.False(status.RoutesMet);
        Assert.Equal(["illusion"], status.CompleteRouteIds);
    }

    [Theory]
    [InlineData(12, 12, true)]
    [InlineData(11, 12, false)]
    [InlineData(null, 12, false)]
    public void CheckWith_SameSessionInAnEarlierBatch_CountsAsTogether(int? learned, int reveal, bool together)
    {
        var with = FactGates.CheckWith(new GateSpec { With = [NadarPlan] }, f => f == NadarPlan ? learned ?? FactGates.KnownWithoutSession : null, reveal,
            new HashSet<string>());

        Assert.Equal(together, with.Met);
        if (!together)
        {
            Assert.Equal([new LandedSeparately(NadarPlan, learned)], with.LandedSeparately);
        }
    }

    [Fact]
    public void CheckWith_NoRevealSession_OnlyTheSameBatchIsTogether()
    {
        var gate = new GateSpec { With = [NadarPlan] };

        Assert.False(FactGates.CheckWith(gate, _ => 12, null, new HashSet<string>()).Met);
        Assert.True(FactGates.CheckWith(gate, _ => 12, null, new HashSet<string> { NadarPlan }).Met);
    }

    /// <summary>
    /// The fixture's world as pure data: when each fact was established (and whether it is in play) and when the party
    /// learned each fact. Steps T1-T5 of understand-onepiece.md §3.
    /// </summary>
    private sealed record World(IReadOnlyDictionary<string, (int? Session, string Canon)> Established, IReadOnlyDictionary<string, int> PartyLearned)
    {
        private static readonly string[] NotInPlay = ["proposed", "planned", "struck", "superseded", "lean"];

        public static World Baseline() => new(
            new Dictionary<string, (int?, string)>
            {
                [HoldsFruitAndShard] = (5, "played"),
                [AxeAssembled] = (null, "planned"),
                [NoUneatenFruits] = (null, "planned"),
                ["shielded-child"] = (6, "played"),
            },
            new Dictionary<string, int> { ["shielded-child"] = 6, [HoldsFruitAndShard] = 5 });

        public World T1() => Learn("t-protector", 8);

        public World T2() => Learn("illusion-rewatched", 9);

        public World T3() => Learn("breaches-climbing", 10).Learn("fruit-appearances-falling", 10);

        public World T4() => this with
        {
            Established = new Dictionary<string, (int?, string)>(Established) { [AxeAssembled] = (11, "played") },
        };

        public World T5() => Learn(Seal, 12).Learn(NadarPlan, 12);

        public World Learn(string fact, int session) => this with
        {
            PartyLearned = new Dictionary<string, int>(PartyLearned) { [fact] = session },
        };

        public bool InPlay(string fact, int? asOf) =>
            Established.TryGetValue(fact, out var e) && e.Session is { } s && (asOf is null || s <= asOf) && !NotInPlay.Contains(e.Canon);

        public bool PartyKnows(string fact, int? asOf) => PartyLearned.TryGetValue(fact, out var s) && (asOf is null || s <= asOf);

        public int? LearnedByParty(string fact) => PartyLearned.TryGetValue(fact, out var s) ? s : null;

        public GateStatus Evaluate(int? asOf) =>
            FactGates.Evaluate(SealGate, f => InPlay(f, asOf), f => PartyKnows(f, asOf), PartyKnows(Seal, asOf));

        public string SecretStatus(int? asOf)
        {
            var status = Evaluate(asOf);
            return SecretStatuses.Derive(PartyKnows(Seal, asOf), status.RoutesComplete > 0, status.Seeded);
        }
    }
}
