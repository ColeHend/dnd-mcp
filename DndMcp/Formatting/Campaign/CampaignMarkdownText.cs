using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// The pieces every campaign tool's markdown shares: the perspective banner, table-cell escaping, echoing user text, the
/// "and N more" line and the output cap. One implementation so seven tools and the resources read alike and cut alike.
///
/// <para>
/// <b>The banner names only the perspective's own character.</b> A non-author view starts by saying whose view it is, so a
/// reader (and the model drafting a song from it) knows the names are that character's. It must say nothing about what was
/// left out: no "3 entries hidden", no "secrets withheld" count, which would itself tell the table that something exists.
/// </para>
/// <para>
/// <b>Bounded.</b> Claude Code warns at 10,000 tokens and moves a result past 25,000 to a file. Every campaign result is cut
/// at <see cref="MaxChars"/> at a line boundary with a line that says so and how to narrow the request, never mid-row,
/// unless the last line boundary falls before half the room: then the cut falls inside that line (a one-paragraph recap of
/// 40,000 characters is one line). Cutting only at line boundaries would drop the whole long line and everything after it,
/// leaving a heading and a note claiming the output was cut at 24,000 characters on a result of a few hundred.
/// </para>
/// <para>
/// <b>Whole characters.</b> Every cut here (the cap, an excerpt, an echo, and the session pages' <c>Bounded</c>) goes
/// through <see cref="WholeCharacters"/>, so none ends between the two halves of a surrogate pair: an emoji or other
/// character outside the Basic Multilingual Plane at the cut point would otherwise leave a lone high surrogate, which is not
/// text, and which a client may show as garbage or refuse to encode.
/// </para>
/// </summary>
internal static class CampaignMarkdownText
{
    /// <summary>The ceiling of every campaign tool result and resource (characters).</summary>
    public const int MaxChars = 24_000;

    /// <summary>How much user text an echo keeps (names in messages, values in history lines).</summary>
    public const int MaxEchoLength = 80;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// The first line of a non-author view, or null for the author's view: "Perspective: character:belmakor (Belmakor
    /// Silverwind). Names are the ones they know; author-only text is withheld." <paramref name="characterName"/> is the
    /// perspective's own character's name as that character knows it (its own name), never another entity's.
    /// <paramref name="typed"/> is the perspective as the caller typed it, when the repository completed it to
    /// <paramref name="perspective"/> (a short slug that begins exactly one character's, review U03): the banner then says
    /// which character it took ("character:belmakor-silverwind (Belmakor Silverwind), the only character whose handle
    /// starts with character:belmakor"), so a view is never silently someone other than the one asked for.
    /// </summary>
    public static string? Banner(Perspective perspective, bool authorView, string? characterName = null, Perspective? typed = null)
    {
        if (authorView)
        {
            return null;
        }

        var who = characterName is { Length: > 0 } ? $"{perspective.Text} ({characterName})" : perspective.Text;
        if (typed is not null && !string.Equals(typed.Text, perspective.Text, StringComparison.Ordinal))
        {
            who += $", the only character whose handle starts with {typed.Text}";
        }

        return $"_Perspective: {who}. Names are the ones this view knows; author-only text is withheld._";
    }

    /// <summary>Text safe inside a markdown table cell: pipes escaped, line breaks as spaces, trimmed; "—" when empty.</summary>
    public static string Cell(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "—";
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            switch (c)
            {
                case '|':
                    builder.Append("\\|");
                    break;
                case '\r':
                    break;
                case '\n':
                    builder.Append(' ');
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>User text shortened for an echo: at most <see cref="MaxEchoLength"/> characters plus "…", control characters escaped.</summary>
    public static string Echo(string? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var shortened = text.Length <= MaxEchoLength ? text : text[..WholeCharacters(text, MaxEchoLength)] + "…";
        var builder = new StringBuilder(shortened.Length);
        foreach (var c in shortened)
        {
            if (char.IsControl(c))
            {
                builder.Append("\\u").Append(((int)c).ToString("x4", Invariant));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>A one-line excerpt of longer text (a summary, a recap) for lists: first line, at most <paramref name="max"/> characters.</summary>
    public static string Excerpt(string? text, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= max ? line : line[..WholeCharacters(line, max - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// <paramref name="end"/> as a cut point in <paramref name="text"/> that keeps whole characters: one back when it would
    /// fall between the halves of a surrogate pair (class summary).
    /// </summary>
    public static int WholeCharacters(string text, int end) =>
        end > 0 && end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]) ? end - 1 : end;

    /// <summary>"_… and 12 more; narrow with kinds or use cursor._" when <paramref name="total"/> exceeds <paramref name="shown"/>; else null.</summary>
    public static string? More(int shown, int total, string hint) =>
        total > shown ? $"_… and {(total - shown).ToString(Invariant)} more; {hint}._" : null;

    /// <summary>
    /// Cuts <paramref name="markdown"/> at <see cref="MaxChars"/> (or <paramref name="max"/>) on a line boundary and says so,
    /// with <paramref name="hint"/> on how to ask for less; inside a line when the last line boundary is before half the
    /// room (class summary). Untouched when it fits.
    /// </summary>
    public static string Cap(string markdown, string hint, int max = MaxChars)
    {
        if (markdown.Length <= max)
        {
            return markdown;
        }

        var note = $"\n\n_Output cut at {max.ToString("N0", Invariant)} characters; {hint}._";
        var room = Math.Max(0, max - note.Length);
        var cut = markdown.LastIndexOf('\n', Math.Max(0, room - 1));
        return markdown[..WholeCharacters(markdown, cut > room / 2 ? cut : room)].TrimEnd() + note;
    }
}
