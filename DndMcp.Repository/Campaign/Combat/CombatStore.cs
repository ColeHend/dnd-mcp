using System.Data;
using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// The unlogged combat tables (<c>encounter</c>, <c>combatant</c>, <c>combat_log</c>) to and from T's states (contract
/// §3, §4, §14.1): an encounter row and its combatants as one <see cref="EncounterState"/>, the rows a step changed written
/// back, and the step's combat_log rows appended with their <c>roll_id</c>. Plain SQL in the caller's transaction, never
/// the <see cref="ChangeRecorder"/>: HP ticks stay out of change_log (PLAN 7, D2), and only <c>end</c>'s write-back is
/// history.
///
/// <para>
/// <b>Reads happen in the step's own transaction</b> (CRIT Q10): two processes on one file each read inside BEGIN
/// IMMEDIATE, so the second step sees the first one's result (a stale <c>next {from}</c> is refused, never a lost update).
/// Readers that render (the board, the author state) call the same loader on a read connection.
/// </para>
/// <para>
/// <b>A row this version cannot read is a store problem</b>, not the caller's, and it is refused as one (a
/// <see cref="CampaignStoreUnavailableException"/>), never read as a default (the next step would write the default over
/// it) and never left to fail later as the generic error. What counts: a JSON value of the wrong type (T's
/// <see cref="CombatJson.FromColumns"/> refuses it), text in a number column (a table whose STRICT was lost), a total or
/// order key that is not a finite number (STRICT REAL takes Infinity, and T's ordering throws on it), a side, ruleset or
/// status outside its vocabulary and a negative round (values that bypassed a CHECK: the step's own UPDATE would hit the
/// CHECK again, as SQLITE_CONSTRAINT). The refusal names the column, the combatant and the fight (author output: the board
/// gets neither name) and the campaign, never the stored text, and it says how to get out: <c>end</c> with
/// <c>discard</c> reads no combatant (<see cref="CombatService.End"/>), and the encounter row itself is read leniently
/// (<see cref="Encounters"/>), so a damaged fight can always be ended that way.
/// </para>
/// </summary>
public static class CombatStore
{
    /// <summary>The longest encounter name (a campaign name's limit).</summary>
    public const int MaxEncounterName = CampaignLimits.MaxNameLength;

    // The combatant columns stored as INTEGER and as REAL (every other column is TEXT): what Load checks each value against.
    private static readonly HashSet<string> IntegerColumns = new(StringComparer.Ordinal)
    {
        CombatantColumns.InitBonus, CombatantColumns.Ac, CombatantColumns.MaxHp, CombatantColumns.MaxHpReduction, CombatantColumns.Hp,
        CombatantColumns.TempHp, CombatantColumns.DamageTaken, CombatantColumns.MakesDeathSaves, CombatantColumns.Exhaustion,
        CombatantColumns.ReactionUsed, CombatantColumns.Surprised, CombatantColumns.Hidden, CombatantColumns.Defeated,
        CombatantColumns.Dead, CombatantColumns.Removed,
    };

    private static readonly HashSet<string> RealColumns = new(StringComparer.Ordinal) { CombatantColumns.Initiative, CombatantColumns.OrderKey };

    /// <summary>The encounter row by id, or null.</summary>
    public static EncounterRow? Encounter(SqliteConnection connection, string id, SqliteTransaction? transaction = null) =>
        Encounters(connection, "WHERE id = @id", new { id }, transaction).FirstOrDefault();

    /// <summary>
    /// Encounter rows (<c>SELECT … FROM encounter</c> followed by <paramref name="tail"/>: the WHERE, ORDER BY and LIMIT),
    /// read LENIENTLY: a value this version cannot run (text in a number column, an unknown ruleset or status, a negative
    /// round) does not fail the read; the row comes back with its number columns as 0 and
    /// <see cref="EncounterRow.Unreadable"/> naming the column (class summary). Every encounter lookup goes through here.
    /// </summary>
    internal static IReadOnlyList<EncounterRow> Encounters(SqliteConnection connection, string tail, object parameters, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        CampaignDatabase.EnsureDapperConfigured();
        var rows = new List<EncounterRow>();
        using var reader = connection.ExecuteReader($"SELECT {EncounterRow.Columns} FROM encounter {tail}", parameters, transaction);
        while (reader.Read())
        {
            rows.Add(EncounterOf(reader));
        }

        return rows;
    }

