using System.Globalization;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// The balance bands of the homebrew-balance scale (research A10, contract §4.6), read from a level-equivalent: how many
/// character levels of damage a feature is worth. Strings, not an enum: results and stored comparisons carry them.
/// </summary>
public static class BalanceBands
{
    /// <summary>LE ≤ −0.25: the feature costs damage.</summary>
    public const string Under = "under";

    /// <summary>|LE| &lt; 0.25.</summary>
    public const string OnBudget = "on_budget";

    /// <summary>0.25 ≤ LE &lt; 0.5.</summary>
    public const string Creeping = "creeping";

    /// <summary>0.5 ≤ LE &lt; 1.0.</summary>
    public const string Over = "over";

    /// <summary>LE ≥ 1.0: worth a whole level or more.</summary>
    public const string Breaking = "breaking";

    public static readonly IReadOnlyList<string> All = [Under, OnBudget, Creeping, Over, Breaking];

    /// <summary>
    /// The band of a level-equivalent AS REPORTED (two significant figures): the number a reader sees and its band never
    /// disagree at a boundary, which reading the unrounded value would allow (0.2496 prints as 0.25, which the scale calls
    /// Creeping).
    /// </summary>
    public static string Of(double reportedLevelEquivalent) => reportedLevelEquivalent switch
    {
        <= -0.25 => Under,
        < 0.25 => OnBudget,
        < 0.5 => Creeping,
        < 1.0 => Over,
        _ => Breaking,
    };

    /// <summary>"On budget".</summary>
    public static string Display(string band) => band switch
    {
        Under => "Under",
        OnBudget => "On budget",
        Creeping => "Creeping",
        Over => "Over",
        Breaking => "Breaking",
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, "Not a balance band."),
    };
}

/// <summary>Where a tier's slope came from.</summary>
public static class SlopeSources
{
    /// <summary>The baseline build's own curve at the tier's edges.</summary>
    public const string Baseline = "baseline";

    /// <summary>RPGBOT's target curve (<see cref="ReferenceCurves.RpgbotTarget"/>), when the baseline's cannot serve.</summary>
    public const string RpgbotReference = "rpgbot_reference";
}

/// <summary>
/// A tier's DPR gained per character level: (D(<see cref="ToLevel"/>) − D(<see cref="FromLevel"/>)) ÷ the levels between.
/// Tier 1 (levels 1–4) is measured from level 1 to 4 (three steps); tiers 2–4 from the level before the tier to its last
/// (levels 4→10, 10→16, 16→20), so each step into the tier counts.
/// </summary>
/// <param name="Source">A <see cref="SlopeSources"/> value.</param>
/// <param name="FromDamage">D at <see cref="FromLevel"/>: the baseline's DPR, or the reference curve's.</param>
/// <param name="FallbackReason">Why the reference was used instead of the baseline, or null.</param>
public sealed record TierSlope(
    int Tier,
    int FirstLevel,
    int LastLevel,
    string Source,
    int FromLevel,
    double FromDamage,
    int ToLevel,
    double ToDamage,
    string? FallbackReason)
{
    public double PerLevel => (ToDamage - FromDamage) / (ToLevel - FromLevel);
}

/// <summary>
/// A feature's worth in character levels: Δ DPR ÷ the tier's slope (research A10), with the band it falls in.
/// </summary>
/// <param name="Value">Δ ÷ slope, unrounded.</param>
/// <param name="Reported">Rounded to two significant figures: what results print, and what <see cref="Band"/> is read from.</param>
/// <param name="Text">"1.3", "0.25", "-0.031": <see cref="Reported"/> as printed, trailing zeros kept.</param>
/// <param name="Band">A <see cref="BalanceBands"/> value.</param>
public sealed record LevelEquivalent(double Value, double Reported, string Text, string Band, TierSlope Slope);

