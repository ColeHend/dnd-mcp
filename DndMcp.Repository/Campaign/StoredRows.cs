using System.Data;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>
/// Reads of Phase 7 rows (holdings, coin ledgers, combat_log) through Dapper, guarded: a value of the wrong storage type
/// (text in a number column of a table whose STRICT was lost, written by another program or a damaged page) makes Dapper
/// throw while it materialises the row (InvalidOperationException when no constructor of the row record takes the stored
/// types, a DataException or cast error for a value it cannot convert), and unguarded that reaches the model as the host's
/// generic "An error occurred", which says nothing about what is wrong or what to do. Guarded, it is the store's
/// unreadable-row message (<see cref="CampaignStoreUnavailableException"/>), as every other value this version cannot read
/// is: which table, which file, that nothing was changed, and the way out. Never the stored value, nor Dapper's message,
/// which quotes it.
/// </summary>
internal static class StoredRows
{
    /// <summary>campaigns.db's full path, for a message.</summary>
    public static string PathOf(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return string.IsNullOrEmpty(connection.DataSource) ? "campaigns.db" : Path.GetFullPath(connection.DataSource);
    }

    /// <summary>Runs one read of <paramref name="table"/>'s rows, a value of the wrong type mapped to the store message.</summary>
    /// <exception cref="CampaignStoreUnavailableException">A row holds a value of the wrong type.</exception>
    public static T Read<T>(SqliteConnection connection, string table, Func<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is DataException or InvalidCastException or FormatException or OverflowException or InvalidOperationException)
        {
            throw Unreadable(connection, table, ex);
        }
    }

    /// <summary>The refusal of a row whose stored value is of the wrong type (class summary).</summary>
    public static CampaignStoreUnavailableException Unreadable(SqliteConnection connection, string table, Exception ex) =>
        new($"A {table} row in {PathOf(connection)} cannot be read: a column holds a value of the wrong type (text where a number " +
            "belongs). Nothing was changed. The value was written by something other than this server, or the file is damaged; " +
            "restore a backup from the backups directory beside it, or repair that row.", ex);
}
