using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DndMcp.Repository.Srd;

/// <summary>
/// The serializer options every reader of the vendored 5e-database JSON uses.
///
/// <para>
/// <b>Unknown fields are skipped here on purpose.</b> In production a new upstream field should never stop the server
/// from indexing the rest of a file it otherwise understands. The strictness lives in the tests instead:
/// <c>DndMcp.Tests/Srd</c> copies these options with <see cref="JsonUnmappedMemberHandling.Disallow"/>, so any field
/// the typed models do not cover fails a test at re-vendor time. It never fails a user's session.
/// </para>
/// <para>
/// Missing and null properties are <b>not</b> lenient. <c>required</c> members and
/// <see cref="JsonSerializerOptions.RespectNullableAnnotations"/> make a monster without an index, or a null property
/// where the model promises a value, fail at import. Accepting them would move the failure to a
/// <see cref="NullReferenceException"/> deep inside a formatter or the simulator.
/// </para>
/// <para>
/// <see cref="JsonSerializerOptions.RespectNullableAnnotations"/> does <b>not</b> reach collection elements:
/// <c>"classes": [null]</c> reads as a list holding null. Only three places reject a null element themselves:
/// description paragraphs (<see cref="Json.StringOrStringArrayConverter"/>), 2014 damage entries
/// (<see cref="Json.MonsterDamageEntryConverter"/>) and the top-level records in <see cref="ReadArray{T}"/>. Everywhere
/// else the guarantee rests on the data. <c>VendoredFileTests</c> pins that the vendored files contain no JSON null at
/// all, and <see cref="ContentManifestVerifier"/> refuses any other bytes. A re-vendor that introduces a null fails
/// that test, and a check for it belongs here before the new data is accepted.
/// </para>
/// <para>
/// Serialization omits nulls so that a typed model written back out has the same shape as the vendored JSON. The
/// round-trip tests depend on that.
/// </para>
/// </summary>
public static class SrdJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>
    /// Reads a whole vendored file (always a top-level JSON array) into typed models.
    ///
    /// <para>
    /// Errors are re-thrown with the file and the JSON path in the MESSAGE. The serializer puts the location only in
    /// <see cref="JsonException.Path"/>, which test runners and log lines do not show. A strict re-vendor failure in a
    /// 1.3 MB monster file would otherwise read "could not map 'new_field' on MonsterUsage" with no hint of which
    /// monster to look at.
    /// </para>
    /// </summary>
    /// <exception cref="JsonException">
    /// The file is not an array of <typeparamref name="T"/>, or one of its records is null. The message names the file
    /// and the JSON path.
    /// </exception>
    public static IReadOnlyList<T> ReadArray<T>(string path, JsonSerializerOptions? options = null)
    {
        List<T>? records;
        using (var stream = File.OpenRead(path))
        {
            try
            {
                records = JsonSerializer.Deserialize<List<T>>(stream, options ?? Options);
            }
            catch (JsonException ex)
            {
                var line = ex.LineNumber is { } zeroBased ? $", line {zeroBased + 1}" : string.Empty;
                throw new JsonException(
                    $"{path} at {ex.Path ?? "$"}{line}: {ex.Message}", ex.Path, ex.LineNumber, ex.BytePositionInLine, ex);
            }
        }

        if (records is null)
        {
            throw new JsonException($"{path} contains JSON null, not an array.");
        }

        var nullIndex = records.FindIndex(record => record is null);
        if (nullIndex >= 0)
        {
            throw new JsonException($"{path} at $[{nullIndex}]: a record is JSON null, not a {typeof(T).Name}.");
        }

        return records;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Rules text is full of apostrophes and typographic quotes; the default encoder would write them as \uXXXX.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
