using System.Numerics;

namespace DndMcp.Domain.Dice;

/// <summary>
/// The distribution of an expression's total, however it was obtained, with the queries <c>dice_odds</c> answers.
/// Exact results also keep their integer weights, so any probability can be given as a fraction.
/// </summary>
public sealed class DiceOdds
{
    /// <summary>z for a two-sided 95% interval.</summary>
    private const double Z95 = 1.959963984540054;

    private readonly Pmf<BigInteger>? _exact;
    private readonly long[] _values;
    private readonly double[] _probabilities;
    private readonly double[] _cumulative;

    private DiceOdds(
        DiceExpression expression,
        DiceOddsMethod method,
        long[] values,
        double[] probabilities,
        Pmf<BigInteger>? exact,
        long samples,
        long seed,
        string? monteCarloReason,
        double explosionCapEffect,
        double? sampleVariance)
    {
        Expression = expression;
        Method = method;
        _values = values;
        _probabilities = probabilities;
        _exact = exact;
        Samples = samples;
        Seed = seed;
        MonteCarloReason = monteCarloReason;
        ExplosionCapEffect = explosionCapEffect;

        _cumulative = new double[probabilities.Length];
        var running = 0.0;
        var mean = 0.0;
        for (var i = 0; i < probabilities.Length; i++)
        {
            running += probabilities[i];
            _cumulative[i] = running;
            mean += values[i] * probabilities[i];
        }

        var variance = 0.0;
        for (var i = 0; i < probabilities.Length; i++)
        {
            var d = values[i] - mean;
            variance += d * d * probabilities[i];
        }

        Mean = mean;
        StandardDeviation = Math.Sqrt(sampleVariance ?? variance);

        if (exact is not null)
        {
            var weighted = BigInteger.Zero;
            for (var i = 0; i < exact.Count; i++)
            {
                weighted += exact.Values[i] * exact.Weights[i];
            }

            ExactMean = Fraction.Create(weighted, exact.Total);
            Mean = ExactMean.Value.ToDouble();
        }
    }

    public DiceExpression Expression { get; }

    public DiceOddsMethod Method { get; }

    public IReadOnlyList<long> Values => _values;

    /// <summary>P(total = Values[i]). Sums to 1 up to rounding (exactly, for sample frequencies).</summary>
    public IReadOnlyList<double> Probabilities => _probabilities;

    public long Min => _values[0];

    public long Max => _values[^1];

    public double Mean { get; }

    public double StandardDeviation { get; }

    /// <summary>Only for <see cref="DiceOddsMethod.Exact"/>.</summary>
    public Fraction? ExactMean { get; }

    /// <summary>Monte Carlo only: how many totals were sampled, and from which seed.</summary>
    public long Samples { get; }

    public long Seed { get; }

    /// <summary>Monte Carlo only: why no exact method answered.</summary>
    public string? MonteCarloReason { get; }

    /// <summary>Upper bound on how far the explosion cap moves any probability; see <see cref="DiceDistribution.ExplosionCapEffect"/>.</summary>
    public double ExplosionCapEffect { get; }

    /// <summary>Monte Carlo only: half-width of the 95% interval on the mean (CLT).</summary>
    public double? MeanHalfWidth => Method == DiceOddsMethod.MonteCarlo ? Z95 * StandardDeviation / Math.Sqrt(Samples) : null;

    internal static DiceOdds FromExact(DiceExpression expression, Pmf<BigInteger> pmf, double explosionCapEffect) =>
        new(expression, DiceOddsMethod.Exact, pmf.Values.ToArray(), pmf.Probabilities(Fraction.ToDouble), pmf, 0, 0, null, explosionCapEffect, null);

    internal static DiceOdds FromFloatingPoint(DiceExpression expression, Pmf<double> pmf, double explosionCapEffect) =>
        new(expression, DiceOddsMethod.FloatingPoint, pmf.Values.ToArray(), pmf.Probabilities((w, t) => w / t), null, 0, 0, null, explosionCapEffect, null);

