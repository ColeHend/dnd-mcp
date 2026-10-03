using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>One roll of a dice_roll call, as the host logs it.</summary>
/// <param name="Expression">The expression as typed ("1d20+5>=15"): re-parsing it gives the rules, so no enum is stored.</param>
/// <param name="Label">The call's label, if any.</param>
/// <param name="Total">The roll's total.</param>
/// <param name="Outcome">A trailing comparison's result; null when the expression has none.</param>
/// <param name="DetailJson">The faces and groups as a JSON object (the host's shape, contract §3.11 and understand-change-sites §1.4).</param>
public sealed record DiceLogRoll(string Expression, string? Label, long Total, bool? Outcome, string DetailJson);

/// <summary>A logged roll: its <c>dice_roll.seq</c> and id.</summary>
public sealed record LoggedRoll(long Seq, string Id);

/// <summary>What <see cref="DiceLogWriter.TryLog"/> did.</summary>
/// <param name="Logged">The rolls were written.</param>
/// <param name="CampaignSlug">The campaign they were logged to (null when not logged for want of a campaign).</param>
/// <param name="SessionNumber">The live session they were logged under.</param>
/// <param name="Secret">They were logged as secret (behind the DM's screen).</param>
/// <param name="Rolls">The rows written, in roll order.</param>
/// <param name="NotLoggedReason">Why nothing was logged ("no session is live"), for the "Not logged: …" line.</param>
public sealed record DiceLogResult(
    bool Logged,
    string? CampaignSlug,
    int? SessionNumber,
    bool Secret,
    IReadOnlyList<LoggedRoll> Rolls,
    string? NotLoggedReason);

/// <summary>
/// Logs the host's dice_roll results to a campaign (contract §3.11): only while that campaign has a live session, one
/// <c>dice_roll</c> row per roll, all in one transaction.
///
/// <para>
/// <b>Never throws for the store.</b> The dice are already rolled when this runs; an error would read as a failed roll
/// and invite a re-roll, which is exactly what a logged table roll must never cause. So a missing database, a missing
/// campaign, no live session, a locked or unwritable file all come back as <see cref="DiceLogResult.NotLoggedReason"/>
/// (store failures are logged as warnings too). Arguments that are the host's bug (no rolls, detail that is not a JSON
/// object) still throw. Not in change_log: undo never un-rolls dice.
/// </para>
/// </summary>
public sealed class DiceLogWriter
{
    /// <summary>What <see cref="DiceLogResult.NotLoggedReason"/> says when nothing is live.</summary>
    public const string NoLiveSession = "no session is live";

    private readonly CampaignDatabase _database;
    private readonly ILogger? _logger;

    public DiceLogWriter(CampaignDatabase database, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _logger = logger;
    }

    /// <summary>Logs <paramref name="rolls"/> to the campaign's live session, or says why not.</summary>
    /// <exception cref="ArgumentException">No rolls, a blank expression, or detail that is not a JSON object (host bugs).</exception>
    public DiceLogResult TryLog(string campaignId, IReadOnlyList<DiceLogRoll> rolls, bool secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        ArgumentNullException.ThrowIfNull(rolls);
        if (rolls.Count == 0)
        {
            throw new ArgumentException("There are no rolls to log.", nameof(rolls));
        }

        foreach (var roll in rolls)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(roll.Expression, nameof(rolls));
            if (JsonNode.Parse(roll.DetailJson) is not JsonObject)
            {
                throw new ArgumentException("A roll's detail must be a JSON object.", nameof(rolls));
            }
        }

        if (!_database.Exists)
        {
            return NotLogged(null, secret, "there is no campaign database");
        }

        try
        {
            return _database.Write((connection, transaction) =>
            {
                var campaign = WriteBatch.LoadCampaign(connection, transaction, campaignId);
                if (campaign is null)
                {
                    return NotLogged(null, secret, "the campaign no longer exists");
                }

                var live = new HandleResolver(connection, campaign.Id, transaction).LiveSession();
                if (live is null)
                {
                    return NotLogged(campaign.Slug, secret, NoLiveSession);
                }

                var at = _database.Now();
                var logged = new List<LoggedRoll>(rolls.Count);
                foreach (var roll in rolls)
                {
                    var id = CampaignDatabase.NewId();
                    var seq = DiceRollLog.Append(connection, transaction, new DiceRollRow(0, id, campaign.Id, live.EntityId, roll.Expression.Trim(),
                        string.IsNullOrWhiteSpace(roll.Label) ? null : roll.Label.Trim(), roll.Total,
                        roll.Outcome switch { null => null, true => 1L, false => 0L }, roll.DetailJson, secret ? 1 : 0, at));
                    logged.Add(new LoggedRoll(seq, id));
                }

                return new DiceLogResult(true, campaign.Slug, checked((int)live.Number), secret, logged, null);
            });
        }
        catch (CampaignStoreUnavailableException ex)
        {
            _logger?.LogWarning(ex, "A dice roll was not logged to campaigns.db.");
            return NotLogged(null, secret, ex.Message);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "A dice roll was not logged to campaigns.db at {Path}.", _database.Path);
            return NotLogged(null, secret, "the campaign database could not be written");
        }
    }

    private static DiceLogResult NotLogged(string? slug, bool secret, string reason) => new(false, slug, null, secret, [], reason);
}
