using System.Globalization;
using System.Text;
using DndMcp.Domain.Simulation;
using DndMcp.Formatting.Srd;

namespace DndMcp.Formatting;

/// <summary>
/// Renders a <see cref="SimulationReport"/> for <c>balance_simulate</c>: the headline first (P(party wins) with its 95%
/// interval, defeat, draw, a death, a member left dying, rounds), then the per-combatant table, the comparison when asked, what the stat blocks
/// leave out, the policies and assumptions, the seed with how to reproduce the result, and the replayed fight last.
///
/// <para>
/// <b>Bounded.</b> A typical result (a party of four against a few kinds of monster) stays under 8,000 characters and
/// every result under <see cref="MaxChars"/> (24,000; about 6,000 tokens, well under Claude Code's 10,000-token warning).
/// Lists that grow with the fight are capped and say how many they leave out (warnings per stat block and stat blocks
/// shown, resources per combatant, notes), and the replay log gets what room is left, cut at a line with its summary kept
/// whole: the outcome of the replayed fight is never the part that is lost. <c>SimulateOutputSizeTests</c> pins both
/// ceilings, the second with the largest fight the limits allow.
/// </para>
/// <para>
/// <b>Probabilities never round onto 0% or 100%</b> unless exact (<see cref="BalanceMarkdownText.Percent"/>): "the party
/// wins 100%" after 9,996 wins in 10,000 reads as a certainty the dice never gave. Every fight-level proportion carries
/// its Wilson interval, and the rounds and the comparison's differences their CLT intervals, so a model comparing two runs
/// can tell a difference from noise; the per-combatant figures have none (<see cref="SimulationReport"/> says why).
/// </para>
/// </summary>
internal static partial class SimulationMarkdown
{
    /// <summary>The ceiling on any result, replay log included (about 6,000 tokens).</summary>
    public const int MaxChars = 24_000;

    /// <summary>Warnings shown per stat block before "and N more".</summary>
    public const int MaxWarningsPerBlock = 4;

    /// <summary>Stat blocks whose warnings are listed; the rest are named with their counts.</summary>
    public const int MaxWarningBlocks = 6;

    /// <summary>Resources shown per combatant before "and N more".</summary>
    public const int MaxResources = 6;

    /// <summary>Resource lines (combatants) shown before the rest are summarised.</summary>
    public const int MaxResourceLines = 12;

    /// <summary>Resolution notes shown.</summary>
    public const int MaxNotes = 8;

    /// <summary>
    /// The assumptions' room. The simulator's general lines come first and the party archetypes' own (about 700 characters
    /// each) last, so a party of many distinct archetypes loses only the tail of those, with a count; the general rules of
    /// the fight are always shown.
    /// </summary>
    public const int MaxAssumptionsChars = 6_000;

    /// <summary>Entries named per side in the title.</summary>
    private const int MaxTitleEntries = 3;

    // Room the replay needs beyond its log: the heading, the fence and the cut note.
    private const int ReplayOverhead = 400;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <param name="seedGiven">The caller passed the seed (the reproduce hint then says the same call repeats it).</param>
    /// <param name="notes">How the monsters were found (forms, another edition's stat block), from the host's lookup.</param>
    public static string Format(SimulationReport report, bool seedGiven, IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(report);
        var blocks = new List<string?>
        {
            Title(report) + "\n\n" + Headline(report),
            Meta(report, seedGiven),
            Combatants(report),
            Resources(report),
            Compare(report),
            Warnings(report),
            Policies(report),
            Assumptions(report),
            Notes(notes),
            SeedLine(report, seedGiven),
        };

        var main = SrdMarkdownText.Blocks(blocks);
        if (report.ReplayLog is not { } log || report.ReplayIteration is not { } iteration)
        {
            return Cap(main + "\n");
        }

        var room = MaxChars - main.Length - ReplayOverhead;
        return Cap(main + "\n\n" + Replay(iteration, report, log, room) + "\n");
    }

