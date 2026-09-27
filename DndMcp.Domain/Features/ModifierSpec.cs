using System.ComponentModel;

namespace DndMcp.Domain.Features;

/// <summary>
/// One modifier: a flat union of every kind's fields, discriminated by <see cref="Kind"/>.
///
/// <para>
/// <b>Flat rather than one class per kind</b> because the MCP SDK builds one schema per parameter type and has no
/// discriminated unions the model reliably fills; a flat object with a <c>kind</c> reads like the 5e data. The price is
/// that the schema allows every field on every kind, so <see cref="ModifierFields"/> holds the per-kind table and the
/// validator refuses a field the kind does not take ("modifiers item 2 (to_hit): does not take \"type\" …"): silently
/// ignoring it would let a model believe <c>{"kind": "to_hit", "type": "fire"}</c> did something.
/// </para>
/// <para>
/// Every field is nullable so "given" is visible to that check, and so the resolver, not the binder, applies each
/// kind's defaults (a policy defaults to any_hit on extra_damage but auto on power_attack).
/// </para>
/// </summary>
public sealed class ModifierSpec
{
    [Description(
        "Required: to_hit, extra_damage, bonus_damage, crit_range, advantage, lucky, elven_accuracy, damage_die_remap, " +
        "reroll_damage_take_best, extra_attack, power_attack, save_effect, condition_on_hit, ignore_cover, ac, resistance, " +
        "temp_hp.")]
    public string? Kind { get; init; }

    [Description("A label for results, at most 60 characters, e.g. \"Hex\".")]
    public string? Name { get; init; }

    [Description("The names of the attacks it applies to. Default: every attack it can apply to.")]
    public IReadOnlyList<string>? Attacks { get; init; }

    [Description("The first level it is active at, 1-20.")]
    public int? FromLevel { get; init; }

    [Description("The last level it is active at, 1-20.")]
    public int? UntilLevel { get; init; }

    [Description(
        "Limited uses, e.g. {\"uses\": 3, \"per\": \"long_rest\"}. Taken by extra_damage, extra_attack, save_effect, " +
        "condition_on_hit and the defensive kinds.")]
    public ResourceSpec? Resource { get; init; }

    [Description("Needs Concentration: only one concentration modifier may be active at a level. Default false.")]
    public bool? Concentration { get; init; }

    [Description("\"bonus_action\" or \"action\": a cost paid in the first round of a fight (casting Hex); it then stays on.")]
    public string? Setup { get; init; }

    [Description(
        "A whole number -100 to 100, \"pb\" (proficiency bonus), an ability (\"cha\" = its modifier) or a step map of " +
        "numbers by level. to_hit: attack bonus; extra_damage, bonus_damage, save_effect: flat damage.")]
    public object? Amount { get; init; }

    [Description(
        "Dice. to_hit: added to attack rolls, \"1d4\" (Bless) or \"-1d4\" (Bane). extra_damage, save_effect: damage dice " +
        "such as \"2d8\", or a step map by level.")]
    public object? Dice { get; init; }

    [Description("A damage type, e.g. \"fire\" (extra_damage, save_effect, resistance; elemental_adept's type).")]
    public string? Type { get; init; }

    [Description(
        "extra_damage: every_hit (default), first_hit_per_turn, on_crit (added once) or on_miss. condition_on_hit: " +
        "every_hit (default) or first_hit_per_turn.")]
    public string? When { get; init; }

    [Description(
        "When an optional rider is spent: any_hit (default), crits_only, crit_or_last, optimal (maximises damage minus " +
        "use_value per use). power_attack: auto (default), always or never.")]
    public string? Policy { get; init; }

    [Description("With policy optimal only: the damage one use is worth elsewhere, 0 or more (e.g. 9 for a spell slot).")]
    public double? UseValue { get; init; }

    [Description("extra_damage: its dice are doubled on a crit. Default true.")]
    public bool? CritDoubles { get; init; }

    [Description("Only on attacks made as part of the Attack action (2024 Great Weapon Master's +PB). Default false.")]
    public bool? AttackActionOnly { get; init; }

