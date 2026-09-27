using System.Globalization;
using System.Text;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// The address of one SRD document: <c>{edition}/{kind}/{slug}</c>, e.g. <c>2024/spell/fireball</c>.
///
/// <para>
/// The edition is part of the ref because the same slug means different text in each edition. A ref the model copies
/// out of a search result must fetch that result, not whatever the default edition holds under the same slug.
/// </para>
/// <para>
/// This type only formats and converts. Parsing what a user typed (a bare <c>spell/fireball</c>, plurals, synonyms,
/// API URLs) belongs to the query layer, which owns the error messages.
/// </para>
/// </summary>
public readonly record struct SrdRef(string Edition, string Kind, string Slug)
{
    public override string ToString() => $"{Edition}/{Kind}/{Slug}";

    /// <summary>
    /// The ref an upstream API URL points at, or null when the URL is not one of the forms the vendored data uses.
    ///
    /// <para>
    /// Records link to each other by URL: <c>/api/2014/traits/darkvision</c>. Level records are nested,
    /// <c>/api/2014/classes/fighter/levels/5</c> or <c>/api/2024/subclasses/berserker/levels/3</c>, and their slug is
    /// <c>{segment}-{n}</c> (<c>fighter-5</c>, <c>berserker-3</c>). That holds in both editions even though the 2024
    /// subclass itself is <c>path-of-the-berserker</c>; the URL keeps the short name and so does the level's index.
    /// </para>
    /// <para>
    /// Null for anything else, including 2014 dead links such as <c>/api/2014/rule-sections/…</c>: a formatter shows the
    /// linked name without a ref rather than a ref that resolves to nothing.
    /// </para>
    /// </summary>
    public static SrdRef? FromApiUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || parts[0] != "api" || !SrdEdition.All.Contains(parts[1]))
        {
            return null;
        }

        var edition = parts[1];
        if (parts.Length == 6 && parts[2] is "classes" or "subclasses" && parts[4] == "levels" &&
            int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var level))
        {
            return new SrdRef(edition, SrdKinds.Level, $"{parts[3]}-{level}");
        }

        if (parts.Length != 4 || SrdKinds.FromApiResource(parts[2]) is not { } kind || !SrdKinds.ExistsIn(kind.Name, edition))
        {
            return null;
        }

        return new SrdRef(edition, kind.Name, parts[3]);
    }
}

/// <summary>
/// Slugs for documents upstream gives none: the 2024 Rules Glossary has names and UUIDs only.
/// </summary>
public static class SrdSlug
{
    /// <summary>
    /// Lower-case ASCII letters and digits, every other run of characters becoming one hyphen, trimmed:
    /// "Ammunition (Weapon Property)" → <c>ammunition-weapon-property</c>, "D20 Test" → <c>d20-test</c>. The same rule
    /// upstream's own indexes follow, so glossary slugs look like every other slug. Diacritics are folded first
    /// ("Façade" → <c>facade</c>) rather than dropped.
    /// </summary>
    public static string FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var folded = name.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(folded.Length);
        var pendingHyphen = false;
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var lower = char.ToLowerInvariant(ch);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(lower);
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        return builder.ToString();
    }
}
