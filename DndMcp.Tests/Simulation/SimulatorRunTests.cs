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
    public void Run_HeavyFights_ByteIdenticalReportWhateverTheChunkSize()
    {
        // 40 creatures to round 100 are chunked by work: 16 fights a chunk on one thread, 14 on seven (100 fights spread
        // over at least seven chunks), 6 on sixteen. Integer tallies merge the same whichever chunks and threads they came
        // from. (Twenty fighters against twenty goblins: the bound is heavy, the fights are short.)
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 20)], [SimKit.Monster(TestStatBlocks.Goblin, count: 20)],
            iterations: 100, roundCap: 100, enemyHp: "roll");
        Assert.Equal((16, 14, 6), (Simulator.ChunkFights(4_000, 100, 1), Simulator.ChunkFights(4_000, 100, 7), Simulator.ChunkFights(4_000, 100, 16)));

        var one = Canonical(Simulator.Run(spec, 42, maxThreads: 1));
        Assert.Equal(one, Canonical(Simulator.Run(spec, 42, maxThreads: 7)));
        Assert.Equal(one, Canonical(Simulator.Run(spec, 42, maxThreads: 16)));
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

    [Theory]
    [InlineData(0UL)]
    [InlineData(42UL)]
    [InlineData(20260927UL)]
    public void IterationSeed_ConsecutiveFights_DoNotStartOverlappingGeneratorStreams(ulong master)
    {
        // The generator seeds itself from a SplitMix64 stream starting at its seed: two seeds a multiple of the golden
        // gamma apart share state words, and their fights' first rolls are correlated. The fight seeds must be hashed.
        for (var i = 0; i < 64; i++)
        {
            var gap = Simulator.IterationSeed(master, i + 1) - Simulator.IterationSeed(master, i);
            for (ulong k = 1; k <= 4; k++)
            {
                Assert.NotEqual(k * Domain.Rng.SplitMix64.GoldenGamma, gap);
                Assert.NotEqual(0UL - (k * Domain.Rng.SplitMix64.GoldenGamma), gap);
            }
        }
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
        Assert.Equal((0.0, 0.0), (compare.AnyDyingDifference.Mean, compare.AnyDyingDifference.StandardError));
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
    public void Compare_AStrongFeature_PairedDifferencesAreVariantMinusBaseline()
    {
        // The mean of per-fight differences equals the difference of the two runs' means exactly (same fights, same n):
        // pins the direction of every paired difference (variant minus baseline), which the intervals alone do not.
        var feature = SimKit.Feature("""{ "name": "Third attack", "attacks": [{ "name": "Greatsword", "count": 3, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }] }""");
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter")], [SimKit.Monster(TestStatBlocks.Ogre, count: 2)],
            iterations: 2_000, compare: new CompareSpec { Member = 1, Feature = feature });
        var compare = Simulator.Run(spec, 8).Compare!;
        Assert.Equal(compare.VariantWins.Estimate - compare.BaselineWins.Estimate, compare.WinDifference.Mean, 9);
        Assert.Equal(compare.VariantRounds.Mean - compare.BaselineRounds.Mean, compare.RoundsDifference.Mean, 9);
        Assert.Equal(compare.VariantAnyDeath.Estimate - compare.BaselineAnyDeath.Estimate, compare.AnyDeathDifference.Mean, 9);
        Assert.True(compare.RoundsDifference.Mean < 0, $"a third attack should shorten the fight: Δ rounds {compare.RoundsDifference.Mean}");
    }

    [Fact]
    public void Compare_ALoneFighterWhoFallsLeftDying_PairsTheLeftDyingLikeTheDeaths()
    {
        // A lone fighter who drops ends the fight dying (nobody is left above 0 HP), so a third attack that saves some of
        // those fights must show in "left dying" beside "dies": variant minus baseline, fight by fight, like the deaths.
        var feature = SimKit.Feature("""{ "name": "Third attack", "attacks": [{ "name": "Greatsword", "count": 3, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }] }""");
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter")], [SimKit.Monster(TestStatBlocks.Ogre, count: 2)],
            iterations: 2_000, compare: new CompareSpec { Member = 1, Feature = feature });
        var report = Simulator.Run(spec, 8);
        var compare = report.Compare!;
        Assert.Equal(report.AnyPartyDying, compare.BaselineAnyDying);
        Assert.True(compare.BaselineAnyDying.Count > 0 && compare.VariantAnyDying.Count < compare.BaselineAnyDying.Count,
            $"left dying {compare.BaselineAnyDying.Count} without, {compare.VariantAnyDying.Count} with");
        Assert.Equal(compare.VariantAnyDying.Estimate - compare.BaselineAnyDying.Estimate, compare.AnyDyingDifference.Mean, 9);
        Assert.True(compare.AnyDyingDifference.High < 0, $"Δ {compare.AnyDyingDifference.Mean} [{compare.AnyDyingDifference.Low}, {compare.AnyDyingDifference.High}]");
    }

    [Fact]
    public void Run_FourFightersAgainstOneCommoner_OneKillAndNoPartyDeathEveryFight()
    {
        // The commoner (AC 10, 4 HP, +2 for 1d4 against AC 18 and 44 HP) can never kill a fighter and always dies: every
        // fight is one kill by the party and no party death, whatever the thread count merged it from.
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 4)], [SimKit.Monster(TestStatBlocks.Commoner)], iterations: 3_000);
        var report = Simulator.Run(spec, 11, maxThreads: 4);
        var fighters = report.Combatants.Single(c => c.Name == "Fighter");
        var commoner = report.Combatants.Single(c => c.Name == "Commoner");
        Assert.Equal(3_000, report.PartyWins.Count);
        Assert.Equal(0, report.AnyPartyDeath.Count);
        Assert.Equal(1.0, commoner.DeadAtEnd.Estimate);
        Assert.Equal(1.0, fighters.Kills.Mean * fighters.Count, 9);
    }

    [Fact]
    public void Run_ThePartyFallsWhileItsMemberIsDying_ReportsTheDyingApartFromTheDead()
    {
        // One 3 HP party member against a sandbag that hits for 1 on anything but a natural 1: every fight ends the moment
        // it drops (1 damage is never massive damage), dying, and the fight stops there with its death saves unrolled. The
        // report must say so, not count it alive and well — nor dead, which the dice never decided.
        var poker = TestStatBlocks.Sandbag(actions: [TestStatBlocks.Attack("Poke", 30, "1", "bludgeoning")]);
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Commoner, hp: 3, ac: 10, name: "Commoner")], [SimKit.Monster(poker)], iterations: 500);
        var report = Simulator.Run(spec, 5);
        var pc = report.Combatants[0];
        Assert.Equal(500, report.PartyDefeated.Count);
        Assert.Equal((0L, 500L), (report.AnyPartyDeath.Count, report.AnyPartyDying.Count));
        Assert.Equal((0L, 500L), (pc.DeadAtEnd.Count, pc.DyingAtEnd.Count));
        Assert.Equal(0, report.Combatants[1].DyingAtEnd.Count); // a monster without death saves is never dying
    }

    [Fact]
    public void Run_ThePartyWinsWithAMemberStillDying_CountsItAsDyingNotDead()
    {
        // Two members, one with 1 HP: the sandbag drops it at once, and the other ends the fight by beating a 1 HP sandbag
        // — before, or soon after. Every fight whose 1 HP member dropped and did not roll its way to stable, dead or up
        // again ends with it dying, whoever won.
        var poker = TestStatBlocks.Sandbag(hp: 1, ac: 5, actions: [TestStatBlocks.Attack("Poke", 30, "1", "bludgeoning")]);
        var spec = SimKit.Spec(
            [SimKit.Pc(SimKit.Commoner, hp: 1, ac: 10, name: "Frail"), SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 30, name: "Fighter")],
            [SimKit.Monster(poker)], iterations: 500, policies: new PolicySpec { Enemies = "focus_fire" });
        var report = Simulator.Run(spec, 9);
        var frail = report.Combatants[0];
        Assert.True(report.PartyWins.Count > 0 && frail.DyingAtEnd.Count > 0, $"{report.PartyWins.Count} wins, {frail.DyingAtEnd.Count} left dying");
        Assert.Equal(frail.DyingAtEnd.Count, report.AnyPartyDying.Count);
        Assert.True(frail.DyingAtEnd.Count + frail.DeadAtEnd.Count <= frail.DroppedToZero.Count);
    }

    [Fact]
    public void EntryMeans_CopiesCaughtByOneFireball_TheIntervalCountsFightsNotCreatures()
    {
        // One DC 30 Fireball a fight on four goblins, who all fail: each takes the same 8d6, so the four copies are one
        // sample per fight, not four. Their per-creature mean's standard error is the single roll's, sd(8d6)/√fights, a
        // quarter of the wizard's (who deals 4 × 8d6) — not half that, as it would be over fights × copies.
        const string wizard = """
            { "name": "Wizard", "edition": "2014", "level": 5, "abilities": {"int": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 30, "dice": "8d6", "type": "fire", "shape": "sphere", "size": 20 }] }
            """;
        var report = Simulator.Run(SimKit.Spec([SimKit.Pc(wizard, hp: 30, ac: 12)], [SimKit.Monster(TestStatBlocks.Goblin, count: 4)],
            iterations: 4_000, roundCap: 1, edition: "2014", surprise: "enemies"), 3);
        var dealt = report.Combatants[0].DamageDealt;
        var taken = report.Combatants[1].DamageTaken;
        Assert.Equal(dealt.Mean / 4, taken.Mean, 9);
        Assert.Equal(dealt.StandardError / 4, taken.StandardError, 9);
        Assert.Equal(4_000, taken.Samples);
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
    public void Precision_ExactlyTheFirstBatchsHalfWidth_StopsThere()
    {
        // The first precision batch is fights 0–9,999, the same fights as a 10,000-iteration run: asking for exactly that
        // run's half-width must stop after it (the target is "at most"), not run another batch.
        var fight = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18)], [SimKit.Monster(TestStatBlocks.Ogre)], iterations: SimulationLimits.PrecisionBatch);
        var halfWidth = Simulator.Run(fight, 3).PartyWins.HalfWidth;
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18)], [SimKit.Monster(TestStatBlocks.Ogre)], precision: halfWidth);
        var report = Simulator.Run(spec, 3);
        Assert.Equal(SimulationLimits.PrecisionBatch, report.Iterations);
        Assert.True(report.PrecisionReached);
    }

    [Fact]
    public void Precision_UnreachableTarget_RunsTheMaximum()
    {
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Commoner, hp: 4, ac: 10)], [SimKit.Monster(TestStatBlocks.Commoner)], precision: 0.001);
        var report = Simulator.Run(spec, 1);
        Assert.Equal(SimulationLimits.MaxIterations, report.Iterations);
        Assert.False(report.PrecisionReached);
        Assert.Equal(SimulationLimits.MaxIterations, report.PrecisionMaxFights);
    }

    [Fact]
    public void Precision_ReplayPastTheFightsRun_IsStillShown()
    {
        // Precision accepts a replay up to 100,000 but reaches ±2% here in its first 10,000 fights. Fight 15,000 is a pure
        // function of (seed, i), so it is shown, the same as in a run that got that far, not left out without a word.
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18)], [SimKit.Monster(TestStatBlocks.Ogre)], precision: 0.02, replay: 15_000);
        var report = Simulator.Run(spec, 1);
        var (alone, _) = Simulator.RunOne(spec, 1, 14_999);

        Assert.Equal(SimulationLimits.PrecisionBatch, report.Iterations);
        Assert.Equal(15_000, report.ReplayIteration);
        Assert.Equal(alone.Outcome, report.ReplayOutcome);
        Assert.Equal(alone.Rounds, report.ReplayRounds);
        Assert.Contains("Summary of fight 15000:", report.ReplayLog);
    }

    [Fact]
    public void Precision_TheNextBatchWouldPassTheWorkBudget_StopsThereNotReached()
    {
        // 10 combatants to round cap 100 is 1,000 of work a fight: 20,000 fights fill the 20 million budget, so a third
        // batch would pass it. Precision is charged only its first batch up front; the run stops after the second, not
        // reached, and the report says the work limit stopped it (100,000 fights would be 100 million).
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Commoner, hp: 4, ac: 10, count: 5)], [SimKit.Monster(TestStatBlocks.Commoner, count: 5)],
            roundCap: 100, precision: 0.001);
        var seen = new List<(int Completed, int Total)>();
        var report = Simulator.Run(spec, 1, progress: new SynchronousProgress(seen.Add), maxThreads: 4);

        Assert.Equal(2 * SimulationLimits.PrecisionBatch, report.Iterations);
        Assert.False(report.PrecisionReached);
        Assert.Equal(2 * SimulationLimits.PrecisionBatch, report.PrecisionMaxFights);
        Assert.Equal((20_000, 20_000), seen[^1]);
        Assert.DoesNotContain(seen, s => s.Total > 20_000); // never planned past the budget
    }

    [Fact]
    public void Progress_ReportsEachChunk_UpToTheTotal()
    {
        var seen = new List<(int Completed, int Total)>();
        var progress = new SynchronousProgress(seen.Add);
        Simulator.Run(Skirmish(5_000), 1, progress: progress, maxThreads: 1);
        Assert.Equal(13, seen.Count); // ⌈5,000 / 409⌉ chunks: 65,536 ÷ (8 combatants × round cap 20) fights each
        Assert.Equal((5_000, 5_000), seen[^1]);
        Assert.Equal(seen.Select(s => s.Completed).Order(), seen.Select(s => s.Completed));
    }

    [Fact]
    public void Progress_HeavyFights_ComesEverySixteenFightsNotEvery1024()
    {
        // 40 creatures to round 100 is up to 4,000 creature-turns a fight: a second or so of one thread per 16 such fights.
        // Chunked by fight count, 256 of them were one chunk with one report at the very end (1,024 fights of 20 walls
        // against 20: 34 s without progress). Twenty fighters against twenty 1 HP sandbags keep the fights themselves short.
        var seen = new List<(int Completed, int Total)>();
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 20)],
            [SimKit.Monster(TestStatBlocks.Sandbag(hp: 1, ac: 5), count: 20)], iterations: 256, roundCap: 100);
        Simulator.Run(spec, 1, progress: new SynchronousProgress(seen.Add), maxThreads: 1);
        Assert.True(seen.Count >= 4, $"{seen.Count} reports");
        Assert.All(seen.Zip(seen.Skip(1)), p => Assert.InRange(p.Second.Completed - p.First.Completed, 1, 16));
        Assert.Equal((256, 256), seen[^1]);
    }

    [Theory]
    [InlineData(140, 100_000, 15, 468)]  // 4v3 to round 20: 65,536 ÷ 140
    [InlineData(20, 100_000, 15, 1_024)] // light fights: at most 1,024 a chunk
    [InlineData(140, 1_000, 15, 66)]     // few fights: spread over at least 15 chunks (⌊1,000 / 15⌋ a chunk)
    [InlineData(8_000, 50, 15, 3)]       // 40 × 100 × 2 (compare): 8 by work, 3 to give 15 threads a chunk each
    [InlineData(8_000, 5, 15, 1)]        // fewer fights than threads: one a chunk
    [InlineData(80_000, 10, 1, 1)]       // heavier than a chunk: one fight a chunk still
    public void ChunkFights_SizedByWorkAndSpreadOverTheThreads(long fightWork, int count, int threads, int expected) =>
        Assert.Equal(expected, Simulator.ChunkFights(fightWork, count, threads));

    [Fact]
    public void Run_CancelledToken_ThrowsOperationCanceled()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Simulator.Run(Skirmish(), 1, source.Token));
    }

    [Fact]
    public void Run_CancelledDuringItsOnlyChunk_StopsBetweenFights()
    {
        // One chunk of long fights on one thread: 40 creatures that never fall, to round 25, are 1,000 creature-turns a
        // fight, so 65 fights a chunk (ChunkFights), a large share of a second. Cancelled 20 ms in, the run must stop at
        // the next fight, not finish the chunk (which the parallel loop alone would allow). Timed from the cancel itself
        // against half the chunk at this machine's current pace (the whole chunk timed first): load slows both.
        var walls = Enumerable.Range(1, 20).Select(i => SimKit.Pc(SimKit.Fighter2024, hp: 5000, ac: 30, name: $"Wall {i}")).ToList();
        SimulationSpec Spec(int fights) =>
            SimKit.Spec(walls, [SimKit.Monster(TestStatBlocks.Sandbag(ac: 30), count: 20)], iterations: fights, roundCap: 25);
        var chunk = Simulator.ChunkFights(40 * 25, SimulationLimits.MaxIterations, 1);
        Simulator.Run(Spec(8), 1, maxThreads: 1); // warm-up
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Simulator.Run(Spec(chunk), 1, maxThreads: 1);
        var bound = watch.Elapsed / 2;

        using var source = new CancellationTokenSource();
        var cancelledAt = TimeSpan.Zero;
        var canceller = new Thread(() =>
        {
            Thread.Sleep(20);
            cancelledAt = watch.Elapsed;
            source.Cancel();
        });
        watch.Restart();
        canceller.Start();
        Assert.ThrowsAny<OperationCanceledException>(() => Simulator.Run(Spec(chunk), 1, source.Token, maxThreads: 1));
        var stopped = watch.Elapsed;
        canceller.Join();

        Assert.True(stopped - cancelledAt < bound, $"{stopped - cancelledAt} to stop after the cancel; the {chunk}-fight chunk takes {bound * 2}");
    }

    [Fact]
    public void Report_WithoutAnEdition_TheFightFollowsThePartysRules()
    {
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Commoner, hp: 4, ac: 10, name: "Commoner")], [SimKit.Monster(TestStatBlocks.Goblin)], iterations: 10);
        Assert.Equal("2014", Simulator.Run(spec, 1).Edition);
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
