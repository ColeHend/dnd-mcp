using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: campaign_get of the Belmakor campaign shows each view exactly its part (<c>understand-belmakor.md</c> §3
/// rows 30-35): Belmakor sees "The Old King" with the sorcerer-king alias and the errand facts, never "Keras", the secret
/// text, f:2 or the cross-link to One Piece; only the author sees those. The leak matrix gets every entity and fact each
/// view can reach, with every include, and checks the serialized result.
/// </summary>
public sealed class BelmakorGetTests(BelmakorFixture fixture) : IClassFixture<BelmakorFixture>
{
    private GetResult Get(string perspective, EntityIncludes includes, params string[] refs) =>
        new EntityReader(fixture.Db.Database).Get(fixture.CampaignRow, refs, includes, Perspective.Parse(perspective));

    [Fact]
    public void Get_Row30OldKingAsBelmakor_ShowsHisNamesAndFactsAndNothingOfKeras()
    {
        var result = Get("character:belmakor", new EntityIncludes(Relations: true, Facts: true, Knowledge: true), "character:old-king");

        var king = Assert.Single(result.Entities);
        Assert.Equal("character:old-king", king.Ref);
        Assert.Equal("The Old King", king.DisplayName);
        Assert.Equal([new AliasView("the sorcerer king", null)], king.Aliases);
        Assert.Null(king.Author);
        Assert.Equal([fixture.F3.SeqHandle, fixture.F4.SeqHandle], king.Facts!.Select(f => f.Ref));
        Assert.All(king.Facts!, f => Assert.Null(f.Author));
        var own = king.Knowledge![0];
        Assert.Equal(("character:old-king", Standings.Knows, "met", "the old king", 3),
            (own.Target, own.Standing, own.State, own.KnownAs, own.LearnedSession));
        Assert.All(king.Knowledge!, k => Assert.Null(k.Author));
        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor("character:belmakor").Append(fixture.F2.SeqHandle + "\""), "row 30");
    }

    [Fact]
    public void Get_Row31OldKingAsAuthor_ShowsTheAuthorAliasTheSecretF2AndTheCrossLink()
    {
        var result = Get("author", EntityIncludes.All, "character:old-king");

        var king = Assert.Single(result.Entities);
        Assert.Contains(new AliasView("Keras", "author"), king.Aliases);
        Assert.Contains(new AliasView("the sorcerer king", "party"), king.Aliases);
        Assert.StartsWith("Keras, Cole's old PC", king.Author!.SecretMd, StringComparison.Ordinal);
        Assert.Contains(king.Facts!, f => f.Ref == fixture.F2.SeqHandle && f.Author!.Visibility == "restricted");
        var link = Assert.Single(king.Author.CrossLinks);
        Assert.Equal(("one-piece", "one-piece/character:keras", "Keras"), (link.Campaign, link.Ref, link.Name));
        Assert.Contains(king.Knowledge!, k => k.Knower == "author" && k.KnownAs == "Keras");
        Assert.Contains(king.Knowledge!, k => k.Knower == "dm" && k.Target == fixture.F2.SeqHandle && k.Standing == Standings.Knows);
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("table")]
    [InlineData("party")]
    [InlineData("character:ignis")]
    public void Get_Row32OldKingAsAnyOtherView_HasNoCrossLinkAndNoAuthorPart(string perspective)
    {
        var result = Get(perspective, EntityIncludes.All, "character:old-king");

        var king = Assert.Single(result.Entities);
        Assert.Null(king.Author);
        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor(perspective).Append("same_as"), $"row 32 as {perspective}");
    }

    /// <summary>Row 32 for the public: the old king is party-visible, so the public has no record of him at all.</summary>
    [Fact]
    public void Get_Row32OldKingAsPublic_IsNotFound()
    {
        var ex = Assert.Throws<DndInputException>(() => Get("public", EntityIncludes.All, "character:old-king"));

        Assert.Contains("nothing by that handle for this perspective", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Row33KerasInOnePieceAsParty_HasNoCrossLinkToTheSkyWorld()
    {
        var result = new EntityReader(fixture.Db.Database).Get(fixture.OnePieceRow, ["character:keras"], EntityIncludes.All, Perspective.Parse("party"));

        var keras = Assert.Single(result.Entities);
        Assert.Equal("Keras", keras.DisplayName);
        Assert.Null(keras.Author);
        LeakAssert.Clean(result, ["belmakor", "old king", "old-king", "sky-world", "same_as"], "row 33");
    }

    [Fact]
    public void Get_Row35BelmakorAsAuthor_ShowsTheLevelFactsWithTheSupersession()
    {
        var result = Get("author", new EntityIncludes(Facts: true, History: true), "character:belmakor");

        var belmakor = Assert.Single(result.Entities);
        var level12 = Assert.Single(belmakor.Facts!, f => f.Ref == fixture.F9.SeqHandle);
        var level11 = Assert.Single(belmakor.Facts!, f => f.Ref == fixture.F8.SeqHandle);
        Assert.Equal("canon", level12.Author!.CanonStatus);
        Assert.Equal(("superseded", fixture.F9.SeqHandle, "party-and-band.md:19"),
            (level11.Author!.CanonStatus, level11.Author.SupersededBy, level11.Author.Source));
        Assert.NotNull(belmakor.Author!.History);
    }

    /// <summary>A superseded fact is not shown to a player view (it is no longer true in the story); the author sees both.</summary>
    [Fact]
    public void Get_BelmakorAsParty_ShowsTheCurrentLevelOnly()
    {
        var result = Get("party", new EntityIncludes(Facts: true), "e:" + fixture.Belmakor.Seq);

        var facts = Assert.Single(result.Entities).Facts!.Select(f => f.Ref).ToList();
        Assert.Contains(fixture.F9.SeqHandle, facts);
        Assert.DoesNotContain(fixture.F8.SeqHandle, facts);
        Assert.DoesNotContain(fixture.F5.SeqHandle, facts);
    }

    /// <summary>
    /// Belmakor's slug is "belmakor", which spells his party alias, not his name: the party sees it (the alias is theirs),
    /// so the ref stays <c>character:belmakor</c>. Vars's slug "vars" spells nothing the party sees as a name or alias, so
    /// the party gets <c>e:&lt;n&gt;</c>.
    /// </summary>
    [Fact]
    public void Get_RefsForTheParty_UseTheSlugOnlyWhenItSpellsANameTheyUse()
    {
        var result = Get("party", EntityIncludes.Default, "character:belmakor", "e:" + fixture.Vars.Seq, "character:aiden-ironstar");

        Assert.Equal(["character:belmakor", "e:" + fixture.Vars.Seq, "character:aiden-ironstar"], result.Entities.Select(e => e.Ref));
    }

    /// <summary>
    /// The item is disguised for the party: it shows only "the thing he wants", its kind, e:&lt;n&gt; and the facts the
    /// party knows linked to it. Typing its true slug finds nothing, worded exactly like a handle that names nothing.
    /// </summary>
    [Fact]
    public void Get_DisguisedItemAsParty_ShowsOnlyTheDisguiseAndRefusesTheTrueSlug()
    {
        var bySeq = Get("party", EntityIncludes.All, "e:" + fixture.Thing.Seq);
        var bySlug = Assert.Throws<DndInputException>(() => Get("party", EntityIncludes.All, "item:thing-he-wants"));
        var missing = Assert.Throws<DndInputException>(() => Get("party", EntityIncludes.All, "item:nothing-here"));

        var item = Assert.Single(bySeq.Entities);
        Assert.Equal(("e:" + fixture.Thing.Seq, "item", "the thing he wants"), (item.Ref, item.Kind, item.DisplayName));
        Assert.Null(item.Summary);
        Assert.Null(item.BodyMd);
        Assert.Null(item.Status);
        Assert.Null(item.Subtype);
        Assert.Empty(item.Aliases);
        Assert.Empty(item.Tags);
        Assert.Empty(item.Relations!);
        Assert.Empty(item.Children!);
        Assert.Equal([fixture.F3.SeqHandle], item.Facts!.Select(f => f.Ref));
        LeakAssert.Clean(bySeq, BelmakorFixture.ForbiddenFor("party").Concat(["thing-he-wants", "The thing the old king wants", "don't use it"]), "disguised item");
        Assert.Contains("nothing by that handle for this perspective", bySlug.Message, StringComparison.Ordinal);
        Assert.Contains("nothing by that handle for this perspective", missing.Message, StringComparison.Ordinal);
        LeakAssert.CleanMessage(bySlug.Message, "item:thing-he-wants", ["The thing the old king wants", "Axiom"], "not-found message");
    }

    [Theory]
    [InlineData("party", "rule:old-king-no-name-no-timespan")]
    [InlineData("character:vars", "secret:belmakors-ambition")]
    [InlineData("public", "character:old-king")]
    [InlineData("party", "keras")]
    [InlineData("party", "f:1")]
    [InlineData("character:belmakor", "f:2")]
    public void Get_HiddenHandleForAPlayerView_IsNotFound(string perspective, string handle)
    {
        var ex = Assert.Throws<DndInputException>(() => Get(perspective, EntityIncludes.All, handle));

        Assert.Contains("nothing by that handle for this perspective", ex.Message, StringComparison.Ordinal);
        LeakAssert.CleanMessage(ex.Message, handle, BelmakorFixture.ForbiddenFor(perspective), $"{handle} as {perspective}");
    }

    [Fact]
    public void Get_FactAsBelmakor_ShowsItsVisibleLinksAndHisVerdict()
    {
        var result = Get("character:belmakor", new EntityIncludes(Knowledge: true), fixture.F3.SeqHandle);

        var fact = Assert.Single(result.Facts);
        Assert.Null(fact.Author);
        Assert.Contains(fact.Links, l => l.Role == "about" && l.Entity.Name == "the thing he wants" && l.Entity.Ref == "e:" + fixture.Thing.Seq);
        var verdict = Assert.Single(fact.Knowledge!);
        Assert.Equal((Standings.Knows, "knows", 3), (verdict.Standing, verdict.State, verdict.LearnedSession));
        Assert.Contains("Belmakor Silverwind was present", verdict.Explanation, StringComparison.Ordinal);
        LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor("character:belmakor"), "a fact for Belmakor");
    }

    [Fact]
    public void Get_Row17ContingencyAsOfSession1AsTable_IsNotFoundButAsOf2ItIs()
    {
        var reader = new EntityReader(fixture.Db.Database);

        Assert.Throws<DndInputException>(() => reader.Get(fixture.CampaignRow, [fixture.F6.SeqHandle], EntityIncludes.Default, Perspective.Parse("table"), 1));
        var asOf2 = reader.Get(fixture.CampaignRow, [fixture.F6.SeqHandle], EntityIncludes.Default, Perspective.Parse("table"), 2);

        Assert.Equal(fixture.F6.SeqHandle, Assert.Single(asOf2.Facts).Ref);
        Assert.Equal(2, asOf2.AsOfSession);
    }

    [Fact]
    public void Get_TooManyOrNoRefs_IsRefused()
    {
        var none = Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default));
        var many = Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default, Enumerable.Repeat("character:belmakor", 11).ToArray()));

        Assert.Contains("refs is required", none.Message, StringComparison.Ordinal);
        Assert.Contains("at most 10", many.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_SeveralBadRefs_ListsEachWithItsPosition()
    {
        var ex = Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default, "character:belmakor", "character:nobody", "e:0"));

        Assert.Contains("refs item 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refs item 3", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("refs item 1", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> Perspectives() => new(BelmakorFixture.NonAuthorPerspectives);

    /// <summary>
    /// Everything a view can reach (every entity of its listing and every fact it can find), read with every include,
    /// contains nothing the view must not see.
    /// </summary>
    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Get_EverythingAViewCanReach_NeverContainsWhatItMustNotSee(string perspective)
    {
        var view = Perspective.Parse(perspective);
        var search = new CampaignSearch(fixture.Db.Database);
        var refs = search.Search(fixture.CampaignRow, new SearchRequest(Perspective: view, Limit: 50)).Entities.Select(e => e.Ref).ToList();
        var facts = Enumerable.Range(1, 9).Select(i => "f:" + i)
            .Where(f => TryGet(perspective, f) is not null).ToList();
        Assert.True(perspective == "public" || refs.Count > 0, "every member view reaches something");

        foreach (var chunk in refs.Concat(facts).Chunk(10))
        {
            var result = Get(perspective, EntityIncludes.All, chunk);
            LeakAssert.Clean(result, BelmakorFixture.ForbiddenFor(perspective), $"get {string.Join(", ", chunk)} as {perspective}");
        }
    }

    private GetResult? TryGet(string perspective, string handle)
    {
        try
        {
            return Get(perspective, EntityIncludes.Default, handle);
        }
        catch (DndInputException)
        {
            return null;
        }
    }
}
