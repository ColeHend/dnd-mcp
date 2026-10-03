using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SS = DndMcp.Domain.Campaign.CampaignValues.SessionStatuses;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// The session-end checklist (contract §6): what a recap usually leaves behind. Every entry names things by handle or
/// quotes the recap's own words (the author's), never secret text from elsewhere.
/// </summary>
/// <param name="UnknownNames">
/// Proper nouns in the recap and the live log that match no entity name, alias or known_as here: possible new people,
/// places or things to add. Advisory (review U04): the scan is a heuristic, and a word that merely opens a sentence is an
/// expected false hit, so the checklist says to ignore ordinary words; worded "strike them from the recap", it had a model
/// rewrite the user's own recap to clear "Afterwards".
/// </param>
/// <param name="ClocksNotTicked">Running clocks nobody ticked this session (<c>clock:slug</c>).</param>
/// <param name="FactsWithoutKnowers">Facts established this session that no non-author knower holds, and that no visibility default gives the table (<c>f:12</c>).</param>
/// <param name="ClosedGateReveals">Gated facts that reached a non-author knower this session while their gate was not ready ("f:12 to party").</param>
/// <param name="ProposedInventions">Entities and facts created this session still proposed: accept or strike ("F3 f:12").</param>
/// <param name="AttendanceNotRecorded">No attendance rows for the session: party knowledge will read as "attendance not recorded".</param>
public sealed record SessionChecklist(
    IReadOnlyList<string> UnknownNames,
    IReadOnlyList<string> ClocksNotTicked,
    IReadOnlyList<string> FactsWithoutKnowers,
    IReadOnlyList<string> ClosedGateReveals,
    IReadOnlyList<string> ProposedInventions,
    bool AttendanceNotRecorded)
{
    public bool IsEmpty =>
        UnknownNames.Count == 0 && ClocksNotTicked.Count == 0 && FactsWithoutKnowers.Count == 0 && ClosedGateReveals.Count == 0 &&
        ProposedInventions.Count == 0 && !AttendanceNotRecorded;
}

/// <summary>What a session write did.</summary>
/// <param name="BatchId">The batch (null for a dry run).</param>
/// <param name="DryRun">Nothing was kept.</param>
/// <param name="Session"><c>session:12</c>.</param>
/// <param name="Number">The session's number.</param>
/// <param name="Status">Its status now (planned, prepped, live, played).</param>
/// <param name="Outcome">created, updated or unchanged.</param>
/// <param name="ChangedFields">
/// What changed (columns, next_hooks, and the attendance as one entry naming who was added, changed and removed:
/// "attendance (added character:aria; removed character:serif)").
/// </param>
/// <param name="Warnings">Applied anyway, worth reading.</param>
/// <param name="Checklist">For end and record_past: what the recap leaves to do.</param>
/// <param name="BackupPath">For end: the session-end backup, when it was written.</param>
/// <param name="BackupProblem">For end: why the backup failed (the session was ended all the same).</param>
/// <param name="AttendanceAdded">Characters given an attendance row by this write (<c>character:slug</c>).</param>
/// <param name="AttendanceRemoved">Characters whose attendance row this write removed: end and record_past replace the list.</param>
public sealed record SessionWriteResult(
    string? BatchId,
    bool DryRun,
    string Session,
    int Number,
    string Status,
    string Outcome,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<WriteWarning> Warnings,
    SessionChecklist? Checklist = null,
    string? BackupPath = null,
    string? BackupProblem = null,
    IReadOnlyList<string>? AttendanceAdded = null,
    IReadOnlyList<string>? AttendanceRemoved = null);

/// <summary>What a live-log append did.</summary>
public sealed record SessionLogResult(string Session, int Number, int NotesAdded, int NotesTotal);

/// <summary>
/// campaign_session's writing actions (contract §3.6, §6): plan, start, log, end and record_past.
///
/// <para>
/// A session is an entity (kind session, slug <c>session-&lt;n&gt;</c>, name = the title or "Session n", body_md = the
/// recap) plus its <c>session</c> row. Numbers are explicit (0 is session zero); with none, plan takes the next after the
/// highest (a new session), while start and record_past take the next session to PLAY: the lowest planned one after the
/// last played, else the next after the highest (so "plan 12, then start" starts 12 rather than creating 13). <b>At most
/// one session is live per campaign</b> (a partial unique index; start refuses a second with a message rather than a
/// constraint error), because "the live session" is the default context of every write: two would make it a guess which
/// night a reveal belongs to.
/// </para>
/// <para>
/// <b>Start and record_past are filed under the session they write</b> (change_log.session_id), unless the call names a
/// session: point-in-time replay reverses a change by the session it is filed under, so a start filed under no session
/// would leave session 12 "live" as of session 5, and a record_past of session 3 made while 12 is live would make session
/// 3 not exist as of session 5. End is filed under the live session it ends, which is the same thing. Plan follows the
/// usual context (prep made during session 12 belongs to 12; prep between sessions is timeless).
/// </para>
/// <para>
/// <b>The live log is not history.</b> <see cref="Log"/> appends <c>{at, text}</c> notes to <c>session.live_log</c>
/// without change_log rows (the column is unlogged): it is a scratchpad that becomes the recap, and logging every note
/// would bury the batches undo and the "changes since" feed are for.
/// </para>
/// <para>
/// <b>End</b> writes the recap, marks the session played, returns the checklist, and after the commit takes a
/// <c>session-end</c> backup; a failed backup is logged and reported, never a failed end (the session did end).
/// </para>
/// <para>
/// <b>A planned title is prep</b> (review L01): while a session is planned every player-side view hides it, title and
/// all, so a plan's title is written for the author ("Morwen Vashkar unmasked at the lighthouse"). Start and record_past
/// make the session party-visible, and with it the title, so both take a <c>title</c> (the players' name for the night),
/// and when they keep a planned title that is not the default "Session n" the result warns that players now see it. End
/// takes a title too, so a live session can be renamed.
/// </para>
/// <para>
/// <b>Attendance given to end or record_past replaces the session's list</b> (review C14): record_past is how a played
/// session is corrected, and merging kept a character wrongly listed (and said nothing changed). Rows not in the list
/// are removed, as logged deletes, and the result names who was added and removed. Start only adds and updates: it opens
/// the night, and nothing recorded before it is a correction.
/// </para>
/// <para>
/// <b>Played on is the table's date</b> (review U13): start's default is the local date of the clock
/// (<see cref="TimeProvider.GetLocalNow"/>), not the UTC one, which files every evening session west of UTC under the next
/// day. Timestamps stay UTC.
/// </para>
/// </summary>
public sealed partial class SessionWriter
{
    /// <summary>Notes one log call appends, and notes a live log keeps.</summary>
    public const int MaxNotesPerCall = 50;

