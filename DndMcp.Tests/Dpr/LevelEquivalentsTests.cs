using DndMcp.Domain.Dpr;
using Xunit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: a level-equivalent is Δ ÷ the tier slope, reported to two significant figures with the digits a reader
/// sees (halves away from zero on the decimal value, trailing zeros kept), and its band is read from that reported value
/// with the scale's exact boundaries (≤ −0.25 Under, |LE| &lt; 0.25 On budget, &lt; 0.5 Creeping, &lt; 1.0 Over, else
/// Breaking) — so the number printed and its band can never disagree. Tiers and their slope levels are the contract's.
/// </summary>
public sealed class LevelEquivalentsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    [InlineData(16, 3)]
    [InlineData(17, 4)]
    [InlineData(20, 4)]
    public void Tier_IsTheTierOfPlay(int level, int tier)
    {
        Assert.Equal(tier, LevelEquivalents.Tier(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Tier_OutsideOneToTwenty_IsACallerBug(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelEquivalents.Tier(level));
    }

    [Theory]
    [InlineData(1, 1, 4)] // (D(4) − D(1)) ÷ 3
    [InlineData(2, 4, 10)] // (D(10) − D(4)) ÷ 6
    [InlineData(3, 10, 16)]
    [InlineData(4, 16, 20)] // ÷ 4
    public void SlopeLevels_AreTheContractsEdges(int tier, int from, int to)
    {
        Assert.Equal((from, to), LevelEquivalents.SlopeLevels(tier));
    }

    [Theory]
    [InlineData(1, 1.25)] // (130 − 85) ÷ 12 ÷ 3
    [InlineData(2, 1.25)] // (220 − 130) ÷ 12 ÷ 6
    [InlineData(3, 1.25)] // (310 − 220) ÷ 12 ÷ 6
    [InlineData(4, 1.875)] // (400 − 310) ÷ 12 ÷ 4
    public void ReferenceSlope_IsRpgbotsCurvePerTier(int tier, double perLevel)
    {
        var slope = LevelEquivalents.ReferenceSlope(tier, "reason");

        Assert.Equal(perLevel, slope.PerLevel, 1e-12);
        Assert.Equal(SlopeSources.RpgbotReference, slope.Source);
        Assert.Equal("reason", slope.FallbackReason);
    }

    [Theory]
    [InlineData(0.3, 0.3, "0.30")]
    [InlineData(1.32766, 1.3, "1.3")]
    [InlineData(12.34, 12, "12")]
    [InlineData(123.4, 120, "120")]
    [InlineData(0.0456, 0.046, "0.046")]
    [InlineData(-0.031, -0.031, "-0.031")]
    [InlineData(0.996, 1.0, "1.0")] // the carry makes a new leading digit
    [InlineData(9.96, 10, "10")]
    [InlineData(0.245, 0.25, "0.25")] // halves away from zero on the digits a reader sees (0.245 is 0.24499… as a double)
    [InlineData(-0.245, -0.25, "-0.25")]
    [InlineData(0.0, 0.0, "0")]
    [InlineData(2.5, 2.5, "2.5")]
    public void Significant_RoundsToTwoFiguresAsPrinted(double value, double rounded, string text)
    {
        var (actual, actualText) = LevelEquivalents.Significant(value);

        Assert.Equal(rounded, actual, 1e-15);
        Assert.Equal(text, actualText);
    }

    [Theory]
    [InlineData(-5.0, BalanceBands.Under)]
    [InlineData(-0.25, BalanceBands.Under)] // the boundary is Under
    [InlineData(-0.24, BalanceBands.OnBudget)]
    [InlineData(0.0, BalanceBands.OnBudget)]
    [InlineData(0.24, BalanceBands.OnBudget)]
    [InlineData(0.25, BalanceBands.Creeping)] // the boundary is Creeping
    [InlineData(0.49, BalanceBands.Creeping)]
    [InlineData(0.5, BalanceBands.Over)]
    [InlineData(0.99, BalanceBands.Over)]
    [InlineData(1.0, BalanceBands.Breaking)]
    [InlineData(7.0, BalanceBands.Breaking)]
    public void Bands_HaveTheScalesBoundaries(double reported, string band)
    {
        Assert.Equal(band, BalanceBands.Of(reported));
    }

    [Theory]
    [InlineData(0.2449, "0.24", BalanceBands.OnBudget)]
    [InlineData(0.2451, "0.25", BalanceBands.Creeping)] // prints 0.25, so it is Creeping, not On budget
    [InlineData(-0.2451, "-0.25", BalanceBands.Under)]
    [InlineData(0.4951, "0.50", BalanceBands.Over)]
    [InlineData(0.9951, "1.0", BalanceBands.Breaking)]
    [InlineData(0.994, "0.99", BalanceBands.Over)]
    public void Of_BandIsReadFromTheReportedValue(double delta, string text, string band)
    {
        var slope = new TierSlope(2, 5, 10, SlopeSources.Baseline, 4, 10, 10, 16, null); // exactly 1 DPR a level

        var le = LevelEquivalents.Of(delta, slope);

        Assert.Equal(delta, le.Value, 1e-15);
        Assert.Equal(text, le.Text);
        Assert.Equal(band, le.Band);
        Assert.Same(slope, le.Slope);
    }

    [Fact]
    public void Of_NonPositiveSlope_IsACallerBug()
    {
        var flat = new TierSlope(2, 5, 10, SlopeSources.Baseline, 4, 10, 10, 10, null);

        Assert.Throws<ArgumentOutOfRangeException>(() => LevelEquivalents.Of(1, flat));
        Assert.Throws<ArgumentOutOfRangeException>(() => LevelEquivalents.Of(double.NaN, LevelEquivalents.ReferenceSlope(1, "r")));
    }

    [Fact]
    public void Display_NamesEachBand()
    {
        Assert.Equal(["Under", "On budget", "Creeping", "Over", "Breaking"], BalanceBands.All.Select(BalanceBands.Display));
    }
}
