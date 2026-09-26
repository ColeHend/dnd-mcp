using System.Numerics;

namespace DndMcp.Domain.Dice;

/// <summary>
/// A probability mass function over whole numbers: sorted distinct values, each with a positive weight, and the
/// <see cref="Total"/> the weights are divided by. P(value) = weight / Total.
///
/// <para>
/// Generic so the same algorithms run twice. With <see cref="BigInteger"/> weights every probability is an exact
/// fraction, which is how <c>dice_odds</c> can say "exactly 638543/1679616"; but the numbers grow with the
/// outcome space (1000d6 has 6^1000 outcomes), so large expressions use <see cref="double"/> weights kept
/// normalised (Total = 1) instead. <see cref="DiceDistribution"/> picks between them by budget.
/// </para>
/// <para>
/// Sparse by construction: <c>(2d6)*10</c> has 11 values spread over a range of 101, and a dense array would carry
/// the 90 zeros through every later convolution.
/// </para>
/// <para>
/// <see cref="Values"/> is the exact support: every reachable value and no other. Exact weights are never zero, but a
/// double weight can underflow (P(1000d6 = 1000) is 6^-1000), and such values are kept with weight 0 rather than
/// dropped — otherwise 1000d6 would report its range as 1599 to 5401, and P(≥ 1600) as a flat certainty.
/// </para>
/// </summary>
public sealed class Pmf<T>
    where T : INumber<T>
{
    private readonly long[] _values;
    private readonly T[] _weights;

    private Pmf(long[] values, T[] weights, T total)
    {
        _values = values;
        _weights = weights;
        Total = total;
    }

    public int Count => _values.Length;

    public ReadOnlySpan<long> Values => _values;

    public ReadOnlySpan<T> Weights => _weights;

    public T Total { get; }

    public long Min => _values[0];

    public long Max => _values[^1];

    public static Pmf<T> Point(long value) => new([value], [T.One], T.One);

    /// <summary>Merges duplicate values. Every value given is reachable, so a zero (underflowed) weight is kept.</summary>
    public static Pmf<T> FromPairs(IEnumerable<KeyValuePair<long, T>> pairs, T total)
    {
        var merged = new Dictionary<long, T>();
        foreach (var (value, weight) in pairs)
        {
            merged[value] = merged.TryGetValue(value, out var existing) ? existing + weight : weight;
        }

        return FromDictionary(merged, total);
    }

    internal static Pmf<T> FromDictionary(Dictionary<long, T> weights, T total)
    {
        if (weights.Count == 0)
        {
            throw new InvalidOperationException("A distribution needs at least one value.");
        }

        var values = new long[weights.Count];
        var ws = new T[weights.Count];
        var count = 0;
        foreach (var (value, weight) in weights)
        {
            values[count] = value;
            ws[count] = weight;
            count++;
        }

        Array.Sort(values, ws);
        return new Pmf<T>(values, ws, total);
    }

    /// <summary>P(value) for every value, as doubles.</summary>
    public double[] Probabilities(Func<T, T, double> ratio)
    {
        var result = new double[_weights.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ratio(_weights[i], Total);
        }

        return result;
    }

    /// <summary>The distribution of f(X). Values f maps together have their weights added.</summary>
    public Pmf<T> Map(Func<long, long> f, WorkMeter meter)
    {
        meter.Spend(_values.Length);

        var values = new long[_values.Length];
        var weights = (T[])_weights.Clone();
        var increasing = true;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = f(_values[i]);
            increasing &= i == 0 || values[i] > values[i - 1];
        }

        if (increasing)
        {
            return new Pmf<T>(values, weights, Total);
        }

        Array.Sort(values, weights);
        var write = 0;
        for (var read = 0; read < values.Length; read++)
        {
            if (write > 0 && values[write - 1] == values[read])
            {
                weights[write - 1] += weights[read];
            }
            else
            {
                values[write] = values[read];
                weights[write] = weights[read];
                write++;
            }
        }

        Array.Resize(ref values, write);
        Array.Resize(ref weights, write);
        return new Pmf<T>(values, weights, Total);
    }

    /// <summary>The distribution of X + Y for independent X ~ this and Y ~ other.</summary>
    public Pmf<T> Convolve(Pmf<T> other, WorkMeter meter)
    {
        var pairs = (long)_values.Length * other._values.Length;
        var low = Min + other.Min;
        var span = Max + other.Max - low + 1;

        // Dense accumulation when the result range is not much larger than the work: dice sums, the common case.
        // Sparse inputs spread far apart ((2d6)*1000 + 1d6) would allocate mostly zeros, so they use a dictionary.
        // The allocation is charged too, and capped: (5d1000)*10000+5d1000 once took 2 GB here in under two seconds of
        // "work".
        var dense = span <= Math.Max(4 * pairs, 1024) && span <= meter.MaxSupport;
        meter.Spend(dense ? pairs + span : pairs * WorkMeter.DictionaryCost);

        if (dense)
        {
            var accumulator = new T[span];
            if (!T.IsZero(default!))
            {
                Array.Fill(accumulator, T.Zero);
            }

            // Reachability is tracked apart from the weights: a gap in the range (never written) and an underflowed
            // double (written, rounded to 0) both read as zero, but only the second is part of the support.
            var reached = new bool[span];
            for (var i = 0; i < _values.Length; i++)
            {
                var offset = _values[i] - low;
                var weight = _weights[i];
                for (var j = 0; j < other._values.Length; j++)
                {
                    var index = offset + other._values[j];
                    accumulator[index] += weight * other._weights[j];
                    reached[index] = true;
                }
            }

            var count = 0;
            foreach (var r in reached)
            {
                count += r ? 1 : 0;
            }

            meter.CheckSupport(count);

            var values = new long[count];
            var weights = new T[count];
            var k = 0;
            for (var i = 0; i < accumulator.Length; i++)
            {
                if (reached[i])
                {
                    values[k] = low + i;
                    weights[k] = accumulator[i];
                    k++;
                }
            }

            return new Pmf<T>(values, weights, Total * other.Total);
        }

        var sums = new Dictionary<long, T>();
        for (var i = 0; i < _values.Length; i++)
        {
            for (var j = 0; j < other._values.Length; j++)
            {
                var value = _values[i] + other._values[j];
                var weight = _weights[i] * other._weights[j];
                sums[value] = sums.TryGetValue(value, out var existing) ? existing + weight : weight;
            }

            meter.CheckSupport(sums.Count);
        }

        return FromDictionary(sums, Total * other.Total);
    }

    /// <summary>The sum of <paramref name="n"/> independent copies, by repeated squaring.</summary>
    public Pmf<T> Power(int n, WorkMeter meter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);

        Pmf<T>? result = null;
        var square = this;
        while (n > 0)
        {
            if ((n & 1) == 1)
            {
                result = result is null ? square : result.Convolve(square, meter);
            }

            n >>= 1;
            if (n > 0)
            {
                square = square.Convolve(square, meter);
            }
        }

        return result ?? Point(0);
    }

    /// <summary>The summed weight of the values <paramref name="predicate"/> accepts.</summary>
    public T WeightWhere(Func<long, bool> predicate)
    {
        var sum = T.Zero;
        for (var i = 0; i < _values.Length; i++)
        {
            if (predicate(_values[i]))
            {
                sum += _weights[i];
            }
        }

        return sum;
    }
}

