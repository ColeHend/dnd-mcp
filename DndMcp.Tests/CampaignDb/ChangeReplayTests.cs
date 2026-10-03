using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: a row "as of session n" is today's row with every change attributed to a later session reversed, newest
/// first; changes with no session (prep, world-building) are timeless and stay; a row created later did not exist; a row
/// deleted later is still there. <c>as_of_session</c> reads ("what did the party know at the end of session 3?") are only
/// as right as this replay.
/// </summary>
public sealed class ChangeReplayTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SeededCampaign _campaign;
    private readonly Dictionary<int, SeededSession> _sessions = [];
    private readonly SeededEntity _pc;
    private readonly List<SqliteConnection> _opened = [];

    public ChangeReplayTests()
    {
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        _campaign = seed.Campaign();
        for (var n = 1; n <= 4; n++)
        {
            _sessions[n] = seed.Session(_campaign.Id, n);
        }

        _pc = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc");
    }

    public void Dispose()
    {
        _opened.ForEach(c => c.Dispose());
        _db.Dispose();
    }

    /// <summary>
    /// Iron Guts: created in prep (timeless), wounded in S2, given a summary between sessions (timeless), killed in S3.
    /// Each session's view shows exactly the changes up to it, and the timeless summary shows in all of them.
    /// </summary>
    [Theory]
    [InlineData(1, "alive", 20)]
    [InlineData(2, "missing", -10)]
    [InlineData(3, "dead", -10)]
    [InlineData(4, "dead", -10)]
    [InlineData(99, "dead", -10)]
    public void RowAsOf_FieldsChangedAcrossSessions_ShowTheStateAtTheEndOfThatSession(int asOf, string status, int attitude)
    {
        var npc = IronGutsHistory();

        var row = ChangeReplay.RowAsOf(Open(), "entity", npc, asOf)!;

        Assert.Equal(status, row["status"]);
        Assert.Equal(attitude, (int)JsonNode.Parse((string)row["data"]!)!["attitude"]!);
        Assert.Equal("Owes the party.", row["summary"]);
    }

    /// <summary>
    /// A session row shares its key with its session entity (session.entity_id is the entity's id): replaying the row
    /// applies only session-table changes. Mixing in the entity's (its summary, its name) would put entity fields on a
    /// session row, or undo the wrong table's values.
    /// </summary>
    [Fact]
    public void RowAsOf_SessionRow_IgnoresChangesToItsEntity()
    {
        var s3 = _sessions[3];
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("session", s3.EntityId, new Dictionary<string, object?> { ["prep_md"] = "new prep" }, "session");
            r.Update("entity", s3.EntityId, new Dictionary<string, object?> { ["summary"] = "Recap line." }, "session");
        }, sessionId: _sessions[4].EntityId);

        var row = ChangeReplay.RowAsOf(Open(), "session", s3.EntityId, 3)!;

        Assert.Equal(string.Empty, row["prep_md"]);
        Assert.Equal(CampaignTables.Session.Columns.Select(c => c.Name).Order(), row.Keys.Order());
        Assert.Equal("new prep", ChangeReplay.RowAsOf(Open(), "session", s3.EntityId, 4)!["prep_md"]);
    }

    /// <summary>The clock twin of the session case: a clock row is keyed by its clock entity's id.</summary>
    [Fact]
    public void RowAsOf_ClockRow_IgnoresChangesToItsEntity()
    {
        SeededEntity clock;
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            clock = seed.Entity(_campaign.Id, CampaignValues.Kinds.Clock, "The Seal Weakens");
            seed.Clock(clock.Id, segments: 6, filled: 1);
        }

        _db.Batch(_campaign.Id, r =>
        {
            r.Update("clock", clock.Id, new Dictionary<string, object?> { ["filled"] = 3 }, "tick");
            r.Update("entity", clock.Id, new Dictionary<string, object?> { ["name"] = "The Seal Breaks", ["summary"] = "Soon." }, "tick");
        }, sessionId: _sessions[4].EntityId);

        var row = ChangeReplay.RowAsOf(Open(), "clock", clock.Id, 3)!;

        Assert.Equal(1L, row["filled"]);
        Assert.Equal(CampaignTables.Clock.Columns.Select(c => c.Name).Order(), row.Keys.Order());
        Assert.Equal(3L, ChangeReplay.RowAsOf(Open(), "clock", clock.Id, 4)!["filled"]);
    }

    /// <summary>A row created in a later session did not exist yet; a row created in prep (no session) always existed.</summary>
    [Fact]
    public void RowAsOf_CreatedInALaterSession_IsAbsentAndPrepCreationsAreTimeless()
    {
        string later = null!;
        string prep = null!;
        _db.Batch(_campaign.Id, r => prep = Create(r, "Serret"));
        _db.Batch(_campaign.Id, r => later = Create(r, "Dinosaur Island"), sessionId: _sessions[3].EntityId);

        using var connection = _db.Open();
        Assert.Null(ChangeReplay.RowAsOf(connection, "entity", later, 2));
        Assert.Equal("Dinosaur Island", ChangeReplay.RowAsOf(connection, "entity", later, 3)!["name"]);
        Assert.Equal("Serret", ChangeReplay.RowAsOf(connection, "entity", prep, 0)!["name"]);
    }

    /// <summary>A row hard-deleted later comes back from its logged snapshot; one soft-deleted later is not yet deleted.</summary>
    [Fact]
    public void RowAsOf_DeletedInALaterSession_IsStillThere()
    {
        string relation;
        SeededEntity doomed;
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            doomed = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Tristan");
            relation = seed.Relation(_campaign.Id, _pc.Id, "ally_of", doomed.Id, data: "{\"since\":\"S1\"}");
        }

        _db.Batch(_campaign.Id, r =>
        {
            r.Delete("relation", relation, "unlink");
            r.SoftDelete("entity", doomed.Id);
        }, sessionId: _sessions[4].EntityId);

        using var check = _db.Open();
        var relationThen = ChangeReplay.RowAsOf(check, "relation", relation, 3)!;
        Assert.Equal("ally_of", relationThen["rel"]);
        Assert.Equal("{\"since\":\"S1\"}", relationThen["data"]);
        Assert.Null(ChangeReplay.RowAsOf(check, "relation", relation, 4));
        Assert.Null(ChangeReplay.RowAsOf(check, "entity", doomed.Id, 3)!["deleted_at"]);
        Assert.NotNull(ChangeReplay.RowAsOf(check, "entity", doomed.Id, 4)!["deleted_at"]);
    }

    /// <summary>Composite keys replay by their JSON-array target id, and a data key added later is absent before.</summary>
    [Fact]
    public void RowAsOf_CompositeKeyAndDataKeyAddedLater_AreReplayed()
    {
        _db.Batch(_campaign.Id, r =>
        {
            r.Insert("entity_alias", new Dictionary<string, object?> { ["entity_id"] = _pc.Id, ["alias"] = "Bel" }, "upsert");
            r.PatchObject("entity", _pc.Id, "data", new JsonObject { ["instrument"] = "lute" }, "upsert");
        }, sessionId: _sessions[2].EntityId);

        using var connection = _db.Open();
        var alias = CampaignTables.EntityAlias.TargetId([_pc.Id, "Bel"]);
        Assert.Null(ChangeReplay.RowAsOf(connection, "entity_alias", alias, 1));
        Assert.Equal("Bel", ChangeReplay.RowAsOf(connection, "entity_alias", alias, 2)!["alias"]);
        Assert.Equal("{}", ChangeReplay.RowAsOf(connection, "entity", _pc.Id, 1)!["data"]);
    }

    [Fact]
    public void RowsAsOf_ManyRows_ReturnsEveryRequestedIdWithNullForAbsentOnes()
    {
        var npc = IronGutsHistory();

        var rows = ChangeReplay.RowsAsOf(Open(), "entity", [npc, _pc.Id, "no-such-id"], 2);

        Assert.Equal(3, rows.Count);
        Assert.Equal("missing", rows[npc]!["status"]);
        Assert.Equal("Belmakor Silverwind", rows[_pc.Id]!["name"]);
        Assert.Null(rows["no-such-id"]);
    }

    /// <summary>An undo in a later session is itself reversed as of an earlier one: the replay reads undo rows like any other.</summary>
    [Fact]
    public void RowAsOf_UndoInALaterSession_IsReversedToo()
    {
        var npc = IronGutsHistory();
        var kill = Log().Last(r => r.FieldPath == "status").BatchId;
        _db.Undo(_campaign.Id, kill, sessionId: _sessions[4].EntityId);

        using var connection = _db.Open();
        Assert.Equal("missing", ChangeReplay.RowAsOf(connection, "entity", npc, 4)!["status"]);
        Assert.Equal("dead", ChangeReplay.RowAsOf(connection, "entity", npc, 3)!["status"]);
    }

    /// <summary>A change whose session no longer exists cannot be placed in time, so it is treated as timeless.</summary>
    [Fact]
    public void RowAsOf_ChangeAttributedToAMissingSession_IsTimeless()
    {
        _db.Batch(_campaign.Id, r => r.Update("entity", _pc.Id, new Dictionary<string, object?> { ["summary"] = "Sings." }, "upsert"),
            sessionId: "0199ffff-0000-7000-8000-000000000000");

        Assert.Equal("Sings.", ChangeReplay.RowAsOf(Open(), "entity", _pc.Id, 0)!["summary"]);
    }

    private string IronGutsHistory()
    {
        string npc = null!;
        _db.Batch(_campaign.Id, r => npc = Create(r, "Iron Guts", "{\"attitude\":20}", "alive"));
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity", npc, new Dictionary<string, object?> { ["status"] = "missing" }, "status");
            r.PatchObject("entity", npc, "data", new JsonObject { ["attitude"] = -10 }, "upsert");
        }, sessionId: _sessions[2].EntityId);
        _db.Batch(_campaign.Id, r => r.Update("entity", npc, new Dictionary<string, object?> { ["summary"] = "Owes the party." }, "upsert"));
        _db.Batch(_campaign.Id, r => r.Update("entity", npc, new Dictionary<string, object?> { ["status"] = "dead" }, "status"),
            sessionId: _sessions[3].EntityId);
        return npc;
    }

    private string Create(ChangeRecorder r, string name, string data = "{}", string? status = null) =>
        (string)r.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["kind"] = "character", ["slug"] = CampaignSlugs.From(name, "character"),
            ["name"] = name, ["data"] = data, ["status"] = status,
        }, "upsert")["id"]!;

    private List<ChangeRow> Log()
    {
        using var connection = _db.Open();
        return connection.Query<ChangeRow>($"SELECT {ChangeRow.Columns} FROM change_log ORDER BY seq").ToList();
    }

    // A connection closed with the test (for one-line assertions).
    private SqliteConnection Open()
    {
        var connection = _db.Open();
        _opened.Add(connection);
        return connection;
    }
}
