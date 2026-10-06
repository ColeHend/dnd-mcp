using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace DndMcp.Repository.Campaign;

/// <summary>A backup file, as its name describes it.</summary>
/// <param name="Path">The full path.</param>
/// <param name="At">When it was taken (UTC, millisecond precision, from the name).</param>
/// <param name="ProcessId">The process that took it (two sessions can back up in the same millisecond).</param>
/// <param name="Reason"><c>daily</c>, <c>session-end</c>, <c>manual</c>, <c>pre-restore</c> or <c>pre-migrate-v&lt;N&gt;</c>.</param>
/// <param name="SizeBytes">The file size.</param>
public sealed record BackupFile(string Path, DateTimeOffset At, int ProcessId, string Reason, long SizeBytes);

/// <summary>What <see cref="CampaignBackups.Restore"/> did, for the CLI to print.</summary>
/// <param name="RestoredFrom">The backup that is now campaigns.db.</param>
/// <param name="DatabasePath">campaigns.db.</param>
/// <param name="PreviousSavedTo">The <c>pre-restore</c> backup of what campaigns.db held before; null when there was no campaigns.db.</param>
/// <param name="BackupSchemaVersion">The backup's schema version.</param>
/// <param name="SchemaVersion">The schema version after restoring (the backup's, migrated to this build's when older).</param>
/// <param name="CampaignSlugs">The campaigns the restored database holds.</param>
public sealed record RestoreResult(
    string RestoredFrom,
    string DatabasePath,
    string? PreviousSavedTo,
    int BackupSchemaVersion,
    int SchemaVersion,
    IReadOnlyList<string> CampaignSlugs);

/// <summary>
/// campaigns.db backups in <c>&lt;directory of campaigns.db&gt;/backups/</c>: taking them, listing them, pruning them and
/// restoring one (contract §3.8).
///
/// <para>
/// <b>VACUUM INTO, never a file copy.</b> campaigns.db runs in WAL mode, so committed rows can live only in the -wal file
/// for a long time; a raw copy of the .db silently lacks them (VacuumIntoBackupTests pins this). <c>VACUUM INTO</c>
/// writes one consistent, self-contained file (rollback-journal mode, no -wal) from a read transaction, so it runs beside
/// other sessions' writes without blocking them. It refuses an existing target and cannot run inside a transaction.
/// </para>
/// <para>
/// <b>Names</b> are <c>campaigns-&lt;yyyyMMdd'T'HHmmssfff'Z'&gt;-&lt;pid&gt;-&lt;reason&gt;.db</c> (UTC, invariant digits,
/// no ':' for Windows): unique per backup (VACUUM INTO refuses a name that exists, and a fixed pre-migration name used to
/// block every retry of a failed migration), sortable, and carrying what retention needs. Retention parses the name,
/// never the file's own times (a copied file gets a new mtime), and only files matching the pattern are ever deleted:
/// whatever else a user keeps in backups/ is theirs.
/// </para>
/// <para>
/// <b>A name only ever holds a complete backup.</b> <c>VACUUM INTO</c> writes a temporary <c>&lt;name&gt;.partial</c>
/// (which the pattern does not match) and the file is renamed to its name only once it is complete. A backup interrupted
/// part-way (a server killed during its daily or session-end backup, Ctrl-C on the backup command) written straight to
/// its name would leave a truncated file there: it would count as "a backup stamped today", so no process would take a
/// usable daily backup for the rest of the day, it would take one of retention's newest slots, and restoring it would
/// fail. Instead it leaves a <c>.partial</c> (and its <c>-journal</c>) that nothing lists. Retention removes such
/// leftovers once they are <see cref="LeftoverAge"/> old, together with any <c>-journal</c> whose file is gone (deleting a
/// backup never knew about its journal); a younger one may belong to a backup another process is writing right now.
/// </para>
/// <para>
/// <b>Retention</b>, after every backup: keep every <c>pre-migrate-*</c> (the only way back from a schema change); of the
/// rest keep the newest <see cref="KeepNewest"/>, and the newest of each UTC day for the last <see cref="KeepDailyDays"/>
/// days (today and the 29 before it); delete the others. A file that cannot be deleted is logged and left. A restore runs
/// no retention: it must not delete the backup it restored from.
/// </para>
/// </summary>
public sealed partial class CampaignBackups
{
    /// <summary>The backups directory's name, beside campaigns.db.</summary>
    public const string DirectoryName = "backups";

    /// <summary>How many of the newest non-migration backups retention always keeps.</summary>
    public const int KeepNewest = 10;

