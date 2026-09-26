using System.Text.Json;
using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Finds the first place two JSON values differ, ignoring property order and number formatting (<c>17</c> equals
/// <c>17.0</c>). The round-trip tests need a path, not just "not equal": with 341 monsters, a bare failure gives
/// no clue which field the models dropped or reshaped.
/// </summary>
internal static class JsonDiff
{
    /// <summary>A JSONPath-like location and description of the first difference, or null when equal.</summary>
    public static string? FirstDifference(JsonElement expected, JsonElement actual, string path = "$")
    {
        if (expected.ValueKind != actual.ValueKind &&
            !(IsBoolean(expected) && IsBoolean(actual)))
        {
            return $"{path}: expected {expected.ValueKind} {Short(expected)}, got {actual.ValueKind} {Short(actual)}";
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var actualNames = actual.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

                var missing = expectedNames.Except(actualNames).Order(StringComparer.Ordinal).FirstOrDefault();
                if (missing is not null)
                {
                    return $"{path}.{missing}: missing after round trip (expected {Short(expected.GetProperty(missing))})";
                }

                var added = actualNames.Except(expectedNames).Order(StringComparer.Ordinal).FirstOrDefault();
                if (added is not null)
                {
                    return $"{path}.{added}: not in the vendored JSON (got {Short(actual.GetProperty(added))})";
                }

                foreach (var property in expected.EnumerateObject())
                {
                    var difference = FirstDifference(property.Value, actual.GetProperty(property.Name), $"{path}.{property.Name}");
                    if (difference is not null)
                    {
                        return difference;
                    }
                }

                return null;

            case JsonValueKind.Array:
                if (expected.GetArrayLength() != actual.GetArrayLength())
                {
                    return $"{path}: expected {expected.GetArrayLength()} items, got {actual.GetArrayLength()}";
                }

                for (var i = 0; i < expected.GetArrayLength(); i++)
                {
                    var difference = FirstDifference(expected[i], actual[i], $"{path}[{i}]");
                    if (difference is not null)
                    {
                        return difference;
                    }
                }

                return null;

            case JsonValueKind.Number:
                return expected.GetDecimal() == actual.GetDecimal()
                    ? null
                    : $"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}";

            case JsonValueKind.String:
                return string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal)
                    ? null
                    : $"{path}: expected {Short(expected)}, got {Short(actual)}";

            case JsonValueKind.True:
            case JsonValueKind.False:
                return expected.GetBoolean() == actual.GetBoolean()
                    ? null
                    : $"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}";

            default:
                return null;
        }
    }

    /// <summary>
    /// Serializes every typed record with the production options and asserts it equals the vendored record at the
    /// same position, listing up to 20 offenders with the first differing path of each.
    /// </summary>
    public static void AssertRecordsRoundTrip<T>(JsonElement raw, IReadOnlyList<T> typed, Func<T, string> index)
    {
        Assert.Equal(raw.GetArrayLength(), typed.Count);

        var failures = new List<string>();
        for (var i = 0; i < typed.Count; i++)
        {
            var written = JsonSerializer.SerializeToElement(typed[i], SrdJson.Options);
            var difference = FirstDifference(raw[i], written);
            if (difference is not null)
            {
                failures.Add($"{index(typed[i])}: {difference}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} records do not round-trip:{Environment.NewLine}{string.Join(Environment.NewLine, failures.Take(20))}");
    }

    private static bool IsBoolean(JsonElement element) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static string Short(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length <= 80 ? text : text[..80] + "…";
    }
}
