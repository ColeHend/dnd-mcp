using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: every level-independent mistake in a build is refused with ONE <see cref="DndInputException"/> whose lines
/// say where ("attacks item 2 (Greatsword): …", counted from 1), what was wrong and what is accepted — and nothing valid
/// is refused. The model reads these messages verbatim to fix its next call.
/// </summary>
public sealed class BuildSpecValidationTests
{
    private static string Problem(BuildSpec spec) => Assert.Throws<DndInputException>(() => BuildResolver.Validate(spec)).Message;

    private static string Problem(string json) => Problem(Build(json));

    [Fact]
    public void Validate_ContractVocabulary_RefusedFieldNamesItemKindAndTheFieldsItTakes()
    {
        var message = Problem(WithModifier("""{ "kind": "to_hit", "amount": 1, "type": "fire" }"""));

        Assert.Equal(
            "Invalid build: modifiers item 1 (to_hit): does not take \"type\"; to_hit takes amount, dice, attacks, name, from_level, " +
            "until_level, concentration, setup.",
            message);
    }

    [Fact]
    public void Validate_ContractVocabulary_BadDamageNamesTheAttackAndWhatToWrite()
    {
        var message = Problem(With("""[{ "name": "Greatsword", "damage": "2d6" }, { "name": "Greataxe", "damage": "2d6kh1" }]"""));

        Assert.Equal(
            "Invalid build: attacks item 2 (Greataxe): damage \"2d6kh1\" keeps or drops dice (\"2d6kh1\"); damage is plain dice and " +
            "whole numbers joined by + or -, e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\".",
            message);
    }

