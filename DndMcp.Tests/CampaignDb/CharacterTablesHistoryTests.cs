using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: the four Phase 7 logged tables (character_sheet, holding, currency_txn, award) are history like every 0001
/// table, through the generic recorder alone: every create, column update, per-key tracker update and delete is logged
/// under the catalogue's entity columns; undo puts each back exactly (REAL quantities and JSON documents included) and a
/// redo re-applies it; a point-in-time read replays them by session; and undo's conflict rules hold at the grain the
/// end-of-combat write-back needs (the same field or the same tracker key refuses, another key or column does not, a
/// later row naming a created award, holding or coin entry refuses). If any of this breaks, undoing a fight's write-back
/// either clobbers a later edit, refuses for no reason, or leaves the loot behind.
/// </summary>
public sealed class CharacterTablesHistoryTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SeededCampaign _campaign;
    private readonly SeededEntity _pc;
    private readonly SeededEntity _item;
    private readonly Dictionary<int, SeededSession> _sessions = [];

    public CharacterTablesHistoryTests()
    {
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        _campaign = seed.Campaign(ruleset: CampaignValues.Rulesets.R2014);
        for (var n = 1; n <= 3; n++)
        {
            _sessions[n] = seed.Session(_campaign.Id, n);
        }

        _pc = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc", status: "alive");
        _item = seed.Entity(_campaign.Id, CampaignValues.Kinds.Item, "Ring of protection", subtype: "magic");
    }

    public void Dispose() => _db.Dispose();

    public static TheoryData<string> Tables() => new() { "character_sheet", "holding", "currency_txn", "award" };

    /// <summary>
    /// A create is one change_log row naming the row's entities (the character for its sheet, the holder and the item, the
    /// recipient and the session), so an entity's history finds it; undoing it removes the row and nothing else.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void Undo_CreateOfALoggedRow_LeavesNothingBehind(string table)
    {
        var before = Snapshot();
        string target = null!;
        var batch = _db.Batch(_campaign.Id, r => target = Create(r, table), sessionId: _sessions[2].EntityId);

        var created = Assert.Single(Log(batch));
        Assert.Equal((CampaignValues.ChangeOps.Create, table, target), (created.Op, created.TargetTable, created.TargetId));
        Assert.Equal(_pc.Id, created.EntityId);
        Assert.Equal(table switch
        {
            "holding" => _item.Id,
            "currency_txn" or "award" => _sessions[2].EntityId,
            _ => null,
        }, created.OtherEntityId);

        _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Snapshot());
    }

    /// <summary>A hard delete comes back as the logged row, every column as it was (the REAL quantity, the JSON documents).</summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void Undo_DeleteOfALoggedRow_ReinsertsItExactly(string table)
    {
        string target = null!;
        _db.Batch(_campaign.Id, r => target = Create(r, table));
        var before = Snapshot();
        var batch = _db.Batch(_campaign.Id, r => Assert.True(r.Delete(table, target, "update")));
        Assert.Equal(0, Count(table));

        _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Snapshot());
    }

    public static TheoryData<string, string, string> PerKeyChanges() => new()
    {
        { "abilities", "dex", "18" },
        { "hit_dice", "d6", "{\"max\":12,\"used\":3}" },
        { "spell_slots", "3", "{\"max\":3,\"used\":1}" },
        { "resources", "bladesong", "{\"name\":\"Bladesong\",\"max\":4,\"used\":1,\"recharge\":\"long_rest\"}" },
    };

    /// <summary>
    /// The sheet's keyed trackers log one row per changed key (<c>spell_slots.3</c>), the old and new value being that
    /// key's JSON, through a merge patch or a whole-document update alike; undo restores exactly that key.
    /// </summary>
    [Theory]
    [MemberData(nameof(PerKeyChanges))]
    public void Update_PerKeyTrackerColumn_LogsTheChangedKeyAndUndoRestoresIt(string column, string key, string value)
    {
        _db.Batch(_campaign.Id, r => CreateSheet(r));
        var before = Snapshot();
        var old = Key(column, key);

        var batch = _db.Batch(_campaign.Id, r => r.PatchObject("character_sheet", _pc.Id, column, new JsonObject { [key] = JsonNode.Parse(value) }, "use"));

        var row = Assert.Single(Log(batch));
        Assert.Equal((CampaignValues.ChangeOps.Update, $"{column}.{key}", _pc.Id), (row.Op, row.FieldPath, row.EntityId));
        Assert.Equal(old, row.OldValue);
        Assert.Equal(value, row.NewValue);
        _db.Undo(_campaign.Id, batch);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>A whole new tracker document logs only the keys that changed, one row each (a key removed logs NULL).</summary>
    [Fact]
    public void Update_WholeSpellSlotsDocument_LogsOneRowPerChangedKey()
    {
        _db.Batch(_campaign.Id, r => CreateSheet(r));

        var batch = _db.Batch(_campaign.Id, r => r.Update("character_sheet", _pc.Id, new Dictionary<string, object?>
        {
            ["spell_slots"] = "{\"1\":{\"max\":4,\"used\":2},\"3\":{\"max\":3,\"used\":0},\"pact\":{\"level\":3,\"max\":2,\"used\":0}}",
        }, "update"));

        Assert.Equal(
            new[] { ("spell_slots.1", "{\"max\":4,\"used\":2}"), ("spell_slots.6", (string?)null), ("spell_slots.pact", "{\"level\":3,\"max\":2,\"used\":0}") },
            Log(batch).Select(r => (r.FieldPath!, r.NewValue)).Order().ToArray());
    }

    public static TheoryData<string, string, object?> ColumnChanges() => new()
    {
        { "character_sheet", "hp", 41L },
        { "character_sheet", "temp_hp", 5L },
        { "character_sheet", "xp", 3200L },
        { "character_sheet", "max_hp_reduction", 7L },
        { "character_sheet", "exhaustion", 2L },
        { "character_sheet", "death_saves", "{\"successes\":1,\"failures\":2,\"stable\":false}" },
        { "character_sheet", "conditions", "[{\"name\":\"cursed (Mucus Cloud)\",\"source\":\"Aboleth\",\"duration\":\"until_removed\"}]" },
        { "character_sheet", "concentration", "{\"spell\":\"Circle of Power\",\"level\":5,\"remaining_rounds\":98}" },
        { "character_sheet", "sim_profile", null },
        { "character_sheet", "level", null },
        { "holding", "quantity", 0.5 },
        { "holding", "charges", "{\"max\":7,\"used\":3}" },
        { "holding", "equipped", 0L },
        { "award", "amount", 900L },
    };

    /// <summary>
    /// Every other column logs whole, under its own name (the JSON arrays and objects as their JSON, a REAL in round-trip
    /// digits, a NULL as NULL), and undo writes the old value back exactly.
    /// </summary>
    [Theory]
    [MemberData(nameof(ColumnChanges))]
    public void Update_Column_LogsTheColumnAndUndoRestoresIt(string table, string column, object? value)
    {
        string target = null!;
        _db.Batch(_campaign.Id, r => target = Create(r, table));
        var before = Snapshot();

        var batch = _db.Batch(_campaign.Id, r => Assert.True(r.Update(table, target, new Dictionary<string, object?> { [column] = value }, "update")));

        var row = Assert.Single(Log(batch));
        Assert.Equal(column, row.FieldPath);
        Assert.Equal(value switch { null => null, double d => d.ToString("R", CultureInfo.InvariantCulture), _ => Convert.ToString(value, CultureInfo.InvariantCulture) }, row.NewValue);
        _db.Undo(_campaign.Id, batch);
        Assert.Equal(before, Snapshot());
    }

    public static TheoryData<string, string, bool> LaterSheetEdits() => new()
    {
        { "hp", "hp", true },
        { "hp", "max_hp", false },
        { "spell_slots.3", "spell_slots.3", true },
        { "spell_slots.3", "spell_slots.1", false },
        { "resources.bladesong", "resources.bladesong", true },
        { "resources.bladesong", "resources.arcane-recovery", false },
        { "conditions", "conditions", true },
        { "spell_slots.3", "hp", false },
    };

    /// <summary>
    /// The write-back grain (contract D6, INFRA Correction 4): a later batch that changed the SAME field (or the same
    /// tracker key) makes undoing an earlier sheet batch refuse, listing it, and change nothing; one that changed another
    /// column or another key of the same tracker does not block it, and after the undo that later change is still there.
    /// </summary>
    [Theory]
    [MemberData(nameof(LaterSheetEdits))]
    public void Undo_SheetBatchWithALaterEdit_ConflictsExactlyOnTheSameField(string earlier, string later, bool conflicts)
    {
        _db.Batch(_campaign.Id, r => CreateSheet(r));
        var batch = _db.Batch(_campaign.Id, r => Change(r, earlier, 1));
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        var laterBatch = _db.Batch(_campaign.Id, r => Change(r, later, 2));
        var after = Snapshot();

        var error = Record.Exception(() => _db.Undo(_campaign.Id, batch));

        if (conflicts)
        {
            var refusal = Assert.IsType<DndInputException>(error);
            Assert.Contains(laterBatch, refusal.Message);
            Assert.Contains($"changes a character sheet row {later}", refusal.Message);
            Assert.Equal(after, Snapshot());
        }
        else
        {
            Assert.Null(error);
            using var connection = _db.Open();
            var sheet = CurrentSheet(connection);
            Assert.Equal(Expected(later, 2), Value(sheet, later));
        }
    }

    public static TheoryData<string, string> LaterMentions() => new()
    {
        { "award", "update" },
        { "award", "delete" },
        { "award", "mention" },
        { "holding", "update" },
        { "holding", "delete" },
        { "holding", "mention" },
        { "currency_txn", "delete" },
        { "currency_txn", "mention" },
    };

    /// <summary>
    /// A created award, holding or coin entry is the write-back's loot: a later batch that changed or removed it, or any
    /// later row naming its id, builds on it, so undoing the batch that created it is refused (undoing it would delete a
    /// row something later points at).
    /// </summary>
    [Theory]
    [MemberData(nameof(LaterMentions))]
    public void Undo_LaterBatchBuildingOnACreatedLootRow_IsRefused(string table, string how)
    {
        string target = null!;
        var batch = _db.Batch(_campaign.Id, r => target = Create(r, table));
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        var later = _db.Batch(_campaign.Id, r =>
        {
            switch (how)
            {
                case "update":
                    r.Update(table, target, new Dictionary<string, object?> { [table == "award" ? "note" : "notes"] = "given to Serif" }, "update");
                    break;
                case "delete":
                    r.Delete(table, target, "update");
                    break;
                default:
                    r.PatchObject("entity", _pc.Id, "data", new JsonObject { ["owes"] = target }, "upsert");
                    break;
            }
        });
        var after = Snapshot();

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains(later, error.Message);
        Assert.DoesNotContain("Belmakor", error.Message);
        Assert.Equal(after, Snapshot());
    }

    public static TheoryData<int, long, string, long> SheetAsOf() => new()
    {
        { 1, 98, "{\"max\":3,\"used\":0}", 0 },
        { 2, 41, "{\"max\":3,\"used\":1}", 0 },
        { 3, 60, "{\"max\":3,\"used\":1}", 1 },
        { 99, 60, "{\"max\":3,\"used\":1}", 1 },
    };

    /// <summary>
    /// A sheet as of the end of session n: written in prep (timeless), hit and a 3rd-level slot spent in session 2, healed
    /// and Bladesong used in session 3. Each session's view replays the columns and the per-key trackers, and the replayed
    /// row binds to <see cref="CharacterSheetRow"/> exactly as a current read does.
    /// </summary>
    [Theory]
    [MemberData(nameof(SheetAsOf))]
    public void RowAsOf_Sheet_ReplaysColumnsAndTrackerKeysBySession(int asOf, long hp, string slot3, long bladesongUsed)
    {
        _db.Batch(_campaign.Id, r => CreateSheet(r));
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("character_sheet", _pc.Id, new Dictionary<string, object?> { ["hp"] = 41 }, "damage");
            r.PatchObject("character_sheet", _pc.Id, "spell_slots", new JsonObject { ["3"] = new JsonObject { ["max"] = 3, ["used"] = 1 } }, "use");
        }, sessionId: _sessions[2].EntityId);
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("character_sheet", _pc.Id, new Dictionary<string, object?> { ["hp"] = 60 }, "heal");
            r.PatchObject("character_sheet", _pc.Id, "resources", new JsonObject { ["bladesong"] = new JsonObject { ["used"] = 1 } }, "use");
        }, sessionId: _sessions[3].EntityId);

        using var connection = _db.Open();
        var row = CampaignRows.FromValues<CharacterSheetRow>(ChangeReplay.RowAsOf(connection, "character_sheet", _pc.Id, asOf)!);

        Assert.Equal(hp, row.Hp);
        Assert.Equal(slot3, JsonNode.Parse(row.SpellSlots)!["3"]!.ToJsonString());
        Assert.Equal(bladesongUsed, (long)JsonNode.Parse(row.Resources)!["bladesong"]!["used"]!);
        Assert.Equal(98L, row.MaxHp);
    }

    public static TheoryData<string, int, bool> LootAsOf()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (var table in new[] { "holding", "currency_txn", "award" })
        {
            data.Add(table, 1, false);
            data.Add(table, 2, true);
            data.Add(table, 3, false);
        }

        return data;
    }

    /// <summary>
    /// A loot row created in session 2 and deleted in session 3 did not exist as of session 1, existed as of 2 (rebuilt
    /// from its delete snapshot, the REAL quantity and JSON charges included, and bound to its row record), and is gone
    /// as of 3.
    /// </summary>
    [Theory]
    [MemberData(nameof(LootAsOf))]
    public void RowAsOf_LootCreatedAndDeletedInLaterSessions_ExistsExactlyInBetween(string table, int asOf, bool exists)
    {
        string target = null!;
        _db.Batch(_campaign.Id, r => target = Create(r, table), sessionId: _sessions[2].EntityId);
        string current;
        using (var connection = _db.Open())
        {
            current = Describe(table, ChangeReplay.RowAsOf(connection, table, target, 99)!);
        }

        _db.Batch(_campaign.Id, r => r.Delete(table, target, "update"), sessionId: _sessions[3].EntityId);

        using var check = _db.Open();
        var row = ChangeReplay.RowAsOf(check, table, target, asOf);
        Assert.Equal(exists, row is not null);
        if (row is not null)
        {
            Assert.Equal(current, Describe(table, row));
        }
    }

    /// <summary>
    /// The write-back in miniature (U1): one batch changes the sheet's combat-owned fields and tracker keys and files the
    /// fight's XP, coins and loot; undoing it puts every table back, and undoing the undo (redo) gives exactly the
    /// written state again, under the same ids.
    /// </summary>
    [Fact]
    public void Undo_OfAWriteBackShapedBatch_RestoresEverythingAndRedoReappliesIt()
    {
        _db.Batch(_campaign.Id, r => CreateSheet(r));
        var before = Snapshot();
        var writeBack = _db.Batch(_campaign.Id, r =>
        {
            r.Update("character_sheet", _pc.Id, new Dictionary<string, object?>
            {
                ["hp"] = 0, ["temp_hp"] = 0, ["death_saves"] = "{\"successes\":1,\"failures\":1,\"stable\":false}", ["exhaustion"] = 1,
                ["xp"] = 4350, ["conditions"] = "[{\"name\":\"cursed (Mucus Cloud)\",\"source\":\"Aboleth\",\"duration\":\"until_removed\"}]",
            }, "combat_end");
            r.PatchObject("character_sheet", _pc.Id, "spell_slots", new JsonObject { ["3"] = new JsonObject { ["used"] = 2 } }, "combat_end");
            r.PatchObject("character_sheet", _pc.Id, "resources", new JsonObject { ["bladesong"] = new JsonObject { ["used"] = 1 } }, "combat_end");
            Create(r, "award");
            Create(r, "currency_txn");
            Create(r, "holding");
        }, sessionId: _sessions[3].EntityId);
        var after = Snapshot();

        var undo = _db.Undo(_campaign.Id, writeBack);
        Assert.Equal(before, Snapshot());
        _db.Undo(_campaign.Id, undo.UndoBatchId);

        Assert.Equal(after, Snapshot());
    }

    // Creates one row of the table for the PC; returns its key (the PC's id for a sheet).
    private string Create(ChangeRecorder r, string table) => table switch
    {
        "character_sheet" => CreateSheet(r),
        "holding" => (string)r.Insert("holding", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["holder_id"] = _pc.Id, ["item_id"] = _item.Id, ["name"] = "Ring of protection",
            ["quantity"] = 2.5, ["equipped"] = true, ["charges"] = new JsonObject { ["max"] = 7, ["used"] = 2 },
            ["acquired_session_id"] = _sessions[1].EntityId, ["notes"] = "from the crypt",
        }, "inventory")["id"]!,
        "currency_txn" => (string)r.Insert("currency_txn", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["holder_id"] = _pc.Id, ["session_id"] = _sessions[2].EntityId, ["gp"] = 150, ["sp"] = -3,
            ["note"] = "The crypt",
        }, "currency")["id"]!,
        "award" => (string)r.Insert("award", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["recipient_id"] = _pc.Id, ["session_id"] = _sessions[2].EntityId, ["kind"] = "xp",
            ["amount"] = 450, ["note"] = "share", ["source"] = "encounter: The crypt",
        }, "award")["id"]!,
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
    };

    private string CreateSheet(ChangeRecorder r) =>
        (string)r.Insert("character_sheet", new Dictionary<string, object?>
        {
            ["entity_id"] = _pc.Id, ["ruleset"] = "2014", ["species"] = "elf", ["level"] = 12, ["xp"] = 3900,
            ["classes"] = new JsonArray(new JsonObject { ["class"] = "wizard", ["subclass"] = "bladesinger", ["level"] = 12 }),
            ["abilities"] = "{\"str\":11,\"dex\":20,\"con\":16,\"int\":20}", ["ac"] = 15, ["max_hp"] = 98, ["hp"] = 98,
            ["hit_dice"] = "{\"d6\":{\"max\":12,\"used\":0}}",
            ["spell_slots"] = "{\"1\":{\"max\":4,\"used\":0},\"3\":{\"max\":3,\"used\":0},\"6\":{\"max\":1,\"used\":0}}",
            ["resources"] = "{\"bladesong\":{\"name\":\"Bladesong\",\"max\":4,\"used\":0,\"recharge\":\"long_rest\"}," +
                            "\"arcane-recovery\":{\"name\":\"Arcane Recovery\",\"max\":1,\"used\":0,\"recharge\":\"long_rest\"}}",
            ["sim_profile"] = "{\"class\":\"wizard\"}",
        }, "update")["entity_id"]!;

    // One sheet change by field path: a column takes `n`-dependent values, a tracker key a document whose used is n.
    private void Change(ChangeRecorder r, string field, int n)
    {
        var dot = field.IndexOf('.');
        if (dot > 0)
        {
            r.PatchObject("character_sheet", _pc.Id, field[..dot], new JsonObject { [field[(dot + 1)..]] = new JsonObject { ["used"] = n } }, "use");
            return;
        }

        r.Update("character_sheet", _pc.Id, new Dictionary<string, object?> { [field] = Expected(field, n) }, "update");
    }

    private static object Expected(string field, int n) => field switch
    {
        "conditions" => $"[{{\"name\":\"poisoned\",\"note\":\"{n}\"}}]",
        _ when field.Contains('.', StringComparison.Ordinal) => (long)n,
        _ => 40L + n,
    };

    private static object? Value(IReadOnlyDictionary<string, object?> sheet, string field)
    {
        var dot = field.IndexOf('.');
        if (dot < 0)
        {
            return sheet[field];
        }

        return (long)JsonNode.Parse((string)sheet[field[..dot]]!)![field[(dot + 1)..]]!["used"]!;
    }

    private string? Key(string column, string key)
    {
        using var connection = _db.Open();
        var document = JsonNode.Parse(connection.ExecuteScalar<string>($"SELECT {column} FROM character_sheet WHERE entity_id = @id", new { id = _pc.Id })!)!;
        return document[key]?.ToJsonString(CampaignLogJson.Options);
    }

    private IReadOnlyDictionary<string, object?> CurrentSheet(Microsoft.Data.Sqlite.SqliteConnection connection) =>
        ChangeReplay.RowAsOf(connection, "character_sheet", _pc.Id, int.MaxValue)!;

    // A replayed row as its record's text (the record binds, so every column has its stored type).
    private static string Describe(string table, IReadOnlyDictionary<string, object?> row) => table switch
    {
        "holding" => CampaignRows.FromValues<HoldingRow>(row).ToString(),
        "currency_txn" => CampaignRows.FromValues<CurrencyTxnRow>(row).ToString(),
        "award" => CampaignRows.FromValues<AwardRow>(row).ToString(),
        _ => CampaignRows.FromValues<CharacterSheetRow>(row).ToString(),
    };

    private long Count(string table)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long>($"SELECT count(*) FROM {table}");
    }

    private List<ChangeRow> Log(string batchId)
    {
        using var connection = _db.Open();
        return connection.Query<ChangeRow>($"SELECT {ChangeRow.Columns} FROM change_log WHERE batch_id = @batchId ORDER BY seq", new { batchId }).ToList();
    }

    // Every loggable table (updated_at aside: an undo stamps its own time), as text.
    private string Snapshot()
    {
        using var connection = _db.Open();
        var text = new StringBuilder();
        foreach (var table in CampaignTables.All)
        {
            var columns = string.Join(", ", table.Columns.Where(c => c.Name != "updated_at").Select(c => c.Name));
            text.AppendLine("## " + table.Name);
            foreach (var row in connection.Query($"SELECT {columns} FROM {table.Name} ORDER BY {string.Join(", ", table.KeyColumns)}"))
            {
                text.AppendLine(string.Join(" | ", ((IDictionary<string, object?>)row).Select(p =>
                    $"{p.Key}={Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "NULL"}")));
            }
        }

        return text.ToString();
    }
}
