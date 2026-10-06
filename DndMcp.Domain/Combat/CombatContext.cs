using DndMcp.Domain.Campaign;
using DndMcp.Domain.Rules;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The reminders every step's result carries while it is N's turn (contract §6.3, "Every step's result during N's turn"):
/// what N's conditions and exhaustion do, what ends at the end of N's turn, every other combatant dying at 0 HP, and every
/// other legendary creature that may act when N's turn ends; plus, whatever the turn, the combatants still without an
/// initiative and "all enemies are defeated". They are recomputed from the state each time, so the same state always
/// reminds the same things, and a reminder never outlives what it is about.
/// </summary>
public static class CombatContext
{
    /// <summary>The context reminders for <paramref name="state"/>.</summary>
    public static IReadOnlyList<CombatReminder> Reminders(EncounterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var list = new List<CombatReminder>();
        var edition = state.Ruleset;
        if (state.Round >= 1 && state.TurnHolder is { } n)
        {
            foreach (var condition in n.Conditions.Where(c => c.IsSrdCondition && !string.Equals(c.Name, C.Exhaustion, StringComparison.Ordinal)))
            {
                list.Add(new CombatReminder(K.ConditionEffects, n.Id, ConditionLine(state, n, condition, edition)));
            }

            if (n.Exhaustion > 0)
            {
                list.Add(new CombatReminder(K.ExhaustionEffects, n.Id, $"{n.Name} — {CombatRules.Exhaustion(n.Exhaustion, edition).Text}"));
            }

            foreach (var holder in state.Combatants)
            {
                foreach (var condition in holder.Conditions)
                {
                    var endsNow = !condition.SkipEnd &&
                        ((condition.Duration == D.UntilEndOfTargetTurn && holder.Id == n.Id) ||
                         (condition.Duration == D.UntilEndOfSourceTurn && condition.Source == n.Id));
                    if (endsNow)
                    {
                        list.Add(new CombatReminder(K.Expiring, holder.Id, $"{Describe(state, condition)} on {holder.Name} ends at the end of this turn"));
                    }
                }
            }

            foreach (var other in state.Combatants.Where(c => c.Id != n.Id && c.Dying && !c.Removed))
            {
                list.Add(new CombatReminder(K.Dying, other.Id, $"{other.Name}: 0 HP, {Tallies(other.DeathSaves)}"));
            }

            foreach (var legendary in state.Combatants.Where(c => c.Id != n.Id && LegendaryAvailable(state, c)))
            {
                var l = legendary.Legendary!;
                list.Add(new CombatReminder(
                    K.LegendaryAvailable,
                    legendary.Id,
                    $"when {n.Name}'s turn ends, {legendary.Name} may take a legendary action ({l.ActionsLeft}/{l.Actions}), before next",
                    CombatCalls.Combat("legendary", ("source", legendary.Address), ("amount", 1), ("name", CombatCalls.Fill))));
            }
        }

        if (state.Round >= 1)
        {
            foreach (var waiting in state.Combatants.Where(c => c.Initiative is null && !c.Removed && !c.Dead))
            {
                list.Add(new CombatReminder(
                    K.NoInitiative,
                    waiting.Id,
                    $"{waiting.Name} has no initiative and takes no turn until it has one",
                    CombatCalls.Combat("initiative", ("rolls", new[] { CombatCalls.Object(("combatant", waiting.Address), ("total", CombatCalls.Fill)) }))));
            }
        }

        var enemies = state.Combatants.Where(c => c.Side == CampaignValues.CombatSides.Enemy).ToList();
        if (enemies.Count > 0 && enemies.All(e => e.Defeated || e.Removed) && enemies.Any(e => e.Defeated))
        {
            list.Add(new CombatReminder(K.AllEnemiesDown, null, "all enemies are defeated: end the combat?", CombatCalls.Combat("end", ("outcome", CombatCalls.Fill))));
        }

        return list;
    }

    /// <summary>
    /// Whether a legendary creature may spend a legendary action now (contract §5.12): it has uses left and is not
    /// incapacitated, dead, defeated or left, and (2014) is not still surprised before its first turn. Its own turn is the
    /// caller's check.
    /// </summary>
    public static bool LegendaryAvailable(EncounterState state, CombatantState c) =>
        c.Legendary is { Actions: > 0 } l && l.ActionsLeft > 0 && !c.Incapacitated && !c.Dead && !c.Defeated && !c.Removed &&
        !(c.Surprised && state.Ruleset == Features.DslValues.Editions.E2014);

    /// <summary>"Lieutenant James Torch is frightened (Mummy): disadvantage on attack rolls …".</summary>
    public static string ConditionLine(EncounterState state, CombatantState holder, CombatCondition condition, string edition)
    {
        var source = CombatConditions.SourceDependent.Contains(condition.Name) ? SourceName(state, condition) : null;
        var effects = CombatConditions.Effects(condition.Name, edition);
        return $"{holder.Name} is {condition.Name}{(source is null ? string.Empty : $" ({source})")}: {effects}";
    }

    /// <summary>
    /// The death-save procedure the tracker applies (contract §5.5-§5.6), worded per edition, for the reminder of a creature
    /// that falls dying (review U05): the 2024 rules' procedure is in the SRD 5.2.1 "Playing the Game" chapter, which this
    /// server's data lacks, so without it the model could only say "not in this server's data" about rules the tracker
    /// enforces. Each clause is one the tracker applies: the DC, the tallies, the naturals, damage at 0 HP, the exhaustion
    /// modifier (2014 Disadvantage at 3, 2024 −2 per level) and massive damage.
    /// </summary>
    public static string DeathSaveProcedure(string edition) => edition == Features.DslValues.Editions.E2024
        ? "Death Saving Throws at the start of each of its turns: 10 or more succeeds (−2 per Exhaustion level); 3 successes, Stable; 3 failures, " +
          "dead; a 20 regains 1 Hit Point; a 1 counts as two failures; damage at 0 Hit Points is a failure, a Critical Hit two (and damage of at " +
          "least its Hit Point maximum kills)."
        : "Death saving throws at the start of each of its turns: 10 or more succeeds (with Disadvantage from exhaustion 3); 3 successes, stable; " +
          "3 failures, dead; a 20 regains 1 hit point; a 1 counts as two failures; damage at 0 HP is a failure, a critical hit two (and damage of " +
          "at least its hit point maximum kills).";

    /// <summary>"0 successes, 2 failures" (and "stable").</summary>
    public static string Tallies(DeathSaveTally t) =>
        $"{t.Successes} success{(t.Successes == 1 ? string.Empty : "es")}, {t.Failures} failure{(t.Failures == 1 ? string.Empty : "s")}{(t.Stable ? ", stable" : string.Empty)}";

    /// <summary>"frightened (Mummy)": the condition and its source's tracker name or note.</summary>
    public static string Describe(EncounterState state, CombatCondition condition) =>
        SourceName(state, condition) is { } source ? $"{condition.Name} ({source})" : condition.Name;

    private static string? SourceName(EncounterState state, CombatCondition condition) =>
        condition.Source is { } id && state.Find(id) is { } s ? s.Name : condition.SourceNote;
}
