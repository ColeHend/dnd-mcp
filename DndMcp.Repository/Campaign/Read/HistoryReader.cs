using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>One batch (one tool call that wrote), with its changes rendered for reading.</summary>
/// <param name="BatchId">The full batch id (the one thing internal ids are shown for: undo takes it).</param>
/// <param name="At">When (UTC, campaigns.db's timestamp format).</param>
/// <param name="Actor"><c>claude</c> or <c>cli</c>.</param>
/// <param name="Tool">The tool and action (<c>campaign_write/upsert</c>).</param>
/// <param name="Reason">The call's reason, with any gate warnings appended.</param>
/// <param name="Session">The session number the batch was written in, if any.</param>
/// <param name="UndoOf">The batch this one reverses, if it is an undo.</param>
/// <param name="Changes">Its changes, in the order they were made (only those about the requested targets, when targets were given).</param>
public sealed record HistoryBatch(
    string BatchId,
    string At,
    string Actor,
    string? Tool,
    string? Reason,
    int? Session,
    string? UndoOf,
    IReadOnlyList<HistoryChange> Changes);

/// <summary>One change_log row, rendered: handles instead of ids, long text cut, gates with fact handles.</summary>
/// <param name="Seq">The change_log sequence number.</param>
/// <param name="Action">The semantic label (upsert, reveal, tick, undo, …).</param>
/// <param name="Op">create, update or delete.</param>
/// <param name="Table">The table changed.</param>
/// <param name="Target">What changed: <c>character:iron-guts</c>, <c>f:12 (F36) "…"</c>, <c>character:a member_of faction:b</c>.</param>
/// <param name="Field">The column (or <c>data.&lt;key&gt;</c>) of an update.</param>
/// <param name="Old">The value before, rendered.</param>
/// <param name="New">The value after, rendered.</param>
/// <param name="Text">The whole line: <c>character:iron-guts status: alive → dead</c>.</param>
public sealed record HistoryChange(long Seq, string Action, string Op, string Table, string Target, string? Field, string? Old, string? New, string Text);

/// <summary>A page of batches.</summary>
public sealed record HistoryPage(IReadOnlyList<HistoryBatch> Batches, string? NextCursor, int Total);

/// <summary>
/// campaign_history's reads (contract §7), for the author only: change_log is the record of every edit, including the
/// ones to secrets, so no other perspective ever gets it (the leak rule's "no history"); campaign_get includes history for
/// the author view alone.
///
/// <para>
/// <b>Rendering.</b> change_log stores ids; the model never sees internal ids except batch ids (contract §0), so every id
/// column is rendered as a handle (<c>character:iron-guts</c>, <c>f:12</c>, <c>session:3</c>) and a gate's fact ids as fact
/// handles. A fact is shown with its code and statement, because the "canon changes since" feed exists so a change can be
/// classified (confirms / contradicts / extends / ruling) without a second lookup. Long text is cut to a line.
/// </para>
/// </summary>
public sealed class HistoryReader
{
    private readonly CampaignDatabase _database;

