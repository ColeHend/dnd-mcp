using System.Globalization;
using System.Text;
using DndMcp.Repository.Sqlite;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// The comparison key for SRD names: what makes "Dragon's Breath", "dragons breath" and "DRAGON’S BREATH" the same
/// name, so a name lookup finds the record however the model or the player typed it.
///
/// <para>
/// srd.db stores this key for every name and alias, and exact-name lookups and the "exact match first" rule of
/// search compare keys, never raw names. The key is part of the index's contract: changing <see cref="Key"/> changes
/// which rows match, so it must come with a <see cref="SrdIndexSchema.Version"/> bump or old indexes would be reused
/// with keys computed the old way.
/// </para>
/// </summary>
public static class SrdNames
{
    /// <summary>
    /// Lower-case, diacritics folded ("Façade" → "facade"), compatibility forms folded ("ﬂaming" → "flaming",
    /// fullwidth "Ｆｉｒｅｂａｌｌ" → "fireball"), invisible format characters dropped (a soft hyphen or zero-width space
    /// inside "Fire­ball" joins, never splits), apostrophes removed ("Dragon's" → "dragons", typographic ’ included,
    /// because 2024 spells it "Will-o’-Wisp" and 2014 "Will-o'-Wisp"), and every other run of characters that is not a
    /// letter or digit becoming one space, trimmed: "Finesse (Weapon Property)" → "finesse weapon property".
    /// Apostrophes are removed rather than spaced because people drop them ("dragons breath"), while they never drop the
    /// space a hyphen or parenthesis stands for. Ligatures, fullwidth letters and soft hyphens are what text copied from
    /// a PDF or a web page carries, and without the folding such a name missed its entry.
    /// </summary>
    public static string Key(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var folded = Fts5Query.Normalize(name, NormalizationForm.FormKD);
        var builder = new StringBuilder(folded.Length);
        var pendingSpace = false;
        foreach (var ch in folded)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format || IsApostrophe(ch))
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Edits (insert, delete, substitute, or swap two adjacent characters) between two keys, or any value above
    /// <paramref name="max"/> once it is certain to exceed it. The swap counts as one edit because it is the commonest
    /// typo ("Fierball"); plain Levenshtein would call that two and push real neighbours past the cut-off. Used for
    /// "did you mean" suggestions after a failed name lookup, never for matching.
    /// </summary>
    public static int EditDistance(string a, string b, int max)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        // Three rolling rows of the optimal-string-alignment table.
        var before = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMinimum = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, before[j - 2] + 1);
                }

                current[j] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }

            if (rowMinimum > max)
            {
                return max + 1;
            }

            (before, previous, current) = (previous, current, before);
        }

        return previous[b.Length];
    }

    // ASCII ', the typographic right and left single quotes, and the modifier-letter apostrophe that some keyboards emit.
    private static bool IsApostrophe(char ch) => ch is '\'' or '’' or '‘' or 'ʼ';
}
