using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>
/// Who and why for one batch: every change_log row a batch writes carries these.
/// </summary>
/// <param name="CampaignId">The campaign the batch writes to (change_log.campaign_id; not a foreign key, history outlives it).</param>
/// <param name="BatchId">A UUIDv7; one tool call that writes is one batch, whatever it touches. Shown in full to the model for undo.</param>
/// <param name="Actor"><see cref="CampaignValues.Actors"/>: <c>claude</c> (tools) or <c>cli</c>.</param>
/// <param name="Tool">"campaign_write/upsert"-style, or null.</param>
/// <param name="SessionId">
/// The batch's session context (explicit, else the live session, else null): the session ENTITY id. Point-in-time replay
/// reverses changes by this session's number, and rows with none are timeless.
/// </param>
/// <param name="Reason">The call's reason; gate warnings are appended with <see cref="ChangeRecorder.AppendReason"/>.</param>
/// <param name="UndoOf">For an undo batch, the batch it reverses (set on every row, so "was this undone?" is one index lookup).</param>
public sealed record BatchContext(
    string CampaignId,
    string BatchId,
    string Actor,
    string? Tool,
    string? SessionId,
    string? Reason,
    string? UndoOf = null)
{
    /// <summary>A context with a fresh batch id.</summary>
    public static BatchContext New(string campaignId, string actor, string? tool, string? sessionId, string? reason) =>
        new(campaignId, CampaignDatabase.NewId(), actor, tool, sessionId, reason);
}

/// <summary>What <see cref="ChangeRecorder.Upsert"/> did.</summary>
/// <param name="Row">The row as stored now (every column).</param>
/// <param name="Created">True when it was inserted.</param>
/// <param name="Changed">True when anything was written (always, when created).</param>
public sealed record UpsertResult(IReadOnlyDictionary<string, object?> Row, bool Created, bool Changed);

/// <summary>
/// Writes rows of the loggable tables AND their change_log rows, in one place, so no mutation of campaign data can
/// happen without its history (PLAN §6 principle 7; contract §3.5). Every write path (the write tools, knowledge, sessions,
/// undo) goes through one instance per batch, constructed inside the batch's write transaction.
///
/// <para>
/// <b>What it logs</b> (<see cref="ChangeValues"/> has the value encoding):
/// <list type="bullet">
/// <item><see cref="Insert"/>: <c>op=create</c>, <c>new_value</c> = the whole row as read back after the insert (so the
/// assigned <c>seq</c> and every default are in it, and undo-then-redo re-inserts exactly this row).</item>
/// <item><see cref="Update"/>: <c>op=update</c>, one row per changed column; one per changed top-level key of
/// <c>data</c>/<c>settings</c>. Unchanged values write and log nothing; <c>updated_at</c> is stamped, never logged.</item>
/// <item><see cref="Delete"/>: <c>op=delete</c>, <c>old_value</c> = the whole row.</item>
/// <item><see cref="SoftDelete"/>/<see cref="Restore"/>: an update of <c>deleted_at</c> with action delete/restore.</item>
/// </list>
/// <c>target_id</c> is the key (a JSON array in key order for a composite key), and <c>entity_id</c>/
/// <c>other_entity_id</c> follow <see cref="CampaignTables"/> so an entity's history finds every row about it.
/// </para>
/// <para>
/// <b>Rows are buffered and written by <see cref="Flush"/></b>, all with the batch's final reason. A gate warning is often
/// only known after the reveal it is about has been written (the route-completion warning needs the new row), and
/// change_log rows cannot be updated afterwards (append-only triggers), so writing each row immediately would leave the
/// reveal's own rows without the warning. <see cref="CampaignDatabase.Write{T}"/> flushes the transaction's recorder
/// before it commits (and before a dry run rolls back, so a dry run fails wherever the real run would), so a caller that
/// forgets cannot commit data without its history. The buffer keeps call order, which is the order undo reverses.
/// </para>
/// <para>
/// <b>One recorder per transaction.</b> A second one on the same transaction throws: one write transaction is one batch,
/// and two batches interleaved in one transaction would be undone as two units that were committed as one.
/// </para>
/// <para>
/// FK cascades (deleting an entity removes its aliases) and the FTS triggers are not logged: callers delete children
/// first, as undo does by reversing in order, and FTS is derived data.
/// </para>
/// </summary>
public sealed class ChangeRecorder
{
    private static readonly ConditionalWeakTable<SqliteTransaction, ChangeRecorder> ByTransaction = new();

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly List<PendingRow> _pending = [];
    private int _flushed;

