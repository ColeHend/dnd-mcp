using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// What one instance of damage of one type does to the target (research A7): halve it for a successful save, then
/// Resistance, then Vulnerability, then Immunity, floored at 0.
///
/// <para>
/// <b>Per type, on whole numbers, in this order</b> (both editions: "resistance and then vulnerability are applied
/// after all other modifiers"; each halving rounds down). A weapon hit plus a radiant smite is two instances, each
/// adjusted on its own, so a hit is split by type before any of this and summed after. Working on the integer PMF, not
/// on a mean, is the point: E[floor(X/2)] is not E[X]/2 − 0.25 (the TypeScript port target's bug 4) for a flat amount,
/// for a GWF or Elemental Adept die, or after a save's halving (floor(floor(X/2)/2) = floor(X/4)).
/// </para>
/// <para>
/// <b>Typeless damage</b> (an attack or rider without a type) is never resisted, made vulnerable or ignored; it is
/// only halved by a save and floored at 0. The build resolver warns when a target with adjustments meets it.
/// </para>
/// </summary>
public static class DamageAdjustment
{
    /// <summary>The damage this instance deals.</summary>
    /// <param name="damage">The rolled total of one type (may be negative with a negative modifier).</param>
    /// <param name="damageType">The type, or null for typeless.</param>
    /// <param name="halve">A successful save's "half as much damage", applied first.</param>
    public static long Apply(long damage, string? damageType, ResolvedTarget target, bool halve = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        var x = halve ? FloorHalf(damage) : damage;
        if (damageType is not null)
        {
            if (target.IsImmune(damageType))
            {
                return 0;
            }

            if (target.IsResistant(damageType))
            {
                x = FloorHalf(x);
            }

            if (target.IsVulnerable(damageType))
            {
                x *= 2;
            }
        }

        return Math.Max(x, 0);
    }

    /// <summary>Rounds toward negative infinity, as "half, rounded down" means for a negative total too.</summary>
    private static long FloorHalf(long x) => x >= 0 ? x / 2 : -((-x + 1) / 2);
}
