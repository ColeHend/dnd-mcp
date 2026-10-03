using DndMcp.Domain.Campaign;
using Xunit;
using static DndMcp.Tests.Campaign.Knowers;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: relations and objectives (which have no knowledge rows) are seen by visibility alone: public by everyone,
/// party by the party, the table, the dm and characters who are party members now (not ones who left: a row with no
/// session cannot say whether it came before they left), author by the author view only; a relation also needs both
/// endpoints visible.
/// </summary>
public sealed class AudienceTests
{
    [Theory]
    [InlineData("author", "player", "author", true)]
    [InlineData("dm", "dm", "author", true)]
    [InlineData("dm", "player", "author", false)]
    [InlineData("party", "player", "author", false)]
    [InlineData("member", "player", "author", false)]
    [InlineData("public", "player", "public", true)]
    [InlineData("stranger", "player", "public", true)]
    [InlineData("party", "player", "party", true)]
    [InlineData("table", "player", "party", true)]
    [InlineData("dm", "player", "party", true)]
    [InlineData("member", "player", "party", true)]
    [InlineData("joined", "player", "party", true)]
    // FD1 (review L02): a former member (until set) no longer sees party rows; he still sees public ones.
    [InlineData("former", "player", "party", false)]
    [InlineData("former", "player", "public", true)]
    // A membership marked former with no until has ended too (review of FD1): its status says so.
    [InlineData("left", "player", "party", false)]
    [InlineData("left", "player", "public", true)]
    [InlineData("stranger", "player", "party", false)]
    [InlineData("public", "player", "party", false)]
    [InlineData("party", "player", "restricted", false)]
    [InlineData("author", "player", "restricted", true)]
    public void Sees_Visibility_FollowsTheAudienceRules(string who, string role, string visibility, bool sees)
    {
        var context = who switch
        {
            "author" => Author(role),
            "member" => Character("c1", "Belmakor", new PartyMembership(null, null), role),
            "joined" => Character("c2", "Aiden Ironstar", new PartyMembership(3, null), role),
            "former" => Character("c3", "Tristan", new PartyMembership(null, 1), role),
            "left" => Character("c4", "Robin", new PartyMembership(2, null) { Former = true }, role),
            "stranger" => Character("c9", "The Old King", null, role),
            _ => Of(who, role),
        };

        Assert.Equal(sees, Audience.Sees(context, visibility));
    }

    [Fact]
    public void Sees_UnknownVisibility_Throws()
    {
        Assert.Throws<ArgumentException>(() => Audience.Sees(Of("party"), "secret"));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void RelationVisible_PartyRelation_NeedsBothEndpointsVisible(bool from, bool to, bool visible)
    {
        Assert.Equal(visible, Audience.RelationVisible(Of("party"), "party", from, to));
        Assert.False(Audience.RelationVisible(Of("party"), "author", from, to));
    }
}
