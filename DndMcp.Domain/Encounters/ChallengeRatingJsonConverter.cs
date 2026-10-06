using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Encounters;

/// <summary>
/// JSON for a <see cref="ChallengeRating"/>: the string the SRD prints ("1/8", "1/2", "5"), read back with
/// <see cref="ChallengeRating.Parse"/> (a number such as 0.125 is read too, through <see cref="ChallengeRating.FromNumber"/>).
/// </summary>
/// <remarks>
/// Without it a CR serialises its computed members, including the recursive <c>next</c> (a CR 0 is a chain of 34 nested
/// objects), and reads back silently as CR 0: its constructor is private and <c>Eighths</c> has no setter. A stored stat
/// block snapshot would then award a mummy lord's XP as a CR 0's. Attached to the type, so it applies under any options;
/// no published schema contains a <see cref="ChallengeRating"/> (the tools take <c>cr</c> as <c>object?</c>).
/// </remarks>
public sealed class ChallengeRatingJsonConverter : JsonConverter<ChallengeRating>
{
    public override ChallengeRating Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                try
                {
                    return ChallengeRating.Parse(reader.GetString());
                }
                catch (DndInputException ex)
                {
                    throw new JsonException(ex.Message, ex);
                }

            case JsonTokenType.Number when reader.TryGetDouble(out var number) && ChallengeRating.FromNumber(number) is { } cr:
                return cr;
            default:
                throw new JsonException("A Challenge Rating is a string such as \"1/8\" or \"5\".");
        }
    }

    public override void Write(Utf8JsonWriter writer, ChallengeRating value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
