using System.Text;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>What one instance of damage did.</summary>
/// <param name="Dealt">The damage after resistances (what "raw" damage counts).</param>
/// <param name="Effective">What it removed: capped at the temporary and real hit points the target had.</param>
/// <param name="Dropped">It reduced the target to 0 HP (the crit_or_kill trigger's "reduce a creature to 0 hit points").</param>
/// <param name="Killed">The target died of it.</param>
internal readonly record struct DamageResult(int Dealt, int Effective, bool Dropped, bool Killed);

/// <summary>
/// Damage, death, healing, concentration and conditions (contract §5.4 "Death and HP", "Concentration", "Conditions").
///
/// <para>
/// <b>Per type, then per creature.</b> A hit's damage arrives summed per type (<see cref="DamageTypes"/>); each type is
/// halved for a successful save first, then Resistance (floor), Vulnerability (×2), Immunity (0), floored at 0 — the same
/// order and rounding as the closed form's <c>DamageAdjustment</c> — with a qualified adjustment ("from nonmagical
/// attacks") applying only when the damage is not magical/silvered/adamantine as its qualifier says. Petrified adds
/// resistance to everything. Temporary HP absorb first; the rest comes off hit points.
/// </para>
/// <para>
/// <b>At 0 HP</b> a PC-like creature falls unconscious and prone and starts dying; leftover damage of at least its hit
/// point maximum kills it outright (massive damage). Damage while at 0 HP is a death-save failure (two on a crit), or death
/// when it is at least the maximum. Other creatures die at 0 HP, except through Relentless, Undead Fortitude, or a
/// troll's regeneration. An outright kill by hit points (Power Word Kill, the solar's Slaying Longbow and Slaying Bow) is a
/// death with no damage, so none of those apply to it. One Constitution save per damage instance keeps concentration (DC max(10,
/// half), capped at 30 in a 2024 fight).
/// </para>
/// </summary>
internal sealed partial class Fight
{
    private static readonly ConditionTemplate ProneTemplate = new() { Condition = Cond.Prone, Duration = DurationKind.UntilStands };

    /// <summary>Set while a fight's start state is applied (<see cref="ApplyStart"/>): what happened in the live fight is not replayed (no death burst).</summary>
    private bool _seeding;

    private readonly int[] _hit = new int[DamageTypes.Count];
    private readonly int[] _aux = new int[DamageTypes.Count];
    private readonly int[] _aoe = new int[DamageTypes.Count];
    private readonly int[] _adjusted = new int[DamageTypes.Count];

    private static int FloorHalf(int x) => x >= 0 ? x / 2 : -((-x + 1) / 2);

    /// <summary>The damage after the target's adjustments, per type into <see cref="_adjusted"/>; returns the total.</summary>
    private int Adjust(Creature target, int[] perType, bool magical, bool silvered, bool adamantine, bool halve, out int types, out bool radiant)
    {
        var total = 0;
        types = 0;
        radiant = false;
        var petrified = target.Has(Cond.Petrified);
        var t = target.T;
        for (var type = 0; type < DamageTypes.Count; type++)
        {
            var x = perType[type];
            _adjusted[type] = 0;
            if (x == 0)
            {
                continue;
            }

            if (halve)
            {
                x = FloorHalf(x);
            }

            if (type != DamageTypes.Typeless)
            {
                if (Qualifier.Applies(t.Immune[type], magical, silvered, adamantine))
                {
                    continue;
                }

                if (petrified || Qualifier.Applies(t.Resist[type], magical, silvered, adamantine))
                {
                    x = FloorHalf(x);
                }

                if (Qualifier.Applies(t.Vulnerable[type], magical, silvered, adamantine))
                {
                    x *= 2;
                }
            }
            else if (petrified)
            {
                x = FloorHalf(x);
            }

            if (x <= 0)
            {
                continue;
            }

            _adjusted[type] = x;
            total += x;
            types |= 1 << type;
            radiant |= type == DamageTypes.Radiant;
        }

        return total;
    }

