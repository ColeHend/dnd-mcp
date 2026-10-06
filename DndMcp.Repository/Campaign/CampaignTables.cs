using System.Text.Json;
using System.Text.Json.Nodes;

namespace DndMcp.Repository.Campaign;

/// <summary>How a column's value is stored and logged. In-process only: nothing stores these names.</summary>
public enum CampaignColumnType
{
    /// <summary>SQLite TEXT, a C# <see cref="string"/>.</summary>
    Text,

    /// <summary>SQLite INTEGER, a C# <see cref="long"/> (booleans are 0/1).</summary>
    Integer,

    /// <summary>SQLite REAL, a C# <see cref="double"/>; never NaN or infinity.</summary>
    Real,

    /// <summary>TEXT holding a JSON object (the schema CHECKs <c>json_type(x) = 'object'</c>).</summary>
    JsonObject,

    /// <summary>TEXT holding a JSON array (<c>json_type(x) = 'array'</c>).</summary>
    JsonArray,

    /// <summary>TEXT holding any JSON value. No 0001 column uses it; later migrations may.</summary>
    JsonAny,
}

/// <summary>One column of a loggable table.</summary>
/// <param name="Name">The SQL column name.</param>
/// <param name="Type">How it is stored.</param>
/// <param name="Nullable">True when the column accepts NULL.</param>
/// <param name="LoggedPerKey">
/// True for the free-form object columns (<c>data</c>, <c>settings</c>) and the character sheet's keyed trackers
/// (<c>abilities</c>, <c>hit_dice</c>, <c>spell_slots</c>, <c>resources</c>): an update logs one change_log row per changed
/// top-level key (<c>field_path</c> = <c>data.&lt;key&gt;</c>, <c>spell_slots.1</c>) rather than the whole document, so
/// history reads "data.attitude: 20 → -40" and undoing one key's change cannot clobber another key written later (the
/// end-of-combat write-back that spent a 3rd-level slot stays undoable after a later rest touched only <c>spell_slots.1</c>).
/// </param>
/// <param name="Logged">
/// False for columns whose changes never reach change_log: <c>updated_at</c> (bookkeeping, not history) and
/// <c>session.live_log</c> (the live scratchpad that becomes the recap; logging every note would bury the batches that
/// matter). Full-row snapshots (create and delete rows) still include them.
/// </param>
public sealed record CampaignColumn(
    string Name,
    CampaignColumnType Type,
    bool Nullable,
    bool LoggedPerKey = false,
    bool Logged = true)
{
    /// <summary>True for the JSON column types (stored as TEXT, logged as embedded JSON).</summary>
    public bool IsJson => Type is CampaignColumnType.JsonObject or CampaignColumnType.JsonArray or CampaignColumnType.JsonAny;
}

/// <summary>
/// What <see cref="ChangeRecorder"/>, <see cref="UndoEngine"/> and <see cref="ChangeReplay"/> need to know about one
/// loggable table: its columns in table order with their types, its key, and which columns name the entities a
/// change_log row is about.
/// </summary>
public sealed class CampaignTable
{
    private readonly Dictionary<string, CampaignColumn> _byName;

    internal CampaignTable(
        string name,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<CampaignColumn> columns,
        string? entityColumn,
        string? otherEntityColumn)
    {
        Name = name;
        KeyColumns = keyColumns;
        Columns = columns;
        EntityColumn = entityColumn;
        OtherEntityColumn = otherEntityColumn;
        _byName = columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
        ColumnList = string.Join(", ", columns.Select(c => c.Name));
        KeyPredicate = string.Join(" AND ", keyColumns.Select((c, i) => $"{c} = $k{i}"));
    }

    /// <summary>The SQL table name, as change_log.target_table stores it.</summary>
    public string Name { get; }

    /// <summary>
    /// The columns that identify a row, in primary-key order. For entity and fact this is <c>id</c> (the UNIQUE UUID
    /// every foreign key references), not the <c>seq</c> INTEGER PRIMARY KEY: <c>seq</c> is the FTS rowid and the
    /// <c>e:</c>/<c>f:</c> handle, and ids are what other rows and change_log point at.
    /// </summary>
    public IReadOnlyList<string> KeyColumns { get; }

