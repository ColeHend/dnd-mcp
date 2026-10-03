using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a perspective parses forgivingly in its kind (default author), a character perspective carries a handle
/// that can name a character, and <see cref="PerspectiveContext.IsAuthorView"/> is the author, or the dm in a DM
/// campaign, and nothing else.
/// </summary>
public sealed class PerspectiveTests
{
    [Theory]
    [InlineData(null, "author")]
    [InlineData("", "author")]
    [InlineData("  ", "author")]
    [InlineData("Author", "author")]
    [InlineData("PARTY", "party")]
    [InlineData(" table ", "table")]
    [InlineData("dm", "dm")]
    [InlineData("Public", "public")]
    [InlineData("character:belmakor", "character:belmakor")]
    [InlineData("Character: Belmakor", "character:belmakor")]
    [InlineData("character:character:belmakor", "character:belmakor")]
    [InlineData("character:e:12", "character:e:12")]
    [InlineData("character:Q22", "character:Q22")]
    public void Parse_Forms_PrintCanonically(string? text, string canonical)
    {
        Assert.Equal(canonical, Perspective.Parse(text).Text);
    }

    [Fact]
    public void Parse_Default_IsTheSharedAuthorInstance()
    {
        Assert.Same(Perspective.Author, Perspective.Parse(null));
        Assert.True(Perspective.Parse("author").IsAuthor);
        Assert.False(Perspective.Parse("dm").IsAuthor);
    }

    [Theory]
    [InlineData("gm", "is not one of")]
    [InlineData("party:belmakor", "only \"character:<slug>\" takes a handle")]
    [InlineData("character", "needs the character after a colon")]
    [InlineData("character:", "needs the character after a colon")]
    [InlineData("character:f:3", "must name a character")]
    [InlineData("character:session:3", "must name a character")]
    [InlineData("character:location:silk-isle", "a perspective is a character, not a location")]
    [InlineData("character:one-piece/keras", "must name a character")]
    public void Parse_Malformed_IsRefused(string text, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => Perspective.Parse(text));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForCharacter_WrapsAParsedHandle()
    {
        var perspective = Perspective.ForCharacter(CampaignHandle.Parse("e:7"));

        Assert.Equal("character:e:7", perspective.Text);
        Assert.Equal(CampaignValues.PerspectiveKinds.Character, perspective.Kind);
    }

    [Theory]
    [InlineData("author", "player", true)]
    [InlineData("author", "dm", true)]
    [InlineData("dm", "dm", true)]
    [InlineData("dm", "player", false)]
    [InlineData("party", "dm", false)]
    [InlineData("table", "dm", false)]
    [InlineData("public", "dm", false)]
    [InlineData("character:belmakor", "dm", false)]
    public void IsAuthorView_OnlyTheAuthorOrTheDmOfADmCampaign(string perspective, string role, bool authorView)
    {
        Assert.Equal(authorView, PerspectiveContext.For(Perspective.Parse(perspective), role).IsAuthorView);
    }
}
