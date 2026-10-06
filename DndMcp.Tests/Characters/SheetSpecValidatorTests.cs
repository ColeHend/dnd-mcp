using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: every mistake in a <c>sheet</c> is refused before anything is applied, in ONE exception listing up to five
/// problems, each saying where ("classes item 2 (Rogue): …", items counted from 1), what was wrong and what to send; a
/// valid sheet passes.
/// </summary>
public sealed class SheetSpecValidatorTests
{
    private static string Problem(string json) => Assert.Throws<DndInputException>(() => SheetSpecValidator.ThrowIfInvalid(Spec(json))).Message;

    [Theory]
    [InlineData(BelmakorJson)]
    [InlineData(BjornJson)]
    [InlineData("""{}""")]
    [InlineData("""{ "level": 5 }""")]
    [InlineData("""{ "classes": [{ "class": "Dragon Slayer", "subclass": "Amethyst", "level": 8, "hit_die": 10 }] }""")]
    [InlineData("""{ "classes": [{ "class": "artificer", "level": 12, "hit_die": 8 }] }""")]
    [InlineData("""{ "classes": [{ "class": "artificer", "level": 12 }] }""")] // a homebrew hit die is checked against the sheet (SheetUpdate)
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 6, "hit_die": 6 }, { "class": "rogue", "level": 6 }] }""")]
    [InlineData("""{ "slots": [4, 3, 3, 3, 2, 1, 1, 1, 1], "pact": { "level": 5, "max": 4 } }""")]
    [InlineData("""{ "pact": { "max": 0 } }""")]
    [InlineData("""{ "resources": [{ "name": "Rage", "remove": true }] }""")]
    [InlineData("""{ "defenses": { "resist": ["Fire", "cold"], "condition_immune": ["Poisoned"] }, "save_proficiencies": ["Strength", "con"] }""")]
    [InlineData("""{ "player": "", "notes": "" }""")]
    public void ThrowIfInvalid_ValidSheet_Passes(string json)
    {
        SheetSpecValidator.ThrowIfInvalid(Spec(json));
    }

    [Theory]
    [InlineData("""{ "ruleset": "5e" }""", "ruleset \"5e\" is not an edition; give 2014 or 2024.")]
    [InlineData("""{ "classes": [] }""", "classes is empty; give at least one")]
    [InlineData("""{ "classes": [{ "level": 3 }] }""", "classes item 1: class is required: an SRD class (barbarian, bard, cleric, druid, fighter, monk, paladin, ranger, rogue, sorcerer, warlock, wizard) or a homebrew class's name with its hit_die.")]
    [InlineData("""{ "classes": [{ "class": "wizard" }] }""", "classes item 1 (wizard): level is required: the levels in this class, 1-20.")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 0 }] }""", "classes item 1 (wizard): level is 0; it is 1 to 20.")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 6 }, { "class": "Rogue", "level": 0 }] }""", "classes item 2 (Rogue): level is 0; it is 1 to 20.")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 12 }, { "class": "Wizard", "level": 2 }] }""", "classes item 2 (Wizard): is the same class as item 1; give each class once, with its total levels.")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 12 }, { "class": "rogue", "level": 9 }] }""", "classes: the levels add up to 21; a character level is 1 to 20.")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 12, "hit_die": 8 }] }""", "classes item 1 (wizard): hit_die is 8, but the Wizard's hit die is d6; leave hit_die out for an SRD class.")]
    [InlineData("""{ "classes": [{ "class": "homebrew", "level": 1, "hit_die": 7 }] }""", "classes item 1 (homebrew): hit_die is 7; give 4, 6, 8, 10 or 12.")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 1 }], "level": 1 }""", "level is the sum of the class levels: give classes or level, not both.")]
    [InlineData("""{ "level": 21 }""", "level is 21; it is 1 to 20.")]
    [InlineData("""{ "xp": -1 }""", "xp is -1; it is 0 to 100000000.")]
    [InlineData("""{ "abilities": { "dex": 31 } }""", "abilities dex is 31; it is 1 to 30.")]
    [InlineData("""{ "ac": 51 }""", "ac is 51; it is 0 to 50.")]
    [InlineData("""{ "max_hp": 0 }""", "max_hp is 0; it is 1 to 5000.")]
    [InlineData("""{ "hp": -1 }""", "hp is -1; it is 0 to 5000.")]
    [InlineData("""{ "temp_hp": -3 }""", "temp_hp is -3; it is 0 to 5000.")]
    [InlineData("""{ "max_hp_reduction": -1 }""", "max_hp_reduction is -1; it is 0 to 5000.")]
    [InlineData("""{ "initiative_bonus": 21 }""", "initiative_bonus is 21; it is -10 to 20.")]
    [InlineData("""{ "spell_save_dc": 0 }""", "spell_save_dc is 0; it is 1 to 40.")]
    [InlineData("""{ "save_proficiencies": ["int", "luck"] }""", "save_proficiencies item 2 \"luck\" is not an ability; give str, dex, con, int, wis or cha.")]
    [InlineData("""{ "save_bonus": { "con": 21 } }""", "save_bonus con is 21; it is -20 to 20.")]
    [InlineData("""{ "defenses": { "resist": ["fire", "firey"] } }""", "defenses resist item 2 \"firey\" is not a damage type; give acid, bludgeoning")]
    [InlineData("""{ "defenses": { "condition_immune": ["sleepy"] } }""", "defenses condition_immune item 1 \"sleepy\" is not a condition; give prone, restrained")]
    [InlineData("""{ "slots": [4, 3, 3, 3, 2, 1, 1, 1, 1, 1] }""", "slots has 10 levels; give at most 9")]
    [InlineData("""{ "slots": [4, 11] }""", "slots item 2 (level 2 slots) is 11; it is 0 to 10.")]
    [InlineData("""{ "pact": { "level": 3 } }""", "pact max is required")]
    [InlineData("""{ "pact": { "max": 2 } }""", "pact level is required")]
    [InlineData("""{ "pact": { "level": 6, "max": 2 } }""", "pact level is 6; it is 1 to 5.")]
    [InlineData("""{ "resources": [{ "max": 4 }] }""", "resources item 1: name is required, e.g. \"Bladesong\".")]
    [InlineData("""{ "resources": [{ "name": "!!!", "max": 4 }] }""", "resources item 1 (!!!): name needs a letter or a digit")]
    [InlineData("""{ "resources": [{ "name": "Rage", "max": 4 }, { "name": "rage", "max": 3 }] }""", "resources item 2 (rage): is the same resource as item 1; give each resource once.")]
    [InlineData("""{ "resources": [{ "name": "Rage", "max": 2, "used": 3 }] }""", "resources item 1 (Rage): used is 3, more than max 2.")]
    [InlineData("""{ "resources": [{ "name": "Rage", "max": 2, "recharge": "weekly" }] }""", "resources item 1 (Rage): recharge \"weekly\" is not a recharge; give short_rest, short_rest_one, long_rest, dawn or none.")]
    [InlineData("""{ "resources": [{ "name": "Contingency", "max": 1, "state": "set" }] }""", "resources item 1 (Contingency): a resource is counted (max, used, recharge) or a tracker (state), not both.")]
    [InlineData("""{ "resources": [{ "name": "Rage", "remove": true, "max": 1 }] }""", "resources item 1 (Rage): remove takes only the name")]
    [InlineData("""{ "feats": ["Tough", ""] }""", "feats item 2 must be a name of one line")]
    [InlineData("""{ "player": "line one\nline two" }""", "player must be one line of at most 80 characters.")]
    public void ThrowIfInvalid_BadField_IsRefusedWithWhereAndWhat(string json, string expected)
    {
        Assert.Contains(expected, Problem(json), StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_OneProblem_IsOneLine()
    {
        Assert.Equal("Invalid sheet: ac is 51; it is 0 to 50.", Problem("""{ "ac": 51 }"""));
    }

    [Fact]
    public void ThrowIfInvalid_SevenProblems_ListsFiveAndCountsTheRest()
    {
        var message = Problem("""{ "ac": 51, "max_hp": 0, "hp": -1, "temp_hp": -1, "speed": -1, "spell_save_dc": 0, "spell_attack": 31 }""");

        Assert.StartsWith("Invalid sheet (7 problems):\n- ac is 51", message, StringComparison.Ordinal);
        Assert.Equal(5, message.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal) && !l.StartsWith("- …", StringComparison.Ordinal)));
        Assert.EndsWith("- … and 2 more; fix these and send it again to see them.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_LongPastedText_IsEchoedCut()
    {
        var message = Problem($$"""{ "classes": [{ "class": "{{new string('x', 200)}}", "level": 1, "hit_die": 6 }] }""");

        Assert.Contains($"classes item 1 ({new string('x', 60)}…): class must be one line", message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 61), message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowIfInvalid_NullItems_AreNamed()
    {
        var message = Problem("""{ "classes": [null], "resources": [null] }""");

        Assert.Contains("classes item 1: is null", message, StringComparison.Ordinal);
        Assert.Contains("resources item 1: is null", message, StringComparison.Ordinal);
    }
}
