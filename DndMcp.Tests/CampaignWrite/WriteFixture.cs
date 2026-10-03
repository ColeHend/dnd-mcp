using System.Text.Json;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// A campaigns.db of its own (<see cref="CampaignTestDb"/>) with every write-path service over it, and the reads the
/// write tests assert with: rows by handle, change_log rows, counts and a whole-database dump.
///
/// <para>
/// Tests build their data through the services under test (not raw SQL), so every fixture is also a test that the
/// write path can express it; later stages (the scenario tests) can do the same.
/// </para>
/// </summary>
public sealed class WriteFixture : IDisposable
{
    public WriteFixture(ManualTimeProvider? time = null)
    {
        Db = new CampaignTestDb(time);
        Store = new CampaignStore(Db.Database);
        Writer = new CampaignWriter(Db.Database);
        Knowledge = new KnowledgeWriter(Db.Database);
        Sessions = new SessionWriter(Db.Database, Db.Logger);
        History = new HistoryWriter(Db.Database);
        Dice = new DiceLogWriter(Db.Database, Db.Logger);
    }

    public CampaignTestDb Db { get; }

    public CampaignStore Store { get; }

    public CampaignWriter Writer { get; }

    public KnowledgeWriter Knowledge { get; }

    public SessionWriter Sessions { get; }

    public HistoryWriter History { get; }

    public DiceLogWriter Dice { get; }

    /// <summary>A new campaign (DM, 2024 unless said otherwise).</summary>
    public CampaignRow Campaign(string name = "Test Campaign", string role = CampaignValues.Roles.Dm, string ruleset = CampaignValues.Rulesets.R2024,
        string? myCharacter = null, string? settingsJson = null, string? partyName = null) =>
        Store.Create(name, role, ruleset, myCharacter: myCharacter, partyName: partyName,
            settings: settingsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson)).Campaign;

    /// <summary>Applies ops with the default context (live session, no reason, real run).</summary>
    public WriteResult Apply(CampaignRow campaign, params CampaignOpSpec[] ops) => Writer.Apply(campaign, ops, WriteContext.Default);

    /// <summary>Applies ops with a context.</summary>
    public WriteResult Apply(CampaignRow campaign, WriteContext context, params CampaignOpSpec[] ops) => Writer.Apply(campaign, ops, context);

    /// <summary>A session played in the past, by number.</summary>
    public SessionWriteResult Played(CampaignRow campaign, int number, params (string Character, bool Present)[] attendance) =>
        Sessions.RecordPast(campaign, number, attendance: attendance.Length == 0
            ? null
            : attendance.Select(a => (AttendanceSpec?)new AttendanceSpec { Character = a.Character, Present = a.Present }).ToList());

    public SqliteConnection Open() => Db.Open();

    public EntityRow Entity(CampaignRow campaign, string handle, bool includeDeleted = false)
    {
        using var connection = Open();
        return new HandleResolver(connection, campaign.Id).TryEntity(CampaignHandle.Parse(handle), includeDeleted) ??
               throw new InvalidOperationException($"No entity {handle}.");
    }

    public EntityRow? TryEntity(CampaignRow campaign, string handle, bool includeDeleted = false)
    {
        using var connection = Open();
        return new HandleResolver(connection, campaign.Id).TryEntity(CampaignHandle.Parse(handle), includeDeleted);
    }

    public FactRow Fact(CampaignRow campaign, string handle, bool includeDeleted = false)
    {
        using var connection = Open();
        return new HandleResolver(connection, campaign.Id).TryFact(CampaignHandle.Parse(handle), includeDeleted) ??
               throw new InvalidOperationException($"No fact {handle}.");
    }

    public CampaignRow Reload(CampaignRow campaign)
    {
        using var connection = Open();
        return connection.QuerySingle<CampaignRow>($"SELECT {CampaignRow.Columns} FROM campaign WHERE id = @id", new { id = campaign.Id });
    }

    public IReadOnlyList<ChangeRow> Log(string? batchId = null)
    {
        using var connection = Open();
        return connection.Query<ChangeRow>(
            $"SELECT {ChangeRow.Columns} FROM change_log" + (batchId is null ? string.Empty : " WHERE batch_id = @batchId") + " ORDER BY seq",
            new { batchId }).ToList();
    }

    public long Count(string sql, object? args = null)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(sql, args);
    }

    public T Scalar<T>(string sql, object? args = null)
    {
        using var connection = Open();
        return connection.ExecuteScalar<T>(sql, args)!;
    }

    public IReadOnlyList<T> Query<T>(string sql, object? args = null)
    {
        using var connection = Open();
        return connection.Query<T>(sql, args).ToList();
    }

    /// <summary>
    /// Every loggable table's rows (every column but updated_at, which undo re-stamps), sorted, as text: two dumps are
    /// equal when the data is, so "undo puts everything back" is one assertion.
    /// </summary>
    public string Dump()
    {
        using var connection = Open();
        var lines = new List<string>();
        foreach (var table in CampaignTables.All)
        {
            var columns = table.Columns.Where(c => c.Name != "updated_at").Select(c => c.Name).ToList();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {string.Join(", ", columns)} FROM {table.Name}";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                rows.Add(table.Name + ": " + string.Join(" | ", Enumerable.Range(0, columns.Count).Select(i =>
                    reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
            }

            rows.Sort(StringComparer.Ordinal);
            lines.AddRange(rows);
        }

        return string.Join("\n", lines);
    }

    public void Dispose() => Db.Dispose();
}

/// <summary>Short builders for op specs, so a test reads as the batch it sends.</summary>
public static class Op
{
    public static CampaignOpSpec Upsert(string kind, string name, string? subtype = null, string? visibility = null, string? status = null) =>
        new() { Op = "upsert", Kind = kind, Name = name, Subtype = subtype, Visibility = visibility, Status = status };

    public static CampaignOpSpec Fact(string statement, string? canonStatus = null, string? visibility = null, string? code = null) =>
        new() { Op = "fact", Statement = statement, CanonStatus = canonStatus, Visibility = visibility, Code = code };

    public static CampaignOpSpec Link(string from, string rel, string to) => new() { Op = "link", From = from, Rel = rel, To = to };

    public static KnowerSpec Knower(string who, string? state = null, string? knownAs = null, int? session = null) =>
        new() { Who = who, State = state, KnownAs = knownAs, Session = session };

    public static Dictionary<string, JsonElement> Data(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
