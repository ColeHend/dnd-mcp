using System.ComponentModel;
using System.Globalization;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign_session</c>: a campaign's sessions, from planning through the night itself to the recap. Reads go through
/// <see cref="SessionReader"/> (with the caller's perspective), writes through <see cref="SessionWriter"/>; rendering is
/// <see cref="SessionMarkdown"/>.
///
/// <para>
/// <b>The live session is the context of every other write.</b> <c>start</c> makes a session live; until <c>end</c>, a
/// <c>campaign_write</c> or <c>campaign_knowledge</c> call without a session belongs to it (its change_log session, the
/// learned session of knowledge it writes). So <c>end</c> is what closes the night: it writes the recap, marks the
/// session played, takes the session-end backup and returns the checklist of loose ends.
/// </para>
/// <para>
/// <b><c>session</c> is the session the action is about</b> (a number): the one to plan, start or record for those
/// actions; the one to read for get and recap (default: the live session, else the last played); for end, when given, it
/// must be the live one (end never ends another session by mistake). <c>log</c> always appends to the live session and
/// takes no session, reason or dry_run: the live log is a scratchpad outside the history, so there is nothing to preview or
/// undo.
/// </para>
/// <para>
/// <b>start and end of a campaign named explicitly make it this session's current campaign</b> (and say so), as
/// <c>campaign use</c> would. The live session is the context of every write without a session and the log of every
/// <c>dice_roll</c>, but both follow the current campaign: a user with two campaigns who starts belmakor's session while
/// one-piece is current would otherwise have every roll of the night left out of belmakor's log, and every write without
/// <c>campaign</c> filed in one-piece, while the start result said the session was the context of the writes. The prompts
/// tell the model to pass <c>campaign</c> on every call, so this is the normal path, not an edge. Only this process's
/// current campaign changes (not the persisted active one another session starts from); a dry run changes nothing but
/// says the campaign would become current, and a start or end without <c>campaign</c> (which went to the campaign calls
/// already use) changes nothing and says nothing.
/// </para>
/// <para>
/// <b>A session number below 0 is refused before it becomes a handle.</b> get and recap pass the number to the reader as
/// text, and "-1" read as a handle is the slug "1": the refusal would name session 1, which may exist.
/// </para>
/// <para>
/// <b>Annotations: not destructive</b> (nothing is deleted: a played session is corrected by record_past, never removed),
/// not idempotent (start then start again is refused; log appends), closed-world.
/// </para>
/// </summary>
public sealed class CampaignSessionTools
{
    internal const string Example =
        "{\"action\": \"end\", \"campaign\": \"belmakor\", \"recap_md\": \"The band played the Sky Fair; Serif bargained with the " +
        "harbourmaster.\", \"attendance\": [{\"character\": \"character:belmakor\"}, {\"character\": \"character:serif\", \"present\": false}]}";

    private const string List = "list";
    private const string Get = "get";
    private const string Recap = "recap";
    private const string Plan = "plan";
    private const string Start = "start";
    private const string Log = "log";
    private const string End = "end";
    private const string RecordPast = "record_past";

    private static readonly ActionArgumentCheck Actions = new(
        "campaign_session",
        new DslValueSet("campaign_session action", [List, Get, Plan, Start, Log, End, Recap, RecordPast]),
        new Dictionary<string, ActionArguments>(StringComparer.Ordinal)
        {
            [List] = new(["status", "limit", "cursor", "perspective", "campaign"], "{\"action\": \"list\", \"status\": \"played\", \"limit\": 10}"),
            [Get] = new(["session", "perspective", "campaign"], "{\"action\": \"get\", \"session\": 12, \"perspective\": \"party\"}"),
            [Recap] = new(["session", "campaign"], "{\"action\": \"recap\", \"session\": 12}"),
            [Plan] = new(["session", "title", "arc", "prep_md", "played_on", "reason", "dry_run", "campaign"],
                "{\"action\": \"plan\", \"session\": 13, \"title\": \"The harbourmaster's price\", \"prep_md\": \"- Open on the docks\"}"),
            [Start] = new(["session", "played_on", "precision", "attendance", "ingame", "title", "reason", "dry_run", "campaign"],
                "{\"action\": \"start\", \"attendance\": [{\"character\": \"character:serif\"}]}"),
            [Log] = new(["notes", "campaign"], "{\"action\": \"log\", \"notes\": [\"Serif bargains with the harbourmaster\"]}"),
            [End] = new(["session", "recap_md", "attendance", "ingame_end", "next_hooks", "title", "reason", "dry_run", "campaign"], Example),
            [RecordPast] = new(
            [
                "session", "title", "played_on", "precision", "recap_md", "attendance", "arc", "ingame", "confidence", "reason", "dry_run",
                "campaign",
            ], "{\"action\": \"record_past\", \"session\": 11, \"played_on\": \"2026-09-05\", \"recap_md\": \"The band reached Flotsam.\"}"),
        },
        Example);

