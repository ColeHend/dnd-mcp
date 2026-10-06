using System.Globalization;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Encounters;

/// <summary>
/// A monster's Challenge Rating: 0, 1/8, 1/4, 1/2 or a whole number 1–30 — the 34 rows of the CR tables and nothing in
/// between.
///
/// <para>
/// Stored as eighths so the three fractions are exact: as a <c>double</c>, "is this CR 1/8?" becomes a float comparison,
/// and a CR read from JSON as 0.125 must land on the same table row as one typed "1/8". A value between rows (CR 3.5,
/// CR 1/3) is refused rather than rounded, because every table lookup that takes a CR would otherwise pick a row the
/// rules never define, and the XP it reports would look authoritative.
/// </para>
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ChallengeRatingJsonConverter))]
public readonly record struct ChallengeRating : IComparable<ChallengeRating>
{
    public const int MaxWhole = 30;

    private ChallengeRating(int eighths)
    {
        Eighths = eighths;
    }

    /// <summary>Every CR the tables define, lowest first: 0, 1/8, 1/4, 1/2, 1 … 30.</summary>
    public static IReadOnlyList<ChallengeRating> All { get; } =
        [new(0), new(1), new(2), new(4), .. Enumerable.Range(1, MaxWhole).Select(n => new ChallengeRating(n * 8))];

    public static ChallengeRating Zero => All[0];

    /// <summary>The CR in eighths: 1 for CR 1/8, 8 for CR 1, 240 for CR 30.</summary>
    public int Eighths { get; }

    public double Value => Eighths / 8.0;

    /// <summary>The whole-number CR (1–30), or null for 0 and the fractions.</summary>
    public int? Whole => Eighths >= 8 ? Eighths / 8 : null;

    /// <summary>This CR's position in <see cref="All"/>: 0 for CR 0, 4 for CR 1, 33 for CR 30.</summary>
    public int Row => Eighths switch
    {
        0 => 0,
        1 => 1,
        2 => 2,
        4 => 3,
        _ => 3 + Eighths / 8,
    };

    /// <summary>
    /// The next CR up the table (CR 1/2 → 1, 10 → 11), or null at CR 30. The 2024 stat blocks' "XP 5,900, or 7,200 in
    /// lair" is the next row's XP, which is why a caller needs it.
    /// </summary>
    public ChallengeRating? Next => Row + 1 < All.Count ? All[Row + 1] : null;

    /// <summary>
    /// The CR a number stands for (0.125 → 1/8, 5 → 5), or null when the number is not one of the 34 table values. The
    /// SRD data stores CR as a JSON number, and a model sends one; both must agree with <see cref="Parse"/>.
    /// </summary>
    public static ChallengeRating? FromNumber(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > MaxWhole)
        {
            return null;
        }

        var eighths = value * 8;
        if (eighths != Math.Floor(eighths))
        {
            return null;
        }

        var candidate = (int)eighths;
        return All.Any(cr => cr.Eighths == candidate) ? new ChallengeRating(candidate) : null;
    }

    /// <summary>
    /// A CR as a person writes it: "5", "1/2", "0.5", ".25", "½", "CR 1/8". Anything else is refused with the values that
    /// are accepted.
    /// </summary>
    /// <exception cref="DndInputException">The text is not one of the 34 CRs.</exception>
    public static ChallengeRating Parse(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.StartsWith("CR", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..].Trim();
        }

        var normalized = trimmed switch
        {
            "⅛" => "1/8",
            "¼" => "1/4",
            "½" => "1/2",
            _ => trimmed,
        };

        var slash = normalized.IndexOf('/');
        if (slash > 0 &&
            int.TryParse(normalized[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) &&
            int.TryParse(normalized[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) &&
            denominator is 2 or 4 or 8 && numerator == 1)
        {
            return new ChallengeRating(8 / denominator);
        }

        if (slash < 0 &&
            double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) &&
            FromNumber(number) is { } fromNumber)
        {
            return fromNumber;
        }

        throw new DndInputException(
            $"\"{Echo(text ?? string.Empty)}\" is not a Challenge Rating. A CR is 0, 1/8, 1/4, 1/2 (or 0.125, 0.25, 0.5) or a " +
            $"whole number from 1 to {MaxWhole}.");
    }

    public int CompareTo(ChallengeRating other) => Eighths.CompareTo(other.Eighths);

    public static bool operator <(ChallengeRating left, ChallengeRating right) => left.Eighths < right.Eighths;

    public static bool operator >(ChallengeRating left, ChallengeRating right) => left.Eighths > right.Eighths;

    public static bool operator <=(ChallengeRating left, ChallengeRating right) => left.Eighths <= right.Eighths;

    public static bool operator >=(ChallengeRating left, ChallengeRating right) => left.Eighths >= right.Eighths;

    /// <summary>"1/8", "1/2", "5": how the SRD prints a CR.</summary>
    public override string ToString() => Eighths switch
    {
        1 => "1/8",
        2 => "1/4",
        4 => "1/2",
        _ => (Eighths / 8).ToString(CultureInfo.InvariantCulture),
    };

    private static string Echo(string text) => text.Length <= 40 ? text : text[..40] + "…";
}
