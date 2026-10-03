using System.Globalization;
using System.Text;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// The comparison key for names typed by people: "The Old King", "the old king" and "the  Old King" are one name, and so
/// are "Björn" and "Bjorn", or "Nadar's" typed with a straight or a curly apostrophe.
///
/// <para>
/// Every place that decides whether two names are the same goes through <see cref="Key"/>: upsert-by-name (so a second
/// upsert of "Iron Guts" updates the entity instead of creating "iron-guts-2"), handle fallbacks, the knowledge check's
/// name scanner and known_as comparisons. Two different normalizations in two places would let a name match in one and
/// not the other, which in the knowledge check means a leaked name is not flagged.
/// </para>
/// </summary>
public static class CampaignText
{
    /// <summary>
    /// Lower-case, diacritics removed (NFKD then combining marks dropped), apostrophes removed, every other run of
    /// non-letters and non-digits turned into one space, trimmed. Null or blank gives "".
    /// </summary>
    public static string Key(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            {
                continue;
            }

            if (IsApostrophe(c))
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// <see cref="Key"/> without a leading "the", "a" or "an": "The Old King" and "old king" share this key. Names are
    /// quoted without their article as often as with it ("Old king, come down"), so the scanner and name lookups try both.
    /// </summary>
    public static string KeyWithoutArticle(string? text)
    {
        var key = Key(text);
        foreach (var article in (ReadOnlySpan<string>)["the ", "a ", "an "])
        {
            if (key.StartsWith(article, StringComparison.Ordinal) && key.Length > article.Length)
            {
                return key[article.Length..];
            }
        }

        return key;
    }

    private static bool IsApostrophe(char c) => c is '\'' or '’' or '‘' or 'ʼ' or '`' or '´';
}