    /// <param name="connection">The batch's connection.</param>
    /// <param name="transaction">The batch's write transaction (BEGIN IMMEDIATE, from <see cref="CampaignDatabase.Write{T}"/>).</param>
    /// <param name="batch">Who and why.</param>
    /// <param name="at">The batch's timestamp (<see cref="CampaignDatabase.Now"/>), on every row it logs and stamps.</param>
    /// <exception cref="InvalidOperationException">The transaction already has a recorder, or belongs to another connection.</exception>
    public ChangeRecorder(SqliteConnection connection, SqliteTransaction transaction, BatchContext batch, string at)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentException.ThrowIfNullOrWhiteSpace(at);
        if (!ReferenceEquals(transaction.Connection, connection))
        {
            throw new InvalidOperationException("The transaction is not open on this connection.");
        }

        if (!ByTransaction.TryAdd(transaction, this))
        {
            throw new InvalidOperationException(
                "This transaction already has a ChangeRecorder: one write transaction is one batch, with one recorder.");
        }

        _connection = connection;
        _transaction = transaction;
        Batch = batch;
        At = at;
        Reason = string.IsNullOrWhiteSpace(batch.Reason) ? null : batch.Reason;
    }

    public BatchContext Batch { get; }

    /// <summary>The batch timestamp.</summary>
    public string At { get; }

    /// <summary>The reason every row of this batch will carry: the call's, plus anything appended.</summary>
    public string? Reason { get; private set; }

    /// <summary>change_log rows this batch has produced (flushed or not).</summary>
    public int RowsLogged => _flushed + _pending.Count;

    /// <summary>
    /// Appends to the batch's reason ("; " between parts). Applies to every row of the batch, including rows logged
    /// before the call, because rows are buffered until <see cref="Flush"/>.
    /// </summary>
    public void AppendReason(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Reason = Reason is null ? text.Trim() : Reason + "; " + text.Trim();
    }

    /// <summary>
    /// Inserts a row and logs its creation. Omitted columns take their defaults; a table keyed by a generated <c>id</c>
    /// gets <see cref="CampaignDatabase.NewId"/> when the row has none; <c>created_at</c>/<c>updated_at</c> default to
    /// <see cref="At"/>. An explicit <c>seq</c> is kept (undo re-inserts a deleted entity under its old <c>e:</c> handle).
    /// </summary>
    /// <returns>The whole row as stored (with <c>seq</c>, <c>id</c> and defaults).</returns>
    public IReadOnlyDictionary<string, object?> Insert(string table, IReadOnlyDictionary<string, object?> row, string action)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var meta = CampaignTables.Get(table);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in row)
        {
            values[name] = ChangeValues.Normalize(meta, meta.Column(name), value);
        }

        if (meta.HasGeneratedId && (!values.TryGetValue("id", out var id) || id is null))
        {
            values["id"] = CampaignDatabase.NewId();
        }

        if (meta.HasCreatedAt && !values.ContainsKey("created_at"))
        {
            values["created_at"] = At;
        }

        if (meta.HasUpdatedAt && !values.ContainsKey("updated_at"))
        {
            values["updated_at"] = At;
        }

        var key = meta.KeyOf(values);
        using (var command = Command(
                   $"INSERT INTO {meta.Name} ({string.Join(", ", values.Keys)}) " +
                   $"VALUES ({string.Join(", ", values.Keys.Select((_, i) => "$v" + i))})"))
        {
            var i = 0;
            foreach (var value in values.Values)
            {
                command.Parameters.AddWithValue("$v" + i++, ChangeValues.ToParameter(value));
            }

            command.ExecuteNonQuery();
        }

        var stored = Read(meta, key) ??
                     throw new InvalidOperationException($"The {meta.Name} row just inserted could not be read back.");
        Log(meta, stored, CampaignValues.ChangeOps.Create, action, fieldPath: null, oldValue: null,
            newValue: CampaignLogJson.Serialize(ChangeValues.RowToJson(meta, stored)));
        return stored;
    }

    /// <inheritdoc cref="Update(string, IReadOnlyList{string}, IReadOnlyDictionary{string, object?}, string)"/>
    public bool Update(string table, string id, IReadOnlyDictionary<string, object?> changes, string action) =>
        Update(table, [id], changes, action);

    /// <summary>
    /// Updates columns of an existing row, logging each changed column (each changed top-level key of a per-key object
    /// column, when the new value is the whole object). Values equal to the stored ones write and log nothing. Key
    /// columns and <c>seq</c> cannot change (an alias rename is a delete and an insert: a changed key would orphan the
    /// history of the old one).
    /// </summary>
    /// <returns>True when anything changed.</returns>
    /// <exception cref="InvalidOperationException">The row does not exist (callers resolve handles first).</exception>
    public bool Update(string table, IReadOnlyList<string> key, IReadOnlyDictionary<string, object?> changes, string action)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var meta = CampaignTables.Get(table);
        var current = Read(meta, key) ??
                      throw new InvalidOperationException($"No {meta.Name} row {meta.TargetId(key)} to update.");
        return Apply(meta, current, changes, action);
    }

    /// <inheritdoc cref="PatchObject(string, IReadOnlyList{string}, string, JsonObject, string)"/>
    public bool PatchObject(string table, string id, string column, JsonObject patch, string action) =>
        PatchObject(table, [id], column, patch, action);

    /// <summary>
    /// Applies an RFC 7396 merge patch to an object column (<c>data</c>, <c>settings</c>): a null member removes the key,
    /// an object member merges into an object, anything else replaces the key's value (arrays whole). Logs one row per
    /// top-level key whose value changed.
    /// </summary>
    /// <returns>True when the object changed.</returns>
    public bool PatchObject(string table, IReadOnlyList<string> key, string column, JsonObject patch, string action)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var meta = CampaignTables.Get(table);
        var target = meta.Column(column);
        if (target.Type != CampaignColumnType.JsonObject)
        {
            throw new ArgumentException($"{meta.Name}.{column} is not a JSON object column.", nameof(column));
        }

        var current = Read(meta, key) ??
                      throw new InvalidOperationException($"No {meta.Name} row {meta.TargetId(key)} to patch.");
        var document = current[column] is string text ? JsonNode.Parse(text) : new JsonObject();
        var merged = MergePatch(document, patch) as JsonObject ??
                     throw new InvalidOperationException("A merge patch of an object with an object gave a non-object.");
        return Apply(meta, current, new Dictionary<string, object?> { [column] = CampaignLogJson.Serialize(merged) }, action);
    }

    /// <summary>
    /// Inserts, or updates, the one row whose <paramref name="match"/> columns hold these values (NULL-safe: a null
    /// matches NULL; COLLATE NOCASE columns match case-insensitively), as the write path does for rows with a natural key:
    /// a knowledge row per (target, knower kind, knower), a relation per (from, rel, to), an alias, a tag by name, an
    /// attendance row. Inside the batch's BEGIN IMMEDIATE transaction nothing can slip in between the read and the write,
    /// and unlike a SQL upsert it logs exactly what changed (never REPLACE: its implicit delete skips the FTS triggers).
    /// An existing row takes <paramref name="values"/> minus its key and match columns; a new one gets both.
    /// </summary>
    /// <exception cref="InvalidOperationException">More than one row matches (the match columns are not a unique key).</exception>
    public UpsertResult Upsert(string table, IReadOnlyDictionary<string, object?> match, IReadOnlyDictionary<string, object?> values, string action)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(values);
        var meta = CampaignTables.Get(table);
        var key = FindKey(table, match);
        if (key is null)
        {
            var row = new Dictionary<string, object?>(values, StringComparer.Ordinal);
            foreach (var (name, value) in match)
            {
                row[name] = value;
            }

            return new UpsertResult(Insert(table, row, action), Created: true, Changed: true);
        }

        var changes = values
            .Where(p => !match.ContainsKey(p.Key) && !meta.KeyColumns.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var changed = changes.Count > 0 && Update(table, key, changes, action);
        return new UpsertResult(Read(meta, key)!, Created: false, Changed: changed);
    }

    /// <summary>
    /// The key of the one row whose <paramref name="match"/> columns hold these values (NULL-safe), or null when none.
    /// </summary>
    /// <exception cref="InvalidOperationException">More than one row matches.</exception>
    public IReadOnlyList<string>? FindKey(string table, IReadOnlyDictionary<string, object?> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var meta = CampaignTables.Get(table);
        if (match.Count == 0)
        {
            throw new ArgumentException("A match needs at least one column.", nameof(match));
        }

        using var command = Command(
            $"SELECT {string.Join(", ", meta.KeyColumns)} FROM {meta.Name} WHERE " +
            string.Join(" AND ", match.Keys.Select((c, i) => $"{meta.Column(c).Name} IS $m{i}")) + " LIMIT 2");
        var index = 0;
        foreach (var (name, value) in match)
        {
            command.Parameters.AddWithValue("$m" + index++, ChangeValues.ToParameter(NormalizeMatch(meta, meta.Column(name), value)));
        }

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var key = Enumerable.Range(0, meta.KeyColumns.Count).Select(reader.GetString).ToList();
        if (reader.Read())
        {
            throw new InvalidOperationException($"More than one {meta.Name} row matches; the match columns are not a unique key.");
        }

        return key;
    }

    /// <inheritdoc cref="Delete(string, IReadOnlyList{string}, string)"/>
    public bool Delete(string table, string id, string action) => Delete(table, [id], action);

    /// <summary>Hard-deletes a row and logs it whole (so undo can re-insert it exactly).</summary>
    /// <returns>False when there was no such row (nothing logged).</returns>
    public bool Delete(string table, IReadOnlyList<string> key, string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var meta = CampaignTables.Get(table);
        var current = Read(meta, key);
        if (current is null)
        {
            return false;
        }

        using (var command = Command($"DELETE FROM {meta.Name} WHERE {meta.KeyPredicate}"))
        {
            BindKey(command, meta.KeyOf(current));
            command.ExecuteNonQuery();
        }

        Log(meta, current, CampaignValues.ChangeOps.Delete, action, fieldPath: null,
            oldValue: CampaignLogJson.Serialize(ChangeValues.RowToJson(meta, current)), newValue: null);
        return true;
    }

    /// <summary>
    /// Soft-deletes an entity or fact (sets <c>deleted_at</c>; action <c>delete</c>). Its FTS row goes (the trigger), its
    /// slug, code and handles stay reserved, and <see cref="Restore"/> brings it back. A row that is already deleted is left
    /// exactly as it is, whenever it was deleted: moving <c>deleted_at</c> to now would log a second "delete" of something
    /// already gone, and the caller would report "deleted" where nothing changed.
    /// </summary>
    /// <returns>False when it was already deleted (nothing written or logged).</returns>
    /// <exception cref="InvalidOperationException">There is no such row.</exception>
    public bool SoftDelete(string table, string id) => SetDeleted(table, id, At, CampaignValues.OpKinds.Delete);

    /// <summary>Restores a soft-deleted entity or fact (clears <c>deleted_at</c>; action <c>restore</c>).</summary>
    /// <returns>False when it was not deleted.</returns>
    public bool Restore(string table, string id) => SetDeleted(table, id, null, CampaignValues.OpKinds.Restore);

    /// <inheritdoc cref="Read(string, IReadOnlyList{string})"/>
    public IReadOnlyDictionary<string, object?>? Read(string table, string id) => Read(CampaignTables.Get(table), [id]);

    /// <summary>The whole current row (every catalogued column), or null when there is none.</summary>
    public IReadOnlyDictionary<string, object?>? Read(string table, IReadOnlyList<string> key) => Read(CampaignTables.Get(table), key);

    /// <summary>
    /// Writes the buffered change_log rows, each with the batch's current <see cref="Reason"/>. Idempotent;
    /// <see cref="CampaignDatabase.Write{T}"/> calls it before committing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Rows are pending but the transaction has already ended.</exception>
    public void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        if (_transaction.Connection is null)
        {
            throw new InvalidOperationException(
                "The batch's transaction ended before its change_log rows were written; the batch has no history.");
        }

        using var command = Command(
            "INSERT INTO change_log(campaign_id, at, session_id, actor, tool, batch_id, action, op, target_table, target_id, " +
            "entity_id, other_entity_id, field_path, old_value, new_value, reason, undo_of) VALUES ($campaign, $at, $session, " +
            "$actor, $tool, $batch, $action, $op, $table, $target, $entity, $other, $field, $old, $new, $reason, $undo)");
        var p = (string name) => command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        var campaign = p("$campaign");
        var at = p("$at");
        var session = p("$session");
        var actor = p("$actor");
        var tool = p("$tool");
        var batch = p("$batch");
        var actionParameter = p("$action");
        var op = p("$op");
        var tableParameter = p("$table");
        var target = p("$target");
        var entity = p("$entity");
        var other = p("$other");
        var field = p("$field");
        var old = p("$old");
        var @new = p("$new");
        var reason = p("$reason");
        var undo = p("$undo");
        foreach (var row in _pending)
        {
            campaign.Value = Batch.CampaignId;
            at.Value = At;
            session.Value = ChangeValues.ToParameter(Batch.SessionId);
            actor.Value = Batch.Actor;
            tool.Value = ChangeValues.ToParameter(Batch.Tool);
            batch.Value = Batch.BatchId;
            actionParameter.Value = row.Action;
            op.Value = row.Op;
            tableParameter.Value = row.Table;
            target.Value = row.TargetId;
            entity.Value = ChangeValues.ToParameter(row.EntityId);
            other.Value = ChangeValues.ToParameter(row.OtherEntityId);
            field.Value = ChangeValues.ToParameter(row.FieldPath);
            old.Value = ChangeValues.ToParameter(row.OldValue);
            @new.Value = ChangeValues.ToParameter(row.NewValue);
            reason.Value = ChangeValues.ToParameter(Reason);
            undo.Value = ChangeValues.ToParameter(Batch.UndoOf);
            command.ExecuteNonQuery();
        }

        _flushed += _pending.Count;
        _pending.Clear();
    }

    /// <summary>Flushes the recorder of <paramref name="transaction"/>, if it has one.</summary>
    internal static void FlushFor(SqliteTransaction transaction)
    {
        if (ByTransaction.TryGetValue(transaction, out var recorder))
        {
            recorder.Flush();
        }
    }

    /// <summary>RFC 7396: null removes, objects merge recursively, anything else replaces.</summary>
    internal static JsonNode? MergePatch(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        var result = target as JsonObject ?? new JsonObject();
        foreach (var (name, value) in patchObject.ToList())
        {
            if (value is null)
            {
                result.Remove(name);
            }
            else
            {
                var existing = result.TryGetPropertyValue(name, out var node) ? node : null;
                var merged = MergePatch(existing?.DeepClone(), value);
                result[name] = merged;
            }
        }

        return result;
    }

    // A match value may be null whatever the column (knower_id IS NULL for a party row).
    private static object? NormalizeMatch(CampaignTable meta, CampaignColumn column, object? value) =>
        value is null ? null : ChangeValues.Normalize(meta, column, value);

    private bool SetDeleted(string table, string id, string? deletedAt, string action)
    {
        var meta = CampaignTables.Get(table);
        if (meta != CampaignTables.Entity && meta != CampaignTables.Fact)
        {
            throw new ArgumentException($"Only entity and fact rows are soft-deleted, not {meta.Name}.", nameof(table));
        }

        var current = Read(meta, [id]) ??
                      throw new InvalidOperationException($"No {meta.Name} row {meta.TargetId([id])} to update.");
        if (deletedAt is not null && current["deleted_at"] is not null)
        {
            return false;
        }

        return Apply(meta, current, new Dictionary<string, object?> { ["deleted_at"] = deletedAt }, action);
    }

    // The shared update: compares, writes the changed columns (plus updated_at) and logs the logged ones.
    private bool Apply(CampaignTable meta, IReadOnlyDictionary<string, object?> current, IReadOnlyDictionary<string, object?> changes, string action)
    {
        var sets = new Dictionary<string, object?>(StringComparer.Ordinal);
        var logs = new List<(string Field, string? Old, string? New)>();
        foreach (var (name, raw) in changes)
        {
            var column = meta.Column(name);
            if (meta.KeyColumns.Contains(name) || name == "seq")
            {
                throw new ArgumentException($"{meta.Name}.{name} identifies the row and cannot be updated.", nameof(changes));
            }

            var value = ChangeValues.Normalize(meta, column, raw);
            var old = current[name];
            if (ChangeValues.Same(column, old, value))
            {
                continue;
            }

            sets[name] = value;
            if (!column.Logged)
            {
                continue;
            }

            if (column.LoggedPerKey)
            {
                logs.AddRange(KeyChanges(column, old as string, value as string));
            }
            else
            {
                logs.Add((name, ChangeValues.ToLogText(column, old), ChangeValues.ToLogText(column, value)));
            }
        }

        if (sets.Count == 0)
        {
            return false;
        }

        if (meta.HasUpdatedAt && !sets.ContainsKey("updated_at"))
        {
            sets["updated_at"] = At;
        }

        var key = meta.KeyOf(current);
        using (var command = Command(
                   $"UPDATE {meta.Name} SET {string.Join(", ", sets.Keys.Select((c, i) => $"{c} = $v{i}"))} WHERE {meta.KeyPredicate}"))
        {
            var i = 0;
            foreach (var value in sets.Values)
            {
                command.Parameters.AddWithValue("$v" + i++, ChangeValues.ToParameter(value));
            }

            BindKey(command, key);
            command.ExecuteNonQuery();
        }

        var after = new Dictionary<string, object?>(current, StringComparer.Ordinal);
        foreach (var (name, value) in sets)
        {
            after[name] = value;
        }

        foreach (var (field, old, @new) in logs)
        {
            Log(meta, after, CampaignValues.ChangeOps.Update, action, field, old, @new);
        }

        return true;
    }

    // One (field, old, new) per top-level key whose value differs; absent is SQL NULL.
    private static IEnumerable<(string Field, string? Old, string? New)> KeyChanges(CampaignColumn column, string? oldText, string? newText)
    {
        var before = (oldText is null ? null : JsonNode.Parse(oldText)) as JsonObject ?? new JsonObject();
        var after = (newText is null ? null : JsonNode.Parse(newText)) as JsonObject ?? new JsonObject();
        var keys = before.Select(p => p.Key).Concat(after.Select(p => p.Key).Where(k => !before.ContainsKey(k))).ToList();
        foreach (var key in keys)
        {
            var hadOld = before.TryGetPropertyValue(key, out var oldValue);
            var hasNew = after.TryGetPropertyValue(key, out var newValue);
            if (hadOld && hasNew && JsonNode.DeepEquals(oldValue, newValue))
            {
                continue;
            }

            yield return ($"{column.Name}.{key}",
                hadOld ? CampaignLogJson.Serialize(oldValue) : null,
                hasNew ? CampaignLogJson.Serialize(newValue) : null);
        }
    }

    private void Log(CampaignTable meta, IReadOnlyDictionary<string, object?> row, string op, string action, string? fieldPath, string? oldValue, string? newValue)
    {
        _pending.Add(new PendingRow(
            action,
            op,
            meta.Name,
            meta.TargetId(meta.KeyOf(row)),
            meta.EntityColumn is { } e ? row[e] as string : null,
            meta.OtherEntityColumn is { } o ? row[o] as string : null,
            fieldPath,
            oldValue,
            newValue));
    }

    private IReadOnlyDictionary<string, object?>? Read(CampaignTable meta, IReadOnlyList<string> key)
    {
        using var command = Command($"SELECT {meta.ColumnList} FROM {meta.Name} WHERE {meta.KeyPredicate}");
        BindKey(command, key);
        return ReadRow(meta, command);
    }

    internal static IReadOnlyDictionary<string, object?>? ReadRow(CampaignTable meta, SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var row = new Dictionary<string, object?>(meta.Columns.Count, StringComparer.Ordinal);
        for (var i = 0; i < meta.Columns.Count; i++)
        {
            row[meta.Columns[i].Name] = ChangeValues.FromReader(reader, i);
        }

        return row;
    }

    internal static void BindKey(SqliteCommand command, IReadOnlyList<string> key)
    {
        for (var i = 0; i < key.Count; i++)
        {
            command.Parameters.AddWithValue("$k" + i, key[i]);
        }
    }

    private SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        return command;
    }

    private sealed record PendingRow(
        string Action,
        string Op,
        string Table,
        string TargetId,
        string? EntityId,
        string? OtherEntityId,
        string? FieldPath,
        string? OldValue,
        string? NewValue);
}
