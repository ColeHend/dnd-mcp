using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: <see cref="CampaignDatabase"/> touches nothing until a campaign call needs it, then opens, creates and
/// migrates campaigns.db once; every connection follows the house rules; a write is one IMMEDIATE transaction that commits
/// once (or rolls back for a dry run or an exception) with its change_log flushed; the first write of a UTC day backs up
/// an existing database without ever blocking the write; a schema newer than the build is refused on every use, even
/// after another process migrated the file under a running one; and a problem the user can fix, met while opening,
/// writing or reading, is a <see cref="CampaignStoreUnavailableException"/> naming the file, retried on the next call
/// (the calls that were waiting while an attempt to open failed share its refusal instead of each waiting again).
/// </summary>
public sealed class CampaignDatabaseTests : IDisposable
{
    private readonly CampaignTestDb _db = new(create: false);

    public void Dispose() => _db.Dispose();

    /// <summary>The server must answer initialize even when the data directory is hostile: no I/O at construction.</summary>
    [Fact]
    public void Constructor_PathInAMissingDirectory_TouchesNothing()
    {
        var path = Path.Combine(_db.DirectoryPath, "not", "yet", "campaigns.db");

        using var database = new CampaignDatabase(path, _db.Time);

        Assert.False(Directory.Exists(Path.Combine(_db.DirectoryPath, "not")));
        Assert.False(database.Exists);
        Assert.Equal(path, database.Path);
        Assert.Equal(Path.Combine(_db.DirectoryPath, "not", "yet", "backups"), database.Backups.DirectoryPath);
    }

    [Theory]
    [InlineData("2026-09-01T12:00:00+00:00", "2026-09-01T12:00:00.000Z")]
    [InlineData("2026-09-01T23:59:59.9999+00:00", "2026-09-01T23:59:59.999Z")]
    [InlineData("2026-09-02T01:30:00.123+02:00", "2026-09-01T23:30:00.123Z")]
    public void Now_ManualClock_IsUtcWithMilliseconds(string clock, string expected)
    {
        _db.Time.SetUtcNow(DateTimeOffset.Parse(clock, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expected, _db.Database.Now());
    }

    [Fact]
    public void NewId_IsALowerCaseVersion7Uuid()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => CampaignDatabase.NewId()).ToList();

