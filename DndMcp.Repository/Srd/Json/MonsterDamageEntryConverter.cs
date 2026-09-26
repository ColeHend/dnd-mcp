using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Repository.Srd.Json;

/// <summary>
/// Reads one element of a 2014 monster action's <c>damage[]</c>. An element is either a damage roll
/// (<c>{damage_type, damage_dice, dc?}</c>) or a choice between rolls (<c>{choose, type: "damage", from}</c>), such as
/// the djinni's "1d6 lightning or thunder (djinni's choice)". There are 16 such choices in 2014 and none in 2024.
///
/// <para>
/// The discriminator is the presence of <c>choose</c>, which every 5e-database Choice has and no damage roll has.
/// Reading everything as a damage roll would make each choice a roll with no dice. The DPR engine would then drop
/// the djinni's rider without any error, and a strict test would fail on the unmapped <c>choose</c>.
/// </para>
/// <para>
/// A <c>choose</c> object whose <c>type</c> is not <c>"damage"</c> is some other Choice that has leaked into a damage
/// array. It throws, and so does anything that is not an object, rather than being read as an empty roll.
/// <see cref="HandleNull"/> is on so that a <c>null</c> element reaches that check. Otherwise the serializer stores
/// null in the list without calling the converter, and the DPR engine gets a NullReferenceException instead of an
/// import error.
/// </para>
/// <para>
/// The discriminator is found by scanning a COPY of the reader (a struct), and the entry is then read from the
/// original reader by the default converter for the chosen type. Parsing the entry into a <c>JsonDocument</c> first
/// would restart path tracking inside the entry. A re-vendor that adds a field to one of the 2014 damage entries would
/// then report <c>$.new_field</c> at line 0 instead of <c>$[212].actions[1].damage[0]</c>, and nobody could tell which
/// monster to look at.
/// </para>
/// </summary>
public sealed class MonsterDamageEntryConverter : JsonConverter<MonsterDamageEntry2014>
{
    public override bool HandleNull => true;

    public override MonsterDamageEntry2014 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                "A monster damage entry must be an object (a damage roll or a damage choice), " +
                $"but found {JsonTokenText.Describe(reader.TokenType)}.");
        }

        var discriminator = PeekDiscriminator(reader);

        if (!discriminator.HasChoose)
        {
            return MonsterDamageEntry2014.FromDamage(ReadWithDefaultConverter<MonsterDamage>(ref reader, options));
        }

        if (discriminator.Type != ChoiceTypes.Damage)
        {
            throw new JsonException(
                "A monster damage entry with \"choose\" must be a damage choice (\"type\": \"damage\"), " +
                $"but its type is {discriminator.TypeForMessage}.");
        }

        return MonsterDamageEntry2014.FromChoice(ReadWithDefaultConverter<Choice<DamageOption2014>>(ref reader, options));
    }

    public override void Write(Utf8JsonWriter writer, MonsterDamageEntry2014 value, JsonSerializerOptions options)
    {
        // HandleNull also routes null values here when writing.
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else if (value.Choice is not null)
        {
            JsonSerializer.Serialize(writer, value.Choice, options);
        }
        else
        {
            JsonSerializer.Serialize(writer, value.Damage, options);
        }
    }

    // Calling the resolved converter's Read directly, rather than JsonSerializer.Deserialize(ref reader), keeps any
    // exception's Path unset, so the outer serializer stamps it with the entry's absolute location in the file.
    private static T ReadWithDefaultConverter<T>(ref Utf8JsonReader reader, JsonSerializerOptions options)
        where T : class
    {
        var converter = (JsonConverter<T>)options.GetConverter(typeof(T));
        return converter.Read(ref reader, typeof(T), options)
               ?? throw new JsonException($"A monster damage entry read as null {typeof(T).Name}.");
    }

    /// <summary>
    /// Scans the object's top-level properties on a copy of the reader, leaving the caller's reader where it was.
    /// </summary>
    /// <remarks>
    /// A file read from a stream arrives in blocks, and <see cref="Utf8JsonReader.Skip"/> throws on any block but the
    /// last. <see cref="Utf8JsonReader.TrySkip"/> succeeds because the serializer buffers a custom converter's whole
    /// value before calling it. String-input tests never see a partial block; the stream test does.
    /// </remarks>
    private static Discriminator PeekDiscriminator(Utf8JsonReader probe)
    {
        var hasChoose = false;
        string? type = null;
        var typeForMessage = "missing";

        while (probe.Read() && probe.TokenType == JsonTokenType.PropertyName)
        {
            var isChoose = probe.ValueTextEquals("choose"u8);
            var isType = probe.ValueTextEquals("type"u8);
            probe.Read();

            hasChoose |= isChoose;
            if (isType)
            {
                type = probe.TokenType == JsonTokenType.String ? probe.GetString() : null;
                typeForMessage = probe.TokenType switch
                {
                    JsonTokenType.String => $"\"{type}\"",
                    JsonTokenType.Number => probe.HasValueSequence
                        ? Encoding.UTF8.GetString(probe.ValueSequence)
                        : Encoding.UTF8.GetString(probe.ValueSpan),
                    _ => JsonTokenText.Describe(probe.TokenType),
                };
            }

            if (!probe.TrySkip())
            {
                throw new JsonException("A monster damage entry was not fully buffered; this is a serializer contract violation.");
            }
        }

        return new Discriminator(hasChoose, type, typeForMessage);
    }

    /// <param name="HasChoose">Whether the object has a top-level <c>choose</c>.</param>
    /// <param name="Type">The string value of <c>type</c>; null when absent or not a string.</param>
    /// <param name="TypeForMessage"><c>type</c> as an error message shows it: <c>"action"</c>, <c>7</c>, <c>missing</c>.</param>
    private readonly record struct Discriminator(bool HasChoose, string? Type, string TypeForMessage);
}
