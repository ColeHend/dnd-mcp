using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>One word of a search query, folded (<see cref="CampaignText.Key"/>); the last word of a token typed with <c>*</c> is a prefix.</summary>
internal readonly record struct QueryWord(string Key, bool Prefix);

/// <summary>One whitespace-separated token of a query: "G.O.D.S." is one token of four folded words.</summary>
internal sealed record QueryToken(IReadOnlyList<QueryWord> Words);

/// <summary>
/// Text helpers of the read path that must be done in C#: matching query words against text the perspective may see
/// (known_as names, fact phrasings) and building snippets.
///
/// <para>
/// <b>Why snippets are built here and never by FTS5:</b> <c>snippet()</c> and <c>highlight()</c> read whichever column
/// they are pointed at, including <c>secret</c> and <c>hidden_aliases</c>, whatever the MATCH column filter said
/// (<c>understand-sqlite.md</c> (f)13). A snippet built from a list of fields the caller has already filtered for the
/// perspective can only ever quote those fields. The same goes for the known_as branch of search: a knower's name for an
/// entity is not in the FTS index at all, so the match is decided here, on exactly that name.
/// </para>
/// <para>
/// Matching folds like <see cref="CampaignText.Key"/> (case, diacritics, apostrophes) and forgives the English endings the
/// FTS porter stemmer forgives (seal/seals/sealed/sealing), so a C# match and an FTS match agree on ordinary words.
/// </para>
/// </summary>
internal static class ReadText
{
    /// <summary>The query's tokens; empty when it has no letters or digits.</summary>
    public static IReadOnlyList<QueryToken> Tokens(string? query)
    {
        var tokens = new List<QueryToken>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return tokens;
        }

        foreach (var raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = raw.EndsWith('*');
            var keys = CampaignText.Key(raw.TrimEnd('*')).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (keys.Length == 0)
            {
                continue;
            }

            tokens.Add(new QueryToken(keys.Select((k, i) => new QueryWord(k, prefix && i == keys.Length - 1)).ToList()));
        }

        return tokens;
    }

    /// <summary>
    /// Whether <paramref name="text"/> holds the query: every token (<paramref name="all"/>) or any token, where a token
    /// matches when each of its words occurs as a word of the text.
    /// </summary>
    public static bool Matches(string? text, IReadOnlyList<QueryToken> tokens, bool all)
    {
        if (string.IsNullOrWhiteSpace(text) || tokens.Count == 0)
        {
            return false;
        }

        var words = Words(text).Select(w => w.Key).ToList();
        bool TokenMatches(QueryToken token) => token.Words.All(q => words.Any(w => WordMatches(w, q)));
        return all ? tokens.All(TokenMatches) : tokens.Any(TokenMatches);
    }

    /// <summary>
    /// A window of about <paramref name="width"/> characters of <paramref name="text"/> around its first word that matches
    /// the query, on one line, with "…" where it was cut; null when no word matches.
    /// </summary>
    public static string? Snippet(string? text, IReadOnlyList<QueryToken> tokens, int width = 180)
    {
        if (string.IsNullOrWhiteSpace(text) || tokens.Count == 0)
        {
            return null;
        }

        var queryWords = tokens.SelectMany(t => t.Words).ToList();
        var hit = Words(text).FirstOrDefault(w => queryWords.Any(q => WordMatches(w.Key, q)));
        if (hit.Key is null)
        {
            return null;
        }

        var start = Math.Max(0, hit.Start - (width / 3));
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }

        var end = Math.Min(text.Length, start + width);
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && end - start < width + 40)
        {
            end++;
        }

        var body = OneLine(text[start..end]);
        return (start > 0 ? "…" : string.Empty) + body + (end < text.Length ? "…" : string.Empty);
    }

    /// <summary>The first <paramref name="max"/> characters of <paramref name="text"/> on one line, cut at a word, with "…".</summary>
    public static string Excerpt(string text, int max)
    {
        var line = OneLine(text);
        if (line.Length <= max)
        {
            return line;
        }

        var cut = line.LastIndexOf(' ', Math.Max(0, max - 1));
        return line[..(cut > max / 2 ? cut : max)].TrimEnd() + "…";
    }

    /// <summary>Whitespace runs (newlines included) collapsed to one space, trimmed.</summary>
    public static string OneLine(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>The first <paramref name="lines"/> non-blank lines of a markdown text (a recap's opening), or null.</summary>
    public static string? FirstLines(string? text, int lines, int maxLength = 400)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var kept = text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).Take(lines);
        var joined = string.Join("\n", kept);
        return joined.Length <= maxLength ? joined : Excerpt(joined, maxLength);
    }

    /// <summary>Each word of the text: its folded key and span in the original.</summary>
    internal static IReadOnlyList<(string Key, int Start, int End)> Words(string text)
    {
        var words = new List<(string, int, int)>();
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text, i))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && (IsWordChar(text, i) || IsApostrophe(text[i]) || IsMark(text[i])))
            {
                i++;
            }

            var key = CampaignText.Key(text[start..i]).Replace(" ", string.Empty, StringComparison.Ordinal);
            if (key.Length > 0)
            {
                words.Add((key, start, i));
            }
        }

        return words;
    }

    /// <summary>
    /// A text word against a query word: a prefix query word is a prefix of the word; otherwise the two are equal after
    /// dropping a common English ending (s, es, ed, ing, er), as the porter stemmer does for the FTS side.
    /// </summary>
    internal static bool WordMatches(string word, QueryWord query) =>
        query.Prefix
            ? word.StartsWith(query.Key, StringComparison.Ordinal)
            : word == query.Key || Stem(word) == Stem(query.Key);

    private static string Stem(string word)
    {
        foreach (var ending in (ReadOnlySpan<string>)["ing", "ies", "es", "ed", "er", "s"])
        {
            if (word.Length - ending.Length >= 3 && word.EndsWith(ending, StringComparison.Ordinal))
            {
                var stem = word[..^ending.Length];
                return ending == "ies" ? stem + "y" : stem.TrimEnd('e');
            }
        }

        return word.TrimEnd('e');
    }

    private static bool IsWordChar(string text, int i) => char.IsLetterOrDigit(text[i]);

    private static bool IsMark(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool IsApostrophe(char c) => c is '\'' or '’' or '‘' or 'ʼ';
}
