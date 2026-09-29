using DndMcp.Domain.Probability;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// A stat block's turn (contract §5.4 "Monster turns"): greedy by expected damage.
///
/// <para>
/// <b>The Action</b>: heal an ally at 25% HP or less when it has a heal; escape a restraint it can escape; otherwise the
/// option with the highest expected damage against the targets its side's policy would pick — each Multiattack routine
/// (its fixed steps plus the best choices for its "choose" picks), each attack, save and auto-hit action and each combat
/// spell that is available (recharged, uses or slots left, and not a concentration spell while it concentrates). An area
/// is valued as expected damage per creature × the creatures it would catch. With nothing usable it takes the Dodge
/// action. <b>The Bonus Action</b> is chosen the same way. <b>Legendary actions</b> are spent after other creatures' turns,
/// the best value per use first, while uses last and the cost fits, once per round where the action says so.
/// </para>
/// <para>
/// Expected damage here ranks choices only (hit odds from <see cref="AttackRoll.Odds"/>, damage means scaled by the
/// target's resistances); every outcome is rolled. A condition an action imposes adds its <see cref="ConditionValue"/>
/// times the chance it lands — a failed save for a save action, the hit (and the rider's failed save, when it has one) for
/// an attack — so a lich reaches for Paralyzing Touch and a giant spider for Web when the target is worth holding. An
/// outright kill (Power Word Kill) is worth the hit points it takes, and a single-target one is aimed at, and valued at,
/// a creature it kills when there is one. An effect the target is immune to (it succeeded against it before:
/// <see cref="StatBlockAction.ImmuneAfterSuccess"/>) is worth nothing against it, and an area of it that could reach
/// only immune creatures is not used at all.
/// </para>
/// <para>
/// <b>A use_actions routine that pays a usage slot</b> (the 2024 pit fiend's Hellfire Spellcasting, Recharge 4–6, which
/// casts Fireball twice from the same shared recharge) pays it once: the uses it runs are neither checked against that
/// slot nor charged for it again (<see cref="Paid"/>).
/// </para>
/// </summary>
internal sealed partial class Fight
{
    private readonly List<Creature> _monsterTargets = [];

    /// <summary>The creature whose use_actions routine is running and has paid <see cref="_paidSlots"/> (−1: none).</summary>
    private int _paidBy = -1;

    /// <summary>The usage slots (bits, slots 0–63) the running routines of <see cref="_paidBy"/> have paid.</summary>
    private ulong _paidSlots;

    private void MonsterTurn(Creature m)
    {
        if (!TryMonsterHeal(m, m.T.Actions) && !TryEscape(m))
        {
            var (routine, action, value) = BestAction(m);
            if (routine is not null && value > 0)
            {
                if (_log is not null)
                {
                    _log.Line($"  {routine.Label}");
                }

                ExecuteRoutine(m, routine);
            }
            else if (action is not null && value > 0)
            {
                Execute(m, action);
            }
            else
            {
                m.Dodging = true;
                if (_log is not null)
                {
                    _log.Line("  takes the Dodge action");
                }
            }
        }

        if (!m.Dead && m.Up && !m.Incapacitated && m.T.BonusActions.Length > 0 && !TryMonsterHeal(m, m.T.BonusActions))
        {
            MonsterAction? best = null;
            var bestValue = 0.0;
            foreach (var bonus in m.T.BonusActions)
            {
                if (bonus.IsHeal || !Available(m, bonus))
                {
                    continue;
                }

                var value = ActionValue(m, bonus);
                if (value > bestValue)
                {
                    best = bonus;
                    bestValue = value;
                }
            }

            if (best is not null)
            {
                if (_log is not null)
                {
                    _log.Line("  Bonus Action:");
                }

                Execute(m, best);
            }
        }
    }

