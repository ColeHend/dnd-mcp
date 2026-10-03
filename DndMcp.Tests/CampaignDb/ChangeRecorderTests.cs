using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: every mutation made through <see cref="ChangeRecorder"/> leaves exactly the change_log rows contract §3.5
/// describes: a create with the whole row (JSON columns embedded), one update row per changed column or data key and
/// nothing for unchanged values or updated_at, a delete with the whole row, soft delete and restore as labelled updates
/// of deleted_at, composite keys as JSON arrays, and the entity columns an entity's history is found by. Undo and
/// point-in-time replay read nothing else, so a missing or wrong row here is a change that can never be undone.
/// </summary>
public sealed class ChangeRecorderTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SeededCampaign _campaign;
    private readonly SeededEntity _npc;
    private readonly SeededSession _session;

    public ChangeRecorderTests()
    {
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        _campaign = seed.Campaign();
        _session = seed.Session(_campaign.Id, 4);
        _npc = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Iron Guts", subtype: "npc", status: "alive",
            data: "{\"attitude\":20,\"likes\":[\"ale\"],\"home\":{\"city\":\"Serret\",\"dock\":3}}");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Insert_Entity_LogsACreateWithTheWholeStoredRowAndTheBatchContext()
    {
        IReadOnlyDictionary<string, object?> stored = null!;
        var batch = _db.Batch(_campaign.Id, r => stored = r.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id,
            ["kind"] = "location",
            ["slug"] = "serret",
            ["name"] = "Serret",
            ["parent_id"] = _npc.Id,
            ["data"] = new JsonObject { ["climate"] = "wet" },
            ["sort_key"] = 1,
        }, "upsert"), sessionId: _session.EntityId, reason: "S4 arrival");

        var row = Assert.Single(Log());
        Assert.Equal(CampaignValues.ChangeOps.Create, row.Op);
        Assert.Equal("upsert", row.Action);
        Assert.Equal("entity", row.TargetTable);
        Assert.Equal((string)stored["id"]!, row.TargetId);
        Assert.Equal((string)stored["id"]!, row.EntityId);
        Assert.Equal(_npc.Id, row.OtherEntityId);
        Assert.Null(row.FieldPath);
        Assert.Null(row.OldValue);
        Assert.Equal(batch, row.BatchId);
        Assert.Equal(_campaign.Id, row.CampaignId);
        Assert.Equal(_session.EntityId, row.SessionId);
        Assert.Equal(CampaignValues.Actors.Claude, row.Actor);
        Assert.Equal("test", row.Tool);
        Assert.Equal("S4 arrival", row.Reason);
        Assert.Null(row.UndoOf);
        Assert.Equal(_db.Database.Now(), row.At);

        var snapshot = JsonNode.Parse(row.NewValue!)!.AsObject();
        Assert.Equal(CampaignTables.Entity.Columns.Select(c => c.Name), snapshot.Select(p => p.Key));
        Assert.IsType<JsonObject>(snapshot["data"]);
        Assert.Equal("wet", (string?)snapshot["data"]!["climate"]);
        Assert.Equal((long)stored["seq"]!, (long)snapshot["seq"]!);
        Assert.Equal(1.0, (double)snapshot["sort_key"]!);
        Assert.Equal("canon", (string?)snapshot["canon_status"]);
        Assert.Null(snapshot["deleted_at"]);
        Assert.Equal(_db.Database.Now(), stored["created_at"]);
        Assert.Matches("^[0-9a-f-]{36}$", (string)stored["id"]!);
    }

    [Fact]
    public void Insert_CompositeKey_TargetIdIsAJsonArrayInKeyOrder()
    {
        string factId = null!;
        _db.Batch(_campaign.Id, r =>
        {
            factId = (string)r.Insert("fact", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["statement"] = "Guts owes us." }, "fact")["id"]!;
            r.Insert("fact_link", new Dictionary<string, object?> { ["fact_id"] = factId, ["entity_id"] = _npc.Id, ["role"] = "about" }, "fact");
        });

        var link = Log().Single(r => r.TargetTable == "fact_link");

        Assert.Equal($"[\"{factId}\",\"{_npc.Id}\",\"about\"]", link.TargetId);
        Assert.Equal(_npc.Id, link.EntityId);
        Assert.Null(link.OtherEntityId);
    }

    /// <summary>One row per changed column, old and new as the column's text; unchanged columns and updated_at are not logged.</summary>
    [Fact]
    public void Update_Columns_LogsOneRowPerChangedColumnOnly()
    {
        _db.Time.Advance(TimeSpan.FromMinutes(5));
        var changed = false;
        _db.Batch(_campaign.Id, r => changed = r.Update("entity", _npc.Id, new Dictionary<string, object?>
        {
            ["status"] = "dead",
            ["name"] = "Iron Guts",
            ["sort_key"] = 2.5,
            ["summary"] = "Fell at the docks.",
        }, "status"));

        Assert.True(changed);
        var rows = Log();
        Assert.Equal(new[] { "status", "sort_key", "summary" }, rows.Select(r => r.FieldPath));
        Assert.All(rows, r => Assert.Equal(CampaignValues.ChangeOps.Update, r.Op));
        Assert.Equal(("alive", "dead"), (rows[0].OldValue, rows[0].NewValue));
        Assert.Equal(((string?)null, "2.5"), (rows[1].OldValue, rows[1].NewValue));
        Assert.Equal(("", "Fell at the docks."), (rows[2].OldValue, rows[2].NewValue));
        Assert.All(rows, r => Assert.Equal(_npc.Id, r.EntityId));
        using var connection = _db.Open();
        Assert.Equal(_db.Database.Now(), connection.ExecuteScalar<string>("SELECT updated_at FROM entity WHERE id = @id", new { id = _npc.Id }));
    }

    [Fact]
    public void Update_NothingChanged_ReturnsFalseLogsNothingAndKeepsUpdatedAt()
    {
        _db.Time.Advance(TimeSpan.FromMinutes(5));
        var changed = true;

        _db.Batch(_campaign.Id, r => changed = r.Update("entity", _npc.Id, new Dictionary<string, object?>
        {
            ["status"] = "alive",
            ["data"] = "{\"home\":{\"dock\":3,\"city\":\"Serret\"},\"likes\":[\"ale\"],\"attitude\":20}",
        }, "upsert"));

        Assert.False(changed);
        Assert.Empty(Log());
        using var connection = _db.Open();
        Assert.Equal("2026-09-01T12:00:00.000Z", connection.ExecuteScalar<string>("SELECT updated_at FROM entity WHERE id = @id", new { id = _npc.Id }));
    }

    /// <summary>
    /// A whole new data object logs one row per top-level key that differs: changed (old and new), added (old NULL),
    /// removed (new NULL). Unchanged keys are not logged, so undoing one key cannot clobber another written later.
    /// </summary>
    [Fact]
    public void Update_DataObject_LogsOneRowPerChangedTopLevelKey()
    {
        _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?>
        {
            ["data"] = new JsonObject
            {
                ["attitude"] = -40,
                ["likes"] = new JsonArray("ale"),
                ["oath"] = "revenge",
            },
        }, "upsert"));

        var rows = Log().ToDictionary(r => r.FieldPath!);
        Assert.Equal(new[] { "data.attitude", "data.home", "data.oath" }, rows.Keys.Order());
        Assert.Equal(("20", "-40"), (rows["data.attitude"].OldValue, rows["data.attitude"].NewValue));
        Assert.Equal(("{\"city\":\"Serret\",\"dock\":3}", (string?)null), (rows["data.home"].OldValue, rows["data.home"].NewValue));
        Assert.Equal(((string?)null, "\"revenge\""), (rows["data.oath"].OldValue, rows["data.oath"].NewValue));
    }

    /// <summary>RFC 7396: null removes a key, an object merges into an object, anything else (arrays included) replaces.</summary>
    [Fact]
    public void PatchObject_MergePatch_MergesRemovesAndReplacesPerRfc7396()
    {
        var changed = false;
        _db.Batch(_campaign.Id, r => changed = r.PatchObject("entity", _npc.Id, "data", new JsonObject
        {
            ["attitude"] = null,
            ["home"] = new JsonObject { ["dock"] = null, ["street"] = "Tar Lane" },
            ["likes"] = new JsonArray("rum"),
        }, "upsert"));

        Assert.True(changed);
        using var connection = _db.Open();
        var data = JsonNode.Parse(connection.ExecuteScalar<string>("SELECT data FROM entity WHERE id = @id", new { id = _npc.Id })!)!;
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("{\"likes\":[\"rum\"],\"home\":{\"city\":\"Serret\",\"street\":\"Tar Lane\"}}"), data));
        var rows = Log().ToDictionary(r => r.FieldPath!);
        Assert.Equal(new[] { "data.attitude", "data.home", "data.likes" }, rows.Keys.Order());
        Assert.Null(rows["data.attitude"].NewValue);
        Assert.Equal("{\"city\":\"Serret\",\"street\":\"Tar Lane\"}", rows["data.home"].NewValue);
    }

    [Fact]
    public void PatchObject_NoEffectivePatch_ReturnsFalseAndLogsNothing()
    {
        var changed = true;

        _db.Batch(_campaign.Id, r => changed = r.PatchObject("entity", _npc.Id, "data", new JsonObject
        {
            ["attitude"] = 20,
            ["missing"] = null,
            ["home"] = new JsonObject { ["dock"] = 3 },
        }, "upsert"));

        Assert.False(changed);
        Assert.Empty(Log());
    }

    [Theory]
    [InlineData("entity", "name")]
    [InlineData("session", "live_log")]
    public void PatchObject_NotAnObjectColumn_Throws(string table, string column)
    {
        var id = table == "session" ? _session.EntityId : _npc.Id;

        Assert.Throws<ArgumentException>(() => _db.Batch(_campaign.Id, r => r.PatchObject(table, id, column, new JsonObject(), "upsert")));
    }

    [Fact]
    public void Delete_Row_LogsTheWholeRowAndRemovesIt()
    {
        string relationId = null!;
        using (var connection = _db.Open())
        {
            relationId = new CampaignSeed(connection).Relation(_campaign.Id, _npc.Id, "enemy_of", _campaign.Party!.Id, data: "{\"why\":\"debts\"}");
        }

        var deleted = false;
        var missing = true;
        _db.Batch(_campaign.Id, r =>
        {
            deleted = r.Delete("relation", relationId, "unlink");
            missing = r.Delete("relation", relationId, "unlink");
        });

        Assert.True(deleted);
        Assert.False(missing);
        var row = Assert.Single(Log());
        Assert.Equal(CampaignValues.ChangeOps.Delete, row.Op);
        Assert.Equal(_npc.Id, row.EntityId);
        Assert.Equal(_campaign.Party!.Id, row.OtherEntityId);
        var snapshot = JsonNode.Parse(row.OldValue!)!;
        Assert.Equal("debts", (string?)snapshot["data"]!["why"]);
        Assert.Equal("enemy_of", (string?)snapshot["rel"]);
        Assert.Null(row.NewValue);
        using var check = _db.Open();
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM relation WHERE id = @relationId", new { relationId }));
    }

    /// <summary>Soft delete and restore are updates of deleted_at labelled delete / restore; the FTS row goes and comes back.</summary>
    [Fact]
    public void SoftDeleteAndRestore_Entity_AreLabelledUpdatesOfDeletedAt()
    {
        bool first = false, again = true, restored = false;
        _db.Batch(_campaign.Id, r =>
        {
            first = r.SoftDelete("entity", _npc.Id);
            again = r.SoftDelete("entity", _npc.Id);
        });
        Assert.Equal(0L, FtsCount("guts"));
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        _db.Batch(_campaign.Id, r => restored = r.Restore("entity", _npc.Id));

        Assert.True(first);
        Assert.False(again);
        Assert.True(restored);
        var rows = Log();
        Assert.Equal(new[] { "delete", "restore" }, rows.Select(r => r.Action));
        Assert.All(rows, r => Assert.Equal("deleted_at", r.FieldPath));
        Assert.Equal(((string?)null, "2026-09-01T12:00:00.000Z"), (rows[0].OldValue, rows[0].NewValue));
        Assert.Equal(("2026-09-01T12:00:00.000Z", (string?)null), (rows[1].OldValue, rows[1].NewValue));
        Assert.Equal(1L, FtsCount("guts"));
    }

    /// <summary>
    /// Deleting what a LATER batch finds already deleted changes nothing: deleted_at keeps its first time and no second
    /// "delete" is logged, so the write path can report "already deleted" and history shows one delete. (Within one batch
    /// the timestamps are equal, which hid this; here the clock has moved on.) Restoring what is not deleted is the same.
    /// </summary>
    [Theory]
    [InlineData("entity")]
    [InlineData("fact")]
    public void SoftDelete_AlreadyDeletedInAnEarlierBatch_ReturnsFalseAndWritesAndLogsNothing(string table)
    {
        var id = table == "entity" ? _npc.Id : SeedFact();
        bool first = false, again = true, restoredLive = true;
        _db.Batch(_campaign.Id, r => first = r.SoftDelete(table, id));
        _db.Time.Advance(TimeSpan.FromMinutes(5));

        _db.Batch(_campaign.Id, r => again = r.SoftDelete(table, id));

        Assert.True(first);
        Assert.False(again);
        var row = Assert.Single(Log());
        Assert.Equal(("delete", "2026-09-01T12:00:00.000Z"), (row.Action, row.NewValue));
        using (var check = _db.Open())
        {
            Assert.Equal("2026-09-01T12:00:00.000Z",
                check.ExecuteScalar<string>($"SELECT deleted_at FROM {table} WHERE id = @id", new { id }));
        }

        _db.Batch(_campaign.Id, r => r.Restore(table, id));
        _db.Batch(_campaign.Id, r => restoredLive = r.Restore(table, id));
        Assert.False(restoredLive);
        Assert.Equal(2, Log().Count);
    }

    [Fact]
    public void SoftDelete_NoSuchRow_Throws() =>
        Assert.Throws<InvalidOperationException>(() => _db.Batch(_campaign.Id, r => r.SoftDelete("entity", CampaignDatabase.NewId())));

    [Fact]
    public void SoftDelete_NotAnEntityOrFact_Throws() =>
        Assert.Throws<ArgumentException>(() => _db.Batch(_campaign.Id, r => r.SoftDelete("relation", "x")));

    /// <summary>
    /// A knowledge row is found in the history of its character knower (entity_id) and of the entity it is about
    /// (other_entity_id); a party row has no character.
    /// </summary>
    [Fact]
    public void Insert_KnowledgeRows_CarryTheKnowerAndTheTargetEntity()
    {
        _db.Batch(_campaign.Id, r =>
        {
            r.Insert("knowledge", new Dictionary<string, object?>
            {
                ["campaign_id"] = _campaign.Id, ["entity_id"] = _npc.Id, ["knower_kind"] = "party", ["state"] = "met",
            }, "record");
            r.Insert("knowledge", new Dictionary<string, object?>
            {
                ["campaign_id"] = _campaign.Id, ["entity_id"] = _campaign.Party!.Id, ["knower_kind"] = "character",
                ["knower_id"] = _npc.Id, ["state"] = "knows",
            }, "record");
        });

        var rows = Log();
        Assert.Equal((null, _npc.Id), (rows[0].EntityId, rows[0].OtherEntityId));
        Assert.Equal((_npc.Id, _campaign.Party!.Id), (rows[1].EntityId, rows[1].OtherEntityId));
    }

    /// <summary>
    /// Rows are buffered until Flush, so a gate warning appended after the reveal was written still lands on the reveal's
    /// own rows (they cannot be updated afterwards: change_log is append-only).
    /// </summary>
    [Fact]
    public void AppendReason_AfterRowsWereLogged_AppliesToEveryRowOfTheBatch()
    {
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status");
            r.AppendReason("gate: f:3 revealed before f:2");
            r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["summary"] = "Gone." }, "upsert");
            r.AppendReason("  ");
            r.AppendReason("route r1 complete");
        }, reason: "session 4 reveal");

        Assert.All(Log(), row => Assert.Equal("session 4 reveal; gate: f:3 revealed before f:2; route r1 complete", row.Reason));
    }

    [Fact]
    public void AppendReason_NoCallReason_StartsTheReason()
    {
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status");
            r.AppendReason("warning");
        });

        Assert.Equal("warning", Assert.Single(Log()).Reason);
    }

    /// <summary>One write transaction is one batch: a second recorder on it would split one commit into two undo units.</summary>
    [Fact]
    public void Constructor_SecondRecorderOnTheSameTransaction_Throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _db.Database.Write((connection, transaction) =>
        {
            _ = new ChangeRecorder(connection, transaction, BatchContext.New(_campaign.Id, "claude", null, null, null), _db.Database.Now());
            return new ChangeRecorder(connection, transaction, BatchContext.New(_campaign.Id, "claude", null, null, null), _db.Database.Now());
        }));

        Assert.Contains("one batch", error.Message);
    }

    /// <summary>History that could not be written must not be silently dropped: flushing after the transaction ended throws.</summary>
    [Fact]
    public void Flush_AfterTheTransactionEnded_Throws()
    {
        using var connection = _db.Open();
        var transaction = connection.BeginTransaction();
        var recorder = new ChangeRecorder(connection, transaction, BatchContext.New(_campaign.Id, "claude", null, null, null), _db.Database.Now());
        recorder.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status");
        transaction.Commit();

        Assert.Throws<InvalidOperationException>(recorder.Flush);
        transaction.Dispose();
    }

    public static TheoryData<string, string, object?> BadValues() => new()
    {
        { "entity", "seq", 99L },
        { "entity", "nonexistent", "x" },
        { "entity", "name", 5L },
        { "entity", "name", null },
        { "entity", "sort_key", double.NaN },
        { "entity", "sort_key", double.PositiveInfinity },
        { "entity", "sort_key", "1.5" },
        { "entity", "data", "[1,2]" },
        { "entity", "data", "{not json" },
        { "entity", "id", "other" },
    };

    /// <summary>Caller bugs fail loudly before anything is written: a wrong type would otherwise slip into a STRICT column as '5'.</summary>
    [Theory]
    [MemberData(nameof(BadValues))]
    public void Update_ValueTheColumnCannotHold_ThrowsAndWritesNothing(string table, string column, object? value)
    {
        Assert.Throws<ArgumentException>(() => _db.Batch(_campaign.Id, r =>
            r.Update(table, _npc.Id, new Dictionary<string, object?> { ["status"] = "dead", [column] = value }, "upsert")));

        Assert.Empty(Log());
        using var connection = _db.Open();
        Assert.Equal("alive", connection.ExecuteScalar<string>("SELECT status FROM entity WHERE id = @id", new { id = _npc.Id }));
    }

    [Fact]
    public void Update_MissingRow_Throws() =>
        Assert.Throws<InvalidOperationException>(() => _db.Batch(_campaign.Id, r =>
            r.Update("entity", "no-such-id", new Dictionary<string, object?> { ["status"] = "dead" }, "status")));

    /// <summary>The live log is a scratchpad: appends are written but never logged (the recap is what history keeps).</summary>
    [Fact]
    public void Update_LiveLog_IsWrittenButNotLogged()
    {
        _db.Batch(_campaign.Id, r => r.Update("session", _session.EntityId,
            new Dictionary<string, object?> { ["live_log"] = "[{\"at\":\"x\",\"text\":\"Guts fell\"}]", ["prep_md"] = "Docks." }, "log"));

        Assert.Equal(new[] { "prep_md" }, Log().Select(r => r.FieldPath));
        using var connection = _db.Open();
        Assert.Contains("Guts fell", connection.ExecuteScalar<string>("SELECT live_log FROM session"));
    }

    /// <summary>Booleans and ints are stored as INTEGER longs (STRICT would otherwise accept '1' as text).</summary>
    [Fact]
    public void Insert_BoolAndIntValues_AreStoredAsIntegers()
    {
        _db.Batch(_campaign.Id, r => r.Insert("relation", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["from_id"] = _npc.Id, ["rel"] = "ally_of", ["to_id"] = _campaign.Party!.Id,
            ["symmetric"] = true, ["attitude"] = 15,
        }, "link"));

        using var connection = _db.Open();
        Assert.Equal(("integer", "integer"), connection.QuerySingle<(string, string)>("SELECT typeof(symmetric), typeof(attitude) FROM relation"));
        var snapshot = JsonNode.Parse(Assert.Single(Log()).NewValue!)!;
        Assert.Equal(1L, (long)snapshot["symmetric"]!);
    }

    /// <summary>
    /// A knowledge row per (fact, knower kind, knower): the first write creates it, the second updates the state (one
    /// update row), a third with the same values writes nothing. knower_id NULL matches NULL (the party row), the way the
    /// schema's partial unique index treats it.
    /// </summary>
    [Fact]
    public void Upsert_KnowledgeRowByNaturalKey_CreatesThenUpdatesThenLeavesAlone()
    {
        string factId;
        using (var connection = _db.Open())
        {
            factId = new CampaignSeed(connection).Fact(_campaign.Id, "The seal holds.").Id;
        }

        var match = new Dictionary<string, object?> { ["fact_id"] = factId, ["knower_kind"] = "party", ["knower_id"] = null };
        var results = new List<UpsertResult>();
        _db.Batch(_campaign.Id, r =>
        {
            results.Add(r.Upsert("knowledge", match, new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["state"] = "suspects" }, "record"));
            results.Add(r.Upsert("knowledge", match, new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["state"] = "knows" }, "reveal"));
            results.Add(r.Upsert("knowledge", match, new Dictionary<string, object?> { ["state"] = "knows" }, "reveal"));
        });

        Assert.Equal(new[] { (true, true), (false, true), (false, false) }, results.Select(x => (x.Created, x.Changed)));
        Assert.Equal(results[0].Row["id"], results[2].Row["id"]);
        Assert.Equal("knows", results[2].Row["state"]);
        Assert.Equal(new[] { ("create", (string?)null), ("update", "state") }, Log().Select(x => (x.Op, x.FieldPath)));
        using var check = _db.Open();
        Assert.Equal(1, check.ExecuteScalar<long>("SELECT count(*) FROM knowledge"));
    }

    /// <summary>A NOCASE key column matches whatever the case: "guts" finds the alias "Guts" and updates its visibility.</summary>
    [Fact]
    public void Upsert_AliasDifferentCase_UpdatesTheExistingRow()
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Alias(_npc.Id, "Guts", "public");
        }

        UpsertResult result = null!;
        _db.Batch(_campaign.Id, r => result = r.Upsert("entity_alias",
            new Dictionary<string, object?> { ["entity_id"] = _npc.Id, ["alias"] = "guts" },
            new Dictionary<string, object?> { ["visibility"] = "author" }, "upsert"));

        Assert.False(result.Created);
        Assert.Equal("Guts", result.Row["alias"]);
        var row = Assert.Single(Log());
        Assert.Equal(("visibility", $"[\"{_npc.Id}\",\"Guts\"]"), (row.FieldPath, row.TargetId));
    }

    [Fact]
    public void FindKey_SeveralRowsMatch_Throws()
    {
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.Entity(_campaign.Id, CampaignValues.Kinds.Location, "Dock", slug: "dock-1");
            seed.Entity(_campaign.Id, CampaignValues.Kinds.Location, "Dock", slug: "dock-2");
        }

        Assert.Throws<InvalidOperationException>(() => _db.Batch(_campaign.Id, r =>
            r.FindKey("entity", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["name"] = "Dock" })));
        _db.Batch(_campaign.Id, r =>
            Assert.Null(r.FindKey("entity", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["name"] = "Nowhere" })));
    }

    private string SeedFact()
    {
        using var connection = _db.Open();
        return new CampaignSeed(connection).Fact(_campaign.Id, "Iron Guts owes the party.").Id;
    }

    private long FtsCount(string word)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long>("SELECT count(*) FROM entity_fts WHERE entity_fts MATCH @word", new { word });
    }

    private List<ChangeRow> Log()
    {
        using var connection = _db.Open();
        return connection.Query<ChangeRow>($"SELECT {ChangeRow.Columns} FROM change_log ORDER BY seq").ToList();
    }
}
