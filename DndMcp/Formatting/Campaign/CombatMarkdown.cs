using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// The <c>combat</c> tool's results (contract §6, §15 H2): every AUTHOR step as the fight now stands, <c>end</c> as a write
/// result, the author's <c>state</c> (and <c>/combat/current</c>), the no-fight listing, and the party BOARD a perspective
/// reads.
///
/// <para>
/// <b>An author step</b> is read top to bottom at the table: the heading (<c># &lt;encounter&gt; — round N ·
/// &lt;name&gt;'s turn</c>), what the step changed (the tracker's lines, one per target, with their arithmetic), the rolls
/// the server made (the expression actually rolled, the kept faces, how each was logged), the D20b warnings, the
/// initiative table (order, initiative, name, HP, AC, conditions with their sources, concentration; the turn marked, the
/// defeated struck through, the hidden and the left said so), then the reminders, each with the call that resolves it.
/// </para>
/// <para>
/// <b>What a long step gives up, in order</b> (<see cref="Fit"/>): the reminders and the warnings are NEVER cut — a
/// concentration save due, a death save due or a legendary action on offer is said once, by the step that made it due, and
/// no other call repeats the step's own reminders, so a reminder cut from a step is lost. Past
/// <see cref="CampaignMarkdownText.MaxChars"/> the initiative table loses rows from the bottom first (the note says how many
/// are shown and, only when it is true, that <c>combat state</c> shows the whole table), then the "what changed" lines (the
/// note says the step was applied whole, which is all that is true: no other call lists them). A note never points at a
/// call that would not show what was cut. <c>state</c> follows the same order (its table before its reminders).
/// </para>
/// <para>
/// <b>Every printed call names the campaign LAST</b> (<see cref="CampaignLastIn"/>), whoever wrote it: the tracker's calls
/// name no campaign (it does not know the slug), the Repository's name it second, the proposals' first. A call sent while
/// another campaign is current must still reach this fight, and one order everywhere is one thing to learn. The tool runs
/// its refusals through the same pass.
/// </para>
/// <para>
/// <b><c>end</c> prints like a write result</b>: the batch paragraph (the id and its undo call, a dry run's sentence, or
/// "Nothing changed") only when a batch was logged or the call was a dry run, then every sheet field written with both
/// values, the summary (what the fight was worth, what persisted and what ended with it, items used, loot and coins), what
/// <c>force</c> wrote over, the author-only status proposals (never applied) and the reminders. The batch paragraph comes
/// right after the heading, before anything that can grow, so the output cap can never cut the only handle on the undo. A
/// write-back that gave or used items or coins says right under it how to move one of them without undoing the rest
/// (<see cref="MoveOneItem"/>).
/// </para>
/// <para>
/// <b>The board prints only the board model's fields</b> (§6.12): K's <see cref="CombatBoard"/> is a whitelist of the
/// strings and numbers a non-author view may read, so this formatter never sees the encounter's name, its rows, its log or
/// a stat block. Numbers go only on the rows the model marks <see cref="CombatBoardRow.PartySide"/> (a party-side row
/// shown under the combatant's own name); every other row prints its one HP word. Nothing else: no "what changed", no
/// reminders, no calls, no ids.
/// </para>
/// </summary>
internal static class CombatMarkdown
{
    /// <summary>
    /// The last line of defence of a step (<see cref="Fit"/>): reached only when the reminders, warnings and rolls alone
    /// pass the cap. Nothing in it claims another call shows what was cut.
    /// </summary>
    private const string StepHint = "the step was applied whole";

    private const string StateHint = "the fight is unchanged";

    private const string BoardHint = "the board is cut; the fight is unchanged";

    /// <summary>
    /// What an <c>end</c> that wrote items or coins says right under its batch paragraph (fix F1, review U03): "Undo that,
    /// I gave the sword to the wrong person" was answered by undoing the whole write-back, which also took back every
    /// sheet's HP, slots and potions, and the fight cannot be ended again. One item is moved by two inventory calls.
    /// </summary>
    internal const string MoveOneItem =
        "To move one item to someone else, use campaign_character inventory on both sheets (currency for coins); undoing the write-back reverts " +
        "everything it wrote.";

