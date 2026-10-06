using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// Which encounter a <c>combat</c> call means (contract D13): <c>"current"</c> (the active one; the default),
/// <c>"last"</c> (the most recently ended), or a name, compared by <see cref="CampaignText.Key"/>. A name is unique among
/// the campaign's NOT-ended encounters (checked at <c>prepare</c> and <c>start</c>), so it first means that one; a name that
/// matches only ended encounters means the most recently ended of them. There is no <c>encounter:</c> handle kind: the
/// encounter is the tracker's state, not a campaign entry (a <c>scene</c> entity holds prep prose).
///
/// <para>
/// <b>One active encounter per campaign</b> (D13), checked inside the transaction that would make a second one
/// (<see cref="RequireNoActive"/>): under BEGIN IMMEDIATE that check is race-free, and the partial unique index
/// <c>ux_encounter_active</c> is only the backstop. A <c>SQLITE_CONSTRAINT_UNIQUE</c> (2067) from it is mapped to the same
/// refusal (<see cref="IsActiveConflict"/>), never the generic error: a raw SqliteException reaches the model as "An error
/// occurred" and could carry SQL.
/// </para>
/// <para>
/// <b>Author refusals only.</b> The messages here list encounter names (author text): the non-author board never uses this
/// class's refusals (its one wording is <see cref="CombatBoard.NothingText"/>).
/// </para>
/// </summary>
public static class EncounterResolver
{
    /// <summary>The active encounter (the default address).</summary>
    public const string Current = "current";

    /// <summary>The most recently ended encounter.</summary>
    public const string Last = "last";

    /// <summary>The most encounter names a refusal lists.</summary>
    public const int MaxListed = 8;

    /// <summary>SQLite's extended result code for a UNIQUE constraint failure.</summary>
    public const int UniqueConstraint = 2067;

    /// <summary>The campaign's active encounter, or null (read leniently: <see cref="CombatStore.Encounters"/>).</summary>
    public static EncounterRow? Active(SqliteConnection connection, string campaignId, SqliteTransaction? transaction = null) =>
        CombatStore.Encounters(connection, "WHERE campaign_id = @campaignId AND status = @active ORDER BY rowid LIMIT 1",
            new { campaignId, active = ES.Active }, transaction).FirstOrDefault();

    /// <summary>The most recently ended encounter, or null.</summary>
    public static EncounterRow? LastEnded(SqliteConnection connection, string campaignId, SqliteTransaction? transaction = null) =>
        CombatStore.Encounters(connection, "WHERE campaign_id = @campaignId AND status = @ended ORDER BY ended_at DESC, rowid DESC LIMIT 1",
            new { campaignId, ended = ES.Ended }, transaction).FirstOrDefault();

    /// <summary>Every encounter of the campaign, oldest first.</summary>
    public static IReadOnlyList<EncounterRow> All(SqliteConnection connection, string campaignId, SqliteTransaction? transaction = null) =>
        CombatStore.Encounters(connection, "WHERE campaign_id = @campaignId ORDER BY rowid", new { campaignId }, transaction);

    /// <summary>
    /// The encounter a name means (D13): the not-ended one of that name, else the most recently ended one, else null.
    /// </summary>
    public static EncounterRow? ByName(SqliteConnection connection, string campaignId, string name, SqliteTransaction? transaction = null)
    {
        var key = CampaignText.Key(name);
        if (key.Length == 0)
        {
            return null;
        }

        var matches = All(connection, campaignId, transaction).Where(e => CampaignText.Key(e.Name) == key).ToList();
        return matches.LastOrDefault(e => e.Status != ES.Ended)
               ?? matches.Where(e => e.Status == ES.Ended).OrderBy(e => e.EndedAt, StringComparer.Ordinal).LastOrDefault();
    }

    /// <summary>The encounter an address means (null or blank: <see cref="Current"/>), or null when none.</summary>
    public static EncounterRow? TryResolve(SqliteConnection connection, string campaignId, string? address, SqliteTransaction? transaction = null)
    {
        var text = string.IsNullOrWhiteSpace(address) ? Current : address.Trim();
        return CampaignText.Key(text) switch
        {
            Current => Active(connection, campaignId, transaction),
            Last => LastEnded(connection, campaignId, transaction),
            _ => ByName(connection, campaignId, text, transaction),
        };
    }

