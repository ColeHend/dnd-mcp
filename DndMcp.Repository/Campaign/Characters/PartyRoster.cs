using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Characters;

/// <summary>One current member of the party (contract D8), with the sheet when it has one. Author-facing.</summary>
/// <param name="EntityId">The character entity's id.</param>
/// <param name="Handle">Its author handle, <c>character:slug</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="Subtype">pc, npc, companion, …</param>
/// <param name="Sheet">Its sheet (as of the roster's session), or null when it has none.</param>
public sealed record PartyMember(string EntityId, string Handle, string Name, string? Subtype, CharacterSheet? Sheet)
{
    /// <summary>The sheet's level, when it has a sheet with one.</summary>
    public int? Level => Sheet?.Level;
}

/// <summary>A character linked to the party as current but left out of the roster: dead or departed (named in a note).</summary>
/// <param name="EntityId">Its id.</param>
/// <param name="Handle">Its author handle.</param>
/// <param name="Name">Its name.</param>
/// <param name="Reason">Its status: <see cref="CV.Statuses.CharacterDead"/> or <see cref="PartyRoster.Departed"/>.</param>
public sealed record ExcludedMember(string EntityId, string Handle, string Name, string Reason);

/// <summary>
/// The current party (contract D8) and the dead or departed characters still linked to it. Author-facing: the handles and
/// names are the author's (a non-author reader filters members through its own view, <see cref="SheetReader"/>).
/// </summary>
/// <param name="Members">Current members, by name.</param>
/// <param name="Excluded">Members left out because they are dead or departed, by name.</param>
/// <param name="PartyRef">
/// The party faction's author handle (<c>faction:the-party</c>), or null when the campaign has no party: D8's refusal of an
/// empty roster names it ("the sea campaign has no current party members: link characters member_of faction:the-party"),
/// and <see cref="CampaignRow"/> holds only its id.
/// </param>
public sealed record PartyRosterResult(IReadOnlyList<PartyMember> Members, IReadOnlyList<ExcludedMember> Excluded, string? PartyRef = null)
{
    /// <summary>The members with no sheet, or a sheet with no level (D8's <c>party: "campaign"</c> refusal names each).</summary>
    public IReadOnlyList<PartyMember> WithoutLevel => Members.Where(m => m.Level is null).ToList();

    /// <summary>Whether a character is a current member.</summary>
    public bool Contains(string entityId) => Members.Any(m => m.EntityId == entityId);
}

/// <summary>
/// Who the party is now (contract D8), the one answer every Phase 7 reader of "the party" shares: <c>combat start
/// add_party</c>, <c>encounter_difficulty party: "campaign"</c>, the encounter simulations, <c>/party</c>, the list form of
/// <c>campaign_character get</c>, and the non-author sheet line (only a current member's sheet is ever shown outside the
/// author view, §7.4).
///
/// <para>
/// <b>A current member</b> is a character linked <c>member_of</c> the campaign's party faction (<c>campaign.party_id</c>)
/// by a relation whose status is <c>current</c>, that is not deleted, and whose status is not <c>dead</c> or
/// <c>departed</c>, and whose link is in effect NOW: its <c>since</c> session (if any) is at or before now and its
/// <c>until</c> session (if any) after it, where now is the live session's number, else the last played session's, else 1
/// (nothing played or live yet: the first session; F2, review LR02). This is the as-of rule below with that session
/// (review L05): a member planned to join in session 14 while 12 is the last played is not yet a member (the party's
/// views would print his sheet line, <c>add_party</c> would seat him and <c>party: "campaign"</c> count him before he
/// joins), and a link whose <c>until</c> is session 2 is a member who left at session 2 (the write path sets it when one
/// leaves). Before anything is played, the party linked <c>since</c> session 1 is the party being prepared for (L05's
/// "before every session" left it out, so the list form, <c>add_party</c> and <c>party: "campaign"</c> told the author to
/// link characters who already were), while a <c>since</c> session 2 still waits for session 2. A dead or departed member
/// keeps its link (its history is the party's) and is reported in <see cref="PartyRosterResult.Excluded"/> so a caller
/// can name it ("Tristan is dead: left out"). Phase 6's two partial versions of this query (the campaign summary's and
/// the player-text check's) each missed one of these conditions; a fight seeded from one and a difficulty computed from
/// the other would disagree on who is in the party.
/// </para>
/// <para>
/// <b>As of a session</b> (<c>campaign_get include sheet</c> with <c>as_of_session</c>, §7.4) the membership relations,
/// the characters and their sheets are replayed to the end of that session (<see cref="ChangeReplay"/>), and a member
/// must have joined by then (a <c>since</c> session after it is not yet a member) and not left by then.
/// </para>
/// </summary>
public static class PartyRoster
{
    /// <summary>The current party, on a read connection of its own.</summary>
    /// <exception cref="CampaignStoreUnavailableException">campaigns.db cannot be read, or a member's sheet cannot.</exception>
    public static PartyRosterResult Read(CampaignDatabase database, CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var connection = ReadConnection.Open(database);
        return Read(connection, campaign);
    }