    public HistoryReader(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Batches oldest first, optionally since a time or a session and about some targets. <paramref name="since"/> is a
    /// UTC date or timestamp ("2026-09-19", "2026-09-19T20:00:00Z"); <paramref name="session"/> starts at the first change
    /// written in that session (or any later one), else at the session's start time; give one or neither.
    /// </summary>
    /// <exception cref="DndInputException">Both given, an unreadable date, an unknown target, a bad limit or cursor.</exception>
    public HistoryPage Since(
        CampaignRow campaign,
        string? since = null,
        int? session = null,
        IReadOnlyList<string>? targets = null,
        int? limit = null,
        string? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        var take = ReadCursor.Limit(limit);
        var filter = new Filter();
        if (!string.IsNullOrWhiteSpace(since) && session is not null)
        {
            throw new DndInputException("Give since (a date) or session (a number), not both.");
        }

        ReadScope.CheckSession(session, "session");
        if (!string.IsNullOrWhiteSpace(since))
        {
            filter.Add("cl.at >= @since", "since", SinceTimestamp(since));
        }
        else if (session is { } n)
        {
            filter.Add("cl.seq >= @fromSeq", "fromSeq", SessionStart(connection, campaign, n));
        }

        if (targets is { Count: > 0 })
        {
            TargetFilter(connection, campaign, targets, filter);
        }

        var fingerprint = ReadCursor.Fingerprint("history-since", campaign.Id, since, session, targets?.ToList());
        return Page(connection, campaign, filter, newestFirst: false, take, cursor, fingerprint);
    }

    /// <summary>The batches that changed one entity or fact (rows where it is the entity or the other entity), newest first.</summary>
    /// <exception cref="DndInputException">An unknown handle, a bad limit or cursor.</exception>
    public HistoryPage Entity(CampaignRow campaign, string handle, int? limit = null, string? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        var take = ReadCursor.Limit(limit);
        var filter = new Filter();
        TargetFilter(connection, campaign, [handle], filter);
        return Page(connection, campaign, filter, newestFirst: true, take, cursor, ReadCursor.Fingerprint("history-entity", campaign.Id, handle));
    }

    /// <summary>One batch, every row (a unique prefix of at least 8 characters of its id is enough).</summary>
    /// <exception cref="DndInputException">No such batch, or the prefix is ambiguous or too short.</exception>
    public HistoryBatch Batch(CampaignRow campaign, string batchId)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        var id = UndoEngine.ResolveBatch(connection, null, campaign.Id, batchId);
        var rows = connection.Query<ChangeRow>(
            $"SELECT {ChangeRow.Columns} FROM change_log WHERE campaign_id = @campaignId AND batch_id = @id ORDER BY seq",
            new { campaignId = campaign.Id, id }).ToList();
        return new HistoryRenderer(connection, campaign).Batches(rows).Single();
    }

    /// <summary>
    /// Entity (or fact) details as they stood at the end of a session: <see cref="EntityReader"/> with as_of, author view.
    /// Its refusals name campaign_history's own <c>session</c> and send today's read to campaign_get (review CR01).
    /// </summary>
    public GetResult AsOf(CampaignRow campaign, int session, IReadOnlyList<string> handles, EntityIncludes? includes = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        return EntityReader.Get(connection, campaign, handles, includes ?? EntityIncludes.Default, Perspective.Author, session, AsOfWording.History);
    }

    /// <summary>
    /// An entity's (or fact's) history over an open connection, newest batch first, at most <paramref name="max"/> batches
    /// with the total: the author view of campaign_get (its cursor is not for paging; campaign_history pages).
    /// </summary>
    internal static HistoryPage ForTarget(SqliteConnection connection, CampaignRow campaign, string? entityId, string? factId, int max)
    {
        var filter = new Filter();
        if (entityId is not null)
        {
            filter.Add("(cl.entity_id = @target OR cl.other_entity_id = @target)", "target", entityId);
        }
        else
        {
            FactTargetFilter(connection, [factId!], filter);
        }

        var page = Page(connection, campaign, filter, newestFirst: true, max, null, "internal");
        return page with { NextCursor = null };
    }

