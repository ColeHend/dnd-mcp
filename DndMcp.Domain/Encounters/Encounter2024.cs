using System.Globalization;

namespace DndMcp.Domain.Encounters;

/// <summary>
/// The 2024 method (SRD 5.2.1, Gameplay Toolbox › Combat Encounter Difficulty): an XP budget per character for Low,
/// Moderate and High, summed over the party, against the monsters' plain XP total. No multiplier.
///
/// <para>
/// <b>Classifying an encounter someone already built is an interpretation</b>, because the SRD describes only building
/// one: choose a difficulty, then "spend as much of your XP budget as you can without going over". So a fight is the
/// lowest difficulty whose budget its XP fits: its own worked examples call 150 XP for four level 1 characters (budget
/// 200) a Low encounter, and 1,100 XP for five level 3 characters (Low 750, Moderate 1,125) a Moderate one. Reading it the
/// other way ("the highest band the total reaches", like 2014's thresholds) calls both of those one band lower, and
/// every fight under the Low budget would have no name. Above the High budget there is no SRD difficulty;
/// <see cref="EncounterDifficulty.BeyondHigh"/> says so.
/// </para>
/// <para>
/// Mixed levels are an interpretation too: the SRD multiplies one "party's level" by the number of characters; this sums
/// each character's own budget, which is the same for a single-level party.
/// </para>
/// </summary>
public static class Encounter2024
{
    /// <summary>"If your encounter includes more than two creatures per character, include fragile creatures…"</summary>
    public const int MaxCreaturesPerCharacter = 2;

    /// <summary>"Referencing more than two or three stat blocks for a single encounter can be daunting…"</summary>
    public const int MaxStatBlocks = 3;

    /// <summary>
    /// More CR 0 creatures than this counts as "many" ("If you want to include many CR 0 critters in an encounter, use
    /// swarms instead"). The SRD gives no number, only "sparingly"; two matches the other limits' "more than two", and the
    /// warning quotes the SRD's advice, not this number.
    /// </summary>
    public const int MaxCrZeroCreatures = 2;

    /// <summary>"This guideline is especially important for characters of level 1 or 2."</summary>
    public const int FragileLevelMax = 2;

    public static class Warnings
    {
        public const string ManyCreatures = "many-creatures";
        public const string PowerfulCreature = "powerful-creature";
        public const string CrZero = "cr-zero";
        public const string ManyStatBlocks = "many-stat-blocks";
    }

    public static Encounter2024Result Assess(IReadOnlyList<int> levels, IReadOnlyList<EncounterMonster> monsters)
    {
        EncounterLimits.ValidateParty(levels);
        EncounterLimits.ValidateMonsters(monsters);

        var budget = levels.Aggregate(XpBudget2024.Zero, (sum, level) => sum + EncounterTables2024.XpBudget(level));
        var monsterXp = monsters.Sum(m => m.TotalXp);
        return new Encounter2024Result(budget, monsterXp, Classify(monsterXp, budget));
    }

    /// <summary>The lowest difficulty whose budget <paramref name="xp"/> fits; <see cref="EncounterDifficulty.BeyondHigh"/> above High.</summary>
    public static string Classify(long xp, XpBudget2024 budget) =>
        xp <= budget.Low ? EncounterDifficulty.Low
        : xp <= budget.Moderate ? EncounterDifficulty.Moderate
        : xp <= budget.High ? EncounterDifficulty.High
        : EncounterDifficulty.BeyondHigh;

    /// <summary>
    /// The SRD's "Troubleshooting" advice that applies to this encounter, judged at the characters' printed levels (a
    /// level 1 character's hit points do not rise with an effective-level offset). "Adjustments" and "Unusual Features"
    /// need a person's judgement and are not checked; "Unusual Features" rides along with a powerful creature.
    /// </summary>
    public static IReadOnlyList<EncounterWarning> Troubleshoot(IReadOnlyList<int> levels, IReadOnlyList<EncounterMonster> monsters)
    {
        EncounterLimits.ValidateParty(levels);
        EncounterLimits.ValidateMonsters(monsters);

        var warnings = new List<EncounterWarning>();
        var creatures = monsters.Sum(m => m.Count);
        if (creatures > MaxCreaturesPerCharacter * levels.Count)
        {
            var lowLevels = levels.Any(l => l <= FragileLevelMax) ? " This matters most for characters of level 1 or 2, as some here are." : string.Empty;
            warnings.Add(new EncounterWarning(
                Warnings.ManyCreatures,
                $"{Creatures(creatures)} against {Characters(levels.Count)} is more than {MaxCreaturesPerCharacter} per character, so a lucky streak can " +
                $"deal more damage than expected: include fragile creatures that can be defeated quickly.{lowLevels}"));
        }

        foreach (var statBlock in monsters.GroupBy(m => m.StatBlock, StringComparer.Ordinal).Select(g => g.MaxBy(m => m.ChallengeRating)!))
        {
            var below = levels.Count(l => statBlock.ChallengeRating.Value > l);
            if (below == 0)
            {
                continue;
            }

            var lowest = levels.Min();
            var whom = below == levels.Count
                ? levels.Distinct().Count() == 1 ? $"the party's level ({lowest})" : "every character's level"
                : $"the level of {below} of the {levels.Count} characters (lowest {lowest})";
            warnings.Add(new EncounterWarning(
                Warnings.PowerfulCreature,
                $"{statBlock.Name} is CR {statBlock.ChallengeRating}, above {whom}: it might take out one or more characters with a " +
                "single action. Also check it has no feature those characters can't easily overcome."));
        }

        var crZero = monsters.Where(m => m.ChallengeRating == ChallengeRating.Zero).ToList();
        var crZeroCount = crZero.Sum(m => m.Count);
        var zeroXp = crZero.Where(m => m.Xp == 0).Sum(m => m.Count);
        if (crZeroCount > MaxCrZeroCreatures || zeroXp > 0)
        {
            var which = zeroXp == 0 ? $"{Creatures(crZeroCount)} of CR 0"
                : zeroXp == crZeroCount ? $"{Creatures(crZeroCount)} of CR 0 worth 0 XP, which the budget cannot see"
                : $"{Creatures(crZeroCount)} of CR 0, {zeroXp.ToString(CultureInfo.InvariantCulture)} of them worth 0 XP, which the budget cannot see";
            warnings.Add(new EncounterWarning(
                Warnings.CrZero,
                $"{which}: the SRD says to use CR 0 creatures sparingly, and a swarm instead of many."));
        }

        var statBlocks = monsters.Select(m => m.StatBlock).Distinct(StringComparer.Ordinal).Count();
        if (statBlocks > MaxStatBlocks)
        {
            warnings.Add(new EncounterWarning(
                Warnings.ManyStatBlocks,
                $"{statBlocks.ToString(CultureInfo.InvariantCulture)} different stat blocks: more than two or three for one encounter " +
                "can be daunting to run, particularly if the creatures are complex."));
        }

        return warnings;
    }

    private static string Creatures(int n) => n == 1 ? "1 creature" : $"{n.ToString("N0", CultureInfo.InvariantCulture)} creatures";

    private static string Characters(int n) => n == 1 ? "1 character" : $"{n.ToString(CultureInfo.InvariantCulture)} characters";
}

/// <param name="Budget">The party's summed budget for each difficulty.</param>
/// <param name="MonsterXp">Every creature's XP: both what is judged and what the party earns.</param>
public sealed record Encounter2024Result(XpBudget2024 Budget, long MonsterXp, string Difficulty);
