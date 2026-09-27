using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using DndMcp.Domain.Probability;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// The exact per-turn dynamic programme (contract §4.2, research A3) for one advantage sample and one power-attack
/// choice: <see cref="Value"/> is the backward pass (expected values; every decision made there), and
/// <see cref="Distribution"/> the second pass that follows those decisions to the exact damage distribution.
///
/// <para>
/// <b>Why two passes.</b> Decisions are made on expected values (contract: "Decisions are made on expected values; the
/// forward pass then yields the exact distribution under the chosen policy"), so the first pass needs no distributions
/// at all — the fight horizon and a level × AC grid run on it alone. The second pass convolves damage PMFs only along
/// the branches actually chosen, and only when a caller asks for the round-1 distribution. It walks the same states
/// backward rather than forward, which is equally exact and simpler: the damage still to come depends only on the state,
/// so each state's distribution of remaining damage is built once (Σ over its branches of P(branch) · (branch damage ⊛
/// the next state's distribution)) and shared by every parent, where a forward pass would carry a sub-distribution of
/// damage so far in each state and merge them.
/// </para>
/// <para>
/// <b>Memoised by state</b> (<see cref="TurnState"/>): the rest of a turn depends only on the state, so each state is
/// valued once and shared by every history that reaches it — a first-hit rider held through a miss and one held
/// through a normal hit meet in the same state. States are shared across rounds and carried states too, since the start
/// of a turn is just another state.
/// </para>
/// <para>
/// <b>Decisions.</b> On each hit the optional choices (riders, Savage Attacker, conditions) with a fixed policy are
/// applied by rule — any_hit, crits_only, crit_or_last (a normal hit counts only on the phase's last attack the choice
/// could apply to) — at most one Bonus-Action-costing rider per turn, the first listed. The optimal ones are chosen
/// jointly by trying every subset (smallest first) and keeping the best E[damage] − use_value × uses from here to the
/// end of the turn, a tie keeping the smaller subset: ties do not spend. The Bonus Action takes the option with the best
/// objective, a tie going to the first in plan order and "none" last.
/// </para>
/// </summary>
internal sealed class TurnEvaluator
{
    private readonly TurnPlan _plan;
    private readonly DamageModel _damage;
    private readonly IReadOnlyList<SaveEffectDamage> _saves;
    private readonly TallyLayout _layout;
    private readonly ulong _present;
    private readonly int _powerOn;
    private readonly bool _trackCarry;
    private readonly WorkMeter _meter;
    private readonly Pmf<double>? _saveDice;
    private readonly int _stateLimit;

    private readonly Dictionary<TurnState, NodeValue> _values = [];
    private readonly Dictionary<(TurnState After, int Line, bool Crit), ulong> _hitChoices = [];
    private readonly Dictionary<TurnState, int> _bonusActionChoices = [];
    private readonly Dictionary<TurnState, Pmf<double>> _distributions = [];
    private readonly Dictionary<(int Line, D20Mode Mode, bool AutoCrit), AttackRollOdds> _odds = [];
    private readonly Dictionary<(string Ability, int Dc, bool Magical, int Conditions), double> _saveFails = [];
    private readonly Dictionary<(int Line, bool Crit, ulong Riders, bool Savage, int PowerBonus, bool NonZero), Pmf<double>> _conditional = [];

    public TurnEvaluator(
        TurnPlan plan,
        DamageModel damage,
        IReadOnlyList<SaveEffectDamage> saves,
        TallyLayout layout,
        ulong advantageSourcesPresent,
        int powerAttacksOn,
        bool trackCarry,
        Pmf<double>? saveDice,
        int stateLimit)
    {
        _plan = plan;
        _damage = damage;
        _saves = saves;
        _layout = layout;
        _present = advantageSourcesPresent;
        _powerOn = powerAttacksOn;
        _trackCarry = trackCarry;
        _meter = plan.Meter;
        _saveDice = saveDice;
        _stateLimit = stateLimit;
    }

    public int StateCount => _values.Count;

    /// <summary>The power attacks switched on in this evaluator (bits over the plan's list).</summary>
    public int PowerAttacksOn => _powerOn;

    /// <summary>The expected value of the rest of the turn from <paramref name="state"/>, every decision made.</summary>
    public NodeValue Value(TurnState state)
    {
        if (_values.TryGetValue(state, out var known))
        {
            return known;
        }

        if (_values.Count >= _stateLimit)
        {
            throw new DndInputException(
                $"this build is too large to compute exactly: one turn reaches more than {_stateLimit.ToString("N0", CultureInfo.InvariantCulture)} distinct states. " +
                "Reduce the attacks per turn, the resource-limited modifiers (or their uses), the once-per-turn riders, the " +
                "conditions on hit or the fight's rounds, or split the build into two.");
        }

        _meter.Spend(1 + _layout.Size);
        var value = Compute(state);
        _values[state] = value;
        return value;
    }

