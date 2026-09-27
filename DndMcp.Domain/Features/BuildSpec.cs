using System.ComponentModel;

namespace DndMcp.Domain.Features;

/// <summary>
/// A character's damage routine in the feature DSL: the <c>build</c> argument of <c>balance_dpr</c>, the baseline and
/// variant of <c>balance_compare</c>, and later (Phase 7) the stored <c>character_sheet.sim_profile</c>.
///
/// <para>
/// <b>These classes ARE the tool's input schema</b>: the MCP SDK builds the schema the model reads from the properties and
/// their <see cref="DescriptionAttribute"/>s, and binds the model's JSON straight into them with the host's snake_case
/// options. So every property is a public <c>init</c> property, optional in the schema and nullable, and the descriptions
/// are written for the model (short, with the accepted values and a tiny example). Which fields are required, and every
/// range, is checked by <see cref="BuildSpecValidator"/> with a message that says what to send; the binder would only say
/// "an error occurred". Defaults are applied by the resolver, never here, so "not given" stays distinguishable from a
/// value that happens to equal the default (a modifier kind that does not take a field is refused only if it was given).
/// </para>
/// <para>
/// Step values (a scalar or <c>{"1": 1, "5": 2}</c> by level) are typed <c>object?</c>: they bind as a
/// <see cref="System.Text.Json.JsonElement"/>, as <c>encounter_difficulty</c>'s <c>cr</c> does, and
/// <see cref="LevelValue"/> parses them.
/// </para>
/// </summary>
public sealed class BuildSpec
{
    [Description("Required. A label for the build, one line, at most 80 characters, e.g. \"L5 Fighter, GWM\".")]
    public string? Name { get; init; }

    [Description(
        "A ready-made build instead of abilities, attacks and modifiers: \"warlock_baseline\" (Eldritch Blast + Agonizing " +
        "Blast + Hex, Cha 16/18/20). With a preset give only name, edition and level.")]
    public string? Preset { get; init; }

    [Description("\"2024\" (default) or \"2014\": the rules the build follows (fighting style sugar, notes).")]
    public string? Edition { get; init; }

    [Description("Required. The character level the build describes, 1-20; step values are read at each level evaluated.")]
    public int? Level { get; init; }

    [Description(
        "Ability scores 1-30; any not given is 10. Each is a number or a step map by level: " +
        "{\"str\": 18} or {\"cha\": {\"1\": 16, \"4\": 18, \"8\": 20}}.")]
    public AbilitiesSpec? Abilities { get; init; }

    [Description("Overrides the proficiency bonus, 2-9. Default: by level (+2 at 1-4 ... +6 at 17-20).")]
    public int? ProficiencyBonus { get; init; }

    [Description(
        "\"gwf\" (Great Weapon Fighting on two-handed or versatile melee weapons: 2014 reroll 1-2, 2024 1-2 count as 3), " +
        "\"archery\" (+2 to hit, ranged weapons), \"dueling\" (+2 damage, one-handed melee weapons), \"twf\" (offhand " +
        "attacks add the ability modifier).")]
    public string? FightingStyle { get; init; }

    [Description(
        "The attacks, at most 10, e.g. [{\"name\": \"Greatsword\", \"count\": {\"1\": 1, \"5\": 2}, \"damage\": \"2d6\", " +
        "\"damage_type\": \"slashing\", \"properties\": [\"melee\", \"heavy\", \"two-handed\"]}]. to_hit defaults to Str + " +
        "proficiency; the ability modifier is added to damage.")]
    public IReadOnlyList<AttackSpec>? Attacks { get; init; }

    [Description(
        "Features, at most 20, each with a kind, e.g. [{\"kind\": \"extra_damage\", \"name\": \"Hex\", \"dice\": \"1d6\", \"type\": " +
        "\"necrotic\"}]. A field the kind does not take is refused, and the message lists the ones it takes.")]
    public IReadOnlyList<ModifierSpec>? Modifiers { get; init; }
}

/// <summary>
/// Ability scores, each a step value of an integer 1–30. Named <c>str</c> … <c>cha</c> on the wire (the snake_case
/// policy lower-cases the C# names), matching the 5e data and the DSL's ability keys.
/// </summary>
public sealed class AbilitiesSpec
{
    [Description("Strength 1-30, or a step map by level such as {\"1\": 16, \"4\": 18}. Default 10.")]
    public object? Str { get; init; }

    [Description("Dexterity 1-30 or a step map. Default 10.")]
    public object? Dex { get; init; }

    [Description("Constitution 1-30 or a step map. Default 10.")]
    public object? Con { get; init; }

    [Description("Intelligence 1-30 or a step map. Default 10.")]
    public object? Int { get; init; }

    [Description("Wisdom 1-30 or a step map. Default 10.")]
    public object? Wis { get; init; }

    [Description("Charisma 1-30 or a step map. Default 10.")]
    public object? Cha { get; init; }

    /// <summary>The raw value for an ability key, as <see cref="DslValues.Abilities"/> names them.</summary>
    public object? Get(string ability) => ability switch
    {
        DslValues.Abilities.Str => Str,
        DslValues.Abilities.Dex => Dex,
        DslValues.Abilities.Con => Con,
        DslValues.Abilities.Int => Int,
        DslValues.Abilities.Wis => Wis,
        DslValues.Abilities.Cha => Cha,
        _ => throw new ArgumentOutOfRangeException(nameof(ability), ability, "Not an ability key."),
    };
}
