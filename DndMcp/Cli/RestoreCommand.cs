using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;

namespace DndMcp.Cli;

/// <summary>
/// <c>DndMcp restore &lt;file&gt;</c>: replaces campaigns.db with a backup file, after saving what campaigns.db held as a
/// <c>pre-restore</c> backup, and prints what was restored and where the previous database went.
///
/// <para>
/// <b>A command, never a tool</b> (PLAN.md): other Claude sessions' servers may hold campaigns.db open, and replacing the
/// database under a conversation is not something the model should be able to do. The work is
/// <see cref="CampaignBackups.Restore(string)"/>: it checks the file (exists, SQLite, integrity, a campaigns schema this
/// build understands) before changing anything, saves the current database first (so a restore is itself undoable:
/// restoring the printed pre-restore file puts everything back), copies the backup in with SQLite's online backup API
/// (another process sees the old or the new database, never a torn file), and migrates an older backup.
/// </para>
/// <para>
/// <b>Other sessions must restart.</b> A running server keeps whatever it has cached about the old database (its current
/// campaign, its migration check), so the output says to restart them; nothing here can reach another process.
/// </para>
/// <para>
/// Exit codes as every command: 0 restored; 1 with the reason on stderr (not a backup, damaged, locked by another program,
/// newer than this build, the current database could not be saved first: nothing was changed in any of these); 2 for a
/// command line it does not understand, before any file is touched.
/// </para>
/// </summary>
internal static class RestoreCommand
{
    public const string Name = "restore";

    public static int Run(IReadOnlyList<string> options, DndMcpServerOptions serverOptions, TextWriter output, TextWriter error)
    {
        string? file = null;
        foreach (var option in options)
        {
            if (option.StartsWith('-'))
            {
                error.Write($"Unknown option '{option}' for {Name}.\n{DndMcpCli.Usage}");
                return DndMcpCli.ExitUsage;
            }

            if (file is not null)
            {
                error.Write($"{Name} takes one backup file; '{option}' is a second.\n{DndMcpCli.Usage}");
                return DndMcpCli.ExitUsage;
            }

            file = option;
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            error.Write($"{Name} needs the backup file to restore, e.g. DndMcp {Name} <data dir>/backups/campaigns-….db.\n{DndMcpCli.Usage}");
            return DndMcpCli.ExitUsage;
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
        RestoreResult result;
        try
        {
            result = database.Backups.Restore(file);
        }
        catch (Exception ex) when (ex is DndInputException or CampaignStoreUnavailableException)
        {
            // Both are written for the person running this: which file, what is wrong with it, what to do.
            return Fail(error, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            error.WriteLine(Unexplained(ex, file, databasePath));
            return DndMcpCli.ExitFailed;
        }

        var schema = result.BackupSchemaVersion == result.SchemaVersion
            ? $"version {Number(result.SchemaVersion)}"
            : $"version {Number(result.SchemaVersion)} (the backup's {Number(result.BackupSchemaVersion)}, migrated)";
        output.WriteLine($"campaigns.db restored from {result.RestoredFrom}.");
        output.WriteLine($"  path:      {result.DatabasePath}");
        output.WriteLine($"  previous:  {result.PreviousSavedTo ?? "none (there was no campaigns.db)"}");
        output.WriteLine($"  schema:    {schema}");
        output.WriteLine($"  campaigns: {(result.CampaignSlugs.Count == 0 ? "none" : string.Join(", ", result.CampaignSlugs))}");
        output.WriteLine(result.PreviousSavedTo is null
            ? "Restart any running dnd-mcp servers (other Claude sessions) so they read the restored database."
            : "Restart any running dnd-mcp servers (other Claude sessions) so they read the restored database. To undo this " +
              "restore, restore the previous file above.");
        return DndMcpCli.ExitOk;
    }

    /// <summary>
    /// The line for a failure Restore did not explain: an I/O, permission or SQLite error it maps to no message of its own.
    /// Restore turns every failure it can pin on a file into a message naming that file, and every failure after the
    /// backup is copied in into one that says so, so what reaches here happened before anything changed, and could be
    /// either file's. It names both and blames neither: it used to say "cannot restore into &lt;campaigns.db&gt;" with
    /// SQLite's own text, so a backup another program had locked sent the user to a campaigns.db that was fine. An I/O or
    /// permission error's own text is kept (it names its path); SQLite's is not.
    /// </summary>
    internal static string Unexplained(Exception exception, string backup, string databasePath)
    {
        var detail = exception is SqliteException ? string.Empty : " " + exception.Message;
        return $"{Name} failed; nothing was changed. It was restoring {Path.GetFullPath(backup)} into {databasePath}.{detail}";
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static int Fail(TextWriter error, string reason)
    {
        error.WriteLine($"{Name} failed: {reason}");
        return DndMcpCli.ExitFailed;
    }
}
