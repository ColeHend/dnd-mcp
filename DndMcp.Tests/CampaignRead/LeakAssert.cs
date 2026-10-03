using System.Text.Encodings.Web;
using System.Text.Json;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// The leak rule as an assertion (contract §0): serialize the WHOLE result a reader returned for a perspective and check
/// that none of the strings it must not see appear anywhere in it, in any casing: names, refs (<c>character:keras</c>),
/// snippets, known_by lists, counts, nested author records. Serializing everything, rather than checking the fields a
/// test thinks of, is the point: a leak lives in the field nobody thought to check.
/// </summary>
internal static class LeakAssert
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Readable text (no ' for an apostrophe), so a forbidden phrase is found as written.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>The result as JSON, every public property at every depth.</summary>
    public static string Serialize(object? result) => JsonSerializer.Serialize(result, result?.GetType() ?? typeof(object), Options);

    /// <summary>Asserts none of <paramref name="forbidden"/> occurs in the serialized result (case-insensitive).</summary>
    public static void Clean(object? result, IEnumerable<string> forbidden, string because)
    {
        var json = Serialize(result);
        foreach (var word in forbidden)
        {
            Assert.True(json.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0,
                $"{because}: the result contains \"{word}\":\n{json}");
        }
    }

    /// <summary>Asserts a message carries none of the forbidden strings once the caller's own typed text is taken out.</summary>
    public static void CleanMessage(string message, string typed, IEnumerable<string> forbidden, string because)
    {
        var stripped = message.Replace(typed, string.Empty, StringComparison.OrdinalIgnoreCase);
        foreach (var word in forbidden)
        {
            Assert.True(stripped.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0,
                $"{because}: the message contains \"{word}\": {message}");
        }
    }
}
