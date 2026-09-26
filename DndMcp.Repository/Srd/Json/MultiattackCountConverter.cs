using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Repository.Srd.Json;

/// <summary>
/// Reads a multiattack <c>count</c>. It can be a JSON string (<c>"2"</c>, <c>"Number of Heads"</c>, <c>"1d4"</c>) or a
/// JSON number.
///
/// <para>
/// In v7.0.0 every count is a string, in <c>actions[]</c> and inside <c>action_options</c> alike. Older 5e-database
/// releases, and the live API responses the research was based on, used numbers inside <c>action_options</c>. Both
/// forms are accepted so a re-vendor in either direction still loads, and the data-quirk tests pin which form the
/// vendored tag uses. A plain <c>int</c> property would throw on "Number of Heads" (hydra) and "1d4" (violet
/// fungus). A plain <c>string</c> property would throw on the numeric form.
/// </para>
/// <para>
/// Booleans, objects, arrays, empty strings, fractions, negative numbers, numbers too large for an <c>int</c> and
/// whole numbers written as <c>2.0</c> or <c>2e0</c> are malformed. Each throws a <see cref="JsonException"/> saying
/// which of these it is, rather than becoming a count of zero. Writing keeps the token kind that was read, so a typed
/// monster serializes back to the same JSON. That is also why <c>2.0</c> is refused rather than read as 2: it would be
/// written back as <c>2</c>.
/// </para>
/// </summary>
public sealed class MultiattackCountConverter : JsonConverter<MultiattackCount>
{
    public override MultiattackCount Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return MultiattackCount.TryCreate(reader.GetString()!, out var count, out var error)
                    ? count
                    : throw new JsonException(error);

            case JsonTokenType.Number:
                if (!reader.TryGetInt32(out var number))
                {
                    throw new JsonException(DescribeUnusableNumber(ref reader));
                }

                if (number < 0)
                {
                    throw new JsonException($"A multiattack count cannot be negative, but found {number}.");
                }

                return MultiattackCount.FromNumber(number);

            default:
                throw new JsonException(
                    "A multiattack count must be a string such as \"2\", \"Number of Heads\" or \"1d4\", or a whole number, " +
                    $"but found {JsonTokenText.Describe(reader.TokenType)}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, MultiattackCount value, JsonSerializerOptions options)
    {
        if (value.WasJsonNumber && value.Value is int number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteStringValue(value.Text);
        }
    }

    // Says WHY a JSON number is not a usable count. "Must be a whole number" for 99999999999 would send whoever reads
    // the log looking for a fraction that is not there.
    private static string DescribeUnusableNumber(ref Utf8JsonReader reader)
    {
        var raw = RawNumber(ref reader);
        if (!reader.TryGetDecimal(out var value))
        {
            return $"The multiattack count {raw} is out of range.";
        }

        if (value != decimal.Truncate(value))
        {
            return $"A multiattack count must be a whole number, but found {raw}.";
        }

        if (value < 0)
        {
            return $"A multiattack count cannot be negative, but found {raw}.";
        }

        return value > int.MaxValue
            ? $"The multiattack count {raw} is too large."
            : $"A multiattack count must be written as a plain integer, but found {raw}.";
    }

    private static string RawNumber(ref Utf8JsonReader reader) =>
        reader.HasValueSequence
            ? Encoding.UTF8.GetString(reader.ValueSequence)
            : Encoding.UTF8.GetString(reader.ValueSpan);
}
