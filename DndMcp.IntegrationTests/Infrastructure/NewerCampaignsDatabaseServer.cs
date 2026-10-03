using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>
/// Class fixture: the production server whose campaigns.db was written by a newer dnd-mcp (its <c>user_version</c> is
/// <see cref="SchemaVersion"/>, past every migration this build has), so every campaign call, prompt and resource read
/// that opens it fails with <see cref="CampaignStoreUnavailableException"/>.
///
/// <para>
/// Why it exists: that exception is the one campaigns.db failure a user can act on (update dnd-mcp, free the lock, fix
/// the permissions), and only the host's filters turn it into a message the model sees: the call-tool filter for the
/// tools, a get-prompt and a read-resource filter for the rest. Drop any of the three and the model gets the SDK's bare
/// "An error occurred" instead, with nothing to tell the user. A newer schema is the case a test can make exactly and
/// that changes nothing on disk (the migrator refuses before it writes), so the file stays the same for every test in a
/// class.
/// </para>
/// </summary>
public sealed class NewerCampaignsDatabaseServer : IAsyncLifetime
{
    /// <summary>The schema version the file claims: newer than any this build knows.</summary>
    public const int SchemaVersion = 99;

    public McpServerHarness Harness { get; } = new();

    /// <summary>campaigns.db as the server resolves it: <c>campaigns.db</c> in the harness's own data directory.</summary>
    public string DatabasePath => Path.Combine(Harness.DataDirectory, "campaigns.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Harness.DataDirectory);
        using (var connection = Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {SchemaVersion}";
            command.ExecuteNonQuery();
        }

        await Harness.InitializeAsync();
    }

    /// <summary>The file's <c>user_version</c> now: still <see cref="SchemaVersion"/> unless something migrated or replaced it.</summary>
    public long UserVersion()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>
    /// The message the store itself gives for this file, asked of a <see cref="CampaignDatabase"/> of the test's own (as
    /// ToolErrorTests asks Domain for a dice message): tests then pin that the message reaches the client unchanged,
    /// while the wording stays the repository's, which its own tests pin.
    /// </summary>
    public string StoreMessage()
    {
        using var database = new CampaignDatabase(DatabasePath, TimeProvider.System);
        return Assert.Throws<CampaignStoreUnavailableException>(database.EnsureReady).Message;
    }

    public Task DisposeAsync() => Harness.DisposeAsync();

    // Pooling off (the house rule): a pooled handle would keep the file open after the test and block the cleanup.
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