    // One encounter row, leniently (Encounters): every problem named, the first one kept.
    private static EncounterRow EncounterOf(IDataRecord record)
    {
        string? problem = null;
        void Problem(string text) => problem ??= text;

        object? Value(string column)
        {
            var i = record.GetOrdinal(column);
            return record.IsDBNull(i) ? null : record.GetValue(i);
        }

        string? Text(string column)
        {
            switch (Value(column))
            {
                case null:
                    return null;
                case string s:
                    return s;
                case var other:
                    Problem($"encounter column {column} holds {StoredKind(other)}, not text.");
                    return Convert.ToString(other, CultureInfo.InvariantCulture);
            }
        }

        long Whole(string column)
        {
            switch (Value(column))
            {
                case long l:
                    return l;
                case null:
                    Problem($"encounter column {column} is empty.");
                    return 0;
                case var other:
                    Problem($"encounter column {column} holds {StoredKind(other)}, not a whole number.");
                    return 0;
            }
        }

        var ruleset = Text("ruleset") ?? string.Empty;
        var status = Text("status") ?? string.Empty;
        var round = Whole("round");
        var lair = Whole("lair");
        if (!CV.Rulesets.Editions.Values.Contains(ruleset, StringComparer.Ordinal))
        {
            Problem("encounter column ruleset is not an edition (2014 or 2024).");
        }

        if (!CV.EncounterStatuses.Set.Values.Contains(status, StringComparer.Ordinal))
        {
            Problem("encounter column status is not an encounter status.");
        }

        if (round < 0)
        {
            Problem("encounter column round is negative.");
            round = 0;
        }

        return new EncounterRow(
            Text("id") ?? string.Empty,
            Text("campaign_id") ?? string.Empty,
            Text("scene_id"),
            Text("session_id"),
            Text("name") ?? string.Empty,
            ruleset,
            status,
            round,
            Text("turn_combatant_id"),
            lair,
            Text("data") ?? "{}",
            Text("notes_md") ?? string.Empty,
            Text("outcome_md"),
            Text("writeback_batch_id"),
            Text("started_at"),
            Text("ended_at"),
            Text("created_at") ?? string.Empty,
            Text("updated_at") ?? string.Empty)
        {
            Unreadable = problem,
        };
    }