    /// <summary>
    /// Room a <c>combat state</c> of the same fight needs beyond a step's heading, table and reminders: its own subtitle and
    /// its "Last change" line (two tracker names of at most 80 characters, a kind, a round, an amount) and its cut note.
    /// </summary>
    private const int StateOverhead = 600;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    // A printed call: a campaign-bearing tool's name, a space, and the JSON object that follows (CampaignLastIn).
    private static readonly Regex CallStart = new(@"(?<![A-Za-z0-9_])(?:combat|campaign_[a-z]+|balance_simulate|encounter_difficulty) \{",
        RegexOptions.CultureInvariant);

    // -------------------------------------------------------------------------------------------------------------------
    // Steps

    /// <summary>One author step (every action but <c>state</c> and <c>end</c>).</summary>
    /// <param name="note">A line the tool adds (the campaign made current by a start that named it), or null.</param>
    public static string FormatStep(CombatOutcome outcome, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var campaign = outcome.Campaign;
        var head = new StringBuilder();
        AppendHeading(head, outcome.Encounter);
        AppendSubtitle(head, outcome.Action, campaign, outcome.Encounter);

        var lines = outcome.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => CampaignLastIn(OneLine(l), campaign)).ToList();
        if (note is not null)
        {
            lines.Add(note);
        }

        var middle = new StringBuilder();
        AppendRolls(middle, outcome.Rolls);
        AppendWarnings(middle, outcome.Warnings, campaign);
        var reminders = Reminders(outcome.Reminders, campaign);
        var rows = TableRows(outcome.Encounter);