        Assert.All(ids, id => Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EnsureReady_NewPath_CreatesTheDirectoryAndAMigratedWalDatabase()
    {
        var path = Path.Combine(_db.DirectoryPath, "data", "campaigns.db");
        using var database = new CampaignDatabase(path, _db.Time);

        database.EnsureReady();
        database.EnsureReady();

        Assert.True(database.Exists);
        using var connection = database.OpenRead();
        Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
    }

    /// <summary>A read never creates the database: "no campaigns yet" is an answer, not a reason to write a file.</summary>
    [Fact]
    public void TryOpenExisting_NoFile_ReturnsNullAndCreatesNothing()
    {
        using var connection = _db.Database.TryOpenExisting();

        Assert.Null(connection);
        Assert.False(File.Exists(_db.DatabasePath));
    }

    [Fact]
    public void TryOpenExisting_FileExists_OpensIt()
    {
        _db.Database.EnsureReady();

        using var connection = _db.Database.TryOpenExisting();

        Assert.NotNull(connection);
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM campaign"));
    }

    /// <summary>Deleted under a running server: the next call builds a new file instead of failing until a restart.</summary>
    [Fact]
    public void OpenRead_FileDeletedAfterReady_CreatesItAgain()
    {
        _db.Database.EnsureReady();
        foreach (var file in Directory.GetFiles(_db.DirectoryPath, "campaigns.db*"))
        {
            File.Delete(file);
        }

        using var connection = _db.Database.OpenRead();

        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
    }

    /// <summary>
    /// The house pragmas on every connection: foreign keys, recursive triggers (REPLACE's implicit delete fires the delete
    /// triggers), busy_timeout 5000 (Default Timeout alone leaves it 0), synchronous NORMAL (1).
    /// </summary>
    [Theory]
    [InlineData("PRAGMA foreign_keys", 1L)]
    [InlineData("PRAGMA recursive_triggers", 1L)]
    [InlineData("PRAGMA busy_timeout", 5000L)]
    [InlineData("PRAGMA synchronous", 1L)]
    public void OpenRead_Connection_HasTheHousePragmas(string pragma, long expected)
    {
        using var connection = _db.Database.OpenRead();

        Assert.Equal(expected, connection.ExecuteScalar<long>(pragma));
    }

    [Fact]
    public void Write_Work_CommitsAndFlushesTheRecorderBeforeCommit()
    {
        var campaignId = SeedCampaign();

        _db.Database.Write((connection, transaction) =>
        {
            var recorder = new ChangeRecorder(connection, transaction,
                BatchContext.New(campaignId, CampaignValues.Actors.Claude, "test", null, "because"), _db.Database.Now());
            recorder.Update("campaign", campaignId, new Dictionary<string, object?> { ["summary_md"] = "A tale." }, "update");
            return 0;
        });

        using var check = _db.Open();
        Assert.Equal("A tale.", check.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
        Assert.Equal(1, check.ExecuteScalar<long>("SELECT count(*) FROM change_log"));
    }

    /// <summary>A dry run runs everything (so it fails where the real run would) and persists nothing: no row, no batch.</summary>
    [Fact]
    public void Write_DryRun_RunsTheWorkAndPersistsNothing()
    {
        var campaignId = SeedCampaign();
        var logged = 0;

        var seen = _db.Database.Write((connection, transaction) =>
        {
            var recorder = new ChangeRecorder(connection, transaction,
                BatchContext.New(campaignId, CampaignValues.Actors.Claude, "test", null, null), _db.Database.Now());
            recorder.Update("campaign", campaignId, new Dictionary<string, object?> { ["summary_md"] = "A tale." }, "update");
            logged = recorder.RowsLogged;
            return connection.ExecuteScalar<string>("SELECT summary_md FROM campaign", transaction: transaction);
        }, dryRun: true);

        Assert.Equal("A tale.", seen);
        Assert.Equal(1, logged);
        using var check = _db.Open();
        Assert.Equal(string.Empty, check.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM change_log"));
    }

    /// <summary>
    /// RAISE(ABORT) and other failures undo only their own statement; the batch writer must roll back the rest itself,
    /// or half a batch persists (AppendOnlyTriggerTests pins the half-batch hazard).
    /// </summary>
    [Fact]
    public void Write_WorkThrowsAfterWriting_RollsBackEverythingAndPropagates()
    {
        var campaignId = SeedCampaign();

        Assert.Throws<SqliteException>(() => _db.Database.Write<int>((connection, transaction) =>
        {
            connection.Execute("UPDATE campaign SET summary_md = 'half'", transaction: transaction);
            connection.Execute("INSERT INTO campaign(id) VALUES ('incomplete')", transaction: transaction);
            return 0;
        }));

        using var check = _db.Open();
        Assert.Equal(string.Empty, check.ExecuteScalar<string>("SELECT summary_md FROM campaign WHERE id = @campaignId", new { campaignId }));
    }

    [Fact]
    public async Task WriteAsync_Work_Commits()
    {
        var campaignId = SeedCampaign();

        var result = await _db.Database.WriteAsync((connection, transaction) =>
            connection.Execute("UPDATE campaign SET summary_md = 'async' WHERE id = @campaignId", new { campaignId }, transaction));

        Assert.Equal(1, result);
        using var check = _db.Open();
        Assert.Equal("async", check.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
    }

    [Fact]
    public async Task WriteAsync_CancelledWhileWaitingForTheLock_WritesNothing()
    {
        var campaignId = SeedCampaign();
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Run(() => _db.Database.Write((_, _) =>
        {
            holding.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }));
        holding.Wait(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();

        var waiting = _db.Database.WriteAsync((connection, transaction) =>
            connection.Execute("UPDATE campaign SET summary_md = 'cancelled'", transaction: transaction), ct: cancel.Token);
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.Set();
        await first;
        using var check = _db.Open();
        Assert.Equal(string.Empty, check.ExecuteScalar<string>("SELECT summary_md FROM campaign WHERE id = @campaignId", new { campaignId }));
    }

    [Fact]
    public void EnsureReady_DatabaseNewerThanThisBuild_ThrowsUnavailableAndLeavesTheFileUntouched()
    {
        _db.Database.EnsureReady();
        using (var connection = _db.Open())
        {
            connection.Execute("PRAGMA user_version = 99");
            connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        var before = Hash(_db.DatabasePath);
        using var other = _db.OtherProcess();

        var error = Assert.Throws<CampaignStoreUnavailableException>(other.EnsureReady);

        Assert.Contains("schema version 99", error.Message);
        Assert.Equal(before, Hash(_db.DatabasePath));
    }

    /// <summary>
    /// A refusal is never cached as success: a newer-schema database is refused on every call and by every entry point,
    /// never read or written by the call after the first one fails.
    /// </summary>
    [Fact]
    public void EnsureReady_DatabaseNewerThanThisBuild_IsRefusedOnEveryCall()
    {
        _db.Database.EnsureReady();
        using (var connection = _db.Open())
        {
            connection.Execute($"PRAGMA user_version = {CampaignDbMigrator.LatestVersion + 1}");
        }

        using var other = _db.OtherProcess();

        Assert.Throws<CampaignStoreUnavailableException>(other.EnsureReady);
        Assert.Throws<CampaignStoreUnavailableException>(other.EnsureReady);
        Assert.Throws<CampaignStoreUnavailableException>(() => other.OpenRead().Dispose());
        Assert.Throws<CampaignStoreUnavailableException>(() => other.TryOpenExisting()?.Dispose());
        Assert.Throws<CampaignStoreUnavailableException>(() => other.Write((connection, transaction) =>
            connection.Execute("INSERT INTO app_state(key, value) VALUES ('k', 'v')", transaction: transaction)));
        using var check = new SqliteConnection($"Data Source={_db.DatabasePath};Mode=ReadOnly;Pooling=False");
        check.Open();
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT count(*) FROM app_state"));
    }

    /// <summary>
    /// R02 (contract fix FI2): the newer-schema refusal also holds for a process that was already running when another one
    /// (the next dnd-mcp build, in another session) migrated the file. Its next write is refused after BEGIN IMMEDIATE,
    /// with nothing written, and its next read is refused at open. Before, the first successful check was cached for the
    /// process's life, so an old server kept writing old-shaped rows into the newer schema. One version ahead is the
    /// boundary (the next build's migration lands exactly there); far ahead must not wrap or pass either.
    /// </summary>
    [Theory]
    [InlineData("write", 1)]
    [InlineData("write-async", 1)]
    [InlineData("open-read", 1)]
    [InlineData("try-open-existing", 1)]
    [InlineData("write", 98)]
    [InlineData("write-async", 98)]
    [InlineData("open-read", 98)]
    [InlineData("try-open-existing", 98)]
    public async Task Use_AnotherProcessMigratedTheFileToANewerSchemaAfterReady_IsRefusedAndWritesNothing(string entry, int versionsAhead)
    {
        var version = CampaignDbMigrator.LatestVersion + versionsAhead;
        SeedCampaign();
        _db.Database.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'before'", transaction: transaction));
        using (var newer = new SqliteConnection($"Data Source={_db.DatabasePath};Pooling=False"))
        {
            newer.Open();
            newer.Execute($"PRAGMA user_version = {version}");
        }

        var error = await Record.ExceptionAsync(async () =>
        {
            switch (entry)
            {
                case "write":
                    _db.Database.Write((connection, transaction) =>
                        connection.Execute("UPDATE campaign SET summary_md = 'after'", transaction: transaction));
                    break;
                case "write-async":
                    await _db.Database.WriteAsync((connection, transaction) =>
                        connection.Execute("UPDATE campaign SET summary_md = 'after'", transaction: transaction));
                    break;
                case "open-read":
                    _db.Database.OpenRead().Dispose();
                    break;
                case "try-open-existing":
                    _db.Database.TryOpenExisting()?.Dispose();
                    break;
            }
        });

        var refused = Assert.IsType<CampaignStoreUnavailableException>(error);
        Assert.Contains(
            $"written by a newer version of dnd-mcp (schema version {version}; this version understands up to {CampaignDbMigrator.LatestVersion})",
            refused.Message);
        Assert.Contains(_db.DatabasePath, refused.Message);
        using var check = new SqliteConnection($"Data Source={_db.DatabasePath};Mode=ReadOnly;Pooling=False");
        check.Open();
        Assert.Equal("before", check.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
    }

    /// <summary>
    /// FI2: the schema version is read under the write lock, after BEGIN IMMEDIATE. Another build's migration holds that
    /// lock while it runs, so a batch that waited for the lock behind it is refused once it gets in, and writes nothing. A
    /// check made before waiting would have seen the old version and let the batch write into the newer schema.
    /// </summary>
    [Fact]
    public async Task Write_AnotherProcessMigratesWhileTheBatchWaitsForTheLock_IsRefusedAndWritesNothing()
    {
        SeedCampaign();
        _db.Database.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'before'", transaction: transaction));
        using var migration = new SqliteConnection(CampaignDatabase.ConnectionString(_db.DatabasePath, SqliteOpenMode.ReadWrite));
        migration.Open();
        using var upgrade = migration.BeginTransaction();

        var batch = Task.Run(() => _db.Database.Write((connection, transaction) =>
            connection.Execute("UPDATE campaign SET summary_md = 'after'", transaction: transaction)));
        await Task.Delay(500);
        Assert.False(batch.IsCompleted, "The batch did not wait for the migration's lock.");
        migration.Execute($"PRAGMA user_version = {CampaignDbMigrator.LatestVersion + 1}", transaction: upgrade);
        upgrade.Commit();

        var refused = await Assert.ThrowsAsync<CampaignStoreUnavailableException>(() => batch);
        Assert.Contains("written by a newer version of dnd-mcp", refused.Message);
        using var check = new SqliteConnection($"Data Source={_db.DatabasePath};Mode=ReadOnly;Pooling=False");
        check.Open();
        Assert.Equal("before", check.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
    }

    public static TheoryData<int, string> UserFixableCodes() => new()
    {
        { 5, "is locked by another dnd-mcp process" },
        { 517, "is locked by another dnd-mcp process" },
        { 3, "not readable and writable by this user" },
        { 8, "not readable and writable by this user" },
        { 14, "not readable and writable by this user" },
        { 10, "A disk I/O error stopped dnd-mcp" },
        { 11, "is damaged or is not a dnd-mcp database" },
        { 26, "is damaged or is not a dnd-mcp database" },
        { 13, "is full" },
    };

    /// <summary>
    /// R04 (contract fix FI4): the mapping the write path uses, for the host's filters to apply to read failures too. A
    /// user-fixable code (517 is SQLITE_BUSY_SNAPSHOT, compared on its primary code) becomes the store's message naming
    /// the file, with the SQLite error kept as the inner exception and none of its text (SQL, schema) in the message.
    /// </summary>
    [Theory]
    [MemberData(nameof(UserFixableCodes))]
    public void TryMapUnavailable_UserFixableCode_IsTheStoreMessageNamingTheFile(int code, string phrase)
    {
        var failure = new SqliteException("SQLite Error: near SELECT secret_md FROM entity", code);

        Assert.True(CampaignDatabase.TryMapUnavailable(failure, _db.DatabasePath, out var unavailable));

        Assert.Contains(phrase, unavailable.Message);
        Assert.Contains(_db.DatabasePath, unavailable.Message);
        Assert.DoesNotContain("SELECT", unavailable.Message);
        Assert.DoesNotContain("SQLite Error", unavailable.Message);
        Assert.Same(failure, unavailable.InnerException);
    }

    /// <summary>Everything else (a bug, a constraint, a misuse) is not the user's to fix and stays unmapped.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(275)]
    public void TryMapUnavailable_OtherCode_IsNotMapped(int code)
    {
        Assert.False(CampaignDatabase.TryMapUnavailable(new SqliteException("SQLite Error", code), _db.DatabasePath, out var unavailable));
        Assert.Null(unavailable);
    }

    /// <summary>
    /// R04: the failure the read path met, mapped. A damaged table page (header and schema intact, so the open and the
    /// migration check pass) fails the first read of that table with SQLITE_CORRUPT, which maps to the damaged-file
    /// message naming the backups to restore from.
    /// </summary>
    [Fact]
    public void TryMapUnavailable_AReadOfADamagedTablePage_IsTheDamagedFileMessage()
    {
        SeedCampaign();
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Entity(connection.ExecuteScalar<string>("SELECT id FROM campaign")!, CampaignValues.Kinds.Character, "Old Hero");
        }

        _db.ScrambleRootPage("entity");
        using var reader = _db.Database.OpenRead();
        var failure = Assert.Throws<SqliteException>(() => reader.Query<string>("SELECT name FROM entity").ToList());

        Assert.True(CampaignDatabase.TryMapUnavailable(failure, _db.DatabasePath, out var unavailable));

        Assert.Contains("is damaged or is not a dnd-mcp database", unavailable.Message);
        Assert.Contains(_db.Database.Backups.DirectoryPath, unavailable.Message);
    }

    /// <summary>
    /// FI10: a reader that must never create campaigns.db (the rules tools' ambient defaults) opens it read-write without
    /// create: on a missing file that is SQLITE_CANTOPEN, and no file appears.
    /// </summary>
    [Fact]
    public void ConnectionString_ReadWriteModeOnAMissingFile_FailsAndCreatesNothing()
    {
        using var connection = new SqliteConnection(CampaignDatabase.ConnectionString(_db.DatabasePath, SqliteOpenMode.ReadWrite));

        var error = Assert.Throws<SqliteException>(connection.Open);

        Assert.Equal(14, error.SqliteErrorCode);
        Assert.False(File.Exists(_db.DatabasePath));
    }

    /// <summary>
    /// R08/C12 (FI10): why such a reader must not be read-only. After a read of the WAL database and a close, a
    /// read-write connection leaves nothing beside campaigns.db; a read-only one cannot remove the -wal and -shm it made.
    /// </summary>
    [Theory]
    [InlineData(SqliteOpenMode.ReadWrite, false)]
    [InlineData(SqliteOpenMode.ReadOnly, true)]
    public void ConnectionString_ReadOfTheWalDatabaseThenClose_LeavesWalAndShmOnlyWhenReadOnly(SqliteOpenMode mode, bool leftBehind)
    {
        SeedCampaign();
        Assert.False(File.Exists(_db.DatabasePath + "-wal"));

        using (var connection = new SqliteConnection(CampaignDatabase.ConnectionString(_db.DatabasePath, mode)))
        {
            connection.Open();
            Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM campaign"));
        }

        Assert.Equal(leftBehind, File.Exists(_db.DatabasePath + "-wal"));
        Assert.Equal(leftBehind, File.Exists(_db.DatabasePath + "-shm"));
    }

    /// <summary>
    /// FI10: a reader with a fallback can give up on a locked file quickly. Microsoft.Data.Sqlite retries a locked
    /// statement until its command timeout (the store's is 5 s) whatever busy_timeout says, so the bound is the
    /// connection string's: at 1 s a read behind another process's EXCLUSIVE lock fails in about a second.
    /// </summary>
    [Fact]
    public void ConnectionString_DefaultTimeoutOfOneSecond_GivesUpOnALockedFileInAboutASecond()
    {
        SeedCampaign();
        using (var rollback = new SqliteConnection($"Data Source={_db.DatabasePath};Pooling=False"))
        {
            rollback.Open();
            Assert.Equal("delete", rollback.ExecuteScalar<string>("PRAGMA journal_mode = DELETE"));
        }

        using var holder = new SqliteConnection(CampaignDatabase.ConnectionString(_db.DatabasePath, SqliteOpenMode.ReadWrite));
        holder.Open();
        holder.Execute("BEGIN EXCLUSIVE");
        using var reader = new SqliteConnection(CampaignDatabase.ConnectionString(_db.DatabasePath, SqliteOpenMode.ReadWrite, defaultTimeoutSeconds: 1));
        reader.Open();
        reader.Execute("PRAGMA busy_timeout = 250");
        var clock = Stopwatch.StartNew();

        var error = Assert.Throws<SqliteException>(() => reader.ExecuteScalar<long>("SELECT count(*) FROM campaign"));

        Assert.Equal(5, error.SqliteErrorCode);
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(3.5));
        holder.Execute("ROLLBACK");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConnectionString_DefaultTimeoutBelowOneSecond_Throws(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CampaignDatabase.ConnectionString(_db.DatabasePath, SqliteOpenMode.ReadWrite, defaultTimeoutSeconds: seconds));

    /// <summary>A damaged file left where it is stays refused on every call (the recovery path only covers a missing file).</summary>
    [Fact]
    public void EnsureReady_DamagedFileLeftInPlace_IsRefusedOnEveryCall()
    {
        File.WriteAllText(_db.DatabasePath, new string('x', 8192));

        Assert.Throws<CampaignStoreUnavailableException>(_db.Database.EnsureReady);
        Assert.Throws<CampaignStoreUnavailableException>(_db.Database.EnsureReady);
        Assert.Throws<CampaignStoreUnavailableException>(() => _db.Database.OpenRead().Dispose());
        Assert.Throws<CampaignStoreUnavailableException>(() => _db.Database.Write((_, _) => 0));
    }

    /// <summary>
    /// A file the user cannot write opens (SQLite quietly opens it read-only) and only fails inside the batch: that failure
    /// is still the user-facing error naming the file, not a raw SQLite error, and nothing is written.
    /// </summary>
    [Fact]
    public void Write_ReadOnlyFile_ThrowsUnavailableNamingTheFileAndWritesNothing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var campaignId = SeedCampaign();
        File.SetUnixFileMode(_db.DatabasePath, UnixFileMode.UserRead);
        try
        {
            using var other = _db.OtherProcess();

            var error = Assert.Throws<CampaignStoreUnavailableException>(() => other.Write((connection, transaction) =>
                connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction)));

            Assert.Contains(_db.DatabasePath, error.Message);
            Assert.Contains("not readable and writable", error.Message);
            Assert.DoesNotContain("SQLite Error", error.Message);
            Assert.Equal(8, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        }
        finally
        {
            File.SetUnixFileMode(_db.DatabasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using var check = _db.Open();
        Assert.Equal(string.Empty, check.ExecuteScalar<string>("SELECT summary_md FROM campaign WHERE id = @campaignId", new { campaignId }));
    }

    [Theory]
    [InlineData("this is not a database, it is a shopping list")]
    [InlineData("SQLite format 3\0 but the rest is garbage garbage garbage garbage garbage garbage garbage garbage")]
    public void EnsureReady_DamagedFile_ThrowsUnavailableNamingTheFileAndNoSql(string content)
    {
        File.WriteAllText(_db.DatabasePath, content + new string('x', 4096));

        var error = Assert.Throws<CampaignStoreUnavailableException>(_db.Database.EnsureReady);

        Assert.Contains(_db.DatabasePath, error.Message);
        Assert.Contains("damaged or is not a dnd-mcp database", error.Message);
        Assert.DoesNotContain("SQLite Error", error.Message);
        Assert.IsType<SqliteException>(error.InnerException);
    }

    /// <summary>Failure is not cached: once the user moves the damaged file aside, the next call works without a restart.</summary>
    [Fact]
    public void EnsureReady_AfterAFailureIsFixed_SucceedsOnTheNextCall()
    {
        File.WriteAllText(_db.DatabasePath, new string('x', 8192));
        Assert.Throws<CampaignStoreUnavailableException>(_db.Database.EnsureReady);
        File.Move(_db.DatabasePath, _db.DatabasePath + ".broken");

        _db.Database.EnsureReady();

        using var connection = _db.Database.OpenRead();
        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
    }

    /// <summary>
    /// RR01: calls that waited while an attempt to open campaigns.db failed share that attempt's refusal. On an instance
    /// not yet ready (a new session's server) while another connection holds the file past busy_timeout, each attempt
    /// waits about 10 s; when every queued call ran an attempt of its own after the one before it, four calls at once were
    /// refused at 10, 20, 30 and 40 s, and dice_roll's log lookup queued behind them. Now all four are refused after one
    /// wait, with the same message and the same cause (SQLITE_BUSY: dice_roll words its "Not logged" line by it, and the
    /// store's own sentence, "this call did nothing", is wrong next to a roll that stands). A call made after the lock is
    /// gone opens the file: the failure is not cached. Threads, not the thread pool, so all four are waiting before the
    /// first attempt ends however busy the pool is.
    /// </summary>
    [Fact]
    public void OpenRead_FourCallsAtOnceOnAnInstanceNotReadyUnderAnotherConnectionsExclusiveLock_AreAllRefusedAfterOneWait()
    {
        SeedCampaign();
        using var fresh = _db.OtherProcess();
        using (var holder = new SqliteConnection($"Data Source={_db.DatabasePath};Pooling=False"))
        {
            holder.Open();
            holder.Execute("PRAGMA locking_mode = EXCLUSIVE");
            holder.Execute("BEGIN EXCLUSIVE");
            holder.Execute("UPDATE campaign SET summary_md = 'held'");
            var single = Stopwatch.StartNew();
            Assert.Throws<CampaignStoreUnavailableException>(() => fresh.OpenRead().Dispose());
            var one = single.Elapsed;

            var clock = Stopwatch.StartNew();
            var refusals = new (Exception? Error, TimeSpan At)[4];
            var calls = Enumerable.Range(0, refusals.Length).Select(i => new Thread(() =>
            {
                var error = Record.Exception(() => fresh.OpenRead().Dispose());
                refusals[i] = (error, clock.Elapsed);
            })).ToList();
            calls.ForEach(t => t.Start());
            calls.ForEach(t => t.Join());

            var messages = refusals.Select(r => Assert.IsType<CampaignStoreUnavailableException>(r.Error).Message).Distinct().ToList();
            Assert.Single(messages);
            Assert.Contains(_db.DatabasePath, messages[0], StringComparison.Ordinal);
            Assert.All(refusals, r => Assert.Equal(5, Assert.IsType<SqliteException>(r.Error!.InnerException).SqliteErrorCode & 0xFF));
            Assert.True(refusals.Max(r => r.At) < one * 1.5,
                $"one refused OpenRead took {Seconds(one)}; four at once were refused at " +
                string.Join(", ", refusals.Select(r => Seconds(r.At)).Order(StringComparer.Ordinal)));
            holder.Execute("ROLLBACK");
        }

        using var connection = fresh.OpenRead();
        Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT count(*) FROM campaign"));
    }

    /// <summary>
    /// RR01: only the store's own refusal is shared with the calls that waited for a failed attempt. An attempt that fails
    /// any other way (a bug, or a SQLite library without a feature campaigns.db needs) is not handed to them as a store
    /// refusal, which would tell the user to fix a file that is fine, and neither is an earlier attempt's store refusal:
    /// each runs an attempt of its own, which here succeeds. The failing attempt ends only once the other three calls are
    /// waiting for it.
    /// </summary>
    [Fact]
    public void EnsureReady_AnAttemptFailingWithAnErrorTheStoreDoesNotMapWhileOthersWait_TheyRunTheirOwnAttempt()
    {
        SeedCampaign();
        using var fresh = _db.OtherProcess();
        using var inAttempt = new ManualResetEventSlim();
        using var waiting = new CountdownEvent(3);
        var attempts = 0;
        fresh.BeforeEachAttempt = () =>
        {
            switch (Interlocked.Increment(ref attempts))
            {
                case 1:
                    throw new CampaignStoreUnavailableException("an earlier attempt's refusal");
                case 2:
                    inAttempt.Set();
                    Assert.True(waiting.Wait(TimeSpan.FromSeconds(30)));
                    Thread.Sleep(500); // the three read the failure count and queue for the ready lock
                    throw new InvalidOperationException("an attempt that failed with a bug");
            }
        };
        Assert.Equal("an earlier attempt's refusal", Assert.Throws<CampaignStoreUnavailableException>(() => fresh.OpenRead().Dispose()).Message);
        Exception? failed = null;
        var failing = new Thread(() => failed = Record.Exception(() => fresh.OpenRead().Dispose()));
        failing.Start();
        Assert.True(inAttempt.Wait(TimeSpan.FromSeconds(30)));

        var errors = new Exception?[3];
        var calls = Enumerable.Range(0, errors.Length).Select(i => new Thread(() =>
        {
            waiting.Signal();
            errors[i] = Record.Exception(() => fresh.OpenRead().Dispose());
        })).ToList();
        calls.ForEach(t => t.Start());
        calls.ForEach(t => t.Join());
        failing.Join();

        Assert.Equal("an attempt that failed with a bug", Assert.IsType<InvalidOperationException>(failed).Message);
        Assert.All(errors, error => Assert.Null(error));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void EnsureReady_DirectoryCannotBeCreated_ThrowsUnavailable()
    {
        var blocker = Path.Combine(_db.DirectoryPath, "blocker");
        File.WriteAllText(blocker, "a file where a directory should be");
        using var database = new CampaignDatabase(Path.Combine(blocker, "campaigns.db"), _db.Time);

        var error = Assert.Throws<CampaignStoreUnavailableException>(database.EnsureReady);

        Assert.Contains(blocker, error.Message);
        Assert.Contains("DND_MCP_DB", error.Message);
    }

    /// <summary>An existing directory this user cannot write: SQLite answers SQLITE_CANTOPEN, not an IOException.</summary>
    [Fact]
    public void EnsureReady_UnwritableDirectory_ThrowsUnavailableThenRecoversWhenFixed()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        var locked = Path.Combine(_db.DirectoryPath, "locked");
        Directory.CreateDirectory(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        using var database = new CampaignDatabase(Path.Combine(locked, "campaigns.db"), _db.Time);
        try
        {
            var error = Assert.Throws<CampaignStoreUnavailableException>(database.EnsureReady);

            Assert.Contains("not readable and writable", error.Message);
            Assert.Equal(14, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        database.EnsureReady();
        Assert.True(database.Exists);
    }

    /// <summary>The first write of a UTC day on a database this process did not create backs it up first.</summary>
    [Fact]
    public void Write_FirstWriteOfTheDayOnAnExistingDatabase_TakesADailyBackup()
    {
        SeedCampaign();
        using var later = _db.OtherProcess();

        later.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction));

        var backup = Assert.Single(later.Backups.List());
        Assert.Equal(CampaignBackups.Reasons.Daily, backup.Reason);
        Assert.Equal(_db.Time.GetUtcNow(), backup.At);
        using var copy = new SqliteConnection($"Data Source={backup.Path};Mode=ReadOnly;Pooling=False");
        copy.Open();
        Assert.Equal(string.Empty, copy.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
    }

    [Fact]
    public void Write_LaterWritesTheSameUtcDay_TakeNoSecondBackup()
    {
        SeedCampaign();
        using var later = _db.OtherProcess();

        for (var i = 0; i < 3; i++)
        {
            later.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction));
            _db.Time.Advance(TimeSpan.FromHours(3));
        }

        Assert.Single(later.Backups.List());
    }

    [Fact]
    public void Write_NextUtcDay_TakesAnotherDailyBackup()
    {
        SeedCampaign();
        using var later = _db.OtherProcess();
        later.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction));

        _db.Time.Advance(TimeSpan.FromHours(13));
        later.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'y'", transaction: transaction));

        Assert.Equal(2, later.Backups.List().Count(b => b.Reason == CampaignBackups.Reasons.Daily));
    }

    /// <summary>A database this process created today has nothing to lose yet.</summary>
    [Fact]
    public void Write_DatabaseCreatedByThisInstanceToday_TakesNoDailyBackup()
    {
        SeedCampaign();

        _db.Database.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction));

        Assert.Empty(_db.Database.Backups.List());
    }

    /// <summary>Another session already backed up today (any reason): once a day is enough.</summary>
    [Fact]
    public void Write_ABackupIsAlreadyStampedToday_TakesNoDailyBackup()
    {
        SeedCampaign();
        using var later = _db.OtherProcess();
        later.Backups.Create(CampaignBackups.Reasons.SessionEnd);

        later.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction));

        Assert.Equal(CampaignBackups.Reasons.SessionEnd, Assert.Single(later.Backups.List()).Reason);
    }

    /// <summary>
    /// FI2: a running process whose file another build has since moved to a newer schema takes no daily backup before its
    /// batch is refused. Its backup names and retention are not the newer build's, so its retention pass could delete
    /// backups the newer build keeps; the refusal's "nothing was changed" covers backups/ too.
    /// </summary>
    [Fact]
    public void Write_FirstWriteOfTheDayAfterAnotherProcessMovedTheFileToANewerSchema_IsRefusedAndTakesNoBackup()
    {
        SeedCampaign();
        using var later = _db.OtherProcess();
        later.EnsureReady();
        using (var newer = new SqliteConnection($"Data Source={_db.DatabasePath};Pooling=False"))
        {
            newer.Open();
            newer.Execute($"PRAGMA user_version = {CampaignDbMigrator.LatestVersion + 1}");
        }

        Assert.Throws<CampaignStoreUnavailableException>(() => later.Write((connection, transaction) =>
            connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction)));

        Assert.False(Directory.Exists(_db.BackupsPath) && Directory.EnumerateFileSystemEntries(_db.BackupsPath).Any(),
            "A backup was taken of the newer file.");
    }

    /// <summary>
    /// R03 (contract fix FI3): a backup interrupted earlier today (a server killed during its daily backup) left only a
    /// <c>.partial</c> and its journal, which is not "a backup stamped today": the first write of the day still takes a
    /// usable daily backup, and its retention pass removes the leftover.
    /// </summary>
    [Fact]
    public void Write_FirstWriteOfTheDay_OnlyAnInterruptedBackupOfTodayIsLeft_TakesAUsableDailyBackupAndRemovesTheLeftover()
    {
        SeedCampaign();
        Directory.CreateDirectory(_db.BackupsPath);
        var interrupted = Path.Combine(_db.BackupsPath,
            CampaignBackups.FileName(_db.Time.GetUtcNow().AddHours(-1), 4242, CampaignBackups.Reasons.Daily) + CampaignBackups.PartialSuffix);
        File.WriteAllText(interrupted, "SQLite format 3\0 cut off");
        File.WriteAllText(interrupted + "-journal", "a writer's journal");
        File.SetLastWriteTimeUtc(interrupted, DateTime.UtcNow.AddMinutes(-60));
        File.SetLastWriteTimeUtc(interrupted + "-journal", DateTime.UtcNow.AddMinutes(-60));
        using var later = _db.OtherProcess();

        later.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction));