    private (MultiattackPlan? Routine, MonsterAction? Action, double Value) BestAction(Creature m)
    {
        MultiattackPlan? bestRoutine = null;
        MonsterAction? bestAction = null;
        var bestValue = 0.0;
        foreach (var routine in m.T.Multiattacks)
        {
            var value = RoutineValue(m, routine);
            if (value > bestValue)
            {
                (bestRoutine, bestAction, bestValue) = (routine, null, value);
            }
        }

        foreach (var action in m.T.Actions)
        {
            if (action.IsHeal || !Available(m, action))
            {
                continue;
            }

            var value = ActionValue(m, action);
            if (value > bestValue)
            {
                (bestRoutine, bestAction, bestValue) = (null, action, value);
            }
        }

        return (bestRoutine, bestAction, bestValue);
    }

    private double RoutineValue(Creature m, MultiattackPlan routine)
    {
        var value = 0.0;
        foreach (var (action, count) in routine.Steps)
        {
            if (Available(m, action))
            {
                value += count * ActionValue(m, action);
            }
        }

        if (routine.Choose > 0)
        {
            var best = 0.0;
            foreach (var option in routine.Options)
            {
                if (Available(m, option))
                {
                    best = Math.Max(best, ActionValue(m, option));
                }
            }

            value += routine.Choose * best;
        }

        return value;
    }

    private void ExecuteRoutine(Creature m, MultiattackPlan routine)
    {
        foreach (var (action, count) in routine.Steps)
        {
            for (var i = 0; i < count && CanStillAct(m); i++)
            {
                if (Available(m, action))
                {
                    Execute(m, action);
                }
            }
        }

        for (var pick = 0; pick < routine.Choose && CanStillAct(m); pick++)
        {
            MonsterAction? best = null;
            var bestValue = 0.0;
            foreach (var option in routine.Options)
            {
                if (!Available(m, option))
                {
                    continue;
                }

                var value = ActionValue(m, option);
                if (value > bestValue)
                {
                    best = option;
                    bestValue = value;
                }
            }

            if (best is null)
            {
                break;
            }

            Execute(m, best);
        }
    }

    private bool CanStillAct(Creature m) => m.Up && !m.Incapacitated && !_over;

    /// <summary>
    /// Whether an action can be used now: recharged, a use or a slot left (or its usage slot already paid by the routine
    /// running it, <see cref="Paid"/>), and no second concentration. A use-actions action needs everything it uses (at
    /// most three levels deep, so a malformed cycle cannot recurse forever).
    /// </summary>
    private bool Available(Creature m, MonsterAction action, int depth = 0)
    {
        if (action.Concentration && m.ConcentrationToken != 0)
        {
            return false;
        }

        if (action.UsageSlot >= 0 && !Paid(m, action.UsageSlot))
        {
            var usage = action.Source.Usage;
            if (usage.Kind == K.UsageKinds.Recharge ? !m.RechargeReady[action.UsageSlot] : m.UsesLeft[action.UsageSlot] <= 0)
            {
                return false;
            }
        }

        if (action.PoolSlot >= 0 && m.PoolLeft[action.PoolSlot] <= 0)
        {
            return false;
        }

        if (action.IsUseActions)
        {
            if (depth >= MaxUseDepth)
            {
                return false;
            }

            foreach (var (used, _) in action.Uses)
            {
                if (!Available(m, used, depth + 1))
                {
                    return false;
                }
            }

            return action.Uses.Length > 0;
        }

        return true;
    }

    private const int MaxUseDepth = 3;

    /// <summary>
    /// Whether a use_actions routine of <paramref name="m"/> that is running now has already paid this usage slot: a use it
    /// makes from the same slot (the Fireballs of the pit fiend's Hellfire Spellcasting share its recharge) is neither
    /// refused for the spent slot nor charged again. Without it the routine would spend the shared recharge and then find
    /// every spell it casts unavailable.
    /// </summary>
    private bool Paid(Creature m, int slot) => m.Id == _paidBy && slot is >= 0 and < 64 && (_paidSlots & (1UL << slot)) != 0;

