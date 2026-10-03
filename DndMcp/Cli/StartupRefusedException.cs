namespace DndMcp.Cli;

/// <summary>
/// A hosted service refused to let the server start, for a reason it has already logged in full (today only
/// <see cref="Hosting.SqliteCapabilityCheck"/>: a SQLite library missing a feature). The message is the one line
/// <see cref="DndMcpCli.RunServerAsync"/> prints.
///
/// <para>
/// <b>Why a type of its own.</b> The generic host logs every start failure again, with its stack trace and source paths
/// ("Hosting failed to start"), before <see cref="DndMcpCli.RunServerAsync"/> can print its summary. For a refusal that
/// is noise between the service's own error line and the summary, and it reads like a crash. This type is how
/// <see cref="ReportedStartFailureLogFilter"/> tells a refusal (drop the host's repeat) from a real fault in a hosted
/// service (keep it: its stack trace is the only clue). Deriving from <see cref="InvalidOperationException"/> keeps what
/// callers of <c>SqliteCapabilities.EnsureSupported</c> already catch.
/// </para>
/// </summary>
public sealed class StartupRefusedException : InvalidOperationException
{
    public StartupRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
