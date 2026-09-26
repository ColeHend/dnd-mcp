using System.Text.Json;
using System.Text.Json.Serialization;

namespace DndMcp.Repository.Srd.Json;

/// <summary>
/// Reads a description-like field that is a JSON string in some files and a <c>string[]</c> in others, and always
/// returns paragraphs: a single string becomes a one-element list.
///
/// <para>
/// 5e-database is not consistent even within one edition. 2014 <c>desc</c> is an array in Spells and Features but a
/// plain string in Rules and Alignments, and 2024 renames it <c>description</c> and makes it a string. A plain
/// <c>string[]</c> property throws on the string files, and a plain <c>string</c> property throws on the rest. Either
/// way, the importer would lose whole resource kinds to one field.
/// </para>
/// <para>
/// Anything else (a number, an object, <c>null</c> inside the array) is a real shape change and throws a
/// <see cref="JsonException"/> that says what was found. It is never coerced to text. Writing always produces an
/// array. The importer stores the vendored bytes, not re-serialized models, so the original shape is never needed
/// back.
/// </para>
/// </summary>
public sealed class StringOrStringArrayConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException(
                $"A description field must be a string or an array of strings, but found {JsonTokenText.Describe(reader.TokenType)}.");
        }

        var paragraphs = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException(
                    $"A description array may contain only strings, but item {paragraphs.Count} is {JsonTokenText.Describe(reader.TokenType)}.");
            }

            paragraphs.Add(reader.GetString()!);
        }

        return paragraphs;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var paragraph in value)
        {
            writer.WriteStringValue(paragraph);
        }

        writer.WriteEndArray();
    }
}
