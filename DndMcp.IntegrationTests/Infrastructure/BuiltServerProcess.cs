using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>
/// The built DndMcp host running as a real child process, spoken to over its real stdin/stdout.
///
/// <para>
/// This exists because the in-memory harness cannot see stdout pollution: it swaps the stdio transport for pipes,
/// so a stray <c>Console.WriteLine</c> — from our code, a dependency, or the generic host's startup banner — goes
/// to the test runner's console and every in-memory test stays green, while Claude Code, reading the same stdout
/// as JSON-RPC, drops the session. Only the real process with the real Program.cs shows it.
/// </para>
/// <para>
/// stderr is drained continuously on its own thread. If it were not, a chatty server would fill the OS pipe
/// buffer, block on its next log write, and the test would time out for a reason unrelated to what it checks.
/// </para>
/// </summary>
public sealed class BuiltServerProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _workingDirectory;
    private readonly string _command;
    private readonly Channel<string> _unreadLines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<string> _stdout = new();
    private readonly ConcurrentQueue<string> _stderr = new();
    private readonly Task _stdoutPump;

    private BuiltServerProcess(Process process, string workingDirectory, string command)
    {
        _process = process;
        _workingDirectory = workingDirectory;
        _command = command;

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _stderr.Enqueue(e.Data);
            }
        };
        _process.BeginErrorReadLine();

        _stdoutPump = Task.Run(PumpStdoutAsync);
    }

    /// <summary>Every line the process has written to stdout so far, in order.</summary>
    public IReadOnlyList<string> StdoutLines => _stdout.ToArray();

    /// <summary>Every line the process has written to stderr so far (its log), in order.</summary>
    public IReadOnlyList<string> StderrLines => _stderr.ToArray();

    /// <summary>The private directory the process runs in and keeps its (isolated) data and cache under.</summary>
    public string WorkingDirectory => _workingDirectory;

    /// <summary>The server's process id, for tests that inspect what the process holds (its file watches).</summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// Starts the built host (<see cref="BuiltHost.Command"/>) in an empty temporary working directory, with its
    /// data locations isolated there by <see cref="IsolateUserData"/>.
    /// </summary>
    /// <param name="prepareWorkingDirectory">
    /// Fills the working directory before the process starts. Claude Code launches the server in the user's project, so
    /// this is how a test puts that project's files (an appsettings.json, many subdirectories) around the server.
    /// </param>
    public static BuiltServerProcess Start(Action<string>? prepareWorkingDirectory = null)
    {
        var workingDirectory = Directory.CreateTempSubdirectory("dnd-mcp-stdio-").FullName;
        prepareWorkingDirectory?.Invoke(workingDirectory);
        var (startInfo, command) = CreateStartInfo([], workingDirectory);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{command}'.");

        return new BuiltServerProcess(process, workingDirectory, command);
    }

    /// <summary>
    /// Runs the built host as a command (<c>DndMcp srd-build</c>) in <paramref name="workingDirectory"/>, with the same
    /// isolation as the server, and waits for it to exit. Several runs may share one working directory, which is how a
    /// test sees a second run reuse what the first built. The caller owns (and deletes) the directory.
    /// </summary>
    public static async Task<CommandResult> RunCommandAsync(IReadOnlyList<string> commandArguments, string workingDirectory, TimeSpan timeout)
    {
        var (startInfo, command) = CreateStartInfo(commandArguments, workingDirectory);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{command}'.");

        // A command reads nothing; closed stdin also means one that wrongly started the server would exit, not hang.
        process.StandardInput.Close();

        // Both streams are read concurrently: reading one to the end first can deadlock on the other's full pipe.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{command}' did not exit within {timeout.TotalSeconds}s.");
        }

        return new CommandResult(process.ExitCode, await stdout, await stderr, command);
    }

    private static (ProcessStartInfo StartInfo, string Command) CreateStartInfo(IReadOnlyList<string> commandArguments, string workingDirectory)
    {
        var (fileName, arguments) = BuiltHost.Command();

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments.Concat(commandArguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        IsolateUserData(startInfo.Environment, workingDirectory);
        return (startInfo, string.Join(' ', [fileName, .. arguments, .. commandArguments]));
    }

    /// <summary>
    /// Points every location the server reads or writes user data at <paramref name="workingDirectory"/>, and drops
    /// any other <c>DND_MCP_*</c> setting inherited from the test runner.
    /// </summary>
    /// <remarks>
    /// The child inherits the developer's environment. PLAN.md's overrides (DND_MCP_DATA_DIR, DND_MCP_CACHE_DIR,
    /// DND_MCP_DB, XDG_DATA_HOME, XDG_CACHE_HOME) exist precisely so a developer can export them — and a
    /// DND_MCP_CACHE_DIR or DND_MCP_DB left pointing at the real files would have this test's server rebuild the user's
    /// srd.db or open their campaigns.db. Every inherited DND_MCP_* is removed first so an override added later is
    /// isolated without editing this. StdoutPurityTests pins it.
    /// </remarks>
    public static void IsolateUserData(IDictionary<string, string?> environment, string workingDirectory)
    {
        foreach (var key in environment.Keys.Where(k => k.StartsWith("DND_MCP_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            environment.Remove(key);
        }

        environment["DND_MCP_CACHE_DIR"] = Path.Combine(workingDirectory, "cache");
        environment["DND_MCP_DATA_DIR"] = Path.Combine(workingDirectory, "data");
        environment["DND_MCP_DB"] = Path.Combine(workingDirectory, "data", "campaigns.db");
        environment["XDG_DATA_HOME"] = Path.Combine(workingDirectory, "xdg-data");
        environment["XDG_CACHE_HOME"] = Path.Combine(workingDirectory, "xdg-cache");
    }

    /// <summary>Writes one JSON-RPC message as one newline-terminated line, which is the MCP stdio framing.</summary>
    public async Task SendAsync(string json)
    {
        await _process.StandardInput.WriteAsync(json.AsMemory());
        await _process.StandardInput.WriteAsync("\n".AsMemory());
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>
    /// Reads stdout until a response (result or error) has arrived for every id, and returns them by id.
    /// Notifications are skipped. Throws if stdout ends or the token fires first — and at once on the first line
    /// that is not a JSON-RPC object.
    /// </summary>
    /// <remarks>
    /// Failing fast matters because stray output with no newline (a <c>Console.Write</c> banner) is glued onto the
    /// next response, which then never parses: skipping it would wait out the whole timeout and report a missing
    /// response instead of the stdout pollution that caused it.
    /// </remarks>
    public async Task<IReadOnlyDictionary<int, JsonElement>> WaitForResponsesAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
    {
        var responses = new Dictionary<int, JsonElement>();

        while (responses.Count < ids.Count)
        {
            string line;
            try
            {
                if (!await _unreadLines.Reader.WaitToReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"The server closed stdout before answering ids [{string.Join(", ", ids.Except(responses.Keys))}].{Diagnostics()}");
                }

                line = await _unreadLines.Reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"No response for ids [{string.Join(", ", ids.Except(responses.Keys))}] before the timeout.{Diagnostics()}");
            }

            if (!IsJsonRpcObject(line))
            {
                throw new InvalidOperationException(
                    $"stdout carried a line that is not a JSON-RPC 2.0 object, so a real client would drop the session:{Environment.NewLine}" +
                    $"  >> {line}{Diagnostics()}");
            }

            if (TryParseResponse(line, out var id, out var message) && ids.Contains(id))
            {
                responses[id] = message;
            }
        }

        return responses;
    }

    /// <summary>Closes stdin, which is how Claude Code ends a stdio server's session.</summary>
    public void CloseInput() => _process.StandardInput.Close();

    /// <summary>The exit code, or null if the process is still running after <paramref name="timeout"/>.</summary>
    public async Task<int?> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return _process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Waits for stdout to reach end-of-file so lines written during shutdown are included too.</summary>
    public async Task<IReadOnlyList<string>> ReadStdoutToEndAsync(TimeSpan timeout)
    {
        await _stdoutPump.WaitAsync(timeout);
        return StdoutLines;
    }

    /// <summary>stdout, the last stderr lines and the executable path, for assertion messages.</summary>
    public string Diagnostics(int stderrLines = 40)
    {
        var text = new StringBuilder()
            .AppendLine()
            .AppendLine($"Host: {_command}")
            .AppendLine("stdout:");
        foreach (var line in _stdout)
        {
            text.AppendLine("  " + line);
        }

        text.AppendLine($"stderr (last {stderrLines} lines):");
        foreach (var line in _stderr.TakeLast(stderrLines))
        {
            text.AppendLine("  " + line);
        }

        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            await _stdoutPump.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // Already gone, or stdout never closed after a kill; nothing more to release.
        }
        finally
        {
            _process.Dispose();
            TryDeleteDirectory(_workingDirectory);
        }
    }

    /// <summary>True for a line that is one JSON object carrying <c>"jsonrpc":"2.0"</c> — the only thing stdout may hold.</summary>
    public static bool IsJsonRpcObject(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("jsonrpc", out var version) &&
                   version.ValueKind == JsonValueKind.String &&
                   version.GetString() == "2.0";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseResponse(string line, out int id, out JsonElement message)
    {
        id = 0;
        message = default;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.Number &&
                idElement.TryGetInt32(out id) &&
                (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
            {
                message = root.Clone();
                return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON; the purity assertion reports it.
        }

        return false;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                _stdout.Enqueue(line);
                _unreadLines.Writer.TryWrite(line);
            }
        }
        finally
        {
            _unreadLines.Writer.TryComplete();
        }
    }
}

