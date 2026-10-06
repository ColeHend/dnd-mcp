using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Features;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;
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
        "app_state", "award", "beat_edge", "campaign", "change_log", "character_sheet", "clock", "combat_log", "combatant",
        "cross_link", "currency_txn", "dice_roll", "encounter", "entity", "entity_alias", "entity_fts", "entity_tag", "fact",
        "fact_dependency", "fact_fts", "fact_link", "holding", "knowledge", "objective", "relation", "schema_migrations",
        "session", "session_attendance", "tag",
    ];

    // Test migrations come after every embedded one: numbered from LatestVersion so these tests keep testing a PENDING
    // migration over a file the build has fully migrated, however many migrations the build embeds.
    private static readonly int Next = CampaignDbMigrator.LatestVersion + 1;

    private static readonly CampaignMigration AddNotesTable =
        Extra("notes", "CREATE TABLE extra_note (id TEXT PRIMARY KEY, body TEXT NOT NULL) STRICT;");

    private readonly CampaignTestDb _db = new(create: false);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Migrate_FreshDatabase_CreatesEveryTableAtTheLatestVersionWithoutABackup()
    {
        using var connection = OpenRaw();

        var result = new CampaignDbMigrator().Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(0, result.FromVersion);
        Assert.Equal(CampaignDbMigrator.LatestVersion, result.ToVersion);
        Assert.Equal(new[] { 1, 2 }, result.Applied);
        Assert.Empty(result.Backups);
        Assert.Equal("wal", result.JournalMode);
        Assert.Equal(ExpectedTables, Tables(connection));
        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
        Assert.Equal(
            new[] { (1L, "0001_init"), (2L, "0002_characters_combat") },
            connection.Query<(long, string)>("SELECT version, name FROM schema_migrations ORDER BY version").ToList());
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
        Assert.Equal(CampaignDbMigrator.LatestVersion, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
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
        Assert.Equal(CampaignDbMigrator.LatestVersion, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
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
        var campaign = SeedCampaignAtTheLatestVersion();
        using var connection = _db.Open();

        var result = With(AddNotesTable).Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(new[] { Next }, result.Applied);
        var backup = Assert.Single(result.Backups);
        Assert.Matches($@"^campaigns-\d{{8}}T\d{{9}}Z-\d+-pre-migrate-v{Next}\.db$", Path.GetFileName(backup));
        Assert.StartsWith("campaigns-20260901T120000000Z-", Path.GetFileName(backup), StringComparison.Ordinal);
        using var copy = OpenReadOnly(backup);
        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(copy, null));
        Assert.Equal(campaign.Slug, copy.ExecuteScalar<string>("SELECT slug FROM campaign"));
        Assert.Equal(0, copy.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'extra_note'"));
        Assert.Equal(Next, CampaignDbMigrator.UserVersion(connection, null));
    }

    /// <summary>
    /// R02: while another process holds the version-1 file's write lock past busy_timeout, every campaign call tries the
    /// migration again, and each attempt fails after its backup. Each attempt copies the file, and a copy byte-identical
    /// to the newest pre-migrate-v2 backup is deleted (F2, review RR01), so three refused attempts and the migration that
    /// finally goes through leave ONE backup of the unchanged file; a file that changed in between gets a new one (below).
    /// </summary>
    [Fact]
    public void Migrate_Version1FileHeldLockedByAnotherProcess_ThreeRefusedAttemptsAndTheMigrationTakeOneBackup()
    {
        Phase6File();
        var backups = _db.Database.Backups;
        using (var holder = OpenRaw())
        {
            using var held = holder.BeginTransaction(deferred: false);
            holder.Execute("UPDATE campaign SET name = name", transaction: held);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var connection = Impatient();
                Assert.Throws<SqliteException>(() => new CampaignDbMigrator().Migrate(connection, backups, _db.DatabasePath));
                Assert.Single(backups.List());
            }

            held.Rollback();
        }

        _db.Database.EnsureReady();

        var backup = Assert.Single(backups.List());
        Assert.Equal(CampaignBackups.PreMigrateReason(2), backup.Reason);
        using var copy = OpenReadOnly(backup.Path);
        Assert.Equal(1, CampaignDbMigrator.UserVersion(copy, null));
        using var migrated = OpenRaw();
        Assert.Equal(2, CampaignDbMigrator.UserVersion(migrated, null));
    }

    /// <summary>
    /// R02, RR01: a file that changed between two refused attempts gets a new backup, of what the migration then changes,
    /// whatever the write was: a roll logged (the file's counters move), or a row changed in place (an UPDATE, or a 0.6.0
    /// logged write that adds no entity, fact or roll: change_log.seq is no AUTOINCREMENT key), which neither the file's size
    /// nor its counters show, so F1's fingerprint reused the first attempt's backup and the only pre-migrate-v2 backup
    /// missed the write. Every attempt now takes its copy and keeps it unless it is byte-identical to the newest one.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO dice_roll (id, campaign_id, expression, label, total, detail, secret, at) " +
                "SELECT 'late-roll', id, '1d20', 'after the first attempt', 12, '{}', 0, '2026-09-01T12:00:00.000Z' FROM campaign LIMIT 1",
                "SELECT count(*) FROM dice_roll WHERE id = 'late-roll'")]
    [InlineData("UPDATE entity SET summary = 'Changed between the attempts.' WHERE slug = 'belmakor'",
                "SELECT count(*) FROM entity WHERE summary = 'Changed between the attempts.'")]
    [InlineData("UPDATE campaign SET name = 'Renamed between the attempts' WHERE slug = 'belmakor'",
                "SELECT count(*) FROM campaign WHERE name = 'Renamed between the attempts'")]
    public void Migrate_Version1FileChangedBetweenTwoRefusedAttempts_TheNewestBackupHoldsTheChange(string write, string check)
    {
        Phase6File();
        var backups = _db.Database.Backups;

        RefusedWhileHeld();
        using (var writer = OpenRaw())
        {
            Assert.Equal(1, writer.Execute(write));
        }

        RefusedWhileHeld();
        _db.Database.EnsureReady();

        var taken = backups.List().OrderBy(b => b.At).ToList();
        Assert.Equal(2, taken.Count);
        using var first = OpenReadOnly(taken[0].Path);
        using var latest = OpenReadOnly(taken[^1].Path);
        Assert.Equal((0L, 1L), (first.ExecuteScalar<long>(check), latest.ExecuteScalar<long>(check)));
    }

    // One migration attempt refused because another connection holds the write lock (BEGIN IMMEDIATE) past busy_timeout.
    private void RefusedWhileHeld()
    {
        using var holder = OpenRaw();
        using var held = holder.BeginTransaction(deferred: false);
        using var connection = Impatient();
        Assert.Throws<SqliteException>(() => new CampaignDbMigrator().Migrate(connection, _db.Database.Backups, _db.DatabasePath));
        held.Rollback();
    }

    // A connection that gives up on a held lock at once (no busy wait, the shortest retry bound), for refused attempts.
    private SqliteConnection Impatient()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _db.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        connection.Execute("PRAGMA busy_timeout = 0");
        return connection;
    }

    /// <summary>A brand-new file has nothing to lose: several migrations in a row take no backup at all.</summary>
    [Fact]
    public void Migrate_FreshDatabaseWithSeveralPendingMigrations_TakesNoBackup()
    {
        using var connection = OpenRaw();

        var result = With(AddNotesTable).Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(Enumerable.Range(1, Next), result.Applied);
        Assert.Empty(result.Backups);
        Assert.Equal(Next, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
    }

    /// <summary>
    /// A migration that fails halfway: the transaction takes the half with it (user_version and schema_migrations
    /// included), the data is as it was, and the backup taken before stays, named in the message.
    /// </summary>
    [Fact]
    public void Migrate_FailingMigration_LeavesVersionAndDataUnchangedAndKeepsTheBackup()
    {
        var campaign = SeedCampaignAtTheLatestVersion();
        var failing = Extra("broken",
            "CREATE TABLE half_done (x INTEGER) STRICT; UPDATE campaign SET name = 'renamed'; INSERT INTO no_such_table VALUES (1);");
        using var connection = _db.Open();

        var error = Assert.Throws<CampaignStoreUnavailableException>(() =>
            With(failing).Migrate(connection, _db.Database.Backups, _db.DatabasePath));

        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
        Assert.Equal(CampaignDbMigrator.LatestVersion, connection.ExecuteScalar<long>("SELECT count(*) FROM schema_migrations"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'half_done'"));
        Assert.Equal("Test Campaign", connection.ExecuteScalar<string>("SELECT name FROM campaign WHERE id = @id", new { id = campaign.Id }));
        var backup = Assert.Single(_db.Database.Backups.List());
        Assert.Equal(CampaignBackups.PreMigrateReason(Next), backup.Reason);
        Assert.Contains(backup.Path, error.Message);
        Assert.Contains("rolled back", error.Message);
        Assert.DoesNotContain("no_such_table", error.Message);
        Assert.IsType<SqliteException>(error.InnerException);
    }

    /// <summary>No copy, no update: a migration never runs without its backup.</summary>
    [Fact]
    public void Migrate_BackupCannotBeWritten_StopsBeforeMigrating()
    {
        SeedCampaignAtTheLatestVersion();
        File.WriteAllText(_db.BackupsPath, "a file where the backups directory should be");
        using var connection = _db.Open();

        var error = Assert.Throws<CampaignStoreUnavailableException>(() =>
            With(AddNotesTable).Migrate(connection, _db.Database.Backups, _db.DatabasePath));

        Assert.Contains("nothing was changed", error.Message);
        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
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
        var campaign = SeedCampaignAtTheLatestVersion();
        using (var seed = _db.Open())
        {
            new CampaignSeed(seed).Entity(campaign.Id, CampaignValues.Kinds.Character, "Iron Guts");
        }

        var rebuild = Extra("rebuild_campaign",
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

        With(rebuild).Migrate(connection, _db.Database.Backups, _db.DatabasePath);

        Assert.Equal(entitiesAfter, connection.ExecuteScalar<long>("SELECT count(*) FROM entity"));
        Assert.Equal(1, connection.ExecuteScalar<long>("SELECT count(*) FROM campaign"));
        Assert.Equal(1, connection.ExecuteScalar<long>("PRAGMA foreign_keys"));
        Assert.Empty(connection.Query("PRAGMA foreign_key_check"));
    }

    /// <summary>With keys off nothing stops a migration leaving dangling references, so foreign_key_check runs before commit.</summary>
    [Fact]
    public void Migrate_ForeignKeysOffMigrationLeavingDanglingKeys_IsRolledBack()
    {
        var campaign = SeedCampaignAtTheLatestVersion();
        using (var seed = _db.Open())
        {
            var entity = new CampaignSeed(seed).Entity(campaign.Id, CampaignValues.Kinds.Character, "Iron Guts");
            new CampaignSeed(seed).Tag(campaign.Id, entity.Id, "villain");
        }

        var dangling = Extra("dangling", CampaignMigration.ForeignKeysOffDirective + "\nDELETE FROM tag;");
        using var connection = _db.Open();

        Assert.Throws<CampaignStoreUnavailableException>(() =>
            With(dangling).Migrate(connection, _db.Database.Backups, _db.DatabasePath));

        Assert.Equal(CampaignDbMigrator.LatestVersion, CampaignDbMigrator.UserVersion(connection, null));
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
        seed.CharacterSheet(pc.Id, level: 12, maxHp: 98, hp: 98);
        seed.Holding(campaign.Id, pc.Id, "Potion of healing", itemId: other.Id, acquiredSessionId: session.EntityId);
        seed.CurrencyTxn(campaign.Id, pc.Id, gp: 50, sessionId: session.EntityId);
        seed.Award(campaign.Id, pc.Id, amount: 450, sessionId: session.EntityId);
        var encounter = seed.Encounter(campaign.Id, status: CampaignValues.EncounterStatuses.Active, sessionId: session.EntityId, sceneId: beatA.Id);
        var combatant = seed.Combatant(encounter, "Belmakor", CampaignValues.CombatSides.Party, entityId: pc.Id, sheetSnapshot: "{}");
        var roll = CampaignDatabase.NewId();
        seed.DiceRoll(campaign.Id, session.EntityId, id: roll, encounterId: encounter);
        seed.CombatLog(encounter, CampaignValues.CombatLogKinds.Damage, actorId: combatant, targetId: combatant, rollId: roll);
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
                     "combat_log", "combatant", "dice_roll", "encounter", "award", "currency_txn", "holding", "character_sheet",
                     "beat_edge", "objective", "clock", "session_attendance", "knowledge", "fact_dependency", "fact_link",
                     "cross_link", "relation", "entity_tag", "tag", "entity_alias", "session", "fact", "app_state",
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
    /// The upgrade every 0.6.0 user gets: their version-1 file, full of Phase 6 history, is migrated by the first campaign
    /// call (<see cref="CampaignDatabase.EnsureReady"/>, the production path) to version 2. A pre-migrate-v2 backup holding
    /// the version-1 data comes first; afterwards every row of every Phase 6 table (and both FTS indexes, and the
    /// AUTOINCREMENT counters) is exactly as it was, dice_roll has gained encounter_id last and NULL, foreign keys and the
    /// file's integrity check are clean, and the Phase 7 tables exist, empty.
    /// </summary>
    [Fact]
    public void Migrate_Version1FileWithPhase6Data_KeepsEveryRowBehindAPreMigrateV2Backup()
    {
        var world = Phase6File();
        IReadOnlyDictionary<string, IReadOnlyList<string>> v1;
        string before;
        using (var raw = OpenRaw())
        {
            Assert.Equal(1, CampaignDbMigrator.UserVersion(raw, null));
            v1 = Version1File.Tables(raw);
            before = Version1File.Dump(raw, v1);
        }

        Assert.Contains("dice_roll", v1.Keys);
        Assert.DoesNotContain("encounter_id", v1["dice_roll"]);
        Assert.True(world.Rolls >= 2 && world.Batches > 20, $"{world.Rolls} rolls, {world.Batches} batches: not much of a Phase 6 file");

        _db.Database.EnsureReady();

        using var connection = _db.Open();
        Assert.Equal(2, CampaignDbMigrator.UserVersion(connection, null));
        Assert.Equal(
            new[] { (1L, "0001_init"), (2L, "0002_characters_combat") },
            connection.Query<(long, string)>("SELECT version, name FROM schema_migrations ORDER BY version").ToList());
        Assert.Equal(before, Version1File.Dump(connection, v1));
        Assert.Equal(ExpectedTables, Tables(connection));
        Assert.Equal(v1["dice_roll"].Append("encounter_id"), Version1File.Tables(connection)["dice_roll"]);
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM dice_roll WHERE encounter_id IS NOT NULL"));
        Assert.Empty(connection.Query("PRAGMA foreign_key_check"));
        Assert.Equal("ok", connection.ExecuteScalar<string>("PRAGMA integrity_check"));
        foreach (var table in new[] { "character_sheet", "holding", "currency_txn", "award", "encounter", "combatant", "combat_log" })
        {
            Assert.Equal(0, connection.ExecuteScalar<long>($"SELECT count(*) FROM {table}"));
        }

        var backup = Assert.Single(_db.Database.Backups.List());
        Assert.Equal(CampaignBackups.PreMigrateReason(2), backup.Reason);
        using var copy = OpenReadOnly(backup.Path);
        Assert.Equal(1, CampaignDbMigrator.UserVersion(copy, null));
        Assert.Equal(before, Version1File.Dump(copy, v1));
        Assert.Equal(0, copy.ExecuteScalar<long>("SELECT count(*) FROM sqlite_master WHERE name = 'character_sheet'"));
    }

    /// <summary>
    /// Search still works on a migrated file: what was indexed is found, and 0001's FTS triggers (which 0002 must not have
    /// dropped: it rebuilds no table) still re-index an entity on a rename and on a new alias.
    /// </summary>
    [Fact]
    public void Migrate_Version1FileWithPhase6Data_KeepsSearchWorking()
    {
        var world = Phase6File();
        string[] triggers;
        using (var raw = OpenRaw())
        {
            triggers = Triggers(raw);
        }

        _db.Database.EnsureReady();

        using (var connection = _db.Open())
        {
            Assert.Equal(triggers, Triggers(connection));
        }

        Assert.Equal(15, triggers.Length);
        Assert.Contains(world.BelmakorSeq, Hits("silverwind"));
        Assert.Equal(new[] { world.LateArrivalSeq }, Hits("\"late arrival\""));
        _db.Batch(world.CampaignId, r =>
        {
            r.Update("entity", world.LateArrivalId, new Dictionary<string, object?> { ["name"] = "Captain Ondine" }, "upsert");
            r.Insert("entity_alias", new Dictionary<string, object?> { ["entity_id"] = world.BelmakorId, ["alias"] = "Bladesong Bard", ["visibility"] = "party" }, "upsert");
        });

        Assert.Equal(new[] { world.LateArrivalSeq }, Hits("ondine"));
        Assert.Empty(Hits("\"late arrival\""));
        Assert.Equal(new[] { world.BelmakorSeq }, Hits("\"bladesong bard\""));
    }

    /// <summary>
    /// History carries across the upgrade: a batch made before it is undone after it (the late batch, and the FTS row it
    /// made goes with it), a point-in-time read replays a pre-migration change (f:6 was restricted until session 2), and
    /// the new logged tables take a batch and an undo on the old file like on a new one.
    /// </summary>
    [Fact]
    public void Migrate_Version1FileWithPhase6Data_KeepsHistoryUndoableAndReplayable()
    {
        var world = Phase6File();
        _db.Database.EnsureReady();

        _db.Undo(world.CampaignId, world.LateBatchId);

        using (var connection = _db.Open())
        {
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM entity WHERE id = @id", new { id = world.LateArrivalId }));
            Assert.Equal("restricted", ChangeReplay.RowAsOf(connection, "fact", world.F6Id, 1)!["visibility"]);
            Assert.Equal("party", ChangeReplay.RowAsOf(connection, "fact", world.F6Id, 2)!["visibility"]);
        }

        Assert.Empty(Hits("\"late arrival\""));
        var sheet = _db.Batch(world.CampaignId, r => r.Insert("character_sheet", new Dictionary<string, object?>
        {
            ["entity_id"] = world.BelmakorId, ["ruleset"] = "2014", ["level"] = 12, ["max_hp"] = 98, ["hp"] = 98,
            ["classes"] = new JsonArray(new JsonObject { ["class"] = "wizard", ["subclass"] = "bladesinger", ["level"] = 12 }),
        }, "update"));
        using (var connection = _db.Open())
        {
            Assert.Equal(98L, connection.ExecuteScalar<long>("SELECT hp FROM character_sheet WHERE entity_id = @id", new { id = world.BelmakorId }));
        }

        _db.Undo(world.CampaignId, sheet);

        using (var connection = _db.Open())
        {
            Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM character_sheet"));
        }
    }

    /// <summary>
    /// The foreign-key actions 0002 declares, each exercised (an action that is only declared fails at DML time, as
    /// "no such table" or a mismatch, never at CREATE): deleting an entity takes its sheet, holdings, coins and awards with
    /// it but only unlinks an item, a combatant, an encounter's scene and session, and the session of coins and awards;
    /// deleting a combatant unlinks the log rows naming it (actor or target); deleting a roll unlinks the log row that
    /// cites it; deleting an encounter takes its combatants and log and unlinks its rolls; deleting a campaign takes all of
    /// it. Every SET NULL is checked as "the row is still there, with the column NULL": a CASCADE in its place would
    /// silently delete logged holdings, a live fight's combatants, or dice rolls (which nothing may un-roll), and reading
    /// the column alone cannot tell a NULL from a row that is gone.
    /// </summary>
    [Fact]
    public void Migrate_Phase7ForeignKeys_ActAsDeclared()
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        var campaign = seed.Campaign();
        var pc = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc");
        var npc = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Iron Guts", subtype: "npc");
        var sword = seed.Entity(campaign.Id, CampaignValues.Kinds.Item, "Flame tongue");
        var scene = seed.Entity(campaign.Id, CampaignValues.Kinds.Scene, "The crypt");
        var session = seed.Session(campaign.Id, 1);
        seed.CharacterSheet(pc.Id, level: 12);
        var held = seed.Holding(campaign.Id, npc.Id, "Flame tongue", itemId: sword.Id);
        var heldByPc = seed.Holding(campaign.Id, pc.Id, "Rope", acquiredSessionId: session.EntityId);
        var coins = seed.CurrencyTxn(campaign.Id, pc.Id, gp: 5, sessionId: session.EntityId);
        var award = seed.Award(campaign.Id, pc.Id, amount: 10, sessionId: session.EntityId);
        var encounter = seed.Encounter(campaign.Id, status: CampaignValues.EncounterStatuses.Active, sessionId: session.EntityId, sceneId: scene.Id);
        var guts = seed.Combatant(encounter, "Iron Guts", entityId: npc.Id, hp: 10, maxHp: 10);
        var bel = seed.Combatant(encounter, "Belmakor", CampaignValues.CombatSides.Party, entityId: pc.Id, sheetSnapshot: "{}");
        var mummy = seed.Combatant(encounter, "Mummy");
        var roll = CampaignDatabase.NewId();
        seed.DiceRoll(campaign.Id, session.EntityId, id: roll, encounterId: encounter);
        var laterRoll = CampaignDatabase.NewId();
        seed.DiceRoll(campaign.Id, null, id: laterRoll, encounterId: encounter);
        var log = seed.CombatLog(encounter, CampaignValues.CombatLogKinds.Damage, actorId: bel, targetId: guts, rollId: roll);
        seed.CombatLog(encounter, CampaignValues.CombatLogKinds.Damage, actorId: mummy, targetId: mummy, rollId: laterRoll);

        connection.Execute("DELETE FROM entity WHERE id = @id", new { id = sword.Id });
        Assert.Equal(1, Count(connection, "holding WHERE id = @id AND item_id IS NULL", held));
        connection.Execute("DELETE FROM entity WHERE id = @id", new { id = npc.Id });
        Assert.Equal(1, Count(connection, "combatant WHERE id = @id AND entity_id IS NULL", guts));
        Assert.Equal(0, Count(connection, "holding WHERE id = @id", held));
        connection.Execute("DELETE FROM entity WHERE id IN (@scene, @session)", new { scene = scene.Id, session = session.EntityId });
        Assert.Equal(1, Count(connection, "encounter WHERE id = @id AND scene_id IS NULL AND session_id IS NULL", encounter));
        Assert.Equal(1, Count(connection, "holding WHERE id = @id AND acquired_session_id IS NULL", heldByPc));
        Assert.Equal(1, Count(connection, "currency_txn WHERE id = @id AND session_id IS NULL", coins));
        Assert.Equal(1, Count(connection, "award WHERE id = @id AND session_id IS NULL", award));
        connection.Execute("DELETE FROM combatant WHERE id = @bel", new { bel });
        Assert.Equal(1, Count(connection, "combat_log WHERE seq = @id AND actor_id IS NULL AND target_id IS NOT NULL", log));
        connection.Execute("DELETE FROM combatant WHERE id = @guts", new { guts });
        Assert.Equal(1, Count(connection, "combat_log WHERE seq = @id AND target_id IS NULL AND roll_id IS NOT NULL", log));
        connection.Execute("DELETE FROM dice_roll WHERE id = @roll", new { roll });
        Assert.Equal(1, Count(connection, "combat_log WHERE seq = @id AND roll_id IS NULL", log));
        connection.Execute("DELETE FROM entity WHERE id = @id", new { id = pc.Id });
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM character_sheet"));
        Assert.Equal(0, Count(connection, "holding WHERE id = @id", heldByPc));
        Assert.Equal(0, Count(connection, "currency_txn WHERE id = @id", coins));
        Assert.Equal(0, Count(connection, "award WHERE id = @id", award));
        connection.Execute("DELETE FROM encounter WHERE id = @encounter", new { encounter });
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM combatant"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM combat_log"));
        Assert.Equal(1, Count(connection, "dice_roll WHERE id = @id AND encounter_id IS NULL", laterRoll));
        var second = seed.Encounter(campaign.Id);
        seed.Combatant(second, "Mummy");
        connection.Execute("UPDATE campaign SET party_id = NULL");
        connection.Execute("DELETE FROM entity");
        connection.Execute("DELETE FROM campaign");
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM encounter"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM combatant"));
        Assert.Equal(0, connection.ExecuteScalar<long>("SELECT count(*) FROM dice_roll"));
        Assert.Empty(connection.Query("PRAGMA foreign_key_check"));
    }

    /// <summary>
    /// One active encounter per campaign is the partial unique index's job (D13: the combat service maps its constraint
    /// error to its own refusal): a second active one in the same campaign is refused, while any number of planned,
    /// paused and ended ones, and an active one in another campaign, are not.
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.EncounterStatuses.Active, true, true)]
    [InlineData(CampaignValues.EncounterStatuses.Active, false, false)]
    [InlineData(CampaignValues.EncounterStatuses.Planned, true, false)]
    [InlineData(CampaignValues.EncounterStatuses.Paused, true, false)]
    [InlineData(CampaignValues.EncounterStatuses.Ended, true, false)]
    public void Migrate_EncounterActiveIndex_AllowsOneActiveEncounterPerCampaign(string secondStatus, bool sameCampaign, bool refused)
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        var campaign = seed.Campaign();
        var other = seed.Campaign(name: "Other");
        seed.Encounter(campaign.Id, "The crypt", status: CampaignValues.EncounterStatuses.Active);
        seed.Encounter(campaign.Id, "Old fight", status: CampaignValues.EncounterStatuses.Ended);

        var second = Record.Exception(() => seed.Encounter(sameCampaign ? campaign.Id : other.Id, "The dark station", status: secondStatus));

        if (refused)
        {
            var error = Assert.IsType<SqliteException>(second);
            Assert.Equal(19, error.SqliteErrorCode);
            Assert.Contains("encounter.campaign_id", error.Message);
        }
        else
        {
            Assert.Null(second);
        }
    }

    /// <summary>
    /// The constraints of 0002 that no vocabulary list covers refuse what the C# rules must never write: a level outside
    /// 1-20, negative XP, HP, quantity, reduction, damage taken or round, an exhaustion past 6, JSON of the wrong kind, a
    /// combatant above its maximum HP, a NULL where a column is required, and a key that names no row (the foreign keys the
    /// combat log and the dice log rely on). They are the store's backstop under the sheet and tracker rules.
    /// </summary>
    [Theory]
    [InlineData("UPDATE character_sheet SET level = 21")]
    [InlineData("UPDATE character_sheet SET level = 0")]
    [InlineData("UPDATE character_sheet SET xp = -1")]
    [InlineData("UPDATE character_sheet SET hp = -1")]
    [InlineData("UPDATE character_sheet SET temp_hp = -1")]
    [InlineData("UPDATE character_sheet SET ac = -1")]
    [InlineData("UPDATE character_sheet SET max_hp = 5001")]
    [InlineData("UPDATE character_sheet SET death_saves = '[]'")]
    [InlineData("UPDATE character_sheet SET classes = NULL")]
    [InlineData("UPDATE character_sheet SET ac = 51")]
    [InlineData("UPDATE character_sheet SET max_hp = 0")]
    [InlineData("UPDATE character_sheet SET max_hp_reduction = -1")]
    [InlineData("UPDATE character_sheet SET exhaustion = 7")]
    [InlineData("UPDATE character_sheet SET inspiration = 2")]
    [InlineData("UPDATE character_sheet SET classes = '{}'")]
    [InlineData("UPDATE character_sheet SET spell_slots = '[]'")]
    [InlineData("UPDATE character_sheet SET concentration = 'not json'")]
    [InlineData("UPDATE character_sheet SET ruleset = 'mixed'")]
    [InlineData("UPDATE holding SET quantity = -0.5")]
    [InlineData("UPDATE holding SET quantity = 'two'")]
    [InlineData("UPDATE award SET kind = 'gold'")]
    [InlineData("UPDATE encounter SET status = 'won'")]
    [InlineData("UPDATE encounter SET ruleset = 'mixed'")]
    [InlineData("UPDATE combatant SET hp = 11")]
    [InlineData("UPDATE combatant SET side = 'monster'")]
    [InlineData("UPDATE combatant SET exhaustion = 7")]
    [InlineData("UPDATE combatant SET conditions = '{}'")]
    [InlineData("UPDATE combat_log SET kind = 'attack'")]
    [InlineData("UPDATE combat_log SET round = -1")]
    [InlineData("UPDATE combat_log SET detail = '[]'")]
    [InlineData("UPDATE combat_log SET roll_id = 'no such roll'")]
    [InlineData("UPDATE combat_log SET encounter_id = 'no such encounter'")]
    [InlineData("UPDATE combat_log SET actor_id = 'no such combatant'")]
    [InlineData("UPDATE combatant SET damage_taken = -1")]
    [InlineData("UPDATE combatant SET temp_hp = -1")]
    [InlineData("UPDATE combatant SET max_hp = 0")]
    [InlineData("UPDATE combatant SET statblock = '[]'")]
    [InlineData("UPDATE combatant SET sheet_snapshot = '[]'")]
    [InlineData("UPDATE combatant SET resources = '[]'")]
    [InlineData("UPDATE combatant SET name = NULL")]
    [InlineData("UPDATE combatant SET order_key = NULL")]
    [InlineData("UPDATE combatant SET entity_id = 'no such entity'")]
    [InlineData("UPDATE encounter SET round = -1")]
    [InlineData("UPDATE encounter SET ruleset = NULL")]
    [InlineData("UPDATE encounter SET lair = 2")]
    [InlineData("UPDATE encounter SET data = '[]'")]
    [InlineData("UPDATE holding SET name = NULL")]
    [InlineData("UPDATE holding SET item_id = 'no such entity'")]
    [InlineData("UPDATE dice_roll SET encounter_id = 'no such encounter'")]
    public void Migrate_Phase7Checks_RefuseWhatTheRulesMustNeverWrite(string update)
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        var campaign = seed.Campaign();
        var pc = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc");
        seed.CharacterSheet(pc.Id, level: 12, maxHp: 98, hp: 98);
        seed.Holding(campaign.Id, pc.Id, "Rope");
        seed.Award(campaign.Id, pc.Id, amount: 1);
        var encounter = seed.Encounter(campaign.Id);
        seed.Combatant(encounter, "Mummy", hp: 10, maxHp: 10);
        seed.CombatLog(encounter);
        seed.DiceRoll(campaign.Id, null, encounterId: encounter);

        var error = Assert.Throws<SqliteException>(() => connection.Execute(update));

        Assert.Equal(19, error.SqliteErrorCode);
    }

    public static TheoryData<string, string, object> Phase7Defaults() => new()
    {
        { "character_sheet", "death_saves", "{\"successes\":0,\"failures\":0,\"stable\":false}" },
        { "character_sheet", "classes", "[]" },
        { "character_sheet", "abilities", "{}" },
        { "character_sheet", "spell_slots", "{}" },
        { "character_sheet", "conditions", "[]" },
        { "character_sheet", "max_hp_reduction", 0L },
        { "character_sheet", "temp_hp", 0L },
        { "character_sheet", "exhaustion", 0L },
        { "character_sheet", "inspiration", 0L },
        { "character_sheet", "notes_md", "" },
        { "holding", "quantity", 1.0 },
        { "holding", "equipped", 0L },
        { "holding", "attuned", 0L },
        { "currency_txn", "gp", 0L },
        { "encounter", "status", "planned" },
        { "encounter", "round", 0L },
        { "encounter", "lair", 0L },
        { "encounter", "data", "{}" },
        { "encounter", "notes_md", "" },
        { "combatant", "death_saves", "{\"successes\":0,\"failures\":0,\"stable\":false}" },
        { "combatant", "conditions", "[]" },
        { "combatant", "resources", "{}" },
        { "combatant", "init_bonus", 0L },
        { "combatant", "damage_taken", 0L },
        { "combatant", "makes_death_saves", 0L },
        { "combatant", "removed", 0L },
    };

    /// <summary>
    /// What a row gets for a column its insert leaves out (contract §3/§4): a new sheet is alive with no death-save tallies
    /// (an empty object would read as no state at all), a holding holds one, a new encounter is planned at round 0 (a
    /// default of active would take the campaign's one active slot), and every count and flag starts at 0. The sheet store
    /// and the tracker insert only what they know and rely on these.
    /// </summary>
    [Theory]
    [MemberData(nameof(Phase7Defaults))]
    public void Migrate_Phase7Defaults_FillWhatAnInsertLeavesOut(string table, string column, object expected)
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        var seed = new CampaignSeed(connection);
        var campaign = seed.Campaign();
        var pc = seed.Entity(campaign.Id, CampaignValues.Kinds.Character, "Belmakor Silverwind", subtype: "pc");
        var encounter = seed.Encounter(campaign.Id, "Old fight");
        var at = new { campaign = campaign.Id, pc = pc.Id, encounter, at = "2026-09-01T12:00:00.000Z" };
        connection.Execute(table switch
        {
            "character_sheet" => "INSERT INTO character_sheet(entity_id, created_at, updated_at) VALUES (@pc, @at, @at)",
            "holding" => "INSERT INTO holding(id, campaign_id, holder_id, name, created_at, updated_at) VALUES ('new', @campaign, @pc, 'Rope', @at, @at)",
            "currency_txn" => "INSERT INTO currency_txn(id, campaign_id, holder_id, note, created_at) VALUES ('new', @campaign, @pc, 'loot', @at)",
            "encounter" => "INSERT INTO encounter(id, campaign_id, name, ruleset, created_at, updated_at) VALUES ('new', @campaign, 'The crypt', '2024', @at, @at)",
            _ => "INSERT INTO combatant(id, encounter_id, name, side, order_key, created_at, updated_at) VALUES ('new', @encounter, 'Mummy', 'enemy', 1, @at, @at)",
        }, at);

        var key = table == "character_sheet" ? "entity_id = @pc" : "id = 'new'";
        var stored = connection.ExecuteScalar<object>($"SELECT {column} FROM {table} WHERE {key}", at);

        Assert.Equal(expected, stored);
    }

    /// <summary>
    /// The schema 0002 leaves, object by object as SQLite stores it (sqlite_master, whitespace runs collapsed), is exactly
    /// what contract §3's frozen text gives when run on a version-1 file: every column, type, NOT NULL, DEFAULT, CHECK,
    /// foreign key and action, every index and its WHERE, and the column dice_roll gains. Behaviour tests cover the
    /// constraints the rules lean on; this covers the rest, which an edit could otherwise change with every test green and
    /// ship to every user's file. A difference fails naming the object.
    /// </summary>
    [Fact]
    public void Embedded_CharactersCombatMigration_CreatesExactlyTheFrozenSchema()
    {
        var migrated = SchemaAfter(CampaignDbMigrator.Embedded, then: null);
        var frozen = SchemaAfter([CampaignDbMigrator.Embedded[0]], then: FrozenSchema.CharactersCombat);

        Assert.Equal(frozen, migrated);
        Assert.Contains(migrated, o => o.StartsWith("table combatant: CREATE TABLE combatant (", StringComparison.Ordinal));
        Assert.Contains(migrated, o => o.StartsWith("table dice_roll: ", StringComparison.Ordinal) &&
            o.EndsWith(", encounter_id TEXT REFERENCES encounter(id) ON DELETE SET NULL) STRICT", StringComparison.Ordinal));
    }

    public static TheoryData<string, string> FrozenMigrations() => new()
    {
        { "0001_init", "A6F4E4E7120C3FA3583AA6A0681A31EB533BC303EC311B9306F736EE54A840AF" },
        { "0002_characters_combat", "6B6611C52DA95FCC31C158EAF8D50C8EE7E234C1A6A0B969DE81F6886190CEED" },
    };

    /// <summary>
    /// A migration that has shipped (0001 in 0.6.0) or been frozen by its contract (0002, Phase 7 §3) is never edited, not
    /// even a comment: every file already migrated by it keeps the old text's schema, so an edit would split users at the
    /// same user_version. The SHA-256 of each embedded file (line endings as checked out, LF) is pinned; a schema change is
    /// a new migration. Never update a hash here to follow an edit.
    /// </summary>
    [Theory]
    [MemberData(nameof(FrozenMigrations))]
    public void Embedded_FrozenMigration_IsNeverEdited(string name, string sha256)
    {
        var migration = Assert.Single(CampaignDbMigrator.Embedded, m => m.Name == name);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(migration.Sql.ReplaceLineEndings("\n"))));

        Assert.Equal(sha256, hash);
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
        { "character_sheet", "ruleset", Values(CampaignValues.Rulesets.Editions) },
        { "award", "kind", Values(CampaignValues.AwardKinds.Set) },
        { "encounter", "ruleset", Values(CampaignValues.Rulesets.Editions) },
        { "encounter", "status", Values(CampaignValues.EncounterStatuses.Set) },
        { "combatant", "side", Values(CampaignValues.CombatSides.Set) },
        { "combat_log", "kind", Values(CampaignValues.CombatLogKinds.Set) },
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
        Assert.Equal(2, CampaignDbMigrator.LatestVersion);
        var first = CampaignDbMigrator.Embedded[0];
        Assert.Equal(1, first.Version);
        Assert.Equal("0001_init", first.Name);
        Assert.Contains("CREATE TABLE change_log", first.Sql);
        Assert.False(first.ForeignKeysOff);
    }

    /// <summary>
    /// 0002 only creates tables and appends one column: no Phase 6 table is rebuilt (a rebuild of entity would drop its
    /// FTS triggers), so it needs no foreign_keys=off directive, and every statement is a CREATE or the dice_roll ALTER.
    /// </summary>
    [Fact]
    public void Embedded_CharactersCombatMigration_IsVersionTwoAndOnlyAddsTablesAndOneColumn()
    {
        var second = CampaignDbMigrator.Embedded[1];

        Assert.Equal((2, "0002_characters_combat"), (second.Version, second.Name));
        Assert.False(second.ForeignKeysOff);
        var statements = Regex.Matches(second.Sql, @"(?m)^(CREATE|ALTER|DROP|INSERT|UPDATE|DELETE|PRAGMA)\b[^\n]*").Select(m => m.Value).ToList();
        Assert.All(statements, st => Assert.True(
            st.StartsWith("CREATE TABLE ", StringComparison.Ordinal) || st.StartsWith("CREATE INDEX ", StringComparison.Ordinal) ||
            st.StartsWith("CREATE UNIQUE INDEX ", StringComparison.Ordinal) ||
            st == "ALTER TABLE dice_roll ADD COLUMN encounter_id TEXT REFERENCES encounter(id) ON DELETE SET NULL;", st));
        Assert.Equal(7, statements.Count(st => st.StartsWith("CREATE TABLE ", StringComparison.Ordinal)));
    }

    /// <summary>A gap means a migration file fell out of the build; applying the next one to the wrong schema corrupts data.</summary>
    [Fact]
    public void Constructor_MigrationsWithAGap_Throw()
    {
        var afterAGap = new CampaignMigration(Next + 1, "9999_later", "SELECT 1;");

        var error = Assert.Throws<InvalidOperationException>(() => new CampaignDbMigrator([.. CampaignDbMigrator.Embedded, afterAGap]));

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

    private static long Count(SqliteConnection connection, string fromWhere, object id) =>
        connection.ExecuteScalar<long>($"SELECT count(*) FROM {fromWhere}", new { id });

    // A fresh in-memory file migrated by the given migrations, then (optionally) running more SQL by hand: its
    // sqlite_master as "type name: sql" lines with whitespace runs collapsed, in a stable order.
    private List<string> SchemaAfter(IReadOnlyList<CampaignMigration> migrations, string? then)
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True;Pooling=False");
        connection.Open();
        new CampaignDbMigrator(migrations).Migrate(connection, _db.Database.Backups, _db.DatabasePath);
        if (then is not null)
        {
            connection.Execute(then);
        }

        return connection.Query<(string Type, string Name, string? Sql)>("SELECT type, name, sql FROM sqlite_master ORDER BY type, name")
            .Select(o => $"{o.Type} {o.Name}: {Regex.Replace(o.Sql ?? "(none)", @"\s+", " ").Trim()}")
            .ToList();
    }

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

    // The test database's file as a version-1 campaigns.db holding the Phase 6 world below, written through the Phase 6
    // write path into a scratch database and copied into a real 0001 schema (Version1File).
    private Phase6World Phase6File()
    {
        using var source = new WriteFixture();
        var world = BuildPhase6World(source);
        Version1File.CopyFrom(source.Db.DatabasePath, _db.DatabasePath, _db.Database.Backups);
        return world;
    }

    // The Belmakor fixture (both campaigns, the cross-links, three played sessions, facts with a supersession and a
    // visibility change in session 2, knowledge), plus through the same services: a clock ticked, a quest objective, a
    // story-web edge, tags, a live session with a note and two logged rolls (one secret), a batch undone (undo_of rows),
    // and one last batch, made in the live session, for the history test to undo after the upgrade.
    private static Phase6World BuildPhase6World(WriteFixture f)
    {
        var fixture = BelmakorFixture.Build(f);
        var c = fixture.Belmakor;
        f.Apply(c,
            new CampaignOpSpec { Op = "upsert", Kind = "clock", Name = "Kraken hunger", Clock = new ClockSpec { Segments = 4 } },
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Salvage the mithril", Visibility = "party", Tags = ["salvage", "sky"] },
            new CampaignOpSpec { Op = "objective", Ref = "quest:salvage-the-mithril", Text = "Find the wreck", Progress = 1, ProgressMax = 3 },
            new CampaignOpSpec { Op = "upsert", Kind = "beat", Name = "Dock fight" },
            new CampaignOpSpec { Op = "upsert", Kind = "beat", Name = "Blood moon" },
            new CampaignOpSpec { Op = "link", From = "beat:dock-fight", Rel = "leads_to", To = "beat:blood-moon", Mode = "any_of" });
        f.Sessions.Start(c);
        f.Sessions.Log(c, ["The Silver Gull docks."]);
        f.Apply(c, new CampaignOpSpec { Op = "tick", Ref = "clock:kraken-hunger" });
        Assert.True(f.Dice.TryLog(c.Id, [new DiceLogRoll("1d20+5", "Perception", 17, null, "{\"v\":1}")], secret: false).Logged);
        Assert.True(f.Dice.TryLog(c.Id, [new DiceLogRoll("1d20", "Behind the screen", 3, null, "{\"v\":1}")], secret: true).Logged);
        var undone = f.Apply(c, new CampaignOpSpec { Op = "upsert", Ref = "character:belmakor", Summary = "A passing summary." }).BatchId!;
        f.History.Undo(c, undone, WriteContext.Default);
        var late = f.Apply(c, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Late Arrival", Subtype = "npc", Visibility = "party" });
        var belmakor = f.Entity(c, "character:belmakor");
        var lateArrival = f.Entity(c, "character:late-arrival");
        return new Phase6World(
            c.Id,
            belmakor.Id,
            belmakor.Seq,
            lateArrival.Id,
            lateArrival.Seq,
            late.BatchId!,
            f.Fact(c, "f:6").Id,
            f.Count("SELECT count(*) FROM dice_roll"),
            f.Count("SELECT count(DISTINCT batch_id) FROM change_log"));
    }

    // Every trigger, name and text: 0002 creates none and must drop none (a dropped FTS trigger silently stops search
    // from seeing new text; change_log's append-only triggers are what keeps history history).
    private static string[] Triggers(SqliteConnection connection) =>
        connection.Query<(string Name, string Sql)>("SELECT name, sql FROM sqlite_master WHERE type = 'trigger' ORDER BY name")
            .Select(t => t.Name + ": " + t.Sql).ToArray();

    private long[] Hits(string match)
    {
        using var connection = _db.Open();
        return connection.Query<long>("SELECT rowid FROM entity_fts WHERE entity_fts MATCH @match ORDER BY rowid", new { match }).ToArray();
    }

    private SeededCampaign SeedCampaignAtTheLatestVersion()
    {
        _db.Database.EnsureReady();
        using var connection = _db.Open();
        return new CampaignSeed(connection).Campaign();
    }

    // A test migration numbered after every embedded one ("0003_notes" while 0002 is the newest).
    private static CampaignMigration Extra(string name, string sql) =>
        new(Next, Next.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + "_" + name, sql);

    // The build's migrations plus one test migration.
    private static CampaignDbMigrator With(CampaignMigration extra) => new([.. CampaignDbMigrator.Embedded, extra]);
}

/// <summary>What the version-1 tests need to know about the Phase 6 world they migrate.</summary>
internal sealed record Phase6World(
    string CampaignId,
    string BelmakorId,
    long BelmakorSeq,
    string LateArrivalId,
    long LateArrivalSeq,
    string LateBatchId,
    string F6Id,
    long Rolls,
    long Batches);