    /// <summary>Every column, in the migration's table order.</summary>
    public IReadOnlyList<CampaignColumn> Columns { get; }

    /// <summary>The column copied into change_log.entity_id (null: none). See §3.5 of the Phase 6 contract.</summary>
    public string? EntityColumn { get; }

    /// <summary>The column copied into change_log.other_entity_id (null: none).</summary>
    public string? OtherEntityColumn { get; }

    /// <summary>"seq, id, campaign_id, …" for SELECTs that read a whole row generically.</summary>
    public string ColumnList { get; }

    /// <summary>"id = $k0" or "fact_id = $k0 AND entity_id = $k1 AND role = $k2", bound by <see cref="KeyColumns"/> order.</summary>
    internal string KeyPredicate { get; }

    /// <summary>True when the key is a single generated <c>id</c> column (a UUIDv7 other rows can reference).</summary>
    public bool HasGeneratedId => KeyColumns is ["id"];

    /// <summary>True when the table has an <c>updated_at</c> column, which every changing write stamps (unlogged).</summary>
    public bool HasUpdatedAt => _byName.ContainsKey("updated_at");

    /// <summary>True when the table has a <c>created_at</c> column, which an insert stamps when the row omits it.</summary>
    public bool HasCreatedAt => _byName.ContainsKey("created_at");

    public bool TryColumn(string name, out CampaignColumn column) => _byName.TryGetValue(name, out column!);

    /// <exception cref="ArgumentException">The table has no such column (a caller bug: column names never come from input).</exception>
    public CampaignColumn Column(string name) =>
        _byName.TryGetValue(name, out var column)
            ? column
            : throw new ArgumentException($"{Name} has no column \"{name}\".", nameof(name));

    /// <summary>
    /// change_log.target_id for a key: the value itself for a one-column key, else a JSON array in key-column order
    /// (<c>["&lt;fact id&gt;","&lt;entity id&gt;","about"]</c>). Always built from the stored values, never from what a
    /// caller typed, because <c>entity_alias.alias</c> is COLLATE NOCASE: "Old King" and "old king" address the same row
    /// and must give the same target id, or undo's conflict check and point-in-time replay would miss changes.
    /// </summary>
    public string TargetId(IReadOnlyList<string> key)
    {
        if (key.Count != KeyColumns.Count)
        {
            throw new ArgumentException($"{Name} has a {KeyColumns.Count}-column key; got {key.Count} values.", nameof(key));
        }

        if (key.Count == 1)
        {
            return key[0];
        }

        var array = new JsonArray();
        foreach (var part in key)
        {
            array.Add(JsonValue.Create(part));
        }

        return array.ToJsonString(CampaignLogJson.Options);
    }

    /// <summary>The key values of a whole row, in key-column order.</summary>
    public IReadOnlyList<string> KeyOf(IReadOnlyDictionary<string, object?> row) =>
        KeyColumns.Select(c => row.TryGetValue(c, out var v) && v is string s
            ? s
            : throw new ArgumentException($"{Name} row has no {c}.", nameof(row))).ToList();

    /// <summary>The inverse of <see cref="TargetId"/>.</summary>
    public IReadOnlyList<string> ParseTargetId(string targetId)
    {
        if (KeyColumns.Count == 1)
        {
            return [targetId];
        }

        var parts = JsonSerializer.Deserialize<string[]>(targetId, CampaignLogJson.Options) ?? [];
        if (parts.Length != KeyColumns.Count)
        {
            throw new ArgumentException($"\"{targetId}\" is not a {Name} key.", nameof(targetId));
        }

        return parts;
    }
}

/// <summary>
/// The catalogue of every table change_log records: the metadata the generic recorder, undo and replay run on, so no
/// table needs its own logging code (a hand-written logger per table is where a forgotten column hides).
///
/// <para>
/// <b>What breaks if it drifts:</b> a column missing here is never logged, never undone and never replayed, silently.
/// <c>CampaignTablesTests</c> therefore pins this catalogue against the migrated schema (every column of every table,
/// in order, with its type and nullability; every table is either here or in <see cref="NotLogged"/>), so a migration
/// that adds a column or a table fails a test until the catalogue knows it.
/// </para>
/// </summary>
public static class CampaignTables
{
    private const CampaignColumnType T = CampaignColumnType.Text;
    private const CampaignColumnType I = CampaignColumnType.Integer;
    private const CampaignColumnType R = CampaignColumnType.Real;
    private const CampaignColumnType O = CampaignColumnType.JsonObject;
    private const CampaignColumnType A = CampaignColumnType.JsonArray;