    private static HistoryPage Page(
        SqliteConnection connection,
        CampaignRow campaign,
        Filter filter,
        bool newestFirst,
        int limit,
        string? cursor,
        string fingerprint)
    {
        var offset = ReadCursor.Decode(cursor, fingerprint);
        var where = "cl.campaign_id = @campaignId" + filter.Where;
        var parameters = filter.Parameters(campaign.Id);
        var batches = connection.Query<(string BatchId, long First)>(
            $"SELECT cl.batch_id, min(cl.seq) AS first FROM change_log cl WHERE {where} GROUP BY cl.batch_id " +
            $"ORDER BY first {(newestFirst ? "DESC" : "ASC")}", parameters).ToList();
        var page = batches.Skip(offset).Take(limit).Select(b => b.BatchId).ToList();
        if (page.Count == 0)
        {
            return new HistoryPage([], null, batches.Count);
        }

        parameters.Add("page", page);
        var rows = connection.Query<ChangeRow>(
            $"SELECT {CampaignRows.Prefixed(ChangeRow.Columns, "cl")} FROM change_log cl WHERE {where} AND cl.batch_id IN @page ORDER BY cl.seq",
            parameters).ToList();
        var rendered = new HistoryRenderer(connection, campaign).Batches(rows);
        var order = page.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
        return new HistoryPage(
            rendered.OrderBy(b => order[b.BatchId]).ToList(),
            ReadCursor.Next(offset, limit, batches.Count, fingerprint),
            batches.Count);
    }

    private static string SinceTimestamp(string since)
    {
        if (!DateTimeOffset.TryParse(since.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            var shown = since.Length <= 40 ? since : since[..40] + "…";
            throw new DndInputException(
                $"since \"{shown}\" is not a date; give a UTC date or time such as \"2026-09-19\" or \"2026-09-19T20:00:00Z\".");
        }

        return CampaignDatabase.FormatTimestamp(at);
    }

    // The first change written in session n or later; else (nothing logged then) changes after the session started.
    private static long SessionStart(SqliteConnection connection, CampaignRow campaign, int number)
    {
        var first = connection.QueryFirstOrDefault<long?>(
            "SELECT min(cl.seq) FROM change_log cl JOIN session s ON s.entity_id = cl.session_id " +
            "WHERE cl.campaign_id = @campaignId AND s.number >= @number", new { campaignId = campaign.Id, number });
        if (first is { } seq)
        {
            return seq;
        }

        var started = connection.QueryFirstOrDefault<string?>(
            "SELECT started_at FROM session WHERE campaign_id = @campaignId AND number = @number", new { campaignId = campaign.Id, number });
        return started is null
            ? long.MaxValue
            : connection.QueryFirstOrDefault<long?>(
                  "SELECT min(seq) FROM change_log WHERE campaign_id = @campaignId AND at >= @started", new { campaignId = campaign.Id, started })
              ?? long.MaxValue;
    }

    private static void TargetFilter(SqliteConnection connection, CampaignRow campaign, IReadOnlyList<string> handles, Filter filter)
    {
        var resolver = new HandleResolver(connection, campaign.Id);
        var entities = new List<string>();
        var facts = new List<string>();
        var problems = new List<string>();
        for (var i = 0; i < handles.Count; i++)
        {
            var text = handles[i] ?? string.Empty;
            if (!CampaignHandle.TryParse(text, out var handle, out var problem))
            {
                problems.Add($"targets item {i + 1}: {problem}");
                continue;
            }

            var (entity, fact) = resolver.TryEntityOrFact(handle, includeDeleted: true);
            if (entity is not null)
            {
                entities.Add(entity.Id);
            }
            else if (fact is not null)
            {
                facts.Add(fact.Id);
            }
            else
            {
                problems.Add($"targets item {i + 1} ({handle}): nothing in this campaign has that handle.");
            }
        }

        Domain.Features.DslProblems.ThrowIfAny(problems, "history targets");
        var clauses = new List<string>();
        if (entities.Count > 0)
        {
            filter.Parameter("entities", entities);
            clauses.Add("cl.entity_id IN @entities OR cl.other_entity_id IN @entities");
        }

        if (facts.Count > 0)
        {
            clauses.Add(FactClause(connection, facts, filter));
        }

        filter.AddClause("(" + string.Join(" OR ", clauses) + ")");
    }

    private static void FactTargetFilter(SqliteConnection connection, IReadOnlyList<string> factIds, Filter filter) =>
        filter.AddClause("(" + FactClause(connection, factIds, filter) + ")");

    // A fact's own rows, its links and dependencies (the fact id is in their key), and its knowledge rows (found by id,
    // since a knowledge update's log row carries only the knowledge id).
    private static string FactClause(SqliteConnection connection, IReadOnlyList<string> factIds, Filter filter)
    {
        var knowledgeIds = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<string> { "cl.target_id IN @facts" };
        filter.Parameter("facts", factIds);
        for (var i = 0; i < factIds.Count; i++)
        {
            var name = "fact" + i.ToString(CultureInfo.InvariantCulture);
            filter.Parameter(name, factIds[i]);
            parts.Add($"(cl.target_table IN ('fact_link', 'fact_dependency') AND instr(cl.target_id, @{name}) > 0)");
            knowledgeIds.UnionWith(connection.Query<string>("SELECT id FROM knowledge WHERE fact_id = @factId", new { factId = factIds[i] }));
            knowledgeIds.UnionWith(connection.Query<string>(
                "SELECT DISTINCT target_id FROM change_log WHERE target_table = 'knowledge' AND op IN ('create', 'delete') " +
                "AND instr(coalesce(new_value, old_value, ''), @factId) > 0", new { factId = factIds[i] }));
        }

        if (knowledgeIds.Count > 0)
        {
            filter.Parameter("knowledge", knowledgeIds.ToList());
            parts.Add("(cl.target_table = 'knowledge' AND cl.target_id IN @knowledge)");
        }

        return string.Join(" OR ", parts);
    }

    private sealed class Filter
    {
        private readonly List<string> _clauses = [];
        private readonly Dictionary<string, object> _parameters = new(StringComparer.Ordinal);

        public string Where => _clauses.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", _clauses);

        public void Add(string clause, string name, object value)
        {
            _clauses.Add(clause);
            _parameters[name] = value;
        }

        public void AddClause(string clause) => _clauses.Add(clause);

        public void Parameter(string name, object value) => _parameters[name] = value;

        public DynamicParameters Parameters(string campaignId)
        {
            var parameters = new DynamicParameters();
            parameters.Add("campaignId", campaignId);
            foreach (var (name, value) in _parameters)
            {
                parameters.Add(name, value);
            }

            return parameters;
        }
    }
}

/// <summary>
/// Renders change_log rows (<see cref="HistoryReader"/>): ids to handles, values to short text. Author-facing only.
/// Phase 7's logged tables read as what they are about: "character:belmakor sheet", "character:bjorn-mountainfell holding
/// "Potion of Healing"", "faction:the-party coins", "character:vars award (xp)"; their entity columns (holder, item,
/// recipient, the session a holding was gained in) print as handles, never as raw ids.
/// </summary>
internal sealed class HistoryRenderer
{
    private const int ValueLength = 100;

