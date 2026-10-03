using System.Globalization;
using System.Text.Json;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;
using SS = DndMcp.Domain.Campaign.CampaignValues.SessionStatuses;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>A session in a list.</summary>
/// <param name="Ref"><c>session:&lt;n&gt;</c>.</param>
/// <param name="Number">Its number.</param>
/// <param name="Title">Its title (the session entity's name).</param>
/// <param name="Status">planned, prepped, live, played, cancelled.</param>
/// <param name="PlayedOn">The real-world date, as recorded.</param>
/// <param name="PlayedOnPrecision">day, month, approx, unknown.</param>
/// <param name="Arc">Its arc, when visible.</param>
public sealed record SessionSummary(string Ref, int Number, string Title, string Status, string? PlayedOn, string PlayedOnPrecision, EntityLink? Arc);

/// <summary>A page of sessions, by number.</summary>
public sealed record SessionPage(IReadOnlyList<SessionSummary> Sessions, string? NextCursor, int Total);

/// <summary>One session as a perspective sees it.</summary>
/// <param name="Session">The session.</param>
/// <param name="Recap">The recap (the session entity's body).</param>
/// <param name="IngameStart">In-game date at the start.</param>
/// <param name="IngameEnd">In-game date at the end.</param>
/// <param name="Attendance">Who was there (characters this view may see).</param>
/// <param name="AttendanceRecorded">
/// Whether any attendance was recorded (verdicts treat none as "not recorded"). Outside the author view it counts only the
/// rows this view may see: "recorded" over an empty list would say a hidden character was there (contract §3.2, no trace
/// of what is hidden).
/// </param>
/// <param name="DiceRolls">The latest rolls logged in the session, oldest first (secret rolls: author view only).</param>
/// <param name="DiceRollsTotal">
/// How many rolls this view may see were logged in all; more than <see cref="DiceRolls"/> holds when the list was cut, so
/// the host can say "… and N more" (contract §3.10) instead of passing a partial list off as the whole session.
/// </param>
/// <param name="Author">Prep, live log, what changed in the session: author view only.</param>
public sealed record SessionDetail(
    SessionSummary Session,
    string? Recap,
    string? IngameStart,
    string? IngameEnd,
    IReadOnlyList<AttendanceView> Attendance,
    bool AttendanceRecorded,
    IReadOnlyList<DiceRollView> DiceRolls,
    int DiceRollsTotal,
    AuthorSessionDetail? Author);

/// <summary>One character's attendance; the note is the author's.</summary>
public sealed record AttendanceView(EntityLink Character, bool Present, string? Note);

/// <summary>A logged roll. <paramref name="Success"/> is the trailing comparison's outcome, when there was one.</summary>
public sealed record DiceRollView(string Expression, string? Label, long Total, bool? Success, bool? Secret, string At, string? Detail);

/// <summary>One live-log note.</summary>
public sealed record LiveLogEntry(string? At, string Text);

/// <summary>A session's author-only parts.</summary>
/// <param name="Visibility">The session entity's visibility.</param>
/// <param name="PrepMd">The prep notes / run-sheet.</param>
/// <param name="LiveLog">Notes taken at the table (a scratchpad, not in change_log).</param>
/// <param name="StartedAt">When the session went live.</param>
/// <param name="EndedAt">When it ended.</param>
/// <param name="LevelAtStart">The party's level at the start.</param>
/// <param name="Data">The session's data object as JSON text.</param>
/// <param name="Changes">What changed in this session (batches written with it as their session), oldest first.</param>
public sealed record AuthorSessionDetail(
    string Visibility,
    string PrepMd,
    IReadOnlyList<LiveLogEntry> LiveLog,
    string? StartedAt,
    string? EndedAt,
    long? LevelAtStart,
    string Data,
    IReadOnlyList<HistoryBatch> Changes);

