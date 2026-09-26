using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: a backup taken with <c>VACUUM INTO</c> while campaigns.db is open in WAL mode contains every
/// committed write, including writes that so far exist only in the -wal file, and is one self-contained,
/// readable file (PLAN.md §6 Operations: "Backups use VACUUM INTO (a raw file copy can miss committed WAL
/// data)"; the pre-migration copy <c>VACUUM INTO backups/pre-migrate-vN.db</c>).
/// </summary>
public sealed class VacuumIntoBackupTests : IDisposable
{
    private const string Db = "campaigns.db";

    private readonly SqliteScratch _scratch = new();
    private readonly SqliteConnection _live;

    /// <summary>
    /// Sets up the dangerous state on purpose. With WAL and auto-checkpoint off, the schema is checkpointed
    /// into the main file, then three rows are committed. They exist only in the -wal file, and stay there
    /// because this connection stays open (the last connection to close would checkpoint them).
    /// </summary>
    public VacuumIntoBackupTests()
    {
        _live = _scratch.Open(Db);
        _live.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
        _live.Execute("PRAGMA wal_autocheckpoint = 0");
        _live.Execute("CREATE TABLE session(seq INTEGER PRIMARY KEY, title TEXT NOT NULL) STRICT");
        _live.ExecuteScalar<string>("PRAGMA wal_checkpoint(TRUNCATE)");
        _live.Execute("INSERT INTO session(title) VALUES ('The Listen'), ('The Keras Fight'), ('Dinosaur Island')");
    }

    public void Dispose()
    {
        _live.Dispose();
        _scratch.Dispose();
    }

    /// <summary>
    /// The counterfactual that proves the setup: the committed rows are in the WAL, and a raw copy of the
    /// .db file alone opens fine but has none of them. That silent loss is why backups must not be file copies.
    /// </summary>
    [Fact]
    public void RawFileCopy_WhileCommittedDataIsOnlyInWal_SilentlyMissesIt()
    {
        Assert.True(new FileInfo(_scratch.PathOf(Db + "-wal")).Length > 0, "Setup failed: nothing is in the WAL.");

        File.Copy(_scratch.PathOf(Db), _scratch.PathOf("raw-copy.db"));
        using var copy = _scratch.Open("raw-copy.db");

        Assert.Equal(0, copy.ExecuteScalar<long>("SELECT count(*) FROM session"));
    }

    /// <summary>
    /// PLAN.md §6 "Backups use VACUUM INTO". The backup has every committed row and passes integrity_check.
    /// It is in rollback-journal ("delete") mode, so it is one file with no -wal sidecar, safe to move,
    /// copy or hand to the restore CLI. The path is a bound parameter, so backup paths containing quotes need
    /// no escaping.
    /// </summary>
    [Fact]
    public void VacuumInto_WhileCommittedDataIsOnlyInWal_BackupContainsIt()
    {
        var backupPath = _scratch.PathOf("dnd-2026-09-26-session'end.db");

        _live.Execute("VACUUM INTO @backupPath", new { backupPath });

        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        backup.Open();
        Assert.Equal(
            new[] { "The Listen", "The Keras Fight", "Dinosaur Island" },
            backup.Query<string>("SELECT title FROM session ORDER BY seq"));
        Assert.Equal("ok", backup.ExecuteScalar<string>("PRAGMA integrity_check"));
        Assert.Equal("delete", backup.ExecuteScalar<string>("PRAGMA journal_mode"));
        Assert.False(File.Exists(backupPath + "-wal"));
    }

    /// <summary>
    /// VACUUM INTO refuses to overwrite an existing file. Every backup name must therefore be unique per
    /// backup, or the second backup to the same name fails and the operation it guards (a migration or
    /// import) must not continue.
    ///
    /// <para>
    /// PLAN.md §6 Operations currently breaks this for migrations: <c>VACUUM INTO backups/pre-migrate-vN.db</c>
    /// is the same name every time migration N is attempted. If migration N fails after its backup (it rolls
    /// back, or the process dies), the next start retries it, hits "output file already exists", and the
    /// migration stays blocked until someone deletes the file by hand. Silently reusing the old file is only
    /// safe if nothing wrote in between, and other sessions on the same campaigns.db can. The pre-migration
    /// name needs a timestamp, like every other backup.
    /// </para>
    /// </summary>
    [Fact]
    public void VacuumInto_TargetAlreadyExists_Fails()
    {
        var backupPath = _scratch.PathOf("backup.db");
        _live.Execute("VACUUM INTO @backupPath", new { backupPath });

        var exception = Assert.Throws<SqliteException>(() => _live.Execute("VACUUM INTO @backupPath", new { backupPath }));

        Assert.Contains("output file already exists", exception.Message);
    }
}
