using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>
/// Which character an author's <c>campaign_character</c> call is about (contract §7.1): the <c>character</c> argument, or by
/// default the campaign's own character in a player campaign (<c>campaign.my_character_id</c>; "my PC" is that column,
/// never a sheet flag). A DM campaign has no default: the refusal lists the party's handles. A short slug that begins
/// exactly one character's slug resolves; two or more is a refusal, never a guess. A cross-campaign handle
/// (<c>one-piece/character:keras</c>) is refused: Phase 7 takes it only in <c>same_as</c> links.
///
/// <para>
/// <b>"Begins" means at a word boundary</b> (<c>bjorn</c> completes <c>bjorn-mountainfell</c>; <c>bjo</c> completes
/// nothing; <c>side</c> does not complete <c>sidekick</c>): the reading of contract §7.1 the stage-2 review accepted,
/// because it is how a perspective's slug completes (Phase 6's <c>KnowledgeLoader.UniqueBySlugPrefix</c>, slug + "-"), so
/// <c>campaign_character {"character": "bjorn"}</c> and <c>perspective: "character:bjorn"</c> name the same character,
/// and a fragment of a word never lands a write on a character the author did not mean.
/// </para>
///
/// <para>
/// <b>Author calls only.</b> Its refusals name the party's handles and the typed handle; a non-author read
/// (<c>get</c> with a perspective) resolves through <see cref="Read.ReadScope.ResolveEntity"/> instead, whose refusals
/// never name what that view may not print.
/// </para>
/// </summary>
internal static class CharacterLookup
{
    /// <summary>The character the call names (or the campaign's own, in a player campaign); refused otherwise.</summary>
    /// <param name="missingHint">A sentence for a handle that names nothing (update: how to create the character).</param>
    /// <exception cref="DndInputException">No character given in a DM campaign, a malformed or cross-campaign handle, a handle that names no live character.</exception>
    public static EntityRow Resolve(SqliteConnection connection, CampaignRow campaign, string? character, SqliteTransaction? transaction = null, string? missingHint = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        var resolver = new HandleResolver(connection, campaign.Id, transaction);
        if (string.IsNullOrWhiteSpace(character))
        {
            return Default(connection, campaign, resolver, transaction);
        }

        var text = character.Trim();
        if (!CampaignHandle.TryParse(text, out var handle, out var problem))
        {
            throw new DndInputException($"character: {problem}");
        }

        if (handle is CampaignHandle.CrossCampaign cross)
        {
            throw new DndInputException(OwnSlugProblem("character", text, cross, campaign) ??
                $"character \"{WriteBatch.Echo(text)}\" names another campaign's entity; give a character of {campaign.Slug} (a campaign-slug/ handle is only for same_as links).");
        }

        EntityRow? entity;
        try
        {
            entity = resolver.TryEntity(handle);
        }
        catch (DndInputException ex)
        {
            throw new DndInputException($"character: {ex.Message}");
        }

        if (entity is null && handle is CampaignHandle.EntityBySlug { Kind: null or CV.Kinds.Character } bySlug)
        {
            entity = ByPrefix(connection, campaign, bySlug.Slug, transaction);
        }

        if (entity is { Kind: CV.Kinds.Character })
        {
            return entity;
        }

        if (entity is not null)
        {
            throw new DndInputException($"character {entity.Handle} is a {entity.Kind}, not a character; a sheet belongs to a character.");
        }

        if (resolver.TryEntity(handle, includeDeleted: true) is { Kind: CV.Kinds.Character, IsDeleted: true } deleted)
        {
            throw new DndInputException(
                $"character {deleted.Handle} is deleted ({deleted.SeqHandle}); restore it first: campaign_write " +
                $"{{\"ops\": [{{\"op\": \"restore\", \"ref\": \"{deleted.SeqHandle}\"}}], \"campaign\": \"{campaign.Slug}\"}}.");
        }

        var party = PartyHandles(connection, campaign, transaction);
        var listing = party.Count == 0 ? string.Empty : $" The party: {string.Join(", ", party)}.";
        throw new DndInputException(
            $"character \"{WriteBatch.Echo(text)}\": no character by that handle in {campaign.Slug}.{(missingHint is null ? string.Empty : " " + missingHint)}{listing}");
    }

