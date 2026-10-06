using System.Text.Json;
using System.Text.Json.Serialization;

namespace DndMcp.Domain.Features;

/// <summary>
/// JSON for a <see cref="DamageFormula"/>: <c>{"dice":[[2,6],[1,4,true]],"flat":3}</c>, each die term
/// <c>[count, sides]</c> or <c>[count, sides, true]</c> for a subtracted term, read back through
/// <see cref="DamageFormula.Of"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> A stat block snapshot (the combat tracker's <c>combatant.statblock</c>) stores every formula of
/// the stat block: hit dice, every damage roll, traits and heals. Without a converter a formula serialises its computed
/// members (<c>text</c>, <c>dice_count</c>, <c>has_dice</c>) and cannot be read back at all (its constructor is
/// private), so a stored snapshot would never load.
/// </para>
/// <para>
/// <b>Why the object form and not the text.</b> Reading "33d20+330" back would need a parser, and the DSL's
/// (<see cref="DamageFormula.ParseDamage"/>) refuses the tarrasque's hit dice by its 50-dice and 100-flat limits, which
/// exist for what a user types, not for what the normalizer wrote. <see cref="DamageFormula.Of"/> takes the terms as
/// they are, with no limits, and merges them into the same canonical form, so a round trip is exact (equality is by
/// <see cref="DamageFormula.Text"/>).
/// </para>
/// <para>
/// It is attached to the type, so it applies under any options; no published schema contains a <see cref="DamageFormula"/>
/// (the DSL's dice are strings in <c>object?</c> fields).
/// </para>
/// </remarks>
public sealed class DamageFormulaJsonConverter : JsonConverter<DamageFormula>
{
    public override DamageFormula Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A damage formula is an object {\"dice\": [[count, sides]], \"flat\": n}.");
        }

        var dice = new List<DiceTerm>();
        var flat = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A damage formula is an object {\"dice\": [[count, sides]], \"flat\": n}.");
            }

            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "dice":
                    ReadDice(ref reader, dice);
                    break;
                case "flat":
                    flat = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)
                        ? value
                        : throw new JsonException("A damage formula's flat part is a whole number.");
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return DamageFormula.Of(dice, flat);
    }

    public override void Write(Utf8JsonWriter writer, DamageFormula value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("dice");
        foreach (var term in value.Dice)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(term.Count);
            writer.WriteNumberValue(term.Sides);
            if (term.Negative)
            {
                writer.WriteBooleanValue(true);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteNumber("flat", value.Flat);
        writer.WriteEndObject();
    }

    private static void ReadDice(ref Utf8JsonReader reader, List<DiceTerm> dice)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("A damage formula's dice are an array of [count, sides] terms.");
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("A die term is [count, sides] or [count, sides, true].");
            }

            var count = ReadPositive(ref reader, "count");
            var sides = ReadPositive(ref reader, "sides");
            reader.Read();
            var negative = false;
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
            {
                negative = reader.GetBoolean();
                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndArray)
            {
                throw new JsonException("A die term is [count, sides] or [count, sides, true].");
            }

            dice.Add(new DiceTerm(count, sides, negative));
        }
    }

    private static int ReadPositive(ref Utf8JsonReader reader, string what)
    {
        reader.Read();
        return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value >= 1
            ? value
            : throw new JsonException($"A die term's {what} is a whole number of at least 1.");
    }
}
