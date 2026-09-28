using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// One spell as the simulator can cast it: what it does in a fight at its own level, and how that grows with the slot
/// or (cantrips) the caster's level. Produced by <see cref="SpellNormalizer"/> from a spell record plus the overlay;
/// turned into a <see cref="StatBlockAction"/> for one caster by <see cref="SpellNormalizer.Cast"/>.
///
/// <para>
/// A profile always exists: a spell with no combat effect or one the simulator does not model is still a profile, of
/// kind <see cref="SpellProfileKinds.NoCombatEffect"/> or <see cref="SpellProfileKinds.NotModelled"/> with the
/// <see cref="Reason"/>. That is what lets every monster's spell list be accounted for in full (named in notes or
/// warnings) rather than silently shortened to the spells that deal damage.
/// </para>
/// </summary>
public sealed record SpellProfile
{
    /// <summary>The spell's ref, e.g. <c>2024/spell/fireball</c>.</summary>
    public required string Ref { get; init; }

    public required string Name { get; init; }

    /// <summary><see cref="SpellProfileKinds"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The spell's own level (0 for cantrips).</summary>
    public required int Level { get; init; }

    public bool Concentration { get; init; }

    /// <summary>Attack spells: <see cref="StatBlockValues.AttackRanges"/>.</summary>
    public string? AttackRange { get; init; }

    /// <summary>Save spells: the ability key and what a success does.</summary>
    public string? SaveAbility { get; init; }

    public string? OnSuccess { get; init; }

    /// <summary>Damage at the spell's own level (per ray, per dart, per target).</summary>
    public IReadOnlyList<DamageRoll> Damage { get; init; } = [];

    /// <summary>2014 records' damage table by slot level, where the record has one; wins over <see cref="UpcastDamage"/>.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<DamageRoll>> DamageBySlot { get; init; } = new Dictionary<int, IReadOnlyList<DamageRoll>>();

    /// <summary>Damage added per slot level above <see cref="Level"/>.</summary>
    public IReadOnlyList<DamageRoll> UpcastDamage { get; init; } = [];

    /// <summary>2014 cantrip tables by character level (1, 5, 11, 17).</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<DamageRoll>> DamageByCharacterLevel { get; init; } = new Dictionary<int, IReadOnlyList<DamageRoll>>();

    /// <summary>Cantrips without a table: <c>dice</c> (×1/×2/×3/×4 at 1/5/11/17) or <c>beams</c> (that many attacks).</summary>
    public string? CantripScaling { get; init; }

    /// <summary>The spellcasting modifier is added to each damage roll (Spiritual Weapon).</summary>
    public bool DamageAddsModifier { get; init; }

    /// <summary>Rays, darts or chosen creatures at the spell's own level.</summary>
    public int Targets { get; init; } = 1;

    public int UpcastTargets { get; init; }

    public AreaSpec? Area { get; init; }

    /// <summary>The condition a failed save (or a hit) imposes; its save and escape DC come from the caster.</summary>
    public SpellOverlayCondition? Condition { get; init; }

    public DamageFormula? Healing { get; init; }

    public bool HealingAddsModifier { get; init; }

    public DamageFormula? UpcastHealing { get; init; }

    /// <summary>Parry spells (Shield).</summary>
    public int? AcBonus { get; init; }

    /// <summary>How a lingering effect is simplified; an <c>approximated</c> warning on every caster.</summary>
    public string? Approximation { get; init; }

    /// <summary>Why a spell has no combat profile (<see cref="SpellProfileKinds.NoCombatEffect"/> / <see cref="SpellProfileKinds.NotModelled"/>).</summary>
    public string? Reason { get; init; }

    /// <summary>Notes carried to every cast (a rider not modelled, an entry from the overlay).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>True for the kinds the simulator casts.</summary>
    public bool IsCombat => Kind is not (SpellProfileKinds.NoCombatEffect or SpellProfileKinds.NotModelled);
}

/// <summary>
/// <see cref="SpellProfile.Kind"/> values: the combat kinds are <see cref="StatBlockValues.ActionKinds"/>' own.
/// </summary>
public static class SpellProfileKinds
{
    public const string Attack = StatBlockValues.ActionKinds.Attack;
    public const string Save = StatBlockValues.ActionKinds.Save;
    public const string AutoHit = StatBlockValues.ActionKinds.AutoHit;
    public const string Heal = StatBlockValues.ActionKinds.Heal;
    public const string Parry = StatBlockValues.ActionKinds.Parry;

    /// <summary>No effect in a fight without a grid, light, travel or time (Detect Magic, Misty Step, Mage Armor already in AC).</summary>
    public const string NoCombatEffect = "no_combat_effect";

    /// <summary>Changes a fight, but the simulator does not cast it (Counterspell, Mirror Image, Banishment): a warning on the caster.</summary>
    public const string NotModelled = "not_modelled";

    public static readonly IReadOnlyList<string> All = [Attack, Save, AutoHit, Heal, Parry, NoCombatEffect, NotModelled];
}
