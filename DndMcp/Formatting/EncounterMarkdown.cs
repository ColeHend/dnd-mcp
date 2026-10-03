using System.Globalization;
using System.Text;
using DndMcp.Domain.Encounters;
using DndMcp.Formatting.Srd;
using DndMcp.Repository.Srd;

namespace DndMcp.Formatting;

/// <summary>
/// Renders <c>encounter_difficulty</c>: the monsters as resolved (with the refs used, so every CR and XP can be checked),
/// then one section per edition with the answer in its heading, the party's numbers and the arithmetic, then what the
/// editions' bands mean side by side, notes, and sources.
///
/// <para>
/// <b>The label never stands alone.</b> Every label comes with the numbers it was judged on and the ones either side of
/// it ("reaches Medium (2,000) but not Hard (3,000)"), because both editions' labels are coarse and the model is asked
/// "how close to Deadly is this?" next. <b>Every interpretation is said where it applies</b>: the 2024 classification
/// rule, summing mixed levels, what excluding a monster does, and that "Trivial" and "Beyond High" are not book terms.
/// The sources line names the DMG for 2014, so its tables are never taken for SRD text.
/// </para>
/// </summary>
internal static class EncounterMarkdown
{
    /// <summary>The party sizes the multiplier table's plain column is for: "3–5".</summary>
    private static readonly string StandardParty = $"{EncounterTables2014.SmallPartyBelow}–{EncounterTables2014.LargePartyFrom - 1}";

    /// <summary>How many monsters "The editions compared" names when their XP differs between the editions.</summary>
    private const int MaxDifferingShown = 5;

    public static string Format(EncounterReport report)
    {
        var blocks = new List<string?>
        {
            "# Encounter difficulty",
            Party(report),
            Monsters(report),
        };

        if (report.For2014 is { } for2014)
        {
            blocks.Add(Section2014(report, for2014));
        }

        if (report.For2024 is { } for2024)
        {
            blocks.Add(Section2024(report, for2024));
        }

        if (report is { For2014: { } both2014, For2024: { } both2024 })
        {
            blocks.Add(Comparison(report, both2014, both2024));
        }

        blocks.Add(Notes(report));
        blocks.Add(Sources(report));
        return SrdMarkdownText.Blocks(blocks) + "\n";
    }

    private static string Party(EncounterReport report)
    {
        var text = new StringBuilder("**Party:** ").Append(PartyText(report.Levels)).Append('.');
        if (report.Effective is { } effective)
        {
            text.Append(CultureInfo.InvariantCulture, $" **Effective level:** {Signed(effective.Offset)} ({PartyText(effective.Levels)})");
            if (effective.ClampedCount > 0)
            {
                text.Append(CultureInfo.InvariantCulture, $"; {Plural(effective.ClampedCount, "character")} held to the 1–20 range");
            }

            text.Append('.');
        }

        return text.ToString();
    }

    private static string Monsters(EncounterReport report)
    {
        var entries = report.Entries;
        if (report.Editions.Count == 1)
        {
            var edition = report.Editions[0];
            var rows = entries.Select(e =>
            {
                var side = e.Sides[edition];
                return (IReadOnlyList<string>)
                [
                    MonsterCell(side.Name, side.Ref is null ? [] : [side.Ref], e, edition),
                    side.ChallengeRating.ToString(),
                    XpCell(side),
                    Number(e.Count),
                    Number((long)side.Xp * e.Count),
                ];
            }).ToList();
            rows.Add(
            [
                "**Total**",
                string.Empty,
                string.Empty,
                $"**{Number(entries.Sum(e => e.Count))}**",
                $"**{Number(TotalXp(entries, edition))}**",
            ]);
            return SrdMarkdownText.Table(["Monster", "CR", "XP each", "Count", "XP"], rows);
        }

        var bothRows = entries.Select(e =>
        {
            var s2014 = e.Sides[SrdEdition.Edition2014];
            var s2024 = e.Sides[SrdEdition.Edition2024];
            var name = s2014.Name == s2024.Name ? s2024.Name : $"{s2014.Name} (2014) / {s2024.Name} (2024)";
            var refs = new[] { s2014.Ref, s2024.Ref }.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            return (IReadOnlyList<string>)
            [
                MonsterCell(name, refs, e, edition: null),
                Number(e.Count),
                BothCell(s2014),
                BothCell(s2024),
            ];
        }).ToList();
        bothRows.Add(
        [
            "**Total**",
            $"**{Number(entries.Sum(e => e.Count))}**",
            $"**{Number(TotalXp(entries, SrdEdition.Edition2014))} XP**",
            $"**{Number(TotalXp(entries, SrdEdition.Edition2024))} XP**",
        ]);
        return SrdMarkdownText.Table(["Monster", "Count", "2014", "2024"], bothRows);
    }

