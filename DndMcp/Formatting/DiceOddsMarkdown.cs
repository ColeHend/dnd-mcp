using System.Globalization;
using System.Text;
using DndMcp.Domain.Dice;

namespace DndMcp.Formatting;

/// <summary>
/// Renders <c>dice_odds</c> results: the answer first (the probability asked for, or the mean), then the shape of
/// the distribution, then how it was computed. Exact answers carry their fraction while it is short enough to read;
/// estimates carry 95% intervals and say why they are estimates.
/// </summary>
internal static class DiceOddsMarkdown
{
    /// <summary>Distributions with at most this many totals get a full table (about 2k characters at most).</summary>
    public const int MaxTableRows = 60;

    /// <summary>Fractions with longer denominators are noise to a reader; the decimal says the same thing.</summary>
    private const int MaxFractionDigits = 15;

    private static readonly (double Fraction, string Name)[] Percentiles =
    [
        (0.05, "5th"), (0.10, "10th"), (0.25, "25th"), (0.50, "median"), (0.75, "75th"), (0.90, "90th"), (0.95, "95th"),
    ];

    public static string Format(DiceOdds odds)
    {
        var output = new StringBuilder();
        var expression = odds.Expression;
        var estimated = odds.Method == DiceOddsMethod.MonteCarlo;

        if (expression.Comparison is { } comparison)
        {
            // "P(…) > 99.9999999999%" states a bound and "≈ 0%" already says it is estimated; "= > 99.99…" would read as a
            // typo. Impossible and certain are exact even for an estimate (they come from the expression's bounds), so
            // they get "=" and no interval.
            var chance = Chance(odds, comparison);
            var settled = !odds.CanMeet(comparison) || odds.MustMeet(comparison);
            var relation = chance[0] is '<' or '>' or '≈' ? string.Empty : estimated && !settled ? "≈ " : "= ";
            output.Append(Invariant($"**P({expression.TotalText} {comparison.Symbol} {comparison.Target}) {relation}{chance}**"))
                  .Append(Exactly(odds.ExactProbability(comparison)))
                  .Append(settled ? string.Empty : Interval(odds, comparison))
                  .AppendLine();

            // The neighbouring questions, so "at least 30" and "more than 30" are both answered.
            var t = comparison.Target;
            output.Append(string.Join(" · ", new[]
            {
                new DiceCondition(DiceComparison.Equal, t),
                new DiceCondition(DiceComparison.GreaterOrEqual, t),
                new DiceCondition(DiceComparison.LessOrEqual, t),
            }.Where(c => c != comparison).Select(c => Invariant($"P({c.Symbol} {t}) {Chance(odds, c)}"))))
                  .AppendLine();
            output.Append(Summary(odds, withHeading: false)).AppendLine();
        }
        else
        {
            output.Append(Invariant($"**{expression.TotalText}**: ")).Append(Summary(odds, withHeading: true)).AppendLine();
        }

        output.Append("Percentiles: ")
              .Append(string.Join(" · ", Percentiles.Select(p => Invariant($"{p.Name} {odds.Percentile(p.Fraction)}"))))
              .AppendLine();

        if (expression.Comparison is null && odds.Values.Count <= MaxTableRows)
        {
            output.AppendLine().Append(Table(odds));
        }

        output.Append(Method(odds));

        if (odds.ExplosionCapEffect > 1e-12)
        {
            output.AppendLine().Append(Invariant(
                $"_Explosions stop after {DiceLimits.MaxExplosionsPerDie} per die, as in dice_roll; that shifts these figures by at most {odds.ExplosionCapEffect:0.#e-0}._"));
        }

        return output.ToString();
    }

    private static string Summary(DiceOdds odds, bool withHeading)
    {
        var mean = Number(odds.Mean);
        var meanText = withHeading ? $"mean {mean}" : $"Mean {mean}";
        if (odds.MeanHalfWidth is { } half)
        {
            meanText += $" ± {Number(half)}";
        }

        meanText += Exactly(odds.ExactMean);
        var range = odds.Method == DiceOddsMethod.MonteCarlo ? "observed range" : "range";
        return Invariant($"{meanText} · SD {Number(odds.StandardDeviation)} · {range} {odds.Min} to {odds.Max}");
    }

