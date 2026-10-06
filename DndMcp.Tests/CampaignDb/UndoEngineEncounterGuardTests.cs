using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: undo never changes a fight behind its back. Encounters, combatants, the combat log and the rolls a fight
/// made are not in change_log, so the conflict scan cannot see them; the hard delete undo makes of a row the batch created
/// would reach them only through foreign-key actions (a cascade, a SET NULL) with no history row, and a redo could not
/// re-link them. So undo refuses, writing nothing and naming what to do instead, when the batch created: a campaign that
/// has encounters; a session an encounter was run in; an entity that is a combatant, an encounter's scene or its session;
/// a character sheet a combatant of a not-yet-ended encounter was seeded from; a holding such a combatant draws on (its
/// resources key "item:&lt;holding id&gt;"). Entities are named by handle, encounters by name (their only address), never
/// an entity's or an item's name, and every call the fix needs is printed ready to send (one end call per blocking
/// encounter, each naming the campaign). Anything else (an entity no fight uses, the sheet of a fight that ended, a
/// combatant only linked to the character, another character's sheet-seeded combatant, a batch that only UPDATED what a
/// fight uses) still undoes.
/// </summary>
public sealed class UndoEngineEncounterGuardTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SeededCampaign _campaign;
    private readonly SeededEntity _pc;
    private readonly SeededEntity _other;

    public UndoEngineEncounterGuardTests()
    {
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        _campaign = seed.Campaign(name: "Belmakor", ruleset: CampaignValues.Rulesets.R2014);
        _pc = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc", status: "alive");
        _other = seed.Entity(_campaign.Id, CampaignValues.Kinds.Character, "Serif", subtype: "pc", status: "alive");
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(1, "encounter \"The crypt\" was run in it")]
    [InlineData(2, "encounters \"The crypt\", \"The dark station\" were run in it")]
    public void Undo_BatchThatCreatedASessionAnEncounterWasRunIn_IsRefusedAndTheEncounterKeepsItsSession(int encounters, string named)
    {
        var start = StartSession(6, out var sessionId);
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.Encounter(_campaign.Id, "The crypt", status: CampaignValues.EncounterStatuses.Active, sessionId: sessionId);
            if (encounters == 2)
            {
                seed.Encounter(_campaign.Id, "The dark station", status: CampaignValues.EncounterStatuses.Planned, sessionId: sessionId);
            }
        }

        var before = Snapshot();

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, start));

        Assert.Contains($"Batch {start} cannot be undone: it created session:6, and {named}", error.Message);
        Assert.Contains("Encounters are not in history", error.Message);
        Assert.Contains($"campaign_session {{\"action\": \"end\", \"campaign\": \"{_campaign.Slug}\"}}", error.Message);
        Assert.EndsWith("Nothing was changed.", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
        using var check = _db.Open();
        Assert.Equal(encounters, check.ExecuteScalar<long>("SELECT count(*) FROM encounter WHERE session_id = @sessionId", new { sessionId }));
    }

    public static TheoryData<string, string> EntityUses() => new()
    {
        { "combatant", "it is a combatant in encounter \"The crypt\"" },
        { "combatant of an ended encounter", "it is a combatant in encounter \"The crypt\"" },
        { "scene", "it is the scene of encounter \"The crypt\"" },
        { "session", "encounter \"The crypt\" was run in it" },
        { "combatant and scene", "it is a combatant in encounter \"The crypt\"; it is the scene of encounter \"The crypt\"" },
    };

    /// <summary>
    /// An entity the batch created that a fight uses: undoing would unlink the combatant (or the encounter's scene or
    /// session) for good. Refused naming the entity by handle (never its name: "Iron Guts" is the DM's) and offering the
    /// soft delete, which keeps the link; the fight is untouched.
    /// </summary>
    [Theory]
    [MemberData(nameof(EntityUses))]
    public void Undo_BatchThatCreatedAnEntityAnEncounterUses_IsRefusedNamingItByHandle(string use, string named)
    {
        string entity = null!;
        var batch = _db.Batch(_campaign.Id, r => entity = CreateEntity(r, "Iron Guts", use == "scene" ? "scene" : "character"));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            var status = use.Contains("ended", StringComparison.Ordinal) ? CampaignValues.EncounterStatuses.Ended : CampaignValues.EncounterStatuses.Active;
            var encounter = seed.Encounter(_campaign.Id, "The crypt", status: status,
                sceneId: use.Contains("scene", StringComparison.Ordinal) ? entity : null, sessionId: use == "session" ? entity : null);
            if (use.StartsWith("combatant", StringComparison.Ordinal))
            {
                seed.Combatant(encounter, "Iron Guts", entityId: entity, hp: 30, maxHp: 30);
            }
        }

        var handle = Handle(entity);
        var before = Snapshot();

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains($"Batch {batch} cannot be undone: it created entity {handle}, and {named}.", error.Message);
        Assert.Contains("a redo could not re-link them", error.Message);
        Assert.Contains(
            $"campaign_write {{\"ops\": [{{\"op\": \"delete\", \"ref\": \"{handle}\"}}], \"campaign\": \"{_campaign.Slug}\"}}",
            error.Message);
        Assert.DoesNotContain("Iron Guts", error.Message);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// An entity in many fights: the first five encounters are named, oldest first, and the rest counted, so the refusal
    /// stays short however many sandbox fights the entity was in.
    /// </summary>
    [Fact]
    public void Undo_BatchThatCreatedAnEntityManyEncountersUse_NamesFiveAndCountsTheRest()
    {
        string entity = null!;
        var batch = _db.Batch(_campaign.Id, r => entity = CreateEntity(r, "Iron Guts", "character"));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            for (var i = 1; i <= 6; i++)
            {
                var status = i == 1 ? CampaignValues.EncounterStatuses.Active : CampaignValues.EncounterStatuses.Ended;
                seed.Combatant(seed.Encounter(_campaign.Id, $"Fight {i}", status: status), "Iron Guts", entityId: entity);
            }
        }

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains(
            $"it created entity {Handle(entity)}, and it is a combatant in encounters \"Fight 1\", \"Fight 2\", \"Fight 3\", " +
            "\"Fight 4\", \"Fight 5\" and 1 more. ", error.Message);
        Assert.DoesNotContain("Fight 6", error.Message);
    }

    /// <summary>
    /// A batch that created a session AND an entity (a record_past or start that also made an NPC): the session's own
    /// guard does not cover the entity, so an entity a fight uses is still refused, by handle, even when no encounter was
    /// run in the session.
    /// </summary>
    [Fact]
    public void Undo_BatchThatCreatedASessionAndAnEntityAFightUses_IsRefusedNamingTheEntity()
    {
        var sessionId = CampaignDatabase.NewId();
        string entity = null!;
        var batch = _db.Batch(_campaign.Id, r =>
        {
            InsertSession(r, sessionId, 6);
            entity = CreateEntity(r, "Iron Guts", "character");
        }, sessionId: sessionId);
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.Combatant(seed.Encounter(_campaign.Id, "The crypt", status: CampaignValues.EncounterStatuses.Active), "Iron Guts", entityId: entity);
        }

        var before = Snapshot();

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains($"Batch {batch} cannot be undone: it created entity {Handle(entity)}, and it is a combatant in encounter \"The crypt\".",
            error.Message);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// What no fight uses still undoes: an entity no encounter names, and an entity created alongside a combatant that is
    /// linked to ANOTHER entity.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_BatchThatCreatedAnEntityNoEncounterUses_RemovesIt(bool anotherEntityFights)
    {
        string entity = null!;
        var batch = _db.Batch(_campaign.Id, r => entity = CreateEntity(r, "Iron Guts", "character"));
        if (anotherEntityFights)
        {
            using var connection = _db.Open();
            var seed = new CampaignSeed(connection);
            seed.Combatant(seed.Encounter(_campaign.Id, status: CampaignValues.EncounterStatuses.Active), "Belmakor", CampaignValues.CombatSides.Party,
                entityId: _pc.Id);
        }

        _db.Undo(_campaign.Id, batch);

        using var check = _db.Open();
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM entity WHERE id = @entity", new { entity }));
    }

    public static TheoryData<string, bool, bool, bool> SheetSeedings() => new()
    {
        { CampaignValues.EncounterStatuses.Planned, true, false, true },
        { CampaignValues.EncounterStatuses.Active, true, false, true },
        { CampaignValues.EncounterStatuses.Paused, true, false, true },
        { CampaignValues.EncounterStatuses.Ended, true, false, false },
        { CampaignValues.EncounterStatuses.Active, false, false, false },
        { CampaignValues.EncounterStatuses.Active, true, true, false },
        { CampaignValues.EncounterStatuses.Planned, true, true, false },
    };

    /// <summary>
    /// A sheet the batch created that a combatant of a fight not yet ended was seeded from (it has a sheet snapshot): the
    /// end-of-combat write-back would have no sheet to write to. Refused, naming the encounter and its status and giving
    /// the end call; once the fight has ended, when the combatant only links the character (played from a stat block, no
    /// snapshot), or when the sheet-seeded combatant is ANOTHER character (a party fight holds several), the sheet's
    /// creation undoes.
    /// </summary>
    [Theory]
    [MemberData(nameof(SheetSeedings))]
    public void Undo_BatchThatCreatedASheetAFightWasSeededFrom_IsRefusedUntilTheFightEnds(string status, bool seeded, bool otherCharacter, bool refused)
    {
        var batch = _db.Batch(_campaign.Id, r => CreateSheet(r));
        var fighter = otherCharacter ? _other : _pc;
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            var encounter = seed.Encounter(_campaign.Id, "The crypt", status: status);
            seed.Combatant(encounter, fighter.Name, CampaignValues.CombatSides.Party, entityId: fighter.Id, sheetSnapshot: seeded ? "{\"hp\":98}" : null);
        }

        var before = Snapshot();

        var error = Record.Exception(() => _db.Undo(_campaign.Id, batch));

        using var check = _db.Open();
        if (refused)
        {
            var refusal = Assert.IsType<DndInputException>(error);
            Assert.Contains(
                $"Batch {batch} cannot be undone: it created the character sheet of {_pc.SeqHandle}, and encounter \"The crypt\" ({status}) " +
                "seeded a combatant from it", refusal.Message);
            Assert.Contains(
                $"combat {{\"action\": \"end\", \"encounter\": \"The crypt\", \"discard\": true, \"campaign\": \"{_campaign.Slug}\"}}",
                refusal.Message);
            Assert.DoesNotContain("Belmakor Silverwind", refusal.Message);
            Assert.Equal(before, Snapshot());
            Assert.Equal(1, check.ExecuteScalar<long>("SELECT count(*) FROM character_sheet"));
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM character_sheet"));
            Assert.Equal(1, check.ExecuteScalar<long>("SELECT count(*) FROM combatant WHERE entity_id = @id", new { id = fighter.Id }));
        }
    }

    /// <summary>
    /// A sheet several unended fights were seeded from (a prepared ambush and the fight in progress): every one of them
    /// blocks the undo, so the refusal gives one ready end call per encounter, oldest first (one call for all of them would
    /// clear only the first, and the next undo would be refused again). Past five the rest are counted, and the next undo
    /// names them: following each refusal's calls always reaches an undo that goes through.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void Undo_BatchThatCreatedASheetSeveralFightsWereSeededFrom_GivesAnEndCallForEach(int fights)
    {
        var batch = _db.Batch(_campaign.Id, r => CreateSheet(r));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            for (var i = 1; i <= fights; i++)
            {
                var status = i == 1 ? CampaignValues.EncounterStatuses.Active : CampaignValues.EncounterStatuses.Planned;
                seed.Combatant(seed.Encounter(_campaign.Id, $"Fight {i}", status: status), "Belmakor", CampaignValues.CombatSides.Party,
                    entityId: _pc.Id, sheetSnapshot: "{}");
            }
        }

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        var listed = Enumerable.Range(1, Math.Min(fights, 5)).ToList();
        Assert.Contains(
            $"it created the character sheet of {_pc.SeqHandle}, and encounters " +
            string.Join(", ", listed.Select(i => $"\"Fight {i}\" ({(i == 1 ? "active" : "planned")})")) +
            (fights > 5 ? $" and {fights - 5} more" : string.Empty) + " seeded a combatant from it", error.Message);
        Assert.Contains(
            "End each of those encounters first, writing nothing back (" + string.Join("; ", listed.Select(EndCall)) +
            (fights > 5 ? $"; and {fights - 5} more, which the next undo names" : string.Empty) + "), then undo again. Nothing was changed.",
            error.Message);
        if (fights <= 5)
        {
            return;
        }

        Assert.DoesNotContain("Fight 6", error.Message);
        EndEncounters(listed.Select(i => $"Fight {i}"));
        var next = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));
        Assert.Contains("and encounter \"Fight 6\" (planned) seeded a combatant from it", next.Message);
        Assert.Contains($"End that encounter first ({EndCall(6)} ends it writing nothing back), then undo again.", next.Message);
        EndEncounters(["Fight 6"]);
        _db.Undo(_campaign.Id, batch);
    }

    /// <summary>
    /// Encounters made in the same millisecond (a script preparing several) have the same created_at and UUIDv7 ids in no
    /// particular order, so ties are listed in the order the encounters were made (rowid), never by id: the refusal and its
    /// calls read the same on every run.
    /// </summary>
    [Fact]
    public void Undo_EncountersMadeInTheSameMillisecond_AreListedInTheOrderTheyWereMade()
    {
        var batch = _db.Batch(_campaign.Id, r => CreateSheet(r));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            foreach (var (id, name) in new[] { ("ffffffff-0000-7000-8000-000000000000", "Made first"), ("00000000-0000-7000-8000-000000000000", "Made second") })
            {
                connection.Execute(
                    "INSERT INTO encounter(id, campaign_id, name, ruleset, status, created_at, updated_at) " +
                    "VALUES (@id, @campaignId, @name, '2014', 'planned', @at, @at)",
                    new { id, campaignId = _campaign.Id, name, at = seed.At });
                seed.Combatant(id, "Belmakor", CampaignValues.CombatSides.Party, entityId: _pc.Id, sheetSnapshot: "{}");
            }
        }

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains("encounters \"Made first\" (planned), \"Made second\" (planned) seeded", error.Message);
        Assert.Contains($"({EndCall("Made first")}; {EndCall("Made second")})", error.Message);
    }

    /// <summary>
    /// A holding the batch created (a potion added to the inventory) that a combatant of a fight not yet ended draws on
    /// (its resources key "item:&lt;holding id&gt;", which no foreign key guards): the write-back would have no holding to
    /// take the used quantity from. Refused naming the holder by handle and the resource key (never the item's name),
    /// with the end call; once the fight has ended, or when the fight draws on another holding, the holding's creation
    /// undoes.
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.EncounterStatuses.Planned, true, true)]
    [InlineData(CampaignValues.EncounterStatuses.Active, true, true)]
    [InlineData(CampaignValues.EncounterStatuses.Paused, true, true)]
    [InlineData(CampaignValues.EncounterStatuses.Ended, true, false)]
    [InlineData(CampaignValues.EncounterStatuses.Active, false, false)]
    public void Undo_BatchThatCreatedAHoldingAFightDrawsOn_IsRefusedUntilTheFightEnds(string status, bool drawsOnIt, bool refused)
    {
        string holding = null!;
        var batch = _db.Batch(_campaign.Id, r => holding = CreateHolding(r, "Potion of healing"));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            var rope = seed.Holding(_campaign.Id, _pc.Id, "Rope");
            seed.Combatant(seed.Encounter(_campaign.Id, "The crypt", status: status), "Belmakor", CampaignValues.CombatSides.Party,
                entityId: _pc.Id, sheetSnapshot: "{}", resources: ItemResources(drawsOnIt ? holding : rope));
        }

        var before = Snapshot();

        var error = Record.Exception(() => _db.Undo(_campaign.Id, batch));

        using var check = _db.Open();
        if (refused)
        {
            var refusal = Assert.IsType<DndInputException>(error);
            Assert.Contains(
                $"Batch {batch} cannot be undone: it created a holding of {_pc.SeqHandle} (combat resource \"item:{holding}\"), and a " +
                $"combatant in encounter \"The crypt\" ({status}) draws on it: the end-of-combat write-back would have no holding to " +
                $"take the used quantity from. End that encounter first ({EndCall("The crypt")} ends it writing nothing back), then " +
                "undo again. Nothing was changed.", refusal.Message);
            Assert.DoesNotContain("Potion", refusal.Message);
            Assert.DoesNotContain("Belmakor Silverwind", refusal.Message);
            Assert.Equal(before, Snapshot());
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM holding WHERE id = @holding", new { holding }));
        }
    }

    public static TheoryData<string> Changes() => new() { "sheet hp", "sheet resource", "entity summary", "entity delete", "holding quantity" };

    /// <summary>
    /// Only creates are guarded: a batch that only UPDATED a sheet, an entity or a holding a fight uses (a pre-fight heal,
    /// a resource spent, a summary edit, a soft delete, a potion count) undoes while an active and a planned fight are seeded
    /// from that character and draw on that holding, because undoing it writes the old value back and unlinks nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(Changes))]
    public void Undo_BatchThatOnlyChangedWhatAFightUses_IsNotRefused(string change)
    {
        string holding;
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.CharacterSheet(_pc.Id, level: 12, maxHp: 98, hp: 98,
                resources: "{\"bladesong\":{\"name\":\"Bladesong\",\"max\":4,\"used\":0,\"recharge\":\"long_rest\"}}");
            holding = seed.Holding(_campaign.Id, _pc.Id, "Potion of healing", quantity: 2);
            foreach (var (name, status) in new[] { ("The crypt", CampaignValues.EncounterStatuses.Active), ("Ambush", CampaignValues.EncounterStatuses.Planned) })
            {
                seed.Combatant(seed.Encounter(_campaign.Id, name, status: status), "Belmakor", CampaignValues.CombatSides.Party,
                    entityId: _pc.Id, sheetSnapshot: "{\"hp\":98}", resources: ItemResources(holding));
            }
        }

        var read = change switch
        {
            "sheet hp" => "SELECT CAST(hp AS TEXT) FROM character_sheet WHERE entity_id = @id",
            "sheet resource" => "SELECT CAST(json_extract(resources, '$.bladesong.used') AS TEXT) FROM character_sheet WHERE entity_id = @id",
            "entity summary" => "SELECT summary FROM entity WHERE id = @id",
            "entity delete" => "SELECT deleted_at FROM entity WHERE id = @id",
            _ => "SELECT CAST(quantity AS TEXT) FROM holding WHERE id = @holding",
        };
        var before = Read(read, holding);
        var batch = _db.Batch(_campaign.Id, r =>
        {
            switch (change)
            {
                case "sheet hp":
                    r.Update("character_sheet", _pc.Id, new Dictionary<string, object?> { ["hp"] = 40 }, "damage");
                    break;
                case "sheet resource":
                    r.PatchObject("character_sheet", _pc.Id, "resources", new JsonObject { ["bladesong"] = new JsonObject { ["used"] = 2 } }, "use");
                    break;
                case "entity summary":
                    r.Update("entity", _pc.Id, new Dictionary<string, object?> { ["summary"] = "Bladesinger of the band." }, "upsert");
                    break;
                case "entity delete":
                    r.SoftDelete("entity", _pc.Id);
                    break;
                default:
                    r.Update("holding", holding, new Dictionary<string, object?> { ["quantity"] = 1.0 }, "inventory");
                    break;
            }
        });
        Assert.NotEqual(before, Read(read, holding));

        _db.Undo(_campaign.Id, batch);

        Assert.Equal(before, Read(read, holding));
    }

    /// <summary>
    /// A redo that would delete a sheet again is a batch that created it (its reversal of a delete): the same guard
    /// applies, so "delete the sheet, undo, start a fight from it, redo" cannot pull the sheet out from under the fight.
    /// </summary>
    [Fact]
    public void Undo_RedoThatWouldDeleteASheetAFightWasSeededFrom_IsRefused()
    {
        _db.Batch(_campaign.Id, r => CreateSheet(r));
        var delete = _db.Batch(_campaign.Id, r => r.Delete("character_sheet", _pc.Id, "update"));
        var undo = _db.Undo(_campaign.Id, delete);
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.Combatant(seed.Encounter(_campaign.Id, "The crypt", status: CampaignValues.EncounterStatuses.Active), "Belmakor",
                CampaignValues.CombatSides.Party, entityId: _pc.Id, sheetSnapshot: "{}");
        }

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, undo.UndoBatchId));

        Assert.Contains($"it created the character sheet of {_pc.SeqHandle}", error.Message);
    }

    [Theory]
    [InlineData(1, "an encounter was run in it")]
    [InlineData(3, "3 encounters were run in it")]
    public void Undo_CampaignCreationBatchOfACampaignWithEncounters_IsRefused(int encounters, string howMany)
    {
        var campaignId = CampaignDatabase.NewId();
        var batch = _db.Batch(campaignId, r => r.Insert("campaign", new Dictionary<string, object?>
        {
            ["id"] = campaignId, ["slug"] = "one-piece", ["name"] = "One Piece", ["role"] = "dm", ["ruleset"] = "2024",
        }, "create"));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            for (var i = 0; i < encounters; i++)
            {
                seed.Encounter(campaignId, $"Sandbox {i}", status: CampaignValues.EncounterStatuses.Ended);
            }
        }

        var error = Assert.Throws<DndInputException>(() => _db.Undo(campaignId, batch));

        Assert.Contains($"it created campaign one-piece, and {howMany}", error.Message);
        Assert.DoesNotContain("Sandbox", error.Message);
        using var check = _db.Open();
        Assert.Equal(encounters, check.ExecuteScalar<long>("SELECT count(*) FROM encounter WHERE campaign_id = @campaignId", new { campaignId }));
    }

    /// <summary>
    /// The encounter guard comes before the conflict scan: undoing the later batches first would gain nothing while the
    /// fight still uses the entity, so the refusal says the thing that actually blocks the undo.
    /// </summary>
    [Fact]
    public void Undo_EntityAFightUsesAndALaterEditConflicts_ReportsTheFightFirst()
    {
        string entity = null!;
        var batch = _db.Batch(_campaign.Id, r => entity = CreateEntity(r, "Iron Guts", "character"));
        _db.Batch(_campaign.Id, r => r.Update("entity", entity, new Dictionary<string, object?> { ["status"] = "dead" }, "status"));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.Combatant(seed.Encounter(_campaign.Id, status: CampaignValues.EncounterStatuses.Active), "Iron Guts", entityId: entity);
        }

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains("it is a combatant in encounter", error.Message);
        Assert.DoesNotContain("later changes build on", error.Message);
    }

    /// <summary>An encounter name is quoted as a JSON string, so one holding quotes reads (and pastes into the call) unambiguously.</summary>
    [Fact]
    public void Undo_EncounterNameWithQuotes_IsQuotedAsJson()
    {
        var batch = _db.Batch(_campaign.Id, r => CreateSheet(r));
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            seed.Combatant(seed.Encounter(_campaign.Id, "The \"Nester\" lair", status: CampaignValues.EncounterStatuses.Active), "Belmakor",
                CampaignValues.CombatSides.Party, entityId: _pc.Id, sheetSnapshot: "{}");
        }

        var error = Assert.Throws<DndInputException>(() => _db.Undo(_campaign.Id, batch));

        Assert.Contains("encounter \"The \\\"Nester\\\" lair\" (active)", error.Message);
        Assert.Contains($"\"encounter\": \"The \\\"Nester\\\" lair\", \"discard\": true, \"campaign\": \"{_campaign.Slug}\"}}", error.Message);
    }

    // What campaign_session start does for a new session: the session entity and its live row in one batch, filed under it.
    private string StartSession(int number, out string sessionId)
    {
        var id = CampaignDatabase.NewId();
        sessionId = id;
        return _db.Batch(_campaign.Id, r => InsertSession(r, id, number), sessionId: id);
    }

    private void InsertSession(ChangeRecorder r, string id, int number)
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
    }

    private string CreateHolding(ChangeRecorder r, string name) =>
        (string)r.Insert("holding", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["holder_id"] = _pc.Id, ["name"] = name, ["quantity"] = 2.0,
        }, "inventory")["id"]!;

    // A combatant's resources drawing on one holding (contract §4's "item:<holding id>" key).
    private static string ItemResources(string holdingId) =>
        new JsonObject { ["item:" + holdingId] = new JsonObject { ["name"] = "Potion of healing", ["used"] = 1 } }.ToJsonString();

    // The call the refusals print to end an encounter with no write-back.
    private string EndCall(string encounter) =>
        $"combat {{\"action\": \"end\", \"encounter\": \"{encounter}\", \"discard\": true, \"campaign\": \"{_campaign.Slug}\"}}";

    private string EndCall(int fight) => EndCall($"Fight {fight}");

    // What following the printed end calls does to the encounters (K's end; raw here).
    private void EndEncounters(IEnumerable<string> names)
    {
        using var connection = _db.Open();
        foreach (var name in names)
        {
            connection.Execute("UPDATE encounter SET status = 'ended' WHERE campaign_id = @campaignId AND name = @name",
                new { campaignId = _campaign.Id, name });
        }
    }

    private string? Read(string sql, string holding)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<string?>(sql, new { id = _pc.Id, holding });
    }

    private string CreateEntity(ChangeRecorder r, string name, string kind) =>
        (string)r.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["kind"] = kind, ["slug"] = CampaignSlugs.From(name, kind), ["name"] = name,
        }, "upsert")["id"]!;

    private void CreateSheet(ChangeRecorder r) =>
        r.Insert("character_sheet", new Dictionary<string, object?>
        {
            ["entity_id"] = _pc.Id, ["ruleset"] = "2014", ["level"] = 12, ["max_hp"] = 98, ["hp"] = 98,
            ["classes"] = new JsonArray(new JsonObject { ["class"] = "wizard", ["level"] = 12 }),
        }, "update");

    private string Handle(string entityId)
    {
        using var connection = _db.Open();
        return "e:" + connection.ExecuteScalar<long>("SELECT seq FROM entity WHERE id = @entityId", new { entityId });
    }

    // Every loggable table and every tracker table (updated_at aside), as text: a refused undo leaves all of it as it was.
    private string Snapshot()
    {
        using var connection = _db.Open();
        var text = new StringBuilder();
        var tables = CampaignTables.All.Select(t => (t.Name, Order: string.Join(", ", t.KeyColumns)))
            .Concat([("encounter", "id"), ("combatant", "id"), ("combat_log", "seq"), ("dice_roll", "seq")]);
        foreach (var (table, order) in tables)
        {
            text.AppendLine("## " + table);
            foreach (var row in connection.Query($"SELECT * FROM {table} ORDER BY {order}"))
            {
                text.AppendLine(string.Join(" | ", ((IDictionary<string, object?>)row).Where(p => p.Key != "updated_at").Select(p => $"{p.Key}={p.Value ?? "NULL"}")));
            }
        }

        text.AppendLine(connection.ExecuteScalar<long>("SELECT count(*) FROM change_log").ToString(System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }
}
