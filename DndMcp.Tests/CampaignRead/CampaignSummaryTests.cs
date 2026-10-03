using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignDb;
using Microsoft.Data.Sqlite;
using Xunit;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: the campaign summary a player view gets names members, places and threads by the names that view knows,
/// counts only what it may see (a withheld or lean question as open, with no withheld count), shows only clocks shown to
/// players, and has no author part (secrets, inventions, settings, history); the author's summary has all of it.
/// </summary>
public sealed class CampaignSummaryTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;

    public CampaignSummaryTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Sky World", role: CampaignValues.Roles.Player, dmName: "Robin", settings: "{\"effective_level_offset\":1}");
        var hero = _seed.Entity(_campaign.Id, "character", "Belmakor", subtype: "pc");
        var disguised = _seed.Entity(_campaign.Id, "character", "Keras", subtype: "npc", visibility: V.Restricted);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: disguised.Id, knownAs: "the old king");
        _seed.MemberOf(_campaign.Id, hero.Id, _campaign.Party!.Id);
        _seed.MemberOf(_campaign.Id, disguised.Id, _campaign.Party.Id);
        _seed.SetMyCharacter(_campaign.Id, hero.Id);
        _seed.Session(_campaign.Id, 1, title: "The Iron Guts job", recap: "The party took the job.\nTristan fell.\nThey fled.\nFourth line.");
        _seed.Session(_campaign.Id, 2, CampaignValues.SessionStatuses.Planned, title: "Keras reveals himself", visibility: V.Author);
        _seed.Entity(_campaign.Id, "thread", "The old king's errand", status: "open");
        _seed.Entity(_campaign.Id, "thread", "Done thread", status: "resolved");
        _seed.Entity(_campaign.Id, "quest", "Secret quest", status: "open", visibility: V.Author);
        _seed.Entity(_campaign.Id, "question", "Who sank the Maru?", status: "withheld");
        _seed.Entity(_campaign.Id, "question", "Where is the city?", status: "lean");
        _seed.Entity(_campaign.Id, "question", "What is the moon?", status: "open");
        var shownClock = _seed.Entity(_campaign.Id, "clock", "Storm", status: "running");
        _seed.Clock(shownClock.Id, 4, filled: 1, shownToPlayers: true);
        var hiddenClock = _seed.Entity(_campaign.Id, "clock", "Doom", status: "running");
        _seed.Clock(hiddenClock.Id, 6, filled: 5);
        _seed.Entity(_campaign.Id, "secret", "Belmakor's ambition", status: "hidden", visibility: V.Restricted);
        _seed.Entity(_campaign.Id, "location", "Tern Rock", canonStatus: "proposed", code: "F1");
        _seed.Fact(_campaign.Id, "The moon winks.", canonStatus: "proposed", code: "F2");
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private CampaignSummaryView Build(string perspective) =>
        new CampaignSummary(_db.Database).Build(_seed.LoadCampaign(_campaign.Id), Perspective.Parse(perspective));

    [Fact]
    public void Build_AsParty_ShowsOnlyWhatThePartySeesByItsNames()
    {
        var summary = Build("party");

        Assert.Equal(("Sky World", "player", "2024", "Robin"), (summary.Name, summary.Role, summary.Ruleset, summary.DmName));
        Assert.Equal("Belmakor", summary.MyCharacter!.Name);
        Assert.Equal(["Belmakor", "the old king"], summary.PartyMembers.Select(m => m.Name));
        Assert.Equal(1, summary.LastPlayed!.Number);
        Assert.Equal("The party took the job.\nTristan fell.\nThey fled.", summary.LastRecapOpening);
        Assert.Equal(["The old king's errand"], summary.OpenThreads.Select(t => t.Entity.Name));
        Assert.Equal(1, summary.OpenThreadsTotal);
        Assert.Equal(3, summary.OpenQuestions);
        Assert.Equal(["Storm"], summary.RunningClocks.Select(c => c.Clock.Name));
        Assert.Null(summary.Author);
        LeakAssert.Clean(summary, ["Keras", "withheld", "lean", "Doom", "Secret quest", "ambition", "Tern Rock", "winks", "effective_level_offset"], "party summary");
    }

    [Fact]
    public void Build_AsAuthor_HasTheSecretsInventionsAndCounts()
    {
        var summary = Build("author");

        Assert.Equal((1, 1, 1), (summary.OpenQuestions, summary.Author!.LeanQuestions, summary.Author.WithheldQuestions));
        Assert.Equal(["Doom", "Storm"], summary.RunningClocks.Select(c => c.Clock.Name));
        Assert.Equal(2, summary.OpenThreadsTotal);
        Assert.Equal(["Belmakor's ambition"], summary.Author!.Secrets.Select(s => s.Entity.Name));
        Assert.Equal(["F1", "F2"], summary.Author.ProposedInventions.Select(i => i.Code));
        Assert.Contains("effective_level_offset", summary.Author.Settings, StringComparison.Ordinal);
        Assert.Equal(["Belmakor", "Keras"], summary.PartyMembers.Select(m => m.Name));
    }

    [Fact]
    public void Build_AsAuthor_CountsTheChangesSinceTheLastPlayedSession()
    {
        Dapper.SqlMapper.Execute(_connection,
            "UPDATE session SET ended_at = '2026-09-01T12:00:00.000Z' WHERE campaign_id = @campaignId AND number = 1", new { campaignId = _campaign.Id });
        _db.Time.Advance(TimeSpan.FromHours(1));
        var place = _seed.Entity(_campaign.Id, "location", "Quay");
        _db.Batch(_campaign.Id, r => r.Update("entity", place.Id, new Dictionary<string, object?> { ["summary"] = "A quay." }, "upsert"));
        _db.Batch(_campaign.Id, r => r.Update("entity", place.Id, new Dictionary<string, object?> { ["summary"] = "The quay." }, "upsert"));

        var author = Build("author").Author!;

        Assert.Equal(2, author.ChangesSinceLastSession);
        Assert.Equal("location:quay summary: \"A quay.\" → \"The quay.\"", author.RecentChanges[0].Changes[0].Text);
    }
}

