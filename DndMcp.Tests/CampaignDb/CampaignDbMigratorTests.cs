using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// Invariant: campaigns.db is only ever moved between schema versions by <see cref="CampaignDbMigrator"/>'s sequence
/// (contract §3.8): an existing database is backed up first, each migration commits with its user_version and
/// schema_migrations row or not at all, a second process that migrated first is noticed after BEGIN IMMEDIATE, and a
/// database newer than the build is left alone. Each of these, broken, loses or corrupts a user's campaigns on an
/// upgrade, which is the one moment nobody is watching.
/// </summary>
public sealed class CampaignDbMigratorTests : IDisposable
{
    private static readonly string[] ExpectedTables =
    [
        "app_state", "beat_edge", "campaign", "change_log", "clock", "cross_link", "dice_roll", "entity", "entity_alias",
        "entity_fts", "entity_tag", "fact", "fact_dependency", "fact_fts", "fact_link", "knowledge", "objective", "relation",
        "schema_migrations", "session", "session_attendance", "tag",
    ];

    private static readonly CampaignMigration AddNotesTable =
        new(2, "0002_notes", "CREATE TABLE extra_note (id TEXT PRIMARY KEY, body TEXT NOT NULL) STRICT;");

    private readonly CampaignTestDb _db = new(create: false);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Migrate_FreshDatabase_CreatesEveryTableAtTheLatestVersionWithoutABackup()
    {
        using var connection = OpenRaw();

        var result = new CampaignDbMigrator().Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(0, result.FromVersion);
        Assert.Equal(CampaignDbMigrator.LatestVersion, result.ToVersion);
        Assert.Equal(new[] { 1 }, result.Applied);
        Assert.Empty(result.Backups);
        Assert.Equal("wal", result.JournalMode);
        Assert.Equal(ExpectedTables, Tables(connection));
        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
        Assert.Equal(new[] { (1L, "0001_init") }, connection.Query<(long, string)>("SELECT version, name FROM schema_migrations").ToList());
        Assert.False(Directory.Exists(_db.BackupsPath));
    }

    /// <summary>
    /// A file system that cannot do WAL (here an in-memory database, which answers "memory") is not fatal: the migration
    /// completes, the result says what SQLite answered, and a warning names the file, so a slow multi-session setup can be
    /// explained from the log.
    /// </summary>
    [Fact]
    public void Migrate_JournalModeIsNotWal_WarnsNamingTheFileAndCarriesOn()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True;Pooling=False");
        connection.Open();
        var logger = new ListLogger<CampaignDatabase>();