    // Columns holding an entity id, per table; the rest of the id columns hold a fact or tag id.
    private static readonly HashSet<string> EntityIdColumns = new(StringComparer.Ordinal)
    {
        "parent_id", "introduced_session_id", "from_id", "to_id", "since_session_id", "until_session_id",
        "established_session_id", "entity_id", "knower_id", "learned_session_id", "via_entity_id", "valid_until_session_id",
        "arc_id", "session_id", "character_id", "quest_id", "resolved_session_id", "front_id", "from_beat_id", "to_beat_id",
        "my_character_id", "party_id", "current_location_id", "a_id", "b_id",
        "holder_id", "item_id", "recipient_id", "acquired_session_id",
    };

    private static readonly HashSet<string> FactIdColumns = new(StringComparer.Ordinal) { "fact_id", "depends_on", "superseded_by" };

    private readonly SqliteConnection _connection;
    private readonly ReadScope _scope;
    private readonly Dictionary<string, string> _entityRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _factLabels = new(StringComparer.Ordinal);

    public HistoryRenderer(SqliteConnection connection, CampaignRow campaign)
    {
        _connection = connection;
        _scope = ReadScope.Open(connection, campaign, Perspective.Author, null);
    }

    /// <summary>Rows (in seq order) grouped into batches in order of their first row.</summary>
    public IReadOnlyList<HistoryBatch> Batches(IReadOnlyList<ChangeRow> rows) =>
        rows.GroupBy(r => r.BatchId, StringComparer.Ordinal)
            .OrderBy(g => g.Min(r => r.Seq))
            .Select(g =>
            {
                var first = g.First();
                return new HistoryBatch(
                    g.Key,
                    first.At,
                    first.Actor,
                    first.Tool,
                    g.Select(r => r.Reason).LastOrDefault(r => r is not null),
                    _scope.SessionNumber(first.SessionId),
                    first.UndoOf,
                    g.Select(Change).ToList());
            })
            .ToList();

