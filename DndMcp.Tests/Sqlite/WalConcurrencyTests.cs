using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: two processes writing campaigns.db at once wait for each other instead of failing, as long as
/// every write transaction takes the lock up front and is short (PLAN.md Architecture invariants, "Several
/// processes may open campaigns.db at once … WAL, busy_timeout=5000, short BEGIN IMMEDIATE write
/// transactions"; Open risk 6). Each Claude Code or Desktop session spawns its own server process on the same
/// file, so this is the normal case, not an edge case.
///
/// <para>
/// How waiting really works with Microsoft.Data.Sqlite 10.0.12 (pinned below):
/// <list type="bullet">
/// <item>The <c>Default Timeout</c> keyword sets the COMMAND timeout (default 30 s). It does NOT set SQLite's
/// busy_timeout, which stays 0.</item>
/// <item>Microsoft.Data.Sqlite nonetheless waits for locks. On SQLITE_BUSY it resets the statement, sleeps
/// about 150 ms and retries, until the command timeout elapses. So Default Timeout alone makes a second
/// writer wait, in coarse 150 ms steps.</item>
/// <item><c>PRAGMA busy_timeout</c> adds SQLite's native wait INSIDE each attempt. SQLite's busy handler
/// re-polls with backoff (1, 2, 5 … ms, never more than 100 ms apart), so it notices a freed lock somewhat
/// sooner than the 150 ms retry loop, though not instantly. The bigger effect is that it can wait longer than
/// the command timeout, because the timeout is only checked between attempts.</item>
/// <item>Neither helps a DEFERRED transaction that read and then tries to write after another connection
/// committed. That fails with SQLITE_BUSY_SNAPSHOT, which no amount of waiting fixes. Hence BEGIN IMMEDIATE,
/// which is also what <c>SqliteConnection.BeginTransaction()</c> issues by default.</item>
/// </list>
/// Recommended per connection: <c>Foreign Keys=True;Default Timeout=5</c> plus
/// <c>PRAGMA busy_timeout = 5000</c> after Open, with writes in <c>BeginTransaction()</c> (IMMEDIATE).
/// </para>
/// </summary>
public sealed class WalConcurrencyTests : IDisposable
{
    private const int SqliteBusy = 5;
    private const int SqliteBusySnapshot = 517;
    private const string Db = "campaigns.db";

    private readonly SqliteScratch _scratch = new();

    public WalConcurrencyTests()
    {
        using var setup = _scratch.Open(Db);
        setup.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
        setup.Execute("CREATE TABLE note(seq INTEGER PRIMARY KEY, author TEXT NOT NULL) STRICT");
    }

    public void Dispose() => _scratch.Dispose();

    /// <summary>
    /// research §4 "Once at creation: PRAGMA journal_mode=WAL". WAL is recorded in the database file, so it is
    /// set once and every later connection (from any process) sees it without setting it again.
    /// </summary>
    [Fact]
    public void JournalModeWal_SetOnce_PersistsForLaterConnections()
    {
        using var later = _scratch.Open(Db);

        Assert.Equal("wal", later.ExecuteScalar<string>("PRAGMA journal_mode"));
    }

    /// <summary>
    /// Why these tests use temp FILES: an in-memory database cannot use WAL, and the pragma refuses quietly
    /// by answering "memory" instead of raising. Callers must check the returned mode.
    /// </summary>
    [Fact]
    public void JournalModeWal_InMemoryDatabase_IsQuietlyRefused()
    {
        using var memory = SqliteScratch.OpenInMemory();

        Assert.Equal("memory", memory.ExecuteScalar<string>("PRAGMA journal_mode=WAL"));
    }

