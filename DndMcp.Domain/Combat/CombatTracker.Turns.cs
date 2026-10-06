using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;

namespace DndMcp.Domain.Combat;

public static partial class CombatTracker
{
    /// <summary>The note a server initiative roll for an enemy carries in a player campaign (D17).</summary>
    public const string RolledHereNote = "rolled here; give the DM's order as totals";

    // -------------------------------------------------------------------------------------------------------------------
    // initiative

    private sealed record InitiativePlan(
        IReadOnlyDictionary<string, InitiativeValue> Given,
        IReadOnlyList<(string Key, IReadOnlyList<CombatantState> Members, InitiativeRollPlan Roll)> ToRoll,
        IReadOnlySet<string> Surprised,
        IReadOnlyList<string> Left);

    private static IReadOnlyList<RollNeed> InitiativeNeeds(EncounterState state, InitiativeOp op) =>
        PlanInitiative(state, op).ToRoll.Select(r => new RollNeed(r.Key, r.Members[0].Id, P.Initiative, r.Roll.Roll.Expression)).ToList();

    private static InitiativePlan PlanInitiative(EncounterState state, InitiativeOp op)
    {
        RequireActive(state, "initiative");
        var surprised = new HashSet<string>(StringComparer.Ordinal);
        if (op.Surprised.Count > 0)
        {
            foreach (var c in CombatAddressing.ResolveMany(state.Combatants, op.Surprised, "surprised"))
            {
                surprised.Add(c.Id);
            }
        }

        // Every roll checked first, each one's problem reported (up to five).
        EachItem(op.Rolls.Count, "initiative", i =>
        {
            var roll = op.Rolls[i];
            var where = $"rolls item {N(i + 1)}";
            if ((roll.Face is null) == (roll.Total is null))
            {
                throw new DndInputException($"{where} gives {(roll.Face is null ? "neither" : "both")} face and total; give the kept d20 face OR the total.");
            }

            Range($"{where} face", roll.Face, 1, 20);
            if (roll.Total is { } total && (!double.IsFinite(total) || total < -50 || total > 100))
            {
                throw new DndInputException($"{where} total is not a number from -50 to 100.");
            }

            _ = CombatAddressing.Resolve(state.Combatants, roll.Combatant, where);
        });

        // Each given value, per combatant; an init group's members share one value (contract §5.11). A left combatant
        // takes none ("no effect: X left"; it re-joins with add).
        var perCombatant = new Dictionary<string, InitiativeValue>(StringComparer.Ordinal);
        var left = new List<string>();
        for (var i = 0; i < op.Rolls.Count; i++)
        {
            var roll = op.Rolls[i];
            var where = $"rolls item {N(i + 1)}";
            var c = CombatAddressing.Resolve(state.Combatants, roll.Combatant, where);
            if (c.Removed)
            {
                left.Add(c.Name);
                continue;
            }

            var value = new InitiativeValue(roll.Face, roll.Total);
            var members = c.InitGroup is { } group ? state.Combatants.Where(m => m.InitGroup == group).ToList() : [c];
            foreach (var member in members)
            {
                if (perCombatant.TryGetValue(member.Id, out var already) && already != value)
                {
                    throw new DndInputException(
                        $"{where} gives {c.Name} a different initiative than another entry gave {(member.Id == c.Id ? c.Name : $"its group ({member.Name})")}; " +
                        "identical monsters of one add share one value: give it once.");
                }

                perCombatant[member.Id] = value;
            }
        }

        // Everyone else without an initiative rolls: one roll per init group.
        var toRoll = new List<(string, IReadOnlyList<CombatantState>, InitiativeRollPlan)>();
        var handled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in state.Combatants)
        {
            if (handled.Contains(c.Id) || c.Removed || c.Dead || c.Initiative is not null || perCombatant.ContainsKey(c.Id))
            {
                continue;
            }

            var members = c.InitGroup is { } group
                ? state.Combatants.Where(m => m.InitGroup == group && !m.Removed && !m.Dead && m.Initiative is null && !perCombatant.ContainsKey(m.Id)).ToList()
                : [c];
            foreach (var m in members)
            {
                handled.Add(m.Id);
            }

            var plan = CombatRules.InitiativeRoll(
                c.InitBonus, state.Ruleset, c.Surprised || surprised.Contains(c.Id), c.Conditions.Select(x => x.Name), c.Exhaustion);
            toRoll.Add(($"initiative:{c.Id}", members, plan));
        }