    public static readonly CampaignTable Campaign = new("campaign", ["id"],
    [
        C("id", T), C("slug", T), C("name", T), C("role", T), C("ruleset", T), C("status", T), N("dm_name", T),
        N("my_character_id", T), N("party_id", T), N("current_location_id", T), N("current_ingame", T),
        new("settings", O, Nullable: false, LoggedPerKey: true), C("summary_md", T), C("created_at", T), Stamp(),
    ], entityColumn: null, otherEntityColumn: null);

    public static readonly CampaignTable Entity = new("entity", ["id"],
    [
        C("seq", I), C("id", T), C("campaign_id", T), C("kind", T), N("subtype", T), C("slug", T), N("code", T),
        C("name", T), C("summary", T), C("body_md", T), C("secret_md", T), N("status", T), C("visibility", T),
        C("canon_status", T), C("confidence", T), N("parent_id", T), N("sort_key", R),
        new("data", O, Nullable: false, LoggedPerKey: true), N("introduced_session_id", T), N("source", T),
        C("created_at", T), Stamp(), N("deleted_at", T),
    ], entityColumn: "id", otherEntityColumn: "parent_id");

    public static readonly CampaignTable EntityAlias = new("entity_alias", ["entity_id", "alias"],
        [C("entity_id", T), C("alias", T), C("visibility", T)], entityColumn: "entity_id", otherEntityColumn: null);

    public static readonly CampaignTable Tag = new("tag", ["id"],
        [C("id", T), C("campaign_id", T), C("name", T)], entityColumn: null, otherEntityColumn: null);

    public static readonly CampaignTable EntityTag = new("entity_tag", ["entity_id", "tag_id"],
        [C("entity_id", T), C("tag_id", T)], entityColumn: "entity_id", otherEntityColumn: null);

    public static readonly CampaignTable Relation = new("relation", ["id"],
    [
        C("id", T), C("campaign_id", T), C("from_id", T), C("rel", T), C("to_id", T), N("label", T), N("attitude", I),
        C("symmetric", I), C("visibility", T), C("status", T), N("since_session_id", T), N("until_session_id", T),
        new("data", O, Nullable: false, LoggedPerKey: true), C("created_at", T), Stamp(),
    ], entityColumn: "from_id", otherEntityColumn: "to_id");

    public static readonly CampaignTable CrossLink = new("cross_link", ["a_id", "b_id"],
        [C("a_id", T), C("b_id", T), N("note", T), C("created_at", T)], entityColumn: "a_id", otherEntityColumn: "b_id");

    public static readonly CampaignTable Fact = new("fact", ["id"],
    [
        C("seq", I), C("id", T), C("campaign_id", T), N("code", T), C("statement", T), C("fact_type", T), C("truth", T),
        C("canon_status", T), C("confidence", T), C("visibility", T), N("gate", O), N("established_session_id", T),
        N("source", T), N("superseded_by", T), C("created_at", T), Stamp(), N("deleted_at", T),
    ], entityColumn: null, otherEntityColumn: null);

    public static readonly CampaignTable FactLink = new("fact_link", ["fact_id", "entity_id", "role"],
        [C("fact_id", T), C("entity_id", T), C("role", T)], entityColumn: "entity_id", otherEntityColumn: null);

    public static readonly CampaignTable FactDependency = new("fact_dependency", ["fact_id", "depends_on"],
        [C("fact_id", T), C("depends_on", T)], entityColumn: null, otherEntityColumn: null);

    // knower_id is non-null exactly for character knowers (a schema CHECK), so it is "the character knower, if any".
    public static readonly CampaignTable Knowledge = new("knowledge", ["id"],
    [
        C("id", T), C("campaign_id", T), N("fact_id", T), N("entity_id", T), C("knower_kind", T), N("knower_id", T),
        C("state", T), N("known_as", T), N("learned_session_id", T), N("learned_ingame", T), N("via_entity_id", T),
        N("how", T), N("note", T), N("valid_until_session_id", T), C("created_at", T), Stamp(),
    ], entityColumn: "knower_id", otherEntityColumn: "entity_id");

