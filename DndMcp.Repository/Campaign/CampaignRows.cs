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
/// A roll made while a session was live. <c>Outcome</c> is 1/0 for a trailing comparison, else null; <c>Secret</c> 0/1;
/// <c>Detail</c> a JSON object (text). Not in change_log: undo never un-rolls dice.
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
    string At)
{
    public const string Columns = "seq, id, campaign_id, session_id, expression, label, total, outcome, detail, secret, at";
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
