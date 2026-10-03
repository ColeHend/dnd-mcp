using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignRead;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-belmakor.md</c> §3 rows 20-29 on the world the write path built. A player-side search never
/// finds the Axiom Cage, the name Keras or Belmakor's ambition through text it cannot see (secret_md, author aliases,
/// facts it does not know), and still finds what the names it uses name. The write path puts author aliases in
/// <c>hidden_aliases</c> and secret text in <c>secret</c> through its own FTS maintenance; a regression there (an author
/// alias indexed into the player-visible <c>aliases</c> column) passes every seeded read test and fails here.
/// </summary>
public sealed class BelmakorSearchScenarioTests(BelmakorScenario world) : IClassFixture<BelmakorScenario>
{
    private SearchResult Search(string query, string perspective = "author") => world.Reads.Search(world.Campaign, query, perspective);

    /// <summary>
    /// Row 20: nothing found, and no hint that anything was hidden. The any-word retry (contract §3.9) runs for every
    /// two-word query with no visible full match, so its flag is set here exactly as it is for two words the campaign
    /// never stored: the result is indistinguishable from "there is nothing", which is the property the golden's "no
    /// hidden result hint" asks for (a flag that differed would say "something matched that you cannot see").
    /// </summary>
    [Fact]
    public void Search_Row20AxiomCageAsParty_FindsNothingAndSaysNothingIsHidden()
    {
        var result = Search("Axiom Cage", "party");
        var nothing = Search("zqxv wvqj", "party");

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
        Assert.Equal(0, result.Total);
        Assert.Null(result.NextCursor);
        Assert.Equal(LeakAssert.Serialize(nothing), LeakAssert.Serialize(result));
        Assert.Equal((nothing.PartialMatch, nothing.Total, nothing.NextCursor), (result.PartialMatch, result.Total, result.NextCursor));
        LeakAssert.Clean(result, [.. BelmakorScenario.ForbiddenFor("party"), "hidden", "withheld"], "row 20");
    }

    [Theory]
    [InlineData("Axiom", "party")]
    [InlineData("Axiom", "character:belmakor")]
    [InlineData("Axiom", "table")]
    [InlineData("Axiom", "dm")]
    [InlineData("Axiom", "public")]
    [InlineData("cage", "party")]
    [InlineData("cage", "character:belmakor")]
    [InlineData("cage", "table")]
    [InlineData("cage", "dm")]
    [InlineData("cage", "public")]
    public void Search_Row21OneWordOfTheTrueNameForAnyPlayerView_FindsNothing(string query, string perspective)
    {
        var result = Search(query, perspective);

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
    }

    [Fact]
    public void Search_Row22AxiomCageAsBelmakor_FindsNothing()
    {
        var result = Search("Axiom Cage", "character:belmakor");

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
        LeakAssert.Clean(result, BelmakorScenario.ForbiddenFor("character:belmakor"), "row 22");
    }

    [Fact]
    public void Search_Row23AxiomCageAsAuthor_FindsTheItemByItsAuthorAliasTheThreadByItsSecretAndF1()
    {
        var result = Search("Axiom Cage");

        Assert.Contains(result.Entities, e => e.Ref == "item:thing-he-wants");
        Assert.Contains(result.Entities, e => e.Ref == "thread:old-kings-errand" && e.SnippetFrom == SnippetFields.Secret);
        var f1 = Assert.Single(result.Facts, f => f.Ref == "f:1");
        Assert.Equal(["author"], f1.KnownBy);
        Assert.False(result.PartialMatch);
    }

    [Fact]
    public void Search_Row24OldKingAsBelmakor_PutsTheOldKingFirstUnderItsSafeRef()
    {
        var result = Search("old king", "character:belmakor");

        var first = result.Entities[0];
        Assert.Equal(("character:old-king", "The Old King", SearchTiers.Exact), (first.Ref, first.DisplayName, first.MatchTier));
        Assert.Contains(result.Entities, e => e.Ref == "thread:old-kings-errand");
        Assert.Contains(result.Facts, f => f.Ref == "f:3");
        Assert.All(result.Facts, f => Assert.Null(f.KnownBy));
        LeakAssert.Clean(result, BelmakorScenario.ForbiddenFor("character:belmakor"), "row 24");
    }