    private readonly CampaignService _campaigns;
    private readonly ILogger<SessionWriter> _logger;

    public CampaignSessionTools(CampaignService campaigns, ILogger<SessionWriter> logger)
    {
        _campaigns = campaigns;
        _logger = logger;
    }

    // Not destructive: sessions are corrected, never deleted. Not idempotent: start refuses a second live session and log
    // appends. Closed-world.
    [McpServerTool(Name = "campaign_session", Title = "Campaign sessions", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "A campaign's sessions: plan them, run the live one, end it with a recap, read them back. Actions and their arguments:\n" +
        "- list: status, limit (1-50, default 15), cursor, perspective.\n" +
        "- get: session (a number; default the live session, else the last played), perspective: recap, attendance, dice; " +
        "the author also sees the next hooks, prep, the live log and what changed.\n" +
        "- recap: session: the recap with the facts it established and who learned what (author).\n" +
        "- plan: session (default the next number), title, arc, prep_md, played_on.\n" +
        "- start: session (default the next to play), played_on, precision, attendance, ingame, title. While it is live, " +
        "campaign writes without a session belong to it.\n" +
        "- log: notes (a list), appended to the live session's log (a scratchpad; not undoable).\n" +
        "- end: recap_md (required), attendance, ingame_end, next_hooks, title; session, if given, must be the live one. Marks it " +
        "played, backs up, and returns a checklist: unknown names, clocks not ticked, facts nobody learned, gated reveals, " +
        "inventions, attendance.\n" +
        "- record_past: session, title, played_on, precision, recap_md, attendance, arc, ingame, confidence: a session played " +
        "earlier (or a correction to one), in one batch.\n" +
        "attendance is [{character, present (default true), note}] (end and record_past replace the list recorded); a title is " +
        "shown to players from start; played_on is yyyy-MM-dd (or yyyy-MM, yyyy); precision day, " +
        "month, approx or unknown. plan, start, end and record_past take reason and dry_run and give a batch id for " +
        "campaign_history undo. Every action takes campaign (default: the active one).\n" +
        "Example: " + Example)]
    public string Session(
        [Description("list, get, plan, start, log, end, recap or record_past.")] string action,
        [Description("The campaign's slug, e.g. \"belmakor\". Default: the one chosen with campaign use, else the only one.")]
        string? campaign = null,
        [Description("The session's number, e.g. 12: the one to plan, start or record; to read (get, recap); for end, the live one.")]
        int? session = null,
        [Description("plan, start, end, record_past: the session's title, one line. Players see it once the session is live or played.")]
        string? title = null,
        [Description("plan, record_past: the arc it belongs to, e.g. \"arc:sky-fair\".")]
        string? arc = null,
        [Description("plan: the prep notes / run-sheet, in markdown (author only).")]
        [AIParameterName("prep_md")] string? prepMd = null,
        [Description("plan, start, record_past: the real-world date, yyyy-MM-dd (or yyyy-MM, yyyy), e.g. \"2026-09-12\". start defaults to today.")]
        [AIParameterName("played_on")] string? playedOn = null,
        [Description("start, record_past: how exact played_on is: day, month, approx or unknown.")]
        string? precision = null,
        [Description("start, end, record_past: who was at the table, e.g. [{\"character\": \"character:serif\", \"present\": false}]. Not given: not recorded.")]
        AttendanceSpec[]? attendance = null,
        [Description("start, record_past: the in-game date or time at the start, one line.")]
        string? ingame = null,
        [Description("end: the in-game date or time at the end, one line.")]
        [AIParameterName("ingame_end")] string? ingameEnd = null,
        [Description("log: notes to append to the live session's log, e.g. [\"Serif bargains with the harbourmaster\"].")]
        string[]? notes = null,
        [Description("end (required), record_past: what happened, in markdown; it becomes the session's recap.")]
        [AIParameterName("recap_md")] string? recapMd = null,
        [Description("end: hooks for next session, one line each.")]
        [AIParameterName("next_hooks")] string[]? nextHooks = null,
        [Description("record_past: how sure the record is: confirmed, approximate, reconstructed or unverified.")]
        string? confidence = null,
        [Description("list: only sessions with this status: planned, prepped, live, played or cancelled.")]
        string? status = null,
        [Description("list: most sessions to return, 1-50. Default 15.")]
        int? limit = null,
        [Description("list: the cursor from the previous page.")]
        string? cursor = null,
        [Description("list, get: whose view: \"author\" (default), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\".")]
        string? perspective = null,
        [Description("plan, start, end, record_past: why, kept in the history.")]
        string? reason = null,
        [Description("plan, start, end, record_past: true to preview; nothing is written. Default false.")]
        [AIParameterName("dry_run")] bool? dryRun = null)
    {
        var name = Actions.Action(action);
        Actions.Refuse(name,
            ("session", session is not null), ("title", title is not null), ("arc", arc is not null), ("prep_md", prepMd is not null),
            ("played_on", playedOn is not null), ("precision", precision is not null), ("attendance", attendance is not null),
            ("ingame", ingame is not null), ("ingame_end", ingameEnd is not null), ("notes", notes is not null),
            ("recap_md", recapMd is not null), ("next_hooks", nextHooks is not null), ("confidence", confidence is not null),
            ("status", status is not null), ("limit", limit is not null), ("cursor", cursor is not null),
            ("perspective", perspective is not null), ("reason", reason is not null), ("dry_run", dryRun is not null));

        var row = _campaigns.Resolve(campaign);
        var named = !string.IsNullOrWhiteSpace(campaign);
        var writer = new SessionWriter(_campaigns.Database, _logger);
        var context = WriteContext.For(null, reason, dryRun ?? false);
        switch (name)
        {
            case List:
            {
                var (view, banner) = View(row, perspective);
                var page = new SessionReader(_campaigns.Database).List(row, status, limit, cursor, view);
                return SessionMarkdown.FormatList(row, page, banner, status);
            }

            case Get:
            {
                var (view, banner) = View(row, perspective);
                var reader = new SessionReader(_campaigns.Database);
                var detail = reader.Get(row, session is { } n ? Number(n) : Default(reader, row, view), view);
                return SessionMarkdown.FormatGet(row, detail, banner);
            }

            case Recap:
            {
                var reader = new SessionReader(_campaigns.Database);
                return SessionMarkdown.FormatRecap(row, reader.Recap(row, session is { } n ? Number(n) : Default(reader, row, Perspective.Author)));
            }

            case Plan:
                return SessionMarkdown.FormatWrite(row, name, writer.Plan(row, session, title, arc, prepMd, playedOn, context));
            case Start:
            {
                var started = writer.Start(row, session, playedOn, precision, attendance, ingame, context, title);
                return SessionMarkdown.FormatWrite(row, name, started, MakeCurrent(row, named, started.DryRun));
            }

            case Log:
                return SessionMarkdown.FormatLog(row, writer.Log(row, Strings(notes)));
            case End:
            {
                // The session given to end is the batch's context: the writer refuses it unless it is the live one.
                var ended = writer.End(row, recapMd ?? string.Empty, ingameEnd, attendance,
                    nextHooks is null ? null : Strings(nextHooks), WriteContext.For(session, reason, dryRun ?? false), title);
                return SessionMarkdown.FormatWrite(row, name, ended, MakeCurrent(row, named, ended.DryRun));
            }

            default:
                return SessionMarkdown.FormatWrite(row, name, writer.RecordPast(row, session, title, playedOn, precision, recapMd, attendance,
                    arc, ingame, context, confidence));
        }
    }