    /// <summary>
    /// The last line of defence for <see cref="MaxChars"/>: every section is capped on its own, but 40 entries with
    /// 80-character names make a table and a replay summary long enough to pass it together. Cut at a line, close an open
    /// code fence, and say so; the headline, at the top, is never what is cut.
    /// </summary>
    private static string Cap(string text)
    {
        if (text.Length <= MaxChars)
        {
            return text;
        }

        const string Note = "\n*[Cut to keep the result readable: {0} more characters not shown. Fewer entries, shorter names or no replay fit whole.]*\n";
        var cut = text.LastIndexOf('\n', MaxChars - 300);
        var kept = text[..cut];
        var fence = kept.Split('\n').Count(l => l.StartsWith("```", StringComparison.Ordinal)) % 2 == 1 ? "\n```" : string.Empty;
        return kept + fence + "\n" + string.Format(Invariant, Note, Number(text.Length - cut));
    }

    private static string Title(SimulationReport report)
    {
        var party = report.Combatants.Where(c => c.Side == "party").ToList();
        var enemies = report.Combatants.Where(c => c.Side != "party").ToList();
        return $"# Fight simulation: {Side(party)} vs {Side(enemies)}";

        static string Side(IReadOnlyList<CombatantReport> side)
        {
            var named = side.Take(MaxTitleEntries).Select(c => c.Count > 1 ? $"{c.Name} ×{c.Count.ToString(Invariant)}" : c.Name);
            var more = side.Count > MaxTitleEntries ? $" and {Plural(side.Count - MaxTitleEntries, "more entry", "more entries")}" : string.Empty;
            return string.Join(", ", named) + more;
        }
    }

    /// <summary>
    /// The outcome lines. A party member still dying when the fight stopped is its own figure, with what it means: the fight
    /// ends the moment a side has nobody above 0 HP, so "a party member dies" counts only the deaths the fight dealt, and
    /// "Party defeated 100% · a party member dies 0.1%" alone reads as a wipe nobody died in.
    /// </summary>
    private static string Headline(SimulationReport report)
    {
        var fights = Number(report.Iterations);
        var dying = report.AnyPartyDying.Count == 0
            ? "."
            : $" · a party member is left dying {Percent(report.AnyPartyDying.Estimate)} ({Interval(report.AnyPartyDying)}).\n" +
              "*Left dying: at 0 HP, neither stable nor dead, when the fight stopped (it stops once a side has nobody above 0 HP); " +
              "their death saves are not rolled, so whether they die is not in the figures.*";
        return
            $"**The party wins {Percent(report.PartyWins.Estimate)}** of {fights} fights ({Every(report.PartyWins)}95% CI {Interval(report.PartyWins)}).\n" +
            $"Party defeated (every member at 0 HP) {Percent(report.PartyDefeated.Estimate)} ({Interval(report.PartyDefeated)}) · " +
            $"draw at round {report.RoundCap.ToString(Invariant)} {Percent(report.Draw.Estimate)} ({Interval(report.Draw)}) · " +
            $"a party member dies {Percent(report.AnyPartyDeath.Estimate)} ({Interval(report.AnyPartyDeath)}){dying}\n" +
            $"Rounds: mean {Decimal(report.Rounds.Mean.Mean, 2)} (95% CI {Decimal(report.Rounds.Mean.Low, 2)}–{Decimal(report.Rounds.Mean.High, 2)}), " +
            $"median {Number(report.Rounds.P50)}, 90th percentile {Number(report.Rounds.P90)}.";
    }

    // "all 10,000; " / "none; ": a whole-run result is exact for these fights, and the interval says what it means beyond them.
    private static string Every(Proportion proportion) =>
        proportion.Total == 0 ? string.Empty
        : proportion.Count == proportion.Total ? $"all {Number(proportion.Total)}; "
        : proportion.Count == 0 ? "none; "
        : string.Empty;

