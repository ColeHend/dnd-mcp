using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Characters;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// <c>character_sheet</c> rows to and from P's <see cref="CharacterSheet"/> model (contract §13.1): read inside a write
/// transaction or on a read connection, as of a session through <see cref="ChangeReplay"/>, and written only through a
/// batch's <see cref="ChangeRecorder"/> (a whole row on create, then plain columns with <c>Update</c> and the four per-key
/// columns with <c>PatchObject</c>, so each changed slot level, resource, die size and ability is its own change_log row
/// and undo conflicts are per key, contract §3).
///
/// <para>
/// <b>A stored value of the wrong type is refused, never defaulted</b> (P's <see cref="SheetJson.FromColumns"/>): read as
/// a default, the next write would store the default over it. Such a row (written by hand, by another program, or
/// damaged) blocks every read and write of that sheet, so it is reported as what it is, a store problem a person fixes,
/// with a <see cref="CampaignStoreUnavailableException"/> naming whose sheet it is, the campaign, the column (never its
/// stored text) and the file, the way the host shows every other unreadable-store message. It is not a
/// <c>DndInputException</c>: nothing the caller typed is wrong, and no tool call repairs it (restore a backup, or repair
/// the column with SQL).
/// </para>
/// <para>
/// <b>The column lists agree by construction:</b> the catalogue (<see cref="CampaignTables.CharacterSheet"/>), the row
/// record (<see cref="CharacterSheetRow"/>) and P's <see cref="SheetColumns.All"/> name the same 43 columns in the same
/// order, and a test pins that. A row is read as the catalogue's column map, which is exactly what
/// <see cref="SheetJson.FromColumns"/> takes (text, integers as long, NULL).
/// </para>
/// </summary>
public static class CharacterSheetStore
{
    /// <summary>The change_log action of a sheet written by <c>campaign_character</c> (the tool label names the action).</summary>
    public const string Action = "sheet";

    /// <summary>The sheet of a character, or null when it has none.</summary>
    /// <param name="authorView">
    /// False for a non-author reader (a party line): an unreadable sheet's refusal then names the character by its
    /// <c>e:&lt;n&gt;</c> handle alone, never by the author's <c>character:slug</c> (<see cref="Unreadable"/>).
    /// </param>
    /// <exception cref="CampaignStoreUnavailableException">The stored row cannot be read (a value of the wrong type).</exception>
    public static CharacterSheet? Read(SqliteConnection connection, string entityId, SqliteTransaction? transaction = null, bool authorView = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        var table = CampaignTables.CharacterSheet;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {table.ColumnList} FROM {table.Name} WHERE {table.KeyPredicate}";
        ChangeRecorder.BindKey(command, [entityId]);
        var row = ChangeRecorder.ReadRow(table, command);
        return row is null ? null : FromColumns(connection, row, entityId, transaction, authorView);
    }

    /// <summary>The sheets of several characters (entity id → sheet; characters with none are absent).</summary>
    /// <exception cref="CampaignStoreUnavailableException">A stored row cannot be read.</exception>
    public static IReadOnlyDictionary<string, CharacterSheet> ReadMany(SqliteConnection connection, IEnumerable<string> entityIds, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entityIds);
        var sheets = new Dictionary<string, CharacterSheet>(StringComparer.Ordinal);
        foreach (var id in entityIds.Distinct(StringComparer.Ordinal))
        {
            if (Read(connection, id, transaction) is { } sheet)
            {
                sheets[id] = sheet;
            }
        }

