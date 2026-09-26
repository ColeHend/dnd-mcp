using System.Numerics;
using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: for every modifier and combination of them, the exact distribution <c>dice_odds</c> reports is exactly
/// — fraction for fraction — the distribution of what <c>dice_roll</c> does, caps included.
///
/// <para>
/// Rolling (<see cref="DiceEvaluator"/>) and the distributions (<see cref="DistributionCompiler{T}"/>) implement the
/// modifier order twice, in very different ways: one replays faces, the other builds PMFs with a keep-highest DP and
/// explosion chains. Any disagreement — clamping a compound die per face instead of on its total, rerolling
/// exploded dice differently, penetration applied to the first die — is a bug in one of them, and nothing else would
/// notice: both would still look plausible. <see cref="RollPathEnumerator"/> is the oracle; the caps are lowered so
/// every path can be visited.
/// </para>
/// </summary>
public sealed class DiceSemanticsAgreementTests
{
    private static readonly DiceCaps SmallCaps = new(MaxExplosionsPerDie: 2, MaxRerollsPerDie: 2);

    public static TheoryData<string> Expressions => new()
    {
        "3d6",
        "2d6+1d4-3",
        "1d8-1d4",
        "-1d4",
        "4d6kh3",
        "4d6dl1",
        "5d4kl2",
        "3d6dh1",
        "adv",
        "dis+5",
        "ea",
        "2d6r1",
        "2d6r<=2",
        "2d6ro<=2",
        "1d6r1",
        "3d4ro1kh2",
        "2d6!",
        "2d4!>=3",
        "2d6!!",
        "2d4!!kh1",
        "2d6!p",
        "1d4!pmin2",
        "1d6!!max8",
        "1d4!!min3",
        "2d4!!max3",
        "2d6!min2max5",
        "1d20min10+3",
        "3d6max4",
        "5d6cs>=5",
        "4d6cs>=5cf=1",
        "4d6kh2cs>=4",
        "2d4min3cs>=3",
        "3d4max2cs>=2kl2",
        "3d4!cs>=4",
        "2d4!!cs>=5",
        "(1d4-3)/2",
        "(2d4+1)*3-1d6",
        "1d6*2/3",
        "2d4[fire]+1d4[cold]",
        "1d4r1!",
        "1d6r<5!",
        "2d4ro>=3!",
    };

    [Theory]
    [MemberData(nameof(Expressions))]
    public void Exact_Expression_EqualsEnumerationOfEveryRollPath(string expression)
    {
        var oracle = RollPathEnumerator.Enumerate(expression, SmallCaps);

        var exact = DiceDistribution.Exact(DiceExpression.Parse(expression), SmallCaps);

        Assert.Equal(Describe(oracle), Describe(exact));
    }

    [Theory]
    [MemberData(nameof(Expressions))]
    public void FloatingPoint_Expression_MatchesEnumerationWithinRounding(string expression)
    {
        var oracle = RollPathEnumerator.Enumerate(expression, SmallCaps);

        var floating = DiceDistribution.FloatingPoint(DiceExpression.Parse(expression), SmallCaps);

        Assert.Equal(oracle.Keys.Order(), floating.Values.ToArray());
        for (var i = 0; i < floating.Count; i++)
        {
            Assert.Equal(oracle[floating.Values[i]].ToDouble(), floating.Weights[i] / floating.Total, 1e-13);
        }
    }

    [Theory]
    [InlineData("3d4!kh2")]
    [InlineData("2d6!pkl1")]
    [InlineData("3d4!cs>=3kh2")]
    public void MonteCarlo_ExplodingPoolWithKeep_BracketsEnumeratedTruthIn95PercentIntervals(string expression)
    {
        // Explode + keep has a random pool size and no exact form here, so dice_odds samples it. The sampler is the
        // rolling code itself; the check is that the estimate and its intervals are computed correctly around it.
        var oracle = RollPathEnumerator.Enumerate(expression, SmallCaps);
        var odds = DiceDistribution.Compute(DiceExpression.Parse(expression), new DiceOddsOptions { Caps = SmallCaps });

        Assert.Equal(DiceOddsMethod.MonteCarlo, odds.Method);
        Assert.Contains("keeps or drops", odds.MonteCarloReason, StringComparison.Ordinal);

        var trueMean = oracle.Sum(o => o.Key * o.Value.ToDouble());
        Assert.InRange(trueMean, odds.Mean - odds.MeanHalfWidth!.Value, odds.Mean + odds.MeanHalfWidth.Value);

        // One threshold near the middle of the distribution, where the interval is widest and a bug shows most.
        var median = odds.Percentile(0.5);
        var condition = new DiceCondition(DiceComparison.GreaterOrEqual, median);
        var truth = oracle.Where(o => o.Key >= median).Sum(o => o.Value.ToDouble());
        var (low, high) = odds.ProbabilityInterval(condition)!.Value;
        Assert.InRange(truth, low, high);
    }

    private static string Describe(Dictionary<long, Fraction> distribution) =>
        string.Join(", ", distribution.OrderBy(d => d.Key).Select(d => $"{d.Key}:{d.Value}"));

    private static string Describe(Pmf<BigInteger> pmf) =>
        string.Join(", ", Enumerable.Range(0, pmf.Count).Select(i => $"{pmf.Values[i]}:{Fraction.Create(pmf.Weights[i], pmf.Total)}"));
}
