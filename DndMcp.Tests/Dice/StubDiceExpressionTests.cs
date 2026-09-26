using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: the Phase 0 stub parser reads only unambiguous sums of <c>NdM</c> and whole numbers, and refuses
/// everything else with a <see cref="DndInputException"/> — never a silent misreading such as "2d6 3" becoming
/// "2d63" — within the limits of 1,000 dice, 1,000 sides, 1,000,000 per constant and 256 characters, which
/// together keep every total inside <c>int</c>.
///
/// <para>
/// THE STUB IS REPLACED WHOLESALE IN PHASE 1 by the full parser (keep/drop, rerolls, exploding dice, exact
/// distributions). These parse-rule tests are expected to be rewritten against it then. The rules worth carrying
/// over are the refusal of juxtaposed terms ("2d6 3") and the dice limits; the rest is stub detail.
/// </para>
/// </summary>
public sealed class StubDiceExpressionTests
{
    [Theory]
    [InlineData("2d6+3", "+2d6 +3")]
    [InlineData("2d6 + 3", "+2d6 +3")]
    [InlineData("2d6\t+\t3", "+2d6 +3")]
    [InlineData("  1d8  ", "+1d8")]
    [InlineData("d20", "+1d20")]
    [InlineData("-1d4", "-1d4")]
    [InlineData("+2d6", "+2d6")]
    [InlineData("1d20-1", "+1d20 -1")]
    [InlineData("2D6", "+2d6")]
    [InlineData("2d6+1d4+3", "+2d6 +1d4 +3")]
    [InlineData("5", "+5")]
    [InlineData("5-2", "+5 -2")]
    public void Parse_ValidExpression_ProducesSignedTerms(string expression, string expected)
    {
        Assert.Equal(expected, Describe(StubDiceExpression.Parse(expression)));
    }

