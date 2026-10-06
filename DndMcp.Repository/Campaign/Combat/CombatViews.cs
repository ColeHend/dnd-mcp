using System.Globalization;
using Dapper;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// The AUTHOR's view of an encounter (<see cref="EncounterView"/>): the heading's facts, the initiative table rows, the
/// session it is filed under and the write-back's state (D6). Built from the tracker's state as the step left it, so the
/// table a step prints is the state it wrote.
/// </summary>
internal static class CombatViews
{
    /// <summary>The author view of <paramref name="state"/> (with <paramref name="row"/>'s lifecycle columns).</summary>
    public static EncounterView Encounter(SqliteConnection connection, SqliteTransaction? transaction, EncounterRow row, EncounterState state)
    {
        var session = row.SessionId is null
            ? null
            : connection.QueryFirstOrDefault<long?>("SELECT number FROM session WHERE entity_id = @id", new { id = row.SessionId }, transaction);
        var writeback = WritebackStatus(connection, row.WritebackBatchId, transaction);
        return new EncounterView(
            row.Id,
            row.Name,
            row.Ruleset,
            state.Status,
            state.Round,
            state.Lair,
            state.TurnCombatantId,
            state.TurnHolder?.Name,
            session is { } n ? checked((int)n) : null,
            row.OutcomeMd,
            row.WritebackBatchId,
            writeback,
            Rows(state),
            state);
    }

    /// <summary>
    /// The table rows: the turn order (left and dead ones keep their place only while they hold an initiative and have
    /// not left), then the combatants with no initiative yet, then the ones that left.
    /// </summary>
    public static IReadOnlyList<CombatantView> Rows(EncounterState state)
    {
        var rows = new List<CombatantView>();
        var position = 0;
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in state.Order.Where(c => !c.Removed))
        {
            rows.Add(Row(state, c, ++position));
            placed.Add(c.Id);
        }

        foreach (var c in state.Combatants.Where(c => !c.Removed && !placed.Contains(c.Id)))
        {
            rows.Add(Row(state, c, null));
        }

        foreach (var c in state.Combatants.Where(c => c.Removed))
        {
            rows.Add(Row(state, c, null));
        }

        return rows;
    }

    /// <summary>
    /// What the author's view says about the write-back batch (D6): null without one; "undone" when an undo of it exists
    /// that has not itself been undone; "applied again" after a redo. The chain of <c>undo_of</c> is followed batch by
    /// batch (an undone batch cannot be undone twice, so it is a line).
    /// </summary>
    public static string? WritebackStatus(SqliteConnection connection, string? batchId, SqliteTransaction? transaction = null) =>
        batchId is null ? null : WritebackChain(connection, batchId, transaction).Status;

    /// <summary>
    /// The write-back batch's undo chain (<see cref="WritebackStatus"/>): its status and the LAST batch of the chain, the
    /// only one that can still be undone (the write-back itself when it stands, the undo after an undo, the redo after a
    /// redo).
    /// </summary>
    public static (string Status, string Last) WritebackChain(SqliteConnection connection, string batchId, SqliteTransaction? transaction = null)
    {
        var depth = 0;
        var current = batchId;
        while (depth < 1_000 && connection.QueryFirstOrDefault<string?>(
                   "SELECT batch_id FROM change_log WHERE undo_of = @current ORDER BY seq LIMIT 1", new { current }, transaction) is { } undo)
        {
            depth++;
            current = undo;
        }

        return (depth == 0 ? WritebackStatuses.Applied : depth % 2 == 1 ? WritebackStatuses.Undone : WritebackStatuses.AppliedAgain, current);
    }

    private static CombatantView Row(EncounterState state, CombatantState c, int? position)
    {
        var edition = state.Ruleset;
        var conditions = c.Conditions.Select(x => CombatContext.Describe(state, x)).ToList();
        var concentration = c.Concentration is { } held
            ? held.Level is { } level ? $"{held.Spell} (level {level.ToString(CultureInfo.InvariantCulture)})" : held.Spell
            : null;
        var deathSaves = c.MakesDeathSaves && c.HpKnown && (c.Hp == 0 || c.Dead) ? c.DeathSaves : null;
        return new CombatantView(
            position,
            c.Id,
            c.Name,
            c.EntityHandle,
            c.Side,
            c.Initiative,
            c.Hp,
            c.EffectiveMaxHp(edition),
            c.TempHp,
            c.DamageTaken,
            c.DisplayedAc,
            c.Exhaustion,
            conditions,
            concentration,
            deathSaves,
            c.Legendary,
            c.Id == state.TurnCombatantId,
            c.Defeated,
            c.Dead,
            c.Dying,
            c.Hidden,
            c.Removed,
            c.Surprised,
            c.IsSheetSeeded);
    }
}
