using System.Globalization;
using DndMcp.Formatting;
using DndMcp.Formatting.Srd;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tools;

/// <summary>
/// The defaults a rules, encounter or balance call takes from a campaign it CHOSE: named with <c>campaign</c>, or resolved
/// because it asked for that campaign's party (<c>encounter_difficulty party: "campaign"</c>) or, from stage 4, an
/// encounter or a character (<c>balance_simulate</c>). One reading and one wording for every such tool, so the notes the
/// tests pin byte for byte cannot drift between two copies.
///
/// <para>
/// <b>Why not <see cref="CampaignService.ReadDefaults"/>.</b> That reads the AMBIENT campaign (this process's current one,
/// else the active one) and never resolves, so it fails soft for a tool that merely may have a campaign. A call that
/// names its campaign has chosen one: the campaign is resolved as every campaign call resolves it
/// (<see cref="CampaignService.Resolve"/>, which may migrate the file and refuses an unknown slug), and the edition and
/// offset defaults then come from THAT campaign, never from the active one. One campaign's party judged under another's
/// ruleset is a wrong answer that looks right.
/// </para>
/// <para>
/// <b>Two wordings, chosen by whether the campaign is also the ambient one.</b> When it is (the current campaign, or the
/// active one when none is current), the notes are <see cref="CampaignDefaultNotes"/>' own, word for word ("2014 rules: the
/// active campaign's (belmakor) ruleset."), so naming the active campaign gives the same text as leaving it out. Any other
/// campaign's notes name it ("2014 rules: the sky campaign's ruleset."): calling it "the active campaign" would be false,
/// and a model would then believe the wrong campaign is active.
/// </para>
/// <para>
/// <b>Store failures are this helper's to map</b> (<see cref="Mapped{T}"/>): the tools that call it are not campaign tools
/// to the host's filter (their SQLite failures are srd.db's), so a damaged or locked campaigns.db met on this path would
/// otherwise reach the model as the SDK's bare "An error occurred invoking …".
/// </para>
/// </summary>
internal static class CampaignArgumentDefaults
{
    /// <summary>
    /// The campaign a call chose (<paramref name="campaign"/>, else the current one, else the active one, else the only one)
    /// with its defaults. Unmapped: call it inside <see cref="Mapped{T}"/> (with whatever else the call reads from the same
    /// campaign).
    /// </summary>
    /// <exception cref="Domain.Core.DndInputException">No campaigns, no such campaign, or several and none chosen.</exception>
    public static ResolvedCampaignDefaults Read(CampaignService campaigns, string? campaign)
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        return Of(campaigns, campaigns.Resolve(string.IsNullOrWhiteSpace(campaign) ? null : campaign));
    }

    /// <summary>
    /// <paramref name="row"/>'s defaults (its ruleset, its <c>effective_level_offset</c>), and whether it is the ambient
    /// campaign whose defaults a call without <c>campaign</c> takes. Unmapped, as <see cref="Read"/>.
    /// </summary>
    public static ResolvedCampaignDefaults Of(CampaignService campaigns, CampaignRow row)
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        ArgumentNullException.ThrowIfNull(row);

        // TryRead falls back to the active campaign when the id it prefers is gone; the slug check keeps another
        // campaign's settings from being read as this one's.
        var values = CampaignDefaults.TryRead(campaigns.Database, row.Id) is { } read && read.Slug == row.Slug ? read : null;
        var ambient = CampaignDefaults.TryRead(campaigns.Database, campaigns.CurrentCampaignId)?.Slug == row.Slug;
        return new ResolvedCampaignDefaults(row, values, ambient);
    }

    /// <summary>
    /// Runs <paramref name="read"/> (campaigns.db reads about a chosen campaign) with a SQLite failure turned into the
    /// store's own message (<see cref="CampaignDatabase.TryMapUnavailable"/>: locked, damaged, unreadable, newer), or, for
    /// any other code, one that names the file and says what was not read.
    /// </summary>
    /// <param name="notRead">What the call could not read, for the fallback message: "the campaign's party was not read".</param>
    /// <exception cref="CampaignStoreUnavailableException">campaigns.db could not be read.</exception>
    public static T Mapped<T>(CampaignService campaigns, Func<T> read, string notRead)
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        ArgumentNullException.ThrowIfNull(read);
        try
        {
            return read();
        }
        catch (SqliteException ex)
        {
            var path = campaigns.Database.Path;
            throw CampaignDatabase.TryMapUnavailable(ex, path, out var unavailable)
                ? unavailable
                : new CampaignStoreUnavailableException(
                    $"campaigns.db at {Path.GetFullPath(path)} could not be read (SQLite error " +
                    $"{ex.SqliteErrorCode.ToString(CultureInfo.InvariantCulture)}), so {notRead}. Nothing was changed.", ex);
        }
    }

    /// <summary>"2014 rules: the sky campaign's ruleset." (a chosen campaign that is not the ambient one).</summary>
    public static string EditionNote(string edition, string campaignSlug) => $"{edition} rules: the {campaignSlug} campaign's ruleset.";

    /// <summary>
    /// "Effective level +2: the deep campaign's effective_level_offset; pass effective_level_offset 0 for the book levels
    /// alone." (a chosen campaign that is not the ambient one).
    /// </summary>
    public static string LevelOffsetNote(int offset, string campaignSlug) =>
        $"Effective level {SrdMarkdownText.Signed(offset)}: the {campaignSlug} campaign's effective_level_offset; " +
        "pass effective_level_offset 0 for the book levels alone.";
}

/// <summary>What <see cref="CampaignArgumentDefaults"/> read about a chosen campaign.</summary>
/// <param name="Row">The campaign.</param>
/// <param name="Values">Its defaults; null when campaigns.db gave none for it (the tool's own defaults then stand).</param>
/// <param name="IsAmbient">It is also the campaign a call without <c>campaign</c> takes its defaults from (the notes' wording).</param>
internal sealed record ResolvedCampaignDefaults(CampaignRow Row, CampaignDefaultValues? Values, bool IsAmbient)
{
    /// <summary>The campaign's ruleset as an edition default: 2014 or 2024; null for a mixed campaign (2024 then, unnoted).</summary>
    public string? Edition => new CampaignDefaultsReading(Values, null).Edition;

    /// <summary>The campaign's <c>effective_level_offset</c> when it is set and not 0; else null (nothing to apply or note).</summary>
    public int? LevelOffset => Values?.EffectiveLevelOffset is { } offset and not 0 ? offset : null;

    /// <summary>The note for an edition this campaign supplied: <see cref="CampaignDefaultNotes.Edition"/>'s when ambient.</summary>
    public string EditionNote(string edition) =>
        IsAmbient ? CampaignDefaultNotes.Edition(edition, Row.Slug) : CampaignArgumentDefaults.EditionNote(edition, Row.Slug);

    /// <summary>The note for an offset this campaign supplied: <see cref="CampaignDefaultNotes.LevelOffset"/>'s when ambient.</summary>
    public string LevelOffsetNote(int offset) =>
        IsAmbient ? CampaignDefaultNotes.LevelOffset(offset, Row.Slug) : CampaignArgumentDefaults.LevelOffsetNote(offset, Row.Slug);
}
