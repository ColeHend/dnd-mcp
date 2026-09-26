using System.Text.Json;

namespace DndMcp.Repository.Srd.Json;

/// <summary>
/// Plain-English token names for converter error messages. "found StartObject" tells a reader nothing when a
/// re-vendored file changes shape; "found an object" does.
/// </summary>
internal static class JsonTokenText
{
    public static string Describe(JsonTokenType token) => token switch
    {
        JsonTokenType.String => "a string",
        JsonTokenType.Number => "a number",
        JsonTokenType.True or JsonTokenType.False => "a boolean",
        JsonTokenType.Null => "null",
        JsonTokenType.StartObject => "an object",
        JsonTokenType.StartArray => "an array",
        _ => token.ToString(),
    };
}
