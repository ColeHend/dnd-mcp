using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// What <see cref="DummyDpr.Run"/> measured: the build's mean damage per round over the fight, round 1's mean, their
/// standard errors (from per-fight totals: the fights are independent), and round 1's damage histogram.
/// </summary>
public sealed record DummyDprResult(
    int Iterations,
    int Rounds,
    double MeanPerRound,
    double StandardError,
    double Round1Mean,
    double Round1StandardError,
    IReadOnlyDictionary<long, long> Round1Histogram)
{
    /// <summary>The smallest round-1 total whose cumulative share reaches <paramref name="fraction"/> (as the closed form's percentiles).</summary>
    public long Round1Percentile(double fraction)
    {
        var total = Round1Histogram.Values.Sum();
        var needed = Math.Ceiling((fraction * total) - 1e-9);
        long cumulative = 0;
        foreach (var (damage, count) in Round1Histogram.OrderBy(p => p.Key))
        {
            cumulative += count;
            if (cumulative >= needed)
            {
                return damage;
            }
        }

        return Round1Histogram.Keys.DefaultIfEmpty(0).Max();
    }
}

/// <summary>
/// The agreement harness (contract §5.2, §5.8 test 1): the build alone, turn after turn, against infinite-HP dummies
/// with a <see cref="ResolvedTarget"/>'s AC, saves, damage adjustments, Magic Resistance, Evasion, starting condition,
/// cover and save dice — through the SAME simulator code that runs real fights — so its mean damage per round can be
/// held against the closed form's exact figure (<c>DprEngine</c>) within a few standard errors.
///
/// <para>
/// <b>Set-up.</b> The build acts first each round, then the dummies take (empty) turns — which is when conditions with
/// "start/end of the source's turn" durations tick, a toppled dummy stands, and the build's reaction attack happens (at
/// the end of the first dummy's turn). A save effect catching n creatures meets n identical dummies (the closed form sums
/// its damage over n targets); Cleave's second creature is one more dummy WITHOUT the starting condition, present on a
/// turn with <see cref="ResolvedTarget.SecondTargetRate"/>, drawn at the first eligible hit as the closed form does. The
/// starting condition never ends (the closed form's target has it every turn). Legendary Resistance is spent only
/// against conditions.
/// </para>
/// <para>
/// <b>Where it may legitimately differ from the closed form</b>: an imposed condition lasts its real duration (2014
/// Stunning Strike carries into the build's next turn, the closed form resets it every turn), the optimal policy is a
/// myopic approximation, and power attack auto is chosen from estimates. The agreement tests compare only where the rules
/// coincide, and check the direction where they do not.
/// </para>
/// </summary>
public static class DummyDpr
{
    /// <summary>Runs <paramref name="iterations"/> fights of <paramref name="rounds"/> rounds.</summary>
    /// <param name="maxThreads">At most this many threads (default: all); results do not depend on it.</param>
    public static DummyDprResult Run(ResolvedBuild build, ResolvedTarget target, int rounds, int iterations, ulong seed, int? maxThreads = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(rounds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        var setup = Setup(build, target, rounds);
        var gate = new object();
        long total = 0, totalSquares = 0, first = 0, firstSquares = 0;
        var histogram = new Dictionary<long, long>();
        var chunks = (iterations + SimulationLimits.ChunkSize - 1) / SimulationLimits.ChunkSize;
        Parallel.For(
            0,
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = maxThreads is > 0 ? maxThreads.Value : -1 },
            () => (Fight: new Fight(setup) { RoundDealt = new long[rounds] }, Sums: new long[4], Histogram: new Dictionary<long, long>()),
            (chunk, _, local) =>
            {
                var from = chunk * SimulationLimits.ChunkSize;
                var to = Math.Min(iterations, from + SimulationLimits.ChunkSize);
                for (var i = from; i < to; i++)
                {
                    local.Fight.Run(Simulator.IterationSeed(seed, i));
                    var dealt = local.Fight.RoundDealt!;
                    var all = dealt[rounds - 1];
                    var one = dealt[0];
                    local.Sums[0] += all;
                    local.Sums[1] += all * all;
                    local.Sums[2] += one;
                    local.Sums[3] += one * one;
                    local.Histogram[one] = local.Histogram.GetValueOrDefault(one) + 1;
                }

                return local;
            },
            local =>
            {
                lock (gate)
                {
                    total += local.Sums[0];
                    totalSquares += local.Sums[1];
                    first += local.Sums[2];
                    firstSquares += local.Sums[3];
                    foreach (var (damage, count) in local.Histogram)
                    {
                        histogram[damage] = histogram.GetValueOrDefault(damage) + count;
                    }
                }
            });

        var perRound = SimulationStatistics.Mean(total, totalSquares, iterations, rounds);
        var round1 = SimulationStatistics.Mean(first, firstSquares, iterations);
        return new DummyDprResult(iterations, rounds, perRound.Mean, perRound.StandardError, round1.Mean, round1.StandardError, histogram);
    }

