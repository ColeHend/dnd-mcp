using System.Text.Json.Nodes;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using E = DndMcp.Domain.Combat.CombatValues.ExpiryPoints;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using V = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The turn engine (contract §6.3): the end of a turn, the advance through the order (skipped places still firing the
/// expiries they anchor, the round wrapping once, the 2014 lair slot at initiative 20), and the start of a turn. Used by
/// <c>next</c>, by the first <c>initiative</c> (round 1's first turn) and by <c>leave</c> of the turn-holder.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order of processing</b> is the contract's: (1) the END of the current creature C's turn: <c>until_end_of_target_turn</c>
/// on C and <c>until_end_of_source_turn</c> sourced by C expire, unless applied during that very turn (<c>skip_end</c>,
/// cleared instead); C's save prompts; the 2014 surprised flag clears. (2) The advance: the next combatant in the order that
/// takes turns (D16); every place passed over still fires its start and end expiries; a wrap increments the round once and
/// expires <c>end_of_round</c> effects. (3) The START of the new creature N's turn: <c>until_start_of_source_turn</c>
/// sourced by N, <c>until_start_of_target_turn</c> on N and <c>rounds</c> effects anchored on N whose round has come
/// expire, and a concentration whose time ran out; N's reaction and legendary actions reset; recharge, regeneration,
/// death-save and 2014 surprise reminders.
/// </para>
/// <para>
/// Every change these make is AUTOMATIC: recorded as a line of the turn row and, with <see cref="CombatWork.TrackRestore"/>,
/// with the touched combatants' fields as they were, so <c>prev</c> can undo it exactly (FIX A28a).
/// </para>
/// </remarks>
internal static class CombatTurns
{
    /// <summary>The end of <paramref name="id"/>'s turn (or of its place, when it takes no turn: expiries only).</summary>
    public static void EndOfTurn(CombatWork w, string id, bool place)
    {
        foreach (var holder in w.All)
        {
            var kept = new List<CombatCondition>();
            var gone = new List<CombatCondition>();
            var changed = false;
            foreach (var condition in holder.Conditions)
            {
                var anchored = (condition.Duration == D.UntilEndOfTargetTurn && holder.Id == id) ||
                               (condition.Duration == D.UntilEndOfSourceTurn && condition.Source == id);
                if (!anchored)
                {
                    kept.Add(condition);
                }
                else if (condition.SkipEnd)
                {
                    kept.Add(condition with { SkipEnd = false });
                    changed = true;
                }
                else
                {
                    gone.Add(condition);
                    changed = true;
                }
            }

            if (!changed)
            {
                continue;
            }

            w.Put(holder with { Conditions = kept });
            foreach (var condition in gone)
            {
                w.Automatic(K.Expired, holder.Id, $"{w.Describe(condition)} ended on {holder.Name}");
            }
        }

        if (place)
        {
            return;
        }

        var c = w.Get(id);
        foreach (var condition in c.Conditions.Where(x => x.Save is not null))
        {
            var save = condition.Save!;
            w.Remind(
                K.SaveEnds,
                id,
                $"{c.Name}: {DslValues.Abilities.Display(save.Ability)} save DC {save.Dc} against {w.Describe(condition)} at the end of its turn; on a success it ends",
                CombatCalls.Combat("condition", ("targets", new[] { c.Address }), ("remove", new[] { condition.Name })));
        }

        if (c.Surprised && !w.Is2024)
        {
            w.Put(c with { Surprised = false });
            w.Automatic(K.Surprised, id, $"{c.Name} is no longer surprised (its first turn ended)");
        }
    }

    /// <summary>The start of <paramref name="id"/>'s turn in the current round (or of its place: expiries only).</summary>
    public static void StartOfTurn(CombatWork w, string id, bool place)
    {
        var round = w.Round;
        w.EndConditions(
            (holder, c) => (c.Duration == D.UntilStartOfSourceTurn && c.Source == id) ||
                           (c.Duration == D.UntilStartOfTargetTurn && holder.Id == id) ||
                           (c.Expires is { At: E.Start } e && e.Of == id && e.Round <= round),
            K.Expired,
            string.Empty,
            automatic: true);

        foreach (var holder in w.All.Where(c => c.Concentration?.Expires is { At: E.Start } e && e.Of == id && e.Round <= round))
        {
            w.EndConcentration(holder.Id, K.Expired, "its duration ran out", automatic: true);
        }

        if (place)
        {
            return;
        }

        var n = w.Get(id);
        if (n.ReactionUsed)
        {
            w.Put(n with { ReactionUsed = false });
            n = w.Get(id);
        }

        if (n.Legendary is { Actions: > 0 } legendary && !n.Dead)
        {
            if (legendary.Used > 0)
            {
                w.Put(n with { Legendary = legendary with { Used = 0 } });
                n = w.Get(id);
            }

            var was = legendary.Used > 0 ? $" (it had {legendary.ActionsLeft}/{legendary.Actions} left)" : string.Empty;
            w.Automatic(K.LegendaryReset, id, $"{n.Name}: legendary actions reset: {legendary.Actions}/{legendary.Actions}{was}");
        }

        foreach (var (key, resource) in n.Resources)
        {
            if (resource.Kind == CombatValues.ResourceKinds.Recharge && resource.Ready == false && !n.Dead)
            {
                var name = resource.Name ?? key[CombatValues.ResourceKeys.LimitedPrefix.Length..];
                var faces = resource.Min is { } min and < 6 ? $"{min}-6" : "6";
                w.Remind(
                    K.Recharge,
                    id,
                    $"{n.Name}: roll d6: {name} recharges on {faces}",
                    CombatCalls.Combat("use", ("targets", new[] { n.Address }), ("resource", name), ("amount", -1)));
            }
        }

        if (n.StatBlock is { } block && !n.Dead)
        {
            foreach (var trait in block.Traits.Where(t => t.Kind == V.TraitKinds.Regeneration))
            {
                w.Remind(
                    K.Regeneration,
                    id,
                    $"{n.Name} — {trait.Name}: {trait.Text}",
                    trait.Amount is { } amount ? CombatCalls.Combat("heal", ("targets", new[] { n.Address }), ("amount", amount)) : null);
            }
        }

        if (n.Dying)
        {
            w.Remind(
                K.DeathSaveDue,
                id,
                $"{n.Name}: death saving throw due ({CombatContext.Tallies(n.DeathSaves)})",
                CombatCalls.Combat("death_save", ("targets", new[] { n.Address }), ("face", CombatCalls.Fill)));
        }

        if (n.Surprised && !w.Is2024)
        {
            w.Remind(K.Surprised, id, $"{n.Name} is surprised: it can't move or take an action on this turn, and can't take a reaction until this turn ends");
        }
    }

    /// <summary>
    /// From the current turn-holder (whose END is already processed) to the next combatant that takes turns: places passed
    /// over fire their expiries, a wrap begins the next round, and the 2014 lair slot is reminded when passed; then the
    /// start of the new turn.
    /// </summary>
    /// <exception cref="DndInputException">No combatant can take a turn (every one is dead, defeated or left).</exception>
    public static void Advance(CombatWork w)
    {
        var order = w.Order;
        if (order.Count == 0)
        {
            throw new DndInputException("No combatant has an initiative: roll it with combat {\"action\": \"initiative\"}.");
        }

        var index = IndexOf(order, w.Turn);
        var lairGap = LairGap(w, order);
        for (var steps = 0; steps <= order.Count; steps++)
        {
            var nextIndex = index + 1;
            var wrapped = nextIndex >= order.Count;
            if (lairGap is { } gap && (wrapped ? gap == -1 : gap == index))
            {
                LairAction(w);
            }

            if (wrapped)
            {
                nextIndex = 0;
                var ending = w.Round;
                w.Round = ending + 1;
                w.EndConditions((_, c) => c.Expires is { At: E.RoundEnd } e && e.Round <= ending, K.Expired, $"round {ending} is over", automatic: true);
                w.Automatic(K.Round, null, $"Round {w.Round}");
            }

            var candidate = w.Get(order[nextIndex].Id);
            if (CombatOrder.TakesTurns(candidate))
            {
                w.Turn = candidate.Id;
                StartOfTurn(w, candidate.Id, place: false);
                return;
            }

            StartOfTurn(w, candidate.Id, place: true);
            EndOfTurn(w, candidate.Id, place: true);
            index = nextIndex;
        }

        throw new DndInputException("No combatant can take a turn (every one is dead, defeated or has left): end the fight with combat {\"action\": \"end\"}.");
    }

    /// <summary>
    /// Round 1 begins (D15): effects applied before initiative are anchored on the first creature to act, places before it
    /// fire their expiries, the lair slot is reminded when it comes first, and the first turn starts.
    /// </summary>
    public static void BeginRoundOne(CombatWork w)
    {
        var order = w.Order;
        var first = order.FirstOrDefault(CombatOrder.TakesTurns)
            ?? throw new DndInputException("No combatant can take a turn (every one is dead or has left).");
        w.Round = 1;
        foreach (var holder in w.All)
        {
            var anchored = holder.Conditions.Select(c => c.Expires is { At: E.Start, Of: null } e ? c with { Expires = e with { Of = first.Id } } : c).ToList();
            var concentration = holder.Concentration is { Expires: { At: E.Start, Of: null } ce } conc ? conc with { Expires = ce with { Of = first.Id } } : holder.Concentration;
            if (!anchored.SequenceEqual(holder.Conditions) || !ReferenceEquals(concentration, holder.Concentration))
            {
                w.Put(holder with { Conditions = anchored, Concentration = concentration });
            }
        }

        if (LairGap(w, order) == -1 && order[0].Initiative < CombatRules.LairActionInitiative)
        {
            LairAction(w);
        }

        foreach (var before in order.TakeWhile(c => c.Id != first.Id))
        {
            StartOfTurn(w, before.Id, place: true);
            EndOfTurn(w, before.Id, place: true);
        }

        w.Turn = first.Id;
        w.Automatic(K.Round, null, "Round 1");
        StartOfTurn(w, first.Id, place: false);
    }

    /// <summary>The turn row: what turn ended and began, and (for <c>prev</c>) the touched combatants' fields before.</summary>
    public static JsonObject TurnDetail(CombatWork w, string? from, int roundBefore, bool start = false) =>
        (JsonObject)JsonNode.Parse(CombatJson.WriteTurn(new TurnRecord
        {
            Start = start,
            From = from,
            To = w.Turn,
            RoundBefore = roundBefore,
            Round = w.Round,
            Wrapped = w.Round != roundBefore && !start,
            Restore = w.Restores,
            Changes = w.AutomaticLines,
        }))!;

    private static int IndexOf(IReadOnlyList<CombatantState> order, string? id)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The 2014 lair slot's reminder (§5.12), with the damage call of the lair's creature (review LR03): a lair action is
    /// rolled on nobody's turn, so a damage call with no source would be the turn-holder's roll. The source is the fight's
    /// one legendary creature still in it; with none or several, "…" for the caller to fill.
    /// </summary>
    private static void LairAction(CombatWork w)
    {
        var owners = w.All.Where(c => c.Legendary is not null && !c.Removed && !c.Dead).Take(2).ToList();
        w.Automatic(
            K.LairAction,
            null,
            "lair action (initiative 20, losing ties): give its damage \"source\", or the roll is the turn-holder's:",
            CombatTracker.OffTurnDamageCall(owners.Count == 1 ? owners[0].Address : null));
    }

    /// <summary>
    /// Where the 2014 lair slot (initiative 20, losing ties) sits: after the position whose total is 20 or more while the
    /// next one's is below 20, or -1 for the wrap (everyone 20 or more: the end of the round; nobody: its start). Null
    /// when there is no lair slot (not a 2014 lair).
    /// </summary>
    private static int? LairGap(CombatWork w, IReadOnlyList<CombatantState> order)
    {
        if (!w.Before.Lair || w.Is2024)
        {
            return null;
        }

        for (var i = 0; i + 1 < order.Count; i++)
        {
            if (order[i].Initiative >= CombatRules.LairActionInitiative && order[i + 1].Initiative < CombatRules.LairActionInitiative)
            {
                return i;
            }
        }

        return -1;
    }
}
