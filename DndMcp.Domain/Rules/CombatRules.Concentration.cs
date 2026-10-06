using DndMcp.Domain.Features;
using DndMcp.Domain.Probability;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;

namespace DndMcp.Domain.Rules;

public static partial class CombatRules
{
    /// <summary>The concentration DC's floor (both editions).</summary>
    public const int ConcentrationMinimumDc = 10;

    /// <summary>The 2024 cap on the concentration DC (SRD 5.2.1 Concentration "up to a maximum DC of 30"); 2014 has none.</summary>
    public const int ConcentrationMaximumDc2024 = 30;

    /// <summary>
    /// The conditions that are or include Incapacitated (SRD 5.1 and 5.2.1 condition texts: Paralyzed, Petrified, Stunned
    /// and Unconscious each say the creature is incapacitated). Gaining any of them ends concentration (SRD 5.1 "You lose
    /// concentration on a spell if you are incapacitated"; SRD 5.2.1 Incapacitated "Your Concentration is broken"), stops
    /// legendary actions and reactions, and (2024) gives Disadvantage on Initiative.
    /// </summary>
    public static readonly IReadOnlyList<string> IncapacitatingConditions = [C.Incapacitated, C.Paralyzed, C.Petrified, C.Stunned, C.Unconscious];

    /// <summary>
    /// The concentration save DC for one damage instance (contract §5.8): <c>max(10, ⌊damage / 2⌋)</c>, capped at 30 in
    /// 2024 only (SRD 5.1 "The DC equals 10 or half the damage you take, whichever number is higher"; SRD 5.2.1 "(round
    /// down) … up to a maximum DC of 30"). 35 → 17, 12 → 10, 70 → 35 (2014) / 30 (2024). The simulator's
    /// <c>Fight.ConcentrationDc</c> computes the same (an agreement test runs both).
    /// </summary>
    public static int ConcentrationDc(int damage, string edition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(damage);
        var dc = Math.Max(ConcentrationMinimumDc, damage / 2);
        return Is2024(edition) ? Math.Min(dc, ConcentrationMaximumDc2024) : dc;
    }

