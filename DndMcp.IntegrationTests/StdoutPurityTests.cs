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
/// silent on the happy path. It lists the resources and the prompts at connect, as Claude Code does. It also runs a rules
/// search, which builds the SRD index in the background (SQLite, file moves, the index's own log line) while the session
/// is live, and a campaign's first night: <c>campaign create</c> (campaigns.db created, probed and migrated, a
/// resources/list_changed notification sent), a <c>campaign_write</c> batch, a <c>campaign_search</c> (FTS5 as the
/// party), a <c>campaign_knowledge</c> check; then, with campaigns.db there, what reads it outside the campaign tools: the
/// resource list (which Claude Code sends at every connect, so a stray line there would drop the server for every user
/// with a campaign), a campaign resource, a prompt, and a rules call whose edition the campaign decides; then a session
/// started, a <c>dice_roll</c> logged to it and the session ended (its backup made with <c>VACUUM INTO</c>, and retention
/// run), each of which can log. Those run one after another, each waiting for what it needs, because the server handles
/// requests concurrently and a write sent with the create could reach a campaign that does not exist yet. The startup
/// SQLite probe's verdict goes to stderr too. Shutdown is included because output written while stopping lands after the
/// last response, where a test that stopped reading early would miss it.
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
    private const int CampaignCreateCallId = 11;
    private const int CampaignWriteCallId = 12;
    private const int CampaignSearchCallId = 13;
    private const int CampaignCheckCallId = 14;
    private const int SessionStartCallId = 15;
    private const int LoggedRollCallId = 16;
    private const int SessionEndCallId = 17;
    private const int ListResourcesAtConnectId = 18;
    private const int ListPromptsAtConnectId = 19;
    private const int ListResourcesWithCampaignId = 20;
    private const int ReadSummaryId = 21;
    private const int GetPromptId = 22;
    private const int CampaignEditionCallId = 23;

    // What the startup SQLite probe logs when the native library has everything (SqliteCapabilityCheck).
    private const string ProbeVerdict = ") has every feature dnd-mcp needs: ";

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

        // campaigns.db was created, migrated and written in the isolated data directory, then read back through FTS5 as the
        // party and checked for a character: the native library's campaign features all ran in the real binary.
        Assert.False(IsToolError(responses[CampaignCreateCallId]), $"The campaign create call failed.{server.Diagnostics()}");
        Assert.StartsWith("# Created campaign Purity (`purity`)", ResultText(responses[CampaignCreateCallId]), StringComparison.Ordinal);
        Assert.False(IsToolError(responses[CampaignWriteCallId]), $"The campaign_write call failed.{server.Diagnostics()}");
        Assert.StartsWith("# campaign_write: 2 ops applied (purity)", ResultText(responses[CampaignWriteCallId]), StringComparison.Ordinal);
        Assert.False(IsToolError(responses[CampaignSearchCallId]), $"The campaign_search call failed.{server.Diagnostics()}");
        Assert.Contains("**Iron Guts** · character · `character:iron-guts`", ResultText(responses[CampaignSearchCallId]), StringComparison.Ordinal);
        Assert.False(IsToolError(responses[CampaignCheckCallId]), $"The campaign_knowledge check call failed.{server.Diagnostics()}");
        Assert.StartsWith("# Knowledge check: ", ResultText(responses[CampaignCheckCallId]), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(server.WorkingDirectory, "data", "campaigns.db")), $"campaigns.db is not in the isolated data directory.{server.Diagnostics()}");

        // What Claude Code asks at connect: the resource list (the campaign list handler ran, with no campaigns.db yet) and
        // the prompts. Then, with campaigns.db there, everything that reads it outside the campaign tools ran too.
        var atConnect = ResourceUris(Result(responses[ListResourcesAtConnectId], "resources/list at connect", server));
        Assert.Contains("campaign://list", atConnect);
        Assert.DoesNotContain("campaign://purity/summary", atConnect);
        Assert.Contains(
            Result(responses[ListPromptsAtConnectId], "prompts/list", server).GetProperty("prompts").EnumerateArray(),
            prompt => prompt.GetProperty("name").GetString() == "knowledge_check");
        var withCampaign = ResourceUris(Result(responses[ListResourcesWithCampaignId], "resources/list with a campaign", server));
        Assert.Contains("campaign://purity/summary", withCampaign);
        Assert.Contains("campaign://purity/threads", withCampaign);
        Assert.StartsWith(
            "# Purity (`purity`)",
            Result(responses[ReadSummaryId], "resources/read of the summary", server).GetProperty("contents")[0].GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "1. Call campaign_knowledge {",
            Result(responses[GetPromptId], "prompts/get knowledge_check", server).GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString(),
            StringComparison.Ordinal);
        Assert.False(IsToolError(responses[CampaignEditionCallId]), $"The rules_get call with a campaign active failed.{server.Diagnostics()}");
        Assert.EndsWith("2024 rules: the active campaign's (purity) ruleset.", ResultText(responses[CampaignEditionCallId]), StringComparison.Ordinal);

        // A night at the table: the roll went into the live session's log and the end took the session-end backup.
        Assert.False(IsToolError(responses[SessionStartCallId]), $"The campaign_session start call failed.{server.Diagnostics()}");
        Assert.Contains("session:1 is live.", ResultText(responses[SessionStartCallId]), StringComparison.Ordinal);
        Assert.False(IsToolError(responses[LoggedRollCallId]), $"The logged dice_roll failed.{server.Diagnostics()}");
        Assert.EndsWith("Logged to purity, session 1.", ResultText(responses[LoggedRollCallId]), StringComparison.Ordinal);
        Assert.False(IsToolError(responses[SessionEndCallId]), $"The campaign_session end call failed.{server.Diagnostics()}");
        Assert.Contains("Session-end backup: ", ResultText(responses[SessionEndCallId]), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(server.WorkingDirectory, "data", "backups"), "*-session-end.db"));

        // The startup probe's verdict is a log line: on stderr (and, by the purity check above, nowhere on stdout).
        Assert.True(
            server.StderrLines.Any(line => line.Contains(ProbeVerdict, StringComparison.Ordinal)),
            $"The SQLite probe's verdict is not on stderr.{server.Diagnostics()}");
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
        await server.SendAsync(Request(ListResourcesAtConnectId, "resources/list", "{}"));
        await server.SendAsync(Request(ListPromptsAtConnectId, "prompts/list", "{}"));
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
            [
                ListToolsId, ListResourcesAtConnectId, ListPromptsAtConnectId, GoodCallId, DomainErrorCallId, GuardErrorCallId, UnknownToolId,
                OddsCallId, RulesSearchCallId, SimulateCallId, CombatantCallId,
            ],
            timeout.Token);

        // One at a time (class summary): each campaign call needs what the one before it wrote. A wait only keeps the ids it
        // names, so each starts after every earlier response has been read.
        var campaign = new Dictionary<int, JsonElement>();
        foreach (var (ids, requests) in CampaignCalls())
        {
            foreach (var (id, method, parameters) in requests)
            {
                await server.SendAsync(Request(id, method, parameters));
            }

            foreach (var response in await server.WaitForResponsesAsync(ids, timeout.Token))
            {
                campaign[response.Key] = response.Value;
            }
        }

        server.CloseInput();
        var exitCode = await server.WaitForExitAsync(ExitTimeout);

        // Only once the process is gone is stdout guaranteed complete; anything printed during shutdown counts.
        var stdout = exitCode.HasValue ? await server.ReadStdoutToEndAsync(ExitTimeout) : server.StdoutLines;

        var responses = initialize.Concat(rest).Concat(campaign).ToDictionary(p => p.Key, p => p.Value);
        return new Session(stdout, responses, exitCode);
    }

    // The campaign part of the session, in the order it must run: create, then a write into it, then the reads of what the
    // write made (each step's requests may run together: none needs another's result), then a session started, a roll
    // logged to it and the session ended.
    private static IEnumerable<(int[] Ids, (int Id, string Method, string Params)[] Requests)> CampaignCalls()
    {
        yield return ([CampaignCreateCallId],
        [
            (CampaignCreateCallId, "tools/call",
                """{"name":"campaign","arguments":{"action":"create","name":"Purity","role":"player","ruleset":"2024","my_character":"Aria Vale"}}"""),
        ]);
        yield return ([CampaignWriteCallId],
        [
            (CampaignWriteCallId, "tools/call",
                """{"name":"campaign_write","arguments":{"ops":[{"op":"upsert","kind":"character","name":"Iron Guts","subtype":"npc","visibility":"party"},""" +
                """{"op":"fact","statement":"Iron Guts owes the band a favour.","about":["character:iron-guts"],"known_by":[{"who":"party"}]}]}}"""),
        ]);
        yield return ([CampaignSearchCallId, CampaignCheckCallId],
        [
            (CampaignSearchCallId, "tools/call", """{"name":"campaign_search","arguments":{"query":"iron guts","perspective":"party"}}"""),
            (CampaignCheckCallId, "tools/call",
                """{"name":"campaign_knowledge","arguments":{"action":"check","perspective":"character:aria-vale","text":"Iron Guts owes us a favour.","diegetic":true}}"""),
        ]);
        yield return ([ListResourcesWithCampaignId, ReadSummaryId, GetPromptId, CampaignEditionCallId],
        [
            (ListResourcesWithCampaignId, "resources/list", "{}"),
            (ReadSummaryId, "resources/read", """{"uri":"campaign://purity/summary"}"""),
            (GetPromptId, "prompts/get", """{"name":"knowledge_check","arguments":{"character":"aria-vale"}}"""),
            (CampaignEditionCallId, "tools/call", """{"name":"rules_get","arguments":{"name":"Fireball"}}"""),
        ]);
        yield return ([SessionStartCallId], [(SessionStartCallId, "tools/call", """{"name":"campaign_session","arguments":{"action":"start"}}""")]);
        yield return ([LoggedRollCallId], [(LoggedRollCallId, "tools/call", """{"name":"dice_roll","arguments":{"expression":"1d20+5","label":"Stealth"}}""")]);
        yield return ([SessionEndCallId],
        [
            (SessionEndCallId, "tools/call", """{"name":"campaign_session","arguments":{"action":"end","recap_md":"Iron Guts paid the band back."}}"""),
        ]);
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

    // The result of a request that is not a tool call; a JSON-RPC error instead fails with the error and the server's stderr.
    private static JsonElement Result(JsonElement response, string request, BuiltServerProcess server)
    {
        Assert.True(response.TryGetProperty("result", out var result), $"The {request} request failed: {response}{server.Diagnostics()}");
        return result;
    }

    private static List<string?> ResourceUris(JsonElement result) =>
        result.GetProperty("resources").EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToList();

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
