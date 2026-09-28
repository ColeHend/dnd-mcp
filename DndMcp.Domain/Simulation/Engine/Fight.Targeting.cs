namespace DndMcp.Domain.Simulation;

/// <summary>
/// Who can be attacked and who is (contract §5.4 "Engagement", "AoE", "Targeting policies").
///
/// <para>
/// <b>No grid, two lines.</b> Each side has a front line (its melee combatants, or whoever the spec puts there) and a back
/// line. A melee attack reaches a standing enemy front-liner; only when the enemy side has none standing can it reach
/// anyone, and a flying attacker always can. Ranged attacks, spells and save effects reach anyone. An area takes the
/// DMG's count of creatures, the front line first and then the back line, in a random order within each line (seeded),
/// never an ally. A creature at 0 HP is not a target unless the enemies' <c>finish_downed</c> policy says so; a troll at
/// 0 HP is attacked only when nothing else is left.
/// </para>
/// </summary>
internal sealed partial class Fight
{
    private readonly List<Creature> _candidates = [];
    private readonly List<Creature> _picked = [];
    private readonly List<Creature> _front = [];
    private readonly List<Creature> _back = [];

    /// <summary>Whether <paramref name="attacker"/> may attack <paramref name="target"/> at all (reach aside).</summary>
    private bool Attackable(Creature attacker, Creature target)
    {
        if (target.Dead || target.Side == attacker.Side)
        {
            return false;
        }

        if (!target.Up && !(target.Down && target.T.PcLike && _setup.FinishDowned && attacker.Side == 1))
        {
            return false;
        }

        return !CharmedBy(attacker, target);
    }

    /// <summary>Charmed: it cannot attack the charmer (no line of sight is modelled).</summary>
    private static bool CharmedBy(Creature attacker, Creature target)
    {
        if (attacker.CondCount[Cond.Charmed] == 0)
        {
            return false;
        }

        foreach (var active in attacker.Conditions)
        {
            if (active.Condition == Cond.Charmed && active.Source == target.Id)
            {
                return true;
            }
        }

        return false;
    }

