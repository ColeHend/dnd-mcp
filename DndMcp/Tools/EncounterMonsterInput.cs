using System.ComponentModel;

namespace DndMcp.Tools;

/// <summary>
/// One <c>monsters</c> entry of <c>encounter_difficulty</c>, as the model sends it (snake_case through
/// <c>McpJson.Options</c>). Every property is optional in the schema: which combination is valid (a ref, a name, or a
/// cr with an optional name as its label) is checked by the tool, with a message that says which to give; the SDK's
/// binder would only say "an error occurred".
///
/// <para>
/// <c>cr</c> is untyped in the schema (an <c>object</c>, which binds as a <c>JsonElement</c>) so that "1/2", "5", 0.5 and 5
/// all work: a model sends whichever it thinks of first, and a type error there would cost a round trip for nothing.
/// The tool parses it (<see cref="Domain.Encounters.ChallengeRating.Parse"/>) and says what a CR may be when it is not one.
/// </para>
/// </summary>
public sealed class EncounterMonsterInput
{
    [Description("An SRD monster's ref, e.g. \"2014/monster/ogre\" (from rules_search or rules_get).")]
    public string? Ref { get; init; }

    [Description("An SRD monster's name, e.g. \"Ogre\". With cr, just a label for a monster not in the SRD.")]
    public string? Name { get; init; }

    [Description("Challenge rating for a monster not in the SRD: \"1/2\", \"5\" (or the number 0.5, 5).")]
    public object? Cr { get; init; }

    [Description("How many of this monster, 1-1000. Default 1.")]
    public int Count { get; init; } = 1;

    [Description("2014 only: leave it out of the monster count for the group multiplier (its CR is far below the others'). Default false.")]
    public bool Exclude { get; init; }

    [Description("Fought in its lair: use the 2024 stat block's in-lair XP (with cr, the next CR's XP). Default false.")]
    public bool Lair { get; init; }
}
