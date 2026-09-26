using System.IO.Pipelines;
using DndMcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>
/// A real <see cref="McpServer"/> built from <c>AddDndMcpServer()</c> — the same registrations Program.cs
/// runs — talking to a real <see cref="McpClient"/> over two in-memory pipes.
///
/// <para>
/// Why a real client rather than calling tool methods directly: what the model sees is decided by layers a
/// direct call skips — schema generation from the shared JSON options, argument binding, the call-tool filter
/// that validates arguments and translates <c>DndInputException</c>, and the SDK's own error wrapping. A test
/// that calls <c>DiceTools.Roll</c> directly stays green while every one of those is broken.
/// </para>
/// <para>
/// The client pins protocol <see cref="ProtocolVersion"/> because that is the <c>initialize</c> handshake
/// Claude Code uses for stdio servers. Leaving it unset would make the client probe the newer
/// <c>server/discover</c> path, and the tests would exercise a handshake the real client never uses.
/// </para>
/// <para>
/// Use it as <c>IClassFixture&lt;McpServerHarness&gt;</c> for stateless tools (one server per test class), or
/// create and <see cref="InitializeAsync"/> one per test when a tool keeps state between calls.
/// <see cref="WithExtraTools"/> adds test-only tools on top of the real registrations, for host behaviour no
/// production tool can trigger (an unexpected exception, a renamed parameter). The parameterless fixture never
/// has them, so ServerSurfaceTests still sees exactly the production tool list.
/// </para>
/// <para>
/// The server runs inside the test process, with the developer's real environment and home directory. Nothing
/// registered today reads a path, but the SRD index (Phase 2) and the campaign database (Phase 6) will: before
/// those services are registered, <c>AddDndMcpServer</c> needs a path override this harness sets to a temp
/// directory, or in-memory tests will open the user's real campaigns.db. <see cref="BuiltServerProcess"/>
/// already isolates the child process's paths.
/// </para>
/// </summary>
public sealed class McpServerHarness : IAsyncLifetime
{
    /// <summary>The handshake Claude Code negotiates with stdio servers.</summary>
    public const string ProtocolVersion = "2025-11-25";

    // Generous: the first handshake in a test run pays JIT and schema-generation costs, and a CI box can be slow.
    // A hung handshake must still fail the test rather than hang the whole run.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Action<IMcpServerBuilder>? _configureServer;
    private ServiceProvider? _provider;
    private Task _serverTask = Task.CompletedTask;
    private McpClient? _client;

    /// <summary>The production server exactly as Program.cs registers it, minus the stdio transport.</summary>
    public McpServerHarness()
    {
    }

    // Private: xUnit requires a class fixture to have exactly one public constructor.
    private McpServerHarness(Action<IMcpServerBuilder> configureServer)
    {
        _configureServer = configureServer;
    }

    /// <summary>
    /// The production registrations plus whatever <paramref name="configureServer"/> adds, typically
    /// <c>WithTools&lt;T&gt;(McpJson.Options)</c> for a test-only tool class.
    /// </summary>
    public static McpServerHarness WithExtraTools(Action<IMcpServerBuilder> configureServer) => new(configureServer);

    /// <summary>
    /// Everything the server logged. When a call unexpectedly comes back as the SDK's generic error, the real
    /// exception is only here, so assertions include it via <see cref="CapturedLog.Describe"/>.
    /// </summary>
    public CapturedLog ServerLog { get; } = new();

    public McpClient Client =>
        _client ?? throw new InvalidOperationException("The harness has not been initialized; use it as an xUnit fixture or await InitializeAsync first.");

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddProvider(new CapturingLoggerProvider(ServerLog));
        });

        var server = services
            .AddDndMcpServer()
            .WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream());
        _configureServer?.Invoke(server);

        // Scope validation on: stdio creates a DI scope per request, so a scoped service captured by a singleton
        // would work by accident in one request and leak state across the next. Fail here instead.
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _serverTask = _provider.GetRequiredService<McpServer>().RunAsync(_serverCts.Token);

        using var timeout = new CancellationTokenSource(StartupTimeout);
        _client = await McpClient.CreateAsync(
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream()),
            new McpClientOptions { ProtocolVersion = ProtocolVersion },
            loggerFactory: null,
            timeout.Token);
    }

    /// <summary>
    /// Shuts down the way stdio does in production: the client goes away and the server sees end-of-input. Only
    /// if that fails to stop it within <see cref="ShutdownTimeout"/> is the server cancelled — and then this
    /// throws, because a server that ignores EOF is a server Claude Code has to kill.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        await _clientToServer.Writer.CompleteAsync();

        var stoppedOnEof = await Task.WhenAny(_serverTask, Task.Delay(ShutdownTimeout)) == _serverTask;
        if (!stoppedOnEof)
        {
            await _serverCts.CancelAsync();
        }

        try
        {
            await _serverTask.WaitAsync(ShutdownTimeout);
        }
        catch (OperationCanceledException) when (_serverCts.IsCancellationRequested)
        {
            // Expected only on the forced path, which is reported below.
        }
        finally
        {
            await _serverToClient.Writer.CompleteAsync();

            if (_provider is not null)
            {
                await _provider.DisposeAsync();
            }

            _serverCts.Dispose();
        }

        if (!stoppedOnEof)
        {
            throw new TimeoutException($"The MCP server did not stop within {ShutdownTimeout.TotalSeconds}s of its input closing.");
        }
    }

    /// <summary>Calls a tool through the real client with arguments given as a JSON object literal.</summary>
    /// <remarks>
    /// Raw JSON rather than a dictionary of CLR values so a test can send exactly the wire shapes a model
    /// produces — numbers as strings, nulls, arrays where a scalar belongs — without the client's serializer
    /// normalising them first.
    /// </remarks>
    public async Task<CallToolResult> CallToolJsonAsync(string toolName, string argumentsJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(argumentsJson);
        var arguments = document.RootElement
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

        return await Client.CallToolAsync(toolName, arguments);
    }

    /// <summary>
    /// The one text block a text-returning tool must produce. Fails (with the server log attached) if the result
    /// has any other shape, so a test never passes by reading the wrong block.
    /// </summary>
    public string SingleText(CallToolResult result)
    {
        var block = Assert.Single(result.Content);
        var text = Assert.IsType<TextContentBlock>(block).Text;
        Assert.False(string.IsNullOrEmpty(text), $"Tool returned an empty text block.{ServerLog.Describe()}");
        return text;
    }

    /// <summary>Text of a result that must have succeeded; on failure the message carries the server-side exception.</summary>
    public string SuccessText(CallToolResult result)
    {
        var text = SingleText(result);
        Assert.True(result.IsError is null or false, $"Expected success but the tool returned an error: {text}{ServerLog.Describe()}");
        return text;
    }

    /// <summary>Text of a result that must be a tool error (IsError = true, not a JSON-RPC error).</summary>
    public string ErrorText(CallToolResult result)
    {
        var text = SingleText(result);
        Assert.True(result.IsError == true, $"Expected IsError = true but the tool succeeded with: {text}");
        return text;
    }
}