    /// <summary>Whether this damage would drop the target to 0 HP (Legendary Resistance's "killing blow" test).</summary>
    private bool WouldDrop(Creature target, int[] perType, bool magical, bool halve)
    {
        if (!target.Up || target.T.InfiniteHp)
        {
            return false;
        }

        return Adjust(target, perType, magical, false, false, halve, out _, out _) >= target.Hp + target.TempHp;
    }

    /// <summary>One instance of damage from one source (a hit, one creature's share of an area, one tick of ongoing damage).</summary>
    internal DamageResult ApplyDamage(Creature? source, Creature target, int[] perType, bool magical, bool silvered, bool adamantine, bool crit, bool halve)
    {
        if (target.Dead)
        {
            return default;
        }

        var total = Adjust(target, perType, magical, silvered, adamantine, halve, out var types, out var radiant);
        if (total <= 0)
        {
            if (_log is not null)
            {
                _log.Line($"    {target.Label} takes no damage");
            }

            return default;
        }

        var described = _log is null ? null : DescribeDamage(total);
        if (source is not null)
        {
            source.DealtRaw += total;
        }

        target.TakenRaw += total;
        if (target.T.InfiniteHp)
        {
            if (source is not null)
            {
                source.DealtEffective += total;
            }

            target.TakenEffective += total;
            if (_log is not null)
            {
                _log.Line($"    {target.Label} takes {described}");
            }

            return new DamageResult(total, total, false, false);
        }

        if ((target.T.RegenerationStops & types) != 0)
        {
            target.RegenerationBlocked = true;
        }

        var before = target.Hp + target.TempHp;
        var absorbed = Math.Min(target.TempHp, total);
        target.TempHp -= absorbed;
        var rest = total - absorbed;
        var dropped = false;
        var killed = false;
        var survives = target.Hp > 0 && rest < target.Hp;
        if (_log is not null && !survives)
        {
            // What the damage does next (a drop, a death, a burst) is logged after it.
            _log.Line($"    {target.Label} takes {described}");
        }

        if (target.Hp > 0)
        {
            if (rest >= target.Hp)
            {
                if (target.T.RelentlessAmount > 0 && !target.RelentlessUsed && total <= target.T.RelentlessAmount)
                {
                    target.RelentlessUsed = true;
                    target.Hp = 1;
                    if (_log is not null)
                    {
                        _log.Line($"    Relentless: {target.Label} drops to 1 HP instead");
                    }
                }
                else if (target.T.UndeadFortitude && !radiant && !crit && UndeadFortitude(target, total))
                {
                    target.Hp = 1;
                }
                else
                {
                    var leftover = rest - target.Hp;
                    target.Hp = 0;
                    dropped = true;
                    killed = DropToZero(target, source, leftover);
                }
            }
            else
            {
                target.Hp -= rest;
            }
        }
        else if (target.Down && target.T.PcLike)
        {
            target.Stable = false;
            if (total >= target.MaxHp)
            {
                if (_log is not null)
                {
                    _log.Line($"    {target.Label} takes {total} at 0 HP, at least its hit point maximum: it dies");
                }

                Die(target, source);
                killed = true;
            }
            else
            {
                target.DeathFailures += crit ? 2 : 1;
                if (_log is not null)
                {
                    _log.Line($"    {target.Label} takes damage at 0 HP: {(crit ? "two death save failures" : "a death save failure")} ({target.DeathFailures})");
                }

                if (target.DeathFailures >= 3)
                {
                    Die(target, source);
                    killed = true;
                }
            }
        }

        var effective = Math.Min(total, before);
        if (source is not null)
        {
            source.DealtEffective += effective;
        }

        target.TakenEffective += effective;
        if (_log is not null && survives)
        {
            _log.Line($"    {target.Label} takes {described} → {Status(target)}");
        }

        if (!target.Dead && target.ConcentrationToken != 0)
        {
            ConcentrationSave(target, total);
        }

        return new DamageResult(total, effective, dropped, killed);
    }

