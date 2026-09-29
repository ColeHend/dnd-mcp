using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rng;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// <c>balance_simulate</c>'s engine (contract §5): Monte Carlo fights between a party and enemies — SRD monsters as
/// normalized <see cref="StatBlock"/>s, DSL builds, party archetypes — reported as outcome probabilities with Wilson
/// intervals, rounds, and per-combatant statistics.
///
/// <para>
/// <b>Reproducible by construction.</b> Fight i (0-based) draws everything from
/// <c>Xoshiro256StarStar(SplitMix64(seed ⊕ i·φ))</c> (<see cref="IterationSeed"/>), so a fight is a pure function of
/// (seed, i). Fights run in parallel in chunks sized by their work (<see cref="ChunkFights"/>) with per-thread integer
/// accumulators, merged at the end: integer sums do not depend on which thread ran which chunk, or how big the chunks
/// were, so the report is identical at 1, 4 or 16 threads, <c>replay</c> reproduces any fight
/// from the full run, and <c>compare</c> runs the baseline and the variant on the SAME seeds (common random numbers), so
/// their paired difference has a far smaller interval than two independent runs would.
/// </para>
/// <para>
/// <b>Bounded.</b> A run whose fights × combatants × round cap (× 2 with a comparison) exceeds
/// <see cref="SimulationLimits.WorkBudget"/> is refused up front with what to reduce. Precision mode is charged its first
/// batch, and its later batches stop where the next would pass the budget (<see cref="PrecisionFights"/>), reported as
/// the precision not reached at the work limit. Cancellation is honoured between fights, and progress is reported per
/// chunk (a second or so of one thread's work at most).
/// </para>
/// </summary>
public static class Simulator
{
    /// <summary>Validates, compiles the combatants once, runs the fights in parallel and aggregates.</summary>
    /// <param name="seed">The master seed (the host draws one from the OS when the caller gives none, and echoes it).</param>
    /// <param name="progress">Receives (fights completed, fights planned) after each chunk (<see cref="ChunkFights"/>).</param>
    /// <param name="maxThreads">At most this many threads (default: all); the report does not depend on it.</param>
    /// <exception cref="DndInputException">Bad input, or a run over the work budget (naming what to reduce).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static SimulationReport Run(SimulationSpec spec, ulong seed, CancellationToken ct = default,
                                       IProgress<(int Completed, int Total)>? progress = null, int? maxThreads = null) =>
        Run(Prepare(spec), seed, ct, progress, maxThreads);

    /// <summary>
    /// Validates and compiles <paramref name="spec"/> and checks it against the work budget, without fighting: a host that
    /// queues runs (one at a time on the fight threads) refuses bad input at once rather than after the queue.
    /// </summary>
    /// <exception cref="DndInputException">Bad input, or a run over the work budget (naming what to reduce).</exception>
    public static PreparedSimulation Prepare(SimulationSpec spec)
    {
        var run = SimulationPreparation.Prepare(spec);
        CheckBudget(run);
        return new PreparedSimulation(run);
    }

    /// <summary>Runs a prepared simulation's fights in parallel and aggregates; as <see cref="Run(SimulationSpec, ulong, CancellationToken, IProgress{ValueTuple{int, int}}?, int?)"/>.</summary>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static SimulationReport Run(PreparedSimulation prepared, ulong seed, CancellationToken ct = default,
                                       IProgress<(int Completed, int Total)>? progress = null, int? maxThreads = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var run = prepared.Run;
        ct.ThrowIfCancellationRequested();

        var total = new RunTally(run.Setup);
        var maxFights = run.Precision is null ? run.Iterations : PrecisionFights(run);
        var planned = run.Precision is null ? run.Iterations : Math.Min(SimulationLimits.PrecisionBatch, maxFights);
        var done = 0;
        bool? reached = null;
        while (true)
        {
            var batch = run.Precision is null ? run.Iterations : Math.Min(SimulationLimits.PrecisionBatch, maxFights - done);
            RunRange(run, seed, done, batch, total, ct, progress, planned, maxThreads);
            done += batch;
            if (run.Precision is not { } precision)
            {
                break;
            }

            var wins = SimulationStatistics.Wilson(total.Wins, total.Fights);
            if (wins.HalfWidth <= precision)
            {
                reached = true;
                break;
            }

            if (done >= maxFights)
            {
                reached = false;
                break;
            }

            planned = Math.Min(maxFights, done + SimulationLimits.PrecisionBatch);
        }

        // Precision mode accepts a replay up to 100,000 but may stop sooner (reached, or at the work limit). Fight i is a
        // pure function of (seed, i), so a replay past the fights run is still shown: skipping it left the replay out
        // without a word.
        string? replayLog = null;
        FightOutcome? replayed = null;
        if (run.Replay is { } replay)
        {
            var log = new CombatLog();
            var fight = new Fight(run.Setup);
            replayed = fight.Run(IterationSeed(seed, replay - 1), log);
            replayLog = log.Finish(ReplaySummary(fight, replayed.Value, replay));
        }

        return BuildReport(run, seed, done, total, reached, run.Precision is null ? null : maxFights, replayLog, replayed);
    }

    /// <summary>
    /// Fight i's seed (0-based): SplitMix64(master ⊕ i·φ), a pure function of the master seed and i, which the generator
    /// then expands into its state. The hash matters: handing master ⊕ i·φ straight to the generator (whose own seeding
    /// is a SplitMix64 stream) gives fights i and i+1 three of their four state words in common, and their first draws
    /// — round 1 — measurably correlated (the agreement battery caught round 1 running 2–3 standard errors low).
    /// </summary>
    internal static ulong IterationSeed(ulong master, int iteration) => new SplitMix64(master ^ ((ulong)iteration * SplitMix64.GoldenGamma)).Next();

    /// <summary>
    /// Runs one fight (0-based) on a fresh engine, optionally with a log: a test seam, for checking that a fight of the full
    /// run is a pure function of (seed, i). The replay does not go through here: it reuses the run's prepared setup.
    /// </summary>
    internal static (FightOutcome Outcome, Creature[] Creatures) RunOne(SimulationSpec spec, ulong seed, int iteration, CombatLog? log = null)
    {
        var run = SimulationPreparation.Prepare(spec);
        var fight = new Fight(run.Setup);
        var outcome = fight.Run(IterationSeed(seed, iteration), log);
        return (outcome, fight.Creatures);
    }

    /// <summary>
    /// One fight's work at most: combatants × round cap, twice with a comparison (the variant fights it again). The budget
    /// and the chunk size both count it, so a run's size and its chunks' size are the same measure.
    /// </summary>
    private static long FightWork(PreparedRun run) => (long)run.Combatants * run.Setup.RoundCap * (run.Variant is null ? 1 : 2);

    /// <summary>
    /// Fights per chunk: as many as <see cref="SimulationLimits.ChunkWork"/> allows (at least 1, at most
    /// <see cref="SimulationLimits.ChunkSize"/>), and few enough that <paramref name="count"/> fights make at least
    /// <paramref name="threads"/> chunks when there are that many fights. A chunk is the unit of both parallelism and
    /// progress: sized by fight count alone, 1,024 fights of 20 walls against 20 to round 100 were one chunk on one thread
    /// with no progress for 34 s, and 1,500 of them used two threads of sixteen. The report does not depend on it.
    /// </summary>
    internal static int ChunkFights(long fightWork, int count, int threads)
    {
        var byWork = (int)Math.Clamp(SimulationLimits.ChunkWork / Math.Max(1, fightWork), 1, SimulationLimits.ChunkSize);
        var spread = Math.Max(1, count / Math.Max(1, threads));
        return Math.Min(byWork, spread);
    }

    /// <summary>
    /// The most fights precision mode may run: <see cref="SimulationLimits.MaxIterations"/>, or fewer whole batches when
    /// the next batch would take the run past <see cref="SimulationLimits.WorkBudget"/>. At least the first batch, which
    /// <see cref="CheckBudget"/> has already charged. Charging precision all 100,000 fights up front refused a 4v2 with a
    /// comparison that reached its precision in its first 10,000; stopping here keeps the same worst case.
    /// </summary>
    private static int PrecisionFights(PreparedRun run)
    {
        var batches = SimulationLimits.WorkBudget / (SimulationLimits.PrecisionBatch * FightWork(run));
        return (int)Math.Min(SimulationLimits.MaxIterations, Math.Max(1, batches) * SimulationLimits.PrecisionBatch);
    }

    /// <summary>
    /// Refuses a run whose fights would pass <see cref="SimulationLimits.WorkBudget"/>: its iterations, or in precision
    /// mode its first batch, the least it runs (<see cref="PrecisionFights"/> stops the rest at the budget).
    /// </summary>
    private static void CheckBudget(PreparedRun run)
    {
        var fights = run.Precision is null ? run.Iterations : SimulationLimits.PrecisionBatch;
        var work = fights * FightWork(run);
        if (work <= SimulationLimits.WorkBudget)
        {
            return;
        }

        var compare = run.Variant is null ? "" : " × 2 (compare)";
        var reduce = run.Precision is null
            ? "Lower iterations, the round cap (most fights end well before 20 rounds) or the number of combatants."
            : FormattableString.Invariant(
                $"Lower the round cap (most fights end well before 20 rounds) or the number of combatants, or give fewer iterations instead of precision (it runs at least one batch of {SimulationLimits.PrecisionBatch:N0} fights).");
        throw new DndInputException(FormattableString.Invariant(
            $"this simulation is too large: {fights:N0} fights × {run.Combatants} combatants × round cap {run.Setup.RoundCap}{compare} = {work:N0}, over the limit of {SimulationLimits.WorkBudget:N0}. {reduce}"));
    }

    private sealed class Worker(PreparedRun run)
    {
        public Fight Baseline { get; } = new(run.Setup);

        public Fight? Variant { get; } = run.Variant is null ? null : new Fight(run.Variant);

        public RunTally Tally { get; } = new(run.Setup);
    }

    private static void RunRange(PreparedRun run, ulong seed, int start, int count, RunTally total, CancellationToken ct,
                                 IProgress<(int Completed, int Total)>? progress, int planned, int? maxThreads)
    {
        var threads = maxThreads is > 0 ? maxThreads.Value : Environment.ProcessorCount;
        var size = ChunkFights(FightWork(run), count, threads);
        var chunks = (count + size - 1) / size;
        var gate = new object();
        var completed = start;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxThreads is > 0 ? maxThreads.Value : -1 };
        try
        {
            Parallel.For(
                0,
                chunks,
                options,
                () => new Worker(run),
                (chunk, _, worker) =>
                {
                    var from = start + (chunk * size);
                    var to = Math.Min(start + count, from + size);
                    for (var i = from; i < to; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var iterationSeed = IterationSeed(seed, i);
                        var outcome = worker.Baseline.Run(iterationSeed);
                        var baseline = worker.Tally.Add(outcome, worker.Baseline.Creatures);
                        if (worker.Variant is { } variant)
                        {
                            var variantOutcome = variant.Run(iterationSeed);
                            worker.Tally.AddVariant(variantOutcome, variant.Creatures, baseline, outcome.Rounds);
                        }
                    }

                    if (progress is not null)
                    {
                        lock (gate)
                        {
                            completed += to - from;
                            progress.Report((completed, planned));
                        }
                    }

                    return worker;
                },
                worker =>
                {
                    lock (gate)
                    {
                        total.Merge(worker.Tally);
                    }
                });
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
    }

    private static string ReplaySummary(Fight fight, FightOutcome outcome, int replay)
    {
        var lines = new List<string>
        {
            $"Summary of fight {replay.ToString(CultureInfo.InvariantCulture)}: {Describe(outcome.Outcome)} after {outcome.Rounds.ToString(CultureInfo.InvariantCulture)} round{(outcome.Rounds == 1 ? "" : "s")}.",
        };
        foreach (var c in fight.Creatures)
        {
            var state = c.Dead ? "dead" : c.Down ? (c.Stable ? "0 HP, stable" : "0 HP, dying") : $"{c.Hp}/{c.MaxHp} HP";
            lines.Add($"- {c.Label} ({(c.Side == 0 ? "party" : "enemy")}): {state}; dealt {c.DealtRaw} ({c.DealtEffective} effective), took {c.TakenRaw}" +
                      $"{(c.Kills > 0 ? $", {c.Kills} kill{(c.Kills == 1 ? "" : "s")}" : "")}{(c.LegendaryResistanceSpent > 0 ? $", Legendary Resistance ×{c.LegendaryResistanceSpent}" : "")}");
        }

        return string.Join("\n", lines) + "\n";
    }

    private static string Describe(string outcome) => outcome switch
    {
        SimulationValues.Outcomes.PartyWins => "the party wins",
        SimulationValues.Outcomes.PartyDefeated => "the party is defeated",
        _ => "a draw at the round cap",
    };

    private static SimulationReport BuildReport(PreparedRun run, ulong seed, int fights, RunTally tally, bool? reached, int? precisionMaxFights,
                                                string? replayLog, FightOutcome? replayed)
    {
        var n = tally.Fights;
        var combatants = new List<CombatantReport>();
        for (var e = 0; e < run.Entries.Count; e++)
        {
            combatants.Add(EntryReport(run, run.Entries[e], tally, tally.Entries[e], n));
        }

        CompareReport? compare = null;
        if (run.Variant is not null)
        {
            compare = new CompareReport
            {
                Member = run.CompareMember,
                MemberName = run.CompareMemberName!,
                Feature = run.CompareFeature!,
                BaselineWins = SimulationStatistics.Wilson(tally.Wins, n),
                VariantWins = SimulationStatistics.Wilson(tally.VariantWins, n),
                WinDifference = SimulationStatistics.Mean(tally.WinDiffSum, tally.WinDiffSquares, n),
                BaselineRounds = SimulationStatistics.Mean(tally.RoundsSum, tally.RoundsSquares, n),
                VariantRounds = SimulationStatistics.Mean(tally.VariantRoundsSum, tally.VariantRoundsSquares, n),
                RoundsDifference = SimulationStatistics.Mean(tally.RoundsDiffSum, tally.RoundsDiffSquares, n),
                BaselineAnyDeath = SimulationStatistics.Wilson(tally.AnyDeath, n),
                VariantAnyDeath = SimulationStatistics.Wilson(tally.VariantAnyDeath, n),
                AnyDeathDifference = SimulationStatistics.Mean(tally.DeathDiffSum, tally.DeathDiffSquares, n),
                BaselineAnyDying = SimulationStatistics.Wilson(tally.AnyDying, n),
                VariantAnyDying = SimulationStatistics.Wilson(tally.VariantAnyDying, n),
                AnyDyingDifference = SimulationStatistics.Mean(tally.DyingDiffSum, tally.DyingDiffSquares, n),
            };
        }

        return new SimulationReport
        {
            Seed = seed,
            Iterations = fights,
            RoundCap = run.Setup.RoundCap,
            Edition = run.Setup.Edition,
            Surprise = run.Setup.Surprise,
            EnemyHp = run.EnemyHp,
            Policies = run.Policies,
            PartyWins = SimulationStatistics.Wilson(tally.Wins, n),
            PartyDefeated = SimulationStatistics.Wilson(tally.Defeats, n),
            Draw = SimulationStatistics.Wilson(tally.Draws, n),
            AnyPartyDeath = SimulationStatistics.Wilson(tally.AnyDeath, n),
            AnyPartyDying = SimulationStatistics.Wilson(tally.AnyDying, n),
            Rounds = new RoundsSummary(
                SimulationStatistics.Mean(tally.RoundsSum, tally.RoundsSquares, n),
                SimulationStatistics.Percentile(tally.RoundsHistogram, 0.5, offset: 1),
                SimulationStatistics.Percentile(tally.RoundsHistogram, 0.9, offset: 1),
                tally.RoundsHistogram.ToArray()),
            Combatants = combatants,
            Warnings = run.Warnings,
            Assumptions = run.Assumptions,
            Precision = run.Precision,
            PrecisionReached = reached,
            PrecisionMaxFights = precisionMaxFights,
            ReplayIteration = replayLog is null ? null : run.Replay,
            ReplayLog = replayLog,
            ReplayOutcome = replayed?.Outcome,
            ReplayRounds = replayed?.Rounds,
            Compare = compare,
        };
    }

    /// <summary>
    /// One entry's report, per creature: shares and means over fights × copies, each mean's interval from each fight's
    /// total over the copies (<see cref="EntryTally"/>).
    /// </summary>
    private static CombatantReport EntryReport(PreparedRun run, PreparedEntry entry, RunTally tally, EntryTally squares, long fights)
    {
        var template = run.Setup.Templates[entry.Ids[0]];
        var copies = entry.Ids.Length;
        var n = fights * copies;
        long dropped = 0, dead = 0, dying = 0, lost = 0, startHp = 0, dealt = 0, dealtEff = 0, taken = 0, takenEff = 0, kills = 0, lr = 0, legendary = 0;
        var histogram = new long[entry.Ids.Max(id => tally.Creatures[id].HpLostHistogram.Length)];
        var resources = new long[template.Pc?.Resources.Length ?? 0];
        var limited = new long[template.Limited.Length];
        var pools = new long[template.PoolSizes.Length];
        foreach (var id in entry.Ids)
        {
            var c = tally.Creatures[id];
            dropped += c.Dropped;
            dead += c.Dead;
            dying += c.Dying;
            lost += c.HpLostSum;
            startHp += c.StartHpSum;
            dealt += c.DealtRaw;
            dealtEff += c.DealtEffective;
            taken += c.TakenRaw;
            takenEff += c.TakenEffective;
            kills += c.Kills;
            lr += c.LegendaryResistance;
            legendary += c.LegendaryActions;
            for (var i = 0; i < c.HpLostHistogram.Length; i++)
            {
                histogram[i] += c.HpLostHistogram[i];
            }

            for (var i = 0; i < resources.Length; i++)
            {
                resources[i] += c.ResourceUsed[i];
            }

            for (var i = 0; i < limited.Length; i++)
            {
                limited[i] += c.LimitedUsed[i];
            }

            for (var i = 0; i < pools.Length; i++)
            {
                pools[i] += c.PoolUsed[i];
            }
        }

        double Per(long sum) => n == 0 ? 0 : (double)sum / n;
        MeanEstimate Mean(long sum, long sumOfFightSquares) => SimulationStatistics.Mean(sum, sumOfFightSquares, fights, scale: copies);
        var usage = new List<ResourceUsage>();
        if (template.Pc is { } pc)
        {
            for (var i = 0; i < resources.Length; i++)
            {
                usage.Add(new ResourceUsage(pc.Resources[i].Label, pc.Resources[i].Uses, Per(resources[i])));
            }
        }

        for (var i = 0; i < limited.Length; i++)
        {
            var action = template.Limited[i];
            var available = action.Source.Usage.Kind == StatBlockValues.UsageKinds.PerDay ? action.Source.Usage.Uses ?? 1 : 0;
            usage.Add(new ResourceUsage(template.LimitedNames[i], available, Per(limited[i])));
        }

        for (var i = 0; i < pools.Length; i++)
        {
            usage.Add(new ResourceUsage($"spell slots ({template.PoolNames[i]})", template.PoolSizes[i], Per(pools[i])));
        }

        return new CombatantReport
        {
            Name = entry.Label,
            Side = entry.Side == 0 ? "party" : "enemies",
            Count = entry.Ids.Length,
            Source = entry.Source,
            MaxHp = Per(startHp),
            ArmorClass = template.ArmorClass,
            DeathSaves = template.PcLike,
            DroppedToZero = new CreatureShare(dropped, n),
            DeadAtEnd = new CreatureShare(dead, n),
            DyingAtEnd = new CreatureShare(dying, n),
            HpLost = Mean(lost, squares.HpLostSquares),
            HpLostP50 = SimulationStatistics.Percentile(histogram, 0.5),
            HpLostP90 = SimulationStatistics.Percentile(histogram, 0.9),
            DamageDealt = Mean(dealt, squares.DealtRawSquares),
            DamageDealtEffective = Mean(dealtEff, squares.DealtEffectiveSquares),
            DamageTaken = Mean(taken, squares.TakenRawSquares),
            DamageTakenEffective = Mean(takenEff, squares.TakenEffectiveSquares),
            Kills = Mean(kills, squares.KillsSquares),
            Resources = usage,
            LegendaryResistanceSpent = template.LegendaryResistance > 0 ? Per(lr) : null,
            LegendaryActions = template.LegendaryUses > 0 ? Per(legendary) : null,
        };
    }
}

/// <summary>
/// A simulation validated, compiled and within the work budget (<see cref="Simulator.Prepare"/>), ready to run: its
/// compiled combatants are shared read-only by every fight, so it can be run more than once.
/// </summary>
public sealed class PreparedSimulation
{
    internal PreparedSimulation(PreparedRun run)
    {
        Run = run;
    }

    internal PreparedRun Run { get; }
}