    private void Consume(Creature m, MonsterAction action)
    {
        if (action.UsageSlot >= 0 && !Paid(m, action.UsageSlot))
        {
            m.LimitedUsed[action.UsageSlot]++;
            if (action.Source.Usage.Kind == K.UsageKinds.Recharge)
            {
                m.RechargeReady[action.UsageSlot] = false;
            }
            else
            {
                m.UsesLeft[action.UsageSlot]--;
            }
        }

        if (action.PoolSlot >= 0)
        {
            m.PoolLeft[action.PoolSlot]--;
            m.PoolUsed[action.PoolSlot]++;
        }
    }

    private void Execute(Creature m, MonsterAction action, int depth = 0)
    {
        if (!CanStillAct(m) && !action.IsUseActions)
        {
            return;
        }

        if (action.IsUseActions)
        {
            if (depth >= MaxUseDepth)
            {
                return;
            }

            Consume(m, action);
            var (outerBy, outerSlots) = (_paidBy, _paidSlots);
            if (action.UsageSlot is >= 0 and < 64)
            {
                _paidSlots = (outerBy == m.Id ? outerSlots : 0) | (1UL << action.UsageSlot);
                _paidBy = m.Id;
            }

            foreach (var (used, count) in action.Uses)
            {
                for (var i = 0; i < count; i++)
                {
                    if (Available(m, used) && m.Up)
                    {
                        Execute(m, used, depth + 1);
                    }
                }
            }

            (_paidBy, _paidSlots) = (outerBy, outerSlots);
            return;
        }

        if (action.IsAttack)
        {
            // Several attack rolls per use (Scorching Ray): one use spent, each roll at its own pick.
            var melee = MeleeUse(m, action);
            for (var roll = 0; roll < action.AttackRolls && CanStillAct(m); roll++)
            {
                var target = PickTarget(m, melee);
                if (target is null)
                {
                    break;
                }

                if (roll == 0)
                {
                    Consume(m, action);
                }

                MonsterAttack(m, action, target, melee);
            }
        }
        else if (action.IsSave)
        {
            MonsterSave(m, action);
        }
        else if (action.IsAutoHit)
        {
            MonsterAutoHit(m, action);
        }
        else if (action.IsHeal)
        {
            MonsterHeal(m, action, 1.0);
        }

        CheckOver();
    }

    /// <summary>A melee attack usable at range too is thrown from the back line.</summary>
    private static bool MeleeUse(Creature m, MonsterAction action) => action.Melee && !(action.AlsoRanged && !m.T.Front);

