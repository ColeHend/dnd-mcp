using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Dapper;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DndMcp.Repository.Campaign;

/// <summary>
/// campaigns.db: opening, creating and migrating it lazily, the process-wide write lock, the daily backup, and
/// connections that follow the house SQLite rules. One instance per campaigns.db path per process (the host's
/// <c>CampaignService</c> holds it); thread-safe.
///
/// <para>
/// <b>Lazy, never at construction.</b> The constructor does no I/O: the server must answer <c>initialize</c> even when the
/// data directory is unwritable or the file is damaged, and dice and rules must keep working. The first campaign read or
/// write runs <see cref="EnsureReady"/> (directory, file, migrations, WAL) once; success is cached, failure is not (the
/// next call retries, so fixing a permission needs no restart; only the calls that were already waiting for a failed
/// attempt share its refusal). A read that finds no file does not create one (<see cref="TryOpenExisting"/>).
/// </para>
/// <para>
/// <b>Connections</b> (<c>understand-sqlite.md</c> (c); the spikes in DndMcp.Tests/Sqlite pin each rule):
/// <c>Foreign Keys=True</c>, <c>Default Timeout=5</c>, <c>Pooling=False</c> (a pooled handle keeps the file and its -wal
/// open after Dispose, which breaks restore and test cleanup), <c>Recursive Triggers=True</c> (defence in depth: REPLACE's
/// implicit delete then fires the delete triggers), then <c>PRAGMA busy_timeout = 5000</c> (Default Timeout alone leaves
/// SQLite's own busy wait at 0) and <c>PRAGMA synchronous = NORMAL</c> (safe in WAL: a power cut can lose the last commits,
/// never corrupt). A <see cref="SqliteConnection"/> never serves two threads: every operation opens its own.
/// </para>
/// <para>
/// <b>Writes</b> (<see cref="Write{T}"/>): the process-wide <see cref="SemaphoreSlim"/> first (the SDK runs tool calls
/// concurrently; without it two in-process writers would spin in SQLite's busy handler on pool threads for up to 5 s,
/// and busy_timeout is left to arbitrate between processes only), then the daily backup, then <c>BEGIN IMMEDIATE</c>
/// (never a deferred write transaction: a deferred one that read before another process committed fails with
/// SQLITE_BUSY_SNAPSHOT, which no waiting fixes). The batch's <see cref="ChangeRecorder"/> is flushed, then the
/// transaction commits once, or rolls back for a dry run. Any exception disposes the transaction, which rolls it back:
/// a RAISE(ABORT) undoes only its own statement, so committing after a caught exception would persist half a batch.
/// </para>
/// <para>
/// <b>Dapper's global.</b> The static constructor sets <see cref="DefaultTypeMap.MatchNamesWithUnderscores"/> before any
/// campaign query can run. The row records (<see cref="EntityRow"/>, …) bind snake_case columns to PascalCase constructor
/// parameters only with it on; it is process-wide, and Dapper caches each materializer per (type, column shape) the first
/// time it runs, so setting it later has no effect until the cache is purged (DapperMappingTests pins this). Unit tests
/// build stores directly and never run the host's startup, so the host cannot be the place that sets it; every campaign
/// class that runs Dapper calls <see cref="EnsureDapperConfigured"/> first, which runs this constructor.
/// </para>
/// <para>
/// <b>A newer schema is refused on every use, not once.</b> <see cref="EnsureReady"/> migrates once per instance and
/// caches success, but another process (the next dnd-mcp build, started by a new Claude session while this one runs) can
/// move the file to a newer schema at any time afterwards. So every batch reads <c>PRAGMA user_version</c> again right
/// after <c>BEGIN IMMEDIATE</c> (holding the write lock, so no migration can land between the check and the batch), and
/// every <see cref="OpenRead"/> and <see cref="TryOpenExisting"/> reads it once at open. Above
/// <see cref="CampaignDbMigrator.LatestVersion"/> the call is refused with the same message as a fresh start gets and
/// nothing is written: an old build writing rows into tables whose meaning changed, or undoing them through its old column
/// catalogue (which would drop the newer columns), corrupts data no later migration can repair. Nor does such a batch
/// take the day's backup first: this build's names and retention are not the newer build's to share, and its retention
/// pass could delete backups the newer build keeps.
/// </para>
/// </summary>
public sealed class CampaignDatabase : IDisposable
{
    /// <summary>SQLite's busy wait per statement, in milliseconds.</summary>
    public const int BusyTimeoutMilliseconds = 5000;