    /// <summary>The exact distribution of the rest of the turn's damage from <paramref name="state"/>, under the decisions of <see cref="Value"/>.</summary>
    public Pmf<double> Distribution(TurnState state)
    {
        if (_distributions.TryGetValue(state, out var known))
        {
            return known;
        }

        Value(state);
        var pmf = ComputeDistribution(state);
        _distributions[state] = pmf;
        return pmf;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Expected-value pass.
    // ------------------------------------------------------------------------------------------------------------------

    private NodeValue Compute(TurnState state)
    {
        var segment = _plan.Segments[state.Segment];
        switch (segment.Kind)
        {
            case SegmentKind.End:
                return Leaf(state);
            case SegmentKind.Start:
                return Start(state);
            case SegmentKind.BonusActionPhase:
                return BonusActionPhase(state, segment);
            case SegmentKind.Save:
                return SaveNode(state, segment);
            case SegmentKind.Attacks:
                if (state.CleaveLine < 0 && state.Position >= segment.Queue.Length)
                {
                    return Value(Next(state, segment));
                }

                return AttackNode(state, segment);
            default:
                throw new InvalidOperationException($"Unknown segment kind {segment.Kind}.");
        }
    }

    private static TurnState Next(TurnState state, Segment segment) =>
        state with { Segment = segment.Next, Position = 0, CleaveLine = -1 };

    private NodeValue Leaf(TurnState state)
    {
        var builder = new NodeBuilder(_layout.Size, _trackCarry);
        var applied = state.AppliedConditions;
        for (var bit = 0; bit <= TurnPlan.UnconsciousBit; bit++)
        {
            if ((applied & (1 << bit)) != 0)
            {
                builder.Tally(_layout.ConditionApplied(bit), 1);
            }
        }

        var landed = state.SaveLanded;
        for (var i = 0; i < _plan.TrackedSaveConditions.Length; i++)
        {
            if ((landed & (1 << i)) != 0)
            {
                builder.Tally(_layout.SaveLanded(i), 1);
            }
        }

        if (state.Has(TurnState.SapFlag))
        {
            builder.Tally(_layout.Sap, 1);
        }

        builder.Carry(new CarryKey(state.Vex, state.Uses, applied | (landed << 8)), 1);
        return builder.Build();
    }

    /// <summary>
    /// The turn's start: setup costs on the first round (a Bonus Action setup takes the Bonus Action, an Action setup the
    /// Action), then the Action — the Action save effect, or the Attack action followed by every Action Surge that still
    /// has a use (one use each per turn, spent greedily from round 1).
    /// </summary>
    private NodeValue Start(TurnState state)
    {
        var (child, surges) = StartTransition(state);
        if (surges == 0)
        {
            return Value(child);
        }

        var builder = new NodeBuilder(_layout.Size, _trackCarry);
        for (var i = 0; i < _plan.SurgeExtras.Count; i++)
        {
            if ((surges & (1 << i)) != 0)
            {
                builder.Tally(_layout.ExtraUses(_plan.SurgeExtras[i]), 1);
            }
        }

        builder.Child(1, Value(child));
        return builder.Build();
    }

    private (TurnState Child, int Surges) StartTransition(TurnState state)
    {
        var bonusAction = true;
        var actionAvailable = true;
        if (state.Has(TurnState.FirstRoundFlag))
        {
            foreach (var setup in _plan.SetupCosts)
            {
                if (setup.Cost == V.Setup.BonusAction)
                {
                    bonusAction = false;
                }
                else if (setup.Cost == V.Setup.Action)
                {
                    actionAvailable = false;
                }
            }
        }

        var next = state.With(TurnState.FirstRoundFlag, false).With(TurnState.BonusActionFlag, bonusAction);
        if (actionAvailable && _plan.ActionSaveSegment >= 0)
        {
            return (next with { Segment = _plan.ActionSaveSegment, Position = 0 }, 0);
        }

        var surges = 0;
        for (var i = 0; i < _plan.SurgeExtras.Count; i++)
        {
            var slot = _plan.ExtraSlots[_plan.SurgeExtras[i]];
            if (next.CanSpend(slot))
            {
                surges |= 1 << i;
                next = next.Spend(slot);
            }
        }

        return (next with { Segment = _plan.ActionSegment(actionAvailable, surges), Position = 0 }, surges);
    }

    private NodeValue AttackNode(TurnState state, Segment segment)
    {
        var (line, after, restFrom) = Slot(state, segment);
        var odds = Odds(line, state);

        var builder = new NodeBuilder(_layout.Size, _trackCarry);
        builder.Tally(_layout.LineMade(line.Index), 1);
        builder.Tally(_layout.LineHits(line.Index), odds.Hit);
        builder.Tally(_layout.LineCrits(line.Index), odds.Crit);

        var miss = odds.Miss;
        if (miss > 0)
        {
            var riders = _damage.MissRiders(line);
            var damage = _damage.Miss(line, riders);
            builder.Damage(miss, damage.Mean);
            builder.Tally(_layout.LineDamage(line.Index), miss * damage.Mean);
            for (var r = 0; r < _plan.Riders.Count; r++)
            {
                var bit = 1UL << r;
                if ((riders & bit) != 0)
                {
                    builder.Tally(_layout.RiderDamage(r), miss * (damage.Mean - _damage.Miss(line, riders & ~bit).Mean));
                    builder.Tally(_layout.RiderUses(r), miss);
                }
            }

            builder.Child(miss, Value(after));
        }

        if (odds.NormalHit > 0)
        {
            builder.Child(odds.NormalHit, BestHit(after, segment, restFrom, line, crit: false));
        }

        if (odds.Crit > 0)
        {
            builder.Child(odds.Crit, BestHit(after, segment, restFrom, line, crit: true));
        }

        return builder.Build();
    }

    /// <summary>
    /// The attack at this state: a pending Cleave attack first, else the queue's next. <c>after</c> is the state once the
    /// roll is made: the queue advanced and, for an attack on the main target, a pending Vex Advantage consumed.
    /// <c>restFrom</c> is where the phase's remaining queue starts, for crit_or_last's "last attack".
    /// </summary>
    private (AttackLine Line, TurnState After, int RestFrom) Slot(TurnState state, Segment segment)
    {
        AttackLine line;
        TurnState after;
        int restFrom;
        if (state.CleaveLine >= 0)
        {
            line = _plan.Lines[state.CleaveLine];
            after = state with { CleaveLine = -1 };
            restFrom = state.Position;
        }
        else
        {
            line = _plan.Lines[segment.Queue[state.Position]];
            after = state with { Position = state.Position + 1 };
            restFrom = state.Position + 1;
        }

        if (line.Kind.IsMainTarget())
        {
            after = after.With(TurnState.VexFlag, false);
        }

        return (line, after, restFrom);
    }

    /// <summary>The conditions the attack's target has, as the attack sees them (see <see cref="AttackKindRules"/>).</summary>
    private int ConditionsSeenBy(AttackLine line, TurnState state) =>
        TurnPlan.Effective(
            (line.Kind.SeesInitialCondition() ? _plan.InitialConditions : 0) |
            (line.Kind.SeesAppliedConditions() ? state.AppliedConditions : 0));

    private AttackRollOdds Odds(AttackLine line, TurnState state)
    {
        var advantage = false;
        var disadvantage = false;
        if (line.Kind.UsesAdvantageSources())
        {
            advantage = (line.AdvantageSources & _present) != 0;
            disadvantage = (line.DisadvantageSources & _present) != 0;
        }

        if (line.Kind.IsMainTarget() && state.Vex)
        {
            advantage = true;
        }

        var conditions = ConditionsSeenBy(line, state);
        if ((conditions & (1 << TurnPlan.ProneBit)) != 0)
        {
            // Prone: an attacker within 5 feet has Advantage, one farther away Disadvantage.
            if (line.Attack.IsMelee)
            {
                advantage = true;
            }
            else
            {
                disadvantage = true;
            }
        }

        const int advantageConditions = (1 << TurnPlan.RestrainedBit) | (1 << TurnPlan.BlindedBit) | (1 << TurnPlan.StunnedBit) |
                                        (1 << TurnPlan.ParalyzedBit) | (1 << TurnPlan.UnconsciousBit);
        if ((conditions & advantageConditions) != 0)
        {
            advantage = true;
        }

        if ((conditions & (1 << TurnPlan.DodgingBit)) != 0)
        {
            disadvantage = true;
        }

        var mode = D20.Resolve(advantage, disadvantage);
        const int autoCritConditions = (1 << TurnPlan.ParalyzedBit) | (1 << TurnPlan.UnconsciousBit);
        var autoCrit = line.Attack.IsMelee && (conditions & autoCritConditions) != 0;

        var key = (line.Index, mode, autoCrit);
        if (!_odds.TryGetValue(key, out var odds))
        {
            var attack = line.Attack;
            odds = AttackRoll.Odds(
                attack.AttackBonus - PowerPenalty(line), line.ArmorClass, attack.CritMin,
                new D20Options(mode, attack.Lucky, attack.ElvenAccuracy), line.BonusDice, autoCrit);
            _odds[key] = odds;
        }

        return odds;
    }

    private int PowerPenalty(AttackLine line)
    {
        var penalty = 0;
        var on = line.PowerAttacks & _powerOn;
        for (var i = 0; i < _plan.PowerAttacks.Count; i++)
        {
            if ((on & (1 << i)) != 0)
            {
                penalty += _plan.PowerAttacks[i].Penalty;
            }
        }

        return penalty;
    }

    private int PowerBonus(AttackLine line)
    {
        var bonus = 0;
        var on = line.PowerAttacks & _powerOn;
        for (var i = 0; i < _plan.PowerAttacks.Count; i++)
        {
            if ((on & (1 << i)) != 0)
            {
                bonus += _plan.PowerAttacks[i].Bonus;
            }
        }

        return bonus;
    }

    /// <summary>P(the target fails a save), with everything the target's state changes about it (contract §4.3).</summary>
    private double SaveFail(string ability, int dc, bool magical, int conditions)
    {
        conditions = TurnPlan.Effective(conditions);
        var key = (ability, dc, magical, conditions);
        if (_saveFails.TryGetValue(key, out var known))
        {
            return known;
        }

        var target = _plan.Target;
        var dex = ability == V.Abilities.Dex;
        const int autoFailConditions = (1 << TurnPlan.StunnedBit) | (1 << TurnPlan.ParalyzedBit) | (1 << TurnPlan.UnconsciousBit);
        var autoFail = (dex || ability == V.Abilities.Str) && (conditions & autoFailConditions) != 0;
        var advantage = (magical && target.MagicResistance) || (dex && (conditions & (1 << TurnPlan.DodgingBit)) != 0);
        var disadvantage = dex && (conditions & (1 << TurnPlan.RestrainedBit)) != 0;
        var bonus = target.SaveBonus(ability) + (dex ? target.CoverBonus : 0);
        var fail = SavingThrow.FailChance(dc, bonus, D20.Resolve(advantage, disadvantage), _saveDice, autoFail);
        _saveFails[key] = fail;
        return fail;
    }

    private bool Available(HitOption option, TurnState state, AttackLine line)
    {
        if (!option.AppliesTo[line.Index])
        {
            return false;
        }

        if (option.OnceBit >= 0 && (state.Spent & (1UL << option.OnceBit)) != 0)
        {
            return false;
        }

        // A reaction attack spends no resources (it happens on another creature's turn, contract §4.2).
        if (option.ResourceSlot >= 0 && (line.Kind == AttackKind.Reaction || !state.CanSpend(option.ResourceSlot)))
        {
            return false;
        }

        if (option.BonusActionCost && !state.BonusAction)
        {
            return false;
        }

        // A condition is not imposed again on a target that already has it (and spends nothing trying).
        return option.Kind != HitOptionKind.Condition ||
               (TurnPlan.Effective(_plan.InitialConditions | state.AppliedConditions) & (1 << option.ConditionBit)) == 0;
    }

    /// <summary>Whether no later attack in this phase's queue is one the option could apply to (crit_or_last's "last").</summary>
    private static bool IsLast(HitOption option, Segment segment, int restFrom)
    {
        for (var i = restFrom; i < segment.Queue.Length; i++)
        {
            if (option.AppliesTo[segment.Queue[i]])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The hit's options by policy: the fixed ones that spend now, and the optimal ones to decide.</summary>
    private (ulong Fixed, bool BonusActionTaken, List<HitOption> Optimal) Classify(TurnState after, Segment segment, int restFrom, AttackLine line, bool crit)
    {
        ulong fixedMask = 0;
        var bonusActionTaken = false;
        var optimal = new List<HitOption>();
        foreach (var option in _plan.Options)
        {
            if (!Available(option, after, line))
            {
                continue;
            }

            if (option.Policy == V.Policies.Optimal)
            {
                optimal.Add(option);
                continue;
            }

            var spend = option.Policy switch
            {
                V.Policies.AnyHit => true,
                V.Policies.CritsOnly => crit,
                V.Policies.CritOrLast => crit || IsLast(option, segment, restFrom),
                _ => throw new InvalidOperationException($"Unknown policy \"{option.Policy}\"."),
            };
            if (!spend)
            {
                continue;
            }

            // Only one Bonus Action a turn: among fixed-policy riders that cost it, the first listed takes it.
            if (option.BonusActionCost)
            {
                if (bonusActionTaken)
                {
                    continue;
                }

                bonusActionTaken = true;
            }

            fixedMask |= 1UL << option.Index;
        }

        return (fixedMask, bonusActionTaken, optimal);
    }

    private NodeValue BestHit(TurnState after, Segment segment, int restFrom, AttackLine line, bool crit)
    {
        var (fixedMask, bonusActionTaken, optimal) = Classify(after, segment, restFrom, line, crit);
        var key = (after, line.Index, crit);
        if (optimal.Count == 0)
        {
            _hitChoices[key] = fixedMask;
            return PostHit(after, line, crit, fixedMask);
        }

        NodeValue? best = null;
        var bestMask = fixedMask;
        foreach (var subset in Subsets(optimal))
        {
            var costsBonusAction = (bonusActionTaken ? 1 : 0) + subset.Count(o => o.BonusActionCost);
            if (costsBonusAction > 1)
            {
                continue;
            }

            var mask = fixedMask;
            foreach (var option in subset)
            {
                mask |= 1UL << option.Index;
            }

            var value = PostHit(after, line, crit, mask);
            if (best is null || DprLimits.Beats(value.Objective, best.Objective))
            {
                best = value;
                bestMask = mask;
            }
        }

        _hitChoices[key] = bestMask;
        return best!;
    }

    /// <summary>Every subset of <paramref name="options"/>, smallest first, each size in lexicographic order.</summary>
    private static IEnumerable<IReadOnlyList<HitOption>> Subsets(List<HitOption> options)
    {
        for (var size = 0; size <= options.Count; size++)
        {
            foreach (var subset in Combinations(options, size, 0))
            {
                yield return subset;
            }
        }
    }

    private static IEnumerable<List<HitOption>> Combinations(List<HitOption> options, int size, int from)
    {
        if (size == 0)
        {
            yield return [];
            yield break;
        }

        for (var i = from; i <= options.Count - size; i++)
        {
            foreach (var rest in Combinations(options, size - 1, i + 1))
            {
                rest.Insert(0, options[i]);
                yield return rest;
            }
        }
    }

    /// <summary>One way a hit can continue: its probability, which part of the hit's damage goes with it, and the state after.</summary>
    private readonly record struct Outcome(double Probability, DamagePart Part, TurnState State);

    private enum DamagePart
    {
        All,
        NonZero,
        Zero,
    }

    /// <summary>What a hit with a given spend does, before any value is attached: shared by both passes so they cannot drift.</summary>
    private sealed record HitExpansion(
        DamageSummary Damage,
        ulong Riders,
        bool Savage,
        int PowerBonus,
        double Cost,
        List<Outcome> Outcomes,
        List<(int Condition, double Lands)> ConditionLands);

    private HitExpansion ExpandHit(TurnState after, AttackLine line, bool crit, ulong spend)
    {
        var state = after;
        var cost = 0.0;
        var riders = _damage.MandatoryRiders(line, crit);
        var savage = false;
        var conditions = new List<HitOption>();
        foreach (var option in _plan.Options)
        {
            if ((spend & (1UL << option.Index)) == 0)
            {
                continue;
            }

            if (option.OnceBit >= 0)
            {
                state = state with { Spent = state.Spent | (1UL << option.OnceBit) };
            }

            state = state.Spend(option.ResourceSlot);
            if (option.BonusActionCost)
            {
                state = state.With(TurnState.BonusActionFlag, false);
            }

            cost += option.UseValue;
            switch (option.Kind)
            {
                case HitOptionKind.Rider:
                    riders |= 1UL << option.SourceIndex;
                    break;
                case HitOptionKind.SavageAttacker:
                    savage = true;
                    break;
                case HitOptionKind.Condition:
                    conditions.Add(option);
                    break;
            }
        }

        state = state.With(TurnState.HitTriggerFlag, true);
        if (crit && line.IsMeleeWeapon)
        {
            state = state.With(TurnState.CritTriggerFlag, true);
        }

        var powerBonus = PowerBonus(line);
        var damage = _damage.Hit(line, crit, riders, savage, powerBonus);
        var outcomes = new List<Outcome> { new(1, DamagePart.All, state) };
        var lands = new List<(int, double)>();
        var attack = line.Attack;
        if (line.Kind.IsMainTarget())
        {
            if (attack.Mastery == V.Masteries.Vex)
            {
                // "If you hit a creature with this weapon and deal damage to the creature": a hit that deals 0 grants nothing.
                outcomes = Split(outcomes, 1 - damage.ZeroChance, o => o with { Part = DamagePart.NonZero, State = o.State.With(TurnState.VexFlag, true) }, o => o with { Part = DamagePart.Zero });
                outcomes = outcomes.Select(o => o.Part switch
                {
                    DamagePart.NonZero when damage.ZeroChance == 0 => o with { Part = DamagePart.All },
                    DamagePart.Zero when damage.ZeroChance == 1 => o with { Part = DamagePart.All },
                    _ => o,
                }).ToList();
            }

            if (attack.Mastery == V.Masteries.Sap && _plan.TracksSap)
            {
                outcomes = outcomes.Select(o => o with { State = o.State.With(TurnState.SapFlag, true) }).ToList();
            }

            foreach (var option in conditions)
            {
                var source = _plan.ConditionsOnHit[option.SourceIndex];
                var dc = source.DcFor(attack.Name);
                var landed = 0.0;
                var next = new List<Outcome>();
                foreach (var outcome in outcomes)
                {
                    var present = TurnPlan.Effective(_plan.InitialConditions | outcome.State.AppliedConditions);
                    if ((present & (1 << option.ConditionBit)) != 0)
                    {
                        next.Add(outcome);
                        continue;
                    }

                    var fail = SaveFail(source.Ability, dc, source.Magical, _plan.InitialConditions | outcome.State.AppliedConditions);
                    landed += outcome.Probability * fail;
                    next.AddRange(Split([outcome], fail, o => o with { State = o.State.WithCondition(option.ConditionBit) }, o => o));
                }

                lands.Add((option.SourceIndex, landed));
                outcomes = next;
            }

            if (attack.Mastery == V.Masteries.Topple)
            {
                // "the target must succeed on a Constitution saving throw (DC 8 plus the ability modifier used to make the
                // attack roll and your Proficiency Bonus) or have the Prone condition" — for the rest of this turn here.
                var next = new List<Outcome>();
                foreach (var outcome in outcomes)
                {
                    var present = TurnPlan.Effective(_plan.InitialConditions | outcome.State.AppliedConditions);
                    if ((present & (1 << TurnPlan.ProneBit)) != 0)
                    {
                        next.Add(outcome);
                        continue;
                    }

                    var fail = SaveFail(V.Abilities.Con, attack.MasterySaveDc, false, present);
                    next.AddRange(Split([outcome], fail, o => o with { State = o.State.WithCondition(TurnPlan.ProneBit) }, o => o));
                }

                outcomes = next;
            }

            var cleaveBit = 1UL << TurnPlan.CleaveBit;
            var rate = _plan.Target.SecondTargetRate;
            if (attack.Mastery == V.Masteries.Cleave && attack.IsMelee && (state.Spent & cleaveBit) == 0 && rate > 0)
            {
                // Once per turn, at the first melee hit with the weapon: a second creature is there with probability
                // second_target_rate (sampled once, research A5's p_adj · P(≥ 1 hit) · E[attack]).
                var cleaveLine = _plan.Line(line.AttackIndex, AttackKind.Cleave).Index;
                outcomes = outcomes.SelectMany(o => Split(
                    [o], rate,
                    x => x with { State = x.State with { Spent = x.State.Spent | cleaveBit, CleaveLine = cleaveLine } },
                    x => x with { State = x.State with { Spent = x.State.Spent | cleaveBit } })).ToList();
            }
        }

        return new HitExpansion(damage, riders, savage, powerBonus, cost, outcomes, lands);
    }

    /// <summary>Splits each outcome into (p·q, yes) and (p·(1 − q), no), dropping a branch of probability 0.</summary>
    private static List<Outcome> Split(List<Outcome> outcomes, double q, Func<Outcome, Outcome> yes, Func<Outcome, Outcome> no)
    {
        var result = new List<Outcome>(outcomes.Count * 2);
        foreach (var outcome in outcomes)
        {
            if (q > 0)
            {
                result.Add(yes(outcome) with { Probability = outcome.Probability * q });
            }

            if (q < 1)
            {
                result.Add(no(outcome) with { Probability = outcome.Probability * (1 - q) });
            }
        }

        return result;
    }

    private NodeValue PostHit(TurnState after, AttackLine line, bool crit, ulong spend)
    {
        var hit = ExpandHit(after, line, crit, spend);
        var builder = new NodeBuilder(_layout.Size, _trackCarry);
        builder.Damage(1, hit.Damage.Mean, hit.Cost);
        builder.Tally(_layout.LineDamage(line.Index), hit.Damage.Mean);

        for (var r = 0; r < _plan.Riders.Count; r++)
        {
            var bit = 1UL << r;
            if ((hit.Riders & bit) != 0)
            {
                var without = _damage.Hit(line, crit, hit.Riders & ~bit, hit.Savage, hit.PowerBonus);
                builder.Tally(_layout.RiderDamage(r), hit.Damage.Mean - without.Mean);
                builder.Tally(_layout.RiderUses(r), 1);
            }
        }

        foreach (var option in _plan.Options)
        {
            if ((spend & (1UL << option.Index)) == 0)
            {
                continue;
            }

            if (option.Kind == HitOptionKind.SavageAttacker)
            {
                var plain = _damage.Hit(line, crit, hit.Riders, false, hit.PowerBonus);
                builder.Tally(_layout.RerollDamage(option.SourceIndex), hit.Damage.Mean - plain.Mean);
                builder.Tally(_layout.RerollUses(option.SourceIndex), 1);
            }
            else if (option.Kind == HitOptionKind.Condition)
            {
                builder.Tally(_layout.ConditionAttempts(option.SourceIndex), 1);
            }
        }

        foreach (var (condition, lands) in hit.ConditionLands)
        {
            builder.Tally(_layout.ConditionLands(condition), lands);
        }

        foreach (var outcome in hit.Outcomes)
        {
            builder.Child(outcome.Probability, Value(outcome.State));
        }

        return builder.Build();
    }

    private NodeValue SaveNode(TurnState state, Segment segment)
    {
        var index = segment.SaveIndex;
        var slot = _plan.SaveSlots[index];
        var next = Next(state, segment);
        if (!state.CanSpend(slot))
        {
            // No use left: the effect is not cast (the Action it would have taken goes unused).
            return Value(next);
        }

        next = next.Spend(slot);
        var effect = _plan.SaveEffects[index];
        var damage = _saves[index];
        var fail = SaveFail(effect.Ability, effect.Dc, effect.Magical, _plan.InitialConditions | state.AppliedConditions);

        var builder = new NodeBuilder(_layout.Size, _trackCarry);
        builder.Tally(_layout.SaveCasts(index), 1);
        builder.Tally(_layout.SaveDamage(index), damage.MeanTotal(fail));
        builder.Tally(_layout.SaveFailures(index), damage.Targets * fail);

        var landedBit = _plan.SaveLandedBits[index];
        if (landedBit < 0)
        {
            builder.Damage(1, damage.MeanTotal(fail));
            builder.Child(1, Value(next));
        }
        else
        {
            if (fail > 0)
            {
                builder.Damage(fail, damage.MeanTotalGivenFirst(fail, firstFails: true));
                builder.Child(fail, Value(next.WithSaveLanded(landedBit)));
            }

            if (fail < 1)
            {
                builder.Damage(1 - fail, damage.MeanTotalGivenFirst(fail, firstFails: false));
                builder.Child(1 - fail, Value(next));
            }
        }

        return builder.Build();
    }

    /// <summary>The Bonus Action options this state allows, in plan order.</summary>
    private IEnumerable<BonusActionOption> BonusActionOptions(TurnState state)
    {
        foreach (var option in _plan.BonusActionOptions)
        {
            switch (option.Kind)
            {
                case BonusActionOptionKind.Attacks:
                    yield return option;
                    break;
                case BonusActionOptionKind.ExtraAttack:
                    var extra = _plan.ExtraAttacks[option.ExtraIndex];
                    var triggered = extra.Trigger switch
                    {
                        V.Triggers.Always => true,
                        V.Triggers.Hit => state.Has(TurnState.HitTriggerFlag),
                        V.Triggers.Crit => state.Has(TurnState.CritTriggerFlag),
                        _ => throw new InvalidOperationException($"Unknown trigger \"{extra.Trigger}\"."),
                    };
                    if (triggered && state.CanSpend(option.ResourceSlot))
                    {
                        yield return option;
                    }

                    break;
                case BonusActionOptionKind.SaveEffect:
                    if (state.CanSpend(option.ResourceSlot))
                    {
                        yield return option;
                    }

                    break;
            }
        }
    }

    /// <summary>The state an option leads to (the save effect's node spends its own use).</summary>
    private static TurnState TakeBonusAction(TurnState state, BonusActionOption option)
    {
        var next = state.With(TurnState.BonusActionFlag, false) with { Segment = option.Segment, Position = 0, CleaveLine = -1 };
        return option.Kind == BonusActionOptionKind.ExtraAttack ? next.Spend(option.ResourceSlot) : next;
    }

    private NodeValue BonusActionPhase(TurnState state, Segment segment)
    {
        var none = Next(state, segment);
        if (!state.BonusAction)
        {
            return Value(none);
        }

        var options = BonusActionOptions(state).ToList();
        if (options.Count == 0)
        {
            return Value(none);
        }

        var noneIndex = _plan.BonusActionOptions.Count;
        NodeValue? best = null;
        var bestIndex = noneIndex;
        foreach (var option in options)
        {
            var value = Value(TakeBonusAction(state, option));
            if (best is null || DprLimits.Beats(value.Objective, best.Objective))
            {
                best = value;
                bestIndex = option.Index;
            }
        }

        var noneValue = Value(none);
        if (DprLimits.Beats(noneValue.Objective, best!.Objective))
        {
            best = noneValue;
            bestIndex = noneIndex;
        }

        _bonusActionChoices[state] = bestIndex;
        var builder = new NodeBuilder(_layout.Size, _trackCarry);
        builder.Tally(_layout.BonusActionChoice(bestIndex), 1);
        if (bestIndex < noneIndex && _plan.BonusActionOptions[bestIndex].Kind == BonusActionOptionKind.ExtraAttack)
        {
            builder.Tally(_layout.ExtraUses(_plan.BonusActionOptions[bestIndex].ExtraIndex), 1);
        }

        builder.Child(1, best);
        return builder.Build();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Distribution pass: the same transitions, following the decisions recorded above.
    // ------------------------------------------------------------------------------------------------------------------

    private Pmf<double> ComputeDistribution(TurnState state)
    {
        var segment = _plan.Segments[state.Segment];
        switch (segment.Kind)
        {
            case SegmentKind.End:
                return Pmf<double>.Point(0);
            case SegmentKind.Start:
                return Distribution(StartTransition(state).Child);
            case SegmentKind.BonusActionPhase:
            {
                var choice = _bonusActionChoices.GetValueOrDefault(state, _plan.BonusActionOptions.Count);
                return choice == _plan.BonusActionOptions.Count || !state.BonusAction
                    ? Distribution(Next(state, segment))
                    : Distribution(TakeBonusAction(state, _plan.BonusActionOptions[choice]));
            }

            case SegmentKind.Save:
                return SaveDistribution(state, segment);
            case SegmentKind.Attacks:
                if (state.CleaveLine < 0 && state.Position >= segment.Queue.Length)
                {
                    return Distribution(Next(state, segment));
                }

                return AttackDistribution(state, segment);
            default:
                throw new InvalidOperationException($"Unknown segment kind {segment.Kind}.");
        }
    }

    private Pmf<double> AttackDistribution(TurnState state, Segment segment)
    {
        var (line, after, _) = Slot(state, segment);
        var odds = Odds(line, state);
        var mixture = new PmfAccumulator();
        if (odds.Miss > 0)
        {
            var miss = _damage.Miss(line, _damage.MissRiders(line));
            mixture.Add(odds.Miss, miss.Pmf.Convolve(Distribution(after), _meter));
        }

        foreach (var (crit, probability) in new[] { (false, odds.NormalHit), (true, odds.Crit) })
        {
            if (probability <= 0)
            {
                continue;
            }

            var spend = _hitChoices[(after, line.Index, crit)];
            var hit = ExpandHit(after, line, crit, spend);
            foreach (var outcome in hit.Outcomes)
            {
                var part = outcome.Part switch
                {
                    DamagePart.All => hit.Damage.Pmf,
                    DamagePart.Zero => Pmf<double>.Point(0),
                    _ => NonZero(line, crit, hit),
                };
                mixture.Add(probability * outcome.Probability, part.Convolve(Distribution(outcome.State), _meter));
            }
        }

        return mixture.Build();
    }

    /// <summary>The hit's damage given that it is above 0 (a Vex hit that granted Advantage).</summary>
    private Pmf<double> NonZero(AttackLine line, bool crit, HitExpansion hit)
    {
        var key = (line.Index, crit, hit.Riders, hit.Savage, hit.PowerBonus, true);
        if (!_conditional.TryGetValue(key, out var pmf))
        {
            var pairs = new List<KeyValuePair<long, double>>();
            var source = hit.Damage.Pmf;
            var scale = 1 - hit.Damage.ZeroChance;
            for (var i = 0; i < source.Count; i++)
            {
                if (source.Values[i] != 0)
                {
                    pairs.Add(KeyValuePair.Create(source.Values[i], source.Weights[i] / source.Total / scale));
                }
            }

            pmf = Pmf<double>.FromPairs(pairs, 1.0);
            _conditional[key] = pmf;
        }

        return pmf;
    }

    private Pmf<double> SaveDistribution(TurnState state, Segment segment)
    {
        var index = segment.SaveIndex;
        var slot = _plan.SaveSlots[index];
        var next = Next(state, segment);
        if (!state.CanSpend(slot))
        {
            return Distribution(next);
        }

        next = next.Spend(slot);
        var effect = _plan.SaveEffects[index];
        var damage = _saves[index];
        var fail = SaveFail(effect.Ability, effect.Dc, effect.Magical, _plan.InitialConditions | state.AppliedConditions);
        var landedBit = _plan.SaveLandedBits[index];
        if (landedBit < 0)
        {
            return damage.Total(fail, -1, _meter).Convolve(Distribution(next), _meter);
        }

        var mixture = new PmfAccumulator();
        if (fail > 0)
        {
            mixture.Add(fail, damage.Total(fail, 1, _meter).Convolve(Distribution(next.WithSaveLanded(landedBit)), _meter));
        }

        if (fail < 1)
        {
            mixture.Add(1 - fail, damage.Total(fail, 0, _meter).Convolve(Distribution(next), _meter));
        }

        return mixture.Build();
    }
}
