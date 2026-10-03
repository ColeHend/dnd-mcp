using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>
/// Point-in-time reads (<c>as_of_session</c>, contract §3.5): a row as it stood at the end of session n is its current
/// state with every change_log row attributed to a session numbered above n reversed, newest first.
///
/// <para>
/// <b>Rows with no session are timeless</b> and never reversed: prep between sessions and world-building are written
/// with no session context, and reversing them would make "as of session 3" lose the backstory entered last week.
/// Likewise a change whose session no longer exists (its number cannot be known) is treated as timeless. A row whose
/// create is reversed did not exist then (null); a row deleted after n comes back from its logged snapshot.
/// </para>
/// <para>
/// Replay covers the loggable tables only. Knowledge "as of n" is decided by the rows' learned / valid-until sessions
/// (the verdict rules), not by this replay; FTS text is always today's text (a documented limitation: an as-of search
/// finds rows by their current words).
/// </para>
/// </summary>
public static class ChangeReplay
{
    /// <summary>
    /// One row as of the end of session <paramref name="sessionNumber"/>, or null when it did not exist then (or never
    /// existed). <paramref name="targetId"/> is the row's change_log target id (<see cref="CampaignTable.TargetId"/>):
    /// the id, or a JSON array for a composite key.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? RowAsOf(
        SqliteConnection connection,
        string table,
        string targetId,
        int sessionNumber,
        SqliteTransaction? transaction = null) =>
        RowsAsOf(connection, table, [targetId], sessionNumber, transaction)[targetId];

    /// <summary>
    /// Many rows of one table as of the end of session <paramref name="sessionNumber"/>: every requested target id is a
    /// key of the result (null value: did not exist then).
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>?> RowsAsOf(
        SqliteConnection connection,
        string table,
        IEnumerable<string> targetIds,
        int sessionNumber,
        SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var meta = CampaignTables.Get(table);
        var ids = targetIds.Distinct(StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>?>(StringComparer.Ordinal);
        var changes = LaterChanges(connection, meta, ids, sessionNumber, transaction);
        foreach (var id in ids)
        {
            var current = Current(connection, meta, id, transaction);
            result[id] = changes.TryGetValue(id, out var rows) ? Rewind(meta, current, rows) : current;
        }

        return result;
    }

    /// <summary>
    /// Applies change_log rows in reverse to a row's state (null = absent); <paramref name="newestFirst"/> must already be
    /// in descending seq order.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?>? Rewind(
        CampaignTable meta,
        IReadOnlyDictionary<string, object?>? current,
        IEnumerable<ReplayChange> newestFirst)
    {
        Dictionary<string, object?>? state = current is null ? null : new Dictionary<string, object?>(current, StringComparer.Ordinal);
        foreach (var change in newestFirst)
        {
            switch (change.Op)
            {
                case CampaignValues.ChangeOps.Create:
                    state = null;
                    break;
                case CampaignValues.ChangeOps.Delete:
                    state = change.OldValue is null ? null : ChangeValues.RowFromJson(meta, change.OldValue);
                    break;
                default:
                    if (state is null || change.FieldPath is null)
                    {
                        break;
                    }

                    var (column, key) = ChangeValues.FieldOf(meta, change.FieldPath);
                    if (key is null)
                    {
                        state[column.Name] = ChangeValues.FromLogText(column, change.OldValue);
                    }
                    else
                    {
                        var document = (state[column.Name] is string text ? JsonNode.Parse(text) : null) as JsonObject ?? new JsonObject();
                        if (change.OldValue is null)
                        {
                            document.Remove(key);
                        }
                        else
                        {
                            document[key] = JsonNode.Parse(change.OldValue);
                        }

                        state[column.Name] = CampaignLogJson.Serialize(document);
                    }

                    break;
            }
        }

        return state;
    }

    private static IReadOnlyDictionary<string, object?>? Current(SqliteConnection connection, CampaignTable meta, string targetId, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {meta.ColumnList} FROM {meta.Name} WHERE {meta.KeyPredicate}";
        ChangeRecorder.BindKey(command, meta.ParseTargetId(targetId));
        return ChangeRecorder.ReadRow(meta, command);
    }

    // change_log rows of these targets attributed to a session numbered above n, newest first, grouped by target.
    private static Dictionary<string, List<ReplayChange>> LaterChanges(
        SqliteConnection connection,
        CampaignTable meta,
        IReadOnlyList<string> ids,
        int sessionNumber,
        SqliteTransaction? transaction)
    {
        var byTarget = new Dictionary<string, List<ReplayChange>>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(500))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var names = chunk.Select((_, i) => "$t" + i).ToList();
            command.CommandText =
                "SELECT cl.target_id, cl.op, cl.field_path, cl.old_value FROM change_log cl " +
                "JOIN session s ON s.entity_id = cl.session_id " +
                $"WHERE cl.target_table = $table AND cl.target_id IN ({string.Join(", ", names)}) AND s.number > $n " +
                "ORDER BY cl.seq DESC";
            command.Parameters.AddWithValue("$table", meta.Name);
            command.Parameters.AddWithValue("$n", sessionNumber);
            for (var i = 0; i < chunk.Length; i++)
            {
                command.Parameters.AddWithValue(names[i], chunk[i]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var target = reader.GetString(0);
                if (!byTarget.TryGetValue(target, out var list))
                {
                    byTarget[target] = list = [];
                }

                list.Add(new ReplayChange(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return byTarget;
    }

    /// <summary>What replay needs of one change_log row.</summary>
    internal sealed record ReplayChange(string Op, string? FieldPath, string? OldValue);
}
