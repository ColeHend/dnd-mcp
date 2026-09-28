using System.Diagnostics;
using System.Globalization;
using DndMcp.Repository;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace DndMcp.Hosting;

/// <summary>
/// The one rules index this process serves from: srd.db reused when current, otherwise rebuilt, opened once and shared
/// by every rules tool call.
///
/// <para>
/// <b>The MCP handshake never waits on it.</b> Claude Code gives a server 30 s to answer <c>initialize</c>, and a build
/// after an install or update takes about a second (longer on a slow disk). So the work runs on the thread pool: started
/// early by <see cref="SrdIndexWarmup"/> so it is usually done before the first rules call, and started by the first
/// call otherwise (the in-memory test harness runs no hosted services). Callers share one attempt, so two calls
/// arriving together never build twice.
/// </para>
/// <para>
/// <b>Failures say what to do.</b> Content that is missing or fails verification is a
/// <see cref="SrdIndexUnavailableException"/>, whose message the call-tool filter hands to the model unchanged; any
/// other exception is a bug and reaches the model as the SDK's generic error, with the detail in the stderr log. A
/// failed attempt is not kept: the next call tries again, so a transient failure (a full disk, another server swapping
/// srd.db at that moment) does not disable rules lookup for the rest of a long session.
/// </para>
/// <para>
/// <b>An unwritable cache is not fatal.</b> If the cache directory cannot be created or written (a read-only home, a
/// path that is a file, no home directory at all), the index is built into a private temporary directory instead and
/// a warning says how to fix it. Rules lookup then works, just without reuse between sessions.
/// </para>
/// <para>
/// <b>Deleting or replacing srd.db mid-session is survivable.</b> The README promises deleting the cache is always safe,
/// and another dnd-mcp version (an upgrade, the README's <c>dnd-dev</c> registration) may rebuild srd.db while this
/// server runs. When a query finds its index can no longer be used, the tool calls <see cref="Invalidate"/> and asks
/// again: the next attempt reopens a current srd.db or rebuilds it, instead of every call failing until a restart the
/// model cannot perform.
/// </para>
/// <para>
/// <b>Shutdown lets a running build finish</b> (up to <see cref="DndMcpServerOptions.ShutdownBuildWait"/>): a session
/// that ends during the warm-up build would otherwise exit under it, and the runtime kills the build thread before it
/// deletes its temporary file.
/// </para>
/// </summary>
public sealed class SrdIndexService : IDisposable
{
    // Often enough that Claude Code's idle timer and the user see the wait moving; rarely enough to be noise.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(1);

    // SQLite result codes for "cannot write here": permission, read-only file system, I/O error, disk full, cannot open.
    private static readonly HashSet<int> UnwritableSqliteCodes = [3, 8, 10, 13, 14];

    private readonly DndMcpServerOptions _options;
    private readonly ILogger<SrdIndexService> _logger;
    private readonly Func<string, string, bool, SrdIndexOpenResult> _openOrBuild;
    private readonly Lock _gate = new();

    // Indexes invalidated while calls may still be using them: closed when the service is disposed, not before, because a
    // call that fetched one a moment earlier would otherwise fail with ObjectDisposedException, the SDK's generic error.
    private readonly List<SrdIndex> _retired = [];
    private Task<SrdIndex>? _current;
    private string? _fallbackDirectory;
    private bool _disposed;

    public SrdIndexService(DndMcpServerOptions options, ILogger<SrdIndexService> logger)
        : this(options, logger, SrdIndexOpener.OpenOrBuild)
    {
    }

    /// <param name="openOrBuild">
    /// Stands in for <see cref="SrdIndexOpener.OpenOrBuild"/> (content root, database path, force) so tests can hold a
    /// build open to observe waiting, progress, timeout and cancellation, which a real half-second build cannot show.
    /// </param>
    internal SrdIndexService(
        DndMcpServerOptions options,
        ILogger<SrdIndexService> logger,
        Func<string, string, bool, SrdIndexOpenResult> openOrBuild)
    {
        _options = options;
        _logger = logger;
        _openOrBuild = openOrBuild;
    }

    /// <summary>
    /// Starts opening (or building) the index unless an attempt is running or has succeeded. Returns at once.
    /// </summary>
    public void Start() => _ = Current();

