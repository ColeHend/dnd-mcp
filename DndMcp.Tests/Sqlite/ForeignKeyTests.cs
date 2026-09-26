using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: campaign connections enforce foreign keys, including <c>ON DELETE CASCADE</c> and references to
/// a UNIQUE non-primary-key column (every <c>REFERENCES entity(id)</c> in research/04 §4, where the primary
/// key is <c>seq</c> and <c>id</c> is only UNIQUE). Without enforcement, deleting a campaign leaves orphaned
/// entities, facts and knowledge rows that still turn up in search.
///
/// <para>
/// Finding: in the bundled e_sqlite3, enforcement is ON even with no connection-string keyword. Stock SQLite
/// defaults it OFF. The design still sets <c>Foreign Keys=True</c> explicitly (research §7), so a different
/// native library cannot quietly turn enforcement off. <c>Foreign Keys=False</c> really does disable it.
/// </para>
/// <para>
/// Only the behaviour proves that default. <c>PRAGMA compile_options</c> lists <c>DEFAULT_FOREIGN_KEYS</c>,
/// but the list records that a macro is defined, not its value. The same bundle also lists
/// <c>DEFAULT_RECURSIVE_TRIGGERS</c>, and recursive triggers are off (AppendOnlyTriggerTests pins that).
/// So <c>sqlite_compileoption_used</c> cannot tell ON from OFF, and a test built on it would stay green on a
/// bundle compiled with the default set to 0.
/// </para>
/// </summary>
public sealed class ForeignKeyTests
{
    private const int SqliteConstraintForeignKey = 787;

    private const string Schema = """
        CREATE TABLE parent (seq INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE) STRICT;
        CREATE TABLE child (id TEXT PRIMARY KEY, parent_id TEXT NOT NULL REFERENCES parent(id) ON DELETE CASCADE) STRICT;
        """;

    /// <summary>
    /// research §7 connection string <c>Foreign Keys=True</c>. The pragma value and the behaviour must agree
    /// for each setting. With no keyword, Microsoft.Data.Sqlite sends no pragma at all (its documented
    /// behaviour for a null ForeignKeys), so the first row reads the native library's own default. That row is
    /// the only thing pinning that this bundle defaults to ON. If a package bump flips it, the row fails. That
    /// is harmless for dnd-mcp, which always sets the keyword, but it signals that the native library changed.
    /// </summary>
    [Theory]
    [InlineData("", 1L, true)]
    [InlineData("Foreign Keys=True", 1L, true)]
    [InlineData("Foreign Keys=False", 0L, false)]
    public void ConnectionString_ForeignKeysKeyword_ControlsEnforcement(string keyword, long pragma, bool enforced)
    {
        using var db = SqliteScratch.OpenInMemory(keyword);
        db.Execute(Schema);

        var exception = Record.Exception(() => db.Execute("INSERT INTO child(id, parent_id) VALUES ('orphan', 'no-such-parent')"));

        Assert.Equal(pragma, db.ExecuteScalar<long>("PRAGMA foreign_keys"));
        if (enforced)
        {
            Assert.Equal(SqliteConstraintForeignKey, Assert.IsType<SqliteException>(exception).SqliteExtendedErrorCode);
        }
        else
        {
            Assert.Null(exception);
        }
    }

    /// <summary>
    /// research §4 "campaign_id … REFERENCES campaign(id) ON DELETE CASCADE". Deleting a parent removes its
    /// children when the reference targets a UNIQUE (not primary-key) column, which is the entity.id shape.
    /// </summary>
    [Fact]
    public void Cascade_ParentReferencedByUniqueNonPrimaryKey_DeletesChildren()
    {
        using var db = SqliteScratch.OpenInMemory("Foreign Keys=True");
        db.Execute(Schema);
        db.Execute("INSERT INTO parent(id) VALUES ('campaign-1')");
        db.Execute("INSERT INTO child(id, parent_id) VALUES ('entity-1', 'campaign-1'), ('entity-2', 'campaign-1')");

        db.Execute("DELETE FROM parent WHERE id = 'campaign-1'");

        Assert.Equal(0, db.ExecuteScalar<long>("SELECT count(*) FROM child"));
    }

    /// <summary>
    /// research §7 migrations: "Table rebuilds follow SQLite's create/copy/drop/rename procedure with
    /// foreign_keys=OFF outside the transaction". The "outside" matters: inside a transaction the pragma is a
    /// silent no-op, so a rebuild that turns it off after BEGIN still cascades deletes when it drops the old
    /// table.
    /// </summary>
    [Fact]
    public void ForeignKeysPragma_InsideTransaction_IsSilentlyIgnored()
    {
        using var db = SqliteScratch.OpenInMemory("Foreign Keys=True");
        using var transaction = db.BeginTransaction();

        db.Execute("PRAGMA foreign_keys = OFF", transaction: transaction);

        Assert.Equal(1, db.ExecuteScalar<long>("PRAGMA foreign_keys", transaction: transaction));
    }
}
