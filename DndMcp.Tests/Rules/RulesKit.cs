using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Srd.Combatants;

namespace DndMcp.Tests.Rules;

/// <summary>Small builders for the shared-rules tests: a hit-point state, a damage instance, a real SRD stat block.</summary>
internal static class RulesKit
{
    public static HitPointState State(
        int hp, int max, string edition = "2024", int temp = 0, bool pc = true, int exhaustion = 0, int reduction = 0,
        DeathSaveTally? saves = null, bool concentrating = false, bool knockedOut = false, bool dead = false) => new()
    {
        Edition = edition,
        Hp = hp,
        MaxHp = max,
        TempHp = temp,
        MakesDeathSaves = pc,
        Exhaustion = exhaustion,
        MaxHpReduction = reduction,
        DeathSaves = saves ?? DeathSaveTally.Zero,
        Concentrating = concentrating,
        KnockedOut = knockedOut,
        Dead = dead,
    };

    /// <summary>A dying creature at 0 HP with <paramref name="failures"/> failures.</summary>
    public static HitPointState Dying(int max, string edition = "2024", int successes = 0, int failures = 0, int temp = 0, int exhaustion = 0) =>
        State(0, max, edition, temp, exhaustion: exhaustion, saves: new DeathSaveTally(successes, failures, false));

    public static DamageRequest Hit(params (int Amount, string? Type)[] parts) =>
        new() { Parts = parts.Select(p => new DamageInstancePart(p.Amount, p.Type)).ToList() };

    public static DamageRequest Hit(int amount, string? type = "slashing") => Hit((amount, type));

    /// <summary>A real stat block, from the shipped corrected data through the shipped normalizer.</summary>
    public static StatBlock SrdBlock(string edition, string slug) => CorrectedSrd.Shipped.StatBlock(edition, slug);

    /// <summary>A monster's state at <paramref name="hp"/> of its stat block's hit points.</summary>
    public static HitPointState Monster(StatBlock block, int? hp = null) => State(hp ?? block.HitPoints, block.HitPoints, block.Edition, pc: false);
}