    /// <summary>How many UTC days (including today) retention keeps the newest backup of.</summary>
    public const int KeepDailyDays = 30;

    /// <summary>The suffix of a backup still being written (renamed away once complete); no backup name ends with it.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>SQLite's rollback journal beside a database file (VACUUM INTO writes its target through one).</summary>
    internal const string JournalSuffix = "-journal";

    /// <summary>
    /// How old (by its last write) a <c>.partial</c> or an orphaned <c>-journal</c> must be before retention removes it:
    /// a younger one may belong to a backup another process is writing right now, which writes it continuously.
    /// </summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromMinutes(1);

    private const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    // SQLITE_READONLY: a read-only connection needed to write (to roll back a hot journal).
    private const int SqliteReadOnly = 8;

    // SQLITE_BUSY: another connection holds a lock the statement needs, past busy_timeout.
    private const int SqliteBusy = 5;

    // SQLITE_CANTOPEN: a file (or the journal beside it) could not be opened.
    private const int SqliteCantOpen = 14;

    private readonly CampaignDatabase _database;
    private readonly ILogger? _logger;

    internal CampaignBackups(CampaignDatabase database, ILogger? logger)
    {
        _database = database;
        _logger = logger;
        DirectoryPath = Path.Combine(Path.GetDirectoryName(database.Path)!, DirectoryName);
    }

    /// <summary>The reasons a backup is taken (the last part of its name).</summary>
    public static class Reasons
    {
        /// <summary>Before the first write of a UTC day in a process.</summary>
        public const string Daily = "daily";

        /// <summary>After a session ends (its recap and knowledge are in).</summary>
        public const string SessionEnd = "session-end";

        /// <summary>The CLI's <c>backup</c> command.</summary>
        public const string Manual = "manual";

        /// <summary>What campaigns.db held before a restore replaced it.</summary>
        public const string PreRestore = "pre-restore";
    }

    /// <summary><c>backups/</c> beside campaigns.db.</summary>
    public string DirectoryPath { get; }

    /// <summary>The clock backup names and retention use.</summary>
    public TimeProvider Time => _database.Time;

    /// <summary>
    /// Test seam: runs while a restore holds campaigns.db's write lock, after the pre-restore copy was taken and before
    /// the backup is copied in, with the pre-restore path (null when there was nothing to save), so a test can check
    /// that another process can read but not write in exactly the window the lock exists for.
    /// </summary>
    internal Action<string?>? WhileRestoreHoldsTheLock { get; set; }

    /// <summary><c>pre-migrate-v&lt;N&gt;</c>: the copy taken before migration N.</summary>
    public static string PreMigrateReason(int version) =>
        "pre-migrate-v" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>The file name for a backup taken at <paramref name="at"/> by <paramref name="processId"/>.</summary>
    public static string FileName(DateTimeOffset at, int processId, string reason)
    {
        if (!IsReason(reason))
        {
            throw new ArgumentException(
                $"\"{reason}\" is not a backup reason: daily, session-end, manual, pre-restore or pre-migrate-v<N>.", nameof(reason));
        }

        return $"campaigns-{at.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture)}-" +
               $"{processId.ToString(CultureInfo.InvariantCulture)}-{reason}.db";
    }

    /// <summary>Parses a backup file name; false for anything else (such files are never touched).</summary>
    public static bool TryParseFileName(string fileName, out DateTimeOffset at, out int processId, out string reason)
    {
        at = default;
        processId = 0;
        reason = string.Empty;
        var match = NamePattern().Match(fileName);
        if (!match.Success ||
            !DateTime.TryParseExact(match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp) ||
            !int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out processId))
        {
            return false;
        }

