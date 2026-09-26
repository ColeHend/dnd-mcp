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

        var terms = Terms(userText);
        return terms is null ? null : "{" + string.Join(' ', columns) + "} : (" + terms + ")";
    }

    /// <summary>
    /// MATCH text for <paramref name="userText"/> across every column, for the author perspective, which may
    /// see secrets. It still quotes every word: unquoted user text can be an FTS5 syntax error ("- hoard" is
    /// read as a column filter and fails with "no such column"), which would reach the model as a bare tool
    /// failure. Returns null when there are no searchable words.
    /// </summary>
    public static string? Terms(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var raw in userText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Control characters (NUL above all) can end the FTS5 string early inside the parser.
            var cleaned = new string(raw.Where(ch => !char.IsControl(ch)).ToArray());
            var word = cleaned.TrimEnd('*');
            var prefix = word.Length < cleaned.Length;

            // A word with no letters or digits tokenizes to nothing; as an empty phrase it adds nothing.
            if (!word.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append('"').Append(word.Replace("\"", "\"\"")).Append('"');
            if (prefix)
            {
                builder.Append('*');
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