/// <summary>
/// Invariant: the summary's party members are the CURRENT <c>member_of</c> relations a view may see by the relation rule
/// (contract §3.2: the relation's own visibility admits the view and both ends are visible), the same rule campaign_get
/// applies to the same relation. An author-only membership (the spy secretly in the crew) listed in a player's summary
/// would say what that member's get hides; a planned membership is prep and a former one is not a member now.
/// </summary>
public sealed class CampaignSummaryMembershipTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;
    private readonly SeededEntity _vex;

    public CampaignSummaryMembershipTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Sky", role: CampaignValues.Roles.Dm);
        var hero = _seed.Entity(_campaign.Id, "character", "Hero", subtype: "pc");
        _seed.MemberOf(_campaign.Id, hero.Id, _campaign.Party!.Id);
        _vex = _seed.Entity(_campaign.Id, "character", "Vex", subtype: "npc");
        _seed.Relation(_campaign.Id, _vex.Id, CampaignValues.Rels.MemberOf, _campaign.Party.Id, visibility: V.Author);
        var rook = _seed.Entity(_campaign.Id, "character", "Rook", subtype: "npc");
        _seed.Relation(_campaign.Id, rook.Id, CampaignValues.Rels.MemberOf, _campaign.Party.Id, status: CampaignValues.RelationStatuses.Former);
        var wren = _seed.Entity(_campaign.Id, "character", "Wren", subtype: "npc");
        _seed.Relation(_campaign.Id, wren.Id, CampaignValues.Rels.MemberOf, _campaign.Party.Id, status: CampaignValues.RelationStatuses.Planned);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private CampaignSummaryView Build(string perspective) =>
        new CampaignSummary(_db.Database).Build(_seed.LoadCampaign(_campaign.Id), Perspective.Parse(perspective));

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:hero")]
    public void Build_AuthorOnlyPlannedOrFormerMembership_IsNotAPartyMemberForAPlayerView(string perspective)
    {
        var summary = Build(perspective);
        var vex = Assert.Single(new EntityReader(_db.Database).Get(_seed.LoadCampaign(_campaign.Id), [_vex.Handle], EntityIncludes.All,
            Perspective.Parse(perspective)).Entities);

        Assert.Equal(["Hero"], summary.PartyMembers.Select(m => m.Name));
        Assert.Empty(vex.Relations!);
        LeakAssert.Clean(summary, ["Vex", "Rook", "Wren"], $"summary members as {perspective}");
    }

    [Fact]
    public void Build_AsAuthor_ListsTheAuthorOnlyMemberButNoPlannedOrFormerOne()
    {
        Assert.Equal(["Hero", "Vex"], Build("author").PartyMembers.Select(m => m.Name));
    }
}