    // "Ogre (`2024/monster/ogre`)", "Bandit boss (by CR)", plus what the 2014 multiplier leaves out.
    private static string MonsterCell(string name, IReadOnlyList<string> refs, EncounterEntry entry, string? edition)
    {
        var text = new StringBuilder(name);
        text.Append(refs.Count == 0 ? " (by CR)" : $" ({string.Join(", ", refs.Select(r => $"`{r}`"))})");
        if (entry.Exclude && edition != SrdEdition.Edition2024)
        {
            text.Append(" · excluded from the 2014 multiplier count");
        }

        return text.ToString();
    }

    // "CR 17, 20,000 XP (in lair)".
    private static string BothCell(EncounterEntrySide side) =>
        $"CR {side.ChallengeRating}, {Number(side.Xp)} XP" + (side.XpNote is null ? string.Empty : $" ({side.XpNote})");

    private static string XpCell(EncounterEntrySide side) =>
        side.XpNote is null ? Number(side.Xp) : $"{Number(side.Xp)} ({side.XpNote})";

    private static long TotalXp(IReadOnlyList<EncounterEntry> entries, string edition) =>
        entries.Sum(e => (long)e.Sides[edition].Xp * e.Count);

    private static string Section2014(EncounterReport report, Edition2014Report section)
    {
        var book = section.Book;
        var heading = Heading(SrdEdition.Edition2014, book.Difficulty, section.Effective?.Difficulty, report.Effective);

        var rows = LevelRows(report.Levels, (level, count) =>
        {
            var thresholds = EncounterTables2014.XpThresholds(level);
            return Cells(count, thresholds.Easy, thresholds.Medium, thresholds.Hard, thresholds.Deadly);
        });
        rows.AddRange(TotalRows(report, book.Thresholds, section.Effective?.Thresholds, t => [t.Easy, t.Medium, t.Hard, t.Deadly]));

        var lines = new List<string>
        {
            $"Monster XP {Number(book.MonsterXp)} {Times(book.Multiplier.Value)} = **{Adjusted(book.AdjustedXp)} adjusted XP**: " +
            $"{Times(book.Multiplier.Value)} for {MultiplierBasis(book)}.",
            Reach2014(book, prefix: "That"),
        };
        if (section.Effective is { } effective)
        {
            lines.Add(Reach2014(effective, prefix: $"At effective level {Signed(report.Effective!.Offset)} it"));
        }

        lines.Add(
            $"Adventuring day: this party can handle about {Number(book.AdventuringDayXp)} adjusted XP before a long rest, so this " +
            $"fight is {Percent(book.AdjustedXp, book.AdventuringDayXp)} of a day.");
        lines.Add($"The party earns {Earned(book.MonsterXp, report.Levels.Count)}; the multiplier only judges difficulty.");

        var excluded = section.Monsters.Where(m => m.Excluded).ToList();
        if (excluded.Count > 0)
        {
            lines.Add(
                $"Left out of the multiplier's count: {string.Join(", ", excluded.Select(m => $"{Number(m.Count)} × {m.Name}"))}. " +
                "Their XP still counts (the cautious reading of the DMG's rule).");
        }
        else if (section.Monsters.Select(m => m.ChallengeRating).Distinct().Count() > 1 && book.CountedMonsters > 1)
        {
            lines.Add(
                "The DMG says not to count monsters whose CR is significantly below the others' average toward the multiplier; " +
                "mark such a monster exclude: true to apply it.");
        }

        return SrdMarkdownText.Blocks(
        [
            heading,
            SrdMarkdownText.Table(["Party", "Easy", "Medium", "Hard", "Deadly"], rows),
            Bullets(lines),
        ]);
    }

