using System.Collections.Concurrent;
using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: a backup holds every committed row (including rows that so far live only in the -wal file), is named so
/// retention can read its time and reason, appears under that name only once complete, retention keeps exactly the
/// promised set (every pre-migration copy, the newest ten, the newest of each of the last 30 UTC days), clears what
/// interrupted backups leave and never deletes a file it did not name, and restore puts a checked backup in place after
/// saving what it replaces, losing no write another process committed meanwhile and deleting no backup, or changes
/// nothing when the backup or campaigns.db is unusable. A backup scheme that silently misses the newest data, or prunes
/// the one copy a user needed, looks fine until the day it is used.
/// </summary>
public sealed class CampaignBackupsTests : IDisposable
{
    private readonly CampaignTestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("2026-09-01T12:00:00.000+00:00", 4242, "daily", "campaigns-20260901T120000000Z-4242-daily.db")]
    [InlineData("2026-12-31T23:59:59.999+00:00", 7, "session-end", "campaigns-20261231T235959999Z-7-session-end.db")]
    [InlineData("2026-01-02T03:04:05.006+02:00", 1, "pre-migrate-v12", "campaigns-20260102T010405006Z-1-pre-migrate-v12.db")]
    [InlineData("2026-06-15T00:00:00.000+00:00", 99999, "pre-restore", "campaigns-20260615T000000000Z-99999-pre-restore.db")]
    [InlineData("2026-06-15T00:00:00.000+00:00", 5, "manual", "campaigns-20260615T000000000Z-5-manual.db")]
    public void FileName_RoundTripsThroughTryParseFileName(string at, int pid, string reason, string expected)
    {
        var stamp = DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture);

        var name = CampaignBackups.FileName(stamp, pid, reason);

        Assert.Equal(expected, name);
        Assert.True(CampaignBackups.TryParseFileName(name, out var parsedAt, out var parsedPid, out var parsedReason));
        Assert.Equal(stamp, parsedAt);
        Assert.Equal(pid, parsedPid);
        Assert.Equal(reason, parsedReason);
    }

    /// <summary>Anything not named exactly like a backup is not a backup, so retention can never delete it.</summary>
    [Theory]
    [InlineData("campaigns.db")]
    [InlineData("notes.txt")]
    [InlineData("campaigns-20260901T120000000Z-12-weekly.db")]
    [InlineData("campaigns-20260901T120000000Z-12-daily.db.bak")]
    [InlineData("campaigns-20260901T1200Z-12-daily.db")]
    [InlineData("campaigns-20261301T120000000Z-12-daily.db")]
    [InlineData("my-campaigns-20260901T120000000Z-12-daily.db")]
    [InlineData("campaigns-20260901T120000000Z--daily.db")]
    public void TryParseFileName_NotABackupName_IsFalse(string name) =>
        Assert.False(CampaignBackups.TryParseFileName(name, out _, out _, out _));

    [Theory]
    [InlineData("weekly")]
    [InlineData("pre-migrate")]
    [InlineData("pre-migrate-vx")]
    [InlineData("../escape")]
    public void Create_UnknownReason_Throws(string reason) =>
        Assert.Throws<ArgumentException>(() => _db.Database.Backups.Create(reason));

    /// <summary>
    /// The reason backups are VACUUM INTO and not file copies: rows committed while another connection keeps the WAL
    /// from being checkpointed exist only in campaigns.db-wal, and the backup still has them.
    /// </summary>
    [Fact]
    public void Create_RowsOnlyInTheWal_AreInTheBackup()
    {
        using var live = _db.Open();
        live.Execute("PRAGMA wal_autocheckpoint = 0");
        live.ExecuteScalar<string>("PRAGMA wal_checkpoint(TRUNCATE)");
        new CampaignSeed(live).Campaign(name: "The Listen");
        Assert.True(new FileInfo(_db.DatabasePath + "-wal").Length > 0, "Setup failed: nothing is in the WAL.");

        var path = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);

        using var copy = OpenReadOnly(path);
        Assert.Equal("the-listen", copy.ExecuteScalar<string>("SELECT slug FROM campaign"));
        Assert.Equal("ok", copy.ExecuteScalar<string>("PRAGMA integrity_check"));
        Assert.Equal("delete", copy.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.False(File.Exists(path + "-wal"));
    }

    /// <summary>VACUUM INTO refuses an existing target, so two backups in one millisecond must still get two names.</summary>
    [Fact]
    public void Create_TwiceInTheSameMillisecond_GivesTwoFiles()
    {
        var first = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        var second = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(2, _db.Database.Backups.List().Count);
    }

    /// <summary>
    /// FI3: a name is taken while its <c>.partial</c> exists (another backup of this process and millisecond is still being
    /// written into it), exactly as when the finished file exists: the backup takes the next millisecond's name and leaves
    /// the other file alone. Taking the name would have VACUUM INTO refuse the existing target, and the failed backup's
    /// clean-up would then delete the other backup's file in the middle of its copy.
    /// </summary>
    [Fact]
    public void Create_ANameWhoseBackupIsStillBeingWritten_TakesTheNextMillisecondsNameAndLeavesThatFileAlone()
    {
        Directory.CreateDirectory(_db.BackupsPath);
        var now = _db.Time.GetUtcNow();
        var inProgress = Path.Combine(_db.BackupsPath,
            CampaignBackups.FileName(now, Environment.ProcessId, CampaignBackups.Reasons.Manual) + CampaignBackups.PartialSuffix);
        File.WriteAllText(inProgress, "another backup, half written");

        var path = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);

        Assert.Equal(CampaignBackups.FileName(now.AddMilliseconds(1), Environment.ProcessId, CampaignBackups.Reasons.Manual), Path.GetFileName(path));
        Assert.Equal("another backup, half written", File.ReadAllText(inProgress));
        using var copy = OpenReadOnly(path);
        Assert.Equal("ok", copy.ExecuteScalar<string>("PRAGMA integrity_check"));
    }

    [Fact]
    public void Create_NoDatabaseFile_ThrowsFileNotFound()
    {
        using var empty = new CampaignTestDb(create: false);

        Assert.Throws<FileNotFoundException>(() => empty.Database.Backups.Create(CampaignBackups.Reasons.Manual));
    }

    [Fact]
    public void List_NewestFirst_WithSizesAndReasons()
    {
        _db.Database.Backups.Create(CampaignBackups.Reasons.Daily);
        _db.Time.Advance(TimeSpan.FromMinutes(5));
        _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);

        var list = _db.Database.Backups.List();

        Assert.Equal(new[] { "manual", "daily" }, list.Select(b => b.Reason));
        Assert.All(list, b => Assert.True(b.SizeBytes > 0));
        Assert.All(list, b => Assert.Equal(Environment.ProcessId, b.ProcessId));
        Assert.Equal(_db.Time.GetUtcNow(), list[0].At);
    }

    /// <summary>
    /// 40 simulated days, a daily backup at 09:00 and a session-end at 21:00 each day, pre-migration copies on days 1 and
    /// 20: every pre-migration copy stays, the 10 newest stay, the newest of each of the last 30 UTC days stays, and
    /// nothing else.
    /// </summary>
    [Fact]
    public void ApplyRetention_FortySimulatedDays_KeepsExactlyThePromisedSet()
    {
        var start = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        var made = new List<(DateTimeOffset At, string Reason)>();
        for (var day = 0; day < 40; day++)
        {
            _db.Time.SetUtcNow(start.AddDays(day));
            made.Add((_db.Time.GetUtcNow(), "daily"));
            _db.Database.Backups.Create(CampaignBackups.Reasons.Daily);
            if (day is 1 or 20)
            {
                made.Add((_db.Time.GetUtcNow().AddMinutes(1), $"pre-migrate-v{day}"));
                _db.Time.Advance(TimeSpan.FromMinutes(1));
                _db.Database.Backups.Create(CampaignBackups.PreMigrateReason(day));
            }

            _db.Time.SetUtcNow(start.AddDays(day).AddHours(12));
            made.Add((_db.Time.GetUtcNow(), "session-end"));
            _db.Database.Backups.Create(CampaignBackups.Reasons.SessionEnd);
        }

        var today = DateOnly.FromDateTime(_db.Time.GetUtcNow().UtcDateTime);
        var rest = made.Where(m => !m.Reason.StartsWith("pre-migrate", StringComparison.Ordinal)).OrderByDescending(m => m.At).ToList();
        var expected = made.Where(m => m.Reason.StartsWith("pre-migrate", StringComparison.Ordinal))
            .Concat(rest.Take(10))
            .Concat(rest.GroupBy(m => DateOnly.FromDateTime(m.At.UtcDateTime))
                .Where(g => g.Key > today.AddDays(-30))
                .Select(g => g.First()))
            .Select(m => (m.At, m.Reason))
            .Distinct()
            .OrderByDescending(m => m.At)
            .ToList();

        var kept = _db.Database.Backups.List().Select(b => (b.At, b.Reason)).ToList();

        Assert.Equal(expected, kept);
        Assert.Equal(2, kept.Count(k => k.Reason.StartsWith("pre-migrate", StringComparison.Ordinal)));
        Assert.Equal(30, kept.Where(k => !k.Reason.StartsWith("pre-migrate", StringComparison.Ordinal))
            .Select(k => DateOnly.FromDateTime(k.At.UtcDateTime)).Distinct().Count());
        Assert.Equal(2 + 10 + 25, kept.Count);
    }

    /// <summary>Retention deletes only files it named: whatever else a user keeps in backups/ is theirs.</summary>
    [Fact]
    public void ApplyRetention_FilesNotNamedLikeBackups_AreNeverDeleted()
    {
        Directory.CreateDirectory(_db.BackupsPath);
        var foreign = new[]
        {
            "notes.txt", "campaigns.db", "campaigns-20200101T000000000Z-1-weekly.db", "my-copy.db",
            "campaigns-20200101T000000000Z-1-daily.db.keep",
        };
        foreach (var name in foreign)
        {
            File.WriteAllText(Path.Combine(_db.BackupsPath, name), "mine");
        }

        for (var i = 0; i < 15; i++)
        {
            _db.Time.Advance(TimeSpan.FromDays(3));
            _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        }

        Assert.All(foreign, name => Assert.True(File.Exists(Path.Combine(_db.BackupsPath, name)), name));
        Assert.True(_db.Database.Backups.List().Count < 15);
    }

    [Fact]
    public void Restore_ABackup_ReplacesTheDataAndSavesWhatItReplaced()
    {
        string campaignId;
        using (var connection = _db.Open())
        {
            campaignId = new CampaignSeed(connection).Campaign(name: "Before").Id;
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        _db.Time.Advance(TimeSpan.FromMinutes(1));
        using (var connection = _db.Open())
        {
            connection.Execute("UPDATE campaign SET name = 'After' WHERE id = @campaignId", new { campaignId });
        }

        var result = _db.Database.Backups.Restore(backup);

        Assert.Equal(backup, result.RestoredFrom);
        Assert.Equal(_db.DatabasePath, result.DatabasePath);
        Assert.Equal(new[] { "before" }, result.CampaignSlugs);
        Assert.Equal(CampaignDbMigrator.LatestVersion, result.SchemaVersion);
        Assert.Equal(CampaignDbMigrator.LatestVersion, result.BackupSchemaVersion);
        using (var connection = _db.Open())
        {
            Assert.Equal("Before", connection.ExecuteScalar<string>("SELECT name FROM campaign"));
            Assert.Equal("wal", connection.ExecuteScalar<string>("PRAGMA journal_mode"));
        }

        Assert.NotNull(result.PreviousSavedTo);
        Assert.EndsWith("-pre-restore.db", result.PreviousSavedTo, StringComparison.Ordinal);
        using var previous = OpenReadOnly(result.PreviousSavedTo);
        Assert.Equal("After", previous.ExecuteScalar<string>("SELECT name FROM campaign"));
    }

    /// <summary>The online backup API copies under SQLite's locks, so a restore works while another session has the file open.</summary>
    [Fact]
    public void Restore_WhileAnotherConnectionIsOpen_TheOtherConnectionSeesTheRestoredData()
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign(name: "Kept");
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        using var other = _db.Open();
        other.Execute("DELETE FROM campaign");

        _db.Database.Backups.Restore(backup);

        Assert.Equal("Kept", other.ExecuteScalar<string>("SELECT name FROM campaign"));
    }

    [Fact]
    public void Restore_NoCampaignsDatabaseYet_CreatesItWithoutAPreRestoreCopy()
    {
        using (var seed = _db.Open())
        {
            new CampaignSeed(seed).Campaign(name: "Moved");
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        using var fresh = new CampaignTestDb(create: false);
        var target = Path.Combine(fresh.DirectoryPath, "elsewhere", "campaigns.db");
        using var database = new CampaignDatabase(target, fresh.Time);

        var result = database.Backups.Restore(backup);

        Assert.Null(result.PreviousSavedTo);
        using var connection = database.OpenRead();
        Assert.Equal("Moved", connection.ExecuteScalar<string>("SELECT name FROM campaign"));
    }

    public static TheoryData<string, string> Unrestorable() => new()
    {
        { "text", "is not a SQLite database" },
        { "other-sqlite", "not a dnd-mcp campaigns database" },
        { "damaged-pages", "fails SQLite's integrity check" },
        { "unversioned", "has no campaigns schema version" },
        { "newer", "newer version of dnd-mcp" },
        { "missing", "There is no backup file" },
        { "self", "campaigns.db itself" },
        { "interrupted", "is an incomplete backup (it was interrupted while being written)" },
        { "interrupted-full-length", "is an incomplete backup (it was interrupted while being written)" },
        { "unreadable", "cannot be opened" },
        { "other-page-size", "uses 8192-byte pages" },
        { "locked", "is locked by another program, so it is not restored. Close it and try again, or choose another backup from" },
    };

    /// <summary>
    /// A wrong file is refused before anything happens: campaigns.db is untouched and no pre-restore copy is made. The
    /// message is about that file, never campaigns.db: R06 (contract fix FI6) found an interrupted backup (truncated, its
    /// writer's hot journal beside it, which a read-only open cannot roll back) reported as "cannot restore into
    /// campaigns.db: attempt to write a readonly database", sending the user to fix a database that was fine. The
    /// "interrupted" cases make that leftover; "unreadable" is a backup this user may not read (SQLITE_CANTOPEN); a backup
    /// with another page size cannot be copied into a WAL campaigns.db (SQLite says SQLITE_READONLY, which would blame
    /// campaigns.db's permissions). "locked" is a backup another program holds an exclusive lock on (a sqlite3 shell or a
    /// database browser with it open in a write transaction): its integrity check waits out busy_timeout and gets
    /// SQLITE_BUSY, which escaped as a raw SqliteException that the CLI reported as "cannot restore into campaigns.db:
    /// SQLite Error 5: 'database is locked'" (RR02).
    /// </summary>
    [Theory]
    [MemberData(nameof(Unrestorable))]
    public void Restore_NotAUsableBackup_IsRefusedAndChangesNothing(string kind, string message)
    {
        if (kind == "unreadable" && (OperatingSystem.IsWindows() || Environment.UserName == "root"))
        {
            return;
        }

        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign(name: "Untouched");
        }

        var path = Path.Combine(_db.DirectoryPath, "candidate.db");
        SqliteConnection? holder = null;
        switch (kind)
        {
            case "text":
                File.WriteAllText(path, new string('x', 8192));
                break;
            case "other-sqlite":
                using (var other = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    other.Open();
                    other.Execute("CREATE TABLE recipe (name TEXT)");
                }

                break;
            case "damaged-pages":
                File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
                DamageCampaignSlugIndex(path, "untouched");
                break;
            case "unversioned":
                using (var unversioned = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    unversioned.Open();
                    unversioned.Execute("CREATE TABLE campaign (id TEXT PRIMARY KEY, slug TEXT)");
                }

                break;
            case "newer":
                File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
                using (var newer = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    newer.Open();
                    newer.Execute($"PRAGMA user_version = {CampaignDbMigrator.LatestVersion + 1}");
                }

                break;
            case "self":
                path = _db.DatabasePath;
                break;
            case "interrupted":
            case "interrupted-full-length":
                File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
                if (kind == "interrupted")
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
                    file.SetLength(file.Length / 2);
                }

                WriteHotJournal(path + "-journal");
                break;
            case "unreadable":
                File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
                File.SetUnixFileMode(path, UnixFileMode.None);
                break;
            case "other-page-size":
                File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
                using (var resized = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    resized.Open();
                    resized.Execute("PRAGMA page_size = 8192; VACUUM;");
                }

                break;
            case "locked":
                File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
                holder = LockExclusively(path);
                break;
        }

        var backupsBefore = _db.Database.Backups.List().Count;
        var bytesBefore = File.ReadAllBytes(_db.DatabasePath);

        DndInputException error;
        try
        {
            error = Assert.Throws<DndInputException>(() => _db.Database.Backups.Restore(path));
        }
        finally
        {
            holder?.Dispose();
        }

        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("SQLite Error", error.Message, StringComparison.Ordinal);
        if (kind != "self")
        {
            Assert.Contains(path, error.Message);
            Assert.DoesNotContain(_db.DatabasePath, error.Message);
        }

        Assert.Equal(backupsBefore, _db.Database.Backups.List().Count);
        Assert.Equal(bytesBefore, File.ReadAllBytes(_db.DatabasePath));
        using var connection2 = _db.Open();
        Assert.Equal("Untouched", connection2.ExecuteScalar<string>("SELECT name FROM campaign"));
    }

    /// <summary>
    /// FI1: the copy re-checks the backup it is about to read, because the online backup's first step (the one that takes
    /// campaigns.db's lock) copies an empty source in full: it would reset campaigns.db to an empty database before the
    /// pre-restore copy exists. A backup emptied after it passed the checks, here while the restore waits for this
    /// process's write lock, is refused naming it, and nothing changes.
    /// </summary>
    [Fact]
    public void Restore_BackupEmptiedAfterItsChecks_IsRefusedAndChangesNothing()
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign(name: "Untouched");
        }

        var path = Path.Combine(_db.DirectoryPath, "candidate.db");
        File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
        var backupsBefore = _db.Database.Backups.List().Count;
        Exception? error = null;
        var restore = new Thread(() => error = Record.Exception(() => _db.Database.Backups.Restore(path)));

        _db.Database.RunExclusive(() =>
        {
            restore.Start();
            // The restore checks the backup, then waits for the process's write lock, which is held here.
            Assert.True(SpinWait.SpinUntil(() => (restore.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)),
                "The restore never waited for the write lock.");
            File.WriteAllBytes(path, []);
            return 0;
        });
        restore.Join();

        var refused = Assert.IsType<DndInputException>(error);
        Assert.Contains(path, refused.Message);
        Assert.DoesNotContain(_db.DatabasePath, refused.Message);
        Assert.Equal(backupsBefore, _db.Database.Backups.List().Count);
        using var check = _db.Open();
        Assert.Equal("Untouched", check.ExecuteScalar<string>("SELECT name FROM campaign"));
    }

    /// <summary>
    /// A backup another program locks after it passed the checks (here while the restore waits for this process's write
    /// lock) is refused naming it, and nothing changes. Its SQLITE_BUSY used to surface while the copy took campaigns.db's
    /// lock, refused as campaigns.db "locked by another dnd-mcp process": the wrong file and the wrong program.
    /// </summary>
    [Fact]
    public void Restore_BackupLockedByAnotherProgramAfterItsChecks_IsRefusedNamingItAndChangesNothing()
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign(name: "Untouched");
        }

        var path = Path.Combine(_db.DirectoryPath, "candidate.db");
        File.Copy(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual), path);
        var backupsBefore = _db.Database.Backups.List().Count;
        var bytesBefore = File.ReadAllBytes(_db.DatabasePath);
        Exception? error = null;
        var restore = new Thread(() => error = Record.Exception(() => _db.Database.Backups.Restore(path)));
        SqliteConnection? holder = null;
        try
        {
            _db.Database.RunExclusive(() =>
            {
                restore.Start();
                // The restore checks the backup, then waits for the process's write lock, which is held here.
                Assert.True(SpinWait.SpinUntil(() => (restore.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30)),
                    "The restore never waited for the write lock.");
                holder = LockExclusively(path);
                return 0;
            });
            restore.Join();
        }
        finally
        {
            holder?.Dispose();
        }

        var refused = Assert.IsType<DndInputException>(error);
        Assert.Equal(
            $"{path} is locked by another program, so it is not restored. Close it and try again, or choose another backup " +
            $"from {_db.Database.Backups.DirectoryPath}.",
            refused.Message);
        Assert.Equal(backupsBefore, _db.Database.Backups.List().Count);
        Assert.Equal(bytesBefore, File.ReadAllBytes(_db.DatabasePath));
    }

    /// <summary>
    /// A restore over a damaged campaigns.db is refused before anything changes, with the advice that works there: move the
    /// damaged file aside (with its -wal and -shm) and restore again. The store's usual damaged-file advice ("restore a
    /// backup with dnd-mcp's restore command") would send the user back to the command that just failed. A file that is
    /// not a database at all fails at the restore's first read of it, while taking the lock; one with a damaged table page
    /// fails while the pre-restore copy reads that page, and leaves no partial copy behind.
    /// </summary>
    [Theory]
    [InlineData("not-a-database")]
    [InlineData("damaged-table-page")]
    public void Restore_CampaignsDbIsDamaged_IsRefusedWithMoveItAsideAndChangesNothing(string damage)
    {
        using (var connection = _db.Open())
        {
            var seed = new CampaignSeed(connection);
            var campaign = seed.Campaign();
            for (var i = 0; i < 50; i++)
            {
                seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Hero " + i.ToString(CultureInfo.InvariantCulture), body: new string('x', 500));
            }
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        if (damage == "not-a-database")
        {
            File.Delete(_db.DatabasePath + "-wal");
            File.Delete(_db.DatabasePath + "-shm");
            var noise = new byte[8192];
            new Random(3).NextBytes(noise);
            File.WriteAllBytes(_db.DatabasePath, noise);
        }
        else
        {
            _db.ScrambleRootPage("entity");
        }

        var before = File.ReadAllBytes(_db.DatabasePath);
        var backupsBefore = Directory.GetFiles(_db.BackupsPath).Order(StringComparer.Ordinal).ToList();

        var error = Assert.Throws<CampaignStoreUnavailableException>(() => _db.Database.Backups.Restore(backup));

        Assert.StartsWith($"Could not save the current campaigns.db at {_db.DatabasePath} before restoring, so nothing was changed.", error.Message);
        Assert.Contains("move it (with any campaigns.db-wal and campaigns.db-shm beside it) somewhere safe and run the restore again", error.Message);
        Assert.DoesNotContain("restore command", error.Message);
        Assert.Equal(before, File.ReadAllBytes(_db.DatabasePath));
        Assert.Equal(backupsBefore, Directory.GetFiles(_db.BackupsPath).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// A backup older than the build is brought up to the build's schema on the way in (the migrator's normal path, with a
    /// pre-migrate copy of the restored data), so a restore never leaves a database this build cannot read. The backup is
    /// at the newest embedded version, so the build here is the embedded migrations plus one test migration after them.
    /// </summary>
    [Fact]
    public void Restore_BackupOlderThanTheBuild_IsMigratedToTheBuildsVersion()
    {
        using (var seed = _db.Open())
        {
            new CampaignSeed(seed).Campaign(name: "Old Times");
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        var build = BuildWithANewerMigration();

        var result = _db.Database.Backups.Restore(backup, build);

        Assert.Equal(CampaignDbMigrator.LatestVersion, result.BackupSchemaVersion);
        Assert.Equal(NewerVersion, result.SchemaVersion);
        Assert.Single(result.CampaignSlugs);
        using var connection = OpenReadOnly(_db.DatabasePath);
        Assert.Equal(NewerVersion, connection.ExecuteScalar<long>("PRAGMA user_version"));
        Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'extra_note'"));
        Assert.Equal("Old Times", connection.ExecuteScalar<string>("SELECT name FROM campaign"));
        Assert.Contains(_db.Database.Backups.List(), b => b.Reason == CampaignBackups.PreMigrateReason(NewerVersion));
    }

    /// <summary>
    /// A SQLite error after the backup is copied in (here as an older backup is brought up to the build's schema) says what
    /// happened: the backup is in, and the pre-restore file puts back what campaigns.db held. As a raw SqliteException it
    /// reached the CLI's branch for failures nobody explained, which says nothing was changed: here that is not true. With
    /// no campaigns.db before the restore there is no pre-restore file to name, and the message offers only the retry.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Restore_SqliteErrorAfterTheBackupIsCopiedIn_SaysTheBackupIsInAndNamesThePreRestoreFile(bool campaignsDbExisted)
    {
        using (var seed = _db.Open())
        {
            new CampaignSeed(seed).Campaign(name: "Old Times");
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        var build = BuildWithANewerMigration();
        build.BeforeBegin = _ => throw new SqliteException("disk I/O error", 10);
        using var elsewhere = new CampaignTestDb(create: false);
        using var target = campaignsDbExisted
            ? null
            : new CampaignDatabase(Path.Combine(elsewhere.DirectoryPath, "elsewhere", "campaigns.db"), elsewhere.Time);
        var database = target ?? _db.Database;

        var error = Assert.Throws<CampaignStoreUnavailableException>(() => database.Backups.Restore(backup, build));

        var copiedIn = $"{backup} was copied into campaigns.db at {database.Path}, but finishing the restore (updating its " +
            "schema and journal mode) failed. Run the restore again";
        if (campaignsDbExisted)
        {
            var previous = Assert.Single(database.Backups.List(), b => b.Reason == CampaignBackups.Reasons.PreRestore).Path;
            Assert.Equal($"{copiedIn}, or restore {previous} to put back what campaigns.db held before.", error.Message);
        }
        else
        {
            Assert.DoesNotContain(database.Backups.List(), b => b.Reason == CampaignBackups.Reasons.PreRestore);
            Assert.Equal($"{copiedIn}.", error.Message);
        }

        Assert.IsType<SqliteException>(error.InnerException);
    }

    /// <summary>
    /// The restore source may be a database another process has open in WAL mode: its connection waits on busy_timeout
    /// like every campaign connection, instead of failing at once with a raw SQLITE_BUSY mid-copy.
    /// </summary>
    [Fact]
    public void OpenReadOnly_RestoreSource_HasTheHouseBusyTimeout()
    {
        using var connection = CampaignBackups.OpenReadOnly(_db.DatabasePath);

        Assert.Equal((long)CampaignDatabase.BusyTimeoutMilliseconds, connection.ExecuteScalar<long>("PRAGMA busy_timeout"));
    }

    /// <summary>
    /// R01 (contract fix FI1): another session's server keeps committing batches while the command line restores. Every
    /// batch its caller was told had committed is afterwards in the restored database (it waited for the restore's lock
    /// and landed after the copy-in) or in the pre-restore backup (it committed before the lock); one that could not wait
    /// long enough was refused to its caller. Before the fix the pre-restore copy was taken without the lock, so the
    /// batches committed during it, or between it and the copy-in, were in neither file: acknowledged, then gone. Both
    /// journal modes, because each holds the lock its own way. The writer never pauses, so it can starve the restore of
    /// the lock (SQLite's locks are not fair); a restore refused that way changed nothing and is tried again, as its message
    /// tells the user to.
    /// </summary>
    [Theory]
    [InlineData("wal")]
    [InlineData("delete")]
    public async Task Restore_AnotherProcessCommitsThroughout_EveryCommittedBatchIsInTheRestoredDatabaseOrThePreRestoreBackup(string journalMode)
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign();
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        AddBallast(megabytes: 4);
        using var other = _db.OtherProcess();
        other.EnsureReady();
        SetJournalMode(journalMode);
        var committed = new ConcurrentQueue<string>();
        var stop = 0;
        var writer = Task.Run(() =>
        {
            for (var i = 0; Volatile.Read(ref stop) == 0; i++)
            {
                var key = "marker-" + i.ToString(CultureInfo.InvariantCulture);
                try
                {
                    other.Write((connection, transaction) =>
                        connection.Execute("INSERT INTO app_state(key, value) VALUES (@key, 'x')", new { key }, transaction));
                    committed.Enqueue(key);
                }
                catch (CampaignStoreUnavailableException)
                {
                    // Refused to its caller (the lock was held past busy_timeout): not acknowledged, so not lost.
                }
            }
        });
        await WaitUntil(() => committed.Count >= 20);

        var result = RestoreTryingAgainWhileLocked(backup);
        var committedByThen = committed.Count;
        await WaitUntil(() => committed.Count >= committedByThen + 20);
        Volatile.Write(ref stop, 1);
        await writer;

        var kept = Markers(_db.DatabasePath);
        kept.UnionWith(Markers(result.PreviousSavedTo!));
        var lost = committed.Where(key => !kept.Contains(key)).ToList();
        Assert.True(lost.Count == 0,
            $"{lost.Count} of {committed.Count} committed batches are in neither file, e.g. {string.Join(", ", lost.Take(5))}");
    }

    /// <summary>
    /// FI1: a restore that starts while another process is in the middle of a batch waits for that batch without
    /// stopping it from committing; the batch is then in the pre-restore copy (before the fix it was in neither file).
    /// In rollback-journal mode this pins the order the restore takes its lock in: waiting for it in exclusive locking
    /// mode keeps the SHARED lock the other batch's commit is waiting on, so both time out (a deadlock).
    /// </summary>
    [Theory]
    [InlineData("wal")]
    [InlineData("delete")]
    public async Task Restore_AnotherProcessIsMidBatch_TheBatchCommitsIntoThePreRestoreCopyAndTheRestoreFollows(string journalMode)
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign();
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        SetJournalMode(journalMode);
        using var writer = new SqliteConnection(CampaignDatabase.ConnectionString(_db.DatabasePath, SqliteOpenMode.ReadWrite));
        writer.Open();
        writer.Execute($"PRAGMA busy_timeout = {CampaignDatabase.BusyTimeoutMilliseconds}");
        using var batch = writer.BeginTransaction();
        writer.Execute("INSERT INTO app_state(key, value) VALUES ('marker-mid-batch', 'x')", transaction: batch);
        var restore = Task.Run(() => _db.Database.Backups.Restore(backup));
        await Task.Delay(300);
        Assert.False(restore.IsCompleted, "The restore did not wait for the batch in progress.");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        batch.Commit();
        var committedIn = clock.Elapsed;
        var result = await restore;

        Assert.True(committedIn < TimeSpan.FromSeconds(2), $"The batch's commit waited {committedIn} on the restore.");
        Assert.Contains("marker-mid-batch", Markers(result.PreviousSavedTo!));
        Assert.DoesNotContain("marker-mid-batch", Markers(_db.DatabasePath));
    }

    /// <summary>
    /// FI1, the mechanism: between the pre-restore copy and the copy-in, the restore holds campaigns.db's write lock, so
    /// another process can still read the database as it was but cannot begin a write, and nothing can commit that the
    /// pre-restore copy would miss. In WAL mode the online backup's first step holds the WAL write lock; in
    /// rollback-journal mode a RESERVED lock held through exclusive locking mode (an EXCLUSIVE one would block the
    /// pre-restore copy's own read).
    /// </summary>
    [Theory]
    [InlineData("wal")]
    [InlineData("delete")]
    public void Restore_BetweenThePreRestoreCopyAndTheCopyIn_AnotherProcessCanReadButNotBeginAWrite(string journalMode)
    {
        string campaignId;
        using (var connection = _db.Open())
        {
            campaignId = new CampaignSeed(connection).Campaign(name: "Before").Id;
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        using (var connection = _db.Open())
        {
            connection.Execute("UPDATE campaign SET name = 'After' WHERE id = @campaignId", new { campaignId });
        }

        SetJournalMode(journalMode);
        string? seen = null;
        var refusal = 0;
        string? savedTo = null;
        _db.Database.Backups.WhileRestoreHoldsTheLock = previous =>
        {
            savedTo = previous;
            using var reader = OpenImpatient(_db.DatabasePath);
            seen = reader.ExecuteScalar<string>("SELECT name FROM campaign");
            using var writer = OpenImpatient(_db.DatabasePath);
            refusal = Assert.Throws<SqliteException>(() => writer.Execute("BEGIN IMMEDIATE")).SqliteErrorCode;
        };

        var result = _db.Database.Backups.Restore(backup);

        Assert.Equal("After", seen);
        Assert.Equal(5, refusal);
        Assert.Equal(result.PreviousSavedTo, savedTo);
        using var previousCopy = OpenReadOnly(result.PreviousSavedTo!);
        Assert.Equal("After", previousCopy.ExecuteScalar<string>("SELECT name FROM campaign"));
        using var restored = _db.Open();
        Assert.Equal("Before", restored.ExecuteScalar<string>("SELECT name FROM campaign"));
    }

    /// <summary>
    /// The backup is held in a read transaction for the copy-in: another program that tries to lock it meanwhile (a
    /// sqlite3 shell beginning a write) is kept waiting, and the backup is copied in as it was. A lock taken during the
    /// copy used to fail the copy-in with SQLITE_BUSY, refused as "campaigns.db … is locked by another dnd-mcp process":
    /// the wrong file and the wrong program (a backup locked before the copy is refused by name: the "locked" row and
    /// Restore_BackupLockedByAnotherProgramAfterItsChecks). Both journal modes of campaigns.db, as the copy-in takes its
    /// lock differently in each.
    /// </summary>
    [Theory]
    [InlineData("wal")]
    [InlineData("delete")]
    public void Restore_AnotherProgramLockingTheBackupDuringTheCopy_WaitsAndTheBackupIsCopiedInAsItWas(string journalMode)
    {
        string campaignId;
        using (var connection = _db.Open())
        {
            campaignId = new CampaignSeed(connection).Campaign(name: "Backed up").Id;
        }

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        using (var connection = _db.Open())
        {
            connection.Execute("UPDATE campaign SET name = 'Current' WHERE id = @campaignId", new { campaignId });
        }

        SetJournalMode(journalMode);
        int? refusal = null;
        _db.Database.Backups.WhileRestoreHoldsTheLock = _ =>
        {
            using var other = OpenImpatient(backup);
            refusal = Assert.Throws<SqliteException>(() =>
            {
                other.Execute("PRAGMA locking_mode = EXCLUSIVE");
                other.Execute("BEGIN EXCLUSIVE");
                other.Execute("UPDATE campaign SET name = 'Changed during the copy'");
                other.Execute("COMMIT");
            }).SqliteErrorCode;
        };

        _db.Database.Backups.Restore(backup);

        Assert.Equal(5, refusal);
        using var restored = _db.Open();
        Assert.Equal("Backed up", restored.ExecuteScalar<string>("SELECT name FROM campaign"));
        using var held = OpenReadOnly(backup);
        Assert.Equal("Backed up", held.ExecuteScalar<string>("SELECT name FROM campaign"));
    }

    /// <summary>
    /// R05 (contract fix FI5): a restore deletes no backup. Restoring from the oldest of the ten kept backups used to
    /// delete that very file in the retention pass after the pre-restore copy was added (and the retention after migrating
    /// an older backup would too), so the printed "restored from" path was gone and that point could not be restored again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_FromTheOldestOfTheKeptBackups_DeletesNoBackup(bool olderThanTheBuild)
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign();
        }

        var made = new List<string>();
        for (var i = 0; i < CampaignBackups.KeepNewest; i++)
        {
            _db.Time.Advance(TimeSpan.FromMinutes(1));
            made.Add(_db.Database.Backups.Create(CampaignBackups.Reasons.Manual));
        }

        _db.Time.Advance(TimeSpan.FromMinutes(1));
        var build = olderThanTheBuild ? BuildWithANewerMigration() : new CampaignDbMigrator();

        var result = _db.Database.Backups.Restore(made[0], build);

        Assert.Equal(made[0], result.RestoredFrom);
        Assert.All(made, path => Assert.True(File.Exists(path), $"the restore deleted {path}"));
        Assert.Equal(olderThanTheBuild ? NewerVersion : CampaignDbMigrator.LatestVersion, result.SchemaVersion);
    }

    /// <summary>
    /// R03 (contract fix FI3): a backup appears under its name only once it is complete (it is written as
    /// <c>&lt;name&gt;.partial</c> and renamed). A watcher listing backups/ while a backup is written sees no file of that
    /// name or the finished one, never a growing one; before, a process killed mid-backup left that truncated file
    /// standing for a backup (counted as today's, kept by retention, unrestorable).
    /// </summary>
    [Fact]
    public async Task Create_WhileTheBackupIsBeingWritten_ItsNameNeverHoldsAnUnfinishedFile()
    {
        using (var connection = _db.Open())
        {
            new CampaignSeed(connection).Campaign();
        }

        AddBallast(megabytes: 16);
        Directory.CreateDirectory(_db.BackupsPath);
        var seen = new ConcurrentQueue<(string Name, long Size)>();
        var stop = 0;
        var watcher = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                foreach (var path in Directory.EnumerateFiles(_db.BackupsPath, "campaigns-*.db"))
                {
                    try
                    {
                        seen.Enqueue((Path.GetFileName(path), new FileInfo(path).Length));
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        });

        var backup = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        Volatile.Write(ref stop, 1);
        await watcher;

        var finished = (Path.GetFileName(backup), new FileInfo(backup).Length);
        Assert.All(seen, observed => Assert.Equal(finished, observed));
        Assert.Empty(Directory.EnumerateFiles(_db.BackupsPath, "*" + CampaignBackups.PartialSuffix + "*"));
    }

    /// <summary>
    /// R03 (FI3): what interrupted backups leave (a <c>.partial</c> and its journal) and a journal whose backup is gone
    /// (including a <c>.partial</c>'s, whose file a failed backup removed) are removed by the next retention pass once a
    /// minute old. A younger leftover may belong to another process's backup in progress and stays, as do a journal whose
    /// backup still exists and anything not named like a backup's. None of them is ever listed as a backup.
    /// </summary>
    [Fact]
    public void ApplyRetention_LeftoversOfInterruptedBackups_AreRemovedOnceAMinuteOld()
    {
        var withJournal = _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);
        var stale = DateTime.UtcNow.AddMinutes(-2);
        string Backup(int minutesAgo, string reason) =>
            Path.Combine(_db.BackupsPath, CampaignBackups.FileName(_db.Time.GetUtcNow().AddMinutes(-minutesAgo), 4242, reason));
        var interrupted = Leftover(Backup(30, CampaignBackups.Reasons.Daily) + ".partial", stale);
        var interruptedJournal = Leftover(Backup(30, CampaignBackups.Reasons.Daily) + ".partial-journal", stale);
        var orphanJournal = Leftover(Backup(90, CampaignBackups.Reasons.SessionEnd) + "-journal", stale);
        var orphanPartialJournal = Leftover(Backup(60, CampaignBackups.Reasons.Daily) + ".partial-journal", stale);
        var youngOrphanJournal = Leftover(Backup(2, CampaignBackups.Reasons.SessionEnd) + "-journal", DateTime.UtcNow);
        var inProgress = Leftover(Backup(0, CampaignBackups.Reasons.Manual) + ".partial", DateTime.UtcNow);
        var journalOfABackup = Leftover(withJournal + "-journal", stale);
        var theUsers = new[]
        {
            Leftover(Path.Combine(_db.BackupsPath, "notes.partial"), stale),
            Leftover(Path.Combine(_db.BackupsPath, "my-copy.db-journal"), stale),
            Leftover(Path.Combine(_db.BackupsPath, "campaigns-20200101T000000000Z-1-weekly.db.partial"), stale),
        };
        Assert.DoesNotContain(_db.Database.Backups.List(), b => b.Path.Contains(".partial", StringComparison.Ordinal));

        _db.Time.Advance(TimeSpan.FromMinutes(1));
        _db.Database.Backups.Create(CampaignBackups.Reasons.Manual);

        Assert.False(File.Exists(interrupted));
        Assert.False(File.Exists(interruptedJournal));
        Assert.False(File.Exists(orphanJournal));
        Assert.False(File.Exists(orphanPartialJournal));
        Assert.True(File.Exists(youngOrphanJournal));
        Assert.True(File.Exists(inProgress));
        Assert.True(File.Exists(journalOfABackup));
        Assert.All(theUsers, path => Assert.True(File.Exists(path), path));
        Assert.Equal(2, _db.Database.Backups.List().Count);
    }

    // Flips one byte of a campaign's slug inside the slug index's page only. The file still opens, still has its campaign
    // table and schema version, and reads fine by rowid; only its index no longer matches its table, which nothing but
    // PRAGMA integrity_check notices (the damage a torn copy or a bad sector leaves).
    private static void DamageCampaignSlugIndex(string path, string slug)
    {
        long pageSize;
        List<long> roots;
        using (var connection = OpenReadOnly(path))
        {
            pageSize = connection.ExecuteScalar<long>("PRAGMA page_size");
            roots = connection.Query<long>("SELECT rootpage FROM sqlite_master WHERE type = 'index' AND tbl_name = 'campaign'").ToList();
        }

        var bytes = File.ReadAllBytes(path);
        var needle = System.Text.Encoding.UTF8.GetBytes(slug);
        foreach (var root in roots)
        {
            var page = bytes.AsSpan(checked((int)((root - 1) * pageSize)), checked((int)pageSize));
            var at = page.IndexOf(needle);
            if (at >= 0)
            {
                page[at + needle.Length - 1] ^= 0x01;
                File.WriteAllBytes(path, bytes);
                return;
            }
        }

        throw new InvalidOperationException($"No campaign index page of {path} holds \"{slug}\".");
    }

    // A rollback journal with SQLite's header magic: beside a database that a read-only open finds it non-empty, so SQLite
    // must roll it back first, as it would after a writer was killed mid-transaction (the leftover of an interrupted
    // VACUUM INTO).
    private static void WriteHotJournal(string path)
    {
        var journal = new byte[512];
        new byte[] { 0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7 }.CopyTo(journal, 0);
        File.WriteAllBytes(path, journal);
    }

    private static string Leftover(string path, DateTime lastWriteUtc)
    {
        File.WriteAllText(path, "left behind");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    // A restore beside a writer that commits back to back. SQLite's locks are not fair, so the restore's BEGIN IMMEDIATE
    // (or its first backup step) can miss every gap between the writer's batches for its whole busy wait, mostly on a
    // loaded machine; it is then refused as locked with campaigns.db unchanged, and the user is told to try again. A real
    // server writes seconds apart; this writer never pauses, so the test tries again too.
    private RestoreResult RestoreTryingAgainWhileLocked(string backup)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _db.Database.Backups.Restore(backup);
            }
            catch (CampaignStoreUnavailableException locked)
                when (attempt < 4 && locked.Message.Contains("is locked by another dnd-mcp process", StringComparison.Ordinal))
            {
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), "Timed out waiting for the writer.");
            await Task.Delay(5);
        }
    }

    private static HashSet<string> Markers(string path)
    {
        using var connection = OpenReadOnly(path);
        return connection.Query<string>("SELECT key FROM app_state WHERE key LIKE 'marker-%'").ToHashSet(StringComparer.Ordinal);
    }

    // Ballast rows, so copying campaigns.db takes long enough for another process's writer to commit in the middle.
    private void AddBallast(int megabytes)
    {
        using var connection = _db.Open();
        connection.Execute("CREATE TABLE ballast (b BLOB)");
        for (var i = 0; i < megabytes * 10; i++)
        {
            connection.Execute("INSERT INTO ballast VALUES (randomblob(100000))");
        }
    }

    // campaigns.db to the given journal mode, with no other connection open (leaving WAL needs the file to itself). The
    // instances already ready do not switch it back: the migrator sets WAL once per instance.
    private void SetJournalMode(string mode)
    {
        using var connection = new SqliteConnection($"Data Source={_db.DatabasePath};Pooling=False");
        connection.Open();
        Assert.Equal(mode, connection.ExecuteScalar<string>($"PRAGMA journal_mode = {mode}"));
    }

    // Another program in a write transaction on a database (a sqlite3 shell mid-write): an exclusive lock that keeps every
    // other connection out until it is disposed.
    private static SqliteConnection LockExclusively(string path)
    {
        var holder = new SqliteConnection($"Data Source={path};Pooling=False");
        holder.Open();
        holder.Execute("PRAGMA locking_mode = EXCLUSIVE");
        holder.Execute("BEGIN EXCLUSIVE");
        holder.Execute("UPDATE campaign SET summary_md = 'held'");
        return holder;
    }

    // A connection that gives up on a lock after about a second (no busy wait, the shortest command timeout).
    private static SqliteConnection OpenImpatient(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        return connection;
    }

    // A build one schema version ahead of this one: the embedded migrations plus a test migration after them, so a backup
    // made by this build is "older than the build" however many migrations are embedded.
    private static readonly int NewerVersion = CampaignDbMigrator.LatestVersion + 1;

    private static CampaignDbMigrator BuildWithANewerMigration() =>
        new([
            .. CampaignDbMigrator.Embedded,
            new CampaignMigration(NewerVersion, NewerVersion.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "_notes",
                "CREATE TABLE extra_note (id TEXT PRIMARY KEY) STRICT;"),
        ]);

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