    [Theory]
    [InlineData("2d6 3")]
    [InlineData("2 d6")]
    [InlineData("1d6 1d6")]
    [InlineData("2d6d4")]
    [InlineData("banana")]
    [InlineData("2x6")]
    [InlineData("d")]
    [InlineData("1d")]
    [InlineData("2d6+")]
    [InlineData("+")]
    [InlineData("2d6++3")]
    [InlineData("0d6")]
    [InlineData("1d0")]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_AmbiguousOrMalformed_ThrowsDndInputException(string expression)
    {
        var ex = Assert.Throws<DndInputException>(() => StubDiceExpression.Parse(expression));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Parse_JuxtaposedTerms_MessageShowsHowToJoinThem()
    {
        // Stripping all whitespace would read this as 2d63. The message is what the model uses to fix its call.
        var ex = Assert.Throws<DndInputException>(() => StubDiceExpression.Parse("2d6 3"));

        Assert.Contains("\"2d6 3\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"2d6 + 3\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Unreadable_MessageQuotesInputAndGivesAcceptedExample()
    {
        var ex = Assert.Throws<DndInputException>(() => StubDiceExpression.Parse("banana"));

        Assert.Contains("\"banana\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2d6+1d4+3", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1000d6")]
    [InlineData("1d1000")]
    [InlineData("600d6+400d4")]
    [InlineData("1000000")]
    public void Parse_AtLimit_IsAccepted(string expression)
    {
        Assert.NotEmpty(StubDiceExpression.Parse(expression));
    }

    [Theory]
    [InlineData("1001d6", "1000")]
    [InlineData("1d1001", "1000")]
    [InlineData("600d6+401d4", "1000")]
    [InlineData("99999999999d6", "1000")]
    [InlineData("1000001", "1000000")]
    public void Parse_OverLimit_ThrowsNamingTheLimit(string expression, string limit)
    {
        var ex = Assert.Throws<DndInputException>(() => StubDiceExpression.Parse(expression));

        Assert.Contains(limit, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExpressionAtLengthLimit_IsAcceptedAndSummedExactly()
    {
        var expression = "10" + string.Concat(Enumerable.Repeat("+1", 127));
        Assert.Equal(StubDiceExpression.MaxExpressionLength, expression.Length);

        Assert.Equal(137, StubDiceExpression.Roll(expression, ScriptedDiceRoller.Highest()).Total);
    }

    [Theory]
    [InlineData(StubDiceExpression.MaxExpressionLength + 1)]
    // The first length whose all-constant sum wraps int: 2,148 terms of 1,000,000.
    [InlineData(7 + (2147 * 8))]
    public void Parse_ExpressionOverLengthLimit_ThrowsNamingTheLimit(int length)
    {
        var expression = ("1000000" + string.Concat(Enumerable.Repeat("+1000000", length / 8)))[..length];

        var ex = Assert.Throws<DndInputException>(() => StubDiceExpression.Parse(expression));

        Assert.Contains(StubDiceExpression.MaxExpressionLength.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Limits_WorstCaseTotal_FitsInInt()
    {
        // Roll sums in int. The most terms an expression can hold is one per two characters ("1+1+1..."), so this is
        // the largest magnitude any accepted expression can reach; raising a limit past it brings back silent
        // wrap-around, where a roll reports a large negative total as if it were real.
        long worstCase = ((long)StubDiceExpression.MaxExpressionLength / 2 + 1) * StubDiceExpression.MaxConstant +
                         (long)StubDiceExpression.MaxDice * StubDiceExpression.MaxSides;

        Assert.True(worstCase <= int.MaxValue, $"Worst-case total {worstCase} overflows int.");
    }

    [Theory]
    [InlineData("2d6+3", 15)]
    [InlineData("1d20-1", 19)]
    [InlineData("-1d4", -4)]
    [InlineData("d20", 20)]
    [InlineData("2d6+1d4+3", 19)]
    [InlineData("5", 5)]
    public void Roll_EveryDieHighest_TotalIsMaximum(string expression, int expected)
    {
        Assert.Equal(expected, StubDiceExpression.Roll(expression, ScriptedDiceRoller.Highest()).Total);
    }

    [Theory]
    [InlineData("2d6+3", 5)]
    [InlineData("1d20-1", 0)]
    [InlineData("-1d4", -1)]
    [InlineData("2d6+1d4+3", 6)]
    public void Roll_EveryDieLowest_TotalIsMinimum(string expression, int expected)
    {
        Assert.Equal(expected, StubDiceExpression.Roll(expression, ScriptedDiceRoller.Lowest()).Total);
    }

    [Fact]
    public void Roll_MixedExpression_RollsEachDieOnceAndReportsModifierAndSource()
    {
        var roller = ScriptedDiceRoller.Highest();

        var result = StubDiceExpression.Roll("2d6+1d4-2+5", roller);

        Assert.Equal([6, 6, 4], roller.RequestedSides);
        Assert.Equal([new StubDiceExpression.DieResult(6, 6), new StubDiceExpression.DieResult(6, 6), new StubDiceExpression.DieResult(4, 4)], result.Dice);
        Assert.Equal(3, result.Modifier);
        Assert.Equal(19, result.Total);
        Assert.Equal(roller.Source, result.Source);
    }

    [Fact]
    public void Roll_ConstantsOnly_RollsNoDice()
    {
        var roller = ScriptedDiceRoller.Highest();

        var result = StubDiceExpression.Roll("5-2", roller);

        Assert.Empty(roller.RequestedSides);
        Assert.Empty(result.Dice);
        Assert.Equal(3, result.Total);
        Assert.Equal(3, result.Modifier);
    }

    private static string Describe(IEnumerable<StubDiceExpression.Term> terms) =>
        string.Join(" ", terms.Select(t =>
            (t.Sign < 0 ? "-" : "+") + (t.IsDice ? $"{t.Count}d{t.Sides}" : t.Constant.ToString(System.Globalization.CultureInfo.InvariantCulture))));
}
