namespace DndMcp.Repository.Srd.Models;

// Monster building blocks whose shape AND meaning are the same in both editions. A 2024-only field on one of them is
// optional and documented. Anything whose meaning changed between editions (damage arrays that may hold a Choice,
// spellcasting spell levels, armour class entries) is per-edition in Monster2014.cs / Monster2024.cs instead.

/// <summary>
/// A saving throw attached to an action, trait or damage roll: <c>{dc_type, dc_value, success_type}</c>.
///
/// <para>
/// <see cref="SuccessType"/> is not always right. Five 2014 save-for-half actions are labelled <c>"none"</c> (adult
/// red dragon Fire Breath, ancient white dragon Cold Breath, green dragon wyrmling Poison Breath, lich Disrupt Life,
/// winter wolf Cold Breath). Phase 5 corrects them through overrides; this model reports the data as vendored.
/// Also, when an action has an <c>attack_bonus</c>, a STR DC with <c>"none"</c> is usually a grapple escape DC, not a
/// save (28 of 48 such actions in 2024).
/// </para>
/// </summary>
public sealed class MonsterDifficultyClass
{
    /// <summary>The ability the save uses (<c>dex</c>, <c>con</c>, …).</summary>
    public required ApiReference DcType { get; init; }

    /// <summary>
    /// The DC. It is null when the DC is computed rather than fixed: 2024 zombie and ogre zombie Undead Fortitude,
    /// "DC 5 plus the damage taken". Treating null as 0 would make every such save succeed.
    /// </summary>
    public int? DcValue { get; init; }

    /// <summary>What a successful save does; one of <see cref="SaveSuccessTypes"/>.</summary>
    public required string SuccessType { get; init; }
}

/// <summary><see cref="MonsterDifficultyClass.SuccessType"/> values as they appear in the data.</summary>
public static class SaveSuccessTypes
{
    public const string None = "none";
    public const string Half = "half";
    public const string Other = "other";
}

/// <summary>
/// Limited use of an action or trait: recharge, per day, or after a rest.
///
/// <para>
/// This is the ONLY recharge signal. The API strips "(Recharge 5–6)" from action names, so a normalizer that looked
/// for it in names would treat every breath weapon as usable every turn. <c>usage</c> is complete in both editions.
/// </para>
/// </summary>
public sealed class MonsterUsage
{
    /// <summary>One of <see cref="UsageTypes"/>.</summary>
    public required string Type { get; init; }

    /// <summary>Uses per day (<c>per day</c>, and per-spell usage in spellcasting blocks).</summary>
    public int? Times { get; init; }

    /// <summary>2024 only: uses per day in the creature's lair (Legendary Resistance 3 becomes 4).</summary>
    public int? TimesInLair { get; init; }

    /// <summary>The recharge die, e.g. <c>1d6</c>.</summary>
    public string? Dice { get; init; }

    /// <summary>Lowest roll on <see cref="Dice"/> that recharges it; 5 means "Recharge 5–6".</summary>
    public int? MinValue { get; init; }

    /// <summary><c>short</c> / <c>long</c> for <c>recharge after rest</c>.</summary>
    public IReadOnlyList<string>? RestTypes { get; init; }
}

/// <summary><see cref="MonsterUsage.Type"/> values as they appear in the data.</summary>
public static class UsageTypes
{
    public const string PerDay = "per day";
    public const string RechargeOnRoll = "recharge on roll";
    public const string RechargeAfterRest = "recharge after rest";
    public const string AtWill = "at will";
}

/// <summary>
/// One damage roll: <c>{damage_type, damage_dice, dc?}</c>.
///
/// <para>
/// The ability modifier is already baked into <see cref="DamageDice"/> (<c>"2d10+8"</c>); do not add it again. 2014
/// also has flat values (<c>"1"</c>, 19 cases). 2024 has none: its flat-damage attacks have no <c>damage</c> at all,
/// and the number is only in the prose.
/// </para>
/// </summary>
public sealed class MonsterDamage
{
    public required ApiReference DamageType { get; init; }

    /// <summary><c>NdM</c>, <c>NdM+K</c>, <c>NdM-K</c> or (2014 only) a flat integer.</summary>
    public required string DamageDice { get; init; }

    /// <summary>
    /// A save that applies to this roll only (2014 only, 2 cases, e.g. the assassin's 7d6 poison, CON 15 for half).
    /// </summary>
    public MonsterDifficultyClass? Dc { get; init; }
}

/// <summary>Movement speeds as upstream prints them (<c>"40 ft."</c>). Phase 5 parses them; lookup shows them verbatim.</summary>
public sealed class MonsterSpeed
{
    public string? Walk { get; init; }

    public string? Burrow { get; init; }

    public string? Climb { get; init; }

    public string? Fly { get; init; }

    public string? Swim { get; init; }

    /// <summary>Present (true) only on creatures that hover.</summary>
    public bool? Hover { get; init; }
}

/// <summary>Senses as upstream prints them (<c>"120 ft."</c>), plus the numeric passive Perception.</summary>
public sealed class MonsterSenses
{
    public string? Blindsight { get; init; }

    public string? Darkvision { get; init; }

    public string? Tremorsense { get; init; }

    public string? Truesight { get; init; }

    public required int PassivePerception { get; init; }
}

/// <summary>
/// A proficient save or skill with its TOTAL bonus (<c>saving-throw-dex</c> +6, <c>skill-perception</c> +13). Saves
/// and skills share this list. The <see cref="Proficiency"/> index prefix tells them apart.
/// </summary>
public sealed class MonsterProficiency
{
    public required int Value { get; init; }

    public required ApiReference Proficiency { get; init; }
}
