using System.Numerics;
using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: the Phase 1 golden values from PLAN.md → Verification, computed independently with exact rational
/// arithmetic during research (and re-derived by brute force in Python for this phase), come out exactly.
///
/// <para>
/// Fractions are compared as reduced fractions, not doubles, so an answer that is right to 12 decimal places but
/// wrong in principle (a keep DP that loses a tie case, say) still fails.
/// </para>
/// </summary>
public sealed class DiceGoldenValueTests
{
    [Theory]
    [InlineData("4d6kh3", "15869/1296")]
    [InlineData("4d6dl1", "15869/1296")]
    [InlineData("2d20kh1", "553/40")]
    [InlineData("adv", "553/40")]
    [InlineData("3d20kh1", "1239/80")]
    [InlineData("ea", "1239/80")]
    [InlineData("2d20kl1", "287/40")]
    [InlineData("8d6", "28")]
    [InlineData("1d6ro<=2", "25/6")]
    public void ExactMean_GoldenExpression_IsTheExactFraction(string expression, string mean)
    {
        var odds = DiceDistribution.Compute(DiceExpression.Parse(expression));

        Assert.Equal(DiceOddsMethod.Exact, odds.Method);
        Assert.Equal(mean, odds.ExactMean.ToString());
    }

    [Theory]
    [InlineData("4d6kh3", DiceComparison.Equal, 18, "7/432")]
    [InlineData("4d6kh3", DiceComparison.Equal, 3, "1/1296")]
    [InlineData("8d6", DiceComparison.GreaterOrEqual, 30, "638543/1679616")]
    [InlineData("1d20", DiceComparison.GreaterOrEqual, 15, "3/10")]
    [InlineData("adv", DiceComparison.Equal, 20, "39/400")]
    public void ExactProbability_GoldenQuery_IsTheExactFraction(string expression, DiceComparison comparison, long target, string fraction)
    {
        var odds = DiceDistribution.Compute(DiceExpression.Parse(expression));

        Assert.Equal(fraction, odds.ExactProbability(new DiceCondition(comparison, target)).ToString());
    }

    [Fact]
    public void Probability_4d6kh3AtLeast15_Is0Point2315()
    {
        var odds = DiceDistribution.Compute(DiceExpression.Parse("4d6kh3"));

        Assert.Equal(300.0 / 1296, odds.Probability(new DiceCondition(DiceComparison.GreaterOrEqual, 15)), 1e-15);
    }

    [Fact]
    public void Query_8d6AtLeast30_TrailingComparisonIsParsedAsTheQuestion()
    {
        var expression = DiceExpression.Parse("8d6>=30");
        var odds = DiceDistribution.Compute(expression);

        Assert.Equal(new DiceCondition(DiceComparison.GreaterOrEqual, 30), expression.Comparison);
        Assert.Equal("638543/1679616", odds.ExactProbability(expression.Comparison!.Value).ToString());
        Assert.Equal(638543.0 / 1679616, odds.Probability(expression.Comparison.Value), 1e-15);
    }

    [Theory]
    // Exploding d6: the uncapped mean is 4.2; the cap of 100 explosions is 1e-78 away from it.
    [InlineData(100, 4.2, 1e-12)]
    [InlineData(1, 49.0 / 12, 1e-15)]
    [InlineData(2, 301.0 / 72, 1e-15)]
    [InlineData(5, 65317.0 / 15552, 1e-15)]
    public void Mean_ExplodingD6AtDepthCap_MatchesClosedForm(int cap, double mean, double tolerance)
    {
        var caps = new DiceCaps(cap, DiceLimits.MaxRerollsPerDie);

        var exact = DiceDistribution.Exact(DiceExpression.Parse("1d6!"), caps);
        var floating = DiceDistribution.FloatingPoint(DiceExpression.Parse("1d6!"), caps);

        Assert.Equal(mean, MeanOf(exact), tolerance);
        Assert.Equal(mean, MeanOf(floating), tolerance);
    }

    [Fact]
    public void ExactMean_ExplodingD6CapOne_Is49Over12()
    {
        var exact = DiceDistribution.Exact(DiceExpression.Parse("1d6!"), new DiceCaps(1, DiceLimits.MaxRerollsPerDie));

        var weighted = BigInteger.Zero;
        for (var i = 0; i < exact.Count; i++)
        {
            weighted += exact.Values[i] * exact.Weights[i];
        }

        Assert.Equal("49/12", Fraction.Create(weighted, exact.Total).ToString());
    }

    public static TheoryData<string> SumToOneExpressions => new()
    {
        "1d20", "8d6", "4d6kh3", "adv+5", "ea", "10d10cs>=8", "1d6!", "3d6!!kh2", "1d6!p", "2d6ro<=2",
        "1d20min10+7", "1000d6", "100d100", "20d20kh10", "40d6kl3", "(2d6)*10+1d6", "1d6!>=2", "1d100!",
        "10d10!cs>=8cf=1", "(1d4-3)/2",
    };

    [Theory]
    [MemberData(nameof(SumToOneExpressions))]
    public void Probabilities_AnyExpression_SumToOneWithin1e12(string expression)
    {
        var floating = DiceDistribution.FloatingPoint(DiceExpression.Parse(expression));
        var odds = DiceDistribution.Compute(DiceExpression.Parse(expression));

        Assert.Equal(1.0, floating.Weights.ToArray().Sum() / floating.Total, 1e-12);
        Assert.Equal(1.0, odds.Probabilities.Sum(), 1e-12);
    }

    private static double MeanOf(Pmf<BigInteger> pmf)
    {
        var weighted = BigInteger.Zero;
        for (var i = 0; i < pmf.Count; i++)
        {
            weighted += pmf.Values[i] * pmf.Weights[i];
        }

        return Fraction.ToDouble(weighted, pmf.Total);
    }

    private static double MeanOf(Pmf<double> pmf)
    {
        var mean = 0.0;
        for (var i = 0; i < pmf.Count; i++)
        {
            mean += pmf.Values[i] * pmf.Weights[i] / pmf.Total;
        }

        return mean;
    }
}
