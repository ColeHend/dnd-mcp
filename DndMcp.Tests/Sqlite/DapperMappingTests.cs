using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Runs <see cref="DapperMappingTests"/> alone. <see cref="DefaultTypeMap.MatchNamesWithUnderscores"/> and
/// Dapper's deserializer cache are process-wide, so flipping them while another test class materializes rows
/// would make that class flaky.
/// </summary>
[CollectionDefinition(nameof(DapperGlobalStateCollection), DisableParallelization = true)]
public sealed class DapperGlobalStateCollection
{
}

/// <summary>
/// Invariant: the repository's snake_case columns (research/04 §4: <c>created_at</c>, <c>canon_status</c>)
/// round-trip into C# records through Dapper (PLAN.md D2 "Microsoft.Data.Sqlite + Dapper"), and the ways
/// that silently or loudly fail are known before Phase 6 writes its first store.
///
/// <para>
/// What Dapper 2.1.89 does (pinned below):
/// <list type="bullet">
/// <item>By default, names match case-insensitively but underscores are NOT ignored. <c>created_at</c> does not
/// fill <c>CreatedAt</c>. A settable property silently stays null. A positional record throws, because no
/// constructor matches.</item>
/// <item><c>DefaultTypeMap.MatchNamesWithUnderscores = true</c> fixes both. It is a process-global static, and
/// Dapper caches each materializer per (type, column shape) no matter the SQL text. Flipping it after a
/// shape was first read has no effect until <see cref="SqlMapper.PurgeQueryCache"/>. Set it once at startup,
/// before any query.</item>
/// <item>A positional record is bound through its constructor, which needs the exact CLR type of each column
/// (INTEGER is <c>long</c>, REAL is <c>double</c>; <c>int</c> fails), the same column COUNT, and columns in
/// constructor ORDER.</item>
/// </list>
/// </para>
/// </summary>
[Collection(nameof(DapperGlobalStateCollection))]
public sealed class DapperMappingTests : IDisposable
{
    private const string SnakeSelect = "SELECT seq, id, created_at, canon_status, confidence FROM fact WHERE id = @id";

    private readonly SqliteConnection _db;
    private readonly bool _originalUnderscoreSetting;

    public DapperMappingTests()
    {
        _originalUnderscoreSetting = DefaultTypeMap.MatchNamesWithUnderscores;
        SqlMapper.PurgeQueryCache();

        _db = SqliteScratch.OpenInMemory();
        _db.Execute("""
            CREATE TABLE fact (
              seq INTEGER PRIMARY KEY,
              id TEXT NOT NULL UNIQUE,
              created_at TEXT NOT NULL,
              canon_status TEXT NOT NULL,
              confidence REAL
            ) STRICT;
            INSERT INTO fact(id, created_at, canon_status, confidence) VALUES ('f1', '2026-09-26T20:00:00Z', 'played', 0.5);
            """);
    }

