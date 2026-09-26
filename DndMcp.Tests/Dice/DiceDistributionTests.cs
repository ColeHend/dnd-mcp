using System.Numerics;
using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: <see cref="DiceDistribution.Compute"/> answers every valid expression — exactly when it can, in double
/// precision when exact fractions would be too large, by seeded Monte Carlo otherwise — says which, and stays
/// accurate at the sizes where naive formulas overflow or underflow (1000 dice).
/// </summary>
public sealed class DiceDistributionTests
{
    [Theory]
    [InlineData("8d6", DiceOddsMethod.Exact)]
    [InlineData("4d6kh3", DiceOddsMethod.Exact)]
    [InlineData("1d6!", DiceOddsMethod.Exact)]
    [InlineData("1000d6", DiceOddsMethod.FloatingPoint)]
    [InlineData("40d20kh20", DiceOddsMethod.FloatingPoint)]
    [InlineData("100d100kh50", DiceOddsMethod.MonteCarlo)]
    [InlineData("1000d1000", DiceOddsMethod.MonteCarlo)]
    [InlineData("4d6!kh3", DiceOddsMethod.MonteCarlo)]
    public void Compute_Expression_UsesTheExpectedMethod(string expression, DiceOddsMethod method)
    {
        Assert.Equal(method, DiceDistribution.Compute(DiceExpression.Parse(expression)).Method);
    }

    [Theory]
    [InlineData("1000d1000", "too large to compute exactly in reasonable time")]
    [InlineData("4d6!kh3", "\"4d6!kh3\" keeps or drops dice from a pool whose size depends on how many explode")]
    public void Compute_MonteCarlo_SaysWhy(string expression, string reason)
    {
        Assert.Equal(reason, DiceDistribution.Compute(DiceExpression.Parse(expression)).MonteCarloReason);
    }

    [Fact]
    public void Compute_ExplodingPoolDropLowest_KeepsAllButOneOfTheExplodedPool()
    {
        // 4d6!dl1 keeps pool − 1 ≥ 3 dice; 4d6!kh3 keeps exactly 3. Reading dl1 as "keep 3 of the 4 typed" made the two
        // identical.
        var dropOne = DiceDistribution.Compute(DiceExpression.Parse("4d6!dl1")).Mean;
        var keepThree = DiceDistribution.Compute(DiceExpression.Parse("4d6!kh3")).Mean;

        Assert.True(dropOne > keepThree + 0.3, $"4d6!dl1 mean {dropOne} vs 4d6!kh3 mean {keepThree}.");
    }

    [Theory]
    [InlineData("4d6!", 4, 2424)]
    [InlineData("3d6!", 3, 1818)]
    [InlineData("1d1000!", 1, 101_000)]
    [InlineData("2d6!p", 2, 1012)]
    public void FloatingPoint_ExplodingDice_SupportRunsToTheCap(string expression, long min, long max)
    {
        // Every chain is built to the cap: an early stop "below what a double can show" removed reachable totals.
        var pmf = DiceDistribution.FloatingPoint(DiceExpression.Parse(expression));

        Assert.Equal(min, pmf.Min);
        Assert.Equal(max, pmf.Max);
    }

    [Theory]
    [InlineData("(5d1000)*10000+5d1000")]
    [InlineData("(5d1000)*100000+3d1000")]
    public void Compute_HugeSparseSupport_FallsBackToMonteCarloInsteadOfExhaustingMemory(string expression)
    {
        // Each once built a 15-25 million value distribution (1-2 GB) inside the operation budget.
        var odds = DiceDistribution.Compute(DiceExpression.Parse(expression));

        Assert.Equal(DiceOddsMethod.MonteCarlo, odds.Method);
    }

    [Theory]
    [InlineData(DiceComparison.GreaterOrEqual, 5, 3, 7, true, false)]
    [InlineData(DiceComparison.GreaterOrEqual, 3, 3, 7, true, true)]
    [InlineData(DiceComparison.GreaterOrEqual, 8, 3, 7, false, false)]
    [InlineData(DiceComparison.Equal, 3, 3, 3, true, true)]
    [InlineData(DiceComparison.Equal, 4, 3, 7, true, false)]
    [InlineData(DiceComparison.Less, 3, 3, 7, false, false)]
    [InlineData(DiceComparison.Less, 8, 3, 7, true, true)]
    [InlineData(DiceComparison.LessOrEqual, 3, 3, 7, true, false)]
    [InlineData(DiceComparison.Greater, 7, 3, 7, false, false)]
    [InlineData(DiceComparison.Greater, 2, 3, 7, true, true)]
    public void Condition_OverInterval_KnowsWhetherAnyOrEveryValueMatches(DiceComparison comparison, long target, long low, long high, bool any, bool all)
    {
        var condition = new DiceCondition(comparison, target);

        Assert.Equal(any, condition.MatchesAnyIn(low, high));
        Assert.Equal(all, condition.MatchesAllIn(low, high));
    }

