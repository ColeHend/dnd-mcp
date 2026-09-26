using System.Text.Json.Serialization;

namespace DndMcp.Repository.Srd.Models;

/// <summary>
/// A 2024 (SRD 5.2.1) monster, exactly as <c>2024/5e-SRD-Monsters.json</c> stores it. See <see cref="Monster2014"/>
/// for why each edition has its own model.
///
/// <para>
/// This data landed upstream in September 2026 and is still changing. The pinned counts in <c>DndMcp.Tests/Srd</c>
/// exist so that a re-vendor which changes it shows up as a deliberate, reviewed diff. Notable gaps the simulator
/// must fill (Phase 5): there is no initiative field and no legendary-action uses count ("3, or 4 in lair" is prose
/// only), and 17 attacks deal flat damage that exists only in the prose ("Hit: 1 Piercing damage").
/// </para>
/// </summary>
public sealed class Monster2024
{
    public required string Index { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    /// <summary>Includes combined sizes such as <c>"Medium or small"</c>.</summary>
    public required string Size { get; init; }

    public required string Type { get; init; }

    public required string Alignment { get; init; }

    /// <summary>Always exactly one entry in 2024, with no source type.</summary>
    public required IReadOnlyList<MonsterArmorClass2024> ArmorClass { get; init; }

    public required int HitPoints { get; init; }

    public required string HitDice { get; init; }

    /// <summary>With spaces: <c>"19d12 + 133"</c> (2014 writes <c>"19d12+133"</c>).</summary>
    public required string HitPointsRoll { get; init; }

    public required MonsterSpeed Speed { get; init; }

    public required int Strength { get; init; }

    public required int Dexterity { get; init; }

    public required int Constitution { get; init; }

    public required int Intelligence { get; init; }

    public required int Wisdom { get; init; }

    public required int Charisma { get; init; }

    public required IReadOnlyList<MonsterProficiency> Proficiencies { get; init; }

    public required IReadOnlyList<string> DamageVulnerabilities { get; init; }

    public required IReadOnlyList<string> DamageResistances { get; init; }

    public required IReadOnlyList<string> DamageImmunities { get; init; }

    public required IReadOnlyList<ApiReference> ConditionImmunities { get; init; }

    public required MonsterSenses Senses { get; init; }

    public required string Languages { get; init; }

    /// <summary>Numeric: 0.125, 0.25, 0.5, then whole numbers.</summary>
    public required double ChallengeRating { get; init; }

    public required int ProficiencyBonus { get; init; }

    public required int Xp { get; init; }

    /// <summary>XP when fought in its lair (29 monsters). It is also the only machine-readable "has a lair" signal.</summary>
    public int? XpInLair { get; init; }

    /// <summary>Equipment the creature carries, as prose.</summary>
    public string? Gear { get; init; }

    public IReadOnlyList<MonsterAction2024>? SpecialAbilities { get; init; }

    /// <summary>Absent only on vampire-mist.</summary>
    public IReadOnlyList<MonsterAction2024>? Actions { get; init; }

    public IReadOnlyList<MonsterAction2024>? BonusActions { get; init; }

    /// <summary>
    /// Present (often empty) on most monsters. Every action costs 1; 44 carry a prose-only "can't take this action
    /// again until the start of its next turn" restriction.
    /// </summary>
    public IReadOnlyList<MonsterAction2024>? LegendaryActions { get; init; }

    public IReadOnlyList<MonsterAction2024>? Reactions { get; init; }

    public IReadOnlyList<ApiReference>? Forms { get; init; }

    public required string Image { get; init; }
}

/// <summary>A 2024 armour class: the value and any linked armour. Unlike 2014 there is no source <c>type</c>.</summary>
public sealed class MonsterArmorClass2024
{
    public required int Value { get; init; }

    public IReadOnlyList<ApiReference>? Armor { get; init; }
}

/// <summary>
/// One entry in any 2024 action-like list: <c>actions</c>, <c>bonus_actions</c>, <c>legendary_actions</c>,
/// <c>reactions</c> or <c>special_abilities</c>.
/// </summary>
public sealed class MonsterAction2024
{
    public required string Name { get; init; }

    public required string Desc { get; init; }

    public int? AttackBonus { get; init; }

    /// <summary>
    /// Always plain rolls in 2024 (no Choice entries). Only the first entry is guaranteed to apply on a hit: 148
    /// attacks have several entries, some riders or duplicates, so summing them overestimates DPR. ABSENT, not empty,
    /// on 18 attacks: 17 whose flat damage exists only in the prose ("Hit: 1 Piercing damage"), and roper Tentacle,
    /// which only grapples.
    /// </summary>
    public IReadOnlyList<MonsterDamage>? Damage { get; init; }

    /// <summary>
    /// With <see cref="AttackBonus"/> this is often a grapple escape DC (28 of 48), not a saving throw against the attack.
    /// </summary>
    public MonsterDifficultyClass? Dc { get; init; }

    public MonsterUsage? Usage { get; init; }

    /// <summary>Set on Multiattack; see <see cref="MultiattackTypes"/>.</summary>
    public string? MultiattackType { get; init; }

    /// <summary>The fixed multiattack routine.</summary>
    public IReadOnlyList<MultiattackAction>? Actions { get; init; }

    /// <summary>
    /// Alone, a choice of routine. Together with <see cref="Actions"/> (41 cases), extra attacks chosen on top of the
    /// routine, which in the prose read "It can replace one attack with…".
    /// </summary>
    public Choice<MultiattackOption>? ActionOptions { get; init; }

    /// <summary>2024 puts spellcasting on actions (60), bonus actions (17), legendary actions (7), reactions (3) and traits (3).</summary>
    public MonsterSpellcasting2024? Spellcasting { get; init; }
}

/// <summary>A 2024 spellcasting block. Per-spell <see cref="MonsterSpell2024.Usage"/> replaces 2014's slot table.</summary>
public sealed class MonsterSpellcasting2024
{
    public required ApiReference Ability { get; init; }

    public int? Dc { get; init; }

    /// <summary>Spell attack bonus.</summary>
    public int? Modifier { get; init; }

    public required IReadOnlyList<string> ComponentsRequired { get; init; }

    public required IReadOnlyList<MonsterSpell2024> Spells { get; init; }
}

/// <summary>
/// A spell in a 2024 spellcasting block.
///
/// <para>
/// The JSON field is <c>level</c>, as in 2014, but here it is the level the monster CASTS the spell at. The adult red
/// dragon casts Command (a level 1 spell) at level 2. The property is named <see cref="CastLevel"/> so that code
/// written against 2014 cannot read it as the base level by accident. Looking up <see cref="Index"/> gives the base
/// level.
/// </para>
/// </summary>
public sealed class MonsterSpell2024
{
    public required string Index { get; init; }

    public required string Name { get; init; }

    [JsonPropertyName("level")]
    public required int CastLevel { get; init; }

    public required string Url { get; init; }

    public MonsterUsage? Usage { get; init; }

    public string? Notes { get; init; }
}
