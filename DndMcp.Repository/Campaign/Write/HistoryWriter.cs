using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;
using SS = DndMcp.Domain.Campaign.CampaignValues.SessionStatuses;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>What an undo did (or, for a dry run, would do).</summary>
/// <param name="UndoneBatchId">The batch reversed (in full, even when a prefix was given).</param>
/// <param name="UndoBatchId">The new batch that reverses it (itself undoable: that is redo); null for a dry run.</param>
/// <param name="DryRun">Nothing was kept.</param>
/// <param name="WasRedo">The reversed batch was itself an undo.</param>
/// <param name="RowsReversed">change_log rows of the original batch that were reversed.</param>
/// <param name="Changes">What was reversed, newest first, one line each, by handle ("entity character:iron-guts status").</param>
/// <param name="Consequences">
/// What followed in the same undo batch: a secret whose derived status the reversed knowledge no longer supports
/// ("secret:x: seeded → partial"), recomputed as every batch that changes knowledge recomputes it.
/// </param>
/// <param name="Warnings">
/// Applied anyway, worth reading: the player-text check's <see cref="PlayerTextWarning"/>s for text the undo made
/// readable to a player-side view (a forbidden word, a true name the view does not use), as every write reports them.
/// </param>
public sealed record UndoWriteResult(
    string UndoneBatchId,
    string? UndoBatchId,
    bool DryRun,
    bool WasRedo,
    int RowsReversed,
    IReadOnlyList<string> Changes,
    IReadOnlyList<Consequence> Consequences,
    IReadOnlyList<WriteWarning> Warnings);

/// <summary>
/// <c>campaign_history undo</c> (contract §3.5): reverses exactly one batch as a new batch, through I's
/// <see cref="UndoEngine"/>, which refuses an unknown or already-undone batch and one that later batches build on.
///
/// <para>
/// <b>Secret statuses are re-derived in the undo batch</b> (§3.4: recomputed in every batch that changes knowledge of a
/// secret's facts). Reversing a batch puts back the status it wrote, but when the batch is not the latest one to touch
/// that secret's knowledge (undo T2 after T3 in the One Piece steps) the status it puts back describes knowledge that is
/// gone, or ignores a route a later batch completed. So after the reversal the derivation runs on the undo batch's own
/// recorder (<see cref="UndoEngine.Undo"/>'s <c>afterReversal</c>): its update is logged with the undo's rows, and a redo
/// reverses it with them.
/// </para>
/// <para>
/// <b>An undo runs the write-time player-text check</b> (<see cref="PlayerTextChecks"/>), dry run included, because
/// reversing rows can make text readable to the players exactly as a write can: undoing the batch that recorded the
/// party's known_as "the ancient sorcerer king" for Keras shows the party "Keras" while a gate forbids the word, and a
/// redo (an undo of an undo) re-applies whatever the original batch showed. Before the engine reverses anything
/// (<c>beforeReversal</c>), every entity and fact the batch's rows touch is noted, so the check takes its "before"
/// snapshot; after the reversal and the secret-status re-derivation it scans the text that is new for some view. Without
/// it, the one way of changing what the players read that went unchecked was the one a user reaches for to fix a
/// mistake, and the party's search found "Keras" with nothing said.
/// </para>
/// <para>
/// <b>Constraint refusals become messages.</b> The engine's conflict scan compares the same field of the same row, but
/// some rules span two columns or two rows: a clock's filled within its segments, an objective's progress within its
/// max, one live session per campaign, one row per register code. Reversing an earlier change past a later one can
/// break them (undo "segments 4 → 8" after a tick to 6), and SQLite then refuses the statement. That arrives as a raw
/// constraint error, which the host does not translate, so it is caught here, after
/// <see cref="CampaignDatabase.Write{T}"/> has rolled everything back, and explained from the log: which row, which
/// later batches, and what to do first.
/// </para>
/// <para>
/// <b>Never catch and commit.</b> A refusal can come mid-reversal (re-inserting a deleted row that collides with one
/// created since), after newer rows were already reversed on the transaction; the exception must leave
/// <see cref="CampaignDatabase.Write{T}"/>, which rolls everything back. So nothing is caught inside the transaction, and
/// the description of what was reversed is built inside it, before the commit.
/// </para>
/// <para>
/// The undo batch's session context follows the usual rule (explicit, else live, else none), so point-in-time replay
/// files the undo under the session in which it was made.
/// </para>
/// </summary>
public sealed class HistoryWriter
{
    // SQLITE_CONSTRAINT: a UNIQUE, CHECK or NOT NULL constraint refused a statement.
    private const int SqliteConstraint = 19;