    private void MonsterAttack(Creature m, MonsterAction action, Creature target, bool melee)
    {
        var allyEngaged = AllyEngaged(m, target);
        var advantage = TraitAdvantage(m, target, melee);
        if (m.T.Reckless && melee)
        {
            m.RecklessActive = true;
        }

        var mode = AttackMode(m, target, melee, advantage, false, out var autoCrit);
        var bonus = action.AttackBonus - ExhaustionPenalty(m);
        var face = RollD20(mode, false, false, out var faces);
        var ac = ArmorClass(target);
        var critFace = face == 20;
        var hit = critFace || (face != 1 && face + bonus >= ac);
        if (hit && !critFace && Parried(target, melee, face + bonus, ac))
        {
            hit = false;
        }

        var crit = hit && (critFace || autoCrit);
        if (_log is not null)
        {
            _log.Line($"  {action.Name} vs {target.Label}: d20 {faces}{Signed(bonus)} = {face + bonus} vs AC {ac} → {(crit ? "critical hit" : hit ? "hit" : "miss")}");
        }

        if (!hit)
        {
            return;
        }

        if (KilledOutright(action.KillAtOrBelowHp, target))
        {
            Slay(m, target, action.Name);
            Retaliate(m, target, melee);
            return;
        }

        var damage = _hit;
        Array.Clear(damage);
        foreach (var part in action.Damage)
        {
            damage[part.Type] += RollTerms(part.Dice, crit ? 2 : 1, null, false) + part.Flat;
        }

        foreach (var effect in action.OnHit)
        {
            if (effect.Kind == K.EffectKinds.Damage)
            {
                foreach (var part in effect.Damage)
                {
                    damage[part.Type] += RollTerms(part.Dice, crit ? 2 : 1, null, false) + part.Flat;
                }
            }
        }

        var weaponType = action.Damage.Length > 0 ? action.Damage[0].Type : DamageTypes.Typeless;
        if (m.T.SneakAttack.Length > 0 && m.SneakAttackTurn != _turnId && (mode == D20Mode.Advantage || (allyEngaged && mode != D20Mode.Disadvantage)))
        {
            m.SneakAttackTurn = _turnId;
            foreach (var part in m.T.SneakAttack)
            {
                damage[weaponType] += RollTerms(part.Dice, crit ? 2 : 1, null, false) + part.Flat;
            }
        }

        if (m.T.MartialAdvantage.Length > 0 && m.MartialAdvantageTurn != _turnId && allyEngaged)
        {
            m.MartialAdvantageTurn = _turnId;
            foreach (var part in m.T.MartialAdvantage)
            {
                damage[weaponType] += RollTerms(part.Dice, crit ? 2 : 1, null, false) + part.Flat;
            }
        }

        ApplyDamage(m, target, damage, action.Magical, false, false, crit, false);

        foreach (var effect in action.OnHit)
        {
            if (target.Dead)
            {
                break;
            }

            if (effect.Kind == K.EffectKinds.Save && effect.Save is { } save)
            {
                // A kill rider works only at or below its threshold ("If the target is a creature that has 100 hit points
                // or fewer, it must succeed on a DC 15 Constitution saving throw or die"), read after the hit's damage.
                if ((effect.KillAtOrBelowHp is not null && !KilledOutright(effect.KillAtOrBelowHp, target)) ||
                    (!FitsSize(target, effect.MaxSize) && effect.Damage.Length == 0) || ShrugsOff(m, target, effect.ImmunityIndex, action.Name))
                {
                    continue;
                }

                int[]? rolled = null;
                if (effect.Damage.Length > 0)
                {
                    rolled = _aux;
                    RollParts(effect.Damage, rolled, false);
                }

                var fits = FitsSize(target, effect.MaxSize);
                ResolveSave(m, target, save.Ability, save.Dc, save.OnSuccess, action.Magical, rolled, fits ? effect.Condition : null,
                    ConcentrationTokenFor(m, action), action.Name, fits ? effect.ExtraConditions : null, effect.ImmunityIndex, lethal: effect.KillAtOrBelowHp is not null);
            }
            else if (effect.Kind == K.EffectKinds.Condition && FitsSize(target, effect.MaxSize))
            {
                if (effect.Condition is { } condition)
                {
                    AddCondition(m, target, condition, ConcentrationTokenFor(m, action));
                }

                foreach (var more in effect.ExtraConditions)
                {
                    AddCondition(m, target, more, ConcentrationTokenFor(m, action));
                }
            }
        }

        Retaliate(m, target, melee);
    }

    /// <summary>
    /// Advantage a monster's traits give an attack: Pack Tactics (an ally engaged with the target), advantage while
    /// bloodied, Blood Frenzy (melee against a wounded target), Reckless (its melee attacks).
    /// </summary>
    internal bool TraitAdvantage(Creature m, Creature target, bool melee) =>
        (m.T.PackTactics && AllyEngaged(m, target)) ||
        (m.T.AdvantageWhileBloodied && m.Hp * 2 <= m.MaxHp) ||
        (m.T.BloodFrenzy && melee && target.Hp < target.MaxHp) ||
        (m.T.Reckless && melee);

    private int ConcentrationTokenFor(Creature m, MonsterAction action) =>
        action.Concentration && m.ConcentrationLabel == action.Name ? m.ConcentrationToken : 0;

