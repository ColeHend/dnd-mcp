using DndMcp.Domain.Core;

namespace DndMcp.Domain.Encounters;

/// <summary>
/// The 2014 DMG's method for gauging a combat encounter (DMG 2014 pp. 82–83, "Evaluating Encounter Difficulty"):
/// <list type="number">
/// <item>Sum each character's XP thresholds (mixed levels are summed per character, as the DMG does).</item>
/// <item>Total the monsters' XP.</item>
/// <item>Multiply by the Encounter Multiplier for the number of monsters, shifted one step for parties under three or
/// over five characters. Monsters flagged <see cref="EncounterMonster.Excluded"/> do not count toward that number.</item>
/// <item>The difficulty is the highest threshold the adjusted XP reaches; below Easy it is "trivial", which is not a
/// DMG term.</item>
/// </list>
///
/// <para>
/// <b>Exclusion is an interpretation.</b> The DMG says, of the multiplier step, "don't count any monsters whose challenge
/// rating is significantly below the average challenge rating of the other monsters". Read here as: they do not raise the
/// monster count (the step exists because more monsters make more attack rolls), and their XP still counts, which is the
/// cautious reading. It never happens automatically, because the DMG gives no number for "significantly".
/// </para>
/// <para>
/// The adjusted XP is only for judging difficulty; the XP the party earns is <see cref="Encounter2014Result.MonsterXp"/>.
/// </para>
/// </summary>
public static class Encounter2014
{
    public static Encounter2014Result Assess(IReadOnlyList<int> levels, IReadOnlyList<EncounterMonster> monsters)
    {
        EncounterLimits.ValidateParty(levels);
        EncounterLimits.ValidateMonsters(monsters);

        var thresholds = levels.Aggregate(XpThresholds2014.Zero, (sum, level) => sum + EncounterTables2014.XpThresholds(level));
        var monsterXp = monsters.Sum(m => m.TotalXp);
        var monsterCount = monsters.Sum(m => m.Count);
        var counted = monsters.Where(m => !m.Excluded).Sum(m => m.Count);
        if (counted == 0)
        {
            throw new DndInputException(
                "Every monster is marked exclude, so none is left to count for the 2014 multiplier. exclude is for monsters whose " +
                "CR is far below the others'; leave it off at least the main threat.");
        }

        var multiplier = EncounterTables2014.Multiplier(counted, levels.Count);
        var adjusted = monsterXp * multiplier.Value;
        var dayXp = levels.Sum(l => (long)EncounterTables2014.AdventuringDayXp(l));

        return new Encounter2014Result(thresholds, monsterXp, monsterCount, counted, multiplier, adjusted, Classify(adjusted, thresholds), dayXp);
    }

    /// <summary>The highest threshold <paramref name="adjustedXp"/> reaches; <see cref="EncounterDifficulty.Trivial"/> below Easy.</summary>
    public static string Classify(decimal adjustedXp, XpThresholds2014 thresholds) =>
        adjustedXp >= thresholds.Deadly ? EncounterDifficulty.Deadly
        : adjustedXp >= thresholds.Hard ? EncounterDifficulty.Hard
        : adjustedXp >= thresholds.Medium ? EncounterDifficulty.Medium
        : adjustedXp >= thresholds.Easy ? EncounterDifficulty.Easy
        : EncounterDifficulty.Trivial;
}

/// <param name="Thresholds">The party's summed thresholds.</param>
/// <param name="MonsterXp">Every monster's XP, excluded ones included: what the party earns.</param>
/// <param name="MonsterCount">Every creature.</param>
/// <param name="CountedMonsters">The creatures that picked the multiplier (not excluded).</param>
/// <param name="AdjustedXp">MonsterXp × the multiplier. A × 1.5 or × 2.5 step can make it a half.</param>
/// <param name="AdventuringDayXp">The party's summed Adventuring Day XP: adjusted XP it can handle before a long rest.</param>
public sealed record Encounter2014Result(
    XpThresholds2014 Thresholds,
    long MonsterXp,
    int MonsterCount,
    int CountedMonsters,
    EncounterMultiplier Multiplier,
    decimal AdjustedXp,
    string Difficulty,
    long AdventuringDayXp);
