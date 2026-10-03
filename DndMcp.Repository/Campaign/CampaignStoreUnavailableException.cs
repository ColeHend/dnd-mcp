namespace DndMcp.Repository.Campaign;

/// <summary>
/// campaigns.db cannot be opened, created, read or updated for a reason a person can fix: it was written by a newer
/// dnd-mcp (its schema version is higher than this build knows), its directory or file is not writable, it is damaged or
/// is not a SQLite database, the disk is full or failing, another process held it locked past busy_timeout, or the backup
/// that must precede a schema update or a restore could not be written. Raised:
/// <list type="bullet">
/// <item>when opening or migrating, at a process's first campaign call;</item>
/// <item>by a write batch, where a read-only file or a full disk first shows (the batch is rolled back);</item>
/// <item>by every batch and every read connection once another process has moved the file to a newer schema
/// (<see cref="CampaignDatabase"/>'s per-use check; nothing is read or written);</item>
/// <item>by <see cref="CampaignBackups.Restore(string)"/> when campaigns.db cannot be locked, saved first or written
/// (campaigns.db is unchanged);</item>
/// <item>and built by <see cref="CampaignDatabase.TryMapUnavailable"/> for the host's call-tool, get-prompt and
/// read-resource filters, so a read statement that fails (a lock held past busy_timeout, a damaged page met mid-query)
/// says what a failing write says.</item>
/// </list>
///
/// <para>
/// A distinct type so the host can tell these apart from bugs, exactly as it does for
/// <c>SrdIndexUnavailableException</c>: the call-tool filter shows this message instead of the SDK's bare "An error
/// occurred invoking …", which would leave every campaign tool broken with no explanation. The message is written for
/// the user reading a tool error or the stderr log: what is wrong, which file, and what to do. It never contains SQL,
/// schema text or campaign content (the inner exception carries the SQLite detail for the log). Anything else that
/// escapes <see cref="CampaignDatabase"/> is a bug and is treated as one.
/// </para>
/// </summary>
public sealed class CampaignStoreUnavailableException : Exception
{
    public CampaignStoreUnavailableException(string message)
        : base(message)
    {
    }

    public CampaignStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