/// <summary>A session's recap with what it established (author view).</summary>
/// <param name="Session">The session.</param>
/// <param name="Recap">The recap text.</param>
/// <param name="Attendance">Who was there.</param>
/// <param name="FactsEstablished">Facts whose established session is this one.</param>
/// <param name="KnowledgeLearned">Knowledge rows learned in this session.</param>
public sealed record SessionRecap(
    SessionSummary Session,
    string Recap,
    IReadOnlyList<AttendanceView> Attendance,
    IReadOnlyList<RecapFact> FactsEstablished,
    IReadOnlyList<RecapLearned> KnowledgeLearned);

/// <summary>A fact established in the session.</summary>
public sealed record RecapFact(string Ref, string? Code, string Statement, string CanonStatus, IReadOnlyList<string> KnownBy);

/// <summary>A knowledge row learned in the session: who learned what, in which state, under which name.</summary>
public sealed record RecapLearned(string Target, string TargetLabel, string Knower, string State, string? KnownAs);

/// <summary>
/// campaign_session's reads (contract §7): the list, one session, and the recap.
///
/// <para>
/// <b>What a non-author view gets:</b> played and live sessions whose session entity it may see undisguised (a planned
/// session's title is prep, and prep is the author's: <see cref="ReadScope.HiddenOutsideAuthorView"/> keeps unplayed
/// sessions out of every reader, not only this one), their recap, attendance by the names it knows, and non-secret dice. The prep
/// notes, the live log (a scratchpad the author types into at the table, where "they almost guessed the Protector" is
/// exactly the kind of line it holds), secret rolls and "what changed in this session" (change history) are the
/// author's. <see cref="Recap"/> is an author tool: it lists every fact established and every knowledge row learned.
/// </para>
/// </summary>
public sealed class SessionReader
{
    /// <summary>The most rolls one session detail lists (the newest; <see cref="SessionDetail.DiceRollsTotal"/> counts all).</summary>
    public const int MaxDiceShown = 200;

    private readonly CampaignDatabase _database;