    /// <summary>
    /// The encounter as the tracker reads it: the row's fight state and every combatant (left ones too) in
    /// <c>order_key</c> order, with each linked entity's handle and subtype (not columns: addressing by handle, D5's
    /// routing, the calls reminders print) and the campaign's role (D17).
    /// </summary>
    /// <param name="authorView">
    /// False for a non-author reader (the party board): an unreadable row's refusal then names neither the combatant nor
    /// the fight (both author text).
    /// </param>
    /// <exception cref="CampaignStoreUnavailableException">The encounter row or a combatant row cannot be read (class summary).</exception>
    public static EncounterState Load(SqliteConnection connection, CampaignRow campaign, EncounterRow encounter, SqliteTransaction? transaction = null,
        bool authorView = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(encounter);
        if (encounter.Unreadable is { } damaged)
        {
            throw Unreadable(connection, campaign, encounter, null, new InvalidDataException(damaged), authorView);
        }

        var combatants = new List<CombatantState>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"SELECT {CampaignRows.Prefixed(CombatantRow.Columns, "c")}, e.kind || ':' || e.slug AS entity_handle, e.subtype AS entity_subtype " +
                "FROM combatant c LEFT JOIN entity e ON e.id = c.entity_id WHERE c.encounter_id = $encounter ORDER BY c.order_key, c.rowid";
            command.Parameters.AddWithValue("$encounter", encounter.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                string? handle = null;
                string? subtype = null;
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    switch (name)
                    {
                        case "entity_handle":
                            handle = value as string;
                            break;
                        case "entity_subtype":
                            subtype = value as string;
                            break;
                        default:
                            row[name] = value;
                            break;
                    }
                }

                try
                {
                    CheckCombatant(row);
                    combatants.Add(CombatJson.FromColumns(row, handle, subtype));
                }
                catch (InvalidDataException ex)
                {
                    throw Unreadable(connection, campaign, encounter, row.GetValueOrDefault(CombatantColumns.Name) as string, ex, authorView);
                }
            }
        }

        return new EncounterState
        {
            Id = encounter.Id,
            Name = encounter.Name,
            Ruleset = encounter.Ruleset,
            Status = encounter.Status,
            Round = checked((int)encounter.Round),
            TurnCombatantId = encounter.TurnCombatantId,
            Lair = encounter.Lair != 0,
            Combatants = combatants,
            PlayerCampaign = campaign.Role == CampaignValues.Roles.Player,
        };
    }

    /// <summary>
    /// The encounter's own state WITHOUT its combatants: what <c>end</c> needs to discard a fight (or end a planned or
    /// paused one), which reads no combatant, so a combatant row nobody can read never blocks the way out.
    /// </summary>
    public static EncounterState Unloaded(CampaignRow campaign, EncounterRow encounter)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(encounter);
        return new EncounterState
        {
            Id = encounter.Id,
            Name = encounter.Name,
            Ruleset = encounter.Ruleset,
            Status = encounter.Status,
            Round = (int)Math.Clamp(encounter.Round, 0, int.MaxValue),
            TurnCombatantId = encounter.TurnCombatantId,
            Lair = encounter.Lair != 0,
            Combatants = [],
            PlayerCampaign = campaign.Role == CampaignValues.Roles.Player,
        };
    }

    /// <summary>
    /// Whether a combatant of the campaign's ACTIVE encounter plays <paramref name="entityId"/> and has not left, or left
    /// but is sheet-seeded (it stays tied to its fight until the fight ends: its sheet actions are still routed to it, and
    /// <c>end</c> writes it back; F2, review CR07): one query, reading no combatant state (D5's router asks this before it
    /// loads the fight, so a fight with an unreadable row never blocks a character who is not in it).
    /// </summary>
    public static bool IsInActiveFight(SqliteConnection connection, string campaignId, string entityId, SqliteTransaction? transaction = null) =>
        connection.ExecuteScalar<long>(
            "SELECT count(*) FROM combatant c JOIN encounter e ON e.id = c.encounter_id " +
            "WHERE e.campaign_id = @campaignId AND e.status = @active AND c.entity_id = @entityId AND (c.removed = 0 OR c.sheet_snapshot IS NOT NULL)",
            new { campaignId, active = CV.EncounterStatuses.Active, entityId }, transaction) > 0;

    // The checks of a combatant row before T reads it (class summary): each value of the type its column stores, the
    // required names present, the side in the vocabulary, the totals finite. T's FromColumns takes a value of another CLR
    // type as a host bug (ArgumentException, the generic error); here it is what it is, a stored value nobody can read.
    private static void CheckCombatant(IReadOnlyDictionary<string, object?> row)
    {
        foreach (var (column, value) in row)
        {
            if (value is null)
            {
                continue;
            }

            if (RealColumns.Contains(column))
            {
                if (value is not (double or long))
                {
                    throw new InvalidDataException($"combatant column {column} holds {StoredKind(value)}, not a number.");
                }

                if (value is double d && !double.IsFinite(d))
                {
                    throw new InvalidDataException($"combatant column {column} is not a finite number.");
                }
            }
            else if (IntegerColumns.Contains(column))
            {
                if (value is not long)
                {
                    throw new InvalidDataException($"combatant column {column} holds {StoredKind(value)}, not a whole number.");
                }
            }
            else if (value is not string)
            {
                throw new InvalidDataException($"combatant column {column} holds {StoredKind(value)}, not text.");
            }
        }

        foreach (var column in new[] { CombatantColumns.Id, CombatantColumns.Name, CombatantColumns.Side })
        {
            if (row.GetValueOrDefault(column) is not string { Length: > 0 })
            {
                throw new InvalidDataException($"combatant column {column} is empty.");
            }
        }

        if (row.GetValueOrDefault(CombatantColumns.OrderKey) is null)
        {
            throw new InvalidDataException($"combatant column {CombatantColumns.OrderKey} is empty.");
        }

        if (!CV.CombatSides.Set.Values.Contains((string)row[CombatantColumns.Side]!, StringComparer.Ordinal))
        {
            throw new InvalidDataException($"combatant column {CombatantColumns.Side} is not a side (party, ally, enemy or neutral).");
        }
    }

    // What a stored value is, for a message (never the value itself).
    private static string StoredKind(object value) => value switch
    {
        string => "text",
        long => "a whole number",
        double => "a number",
        byte[] => "binary data",
        _ => "a value of another kind",
    };

    /// <summary>
    /// Writes a step's result: the new combatants inserted and the changed ones updated (every column, so the table's
    /// <c>hp &lt;= max_hp</c> CHECK sees a whole row), the encounter's round and turn, then the combat_log rows in order,
    /// each with the <c>dice_roll</c> id of the roll it cites. New combatants go in before the log rows that name them
    /// (<c>actor_id</c>/<c>target_id</c> are foreign keys).
    /// </summary>
    /// <param name="rollIds">The <c>dice_roll</c> id of each roll key the step logged.</param>
    public static void Save(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncounterState before,
        CombatStepResult result,
        IReadOnlyDictionary<string, string> rollIds,
        string at)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(result);
        WriteCombatants(connection, transaction, before, result.Next, result.ChangedCombatants, at);
        UpdateEncounter(connection, transaction, result.Next, at);
        foreach (var change in result.Changes)
        {
            AppendLog(connection, transaction, before.Id, change, change.RollKey is { } key && rollIds.TryGetValue(key, out var id) ? id : null, at);
        }
    }

    /// <summary>Inserts the combatants of <paramref name="ids"/> that <paramref name="before"/> lacks and updates the others.</summary>
    public static void WriteCombatants(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EncounterState before,
        EncounterState after,
        IEnumerable<string> ids,
        string at)
    {
        foreach (var id in ids)
        {
            var c = after.Find(id) ?? throw new ArgumentException($"The step changed a combatant it does not hold ({id}).", nameof(ids));
            var exists = before.Find(id) is not null;
            var columns = CombatJson.Columns(c with { CreatedAt = exists ? c.CreatedAt ?? at : at, UpdatedAt = at }, after.Id);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (exists)
            {
                var sets = columns.Keys.Where(k => k is not (CombatantColumns.Id or CombatantColumns.EncounterId or CombatantColumns.CreatedAt))
                    .Select(k => $"{k} = ${k}");
                command.CommandText = $"UPDATE combatant SET {string.Join(", ", sets)} WHERE id = $id AND encounter_id = $encounter_id";
            }
            else
            {
                command.CommandText = $"INSERT INTO combatant ({string.Join(", ", columns.Keys)}) VALUES ({string.Join(", ", columns.Keys.Select(k => "$" + k))})";
            }

            foreach (var (name, value) in columns)
            {
                command.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
            }

            command.ExecuteNonQuery();
        }
    }

    /// <summary>The encounter's round, turn pointer and updated_at from a state (status, lair and the rest are the lifecycle's).</summary>
    public static void UpdateEncounter(SqliteConnection connection, SqliteTransaction transaction, EncounterState state, string at) =>
        connection.Execute(
            "UPDATE encounter SET round = @round, turn_combatant_id = @turn, updated_at = @at WHERE id = @id",
            new { round = state.Round, turn = state.TurnCombatantId, at, id = state.Id },
            transaction);

    /// <summary>Appends one combat_log row (contract §6.9).</summary>
    /// <returns>The row's seq.</returns>
    public static long AppendLog(SqliteConnection connection, SqliteTransaction transaction, string encounterId, CombatChange change, string? rollId, string at)
    {
        ArgumentNullException.ThrowIfNull(change);
        return connection.ExecuteScalar<long>(
            "INSERT INTO combat_log (encounter_id, round, turn_combatant_id, actor_id, target_id, kind, amount, detail, roll_id, at) " +
            "VALUES (@encounterId, @round, @turn, @actor, @target, @kind, @amount, @detail, @rollId, @at) RETURNING seq",
            new
            {
                encounterId,
                round = change.Round,
                turn = change.TurnCombatantId,
                actor = change.ActorId,
                target = change.TargetId,
                kind = change.Kind,
                amount = change.Amount,
                detail = change.Detail,
                rollId,
                at,
            },
            transaction);
    }

    /// <summary>The encounter's last combat_log row (what <c>prev</c> reads: an exact revert only straight after a <c>next</c>), or null.</summary>
    public static CombatLogEntry? LastChange(SqliteConnection connection, string encounterId, SqliteTransaction? transaction = null)
    {
        var rows = StoredRows.Read(connection, "combat_log", () => connection.Query<(string Kind, string? Detail)>(
            "SELECT kind, detail FROM combat_log WHERE encounter_id = @encounterId ORDER BY seq DESC LIMIT 1",
            new { encounterId }, transaction).ToList());
        return rows.Count == 0 ? null : new CombatLogEntry(rows[0].Kind, rows[0].Detail);
    }

    /// <summary>Every combat_log row of an encounter, oldest first.</summary>
    public static IReadOnlyList<CombatLogRow> Log(SqliteConnection connection, string encounterId, SqliteTransaction? transaction = null)
    {
        CampaignDatabase.EnsureDapperConfigured();
        return StoredRows.Read(connection, "combat_log", () => connection.Query<CombatLogRow>(
            $"SELECT {CombatLogRow.Columns} FROM combat_log WHERE encounter_id = @encounterId ORDER BY seq", new { encounterId }, transaction).ToList());
    }

    /// <summary>
    /// The holdings of the given characters as the tracker consumes them (<c>heal {item}</c>, <c>use {item}</c>; contract
    /// §12.4b): id, holder, name, quantity.
    /// </summary>
    public static IReadOnlyList<CombatItem> Holdings(SqliteConnection connection, IEnumerable<string> holderIds, SqliteTransaction? transaction = null)
    {
        var ids = holderIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return StoredRows.Read(connection, "holding", () => connection.Query<(string Id, string HolderId, string Name, double Quantity)>(
                "SELECT id, holder_id, name, quantity FROM holding WHERE holder_id IN @ids ORDER BY rowid", new { ids }, transaction)
            .Select(h => new CombatItem(h.Id, h.HolderId, h.Name, h.Quantity))
            .ToList());
    }

    /// <summary>The holdings with these ids (missing ones are absent: a consumed holding that no longer exists is drift at <c>end</c>).</summary>
    public static IReadOnlyList<CombatItem> HoldingsById(SqliteConnection connection, IEnumerable<string> holdingIds, SqliteTransaction? transaction = null)
    {
        var ids = holdingIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return StoredRows.Read(connection, "holding", () => connection.Query<(string Id, string HolderId, string Name, double Quantity)>(
                "SELECT id, holder_id, name, quantity FROM holding WHERE id IN @ids ORDER BY rowid", new { ids }, transaction)
            .Select(h => new CombatItem(h.Id, h.HolderId, h.Name, h.Quantity))
            .ToList());
    }

    /// <summary>
    /// The refusal of a fight whose encounter row or one of whose combatant rows this version cannot read (class summary):
    /// which combatant of which fight (author output; the board's names neither), the campaign, the column and the file,
    /// and the way out: <c>end</c> with <c>discard</c> (which reads no combatant), or a backup. An ended fight has only the
    /// backup.
    /// </summary>
    /// <param name="combatant">The unreadable combatant's tracker name; null when the encounter row itself is the problem.</param>
    internal static CampaignStoreUnavailableException Unreadable(SqliteConnection connection, CampaignRow campaign, EncounterRow encounter, string? combatant,
        InvalidDataException ex, bool authorView = true)
    {
        var what = (authorView, combatant) switch
        {
            (false, null) => "The fight this board shows",
            (false, _) => "A combatant of the fight this board shows",
            (true, null) => $"The fight {EncounterResolver.Json(encounter.Name)}",
            (true, _) => $"Combatant {EncounterResolver.Json(combatant)} of the fight {EncounterResolver.Json(encounter.Name)}",
        };
        var discard = authorView
            ? $"combat {{\"action\": \"end\", \"encounter\": {EncounterResolver.Json(encounter.Name)}, \"discard\": true, \"campaign\": \"{campaign.Slug}\"}}"
            : $"combat {{\"action\": \"end\", \"discard\": true, \"campaign\": \"{campaign.Slug}\"}}";
        var way = encounter.Status == CV.EncounterStatuses.Ended
            ? "restore a backup from the backups directory beside it."
            : $"end the fight writing nothing back, which reads none of it, with {discard}; or restore a backup from the backups directory beside it.";
        return new CampaignStoreUnavailableException(
            $"{what} in campaign {campaign.Slug} cannot be read ({StoredRows.PathOf(connection)}): {ex.Message} Nothing was changed. The value " +
            $"was written by something other than this server, or the file is damaged; {way}", ex);
    }

    /// <summary>A whole number for messages.</summary>
    internal static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