    private static string Table(DiceOdds odds)
    {
        var table = new StringBuilder();
        table.AppendLine("| Total | P(=) | P(≥) | P(≤) |").AppendLine("|---:|---:|---:|---:|");

        foreach (var value in odds.Values)
        {
            table.AppendLine(Invariant(
                $"| {value} | {Chance(odds, new DiceCondition(DiceComparison.Equal, value))} | {Chance(odds, new DiceCondition(DiceComparison.GreaterOrEqual, value))} | {Chance(odds, new DiceCondition(DiceComparison.LessOrEqual, value))} |"));
        }

        return table.ToString();
    }

    private static string Method(DiceOdds odds) => odds.Method switch
    {
        DiceOddsMethod.Exact => "_Exact: every outcome counted, no sampling._",
        DiceOddsMethod.FloatingPoint => "_Exact up to floating-point rounding (too many outcomes to show fractions); no sampling._",
        _ => Invariant(
            $"_Estimated from {odds.Samples:N0} simulated rolls (seed {odds.Seed}), because the expression is {Reason(odds.MonteCarloReason)}. ± and interval figures are 95% confidence intervals; everything else, the observed range included, comes from the samples._"),
    };

    private static string Reason(string? reason) => reason switch
    {
        null => "not computable exactly",
        _ when reason.Contains("keeps or drops", StringComparison.Ordinal) => "exploding dice with keep/drop (the pool size is random)",
        _ => reason,
    };

    private static string Exactly(Fraction? fraction) =>
        fraction is { } f && !f.Denominator.IsOne && f.Denominator.ToString(CultureInfo.InvariantCulture).Length <= MaxFractionDigits
            ? $" (exactly {f})"
            : string.Empty;

    private static string Interval(DiceOdds odds, DiceCondition condition) =>
        odds.ProbabilityInterval(condition) is { } interval
            ? $" (95% interval {Bound(interval.Low)} to {Bound(interval.High)})"
            : string.Empty;

    private static string Bound(double p) => p <= 0 ? "0%" : p >= 1 ? "100%" : Percent(p);

    /// <summary>
    /// A probability as a percentage that never overstates certainty: "0%" and "100%" only when no possible total
    /// (or every one) meets the condition, and the bounds when a real probability is too small to print.
    /// </summary>
    private static string Chance(DiceOdds odds, DiceCondition condition)
    {
        if (!odds.CanMeet(condition))
        {
            return "0%";
        }

        if (odds.MustMeet(condition))
        {
            return "100%";
        }

        // An estimate that never (or always) saw the outcome is not evidence it is impossible (or certain). Exact
        // methods go through Percent, which prints a possible total's rounded-away probability as a bound.
        var p = odds.Probability(condition);
        if (odds.Method == DiceOddsMethod.MonteCarlo && p is <= 0 or >= 1)
        {
            return p <= 0 ? "≈ 0%" : "≈ 100%";
        }

        return Percent(p);
    }

    /// <summary>
    /// Enough decimals to show the smaller of p and 1 − p to three significant figures, so 0.9999871 reads as
    /// 99.9987% rather than a misleading "100.00%", and tiny tails do not collapse to 0.00%. Only for probabilities
    /// strictly between 0 and 1 (see <see cref="Chance"/>).
    /// </summary>
    internal static string Percent(double p)
    {
        var nearer = Math.Min(p, 1 - p) * 100;
        if (nearer < 1e-10 || double.IsNaN(nearer))
        {
            return p < 0.5 ? "< 0.0000000001%" : "> 99.9999999999%";
        }

        var decimals = Math.Clamp(2 - (int)Math.Floor(Math.Log10(nearer)), 2, 12);
        return (p * 100).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>Up to four decimals, trailing zeros trimmed: 28, 4.2, 12.2446.</summary>
    internal static string Number(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
