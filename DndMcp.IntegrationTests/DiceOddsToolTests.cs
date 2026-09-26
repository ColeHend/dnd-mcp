using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>dice_odds</c> through the real client answers the question asked first — the probability for a
/// trailing comparison, the mean otherwise — with the exact fraction when there is one, says how it computed the
/// answer, labels estimates as estimates, and stays well under the result-size ceiling.
///
/// <para>
/// The golden numbers themselves are pinned in DndMcp.Tests; here they only prove the pipeline carries them to the
/// model unchanged — a formatter that rounded 638543/1679616 into "38%" or dropped the fraction would fail.
/// </para>
/// </summary>
public sealed partial class DiceOddsToolTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public DiceOddsToolTests(McpServerHarness server)
    {
        _server = server;
    }

    [GeneratedRegex(@"^\| -?\d+ \| ", RegexOptions.Multiline)]
    private static partial Regex TableRowRegex();

    [Fact]
    public async Task CallTool_8d6AtLeast30_LeadsWithTheExactProbability()
    {
        var text = await OddsAsync("8d6>=30");
        var lines = text.Split('\n');

        Assert.Equal("**P(8d6 ≥ 30) = 38.02%** (exactly 638543/1679616)", lines[0]);
        Assert.StartsWith("P(= 30) ", lines[1], StringComparison.Ordinal);
        Assert.Contains("Mean 28 · ", text, StringComparison.Ordinal);
        Assert.EndsWith("_Exact: every outcome counted, no sampling._", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Comparison_SecondLineGivesTheOtherTwoQuestionsNotTheOneAsked()
    {
        var lines = (await OddsAsync("8d6>=30")).Split('\n');

        Assert.Contains("P(= 30) ", lines[1], StringComparison.Ordinal);
        Assert.Contains("P(≤ 30) ", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("P(≥ 30)", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LongExactFraction_IsLeftOutRatherThanPrinted()
    {
        // 30d6's fractions have denominators up to 6^30 (24 digits): noise to a reader, so only the decimal is shown.
        var text = await OddsAsync("30d6>=105");

        Assert.StartsWith("**P(30d6 ≥ 105) = ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(exactly", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_4d6DropLowest_GivesExactMeanAndFullTable()
    {
        var text = await OddsAsync("4d6kh3");

        Assert.StartsWith("**4d6kh3**: mean 12.2446 (exactly 15869/1296) · ", text, StringComparison.Ordinal);
        Assert.Equal(16, TableRowRegex().Matches(text).Count);
        Assert.Contains("| 18 | 1.62% | 1.62% | 100% |", text, StringComparison.Ordinal);
        Assert.Contains("| 3 | 0.0772% | 100% | 0.0772% |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_AdvantagePlusFiveVsAc15_GivesTheHitChance()
    {
        // P(max of 2d20 >= 10) = 1 - (9/20)^2 = 319/400.
        var text = await OddsAsync("adv+5>=15");

        Assert.StartsWith("**P(adv+5 ≥ 15) = 79.75%** (exactly 319/400)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ImpossibleAndCertain_Say0And100Exactly()
    {
        Assert.StartsWith("**P(2d6 ≥ 13) = 0%**", await OddsAsync("2d6>=13"), StringComparison.Ordinal);
        Assert.StartsWith("**P(2d6 ≥ 2) = 100%**", await OddsAsync("2d6>=2"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_PossibleButTiny_IsNotRoundedToCertainty()
    {
        // 1000 dice: P(total < 1600) is ~1e-200, far below a double's reach once summed; it must not read "100%".
        var text = await OddsAsync("1000d6>=1600");

        Assert.StartsWith("**P(1000d6 ≥ 1600) > 99.9999999999%**", text, StringComparison.Ordinal);
        Assert.Contains("range 1000 to 6000", text, StringComparison.Ordinal);
        Assert.Contains("_Exact up to floating-point rounding", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_ExplodingWithKeep_IsLabelledAnEstimateWithIntervals()
    {
        var text = await OddsAsync("4d6!kh3>=15");

        Assert.Matches(@"^\*\*P\(4d6!kh3 ≥ 15\) ≈ \d+\.\d+%\*\* \(95% interval \d+\.\d+% to \d+\.\d+%\)", text);
        Assert.Contains("_Estimated from 1,000,000 simulated rolls (seed 20260926), because the expression is exploding dice with keep/drop", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EstimateThatNeverSawAnOutcome_DoesNotCallItImpossible()
    {
        // No sample of 1000d1000 reaches 555000 (≈ 5.9 SD above the mean), but it is possible: "≈ 0%", never a flat "0%".
        var text = await OddsAsync("1000d1000>=555000");

        Assert.StartsWith("**P(1000d1000 ≥ 555000) ≈ 0%** (95% interval 0% to ", text, StringComparison.Ordinal);
        Assert.Contains("P(≤ 555000) ≈ 100%", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4d6!", "range 4 to 2424")]
    [InlineData("3d6!", "range 3 to 1818")]
    public async Task CallTool_ExplodingDice_ReportTheTrueRange(string expression, string range)
    {
        // Chains are built to the explosion cap; stopping early once deeper levels were "negligible" cut the range to
        // 4-600 and printed possible totals as 0%.
        Assert.Contains(range, await OddsAsync(expression), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TotalOnlyALongChainReaches_IsPossibleNotZero()
    {
        Assert.StartsWith("**P(1d1000! ≥ 8001) < 0.0000000001%**", await OddsAsync("1d1000! >= 8001"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SameEstimateTwice_IsIdentical()
    {
        // dice_odds is annotated idempotent; the Monte Carlo fallback's fixed seed is what makes that true.
        Assert.Equal(await OddsAsync("6d6!kh3"), await OddsAsync("6d6!kh3"));
    }

    [Fact]
    public async Task CallTool_Estimate_SaysItsRangeIsObservedNotPossible()
    {
        Assert.Contains("observed range", await OddsAsync("4d6!kh3"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EstimateOfAnImpossibleTotal_IsExactlyZero()
    {
        // 4d6!kh3 keeps three separate d6 dice, so 30 is impossible by the expression's bounds, sampled or not.
        var text = await OddsAsync("4d6!kh3>=30");

        Assert.StartsWith("**P(4d6!kh3 ≥ 30) = 0%**\n", text, StringComparison.Ordinal);
        Assert.Contains("P(≤ 30) 100%", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_RareExplosions_HaveNoCapNote()
    {
        // 1d6! reaches the cap with probability 6^-101: the note would only be noise.
        Assert.DoesNotContain("Explosions stop", await OddsAsync("1d6!"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_LikelyExplosions_ReportTheCapEffect()
    {
        var text = await OddsAsync("1d6!>=2");

        Assert.EndsWith("_Explosions stop after 100 per die, as in dice_roll; that shifts these figures by at most 1e-8._", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1d60")]
    [InlineData("1d61")]
    [InlineData("1000d1000")]
    [InlineData("1000d20kl1")]
    [InlineData("10d6!!kh3>=20")]
    public async Task CallTool_AnyExpression_StaysWellUnderTheOutputCeiling(string expression)
    {
        var text = await OddsAsync(expression);

        Assert.True(text.Length < 8_000, $"dice_odds returned {text.Length} characters for {expression}.");
    }

    [Theory]
    [InlineData("1d60", 60)]
    [InlineData("1d61", 0)]
    [InlineData("8d6>=30", 0)]
    public async Task CallTool_Table_OnlyForSmallDistributionsWithoutAComparison(string expression, int rows)
    {
        Assert.Equal(rows, TableRowRegex().Matches(await OddsAsync(expression)).Count);
    }

    private async Task<string> OddsAsync(string expression) =>
        _server.SuccessText(await _server.Client.CallToolAsync("dice_odds", new Dictionary<string, object?> { ["expression"] = expression }));
}
