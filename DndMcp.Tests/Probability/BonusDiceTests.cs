using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Probability;
using Xunit;

namespace DndMcp.Tests.Probability;

/// <summary>
/// Invariant: <see cref="BonusDice.Parse"/> turns plain dice and whole numbers into their exact, normalised distribution
/// with every sign kept (dndMath.ts bug 5 read "1d8-1d4" as +1d4), and refuses every other construct by name — including
/// the ones the dice grammar would quietly normalise away ("1d4kh1" is a plain 1d4 there) — with a message that starts
/// with the field, quotes the text and shows an example.
/// </summary>
public sealed class BonusDiceTests
{
    private const string Where = "modifiers item 3 (to_hit): dice";

    [Theory]
    [InlineData("1d4", 1, 4)]
    [InlineData("-1d4", -4, -1)]
    [InlineData("1d4+1d6", 2, 10)]
    [InlineData("-1d4+1d4", -3, 3)]
    [InlineData("1d4-1d4", -3, 3)]
    [InlineData("1d8-1d4", -3, 7)]
    [InlineData("1d4+1", 2, 5)]
    [InlineData("1d4 - 1", 0, 3)]
    [InlineData(" 1D4 ", 1, 4)]
    [InlineData("d4", 1, 4)]
    [InlineData("-(1d4+1)", -5, -2)]
    [InlineData("2", 2, 2)]
    [InlineData("1d%", 1, 100)]
    [InlineData("50d2", 50, 100)]
    public void Parse_PlainDice_HasTheWholeRangeAsItsSupport(string text, long min, long max)
    {
        var pmf = BonusDice.Parse(text, Where);

        Assert.Equal(Enumerable.Range((int)min, (int)(max - min + 1)).Select(v => (long)v), pmf.Values.ToArray());
        Assert.Equal(1.0, pmf.Total);
        Assert.Equal(1.0, pmf.Weights.ToArray().Sum(), 1e-12);
    }

    [Fact]
    public void Parse_Bless_IsAQuarterEach()
    {
        var pmf = BonusDice.Parse("1d4", Where);

        Assert.Equal([0.25, 0.25, 0.25, 0.25], pmf.Weights.ToArray());
    }

    [Fact]
    public void Parse_Bane_IsMinusOneToMinusFour()
    {
        var pmf = BonusDice.Parse("-1d4", Where);

        Assert.Equal([-4L, -3, -2, -1], pmf.Values.ToArray());
        Assert.Equal([0.25, 0.25, 0.25, 0.25], pmf.Weights.ToArray());
    }

    [Fact]
    public void Parse_BlessAndBane_IsTheirConvolution()
    {
        var pmf = BonusDice.Parse("1d4-1d4", Where);

        // Triangular: 1, 2, 3, 4, 3, 2, 1 sixteenths over −3..3.
        Assert.Equal([1 / 16.0, 2 / 16.0, 3 / 16.0, 4 / 16.0, 3 / 16.0, 2 / 16.0, 1 / 16.0], pmf.Weights.ToArray());
    }

    [Fact]
    public void Parse_SubtractedDie_KeepsItsSign()
    {
        // dndMath.ts bug 5 read this as 1d8+1d4 (2..12); the lowest total is 1 − 4 = −3, with probability 1/32.
        var pmf = BonusDice.Parse("1d8-1d4", Where);

        Assert.Equal(-3, pmf.Min);
        Assert.Equal(1 / 32.0, pmf.Weights[0]);
    }