/// <summary>
/// Counts the work an exact computation does and stops it once a budget is spent, so an expression too large for
/// one strategy falls back to the next (exact fractions → floating point → Monte Carlo) instead of running for
/// minutes. Also where cancellation is observed.
/// </summary>
public sealed class WorkMeter
{
    /// <summary>A dictionary update costs roughly this many dense multiply-adds.</summary>
    public const long DictionaryCost = 10;

    /// <summary>
    /// The most distinct totals one distribution may hold: about 50 MB of doubles and bookkeeping. Operation counts
    /// alone do not bound memory — a sparse convolution does little work per value it creates.
    /// </summary>
    public const int DefaultMaxSupport = 2_000_000;

    private readonly CancellationToken _cancellationToken;
    private long _remaining;

    public WorkMeter(long budget, CancellationToken cancellationToken = default, int maxSupport = DefaultMaxSupport)
    {
        _remaining = budget;
        _cancellationToken = cancellationToken;
        MaxSupport = maxSupport;
    }

    /// <summary>A meter that never runs out, for tests and small known inputs.</summary>
    public static WorkMeter Unlimited => new(long.MaxValue, maxSupport: int.MaxValue);

    public int MaxSupport { get; }

    public void CheckSupport(long count)
    {
        if (count > MaxSupport)
        {
            throw new WorkBudgetExceededException();
        }
    }

    public void Spend(long operations)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _remaining -= operations;
        if (_remaining < 0)
        {
            throw new WorkBudgetExceededException();
        }
    }
}

/// <summary>Thrown by <see cref="WorkMeter"/>; caught by <see cref="DiceDistribution"/> to try the next strategy.</summary>
public sealed class WorkBudgetExceededException : Exception
{
    public WorkBudgetExceededException()
        : base("The computation exceeded its work budget.")
    {
    }
}