    public void Dispose()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = _originalUnderscoreSetting;
        SqlMapper.PurgeQueryCache();
        _db.Dispose();
    }

    /// <summary>
    /// The silent failure: with the default setting, snake_case columns leave PascalCase properties null, with
    /// no exception. A store written this way returns half-empty objects that look fine in a debugger until
    /// someone checks a date.
    /// </summary>
    [Fact]
    public void DefaultSetting_SnakeCaseColumnsIntoSettableProperties_SilentlyLeavesThemNull()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = false;

        var row = _db.QuerySingle<FactClass>(SnakeSelect, new { id = "f1" });

        Assert.Equal(1, row.Seq);
        Assert.Equal("f1", row.Id);
        Assert.Null(row.CreatedAt);
        Assert.Null(row.CanonStatus);
    }

    /// <summary>
    /// The loud failure: with the default setting a positional record cannot be built from snake_case columns.
    /// </summary>
    [Fact]
    public void DefaultSetting_SnakeCaseColumnsIntoPositionalRecord_Throws()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = false;

        var exception = Assert.Throws<InvalidOperationException>(() => _db.QuerySingle<FactRow>(SnakeSelect, new { id = "f1" }));

        Assert.Contains("materialization", exception.Message);
    }

    /// <summary>
    /// The working configuration: MatchNamesWithUnderscores on, and a positional record with <c>long</c> for
    /// INTEGER and <c>double?</c> for a nullable REAL. It round-trips, with the record itself as the insert's
    /// parameter object (parameters bind by property name, so <c>@CreatedAt</c>).
    /// </summary>
    [Fact]
    public void MatchNamesWithUnderscores_PositionalRecordWithExactTypes_RoundTrips()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        var written = new FactRow(0, "f2", "2026-09-27T21:00:00Z", "canon", null);

        _db.Execute("INSERT INTO fact(id, created_at, canon_status, confidence) VALUES (@Id, @CreatedAt, @CanonStatus, @Confidence)", written);
        var read = _db.QuerySingle<FactRow>(SnakeSelect, new { id = "f2" });

        Assert.Equal(written with { Seq = read.Seq }, read);
        Assert.True(read.Seq > 1);
    }

    /// <summary>
    /// Explicit aliases work whatever the global setting, which is the fallback for any query that cannot
    /// rely on it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AliasedColumns_EitherSetting_MapIntoPositionalRecord(bool matchNamesWithUnderscores)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = matchNamesWithUnderscores;

        var row = _db.QuerySingle<FactRow>(
            "SELECT seq AS Seq, id AS Id, created_at AS CreatedAt, canon_status AS CanonStatus, confidence AS Confidence FROM fact WHERE id = 'f1'");

        Assert.Equal(new FactRow(1, "f1", "2026-09-26T20:00:00Z", "played", 0.5), row);
    }

    /// <summary>
    /// Constructor binding does not convert. SQLite INTEGER arrives as Int64, so a record declaring
    /// <c>int Seq</c> cannot be built even with underscores matched, while a settable <c>int</c> property is
    /// converted fine. Records mapped from SQLite use <c>long</c> for every INTEGER column.
    /// </summary>
    [Fact]
    public void MatchNamesWithUnderscores_RecordWithIntForIntegerColumn_Throws()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        var exception = Assert.Throws<InvalidOperationException>(() => _db.QuerySingle<FactRowWithInt>(SnakeSelect, new { id = "f1" }));
        var viaProperty = _db.QuerySingle<FactClassWithInt>("SELECT seq, id FROM fact WHERE id = 'f1'");

        Assert.Contains("System.Int64 seq", exception.Message);
        Assert.Equal(1, viaProperty.Seq);
    }

    /// <summary>
    /// Constructor binding is positional. Selecting the right columns in a different order, or one column
    /// short, fails. <c>SELECT *</c> into a record breaks the day a migration adds a column.
    /// </summary>
    [Theory]
    [InlineData("SELECT id, seq, created_at, canon_status, confidence FROM fact WHERE id = 'f1'")]
    [InlineData("SELECT seq, id, created_at, canon_status FROM fact WHERE id = 'f1'")]
    public void MatchNamesWithUnderscores_ColumnsNotInConstructorOrderOrCount_Throws(string sql)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        Assert.Throws<InvalidOperationException>(() => _db.QuerySingle<FactRow>(sql));
    }

    /// <summary>
    /// The cache trap: once a (type, column shape) has been materialized, changing the global setting has no
    /// effect for that shape, even with different SQL text, until the cache is purged. So the setting must be
    /// fixed before the first query in the process. A lazy "set it in the store constructor" can lose to a
    /// query that ran earlier.
    /// </summary>
    [Fact]
    public void MatchNamesWithUnderscores_FlippedAfterFirstUse_IsIgnoredUntilCachePurged()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = false;
        var before = _db.QuerySingle<FactClass>(SnakeSelect, new { id = "f1" });

        DefaultTypeMap.MatchNamesWithUnderscores = true;
        var afterFlip = _db.QuerySingle<FactClass>(SnakeSelect + " AND 1 = 1", new { id = "f1" });
        SqlMapper.PurgeQueryCache();
        var afterPurge = _db.QuerySingle<FactClass>(SnakeSelect, new { id = "f1" });

        Assert.Null(before.CreatedAt);
        Assert.Null(afterFlip.CreatedAt);
        Assert.Equal("2026-09-26T20:00:00Z", afterPurge.CreatedAt);
    }

    public sealed record FactRow(long Seq, string Id, string CreatedAt, string CanonStatus, double? Confidence);

    public sealed record FactRowWithInt(int Seq, string Id, string CreatedAt, string CanonStatus, double? Confidence);

    public sealed class FactClass
    {
        public long Seq { get; set; }

        public string Id { get; set; } = "";

        public string? CreatedAt { get; set; }

        public string? CanonStatus { get; set; }

        public double? Confidence { get; set; }
    }

    public sealed class FactClassWithInt
    {
        public int Seq { get; set; }

        public string Id { get; set; } = "";
    }
}
