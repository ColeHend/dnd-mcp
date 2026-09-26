using System.Text.Json.Serialization;
using DndMcp.Repository.Srd.Json;

namespace DndMcp.Repository.Srd.Models;

/// <summary>
/// A 2014 (SRD 5.1) monster, exactly as <c>2014/5e-SRD-Monsters.json</c> stores it.
///
/// <para>
/// <b>Why monsters are per-edition rather than one model with optional fields.</b> The two editions share field
/// NAMES whose meanings or shapes differ, and a merged model would let a consumer read one edition's value with the
/// other edition's meaning:
/// </para>
/// <list type="bullet">
/// <item><c>damage[]</c> elements may be a Choice in 2014 (<see cref="MonsterDamageEntry2014"/>) but are always a plain
/// roll in 2024.</item>
/// <item>A spellcasting spell's <c>level</c> is the spell's base level in 2014 but the level it is CAST at in 2024.</item>
/// <item>Spellcasting lives in <see cref="SpecialAbilities"/> in 2014 and in actions (and bonus, legendary and
/// reaction lists) in 2024.</item>
/// <item>Legendary action costs are encoded in 2014 names ("Wing Attack (Costs 2 Actions)") and absent from 2024
/// names.</item>
/// <item>2014 has breath-weapon <c>options</c>, roar <c>attacks</c>, armour class <c>type</c> and <c>subtype</c>; 2024
/// has <c>bonus_actions</c>, <c>gear</c> and <c>xp_in_lair</c>.</item>
/// </list>
/// <para>
/// Keeping them apart means each model states exactly what its edition contains. It also means the strict test
/// (unknown fields disallowed) fails if 2024 ever gains a field that only 2014 had. Leaf types whose meaning did not
/// change (<see cref="MonsterDifficultyClass"/>, <see cref="MonsterUsage"/>, <see cref="MonsterDamage"/>, …) are
/// shared.
/// </para>
/// </summary>
public sealed class Monster2014
{
    public required string Index { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    /// <summary>Flavour text on 45 monsters (the SRD appendix creatures and NPCs, e.g. acolyte, blink dog). Not rules.</summary>
    public string? Desc { get; init; }

    public required string Size { get; init; }

    public required string Type { get; init; }

    public string? Subtype { get; init; }

    public required string Alignment { get; init; }

    /// <summary>
    /// Usually one entry. Seven monsters list a second AC: with a spell (mage, archmage: Mage Armor; druid, dryad:
    /// Barkskin), with a shield or armour (azer, lizardfolk), or while prone (ankheg). The first entry is the default.
    /// </summary>
    public required IReadOnlyList<MonsterArmorClass2014> ArmorClass { get; init; }

    public required int HitPoints { get; init; }

    public required string HitDice { get; init; }

    /// <summary>With the CON bonus and no spaces: <c>"19d12+133"</c> (2024 writes <c>"19d12 + 133"</c>).</summary>
    public required string HitPointsRoll { get; init; }

    public required MonsterSpeed Speed { get; init; }

    public required int Strength { get; init; }

    public required int Dexterity { get; init; }

    public required int Constitution { get; init; }

    public required int Intelligence { get; init; }

    public required int Wisdom { get; init; }

    public required int Charisma { get; init; }

    public required IReadOnlyList<MonsterProficiency> Proficiencies { get; init; }

    /// <summary>Free text. Qualifiers are part of the string ("…from nonmagical attacks"), so never split on commas.</summary>
    public required IReadOnlyList<string> DamageVulnerabilities { get; init; }

    /// <inheritdoc cref="DamageVulnerabilities"/>
    public required IReadOnlyList<string> DamageResistances { get; init; }

    /// <inheritdoc cref="DamageVulnerabilities"/>
    public required IReadOnlyList<string> DamageImmunities { get; init; }

    public required IReadOnlyList<ApiReference> ConditionImmunities { get; init; }

    public required MonsterSenses Senses { get; init; }

    public required string Languages { get; init; }

    /// <summary>Numeric: 0.125, 0.25, 0.5, then whole numbers. <c>double</c> represents all of them exactly.</summary>
    public required double ChallengeRating { get; init; }

    public required int ProficiencyBonus { get; init; }

    public required int Xp { get; init; }

    /// <summary>Traits, including Spellcasting and Innate Spellcasting (2014 keeps all monster spellcasting here).</summary>
    public IReadOnlyList<MonsterAction2014>? SpecialAbilities { get; init; }

    /// <summary>Absent on three monsters with no actions (frog, sea horse, shrieker), not an empty list.</summary>
    public IReadOnlyList<MonsterAction2014>? Actions { get; init; }

    /// <summary>
    /// Costs are only in the names ("(Costs 2 Actions)" ×35, "(Costs 3 Actions)" ×6). The number of legendary
    /// actions per round is not stored anywhere; SRD 5.1 gives 3 for every legendary monster.
    /// </summary>
    public IReadOnlyList<MonsterAction2014>? LegendaryActions { get; init; }

    public IReadOnlyList<MonsterAction2014>? Reactions { get; init; }

    /// <summary>The other stat blocks of a shapechanger (vampire → bat, mist), each a separate monster record.</summary>
    public IReadOnlyList<ApiReference>? Forms { get; init; }

    public required string Image { get; init; }
}

/// <summary>A 2014 armour class entry: the value plus where it comes from.</summary>
public sealed class MonsterArmorClass2014
{
    /// <summary><c>natural</c>, <c>dex</c>, <c>armor</c>, <c>spell</c> or <c>condition</c>.</summary>
    public required string Type { get; init; }

