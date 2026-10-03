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
    public void Catalogue_EntityColumns_FollowTheContract(string table, string? entity, string? other)
    {
        var meta = CampaignTables.Get(table);

        Assert.Equal(entity, meta.EntityColumn);
        Assert.Equal(other, meta.OtherEntityColumn);
    }

    /// <summary>Only the free-form objects log per key; only updated_at and the live log go unlogged.</summary>
    [Fact]
    public void Catalogue_PerKeyAndUnloggedColumns_AreExactlyTheIntendedOnes()
    {
        var perKey = CampaignTables.All.SelectMany(t => t.Columns.Where(c => c.LoggedPerKey).Select(c => $"{t.Name}.{c.Name}"));
        var unlogged = CampaignTables.All.SelectMany(t => t.Columns.Where(c => !c.Logged).Select(c => $"{t.Name}.{c.Name}"));

        Assert.Equal(new[] { "campaign.settings", "entity.data", "relation.data", "session.data" }, perKey);
        Assert.Equal(
            new[] { "campaign.updated_at", "entity.updated_at", "relation.updated_at", "fact.updated_at", "knowledge.updated_at", "session.live_log", "objective.updated_at" },
            unlogged);
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

    [Fact]
    public void Get_UnknownTable_ThrowsNamingTheTables()
    {
        var error = Assert.Throws<ArgumentException>(() => CampaignTables.Get("dice_roll"));

        Assert.Contains("entity", error.Message);
    }
}
