namespace DndMcp.Domain.Simulation;

/// <summary>A proportion with its Wilson score interval (95% unless stated).</summary>
/// <param name="Count">Fights (or creature-fights) where it happened.</param>
/// <param name="Total">Fights (or creature-fights) in all.</param>
public sealed record Proportion(long Count, long Total, double Estimate, double Low, double High)
{
    /// <summary>Half the interval's width: the "±" a result prints.</summary>
    public double HalfWidth => (High - Low) / 2;
}

/// <summary>A mean with its standard error and CLT interval (mean ± z·SE, 95% unless stated).</summary>
public sealed record MeanEstimate(double Mean, double StandardError, double Low, double High, long Samples);

/// <summary>
/// The simulator's statistics (research §C): Wilson score intervals for proportions — they stay inside [0, 1] and keep
/// their coverage near 0 and 1, where the normal approximation's p ± z·√(p(1−p)/n) collapses to a zero-width interval at
/// p = 0 — and the CLT for means. Everything is computed from INTEGER sums (counts, Σx, Σx²), which is what makes a
/// report identical at any thread count: integer addition does not depend on the order the chunks finish in.
/// </summary>
public static class SimulationStatistics
{
    /// <summary>The 97.5th percentile of the standard normal: a two-sided 95% interval.</summary>
    public const double Z95 = 1.959963984540054;

    /// <summary>The 99.95th percentile: a two-sided 99.9% interval (the tests' tolerance for the death-save goldens).</summary>
    public const double Z999 = 3.290526731491926;

    /// <summary>The Wilson score interval for <paramref name="count"/> successes in <paramref name="total"/> trials.</summary>
    public static Proportion Wilson(long count, long total, double z = Z95)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(total, count);
        if (total == 0)
        {
            return new Proportion(0, 0, 0, 0, 1);
        }

        var n = (double)total;
        var p = count / n;
        var z2 = z * z;
        var denominator = 1 + (z2 / n);
        var center = (p + (z2 / (2 * n))) / denominator;
        var half = z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4 * n * n))) / denominator;
        return new Proportion(count, total, p, Math.Max(0, center - half), Math.Min(1, center + half));
    }

    /// <summary>The mean of <paramref name="n"/> samples from their sum and sum of squares, with the CLT interval.</summary>
    /// <param name="scale">Divides every sample (a per-round mean from a per-fight total).</param>
    public static MeanEstimate Mean(long sum, long sumOfSquares, long n, double scale = 1, double z = Z95) =>
        Mean((double)sum, (double)sumOfSquares, n, scale, z);

    /// <summary>As <see cref="Mean(long, long, long, double, double)"/>, for sums already in doubles (a paired difference's).</summary>
    public static MeanEstimate Mean(double sum, double sumOfSquares, long n, double scale = 1, double z = Z95)
    {
        if (n <= 0)
        {
            return new MeanEstimate(0, 0, 0, 0, 0);
        }

        var mean = sum / n;
        var variance = n > 1 ? Math.Max(0, (sumOfSquares - (sum * sum / n)) / (n - 1)) : 0;
        var se = Math.Sqrt(variance / n);
        return new MeanEstimate(mean / scale, se / scale, (mean - (z * se)) / scale, (mean + (z * se)) / scale, n);
    }

    /// <summary>
    /// The smallest value whose cumulative count reaches <paramref name="fraction"/> of the total (the p-th percentile of a
    /// histogram indexed by value). 0 for an empty histogram.
    /// </summary>
    public static long Percentile(IReadOnlyList<long> histogram, double fraction, long offset = 0)
    {
        if (!double.IsFinite(fraction) || fraction is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "A percentile is a fraction in (0, 1].");
        }

        long total = 0;
        foreach (var count in histogram)
        {
            total += count;
        }

        if (total == 0)
        {
            return offset;
        }

        var needed = Math.Ceiling((fraction * total) - 1e-9);
        long cumulative = 0;
        for (var value = 0; value < histogram.Count; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= needed)
            {
                return value + offset;
            }
        }

        return histogram.Count - 1 + offset;
    }
}
