using System.Numerics;

namespace DndMcp.Domain.Dice;

/// <summary>
/// What differs between exact (<see cref="BigInteger"/>) and floating-point (<see cref="double"/>) distributions.
/// Everything else in <see cref="DistributionCompiler{T}"/> is shared, so the two cannot drift apart.
/// </summary>
internal interface IPmfArithmetic<T>
    where T : INumber<T>
{
    /// <summary>A single die from integer weights over an integer total (normalised for double).</summary>
    Pmf<T> FromIntegerWeights(IReadOnlyList<KeyValuePair<long, long>> weights, long total);

    /// <summary>Throws <see cref="WorkBudgetExceededException"/> when the numbers themselves grow too large.</summary>
    Pmf<T> Check(Pmf<T> pmf);

    double Ratio(T weight, T total);

    IKeepCoefficients<T> KeepCoefficients(Pmf<T> die, int[] order, int dice);
}

/// <summary>
/// The transition weights of the keep-highest dynamic programme (see <see cref="KeepDistribution"/>). At
/// <c>step</c> the programme assigns the value <c>order[step]</c> to some of the <c>remaining</c> dice.
/// </summary>
internal interface IKeepCoefficients<T>
{
    /// <summary>Exactly <paramref name="count"/> of the remaining dice show this step's value.</summary>
    T Exactly(int step, int remaining, int count);

    /// <summary>At least <paramref name="need"/> of the remaining dice show it: the kept set is complete.</summary>
    T AtLeast(int step, int remaining, int need);

    /// <summary>The <see cref="Pmf{T}.Total"/> of the resulting distribution.</summary>
    T ResultTotal { get; }
}

/// <summary>
/// Exact arithmetic: weights are outcome counts, totals are products of die sizes. Refuses (so the caller falls back
/// to floating point) once a total passes <see cref="_maxBits"/> bits, because every later operation multiplies
/// numbers that size.
/// </summary>
internal sealed class BigIntegerArithmetic : IPmfArithmetic<BigInteger>
{
    private readonly long _maxBits;

    public BigIntegerArithmetic(long maxBits)
    {
        _maxBits = maxBits;
    }

    public Pmf<BigInteger> FromIntegerWeights(IReadOnlyList<KeyValuePair<long, long>> weights, long total) =>
        Pmf<BigInteger>.FromPairs(weights.Select(w => KeyValuePair.Create(w.Key, (BigInteger)w.Value)), total);

    public Pmf<BigInteger> Check(Pmf<BigInteger> pmf)
    {
        if (pmf.Total.GetBitLength() > _maxBits)
        {
            throw new WorkBudgetExceededException();
        }

        return pmf;
    }

    public double Ratio(BigInteger weight, BigInteger total) => Fraction.ToDouble(weight, total);

    public IKeepCoefficients<BigInteger> KeepCoefficients(Pmf<BigInteger> die, int[] order, int dice) =>
        new BigIntegerKeepCoefficients(die, order, dice);

    /// <summary>
    /// Unconditional counts: Exactly = C(r, c)·w^c, leaving the other r − c dice to later (lower) steps; AtLeast sums
    /// C(r, c)·w^c·L^(r−c) over c ≥ need, L being the weight of every value after this step. The result total is
    /// W^n, the count of all outcomes.
    /// </summary>
    private sealed class BigIntegerKeepCoefficients : IKeepCoefficients<BigInteger>
    {
        private readonly BigInteger[] _weight;
        private readonly BigInteger[] _after;
        private readonly Dictionary<int, BigInteger[]> _binomials = [];
        private readonly Dictionary<int, List<BigInteger>> _weightPowers = [];
        private readonly Dictionary<int, List<BigInteger>> _afterPowers = [];
        private readonly Dictionary<(int, int, int), BigInteger> _atLeast = [];

        public BigIntegerKeepCoefficients(Pmf<BigInteger> die, int[] order, int dice)
        {
            _weight = order.Select(i => die.Weights[i]).ToArray();
            _after = new BigInteger[order.Length];
            var sum = BigInteger.Zero;
            for (var step = order.Length - 1; step >= 0; step--)
            {
                _after[step] = sum;
                sum += _weight[step];
            }

            ResultTotal = BigInteger.Pow(die.Total, dice);
        }

        public BigInteger ResultTotal { get; }

        public BigInteger Exactly(int step, int remaining, int count) =>
            Binomials(remaining)[count] * Power(_weightPowers, _weight, step, count);

        public BigInteger AtLeast(int step, int remaining, int need)
        {
            if (_atLeast.TryGetValue((step, remaining, need), out var cached))
            {
                return cached;
            }

            var sum = BigInteger.Zero;
            var row = Binomials(remaining);
            for (var c = need; c <= remaining; c++)
            {
                sum += row[c] * Power(_weightPowers, _weight, step, c) * Power(_afterPowers, _after, step, remaining - c);
            }

            _atLeast[(step, remaining, need)] = sum;
            return sum;
        }

