namespace DndMcp.Domain.Features;

/// <summary>
/// Every bound the feature DSL enforces, in one place so the validators, the descriptions the model reads and the tests
/// quote the same numbers.
///
/// <para>
/// The bounds on dice, attacks per turn and resource-limited modifiers are what keep the DPR engine's exact dynamic
/// program small: its state carries one flag per once-per-turn rider and a use count per resource, and every attack
/// multiplies the states. They are generous for real characters (a level 20 fighter makes 8 attacks with Action Surge)
/// and exist so a malformed build fails with a message instead of exhausting the work budget.
/// </para>
/// </summary>
public static class DslLimits
{
    public const int MinLevel = 1;
    public const int MaxLevel = 20;

    public const int MaxBuildNameLength = 80;

    /// <summary>Attack and modifier names are labels in result tables.</summary>
    public const int MaxItemNameLength = 60;

    public const int MaxAttacks = 10;
    public const int MaxModifiers = 20;

    public const int MinAbilityScore = 1;
    public const int MaxAbilityScore = 30;

    /// <summary>The default for any ability a build does not give.</summary>
    public const int DefaultAbilityScore = 10;

    public const int MinProficiencyBonus = 2;
    public const int MaxProficiencyBonus = 9;

    /// <summary>One attack item's count (before cantrip beams multiply it), and one extra_attack's.</summary>
    public const int MaxAttackCount = 10;

    /// <summary>Every attack of a turn at one level: all counts, extra attacks included.</summary>
    public const int MaxAttacksPerTurn = 20;

    /// <summary>Modifiers with a resource active at one level (the engine tracks uses left for each).</summary>
    public const int MaxResourceModifiers = 3;

    /// <summary>Advantage sources with a rate below 1 active at one level (the engine mixes over 2^n on/off cases).</summary>
    public const int MaxRateAdvantageSources = 3;

    /// <summary>Power attacks active at one level (the engine tries every on/off combination per turn).</summary>
    public const int MaxPowerAttacks = 3;

    /// <summary>Dice in one formula ("50d6"), after adding up its terms.</summary>
    public const int MaxDice = 50;

    public const int MaxSides = 100;

    /// <summary>|flat| in a formula, and the range of an integer amount.</summary>
    public const int MaxFlat = 100;

    public const int MinToHitBonus = -20;
    public const int MaxToHitBonus = 20;

    public const int MinToHitTotal = -10;
    public const int MaxToHitTotal = 30;

    public const int MinDc = 1;
    public const int MaxDc = 40;

    public const int MaxDcBonus = 10;

    public const int MaxTargets = 20;

    /// <summary>Creatures one heal restores (Mass Healing Word: up to six).</summary>
    public const int MaxHealTargets = 6;

    /// <summary>An area's size in feet.</summary>
    public const int MaxAreaSize = 1000;

    public const int MaxResourceUses = 20;

    public const int MaxPowerAttackPenalty = 20;
    public const int MaxPowerAttackBonus = 100;

    /// <summary>use_value: the damage one use of a resource is worth elsewhere.</summary>
    public const double MaxUseValue = 1000;

    public const int MinTargetAc = 1;
    public const int MaxTargetAc = 40;

    public const int MinSaveBonus = -5;
    public const int MaxSaveBonus = 20;

    public const int MaxTargetHp = 5000;

    public const int MaxLegendaryResistance = 5;

    /// <summary>
    /// target.monster: one line, a ref ("2024/monster/adult-red-dragon") or a name. Longer text is not a monster, and the
    /// host's lookup would only echo it back.
    /// </summary>
    public const int MaxMonsterTextLength = 100;

    /// <summary>Problems one error message lists before "and N more".</summary>
    public const int MaxReportedProblems = 5;

    /// <summary>
    /// The proficiency bonus by character level, the same in both editions: +2 at 1–4, +1 every four levels, +6 at 17–20.
    /// </summary>
    public static int ProficiencyBonus(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, MaxLevel);
        return 2 + (level - 1) / 4;
    }

    /// <summary>An ability score's modifier: (score − 10) / 2 rounded down (score 9 → −1, score 1 → −5).</summary>
    public static int AbilityModifier(int score) => (int)Math.Floor((score - 10) / 2.0);
}
