using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// The full-text <c>text</c> column for one record: what <c>rules_search</c> matches beyond the name and aliases, and
/// what its snippets are cut from.
///
/// <para>
/// The description comes first because FTS5's <c>snippet()</c> shows the best-matching window of this column, and a
/// window of rules prose ("…each creature in a 20-foot-radius **sphere**…") tells the model what the hit is, where a
/// window of field values would not. Every other string value in the record follows, recursively, so a search for
/// "wizard" finds spells by class, "poison" finds monsters by immunity, and "martial" finds weapons by category,
/// without a per-kind list of fields that a re-vendor could silently outdate.
/// </para>
/// <para>
/// Left out: identifiers and links (<c>index</c>, <c>url</c>, <c>image</c>, <c>id</c>, and any value that is an
/// <c>/api/…</c> path), the Choice-tree discriminators <c>option_type</c> / <c>option_set_type</c> ("options_array",
/// "counted_reference"), and the glossary's <c>source</c> ("SRD 5.2" on all 174 records). They are machinery, not rules
/// text: indexed, "reference" or "5.2" would match hundreds of records for no reason a reader could see. The record's
/// own name is left out too; it has its own, heavier-weighted column.
/// </para>
/// <para>
/// Markdown emphasis, heading marks and table rules are stripped. They are not words, and in a snippet they are noise:
/// <c>**_Restricted Movement._**</c> reads as "Restricted Movement.". Changing what goes in here changes what search
/// finds, so it needs a <see cref="SrdIndexSchema.Version"/> bump.
/// </para>
/// </summary>
public static partial class SrdSearchText
{
    private static readonly HashSet<string> SkippedKeys = new(StringComparer.Ordinal)
    {
        "index", "url", "image", "id", "option_type", "option_set_type", "source",
    };

    /// <summary>The search text for <paramref name="record"/>: description paragraphs, then every other string value.</summary>
    public static string Build(JsonElement record)
    {
        var parts = new List<string>();
        if (record.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "desc", "description" })
        {
            if (record.TryGetProperty(key, out var description))
            {
                CollectStrings(description, parts);
            }
        }

        foreach (var property in record.EnumerateObject())
        {
            if (property.NameEquals("name") || property.NameEquals("desc") || property.NameEquals("description") ||
                SkippedKeys.Contains(property.Name))
            {
                continue;
            }

            CollectStrings(property.Value, parts);
        }

        return string.Join("\n", parts.Select(Clean).Where(p => p.Length > 0));
    }

    /// <summary>
    /// <paramref name="text"/> without markdown that is not words: <c>*</c> and <c>_</c> emphasis markers, leading
    /// <c>#</c> heading marks, table separator rows (<c>|---|---|</c>) and cell pipes. Words and line breaks are kept.
    /// </summary>
    public static string Clean(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var builder = new StringBuilder(text.Length);
        foreach (var raw in lines)
        {
            if (TableRule().IsMatch(raw))
            {
                continue;
            }

            var line = HeadingMark().Replace(raw, string.Empty)
                .Replace("*", string.Empty)
                .Replace("_", string.Empty)
                .Replace('|', ' ');
            line = Spaces().Replace(line, " ").Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
        }

        return builder.ToString();
    }

    private static void CollectStrings(JsonElement element, List<string> parts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString()!;
                if (!value.StartsWith("/api/", StringComparison.Ordinal))
                {
                    parts.Add(value);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStrings(item, parts);
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!SkippedKeys.Contains(property.Name))
                    {
                        CollectStrings(property.Value, parts);
                    }
                }

                break;
        }
    }

    // A markdown table's header rule: pipes, dashes, colons and spaces only, with at least three dashes.
    [GeneratedRegex(@"^\s*\|?[\s:|-]*-{3,}[\s:|-]*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^\s*#{1,6}\s*")]
    private static partial Regex HeadingMark();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