    /// <summary>The current party (or, with <paramref name="asOfSession"/>, the party at the end of that session).</summary>
    /// <param name="transaction">The transaction to read in, inside a write (no as_of there).</param>
    /// <param name="sheets">
    /// Read each member's sheet (default). False leaves <see cref="PartyMember.Sheet"/> null: for a reader that needs only
    /// who is in the party, so a damaged sheet of one member never blocks a read about another.
    /// </param>
    /// <exception cref="CampaignStoreUnavailableException">A member's sheet cannot be read.</exception>
    public static PartyRosterResult Read(SqliteConnection connection, CampaignRow campaign, int? asOfSession = null, SqliteTransaction? transaction = null,
        bool sheets = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        CampaignDatabase.EnsureDapperConfigured();
        if (campaign.PartyId is null)
        {
            return new PartyRosterResult([], []);
        }

        var party = connection.QueryFirstOrDefault<EntityRow>(
            $"SELECT {EntityRow.Columns} FROM entity WHERE id = @partyId AND campaign_id = @campaignId", new { partyId = campaign.PartyId, campaignId = campaign.Id }, transaction);

        var relations = connection.Query<RelationRow>(
            $"SELECT {RelationRow.Columns} FROM relation WHERE campaign_id = @campaignId AND rel = @rel AND to_id = @partyId",
            new { campaignId = campaign.Id, rel = CV.Rels.MemberOf, partyId = campaign.PartyId }, transaction).ToList();
        if (asOfSession is { } session)
        {
            relations = AsOfRows.ForEntities<RelationRow>(connection, CampaignTables.Relation, relations.Select(r => r.Id), [campaign.PartyId], session)
                .Where(r => r.Rel == CV.Rels.MemberOf && r.ToId == campaign.PartyId && r.CampaignId == campaign.Id)
                .ToList();
        }

        var sessions = connection.Query<(string EntityId, long Number, string Status)>(
                "SELECT entity_id, number, status FROM session WHERE campaign_id = @campaignId", new { campaignId = campaign.Id }, transaction)
            .ToList();
        var numbers = sessions.ToDictionary(s => s.EntityId, s => checked((int)s.Number), StringComparer.Ordinal);
        int? Number(string? sessionId) => sessionId is not null && numbers.TryGetValue(sessionId, out var n) ? n : null;

        // The window a member must be in: since ≤ now < until, as of the asked session, else NOW (class summary). With no
        // session played or live yet, "now" is session 1: the members who join in it are the party it is prepared for.
        var now = asOfSession ?? Now(sessions);
        var linked = relations
            .Where(r => r.Status == CV.RelationStatuses.Current)
            .Where(r => (Number(r.UntilSessionId) is not { } until || until > now) &&
                        (Number(r.SinceSessionId) is not { } since || since <= now))
            .Select(r => r.FromId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var characters = Characters(connection, campaign, linked, asOfSession, transaction);
        var members = new List<PartyMember>();
        var excluded = new List<ExcludedMember>();
        foreach (var entity in characters.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Seq))
        {
            if (entity.Status is CV.Statuses.CharacterDead or Departed)
            {
                excluded.Add(new ExcludedMember(entity.Id, entity.Handle, entity.Name, entity.Status));
                continue;
            }

            var sheet = !sheets ? null
                : asOfSession is { } n ? CharacterSheetStore.AsOf(connection, entity.Id, n, transaction)
                : CharacterSheetStore.Read(connection, entity.Id, transaction);
            members.Add(new PartyMember(entity.Id, entity.Handle, entity.Name, entity.Subtype, sheet));
        }

        return new PartyRosterResult(members, excluded, party?.Handle);
    }

    /// <summary>
    /// "Now" for the current roster (L05, LR02): the live session's number, else the last played session's, else 1 when
    /// nothing has been played or is live (the first session, the one being prepared: a <c>since: 1</c> counts from the
    /// start, a <c>since: 2</c> waits for session 2).
    /// </summary>
    private static int Now(IReadOnlyList<(string EntityId, long Number, string Status)> sessions) =>
        sessions.Where(s => s.Status == CV.SessionStatuses.Live).Select(s => (int?)checked((int)s.Number)).FirstOrDefault()
        ?? sessions.Where(s => s.Status == CV.SessionStatuses.Played).Select(s => (int?)checked((int)s.Number)).Max()
        ?? FirstSession;

    // The session "now" is before any is played or live (Now).
    private const int FirstSession = 1;

    /// <summary>
    /// The <c>departed</c> character status (one of <see cref="CV.Statuses.ByKind"/>'s character statuses; Phase 6 names no
    /// constant for it): a member who left the story keeps its link and is excluded like a dead one.
    /// </summary>
    public const string Departed = "departed";

    // The linked entities that are live characters of this campaign (as of the session, when one is asked for).
    private static List<EntityRow> Characters(SqliteConnection connection, CampaignRow campaign, IReadOnlyList<string> ids, int? asOfSession, SqliteTransaction? transaction)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = new List<EntityRow>();
        foreach (var chunk in ids.Chunk(400))
        {
            rows.AddRange(connection.Query<EntityRow>(
                $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND id IN @ids", new { campaignId = campaign.Id, ids = chunk }, transaction));
        }

        if (asOfSession is { } session)
        {
            rows = ChangeReplay.RowsAsOf(connection, CampaignTables.Entity.Name, ids, session, transaction)
                .Where(p => p.Value is not null)
                .Select(p => CampaignRows.FromValues<EntityRow>(p.Value!))
                .ToList();
        }

        return rows.Where(r => r.Kind == CV.Kinds.Character && r.DeletedAt is null && r.CampaignId == campaign.Id).ToList();
    }

    /// <summary>"Ignis, Lieutenant James Torch": names for a note, in roster order.</summary>
    public static string Names(IEnumerable<string> names) => string.Join(", ", names);

    /// <summary>"Belmakor Silverwind 12": a member and its level for the encounter_difficulty party note.</summary>
    public static string NameAndLevel(PartyMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.Level is { } level ? member.Name + " " + level.ToString(CultureInfo.InvariantCulture) : member.Name;
    }
}
