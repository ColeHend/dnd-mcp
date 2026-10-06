using DndMcp.Domain.Features;

namespace DndMcp.Domain.Rules;

/// <summary>
/// A death-save tally as the sheet and the combatant store it (<c>death_saves</c>:
/// <c>{"successes":0,"failures":0,"stable":false}</c>). Failures 3 means dead; hp 0 with fewer failures and not stable
/// means dying.
/// </summary>
public sealed record DeathSaveTally(int Successes, int Failures, bool Stable)
{
    /// <summary>No saves made, not stable: the state of a conscious creature, and of one that has just dropped.</summary>
    public static DeathSaveTally Zero { get; } = new(0, 0, false);

    /// <summary>Stable at 0 HP with both counts reset (SRD 5.1 "reset to zero when you … become stable").</summary>
    public static DeathSaveTally Stabilized { get; } = new(0, 0, true);
}

/// <summary>
/// The hit-point state one creature's rules read and write: a combatant's columns, or a sheet's. Every rule in
/// <see cref="CombatRules"/> takes one and returns the next, so the tracker (T), the sheet writer (C) and the simulator
/// seeding read and change hit points the same way; none of them re-derives the maximum (<see cref="EffectiveMaxHp"/> is
/// <see cref="HitPointMath.EffectiveMaxHp"/>).
/// </summary>
/// <remarks>
/// <para>
/// Hit points must be known: a combatant with unknown HP (contract D17: it accumulates <c>damage_taken</c>) or a sheet
/// with no <c>max_hp</c> is the caller's case, not a state.
/// </para>
/// <para>
/// <see cref="Dead"/> is an input because the combatant stores it; a sheet stores no such column, so its caller computes
/// it with <see cref="CombatRules.IsDead"/> (three failures, Exhaustion 6, or a maximum of 0).
/// </para>
/// </remarks>
public sealed record HitPointState
{
    /// <summary>"2014" or "2024" (<see cref="DslValues.Editions"/>): the fight's (or the sheet's) ruleset.</summary>
    public required string Edition { get; init; }

    /// <summary>Current hit points, 0 or more.</summary>
    public required int Hp { get; init; }

    /// <summary>The recorded maximum (<c>max_hp</c>), before any reduction or 2014 Exhaustion halving.</summary>
    public required int MaxHp { get; init; }

    /// <summary>The recorded maximum reduction (<c>max_hp_reduction</c>: Life Drain, mummy rot's withering).</summary>
    public int MaxHpReduction { get; init; }

    /// <summary>Temporary hit points, 0 or more: they absorb damage first and are never healed (contract §5.2).</summary>
    public int TempHp { get; init; }

    /// <summary>Exhaustion level 0-6 (a column, never a condition entry).</summary>
    public int Exhaustion { get; init; }

    /// <summary>
    /// The creature falls unconscious and makes death saves at 0 HP (a PC, a sheet-seeded combatant, one added with
    /// <c>death_saves: true</c>). Otherwise it dies at 0 HP (SRD 5.2 "Monster Death") unless it is knocked out. Either way
    /// a death interceptor of its stat block (<see cref="StatBlockFacts.DeathInterceptors"/>) may hold it at 0 first, as
    /// the simulator reads it (Regeneration at 0 only for a creature that does not make death saves).
    /// </summary>
    public bool MakesDeathSaves { get; init; }

    /// <summary>The death-save tally (<c>death_saves</c>); a creature that died is stored as (0, 3, not stable) when it makes death saves.</summary>
    public DeathSaveTally DeathSaves { get; init; } = DeathSaveTally.Zero;

    /// <summary>
    /// It is dead: nothing more changes it (damage has no effect, healing and temporary hit points are refused). An input:
    /// the combatant's <c>dead</c> column, or <see cref="CombatRules.IsDead"/> for a sheet.
    /// </summary>
    public bool Dead { get; init; }

    /// <summary>
    /// The 2024 knock-out: 1 HP and Unconscious, not dying, until it regains any hit points (or first aid). A 2014
    /// knock-out needs no flag: it is 0 HP and stable.
    /// </summary>
    public bool KnockedOut { get; init; }

    /// <summary>It is concentrating on something (damage may then call for a save).</summary>
    public bool Concentrating { get; init; }

    /// <summary>The maximum every rule caps and compares against (<see cref="HitPointMath.EffectiveMaxHp"/>).</summary>
    public int EffectiveMaxHp => HitPointMath.EffectiveMaxHp(MaxHp, MaxHpReduction, Exhaustion, Edition);

    /// <summary>At 0 HP, making death saves, not stable, not dead.</summary>
    public bool Dying => MakesDeathSaves && !Dead && Hp == 0 && !DeathSaves.Stable;

    /// <summary>2024 Bloodied (<see cref="CombatRules.IsBloodied"/>), at 0 HP too while it lives; always false in 2014 and for the dead.</summary>
    public bool Bloodied => !Dead && CombatRules.IsBloodied(Hp, EffectiveMaxHp, Edition);
}
