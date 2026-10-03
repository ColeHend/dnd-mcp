using System.Globalization;
using System.Text;
using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// <c>campaign_session</c>'s results: the list, one session, the recap, the writes (plan, start, end, record_past) with
/// the end checklist, and a live-log append.
///
/// <para>
/// <b>Reads render what the reader returned, nothing more.</b> <see cref="SessionReader"/> already applies the
/// perspective (unplayed sessions, the live log, prep, secret dice and "what changed" are the author's), so a non-author
/// view here starts with the perspective banner and has no author section at all: no empty "Prep" heading, no "3 secret
/// rolls" line. A heading that exists only to say something is hidden would itself tell the table there is something.
/// </para>
/// <para>
/// <b>The end checklist is the point of <c>end</c>.</b> A recap leaves loose ends the database cannot close by itself: names
/// that match nothing (inventions or typos), clocks nobody ticked, facts established that no player-side knower holds,
/// gated facts that reached a knower with the gate closed, proposed inventions to accept or strike, and attendance not
/// recorded (which makes every party reveal of the session read as "attendance not recorded"). Each item says what to do
/// with it; an empty checklist says so rather than printing nothing.
/// </para>
/// <para>
/// <b>The next hooks come back with the session.</b> <c>end</c> stores them in the session's data (author-only), and
/// <c>session_prep</c> starts by reading them through <c>get</c>; a get that dropped them would lose the one thing the
/// last session left for the next, with nothing but one line of history to recover it from. They follow the recap, for the
/// author only (a non-author view gets no heading at all).
/// </para>
/// <para>
/// <b>Every call printed names its campaign</b> (the undo line, the recap and history pointers, the plan hint): a call
/// printed without <c>campaign</c> goes to whatever campaign is current when the model sends it, so it fails, or reads or
/// writes the wrong campaign, as soon as another one is current. A pointer to one session names that session too: the
/// recap action with no session reads the live or last session, which is not the one being shown. What only the author
/// does or reads is never suggested to a player view: planning, and the recap action (it lists what the session
/// established and who learned what, so it takes no perspective); a player view's get says where its recap was cut and
/// points nowhere, as a non-author view is pointed only at its own knowledge page.
/// </para>
/// <para>
/// <b>Bounded per section</b> (a recap can be 50,000 characters, a live log 2,000 notes, a session hundreds of rolls and
/// batches), so the parts after a long recap are not lost to the overall cap. In <c>get</c> the recap is cut at
/// <see cref="MaxRecap"/> and every list after it stops at its own count or at <see cref="Budget"/>, saying how many more
/// there are. In <c>recap</c> the lists are what the action is for (what the session established and who learned what),
/// so they are budgeted first (within <see cref="MaxRecapLists"/>, printed after the recap) and the recap text gets whatever
/// room is left under the cap: a recap that runs past it is cut at a line with the cut said, instead of pushing both lists
/// out of the result.
/// </para>
/// </summary>
internal static class SessionMarkdown
{
    /// <summary>Characters of a recap shown by get (the recap action shows it up to the room its lists leave).</summary>
    public const int MaxRecap = 12_000;

    /// <summary>Characters the recap action's lists (attendance, facts established, learned) may take before they count the rest.</summary>
    public const int MaxRecapLists = 12_000;

    /// <summary>
    /// Where get's lists after the recap (attendance, dice, the live log, what changed) stop adding entries, so the
    /// "… and N more" lines and the cap's own note still fit under <see cref="CampaignMarkdownText.MaxChars"/>.
    /// </summary>
    public const int Budget = CampaignMarkdownText.MaxChars - 1_500;

    /// <summary>Characters of the prep notes shown by get.</summary>
    public const int MaxPrep = 5_000;

    /// <summary>Dice rolls, live-log notes and changed batches shown by get.</summary>
    public const int MaxDice = 30;

    public const int MaxLogNotes = 40;

    /// <summary>Next hooks shown (end accepts at most 20).</summary>
    public const int MaxHooks = 20;

    public const int MaxBatches = 20;

    /// <summary>Changes listed per batch in get's "what changed".</summary>
    public const int MaxChangesPerBatch = 8;

    /// <summary>Items per checklist entry and per recap list, attendees shown, and knowers named per recap fact.</summary>
    public const int MaxItems = 40;