/// <summary>How a command run of the built host ended: exit code and everything it wrote.</summary>
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, string Command)
{
    /// <summary>The whole run, for assertion messages.</summary>
    public string Describe() =>
        $"{Environment.NewLine}Command: {Command}{Environment.NewLine}Exit code: {ExitCode}{Environment.NewLine}" +
        $"stdout:{Environment.NewLine}{Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{Stderr}";
}

/// <summary>
/// Finds the built host and the <c>dotnet</c> muxer to run it with — or the executable named by
/// <see cref="HostExecutableVariable"/>.
///
/// <para>
/// The ProjectReference copies <c>DndMcp.dll</c>, its runtimeconfig and deps files into this project's output, and
/// that copy is preferred: it is exactly the build these tests compiled against. The host project's own bin folder
/// is the fallback, resolved by walking up to <c>DndMcp.sln</c> rather than a fixed <c>../../../..</c>, which breaks
/// the moment the output path layout changes (a RID-specific build, artifacts output).
/// </para>
/// </summary>
public static class BuiltHost
{
    /// <summary>
    /// Set to a published binary (e.g. ~/.local/share/dnd-mcp/bin/DndMcp) to run the stdout-purity tests against the
    /// executable Claude Code actually launches, as the Phase 0 exit check requires. A self-contained single-file
    /// publish bundles its own runtime and startup path, so the Debug dll passing does not prove it clean.
    /// </summary>
    public const string HostExecutableVariable = "DND_MCP_TEST_HOST_EXE";