        at = new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc));
        reason = match.Groups["reason"].Value;
        return true;
    }

    /// <summary>
    /// Takes a backup of campaigns.db (<c>VACUUM INTO</c> on its own connection, outside any transaction, into
    /// <c>&lt;name&gt;.partial</c>, renamed to its name once complete) and applies retention. The stamp is
    /// <see cref="Time"/>'s now, moved on by a millisecond while a file of that name (or its <c>.partial</c>) exists.
    /// </summary>
    /// <returns>The backup's path.</returns>
    /// <exception cref="FileNotFoundException">There is no campaigns.db to back up.</exception>
    /// <exception cref="SqliteException">The backup could not be written (the partial file is removed).</exception>
    /// <exception cref="IOException">backups/ could not be created, or the finished file could not be renamed.</exception>
    public string Create(string reason) => Create(reason, applyRetention: true);

    internal string Create(string reason, bool applyRetention)
    {
        if (!IsReason(reason))
        {
            throw new ArgumentException(
                $"\"{reason}\" is not a backup reason: daily, session-end, manual, pre-restore or pre-migrate-v<N>.", nameof(reason));
        }

        if (!File.Exists(_database.Path))
        {
            throw new FileNotFoundException($"There is no campaigns.db at {_database.Path} to back up.", _database.Path);
        }

        Directory.CreateDirectory(DirectoryPath);
        var at = Time.GetUtcNow();
        var processId = Environment.ProcessId;
        var path = Path.Combine(DirectoryPath, FileName(at, processId, reason));
        for (var attempt = 0; File.Exists(path) || File.Exists(path + PartialSuffix); attempt++)
        {
            if (attempt >= 1000)
            {
                throw new IOException($"Could not find an unused backup name in {DirectoryPath}.");
            }

            at = at.AddMilliseconds(1);
            path = Path.Combine(DirectoryPath, FileName(at, processId, reason));
        }

        // The connection is closed before the rename: Windows cannot rename a file that is open, and SQLite has already
        // closed the target when VACUUM INTO returns, so this only keeps the order obvious. A VACUUM INTO that fails part-way
        // rolls its target back to an empty file and deletes the target's journal itself; that empty file is removed here.
        // Only a process killed mid-copy (or a disk failing too badly for SQLite to clean up) leaves the journal, and
        // retention removes such leftovers once stale (class summary).
        var partial = path + PartialSuffix;
        try
        {
            using (var connection = _database.OpenConnection(SqliteOpenMode.ReadWrite))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "VACUUM INTO $path";
                command.Parameters.AddWithValue("$path", partial);
                command.ExecuteNonQuery();
            }

            File.Move(partial, path, overwrite: false);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        _logger?.LogInformation("Backed up campaigns.db to {Path} ({Reason}).", path, reason);
        if (applyRetention)
        {
            ApplyRetention();
        }

        return path;
    }

    /// <summary>
    /// The backup taken before migration <paramref name="version"/>: every attempt copies the file to a new
    /// <c>pre-migrate-vN</c> backup, and that copy is deleted again when it is byte for byte the newest older
    /// <c>pre-migrate-vN</c> backup (SHA-256), which is then the one this attempt names.
    ///
    /// <para>
    /// <b>Why not just one copy per attempt</b> (review R02): a migration that cannot take the write lock (another process
    /// holds campaigns.db past busy_timeout) fails AFTER its backup, and is not cached as failed, so every later campaign
    /// call tries again; retention keeps every <c>pre-migrate-*</c> forever (the only way back from a schema change), so the
    /// disk filled one full copy per refused call. A copy of an unchanged file is the same backup, and goes.
    /// </para>
    /// <para>
    /// <b>Why every attempt still copies</b> (F2, review RR01): F1 reused the earlier backup while a fingerprint of the
    /// file (its user_version, size and AUTOINCREMENT counters) was unchanged, and a write that changes a row in place (an
    /// UPDATE, or a 0.6.0 logged write that adds no entity, fact or roll: change_log.seq is no AUTOINCREMENT key) moves
    /// none of them, so the only pre-migrate backup missed a committed write. Correctness first: the bytes decide, and the
    /// backup the migration leaves holds every write committed before it. Only an OLDER backup is compared with: two
    /// processes migrating together never delete each other's copies (the oldest of a run of identical copies stays).
    /// </para>
    /// </summary>
    /// <returns>The backup's path (the new copy, or the identical one it was deleted for).</returns>
    /// <exception cref="SqliteException">The backup could not be written.</exception>
    /// <exception cref="IOException">backups/ could not be created, or the finished file could not be renamed.</exception>
    internal string PreMigrate(int version)
    {
        var reason = PreMigrateReason(version);
        var path = Create(reason, applyRetention: false);
        var mine = List().FirstOrDefault(f => f.Path == path);
        var newest = mine is null ? null : List().FirstOrDefault(f => f.Reason == reason && IsOlder(f, mine));
        if (newest is null || !SameBytes(newest.Path, path))
        {
            return path;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not remove the backup {Path}, the same as {Same}; both are kept.", path, newest.Path);
            return path;
        }

        _logger?.LogInformation("The backup before updating campaigns.db ({Reason}) is the same as {Path}, taken before an earlier attempt: kept that one.",
            reason, newest.Path);
        return newest.Path;
    }

    // Whether a backup was taken before another: by its stamp, then its name (the order List gives, oldest last).
    private static bool IsOlder(BackupFile file, BackupFile than) =>
        file.At < than.At || (file.At == than.At && string.CompareOrdinal(Path.GetFileName(file.Path), Path.GetFileName(than.Path)) < 0);

    // Whether two files hold the same bytes (their SHA-256; the sizes first). A file that cannot be read is not the same.
    private static bool SameBytes(string a, string b)
    {
        try
        {
            if (new FileInfo(a).Length != new FileInfo(b).Length)
            {
                return false;
            }

            using var first = File.OpenRead(a);
            using var second = File.OpenRead(b);
            return System.Security.Cryptography.SHA256.HashData(first).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(second));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Every backup file in backups/, newest first. Files not named like a backup are not listed.</summary>
    public IReadOnlyList<BackupFile> List()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return [];
        }

        var files = new List<BackupFile>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "campaigns-*.db"))
        {
            if (TryParseFileName(Path.GetFileName(path), out var at, out var processId, out var reason))
            {
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    continue;
                }

                files.Add(new BackupFile(path, at, processId, reason, size));
            }
        }

        return files.OrderByDescending(f => f.At).ThenByDescending(f => Path.GetFileName(f.Path), StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Deletes the backups retention does not keep, and the leftovers of interrupted backups (class summary). Never throws
    /// for a file it cannot delete: it logs a warning and moves on, because retention runs after a backup that already
    /// succeeded.
    /// </summary>
    public void ApplyRetention()
    {
        RemoveLeftovers();
        var files = List();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var rest = new List<BackupFile>();
        foreach (var file in files)
        {
            if (file.Reason.StartsWith("pre-migrate-", StringComparison.Ordinal))
            {
                keep.Add(file.Path);
            }
            else
            {
                rest.Add(file);
            }
        }

        foreach (var file in rest.Take(KeepNewest))
        {
            keep.Add(file.Path);
        }

        var today = DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);
        var firstKeptDay = today.AddDays(-(KeepDailyDays - 1));
        foreach (var day in rest.GroupBy(f => DateOnly.FromDateTime(f.At.UtcDateTime)))
        {
            if (day.Key >= firstKeptDay)
            {
                keep.Add(day.First().Path);
            }
        }

        foreach (var file in rest.Where(f => !keep.Contains(f.Path)))
        {
            try
            {
                File.Delete(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Could not delete the old backup {Path}; it is left in place.", file.Path);
            }
        }
    }

    // The leftovers of interrupted backups (class summary): a stale <name>.partial with its -journal, and a stale -journal
    // whose file is gone. Only leftovers of backup names: whatever else is in backups/ is the user's. Their age is their
    // last write against the system clock, which is what stamped it (the injected clock may be a test's, months away).
    private void RemoveLeftovers()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return;
        }

        var staleBefore = DateTime.UtcNow - LeftoverAge;
        foreach (var partial in Directory.EnumerateFiles(DirectoryPath, "campaigns-*.db" + PartialSuffix))
        {
            if (IsBackupName(Path.GetFileName(partial)[..^PartialSuffix.Length]) && LastWrittenBefore(partial, staleBefore))
            {
                TryDelete(partial + JournalSuffix);
                TryDelete(partial);
            }
        }

        foreach (var journal in Directory.EnumerateFiles(DirectoryPath, "campaigns-*" + JournalSuffix))
        {
            var database = journal[..^JournalSuffix.Length];
            var name = Path.GetFileName(database);
            if (name.EndsWith(PartialSuffix, StringComparison.Ordinal))
            {
                name = name[..^PartialSuffix.Length];
            }

            if (IsBackupName(name) && !File.Exists(database) && LastWrittenBefore(journal, staleBefore))
            {
                TryDelete(journal);
            }
        }
    }

    private static bool IsBackupName(string fileName) => TryParseFileName(fileName, out _, out _, out _);

    private static bool LastWrittenBefore(string path, DateTime utc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) < utc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces campaigns.db with a backup (the CLI's <c>restore</c>). The backup must exist, open read-only, pass
    /// <c>PRAGMA integrity_check</c>, have the <c>campaign</c> table and a schema version this build understands. What
    /// campaigns.db held is first saved as a <c>pre-restore</c> backup (a restore is itself undoable), then the backup is
    /// copied in with SQLite's online backup API (page by page under SQLite's locks, so another session with the file
    /// open sees either the old or the new database, never a torn file, which a file copy over an open database cannot
    /// promise), migrated when older, and switched to WAL. Runs under the process's write lock. Deletes no backup file
    /// (no retention pass): the backup restored from stays, so the same point can be restored again. The backup is held in
    /// a read transaction for the copy-in, so a program that tries to lock it meanwhile waits for the restore
    /// (HoldBackup has why).
    ///
    /// <para>
    /// <b>campaigns.db's write lock is held from before the pre-restore copy until the backup is in.</b> Other sessions'
    /// servers keep writing while a restore runs, and the pre-restore copy is a snapshot taken when it starts: without the
    /// lock, a batch another process committed during that copy, or between it and the copy-in, would be acknowledged to its
    /// caller and then be in neither the restored database nor the pre-restore file, and the larger campaigns.db is, the
    /// longer that window. With it, a write another process attempts meanwhile waits (busy_timeout) and lands in the
    /// restored database afterwards, or fails with the store's "locked by another process" message; one that committed
    /// before the lock is in the pre-restore copy. Readers are not blocked (in rollback-journal mode, only while the copy-in
    /// itself runs). How the lock is held depends on the journal mode, because the pre-restore <c>VACUUM INTO</c> runs on
    /// a second connection that must still be able to read: in WAL mode the online backup's first
    /// <c>sqlite3_backup_step(0)</c> takes the WAL write lock and keeps it until the copy finishes (WAL readers are never
    /// blocked by it); in rollback-journal mode (a file system without WAL) that step would take an EXCLUSIVE lock, which
    /// blocks the reader, so the destination connection holds a RESERVED lock instead (<c>BEGIN IMMEDIATE</c>, then
    /// <c>locking_mode=EXCLUSIVE</c> and <c>ROLLBACK</c>: in exclusive locking mode the lock outlives the transaction, and a
    /// COMMIT would escalate it to EXCLUSIVE), returns to normal locking mode, and the copy-in escalates from there. The
    /// lock is SQLite's, which is not fair: another process writing batches back to back can keep a restore from it for the
    /// whole busy wait, and the restore is then refused as locked, with campaigns.db unchanged.
    /// </para>
    /// </summary>
    /// <exception cref="DndInputException">
    /// The file is missing, not SQLite, damaged, an incomplete (interrupted) backup, unreadable, locked by another program,
    /// not a campaigns database, newer than this build, or of a page size a WAL campaigns.db cannot take. Each message
    /// names that file, never campaigns.db.
    /// </exception>
    /// <exception cref="CampaignStoreUnavailableException">
    /// The current campaigns.db could not be locked or saved first, or could not be written (nothing was changed); or a
    /// migration of the copied-in backup failed (the migrator's message); or a SQLite error stopped the restore after the
    /// backup was copied in (the message says the backup is in and names the pre-restore file).
    /// </exception>
    public RestoreResult Restore(string backupPath) => Restore(backupPath, new CampaignDbMigrator(_logger));

    /// <summary>
    /// <see cref="Restore(string)"/> with a given migrator: the test seam for restoring a backup older than the build
    /// (only schema version 1 exists yet, so no embedded migration set has an older version to restore).
    /// </summary>
    internal RestoreResult Restore(string backupPath, CampaignDbMigrator migrator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentNullException.ThrowIfNull(migrator);
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source))
        {
            throw new DndInputException($"There is no backup file at {source}. List the backups in {DirectoryPath}.");
        }

        if (string.Equals(source, _database.Path, StringComparison.Ordinal))
        {
            throw new DndInputException($"{source} is campaigns.db itself; give a backup file from {DirectoryPath}.");
        }

        var backupVersion = Inspect(source, migrator.Migrations[^1].Version);
        return _database.RunExclusive(() =>
        {
            string? previous;
            Directory.CreateDirectory(Path.GetDirectoryName(_database.Path)!);
            using (var from = HoldBackup(source))
            using (var to = OpenDestination())
            using (var copy = LockCopy(to, from))
            {
                previous = SavePrevious();
                WhileRestoreHoldsTheLock?.Invoke(previous);
                CopyIn(copy);
            }

            MigrationResult migration;
            var slugs = new List<string>();
            try
            {
                using var connection = _database.OpenConnection(SqliteOpenMode.ReadWrite);
                migration = migrator.Migrate(connection, this, _database.Path, applyRetention: false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT slug FROM campaign ORDER BY slug";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    slugs.Add(reader.GetString(0));
                }
            }
            catch (SqliteException ex)
            {
                // The backup is in: this failure must not reach the caller looking like one that changed nothing (the CLI
                // says "nothing was changed" of whatever reaches it unexplained).
                throw new CampaignStoreUnavailableException(NotFinished(source, previous), ex);
            }

            _logger?.LogInformation("Restored campaigns.db at {Path} from {Backup}.", _database.Path, source);
            return new RestoreResult(source, _database.Path, previous, backupVersion, migration.ToVersion, slugs);
        });
    }

    private string NotFinished(string source, string? previous) =>
        $"{source} was copied into campaigns.db at {_database.Path}, but finishing the restore (updating its schema and " +
        "journal mode) failed. Run the restore again" +
        (previous is null ? "." : $", or restore {previous} to put back what campaigns.db held before.");

    private static bool IsReason(string reason) => ReasonPattern().IsMatch(reason);

    // campaigns.db, created when there is none (the restore then has nothing to save). A directory this user cannot write
    // is the store's own user-facing message.
    private SqliteConnection OpenDestination()
    {
        try
        {
            return _database.OpenConnection(SqliteOpenMode.ReadWriteCreate);
        }
        catch (SqliteException ex) when (CampaignDatabase.TryMapUnavailable(ex, _database.Path, out var unavailable))
        {
            throw unavailable;
        }
    }

    // Takes campaigns.db's write lock for the copy (Restore's summary). A damaged campaigns.db fails here, at its first
    // read: the same advice as when it cannot be saved. Another process holding the lock past busy_timeout, or a file this
    // user cannot write, is the store's own message; nothing was changed in any case. A lock met here or in CopyIn is
    // campaigns.db's: the backup is held (HoldBackup).
    private LockedCopy LockCopy(SqliteConnection destination, SqliteConnection source)
    {
        try
        {
            return LockedCopy.Begin(destination, source);
        }
        catch (SqliteException ex) when ((ex.SqliteErrorCode & 0xFF) is 11 or 26)
        {
            throw new CampaignStoreUnavailableException(CouldNotSave(), ex);
        }
        catch (SqliteException ex) when (CampaignDatabase.TryMapUnavailable(ex, _database.Path, out var unavailable))
        {
            throw unavailable;
        }
    }

    // The pre-restore copy, taken while the copy holds campaigns.db's lock, so it holds every write committed before the
    // restore. A file with no schema (just created above, or empty) has nothing to save.
    private string? SavePrevious()
    {
        try
        {
            using (var probe = _database.OpenConnection(SqliteOpenMode.ReadWrite))
            {
                if (probe.ScalarLong("SELECT count(*) FROM sqlite_master") == 0)
                {
                    return null;
                }
            }

            return Create(Reasons.PreRestore, applyRetention: false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new CampaignStoreUnavailableException(CouldNotSave(), ex);
        }
    }

    private void CopyIn(LockedCopy copy)
    {
        try
        {
            copy.Complete();
        }
        catch (SqliteException ex) when (CampaignDatabase.TryMapUnavailable(ex, _database.Path, out var unavailable))
        {
            throw unavailable;
        }
    }

    private string CouldNotSave() =>
        $"Could not save the current campaigns.db at {_database.Path} before restoring, so nothing was changed. If " +
        "campaigns.db is damaged, move it (with any campaigns.db-wal and campaigns.db-shm beside it) somewhere safe and run " +
        "the restore again.";

    // The backup's schema version, after checking it is a campaigns database this build can use. Each check stands alone:
    // a file with damaged pages can still open and hold a campaign table, and a file with a campaign table may never have
    // been migrated (user_version 0), so none of them is implied by another. Every refusal names the backup: a failure
    // here is the backup's, and a message about campaigns.db would send the user to fix a database that is fine.
    private int Inspect(string path, int latest)
    {
        try
        {
            using var connection = OpenReadOnly(path);
            var integrity = connection.ScalarText("PRAGMA integrity_check");
            if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
            {
                throw new DndInputException(
                    $"{path} fails SQLite's integrity check, so it is not restored. Choose another backup.");
            }

            if (connection.ScalarLong("SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'campaign'") == 0)
            {
                throw new DndInputException(
                    $"{path} is a SQLite database but not a dnd-mcp campaigns database (it has no campaign table), so it is " +
                    "not restored. Choose a file from the backups directory.");
            }

            var version = CampaignDbMigrator.UserVersion(connection, null);
            if (version > latest)
            {
                throw new DndInputException(
                    $"{path} was written by a newer version of dnd-mcp (schema version {version}; this version understands " +
                    $"up to {latest}), so it is not restored. Restore it with that version.");
            }

            if (version < 1)
            {
                throw new DndInputException(
                    $"{path} has no campaigns schema version, so it is not a dnd-mcp campaigns backup. Choose a file from " +
                    "the backups directory.");
            }

            return version;
        }
        catch (SqliteException ex) when (BackupRefusal(path, ex) is { } refusal)
        {
            throw refusal;
        }
    }

    // The backup, opened again for the copy-in (it may have changed since Inspect: LockedCopy.Begin checks it is not
    // empty), in a read transaction held until the copy-in ends. Held, the backup cannot be locked or changed by another
    // program meanwhile (a sqlite3 shell beginning a write waits for the restore), so a lock SQLite meets in LockCopy or
    // CopyIn is campaigns.db's, as their messages say. Unheld, a lock taken on the backup after Inspect failed the copy
    // with SQLITE_BUSY, refused as "campaigns.db ... is locked by another dnd-mcp process": the wrong file and the wrong
    // program. A lock taken before this point is met here, at the transaction's first read, and refused naming the backup.
    private SqliteConnection HoldBackup(string path)
    {
        SqliteConnection? connection = null;
        try
        {
            connection = OpenReadOnly(path);
            connection.ExecuteText("BEGIN");
            connection.ScalarLong("PRAGMA schema_version");
            return connection;
        }
        catch (SqliteException ex) when (BackupRefusal(path, ex) is { } refusal)
        {
            connection?.Dispose();
            throw refusal;
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }

    // A failure reading the backup, as the refusal naming it (null for one that is not about the file: a bug).
    private DndInputException? BackupRefusal(string path, SqliteException ex)
    {
        if (Incomplete(path, ex))
        {
            return new DndInputException(
                $"{path} is an incomplete backup (it was interrupted while being written), so it is not restored. Choose " +
                $"another backup from {DirectoryPath}.", ex);
        }

        return (ex.SqliteErrorCode & 0xFF) switch
        {
            SqliteCantOpen => new DndInputException(
                $"{path} cannot be opened (this user may not be allowed to read it), so it is not restored. Check its " +
                $"permissions, or choose another backup from {DirectoryPath}.", ex),

            // A sqlite3 shell or a database browser with the backup open in a write transaction: the backup is locked, not
            // campaigns.db.
            SqliteBusy => new DndInputException(
                $"{path} is locked by another program, so it is not restored. Close it and try again, or choose another " +
                $"backup from {DirectoryPath}.", ex),
            11 or 26 => new DndInputException($"{path} is not a SQLite database (or is damaged), so it is not restored.", ex),
            _ => null,
        };
    }

    // A backup a writer stopped part-way through (a killed VACUUM INTO) has a hot journal beside it, which SQLite must
    // roll back before reading: a read-only open cannot, and says SQLITE_READONLY (READONLY_ROLLBACK), or SQLITE_CANTOPEN
    // when it cannot even open that journal. A file that also fails as damaged or not a database with its writer's
    // journal still beside it is the same interrupted backup.
    private static bool Incomplete(string path, SqliteException ex) =>
        (ex.SqliteErrorCode & 0xFF) == SqliteReadOnly ||
        ((ex.SqliteErrorCode & 0xFF) is SqliteCantOpen or 11 or 26 && File.Exists(path + JournalSuffix));

    // The backup being inspected or copied in. busy_timeout as on every campaign connection: a user can point restore at
    // a database another process has open in WAL mode, and without it a busy source fails at once with a raw SQLITE_BUSY
    // in the middle of BackupDatabase instead of waiting for that process's write to finish.
    internal static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            connection.Open();
            connection.ExecuteText(
                $"PRAGMA busy_timeout = {CampaignDatabase.BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not remove the incomplete backup {Path}.", path);
        }
    }

    /// <summary>
    /// The online backup of a restore source into campaigns.db, begun so that campaigns.db's write lock is held before the
    /// pre-restore copy is taken and released only when the copy-in has committed or been abandoned (Restore's summary has
    /// why, and why the way it is held depends on the journal mode). Disposing it before <see cref="Complete"/> abandons
    /// the copy: SQLite rolls the destination's transaction back, so campaigns.db is unchanged, and the lock goes when the
    /// destination connection closes.
    /// </summary>
    private sealed class LockedCopy : IDisposable
    {
        // sqlite3_backup_step copied the last page and committed the destination.
        private const int SqliteDone = 101;

        private readonly SqliteConnection _destination;
        private readonly SqliteConnection _source;
        private readonly bool _wal;
        private sqlite3_backup? _backup;

        private LockedCopy(SqliteConnection destination, SqliteConnection source, bool wal)
        {
            _destination = destination;
            _source = source;
            _wal = wal;
        }

        /// <summary>Takes campaigns.db's write lock (waiting up to busy_timeout for another process's) and copies nothing yet.</summary>
        /// <exception cref="DndInputException">The backup is empty, or has a page size WAL-mode campaigns.db cannot take.</exception>
        /// <exception cref="SqliteException">campaigns.db could not be read or locked; nothing was changed.</exception>
        public static LockedCopy Begin(SqliteConnection destination, SqliteConnection source)
        {
            // The step that would copy an empty source resets the destination to an empty database and commits at once, so
            // it must never run: Inspect has seen a campaign table, and this checks the very connection the copy reads.
            if (source.ScalarLong("PRAGMA page_count") == 0)
            {
                throw new DndInputException($"{source.DataSource} is empty, so it is not restored. Choose another backup.");
            }

            var wal = string.Equals(destination.ScalarText("PRAGMA journal_mode"), "wal", StringComparison.OrdinalIgnoreCase);

            // SQLite cannot change a WAL database's page size, so it refuses such a copy with SQLITE_READONLY, which would
            // read as a permissions problem with campaigns.db. Every backup taken here keeps campaigns.db's page size.
            var pageSize = source.ScalarLong("PRAGMA page_size");
            if (wal && pageSize != destination.ScalarLong("PRAGMA page_size"))
            {
                throw new DndInputException(
                    $"{source.DataSource} uses {pageSize.ToString(CultureInfo.InvariantCulture)}-byte pages and campaigns.db " +
                    "another size, so it cannot be copied into campaigns.db while campaigns.db is in WAL mode; it is not " +
                    "restored. Choose a backup dnd-mcp made.");
            }

            var copy = new LockedCopy(destination, source, wal);
            try
            {
                if (wal)
                {
                    copy.Init();
                    copy.Step(0);
                }
                else
                {
                    // In this order: a BEGIN IMMEDIATE that has to wait keeps the SHARED lock it took on the way in exclusive
                    // locking mode, so a writer holding RESERVED could never commit and both would time out (deadlock).
                    // Waiting in normal mode first, then switching inside the transaction, keeps the RESERVED lock past the
                    // ROLLBACK (exclusive mode never drops a lock; a COMMIT would escalate it to EXCLUSIVE).
                    destination.ExecuteText("BEGIN IMMEDIATE");
                    destination.ExecuteText("PRAGMA locking_mode = EXCLUSIVE");
                    destination.ExecuteText("ROLLBACK");
                }

                return copy;
            }
            catch
            {
                copy.Dispose();
                throw;
            }
        }

        /// <summary>Copies every page of the backup into campaigns.db and commits; the lock goes with the commit.</summary>
        /// <exception cref="SqliteException">The copy failed and was rolled back; campaigns.db is unchanged.</exception>
        public void Complete()
        {
            if (!_wal)
            {
                // Back to normal locking before the copy, so its commit releases the lock instead of keeping it; the
                // RESERVED lock taken in Begin is still held until then.
                _destination.ExecuteText("PRAGMA locking_mode = NORMAL");
                Init();
            }

            if (Step(-1) != SqliteDone)
            {
                throw new InvalidOperationException("The restore copy stopped before its last page.");
            }

            var finished = raw.sqlite3_backup_finish(_backup!);
            _backup = null;
            SqliteException.ThrowExceptionForRC(finished, _destination.Handle);
        }

        public void Dispose()
        {
            if (_backup is not null)
            {
                raw.sqlite3_backup_finish(_backup);
                _backup = null;
            }
        }

        private void Init()
        {
            _backup = raw.sqlite3_backup_init(_destination.Handle, "main", _source.Handle, "main");
            if (_backup is null || _backup.IsInvalid)
            {
                _backup = null;
                SqliteException.ThrowExceptionForRC(raw.sqlite3_errcode(_destination.Handle), _destination.Handle);
                throw new InvalidOperationException("SQLite would not start the restore copy.");
            }
        }

        private int Step(int pages)
        {
            var rc = raw.sqlite3_backup_step(_backup!, pages);
            SqliteException.ThrowExceptionForRC(rc, _destination.Handle);
            return rc;
        }
    }

    [GeneratedRegex(@"^(daily|session-end|manual|pre-restore|pre-migrate-v[0-9]{1,6})$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();

    [GeneratedRegex(@"^campaigns-(?<stamp>[0-9]{8}T[0-9]{9}Z)-(?<pid>[0-9]{1,10})-(?<reason>daily|session-end|manual|pre-restore|pre-migrate-v[0-9]{1,6})\.db$",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
