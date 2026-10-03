using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignDb;
using Xunit;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant (the leak rule, contract §0): every reader, for every non-author perspective, returns nothing that
/// perspective cannot see. Checked on the Belmakor fixture for the readers the golden classes do not already sweep
/// (summary, sessions, the knowledge resource), and on the stage-1 finding-13 shape through every reader: an entity whose
/// own NAME is the secret, known to the party only by its party alias. That shape is the one the column filter alone
/// does not close (the name is in a player column), so it must be disguised everywhere it can surface.
/// </summary>
public sealed class LeakRuleTests(BelmakorFixture fixture) : IClassFixture<BelmakorFixture>
{
    public static TheoryData<string> Perspectives() => new(BelmakorFixture.NonAuthorPerspectives);

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Summary_AnyPlayerView_NeverContainsWhatItMustNotSee(string perspective)
    {
        var summary = new CampaignSummary(fixture.Db.Database).Build(fixture.CampaignRow, Perspective.Parse(perspective));

        Assert.Null(summary.Author);
        LeakAssert.Clean(summary, BelmakorFixture.ForbiddenFor(perspective), $"summary as {perspective}");
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Sessions_AnyPlayerView_NeverContainsWhatItMustNotSee(string perspective)
    {
        var reader = new SessionReader(fixture.Db.Database);
        var view = Perspective.Parse(perspective);

        var list = reader.List(fixture.CampaignRow, perspective: view);
        LeakAssert.Clean(list, BelmakorFixture.ForbiddenFor(perspective), $"session list as {perspective}");
        foreach (var session in list.Sessions)
        {
            var detail = reader.Get(fixture.CampaignRow, session.Ref, view);
            Assert.Null(detail.Author);
            LeakAssert.Clean(detail, BelmakorFixture.ForbiddenFor(perspective), $"{session.Ref} as {perspective}");
        }
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Search_UnknownCharacterPerspective_NamesNothingHidden(string perspective)
    {
        var ex = Assert.Throws<DndInputException>(() => new CampaignSearch(fixture.Db.Database).Search(fixture.CampaignRow,
            new SearchRequest("x", Perspective: Perspective.Parse("character:keras"))));

        // The message echoes what was typed ("keras", twice); nothing else in it may name a hidden thing.
        LeakAssert.CleanMessage(ex.Message, "keras", BelmakorFixture.ForbiddenFor(perspective), "unknown character");
        Assert.DoesNotContain("old-king", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> Finding13Perspectives() => new() { "party", "table", "dm", "character:belmakor" };

    /// <summary>The finding-13 shape through search, get (both ends of a relation), summary, sessions and the knowledge resource.</summary>
    [Theory]
    [MemberData(nameof(Finding13Perspectives))]
    public void EveryReader_EntityNamedWithItsSecretKnownByAPartyAlias_NeverPrintsTheName(string perspective)
    {
        using var db = new CampaignTestDb();
        using var connection = db.Open();
        var seed = new CampaignSeed(connection);
        var campaign = seed.Campaign(name: "Sky", role: CampaignValues.Roles.Player);
        var s1 = seed.Session(campaign.Id, 1, title: "The statue");
        var hero = seed.Entity(campaign.Id, "character", "Belmakor", subtype: "pc");
        seed.MemberOf(campaign.Id, hero.Id, campaign.Party!.Id);
        var keras = seed.Entity(campaign.Id, "character", "Keras", subtype: "npc",
            summary: "Keras, the sorcerer of the first campaign.", body: "Keras was Cole's PC.");
        seed.Alias(keras.Id, "the old king", V.Party);
        seed.Knowledge(campaign.Id, "party", state: "met", entityId: keras.Id, knownAs: "the old king", learnedSessionId: s1.EntityId);
        seed.MemberOf(campaign.Id, keras.Id, campaign.Party.Id);
        seed.Relation(campaign.Id, hero.Id, "fought", keras.Id);
        var errand = seed.Fact(campaign.Id, "The old king sent the party on an errand.", visibility: V.Party);
        seed.FactLink(errand.Id, keras.Id);
        seed.Attendance(s1.EntityId, hero.Id);
        seed.Attendance(s1.EntityId, keras.Id);
        var row = seed.LoadCampaign(campaign.Id);
        var view = Perspective.Parse(perspective);
        var forbidden = new[] { "Keras", "keras", "sorcerer of the first", "Cole" };

        var results = new List<object>
        {
            new CampaignSearch(db.Database).Search(row, new SearchRequest("old king", Perspective: view)),
            new CampaignSearch(db.Database).Search(row, new SearchRequest("Keras", Perspective: view)),
            new CampaignSearch(db.Database).Search(row, new SearchRequest(Perspective: view)),
            new EntityReader(db.Database).Get(row, ["e:" + keras.Seq, "character:belmakor", errand.SeqHandle], EntityIncludes.All, view),
            new CampaignSummary(db.Database).Build(row, view),
            new SessionReader(db.Database).Get(row, "1", view),
            new KnowledgeLedger(db.Database).KnownTo(row, view),
        };

        foreach (var result in results)
        {
            LeakAssert.Clean(result, forbidden, $"finding 13 as {perspective}: {result.GetType().Name}");
        }

        var king = Assert.Single(((GetResult)results[3]).Entities, e => e.Ref == "e:" + keras.Seq);
        Assert.Equal("the old king", king.DisplayName);
        Assert.Throws<DndInputException>(() => new EntityReader(db.Database).Get(row, ["character:keras"], EntityIncludes.All, view));
    }
}
