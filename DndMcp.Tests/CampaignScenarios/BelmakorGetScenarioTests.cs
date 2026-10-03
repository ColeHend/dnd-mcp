using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignRead;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant: <c>understand-belmakor.md</c> §3 rows 30-35 (campaign_get, the cross-campaign firewall and the knowledge
/// resource) on the world the write path built. Belmakor sees "The Old King" with the sorcerer-king alias and the errand
/// facts, never "Keras", the secret text, f:2 or the cross-link the write path stored when it linked the old king
/// <c>same_as</c> one-piece's Keras; the author sees all of it. The cross-link is written by W's link op (stored with
/// a_id &lt; b_id), so this is the only place that proves the reader finds it from both campaigns and shows it to nobody else.
/// </summary>
public sealed class BelmakorGetScenarioTests(BelmakorScenario world) : IClassFixture<BelmakorScenario>
{
    private static readonly EntityIncludes RelationsFactsKnowledge = new(Relations: true, Facts: true, Knowledge: true);

    [Fact]
    public void Get_Row30OldKingAsBelmakor_ShowsHisNamesAndTheErrandFactsAndNothingOfKeras()
    {
        var result = world.Reads.Get(world.Campaign, "character:belmakor", RelationsFactsKnowledge, null, "character:old-king");

        var king = Assert.Single(result.Entities);
        Assert.Equal(("character:old-king", "The Old King"), (king.Ref, king.DisplayName));
        Assert.Equal([new AliasView("the sorcerer king", null)], king.Aliases);
        Assert.Null(king.Author);
        Assert.Equal(["f:3", "f:4"], king.Facts!.Select(f => f.Ref));
        Assert.All(king.Facts!, f => Assert.Null(f.Author));
        LeakAssert.Clean(result, [.. BelmakorScenario.ForbiddenFor("character:belmakor"), "\"f:2\"", "same_as", "secret"], "row 30");
    }

    [Fact]
    public void Get_Row31OldKingAsAuthor_ShowsTheAuthorAliasTheSecretF2AndTheCrossLink()
    {
        var result = world.Reads.Get(world.Campaign, "author", EntityIncludes.All, null, "character:old-king");

        var king = Assert.Single(result.Entities);
        Assert.Contains(new AliasView("Keras", "author"), king.Aliases);
        Assert.Contains(new AliasView("the sorcerer king", "party"), king.Aliases);
        Assert.StartsWith("Keras, Cole's old PC", king.Author!.SecretMd, StringComparison.Ordinal);
        Assert.Contains(king.Facts!, f => f.Ref == "f:2" && f.Author!.Visibility == "restricted");
        var link = Assert.Single(king.Author.CrossLinks);
        Assert.Equal(("one-piece", "one-piece/character:keras", "Keras"), (link.Campaign, link.Ref, link.Name));
        Assert.StartsWith("Same being", link.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("table")]
    [InlineData("party")]
    [InlineData("character:ignis")]
    [InlineData("character:vars")]
    public void Get_Row32OldKingAsAnyOtherView_HasNoCrossLinkAndNoAuthorPart(string perspective)
    {
        var result = world.Reads.Get(world.Campaign, perspective, EntityIncludes.All, null, "character:old-king");

        var king = Assert.Single(result.Entities);
        Assert.Null(king.Author);
        LeakProbe.CleanExceptToldFacts(result, [.. BelmakorScenario.ForbiddenFor(perspective), "same_as"], BelmakorScenario.ToldFacts(perspective),
            $"row 32 as {perspective}");
    }

    /// <summary>
    /// Rows 30 and 34's other entity, opened: "the thing he wants" is disguised for Belmakor and the party (their name for
    /// it is not its name, "The thing the old king wants"), so contract §3.2's disguised shape applies: its display name,
    /// its kind, <c>e:&lt;n&gt;</c> and the facts the view knows about it (f:3, the errand), and nothing else. The item has
    /// a stored summary ("…anything they've seen…"), a subtype and a party alias, so a reader that printed any of its own
    /// fields, or dropped the facts the view knows, fails here. f:1 is about it too, but author-only.
    /// </summary>
    [Theory]
    [InlineData("character:belmakor")]
    [InlineData("party")]
    public void Get_TheThingHeWantsAsAPlayerView_ShowsOnlyTheDisguiseAndTheFactItKnows(string perspective)
    {
        var seq = world.F.Entity(world.Campaign, "item:thing-he-wants").Seq;

        var result = world.Reads.Get(world.Campaign, perspective, EntityIncludes.All, null, "e:" + seq);

        var item = Assert.Single(result.Entities);
        Assert.Equal(("e:" + seq, "item", "the thing he wants"), (item.Ref, item.Kind, item.DisplayName));
        Assert.Null(item.Summary);
        Assert.Null(item.BodyMd);
        Assert.Null(item.Status);
        Assert.Null(item.Subtype);
        Assert.Empty(item.Aliases);
        Assert.Empty(item.Tags);
        Assert.Empty(item.Relations!);
        Assert.Empty(item.Children!);
        Assert.Null(item.Author);
        Assert.Equal(["f:3"], item.Facts!.Select(f => f.Ref));
        LeakAssert.Clean(result, [.. BelmakorScenario.ForbiddenFor(perspective), "they've seen", "artifact", "thing-he-wants", "old king wants"],
            $"the disguised item as {perspective}");
    }

    /// <summary>Row 32 for the public: the old king is party-visible, so to the public he is a handle that names nothing.</summary>
    [Fact]
    public void Get_Row32OldKingAsPublic_IsNotFound()
    {
        var ex = Assert.Throws<DndInputException>(() => world.Reads.Get(world.Campaign, "public", EntityIncludes.All, null, "character:old-king"));

        Assert.Contains("nothing by that handle for this perspective", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 33: from the other side of the firewall, One Piece's party sees Keras (party-visible there) with no trace of
    /// the sky-world: the cross-link W stored is visible to the author view only, from either campaign.
    /// </summary>
    [Fact]
    public void Get_Row33KerasInOnePieceAsParty_HasNoCrossLinkToTheSkyWorld()
    {
        var result = world.Reads.Get(world.OnePiece, "party", EntityIncludes.All, null, "character:keras");

        var keras = Assert.Single(result.Entities);
        Assert.Equal("Keras", keras.DisplayName);
        Assert.Null(keras.Author);
        LeakAssert.Clean(result, ["belmakor", "old king", "old-king", "sky-world", "same_as", "thing he wants"], "row 33");
    }

    [Fact]
    public void Get_Row33KerasInOnePieceAsAuthor_SeesTheCrossLinkBack()
    {
        var keras = world.Reads.Get(world.OnePiece, "author", EntityIncludes.All, null, "character:keras").Entities.Single();

        var link = Assert.Single(keras.Author!.CrossLinks);
        Assert.Equal(("belmakor", "belmakor/character:old-king"), (link.Campaign, link.Ref));
    }

    [Fact]
    public void KnownTo_Row34TheKnowledgeResourceForBelmakor_HasHisNamesAndNothingForbidden()
    {
        var pages = world.Reads.KnownTo(world.Campaign, "character:belmakor");

        var entities = pages.SelectMany(p => p.Entities).ToList();
        var facts = pages.SelectMany(p => p.Facts).ToList();
        Assert.Contains(entities, e => e.Name == "The Old King");
        Assert.Contains(entities, e => e.Name == "the thing he wants" && e.Ref.StartsWith("e:", StringComparison.Ordinal));
        Assert.Contains(facts, f => f.Ref == "f:5");
        Assert.DoesNotContain(facts, f => f.Ref is "f:1" or "f:2");
        var text = LeakAssert.Serialize(pages);
        Assert.Contains("the old king", text, StringComparison.Ordinal);
        Assert.Contains("the thing he wants", text, StringComparison.Ordinal);
        LeakAssert.Clean(pages, BelmakorScenario.ForbiddenFor("character:belmakor"), "row 34");
    }

    /// <summary>
    /// Row 35: the level drift. The write path superseded f:8 ("level 11") by f:9 in its own batch; the author's
    /// campaign_get of Belmakor shows f:9 as canon and f:8 only as superseded by f:9, with the source line it came from,
    /// plus Belmakor's history.
    /// </summary>
    [Fact]
    public void Get_Row35BelmakorAsAuthor_ShowsTheCurrentLevelAndTheSupersededOneWithItsSource()
    {
        var belmakor = world.Reads.Get(world.Campaign, "author", new EntityIncludes(Facts: true, History: true), null, "character:belmakor")
            .Entities.Single();

        var level12 = Assert.Single(belmakor.Facts!, f => f.Ref == "f:9");
        var level11 = Assert.Single(belmakor.Facts!, f => f.Ref == "f:8");
        Assert.Equal("canon", level12.Author!.CanonStatus);
        Assert.Equal(("superseded", "f:9", "party-and-band.md:19"), (level11.Author!.CanonStatus, level11.Author.SupersededBy, level11.Author.Source));
        Assert.NotEmpty(belmakor.Author!.History!);
    }

    /// <summary>Row 35 for a player view: only the current level is shown; a superseded fact is no longer true in the story.</summary>
    [Theory]
    [InlineData("party")]
    [InlineData("character:belmakor")]
    [InlineData("table")]
    public void Get_Row35BelmakorAsAPlayerView_ShowsOnlyTheCurrentLevel(string perspective)
    {
        var belmakor = world.Reads.Get(world.Campaign, perspective, new EntityIncludes(Facts: true), null, "character:belmakor").Entities.Single();

        var facts = belmakor.Facts!.Select(f => f.Ref).ToList();
        Assert.Contains("f:9", facts);
        Assert.DoesNotContain("f:8", facts);
        Assert.Null(world.Reads.TryGet(world.Campaign, perspective, "f:8"));
    }
}
