using DndMcp.Domain.Features;

namespace DndMcp.Tests.Features;

/// <summary>
/// The contract's example builds as DSL JSON, parsed exactly as stored DSL is (<see cref="DslJson"/>). Shared by the
/// feature tests and meant for the DPR tests too, so the golden builds (contract §8) are written down once, in the shape
/// the model sends them.
/// </summary>
public static class FeatureTestBuilds
{
    /// <summary>PLAN.md's feature-DSL example, verbatim apart from the level-5 count.</summary>
    public const string EmberEdgeJson = """
        { "name": "Ember Edge (homebrew)", "edition": "2024", "level": 5,
          "abilities": {"str": 18}, "fighting_style": "gwf",
          "attacks": [{ "name": "Greatsword", "count": 2, "to_hit": {"ability": "str", "proficient": true},
                        "damage": "2d6", "ability_to_damage": true, "properties": ["heavy","two-handed"], "mastery": "graze" }],
          "modifiers": [
            { "kind": "extra_damage", "dice": "1d6", "type": "fire", "when": "first_hit_per_turn", "crit_doubles": true,
              "resource": { "uses": 3, "per": "long_rest" } } ] }
        """;

    /// <summary>
    /// Golden 2: 2014 L5 Fighter, Str 18, greatsword ×2 (Extra Attack at 5), GWF 2014, Great Weapon Master as a power
    /// attack plus the crit bonus attack. At AC 15: 19.611625 DPR.
    /// </summary>
    public const string Fighter2014GwmJson = """
        { "name": "2014 L5 Fighter, GWM", "edition": "2014", "level": 5,
          "abilities": {"str": 18}, "fighting_style": "gwf",
          "attacks": [{ "name": "Greatsword", "count": {"1": 1, "5": 2}, "damage": "2d6", "damage_type": "slashing",
                        "properties": ["melee", "heavy", "two-handed"] }],
          "modifiers": [
            { "kind": "power_attack", "name": "Great Weapon Master" },
            { "kind": "extra_attack", "name": "GWM bonus attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" } ] }
        """;

    /// <summary>
    /// Golden 3: 2024 L5 Fighter, Str 19, greatsword ×2 with Graze, GWF 2024, Great Weapon Master (+PB on Attack-action
    /// hits) and Hew. At AC 15: 24.036 DPR (24.226125 with hew_gets_pb).
    /// </summary>
    public const string Fighter2024GwmJson = """
        { "name": "2024 L5 Fighter, GWM", "edition": "2024", "level": 5,
          "abilities": {"str": 19}, "fighting_style": "gwf",
          "attacks": [{ "name": "Greatsword", "count": {"1": 1, "5": 2}, "damage": "2d6", "damage_type": "slashing",
                        "properties": ["melee", "heavy", "two-handed"], "mastery": "graze" }],
          "modifiers": [
            { "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true },
            { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" } ] }
        """;

    /// <summary>Golden 7's smite setup: +7 vs AC 15, two attacks, a 2d8 radiant rider once per turn (2024 Divine Smite).</summary>
    public const string PaladinSmiteJson = """
        { "name": "L5 Paladin, smite", "edition": "2024", "level": 5,
          "abilities": {"str": 18},
          "attacks": [{ "name": "Longsword", "count": 2, "damage": "1d8", "damage_type": "slashing", "properties": ["melee", "versatile"] }],
          "modifiers": [
            { "kind": "extra_damage", "name": "Divine Smite", "dice": "2d8", "type": "radiant", "when": "first_hit_per_turn",
              "policy": "crit_or_last", "action_cost": "bonus_action", "resource": { "uses": 4, "per": "long_rest" } } ] }
        """;

    /// <summary>Golden 8: Fireball, DC 15 (given), 8d6 fire, a 20-ft sphere (4 targets by the DMG table).</summary>
    public const string FireballJson = """
        { "name": "Fireball", "edition": "2014", "level": 5,
          "modifiers": [
            { "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire",
              "shape": "sphere", "size": 20, "resource": { "uses": 2, "per": "long_rest" } } ] }
        """;

    public static BuildSpec Build(string json) => DslJson.Deserialize<BuildSpec>(json, "build");

    public static TargetSpec Target(string json) => DslJson.Deserialize<TargetSpec>(json, "target");

    public static FeatureSpec Feature(string json) => DslJson.Deserialize<FeatureSpec>(json, "feature");

    /// <summary>A minimal valid build with the given attack and modifier JSON spliced in (for one-field error tests).</summary>
    public static BuildSpec With(string attacksJson = DefaultAttacks, string modifiersJson = "[]", string extra = "") =>
        Build($$"""{ "name": "Test", "level": 5, {{extra}} "attacks": {{attacksJson}}, "modifiers": {{modifiersJson}} }""");

    /// <summary>One modifier on the default attacks.</summary>
    public static BuildSpec WithModifier(string modifierJson, string extra = "") => With(modifiersJson: $"[{modifierJson}]", extra: extra);

    /// <summary>A Str greatsword and a Dex longbow.</summary>
    public const string DefaultAttacks = """
        [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] },
         { "name": "Longbow", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["ranged", "heavy", "two-handed"] }]
        """;
}