    /// <summary>Knowers named on one recap fact before the rest are counted (a fact can have 30 knowers).</summary>
    public const int MaxKnowers = 8;

    /// <summary>Characters of one value in a checklist entry (a name, a clock, a fact) before it is cut.</summary>
    public const int MaxItemChars = 200;

    /// <summary>Characters of one checklist entry after which no more values are added (the rest are counted).</summary>
    public const int MaxEntryChars = 3_000;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// A page of sessions. <paramref name="banner"/> is null for the author view only, which is also the only view told
    /// how to plan one: a player view with no session to see has nothing to do about it. <paramref name="status"/> is the
    /// filter as the caller typed it, printed as the reader matched it (<see cref="ListedStatus"/>).
    /// </summary>
    public static string FormatList(CampaignRow campaign, SessionPage page, string? banner, string? status)
    {
        var listed = ListedStatus(status);
        var b = new StringBuilder();
        b.Append("# Sessions: ").Append(campaign.Slug).Append(" (").Append(page.Total.ToString(Invariant))
            .Append(listed is null ? string.Empty : " " + listed).Append(")\n\n");
        AppendBanner(b, banner);
        if (page.Sessions.Count == 0)
        {
            b.Append(page.Total > 0 ? "No more sessions on this page.\n"
                : listed is not null ? $"No {listed} sessions.\n"
                : banner is not null ? "No sessions yet.\n"
                : $"No sessions yet. campaign_session {{\"action\": \"plan\", \"campaign\": \"{campaign.Slug}\"}} plans one; \"start\" starts one; " +
                  "\"record_past\" records one played earlier.\n");
            return b.ToString();
        }

        b.Append("| Session | Title | Status | Played on | Arc |\n|---|---|---|---|---|\n");
        foreach (var session in page.Sessions)
        {
            b.Append("| ").Append(session.Ref)
                .Append(" | ").Append(CampaignMarkdownText.Cell(session.Title))
                .Append(" | ").Append(session.Status)
                .Append(" | ").Append(CampaignMarkdownText.Cell(PlayedOn(session)))
                .Append(" | ").Append(CampaignMarkdownText.Cell(session.Arc?.Ref))
                .Append(" |\n");
        }

        if (page.NextCursor is { } cursor)
        {
            b.Append("\nMore: pass cursor \"").Append(cursor).Append("\" for the next page.\n");
        }

        return CampaignMarkdownText.Cap(b.ToString(), "use a smaller limit and the cursor");
    }

    /// <summary>
    /// The status filter as the reader applied it: null when none was given or it is blank (the reader then filters
    /// nothing, so the list is every session, and the author is told how to plan one), else the status the reader matched
    /// ("PLANNED" lists the planned sessions and is printed "planned"). Echoing the caller's text printed "No  sessions."
    /// for a blank filter, dropping the plan hint, and the caller's own spelling for a forgiving match.
    /// </summary>
    internal static string? ListedStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? null
        : CampaignValues.SessionStatuses.Set.TryMatch(status, out var matched) ? matched
        : OneLine(status);

