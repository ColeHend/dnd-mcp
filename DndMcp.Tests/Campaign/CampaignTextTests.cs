using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: names that people would call the same compare equal under <see cref="CampaignText.Key"/> (case,
/// diacritics, apostrophes of any kind, punctuation runs), and <see cref="CampaignText.KeyWithoutArticle"/> also drops a
/// leading the / a / an, but never the whole name.
/// </summary>
public sealed class CampaignTextTests
{
    [Theory]
    [InlineData("The Old King", "the old king")]
    [InlineData("  the   OLD king ", "the old king")]
    [InlineData("Björn", "bjorn")]
    [InlineData("Nadar's", "nadars")]
    [InlineData("Nadar’s", "nadars")]
    [InlineData("Nadar`s", "nadars")]
    [InlineData("G.O.D.S. Co.", "g o d s co")]
    [InlineData("rock-'n'-roll", "rock n roll")]
    [InlineData("Ｆｕｌｌｗｉｄｔｈ", "fullwidth")]
    [InlineData("naïve café", "naive cafe")]
    [InlineData("zero​width", "zerowidth")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    [InlineData("!!!", "")]
    public void Key_FoldsWhatPeopleTypeDifferently(string? text, string key)
    {
        Assert.Equal(key, CampaignText.Key(text));
    }

    [Theory]
    [InlineData("The Old King", "old king")]
    [InlineData("the old king", "old king")]
    [InlineData("A Thousand Suns", "thousand suns")]
    [InlineData("An Heir", "heir")]
    [InlineData("Old King", "old king")]
    [InlineData("The", "the")]
    [InlineData("Theodore", "theodore")]
    [InlineData("Anthem", "anthem")]
    [InlineData("The the", "the")]
    public void KeyWithoutArticle_DropsOneLeadingArticleOnly(string text, string key)
    {
        Assert.Equal(key, CampaignText.KeyWithoutArticle(text));
    }
}
