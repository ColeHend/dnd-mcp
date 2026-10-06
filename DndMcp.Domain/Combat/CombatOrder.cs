using DndMcp.Domain.Rules;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The turn order (contract D14): every combatant with an initiative, by <see cref="CombatRules.OrderInitiative"/> (total
/// descending, then initiative bonus, then insertion order; an init group never split). Left and dead combatants keep
/// their places in it: they take no turn, but the expiries they anchor still fire where their turns would be (§6.2,
/// §6.3), as the simulator's dead creatures keep their places.
/// </summary>
internal static class CombatOrder
{
    /// <summary>The order of <paramref name="combatants"/> that have an initiative.</summary>
    public static IReadOnlyList<CombatantState> Of(IEnumerable<CombatantState> combatants)
    {
        var withInitiative = combatants.Where(c => c.Initiative is not null).ToList();
        if (withInitiative.Count == 0)
        {
            return [];
        }

        var byId = withInitiative.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var order = CombatRules.OrderInitiative(withInitiative.Select(Entry));
        return order.Order.Select(e => byId[e.Id]).ToList();
    }

    /// <summary>The ties to flag: equal totals between different combatants (not init-group mates) that have not left.</summary>
    public static IReadOnlyList<InitiativeTie> Ties(IEnumerable<CombatantState> combatants) =>
        CombatRules.OrderInitiative(combatants.Where(c => c.Initiative is not null && !c.Removed).Select(Entry)).Ties;

    /// <summary>R's entry for a combatant with an initiative.</summary>
    public static InitiativeEntry Entry(CombatantState c) => new(c.Id, c.Initiative!.Value, c.InitBonus, c.OrderKey, c.InitGroup);

    /// <summary>Whether it takes its turn when the order reaches it: in the order, not left, not passed over (D16).</summary>
    public static bool TakesTurns(CombatantState c) => c.Initiative is not null && !c.Skipped;
}