    /// <summary>
    /// Row 24's "may also include item:thing-he-wants": for Belmakor the item is disguised (he knows it as "the thing he
    /// wants", not by its name "The thing the old king wants"), so it is never found through its own name, and it is
    /// never printed under the slug that spells that name.
    /// </summary>
    [Fact]
    public void Search_Row24OldKingAsBelmakor_NeverFindsTheDisguisedItemByItsTrueName()
    {
        var result = Search("old king", "character:belmakor");

        Assert.DoesNotContain(result.Entities, e => e.Kind == "item");
        Assert.DoesNotContain("thing-he-wants", LeakAssert.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:ignis")]
    [InlineData("character:belmakor")]
    public void Search_Row25SorcererKing_FindsTheOldKingByItsPartyAlias(string perspective)
    {
        var result = Search("sorcerer king", perspective);

        var hit = Assert.Single(result.Entities, e => e.Ref == "character:old-king");
        Assert.Equal(("The Old King", SearchTiers.Exact), (hit.DisplayName, hit.MatchTier));
    }

    /// <summary>Row 26: "Keras" finds nothing for any view but the author's and the dm's, and never anything of the other campaign.</summary>
    [Theory]
    [InlineData("party")]
    [InlineData("character:belmakor")]
    [InlineData("table")]
    [InlineData("public")]
    [InlineData("character:vars")]
    [InlineData("character:old-king")]
    public void Search_Row26KerasForAPlayerView_FindsNothingHereOrInTheOtherCampaign(string perspective)
    {
        var result = Search("Keras", perspective);

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
        LeakAssert.Clean(result, BelmakorScenario.ForbiddenFor(perspective), $"row 26 as {perspective}");
    }

    /// <summary>
    /// Row 27, pinned as the design answers it (contract §13): the golden's "desired" is that the dm, who knows f:2 ("The
    /// old king's name is Keras."), finds the old king by the name. Per-knower alias resolution is not built: restricted
    /// and author aliases are searchable by no non-author view, so the dm finds the FACT she knows and not the entity.
    /// </summary>
    [Fact]
    public void Search_Row27KerasAsDm_FindsTheFactSheKnowsButNotTheEntity()
    {
        var result = Search("Keras", "dm");

        Assert.Empty(result.Entities);
        var fact = Assert.Single(result.Facts);
        Assert.Equal(("f:2", "The old king's name is Keras."), (fact.Ref, fact.Text));
        Assert.Null(fact.KnownBy);
        LeakProbe.CleanExceptToldFacts(result, BelmakorScenario.ForbiddenFor("dm"), BelmakorScenario.ToldFacts("dm"), "row 27");
    }

    [Theory]
    [InlineData("surface", "party")]
    [InlineData("reclaim", "party")]
    [InlineData("surface", "character:vars")]
    [InlineData("reclaim", "character:vars")]
    [InlineData("reclaim surface", "table")]
    [InlineData("reclaim surface", "public")]
    public void Search_Row28TheAmbitionForAnyoneButBelmakor_FindsNothing(string query, string perspective)
    {
        var result = Search(query, perspective);

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
    }

    [Fact]
    public void Search_Row29ReclaimSurfaceAsBelmakor_FindsHisSecretAndF5()
    {
        var result = Search("reclaim surface", "character:belmakor");

        Assert.Contains(result.Entities, e => e.Ref == "secret:belmakors-ambition" && e.DisplayName == "Belmakor's ambition");
        Assert.Contains(result.Facts, f => f.Ref == "f:5");
    }

    /// <summary>
    /// Row 29 depends on Belmakor's knowledge row on his secret (the scenario's addition): the dm, who knows f:5 but has
    /// no row on the restricted entity, finds the fact and not the entity. Restricted means "the knowers its rows name".
    /// </summary>
    [Fact]
    public void Search_Row29ReclaimSurfaceAsDm_FindsTheFactButNotTheRestrictedEntity()
    {
        var result = Search("reclaim surface", "dm");

        Assert.DoesNotContain(result.Entities, e => e.Kind == "secret");
        Assert.Contains(result.Facts, f => f.Ref == "f:5");
    }
}
