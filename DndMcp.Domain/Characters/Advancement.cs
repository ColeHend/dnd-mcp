using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// Character advancement: the XP each level needs and the proficiency bonus at each level (identical in both editions),
/// and the class levels that bring an Ability Score Improvement or an Epic Boon.
///
/// <para>
/// <b>Source.</b> The Character Advancement table is quoted from SRD 5.1 "Beyond 1st Level" (2014) and SRD 5.2 "Character
/// Creation" › Level Advancement (2024), both under CC-BY-4.0 (<c>content/LICENSES</c>); the two tables are the same row
/// for row. It is NOT in the vendored 5e data (whose level records carry the proficiency bonus but no XP), which is why it
/// is typed here once and served from here (<c>rules://tables/character-advancement</c>): the instructions say Character
/// Creation is missing "except its advancement table". 2024 "When your XP total equals or exceeds a number in the
/// Experience Points column, you reach the corresponding level."
/// </para>
/// <para>
/// <b>ASI levels</b> are the 5e data's level-record features (<c>&lt;class&gt;-ability-score-improvement</c>, 2024
/// <c>&lt;class&gt;-epic-boon</c>), pinned by a test against both editions' files: every class at 4, 8, 12, 16; the
/// fighter also at 6 and 14; the rogue also at 10; level 19 is an ASI in 2014 and an Epic Boon in 2024. They count CLASS
/// levels, not character levels.
/// </para>
/// </summary>
public static class Advancement
{
    /// <summary>The source line a table rendering cites.</summary>
    public const string Source =
        "SRD 5.1 \"Beyond 1st Level\" (2014) and SRD 5.2 \"Character Creation\" › Level Advancement (2024), CC-BY-4.0; " +
        "the same in both editions";

    /// <summary>XP needed for levels 1-20 (index 0 = level 1).</summary>
    private static readonly int[] Thresholds =
    [
        0, 300, 900, 2_700, 6_500, 14_000, 23_000, 34_000, 48_000, 64_000,
        85_000, 100_000, 120_000, 140_000, 165_000, 195_000, 225_000, 265_000, 305_000, 355_000,
    ];

    /// <summary>The table's rows, level 1 to 20.</summary>
    public static IReadOnlyList<AdvancementRow> Rows { get; } =
        Enumerable.Range(DslLimits.MinLevel, DslLimits.MaxLevel)
            .Select(level => new AdvancementRow(level, Thresholds[level - 1], DslLimits.ProficiencyBonus(level)))
            .ToList();

    /// <summary>The XP a level needs (level 1: 0).</summary>
    public static int XpFor(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, DslLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, DslLimits.MaxLevel);
        return Thresholds[level - 1];
    }

    /// <summary>The highest level whose threshold <paramref name="xp"/> equals or exceeds (negative XP reads as 0).</summary>
    public static int LevelForXp(int xp)
    {
        var level = DslLimits.MinLevel;
        while (level < DslLimits.MaxLevel && xp >= Thresholds[level])
        {
            level++;
        }

        return level;
    }

    /// <summary>The XP the next level needs, or null at level 20.</summary>
    public static int? NextThreshold(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, DslLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, DslLimits.MaxLevel);
        return level == DslLimits.MaxLevel ? null : Thresholds[level];
    }

    /// <summary>The proficiency bonus at a character level (+2 at 1-4 … +6 at 17-20; <see cref="DslLimits.ProficiencyBonus"/>).</summary>
    public static int ProficiencyBonus(int level) => DslLimits.ProficiencyBonus(level);

    /// <summary>
    /// The class levels at which an SRD class gains an Ability Score Improvement in an edition (2024: without level 19,
    /// which is <see cref="EpicBoonLevel"/>).
    /// </summary>
    /// <param name="classIndex">An SRD class index (<see cref="ClassTable"/>).</param>
    public static IReadOnlyList<int> AsiLevels(string edition, string classIndex)
    {
        IReadOnlyList<int> standard = edition == V.Editions.E2014 ? [4, 8, 12, 16, 19] : [4, 8, 12, 16];
        IReadOnlyList<int> extra = classIndex switch
        {
            "fighter" => [6, 14],
            "rogue" => [10],
            _ => [],
        };

        return [.. standard.Concat(extra).Order()];
    }

    /// <summary>The 2024 class level of the Epic Boon (every class). The 2014 rules have none (level 19 is an ASI).</summary>
    public const int EpicBoonLevel = 19;

    /// <summary>
    /// What reaching <paramref name="classLevel"/> in an SRD class brings: <see cref="SheetValues.ReminderKinds.AbilityScoreImprovement"/>,
    /// <see cref="SheetValues.ReminderKinds.EpicBoon"/> (2024 level 19) or null.
    /// </summary>
    public static string? ImprovementAt(string edition, string classIndex, int classLevel)
    {
        if (edition == V.Editions.E2024 && classLevel == EpicBoonLevel)
        {
            return SheetValues.ReminderKinds.EpicBoon;
        }

        return AsiLevels(edition, classIndex).Contains(classLevel) ? SheetValues.ReminderKinds.AbilityScoreImprovement : null;
    }
}

/// <summary>One row of the Character Advancement table.</summary>
public sealed record AdvancementRow(int Level, int Xp, int ProficiencyBonus);
