namespace DndMcp.Domain.Rules;

/// <summary>
/// One damage instance against one target: its parts and how the hit was made. "One instance" is one hit or one
/// creature's share of one effect (several damage types from one hit are ONE instance): one concentration save and one
/// death-save failure per instance (SRD 5.1 "a separate saving throw for each source of damage").
/// </summary>
public sealed record DamageRequest
{
    /// <summary>The instance's parts (one per damage type, or several of one type, which are summed before the adjustments).</summary>
    public required IReadOnlyList<DamageInstancePart> Parts { get; init; }

    /// <summary>A critical hit: two death-save failures at 0 HP, and no Undead Fortitude. The dice are the caller's (it doubles a server roll's dice).</summary>
    public bool Critical { get; init; }

    /// <summary>Magical damage (a spell, a magic weapon): a "nonmagical" stat block resistance or immunity does not apply.</summary>
    public bool Magical { get; init; }

    /// <summary>Silvered weapon damage ("nonmagical_not_silvered" does not apply). The tracker does not take it in v1; the rules mirror the simulator.</summary>
    public bool Silvered { get; init; }

    /// <summary>Adamantine weapon damage ("nonmagical_not_adamantine" does not apply). As <see cref="Silvered"/>.</summary>
    public bool Adamantine { get; init; }

    /// <summary>This target succeeded on a save for half: each type's damage is halved (round down) before the adjustments.</summary>
    public bool Half { get; init; }

    /// <summary>Take the damage as given: no resistance, immunity, vulnerability or Petrified (the half on a save still applies).</summary>
    public bool Raw { get; init; }

    /// <summary>
    /// The attacker knocks the target out instead of killing it (a melee attack; the caller says so). It applies only to
    /// damage that reduces the target from above 0 to 0, and INSTEAD of massive damage and of a monster's death.
    /// </summary>
    public bool KnockOut { get; init; }
}

/// <summary>What one adjustment did to one damage type, in pipeline order.</summary>
/// <param name="Kind"><see cref="DamageStepKinds"/>.</param>
/// <param name="Qualifier">The stat block qualifier of the entry that applied (<c>nonmagical</c>, …), or null.</param>
/// <param name="Source">
/// What granted it: an effect's name ("Rage"), <see cref="DamageAdjustments.SheetSource"/>,
/// <see cref="DamageAdjustments.PetrifiedSource"/>, the stat block's own words for an "other" qualifier, or null for a
/// stat block entry.
/// </param>
public sealed record DamageStep(string Kind, int Before, int After, string? Qualifier = null, string? Source = null);

/// <summary>The kinds of <see cref="DamageStep"/>, in the order the pipeline applies them.</summary>
public static class DamageStepKinds
{
    /// <summary>Halved (round down) for a successful save.</summary>
    public const string Half = "half";

    /// <summary>Immune: 0.</summary>
    public const string Immune = "immune";

    /// <summary>Resistant (or Petrified): halved once, round down.</summary>
    public const string Resistant = "resistant";

    /// <summary>Vulnerable: doubled once.</summary>
    public const string Vulnerable = "vulnerable";

    /// <summary>Every kind, in pipeline order.</summary>
    public static readonly IReadOnlyList<string> All = [Half, Immune, Resistant, Vulnerable];
}

/// <summary>One damage type's share of an instance: what was given (parts of one type summed), what was taken, and why.</summary>
/// <param name="DamageType">The canonical type, or null for untyped damage.</param>
/// <param name="Given">The parts of this type summed, each counted at 0 or more.</param>
/// <param name="Amount">What the target takes of this type after every step.</param>
public sealed record DamagePartOutcome(string? DamageType, int Given, int Amount, IReadOnlyList<DamageStep> Steps);

/// <summary>Why a creature died, on the wire (combat_log detail, the tracker's <c>died</c> reminder).</summary>
public static class DeathCauses
{
    /// <summary>A creature that does not make death saves dropped to 0 HP (SRD 5.2 "Monster Death").</summary>
    public const string ZeroHp = "zero_hp";

    /// <summary>Dropped to 0 with damage left over of at least its maximum (SRD 5.1 "Instant Death").</summary>
    public const string MassiveDamage = "massive_damage";

    /// <summary>Damage at 0 HP of at least its maximum, or damage to a monster already at 0.</summary>
    public const string DamageAtZero = "damage_at_zero";

    /// <summary>A third death-save failure (from a save or from damage at 0 HP).</summary>
    public const string DeathSaveFailures = "death_save_failures";

    /// <summary>Exhaustion level 6 (both editions).</summary>
    public const string Exhaustion = "exhaustion";

    /// <summary>The effective hit point maximum reached 0 (SRD 5.2 "Hit Point Maximum of 0"; 2014 per effect).</summary>
    public const string MaxHpZero = "max_hp_zero";

    /// <summary>Every cause.</summary>
    public static readonly IReadOnlyList<string> All = [ZeroHp, MassiveDamage, DamageAtZero, DeathSaveFailures, Exhaustion, MaxHpZero];
}

/// <summary>
/// What one damage instance did to one target: the new state, the per-type breakdown, and every flag the tracker turns
/// into a reminder or a combat_log detail (a drop, a death and its cause, a knock-out, a death interceptor, death-save
/// failures, a concentration save and its DC, Bloodied). Nothing here is decided by the caller afterwards: a tracker that
/// re-derived "did it die" from the numbers would disagree with the sheet on the edge cases this record settles.
/// </summary>
public sealed record DamageOutcome
{
    /// <summary>The target as it was.</summary>
    public required HitPointState Before { get; init; }

