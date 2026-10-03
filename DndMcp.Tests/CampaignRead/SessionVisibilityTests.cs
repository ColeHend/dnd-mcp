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
/// Invariant: a session exists for a player view only once it is played or live (as of the read's session), and only
/// under the name that view knows it by, through EVERY reader: the session list, a search listing, a text search, a get
/// by <c>session:n</c> or <c>e:n</c>, another entity's session links, and the summary. Sessions are party-visible by
/// default, so a planned session's title ("The betrayal at the lighthouse") is the author's prep sitting in a
/// party-visible row; if one reader judged sessions by visibility alone, it would print the plan to the players. A
/// session the view knows under another name is shown as <c>e:n</c> and cannot be addressed as <c>session:n</c>. A
/// player view's session detail never says attendance was recorded when every attendee is hidden from it, and says how
/// many rolls there were in all.
/// </summary>
public sealed class SessionVisibilityTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;
    private readonly SeededSession _s1;
    private readonly SeededSession _s8;
    private readonly SeededEntity _mira;

    public SessionVisibilityTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Sky", role: CampaignValues.Roles.Dm);
        var hero = _seed.Entity(_campaign.Id, "character", "Hero", subtype: "pc");
        _seed.MemberOf(_campaign.Id, hero.Id, _campaign.Party!.Id);
        _s1 = _seed.Session(_campaign.Id, 1, title: "Arrival", recap: "They arrived.");
        _s8 = _seed.Session(_campaign.Id, 8, CampaignValues.SessionStatuses.Planned, title: "The betrayal at the lighthouse",
            recap: "Mira turns on them.");
        _mira = _seed.Entity(_campaign.Id, "character", "Mira", subtype: "npc", introducedSessionId: _s8.EntityId);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private CampaignRow Row => _seed.LoadCampaign(_campaign.Id);

    private SearchResult Search(string? query, string perspective, IReadOnlyList<string>? kinds = null, int? asOf = null) =>
        new CampaignSearch(_db.Database).Search(Row, new SearchRequest(query, kinds, Perspective: Perspective.Parse(perspective), AsOfSession: asOf, Limit: 50));

    private GetResult Get(string perspective, params string[] refs) =>
        new EntityReader(_db.Database).Get(Row, refs, EntityIncludes.All, Perspective.Parse(perspective));

    public static TheoryData<string> PlayerViews() => new(["party", "table", "character:hero"]);

    [Theory]
    [MemberData(nameof(PlayerViews))]
    public void EveryReader_PlannedPartyVisibleSession_ShowsNothingOfItToAPlayerView(string perspective)
    {
        var list = new SessionReader(_db.Database).List(Row, perspective: Perspective.Parse(perspective));
        var sessionListing = Search(null, perspective, kinds: ["session"]);
        var listing = Search(null, perspective);
        var byTitle = Search("betrayal", perspective);
        var byRecap = Search("turns", perspective);
        var mira = Assert.Single(Get(perspective, _mira.Handle).Entities);
        var summary = new CampaignSummary(_db.Database).Build(Row, Perspective.Parse(perspective));
        var bySessionHandle = Assert.Throws<DndInputException>(() => Get(perspective, "session:8"));
        var bySeq = Assert.Throws<DndInputException>(() => Get(perspective, "e:" + _s8.Seq));
        var detail = Assert.Throws<DndInputException>(() => new SessionReader(_db.Database).Get(Row, "8", Perspective.Parse(perspective)));

        Assert.Equal([1], list.Sessions.Select(s => s.Number));
        Assert.Equal(["session:1"], sessionListing.Entities.Select(e => e.Ref));
        Assert.Empty(byTitle.Entities);
        Assert.Empty(byRecap.Entities);
        Assert.Empty(mira.Sessions!);
        Assert.Equal(1, summary.LastPlayed!.Number);
        foreach (var result in new object[] { list, sessionListing, listing, byTitle, byRecap, mira, summary })
        {
            LeakAssert.Clean(result, ["betrayal", "lighthouse", "turns on", "session:8", "e:" + _s8.Seq], $"planned session as {perspective}");
        }

        foreach (var ex in new[] { bySessionHandle, bySeq, detail })
        {
            LeakAssert.CleanMessage(ex.Message, "session:8", ["betrayal", "lighthouse"], $"planned session refusal as {perspective}");
        }
    }

    [Fact]
    public void EveryReader_PlannedSession_IsThereForTheAuthor()
    {
        var sessionListing = Search(null, "author", kinds: ["session"]);
        var byTitle = Search("betrayal", "author");
        var mira = Assert.Single(Get("author", _mira.Handle).Entities);
        var session = Assert.Single(Get("author", "session:8").Entities);

        Assert.Equal(["session:1", "session:8"], sessionListing.Entities.Select(e => e.Ref));
        Assert.Equal("session:8", Assert.Single(byTitle.Entities).Ref);
        Assert.Equal(("session:8", "introduced"), (mira.Sessions!.Single().Ref, mira.Sessions!.Single().Relation));
        Assert.Equal("The betrayal at the lighthouse", session.DisplayName);
    }

    [Theory]
    [InlineData(CampaignValues.SessionStatuses.Played, true)]
    [InlineData(CampaignValues.SessionStatuses.Live, true)]
    [InlineData(CampaignValues.SessionStatuses.Planned, false)]
    [InlineData(CampaignValues.SessionStatuses.Prepped, false)]
    [InlineData(CampaignValues.SessionStatuses.Cancelled, false)]
    public void Search_SessionOfEachStatus_IsSeenByThePartyOnlyWhenPlayedOrLive(string status, bool seen)
    {
        _seed.Session(_campaign.Id, 2, status, title: "The second night");

        var listing = Search(null, "party", kinds: ["session"]);
        var sessions = new SessionReader(_db.Database).List(Row, perspective: Perspective.Parse("party"));

        Assert.Equal(seen, listing.Entities.Any(e => e.Ref == "session:2"));
        Assert.Equal(seen, sessions.Sessions.Any(s => s.Number == 2));
        Assert.Equal(seen, Search("second night", "party").Entities.Count == 1);
    }

    /// <summary>A session played in session 5 was a plan as of session 4: its status is replayed like any row.</summary>
    [Fact]
    public void Search_SessionPlayedInALaterSession_IsNotSeenAsOfAnEarlierOne()
    {
        var s5 = _seed.Session(_campaign.Id, 5, CampaignValues.SessionStatuses.Planned, title: "The storm");
        _db.Batch(_campaign.Id, r => r.Update("session", s5.EntityId, new Dictionary<string, object?> { ["status"] = CampaignValues.SessionStatuses.Played },
            "session"), sessionId: s5.EntityId);

        Assert.DoesNotContain(Search(null, "party", kinds: ["session"], asOf: 4).Entities, e => e.Ref == "session:5");
        Assert.Contains(Search(null, "party", kinds: ["session"], asOf: 5).Entities, e => e.Ref == "session:5");
        Assert.Contains(Search(null, "party", kinds: ["session"]).Entities, e => e.Ref == "session:5");
        Assert.Contains(Search(null, "author", kinds: ["session"], asOf: 4).Entities, e => e.Ref == "session:5");
    }

    /// <summary>
    /// A played session the party knows under another name is disguised like any entity: <c>e:n</c> and that name, never
    /// <c>session:n</c> (which would tie the name to the session number) or its title; <c>session:1</c> finds nothing.
    /// </summary>
    [Fact]
    public void Get_SessionThePartyKnowsUnderAnotherName_IsENAndNotAddressableAsSessionN()
    {
        var masquerade = _seed.Session(_campaign.Id, 2, title: "The masquerade", recap: "The duke was the killer.");
        _seed.Knowledge(_campaign.Id, "party", state: "unrecognized", entityId: masquerade.EntityId, knownAs: "the night at the docks");

        var listing = Search(null, "party", kinds: ["session"]);
        var bySeq = Assert.Single(Get("party", "e:" + masquerade.Seq).Entities);
        var byNumber = Assert.Throws<DndInputException>(() => Get("party", "session:2"));
        var sessions = new SessionReader(_db.Database).List(Row, perspective: Perspective.Parse("party"));
        var author = Assert.Single(Get("author", "session:2").Entities);

        var hit = Assert.Single(listing.Entities, e => e.DisplayName == "the night at the docks");
        Assert.Equal("e:" + masquerade.Seq, hit.Ref);
        Assert.Equal(("e:" + masquerade.Seq, "the night at the docks"), (bySeq.Ref, bySeq.DisplayName));
        Assert.Contains("nothing by that handle", byNumber.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(sessions.Sessions, s => s.Number == 2);
        Assert.Equal("session:2", author.Ref);
        foreach (var result in new object[] { listing, bySeq, sessions })
        {
            LeakAssert.Clean(result, ["masquerade", "session:2", "killer"], "disguised session");
        }
    }

    /// <summary>
    /// A session is the party's from its own number on (review L02, fix FQ1): a member absent from it, not listed in its
    /// attendance, who joined after it or had left before it has no record of it, and no reader shows it to him (Serif,
    /// absent the night Tristan died, read its recap); a member who was there sees it, and the party's own view is unchanged.
    /// </summary>
    [Theory]
    [InlineData("present", true)]
    [InlineData("absent", false)]
    [InlineData("not listed", false)]
    [InlineData("joined later", false)]
    [InlineData("left before", false)]
    public void EveryReader_SessionAMemberMissed_IsNotTheirs(string how, bool sees)
    {
        var s2 = _seed.Session(_campaign.Id, 2, title: "The ocean job", recap: "Tristan fell to the void octopus.");
        var s3 = _seed.Session(_campaign.Id, 3, title: "Landfall");
        var serif = _seed.Entity(_campaign.Id, "character", "Serif", subtype: "pc");
        _seed.MemberOf(_campaign.Id, serif.Id, _campaign.Party!.Id,
            sinceSessionId: how == "joined later" ? s3.EntityId : null, untilSessionId: how == "left before" ? _s1.EntityId : null);
        var vars = _seed.Entity(_campaign.Id, "character", "Vars", subtype: "pc");
        _seed.MemberOf(_campaign.Id, vars.Id, _campaign.Party.Id);
        _seed.Attendance(s2.EntityId, vars.Id);
        if (how is "present" or "absent")
        {
            _seed.Attendance(s2.EntityId, serif.Id, present: how == "present");
        }

        var sessions = new SessionReader(_db.Database).List(Row, perspective: Perspective.Parse("character:serif"));
        var listing = Search(null, "character:serif", kinds: ["session"]);
        var byRecap = Search("octopus", "character:serif");
        var party = Search("octopus", "party");
        var summary = new CampaignSummary(_db.Database).Build(Row, Perspective.Parse("character:serif"));

        // Landfall (S3) recorded no attendance: a member then knows it was played; one who left in S1 does not (review L02b).
        Assert.Equal(how != "left before", summary.LastPlayed?.Number == 3);
        Assert.Equal(sees, sessions.Sessions.Any(x => x.Number == 2));
        Assert.Equal(sees, listing.Entities.Any(e => e.Ref == "session:2"));
        Assert.Equal(sees, byRecap.Entities.Any(e => e.Ref == "session:2"));
        Assert.Equal("session:2", Assert.Single(party.Entities).Ref);
        if (sees)
        {
            Assert.Equal("Tristan fell to the void octopus.", new SessionReader(_db.Database).Get(Row, "2", Perspective.Parse("character:serif")).Recap);
            return;
        }

        var detail = Assert.Throws<DndInputException>(() => new SessionReader(_db.Database).Get(Row, "2", Perspective.Parse("character:serif")));
        Assert.Contains("No session session:2 for this perspective.", detail.Message, StringComparison.Ordinal);
        Assert.Throws<DndInputException>(() => Get("character:serif", "session:2"));
        foreach (var result in new object[] { sessions, listing, byRecap, summary })
        {
            LeakAssert.Clean(result, ["octopus", "ocean job", "session:2"], $"a session Serif missed ({how})");
        }
    }

    [Fact]
    public void Build_LastPlayedAndLiveSessionsHiddenFromTheParty_AreNotInTheirSummary()
    {
        _seed.Session(_campaign.Id, 2, title: "The secret council", recap: "They plotted.", visibility: V.Author);
        _seed.Session(_campaign.Id, 3, CampaignValues.SessionStatuses.Live, title: "The hidden hour", visibility: V.Author);

        var party = new CampaignSummary(_db.Database).Build(Row, Perspective.Parse("party"));
        var author = new CampaignSummary(_db.Database).Build(Row, Perspective.Author);

        Assert.Equal((1, "They arrived."), (party.LastPlayed!.Number, party.LastRecapOpening));
        Assert.Null(party.LiveSession);
        Assert.Equal((2, "They plotted.", 3), (author.LastPlayed!.Number, author.LastRecapOpening, author.LiveSession!.Number));
        LeakAssert.Clean(party, ["secret council", "plotted", "hidden hour"], "hidden sessions in the party summary");
    }

    [Fact]
    public void Get_EveryAttendeeHiddenFromTheView_SaysNoAttendanceWasRecorded()
    {
        var ghost = _seed.Entity(_campaign.Id, "character", "The Ghost", subtype: "npc", visibility: V.Author);
        _seed.Attendance(_s1.EntityId, ghost.Id);

        var party = new SessionReader(_db.Database).Get(Row, "1", Perspective.Parse("party"));
        var author = new SessionReader(_db.Database).Get(Row, "1");

        Assert.Equal((0, false), (party.Attendance.Count, party.AttendanceRecorded));
        Assert.Equal((1, true), (author.Attendance.Count, author.AttendanceRecorded));
        LeakAssert.Clean(party, ["Ghost"], "hidden attendee");
    }

    [Fact]
    public void Get_DiceRolls_CountOnlyTheRollsTheViewMaySee()
    {
        _seed.DiceRoll(_campaign.Id, _s1.EntityId, "1d20", 12);
        _seed.DiceRoll(_campaign.Id, _s1.EntityId, "2d6", 7);
        _seed.DiceRoll(_campaign.Id, _s1.EntityId, "1d20", 3, secret: true);

        var party = new SessionReader(_db.Database).Get(Row, "1", Perspective.Parse("party"));
        var author = new SessionReader(_db.Database).Get(Row, "1");

        Assert.Equal((2, 2), (party.DiceRolls.Count, party.DiceRollsTotal));
        Assert.Equal((3, 3), (author.DiceRolls.Count, author.DiceRollsTotal));
    }

    [Fact]
    public void Get_MoreRollsThanTheCap_ListsTheNewestAndCountsThemAll()
    {
        for (var i = 1; i <= SessionReader.MaxDiceShown + 1; i++)
        {
            _seed.DiceRoll(_campaign.Id, _s1.EntityId, "1d20", i);
        }

        var detail = new SessionReader(_db.Database).Get(Row, "1", Perspective.Parse("party"));

        Assert.Equal((SessionReader.MaxDiceShown, SessionReader.MaxDiceShown + 1), (detail.DiceRolls.Count, detail.DiceRollsTotal));
        Assert.Equal(2L, detail.DiceRolls[0].Total);
    }
}
