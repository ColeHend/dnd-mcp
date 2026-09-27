using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// Damage dice as exact integer distributions (research A2): one die with its remap, sums of dice, a crit's doubled
/// set and Savage Attacker's better of two rolls. Every damage number the engine reports is a mean of one of these, never
/// a closed-form average, so the same functions also fill the "Great Weapon Fighting Expected Damage" table.
///
/// <para>
/// <b>Remaps act on each die before the sum</b>: Great Weapon Fighting and Elemental Adept change what one die shows,
/// so "2d6 with GWF" is two remapped d6s convolved, not 2d6 remapped as a total (which would turn a total of 2 into 3
/// and leave a die's 1 alone whenever the other die is high). The same is why a formula's dice stay terms
/// (<see cref="DamageFormula.Dice"/>) all the way here.
/// </para>
/// <para>
/// <b>The three remaps</b> (per die with M faces):
/// gwf2014 "reroll a 1 or 2 once and use the new roll" → P(f) = [f ≥ 3]/M + (number of faces 1–2)/M · 1/M;
/// gwf2024 "treat a 1 or 2 as a 3" → the faces 1 and 2 move onto 3;
/// elemental_adept "treat a 1 as a 2" → face 1 moves onto 2.
/// When Great Weapon Fighting and Elemental Adept both apply to one die, GWF is applied first and Elemental Adept to the
/// face that results (a GWF 2014 reroll can still show a 1, which Elemental Adept then reads as 2).
/// </para>
/// </summary>
public static class DamageDice
{
    /// <summary>
    /// One die of <paramref name="sides"/> faces after its remaps, normalised (Total = 1).
    /// </summary>
    /// <param name="sides">1 or more.</param>
    /// <param name="remap">A Great Weapon Fighting remap (<see cref="V.Remaps.Gwf2014"/> or <see cref="V.Remaps.Gwf2024"/>), or null.</param>
    /// <param name="elementalAdept">Whether a 1 counts as a 2 (applied after <paramref name="remap"/>).</param>
    /// <exception cref="ArgumentOutOfRangeException">Fewer than one side, or a remap that is not a GWF remap.</exception>
    public static Pmf<double> Die(int sides, string? remap = null, bool elementalAdept = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sides, 1);

        var weights = new double[sides + 1];
        switch (remap)
        {
            case null:
                for (var face = 1; face <= sides; face++)
                {
                    weights[face] = 1.0 / sides;
                }

                break;
            case V.Remaps.Gwf2014:
                // A 1 or 2 is rerolled once and the new roll stands, whatever it shows.
                var low = Math.Min(2, sides);
                for (var face = 1; face <= sides; face++)
                {
                    weights[face] = (face > 2 ? 1.0 / sides : 0.0) + (double)low / sides / sides;
                }

                break;
            case V.Remaps.Gwf2024:
                weights = new double[Math.Max(sides, 3) + 1];
                for (var face = 1; face <= sides; face++)
                {
                    weights[Math.Max(face, 3)] += 1.0 / sides;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(remap), remap, "Not a Great Weapon Fighting remap.");
        }

        if (elementalAdept && weights.Length > 1)
        {
            var moved = new double[Math.Max(weights.Length, 3)];
            for (var face = 1; face < weights.Length; face++)
            {
                moved[face == 1 ? 2 : face] += weights[face];
            }

            weights = moved;
        }

        // Only faces that can show are part of the support: a GWF 2024 die never shows 1 or 2.
        var pairs = new List<KeyValuePair<long, double>>();
        for (var face = 1; face < weights.Length; face++)
        {
            if (weights[face] > 0)
            {
                pairs.Add(KeyValuePair.Create((long)face, weights[face]));
            }
        }

        return Pmf<double>.FromPairs(pairs, 1.0);
    }

    /// <summary>
    /// The sum of dice terms (a damage formula's dice, or signed bonus dice such as Bane's −1d4), each die remapped,
    /// rolled <paramref name="times"/> times (2 for a crit: "roll all of the attack's damage dice twice"). A formula with
    /// no dice is the point 0.
    /// </summary>
    public static Pmf<double> Sum(IEnumerable<DiceTerm> terms, WorkMeter meter, string? remap = null, bool elementalAdept = false, int times = 1)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfLessThan(times, 1);

        var sum = Pmf<double>.Point(0);
        foreach (var term in terms)
        {
            var die = Die(term.Sides, remap, elementalAdept);
            if (term.Negative)
            {
                die = die.Map(x => -x, meter);
            }

            sum = sum.Convolve(die.Power(term.Count * times, meter), meter);
        }

        return sum;
    }

    /// <summary>
    /// The better of two independent rolls of <paramref name="roll"/> (Savage Attacker): P(max = x) = F(x)² − F(x−1)²,
    /// F the cumulative distribution. Exact per value, so the result keeps the input's support.
    /// </summary>
    public static Pmf<double> MaxOfTwo(Pmf<double> roll)
    {
        ArgumentNullException.ThrowIfNull(roll);

        var values = roll.Values;
        var weights = roll.Weights;
        var pairs = new KeyValuePair<long, double>[values.Length];
        var below = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            var upTo = below + weights[i] / roll.Total;
            pairs[i] = KeyValuePair.Create(values[i], (upTo * upTo) - (below * below));
            below = upTo;
        }

        return Pmf<double>.FromPairs(pairs, 1.0);
    }

    /// <summary>E[X].</summary>
    public static double Mean(Pmf<double> pmf)
    {
        ArgumentNullException.ThrowIfNull(pmf);

        var sum = 0.0;
        var values = pmf.Values;
        var weights = pmf.Weights;
        for (var i = 0; i < values.Length; i++)
        {
            sum += values[i] * weights[i];
        }

        return sum / pmf.Total;
    }

    /// <summary>P(X = <paramref name="value"/>).</summary>
    public static double ProbabilityOf(Pmf<double> pmf, long value)
    {
        ArgumentNullException.ThrowIfNull(pmf);

        var index = pmf.Values.BinarySearch(value);
        return index >= 0 ? pmf.Weights[index] / pmf.Total : 0.0;
    }

    /// <summary>
    /// The expected value of one die of <paramref name="sides"/> with a remap (or plain with null): the numbers of the
    /// "Great Weapon Fighting Expected Damage" table. A d6 is 3.5 plain, 25/6 with gwf2014, 4 with gwf2024.
    /// </summary>
    public static double ExpectedValue(int sides, string? remap = null, bool elementalAdept = false) =>
        Mean(Die(sides, remap, elementalAdept));

    /// <summary>
    /// The expected Savage Attacker roll of <paramref name="count"/>d<paramref name="sides"/> (the better of two rolls of
    /// the whole set), optionally remapped: 1d8 → 5.8125, 2d6 → 8.3719, 2d6 with gwf2024 → 8.9105.
    /// </summary>
    public static double ExpectedBestOfTwo(int count, int sides, string? remap = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return Mean(MaxOfTwo(Sum([new DiceTerm(count, sides)], new WorkMeter(DprLimits.WorkBudget), remap)));
    }
}