    private string DescribeDamage(int total)
    {
        var parts = new StringBuilder();
        var count = 0;
        for (var type = 0; type < DamageTypes.Count; type++)
        {
            if (_adjusted[type] > 0)
            {
                count++;
                if (parts.Length > 0)
                {
                    parts.Append(" + ");
                }

                parts.Append(_adjusted[type]).Append(' ').Append(DamageTypes.Name(type));
            }
        }

        return count > 1 ? $"{total} ({parts})" : parts.ToString();
    }

    /// <summary>
    /// Undead Fortitude (both editions): damage that drops it to 0 and is neither radiant nor from a critical hit → a
    /// Constitution save, DC 5 + the damage taken; success leaves it at 1 HP. A hit carrying any radiant damage counts as
    /// radiant.
    /// </summary>
    private bool UndeadFortitude(Creature target, int damage)
    {
        var success = SavingThrow(target, V.Abilities.Con, 5 + damage, false, out var text);
        if (_log is not null)
        {
            _log.Line($"    Undead Fortitude: {text} → {(success ? "drops to 1 HP instead" : "fails")}");
        }

        return success;
    }

    /// <summary>At 0 HP: returns whether it died.</summary>
    private bool DropToZero(Creature target, Creature? source, int leftover)
    {
        target.Dropped = true;
        if (target.T.PcLike)
        {
            if (leftover >= target.MaxHp)
            {
                if (_log is not null)
                {
                    _log.Line($"    massive damage: {leftover} left over is at least {target.Label}'s hit point maximum ({target.MaxHp})");
                }

                Die(target, source);
                return true;
            }

            target.Down = true;
            target.Stable = false;
            target.DeathSuccesses = 0;
            target.DeathFailures = 0;
            OnIncapacitated(target, "falls unconscious");
            if (!target.Has(Cond.Prone))
            {
                AddCondition(null, target, ProneTemplate, 0, quiet: true);
            }

            if (_log is not null)
            {
                _log.Line($"    {target.Label} drops to 0 HP: unconscious and dying");
            }

            return false;
        }

        if (target.T.RegeneratesFromZero && target.T.RegenerationAmount > 0)
        {
            target.Down = true;
            OnIncapacitated(target, "drops to 0 HP");
            if (_log is not null)
            {
                _log.Line($"    {target.Label} drops to 0 HP (it dies only if it starts its turn at 0 HP without regenerating)");
            }

            return false;
        }

        Die(target, source);
        return true;
    }

    private void Die(Creature target, Creature? source)
    {
        if (target.Dead)
        {
            return;
        }

        target.Dead = true;
        target.Down = false;
        target.Stable = false;
        target.Hp = 0;
        target.TempHp = 0;
        target.Dropped = true;
        EndConcentration(target, "dies");
        ReleaseGrapples(target);
        if (source is not null && source.Side != target.Side)
        {
            source.Kills++;
        }

        if (_log is not null)
        {
            _log.Line($"    {target.Label} dies");
        }

        // A death on the seed (a sixth exhaustion level) happened in the live fight: its burst is not rolled again.
        if (target.T.DeathBurst is { } burst && !_seeding)
        {
            DeathBurst(target, burst);
        }
    }

    /// <summary>Healing: capped at the maximum; from 0 HP it wakes (still prone) and its death saves reset.</summary>
    internal void Heal(Creature healer, Creature target, int amount, string what)
    {
        if (target.Dead || amount <= 0)
        {
            return;
        }

        if (target.Down)
        {
            target.Down = false;
            target.Stable = false;
            target.DeathSuccesses = 0;
            target.DeathFailures = 0;
            target.Hp = 0;
        }

        var before = target.Hp;
        target.Hp = Math.Min(target.MaxHp, target.Hp + amount);
        if (_log is not null)
        {
            _log.Line($"    {what}: {target.Label} regains {target.Hp - before} HP ({amount} rolled) → {Status(target)}");
        }
    }