    private static string Meta(SimulationReport report, bool seedGiven)
    {
        var parts = new List<string>
        {
            $"{report.Edition} rules",
            $"{Number(report.Iterations)} fights",
            $"round cap {report.RoundCap.ToString(Invariant)}",
            report.EnemyHp == SimulationValues.EnemyHp.Roll ? "enemy HP rolled" : "enemy HP average",
            report.Surprise switch
            {
                SimulationValues.Surprise.Party => "the party is surprised",
                SimulationValues.Surprise.Enemies => "the enemies are surprised",
                _ => "no surprise",
            },
        };

        if (report.Precision is { } precision)
        {
            parts.Add(report.PrecisionReached == true
                ? $"precision ±{Percent(precision)} reached (±{Percent(report.PartyWins.HalfWidth)})"
                : $"precision ±{Percent(precision)} not reached in {Number(report.Iterations)} fights (±{Percent(report.PartyWins.HalfWidth)}){WorkLimit(report)}");
        }

        parts.Add($"seed {Seed(report.Seed)}{(seedGiven ? string.Empty : " (random)")}");
        return "*" + string.Join(" · ", parts) + "*";
    }

    /// <summary>
    /// Why precision mode stopped short of 100,000 fights, when it did: the next batch would have passed the work limit
    /// (<see cref="SimulationReport.PrecisionMaxFights"/>). Without it, "not reached in 20,000 fights" reads as a fight
    /// limit of 20,000, and nothing says that a lower round cap would buy more fights.
    /// </summary>
    private static string WorkLimit(SimulationReport report)
    {
        if (report.PrecisionMaxFights is not { } most || most >= SimulationLimits.MaxIterations)
        {
            return string.Empty;
        }

        var combatants = Plural(report.Combatants.Sum(c => c.Count), "combatant", "combatants");
        var cap = report.RoundCap.ToString(Invariant);
        return report.Compare is null
            ? $", the most the work limit allows for {combatants} to round cap {cap}; a lower round cap or fewer combatants allow more"
            : $", the most the work limit allows for {combatants} to round cap {cap} with compare; a lower round cap, fewer combatants " +
              "or no compare allow more";
    }

    private static string Combatants(SimulationReport report)
    {
        var rows = report.Combatants.Select(c => (IReadOnlyList<string>)
        [
            c.Count > 1 ? $"{c.Name} ×{c.Count.ToString(Invariant)}" : c.Name,
            c.Side == "party" ? "party" : "enemy",
            c.Source,
            c.ArmorClass.ToString(Invariant),
            Decimal(c.MaxHp, 1),
            Percent(c.DroppedToZero.Estimate),
            c.DeathSaves || c.Side == "party" ? Percent(c.DeadAtEnd.Estimate) : "—",
            c.DeathSaves ? Percent(c.DyingAtEnd.Estimate) : "—",
            $"{Decimal(c.HpLost.Mean, 1)} ({Number(c.HpLostP50)} / {Number(c.HpLostP90)})",
            $"{Decimal(c.DamageDealt.Mean, 1)} ({Decimal(c.DamageDealtEffective.Mean, 1)})",
            $"{Decimal(c.DamageTaken.Mean, 1)} ({Decimal(c.DamageTakenEffective.Mean, 1)})",
            Decimal(c.Kills.Mean, 2),
        ]);

        return "### Per combatant\n\n" +
               "Per creature per fight; copies of an entry are pooled. Damage is after resistances, (effective) counts only the " +
               "hit points it removed. Dead at end is shown for every party member and whatever makes death saves (the rest die " +
               "at 0 HP); dying at end, for what makes death saves: at 0 HP, not yet stable or dead, when the fight stopped. " +
               "Kills count the other side's deaths it caused (by damage, an outright kill such as Power Word Kill, or a sixth " +
               "level of exhaustion); a death from failed death saves (or from regeneration stopped at 0 HP) is credited to no one.\n\n" +
               SrdMarkdownText.Table(
                   ["Combatant", "Side", "From", "AC", "HP", "Dropped to 0", "Dead at end", "Dying at end", "HP lost mean (p50 / p90)", "Damage dealt (effective)", "Damage taken (effective)", "Kills"],
                   rows);
    }

