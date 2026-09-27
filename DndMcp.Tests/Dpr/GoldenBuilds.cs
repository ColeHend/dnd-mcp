namespace DndMcp.Tests.Dpr;

/// <summary>
/// The contract §8 worked-example builds as DSL JSON, shared by the horizon, curve and comparison tests so each golden is
/// reached through the same build text the model would send. Modifier arguments are list ITEMS (no brackets), so a test
/// composes them: <c>Fighter2014(Gwm2014 + ", " + ActionSurge)</c>.
/// </summary>
internal static class GoldenBuilds
{
    /// <summary>2014 Great Weapon Master: −5/+10 (auto) and the bonus attack on a melee crit.</summary>
    public const string Gwm2014 =
        """{ "kind": "power_attack", "name": "Great Weapon Master" }, { "kind": "extra_attack", "name": "GWM bonus attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }""";

    /// <summary>2024 Great Weapon Master: +PB on Attack-action hits, and Hew on a crit.</summary>
    public const string Gwm2024 =
        """{ "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true }, { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" }""";

    /// <summary>Action Surge: a second Attack action's two swings, once per short rest.</summary>
    public const string ActionSurge =
        """{ "kind": "extra_attack", "name": "Action Surge", "attack": "Greatsword", "count": 2, "action": "action", "resource": { "uses": 1, "per": "short_rest" } }""";

    public const string SavageAttacker = """{ "kind": "reroll_damage_take_best", "name": "Savage Attacker" }""";

    /// <summary>§8.2: 2014 level 5 fighter, Str 18, greatsword 2d6 ×2, GWF 2014 style.</summary>
    public static string Fighter2014(string modifiers = "", int strength = 18, string style = "\"gwf\"") =>
        $$"""
        { "name": "2014 L5 Fighter", "edition": "2014", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": {{style}},
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }],
          "modifiers": [{{modifiers}}] }
        """;

    /// <summary>§8.3: 2024 level 5 fighter, Str 19, greatsword 2d6 ×2 with Graze, GWF 2024 style.</summary>
    public static string Fighter2024(string modifiers = "", int strength = 19, string style = "\"gwf\"") =>
        $$"""
        { "name": "2024 L5 Fighter", "edition": "2024", "level": 5, "abilities": {"str": {{strength}}}, "fighting_style": {{style}},
          "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": "graze" }],
          "modifiers": [{{modifiers}}] }
        """;
}