    private bool StandingFrontLine(int side)
    {
        foreach (var c in _c)
        {
            if (c.Side == side && c.Up && c.T.Front)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A melee attack reaches the front line, or anyone when the front line is down, or anyone for a flier.</summary>
    internal bool Reachable(Creature attacker, Creature target, bool melee) =>
        !melee || attacker.T.Flies || target.T.Front || !StandingFrontLine(target.Side);

    private void FillCandidates(Creature attacker, bool melee)
    {
        _candidates.Clear();
        var frontStanding = false;
        foreach (var c in _c)
        {
            if (Attackable(attacker, c))
            {
                _candidates.Add(c);
                frontStanding |= c.Up && c.T.Front;
            }
        }

        if (melee && !attacker.T.Flies && frontStanding)
        {
            _candidates.RemoveAll(c => !c.T.Front);
        }

        if (_candidates.Count == 0)
        {
            // A creature that will get up on its own (a troll at 0 HP) is the last thing left to hit.
            foreach (var c in _c)
            {
                if (c.Side != attacker.Side && c.Down && !c.Dead && !c.T.PcLike && c.T.RegeneratesFromZero)
                {
                    _candidates.Add(c);
                }
            }
        }
    }

    /// <summary>One target for an attack or a single-target effect, by the side's policy.</summary>
    internal Creature? PickTarget(Creature attacker, bool melee)
    {
        if (_setup.Dummy)
        {
            return attacker.Side == 0 ? _c[1] : _c[0];
        }

        FillCandidates(attacker, melee);
        return _candidates.Count == 0 ? null : Choose(_candidates, _setup.Targeting(attacker.Side));
    }

    private Creature Choose(List<Creature> candidates, string policy)
    {
        switch (policy)
        {
            case SimulationValues.Targeting.FocusFire:
            {
                var best = candidates[0];
                for (var i = 1; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    if (c.Hp < best.Hp || (c.Hp == best.Hp && c.MaxHp < best.MaxHp))
                    {
                        best = c;
                    }
                }

                return best;
            }

            case SimulationValues.Targeting.Threat:
            {
                var best = candidates[0];
                for (var i = 1; i < candidates.Count; i++)
                {
                    if (candidates[i].T.Threat > best.T.Threat)
                    {
                        best = candidates[i];
                    }
                }

                return best;
            }

            case SimulationValues.Targeting.HealerFirst:
                return Preferred(candidates, c => c.T.HasHeal);
            case SimulationValues.Targeting.BreakConcentration:
                return Preferred(candidates, c => c.ConcentrationToken != 0);
            default:
                return candidates[(int)_rng.NextBounded((uint)candidates.Count)];
        }
    }

    private Creature Preferred(List<Creature> candidates, Func<Creature, bool> first)
    {
        var count = 0;
        foreach (var c in candidates)
        {
            if (first(c))
            {
                count++;
            }
        }

        if (count == 0)
        {
            return candidates[(int)_rng.NextBounded((uint)candidates.Count)];
        }

        var pick = (int)_rng.NextBounded((uint)count);
        foreach (var c in candidates)
        {
            if (first(c) && pick-- == 0)
            {
                return c;
            }
        }

        return candidates[0];
    }

    /// <summary>Up to <paramref name="count"/> different targets by the side's policy (a save against "up to three creatures").</summary>
    private void PickDistinct(Creature attacker, int count, List<Creature> into)
    {
        into.Clear();
        if (_setup.Dummy)
        {
            for (var i = 0; i < Math.Min(count, _setup.MainDummies); i++)
            {
                into.Add(_c[1 + i]);
            }

            return;
        }

        FillCandidates(attacker, false);
        while (into.Count < count && _candidates.Count > 0)
        {
            var pick = Choose(_candidates, _setup.Targeting(attacker.Side));
            into.Add(pick);
            _candidates.Remove(pick);
        }
    }

    /// <summary>The creatures an area catches: the DMG count, capped by the standing enemies, front line first.</summary>
    private void PickArea(Creature attacker, int count, List<Creature> into)
    {
        into.Clear();
        if (_setup.Dummy)
        {
            for (var i = 0; i < Math.Min(count, _setup.MainDummies); i++)
            {
                into.Add(_c[1 + i]);
            }

            return;
        }

        _front.Clear();
        _back.Clear();
        foreach (var c in _c)
        {
            if (c.Side != attacker.Side && c.Up)
            {
                (c.T.Front ? _front : _back).Add(c);
            }
        }

        Shuffle(_front);
        Shuffle(_back);
        foreach (var c in _front)
        {
            if (into.Count < count)
            {
                into.Add(c);
            }
        }

        foreach (var c in _back)
        {
            if (into.Count < count)
            {
                into.Add(c);
            }
        }
    }

    private void Shuffle(List<Creature> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = (int)_rng.NextBounded((uint)(i + 1));
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Pack Tactics, Sneak Attack and Martial Advantage's "an ally within 5 feet of the target": another standing,
    /// able ally of the attacker is on the front line and so is the target.
    /// </summary>
    private bool AllyEngaged(Creature attacker, Creature target)
    {
        if (!target.T.Front)
        {
            return false;
        }

        foreach (var c in _c)
        {
            if (c != attacker && c.Side == attacker.Side && c.Up && !c.Incapacitated && c.T.Front)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A damage type's multiplier on this creature, for ranking choices only (0, ½, 1 or 2).</summary>
    private static double Factor(Creature target, int type, bool magical, bool silvered = false, bool adamantine = false)
    {
        if (type == DamageTypes.Typeless)
        {
            return target.Has(Cond.Petrified) ? 0.5 : 1;
        }

        var t = target.T;
        if (Qualifier.Applies(t.Immune[type], magical, silvered, adamantine))
        {
            return 0;
        }

        var f = Qualifier.Applies(t.Resist[type], magical, silvered, adamantine) || target.Has(Cond.Petrified) ? 0.5 : 1;
        return Qualifier.Applies(t.Vulnerable[type], magical, silvered, adamantine) ? f * 2 : f;
    }

    /// <summary>
    /// What imposing a condition on this creature is worth in the policies' currency (expected damage): a condition that
    /// takes its turns away (paralyzed, stunned, petrified, unconscious, incapacitated) is worth its whole expected
    /// damage per round (<see cref="CombatantTemplate.Threat"/>); one that hampers it (restrained, blinded, frightened,
    /// poisoned, prone, grappled, charmed) a quarter of it; exhaustion a tenth. Nothing when it already has it or is immune.
    /// A heuristic for ranking only: it lets a caster pick Hold Person over a cantrip when the target is worth holding.
    /// </summary>
    private static double ConditionValue(Creature target, int condition)
    {
        if (condition < 0 || target.Has(condition) || target.T.ImmuneTo(condition))
        {
            return 0;
        }

        var threat = Math.Max(1, target.T.Threat);
        return condition switch
        {
            Cond.Paralyzed or Cond.Stunned or Cond.Petrified or Cond.Unconscious or Cond.Incapacitated => threat,
            Cond.Exhaustion => threat / 10,
            Cond.Deafened => 0,
            _ => threat / 4,
        };
    }

    private static readonly string[] SizeOrder = ["tiny", "small", "medium", "large", "huge", "gargantuan"];

    /// <summary>A rider limited to "Large or smaller": compares the first size word of the target's size.</summary>
    private static bool FitsSize(Creature target, string? maxSize)
    {
        if (maxSize is null)
        {
            return true;
        }

        var size = Array.IndexOf(SizeOrder, target.T.Size.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "medium");
        var max = Array.IndexOf(SizeOrder, maxSize.Trim().ToLowerInvariant());
        return size < 0 || max < 0 || size <= max;
    }
}
