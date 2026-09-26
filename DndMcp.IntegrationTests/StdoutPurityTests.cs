using System.Text.Json;
using System.Text.Json.Nodes;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: the built server writes nothing to stdout except JSON-RPC messages — from its first byte to its last,
/// across success, tool errors and protocol errors — and exits with code 0 within seconds of stdin closing.
///
/// <para>
/// stdout IS the protocol. One stray line (a <c>Console.WriteLine</c>, a startup banner, a log provider left on
/// stdout, an exception dumped by the runtime) and Claude Code fails to parse the stream and drops the server. The
/// in-memory tests cannot see this because they never run Program.cs; this test runs the real binary.
/// </para>
/// <para>
/// The session deliberately includes every path that logs — a failing tool, an argument-guard rejection and an
/// unknown tool, each of which writes an exception to the log — because a logger misconfigured onto stdout stays
/// silent on the happy path. Shutdown is included because output written while stopping lands after the last
/// response, where a test that stopped reading early would miss it.
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
    public void IsolateUserData_DeveloperExportedOverrides_EveryDataLocationMovesIntoTheWorkingDirectory()
    {
        // The server process inherits the test runner's environment; a developer who exported DND_MCP_DB for daily
        // use must not have this test's server open their real campaigns.db.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "dnd-mcp-isolation-check");
        var environment = new Dictionary<string, string?>
        {
            ["DND_MCP_DATA_DIR"] = "/home/dm/.local/share/dnd-mcp",
            ["DND_MCP_DB"] = "/home/dm/campaigns.db",
            ["DND_MCP_SOMETHING_ADDED_LATER"] = "/home/dm/real",
            ["XDG_DATA_HOME"] = "/home/dm/.local/share",
            ["XDG_CACHE_HOME"] = "/home/dm/.cache",
            ["PATH"] = "/usr/bin",
        };

        BuiltServerProcess.IsolateUserData(environment, workingDirectory);

        // The roots every default path derives from must be set: unset, the server falls back to ~/.local/share.
        Assert.All(
            new[] { "DND_MCP_DATA_DIR", "XDG_DATA_HOME", "XDG_CACHE_HOME" },
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

        var initializeParams = new JsonObject
        {
            ["protocolVersion"] = McpServerHarness.ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "stdout-purity-test", ["version"] = "1.0.0" },
        };
        await server.SendAsync(Request(InitializeId, "initialize", initializeParams.ToJsonString()));
        var initialize = await server.WaitForResponsesAsync([InitializeId], timeout.Token);

        await server.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await server.SendAsync(Request(ListToolsId, "tools/list", "{}"));
        await server.SendAsync(Request(GoodCallId, "tools/call", """{"name":"dice_roll","arguments":{"expression":"2d6+3","times":2}}"""));
        await server.SendAsync(Request(DomainErrorCallId, "tools/call", """{"name":"dice_roll","arguments":{"expression":"2d6 3"}}"""));
        await server.SendAsync(Request(GuardErrorCallId, "tools/call", """{"name":"dice_roll","arguments":{"times":"three"}}"""));
        await server.SendAsync(Request(UnknownToolId, "tools/call", """{"name":"no_such_tool","arguments":{}}"""));

        var rest = await server.WaitForResponsesAsync([ListToolsId, GoodCallId, DomainErrorCallId, GuardErrorCallId, UnknownToolId], timeout.Token);

        server.CloseInput();
        var exitCode = await server.WaitForExitAsync(ExitTimeout);

        // Only once the process is gone is stdout guaranteed complete; anything printed during shutdown counts.
        var stdout = exitCode.HasValue ? await server.ReadStdoutToEndAsync(ExitTimeout) : server.StdoutLines;

        var responses = initialize.Concat(rest).ToDictionary(p => p.Key, p => p.Value);
        return new Session(stdout, responses, exitCode);
    }

    private static string Request(int id, string method, string paramsJson) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = JsonNode.Parse(paramsJson),
        }.ToJsonString();

    private static bool IsToolError(JsonElement response) =>
        response.TryGetProperty("result", out var result) &&
        result.TryGetProperty("isError", out var isError) &&
        isError.ValueKind == JsonValueKind.True;

    private sealed record Session(IReadOnlyList<string> Stdout, IReadOnlyDictionary<int, JsonElement> Responses, int? ExitCode);
}
