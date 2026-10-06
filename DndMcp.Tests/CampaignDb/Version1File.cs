using System.Globalization;
using System.Text;
using Dapper;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.CampaignDb;

/// <summary>
/// A campaigns.db at schema version 1, the file dnd-mcp 0.6.0 writes, holding real Phase 6 data: for the tests that
/// migrate such a file forward (0002 runs on users' existing files, the one moment nobody is watching).
///
/// <para>
/// The schema is made by the real 0001 migration through the real migrator (a migrator given only 0001), so it is the
/// 0.6.0 schema exactly, triggers and FTS tables included. The rows are copied, table by table in 0001's creation order
/// and column by column (0001's columns only), from a database the Phase 6 write path filled; that source is at the
/// build's latest version, which only ever adds tables and appends columns, so 0001's columns hold exactly what 0.6.0
/// would have stored. The copy runs with foreign keys off (campaign and entity reference each other) and checks them
/// after; the FTS indexes are rebuilt by 0001's own triggers as the rows go in (entities before their aliases and tags),
/// and AUTOINCREMENT state follows the copied seqs.
/// </para>
/// </summary>
internal static class Version1File
{
    /// <summary>Makes <paramref name="targetPath"/> a version-1 campaigns.db holding every row of <paramref name="sourcePath"/>'s 0001 tables.</summary>
    /// <param name="backups">Required by the migrator; a new file takes no backup.</param>
    public static void CopyFrom(string sourcePath, string targetPath, CampaignBackups backups)
    {
        using var target = Open(targetPath);
        var result = new CampaignDbMigrator([CampaignDbMigrator.Embedded[0]]).Migrate(target, backups, targetPath);
        if (result.ToVersion != 1 || result.Backups.Count != 0)
        {
            throw new InvalidOperationException($"{targetPath} was not a new file: it migrated to {result.ToVersion}.");
        }

        var tables = Tables(target);
        target.Execute("PRAGMA foreign_keys = OFF");
        target.Execute("ATTACH DATABASE @sourcePath AS src", new { sourcePath });
        using (var transaction = target.BeginTransaction())
        {
            foreach (var (table, columns) in tables)
            {
                var list = string.Join(", ", columns);
                target.Execute($"INSERT INTO main.{table} ({list}) SELECT {list} FROM src.{table}", transaction: transaction);
            }

            transaction.Commit();
        }

        target.Execute("DETACH DATABASE src");
        target.Execute("PRAGMA foreign_keys = ON");
        var dangling = target.Query("PRAGMA foreign_key_check").Count();
        if (dangling > 0)
        {
            throw new InvalidOperationException($"The version-1 copy has {dangling} dangling foreign keys.");
        }

        target.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    /// <summary>
    /// Every data table of the file with its columns in table order, in creation order: not the FTS tables (dumped
    /// separately), SQLite's own, or schema_migrations (the migrator's record, which a migration adds to). Read from a
    /// version-1 file, it is 0001's tables and columns: what <see cref="Dump"/> compares.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Tables(SqliteConnection connection)
    {
        var tables = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var table in connection.Query<string>(
                     "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '%_fts%' " +
                     "AND name <> 'schema_migrations' ORDER BY rowid"))
        {
            tables[table] = connection.Query<string>($"SELECT name FROM pragma_table_info('{table}') ORDER BY cid").ToList();
        }

        return tables;
    }

    /// <summary>
    /// The given tables' rows (the given columns only, every row sorted), both FTS indexes and the AUTOINCREMENT counters,
    /// as text: two dumps are equal exactly when that data is.
    /// </summary>
    public static string Dump(SqliteConnection connection, IReadOnlyDictionary<string, IReadOnlyList<string>> tables)
    {
        var text = new StringBuilder();
        foreach (var (table, columns) in tables)
        {
            text.AppendLine("## " + table);
            Rows(connection, $"SELECT {string.Join(", ", columns)} FROM {table}", text);
        }

        text.AppendLine("## entity_fts");
        Rows(connection, "SELECT rowid, name, aliases, summary, body, secret, tags, hidden_aliases FROM entity_fts", text);
        text.AppendLine("## fact_fts");
        Rows(connection, "SELECT rowid, statement FROM fact_fts", text);
        text.AppendLine("## sqlite_sequence");
        Rows(connection, "SELECT name, seq FROM sqlite_sequence", text);
        return text.ToString();
    }

    private static void Rows(SqliteConnection connection, string sql, StringBuilder text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i)
                ? "NULL"
                : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }

        rows.Sort(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            text.AppendLine(row);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5,
            RecursiveTriggers = true,
        }.ToString());
        connection.Open();
        connection.Execute("PRAGMA busy_timeout = 5000");
        return connection;
    }
}
