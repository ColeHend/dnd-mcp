namespace DndMcp.Repository.Srd.Models;

/// <summary>
/// A 2014 (SRD 5.1) spell, exactly as <c>2014/5e-SRD-Spells.json</c> stores it.
///
/// <para>
/// <b>Why spells are per-edition.</b> The editions share names but not shapes, so one merged model would need a
/// converter or an <c>object</c> on nearly every field that matters:
/// </para>
/// <list type="bullet">
/// <item>2014 has <c>desc</c> (<c>string[]</c>); 2024 has <c>description</c> (<c>string</c>).</item>
/// <item><c>higher_level</c> is an array in 2014 and a string in 2024.</item>
/// <item><c>damage</c> is an array in 2014 and a single object in 2024.</item>
/// <item>2014 cantrips scale through <c>damage_at_character_level</c>; 2024 puts the cantrip die under slot level
/// <c>"0"</c> and leaves the scaling in prose.</item>
/// <item>2014 has <c>dc</c>, <c>area_of_effect</c>, <c>heal_at_slot_level</c> and <c>subclasses</c>. No 2024 spell
/// has any of them.</item>
/// </list>
/// <para>
/// Worse, a merged model would make "no DC" look like a property of the spell when for 2024 it is a gap in the
/// data. With separate models, the 2024 type simply has no <c>Dc</c>, and the Phase 5 overlay that supplies one is
/// visibly a separate source.
/// </para>
/// </summary>
public sealed class Spell2014
{
    public required string Index { get; init; }

    public required string Name { get; init; }

    public required string Url { get; init; }

    /// <summary>Rules text, one paragraph (or table row) per element.</summary>
    public required IReadOnlyList<string> Desc { get; init; }

    /// <summary>"At Higher Levels" text (90 spells).</summary>
    public IReadOnlyList<string>? HigherLevel { get; init; }

    public required string Range { get; init; }

    /// <summary><c>V</c>, <c>S</c>, <c>M</c>.</summary>
    public required IReadOnlyList<string> Components { get; init; }

    public string? Material { get; init; }

    public required bool Ritual { get; init; }

    public required string Duration { get; init; }

    public required bool Concentration { get; init; }

    /// <summary>e.g. <c>"1 action"</c> (2024 writes <c>"Action"</c>).</summary>
    public required string CastingTime { get; init; }

    /// <summary>Spell level; 0 is a cantrip.</summary>
    public required int Level { get; init; }

    /// <summary><c>melee</c> or <c>ranged</c> for spell attacks.</summary>
    public string? AttackType { get; init; }

    /// <summary>
    /// Usually one entry. Flame strike, ice storm and meteor swarm have two (e.g. fire plus radiant), each rolled.
    /// </summary>
    public IReadOnlyList<SpellDamage2014>? Damage { get; init; }

    public SpellDifficultyClass2014? Dc { get; init; }

    public SpellAreaOfEffect2014? AreaOfEffect { get; init; }

    /// <summary>Healing per slot level. Values may contain <c>" + MOD"</c> (the caster's spellcasting modifier).</summary>
    public IReadOnlyDictionary<int, string>? HealAtSlotLevel { get; init; }

    public required ApiReference School { get; init; }

    public required IReadOnlyList<ApiReference> Classes { get; init; }

    /// <summary>Subclasses that get the spell for free (e.g. <c>lore</c>, <c>fiend</c>; 2014 indexes).</summary>
    public required IReadOnlyList<ApiReference> Subclasses { get; init; }
}

/// <summary>
/// One damage component of a 2014 spell: a slot-level table or a character-level (cantrip) table.
///
/// <para>
/// Table values are not always plain dice. They include <c>"3d4 + 3"</c>, <c>"1d8 + MOD"</c>,
/// <c>"4d6 OR 5d6"</c> (a choice) and flat numbers such as <c>"20"</c>. The dice parser must accept or reject each
/// form explicitly. Stripping the unfamiliar parts would under-count magic missile and hide the OR.
/// </para>
/// </summary>
public sealed class SpellDamage2014
{
    /// <summary>Absent on prismatic spray (type depends on the ray) and sleep (the "damage" is hit points affected).</summary>
    public ApiReference? DamageType { get; init; }

    /// <summary>Damage keyed by slot level (from the spell's own level up to 9).</summary>
    public IReadOnlyDictionary<int, string>? DamageAtSlotLevel { get; init; }

    /// <summary>Cantrip damage keyed by character level (1, 5, 11, 17).</summary>
    public IReadOnlyDictionary<int, string>? DamageAtCharacterLevel { get; init; }
}

/// <summary>A 2014 spell's saving throw. Unlike a monster DC there is no value: it is the caster's spell save DC.</summary>
public sealed class SpellDifficultyClass2014
{
    public required ApiReference DcType { get; init; }

    /// <summary><c>none</c>, <c>half</c> or <c>other</c>; see <see cref="SaveSuccessTypes"/>.</summary>
    public required string DcSuccess { get; init; }

    /// <summary>What happens on a success when <see cref="DcSuccess"/> is <c>other</c> or needs explaining.</summary>
    public string? Desc { get; init; }
}

/// <summary>A 2014 spell's area: <c>sphere</c>/<c>cylinder</c> use radius, <c>cube</c> side, <c>cone</c>/<c>line</c> length, in feet.</summary>
public sealed class SpellAreaOfEffect2014
{
    public required string Type { get; init; }

    public required int Size { get; init; }
}
