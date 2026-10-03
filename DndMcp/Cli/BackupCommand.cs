using System.Globalization;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;

namespace DndMcp.Cli;

/// <summary>
/// <c>DndMcp backup [--reason R]</c>: takes a backup of campaigns.db (the one the server uses) into its <c>backups/</c>
/// directory and prints where it went.
///
/// <para>
/// <b>What it is for.</b> The server backs up on its own (the first write of each day, every session end, before every
/// migration and restore); this is the "before I try something" copy a user takes by hand, and the check that backups
/// work at all on this machine. It uses the same <see cref="CampaignBackups.Create"/> as the server (<c>VACUUM INTO</c>,
/// never a file copy: a copy of a WAL database silently lacks the commits still in its -wal), so it is safe while a
/// server has the database open, and the backup counts toward the same retention.
/// </para>
/// <para>
/// <b>The reason is a name, not a note.</b> A backup's reason is part of its file name, which retention parses (and which
/// is the only place a reason is kept), so <c>--reason</c> takes one of the names a user may give: <c>manual</c> (the
/// default), <c>daily</c>, <c>session-end</c> or <c>pre-restore</c>. <c>pre-migrate-v&lt;N&gt;</c> is refused: retention keeps
/// those forever, and a hand-made one would claim a migration that never ran.
/// </para>
/// <para>
/// <b>It never migrates or creates.</b> A backup copies what is there: no campaigns.db is a failure (exit 1), and an
/// older schema is copied as it is. A command line it does not understand exits 2 before anything is touched.
/// </para>
/// </summary>
internal static class BackupCommand
{
    public const string Name = "backup";

    /// <summary>The reasons <c>--reason</c> accepts, the default first.</summary>
    public static IReadOnlyList<string> Reasons { get; } =
    [
        CampaignBackups.Reasons.Manual,
        CampaignBackups.Reasons.Daily,
        CampaignBackups.Reasons.SessionEnd,
        CampaignBackups.Reasons.PreRestore,
    ];

    // "manual, daily, session-end or pre-restore": the accepted names, for the usage errors.
    private static string ReasonList => string.Join(", ", Reasons.SkipLast(1)) + " or " + Reasons[^1];

    public static int Run(IReadOnlyList<string> options, DndMcpServerOptions serverOptions, TextWriter output, TextWriter error)
    {
        var reason = CampaignBackups.Reasons.Manual;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i] != "--reason")
            {
                error.Write($"Unknown option '{options[i]}' for {Name}.\n{DndMcpCli.Usage}");
                return DndMcpCli.ExitUsage;
            }

            if (i + 1 >= options.Count)
            {
                error.Write($"--reason needs a value for {Name}: {ReasonList}.\n{DndMcpCli.Usage}");
                return DndMcpCli.ExitUsage;
            }

            var value = options[++i];
            if (!Reasons.Contains(value, StringComparer.Ordinal))
            {
                error.Write($"Unknown reason '{value}' for {Name}; give {ReasonList}.\n{DndMcpCli.Usage}");
                return DndMcpCli.ExitUsage;
            }

            reason = value;
        }

        foreach (var warning in serverOptions.PathWarnings())
        {
            error.WriteLine($"warning: {warning}");
        }

        string databasePath;
        try
        {
            databasePath = serverOptions.ResolveCampaignDatabasePath();
        }
        catch (InvalidOperationException ex)
        {
            return Fail(error, ex.Message);
        }

        using var database = new CampaignDatabase(databasePath, serverOptions.Time);
        if (!database.Exists)
        {
            return Fail(error, $"there is no campaigns.db at {databasePath} to back up.");
        }

        try
        {
            var path = database.Backups.Create(reason);
            var size = new FileInfo(path).Length.ToString("N0", CultureInfo.InvariantCulture);
            output.WriteLine($"campaigns.db backed up ({reason}).");
            output.WriteLine($"  backup:   {path}");
            output.WriteLine($"  from:     {databasePath}");
            output.WriteLine($"  size:     {size} bytes");
            return DndMcpCli.ExitOk;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            return Fail(error, $"cannot back up {databasePath} into {database.Backups.DirectoryPath}: {ex.Message}");
        }
    }

    private static int Fail(TextWriter error, string reason)
    {
        error.WriteLine($"{Name} failed: {reason}");
        return DndMcpCli.ExitFailed;
    }
}