/// <summary>
/// Level-equivalents (contract §4.6): LE = Δ ÷ s_T, s_T the tier-averaged slope of the BASELINE's own DPR curve at the
/// same horizon against the same target spec, or RPGBOT's reference slope when that curve cannot serve.
///
/// <para>
/// <b>Why the tier slope and not the next level's step.</b> DPR rises in jumps (Extra Attack at 5, an ASI at 4 and 8), so
/// the step from L to L + 1 is often zero or a whole attack; averaged over the tier, one level is worth what the tier
/// actually delivers per level. <b>Why the fallback.</b> A baseline that does not change with level, or whose curve is
/// flat or falling against the rising CR = level AC, has no meaningful slope, and dividing by one near zero turns a small
/// Δ into a huge LE; RPGBOT's target (1.25 DPR a level in tiers 1–3, 1.875 in tier 4) is the conventional yardstick then,
/// and a note says so.
/// </para>
/// <para>
/// <b>Which target the slope uses.</b> The target spec as given, resolved at each slope level, like the compared levels
/// themselves: a fixed ac, cr or monster is the same creature at both ends; the default target is the profile's CR = level
/// row at each end — the DMG 2014 row, or under <c>profile</c> mm2014 / mm2024 the SRD medians for CR = that level, so
/// the slope is how the baseline keeps up with that edition's typical monsters. The empirical rows are a census, not a
/// design table, and a slope reads only its two end rows, so their unevenness shows: balance_compare's example baseline
/// (a scaling greatsword fighter) has a tier 2 slope (level 4 → 10) of 1.47 DPR a level against the DMG rows, 1.36 against
/// mm2024 and 1.08 against mm2014, whose CR 4 median AC is 12 against CR 10's 18; its tier 1 slope against mm2024 is flat
/// (CR 1 → 4 medians AC 13 → 15) and falls back to RPGBOT's. That is the profile's answer, not an error, and the slope
/// lines say which targets they used; a slope below <see cref="DprLimits.MinTierSlope"/> still falls back to RPGBOT's,
/// which never depends on the profile.
/// </para>
/// </summary>
public static class LevelEquivalents
{
    /// <summary>The tier of play of a level: 1 (1–4), 2 (5–10), 3 (11–16), 4 (17–20).</summary>
    public static int Tier(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, DslLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, DslLimits.MaxLevel);
        return level switch
        {
            <= 4 => 1,
            <= 10 => 2,
            <= 16 => 3,
            _ => 4,
        };
    }

    /// <summary>The tier's first and last levels.</summary>
    public static (int First, int Last) TierLevels(int tier) => tier switch
    {
        1 => (1, 4),
        2 => (5, 10),
        3 => (11, 16),
        4 => (17, 20),
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Tiers are 1 to 4."),
    };

    /// <summary>The levels the tier's slope is measured between: 1 and 4 for tier 1, else the level before it and its last.</summary>
    public static (int From, int To) SlopeLevels(int tier)
    {
        var (first, last) = TierLevels(tier);
        return tier == 1 ? (first, last) : (first - 1, last);
    }

    /// <summary>RPGBOT's slope for a tier, with the reason it replaces the baseline's.</summary>
    public static TierSlope ReferenceSlope(int tier, string reason)
    {
        var (first, last) = TierLevels(tier);
        var (from, to) = SlopeLevels(tier);
        return new TierSlope(tier, first, last, SlopeSources.RpgbotReference, from, ReferenceCurves.RpgbotTarget(from), to, ReferenceCurves.RpgbotTarget(to), reason);
    }

    /// <summary>The baseline's own slope for a tier from its DPR at the slope levels.</summary>
    public static TierSlope BaselineSlope(int tier, double fromDamage, double toDamage)
    {
        var (first, last) = TierLevels(tier);
        var (from, to) = SlopeLevels(tier);
        return new TierSlope(tier, first, last, SlopeSources.Baseline, from, fromDamage, to, toDamage, null);
    }

    /// <summary>Δ ÷ the slope, reported to two significant figures, with its band.</summary>
    public static LevelEquivalent Of(double delta, TierSlope slope)
    {
        ArgumentNullException.ThrowIfNull(slope);
        if (!double.IsFinite(delta))
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "A DPR difference is finite.");
        }

        if (!(slope.PerLevel > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(slope), slope.PerLevel, "A level-equivalent divides by a positive slope.");
        }

        var value = delta / slope.PerLevel;
        var (reported, text) = Significant(value, 2);
        return new LevelEquivalent(value, reported, text, BalanceBands.Of(reported), slope);
    }

    /// <summary>
    /// <paramref name="value"/> rounded to <paramref name="digits"/> significant figures (halves away from zero, on the
    /// decimal digits a reader sees rather than the double's binary expansion: 0.245 → 0.25), and its text with the
    /// trailing zeros that make the precision visible (0.30, not 0.3).
    /// </summary>
    public static (double Rounded, string Text) Significant(double value, int digits = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 1);
        if (!double.IsFinite(value) || Math.Abs(value) >= 1e20)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Only a finite number below 1e20 in size is rounded here.");
        }

        var exact = (decimal)value;
        if (exact == 0)
        {
            return (0, "0");
        }

        var decimals = digits - 1 - Magnitude(exact);
        decimal rounded;
        if (decimals >= 0)
        {
            rounded = Math.Round(exact, Math.Min(decimals, 28), MidpointRounding.AwayFromZero);
        }
        else
        {
            var scale = Pow10(-decimals);
            rounded = Math.Round(exact / scale, 0, MidpointRounding.AwayFromZero) * scale;
        }

        // Rounding can carry into a new digit (0.996 → 1.0): the text's decimals follow the rounded value's magnitude.
        var shown = Math.Max(0, digits - 1 - Magnitude(rounded));
        return ((double)rounded, rounded.ToString("F" + shown.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    }

    /// <summary>The power of ten of the leading digit (0.03 → −2, 12 → 1), counted on the decimal so no logarithm rounds it.</summary>
    private static int Magnitude(decimal value)
    {
        var abs = Math.Abs(value);
        var magnitude = 0;
        while (abs >= 10)
        {
            abs /= 10;
            magnitude++;
        }

        while (abs < 1)
        {
            abs *= 10;
            magnitude--;
        }

        return magnitude;
    }

    private static decimal Pow10(int power)
    {
        var result = 1m;
        for (var i = 0; i < power; i++)
        {
            result *= 10;
        }

        return result;
    }
}