    private readonly CampaignDatabase _database;

    public HistoryWriter(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Undoes <paramref name="batchId"/> (a full id or a unique prefix of at least 8 characters).</summary>
    /// <exception cref="DndInputException">
    /// No such batch here, already undone, later batches conflict, or putting the old values back would break a rule the
    /// data keeps (a clock, an objective, the one live session); nothing was written.
    /// </exception>
    public UndoWriteResult Undo(CampaignRow campaign, string batchId, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(context);
        var reason = WriteBatch.CheckReason(context.Reason);
        try
        {
            return _database.Write((connection, transaction) =>
            {
                var current = WriteBatch.LoadCampaign(connection, transaction, campaign.Id) ??
                              throw new DndInputException($"Campaign {campaign.Slug} no longer exists.");
                var session = WriteBatch.ResolveSessionContext(new HandleResolver(connection, current.Id, transaction), context.Session, current.Slug);
                var undoBatch = new BatchContext(current.Id, CampaignDatabase.NewId(), context.Actor, context.Tool ?? "campaign_history/undo",
                    session?.EntityId, reason);
                WriteBatch? batch = null;
                var result = UndoEngine.Undo(connection, transaction, current.Id, batchId, undoBatch, _database.Now(),
                    afterReversal: _ => Finish(batch!),
                    beforeReversal: (recorder, rows) =>
                    {
                        batch = WriteBatch.OnRecorder(_database, connection, transaction, current, session, recorder, context.DryRun, UndoEngine.UndoAction);
                        batch.PlayerText.Reversing(rows);
                    });
                var changes = result.Reversed.Select(row => Describe(connection, transaction, row)).ToList();
                return new UndoWriteResult(result.UndoneBatchId, context.DryRun ? null : result.UndoBatchId, context.DryRun, result.WasUndo,
                    result.Reversed.Count, changes, batch!.Consequences.ToList(), batch.Warnings.ToList());
            }, context.DryRun);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteConstraint)
        {
            // The transaction is already rolled back: Write disposes it before the exception reaches here.
            throw new DndInputException(ConstraintRefusal(campaign, batchId), ex);
        }
    }

    // What follows the reversal in the undo batch, as a normal batch's end does: the secret-status derivation (on the
    // undo's recorder, with the undo's action label), then the player-text check over the targets noted before the
    // reversal. The campaign is re-read, as the derivation always read it after the reversal: undoing a campaign_update
    // changes the row (never its role, slug or party, which only creation writes, so the check reads the same readers
    // either way), and undoing a campaign's own creation leaves no campaign to derive or check for.
    private static void Finish(WriteBatch batch)
    {
        if (WriteBatch.LoadCampaign(batch.Connection, batch.Transaction, batch.Campaign.Id) is null)
        {
            return;
        }

        batch.ReloadCampaign();
        batch.DerivationNeeded = true;
        batch.Finish();
    }

    // ---- constraint refusals ----------------------------------------------------------------------------------------

    // Read after the rollback, so the rows are as they were before the undo was tried. The first rule that explains the
    // refusal wins; the generic one lists every later batch that touched a row the batch touched.
    private string ConstraintRefusal(CampaignRow campaign, string batchIdOrPrefix)
    {
        var campaignId = campaign.Id;
        using var connection = _database.OpenRead();
        var batchId = UndoEngine.ResolveBatch(connection, null, campaignId, batchIdOrPrefix);
        var rows = connection.Query<ChangeRow>(
            $"SELECT {ChangeRow.Columns} FROM change_log WHERE campaign_id = @campaignId AND batch_id = @batchId ORDER BY seq",
            new { campaignId, batchId }).ToList();
        var lastSeq = rows.Count == 0 ? 0 : rows[^1].Seq;
        var later = connection.Query<ChangeRow>(
            $"SELECT {ChangeRow.Columns} FROM change_log WHERE campaign_id = @campaignId AND seq > @lastSeq ORDER BY seq",
            new { campaignId, lastSeq }).ToList();
        var problem = LiveSessionClash(connection, campaign, rows, later) ??
                      ClockClash(connection, rows, later) ??
                      ObjectiveClash(connection, rows, later) ??
                      CodeClash(connection, campaignId, rows, later);
        if (problem is not null)
        {
            return $"Batch {batchId} cannot be undone: {problem} Nothing was changed.";
        }

        var touched = rows.Select(r => (r.TargetTable, r.TargetId)).ToHashSet();
        var laterTouching = Batches(later.Where(r => touched.Contains((r.TargetTable, r.TargetId))));
        return $"Batch {batchId} cannot be undone: putting back what it changed would break a rule the campaign's data keeps " +
               "(a unique slug or code, a clock or objective within its range, one live session). " +
               (laterTouching.Length == 0
                   ? "Change what now stands in the way, then undo again."
                   : $"Later batches changed the same rows; undo these first, newest first, then undo {batchId} again:\n{laterTouching}") +
               " Nothing was changed.";
    }

    // Undoing the end (or any status change away from live) of a session makes it live again; the schema allows one. The
    // end call it prints names the campaign (WriteBatch.SessionCall): sent while another campaign is current, an end call
    // without it would end that campaign's live session instead.
    private static string? LiveSessionClash(SqliteConnection connection, CampaignRow campaign, IReadOnlyList<ChangeRow> rows, IReadOnlyList<ChangeRow> later)
    {
        var campaignId = campaign.Id;
        var back = FirstOldValues(rows, "session", "status")
            .FirstOrDefault(p => Equals(p.Value, SS.Live)).Target;
        if (back is null)
        {
            return null;
        }

        var live = connection.QueryFirstOrDefault<SessionRow>(
            $"SELECT {SessionRow.Columns} FROM session WHERE campaign_id = @campaignId AND status = @live", new { campaignId, live = SS.Live });
        if (live is null || live.EntityId == back)
        {
            return null;
        }

        var number = connection.ExecuteScalar<long>("SELECT number FROM session WHERE entity_id = @back", new { back });
        var started = Batches(later.Where(r => r.TargetTable == "session" && r.TargetId == live.EntityId));
        return $"it makes session {WriteBatch.Number(number)} live again, and session {WriteBatch.Number(live.Number)} is live now (one live " +
               $"session at a time). End session {WriteBatch.Number(live.Number)} first ({WriteBatch.SessionCall(campaign.Slug, "end")})" +
               (started.Length == 0 ? ", then undo again." : $", or undo the batches that started it, newest first:\n{started}\nThen undo again.");
    }

    // A clock's filled must stay within its segments: putting back fewer segments than are filled now (or more filled).
    private static string? ClockClash(SqliteConnection connection, IReadOnlyList<ChangeRow> rows, IReadOnlyList<ChangeRow> later)
    {
        foreach (var target in rows.Where(r => r.TargetTable == "clock" && r.Op == CampaignValues.ChangeOps.Update).Select(r => r.TargetId).Distinct())
        {
            var clock = connection.QueryFirstOrDefault<ClockRow>($"SELECT {ClockRow.Columns} FROM clock WHERE entity_id = @target", new { target });
            if (clock is null)
            {
                continue;
            }

            var old = FirstOldValues(rows, "clock", null).Where(p => p.Target == target).ToDictionary(p => p.Field, p => p.Value);
            var segments = old.TryGetValue("segments", out var s) && s is long os ? os : clock.Segments;
            var filled = old.TryGetValue("filled", out var f) && f is long of ? of : clock.Filled;
            if (filled <= segments)
            {
                continue;
            }

            var handle = SeqHandle(connection, target);
            var what = old.ContainsKey("segments")
                ? $"it changed clock {handle}'s segments from {WriteBatch.Number(segments)} to {LastNewValue(rows, target, "segments")}, and the clock has " +
                  $"{WriteBatch.Number(filled)} filled now; {WriteBatch.Number(segments)} segments cannot hold {WriteBatch.Number(filled)}."
                : $"it changed clock {handle}'s filled from {WriteBatch.Number(filled)} to {LastNewValue(rows, target, "filled")}, and the clock has " +
                  $"{WriteBatch.Number(segments)} segments now; {WriteBatch.Number(filled)} filled does not fit.";
            var laterBatches = Batches(later.Where(r => r.TargetTable == "clock" && r.TargetId == target));
            return what + (laterBatches.Length == 0
                ? " Change the clock so the old value fits, then undo again."
                : $" Undo the later batches that changed the clock first, newest first (or change it so the old value fits):\n{laterBatches}\nThen undo again.");
        }

        return null;
    }

    // An objective's progress must stay within its progress_max.
    private static string? ObjectiveClash(SqliteConnection connection, IReadOnlyList<ChangeRow> rows, IReadOnlyList<ChangeRow> later)
    {
        foreach (var target in rows.Where(r => r.TargetTable == "objective" && r.Op == CampaignValues.ChangeOps.Update).Select(r => r.TargetId).Distinct())
        {
            var objective = connection.QueryFirstOrDefault<ObjectiveRow>($"SELECT {ObjectiveRow.Columns} FROM objective WHERE id = @target", new { target });
            if (objective is null)
            {
                continue;
            }

            var old = FirstOldValues(rows, "objective", null).Where(p => p.Target == target).ToDictionary(p => p.Field, p => p.Value);
            var progress = old.TryGetValue("progress", out var p) ? p as long? : objective.Progress;
            var max = old.TryGetValue("progress_max", out var m) ? m as long? : objective.ProgressMax;
            if (progress is null || max is null || progress <= max)
            {
                continue;
            }

            var position = connection.Query<string>("SELECT id FROM objective WHERE quest_id = @quest ORDER BY ordinal, id", new { quest = objective.QuestId })
                .ToList().IndexOf(target) + 1;
            var where = $"objective {WriteBatch.Number(position)} of {SeqHandle(connection, objective.QuestId)}";
            var laterBatches = Batches(later.Where(r => r.TargetTable == "objective" && r.TargetId == target));
            return $"it would put {where} back to progress {WriteBatch.Number(progress.Value)} of at most {WriteBatch.Number(max.Value)}, which does not fit." +
                   (laterBatches.Length == 0
                       ? " Change the objective so the old value fits, then undo again."
                       : $" Undo the later batches that changed the objective first, newest first (or change it so the old value fits):\n{laterBatches}\nThen undo again.");
        }

        return null;
    }

    // A register code is never reused (§3.1), but one the batch took away (an undone auto_code) can be handed out again
    // before the batch is redone: putting it back would give two rows one code.
    private static string? CodeClash(SqliteConnection connection, string campaignId, IReadOnlyList<ChangeRow> rows, IReadOnlyList<ChangeRow> later)
    {
        foreach (var (table, prefix) in new[] { ("entity", "e:"), ("fact", "f:") })
        {
            foreach (var (target, _, value) in FirstOldValues(rows, table, "code"))
            {
                if (value is not string code)
                {
                    continue;
                }

                var holder = CodeHolders(connection, campaignId, code).FirstOrDefault(h => h.Id != target);
                if (holder.Id is null)
                {
                    continue;
                }

                var self = connection.QueryFirstOrDefault<long?>($"SELECT seq FROM {table} WHERE id = @target", new { target }) is { } seq
                    ? prefix + WriteBatch.Number(seq)
                    : "a row";
                var laterBatches = Batches(later.Where(r => r.TargetId == holder.Id));
                return $"it gives {self} its code {code} back, and {holder.Handle} holds {code} now (a register code is never shared or changed)." +
                       (laterBatches.Length == 0
                           ? " Nothing can free that code, so this batch cannot be redone."
                           : $" Undo the batches that gave {holder.Handle} that code first, newest first:\n{laterBatches}\nThen undo again.");
            }
        }

        return null;
    }

    private static IEnumerable<(string? Id, string Handle)> CodeHolders(SqliteConnection connection, string campaignId, string code) =>
        connection.Query<(string Id, long Seq)>("SELECT id, seq FROM entity WHERE campaign_id = @campaignId AND code = @code", new { campaignId, code })
            .Select(r => ((string?)r.Id, "e:" + WriteBatch.Number(r.Seq)))
            .Concat(connection.Query<(string Id, long Seq)>("SELECT id, seq FROM fact WHERE campaign_id = @campaignId AND code = @code", new { campaignId, code })
                .Select(r => ((string?)r.Id, "f:" + WriteBatch.Number(r.Seq))));

    // What each updated field of each row goes back to: undo writes old values newest first, so the value a field ends
    // with is the old value of the batch's FIRST change to it.
    private static IEnumerable<(string Target, string Field, object? Value)> FirstOldValues(IReadOnlyList<ChangeRow> rows, string table, string? field) =>
        rows.Where(r => r.TargetTable == table && r.Op == CampaignValues.ChangeOps.Update && r.FieldPath is not null &&
                        !r.FieldPath.Contains('.', StringComparison.Ordinal) && (field is null || r.FieldPath == field))
            .GroupBy(r => (r.TargetId, r.FieldPath!))
            .Select(g => g.OrderBy(r => r.Seq).First())
            .Select(r => (r.TargetId, r.FieldPath!, ChangeValues.Decode(table, r.FieldPath!, r.OldValue)));

    // The value the batch left a field at (its last change to it), as logged.
    private static string LastNewValue(IReadOnlyList<ChangeRow> rows, string target, string field) =>
        rows.Where(r => r.TargetId == target && r.FieldPath == field).OrderBy(r => r.Seq).LastOrDefault()?.NewValue ?? "(none)";

    // "- <batch id> (<at>, <tool>)" per batch, newest first, at most 5.
    private static string Batches(IEnumerable<ChangeRow> rows)
    {
        var batches = rows.GroupBy(r => r.BatchId)
            .Select(g => (Id: g.Key, Last: g.OrderBy(r => r.Seq).Last()))
            .OrderByDescending(b => b.Last.Seq)
            .ToList();
        var lines = batches.Take(5).Select(b => $"- {b.Id} ({b.Last.At}{(b.Last.Tool is null ? string.Empty : ", " + b.Last.Tool)})");
        var more = batches.Count > 5 ? $"\n- … and {WriteBatch.Number(batches.Count - 5)} more" : string.Empty;
        return string.Join("\n", lines) + more;
    }

    // e:<n>: refusals name things by handle only (the message reaches whoever drives the client).
    private static string SeqHandle(SqliteConnection connection, string entityId) =>
        connection.QueryFirstOrDefault<long?>("SELECT seq FROM entity WHERE id = @entityId", new { entityId }) is { } seq
            ? "e:" + seq.ToString(CultureInfo.InvariantCulture)
            : "an entity";

    // ---- descriptions -----------------------------------------------------------------------------------------------

    // "entity character:iron-guts status (update)", "knowledge row (create)": handles and field names only, never text.
    private static string Describe(SqliteConnection connection, SqliteTransaction transaction, ChangeRow row)
    {
        var what = row.TargetTable switch
        {
            "entity" => "entity " + (EntityHandle(connection, transaction, row) ?? "(removed)"),
            "fact" => "fact " + (FactHandle(connection, transaction, row) ?? "(removed)"),
            _ => row.TargetTable.Replace('_', ' ') + " row",
        };
        var field = row.FieldPath is null ? string.Empty : " " + row.FieldPath;
        return $"{what}{field} ({row.Op} reversed)";
    }

    private static string? EntityHandle(SqliteConnection connection, SqliteTransaction transaction, ChangeRow row)
    {
        var live = connection.QueryFirstOrDefault<string?>(
            "SELECT kind || ':' || slug FROM entity WHERE id = @id", new { id = row.TargetId }, transaction);
        if (live is not null)
        {
            return live;
        }

        return Snapshot(row) is { } snapshot && snapshot["kind"]?.GetValue<string>() is { } kind && snapshot["slug"]?.GetValue<string>() is { } slug
            ? kind + ":" + slug
            : null;
    }

    private static string? FactHandle(SqliteConnection connection, SqliteTransaction transaction, ChangeRow row)
    {
        var seq = connection.QueryFirstOrDefault<long?>("SELECT seq FROM fact WHERE id = @id", new { id = row.TargetId }, transaction)
                  ?? (Snapshot(row)?["seq"] is JsonValue value && value.TryGetValue<long>(out var logged) ? logged : null);
        return seq is { } found ? "f:" + found.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static JsonObject? Snapshot(ChangeRow row) =>
        row.FieldPath is null && (row.NewValue ?? row.OldValue) is { } text ? JsonNode.Parse(text) as JsonObject : null;
}