    // "## 2014 rules: Medium", with the effective-level label when there is one ("Hard at effective level +1", or "the
    // same at effective level +1").
    private static string Heading(string edition, string difficulty, string? effective, EffectiveParty? party)
    {
        var heading = $"## {edition} rules: {EncounterDifficulty.Display(difficulty)}";
        if (effective is null || party is null)
        {
            return heading;
        }

        var label = effective == difficulty ? "the same" : EncounterDifficulty.Display(effective);
        return $"{heading} ({label} at effective level {Signed(party.Offset)})";
    }

    // "3 monsters (the 3–6 row, with 3–5 characters)", "1 counted monster, 10 excluded (…)", or the shifted forms.
    private static string MultiplierBasis(Encounter2014Result result)
    {
        var multiplier = result.Multiplier;
        var counted = result.CountedMonsters == result.MonsterCount
            ? Plural(result.MonsterCount, "monster")
            : $"{Plural(result.CountedMonsters, "counted monster")}, {Number(result.MonsterCount - result.CountedMonsters)} excluded";
        var row = multiplier.Row;
        return multiplier.PartySizeShift switch
        {
            > 0 => $"{counted} (the {row.Label} row's {Times(row.Standard)}, one step up for a party of fewer than {EncounterTables2014.SmallPartyBelow})",
            < 0 => $"{counted} (the {row.Label} row's {Times(row.Standard)}, one step down for a party of {EncounterTables2014.LargePartyFrom} or more)",
            _ => $"{counted} (the {row.Label} row, with {StandardParty} characters)",
        };
    }

    // "That reaches Medium (2,000) but not Hard (3,000)." and its variants at either end.
    private static string Reach2014(Encounter2014Result result, string prefix)
    {
        var t = result.Thresholds;
        return result.Difficulty switch
        {
            EncounterDifficulty.Trivial =>
                $"{prefix} is below Easy ({Number(t.Easy)}): trivial, a label the DMG does not use.",
            EncounterDifficulty.Easy => $"{prefix} reaches Easy ({Number(t.Easy)}) but not Medium ({Number(t.Medium)}).",
            EncounterDifficulty.Medium => $"{prefix} reaches Medium ({Number(t.Medium)}) but not Hard ({Number(t.Hard)}).",
            EncounterDifficulty.Hard => $"{prefix} reaches Hard ({Number(t.Hard)}) but not Deadly ({Number(t.Deadly)}).",
            _ => $"{prefix} reaches Deadly ({Number(t.Deadly)}), at {Ratio(result.AdjustedXp, t.Deadly)} the threshold.",
        };
    }

    private static string Section2024(EncounterReport report, Edition2024Report section)
    {
        var book = section.Book;
        var heading = Heading(SrdEdition.Edition2024, book.Difficulty, section.Effective?.Difficulty, report.Effective);

        var rows = LevelRows(report.Levels, (level, count) =>
        {
            var budget = EncounterTables2024.XpBudget(level);
            return Cells(count, budget.Low, budget.Moderate, budget.High);
        });
        rows.AddRange(TotalRows(report, book.Budget, section.Effective?.Budget, b => [b.Low, b.Moderate, b.High]));

        var lines = new List<string> { Fit2024(book, prefix: $"Monster XP **{Number(book.MonsterXp)}**") };
        if (section.Effective is { } effective)
        {
            lines.Add(Fit2024(effective, prefix: $"At effective level {Signed(report.Effective!.Offset)} it"));
        }

        lines.Add($"No multiplier for groups in 2024. The party earns {Earned(book.MonsterXp, report.Levels.Count)}.");

        var blocks = new List<string?>
        {
            heading,
            SrdMarkdownText.Table(["Party", "Low", "Moderate", "High"], rows),
            Bullets(lines),
        };
        if (section.Warnings.Count > 0)
        {
            blocks.Add("**Troubleshooting (SRD 5.2.1):**\n" + Bullets(section.Warnings.Select(w => w.Text)));
        }

        return SrdMarkdownText.Blocks(blocks);
    }

