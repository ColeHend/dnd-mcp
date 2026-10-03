using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// JSON options for the JSON campaigns.db stores (a fact's gate, an entity's data, a change_log value) and for reading
/// it back.
///
/// <para>
/// <b>Why one set of options:</b> stored JSON is compared as text. change_log records old and new values as JSON, undo
/// writes old values back, and "unchanged values are not logged" compares serialized forms; two serializers that
/// disagree on naming, null handling or escaping would log a change on every write and make undo restore a different
/// spelling. Snake_case names (the wire names), nulls omitted (absent and null mean the same in a stored object),
/// relaxed escaping (so "Björn" and quotes stay readable in the database and in history output), and the web defaults
/// for reading (case-insensitive names, numbers readable from strings), as the host binds tool input.
/// </para>
/// <para>
/// <see cref="Options"/> ignores unknown fields when reading, so a gate written by a later version still loads.
/// <see cref="Strict"/> refuses them, for input the model wrote, where a misspelt field silently ignored would store
/// something other than what was meant.
/// </para>
/// </summary>
public static class CampaignJson
{
    /// <summary>Stored JSON: snake_case, nulls omitted, relaxed escaping; unknown fields ignored when reading.</summary>
    public static readonly JsonSerializerOptions Options = Create(strict: false);

    /// <summary>As <see cref="Options"/>, but unknown fields are refused when reading.</summary>
    public static readonly JsonSerializerOptions Strict = Create(strict: true);

    private static JsonSerializerOptions Create(bool strict)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        if (strict)
        {
            options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        }

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
