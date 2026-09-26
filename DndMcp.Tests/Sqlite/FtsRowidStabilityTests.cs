using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: FTS rows stay linked to their source rows through deletes, VACUUM and VACUUM INTO when the
/// source table has an explicit <c>INTEGER PRIMARY KEY</c> used as the FTS rowid (PLAN.md §6 principle 8,
/// <c>entity.seq</c> and <c>fact.seq</c>).
///
/// <para>
/// A regular FTS5 table stores its own copy of every rowid, and nothing re-syncs them. If the source table is
/// renumbered, every search hit joins to the wrong entity (or to none) with no error. What renumbers implicit
/// rowids is a plain <c>VACUUM</c>: run by hand from the sqlite3 CLI or a DB browser, or by any future
/// compaction step. A table rebuild during a migration can do it too, if it copies rows without their rowid.
/// The design's own backup and pre-migration path, <c>VACUUM INTO</c>, happens to keep implicit rowids today,
/// and so does a plain VACUUM of a table with any index (research's entity and fact have <c>id TEXT UNIQUE</c>).
/// Both are undocumented details of SQLite's copy strategy. Only an explicit INTEGER PRIMARY KEY is documented
/// to survive every one of these, so correctness must not depend on which operation runs.
/// </para>
/// </summary>
public sealed class FtsRowidStabilityTests
{
    /// <summary>
    /// PLAN.md §6 principle 8. Five notes are indexed, the first three are deleted (leaving a gap), and the
    /// database is either vacuumed in place or backed up with VACUUM INTO (PLAN.md §6 Operations), with the
    /// copy checked. Every surviving FTS row must still join to the note with the same name, including in a
    /// backup that is later restored.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vacuum_ExplicitIntegerPrimaryKeyAsFtsRowid_KeepsEveryLinkIntact(bool intoBackup)
    {
        using var scratch = new SqliteScratch();
        using var live = scratch.Open("campaigns.db");
        live.Execute("""
            CREATE TABLE note(seq INTEGER PRIMARY KEY, name TEXT NOT NULL, body TEXT NOT NULL) STRICT;
            CREATE VIRTUAL TABLE note_fts USING fts5(name, body, tokenize = 'porter unicode61 remove_diacritics 2');
            CREATE TRIGGER note_fts_ai AFTER INSERT ON note BEGIN
              INSERT INTO note_fts(rowid, name, body) VALUES (new.seq, new.name, new.body); END;
            CREATE TRIGGER note_fts_ad AFTER DELETE ON note BEGIN
              DELETE FROM note_fts WHERE rowid = old.seq; END;
            """);

        SeedAndDelete(live, "note");
        if (intoBackup)
        {
            live.Execute("VACUUM INTO @path", new { path = scratch.PathOf("backup.db") });
        }
        else
        {
            live.Execute("VACUUM");
        }

        using var backup = intoBackup ? scratch.Open("backup.db") : null;
        var db = backup ?? live;
        Assert.Equal(2, db.ExecuteScalar<long>("SELECT count(*) FROM note_fts"));
        Assert.Equal(0, CountBrokenLinks(db, "note", "seq"));
        Assert.Equal("fifth", db.ExecuteScalar<string>(
            "SELECT n.name FROM note_fts JOIN note n ON n.seq = note_fts.rowid WHERE note_fts MATCH 'fifth'"));
    }

    /// <summary>
    /// The counterfactual that proves the test above can fail. The same scenario on a table WITHOUT an
    /// explicit key and without any index, then a plain VACUUM, renumbers the survivors from 4 and 5 down to
    /// 1 and 2, and both FTS links break. (VACUUM INTO, or any index on the table, happens to keep the old
    /// rowids in current SQLite. The class summary explains why the design does not lean on that.)
    /// </summary>
    [Fact]
    public void Vacuum_ImplicitRowidAsFtsRowid_RenumbersAndBreaksLinks()
    {
        using var scratch = new SqliteScratch();
        using var db = scratch.Open("campaigns.db");
        db.Execute("""
            CREATE TABLE loose_note(name TEXT NOT NULL, body TEXT NOT NULL) STRICT;
            CREATE VIRTUAL TABLE loose_note_fts USING fts5(name, body, tokenize = 'porter unicode61 remove_diacritics 2');
            CREATE TRIGGER loose_note_fts_ai AFTER INSERT ON loose_note BEGIN
              INSERT INTO loose_note_fts(rowid, name, body) VALUES (new.rowid, new.name, new.body); END;
            CREATE TRIGGER loose_note_fts_ad AFTER DELETE ON loose_note BEGIN
              DELETE FROM loose_note_fts WHERE rowid = old.rowid; END;
            """);

        SeedAndDelete(db, "loose_note");
        db.Execute("VACUUM");

        Assert.Equal(new long[] { 1, 2 }, db.Query<long>("SELECT rowid FROM loose_note ORDER BY rowid"));
        Assert.Equal(new long[] { 4, 5 }, db.Query<long>("SELECT rowid FROM loose_note_fts ORDER BY rowid"));
        Assert.Equal(2, CountBrokenLinks(db, "loose_note", "rowid"));
    }

    private static void SeedAndDelete(SqliteConnection db, string table)
    {
        foreach (var name in new[] { "first", "second", "third", "fourth", "fifth" })
        {
            db.Execute($"INSERT INTO {table}(name, body) VALUES (@name, 'a note')", new { name });
        }

        db.Execute($"DELETE FROM {table} WHERE name IN ('first', 'second', 'third')");
    }

    /// <summary>FTS rows whose rowid no longer points at a source row with the same name.</summary>
    private static long CountBrokenLinks(SqliteConnection db, string table, string key) =>
        db.ExecuteScalar<long>($"""
            SELECT count(*) FROM {table}_fts f
            LEFT JOIN {table} t ON t.{key} = f.rowid
            WHERE t.name IS NULL OR t.name <> f.name
            """);
}