        /// <summary>bases[step]^exponent, from a per-step table grown by one multiplication at a time.</summary>
        private static BigInteger Power(Dictionary<int, List<BigInteger>> tables, BigInteger[] bases, int step, int exponent)
        {
            if (!tables.TryGetValue(step, out var powers))
            {
                powers = [BigInteger.One];
                tables[step] = powers;
            }

            while (powers.Count <= exponent)
            {
                powers.Add(powers[^1] * bases[step]);
            }

            return powers[exponent];
        }

        private BigInteger[] Binomials(int n)
        {
            if (!_binomials.TryGetValue(n, out var row))
            {
                row = new BigInteger[n + 1];
                row[0] = BigInteger.One;
                for (var k = 1; k <= n; k++)
                {
                    row[k] = row[k - 1] * (n - k + 1) / k;
                }

                _binomials[n] = row;
            }

            return row;
        }
    }
}

/// <summary>
/// Floating point, kept normalised (Total = 1) so 1000d6's 6^1000 outcomes never overflow.
/// </summary>
internal sealed class DoubleArithmetic : IPmfArithmetic<double>
{
    public static DoubleArithmetic Instance { get; } = new();

    public Pmf<double> FromIntegerWeights(IReadOnlyList<KeyValuePair<long, long>> weights, long total) =>
        Pmf<double>.FromPairs(weights.Select(w => KeyValuePair.Create(w.Key, (double)w.Value / total)), 1.0);

    public Pmf<double> Check(Pmf<double> pmf) => pmf;

    public double Ratio(double weight, double total) => weight / total;

    public IKeepCoefficients<double> KeepCoefficients(Pmf<double> die, int[] order, int dice) => new DoubleKeepCoefficients(die, order);

    /// <summary>
    /// Conditional probabilities, so every weight stays a probability: given the r remaining dice all show this
    /// step's value or a later one, each shows this value with q = p / (mass from here on), and the count is
    /// Binomial(r, q). The unconditional form multiplies C(1000, 500)-sized coefficients by p^c, which overflows or
    /// underflows a double long before 1000 dice.
    /// </summary>
    private sealed class DoubleKeepCoefficients : IKeepCoefficients<double>
    {
        private readonly double[] _q;
        private readonly Dictionary<(int, int), (double[] Row, double[] Tail)> _rows = [];

        public DoubleKeepCoefficients(Pmf<double> die, int[] order)
        {
            _q = new double[order.Length];
            var fromHere = 0.0;
            for (var step = order.Length - 1; step >= 0; step--)
            {
                var p = die.Weights[order[step]] / die.Total;
                fromHere += p;
                _q[step] = step == order.Length - 1 ? 1.0 : Math.Min(1.0, p / fromHere);
            }
        }

        public double ResultTotal => 1.0;

        public double Exactly(int step, int remaining, int count) => Row(step, remaining).Row[count];

        public double AtLeast(int step, int remaining, int need) => Row(step, remaining).Tail[need];

        private (double[] Row, double[] Tail) Row(int step, int remaining)
        {
            if (!_rows.TryGetValue((step, remaining), out var entry))
            {
                var row = BinomialRow(remaining, _q[step]);
                var tail = new double[remaining + 2];
                for (var c = remaining; c >= 0; c--)
                {
                    tail[c] = tail[c + 1] + row[c];
                }

                entry = (row, tail);
                _rows[(step, remaining)] = entry;
            }

            return entry;
        }
    }

    /// <summary>
    /// Binomial(n, q) probabilities, built outward from the mode (where the ratio recurrences never overflow, since
    /// every other term is smaller) and then normalised. Far tails underflow to 0, which is their true size in double.
    /// </summary>
    internal static double[] BinomialRow(int n, double q)
    {
        var row = new double[n + 1];
        if (q >= 1.0)
        {
            row[n] = 1.0;
            return row;
        }

        if (q <= 0.0)
        {
            row[0] = 1.0;
            return row;
        }

        var mode = Math.Clamp((int)Math.Floor((n + 1) * q), 0, n);
        var odds = q / (1.0 - q);
        row[mode] = 1.0;
        for (var c = mode + 1; c <= n; c++)
        {
            row[c] = row[c - 1] * (n - c + 1) / c * odds;
        }

        for (var c = mode - 1; c >= 0; c--)
        {
            row[c] = row[c + 1] * (c + 1) / (n - c) / odds;
        }

        var sum = 0.0;
        foreach (var p in row)
        {
            sum += p;
        }

        for (var c = 0; c <= n; c++)
        {
            row[c] /= sum;
        }

        return row;
    }
}
