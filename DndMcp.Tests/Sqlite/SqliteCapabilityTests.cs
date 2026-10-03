using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: the SQLite library this build ships (Microsoft.Data.Sqlite 10.0.12 → SQLitePCLRaw
/// bundle_e_sqlite3) passes every capability probe the campaign and SRD schemas depend on, and
/// <see cref="SqliteCapabilities.EnsureSupported"/> names every gap when one does not. A package bump that
/// swaps the bundle or drops FTS5/JSON fails here, not at first query in someone's session.
/// </summary>
public sealed class SqliteCapabilityTests
{
    private readonly ITestOutputHelper _output;

    public SqliteCapabilityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// PLAN.md D2 (the schema needs FTS5, triggers, STRICT, partial and expression indexes, json_patch) and
    /// Phase 0 Spike B. Every flag must be true for the bundled library; a false one means the bundle changed.
    /// </summary>
    [Fact]
    public void Probe_BundledLibrary_ReportsEveryRequiredFeature()
    {
        var capabilities = SqliteCapabilities.Probe();

        _output.WriteLine(capabilities.ToString());
        Assert.Empty(capabilities.MissingFeatures());
        Assert.True(capabilities.Fts5);
        Assert.True(capabilities.Fts5PorterUnicode61Tokenizer);
        Assert.True(capabilities.Fts5PrefixIndexes);
        Assert.True(capabilities.Fts5Bm25);
        Assert.True(capabilities.JsonFunctions);
        Assert.True(capabilities.StrictTables);
        Assert.True(capabilities.WithoutRowidTables);
        Assert.True(capabilities.PartialIndexes);
        Assert.True(capabilities.JsonExpressionIndexes);
        Assert.True(capabilities.TriggerRaiseAbort);
        Assert.True(capabilities.WalJournalMode);
        Assert.True(capabilities.VacuumInto);
    }

    /// <summary>
    /// PLAN.md D2: STRICT tables (3.37.0) are the newest feature the schema uses. The actual version is
    /// written to the test output (3.53.3 when this was written) so a bundle bump shows up in the log.
    /// </summary>
    [Fact]
    public void Probe_BundledLibrary_IsAtLeastMinimumVersion()
    {
        var capabilities = SqliteCapabilities.Probe();

        _output.WriteLine($"sqlite_version() = {capabilities.Version} ({capabilities.NativeLibrary})");
        Assert.True(capabilities.MeetsMinimumVersion);
        Assert.True(
            Version.Parse(capabilities.Version) >= Version.Parse(SqliteCapabilities.MinimumVersion),
            $"SQLite {capabilities.Version} is older than {SqliteCapabilities.MinimumVersion}.");
    }

    /// <summary>
    /// PLAN.md Packages table ("the bundled e_sqlite3 includes FTS5 and JSON1") and the comment in
    /// DndMcp.Repository.csproj. The probe results above are only meaningful for the library the published
    /// binary will load, so pin that it is the bundled e_sqlite3 and not a system libsqlite3.
    /// </summary>
    [Fact]
    public void Probe_BundledLibrary_IsE_Sqlite3()
    {
        var capabilities = SqliteCapabilities.Probe();

        Assert.Equal("e_sqlite3", capabilities.NativeLibrary);
    }

    /// <summary>
    /// The host probes once per launch, and Claude launches one server per session. A probe that leaked
    /// its scratch directory would leave one in temp for every session ever started.
    /// </summary>
    [Fact]
    public void Probe_ScratchParent_LeavesNothingBehind()
    {
        using var scratch = new SqliteScratch();

        SqliteCapabilities.Probe(scratch.DirectoryPath);

        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.DirectoryPath));
    }

    /// <summary>
    /// R10 (contract fix FI8): a server killed mid-probe (SIGKILL skips the finally) leaves its scratch directory where it
    /// probed, which for the host is the data directory beside campaigns.db. The next probe deletes such directories once
    /// a minute old. A younger one may be another server's probe running now (two sessions often start together), and a
    /// directory not named exactly like a probe's (prefix plus 32 lower-case hex digits) is not the probe's to delete.
    /// </summary>
    [Fact]
    public void Probe_ScratchDirectoryLeftByAKilledProbe_IsDeletedButYoungerAndForeignOnesStay()
    {
        using var scratch = new SqliteScratch();
        var stale = Directory.CreateDirectory(scratch.PathOf("dnd-mcp-sqlite-probe-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(stale, "probe.db"), "left by a killed probe");
        var running = Directory.CreateDirectory(scratch.PathOf("dnd-mcp-sqlite-probe-" + Guid.NewGuid().ToString("N"))).FullName;
        var foreign = new[]
        {
            "dnd-mcp-sqlite-probe-notes",
            "dnd-mcp-sqlite-probe-" + Guid.NewGuid().ToString("N").ToUpperInvariant(),
            "my-dnd-mcp-sqlite-probe-" + Guid.NewGuid().ToString("N"),
        }.Select(name => Directory.CreateDirectory(scratch.PathOf(name)).FullName).ToList();
        foreach (var directory in foreign.Append(stale))
        {
            Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow.AddMinutes(-2));
        }

        SqliteCapabilities.Probe(scratch.DirectoryPath);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(running));
        Assert.All(foreign, directory => Assert.True(Directory.Exists(directory), directory));
    }

    /// <summary>
    /// The probe must not mutate a database it did not create: checking WAL or VACUUM INTO against
    /// campaigns.db would change the real file. Pin that it works entirely inside its own scratch directory by
    /// giving it a parent that holds a database and checking that database is untouched.
    /// </summary>
    [Fact]
    public void Probe_ExistingDatabaseInScratchParent_IsNotTouched()
    {
        using var scratch = new SqliteScratch();
        using (var existing = new SqliteConnection($"Data Source={scratch.PathOf("campaigns.db")};Pooling=False"))
        {
            existing.Open();
            using var command = existing.CreateCommand();
            command.CommandText = "CREATE TABLE marker(v TEXT)";
            command.ExecuteNonQuery();
        }

        var before = File.ReadAllBytes(scratch.PathOf("campaigns.db"));

        SqliteCapabilities.Probe(scratch.DirectoryPath);

        Assert.Equal(before, File.ReadAllBytes(scratch.PathOf("campaigns.db")));
        Assert.Equal(new[] { "campaigns.db" }, Directory.EnumerateFileSystemEntries(scratch.DirectoryPath).Select(Path.GetFileName));
    }

    /// <summary>
    /// Phase 0 exit criterion "Spike B green". The startup gate must pass on the library that actually
    /// ships, or the server would refuse to start everywhere.
    /// </summary>
    [Fact]
    public void EnsureSupported_BundledLibrary_DoesNotThrow()
    {
        var capabilities = SqliteCapabilities.Probe();

        var exception = Record.Exception(capabilities.EnsureSupported);

        Assert.Null(exception);
    }

    /// <summary>
    /// A startup failure must say which feature is missing in words someone can act on, plus the minimum
    /// version, so the fix is obvious from the log line alone. One case per flag: a flag that
    /// <see cref="SqliteCapabilities.MissingFeatures"/> forgot to check would silently pass EnsureSupported.
    /// </summary>
    [Theory]
    [InlineData(nameof(SqliteCapabilities.MeetsMinimumVersion), "3.37.0 or newer (found 3.36.0)")]
    [InlineData(nameof(SqliteCapabilities.Fts5), "FTS5 full-text search")]
    [InlineData(nameof(SqliteCapabilities.Fts5PorterUnicode61Tokenizer), "porter unicode61 remove_diacritics 2")]
    [InlineData(nameof(SqliteCapabilities.Fts5PrefixIndexes), "prefix indexes")]
    [InlineData(nameof(SqliteCapabilities.Fts5Bm25), "bm25()")]
    [InlineData(nameof(SqliteCapabilities.JsonFunctions), "json_patch")]
    [InlineData(nameof(SqliteCapabilities.StrictTables), "STRICT tables")]
    [InlineData(nameof(SqliteCapabilities.WithoutRowidTables), "WITHOUT ROWID")]
    [InlineData(nameof(SqliteCapabilities.PartialIndexes), "partial indexes")]
    [InlineData(nameof(SqliteCapabilities.JsonExpressionIndexes), "expression indexes on json_extract")]
    [InlineData(nameof(SqliteCapabilities.TriggerRaiseAbort), "RAISE(ABORT)")]
    [InlineData(nameof(SqliteCapabilities.WalJournalMode), "WAL journal mode")]
    [InlineData(nameof(SqliteCapabilities.VacuumInto), "VACUUM INTO")]
    public void EnsureSupported_OneFeatureMissing_NamesItAndTheMinimumVersion(string flag, string expectedName)
    {
        var capabilities = WithFlagOff(AllSupported(), flag);

        var exception = Assert.Throws<InvalidOperationException>(capabilities.EnsureSupported);

        Assert.Single(capabilities.MissingFeatures());
        Assert.Contains(expectedName, exception.Message);
        Assert.Contains($"SQLite {SqliteCapabilities.MinimumVersion} or newer", exception.Message);
    }

    /// <summary>
    /// One restart should fix everything, so all gaps go in one message. The flag list comes from
    /// reflection, so a flag added to the record without a WithFlagOff case (and so without a
    /// MissingFeatures check to test) fails here.
    /// </summary>
    [Fact]
    public void EnsureSupported_EverythingMissing_NamesEveryFeatureInOneMessage()
    {
        var flags = typeof(SqliteCapabilities).GetProperties().Where(p => p.PropertyType == typeof(bool)).Select(p => p.Name).ToList();
        var capabilities = flags.Aggregate(AllSupported(), WithFlagOff);

        var exception = Assert.Throws<InvalidOperationException>(capabilities.EnsureSupported);

        Assert.Equal(flags.Count, capabilities.MissingFeatures().Count);
        foreach (var feature in capabilities.MissingFeatures())
        {
            Assert.Contains(feature, exception.Message);
        }
    }

    private static SqliteCapabilities AllSupported() =>
        new(
            Version: "3.36.0",
            NativeLibrary: "e_sqlite3",
            MeetsMinimumVersion: true,
            Fts5: true,
            Fts5PorterUnicode61Tokenizer: true,
            Fts5PrefixIndexes: true,
            Fts5Bm25: true,
            JsonFunctions: true,
            StrictTables: true,
            WithoutRowidTables: true,
            PartialIndexes: true,
            JsonExpressionIndexes: true,
            TriggerRaiseAbort: true,
            WalJournalMode: true,
            VacuumInto: true);

    private static SqliteCapabilities WithFlagOff(SqliteCapabilities capabilities, string flag) =>
        flag switch
        {
            nameof(SqliteCapabilities.MeetsMinimumVersion) => capabilities with { MeetsMinimumVersion = false },
            nameof(SqliteCapabilities.Fts5) => capabilities with { Fts5 = false },
            nameof(SqliteCapabilities.Fts5PorterUnicode61Tokenizer) => capabilities with { Fts5PorterUnicode61Tokenizer = false },
            nameof(SqliteCapabilities.Fts5PrefixIndexes) => capabilities with { Fts5PrefixIndexes = false },
            nameof(SqliteCapabilities.Fts5Bm25) => capabilities with { Fts5Bm25 = false },
            nameof(SqliteCapabilities.JsonFunctions) => capabilities with { JsonFunctions = false },
            nameof(SqliteCapabilities.StrictTables) => capabilities with { StrictTables = false },
            nameof(SqliteCapabilities.WithoutRowidTables) => capabilities with { WithoutRowidTables = false },
            nameof(SqliteCapabilities.PartialIndexes) => capabilities with { PartialIndexes = false },
            nameof(SqliteCapabilities.JsonExpressionIndexes) => capabilities with { JsonExpressionIndexes = false },
            nameof(SqliteCapabilities.TriggerRaiseAbort) => capabilities with { TriggerRaiseAbort = false },
            nameof(SqliteCapabilities.WalJournalMode) => capabilities with { WalJournalMode = false },
            nameof(SqliteCapabilities.VacuumInto) => capabilities with { VacuumInto = false },
            _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Add the new flag to WithFlagOff and to the theory above."),
        };
}
