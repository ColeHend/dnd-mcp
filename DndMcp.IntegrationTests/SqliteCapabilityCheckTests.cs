using DndMcp.Cli;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: as the server starts, the SQLite library is probed once in the data directory (created if needed), what it
/// can do is logged in one line to stderr, and a library missing anything srd.db or campaigns.db needs stops the start:
/// the process exits 1 with one line on stderr naming every gap, never with an unhandled-exception dump (nor the host's
/// stack-trace repeat of the refusal) and never after answering <c>initialize</c>. A probe that cannot run (an unwritable data directory) and a file system without WAL are
/// warnings, not failures: dice and rules must still start.
///
/// <para>
/// Why it fails silently: without the probe, a SQLite built without FTS5 or STRICT shows up as a migration dying half-way
/// or a rules search failing mid-session; with a probe that throws on its own scratch directory, a sandbox with a read-only
/// data directory would lose every tool over litter; and a failed start reported as an exception dump reads to Claude Code
/// as "failed to connect", with nothing a user can act on. The in-memory harness runs no hosted services, so these tests
/// call the check directly, run a host through <see cref="DndMcpCli.RunServerAsync"/>, and start the built binary.
/// </para>
/// </summary>
public sealed class SqliteCapabilityCheckTests : IDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    // A host whose start wrongly succeeds runs until stopped: the test fails on the timeout instead of hanging the run.
    private static readonly TimeSpan HostTimeout = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("dnd-mcp-sqlite-check-").FullName;
    private readonly CapturedLog _log = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DataDirectory => Path.Combine(_root, "data");

    /// <summary>A capabilities record with every feature, as the bundled e_sqlite3 has.</summary>
    private static SqliteCapabilities Everything() =>
        new("3.46.1", "e_sqlite3", MeetsMinimumVersion: true, Fts5: true, Fts5PorterUnicode61Tokenizer: true, Fts5PrefixIndexes: true,
            Fts5Bm25: true, JsonFunctions: true, StrictTables: true, WithoutRowidTables: true, PartialIndexes: true,
            JsonExpressionIndexes: true, TriggerRaiseAbort: true, WalJournalMode: true, VacuumInto: true);

    private SqliteCapabilityCheck NewCheck(Func<string, SqliteCapabilities>? probe = null, string? dataDirectory = null)
    {
        var logger = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(_log))).CreateLogger<SqliteCapabilityCheck>();
        var options = new DndMcpServerOptions { DataDirectory = dataDirectory ?? DataDirectory };
        return probe is null ? new SqliteCapabilityCheck(options, logger) : new SqliteCapabilityCheck(options, logger, probe);
    }

    [Fact]
    public void Check_TheBundledLibrary_LogsOneInformationLineInTheCreatedDataDirectoryAndLeavesNoScratch()
    {
        var capabilities = NewCheck().Check();

        Assert.NotNull(capabilities);
        Assert.Empty(capabilities.MissingFeatures());
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.StartsWith($"SQLite {capabilities.Version} ({capabilities.NativeLibrary}) has every feature dnd-mcp needs: FTS5", entry.Message, StringComparison.Ordinal);
        Assert.EndsWith("; WAL works.", entry.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(DataDirectory), "The probe did not create the data directory.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(DataDirectory));
    }

    [Fact]
    public void Check_ProbesInTheDataDirectoryNotTemp()
    {
        string? probedIn = null;

        NewCheck(directory =>
        {
            probedIn = directory;
            return Everything();
        }).Check();

        Assert.Equal(Path.GetFullPath(DataDirectory), probedIn);
    }

    [Fact]
    public async Task StartAsync_LibraryWithGaps_LogsAnErrorAndThrowsNamingEveryGap()
    {
        var check = NewCheck(_ => Everything() with { Fts5 = false, StrictTables = false, Version = "3.31.1", MeetsMinimumVersion = false });

        var ex = await Assert.ThrowsAsync<StartupRefusedException>(() => check.StartAsync(CancellationToken.None));

        Assert.Contains("SQLite 3.37.0 or newer (found 3.31.1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("FTS5 full-text search", ex.Message, StringComparison.Ordinal);
        Assert.Contains("STRICT tables", ex.Message, StringComparison.Ordinal);
        var error = Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(
            "SQLite 3.31.1 (e_sqlite3) is missing what dnd-mcp needs: SQLite 3.37.0 or newer (found 3.31.1); FTS5 full-text search " +
            "(rules_search, campaign_search); STRICT tables. The server will not start.",
            error.Message);
    }

    [Theory]
    [InlineData("VacuumInto", "VACUUM INTO (backups)")]
    [InlineData("TriggerRaiseAbort", "triggers with RAISE(ABORT) (append-only change_log)")]
    [InlineData("JsonFunctions", "JSON functions json_patch, json_extract and json_valid")]
    [InlineData("Fts5Bm25", "FTS5 bm25() ranking with per-column weights")]
    public void Check_AnyOneRequiredFeatureMissing_StopsTheStart(string feature, string named)
    {
        var missing = Everything() with
        {
            VacuumInto = feature != "VacuumInto",
            TriggerRaiseAbort = feature != "TriggerRaiseAbort",
            JsonFunctions = feature != "JsonFunctions",
            Fts5Bm25 = feature != "Fts5Bm25",
        };

        var ex = Assert.Throws<StartupRefusedException>(() => NewCheck(_ => missing).Check());

        Assert.Contains(named, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_NoWal_WarnsAndStarts()
    {
        // WAL depends on the file system under the data directory (a network share cannot map its shared memory); the
        // migrator checks it on campaigns.db itself and carries on in rollback-journal mode, so it never stops the start.
        var capabilities = NewCheck(_ => Everything() with { WalJournalMode = false }).Check();

        Assert.NotNull(capabilities);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Information && e.Message.EndsWith("; WAL is unavailable.", StringComparison.Ordinal));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith($"SQLite cannot use WAL in {DataDirectory}", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_DataDirectoryCannotBeCreated_WarnsAndDoesNotProbeOrThrow()
    {
        var file = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(file, "x");
        var probed = false;

        var capabilities = NewCheck(_ =>
        {
            probed = true;
            return Everything();
        }, dataDirectory: Path.Combine(file, "data")).Check();

        Assert.Null(capabilities);
        Assert.False(probed);
        var warning = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Could not check the SQLite library at startup: the data directory cannot be created", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills J03 (FH10, M04): with no home directory and no DND_MCP_DATA_DIR the data directory cannot be decided (an
    /// InvalidOperationException from the paths), which is the other half of "cannot be created or decided": the probe warns
    /// and dice and rules start; it does not crash the start in exactly the environment where they are meant to keep working.
    /// </summary>
    [Fact]
    public void Check_NoHomeAndNoDataDirectory_WarnsAndDoesNotProbeOrThrow()
    {
        var logger = LoggerFactory.Create(b => b.AddProvider(new CapturingLoggerProvider(_log))).CreateLogger<SqliteCapabilityCheck>();
        var options = new DndMcpServerOptions { Paths = () => new Repository.DndMcpPaths(_ => null, homeDirectory: null) };
        var probed = false;

        var capabilities = new SqliteCapabilityCheck(options, logger, _ =>
        {
            probed = true;
            return Everything();
        }).Check();

        Assert.Null(capabilities);
        Assert.False(probed);
        var warning = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Could not check the SQLite library at startup: the data directory cannot be ", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("sqlite")]
    public void Check_ProbeCannotWriteItsScratch_WarnsAndDoesNotThrow(string kind)
    {
        // A SqliteException can only escape the probe from opening its scratch database (SQLITE_CANTOPEN, 14): the
        // directory, not the library, so a warning like the other I/O failures.
        var capabilities = NewCheck(_ => kind switch
        {
            "io" => throw new IOException("disk full"),
            "access" => throw new UnauthorizedAccessException("read-only"),
            _ => throw new SqliteException("unable to open database file", 14),
        }).Check();

        Assert.Null(capabilities);
        var warning = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith($"Could not check the SQLite library at startup: the probe could not write in {DataDirectory}.", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ProbeBug_IsNotSwallowed()
    {
        // Only I/O on the scratch directory is forgiven: a bug in the probe must not pass for "could not check".
        Assert.Throws<ArgumentException>(() => NewCheck(_ => throw new ArgumentException("bug")).Check());
    }

    [Fact]
    public async Task RunServerAsync_StartFailsOnTheLibrary_Exits1WithOneLineOnStderr()
    {
        var error = new StringWriter();
        var host = NewHost(services => services.AddHostedService(sp => new SqliteCapabilityCheck(
            new DndMcpServerOptions { DataDirectory = DataDirectory }, sp.GetRequiredService<ILogger<SqliteCapabilityCheck>>(),
            _ => Everything() with { Fts5 = false })));

        var exitCode = await DndMcpCli.RunServerAsync(host, error).WaitAsync(HostTimeout);

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        var text = error.ToString().ReplaceLineEndings("\n");
        Assert.StartsWith("dnd-mcp could not start: The SQLite library loaded by dnd-mcp (e_sqlite3 3.46.1) is missing: FTS5 full-text search", text, StringComparison.Ordinal);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Category == typeof(SqliteCapabilityCheck).FullName);
    }

    [Fact]
    public async Task RunServerAsync_RefusedStart_TheHostDoesNotRepeatItWithAStackTrace()
    {
        // The check logs its error line and RunServerAsync prints the summary; the host's "Hosting failed to start" with the
        // same exception and its stack trace between them is dropped (Program.cs wires the same filter).
        var host = NewHost(services => services.AddHostedService(sp => new SqliteCapabilityCheck(
            new DndMcpServerOptions { DataDirectory = DataDirectory }, sp.GetRequiredService<ILogger<SqliteCapabilityCheck>>(),
            _ => Everything() with { Fts5 = false })));

        var exitCode = await DndMcpCli.RunServerAsync(host, new StringWriter()).WaitAsync(HostTimeout);

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.Single(_log.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(_log.Entries, e => e.Category == ReportedStartFailureLogFilter.HostCategory && e.Exception is not null);
    }

    [Fact]
    public async Task RunServerAsync_OtherStartFailure_KeepsTheHostsEntryWithTheException()
    {
        // Only a refusal already reported is dropped: any other start failure (a bug in a hosted service) keeps the host's
        // entry, whose stack trace is the only clue to it. This also shows the host does log the entry the filter drops.
        var bug = new InvalidOperationException("a bug in the index warm-up");
        var host = NewHost(services => services.AddHostedService(_ => new ThrowingService(bug)));

        var exitCode = await DndMcpCli.RunServerAsync(host, new StringWriter()).WaitAsync(HostTimeout);

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        var entry = Assert.Single(_log.Entries, e => e.Category == ReportedStartFailureLogFilter.HostCategory && e.Exception is not null);
        Assert.Same(bug, entry.Exception);
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    public static TheoryData<string, int, Exception?, bool> FilterCases => new()
    {
        // category, event id, exception, kept
        { ReportedStartFailureLogFilter.HostCategory, 11, Refused(), false },
        { ReportedStartFailureLogFilter.HostCategory, 11, new AggregateException(Refused(), Refused()), false },
        { ReportedStartFailureLogFilter.HostCategory, 11, new AggregateException(Refused(), new IOException("disk")), true },
        { ReportedStartFailureLogFilter.HostCategory, 11, new AggregateException(), true },
        { ReportedStartFailureLogFilter.HostCategory, 11, new InvalidOperationException("bug"), true },
        { ReportedStartFailureLogFilter.HostCategory, 11, null, true },
        { ReportedStartFailureLogFilter.HostCategory, 9, Refused(), true },
        { ReportedStartFailureLogFilter.HostCategory, 1, null, true },
        { typeof(SqliteCapabilityCheck).FullName!, 11, Refused(), true },
    };

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void Filter_DropsOnlyTheHostsStartFaultForAReportedRefusal(string category, int eventId, Exception? exception, bool kept)
    {
        using var filter = new ReportedStartFailureLogFilter(new CapturingLoggerProvider(_log), ownsInner: false);

        filter.CreateLogger(category).Log(LogLevel.Error, new EventId(eventId), "entry", exception, (state, _) => state);

        Assert.Equal(kept ? 1 : 0, _log.Entries.Count);
    }

    [Fact]
    public void AddServerLogging_OnlyProviderIsTheFilteredConsoleWritingEveryLevelToStderr()
    {
        // Program.cs's logging: stdout is the JSON-RPC stream, so every level goes to stderr, and the console provider is
        // the one wrapped (a provider added beside it, or left unwrapped, would print the host's repeat again).
        var services = new ServiceCollection();
        services.AddLogging(DndMcpCli.AddServerLogging);
        using var provider = services.BuildServiceProvider();

        var logger = Assert.Single(provider.GetServices<ILoggerProvider>());

        var filter = Assert.IsType<ReportedStartFailureLogFilter>(logger);
        Assert.IsType<ConsoleLoggerProvider>(filter.Inner);
        Assert.Equal(LogLevel.Trace, provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.LogToStandardErrorThreshold);
    }

    [Fact]
    public async Task RunServerAsync_StartThrowsAMultiLineMessage_PrintsItOnOneLine()
    {
        var error = new StringWriter();
        var host = NewHost(services => services.AddHostedService(_ => new ThrowingService(new InvalidOperationException("first\r\n  second\nthird"))));

        var exitCode = await DndMcpCli.RunServerAsync(host, error).WaitAsync(HostTimeout);

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.Equal("dnd-mcp could not start: first second third\n", error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task RunServerAsync_LaterServicesAreNotStartedAfterAFailure()
    {
        // The check is registered first so nothing after it (the index warm-up, the stdio transport) starts.
        var later = new RecordingService();
        var host = NewHost(services =>
        {
            services.AddHostedService(_ => new ThrowingService(new InvalidOperationException("no FTS5")));
            services.AddHostedService(_ => later);
        });

        await DndMcpCli.RunServerAsync(host, new StringWriter()).WaitAsync(HostTimeout);

        Assert.False(later.Started);
    }

    [Fact]
    public async Task RunServerAsync_StartSucceedsAndTheHostStops_Exits0WithNothingOnStderr()
    {
        var error = new StringWriter();
        var host = NewHost(services => services.AddHostedService(sp => new StopWhenStarted(sp.GetRequiredService<IHostApplicationLifetime>())));

        var exitCode = await DndMcpCli.RunServerAsync(host, error).WaitAsync(HostTimeout);

        Assert.Equal(DndMcpCli.ExitOk, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Registration_TheCheckIsTheFirstHostedService()
    {
        // Hosted services start in registration order: before the index warm-up and the transport, so a bad library stops
        // the process before initialize is answered.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDndMcpServer();

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();

        Assert.Equal(typeof(SqliteCapabilityCheck), hosted[0].ImplementationType);
        Assert.Contains(hosted, d => d.ImplementationType == typeof(SrdIndexWarmup));
    }

    [Fact]
    public async Task BuiltServer_Start_LogsTheProbeToStderrAndAnswersInitialize()
    {
        await using var server = BuiltServerProcess.Start();
        using var timeout = new CancellationTokenSource(ResponseTimeout);
        await server.SendAsync(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"probe-test","version":"1"}}}""");
        var responses = await server.WaitForResponsesAsync([1], timeout.Token);
        server.CloseInput();
        var exitCode = await server.WaitForExitAsync(ExitTimeout);

        Assert.Equal("dnd", responses[1].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(exitCode == 0, $"The server exited with code {exitCode?.ToString() ?? "(still running)"}.{server.Diagnostics()}");
        Assert.Contains(server.StderrLines, line => line.Contains(") has every feature dnd-mcp needs: ", StringComparison.Ordinal));
        Assert.All(server.StdoutLines, line => Assert.True(BuiltServerProcess.IsJsonRpcObject(line), line));
        var data = Path.Combine(server.WorkingDirectory, "data");
        Assert.True(Directory.Exists(data), $"The probe did not create the data directory.{server.Diagnostics()}");
        Assert.Empty(Directory.EnumerateFileSystemEntries(data));
    }

    /// <summary>A host logging to <see cref="_log"/> through the same filter Program.cs puts around the console.</summary>
    private IHost NewHost(Action<IServiceCollection> configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(new CapturingLoggerProvider(_log));
        builder.Logging.DropReportedStartFailures();
        configure(builder.Services);
        return builder.Build();
    }

    private static StartupRefusedException Refused() => new("no FTS5", new InvalidOperationException("no FTS5"));

    private sealed class ThrowingService(Exception exception) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw exception;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StopWhenStarted(IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.ApplicationStarted.Register(lifetime.StopApplication);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
