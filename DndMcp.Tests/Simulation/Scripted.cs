using DndMcp.Domain.Simulation;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Scripted fights for the rules tests: a spec compiled exactly as the simulator compiles it, set up with
/// <see cref="Fight.Begin"/> (reset, start of fight, initiative) and then driven one turn or one damage instance at a
/// time through the engine's own methods.
/// </summary>
internal static class Scripted
{
    public static Fight Begin(SimulationSpec spec, ulong seed = 1)
    {
        var fight = new Fight(SimulationPreparation.Prepare(spec).Setup);
        fight.Begin(seed);
        return fight;
    }

    public static Fight Begin(IReadOnlyList<SimulationCombatant> party, IReadOnlyList<SimulationCombatant> enemies, ulong seed = 1, string? edition = null, PolicySpec? policies = null) =>
        Begin(SimKit.Spec(party, enemies, edition: edition, policies: policies), seed);

    /// <summary>One instance of <paramref name="amount"/> damage of one type (null: typeless).</summary>
    public static int[] Damage(int amount, string? type = "slashing")
    {
        var perType = new int[DamageTypes.Count];
        perType[DamageTypes.Of(type)] = amount;
        return perType;
    }

    public static Creature Named(this Fight fight, string label) => fight.Creatures.Single(c => c.Label == label);
}
