using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// The one way a reader opens campaigns.db: <see cref="CampaignDatabase.TryOpenExisting"/>, never
/// <see cref="CampaignDatabase.OpenRead"/>. A read must never create the database (contract §3.8: a read that finds no
/// file behaves as "no campaigns"); <c>OpenRead</c> would quietly create an empty one if the file vanished between the
/// host resolving the campaign and the read (a restore, a user deleting it), and every later call would then answer
/// from an empty campaign instead of saying what happened.
///
/// <para>
/// The refusal names no path: a <see cref="DndInputException"/> reaches whoever drives the client, and caller errors carry
/// no file paths (the house rule; <see cref="CampaignStoreUnavailableException"/> is the message that names the file,
/// written for the user). The call it prints is JSON the model can send as it is.
/// </para>
/// </summary>
internal static class ReadConnection
{
    /// <summary>The refusal when there is no campaigns.db to read.</summary>
    public const string NoDatabase =
        "There is no campaigns.db, so there are no campaigns to read. Create one with campaign {\"action\": \"create\"}.";

    /// <exception cref="DndInputException">There is no campaigns.db (<see cref="NoDatabase"/>).</exception>
    /// <exception cref="CampaignStoreUnavailableException">The file exists but cannot be used (see <see cref="CampaignDatabase.EnsureReady"/>).</exception>
    public static SqliteConnection Open(CampaignDatabase database) =>
        database.TryOpenExisting() ?? throw new DndInputException(NoDatabase);
}
