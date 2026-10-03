using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: searching the Belmakor campaign from any view but the author's never finds the Axiom Cage, the name Keras
/// or Belmakor's ambition through text the view cannot see (secret_md, author aliases, author-only or unknown facts),
/// while the names the view does use still find what they name. These are <c>understand-belmakor.md</c> §3 rows 20-29;
/// a regression here is the leak the whole campaign design exists to prevent (a lyric in Belmakor's voice naming the Cage).
/// </summary>
public sealed class BelmakorSearchTests(BelmakorFixture fixture) : IClassFixture<BelmakorFixture>
{
    private SearchResult Search(string? query, string perspective = "author", IReadOnlyList<string>? kinds = null) =>
        new CampaignSearch(fixture.Db.Database).Search(fixture.CampaignRow,
            new SearchRequest(query, Kinds: kinds, Perspective: Perspective.Parse(perspective), Limit: 50));

    [Fact]
    public void Search_Row20AxiomCageAsParty_FindsNothingAtAll()
    {
        var result = Search("Axiom Cage", "party");

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
        Assert.Equal(0, result.Total);
        Assert.Null(result.NextCursor);
        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor("party").Append("hidden"), "row 20");
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
    [InlineData("Axiom Cage", "character:belmakor")]
    [InlineData("Keras", "party")]
    [InlineData("Keras", "character:belmakor")]
    [InlineData("Keras", "table")]
    [InlineData("Keras", "public")]
    [InlineData("surface", "party")]
    [InlineData("reclaim", "party")]
    [InlineData("surface", "character:vars")]
    [InlineData("reclaim", "character:vars")]
    [InlineData("Cole", "party")]
    [InlineData("imported", "character:belmakor")]
    [InlineData("Third Silence", "dm")]
    public void Search_Rows21To28SecretOnlyWordsForNonAuthor_FindNothing(string query, string perspective)
    {
        var result = Search(query, perspective);

        Assert.Empty(result.Entities);
        Assert.Empty(result.Facts);
    }

    [Fact]
    public void Search_Row23AxiomCageAsAuthor_FindsTheItemTheThreadAndF1()
    {
        var result = Search("Axiom Cage");

        Assert.Contains(result.Entities, e => e.Ref == "item:thing-he-wants");
        Assert.Contains(result.Entities, e => e.Ref == "thread:old-kings-errand" && e.SnippetFrom == "secret");
        Assert.Contains(result.Facts, f => f.Ref == fixture.F1.SeqHandle && f.KnownBy!.Contains("author"));
        Assert.False(result.PartialMatch);
    }

    [Fact]
    public void Search_Row24OldKingAsBelmakor_PutsTheOldKingFirstUnderItsSafeRef()
    {
        var result = Search("old king", "character:belmakor");

        var first = result.Entities[0];
        Assert.Equal("character:old-king", first.Ref);
        Assert.Equal("The Old King", first.DisplayName);
        Assert.Equal(SearchTiers.Exact, first.MatchTier);
        Assert.Contains(result.Entities, e => e.Ref == "thread:old-kings-errand");
        Assert.Contains(result.Facts, f => f.Ref == fixture.F3.SeqHandle);
        Assert.All(result.Facts, f => Assert.Null(f.KnownBy));
        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor("character:belmakor"), "row 24");
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:ignis")]
    public void Search_Row25SorcererKing_FindsTheOldKingByItsPartyAlias(string perspective)
    {
        var result = Search("sorcerer king", perspective);

        var hit = Assert.Single(result.Entities, e => e.Ref == "character:old-king");
        Assert.Equal(SearchTiers.Exact, hit.MatchTier);
        Assert.Equal("The Old King", hit.DisplayName);
    }

