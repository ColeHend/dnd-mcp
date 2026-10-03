using DndMcp.Cli;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DndMcp.Hosting;

/// <summary>
/// Checks, once as the server starts, that the native SQLite library loaded into this process can do everything srd.db
/// and campaigns.db are built on (<see cref="SqliteCapabilities"/>: FTS5 with its tokenizer and bm25, the JSON functions,
/// STRICT and WITHOUT ROWID tables, partial and JSON expression indexes, RAISE(ABORT) triggers, VACUUM INTO), logs what it
/// found to stderr, and stops the start when anything is missing.
///
/// <para>
/// <b>Why at startup, and fatal.</b> Every one of those features belongs to the native library, not to
/// Microsoft.Data.Sqlite: a changed SQLitePCLRaw bundle or a system libsqlite3 loaded instead can lack any of them, and
/// the first symptom would otherwise be a migration dying half-way with "no such module: fts5", or a rules search failing
/// mid-session. One failed start that names every gap at once (PLAN.md: fail at startup) is the only message a user can
/// act on; Program.cs turns it into exit code 1 and one line on stderr (<see cref="DndMcpCli.RunServerAsync"/>), and the
/// refusal is a <see cref="StartupRefusedException"/> so the host's own stack-trace repeat of it is dropped
/// (<see cref="ReportedStartFailureLogFilter"/>). It is all or nothing on purpose: the rules index needs FTS5 too, so a
/// server that started without it would fail anyway.
/// </para>
/// <para>
/// <b>Registered before <see cref="SrdIndexWarmup"/></b> and the transport, so it runs first (hosted services start in
/// registration order) and the process never answers <c>initialize</c> with a library it cannot use. It costs a few tens of
/// milliseconds and never touches campaigns.db or srd.db: checking WAL on a real database would change its journal mode.
/// </para>
/// <para>
/// <b>Where it probes, and what is not fatal.</b> The scratch directory is the data directory (created if needed), not
/// <c>/tmp</c>: that is where campaigns.db will live, so its file system is the one whose WAL support matters, and a
/// sandbox with a read-only temp directory must not stop the server over probe litter. An I/O failure there (no home
/// directory, an unwritable data directory) is a logged warning, never a crash: dice and rules do not need the data
/// directory, and <c>CampaignDatabase</c> runs its own probe before it first opens campaigns.db. WAL is reported but not
/// fatal either: it depends on the file system (a network share cannot map its shared memory), and the migrator checks
/// WAL on campaigns.db itself, warning and carrying on in rollback-journal mode (contract §3.8).
/// </para>
/// <para>
/// The in-memory test harness runs no hosted services, so this runs only in a real process; its tests call
/// <see cref="Check"/> directly and run the built binary.
/// </para>
/// </summary>
public sealed class SqliteCapabilityCheck : IHostedService
{
    private readonly DndMcpServerOptions _options;
    private readonly ILogger<SqliteCapabilityCheck> _logger;
    private readonly Func<string, SqliteCapabilities> _probe;

    public SqliteCapabilityCheck(DndMcpServerOptions options, ILogger<SqliteCapabilityCheck> logger)
        : this(options, logger, SqliteCapabilities.Probe)
    {
    }

    /// <param name="probe">Probes in a scratch subdirectory of the directory given: tests stand in a library with gaps.</param>
    internal SqliteCapabilityCheck(DndMcpServerOptions options, ILogger<SqliteCapabilityCheck> logger, Func<string, SqliteCapabilities> probe)
    {
        _options = options;
        _logger = logger;
        _probe = probe;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Check();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Probes, logs one line with the version, the native library and the verdict, and throws when a required feature is
    /// missing. Returns the capabilities, or null when the probe could not run (logged as a warning).
    /// </summary>
    /// <exception cref="StartupRefusedException">A required feature is missing; the message lists every gap.</exception>
    internal SqliteCapabilities? Check()
    {
        string directory;
        try
        {
            directory = _options.ResolveDataDirectory();
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex,
                "Could not check the SQLite library at startup: the data directory cannot be created or decided. Dice and rules " +
                "work without it; campaign tools check the library again before they first open campaigns.db.");
            return null;
        }

        SqliteCapabilities capabilities;
        try
        {
            capabilities = _probe(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // A SqliteException can only escape the probe from opening its scratch file (each feature check catches its
            // own), so it is the directory, not the library.
            _logger.LogWarning(ex,
                "Could not check the SQLite library at startup: the probe could not write in {Directory}. Dice and rules work " +
                "without it; campaign tools check the library again before they first open campaigns.db.", directory);
            return null;
        }

        // WAL is judged on campaigns.db's own file by the migrator (warn and continue), never here.
        var verdict = capabilities with { WalJournalMode = true };
        var missing = verdict.MissingFeatures();
        if (missing.Count == 0)
        {
            _logger.LogInformation(
                "SQLite {Version} ({NativeLibrary}) has every feature dnd-mcp needs: FTS5 (porter/unicode61, prefix indexes, " +
                "bm25), JSON functions, STRICT and WITHOUT ROWID tables, partial and JSON expression indexes, RAISE(ABORT) " +
                "triggers and VACUUM INTO; WAL {Wal}.",
                capabilities.Version, capabilities.NativeLibrary, capabilities.WalJournalMode ? "works" : "is unavailable");
        }
        else
        {
            _logger.LogError(
                "SQLite {Version} ({NativeLibrary}) is missing what dnd-mcp needs: {Missing}. The server will not start.",
                capabilities.Version, capabilities.NativeLibrary, string.Join("; ", missing));
        }

        if (!capabilities.WalJournalMode)
        {
            _logger.LogWarning(
                "SQLite cannot use WAL in {Directory} (a network or unusual file system?). campaigns.db will use a rollback " +
                "journal there: it works, but a long write in one session makes others wait.", directory);
        }

        try
        {
            verdict.EnsureSupported();
        }
        catch (InvalidOperationException ex)
        {
            // Logged above in full; the refusal type lets the host's own repeat of it be dropped (ReportedStartFailureLogFilter).
            throw new StartupRefusedException(ex.Message, ex);
        }

        return capabilities;
    }
}