    public const int MaxLiveLogNotes = 2_000;

    /// <summary>Next hooks an end records.</summary>
    public const int MaxNextHooks = 20;

    private const string Subject = "session";

    private readonly CampaignDatabase _database;
    private readonly ILogger? _logger;

    public SessionWriter(CampaignDatabase database, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _logger = logger;
    }

    /// <summary>
    /// Plans a session (planned; prepped when it has a run-sheet), or updates a planned one's title, arc, prep and date.
    /// </summary>
    /// <exception cref="DndInputException">The session is live or played, or an argument is invalid.</exception>
    public SessionWriteResult Plan(CampaignRow campaign, int? number = null, string? title = null, string? arc = null, string? prepMd = null,
        string? playedOn = null, WriteContext? context = null)
    {
        context ??= WriteContext.Default;
        var problems = new List<string>();
        CheckNumber(problems, number);
        CheckLine(problems, "title", title, CampaignLimits.MaxNameLength);
        CheckBody(problems, "prep_md", prepMd);
        var date = CheckDate(problems, playedOn, null);
        DslProblems.ThrowIfAny(problems, Subject);
        return WriteBatch.Run(_database, campaign, context, "campaign_session/plan", b =>
        {
            var n = number ?? NextNumber(b.Connection, b.Transaction, b.Campaign.Id);
            var existing = b.Resolver.SessionByNumber(n);
            var arcId = arc is null ? null : b.RequireEntity(Subject, "arc", "handle", arc, CampaignValues.Kinds.Arc).Id;
            string outcome;
            List<string> fields;
            if (existing is null)
            {
                var status = string.IsNullOrWhiteSpace(prepMd) ? SS.Planned : SS.Prepped;
                CreateSession(b, null, n, title, status, b.DefaultEntityVisibility(CampaignValues.Kinds.Session), new Dictionary<string, object?>
                {
                    ["prep_md"] = prepMd ?? string.Empty,
                    ["played_on"] = date?.Date,
                    ["played_on_precision"] = date?.Precision ?? CampaignValues.DatePrecisions.Unknown,
                    ["arc_id"] = arcId,
                }, "plan");
                outcome = WriteOutcomes.Created;
                fields = ["session"];
            }
            else
            {
                if (existing.Status is SS.Live or SS.Played)
                {
                    throw WriteBatch.Problem(Subject,
                        $"session {WriteBatch.Number(n)} is {existing.Status}; plan only sessions not yet played (end a live one, or fix a played one with record_past).");
                }

                var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (prepMd is not null)
                {
                    changes["prep_md"] = prepMd;
                }

                if (date is not null)
                {
                    changes["played_on"] = date.Date;
                    changes["played_on_precision"] = date.Precision;
                }

                if (arcId is not null)
                {
                    changes["arc_id"] = arcId;
                }

                var prepped = !string.IsNullOrWhiteSpace(prepMd ?? existing.PrepMd);
                changes["status"] = prepped ? SS.Prepped : SS.Planned;
                fields = b.Update("session", existing.EntityId, changes, "plan").ToList();
                if (title is not null)
                {
                    fields.AddRange(b.Update("entity", existing.EntityId, new Dictionary<string, object?> { ["name"] = title.Trim() }, "plan"));
                }

                outcome = fields.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated;
            }

            b.Finish();
            return SessionResult(b, n, outcome, fields, null);
        });
    }

