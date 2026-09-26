using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: <c>json_patch</c> has RFC 7396 merge-patch semantics, and a query written the way the design
/// writes it is served by the <c>json_extract</c> expression index. research/04 §4 makes every per-kind
/// <c>data</c> update an atomic <c>UPDATE … SET data = json_patch(data, :patch)</c>, and indexes NPC attitude
/// as <c>ix_entity_npc_attitude ON entity(campaign_id, json_extract(data,'$.attitude')) WHERE kind='character'</c>.
/// </summary>
public sealed class JsonFunctionTests : IDisposable
{
    private readonly SqliteConnection _db;

    public JsonFunctionTests()
    {
        _db = SqliteScratch.OpenInMemory();
        _db.Execute("""
            CREATE TABLE entity (
              seq INTEGER PRIMARY KEY,
              id TEXT NOT NULL UNIQUE,
              campaign_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              data TEXT NOT NULL DEFAULT '{}' CHECK (json_valid(data))
            ) STRICT;
            CREATE INDEX ix_entity_npc_attitude ON entity(campaign_id, json_extract(data,'$.attitude')) WHERE kind='character';
            """);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// research §4 "JSON updates are atomic partial patches". Each case is a way a patch could silently
    /// lose data if the semantics were different. Nested objects merge rather than replace. JSON null
    /// DELETES the key, so json_patch cannot store an explicit null. Arrays are replaced wholesale, never
    /// merged, so patching <c>resources</c> or <c>conditions</c> needs the complete new array. A non-object
    /// patch replaces the whole document.
    /// </summary>
    [Theory]
    [InlineData("""{"a":{"b":1,"c":2}}""", """{"a":{"c":3,"d":4}}""", """{"a":{"b":1,"c":3,"d":4}}""")]
    [InlineData("""{"a":{"b":1,"c":2},"z":1}""", """{"a":{"b":null},"z":null}""", """{"a":{"c":2}}""")]
    [InlineData("""{"conditions":["prone","grappled"]}""", """{"conditions":["prone"]}""", """{"conditions":["prone"]}""")]
    [InlineData("""{"attitude":10}""", """{"voice":{"accent":"Nordic"}}""", """{"attitude":10,"voice":{"accent":"Nordic"}}""")]
    [InlineData("""{"a":1}""", """{"a":{"b":2}}""", """{"a":{"b":2}}""")]
    [InlineData("""{"a":1}""", """[1,2]""", """[1,2]""")]
    public void JsonPatch_MergePatchSemantics_MatchRfc7396(string target, string patch, string expected)
    {
        var result = _db.ExecuteScalar<string>("SELECT json_patch(@target, @patch)", new { target, patch });

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(result!)),
            $"json_patch({target}, {patch}) = {result}, expected {expected}");
    }

    /// <summary>
    /// The design's actual statement shape: patch in place on a STRICT table guarded by
    /// <c>CHECK (json_valid(data))</c>. The patched value must satisfy the CHECK, and untouched keys must
    /// survive.
    /// </summary>
    [Fact]
    public void JsonPatch_UpdateInPlace_MergesAndKeepsUntouchedKeys()
    {
        _db.Execute("""INSERT INTO entity(id, campaign_id, kind, data) VALUES ('iron-guts', 'c1', 'character', '{"attitude":-20,"wants":"gold"}')""");

        _db.Execute("UPDATE entity SET data = json_patch(data, @patch) WHERE id = 'iron-guts'", new { patch = """{"attitude":35}""" });

        Assert.Equal(35, _db.ExecuteScalar<long>("SELECT json_extract(data, '$.attitude') FROM entity WHERE id = 'iron-guts'"));
        Assert.Equal("gold", _db.ExecuteScalar<string>("SELECT json_extract(data, '$.wants') FROM entity WHERE id = 'iron-guts'"));
    }

    /// <summary>
    /// json_extract returns SQL-typed values (INTEGER for a JSON integer), so numeric comparisons against it
    /// compare numbers, not text. "-40" &lt; "5" as text but not as numbers.
    /// </summary>
    [Fact]
    public void JsonExtract_Integer_IsSqlIntegerSoComparisonsAreNumeric()
    {
        Assert.Equal("integer", _db.ExecuteScalar<string>("""SELECT typeof(json_extract('{"attitude":-40}', '$.attitude'))"""));
        Assert.Equal(1, _db.ExecuteScalar<long>("""SELECT json_extract('{"attitude":-40}', '$.attitude') < 5"""));
    }

    /// <summary>
    /// research §4 <c>ix_entity_npc_attitude</c>. The planner must use the expression part of the index,
    /// shown as <c>&lt;expr&gt;</c> in the plan. Checking only the index name is not enough, because the
    /// partial index is still picked for <c>campaign_id</c> alone when the expression does not match. Bound
    /// parameters count (Dapper sends every value as one), and so does different whitespace, since the
    /// planner compares parsed expressions.
    /// </summary>
    [Theory]
    [InlineData("SELECT id FROM entity WHERE campaign_id = 'c1' AND kind = 'character' AND json_extract(data,'$.attitude') > 50")]
    [InlineData("SELECT id FROM entity WHERE campaign_id = @campaign AND kind = @kind AND json_extract(data,'$.attitude') > @min")]
    [InlineData("SELECT id FROM entity WHERE campaign_id = 'c1' AND kind = 'character' AND json_extract( data , '$.attitude' ) > 50")]
    public void ExpressionIndex_QueryMatchingIndexedExpression_UsesIt(string sql)
    {
        SeedCharacters();

        var plan = QueryPlan(sql);

        Assert.Contains("USING INDEX ix_entity_npc_attitude", plan);
        Assert.Contains("<expr>", plan);
    }

    /// <summary>
    /// Ways to write "the same" query that silently lose the expression index. <c>data-&gt;&gt;'$.attitude'</c>
    /// is not <c>json_extract(data,'$.attitude')</c> to the planner. Neither is a differently spelled path.
    /// Without <c>kind = 'character'</c> (or with <c>kind IN (…)</c>) the query no longer implies the index's
    /// WHERE clause, so the partial index cannot be used at all. Repository SQL must use the exact indexed
    /// expression.
    /// </summary>
    [Theory]
    [InlineData("SELECT id FROM entity WHERE campaign_id = 'c1' AND kind = 'character' AND data->>'$.attitude' > 50")]
    [InlineData("SELECT id FROM entity WHERE campaign_id = 'c1' AND kind = 'character' AND json_extract(data,'$.\"attitude\"') > 50")]
    [InlineData("SELECT id FROM entity WHERE campaign_id = 'c1' AND json_extract(data,'$.attitude') > 50")]
    [InlineData("SELECT id FROM entity WHERE campaign_id = 'c1' AND kind IN ('character','location') AND json_extract(data,'$.attitude') > 50")]
    public void ExpressionIndex_QueryNotMatchingIndexedExpression_DoesNotUseTheExpression(string sql)
    {
        SeedCharacters();

        var plan = QueryPlan(sql);

        Assert.DoesNotContain("<expr>", plan);
    }

    private void SeedCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            _db.Execute(
                "INSERT INTO entity(id, campaign_id, kind, data) VALUES (@id, @campaign, @kind, json_object('attitude', @attitude))",
                new { id = $"e{i}", campaign = $"c{i % 3}", kind = i % 2 == 0 ? "character" : "location", attitude = i - 100 });
        }
    }

    private string QueryPlan(string sql)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("@campaign", "c1");
        command.Parameters.AddWithValue("@kind", "character");
        command.Parameters.AddWithValue("@min", 50);
        using var reader = command.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }

        return string.Join(" / ", details);
    }
}