    public required int Value { get; init; }

    public IReadOnlyList<ApiReference>? Armor { get; init; }

    /// <summary>The spell granting this AC (<c>mage-armor</c>, <c>barkskin</c>) when <see cref="Type"/> is <c>spell</c>.</summary>
    public ApiReference? Spell { get; init; }

    /// <summary>The condition under which this AC applies when <see cref="Type"/> is <c>condition</c>.</summary>
    public ApiReference? Condition { get; init; }

    /// <summary>Armour described in prose rather than linked (<c>"patchwork armor"</c>).</summary>
    public string? Desc { get; init; }
}

/// <summary>
/// One entry in any 2014 action-like list: <c>actions</c>, <c>legendary_actions</c>, <c>reactions</c> or
/// <c>special_abilities</c>. All four share one shape, so one type covers them. The list an entry sits in is what
/// makes it an action, a trait or a reaction.
/// </summary>
public sealed class MonsterAction2014
{
    public required string Name { get; init; }

    public required string Desc { get; init; }

    public int? AttackBonus { get; init; }

    /// <summary>
    /// Only the FIRST entry is guaranteed to apply on a hit. Later entries may be riders, conditional or ongoing
    /// damage (64 attacks have several plain entries), so summing them all overestimates DPR. An entry may also be a
    /// Choice (16 cases); see <see cref="MonsterDamageEntry2014"/>.
    /// </summary>
    public IReadOnlyList<MonsterDamageEntry2014>? Damage { get; init; }

    public MonsterDifficultyClass? Dc { get; init; }

    /// <summary>Recharge or limited uses; the only reliable recharge signal (names are stripped).</summary>
    public MonsterUsage? Usage { get; init; }

    /// <summary>Set on Multiattack; see <see cref="MultiattackTypes"/>.</summary>
    public string? MultiattackType { get; init; }

    /// <summary>The fixed multiattack routine.</summary>
    public IReadOnlyList<MultiattackAction>? Actions { get; init; }

    /// <summary>A choice between multiattack routines (2014 never combines it with <see cref="Actions"/>).</summary>
    public Choice<MultiattackOption>? ActionOptions { get; init; }

    /// <summary>Breath weapons offered as a choice (20 dragons): pick one breath per use.</summary>
    public Choice<BreathOption2014>? Options { get; init; }

    /// <summary>Sub-effects of one action: the androsphinx's First, Second and Third Roar.</summary>
    public IReadOnlyList<MonsterSubAttack2014>? Attacks { get; init; }

