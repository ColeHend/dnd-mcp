using System.Globalization;
using System.Reflection;
using System.Text;

namespace DndMcp.Repository.Campaign;

// Positional records for campaigns.db rows, one per table, every column in table order.
//
// Why they look like this (DapperMappingTests pins each rule): Dapper binds a positional record through its constructor,
// POSITIONALLY and WITHOUT type conversion. So every record lists every column in table order, INTEGER columns are
// `long` (never int or bool), REAL columns `double`, TEXT and JSON columns `string` (JSON stays text; parse it where it is
// used), and every query selects the record's `Columns` constant, never `SELECT *` (a migration that adds a column would
// otherwise break every read of that table). Snake_case columns reach PascalCase parameters only because CampaignDatabase's
// static constructor sets DefaultTypeMap.MatchNamesWithUnderscores before any campaign query runs.
//
// CampaignRowsTests reads every record from a migrated database and compares each Columns constant with the table's own
// column list, so a migration that adds a column fails a test until the record (and CampaignTables) know it.

/// <summary>A campaign. <c>Settings</c> is a JSON object (text).</summary>
public sealed record CampaignRow(
    string Id,
    string Slug,
    string Name,
    string Role,
    string Ruleset,
    string Status,
    string? DmName,
    string? MyCharacterId,
    string? PartyId,
    string? CurrentLocationId,
    string? CurrentIngame,
    string Settings,
    string SummaryMd,
    string CreatedAt,
    string UpdatedAt)
{
    public const string Columns =
        "id, slug, name, role, ruleset, status, dm_name, my_character_id, party_id, current_location_id, current_ingame, " +
        "settings, summary_md, created_at, updated_at";
}

