using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DndMcp.Domain.Dice;

/// <summary>
/// The <c>dice_roll.detail</c> JSON of one roll (contract §3.11; <c>understand-change-sites.md</c> §1.4): enough to
/// re-render the roll exactly with the host's <c>DiceRollMarkdown</c> after re-parsing the row's <c>expression</c>.
///
/// <para>
/// <b>Why it is in Domain.</b> Two layers log rolls: the host's <c>dice_roll</c> tool, and the combat tracker in the
/// Repository, which rolls initiative, damage and saves inside its own transaction (Phase 7 D11). Both must write this one
/// shape, or a session's roll log would read two ways; the Repository cannot see the host, so the shape lives here, pure
/// (it only formats a <see cref="DiceRoll"/>). Its output is byte-for-byte what the host wrote before the move: rows
/// already stored and the tests that pin them depend on it.
/// </para>
/// <para>
/// <b>Facts, never enums.</b> Only the rolled facts are stored (each group's term text, label and value; each die's faces,
/// raw and value, and the flags that are true); the rules (keep, reroll, explode, the comparison) come back from parsing
/// the stored expression text. A stored <see cref="DiceComparison"/> or <see cref="ExplodeKind"/> number would silently
/// change meaning the day an enum member is added. <c>groups[i]</c> pairs with <c>DiceExpression.Parse(expression).Groups[i]</c>
/// (evaluation order); <c>term</c> is the group's lower-cased text, a drift check for a reader.
/// </para>
/// <para>
/// <b>Bounded.</b> Every face is stored up to <see cref="MaxFacesStored"/> physical faces per roll. Past that (a 1000d6!
/// that exploded, a 1000d2r1) the groups keep their values only and <c>faces_omitted</c> counts the faces left out:
/// storing every face of a 100-roll call of exploding dice would put megabytes into one call's rows, in the database
/// every backup copies.
/// </para>
/// </summary>
public static class DiceLogDetail
{
    /// <summary>The detail shape's version, so a later reader can tell this shape from a changed one.</summary>
    public const int Version = 1;

    /// <summary>Physical faces stored per roll: the grammar's dice cap, so any roll without explosions or rerolls keeps them all.</summary>
    public const int MaxFacesStored = DiceLimits.MaxDice;

    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Roll <paramref name="index"/> (0-based) of a call of <paramref name="count"/> rolls, as a JSON object.</summary>
    public static string Json(DiceRoll roll, int index, int count, string source)
    {
        var faces = roll.Groups.Sum(g => g.Dice.Sum(d => (long)d.Faces.Count));
        var withFaces = faces <= MaxFacesStored;
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Options))
        {
            json.WriteStartObject();
            json.WriteNumber("v", Version);
            json.WriteString("source", source);
            json.WriteStartObject("call");
            json.WriteNumber("roll", index + 1);
            json.WriteNumber("of", count);
            json.WriteEndObject();
            if (!withFaces)
            {
                json.WriteNumber("faces_omitted", faces);
            }

            json.WriteStartArray("groups");
            foreach (var group in roll.Groups)
            {
                json.WriteStartObject();
                json.WriteString("term", group.Group.Text);
                json.WriteString("label", group.Group.Label);
                json.WriteNumber("value", group.Value);
                if (withFaces)
                {
                    json.WriteStartArray("dice");
                    foreach (var die in group.Dice)
                    {
                        WriteDie(json, die);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteDie(Utf8JsonWriter json, RolledDie die)
    {
        json.WriteStartObject();
        json.WriteStartArray("faces");
        foreach (var face in die.Faces)
        {
            json.WriteStartObject();
            json.WriteNumber("face", face.Face);
            if (face.Rerolled)
            {
                json.WriteBoolean("rerolled", true);
            }

            if (face.Exploded)
            {
                json.WriteBoolean("exploded", true);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteNumber("raw", die.Raw);
        json.WriteNumber("value", die.Value);
        if (die.Exploded)
        {
            json.WriteBoolean("exploded", true);
        }

        if (die.Penetrated)
        {
            json.WriteBoolean("penetrated", true);
        }

        if (die.Dropped)
        {
            json.WriteBoolean("dropped", true);
        }

        if (die.Score != 0)
        {
            json.WriteNumber("score", die.Score);
        }

        json.WriteEndObject();
    }
}
