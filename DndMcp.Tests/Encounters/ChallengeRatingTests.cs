using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// Invariant: a Challenge Rating is exactly one of the 34 table rows, however it is written ("1/8", 0.125, "CR 1/8"),
/// and anything between rows is refused rather than rounded onto one.
/// </summary>
public sealed class ChallengeRatingTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1/8", 1)]
    [InlineData("0.125", 1)]
    [InlineData("⅛", 1)]
    [InlineData("1/4", 2)]
    [InlineData(".25", 2)]
    [InlineData("¼", 2)]
    [InlineData("1/2", 4)]
    [InlineData("0.5", 4)]
    [InlineData("½", 4)]
    [InlineData("CR 1/2", 4)]
    [InlineData("cr5", 40)]
    [InlineData(" 12 ", 96)]
    [InlineData("1", 8)]
    [InlineData("1.0", 8)]
    [InlineData("30", 240)]
    public void Parse_AcceptedForm_IsThatRow(string text, int eighths)
    {
        Assert.Equal(eighths, ChallengeRating.Parse(text).Eighths);
    }

    [Theory]
    [InlineData("31")]
    [InlineData("-1")]
    [InlineData("1/3")]
    [InlineData("2/4")]
    [InlineData("1/16")]
    [InlineData("3.5")]
    [InlineData("0.126")]
    [InlineData("1e1")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("five")]
    [InlineData("CR")]
    public void Parse_NotATableValue_IsRefusedWithTheValuesAccepted(string? text)
    {
        var ex = Assert.Throws<DndInputException>(() => ChallengeRating.Parse(text));

        Assert.Contains("is not a Challenge Rating", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0, 1/8, 1/4, 1/2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("from 1 to 30", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_LongText_IsEchoedCut()
    {
        var ex = Assert.Throws<DndInputException>(() => ChallengeRating.Parse(new string('x', 500)));

        Assert.True(ex.Message.Length < 250, ex.Message);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.125, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.5, 4)]
    [InlineData(1.0, 8)]
    [InlineData(17.0, 136)]
    [InlineData(30.0, 240)]
    public void FromNumber_TableValue_IsThatRow(double value, int eighths)
    {
        Assert.Equal(eighths, ChallengeRating.FromNumber(value)!.Value.Eighths);
    }

    [Theory]
    [InlineData(0.375)]
    [InlineData(0.75)]
    [InlineData(1.5)]
    [InlineData(0.3)]
    [InlineData(-0.125)]
    [InlineData(30.125)]
    [InlineData(31.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FromNumber_NotATableValue_IsNull(double value)
    {
        Assert.Null(ChallengeRating.FromNumber(value));
    }

    [Fact]
    public void All_EveryCr_Is34RowsInOrderFromZeroToThirty()
    {
        var all = ChallengeRating.All;

        Assert.Equal(34, all.Count);
        Assert.Equal(["0", "1/8", "1/4", "1/2"], all.Take(4).Select(cr => cr.ToString()));
        Assert.Equal(Enumerable.Range(1, 30).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)), all.Skip(4).Select(cr => cr.ToString()));
        Assert.True(all.Zip(all.Skip(1)).All(pair => pair.First < pair.Second));
    }

    [Fact]
    public void Row_EveryCr_IsItsIndexInAll()
    {
        Assert.Equal(Enumerable.Range(0, 34), ChallengeRating.All.Select(cr => cr.Row));
    }

    [Fact]
    public void ToStringAndParse_EveryCr_RoundTrip()
    {
        Assert.All(ChallengeRating.All, cr => Assert.Equal(cr, ChallengeRating.Parse(cr.ToString())));
        Assert.All(ChallengeRating.All, cr => Assert.Equal(cr, ChallengeRating.FromNumber(cr.Value)));
    }

    [Fact]
    public void Next_EveryCr_IsTheFollowingRowAndNullAtThirty()
    {
        var all = ChallengeRating.All;

        Assert.Equal(all.Skip(1), all.Take(33).Select(cr => cr.Next!.Value));
        Assert.Null(all[33].Next);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("1/2", null)]
    [InlineData("1", 1)]
    [InlineData("30", 30)]
    public void Whole_IsTheWholeNumberOrNullForFractions(string text, int? whole)
    {
        Assert.Equal(whole, ChallengeRating.Parse(text).Whole);
    }

    [Fact]
    public void Default_Uninitialized_IsCrZero()
    {
        Assert.Equal(ChallengeRating.Zero, default);
        Assert.Equal("0", default(ChallengeRating).ToString());
    }
}
