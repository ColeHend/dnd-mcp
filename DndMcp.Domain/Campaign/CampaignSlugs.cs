using System.Text;
using System.Text.RegularExpressions;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// Slugs: the human handle part of <c>kind:slug</c> ("character:iron-guts"), unique per campaign (including soft-deleted
/// entities, so a restored entity gets its handle back and a handle is never silently reused for something else).
///
/// <para>
/// A slug is derived from the name once, at creation, and never changes with a rename: handles are what the model, the
/// user's notes and exported files refer to, and a rename that moved the handle would break every one of them.
/// </para>
/// </summary>
public static partial class CampaignSlugs
{
    /// <summary>Longest slug. Long names are cut at a hyphen so the handle stays readable.</summary>
    public const int MaxLength = 60;

    /// <summary>
    /// "Belmakor Silverwind" → "belmakor-silverwind"; "Nadar's Axe" → "nadars-axe"; "G.O.D.S. Co." → "g-o-d-s-co";
    /// "Björn" → "bjorn". Uses <see cref="CampaignText.Key"/>, so every name that compares equal gets the same slug.
    /// A name with no letters or digits gives <paramref name="fallback"/> (normally the kind).
    /// </summary>
    public static string From(string? name, string fallback)
    {
        var key = CampaignText.Key(name);
        if (key.Length == 0)
        {
            return fallback;
        }

        var slug = key.Replace(' ', '-');
        if (slug.Length > MaxLength)
        {
            var cut = slug.LastIndexOf('-', MaxLength);
            slug = cut > 0 ? slug[..cut] : slug[..MaxLength];
        }

        return IsValid(slug) ? slug : fallback;
    }

    /// <summary>Lower-case letters and digits in hyphen-separated runs, at most <see cref="MaxLength"/> characters.</summary>
    public static bool IsValid(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= MaxLength && SlugPattern().IsMatch(slug);

    /// <summary>
    /// "keras" and 2 → "keras-2": the slug a new entity gets when its name's slug is taken. Shortened first if the suffix
    /// would push it past <see cref="MaxLength"/>.
    /// </summary>
    public static string WithSuffix(string slug, int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 2);
        var suffix = "-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var room = MaxLength - suffix.Length;
        var stem = slug.Length <= room ? slug : slug[..room].TrimEnd('-');
        return stem + suffix;
    }

    /// <summary>A session entity's slug: <c>session-12</c>. Sessions are addressed as <c>session:12</c>.</summary>
    public static string ForSession(int number) =>
        "session-" + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