    /// <summary>
    /// The refusal of a handle prefixed with the campaign's OWN slug (<c>p14/character:prof</c> in p14), or null for
    /// another campaign's: such a handle is refused like any cross-campaign one (a campaign-slug/ handle is only for
    /// same_as links), but "names another campaign's entity" would be false, so it says what is wrong and what to send.
    /// </summary>
    internal static string? OwnSlugProblem(string field, string text, CampaignHandle.CrossCampaign cross, CampaignRow campaign) =>
        string.Equals(cross.CampaignSlug, campaign.Slug, StringComparison.OrdinalIgnoreCase)
            ? $"{field} \"{WriteBatch.Echo(text)}\": {campaign.Slug} is this campaign's own slug; give the handle without it: \"{cross.Inner.Text}\"."
            : null;

    /// <summary>The upsert call a refusal prints for a character that does not exist yet (update's missing-entity hint).</summary>
    public static string CreateHint(CampaignRow campaign) =>
        $"Create the character first: campaign_write {{\"ops\": [{{\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"…\"}}], \"campaign\": \"{campaign.Slug}\"}}, then update its sheet.";

    // The campaign's own character in a player campaign; a DM campaign has none (the party's handles are listed).
    private static EntityRow Default(SqliteConnection connection, CampaignRow campaign, HandleResolver resolver, SqliteTransaction? transaction)
    {
        if (campaign.Role == CV.Roles.Player && campaign.MyCharacterId is { } mine &&
            connection.QueryFirstOrDefault<EntityRow>(
                $"SELECT {EntityRow.Columns} FROM entity WHERE id = @mine AND campaign_id = @campaignId AND deleted_at IS NULL",
                new { mine, campaignId = campaign.Id }, transaction) is { Kind: CV.Kinds.Character } own)
        {
            return own;
        }

        if (campaign.Role == CV.Roles.Player)
        {
            throw new DndInputException(
                $"character is required: {campaign.Slug} has no character of yours (campaign {{\"action\": \"update\", \"my_character\": \"…\", \"campaign\": \"{campaign.Slug}\"}} sets it), " +
                "or give the character's handle.");
        }

        var party = PartyHandles(connection, campaign, transaction);
        throw new DndInputException(
            $"character is required in a DM campaign: give the character's handle" +
            (party.Count == 0 ? ", e.g. \"character:bjorn-mountainfell\"." : $", one of the party: {string.Join(", ", party)}."));
    }

    // The one live character whose slug begins with the typed slug and a hyphen (null: none, or more than one).
    private static EntityRow? ByPrefix(SqliteConnection connection, CampaignRow campaign, string slug, SqliteTransaction? transaction)
    {
        var prefix = slug.Trim().ToLowerInvariant() + "-";
        var candidates = connection.Query<EntityRow>(
            $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND kind = @kind AND deleted_at IS NULL " +
            "AND substr(slug, 1, @length) = @prefix",
            new { campaignId = campaign.Id, kind = CV.Kinds.Character, length = prefix.Length, prefix }, transaction).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    // The current party's author handles (a refusal's listing): D8's one roster (PartyRoster), so a dead or departed member
    // is never offered as "one of the party", and the order is the roster's. Sheets are not read: only who is in the party.
    private static IReadOnlyList<string> PartyHandles(SqliteConnection connection, CampaignRow campaign, SqliteTransaction? transaction) =>
        PartyRoster.Read(connection, campaign, asOfSession: null, transaction, sheets: false).Members.Select(m => m.Handle).ToList();
}