    // "Monster XP **1,350** fits the Low budget (2,000), 68% of it." and its variants.
    private static string Fit2024(Encounter2024Result result, string prefix)
    {
        var b = result.Budget;
        return result.Difficulty switch
        {
            EncounterDifficulty.Low => $"{prefix} fits the Low budget ({Number(b.Low)}), {Percent(result.MonsterXp, b.Low)} of it.",
            EncounterDifficulty.Moderate =>
                $"{prefix} is over the Low budget ({Number(b.Low)}) and fits Moderate ({Number(b.Moderate)}).",
            EncounterDifficulty.High =>
                $"{prefix} is over the Moderate budget ({Number(b.Moderate)}) and fits High ({Number(b.High)}).",
            _ => $"{prefix} is over the High budget ({Number(b.High)}) by {Number(result.MonsterXp - b.High)} " +
                 $"({Ratio(result.MonsterXp, b.High)} the budget). The SRD's difficulties stop at High, so expect worse than High.",
        };
    }

    /// <summary>
    /// What the two editions' bands mean at this party's levels, computed rather than quoted: "2024 Low = 2014 Medium"
    /// holds only up to level 7 (Moderate = Hard to 5, High = Deadly to 8), and a flat claim misleads above that.
    /// </summary>
    private static string Comparison(EncounterReport report, Edition2014Report for2014, Edition2024Report for2024)
    {
        var t = for2014.Book.Thresholds;
        var b = for2024.Book.Budget;
        var pairs = new (string Name2024, long Budget, string Name2014, long Threshold)[]
        {
            ("Low", b.Low, "Medium", t.Medium),
            ("Moderate", b.Moderate, "Hard", t.Hard),
            ("High", b.High, "Deadly", t.Deadly),
        };

        var lines = new List<string>();
        if (pairs.All(p => p.Budget == p.Threshold))
        {
            lines.Add(
                "At these levels the 2024 Low, Moderate and High budgets equal the 2014 Medium, Hard and Deadly thresholds.");
        }
        else
        {
            lines.Add(
                "The 2024 budgets are not the 2014 thresholds renamed: Low equals Medium only up to level 7, Moderate equals Hard " +
                "up to level 5 and High equals Deadly up to level 8. For this party: " +
                string.Join("; ", pairs.Select(p => $"{p.Name2024} {Number(p.Budget)} vs {p.Name2014} {Number(p.Threshold)}")) + ".");
        }

        // The labels run in opposite directions: equal numbers between Medium and Hard are 2014 Medium but 2024 Moderate.
        lines.Add(
            "The labels are read in opposite directions: 2014 names a fight by the highest threshold its adjusted XP reaches, " +
            "2024 by the smallest budget its XP fits. So with equal numbers, XP between Medium and Hard is 2014 Medium but " +
            "2024 Moderate, and anything up to the Low budget is 2024 Low even where 2014 calls it Trivial.");

        var multiplier = for2014.Book.Multiplier.Value;
        var sameXp = for2014.Book.MonsterXp == for2024.Book.MonsterXp;
        lines.Add(multiplier == 1m
            ? sameXp
                ? "2014's multiplier is × 1 here, so both editions judge the same XP total."
                : "2014's multiplier is × 1 here, so each edition judges its own plain XP total."
            : $"2014 judges {Adjusted(for2014.Book.AdjustedXp)} adjusted XP ({Times(multiplier)}); 2024 judges the plain " +
              $"{Number(for2024.Book.MonsterXp)}.");

        if (!sameXp)
        {
            // Say which monsters differ and by how much, not why: a lair, a "0 or 10" CR 0 stat block or a changed CR all do it.
            var differing = report.Entries
                .Where(e => e.Sides[SrdEdition.Edition2014].Xp != e.Sides[SrdEdition.Edition2024].Xp)
                .Select(e => $"{e.Name} is worth {XpCell(e.Sides[SrdEdition.Edition2014])} XP in 2014 and " +
                             $"{XpCell(e.Sides[SrdEdition.Edition2024])} in 2024")
                .ToList();
            lines.Add(
                $"The monsters' XP differs between the editions ({Number(for2014.Book.MonsterXp)} in 2014, " +
                $"{Number(for2024.Book.MonsterXp)} in 2024): {string.Join("; ", differing.Take(MaxDifferingShown))}" +
                (differing.Count > MaxDifferingShown ? $"; and {differing.Count - MaxDifferingShown} more" : string.Empty) + ".");
        }

        return "## The editions compared\n\n" + Bullets(lines);
    }

