namespace DndMcp.Domain.Simulation;

/// <summary>
/// A fight picked up where a live one stands (contract §11.3, balance_simulate <c>from_state</c>): each seeded creature is
/// set from its <see cref="CompiledStart"/> instead of its fresh start of fight, and the tracker's order replaces the
/// initiative roll.
///
/// <para>
/// <b>No die is drawn</b> and every creature is set the same way every fight, in creature order, so a fight is still a
/// pure function of (seed, i): the fresh creatures beside the seeded ones roll exactly what they always rolled.
/// </para>
/// <para>
/// <b>The engine's own rules settle the state</b>: conditions go through <see cref="AddCondition"/> (quietly), after every
/// creature's HP and concentration are set, so incapacitation ends a concentration and releases grapples, Unconscious adds
/// Prone, and a sixth exhaustion level kills, as they would mid-fight. A condition its source can no longer hold (a
/// concentration that did not survive the seed, a grapple by a creature down or incapacitated) is not added, whichever of
/// the two creatures comes first in the order — the result never depends on creature order.
/// </para>
/// </summary>
internal sealed partial class Fight
{
    /// <summary>
    /// One creature's start, first pass: HP and temp HP, death state, uses, recharge, pools, legendary counts, setups and
    /// concentration — everything but the conditions. It takes the place of <see cref="StartOfFight"/>: the build's temp_hp
    /// grant is replaced when a value is seeded, and a concentration modifier is up exactly when it is concentrated on.
    /// </summary>
    private void ApplyStart(Creature c, CompiledStart s)
    {
        var log = _log;
        _log = null;
        _seeding = true;
        try
        {
            if (s.Placeholder)
            {
                c.Dead = true;
                c.Hp = 0;
                c.StartHp = 0;
                c.TempHp = 0;
                return;
            }

            c.Hp = s.Hp;
            c.StartHp = s.Hp;
            GainTempHp(c, s.TempHp ?? c.T.TempHpAtStart);
            if (s.Down)
            {
                c.Down = true;
                c.Dropped = true;
                c.Stable = s.Stable;
                c.DeathSuccesses = s.DeathSuccesses;
                c.DeathFailures = s.DeathFailures;
            }

            foreach (var (slot, uses) in s.LimitedUses)
            {
                c.UsesLeft[slot] = uses;
            }

            foreach (var slot in s.Spent)
            {
                c.RechargeReady[slot] = false;
            }

            foreach (var (pool, slots) in s.Pools)
            {
                c.PoolLeft[pool] = slots;
            }

            c.LegendaryUsesLeft = s.LegendaryActionsLeft ?? c.LegendaryUsesLeft;
            c.LegendaryResistanceLeft = s.LegendaryResistanceLeft ?? c.LegendaryResistanceLeft;
            c.ReactionAvailable = !s.ReactionUsed;
            c.RelentlessUsed = s.RelentlessUsed;
            if (c.Pc is { } pc)
            {
                var build = pc.Build;
                foreach (var (slot, uses) in s.PcUses)
                {
                    pc.UsesLeft[slot] = uses;
                }

                for (var n = 0; n < pc.Active.Length; n++)
                {
                    pc.Active[n] = !build.Gated[n];
                }

                // A concentration modifier that is up only while concentrated on (a setup, or a no-setup rider like Hex)
                // follows the seeded concentration below; a cast save effect stays castable either way.
                var number = build.ConcentrationNumber;
                if (number > 0 && (build.Gated[number] || !IsSaveEffect(build, number)))
                {
                    pc.Active[number] = false;
                }

                // A spell with no slot left (review CR03): down, and never set up or cast; one it still concentrates on is
                // raised again below and runs until that concentration ends.
                foreach (var blocked in s.Unavailable)
                {
                    pc.Blocked[blocked] = true;
                    pc.Active[blocked] = false;
                }

                if (s.UnavailableAttacks.Length > 0)
                {
                    pc.ActionQueue = build.ActionQueue.Where(a => !s.UnavailableAttacks.Contains(a)).ToArray();
                    pc.BonusQueue = build.BonusQueue.Where(a => !s.UnavailableAttacks.Contains(a)).ToArray();
                }

                foreach (var setup in s.ActiveSetups)
                {
                    pc.Active[setup] = true;
                }

                pc.Acted = s.HasActed;
            }

            if (s.Concentration is { } concentration)
            {
                StartConcentration(c, concentration.Label, concentration.Modifier, concentration.Deactivates);
                if (concentration.Modifier > 0 && c.Pc is { } held)
                {
                    held.Active[concentration.Modifier] = true;
                }
            }
        }
        finally
        {
            _seeding = false;
            _log = log;
        }
    }

    /// <summary>
    /// The second pass, once every creature's state and concentration are set: each seeded creature's exhaustion levels and
    /// conditions in creature order, then Prone for a creature that starts down (as dropping to 0 HP adds it). Logs the
    /// start state when the fight is replayed.
    /// </summary>
    private void ApplyStartConditions()
    {
        var log = _log;
        _log = null;
        _seeding = true;
        try
        {
            foreach (var c in _c)
            {
                if (c.T.Start is not { Placeholder: false } s)
                {
                    continue;
                }

                for (var level = 0; level < s.Exhaustion; level++)
                {
                    AddCondition(null, c, StartPreparation.Exhaustion, 0, quiet: true);
                }

                foreach (var condition in s.Conditions)
                {
                    var source = condition.Source >= 0 ? _c[condition.Source] : null;
                    var token = 0;
                    if (condition.Held)
                    {
                        token = source!.ConcentrationToken;
                        if (token == 0)
                        {
                            continue;
                        }
                    }

                    if (condition.Template.Duration == DurationKind.UntilEscape && (source is null || source.Incapacitated))
                    {
                        continue;
                    }

                    AddCondition(source, c, condition.Template, token, quiet: true, skipTurnEnds: condition.SkipTurnEnds, skipTurnStarts: condition.SkipTurnStarts);
                }

                if (c.Down && c.T.PcLike && !c.Has(Cond.Prone))
                {
                    AddCondition(null, c, ProneTemplate, 0, quiet: true);
                }
            }
        }
        finally
        {
            _seeding = false;
            _log = log;
        }

        if (_log is not null)
        {
            foreach (var c in _c)
            {
                if (c.T.Start is { } s)
                {
                    _log.Line(s.Placeholder
                        ? $"Start: {c.Label} (dead; a placeholder holding its place in the order)"
                        : $"Start: {c.Label} ({Status(c)}{(c.ConcentrationToken != 0 ? $", concentrating on {c.ConcentrationLabel}" : "")})");
                }
            }
        }
    }

    /// <summary>A resumed fight's order (the tracker's), in place of the initiative roll: no die, no surprise.</summary>
    private void UseFixedOrder(int[] order)
    {
        Array.Copy(order, _order, _order.Length);
        if (_log is not null)
        {
            _log.Line($"Resumed in round {_setup.StartRound} at {_c[_order[_setup.StartAt]].Label}'s turn (its start of turn is played again).");
            _log.Line("Order: " + string.Join(", ", _order.Select(id => _c[id].Label)));
        }
    }
}