    private static string? Resources(SimulationReport report)
    {
        var lines = new List<string>();
        foreach (var c in report.Combatants)
        {
            var items = c.Resources.Select(ResourceText).ToList();
            if (c.LegendaryResistanceSpent is { } lr)
            {
                items.Add($"Legendary Resistance {Decimal(lr, 2)} used");
            }

            if (c.LegendaryActions is { } legendary)
            {
                items.Add($"legendary actions {Decimal(legendary, 2)} taken");
            }

            if (items.Count == 0)
            {
                continue;
            }

            var shown = string.Join(" · ", items.Take(MaxResources));
            var more = items.Count > MaxResources ? $" · and {Plural(items.Count - MaxResources, "more", "more")}" : string.Empty;
            lines.Add($"- **{c.Name}**{(c.Count > 1 ? " (each)" : string.Empty)}: {shown}{more}");
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var body = string.Join("\n", lines.Take(MaxResourceLines));
        if (lines.Count > MaxResourceLines)
        {
            body += $"\n- … and {Plural(lines.Count - MaxResourceLines, "more combatant", "more combatants")} with limited resources.";
        }

        return "### Resources used per fight (mean)\n\n" + body;
    }

    // "Action Surge 0.97 of 1", "Fire Breath 1.80" (a recharge: no fixed number), "3rd-level slots 1.20 of 3".
    private static string ResourceText(ResourceUsage usage)
    {
        var name = SlotPool().Match(usage.Name) is { Success: true } pool
            ? $"{Ordinal(int.Parse(pool.Groups[1].Value, Invariant))}-level slots"
            : usage.Name;
        return usage.Available > 0
            ? $"{name} {Decimal(usage.MeanUsed, 2)} of {usage.Available.ToString(Invariant)}"
            : $"{name} {Decimal(usage.MeanUsed, 2)}";
    }

    // The simulator names a slot pool "spell slots (slot:3)" after its key.
    [System.Text.RegularExpressions.GeneratedRegex(@"^spell slots \(slot:(\d)\)$")]
    private static partial System.Text.RegularExpressions.Regex SlotPool();

    private static string Ordinal(int level) => level switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{level.ToString(Invariant)}th",
    };