    public SessionReader(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Sessions by number, optionally of one status.</summary>
    /// <exception cref="DndInputException">An unknown status, a bad limit or cursor, an unknown character perspective.</exception>
    public SessionPage List(CampaignRow campaign, string? status = null, int? limit = null, string? cursor = null, Perspective? perspective = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        var take = ReadCursor.Limit(limit);
        string? wanted = null;
        if (!string.IsNullOrWhiteSpace(status) && !SS.Set.TryMatch(status, out wanted))
        {
            throw new DndInputException($"status \"{status.Trim()}\" is not a session status; use one of {SS.Set.List}.");
        }

        var scope = ReadScope.Open(connection, campaign, perspective, null);
        var fingerprint = ReadCursor.Fingerprint("sessions", campaign.Id, wanted, scope.Who.Perspective.Text);
        var offset = ReadCursor.Decode(cursor, fingerprint);
        var rows = Rows(connection, campaign.Id).Where(r => wanted is null || r.Status == wanted).ToList();
        scope.LoadEntities(rows.Select(r => r.EntityId));
        var sessions = rows.Where(r => Visible(scope, r)).Select(r => Summary(scope, r)).ToList();
        return new SessionPage(sessions.Skip(offset).Take(take).ToList(), ReadCursor.Next(offset, take, sessions.Count, fingerprint), sessions.Count);
    }

    /// <summary>One session: a number ("3"), <c>session:3</c>, <c>session:live</c> or <c>session:last</c>.</summary>
    /// <exception cref="DndInputException">No such session this perspective may see (worded the same either way).</exception>
    public SessionDetail Get(CampaignRow campaign, string session, Perspective? perspective = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        var scope = ReadScope.Open(connection, campaign, perspective, null);
        var row = Find(scope, session);
        var entity = scope.Entity(row.EntityId)!;
        var attendance = Attendance(scope, row);
        var dice = DiceRollLog.ForSession(connection, row.EntityId, includeSecret: scope.IsAuthorView, MaxDiceShown)
            .Select(d => new DiceRollView(d.Expression, d.Label, d.Total, d.Outcome is null ? null : d.Outcome != 0,
                scope.IsAuthorView ? d.Secret != 0 : null, d.At, scope.IsAuthorView ? d.Detail : null))
            .ToList();
        var diceTotal = connection.ExecuteScalar<long>(
            "SELECT count(*) FROM dice_roll WHERE session_id = @sessionId" + (scope.IsAuthorView ? string.Empty : " AND secret = 0"),
            new { sessionId = row.EntityId });
        AuthorSessionDetail? author = null;
        if (scope.IsAuthorView)
        {
            var changes = connection.Query<ChangeRow>(
                $"SELECT {ChangeRow.Columns} FROM change_log WHERE campaign_id = @campaignId AND session_id = @sessionId ORDER BY seq",
                new { campaignId = campaign.Id, sessionId = row.EntityId }).ToList();
            author = new AuthorSessionDetail(entity.Row.Visibility, row.PrepMd, LiveLog(row.LiveLog), row.StartedAt, row.EndedAt,
                row.LevelAtStart, row.Data, new HistoryRenderer(connection, campaign).Batches(changes));
        }

        return new SessionDetail(Summary(scope, row), NullIfBlank(entity.Row.BodyMd), row.IngameStart, row.IngameEnd, attendance.Views,
            attendance.Recorded, dice, checked((int)diceTotal), author);
    }

    /// <summary>The recap of a session with the facts it established and the knowledge learned in it (author view).</summary>
    /// <exception cref="DndInputException">No such session.</exception>
    public SessionRecap Recap(CampaignRow campaign, string session)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        var scope = ReadScope.Open(connection, campaign, Perspective.Author, null);
        var row = Find(scope, session);
        var entity = scope.Entity(row.EntityId)!;
        var factIds = connection.Query<string>(
            "SELECT id FROM fact WHERE campaign_id = @campaignId AND established_session_id = @sessionId AND deleted_at IS NULL ORDER BY seq",
            new { campaignId = campaign.Id, sessionId = row.EntityId }).ToList();
        scope.LoadFacts(factIds);
        var facts = factIds.Select(scope.Fact).OfType<FactState>()
            .Select(f => new RecapFact(f.Ref, f.Row.Code, f.Row.Statement, f.Row.CanonStatus, CampaignSearch.KnownByLabels(f.Entries, scope.AuthorRef, null)))
            .ToList();
        var learned = new List<RecapLearned>();
        foreach (var k in connection.Query<KnowledgeRow>(
                     $"SELECT {KnowledgeRow.Columns} FROM knowledge WHERE campaign_id = @campaignId AND learned_session_id = @sessionId " +
                     "ORDER BY created_at, id", new { campaignId = campaign.Id, sessionId = row.EntityId }))
        {
            string target;
            string label;
            if (k.FactId is not null)
            {
                target = ReadGates.FactHandle(scope, k.FactId);
                label = scope.Fact(k.FactId) is { } fact ? ReadText.Excerpt(fact.Row.Statement, 120) : "(deleted fact)";
            }
            else
            {
                target = scope.AuthorRef(k.EntityId);
                label = scope.Entity(k.EntityId)?.Row.Name ?? "(deleted entity)";
            }

            var knower = k.KnowerKind == CampaignValues.KnowerKinds.Character ? scope.AuthorRef(k.KnowerId) : k.KnowerKind;
            learned.Add(new RecapLearned(target, label, knower, k.State, k.KnownAs));
        }

        return new SessionRecap(Summary(scope, row), entity.Row.BodyMd, Attendance(scope, row).Views, facts, learned);
    }

    private static IReadOnlyList<SessionRow> Rows(SqliteConnection connection, string campaignId) =>
        connection.Query<SessionRow>($"SELECT {SessionRow.Columns} FROM session WHERE campaign_id = @campaignId ORDER BY number",
            new { campaignId }).ToList();

