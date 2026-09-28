using System.ComponentModel;

namespace DndMcp.Domain.Features;

/// <summary>
/// The creature a build attacks. Every field is optional: with nothing given, <see cref="TargetResolver"/> uses the DMG
/// 2014 "Monster Statistics by Challenge Rating" row for CR = the level being evaluated, the community convention for
/// "a fair target at level L", and the typical save bonus for that CR (<see cref="TypicalSaveBonus"/>). <c>profile</c>
/// swaps that table for the SRD monsters' medians (<see cref="TargetProfiles"/>).
///
/// <para>
/// <c>monster</c> names an SRD stat block instead (a ref or a name). The Domain only carries the text: the HOST looks it up
/// (as <c>encounter_difficulty</c> resolves monsters) and passes the normalized stat block to
/// <see cref="TargetResolver.Resolve"/>, which takes every number from it; any other field given here overrides the stat
/// block's, with a note. <c>cr</c> and <c>profile</c> choose a table row, which a stat block replaces, so neither is
/// accepted beside it.
/// </para>
///
/// <para>
/// <c>cr</c> is <c>object?</c> for the same reason as <c>encounter_difficulty</c>'s: "1/2", "5", 0.5 and 5 must all
/// work, and it binds as a <see cref="System.Text.Json.JsonElement"/> whose raw text <see cref="Encounters.ChallengeRating.Parse"/>
/// reads.
/// </para>
/// </summary>
public sealed class TargetSpec
{
    [Description("An SRD monster by ref or name, e.g. \"ogre\": its stat block gives AC, saves, HP, resistances and traits; other fields override it.")]
    public string? Monster { get; init; }

    [Description("Armor Class 1-40. Default: the row for CR = the level evaluated (or for cr).")]
    public int? Ac { get; init; }

    [Description("Challenge rating (\"1/2\", \"5\" or 0.5): its row gives the AC (unless ac is given) and typical saves.")]
    public object? Cr { get; init; }

    [Description("The row's table: dmg2014 (default, DMG 2014), mm2024 or mm2014 (SRD monster medians).")]
    public string? Profile { get; init; }

    [Description("Bonus on every saving throw, -5 to 20. Default: the typical save bonus for the CR.")]
    public int? SaveBonus { get; init; }

    [Description("Per-ability save bonuses, overriding save_bonus, e.g. {\"dex\": 2, \"wis\": 5}.")]
    public SavesSpec? Saves { get; init; }

    [Description("Hit points 1-5000: enables kill chances and HP-capped (effective) damage.")]
    public int? Hp { get; init; }

    [Description("Damage types it resists (half damage), e.g. [\"fire\"].")]
    public IReadOnlyList<string>? Resistances { get; init; }

    [Description("Damage types it is vulnerable to (double damage).")]
    public IReadOnlyList<string>? Vulnerabilities { get; init; }

    [Description("Damage types it is immune to (no damage).")]
    public IReadOnlyList<string>? Immunities { get; init; }

    [Description("Advantage on saves against magical effects (Magic Resistance). Default false.")]
    public bool? MagicResistance { get; init; }

    [Description(
        "Evasion: a Dex save for half takes none on a success and half on a failure (2024: not while stunned, paralyzed or " +
        "unconscious). Default false.")]
    public bool? Evasion { get; init; }

    [Description("A condition it starts every turn with: prone, restrained, blinded, stunned, paralyzed, unconscious or dodging.")]
    public string? Condition { get; init; }

    [Description("\"half\" (+2) or \"three_quarters\" (+5) cover: added to its AC and Dex saves.")]
    public string? Cover { get; init; }

    [Description(
        "Legendary Resistance uses per day, 0-5: gives expected casts to land a condition; landing chances ignore it. Default 0.")]
    public int? LegendaryResistance { get; init; }

    [Description("Dice added to its saving throws, e.g. \"-1d4\" (Bane) or \"1d4\".")]
    public string? SaveDice { get; init; }

    [Description("For Cleave: the chance a second creature is next to it, 0-1. Default 0.")]
    public double? SecondTargetRate { get; init; }
}

/// <summary>Per-ability save bonuses of a target; any not given falls back to save_bonus, then the typical bonus.</summary>
public sealed class SavesSpec
{
    [Description("Strength save bonus, -5 to 20.")]
    public int? Str { get; init; }

    [Description("Dexterity save bonus, -5 to 20.")]
    public int? Dex { get; init; }

    [Description("Constitution save bonus, -5 to 20.")]
    public int? Con { get; init; }

    [Description("Intelligence save bonus, -5 to 20.")]
    public int? Int { get; init; }

    [Description("Wisdom save bonus, -5 to 20.")]
    public int? Wis { get; init; }

    [Description("Charisma save bonus, -5 to 20.")]
    public int? Cha { get; init; }

    /// <summary>The bonus given for an ability key, or null.</summary>
    public int? Get(string ability) => ability switch
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

/// <summary>
/// Table rulings the rules text does not settle. Every one defaults to false (the conservative reading, not a claim that
/// the rules say so: gwf_on_riders and savage_attacker_on_crit_dice are open questions); results echo each one that
/// mattered, so two answers computed under different rulings can be told apart.
/// </summary>
public sealed class RulingsSpec
{
    [Description("2024 Great Weapon Master: the Hew bonus attack also gets the +PB damage. RAW: no (default false).")]
    public bool HewGetsPb { get; init; }

    [Description("The Cleave mastery attack counts as part of the Attack action (for Attack-action-only bonuses). Default false.")]
    public bool CleavePartOfAttackAction { get; init; }

    [Description("Great Weapon Fighting also applies to rider dice such as smites. Default false (weapon dice only).")]
    public bool GwfOnRiders { get; init; }

    [Description("Savage Attacker rerolls all the doubled dice of a crit. Default false (one set of the weapon's dice).")]
    public bool SavageAttackerOnCritDice { get; init; }
}

/// <summary>
/// A feature to try on a baseline build (<c>balance_compare</c>'s <c>feature</c>): what it adds or changes.
/// <see cref="FeatureMerge.Merge"/> turns baseline + feature into the variant build.
/// </summary>
public sealed class FeatureSpec
{
    [Description("Required. The feature's name, one line, at most 80 characters, e.g. \"Great Weapon Master\".")]
    public string? Name { get; init; }

    [Description("Attacks to add. One with the name of a baseline attack REPLACES that attack.")]
    public IReadOnlyList<AttackSpec>? Attacks { get; init; }

    [Description("Modifiers to add to the baseline's, e.g. [{\"kind\": \"bonus_damage\", \"amount\": \"pb\", \"attack_action_only\": true}].")]
    public IReadOnlyList<ModifierSpec>? Modifiers { get; init; }

    [Description(
        "Ability scores to SET (not add), replacing the baseline's value at every level: {\"str\": 19} for a feat's +1 to an 18. " +
        "With a stepped baseline give the whole step map, e.g. {\"1\": 16, \"4\": 17, \"8\": 19} for a feat instead of the level 4 ASI.")]
    public AbilitiesSpec? Abilities { get; init; }

    [Description("A fighting style replacing the baseline's: gwf, archery, dueling or twf.")]
    public string? FightingStyle { get; init; }
}