    /// <summary>
    /// One session as the perspective sees it. <paramref name="banner"/> is null for the author view only, the one view
    /// pointed at the recap action for the rest of a cut recap (class summary).
    /// </summary>
    public static string FormatGet(CampaignRow campaign, SessionDetail detail, string? banner)
    {
        var session = detail.Session;
        var b = new StringBuilder();
        b.Append("# Session ").Append(session.Number.ToString(Invariant)).Append(TitleSuffix(session))
            .Append(" (").Append(session.Status).Append(")\n\n");
        AppendBanner(b, banner);
        b.Append(session.Ref).Append(" · ").Append(campaign.Slug);
        if (PlayedOn(session) is { } playedOn)
        {
            b.Append(" · played on ").Append(playedOn);
        }

        if (session.Arc is { } arc)
        {
            b.Append(" · arc ").Append(arc.Ref).Append(" (").Append(OneLine(arc.Name)).Append(')');
        }

        b.Append('\n');
        if (detail.IngameStart is not null || detail.IngameEnd is not null)
        {
            b.Append("In game: ").Append(OneLine(detail.IngameStart ?? "?")).Append(" → ").Append(OneLine(detail.IngameEnd ?? "?")).Append('\n');
        }

        b.Append("\n## Recap\n");
        b.Append(detail.Recap is { } recap
            ? Bounded(recap, MaxRecap, banner is null ? RecapCall(campaign, session.Number) + " shows more" : null)
            : "_No recap yet._").Append('\n');
        if (detail.Author is { } withHooks)
        {
            AppendNextHooks(b, withHooks.Data);
        }

        b.Append("\n## Attendance\n");
        if (detail.Attendance.Count == 0)
        {
            b.Append(detail.AttendanceRecorded ? "_Recorded, with no one this view can name._\n" : "_Not recorded._\n");
        }
        else
        {
            var shown = 0;
            foreach (var attendee in detail.Attendance.Take(MaxItems).TakeWhile(_ => b.Length < Budget))
            {
                shown++;
                b.Append("- ").Append(OneLine(attendee.Character.Name)).Append(" (").Append(attendee.Character.Ref).Append("): ")
                    .Append(attendee.Present ? "present" : "absent")
                    .Append(string.IsNullOrWhiteSpace(attendee.Note) ? string.Empty : " — " + CampaignMarkdownText.Excerpt(OneLine(attendee.Note), 200)).Append('\n');
            }

            WriteMarkdown.AppendMore(b, shown, detail.Attendance.Count, "attendees");
        }

        if (detail.DiceRollsTotal > 0)
        {
            AppendDice(b, detail);
        }

        if (detail.Author is { } author)
        {
            AppendAuthor(b, author, session.Number, campaign.Slug);
        }

        return CampaignMarkdownText.Cap(b.ToString().TrimEnd() + "\n",
            banner is null ? RecapCall(campaign, session.Number) + " gives the recap on its own" : "the rest of this session is not shown");
    }

    /// <summary>
    /// The recap call for one session of one campaign (class summary: without the session it reads the live or last one,
    /// without the campaign whichever is current).
    /// </summary>
    private static string RecapCall(CampaignRow campaign, int session) =>
        $"campaign_session {{\"action\": \"recap\", \"session\": {session.ToString(Invariant)}, \"campaign\": \"{campaign.Slug}\"}}";

    /// <summary>
    /// The recap with what the session established and who learned what (author view). The lists come first in the budget
    /// (see the class summary): the recap text gets the room they leave.
    /// </summary>
    public static string FormatRecap(CampaignRow campaign, SessionRecap recap)
    {
        var session = recap.Session;
        var head = new StringBuilder();
        head.Append("# Recap of session ").Append(session.Number.ToString(Invariant)).Append(TitleSuffix(session))
            .Append(" (").Append(campaign.Slug).Append(")\n\n");
        head.Append(session.Ref).Append(" · ").Append(session.Status);
        if (PlayedOn(session) is { } playedOn)
        {
            head.Append(" · played on ").Append(playedOn);
        }

        head.Append("\n\n");
        var lists = new StringBuilder();
        if (recap.Attendance.Count > 0)
        {
            var attendees = recap.Attendance.Take(MaxItems).Select(a => $"{CampaignMarkdownText.Excerpt(OneLine(a.Character.Name), 80)} ({(a.Present ? "present" : "absent")})");
            lists.Append("\n## Attendance\n").Append(string.Join(", ", attendees))
                .Append(recap.Attendance.Count > MaxItems ? $", … and {(recap.Attendance.Count - MaxItems).ToString(Invariant)} more" : string.Empty).Append('\n');
        }

        lists.Append("\n## Facts established (").Append(recap.FactsEstablished.Count.ToString(Invariant)).Append(")\n");
        if (recap.FactsEstablished.Count == 0)
        {
            lists.Append("_None._\n");
        }

        // Each list stops at half the lists' budget, so a session with 500 facts still shows who learned what.
        var facts = 0;
        foreach (var fact in recap.FactsEstablished.Take(MaxItems).TakeWhile(_ => lists.Length < MaxRecapLists / 2))
        {
            facts++;
            lists.Append("- ").Append(fact.Ref).Append(fact.Code is { Length: > 0 } code ? $" ({code})" : string.Empty)
                .Append(" \"").Append(CampaignMarkdownText.Excerpt(fact.Statement, 200)).Append("\" · ").Append(fact.CanonStatus)
                .Append(" · known by ").Append(KnownBy(fact.KnownBy)).Append('\n');
        }

        WriteMarkdown.AppendMore(lists, facts, recap.FactsEstablished.Count, "facts");
        lists.Append("\n## Learned this session (").Append(recap.KnowledgeLearned.Count.ToString(Invariant)).Append(")\n");
        if (recap.KnowledgeLearned.Count == 0)
        {
            lists.Append("_Nothing recorded as learned in this session._\n");
        }

        var learnedShown = 0;
        foreach (var learned in recap.KnowledgeLearned.Take(MaxItems).TakeWhile(_ => lists.Length < MaxRecapLists))
        {
            learnedShown++;
            lists.Append("- ").Append(learned.Knower).Append(": ").Append(learned.State).Append(' ').Append(learned.Target)
                .Append(" \"").Append(CampaignMarkdownText.Excerpt(learned.TargetLabel, 120)).Append('"')
                .Append(string.IsNullOrWhiteSpace(learned.KnownAs) ? string.Empty : $" as \"{CampaignMarkdownText.Excerpt(OneLine(learned.KnownAs), 120)}\"")
                .Append('\n');
        }

        WriteMarkdown.AppendMore(lists, learnedShown, recap.KnowledgeLearned.Count, "rows");

        // The recap takes what the lists leave (less room for its cut note), and is cut at a line when it runs past it.
        var room = CampaignMarkdownText.MaxChars - head.Length - lists.Length - 300;
        var text = string.IsNullOrWhiteSpace(recap.Recap)
            ? "_No recap yet._"
            : Bounded(recap.Recap, room, " so the lists below fit the output limit; the stored recap is whole");
        return CampaignMarkdownText.Cap((head + text + "\n" + lists).TrimEnd() + "\n", "narrow what the session established");
    }

