using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Features;

/// <summary>
/// JSON options for reading and writing the feature DSL inside Domain (tests, and later the stored
/// <c>character_sheet.sim_profile</c>).
///
/// <para>
/// <b>They must bind exactly as the host's <c>McpJson.Options</c> do</b>, because the MCP host binds the same spec classes
/// with those: a build that parses here must parse there and mean the same. Hence the same base (the web defaults, as
/// <c>McpJsonUtilities.DefaultOptions</c> uses: case-insensitive names, numbers readable from strings), snake_case names,
/// null properties left out when writing, and string enums. An <c>object?</c> property (every step value, the target's
/// <c>cr</c>) binds as a <see cref="JsonElement"/> under both.
/// </para>
/// <para>
/// One deliberate difference: <b>unknown fields are refused</b> here, where System.Text.Json would ignore them. The host
/// refuses them too, earlier, in its argument guard against the schema; without this, a stored build with a misspelt
/// <c>"cuont": 2</c> would load as one attack a turn and no one would know.
/// </para>
/// </summary>
public static class DslJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>
    /// Reads one DSL object (a build, a target, a feature) from JSON text, turning a malformed document into a
    /// <see cref="DndInputException"/> that names where it failed ("build: could not read field 'attacks[0].count' …").
    /// </summary>
    /// <param name="what">What the text is, for the message: "build", "target".</param>
    public static T Deserialize<T>(string json, string what)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options)
                   ?? throw new DndInputException($"{what}: is null; give a JSON object.");
        }
        catch (JsonException ex)
        {
            var where = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? string.Empty : $" at '{ex.Path}'";
            throw new DndInputException(
                $"{what}: could not be read{where}: {FirstSentence(ex.Message)} Field names are snake_case, e.g. \"to_hit\", and " +
                "unknown fields are refused.", ex);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    // System.Text.Json appends "Path: … | LineNumber: …" to its messages; the path is reported separately.
    private static string FirstSentence(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        var text = (cut >= 0 ? message[..cut] : message).Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }
}
