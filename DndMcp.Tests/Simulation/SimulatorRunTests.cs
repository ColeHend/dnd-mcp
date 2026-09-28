using System.Text.Json;
using DndMcp.Domain.Simulation;
using Xunit;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// <see cref="Simulator.Run"/> as a whole (contract §5.6–5.8): determinism at any thread count, replay, the
/// common-random-numbers comparison, precision batches, progress and cancellation, and the sanity fights.
/// </summary>
public sealed class SimulatorRunTests
{
    private static SimulationSpec Skirmish(int iterations = 3_000, int? replay = null, CompareSpec? compare = null) => SimKit.Spec(
        [SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 2)],
        [SimKit.Monster(TestStatBlocks.Goblin, count: 3), SimKit.Monster(TestStatBlocks.Wolf, count: 2), SimKit.Monster(TestStatBlocks.Troll)],
        iterations: iterations, enemyHp: "roll", replay: replay, compare: compare);

    private static string Canonical(SimulationReport report) => JsonSerializer.Serialize(report);

    // ------------------------------------------------------------------------------------------------------------------
    // Determinism (contract §5.8 test 3).
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Run_SameSeed_ByteIdenticalReportAt1_4And16Threads()
    {
        var one = Canonical(Simulator.Run(Skirmish(), 42, maxThreads: 1));
        var four = Canonical(Simulator.Run(Skirmish(), 42, maxThreads: 4));
        var sixteen = Canonical(Simulator.Run(Skirmish(), 42, maxThreads: 16));
        Assert.Equal(one, four);
        Assert.Equal(one, sixteen);
    }

    [Fact]
    public void Run_DifferentSeeds_DifferentReports() =>
        Assert.NotEqual(Canonical(Simulator.Run(Skirmish(), 42)), Canonical(Simulator.Run(Skirmish(), 43)));

    [Fact]
    public void Run_IsTheSumOfItsFights_EachReproducibleAlone()
    {
        const int fights = 60;
        var report = Simulator.Run(Skirmish(fights), 7);
        var wins = 0;
        var histogram = new long[report.RoundCap];
        for (var i = 0; i < fights; i++)
        {
            var (outcome, _) = Simulator.RunOne(Skirmish(fights), 7, i);
            wins += outcome.Outcome == SimulationValues.Outcomes.PartyWins ? 1 : 0;
            histogram[outcome.Rounds - 1]++;
        }

        Assert.Equal(report.PartyWins.Count, wins);
        Assert.Equal(report.Rounds.Histogram, histogram);
    }

    [Fact]
    public void Replay_ReproducesThatFightOfTheFullRun_WithAFullLog()
    {
        var report = Simulator.Run(Skirmish(200, replay: 17), 99);
        var (alone, _) = Simulator.RunOne(Skirmish(200), 99, 16);
        Assert.Equal(17, report.ReplayIteration);
        Assert.Equal(alone.Outcome, report.ReplayOutcome);
        Assert.Equal(alone.Rounds, report.ReplayRounds);

        var log = report.ReplayLog!;
        Assert.Contains("Initiative: Fighter, Fighter 2", log);
        Assert.Contains("Round 1", log);
        Assert.Contains("d20", log);
        Assert.Contains("Summary of fight 17:", log);
        Assert.Contains("- Troll (enemy):", log);
    }

    [Fact]
    public void Replay_LongFight_LogIsCappedButTheSummaryIsKept()
    {
        var walls = Enumerable.Range(1, 8).Select(i => SimKit.Pc(SimKit.Fighter2024, hp: 5000, ac: 12, name: $"Wall {i}")).ToList();
        var spec = SimKit.Spec(walls, [SimKit.Monster(TestStatBlocks.Sandbag(ac: 12), count: 8)], iterations: 1, roundCap: 100, replay: 1);
        var log = Simulator.Run(spec, 1).ReplayLog!;
        var summary = log.IndexOf("Summary of fight 1:", StringComparison.Ordinal);
        Assert.Contains("… truncated", log[..summary]);
        Assert.True(summary <= SimulationLimits.ReplayLogChars + 200, $"log body {summary} chars");
        Assert.Contains("- Sandbag 8 (enemy):", log[summary..]);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Comparison, precision, progress, cancellation.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_AFeatureThatChangesNothing_GivesIdenticalFights()
    {
        // Common random numbers: the variant fights on the same seeds; with nothing changed every fight is the same, so
        // the paired difference is exactly 0 with no spread at all.
        var feature = SimKit.Feature("""{ "name": "Nothing", "modifiers": [{ "kind": "to_hit", "name": "Zero", "amount": 0 }] }""");
        var report = Simulator.Run(Skirmish(2_000, compare: new CompareSpec { Member = 1, Feature = feature }), 5);
        var compare = report.Compare!;
        Assert.Equal(0, compare.WinDifference.Mean);
        Assert.Equal(0, compare.WinDifference.StandardError);
        Assert.Equal(compare.BaselineWins, compare.VariantWins);
        Assert.Equal(report.PartyWins, compare.BaselineWins);
        Assert.Equal("Nothing", compare.Feature);
    }

    [Fact]
    public void Compare_AStrongFeature_PairedDifferenceIsPositiveWithATightInterval()
    {
        var feature = SimKit.Feature("""{ "name": "Third attack", "attacks": [{ "name": "Greatsword", "count": 3, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }] }""");
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter")], [SimKit.Monster(TestStatBlocks.Ogre, count: 2)],
            iterations: 4_000, compare: new CompareSpec { Member = 1, Feature = feature });
        var compare = Simulator.Run(spec, 8).Compare!;
        Assert.True(compare.WinDifference.Low > 0, $"Δ {compare.WinDifference.Mean} [{compare.WinDifference.Low}, {compare.WinDifference.High}]");
        Assert.True(compare.VariantWins.Estimate > compare.BaselineWins.Estimate);
        // Paired: the difference's interval is narrower than the two runs' own intervals added up.
        Assert.True(compare.WinDifference.High - compare.WinDifference.Low < (compare.VariantWins.High - compare.VariantWins.Low) + (compare.BaselineWins.High - compare.BaselineWins.Low));
    }

    [Fact]
    public void Precision_StopsAtTheFirstBatchThatReachesTheHalfWidth()
    {
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18)], [SimKit.Monster(TestStatBlocks.Ogre)], precision: 0.02);
        var report = Simulator.Run(spec, 1);
        Assert.Equal(SimulationLimits.PrecisionBatch, report.Iterations);
        Assert.True(report.PrecisionReached);
        Assert.True(report.PartyWins.HalfWidth <= 0.02);
    }

    [Fact]
    public void Precision_UnreachableTarget_RunsTheMaximum()
    {
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Commoner, hp: 4, ac: 10)], [SimKit.Monster(TestStatBlocks.Commoner)], precision: 0.001);
        var report = Simulator.Run(spec, 1);
        Assert.Equal(SimulationLimits.MaxIterations, report.Iterations);
        Assert.False(report.PrecisionReached);
    }

    [Fact]
    public void Progress_ReportsEachChunk_UpToTheTotal()
    {
        var seen = new List<(int Completed, int Total)>();
        var progress = new SynchronousProgress(seen.Add);
        Simulator.Run(Skirmish(5_000), 1, progress: progress);
        Assert.Equal(5, seen.Count); // ⌈5,000 / 1,024⌉ chunks
        Assert.Equal((5_000, 5_000), seen[^1]);
        Assert.Equal(seen.Select(s => s.Completed).Order(), seen.Select(s => s.Completed));
    }

    [Fact]
    public void Run_CancelledToken_ThrowsOperationCanceled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Simulator.Run(Skirmish(), 1, source.Token));
    }

    [Fact]
    public void Report_EchoesTheRunAndItsAssumptions()
    {
        var report = Simulator.Run(Skirmish(500), 12345);
        Assert.Equal(12345UL, report.Seed);
        Assert.Equal(500, report.Iterations);
        Assert.Equal("2024", report.Edition);
        Assert.Equal("none", report.Surprise);
        Assert.Equal(["party", "enemies", "legendary_resistance", "healing", "finish_downed", "pcs_win_ties"], report.Policies.Select(p => p.Name));
        Assert.Equal("focus_fire", report.Policies[0].Value);
        Assert.Contains(report.Assumptions, a => a.Contains("not in a lair", StringComparison.Ordinal));
        Assert.Contains(report.Assumptions, a => a.Contains("troll", StringComparison.Ordinal));
        Assert.Equal(500, report.PartyWins.Count + report.PartyDefeated.Count + report.Draw.Count);
        Assert.Equal(["Fighter", "Goblin", "Wolf", "Troll"], report.Combatants.Select(c => c.Name));
        Assert.Equal(2, report.Combatants[0].Count);
        Assert.Equal("party", report.Combatants[0].Side);
        Assert.True(report.Combatants[0].DeathSaves);
        Assert.False(report.Combatants[1].DeathSaves);
        Assert.Equal(500, report.Rounds.Histogram.Sum());
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Sanity fights (contract §5.8 test 5): ranges with a reason, not goldens.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Sanity_FourLevel5FightersAgainstThreeOgres_PartyWinsMostOfTheTime()
    {
        // Four L5 fighters (2 × 2d6+4 at +7) deal ~70 a round against 3 × 59 HP at AC 11; the ogres (+6 vs AC 18, 2d8+4)
        // deal ~15. A medium-hard fight on paper that the party should carry nearly always.
        var report = Simulator.Run(SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 4)], [SimKit.Monster(TestStatBlocks.Ogre, count: 3)], iterations: 5_000), 2024);
        Assert.True(report.PartyWins.Estimate > 0.95, $"P(win) {report.PartyWins.Estimate}");
        Assert.InRange(report.Rounds.Mean.Mean, 2, 5);
    }

    [Fact]
    public void Sanity_OneCommonerAgainstAnAdultRedDragon_TheDragonWins()
    {
        var report = Simulator.Run(SimKit.Spec([SimKit.Pc(SimKit.Commoner, hp: 4, ac: 10, name: "Commoner")], [SimKit.Monster(TestStatBlocks.AdultRedDragon)], iterations: 2_000), 1);
        Assert.True(report.PartyDefeated.Estimate > 0.999, $"P(defeat) {report.PartyDefeated.Estimate}");
        Assert.True(report.Rounds.Mean.Mean < 1.5);
    }

    [Fact]
    public void Sanity_MirrorFight_IsEven()
    {
        // The same build on both sides (the enemy copy dies at 0 HP, the party one goes down: both end the fight):
        // P(party wins) among decided fights inside the 99.9% interval around 1/2 (seed 77, 20,000 fights).
        var enemy = new SimulationCombatant(new CombatantSpec { Name = "Mirror", Build = SimKit.Build(SimKit.Fighter2024), Hp = 44, Ac = 18 });
        var report = Simulator.Run(SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter")], [enemy], iterations: 20_000), 77);
        var decided = report.PartyWins.Count + report.PartyDefeated.Count;
        var even = SimulationStatistics.Wilson(report.PartyWins.Count, decided, SimulationStatistics.Z999);
        Assert.InRange(0.5, even.Low, even.High);
    }

    [Fact]
    public void Sanity_ADragonUsesItsBreathLegendaryActionsAndResistance()
    {
        var party = new List<SimulationCombatant>
        {
            SimKit.Pc(SimKit.Fighter2024, hp: 120, ac: 20, name: "Fighter", count: 3),
            SimKit.Pc("""
                { "name": "Wizard", "edition": "2024", "level": 11, "abilities": {"int": 20},
                  "attacks": [{ "name": "Fire Bolt", "to_hit": {"ability": "int"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "dice" }],
                  "modifiers": [{ "kind": "save_effect", "name": "Hold Monster", "ability": "wis", "dc": 17, "condition": "paralyzed", "concentration": true, "resource": {"uses": 2, "per": "long_rest"} }] }
                """, hp: 70, ac: 15, name: "Wizard"),
        };
        var report = Simulator.Run(SimKit.Spec(party, [SimKit.Monster(TestStatBlocks.AdultRedDragon)], iterations: 2_000), 17);
        var dragon = report.Combatants.Single(c => c.Name == "Adult Red Dragon");
        Assert.True(dragon.Resources.Single(r => r.Name == "Fire Breath").MeanUsed >= 1);
        Assert.True(dragon.LegendaryActions > 1);
        Assert.True(dragon.LegendaryResistanceSpent > 0.3, $"LR spent {dragon.LegendaryResistanceSpent}");
    }

    private sealed class SynchronousProgress(Action<(int Completed, int Total)> report) : IProgress<(int Completed, int Total)>
    {
        public void Report((int Completed, int Total) value) => report(value);
    }
}
