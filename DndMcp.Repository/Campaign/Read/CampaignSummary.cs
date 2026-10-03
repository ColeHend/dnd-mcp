using Dapper;
using DndMcp.Domain.Campaign;
using Microsoft.Data.Sqlite;
using K = DndMcp.Domain.Campaign.CampaignValues.Kinds;
using SS = DndMcp.Domain.Campaign.CampaignValues.SessionStatuses;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>A campaign at a glance, for one perspective.</summary>
/// <param name="Slug">The campaign's slug.</param>
/// <param name="Name">Its name.</param>
/// <param name="Role">player or dm.</param>
/// <param name="Ruleset">2014, 2024 or mixed.</param>
/// <param name="Status">active, hiatus, ended.</param>
/// <param name="DmName">The DM's name, when recorded.</param>
/// <param name="CurrentIngame">The in-game date now.</param>
/// <param name="MyCharacter">The player's character (a player campaign), when visible.</param>
/// <param name="Party">The party faction, when visible.</param>
/// <param name="PartyMembers">
/// Current members this view may see: the member and the party visible to it AND the <c>member_of</c> relation's own
/// visibility admitting it (the relation rule of contract §3.2, the same test campaign_get's relations use). An
/// author-only membership (the spy who is secretly in the crew) listed in a player's summary would say what the get of
/// that member hides; a planned or former membership is not a current member for anyone.
/// </param>
/// <param name="CurrentLocation">Where the party is, when visible.</param>
/// <param name="LiveSession">The session at the table now (outside the author view: only when this view may see its entity, undisguised).</param>
/// <param name="LastPlayed">The last played session this view may see (the same rule).</param>
/// <param name="LastRecapOpening">The first lines of that session's recap.</param>
/// <param name="OpenThreads">Up to eight open, active or blocked quests and threads.</param>
/// <param name="OpenThreadsTotal">How many there are in all (of those this view may see).</param>
/// <param name="OpenQuestions">
/// Open questions. Outside the author view a withheld or lean question counts as open (it is shown as open); the author's
/// lean and withheld counts are in <see cref="AuthorCampaignSummary"/>, so no player-side result even has a field that
/// says the author holds answers.
/// </param>
/// <param name="RunningClocks">Running clocks (outside the author view: only those shown to players).</param>
/// <param name="Author">Secrets, inventions, settings and changes since the last session: author view only.</param>
public sealed record CampaignSummaryView(
    string Slug,
    string Name,
    string Role,
    string Ruleset,
    string Status,
    string? DmName,
    string? CurrentIngame,
    EntityLink? MyCharacter,
    EntityLink? Party,
    IReadOnlyList<EntityLink> PartyMembers,
    EntityLink? CurrentLocation,
    SessionSummary? LiveSession,
    SessionSummary? LastPlayed,
    string? LastRecapOpening,
    IReadOnlyList<ThreadLine> OpenThreads,
    int OpenThreadsTotal,
    int OpenQuestions,
    IReadOnlyList<ClockLine> RunningClocks,
    AuthorCampaignSummary? Author);

/// <summary>An open quest or thread and its status.</summary>
public sealed record ThreadLine(EntityLink Entity, string? Status);

/// <summary>A running clock.</summary>
public sealed record ClockLine(EntityLink Clock, long Filled, long Segments, string Unit);

/// <summary>The author-only part of a summary.</summary>
/// <param name="Settings">The campaign's settings as JSON text.</param>
/// <param name="Secrets">Every secret and its status.</param>
/// <param name="ProposedInventions">Entities and facts still proposed (awaiting accept or strike), with codes.</param>
/// <param name="ChangesSinceLastSession">Batches written after the last played session ended.</param>
/// <param name="RecentChanges">The newest of those batches (at most five).</param>
/// <param name="LeanQuestions">Questions the author leans toward an answer on.</param>
/// <param name="WithheldQuestions">Questions the author has answered but withholds.</param>
public sealed record AuthorCampaignSummary(
    string Settings,
    IReadOnlyList<ThreadLine> Secrets,
    IReadOnlyList<InventionLine> ProposedInventions,
    int ChangesSinceLastSession,
    IReadOnlyList<HistoryBatch> RecentChanges,
    int LeanQuestions,
    int WithheldQuestions);

