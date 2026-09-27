using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DndMcp.Repository.Sqlite;

/// <summary>
/// Turns what a user or the model typed into FTS5 MATCH text that cannot change which columns it searches.
///
/// <para>
/// Perspective-safe search (PLAN.md "6. Campaign tracking", principle 4) keeps DM secrets out of
/// player-perspective results with an FTS5 column filter that leaves the <c>secret</c> column out:
/// <c>{name aliases summary body tags} : …</c>. The form drafted in research/04 §4,
/// <c>'{…} : ' || :q</c>, LEAKS. FTS5 binds a column filter to the single phrase right after it, so
/// "keras hoard" filters only "keras" and lets "hoard" match the secret column: an innocent two-word search
/// is enough. Text containing FTS5 syntax can do it on purpose (<c>dragon OR secret:hoard</c>, or an
/// unbalanced <c>)</c> that closes the group early). PerspectiveSafeSearchTests pins each of these leaks
/// against raw concatenation, and pins that this builder closes them.
/// </para>
/// <para>
/// Two things make it safe, and both are needed:
/// <list type="number">
/// <item>Every word becomes an FTS5 string literal (<c>"…"</c> with <c>"</c> doubled). Inside a string,
/// <c>OR</c>, <c>NOT</c>, <c>NEAR</c>, <c>:</c>, <c>{</c>, <c>(</c>, <c>^</c> and <c>-</c> are plain text for the
/// tokenizer, so no word can become an operator or a column name.</item>
/// <item>All the words go in one parenthesised group under the filter. A filter on a group applies to every
/// phrase in it, and nested filters intersect, so no phrase inside can reach a column outside the list.</item>
/// </list>
/// </para>
/// <para>
/// The price is that search is words ANDed together, with no user-facing OR, NOT or NEAR. A trailing
/// <c>*</c> on a word still means prefix search (<c>"belm"*</c>), which is the one operator worth keeping.
/// That trade-off is a proposal for Phase 6 (campaign_search) to confirm. If it wants OR, add it here as
/// an operator this builder emits between quoted words, never by passing user syntax through.
/// </para>
/// <para>
/// This closes the QUERY side only. A filter controls which columns are read, not what the triggers put
/// in them, so each listed column must hold nothing but text the perspective may see. research/04's
/// <c>entity_fts_au</c> breaks that for aliases: it indexes author-only aliases into <c>aliases</c>.
/// AliasVisibilitySearchTests pins that leak and the trigger shape that fixes it.
/// </para>
/// </summary>
public static partial class Fts5Query
{
    /// <summary>
    /// MATCH text for <paramref name="userText"/> restricted to <paramref name="columns"/>, for example
    /// <c>{name aliases summary body tags} : ("keras" "hoard"*)</c>. Returns null when the text has no
    /// searchable words (empty, whitespace or punctuation only): FTS5 has no "match everything" query, so the
    /// caller must list without FTS instead.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A column name is not a plain identifier. Column lists come from code, never from input; this guard
    /// stops a future caller from passing input through and reopening the leak this type exists to close.
    /// </exception>
    public static string? ColumnFiltered(string? userText, IReadOnlyCollection<string> columns)
    {
        var filter = ColumnFilter(columns);
        var terms = Terms(userText);
        return terms is null ? null : filter + "(" + terms + ")";
    }

    // "{a b c} : ", after checking every column is a plain identifier. Checked before the text is looked at, so a
    // bad column list fails even for input with no searchable words.
    private static string ColumnFilter(IReadOnlyCollection<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required; an empty filter would match nothing.", nameof(columns));
        }

        foreach (var column in columns)
        {
            if (column is null || !IdentifierPattern().IsMatch(column))
            {
                throw new ArgumentException($"'{column}' is not a plain FTS5 column name.", nameof(columns));
            }
        }

