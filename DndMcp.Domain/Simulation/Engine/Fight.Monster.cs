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
/// target's resistances); every outcome is rolled. Conditions an action imposes add no value to its rank.
/// </para>
/// </summary>
internal sealed partial class Fight
{
    private readonly List<Creature> _monsterTargets = [];

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
    /// Whether an action can be used now: recharged, a use or a slot left, and no second concentration. A use-actions
    /// action needs everything it uses (at most three levels deep, so a malformed cycle cannot recurse forever).
    /// </summary>
    private bool Available(Creature m, MonsterAction action, int depth = 0)
    {
        if (action.Concentration && m.ConcentrationToken != 0)
        {
            return false;
        }

        if (action.UsageSlot >= 0)
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

    private void Consume(Creature m, MonsterAction action)
    {
        if (action.UsageSlot >= 0)
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
        var ac = target.T.ArmorClass + target.T.CoverBonus;
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

        if (melee && target.T.Retaliation is { } retaliation && !m.Dead)
        {
            TraitDamage(target, m, retaliation);
        }

        foreach (var effect in action.OnHit)
        {
            if (target.Dead)
            {
                break;
            }

            if (effect.Kind == K.EffectKinds.Save && effect.Save is { } save)
            {
                if (!FitsSize(target, effect.MaxSize) && effect.Damage.Length == 0)
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
                    ConcentrationTokenFor(m, action), action.Name, fits ? effect.ExtraConditions : null);
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
            PickArea(m, action.AreaCount, targets);
        }
        else
        {
            PickDistinct(m, action.Targets, targets);
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
            ResolveSave(m, target, save.Ability, save.Dc, save.OnSuccess, action.Magical, rolled, action.Condition, token, action.Name, action.ExtraConditions);
        }

        if (token != 0 && LinkedConditions(token) == 0)
        {
            EndConcentration(m, "has nothing left to hold");
        }
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
                ApplyDamage(m, target, rolled, action.Magical, false, false, false, false);
            }

            return;
        }

        var used = false;
        for (var dart = 0; dart < action.Targets; dart++)
        {
            var target = PickTarget(m, false);
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
    /// candidates (spread and the rest); a save or auto-hit over the creatures it would reach.
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

        // The average over the candidates. Identical candidates (one entry's copies, unhurt or all hurt alike, with no
        // conditions) have identical values: each distinct one is valued once.
        var sum = 0.0;
        CombatantTemplate? lastTemplate = null;
        var lastWounded = false;
        var lastValue = 0.0;
        foreach (var c in _candidates)
        {
            var plain = c.Conditions.Count == 0 && c.Up && !c.Dodging && c.SappedBy < 0 && !c.RecklessActive;
            var wounded = c.Hp < c.MaxHp;
            if (plain && lastTemplate == c.T && lastWounded == wounded && c.T.PermanentConditions == 0)
            {
                sum += lastValue;
                continue;
            }

            var value = AttackValue(m, action, c, melee);
            sum += value;
            (lastTemplate, lastWounded, lastValue) = plain ? (c.T, wounded, value) : (null, false, 0.0);
        }

        return sum / _candidates.Count;
    }

    private double EffectValue(Creature m, MonsterAction action)
    {
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
                total += action.IsSave ? SaveValue(action, c) : action.MeanDamage * Factor(c, action.Damage.Length > 0 ? action.Damage[0].Type : DamageTypes.Typeless, action.Magical);
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

    private double AttackValue(Creature m, MonsterAction action, Creature target, bool melee)
    {
        var mode = AttackMode(m, target, melee, TraitAdvantage(m, target, melee), false, out var autoCrit, consume: false);
        var (hit, crit) = Odds(action.AttackBonus - ExhaustionPenalty(m), target.T.ArmorClass + target.T.CoverBonus, 20, mode, false, false, null, 0, autoCrit);
        var factor = action.Damage.Length > 0 ? Factor(target, action.Damage[0].Type, action.Magical) : 1;
        var value = (hit * action.MeanDamage * factor) + (crit * action.MeanCritExtra * factor);
        foreach (var effect in action.OnHit)
        {
            if (effect.Kind == K.EffectKinds.Save && effect.Save is { } save && effect.Damage.Length > 0)
            {
                var fail = SavingThrowFail(target, save.Ability, save.Dc, action.Magical);
                var mean = effect.Damage.Sum(d => d.Mean) * Factor(target, effect.Damage[0].Type, action.Magical);
                value += hit * ((fail * mean) + ((1 - fail) * (save.OnSuccess == K.OnSuccess.Half ? mean / 2 : 0)));
            }
        }

        return value;
    }

    private double SaveValue(MonsterAction action, Creature target)
    {
        var save = action.Save!;
        var fail = SavingThrowFail(target, save.Ability, save.Dc, action.Magical);
        var mean = action.MeanDamage * (action.Damage.Length > 0 ? Factor(target, action.Damage[0].Type, action.Magical) : 1);
        var evasion = Evades(target, save.Ability, save.OnSuccess);
        var onFail = evasion ? mean / 2 : mean;
        var onSuccess = save.OnSuccess == K.OnSuccess.Half && !evasion ? mean / 2 : 0;
        var condition = action.Condition is not null ? ConditionValue(target, action.Condition.Condition) : 0;
        foreach (var more in action.ExtraConditions)
        {
            condition += ConditionValue(target, more.Condition);
        }

        return (fail * (onFail + condition)) + ((1 - fail) * onSuccess);
    }
}
