using System.Globalization;
using System.Text;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Markdown building blocks shared by every SRD formatter, so that refs, tables and paragraphs read the same whichever
/// kind the model looks up.
/// </summary>
internal static class SrdMarkdownText
{
    /// <summary>
    /// What a body says when the record has nothing to show. One wording for every kind, so "the SRD has no text here"
    /// reads the same wherever it happens and can never be mistaken for a formatter that silently dropped the text.
    /// </summary>
    public const string NoDescription = "*No description in the SRD data.*";

    /// <summary>
    /// A linked record as "Name (<c>ref</c>)" when its URL resolves to a ref, otherwise just its name.
    /// The ref is what the model passes back to <c>rules_get</c>, so it is shown verbatim in code style.
    /// Any <c>note</c> on the reference is left out; see <see cref="LinkWithNote(JsonElement)"/>.
    /// </summary>
    public static string Link(JsonElement apiReference) => LinkWithNote(apiReference, note: null);

    /// <summary>
    /// <see cref="Link"/> with the reference's own <c>note</c> kept next to the name it qualifies, ahead of the ref:
    /// "Magic Initiate (Cleric) (<c>2024/feat/magic-initiate</c>)", "Charmed (with Mind Blank)
    /// (<c>2024/condition/charmed</c>)". Upstream narrows a reference with that note; without it the acolyte would seem
    /// to grant Magic Initiate from any spell list, and the archmage to be immune to Charmed at all times.
    /// </summary>
    public static string LinkWithNote(JsonElement apiReference) => LinkWithNote(apiReference, apiReference.Str("note"));

    /// <summary>
    /// <see cref="Link"/> with a qualifier the caller worked out ("Command (level 2 version)"), in place of the
    /// reference's own note. A blank note shows the plain link; the note is trimmed, because upstream pads some.
    /// </summary>
    public static string LinkWithNote(JsonElement apiReference, string? note)
    {
        var name = apiReference.Str("name") ?? apiReference.Str("index") ?? "?";
        var shown = string.IsNullOrWhiteSpace(note) ? name : $"{name} ({note.Trim()})";
        return SrdRef.FromApiUrl(apiReference.Str("url")) is { } reference ? $"{shown} (`{reference}`)" : shown;
    }

    /// <summary>"Name (<c>ref</c>), Name (<c>ref</c>)" for an array of API references; empty string when there are none.</summary>
    public static string LinkList(IEnumerable<JsonElement> apiReferences) =>
        string.Join(", ", apiReferences.Select(Link));

    /// <summary>Just the names of an array of API references, comma-separated.</summary>
    public static string NameList(IEnumerable<JsonElement> apiReferences) =>
        string.Join(", ", apiReferences.Select(r => r.Str("name") ?? r.Str("index") ?? "?"));

    /// <summary>A score's modifier as the SRD prints it: 10 → "+0", 8 → "−1" (a true minus sign), 18 → "+4".</summary>
    public static string AbilityModifier(long score)
    {
        var modifier = (long)Math.Floor((score - 10) / 2.0);
        return Signed(modifier);
    }

    /// <summary>"+3", "+0", "−2" (a true minus sign, as the SRD prints it).</summary>
    public static string Signed(long value) =>
        value < 0 ? "−" + (-value).ToString(CultureInfo.InvariantCulture) : "+" + value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A markdown table. Cells are made safe for a table row: pipes are escaped and line breaks become spaces, because
    /// either would otherwise split the row and shift every later column.
    /// </summary>
    public static string Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.Append("| ").Append(string.Join(" | ", headers.Select(Cell))).Append(" |\n");
        builder.Append('|').Append(string.Concat(headers.Select(_ => "---|"))).Append('\n');
        foreach (var row in rows)
        {
            builder.Append("| ").Append(string.Join(" | ", row.Select(Cell))).Append(" |\n");
        }

        return builder.ToString().TrimEnd('\n');
    }

    /// <summary>One table cell's text: pipes escaped, line breaks flattened, empty shown as an em dash.</summary>
    public static string Cell(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "—";
        }

        return text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace("|", "\\|").Trim();
    }

    /// <summary>
    /// Paragraphs separated by blank lines, unchanged otherwise. Only for text that is already well-formed markdown
    /// paragraph by paragraph (the 2014 rule tree). SRD prose in general goes through <see cref="SrdProse.Join"/>:
    /// 2014 stores each table row and list item as its own element, and blank lines between them break the table apart.
    /// </summary>
    public static string JoinParagraphs(IEnumerable<string> paragraphs) =>
        string.Join("\n\n", paragraphs.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()));

    /// <summary>"**Label** value" as one line, or null when the value is empty, for "**Casting Time** 1 action" lines.</summary>
    public static string? Field(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"**{label}** {value.Trim()}";

    /// <summary>Joins non-null lines with single line breaks (markdown hard breaks need two spaces; lists and fields don't).</summary>
    public static string Lines(IEnumerable<string?> lines) =>
        string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));

    /// <summary>Joins non-empty blocks with blank lines.</summary>
    public static string Blocks(IEnumerable<string?> blocks) =>
        string.Join("\n\n", blocks.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!.TrimEnd()));

    /// <summary>A section: <c>### Title</c> then the body; null when the body is empty, so empty sections vanish.</summary>
    public static string? Section(string title, string? body) =>
        string.IsNullOrWhiteSpace(body) ? null : $"### {title}\n\n{body.TrimEnd()}";

    /// <summary>The source line for an edition: which SRD the text comes from.</summary>
    public static string SourceFor(string edition) => edition switch
    {
        SrdEdition.Edition2014 => "SRD 5.1",
        SrdEdition.Edition2024 => "SRD 5.2.1",
        _ => edition,
    };
}