    [Theory]
    [InlineData("""{ "level": 5, "attacks": [{ "name": "A", "damage": "1d8" }] }""", "name is required: a label such as \"L5 Fighter, GWM\".")]
    [InlineData("""{ "name": "  ", "level": 5, "attacks": [{ "name": "A", "damage": "1d8" }] }""", "name is required")]
    [InlineData("""{ "name": "a\nb", "level": 5, "attacks": [{ "name": "A", "damage": "1d8" }] }""", "name must be one line of at most 80 characters (got \"a\\nb\").")]
    [InlineData("""{ "name": "X", "attacks": [{ "name": "A", "damage": "1d8" }] }""", "level is required: the character level 1-20 the build describes, e.g. \"level\": 5.")]
    [InlineData("""{ "name": "X", "level": 0, "attacks": [{ "name": "A", "damage": "1d8" }] }""", "level is 0; it is 1 to 20.")]
    [InlineData("""{ "name": "X", "level": 21, "attacks": [{ "name": "A", "damage": "1d8" }] }""", "level is 21; it is 1 to 20.")]
    [InlineData("""{ "name": "X", "level": 5, "edition": "2020", "attacks": [{ "name": "A", "damage": "1d8" }] }""", "edition \"2020\" is not an edition; give 2014 or 2024.")]
    [InlineData("""{ "name": "X", "level": 5, "proficiency_bonus": 1, "attacks": [{ "name": "A", "damage": "1d8" }] }""", "proficiency_bonus is 1; it is 2 to 9 (leave it out to use the level's).")]
    [InlineData("""{ "name": "X", "level": 5, "proficiency_bonus": 10, "attacks": [{ "name": "A", "damage": "1d8" }] }""", "proficiency_bonus is 10; it is 2 to 9")]
    [InlineData("""{ "name": "X", "level": 5, "fighting_style": "defense", "attacks": [{ "name": "A", "damage": "1d8" }] }""", "fighting_style \"defense\" is not a fighting style; give gwf, archery, dueling or twf.")]
    [InlineData("""{ "name": "X", "level": 5, "preset": "fighter_baseline" }""", "preset \"fighter_baseline\" is not a preset; give \"warlock_baseline\".")]
    [InlineData("""{ "name": "X", "level": 5, "preset": "warlock_baseline", "attacks": [], "fighting_style": "gwf" }""", "with preset \"warlock_baseline\" give only name, edition and level; remove fighting_style, attacks.")]
    [InlineData("""{ "name": "X", "level": 5 }""", "a build needs at least one attack (or a save_effect modifier)")]
    [InlineData("""{ "name": "X", "level": 5, "attacks": [], "modifiers": [] }""", "a build needs at least one attack")]
    public void Validate_BadBuildField_IsRefusedWithWhy(string json, string why)
    {
        Assert.Contains(why, Problem(json), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_TooManyItems_IsRefused()
    {
        var attacks = "[" + string.Join(",", Enumerable.Range(1, 11).Select(i => $$"""{ "name": "A{{i}}", "damage": "1d4" }""")) + "]";
        var modifiers = "[" + string.Join(",", Enumerable.Range(1, 21).Select(_ => """{ "kind": "lucky" }""")) + "]";

        var message = Problem(With(attacks, modifiers));

        Assert.Contains("attacks has 11 items; at most 10 are accepted.", message, StringComparison.Ordinal);
        Assert.Contains("modifiers has 21 items; at most 20 are accepted.", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "str": 31 }""", "abilities str is 31; it is 1 to 30.")]
    [InlineData("""{ "dex": 0 }""", "abilities dex is 0; it is 1 to 30.")]
    [InlineData("""{ "con": "strong" }""", "abilities con must be a whole number 1 to 30, or a step map by level such as {\"1\": 16, \"4\": 18, \"8\": 20}, but was the string \"strong\".")]
    [InlineData("""{ "int": 12.5 }""", "abilities int must be a whole number 1 to 30")]
    [InlineData("""{ "wis": [16] }""", "but was an array.")]
    [InlineData("""{ "cha": {"0": 16} }""", "abilities cha: step map key \"0\" is not a level; keys are levels 1 to 20 written as whole numbers, e.g. {\"1\": 16, \"4\": 18, \"8\": 20}.")]
    [InlineData("""{ "cha": {"01": 16} }""", "step map key \"01\" is not a level")]
    [InlineData("""{ "cha": {"4.0": 16} }""", "step map key \"4.0\" is not a level")]
    [InlineData("""{ "cha": {"21": 16} }""", "step map key \"21\" is not a level")]
    [InlineData("""{ "cha": {} }""", "abilities cha: step map is empty; give a value, or levels as keys")]
    [InlineData("""{ "cha": {"1": 16, "1": 18} }""", "abilities cha: step map gives level 1 twice")]
    [InlineData("""{ "cha": {"1": 16, "4": 31} }""", "abilities cha at level 4 is 31; it is 1 to 30.")]
    [InlineData("""{ "cha": {"1": 16, "4": {"5": 1}} }""", "abilities cha at level 4 must be a whole number 1 to 30, but was an object.")]
    public void Validate_BadAbility_IsRefusedWithWhy(string abilities, string why)
    {
        Assert.Contains(why, Problem(With(extra: $"\"abilities\": {abilities},")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "str": "18", "dex": {"1": "14", "4": 16} }""")]
    [InlineData("""{ "str": 18, "dex": null }""")]
    public void Validate_AbilityForms_NumericStringsAndNullsAreAccepted(string abilities)
    {
        BuildResolver.Validate(With(extra: $"\"abilities\": {abilities},"));
    }

    [Theory]
    [InlineData("""[null]""", "attacks item 1: is null; give an attack")]
    [InlineData("""[{ "damage": "1d8" }]""", "attacks item 1: name is required: modifiers refer to the attack by it")]
    [InlineData("""[{ "name": "Sword", "damage": "1d8" }, { "name": "SWORD", "damage": "1d6" }]""", "attacks item 2 (SWORD): name \"SWORD\" is also attacks item 1's; names must differ (case is ignored).")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "count": 0 }]""", "attacks item 1 (A): count is 0; it is 1 to 10.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "count": 11 }]""", "attacks item 1 (A): count is 11; it is 1 to 10.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "count": "two" }]""", "count must be a whole number 1 to 10, or a step map by level such as {\"1\": 1, \"5\": 2}, but was the string \"two\".")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "count": {"1": 1, "5": 0} }]""", "count at level 5 is 0; it is 1 to 10.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "action": "reaction" }]""", "action \"reaction\" is not an attack action; give action or bonus_action.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "to_hit": {"ability": "luck"} }]""", "to_hit ability \"luck\" is not an ability; give str, dex, con, int, wis, cha or none.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "to_hit": {"bonus": 21} }]""", "to_hit bonus is 21; it is -20 to 20.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "to_hit": {"total": 31} }]""", "to_hit total is 31; it is -10 to 30.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "to_hit": {"total": -11} }]""", "to_hit total is -11; it is -10 to 30.")]
    [InlineData("""[{ "name": "A" }]""", "attacks item 1 (A): damage is required: dice and whole numbers such as \"2d6\" or \"1d8+1\"")]
    [InlineData("""[{ "name": "A", "damage": "" }]""", "damage is empty; damage is plain dice")]
    [InlineData("""[{ "name": "A", "damage": "1d8-1d4" }]""", "damage \"1d8-1d4\" subtracts dice (-1d4); damage can only add dice.")]
    [InlineData("""[{ "name": "A", "damage": "-1d8" }]""", "subtracts dice (-1d8)")]
    [InlineData("""[{ "name": "A", "damage": "2d6[fire]" }]""", "has a label ([fire]): give the damage type in its own field")]
    [InlineData("""[{ "name": "A", "damage": "2d6+3[magic]" }]""", "has a label ([…]); give the damage type in its own field")]
    [InlineData("""[{ "name": "A", "damage": "(1d6+2)" }]""", "uses parentheses; write the terms out")]
    [InlineData("""[{ "name": "A", "damage": "2d6*2" }]""", "multiplies or divides (* or /); write the dice out, e.g. \"4d6\" (a crit doubles dice by itself)")]
    [InlineData("""[{ "name": "A", "damage": "8d6>=30" }]""", "has a comparison (>=30)")]
    [InlineData("""[{ "name": "A", "damage": "2d6ro<=2" }]""", "rerolls dice (\"2d6ro<=2\"): for Great Weapon Fighting use fighting_style \"gwf\" or a damage_die_remap modifier")]
    [InlineData("""[{ "name": "A", "damage": "1d6!" }]""", "explodes dice (\"1d6!\")")]
    [InlineData("""[{ "name": "A", "damage": "1d6min2" }]""", "clamps dice (\"1d6min2\"): for Elemental Adept use a damage_die_remap modifier")]
    [InlineData("""[{ "name": "A", "damage": "10d10cs>=8" }]""", "counts successes (\"10d10cs>=8\")")]
    [InlineData("""[{ "name": "A", "damage": "adv" }]""", "keeps or drops dice (\"adv\")")]
    [InlineData("""[{ "name": "A", "damage": "30d6+21d6" }]""", "damage \"30d6+21d6\" rolls 51 dice; at most 50 are accepted.")]
    [InlineData("""[{ "name": "A", "damage": "1d120" }]""", "damage \"1d120\" has a d120; dice here have at most 100 sides.")]
    [InlineData("""[{ "name": "A", "damage": "1d6+500" }]""", "damage \"1d6+500\" adds 500; whole numbers here are -100 to 100.")]
    [InlineData("""[{ "name": "A", "damage": "1d6+60+60" }]""", "damage \"1d6+60+60\" adds 120 in all; whole numbers here total -100 to 100.")]
    [InlineData("""[{ "name": "A", "damage": "2d6 +" }]""", "attacks item 1 (A): damage: Could not read the dice expression \"2d6 +\" at the end")]
    [InlineData("""[{ "name": "A", "damage": 101 }]""", "damage is 101; a whole number here is -100 to 100.")]
    [InlineData("""[{ "name": "A", "damage": true }]""", "damage must be dice such as \"2d6\" or \"1d8+1\", or a step map by level such as {\"1\": \"1d8\", \"5\": \"2d8\"}, but was the boolean true.")]
    [InlineData("""[{ "name": "A", "damage": {"1": "1d8", "5": "2d6kh1"} }]""", "damage at level 5 \"2d6kh1\" keeps or drops dice")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "damage_type": "sonic" }]""", "damage_type \"sonic\" is not a damage type; give acid, bludgeoning")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "properties": ["magic"] }]""", "properties has \"magic\", which is not an attack property; they are melee, ranged, spell, heavy, light, finesse, two-handed, versatile, reach, thrown, magical, silvered, adamantine.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "properties": ["melee", "ranged"] }]""", "properties has both melee and ranged; an attack is one or the other")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "mastery": "bleed" }]""", "mastery \"bleed\" is not a weapon mastery; give graze, vex, topple, sap, cleave, nick, push or slow.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "cantrip": "levels" }]""", "cantrip \"levels\" is not a cantrip scaling; give dice or beams.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "from_level": 0 }]""", "from_level is 0; it is 1 to 20.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "until_level": 21 }]""", "until_level is 21; it is 1 to 20.")]
    [InlineData("""[{ "name": "A", "damage": "1d8", "from_level": 10, "until_level": 5 }]""", "from_level 10 is after until_level 5.")]
    public void Validate_BadAttack_IsRefusedWithWhereAndWhy(string attacks, string why)
    {
        Assert.Contains(why, Problem(With(attacks)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""[{ "name": "A", "damage": "2d6+str" }]""", "attacks item 1 (A): damage \"2d6+str\" has \"str\": the ability modifier is added for you (to_hit ability; ability_to_damage, default true)")]
    [InlineData("""[{ "name": "A", "damage": "1d8+PB" }]""", "has \"PB\": the ability modifier is added for you (to_hit ability; ability_to_damage, default true); proficiency bonus is a bonus_damage modifier with amount \"pb\"")]
    [InlineData("""[{ "name": "A", "damage": "1d10 force" }]""", "has \"force\": the ability modifier is added for you (to_hit ability; ability_to_damage, default true); proficiency bonus is a bonus_damage modifier with amount \"pb\"; the damage type goes in damage_type. Damage is plain dice and whole numbers joined by + or -, e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\".")]
    [InlineData("""[{ "name": "A", "damage": {"1": "1d8", "5": "2d6+str"} }]""", "attacks item 1 (A): damage at level 5 \"2d6+str\" has \"str\": the ability modifier is added for you")]
    public void Validate_WordInAttackDamage_SaysWhereItBelongs(string attacks, string why)
    {
        Assert.Contains(why, Problem(With(attacks)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6 fire" }""", "dice \"1d6 fire\" has \"fire\": a flat bonus, \"pb\" or an ability modifier goes in amount; the damage type goes in type.")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6+pb" }""", "dice \"1d6+pb\" has \"pb\": a flat bonus, \"pb\" or an ability modifier goes in amount")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "8d6 fire", "action_cost": "none" }""", "has \"fire\": a flat bonus, \"pb\" or an ability modifier goes in amount; the damage type goes in type.")]
    [InlineData("""{ "kind": "to_hit", "dice": "1d4+pb" }""", "dice \"1d4+pb\" has \"pb\": put a flat bonus in amount. These dice are plain dice joined by + or -")]
    public void Validate_WordInModifierDice_SaysWhereItBelongs(string modifier, string why)
    {
        var message = Problem(WithModifier(modifier));

        Assert.Contains(why, message, StringComparison.Ordinal);
        Assert.DoesNotContain("damage_type", message, StringComparison.Ordinal); // a modifier's type field is "type"
    }

    [Fact]
    public void Validate_DamageSyntaxError_DropsTheDiceGrammarsRefusedExamples()
    {
        // The dice_roll grammar's examples ("4d6kh3", "adv+5", "8d6>=30") are all forms this field refuses: replaced by its own.
        var message = Problem(With("""[{ "name": "A", "damage": "2d6 +" }]"""));

        Assert.Contains("Damage is plain dice and whole numbers joined by + or -, e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\".", message, StringComparison.Ordinal);
        Assert.DoesNotContain("adv+5", message, StringComparison.Ordinal);
        Assert.DoesNotContain("4d6kh3", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "name": "A", "damage": "1d8+1d6-1", "properties": ["Two Handed", "MELEE"], "action": "Bonus-Action", "damage_type": "Fire" }""")]
    [InlineData("""{ "name": "A", "damage": 5 }""")]
    [InlineData("""{ "name": "A", "damage": "1d100+100" }""")]
    [InlineData("""{ "name": "A", "damage": "25d6+25d4" }""")]
    [InlineData("""{ "name": "A", "damage": "1d4 - 1", "to_hit": {"ability": "Strength", "total": 30} }""")]
    [InlineData("""{ "name": "A", "damage": "d6", "count": "2", "properties": [] }""")]
    public void Validate_LenientButValidAttack_IsAccepted(string attack)
    {
        BuildResolver.Validate(With($"[{attack}]"));
    }

    [Theory]
    [InlineData("""null""", "modifiers item 1: is null; give a modifier with a kind")]
    [InlineData("""{ "amount": 1 }""", "modifiers item 1: kind is required; kinds are to_hit, extra_damage, bonus_damage")]
    [InlineData("""{ "kind": "smite" }""", "modifiers item 1: kind \"smite\" is not a modifier kind; kinds are to_hit, extra_damage")]
    [InlineData("""{ "kind": "to_hit", "amount": 1, "type": "fire", "when": "every_hit" }""", "modifiers item 1 (to_hit): does not take \"type\" or \"when\"; to_hit takes")]
    [InlineData("""{ "kind": "to_hit", "amount": 1, "resource": {"uses": 1, "per": "long_rest"} }""", "does not take \"resource\"")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "bonus_action", "attacks": ["Greatsword"] }""", "(extra_attack): does not take \"attacks\"; extra_attack takes attack, count, action")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "attacks": ["Greatsword"], "action_cost": "none" }""", "(save_effect): does not take \"attacks\"")]
    [InlineData("""{ "kind": "lucky", "amount": 1 }""", "modifiers item 1 (lucky): does not take \"amount\"; lucky takes attacks, name, from_level, until_level, concentration, setup.")]
    [InlineData("""{ "kind": "lucky", "name": "" }""", "modifiers item 1 (lucky): name must be one line of 1 to 60 characters.")]
    [InlineData("""{ "kind": "lucky", "attacks": [] }""", "attacks is empty; leave it out to apply to every attack it can, or name attacks.")]
    [InlineData("""{ "kind": "lucky", "attacks": ["Greatsord"] }""", "modifiers item 1 (lucky): attacks names \"Greatsord\", which is not an attack of this build; its attacks are \"Greatsword\", \"Longbow\".")]
    [InlineData("""{ "kind": "lucky", "from_level": 9, "until_level": 3 }""", "from_level 9 is after until_level 3.")]
    [InlineData("""{ "kind": "lucky", "setup": "reaction" }""", "setup \"reaction\" is not a setup; give bonus_action or action.")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6", "resource": {"uses": 2} }""", "resource needs uses (1-20, or a step map by level) and per (\"short_rest\" or \"long_rest\")")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6", "resource": {"uses": 0, "per": "long_rest"} }""", "resource uses is 0; it is 1 to 20.")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6", "resource": {"uses": 1, "per": "dawn"} }""", "resource per \"dawn\" is not a rest; give short_rest or long_rest.")]
    [InlineData("""{ "kind": "to_hit" }""", "to_hit needs amount (e.g. 1) or dice (e.g. \"1d4\" for Bless, \"-1d4\" for Bane).")]
    [InlineData("""{ "kind": "to_hit", "dice": "1d4+1" }""", "dice \"1d4+1\" has a whole number (+1); these are dice only: put a flat bonus in amount.")]
    [InlineData("""{ "kind": "to_hit", "dice": 4 }""", "dice must be dice such as \"1d4\" (Bless) or \"-1d4\" (Bane), but was the number 4.")]
    [InlineData("""{ "kind": "to_hit", "amount": "luck" }""", "amount \"luck\" is not an amount; give a whole number -100 to 100, \"pb\" (proficiency bonus), an ability such as \"cha\" (its modifier)")]
    [InlineData("""{ "kind": "to_hit", "amount": 101 }""", "amount is 101; an amount is -100 to 100.")]
    [InlineData("""{ "kind": "to_hit", "amount": {"1": 1, "5": "pb"} }""", "amount at level 5 must be a whole number -100 to 100, but was the string \"pb\": a step map holds numbers only")]
    [InlineData("""{ "kind": "extra_damage" }""", "extra_damage needs dice (e.g. \"1d6\") or amount (e.g. 2).")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6", "policy": "crits_only" }""", "policy applies only to optional riders (first_hit_per_turn, or every_hit with a resource or action_cost); this every_hit rider is always applied.")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "on_crit", "policy": "any_hit", "crit_doubles": true }""", "an on_crit rider is applied every time it can be, with its dice added once (not doubled), so it takes no policy, crit_doubles; remove them")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "on_miss", "resource": {"uses": 1, "per": "long_rest"} }""", "an on_miss rider is applied every time it can be")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "first_hit_per_turn", "use_value": 9 }""", "use_value is used only with policy \"optimal\"")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "first_hit_per_turn", "policy": "optimal", "use_value": -1 }""", "use_value is -1; it is a number 0 to 1000.")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "first_hit_per_turn", "policy": "greedy" }""", "policy \"greedy\" is not a policy; give any_hit, crits_only, crit_or_last or optimal.")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "sometimes" }""", "when \"sometimes\" is not a when; give every_hit, first_hit_per_turn, on_crit or on_miss.")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "action_cost": "action" }""", "action_cost \"action\" is not an action_cost; give \"bonus_action\".")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "type": "holy" }""", "type \"holy\" is not a damage type")]
    [InlineData("""{ "kind": "bonus_damage" }""", "bonus_damage needs amount: a whole number, \"pb\" or an ability such as \"cha\".")]
    [InlineData("""{ "kind": "crit_range" }""", "crit_range needs min: the lowest d20 roll that crits, 2-20, e.g. 19.")]
    [InlineData("""{ "kind": "crit_range", "min": 1 }""", "min is 1; it is 2 to 20.")]
    [InlineData("""{ "kind": "crit_range", "min": 21 }""", "min is 21; it is 2 to 20.")]
    [InlineData("""{ "kind": "advantage", "rate": 1.5 }""", "rate is 1.5; it is a number 0 to 1.")]
    [InlineData("""{ "kind": "advantage", "mode": "super" }""", "mode \"super\" is not a mode; give advantage or disadvantage.")]
    [InlineData("""{ "kind": "damage_die_remap" }""", "damage_die_remap needs remap: gwf2014, gwf2024 or elemental_adept.")]
    [InlineData("""{ "kind": "damage_die_remap", "remap": "savage" }""", "remap \"savage\" is not a remap; give gwf2014, gwf2024 or elemental_adept.")]
    [InlineData("""{ "kind": "damage_die_remap", "remap": "elemental_adept" }""", "elemental_adept needs type: the damage type it works on, e.g. \"fire\".")]
    [InlineData("""{ "kind": "damage_die_remap", "remap": "gwf2024", "type": "fire" }""", "type is only for elemental_adept; gwf2024 works on the attack's own dice whatever their type.")]
    [InlineData("""{ "kind": "elven_accuracy", "attacks": ["Greatsword"] }""", "Elven Accuracy works only on attack rolls using Dex, Int, Wis or Cha; \"Greatsword\" uses Str.")]
    [InlineData("""{ "kind": "reroll_damage_take_best", "use_value": 2 }""", "use_value is used only with policy \"optimal\"")]
    [InlineData("""{ "kind": "extra_attack", "action": "bonus_action" }""", "extra_attack needs attack: the name of the attack it makes; this build's attacks are \"Greatsword\", \"Longbow\".")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Club", "action": "bonus_action" }""", "attack \"Club\" is not an attack of this build; its attacks are \"Greatsword\", \"Longbow\".")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword" }""", "extra_attack needs action: \"bonus_action\"")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "free" }""", "action \"free\" is not an extra attack action; give action, bonus_action or reaction.")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "action" }""", "an extra_attack with action \"action\" (Action Surge) needs a resource, e.g. {\"uses\": 1, \"per\": \"short_rest\"}; for attacks made every turn, raise the attack's count instead.")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "reaction", "trigger": "crit", "trigger_probability": 0.5 }""", "trigger \"crit\" is only for bonus_action extra attacks; reaction extra attacks take trigger \"always\" (or none).")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "reaction" }""", "a reaction extra_attack needs trigger_probability: the chance per round it happens, 0-1, e.g. 0.3.")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "bonus_action", "trigger_probability": 0.5 }""", "trigger_probability is only for reaction extra attacks; remove it.")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "reaction", "trigger_probability": 2 }""", "trigger_probability is 2; it is a number 0 to 1.")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "bonus_action", "count": 11 }""", "count is 11; it is 1 to 10.")]
    [InlineData("""{ "kind": "power_attack", "penalty": 0 }""", "penalty is 0; it is 1 to 20.")]
    [InlineData("""{ "kind": "power_attack", "bonus": 101 }""", "bonus is 101; it is 1 to 100.")]
    [InlineData("""{ "kind": "power_attack", "policy": "optimal" }""", "policy \"optimal\" is not a power attack policy; give auto, always or never.")]
    [InlineData("""{ "kind": "save_effect", "dc": 13, "dice": "1d6", "action_cost": "none" }""", "ability is required: the saving throw the target makes, e.g. \"dex\".")]
    [InlineData("""{ "kind": "save_effect", "ability": "luck", "dc": 13, "dice": "1d6", "action_cost": "none" }""", "ability \"luck\" is not an ability; give str, dex, con, int, wis or cha.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dice": "1d6", "action_cost": "none" }""", "needs dc (e.g. 15) or dc_ability (e.g. \"cha\" for 8 + proficiency bonus + Cha).")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dc_ability": "int", "dice": "1d6", "action_cost": "none" }""", "give dc or dc_ability, not both.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 41, "dice": "1d6", "action_cost": "none" }""", "dc is 41; it is 1 to 40.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dc_bonus": 1, "dice": "1d6", "action_cost": "none" }""", "dc_bonus adds to a DC from dc_ability; with dc, give the final DC.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc_ability": "int", "dc_bonus": 11, "dice": "1d6", "action_cost": "none" }""", "dc_bonus is 11; it is -10 to 10.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "action_cost": "none" }""", "save_effect does nothing: give dice (e.g. \"8d6\"), amount, or a condition.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "amount": 3, "cantrip": true, "action_cost": "none" }""", "cantrip scales the dice, so it needs dice, e.g. \"1d8\" for Sacred Flame.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "targets": 2, "shape": "cone", "size": 15, "action_cost": "none" }""", "give targets or an area (shape and size), not both.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "shape": "cone", "action_cost": "none" }""", "an area needs both shape and size, e.g. \"shape\": \"sphere\", \"size\": 20 for Fireball.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "shape": "blob", "size": 10, "action_cost": "none" }""", "shape \"blob\" is not a shape; give cone, cube, cylinder, line or sphere.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "shape": "cube", "size": 0, "action_cost": "none" }""", "size is 0; it is 1 to 1000.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "shape": "sphere", "size": 150, "action_cost": "none" }""", "a sphere of 150 ft covers 30 creatures by the DMG's table; at most 20 are modelled. Give targets instead.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "targets": 21, "action_cost": "none" }""", "targets is 21; it is 1 to 20.")]
    [InlineData("""{ "kind": "save_effect", "ability": "wis", "dc": 13, "condition": "confused", "action_cost": "none" }""", "condition \"confused\" is not a condition; give prone, restrained, blinded, stunned, paralyzed, unconscious, charmed")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "action_cost": "reaction" }""", "action_cost \"reaction\" is not an action_cost; give action, bonus_action or none.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "on_success": "quarter", "action_cost": "none" }""", "on_success \"quarter\" is not an on_success; give half or none.")]
    [InlineData("""{ "kind": "condition_on_hit", "ability": "con" }""", "condition_on_hit needs condition: prone, restrained, blinded, stunned, paralyzed or unconscious.")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "frightened", "ability": "wis" }""", "condition \"frightened\" is not a condition; give prone, restrained, blinded, stunned, paralyzed or unconscious.")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "prone" }""", "ability is required: the saving throw the target makes")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "prone", "ability": "str", "dc": 13, "dc_ability": "str" }""", "give dc or dc_ability, not both.")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "prone", "ability": "str", "when": "on_crit" }""", "when \"on_crit\" is not a when; give every_hit or first_hit_per_turn.")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "prone", "ability": "str", "dc_bonus": 1 }""", "(condition_on_hit): does not take \"dc_bonus\"")]
    [InlineData("""{ "kind": "resistance", "type": "sonic" }""", "type \"sonic\" is not a damage type")]
    [InlineData("""{ "kind": "heal" }""", "modifiers item 1 (heal): heal needs dice (e.g. \"2d4\" for Healing Word) or amount")]
    [InlineData("""{ "kind": "heal", "dice": "2d4", "action_cost": "none" }""", "action_cost \"none\" is not an action_cost; give action or bonus_action.")]
    [InlineData("""{ "kind": "heal", "dice": "2d4", "targets": 7 }""", "targets is 7; it is 1 to 6.")]
    [InlineData("""{ "kind": "heal", "dice": "2d4", "self_only": true, "targets": 2 }""", "self_only heals only the creature itself, so it takes no targets above 1")]
    [InlineData("""{ "kind": "heal", "dice": "2d4", "setup": "action" }""", "heal takes no setup: it is used when it is needed")]
    [InlineData("""{ "kind": "heal", "dice": "2d4", "when": "every_hit" }""", "(heal): does not take \"when\"; heal takes dice, amount, action_cost, targets, self_only, resource")]
    [InlineData("""{ "kind": "heal", "dice": "2d4+wis" }""", "dice \"2d4+wis\" has \"wis\": a flat bonus, \"pb\" or an ability modifier (\"wis\") goes in amount.")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "prone", "ability": "str", "duration": "forever" }""", "duration \"forever\" is not a duration; give start_of_next_turn, end_of_next_turn, save_ends or fight.")]
    [InlineData("""{ "kind": "save_effect", "ability": "dex", "dc": 13, "dice": "1d6", "duration": "fight", "action_cost": "none" }""", "duration is how long its condition lasts, so it needs a condition")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d6", "duration": "fight" }""", "(extra_damage): does not take \"duration\"")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Longbow", "action": "reaction", "trigger": "crit_or_kill", "trigger_probability": 0.5 }""", "trigger \"crit_or_kill\" is only for bonus_action extra attacks")]
    public void Validate_BadModifier_IsRefusedWithWhereAndWhy(string modifier, string why)
    {
        Assert.Contains(why, Problem(WithModifier(modifier)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "kind": "To-Hit", "dice": "1d4", "name": "Bless", "concentration": true, "setup": "action" }""")]
    [InlineData("""{ "kind": "extra_damage", "dice": "2d8", "when": "first_hit_per_turn", "policy": "optimal", "use_value": 9, "resource": {"uses": {"1": 2, "5": 4}, "per": "long_rest"}, "action_cost": "bonus_action" }""")]
    [InlineData("""{ "kind": "extra_damage", "amount": "cha", "type": "radiant", "attack_action_only": true, "crit_doubles": false }""")]
    [InlineData("""{ "kind": "extra_damage", "dice": "1d8", "when": "on_crit" }""")]
    [InlineData("""{ "kind": "extra_damage", "dice": "2d8", "resource": {"uses": 3, "per": "long_rest"}, "policy": "crits_only" }""")]
    [InlineData("""{ "kind": "bonus_damage", "amount": "Proficiency Bonus", "attacks": ["GREATSWORD"] }""")]
    [InlineData("""{ "kind": "crit_range", "min": {"3": 19, "15": 18} }""")]
    [InlineData("""{ "kind": "advantage", "mode": "disadvantage", "rate": 0 }""")]
    [InlineData("""{ "kind": "elven_accuracy", "attacks": ["Longbow"] }""")]
    [InlineData("""{ "kind": "damage_die_remap", "remap": "elemental_adept", "type": "cold" }""")]
    [InlineData("""{ "kind": "reroll_damage_take_best", "policy": "optimal", "use_value": 0 }""")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Longbow", "action": "reaction", "trigger_probability": 0 }""")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "hit", "count": {"1": 1, "11": 2} }""")]
    [InlineData("""{ "kind": "power_attack", "penalty": 5, "bonus": 10, "policy": "never", "attacks": ["Longbow"] }""")]
    [InlineData("""{ "kind": "save_effect", "ability": "Dexterity", "dc_ability": "cha", "dc_bonus": -2, "dice": "3d6", "amount": 2, "type": "fire", "on_success": "none", "targets": 3, "magical": false, "condition": "restrained", "action_cost": "bonus_action", "cantrip": true, "concentration": true }""")]
    [InlineData("""{ "kind": "save_effect", "ability": "con", "dc": 12, "condition": "poisoned", "shape": "line", "size": 60, "action_cost": "none" }""")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "stunned", "ability": "con", "dc_ability": "wis", "when": "first_hit_per_turn", "policy": "crits_only", "magical": true, "resource": {"uses": 5, "per": "short_rest"} }""")]
    [InlineData("""{ "kind": "ignore_cover", "attacks": ["Longbow"] }""")]
    [InlineData("""{ "kind": "temp_hp", "amount": {"1": 5, "10": 10}, "resource": {"uses": 1, "per": "short_rest"} }""")]
    [InlineData("""{ "kind": "heal", "name": "Mass Healing Word", "dice": {"1": "1d4", "9": "2d4"}, "amount": "wis", "action_cost": "Bonus Action", "targets": 6, "resource": {"uses": 1, "per": "long_rest"}, "concentration": false }""")]
    [InlineData("""{ "kind": "heal", "name": "Second Wind", "dice": "1d10", "amount": {"1": 1, "5": 5}, "self_only": true, "targets": 1 }""")]
    [InlineData("""{ "kind": "condition_on_hit", "condition": "stunned", "ability": "con", "dc": 15, "duration": "End of Next Turn" }""")]
    [InlineData("""{ "kind": "save_effect", "ability": "wis", "dc": 15, "condition": "paralyzed", "duration": "save_ends", "action_cost": "bonus_action" }""")]
    [InlineData("""{ "kind": "extra_attack", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit_or_kill" }""")]
    public void Validate_EveryKindAtItsFullest_IsAccepted(string modifier)
    {
        BuildResolver.Validate(WithModifier(modifier));
    }

    [Fact]
    public void Validate_ManyProblems_OneExceptionListsFiveAndCountsTheRest()
    {
        var message = Problem(Build("""
            { "level": 25, "edition": "3.5",
              "attacks": [{ "name": "A", "damage": "2d6kh1", "count": 0, "damage_type": "sonic" }],
              "modifiers": [{ "kind": "smite" }, { "kind": "to_hit" }] }
            """));

        var lines = message.Split('\n');
        Assert.Equal("Invalid build (8 problems):", lines[0]);
        Assert.Equal(
            [
                "- name is required: a label such as \"L5 Fighter, GWM\".",
                "- edition \"3.5\" is not an edition; give 2014 or 2024.",
                "- level is 25; it is 1 to 20.",
                "- attacks item 1 (A): count is 0; it is 1 to 10.",
                "- attacks item 1 (A): damage \"2d6kh1\" keeps or drops dice (\"2d6kh1\"); damage is plain dice and whole numbers joined by + or -, e.g. \"2d6\", \"1d8+1\" or \"1d10+1d6\".",
                "- … and 3 more; fix these and send it again to see them.",
            ],
            lines[1..]);
    }

    [Fact]
    public void Validate_Subject_NamesWhatWasValidated()
    {
        var ex = Assert.Throws<DndInputException>(() => BuildResolver.Validate(Build("""{ "name": "X", "level": 30, "attacks": [{ "name": "A", "damage": "1d8" }] }"""), subject: "baseline"));

        Assert.Equal("Invalid baseline: level is 30; it is 1 to 20.", ex.Message);
    }

    [Fact]
    public void Validate_NonFiniteDoubles_AreRefused()
    {
        var spec = new BuildSpec
        {
            Name = "X",
            Level = 5,
            Attacks = [new AttackSpec { Name = "A", Damage = "1d8" }],
            Modifiers =
            [
                new ModifierSpec { Kind = "advantage", Rate = double.NaN },
                new ModifierSpec { Kind = "extra_damage", Dice = "1d8", When = "first_hit_per_turn", Policy = "optimal", UseValue = double.PositiveInfinity },
            ],
        };

        var message = Problem(spec);

        Assert.Contains("modifiers item 1 (advantage): rate is not a finite number; it is a number 0 to 1.", message, StringComparison.Ordinal);
        Assert.Contains("modifiers item 2 (extra_damage): use_value is not a finite number; it is a number 0 to 1000.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ClrValuesForStepFields_GoThroughTheSameParser()
    {
        var spec = new BuildSpec
        {
            Name = "X",
            Level = 5,
            Abilities = new AbilitiesSpec { Str = new Dictionary<string, int> { ["1"] = 16, ["4"] = 18 } },
            Attacks = [new AttackSpec { Name = "A", Damage = "1d8", Count = 2 }],
        };

        var build = BuildResolver.Resolve(spec, 5);

        Assert.Equal(18, build.Abilities.Str);
        Assert.Equal(2, build.Attacks[0].Count);
        Assert.Contains("count is 0", Problem(new BuildSpec { Name = "X", Level = 5, Attacks = [new AttackSpec { Name = "A", Damage = "1d8", Count = 0 }] }), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ModifierNameInWhere_IsQuoted()
    {
        var message = Problem(WithModifier("""{ "kind": "extra_damage", "name": "Hex" }"""));

        Assert.StartsWith("Invalid build: modifiers item 1 (extra_damage \"Hex\"): extra_damage needs dice", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_LongEchoes_AreCut()
    {
        var message = Problem(With($$"""[{ "name": "A", "damage": "1d8", "damage_type": "{{new string('x', 500)}}" }]"""));

        Assert.True(message.Length < 400, message);
        Assert.Contains("…", message, StringComparison.Ordinal);
    }
}