/// <summary>A proposed invention: an entity or fact with its code.</summary>
public sealed record InventionLine(string Ref, string? Code, string Label);

/// <summary>
/// The campaign summary (contract §7): what the <c>campaign summary</c> action and the <c>campaign://&lt;slug&gt;/summary</c>
/// resource show. Everything goes through <see cref="ReadScope"/>, so a player's summary names party members, locations
/// and threads by the names the party knows, counts only what it may see, and carries none of the author's secrets,
/// inventions or history.
/// </summary>
public sealed class CampaignSummary
{
    /// <summary>Threads and quests listed (the total says how many more there are).</summary>
    public const int MaxThreads = 8;

    private readonly CampaignDatabase _database;

    public CampaignSummary(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>The summary of one campaign for one perspective.</summary>
    public CampaignSummaryView Build(CampaignRow campaign, Perspective? perspective = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        return Build(connection, campaign, perspective);
    }

    internal static CampaignSummaryView Build(SqliteConnection connection, CampaignRow campaign, Perspective? perspective)
    {
        var scope = ReadScope.Open(connection, campaign, perspective, null);
        var all = scope.LoadAllEntities().Select(scope.Entity).OfType<EntityState>().ToList();

        var partyVisible = scope.VisibleEntity(campaign.PartyId) is not null;
        var members = connection.Query<(string FromId, string Visibility)>(
                "SELECT from_id, visibility FROM relation WHERE campaign_id = @campaignId AND rel = @rel AND to_id = @party AND status = @current",
                new { campaignId = campaign.Id, rel = CampaignValues.Rels.MemberOf, party = campaign.PartyId ?? string.Empty, current = CampaignValues.RelationStatuses.Current })
            .Select(r => (Member: scope.VisibleEntity(r.FromId), r.Visibility))
            .Where(r => r.Member is not null && Audience.RelationVisible(scope.Who, r.Visibility, fromVisible: true, toVisible: partyVisible))
            .Select(r => r.Member!)
            .DistinctBy(m => m.Row.Id)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.Link)
            .ToList();

        var sessions = connection.Query<SessionRow>($"SELECT {SessionRow.Columns} FROM session WHERE campaign_id = @campaignId ORDER BY number",
            new { campaignId = campaign.Id }).ToList();
        bool SessionVisible(SessionRow s) => scope.IsAuthorView || scope.Entity(s.EntityId) is { Shown: true };
        var live = sessions.FirstOrDefault(s => s.Status == SS.Live && SessionVisible(s));
        var last = sessions.LastOrDefault(s => s.Status == SS.Played && SessionVisible(s));

        var threads = all.Where(e => e.Row.Kind is K.Quest or K.Thread && (scope.IsAuthorView ? e.Visible : e.Shown) &&
                                     scope.DisplayStatus(e) is { } status && ReadStatuses.OpenThread.Contains(status))
            .OrderBy(e => e.Row.SortKey is null ? 1 : 0).ThenBy(e => e.Row.SortKey ?? 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var questions = all.Where(e => e.Row.Kind == K.Question && (scope.IsAuthorView || e.Shown)).ToList();
        var openQuestions = questions.Count(q => scope.DisplayStatus(q) == CampaignValues.Statuses.QuestionOpen);

        var clocks = connection.Query<ClockRow>(
                $"SELECT {CampaignRows.Prefixed(ClockRow.Columns, "c")} FROM clock c JOIN entity e ON e.id = c.entity_id " +
                "WHERE e.campaign_id = @campaignId AND e.deleted_at IS NULL", new { campaignId = campaign.Id })
            .Select(c => (Clock: c, Entity: scope.VisibleEntity(c.EntityId)))
            .Where(c => c.Entity is not null && c.Entity.Row.Status == ReadStatuses.ClockRunning &&
                        (scope.IsAuthorView || (c.Entity.Shown && c.Clock.ShownToPlayers != 0)))
            .OrderBy(c => c.Entity!.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ClockLine(c.Entity!.Link, c.Clock.Filled, c.Clock.Segments, c.Clock.Unit))
            .ToList();

        SessionSummary? Summary(SessionRow? s) =>
            s is null ? null : new SessionSummary(EntityReader.SessionHandle(checked((int)s.Number)), checked((int)s.Number),
                scope.Entity(s.EntityId)?.Name ?? "Session " + s.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), s.Status, s.PlayedOn, s.PlayedOnPrecision, scope.VisibleEntity(s.ArcId)?.Link);

        return new CampaignSummaryView(
            campaign.Slug,
            campaign.Name,
            campaign.Role,
            campaign.Ruleset,
            campaign.Status,
            campaign.DmName,
            campaign.CurrentIngame,
            scope.VisibleEntity(campaign.MyCharacterId)?.Link,
            scope.VisibleEntity(campaign.PartyId)?.Link,
            members,
            scope.VisibleEntity(campaign.CurrentLocationId)?.Link,
            Summary(live),
            Summary(last),
            last is null ? null : ReadText.FirstLines(scope.Entity(last.EntityId)?.Row.BodyMd, 3),
            threads.Take(MaxThreads).Select(t => new ThreadLine(t.Link, scope.DisplayStatus(t))).ToList(),
            threads.Count,
            openQuestions,
            clocks,
            scope.IsAuthorView ? Author(connection, campaign, all, last, questions) : null);
    }

