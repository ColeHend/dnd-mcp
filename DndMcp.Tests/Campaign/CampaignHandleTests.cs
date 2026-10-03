using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Xunit;
using H = DndMcp.Domain.Campaign.CampaignHandle;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: every handle form parses to the one thing it names and prints back canonically; forgiveness is limited to
/// what cannot change the meaning (spaces, the kind's spelling, case, a name typed for a slug); anything else is refused
/// with the accepted forms listed, because a guessed handle writes to the wrong place.
/// </summary>
public sealed class CampaignHandleTests
{
    [Theory]
    [InlineData("character:iron-guts", "character:iron-guts")]
    [InlineData("  Character : Iron-Guts ", "character:iron-guts")]
    [InlineData("character:Iron Guts", "character:iron-guts")]
    [InlineData("iron-guts", "iron-guts")]
    [InlineData("Iron Guts", "iron-guts")]
    [InlineData("Björn's Axe", "bjorns-axe")]
    [InlineData("e:12", "e:12")]
    [InlineData("E:12", "e:12")]
    [InlineData("f:3", "f:3")]
    [InlineData("F:3", "f:3")]
    [InlineData("Q22", "Q22")]
    [InlineData("q22", "Q22")]
    [InlineData("f56a", "F56a")]
    [InlineData("F56A", "F56a")]
    [InlineData("q7B", "Q7b")]
    [InlineData("F36", "F36")]
    [InlineData("session:12", "session:12")]
    [InlineData("Session:0", "session:0")]
    [InlineData("session:live", "session:live")]
    [InlineData("session:LAST", "session:last")]
    [InlineData("one-piece/character:keras", "one-piece/character:keras")]
    [InlineData("One-Piece/keras", "one-piece/keras")]
    [InlineData("one-piece/e:4", "one-piece/e:4")]
    [InlineData("one-piece/Q22", "one-piece/Q22")]
    public void Parse_EveryForm_PrintsCanonically(string text, string canonical)
    {
        Assert.Equal(canonical, H.Parse(text).Text);
        Assert.Equal(canonical, H.Parse(text).ToString());
    }

    [Fact]
    public void Parse_Forms_AreTheRightRecords()
    {
        Assert.Equal(new H.EntityBySlug("character", "iron-guts"), H.Parse("character:iron-guts"));
        Assert.Equal(new H.EntityBySlug(null, "iron-guts"), H.Parse("iron-guts"));
        Assert.Equal(new H.EntityBySeq(12), H.Parse("e:12"));
        Assert.Equal(new H.FactBySeq(3), H.Parse("f:3"));
        Assert.Equal(new H.ByCode("F56a"), H.Parse("F56a"));
        Assert.Equal(new H.ByCode("F56a"), H.Parse("F56A"));
        Assert.Equal(new H.SessionByNumber(12), H.Parse("session:12"));
        Assert.IsType<H.SessionLive>(H.Parse("session:live"));
        Assert.IsType<H.SessionLast>(H.Parse("session:last"));
        Assert.Equal(new H.CrossCampaign("one-piece", new H.EntityBySlug("character", "keras")), H.Parse("one-piece/character:keras"));
    }

    [Fact]
    public void Parse_KindSessionWithAName_IsASessionEntitySlug()
    {
        Assert.Equal(new H.EntityBySlug("session", "session-12"), H.Parse("session:session-12"));
    }

    [Theory]
    [InlineData(null, "A handle is required")]
    [InlineData("   ", "A handle is required")]
    [InlineData("wizard:iron-guts", "\"wizard\" is not a kind")]
    [InlineData("character:", "has nothing after the colon")]
    [InlineData("e:0", "expected a positive whole number")]
    [InlineData("e:abc", "expected a positive whole number")]
    [InlineData("f:-1", "expected a positive whole number")]
    [InlineData("session:100001", "the session number is too large")]
    [InlineData("!!!", "is not a handle")]
    [InlineData("one piece!/keras", "is not a campaign slug")]
    [InlineData("one-piece/session:3", "only an entity of another campaign")]
    [InlineData("one-piece/f:3", "only an entity of another campaign")]
    public void Parse_Malformed_IsRefusedWithTheForms(string? text, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => H.Parse(text));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.False(H.TryParse(text, out _, out var problem));
        Assert.Equal(ex.Message, problem);
    }

    [Fact]
    public void Parse_TooLong_EchoesOnlyTheStart()
    {
        var ex = Assert.Throws<DndInputException>(() => H.Parse(new string('a', 201)));

        Assert.Contains("is too long for a handle", ex.Message, StringComparison.Ordinal);
        Assert.True(ex.Message.Length < 400);
    }

    [Theory]
    [InlineData("F36", true)]
    [InlineData("q22", true)]
    [InlineData("F56a", true)]
    [InlineData("ABCD123456", true)]
    // FD6 (review C11) changed this from false: a code typed in capitals with its suffix (F56A) is the code F56a, not the
    // slug "f56a" that names nothing.
    [InlineData("F56A", true)]
    [InlineData("F56AB", false)]
    [InlineData("ABCDE1", false)]
    [InlineData("F1234567", false)]
    [InlineData("36", false)]
    [InlineData("F", false)]
    [InlineData(null, false)]
    public void IsCode_RegisterCodeShape(string? text, bool code)
    {
        Assert.Equal(code, H.IsCode(text));
    }

    [Fact]
    public void CanonicalCode_UpperCasesLettersAndLowerCasesTheSuffix()
    {
        Assert.Equal("F56a", H.CanonicalCode(" f56a "));
        Assert.Equal("F56a", H.CanonicalCode("F56A"));
        Assert.Throws<ArgumentException>(() => H.CanonicalCode("not a code"));
    }
}
