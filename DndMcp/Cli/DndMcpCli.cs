using DndMcp.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DndMcp.Cli;

/// <summary>
/// The command-line side of the executable: <c>DndMcp &lt;command&gt;</c> runs one operation and exits, where plain
/// <c>DndMcp</c> (no arguments, which is how Claude Code launches it) is the MCP server.
///
/// <para>
/// Commands exist for work that should not happen inside an MCP session: pre-building or checking srd.db after an
/// install (<c>srd-build</c>), and the operations on campaigns.db that must not be tools (<c>backup</c>, <c>restore</c>:
/// a restore replaces a file other sessions' servers may hold open, so it is never something the model can trigger). They
/// run before any host is built, so they never start the stdio transport, and stdout is theirs to use: in this mode
/// nothing is reading it as JSON-RPC. Output goes through the <see cref="TextWriter"/>s passed in, so tests run commands
/// in-process.
/// </para>
/// <para>
/// Exit codes: <see cref="ExitOk"/>; <see cref="ExitFailed"/> for a command (or a server start) that could not do its job,
/// with the reason on stderr; <see cref="ExitUsage"/> for a command line it does not understand, with the usage on stderr
/// and nothing done: every argument is read before a command touches a file.
/// </para>
/// </summary>
internal static class DndMcpCli
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    public const string Usage =
        "Usage:\n" +
        "  DndMcp                      Run the MCP server on stdin/stdout (how Claude Code starts it).\n" +
        "  DndMcp srd-build [--force]  Build the SRD rules index (srd.db), or confirm the existing one is current, and\n" +
        "                              print a summary. --force rebuilds even when it is current.\n" +
        "  DndMcp backup [--reason R]  Back up campaigns.db into the backups directory beside it and print the backup's\n" +
        "                              path. R names it: manual (default), daily, session-end or pre-restore.\n" +
        "  DndMcp restore <file>       Replace campaigns.db with a backup file; the current campaigns.db is saved first as a\n" +
        "                              pre-restore backup. Restart other running dnd-mcp servers afterwards.\n" +
        "  DndMcp help                 Show this text.\n";

    /// <summary>Runs the command <paramref name="args"/> names and returns the process exit code.</summary>
    /// <param name="args">The command line; must not be empty (no arguments means "run the server").</param>
    public static int Run(IReadOnlyList<string> args, DndMcpServerOptions options, TextWriter output, TextWriter error)
    {
        ArgumentOutOfRangeException.ThrowIfZero(args.Count);

        switch (args[0])
        {
            case SrdBuildCommand.Name:
                return SrdBuildCommand.Run(args.Skip(1).ToList(), options, output, error);

            case BackupCommand.Name:
                return BackupCommand.Run(args.Skip(1).ToList(), options, output, error);

            case RestoreCommand.Name:
                return RestoreCommand.Run(args.Skip(1).ToList(), options, output, error);

            case "help" or "--help" or "-h":
                output.Write(Usage);
                return ExitOk;

            default:
                error.Write($"Unknown command '{args[0]}'.\n{Usage}");
                return ExitUsage;
        }
    }

    /// <summary>
    /// The server's logging (Program.cs): every level to stderr, because stdout carries the MCP JSON-RPC stream and a single
    /// stray line breaks the session (StdoutPurityTests checks the built binary); and the host's repeat of a refused start
    /// dropped (<see cref="ReportedStartFailureLogFilter"/>), so a refusal shows the service's line and one summary.
    /// </summary>
    public static void AddServerLogging(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders();
        logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        logging.DropReportedStartFailures();
    }

    /// <summary>
    /// Runs the MCP server host until it shuts down (<see cref="ExitOk"/>), or, when it cannot start, disposes it and
    /// returns <see cref="ExitFailed"/> after one line on <paramref name="error"/>: "dnd-mcp could not start: &lt;why&gt;".
    /// </summary>
    /// <remarks>
    /// A start that throws (<see cref="SqliteCapabilityCheck"/> finding a SQLite library without FTS5) would otherwise end
    /// the process with an unhandled-exception dump and the runtime's own exit code, which Claude Code shows as "failed to
    /// connect" with nothing to act on. The failing service has already logged its detail; the host is disposed before
    /// the summary is written so the logger's queued lines come first. A <see cref="StartupRefusedException"/> is not
    /// logged again by the host (Program.cs wraps the console logger in <see cref="ReportedStartFailureLogFilter"/>), so
    /// a refused start shows the service's error line and this summary, not a stack trace between them; any other start
    /// failure keeps the host's entry with its stack trace. Only the start is guarded: an exception after the server is
    /// running is the host's to report, as before.
    /// </remarks>
    public static async Task<int> RunServerAsync(IHost host, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(error);
        string? failure;
        try
        {
            try
            {
                await host.StartAsync();
                failure = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = OneLine(ex is AggregateException { InnerExceptions.Count: 1 } single ? single.InnerExceptions[0] : ex);
            }

            if (failure is null)
            {
                await host.WaitForShutdownAsync();
                return ExitOk;
            }
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }

        error.WriteLine($"dnd-mcp could not start: {failure}");
        return ExitFailed;
    }

    private static string OneLine(Exception ex) =>
        string.Join(' ', ex.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