    private HistoryChange Change(ChangeRow row)
    {
        var meta = CampaignTables.TryGet(row.TargetTable, out var table) ? table : null;
        if (meta is null)
        {
            var unknown = $"{row.TargetTable} {row.Op}";
            return new HistoryChange(row.Seq, row.Action, row.Op, row.TargetTable, row.TargetTable, row.FieldPath, null, null, unknown);
        }

        var snapshot = row.Op switch
        {
            CampaignValues.ChangeOps.Create => Snapshot(meta, row.NewValue),
            CampaignValues.ChangeOps.Delete => Snapshot(meta, row.OldValue),
            _ => CurrentOrLogged(meta, row.TargetId),
        };
        var target = snapshot is null ? $"{meta.Name} (no longer exists)" : Label(meta, snapshot);
        switch (row.Op)
        {
            case CampaignValues.ChangeOps.Create:
                var created = snapshot is null ? string.Empty : Details(meta, snapshot);
                return new HistoryChange(row.Seq, row.Action, row.Op, meta.Name, target, null, null, null,
                    $"{target} created{(created.Length > 0 ? ": " + created : string.Empty)}");
            case CampaignValues.ChangeOps.Delete:
                return new HistoryChange(row.Seq, row.Action, row.Op, meta.Name, target, null, null, null, $"{target} deleted");
        }

        var field = row.FieldPath ?? string.Empty;
        if (field == "deleted_at")
        {
            var verb = row.NewValue is null ? "restored" : "deleted";
            return new HistoryChange(row.Seq, row.Action, row.Op, meta.Name, target, field, null, null, $"{target} {verb}");
        }

        var oldText = Value(meta, field, row.OldValue);
        var newText = Value(meta, field, row.NewValue);
        return new HistoryChange(row.Seq, row.Action, row.Op, meta.Name, target, field, oldText, newText,
            $"{target} {field}: {oldText} → {newText}");
    }

    private IReadOnlyDictionary<string, object?>? CurrentOrLogged(CampaignTable meta, string targetId)
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = $"SELECT {meta.ColumnList} FROM {meta.Name} WHERE {meta.KeyPredicate}";
            ChangeRecorder.BindKey(command, meta.ParseTargetId(targetId));
            if (ChangeRecorder.ReadRow(meta, command) is { } current)
            {
                return current;
            }
        }

