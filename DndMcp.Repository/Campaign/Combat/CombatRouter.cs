using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Characters;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// The combat side of the D5 seam (<see cref="ICombatRouter"/>): while a character is a (not left) combatant of its
/// campaign's ACTIVE encounter, <c>campaign_character</c> <c>damage</c>, <c>heal</c>, <c>temp_hp</c>, <c>use</c> and
/// <c>condition</c> change the COMBATANT, exactly as the same <c>combat</c> step would (T's rules, unlogged: a combatant
/// row and combat_log rows in the writer's own transaction), and the sheet catches up once, at <c>end</c>. Without it a
/// heal on the sheet mid-fight would be written over by the write-back, or make <c>end</c> refuse for drift.
///
/// <para>
/// <b>A sheet-seeded combatant that LEFT is still routed</b> until the fight ends (F2, review CR07): <c>end</c> still
/// writes it back, so a heal on its sheet between <c>leave</c> and <c>end</c> used to be written over without a word. Its
/// action is applied exactly as before it left: the step runs with the combatant back in the fight for that step only
/// (the tracker answers "no effect: it left" to a step on a left combatant), and it is marked left again in the same
/// transaction; a rest is still refused. A left combatant from a stat block or with no sheet is not routed: the fight
/// never writes it back, so its sheet is the sheet.
/// </para>
///
/// <para>
/// <b>Only a SHEET-SEEDED combatant is routed</b> (D19): a character in the fight from a stat block (<c>srd</c>) is reported
/// (<see cref="RouteOutcomes.StatBlock"/>) and the writer acts on the sheet, which the fight never writes back; a character
/// in the fight with no sheet (<see cref="RouteOutcomes.InFight"/>) likewise. A <c>rest</c> is never applied: it is
/// reported so the writer can refuse it.
/// </para>
/// <para>
/// <b>A routed condition persists</b>: it is added with the sheet's default duration, <c>until_removed</c> (the writer
/// passes it), so the write-back keeps it on the sheet, where a <c>combat condition</c> would last the fight. It has no
/// source (<see cref="ConditionOp.NoSource"/>, review F2R09): nobody in the fight imposed it, and the turn-holder the
/// tracker defaults a source to was persisted as its poisoner. A routed <c>remove: ["concentration"]</c> (the sheet's way
/// to end a concentration) ends the combatant's concentration.
/// </para>
/// <para>
/// <b>Read inside the writer's transaction</b> (CRIT Q10): the encounter and its combatants are loaded with the given
/// transaction, so a fight that starts between a read and this write cannot be missed. Whether the character is in the
/// fight at all is asked first, with one query that reads no combatant state, and only then is the fight loaded: a
/// character who is not a combatant never depends on the fight's rows being readable. Nothing is rolled: a routed action
/// carries amounts, never dice. Refusals are the tracker's own (<see cref="DndInputException"/>: a dead combatant healed,
/// more slots than are left), and they roll the writer's batch back.
/// </para>
/// </summary>
public sealed class CombatRouter : ICombatRouter
{
    private readonly TimeProvider _time;

    /// <param name="time">
    /// The clock the combat_log rows are stamped with: the server registers the options' clock (the one campaigns.db stamps
    /// everything else with), a test its manual one; the system clock when none is given.
    /// </param>
    public CombatRouter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool TryRoute(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, string characterEntityId,
        CharacterAction action, out RoutedResult result)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentException.ThrowIfNullOrWhiteSpace(characterEntityId);
        ArgumentNullException.ThrowIfNull(action);
        result = null!;

        // Membership first, with one query that reads no combatant state: a character who is not in the fight is never
        // blocked by it (a fight with a row nobody can read refuses only the actions that would have to load it).
        if (!CombatStore.IsInActiveFight(connection, campaign.Id, characterEntityId, transaction) ||
            EncounterResolver.Active(connection, campaign.Id, transaction) is not { } row)
        {
            return false;
        }

        var state = CombatStore.Load(connection, campaign, row, transaction);
        if (state.Combatants.Where(c => c.EntityId == characterEntityId).OrderBy(c => c.Removed).FirstOrDefault(c => !c.Removed || c.IsSheetSeeded) is not { } combatant)
        {
            return false;
        }