    private static string? Notes(EncounterReport report)
    {
        // What the campaign decided comes first: it says which rules and levels every number above was computed for.
        var notes = report.CampaignNotes.Concat(report.Entries.SelectMany(e => e.Notes)).Distinct(StringComparer.Ordinal).ToList();
        if (report.For2014 is null && report.Entries.Any(e => e.Exclude))
        {
            notes.Add("exclude changes only the 2014 multiplier's count; the 2024 method counts every creature's XP.");
        }

        if (report.For2024 is not null)
        {
            notes.Add(
                "2024 classifies a fight as the lowest difficulty whose budget its XP fits, as the SRD's worked examples do (the " +
                "SRD describes only building to a budget).");
            if (report.Levels.Distinct().Count() > 1)
            {
                notes.Add("Mixed levels: each character's own budget is summed (the SRD multiplies one party level by the number of characters).");
            }
        }

        return notes.Count == 0 ? null : "**Notes:**\n" + Bullets(notes);
    }

    private static string Sources(EncounterReport report)
    {
        var sources = new List<string>();
        if (report.For2014 is not null)
        {
            sources.Add(
                "2014 thresholds, multipliers and adventuring-day XP: Dungeon Master's Guide (2014), pp. 82–84, also in the free " +
                "2014 Basic Rules (not SRD 5.1)");
        }

        if (report.For2024 is not null)
        {
            sources.Add("2024 budget and troubleshooting: SRD 5.2.1, Gameplay Toolbox › Combat Encounter Difficulty");
        }

        sources.Add("CR and XP: the SRD stat blocks, or the XP by CR table for a monster given by CR");
        return $"*Sources: {string.Join("; ", sources)}. The tables: rules_get ref \"{RulesTables.IndexUri}\".*";
    }

    // One row per distinct level, highest first: "4 × level 5".
    private static List<IReadOnlyList<string>> LevelRows(IReadOnlyList<int> levels, Func<int, int, IReadOnlyList<string>> cells) =>
        levels.GroupBy(l => l).OrderByDescending(g => g.Key)
            .Select(g => (IReadOnlyList<string>)[$"{Number(g.Count())} × level {Number(g.Key)}", .. cells(g.Key, g.Count())])
            .ToList();

    // Each level's value times its count.
    private static IReadOnlyList<string> Cells(int count, params long[] perCharacter) =>
        perCharacter.Select(v => Number(v * count)).ToList();

    // The party total (when levels differ) and the effective-level total (when there is one).
    private static IEnumerable<IReadOnlyList<string>> TotalRows<T>(EncounterReport report, T book, T? effective, Func<T, long[]> values)
        where T : class
    {
        if (report.Levels.Distinct().Count() > 1)
        {
            yield return [$"**Party ({Number(report.Levels.Count)})**", .. values(book).Select(v => $"**{Number(v)}**")];
        }

        if (effective is not null)
        {
            yield return [$"Effective level ({Signed(report.Effective!.Offset)})", .. values(effective).Select(Number)];
        }
    }

