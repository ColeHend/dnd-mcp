using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: a representative campaign_write batch leaves exactly the change_log rows §3.5 describes: one create per
/// row with the whole row (JSON columns embedded), composite keys as JSON arrays, the entity columns an entity's
/// history is found by (knowledge: the character knower and the entity target; relation: from and to; alias, tag link
/// and fact link: the entity), then one update row per changed column or data key and nothing for updated_at. Undo,
/// as_of replay and "changes since" read nothing else.
/// </summary>
public sealed class ChangeLogShapeTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _dm;

    public ChangeLogShapeTests()
    {
        _dm = _f.Campaign("One Piece");
        _f.Sessions.Start(_dm);
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public void CreateBatch_LogsEveryRowInOrderWithItsKeyAndEntities()
    {
        var result = _f.Apply(_dm, new WriteContext { Reason = "recap" },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Iron Guts", Aliases = [new AliasSpec { Alias = "the smith" }], Tags = ["dwarves"],
                Data = Op.Data("{\"attitude\": 20}"), KnownBy = [Op.Knower("party", "met")],
            },
            new CampaignOpSpec { Op = "fact", Statement = "Iron Guts owes the party.", About = ["character:iron-guts"], KnownBy = [Op.Knower("party")] },
            Op.Link("character:iron-guts", "ally_of", "faction:the-party"));

        var npc = _f.Entity(_dm, "character:iron-guts");
        var party = _f.Entity(_dm, "faction:the-party");
        var fact = _f.Fact(_dm, "f:1");
        var tag = _f.Scalar<string>("SELECT id FROM tag");
        var session = _f.Entity(_dm, "session:1").Id;
        var rows = _f.Log(result.BatchId);
        Assert.Equal(
            [
                ("create", "upsert", "entity", npc.Id, npc.Id, (string?)null),
                ("create", "upsert", "entity_alias", $"[\"{npc.Id}\",\"the smith\"]", npc.Id, null),
                ("create", "upsert", "tag", tag, null, null),
                ("create", "upsert", "entity_tag", $"[\"{npc.Id}\",\"{tag}\"]", npc.Id, null),
                ("create", "upsert", "knowledge", Knowledge(npc.Id, null), null, npc.Id),
                ("create", "fact", "fact", fact.Id, null, null),
                ("create", "fact", "fact_link", $"[\"{fact.Id}\",\"{npc.Id}\",\"about\"]", npc.Id, null),
                ("create", "fact", "knowledge", Knowledge(null, fact.Id), null, null),
                ("create", "link", "relation", RelationId(npc.Id, party.Id), npc.Id, party.Id),
            ],
            rows.Select(r => (r.Op, r.Action, r.TargetTable, r.TargetId, r.EntityId, r.OtherEntityId)));
        Assert.All(rows, r =>
        {
            Assert.Null(r.FieldPath);
            Assert.Null(r.OldValue);
            Assert.Equal(("recap", session, "campaign_write", "claude"), (r.Reason, r.SessionId, r.Tool, r.Actor));
        });

        var entity = JsonNode.Parse(rows[0].NewValue!)!.AsObject();
        Assert.Equal(CampaignTables.Entity.Columns.Select(c => c.Name), entity.Select(p => p.Key));
        Assert.Equal(20, (int)entity["data"]!["attitude"]!);
        Assert.Equal(npc.Seq, (long)entity["seq"]!);
        var knowledge = JsonNode.Parse(rows[7].NewValue!)!.AsObject();
        Assert.Equal((session, "knows"), ((string?)knowledge["learned_session_id"], (string?)knowledge["state"]));
    }

    [Fact]
    public void UpdateBatch_LogsOneRowPerChangedColumnAndDataKey_NotUpdatedAt()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", Status = "alive", Data = Op.Data("{\"attitude\": 20, \"likes\": \"ale\"}") });

        var result = _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Ref = "character:iron-guts", Summary = "Smith", Status = "dead", Data = Op.Data("{\"attitude\": -40, \"home\": \"Serret\", \"likes\": \"ale\"}"),
        });

        var npc = _f.Entity(_dm, "character:iron-guts");
        Assert.Equal(
            [
                ("summary", "", "Smith"),
                ("status", "alive", "dead"),
                ("data.attitude", "20", "-40"),
                ("data.home", (string?)null, "\"Serret\""),
            ],
            _f.Log(result.BatchId).Select(r => (r.FieldPath!, r.OldValue, r.NewValue)));
        Assert.All(_f.Log(result.BatchId), r => Assert.Equal(("update", "upsert", "entity", npc.Id, npc.Id), (r.Op, r.Action, r.TargetTable, r.TargetId, r.EntityId)));
    }

    [Fact]
    public void KnowledgeOfACharacter_NamesTheCharacterAsEntityId()
    {
        _f.Apply(_dm, Op.Upsert("character", "Ignis"), Op.Upsert("location", "The City"), Op.Fact("Ignis was not born here."));

        var result = _f.Knowledge.Record(_dm, ["f:1", "location:the-city"], [Op.Knower("character:ignis")], WriteContext.Default);

        var ignis = _f.Entity(_dm, "character:ignis").Id;
        var city = _f.Entity(_dm, "location:the-city").Id;
        Assert.Equal([(ignis, (string?)null), (ignis, city)], _f.Log(result.BatchId).Select(r => (r.EntityId!, r.OtherEntityId)));
    }

    [Fact]
    public void Attendance_NamesTheSessionAndTheCharacter()
    {
        _f.Apply(_dm, Op.Upsert("character", "Serif", "pc"));

        var result = _f.Sessions.RecordPast(_dm, 5, attendance: [new AttendanceSpec { Character = "character:serif", Present = false, Note = "not yet in the party" }]);

        var serif = _f.Entity(_dm, "character:serif").Id;
        var session = _f.Entity(_dm, "session:5").Id;
        var row = Assert.Single(_f.Log(result.BatchId), r => r.TargetTable == "session_attendance");
        Assert.Equal(($"[\"{session}\",\"{serif}\"]", session, serif), (row.TargetId, row.EntityId, row.OtherEntityId));
    }

    private string Knowledge(string? entityId, string? factId) =>
        _f.Scalar<string>("SELECT id FROM knowledge WHERE entity_id IS @entityId AND fact_id IS @factId", new { entityId, factId });

    private string RelationId(string from, string to) =>
        _f.Scalar<string>("SELECT id FROM relation WHERE from_id = @from AND to_id = @to", new { from, to });
}