    // A non-author view sees sessions whose entity it may see undisguised; the scope already hides a session that is not
    // played or live from it (ReadScope.HiddenOutsideAuthorView), so every reader agrees on which sessions exist for it.
    private static bool Visible(ReadScope scope, SessionRow row) =>
        scope.IsAuthorView || scope.Entity(row.EntityId) is { Shown: true };

    private static SessionRow Find(ReadScope scope, string session)
    {
        var text = (session ?? string.Empty).Trim();
        CampaignHandle handle;
        if (text.Length > 0 && text.All(char.IsAsciiDigit) &&
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            handle = new CampaignHandle.SessionByNumber(number);
        }
        else if (!CampaignHandle.TryParse(text, out handle, out var problem))
        {
            throw new DndInputException($"session: {problem} A session is a number (3) or session:3, session:live, session:last.");
        }

        var row = new HandleResolver(scope.Connection, scope.Campaign.Id).TrySession(handle);
        if (row is not null)
        {
            scope.LoadEntities([row.EntityId]);
        }

        if (row is null || !Visible(scope, row) || scope.Entity(row.EntityId) is null)
        {
            throw new DndInputException(scope.IsAuthorView
                ? $"No session {handle} in this campaign. campaign_session {ListCall(scope)} lists the sessions."
                : $"No session {handle} for this perspective. campaign_session {ListCall(scope)} lists the sessions it can see.");
        }

        return row;
    }

    /// <summary>
    /// The list call a "no such session" refusal prints: valid JSON naming the campaign (a call sent as printed must work
    /// while another campaign is current) and, for a non-author view, the perspective (so it lists what that view sees, not
    /// the author's planned sessions). Both are what the caller already gave: nothing here is hidden from it.
    /// </summary>
    private static string ListCall(ReadScope scope) => scope.IsAuthorView
        ? $"{{\"action\": \"list\", \"campaign\": \"{scope.Campaign.Slug}\"}}"
        : $"{{\"action\": \"list\", \"campaign\": \"{scope.Campaign.Slug}\", \"perspective\": \"{scope.Who.Perspective.Text}\"}}";

    private static SessionSummary Summary(ReadScope scope, SessionRow row)
    {
        var entity = scope.Entity(row.EntityId);
        var number = checked((int)row.Number);
        return new SessionSummary(
            EntityReader.SessionHandle(number),
            number,
            entity?.Name ?? "Session " + number.ToString(CultureInfo.InvariantCulture),
            row.Status,
            row.PlayedOn,
            row.PlayedOnPrecision,
            scope.VisibleEntity(row.ArcId)?.Link);
    }

    private static (IReadOnlyList<AttendanceView> Views, bool Recorded) Attendance(ReadScope scope, SessionRow row)
    {
        var rows = scope.Connection.Query<AttendanceRow>(
            $"SELECT {AttendanceRow.Columns} FROM session_attendance WHERE session_id = @sessionId", new { sessionId = row.EntityId }).ToList();
        scope.LoadEntities(rows.Select(r => r.CharacterId));
        var views = rows
            .Select(r => (Row: r, Character: scope.VisibleEntity(r.CharacterId)))
            .Where(r => r.Character is not null)
            .Select(r => new AttendanceView(r.Character!.Link, r.Row.Present != 0, scope.IsAuthorView ? r.Row.Note : null))
            .OrderBy(v => v.Character.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (views, scope.IsAuthorView ? rows.Count > 0 : views.Count > 0);
    }

    private static IReadOnlyList<LiveLogEntry> LiveLog(string json)
    {
        var entries = new List<LiveLogEntry>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return entries;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.String:
                        entries.Add(new LiveLogEntry(null, item.GetString() ?? string.Empty));
                        break;
                    case JsonValueKind.Object:
                        var at = item.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
                        var text = item.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : item.GetRawText();
                        entries.Add(new LiveLogEntry(at, text ?? string.Empty));
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // A hand-edited log that is not JSON is shown as nothing rather than failing the read.
        }

        return entries;
    }

    private static string? NullIfBlank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
