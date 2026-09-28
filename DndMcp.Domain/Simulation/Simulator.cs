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
/// (seed, i). Fights run in parallel in chunks of 1,024 with per-thread integer
/// accumulators, merged at the end: the report is identical at 1, 4 or 16 threads, <c>replay</c> reproduces any fight
/// from the full run, and <c>compare</c> runs the baseline and the variant on the SAME seeds (common random numbers), so
/// their paired difference has a far smaller interval than two independent runs would.
/// </para>
/// <para>
/// <b>Bounded.</b> A run whose fights × combatants × round cap (× 2 with a comparison) exceeds
/// <see cref="SimulationLimits.WorkBudget"/> is refused up front with what to reduce; cancellation is honoured between
/// fights, and progress is reported per chunk.
/// </para>
/// </summary>
public static class Simulator
{
    /// <summary>Validates, compiles the combatants once, runs the fights in parallel and aggregates.</summary>
    /// <param name="seed">The master seed (the host draws one from the OS when the caller gives none, and echoes it).</param>
    /// <param name="progress">Receives (fights completed, fights planned) after each chunk of 1,024.</param>
    /// <param name="maxThreads">At most this many threads (default: all); the report does not depend on it.</param>
    /// <exception cref="DndInputException">Bad input, or a run over the work budget (naming what to reduce).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static SimulationReport Run(SimulationSpec spec, ulong seed, CancellationToken ct = default,
                                       IProgress<(int Completed, int Total)>? progress = null, int? maxThreads = null)
    {
        var run = SimulationPreparation.Prepare(spec);
        CheckBudget(run);
        ct.ThrowIfCancellationRequested();

        var total = new RunTally(run.Setup);
        var planned = run.Precision is null ? run.Iterations : SimulationLimits.PrecisionBatch;
        var done = 0;
        bool? reached = null;
        while (true)
        {
            var batch = run.Precision is null ? run.Iterations : Math.Min(SimulationLimits.PrecisionBatch, SimulationLimits.MaxIterations - done);
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

            if (done >= SimulationLimits.MaxIterations)
            {
                reached = false;
                break;
            }

            planned = Math.Min(SimulationLimits.MaxIterations, done + SimulationLimits.PrecisionBatch);
        }

        string? replayLog = null;
        FightOutcome? replayed = null;
        if (run.Replay is { } replay && replay <= done)
        {
            var log = new CombatLog();
            var fight = new Fight(run.Setup);
            replayed = fight.Run(IterationSeed(seed, replay - 1), log);
            replayLog = log.Finish(ReplaySummary(fight, replayed.Value, replay));
        }

        return BuildReport(run, seed, done, total, reached, replayLog, replayed);
    }

    /// <summary>
    /// Fight i's seed (0-based): SplitMix64(master ⊕ i·φ), a pure function of the master seed and i, which the generator
    /// then expands into its state. The hash matters: handing master ⊕ i·φ straight to the generator (whose own seeding
    /// is a SplitMix64 stream) gives fights i and i+1 three of their four state words in common, and their first draws
    /// — round 1 — measurably correlated (the agreement battery caught round 1 running 2–3 standard errors low).
    /// </summary>
    internal static ulong IterationSeed(ulong master, int iteration) => new SplitMix64(master ^ ((ulong)iteration * SplitMix64.GoldenGamma)).Next();

    /// <summary>Runs one fight (0-based) on a fresh engine, optionally with a log: what replay and the tests use.</summary>
    internal static (FightOutcome Outcome, Creature[] Creatures) RunOne(SimulationSpec spec, ulong seed, int iteration, CombatLog? log = null)
    {
        var run = SimulationPreparation.Prepare(spec);
        var fight = new Fight(run.Setup);
        var outcome = fight.Run(IterationSeed(seed, iteration), log);
        return (outcome, fight.Creatures);
    }

    private static void CheckBudget(PreparedRun run)
    {
        var fights = run.Precision is null ? run.Iterations : SimulationLimits.MaxIterations;
        var work = (long)fights * run.Combatants * run.Setup.RoundCap * (run.Variant is null ? 1 : 2);
        if (work <= SimulationLimits.WorkBudget)
        {
            return;
        }

        var compare = run.Variant is null ? "" : " × 2 (compare)";
        var precisionHint = run.Precision is null ? "" : " (precision may run 100,000 fights: drop it or give iterations instead)";
        throw new DndInputException(FormattableString.Invariant(
            $"this simulation is too large: {fights:N0} fights × {run.Combatants} combatants × round cap {run.Setup.RoundCap}{compare} = {work:N0}, over the limit of {SimulationLimits.WorkBudget:N0}. Lower iterations{precisionHint}, the round cap (most fights end well before 20 rounds) or the number of combatants."));
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
        var chunks = (count + SimulationLimits.ChunkSize - 1) / SimulationLimits.ChunkSize;
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
                    var from = start + (chunk * SimulationLimits.ChunkSize);
                    var to = Math.Min(start + count, from + SimulationLimits.ChunkSize);
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

    private static SimulationReport BuildReport(PreparedRun run, ulong seed, int fights, RunTally tally, bool? reached, string? replayLog, FightOutcome? replayed)
    {
        var n = tally.Fights;
        var combatants = new List<CombatantReport>();
        foreach (var entry in run.Entries)
        {
            combatants.Add(EntryReport(run, entry, tally, n));
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
            ReplayIteration = replayLog is null ? null : run.Replay,
            ReplayLog = replayLog,
            ReplayOutcome = replayed?.Outcome,
            ReplayRounds = replayed?.Rounds,
            Compare = compare,
        };
    }

    private static CombatantReport EntryReport(PreparedRun run, PreparedEntry entry, RunTally tally, long fights)
    {
        var template = run.Setup.Templates[entry.Ids[0]];
        var n = fights * entry.Ids.Length;
        long dropped = 0, dead = 0, lost = 0, lostSq = 0, startHp = 0, dealt = 0, dealtSq = 0, dealtEff = 0, dealtEffSq = 0;
        long taken = 0, takenSq = 0, takenEff = 0, takenEffSq = 0, kills = 0, killsSq = 0, lr = 0, legendary = 0;
        var histogram = new long[entry.Ids.Max(id => tally.Creatures[id].HpLostHistogram.Length)];
        var resources = new long[template.Pc?.Resources.Length ?? 0];
        var limited = new long[template.Limited.Length];
        var pools = new long[template.PoolSizes.Length];
        foreach (var id in entry.Ids)
        {
            var c = tally.Creatures[id];
            dropped += c.Dropped;
            dead += c.Dead;
            lost += c.HpLostSum;
            lostSq += c.HpLostSquares;
            startHp += c.StartHpSum;
            dealt += c.DealtRaw;
            dealtSq += c.DealtRawSquares;
            dealtEff += c.DealtEffective;
            dealtEffSq += c.DealtEffectiveSquares;
            taken += c.TakenRaw;
            takenSq += c.TakenRawSquares;
            takenEff += c.TakenEffective;
            takenEffSq += c.TakenEffectiveSquares;
            kills += c.Kills;
            killsSq += c.KillsSquares;
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
            DroppedToZero = SimulationStatistics.Wilson(dropped, n),
            DeadAtEnd = SimulationStatistics.Wilson(dead, n),
            HpLost = SimulationStatistics.Mean(lost, lostSq, n),
            HpLostP50 = SimulationStatistics.Percentile(histogram, 0.5),
            HpLostP90 = SimulationStatistics.Percentile(histogram, 0.9),
            DamageDealt = SimulationStatistics.Mean(dealt, dealtSq, n),
            DamageDealtEffective = SimulationStatistics.Mean(dealtEff, dealtEffSq, n),
            DamageTaken = SimulationStatistics.Mean(taken, takenSq, n),
            DamageTakenEffective = SimulationStatistics.Mean(takenEff, takenEffSq, n),
            Kills = SimulationStatistics.Mean(kills, killsSq, n),
            Resources = usage,
            LegendaryResistanceSpent = template.LegendaryResistance > 0 ? Per(lr) : null,
            LegendaryActions = template.LegendaryUses > 0 ? Per(legendary) : null,
        };
    }
}