    /// <summary>
    /// plan, start, end or record_past: what happened to the session, the batch, and (end, record_past) the checklist.
    /// <paramref name="madeCurrent"/>: the call named a campaign that was not the current one, and start or end made it
    /// current (a dry run: would make it); the result says so, since every later call without <c>campaign</c> (and
    /// dice_roll's log) now goes there.
    /// </summary>
    public static string FormatWrite(CampaignRow campaign, string action, SessionWriteResult result, bool madeCurrent = false)
    {
        var b = new StringBuilder();
        var what = action switch
        {
            "plan" => result.Outcome == WriteOutcomes.Created ? "planned" : result.Outcome == WriteOutcomes.Unchanged ? "plan unchanged" : "plan updated",
            "start" => "started",
            "end" => "ended",
            _ => result.Outcome == WriteOutcomes.Created ? "recorded" : result.Outcome == WriteOutcomes.Unchanged ? "record unchanged" : "record updated",
        };
        b.Append("# ").Append(result.DryRun ? "Dry run: " : string.Empty).Append("Session ").Append(result.Number.ToString(Invariant)).Append(' ')
            .Append(what).Append(" (").Append(campaign.Slug).Append(')').Append(result.DryRun ? ": nothing written" : string.Empty).Append("\n\n");
        var changed = result.Outcome != WriteOutcomes.Unchanged;
        WriteMarkdown.AppendBatch(b, campaign.Slug, result.BatchId, result.DryRun, null, changed);
        b.Append('\n').Append(result.Session).Append(" is ").Append(result.Status).Append('.');
        if (action == "start")
        {
            b.Append(" While it is live, every campaign write without a session belongs to it, and knowledge learned defaults to it.");
        }

        b.Append('\n');
        if (madeCurrent && result.DryRun)
        {
            b.Append(campaign.Slug).Append(" would become the current campaign: calls without campaign would use it")
                .Append(action == "start" ? ", and dice_roll would log to its live session.\n" : ".\n");
        }
        else if (madeCurrent)
        {
            b.Append(campaign.Slug).Append(" is now the current campaign: calls without campaign use it")
                .Append(action == "start" ? ", and dice_roll logs to its live session.\n" : ".\n");
        }

        if (result.ChangedFields.Count > 0)
        {
            b.Append("Changed: ").Append(string.Join(", ", result.ChangedFields)).Append(".\n");
        }

        if (result.BackupPath is { } backup)
        {
            b.Append("Session-end backup: ").Append(backup).Append('\n');
        }
        else if (result.BackupProblem is { } problem)
        {
            b.Append("Backup not written: ").Append(problem).Append('\n');
        }

        WriteMarkdown.AppendWarnings(b, result.Warnings, opLabel: null);
        if (result.Checklist is { } checklist)
        {
            AppendChecklist(b, checklist);
        }

        return CampaignMarkdownText.Cap(b.ToString().TrimEnd() + "\n", "the session write itself is complete");
    }

