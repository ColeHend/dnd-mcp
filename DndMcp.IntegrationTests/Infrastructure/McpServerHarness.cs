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
/// The server runs inside the test process, with the developer's real environment and home directory, so every path
/// it touches is set here rather than left to the environment. The SRD index goes to <see cref="SharedCacheDirectory"/>
/// under the test output (never <c>~/.cache/dnd-mcp</c>): shared by every harness in the run, so srd.db is built once
/// per test run rather than once per test class. It is emptied before its first use in each run, because srd.db's
/// staleness key covers the content and the schema version but not the importer's code: a srd.db an earlier run left
/// behind would hide a pairing, alias or search-text change from every tool-level test.
/// <see cref="WithOptions"/> points a harness elsewhere (broken content, an unwritable cache). The data directory
/// (campaigns.db and its backups) is <see cref="DataDirectory"/>: a fresh directory per harness under
/// <see cref="DataRoot"/>, never the user's <c>~/.local/share/dnd-mcp</c>, and never shared between harnesses. Shared, one
/// test class making a campaign active would flip the edition every other parallel test class defaults to (the rules and
/// balance tools default to the active campaign's ruleset), and test order would decide results.
/// <see cref="BuiltServerProcess"/> isolates the child process's paths through its environment.
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

    /// <summary>
    /// Servers are built one at a time. When two threads build the same tool at once (xUnit runs test classes in
    /// parallel, each with its own harness), ModelContextProtocol 2.2.0 / Microsoft.Extensions.AI 10.8.3 intermittently
    /// leave an injected parameter such as <c>IProgress&lt;ProgressNotificationValue&gt;</c> in the tool's input schema,
    /// where it shows up as a "progress" argument. The real server builds its one server once, on one thread, so this is
    /// a test-only hazard; ServerSurfaceTests pins that parallel harnesses all see the production schema.
    /// </summary>
    private static readonly Lock ServerConstructionLock = new();

    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly CancellationTokenSource _serverCts = new();
    private readonly Action<IMcpServerBuilder>? _configureServer;
    private readonly Action<DndMcpServerOptions>? _configureOptions;
    private ServiceProvider? _provider;
    private Task _serverTask = Task.CompletedTask;
    private McpClient? _client;

    /// <summary>The production server exactly as Program.cs registers it, minus the stdio transport.</summary>
    public McpServerHarness()
    {
    }

    // Private: xUnit requires a class fixture to have exactly one public constructor.
    private McpServerHarness(Action<IMcpServerBuilder>? configureServer, Action<DndMcpServerOptions>? configureOptions)
    {
        _configureServer = configureServer;
        _configureOptions = configureOptions;
    }

    // Emptied on first use in each test process (Lazy runs the factory once, however many test classes ask at once).
    private static readonly Lazy<string> SharedCache = new(() =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-cache");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return directory;
    });

    /// <summary>
    /// The directory every harness keeps srd.db in: under the test output, so it is deleted with the build output and
    /// never the user's cache. Emptied before its first use in each test process, so the first harness builds srd.db with
    /// this run's importer (see the class summary).
    /// </summary>
    public static string SharedCacheDirectory => SharedCache.Value;

    // Emptied on first use in each test process, like the cache: a campaigns.db an earlier run left behind (a harness
    // whose dispose was skipped by a crashed run) must not reach this run.
    private static readonly Lazy<string> SharedDataRoot = new(() =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data");
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return directory;
    });

    /// <summary>The directory every harness's own data directory lives under: the test output, never the user's data.</summary>
    public static string DataRoot => SharedDataRoot.Value;

    /// <summary>
    /// This harness's data directory (<see cref="DndMcpServerOptions.DataDirectory"/>): a fresh directory under
    /// <see cref="DataRoot"/>, deleted when the harness is disposed. Not created until something writes there.
    /// </summary>
    public string DataDirectory { get; } = Path.Combine(DataRoot, Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The production registrations plus whatever <paramref name="configureServer"/> adds, typically
    /// <c>WithTools&lt;T&gt;(McpJson.Options)</c> for a test-only tool class, or a replacement service.
    /// </summary>
    public static McpServerHarness WithExtraTools(Action<IMcpServerBuilder> configureServer) => new(configureServer, null);

    /// <summary>
    /// The production server with its options changed after the harness's own (<see cref="SharedCacheDirectory"/>),
    /// e.g. a <see cref="DndMcpServerOptions.ContentRoot"/> with no content, to see what the model is told.
    /// </summary>
    public static McpServerHarness WithOptions(Action<DndMcpServerOptions> configureOptions) => new(null, configureOptions);

    /// <summary>
    /// Everything the server logged. When a call unexpectedly comes back as the SDK's generic error, the real
    /// exception is only here, so assertions include it via <see cref="CapturedLog.Describe"/>.
    /// </summary>
    public CapturedLog ServerLog { get; } = new();

    public McpClient Client =>
        _client ?? throw new InvalidOperationException("The harness has not been initialized; use it as an xUnit fixture or await InitializeAsync first.");

    /// <summary>The server's services, for tests that check what the server holds (the SRD index service).</summary>
    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("The harness has not been initialized; use it as an xUnit fixture or await InitializeAsync first.");

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddProvider(new CapturingLoggerProvider(ServerLog));
        });

        var server = services
            .AddDndMcpServer(options =>
            {
                options.CacheDirectory = SharedCacheDirectory;
                options.DataDirectory = DataDirectory;
                _configureOptions?.Invoke(options);
            })
            .WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream());
        _configureServer?.Invoke(server);

        // Scope validation on: stdio creates a DI scope per request, so a scoped service captured by a singleton
        // would work by accident in one request and leak state across the next. Fail here instead.
        McpServer mcpServer;
        lock (ServerConstructionLock)
        {
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            mcpServer = _provider.GetRequiredService<McpServer>();
        }

        _serverTask = mcpServer.RunAsync(_serverCts.Token);

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
            DeleteDataDirectory();
        }

        if (!stoppedOnEof)
        {
            throw new TimeoutException($"The MCP server did not stop within {ShutdownTimeout.TotalSeconds}s of its input closing.");
        }
    }

    // Best effort: every campaigns.db connection is opened with Pooling=false and closed after use, so nothing should hold
    // a file here once the server has stopped; a leftover directory is emptied with the rest of DataRoot next run.
    private void DeleteDataDirectory()
    {
        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
