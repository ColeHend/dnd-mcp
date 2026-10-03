using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: undo reverses exactly one batch, as a new batch, leaving every loggable table (and the FTS index built from
/// it) as it was before that batch, for every kind of change: create, column update, data-key update, hard delete, soft
/// delete, composite keys and JSON columns. It refuses, writing nothing, when the batch is unknown or already undone, or
/// when a later change builds on it (the same field, a reference to an id it created, or a batch made in a session it
/// created), or when dice were rolled in a session it created. An undo that clobbers a later edit, deletes a row later
/// rows point at, or strands a session's rolls and batches destroys work the user never asked to lose.
/// </summary>
public sealed class UndoEngineTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SeededCampaign _campaign;
    private readonly SeededEntity _npc;
    private readonly SeededEntity _pc;
    private readonly SeededSession _session;

    public UndoEngineTests()
    {
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        _campaign = seed.Campaign(settings: "{\"effective_level_offset\":1}");
        _session = seed.Session(_campaign.Id, 5);
        _npc = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Iron Guts", subtype: "npc", status: "alive",
            data: "{\"attitude\":20,\"home\":{\"city\":\"Serret\"}}");
        _pc = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc", status: "alive");
        seed.Alias(_npc.Id, "Guts", "public");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Undo_CreatesOfAnEntityWithItsAliasesTagsFactsAndKnowledge_LeaveNothingBehind()
    {
        var before = Snapshot();
        var batch = _db.Batch(_campaign.Id, r =>
        {
            var keras = CreateEntity(r, "Keras", "character");
            r.Insert("entity_alias", new Dictionary<string, object?> { ["entity_id"] = keras, ["alias"] = "the old king", ["visibility"] = "party" }, "upsert");
            var tag = (string)r.Insert("tag", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["name"] = "fragment" }, "upsert")["id"]!;
            r.Insert("entity_tag", new Dictionary<string, object?> { ["entity_id"] = keras, ["tag_id"] = tag }, "upsert");
            var fact = CreateFact(r, "Keras sleeps under the lake.");
            r.Insert("fact_link", new Dictionary<string, object?> { ["fact_id"] = fact, ["entity_id"] = keras, ["role"] = "about" }, "fact");
            r.Insert("knowledge", new Dictionary<string, object?>
            {
                ["campaign_id"] = _campaign.Id, ["fact_id"] = fact, ["knower_kind"] = "party", ["state"] = "suspects",
                ["learned_session_id"] = _session.EntityId,
            }, "fact");
            r.Insert("clock", new Dictionary<string, object?> { ["entity_id"] = keras, ["segments"] = 4 }, "upsert");
        });
        Assert.Equal(1, EntityHits("\"old king\""));

        var result = _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Snapshot());
        Assert.Equal(0, EntityHits("keras"));
        Assert.Equal(0, EntityHits("\"old king\""));
        Assert.Equal(0, FactHits("lake"));
        Assert.Equal(batch, result.UndoneBatchId);
        Assert.Equal(8, result.RowsLogged);
        Assert.False(result.WasUndo);
    }

    /// <summary>Columns, a data key changed, one added, one removed, a nested object: all back exactly.</summary>
    [Fact]
    public void Undo_UpdatesOfColumnsAndDataKeys_RestoreEveryOldValue()
    {
        var before = Snapshot();
        var batch = _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead", ["sort_key"] = 2.5, ["summary"] = "Gone." }, "status");
            r.PatchObject("entity", _npc.Id, "data", new JsonObject
            {
                ["attitude"] = -40, ["oath"] = "revenge", ["home"] = new JsonObject { ["city"] = null, ["ship"] = "Gull" },
            }, "upsert");
            r.PatchObject("campaign", _campaign.Id, "settings", new JsonObject { ["effective_level_offset"] = null, ["default_visibility"] = "party" }, "update");
        });

        _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Snapshot());
    }

    /// <summary>A hard delete (unlink, retract) comes back as the logged row, JSON data and all.</summary>
    [Fact]
    public void Undo_HardDeletes_ReinsertTheLoggedRows()
    {
        string relationId;
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            relationId = seed.Relation(_campaign.Id, _npc.Id, "enemy_of", _pc.Id, attitude: -80, data: "{\"why\":\"debts\"}", sinceSessionId: _session.EntityId);
            seed.Attendance(_session.EntityId, _pc.Id, present: false, note: "sick");
        }

        var before = Snapshot();
        var batch = _db.Batch(_campaign.Id, r =>
        {
            r.Delete("relation", relationId, "unlink");
            r.Delete("session_attendance", [_session.EntityId, _pc.Id], "session");
            r.Delete("entity_alias", [_npc.Id, "guts"], "upsert");
        });
        Assert.Equal(0, EntityHits("aliases : guts"));

        _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Snapshot());
        Assert.Equal(1, EntityHits("aliases : guts"));
    }

    [Fact]
    public void Undo_SoftDelete_RestoresTheEntityAndSearchFindsItAgain()
    {
        var before = Snapshot();
        var batch = _db.Batch(_campaign.Id, r => r.SoftDelete("entity", _npc.Id));
        Assert.Equal(0, EntityHits("iron"));

        _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Snapshot());
        Assert.Equal(1, EntityHits("iron"));
        Assert.Equal(1, EntityHits("aliases : guts"));
    }

    [Fact]
    public void Undo_JsonColumns_GateAndRelationDataAreRestored()
    {
        string factId;
        string relationId;
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            factId = seed.Fact(_campaign.Id, "The seal holds.").Id;
            relationId = seed.Relation(_campaign.Id, _npc.Id, "ally_of", _pc.Id, data: "{\"terms\":[\"ale\"]}");
        }

        var before = Snapshot();
        var batch = _db.Batch(_campaign.Id, r =>
        {
            r.Update("fact", factId, new Dictionary<string, object?> { ["gate"] = "{\"forbidden_terms\":[\"seal\"]}" }, "fact");
            r.Update("relation", relationId, new Dictionary<string, object?> { ["data"] = "{\"terms\":[\"rum\"],\"since\":\"S5\"}" }, "link");
        });

        _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// Undo of an undo is redo: the entity comes back under its old id AND its old e: handle (seq), so a handle the model
    /// saw before the undo means the same entity again, and search finds it.
    /// </summary>
    [Fact]
    public void Undo_OfAnUndo_RedoesTheBatchUnderTheSameIdsAndSeqs()
    {
        string keras = null!;
        var batch = _db.Batch(_campaign.Id, r =>
        {
            keras = CreateEntity(r, "Keras", "character");
            r.Insert("entity_alias", new Dictionary<string, object?> { ["entity_id"] = keras, ["alias"] = "the old king", ["visibility"] = "party" }, "upsert");
            r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "missing" }, "status");
        });
        var after = Snapshot();
        var undo = _db.Undo(_campaign.Id, batch);

        var redo = _db.Undo(_campaign.Id, undo.UndoBatchId);

        Assert.True(redo.WasUndo);
        Assert.Equal(after, Snapshot());
        Assert.Equal(1, EntityHits("\"old king\""));
    }

    [Fact]
    public void Undo_UndoBatchRows_AreLabelledAndPointAtTheOriginal()
    {
        var batch = _db.Batch(_campaign.Id, r =>
        {
            var keras = CreateEntity(r, "Keras", "character");
            r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status");
        });
        _db.Time.Advance(TimeSpan.FromMinutes(3));
        var original = Log().Where(r => r.BatchId == batch).ToList();

        var result = _db.Undo(_campaign.Id, batch, sessionId: _session.EntityId, reason: "misclick");

        var undoRows = Log().Where(r => r.BatchId == result.UndoBatchId).ToList();
        Assert.Equal(new[] { "update", "delete" }, undoRows.Select(r => r.Op));
        Assert.All(undoRows, r =>
        {
            Assert.Equal(UndoEngine.UndoAction, r.Action);
            Assert.Equal(batch, r.UndoOf);
            Assert.Equal(_session.EntityId, r.SessionId);
            Assert.Equal("campaign_history/undo", r.Tool);
            Assert.Equal("misclick", r.Reason);
            Assert.Equal("2026-09-01T12:03:00.000Z", r.At);
        });
        Assert.Equal(("dead", "alive"), (undoRows[0].OldValue, undoRows[0].NewValue));
        Assert.Equal(original.AsEnumerable().Reverse().Select(r => r.Seq), result.Reversed.Select(r => r.Seq));
        Assert.Equal(original, Log().Where(r => r.BatchId == batch).ToList());
    }

    [Fact]
    public void Undo_AlreadyUndone_IsRefusedNamingTheUndoBatchAndWritesNothing()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        var undo = _db.Undo(_campaign.Id, batch);
        var logged = Log().Count;

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains($"already undone by batch {undo.UndoBatchId}", error.Message);
        Assert.Contains("to redo it", error.Message);
        Assert.Equal(logged, Log().Count);
    }

    /// <summary>After undo and redo, the way to reverse the batch again is to undo the redo; the message says so.</summary>
    [Fact]
    public void Undo_UndoneThenRedone_PointsAtTheRedo()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        var undo = _db.Undo(_campaign.Id, batch);
        var redo = _db.Undo(_campaign.Id, undo.UndoBatchId);

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains($"undo {redo.UndoBatchId} to reverse it again", error.Message);
    }

    /// <summary>A later edit of the same field is not silently clobbered: the undo is refused and names the later batch.</summary>
    [Fact]
    public void Undo_LaterChangeToTheSameField_IsRefusedListingTheLaterBatch()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        var later = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "missing" }, "status"));
        var before = Snapshot();
        var logged = Log().Count;

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains(later, error.Message);
        Assert.Contains($"changes entity {_npc.SeqHandle} status", error.Message);
        Assert.DoesNotContain("Iron Guts", error.Message);
        Assert.Equal(before, Snapshot());
        Assert.Equal(logged, Log().Count);
    }

    [Fact]
    public void Undo_LaterChangeToADataKey_ConflictsOnlyOnTheSameKey()
    {
        var batch = _db.Batch(_campaign.Id, r => r.PatchObject("entity", _npc.Id, "data", new JsonObject { ["attitude"] = -40 }, "upsert"));
        _db.Batch(_campaign.Id, r => r.PatchObject("entity", _npc.Id, "data", new JsonObject { ["oath"] = "revenge" }, "upsert"));

        _db.Undo(_campaign.Id, batch);

        using var connection = _db.Open();
        var data = JsonNode.Parse(connection.ExecuteScalar<string>("SELECT data FROM entity WHERE id = @id", new { id = _npc.Id })!)!;
        Assert.Equal(20, (int)data["attitude"]!);
        Assert.Equal("revenge", (string?)data["oath"]);
    }

    /// <summary>A later change to another field of the same row, or to another row, does not block the undo.</summary>
    [Fact]
    public void Undo_UnrelatedLaterChanges_DoNotConflict()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["summary"] = "Fell." }, "upsert"));
        _db.Batch(_campaign.Id, r => r.Update("entity", _pc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        _db.Batch(_campaign.Id, r => CreateEntity(r, "Tristan", "character"));

        _db.Undo(_campaign.Id, batch);

        using var connection = _db.Open();
        Assert.Equal(("alive", "Fell."), connection.QuerySingle<(string, string)>("SELECT status, summary FROM entity WHERE id = @id", new { id = _npc.Id }));
    }

    public static TheoryData<string> LaterReferences() => new() { "relation", "fact_link", "knowledge", "gate", "parent", "alias" };

    /// <summary>
    /// Deleting a created row that later rows point at would cascade them away unlogged (or break the gate that names
    /// it): any later mention of an id the batch created blocks the undo.
    /// </summary>
    [Theory]
    [MemberData(nameof(LaterReferences))]
    public void Undo_LaterRowReferencesAnIdTheBatchCreated_IsRefused(string reference)
    {
        string entity = null!;
        string fact = null!;
        var batch = _db.Batch(_campaign.Id, r =>
        {
            entity = CreateEntity(r, "Keras", "character");
            fact = CreateFact(r, "Keras sleeps.");
        });
        string gatedFact;
        using (var connection = _db.Open())
        {
            gatedFact = new CampaignSeed(connection).Fact(_campaign.Id, "The seal breaks.").Id;
        }

        var later = _db.Batch(_campaign.Id, r =>
        {
            switch (reference)
            {
                case "relation":
                    r.Insert("relation", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["from_id"] = _pc.Id, ["rel"] = "fears", ["to_id"] = entity }, "link");
                    break;
                case "fact_link":
                    r.Insert("fact_link", new Dictionary<string, object?> { ["fact_id"] = gatedFact, ["entity_id"] = entity, ["role"] = "about" }, "fact");
                    break;
                case "knowledge":
                    r.Insert("knowledge", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["fact_id"] = fact, ["knower_kind"] = "party", ["state"] = "knows" }, "reveal");
                    break;
                case "gate":
                    r.Update("fact", gatedFact, new Dictionary<string, object?> { ["gate"] = $"{{\"after\":[\"{fact}\"]}}" }, "fact");
                    break;
                case "parent":
                    r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["parent_id"] = entity }, "upsert");
                    break;
                case "alias":
                    r.Insert("entity_alias", new Dictionary<string, object?> { ["entity_id"] = entity, ["alias"] = "the old king" }, "upsert");
                    break;
            }
        });
        var before = Snapshot();

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains(later, error.Message);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// cross_link is the one table shared between campaigns, logged under whichever campaign linked: another campaign
    /// linking an entity this batch created blocks the undo (the hard delete would cascade that campaign's link away with no
    /// history row), and the refusal says which campaign the later batch is in, since undo resolves batches per campaign.
    /// </summary>
    [Fact]
    public void Undo_AnotherCampaignLaterCrossLinkedACreatedEntity_IsRefusedNamingThatCampaign()
    {
        var (other, twin) = OtherCampaignWithTwin();
        string created = null!;
        var batch = _db.Batch(_campaign.Id, r => created = CreateEntity(r, "Old King", "character"));
        var link = _db.Batch(other.Id, r => r.Insert("cross_link", CrossLink(created, twin.Id), "link"));
        var before = Snapshot();
        var logged = Log().Count;

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains(link, error.Message);
        Assert.Contains($"in campaign {other.Slug}", error.Message);
        Assert.Contains("creates a cross link row", error.Message);
        Assert.Equal(before, Snapshot());
        Assert.Equal(logged, Log().Count);
    }

    /// <summary>The other campaign's link and its own undo cancel out, as within one campaign: the undo then goes ahead.</summary>
    [Fact]
    public void Undo_AnotherCampaignsCrossLinkThatWasItselfUndone_DoesNotConflict()
    {
        var (other, twin) = OtherCampaignWithTwin();
        string created = null!;
        var batch = _db.Batch(_campaign.Id, r => created = CreateEntity(r, "Old King", "character"));
        var link = _db.Batch(other.Id, r => r.Insert("cross_link", CrossLink(created, twin.Id), "link"));
        _db.Undo(other.Id, link);

        _db.Undo(_campaign.Id, batch);

        using var connection = _db.Open();
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM entity WHERE id = @created", new { created }));
    }

    /// <summary>
    /// A row the batch updated has gone by a route the log does not show (here a raw delete): refused before anything is
    /// reversed. Checked on the caller's own transaction, so it holds even for a caller that swallowed the exception: the
    /// newer row (the status change, reversed first) is still as the batch left it.
    /// </summary>
    [Fact]
    public void Undo_ARowTheBatchUpdatedNoLongerExists_IsRefusedBeforeAnythingIsReversed()
    {
        var batch = _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity_alias", [_npc.Id, "Guts"], new Dictionary<string, object?> { ["visibility"] = "party" }, "upsert");
            r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status");
        });
        using var connection = _db.Open();
        connection.Execute("DELETE FROM entity_alias WHERE entity_id = @id", new { id = _npc.Id });
        using var transaction = connection.BeginTransaction();

        var error = Assert.Throws<DndInputException>(() => UndoEngine.Undo(connection, transaction, _campaign.Id, batch,
            BatchContext.New(_campaign.Id, CampaignValues.Actors.Claude, "campaign_history/undo", null, null), _db.Database.Now()));

        Assert.Contains("an entity alias row, which it changed, no longer exists", error.Message);
        Assert.Equal("dead", connection.ExecuteScalar<string>("SELECT status FROM entity WHERE id = @id", new { id = _npc.Id }, transaction));
        Assert.Equal(2, connection.ExecuteScalar<long>("SELECT count(*) FROM change_log", transaction: transaction));
    }

    /// <summary>
    /// Redo of an undone creation after a new entity took the same slug: a refusal the model can act on, naming the
    /// entity by handle, instead of a raw UNIQUE constraint error; nothing persists.
    /// </summary>
    [Fact]
    public void Undo_RedoCollidesWithAnEntityCreatedSinceUnderTheSameSlug_IsRefusedAndWritesNothing()
    {
        long seq = 0;
        var batch = _db.Batch(_campaign.Id, r =>
        {
            var id = CreateEntity(r, "Keras", "character");
            seq = (long)r.Read("entity", id)!["seq"]!;
        });
        var undo = _db.Undo(_campaign.Id, batch);
        _db.Batch(_campaign.Id, r => CreateEntity(r, "Keras", "character"));
        var before = Snapshot();
        var logged = Log().Count;

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, undo.UndoBatchId));

        Assert.Contains($"it deleted entity e:{seq}", error.Message);
        Assert.Contains("collides with something that exists now", error.Message);
        Assert.DoesNotContain("UNIQUE", error.Message);
        Assert.DoesNotContain("Keras", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Snapshot());
        Assert.Equal(logged, Log().Count);
    }

    /// <summary>The refusal lists every conflicting later batch, newest first (the order to undo them in).</summary>
    [Fact]
    public void Undo_SeveralConflictingLaterBatches_AreListedNewestFirst()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        var second = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "missing" }, "status"));
        _db.Batch(_campaign.Id, r => r.Update("entity", _pc.Id, new Dictionary<string, object?> { ["summary"] = "unrelated" }, "upsert"));
        var fourth = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "alive" }, "status"));

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.True(error.Message.IndexOf(fourth, StringComparison.Ordinal) < error.Message.IndexOf(second, StringComparison.Ordinal));
        Assert.Equal(3, error.Message.Split('\n').Length);
    }

    /// <summary>
    /// A later batch and its own undo cancel out, so "undo, oops, redo" never leaves an earlier batch undoable never
    /// again; but once that undo is itself undone (redo), the later batch is in effect and conflicts again.
    /// </summary>
    [Fact]
    public void Undo_LaterBatchesThatCancelOut_DoNotConflictButARedoneOneDoes()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        var later = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "missing" }, "status"));
        var undoLater = _db.Undo(_campaign.Id, later);
        var redoLater = _db.Undo(_campaign.Id, undoLater.UndoBatchId);

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));
        Assert.Contains(later, error.Message);
        Assert.DoesNotContain(redoLater.UndoBatchId, error.Message);

        _db.Undo(_campaign.Id, redoLater.UndoBatchId);
        _db.Undo(_campaign.Id, batch);

        using var connection = _db.Open();
        Assert.Equal("alive", connection.ExecuteScalar<string>("SELECT status FROM entity WHERE id = @id", new { id = _npc.Id }));
    }

    /// <summary>
    /// C05 (contract fix FI9): dice rolled in a session make the batch that created the session (a start, a record_past)
    /// impossible to undo. Rolls are not in change_log (dice cannot be un-rolled), so deleting the session used to detach
    /// them for good (dice_roll.session_id is ON DELETE SET NULL, and a redo cannot re-attach them): the session's roll
    /// log, secret rolls included, vanished from every view. One roll is enough (the commonest case: a single check made
    /// right after the start). The refusal names the session by handle, never a roll's label (a secret roll's label is the
    /// DM's).
    /// </summary>
    [Theory]
    [InlineData(1, "a dice roll was logged")]
    [InlineData(2, "2 dice rolls were logged")]
    public void Undo_BatchThatCreatedASessionDiceWereRolledIn_IsRefusedAndTheRollsStayAttached(int rolls, string howMany)
    {
        var start = StartSession(6, out var sessionId);
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.DiceRoll(_campaign.Id, sessionId, label: "Behind the screen", secret: true);
            if (rolls == 2)
            {
                seed.DiceRoll(_campaign.Id, sessionId, label: "Stealth");
            }
        }

        var before = Snapshot();
        var logged = Log().Count;

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, start));

        Assert.Contains($"Batch {start} cannot be undone: it created session:6, and {howMany} in that session", error.Message);
        Assert.Contains("Dice cannot be un-rolled", error.Message);
        Assert.Contains($"campaign_session {{\"action\": \"end\", \"campaign\": \"{_campaign.Slug}\"}}", error.Message);
        Assert.DoesNotContain("Stealth", error.Message);
        Assert.DoesNotContain("screen", error.Message);
        Assert.Equal(before, Snapshot());
        Assert.Equal(logged, Log().Count);
        using var check = _db.Open();
        Assert.Equal(rolls, check.ExecuteScalar<long>("SELECT count(*) FROM dice_roll WHERE session_id = @sessionId", new { sessionId }));
    }

    /// <summary>
    /// C05 (FI9): batches made in a session (their change_log session_id) build on the batch that created it. Undoing that
    /// batch first used to leave them filed under a session that no longer exists: history "since" the session found
    /// nothing and point-in-time replay treated them as timeless. Refused like any other conflict, listing the later batch
    /// and saying to keep the session instead; once that batch is undone (it and its undo cancel out), the undo goes ahead.
    /// </summary>
    [Fact]
    public void Undo_BatchThatCreatedASessionLaterBatchesWereMadeIn_IsRefusedUntilTheyAreUndone()
    {
        var start = StartSession(6, out var sessionId);
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        var later = _db.Batch(_campaign.Id, r => CreateEntity(r, "Mira", "character"), sessionId: sessionId);
        var before = Snapshot();
        var logged = Log().Count;

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, start));

        Assert.Contains("It created session:6, and later batches were made in it", error.Message);
        Assert.Contains($"- {later} (", error.Message);
        Assert.Contains("made in session:6", error.Message);
        Assert.Contains($"campaign_session {{\"action\": \"end\", \"campaign\": \"{_campaign.Slug}\"}}", error.Message);
        Assert.DoesNotContain("Mira", error.Message);
        Assert.Equal(before, Snapshot());
        Assert.Equal(logged, Log().Count);

        _db.Undo(_campaign.Id, later);
        _db.Undo(_campaign.Id, start);

        using var check = _db.Open();
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM session WHERE entity_id = @sessionId", new { sessionId }));
    }

    /// <summary>A session nothing else uses yet (no dice, no batch made in it) is undone with the batch that created it.</summary>
    [Fact]
    public void Undo_BatchThatCreatedASessionNothingUsesYet_RemovesTheSession()
    {
        var before = Snapshot();
        var start = StartSession(6, out var sessionId);

        _db.Undo(_campaign.Id, start);

        Assert.Equal(before, Snapshot());
        using var check = _db.Open();
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM entity WHERE id = @sessionId", new { sessionId }));
    }

    /// <summary>A whole campaign-creation batch (campaign row, party, party_id update) undoes cleanly: the FK order works out.</summary>
    [Fact]
    public void Undo_CampaignCreationBatch_RemovesTheCampaignAndItsParty()
    {
        var campaignId = CampaignDatabase.NewId();
        var batch = _db.Batch(campaignId, r =>
        {
            r.Insert("campaign", new Dictionary<string, object?>
            {
                ["id"] = campaignId, ["slug"] = "one-piece", ["name"] = "One Piece", ["role"] = "dm", ["ruleset"] = "2024",
            }, "create");
            var party = (string)r.Insert("entity", new Dictionary<string, object?>
            {
                ["campaign_id"] = campaignId, ["kind"] = "faction", ["subtype"] = "party", ["slug"] = "crew", ["name"] = "Crew",
            }, "create")["id"]!;
            r.Update("campaign", campaignId, new Dictionary<string, object?> { ["party_id"] = party }, "create");
        });

        _db.Undo(campaignId, batch);

        using var connection = _db.Open();
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM campaign WHERE id = @campaignId", new { campaignId }));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM entity WHERE campaign_id = @campaignId", new { campaignId }));
    }

    [Fact]
    public void Undo_UniquePrefixOfEightCharactersOrMore_ResolvesTheBatch()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"),
            batchId: "0199aaaa-1111-7000-8000-000000000001");

        var result = _db.Undo(_campaign.Id, "  0199AAAA ");

        Assert.Equal(batch, result.UndoneBatchId);
    }

    public static TheoryData<string, string> BadBatchIds() => new()
    {
        { "0199aaa", "too short" },
        { "0199aaaa", "matches 2 batches" },
        { "0199bbbb", "No batch" },
        { "0199aaaa-1111-7000-8000-00000000000z", "is not a batch id" },
        { "0199aaaa%", "is not a batch id" },
    };

    [Theory]
    [MemberData(nameof(BadBatchIds))]
    public void Undo_BatchIdThatNamesNoSingleBatch_IsRefused(string text, string message)
    {
        _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"),
            batchId: "0199aaaa-1111-7000-8000-000000000001");
        _db.Batch(_campaign.Id, r => r.Update("entity", _pc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"),
            batchId: "0199aaaa-2222-7000-8000-000000000002");

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, text));

        Assert.Contains(message, error.Message);
    }

    /// <summary>A batch of another campaign is not found from this one (undo is per campaign).</summary>
    [Fact]
    public void Undo_BatchOfAnotherCampaign_IsNotFound()
    {
        SeededCampaign other;
        using (var connection = _db.Open())
        {
            other = new CampaignSeed(connection).Campaign(name: "Other");
        }

        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));

        var error = Assert.Throws<DndInputException>(() => _db.Undo(other.Id, batch));

        Assert.Contains("No batch", error.Message);
    }

    /// <summary>An undo inside a dry run shows what it would do and leaves nothing: no reversal, no undo batch.</summary>
    [Fact]
    public void Undo_InADryRun_ChangesNothing()
    {
        var batch = _db.Batch(_campaign.Id, r => r.Update("entity", _npc.Id, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        var before = Snapshot();
        var logged = Log().Count;

        var result = _db.Database.Write((connection, transaction) => UndoEngine.Undo(connection, transaction, _campaign.Id, batch,
            BatchContext.New(_campaign.Id, CampaignValues.Actors.Claude, "campaign_history/undo", null, null), _db.Database.Now()), dryRun: true);

        Assert.Equal(1, result.RowsLogged);
        Assert.Equal(before, Snapshot());
        Assert.Equal(logged, Log().Count);
    }

    // What campaign_session start does when the session is new: the session entity and its live session row in one
    // batch, filed under the session it creates (its id is chosen first, as the write path does). Returns the batch id.
    private string StartSession(int number, out string sessionId)
    {
        var id = CampaignDatabase.NewId();
        sessionId = id;
        return _db.Batch(_campaign.Id, r =>
        {
            r.Insert("entity", new Dictionary<string, object?>
            {
                ["id"] = id, ["campaign_id"] = _campaign.Id, ["kind"] = CampaignValues.Kinds.Session,
                ["slug"] = CampaignSlugs.ForSession(number), ["name"] = $"Session {number}", ["visibility"] = "party",
            }, "start");
            r.Insert("session", new Dictionary<string, object?>
            {
                ["entity_id"] = id, ["campaign_id"] = _campaign.Id, ["number"] = number, ["status"] = CampaignValues.SessionStatuses.Live,
            }, "start");
        }, sessionId: id);
    }

    private string CreateEntity(ChangeRecorder r, string name, string kind) =>
        (string)r.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["kind"] = kind, ["slug"] = CampaignSlugs.From(name, kind), ["name"] = name,
            ["data"] = "{\"new\":true}",
        }, "upsert")["id"]!;

    private static Dictionary<string, object?> CrossLink(string one, string other)
    {
        var (a, b) = string.CompareOrdinal(one, other) < 0 ? (one, other) : (other, one);
        return new Dictionary<string, object?> { ["a_id"] = a, ["b_id"] = b, ["note"] = "same person" };
    }

    private (SeededCampaign Campaign, SeededEntity Twin) OtherCampaignWithTwin()
    {
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        var other = seed.Campaign(name: "One Piece");
        return (other, seed.Entity(other.Id, CampaignValues.Kinds.Character, "Keras"));
    }

    private string CreateFact(ChangeRecorder r, string statement) =>
        (string)r.Insert("fact", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["statement"] = statement }, "fact")["id"]!;

    private long EntityHits(string match)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long>("SELECT count(*) FROM entity_fts WHERE entity_fts MATCH @match", new { match });
    }

    private long FactHits(string match)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long>("SELECT count(*) FROM fact_fts WHERE fact_fts MATCH @match", new { match });
    }

    private List<ChangeRow> Log()
    {
        using var connection = _db.Open();
        return connection.Query<ChangeRow>($"SELECT {ChangeRow.Columns} FROM change_log ORDER BY seq").ToList();
    }

    // Every loggable table (updated_at aside: an undo stamps its own time) plus both FTS indexes, as text.
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
                text.AppendLine(string.Join(" | ", ((IDictionary<string, object?>)row).Select(p => $"{p.Key}={p.Value ?? "NULL"}")));
            }
        }

        text.AppendLine("## entity_fts");
        foreach (var row in connection.Query("SELECT rowid, name, aliases, summary, body, secret, tags, hidden_aliases FROM entity_fts ORDER BY rowid"))
        {
            text.AppendLine(string.Join(" | ", ((IDictionary<string, object?>)row).Values));
        }

        text.AppendLine("## fact_fts");
        foreach (var row in connection.Query("SELECT rowid, statement FROM fact_fts ORDER BY rowid"))
        {
            text.AppendLine(string.Join(" | ", ((IDictionary<string, object?>)row).Values));
        }

        return text.ToString();
    }
}