    private const string HostAssembly = "DndMcp.dll";

    /// <summary>The program and arguments that start the host.</summary>
    /// <remarks>
    /// A variable that is set but names a missing file throws rather than falling back to the dll: a silent
    /// fallback would let the exit check pass against the wrong binary.
    /// </remarks>
    public static (string FileName, IReadOnlyList<string> Arguments) Command()
    {
        var executable = Environment.GetEnvironmentVariable(HostExecutableVariable);
        if (!string.IsNullOrEmpty(executable))
        {
            var fullPath = Path.GetFullPath(executable);
            return File.Exists(fullPath)
                ? (fullPath, [])
                : throw new FileNotFoundException($"{HostExecutableVariable} is set to '{executable}', which does not exist.", fullPath);
        }

        return (DotnetExecutable(), [LocateDll()]);
    }

    public static string LocateDll()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidates = new List<string> { Path.Combine(baseDirectory, HostAssembly) };

        var configuration = typeof(BuiltHost).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        var targetFramework = Path.GetFileName(baseDirectory);
        for (var directory = new DirectoryInfo(baseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DndMcp.sln")))
            {
                candidates.Add(Path.Combine(directory.FullName, "DndMcp", "bin", configuration, targetFramework, HostAssembly));
                break;
            }
        }

        // A dll without its runtimeconfig cannot be started with `dotnet <dll>`, so it does not count.
        var found = candidates.FirstOrDefault(c => File.Exists(c) && File.Exists(Path.ChangeExtension(c, ".runtimeconfig.json")));

        return found ?? throw new FileNotFoundException(
            $"Could not find the built host (with its runtimeconfig.json). Build the solution first. Looked in: {string.Join("; ", candidates)}");
    }

    /// <summary>
    /// The muxer that is running these tests, so the child gets the same runtime. DOTNET_HOST_PATH is set by the
    /// .NET CLI for the processes it launches; the PATH lookup is the last resort.
    /// </summary>
    public static string DotnetExecutable()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var current = Environment.ProcessPath;
        if (current is not null && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
    }
}