/// <summary>
/// An entity (character, location, secret, session, …). <c>Seq</c> is the <c>e:&lt;n&gt;</c> handle and the FTS rowid;
/// <c>Id</c> is what every other row references. <c>SecretMd</c> is author-only text: never render it for another view.
/// </summary>
public sealed record EntityRow(
    long Seq,
    string Id,
    string CampaignId,
    string Kind,
    string? Subtype,
    string Slug,
    string? Code,
    string Name,
    string Summary,
    string BodyMd,
    string SecretMd,
    string? Status,
    string Visibility,
    string CanonStatus,
    string Confidence,
    string? ParentId,
    double? SortKey,
    string Data,
    string? IntroducedSessionId,
    string? Source,
    string CreatedAt,
    string UpdatedAt,
    string? DeletedAt)
{
    public const string Columns =
        "seq, id, campaign_id, kind, subtype, slug, code, name, summary, body_md, secret_md, status, visibility, " +
        "canon_status, confidence, parent_id, sort_key, data, introduced_session_id, source, created_at, updated_at, deleted_at";

    /// <summary>The <c>e:&lt;n&gt;</c> handle.</summary>
    public string SeqHandle => "e:" + Seq.ToString(CultureInfo.InvariantCulture);

    /// <summary>The <c>kind:slug</c> handle (author view; other views go through the perspective rules).</summary>
    public string Handle => Kind + ":" + Slug;

    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>An entity alias. Only public/party aliases may be shown to non-author views.</summary>
public sealed record AliasRow(string EntityId, string Alias, string Visibility)
{
    public const string Columns = "entity_id, alias, visibility";
}

/// <summary>A tag (per campaign; names compare case-insensitively).</summary>
public sealed record TagRow(string Id, string CampaignId, string Name)
{
    public const string Columns = "id, campaign_id, name";
}

/// <summary>An entity's tag.</summary>
public sealed record EntityTagRow(string EntityId, string TagId)
{
    public const string Columns = "entity_id, tag_id";
}

/// <summary>A relation between two entities of one campaign. <c>Symmetric</c> is 0/1.</summary>
public sealed record RelationRow(
    string Id,
    string CampaignId,
    string FromId,
    string Rel,
    string ToId,
    string? Label,
    long? Attitude,
    long Symmetric,
    string Visibility,
    string Status,
    string? SinceSessionId,
    string? UntilSessionId,
    string Data,
    string CreatedAt,
    string UpdatedAt)
{
    public const string Columns =
        "id, campaign_id, from_id, rel, to_id, label, attitude, symmetric, visibility, status, since_session_id, " +
        "until_session_id, data, created_at, updated_at";
}

/// <summary>The same being in two campaigns. Author view only, always.</summary>
public sealed record CrossLinkRow(string AId, string BId, string? Note, string CreatedAt)
{
    public const string Columns = "a_id, b_id, note, created_at";
}

/// <summary>
/// A fact. <c>Seq</c> is the <c>f:&lt;n&gt;</c> handle. <c>Gate</c> is a JSON object (text) storing fact ids, or null.
/// </summary>
public sealed record FactRow(
    long Seq,
    string Id,
    string CampaignId,
    string? Code,
    string Statement,
    string FactType,
    string Truth,
    string CanonStatus,
    string Confidence,
    string Visibility,
    string? Gate,
    string? EstablishedSessionId,
    string? Source,
    string? SupersededBy,
    string CreatedAt,
    string UpdatedAt,
    string? DeletedAt)
{
    public const string Columns =
        "seq, id, campaign_id, code, statement, fact_type, truth, canon_status, confidence, visibility, gate, " +
        "established_session_id, source, superseded_by, created_at, updated_at, deleted_at";

    /// <summary>The <c>f:&lt;n&gt;</c> handle.</summary>
    public string SeqHandle => "f:" + Seq.ToString(CultureInfo.InvariantCulture);

    public bool IsDeleted => DeletedAt is not null;
}

/// <summary>How a fact is tied to an entity (about, clue_for, …).</summary>
public sealed record FactLinkRow(string FactId, string EntityId, string Role)
{
    public const string Columns = "fact_id, entity_id, role";
}

/// <summary>A fact that depends on another (supersession reports the dependents).</summary>
public sealed record FactDependencyRow(string FactId, string DependsOn)
{
    public const string Columns = "fact_id, depends_on";
}

/// <summary>
/// Who knows a fact (or an entity): exactly one of <c>FactId</c>/<c>EntityId</c>; <c>KnowerId</c> set iff the knower is a
/// character. Session references are session ENTITY ids; <see cref="KnowledgeLoader"/> turns them into numbers.
/// </summary>
public sealed record KnowledgeRow(
    string Id,
    string CampaignId,
    string? FactId,
    string? EntityId,
    string KnowerKind,
    string? KnowerId,
    string State,
    string? KnownAs,
    string? LearnedSessionId,
    string? LearnedIngame,
    string? ViaEntityId,
    string? How,
    string? Note,
    string? ValidUntilSessionId,
    string CreatedAt,
    string UpdatedAt)
{
    public const string Columns =
        "id, campaign_id, fact_id, entity_id, knower_kind, knower_id, state, known_as, learned_session_id, learned_ingame, " +
        "via_entity_id, how, note, valid_until_session_id, created_at, updated_at";
}

/// <summary>
/// A session's mechanics; its name, recap (<c>body_md</c>) and handle live on the session entity (<c>EntityId</c>).
/// <c>LiveLog</c> is a JSON array (text), <c>Data</c> a JSON object (text).
/// </summary>
public sealed record SessionRow(
    string EntityId,
    string CampaignId,
    long Number,
    string Status,
    string? PlayedOn,
    string PlayedOnPrecision,
    string? IngameStart,
    string? IngameEnd,
    string? ArcId,
    long? LevelAtStart,
    string PrepMd,
    string LiveLog,
    string Data,
    string? StartedAt,
    string? EndedAt)
{
    public const string Columns =
        "entity_id, campaign_id, number, status, played_on, played_on_precision, ingame_start, ingame_end, arc_id, " +
        "level_at_start, prep_md, live_log, data, started_at, ended_at";
}

/// <summary>Whether a character was at a session. <c>Present</c> is 0/1.</summary>
public sealed record AttendanceRow(string SessionId, string CharacterId, long Present, string? Note)
{
    public const string Columns = "session_id, character_id, present, note";
}

/// <summary>An objective of a quest or thread, ordered by <c>Ordinal</c>.</summary>
public sealed record ObjectiveRow(
    string Id,
    string QuestId,
    double Ordinal,
    string Text,
    string Status,
    long? Progress,
    long? ProgressMax,
    string Visibility,
    string? ResolvedSessionId,
    string UpdatedAt)
{
    public const string Columns =
        "id, quest_id, ordinal, text, status, progress, progress_max, visibility, resolved_session_id, updated_at";
}

/// <summary>A clock's segments (the clock itself is an entity of kind clock). <c>ShownToPlayers</c> is 0/1.</summary>
public sealed record ClockRow(
    string EntityId,
    long Segments,
    long Filled,
    string Unit,
    string? FrontId,
    long ShownToPlayers,
    string OnFillMd)
{
    public const string Columns = "entity_id, segments, filled, unit, front_id, shown_to_players, on_fill_md";
}

/// <summary>A story-web edge between two beats (all_of / any_of).</summary>
public sealed record BeatEdgeRow(string Id, string CampaignId, string FromBeatId, string ToBeatId, string Mode)
{
    public const string Columns = "id, campaign_id, from_beat_id, to_beat_id, mode";
}

/// <summary>
/// A logged roll: one the dice_roll tool made while a session was live, or one the combat tracker made (<c>EncounterId</c>
/// set; <c>SessionId</c> the live session, else the encounter's, else null). <c>Outcome</c> is 1/0 for a trailing
/// comparison, else null; <c>Secret</c> 0/1; <c>Detail</c> a JSON object (text, <c>DiceLogDetail.Json</c>'s shape). Not in
/// change_log: undo never un-rolls dice. <c>EncounterId</c> is last because 0002 added it with ALTER TABLE, which appends.
/// </summary>
public sealed record DiceRollRow(
    long Seq,
    string Id,
    string CampaignId,
    string? SessionId,
    string Expression,
    string? Label,
    long Total,
    long? Outcome,
    string Detail,
    long Secret,
    string At,
    string? EncounterId)
{
    public const string Columns =
        "seq, id, campaign_id, session_id, expression, label, total, outcome, detail, secret, at, encounter_id";
}

/// <summary>
/// A character's sheet (0002), keyed by its character entity's id: any character entity may have one (PCs and NPCs
/// alike). Logged like every 0001 table (undo, history, as_of replay). The JSON columns are text in the shapes of
/// contract §4 (owned by the sheet model): <c>Classes</c>, <c>Conditions</c>, <c>Feats</c>, <c>Features</c>,
/// <c>Spells</c>, <c>Languages</c> are arrays; <c>Abilities</c>, <c>Saves</c>, <c>Skills</c>, <c>Movement</c>,
/// <c>Senses</c>, <c>Defenses</c>, <c>HitDice</c>, <c>SpellSlots</c>, <c>Resources</c>, <c>DeathSaves</c> objects;
/// <c>Concentration</c> and <c>SimProfile</c> an object or null. <c>Inspiration</c> is 0/1. <c>Ac</c> is the BASE armour
/// class (no Shield, no Bladesong). The effective maximum HP is <c>HitPointMath.EffectiveMaxHp(MaxHp,
/// MaxHpReduction, Exhaustion, edition)</c>, never <c>MaxHp</c> alone. <c>SimProfile</c>, <c>SheetSource</c> and
/// <c>NotesMd</c> are author-only.
/// </summary>
public sealed record CharacterSheetRow(
    string EntityId,
    string? Player,
    string? Ruleset,
    string? Species,
    string? Lineage,
    string? Background,
    string? Size,
    string Classes,
    long? Level,
    long? Xp,
    string Abilities,
    string Saves,
    string Skills,
    long? Ac,
    long? MaxHp,
    long MaxHpReduction,
    long? Hp,
    long TempHp,
    long? Speed,
    string Movement,
    string Senses,
    long? InitiativeBonus,
    long? PassivePerception,
    long? SpellSaveDc,
    long? SpellAttack,
    string Defenses,
    string HitDice,
    string SpellSlots,
    string Resources,
    string Conditions,
    string? Concentration,
    string DeathSaves,
    long Exhaustion,
    long Inspiration,
    string Feats,
    string Features,
    string Spells,
    string Languages,
    string? SimProfile,
    string NotesMd,
    string? SheetSource,
    string CreatedAt,
    string UpdatedAt)
{
    public const string Columns =
        "entity_id, player, ruleset, species, lineage, background, size, classes, level, xp, abilities, saves, skills, ac, " +
        "max_hp, max_hp_reduction, hp, temp_hp, speed, movement, senses, initiative_bonus, passive_perception, spell_save_dc, " +
        "spell_attack, defenses, hit_dice, spell_slots, resources, conditions, concentration, death_saves, exhaustion, " +
        "inspiration, feats, features, spells, languages, sim_profile, notes_md, sheet_source, created_at, updated_at";
}

/// <summary>
/// Something a character (or the party faction) carries (0002, logged). <c>ItemId</c> links an item entity when there is
/// one: its name reaches a non-author view only through the perspective rules, never as <c>Name</c>. <c>Quantity</c> is
/// REAL (arrows, rations, 0.5 lb of salt); 0 is a used-up stack, kept. <c>Equipped</c>/<c>Attuned</c> are 0/1;
/// <c>Charges</c> a JSON object or null.
/// </summary>
public sealed record HoldingRow(
    string Id,
    string CampaignId,
    string HolderId,
    string? ItemId,
    string Name,
    string? SrdRef,
    double Quantity,
    long Equipped,
    long Attuned,
    string? Charges,
    string? AcquiredSessionId,
    string? Notes,
    string CreatedAt,
    string UpdatedAt)
{
    public const string Columns =
        "id, campaign_id, holder_id, item_id, name, srd_ref, quantity, equipped, attuned, charges, acquired_session_id, notes, " +
        "created_at, updated_at";
}

/// <summary>
/// Coins in (positive) or out (negative) for a holder (0002, logged): a ledger, so a balance is the sum of its rows.
/// Never updated in place.
/// </summary>
public sealed record CurrencyTxnRow(
    string Id,
    string CampaignId,
    string HolderId,
    string? SessionId,
    long Cp,
    long Sp,
    long Ep,
    long Gp,
    long Pp,
    string Note,
    string CreatedAt)
{
    public const string Columns = "id, campaign_id, holder_id, session_id, cp, sp, ep, gp, pp, note, created_at";
}

/// <summary>An award to a character or the party (0002, logged): <c>Kind</c> is a <c>CampaignValues.AwardKinds</c> value.</summary>
public sealed record AwardRow(
    string Id,
    string CampaignId,
    string RecipientId,
    string? SessionId,
    string Kind,
    long? Amount,
    string? Note,
    string? Source,
    string CreatedAt)
{
    public const string Columns = "id, campaign_id, recipient_id, session_id, kind, amount, note, source, created_at";
}

/// <summary>
/// A fight (0002, NOT logged: the tracker writes it with plain SQL in its step's transaction). <c>Status</c> is a
/// <c>CampaignValues.EncounterStatuses</c> value (one active per campaign); <c>Ruleset</c> an edition, never mixed.
/// <c>Round</c> 0 = not started. <c>TurnCombatantId</c> points at the turn-holder (no foreign key, deliberately: the
/// combatant may have left). <c>Lair</c> is 0/1; <c>Data</c> a JSON object. <c>WritebackBatchId</c> is the change_log batch
/// of the end-of-combat write-back (null: nothing was written, or not ended).
/// </summary>
public sealed record EncounterRow(
    string Id,
    string CampaignId,
    string? SceneId,
    string? SessionId,
    string Name,
    string Ruleset,
    string Status,
    long Round,
    string? TurnCombatantId,
    long Lair,
    string Data,
    string NotesMd,
    string? OutcomeMd,
    string? WritebackBatchId,
    string? StartedAt,
    string? EndedAt,
    string CreatedAt,
    string UpdatedAt)
{
    public const string Columns =
        "id, campaign_id, scene_id, session_id, name, ruleset, status, round, turn_combatant_id, lair, data, notes_md, " +
        "outcome_md, writeback_batch_id, started_at, ended_at, created_at, updated_at";

    /// <summary>
    /// Why the stored row cannot be run as a fight (an unknown ruleset or status, a negative round, text in a number
    /// column: something other than this server wrote it), naming the column and never its value; null when it can.
    /// Set by <c>CombatStore</c>'s reader, which reads such a row anyway (its number columns as 0) rather than fail: the
    /// fight must still be found, listed and ended with <c>discard</c>, which reads none of it, while every step and view
    /// that would run it refuses with the store's unreadable-row message (<c>CombatStore.Load</c>).
    /// </summary>
    public string? Unreadable { get; init; }
}

/// <summary>
/// One creature in a fight (0002, NOT logged). <c>Name</c> is the AUTHOR-facing tracker name: a non-author view names a
/// linked combatant through the perspective rules, never by this text. <c>Side</c> is a <c>CampaignValues.CombatSides</c>
/// value. <c>Initiative</c> and <c>OrderKey</c> are REAL (totals such as 14.5 reorder ties). <c>Statblock</c>,
/// <c>Concentration</c>, <c>Legendary</c> and <c>SheetSnapshot</c> are JSON objects or null; <c>Conditions</c> a JSON array,
/// <c>DeathSaves</c> and <c>Resources</c> objects (contract §4, owned by the tracker). The 0/1 flags: <c>MakesDeathSaves</c>,
/// <c>ReactionUsed</c>, <c>Surprised</c>, <c>Hidden</c>, <c>Defeated</c>, <c>Dead</c>, <c>Removed</c> (left the fight).
/// A combatant is sheet-seeded iff <c>SheetSnapshot</c> is not null.
/// </summary>
public sealed record CombatantRow(
    string Id,
    string EncounterId,
    string? EntityId,
    string Name,
    string Side,
    string? InitGroup,
    string? SrdRef,
    string? Statblock,
    double? Initiative,
    long InitBonus,
    long? Ac,
    long? MaxHp,
    long MaxHpReduction,
    long? Hp,
    long TempHp,
    long DamageTaken,
    string Conditions,
    string? Concentration,
    string DeathSaves,
    long MakesDeathSaves,
    long Exhaustion,
    string? Legendary,
    string Resources,
    string? SheetSnapshot,
    long ReactionUsed,
    long Surprised,
    long Hidden,
    long Defeated,
    long Dead,
    long Removed,
    double OrderKey,
    string CreatedAt,
    string UpdatedAt)
{
    public const string Columns =
        "id, encounter_id, entity_id, name, side, init_group, srd_ref, statblock, initiative, init_bonus, ac, max_hp, " +
        "max_hp_reduction, hp, temp_hp, damage_taken, conditions, concentration, death_saves, makes_death_saves, exhaustion, " +
        "legendary, resources, sheet_snapshot, reaction_used, surprised, hidden, defeated, dead, removed, order_key, " +
        "created_at, updated_at";
}

/// <summary>
/// One state change of a fight (0002, NOT logged; append-only by convention). <c>Kind</c> is a
/// <c>CampaignValues.CombatLogKinds</c> value; <c>ActorId</c>/<c>TargetId</c> are combatant ids; <c>Detail</c> a JSON
/// object or null; <c>RollId</c> the <c>dice_roll.id</c> of a server roll (null for values the caller gave).
/// </summary>
public sealed record CombatLogRow(
    long Seq,
    string EncounterId,
    long Round,
    string? TurnCombatantId,
    string? ActorId,
    string? TargetId,
    string Kind,
    long? Amount,
    string? Detail,
    string? RollId,
    string At)
{
    public const string Columns = "seq, encounter_id, round, turn_combatant_id, actor_id, target_id, kind, amount, detail, roll_id, at";
}

/// <summary>
/// One change_log row. <c>Op</c> is create/update/delete; <c>Action</c> the semantic label; <c>FieldPath</c> the column
/// (or <c>data.&lt;key&gt;</c>) of an update; <c>OldValue</c>/<c>NewValue</c> per <see cref="ChangeValues"/>.
/// </summary>
public sealed record ChangeRow(
    long Seq,
    string CampaignId,
    string At,
    string? SessionId,
    string Actor,
    string? Tool,
    string BatchId,
    string Action,
    string Op,
    string TargetTable,
    string TargetId,
    string? EntityId,
    string? OtherEntityId,
    string? FieldPath,
    string? OldValue,
    string? NewValue,
    string? Reason,
    string? UndoOf)
{
    public const string Columns =
        "seq, campaign_id, at, session_id, actor, tool, batch_id, action, op, target_table, target_id, entity_id, " +
        "other_entity_id, field_path, old_value, new_value, reason, undo_of";
}

/// <summary>Helpers over the row records' column lists.</summary>
public static class CampaignRows
{
    /// <summary>
    /// "e.seq, e.id, …" for a join: <c>Prefixed(EntityRow.Columns, "e")</c>. The aliases keep the plain column names, so
    /// the record still binds.
    /// </summary>
    public static string Prefixed(string columns, string tableAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableAlias);
        return string.Join(", ", columns.Split(',').Select(c => $"{tableAlias}.{c.Trim()}"));
    }

    /// <summary>
    /// Builds a row record from a generic row (<see cref="ChangeRecorder.Read"/>, <see cref="ChangeReplay.RowAsOf"/>): each
    /// constructor parameter takes the column of the same name in snake_case. Used to turn an as-of-session row back
    /// into the record the readers already render.
    /// </summary>
    /// <exception cref="ArgumentException">A column the record needs is missing, or its value has the wrong type.</exception>
    public static T FromValues<T>(IReadOnlyDictionary<string, object?> values)
        where T : class
    {
        var constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select(p =>
        {
            var column = SnakeCase(p.Name!);
            if (!values.TryGetValue(column, out var value))
            {
                throw new ArgumentException($"The row has no \"{column}\" for {typeof(T).Name}.{p.Name}.", nameof(values));
            }

            if (value is null)
            {
                return null;
            }

            var target = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            if (!target.IsInstanceOfType(value))
            {
                throw new ArgumentException(
                    $"\"{column}\" holds a {value.GetType().Name}; {typeof(T).Name}.{p.Name} is a {target.Name}.", nameof(values));
            }

            return value;
        }).ToArray();

        return (T)constructor.Invoke(arguments);
    }

    /// <summary>"CampaignId" → "campaign_id"; "AId" → "a_id".</summary>
    internal static string SnakeCase(string pascal)
    {
        var builder = new StringBuilder(pascal.Length + 4);
        for (var i = 0; i < pascal.Length; i++)
        {
            var c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
