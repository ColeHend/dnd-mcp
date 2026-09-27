using System.ComponentModel;

namespace DndMcp.Domain.Features;

/// <summary>
/// One kind of attack a build makes. See <see cref="BuildSpec"/> for why every property is nullable and typed for
/// binding; the resolver (<see cref="BuildResolver"/>) applies the defaults and turns it into a <see cref="ResolvedAttack"/>
/// with every level-dependent value concrete.
/// </summary>
public sealed class AttackSpec
{
    [Description("Required. Unique in the build (case is ignored), at most 60 characters; modifiers name attacks by it.")]
    public string? Name { get; init; }

    [Description("Attacks of this kind per turn, 1-10, or a step map by level: {\"1\": 1, \"5\": 2} for Extra Attack. Default 1.")]
    public object? Count { get; init; }

    [Description(
        "\"action\" (default: part of the Attack action) or \"bonus_action\" (made every turn with the Bonus Action, e.g. " +
        "a 2014 offhand attack).")]
    public string? Action { get; init; }

    [Description("The attack roll. Default {\"ability\": \"str\", \"proficient\": true}.")]
    public ToHitSpec? ToHit { get; init; }

    [Description(
        "Required. Damage dice and whole numbers joined by + or -: \"2d6\", \"1d8+1\", \"1d10+1d6\" (no kh, r, ! or labels). " +
        "The ability modifier is added separately. Or a step map by level: {\"1\": \"1d6\", \"5\": \"2d6\"}.")]
    public object? Damage { get; init; }

    [Description(
        "acid, bludgeoning, cold, fire, force, lightning, necrotic, piercing, poison, psychic, radiant, slashing or " +
        "thunder. Leave out for typeless damage, which nothing resists.")]
    public string? DamageType { get; init; }

    [Description("Add the to_hit ability's modifier to damage. Default true (false for an offhand attack).")]
    public bool? AbilityToDamage { get; init; }

    [Description(
        "Any of melee, ranged, spell, heavy, light, finesse, two-handed, versatile, reach, thrown. Neither melee nor " +
        "ranged means melee. spell = a spell attack, not a weapon.")]
    public IReadOnlyList<string>? Properties { get; init; }

    [Description(
        "The extra attack from a Light weapon in the other hand: its damage gets no ability modifier unless negative " +
        "(fighting_style twf adds it). Default false.")]
    public bool? Offhand { get; init; }

    [Description("A 2024 weapon mastery: graze, vex, topple, sap, cleave, nick, push or slow.")]
    public string? Mastery { get; init; }

    [Description(
        "Cantrip scaling at character level 1/5/11/17: \"dice\" multiplies the damage dice by 1/2/3/4 (Fire Bolt), " +
        "\"beams\" multiplies count by 1/2/3/4 (Eldritch Blast).")]
    public string? Cantrip { get; init; }

    [Description("The first level this attack exists at, 1-20.")]
    public int? FromLevel { get; init; }

    [Description("The last level this attack exists at, 1-20.")]
    public int? UntilLevel { get; init; }
}

/// <summary>
/// How an attack roll's bonus is made up. <see cref="Total"/> exists for homebrew and stat-block attacks whose bonus is
/// known but not its parts; the ability is still needed then, for damage, Graze and Topple.
/// </summary>
public sealed class ToHitSpec
{
    [Description("str (default), dex, con, int, wis, cha or none: the modifier on the attack roll (and on damage).")]
    public string? Ability { get; init; }

    [Description("Add the proficiency bonus. Default true.")]
    public bool? Proficient { get; init; }

    [Description("A flat bonus, -20 to 20, e.g. 1 for a +1 weapon. Default 0.")]
    public int? Bonus { get; init; }

    [Description(
        "The whole attack bonus, -10 to 30, e.g. 7, instead of ability + proficiency + bonus (+ Archery). to_hit " +
        "modifiers still add to it.")]
    public int? Total { get; init; }
}
