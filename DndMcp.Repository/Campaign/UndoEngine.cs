using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>What an undo did.</summary>
/// <param name="UndoneBatchId">The batch that was reversed (the full id, even when a prefix was given).</param>
/// <param name="UndoBatchId">The new batch that reverses it (itself undoable: that is redo).</param>
/// <param name="Reversed">The original batch's change_log rows, in the order they were reversed (newest first).</param>
/// <param name="RowsLogged">change_log rows the undo batch wrote.</param>
/// <param name="WasUndo">True when the reversed batch was itself an undo (so this was a redo).</param>
public sealed record UndoResult(
    string UndoneBatchId,
    string UndoBatchId,
    IReadOnlyList<ChangeRow> Reversed,
    int RowsLogged,
    bool WasUndo);

/// <summary>
/// Reverses one batch as a new batch (contract §3.5): every row of the original, newest first, through a
/// <see cref="ChangeRecorder"/> whose rows all carry <c>undo_of</c> = the original and action <c>undo</c>. A create
/// becomes a hard delete, an update writes the old value back (a <c>data.&lt;key&gt;</c> row sets or removes just that
/// key), a delete re-inserts the logged row (under its old <c>seq</c>, so <c>e:12</c> means the same entity again). Undo
/// only ever inserts change_log rows; the append-only triggers never fire.
///
/// <para>
/// <b>Refused</b> with a <see cref="DndInputException"/> the model can act on. Every refusal but the last kind below comes
/// before anything is written; none is ever committed, because the caller lets it roll the transaction back (see
/// <see cref="Undo"/>):
/// <list type="bullet">
/// <item>No such batch in this campaign, a prefix shorter than 8 characters, or a prefix matching several batches.</item>
/// <item>Already undone: a batch whose rows carry <c>undo_of</c> = it exists (the message names it, and says which batch
/// to undo instead when that undo was itself undone).</item>
/// <item><b>A conflict</b>: a later change_log row (higher seq, same campaign) touched the same row and field
/// (any change to a row the batch created or deleted counts), or mentions an id the batch created (in its target,
/// entity columns or values: a relation to a created entity, a fact_link, a knowledge row, a gate that stores the id;
/// UUIDs are unique strings, so a substring test is exact). Reversing past a later edit would silently clobber it, and
/// deleting a created row later rows point at would cascade them away unlogged. The refusal lists the later batches,
/// newest first, to undo first.</item>
/// <item><b>cross_link is the one table shared between campaigns</b> (it links an entity here to its twin in another
/// campaign, and is logged under whichever campaign the call wrote in), so its later rows are scanned in every campaign:
/// otherwise undoing the creation of an entity another campaign has since linked would delete that campaign's link with
/// no history row, leaving its log describing a link that is gone. Such a batch is listed with its campaign's slug.</item>
/// <item><b>A session the batch created is in use</b> (a <c>start</c> or <c>record_past</c> that made the session, or a
/// redo that put one back). Dice rolled in it: refused outright, because dice are never in change_log (a roll cannot be
/// un-rolled), so deleting the session would silently detach its rolls (<c>dice_roll.session_id</c> is ON DELETE SET
/// NULL) and a redo could not re-attach them: the session's roll log, secret rolls included, would be gone from every
/// view for good. Later batches filed under it (their <c>session_id</c>, which the id scan above does not read): a
/// conflict like any other, so those batches are undone first; otherwise they would point at a missing session, history
/// "since" that session would find nothing, and point-in-time replay would treat them as timeless. Either way the
/// message says to keep the session (and end it, if it is live) instead.</item>
/// <item>A row the batch updated no longer exists, or re-creating a row it deleted collides with one that exists now
/// (an entity created since with the same slug or code). Existence is checked before anything is reversed; a collision
/// shows only when the row is re-inserted.</item>
/// </list>
/// A later batch and its own undo, both after the batch being undone, cancel out and are not conflicts (unless that undo
/// was itself undone): without this, "undo, oops, redo" on anything the batch touched would leave the batch undoable
/// never again.
/// </para>
/// <para>
/// Messages name batches, campaigns (by slug, for another campaign's cross_link batch), tables, fields and
/// <c>e:</c>/<c>f:</c> handles only, never entity names or text: whoever drives the client reads them.
/// </para>
/// </summary>
public static partial class UndoEngine
{
    /// <summary>The shortest batch-id prefix undo accepts.</summary>
    public const int MinimumPrefixLength = 8;