    /// <summary>
    /// The open index, waiting for it if necessary: up to <see cref="DndMcpServerOptions.IndexWaitTimeout"/>, reporting
    /// progress every second while it waits, and stopping as soon as <paramref name="cancellationToken"/> fires.
    /// </summary>
    /// <exception cref="SrdIndexUnavailableException">The index cannot be built; the message says why and what to do.</exception>
    /// <exception cref="McpException">The index is still being built when the wait times out.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled while waiting.</exception>
    public async Task<SrdIndex> GetIndexAsync(IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var task = Current();
        if (task.IsCompleted)
        {
            return await task;
        }

        var timeout = _options.IndexWaitTimeout;
        var waited = Stopwatch.StartNew();
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Message = "Preparing the SRD rules index (built once after an install or update; usually about a second).",
        });

        while (true)
        {
            var remaining = timeout - waited.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                throw new McpException(
                    $"The SRD rules index is still being built (waited {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s). " +
                    "Try the same call again in a few seconds; the build carries on in the background.");
            }

            try
            {
                return await task.WaitAsync(remaining < ProgressInterval ? remaining : ProgressInterval, cancellationToken);
            }
            catch (TimeoutException)
            {
                if (task.IsCompleted)
                {
                    // The build finished just as this wait timed out. Filtering the catch on "not completed" let that
                    // TimeoutException escape as the SDK's generic error (seen when a simulation and a search both waited on
                    // the first build); the finished task has the answer, or the build's own exception.
                    return await task;
                }

                // Progress must increase with every notification (MCP spec), so it is the seconds waited so far.
                progress?.Report(new ProgressNotificationValue
                {
                    Progress = (float)waited.Elapsed.TotalSeconds,
                    Message = $"Still building the SRD rules index ({waited.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s).",
                });
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="query"/> on the index, once more on a freshly opened one if the first raised
    /// <see cref="SrdIndexUnavailableException"/>: srd.db was deleted, replaced by a different build or damaged in place
    /// while this server ran. The README promises deleting the cache is always safe, and "restart the server" is advice
    /// the model cannot follow, so the call reopens (or rebuilds) the index itself. A second failure is real and reaches
    /// the model. Every tool that reads the index goes through here, so none can forget the retry.
    /// </summary>
    public async Task<T> QueryAsync<T>(
        Func<SrdIndex, T> query, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var index = await GetIndexAsync(progress, cancellationToken);
        try
        {
            return query(index);
        }
        catch (SrdIndexUnavailableException)
        {
            Invalidate(index);
            var reopened = await GetIndexAsync(progress, cancellationToken);
            if (ReferenceEquals(reopened, index))
            {
                throw;
            }

            return query(reopened);
        }
    }

    /// <summary>
    /// Stops handing out <paramref name="index"/> after a query on it raised <see cref="SrdIndexUnavailableException"/>
    /// (srd.db deleted, or replaced by a different build), so the next <see cref="GetIndexAsync"/> opens a current srd.db
    /// or rebuilds one. Does nothing when <paramref name="index"/> is no longer the current index: parallel calls that all
    /// failed on the old one each report it, and only the first may start a reopen, or each would discard the fresh index
    /// the one before it opened.
    /// </summary>
    public void Invalidate(SrdIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        lock (_gate)
        {
            if (_disposed || _current is not { IsCompletedSuccessfully: true } current || !ReferenceEquals(current.Result, index))
            {
                return;
            }

            _current = null;
            _retired.Add(index);
        }

        _logger.LogWarning(
            "srd.db at {Path} was deleted or replaced while this server was using it; opening the SRD index again.", index.DatabasePath);
    }

    /// <summary>
    /// Waits for a build still running, up to <see cref="DndMcpServerOptions.ShutdownBuildWait"/> or until
    /// <paramref name="cancellationToken"/> fires, so stopping the server does not cut a build off before it cleans up its
    /// temporary files. Never throws: a build that failed has already been logged, and at shutdown nothing else is owed.
    /// </summary>
    public async Task WaitForBuildAsync(CancellationToken cancellationToken)
    {
        Task<SrdIndex>? task;
        lock (_gate)
        {
            task = _current;
        }

        if (task is null || task.IsCompleted)
        {
            return;
        }

        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(task, Task.Delay(_options.ShutdownBuildWait, stopWaiting.Token)).ConfigureAwait(false);
        await stopWaiting.CancelAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        Task<SrdIndex>? task;
        List<SrdIndex> retired;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            task = _current;
            retired = [.. _retired];
            _retired.Clear();
        }

        foreach (var index in retired)
        {
            index.Dispose();
        }

        if (task is null || task.IsCompleted)
        {
            Release(task);
            return;
        }

        // Still building when the container is disposed (SrdIndexWarmup.StopAsync already waited as long as it may): the
        // index is closed, and any private directory removed, when the build finishes.
        task.ContinueWith(Release, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private Task<SrdIndex> Current()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current is null || _current.IsFaulted || _current.IsCanceled)
            {
                _current = Task.Run(OpenOrBuild);

                // A failure nobody awaits (the warm-up's, when no rules call follows) is already logged; observing it here
                // keeps it from resurfacing as an UnobservedTaskException.
                _current.ContinueWith(
                    t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            return _current;
        }
    }

    private SrdIndex OpenOrBuild()
    {
        foreach (var warning in _options.PathWarnings())
        {
            _logger.LogWarning("SRD index path: {Warning}", warning);
        }

        string databasePath;
        try
        {
            databasePath = _options.ResolveSrdDatabasePath();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                "No cache directory for the SRD index ({Problem}); building a private copy in a temporary directory instead. " +
                "Set {Variable} to a writable directory to keep one index between sessions.",
                ex.Message, DndMcpPaths.CacheDirectoryVariable);
            return OpenOrBuildInFallback(ex.Message);
        }

        try
        {
            return Ready(_openOrBuild(_options.ContentRoot, databasePath, false), databasePath);
        }
        catch (Exception ex) when (IsUnwritable(ex))
        {
            _logger.LogWarning(
                ex,
                "Cannot write the SRD index at {Path} ({Problem}); building a private copy in a temporary directory instead. " +
                "Set {Variable} to a writable directory to keep one index between sessions.",
                databasePath, ex.Message, DndMcpPaths.CacheDirectoryVariable);
            return OpenOrBuildInFallback($"{databasePath}: {ex.Message}");
        }
        catch (SrdIndexUnavailableException ex)
        {
            _logger.LogError("The SRD rules index is unavailable: {Problem}", ex.Message);
            throw;
        }
    }

    private SrdIndex OpenOrBuildInFallback(string cacheProblem)
    {
        string databasePath;
        try
        {
            lock (_gate)
            {
                _fallbackDirectory ??= Directory.CreateTempSubdirectory("dnd-mcp-srd-").FullName;
                databasePath = Path.Combine(_fallbackDirectory, DndMcpPaths.SrdDatabaseFileName);
            }

            return Ready(_openOrBuild(_options.ContentRoot, databasePath, false), databasePath);
        }
        catch (Exception ex) when (IsUnwritable(ex))
        {
            var message =
                $"Cannot write the SRD rules index: the cache failed ({cacheProblem}) and so did a temporary directory " +
                $"({ex.Message}). Set {DndMcpPaths.CacheDirectoryVariable} to a writable directory and restart the server.";
            _logger.LogError(ex, "The SRD rules index is unavailable: {Problem}", message);
            throw new SrdIndexUnavailableException(message, ex);
        }
        catch (SrdIndexUnavailableException ex)
        {
            _logger.LogError("The SRD rules index is unavailable: {Problem}", ex.Message);
            throw;
        }
    }

    private SrdIndex Ready(SrdIndexOpenResult result, string databasePath)
    {
        var index = result.Index;
        var perEdition = string.Join(", ", index.Counts()
            .GroupBy(c => c.Edition)
            .Select(g => $"{g.Sum(c => c.Count).ToString(CultureInfo.InvariantCulture)} {g.Key}"));

        _logger.LogInformation(
            "SRD index {Outcome} ({Reason}): {Documents} documents ({PerEdition}) from {ContentTag}, {Milliseconds} ms, at {Path}",
            result.Rebuilt ? "rebuilt" : "reused",
            result.Reason,
            index.Info.DocumentCount,
            perEdition,
            index.Info.ContentTag,
            (long)result.Elapsed.TotalMilliseconds,
            databasePath);

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("SRD index build: {Warning}", warning);
        }

        return index;
    }

    private void Release(Task<SrdIndex>? task)
    {
        if (task is { IsCompletedSuccessfully: true })
        {
            task.Result.Dispose();
        }

        string? fallback;
        lock (_gate)
        {
            fallback = _fallbackDirectory;
        }

        if (fallback is not null)
        {
            try
            {
                Directory.Delete(fallback, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary directory costs disk space only; the OS temp cleaner will get it.
            }
        }
    }

    // "Cannot create or write the target": directory creation and the final rename throw IOException or
    // UnauthorizedAccessException; SQLite creating the temp database in an unwritable directory throws its own codes.
    // A SqliteException with any other code is a bug in the builder and must not be retried somewhere else.
    private static bool IsUnwritable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException ||
        ex is SqliteException sqlite && UnwritableSqliteCodes.Contains(sqlite.SqliteErrorCode);
}

/// <summary>
/// Starts the SRD index as the host starts, so it is ready by the time the model first asks a rules question.
/// <see cref="StartAsync"/> returns at once: hosted services start in order, and the stdio transport after this one
/// must answer <c>initialize</c> without waiting on a build. <see cref="StopAsync"/> gives a build still running a
/// bounded time to finish (<see cref="SrdIndexService.WaitForBuildAsync"/>), so a session that ends during the warm-up
/// does not leave a half-written temporary database in the user's cache.
/// </summary>
public sealed class SrdIndexWarmup : IHostedService
{
    private readonly SrdIndexService _index;

    public SrdIndexWarmup(SrdIndexService index)
    {
        _index = index;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _index.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _index.WaitForBuildAsync(cancellationToken);
}