    internal static DiceOdds FromSamples(DiceExpression expression, Dictionary<long, long> histogram, long samples, long seed, string reason, double explosionCapEffect)
    {
        var values = histogram.Keys.Order().ToArray();
        var probabilities = values.Select(v => (double)histogram[v] / samples).ToArray();

        // Unbiased (n − 1) sample variance, for the interval on the mean.
        var mean = values.Zip(probabilities).Sum(p => p.First * p.Second);
        var sumSquares = values.Sum(v => histogram[v] * (v - mean) * (v - mean));
        var variance = samples > 1 ? sumSquares / (samples - 1) : 0.0;

        return new DiceOdds(expression, DiceOddsMethod.MonteCarlo, values, probabilities, null, samples, seed, reason, explosionCapEffect, variance);
    }

    /// <summary>
    /// P(total meets <paramref name="condition"/>). Exact results divide the exact weights, so P(≤ max) is exactly 1
    /// rather than a sum of doubles that rounds to 0.9999999999999998 and prints as "not quite certain". Otherwise the
    /// smaller side is summed and the larger one taken as its complement, which keeps near-certain answers accurate.
    /// </summary>
    public double Probability(DiceCondition condition)
    {
        if (_exact is not null)
        {
            return Fraction.ToDouble(_exact.WeightWhere(condition.Matches), _exact.Total);
        }

        double matching = 0, other = 0;
        int matchingCount = 0, otherCount = 0;
        for (var i = 0; i < _values.Length; i++)
        {
            if (condition.Matches(_values[i]))
            {
                matching += _probabilities[i];
                matchingCount++;
            }
            else
            {
                other += _probabilities[i];
                otherCount++;
            }
        }

        if (otherCount == 0)
        {
            return 1.0;
        }

        if (matchingCount == 0)
        {
            return 0.0;
        }

        return Math.Clamp(matching <= other ? matching : 1.0 - other, 0.0, 1.0);
    }

    /// <summary>
    /// Some possible total meets the condition. Distinguishes "impossible" from a probability too small for a double.
    /// Monte Carlo judges by the expression's static bounds, not by what happened to be sampled: a total no sample hit
    /// is still possible.
    /// </summary>
    public bool CanMeet(DiceCondition condition) => Method == DiceOddsMethod.MonteCarlo
        ? condition.MatchesAnyIn(Expression.MinValue, Expression.MaxValue)
        : _values.Any(condition.Matches);

    /// <summary>Every possible total meets the condition: the probability is exactly 1, not merely close.</summary>
    public bool MustMeet(DiceCondition condition) => Method == DiceOddsMethod.MonteCarlo
        ? condition.MatchesAllIn(Expression.MinValue, Expression.MaxValue)
        : _values.All(condition.Matches);

    /// <summary>The probability as a reduced fraction, when the method was <see cref="DiceOddsMethod.Exact"/>.</summary>
    public Fraction? ExactProbability(DiceCondition condition) =>
        _exact is null ? null : Fraction.Create(_exact.WeightWhere(condition.Matches), _exact.Total);

    /// <summary>Monte Carlo only: the Wilson 95% interval for the probability.</summary>
    /// <remarks>
    /// Wilson rather than p ± z·√(p(1−p)/n): the normal interval collapses to zero width at p = 0 or 1, claiming
    /// certainty from a sample that simply never saw the rare outcome.
    /// </remarks>
    public (double Low, double High)? ProbabilityInterval(DiceCondition condition)
    {
        if (Method != DiceOddsMethod.MonteCarlo)
        {
            return null;
        }

        var n = (double)Samples;
        var p = Probability(condition);
        var z2 = Z95 * Z95;
        var centre = (p + (z2 / (2 * n))) / (1 + (z2 / n));
        var half = Z95 / (1 + (z2 / n)) * Math.Sqrt((p * (1 - p) / n) + (z2 / (4 * n * n)));

        // At p = 0 (or 1) the lower (upper) end is exactly 0 (1) in theory; rounding leaves 1e-17, which would print
        // as "< 0.0000000001%" instead of 0%.
        var low = p <= 0 ? 0 : Math.Max(0, centre - half);
        var high = p >= 1 ? 1 : Math.Min(1, centre + half);
        return (low, high);
    }

    /// <summary>
    /// The smallest total t with P(total ≤ t) ≥ <paramref name="fraction"/>. A hair of tolerance keeps rounding in the
    /// cumulative sum from pushing an exact boundary (P(≤ 7) = 0.5 exactly) to the next value.
    /// </summary>
    public long Percentile(double fraction)
    {
        for (var i = 0; i < _cumulative.Length; i++)
        {
            if (_cumulative[i] >= fraction - 1e-12)
            {
                return _values[i];
            }
        }

        return _values[^1];
    }
}
