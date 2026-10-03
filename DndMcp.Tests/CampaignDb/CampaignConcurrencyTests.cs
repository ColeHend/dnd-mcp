using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: campaigns.db is shared safely. Several server processes (one per Claude session) write the same file and
/// all succeed, waiting on SQLite's busy_timeout; in one process, concurrent tool calls queue on the write semaphore and
/// never overlap inside SQLite; and a reader during a write sees the last committed state, never half a batch.
/// </summary>
public sealed class CampaignConcurrencyTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly string _campaignId;

    public CampaignConcurrencyTests()
    {
        using var connection = _db.Open();
        _campaignId = new CampaignSeed(connection).Campaign().Id;
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Two "processes" (two CampaignDatabase instances, so two semaphores) write the same file at once, each write holding
    /// its transaction for a while: every write lands, because BEGIN IMMEDIATE waits on busy_timeout instead of failing.
    /// </summary>
    [Fact]
    public async Task Write_TwoInstancesOnOneFileConcurrently_AllWritesSucceed()
    {
        using var first = _db.OtherProcess();
        using var second = _db.OtherProcess();
        first.EnsureReady();
        second.EnsureReady();

        var writes = Enumerable.Range(0, 12).Select(i => Task.Run(() => (i % 2 == 0 ? first : second).Write((connection, transaction) =>
        {
            connection.Execute("INSERT INTO app_state(key, value) VALUES (@key, 'x')", new { key = "k" + i }, transaction);
            Thread.Sleep(20);
            return i;
        })));
        await Task.WhenAll(writes);

        using var check = _db.Open();
        Assert.Equal(12, check.ExecuteScalar<long>("SELECT count(*) FROM app_state"));
    }

    /// <summary>In one process, writers never overlap: the second's work starts only after the first's has committed.</summary>
    [Fact]
    public async Task WriteAsync_InProcessWriters_SerialiseOnTheSemaphore()
    {
        var active = 0;
        var maxActive = 0;
        var order = new ConcurrentQueue<int>();

        using var start = new ManualResetEventSlim();
        var writes = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return await _db.Database.WriteAsync((connection, transaction) =>
            {
                var now = Interlocked.Increment(ref active);
                InterlockedMax(ref maxActive, now);
                Thread.Sleep(10);
                connection.Execute("INSERT INTO app_state(key, value) VALUES (@key, 'x')", new { key = "k" + i }, transaction);
                order.Enqueue(i);
                Interlocked.Decrement(ref active);
                return i;
            });
        })).ToList();
        start.Set();
        await Task.WhenAll(writes);

        Assert.Equal(1, maxActive);
        Assert.Equal(8, order.Count);
    }

    /// <summary>
    /// The batch holds the write lock from BEGIN, before its work writes anything (BEGIN IMMEDIATE, contract §0). A
    /// deferred transaction would take it only at the first write, so a batch that reads and then writes (every recorder
    /// update does) could lose the race to another process and fail with SQLITE_BUSY_SNAPSHOT, which no waiting fixes.
    /// </summary>
    [Fact]
    public void Write_TheLockIsTakenAtBegin_BeforeTheWorkWritesAnything()
    {
        var otherError = 0;

        _db.Database.Write((connection, transaction) =>
        {
            _ = connection.ExecuteScalar<long>("SELECT count(*) FROM campaign", transaction: transaction);
            using var other = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _db.DatabasePath,
                DefaultTimeout = 1,
                Pooling = false,
            }.ToString());
            other.Open();
            otherError = Assert.Throws<SqliteException>(() =>
                other.Execute("INSERT INTO app_state(key, value) VALUES ('k', 'v')")).SqliteErrorCode;
            return 0;
        });

        Assert.Equal(5, otherError);
    }

    /// <summary>
    /// What the semaphore is for: a writer queued behind another waits on it, so it can be cancelled while it waits and
    /// holds no pool thread. Without it the writer would sit inside SQLite's busy handler (BEGIN IMMEDIATE serialises the
    /// work either way), deaf to cancellation until the lock frees or busy_timeout runs out.
    /// </summary>
    [Fact]
    public async Task WriteAsync_QueuedBehindAnotherWriter_IsCancelledPromptlyWhileItWaits()
    {
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => _db.Database.Write((_, _) =>
        {
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(15));
            return 0;
        }));
        Assert.True(holding.Wait(TimeSpan.FromSeconds(10)));
        using var cancel = new CancellationTokenSource();
        var waiting = _db.Database.WriteAsync((connection, transaction) =>
            connection.Execute("UPDATE campaign SET summary_md = 'queued'", transaction: transaction), ct: cancel.Token);
        await Task.Delay(300);

        cancel.Cancel();
        var finished = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(2)));

        release.Set();
        await first;
        Assert.Same(waiting, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        using var check = _db.Open();
        Assert.Equal(string.Empty, check.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
    }

    /// <summary>
    /// Another process holding the write lock past busy_timeout (a long migration or restore in another session) is the
    /// environment, not a bug: the user-facing error, after waiting the full timeout, with nothing written.
    /// </summary>
    [Fact]
    public void Write_AnotherProcessHoldsTheLockPastTheBusyTimeout_ThrowsUnavailableAfterWaiting()
    {
        using var other = _db.OtherProcess();
        other.EnsureReady();
        using var holder = new SqliteConnection(CampaignDatabase.ConnectionString(_db.DatabasePath, SqliteOpenMode.ReadWrite));
        holder.Open();
        using var held = holder.BeginTransaction();
        var clock = Stopwatch.StartNew();

        var error = Assert.Throws<CampaignStoreUnavailableException>(() => other.Write((connection, transaction) =>
            connection.Execute("INSERT INTO app_state(key, value) VALUES ('k', 'v')", transaction: transaction)));

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(4), $"gave up after {clock.Elapsed}, before busy_timeout");
        Assert.Equal(5, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        Assert.Contains(_db.DatabasePath, error.Message);
        Assert.Contains("locked by another dnd-mcp process", error.Message);
        held.Rollback();
        Assert.Equal(0, holder.ExecuteScalar<long>("SELECT count(*) FROM app_state"));
    }

    /// <summary>WAL readers see the last commit: an uncommitted change is invisible to a reader, then visible after.</summary>
    [Fact]
    public void OpenRead_DuringAWrite_SeesTheCommittedSnapshotOnly()
    {
        string? seenDuring = null;

        _db.Database.Write((connection, transaction) =>
        {
            var recorder = new ChangeRecorder(connection, transaction,
                BatchContext.New(_campaignId, CampaignValues.Actors.Claude, "test", null, null), _db.Database.Now());
            recorder.Update("campaign", _campaignId, new Dictionary<string, object?> { ["summary_md"] = "in progress" }, "update");
            using var reader = _db.Database.OpenRead();
            seenDuring = reader.ExecuteScalar<string>("SELECT summary_md FROM campaign");
            return 0;
        });

        Assert.Equal(string.Empty, seenDuring);
        using var after = _db.Open();
        Assert.Equal("in progress", after.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
