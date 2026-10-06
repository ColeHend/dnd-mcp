using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignDb;
using Microsoft.Data.Sqlite;
using Xunit;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: a player view of the sessions lists only played and live sessions it may see, with their recap and
/// attendance by the names it knows and only non-secret dice; prep notes, the live log, secret rolls and the changes made
/// in a session are the author's. The recap lists what a session established and who learned what in it.
/// </summary>
public sealed class SessionReaderTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;
    private readonly SeededSession _s1;
    private readonly SeededEntity _hero;
    private readonly SeededEntity _spy;
    private readonly SeededFact _fact;

    public SessionReaderTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Test", role: CampaignValues.Roles.Player);
        _s1 = _seed.Session(_campaign.Id, 1, title: "The harbor", recap: "They reached the harbor.", prepMd: "The spy is Keras's agent.",
            liveLog: "[{\"at\":\"2026-09-01T20:00:00.000Z\",\"text\":\"they almost guessed the spy\"}]");
        _seed.Session(_campaign.Id, 2, CampaignValues.SessionStatuses.Planned, title: "The spy unmasked");
        _seed.Session(_campaign.Id, 3, CampaignValues.SessionStatuses.Live, title: "The chase");
        _hero = _seed.Entity(_campaign.Id, "character", "Belmakor", subtype: "pc");
        _spy = _seed.Entity(_campaign.Id, "character", "Agent of Keras", visibility: V.Restricted);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: _spy.Id, knownAs: "the dockhand");
        _seed.Attendance(_s1.EntityId, _hero.Id, note: "played twice");
        _seed.Attendance(_s1.EntityId, _spy.Id);
        _seed.DiceRoll(_campaign.Id, _s1.EntityId, "1d20+5", 17, label: "Perception");
        _seed.DiceRoll(_campaign.Id, _s1.EntityId, "1d20", 3, label: "spy's stealth", secret: true);
        _fact = _seed.Fact(_campaign.Id, "The harbor is closed.", establishedSessionId: _s1.EntityId, canonStatus: "played");
        _seed.Knowledge(_campaign.Id, "party", factId: _fact.Id, learnedSessionId: _s1.EntityId);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private SessionReader Reader => new(_db.Database);

    private CampaignRow Row => _seed.LoadCampaign(_campaign.Id);

    [Fact]
    public void List_AsParty_ShowsOnlyPlayedAndLiveSessions()
    {
        var party = Reader.List(Row, perspective: Perspective.Parse("party"));
        var author = Reader.List(Row);

        Assert.Equal([1, 3], party.Sessions.Select(s => s.Number));
        Assert.Equal([1, 2, 3], author.Sessions.Select(s => s.Number));
        Assert.Equal("session:2", author.Sessions[1].Ref);
        LeakAssert.Clean(party, ["unmasked"], "planned session title");
    }

    [Fact]
    public void List_ByStatus_FiltersAndRefusesUnknownStatuses()
    {
        Assert.Equal([2], Reader.List(Row, status: "Planned").Sessions.Select(s => s.Number));
        var ex = Assert.Throws<DndInputException>(() => Reader.List(Row, status: "finished"));
        Assert.Contains("planned, prepped, live, played, cancelled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_AsParty_HasTheRecapAttendanceByKnownNamesAndOnlyOpenRolls()
    {
        var session = Reader.Get(Row, "1", Perspective.Parse("party"));

        Assert.Equal(("session:1", "The harbor", "They reached the harbor."), (session.Session.Ref, session.Session.Title, session.Recap));
        Assert.Equal(["Belmakor", "the dockhand"], session.Attendance.Select(a => a.Character.Name));
        Assert.All(session.Attendance, a => Assert.Null(a.Note));
        var roll = Assert.Single(session.DiceRolls);
        Assert.Equal(("1d20+5", 17L, (bool?)null), (roll.Expression, roll.Total, roll.Secret));
        Assert.Null(session.Author);
        LeakAssert.Clean(session, ["Keras", "spy", "almost guessed", "played twice", "Agent"], "party session");
    }

    [Fact]
    public void Get_AsAuthor_HasPrepLiveLogSecretRollsAndChanges()
    {
        _db.Batch(_campaign.Id, r => r.Update("entity", _hero.Id, new Dictionary<string, object?> { ["status"] = "alive" }, "status"), sessionId: _s1.EntityId);

        var session = Reader.Get(Row, "session:1");

        Assert.Equal(2, session.DiceRolls.Count);
        Assert.Equal("The spy is Keras's agent.", session.Author!.PrepMd);
        Assert.Equal("they almost guessed the spy", Assert.Single(session.Author.LiveLog).Text);
        Assert.Equal("character:belmakor status: (none) → alive", session.Author.Changes.Single().Changes.Single().Text);
        Assert.Equal("played twice", session.Attendance.Single(a => a.Character.Name == "Belmakor").Note);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("session:2")]
    [InlineData("session:9")]
    public void Get_PlannedOrMissingSessionAsParty_IsNotFound(string handle)
    {
        var ex = Assert.Throws<DndInputException>(() => Reader.Get(Row, handle, Perspective.Parse("party")));

        Assert.Contains("for this perspective", ex.Message, StringComparison.Ordinal);
        LeakAssert.CleanMessage(ex.Message, handle, ["unmasked", "spy"], "session not found");
    }

    /// <summary>
    /// The refusal prints a list call the model can send as it is: valid JSON (it once read <c>{action: "list"}</c>) that
    /// names the campaign, so it works while another campaign is current, and for a player view the perspective, so the
    /// list is what that view can see rather than the author's plans.
    /// </summary>
    [Theory]
    [InlineData("author", "9", "No session session:9 in this campaign. campaign_session {\"action\": \"list\", \"campaign\": \"test\"} lists the sessions.")]
    [InlineData("party", "2",
        "No session session:2 for this perspective. campaign_session {\"action\": \"list\", \"perspective\": \"party\", \"campaign\": \"test\"} lists the sessions it can see.")]
    [InlineData("character:belmakor", "session:9",
        "No session session:9 for this perspective. campaign_session {\"action\": \"list\", \"perspective\": \"character:belmakor\", \"campaign\": \"test\"} lists the sessions it can see.")]
    public void Get_NoSuchSessionForTheView_PrintsAListCallThatWorksAsSent(string perspective, string handle, string message)
    {
        var ex = Assert.Throws<DndInputException>(() => Reader.Get(Row, handle, Perspective.Parse(perspective)));

        Assert.Equal(message, ex.Message);
        var call = ex.Message[ex.Message.IndexOf('{', StringComparison.Ordinal)..(ex.Message.LastIndexOf('}') + 1)];
        using var json = System.Text.Json.JsonDocument.Parse(call);
        Assert.Equal("test", json.RootElement.GetProperty("campaign").GetString());
    }

    [Fact]
    public void Get_SessionLiveAndLast_ResolveToTheRightSessions()
    {
        Assert.Equal(3, Reader.Get(Row, "session:live").Session.Number);
        Assert.Equal(1, Reader.Get(Row, "session:last").Session.Number);
    }

    [Fact]
    public void Recap_ListsFactsEstablishedAndKnowledgeLearned()
    {
        var recap = Reader.Recap(Row, "1");

        var fact = Assert.Single(recap.FactsEstablished);
        Assert.Equal((_fact.SeqHandle, "played", "party"), (fact.Ref, fact.CanonStatus, fact.KnownBy.Single()));
        var learned = Assert.Single(recap.KnowledgeLearned);
        Assert.Equal((_fact.SeqHandle, "party", "knows"), (learned.Target, learned.Knower, learned.State));
    }
}