    /// <summary>
    /// Starts a session: it becomes the live one (the default context of every write until it ends). With no number, the
    /// lowest planned session after the last played one, else a new one numbered after the highest. Filed under the
    /// session it starts unless the context names one. <paramref name="title"/> is the session's name from now on (the
    /// players see it); without one a planned session keeps its planned title, with a warning (class summary).
    /// </summary>
    /// <exception cref="DndInputException">Another session is live, the session was played, or an argument is invalid.</exception>
    public SessionWriteResult Start(CampaignRow campaign, int? number = null, string? playedOn = null, string? precision = null,
        IReadOnlyList<AttendanceSpec?>? attendance = null, string? ingame = null, WriteContext? context = null, string? title = null)
    {
        context ??= WriteContext.Default;
        var problems = new List<string>();
        CheckNumber(problems, number);
        CheckLine(problems, "title", title, CampaignLimits.MaxNameLength);
        var date = CheckDate(problems, playedOn, precision);
        problems.AddRange(AttendanceSpecs.Validate(attendance, "attendance"));
        CheckLine(problems, "ingame", ingame, CampaignLimits.MaxLabelLength);
        DslProblems.ThrowIfAny(problems, Subject);
        SessionTarget? own = null;
        return WriteBatch.Run(_database, campaign, context, "campaign_session/start", b =>
        {
            if (b.Resolver.LiveSession() is { } live)
            {
                throw WriteBatch.Problem(Subject,
                    $"session {WriteBatch.Number(live.Number)} is live; end it with {WriteBatch.SessionCall(b.Campaign.Slug, "end")} before starting another (one live session at a time).");
            }

            var n = own?.Number ?? number ?? NextToStart(b.Connection, b.Transaction, b.Campaign.Id);
            var existing = b.Resolver.SessionByNumber(n);
            if (existing is { Status: SS.Played })
            {
                throw WriteBatch.Problem(Subject, $"session {WriteBatch.Number(n)} was played; start the next one (leave session out) or give another session number.");
            }

            // The table's date: the clock's local date, not UTC's (class summary).
            var today = DateOnly.FromDateTime(_database.Time.GetLocalNow().DateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = SS.Live,
                ["started_at"] = b.Recorder.At,
                ["played_on"] = date?.Date ?? today,
                ["played_on_precision"] = date?.Precision ?? CampaignValues.DatePrecisions.Day,
            };
            if (ingame is not null)
            {
                values["ingame_start"] = ingame.Trim();
            }

            string outcome;
            List<string> fields;
            string sessionId;
            if (existing is null)
            {
                sessionId = CreateSession(b, own?.EntityId, n, title, SS.Live, CampaignValues.Visibilities.Party, values, "start");
                b.PlayerText.EntityCreated(sessionId, null);
                outcome = WriteOutcomes.Created;
                fields = ["session"];
            }
            else
            {
                sessionId = existing.EntityId;
                b.PlayerText.Entity(sessionId, null);
                fields = b.Update("session", sessionId, values, "start").ToList();
                var entity = new Dictionary<string, object?> { ["visibility"] = CampaignValues.Visibilities.Party };
                if (title is not null)
                {
                    entity["name"] = title.Trim();
                }
                else
                {
                    // Live, the session can still be renamed by end's title (plan and record_past refuse a live one).
                    WarnPlannedTitle(b, existing, "end");
                }

                fields.AddRange(b.Update("entity", sessionId, entity, "start"));
                outcome = WriteOutcomes.Updated;
            }

            var attended = Attendance(b, sessionId, attendance, "start", replace: false);
            fields.AddRange(attended.Fields);
            b.Finish();
            return SessionResult(b, n, outcome, fields, null, attended);
        }, setup => own = OwnSession(setup, number));
    }

    /// <summary>Appends notes to the live session's log (not change-logged: see the class summary).</summary>
    /// <exception cref="DndInputException">No session is live, or the notes are invalid.</exception>
    public SessionLogResult Log(CampaignRow campaign, IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var problems = new List<string>();
        if (notes is null || notes.Count == 0)
        {
            problems.Add("notes is required: one or more notes, e.g. [\"Serif bargains with the harbourmaster\"].");
        }
        else
        {
            if (notes.Count > MaxNotesPerCall)
            {
                problems.Add($"notes has {WriteBatch.Number(notes.Count)} items; at most {WriteBatch.Number(MaxNotesPerCall)} per call.");
            }

            for (var i = 0; i < notes.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(notes[i]))
                {
                    problems.Add($"notes item {WriteBatch.Number(i + 1)} is blank.");
                }
                else if (notes[i].Length > CampaignLimits.MaxStatementLength)
                {
                    problems.Add($"notes item {WriteBatch.Number(i + 1)} is {WriteBatch.Number(notes[i].Length)} characters; at most {WriteBatch.Number(CampaignLimits.MaxStatementLength)}.");
                }
            }
        }