        var left = combatant.Removed;
        if (!combatant.IsSheetSeeded || action.Kind == CharacterActions.Rest || !CharacterActions.Routable.Contains(action.Kind))
        {
            var outcome = combatant.StatBlock is not null ? RouteOutcomes.StatBlock : RouteOutcomes.InFight;
            result = new RoutedResult(outcome, row.Name, combatant.Name, [], [], left);
            return true;
        }

        // A left combatant takes the step as it would have before it left (class summary): back in the fight for the step.
        var at = CampaignDatabase.FormatTimestamp(_time.GetUtcNow());
        var before = left ? WithRemoved(state, combatant.Id, removed: false) : state;
        var address = Address(before, combatant);
        var run = new CombatRun(connection, transaction, campaign, row, before, at, CombatSubjects.None, roller: null);
        foreach (var op in Ops(action, address))
        {
            run.Apply(op);
        }

        if (left)
        {
            var again = WithRemoved(run.State, combatant.Id, removed: true);
            CombatStore.WriteCombatants(connection, transaction, run.State, again, [combatant.Id], at);
            run.Use(again);
        }

        var reminders = run.DistinctReminders()
            .Where(r => r.CombatantId == combatant.Id || r.CombatantId is null)
            .Select(r => new RoutedReminder(r.Kind, r.Text, r.Call))
            .ToList();
        result = new RoutedResult(RouteOutcomes.Applied, row.Name, combatant.Name, run.Lines.ToList(), reminders, left);
        return true;
    }

    // The state with one combatant's left flag set or cleared (a routed step on a combatant that left: class summary).
    private static EncounterState WithRemoved(EncounterState state, string combatantId, bool removed) =>
        state with { Combatants = state.Combatants.Select(c => c.Id == combatantId ? c with { Removed = removed } : c).ToList() };

    /// <summary>
    /// The tracker steps a routed action is (contract D5): one op, except a condition that also ends a concentration
    /// (<c>remove: ["concentration"]</c>, the sheet's spelling of a drop), which is that drop and then the rest.
    /// </summary>
    public static IReadOnlyList<CombatOp> Ops(CharacterAction action, string address)
    {
        ArgumentNullException.ThrowIfNull(action);
        IReadOnlyList<string> targets = [address];
        switch (action.Kind)
        {
            case CharacterActions.Damage:
                return [new DamageOp(targets) { Amount = action.Amount, DamageType = action.DamageType }];
            case CharacterActions.Heal:
                return [new HealOp(targets) { Amount = action.Amount }];
            case CharacterActions.TempHp:
                return [new HealOp(targets) { Amount = action.Amount, Temp = true }];
            case CharacterActions.Use:
                return [new UseOp(targets) { SlotLevel = action.SlotLevel, Pact = action.Pact, Resource = action.Resource, Amount = action.Amount ?? 1 }];
            case CharacterActions.Condition:
                var ops = new List<CombatOp>();
                var remove = action.Remove?.ToList() ?? [];
                if (remove.RemoveAll(IsConcentration) > 0)
                {
                    ops.Add(new ConcentrationOp(targets) { Drop = true });
                }

                var add = action.Add?.ToList() ?? [];
                if (add.Count > 0 || remove.Count > 0)
                {
                    ops.Add(new ConditionOp(targets)
                    {
                        Add = add.Count > 0 ? add : null,
                        Remove = remove.Count > 0 ? remove : null,
                        Level = action.Level,
                        Duration = add.Count > 0 ? action.Duration ?? CombatValues.Durations.UntilRemoved : null,
                        NoSource = add.Count > 0,
                    });
                }

                return ops;
            default:
                throw new ArgumentException($"The fight does not take {action.Kind}.", nameof(action));
        }
    }

    // The address that names exactly this combatant: its handle, else its tracker name (a typed name another combatant
    // shares cannot hide it).
    private static string Address(EncounterState state, CombatantState combatant)
    {
        foreach (var address in new[] { combatant.EntityHandle, combatant.Name, combatant.Address })
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            try
            {
                if (CombatAddressing.Resolve(state.Combatants, address, "character").Id == combatant.Id)
                {
                    return address;
                }
            }
            catch (DndInputException)
            {
                // Try the next spelling.
            }
        }

        throw new DndInputException($"{combatant.Name} cannot be addressed in the fight \"{state.Name}\": another combatant shares its name; rename one with combat set.");
    }

    private static bool IsConcentration(string? text) => CampaignText.Key(text ?? string.Empty) == "concentration";
}
