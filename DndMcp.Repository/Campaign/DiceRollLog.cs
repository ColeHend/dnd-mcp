using Dapper;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>
/// The dice rolled at the table while a session is live (contract §3.11), and the dice the combat tracker rolls (Phase 7
/// D11: <see cref="DiceRollRow.EncounterId"/> set, logged inside the combat step's own transaction, with or without a live
/// session): plain inserts into dice_roll and reads per session or per encounter.
///
/// <para>
/// <b>Not in change_log, on purpose.</b> A roll is its own record, made once, and history's undo must never un-roll dice
/// (undoing the batch that recorded a death does not make the saving throws not have happened). So appends bypass
/// <see cref="ChangeRecorder"/>, and nothing here updates or deletes a roll. Whether to log at all (a live session, no
/// seed) is the caller's decision.
/// </para>
/// </summary>
public static class DiceRollLog
{
    /// <summary>
    /// Inserts one roll. <see cref="DiceRollRow.Seq"/> is ignored (assigned by SQLite); an empty <see cref="DiceRollRow.Id"/>
    /// gets a new id (read it back with <see cref="ForEncounter"/> or pass your own: a combat_log row's <c>roll_id</c> is it).
    /// </summary>
    /// <returns>The roll's seq.</returns>
    /// <exception cref="SqliteException">
    /// A CHECK failed (detail not a JSON object, outcome or secret not 0/1), or <see cref="DiceRollRow.EncounterId"/> names
    /// no encounter (a foreign key).
    /// </exception>
    public static long Append(SqliteConnection connection, SqliteTransaction? transaction, DiceRollRow roll)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(roll);
        CampaignDatabase.EnsureDapperConfigured();
        return connection.ExecuteScalar<long>(
            "INSERT INTO dice_roll(id, campaign_id, session_id, expression, label, total, outcome, detail, secret, at, encounter_id) " +
            "VALUES (@Id, @CampaignId, @SessionId, @Expression, @Label, @Total, @Outcome, @Detail, @Secret, @At, @EncounterId) " +
            "RETURNING seq",
            roll with { Id = string.IsNullOrEmpty(roll.Id) ? CampaignDatabase.NewId() : roll.Id },
            transaction);
    }

    /// <summary>
    /// The latest <paramref name="max"/> rolls of a session, oldest first. Secret rolls (the DM's behind the screen) only
    /// when <paramref name="includeSecret"/>, which is the author view's decision.
    /// </summary>
    public static IReadOnlyList<DiceRollRow> ForSession(
        SqliteConnection connection,
        string sessionId,
        bool includeSecret,
        int max,
        SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        CampaignDatabase.EnsureDapperConfigured();
        if (max <= 0)
        {
            return [];
        }

        var rows = connection.Query<DiceRollRow>(
            $"SELECT {DiceRollRow.Columns} FROM dice_roll WHERE session_id = @sessionId" +
            (includeSecret ? string.Empty : " AND secret = 0") +
            " ORDER BY seq DESC LIMIT @max",
            new { sessionId, max }, transaction).ToList();
        rows.Reverse();
        return rows;
    }

    /// <summary>
    /// The latest <paramref name="max"/> rolls the combat tracker made for one encounter, oldest first, whichever session
    /// (if any) they were filed under. Secret rolls only when <paramref name="includeSecret"/>, the author view's decision,
    /// exactly as <see cref="ForSession"/>.
    /// </summary>
    public static IReadOnlyList<DiceRollRow> ForEncounter(
        SqliteConnection connection,
        string encounterId,
        bool includeSecret,
        int max,
        SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(encounterId);
        CampaignDatabase.EnsureDapperConfigured();
        if (max <= 0)
        {
            return [];
        }

        var rows = connection.Query<DiceRollRow>(
            $"SELECT {DiceRollRow.Columns} FROM dice_roll WHERE encounter_id = @encounterId" +
            (includeSecret ? string.Empty : " AND secret = 0") +
            " ORDER BY seq DESC LIMIT @max",
            new { encounterId, max }, transaction).ToList();
        rows.Reverse();
        return rows;
    }
}
