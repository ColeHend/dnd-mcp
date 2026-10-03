using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>What a campaign sets for the rules and encounter tools: its slug (for "the active campaign's" notes), ruleset and level offset.</summary>
/// <param name="Slug">The campaign's slug.</param>
/// <param name="Ruleset">2014, 2024 or mixed (a mixed campaign gives no edition default: callers fall back to 2024).</param>
/// <param name="EffectiveLevelOffset"><c>settings.effective_level_offset</c> when it is a whole number from −10 to 10; else null.</param>
public sealed record CampaignDefaultValues(string Slug, string Ruleset, int? EffectiveLevelOffset);

/// <summary>
/// The ambient defaults the rules, encounter and balance tools take from a campaign, read without ever creating,
/// migrating or writing campaigns.db (contract §7, §8).
///
/// <para>
/// <b>Why it never goes through <see cref="CampaignDatabase.OpenRead"/> or <see cref="CampaignDatabase.TryOpenExisting"/>:</b>
/// both run <see cref="CampaignDatabase.EnsureReady"/>, which creates the file or migrates it. The defaults are read on
/// every rules_search and encounter_difficulty call, by users who may never touch campaigns; a rules lookup must not
/// leave a campaigns.db behind, take a pre-migration backup or block on a migration lock. So this opens the file only
/// when it exists, reads two rows, and returns null for anything short of a readable, migrated database (no file, an
/// empty or unmigrated file).
/// </para>
/// <para>
/// <b>Read-write, never read-only, and never long</b> (review R08/C12). A read-only connection to a WAL database creates
/// campaigns.db-wal and -shm and cannot remove them when it closes, so every rules call left both beside the user's
/// database; read-write (which never creates the file: <see cref="CampaignDatabase.ConnectionString"/>) removes them on
/// close, and only SELECTs run on it. The wait for a lock is short (<see cref="BusyTimeoutMilliseconds"/>, with the
/// shortest whole-second retry Microsoft.Data.Sqlite allows): a rules lookup has a fallback, and must not stall for
/// seconds behind a restore or another process's migration.
/// </para>
/// <para>
/// <b>"No campaign" and "campaign unreadable" are told apart.</b> Null is "no campaign": nothing chosen, nothing there,
/// so the built-in default is simply right. A file that exists and cannot be read is
/// <see cref="CampaignStoreUnavailableException"/>, with a message for the user that names the file and the reason: a
/// lock held too long, a damaged file, no permission, or a schema newer than this build. The host then keeps the rules
/// tools working with their built-in default and says it could not read the campaign's settings, rather than silently
/// ignoring a campaign the user chose. A newer schema is refused before any table is read: this build cannot know what
/// its tables mean (the per-use check every other read makes, review R02).
/// </para>
/// <para>
/// <b>Which campaign:</b> the preferred id (the process's current campaign, set by <c>campaign use</c>/<c>create</c>),
/// else <c>app_state.active_campaign</c> (ignored when it names a campaign that no longer exists). Never "the only
/// campaign": a default applied silently must come from a choice the user made.
/// </para>
/// </summary>
public static class CampaignDefaults
{
    /// <summary>The app_state key holding the active campaign's id.</summary>
    public const string ActiveCampaignKey = "active_campaign";

    /// <summary>How long the defaults read waits for another process's lock before reporting the campaign unreadable.</summary>
    public const int BusyTimeoutMilliseconds = 250;

    /// <summary>The smallest bound <see cref="CampaignDatabase.ConnectionString"/> accepts for Microsoft.Data.Sqlite's own retry.</summary>
    private const int DefaultTimeoutSeconds = 1;

    /// <summary>SQLITE_BUSY: another connection holds a lock this read could not wait out.</summary>
    private const int SqliteBusy = 5;

    /// <summary>The defaults of the preferred or active campaign, or null (no database, an unmigrated one, none chosen, the chosen one gone).</summary>
    /// <exception cref="CampaignStoreUnavailableException">
    /// "Campaign unreadable": campaigns.db exists but cannot be read (locked past the short wait, damaged, unreadable, or
    /// written by a newer dnd-mcp). The message is the user's: which file and why.
    /// </exception>
    public static CampaignDefaultValues? TryRead(CampaignDatabase database, string? preferredCampaignId)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!database.Exists)
        {
            return null;
        }

        CampaignDatabase.EnsureDapperConfigured();
        try
        {
            using var connection = new SqliteConnection(
                CampaignDatabase.ConnectionString(database.Path, SqliteOpenMode.ReadWrite, DefaultTimeoutSeconds));
            connection.Open();
            connection.Execute($"PRAGMA busy_timeout = {BusyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
            var version = CampaignDbMigrator.UserVersion(connection, null);
            if (version < 1)
            {
                return null;
            }

            if (version > CampaignDbMigrator.LatestVersion)
            {
                throw new CampaignStoreUnavailableException(
                    CampaignDbMigrator.NewerSchemaMessage(Path.GetFullPath(database.Path), version, CampaignDbMigrator.LatestVersion));
            }

            var campaign = Campaign(connection, preferredCampaignId);
            if (campaign is null)
            {
                var active = connection.QueryFirstOrDefault<string?>("SELECT value FROM app_state WHERE key = @key", new { key = ActiveCampaignKey });
                campaign = Campaign(connection, active);
            }

            return campaign is null ? null : new CampaignDefaultValues(campaign.Slug, campaign.Ruleset, LevelOffset(campaign.Settings));
        }
        catch (SqliteException ex)
        {
            // The store's own busy message speaks of its five-second wait; this read gives up after a fraction of that.
            var path = Path.GetFullPath(database.Path);
            if ((ex.SqliteErrorCode & 0xFF) == SqliteBusy)
            {
                throw new CampaignStoreUnavailableException(
                    $"campaigns.db at {path} is locked by another dnd-mcp process (a restore or a migration in another session), so " +
                    "its campaign settings were not read this time. Nothing was changed.", ex);
            }

            throw CampaignDatabase.TryMapUnavailable(ex, path, out var unavailable)
                ? unavailable
                : new CampaignStoreUnavailableException(
                    $"campaigns.db at {path} could not be read (SQLite error " +
                    $"{ex.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}), so its campaign settings were not used. " +
                    "Nothing was changed; a campaign tool call says more about the file.", ex);
        }
    }

    // By id; the active-campaign value is also accepted as a slug, since it is hand-editable in app_state.
    private static CampaignRow? Campaign(SqliteConnection connection, string? idOrSlug) =>
        string.IsNullOrWhiteSpace(idOrSlug)
            ? null
            : connection.QueryFirstOrDefault<CampaignRow>(
                $"SELECT {CampaignRow.Columns} FROM campaign WHERE id = @value OR slug = @value ORDER BY id = @value DESC LIMIT 1",
                new { value = idOrSlug.Trim() });

    // The settings validator's range, both ends included: a campaign set to 10 is +10, not "no offset".
    private static int? LevelOffset(string settings)
    {
        try
        {
            using var document = JsonDocument.Parse(settings);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("effective_level_offset", out var value) &&
                   value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var offset) && offset is >= -10 and <= 10
                ? offset
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