        var daily = Assert.Single(later.Backups.List());
        Assert.Equal(CampaignBackups.Reasons.Daily, daily.Reason);
        using (var copy = new SqliteConnection($"Data Source={daily.Path};Mode=ReadOnly;Pooling=False"))
        {
            copy.Open();
            Assert.Equal("ok", copy.ExecuteScalar<string>("PRAGMA integrity_check"));
        }

        Assert.False(File.Exists(interrupted));
        Assert.False(File.Exists(interrupted + "-journal"));
    }

    /// <summary>A backup must never block the write it protects: a failed daily backup is a logged warning.</summary>
    [Fact]
    public void Write_DailyBackupFails_TheWriteSucceedsAndAWarningIsLogged()
    {
        SeedCampaign();
        File.WriteAllText(_db.BackupsPath, "a file where the backups directory should be");
        var logger = new ListLogger<CampaignDatabase>();
        using var later = _db.OtherProcess(logger);

        later.Write((connection, transaction) => connection.Execute("UPDATE campaign SET summary_md = 'x'", transaction: transaction));

        using var check = _db.Open();
        Assert.Equal("x", check.ExecuteScalar<string>("SELECT summary_md FROM campaign"));
        Assert.Contains(logger.Warnings, w => w.Contains("daily backup", StringComparison.Ordinal));
    }

    /// <summary>
    /// The row records bind snake_case columns only with Dapper's global underscore matching on; campaign code sets it in
    /// CampaignDatabase's static constructor, before any campaign query.
    /// </summary>
    [Fact]
    public void EnsureDapperConfigured_SetsMatchNamesWithUnderscores()
    {
        CampaignDatabase.EnsureDapperConfigured();

        Assert.True(DefaultTypeMap.MatchNamesWithUnderscores);
    }

    [Fact]
    public void Dispose_ThenUse_Throws()
    {
        var database = new CampaignDatabase(_db.DatabasePath, _db.Time);

        database.Dispose();
        database.Dispose();

        Assert.Throws<ObjectDisposedException>(database.EnsureReady);
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Seconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private string SeedCampaign()
    {
        using var connection = _db.Open();
        return new CampaignSeed(connection).Campaign().Id;
    }
}
