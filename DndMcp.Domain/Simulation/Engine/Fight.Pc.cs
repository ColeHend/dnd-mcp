using DndMcp.Domain.Features;
using DndMcp.Domain.Probability;
using SavingThrowOdds = DndMcp.Domain.Probability.SavingThrow;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// A DSL build's turn (contract §5.4 "PC turns"), written from the Phase 4 contract's §4.1–4.2 semantics and NOT by
/// calling the closed form's turn evaluator: the simulator is the closed form's cross-check, so the two must be
/// independent implementations of one rule text.
///
/// <para>
/// <b>The turn</b>: the advantage-rate sources are sampled once; setup costs are paid on the first turn (and a lost
/// concentration setup re-paid when it looks worth it), and an advantage source a setup switches on is sampled as it is
/// paid, so it applies to this turn's attacks; healing by policy; then the Action — the Attack action (its
/// attacks in list order, each <c>count</c> times, then every Action Surge with a use left) or an Action save effect,
/// whichever is expected to deal more against the likely targets; then the Bonus Action — bonus_action attacks (an offhand
/// attack only after the Attack action), a triggered extra attack (hit, crit, always, crit_or_kill — the kill is real
/// here), or a Bonus Action save effect, by expected damage; then save effects that cost no action — neither once the
/// build has dropped or been incapacitated during its own turn (a retaliation trait's burn, a death burst it caused).
/// The reaction attack is drawn with its per-round probability and made at the end of the next enemy turn.
/// </para>
/// <para>
/// <b>Per attack</b> (the closed form's rules, rolled): d20 with the sample's rate sources, Vex, conditions and
/// Lucky/Elven Accuracy, bonus dice, the power attacks switched on this turn; on a hit, riders by policy (any_hit,
/// crits_only, crit_or_last as rules; optimal by a myopic backward rule over the phase's remaining eligible attacks),
/// Savage Attacker, the damage, Vex, Sap, conditions on hit (with their durations carried), Topple, the target's
/// retaliation trait on a melee hit, Cleave against a real second enemy, and the triggers; on a miss Graze and on_miss
/// riders. Untyped riders deal the attack's damage type.
/// </para>
/// </summary>
internal sealed partial class Fight
{
    private readonly List<(int Attack, LineKind Kind)> _queue = [];
    private readonly List<int> _chosenConditions = [];
    private readonly List<Creature> _saveTargets = [];
    private readonly List<int> _autoPower = [];

    private void PcTurn(Creature pc)
    {
        var state = pc.Pc!;
        var build = state.Build;
        var ctx = state.Turn;
        ctx.Begin(reaction: false);
        DrawReaction(pc, state);

        // The advantage-rate sources, sampled once for the turn. One still waiting for its setup is sampled only once this
        // turn pays it (phase 4 §3.4: "active for every later attack from that point", and the setup is paid before the
        // Action): Innate Sorcery set up with the Bonus Action gives round 1's Fire Bolt Advantage. Sampling it after the
        // setups, rather than moving the setups first, keeps every other build on the same random stream, and keeps the
        // re-setup rule's estimate (WorthSettingUp reads the sample) reading the sources that were already up.
        var waiting = 0UL;
        for (var i = 0; i < build.Advantage.Length; i++)
        {
            var source = build.Advantage[i];
            if (!state.IsActive(source.Source.Number))
            {
                waiting |= 1UL << i;
            }
            else if (Chance(source.Rate))
            {
                ctx.Present |= 1UL << i;
            }
        }

        var action = true;
        PaySetups(pc, state, ctx, ref action);
        for (var i = 0; waiting != 0 && i < build.Advantage.Length; i++)
        {
            var source = build.Advantage[i];
            if ((waiting & (1UL << i)) != 0 && state.IsActive(source.Source.Number) && Chance(source.Rate))
            {
                ctx.Present |= 1UL << i;
            }
        }

        PcHeals(pc, state, ctx, ref action);
        if (action && TryEscape(pc))
        {
            action = false;
        }

        ctx.Target = PickTarget(pc, FirstAttackIsMelee(build));
        ChoosePowerAttacks(pc, state, ctx, action);

        if (action)
        {
            PcAction(pc, state, ctx);
        }
        else if (build.SurgeExtras.Length > 0)
        {
            // The Action went elsewhere (a setup, a heal, an escape): an Action Surge still gives an Attack action.
            RunAttackAction(pc, state, ctx, withAction: false);
        }

        // A melee hit on a retaliating creature, or a death burst the build caused, can drop or incapacitate it during its
        // own turn: a creature at 0 HP takes no Bonus Action and keeps no effect going.
        if (CanStillAct(pc))
        {
            PcBonusAction(pc, state, ctx);
        }

        foreach (var s in build.FreeSaves)
        {
            if (CanStillAct(pc) && CanCast(pc, state, s))
            {
                CastSaveEffect(pc, state, s);
            }
        }

        state.Acted = true;
    }

    private static bool FirstAttackIsMelee(PcBuild build) =>
        build.ActionQueue.Length > 0 ? build.Attacks[build.ActionQueue[0]].Melee : build.BonusQueue.Length > 0 && build.Attacks[build.BonusQueue[0]].Melee;

    // ------------------------------------------------------------------------------------------------------------------
    // Setup, healing, reaction draw.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Setup costs: all paid on the build's first turn (as the closed form pays them in round 1). A concentration modifier
    /// lost later is set up again when its expected value over two turns is at least what the cost's other use would deal
    /// this turn (a heuristic; the contract leaves the rule open).
    /// </summary>
    private void PaySetups(Creature pc, PcState state, PcTurnContext ctx, ref bool action)
    {
        var build = state.Build;
        foreach (var setup in build.Setups)
        {
            var number = setup.Source.Number;
            if (state.Active[number])
            {
                continue;
            }

            var bonusAction = setup.Cost == V.Setup.BonusAction;
            if (bonusAction ? !ctx.BonusAction : !action)
            {
                continue;
            }

            if (state.Acted && !WorthSettingUp(pc, state, ctx, number, bonusAction))
            {
                continue;
            }

            if (bonusAction)
            {
                ctx.BonusAction = false;
            }
            else
            {
                action = false;
            }

            state.Active[number] = true;
            if (_log is not null)
            {
                _log.Line($"  sets up {setup.Source.Label} ({(bonusAction ? "Bonus Action" : "Action")})");
            }

            if (number == build.ConcentrationNumber)
            {
                StartConcentration(pc, build.ConcentrationLabel!, number, deactivates: true);
            }
        }
    }