    /// <summary>The target now: what the tracker stores on the combatant, or the sheet writer on the sheet.</summary>
    public required HitPointState After { get; init; }

    /// <summary>One entry per damage type given, in first-appearance order.</summary>
    public required IReadOnlyList<DamagePartOutcome> Parts { get; init; }

    /// <summary>
    /// The damage after the adjustments and BEFORE temporary hit points absorb it: what the concentration DC and the
    /// damage-at-0 rules read (<see cref="RulingFlags.ConcentrationOnPreTempDamage"/>). 0 means nothing got through, and
    /// then nothing else happens (no save, no failure).
    /// </summary>
    public int Total { get; init; }

    /// <summary>Temporary hit points lost.</summary>
    public int TempAbsorbed { get; init; }

    /// <summary>Hit points lost (<c>Before.Hp − After.Hp</c>).</summary>
    public int HpLost { get; init; }

    /// <summary>Damage past 0 when it dropped (what massive damage compares with the maximum); 0 otherwise.</summary>
    public int Leftover { get; init; }

    /// <summary>
    /// It went from above 0 to 0 HP in this instance (a 2014 knock-out and an interception included; the 2024 knock-out
    /// leaves 1 HP). Never for damage taken already at 0.
    /// </summary>
    public bool Dropped { get; init; }

    /// <summary>The knock-out applied: 2014 0 HP, unconscious and stable; 2024 1 HP and unconscious, not dying.</summary>
    public bool KnockedOut { get; init; }

    /// <summary>
    /// It fell unconscious now: the caller adds Unconscious (duration <c>zero_hp</c>, or until healed or given first aid for
    /// a 2024 knock-out) and Prone (<c>until_stands</c>; Unconscious implies Prone in both editions). False for a creature
    /// already unconscious from a 2024 knock-out (<c>Before.KnockedOut</c>): its Unconscious stays, and when this damage
    /// dropped it to 0 (<c>After.KnockedOut</c> false, dying) that Unconscious now lasts until it regains hit points, as a
    /// <c>zero_hp</c> one does (first aid only stabilises it). False for an intercepted creature (held, not unconscious).
    /// </summary>
    public bool FellUnconscious { get; init; }

    /// <summary>It died of this damage (<see cref="DeathCause"/> says how); <c>After.Dead</c> is true.</summary>
    public bool Died { get; init; }

    /// <summary><see cref="DeathCauses"/> when <see cref="Died"/>.</summary>
    public string? DeathCause { get; init; }

    /// <summary>
    /// It dropped to 0 and a trait of its stat block may keep it alive (<see cref="Interceptors"/>), as the simulator reads
    /// it for any creature, death-save makers included: it is held at 0 HP, defeated (enemy side), NOT dead and not
    /// unconscious, until the table resolves the trait. The trait holds → set its HP to 1 (Undead Fortitude on a successful
    /// save, Relentless automatically; the concentration save of <see cref="ConcentrationDc"/> is still owed). It does not
    /// → apply <see cref="IfTraitFails"/>. Regeneration at 0 (the troll) is resolved at the start of its turn instead.
    /// </summary>
    public bool Intercepted { get; init; }

    /// <summary>The death interceptors that can apply to this drop (Undead Fortitude not on radiant or critical damage; Relentless within its threshold).</summary>
    public IReadOnlyList<DeathInterceptor> Interceptors { get; init; } = [];

    /// <summary>
    /// With <see cref="Intercepted"/>: the same damage with no trait applied, which is what happens when the trait does not
    /// save it (the engine's next branch): a creature that does not make death saves dies (<see cref="DeathCauses.ZeroHp"/>);
    /// a death-save maker falls unconscious and dying, or dies of massive damage when <see cref="Leftover"/> is at least
    /// its effective maximum; its concentration ends. Null when nothing intercepted the drop.
    /// </summary>
    public DamageOutcome? IfTraitFails { get; init; }

    /// <summary>Death-save failures this damage added at 0 HP (1, or 2 on a critical hit), else 0.</summary>
    public int DeathSaveFailures { get; init; }

    /// <summary>It was stable at 0 HP and the damage made it start dying again.</summary>
    public bool StableLost { get; init; }

    /// <summary>
    /// A concentration save is due against this DC: it is concentrating, took damage, and is still conscious, or it is held
    /// at 0 by Undead Fortitude or Relentless (the save is owed once the trait leaves it at 1 HP, as the simulator rolls it).
    /// </summary>
    public int? ConcentrationDc { get; init; }

    /// <summary>Its concentration ended without a save: it fell unconscious, was knocked out, dropped to 0 (not held by a trait), or died.</summary>
    public bool ConcentrationBroken { get; init; }

    /// <summary>
    /// 2024: it crossed from above half its effective maximum to half or less (a reminder at EACH crossing, contract §5.10),
    /// a drop to 0 included (dying, knocked out or held by a trait); never for a creature that died.
    /// </summary>
    public bool BecameBloodied { get; init; }

    /// <summary>Radiant damage got through (Undead Fortitude does not apply).</summary>
    public bool Radiant { get; init; }

    /// <summary>The target was already dead: nothing changed (the tracker says "no effect", it never refuses the whole call).</summary>
    public bool NoEffect { get; init; }

    /// <summary>The <see cref="RulingFlags"/> that decided something here.</summary>
    public IReadOnlyList<string> Rulings { get; init; } = [];

    /// <summary>"28 fire ×2 (vulnerable) = 56; 97 → 41": the per-type arithmetic, temporary HP and hit points, for the author view.</summary>
    public required string Arithmetic { get; init; }
}
