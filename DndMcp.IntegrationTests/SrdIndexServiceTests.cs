using System.Diagnostics;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository;
using DndMcp.Repository.Srd.Index;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: the server gets its rules index without ever holding up the MCP handshake, reuses a current srd.db, builds
/// somewhere private when the cache cannot be written, reopens it when srd.db is deleted or replaced under it, lets a
/// build still running at shutdown finish, and when it has no index says why — to the model as an actionable tool error
/// and to stderr — while everything that does not need the index keeps working.
///
/// <para>
/// Each of these fails quietly. A warm-up that awaited the build would hold <c>initialize</c> past Claude Code's 30 s on a
/// slow disk and the server would never connect; a cached failure would disable rules lookup for a whole session after
/// one transient error, and so would an index kept after its file was deleted; a read-only cache (SQLite reports it with
/// its own error codes, not an IOException) would break rules lookup outright; a shutdown that cut a build off left a
/// multi-megabyte temporary file in the user's cache; and a missing <c>content/</c> directory (a publish copied without
/// it) would reach the model as the SDK's bare "An error occurred". A real build takes half a second, too short to
/// observe waiting, so some tests hold the build open with a gated stand-in for <see cref="SrdIndexOpener.OpenOrBuild"/>
/// that then opens the real index.
/// </para>
/// </summary>
public sealed class SrdIndexServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task GetIndexAsync_NoIndexYet_BuildsOneAndLogsWhy()
    {
        using var cache = new TempDirectory();
        var log = new CapturedLog();
        using var service = new SrdIndexService(Options(cache.Path), Logger(log));

        var index = await service.GetIndexAsync(null, CancellationToken.None);

        Assert.Equal(Path.Combine(cache.Path, "srd.db"), index.DatabasePath);
        var entry = Assert.Single(log.Entries, e => e.Message.StartsWith("SRD index ", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith(
            $"SRD index rebuilt (no index at {Path.Combine(cache.Path, "srd.db")}): 4602 documents (2415 2014, 2187 2024) from 5e-database-v7.0.0, ",
            entry.Message,
            StringComparison.Ordinal);
        Assert.EndsWith($" ms, at {Path.Combine(cache.Path, "srd.db")}", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetIndexAsync_CurrentIndexInTheCache_ReusesItInsteadOfRebuilding()
    {
        using var cache = new TempDirectory();
        using (var first = new SrdIndexService(Options(cache.Path), Logger(new CapturedLog())))
        {
            await first.GetIndexAsync(null, CancellationToken.None);
        }

        var built = File.GetLastWriteTimeUtc(Path.Combine(cache.Path, "srd.db"));
        var log = new CapturedLog();
        using var second = new SrdIndexService(Options(cache.Path), Logger(log));
        await second.GetIndexAsync(null, CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Message.StartsWith("SRD index reused (srd.db is current): 4602 documents", StringComparison.Ordinal));
        Assert.Equal(built, File.GetLastWriteTimeUtc(Path.Combine(cache.Path, "srd.db")));
    }

    [Fact]
    public async Task GetIndexAsync_CacheDirectoryIsAFile_BuildsInAPrivateTemporaryDirectoryAndWarns()
    {
        using var cache = new TempDirectory();
        var notADirectory = Path.Combine(cache.Path, "cache-is-a-file");
        await File.WriteAllTextAsync(notADirectory, "x");
        var log = new CapturedLog();
        var service = new SrdIndexService(Options(notADirectory), Logger(log));

        var index = await service.GetIndexAsync(null, CancellationToken.None);
        var privateDirectory = Path.GetDirectoryName(index.DatabasePath)!;

        Assert.StartsWith("dnd-mcp-srd-", Path.GetFileName(privateDirectory), StringComparison.Ordinal);
        Assert.Equal("2024/spell/fireball", index.Get("2024", "spell", "fireball")?.Ref.ToString());
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith($"Cannot write the SRD index at {Path.Combine(notADirectory, "srd.db")} (", warning.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            "); building a private copy in a temporary directory instead. Set DND_MCP_CACHE_DIR to a writable directory to keep one index between sessions.",
            warning.Message,
            StringComparison.Ordinal);

        service.Dispose();
        Assert.False(Directory.Exists(privateDirectory), "The private index directory outlived the service.");
    }

    [UnixNonRootFact]
    public async Task GetIndexAsync_ReadOnlyCacheDirectory_BuildsInAPrivateTemporaryDirectory()
    {
        // SQLite, not .NET, creates the database file, so an unwritable directory surfaces as SqliteException
        // (SQLITE_CANTOPEN) rather than IOException. A fallback that only caught I/O exceptions would fail here.
        using var cache = new TempDirectory();
        var readOnly = Directory.CreateDirectory(Path.Combine(cache.Path, "read-only")).FullName;
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        try
        {
            using var service = new SrdIndexService(Options(readOnly), Logger(new CapturedLog()));

            var index = await service.GetIndexAsync(null, CancellationToken.None);

            Assert.StartsWith("dnd-mcp-srd-", Path.GetFileName(Path.GetDirectoryName(index.DatabasePath)), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(readOnly, "srd.db")));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(readOnly, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task GetIndexAsync_NoContent_ThrowsTheActionableReasonAndLogsIt()
    {
        using var content = new TempDirectory();
        var log = new CapturedLog();
        using var service = new SrdIndexService(Options(McpServerHarness.SharedCacheDirectory, content.Path), Logger(log));

        var ex = await Assert.ThrowsAsync<SrdIndexUnavailableException>(() => service.GetIndexAsync(null, CancellationToken.None));

        var expected = $"No content manifest at {Path.Combine(content.Path, "5e-database", "manifest.json")}. " +
                       "Vendor the data with scripts/fetch-5e-database.sh.";
        Assert.Equal(expected, ex.Message);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message == "The SRD rules index is unavailable: " + expected);
    }

    [Fact]
    public async Task GetIndexAsync_AfterAFailedAttempt_TriesAgain()
    {
        // A failure is not remembered: a transient one (disk full, srd.db swapped mid-open) must not end rules lookup for
        // the rest of the session.
        var calls = 0;
        using var service = new SrdIndexService(
            Options(McpServerHarness.SharedCacheDirectory),
            Logger(new CapturedLog()),
            (content, database, force) => Interlocked.Increment(ref calls) == 1
                ? throw new SrdIndexUnavailableException("first attempt fails")
                : SrdIndexOpener.OpenOrBuild(content, database, force));

        await Assert.ThrowsAsync<SrdIndexUnavailableException>(() => service.GetIndexAsync(null, CancellationToken.None));
        var index = await service.GetIndexAsync(null, CancellationToken.None);

        Assert.NotNull(index.Get("2024", "spell", "fireball"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetIndexAsync_ConcurrentCallers_ShareOneAttempt()
    {
        var gate = new GatedOpener();
        using var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));

        service.Start();
        var waiters = Enumerable.Range(0, 5).Select(_ => service.GetIndexAsync(null, CancellationToken.None)).ToList();
        await gate.WaitForCallsAsync(1);
        gate.Open();
        var indexes = await Task.WhenAll(waiters);

        Assert.All(indexes, index => Assert.Same(indexes[0], index));
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task Warmup_StartAsync_ReturnsAtOnceAndStartsTheBuild()
    {
        // The stdio transport starts after this hosted service; if StartAsync waited for the build, so would initialize.
        var gate = new GatedOpener();
        using var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));

        var start = new SrdIndexWarmup(service).StartAsync(CancellationToken.None);

        Assert.True(start.IsCompletedSuccessfully);
        await gate.WaitForCallsAsync(1);
        gate.Open();
        Assert.NotNull((await service.GetIndexAsync(null, CancellationToken.None)).Get("2014", "spell", "fireball"));
    }

    [Fact]
    public async Task GetIndexAsync_WhileTheBuildRuns_ReportsProgressThatAlwaysIncreases()
    {
        var gate = new GatedOpener();
        using var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));
        var progress = new RecordingProgress(openAfter: 3, gate);

        await service.GetIndexAsync(progress, CancellationToken.None);

        var reports = progress.Reports;
        Assert.True(reports.Count >= 3, $"{reports.Count} progress reports.");
        Assert.Equal(0, reports[0].Progress);
        Assert.Equal("Preparing the SRD rules index (built once after an install or update; usually about a second).", reports[0].Message);
        Assert.All(reports.Skip(1), r => Assert.StartsWith("Still building the SRD rules index (", r.Message, StringComparison.Ordinal));
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.Second.Progress > pair.First.Progress, "Progress went backwards."));
    }

    [Fact]
    public async Task GetIndexAsync_IndexAlreadyOpen_ReportsNoProgress()
    {
        using var service = new SrdIndexService(Options(McpServerHarness.SharedCacheDirectory), Logger(new CapturedLog()));
        await service.GetIndexAsync(null, CancellationToken.None);
        var progress = new RecordingProgress(openAfter: int.MaxValue, gate: null);

        await service.GetIndexAsync(progress, CancellationToken.None);

        Assert.Empty(progress.Reports);
    }

    [Fact]
    public async Task GetIndexAsync_BuildOutlastsTheWait_SaysItIsStillBuildingAndLetsItFinish()
    {
        var gate = new GatedOpener();
        var options = Options(McpServerHarness.SharedCacheDirectory);
        options.IndexWaitTimeout = TimeSpan.FromSeconds(1);
        using var service = gate.CreateService(options);

        var ex = await Assert.ThrowsAsync<McpException>(() => service.GetIndexAsync(null, CancellationToken.None));

        Assert.Equal(
            "The SRD rules index is still being built (waited 1 s). Try the same call again in a few seconds; the build carries on " +
            "in the background.",
            ex.Message);
        gate.Open();
        Assert.NotNull(await service.GetIndexAsync(null, CancellationToken.None));
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task GetIndexAsync_TimeoutShorterThanTheProgressInterval_GivesUpAtTheTimeout()
    {
        // The wait is sliced into one-second progress steps; the last slice must be cut to what is left, or the 25 s budget
        // (chosen to stay under Claude Code's 30 s request timeout) overshoots by up to a second.
        var gate = new GatedOpener();
        var options = Options(McpServerHarness.SharedCacheDirectory);
        options.IndexWaitTimeout = TimeSpan.FromMilliseconds(200);
        using var service = gate.CreateService(options);
        var waited = Stopwatch.StartNew();

        await Assert.ThrowsAsync<McpException>(() => service.GetIndexAsync(null, CancellationToken.None));

        Assert.True(waited.Elapsed < TimeSpan.FromMilliseconds(800), $"Gave up after {waited.Elapsed.TotalMilliseconds:0} ms.");
        gate.Open();
    }

    [Fact]
    public async Task GetIndexAsync_CacheAndTemporaryDirectoryBothUnwritable_SaysSoAndHowToFixIt()
    {
        // Without this translation the second failure is a bare IOException: the model gets the SDK's generic error and the
        // user no hint that both places refused.
        var log = new CapturedLog();
        using var service = new SrdIndexService(
            Options(McpServerHarness.SharedCacheDirectory), Logger(log), (_, path, _) => throw new IOException($"disk full at {path}"));

        var ex = await Assert.ThrowsAsync<SrdIndexUnavailableException>(() => service.GetIndexAsync(null, CancellationToken.None));

        Assert.StartsWith(
            $"Cannot write the SRD rules index: the cache failed ({Path.Combine(McpServerHarness.SharedCacheDirectory, "srd.db")}: disk full at ",
            ex.Message,
            StringComparison.Ordinal);
        Assert.EndsWith(". Set DND_MCP_CACHE_DIR to a writable directory and restart the server.", ex.Message, StringComparison.Ordinal);
        Assert.Contains(") and so did a temporary directory (disk full at ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetIndexAsync_NoHomeDirectoryAndNoOverride_BuildsInAPrivateTemporaryDirectory()
    {
        // A service account or container may have no home directory; rules lookup must still work, from a private copy.
        var log = new CapturedLog();
        var options = new DndMcpServerOptions { Paths = () => new DndMcpPaths(_ => null, homeDirectory: null) };

        // First, so that options ignoring Paths fail here instead of building in the developer's real ~/.cache/dnd-mcp.
        Assert.Throws<InvalidOperationException>(() => options.ResolveSrdDatabasePath());
        var service = new SrdIndexService(options, Logger(log));

        var index = await service.GetIndexAsync(null, CancellationToken.None);
        var privateDirectory = Path.GetDirectoryName(index.DatabasePath)!;

        Assert.StartsWith("dnd-mcp-srd-", Path.GetFileName(privateDirectory), StringComparison.Ordinal);
        Assert.NotNull(index.Get("2024", "spell", "fireball"));
        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.StartsWith("No cache directory for the SRD index (", warning.Message, StringComparison.Ordinal);
        service.Dispose();
        Assert.False(Directory.Exists(privateDirectory), "The private index directory outlived the service.");
    }

    [Fact]
    public async Task GetIndexAsync_RelativeCacheOverride_LogsThatItWasIgnored()
    {
        // MCP configs are JSON, so "~/x" or "cache" arrives unexpanded; the setting is ignored, and the log must say so or
        // the user never learns why the index is not where they put it.
        var log = new CapturedLog();
        var home = Path.Combine(Path.GetTempPath(), "dnd-mcp-home-" + Guid.NewGuid().ToString("N"));
        var options = new DndMcpServerOptions
        {
            Paths = () => new DndMcpPaths(name => name == DndMcpPaths.CacheDirectoryVariable ? "relative/cache" : null, home),
        };

        try
        {
            using var service = new SrdIndexService(options, Logger(log));
            await service.GetIndexAsync(null, CancellationToken.None);

            var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("SRD index path: ", StringComparison.Ordinal));
            Assert.Contains(DndMcpPaths.CacheDirectoryVariable, warning.Message, StringComparison.Ordinal);
            Assert.Contains("relative/cache", warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(home))
            {
                Directory.Delete(home, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetIndexAsync_RetryAfterAFailureInThePrivateDirectory_ReusesThatDirectory()
    {
        // One private directory per service: a retry that made a new one would leave the first behind for the OS to find.
        var fallbackPaths = new List<string>();
        var fallbackAttempts = 0;
        using var cache = new TempDirectory();
        var notADirectory = Path.Combine(cache.Path, "cache-is-a-file");
        await File.WriteAllTextAsync(notADirectory, "x");
        var service = new SrdIndexService(Options(notADirectory), Logger(new CapturedLog()), (content, path, force) =>
        {
            if (path.StartsWith(notADirectory, StringComparison.Ordinal))
            {
                throw new IOException("not a directory");
            }

            lock (fallbackPaths)
            {
                fallbackPaths.Add(path);
            }

            return Interlocked.Increment(ref fallbackAttempts) == 1
                ? throw new SrdIndexUnavailableException("first attempt fails")
                : SrdIndexOpener.OpenOrBuild(content, path, force);
        });

        await Assert.ThrowsAsync<SrdIndexUnavailableException>(() => service.GetIndexAsync(null, CancellationToken.None));
        var index = await service.GetIndexAsync(null, CancellationToken.None);

        Assert.Equal(2, fallbackPaths.Count);
        Assert.Equal(fallbackPaths[0], fallbackPaths[1]);
        Assert.Equal(fallbackPaths[1], index.DatabasePath);
        service.Dispose();
        Assert.False(Directory.Exists(Path.GetDirectoryName(fallbackPaths[0])), "The private index directory outlived the service.");
    }

    [Fact]
    public async Task Invalidate_TheCurrentIndex_NextCallOpensItAgain()
    {
        // The safety net for srd.db deleted or replaced under a running server: once a query says the index can no longer be
        // used, the next call must open (or rebuild) a fresh one rather than fail for the rest of the session.
        var calls = 0;
        using var service = new SrdIndexService(
            Options(McpServerHarness.SharedCacheDirectory),
            Logger(new CapturedLog()),
            (content, path, force) =>
            {
                Interlocked.Increment(ref calls);
                return SrdIndexOpener.OpenOrBuild(content, path, force);
            });
        var first = await service.GetIndexAsync(null, CancellationToken.None);

        service.Invalidate(first);
        var second = await service.GetIndexAsync(null, CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(2, calls);
        Assert.NotNull(second.Get("2024", "spell", "fireball"));

        // Calls already holding the old index may still be using it, so it stays open until the service is disposed.
        Assert.NotNull(first.Get("2024", "spell", "fireball"));
    }

    [Fact]
    public async Task Invalidate_AnIndexAlreadyReplaced_ChangesNothing()
    {
        // Parallel calls that all failed on the old index each report it; only the first report may reopen, or every one of
        // them would throw away the fresh index the one before opened.
        var calls = 0;
        using var service = new SrdIndexService(
            Options(McpServerHarness.SharedCacheDirectory),
            Logger(new CapturedLog()),
            (content, path, force) =>
            {
                Interlocked.Increment(ref calls);
                return SrdIndexOpener.OpenOrBuild(content, path, force);
            });
        var first = await service.GetIndexAsync(null, CancellationToken.None);
        service.Invalidate(first);
        var second = await service.GetIndexAsync(null, CancellationToken.None);

        service.Invalidate(first);

        Assert.Same(second, await service.GetIndexAsync(null, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Dispose_AfterInvalidate_ClosesTheReplacedIndexToo()
    {
        var service = new SrdIndexService(Options(McpServerHarness.SharedCacheDirectory), Logger(new CapturedLog()));
        var first = await service.GetIndexAsync(null, CancellationToken.None);
        service.Invalidate(first);
        var second = await service.GetIndexAsync(null, CancellationToken.None);

        service.Dispose();

        Assert.True(IsDisposed(first), "The replaced index was left open.");
        Assert.True(IsDisposed(second), "The current index was left open.");
    }

    [Fact]
    public async Task Warmup_StopAsyncWhileTheBuildRuns_WaitsForItToFinish()
    {
        // A session that ends during the warm-up build (a health check, `claude mcp list` right after install) must not exit
        // mid-build: the runtime would kill the build thread and leave srd.db.<pid>.<guid>.tmp and its journal behind.
        var gate = new GatedOpener();
        using var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));
        var warmup = new SrdIndexWarmup(service);
        await warmup.StartAsync(CancellationToken.None);
        await gate.WaitForCallsAsync(1);

        var stop = warmup.StopAsync(CancellationToken.None);
        await Task.Delay(300);
        Assert.False(stop.IsCompleted, "StopAsync returned while the build was still running.");

        gate.Open();
        await stop.WaitAsync(Patience);
        Assert.True(gate.Opened.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Warmup_StopAsyncWhenTheBuildOutlastsTheLimit_ReturnsAtTheLimit()
    {
        // Shutdown waits for a build only so long: a stuck disk must not keep the process alive past Claude Code's patience.
        var gate = new GatedOpener();
        var options = Options(McpServerHarness.SharedCacheDirectory);
        options.ShutdownBuildWait = TimeSpan.FromMilliseconds(200);
        using var service = gate.CreateService(options);
        var warmup = new SrdIndexWarmup(service);
        await warmup.StartAsync(CancellationToken.None);
        await gate.WaitForCallsAsync(1);
        var waited = Stopwatch.StartNew();

        await warmup.StopAsync(CancellationToken.None).WaitAsync(Patience);

        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(5), $"StopAsync took {waited.Elapsed}.");
        gate.Open();
    }

    [Fact]
    public async Task Warmup_StopAsyncCancelled_ReturnsAtOnce()
    {
        // The host cancels StopAsync when its own shutdown timeout runs out; honouring that keeps shutdown bounded.
        var gate = new GatedOpener();
        using var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));
        var warmup = new SrdIndexWarmup(service);
        await warmup.StartAsync(CancellationToken.None);
        await gate.WaitForCallsAsync(1);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var waited = Stopwatch.StartNew();

        await warmup.StopAsync(cancelled.Token).WaitAsync(Patience);

        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(2), $"StopAsync took {waited.Elapsed}.");
        gate.Open();
    }

    [Fact]
    public async Task RulesTools_SrdDbDeletedMidSession_ParallelCallsAllSucceed()
    {
        // The README says deleting the cache is always safe. Deleting it under a running server must not break the calls
        // that follow, parallel ones included (Claude routinely sends several rules_get at once).
        using var cache = new TempDirectory();
        var server = McpServerHarness.WithOptions(o => o.CacheDirectory = cache.Path);
        await server.InitializeAsync();
        try
        {
            server.SuccessText(await server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["name"] = "Fireball" }));
            Directory.Delete(cache.Path, recursive: true);

            string[] names = ["Wizard", "Fighter", "Cleric", "Druid", "Adult Red Dragon", "Lich", "Grappled", "Shield"];
            var calls = names.Concat(names).Select(name =>
                server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["name"] = name }).AsTask()).ToList();
            var results = await Task.WhenAll(calls);

            Assert.All(results, result => server.SuccessText(result));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    // Overwritten in place (the same inode, so the open connections read the new bytes): SQLite Error 26.
    [InlineData("garbage")]
    // Truncated in place (": > srd.db", an interrupted cp): the schema vanishes and SQLite reports Error 1.
    [InlineData("truncate")]
    public async Task RulesTools_SrdDbDamagedInPlaceMidSession_TheNextCallsReopenAndAnswer(string damage)
    {
        // Pins the tools' safety net (RulesTools.QueryAsync: Invalidate, reopen, run once more). Deleting the file never
        // reaches it (the pool keeps the deleted file open), so without this test the retry could be removed with every
        // suite green, and a damaged srd.db would then fail every rules call until a restart the model cannot perform.
        using var cache = new TempDirectory();
        var server = McpServerHarness.WithOptions(o => o.CacheDirectory = cache.Path);
        await server.InitializeAsync();
        try
        {
            server.SuccessText(await server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["name"] = "Fireball" }));
            var path = Path.Combine(cache.Path, "srd.db");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                if (damage == "truncate")
                {
                    stream.SetLength(0);
                }
                else
                {
                    var bytes = new byte[stream.Length];
                    Array.Fill(bytes, (byte)0x5A);
                    stream.Write(bytes);
                }
            }

            var search = server.SuccessText(await server.Client.CallToolAsync(
                "rules_search", new Dictionary<string, object?> { ["query"] = "grapple", ["edition"] = "both", ["limit"] = 50 }));
            var get = server.SuccessText(await server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["name"] = "Lich" }));

            Assert.StartsWith("**", search, StringComparison.Ordinal);
            Assert.StartsWith("# Lich\n", get, StringComparison.Ordinal);
            Assert.Contains(server.ServerLog.Entries, e => e.Message.Contains("opening the SRD index again", StringComparison.Ordinal));
            var current = await server.Services.GetRequiredService<SrdIndexService>().GetIndexAsync(null, CancellationToken.None);
            using var rebuilt = SrdIndex.TryOpen(path, current.Info.StalenessKey);
            Assert.NotNull(rebuilt);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetIndexAsync_CallerCancelsWhileWaiting_StopsWaitingButNotTheBuild()
    {
        var gate = new GatedOpener();
        using var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var waited = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetIndexAsync(null, cts.Token));

        Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {waited.Elapsed}.");
        gate.Open();
        Assert.NotNull(await service.GetIndexAsync(null, CancellationToken.None));
        Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task Dispose_WhileBuilding_ReturnsAtOnceAndClosesTheIndexWhenTheBuildEnds()
    {
        var gate = new GatedOpener();
        var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));
        service.Start();
        await gate.WaitForCallsAsync(1);

        service.Dispose();
        gate.Open();

        var index = await gate.Opened.Task.WaitAsync(Patience);
        var deadline = Stopwatch.StartNew();
        while (!IsDisposed(index))
        {
            Assert.True(deadline.Elapsed < Patience, "The index built after Dispose was never closed.");
            await Task.Delay(20);
        }

        Assert.Throws<ObjectDisposedException>(() => service.Start());
    }

    [Fact]
    public async Task RulesTools_NoContent_ReturnTheReasonWhileDiceAndTheHandshakeWork()
    {
        using var content = new TempDirectory();
        var server = McpServerHarness.WithOptions(o => o.ContentRoot = content.Path);
        await server.InitializeAsync();
        try
        {
            Assert.False(string.IsNullOrWhiteSpace(server.Client.ServerInstructions));
            Assert.Equal(4, (await server.Client.ListToolsAsync()).Count);

            var expected = $"No content manifest at {Path.Combine(content.Path, "5e-database", "manifest.json")}. " +
                           "Vendor the data with scripts/fetch-5e-database.sh.";
            var search = await server.Client.CallToolAsync("rules_search", new Dictionary<string, object?> { ["query"] = "fireball" });
            Assert.Equal("An error occurred invoking 'rules_search': " + expected, server.ErrorText(search));
            var get = await server.Client.CallToolAsync("rules_get", new Dictionary<string, object?> { ["name"] = "Fireball" });
            Assert.Equal("An error occurred invoking 'rules_get': " + expected, server.ErrorText(get));

            var roll = await server.Client.CallToolAsync("dice_roll", new Dictionary<string, object?> { ["expression"] = "1d20" });
            Assert.StartsWith("**", server.SuccessText(roll), StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RulesTools_ContentDirectoryMissing_SayToCopyTheWholePublishDirectory()
    {
        using var parent = new TempDirectory();
        var missing = Path.Combine(parent.Path, "content");
        var server = McpServerHarness.WithOptions(o => o.ContentRoot = missing);
        await server.InitializeAsync();
        try
        {
            var result = await server.Client.CallToolAsync("rules_search", new Dictionary<string, object?> { ["query"] = "fireball" });

            Assert.Equal(
                $"An error occurred invoking 'rules_search': No SRD content directory at {missing}. The server reads content/ next " +
                "to its executable; copy the whole publish directory, not just the binary.",
                server.ErrorText(result));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    public static TheoryData<string, string, string> ArgumentsOverTheIndexCaps()
    {
        var longQuery = string.Join(" ", Enumerable.Repeat("fireball", 70));
        var manyWords = string.Join(" ", Enumerable.Range(0, 40).Select(i => $"w{i}"));
        var longName = new string('n', 250);
        return new TheoryData<string, string, string>
        {
            {
                "rules_search", $$"""{"query":"{{longQuery}}"}""",
                "The query is 629 characters long; search with at most 500 characters of key words, e.g. \"grapple escape\" or " +
                "\"fire*\". To read one entry, use rules_get with its name."
            },
            {
                "rules_search", $$"""{"query":"{{manyWords}}"}""",
                "The query has 40 different words; search with at most 32 key words, e.g. \"grapple escape\" or \"fire*\"."
            },
            {
                "rules_get", $$"""{"name":"{{longName}}"}""",
                "The name is 250 characters long; names are at most 200 characters. Pass just the entry's name, e.g. \"Fireball\", " +
                "or search for longer text with rules_search."
            },
        };
    }

    [Theory]
    [MemberData(nameof(ArgumentsOverTheIndexCaps))]
    public async Task RulesTools_ArgumentsOverTheIndexCapsWhileTheIndexIsBroken_GetTheCapFirst(string tool, string argumentsJson, string message)
    {
        // The caps are checked before the index is awaited, like every other argument: an over-long query got the index's
        // error (or waited out a first-run build) before being told it was too long.
        using var content = new TempDirectory();
        var server = McpServerHarness.WithOptions(o => o.ContentRoot = content.Path);
        await server.InitializeAsync();
        try
        {
            var result = await server.CallToolJsonAsync(tool, argumentsJson);

            Assert.Equal($"An error occurred invoking '{tool}': {message}", server.ErrorText(result));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(ArgumentsOverTheIndexCaps))]
    public async Task RulesTools_ArgumentsOverTheIndexCaps_GetExactlyTheIndexsOwnMessage(string tool, string argumentsJson, string message)
    {
        // The tools check the caps early with a copy of the index's wording; the index checks them again for any other
        // caller. The two must say the same thing, or the answer depends on which check ran.
        var server = new McpServerHarness();
        await server.InitializeAsync();
        try
        {
            var index = await server.Services.GetRequiredService<SrdIndexService>().GetIndexAsync(null, CancellationToken.None);
            using var arguments = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var indexError = tool == "rules_search"
                ? Assert.Throws<DndMcp.Domain.Core.DndInputException>(() => index.Search(arguments.RootElement.GetProperty("query").GetString()!, ["2024"]))
                : Assert.Throws<DndMcp.Domain.Core.DndInputException>(() => index.FindByName(arguments.RootElement.GetProperty("name").GetString()!, "2024"));

            Assert.Equal(message, indexError.Message);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("rules_search", """{"query":"fireball","limit":0}""", "limit must be between 1 and 50 (got 0).")]
    [InlineData("rules_search", """{"query":"???"}""",
        "The query needs at least one word to search for (letters or digits); \"???\" has none. Example: \"fireball\", " +
        "\"grapple escape\" or a prefix like \"fire*\".")]
    [InlineData("rules_get", """{"name":"???"}""", "The name needs at least one letter or digit; \"???\" has none. Example: name \"Fireball\".")]
    public async Task RulesTools_ArgumentsWrongWhileTheIndexIsBroken_GetTheArgumentCorrectionFirst(string tool, string argumentsJson, string message)
    {
        // Arguments are checked before the index is awaited, so a bad call is corrected even when no index exists (and at
        // once, rather than after waiting out a first-run build).
        using var content = new TempDirectory();
        var server = McpServerHarness.WithOptions(o => o.ContentRoot = content.Path);
        await server.InitializeAsync();
        try
        {
            var result = await server.CallToolJsonAsync(tool, argumentsJson);

            Assert.Equal($"An error occurred invoking '{tool}': {message}", server.ErrorText(result));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("rules_search", """{"query":"fireball"}""")]
    [InlineData("rules_get", """{"name":"Fireball"}""")]
    public async Task RulesTool_WhileTheIndexBuilds_SendsProgressToTheClient(string tool, string argumentsJson)
    {
        var gate = new GatedOpener();
        var service = gate.CreateService(Options(McpServerHarness.SharedCacheDirectory));
        var server = McpServerHarness.WithExtraTools(builder => builder.Services.AddSingleton(service));
        await server.InitializeAsync();
        try
        {
            var progress = new RecordingProgress(openAfter: 1, gate);
            using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

            var result = await server.Client.CallToolAsync(tool, arguments, progress);

            Assert.Contains("fireball", server.SuccessText(result), StringComparison.Ordinal);
            Assert.Contains(progress.Reports, r => r.Message?.StartsWith("Preparing the SRD rules index", StringComparison.Ordinal) == true);
        }
        finally
        {
            await server.DisposeAsync();

            // Registered as an instance, so the container does not own (or dispose) it.
            service.Dispose();
        }
    }

    [Fact]
    public async Task Harness_RulesIndex_LivesUnderTheTestOutputNeverInTheUserCache()
    {
        // In-memory servers run with the developer's environment; left to it, every test run would rebuild the user's own
        // ~/.cache/dnd-mcp/srd.db, and a test pointing the server at broken content would do so with broken content.
        var server = new McpServerHarness();
        await server.InitializeAsync();
        try
        {
            var index = await server.Services.GetRequiredService<SrdIndexService>().GetIndexAsync(null, CancellationToken.None);

            Assert.Equal(Path.Combine(McpServerHarness.SharedCacheDirectory, "srd.db"), index.DatabasePath);
            Assert.StartsWith(AppContext.BaseDirectory, index.DatabasePath, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Harness_DataDirectory_LivesUnderTheTestOutputNeverInTheUsersData()
    {
        // Phase 6 opens campaigns.db through DndMcpServerOptions.ResolveDataDirectory; an in-memory server left to the
        // developer's environment would open the user's real campaigns.
        var server = new McpServerHarness();
        await server.InitializeAsync();
        try
        {
            var dataDirectory = server.Services.GetRequiredService<DndMcpServerOptions>().ResolveDataDirectory();

            Assert.Equal(McpServerHarness.DataDirectory, dataDirectory);
            Assert.StartsWith(AppContext.BaseDirectory, dataDirectory, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void ResolveDataDirectory_NotSet_IsTheEnvironmentsDataDirectory()
    {
        var home = Path.Combine(Path.GetTempPath(), "dnd-mcp-home");
        var options = new DndMcpServerOptions { Paths = () => new DndMcpPaths(_ => null, home) };

        Assert.Equal(Path.Combine(home, ".local", "share", "dnd-mcp"), options.ResolveDataDirectory());
    }

    [Theory]
    // A variable whose directory the options set is not consulted, so its warning would only mislead.
    [InlineData(true, false, new[] { "DND_MCP_DATA_DIR" })]
    [InlineData(false, true, new[] { "DND_MCP_CACHE_DIR" })]
    [InlineData(true, true, new string[0])]
    [InlineData(false, false, new[] { "DND_MCP_CACHE_DIR", "DND_MCP_DATA_DIR" })]
    public void PathWarnings_RelativeOverrides_WarnOnlyForTheDirectoriesTheEnvironmentDecides(bool cacheSet, bool dataSet, string[] warned)
    {
        var options = new DndMcpServerOptions
        {
            CacheDirectory = cacheSet ? Path.GetTempPath() : null,
            DataDirectory = dataSet ? Path.GetTempPath() : null,
            Paths = () => new DndMcpPaths(name => name.StartsWith("DND_MCP_", StringComparison.Ordinal) ? "relative/dir" : null, Path.GetTempPath()),
        };

        Assert.Equal(warned, options.PathWarnings().Select(w => w.Split(' ')[0]).ToArray());
    }

    [Fact]
    public async Task Harness_SharedCache_IsBuiltByThisTestRunNotLeftOverFromAnEarlierOne()
    {
        // The staleness key covers the content and SrdIndexSchema.Version, not the importer's code, so a srd.db left in the
        // test output by an earlier run would hide an importer, counterpart or search-text change from every tool-level test
        // until someone remembered the version bump. The harness clears the shared cache once per test process.
        var server = new McpServerHarness();
        await server.InitializeAsync();
        try
        {
            var index = await server.Services.GetRequiredService<SrdIndexService>().GetIndexAsync(null, CancellationToken.None);
            var processStarted = new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime(), TimeSpan.Zero);

            Assert.True(
                index.Info.BuiltAtUtc >= processStarted.AddSeconds(-1),
                $"The shared srd.db was built at {index.Info.BuiltAtUtc:O}, before this test process started at {processStarted:O}.");
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Services_Harness_SharesOneIndexServiceAcrossCalls()
    {
        // One index per process: tools are created per call, the service they are handed must not be.
        var server = new McpServerHarness();
        await server.InitializeAsync();
        try
        {
            Assert.Same(server.Services.GetRequiredService<SrdIndexService>(), server.Services.GetRequiredService<SrdIndexService>());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    private static DndMcpServerOptions Options(string cacheDirectory, string? contentRoot = null)
    {
        var options = new DndMcpServerOptions { CacheDirectory = cacheDirectory };
        if (contentRoot is not null)
        {
            options.ContentRoot = contentRoot;
        }

        return options;
    }

    private static ILogger<SrdIndexService> Logger(CapturedLog log) =>
        LoggerFactory.Create(builder => builder.AddProvider(new CapturingLoggerProvider(log))).CreateLogger<SrdIndexService>();

    private static bool IsDisposed(SrdIndex index)
    {
        try
        {
            index.Get("2024", "spell", "fireball");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Stands in for <see cref="SrdIndexOpener.OpenOrBuild"/>: blocks until <see cref="Open"/>, then opens the real index,
    /// so a test decides exactly how long "building" lasts.
    /// </summary>
    private sealed class GatedOpener
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>The index the (first) build returned, once it has.</summary>
        public TaskCompletionSource<SrdIndex> Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate.TrySetResult();

        public SrdIndexService CreateService(DndMcpServerOptions options) =>
            new(options, LoggerFactory.Create(_ => { }).CreateLogger<SrdIndexService>(), OpenOrBuild);

        public async Task WaitForCallsAsync(int calls)
        {
            var deadline = Stopwatch.StartNew();
            while (Calls < calls)
            {
                Assert.True(deadline.Elapsed < Patience, $"The build was started {Calls} time(s), expected {calls}.");
                await Task.Delay(10);
            }
        }

        private SrdIndexOpenResult OpenOrBuild(string contentRoot, string databasePath, bool force)
        {
            Interlocked.Increment(ref _calls);
            if (!_gate.Task.Wait(Patience))
            {
                throw new TimeoutException("The test never opened the gate.");
            }

            var result = SrdIndexOpener.OpenOrBuild(contentRoot, databasePath, force);
            Opened.TrySetResult(result.Index);
            return result;
        }
    }

    /// <summary>A synchronous progress sink (Progress&lt;T&gt; posts to a context and would reorder reports).</summary>
    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        private readonly List<ProgressNotificationValue> _reports = [];
        private readonly int _openAfter;
        private readonly GatedOpener? _gate;

        public RecordingProgress(int openAfter, GatedOpener? gate)
        {
            _openAfter = openAfter;
            _gate = gate;
        }

        public IReadOnlyList<ProgressNotificationValue> Reports
        {
            get
            {
                lock (_reports)
                {
                    return _reports.ToList();
                }
            }
        }

        public void Report(ProgressNotificationValue value)
        {
            int count;
            lock (_reports)
            {
                _reports.Add(value);
                count = _reports.Count;
            }

            if (count >= _openAfter)
            {
                _gate?.Open();
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("dnd-mcp-index-service-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// A fact that needs Unix file modes to take effect: skipped on Windows, and for root, whom a read-only directory does not
/// stop (xUnit 2.9 has no Assert.Skip, so the decision is made when the attribute is constructed).
/// </summary>
public sealed class UnixNonRootFactAttribute : FactAttribute
{
    public UnixNonRootFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix file modes only.";
        }
        else if (Environment.UserName == "root")
        {
            Skip = "root can write to a read-only directory.";
        }
    }
}
