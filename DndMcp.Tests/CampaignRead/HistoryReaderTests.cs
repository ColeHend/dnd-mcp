using System.Text.RegularExpressions;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignDb;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: campaign_history renders change_log for reading: batches in order with their tool, reason, session and
/// undo_of, each change as one line with handles instead of internal ids (<c>character:iron-guts status: alive → dead</c>,
/// facts with code and statement, gates with fact handles), filtered by time, session, target or entity, and paged.
/// No internal id other than a batch id ever appears: ids are how rows are joined, never what the model is shown.
/// </summary>
public sealed partial class HistoryReaderTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;
    private readonly SeededSession _s1;
    private readonly SeededSession _s2;

    public HistoryReaderTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Test");
        _s1 = _seed.Session(_campaign.Id, 1);
        _s2 = _seed.Session(_campaign.Id, 2);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private HistoryReader Reader => new(_db.Database);

    private CampaignRow Row => _seed.LoadCampaign(_campaign.Id);

    private string CreateIronGuts(string? session = null) =>
        _db.Batch(_campaign.Id, r => r.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["kind"] = "character", ["slug"] = "iron-guts", ["name"] = "Iron Guts", ["status"] = "alive",
        }, "upsert"), sessionId: session, reason: "met him");

    private string IronGutsId => Dapper.SqlMapper.QuerySingle<string>(_connection, "SELECT id FROM entity WHERE slug = 'iron-guts'");

    [Fact]
    public void Since_TwoBatches_AreOldestFirstWithTheirContextAndCompactLines()
    {
        var created = CreateIronGuts(_s1.EntityId);
        var died = _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["status"] = "dead" }, "status"),
            sessionId: _s2.EntityId, reason: "fell at the gate");

        var page = Reader.Since(Row);

        Assert.Equal([created, died], page.Batches.Select(b => b.BatchId));
        var first = page.Batches[0];
        Assert.Equal(("claude", "test", "met him", 1), (first.Actor, first.Tool, first.Reason, first.Session));
        Assert.Equal("character:iron-guts created: name \"Iron Guts\", status alive, visibility party", first.Changes[0].Text);
        var second = page.Batches[1];
        Assert.Equal((2, "fell at the gate"), (second.Session, second.Reason));
        var change = Assert.Single(second.Changes);
        Assert.Equal(("status", "alive", "dead", "character:iron-guts status: alive → dead"), (change.Field, change.Old, change.New, change.Text));
    }

    [Fact]
    public void Since_FactChangesAndKnowledge_ShowCodeStatementAndHandlesNeverIds()
    {
        var old = _seed.Fact(_campaign.Id, "The old timeline holds.", code: "F7");
        var fresh = _seed.Fact(_campaign.Id, "The corrected timeline.");
        var batch = _db.Batch(_campaign.Id, r =>
        {
            r.Update("fact", old.Id, new Dictionary<string, object?> { ["canon_status"] = "superseded", ["superseded_by"] = fresh.Id }, "fact");
            r.Insert("knowledge", new Dictionary<string, object?>
            {
                ["campaign_id"] = _campaign.Id, ["fact_id"] = fresh.Id, ["knower_kind"] = "party", ["state"] = "knows",
                ["known_as"] = "the new count", ["learned_session_id"] = _s2.EntityId,
            }, "record");
            r.Update("fact", fresh.Id, new Dictionary<string, object?>
            {
                ["gate"] = FactGates.Serialize(new GateSpec { After = [old.Id] }),
            }, "fact");
        });

        var page = Reader.Since(Row);

        var lines = page.Batches.Single(b => b.BatchId == batch).Changes.Select(c => c.Text).ToList();
        Assert.Contains($"{old.SeqHandle} (F7) \"The old timeline holds.\" canon_status: canon → superseded", lines);
        Assert.Contains($"{old.SeqHandle} (F7) \"The old timeline holds.\" superseded_by: (none) → {fresh.SeqHandle}", lines);
        Assert.Contains($"knowledge of party about {fresh.SeqHandle} created: state knows, known_as \"the new count\", learned session:2", lines);
        Assert.Contains($"{fresh.SeqHandle} \"The corrected timeline.\" gate: (none) → {{\"after\":[\"{old.SeqHandle}\"]}}", lines);
        AssertNoIdsBut(page, [batch]);
    }

    [Fact]
    public void Since_RelationSoftDeleteRestoreAndUndo_RenderAsReadableLines()
    {
        CreateIronGuts();
        var id = IronGutsId;
        var link = _db.Batch(_campaign.Id, r => r.Insert("relation", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["from_id"] = id, ["rel"] = "member_of", ["to_id"] = _campaign.Party!.Id,
        }, "link"));
        _db.Batch(_campaign.Id, r => r.SoftDelete("entity", id));
        _db.Batch(_campaign.Id, r => r.Restore("entity", id));
        _db.Undo(_campaign.Id, link);

        var page = Reader.Since(Row);

        var texts = page.Batches.SelectMany(b => b.Changes).Select(c => c.Text).ToList();
        Assert.Contains("character:iron-guts member_of faction:the-party created: status current, visibility party", texts);
        Assert.Contains("character:iron-guts deleted", texts);
        Assert.Contains("character:iron-guts restored", texts);
        var undoBatch = page.Batches.Last();
        Assert.Equal(link, undoBatch.UndoOf);
        Assert.Equal("character:iron-guts member_of faction:the-party deleted", Assert.Single(undoBatch.Changes).Text);
    }

    [Fact]
    public void Since_SessionNumber_StartsAtTheFirstChangeOfThatSession()
    {
        CreateIronGuts(_s1.EntityId);
        var died = _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["status"] = "dead" }, "status"),
            sessionId: _s2.EntityId);
        var prep = _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["summary"] = "Gone." }, "upsert"));

        var page = Reader.Since(Row, session: 2);

        Assert.Equal([died, prep], page.Batches.Select(b => b.BatchId));
        Assert.Empty(Reader.Since(Row, session: 5).Batches);
    }

    [Fact]
    public void Since_Date_KeepsLaterBatchesAndRefusesNonsense()
    {
        CreateIronGuts();
        _db.Time.Advance(TimeSpan.FromDays(2));
        var later = _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["summary"] = "Later." }, "upsert"));

        Assert.Equal([later], Reader.Since(Row, since: "2026-09-02").Batches.Select(b => b.BatchId));
        Assert.Throws<DndInputException>(() => Reader.Since(Row, since: "last tuesday"));
        Assert.Throws<DndInputException>(() => Reader.Since(Row, since: "2026-09-02", session: 1));
    }

    [Fact]
    public void Since_FactTarget_IncludesItsLinksAndKnowledgeUpdates()
    {
        var fact = _seed.Fact(_campaign.Id, "A fact.");
        CreateIronGuts();
        string? knowledgeId = null;
        _db.Batch(_campaign.Id, r =>
        {
            r.Insert("fact_link", new Dictionary<string, object?> { ["fact_id"] = fact.Id, ["entity_id"] = IronGutsId, ["role"] = "about" }, "fact");
            knowledgeId = (string)r.Insert("knowledge", new Dictionary<string, object?>
            {
                ["campaign_id"] = _campaign.Id, ["fact_id"] = fact.Id, ["knower_kind"] = "party", ["state"] = "suspects",
            }, "record")["id"]!;
        });
        _db.Batch(_campaign.Id, r => r.Update("knowledge", knowledgeId!, new Dictionary<string, object?> { ["state"] = "knows" }, "reveal"));

        var page = Reader.Since(Row, targets: [fact.SeqHandle]);

        var texts = page.Batches.SelectMany(b => b.Changes).Select(c => c.Text).ToList();
        Assert.Equal(3, texts.Count);
        Assert.Contains($"knowledge of party about {fact.SeqHandle} state: suspects → knows", texts);
        Assert.DoesNotContain(texts, t => t.StartsWith("character:iron-guts created", StringComparison.Ordinal));
    }

    /// <summary>A batch that touched the target and something else shows only its rows about the target.</summary>
    [Fact]
    public void Since_WithTargets_ReturnsOnlyTheRowsAboutThem()
    {
        CreateIronGuts();
        var quay = _seed.Entity(_campaign.Id, "location", "Quay");
        var both = _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["status"] = "dead" }, "status");
            r.Update("entity", quay.Id, new Dictionary<string, object?> { ["summary"] = "Burned." }, "upsert");
        });

        var page = Reader.Since(Row, targets: [quay.Handle]);

        var batch = Assert.Single(page.Batches);
        Assert.Equal(both, batch.BatchId);
        Assert.Equal(["location:quay summary: \"\" → Burned."], batch.Changes.Select(c => c.Text));
        Assert.Equal(2, Reader.Batch(Row, both).Changes.Count);
    }

    [Fact]
    public void Entity_History_IsNewestFirstAndOnlyAboutIt()
    {
        CreateIronGuts();
        var other = _db.Batch(_campaign.Id, r => r.Update("campaign", _campaign.Id, new Dictionary<string, object?> { ["summary_md"] = "x" }, "update"));
        var died = _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));

        var page = Reader.Entity(Row, "character:iron-guts");

        Assert.Equal(2, page.Total);
        Assert.Equal(died, page.Batches[0].BatchId);
        Assert.DoesNotContain(page.Batches, b => b.BatchId == other);
    }

    [Fact]
    public void Batch_ByPrefix_ReturnsEveryRowAndRefusesAShortPrefix()
    {
        var id = CreateIronGuts();

        var batch = Reader.Batch(Row, id[..12]);

        Assert.Equal(id, batch.BatchId);
        Assert.Single(batch.Changes);
        Assert.Throws<DndInputException>(() => Reader.Batch(Row, id[..4]));
    }

    [Fact]
    public void Since_Paging_WalksBatchesWithTheCursor()
    {
        CreateIronGuts();
        for (var i = 0; i < 4; i++)
        {
            var summary = "v" + i;
            _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["summary"] = summary }, "upsert"));
        }

        var first = Reader.Since(Row, limit: 2);
        var second = Reader.Since(Row, limit: 2, cursor: first.NextCursor);
        var third = Reader.Since(Row, limit: 2, cursor: second.NextCursor);

        Assert.Equal(5, first.Total);
        Assert.Equal([2, 2, 1], new[] { first, second, third }.Select(p => p.Batches.Count));
        Assert.Null(third.NextCursor);
    }

    [Fact]
    public void AsOf_ReadsTheEntityAsItStoodThen()
    {
        CreateIronGuts(_s1.EntityId);
        _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["status"] = "dead" }, "status"),
            sessionId: _s2.EntityId);

        var then = Reader.AsOf(Row, 1, ["character:iron-guts"]);

        Assert.Equal("alive", Assert.Single(then.Entities).Status);
        Assert.Equal(1, then.AsOfSession);
    }

    [Fact]
    public void Get_HistoryIncludeForAPlayerView_IsNeverThere()
    {
        CreateIronGuts();
        _db.Batch(_campaign.Id, r => r.Update("entity", IronGutsId, new Dictionary<string, object?> { ["secret_md"] = "He is a spy." }, "upsert"));

        var party = new EntityReader(_db.Database).Get(Row, ["character:iron-guts"], EntityIncludes.All, Perspective.Parse("party"));
        var author = new EntityReader(_db.Database).Get(Row, ["character:iron-guts"], EntityIncludes.All, Perspective.Author);

        LeakAssert.Clean(party, ["spy", "secret_md", "met him", "upsert"], "history include");
        Assert.Equal(2, author.Entities[0].Author!.History!.Count);
    }

    private static void AssertNoIdsBut(object result, IReadOnlyList<string> allowed)
    {
        var json = LeakAssert.Serialize(result);
        foreach (Match match in Uuid().Matches(json))
        {
            Assert.Contains(match.Value, allowed);
        }
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex Uuid();
}
