using Dapper;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// The rows of one table that belong to some entities or facts, as they stood at the end of a session: I's
/// <see cref="ChangeReplay"/> for every row that exists now AND every row a later session's change_log touched.
///
/// <para>
/// <b>Why both sets:</b> replaying only today's rows answers "what did these rows say then" but misses rows that existed
/// then and were deleted since (an alias removed in session 9 must still show as of session 5), while rows created since
/// replay to "did not exist". The later-touched rows are found through change_log's <c>entity_id</c> /
/// <c>other_entity_id</c> (the entities a row is about, contract §3.5), or, for fact-keyed tables whose log rows carry
/// no fact column, through the fact id inside <c>target_id</c> (ids are UUIDs, so a substring test is exact).
/// </para>
/// </summary>
internal static class AsOfRows
{
    /// <summary>Rows of <paramref name="table"/> about <paramref name="entityIds"/> as of session <paramref name="session"/>.</summary>
    /// <param name="currentTargetIds">The target ids of the matching rows that exist now.</param>
    public static List<T> ForEntities<T>(
        SqliteConnection connection,
        CampaignTable table,
        IEnumerable<string> currentTargetIds,
        IReadOnlyCollection<string> entityIds,
        int session)
        where T : class
    {
        var ids = new HashSet<string>(currentTargetIds, StringComparer.Ordinal);
        foreach (var chunk in entityIds.Chunk(400))
        {
            ids.UnionWith(connection.Query<string>(
                "SELECT DISTINCT cl.target_id FROM change_log cl JOIN session s ON s.entity_id = cl.session_id " +
                "WHERE cl.target_table = @table AND s.number > @session AND (cl.entity_id IN @ids OR cl.other_entity_id IN @ids)",
                new { table = table.Name, session, ids = chunk }));
        }

        return Replay<T>(connection, table, ids, session);
    }

    /// <summary>Rows of a fact-keyed table (<paramref name="table"/>) of <paramref name="factIds"/> as of session <paramref name="session"/>.</summary>
    public static List<T> ForFacts<T>(
        SqliteConnection connection,
        CampaignTable table,
        IEnumerable<string> currentTargetIds,
        IReadOnlyCollection<string> factIds,
        int session)
        where T : class
    {
        var ids = new HashSet<string>(currentTargetIds, StringComparer.Ordinal);
        foreach (var factId in factIds)
        {
            ids.UnionWith(connection.Query<string>(
                "SELECT DISTINCT cl.target_id FROM change_log cl JOIN session s ON s.entity_id = cl.session_id " +
                "WHERE cl.target_table = @table AND s.number > @session AND instr(cl.target_id, @factId) > 0",
                new { table = table.Name, session, factId }));
        }

        return Replay<T>(connection, table, ids, session);
    }

    private static List<T> Replay<T>(SqliteConnection connection, CampaignTable table, IEnumerable<string> ids, int session)
        where T : class =>
        ChangeReplay.RowsAsOf(connection, table.Name, ids, session).Values
            .Where(v => v is not null)
            .Select(v => CampaignRows.FromValues<T>(v!))
            .ToList();
}