    /// <summary>Microsoft.Data.Sqlite's retry bound on SQLITE_BUSY for the store's own connections, in seconds (the busy wait's).</summary>
    internal const int DefaultTimeoutSeconds = 5;

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private static readonly object ProbeLock = new();
    private static bool s_probed;

    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _readyLock = new();
    private volatile bool _ready;
    private int _failedAttempts;
    private CampaignStoreUnavailableException? _lastFailure;
    private volatile bool _disposed;
    private DateOnly? _createdOn;
    private DateOnly? _dailyCheckedOn;

    static CampaignDatabase()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    /// <param name="path">campaigns.db (made absolute; nothing is opened or created here).</param>
    /// <param name="time">The clock for timestamps, backup names and the daily backup (tests pass a manual one).</param>
    /// <param name="logger">Warnings that must not block a write (a failed daily backup, no WAL, retention failures).</param>
    public CampaignDatabase(string path, TimeProvider time, ILogger<CampaignDatabase>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(time);
        Path = System.IO.Path.GetFullPath(path);
        Time = time;
        _logger = logger;
        Backups = new CampaignBackups(this, logger);
    }

    /// <summary>The campaigns.db file.</summary>
    public string Path { get; }

    /// <summary>True when the file exists (it may still need migrating).</summary>
    public bool Exists => File.Exists(Path);

    public TimeProvider Time { get; }

    /// <summary>Backups of this database.</summary>
    public CampaignBackups Backups { get; }

    /// <summary>
    /// Test seam: runs at the start of every open-and-migrate attempt of <see cref="EnsureReady"/>, holding the ready lock,
    /// so a test can fail an attempt in a way the store does not map (a bug) while other calls wait for it.
    /// </summary>
    internal Action? BeforeEachAttempt { get; set; }

    /// <summary>
    /// A timestamp as campaigns.db stores it: UTC, <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>, invariant culture. Fixed width, so
    /// text order is time order (history sorts and the <c>ix_cl_at</c> index rely on it).
    /// </summary>
    public static string FormatTimestamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>A new row id: a UUIDv7 in lower-case "D" form. Time-ordered, so ids created later sort later.</summary>
    public static string NewId() => Guid.CreateVersion7().ToString("D");

    /// <summary>Runs the static constructor (which configures Dapper) if it has not run yet. Call before any Dapper query.</summary>
    public static void EnsureDapperConfigured()
    {
    }

    /// <summary>Now, as <see cref="FormatTimestamp"/> writes it.</summary>
    public string Now() => FormatTimestamp(Time.GetUtcNow());