    /// <summary>A live-log append.</summary>
    public static string FormatLog(CampaignRow campaign, SessionLogResult result)
    {
        var b = new StringBuilder();
        b.Append("# Logged ").Append(WriteMarkdown.Count(result.NotesAdded, "note", "notes")).Append(" to session ")
            .Append(result.Number.ToString(Invariant)).Append(" (").Append(campaign.Slug).Append(")\n\n");
        // Says what end does with the log without printing an end call: sent as printed, one would end the session now,
        // with no recap.
        b.Append("The live log of ").Append(result.Session).Append(" holds ").Append(WriteMarkdown.Count(result.NotesTotal, "note", "notes"))
            .Append(". It is a scratchpad for the recap: not in the history and not undoable. When the session ends, the end action ")
            .Append("checks its names along with the recap's.\n");
        return b.ToString();
    }

    /// <summary>The end / record_past checklist; an empty one says so.</summary>
    public static void AppendChecklist(StringBuilder b, SessionChecklist checklist)
    {
        b.Append("\n## Checklist\n");
        if (checklist.IsEmpty)
        {
            b.Append("Nothing left to do: every name is known, clocks are ticked, facts have knowers, attendance is recorded.\n");
            return;
        }

        // Advisory (review U04): the names are a heuristic, and a sentence-opening word is not a name. Worded as "strike
        // them from the recap", it had a model rewrite the user's own recap to clear "Afterwards".
        Item(b, checklist.UnknownNames.Select(n => $"\"{OneLine(n)}\""), "Names that match no entry",
            " — add the new people, places or things; ignore ordinary words");
        Item(b, checklist.ClocksNotTicked, "Running clocks not ticked this session: tick them (campaign_write tick) if time passed");
        Item(b, checklist.FactsWithoutKnowers, "Facts established this session that no player-side knower holds: record who learned them (campaign_knowledge record)");
        Item(b, checklist.ClosedGateReveals, "Gated facts that reached a knower this session while the gate was not ready: confirm it was meant (or undo that batch)");
        Item(b, checklist.ProposedInventions, "Inventions proposed this session: accept or strike each (campaign_write fact/upsert with canon_status accepted or struck)");
        if (checklist.AttendanceNotRecorded)
        {
            b.Append("- [ ] Attendance was not recorded: what the party learned this session reads as \"attendance not recorded\" for every ")
                .Append("character; give attendance [{character, present}] (record_past can add it).\n");
        }
    }

    // One checklist entry: at most MaxItems values, each cut at MaxItemChars, and the entry stopped at MaxEntryChars, with
    // the rest counted, then (for the names line) the advice that follows the list. Unbounded, one entry made of a recap's
    // 20,000-character capitalised run was a single line longer than the output cap, and the cap dropped it with every
    // entry after it.
    private static void Item(StringBuilder b, IEnumerable<string> values, string what, string? then = null)
    {
        var list = values.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var line = new StringBuilder("- [ ] ").Append(what).Append(": ");
        var shown = 0;
        foreach (var value in list.Take(MaxItems))
        {
            if (shown > 0 && line.Length > MaxEntryChars)
            {
                break;
            }

            line.Append(shown == 0 ? string.Empty : ", ").Append(CampaignMarkdownText.Excerpt(OneLine(value), MaxItemChars));
            shown++;
        }

        b.Append(line).Append(list.Count > shown ? $", … and {(list.Count - shown).ToString(Invariant)} more" : string.Empty)
            .Append(then).Append(".\n");
    }

