using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: once a <c>change_log</c> row is written, no statement can change or remove it (PLAN.md §6
/// principle 7, "Triggers make it immutable"; Verification "change_log rejects UPDATE and DELETE"). History is
/// what makes <c>campaign_history</c>, <c>as_of_session</c> replay and undo trustworthy. A rewritten row is
/// worse than a missing one, because nothing shows it was rewritten.
///
/// <para>
/// The research DDL's two triggers (BEFORE UPDATE and BEFORE DELETE, each RAISE(ABORT)) block UPDATE,
/// DELETE and UPSERT. They do NOT block <c>INSERT OR REPLACE</c> / <c>REPLACE</c> with an existing
/// <c>seq</c>. REPLACE deletes the old row without firing delete triggers unless
/// <c>PRAGMA recursive_triggers</c> is on, and it is off by default. This class pins that hole and both ways
/// to close it.
/// </para>
/// </summary>
public sealed class AppendOnlyTriggerTests : IDisposable
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintTrigger = 1811;

    private const string ResearchDdl = """
        CREATE TABLE change_log (
          seq INTEGER PRIMARY KEY,
          campaign_id TEXT NOT NULL,
          at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
          actor TEXT NOT NULL,
          batch_id TEXT NOT NULL,
          op TEXT NOT NULL,
          target_table TEXT NOT NULL,
          target_id TEXT NOT NULL,
          old_value TEXT,
          new_value TEXT,
          reason TEXT
        ) STRICT;
        CREATE TRIGGER change_log_no_update BEFORE UPDATE ON change_log BEGIN SELECT RAISE(ABORT,'change_log is append-only'); END;
        CREATE TRIGGER change_log_no_delete BEFORE DELETE ON change_log BEGIN SELECT RAISE(ABORT,'change_log is append-only'); END;
        """;

    /// <summary>The schema-level fix: refuse any insert that names an existing seq, whatever the pragma.</summary>
    private const string ReplaceGuardDdl = """
        CREATE TRIGGER change_log_no_replace BEFORE INSERT ON change_log
        WHEN EXISTS (SELECT 1 FROM change_log WHERE seq = NEW.seq)
        BEGIN SELECT RAISE(ABORT,'change_log is append-only'); END;
        """;

    private readonly SqliteConnection _db;

    public AppendOnlyTriggerTests()
    {
        _db = SqliteScratch.OpenInMemory();
        _db.Execute(ResearchDdl);
        Append("died protecting Sky");
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// PLAN.md §6 principle 7. Each statement is rejected with SQLITE_CONSTRAINT_TRIGGER and the trigger's
    /// message, and the row is unchanged afterwards. UPSERT counts because DO UPDATE fires the update trigger.
    /// </summary>
    [Theory]
    [InlineData("UPDATE change_log SET reason = 'rewritten'")]
    [InlineData("DELETE FROM change_log")]
    [InlineData("INSERT INTO change_log(seq, campaign_id, actor, batch_id, op, target_table, target_id, reason) VALUES (1, 'c1', 'claude', 'b2', 'update', 'entity', 'e1', 'rewritten') ON CONFLICT(seq) DO UPDATE SET reason = excluded.reason")]
    public void ResearchTriggers_UpdateDeleteOrUpsert_AreRejectedAndRowSurvives(string sql)
    {
        var exception = Assert.Throws<SqliteException>(() => _db.Execute(sql));

        Assert.Equal(SqliteConstraint, exception.SqliteErrorCode);
        Assert.Equal(SqliteConstraintTrigger, exception.SqliteExtendedErrorCode);
        Assert.Contains("change_log is append-only", exception.Message);
        Assert.Equal(new[] { "died protecting Sky" }, Reasons());
    }

    /// <summary>
    /// THE HOLE. With only the research triggers and default pragmas, INSERT OR REPLACE and REPLACE naming an
    /// existing seq succeed and silently overwrite history. If this starts failing, SQLite changed REPLACE
    /// semantics (or the bundle turned recursive_triggers on by default). Re-check before removing either fix.
    /// </summary>
    [Theory]
    [InlineData("INSERT OR REPLACE")]
    [InlineData("REPLACE")]
    public void ResearchTriggers_ReplaceWithExistingSeq_SilentlyOverwritesHistory(string verb)
    {
        Assert.Equal(0, _db.ExecuteScalar<long>("PRAGMA recursive_triggers"));

        _db.Execute(ReplaceSql(verb));

        Assert.Equal(new[] { "rewritten" }, Reasons());
    }

    /// <summary>
    /// Fix 1: <c>PRAGMA recursive_triggers = ON</c> makes REPLACE's implicit delete fire the delete trigger.
    /// It is per connection, though. dnd-mcp's own connections can get it from the
    /// <c>Recursive Triggers=True</c> connection-string keyword (next test). But the sqlite3 CLI and DB
    /// browsers never see that string, and a connection from any of them reopens the hole. That is why Fix 2
    /// is the recommended one.
    /// </summary>
    [Theory]
    [InlineData("INSERT OR REPLACE")]
    [InlineData("REPLACE")]
    public void RecursiveTriggersOn_ReplaceWithExistingSeq_IsRejected(string verb)
    {
        _db.Execute("PRAGMA recursive_triggers = ON");

        var exception = Assert.Throws<SqliteException>(() => _db.Execute(ReplaceSql(verb)));

        Assert.Equal(SqliteConstraintTrigger, exception.SqliteExtendedErrorCode);
        Assert.Equal(new[] { "died protecting Sky" }, Reasons());
    }

    /// <summary>
    /// Fix 1 without hand-written pragmas: Microsoft.Data.Sqlite's <c>Recursive Triggers=True</c> keyword
    /// sends <c>PRAGMA recursive_triggers = 1</c> at Open, like <c>Foreign Keys=True</c> does for foreign
    /// keys. It is still per connection, so it only covers connections opened with dnd-mcp's connection
    /// string. If a package bump drops the keyword, opening throws on the unknown keyword and this fails.
    /// </summary>
    [Theory]
    [InlineData("INSERT OR REPLACE")]
    [InlineData("REPLACE")]
    public void RecursiveTriggersKeyword_ReplaceWithExistingSeq_IsRejected(string verb)
    {
        using var db = SqliteScratch.OpenInMemory("Recursive Triggers=True");
        db.Execute(ResearchDdl);
        db.Execute("INSERT INTO change_log(campaign_id, actor, batch_id, op, target_table, target_id, reason) VALUES ('c1', 'claude', 'b1', 'update', 'entity', 'e1', 'died protecting Sky')");

        var exception = Assert.Throws<SqliteException>(() => db.Execute(ReplaceSql(verb)));

        Assert.Equal(1, db.ExecuteScalar<long>("PRAGMA recursive_triggers"));
        Assert.Equal(SqliteConstraintTrigger, exception.SqliteExtendedErrorCode);
        Assert.Equal(new[] { "died protecting Sky" }, db.Query<string>("SELECT reason FROM change_log ORDER BY seq"));
    }

    /// <summary>
    /// Fix 2 (recommended, because it lives in the schema and so cannot be forgotten per connection): a
    /// BEFORE INSERT trigger that refuses an existing seq. Ordinary appends, which leave seq to SQLite, still
    /// work.
    /// </summary>
    [Theory]
    [InlineData("INSERT OR REPLACE")]
    [InlineData("REPLACE")]
    public void ReplaceGuardTrigger_ReplaceWithExistingSeq_IsRejectedWhileAppendsStillWork(string verb)
    {
        _db.Execute(ReplaceGuardDdl);

        var exception = Assert.Throws<SqliteException>(() => _db.Execute(ReplaceSql(verb)));
        Append("second entry");

        Assert.Equal(SqliteConstraintTrigger, exception.SqliteExtendedErrorCode);
        Assert.Equal(new[] { "died protecting Sky", "second entry" }, Reasons());
    }

    /// <summary>
    /// RAISE(ABORT) undoes the failing STATEMENT only. The surrounding transaction stays open, and the earlier
    /// writes in it are still pending. PLAN.md makes one tool call one batch and one undo unit, so the batch
    /// writer must roll back on any exception itself. Committing after catching would persist half a batch.
    /// </summary>
    [Fact]
    public void RaiseAbort_InsideTransaction_UndoesOnlyTheFailingStatement()
    {
        using (var transaction = _db.BeginTransaction())
        {
            _db.Execute(
                "INSERT INTO change_log(campaign_id, actor, batch_id, op, target_table, target_id, reason) VALUES ('c1', 'claude', 'b2', 'create', 'entity', 'e2', 'half a batch')",
                transaction: transaction);

            Assert.Throws<SqliteException>(() => _db.Execute("DELETE FROM change_log", transaction: transaction));

            transaction.Commit();
        }

        Assert.Equal(new[] { "died protecting Sky", "half a batch" }, Reasons());
    }

    private static string ReplaceSql(string verb) =>
        $"{verb} INTO change_log(seq, campaign_id, actor, batch_id, op, target_table, target_id, reason) VALUES (1, 'c1', 'claude', 'b2', 'update', 'entity', 'e1', 'rewritten')";

    private void Append(string reason) =>
        _db.Execute(
            "INSERT INTO change_log(campaign_id, actor, batch_id, op, target_table, target_id, reason) VALUES ('c1', 'claude', 'b1', 'update', 'entity', 'e1', @reason)",
            new { reason });

    private string[] Reasons() => _db.Query<string>("SELECT reason FROM change_log ORDER BY seq").ToArray();
}