    /// <summary>
    /// The finding the design needs pinned: <c>Default Timeout</c> sets <see cref="SqliteCommand.CommandTimeout"/>
    /// and leaves SQLite's native busy_timeout at 0, whatever its value. Omitted, it is 30 s.
    /// </summary>
    [Theory]
    [InlineData(null, 30)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    public void DefaultTimeoutKeyword_AnyValue_SetsCommandTimeoutButNotBusyTimeout(int? seconds, int expectedCommandTimeout)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = _scratch.PathOf(Db), Pooling = false };
        if (seconds is { } s)
        {
            builder.DefaultTimeout = s;
        }

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        Assert.Equal(expectedCommandTimeout, connection.CreateCommand().CommandTimeout);
        Assert.Equal(0, connection.ExecuteScalar<long>("PRAGMA busy_timeout"));
    }

    /// <summary>
    /// PLAN.md invariant "short BEGIN IMMEDIATE write transactions". The first writer holds the write lock
    /// for about half a second. The second writer, with busy_timeout still 0 and Default Timeout=5, waits (on
    /// Microsoft.Data.Sqlite's retry loop) and then succeeds. Both rows land in order. The elapsed-time floor
    /// proves the lock really was contended, so this is not passing by luck of scheduling.
    /// </summary>
    [Fact]
    public async Task BeginImmediate_SecondWriterWithDefaultTimeoutOnly_WaitsThenSucceeds()
    {
        using var first = _scratch.Open(Db);
        using var second = _scratch.Open(Db, defaultTimeoutSeconds: 5);
        first.Execute("BEGIN IMMEDIATE");
        first.Execute("INSERT INTO note(author) VALUES ('first')");

        var release = Task.Run(async () =>
        {
            await Task.Delay(500);
            first.Execute("COMMIT");
        });
        var stopwatch = Stopwatch.StartNew();
        second.Execute("BEGIN IMMEDIATE");
        var waited = stopwatch.Elapsed;
        second.Execute("INSERT INTO note(author) VALUES ('second')");
        second.Execute("COMMIT");
        await release;

        Assert.True(waited >= TimeSpan.FromMilliseconds(300), $"Second writer only waited {waited.TotalMilliseconds} ms; the lock was not contended.");
        Assert.Equal(new[] { "first", "second" }, second.Query<string>("SELECT author FROM note ORDER BY seq"));
    }

    /// <summary>
    /// The limit of that wait. With Default Timeout=1 and a lock held throughout, the second writer gives up
    /// after about one second with SQLITE_BUSY ("database is locked"). This is what a user sees if a write
    /// transaction is held too long, and why transactions must stay short.
    /// </summary>
    [Fact]
    public void BeginImmediate_LockHeldLongerThanDefaultTimeout_FailsWithBusy()
    {
        using var first = _scratch.Open(Db);
        using var second = _scratch.Open(Db, defaultTimeoutSeconds: 1);
        first.Execute("BEGIN IMMEDIATE");

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.Throws<SqliteException>(() => second.Execute("BEGIN IMMEDIATE"));
        var waited = stopwatch.Elapsed;
        first.Execute("ROLLBACK");

        Assert.Equal(SqliteBusy, exception.SqliteErrorCode);
        Assert.True(waited >= TimeSpan.FromMilliseconds(900), $"Gave up after {waited.TotalMilliseconds} ms, before the 1 s command timeout.");
    }

    /// <summary>
    /// PLAN.md invariant "busy_timeout=5000". PRAGMA busy_timeout is the native wait inside each attempt, and
    /// it outlasts the command timeout. With Default Timeout=1 but busy_timeout=5000, the second writer waits
    /// past the 1 s mark for a lock released at 2 s and succeeds. So busy_timeout, not Default Timeout, sets the
    /// longest wait once it is set.
    /// </summary>
    [Fact]
    public async Task BusyTimeoutPragma_LongerThanCommandTimeout_WaitsForTheLongerOne()
    {
        using var first = _scratch.Open(Db);
        using var second = _scratch.Open(Db, defaultTimeoutSeconds: 1);
        second.Execute("PRAGMA busy_timeout = 5000");
        first.Execute("BEGIN IMMEDIATE");

        var release = Task.Run(async () =>
        {
            await Task.Delay(2000);
            first.Execute("ROLLBACK");
        });
        var stopwatch = Stopwatch.StartNew();
        second.Execute("BEGIN IMMEDIATE");
        var waited = stopwatch.Elapsed;
        second.Execute("ROLLBACK");
        await release;

        Assert.Equal(5000, second.ExecuteScalar<long>("PRAGMA busy_timeout"));
        Assert.True(waited >= TimeSpan.FromMilliseconds(1500), $"Only waited {waited.TotalMilliseconds} ms; busy_timeout did not extend the wait.");
    }

    /// <summary>
    /// <see cref="SqliteConnection.BeginTransaction()"/> with no arguments issues BEGIN IMMEDIATE: it takes the
    /// write lock at BEGIN, before any statement. The repository can use the ADO.NET API and still get the
    /// design's IMMEDIATE transactions. <c>deferred: true</c> takes no lock until the first write.
    /// </summary>
    [Fact]
    public void BeginTransaction_Default_TakesTheWriteLockImmediately()
    {
        using var first = _scratch.Open(Db);
        using var second = _scratch.Open(Db, defaultTimeoutSeconds: 1);

        SqliteException? whileImmediate;
        using (first.BeginTransaction())
        {
            whileImmediate = Assert.Throws<SqliteException>(() => second.Execute("INSERT INTO note(author) VALUES ('blocked')"));
        }

        using (first.BeginTransaction(deferred: true))
        {
            second.Execute("INSERT INTO note(author) VALUES ('not blocked')");
        }

        Assert.Equal(SqliteBusy, whileImmediate.SqliteErrorCode);
        Assert.Equal(new[] { "not blocked" }, second.Query<string>("SELECT author FROM note"));
    }

    /// <summary>
    /// Why DEFERRED write transactions are banned. A deferred transaction that has read, then tries to write
    /// after another connection committed, fails with SQLITE_BUSY_SNAPSHOT. Its snapshot is stale and waiting
    /// cannot fix that, so SQLite does not even call the busy handler. busy_timeout=5000 is set here, and the
    /// failure still arrives after only the 1 s retry loop.
    /// </summary>
    [Fact]
    public void DeferredTransaction_ReadThenWriteAfterConcurrentCommit_FailsWithBusySnapshot()
    {
        using var reader = _scratch.Open(Db, defaultTimeoutSeconds: 1);
        using var other = _scratch.Open(Db);
        reader.Execute("PRAGMA busy_timeout = 5000");

        using var transaction = reader.BeginTransaction(deferred: true);
        reader.ExecuteScalar<long>("SELECT count(*) FROM note", transaction: transaction);
        other.Execute("INSERT INTO note(author) VALUES ('other')");

        var stopwatch = Stopwatch.StartNew();
        var exception = Assert.Throws<SqliteException>(
            () => reader.Execute("INSERT INTO note(author) VALUES ('stale')", transaction: transaction));
        var waited = stopwatch.Elapsed;

        Assert.Equal(SqliteBusy, exception.SqliteErrorCode);
        Assert.Equal(SqliteBusySnapshot, exception.SqliteExtendedErrorCode);
        Assert.True(waited < TimeSpan.FromMilliseconds(4000), $"Waited {waited.TotalMilliseconds} ms; the busy handler should not run for a stale snapshot.");
    }
}