    public static readonly CampaignTable Session = new("session", ["entity_id"],
    [
        C("entity_id", T), C("campaign_id", T), C("number", I), C("status", T), N("played_on", T),
        C("played_on_precision", T), N("ingame_start", T), N("ingame_end", T), N("arc_id", T), N("level_at_start", I),
        C("prep_md", T), new("live_log", A, Nullable: false, Logged: false),
        new("data", O, Nullable: false, LoggedPerKey: true), N("started_at", T), N("ended_at", T),
    ], entityColumn: "entity_id", otherEntityColumn: null);

    public static readonly CampaignTable SessionAttendance = new("session_attendance", ["session_id", "character_id"],
        [C("session_id", T), C("character_id", T), C("present", I), N("note", T)],
        entityColumn: "session_id", otherEntityColumn: "character_id");

    public static readonly CampaignTable Objective = new("objective", ["id"],
    [
        C("id", T), C("quest_id", T), C("ordinal", R), C("text", T), C("status", T), N("progress", I),
        N("progress_max", I), C("visibility", T), N("resolved_session_id", T), Stamp(),
    ], entityColumn: "quest_id", otherEntityColumn: null);

    public static readonly CampaignTable Clock = new("clock", ["entity_id"],
    [
        C("entity_id", T), C("segments", I), C("filled", I), C("unit", T), N("front_id", T), C("shown_to_players", I),
        C("on_fill_md", T),
    ], entityColumn: "entity_id", otherEntityColumn: null);

    public static readonly CampaignTable BeatEdge = new("beat_edge", ["id"],
        [C("id", T), C("campaign_id", T), C("from_beat_id", T), C("to_beat_id", T), C("mode", T)],
        entityColumn: "from_beat_id", otherEntityColumn: "to_beat_id");

    // 0002 (Phase 7). A character's sheet is keyed by its character entity's id (a session row's pattern), so its history
    // is the character's history. The keyed trackers log per key: a combat write-back spending a 3rd-level slot and a later
    // rest restoring 1st-level slots touch different keys, so neither blocks undoing the other; abilities likewise per
    // score. The JSON arrays (classes, conditions, feats, …) and the other objects are logged whole.
    public static readonly CampaignTable CharacterSheet = new("character_sheet", ["entity_id"],
    [
        C("entity_id", T), N("player", T), N("ruleset", T), N("species", T), N("lineage", T), N("background", T), N("size", T),
        C("classes", A), N("level", I), N("xp", I), new("abilities", O, Nullable: false, LoggedPerKey: true), C("saves", O),
        C("skills", O), N("ac", I), N("max_hp", I), C("max_hp_reduction", I), N("hp", I), C("temp_hp", I), N("speed", I),
        C("movement", O), C("senses", O), N("initiative_bonus", I), N("passive_perception", I), N("spell_save_dc", I),
        N("spell_attack", I), C("defenses", O), new("hit_dice", O, Nullable: false, LoggedPerKey: true),
        new("spell_slots", O, Nullable: false, LoggedPerKey: true), new("resources", O, Nullable: false, LoggedPerKey: true),
        C("conditions", A), N("concentration", O), C("death_saves", O), C("exhaustion", I), C("inspiration", I),
        C("feats", A), C("features", A), C("spells", A), C("languages", A), N("sim_profile", O), C("notes_md", T),
        N("sheet_source", T), C("created_at", T), Stamp(),
    ], entityColumn: "entity_id", otherEntityColumn: null);

    // A thing someone carries. item_id links an item entity when there is one (the other entity of its history rows).
    public static readonly CampaignTable Holding = new("holding", ["id"],
    [
        C("id", T), C("campaign_id", T), C("holder_id", T), N("item_id", T), C("name", T), N("srd_ref", T), C("quantity", R),
        C("equipped", I), C("attuned", I), N("charges", O), N("acquired_session_id", T), N("notes", T), C("created_at", T),
        Stamp(),
    ], entityColumn: "holder_id", otherEntityColumn: "item_id");