        var logged = _connection.QueryFirstOrDefault<string?>(
            "SELECT coalesce(new_value, old_value) FROM change_log WHERE target_table = @table AND target_id = @targetId " +
            "AND op IN ('create', 'delete') ORDER BY seq DESC LIMIT 1", new { table = meta.Name, targetId });
        return Snapshot(meta, logged);
    }

    private static IReadOnlyDictionary<string, object?>? Snapshot(CampaignTable meta, string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return ChangeValues.RowFromJson(meta, json);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // What a row is, in a few words.
    private string Label(CampaignTable meta, IReadOnlyDictionary<string, object?> r)
    {
        string S(string column) => r.TryGetValue(column, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture)! : string.Empty;
        return meta.Name switch
        {
            "campaign" => "campaign " + S("slug"),
            "entity" => EntityLabel(S("id"), S("kind"), S("slug")),
            "entity_alias" => $"{EntityRef(S("entity_id"))} alias {Quote(S("alias"))}",
            "tag" => "tag " + Quote(S("name")),
            "entity_tag" => $"{EntityRef(S("entity_id"))} tag {Quote(TagName(S("tag_id")))}",
            "relation" => $"{EntityRef(S("from_id"))} {S("rel")} {EntityRef(S("to_id"))}",
            "cross_link" => $"{EntityRef(S("a_id"))} same_as {EntityRef(S("b_id"))}",
            "fact" => FactLabel(S("id")),
            "fact_link" => $"{FactHandle(S("fact_id"))} {S("role")} {EntityRef(S("entity_id"))}",
            "fact_dependency" => $"{FactHandle(S("fact_id"))} depends_on {FactHandle(S("depends_on"))}",
            "knowledge" => $"knowledge of {KnowerLabel(S("knower_kind"), S("knower_id"))} about " +
                           (S("fact_id").Length > 0 ? FactHandle(S("fact_id")) : EntityRef(S("entity_id"))),
            "session" => EntityRef(S("entity_id")),
            "session_attendance" => $"{EntityRef(S("session_id"))} attendance of {EntityRef(S("character_id"))}",
            "objective" => $"{EntityRef(S("quest_id"))} objective {Quote(Cut(S("text"), 60))}",
            "clock" => $"{EntityRef(S("entity_id"))} clock",
            "beat_edge" => $"{EntityRef(S("from_beat_id"))} leads_to {EntityRef(S("to_beat_id"))} ({S("mode")})",
            "character_sheet" => $"{EntityRef(S("entity_id"))} sheet",
            "holding" => $"{EntityRef(S("holder_id"))} holding {Quote(Cut(S("name"), 60))}",
            "currency_txn" => $"{EntityRef(S("holder_id"))} coins",
            "award" => $"{EntityRef(S("recipient_id"))} award ({S("kind")})",
            _ => meta.Name,
        };
    }

    // The interesting columns of a created row (not its ids, timestamps or defaults).
    private string Details(CampaignTable meta, IReadOnlyDictionary<string, object?> r)
    {
        var parts = new List<string>();
        void Add(string column, bool quote = false)
        {
            if (r.TryGetValue(column, out var value) && value is not null && Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: > 0 } text)
            {
                var shown = EntityIdColumns.Contains(column) ? EntityRef(text)
                    : FactIdColumns.Contains(column) ? FactHandle(text)
                    : quote ? Quote(Cut(text, ValueLength)) : Cut(text, ValueLength);
                parts.Add($"{column} {shown}");
            }
        }

        switch (meta.Name)
        {
            case "entity":
                Add("name", quote: true);
                Add("status");
                Add("visibility");
                if (r.GetValueOrDefault("canon_status") is string canon && canon != CampaignValues.CanonStatuses.Canon)
                {
                    Add("canon_status");
                }

                Add("code");
                break;
            case "fact":
                Add("canon_status");
                Add("visibility");
                Add("truth");
                break;
            case "knowledge":
                Add("state");
                Add("known_as", quote: true);
                if (r.GetValueOrDefault("learned_session_id") is string learned)
                {
                    parts.Add("learned " + EntityRef(learned));
                }

                break;
            case "relation":
                Add("status");
                Add("visibility");
                Add("label", quote: true);
                break;
            case "entity_alias":
            case "objective":
            case "cross_link":
                Add("visibility");
                Add("status");
                Add("note", quote: true);
                break;
            case "session":
                Add("number");
                Add("status");
                break;
            case "session_attendance":
                parts.Add(r.GetValueOrDefault("present") is 0L ? "absent" : "present");
                break;
            case "clock":
                parts.Add($"{r.GetValueOrDefault("filled")}/{r.GetValueOrDefault("segments")} {r.GetValueOrDefault("unit")}");
                break;
            case "character_sheet":
                Add("level");
                Add("max_hp");
                Add("ruleset");
                break;
            case "holding":
                parts.Add("quantity " + Number(r.GetValueOrDefault("quantity")));
                Add("srd_ref");
                Add("item_id");
                break;
            case "currency_txn":
                var coins = new[] { "pp", "gp", "ep", "sp", "cp" }
                    .Where(c => r.GetValueOrDefault(c) is long n && n != 0)
                    .Select(c => $"{((long)r[c]!).ToString("+#;-#", CultureInfo.InvariantCulture)} {c}")
                    .ToList();
                if (coins.Count > 0)
                {
                    parts.Add(string.Join(" ", coins));
                }

                Add("note", quote: true);
                break;
            case "award":
                Add("amount");
                Add("source", quote: true);
                Add("note", quote: true);
                break;
        }

        return string.Join(", ", parts);
    }

    // A logged value as text: ids as handles, JSON compact (gates with fact handles), text cut to one line.
    private string Value(CampaignTable meta, string field, string? logText)
    {
        if (logText is null)
        {
            return "(none)";
        }

        var column = field.Contains('.', StringComparison.Ordinal) ? field[..field.IndexOf('.', StringComparison.Ordinal)] : field;
        if (EntityIdColumns.Contains(column) && !field.Contains('.', StringComparison.Ordinal))
        {
            return EntityRef(logText);
        }

        if (FactIdColumns.Contains(column))
        {
            return FactHandle(logText);
        }

        if (meta.Name == "fact" && column == "gate" && FactGates.TryParse(logText, out var gate) && gate is not null)
        {
            return Cut(FactGates.Serialize(ReadGates.WithHandles(_scope, gate)), ValueLength * 2);
        }

        // Text with a space, or none at all, is quoted: an empty value printed bare reads "summary:  → Burned.".
        var text = Cut(logText, ValueLength);
        return meta.TryColumn(column, out var col) && col.Type == CampaignColumnType.Text &&
               (text.Length == 0 || text.Contains(' ', StringComparison.Ordinal))
            ? Quote(text)
            : text;
    }

    // A REAL as the ledger prints it: 2, not 2.0; 0.5 as is.
    private static string Number(object? value) => value switch
    {
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        null => "(none)",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    private string EntityLabel(string id, string kind, string slug)
    {
        var reference = EntityRef(id);
        return reference == "(deleted entity)" && kind.Length > 0 ? kind + ":" + slug : reference;
    }

    private string EntityRef(string id)
    {
        if (id.Length == 0)
        {
            return "(none)";
        }

        if (!_entityRefs.TryGetValue(id, out var reference))
        {
            reference = _scope.AuthorRef(id);
            _entityRefs[id] = reference;
        }

        return reference;
    }

    private string FactHandle(string id) => id.Length == 0 ? "(none)" : ReadGates.FactHandle(_scope, id);

    private string FactLabel(string id)
    {
        if (_factLabels.TryGetValue(id, out var label))
        {
            return label;
        }

        var fact = _connection.QueryFirstOrDefault<(long? Seq, string? Code, string? Statement)>(
            "SELECT seq, code, statement FROM fact WHERE id = @id", new { id });
        label = fact.Seq is { } seq
            ? $"f:{seq.ToString(CultureInfo.InvariantCulture)}{(fact.Code is null ? string.Empty : $" ({fact.Code})")} {Quote(Cut(fact.Statement ?? string.Empty, 80))}"
            : "(deleted fact)";
        _factLabels[id] = label;
        return label;
    }

    private string KnowerLabel(string kind, string knowerId) =>
        kind == CampaignValues.KnowerKinds.Character && knowerId.Length > 0 ? EntityRef(knowerId) : kind;

    private string TagName(string tagId) =>
        _connection.QueryFirstOrDefault<string?>("SELECT name FROM tag WHERE id = @tagId", new { tagId }) ?? "(deleted tag)";

    private static string Cut(string text, int max)
    {
        var line = ReadText.OneLine(text);
        return line.Length <= max ? line : ReadText.Excerpt(line, max);
    }

    private static string Quote(string text) => "\"" + text + "\"";
}
