using System.Globalization;
using System.Text.Json;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Tolerant reads from vendored SRD JSON for the formatters.
///
/// <para>
/// Formatters render all ~3,000 records of 28 kinds in two editions, and field shapes drift between them (a string in
/// one file, an array in another; a number here, a string like "40 ft." there). Every accessor returns null for a
/// missing property or an unexpected JSON type instead of throwing, so one odd record renders with a line missing
/// rather than failing the whole call with the SDK's generic error. The render-everything tests are what keep lines
/// from going missing unnoticed.
/// </para>
/// </summary>
internal static class SrdJsonAccess
{
    /// <summary>The property's value when it is a string, else null.</summary>
    public static string? Str(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The property's value when it is an integral JSON number, else null.</summary>
    public static long? Int(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    /// <summary>The property's value when it is any JSON number (CR 0.125, 0.25, 0.5), else null.</summary>
    public static double? Num(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>The property's value when it is true or false, else null.</summary>
    public static bool? Bool(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>The property when it is an object, else null.</summary>
    public static JsonElement? Obj(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    /// <summary>The property's elements when it is an array, else empty.</summary>
    public static IReadOnlyList<JsonElement> Arr(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : [];

    /// <summary>
    /// Text from a property that upstream stores as a string in some files and a string array in others
    /// (<c>desc</c>, <c>description</c>, <c>higher_level</c>): its paragraphs, empty when absent.
    /// </summary>
    public static IReadOnlyList<string> Paragraphs(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!],
            JsonValueKind.Array => value.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String)
                .Select(p => p.GetString()!)
                .ToList(),
            _ => [],
        };
    }

    /// <summary>
    /// The record's descriptive text wherever this edition keeps it: <c>desc</c> (2014; 2024 magic items) or
    /// <c>description</c> (2024). Empty when it has neither.
    /// </summary>
    public static IReadOnlyList<string> Description(this JsonElement element)
    {
        var desc = element.Paragraphs("desc");
        return desc.Count > 0 ? desc : element.Paragraphs("description");
    }

    /// <summary>A number the way the SRD prints it: 0.125 → "1/8", 0.25 → "1/4", 0.5 → "1/2", 3 → "3".</summary>
    public static string FormatNumber(double value) => value switch
    {
        0.125 => "1/8",
        0.25 => "1/4",
        0.5 => "1/2",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}
