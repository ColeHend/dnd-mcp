using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>
/// Class fixture: the production server with one campaign (<see cref="Slug"/>, a DM campaign with one character), made
/// through the tools, whose campaigns.db then had the <c>entity</c> table's root page scrambled. The header and the schema
/// are intact, so the open and the migration check pass, and the damage shows only when a statement reads that table:
/// SQLITE_CORRUPT from a read, not from the open.
///
/// <para>
/// Why it exists: the store maps the SQLite failures a person can fix only where it opens and writes, so a read that met a
/// damaged page escaped as a raw SqliteException, which the SDK turns into "An error occurred invoking 'campaign_get'."
/// with nothing to tell the user (the same server's campaign_write named the damaged file). The host's filters map it now
/// (<c>CampaignService.TryMapStoreFailure</c>); this is the file that shows whether they do, for tools, prompts and
/// resources alike. Reading leaves the file as it is, so one fixture serves the class.
/// </para>
/// </summary>
public sealed class DamagedCampaignsDatabaseServer : IAsyncLifetime
{
    /// <summary>The campaign's slug.</summary>
    public const string Slug = "probe";

    /// <summary>Its one character, on the damaged page.</summary>
    public const string Character = "character:old-hero";

    public McpServerHarness Harness { get; } = new();

    /// <summary>campaigns.db as the server resolves it.</summary>
    public string DatabasePath => Path.Combine(Harness.DataDirectory, "campaigns.db");

    public async Task InitializeAsync()
    {
        await Harness.InitializeAsync();
        Harness.SuccessText(await Harness.CallToolJsonAsync("campaign",
            $$"""{"action": "create", "name": "Probe", "role": "dm", "ruleset": "2014", "slug": "{{Slug}}"}"""));
        Harness.SuccessText(await Harness.CallToolJsonAsync("campaign_write",
            """{"ops": [{"op": "upsert", "kind": "character", "name": "Old Hero", "summary": "a hero", "visibility": "party"}]}"""));
        ScrambleRootPage(DatabasePath, "entity");
    }

    /// <summary>
    /// The message the store gives for a damaged campaigns.db, asked of the repository (as ToolErrorTests asks Domain for a
    /// dice message): tests then pin that it reaches the client unchanged, while its wording stays the repository's.
    /// </summary>
    public string StoreMessage()
    {
        Assert.True(CampaignDatabase.TryMapUnavailable(new SqliteException("SQLite Error 11: 'database disk image is malformed'.", 11), DatabasePath,
            out var unavailable));
        return unavailable.Message;
    }

    public Task DisposeAsync() => Harness.DisposeAsync();

    /// <summary>
    /// Scrambles the root page of <paramref name="table"/> in the campaigns.db at <paramref name="path"/>, past the page
    /// header's first eight bytes, leaving the file header and the schema intact: any statement that reads that table fails
    /// with SQLITE_CORRUPT, and nothing else does. Checkpoints the WAL into the file first, so the page scrambled is the one
    /// every later read meets.
    /// </summary>
    public static void ScrambleRootPage(string path, string table)
    {
        long root;
        long pageSize;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            root = Scalar(connection, $"SELECT rootpage FROM sqlite_master WHERE name = '{table}'");
            pageSize = Scalar(connection, "PRAGMA page_size");
        }

        var garbage = new byte[pageSize - 8];
        new Random(7).NextBytes(garbage);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        file.Seek(((root - 1) * pageSize) + 8, SeekOrigin.Begin);
        file.Write(garbage);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
