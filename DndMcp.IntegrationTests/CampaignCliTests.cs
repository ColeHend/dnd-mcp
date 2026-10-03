using System.Security.Cryptography;
using DndMcp.Cli;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>DndMcp backup</c> copies the campaigns.db the server uses into its <c>backups/</c> directory and prints
/// the copy's path; <c>DndMcp restore &lt;file&gt;</c> replaces campaigns.db with a backup after saving what it held as a
/// pre-restore backup, and prints both; every failure exits 1 with the reason on stderr and changes nothing; anything
/// either command does not understand exits 2 with the usage on stderr, before a single file is touched.
///
/// <para>
/// Why it fails silently: a backup that "succeeded" into the wrong directory, or from the wrong campaigns.db, is found
/// missing on the day it is needed; a restore that replaced the database without keeping the old one is an unrecoverable
/// loss that looked like success; and a mistyped command line that still did something (a backup with a reason it
/// misread, a restore of the wrong file) is worse than one that did nothing. Every test reads the files themselves rather
/// than trusting the printed summary, and every refusal checks that the data directory is byte-for-byte what it was.
/// </para>
/// <para>
/// The in-process tests use a fixed clock, so each backup's name (and so its printed path) is exact; the built-binary test
/// proves Program.cs routes both commands before any host exists, against the child's isolated DND_MCP_DB.
/// </para>
/// </summary>
public sealed class CampaignCliTests : IDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private static readonly DateTimeOffset At = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private const string RestartLine =
        "Restart any running dnd-mcp servers (other Claude sessions) so they read the restored database.";

    private readonly string _data = Directory.CreateTempSubdirectory("dnd-mcp-campaign-cli-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private string DatabasePath => Path.Combine(_data, "campaigns.db");

    private string BackupsDirectory => Path.Combine(_data, CampaignBackups.DirectoryName);

    private DndMcpServerOptions Options(string? dataDirectory = null) =>
        new() { DataDirectory = dataDirectory ?? _data, Time = new FixedTime(At) };

    /// <summary>The backup path the fixed clock gives a backup taken by this process for <paramref name="reason"/>.</summary>
    private string BackupPath(string reason, int millisecondsLater = 0) =>
        Path.Combine(BackupsDirectory, CampaignBackups.FileName(At.AddMilliseconds(millisecondsLater), Environment.ProcessId, reason));

    /// <summary>A campaigns.db at <paramref name="path"/> holding the named campaigns (one instance: it created the file, so no daily backup).</summary>
    private static CampaignDatabase Seed(string path, params string[] names)
    {
        var database = new CampaignDatabase(path, new FixedTime(At));
        var store = new CampaignStore(database);
        foreach (var name in names)
        {
            store.Create(name, "dm", "2024");
        }

        return database;
    }

    [Fact]
    public void Run_BackupWithNoCampaignsDatabase_SaysSoAndExits1CreatingNothing()
    {
        var (exitCode, output, error) = Run(["backup"], Options());

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.Equal($"backup failed: there is no campaigns.db at {DatabasePath} to back up.\n", error.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, output);
        Assert.False(File.Exists(DatabasePath), "backup created a campaigns.db.");
        Assert.False(Directory.Exists(BackupsDirectory), "backup created a backups directory for nothing.");
    }

    [Theory]
    [InlineData(new string[0], "manual")]
    [InlineData(new[] { "--reason", "manual" }, "manual")]
    [InlineData(new[] { "--reason", "daily" }, "daily")]
    [InlineData(new[] { "--reason", "session-end" }, "session-end")]
    [InlineData(new[] { "--reason", "pre-restore" }, "pre-restore")]
    [InlineData(new[] { "--reason", "daily", "--reason", "manual" }, "manual")]
    public void Run_Backup_CopiesTheDatabaseIntoBackupsAndPrintsTheCopysPath(string[] options, string reason)
    {
        Seed(DatabasePath, "Belmakor", "One Piece");

        var (exitCode, output, error) = Run(["backup", .. options], Options());

        var path = BackupPath(reason);
        Assert.Equal(DndMcpCli.ExitOk, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            [
                $"campaigns.db backed up ({reason}).",
                $"  backup:   {path}",
                $"  from:     {DatabasePath}",
                $"  size:     {new FileInfo(path).Length:N0} bytes",
                "",
            ],
            output.ReplaceLineEndings("\n").Split('\n'));
        Assert.Equal(["belmakor", "one-piece"], Slugs(path));
    }

    [Fact]
    public void Run_BackupTwiceInTheSameMillisecond_KeepsBothCopies()
    {
        Seed(DatabasePath, "Belmakor");

        Run(["backup"], Options());
        var (exitCode, output, _) = Run(["backup"], Options());

        Assert.Equal(DndMcpCli.ExitOk, exitCode);
        Assert.Contains($"  backup:   {BackupPath("manual", millisecondsLater: 1)}\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.True(File.Exists(BackupPath("manual")));
    }

    [Fact]
    public void Run_BackupIntoAnUnwritableBackupsDirectory_PrintsTheReasonAndExits1()
    {
        Seed(DatabasePath, "Belmakor");
        File.WriteAllText(BackupsDirectory, "a file where the backups directory should be");

        var (exitCode, output, error) = Run(["backup"], Options());

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.StartsWith($"backup failed: cannot back up {DatabasePath} into {BackupsDirectory}: ", error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, output);
    }

    [Theory]
    [InlineData(new[] { "backup", "--force" }, "Unknown option '--force' for backup.\n")]
    [InlineData(new[] { "backup", "now" }, "Unknown option 'now' for backup.\n")]
    [InlineData(new[] { "backup", "--reason=manual" }, "Unknown option '--reason=manual' for backup.\n")]
    [InlineData(new[] { "backup", "--reason" }, "--reason needs a value for backup: manual, daily, session-end or pre-restore.\n")]
    [InlineData(new[] { "backup", "--reason", "pre-migrate-v1" }, "Unknown reason 'pre-migrate-v1' for backup; give manual, daily, session-end or pre-restore.\n")]
    [InlineData(new[] { "backup", "--reason", "before the dragon" }, "Unknown reason 'before the dragon' for backup; give manual, daily, session-end or pre-restore.\n")]
    [InlineData(new[] { "backup", "--reason", "Manual" }, "Unknown reason 'Manual' for backup; give manual, daily, session-end or pre-restore.\n")]
    [InlineData(new[] { "backup", "--reason", "daily", "--force" }, "Unknown option '--force' for backup.\n")]
    [InlineData(new[] { "restore" }, "restore needs the backup file to restore, e.g. DndMcp restore <data dir>/backups/campaigns-….db.\n")]
    [InlineData(new[] { "restore", " " }, "restore needs the backup file to restore, e.g. DndMcp restore <data dir>/backups/campaigns-….db.\n")]
    [InlineData(new[] { "restore", "a.db", "b.db" }, "restore takes one backup file; 'b.db' is a second.\n")]
    [InlineData(new[] { "backup", "-r", "manual" }, "Unknown option '-r' for backup.\n")]
    [InlineData(new[] { "restore", "--force", "a.db" }, "Unknown option '--force' for restore.\n")]
    [InlineData(new[] { "restore", "-f" }, "Unknown option '-f' for restore.\n")]
    [InlineData(new[] { "restore", "-f", "a.db" }, "Unknown option '-f' for restore.\n")]
    [InlineData(new[] { "restore", "a.db", "--dry-run" }, "Unknown option '--dry-run' for restore.\n")]
    public void Run_CommandLineNotUnderstood_PrintsUsageToStderrExits2AndTouchesNothing(string[] arguments, string firstLine)
    {
        // A real database and a real backup beside it, so a command that acted anyway would show in the snapshot.
        using (Seed(DatabasePath, "Belmakor"))
        {
        }

        Run(["backup"], Options());
        File.Copy(BackupPath("manual"), Path.Combine(_data, "a.db"));
        var before = Snapshot(_data);

        var (exitCode, output, error) = Run(arguments, Options());

        Assert.Equal(DndMcpCli.ExitUsage, exitCode);
        Assert.Equal(firstLine + DndMcpCli.Usage, error.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, output);
        Assert.Equal(before, Snapshot(_data));
    }

    [Fact]
    public void Run_RestoreABackup_ReplacesTheDatabaseSavesThePreviousOneAndPrintsBoth()
    {
        var database = Seed(DatabasePath, "Belmakor");
        var backup = database.Backups.Create(CampaignBackups.Reasons.Manual);
        new CampaignStore(database).Create("One Piece", "dm", "2014");

        var (exitCode, output, error) = Run(["restore", backup], Options());

        var previous = BackupPath(CampaignBackups.Reasons.PreRestore);
        Assert.Equal(DndMcpCli.ExitOk, exitCode);
        Assert.Equal(string.Empty, error);
        Assert.Equal(
            [
                $"campaigns.db restored from {backup}.",
                $"  path:      {DatabasePath}",
                $"  previous:  {previous}",
                "  schema:    version 1",
                "  campaigns: belmakor",
                RestartLine + " To undo this restore, restore the previous file above.",
                "",
            ],
            output.ReplaceLineEndings("\n").Split('\n'));
        Assert.Equal(["belmakor"], Slugs(DatabasePath));
        Assert.Equal(["belmakor", "one-piece"], Slugs(previous));
    }

    [Fact]
    public void Run_RestoreThePreRestoreFile_UndoesTheRestore()
    {
        var database = Seed(DatabasePath, "Belmakor");
        var backup = database.Backups.Create(CampaignBackups.Reasons.Manual);
        new CampaignStore(database).Create("One Piece", "dm", "2014");
        Run(["restore", backup], Options());

        var (exitCode, output, _) = Run(["restore", BackupPath(CampaignBackups.Reasons.PreRestore)], Options());

        Assert.Equal(DndMcpCli.ExitOk, exitCode);
        Assert.Contains("  campaigns: belmakor, one-piece\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains($"  previous:  {BackupPath(CampaignBackups.Reasons.PreRestore, millisecondsLater: 1)}\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(["belmakor", "one-piece"], Slugs(DatabasePath));
    }

    [Fact]
    public void Run_RestoreWithNoCampaignsDatabase_CreatesItAndSaysThereWasNoPreviousOne()
    {
        var elsewhere = Directory.CreateTempSubdirectory("dnd-mcp-campaign-cli-source-").FullName;
        try
        {
            var backup = Seed(Path.Combine(elsewhere, "campaigns.db"), "Belmakor").Backups.Create(CampaignBackups.Reasons.Manual);

            var (exitCode, output, error) = Run(["restore", backup], Options());

            Assert.Equal(DndMcpCli.ExitOk, exitCode);
            Assert.Equal(string.Empty, error);
            var lines = output.ReplaceLineEndings("\n").Split('\n');
            Assert.Equal("  previous:  none (there was no campaigns.db)", lines[2]);
            Assert.Equal(RestartLine, lines[5]);
            Assert.Equal(["belmakor"], Slugs(DatabasePath));
            Assert.False(
                Directory.Exists(BackupsDirectory) && Directory.EnumerateFiles(BackupsDirectory, "*pre-restore*").Any(),
                "A pre-restore backup of nothing was made.");
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-sqlite")]
    [InlineData("other-sqlite")]
    [InlineData("itself")]
    public void Run_RestoreSomethingThatIsNotABackup_SaysWhyExits1AndChangesNothing(string kind)
    {
        using (Seed(DatabasePath, "Belmakor"))
        {
        }

        var file = Path.Combine(_data, "candidate.db");
        string expected;
        switch (kind)
        {
            case "missing":
                expected = $"There is no backup file at {file}. List the backups in {BackupsDirectory}.";
                break;
            case "not-sqlite":
                File.WriteAllText(file, "not a database, just some text that happens to end in .db");
                expected = $"{file} is not a SQLite database (or is damaged), so it is not restored.";
                break;
            case "other-sqlite":
                using (var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
                {
                    other.Open();
                    using var create = other.CreateCommand();
                    create.CommandText = "CREATE TABLE notes (body TEXT)";
                    create.ExecuteNonQuery();
                }

                expected = $"{file} is a SQLite database but not a dnd-mcp campaigns database (it has no campaign table), so it " +
                           "is not restored. Choose a file from the backups directory.";
                break;
            default:
                file = DatabasePath;
                expected = $"{DatabasePath} is campaigns.db itself; give a backup file from {BackupsDirectory}.";
                break;
        }

        var before = Snapshot(_data);

        var (exitCode, output, error) = Run(["restore", file], Options());

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.Equal($"restore failed: {expected}\n", error.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, output);
        Assert.Equal(before, Snapshot(_data));
    }

    /// <summary>
    /// Kills I02 and I03 (FH10, M21): backup and restore print the path warnings (a relative DND_MCP_DATA_DIR is ignored)
    /// first, like srd-build, so a user whose override had no effect learns why the command used another campaigns.db. The
    /// environment is a stand-in whose home is a temporary directory: nothing touches the user's files.
    /// </summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("restore")]
    public void Run_RelativeDataDirectoryOverride_WarnsOnStderrFirst(string command)
    {
        var options = new DndMcpServerOptions
        {
            Paths = () => new Repository.DndMcpPaths(name => name == Repository.DndMcpPaths.DataDirectoryVariable ? "relative/data" : null, _data),
        };

        var (exitCode, _, error) = Run(command == "backup" ? ["backup"] : ["restore", Path.Combine(_data, "missing.db")], options);

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.StartsWith("warning: " + Repository.DndMcpPaths.DataDirectoryVariable, error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills I09 and I10 (FH10, M21): with no home directory and no DND_MCP_DATA_DIR or DND_MCP_DB there is no campaigns.db
    /// path; backup and restore say why and exit 1, like every other failure of theirs, rather than crash with a stack trace.
    /// </summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("restore")]
    public void Run_NoPathForCampaignsDb_Exits1WithTheReason(string command)
    {
        var options = new DndMcpServerOptions { Paths = () => new Repository.DndMcpPaths(_ => null, homeDirectory: null) };

        var (exitCode, output, error) = Run(command == "backup" ? ["backup"] : ["restore", Path.Combine(_data, "no-such-backup.db")], options);

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.StartsWith($"{command} failed: ", error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// Kills I11 (FH10, M21): a campaigns.db SQLite cannot read (here not a database at all, the realistic reason to back up
    /// by hand) fails the backup with the reason and exit 1, never an escaped SqliteException. The unwritable-directory test
    /// above fails with an IOException before SQLite runs.
    /// </summary>
    [Fact]
    public void Run_BackupOfACampaignsDbSqliteCannotRead_Exits1WithTheReason()
    {
        File.WriteAllText(DatabasePath, "This is not a database, only text where campaigns.db should be.");

        var (exitCode, output, error) = Run(["backup"], Options());

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.StartsWith($"backup failed: cannot back up {DatabasePath} into ", error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, output);
    }

    /// <summary>
    /// Kills P02 (FH10, M21): when the current campaigns.db cannot be saved before a restore (it is damaged), restore
    /// refuses with the store's message and exit 1 and changes nothing, never an escaped exception.
    /// </summary>
    [Fact]
    public void Run_RestoreWhenTheCurrentDatabaseCannotBeSavedFirst_Exits1WithTheReasonAndChangesNothing()
    {
        string backup;
        using (var source = Seed(Path.Combine(_data, "source", "campaigns.db"), "Old"))
        {
            backup = source.Backups.Create("manual");
        }

        File.WriteAllText(DatabasePath, "not a database at all");

        var (exitCode, _, error) = Run(["restore", backup], Options());

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.StartsWith("restore failed: Could not save the current campaigns.db", error, StringComparison.Ordinal);
        Assert.Equal("not a database at all", File.ReadAllText(DatabasePath));
    }

    /// <summary>
    /// RR02: a backup another program holds locked (a sqlite3 shell with it open in a write transaction) is refused naming
    /// the backup, exit 1, nothing changed. Its SQLITE_BUSY used to escape Restore and be reported as "cannot restore into
    /// &lt;campaigns.db&gt;: SQLite Error 5: 'database is locked'", sending the user to a campaigns.db that was fine.
    /// </summary>
    [Fact]
    public void Run_RestoreABackupAnotherProgramHoldsLocked_NamesTheBackupExits1AndChangesNothing()
    {
        using (Seed(DatabasePath, "Belmakor"))
        {
        }

        string backup;
        using (var source = Seed(Path.Combine(_data, "source", "campaigns.db"), "Old"))
        {
            backup = source.Backups.Create("manual");
        }

        var before = Snapshot(_data);
        int exitCode;
        string output;
        string error;
        using (var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
        {
            holder.Open();
            using var hold = holder.CreateCommand();
            hold.CommandText = "PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE; UPDATE campaign SET summary_md = 'held';";
            hold.ExecuteNonQuery();

            (exitCode, output, error) = Run(["restore", backup], Options());
        }

        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.Equal(
            $"restore failed: {backup} is locked by another program, so it is not restored. Close it and try again, or choose " +
            $"another backup from {BackupsDirectory}.\n",
            error.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, output);
        Assert.Equal(before, Snapshot(_data));
    }

    /// <summary>
    /// RR02: a failure Restore does not explain itself (here campaigns.db's directory is a file, so it cannot be created)
    /// says nothing was changed, names the backup it was restoring and where to, and blames neither: it used to say
    /// "cannot restore into &lt;campaigns.db&gt;" with the exception's text, which for a SQLite error is SQLite's own words.
    /// An I/O error's own text is kept (it names its path); SQLite's is left out. A backup given as a relative path is
    /// named by its full path, as every other restore message names it.
    /// </summary>
    [Fact]
    public void Run_RestoreFailingWithAnErrorRestoreDoesNotExplain_SaysNothingWasChangedAndBlamesNeitherFile()
    {
        string backup;
        using (var source = Seed(Path.Combine(_data, "source", "campaigns.db"), "Old"))
        {
            backup = source.Backups.Create("manual");
        }

        var blocker = Path.Combine(_data, "blocker");
        File.WriteAllText(blocker, "a file where campaigns.db's directory should be");
        var before = Snapshot(_data);

        var (exitCode, output, error) = Run(["restore", backup], Options(blocker));

        var stopped = $"restore failed; nothing was changed. It was restoring {backup} into {Path.Combine(blocker, "campaigns.db")}. ";
        Assert.Equal(DndMcpCli.ExitFailed, exitCode);
        Assert.StartsWith(stopped, error, StringComparison.Ordinal);
        Assert.Contains(blocker, error[stopped.Length..], StringComparison.Ordinal);
        Assert.Equal(string.Empty, output);
        Assert.Equal(before, Snapshot(_data));
        Assert.Equal(
            $"restore failed; nothing was changed. It was restoring {backup} into {DatabasePath}.",
            RestoreCommand.Unexplained(new SqliteException("database is locked", 5), backup, DatabasePath));
        var relative = Path.Combine("backups", Path.GetFileName(backup));
        Assert.Equal(
            $"restore failed; nothing was changed. It was restoring {Path.GetFullPath(relative)} into {DatabasePath}.",
            RestoreCommand.Unexplained(new SqliteException("database is locked", 5), relative, DatabasePath));
    }

    [Fact]
    public void Usage_NamesBackupAndRestoreWithTheirArguments()
    {
        Assert.Contains(
            "  DndMcp backup [--reason R]  Back up campaigns.db into the backups directory beside it and print the backup's\n" +
            "                              path. R names it: manual (default), daily, session-end or pre-restore.\n",
            DndMcpCli.Usage, StringComparison.Ordinal);
        Assert.Contains(
            "  DndMcp restore <file>       Replace campaigns.db with a backup file; the current campaigns.db is saved first as a\n" +
            "                              pre-restore backup. Restart other running dnd-mcp servers afterwards.\n",
            DndMcpCli.Usage, StringComparison.Ordinal);
        Assert.All(DndMcpCli.Usage.Split('\n'), line => Assert.True(line.Length <= 120, $"Usage line too wide: {line}"));
    }

    [Fact]
    public async Task BuiltHost_BackupAndRestore_UseTheIsolatedCampaignsDatabaseWithTheirExitCodes()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("dnd-mcp-campaign-cli-").FullName;
        try
        {
            // The child's DND_MCP_DB (BuiltServerProcess.IsolateUserData).
            var database = Path.Combine(workingDirectory, "data", "campaigns.db");

            var nothing = await BuiltServerProcess.RunCommandAsync(["backup"], workingDirectory, CommandTimeout);
            Assert.True(nothing.ExitCode == 1, nothing.Describe());
            Assert.Equal($"backup failed: there is no campaigns.db at {database} to back up.\n", nothing.Stderr.ReplaceLineEndings("\n"));
            Assert.Equal(string.Empty, nothing.Stdout);

            using (Seed(database, "Belmakor"))
            {
            }

            var backup = await BuiltServerProcess.RunCommandAsync(["backup"], workingDirectory, CommandTimeout);
            Assert.True(backup.ExitCode == 0, backup.Describe());
            Assert.Equal(string.Empty, backup.Stderr);
            var backupPath = backup.Stdout.ReplaceLineEndings("\n").Split('\n')[1]["  backup:   ".Length..];
            Assert.StartsWith(Path.Combine(workingDirectory, "data", "backups", "campaigns-"), backupPath, StringComparison.Ordinal);
            Assert.EndsWith("-manual.db", backupPath, StringComparison.Ordinal);
            Assert.Equal(["belmakor"], Slugs(backupPath));

            var refused = await BuiltServerProcess.RunCommandAsync(["backup", "--bogus"], workingDirectory, CommandTimeout);
            Assert.True(refused.ExitCode == 2, refused.Describe());
            Assert.Equal("Unknown option '--bogus' for backup.\n" + DndMcpCli.Usage, refused.Stderr.ReplaceLineEndings("\n"));
            Assert.Single(Directory.GetFiles(Path.Combine(workingDirectory, "data", "backups")));

            var restore = await BuiltServerProcess.RunCommandAsync(["restore", backupPath], workingDirectory, CommandTimeout);
            Assert.True(restore.ExitCode == 0, restore.Describe());
            Assert.StartsWith($"campaigns.db restored from {backupPath}.\n  path:      {database}\n  previous:  ", restore.Stdout.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Equal(string.Empty, restore.Stderr);

            var missing = await BuiltServerProcess.RunCommandAsync(["restore", "nope.db"], workingDirectory, CommandTimeout);
            Assert.True(missing.ExitCode == 1, missing.Describe());
            Assert.StartsWith($"restore failed: There is no backup file at {Path.Combine(workingDirectory, "nope.db")}.", missing.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    /// <summary>The campaign slugs a campaigns.db (or backup) holds, read directly.</summary>
    private static List<string> Slugs(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slug FROM campaign ORDER BY slug";
        using var reader = command.ExecuteReader();
        var slugs = new List<string>();
        while (reader.Read())
        {
            slugs.Add(reader.GetString(0));
        }

        return slugs;
    }

    /// <summary>Every file under <paramref name="directory"/> with a hash of its bytes, in path order.</summary>
    private static List<string> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(directory, f)} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}")
            .ToList();

    private static (int ExitCode, string Output, string Error) Run(string[] arguments, DndMcpServerOptions options)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = DndMcpCli.Run(arguments, options, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    /// <summary>A clock stopped at one instant: backup names are then exact.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