    /// <summary>
    /// Row 27, pinned (contract §13): the dm knows f:2 ("The old king's name is Keras."), so the fact is found, but the
    /// entity is never found by its author alias: restricted and author aliases are not searchable by any non-author view.
    /// </summary>
    [Fact]
    public void Search_Row27KerasAsDm_FindsTheFactTheDmKnowsButNotTheEntity()
    {
        var result = Search("Keras", "dm");

        Assert.Empty(result.Entities);
        var fact = Assert.Single(result.Facts);
        Assert.Equal(fixture.F2.SeqHandle, fact.Ref);
        Assert.Null(fact.Truth);
        Assert.Null(fact.CanonStatus);
        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor("dm"), "row 27");
    }

    /// <summary>The author's known_by names each character knower by its ref, so two characters never collapse into one label.</summary>
    [Fact]
    public void Search_FactAsAuthor_NamesEachCharacterKnowerByItsRef()
    {
        var result = Search("reclaim surface");

        var f5 = Assert.Single(result.Facts, f => f.Ref == fixture.F5.SeqHandle);
        Assert.Equal(["author", "character:belmakor", "dm"], f5.KnownBy!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Search_Row29ReclaimSurfaceAsBelmakor_FindsHisSecretAndF5()
    {
        var result = Search("reclaim surface", "character:belmakor");

        Assert.Contains(result.Entities, e => e.Ref == "secret:belmakors-ambition" && e.DisplayName == "Belmakor's ambition");
        Assert.Contains(result.Facts, f => f.Ref == fixture.F5.SeqHandle);
    }

    /// <summary>
    /// The item is known to the party only as "the thing he wants" (a party alias, not its name): disguised, so its own
    /// name ("The thing the old king wants") and slug never find it, only the name the party uses, under e:&lt;n&gt;.
    /// </summary>
    [Fact]
    public void Search_DisguisedItemByItsTrueNameAsParty_IsNotFoundButItsKnownNameFindsItAsSeqRef()
    {
        var byTrueName = Search("old king wants", "party");
        var byKnownName = Search("thing he wants", "party");

        Assert.DoesNotContain(byTrueName.Entities, e => e.Kind == "item");
        Assert.Contains(byTrueName.Entities, e => e.Ref == "thread:old-kings-errand");
        var hit = Assert.Single(byKnownName.Entities, e => e.Kind == "item");
        Assert.Equal("e:" + fixture.Thing.Seq, hit.Ref);
        Assert.Equal("the thing he wants", hit.DisplayName);
        Assert.Null(hit.Status);
        Assert.Null(hit.Snippet);
        Assert.Equal(SearchTiers.Exact, hit.MatchTier);
    }

    [Fact]
    public void Search_ListingAsParty_ShowsOnlyWhatThePartyMaySeeByItsNames()
    {
        var result = Search(null, "party");

        Assert.All(result.Entities, e => Assert.Equal(SearchTiers.List, e.MatchTier));
        Assert.Contains(result.Entities, e => e.DisplayName == "the thing he wants");
        Assert.DoesNotContain(result.Entities, e => e.Kind is "rule" or "secret");
        Assert.DoesNotContain(result.Entities, e => e.DisplayName == "The thing the old king wants");
        Assert.Empty(result.Facts);
        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor("party"), "party listing");
    }

    [Fact]
    public void Search_ListingKindsAsAuthor_OrdersByKindThenName()
    {
        var result = Search(null, kinds: ["character", "arc"]);

        var kinds = result.Entities.Select(e => e.Kind).ToList();
        Assert.Equal(kinds.OrderBy(k => k, StringComparer.Ordinal), kinds);
        var characters = result.Entities.Where(e => e.Kind == "character").Select(e => e.DisplayName).ToList();
        Assert.Equal(characters.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), characters);
        Assert.Contains("The Old King", characters);
    }

    /// <summary>A Belmakor-side search never reaches the other campaign, even for the author (search is per campaign).</summary>
    [Fact]
    public void Search_KerasAsAuthor_FindsOnlyThisCampaignsOldKing()
    {
        var result = Search("Keras");

        Assert.Contains(result.Entities, e => e.Ref == "character:old-king");
        Assert.DoesNotContain(result.Entities, e => e.Ref.Contains("keras", StringComparison.OrdinalIgnoreCase));
    }

    public static TheoryData<string, string> LeakCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in BelmakorFixture.NonAuthorPerspectives)
        {
            foreach (var query in new[]
                     {
                         "Keras", "Axiom", "Cage", "Axiom Cage", "old king", "sorcerer king", "king*", "thing", "errand", "surface",
                         "reclaim", "Cole", "secret", "Baal", "Silence", "one piece", "Belmakor", "level", "Contingency", "the",
                     })
            {
                data.Add(perspective, query);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LeakCases))]
    public void Search_AnyQueryForAnyNonAuthorPerspective_NeverContainsWhatThatViewMustNotSee(string perspective, string query)
    {
        var result = Search(query, perspective);

        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor(perspective), $"search \"{query}\" as {perspective}");
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Search_ListingForAnyNonAuthorPerspective_NeverContainsWhatThatViewMustNotSee(string perspective)
    {
        var result = Search(null, perspective);

        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor(perspective), $"listing as {perspective}");
    }

    public static TheoryData<string> Perspectives() => new(BelmakorFixture.NonAuthorPerspectives);
}
