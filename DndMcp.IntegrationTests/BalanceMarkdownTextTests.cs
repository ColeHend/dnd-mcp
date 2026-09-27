using DndMcp.Domain.Dpr;
using DndMcp.Formatting;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: the balance tools print a number the way a reader checks it against a table: two decimals rounded half away
/// from zero on the decimal digits (the smite golden 1701/200 prints 8.51, not the binary double's 8.50), a true minus
/// sign, and a probability that is neither 0 nor 1 never shown as 0% or 100%.
///
/// <para>
/// These call the formatter's helpers directly (the host's internals are visible to this project), since the edge values
/// cannot be steered through a build: the goldens reach only some of them, and a "100%" that is really 0.99996 appears only
/// in large kill tables.
/// </para>
/// </summary>
public sealed class BalanceMarkdownTextTests
{
    [Theory]
    [InlineData(1701.0 / 200, "8.51")] // §8.7 any_hit smite: the nearest double is 8.50499999…
    [InlineData(1377.0 / 200, "6.89")] // crit_or_last
    [InlineData(351.0 / 200, "1.76")] // crits_only: 1.755
    [InlineData(156893.0 / 8000, "19.61")] // §8.2: 19.611625
    [InlineData(6009.0 / 250, "24.04")] // §8.3: 24.036
    [InlineData(193809.0 / 8000, "24.23")] // §8.3 with hew_gets_pb: 24.226125
    [InlineData(0.0, "0.00")]
    [InlineData(0.004, "0.00")]
    [InlineData(-0.004, "0.00")]
    [InlineData(-2.345, "−2.35")]
    public void Dpr_Value_PrintsTwoDecimalsRoundedOnTheDecimalDigits(double value, string expected)
    {
        Assert.Equal(expected, BalanceMarkdownText.Dpr(value));
    }

    [Theory]
    [InlineData(767.0 / 960, "+0.80")] // §8.4: Savage Attacker 0.798958
    [InlineData(855619.0 / 1036800, "+0.83")] // with savage_attacker_on_crit_dice: 0.825250
    [InlineData(65879.0 / 24000, "+2.74")] // §8.2 GWM over no feat: 2.744958
    [InlineData(1209.0 / 250, "+4.84")] // §8.3 GWM over no feat: 4.836
    [InlineData(-1.3, "−1.30")]
    [InlineData(0.004, "0.00")]
    [InlineData(-0.004, "0.00")]
    public void SignedDpr_Difference_HasASignAndATrueMinus(double value, string expected)
    {
        Assert.Equal(expected, BalanceMarkdownText.SignedDpr(value));
    }

    [Theory]
    [InlineData(0.0, "0%")]
    [InlineData(1.0, "100%")]
    [InlineData(0.65, "65%")]
    [InlineData(0.8775, "87.75%")]
    [InlineData(3642569.0 / 3645000, "99.93%")] // §8.8: P(all four goblins die) 0.999333
    [InlineData(0.00004, "< 0.01%")]
    [InlineData(0.99996, "> 99.99%")]
    [InlineData(0.00005, "0.01%")]
    [InlineData(0.99994, "99.99%")]
    public void Percent_Probability_NeverRoundsOntoCertainty(double probability, string expected)
    {
        Assert.Equal(expected, BalanceMarkdownText.Percent(probability));
    }

    [Theory]
    [InlineData(0.0, "<1%")]
    [InlineData(0.004, "<1%")]
    [InlineData(0.9033, "90%")]
    [InlineData(0.996, ">99%")]
    [InlineData(1.0, ">99%")]
    public void WholePercent_GridCell_IsWholeAndNeverLooksLikeOffOrAlways(double probability, string expected)
    {
        Assert.Equal(expected, BalanceMarkdownText.WholePercent(probability));
    }

    [Theory]
    [InlineData(0.033, "+3.3%")]
    [InlineData(-0.0044, "−0.4%")]
    [InlineData(0.0004, "0.0%")]
    [InlineData(null, "—")]
    public void SignedPercent_RelativeDelta_IsSignedOrADashWithoutABaseline(double? fraction, string expected)
    {
        Assert.Equal(expected, BalanceMarkdownText.SignedPercent(fraction));
    }

    [Theory]
    [InlineData(2.0, "2", "2 times")]
    [InlineData(1.0, "1", "once")]
    [InlineData(0.87750000000000006, "0.8775", "0.8775 times")]
    [InlineData(1.0 / 3, "0.3333", "0.3333 times")]
    [InlineData(0.0, "0", "0 times")]
    public void RateAndTimes_PerRound_AreFourDecimalsWithoutTrailingZeros(double value, string rate, string times)
    {
        Assert.Equal(rate, BalanceMarkdownText.Rate(value));
        Assert.Equal(times, BalanceMarkdownText.Times(value));
    }

    [Fact]
    public void Formatters_NonFiniteValue_PrintADashRatherThanThrow()
    {
        // A throw would replace the whole answer with the SDK's generic error.
        Assert.Equal("—", BalanceMarkdownText.Dpr(double.NaN));
        Assert.Equal("—", BalanceMarkdownText.SignedDpr(double.PositiveInfinity));
        Assert.Equal("—", BalanceMarkdownText.SignedPercent(double.NaN));
        Assert.Equal("—", BalanceMarkdownText.Percent(double.NaN));
        Assert.Equal("—", BalanceMarkdownText.WholePercent(double.NaN));
        Assert.Equal("—", BalanceMarkdownText.Rate(double.NegativeInfinity));
    }

    [Theory]
    [InlineData(-1.3, "−1.0")]
    [InlineData(0.1, "0.080")]
    [InlineData(0.8, "0.64")]
    [InlineData(2.6, "2.1")]
    public void LevelEquivalentText_Negative_UsesATrueMinusAndKeepsTheSignificantFigures(double delta, string expected)
    {
        var slope = LevelEquivalents.ReferenceSlope(2, "test");

        Assert.Equal(expected, BalanceMarkdownText.LevelEquivalentText(LevelEquivalents.Of(delta, slope)));
    }
}
