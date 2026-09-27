using System.Globalization;
using System.Text.Json;

namespace DndMcp.Domain.Features;

/// <summary>
/// How DSL messages quote what the model sent: cut to a readable length and with control characters escaped, so a
/// pasted page or a newline in a name comes back as one short line the model can find in its own call.
/// </summary>
internal static class DslText
{
    public const int MaxEchoLength = 60;

    /// <summary>The text cut to <see cref="MaxEchoLength"/> characters, control characters escaped.</summary>
    public static string Echo(string? text)
    {
        var printable = Printable(text ?? string.Empty);
        return printable.Length <= MaxEchoLength ? printable : printable[..MaxEchoLength] + "…";
    }

    /// <summary>True for a name that fits one line of <paramref name="maxLength"/> characters.</summary>
    public static bool IsOneLine(string text, int maxLength) => text.Length <= maxLength && !text.Any(char.IsControl);

    /// <summary>"the string \"x\"", "the number 2.5", "an object": a JSON value described for a message.</summary>
    public static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string \"{Echo(value.GetString())}\"",
        JsonValueKind.Number => $"the number {Echo(value.GetRawText())}",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {value.GetRawText()}",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => value.ValueKind.ToString(),
    };

    /// <summary>1-based list position as messages count items, invariant culture.</summary>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Printable(string text) =>
        string.Concat(text.Select(c => c switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(c) => $"\\u{(int)c:x4}",
            _ => c.ToString(),
        }));
}