    /// <summary>The action label on every row of an undo batch.</summary>
    public const string UndoAction = "undo";

    /// <summary>
    /// Reverses <paramref name="batchIdOrPrefix"/> inside the caller's write transaction. <paramref name="undoBatch"/>
    /// supplies who/why/session; its <see cref="BatchContext.UndoOf"/> is replaced by the resolved batch id and its
    /// campaign must be <paramref name="campaignId"/>. Construct no other <see cref="ChangeRecorder"/> on this transaction.
    /// </summary>
    /// <param name="at">The undo batch's timestamp (<see cref="CampaignDatabase.Now"/>).</param>
    /// <param name="afterReversal">
    /// Optional: runs on the undo batch's own recorder once every row is reversed, before its rows are flushed. It is for
    /// state DERIVED from the reversed data that must follow it in the same batch (the write path re-derives secret
    /// statuses, contract §3.4: undoing a reveal that is not the latest one to touch a secret's facts otherwise leaves the
    /// stored status describing knowledge that is gone), and for checks that compare the data after the reversal with
    /// what <paramref name="beforeReversal"/> saw. What it writes carries <c>undo_of</c> like the reversal's own rows, so a
    /// redo reverses it too. It must write through the recorder it is given: a second recorder on the transaction throws.
    /// </param>
    /// <param name="beforeReversal">
    /// Optional: runs once every refusal has passed and the undo batch's recorder exists, before the first row is
    /// reversed, with that recorder and the original batch's rows (in seq order). It is for a "before" snapshot of what
    /// the rows are about to change: the write path's player-text check notes every entity and fact the rows touch, so
    /// that an undo which makes text readable to the players (undoing the batch that gave the party its known_as for an
    /// NPC shows them his true name) is warned about like any other write. It must not write: anything it changed would
    /// be reversed by nothing and logged by nobody.
    /// </param>
    /// <exception cref="DndInputException">
    /// Refused (see the class summary). Every refusal but a re-insert collision comes before anything is written; a
    /// collision comes mid-reversal, after newer rows were already reversed on the transaction. So a caller must never
    /// catch this (or anything else Undo throws) and then commit: let it escape <see cref="CampaignDatabase.Write{T}"/>,
    /// which rolls the transaction back, and build the message from it outside.
    /// </exception>
    public static UndoResult Undo(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string campaignId,
        string batchIdOrPrefix,
        BatchContext undoBatch,
        string at,
        Action<ChangeRecorder>? afterReversal = null,
        Action<ChangeRecorder, IReadOnlyList<ChangeRow>>? beforeReversal = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        ArgumentNullException.ThrowIfNull(undoBatch);
        if (!string.Equals(undoBatch.CampaignId, campaignId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The undo batch must belong to the campaign being undone.", nameof(undoBatch));
        }

        CampaignDatabase.EnsureDapperConfigured();
        var batchId = ResolveBatch(connection, transaction, campaignId, batchIdOrPrefix);
        var rows = connection.Query<ChangeRow>(
            $"SELECT {ChangeRow.Columns} FROM change_log WHERE campaign_id = @campaignId AND batch_id = @batchId ORDER BY seq",
            new { campaignId, batchId }, transaction).ToList();

        RefuseIfUndone(connection, transaction, campaignId, batchId);
        var sessions = CreatedSessions(rows);
        RefuseIfDiceWereRolled(connection, transaction, campaignId, batchId, sessions);
        RefuseIfConflicts(connection, transaction, campaignId, batchId, rows, sessions);
        RefuseIfMissing(connection, transaction, batchId, rows);

        var recorder = new ChangeRecorder(connection, transaction, undoBatch with { UndoOf = batchId }, at);
        beforeReversal?.Invoke(recorder, rows);
        var reversed = new List<ChangeRow>(rows.Count);
        foreach (var row in Enumerable.Reverse(rows))
        {
            try
            {
                Reverse(recorder, row);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint && row.Op == CampaignValues.ChangeOps.Delete)
            {
                throw new DndInputException(
                    $"Batch {batchId} cannot be undone: it deleted {Describe(connection, transaction, row, subject: true)}, and " +
                    "putting it back collides with something that exists now (most often an entity or fact created since with " +
                    "the same slug or code). Rename or remove that one, then undo again.", ex);
            }

            reversed.Add(row);
        }

        afterReversal?.Invoke(recorder);
        recorder.Flush();
        return new UndoResult(batchId, undoBatch.BatchId, reversed, recorder.RowsLogged, rows.Any(r => r.UndoOf is not null));
    }

    /// <summary>
    /// The full batch id a caller's text names in this campaign: an exact id, or a unique prefix of at least
    /// <see cref="MinimumPrefixLength"/> characters (case-insensitive).
    /// </summary>
    /// <exception cref="DndInputException">Too short, not an id, unknown here, or ambiguous.</exception>
    public static string ResolveBatch(SqliteConnection connection, SqliteTransaction? transaction, string campaignId, string batchIdOrPrefix)
    {
        CampaignDatabase.EnsureDapperConfigured();
        var text = (batchIdOrPrefix ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length < MinimumPrefixLength)
        {
            throw new DndInputException(
                $"batch_id \"{Echo(text)}\" is too short: give the batch id printed after the write (at least " +
                $"{MinimumPrefixLength} characters of it), e.g. \"0199a1b2-…\".");
        }

        if (!BatchIdPattern().IsMatch(text))
        {
            throw new DndInputException(
                $"batch_id \"{Echo(text)}\" is not a batch id: batch ids are hexadecimal with hyphens, as printed after a " +
                "write (e.g. \"0199a1b2-7c3d-7e4f-8a9b-0c1d2e3f4a5b\").");
        }

        var matches = connection.Query<string>(
            "SELECT DISTINCT batch_id FROM change_log WHERE campaign_id = @campaignId AND batch_id LIKE @prefix ORDER BY batch_id",
            new { campaignId, prefix = text + "%" }, transaction).ToList();
        return matches.Count switch
        {
            0 => throw new DndInputException(NoBatch(connection, transaction, campaignId, text)),
            1 => matches[0],
            _ => throw new DndInputException(
                $"batch_id \"{text}\" matches {matches.Count} batches ({string.Join(", ", matches.Take(5))}); give more of the id."),
        };
    }

    // "No batch …" with a list call that names the campaign searched: the batch was looked up in the campaign the call
    // resolved to, and the usual reason it is not there is that it belongs to another one (an undo printed without
    // campaign, sent while another campaign is current). Sent as printed, the list call shows this campaign's batches
    // whichever campaign is current; the sentence after it says how to reach the other one.
    private static string NoBatch(SqliteConnection connection, SqliteTransaction? transaction, string campaignId, string text)
    {
        var slug = connection.ExecuteScalar<string?>("SELECT slug FROM campaign WHERE id = @campaignId", new { campaignId }, transaction);
        var since = slug is null
            ? "campaign_history {\"action\": \"since\"}"
            : $"campaign_history {{\"action\": \"since\", \"campaign\": \"{slug}\"}}";
        return $"No batch {Echo(text)} in this campaign. {since} lists its recent batches with their ids; a batch of another " +
               "campaign is found by passing that campaign's slug as campaign.";
    }

    private static void RefuseIfUndone(SqliteConnection connection, SqliteTransaction transaction, string campaignId, string batchId)
    {
        var undoneBy = connection.Query<string>(
            "SELECT batch_id FROM change_log WHERE campaign_id = @campaignId AND undo_of = @batchId GROUP BY batch_id ORDER BY min(seq)",
            new { campaignId, batchId }, transaction).ToList();
        if (undoneBy.Count == 0)
        {
            return;
        }

        var undo = undoneBy[^1];
        var redo = connection.Query<string>(
            "SELECT batch_id FROM change_log WHERE campaign_id = @campaignId AND undo_of = @undo GROUP BY batch_id ORDER BY min(seq)",
            new { campaignId, undo }, transaction).LastOrDefault();
        var instead = redo is null
            ? $"undo {undo} to redo it."
            : $"that undo was itself undone by {redo}, so undo {redo} to reverse it again.";
        throw new DndInputException($"Batch {batchId} was already undone by batch {undo}; {instead}");
    }

    // The sessions this batch created (session id → number), from its own create rows on the session table: a start or
    // record_past that made the session, or a redo that put one back.
    private static IReadOnlyDictionary<string, long> CreatedSessions(IReadOnlyList<ChangeRow> rows)
    {
        var sessions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Op == CampaignValues.ChangeOps.Create && row.TargetTable == CampaignTables.Session.Name &&
                row.NewValue is { } created && JsonNode.Parse(created) is JsonObject logged &&
                logged["number"] is JsonValue number && number.TryGetValue<long>(out var n))
            {
                sessions[row.TargetId] = n;
            }
        }

