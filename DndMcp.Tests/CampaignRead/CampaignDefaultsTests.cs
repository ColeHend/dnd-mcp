using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignDb;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: the ambient defaults (edition, level offset) are read without ever creating, migrating or writing
/// campaigns.db, from the preferred campaign or the active one only (never "the only campaign"), and a missing or empty
/// file means no defaults. A rules lookup that left a database file behind, or applied the only campaign's ruleset to
/// someone who never chose it, would change behaviour for users who never touched campaigns.
/// </summary>
public sealed class CampaignDefaultsTests
{
    [Fact]
    public void TryRead_NoFile_IsNullAndCreatesNothing()
    {
        using var db = new CampaignTestDb(create: false);

        Assert.Null(CampaignDefaults.TryRead(db.Database, null));
        Assert.Null(CampaignDefaults.TryRead(db.Database, "some-id"));
        Assert.False(File.Exists(db.DatabasePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(db.DirectoryPath));
    }

    [Fact]
    public void TryRead_AnEmptyUnmigratedFile_IsNullAndIsNotMigrated()
    {
        using var db = new CampaignTestDb(create: false);
        File.WriteAllBytes(db.DatabasePath, []);

        Assert.Null(CampaignDefaults.TryRead(db.Database, null));
        using var connection = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
        connection.Open();
        Assert.Equal(0L, connection.ExecuteScalar<long>("PRAGMA user_version"));
        Assert.False(Directory.Exists(db.BackupsPath));
    }

    [Fact]
    public void TryRead_PreferredCampaign_GivesItsSlugRulesetAndOffset()
    {
        using var db = new CampaignTestDb();
        string id;
        using (var connection = db.Open())
        {
            var seed = new CampaignSeed(connection);
            id = seed.Campaign(name: "Belmakor", ruleset: CampaignValues.Rulesets.R2014, settings: "{\"effective_level_offset\":1}").Id;
            seed.Campaign(name: "Other");
        }

        Assert.Equal(new CampaignDefaultValues("belmakor", "2014", 1), CampaignDefaults.TryRead(db.Database, id));
    }

    [Fact]
    public void TryRead_NoPreference_UsesTheActiveCampaignAndNeverTheOnlyOne()
    {
        using var db = new CampaignTestDb();
        string id;
        using (var connection = db.Open())
        {
            id = new CampaignSeed(connection).Campaign(name: "Solo", settings: "{\"effective_level_offset\":42}").Id;
        }

        Assert.Null(CampaignDefaults.TryRead(db.Database, null));
        using (var connection = db.Open())
        {
            connection.Execute("INSERT INTO app_state(key, value) VALUES (@key, @id)", new { key = CampaignDefaults.ActiveCampaignKey, id });
        }

        Assert.Equal(new CampaignDefaultValues("solo", "2024", null), CampaignDefaults.TryRead(db.Database, null));
    }

    [Fact]
    public void TryRead_ActiveCampaignThatNoLongerExists_IsIgnored()
    {
        using var db = new CampaignTestDb();
        using (var connection = db.Open())
        {
            new CampaignSeed(connection).Campaign(name: "Solo");
            connection.Execute("INSERT INTO app_state(key, value) VALUES (@key, 'gone')", new { key = CampaignDefaults.ActiveCampaignKey });
        }

        Assert.Null(CampaignDefaults.TryRead(db.Database, "also-gone"));
    }

    /// <summary>
    /// The level offset is read at both ends of the settings validator's range (review M20): a campaign set to 10 or -10 is
    /// +10 or -10, not "no offset"; one outside the range (hand-edited) is ignored.
    /// </summary>
    [Theory]
    [InlineData(10, 10)]
    [InlineData(-10, -10)]
    [InlineData(11, null)]
    [InlineData(-11, null)]
    [InlineData(0, 0)]
    public void TryRead_LevelOffsetAtAndPastTheLimits_IsReadWithinThemOnly(int stored, int? read)
    {
        using var db = new CampaignTestDb();
        string id;
        using (var connection = db.Open())
        {
            id = new CampaignSeed(connection).Campaign(name: "Hot", settings: $"{{\"effective_level_offset\":{stored}}}").Id;
        }

        Assert.Equal(read, CampaignDefaults.TryRead(db.Database, id)!.EffectiveLevelOffset);
    }

    /// <summary>
    /// The defaults read leaves nothing beside campaigns.db (review R08/C12): a read-only connection to a WAL database makes
    /// campaigns.db-wal and -shm and cannot remove them, so every rules call left both behind; read-write removes them.
    /// </summary>
    [Fact]
    public void TryRead_NothingElseOpen_LeavesNoWalOrShmBesideCampaignsDb()
    {
        using var db = new CampaignTestDb();
        string id;
        using (var connection = db.Open())
        {
            id = new CampaignSeed(connection).Campaign(ruleset: CampaignValues.Rulesets.R2014).Id;
        }

        Assert.False(File.Exists(db.DatabasePath + "-wal"));

        var defaults = CampaignDefaults.TryRead(db.Database, id);

        Assert.Equal("2014", defaults?.Ruleset);
        Assert.False(File.Exists(db.DatabasePath + "-wal"), "campaigns.db-wal left behind");
        Assert.False(File.Exists(db.DatabasePath + "-shm"), "campaigns.db-shm left behind");
    }

    /// <summary>
    /// A campaigns.db written by a newer dnd-mcp is "campaign unreadable", never read (FQ12, the per-use check of review R02):
    /// this build cannot know what a newer schema's tables mean, and "no campaign" would hide that a chosen campaign exists.
    /// </summary>
    [Fact]
    public void TryRead_NewerSchema_IsUnreadableAndReadsNoTable()
    {
        using var db = new CampaignTestDb();
        string id;
        using (var connection = db.Open())
        {
            id = new CampaignSeed(connection).Campaign(name: "Solo").Id;
            connection.Execute($"PRAGMA user_version = {CampaignDbMigrator.LatestVersion + 1}");
        }

        var error = Assert.Throws<CampaignStoreUnavailableException>(() => CampaignDefaults.TryRead(db.Database, id));

        Assert.Contains("written by a newer version of dnd-mcp", error.Message, StringComparison.Ordinal);
        Assert.Contains(db.DatabasePath, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A campaigns.db another process holds locked is "campaign unreadable" within about a second (FQ12): the defaults feed
    /// every rules call, which has a fallback and must not stall for the store's five-second wait. The message is the user's.
    /// </summary>
    [Fact]
    public void TryRead_LockedByAnotherConnection_IsUnreadableQuickly()
    {
        using var db = new CampaignTestDb();
        string id;
        using (var connection = db.Open())
        {
            id = new CampaignSeed(connection).Campaign(name: "Solo").Id;
        }

        using var holder = new SqliteConnection($"Data Source={db.DatabasePath};Pooling=False");
        holder.Open();
        holder.Execute("PRAGMA locking_mode = EXCLUSIVE; BEGIN EXCLUSIVE;");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var error = Assert.Throws<CampaignStoreUnavailableException>(() => CampaignDefaults.TryRead(db.Database, id));

        clock.Stop();
        Assert.Contains("is locked by another dnd-mcp process", error.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"The defaults read waited {clock.Elapsed.TotalSeconds:0.0} s for the lock.");
        holder.Execute("ROLLBACK");
    }

    [Fact]
    public void TryRead_ExistingDatabase_ReadsWithoutWritingTheFile()
    {
        using var db = new CampaignTestDb();
        string id;
        using (var connection = db.Open())
        {
            id = new CampaignSeed(connection).Campaign(name: "Solo").Id;
        }

        var before = File.GetLastWriteTimeUtc(db.DatabasePath);
        var values = CampaignDefaults.TryRead(db.Database, id);

        Assert.Equal("solo", values!.Slug);
        Assert.Equal(before, File.GetLastWriteTimeUtc(db.DatabasePath));
    }
}

/// <summary>
/// Invariant: no reader ever creates campaigns.db. If the file is gone when a read runs (restored or deleted after the
/// host resolved the campaign), the read says so (with no file path, which a caller error never carries, and with a call
/// that works as printed) and leaves no new, empty database behind to answer later calls.
/// </summary>
public sealed class ReadConnectionTests
{
    [Fact]
    public void Readers_DatabaseFileGone_RefuseAndCreateNothing()
    {
        using var db = new CampaignTestDb();
        CampaignRow row;
        using (var connection = db.Open())
        {
            var seed = new CampaignSeed(connection);
            row = seed.LoadCampaign(seed.Campaign(name: "Gone").Id);
        }

        foreach (var file in Directory.GetFiles(db.DirectoryPath, "campaigns.db*"))
        {
            File.Delete(file);
        }

        var calls = new Action[]
        {
            () => new CampaignSearch(db.Database).Search(row, new SearchRequest("x")),
            () => new EntityReader(db.Database).Get(row, ["e:1"]),
            () => new CampaignSummary(db.Database).Build(row),
            () => new SessionReader(db.Database).List(row),
            () => new HistoryReader(db.Database).Since(row),
            () => new KnowledgeLedger(db.Database).KnownTo(row, Perspective.Author),
            () => new KnowledgeCheck(db.Database).Check(row, new CheckRequest("hello")),
        };
        foreach (var call in calls)
        {
            var ex = Assert.Throws<DndInputException>(call);
            // Exactly the refusal: no file path (caller errors carry none), and a call the model can send as printed.
            Assert.Equal("There is no campaigns.db, so there are no campaigns to read. Create one with campaign {\"action\": \"create\"}.",
                ex.Message);
            Assert.DoesNotContain(db.DirectoryPath, ex.Message, StringComparison.Ordinal);
        }

        Assert.False(File.Exists(db.DatabasePath));
    }
}