    private bool WorthSettingUp(Creature pc, PcState state, PcTurnContext ctx, int number, bool bonusAction)
    {
        var build = state.Build;
        var target = PickTarget(pc, FirstAttackIsMelee(build));
        if (target is null)
        {
            return false;
        }

        var value = 0.0;
        for (var r = 0; r < build.Riders.Length; r++)
        {
            if (build.Riders[r].Source.Number != number)
            {
                continue;
            }

            foreach (var a in build.ActionQueue)
            {
                var attack = build.Attacks[a];
                if (attack.Riders.Contains(r))
                {
                    var (hit, crit) = EstimateOdds(pc, ctx, attack, LineKind.Action, target, ctx.PowerOn);
                    value += hit * build.RiderDiceMean[r] * Factor(target, RiderType(build.Riders[r], attack), attack.Magical) + crit * build.RiderDiceMean[r];
                }
            }
        }

        if (value == 0)
        {
            value = double.PositiveInfinity; // not a rider (an extra attack, a condition): set it up again
        }

        var alternative = bonusAction ? BonusQueueValue(pc, state, ctx, target) : QueueValue(pc, ctx, build.ActionQueue, LineKind.Action, target, ctx.PowerOn);
        return 2 * value >= alternative;
    }

    /// <summary>Heals by the healing policy: Bonus Action heals first, then Action heals, each on the allies that qualify.</summary>
    private void PcHeals(Creature pc, PcState state, PcTurnContext ctx, ref bool action)
    {
        var build = state.Build;
        if (_setup.Healing == SimulationValues.Healing.Never || build.Heals.Length == 0)
        {
            return;
        }

        for (var pass = 0; pass < 2; pass++)
        {
            var bonusFirst = pass == 0;
            for (var h = 0; h < build.Heals.Length; h++)
            {
                var heal = build.Heals[h];
                var isBonus = heal.ActionCost == V.ActionCosts.BonusAction;
                if (isBonus != bonusFirst || (isBonus ? !ctx.BonusAction : !action) || !state.CanSpend(build.HealSlot[h]) || !state.IsActive(heal.Source.Number))
                {
                    continue;
                }

                HealTargets(pc, heal, _picked);
                if (_picked.Count == 0)
                {
                    continue;
                }

                state.Spend(build.HealSlot[h]);
                if (isBonus)
                {
                    ctx.BonusAction = false;
                }
                else
                {
                    action = false;
                }

                if (_log is not null)
                {
                    _log.Line($"  {heal.Source.Label} ({(isBonus ? "Bonus Action" : "Action")})");
                }

                foreach (var target in _picked)
                {
                    Heal(pc, target, RollFormula(heal.Healing), heal.Source.Label);
                }
            }
        }
    }

    private void HealTargets(Creature pc, ResolvedHeal heal, List<Creature> into)
    {
        into.Clear();
        if (heal.SelfOnly)
        {
            if (pc.Hp * 2 <= pc.MaxHp)
            {
                into.Add(pc);
            }

            return;
        }

        var belowHalf = _setup.Healing == SimulationValues.Healing.BelowHalf;
        foreach (var c in _c)
        {
            if (c.Side == pc.Side && !c.Dead && (belowHalf ? c.Hp * 2 <= c.MaxHp : c.Down && c.T.PcLike))
            {
                into.Add(c);
            }
        }

        // The fallen first, then the lowest fraction of hit points.
        into.Sort((a, b) => a.Down != b.Down ? (a.Down ? -1 : 1) : ((double)a.Hp / a.MaxHp).CompareTo((double)b.Hp / b.MaxHp));
        if (into.Count > heal.Targets)
        {
            into.RemoveRange(heal.Targets, into.Count - heal.Targets);
        }
    }

    /// <summary>
    /// The round's reaction attack: the reaction extra attack with the best trigger probability × expected damage, drawn
    /// with its probability now and made at the end of the next enemy creature's turn (against that creature).
    /// </summary>
    private void DrawReaction(Creature pc, PcState state)
    {
        var build = state.Build;
        state.ReactionPending = false;
        state.ReactionExtra = -1;
        if (build.ReactionExtras.Length == 0)
        {
            return;
        }

        var best = -1;
        var bestValue = double.NegativeInfinity;
        foreach (var e in build.ReactionExtras)
        {
            if (!state.IsActive(build.Extras[e].Source.Number))
            {
                continue;
            }

            var value = (build.Extras[e].TriggerProbability ?? 1) * build.Extras[e].Count * (build.Attacks[build.ExtraAttack[e]].DiceMean + 1);
            if (value > bestValue)
            {
                best = e;
                bestValue = value;
            }
        }

        if (best >= 0 && Chance(build.Extras[best].TriggerProbability ?? 1))
        {
            state.ReactionPending = true;
            state.ReactionExtra = best;
        }
    }