    [Theory]
    [InlineData("1d4kh1", "\"kh\" is a dice modifier")]
    [InlineData("2d6kh2", "\"kh\" is a dice modifier")]
    [InlineData("2d6kl1", "\"kl\" is a dice modifier")]
    [InlineData("4d6dl1", "\"dl\" is a dice modifier")]
    [InlineData("1d4r1", "\"r\" is a dice modifier")]
    [InlineData("2d4ro<2", "\"ro\" is a dice modifier")]
    [InlineData("1d4min2", "\"min\" is a dice modifier")]
    [InlineData("1d4max3", "\"max\" is a dice modifier")]
    [InlineData("10d10cs>=8", "\"cs\" is a dice modifier")]
    [InlineData("1d6!", "\"!\" makes dice explode")]
    [InlineData("1d6!!", "\"!\" makes dice explode")]
    [InlineData("1d4[bless]", "labels such as")]
    [InlineData("1d4 [bless]", "labels such as")]
    [InlineData("1d4>=3", "\">\" compares")]
    [InlineData("1d4=3", "\"=\" compares")]
    [InlineData("1d4*2", "\"*\" multiplies")]
    [InlineData("(1d8)/2", "\"/\" divides")]
    [InlineData("adv", "\"adv\" rolls the d20 itself")]
    [InlineData("dis+1d4", "\"dis\" rolls the d20 itself")]
    [InlineData("EA", "\"EA\" rolls the d20 itself")]
    [InlineData("1d4 fire", "\"fire\" is not part of a dice expression")]
    [InlineData("1d4+bless", "\"bless\" is not part of a dice expression")]
    public void Parse_AnythingButPlainDice_IsRefusedByName(string text, string reason)
    {
        var ex = Assert.Throws<DndInputException>(() => BonusDice.Parse(text, Where));

        Assert.StartsWith($"{Where} \"{text.Trim()}\" cannot be used: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("e.g. \"1d4\" (Bless), \"-1d4\" (Bane) or \"1d4+1d6\".", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("51d4", "it rolls 51 dice; the limit is 50")]
    [InlineData("25d4+26d6", "it rolls 51 dice; the limit is 50")]
    [InlineData("1d101", "\"1d101\" has 101 sides; the limit is 100")]
    [InlineData("1d4+101", "the whole number 101 is above 100")]
    [InlineData("1d4-101", "the whole number 101 is above 100")]
    public void Parse_BeyondTheLimits_IsRefusedWithTheLimit(string text, string reason)
    {
        var ex = Assert.Throws<DndInputException>(() => BonusDice.Parse(text, Where));

        Assert.StartsWith($"{Where} \"{text}\" cannot be used: {reason}.", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1d4+")]
    [InlineData("1d")]
    [InlineData("1d4 1d6")]
    [InlineData("--1d4")]
    [InlineData("(1d4")]
    [InlineData("1d0")]
    [InlineData("1d4.5")]
    public void Parse_Malformed_IsRefusedWithTheParserReasonAndThePlainDiceHint(string text)
    {
        var ex = Assert.Throws<DndInputException>(() => BonusDice.Parse(text, Where));

        Assert.StartsWith($"{Where}: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"\"{text}\"", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("e.g. \"1d4\" (Bless), \"-1d4\" (Bane) or \"1d4+1d6\".", ex.Message, StringComparison.Ordinal);
        Assert.IsType<DndInputException>(ex.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_IsRefusedWithAnExample(string? text)
    {
        var ex = Assert.Throws<DndInputException>(() => BonusDice.Parse(text!, Where));

        Assert.Equal($"{Where} is empty. Bonus dice are plain dice and whole numbers joined by + or -, e.g. \"1d4\" (Bless), \"-1d4\" (Bane) or \"1d4+1d6\".", ex.Message);
    }

    [Fact]
    public void Parse_LongText_IsEchoedCut()
    {
        var text = string.Concat(Enumerable.Repeat("1d4+", 30)) + "1d4kh1";

        var ex = Assert.Throws<DndInputException>(() => BonusDice.Parse(text, Where));

        Assert.Contains("…\" cannot be used", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 400, ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Parse_NoFieldName_IsABugInTheCaller(string? where)
    {
        Assert.ThrowsAny<ArgumentException>(() => BonusDice.Parse("1d4", where!));
    }

    [Fact]
    public void Parse_Result_IsAcceptedByTheOddsFunctions()
    {
        var dice = BonusDice.Parse("1d4+1d6-1", Where);

        var odds = AttackRoll.Odds(2, 15, 20, D20Options.Normal, dice);
        var fail = SavingThrow.FailChance(15, 2, D20Mode.Normal, dice);

        // b = 1..9 with E[b] = 2.5 + 3.5 − 1 = 5, and no outcome reaches a natural-1 or natural-20 clamp here, so the
        // chances are linear in b: hit on 13 − b or higher → (8 + E[b])/20; fail on 12 − b or lower → (12 − E[b])/20.
        Assert.Equal(13.0 / 20, odds.Hit, 1e-12);
        Assert.Equal(7.0 / 20, fail, 1e-12);
    }
}