    private void MonsterSave(Creature m, MonsterAction action)
    {
        var targets = _monsterTargets;
        if (action.AreaCount > 0)
        {
            if (OnlyImmuneInReach(m, action.ImmunityIndex))
            {
                return;
            }

            PickArea(m, action.AreaCount, targets);
        }
        else
        {
            PickDistinct(m, action.Targets, targets, action.ImmunityIndex, action.KillAtOrBelowHp);
        }

        if (targets.Count == 0 || action.Save is not { } save)
        {
            return;
        }

        Consume(m, action);
        var token = action.Concentration && (action.Condition is not null || action.ExtraConditions.Length > 0) ? StartConcentration(m, action.Name, 0, deactivates: false) : 0;
        int[]? rolled = null;
        if (action.Damage.Length > 0)
        {
            rolled = _aoe;
            RollParts(action.Damage, rolled, false);
        }

        if (_log is not null)
        {
            _log.Line($"  {action.Name} (DC {save.Dc} {save.Ability}) on {string.Join(", ", targets.Select(t => t.Label))}{(rolled is null ? "" : $": rolls {rolled.Sum()}")}");
        }

        foreach (var target in targets.ToArray())
        {
            ResolveSave(m, target, save.Ability, save.Dc, save.OnSuccess, action.Magical, rolled, action.Condition, token, action.Name, action.ExtraConditions,
                action.ImmunityIndex, lethal: KilledOutright(action.KillAtOrBelowHp, target));
        }

        if (token != 0 && LinkedConditions(token) == 0)
        {
            EndConcentration(m, "has nothing left to hold");
        }
    }

    /// <summary>
    /// Whether every enemy an area of this effect could catch (<see cref="PickArea"/>: standing, or at 0 HP and alive) is
    /// immune to it (<see cref="StatBlockAction.ImmuneAfterSuccess"/>). The dragons' Multiattack runs "The dragon can use
    /// its Frightful Presence" as a step whatever its value; once the whole party has shaken the fear off, the step is
    /// skipped (the option not taken) instead of drawing an area and logging every creature's immunity, turn after turn.
    /// A single-target effect needs no such test: <see cref="PickDistinct"/> leaves immune creatures out, and with none
    /// left the effect is not used.
    /// </summary>
    private bool OnlyImmuneInReach(Creature m, int immunity)
    {
        if (immunity < 0)
        {
            return false;
        }

        foreach (var c in _c)
        {
            if (c.Side != m.Side && (c.Up || (c.Down && !c.Dead)) && !c.IsImmuneToEffect(m.Id, immunity))
            {
                return false;
            }
        }

        return true;
    }

    private void MonsterAutoHit(Creature m, MonsterAction action)
    {
        if (action.AreaCount > 0)
        {
            // An area with no roll and no save (the 2024 lich's Deathly Teleport): one damage roll for everyone in it.
            PickArea(m, action.AreaCount, _monsterTargets);
            if (_monsterTargets.Count == 0)
            {
                return;
            }

            Consume(m, action);
            var rolled = _aoe;
            RollParts(action.Damage, rolled, false);
            if (_log is not null)
            {
                _log.Line($"  {action.Name} on {string.Join(", ", _monsterTargets.Select(t => t.Label))}");
            }

            foreach (var target in _monsterTargets.ToArray())
            {
                if (KilledOutright(action.KillAtOrBelowHp, target))
                {
                    Slay(m, target, action.Name);
                }
                else
                {
                    ApplyDamage(m, target, rolled, action.Magical, false, false, false, false);
                }
            }

            return;
        }

        var used = false;
        for (var dart = 0; dart < action.Targets; dart++)
        {
            var target = PickTarget(m, false, action.KillAtOrBelowHp);
            if (target is null)
            {
                break;
            }

            if (!used)
            {
                Consume(m, action);
                used = true;
                if (_log is not null)
                {
                    _log.Line($"  {action.Name}");
                }
            }

            // Power Word Kill: "If the target has 100 Hit Points or fewer, it dies. Otherwise, it takes 12d12 Psychic damage."
            if (KilledOutright(action.KillAtOrBelowHp, target))
            {
                Slay(m, target, action.Name);
                continue;
            }

            RollParts(action.Damage, _hit, false);
            ApplyDamage(m, target, _hit, action.Magical, false, false, false, false);
        }
    }

