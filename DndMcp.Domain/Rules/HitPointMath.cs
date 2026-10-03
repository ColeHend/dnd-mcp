using DndMcp.Domain.Features;

namespace DndMcp.Domain.Rules;

/// <summary>
/// The one formula for a creature's effective hit point maximum, shared by the character sheet (rests, healing caps), the
/// combat tracker (damage, massive damage, Bloodied) and the simulator seeding. It exists before the rest of the Phase 7
/// rules so that every layer reads the same number: two copies that disagreed on the order of the reduction and the
/// halving would put a sheet and its combatant at different maxima after a rest, and the end-of-combat write-back would
/// then see drift that never happened.
/// </summary>
/// <remarks>
/// <para>
/// A maximum reduction (2014 wraith Life Drain "its hit point maximum is reduced by an amount equal to the damage taken";
/// 2024 the same wording) is subtracted first and floors at 0; SRD 5.2.1 "Hit Point Maximum of 0: a creature dies if its
/// Hit Point maximum reaches 0", which callers read from a result of 0.
/// </para>
/// <para>
/// 2014 Exhaustion level 4 is "Hit point maximum halved" (SRD 5.1 Conditions, cumulative with the levels above it); it is
/// applied after the reduction and rounds down. 2024 Exhaustion has no effect on the maximum (−2 × level on D20 Tests and
/// −5 ft × level of Speed instead).
/// </para>
/// </remarks>
public static class HitPointMath
{
    /// <summary>The 2014 Exhaustion level from which the hit point maximum is halved.</summary>
    public const int Exhaustion2014HalvesMaximumAt = 4;

    /// <summary>
    /// <c>max(0, maxHp − reduction)</c>, halved (rounding down) in 2014 at Exhaustion 4 or more. 0 means the creature dies.
    /// </summary>
    /// <param name="maxHp">The hit point maximum as recorded (sheet <c>max_hp</c>, combatant <c>max_hp</c>).</param>
    /// <param name="reduction">The recorded maximum reduction (<c>max_hp_reduction</c>), 0 or more.</param>
    /// <param name="exhaustion">The Exhaustion level, 0-6.</param>
    /// <param name="edition">"2014" or "2024" (<see cref="DslValues.Editions"/>).</param>
    public static int EffectiveMaxHp(int maxHp, int reduction, int exhaustion, string edition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxHp);
        ArgumentOutOfRangeException.ThrowIfNegative(reduction);
        ArgumentOutOfRangeException.ThrowIfNegative(exhaustion);
        var reduced = Math.Max(0, maxHp - reduction);
        return edition == DslValues.Editions.E2014 && exhaustion >= Exhaustion2014HalvesMaximumAt ? reduced / 2 : reduced;
    }
}