    /// <summary>
    /// The perspective and the banner a non-author view starts with. Resolving it here (the reader resolves it again) is
    /// what gives the banner the perspective's own character's name, and refuses an unknown character before any read.
    /// </summary>
    private (Perspective View, string? Banner) View(CampaignRow campaign, string? perspective)
    {
        var view = Perspective.Parse(perspective);
        if (view.IsAuthor)
        {
            return (view, null);
        }

        using var connection = _campaigns.Database.TryOpenExisting();
        if (connection is null)
        {
            return (view, CampaignMarkdownText.Banner(view, authorView: false));
        }

        var context = new KnowledgeLoader(connection, campaign).Resolve(view);
        return (view, CampaignMarkdownText.Banner(context.Perspective, context.IsAuthorView, context.CharacterName, typed: view));
    }

    /// <summary>
    /// Makes <paramref name="campaign"/> this process's current campaign after a start or end that named it, when it is not
    /// already (class summary); true when it changed, or in a dry run would have, so the result says so. A dry run changes
    /// nothing, but says what the real call would do (its banner promises exactly that). A start or end without
    /// <c>campaign</c> went to the campaign calls already use, so it changes nothing and says nothing.
    /// </summary>
    private bool MakeCurrent(CampaignRow campaign, bool named, bool dryRun)
    {
        if (!named || _campaigns.CurrentCampaignId == campaign.Id)
        {
            return false;
        }

        if (!dryRun)
        {
            _campaigns.SetCurrent(campaign.Id);
        }

        return true;
    }

    // get and recap with no session: the live one when a session is live (for this view), else the last played.
    private static string Default(SessionReader reader, CampaignRow campaign, Perspective view) =>
        reader.List(campaign, CampaignValues.SessionStatuses.Live, 1, null, view).Total > 0 ? "session:live" : "session:last";

    // The session get and recap read, as the reader takes it; a number out of range is refused here (class summary), with
    // the words plan and start use for it.
    private static string Number(int number) => number is < 0 or > CampaignLimits.MaxSessionNumber
        ? throw DslProblems.Exception(
            [$"session is {number.ToString(CultureInfo.InvariantCulture)}; it is a session number, 0 to " +
             $"{CampaignLimits.MaxSessionNumber.ToString(CultureInfo.InvariantCulture)}."], "session")
        : number.ToString(CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> Strings(string?[]? values) => values?.Select(v => v ?? string.Empty).ToList() ?? [];
}