    private static string? Compare(SimulationReport report)
    {
        if (report.Compare is not { } compare)
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "Party wins",
                $"{Percent(compare.BaselineWins.Estimate)} ({Interval(compare.BaselineWins)})",
                $"{Percent(compare.VariantWins.Estimate)} ({Interval(compare.VariantWins)})",
                Points(compare.WinDifference),
            },
            new[]
            {
                "A party member dies",
                $"{Percent(compare.BaselineAnyDeath.Estimate)} ({Interval(compare.BaselineAnyDeath)})",
                $"{Percent(compare.VariantAnyDeath.Estimate)} ({Interval(compare.VariantAnyDeath)})",
                Points(compare.AnyDeathDifference),
            },
            new[]
            {
                "Rounds (mean)",
                Decimal(compare.BaselineRounds.Mean, 2),
                Decimal(compare.VariantRounds.Mean, 2),
                $"{Signed(compare.RoundsDifference.Mean, 2)} ({Signed(compare.RoundsDifference.Low, 2)} to {Signed(compare.RoundsDifference.High, 2)})",
            },
        };

        // Beside the deaths whenever either run left a member dying: a feature that ends fights sooner leaves the fallen
        // fewer rounds to fail their death saves, so "a party member dies" alone reads as lives saved.
        var dying = compare.BaselineAnyDying.Count + compare.VariantAnyDying.Count > 0;
        if (dying)
        {
            rows.Insert(2, new[]
            {
                "A party member is left dying",
                $"{Percent(compare.BaselineAnyDying.Estimate)} ({Interval(compare.BaselineAnyDying)})",
                $"{Percent(compare.VariantAnyDying.Estimate)} ({Interval(compare.VariantAnyDying)})",
                Points(compare.AnyDyingDifference),
            });
        }

        return $"### Compare: {compare.MemberName} (party entry {compare.Member.ToString(Invariant)}) with {compare.Feature}\n\n" +
               $"The same {Number(report.Iterations)} fights (the same dice) without and with the feature; the difference is " +
               "paired fight by fight (95% CI), so it resolves far smaller changes than two separate runs could. The headline " +
               "above is the fight without the feature.\n\n" +
               SrdMarkdownText.Table(["Per fight", "Without", "With", "Difference (95% CI)"], rows) +
               (dying
                   ? "\n\n*Read the deaths with the dying: the dying's death saves are not rolled, and a feature that ends fights " +
                     "sooner leaves them fewer rounds to fail, so fewer deaths beside more left dying is not lives saved.*"
                   : string.Empty);
    }

    // "+1.3 points (+0.9 to +1.7)": a paired difference of two proportions, in percentage points.
    private static string Points(MeanEstimate difference) =>
        $"{Signed(difference.Mean * 100, 2)} points ({Signed(difference.Low * 100, 2)} to {Signed(difference.High * 100, 2)})";

    private static string? Warnings(SimulationReport report)
    {
        if (report.Warnings.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("### What the simulation leaves out\n\n");
        text.Append("From the SRD stat blocks as the simulator reads them (rules_get with format \"combatant\" shows one whole):\n");
        foreach (var block in report.Warnings.Take(MaxWarningBlocks))
        {
            var distinct = block.Warnings.DistinctBy(w => (w.Where, w.Message)).ToList();
            text.Append(Invariant, $"\n**{block.Name}** (`{block.Ref}`, {(block.Side == "party" ? "party" : "enemies")}):\n");
            foreach (var warning in distinct.Take(MaxWarningsPerBlock))
            {
                text.Append(Invariant, $"- {warning.Where}: {warning.Message}\n");
            }

            if (distinct.Count > MaxWarningsPerBlock)
            {
                text.Append(Invariant, $"- … and {Plural(distinct.Count - MaxWarningsPerBlock, "more", "more")}.\n");
            }
        }

        if (report.Warnings.Count > MaxWarningBlocks)
        {
            var rest = report.Warnings.Skip(MaxWarningBlocks)
                .Select(b => $"{b.Name} ({b.Warnings.Count.ToString(Invariant)})");
            text.Append(Invariant, $"\nAlso warned: {string.Join(", ", rest)}.\n");
        }

        return text.ToString().TrimEnd();
    }

    private static string Policies(SimulationReport report) =>
        "### Policies\n\n" + string.Join("\n", report.Policies.Select(p => $"- {p.Name} {p.Value}: {p.Meaning}"));

    private static string Assumptions(SimulationReport report)
    {
        var text = new StringBuilder("### Assumptions\n\n");
        var shown = 0;
        foreach (var assumption in report.Assumptions)
        {
            var line = "- " + assumption + "\n";
            if (shown > 0 && text.Length + line.Length > MaxAssumptionsChars)
            {
                break;
            }

            text.Append(line);
            shown++;
        }

        if (shown < report.Assumptions.Count)
        {
            text.Append(
                $"- … and {Plural(report.Assumptions.Count - shown, "more assumption", "more assumptions")} (the party archetypes' own " +
                "simplifications, listed last); a fight with fewer distinct archetypes shows them.\n");
        }

        return text.ToString().TrimEnd();
    }

    private static string? Notes(IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
        {
            return null;
        }

        var shown = string.Join("\n", notes.Take(MaxNotes).Select(n => "- " + n));
        var more = notes.Count > MaxNotes ? $"\n- … and {Plural(notes.Count - MaxNotes, "more note", "more notes")}." : string.Empty;
        return "### Notes\n\n" + shown + more;
    }

    private static string SeedLine(SimulationReport report, bool seedGiven) =>
        seedGiven
            ? $"Seed {Seed(report.Seed)} (given): the same call gives this result again, fight for fight."
            : $"Seed {Seed(report.Seed)} (drawn at random): pass \"seed\": \"{Seed(report.Seed)}\" with the same arguments to " +
              "reproduce this result exactly.";

    /// <summary>
    /// The replayed fight in a text block, within <paramref name="room"/> characters: the event lines are cut at a line
    /// when they do not fit, and the summary (from "Summary of fight") is always kept whole.
    /// </summary>
    private static string Replay(int iteration, SimulationReport report, string log, int room)
    {
        var at = log.LastIndexOf("Summary of fight ", StringComparison.Ordinal);
        var events = at >= 0 ? log[..at] : log;
        var summary = at >= 0 ? log[at..] : string.Empty;
        var eventRoom = Math.Max(0, room - summary.Length);
        string? cutNote = null;
        if (events.Length > eventRoom)
        {
            var cut = eventRoom <= 0 ? 0 : events.LastIndexOf('\n', Math.Max(0, eventRoom - 1));
            var kept = cut <= 0 ? string.Empty : events[..(cut + 1)];
            cutNote = $"… {Number(events.Length - kept.Length)} more characters of this fight's events are not shown, to keep the " +
                      "result readable; the summary below is complete.\n";
            events = kept;
        }

        var outcome = report.ReplayOutcome switch
        {
            SimulationValues.Outcomes.PartyWins => "the party wins",
            SimulationValues.Outcomes.PartyDefeated => "the party is defeated",
            _ => "a draw at the round cap",
        };
        var rounds = report.ReplayRounds is { } r ? $" in {Plural(r, "round", "rounds")}" : string.Empty;
        return $"### Fight {Number(iteration)}, turn by turn ({outcome}{rounds})\n\n" +
               "```text\n" + events + cutNote + summary.TrimEnd() + "\n```";
    }

    private static string Percent(double probability) => BalanceMarkdownText.Percent(probability);

    // "97.31–97.89%": a Wilson interval with its bounds printed like the estimate (never rounded onto 0% or 100%). At none
    // or all of the fights the bound on that side is exactly 0% or 100% (SimulationStatistics.Wilson makes it so).
    private static string Interval(Proportion proportion)
    {
        var low = Percent(proportion.Low);
        var high = Percent(proportion.High);
        return low.EndsWith('%') && !low.StartsWith('<') && !low.StartsWith('>') ? $"{low[..^1]}–{high}" : $"{low} to {high}";
    }

    private static string Decimal(double value, int decimals) =>
        double.IsFinite(value)
            ? BalanceMarkdownText.Round(value, decimals).ToString(decimals switch { 1 => "0.0", 2 => "0.00", _ => "0" }, Invariant)
            : "—";

    // "+0.21", "−0.40" (a true minus sign), "0.00".
    private static string Signed(double value, int decimals)
    {
        if (!double.IsFinite(value))
        {
            return "—";
        }

        var rounded = BalanceMarkdownText.Round(value, decimals);
        var format = decimals == 1 ? "0.0" : "0.00";
        return rounded switch
        {
            > 0 => "+" + rounded.ToString(format, Invariant),
            < 0 => "−" + (-rounded).ToString(format, Invariant),
            _ => rounded.ToString(format, Invariant),
        };
    }

    private static string Number(long value) => value.ToString("N0", Invariant);

    private static string Seed(ulong seed) => seed.ToString(Invariant);

    private static string Plural(long count, string one, string many) => $"{Number(count)} {(count == 1 ? one : many)}";
}
