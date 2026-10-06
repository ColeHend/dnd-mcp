using System.Reflection;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: every row record lists every column of its table, in table order, with CLR types Dapper can bind
/// positionally (long for INTEGER, double for REAL, string for TEXT and JSON). Dapper binds records by constructor
/// position without conversion (DapperMappingTests), so a record one column short, reordered, or with an int where SQLite
/// hands back a long throws on the first read, in whichever tool first reads that table. A migration that adds a column
/// fails here until the record knows it.
/// </summary>
public sealed class CampaignRowsTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly Fixture _rows;

    public CampaignRowsTests()
    {
        _connection = _db.Open();
        _rows = Seed(new CampaignSeed(_connection));
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    public static TheoryData<string, string> Records() => new()
    {
        { nameof(CampaignRow), "campaign" },
        { nameof(EntityRow), "entity" },
        { nameof(AliasRow), "entity_alias" },
        { nameof(TagRow), "tag" },
        { nameof(EntityTagRow), "entity_tag" },
        { nameof(RelationRow), "relation" },
        { nameof(CrossLinkRow), "cross_link" },
        { nameof(FactRow), "fact" },
        { nameof(FactLinkRow), "fact_link" },
        { nameof(FactDependencyRow), "fact_dependency" },
        { nameof(KnowledgeRow), "knowledge" },
        { nameof(SessionRow), "session" },
        { nameof(AttendanceRow), "session_attendance" },
        { nameof(ObjectiveRow), "objective" },
        { nameof(ClockRow), "clock" },
        { nameof(BeatEdgeRow), "beat_edge" },
        { nameof(DiceRollRow), "dice_roll" },
        { nameof(ChangeRow), "change_log" },
        { nameof(CharacterSheetRow), "character_sheet" },
        { nameof(HoldingRow), "holding" },
        { nameof(CurrencyTxnRow), "currency_txn" },
        { nameof(AwardRow), "award" },
        { nameof(EncounterRow), "encounter" },
        { nameof(CombatantRow), "combatant" },
        { nameof(CombatLogRow), "combat_log" },
    };

    [Theory]
    [MemberData(nameof(Records))]
    public void Columns_ListEveryColumnOfTheTableInOrder(string record, string table)
    {
        var columns = (string)RecordType(record).GetField("Columns", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        var actual = _connection.Query<string>($"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");

        Assert.Equal(actual, columns.Split(',').Select(c => c.Trim()));
    }

    /// <summary>Each record's constructor parameters are its columns in order, with Dapper-bindable types.</summary>
    [Theory]
    [MemberData(nameof(Records))]
    public void Constructor_ParametersAreTheColumnsWithBindableTypes(string record, string table)
    {
        var type = RecordType(record);
        var parameters = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First().GetParameters();
        var columns = _connection.Query<(string Name, string Type)>($"SELECT name, type FROM pragma_table_info('{table}') ORDER BY cid").ToList();

        Assert.Equal(columns.Count, parameters.Length);
        foreach (var (parameter, column) in parameters.Zip(columns))
        {
            Assert.Equal(column.Name, CampaignRowsSnake(parameter.Name!));
            var clr = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
            var expected = column.Type switch
            {
                "INTEGER" => typeof(long),
                "REAL" => typeof(double),
                _ => typeof(string),
            };
            Assert.True(expected == clr, $"{record}.{parameter.Name} is {clr.Name}; column {column.Name} is {column.Type}");
        }
    }

    /// <summary>Every record actually materializes from a real row, with the nullable columns filled.</summary>
    [Fact]
    public void Query_EveryRecord_MaterializesFromTheSeededRows()
    {
        Assert.Equal(_rows.Campaign.Id, Single<CampaignRow>("campaign", $"id = '{_rows.Campaign.Id}'").Id);
        var entity = Single<EntityRow>("entity", $"id = '{_rows.Npc.Id}'");
        Assert.Equal(2.5, entity.SortKey);
        Assert.Equal("Q7", entity.Code);
        Assert.Equal("{\"attitude\":-40}", entity.Data);
        Assert.Single(Rows<AliasRow>("entity_alias"));
        Assert.Single(Rows<TagRow>("tag"));
        Assert.Single(Rows<EntityTagRow>("entity_tag"));
        var relation = Rows<RelationRow>("relation").Single(r => r.Rel == "ally_of");
        Assert.Equal(-40L, relation.Attitude);
        Assert.Equal(1L, relation.Symmetric);
        Assert.Single(Rows<CrossLinkRow>("cross_link"));
        var fact = Rows<FactRow>("fact").Single(f => f.Code == "F3");
        Assert.Equal("{\"after\":[]}", fact.Gate);
        Assert.Single(Rows<FactLinkRow>("fact_link"));
        Assert.Single(Rows<FactDependencyRow>("fact_dependency"));
        var knowledge = Assert.Single(Rows<KnowledgeRow>("knowledge"));
        Assert.Equal("the old king", knowledge.KnownAs);
        var session = Assert.Single(Rows<SessionRow>("session"));
        Assert.Equal(3L, session.Number);
        Assert.Equal(1L, Assert.Single(Rows<AttendanceRow>("session_attendance")).Present);
        var objective = Assert.Single(Rows<ObjectiveRow>("objective"));
        Assert.Equal(1.0, objective.Ordinal);
        Assert.Equal(4L, objective.ProgressMax);
        Assert.Equal(6L, Assert.Single(Rows<ClockRow>("clock")).Segments);
        Assert.Single(Rows<BeatEdgeRow>("beat_edge"));
        Assert.Equal(1L, Rows<DiceRollRow>("dice_roll").Single(r => r.EncounterId is null).Outcome);
        Assert.Empty(Rows<ChangeRow>("change_log"));
        var sheet = Assert.Single(Rows<CharacterSheetRow>("character_sheet"));
        Assert.Equal((12L, 3L, 17L, 1L), (sheet.Level!.Value, sheet.MaxHpReduction, sheet.SpellSaveDc!.Value, sheet.Inspiration));
        Assert.Equal("{\"spell\":\"Circle of Power\",\"level\":5}", sheet.Concentration);
        Assert.Equal("{\"build\":\"bladesinger\"}", sheet.SimProfile);
        var holding = Assert.Single(Rows<HoldingRow>("holding"));
        Assert.Equal((2.5, 1L, "{\"max\":7,\"used\":2}"), (holding.Quantity, holding.Attuned, holding.Charges));
        Assert.Equal(-15L, Assert.Single(Rows<CurrencyTxnRow>("currency_txn")).Gp);
        Assert.Equal(450L, Assert.Single(Rows<AwardRow>("award")).Amount);
        var encounter = Assert.Single(Rows<EncounterRow>("encounter"));
        Assert.Equal((3L, 1L, "batch-1"), (encounter.Round, encounter.Lair, encounter.WritebackBatchId));
        var combatant = Assert.Single(Rows<CombatantRow>("combatant"));
        Assert.Equal((14.5, 2.0, 4L), (combatant.Initiative!.Value, combatant.OrderKey, combatant.Exhaustion));
        Assert.Equal("{\"actions\":3,\"used\":1}", combatant.Legendary);
        var log = Assert.Single(Rows<CombatLogRow>("combat_log"));
        Assert.Equal((combatant.Id, -7L), (log.TargetId, log.Amount!.Value));
        Assert.Equal(encounter.Id, Rows<DiceRollRow>("dice_roll").Single(r => r.Id == log.RollId).EncounterId);
    }

    /// <summary>
    /// <see cref="CampaignRows.FromValues{T}"/> turns a generic row (what replay returns) into the same record Dapper
    /// reads, so an as-of-session read renders through the same code as a current one.
    /// </summary>
    [Fact]
    public void FromValues_GenericRows_EqualTheRecordsDapperReads()
    {
        Assert.Equal(Single<EntityRow>("entity", $"id = '{_rows.Npc.Id}'"), Generic<EntityRow>("entity", _rows.Npc.Id));
        Assert.Equal(Single<CampaignRow>("campaign", $"id = '{_rows.Campaign.Id}'"), Generic<CampaignRow>("campaign", _rows.Campaign.Id));
        var fact = Rows<FactRow>("fact").Single(f => f.Code == "F3");
        Assert.Equal(fact, Generic<FactRow>("fact", fact.Id));
        var relation = Rows<RelationRow>("relation").Single(r => r.Rel == "ally_of");
        Assert.Equal(relation, Generic<RelationRow>("relation", relation.Id));
        var knowledge = Rows<KnowledgeRow>("knowledge").Single();
        Assert.Equal(knowledge, Generic<KnowledgeRow>("knowledge", knowledge.Id));
        var session = Rows<SessionRow>("session").Single();
        Assert.Equal(session, Generic<SessionRow>("session", session.EntityId));
        var objective = Rows<ObjectiveRow>("objective").Single();
        Assert.Equal(objective, Generic<ObjectiveRow>("objective", objective.Id));
        var sheet = Rows<CharacterSheetRow>("character_sheet").Single();
        Assert.Equal(sheet, Generic<CharacterSheetRow>("character_sheet", sheet.EntityId));
        var holding = Rows<HoldingRow>("holding").Single();
        Assert.Equal(holding, Generic<HoldingRow>("holding", holding.Id));
        var coins = Rows<CurrencyTxnRow>("currency_txn").Single();
        Assert.Equal(coins, Generic<CurrencyTxnRow>("currency_txn", coins.Id));
        var award = Rows<AwardRow>("award").Single();
        Assert.Equal(award, Generic<AwardRow>("award", award.Id));
    }

    [Fact]
    public void FromValues_MissingOrMistypedColumn_Throws()
    {
        var values = ChangeReplay.RowAsOf(_connection, "entity", _rows.Npc.Id, 999)!.ToDictionary(p => p.Key, p => p.Value);
        var missing = values.Where(p => p.Key != "slug").ToDictionary(p => p.Key, p => p.Value);
        var mistyped = new Dictionary<string, object?>(values) { ["seq"] = "12" };

        Assert.Contains("slug", Assert.Throws<ArgumentException>(() => CampaignRows.FromValues<EntityRow>(missing)).Message);
        Assert.Contains("seq", Assert.Throws<ArgumentException>(() => CampaignRows.FromValues<EntityRow>(mistyped)).Message);
    }

    [Fact]
    public void Prefixed_Columns_GetTheTableAlias() =>
        Assert.Equal("e.entity_id, e.alias, e.visibility", CampaignRows.Prefixed(AliasRow.Columns, "e"));

    [Fact]
    public void Handles_OnEntityAndFactRows_AreSeqAndKindSlug()
    {
        var entity = Single<EntityRow>("entity", $"id = '{_rows.Npc.Id}'");

        Assert.Equal("character:iron-guts", entity.Handle);
        Assert.Equal(_rows.Npc.SeqHandle, entity.SeqHandle);
        Assert.False(entity.IsDeleted);
    }

    private static string CampaignRowsSnake(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : string.Empty) + char.ToLowerInvariant(c) : c.ToString()));

    private static Type RecordType(string name) => typeof(EntityRow).Assembly.GetType("DndMcp.Repository.Campaign." + name, throwOnError: true)!;

    private T Single<T>(string table, string where)
    {
        var columns = (string)typeof(T).GetField("Columns")!.GetValue(null)!;
        return _connection.QuerySingle<T>($"SELECT {columns} FROM {table} WHERE {where}");
    }

    private List<T> Rows<T>(string table)
    {
        var columns = (string)typeof(T).GetField("Columns")!.GetValue(null)!;
        return _connection.Query<T>($"SELECT {columns} FROM {table}").ToList();
    }

    private T Generic<T>(string table, string id)
        where T : class =>
        CampaignRows.FromValues<T>(ChangeReplay.RowAsOf(_connection, table, id, 999)!);

    // One row in every table, with the nullable columns filled where it matters for binding.
    private static Fixture Seed(CampaignSeed seed)
    {
        var campaign = seed.Campaign(dmName: "Mira", settings: "{\"default_visibility\":\"party\"}");
        var session = seed.Session(campaign.Id, 3, playedOn: "2026-08-30");
        var npc = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Iron Guts", subtype: "npc", code: "Q7", sortKey: 2.5,
            data: "{\"attitude\":-40}", introducedSessionId: session.EntityId, source: "S3 notes", parentId: campaign.Party!.Id);
        var pc = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc");
        var clock = seed.Entity(campaign.Id, CampaignValues.Kinds.Clock, "Doom");
        var quest = seed.Entity(campaign.Id, CampaignValues.Kinds.Quest, "Find the old king");
        var beatA = seed.Entity(campaign.Id, CampaignValues.Kinds.Beat, "Arrive");
        var beatB = seed.Entity(campaign.Id, CampaignValues.Kinds.Beat, "Leave");
        seed.Alias(npc.Id, "Guts", "public");
        seed.Tag(campaign.Id, npc.Id, "villain");
        seed.Relation(campaign.Id, npc.Id, "ally_of", pc.Id, label: "uneasy", attitude: -40, symmetric: true,
            sinceSessionId: session.EntityId, untilSessionId: session.EntityId, data: "{\"since\":\"the raid\"}");
        seed.CrossLink(npc.Id, pc.Id, "same soul");
        var first = seed.Fact(campaign.Id, "Iron Guts owes the party.");
        var second = seed.Fact(campaign.Id, "Iron Guts will pay.", code: "F3", gate: "{\"after\":[]}", establishedSessionId: session.EntityId,
            source: "S3", supersededBy: first.Id);
        seed.FactLink(second.Id, npc.Id);
        seed.FactDependency(second.Id, first.Id);
        seed.Knowledge(campaign.Id, CampaignValues.KnowerKinds.Character, pc.Id, CampaignValues.KnowledgeStates.Met, entityId: npc.Id,
            knownAs: "the old king", learnedSessionId: session.EntityId, learnedIngame: "Day 3", viaEntityId: npc.Id, how: "told",
            note: "at the docks", validUntilSessionId: session.EntityId);
        seed.Attendance(session.EntityId, pc.Id, note: "late");
        seed.Clock(clock.Id, 6, 2, frontId: quest.Id, shownToPlayers: true, onFillMd: "The tower falls.");
        seed.Objective(quest.Id, "Climb the tower", progress: 1, progressMax: 4, resolvedSessionId: session.EntityId);
        seed.BeatEdge(campaign.Id, beatA.Id, beatB.Id);
        seed.DiceRoll(campaign.Id, session.EntityId, label: "Perception", outcome: 1, detail: "{\"rolls\":[14]}");
        seed.CharacterSheet(pc.Id, player: "Cole", ruleset: "2014", species: "elf", lineage: "high elf", background: "entertainer",
            size: "medium", classes: "[{\"class\":\"wizard\",\"subclass\":\"bladesinger\",\"level\":12}]", level: 12, xp: 100000,
            abilities: "{\"int\":20}", ac: 15, maxHp: 98, maxHpReduction: 3, hp: 41, tempHp: 5, speed: 30, initiativeBonus: 5,
            passivePerception: 13, spellSaveDc: 17, spellAttack: 9, concentration: "{\"spell\":\"Circle of Power\",\"level\":5}",
            exhaustion: 1, inspiration: true, simProfile: "{\"build\":\"bladesinger\"}", notesMd: "notes", sheetSource: "pwa");
        seed.Holding(campaign.Id, pc.Id, "Arrows", quantity: 2.5, itemId: quest.Id, srdRef: "arrow", equipped: true, attuned: true,
            charges: "{\"max\":7,\"used\":2}", acquiredSessionId: session.EntityId, notes: "fletched");
        seed.CurrencyTxn(campaign.Id, pc.Id, "bribe", cp: 1, sp: 2, ep: 3, gp: -15, pp: 4, sessionId: session.EntityId);
        seed.Award(campaign.Id, pc.Id, amount: 450, sessionId: session.EntityId, note: "the crypt", source: "encounter: The crypt");
        var encounter = seed.Encounter(campaign.Id, "The crypt", status: CampaignValues.EncounterStatuses.Ended, sessionId: session.EntityId,
            sceneId: beatA.Id, round: 3, lair: true, data: "{\"x\":1}", notesMd: "n", outcomeMd: "won", writebackBatchId: "batch-1",
            startedAt: seed.At, endedAt: seed.At);
        var combatant = seed.Combatant(encounter, "Iron Guts", entityId: npc.Id, initGroup: "g1", srdRef: "mummy", statblock: "{}",
            initiative: 14.5, initBonus: 2, ac: 11, maxHp: 58, hp: 30, tempHp: 4, damageTaken: 28, concentration: "{}",
            makesDeathSaves: true, exhaustion: 4, legendary: "{\"actions\":3,\"used\":1}", sheetSnapshot: "{}", reactionUsed: true,
            surprised: true, hidden: true, defeated: true, dead: true, removed: true, orderKey: 2);
        var roll = CampaignDatabase.NewId();
        seed.DiceRoll(campaign.Id, null, "2d6+3", total: 7, id: roll, encounterId: encounter);
        seed.CombatLog(encounter, CampaignValues.CombatLogKinds.Damage, round: 3, turnCombatantId: combatant, actorId: combatant,
            targetId: combatant, amount: -7, detail: "{\"given\":true}", rollId: roll);
        return new Fixture(campaign, npc);
    }

    private sealed record Fixture(SeededCampaign Campaign, SeededEntity Npc);
}
