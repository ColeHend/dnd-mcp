using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DndMcp.Repository.Campaign;

/// <summary>One embedded schema migration.</summary>
/// <param name="Version">The <c>PRAGMA user_version</c> the database has after it (the file's NNNN).</param>
/// <param name="Name">The file name without extension ("0001_init"), stored in schema_migrations.</param>
/// <param name="Sql">The script, run as one multi-statement command inside the migration's transaction.</param>
public sealed record CampaignMigration(int Version, string Name, string Sql)
{
    /// <summary>The first line that turns foreign keys off for a table-rebuild migration.</summary>
    public const string ForeignKeysOffDirective = "-- dnd-mcp: foreign_keys=off";

    /// <summary>
    /// True when the script's first line is <see cref="ForeignKeysOffDirective"/>: the migrator then sets
    /// <c>PRAGMA foreign_keys=OFF</c> BEFORE <c>BEGIN</c> (inside a transaction the pragma is a silent no-op, and a table
    /// rebuild with keys on cascades deletes through every child table) and runs <c>PRAGMA foreign_key_check</c> before
    /// committing.
    /// </summary>
    public bool ForeignKeysOff =>
        Sql.TrimStart('﻿').Split('\n', 2)[0].Trim().Equals(ForeignKeysOffDirective, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a <see cref="CampaignDbMigrator.Migrate"/> call did.</summary>
/// <param name="FromVersion">user_version before.</param>
/// <param name="ToVersion">user_version after.</param>
/// <param name="Applied">The versions this call applied (empty when current, or when another process did it first).</param>
/// <param name="Backups">The pre-migration backups this call took.</param>
/// <param name="JournalMode">What <c>PRAGMA journal_mode=WAL</c> answered ("wal" unless the file system cannot do WAL).</param>
public sealed record MigrationResult(
    int FromVersion,
    int ToVersion,
    IReadOnlyList<int> Applied,
    IReadOnlyList<string> Backups,
    string JournalMode);

/// <summary>
/// Brings campaigns.db to the schema this build knows: the embedded <c>Campaign/Migrations/NNNN_name.sql</c> scripts,
/// tracked by <c>PRAGMA user_version</c> (contract §3.8).
///
/// <para>
/// <b>The sequence, per pending migration N</b>: read user_version → (a database that had a schema before this call only)
/// <c>VACUUM INTO</c> a <c>pre-migrate-vN</c> backup → <c>BEGIN IMMEDIATE</c> → re-read user_version → apply →
/// <c>PRAGMA user_version = N</c> → insert schema_migrations → commit; after the last one <c>PRAGMA journal_mode=WAL</c>.
/// Why each step:
/// <list type="bullet">
/// <item>The backup comes first because <c>VACUUM INTO</c> cannot run inside a transaction, and a failed backup stops
/// the migration: an update is never attempted without a copy to go back to. A brand-new file has nothing to back up.</item>
/// <item>The re-read after BEGIN IMMEDIATE: two server processes often start together (each Claude session spawns one),
/// both see the same pending version before either takes the write lock, and the second would re-run a script whose
/// CREATE TABLEs already exist. Holding the lock, the second sees the new version and stops.</item>
/// <item>user_version and the schema_migrations row commit with the script (<c>PRAGMA user_version</c> is transactional),
/// so a failure leaves the database exactly as it was, with the backup kept.</item>
/// <item>WAL is set after every migration call, not once at creation: a restored backup is in rollback-journal mode, and
/// the pragma is idempotent. A network file system may refuse WAL; that is logged and campaigns still work, one process
/// at a time.</item>
/// </list>
/// A database newer than this build (user_version above <see cref="LatestVersion"/>) is refused before anything is
/// written: reading tables whose meaning changed would corrupt them.
/// </para>
/// </summary>
public sealed partial class CampaignDbMigrator
{
    /// <summary>The embedded resources' logical-name prefix (fixed in DndMcp.Repository.csproj).</summary>
    public const string ResourcePrefix = "DndMcp.Repository.Campaign.Migrations.";

    private readonly ILogger? _logger;

    /// <summary>A migrator over the embedded migrations.</summary>
    public CampaignDbMigrator(ILogger? logger = null)
        : this(Embedded, logger)
    {
    }

    /// <summary>A migrator over a given set (tests add a failing or table-rebuild migration after the embedded ones).</summary>
    internal CampaignDbMigrator(IReadOnlyList<CampaignMigration> migrations, ILogger? logger = null)
    {
        Migrations = Validate(migrations);
        _logger = logger;
    }

    /// <summary>The embedded migrations, in version order (1, 2, …, with no gaps).</summary>
    public static IReadOnlyList<CampaignMigration> Embedded { get; } = Discover();

    /// <summary>The schema version this build writes: the highest embedded migration.</summary>
    public static int LatestVersion => Embedded[^1].Version;

    /// <summary>This migrator's migrations.</summary>
    public IReadOnlyList<CampaignMigration> Migrations { get; }

    /// <summary>
    /// Test seam: runs after a migration's backup and before its <c>BEGIN IMMEDIATE</c>, with the version about to be
    /// applied, so a test can let "another process" migrate in exactly the window the re-read exists for.
    /// </summary>
    internal Action<int>? BeforeBegin { get; set; }

    /// <summary>
    /// Applies every pending migration to the database open on <paramref name="connection"/> (no transaction open), then
    /// sets WAL.
    /// </summary>
    /// <param name="connection">An open read-write connection (house pragmas set).</param>
    /// <param name="backups">Where pre-migration backups go.</param>
    /// <param name="databasePath">For messages.</param>
    /// <exception cref="CampaignStoreUnavailableException">
    /// The database is newer than this build; a pre-migration backup could not be written; or a migration failed (rolled
    /// back; its backup kept and named in the message).
    /// </exception>
    public MigrationResult Migrate(SqliteConnection connection, CampaignBackups backups, string databasePath) =>
        Migrate(connection, backups, databasePath, applyRetention: true);

    /// <summary>
    /// <see cref="Migrate(SqliteConnection, CampaignBackups, string)"/>, optionally without the retention pass that follows
    /// a pre-migration backup: a restore migrates the backup it has just copied in, and retention there could delete the
    /// very backup it restored from (a restore deletes no backup file, so the same point can be restored again).
    /// </summary>
    internal MigrationResult Migrate(SqliteConnection connection, CampaignBackups backups, string databasePath, bool applyRetention)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(backups);
        var latest = Migrations[^1].Version;
        var from = UserVersion(connection, null);
        if (from > latest)
        {
            throw new CampaignStoreUnavailableException(NewerSchemaMessage(databasePath, from, latest));
        }

        // "Existing data" is decided once, before anything is applied: a file this call creates gets no backups, even
        // across several migrations.
        var hadSchema = from > 0 || connection.ScalarLong("SELECT count(*) FROM sqlite_master") > 0;
        var applied = new List<int>();
        var taken = new List<string>();
        while (true)
        {
            var version = UserVersion(connection, null);
            var next = Migrations.FirstOrDefault(m => m.Version > version);
            if (next is null)
            {
                break;
            }

            if (hadSchema)
            {
                try
                {
                    taken.Add(backups.Create(CampaignBackups.PreMigrateReason(next.Version), applyRetention: false));
                }
                catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
                {
                    throw new CampaignStoreUnavailableException(
                        $"Could not back up campaigns.db at {databasePath} before updating it to schema version {next.Version}, " +
                        $"so the update was not attempted and nothing was changed. Check that {backups.DirectoryPath} is " +
                        "writable and the disk has space, then try again.", ex);
                }
            }

            BeforeBegin?.Invoke(next.Version);
            if (!Apply(connection, next, databasePath, taken.LastOrDefault(), backups.Time))
            {
                continue;
            }

            applied.Add(next.Version);
            _logger?.LogInformation("campaigns.db at {Path} updated to schema version {Version} ({Name}).",
                databasePath, next.Version, next.Name);
        }

        if (taken.Count > 0 && applyRetention)
        {
            backups.ApplyRetention();
        }

        var mode = SetWal(connection);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning(
                "campaigns.db at {Path} could not switch to WAL journal mode (it answered \"{Mode}\"); the file system may " +
                "not support it. Campaigns still work, but several sessions writing at once will wait for each other longer.",
                databasePath, mode);
        }

        return new MigrationResult(from, UserVersion(connection, null), applied, taken, mode);
    }