    private void ReactionAttack(Creature pc, Creature enemy)
    {
        var state = pc.Pc!;
        var build = state.Build;
        var e = state.ReactionExtra;
        var attack = build.Attacks[build.ExtraAttack[e]];
        if (!Attackable(pc, enemy) || !Reachable(pc, enemy, attack.Melee))
        {
            return; // this enemy is out of reach: the reaction waits for the next enemy turn this round
        }

        state.ReactionPending = false;
        pc.ReactionAvailable = false;
        var ctx = state.ReactionTurn;
        ctx.Begin(reaction: true);
        ctx.PowerOn = FixedPowerAttacks(state);
        ctx.Target = enemy;
        if (_log is not null)
        {
            _log.Line($"  {pc.Label} reacts: {build.Extras[e].Source.Label} against {enemy.Label}");
        }

        _queue.Clear();
        for (var i = 0; i < build.Extras[e].Count; i++)
        {
            _queue.Add((attack.Index, LineKind.Reaction));
        }

        RunQueue(pc, state, ctx, fixedTarget: true);
        CheckOver();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The Action and the Bonus Action.
    // ------------------------------------------------------------------------------------------------------------------

    private void PcAction(Creature pc, PcState state, PcTurnContext ctx)
    {
        var build = state.Build;
        var target = ctx.Target;
        var attackValue = build.ActionQueue.Length > 0 && target is not null
            ? QueueValue(pc, ctx, build.ActionQueue, LineKind.Action, target, ctx.PowerOn)
            : double.NegativeInfinity;
        var bestSave = -1;
        var bestSaveValue = double.NegativeInfinity;
        foreach (var s in build.ActionSaves)
        {
            if (!CanCast(pc, state, s))
            {
                continue;
            }

            var value = SaveEffectValue(pc, build, s);
            if (value > bestSaveValue)
            {
                bestSave = s;
                bestSaveValue = value;
            }
        }

        if (bestSave >= 0 && (bestSaveValue > attackValue || build.ActionQueue.Length == 0))
        {
            CastSaveEffect(pc, state, bestSave);
            return;
        }

        if (build.ActionQueue.Length > 0 || build.SurgeExtras.Length > 0)
        {
            RunAttackAction(pc, state, ctx, withAction: build.ActionQueue.Length > 0);
        }
    }

    /// <summary>The Attack action's attacks, then each Action Surge with a use left (spent greedily, one use each per turn).</summary>
    private void RunAttackAction(Creature pc, PcState state, PcTurnContext ctx, bool withAction)
    {
        var build = state.Build;
        _queue.Clear();
        if (withAction)
        {
            // The Attack action is taken when the Action makes a weapon attack; spell attacks are the casting action.
            foreach (var a in build.ActionQueue)
            {
                _queue.Add((a, LineKind.Action));
                ctx.AttackActionTaken |= build.Attacks[a].Weapon;
            }
        }

        foreach (var e in build.SurgeExtras)
        {
            var extra = build.Extras[e];
            if (!state.IsActive(extra.Source.Number) || !state.CanSpend(build.ExtraSlot[e]) || ctx.Target is null)
            {
                continue;
            }

            state.Spend(build.ExtraSlot[e]);
            if (_log is not null)
            {
                _log.Line($"  {extra.Source.Label}");
            }

            // Action Surge is a second Attack action when its attacks are weapon attacks (it lets the offhand attack follow).
            ctx.AttackActionTaken |= build.Attacks[build.ExtraAttack[e]].Weapon;
            for (var i = 0; i < extra.Count; i++)
            {
                _queue.Add((build.ExtraAttack[e], LineKind.Surge));
            }
        }

        RunQueue(pc, state, ctx, fixedTarget: false);
    }

    private void PcBonusAction(Creature pc, PcState state, PcTurnContext ctx)
    {
        var build = state.Build;
        if (!ctx.BonusAction)
        {
            return;
        }

        var target = PickCurrent(pc, ctx, FirstAttackIsMelee(build));
        var best = 0;
        var bestIndex = -1;
        var bestValue = 0.0;

        if (build.BonusQueue.Length > 0 && target is not null)
        {
            var value = BonusQueueValue(pc, state, ctx, target);
            if (value > bestValue)
            {
                (best, bestIndex, bestValue) = (1, 0, value);
            }
        }

        foreach (var e in build.BonusExtras)
        {
            var extra = build.Extras[e];
            if (target is null || !state.IsActive(extra.Source.Number) || !state.CanSpend(build.ExtraSlot[e]) || !Triggered(extra.Trigger, ctx))
            {
                continue;
            }

            var attack = build.Attacks[build.ExtraAttack[e]];
            var value = extra.Count * AttackValue(pc, ctx, attack, LineKind.Hew, target, ctx.PowerOn);
            if (value > bestValue)
            {
                (best, bestIndex, bestValue) = (2, e, value);
            }
        }

        foreach (var s in build.BonusSaves)
        {
            if (!CanCast(pc, state, s))
            {
                continue;
            }

            var value = SaveEffectValue(pc, build, s);
            if (value > bestValue)
            {
                (best, bestIndex, bestValue) = (3, s, value);
            }
        }

        switch (best)
        {
            case 1:
                ctx.BonusAction = false;
                _queue.Clear();
                foreach (var a in build.BonusQueue)
                {
                    if (!build.Attacks[a].A.Offhand || ctx.AttackActionTaken)
                    {
                        _queue.Add((a, LineKind.BonusAction));
                    }
                }

                RunQueue(pc, state, ctx, fixedTarget: false);
                break;
            case 2:
                ctx.BonusAction = false;
                var extra = build.Extras[bestIndex];
                state.Spend(build.ExtraSlot[bestIndex]);
                if (_log is not null)
                {
                    _log.Line($"  {extra.Source.Label} (Bonus Action)");
                }

                _queue.Clear();
                for (var i = 0; i < extra.Count; i++)
                {
                    _queue.Add((build.ExtraAttack[bestIndex], LineKind.Hew));
                }

                RunQueue(pc, state, ctx, fixedTarget: false);
                break;
            case 3:
                ctx.BonusAction = false;
                CastSaveEffect(pc, state, bestIndex);
                break;
        }
    }

    private static bool Triggered(string trigger, PcTurnContext ctx) => trigger switch
    {
        V.Triggers.Always => true,
        V.Triggers.Hit => ctx.HitTrigger,
        V.Triggers.Crit => ctx.CritTrigger,
        V.Triggers.CritOrKill => ctx.CritTrigger || ctx.KillTrigger,
        _ => false,
    };

    // ------------------------------------------------------------------------------------------------------------------
    // Attacks.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>The current target if it can still be attacked this way, otherwise a new one by policy.</summary>
    private Creature? PickCurrent(Creature pc, PcTurnContext ctx, bool melee)
    {
        if (ctx.Target is { } current && (Attackable(pc, current) || (_setup.Dummy && !current.Dead)) && Reachable(pc, current, melee))
        {
            return current;
        }

        ctx.Target = PickTarget(pc, melee);
        return ctx.Target;
    }

    private void RunQueue(Creature pc, PcState state, PcTurnContext ctx, bool fixedTarget)
    {
        var build = state.Build;
        for (var position = 0; position < _queue.Count; position++)
        {
            var (a, kind) = _queue[position];
            var attack = build.Attacks[a];
            Creature? target;
            if (fixedTarget)
            {
                target = ctx.Target is { } t && (Attackable(pc, t) || _setup.Dummy) ? t : null;
            }
            else
            {
                target = PickCurrent(pc, ctx, attack.Melee);
            }

            if (target is null || pc.Dead || !pc.Up)
            {
                return;
            }

            MakeAttack(pc, state, ctx, attack, kind, target, position + 1);
        }
    }

    /// <summary>
    /// attack_action_only riders apply to the Attack action and Action Surge when the attack is a WEAPON attack (a spell
    /// attack made with the Action is the spell's casting action, not the Attack action); Cleave only under its ruling.
    /// </summary>
    private static bool RiderAttackAction(LineKind kind, PcAttack attack, ResolvedRulings rulings) =>
        (kind is LineKind.Action or LineKind.Surge && attack.Weapon) || (kind == LineKind.Cleave && rulings.CleavePartOfAttackAction);

    private static int PowerPenalty(PcBuild build, PcAttack attack, int on)
    {
        var penalty = 0;
        foreach (var p in attack.PowerAttacks)
        {
            if ((on & (1 << p)) != 0)
            {
                penalty += build.PowerAttacks[p].Penalty;
            }
        }

        return penalty;
    }

    private static int PowerBonus(PcBuild build, PcAttack attack, int on)
    {
        var bonus = 0;
        foreach (var p in attack.PowerAttacks)
        {
            if ((on & (1 << p)) != 0)
            {
                bonus += build.PowerAttacks[p].Bonus;
            }
        }

        return bonus;
    }

    private (bool Advantage, bool Disadvantage) SourceModes(PcBuild build, PcTurnContext ctx, PcAttack attack, LineKind kind)
    {
        var advantage = false;
        var disadvantage = false;
        if (kind.UsesAdvantageSources())
        {
            foreach (var i in attack.Advantage)
            {
                if ((ctx.Present & (1UL << i)) != 0)
                {
                    if (build.Advantage[i].Mode == V.AdvantageModes.Advantage)
                    {
                        advantage = true;
                    }
                    else
                    {
                        disadvantage = true;
                    }
                }
            }
        }

        return (advantage, disadvantage);
    }

    private static int TargetArmorClass(Creature target, PcAttack attack) => ArmorClass(target, attack.A.IgnoresCover);

    private void MakeAttack(Creature pc, PcState state, PcTurnContext ctx, PcAttack attack, LineKind kind, Creature target, int restFrom)
    {
        var build = state.Build;
        var (advantage, disadvantage) = SourceModes(build, ctx, attack, kind);
        var vex = kind.IsMainTarget() && pc.VexTarget == target.Id;
        if (vex)
        {
            advantage = true;
            pc.VexTarget = -1;
        }

        var mode = AttackMode(pc, target, attack.Melee, advantage, disadvantage, out var autoCrit);
        var penalty = PowerPenalty(build, attack, ctx.PowerOn);
        var dice = RollSignedDice(attack.ToHitDice);
        var bonus = attack.AttackBonus - penalty + dice - ExhaustionPenalty(pc);
        var face = RollD20(mode, attack.A.Lucky, attack.A.ElvenAccuracy, out var faces);
        var ac = TargetArmorClass(target, attack);
        var critFace = face >= attack.CritMin;
        var hit = critFace || (face != 1 && face + bonus >= ac);
        if (hit && !critFace && Parried(target, attack.Melee, face + bonus, ac))
        {
            hit = false;
        }

        var crit = hit && (critFace || autoCrit);
        if (_log is not null)
        {
            _log.Line($"  {attack.A.Name} ({kind.Label()}) vs {target.Label}: d20 {faces}{Signed(bonus)} = {face + bonus} vs AC {ac} → {(crit ? "critical hit" : hit ? "hit" : "miss")}{(vex ? " (Vex)" : "")}");
        }

        if (hit)
        {
            PcHit(pc, state, ctx, attack, kind, target, crit, restFrom);
        }
        else
        {
            PcMiss(pc, state, attack, kind, target);
        }
    }

    /// <summary>
    /// A monster's parry reaction, spent when its AC bonus turns a hit (not a critical one) into a miss. A true Parry ("adds
    /// 2 to its AC against one melee attack that would hit it") covers that one attack. A spell parry (Shield: "+5 bonus to
    /// AC, including against the triggering attack, until the start of your next turn", 2014 alike) stays up: every later
    /// attack roll against the caster meets it until the caster's next turn starts (<see cref="Creature.ShieldAc"/>), so
    /// the rest of a party's attacks that round face the mage's AC 20, not its 15.
    /// </summary>
    private bool Parried(Creature target, bool melee, int total, int ac)
    {
        if (target.T.Parries.Length == 0 || !target.ReactionAvailable || target.Incapacitated || target.Surprised)
        {
            return false;
        }

        foreach (var parry in target.T.Parries)
        {
            if ((parry.ParryMeleeOnly && !melee) || total >= ac + parry.AcBonus || !Available(target, parry))
            {
                continue;
            }

            target.ReactionAvailable = false;
            Consume(target, parry);
            if (parry.IsSpell)
            {
                target.ShieldAc = Math.Max(target.ShieldAc, parry.AcBonus);
            }

            if (_log is not null)
            {
                _log.Line($"    {target.Label} uses {parry.Name} (+{parry.AcBonus} AC{(parry.IsSpell ? " until the start of its next turn" : "")}): the attack misses");
            }

            return true;
        }

        return false;
    }

    private static int RiderType(ResolvedRider rider, PcAttack attack) =>
        rider.DamageType is { } type ? DamageTypes.Of(type) : attack.Type;

    private void PcHit(Creature pc, PcState state, PcTurnContext ctx, PcAttack attack, LineKind kind, Creature target, bool crit, int restFrom)
    {
        var build = state.Build;
        var rulings = build.Rulings;
        var damage = _hit;
        Array.Clear(damage);
        var weaponTimes = crit ? 2 : 1;

        // Savage Attacker: once per turn, the better of two rolls of the weapon's dice.
        var savage = false;
        foreach (var s in attack.Rerolls)
        {
            var reroll = build.Rerolls[s];
            if (!state.IsActive(reroll.Source.Number) || ctx.RerollOnce[s])
            {
                continue;
            }

            if (DecideOption(pc, state, ctx, reroll.Policy, reroll.UseValue, crit, OptionKind.Reroll, s, attack, kind, target, restFrom))
            {
                ctx.RerollOnce[s] = true;
                savage = true;
                break;
            }
        }

        var remap = attack.A.WeaponRemap;
        var ownEa = attack.ElementalAdept[attack.Type];
        int weapon;
        if (savage)
        {
            if (crit && !rulings.SavageAttackerOnCritDice)
            {
                weapon = Math.Max(RollTerms(attack.Dice, 1, remap, ownEa), RollTerms(attack.Dice, 1, remap, ownEa)) + RollTerms(attack.Dice, 1, remap, ownEa);
            }
            else
            {
                weapon = Math.Max(RollTerms(attack.Dice, weaponTimes, remap, ownEa), RollTerms(attack.Dice, weaponTimes, remap, ownEa));
            }
        }
        else
        {
            weapon = RollTerms(attack.Dice, weaponTimes, remap, ownEa);
        }

        damage[attack.Type] += weapon + attack.Flat[(int)kind] + PowerBonus(build, attack, ctx.PowerOn);

        foreach (var r in attack.Riders)
        {
            var rider = build.Riders[r];
            if ((rider.AttackActionOnly && !RiderAttackAction(kind, attack, rulings)) || !state.IsActive(rider.Source.Number) || rider.When == V.When.OnMiss)
            {
                continue;
            }

            var apply = false;
            if (rider.When == V.When.OnCrit)
            {
                apply = crit;
            }
            else if (!rider.IsOptional)
            {
                apply = true;
            }
            else
            {
                var slot = build.RiderSlot[r];
                var available = !(rider.When == V.When.FirstHitPerTurn && ctx.RiderOnce[r]) &&
                                !(slot >= 0 && (kind == LineKind.Reaction || !state.CanSpend(slot))) &&
                                !(rider.ActionCost == V.ActionCosts.BonusAction && !ctx.BonusAction);
                if (available && DecideOption(pc, state, ctx, rider.Policy, rider.UseValue, crit, OptionKind.Rider, r, attack, kind, target, restFrom))
                {
                    apply = true;
                    if (rider.When == V.When.FirstHitPerTurn)
                    {
                        ctx.RiderOnce[r] = true;
                    }

                    state.Spend(slot);
                    if (rider.ActionCost == V.ActionCosts.BonusAction)
                    {
                        ctx.BonusAction = false;
                    }
                }
            }

            if (!apply)
            {
                continue;
            }

            var type = RiderType(rider, attack);
            var times = crit && rider.CritDoubles ? 2 : 1;
            damage[type] += RollTerms(rider.Damage.Dice, times, rulings.GwfOnRiders ? remap : null, attack.ElementalAdept[type]) + rider.Damage.Flat;
            if (_log is not null)
            {
                _log.Line($"    + {rider.Source.Label}");
            }
        }

        // Conditions on hit: chosen now (by policy), imposed after the damage, on the main target only.
        _chosenConditions.Clear();
        if (kind.IsMainTarget())
        {
            foreach (var c in attack.Conditions)
            {
                var source = build.ConditionsOnHit[c];
                var condition = Cond.Of(source.Condition);
                var slot = build.ConditionSlot[c];
                if (!state.IsActive(source.Source.Number) || (source.When == V.When.FirstHitPerTurn && ctx.ConditionOnce[c]) ||
                    !state.CanSpend(slot) || target.Has(condition) || target.T.ImmuneTo(condition) || (condition == Cond.Unconscious && target.Down))
                {
                    continue;
                }

                if (DecideOption(pc, state, ctx, source.Policy, source.UseValue, crit, OptionKind.Condition, c, attack, kind, target, restFrom))
                {
                    if (source.When == V.When.FirstHitPerTurn)
                    {
                        ctx.ConditionOnce[c] = true;
                    }

                    state.Spend(slot);
                    _chosenConditions.Add(c);
                }
            }
        }

        var result = ApplyDamage(pc, target, damage, attack.Magical, attack.Silvered, attack.Adamantine, crit, false);

        if (kind.IsMainTarget())
        {
            if (attack.A.Mastery == V.Masteries.Vex && result.Dealt > 0)
            {
                pc.VexTarget = target.Id;
                pc.VexTurn = _turnId;
            }

            if (attack.A.Mastery == V.Masteries.Sap && !target.Dead)
            {
                target.SappedBy = pc.Id;
            }

            foreach (var c in _chosenConditions)
            {
                var source = build.ConditionsOnHit[c];
                if (target.Dead || target.Has(Cond.Of(source.Condition)))
                {
                    continue;
                }

                var dc = source.DcFor(attack.A.Name);
                var template = build.OnHitCondition[c];
                var token = source.Concentration && source.Source.Number == build.ConcentrationNumber ? pc.ConcentrationToken : 0;
                if (!SaveAgainst(target, source.Ability, dc, source.Magical, true, null, false, source.Source.Label))
                {
                    AddCondition(pc, target, template, token);
                }
            }

            if (attack.A.Mastery == V.Masteries.Topple && !target.Dead && !target.Has(Cond.Prone) && !target.Down && !target.T.ImmuneTo(Cond.Prone))
            {
                if (!SaveAgainst(target, V.Abilities.Con, attack.A.MasterySaveDc, false, true, null, false, "Topple"))
                {
                    AddCondition(pc, target, ProneTemplate, 0);
                }
            }
        }

        Retaliate(pc, target, attack.Melee);

        // Cleave is a further attack: not once the burn has dropped the build.
        if (kind.IsMainTarget() && attack.A.Mastery == V.Masteries.Cleave && attack.Melee && !ctx.CleaveUsed && CanStillAct(pc))
        {
            ctx.CleaveUsed = true;
            var second = CleaveTarget(pc, target);
            if (second is not null)
            {
                MakeAttack(pc, state, ctx, attack, LineKind.Cleave, second, restFrom);
            }
        }

        ctx.HitTrigger = true;
        if (crit && attack.MeleeWeapon)
        {
            ctx.CritTrigger = true;
        }

        if (result.Dropped && attack.MeleeWeapon)
        {
            ctx.KillTrigger = true;
        }
    }

    /// <summary>Cleave's second creature: another standing enemy on the front line (the harness: present with the target's second_target_rate).</summary>
    private Creature? CleaveTarget(Creature pc, Creature first)
    {
        if (_setup.Dummy)
        {
            return _setup.CleaveDummy >= 0 && Chance(_setup.SecondTargetRate) ? _c[_setup.CleaveDummy] : null;
        }

        _picked.Clear();
        foreach (var c in _c)
        {
            if (c != first && c.T.Front && c.Up && Attackable(pc, c))
            {
                _picked.Add(c);
            }
        }

        return _picked.Count == 0 ? null : Choose(_picked, _setup.Targeting(pc.Side));
    }

    private void PcMiss(Creature pc, PcState state, PcAttack attack, LineKind kind, Creature target)
    {
        var build = state.Build;
        var damage = _hit;
        Array.Clear(damage);
        var any = false;
        if (attack.A.Mastery == V.Masteries.Graze && attack.A.AbilityModifier > 0 && kind != LineKind.Cleave)
        {
            damage[attack.Type] += attack.A.AbilityModifier;
            any = true;
        }

        foreach (var r in attack.Riders)
        {
            var rider = build.Riders[r];
            if (rider.When != V.When.OnMiss || (rider.AttackActionOnly && !RiderAttackAction(kind, attack, build.Rulings)) || !state.IsActive(rider.Source.Number))
            {
                continue;
            }

            var type = RiderType(rider, attack);
            damage[type] += RollTerms(rider.Damage.Dice, 1, null, attack.ElementalAdept[type]) + rider.Damage.Flat;
            any = true;
        }

        if (any)
        {
            ApplyDamage(pc, target, damage, attack.Magical, attack.Silvered, attack.Adamantine, false, false);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Save effects.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether a save effect can be cast now: a use left, and — for a concentration effect — not while the build holds
    /// another concentration or this one still holds a condition.
    /// </summary>
    private static bool CanCast(Creature pc, PcState state, int s)
    {
        var build = state.Build;
        var effect = build.SaveEffects[s];
        if (!state.CanSpend(build.SaveSlot[s]) || !state.IsActive(effect.Source.Number))
        {
            return false;
        }

        return !effect.Concentration || pc.ConcentrationToken == 0 || pc.ConcentrationModifier == effect.Source.Number;
    }

    private void CastSaveEffect(Creature pc, PcState state, int s)
    {
        var build = state.Build;
        var effect = build.SaveEffects[s];
        if (effect.Concentration && pc.ConcentrationToken != 0 && pc.ConcentrationModifier == effect.Source.Number && LinkedConditions(pc.ConcentrationToken) > 0)
        {
            return; // it still holds its condition: nothing to recast
        }

        var targets = _saveTargets;
        if (effect.TargetsFromArea)
        {
            PickArea(pc, effect.Targets, targets);
        }
        else
        {
            PickDistinct(pc, effect.Targets, targets);
        }

        if (targets.Count == 0)
        {
            return;
        }

        state.Spend(build.SaveSlot[s]);
        var token = effect.Concentration ? StartConcentration(pc, effect.Source.Label, effect.Source.Number, deactivates: false) : 0;
        int[]? rolled = null;
        if (build.SaveDamage[s] is { } part)
        {
            rolled = _aoe;
            Array.Clear(rolled);
            rolled[part.Type] = RollTerms(part.Dice, 1, null, effect.ElementalAdeptTypes.Contains(DamageTypes.Name(part.Type))) + part.Flat;
        }

        if (_log is not null)
        {
            _log.Line($"  casts {effect.Source.Label} (DC {effect.Dc} {effect.Ability}) on {string.Join(", ", targets.Select(t => t.Label))}{(rolled is null ? "" : $": rolls {rolled.Sum()}")}");
        }

        foreach (var target in targets)
        {
            ResolveSave(pc, target, effect.Ability, effect.Dc, effect.OnSuccess, effect.Magical, rolled, build.SaveCondition[s], token, effect.Source.Label);
        }

        if (token != 0 && LinkedConditions(token) == 0 && build.SaveCondition[s] is not null)
        {
            EndConcentration(pc, "has nothing left to hold");
        }

        CheckOver();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Decisions: optional hit choices and the power attack, ranked by expected values.
    // ------------------------------------------------------------------------------------------------------------------

    private enum OptionKind
    {
        Rider,
        Reroll,
        Condition,
    }

    /// <summary>
    /// Whether to spend an optional choice on this hit. any_hit, crits_only and crit_or_last are rules (crit_or_last's
    /// "last" = no later attack of this phase's queue could take it). <b>optimal</b> is myopic: spend now iff this hit's
    /// gain minus use_value beats the value of holding, computed by backward induction over the phase's remaining eligible
    /// attacks alone (each later hit or crit spends when that beats holding further). It ignores what the choice changes
    /// elsewhere (a Bonus Action a rider would cost, later turns' uses beyond use_value), where the closed form's exact
    /// programme may choose differently; for an every-hit rider with more than one use left, holding is worth 0.
    /// </summary>
    private bool DecideOption(Creature pc, PcState state, PcTurnContext ctx, string policy, double useValue, bool crit, OptionKind kind, int index,
                              PcAttack attack, LineKind line, Creature target, int restFrom)
    {
        switch (policy)
        {
            case V.Policies.AnyHit:
                return true;
            case V.Policies.CritsOnly:
                return crit;
            case V.Policies.CritOrLast:
                return crit || IsLast(state.Build, kind, index, restFrom);
            default:
                var now = Gain(pc, state, ctx, kind, index, attack, line, target, crit, restFrom) - useValue;
                if (kind == OptionKind.Rider)
                {
                    var rider = state.Build.Riders[index];
                    var slot = state.Build.RiderSlot[index];
                    if (rider.When == V.When.EveryHit && (slot < 0 || state.UsesLeft[slot] > 1))
                    {
                        return now > 0;
                    }
                }

                return now > HoldValue(pc, state, ctx, kind, index, target, restFrom, useValue);
        }
    }

    private bool Applies(PcBuild build, OptionKind kind, int index, PcAttack attack, LineKind line) => kind switch
    {
        OptionKind.Rider => attack.Riders.Contains(index) && (!build.Riders[index].AttackActionOnly || RiderAttackAction(line, attack, build.Rulings)),
        OptionKind.Reroll => attack.Rerolls.Contains(index),
        _ => attack.Conditions.Contains(index) && line.IsMainTarget(),
    };

    private bool IsLast(PcBuild build, OptionKind kind, int index, int restFrom)
    {
        for (var i = restFrom; i < _queue.Count; i++)
        {
            var (a, line) = _queue[i];
            if (Applies(build, kind, index, build.Attacks[a], line))
            {
                return false;
            }
        }

        return true;
    }

    private double HoldValue(Creature pc, PcState state, PcTurnContext ctx, OptionKind kind, int index, Creature target, int restFrom, double useValue)
    {
        var build = state.Build;
        var value = 0.0;
        for (var i = _queue.Count - 1; i >= restFrom; i--)
        {
            var (a, line) = _queue[i];
            var attack = build.Attacks[a];
            if (!Applies(build, kind, index, attack, line))
            {
                continue;
            }

            var (hit, crit) = EstimateOdds(pc, ctx, attack, line, target, ctx.PowerOn);
            var normal = Gain(pc, state, ctx, kind, index, attack, line, target, false, i + 1) - useValue;
            var critical = Gain(pc, state, ctx, kind, index, attack, line, target, true, i + 1) - useValue;
            value = ((hit - crit) * Math.Max(normal, value)) + (crit * Math.Max(critical, value)) + ((1 - hit) * value);
        }

        return value;
    }

    /// <summary>What spending the choice on a hit is expected to add.</summary>
    private double Gain(Creature pc, PcState state, PcTurnContext ctx, OptionKind kind, int index, PcAttack attack, LineKind line, Creature target, bool crit, int restFrom)
    {
        var build = state.Build;
        switch (kind)
        {
            case OptionKind.Rider:
            {
                var rider = build.Riders[index];
                var type = RiderType(rider, attack);
                var dice = build.RiderDiceMean[index] * (crit && rider.CritDoubles ? 2 : 1);
                return (dice + rider.Damage.Flat) * Factor(target, type, attack.Magical, attack.Silvered, attack.Adamantine);
            }

            case OptionKind.Reroll:
                return (crit ? (build.Rulings.SavageAttackerOnCritDice ? attack.SavageGainCritAll : attack.SavageGain) : attack.SavageGain) *
                       Factor(target, attack.Type, attack.Magical, attack.Silvered, attack.Adamantine);
            default:
            {
                // A condition is worth what Advantage adds to the rest of this phase's attacks, times the chance it lands.
                var source = build.ConditionsOnHit[index];
                var fail = SavingThrowFail(target, source.Ability, source.DcFor(attack.A.Name), source.Magical);
                var gain = 0.0;
                for (var i = restFrom; i < _queue.Count; i++)
                {
                    var (a, later) = _queue[i];
                    if (!later.IsMainTarget())
                    {
                        continue;
                    }

                    var next = build.Attacks[a];
                    var plain = AttackValue(pc, ctx, next, later, target, ctx.PowerOn);
                    var withAdvantage = AttackValue(pc, ctx, next, later, target, ctx.PowerOn, forceAdvantage: true);
                    gain += withAdvantage - plain;
                }

                return fail * gain;
            }
        }
    }

    private double SavingThrowFail(Creature target, string ability, int dc, bool magical)
    {
        var index = CombatantTemplate.AbilityIndex(ability);
        var autoFail = index <= 1 && (target.Has(Cond.Paralyzed) || target.Has(Cond.Stunned) || target.Unconscious || target.Has(Cond.Petrified));
        var advantage = (magical && target.T.MagicResistance) || (index == 1 && target.IsDodging);
        var disadvantage = (index == 1 && target.Has(Cond.Restrained)) || ExhaustionDisadvantage(target);
        var bonus = target.T.Saves[index] + (index == 1 ? target.T.CoverBonus : 0) - ExhaustionPenalty(target);
        return SavingThrowOdds.FailChance(dc, bonus, D20.Resolve(advantage, disadvantage), autoFail: autoFail);
    }

    private (double Hit, double Crit) EstimateOdds(Creature pc, PcTurnContext ctx, PcAttack attack, LineKind line, Creature target, int powerOn, bool forceAdvantage = false)
    {
        var build = pc.Pc!.Build;
        var (advantage, disadvantage) = SourceModes(build, ctx, attack, line);
        advantage |= forceAdvantage || (line.IsMainTarget() && pc.VexTarget == target.Id);
        var mode = AttackMode(pc, target, attack.Melee, advantage, disadvantage, out var autoCrit, consume: false);
        return Odds(attack.AttackBonus - PowerPenalty(build, attack, powerOn) - ExhaustionPenalty(pc), TargetArmorClass(target, attack), attack.CritMin,
            mode, attack.A.Lucky, attack.A.ElvenAccuracy, attack.ToHitPmf, attack.OddsKey, autoCrit);
    }

    /// <summary>E[damage] of one attack made this way against the target: weapon, flat parts, power attack and every rider applied without a choice.</summary>
    private double AttackValue(Creature pc, PcTurnContext ctx, PcAttack attack, LineKind line, Creature target, int powerOn, bool forceAdvantage = false)
    {
        var state = pc.Pc!;
        var build = state.Build;
        var (hit, crit) = EstimateOdds(pc, ctx, attack, line, target, powerOn, forceAdvantage);
        var factor = Factor(target, attack.Type, attack.Magical, attack.Silvered, attack.Adamantine);
        var flat = attack.Flat[(int)line] + PowerBonus(build, attack, powerOn);
        var normal = (attack.DiceMean + flat) * factor;
        var critical = ((2 * attack.DiceMean) + flat) * factor;
        var miss = attack.A.Mastery == V.Masteries.Graze && attack.A.AbilityModifier > 0 ? attack.A.AbilityModifier * factor : 0;
        foreach (var r in attack.Riders)
        {
            var rider = build.Riders[r];
            if ((rider.AttackActionOnly && !RiderAttackAction(line, attack, build.Rulings)) || !state.IsActive(rider.Source.Number))
            {
                continue;
            }

            var riderFactor = Factor(target, RiderType(rider, attack), attack.Magical, attack.Silvered, attack.Adamantine);
            var mean = build.RiderDiceMean[r];
            if (rider.When == V.When.OnCrit)
            {
                critical += mean * riderFactor;
            }
            else if (rider.When == V.When.OnMiss)
            {
                miss += (mean + rider.Damage.Flat) * riderFactor;
            }
            else if (!rider.IsOptional)
            {
                normal += (mean + rider.Damage.Flat) * riderFactor;
                critical += ((rider.CritDoubles ? 2 : 1) * mean + rider.Damage.Flat) * riderFactor;
            }
        }

        return ((hit - crit) * normal) + (crit * critical) + ((1 - hit) * miss);
    }

    private double QueueValue(Creature pc, PcTurnContext ctx, int[] attacks, LineKind line, Creature target, int powerOn)
    {
        var build = pc.Pc!.Build;
        var value = 0.0;
        foreach (var a in attacks)
        {
            value += AttackValue(pc, ctx, build.Attacks[a], line, target, powerOn);
        }

        return value;
    }

    private double BonusQueueValue(Creature pc, PcState state, PcTurnContext ctx, Creature target)
    {
        var build = state.Build;
        var value = 0.0;
        foreach (var a in build.BonusQueue)
        {
            if (!build.Attacks[a].A.Offhand || ctx.AttackActionTaken || build.ActionQueue.Any(q => build.Attacks[q].Weapon))
            {
                value += AttackValue(pc, ctx, build.Attacks[a], LineKind.BonusAction, target, ctx.PowerOn);
            }
        }

        return value;
    }

    /// <summary>A save effect's expected total over the creatures it would catch (the policy's picks, or the area's count).</summary>
    private double SaveEffectValue(Creature pc, PcBuild build, int s)
    {
        var effect = build.SaveEffects[s];
        var part = build.SaveDamage[s];
        var count = 0;
        var total = 0.0;
        foreach (var c in _c)
        {
            if (c.Side == pc.Side || !c.Up)
            {
                continue;
            }

            count++;
            var fail = SavingThrowFail(c, effect.Ability, effect.Dc, effect.Magical);
            var mean = part is null ? 0 : part.Mean * Factor(c, part.Type, effect.Magical);
            var evasion = Evades(c, effect.Ability, effect.OnSuccess);
            var onFail = evasion ? mean / 2 : mean;
            var onSuccess = effect.OnSuccess == V.OnSuccess.Half && !evasion ? mean / 2 : 0;
            var conditionValue = build.SaveCondition[s] is { } condition ? ConditionValue(c, condition.Condition) : 0;
            total += (fail * (onFail + conditionValue)) + ((1 - fail) * onSuccess);
        }

        if (count == 0)
        {
            return 0;
        }

        return total / count * Math.Min(effect.Targets, _setup.Dummy ? Math.Min(effect.Targets, _setup.MainDummies) : count);
    }

    private static int FixedPowerAttacks(PcState state)
    {
        var on = 0;
        for (var p = 0; p < state.Build.PowerAttacks.Length; p++)
        {
            var power = state.Build.PowerAttacks[p];
            if (power.Policy == V.PowerAttackPolicies.Always && state.IsActive(power.Source.Number))
            {
                on |= 1 << p;
            }
        }

        return on;
    }

    /// <summary>
    /// Power attack "auto": per turn, the on/off combination (always ones fixed on) that maximises the turn's expected
    /// damage against the current target — the Attack action's attacks (with any Action Surge), the Bonus Action attacks,
    /// and a crit-triggered bonus attack weighted by the chance of a crit. A tie keeps it off, as in the closed form.
    /// </summary>
    private void ChoosePowerAttacks(Creature pc, PcState state, PcTurnContext ctx, bool action)
    {
        var build = state.Build;
        var fixedOn = FixedPowerAttacks(state);
        ctx.PowerOn = fixedOn;
        if (build.PowerAttacks.Length == 0 || ctx.Target is not { } target)
        {
            return;
        }

        var auto = _autoPower;
        auto.Clear();
        for (var p = 0; p < build.PowerAttacks.Length; p++)
        {
            if (build.PowerAttacks[p].Policy == V.PowerAttackPolicies.Auto && state.IsActive(build.PowerAttacks[p].Source.Number))
            {
                auto.Add(p);
            }
        }

        if (auto.Count == 0)
        {
            return;
        }

        var best = double.NegativeInfinity;
        for (var size = 0; size <= auto.Count; size++)
        {
            for (var mask = 0; mask < 1 << auto.Count; mask++)
            {
                if (System.Numerics.BitOperations.PopCount((uint)mask) != size)
                {
                    continue;
                }

                var on = fixedOn;
                for (var i = 0; i < auto.Count; i++)
                {
                    if ((mask & (1 << i)) != 0)
                    {
                        on |= 1 << auto[i];
                    }
                }

                var value = TurnValue(pc, state, ctx, target, on, action);
                if (value > best + 1e-12)
                {
                    best = value;
                    ctx.PowerOn = on;
                }
            }
        }
    }

    private double TurnValue(Creature pc, PcState state, PcTurnContext ctx, Creature target, int on, bool action)
    {
        var build = state.Build;
        var value = 0.0;
        var noCrit = 1.0;
        var noHit = 1.0;
        if (action)
        {
            foreach (var a in build.ActionQueue)
            {
                Accumulate(pc, ctx, build.Attacks[a], LineKind.Action, target, on, ref value, ref noHit, ref noCrit);
            }
        }

        foreach (var e in build.SurgeExtras)
        {
            if (state.IsActive(build.Extras[e].Source.Number) && state.CanSpend(build.ExtraSlot[e]))
            {
                for (var i = 0; i < build.Extras[e].Count; i++)
                {
                    Accumulate(pc, ctx, build.Attacks[build.ExtraAttack[e]], LineKind.Surge, target, on, ref value, ref noHit, ref noCrit);
                }
            }
        }

        var bonus = 0.0;
        if (ctx.BonusAction)
        {
            foreach (var a in build.BonusQueue)
            {
                bonus += AttackValue(pc, ctx, build.Attacks[a], LineKind.BonusAction, target, on);
            }

            foreach (var e in build.BonusExtras)
            {
                var extra = build.Extras[e];
                if (!state.IsActive(extra.Source.Number) || !state.CanSpend(build.ExtraSlot[e]))
                {
                    continue;
                }

                var chance = extra.Trigger switch
                {
                    V.Triggers.Always => 1,
                    V.Triggers.Hit => 1 - noHit,
                    _ => 1 - noCrit,
                };
                bonus = Math.Max(bonus, chance * extra.Count * AttackValue(pc, ctx, build.Attacks[build.ExtraAttack[e]], LineKind.Hew, target, on));
            }
        }

        return value + bonus;
    }

    private void Accumulate(Creature pc, PcTurnContext ctx, PcAttack attack, LineKind line, Creature target, int on, ref double value, ref double noHit, ref double noCrit)
    {
        value += AttackValue(pc, ctx, attack, line, target, on);
        var (hit, crit) = EstimateOdds(pc, ctx, attack, line, target, on);
        noHit *= 1 - hit;
        if (attack.MeleeWeapon)
        {
            noCrit *= 1 - crit;
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Escape.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A creature Restrained by something it can escape (an escape DC) uses its Action to try, every turn (policy: always
    /// try). A success ends every until-escape condition from that source (a grapple and its restraint together).
    /// </summary>
    private bool TryEscape(Creature c)
    {
        if (c.CondCount[Cond.Restrained] == 0)
        {
            return false;
        }

        foreach (var active in c.Conditions)
        {
            if (active.Condition != Cond.Restrained || active.Duration != DurationKind.UntilEscape || active.Template.EscapeDc <= 0)
            {
                continue;
            }

            var success = EscapeCheck(c, active.Template.EscapeDc, out var text);
            if (_log is not null)
            {
                _log.Line($"  tries to escape: {text} → {(success ? "free" : "still restrained")}");
            }

            if (success)
            {
                var source = active.Source;
                RemoveMatching(c, DurationKind.UntilEscape, source, -1, "escaped");
            }

            return true;
        }

        return false;
    }
}
