using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;

namespace DndMcp.Hosting;

/// <summary>
/// The one set of JSON options every tool, prompt and resource is registered with. The same options
/// drive three things at once — the input schema the model sees, argument deserialization, and result
/// serialization — so they must never differ between registrations.
/// </summary>
internal static class McpJson
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Copied from <see cref="McpJsonUtilities.DefaultOptions"/> rather than built fresh: the defaults
    /// carry <c>JsonStringEnumConverter</c>. Without it enums become integers in the schema and the model
    /// has to guess what 0, 1 and 2 mean.</item>
    /// <item><see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>: the default encoder escapes
    /// apostrophes and non-ASCII as <c>\uXXXX</c>. Monster names, rules text and campaign notes would reach
    /// the model mangled. "Unsafe" only matters when JSON is embedded in HTML, which never happens here.</item>
    /// <item>snake_case properties so nested objects (builds, campaign ops) read like the 5e data and the
    /// feature DSL. Top-level parameter names come from the C# parameter names and are not affected.</item>
    /// </list>
    /// </summary>
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        options.MakeReadOnly();
        return options;
    }
}
