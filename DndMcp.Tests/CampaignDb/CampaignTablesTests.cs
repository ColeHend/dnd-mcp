using System.Text.RegularExpressions;
using Dapper;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: <see cref="CampaignTables"/> describes the migrated schema exactly: every table is either logged or on the
/// deliberate not-logged list, and every logged table's columns (order, type, nullability, JSON kind), key and entity
/// columns are right. The recorder, undo and replay run on this catalogue alone, so a column it does not know is never
/// logged, undone or replayed, silently. A later migration that adds a column or a table fails here until the catalogue
/// knows it.
/// </summary>
public sealed class CampaignTablesTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;

    public CampaignTablesTests() => _connection = _db.Open();

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    public static TheoryData<string> LoggedTables()
    {
        var data = new TheoryData<string>();
        foreach (var table in CampaignTables.All)
        {
            data.Add(table.Name);
        }

        return data;
    }

    [Fact]
    public void Catalogue_EveryTableOfTheSchema_IsLoggedOrDeliberatelyNot()
    {
        var schema = _connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'")
            .Where(t => !CampaignTables.IsInternal(t))
            .Order(StringComparer.Ordinal);

        var known = CampaignTables.All.Select(t => t.Name).Concat(CampaignTables.NotLogged).Order(StringComparer.Ordinal);

        Assert.Equal(known, schema);
    }

    [Theory]
    [MemberData(nameof(LoggedTables))]
    public void Catalogue_Columns_MatchTheMigratedTableInOrderTypeAndNullability(string table)
    {
        var meta = CampaignTables.Get(table);
        var actual = _connection.Query<(long Cid, string Name, string Type, long NotNull, string? Default, long Pk)>(
            $"SELECT cid, name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}') ORDER BY cid").ToList();

        Assert.Equal(actual.Select(c => c.Name), meta.Columns.Select(c => c.Name));
        foreach (var (column, info) in meta.Columns.Zip(actual))
        {
            var sqlType = column.Type switch
            {
                CampaignColumnType.Integer => "INTEGER",
                CampaignColumnType.Real => "REAL",
                _ => "TEXT",
            };
            Assert.True(sqlType == info.Type, $"{table}.{column.Name}: catalogue {sqlType}, schema {info.Type}");
            var nullable = info.NotNull == 0 && info.Pk == 0;
            Assert.True(nullable == column.Nullable, $"{table}.{column.Name}: catalogue nullable {column.Nullable}, schema {nullable}");
        }
    }

    /// <summary>A JSON column's kind is its CHECK: object columns check json_type = 'object', arrays 'array'.</summary>
    [Theory]
    [MemberData(nameof(LoggedTables))]
    public void Catalogue_JsonColumns_MatchTheirChecks(string table)
    {
        var meta = CampaignTables.Get(table);
        var sql = _connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name = @table", new { table })!;
        var checkedJson = Regex.Matches(sql, @"json_type\((\w+)\)\s*=\s*'(\w+)'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

        foreach (var column in meta.Columns)
        {
            var expected = column.Type switch
            {
                CampaignColumnType.JsonObject => "object",
                CampaignColumnType.JsonArray => "array",
                _ => null,
            };
            Assert.True(expected == checkedJson.GetValueOrDefault(column.Name), $"{table}.{column.Name}: catalogue {column.Type}");
        }
    }

    /// <summary>
    /// The key is the primary key, except for entity and fact, whose key is the UNIQUE id (seq is the FTS rowid and the
    /// e:/f: handle; ids are what every other row and change_log reference).
    /// </summary>
    [Theory]
    [MemberData(nameof(LoggedTables))]
    public void Catalogue_KeyColumns_AreThePrimaryKeyOrTheUniqueId(string table)
    {
        var meta = CampaignTables.Get(table);
        var pk = _connection.Query<(string Name, long Pk)>($"SELECT name, pk FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk")
            .Select(c => c.Name).ToList();

        if (pk is ["seq"])
        {
            Assert.Equal(new[] { "id" }, meta.KeyColumns);
            Assert.Equal(1, _connection.ExecuteScalar<long>(
                $"SELECT count(*) FROM pragma_index_list('{table}') l JOIN pragma_index_info(l.name) i WHERE l.\"unique\" = 1 AND i.name = 'id'"));
        }
        else
        {
            Assert.Equal(pk, meta.KeyColumns);
        }
    }

    /// <summary>
    /// change_log.entity_id / other_entity_id per contract §3.5, so an entity's history finds every row about it (either
    /// end of a relation, the knower and the target of a knowledge row, a session's attendance, …).
    /// </summary>
    [Theory]
    [InlineData("campaign", null, null)]
    [InlineData("entity", "id", "parent_id")]
    [InlineData("entity_alias", "entity_id", null)]
    [InlineData("tag", null, null)]
    [InlineData("entity_tag", "entity_id", null)]
    [InlineData("relation", "from_id", "to_id")]
    [InlineData("cross_link", "a_id", "b_id")]
    [InlineData("fact", null, null)]
    [InlineData("fact_link", "entity_id", null)]
    [InlineData("fact_dependency", null, null)]
    [InlineData("knowledge", "knower_id", "entity_id")]
    [InlineData("session", "entity_id", null)]
    [InlineData("session_attendance", "session_id", "character_id")]
    [InlineData("objective", "quest_id", null)]
    [InlineData("clock", "entity_id", null)]
    [InlineData("beat_edge", "from_beat_id", "to_beat_id")]
    [InlineData("character_sheet", "entity_id", null)]
    [InlineData("holding", "holder_id", "item_id")]
    [InlineData("currency_txn", "holder_id", "session_id")]
    [InlineData("award", "recipient_id", "session_id")]
    public void Catalogue_EntityColumns_FollowTheContract(string table, string? entity, string? other)
    {
        var meta = CampaignTables.Get(table);

        Assert.Equal(entity, meta.EntityColumn);
        Assert.Equal(other, meta.OtherEntityColumn);
    }

    /// <summary>
    /// Only the free-form objects and the sheet's keyed trackers log per key; only updated_at and the live log go unlogged.
    /// A sheet tracker logged whole would make a later rest that touched the 1st-level slots block undoing the fight that
    /// spent a 3rd-level one; an updated_at logged would make every write to a sheet or holding conflict with every other.
    /// </summary>
    [Fact]
    public void Catalogue_PerKeyAndUnloggedColumns_AreExactlyTheIntendedOnes()
    {
        var perKey = CampaignTables.All.SelectMany(t => t.Columns.Where(c => c.LoggedPerKey).Select(c => $"{t.Name}.{c.Name}"));
        var unlogged = CampaignTables.All.SelectMany(t => t.Columns.Where(c => !c.Logged).Select(c => $"{t.Name}.{c.Name}"));

        Assert.Equal(
            new[]
            {
                "campaign.settings", "entity.data", "relation.data", "session.data", "character_sheet.abilities", "character_sheet.hit_dice",
                "character_sheet.spell_slots", "character_sheet.resources",
            },
            perKey);
        Assert.Equal(
            new[]
            {
                "campaign.updated_at", "entity.updated_at", "relation.updated_at", "fact.updated_at", "knowledge.updated_at", "session.live_log",
                "objective.updated_at", "character_sheet.updated_at", "holding.updated_at",
            },
            unlogged);
    }

    /// <summary>
    /// The combat tracker's tables are deliberately unlogged (HP ticks are not history; the write-back is one logged batch),
    /// and the four Phase 7 tables that are history are logged: a table on the wrong list either floods change_log with
    /// every hit point or loses the loot an undo should take back.
    /// </summary>
    [Theory]
    [InlineData("character_sheet", true)]
    [InlineData("holding", true)]
    [InlineData("currency_txn", true)]
    [InlineData("award", true)]
    [InlineData("encounter", false)]
    [InlineData("combatant", false)]
    [InlineData("combat_log", false)]
    public void Catalogue_Phase7Tables_AreLoggedExactlyWhenTheyAreHistory(string table, bool logged)
    {
        Assert.Equal(logged, CampaignTables.TryGet(table, out _));
        Assert.Equal(!logged, CampaignTables.NotLogged.Contains(table));
    }

    /// <summary>
    /// Undo, the recorder and replay bind keys as strings: a logged table keyed by an INTEGER (research's currency_txn seq)
    /// could never be undone. Every logged table's key columns are TEXT.
    /// </summary>
    [Theory]
    [MemberData(nameof(LoggedTables))]
    public void Catalogue_KeyColumns_AreText(string table)
    {
        var meta = CampaignTables.Get(table);

        Assert.All(meta.KeyColumns, k => Assert.Equal(CampaignColumnType.Text, meta.Column(k).Type));
    }

    /// <summary>
    /// Undo reverses one logged column at a time, so a CHECK spanning two columns of a logged table could refuse a state
    /// on the way back that never existed. 0002's logged tables have none; the combatant's hp &lt;= max_hp is fine because
    /// combatants are not logged (and shows the pattern finds a table-level CHECK when there is one).
    /// </summary>
    [Theory]
    [InlineData("character_sheet", false)]
    [InlineData("holding", false)]
    [InlineData("currency_txn", false)]
    [InlineData("award", false)]
    [InlineData("combatant", true)]
    public void Schema_TableLevelCheck_OnlyOnTheUnloggedCombatant(string table, bool expected)
    {
        var sql = _connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name = @table", new { table })!;

        Assert.Equal(expected, Regex.IsMatch(sql, @"(?m)^\s*CHECK\s*\("));
    }

    [Theory]
    [InlineData("entity", new[] { "0199-a" }, "0199-a")]
    [InlineData("fact_link", new[] { "f1", "e1", "about" }, "[\"f1\",\"e1\",\"about\"]")]
    [InlineData("entity_alias", new[] { "e1", "Björn's \"Axe\"" }, "[\"e1\",\"Björn's \\\"Axe\\\"\"]")]
    public void TargetId_Key_IsTheValueOrAJsonArrayInKeyOrderAndParsesBack(string table, string[] key, string expected)
    {
        var meta = CampaignTables.Get(table);

        var targetId = meta.TargetId(key);

        Assert.Equal(expected, targetId);
        Assert.Equal(key, meta.ParseTargetId(targetId));
    }

    [Theory]
    [InlineData("dice_roll")]
    [InlineData("combatant")]
    public void Get_UnknownTable_ThrowsNamingTheTables(string table)
    {
        var error = Assert.Throws<ArgumentException>(() => CampaignTables.Get(table));

        Assert.Contains("entity", error.Message);
        Assert.Contains("character_sheet", error.Message);
    }
}
