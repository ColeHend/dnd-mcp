using System.Text;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Turns SRD text into markdown blocks: one paragraph per line of source text, with the rows of a table and the items
/// of a list kept on consecutive lines.
///
/// <para>
/// Upstream stores text two ways, and neither survives a plain "join with blank lines":
/// </para>
/// <list type="bullet">
/// <item>2014 <c>desc</c> arrays put every table row and every list item in its own array element (the Potions of
/// Healing table is six elements; each effect of a 2014 condition is its own "- …" element). Joined with blank lines,
/// a table becomes six one-row fragments that no markdown reader renders as a table, and a list becomes separate
/// lists.</item>
/// <item>2024 strings separate paragraphs with a single newline, sometimes padded (<c>"  \n "</c> in every 2024 magic
/// item), and the Rules Glossary starts tables straight after a line of text and resumes text straight after the last
/// row. In markdown a single newline continues the paragraph, so the paragraphs run together, and a text line right
/// after a table is read as another table row.</item>
/// </list>
/// <para>
/// Each line is trimmed (dropping that padding); blank lines end a block. Only whitespace and line breaks change:
/// the words are upstream's, so a model can still quote them to a player verbatim.
/// </para>
/// <para>
/// The one exception to "a line is a paragraph": upstream sometimes wraps a sentence where the PDF it was extracted from
/// wrapped it (2024 Long Jump: "…up to your Strength score if you\nmove at least 10 feet…", and a dozen 2024 class
/// features). Made into two paragraphs, the half-sentence reads as a finished rule, and a model quoting the first
/// paragraph quotes a fragment. Such a line continues the paragraph instead (<see cref="ContinuesSentence"/>).
/// </para>
/// </summary>
internal static class SrdProse
{
    private enum LineKind
    {
        None,
        Text,
        TableRow,
        ListItem,
    }

    /// <summary>
    /// The paragraphs as markdown blocks separated by blank lines; empty when there is no text. A table row or list
    /// item continues the block above it when that block is the same kind, even across source paragraphs (the 2014
    /// shape). A text line starts a new block unless it continues a sentence the line above it left unfinished in the
    /// same source paragraph (<see cref="ContinuesSentence"/>). A blank line always ends a block, and so does the end of
    /// an array element: 2014 keeps one paragraph per element, so an element boundary is a real paragraph break.
    /// </summary>
    public static string Join(IEnumerable<string> paragraphs)
    {
        var blocks = new List<StringBuilder>();
        var previous = LineKind.None;
        string? previousText = null;

        foreach (var paragraph in paragraphs)
        {
            previousText = null;
            foreach (var raw in paragraph.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    previous = LineKind.None;
                    previousText = null;
                    continue;
                }

                var kind = KindOf(line);
                if (kind == LineKind.Text && previousText is not null && ContinuesSentence(previousText, line))
                {
                    blocks[^1].Append(' ').Append(line);
                }
                else if (kind != LineKind.Text && kind == previous)
                {
                    blocks[^1].Append('\n').Append(line);
                }
                else
                {
                    blocks.Add(new StringBuilder(line));
                }

                previous = kind;
                previousText = kind == LineKind.Text ? line : null;
            }
        }

        return string.Join("\n\n", blocks);
    }

    /// <summary>
    /// True when <paramref name="next"/> is the rest of a sentence <paramref name="line"/> left unfinished: the line
    /// does not end in sentence punctuation (<c>.</c> <c>!</c> <c>?</c> <c>:</c>, possibly inside closing quotes,
    /// brackets or emphasis), and either the next line starts in lower case, or the line ends with a comma or with
    /// "a", "an" or "the", which no paragraph ends with. An unfinished line followed by a capital is left alone: that is
    /// how a heading or label sits above its text ("Wondrous Item", "Arid Land"), and joining it would bury the label.
    /// Across the vendored data this joins the 17 sentences upstream wrapped in 2024 features, subclasses and the
    /// glossary, and nothing else; the whole-data tests in <c>SrdProseTests</c> pin both.
    /// </summary>
    private static bool ContinuesSentence(string line, string next)
    {
        var end = line.TrimEnd('"', '\'', '\u201D', '\u2019', ')', ']', '*', '_');
        if (end.Length == 0 || end[^1] is '.' or '!' or '?' or ':')
        {
            return false;
        }

        return char.IsLower(next[0]) || line[^1] == ',' || EndsWithArticle(line);
    }

    private static bool EndsWithArticle(string line)
    {
        var start = line.LastIndexOf(' ') + 1;
        return line[start..] is "a" or "an" or "the";
    }

    /// <summary>
    /// A table row starts with a pipe; a list item with "- ", "* ", "+ " or "1. " / "1) ". Emphasis ("*Mechanical
    /// trap*", "**Speed 0.**") has no space after its asterisks, so it stays text.
    /// </summary>
    private static LineKind KindOf(string line)
    {
        if (line[0] == '|')
        {
            return LineKind.TableRow;
        }

        if (line.Length > 1 && (line[0] is '-' or '*' or '+') && line[1] == ' ')
        {
            return LineKind.ListItem;
        }

        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits > 0 && digits + 1 < line.Length && (line[digits] is '.' or ')') && line[digits + 1] == ' '
            ? LineKind.ListItem
            : LineKind.Text;
    }
}
