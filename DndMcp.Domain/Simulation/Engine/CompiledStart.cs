namespace DndMcp.Domain.Simulation;

/// <summary>
/// A <see cref="CombatantStart"/> compiled against its creature's template (<see cref="StartPreparation.Compile"/>):
/// names resolved to state slots, conditions to shared <see cref="ConditionTemplate"/>s, sources to creature ids. It
/// sits on <see cref="CombatantTemplate.Start"/>, read-only across every thread, and <see cref="Fight"/> sets each fight's
/// creature from it without drawing a die (<c>Fight.ApplyStart</c>).
/// </summary>
internal sealed class CompiledStart
{
    /// <summary>Hit points at the start (the maximum stays the template's).</summary>
    public required int Hp { get; init; }

    /// <summary>Temporary hit points, replacing the build's start-of-fight grant; null keeps the grant.</summary>
    public int? TempHp { get; init; }

    /// <summary>Kept only for its place in the order: dead the whole fight, out of the outcomes and the report.</summary>
    public bool Placeholder { get; init; }

    /// <summary>At 0 HP and not a placeholder: down (a PC-like creature dying or stable, or a troll waiting to regenerate).</summary>
    public bool Down { get; init; }

    public bool Stable { get; init; }

    public int DeathSuccesses { get; init; }

    public int DeathFailures { get; init; }

    public int Exhaustion { get; init; }

    /// <summary>What it concentrates on, or null.</summary>
    public StartConcentration? Concentration { get; init; }

    public StartConditionEntry[] Conditions { get; init; } = [];

    /// <summary>Per build resource (<see cref="PcBuild.Resources"/> index): uses left.</summary>
    public (int Slot, int Uses)[] PcUses { get; init; } = [];

    /// <summary>Per limited slot (<see cref="CombatantTemplate.Limited"/> index): per-day uses left.</summary>
    public (int Slot, int Uses)[] LimitedUses { get; init; } = [];

    /// <summary>Limited slots (recharge) that start spent.</summary>
    public int[] Spent { get; init; } = [];

    /// <summary>Per spell-slot pool (<see cref="CombatantTemplate.PoolNames"/> index): slots left.</summary>
    public (int Pool, int Slots)[] Pools { get; init; } = [];

    public int? LegendaryActionsLeft { get; init; }

    public int? LegendaryResistanceLeft { get; init; }

    /// <summary>Build modifier numbers whose setup is paid (not the concentration modifier).</summary>
    public int[] ActiveSetups { get; init; } = [];

    /// <summary>Build modifier numbers it cannot set up or cast this fight (<see cref="CombatantStart.Unavailable"/>).</summary>
    public int[] Unavailable { get; init; } = [];

    /// <summary>Build attack indexes (<see cref="PcBuild.Attacks"/>) it cannot make this fight (<see cref="CombatantStart.Unavailable"/>).</summary>
    public int[] UnavailableAttacks { get; init; } = [];

    public bool HasActed { get; init; }

    public bool ReactionUsed { get; init; }

    public bool RelentlessUsed { get; init; }

    /// <summary>The uses this creature starts with, for "resources used" (the fresh counts otherwise).</summary>
    public int PcUsesOf(int slot, int fresh) => Find(PcUses, slot) ?? fresh;

    /// <summary>The per-day uses this creature starts with.</summary>
    public int LimitedUsesOf(int slot, int fresh) => Find(LimitedUses, slot) ?? fresh;

    /// <summary>The slots this creature starts with in a pool.</summary>
    public int PoolSlotsOf(int pool, int fresh) => Find(Pools, pool) ?? fresh;

    private static int? Find((int Key, int Value)[] values, int key)
    {
        foreach (var (k, v) in values)
        {
            if (k == key)
            {
                return v;
            }
        }

        return null;
    }
}

/// <summary>
/// A seeded concentration: its label, the build modifier it is (0: none of the build's — a spell the build does not model,
/// or a monster's), and whether losing it switches that modifier off (as the engine starts each kind: a setup or a
/// no-setup rider like Hex does, a cast save effect does not).
/// </summary>
internal sealed record StartConcentration(string Label, int Modifier, bool Deactivates);

/// <summary>
/// One seeded condition: its compiled template, its source creature (−1: none), whether its source's concentration holds
/// it, and the turn ends (until-end kinds) or turn starts (until-start kinds) of its anchor to skip — 1 for one imposed
/// during the resumed turn whose anchor is the turn-holder (<see cref="StartCondition.ImposedDuringResumedTurn"/>).
/// </summary>
internal sealed record StartConditionEntry(ConditionTemplate Template, int Source, bool Held, int SkipTurnEnds, int SkipTurnStarts);