    [Description(
        "extra_damage: \"bonus_action\" when spending it uses the Bonus Action (2024 Divine Smite). save_effect: " +
        "\"action\" (default), \"bonus_action\" or \"none\".")]
    public string? ActionCost { get; init; }

    [Description("crit_range: crit on this d20 roll or higher, 2-20 (e.g. 19), or a step map by level.")]
    public object? Min { get; init; }

    [Description("advantage: \"advantage\" (default) or \"disadvantage\".")]
    public string? Mode { get; init; }

    [Description("advantage: the chance per turn the source is present, 0-1 (default 1); a turn's attacks share it.")]
    public double? Rate { get; init; }

    [Description(
        "damage_die_remap: gwf2014 (reroll 1-2 once), gwf2024 (1-2 count as 3) or elemental_adept (a 1 counts as 2 on " +
        "dice of type).")]
    public string? Remap { get; init; }

    [Description("extra_attack: the name of the attack it makes.")]
    public string? Attack { get; init; }

    [Description("extra_attack: how many attacks, 1-10 (default 1), or a step map by level.")]
    public object? Count { get; init; }

    [Description(
        "extra_attack: \"action\" (Action Surge; needs a resource), \"bonus_action\" or \"reaction\" (needs " +
        "trigger_probability).")]
    public string? Action { get; init; }

    [Description(
        "extra_attack with bonus_action: always (default), hit (after any hit this turn) or crit (after a melee crit this " +
        "turn: Great Weapon Master).")]
    public string? Trigger { get; init; }

    [Description("extra_attack with reaction, and only then: the chance per round the reaction attack happens, 0-1.")]
    public double? TriggerProbability { get; init; }

    [Description("power_attack: the attack roll penalty. Default 5.")]
    public int? Penalty { get; init; }

    [Description("power_attack: the damage bonus on a hit. Default 10.")]
    public int? Bonus { get; init; }

    [Description("save_effect, condition_on_hit: the saving throw, str, dex, con, int, wis or cha.")]
    public string? Ability { get; init; }

    [Description("The save DC, 1-40. Or give dc_ability.")]
    public int? Dc { get; init; }

    [Description(
        "The DC is 8 + proficiency bonus + this ability's modifier, e.g. \"cha\". condition_on_hit default: the attack's " +
        "ability.")]
    public string? DcAbility { get; init; }

    [Description("save_effect with dc_ability: added to the DC, -10 to 10 (e.g. 1 for a +1 focus).")]
    public int? DcBonus { get; init; }

    [Description("save_effect: \"half\" (default) or \"none\": damage on a successful save.")]
    public string? OnSuccess { get; init; }

    [Description("save_effect: creatures affected, 1-20. Default 1; or give shape and size instead.")]
    public int? Targets { get; init; }

    [Description("save_effect area: cone, cube, cylinder, line or sphere, with size (creatures per the DMG's area table).")]
    public string? Shape { get; init; }

    [Description("save_effect area size in feet (cone/line length, cube side, cylinder/sphere radius). Fireball: sphere, 20.")]
    public int? Size { get; init; }

    [Description("It is magical, so Magic Resistance applies. Default true for save_effect, false for condition_on_hit.")]
    public bool? Magical { get; init; }

    [Description(
        "condition_on_hit (required) and save_effect: prone, restrained, blinded, stunned, paralyzed or unconscious; " +
        "save_effect also takes other conditions (frightened, …) as labels.")]
    public string? Condition { get; init; }

    [Description("save_effect: the damage dice scale like a cantrip's, x1/x2/x3/x4 at level 1/5/11/17. Default false.")]
    public bool? Cantrip { get; init; }
}

/// <summary>Limited uses of a modifier, recovered on a short or long rest.</summary>
public sealed class ResourceSpec
{
    [Description("Required. Uses per rest, 1-20, or a step map by level such as {\"1\": 1, \"17\": 2}.")]
    public object? Uses { get; init; }

    [Description("Required. \"short_rest\" or \"long_rest\": when the uses come back.")]
    public string? Per { get; init; }
}