    /// <summary>
    /// Temporary hit points never stack: the creature keeps the higher of what it has and what it gains. The only grant
    /// today is a build's temp_hp at the start of the fight (from 0), so the "keeps the higher" half is pinned by the rules
    /// test alone; a second source (a monster's or a spell's temporary hit points) must come through here to keep it.
    /// </summary>
    internal void GainTempHp(Creature c, int amount)
    {
        c.TempHp = Math.Max(c.TempHp, amount);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Concentration.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts concentrating on <paramref name="label"/> and returns the token its conditions carry. A build that already
    /// concentrates on the same modifier keeps that concentration and its token: a setup-paid save effect (the cleric
    /// archetype's Spirit Guardians, <c>setup: action</c>, <c>action_cost: none</c>) runs every turn on the concentration
    /// its setup started, and ending it here would switch the setup off (<see cref="Creature.ConcentrationDeactivates"/>),
    /// so the build would pay its Action to set it up again every turn and never attack. Anything else (a monster's
    /// concentration spell, modifier 0) ends the old concentration first.
    /// </summary>
    internal int StartConcentration(Creature c, string label, int modifier, bool deactivates)
    {
        if (modifier > 0 && c.ConcentrationToken != 0 && c.ConcentrationModifier == modifier)
        {
            return c.ConcentrationToken;
        }

        EndConcentration(c, "starts concentrating on something else");
        c.ConcentrationToken = ++_tokens;
        c.ConcentrationModifier = modifier;
        c.ConcentrationDeactivates = deactivates;
        c.ConcentrationLabel = label;
        return c.ConcentrationToken;
    }

    /// <summary>The concentration save's DC: max(10, ⌊damage/2⌋), capped at 30 in a 2024 fight (70 damage: DC 30 in 2024, 35 in 2014).</summary>
    internal static int ConcentrationDc(int damage, bool is2024)
    {
        var dc = Math.Max(10, damage / 2);
        return is2024 ? Math.Min(dc, 30) : dc;
    }

    /// <summary>One Constitution save against <see cref="ConcentrationDc"/>; a failure ends it.</summary>
    private void ConcentrationSave(Creature c, int damage)
    {
        var dc = ConcentrationDc(damage, _setup.Is2024);
        var success = SavingThrow(c, V.Abilities.Con, dc, false, out var text);
        if (_log is not null)
        {
            _log.Line($"    concentration ({c.ConcentrationLabel}): {text} → {(success ? "kept" : "lost")}");
        }

        if (!success)
        {
            EndConcentration(c, "fails its concentration save");
        }
    }

    /// <summary>Ends what the creature concentrates on: every condition it maintains, and a build modifier stops applying.</summary>
    private void EndConcentration(Creature c, string why)
    {
        if (c.ConcentrationToken == 0)
        {
            return;
        }

        var token = c.ConcentrationToken;
        c.ConcentrationToken = 0;
        foreach (var other in _c)
        {
            for (var i = other.Conditions.Count - 1; i >= 0; i--)
            {
                if (other.Conditions[i].ConcentrationToken == token)
                {
                    RemoveAt(other, i);
                }
            }
        }

        if (c.Pc is { } pc && c.ConcentrationModifier > 0 && c.ConcentrationDeactivates)
        {
            pc.Active[c.ConcentrationModifier] = false;
        }

        if (_log is not null)
        {
            _log.Line($"    {c.Label} {why}: concentration on {c.ConcentrationLabel} ends");
        }

        c.ConcentrationModifier = 0;
        c.ConcentrationLabel = null;
    }

    /// <summary>Incapacitation ends concentration, the Dodge and every grapple the creature holds.</summary>
    private void OnIncapacitated(Creature c, string why)
    {
        EndConcentration(c, why);
        ReleaseGrapples(c);
        c.Dodging = false;
    }

    private void ReleaseGrapples(Creature source)
    {
        foreach (var other in _c)
        {
            for (var i = other.Conditions.Count - 1; i >= 0; i--)
            {
                var active = other.Conditions[i];
                if (active.Source == source.Id && active.Duration == DurationKind.UntilEscape)
                {
                    RemoveAt(other, i);
                }
            }
        }
    }

    private int LinkedConditions(int token)
    {
        var count = 0;
        foreach (var other in _c)
        {
            foreach (var active in other.Conditions)
            {
                if (active.ConcentrationToken == token)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Conditions.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Imposes a condition (refused by an immunity); returns whether it landed. <paramref name="immunity"/> is the imposing
    /// effect's <see cref="MonsterAction.ImmunityIndex"/> (−1: none): when this condition ends, the target becomes immune to
    /// that effect of <paramref name="source"/> (<see cref="RemoveAt"/>). <paramref name="skipTurnEnds"/> (−1: from whose
    /// turn it is now) and <paramref name="skipTurnStarts"/> are a seeded condition's own counts
    /// (<see cref="StartCondition.ImposedDuringResumedTurn"/>): nothing imposed mid-fight ever skips a turn start, because
    /// the turn it was imposed in has already started.
    /// </summary>
    internal bool AddCondition(Creature? source, Creature target, ConditionTemplate template, int concentrationToken, bool quiet = false, int immunity = -1,
                               int skipTurnEnds = -1, int skipTurnStarts = 0)
    {
        if (target.Dead)
        {
            return false;
        }

        var condition = template.Condition;
        if (target.T.ImmuneTo(condition))
        {
            if (_log is not null && !quiet)
            {
                _log.Line($"    {target.Label} is immune to {Cond.Name(condition)}");
            }

            return false;
        }

        target.Conditions.Add(new ActiveCondition
        {
            Condition = condition,
            Source = source?.Id ?? -1,
            Duration = template.Duration,
            RoundsLeft = template.Duration is DurationKind.Rounds or DurationKind.SaveEnds ? template.Rounds : 0,
            SkipTurnEnds = skipTurnEnds >= 0 ? skipTurnEnds
                : template.Duration == DurationKind.UntilEndOfTargetTurn
                    ? (_active == target.Id ? 1 : 0)
                    : (source is not null && _active == source.Id ? 1 : 0),
            SkipTurnStarts = skipTurnStarts,
            Template = template,
            ConcentrationToken = concentrationToken,
            Immunity = source is null ? -1 : immunity,
        });
        target.CondCount[condition]++;
        if (_log is not null && !quiet)
        {
            _log.Line($"    {target.Label} is {Cond.Name(condition)} ({DescribeDuration(template)})");
        }

        if (condition == Cond.Exhaustion && target.Exhaustion >= 6)
        {
            Die(target, source);
            return true;
        }

        if (condition is Cond.Incapacitated or Cond.Stunned or Cond.Paralyzed or Cond.Petrified or Cond.Unconscious)
        {
            OnIncapacitated(target, "is " + Cond.Name(condition));
        }

        if (condition == Cond.Unconscious && !target.Has(Cond.Prone))
        {
            AddCondition(source, target, ProneTemplate, 0, quiet: true);
        }

        return true;
    }

    private static string DescribeDuration(ConditionTemplate template) => template.Duration switch
    {
        DurationKind.UntilStartOfSourceTurn => "until the start of the source's next turn",
        DurationKind.UntilEndOfSourceTurn => "until the end of the source's next turn",
        DurationKind.SaveEnds => $"{template.SaveAbility} save DC {template.SaveDc} ends it",
        DurationKind.Rounds => $"{template.Rounds} rounds",
        DurationKind.UntilEscape => $"escape DC {template.EscapeDc}",
        DurationKind.UntilEndOfTargetTurn => "until the end of its next turn",
        DurationKind.UntilStartOfTargetTurn => "until the start of its next turn",
        DurationKind.UntilStands => "until it stands",
        _ => "for the fight",
    };

    /// <summary>
    /// Ends one condition. Every way a condition ends comes through here (its duration, a repeated save, an escape, the
    /// source's concentration or grapple ending), which is what "the effect ends for it" means for an effect that leaves
    /// immunity behind (<see cref="ActiveCondition.Immunity"/>).
    /// </summary>
    private static void RemoveAt(Creature target, int index)
    {
        var active = target.Conditions[index];
        target.CondCount[active.Condition]--;
        target.Conditions.RemoveAt(index);
        target.BecomeImmuneToEffect(active.Source, active.Immunity);
    }

    /// <summary>
    /// Removes the conditions with this duration (from this source, and of this condition, when given). A seeded until-start
    /// condition imposed during the resumed turn (<see cref="ActiveCondition.SkipTurnStarts"/>, the only conditions with one)
    /// outlives the replayed start of that turn instead: it is only ever matched here at a turn start.
    /// </summary>
    private void RemoveMatching(Creature target, DurationKind duration, int? source, int condition, string why)
    {
        for (var i = target.Conditions.Count - 1; i >= 0; i--)
        {
            var active = target.Conditions[i];
            if (active.Duration == duration && (source is null || active.Source == source) && (condition < 0 || active.Condition == condition))
            {
                if (active.SkipTurnStarts > 0)
                {
                    active.SkipTurnStarts--;
                    target.Conditions[i] = active;
                    continue;
                }

                if (_log is not null)
                {
                    _log.Line($"  {target.Label} is no longer {Cond.Name(active.Condition)} ({why})");
                }

                RemoveAt(target, i);
            }
        }
    }

    /// <summary>
    /// A saving throw against an effect, with the target's Legendary Resistance per policy: "conditions" spends a use when
    /// the failure would impose a condition that matters or its damage would drop the creature to 0 HP.
    /// </summary>
    private bool SaveAgainst(Creature target, string ability, int dc, bool magical, bool imposesCondition, int[]? failDamage, bool halveOnFail, string what)
    {
        var success = SavingThrow(target, ability, dc, magical, out var text);
        if (_log is not null)
        {
            _log.Line($"    {target.Label} vs {what}: {text} → {(success ? "success" : "failure")}");
        }

        if (!success && target.LegendaryResistanceLeft > 0)
        {
            var spend = _setup.LegendaryResistance switch
            {
                SimulationValues.LegendaryResistance.Always => true,
                SimulationValues.LegendaryResistance.Never => false,
                _ => imposesCondition || (failDamage is not null && WouldDrop(target, failDamage, magical, halveOnFail)),
            };
            if (spend)
            {
                target.LegendaryResistanceLeft--;
                target.LegendaryResistanceSpent++;
                success = true;
                if (_log is not null)
                {
                    _log.Line($"    {target.Label} uses Legendary Resistance ({target.LegendaryResistanceLeft} left): success");
                }
            }
        }

        return success;
    }

    /// <summary>Evasion (a trait): Dex saves for half take none on a success and half on a failure; 2024 not while incapacitated.</summary>
    private static bool Evades(Creature target, string ability, string onSuccess) =>
        target.T.Evasion && ability == V.Abilities.Dex && onSuccess == V.OnSuccess.Half &&
        (target.T.Edition == V.Editions.E2014 || !target.Incapacitated);

    /// <summary>
    /// One target's share of a save effect whose damage was already rolled (shared by every target of an area): the save,
    /// Legendary Resistance, half or none on a success (Evasion: none, and half on a failure), and the condition on a failure.
    /// <paramref name="immunity"/> is the effect's <see cref="MonsterAction.ImmunityIndex"/> (−1: none): a target immune to
    /// it is not affected at all, and a success (Legendary Resistance's too) makes it immune. <paramref name="lethal"/>: a
    /// failure kills it outright instead of dealing the damage or imposing the conditions — the 2024 solar's Slaying Bow on
    /// a creature with 100 Hit Points or fewer: "it dies. It otherwise takes …" — which Legendary Resistance's "conditions"
    /// policy treats like a condition that matters. Damaging first would count the damage as dealt and taken and could
    /// trip Relentless, Undead Fortitude or massive damage before a death that has none. (The 2014 Slaying Longbow's rider
    /// carries no damage of its own; the hit's damage lands before it, and its threshold is read after that damage.)
    /// </summary>
    private void ResolveSave(Creature source, Creature target, string ability, int dc, string onSuccess, bool magical, int[]? rolled,
                             ConditionTemplate? condition, int token, string what, ConditionTemplate[]? extra = null, int immunity = -1, bool lethal = false)
    {
        if (target.Dead || ShrugsOff(source, target, immunity, what))
        {
            return;
        }

        var evasion = Evades(target, ability, onSuccess);
        var imposes = lethal || Imposes(target, condition);
        foreach (var more in extra ?? [])
        {
            imposes |= Imposes(target, more);
        }

        var success = SaveAgainst(target, ability, dc, magical, imposes, rolled, evasion, what);
        if (success)
        {
            target.BecomeImmuneToEffect(source.Id, immunity);
        }
        else if (lethal)
        {
            Slay(source, target, what);
            return;
        }

        if (rolled is not null)
        {
            if (!success)
            {
                ApplyDamage(source, target, rolled, magical, false, false, false, halve: evasion);
            }
            else if (onSuccess == V.OnSuccess.Half && !evasion)
            {
                ApplyDamage(source, target, rolled, magical, false, false, false, halve: true);
            }
        }

        if (!success && !target.Dead)
        {
            if (condition is not null)
            {
                AddCondition(source, target, condition, token, immunity: immunity);
            }

            foreach (var more in extra ?? [])
            {
                AddCondition(source, target, more, token, immunity: immunity);
            }
        }
    }

    /// <summary>Whether <paramref name="target"/> is immune to this effect of <paramref name="source"/> (it succeeded against it, or shook it off, before).</summary>
    private bool ShrugsOff(Creature source, Creature target, int immunity, string what)
    {
        if (!target.IsImmuneToEffect(source.Id, immunity))
        {
            return false;
        }

        if (_log is not null)
        {
            _log.Line($"    {target.Label} is immune to {source.Label}'s {what} (it succeeded against it or shook it off before)");
        }

        return true;
    }

    /// <summary>
    /// An outright kill ("it dies": Power Word Kill at 100 hit points or fewer, a failed save against the solar's Slaying
    /// Longbow or Slaying Bow): no damage, so no death saves, no massive-damage test, no Relentless, Undead Fortitude or regeneration from
    /// 0; it counts as a death, and as a kill for the source.
    /// </summary>
    private void Slay(Creature source, Creature target, string what)
    {
        if (target.Dead)
        {
            return;
        }

        if (_log is not null)
        {
            _log.Line($"    {what}: {target.Label} ({target.Hp} HP) dies outright");
        }

        Die(target, source);
    }

    /// <summary>
    /// Whether an action's outright kill (<see cref="StatBlockAction.KillAtOrBelowHp"/>) takes <paramref name="target"/>:
    /// "If the target has 100 Hit Points or fewer, it dies" reads its hit points, not its temporary ones; a creature at 0
    /// (dying) has fewer. The harness's infinite-HP dummies are never killed.
    /// </summary>
    private static bool KilledOutright(int? killAtOrBelowHp, Creature target) =>
        killAtOrBelowHp is { } threshold && target.Hp <= threshold && !target.Dead && !target.T.InfiniteHp;

    /// <summary>Whether this condition would change something on the target (Legendary Resistance's "conditions" test).</summary>
    private static bool Imposes(Creature target, ConditionTemplate? condition) =>
        condition is not null && Cond.IsMechanical(condition.Condition) && !target.T.ImmuneTo(condition.Condition) && !target.Has(condition.Condition);

    // ------------------------------------------------------------------------------------------------------------------
    // Traits that deal damage.
    // ------------------------------------------------------------------------------------------------------------------

    private void RollParts(RollPart[] parts, int[] into, bool crit)
    {
        Array.Clear(into);
        foreach (var part in parts)
        {
            into[part.Type] += RollTerms(part.Dice, crit ? 2 : 1, null, false) + part.Flat;
        }
    }

    /// <summary>
    /// A retaliation trait (a fire elemental's body, Heated Body: "a creature that touches it or hits it with a melee attack
    /// takes 10 fire damage") after a hit on its owner: ONE rule for both sides' hits, so a party member's blade is burned as
    /// a monster's claw is. It comes after the hit's own effects (damage, riders, conditions) and before anything the
    /// attacker does next. <paramref name="melee"/> is how the attack was MADE: a weapon thrown from the back line, or a
    /// build's attack with the ranged property, is not a melee attack.
    /// </summary>
    private void Retaliate(Creature attacker, Creature target, bool melee)
    {
        if (melee && target.T.Retaliation is { } retaliation && !attacker.Dead)
        {
            TraitDamage(target, attacker, retaliation);
        }
    }

    /// <summary>A trait's damage (retaliation, aura) on one creature, with its save when it has one.</summary>
    private void TraitDamage(Creature source, Creature target, TraitEffect effect)
    {
        if (target.Dead)
        {
            return;
        }

        if (_log is not null)
        {
            _log.Line($"  {source.Label}'s {effect.Name} on {target.Label}");
        }

        var rolled = new int[DamageTypes.Count];
        RollParts(effect.Damage, rolled, false);
        if (effect.Save is { } save)
        {
            ResolveSave(source, target, save.Ability, save.Dc, save.OnSuccess, false, effect.Damage.Length > 0 ? rolled : null, effect.Condition, 0, effect.Name);
        }
        else
        {
            if (effect.Damage.Length > 0)
            {
                ApplyDamage(source, target, rolled, false, false, false, false, false);
            }

            if (effect.Condition is not null && !target.Dead)
            {
                AddCondition(source, target, effect.Condition, 0);
            }
        }
    }

    /// <summary>
    /// An aura that works on its owner's turn ("at the start of each of the balor's turns, each creature within 5 feet"):
    /// each standing enemy engaged with it — the other side's front line.
    /// </summary>
    private void AuraDamage(Creature c, TraitEffect aura)
    {
        foreach (var other in _c)
        {
            if (other.Side != c.Side && other.Up && other.T.Front)
            {
                TraitDamage(c, other, aura);
            }
        }
    }

    /// <summary>
    /// Auras that work on the creature entering their turn ("any creature that starts its turn within 5 feet": Stench,
    /// Fear Aura, a fire elemental's body): a front-liner starting its turn next to a standing enemy that has one.
    /// </summary>
    private void AurasOn(Creature c)
    {
        if (!c.T.Front)
        {
            return;
        }

        foreach (var owner in _c)
        {
            if (owner.Side != c.Side && owner.Up && owner.T.Aura is { StartOfTargetTurn: true } aura && !c.Dead)
            {
                TraitDamage(owner, c, aura);
            }
        }
    }

    /// <summary>A death burst: one shared roll on the enemies its area reaches (the DMG count; no friendly fire).</summary>
    private void DeathBurst(Creature c, TraitEffect burst)
    {
        var targets = new List<Creature>();
        PickArea(c, Math.Max(1, burst.AreaCount), targets);
        if (targets.Count == 0)
        {
            return;
        }

        if (_log is not null)
        {
            _log.Line($"  {c.Label}'s {burst.Name}");
        }

        var rolled = new int[DamageTypes.Count];
        RollParts(burst.Damage, rolled, false);
        foreach (var target in targets)
        {
            if (burst.Save is { } save)
            {
                ResolveSave(c, target, save.Ability, save.Dc, save.OnSuccess, false, burst.Damage.Length > 0 ? rolled : null, burst.Condition, 0, burst.Name);
            }
            else
            {
                ApplyDamage(c, target, rolled, false, false, false, false, false);
            }
        }
    }

    /// <summary>Ongoing damage from conditions ("burning", swallowed) at the start of the creature's turn.</summary>
    private void OngoingDamage(Creature c)
    {
        for (var i = 0; i < c.Conditions.Count && !c.Dead; i++)
        {
            var active = c.Conditions[i];
            if (active.Template.Ongoing.Length == 0)
            {
                continue;
            }

            if (_log is not null)
            {
                _log.Line($"  ongoing damage ({Cond.Name(active.Condition)})");
            }

            var rolled = new int[DamageTypes.Count];
            RollParts(active.Template.Ongoing, rolled, false);
            ApplyDamage(active.Source >= 0 ? _c[active.Source] : null, c, rolled, false, false, false, false, false);
        }
    }
}