    /// <summary>
    /// Creates the directory and the file when needed, checks the SQLite library once per process, applies pending
    /// migrations (backing up an existing database first) and sets WAL. Idempotent and thread-safe; success is cached per
    /// instance, failure is not: a call that arrives after a failed attempt runs its own.
    ///
    /// <para>
    /// <b>Calls that waited for a failed attempt share its refusal.</b> One attempt runs at a time, under the ready lock.
    /// When it fails, every call that was already waiting for the lock is refused with that attempt's message instead of
    /// running an attempt of its own. Without this, an instance that is not ready yet (a new Claude session's first campaign
    /// calls) while another process holds campaigns.db past busy_timeout answered a burst of calls one by one, each about
    /// one busy wait (some 10 s) after the last: the fourth after 40 s, and dice_roll's log lookup, which waits here too,
    /// behind all of them. A ready instance answers the same burst together after one wait. Failed attempts are counted
    /// (<c>_failedAttempts</c>, read before the lock is taken), so a waiting call can tell that one failed while it waited.
    /// </para>
    /// </summary>
    /// <exception cref="CampaignStoreUnavailableException">
    /// Something the user must fix: a database newer than this build, an unwritable directory or file, a damaged file, a
    /// full disk, another process holding the file past busy_timeout, or a failed pre-migration backup or migration. The
    /// message says which file and what to do.
    /// </exception>
    /// <exception cref="InvalidOperationException">The SQLite library lacks a feature campaigns.db needs.</exception>
    public void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ready)
        {
            if (File.Exists(Path))
            {
                return;
            }

            // Deleted under a running server: build a new one rather than failing every call until a restart.
            _ready = false;
        }

        var failedBefore = Volatile.Read(ref _failedAttempts);
        lock (_readyLock)
        {
            if (_ready)
            {
                return;
            }

            if (_failedAttempts != failedBefore && _lastFailure is { } failure)
            {
                // A new instance per call: an exception object is thrown on one thread only.
                throw failure.InnerException is { } cause
                    ? new CampaignStoreUnavailableException(failure.Message, cause)
                    : new CampaignStoreUnavailableException(failure.Message);
            }

            try
            {
                Attempt();
            }
            catch (Exception ex)
            {
                // Only the store's own refusal is shared; anything else is a bug or a missing SQLite feature, which the
                // waiting calls find out for themselves.
                _lastFailure = ex as CampaignStoreUnavailableException;
                Interlocked.Increment(ref _failedAttempts);
                throw;
            }

            _ready = true;
        }
    }

    // One open-and-migrate attempt (EnsureReady holds the ready lock).
    private void Attempt()
    {
        BeforeEachAttempt?.Invoke();
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CampaignStoreUnavailableException(
                $"Cannot create the directory {directory} for campaigns.db. Fix its permissions, or set DND_MCP_DB (a file) " +
                $"or {DndMcpPaths.DataDirectoryVariable} (a directory) to a location this user can write.", ex);
        }

        EnsureSqliteSupported();
        var existed = File.Exists(Path) && new FileInfo(Path).Length > 0;
        try
        {
            using var connection = OpenConnection(SqliteOpenMode.ReadWriteCreate);
            new CampaignDbMigrator(_logger).Migrate(connection, Backups, Path);
        }
        catch (SqliteException ex) when (Unavailable(ex) is { } message)
        {
            throw new CampaignStoreUnavailableException(message, ex);
        }

        if (!existed)
        {
            _createdOn = Today();
        }
    }

    /// <summary>
    /// A read connection (read-write mode: WAL readers maintain the -shm), after <see cref="EnsureReady"/>. Refused when
    /// another process has since moved the file to a newer schema (class summary).
    /// </summary>
    /// <exception cref="CampaignStoreUnavailableException">As <see cref="EnsureReady"/>.</exception>
    public SqliteConnection OpenRead()
    {
        EnsureReady();
        return OpenCheckedForUse();
    }

    /// <summary>
    /// A connection when campaigns.db exists (migrated first if it is behind), or null when there is no file: a read never
    /// creates the database ("no campaigns yet" is an answer, not a reason to write a file). Refused when another process
    /// has since moved the file to a newer schema (class summary).
    /// </summary>
    /// <exception cref="CampaignStoreUnavailableException">As <see cref="EnsureReady"/>.</exception>
    public SqliteConnection? TryOpenExisting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(Path))
        {
            return null;
        }

        EnsureReady();
        return OpenCheckedForUse();
    }

    /// <summary>
    /// The user-facing <see cref="CampaignStoreUnavailableException"/> for a SQLite failure a person can fix, raised by a
    /// statement on any campaigns.db connection, not only while opening or writing: a read past busy_timeout behind another
    /// process's lock (SQLITE_BUSY), a damaged page met mid-query (SQLITE_CORRUPT), a file replaced by something that is
    /// not a database after the first open (SQLITE_NOTADB), an I/O error, a full disk, a read-only or unopenable file. The
    /// host's call-tool, get-prompt and read-resource filters use it so a failing read says what is wrong with which file,
    /// exactly as a failing write does, instead of the SDK's bare "An error occurred". The codes are those
    /// <see cref="Write{T}"/> maps (3, 5, 8, 10, 11, 13, 14 and 26, compared on the primary code, so SQLITE_BUSY_SNAPSHOT
    /// counts as busy); anything else is a bug and stays unmapped.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="path">The campaigns.db the statement ran on (named in the message, with its backups directory).</param>
    /// <param name="unavailable">The exception to throw instead (its inner exception is <paramref name="exception"/>); null when false.</param>
    /// <returns>True when the code is one a person can act on.</returns>
    public static bool TryMapUnavailable(SqliteException exception, string path, [NotNullWhen(true)] out CampaignStoreUnavailableException? unavailable)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var backups = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(fullPath)!, CampaignBackups.DirectoryName);
        var message = UnavailableMessage(exception.SqliteErrorCode, fullPath, backups);
        unavailable = message is null ? null : new CampaignStoreUnavailableException(message, exception);
        return unavailable is not null;
    }

    /// <summary>
    /// Runs one batch: the write lock, the daily backup, <c>BEGIN IMMEDIATE</c>, <paramref name="work"/>, the batch's
    /// change_log flush, then commit (or roll back when <paramref name="dryRun"/>: everything ran, nothing persists, no
    /// batch exists). Exceptions roll back and propagate.
    /// </summary>
    /// <exception cref="CampaignStoreUnavailableException">
    /// As <see cref="EnsureReady"/>, and for what only shows once the batch runs: a read-only file, a full disk, an I/O
    /// error, or another process holding the lock past busy_timeout. The batch was rolled back.
    /// </exception>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureReady();
        _writeLock.Wait();
        try
        {
            return WriteLocked(work, dryRun);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// <see cref="Write{T}"/> that waits for the write lock asynchronously, so queued tool calls do not hold pool threads
    /// (balance_simulate's fights need them), and a queued call stays cancellable until its turn: a writer waiting inside
    /// SQLite's busy handler instead could be neither cancelled nor released for up to busy_timeout. The work itself is
    /// synchronous and short.
    /// </summary>
    /// <exception cref="CampaignStoreUnavailableException">As <see cref="Write{T}"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled before the batch began.</exception>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work, bool dryRun = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        EnsureReady();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            return WriteLocked(work, dryRun);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Marks the instance disposed. Never blocks or throws (server shutdown must finish promptly).</summary>
    public void Dispose()
    {
        _disposed = true;
    }

    /// <summary>A connection with the house settings and pragmas. No EnsureReady (the migrator and backups use it).</summary>
    internal SqliteConnection OpenConnection(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(ConnectionString(Path, mode));
        try
        {
            connection.Open();
            connection.ExecuteText(
                $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)}; PRAGMA synchronous = NORMAL;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The house connection string for a campaigns.db file.
    ///
    /// <para>
    /// <b>A caller that only reads and must never create the file</b> (the rules tools' ambient defaults, which run on
    /// every rules call by people who may never use campaigns) passes <see cref="SqliteOpenMode.ReadWrite"/>: it opens an
    /// existing file and fails with SQLITE_CANTOPEN on a missing one, creating nothing. Not
    /// <see cref="SqliteOpenMode.ReadOnly"/>: on a WAL database a read-only connection creates campaigns.db-wal and -shm
    /// and cannot remove them when it closes, so they would stay beside the user's database after every such read
    /// (CampaignDatabaseTests pins both halves).
    /// </para>
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="mode">ReadWriteCreate for the store itself; ReadWrite for a read that must not create the file.</param>
    /// <param name="defaultTimeoutSeconds">
    /// Microsoft.Data.Sqlite's own retry on SQLITE_BUSY: a statement that finds the file locked is retried every 150 ms
    /// until this many whole seconds have passed, on top of SQLite's <c>PRAGMA busy_timeout</c> wait per attempt (which the
    /// caller sets after opening). A caller that must give up quickly (a short busy_timeout, a lookup that has a fallback)
    /// passes 1, the shortest bound there is: 0 would mean "retry forever", so it is refused.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="defaultTimeoutSeconds"/> is below 1.</exception>
    internal static string ConnectionString(string path, SqliteOpenMode mode, int defaultTimeoutSeconds = DefaultTimeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultTimeoutSeconds, 1);
        return new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            ForeignKeys = true,
            DefaultTimeout = defaultTimeoutSeconds,
            Pooling = false,
            RecursiveTriggers = true,
        }.ToString();
    }

    /// <summary>Runs <paramref name="action"/> holding the process's write lock (restore replaces the whole file).</summary>
    internal T RunExclusive<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try
        {
            return action();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // A read-only file, a full disk, an I/O error or another process holding the lock too long surface at BEGIN, in the
    // work or at COMMIT, not at open, so the user-facing mapping covers the whole transaction. The filter runs before the
    // transaction is disposed, but it only builds a message; the rollback still happens. Constraint and logic errors are
    // not mapped: they are bugs (or the caller's to translate) and propagate as they are. The schema version is read after
    // BEGIN IMMEDIATE, under the write lock, so a migration by another process cannot land between the check and the work.
    private T WriteLocked<T>(Func<SqliteConnection, SqliteTransaction, T> work, bool dryRun)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MaybeDailyBackup();
        using var connection = OpenChecked(SqliteOpenMode.ReadWrite);
        try
        {
            using var transaction = connection.BeginTransaction();
            RefuseNewerSchema(connection, transaction);
            var result = work(connection, transaction);
            ChangeRecorder.FlushFor(transaction);
            if (dryRun)
            {
                transaction.Rollback();
            }
            else
            {
                transaction.Commit();
            }

            return result;
        }
        catch (SqliteException ex) when (Unavailable(ex) is { } message)
        {
            throw new CampaignStoreUnavailableException(message, ex);
        }
    }

    // Before the first write of each UTC day in this process, unless a backup is already stamped today, this process
    // created the file today (a new database has nothing to lose), or another process has since moved the file to a newer
    // schema (the batch is refused right after; class summary). Failure is logged, never thrown: a backup must not block
    // the write it protects. Called under the process's write lock, so two writers here never both take it.
    private void MaybeDailyBackup()
    {
        var today = Today();
        if (_dailyCheckedOn == today)
        {
            return;
        }

        _dailyCheckedOn = today;
        if (_createdOn == today)
        {
            return;
        }

        try
        {
            if (Backups.List().Any(b => DateOnly.FromDateTime(b.At.UtcDateTime) == today) || NewerThanThisBuild())
            {
                return;
            }

            Backups.Create(CampaignBackups.Reasons.Daily);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex,
                "The daily backup of campaigns.db at {Path} failed; the write goes ahead without it. Check that {Backups} " +
                "is writable and the disk has space.", Path, Backups.DirectoryPath);
        }
    }

    private SqliteConnection OpenChecked(SqliteOpenMode mode)
    {
        try
        {
            return OpenConnection(mode);
        }
        catch (SqliteException ex) when (Unavailable(ex) is { } message)
        {
            throw new CampaignStoreUnavailableException(message, ex);
        }
    }

    // OpenRead / TryOpenExisting: the connection, after checking the file has not moved to a newer schema since this
    // instance migrated it. Reading user_version reads the header, which can itself meet a lock or a damaged file.
    private SqliteConnection OpenCheckedForUse()
    {
        var connection = OpenChecked(SqliteOpenMode.ReadWrite);
        try
        {
            RefuseNewerSchema(connection, null);
            return connection;
        }
        catch (SqliteException ex) when (Unavailable(ex) is { } message)
        {
            connection.Dispose();
            throw new CampaignStoreUnavailableException(message, ex);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    // The daily backup's look ahead at the batch's own schema check, made without the write lock: a migration that lands
    // between the two still has the batch refused, after a backup of the newer file (the narrow race left).
    private bool NewerThanThisBuild()
    {
        using var connection = OpenConnection(SqliteOpenMode.ReadWrite);
        return CampaignDbMigrator.UserVersion(connection, null) > CampaignDbMigrator.LatestVersion;
    }

    // The per-use half of the newer-schema rule (class summary); the migrator refuses the same file at EnsureReady.
    private void RefuseNewerSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var version = CampaignDbMigrator.UserVersion(connection, transaction);
        if (version > CampaignDbMigrator.LatestVersion)
        {
            throw new CampaignStoreUnavailableException(
                CampaignDbMigrator.NewerSchemaMessage(Path, version, CampaignDbMigrator.LatestVersion));
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime);

    private string? Unavailable(SqliteException ex) => UnavailableMessage(ex.SqliteErrorCode, Path, Backups.DirectoryPath);

    // The user-facing reason for the SQLite failures a person can fix; null for everything else (bugs propagate as is).
    // SQLITE_BUSY (5) is here because it is the environment, not a bug: every write is BEGIN IMMEDIATE, so it only means
    // another process held the lock past busy_timeout (a long migration or restore in another session). Compared on the
    // primary code (the low byte), so an extended code such as SQLITE_BUSY_SNAPSHOT (517) maps as its primary.
    private static string? UnavailableMessage(int code, string path, string backupsDirectory) => (code & 0xFF) switch
    {
        5 =>
            $"campaigns.db at {path} is locked by another dnd-mcp process (another session's server, or the dnd-mcp " +
            "command line) that held it for more than 5 seconds, so this call did nothing. Try again; if it keeps " +
            "happening, close the other process.",
        3 or 8 or 14 =>
            $"Cannot open or write campaigns.db at {path}: the file or its directory is not readable and writable by this " +
            $"user. Fix the permissions, or set DND_MCP_DB (a file) or {DndMcpPaths.DataDirectoryVariable} (a directory) to " +
            "a location this user can write.",
        11 or 26 =>
            $"campaigns.db at {path} is damaged or is not a dnd-mcp database, so campaign tools cannot use it. Nothing was " +
            $"changed. Restore a backup from {backupsDirectory} with dnd-mcp's restore command, or move the file " +
            "aside to start a new one.",
        13 => $"The disk holding campaigns.db at {path} is full. Free some space; nothing was written.",
        10 => $"A disk I/O error stopped dnd-mcp reading or writing campaigns.db at {path}. Check the disk, then try again.",
        _ => null,
    };

    // Once per process: a native SQLite without FTS5, JSON or STRICT would otherwise fail inside the migration with a
    // raw SQL error. The host probes at startup too; this covers the CLI and tests. WAL is left out of the verdict: the
    // probe's scratch file says only whether the library can do WAL on the temp file system, and WAL on campaigns.db's
    // own file system is checked by the migrator, which warns and carries on (contract §3.8). A temp directory the
    // process cannot write (a sandbox) skips the probe with a warning rather than blocking campaigns over litter.
    private void EnsureSqliteSupported()
    {
        if (Volatile.Read(ref s_probed))
        {
            return;
        }

        lock (ProbeLock)
        {
            if (s_probed)
            {
                return;
            }

            try
            {
                (SqliteCapabilities.Probe() with { WalJournalMode = true }).EnsureSupported();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(ex, "Could not probe the SQLite library in the temp directory; continuing without the check.");
            }

            Volatile.Write(ref s_probed, true);
        }
    }
}
