using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// Hit points per level (contract §5.16), the one place the sheet's derivations, level-ups and the party archetypes take
/// them from.
///
/// <para>
/// <b>The rules.</b> Level 1 (the STARTING class): the hit die's maximum + the Con modifier. Each later level, in whichever
/// class: the die rolled + Con, or the fixed value, "the average result of the die roll (rounded up)" = die ÷ 2 + 1 (SRD
/// 5.1 "Beyond 1st Level"; SRD 5.2 "Character Creation": Barbarian 7, Fighter/Paladin/Ranger 6, …, Sorcerer/Wizard 4) +
/// Con. The 2024 rules add "(minimum of 1)" to each level's gain; the 2014 rules state no minimum. Multiclassing gives
/// level 1 hit points only for the first class; every other class's levels count as later levels. A Con modifier change
/// is retroactive (1 per level per point), which the sheet REMINDS of and never applies on its own (contract §5.16).
/// </para>
/// </summary>
public static class LevelHitPoints
{
    /// <summary>The fixed gain of one level after the first: die ÷ 2 + 1 + Con, at least 1 in 2024.</summary>
    public static int FixedGain(int hitDie, int conModifier, string edition)
    {
        var gain = hitDie / 2 + 1 + conModifier;
        return edition == V.Editions.E2024 ? Math.Max(1, gain) : gain;
    }

    /// <summary>
    /// The maximum by the fixed values: the first class's hit die maximum + Con at level 1, then the fixed gain for every
    /// other level of every class. Never below 1 (the sheet's <c>max_hp</c> is at least 1).
    /// </summary>
    /// <param name="classes">(hit die, levels) per class, the STARTING class first.</param>
    /// <param name="minimumOnePerLevel">The 2024 "(minimum of 1)" on each later level's gain.</param>
    public static int FixedTotal(IReadOnlyList<(int HitDie, int Levels)> classes, int conModifier, bool minimumOnePerLevel)
    {
        if (classes.Count == 0 || classes.Any(c => c.Levels < 1 || c.HitDie < 1))
        {
            throw new ArgumentException("Give at least one class, each with a hit die and at least one level.", nameof(classes));
        }

        var total = classes[0].HitDie + conModifier;
        for (var i = 0; i < classes.Count; i++)
        {
            var later = i == 0 ? classes[i].Levels - 1 : classes[i].Levels;
            var gain = classes[i].HitDie / 2 + 1 + conModifier;
            total += later * (minimumOnePerLevel ? Math.Max(1, gain) : gain);
        }

        return Math.Max(SheetLimits.MinMaxHp, total);
    }

    /// <summary><see cref="FixedTotal"/> with the edition's per-level minimum.</summary>
    public static int FixedTotal(IReadOnlyList<(int HitDie, int Levels)> classes, int conModifier, string edition) =>
        FixedTotal(classes, conModifier, minimumOnePerLevel: edition == V.Editions.E2024);
}
