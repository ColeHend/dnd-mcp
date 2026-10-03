using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the next register code is 1 + the largest number of that letter among every existing code (suffixes
/// ignored, other letters and malformed codes ignored), so codes are never reused or renumbered (understand-onepiece.md
/// rows 41-43: F1 accepted and F2 struck give F3; the next is F4 whatever was struck).
/// </summary>
public sealed class RegisterCodesTests
{
    [Theory]
    [InlineData("F", "", "F1")]
    [InlineData("F", "F1|F2", "F3")]
    [InlineData("F", "F1|F2|F3|F4", "F5")]
    [InlineData("F", "F2|F1", "F3")]
    [InlineData("F", "F56a|F55", "F57")]
    [InlineData("F", "F56a", "F57")]
    [InlineData("F", "F76|F56a|Q22|C13", "F77")]
    [InlineData("F", "Q22|C13", "F1")]
    [InlineData("f", "f9", "F10")]
    [InlineData("C", "F99|C13|C2", "C14")]
    [InlineData("Q", "Q22|QQ30|Q-5|not a code|", "Q23")]
    [InlineData("QQ", "Q22|QQ30", "QQ31")]
    [InlineData("F", "F09", "F10")]
    public void Next_ExistingCodes_IsOneMoreThanTheLargestOfThatLetter(string letter, string existing, string next)
    {
        var codes = existing.Length == 0 ? [] : existing.Split('|');

        Assert.Equal(next, RegisterCodes.Next(letter, codes));
    }

    [Fact]
    public void Next_NullEntries_AreIgnored()
    {
        Assert.Equal("F2", RegisterCodes.Next("F", [null, "F1"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("F1")]
    [InlineData("FIVES")]
    [InlineData("É")]
    public void Next_NotALetter_Throws(string letter)
    {
        Assert.Throws<ArgumentException>(() => RegisterCodes.Next(letter, []));
    }

    [Fact]
    public void Next_NumbersUsedUp_IsAnInputError()
    {
        var ex = Assert.Throws<DndInputException>(() => RegisterCodes.Next("F", ["F999999"]));

        Assert.Contains("use another letter", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("f56a", true, "F", 56L, "a")]
    [InlineData(" Q22 ", true, "Q", 22L, "")]
    [InlineData("F56A", true, "F", 56L, "a")]
    [InlineData("56", false, "", 0L, "")]
    [InlineData("F", false, "", 0L, "")]
    [InlineData("F56ab", false, "", 0L, "")]
    public void TryParse_Code_SplitsLettersNumberAndSuffix(string code, bool ok, string letters, long number, string suffix)
    {
        Assert.Equal(ok, RegisterCodes.TryParse(code, out var l, out var n, out var s));
        Assert.Equal((letters, number, suffix), (l, n, s));
    }
}
