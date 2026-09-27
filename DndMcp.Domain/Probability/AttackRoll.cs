using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;

namespace DndMcp.Domain.Probability;

/// <summary>
/// The three outcomes of one attack roll. <see cref="Hit"/> INCLUDES crits, because every crit is a hit; the damage
/// model splits it as (Hit − Crit)·E[normal hit] + Crit·E[crit] + Miss·E[miss].
/// </summary>
/// <param name="Hit">P(the attack hits), crits included. Never below <see cref="Crit"/>.</param>
/// <param name="Crit">P(the attack is a critical hit).</param>
public readonly record struct AttackRollOdds(double Hit, double Crit)
{
    /// <summary>P(a hit that is not a crit). Not negative: <see cref="AttackRoll.Odds"/> floors the hit chance at the crit chance.</summary>
    public double NormalHit => Hit - Crit;

    /// <summary>P(the attack misses): what Graze and on-miss riders are weighted by.</summary>
    public double Miss => 1 - Hit;
}

/// <summary>
/// Hit and crit chances for one attack roll (research A1): tail sums of the kept-face distribution
/// (<see cref="D20.FacePmf"/>), so every mode and feature combination is exact.
///
/// <para>
/// A natural face <c>f</c> hits when <c>f ≥ critMin</c> (a crit always hits) or when <c>f ≠ 1</c> and
/// <c>f + bonus ≥ AC</c> (a natural 1 always misses). The first half is the <b>crit floor</b> the TypeScript port target
/// lacks (its bug 1): it clamped the hit chance to at least 5% whatever the crit range, so with Improved Critical against
/// a high AC the hit chance fell below the crit chance and the normal-hit share went negative — +5 vs AC 30 crit 19 with
/// 1d8+3 gave 0.825 damage per attack instead of 1.2. Both halves are "from some face up", so the hitting faces are
/// exactly <c>f ≥ </c><see cref="LowestHittingFace"/>.
/// </para>
/// </summary>
public static class AttackRoll
{
    /// <summary>The widest crit range any build may give: 2–20. A natural 1 always misses, so it can never crit.</summary>
    public const int LowestCritMin = 2;

    /// <summary>
    /// The chance this attack hits and crits.
    /// </summary>
    /// <param name="attackBonus">Everything added to the d20 except bonus dice: ability, proficiency, weapon, Archery, a power attack's −5.</param>
    /// <param name="targetAc">The AC to meet or beat, cover included.</param>
    /// <param name="critMin">The lowest natural face that crits, 2–20: 20 normally, 19 for Improved Critical.</param>
    /// <param name="options">The attacker's d20: mode after cancelling, Lucky, Elven Accuracy.</param>
    /// <param name="bonusDice">
    /// Dice added to the roll (Bless +1d4 → values 1..4; Bane −1d4 → −4..−1; both → their convolution), or null. The hit
    /// chance is Σ_b P(bonus = b)·P(hit at attackBonus + b); the crit chance does not move, because a crit is decided by
    /// the natural face alone.
    /// </param>
    /// <param name="autoCrit">
    /// Every hit is a crit: a Paralyzed or Unconscious target hit by an attacker within 5 feet. The hit chance is
    /// unchanged — the condition's Advantage is the caller's to put in <paramref name="options"/>.
    /// </param>
    /// <exception cref="DndInputException"><paramref name="critMin"/> is outside 2–20.</exception>
    /// <exception cref="ArgumentException"><paramref name="bonusDice"/> is not a probability distribution (a bug in the caller), or the mode is undefined.</exception>
    public static AttackRollOdds Odds(
        int attackBonus,
        int targetAc,
        int critMin,
        D20Options options,
        Pmf<double>? bonusDice = null,
        bool autoCrit = false)
    {
        CheckCritMin(critMin);
        var table = D20.Table(options);
        var crit = table.AtLeast(critMin);

        // In long: an int AC minus an int bonus cannot overflow there, nor can a bonus die up to BonusDice.MaxMagnitude.
        var need = (long)targetAc - attackBonus;
        if (bonusDice is not null)
        {
            BonusDice.CheckDistribution(bonusDice, nameof(bonusDice));
        }

        double hit;
        if (bonusDice is null || bonusDice.Count == 1)
        {
            // A single value is a flat bonus: the same single table read as adding it to attackBonus, bit for bit.
            hit = table.AtLeast(LowestHittingFace(need - (bonusDice?.Min ?? 0), critMin));
        }
        else
        {
            // Summed as the crit chance plus each outcome's share of NON-crit hits, rather than as Σ P(b)·P(hit at b):
            // every term is then ≥ 0 and is exactly 0 when only crit faces hit, so the crit floor holds bit for bit.
            // The plain sum rounds an ulp either side of the crit chance when every outcome is floored (3/16 of 0.1 is
            // not exact), and a NormalHit of −1e-17, or of +1e-17 where the true value is 0, leaks into every later sum.
            var normalHit = 0.0;
            var values = bonusDice.Values;
            var weights = bonusDice.Weights;
            for (var i = 0; i < values.Length; i++)
            {
                normalHit += weights[i] / bonusDice.Total * (table.AtLeast(LowestHittingFace(need - values[i], critMin)) - crit);
            }

            hit = Math.Min(crit + normalHit, 1.0);
        }

        return new AttackRollOdds(hit, autoCrit ? hit : crit);
    }

    /// <summary>
    /// The lowest natural d20 face that hits, 2–20: the face that meets the AC, raised to 2 (a natural 1 misses) and
    /// capped at <paramref name="critMin"/> (a crit hits whatever the AC). "+7 vs AC 15" → 8.
    /// </summary>
    /// <exception cref="DndInputException"><paramref name="critMin"/> is outside 2–20.</exception>
    public static int LowestHittingFace(int attackBonus, int targetAc, int critMin)
    {
        CheckCritMin(critMin);
        return LowestHittingFace((long)targetAc - attackBonus, critMin);
    }

    private static int LowestHittingFace(long need, int critMin) => (int)Math.Min(critMin, Math.Max(2, need));

    private static void CheckCritMin(int critMin)
    {
        if (critMin is < LowestCritMin or > D20.Faces)
        {
            throw new DndInputException(string.Create(CultureInfo.InvariantCulture,
                $"A critical hit range starts at a natural {LowestCritMin} to {D20.Faces}; got {critMin}. Use 20 when only a natural 20 crits, 19 for Improved Critical, 18 for Superior Critical."));
        }
    }
}
