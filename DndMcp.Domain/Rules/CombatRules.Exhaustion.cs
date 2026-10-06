using System.Globalization;
using System.Text;

namespace DndMcp.Domain.Rules;

public static partial class CombatRules
{
    /// <summary>The Exhaustion level at which a creature dies (both editions).</summary>
    public const int ExhaustionDeathLevel = 6;

    /// <summary>
    /// What an Exhaustion level does in an edition (contract §5.9): the penalties and Disadvantages the tracker reminds
    /// of and the roll plans apply, the maximum-HP halving <see cref="HitPointMath"/> already applies, death at 6, and the
    /// reminder line.
    /// </summary>
    /// <remarks>
    /// 2024 (SRD 5.2.1 Exhaustion): "When you make a D20 Test, the roll is reduced by 2 times your Exhaustion level"
    /// (attack rolls, saving throws, ability checks, so initiative and death saves too) and "Your Speed is reduced by a
    /// number of feet equal to 5 times your Exhaustion level"; "You die if your Exhaustion level is 6". 2014 (SRD 5.1
    /// Conditions), cumulative: 1 Disadvantage on ability checks; 2 Speed halved; 3 Disadvantage on attack rolls and saving
    /// throws; 4 hit point maximum halved; 5 Speed reduced to 0; 6 death.
    /// </remarks>
    public static ExhaustionEffects Exhaustion(int level, string edition)
    {
        CheckExhaustion(level);
        var is2024 = Is2024(edition);
        var dead = level >= ExhaustionDeathLevel;
        var effects = new ExhaustionEffects
        {
            Level = level,
            Edition = edition,
            D20Penalty = ExhaustionD20Penalty(level, edition),
            SpeedPenaltyFeet = is2024 ? 5 * level : 0,
            SpeedHalved = !is2024 && level is >= 2 and < 5,
            SpeedZero = !is2024 && level >= 5,
            DisadvantageOnChecks = ExhaustionDisadvantageOnChecks(level, edition),
            DisadvantageOnAttacksAndSaves = ExhaustionDisadvantageOnAttacksAndSaves(level, edition),
            MaxHpHalved = !is2024 && level >= HitPointMath.Exhaustion2014HalvesMaximumAt,
            Dead = dead,
            Text = Text(level, is2024),
        };
        return effects;

        static string Text(int level, bool is2024)
        {
            if (level == 0)
            {
                return string.Empty;
            }

            if (level >= ExhaustionDeathLevel)
            {
                return $"Exhaustion {Num(level)}: dead";
            }

            if (is2024)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Exhaustion {level}: D20 Tests −{2 * level}; Speed −{5 * level} ft");
            }

            var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Exhaustion {level}: Disadvantage on ability checks");
            if (level >= 5)
            {
                text.Append("; Speed 0");
            }
            else if (level >= 2)
            {
                text.Append("; Speed halved");
            }

            if (level >= 3)
            {
                text.Append("; Disadvantage on attack rolls and saving throws");
            }

            if (level >= 4)
            {
                text.Append("; hit point maximum halved");
            }

            return text.ToString();
        }
    }

    /// <summary>2024's −2 × level on every D20 Test (the simulator's <c>ExhaustionPenalty</c>); 0 in 2014.</summary>
    public static int ExhaustionD20Penalty(int level, string edition)
    {
        CheckExhaustion(level);
        return Is2024(edition) ? 2 * level : 0;
    }

    /// <summary>2014 level 1 or more: Disadvantage on ability checks (initiative included; the simulator's escape check).</summary>
    public static bool ExhaustionDisadvantageOnChecks(int level, string edition)
    {
        CheckExhaustion(level);
        return !Is2024(edition) && level >= 1;
    }

    /// <summary>2014 level 3 or more: Disadvantage on attack rolls and saving throws (death and concentration saves included; the simulator's <c>ExhaustionDisadvantage</c>).</summary>
    public static bool ExhaustionDisadvantageOnAttacksAndSaves(int level, string edition)
    {
        CheckExhaustion(level);
        return !Is2024(edition) && level >= 3;
    }
}

/// <summary>What one Exhaustion level does (<see cref="CombatRules.Exhaustion"/>).</summary>
public sealed record ExhaustionEffects
{
    /// <summary>The Exhaustion level, 0-6.</summary>
    public required int Level { get; init; }

    /// <summary>"2014" or "2024": the two editions' Exhaustion are different conditions.</summary>
    public required string Edition { get; init; }

    /// <summary>2024: subtracted from every D20 Test (2 × level). 0 in 2014.</summary>
    public required int D20Penalty { get; init; }

    /// <summary>2024: feet of Speed lost (5 × level). 0 in 2014.</summary>
    public required int SpeedPenaltyFeet { get; init; }

    /// <summary>2014 levels 2-4.</summary>
    public required bool SpeedHalved { get; init; }

    /// <summary>2014 level 5 and up.</summary>
    public required bool SpeedZero { get; init; }

    /// <summary>2014 level 1 and up.</summary>
    public required bool DisadvantageOnChecks { get; init; }

    /// <summary>2014 level 3 and up.</summary>
    public required bool DisadvantageOnAttacksAndSaves { get; init; }

    /// <summary>2014 level 4 and up (<see cref="HitPointMath.EffectiveMaxHp"/> applies it).</summary>
    public required bool MaxHpHalved { get; init; }

    /// <summary>Level 6 (both editions).</summary>
    public required bool Dead { get; init; }

    /// <summary>"Exhaustion 1: D20 Tests −2; Speed −5 ft"; empty at level 0. The tracker's <c>exhaustion_effects</c> line.</summary>
    public required string Text { get; init; }
}
