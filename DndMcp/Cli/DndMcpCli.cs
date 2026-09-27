using DndMcp.Hosting;

namespace DndMcp.Cli;

/// <summary>
/// The command-line side of the executable: <c>DndMcp &lt;command&gt;</c> runs one operation and exits, where plain
/// <c>DndMcp</c> (no arguments, which is how Claude Code launches it) is the MCP server.
///
/// <para>
/// Commands exist for work that should not happen inside an MCP session: pre-building or checking srd.db after an
/// install (<c>srd-build</c>), and from Phase 6 the operations that need exclusive database access. They run before any
/// host is built, so they never start the stdio transport, and stdout is theirs to use: in this mode nothing is reading
/// it as JSON-RPC. Output goes through the <see cref="TextWriter"/>s passed in, so tests run commands in-process.
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

            case "help" or "--help" or "-h":
                output.Write(Usage);
                return ExitOk;

            default:
                error.Write($"Unknown command '{args[0]}'.\n{Usage}");
                return ExitUsage;
        }
    }
}