    /// <summary>A monster heals an ally (or itself) at or below the fraction of its hit points: 25% for the Action choice.</summary>
    private bool TryMonsterHeal(Creature m, MonsterAction[] actions)
    {
        if (!m.T.HasHeal)
        {
            return false;
        }

        foreach (var action in actions)
        {
            if (action.IsHeal && Available(m, action) && MonsterHeal(m, action, 0.25))
            {
                return true;
            }
        }

        return false;
    }

    private bool MonsterHeal(Creature m, MonsterAction action, double threshold)
    {
        _picked.Clear();
        foreach (var c in _c)
        {
            if (c.Side == m.Side && c.Up && c.Hp <= threshold * c.MaxHp && c.Hp < c.MaxHp && (!action.SelfOnly || c == m))
            {
                _picked.Add(c);
            }
        }

        if (_picked.Count == 0 || action.Healing is null)
        {
            return false;
        }

        _picked.Sort(static (a, b) => ((double)a.Hp / a.MaxHp).CompareTo((double)b.Hp / b.MaxHp));
        Consume(m, action);
        if (_log is not null)
        {
            _log.Line($"  {action.Name}");
        }

        for (var i = 0; i < Math.Min(action.Targets, _picked.Count); i++)
        {
            Heal(m, _picked[i], RollFormula(action.Healing), action.Name);
        }

        return true;
    }