    public MonsterSpellcasting2014? Spellcasting { get; init; }
}

/// <summary>
/// One element of a 2014 <c>damage[]</c>: exactly one of <see cref="Damage"/> (a roll) or <see cref="Choice"/> (pick a
/// roll, e.g. lightning OR thunder). <see cref="MonsterDamageEntryConverter"/> decides which. The DPR engine must
/// handle both; treating a choice as a roll with no dice would drop the damage.
/// </summary>
[JsonConverter(typeof(MonsterDamageEntryConverter))]
public sealed class MonsterDamageEntry2014
{
    private MonsterDamageEntry2014(MonsterDamage? damage, Choice<DamageOption2014>? choice)
    {
        Damage = damage;
        Choice = choice;
    }

    public MonsterDamage? Damage { get; }

    public Choice<DamageOption2014>? Choice { get; }

    public bool IsChoice => Choice is not null;

    public static MonsterDamageEntry2014 FromDamage(MonsterDamage damage) => new(damage ?? throw new ArgumentNullException(nameof(damage)), null);

    public static MonsterDamageEntry2014 FromChoice(Choice<DamageOption2014> choice) => new(null, choice ?? throw new ArgumentNullException(nameof(choice)));
}

/// <summary>One roll offered by a 2014 damage Choice.</summary>
public sealed class DamageOption2014
{
    /// <summary>Always <c>damage</c>.</summary>
    public required string OptionType { get; init; }

    public required ApiReference DamageType { get; init; }

    public required string DamageDice { get; init; }

    /// <summary>When this option applies (e.g. <c>"One handed"</c> vs <c>"Two handed"</c>).</summary>
    public string? Notes { get; init; }
}

/// <summary>One breath weapon offered by a 2014 dragon's Breath Weapons choice.</summary>
public sealed class BreathOption2014
{
    /// <summary>Always <c>breath</c>.</summary>
    public required string OptionType { get; init; }

    public required string Name { get; init; }

    public required MonsterDifficultyClass Dc { get; init; }

    /// <summary>Absent on non-damaging breaths (Sleep, Repulsion, Slowing, Weakening and Paralyzing Breath).</summary>
    public IReadOnlyList<MonsterDamage>? Damage { get; init; }
}

/// <summary>A named sub-effect of a 2014 action (only the androsphinx Roar uses this).</summary>
public sealed class MonsterSubAttack2014
{
    public required string Name { get; init; }

    public required MonsterDifficultyClass Dc { get; init; }

    public IReadOnlyList<MonsterDamage>? Damage { get; init; }
}

/// <summary>
/// A 2014 Spellcasting or Innate Spellcasting trait. Slot-based casters have <see cref="Level"/>, <see cref="School"/>
/// and <see cref="Slots"/>. Innate casters have per-spell <see cref="MonsterSpell2014.Usage"/> instead.
/// </summary>
public sealed class MonsterSpellcasting2014
{
    public required ApiReference Ability { get; init; }

    public int? Dc { get; init; }

    /// <summary>Spell attack bonus.</summary>
    public int? Modifier { get; init; }

    public required IReadOnlyList<string> ComponentsRequired { get; init; }

    /// <summary>CASTER level ("an 18th-level spellcaster"), not a spell level.</summary>
    public int? Level { get; init; }

    /// <summary>The class spell list the caster uses (<c>wizard</c>, <c>cleric</c>), despite the field name.</summary>
    public string? School { get; init; }

    /// <summary>Spell slots per spell level (1–9).</summary>
    public IReadOnlyDictionary<int, int>? Slots { get; init; }

    public required IReadOnlyList<MonsterSpell2014> Spells { get; init; }
}

/// <summary>
/// A spell in a 2014 spellcasting trait. It has no <c>index</c>; the spell's index is the last segment of
/// <see cref="Url"/>. <see cref="Level"/> is the spell's own base level (0 for cantrips).
/// </summary>
public sealed class MonsterSpell2014
{
    public required string Name { get; init; }

    public required int Level { get; init; }

    public required string Url { get; init; }

    /// <summary>At will / per day, for innate casters.</summary>
    public MonsterUsage? Usage { get; init; }

    /// <summary>A restriction on the spell, e.g. <c>"Self only"</c> or <c>"Cast on self before combat"</c>.</summary>
    public string? Notes { get; init; }
}