    /// <summary>The encounter an address means, or the author refusal that says what exists and how to start one.</summary>
    /// <exception cref="DndInputException">No such encounter.</exception>
    public static EncounterRow Resolve(SqliteConnection connection, CampaignRow campaign, string? address, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        if (TryResolve(connection, campaign.Id, address, transaction) is { } found)
        {
            return found;
        }

        var text = string.IsNullOrWhiteSpace(address) ? Current : address.Trim();
        var all = All(connection, campaign.Id, transaction);
        var listing = Listing(all);
        throw new DndInputException(CampaignText.Key(text) switch
        {
            Current => $"No combat is running in {campaign.Slug}: start one with combat {{\"action\": \"start\", \"name\": …, \"campaign\": \"{campaign.Slug}\"}}{listing}",
            Last => $"No combat has ended in {campaign.Slug} yet{listing}",
            _ => $"encounter \"{WriteBatch.Echo(text)}\": no encounter by that name in {campaign.Slug}{listing}",
        });
    }

    /// <summary>
    /// Refuses when the campaign already has an active encounter other than <paramref name="exceptId"/> (D13): "end it
    /// first", naming it, with the call that ends it.
    /// </summary>
    /// <exception cref="DndInputException">Another encounter is active.</exception>
    public static void RequireNoActive(SqliteConnection connection, CampaignRow campaign, SqliteTransaction? transaction, string? exceptId = null)
    {
        if (Active(connection, campaign.Id, transaction) is { } active && active.Id != exceptId)
        {
            throw ActiveRefusal(campaign, active.Name);
        }
    }

    /// <summary>The refusal of a second active encounter (D13), for the check and for the unique index's backstop alike.</summary>
    public static DndInputException ActiveRefusal(CampaignRow campaign, string activeName) =>
        new($"\"{WriteBatch.Echo(activeName)}\" is already running in {campaign.Slug} (one fight at a time): end it first with " +
            $"combat {{\"action\": \"end\", \"encounter\": {Json(activeName)}, \"campaign\": \"{campaign.Slug}\"}}, then start this one.");

    /// <summary>
    /// A name as a JSON string inside a printed call: whole (never cut like an echo, or the call would name another fight)
    /// and escaped (a quote in the name would break the call), accented letters kept.
    /// </summary>
    public static string Json(string text) => JsonValue.Create(text).ToJsonString(CampaignLogJson.Options);

    /// <summary>Whether a SQLite failure is the one-active index refusing a second active encounter.</summary>
    public static bool IsActiveConflict(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SqliteErrorCode == 19 && exception.SqliteExtendedErrorCode == UniqueConstraint &&
               exception.Message.Contains("encounter.campaign_id", StringComparison.Ordinal);
    }

    /// <summary>
    /// Refuses an encounter name that is blank, too long, not one line, without a letter or digit, one of the reserved
    /// addresses, or already the name of a not-ended encounter (D13: a name addresses exactly one such encounter). A name
    /// with no letter or digit ("!!!") has an empty key (F2, review CR05): no address could ever reach it, so a planned fight
    /// of that name could not be started, ended or discarded, and it took every other keyless name; refused with C02's
    /// shared phrase (<see cref="CharacterWriter.NoKeyProblem"/>).
    /// </summary>
    /// <exception cref="DndInputException">The name cannot be used.</exception>
    public static string CheckNewName(SqliteConnection connection, CampaignRow campaign, string? name, SqliteTransaction? transaction, string field = "name")
    {
        var text = name?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > CombatStore.MaxEncounterName || text.Any(char.IsControl))
        {
            throw new DndInputException($"{field} must be one line of 1 to {CombatStore.N(CombatStore.MaxEncounterName)} characters, e.g. \"The crypt\".");
        }

        if (CampaignText.Key(text).Length == 0)
        {
            throw new DndInputException($"{field} \"{WriteBatch.Echo(text)}\": {CharacterWriter.NoKeyProblem(field)}");
        }

        if (CampaignText.Key(text) is Current or Last)
        {
            throw new DndInputException($"{field} \"{WriteBatch.Echo(text)}\" is how calls address a fight; give the fight another name.");
        }

        var key = CampaignText.Key(text);
        if (All(connection, campaign.Id, transaction).FirstOrDefault(e => e.Status != ES.Ended && CampaignText.Key(e.Name) == key) is { } clash)
        {
            throw new DndInputException(
                $"{field} \"{WriteBatch.Echo(text)}\" is already the name of a {clash.Status} fight in {campaign.Slug}; give another name, or address that one with encounter.");
        }

        return text;
    }

    // " Encounters: "The crypt" (planned), …" — the names an author may address.
    private static string Listing(IReadOnlyList<EncounterRow> all)
    {
        if (all.Count == 0)
        {
            return ".";
        }

        var shown = all.Where(e => e.Status != ES.Ended).Concat(all.Where(e => e.Status == ES.Ended).Reverse()).Take(MaxListed)
            .Select(e => $"\"{WriteBatch.Echo(e.Name)}\" ({e.Status})").ToList();
        var more = all.Count > shown.Count ? $", and {CombatStore.N(all.Count - shown.Count)} more" : string.Empty;
        return $". Encounters: {string.Join(", ", shown)}{more}.";
    }
}
