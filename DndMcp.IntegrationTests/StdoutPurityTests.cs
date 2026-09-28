using System.Text.Json;
using System.Text.Json.Nodes;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: the built server writes nothing to stdout except JSON-RPC messages — from its first byte to its last,
/// across success, tool errors and protocol errors — and exits with code 0 within seconds of stdin closing; closed during
/// its first index build, it finishes the build rather than leaving temporary files in the cache; and whatever project
/// directory it is launched in, it neither reads that project's configuration nor watches its files.
///
/// <para>
/// stdout IS the protocol. One stray line (a <c>Console.WriteLine</c>, a startup banner, a log provider left on
/// stdout, an exception dumped by the runtime) and Claude Code fails to parse the stream and drops the server. The
/// in-memory tests cannot see this because they never run Program.cs; this test runs the real binary.
/// </para>
/// <para>
/// The session deliberately includes every path that logs — a failing tool, an argument-guard rejection and an
/// unknown tool, each of which writes an exception to the log — because a logger misconfigured onto stdout stays
/// silent on the happy path. It also runs a rules search, which builds the SRD index in the background (SQLite, file
/// moves, the index's own log line) while the session is live. Shutdown is included because output written while
/// stopping lands after the last response, where a test that stopped reading early would miss it.
/// </para>
/// <para>
/// By default this runs <c>dotnet DndMcp.dll</c> from the build. Set <see cref="BuiltHost.HostExecutableVariable"/>
/// to the published binary to check the executable Claude Code launches (the Phase 0 exit check).
/// </para>
/// </summary>
public sealed class StdoutPurityTests
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    private const int InitializeId = 1;
    private const int ListToolsId = 2;
    private const int GoodCallId = 3;
    private const int DomainErrorCallId = 4;
    private const int GuardErrorCallId = 5;
    private const int UnknownToolId = 6;
    private const int OddsCallId = 7;
    private const int RulesSearchCallId = 8;
    private const int SimulateCallId = 9;
    private const int CombatantCallId = 10;

    [Fact]
    public async Task BuiltServer_FullSession_WritesOnlyJsonRpcToStdout()
    {
        await using var server = BuiltServerProcess.Start();
        var session = await RunSessionAsync(server);

        Assert.NotEmpty(session.Stdout);
        var impure = session.Stdout.Where(line => !BuiltServerProcess.IsJsonRpcObject(line)).ToList();
        Assert.True(
            impure.Count == 0,
            $"stdout carried {impure.Count} line(s) that are not JSON-RPC 2.0 objects:{Environment.NewLine}" +
            string.Join(Environment.NewLine, impure.Select(l => "  >> " + l)) +
            server.Diagnostics());

        // The purity check is only meaningful if the session really exercised each path.
        var responses = session.Responses;
        Assert.Equal("dnd", responses[InitializeId].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Contains(
            responses[ListToolsId].GetProperty("result").GetProperty("tools").EnumerateArray(),
            tool => tool.GetProperty("name").GetString() == "dice_roll");
        Assert.False(IsToolError(responses[GoodCallId]), $"The good call failed.{server.Diagnostics()}");
        Assert.True(IsToolError(responses[DomainErrorCallId]));
        Assert.True(IsToolError(responses[GuardErrorCallId]));
        Assert.True(responses[UnknownToolId].TryGetProperty("error", out _), "An unknown tool should be a JSON-RPC error.");
        Assert.False(IsToolError(responses[OddsCallId]), $"The dice_odds call (Monte Carlo path) failed.{server.Diagnostics()}");
        Assert.False(IsToolError(responses[RulesSearchCallId]), $"The rules_search call failed.{server.Diagnostics()}");
        Assert.Contains("`2024/spell/fireball`", ResultText(responses[RulesSearchCallId]), StringComparison.Ordinal);

        // The simulator runs on worker threads and the normalizer reads the overrides: neither may print.
        Assert.False(IsToolError(responses[SimulateCallId]), $"The balance_simulate call failed.{server.Diagnostics()}");
        Assert.StartsWith("# Fight simulation: ", ResultText(responses[SimulateCallId]), StringComparison.Ordinal);
        Assert.False(IsToolError(responses[CombatantCallId]), $"The rules_get combatant call failed.{server.Diagnostics()}");
    }

    [Fact]
    public async Task BuiltServer_FirstRulesCall_BuildsTheIndexInTheIsolatedCacheAndLogsItToStderr()
    {
        // The index build's log line must reach stderr (and, per the test above, nothing of it stdout), and the build must
        // land in the isolated cache, not the developer's ~/.cache/dnd-mcp.
        await using var server = BuiltServerProcess.Start();
        await RunSessionAsync(server);

        var cache = Path.Combine(server.WorkingDirectory, "cache", "srd.db");
        Assert.Contains(server.StderrLines, line => line.Contains($"SRD index rebuilt (no index at {cache})", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuiltServer_StdinClosedAfterSession_ExitsWithCodeZero()
    {
        await using var server = BuiltServerProcess.Start();
        var session = await RunSessionAsync(server);

        Assert.True(session.ExitCode.HasValue, $"The server was still running {ExitTimeout.TotalSeconds}s after stdin closed.{server.Diagnostics()}");
        Assert.True(session.ExitCode == 0, $"The server exited with code {session.ExitCode}.{server.Diagnostics()}");
    }

    [Fact]
    public async Task BuiltServer_StdinClosedDuringTheFirstIndexBuild_FinishesTheBuildAndLeavesNoTempFiles()
    {
        // The warm-up starts building srd.db as the server starts. A session that ends at once (a health check, `claude mcp
        // list` right after an install) used to exit mid-build, and the runtime killed the build thread before it could
        // clean up: srd.db.<pid>.<guid>.tmp and its journal stayed in the user's cache after every such session.
        await using var server = BuiltServerProcess.Start();
        using var timeout = new CancellationTokenSource(ResponseTimeout);
        await server.SendAsync(Request(InitializeId, "initialize", InitializeParams()));
        await server.WaitForResponsesAsync([InitializeId], timeout.Token);

        server.CloseInput();
        var exitCode = await server.WaitForExitAsync(ExitTimeout);

        Assert.True(exitCode == 0, $"The server exited with code {exitCode?.ToString() ?? "(still running)"}.{server.Diagnostics()}");
        var cache = Path.Combine(server.WorkingDirectory, "cache");
        // Filtered by hand: the pattern "srd.db.*" also matches srd.db itself (the old DOS rule for ".*").
        var leftovers = Directory.Exists(cache)
            ? Directory.GetFiles(cache).Where(f => Path.GetFileName(f).StartsWith("srd.db.", StringComparison.Ordinal)).ToArray()
            : [];
        Assert.True(leftovers.Length == 0, $"Left in the cache: {string.Join(", ", leftovers.Select(Path.GetFileName))}.{server.Diagnostics()}");
        Assert.True(File.Exists(Path.Combine(cache, "srd.db")), $"The build was abandoned rather than finished.{server.Diagnostics()}");
    }

    [Fact]
    public async Task BuiltServer_LaunchedInAProjectWithAMalformedAppsettings_StillAnswersInitialize()
    {
        // Claude Code starts the server in the user's project. A host whose content root is the current directory loads that
        // project's appsettings.json, and a malformed one (or one meant for the user's own app) aborted startup.
        await using var server = BuiltServerProcess.Start(directory => File.WriteAllText(Path.Combine(directory, "appsettings.json"), "{ \"Logging\": { "));
        using var timeout = new CancellationTokenSource(ResponseTimeout);

        await server.SendAsync(Request(InitializeId, "initialize", InitializeParams()));
        var responses = await server.WaitForResponsesAsync([InitializeId], timeout.Token);

        Assert.Equal("dnd", responses[InitializeId].GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
    }

    [LinuxOnlyFact]
    public async Task BuiltServer_LaunchedInALargeProject_WatchesNoneOfItsDirectories()
    {
        // A configuration file watcher rooted at the launch directory holds one inotify watch per subdirectory: in a big
        // repository, times every open session, that exhausts fs.inotify.max_user_watches and breaks file watching in the
        // user's other tools.
        const int Subdirectories = 200;
        await using var server = BuiltServerProcess.Start(directory =>
        {
            for (var i = 0; i < Subdirectories; i++)
            {
                Directory.CreateDirectory(Path.Combine(directory, "src", $"module{i}"));
            }
        });
        using var timeout = new CancellationTokenSource(ResponseTimeout);
        await server.SendAsync(Request(InitializeId, "initialize", InitializeParams()));
        await server.WaitForResponsesAsync([InitializeId], timeout.Token);

        var watches = InotifyWatchCount(server.ProcessId);

        Assert.True(watches < Subdirectories / 2, $"The server holds {watches} inotify watches.{server.Diagnostics()}");
    }

    [Fact]
    public void IsolateUserData_DeveloperExportedOverrides_EveryDataLocationMovesIntoTheWorkingDirectory()
    {
        // The server process inherits the test runner's environment; a developer who exported DND_MCP_DB for daily
        // use must not have this test's server open their real campaigns.db.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "dnd-mcp-isolation-check");
        var environment = new Dictionary<string, string?>
        {
            ["DND_MCP_DATA_DIR"] = "/home/dm/.local/share/dnd-mcp",
            ["DND_MCP_CACHE_DIR"] = "/home/dm/.cache/dnd-mcp",
            ["DND_MCP_DB"] = "/home/dm/campaigns.db",
            ["DND_MCP_SOMETHING_ADDED_LATER"] = "/home/dm/real",
            ["XDG_DATA_HOME"] = "/home/dm/.local/share",
            ["XDG_CACHE_HOME"] = "/home/dm/.cache",
            ["PATH"] = "/usr/bin",
        };

        BuiltServerProcess.IsolateUserData(environment, workingDirectory);

        // The roots every default path derives from must be set: unset, the server falls back to ~/.local/share and
        // ~/.cache, and would rebuild the developer's own srd.db.
        Assert.All(
            new[] { "DND_MCP_DATA_DIR", "DND_MCP_CACHE_DIR", "XDG_DATA_HOME", "XDG_CACHE_HOME" },
            key => Assert.True(environment.ContainsKey(key), $"{key} is not set for the child."));

        // And no inherited location survives, including overrides this helper has never heard of.
        Assert.All(
            environment.Where(e => e.Key.StartsWith("DND_MCP_", StringComparison.Ordinal) || e.Key.StartsWith("XDG_", StringComparison.Ordinal)),
            e => Assert.StartsWith(workingDirectory + Path.DirectorySeparatorChar, e.Value, StringComparison.Ordinal));
        Assert.Equal("/usr/bin", environment["PATH"]);
    }

    private static async Task<Session> RunSessionAsync(BuiltServerProcess server)
    {
        using var timeout = new CancellationTokenSource(ResponseTimeout);

        await server.SendAsync(Request(InitializeId, "initialize", InitializeParams()));
        var initialize = await server.WaitForResponsesAsync([InitializeId], timeout.Token);

        await server.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await server.SendAsync(Request(ListToolsId, "tools/list", "{}"));
        await server.SendAsync(Request(GoodCallId, "tools/call", """{"name":"dice_roll","arguments":{"expression":"2d6+3","times":2}}"""));
        await server.SendAsync(Request(DomainErrorCallId, "tools/call", """{"name":"dice_roll","arguments":{"expression":"2d6 3"}}"""));
        await server.SendAsync(Request(GuardErrorCallId, "tools/call", """{"name":"dice_roll","arguments":{"times":"three"}}"""));
        await server.SendAsync(Request(UnknownToolId, "tools/call", """{"name":"no_such_tool","arguments":{}}"""));
        await server.SendAsync(Request(OddsCallId, "tools/call", """{"name":"dice_odds","arguments":{"expression":"4d6!kh3>=15"}}"""));
        await server.SendAsync(Request(RulesSearchCallId, "tools/call", """{"name":"rules_search","arguments":{"query":"fireball"}}"""));
        await server.SendAsync(Request(SimulateCallId, "tools/call",
            """{"name":"balance_simulate","arguments":{"party":[{"monster":"Knight","count":2}],"enemies":[{"monster":"Lich"}],"iterations":2048,"seed":1,"replay":1}}"""));
        await server.SendAsync(Request(CombatantCallId, "tools/call", """{"name":"rules_get","arguments":{"name":"Lich","format":"combatant","edition":"both"}}"""));

        var rest = await server.WaitForResponsesAsync(
            [ListToolsId, GoodCallId, DomainErrorCallId, GuardErrorCallId, UnknownToolId, OddsCallId, RulesSearchCallId, SimulateCallId, CombatantCallId],
            timeout.Token);

        server.CloseInput();
        var exitCode = await server.WaitForExitAsync(ExitTimeout);

        // Only once the process is gone is stdout guaranteed complete; anything printed during shutdown counts.
        var stdout = exitCode.HasValue ? await server.ReadStdoutToEndAsync(ExitTimeout) : server.StdoutLines;

        var responses = initialize.Concat(rest).ToDictionary(p => p.Key, p => p.Value);
        return new Session(stdout, responses, exitCode);
    }

    private static string InitializeParams() =>
        new JsonObject
        {
            ["protocolVersion"] = McpServerHarness.ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "stdout-purity-test", ["version"] = "1.0.0" },
        }.ToJsonString();

    // Every "inotify wd:" line in /proc/<pid>/fdinfo is one watched directory.
    private static int InotifyWatchCount(int processId)
    {
        var count = 0;
        foreach (var fdinfo in Directory.GetFiles($"/proc/{processId}/fdinfo"))
        {
            try
            {
                count += File.ReadLines(fdinfo).Count(line => line.StartsWith("inotify wd:", StringComparison.Ordinal));
            }
            catch (IOException)
            {
                // The descriptor closed between the listing and the read.
            }
        }

        return count;
    }

    private static string Request(int id, string method, string paramsJson) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonNode.Parse(paramsJson),
        }.ToJsonString();

    private static string ResultText(JsonElement response) =>
        response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;

    private static bool IsToolError(JsonElement response) =>
        response.TryGetProperty("result", out var result) &&
        result.TryGetProperty("isError", out var isError) &&
        isError.ValueKind == JsonValueKind.True;

    private sealed record Session(IReadOnlyList<string> Stdout, IReadOnlyDictionary<int, JsonElement> Responses, int? ExitCode);
}

/// <summary>A fact that reads Linux's /proc: skipped elsewhere (xUnit 2.9 has no Assert.Skip).</summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Reads /proc, which only Linux has.";
        }
    }
}
