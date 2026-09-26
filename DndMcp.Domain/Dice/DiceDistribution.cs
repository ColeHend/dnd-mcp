using System.Numerics;

namespace DndMcp.Domain.Dice;

public enum DiceOddsMethod
{
    /// <summary>Every outcome counted with integer weights: probabilities are exact fractions.</summary>
    Exact,

    /// <summary>The same algorithms in double precision: exact up to rounding (~1e-15), no fractions.</summary>
    FloatingPoint,

    /// <summary>Sampled; every probability carries a confidence interval.</summary>
    MonteCarlo,
}

/// <summary>
/// Budgets for <see cref="DiceDistribution.Compute"/>. With the defaults typical questions answer in well under a
/// second; the slowest inputs found (1000d1000!&gt;=2kh1, 100d100kh50) take about 1-2 s in Release.
/// </summary>
public sealed record DiceOddsOptions
{
    /// <summary>Work units (see <see cref="WorkMeter"/>) for the exact BigInteger path.</summary>
    public long ExactBudget { get; init; } = 1_000_000;

    /// <summary>The largest outcome-space total (in bits) the exact path carries before handing over to doubles.</summary>
    public long ExactMaxBits { get; init; } = 1024;

    public long FloatingPointBudget { get; init; } = 200_000_000;

    /// <summary>Physical die rolls the Monte Carlo fallback aims to spend; the sample count follows from it.</summary>
    public long MonteCarloRolls { get; init; } = 20_000_000;

    /// <summary>
    /// The sample count aimed for however expensive each sample is. Low on purpose: 1000d1000!&gt;=2kh1 costs ~10^5
    /// rolls per sample, so 200 samples is already ~2 s; the wide interval that results is reported, not hidden. The
    /// run still stops at 1.5x <see cref="MonteCarloRolls"/> (keeping at least
    /// <see cref="DiceDistribution.MonteCarloSampleFloor"/> samples) if the pilot underestimated the cost.
    /// </summary>
    public int MonteCarloMinSamples { get; init; } = 200;

    public int MonteCarloMaxSamples { get; init; } = 1_000_000;

    /// <summary>Fixed, so dice_odds is idempotent: the same question always gets the same estimate.</summary>
    public long MonteCarloSeed { get; init; } = 20_260_926;

    public DiceCaps Caps { get; init; } = DiceCaps.Default;

    /// <summary>Tests only: skip the exact paths.</summary>
    public bool ForceMonteCarlo { get; init; }
}

/// <summary>
/// Probabilities for a <see cref="DiceExpression"/>. Tries, in order: exact fractions (BigInteger weights), exact
/// floating point (the same algorithms in double), and a seeded Monte Carlo estimate. Each exact path stops when its
/// <see cref="WorkMeter"/> budget runs out; Monte Carlo is also the answer when no exact form exists here
/// (<see cref="NotExactlyComputableException"/>). The result says which path answered.
/// </summary>
public static class DiceDistribution
{
    /// <summary>The fewest samples an estimate is ever reported from, whatever the roll budget says.</summary>
    public const int MonteCarloSampleFloor = 20;

    public static DiceOdds Compute(DiceExpression expression, DiceOddsOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DiceOddsOptions();
        string monteCarloReason;

        if (options.ForceMonteCarlo)
        {
            monteCarloReason = "forced";
        }
        else
        {
            try
            {
                try
                {
                    var exact = new DistributionCompiler<BigInteger>(
                        new BigIntegerArithmetic(options.ExactMaxBits),
                        new WorkMeter(options.ExactBudget, cancellationToken),
                        options.Caps).Compile(expression.Root);
                    return DiceOdds.FromExact(expression, exact, ExplosionCapEffect(expression, options.Caps));
                }
                catch (WorkBudgetExceededException)
                {
                    // Too large for exact fractions; the same algorithms in double usually still fit.
                }

                var floating = new DistributionCompiler<double>(
                    DoubleArithmetic.Instance,
                    new WorkMeter(options.FloatingPointBudget, cancellationToken),
                    options.Caps).Compile(expression.Root);
                return DiceOdds.FromFloatingPoint(expression, floating, ExplosionCapEffect(expression, options.Caps));
            }
            catch (WorkBudgetExceededException)
            {
                monteCarloReason = "too large to compute exactly in reasonable time";
            }
            catch (NotExactlyComputableException ex)
            {
                monteCarloReason = ex.Message;
            }
        }

        return MonteCarlo(expression, options, monteCarloReason, cancellationToken);
    }

