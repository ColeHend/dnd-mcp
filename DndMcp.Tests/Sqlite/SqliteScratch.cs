using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// A private directory under the system temp path for file-backed test databases, deleted on Dispose.
///
/// <para>
/// File-backed because the behaviours under test only exist on files: WAL cannot be enabled on
/// <c>:memory:</c>, two connections cannot share an in-memory database without a shared-cache URI (which
/// changes the locking under test), and VACUUM INTO and raw file copies need real files.
/// </para>
/// <para>
/// Every connection string sets Pooling=False. A pooled connection keeps its file handle, and therefore the
/// WAL, open after Dispose. That would stop the directory being deleted on Windows, and it would hide the
/// "last connection closes, WAL is checkpointed" behaviour that the backup tests depend on NOT happening
/// until they say so.
/// </para>
/// </summary>
public sealed class SqliteScratch : IDisposable
{
    public SqliteScratch()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "dnd-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string PathOf(string fileName) => Path.Combine(DirectoryPath, fileName);

    public string ConnectionString(string fileName, int defaultTimeoutSeconds = 5) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = PathOf(fileName),
            DefaultTimeout = defaultTimeoutSeconds,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();

    public SqliteConnection Open(string fileName, int defaultTimeoutSeconds = 5)
    {
        var connection = new SqliteConnection(ConnectionString(fileName, defaultTimeoutSeconds));
        connection.Open();
        return connection;
    }

    /// <summary>
    /// An unpooled private in-memory database. Unpooled so no test can ever be handed a connection that
    /// another test already created tables in.
    /// </summary>
    public static SqliteConnection OpenInMemory(string extraConnectionString = "")
    {
        var connection = new SqliteConnection("Data Source=:memory:;Pooling=False;" + extraConnectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
