using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: one campaign_write call is one batch: validated before anything is opened, applied in order (an op may use
/// what an earlier op created), all or nothing (a failing op 3 leaves ops 1 and 2 unwritten and consumes no code), a dry
/// run keeps nothing and has no batch, and every change_log row carries the batch id, session, tool, actor and reason.
/// The write path only ever inserts change_log rows, so the append-only triggers never fire.
/// </summary>
public sealed class BatchTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _dm;

    public BatchTests()
    {
        _dm = _f.Campaign("One Piece");
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Apply_FailingOpThree_WritesNothingAtAll()
    {
        var before = _f.Dump();
        var logBefore = _f.Log().Count;

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", CanonStatus = "proposed" },
            Op.Fact("Iron Guts owes the party.", "proposed"),
            new CampaignOpSpec { Op = "link", From = "character:iron-guts", Rel = "ally_of", To = "character:nobody" }));

        Assert.StartsWith("Invalid ops: ops item 3 (link character:iron-guts ally_of character:nobody): to: no entity character:nobody", ex.Message);
        Assert.Equal(before, _f.Dump());
        Assert.Equal(logBefore, _f.Log().Count);
        var next = _f.Apply(_dm, Op.Fact("Now.", "proposed"));
        Assert.Equal("F1", Assert.Single(next.Applied).Code);
    }

    [Fact]
    public void Apply_InvalidBatch_IsRefusedBeforeTheDatabaseIsTouched()
    {
        using var empty = new CampaignTestDb(create: false);
        var writer = new CampaignWriter(empty.Database);
        var campaign = new CampaignRow("id", "x", "X", "dm", "2024", "active", null, null, null, null, null, "{}", "", "", "");

        var ex = Assert.Throws<DndInputException>(() => writer.Apply(campaign,
            [new CampaignOpSpec { Op = "upsert", Kind = "character" }, new CampaignOpSpec { Op = "tick" }], WriteContext.Default));

        Assert.Contains("Invalid ops (2 problems)", ex.Message);
        Assert.False(File.Exists(empty.DatabasePath));
    }

    [Fact]
    public void Apply_OpUsesWhatAnEarlierOpCreated()
    {
        var result = _f.Apply(_dm,
            Op.Upsert("faction", "Mythril Zeppelin", "band"),
            Op.Upsert("character", "Ignis", "npc"),
            Op.Link("character:ignis", "member_of", "faction:mythril-zeppelin"),
            new CampaignOpSpec { Op = "fact", Statement = "Ignis was not born here.", About = ["character:ignis"], KnownBy = [Op.Knower("character:ignis")] });

        Assert.Equal([WriteOutcomes.Created, WriteOutcomes.Created, WriteOutcomes.Linked, WriteOutcomes.Created], result.Applied.Select(a => a.Outcome));
        Assert.Equal([0, 1, 2, 3], result.Applied.Select(a => a.OpIndex));
    }

    [Fact]
    public void DryRun_RunsEveryCheckAndKeepsNothing()
    {
        var before = _f.Dump();

        var result = _f.Apply(_dm, new WriteContext { DryRun = true, Reason = "try" },
            Op.Upsert("character", "Keras"),
            new CampaignOpSpec { Op = "upsert", Ref = "character:keras", Name = "The Old King" },
            Op.Fact("Proposed.", "proposed"));

        Assert.True(result.DryRun);
        Assert.Null(result.BatchId);
        Assert.Equal([WriteOutcomes.Created, WriteOutcomes.Updated, WriteOutcomes.Created], result.Applied.Select(a => a.Outcome));
        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.SlugKept);
        Assert.Equal(before, _f.Dump());
        Assert.Equal(3, _f.Log().Count);
    }

    [Fact]
    public void DryRun_ThatFails_FailsLikeTheRealRun()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new WriteContext { DryRun = true },
            new CampaignOpSpec { Op = "status", Ref = "character:nobody", Status = "dead" }));

        Assert.Contains("ops item 1 (status character:nobody): ref: no entity character:nobody", ex.Message);
    }

    [Fact]
    public void Apply_EveryRowCarriesTheBatchContext()
    {
        _f.Sessions.Start(_dm);

        var result = _f.Apply(_dm, new WriteContext { Reason = "S1 recap", Actor = CampaignValues.Actors.Cli },
            Op.Upsert("character", "Björn Mountainfell", "pc"), Op.Fact("Björn lost an eye."));

        var rows = _f.Log(result.BatchId);
        Assert.Equal(1, result.SessionNumber);
        Assert.Equal(2, rows.Count);
        var session = _f.Entity(_dm, "session:1").Id;
        Assert.All(rows, r =>
        {
            Assert.Equal(result.BatchId, r.BatchId);
            Assert.Equal(_dm.Id, r.CampaignId);
            Assert.Equal(session, r.SessionId);
            Assert.Equal("cli", r.Actor);
            Assert.Equal("campaign_write", r.Tool);
            Assert.Equal("S1 recap", r.Reason);
            Assert.Null(r.UndoOf);
        });
    }

    [Theory]
    [InlineData("upsert", "campaign_write/upsert")]
    [InlineData("fact", "campaign_write/fact")]
    public void Apply_OneOpKind_IsLabelledWithIt(string op, string tool)
    {
        var spec = op == "upsert" ? Op.Upsert("note", "N") : Op.Fact("F.");

        var result = _f.Apply(_dm, spec);

        Assert.All(_f.Log(result.BatchId), r => Assert.Equal(tool, r.Tool));
    }

    [Theory]
    [InlineData("12", "session 12: no such session in this campaign")]
    [InlineData("session:live", "session:live: no session is live")]
    [InlineData("session:last", "session:last: no session has been played yet")]
    [InlineData("tomorrow", "session \"tomorrow\" is not a session")]
    public void Apply_SessionContextThatNamesNoSession_IsRefused(string session, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new WriteContext { Session = session }, Op.Upsert("note", "N")));

        Assert.Contains(expected, ex.Message);
    }

    /// <summary>
    /// The call a session-context refusal prints names the campaign (and the session to start), so sent as printed while
    /// another campaign is current it starts the session in the campaign the write was for.
    /// </summary>
    [Theory]
    [InlineData("12", "Start it if it is being played now: campaign_session {\"action\": \"start\", \"session\": 12, \"campaign\": \"{slug}\"}")]
    [InlineData("session:live", "no session is live; campaign_session {\"action\": \"start\", \"campaign\": \"{slug}\"} starts one")]
    public void Apply_SessionContextThatNamesNoSession_PrintsACallNamingTheCampaign(string session, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new WriteContext { Session = session }, Op.Upsert("note", "N")));

        Assert.Contains(expected.Replace("{slug}", _dm.Slug, StringComparison.Ordinal), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review U08: a missing session is usually tonight's, so the refusal offers start for one being played now,
    /// record_past for one already played and plan for one still to come (plan alone filed tonight under a session that
    /// was not live).
    /// </summary>
    [Fact]
    public void Apply_SessionContextNumberingNoSession_OffersStartRecordPastOrPlanByWhenItIsPlayed()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, WriteContext.For(7), Op.Upsert("note", "N")));

        Assert.Equal("session 7: no such session in this campaign. Start it if it is being played now: campaign_session " +
                     $"{{\"action\": \"start\", \"session\": 7, \"campaign\": \"{_dm.Slug}\"}}. If it was played already, send the same call with " +
                     "\"record_past\" instead of \"start\"; if it is still to come, with \"plan\".", ex.Message);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("session:3")]
    [InlineData("session:last")]
    public void Apply_SessionContext_ByNumberOrHandle(string session)
    {
        _f.Played(_dm, 3);

        var result = _f.Apply(_dm, new WriteContext { Session = session }, Op.Upsert("note", "N"));

        Assert.Equal(3, result.SessionNumber);
        Assert.Equal(_f.Entity(_dm, "session:3").Id, Assert.Single(_f.Log(result.BatchId)).SessionId);
    }

    [Fact]
    public void Apply_ReasonTooLong_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new WriteContext { Reason = new string('r', 1001) }, Op.Upsert("note", "N")));

        Assert.Contains("reason is 1001 characters; at most 1000", ex.Message);
    }

    [Fact]
    public void WritePath_NeverUpdatesOrDeletesChangeLogRows()
    {
        _f.Played(_dm, 1);
        _f.Apply(_dm, Op.Upsert("character", "Tristan", "pc"), Op.Fact("Tristan died.", "played"),
            new CampaignOpSpec { Op = "upsert", Kind = "clock", Name = "C", Clock = new ClockSpec { Segments = 2 } });
        var snapshot = _f.Log();

        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Ref = "character:tristan", Status = "dead" }, new CampaignOpSpec { Op = "tick", Ref = "clock:c" });
        var recorded = _f.Knowledge.Record(_dm, ["f:1"], [Op.Knower("party")], WriteContext.Default);
        _f.History.Undo(_dm, recorded.BatchId!, WriteContext.Default);
        _f.Apply(_dm, new CampaignOpSpec { Op = "unlink", From = "character:tristan", Rel = "x", To = "clock:c" }, new CampaignOpSpec { Op = "delete", Ref = "character:tristan" });

        Assert.Equal(snapshot, _f.Log().Take(snapshot.Count));
        Assert.Equal(snapshot.Count + 5, _f.Log().Count);
    }

    [Theory]
    [InlineData("clock", CampaignValues.Statuses.ClockRunning)]
    [InlineData("clock", CampaignValues.Statuses.ClockDone)]
    [InlineData("beat", CampaignValues.Statuses.BeatPending)]
    [InlineData("character", CampaignValues.Statuses.CharacterAlive)]
    [InlineData("character", CampaignValues.Statuses.CharacterDead)]
    [InlineData("handout", CampaignValues.Statuses.HandoutDelivered)]
    [InlineData("faction", CampaignValues.Statuses.FactionActive)]
    public void StatusConstants_TheWritePathStores_AreInTheirKindsStatusSets(string kind, string status)
    {
        Assert.True(CampaignValues.Statuses.ByKind[kind].Contains(status), $"{kind}: {status}");
    }
}