    private static AuthorCampaignSummary Author(
        SqliteConnection connection,
        CampaignRow campaign,
        IReadOnlyList<EntityState> all,
        SessionRow? last,
        IReadOnlyList<EntityState> questions)
    {
        var secrets = all.Where(e => e.Row.Kind == K.Secret)
            .OrderBy(e => e.Row.SortKey is null ? 1 : 0).ThenBy(e => e.Row.SortKey ?? 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ThreadLine(e.Link, e.Row.Status))
            .ToList();
        var inventions = all.Where(e => e.Row.CanonStatus == CampaignValues.CanonStatuses.Proposed)
            .Select(e => (Seq: e.Row.Seq, Line: new InventionLine(e.Ref!, e.Row.Code, e.Row.Name)))
            .OrderBy(e => e.Line.Code ?? "~", StringComparer.Ordinal).ThenBy(e => e.Seq)
            .Select(e => e.Line)
            .ToList();
        inventions.AddRange(connection.Query<(long Seq, string? Code, string Statement)>(
                "SELECT seq, code, statement FROM fact WHERE campaign_id = @campaignId AND canon_status = @proposed AND deleted_at IS NULL " +
                "ORDER BY coalesce(code, '~'), seq", new { campaignId = campaign.Id, proposed = CampaignValues.CanonStatuses.Proposed })
            .Select(f => new InventionLine("f:" + f.Seq, f.Code, ReadText.Excerpt(f.Statement, 120))));

        // Changes since the last played session: after it ended, or (no end time) after its last logged change.
        var since = last?.EndedAt;
        var fromSeq = since is null && last is not null
            ? connection.QueryFirstOrDefault<long?>("SELECT max(seq) FROM change_log WHERE session_id = @id", new { id = last.EntityId }) ?? 0
            : 0;
        var batches = connection.Query<(string BatchId, long First)>(
            "SELECT batch_id, min(seq) AS first FROM change_log WHERE campaign_id = @campaignId AND seq > @fromSeq " +
            (since is null ? string.Empty : "AND at > @since ") + "GROUP BY batch_id ORDER BY first DESC",
            new { campaignId = campaign.Id, fromSeq, since }).ToList();
        var recentIds = batches.Take(5).Select(b => b.BatchId).ToList();
        var rows = recentIds.Count == 0
            ? []
            : connection.Query<ChangeRow>($"SELECT {ChangeRow.Columns} FROM change_log WHERE batch_id IN @recentIds ORDER BY seq", new { recentIds }).ToList();
        var recent = new HistoryRenderer(connection, campaign).Batches(rows).OrderBy(b => recentIds.IndexOf(b.BatchId)).ToList();
        return new AuthorCampaignSummary(campaign.Settings, secrets, inventions, batches.Count, recent,
            questions.Count(q => q.Row.Status == ReadStatuses.QuestionLean),
            questions.Count(q => q.Row.Status == CampaignValues.Statuses.QuestionWithheld));
    }
}