        return sheets;
    }

    /// <summary>
    /// The sheet as it stood at the end of session <paramref name="session"/> (<see cref="ChangeReplay"/>: changes filed
    /// under later sessions reversed; changes with no session are timeless), or null when it did not exist then.
    /// </summary>
    /// <param name="authorView">As <see cref="Read"/>'s.</param>
    /// <exception cref="CampaignStoreUnavailableException">The replayed row cannot be read.</exception>
    public static CharacterSheet? AsOf(SqliteConnection connection, string entityId, int session, SqliteTransaction? transaction = null, bool authorView = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        var row = ChangeReplay.RowAsOf(connection, CampaignTables.CharacterSheet.Name, entityId, session, transaction);
        return row is null ? null : FromColumns(connection, row, entityId, transaction, authorView);
    }

    /// <summary>A row read through Dapper (<see cref="CharacterSheetRow"/>) as the model, for a reader that has one.</summary>
    /// <exception cref="InvalidDataException">A stored value of the wrong type (P's refusal, naming the column).</exception>
    public static CharacterSheet FromRow(CharacterSheetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SheetJson.FromColumns(Columns(row));
    }

    /// <summary>A <see cref="CharacterSheetRow"/>'s columns by name, as SQLite stores them (the shape <see cref="SheetJson.FromColumns"/> takes).</summary>
    public static IReadOnlyDictionary<string, object?> Columns(CharacterSheetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SheetColumns.EntityId] = row.EntityId,
            [SheetColumns.Player] = row.Player,
            [SheetColumns.Ruleset] = row.Ruleset,
            [SheetColumns.Species] = row.Species,
            [SheetColumns.Lineage] = row.Lineage,
            [SheetColumns.Background] = row.Background,
            [SheetColumns.Size] = row.Size,
            [SheetColumns.Classes] = row.Classes,
            [SheetColumns.Level] = row.Level,
            [SheetColumns.Xp] = row.Xp,
            [SheetColumns.Abilities] = row.Abilities,
            [SheetColumns.Saves] = row.Saves,
            [SheetColumns.Skills] = row.Skills,
            [SheetColumns.Ac] = row.Ac,
            [SheetColumns.MaxHp] = row.MaxHp,
            [SheetColumns.MaxHpReduction] = row.MaxHpReduction,
            [SheetColumns.Hp] = row.Hp,
            [SheetColumns.TempHp] = row.TempHp,
            [SheetColumns.Speed] = row.Speed,
            [SheetColumns.Movement] = row.Movement,
            [SheetColumns.Senses] = row.Senses,
            [SheetColumns.InitiativeBonus] = row.InitiativeBonus,
            [SheetColumns.PassivePerception] = row.PassivePerception,
            [SheetColumns.SpellSaveDc] = row.SpellSaveDc,
            [SheetColumns.SpellAttack] = row.SpellAttack,
            [SheetColumns.Defenses] = row.Defenses,
            [SheetColumns.HitDice] = row.HitDice,
            [SheetColumns.SpellSlots] = row.SpellSlots,
            [SheetColumns.Resources] = row.Resources,
            [SheetColumns.Conditions] = row.Conditions,
            [SheetColumns.Concentration] = row.Concentration,
            [SheetColumns.DeathSaves] = row.DeathSaves,
            [SheetColumns.Exhaustion] = row.Exhaustion,
            [SheetColumns.Inspiration] = row.Inspiration,
            [SheetColumns.Feats] = row.Feats,
            [SheetColumns.Features] = row.Features,
            [SheetColumns.Spells] = row.Spells,
            [SheetColumns.Languages] = row.Languages,
            [SheetColumns.SimProfile] = row.SimProfile,
            [SheetColumns.NotesMd] = row.NotesMd,
            [SheetColumns.SheetSource] = row.SheetSource,
            [SheetColumns.CreatedAt] = row.CreatedAt,
            [SheetColumns.UpdatedAt] = row.UpdatedAt,
        };
    }

    /// <summary>
    /// Inserts a new sheet through the batch's recorder (one <c>create</c> change_log row with the whole row); the
    /// recorder stamps created_at and updated_at.
    /// </summary>
    public static void Insert(ChangeRecorder recorder, CharacterSheet sheet, string action = Action)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(sheet);
        var row = SheetJson.ToColumns(sheet)
            .Where(p => p.Key is not (SheetColumns.CreatedAt or SheetColumns.UpdatedAt))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        recorder.Insert(CampaignTables.CharacterSheet.Name, row, action);
    }

    /// <summary>
    /// Writes a <see cref="SheetDiff"/> through the batch's recorder: the plain columns with one <c>Update</c> (a row per
    /// changed column), then each per-key column's merge patch with <c>PatchObject</c> (a row per changed key). A patch is
    /// applied to the column as it stands, so a key this diff does not name keeps the value another call gave it.
    /// </summary>
    /// <returns>True when anything was written.</returns>
    public static bool Apply(ChangeRecorder recorder, string entityId, SheetDiff diff, string action = Action)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(diff);
        var table = CampaignTables.CharacterSheet.Name;
        var changed = false;
        if (diff.Columns.Count > 0)
        {
            changed |= recorder.Update(table, [entityId], diff.Columns, action);
        }

        foreach (var (column, patch) in diff.Patches)
        {
            changed |= recorder.PatchObject(table, [entityId], column, (JsonObject)patch.DeepClone(), action);
        }

        return changed;
    }

    /// <summary>
    /// The refusal of a stored sheet that cannot be read (class summary), for every reader and writer of it: whose sheet and
    /// in which campaign (so the author knows which row to repair: one bad sheet refuses every party-wide read, the roster,
    /// the party list, <c>add_party</c>, <c>party: "campaign"</c>), the column and the file. The character is named by its
    /// author handle and its <c>e:&lt;n&gt;</c> for the author, by <c>e:&lt;n&gt;</c> alone for any other view (a handle the
    /// author typed may say more than the view knows).
    /// </summary>
    internal static CampaignStoreUnavailableException Unreadable(SqliteConnection connection, string entityId, SqliteTransaction? transaction,
        InvalidDataException ex, bool authorView = true)
    {
        var who = connection.QueryFirstOrDefault<(string Kind, string Slug, long Seq, string Campaign)?>(
            "SELECT e.kind, e.slug, e.seq, c.slug FROM entity e JOIN campaign c ON c.id = e.campaign_id WHERE e.id = @entityId",
            new { entityId }, transaction);
        var whose = who is not { } w
            ? "A character sheet"
            : authorView
                ? $"The character sheet of {w.Kind}:{w.Slug} (e:{N(w.Seq)}) in campaign {w.Campaign}"
                : $"The character sheet of e:{N(w.Seq)} in campaign {w.Campaign}";
        return new CampaignStoreUnavailableException(
            $"{whose} cannot be read ({StoredRows.PathOf(connection)}): {ex.Message} Nothing was changed. The value was written by " +
            "something other than this server, or the file is damaged; restore a backup from the backups directory beside it, " +
            "or repair that column.", ex);
    }

    private static CharacterSheet FromColumns(SqliteConnection connection, IReadOnlyDictionary<string, object?> row, string entityId,
        SqliteTransaction? transaction, bool authorView)
    {
        try
        {
            // A REAL or a BLOB in a column of a table whose STRICT was lost: P takes another CLR type as a host bug (the
            // generic error); it is a stored value nobody can read.
            foreach (var (column, value) in row)
            {
                if (value is double or byte[])
                {
                    throw new InvalidDataException(
                        $"The character sheet's {column} column holds {(value is double ? "a fractional number" : "binary data")}, not text or a whole number.");
                }
            }

            return SheetJson.FromColumns(row);
        }
        catch (InvalidDataException ex)
        {
            throw Unreadable(connection, entityId, transaction, ex, authorView);
        }
    }

    private static string N(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