        DslProblems.ThrowIfAny(problems, Subject);
        return _database.Write((connection, transaction) =>
        {
            var resolver = new HandleResolver(connection, campaign.Id, transaction);
            var live = resolver.LiveSession() ??
                       throw new DndInputException($"No session is live: {WriteBatch.SessionCall(campaign.Slug, "start")} starts one (log notes are for the session at the table).");
            var log = JsonNode.Parse(live.LiveLog) as JsonArray ?? [];
            if (log.Count + notes!.Count > MaxLiveLogNotes)
            {
                throw new DndInputException(
                    $"The live log of session {WriteBatch.Number(live.Number)} holds {WriteBatch.Number(log.Count)} notes; at most {WriteBatch.Number(MaxLiveLogNotes)}. End the session and fold them into the recap.");
            }

            var at = _database.Now();
            foreach (var note in notes)
            {
                log.Add(new JsonObject { ["at"] = at, ["text"] = note.Trim() });
            }

            // live_log is deliberately outside change_log (CampaignTables marks it unlogged), so this is a plain UPDATE.
            connection.Execute("UPDATE session SET live_log = @log WHERE entity_id = @id",
                new { log = CampaignLogJson.Serialize(log), id = live.EntityId }, transaction);
            return new SessionLogResult(SessionRef(checked((int)live.Number)), checked((int)live.Number), notes.Count, log.Count);
        });
    }

    /// <summary>
    /// Ends the live session: played, the recap as its body, attendance (replacing what was recorded) and next hooks, and
    /// the title when one is given; returns the checklist; after the commit, a session-end backup.
    /// </summary>
    /// <exception cref="DndInputException">No session is live, the context names another session, or an argument is invalid.</exception>
    public SessionWriteResult End(CampaignRow campaign, string recapMd, string? ingameEnd = null, IReadOnlyList<AttendanceSpec?>? attendance = null,
        IReadOnlyList<string>? nextHooks = null, WriteContext? context = null, string? title = null)
    {
        context ??= WriteContext.Default;
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(recapMd))
        {
            problems.Add("recap_md is required: what happened, in markdown (it becomes the session's body and the next summary's recap).");
        }

        CheckLine(problems, "title", title, CampaignLimits.MaxNameLength);
        CheckBody(problems, "recap_md", recapMd);
        CheckLine(problems, "ingame_end", ingameEnd, CampaignLimits.MaxLabelLength);
        problems.AddRange(AttendanceSpecs.Validate(attendance, "attendance"));
        CheckHooks(problems, nextHooks);
        DslProblems.ThrowIfAny(problems, Subject);
        var result = WriteBatch.Run(_database, campaign, context, "campaign_session/end", b =>
        {
            var live = b.Resolver.LiveSession() ??
                       throw WriteBatch.Problem(Subject, $"no session is live; to record a session after the fact use {WriteBatch.SessionCall(b.Campaign.Slug, "record_past")}.");
            if (b.Session is { } named && named.EntityId != live.EntityId)
            {
                throw WriteBatch.Problem(Subject, $"session {WriteBatch.Number(named.Number)} is not the live one (session {WriteBatch.Number(live.Number)} is); end ends the live session.");
            }

            var changes = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = SS.Played,
                ["ended_at"] = b.Recorder.At,
            };
            if (ingameEnd is not null)
            {
                changes["ingame_end"] = ingameEnd.Trim();
            }

            if (nextHooks is not null)
            {
                var hooks = new JsonArray(nextHooks.Select(h => (JsonNode?)JsonValue.Create(h.Trim())).ToArray());
                var data = JsonNode.Parse(live.Data) as JsonObject ?? new JsonObject();
                data["next_hooks"] = hooks;
                changes["data"] = data;
            }

            b.PlayerText.Entity(live.EntityId, null);
            var fields = b.Update("session", live.EntityId, changes, "end").ToList();
            var entity = new Dictionary<string, object?>
            {
                ["body_md"] = recapMd,
                ["visibility"] = CampaignValues.Visibilities.Party,
            };
            if (title is not null)
            {
                entity["name"] = title.Trim();
            }

            fields.AddRange(b.Update("entity", live.EntityId, entity, "end"));
            var attended = Attendance(b, live.EntityId, attendance, "end", replace: true);
            fields.AddRange(attended.Fields);
            b.Finish();
            var checklist = Checklist(b, live.EntityId, checked((int)live.Number), recapMd);
            return SessionResult(b, checked((int)live.Number), WriteOutcomes.Updated, fields, checklist, attended);
        });
        if (result.DryRun)
        {
            return result;
        }

        try
        {
            return result with { BackupPath = _database.Backups.Create(CampaignBackups.Reasons.SessionEnd) };
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "The session-end backup of campaigns.db at {Path} failed; the session was ended without it.", _database.Path);
            return result with
            {
                BackupProblem = $"The session-end backup could not be written to {_database.Backups.DirectoryPath}; the session is ended. Check that the directory is writable and the disk has space.",
            };
        }
    }

    /// <summary>
    /// Records a session played in the past (bootstrap, or one nobody started live), as one batch: created played, or a
    /// planned one marked played (keeping a planned title warns: class summary), or a played one's recap, title, date and
    /// attendance corrected (attendance given replaces the recorded list). Returns the checklist. With no number, the
    /// session start would start (the next to play). Filed under the session it records unless the context names one, even
    /// while another session is live.
    /// </summary>
    /// <exception cref="DndInputException">The session is live, or an argument is invalid.</exception>
    public SessionWriteResult RecordPast(CampaignRow campaign, int? number = null, string? title = null, string? playedOn = null, string? precision = null,
        string? recapMd = null, IReadOnlyList<AttendanceSpec?>? attendance = null, string? arc = null, string? ingame = null,
        WriteContext? context = null, string? confidence = null)
    {
        context ??= WriteContext.Default;
        var problems = new List<string>();
        CheckNumber(problems, number);
        CheckLine(problems, "title", title, CampaignLimits.MaxNameLength);
        var date = CheckDate(problems, playedOn, precision);
        CheckBody(problems, "recap_md", recapMd);
        problems.AddRange(AttendanceSpecs.Validate(attendance, "attendance"));
        CheckLine(problems, "ingame", ingame, CampaignLimits.MaxLabelLength);
        if (confidence is not null && !CampaignValues.Confidences.Set.TryMatch(confidence, out _))
        {
            problems.Add($"confidence \"{WriteBatch.Echo(confidence)}\" is not a confidence; give {CampaignValues.Confidences.Set.List}.");
        }

        DslProblems.ThrowIfAny(problems, Subject);
        SessionTarget? own = null;
        return WriteBatch.Run(_database, campaign, context, "campaign_session/record_past", b =>
        {
            var n = own?.Number ?? number ?? NextToStart(b.Connection, b.Transaction, b.Campaign.Id);
            var existing = b.Resolver.SessionByNumber(n);
            if (existing is { Status: SS.Live })
            {
                throw WriteBatch.Problem(Subject, $"session {WriteBatch.Number(n)} is live; end it with {WriteBatch.SessionCall(b.Campaign.Slug, "end")}.");
            }

            var arcId = arc is null ? null : b.RequireEntity(Subject, "arc", "handle", arc, CampaignValues.Kinds.Arc).Id;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["status"] = SS.Played };
            if (date is not null)
            {
                values["played_on"] = date.Date;
                values["played_on_precision"] = date.Precision;
            }
            else if (precision is not null)
            {
                values["played_on_precision"] = Canonical.Of(CampaignValues.DatePrecisions.Set, precision);
            }

            if (arcId is not null)
            {
                values["arc_id"] = arcId;
            }

            if (ingame is not null)
            {
                values["ingame_start"] = ingame.Trim();
            }

            var entity = new Dictionary<string, object?>(StringComparer.Ordinal) { ["visibility"] = CampaignValues.Visibilities.Party };
            if (recapMd is not null)
            {
                entity["body_md"] = recapMd;
            }

            if (confidence is not null)
            {
                entity["confidence"] = Canonical.Of(CampaignValues.Confidences.Set, confidence);
            }

            string outcome;
            List<string> fields;
            string sessionId;
            if (existing is null)
            {
                values.TryAdd("played_on_precision", CampaignValues.DatePrecisions.Unknown);
                sessionId = CreateSession(b, own?.EntityId, n, title, SS.Played, CampaignValues.Visibilities.Party, values, "record_past", entity);
                b.PlayerText.EntityCreated(sessionId, null);
                outcome = WriteOutcomes.Created;
                fields = ["session"];
            }
            else
            {
                sessionId = existing.EntityId;
                b.PlayerText.Entity(sessionId, null);
                if (title is not null)
                {
                    entity["name"] = title.Trim();
                }
                else
                {
                    WarnPlannedTitle(b, existing, "record_past");
                }

                fields = b.Update("session", sessionId, values, "record_past").ToList();
                fields.AddRange(b.Update("entity", sessionId, entity, "record_past"));
                outcome = fields.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated;
            }

            var attended = Attendance(b, sessionId, attendance, "record_past", replace: true);
            fields.AddRange(attended.Fields);
            if (outcome == WriteOutcomes.Unchanged && attended.Fields.Count > 0)
            {
                outcome = WriteOutcomes.Updated;
            }

            b.Finish();
            var body = b.EntityById(sessionId)!.BodyMd;
            return SessionResult(b, n, outcome, fields, Checklist(b, sessionId, n, body), attended);
        }, setup => own = OwnSession(setup, number));
    }

    /// <summary>
    /// The planned-title warning (class summary): a session no player view could see yet (planned, prepped or cancelled)
    /// whose name is not the default "Session n" is about to be shown to players under it. Quotes the title: the author's
    /// own prep, reported to the author, who has to recognise which title is now public.
    /// </summary>
    /// <param name="where">The action a title can still be passed to: end (after start), record_past.</param>
    private static void WarnPlannedTitle(WriteBatch b, SessionRow existing, string where)
    {
        if (existing.Status is SS.Played or SS.Live)
        {
            return;
        }

        var number = checked((int)existing.Number);
        var name = b.EntityById(existing.EntityId)!.Name;
        if (CampaignText.Key(name) == CampaignText.Key(DefaultTitle(number)))
        {
            return;
        }

        b.Warnings.Add(new WriteWarning(WarningKinds.PlannedTitle, WarningSeverities.Warning,
            $"{SessionRef(number)}'s planned title \"{name}\" is now visible to players; pass title to {where} to change it."));
    }

    /// <summary>A session's name when it has no title: "Session 12".</summary>
    private static string DefaultTitle(int number) => "Session " + WriteBatch.Number(number);

    /// <summary>Creates the session entity and its row; returns the entity id (<paramref name="id"/> when given: the id the batch is filed under).</summary>
    private static string CreateSession(WriteBatch b, string? id, int number, string? title, string status, string visibility,
        IReadOnlyDictionary<string, object?> sessionValues, string action, IReadOnlyDictionary<string, object?>? entityValues = null)
    {
        var slug = CampaignSlugs.ForSession(number);
        if (b.Resolver.TryEntity(new CampaignHandle.EntityBySlug(null, slug), includeDeleted: true) is { } holder)
        {
            var baseSlug = slug;
            for (var n = 2; b.Resolver.TryEntity(new CampaignHandle.EntityBySlug(null, slug), includeDeleted: true) is not null; n++)
            {
                slug = CampaignSlugs.WithSuffix(baseSlug, n);
            }

            b.Warnings.Add(new WriteWarning(WarningKinds.SlugCollision, WarningSeverities.Warning,
                $"slug {baseSlug} is taken by {holder.Handle} ({holder.SeqHandle}), so session {WriteBatch.Number(number)}'s entity is session:{slug}; session:{WriteBatch.Number(number)} still names it."));
        }

        var entity = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["campaign_id"] = b.Campaign.Id,
            ["kind"] = CampaignValues.Kinds.Session,
            ["slug"] = slug,
            ["name"] = string.IsNullOrWhiteSpace(title) ? DefaultTitle(number) : title.Trim(),
            ["visibility"] = visibility,
        };
        if (id is not null)
        {
            entity["id"] = id;
        }

        foreach (var (key, value) in entityValues ?? new Dictionary<string, object?>())
        {
            entity[key] = value;
        }

        var entityId = (string)b.Recorder.Insert("entity", entity, action)["id"]!;
        var row = new Dictionary<string, object?>(sessionValues, StringComparer.Ordinal)
        {
            ["entity_id"] = entityId,
            ["campaign_id"] = b.Campaign.Id,
            ["number"] = number,
            ["status"] = status,
        };
        b.Recorder.Insert("session", row, action);
        return entityId;
    }

    /// <summary>What an attendance write did: who was added, changed and removed, and the one changed-fields entry naming them.</summary>
    private sealed record AttendanceWrite(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed)
    {
        public static AttendanceWrite None { get; } = new([], [], []);

        /// <summary>"attendance (added character:aria; removed character:serif)", or nothing when nothing changed.</summary>
        public IReadOnlyList<string> Fields
        {
            get
            {
                var parts = new List<string>();
                foreach (var (verb, list) in new[] { ("added", Added), ("changed", Changed), ("removed", Removed) })
                {
                    if (list.Count > 0)
                    {
                        parts.Add($"{verb} {string.Join(", ", list)}");
                    }
                }

                return parts.Count == 0 ? [] : [$"attendance ({string.Join("; ", parts)})"];
            }
        }
    }

    /// <summary>
    /// Writes the attendance given (inserting or updating one row per character); with <paramref name="replace"/>, also
    /// removes the rows of characters the list leaves out (class summary), as logged deletes.
    /// </summary>
    private static AttendanceWrite Attendance(WriteBatch b, string sessionId, IReadOnlyList<AttendanceSpec?>? attendance, string action, bool replace)
    {
        if (attendance is null)
        {
            return AttendanceWrite.None;
        }

        var added = new List<string>();
        var changed = new List<string>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < attendance.Count; i++)
        {
            var item = attendance[i]!;
            var character = b.RequireEntity(Subject, $"attendance item {WriteBatch.Number(i + 1)}", "character", item.Character!, CampaignValues.Kinds.Character);
            listed.Add(character.Id);
            var match = new Dictionary<string, object?> { ["session_id"] = sessionId, ["character_id"] = character.Id };
            var values = new Dictionary<string, object?> { ["present"] = item.Present ?? true };
            if (item.Note is not null)
            {
                values["note"] = string.IsNullOrWhiteSpace(item.Note) ? null : item.Note.Trim();
            }

            var key = b.Recorder.FindKey("session_attendance", match);
            if (key is null)
            {
                b.Recorder.Insert("session_attendance", new Dictionary<string, object?>(match.Concat(values)), action);
                added.Add(character.Handle);
            }
            else if (b.Update("session_attendance", key, values, action).Count > 0 && !changed.Contains(character.Handle))
            {
                changed.Add(character.Handle);
            }
        }

        var removed = new List<string>();
        if (replace)
        {
            var rows = b.Connection.Query<(string CharacterId, string Handle)>(
                "SELECT a.character_id, e.kind || ':' || e.slug FROM session_attendance a JOIN entity e ON e.id = a.character_id " +
                "WHERE a.session_id = @sessionId ORDER BY e.seq", new { sessionId }, b.Transaction).ToList();
            foreach (var (characterId, handle) in rows.Where(r => !listed.Contains(r.CharacterId)))
            {
                b.Recorder.Delete("session_attendance", [sessionId, characterId], action);
                removed.Add(handle);
            }
        }

        return new AttendanceWrite(added, changed, removed);
    }

    // The session start or record_past writes, chosen before the batch opens so the batch can be filed under it: the
    // given number, else the next to play. A session that does not exist yet gets its entity id now.
    private static SessionTarget OwnSession(WriteBatchSetup setup, int? number)
    {
        var n = number ?? NextToStart(setup.Connection, setup.Transaction, setup.Campaign.Id);
        return new SessionTarget(setup.Resolver.SessionByNumber(n)?.EntityId ?? CampaignDatabase.NewId(), n);
    }

    // A new session to plan: after the highest number (contract §3.6).
    private static int NextNumber(SqliteConnection connection, SqliteTransaction transaction, string campaignId) =>
        checked((int)(connection.QueryFirstOrDefault<long?>(
            "SELECT max(number) FROM session WHERE campaign_id = @campaignId", new { campaignId }, transaction) + 1 ?? 1));

    // The next session to play (start, record_past with no number): the lowest planned or prepped session after the last
    // played one, else a new one after the highest. Plain "highest + 1" would start 13 while 12 sits planned, splitting
    // one night across two sessions; a planned session numbered below the last played one was skipped, not next.
    private static int NextToStart(SqliteConnection connection, SqliteTransaction transaction, string campaignId)
    {
        var lastPlayed = connection.QueryFirstOrDefault<long?>(
            "SELECT max(number) FROM session WHERE campaign_id = @campaignId AND status = @played",
            new { campaignId, played = SS.Played }, transaction) ?? -1;
        var planned = connection.QueryFirstOrDefault<long?>(
            "SELECT min(number) FROM session WHERE campaign_id = @campaignId AND status IN (@planned, @prepped) AND number > @lastPlayed",
            new { campaignId, planned = SS.Planned, prepped = SS.Prepped, lastPlayed }, transaction);
        return planned is { } next ? checked((int)next) : NextNumber(connection, transaction, campaignId);
    }

    private static SessionWriteResult SessionResult(WriteBatch b, int number, string outcome, IReadOnlyList<string> fields, SessionChecklist? checklist,
        AttendanceWrite? attended = null)
    {
        var status = b.Resolver.SessionByNumber(number)!.Status;
        return new SessionWriteResult(b.DryRun ? null : b.Context.BatchId, b.DryRun, SessionRef(number), number, status, outcome,
            fields.Distinct().ToList(), b.Warnings.ToList(), checklist, AttendanceAdded: attended?.Added ?? [],
            AttendanceRemoved: attended?.Removed ?? []);
    }

    private static string SessionRef(int number) => "session:" + WriteBatch.Number(number);

    // ---- the checklist ----------------------------------------------------------------------------------------------

    private static SessionChecklist Checklist(WriteBatch b, string sessionId, int number, string? recap)
    {
        var session = b.Resolver.SessionByNumber(number)!;
        var notes = (JsonNode.Parse(session.LiveLog) as JsonArray ?? [])
            .Select(n => n?["text"]?.GetValue<string>())
            .OfType<string>();
        var text = string.Join("\n", new[] { recap ?? string.Empty }.Concat(notes));
        return new SessionChecklist(
            UnknownNames(b, text),
            ClocksNotTicked(b, sessionId),
            FactsWithoutKnowers(b, sessionId),
            ClosedGateReveals(b, sessionId, number),
            ProposedInventions(b, sessionId),
            b.Connection.ExecuteScalar<long>("SELECT count(*) FROM session_attendance WHERE session_id = @sessionId", new { sessionId }, b.Transaction) == 0);
    }

    // Proper nouns matching no name here. Generous on purpose (a miss is an invention made canon by accident; a false
    // flag costs a glance): a candidate is known when it equals a name, alias or known_as (article optional), is a
    // whole-word part of one ("Silverwind" of "Belmakor Silverwind"), or holds names and nothing else but connectors,
    // titles and a sentence's opening word ("Hail Belmakor", "Captain Varro"; "Belmakor Shadowfang" is a new name). The
    // rule is KnownNames, shared with the knowledge check's capitalised words that match no name (review UR01), so the
    // two never disagree on one text.
    private static IReadOnlyList<string> UnknownNames(WriteBatch b, string text)
    {
        var candidates = ProperNouns.Runs(text);
        if (candidates.Count == 0)
        {
            return [];
        }

        var known = new KnownNames(b.Connection.Query<string>(
            "SELECT name FROM entity WHERE campaign_id = @campaignId AND deleted_at IS NULL " +
            "UNION SELECT a.alias FROM entity_alias a JOIN entity e ON e.id = a.entity_id WHERE e.campaign_id = @campaignId AND e.deleted_at IS NULL " +
            "UNION SELECT known_as FROM knowledge WHERE campaign_id = @campaignId AND known_as IS NOT NULL",
            new { campaignId = b.Campaign.Id }, b.Transaction));
        return candidates.Where(c => !known.Covers(c)).Select(c => c.Surface).ToList();
    }

    private static IReadOnlyList<string> ClocksNotTicked(WriteBatch b, string sessionId) =>
        b.Connection.Query<string>(
            "SELECT e.kind || ':' || e.slug FROM entity e JOIN clock c ON c.entity_id = e.id " +
            "WHERE e.campaign_id = @campaignId AND e.status = @running AND e.deleted_at IS NULL AND NOT EXISTS (" +
            "SELECT 1 FROM change_log l WHERE l.campaign_id = @campaignId AND l.target_table = 'clock' AND l.target_id = e.id " +
            "AND l.field_path = 'filled' AND l.session_id = @sessionId) ORDER BY e.seq",
            new { campaignId = b.Campaign.Id, sessionId, running = CampaignValues.Statuses.ClockRunning }, b.Transaction).ToList();

    private static IReadOnlyList<string> FactsWithoutKnowers(WriteBatch b, string sessionId)
    {
        var knowerKinds = CampaignValues.KnowerKinds.Set.Values.Where(b.IsNonAuthorKnower).ToArray();
        return b.Connection.Query<long>(
                "SELECT f.seq FROM fact f WHERE f.campaign_id = @campaignId AND f.established_session_id = @sessionId AND f.deleted_at IS NULL " +
                "AND f.visibility IN @unshared AND NOT EXISTS (SELECT 1 FROM knowledge k WHERE k.fact_id = f.id " +
                "AND k.knower_kind IN @knowerKinds AND k.state IN @aware) ORDER BY f.seq",
                new
                {
                    campaignId = b.Campaign.Id,
                    sessionId,
                    unshared = new[] { CampaignValues.Visibilities.Restricted, CampaignValues.Visibilities.Author },
                    knowerKinds,
                    aware = CampaignValues.KnowledgeStates.AwareStates.ToArray(),
                }, b.Transaction)
            .Select(seq => "f:" + WriteBatch.Number(seq))
            .ToList();
    }

    private static IReadOnlyList<string> ClosedGateReveals(WriteBatch b, string sessionId, int number)
    {
        var rows = b.Connection.Query<(string FactId, string KnowerKind, string? KnowerId)>(
            "SELECT k.fact_id, k.knower_kind, k.knower_id FROM knowledge k JOIN fact f ON f.id = k.fact_id " +
            "WHERE k.campaign_id = @campaignId AND k.learned_session_id = @sessionId AND k.state IN @aware AND f.gate IS NOT NULL AND f.deleted_at IS NULL",
            new { campaignId = b.Campaign.Id, sessionId, aware = CampaignValues.KnowledgeStates.AwareStates.ToArray() }, b.Transaction).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var evaluation = new GateEvaluation(b);
        var result = new List<string>();
        foreach (var (factId, knowerKind, knowerId) in rows)
        {
            if (!b.IsNonAuthorKnower(knowerKind) || !evaluation.Gates.ContainsKey(factId) || evaluation.Status(factId, number).Ready)
            {
                continue;
            }

            var who = knowerId is null ? knowerKind : "character:" + (b.EntityById(knowerId)?.Slug ?? knowerId);
            result.Add($"{evaluation.Ref(factId)} to {who}");
        }

        return result.Distinct().ToList();
    }

    private static IReadOnlyList<string> ProposedInventions(WriteBatch b, string sessionId)
    {
        var args = new { campaignId = b.Campaign.Id, sessionId, proposed = CampaignValues.CanonStatuses.Proposed, create = CampaignValues.ChangeOps.Create };
        var entities = b.Connection.Query<(string? Code, string Kind, string Slug)>(
            "SELECT e.code, e.kind, e.slug FROM entity e WHERE e.campaign_id = @campaignId AND e.canon_status = @proposed AND e.deleted_at IS NULL " +
            "AND EXISTS (SELECT 1 FROM change_log l WHERE l.target_table = 'entity' AND l.target_id = e.id AND l.op = @create AND l.session_id = @sessionId) ORDER BY e.seq",
            args, b.Transaction).Select(e => $"{e.Code ?? "(no code)"} {e.Kind}:{e.Slug}");
        var facts = b.Connection.Query<(string? Code, long Seq)>(
            "SELECT f.code, f.seq FROM fact f WHERE f.campaign_id = @campaignId AND f.canon_status = @proposed AND f.deleted_at IS NULL " +
            "AND EXISTS (SELECT 1 FROM change_log l WHERE l.target_table = 'fact' AND l.target_id = f.id AND l.op = @create AND l.session_id = @sessionId) ORDER BY f.seq",
            args, b.Transaction).Select(f => $"{f.Code ?? "(no code)"} f:{WriteBatch.Number(f.Seq)}");
        return entities.Concat(facts).ToList();
    }

    // ---- validation -------------------------------------------------------------------------------------------------

    private sealed record PlayedOn(string Date, string Precision);

    private static void CheckNumber(List<string> problems, int? number)
    {
        if (number is < 0 or > CampaignLimits.MaxSessionNumber)
        {
            problems.Add($"session is {WriteBatch.Number(number.Value)}; it is a session number, 0 to {WriteBatch.Number(CampaignLimits.MaxSessionNumber)}.");
        }
    }

    private static void CheckLine(List<string> problems, string field, string? text, int max)
    {
        if (text is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            problems.Add($"{field} is blank; give text or leave it out.");
        }
        else if (text.Trim().Length > max || text.Any(char.IsControl))
        {
            problems.Add($"{field} must be one line of at most {WriteBatch.Number(max)} characters.");
        }
    }

    private static void CheckBody(List<string> problems, string field, string? text)
    {
        if (text is { Length: > CampaignLimits.MaxBodyLength })
        {
            problems.Add($"{field} is {WriteBatch.Number(text.Length)} characters; at most {WriteBatch.Number(CampaignLimits.MaxBodyLength)}.");
        }
    }

    private static void CheckHooks(List<string> problems, IReadOnlyList<string>? hooks)
    {
        if (hooks is null)
        {
            return;
        }

        if (hooks.Count > MaxNextHooks)
        {
            problems.Add($"next_hooks has {WriteBatch.Number(hooks.Count)} items; at most {WriteBatch.Number(MaxNextHooks)}.");
        }

        for (var i = 0; i < hooks.Count; i++)
        {
            CheckLine(problems, $"next_hooks item {WriteBatch.Number(i + 1)}", hooks[i] ?? string.Empty, CampaignLimits.MaxNoteLength);
        }
    }

    /// <summary>
    /// A played-on date: yyyy-MM-dd (precision day), yyyy-MM (month) or yyyy (approx) unless a precision is given; null
    /// when none is given.
    /// </summary>
    private static PlayedOn? CheckDate(List<string> problems, string? playedOn, string? precision)
    {
        string? canonicalPrecision = null;
        if (precision is not null && !CampaignValues.DatePrecisions.Set.TryMatch(precision, out canonicalPrecision))
        {
            problems.Add($"precision \"{WriteBatch.Echo(precision)}\" is not a date precision; give {CampaignValues.DatePrecisions.Set.List}.");
        }

        if (playedOn is null)
        {
            return null;
        }

        var text = playedOn.Trim();
        var match = DatePattern().Match(text);
        var valid = match.Success && DateOnly.TryParseExact(
            text + (match.Groups["month"].Success ? string.Empty : "-01") + (match.Groups["day"].Success ? string.Empty : "-01"),
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (!valid)
        {
            problems.Add($"played_on \"{WriteBatch.Echo(playedOn)}\" is not a date: give yyyy-MM-dd, yyyy-MM or yyyy, e.g. \"2026-08-28\" or \"2026-08\" with precision approx.");
            return null;
        }

        var implied = match.Groups["day"].Success ? CampaignValues.DatePrecisions.Day
            : match.Groups["month"].Success ? CampaignValues.DatePrecisions.Month
            : CampaignValues.DatePrecisions.Approx;
        return new PlayedOn(text, canonicalPrecision ?? implied);
    }

    [GeneratedRegex("^[0-9]{4}(?<month>-[0-9]{2}(?<day>-[0-9]{2})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}