    [Fact]
    public void Compute_FloatingPoint_HasNoFractions()
    {
        var odds = DiceDistribution.Compute(DiceExpression.Parse("1000d6"));

        Assert.Null(odds.ExactMean);
        Assert.Null(odds.ExactProbability(new DiceCondition(DiceComparison.GreaterOrEqual, 3500)));
        Assert.Equal(3500, odds.Mean, 1e-9);
    }

    [Fact]
    public void Compute_FloatingPointKeepDp_MatchesExactFractions()
    {
        var expression = DiceExpression.Parse("12d10kh5");
        var exact = DiceDistribution.Exact(expression);
        var floating = DiceDistribution.FloatingPoint(expression);

        Assert.Equal(exact.Values.ToArray(), floating.Values.ToArray());
        for (var i = 0; i < exact.Count; i++)
        {
            Assert.Equal(Fraction.ToDouble(exact.Weights[i], exact.Total), floating.Weights[i], 1e-14);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(1)]
    public void FloatingPoint_HundredD6KeepHighestOne_MatchesClosedForm(int value)
    {
        // P(max = v) = (v/6)^100 − ((v−1)/6)^100, which the unconditional DP form overflows computing.
        var pmf = DiceDistribution.FloatingPoint(DiceExpression.Parse("100d6kh1"));
        var expected = Math.Pow(value / 6.0, 100) - Math.Pow((value - 1) / 6.0, 100);

        var index = pmf.Values.IndexOf(value);
        var actual = index < 0 ? 0.0 : pmf.Weights[index] / pmf.Total;
        Assert.Equal(expected, actual, Math.Max(expected * 1e-10, 1e-300));
    }

    [Fact]
    public void FloatingPoint_ThousandD20KeepLowest_TinyTailStaysRelativelyAccurate()
    {
        // P(min = 2) = (19/20)^1000 − (18/20)^1000 ≈ 5.29e-23: a probability that only survives if every intermediate
        // weight stays a probability (no huge binomial times a tiny power).
        var pmf = DiceDistribution.FloatingPoint(DiceExpression.Parse("1000d20kl1"));
        var expected = Math.Pow(0.95, 1000) - Math.Pow(0.9, 1000);

        var index = pmf.Values.IndexOf(2);
        Assert.Equal(expected, pmf.Weights[index] / pmf.Total, expected * 1e-9);
        Assert.Equal(1.0, pmf.Weights.ToArray().Sum() / pmf.Total, 1e-12);
    }

    [Fact]
    public void MonteCarlo_ForcedOnKnownExpression_BracketsTheExactAnswers()
    {
        var expression = DiceExpression.Parse("4d6kh3");
        var odds = DiceDistribution.Compute(expression, new DiceOddsOptions { ForceMonteCarlo = true });

        Assert.Equal(DiceOddsMethod.MonteCarlo, odds.Method);
        Assert.Equal(1_000_000, odds.Samples);
        Assert.InRange(15869.0 / 1296, odds.Mean - odds.MeanHalfWidth!.Value, odds.Mean + odds.MeanHalfWidth.Value);

        var (low, high) = odds.ProbabilityInterval(new DiceCondition(DiceComparison.GreaterOrEqual, 15))!.Value;
        Assert.InRange(300.0 / 1296, low, high);
        Assert.True(high - low < 0.002, $"Interval [{low}, {high}] is wider than 1M samples should give.");
    }

    [Fact]
    public void MonteCarlo_SameExpressionTwice_GivesIdenticalEstimates()
    {
        // dice_odds is annotated idempotent: a fixed seed makes that true for estimates too.
        var first = DiceDistribution.Compute(DiceExpression.Parse("6d6!kh3"));
        var second = DiceDistribution.Compute(DiceExpression.Parse("6d6!kh3"));

        Assert.Equal(first.Values, second.Values);
        Assert.Equal(first.Probabilities, second.Probabilities);
        Assert.Equal(first.Seed, second.Seed);
    }

    [Fact]
    public void MonteCarlo_ExpensiveSamples_UseFewerSamplesWithinTheRollBudget()
    {
        var odds = DiceDistribution.Compute(DiceExpression.Parse("1000d1000"));

        // 1000 rolls per sample against a 20M-roll budget.
        Assert.Equal(20_000, odds.Samples);
        Assert.Equal(500_500, odds.Mean, 500_500 * 0.001);
    }

    [Fact]
    public void MeanHalfWidth_MonteCarlo_IsZTimesStandardDeviationOverRootN()
    {
        // A variance off by a constant factor still leaves every "truth inside the interval" test green; pin the width.
        var expression = DiceExpression.Parse("4d6kh3");
        var exactSd = DiceDistribution.Compute(expression).StandardDeviation;
        var odds = DiceDistribution.Compute(expression, new DiceOddsOptions { ForceMonteCarlo = true });

        var expected = 1.959963984540054 * exactSd / Math.Sqrt(odds.Samples);
        Assert.Equal(expected, odds.MeanHalfWidth!.Value, expected * 0.01);
    }

    [Fact]
    public void Probability_NearCertainFloatingPoint_IsTheComplementOfTheSmallSide()
    {
        // 200d10's double probabilities sum to 1 + 2e-14. Summing the large side directly would disagree with the
        // complement of the small side in the last bits; the near-certain answer must be computed from the small side.
        var odds = DiceDistribution.Compute(DiceExpression.Parse("200d10"));

        var high = odds.Probability(new DiceCondition(DiceComparison.GreaterOrEqual, 795));
        var low = odds.Probability(new DiceCondition(DiceComparison.Less, 795));

        Assert.Equal(DiceOddsMethod.FloatingPoint, odds.Method);
        Assert.Equal(1.0 - low, high);
    }

    [Fact]
    public void MonteCarlo_SamplesCostlierThanTheFloorAllows_StopsAtTheRollBudget()
    {
        // ~10^6 rolls per sample (90 rerolls x 11 explosions x 1000 dice): the 200-sample floor alone would be 2x10^8
        // rolls, the 25-second call the review found. The per-sample budget check stops it near 1.5x the roll budget.
        var odds = DiceDistribution.Compute(DiceExpression.Parse("1000d1000r<990!>=991kh1"));

        Assert.Equal(DiceOddsMethod.MonteCarlo, odds.Method);
        Assert.InRange(odds.Samples, DiceDistribution.MonteCarloSampleFloor, new DiceOddsOptions().MonteCarloMinSamples - 1);
    }

    [Fact]
    public void ProbabilityInterval_EventNeverSampled_IsWilsonNotZeroWidth()
    {
        var odds = DiceDistribution.Compute(DiceExpression.Parse("1000d1000"));

        var (low, high) = odds.ProbabilityInterval(new DiceCondition(DiceComparison.GreaterOrEqual, 999_000))!.Value;

        Assert.Equal(0, odds.Probability(new DiceCondition(DiceComparison.GreaterOrEqual, 999_000)));
        Assert.Equal(0, low);
        Assert.True(high > 0, "A normal interval collapses to [0, 0]; Wilson must not.");
    }

    [Fact]
    public void ProbabilityInterval_ExactMethod_IsNull()
    {
        Assert.Null(DiceDistribution.Compute(DiceExpression.Parse("2d6")).ProbabilityInterval(new DiceCondition(DiceComparison.Equal, 7)));
    }

    [Theory]
    [InlineData("2d6", 0.5, 7)]
    [InlineData("2d6", 0.05, 3)]
    [InlineData("2d6", 0.95, 11)]
    [InlineData("1d2", 0.5, 1)]
    [InlineData("1d20", 0.25, 5)]
    [InlineData("1d20", 1.0, 20)]
    public void Percentile_Fraction_IsSmallestTotalReachingIt(string expression, double fraction, long expected)
    {
        Assert.Equal(expected, DiceDistribution.Compute(DiceExpression.Parse(expression)).Percentile(fraction));
    }

    [Fact]
    public void StandardDeviation_2d6_IsSqrt35Over6()
    {
        Assert.Equal(Math.Sqrt(35.0 / 6), DiceDistribution.Compute(DiceExpression.Parse("2d6")).StandardDeviation, 1e-12);
    }

    [Theory]
    [InlineData("1d6!>=2", 1.0)]
    [InlineData("4d6!>=2", 4.0)]
    public void ExplosionCapEffect_LikelyExplosions_IsCountTimesPToTheCapPlusOne(string expression, double dice)
    {
        var bound = DiceDistribution.ExplosionCapEffect(DiceExpression.Parse(expression), DiceCaps.Default);

        Assert.Equal(dice * Math.Pow(5.0 / 6, 101), bound, 1e-20);
    }

    [Theory]
    [InlineData("8d6")]
    [InlineData("1d6!")]
    public void ExplosionCapEffect_NoOrRareExplosions_IsNegligible(string expression)
    {
        Assert.True(DiceDistribution.ExplosionCapEffect(DiceExpression.Parse(expression), DiceCaps.Default) < 1e-70);
    }

    [Fact]
    public void Compute_CancelledToken_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            DiceDistribution.Compute(DiceExpression.Parse("8d6"), cancellationToken: cancelled.Token));
    }

    [Theory]
    // Dense: 3d6 + 2d4 is a contiguous range.
    [InlineData("3d6", "2d4")]
    // Sparse: (2d6)*1000 + 1d6 spreads 66 values over 10,001, so the convolution takes the dictionary path.
    [InlineData("(2d6)*1000", "1d6")]
    [InlineData("(1d4)*100000", "(1d4)*3-1d8")]
    public void Convolve_DenseAndSparseCases_MatchEveryPairwiseSum(string left, string right)
    {
        var a = DiceDistribution.Exact(DiceExpression.Parse(left));
        var b = DiceDistribution.Exact(DiceExpression.Parse(right));
        var expected = new SortedDictionary<long, BigInteger>();
        for (var i = 0; i < a.Count; i++)
        {
            for (var j = 0; j < b.Count; j++)
            {
                expected[a.Values[i] + b.Values[j]] = expected.GetValueOrDefault(a.Values[i] + b.Values[j]) + (a.Weights[i] * b.Weights[j]);
            }
        }

        var sum = a.Convolve(b, WorkMeter.Unlimited);

        Assert.Equal(expected.Keys, sum.Values.ToArray());
        Assert.Equal(expected.Values, sum.Weights.ToArray());
        Assert.Equal(a.Total * b.Total, sum.Total);
    }

    [Fact]
    public void Map_CollidingValues_AddsTheirWeights()
    {
        var pmf = DiceDistribution.Exact(DiceExpression.Parse("1d6/2"));

        Assert.Equal([0L, 1, 2, 3], pmf.Values.ToArray());
        Assert.Equal([BigInteger.One, 2, 2, 1], pmf.Weights.ToArray());
    }

    [Fact]
    public void Power_Zero_IsPointMassAtZero()
    {
        var die = DiceDistribution.Exact(DiceExpression.Parse("1d6"));

        var zero = die.Power(0, WorkMeter.Unlimited);

        Assert.Equal([0L], zero.Values.ToArray());
        Assert.Equal(BigInteger.One, zero.Total);
    }

    [Theory]
    [InlineData(10, 0.3)]
    [InlineData(1000, 0.999)]
    [InlineData(1000, 1e-6)]
    [InlineData(1, 0.5)]
    public void BinomialRow_AnyShape_SumsToOneWithoutNaN(int n, double q)
    {
        var row = DoubleArithmetic.BinomialRow(n, q);

        Assert.All(row, p => Assert.True(double.IsFinite(p) && p >= 0));
        Assert.Equal(1.0, row.Sum(), 1e-12);
    }

    [Fact]
    public void BinomialRow_SmallN_MatchesExactBinomial()
    {
        var row = DoubleArithmetic.BinomialRow(5, 0.25);

        // C(5,k) 0.25^k 0.75^(5-k)
        double[] expected = [243.0 / 1024, 405.0 / 1024, 270.0 / 1024, 90.0 / 1024, 15.0 / 1024, 1.0 / 1024];
        for (var k = 0; k <= 5; k++)
        {
            Assert.Equal(expected[k], row[k], 1e-15);
        }
    }

    [Fact]
    public void Fraction_ToDouble_HandlesNumbersBeyondDoubleRange()
    {
        var huge = BigInteger.Pow(6, 500);

        Assert.Equal(1.0 / 6, Fraction.ToDouble(huge / 6, huge), 1e-16);
        Assert.Equal(0.5, Fraction.Create(huge, huge * 2).ToDouble());
    }

    [Theory]
    [InlineData(2, 4, "1/2")]
    [InlineData(-3, 9, "-1/3")]
    [InlineData(3, -9, "-1/3")]
    [InlineData(0, 7, "0")]
    [InlineData(10, 5, "2")]
    public void Fraction_Create_IsInLowestTermsWithPositiveDenominator(long numerator, long denominator, string text)
    {
        Assert.Equal(text, Fraction.Create(numerator, denominator).ToString());
    }
}
