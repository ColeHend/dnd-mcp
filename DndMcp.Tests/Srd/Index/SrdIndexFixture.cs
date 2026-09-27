using DndMcp.Repository.Srd.Index;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// One real srd.db, built from the vendored content the test project copies to <c>&lt;output&gt;/content/</c>, into a
/// private temp directory that is deleted afterwards. Shared by the tests of one class (<c>IClassFixture</c>), so a
/// class pays for one build (about half a second) instead of one per test.
///
/// <para>
/// Nothing here is a fixture dataset: the tests query the exact index the server builds from the exact bytes it ships,
/// so a count or a pairing pinned against this is pinned against production.
/// </para>
/// </summary>
public sealed class SrdIndexFixture : IDisposable
{
    public SrdIndexFixture()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "dnd-mcp-srd-tests-" + Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(DirectoryPath, "srd.db");
        Summary = SrdIndexBuilder.Build(ContentRoot, DatabasePath);
        Index = SrdIndex.TryOpen(DatabasePath, Summary.StalenessKey, out var reason)
                ?? throw new InvalidOperationException($"The freshly built index did not open: {reason}");
    }

    /// <summary>The shipped <c>content/</c> directory (5e-database plus the Rules Glossary).</summary>
    public static string ContentRoot { get; } = Path.Combine(AppContext.BaseDirectory, "content");

    public string DirectoryPath { get; }

    public string DatabasePath { get; }

    public SrdIndexBuildSummary Summary { get; }

    public SrdIndex Index { get; }

    /// <summary>A private read-only connection for assertions about the tables themselves. Dispose it.</summary>
    public SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>Every row of a one-column query, as strings (numbers invariant-formatted).</summary>
    public IReadOnlyList<string> Column(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }

    public long Scalar(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        Index.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
