using System.Numerics;

namespace DndMcp.Domain.Dice;

/// <summary>
/// The exact distribution of the kept dice of a pool — 4d6kh3, 2d20kh1, 8d10kl3 — without enumerating the
/// outcomes (6^4 is fine, 20d20 is 10^26).
///
/// <para>
/// Values are processed from best to worst (highest first for kh, lowest first for kl). The state is (dice
/// assigned so far, score so far); every assigned die is kept until <c>keep</c> of them are, so "kept" needs no
/// separate counter. At each value, c of the r unassigned dice show it: while c stays below the dice still needed
/// the state moves on, and once c reaches that need the kept set is complete — extra dice showing the same value
/// and every die still unassigned are the dropped ones. This works for any per-die PMF, not only uniform dice,
/// which is what makes compounding (<c>!!</c>) and rerolled dice keepable.
/// </para>
/// <para>
/// <c>score</c> maps a kept die's value to what it contributes: the value itself for sums, ±1/0 for success counts.
/// </para>
/// </summary>
internal static class KeepDistribution
{
    public static Pmf<T> Compute<T>(Pmf<T> die, int dice, int keep, bool highest, Func<long, long> score, IPmfArithmetic<T> math, WorkMeter meter)
        where T : INumber<T>
    {
        var values = die.Values.ToArray();
        var order = Enumerable.Range(0, values.Length).ToArray();
        if (highest)
        {
            Array.Reverse(order);
        }

        var coefficients = math.KeepCoefficients(die, order, dice);
        var states = new Dictionary<long, T>?[keep];
        states[0] = new Dictionary<long, T> { [0] = T.One };
        var complete = new Dictionary<long, T>();

        for (var step = 0; step < order.Length; step++)
        {
            var stepScore = score(values[order[step]]);
            var isLast = step == order.Length - 1;
            var next = new Dictionary<long, T>?[keep];

            for (var assigned = 0; assigned < keep; assigned++)
            {
                if (states[assigned] is not { } current)
                {
                    continue;
                }

                var remaining = dice - assigned;
                var need = keep - assigned;

                // The transitions, plus the coefficients themselves: AtLeast sums over every c up to remaining.
                meter.Spend(((current.Count * (need + 1L)) + remaining + need) * WorkMeter.DictionaryCost);

                var done = coefficients.AtLeast(step, remaining, need);

                // At the last value nothing lower is left for the unassigned dice, so every path must complete here.
                var moves = isLast ? [] : Enumerable.Range(0, need).Select(c => coefficients.Exactly(step, remaining, c)).ToArray();

                // No "skip if zero" shortcuts: every coefficient is positive in exact arithmetic, so a zero is a double
                // that underflowed, and its value is still reachable (see Pmf's note on support).
                foreach (var (scoreSoFar, weight) in current)
                {
                    Add(complete, scoreSoFar + (need * stepScore), weight * done);

                    for (var c = 0; c < moves.Length; c++)
                    {
                        Add(next[assigned + c] ??= [], scoreSoFar + (c * stepScore), weight * moves[c]);
                    }
                }
            }

            states = next;
        }

        return math.Check(Pmf<T>.FromDictionary(complete, coefficients.ResultTotal));
    }

    private static void Add<T>(Dictionary<long, T> into, long key, T weight)
        where T : INumber<T> =>
        into[key] = into.TryGetValue(key, out var existing) ? existing + weight : weight;
}
