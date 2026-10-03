using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a slug is derived from the name through the same key names compare by (so names that compare equal get
/// the same slug), is lower-case hyphenated letters and digits of at most 60 characters, and a collision suffix keeps
/// the result within that length.
/// </summary>
public sealed class CampaignSlugsTests
{
    [Theory]
    [InlineData("Belmakor Silverwind", "belmakor-silverwind")]
    [InlineData("Nadar's Axe", "nadars-axe")]
    [InlineData("Nadar’s Axe", "nadars-axe")]
    [InlineData("G.O.D.S. Co.", "g-o-d-s-co")]
    [InlineData("Björn", "bjorn")]
    [InlineData("The Old King", "the-old-king")]
    [InlineData("  Iron   Guts  ", "iron-guts")]
    [InlineData("Session 12", "session-12")]
    public void From_Name_IsItsKeyHyphenated(string name, string slug)
    {
        Assert.Equal(slug, CampaignSlugs.From(name, "character"));
        Assert.True(CampaignSlugs.IsValid(slug));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("’’")]
    public void From_NoLettersOrDigits_GivesTheFallback(string? name)
    {
        Assert.Equal("character", CampaignSlugs.From(name, "character"));
    }

    [Fact]
    public void From_LongName_IsCutAtAHyphenWithinTheLimit()
    {
        var slug = CampaignSlugs.From("The Long And Winding Road That Leads To The Door Of The Old King Himself", "thread");

        Assert.True(slug.Length <= CampaignSlugs.MaxLength);
        Assert.False(slug.EndsWith('-'));
        Assert.StartsWith("the-long-and-winding-road", slug, StringComparison.Ordinal);
        Assert.True(CampaignSlugs.IsValid(slug));
    }

    [Theory]
    [InlineData("iron-guts", true)]
    [InlineData("a1", true)]
    [InlineData("Iron-Guts", false)]
    [InlineData("iron--guts", false)]
    [InlineData("-iron", false)]
    [InlineData("iron-", false)]
    [InlineData("iron guts", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_Shape(string? slug, bool valid)
    {
        Assert.Equal(valid, CampaignSlugs.IsValid(slug));
    }

    [Fact]
    public void IsValid_SixtyOneCharacters_IsTooLong()
    {
        Assert.True(CampaignSlugs.IsValid(new string('a', 60)));
        Assert.False(CampaignSlugs.IsValid(new string('a', 61)));
    }

    [Fact]
    public void WithSuffix_AddsTheNumberAndStaysWithinTheLimit()
    {
        Assert.Equal("keras-2", CampaignSlugs.WithSuffix("keras", 2));
        Assert.Equal("keras-10", CampaignSlugs.WithSuffix("keras", 10));
        var long60 = string.Join('-', Enumerable.Repeat("abcde", 10));
        var suffixed = CampaignSlugs.WithSuffix(long60, 3);
        Assert.True(suffixed.Length <= CampaignSlugs.MaxLength);
        Assert.EndsWith("-3", suffixed, StringComparison.Ordinal);
        Assert.True(CampaignSlugs.IsValid(suffixed));
        Assert.Throws<ArgumentOutOfRangeException>(() => CampaignSlugs.WithSuffix("keras", 1));
    }

    [Fact]
    public void ForSession_IsSessionDashNumber()
    {
        Assert.Equal("session-0", CampaignSlugs.ForSession(0));
        Assert.Equal("session-12", CampaignSlugs.ForSession(12));
    }
}