    /// <summary>The harness's fight: the build (id 0), the target's dummies (ids 1..n) and Cleave's second creature.</summary>
    internal static FightSetup Setup(ResolvedBuild build, ResolvedTarget target, int rounds)
    {
        var templates = new List<CombatantTemplate>
        {
            CombatantCompiler.FromBuild(build, new CombatantSpec { Hp = 1_000_000, Ac = 40, Position = SimulationValues.Positions.Front }, 0, 0, 0, build.Name, pcLike: true),
        };
        var main = Math.Max(1, build.SaveEffects.Select(s => s.Targets).DefaultIfEmpty(1).Max());
        for (var i = 0; i < main; i++)
        {
            templates.Add(Dummy(target, templates.Count, $"Target {i + 1}", withCondition: true, build.Edition));
        }

        var cleave = -1;
        if (build.Attacks.Any(a => a.Mastery == V.Masteries.Cleave && a.IsMelee))
        {
            cleave = templates.Count;
            templates.Add(Dummy(target, templates.Count, "Second creature", withCondition: false, build.Edition));
        }

        return new FightSetup
        {
            Templates = templates.ToArray(),
            Entries = templates.Select(t => new[] { t.Id }).ToArray(),
            Edition = build.Edition,
            Surprise = SimulationValues.Surprise.None,
            RoundCap = rounds,
            PartyTargeting = SimulationValues.Targeting.FocusFire,
            EnemyTargeting = SimulationValues.Targeting.Spread,
            LegendaryResistance = SimulationValues.LegendaryResistance.Conditions,
            Healing = SimulationValues.Healing.Never,
            FinishDowned = false,
            PcsWinTies = false,
            Dummy = true,
            SecondTargetRate = target.SecondTargetRate,
            CleaveDummy = cleave,
            MainDummies = main,
        };
    }

    private static CombatantTemplate Dummy(ResolvedTarget target, int id, string label, bool withCondition, string edition)
    {
        var permanent = 0;
        var dodging = false;
        if (withCondition && target.Condition is { } condition)
        {
            if (condition == V.Conditions.Dodging)
            {
                dodging = true;
            }
            else
            {
                permanent |= 1 << Cond.Of(condition);
                if (condition == V.Conditions.Unconscious)
                {
                    permanent |= 1 << Cond.Prone;
                }
            }
        }

        static int[] Table(IReadOnlyList<string> types)
        {
            var table = new int[DamageTypes.Count];
            foreach (var type in types)
            {
                var index = DamageTypes.Of(type);
                if (index != DamageTypes.Typeless)
                {
                    table[index] = Qualifier.Always;
                }
            }

            return table;
        }

        return new CombatantTemplate
        {
            Id = id,
            Entry = id,
            Side = 1,
            Label = label,
            Edition = edition,
            PcLike = false,
            AverageHp = 1_000_000,
            ArmorClass = target.ArmorClass,
            Saves = V.Abilities.All.Select(target.SaveBonus).ToArray(),
            InitiativeBonus = 0,
            Front = true,
            Resist = Table(target.Resistances),
            Immune = Table(target.Immunities),
            Vulnerable = Table(target.Vulnerabilities),
            MagicResistance = target.MagicResistance,
            Evasion = target.Evasion,
            LegendaryResistance = target.LegendaryResistance,
            InfiniteHp = true,
            Inert = true,
            PermanentConditions = permanent,
            PermanentDodging = dodging,
            CoverBonus = target.CoverBonus,
            SaveDice = target.SaveDice?.Dice.ToArray() ?? [],
        };
    }
}