    /// <summary>
    /// Whether <paramref name="condition"/> (a canonical condition name, case ignored) is or includes Incapacitated: the
    /// ONE predicate for concentration breaking (<see cref="BreaksConcentration"/>), 2024 initiative Disadvantage
    /// (<see cref="InitiativeRoll"/>), and the tracker's "no legendary actions, no reactions, grapples it holds end".
    /// </summary>
    public static bool IsIncapacitating(string condition) => IncapacitatingConditions.Contains(condition, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether gaining <paramref name="condition"/> breaks concentration automatically (no save): <see cref="IsIncapacitating"/>.</summary>
    public static bool BreaksConcentration(string condition) => IsIncapacitating(condition);

    /// <summary>
    /// A creature's save bonus for one ability (contract §5.8 for Constitution, the same rule for any ability): from the
    /// sheet when it says the creature is proficient or adds a flat bonus (modifier + proficiency bonus if proficient +
    /// the flat bonus), else the stat block's total (<see cref="Simulation.StatBlock.SaveBonuses"/>), else the ability
    /// modifier (0 with no score).
    /// </summary>
    public static int SaveBonus(SaveBonusSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var modifier = sources.Score is { } score ? DslLimits.AbilityModifier(score) : 0;
        if (sources.Proficient || sources.Bonus is not null)
        {
            return modifier + (sources.Proficient ? sources.ProficiencyBonus ?? 0 : 0) + (sources.Bonus ?? 0);
        }

        return sources.StatBlockBonus ?? modifier;
    }

    /// <summary>
    /// A monster's save bonus for <paramref name="ability"/> from its stat block (<see cref="Simulation.StatBlock.SaveBonuses"/>:
    /// the proficient total, else the modifier). For Constitution this is the concentration save bonus of a creature added
    /// from a stat block (contract §5.8).
    /// </summary>
    public static int SaveBonus(string ability, Simulation.StatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var key = AbilityKey(ability);
        return block.SaveBonuses.TryGetValue(key, out var bonus) ? bonus : block.Modifier(key);
    }

    /// <summary>
    /// A sheet's save bonus for <paramref name="ability"/> (contract §5.8 for Constitution): the ability modifier from
    /// <paramref name="abilities"/> (0 when the sheet has no score), + <paramref name="proficiencyBonus"/> when the ability
    /// is among <paramref name="proficient"/> (sheet <c>saves.proficient</c>), + the flat extra in <paramref name="bonus"/>
    /// (sheet <c>saves.bonus</c>).
    /// </summary>
    /// <param name="abilities">The sheet's <c>abilities</c> (ability key → score), or null.</param>
    /// <param name="proficiencyBonus">The proficiency bonus of the sheet's level (<c>DslLimits.ProficiencyBonus</c>), or null when it has no level.</param>
    public static int SaveBonus(
        string ability, IReadOnlyDictionary<string, int>? abilities, IEnumerable<string>? proficient, IReadOnlyDictionary<string, int>? bonus, int? proficiencyBonus)
    {
        var key = AbilityKey(ability);
        return SaveBonus(new SaveBonusSources(
            Score: abilities is not null && abilities.TryGetValue(key, out var score) ? score : null,
            Proficient: proficient?.Contains(key, StringComparer.OrdinalIgnoreCase) == true,
            ProficiencyBonus: proficiencyBonus,
            Bonus: bonus is not null && bonus.TryGetValue(key, out var extra) ? extra : null));
    }

    /// <summary>
    /// Whether a creature with these condition immunities (a stat block's <c>ConditionImmunities</c>, a sheet's
    /// <c>defenses.condition_immune</c>; canonical names) cannot gain <paramref name="condition"/> (contract §5.1: the add
    /// is refused for that target).
    /// </summary>
    public static bool IsImmuneToCondition(string condition, IEnumerable<string>? immunities) =>
        immunities?.Contains(condition, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>
    /// The server's roll for a concentration save: d20 + the save bonus − 2 × Exhaustion in 2024, with Disadvantage at 2014
    /// Exhaustion 3 or more. Pass the KEPT face to <see cref="ConcentrationSave"/>.
    /// </summary>
    public static D20RollPlan ConcentrationRoll(int saveBonus, int exhaustion, string edition)
    {
        CheckExhaustion(exhaustion);
        var mode = ExhaustionDisadvantageOnAttacksAndSaves(exhaustion, edition) ? D20Mode.Disadvantage : D20Mode.Normal;
        return D20RollPlan.Of(mode, saveBonus - ExhaustionD20Penalty(exhaustion, edition));
    }

    /// <summary>
    /// Resolves one concentration save (contract §5.8): the given <paramref name="total"/>, else the kept
    /// <paramref name="face"/> + the save bonus − 2 × Exhaustion in 2024, against the DC. A natural 20 is not an automatic
    /// success (neither SRD gives saving throws one; the simulator's saves have no natural rule either).
    /// </summary>
    /// <exception cref="ArgumentException">Neither a face nor a total is given.</exception>
    public static SaveOutcome ConcentrationSave(int dc, int? face, int? total, int saveBonus, int exhaustion, string edition)
    {
        CheckExhaustion(exhaustion);
        _ = Is2024(edition);
        if (face is null && total is null)
        {
            throw new ArgumentException("A concentration save needs the d20 face or the total.", nameof(face));
        }

        if (face is { } f)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(f, 1, nameof(face));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(f, 20, nameof(face));
        }

        var value = total ?? face!.Value + saveBonus - ExhaustionD20Penalty(exhaustion, edition);
        return new SaveOutcome(dc, value, value >= dc);
    }

    /// <summary>"1d20+5", "2d20kh1+3", "2d20kl1-2", "1d20": a d20 roll in the dice grammar <c>dice_roll</c> logs.</summary>
    public static string D20Expression(D20Mode mode, int modifier)
    {
        var dice = mode switch
        {
            D20Mode.Advantage => "2d20kh1",
            D20Mode.Disadvantage => "2d20kl1",
            D20Mode.Normal => "1d20",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a d20 mode."),
        };
        return modifier switch
        {
            0 => dice,
            > 0 => $"{dice}+{Num(modifier)}",
            _ => $"{dice}-{Num(-modifier)}",
        };
    }

    private static string AbilityKey(string ability) =>
        DslValues.Abilities.All.FirstOrDefault(a => string.Equals(a, ability, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"\"{ability}\" is not an ability key.", nameof(ability));

    private static void CheckExhaustion(int exhaustion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(exhaustion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(exhaustion, ExhaustionDeathLevel);
    }
}

/// <summary>
/// Where a creature's save bonus comes from (<see cref="CombatRules.SaveBonus"/>): the sheet's score, proficiency and
/// flat bonus (<c>saves {"proficient":["con"],"bonus":{"con":1}}</c>, the proficiency bonus of its level), and the stat
/// block's total for a monster.
/// </summary>
/// <param name="Score">The ability score (sheet <c>abilities</c>, or the stat block's), or null when unknown.</param>
/// <param name="Proficient">The sheet lists the ability as proficient.</param>
/// <param name="ProficiencyBonus">The sheet's proficiency bonus (by its level), or null.</param>
/// <param name="Bonus">The sheet's flat extra for the ability, or null.</param>
/// <param name="StatBlockBonus">A stat block's total save bonus for the ability (<c>SaveBonuses</c>), or null.</param>
public sealed record SaveBonusSources(int? Score = null, bool Proficient = false, int? ProficiencyBonus = null, int? Bonus = null, int? StatBlockBonus = null);

/// <summary>One saving throw against a DC: the total compared and whether it succeeded.</summary>
public sealed record SaveOutcome(int Dc, int Total, bool Success);