        return new InitiativePlan(perCombatant, toRoll, surprised, left);
    }

    private static CombatStepResult Initiative(EncounterState state, InitiativeOp op, IReadOnlyDictionary<string, RolledValue> rolls)
    {
        var plan = PlanInitiative(state, op);
        var w = new CombatWork(state);
        foreach (var name in plan.Left)
        {
            w.Notes.Add($"no effect: {name} left");
        }

        foreach (var id in plan.Surprised)
        {
            var c = w.Get(id);
            if (!c.Surprised)
            {
                w.Put(c with { Surprised = true });
            }
        }

        foreach (var (id, value) in plan.Given)
        {
            var c = w.Get(id);
            var total = CombatRules.InitiativeTotal(value, c.InitBonus, c.Exhaustion, state.Ruleset);
            w.Put(c with { Initiative = total });
            var detail = new JsonObject { ["total"] = total, ["given"] = true };
            if (value.Face is { } face)
            {
                detail["face"] = face;
            }

            w.Log(L.Initiative, id, id, WholeOrNull(total), detail);
            w.Notes.Add($"{c.Name}: initiative {N(total)}{(value.Face is { } f ? $" ({N(f)} {Signed(c.InitBonus)}{ExhaustionText(c, state.Ruleset)})" : " (given)")}.");
        }

        foreach (var (key, members, roll) in plan.ToRoll)
        {
            var rolled = Rolled(rolls, key);
            var face = D20Face(rolled, key);
            var first = members[0];
            var total = CombatRules.InitiativeTotal(new InitiativeValue(Face: face), first.InitBonus, first.Exhaustion, state.Ruleset);
            var detail = new JsonObject { ["face"] = face, ["total"] = total, ["expression"] = roll.Roll.Expression };
            if (roll.Advantage.Count > 0)
            {
                detail["advantage"] = new JsonArray(roll.Advantage.Select(a => (JsonNode)a).ToArray());
            }

            if (roll.Disadvantage.Count > 0)
            {
                detail["disadvantage"] = new JsonArray(roll.Disadvantage.Select(a => (JsonNode)a).ToArray());
            }

            var playerEnemy = state.PlayerCampaign && first.Side is CampaignValues.CombatSides.Enemy or CampaignValues.CombatSides.Neutral;
            if (playerEnemy)
            {
                detail["note"] = RolledHereNote;
            }

            foreach (var member in members)
            {
                w.Put(w.Get(member.Id) with { Initiative = total });
                w.Log(L.Initiative, member.Id, member.Id, WholeOrNull(total), detail.DeepClone().AsObject(), key);
            }

            var who = members.Count == 1 ? first.Name : string.Join(", ", members.Select(m => m.Name));
            w.Notes.Add($"{who}: initiative {N(total)} (rolled {roll.Roll.Expression}: {N(face)}){(playerEnemy ? $" — {RolledHereNote}" : string.Empty)}.");
            foreach (var reminder in roll.Reminders)
            {
                w.Notes.Add($"{first.Name}: {reminder}.");
            }
        }

        // 2024 surprise is Disadvantage on the initiative roll and nothing else (review C14): once a combatant has its
        // initiative, the flag has done its work, and kept it read as "still surprised" in every table of the fight.
        if (w.Is2024)
        {
            foreach (var c in w.All.Where(c => c.Surprised && c.Initiative is not null))
            {
                w.Put(c with { Surprised = false });
            }
        }

        foreach (var tie in CombatOrder.Ties(w.All))
        {
            var names = tie.Ids.Select(id => w.Get(id).Name).ToList();
            w.Remind(
                K.Tie,
                tie.Ids[0],
                $"tied at {N(tie.Total)}: {string.Join(", ", names)} — give totals such as {N(tie.Total + 0.5)} to reorder",
                CombatCalls.Combat("initiative", ("rolls", new[] { CombatCalls.Object(("combatant", w.Get(tie.Ids[0]).Address), ("total", tie.Total + 0.5)) })));
        }

        if (state.Round == 0)
        {
            CombatTurns.BeginRoundOne(w);
            w.Log(L.Turn, w.Turn, null, null, CombatTurns.TurnDetail(w, null, 0, start: true));
        }

        return w.Finish();
    }

    // -------------------------------------------------------------------------------------------------------------------
    // surprise (start's surprised)

    private static CombatStepResult Surprise(EncounterState state, SurpriseOp op)
    {
        RequireNotEnded(state, "surprised");
        var w = new CombatWork(state);
        foreach (var c in CombatAddressing.ResolveMany(state.Combatants, op.Targets, "surprised"))
        {
            if (c.Removed)
            {
                w.Notes.Add($"no effect: {c.Name} left");
                continue;
            }

            if (!c.Surprised)
            {
                w.Put(w.Get(c.Id) with { Surprised = true });
                w.Log(L.Initiative, w.Turn, c.Id, null, new JsonObject { ["surprised"] = true });
            }

            w.Notes.Add(state.Ruleset == DslValues.Editions.E2014
                ? $"{c.Name} is surprised: it loses its first turn."
                : $"{c.Name} is surprised: Disadvantage on its initiative roll.");
        }

        return w.Finish();
    }

    // -------------------------------------------------------------------------------------------------------------------
    // next

    private static CombatStepResult Next(EncounterState state, NextOp op)
    {
        RequireActive(state, "next");
        if (state.Round < 1)
        {
            throw new DndInputException("next is refused before the first initiative: roll initiative first (combat {\"action\": \"initiative\"}).");
        }

        if (op.From is { } from)
        {
            var named = CombatAddressing.Resolve(state.Combatants, from, "from");
            if (named.Id != state.TurnCombatantId)
            {
                throw new DndInputException($"from names {named.Name}, but the turn already moved to {state.TurnHolder?.Name ?? "nobody"}; nothing was changed.");
            }
        }

        var w = new CombatWork(state) { TrackRestore = true };
        var roundBefore = w.Round;
        var previous = w.Turn;
        if (previous is not null && w.Find(previous) is not null)
        {
            CombatTurns.EndOfTurn(w, previous, place: false);
        }

        CombatTurns.Advance(w);
        w.Log(L.Turn, w.Turn, null, null, CombatTurns.TurnDetail(w, previous, roundBefore));
        w.Notes.Add($"{w.Name(w.Turn)}'s turn (round {N(w.Round)}).");
        return w.Finish();
    }

    // -------------------------------------------------------------------------------------------------------------------
    // prev

    private static CombatStepResult Prev(EncounterState state, PrevOp op)
    {
        RequireActive(state, "prev");
        if (state.Round < 1)
        {
            throw new DndInputException("prev is refused before the first initiative: there is no turn to go back to.");
        }

        var w = new CombatWork(state);
        var current = w.Turn;
        var last = op.LastChange is { Kind: L.Turn } change ? CombatJson.ReadTurn(change.Detail) : null;
        bool exact;
        // Exact only when the last row is a next's turn row for this very turn, back to a combatant still in the fight (a
        // leave of the turn-holder also writes a turn row, from the one who left).
        if (last is { Prev: false, Start: false, From: { } lastFrom } && last.To == state.TurnCombatantId && last.Round == state.Round
            && state.Find(lastFrom) is { Removed: false })
        {
            foreach (var restore in last.Restore)
            {
                if (w.Find(restore.CombatantId) is { } c)
                {
                    w.Put(c with
                    {
                        Conditions = restore.Conditions,
                        Concentration = restore.Concentration,
                        Legendary = restore.Legendary,
                        ReactionUsed = restore.ReactionUsed,
                        Surprised = restore.Surprised,
                    });
                }
            }

            w.Turn = lastFrom;
            w.Round = last.RoundBefore;
            exact = true;
            var undone = last.Changes.Count == 0 ? "it made no automatic changes" : "its automatic changes were undone: " + string.Join("; ", last.Changes);
            w.Notes.Add($"Back to {w.Name(w.Turn)}'s turn (round {N(w.Round)}); {undone}.");
        }
        else
        {
            var (id, round) = PreviousTurn(state);
            w.Turn = id;
            w.Round = round;
            exact = false;
            w.Notes.Add(
                $"Back to {w.Name(w.Turn)}'s turn (round {N(w.Round)}). Only the turn moved: changes made since the last next were kept, " +
                "and so were that next's automatic changes (expiries, resets); correct any with its own call.");
        }

        var detail = (JsonObject)JsonNode.Parse(CombatJson.WriteTurn(new TurnRecord
        {
            Prev = true,
            Exact = exact,
            From = current,
            To = w.Turn,
            RoundBefore = state.Round,
            Round = w.Round,
        }))!;
        w.Log(L.Turn, w.Turn, null, null, detail);
        return w.Finish();
    }

    /// <summary>The turn before the current one: the previous combatant in the order that takes turns, wrapping back a round.</summary>
    private static (string Id, int Round) PreviousTurn(EncounterState state)
    {
        var order = state.Order;
        var index = -1;
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i].Id == state.TurnCombatantId)
            {
                index = i;
            }
        }

        var round = state.Round;
        for (var steps = 0; steps <= order.Count; steps++)
        {
            index--;
            if (index < 0)
            {
                index = order.Count - 1;
                round--;
                if (round < 1)
                {
                    break;
                }
            }

            if (CombatOrder.TakesTurns(order[index]))
            {
                return (order[index].Id, round);
            }
        }

        throw new DndInputException("prev is refused at round 1 on the first turn: there is no earlier turn.");
    }

    private static int? WholeOrNull(double total) => total == Math.Floor(total) && Math.Abs(total) < int.MaxValue ? (int)total : null;

    private static string Signed(int value) => value >= 0 ? $"+ {N(value)}" : $"− {N(-value)}";

    private static string ExhaustionText(CombatantState c, string edition) =>
        CombatRules.ExhaustionD20Penalty(c.Exhaustion, edition) is > 0 and var penalty ? $" − {N(penalty)} exhaustion" : string.Empty;
}