        var result = new CampaignDbMigrator(logger).Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal("memory", result.JournalMode);
        Assert.Equal(CampaignDbMigrator.LatestVersion, result.ToVersion);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(_db.DatabasePath, warning);
        Assert.Contains("WAL", warning);
        Assert.Contains("\"memory\"", warning);
    }

    /// <summary>What every later server start does: nothing, not even a backup.</summary>
    [Fact]
    public void Migrate_AnotherInstanceOnAMigratedFile_DoesNothing()
    {
        _db.Database.EnsureReady();
        using var other = _db.OtherProcess();
        using var connection = other.OpenRead();

        var result = new CampaignDbMigrator().Migrate(connection, other.Backups, _db.DatabasePath);

        Assert.Empty(result.Applied);
        Assert.Empty(result.Backups);
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
        Assert.Empty(other.Backups.List());
    }

    /// <summary>
    /// Two server processes start together; both read user_version 0; the other one migrates in the window between this
    /// one's read and its BEGIN IMMEDIATE. Without the re-read under the lock, this one would run 0001 again and fail on
    /// "table already exists" (or worse, a data migration would run twice).
    /// </summary>
    [Fact]
    public void Migrate_AnotherProcessMigratesBeforeBegin_ReReadsUnderTheLockAndStops()
    {
        using var connection = OpenRaw();
        using var otherConnection = OpenRaw();
        var migrator = new CampaignDbMigrator();
        var raced = false;
        migrator.BeforeBegin = version =>
        {
            if (!raced)
            {
                raced = true;
                new CampaignDbMigrator().Migrate(otherConnection, _db.Database.Backups, _db.DatabasePath);
            }
        };

        var result = migrator.Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.True(raced);
        Assert.Empty(result.Applied);
        Assert.Equal(CampaignDbMigrator.LatestVersion, result.ToVersion);
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public void Migrate_DatabaseNewerThanThisBuild_IsRefusedAndTheFileIsUntouched()
    {
        _db.Database.EnsureReady();
        using (var connection = _db.Open())
        {
            connection.Execute($"PRAGMA user_version = {CampaignDbMigrator.LatestVersion + 1}");
            connection.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        }

        var before = Hash(_db.DatabasePath);
        using var other = _db.OtherProcess();

        var error = Assert.Throws<CampaignStoreUnavailableException>(other.EnsureReady);

        Assert.Contains("newer version of dnd-mcp", error.Message);
        Assert.Contains(_db.DatabasePath, error.Message);
        Assert.Contains("Nothing was changed", error.Message);
        Assert.DoesNotContain("PRAGMA", error.Message);
        Assert.Equal(before, Hash(_db.DatabasePath));
        Assert.Empty(other.Backups.List());
    }

    [Fact]
    public void Migrate_PendingMigrationOverExistingData_TakesAPreMigrateBackupFirst()
    {
        var campaign = SeedCampaignAtVersion1();
        using var connection = _db.Open();

        var result = new CampaignDbMigrator([CampaignDbMigrator.Embedded[0], AddNotesTable]).Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(new[] { 2 }, result.Applied);
        var backup = Assert.Single(result.Backups);
        Assert.Matches(@"^campaigns-\d{8}T\d{9}Z-\d+-pre-migrate-v2\.db$", Path.GetFileName(backup));
        Assert.StartsWith("campaigns-20260901T120000000Z-", Path.GetFileName(backup), StringComparison.Ordinal);
        using var copy = OpenReadOnly(backup);
        Assert.Equal(1, CampaignDbMigrator.UserVersion(copy, null));
        Assert.Equal(campaign.Slug, copy.ExecuteScalar<string>("SELECT slug FROM campaign"));
        Assert.Equal(0, copy.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'extra_note'"));
        Assert.Equal(2, CampaignDbMigrator.UserVersion(connection, null));
    }

    /// <summary>A brand-new file has nothing to lose: several migrations in a row take no backup at all.</summary>
    [Fact]
    public void Migrate_FreshDatabaseWithSeveralPendingMigrations_TakesNoBackup()
    {
        using var connection = OpenRaw();

        var result = new CampaignDbMigrator([CampaignDbMigrator.Embedded[0], AddNotesTable]).Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(new[] { 1, 2 }, result.Applied);
        Assert.Empty(result.Backups);
        Assert.Equal(2, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
    }

    /// <summary>
    /// A migration that fails halfway: the transaction takes the half with it (user_version and schema_migrations
    /// included), the data is as it was, and the backup taken before stays, named in the message.
    /// </summary>
    [Fact]
    public void Migrate_FailingMigration_LeavesVersionAndDataUnchangedAndKeepsTheBackup()
    {
        var campaign = SeedCampaignAtVersion1();
        var failing = new CampaignMigration(2, "0002_broken",
            "CREATE TABLE half_done (x INTEGER) STRICT; UPDATE campaign SET name = 'renamed'; INSERT INTO no_such_table VALUES (1);");
        using var connection = _db.Open();

        var error = Assert.Throws<CampaignStoreUnavailableException>(() =>
            new CampaignDbMigrator([CampaignDbMigrator.Embedded[0], failing]).Migrate(connection, _db.Database.Backups, _db.DatabasePath));

        Assert.Equal(1, CampaignDbMigrator.UserVersion(connection, null));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'half_done'"));
        Assert.Equal("Test Campaign", connection.ExecuteScalar<string>("SELECT name FROM campaign WHERE id = @id", new { id = campaign.Id }));
        var backup = Assert.Single(_db.Database.Backups.List());
        Assert.Equal("pre-migrate-v2", backup.Reason);
        Assert.Contains(backup.Path, error.Message);
        Assert.Contains("rolled back", error.Message);
        Assert.DoesNotContain("no_such_table", error.Message);
        Assert.IsType<SqliteException>(error.InnerException);
    }

    /// <summary>No copy, no update: a migration never runs without its backup.</summary>
    [Fact]
    public void Migrate_BackupCannotBeWritten_StopsBeforeMigrating()
    {
        SeedCampaignAtVersion1();
        File.WriteAllText(_db.BackupsPath, "a file where the backups directory should be");
        using var connection = _db.Open();

        var error = Assert.Throws<CampaignStoreUnavailableException>(() =>
            new CampaignDbMigrator([CampaignDbMigrator.Embedded[0], AddNotesTable]).Migrate(connection, _db.Database.Backups, _db.DatabasePath));

        Assert.Contains("nothing was changed", error.Message);
        Assert.Equal(1, CampaignDbMigrator.UserVersion(connection, null));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'extra_note'"));
    }

    /// <summary>
    /// A table rebuild (SQLite's 12-step ALTER): with foreign keys on, DROP TABLE runs an implicit DELETE that cascades
    /// through every child table, so rebuilding campaign would take every entity with it. The directive turns keys off
    /// before BEGIN (inside a transaction the pragma is ignored), so the children survive; keys are back on afterwards.
    /// </summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    public void Migrate_CampaignTableRebuild_KeepsChildRowsOnlyWithTheForeignKeysOffDirective(bool directive, long entitiesAfter)
    {
        var campaign = SeedCampaignAtVersion1();
        using (var seed = _db.Open())
        {
            new CampaignSeed(seed).Entity(campaign.Id, CampaignValues.Kinds.Character, "Iron Guts");
        }

        var rebuild = new CampaignMigration(2, "0002_rebuild_campaign",
            (directive ? CampaignMigration.ForeignKeysOffDirective + "\n" : string.Empty) +
            """
            CREATE TABLE campaign_new (id TEXT PRIMARY KEY, slug TEXT NOT NULL UNIQUE, name TEXT NOT NULL, role TEXT NOT NULL,
              ruleset TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'active', dm_name TEXT,
              my_character_id TEXT REFERENCES entity(id) ON DELETE SET NULL, party_id TEXT REFERENCES entity(id) ON DELETE SET NULL,
              current_location_id TEXT REFERENCES entity(id) ON DELETE SET NULL, current_ingame TEXT,
              settings TEXT NOT NULL DEFAULT '{}', summary_md TEXT NOT NULL DEFAULT '', created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL) STRICT;
            INSERT INTO campaign_new SELECT id, slug, name, role, ruleset, status, dm_name, my_character_id, party_id,
              current_location_id, current_ingame, settings, summary_md, created_at, updated_at FROM campaign;
            DROP TABLE campaign;
            ALTER TABLE campaign_new RENAME TO campaign;
            """);
        using var connection = _db.Open();

        new CampaignDbMigrator([CampaignDbMigrator.Embedded[0], rebuild]).Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(entitiesAfter, connection.ExecuteScalar<long>("SELECT count(*) FROM entity"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM campaign"));
        Assert.Equal(1, connection.ExecuteScalar<long>("PRAGMA foreign_keys"));
        Assert.Empty(connection.Query("PRAGMA foreign_key_check"));
    }

    /// <summary>With keys off nothing stops a migration leaving dangling references, so foreign_key_check runs before commit.</summary>
    [Fact]
    public void Migrate_ForeignKeysOffMigrationLeavingDanglingKeys_IsRolledBack()
    {
        var campaign = SeedCampaignAtVersion1();
        using (var seed = _db.Open())
        {
            var entity = new CampaignSeed(seed).Entity(campaign.Id, CampaignValues.Kinds.Character, "Iron Guts");
            new CampaignSeed(seed).Tag(campaign.Id, entity.Id, "villain");
        }

        var dangling = new CampaignMigration(2, "0002_dangling", CampaignMigration.ForeignKeysOffDirective + "\nDELETE FROM tag;");
        using var connection = _db.Open();

        Assert.Throws<CampaignStoreUnavailableException>(() =>
            new CampaignDbMigrator([CampaignDbMigrator.Embedded[0], dangling]).Migrate(connection, _db.Database.Backups, _db.DatabasePath));

        Assert.Equal(1, CampaignDbMigrator.UserVersion(connection, null));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM tag"));
        Assert.Equal(1, connection.ExecuteScalar<long>("PRAGMA foreign_keys"));
    }

    /// <summary>
    /// DML-time foreign key errors ("no such table", "foreign key mismatch") are invisible at CREATE time: every table
    /// takes an insert and a delete after migration, and foreign_key_check is clean.
    /// </summary>
    [Fact]
    public void Migrate_AfterMigration_EveryTableTakesAnInsertAndADelete()
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        var campaign = seed.Campaign();
        var pc = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc");
        var other = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Serif");
        var session = seed.Session(campaign.Id, 1);
        var clock = seed.Entity(campaign.Id, CampaignValues.Kinds.Clock, "Doom");
        var quest = seed.Entity(campaign.Id, CampaignValues.Kinds.Quest, "Find the old king");
        var beatA = seed.Entity(campaign.Id, CampaignValues.Kinds.Beat, "Arrive");
        var beatB = seed.Entity(campaign.Id, CampaignValues.Kinds.Beat, "Leave");
        var fact = seed.Fact(campaign.Id, "The old king sleeps.");
        var fact2 = seed.Fact(campaign.Id, "The old king wakes.");
        seed.Alias(pc.Id, "Bel");
        seed.Tag(campaign.Id, pc.Id, "bard");
        seed.Relation(campaign.Id, pc.Id, "ally_of", other.Id);
        seed.CrossLink(pc.Id, other.Id);
        seed.FactLink(fact.Id, pc.Id);
        seed.FactDependency(fact2.Id, fact.Id);
        seed.Knowledge(campaign.Id, CampaignValues.KnowerKinds.Character, pc.Id, factId: fact.Id, learnedSessionId: session.EntityId);
        seed.Attendance(session.EntityId, pc.Id);
        seed.Clock(clock.Id, 6);
        seed.Objective(quest.Id, "Climb the tower");
        seed.BeatEdge(campaign.Id, beatA.Id, beatB.Id);
        seed.DiceRoll(campaign.Id, session.EntityId);
        connection.Execute("INSERT INTO app_state(key, value) VALUES ('active_campaign', @id)", new { id = campaign.Id });
        connection.Execute(
            "INSERT INTO change_log(campaign_id, at, actor, batch_id, action, op, target_table, target_id) " +
            "VALUES (@id, '2026-09-01T12:00:00.000Z', 'claude', 'b1', 'upsert', 'create', 'entity', @id)", new { id = campaign.Id });

        foreach (var table in ExpectedTables.Where(t => !t.EndsWith("_fts", StringComparison.Ordinal)))
        {
            Assert.True(connection.ExecuteScalar<long>($"SELECT count(*) FROM {table}") > 0, $"{table} has no smoke row");
        }

        Assert.Empty(connection.Query("PRAGMA foreign_key_check"));
        foreach (var table in new[]
                 {
                     "dice_roll", "beat_edge", "objective", "clock", "session_attendance", "knowledge", "fact_dependency",
                     "fact_link", "cross_link", "relation", "entity_tag", "tag", "entity_alias", "session", "fact", "app_state",
                 })
        {
            connection.Execute($"DELETE FROM {table}");
        }

        connection.Execute("UPDATE campaign SET party_id = NULL");
        connection.Execute("DELETE FROM entity");
        connection.Execute("DELETE FROM campaign");
        Assert.Empty(connection.Query("PRAGMA foreign_key_check"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM entity_fts"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM change_log"));
    }

    /// <summary>
    /// change_log must never gain a UNIQUE index: INSERT OR REPLACE on that key would rewrite a history row past every
    /// append-only trigger (understand-sqlite.md (f)6).
    /// </summary>
    [Fact]
    public void Migrate_ChangeLog_HasNoUniqueIndex()
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();

        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM pragma_index_list('change_log') WHERE \"unique\" = 1"));
    }

    public static TheoryData<string, string, string[]> CheckLists() => new()
    {
        { "campaign", "role", Values(CampaignValues.Roles.Set) },
        { "campaign", "ruleset", Values(CampaignValues.Rulesets.Set) },
        { "campaign", "status", Values(CampaignValues.CampaignStatuses.Set) },
        { "entity", "kind", Values(CampaignValues.Kinds.Set) },
        { "entity", "visibility", Values(CampaignValues.Visibilities.Set) },
        { "entity", "canon_status", Values(CampaignValues.CanonStatuses.Set) },
        { "entity", "confidence", Values(CampaignValues.Confidences.Set) },
        { "entity_alias", "visibility", Values(CampaignValues.Visibilities.Set) },
        { "relation", "visibility", Values(CampaignValues.Visibilities.RowSet) },
        { "relation", "status", Values(CampaignValues.RelationStatuses.Set) },
        { "fact", "fact_type", Values(CampaignValues.FactTypes.Set) },
        { "fact", "truth", Values(CampaignValues.Truths.Set) },
        { "fact", "canon_status", Values(CampaignValues.CanonStatuses.Set) },
        { "fact", "confidence", Values(CampaignValues.Confidences.Set) },
        { "fact", "visibility", Values(CampaignValues.Visibilities.Set) },
        { "fact_link", "role", Values(CampaignValues.FactLinkRoles.Set) },
        { "knowledge", "knower_kind", Values(CampaignValues.KnowerKinds.Set) },
        { "knowledge", "state", Values(CampaignValues.KnowledgeStates.Set) },
        { "session", "status", Values(CampaignValues.SessionStatuses.Set) },
        { "session", "played_on_precision", Values(CampaignValues.DatePrecisions.Set) },
        { "objective", "status", Values(CampaignValues.ObjectiveStatuses.Set) },
        { "objective", "visibility", Values(CampaignValues.Visibilities.RowSet) },
        { "clock", "unit", Values(CampaignValues.ClockUnits.Set) },
        { "beat_edge", "mode", Values(CampaignValues.BeatEdgeModes.Set) },
        {
            "change_log", "op",
            [CampaignValues.ChangeOps.Create, CampaignValues.ChangeOps.Update, CampaignValues.ChangeOps.Delete]
        },
    };

    /// <summary>
    /// The stored vocabularies and the schema's CHECK lists are one list in two places: a value the code accepts but the
    /// CHECK refuses fails every write of it, and the reverse leaves rows the code cannot read.
    /// </summary>
    [Theory]
    [MemberData(nameof(CheckLists))]
    public void Migration_CheckList_EqualsTheCampaignValuesSet(string table, string column, string[] expected)
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        var sql = connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table", new { table })!;

        var match = Regex.Match(sql, $@"\b{column}\s+IN\s*\(([^)]*)\)");

        Assert.True(match.Success, $"{table}.{column} has no IN (…) CHECK");
        var listed = Regex.Matches(match.Groups[1].Value, "'([^']*)'").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Embedded_Migrations_AreNumberedFromOneWithTheInitScriptFirst()
    {
        Assert.Equal(1, CampaignDbMigrator.LatestVersion);
        var first = CampaignDbMigrator.Embedded[0];
        Assert.Equal(1, first.Version);
        Assert.Equal("0001_init", first.Name);
        Assert.Contains("CREATE TABLE change_log", first.Sql);
        Assert.False(first.ForeignKeysOff);
    }

    /// <summary>A gap means a migration file fell out of the build; applying the next one to the wrong schema corrupts data.</summary>
    [Fact]
    public void Constructor_MigrationsWithAGap_Throw()
    {
        var third = new CampaignMigration(3, "0003_later", "SELECT 1;");

        var error = Assert.Throws<InvalidOperationException>(() => new CampaignDbMigrator([CampaignDbMigrator.Embedded[0], third]));

        Assert.Contains("no gap", error.Message);
    }

    [Theory]
    [InlineData("-- dnd-mcp: foreign_keys=off\nCREATE TABLE x (a INTEGER);", true)]
    [InlineData("-- DND-MCP: FOREIGN_KEYS=OFF  \r\nSELECT 1;", true)]
    [InlineData("﻿-- dnd-mcp: foreign_keys=off\nSELECT 1;", true)]
    [InlineData("SELECT 1;\n-- dnd-mcp: foreign_keys=off", false)]
    [InlineData("-- dnd-mcp: foreign_keys=on\nSELECT 1;", false)]
    public void ForeignKeysOff_Directive_IsReadFromTheFirstLineOnly(string sql, bool expected) =>
        Assert.Equal(expected, new CampaignMigration(2, "0002_x", sql).ForeignKeysOff);

    private static string[] Values(DslValueSet set) => set.Values.ToArray();

    private static string[] Tables(SqliteConnection connection) =>
        connection.Query<string>(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '%_fts_%' ORDER BY name")
            .ToArray();

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _db.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        connection.Execute("PRAGMA busy_timeout = 5000");
        return connection;
    }

    private SeededCampaign SeedCampaignAtVersion1()
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        return new CampaignSeed(connection).Campaign();
    }
}
