using DndMcp.Domain.Characters;
using DndMcp.Domain.Features;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Sheets for the character tests, written as the model sends them (<c>campaign_character update {sheet}</c> JSON, bound
/// with <see cref="DslJson.Options"/> as the host binds it) and created through <see cref="SheetUpdate"/>, so every test
/// starts from a sheet the real path made. Belmakor and Björn are the exit-criteria fixtures' sheets (FIX §1, §3).
/// </summary>
internal static class CharacterSheets
{
    public const string E2014 = DslValues.Editions.E2014;
    public const string E2024 = DslValues.Editions.E2024;

    /// <summary>FIX §1 / §2.1: Belmakor Silverwind, 2014 Bladesinger 12, with the invented fixture values.</summary>
    public const string BelmakorJson = """
        { "player": "Cole", "ruleset": "2014", "species": "High Elf", "background": "Noble",
          "classes": [{ "class": "Wizard", "subclass": "Bladesinger", "level": 12 }],
          "abilities": { "str": 11, "dex": 20, "con": 16, "int": 20, "wis": 13, "cha": 12 },
          "ac": 17, "max_hp": 110, "temp_hp": 7, "initiative_bonus": 5, "spell_save_dc": 17, "spell_attack": 9,
          "save_proficiencies": ["int", "wis", "con"],
          "resources": [
            { "name": "Bladesong", "max": 4, "recharge": "long_rest" },
            { "name": "Arcane Recovery", "max": 1, "recharge": "long_rest" },
            { "name": "Contingency", "state": "set", "note": "Polymorph (T-rex) at low HP" } ],
          "feats": ["War Caster", "Resilient (Constitution)", "Fey Touched", "Tough"],
          "sheet_source": "fixture" }
        """;

    /// <summary>FIX §3: Björn Mountainfell, 2024 Barbarian 8 (the HP 85 = (12 + 3) + 7 × (7 + 3) is derived).</summary>
    public const string BjornJson = """
        { "ruleset": "2024", "classes": [{ "class": "barbarian", "subclass": "Path of the Totem Warrior", "level": 8 }],
          "abilities": { "str": 18, "dex": 14, "con": 16 }, "ac": 15, "initiative_bonus": 2, "xp": 34000,
          "resources": [{ "name": "Rage", "max": 4, "recharge": "short_rest_one" }] }
        """;

    public static SheetSpec Spec(string json) => DslJson.Deserialize<SheetSpec>(json, "sheet");

    /// <summary>A new sheet for entity "e1" from spec JSON, in a campaign of <paramref name="edition"/>.</summary>
    public static CharacterSheet Create(string json, string edition = E2014, BuildSpec? simProfile = null) =>
        SheetUpdate.Apply(null, Spec(json), simProfile, edition, "e1").Sheet;

    public static CharacterSheet Update(CharacterSheet sheet, string json, string edition = E2014) =>
        SheetUpdate.Apply(sheet, Spec(json), null, edition).Sheet;

    public static CharacterSheet Belmakor() => Create(BelmakorJson);

    public static CharacterSheet Bjorn() => Create(BjornJson, E2024);

    /// <summary>A sheet with only classes and a level (fixture A's minimal sheets).</summary>
    public static CharacterSheet Minimal(string classesJson, string edition = E2014) => Create($$"""{ "classes": {{classesJson}} }""", edition);

    /// <summary>The column as stored, as text (JSON for JSON columns).</summary>
    public static string? Text(CharacterSheet sheet, string column) => SheetJson.Column(sheet, column) switch
    {
        null => null,
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        var value => (string)value,
    };
}