    private static string Bullets(IEnumerable<string> lines) => string.Join("\n", lines.Select(l => "- " + l));

    // "4 characters at level 5" or "4 characters: 2 at level 5, 1 at level 4, 1 at level 3".
    private static string PartyText(IReadOnlyList<int> levels)
    {
        var groups = levels.GroupBy(l => l).OrderByDescending(g => g.Key).ToList();
        return groups.Count == 1
            ? $"{Plural(levels.Count, "character")} at level {Number(groups[0].Key)}"
            : $"{Plural(levels.Count, "character")}: {string.Join(", ", groups.Select(g => $"{Number(g.Count())} at level {Number(g.Key)}"))}";
    }

    // "1,350 XP (about 337 each)": whole XP, so a share that does not divide evenly says it is rounded.
    private static string Earned(long xp, int characters)
    {
        if (characters == 1)
        {
            return $"{Number(xp)} XP";
        }

        var each = xp / characters;
        return xp % characters == 0 ? $"{Number(xp)} XP ({Number(each)} each)" : $"{Number(xp)} XP (about {Number(each)} each)";
    }

    // Rounded down, so a total one XP short of a budget never reads as 100% of it, and a sliver as "under 1%", never "0%".
    private static string Percent(decimal part, long whole)
    {
        var percent = whole == 0 ? 0m : 100m * part / whole;
        return percent > 0m && percent < 1m
            ? "under 1%"
            : Math.Floor(percent).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private static string Ratio(decimal value, long of) =>
        (value / of).ToString("0.0#", CultureInfo.InvariantCulture) + "×";

    private static string Adjusted(decimal xp) => xp.ToString("#,##0.#", CultureInfo.InvariantCulture);

    private static string Times(decimal multiplier) => "× " + multiplier.ToString("0.#", CultureInfo.InvariantCulture);

    // "+1", "−2" (a true minus sign, as SrdMarkdownText.Signed prints one).
    private static string Signed(int value) => SrdMarkdownText.Signed(value);

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{Number(count)} {noun}s";
}

/// <summary>Everything <see cref="EncounterMarkdown"/> shows: the inputs as resolved, and each edition's results.</summary>
/// <param name="Effective">The party at its effective level, or null when no offset was given.</param>
internal sealed record EncounterReport(
    IReadOnlyList<int> Levels,
    EffectiveParty? Effective,
    IReadOnlyList<string> Editions,
    IReadOnlyList<EncounterEntry> Entries,
    Edition2014Report? For2014,
    Edition2024Report? For2024)
{
    /// <summary>
    /// The notes saying the active campaign supplied the edition or the level offset (<see cref="CampaignDefaultNotes"/>),
    /// shown first among the notes; empty when the call gave both or no campaign is active.
    /// </summary>
    public IReadOnlyList<string> CampaignNotes { get; init; } = [];
}

/// <summary>
/// One monsters item as resolved: a display name (its first edition's stat block, or its label), how many, whether 2014
/// leaves it out of the multiplier's count, the stat block used per edition, and notes.
/// </summary>
internal sealed record EncounterEntry(
    string Name, int Count, bool Exclude, IReadOnlyDictionary<string, EncounterEntrySide> Sides, IReadOnlyList<string> Notes);

/// <summary>The stat block one edition's maths used for an entry (<see cref="Ref"/> null for a monster given by CR).</summary>
/// <param name="XpNote">Why the XP is not the plain stat block XP ("in lair"), or null.</param>
internal sealed record EncounterEntrySide(string Name, string? Ref, ChallengeRating ChallengeRating, int Xp, string? XpNote);

internal sealed record Edition2014Report(IReadOnlyList<EncounterMonster> Monsters, Encounter2014Result Book, Encounter2014Result? Effective);

internal sealed record Edition2024Report(
    Encounter2024Result Book,
    Encounter2024Result? Effective,
    IReadOnlyList<EncounterWarning> Warnings);