    private void LegendaryAction(Creature m)
    {
        MonsterAction? best = null;
        var bestValue = 0.0;
        foreach (var action in m.T.LegendaryActions)
        {
            if (action.LegendaryCost > m.LegendaryUsesLeft || (action.OncePerRound && m.LegendaryUsedThisRound[action.LegendaryIndex]) ||
                action.IsHeal || !action.Usable || !Available(m, action))
            {
                continue;
            }

            var value = ActionValue(m, action) / Math.Max(1, action.LegendaryCost);
            if (value > bestValue)
            {
                best = action;
                bestValue = value;
            }
        }

        if (best is null)
        {
            return;
        }

        m.LegendaryUsesLeft -= best.LegendaryCost;
        m.LegendaryActionsUsed++;
        m.LegendaryUsedThisRound[best.LegendaryIndex] = true;
        if (_log is not null)
        {
            _log.Line($"  {m.Label} legendary action: {best.Name} ({m.LegendaryUsesLeft} left)");
        }

        Execute(m, best);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Expected values (ranking only).
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// E[damage] of using the action now: an attack against the policy's pick (focus fire, threat) or the average over the
    /// candidates (spread and the rest); a save or auto-hit over the creatures it would reach (<see cref="EffectValue"/>).
    /// </summary>
    private double ActionValue(Creature m, MonsterAction action, int depth = 0)
    {
        if (action.IsUseActions)
        {
            var total = 0.0;
            if (depth < MaxUseDepth)
            {
                foreach (var (used, count) in action.Uses)
                {
                    total += count * ActionValue(m, used, depth + 1);
                }
            }

            return total;
        }

        return action.IsAttack ? action.AttackRolls * AttackActionValue(m, action) : EffectValue(m, action);
    }

    /// <summary>
    /// One attack roll's E[damage]: against the policy's pick (focus fire, threat) or the average over the candidates
    /// (spread and the rest).
    /// </summary>
    private double AttackActionValue(Creature m, MonsterAction action)
    {
        var melee = MeleeUse(m, action);
        FillCandidates(m, melee);
        if (_candidates.Count == 0)
        {
            return 0;
        }

        var policy = _setup.Targeting(m.Side);
        if (policy is SimulationValues.Targeting.FocusFire or SimulationValues.Targeting.Threat || _setup.Dummy)
        {
            var pick = _setup.Dummy ? PickTarget(m, melee)! : Choose(_candidates, policy);
            return AttackValue(m, action, pick, melee);
        }

        // The average over the candidates, each valued on its own: every copy of an entry has its own template, and a
        // value can hang on its exact hit points (an outright kill), so no two candidates are assumed alike.
        var sum = 0.0;
        foreach (var c in _candidates)
        {
            sum += AttackValue(m, action, c, melee);
        }

        return sum / _candidates.Count;
    }

    /// <summary>
    /// A save or auto-hit action's E[damage]: the average per creature over the enemies standing, times the creatures (or
    /// darts) it reaches. A single-target outright kill is aimed at a creature it kills when there is one
    /// (<see cref="KillableAmong"/>), so it is valued at that choice instead (<see cref="KillAimedValue"/>).
    /// </summary>
    private double EffectValue(Creature m, MonsterAction action)
    {
        if (action.KillAtOrBelowHp is not null && action.AreaCount == 0 && action.Targets == 1 && (action.IsSave || action.IsAutoHit) &&
            KillAimedValue(m, action) is { } aimed)
        {
            return aimed;
        }

        if (action.IsSave || action.IsAutoHit)
        {
            var count = 0;
            var total = 0.0;
            foreach (var c in _c)
            {
                if (c.Side == m.Side || !c.Up)
                {
                    continue;
                }

                count++;
                total += action.IsSave ? SaveValue(m, action, c) : AutoHitValue(action, c);
            }

            if (count == 0)
            {
                return 0;
            }

            var reached = action.IsAutoHit && action.AreaCount == 0 ? action.Targets : Math.Min(count, action.AreaCount > 0 ? action.AreaCount : action.Targets);
            return total / count * reached;
        }

        return 0;
    }

    /// <summary>
    /// A single-target outright kill's value at the target it is aimed at (<see cref="PickTarget"/>,
    /// <see cref="PickDistinct"/>): among the creatures it kills, the policy's own pick (focus fire, threat) or the average
    /// over them (the policies that pick at random). Null when it kills no standing candidate: then the average over every
    /// standing enemy stands, as for any other action. Valued over every enemy instead, the 2014 Power Word Kill (no effect above
    /// 100 hit points) looked worth a fraction of the wizard it kills, and was aimed by the policy regardless.
    /// </summary>
    private double? KillAimedValue(Creature m, MonsterAction action)
    {
        FillCandidates(m, false);
        if (action.IsSave)
        {
            DropImmune(m, action.ImmunityIndex);
        }

        if (!KillableAmong(action.KillAtOrBelowHp))
        {
            return null;
        }

        var policy = _setup.Targeting(m.Side);
        if (policy is SimulationValues.Targeting.FocusFire or SimulationValues.Targeting.Threat)
        {
            var pick = Choose(_killable, policy);
            return action.IsSave ? SaveValue(m, action, pick) : AutoHitValue(action, pick);
        }

        var sum = 0.0;
        foreach (var c in _killable)
        {
            sum += action.IsSave ? SaveValue(m, action, c) : AutoHitValue(action, c);
        }

        return sum / _killable.Count;
    }

    private double AttackValue(Creature m, MonsterAction action, Creature target, bool melee)
    {
        var mode = AttackMode(m, target, melee, TraitAdvantage(m, target, melee), false, out var autoCrit, consume: false);
        var (hit, crit) = Odds(action.AttackBonus - ExhaustionPenalty(m), ArmorClass(target), 20, mode, false, false, null, 0, autoCrit);
        var factor = action.Damage.Length > 0 ? Factor(target, action.Damage[0].Type, action.Magical) : 1;
        if (KilledOutright(action.KillAtOrBelowHp, target))
        {
            return hit * Math.Max(KillValue(target), action.MeanDamage * factor);
        }

        var value = (hit * action.MeanDamage * factor) + (crit * action.MeanCritExtra * factor);
        foreach (var effect in action.OnHit)
        {
            if (target.IsImmuneToEffect(m.Id, effect.ImmunityIndex))
            {
                continue;
            }

            var fits = FitsSize(target, effect.MaxSize);
            if (effect.Kind == K.EffectKinds.Save && effect.Save is { } save)
            {
                var fail = SavingThrowFail(target, save.Ability, save.Dc, action.Magical);
                if (effect.KillAtOrBelowHp is not null)
                {
                    value += KilledOutright(effect.KillAtOrBelowHp, target) ? hit * fail * KillValue(target) : 0;
                    continue;
                }

                if (effect.Damage.Length > 0)
                {
                    var mean = effect.Damage.Sum(d => d.Mean) * Factor(target, effect.Damage[0].Type, action.Magical);
                    value += hit * ((fail * mean) + ((1 - fail) * (save.OnSuccess == K.OnSuccess.Half ? mean / 2 : 0)));
                }

                if (fits)
                {
                    value += hit * fail * ConditionsValue(target, effect.Condition, effect.ExtraConditions);
                }
            }
            else if (effect.Kind == K.EffectKinds.Condition && fits)
            {
                value += hit * ConditionsValue(target, effect.Condition, effect.ExtraConditions);
            }
        }

        return value;
    }

    private double SaveValue(Creature m, MonsterAction action, Creature target)
    {
        if (target.IsImmuneToEffect(m.Id, action.ImmunityIndex))
        {
            return 0;
        }

        var save = action.Save!;
        var fail = SavingThrowFail(target, save.Ability, save.Dc, action.Magical);
        var mean = action.MeanDamage * (action.Damage.Length > 0 ? Factor(target, action.Damage[0].Type, action.Magical) : 1);
        var evasion = Evades(target, save.Ability, save.OnSuccess);
        var onFail = evasion ? mean / 2 : mean;
        if (KilledOutright(action.KillAtOrBelowHp, target))
        {
            onFail = Math.Max(onFail, KillValue(target));
        }

        var onSuccess = save.OnSuccess == K.OnSuccess.Half && !evasion ? mean / 2 : 0;
        var condition = ConditionsValue(target, action.Condition, action.ExtraConditions);
        return (fail * (onFail + condition)) + ((1 - fail) * onSuccess);
    }

    /// <summary>An auto-hit's E[damage] on one creature (per dart), or what its outright kill takes when the creature is at or below the threshold.</summary>
    private static double AutoHitValue(MonsterAction action, Creature target)
    {
        var mean = action.MeanDamage * Factor(target, action.Damage.Length > 0 ? action.Damage[0].Type : DamageTypes.Typeless, action.Magical);
        return KilledOutright(action.KillAtOrBelowHp, target) ? Math.Max(mean, KillValue(target)) : mean;
    }

    /// <summary>
    /// What an outright kill is worth in the policies' currency: the hit points (and temporary ones) it takes, which is
    /// what damage that dropped the creature would have had to deal. The callers rank the larger of it and the action's
    /// own damage, so the 2024 Power Word Kill (12d12 otherwise) never ranks below its damage, and the 2014 one (no effect
    /// otherwise) is still worth casting at a creature it kills.
    /// </summary>
    private static double KillValue(Creature target) => target.Hp + target.TempHp;

    /// <summary>The conditions one failed save or hit imposes together, each by <see cref="ConditionValue"/>.</summary>
    private static double ConditionsValue(Creature target, ConditionTemplate? condition, ConditionTemplate[] extra)
    {
        var value = condition is not null ? ConditionValue(target, condition.Condition) : 0;
        foreach (var more in extra)
        {
            value += ConditionValue(target, more.Condition);
        }

        return value;
    }
}
