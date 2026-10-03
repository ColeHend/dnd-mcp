using System.Globalization;
using System.Text;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// One word of a text as the campaign scanners compare it: its folded key and where it sits in the original text.
/// </summary>
/// <param name="Key">Lower-case letters and digits, diacritics and apostrophes removed ("Björn's" → "bjorns").</param>
/// <param name="Start">Index of the word's first character in the original text.</param>
/// <param name="End">Index just past the word's last character in the original text.</param>
/// <param name="Capitalised">The word's first letter is upper case in the original text.</param>
public readonly record struct TextWord(string Key, int Start, int End, bool Capitalised);

/// <summary>
/// Splits text into words the way <see cref="CampaignText.Key"/> folds names, keeping each word's span in the original
/// text so a hit can be shown exactly as it was written.
///
/// <para>
/// <b>Why one splitter for every scanner:</b> the forbidden-vocabulary scan, the name scanner and the proper-noun
/// heuristic each decide "is this the same word". If they folded differently ("Nadar's" as one word in one and two in
/// another), a name could be flagged by one check and missed by the next, and a missed name in the knowledge check is a
/// leaked name. Folding: NFKD then combining marks and format characters dropped (Björn = Bjorn), apostrophes dropped
/// without splitting ("king's" → "kings", so possessives are the plural-s rule), every other non-letter-or-digit splits,
/// except a comma between digits ("1,000" is one number).
/// </para>
/// <para>
/// <b>Public for the read path</b> (review U02): the knowledge check's partial-name flag looks at the capitalised words of
/// a text (<see cref="Split"/>, with their spans and capitals) that are a distinctive word of a name the speaker does not
/// use (<see cref="DistinctiveWords"/>), and must fold those words exactly as the name scanner does.
/// </para>
/// </summary>
public static class CampaignWords
{
    /// <summary>The apostrophes folding drops: straight, curly (both), the modifier letter, the grave and the acute accent.</summary>
    internal static readonly char[] Apostrophes = ['\'', '’', '‘', 'ʼ', '`', '´'];

    // Common English words that name nothing on their own, beyond the proper-noun stoplist (function words and sentence
    // openers): generic nouns, frequent verbs and adjectives, ordinals and number words. "The thing the old king wants"
    // is given away by "king", not by "thing" or "wants".
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "thing", "things", "stuff", "people", "person", "time", "times", "year", "years", "part", "parts", "kind", "sort",
        "place", "ways", "away", "want", "wants", "wanted", "sent", "send", "sends", "said", "says", "knew", "known", "knows",
        "went", "gone", "goes", "came", "comes", "made", "makes", "took", "taken", "takes", "gave", "given", "gives", "told",
        "tells", "kept", "keeps", "found", "find", "finds", "think", "thinks", "thought", "seem", "seems", "feel", "feels",
        "felt", "left", "call", "calls", "called", "need", "needs", "likes", "good", "great", "little", "long", "high",
        "small", "large", "right", "real", "sure", "true", "full", "whole", "same", "different", "early", "late", "young",
        "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth", "three", "four", "five",
        "seven", "eight", "nine", "twelve", "twenty", "hundred", "thousand", "going",
    };

    /// <summary>The words of <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<TextWord> Split(string? text)
    {
        var words = new List<TextWord>();
        if (string.IsNullOrEmpty(text))
        {
            return words;
        }

        var key = new StringBuilder();
        var start = -1;
        var capitalised = false;
        var lastLetterEnd = -1;

        void Flush()
        {
            if (key.Length > 0)
            {
                words.Add(new TextWord(key.ToString(), start, lastLetterEnd, capitalised));
            }

            key.Clear();
            start = -1;
            capitalised = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsApostrophe(c))
            {
                // Dropped without splitting, as CampaignText.Key does: "king's" is "kings", "rock'n'roll" one word.
                continue;
            }

            if (c < 128)
            {
                if (char.IsAsciiLetterOrDigit(c))
                {
                    Append(c, i);
                }
                else if (c == ',' && key.Length > 0 && IsAllDigits(key) && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    // "1,000": the comma is a digit group separator, not a word break.
                }
                else
                {
                    Flush();
                }

                continue;
            }

            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.Format)
            {
                continue;
            }

            if (char.IsSurrogate(c))
            {
                Flush();
                continue;
            }

            var decomposed = c.ToString().Normalize(NormalizationForm.FormKD);
            var any = false;
            foreach (var d in decomposed)
            {
                var dc = CharUnicodeInfo.GetUnicodeCategory(d);
                if (dc is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.Format)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(d))
                {
                    Append(d, i);
                    any = true;
                }
            }

            if (!any)
            {
                Flush();
            }
        }

        Flush();
        return words;

        void Append(char c, int index)
        {
            if (key.Length == 0)
            {
                start = index;
                capitalised = char.IsUpper(c);
            }

            key.Append(char.ToLowerInvariant(c));
            lastLetterEnd = index + 1;
        }
    }

    /// <summary>The keys of <paramref name="text"/>'s words.</summary>
    public static IReadOnlyList<string> Keys(string? text) => Split(text).Select(w => w.Key).ToList();

    /// <summary>
    /// The words of a name that give it away on their own, as keys in name order, each once: at least four letters, not
    /// an article, not a common word of the proper-noun stoplist (or a contraction of one), not another common English
    /// word that names nothing alone ("thing", "wants", "third"); a possessive's s is dropped ("Nadar's Axe" gives
    /// "nadar"). "Axiom Cage" gives "axiom" and "cage", so "haul your Cage back" can be flagged as a word of a secret
    /// name the speaker has never heard; "The Old King" gives "king". A one-word name gives itself.
    /// </summary>
    /// <remarks>
    /// Compare a word of the text by its key (<see cref="Split"/>), allowing a plural or possessive s on it as the name
    /// scanner does ("Cages" is a word of "Axiom Cage"). A word of a name the speaker does use is no giveaway.
    /// </remarks>
    public static IReadOnlyList<string> DistinctiveWords(string? name)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            return keys;
        }

        foreach (var word in Split(name))
        {
            var key = WithoutPossessive(name, word);
            if (key.Count(char.IsLetter) < 4 || IsArticle(key) || ProperNouns.IsCommon(name, word) || Generic.Contains(key) ||
                keys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            keys.Add(key);
        }

        return keys;
    }

    /// <summary>The words "the", "a" and "an", which name matching treats as optional in front of a name.</summary>
    public static bool IsArticle(string key) => key is "the" or "a" or "an";

    private static bool IsAllDigits(StringBuilder key)
    {
        for (var i = 0; i < key.Length; i++)
        {
            if (!char.IsAsciiDigit(key[i]))
            {
                return false;
            }
        }

        return true;
    }

    // The characters of Apostrophes, as a pattern: Split calls it for every character of a text of up to 50,000.
    private static bool IsApostrophe(char c) => c is '\'' or '’' or '‘' or 'ʼ' or '`' or '´';

    // "Nadar's" folds to "nadars"; the name's word is "nadar".
    private static string WithoutPossessive(string text, TextWord word)
    {
        var surface = text.AsSpan(word.Start, word.End - word.Start);
        return surface.Length > 2 && IsApostrophe(surface[^2]) && surface[^1] is 's' or 'S' && word.Key.Length > 1
            ? word.Key[..^1]
            : word.Key;
    }
}
