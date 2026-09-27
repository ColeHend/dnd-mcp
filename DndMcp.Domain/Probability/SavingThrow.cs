using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;

namespace DndMcp.Domain.Probability;

/// <summary>
/// The chance a creature fails a saving throw, and how many casts a save-or-suck effect needs to get through Legendary
/// Resistance (research A6).
///
/// <para>
/// <b>No natural 1 or 20 rule.</b> In both editions a natural 20 or 1 is special on attack rolls (and death saves,
/// which are not modelled here), not on a save against a DC: it succeeds exactly when d20 + bonus ≥ DC. So +9 against
/// DC 10 never fails, and −20 against DC 15 never succeeds. Clamping the fail chance to 5–95% like an attack roll
/// would understate every save-based build against weak-save targets (capped at 95%) and overstate it against strong
/// ones (never below 5%).
/// </para>
/// <para>
/// Computed over the kept d20 face (<see cref="D20.FacePmf"/>), not as F² / 1 − (1 − F)² on a finished fail chance: with
/// bonus dice those shortcuts square the average instead of averaging the squares. Bane (−1d4) on a DC 15 save at +2
/// with Magic Resistance fails 0.52875 of the time, not 0.725² = 0.525625.
/// </para>
/// </summary>
public static class SavingThrow
{
    /// <summary>
    /// P(the creature FAILS): d20 + <paramref name="saveBonus"/> (+ bonus dice) &lt; <paramref name="dc"/>.
    /// </summary>
    /// <param name="dc">The save DC.</param>
    /// <param name="saveBonus">The creature's bonus to this save, cover included.</param>
    /// <param name="mode">
    /// The creature's d20: Advantage for Magic Resistance against a magical effect, or a Dodging creature's Dex save;
    /// Disadvantage for a Restrained creature's Dex save. Both at once cancel (<see cref="D20.Resolve"/>).
    /// </param>
    /// <param name="bonusDice">Dice added to the save (Bane −1d4 → values −4..−1), or null. Combined exactly, per face.</param>
    /// <param name="autoFail">
    /// The save fails without a roll: a Str or Dex save while Paralyzed, Stunned or Unconscious. The other arguments are
    /// still checked, so a bad one is reported even when this short-circuits.
    /// </param>
    /// <exception cref="ArgumentException">The mode is undefined, or <paramref name="bonusDice"/> is not a probability distribution (bugs in the caller).</exception>
    public static double FailChance(int dc, int saveBonus, D20Mode mode = D20Mode.Normal, Pmf<double>? bonusDice = null, bool autoFail = false)
    {
        var table = D20.Table(mode);
        if (bonusDice is not null)
        {
            BonusDice.CheckDistribution(bonusDice, nameof(bonusDice));
        }

        if (autoFail)
        {
            return 1.0;
        }

        // Fails iff the kept face < dc − saveBonus − b. In long, like the attack thresholds.
        var need = (long)dc - saveBonus;
        if (bonusDice is null || bonusDice.Count == 1)
        {
            // A single value is a flat bonus: one table read, the same as adding it to saveBonus.
            return table.Below(need - (bonusDice?.Min ?? 0));
        }

        var sum = 0.0;
        var values = bonusDice.Values;
        var weights = bonusDice.Weights;
        for (var i = 0; i < values.Length; i++)
        {
            sum += weights[i] / bonusDice.Total * table.Below(need - values[i]);
        }

        // Rounding in the sum can pass 1 by an ulp when every outcome fails; a probability must not.
        return Math.Clamp(sum, 0.0, 1.0);
    }

    /// <summary>
    /// The expected number of casts until an effect lands on a creature with <paramref name="legendaryResistances"/>
    /// uses of Legendary Resistance: (L + 1) / F, the mean of a negative binomial. Each failed save is turned into a
    /// success until the uses run out, so the effect needs L + 1 failures, each cast failing independently with
    /// probability F. Assumes the creature spends a use on every failure, which is how Legendary Resistance is played
    /// against anything worth stopping. +∞ when F = 0: the effect never lands, and the caller says so rather than
    /// printing a number.
    /// </summary>
    /// <param name="failChance">P(one save fails), from <see cref="FailChance"/>: 0 to 1.</param>
    /// <param name="legendaryResistances">Uses left, 0 or more.</param>
    /// <exception cref="DndInputException"><paramref name="failChance"/> is not a finite probability, or <paramref name="legendaryResistances"/> is negative.</exception>
    public static double ExpectedCastsToLand(double failChance, int legendaryResistances)
    {
        if (!double.IsFinite(failChance) || failChance is < 0 or > 1)
        {
            throw new DndInputException(string.Create(CultureInfo.InvariantCulture,
                $"The chance a save fails must be a probability from 0 to 1; got {failChance}. E.g. 0.6 for a DC 15 save against a +2 bonus."));
        }

        if (legendaryResistances < 0)
        {
            throw new DndInputException(string.Create(CultureInfo.InvariantCulture,
                $"Legendary Resistance uses must be 0 or more; got {legendaryResistances}. E.g. 3 for most adult dragons, 0 for a creature without it."));
        }

        return failChance == 0 ? double.PositiveInfinity : (legendaryResistances + 1.0) / failChance;
    }
}