        return sessions;
    }

    private static void RefuseIfDiceWereRolled(SqliteConnection connection, SqliteTransaction transaction, string campaignId, string batchId,
        IReadOnlyDictionary<string, long> sessions)
    {
        foreach (var (sessionId, number) in sessions)
        {
            var rolls = connection.ExecuteScalar<long>("SELECT count(*) FROM dice_roll WHERE session_id = @sessionId", new { sessionId }, transaction);
            if (rolls > 0)
            {
                var count = rolls == 1 ? "a dice roll was" : $"{rolls.ToString(CultureInfo.InvariantCulture)} dice rolls were";
                throw new DndInputException(
                    $"Batch {batchId} cannot be undone: it created {SessionHandle(number)}, and {count} logged in that " +
                    "session. Dice cannot be un-rolled, so undoing the session would leave its rolls with no session. Keep " +
                    $"the session instead (end it with {EndCall(connection, transaction, campaignId)} if it is live). Nothing " +
                    "was changed.");
            }
        }
    }

    // The call that ends a live session, naming the campaign (the undo may be for a campaign that is not the current one).
    private static string EndCall(SqliteConnection connection, SqliteTransaction transaction, string campaignId)
    {
        var slug = connection.ExecuteScalar<string?>("SELECT slug FROM campaign WHERE id = @campaignId", new { campaignId }, transaction);
        return slug is null
            ? "campaign_session {\"action\": \"end\"}"
            : $"campaign_session {{\"action\": \"end\", \"campaign\": \"{slug}\"}}";
    }

    private static void RefuseIfConflicts(SqliteConnection connection, SqliteTransaction transaction, string campaignId, string batchId,
        IReadOnlyList<ChangeRow> rows, IReadOnlyDictionary<string, long> sessions)
    {
        var lastSeq = rows[^1].Seq;
        var later = connection.Query<ChangeRow>(
            $"SELECT {ChangeRow.Columns} FROM change_log WHERE (campaign_id = @campaignId OR target_table = @crossLink) " +
            "AND seq > @lastSeq ORDER BY seq",
            new { campaignId, crossLink = CampaignTables.CrossLink.Name, lastSeq }, transaction).ToList();
        if (later.Count == 0)
        {
            return;
        }

        var cancelled = CancelledBatches(later);
        var created = rows
            .Where(r => r.Op == CampaignValues.ChangeOps.Create && CampaignTables.TryGet(r.TargetTable, out var t) && t.HasGeneratedId)
            .Select(r => r.TargetId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var touched = rows.Select(r => (r.TargetTable, r.TargetId)).ToHashSet();

        var conflicts = new List<(ChangeRow Later, string What)>();
        var filedInSession = false;
        foreach (var row in later)
        {
            if (cancelled.Contains(row.BatchId))
            {
                continue;
            }

            if (touched.Contains((row.TargetTable, row.TargetId)))
            {
                var clash = rows.FirstOrDefault(r => r.TargetTable == row.TargetTable && r.TargetId == row.TargetId &&
                    (r.Op != CampaignValues.ChangeOps.Update || row.Op != CampaignValues.ChangeOps.Update ||
                     string.Equals(r.FieldPath, row.FieldPath, StringComparison.Ordinal)));
                if (clash is not null)
                {
                    conflicts.Add((row, Describe(connection, transaction, row)));
                    continue;
                }
            }

            if (created.Any(id => Mentions(row, id)))
            {
                conflicts.Add((row, Describe(connection, transaction, row)));
                continue;
            }

            if (row.SessionId is { } filed && sessions.TryGetValue(filed, out var number))
            {
                filedInSession = true;
                conflicts.Add((row, $"{Describe(connection, transaction, row)}, made in {SessionHandle(number)}"));
            }
        }

        if (conflicts.Count == 0)
        {
            return;
        }

        var batches = conflicts
            .GroupBy(c => c.Later.BatchId)
            .Select(g => (BatchId: g.Key, First: g.First(), Seq: g.Max(c => c.Later.Seq)))
            .OrderByDescending(b => b.Seq)
            .ToList();
        var lines = batches.Take(10).Select(b =>
            $"- {b.BatchId} ({b.First.Later.At}{(b.First.Later.Tool is null ? string.Empty : ", " + b.First.Later.Tool)}" +
            $"{OtherCampaign(connection, transaction, campaignId, b.First.Later.CampaignId)}): {b.First.What}");
        var more = batches.Count > 10 ? $"\n- … and {batches.Count - 10} more" : string.Empty;
        var session = filedInSession
            ? $" It created {string.Join(" and ", sessions.Values.Order().Select(SessionHandle))}, and later batches were made in " +
              $"it; to keep that session instead, leave this batch (end the session with {EndCall(connection, transaction, campaignId)} " +
              "if it is live)."
            : string.Empty;
        throw new DndInputException(
            $"Batch {batchId} cannot be undone: later changes build on what it changed.{session} Undo these first, newest " +
            $"first, then undo {batchId} again:\n{string.Join("\n", lines)}{more}");
    }

    private static string SessionHandle(long number) => "session:" + number.ToString(CultureInfo.InvariantCulture);

    // Later batches that cancel out: an undo batch and the batch it reversed, both later than the batch being undone,
    // unless the undo was itself undone (newest first, so a redo cancels its undo and leaves the original standing).
    private static HashSet<string> CancelledBatches(IReadOnlyList<ChangeRow> later)
    {
        var laterBatches = later.Select(r => r.BatchId).ToHashSet(StringComparer.Ordinal);
        var undoOf = later.Where(r => r.UndoOf is not null)
            .GroupBy(r => r.BatchId)
            .Select(g => (Batch: g.Key, Of: g.First().UndoOf!, Seq: g.Max(r => r.Seq)))
            .OrderByDescending(b => b.Seq);
        var cancelled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (batch, of, _) in undoOf)
        {
            if (!cancelled.Contains(batch) && laterBatches.Contains(of) && !cancelled.Contains(of))
            {
                cancelled.Add(batch);
                cancelled.Add(of);
            }
        }

        return cancelled;
    }

    // Walks the batch newest first as Reverse will, tracking which rows exist, and refuses before anything is written when
    // an update would land on a row that is gone (removed since by something the log does not show, such as a raw edit or
    // a cascade from another campaign's undo before cross_link rows were checked). Without it the refusal came mid-way,
    // after newer rows had been reversed on the caller's transaction.
    private static void RefuseIfMissing(SqliteConnection connection, SqliteTransaction transaction, string batchId, IReadOnlyList<ChangeRow> rows)
    {
        var exists = new Dictionary<(string Table, string Target), bool>();
        foreach (var row in Enumerable.Reverse(rows))
        {
            var target = (row.TargetTable, row.TargetId);
            switch (row.Op)
            {
                case CampaignValues.ChangeOps.Create:
                    exists[target] = false;
                    break;
                case CampaignValues.ChangeOps.Delete:
                    exists[target] = true;
                    break;
                default:
                    if (!exists.TryGetValue(target, out var present))
                    {
                        var table = CampaignTables.Get(row.TargetTable);
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = $"SELECT count(*) FROM {table.Name} WHERE {table.KeyPredicate}";
                        ChangeRecorder.BindKey(command, table.ParseTargetId(row.TargetId));
                        present = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
                        exists[target] = present;
                    }

                    if (!present)
                    {
                        throw new DndInputException(
                            $"Batch {batchId} cannot be undone: {Describe(connection, transaction, row, subject: true)}, which " +
                            "it changed, no longer exists. Nothing was changed.");
                    }

                    break;
            }
        }
    }

    // " in campaign <slug>" for a later batch written in another campaign (only cross_link rows are scanned there).
    private static string OtherCampaign(SqliteConnection connection, SqliteTransaction transaction, string campaignId, string laterCampaignId)
    {
        if (string.Equals(campaignId, laterCampaignId, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var slug = connection.ExecuteScalar<string?>("SELECT slug FROM campaign WHERE id = @id", new { id = laterCampaignId }, transaction);
        return slug is null ? ", in another campaign" : $", in campaign {slug}";
    }

    private static bool Mentions(ChangeRow row, string id) =>
        row.TargetId.Contains(id, StringComparison.Ordinal) ||
        string.Equals(row.EntityId, id, StringComparison.Ordinal) ||
        string.Equals(row.OtherEntityId, id, StringComparison.Ordinal) ||
        (row.OldValue?.Contains(id, StringComparison.Ordinal) ?? false) ||
        (row.NewValue?.Contains(id, StringComparison.Ordinal) ?? false);

    // "changes entity e:12 status", "creates fact f:3", "deletes a relation row": handles and field names only. With
    // subject, just the thing ("entity e:12"); a deleted entity's handle comes from its logged row.
    private static string Describe(SqliteConnection connection, SqliteTransaction transaction, ChangeRow row, bool subject = false)
    {
        var what = row.TargetTable switch
        {
            "entity" => SeqHandle(connection, transaction, "entity", "e", row) is { } e ? $"entity {e}" : "an entity",
            "fact" => SeqHandle(connection, transaction, "fact", "f", row) is { } f ? $"fact {f}" : "a fact",
            _ => Article(row.TargetTable.Replace('_', ' ')) + " row",
        };
        if (subject)
        {
            return what;
        }

        return row.Op switch
        {
            CampaignValues.ChangeOps.Create => $"creates {what}",
            CampaignValues.ChangeOps.Delete => $"deletes {what}",
            _ => $"changes {what} {row.FieldPath}",
        };
    }

    private static string? SeqHandle(SqliteConnection connection, SqliteTransaction transaction, string table, string prefix, ChangeRow row)
    {
        var seq = connection.ExecuteScalar<long?>($"SELECT seq FROM {table} WHERE id = @id", new { id = row.TargetId }, transaction);
        if (seq is null && (row.OldValue ?? row.NewValue) is { } snapshot && row.FieldPath is null &&
            JsonNode.Parse(snapshot) is JsonObject logged && logged["seq"] is JsonValue value && value.TryGetValue<long>(out var s))
        {
            seq = s;
        }

        return seq is { } found ? prefix + ":" + found.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static void Reverse(ChangeRecorder recorder, ChangeRow row)
    {
        var table = CampaignTables.Get(row.TargetTable);
        var key = table.ParseTargetId(row.TargetId);
        switch (row.Op)
        {
            case CampaignValues.ChangeOps.Create:
                recorder.Delete(table.Name, key, UndoAction);
                break;
            case CampaignValues.ChangeOps.Delete:
                recorder.Insert(table.Name, ChangeValues.RowFromJson(table, row.OldValue!), UndoAction);
                break;
            default:
                var current = recorder.Read(table.Name, key) ?? throw new DndInputException(
                    $"Cannot undo: a {table.Name.Replace('_', ' ')} row the batch changed no longer exists.");
                var (column, dataKey) = ChangeValues.FieldOf(table, row.FieldPath!);
                object? value;
                if (dataKey is null)
                {
                    value = ChangeValues.FromLogText(column, row.OldValue);
                }
                else
                {
                    var document = JsonNode.Parse((string)current[column.Name]!) as JsonObject ?? new JsonObject();
                    if (row.OldValue is null)
                    {
                        document.Remove(dataKey);
                    }
                    else
                    {
                        document[dataKey] = JsonNode.Parse(row.OldValue);
                    }

                    value = CampaignLogJson.Serialize(document);
                }

                recorder.Update(table.Name, key, new Dictionary<string, object?> { [column.Name] = value }, UndoAction);
                break;
        }
    }

    // SQLITE_CONSTRAINT: a UNIQUE, PRIMARY KEY or CHECK constraint refused a statement.
    private const int SqliteConstraint = 19;

    private static string Article(string noun) => ("aeiou".Contains(noun[0], StringComparison.Ordinal) ? "an " : "a ") + noun;

    private static string Echo(string text) => text.Length <= 40 ? text : text[..40] + "…";

    [GeneratedRegex("^[0-9a-f-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BatchIdPattern();
}