    private static void AppendDice(StringBuilder b, SessionDetail detail)
    {
        var rows = detail.DiceRolls.TakeLast(MaxDice).Select(roll =>
        {
            var total = roll.Total.ToString(Invariant) + roll.Success switch
            {
                true => " (success)",
                false => " (failure)",
                null => string.Empty,
            };
            return $"| {roll.At} | {CampaignMarkdownText.Cell(CampaignMarkdownText.Excerpt(roll.Expression, 80))} | " +
                   $"{CampaignMarkdownText.Cell(CampaignMarkdownText.Excerpt(roll.Label, 80))}{(roll.Secret == true ? " (secret)" : string.Empty)} | {total} |\n";
        }).ToList();
        var shown = LatestThatFit(rows, Budget - b.Length - 200);
        b.Append("\n## Dice (").Append(detail.DiceRollsTotal.ToString(Invariant)).Append(")\n");
        if (detail.DiceRollsTotal > shown.Count)
        {
            b.Append("_The latest ").Append(shown.Count.ToString(Invariant)).Append(" of ")
                .Append(detail.DiceRollsTotal.ToString(Invariant)).Append("._\n");
        }

        b.Append("| At | Roll | Label | Total |\n|---|---|---|---|\n");
        foreach (var row in shown)
        {
            b.Append(row);
        }
    }

    /// <summary>
    /// The latest of <paramref name="lines"/> (oldest first) whose total length fits in <paramref name="room"/>, oldest
    /// first; always at least the newest one. For the dice and the live log, where the newest entries are the ones that
    /// matter and the cut must fall on the oldest.
    /// </summary>
    private static List<string> LatestThatFit(IReadOnlyList<string> lines, int room)
    {
        var kept = new List<string>();
        var used = 0;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (kept.Count > 0 && used + lines[i].Length > room)
            {
                break;
            }

            kept.Insert(0, lines[i]);
            used += lines[i].Length;
        }