    /// <summary>
    /// The refusal of a database newer than this build, for the user: which file, which versions, that nothing was
    /// changed, and what to do. One text for both places that refuse it: the migration at first use and
    /// <see cref="CampaignDatabase"/>'s per-use check, which catches a file another process migrated afterwards.
    /// </summary>
    internal static string NewerSchemaMessage(string databasePath, int version, int latest) =>
        $"campaigns.db at {databasePath} was written by a newer version of dnd-mcp (schema version " +
        $"{version.ToString(CultureInfo.InvariantCulture)}; this version understands up to " +
        $"{latest.ToString(CultureInfo.InvariantCulture)}), so this version will not read or change it. Nothing was changed. " +
        "Update dnd-mcp to the newer version, or point DND_MCP_DB at another file.";

    /// <summary><c>PRAGMA user_version</c>.</summary>
    public static int UserVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Sets WAL and returns what SQLite answered (lower-case).</summary>
    public static string SetWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        return (command.ExecuteScalar() as string ?? string.Empty).ToLowerInvariant();
    }

    // One migration in its own IMMEDIATE transaction. False when another process applied it first.
    private bool Apply(SqliteConnection connection, CampaignMigration migration, string databasePath, string? backup, TimeProvider time)
    {
        var foreignKeysOff = migration.ForeignKeysOff;
        if (foreignKeysOff)
        {
            connection.ExecuteText("PRAGMA foreign_keys = OFF");
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            if (UserVersion(connection, transaction) >= migration.Version)
            {
                _logger?.LogInformation(
                    "campaigns.db at {Path} was updated to schema version {Version} by another process; nothing to do.",
                    databasePath, migration.Version);
                return false;
            }

            try
            {
                Execute(connection, transaction, migration.Sql);
                if (foreignKeysOff)
                {
                    using var check = connection.CreateCommand();
                    check.Transaction = transaction;
                    check.CommandText = "PRAGMA foreign_key_check";
                    using var reader = check.ExecuteReader();
                    if (reader.Read())
                    {
                        throw new InvalidOperationException(
                            $"Migration {migration.Name} left rows whose foreign keys point nowhere (first: table " +
                            $"{reader.GetString(0)}, rowid {(reader.IsDBNull(1) ? "?" : reader.GetValue(1))}).");
                    }
                }

                Execute(connection, transaction,
                    $"PRAGMA user_version = {migration.Version.ToString(CultureInfo.InvariantCulture)}");
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO schema_migrations(version, name, applied_at) VALUES ($v, $n, $at)";
                    insert.Parameters.AddWithValue("$v", migration.Version);
                    insert.Parameters.AddWithValue("$n", migration.Name);
                    insert.Parameters.AddWithValue("$at", CampaignDatabase.FormatTimestamp(time.GetUtcNow()));
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
            {
                var kept = backup is null ? string.Empty : $"; the copy taken before the update is at {backup}";
                throw new CampaignStoreUnavailableException(
                    $"Updating campaigns.db at {databasePath} to schema version {migration.Version} ({migration.Name}) failed " +
                    $"and was rolled back, so nothing was changed{kept}. This is a bug in dnd-mcp; please report it. " +
                    "Campaign tools stay unavailable until it is fixed; dice, rules and balance tools still work.", ex);
            }
        }
        finally
        {
            if (foreignKeysOff)
            {
                connection.ExecuteText("PRAGMA foreign_keys = ON");
            }
        }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<CampaignMigration> Discover()
    {
        var assembly = typeof(CampaignDbMigrator).Assembly;
        var migrations = new List<CampaignMigration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var file = resource[ResourcePrefix.Length..];
            var match = FileNamePattern().Match(file);
            if (!match.Success)
            {
                throw new InvalidOperationException(
                    $"Embedded migration \"{file}\" is not named NNNN_name.sql (four digits, lower-case name).");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add(new CampaignMigration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                Path.GetFileNameWithoutExtension(file),
                reader.ReadToEnd()));
        }

        return Validate(migrations);
    }

    // Versions 1..N in order with no gap or duplicate: a gap means a migration file was lost from the build, and applying
    // the next one to a schema it was not written against is how data gets corrupted.
    private static IReadOnlyList<CampaignMigration> Validate(IReadOnlyList<CampaignMigration> migrations)
    {
        var ordered = migrations.OrderBy(m => m.Version).ToList();
        if (ordered.Count == 0)
        {
            throw new InvalidOperationException("No campaigns.db migrations are embedded.");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Version != i + 1)
            {
                throw new InvalidOperationException(
                    $"campaigns.db migrations must be numbered 1, 2, 3 … with no gap; found {string.Join(", ", ordered.Select(m => m.Version))}.");
            }
        }

        return ordered;
    }

    [GeneratedRegex(@"^(?<version>[0-9]{4})_[a-z0-9_]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}

/// <summary>Small ADO.NET conveniences for the campaign store's non-Dapper statements (each command disposed).</summary>
internal static class SqliteCommandExtensions
{
    public static int ExecuteText(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteNonQuery();
    }

    public static long ScalarLong(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public static string? ScalarText(this SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command.ExecuteScalar() as string;
    }
}