    /// <summary>The exact distribution with no budget, for tests and small known inputs.</summary>
    public static Pmf<BigInteger> Exact(DiceExpression expression, DiceCaps? caps = null) =>
        new DistributionCompiler<BigInteger>(new BigIntegerArithmetic(long.MaxValue), WorkMeter.Unlimited, caps ?? DiceCaps.Default)
            .Compile(expression.Root);

    /// <summary>The floating-point distribution with no budget, for tests.</summary>
    public static Pmf<double> FloatingPoint(DiceExpression expression, DiceCaps? caps = null) =>
        new DistributionCompiler<double>(DoubleArithmetic.Instance, WorkMeter.Unlimited, caps ?? DiceCaps.Default)
            .Compile(expression.Root);

    /// <summary>
    /// An upper bound on how much the explosion cap moves any probability away from the uncapped ideal: a die differs
    /// only on paths that reach the cap and would explode again, probability p^(cap+1) per die, summed over dice.
    /// 1d6! is about 1e-79; 1d6!&gt;=2 is about 1e-8, worth telling the user.
    /// </summary>
    public static double ExplosionCapEffect(DiceExpression expression, DiceCaps caps)
    {
        var bound = 0.0;
        foreach (var group in expression.Groups)
        {
            if (group.Explode is not { } explode)
            {
                continue;
            }

            var weights = PhysicalDie.Weights(group, out var total);
            var explodes = weights.Where(w => explode.Condition.Matches(w.Key)).Sum(w => w.Value);
            bound += group.Count * Math.Pow((double)explodes / total, caps.MaxExplosionsPerDie + 1);
        }

        return Math.Min(bound, 1.0);
    }

    private static DiceOdds MonteCarlo(DiceExpression expression, DiceOddsOptions options, string reason, CancellationToken cancellationToken)
    {
        // A short pilot run measures how many physical rolls one sample costs (explosions and rerolls vary wildly), so
        // the sample count fits the roll budget. Same seed as the real run, so the whole thing stays deterministic.
        var pilotBudget = new DiceRollBudget(long.MaxValue);
        var pilot = DiceEvaluator.CreateSampler(expression, new SeededDiceRoller(options.MonteCarloSeed), pilotBudget, options.Caps);
        var pilotSamples = 0;
        while (pilotSamples < 200 && pilotBudget.PhysicalRolls < options.MonteCarloRolls / 20)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pilot();
            pilotSamples++;
        }

        var rollsPerSample = Math.Max(1.0, (double)pilotBudget.PhysicalRolls / pilotSamples);
        var target = (long)Math.Clamp(options.MonteCarloRolls / rollsPerSample, options.MonteCarloMinSamples, options.MonteCarloMaxSamples);

        var budget = new DiceRollBudget(long.MaxValue);
        var sampler = DiceEvaluator.CreateSampler(expression, new SeededDiceRoller(options.MonteCarloSeed), budget, options.Caps);
        var histogram = new Dictionary<long, long>();
        long samples = 0;

        while (samples < target)
        {
            // Checked on every sample: one sample can cost 10^6 rolls, so checking every 1024th meant neither the
            // budget nor cancellation was ever consulted on exactly the expressions that need them.
            cancellationToken.ThrowIfCancellationRequested();

            // A pilot that missed the rare long chains underestimates the cost. Stop at 1.5x the budget even below
            // MonteCarloMinSamples, as long as there are MonteCarloSampleFloor samples: a wider interval, reported as
            // such, beats a call that runs for tens of seconds. Deterministic: it depends only on the seed.
            if (samples >= MonteCarloSampleFloor && budget.PhysicalRolls > options.MonteCarloRolls * 3 / 2)
            {
                break;
            }

            var value = sampler();
            histogram[value] = histogram.GetValueOrDefault(value) + 1;
            samples++;
        }

        return DiceOdds.FromSamples(expression, histogram, samples, options.MonteCarloSeed, reason, ExplosionCapEffect(expression, options.Caps));
    }
}