        return kept;
    }

    private static void AppendAuthor(StringBuilder b, AuthorSessionDetail author, int number, string campaignSlug)
    {
        b.Append("\n## Author only\nVisibility ").Append(author.Visibility);
        if (author.StartedAt is not null)
        {
            b.Append(" · started ").Append(author.StartedAt);
        }

        if (author.EndedAt is not null)
        {
            b.Append(" · ended ").Append(author.EndedAt);
        }

        b.Append('\n');
        if (!string.IsNullOrWhiteSpace(author.PrepMd))
        {
            b.Append("\n### Prep\n").Append(Bounded(author.PrepMd, MaxPrep, "the prep is the session's prep_md")).Append('\n');
        }

        if (author.LiveLog.Count > 0)
        {
            // The newest notes are the ones the recap still needs, so a cut (at the count or the budget) drops the oldest.
            var lines = author.LiveLog.TakeLast(MaxLogNotes)
                .Select(note => $"- {(note.At is null ? string.Empty : note.At + ": ")}{CampaignMarkdownText.Excerpt(note.Text, 200)}\n")
                .ToList();
            var notes = LatestThatFit(lines, Budget - b.Length - 300);
            b.Append("\n### Live log (").Append(author.LiveLog.Count.ToString(Invariant)).Append(")\n");
            foreach (var line in notes)
            {
                b.Append(line);
            }

            if (author.LiveLog.Count > notes.Count)
            {
                b.Append("_The latest ").Append(notes.Count.ToString(Invariant)).Append(" of ")
                    .Append(author.LiveLog.Count.ToString(Invariant)).Append(" notes are shown._\n");
            }
        }

        if (author.Changes.Count > 0)
        {
            b.Append("\n### What changed (").Append(WriteMarkdown.Count(author.Changes.Count, "batch", "batches")).Append(")\n");
            var batches = 0;
            foreach (var batch in author.Changes.Take(MaxBatches).TakeWhile(_ => b.Length < Budget))
            {
                batches++;
                b.Append("- `").Append(batch.BatchId).Append("` ").Append(batch.Tool ?? batch.Actor)
                    .Append(batch.UndoOf is null ? string.Empty : $" (undo of {batch.UndoOf})")
                    .Append(string.IsNullOrWhiteSpace(batch.Reason) ? string.Empty : $": {CampaignMarkdownText.Excerpt(batch.Reason, 160)}").Append('\n');
                foreach (var change in batch.Changes.Take(MaxChangesPerBatch))
                {
                    b.Append("  - ").Append(CampaignMarkdownText.Excerpt(change.Text, 200)).Append('\n');
                }

                if (batch.Changes.Count > MaxChangesPerBatch)
                {
                    b.Append("  - … and ").Append((batch.Changes.Count - MaxChangesPerBatch).ToString(Invariant))
                        .Append(" more (campaign_history {\"action\": \"batch\", \"batch_id\": \"").Append(batch.BatchId)
                        .Append("\", \"campaign\": \"").Append(campaignSlug).Append("\"} shows them)\n");
                }
            }

            WriteMarkdown.AppendMore(b, batches, author.Changes.Count,
                $"batches (campaign_history {{\"action\": \"since\", \"session\": {number.ToString(Invariant)}, \"campaign\": \"{campaignSlug}\"}} shows them)");
        }
    }

    // The hooks end stored in the session's data (author-only): up to 20 lines of up to 1,000 characters each.
    private static void AppendNextHooks(StringBuilder b, string data)
    {
        var hooks = NextHooks(data);
        if (hooks.Count == 0)
        {
            return;
        }

        b.Append("\n## Next hooks\n");
        foreach (var hook in hooks.Take(MaxHooks))
        {
            b.Append("- ").Append(CampaignMarkdownText.Excerpt(OneLine(hook), 300)).Append('\n');
        }

        WriteMarkdown.AppendMore(b, Math.Min(hooks.Count, MaxHooks), hooks.Count, "hooks");
    }

    /// <summary>
    /// The <c>next_hooks</c> array of a session's data object; empty when there is none. The data is the database's own
    /// JSON, but a hand-edited or older row must not fail the read: anything unexpected reads as no hooks.
    /// </summary>
    internal static IReadOnlyList<string> NextHooks(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("next_hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return hooks.EnumerateArray()
                .Select(h => h.ValueKind == JsonValueKind.String ? h.GetString() ?? string.Empty : h.GetRawText())
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // "party, table, character:serif, … and 22 more".
    private static string KnownBy(IReadOnlyList<string> knowers) => knowers.Count == 0
        ? "nobody on record"
        : string.Join(", ", knowers.Take(MaxKnowers)) + (knowers.Count > MaxKnowers ? $", … and {(knowers.Count - MaxKnowers).ToString(Invariant)} more" : string.Empty);

    // ": The Sky Fair", or nothing for an untitled session (whose name is "Session 3": "# Session 3: Session 3" says it twice).
    private static string TitleSuffix(SessionSummary session) =>
        string.Equals(session.Title.Trim(), "Session " + session.Number.ToString(Invariant), StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ": " + OneLine(session.Title);

    private static void AppendBanner(StringBuilder b, string? banner)
    {
        if (banner is not null)
        {
            b.Append(banner).Append("\n\n");
        }
    }

    private static string? PlayedOn(SessionSummary session) => session.PlayedOn is null
        ? null
        : session.PlayedOnPrecision is CampaignValues.DatePrecisions.Day or CampaignValues.DatePrecisions.Unknown
            ? session.PlayedOn
            : $"{session.PlayedOn} ({session.PlayedOnPrecision})";

    /// <summary>
    /// <paramref name="text"/> trimmed and, past <paramref name="max"/> characters, cut at a line (inside one when the last
    /// line break is before half of it, never inside a surrogate pair) with a note saying how much of how much is shown.
    /// <paramref name="hint"/>: how to see the rest; null when there is no way this view may take (the cut is said, nothing
    /// is suggested). The session resource cuts its recap and prep the same way.
    /// </summary>
    internal static string Bounded(string text, int max, string? hint)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= max)
        {
            return trimmed;
        }

        var cut = trimmed.LastIndexOf('\n', max);
        var kept = trimmed[..CampaignMarkdownText.WholeCharacters(trimmed, cut > max / 2 ? cut : max)].TrimEnd();
        // A hint starting with a space continues the sentence ("… characters so the lists fit"); any other is a new clause.
        var joined = hint is null ? string.Empty : hint.StartsWith(' ') ? hint : "; " + hint;
        return $"{kept}\n\n_… cut at {max.ToString("N0", Invariant)} of {trimmed.Length.ToString("N0", Invariant)} characters{joined}._";
    }

    private static string OneLine(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();
}