        // What the same fight's combat state would print at most (its own reminders are the turn's context, which every step's
        // reminders carry too): only when that fits is "combat state shows the whole table" true.
        var stateShowsWhole = head.Length + StateOverhead + Table(rows, rows.Count, null).Length + reminders.Length <= CampaignMarkdownText.MaxChars;
        return Fit(head.ToString(), lines, middle.ToString(), rows, reminders, StepHint, (shown, total) =>
            $"_Initiative table cut to keep this result within {MaxText}: {Count(shown, "row", "rows")} of {N(total)} shown; " +
            (stateShowsWhole
                ? "combat state shows the whole table._"
                : "the whole table is longer than one result holds, and combat state shows as many rows as fit._"));
    }

    /// <summary>
    /// The author's <c>combat state</c> (and <c>/combat/current</c>): the fight with its table, the reminders its state
    /// calls for now and the last change; or, when no fight is running, the listing.
    /// </summary>
    public static string FormatState(CampaignRow campaign, CombatStateRead read)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(read);
        if (read.Encounter is not { } encounter)
        {
            return FormatNoFight(read.NoFight ?? new CombatNoFight(campaign.Slug, [], [], null));
        }

        var b = new StringBuilder();
        AppendHeading(b, encounter);
        AppendSubtitle(b, CombatActions.State, read.Campaign, encounter);
        if (encounter.Status == ES.Ended)
        {
            b.Append('\n');
            AppendEnded(b, read.Campaign, encounter.OutcomeMd, encounter.WritebackBatchId, encounter.WritebackStatus);
        }

        if (read.LastChange is { } last)
        {
            b.Append("\nLast change: ").Append(last.Kind).Append(" in round ").Append(N(last.Round));
            if (last.Actor is not null)
            {
                b.Append(", by ").Append(OneLine(last.Actor));
            }

            if (last.Target is not null && last.Target != last.Actor)
            {
                b.Append(", to ").Append(OneLine(last.Target));
            }

            if (last.Amount is { } amount)
            {
                b.Append(" (").Append(N(amount)).Append(')');
            }

            b.Append(".\n");
        }

        return Fit(b.ToString(), [], string.Empty, TableRows(encounter), Reminders(read.Reminders, read.Campaign), StateHint, (shown, total) =>
            $"_Initiative table cut to keep this result within {MaxText}: {Count(shown, "row", "rows")} of {N(total)} shown; every combatant is still in the fight._");
    }

    /// <summary>
    /// No fight running (contract §6.1: <c>state</c> succeeds; <c>/combat/current</c> gives the same text): the planned and
    /// paused fights with the call that starts each, and the last ended one.
    /// </summary>
    public static string FormatNoFight(CombatNoFight listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var b = new StringBuilder("# ").Append(listing.Campaign).Append(": no combat running\n\n");
        if (listing.Planned.Count > 0)
        {
            b.Append("Planned:\n");
            foreach (var e in listing.Planned)
            {
                b.Append("- ").Append(Quoted(e.Name)).Append(" (").Append(Count(e.Combatants, "combatant", "combatants"))
                    .Append("): start it with ").Append(StartCall(listing.Campaign, e.Name)).Append('\n');
            }

            b.Append('\n');
        }

        if (listing.Paused.Count > 0)
        {
            b.Append("Paused:\n");
            foreach (var e in listing.Paused)
            {
                b.Append("- ").Append(Quoted(e.Name)).Append(" (round ").Append(N(e.Round)).Append(", ")
                    .Append(Count(e.Combatants, "combatant", "combatants")).Append("): resume it with ").Append(StartCall(listing.Campaign, e.Name)).Append('\n');
            }

            b.Append('\n');
        }

        if (listing.LastEnded is { } last)
        {
            b.Append("Last ended: ").Append(Quoted(last.Name)).Append(", in round ").Append(N(last.Round));
            if (last.EndedAt is { } at)
            {
                b.Append(", at ").Append(at);
            }

            b.Append(last.WritebackStatus switch
            {
                WritebackStatuses.Applied => "; its write-back stands",
                WritebackStatuses.Undone => "; its write-back was undone",
                WritebackStatuses.AppliedAgain => "; its write-back was applied again",
                _ => "; it wrote nothing back",
            }).Append('.');
            if (!string.IsNullOrWhiteSpace(last.OutcomeMd))
            {
                b.Append(" Outcome: ").Append(Sentence(CampaignMarkdownText.Excerpt(last.OutcomeMd, 300)));
            }

            b.Append(" combat {\"action\": \"state\", \"encounter\": \"last\", \"campaign\": \"").Append(listing.Campaign).Append("\"} shows it.\n\n");
        }

        b.Append("Start a fight with combat {\"action\": \"start\", \"name\": …, \"campaign\": \"").Append(listing.Campaign)
            .Append("\"} (the party joins from their sheets), or prepare one for later with combat {\"action\": \"prepare\", \"name\": …, \"campaign\": \"")
            .Append(listing.Campaign).Append("\"}.\n");
        return CampaignMarkdownText.Cap(b.ToString(), "combat state with an encounter's name shows that one");
    }

    // -------------------------------------------------------------------------------------------------------------------
    // end

    /// <summary>
    /// <c>end</c> as a write result (class summary): the batch paragraph when a batch was logged (or a dry run), then what
    /// was written back and the summary.
    /// </summary>
    public static string FormatEnd(CombatEndOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var encounter = outcome.Encounter;
        var campaign = outcome.Campaign;
        var b = new StringBuilder("# ");
        b.Append(outcome.DryRun ? "Dry run: " : string.Empty).Append(OneLine(encounter.Name))
            .Append(outcome.DryRun ? " — would end in round " : " — ended in round ").Append(N(encounter.Round)).Append("\n\n");
        b.Append("combat end · ").Append(campaign).Append(" · ").Append(encounter.Ruleset).Append(" rules")
            .Append(encounter.Lair ? " · in a lair" : string.Empty).Append('\n');

        if (outcome.DryRun)
        {
            // Not the shared write sentence: end assigns no codes, and the fight it previews is still running.
            b.Append("\n**Dry run: nothing was written and no batch exists.** The fight is still running; this is what ending it would write. " +
                     "Send the same call without dry_run to end it.")
                .Append(outcome.SessionNumber is { } n ? $" Session context: session {N(n)}." : string.Empty).Append('\n');
        }
        else if (outcome.BatchId is not null)
        {
            b.Append('\n');
            WriteMarkdown.AppendBatch(b, campaign, outcome.BatchId, dryRun: false, outcome.SessionNumber, changed: true);
            if (outcome.Loot.Count > 0 || outcome.Currency.Count > 0 || outcome.Items.Count > 0)
            {
                b.Append(MoveOneItem).Append('\n');
            }
        }
        else if (outcome.Discarded)
        {
            b.Append("\nEnded with nothing written back: no sheet, item or award changed, so there is no batch to undo.\n");
        }
        else if (outcome.NothingChanged)
        {
            b.Append("\nNothing changed on any sheet: the fight ended with no batch to undo.\n");
        }

        if (outcome.Changes.Count > 0)
        {
            b.Append(outcome.DryRun ? "\n## Would be written back\n" : "\n## Written back\n");
            foreach (var change in outcome.Changes)
            {
                b.Append("- ").Append(OneLine(change.Combatant)).Append(" (`").Append(change.Ref).Append("`) ").Append(change.Field)
                    .Append(change.Key is null ? string.Empty : " " + OneLine(change.Key)).Append(": ")
                    .Append(Value(change.Before, "none")).Append(" → ").Append(Value(change.After, "removed")).Append('\n');
            }
        }

        var summary = outcome.Summary.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (summary.Count > 0)
        {
            b.Append("\n## Summary\n");
            foreach (var line in summary)
            {
                b.Append("- ").Append(CampaignLastIn(OneLine(line), campaign)).Append('\n');
            }
        }

        // A consumed holding force could not write (the fight used more than the sheet still has, or the holding is gone)
        // is KEPT as the sheet has it, never "written over" (review F2R07): its own line, with what the fight used and what
        // is left, as end's refusal prints them.
        foreach (var kept in outcome.Overwritten.Where(d => d.Field == CombatEnd.HoldingField))
        {
            b.Append("\nKept (force): ").Append(OneLine(kept.Name)).Append(' ').Append(kept.Field)
                .Append(kept.Key is null ? string.Empty : " " + OneLine(kept.Key)).Append(": ")
                .Append(Value(kept.WhenAdded, "none")).Append("; ").Append(Value(kept.Now, "none")).Append(" (the sheet's count is kept)\n");
        }

        var overwritten = outcome.Overwritten.Where(d => d.Field != CombatEnd.HoldingField).ToList();
        if (overwritten.Count > 0)
        {
            b.Append("\n## Written over (force)\n");
            foreach (var drift in overwritten)
            {
                b.Append("- ").Append(OneLine(drift.Name)).Append(' ').Append(drift.Field)
                    .Append(drift.Key is null ? string.Empty : " " + OneLine(drift.Key)).Append(": ")
                    .Append(Value(drift.WhenAdded, "none")).Append(" when it joined, ").Append(Value(drift.Now, "none")).Append(" before this end\n");
            }
        }

        if (outcome.Proposals.Count > 0)
        {
            b.Append("\n## Proposals (not applied)\n");
            foreach (var proposal in outcome.Proposals)
            {
                b.Append("- ").Append(CampaignLastIn(OneLine(proposal.Text), campaign)).Append(' ').Append(CampaignLast(proposal.Call, campaign)).Append('\n');
            }
        }

        b.Append(Reminders(outcome.Reminders, campaign));
        return CampaignMarkdownText.Cap(b.ToString().TrimEnd() + "\n", "the end is complete as reported; combat state with encounter \"last\" shows the fight");
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The board

    /// <summary>
    /// The party board for a non-author view (§6.12): only <paramref name="board"/>'s fields, under the view's banner.
    /// </summary>
    public static string FormatBoard(CombatBoard board, string? banner)
    {
        ArgumentNullException.ThrowIfNull(board);
        var b = new StringBuilder("# ");
        if (!board.Shown)
        {
            b.Append(CombatBoard.NothingText).Append('\n');
            if (banner is not null)
            {
                b.Append('\n').Append(banner).Append('\n');
            }

            return b.ToString();
        }

        b.Append("Combat — ").Append(board.Ended ? $"ended in round {N(board.Round)}" : board.Round == 0 ? "not started" : $"round {N(board.Round)}").Append('\n');
        if (banner is not null)
        {
            b.Append('\n').Append(banner).Append('\n');
        }

        b.Append("\n| Turn | # | Name | Status | Conditions |\n|---|---|---|---|---|\n");
        foreach (var row in board.Rows)
        {
            b.Append("| ").Append(row.Turn ? "▶" : string.Empty).Append(" | ").Append(N(row.Number)).Append(" | ")
                .Append(CampaignMarkdownText.Cell(row.Ref is null ? row.Name : $"{row.Name} ({row.Ref})")).Append(" | ")
                .Append(CampaignMarkdownText.Cell(row.PartySide ? PartyStatus(row) : row.HpWord)).Append(" | ")
                .Append(CampaignMarkdownText.Cell(row.Conditions.Count == 0 ? null : string.Join(", ", row.Conditions))).Append(" |\n");
        }

        return CampaignMarkdownText.Cap(b.ToString(), BoardHint);
    }

    // A party row's numbers: HP current/max (+temp), AC, exhaustion, death saves when dying or stable, concentration.
    private static string PartyStatus(CombatBoardRow row)
    {
        var parts = new List<string>
        {
            row.Hp is { } hp
                ? $"HP {N(hp)}{(row.MaxHp is { } max ? "/" + N(max) : string.Empty)}{(row.TempHp is > 0 and var temp ? $" (+{N(temp)} temp)" : string.Empty)}"
                : "HP unknown",
        };
        if (row.Ac is { } ac)
        {
            parts.Add($"AC {N(ac)}");
        }

        if (row.Exhaustion is > 0 and var exhaustion)
        {
            parts.Add($"exhaustion {N(exhaustion)}");
        }

        if (row.DeathSaves is { } saves)
        {
            parts.Add(Tally(saves));
        }

        if (row.Concentrating)
        {
            parts.Add(row.Concentration is { } spell ? $"concentrating on {spell}" : "concentrating");
        }

        return string.Join(" · ", parts);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Printed calls

    /// <summary>
    /// A printed call with the campaign named LAST (the house order of every printed fix call, C's
    /// <c>CharacterWriter.WithCampaign</c>): a call that already names it elsewhere has it moved to the end, one that
    /// names none gets it. A text that is not a <c>tool {…}</c> call, or a call naming another campaign, is returned as it is.
    /// </summary>
    internal static string CampaignLast(string call, string campaign)
    {
        if (call.Length < 2 || !call.EndsWith('}') || call.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return call;
        }

        var named = $"\"campaign\": \"{campaign}\"";
        var trimmed = call.Replace(named + ", ", string.Empty, StringComparison.Ordinal).Replace(", " + named, string.Empty, StringComparison.Ordinal);
        if (trimmed.Contains("\"campaign\":", StringComparison.Ordinal))
        {
            return call; // Another campaign's call: printed as it is.
        }

        var body = trimmed[..^1].TrimEnd();
        return body.EndsWith('{') ? $"{body}{named}}}" : $"{body}, {named}}}";
    }

    /// <summary>
    /// Every printed call inside <paramref name="text"/> (<c>combat {…}</c>, <c>campaign_character {…}</c>, …: a campaign
    /// tool's name, a space and a JSON object, found by matching its braces outside strings) through
    /// <see cref="CampaignLast"/>; the rest of the text is unchanged. The tracker's lines and refusals print calls with no
    /// campaign ("roll initiative first (combat {"action": "initiative"})") because the Domain never knows the slug, and the
    /// Repository's name it second: this is the one place both are completed, so a call printed anywhere in combat's output
    /// reaches this fight whichever campaign is current when it is sent. A call whose braces do not close is left as it is.
    /// </summary>
    internal static string CampaignLastIn(string text, string campaign)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.IndexOf('{', StringComparison.Ordinal) < 0)
        {
            return text;
        }

        var b = new StringBuilder(text.Length + 32);
        var at = 0;
        for (var match = CallStart.Match(text); match.Success; match = CallStart.Match(text, at))
        {
            var open = match.Index + match.Length - 1;
            var close = ClosingBrace(text, open);
            if (close < 0)
            {
                break;
            }

            b.Append(text, at, match.Index - at).Append(CampaignLast(text[match.Index..(close + 1)], campaign));
            at = close + 1;
        }

        return b.Append(text, at, text.Length - at).ToString();
    }

    // The index of the brace that closes the one at open (strings and their escapes skipped), or -1.
    private static int ClosingBrace(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{' or '[':
                    depth++;
                    break;
                case '}' or ']':
                    depth--;
                    if (depth == 0)
                    {
                        return c == '}' ? i : -1;
                    }

                    break;
            }
        }

        return -1;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Parts

    private static readonly string MaxText = $"{CampaignMarkdownText.MaxChars.ToString("N0", Invariant)} characters";

    /// <summary>
    /// The result within <see cref="CampaignMarkdownText.MaxChars"/> (class summary, "What a long step gives up"):
    /// everything when it fits; else the table's rows from the bottom, with <paramref name="tableNote"/> (rows shown, rows in
    /// all) where they were; else, with no row, the "what changed" lines from the bottom, with a count. The head, the rolls,
    /// the warnings (<paramref name="middle"/>) and the reminders are never cut here; only when they alone pass the cap does
    /// the line-boundary cut take the tail, with <paramref name="hint"/>.
    /// </summary>
    private static string Fit(
        string head, IReadOnlyList<string> lines, string middle, IReadOnlyList<string> rows, string reminders, string hint, Func<int, int, string> tableNote)
    {
        string Compose(int shownLines, int shownRows) =>
            (head + Changed(lines, shownLines) + middle + Table(rows, shownRows, tableNote) + reminders).TrimEnd() + "\n";

        bool Fits(int shownLines, int shownRows) => Compose(shownLines, shownRows).Length <= CampaignMarkdownText.MaxChars;

        if (Fits(lines.Count, rows.Count))
        {
            return Compose(lines.Count, rows.Count);
        }

        if (Fits(lines.Count, 0))
        {
            return Compose(lines.Count, Largest(rows.Count - 1, k => Fits(lines.Count, k)));
        }

        if (Fits(0, 0))
        {
            return Compose(Largest(lines.Count - 1, j => Fits(j, 0)), 0);
        }

        return CampaignMarkdownText.Cap(Compose(0, 0), hint);
    }

    // The largest n in [0, max] with fits(n), given fits(0) (lengths grow with n).
    private static int Largest(int max, Func<int, bool> fits)
    {
        var (lo, hi) = (0, Math.Max(0, max));
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) / 2);
            if (fits(mid))
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    // "## What changed" with the first shown lines, and the count of the rest.
    private static string Changed(IReadOnlyList<string> lines, int shown)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var b = new StringBuilder("\n## What changed\n");
        foreach (var line in lines.Take(shown))
        {
            b.Append("- ").Append(line).Append('\n');
        }

        if (shown < lines.Count)
        {
            b.Append("- _… and ").Append(Count(lines.Count - shown, "more line", "more lines"))
                .Append($" of what changed, cut to keep this result within {MaxText}; the step was applied whole._\n");
        }

        return b.ToString();
    }

    /// <summary><c># &lt;encounter&gt; — round N · &lt;name&gt;'s turn</c>, and the other states' headings.</summary>
    private static void AppendHeading(StringBuilder b, EncounterView encounter)
    {
        b.Append("# ").Append(OneLine(encounter.Name)).Append(" — ");
        b.Append(encounter.Status switch
        {
            ES.Planned => "planned",
            ES.Paused => $"paused in round {N(encounter.Round)}",
            ES.Ended => $"ended in round {N(encounter.Round)}",
            _ when encounter.Round == 0 => "round 0 (roll initiative to begin round 1)",
            _ => $"round {N(encounter.Round)}" + (encounter.TurnName is { } turn ? $" · {OneLine(turn)}'s turn" : string.Empty),
        });
        b.Append("\n\n");
    }

    private static void AppendSubtitle(StringBuilder b, string action, string campaign, EncounterView encounter)
    {
        b.Append("combat ").Append(action).Append(" · ").Append(campaign).Append(" · ").Append(encounter.Ruleset).Append(" rules")
            .Append(encounter.Lair ? " · in a lair" : string.Empty)
            .Append(encounter.SessionNumber is { } session ? $" · session {N(session)}" : string.Empty)
            .Append('\n');
    }

    // An ended fight's outcome and write-back state (D6: applied, undone, applied again).
    private static void AppendEnded(StringBuilder b, string campaign, string? outcome, string? batchId, string? status)
    {
        if (batchId is null)
        {
            b.Append("It ended with nothing written back.\n");
        }
        else
        {
            b.Append(status switch
            {
                WritebackStatuses.Undone => $"Its write-back (batch `{batchId}`) was undone: the sheets are as they were before the fight.\n",
                WritebackStatuses.AppliedAgain => $"Its write-back (batch `{batchId}`) was undone and then applied again (a redo).\n",
                _ => $"Its write-back is batch `{batchId}`. To undo it: {HistoryMarkdown.UndoCall(batchId, campaign)}.\n",
            });
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            b.Append("Outcome: ").Append(CampaignMarkdownText.Excerpt(outcome, 1_000)).Append('\n');
        }
    }

    private static void AppendRolls(StringBuilder b, IReadOnlyList<CombatRoll> rolls)
    {
        if (rolls.Count == 0)
        {
            return;
        }

        b.Append("\n## Rolls\n");
        foreach (var roll in rolls)
        {
            b.Append("- ").Append(roll.Purpose).Append(": `").Append(roll.Expression).Append("` [")
                .Append(string.Join(", ", roll.Faces.Select(f => f.ToString(Invariant)))).Append("] = ").Append(roll.Total.ToString(Invariant))
                .Append(" · logged as \"").Append(roll.Label).Append('"').Append(roll.Secret ? " (secret)" : string.Empty).Append('\n');
        }
    }

    private static void AppendWarnings(StringBuilder b, IReadOnlyList<string> warnings, string campaign)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        b.Append("\n## Warnings\n");
        foreach (var warning in warnings)
        {
            b.Append("- ").Append(CampaignLastIn(OneLine(warning), campaign)).Append('\n');
        }
    }

    // The initiative section: the header and the first shown rows, then the note on the rest (or the empty table's line).
    private static string Table(IReadOnlyList<string> rows, int shown, Func<int, int, string>? note)
    {
        var b = new StringBuilder("\n## Initiative\n");
        if (rows.Count == 0)
        {
            return b.Append("No combatants yet: add them with combat add.\n").ToString();
        }

        if (shown > 0)
        {
            b.Append("| Turn | # | Init | Name | Side | HP | AC | Conditions |\n|---|---|---|---|---|---|---|---|\n");
            foreach (var row in rows.Take(shown))
            {
                b.Append(row);
            }
        }

        if (shown < rows.Count && note is not null)
        {
            b.Append(shown > 0 ? "\n" : string.Empty).Append(note(shown, rows.Count)).Append('\n');
        }

        return b.ToString();
    }

    /// <summary>
    /// The initiative table's rows (author), each a whole line: the order, then the combatants with no initiative yet, then
    /// the ones that left. The turn is marked, the defeated struck through, the hidden and the left said so.
    /// </summary>
    private static IReadOnlyList<string> TableRows(EncounterView encounter)
    {
        var rows = new List<string>(encounter.Rows.Count);
        foreach (var row in encounter.Rows)
        {
            var name = row.EntityHandle is { } handle ? $"{row.Name} ({handle})" : row.Name;
            name = CampaignMarkdownText.Cell(name);
            if (row.Defeated || row.Dead)
            {
                name = "~~" + name + "~~";
            }

            var notes = new List<string>();
            if (row.Hidden)
            {
                notes.Add("hidden");
            }

            if (row.Left)
            {
                notes.Add("left");
            }

            if (row.Surprised)
            {
                notes.Add("surprised");
            }

            if (notes.Count > 0)
            {
                name += " (" + string.Join(", ", notes) + ")";
            }

            rows.Add(new StringBuilder("| ").Append(row.Turn ? "▶" : string.Empty)
                .Append(" | ").Append(row.Position is { } position ? N(position) : "—")
                .Append(" | ").Append(row.Initiative is { } init ? init.ToString("0.####", Invariant) : "—")
                .Append(" | ").Append(name)
                .Append(" | ").Append(row.Side)
                .Append(" | ").Append(CampaignMarkdownText.Cell(Hp(row)))
                .Append(" | ").Append(row.Ac is { } ac ? N(ac) : "—")
                .Append(" | ").Append(CampaignMarkdownText.Cell(Conditions(row)))
                .Append(" |\n").ToString());
        }

        return rows;
    }

    private static string Hp(CombatantView row)
    {
        if (row.Dead)
        {
            return "dead";
        }

        var text = row.Hp is { } hp
            ? $"{N(hp)}/{(row.MaxHp is { } max ? N(max) : "?")}"
            : row.DamageTaken > 0 ? $"unknown, took {N(row.DamageTaken)}" : "unknown";
        if (row.TempHp > 0)
        {
            text += $" (+{N(row.TempHp)} temp)";
        }

        if (row.DeathSaves is { } saves && row.Hp == 0)
        {
            text += " · " + Tally(saves);
        }
        else if (row.Defeated)
        {
            text += " · defeated";
        }

        return text;
    }

    private static string? Conditions(CombatantView row)
    {
        var parts = new List<string>(row.Conditions);
        if (row.Exhaustion > 0)
        {
            parts.Add($"exhaustion {N(row.Exhaustion)}");
        }

        if (row.Concentration is { } concentration)
        {
            parts.Add($"concentrating on {concentration}");
        }

        if (row.Legendary is { } legendary)
        {
            if (legendary.Actions > 0)
            {
                parts.Add($"legendary actions {N(legendary.Actions - legendary.Used)}/{N(legendary.Actions)}");
            }

            if (legendary.Resistance > 0)
            {
                parts.Add($"Legendary Resistance {N(legendary.Resistance - legendary.ResistanceUsed)}/{N(legendary.Resistance)}");
            }
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private static string Tally(DeathSaveTally saves) =>
        saves.Stable
            ? "stable"
            : $"death saves: {Count(saves.Successes, "success", "successes")}, {Count(saves.Failures, "failure", "failures")}";

    // "## Reminders" with each reminder and the call that resolves it, every call naming the campaign last.
    private static string Reminders(IReadOnlyList<CombatReminder> reminders, string campaign)
    {
        if (reminders.Count == 0)
        {
            return string.Empty;
        }

        var b = new StringBuilder("\n## Reminders\n");
        foreach (var reminder in reminders)
        {
            b.Append("- ").Append(CampaignLastIn(OneLine(reminder.Text), campaign));
            if (reminder.Call is { } call)
            {
                b.Append(' ').Append(CampaignLast(call, campaign));
            }

            b.Append('\n');
        }

        return b.ToString();
    }

    private static string StartCall(string campaign, string name) =>
        $"combat {{\"action\": \"start\", \"encounter\": {EncounterResolver.Json(name)}, \"campaign\": \"{campaign}\"}}";

    // A fight's name as the listing quotes it: a JSON string, so a quote inside the name cannot end the quotation.
    private static string Quoted(string name) => EncounterResolver.Json(OneLine(name));

    private static string Value(string? value, string absent)
    {
        if (string.IsNullOrEmpty(value))
        {
            return absent;
        }

        var line = OneLine(value);
        return line.Length <= SheetWriteMarkdown.MaxValueChars ? line : line[..CampaignMarkdownText.WholeCharacters(line, SheetWriteMarkdown.MaxValueChars)] + "…";
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    // An excerpt as a sentence, so the call printed after it cannot run into it ("Outcome: Party victory combat {…} shows
    // it.", fix F1 U12): a full stop unless it already ends in one, a "!" or "?" (also inside closing quotes or brackets)
    // or the excerpt's own "…".
    private static string Sentence(string text)
    {
        var end = text.TrimEnd().TrimEnd('"', '\'', ')', ']', '»', '”', '’');
        return end.Length > 0 && end[^1] is '.' or '!' or '?' or '…' ? text : text.TrimEnd() + ".";
    }

    private static string Count(int count, string one, string many) => $"{N(count)} {(count == 1 ? one : many)}";

    private static string N(int value) => value.ToString(Invariant);
}