        return "{" + string.Join(' ', columns) + "} : ";
    }

    /// <summary>
    /// MATCH text for <paramref name="userText"/> across every column, for the author perspective, which may
    /// see secrets. It still quotes every word: unquoted user text can be an FTS5 syntax error ("- hoard" is
    /// read as a column filter and fails with "no such column"), which would reach the model as a bare tool
    /// failure. Returns null when there are no searchable words.
    /// </summary>
    public static string? Terms(string? userText) => Join(Words(userText), " ");

    /// <summary>
    /// Like <see cref="Terms"/>, but a row matches when ANY word does: <c>"grapple" OR "escape"*</c>. It is the
    /// fallback when nothing matches every word, so a query with one unmatched word (a typo, an edition-specific
    /// term) still finds something, and the caller must say the match was partial.
    ///
    /// <para>
    /// OR is emitted by this builder between words that are each still quoted, so it is the only operator in
    /// the output and no user word can become one. A user's own "OR" is a quoted word like any other.
    /// </para>
    /// </summary>
    public static string? AnyTerms(string? userText) => Join(Words(userText), " OR ");

    /// <summary>
    /// <see cref="AnyTerms"/> restricted to <paramref name="columns"/>, for example <c>{name} : ("fire"* OR "bolt"*)</c>.
    /// A filter on a parenthesised group applies to every phrase in it, OR included, so the any-word form is as
    /// column-safe as <see cref="ColumnFiltered"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A column name is not a plain identifier (see <see cref="ColumnFiltered"/>).</exception>
    public static string? ColumnFilteredAnyTerms(string? userText, IReadOnlyCollection<string> columns)
    {
        var filter = ColumnFilter(columns);
        var terms = AnyTerms(userText);
        return terms is null ? null : filter + "(" + terms + ")";
    }

    /// <summary>
    /// How many distinct searchable words <paramref name="userText"/> has, counted exactly as <see cref="Terms"/> counts
    /// them (a repeated word once).
    /// A caller deciding whether an any-word fallback could find more than the all-words query needs this: with one
    /// word the two queries are the same.
    /// </summary>
    public static int WordCount(string? userText) => Words(userText).Count;

    // Each distinct word as an FTS5 string literal, with a trailing * kept as the prefix operator.
    //
    // The text is NFKC-normalised and stripped of format characters first: text copied from a PDF or a web page carries
    // ligatures ("ﬂaming"), fullwidth letters and invisible soft hyphens or zero-width spaces ("Fire\u00ADball"), which
    // the tokenizer would otherwise index-miss or split. A word that repeats (ignoring case) is kept once: FTS5
    // evaluates every copy of a phrase, so repeats made a query roughly quadratic (2,000 × "fire*" took 14 s) without
    // matching anything more.
    private static List<string> Words(string? userText)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(userText))
        {
            return words;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in Normalize(userText, NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Control characters (NUL above all) can end the FTS5 string early inside the parser.
            var cleaned = new string(raw.Where(ch => !char.IsControl(ch) && CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format).ToArray());
            var word = cleaned.TrimEnd('*');
            var prefix = word.Length < cleaned.Length;

            // A word with no letters or digits tokenizes to nothing; as an empty phrase it adds nothing.
            if (!word.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            var quoted = "\"" + word.Replace("\"", "\"\"") + "\"";
            var phrase = prefix ? quoted + "*" : quoted;
            if (seen.Add(phrase.ToLowerInvariant()))
            {
                words.Add(phrase);
            }
        }

        return words;
    }

    /// <summary>
    /// <paramref name="text"/> in <paramref name="form"/>. Text that is not well-formed UTF-16 (a lone surrogate, which
    /// a JSON string can carry as an escape) cannot be normalised and would throw; its lone surrogates are dropped first,
    /// since they are no letter anyone typed. Shared with <c>SrdNames.Key</c>, which folds names the same way.
    /// </summary>
    internal static string Normalize(string text, NormalizationForm form)
    {
        try
        {
            return text.Normalize(form);
        }
        catch (ArgumentException)
        {
            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder.Append(text[i]).Append(text[++i]);
                }
                else if (!char.IsSurrogate(text[i]))
                {
                    builder.Append(text[i]);
                }
            }

            return builder.ToString().Normalize(form);
        }
    }

    private static string? Join(List<string> words, string separator) =>
        words.Count == 0 ? null : string.Join(separator, words);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