    // Coins in or out (a ledger: a balance is the sum). Created and, by undo, deleted; never updated.
    public static readonly CampaignTable CurrencyTxn = new("currency_txn", ["id"],
    [
        C("id", T), C("campaign_id", T), C("holder_id", T), N("session_id", T), C("cp", I), C("sp", I), C("ep", I), C("gp", I),
        C("pp", I), C("note", T), C("created_at", T),
    ], entityColumn: "holder_id", otherEntityColumn: "session_id");

    public static readonly CampaignTable Award = new("award", ["id"],
    [
        C("id", T), C("campaign_id", T), C("recipient_id", T), N("session_id", T), C("kind", T), N("amount", I), N("note", T),
        N("source", T), C("created_at", T),
    ], entityColumn: "recipient_id", otherEntityColumn: "session_id");

    /// <summary>Every loggable table, in the migrations' order.</summary>
    public static readonly IReadOnlyList<CampaignTable> All =
    [
        Campaign, Entity, EntityAlias, Tag, EntityTag, Relation, CrossLink, Fact, FactLink, FactDependency, Knowledge,
        Session, SessionAttendance, Objective, Clock, BeatEdge, CharacterSheet, Holding, CurrencyTxn, Award,
    ];

    /// <summary>
    /// Tables deliberately outside change_log: bookkeeping (<c>schema_migrations</c>, <c>app_state</c>: which campaign is
    /// active is not history), <c>dice_roll</c> (a roll is its own record, and undo must never un-roll dice), change_log
    /// itself, the FTS tables (derived by triggers), and the live combat tracker (<c>encounter</c>, <c>combatant</c>,
    /// <c>combat_log</c>: HP ticks stay out of history, combat_log is the fight's own audit trail, and what a fight
    /// changes on the sheets reaches change_log as one end-of-combat batch). No logged table has a foreign key into the
    /// tracker's tables, so nothing undo deletes there changes a logged row; the reverse case (undo deleting an entity or
    /// session a tracker row points at) is refused by <see cref="UndoEngine"/>. FTS shadow tables (<c>entity_fts_data</c>,
    /// …) and <c>sqlite_sequence</c> are recognised by <see cref="IsInternal"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> NotLogged =
    [
        "schema_migrations", "app_state", "dice_roll", "change_log", "entity_fts", "fact_fts", "encounter", "combatant",
        "combat_log",
    ];

    private static readonly Dictionary<string, CampaignTable> ByName = All.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <exception cref="ArgumentException">Not a loggable table (a caller bug: table names never come from input).</exception>
    public static CampaignTable Get(string name) =>
        ByName.TryGetValue(name, out var table)
            ? table
            : throw new ArgumentException(
                $"\"{name}\" is not a table change_log records. Tables: {string.Join(", ", ByName.Keys)}.", nameof(name));

    public static bool TryGet(string name, out CampaignTable table) => ByName.TryGetValue(name, out table!);

    /// <summary>SQLite's own tables and the FTS5 shadow tables, which no code writes directly.</summary>
    public static bool IsInternal(string tableName) =>
        tableName.StartsWith("sqlite_", StringComparison.Ordinal) ||
        (tableName.StartsWith("entity_fts_", StringComparison.Ordinal) || tableName.StartsWith("fact_fts_", StringComparison.Ordinal));

    private static CampaignColumn C(string name, CampaignColumnType type) => new(name, type, Nullable: false);

    private static CampaignColumn N(string name, CampaignColumnType type) => new(name, type, Nullable: true);

    private static CampaignColumn Stamp() => new("updated_at", CampaignColumnType.Text, Nullable: false, Logged: false);
}

/// <summary>
/// The one JSON form change_log values use: compact, with non-ASCII kept readable ("Björn", not "Björn"), so a
/// logged row reads like the data and the same value always serializes to the same text (target ids and the undo
/// conflict test compare text).
/// </summary>
public static class CampaignLogJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Compact text of a JSON node (<c>null</c> gives the JSON literal null).</summary>
    public static string Serialize(JsonNode? node) => node is null ? "null" : node.ToJsonString(Options);
}
