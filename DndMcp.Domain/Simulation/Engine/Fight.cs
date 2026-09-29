using System.Globalization;
using DndMcp.Domain.Probability;
using DndMcp.Domain.Rng;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// One fight at a time, reused: the rules engine of the simulator (contract §5.4). A thread owns one instance and runs
/// fight after fight on it; <see cref="Run"/> resets every creature and draws everything from the fight's own
/// <see cref="Xoshiro256StarStar"/>, so a fight's result is a pure function of its seed — which is what makes the report
/// identical at any thread count and lets <c>replay</c> reproduce any fight exactly.
///
/// <para>
/// <b>The round</b> (both editions): each creature in initiative order takes its turn — start of turn (conditions that end
/// now, recharge, Legendary Action reset, regeneration, ongoing damage, the death save, standing up), its Action and Bonus
/// Action, end of turn (save-ends saves, durations) — and after it, pending reaction attacks against it, then one
/// legendary action by each other legendary creature that can take one. A dead creature takes no turn, but at its place
/// the durations counted on its turns still run out (what it imposed until the start or end of its next turn, and round
/// counts): a dead lich's Paralyzing Touch ends there. The fight ends the moment a side has no creature standing (a party
/// member at 0 HP counts as down even while dying), or at the round cap as a draw.
/// </para>
/// <para>
/// The partial files hold the rest: <c>Fight.Rolls</c> (dice, d20s, saves, advantage), <c>Fight.Damage</c> (damage,
/// death, healing, concentration, conditions), <c>Fight.Targeting</c>, <c>Fight.Pc</c> (a DSL build's turn) and
/// <c>Fight.Monster</c> (a stat block's turn).
/// </para>
/// </summary>
internal sealed partial class Fight
{
    private readonly FightSetup _setup;
    private readonly Creature[] _c;
    private readonly int[] _order;
    private readonly int[] _entryOrder;
    private readonly int[] _entryRoll;
    private readonly ulong[] _entryTiebreak;
    private Xoshiro256StarStar _rng;
    private CombatLog? _log;
    private long _turnId;
    private int _round;
    private int _active = -1;
    private int _tokens;
    private bool _over;
    private string _outcome = SimulationValues.Outcomes.Draw;

    public Fight(FightSetup setup)
    {
        _setup = setup;
        _c = setup.Templates.Select(t => new Creature(t)).ToArray();
        _order = new int[_c.Length];
        _entryOrder = new int[setup.Entries.Length];
        _entryRoll = new int[setup.Entries.Length];
        _entryTiebreak = new ulong[setup.Entries.Length];
        _odds = new Dictionary<long, (double Hit, double Crit)>();
    }

    public Creature[] Creatures => _c;

    /// <summary>The harness: each round's cumulative damage dealt by creature 0, filled when set (length = round cap).</summary>
    public long[]? RoundDealt { get; set; }

    /// <summary>Runs one whole fight from <paramref name="seed"/>; the creatures keep its end state for the caller's statistics.</summary>
    public FightOutcome Run(ulong seed, CombatLog? log = null)
    {
        Begin(seed, log);
        var rounds = _setup.RoundCap;
        for (var round = 1; round <= _setup.RoundCap && !_over; round++)
        {
            _round = round;
            if (_log is not null)
            {
                _log.Line($"Round {round}");
            }

            foreach (var id in _order)
            {
                if (_over)
                {
                    break;
                }

                var creature = _c[id];
                if (creature.Dead)
                {
                    // No turn, and no legendary actions after it: only the durations counted on its turns run out.
                    DeadCreaturesPlace(creature);
                    continue;
                }

                TakeTurn(creature);
                if (!_over)
                {
                    AfterTurn(creature);
                }
            }

            if (RoundDealt is { } dealt)
            {
                dealt[round - 1] = _c[0].DealtRaw;
            }

            if (_over)
            {
                rounds = round;
            }
        }

        if (_log is not null && !_over)
        {
            _log.Line($"The round cap ({_setup.RoundCap}) is reached: a draw.");
        }

        return new FightOutcome(_outcome, rounds);
    }

    /// <summary>
    /// Sets up a fight without running it: every creature reset (hit points rolled or averaged), start-of-fight effects,
    /// initiative. <see cref="Run"/> starts here; the rules tests start here too and then script turns and damage.
    /// </summary>
    internal void Begin(ulong seed, CombatLog? log = null)
    {
        _rng = new Xoshiro256StarStar(seed);
        _log = log;
        _turnId = 0;
        _round = 0;
        _active = -1;
        _tokens = 0;
        _over = false;
        _outcome = SimulationValues.Outcomes.Draw;

        foreach (var creature in _c)
        {
            var t = creature.T;
            var hp = t.RolledHp is { } dice ? Math.Max(1, RollFormula(dice)) : t.AverageHp;
            creature.Reset(hp);
        }

        foreach (var creature in _c)
        {
            StartOfFight(creature);
        }

        RollInitiative();
    }

    /// <summary>The initiative order (creature ids) of the current fight.</summary>
    internal IReadOnlyList<int> Order => _order;

    internal bool Over => _over;

    internal string Outcome => _outcome;

    private void StartOfFight(Creature c)
    {
        GainTempHp(c, c.T.TempHpAtStart);
        if (c.Pc is { } pc)
        {
            var build = pc.Build;
            for (var n = 0; n < pc.Active.Length; n++)
            {
                pc.Active[n] = !build.Gated[n];
            }

            // A concentration modifier with no setup cost (the warlock baseline's Hex) is already up when the fight starts, so
            // it is already held: damage can break it from round 1, and then it is off for the rest of the fight (it has no
            // setup to pay again). One cast as a save effect is concentrated on when cast.
            if (build.ConcentrationNumber > 0 && !build.Gated[build.ConcentrationNumber] && !IsSaveEffect(build, build.ConcentrationNumber))
            {
                StartConcentration(c, build.ConcentrationLabel!, build.ConcentrationNumber, deactivates: true);
            }
        }
    }

    private static bool IsSaveEffect(PcBuild build, int number) => build.SaveEffects.Any(s => s.Source.Number == number);

    // ------------------------------------------------------------------------------------------------------------------
    // Initiative.
    // ------------------------------------------------------------------------------------------------------------------

    private void RollInitiative()
    {
        var entries = _setup.Entries;
        if (_setup.Dummy)
        {
            // The harness: the build first, then the dummies, every round (the closed form has no initiative).
            for (var i = 0; i < _c.Length; i++)
            {
                _order[i] = i;
            }

            return;
        }

        for (var e = 0; e < entries.Length; e++)
        {
            var first = _c[entries[e][0]];
            var surprised = Surprised(first.Side);
            var mode = surprised && _setup.Is2024 ? D20Mode.Disadvantage : D20Mode.Normal;
            var face = RollD20(mode, false, false, out var faces);
            _entryRoll[e] = face + first.T.InitiativeBonus;
            _entryTiebreak[e] = _rng.NextUInt64();
            _entryOrder[e] = e;
            foreach (var id in entries[e])
            {
                _c[id].Initiative = _entryRoll[e];
                _c[id].Surprised = surprised && !_setup.Is2024;
                if (_c[id].Surprised)
                {
                    _c[id].ReactionAvailable = false;
                }
            }

            if (_log is not null)
            {
                var names = string.Join(", ", entries[e].Select(id => _c[id].Label));
                _log.Line($"Initiative: {names} {_entryRoll[e]} (d20 {faces}{Signed(first.T.InitiativeBonus)}){(surprised ? " — surprised" : "")}");
            }
        }

        Array.Sort(_entryOrder, CompareEntries);
        var k = 0;
        foreach (var e in _entryOrder)
        {
            foreach (var id in entries[e])
            {
                _order[k++] = id;
            }
        }

        if (_log is not null)
        {
            _log.Line("Order: " + string.Join(", ", _order.Select(id => _c[id].Label)));
        }
    }

    private bool Surprised(int side) =>
        (_setup.Surprise == SimulationValues.Surprise.Party && side == 0) || (_setup.Surprise == SimulationValues.Surprise.Enemies && side == 1);

    /// <summary>Higher total first; then the higher modifier; then (optionally) the party; then the seeded roll-off.</summary>
    private int CompareEntries(int a, int b)
    {
        if (_entryRoll[a] != _entryRoll[b])
        {
            return _entryRoll[b].CompareTo(_entryRoll[a]);
        }

        var ta = _c[_setup.Entries[a][0]].T;
        var tb = _c[_setup.Entries[b][0]].T;
        if (ta.InitiativeBonus != tb.InitiativeBonus)
        {
            return tb.InitiativeBonus.CompareTo(ta.InitiativeBonus);
        }

        if (_setup.PcsWinTies && ta.Side != tb.Side)
        {
            return ta.Side.CompareTo(tb.Side);
        }

        return _entryTiebreak[a] != _entryTiebreak[b] ? _entryTiebreak[b].CompareTo(_entryTiebreak[a]) : a.CompareTo(b);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Turns.
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// One creature's turn: start, Action and Bonus Action, end. A dead creature takes none; a scripted turn of one is its
    /// place in the order coming round (<see cref="DeadCreaturesPlace"/>), as it is in <see cref="Run"/>. A creature that
    /// dies during its turn still ends that turn for what it imposed (<see cref="SourceTurnEnds"/>), so "until the end of
    /// the source's next turn" imposed during it ends at the creature's next place in the order, not a round later.
    /// </summary>
    internal void TakeTurn(Creature c)
    {
        if (c.Dead)
        {
            DeadCreaturesPlace(c);
            return;
        }

        _turnId++;
        _active = c.Id;
        c.TurnsTaken++;
        if (_log is not null)
        {
            _log.Line($"{c.Label}'s turn ({Status(c)})");
        }

        StartOfTurn(c);
        if (!c.Dead && CanAct(c))
        {
            if (c.T.Pc is not null)
            {
                PcTurn(c);
            }
            else
            {
                MonsterTurn(c);
            }
        }
        else if (_log is not null && !c.Dead && c.Up && !c.T.Inert)
        {
            _log.Line(c.Surprised ? "  surprised: no action this turn" : "  incapacitated: no action");
        }

        if (!c.Dead)
        {
            EndOfTurn(c);
        }
        else
        {
            SourceTurnEnds(c);
        }

        _active = -1;
        CheckOver();
    }

    /// <summary>
    /// A dead creature's place in the initiative order. It takes no turn (no recharge, regeneration, death save or
    /// Legendary Action reset, no action, no legendary actions after it), but the durations counted on its turns still run
    /// out there: what it imposed "until the start of its next turn" ends where that turn would start, and "until the end
    /// of its next turn" and round counts end or tick where it would end. Without it the 2024 lich's Paralyzing Touch ("until the start of the lich's next turn") would hold its
    /// target for the rest of the fight once the lich died, open to Advantage and automatic critical hits from the lich's
    /// allies. No die is rolled and nothing is logged unless a condition ends, so a fight in which nothing a dead creature
    /// imposed is still running draws and logs exactly what it did when the dead were skipped.
    /// </summary>
    private void DeadCreaturesPlace(Creature c)
    {
        SourceTurnStarts(c, "its source is dead; its turn would start");
        SourceTurnEnds(c);
    }

    private static bool CanAct(Creature c) => c.Up && !c.Incapacitated && !c.Surprised && !c.T.Inert;

    internal void StartOfTurn(Creature c)
    {
        SourceTurnStarts(c, "its source's turn starts");

        // "Until the start of its next turn" on the creature itself ends now (before an aura imposes it afresh), and so
        // do a Shield it cast and the Dodge.
        RemoveMatching(c, DurationKind.UntilStartOfTargetTurn, null, -1, "its turn starts");
        c.ShieldAc = 0;
        c.Dodging = false;
        c.RecklessActive = false;
        if (!c.Surprised)
        {
            c.ReactionAvailable = true;
        }

        c.LegendaryUsesLeft = c.T.LegendaryUses;
        Array.Clear(c.LegendaryUsedThisRound);

        for (var i = 0; i < c.T.Limited.Length; i++)
        {
            var usage = c.T.Limited[i].Source.Usage;
            if (usage.Kind == StatBlockValues.UsageKinds.Recharge && !c.RechargeReady[i])
            {
                var roll = D(6);
                c.RechargeReady[i] = roll >= (usage.RechargeMin ?? 6);
                if (_log is not null)
                {
                    _log.Line($"  recharge {c.T.LimitedNames[i]}: d6 {roll} → {(c.RechargeReady[i] ? "ready" : "not yet")}");
                }
            }
        }

        Regenerate(c);
        if (c.Dead)
        {
            return;
        }

        if (c.Up && c.T.Aura is { StartOfTargetTurn: false } aura)
        {
            AuraDamage(c, aura);
        }

        AurasOn(c);
        if (c.Dead)
        {
            return;
        }

        OngoingDamage(c);
        if (c.Dead)
        {
            return;
        }

        if (c.Down && c.T.PcLike && !c.Stable)
        {
            DeathSave(c);
            if (c.Dead)
            {
                return;
            }
        }

        if (c.Up && !c.Incapacitated && CanStand(c))
        {
            RemoveMatching(c, DurationKind.UntilStands, null, Cond.Prone, "stands up");
        }

        if (c.Pc is { } pc)
        {
            pc.ReactionPending = false;
        }
    }

    /// <summary>
    /// The start of <paramref name="c"/>'s turn for what it imposed on others: "until the start of the source's next turn"
    /// ends now, and so does the Disadvantage of its Sap. It runs at a dead creature's place too (<see cref="DeadCreaturesPlace"/>).
    /// </summary>
    private void SourceTurnStarts(Creature c, string why)
    {
        foreach (var other in _c)
        {
            RemoveMatching(other, DurationKind.UntilStartOfSourceTurn, c.Id, -1, why);
            if (other.SappedBy == c.Id)
            {
                other.SappedBy = -1;
            }
        }
    }

    private static bool CanStand(Creature c) => c.T.CanStand && !c.Has(Cond.Grappled) && !c.Has(Cond.Restrained);

    private void Regenerate(Creature c)
    {
        var amount = c.T.RegenerationAmount;
        if (amount <= 0)
        {
            return;
        }

        var blocked = c.RegenerationBlocked;
        c.RegenerationBlocked = false;
        if (c.Down && c.T.RegeneratesFromZero && !c.T.PcLike)
        {
            if (blocked)
            {
                if (_log is not null)
                {
                    _log.Line($"  {c.Label} starts its turn at 0 HP and does not regenerate: it dies");
                }

                Die(c, null);
                return;
            }

            Heal(c, c, amount, "Regeneration");
            return;
        }

        if (c.Up && !blocked && c.Hp < c.MaxHp)
        {
            Heal(c, c, amount, "Regeneration");
        }
        else if (c.Up && blocked && _log is not null)
        {
            _log.Line($"  {c.Label}'s Regeneration does not work this turn");
        }
    }

    private void DeathSave(Creature c)
    {
        var face = D(20);
        var (successes, failures, state) = DeathSaves.Step(c.DeathSuccesses, c.DeathFailures, face);
        c.DeathSuccesses = successes;
        c.DeathFailures = failures;
        if (_log is not null)
        {
            _log.Line($"  death save: d20 {face} → {state switch
            {
                DeathSaveState.Revived => "natural 20, regains 1 HP",
                DeathSaveState.Stable => "stable",
                DeathSaveState.Dead => "dies",
                _ => $"{successes} successes, {failures} failures",
            }}");
        }

        switch (state)
        {
            case DeathSaveState.Revived:
                Heal(c, c, 1, "natural 20");
                break;
            case DeathSaveState.Stable:
                c.Stable = true;
                c.DeathSuccesses = 0;
                c.DeathFailures = 0;
                break;
            case DeathSaveState.Dead:
                Die(c, null);
                break;
        }
    }

    internal void EndOfTurn(Creature c)
    {
        // "Until the end of its next turn" on the creature itself: ends now, unless imposed during this very turn.
        for (var i = c.Conditions.Count - 1; i >= 0; i--)
        {
            var own = c.Conditions[i];
            if (own.Duration != DurationKind.UntilEndOfTargetTurn)
            {
                continue;
            }

            if (own.SkipTurnEnds > 0)
            {
                own.SkipTurnEnds--;
                c.Conditions[i] = own;
            }
            else
            {
                if (_log is not null)
                {
                    _log.Line($"  {c.Label} is no longer {Cond.Name(own.Condition)} (its turn ends)");
                }

                RemoveAt(c, i);
            }
        }

        // Save-ends conditions on this creature: one save each, a success ends it.
        for (var i = 0; i < c.Conditions.Count; i++)
        {
            var active = c.Conditions[i];
            if (active.Duration != DurationKind.SaveEnds || active.Template.SaveAbility is null)
            {
                continue;
            }

            var success = SavingThrow(c, active.Template.SaveAbility, active.Template.SaveDc, active.Template.SaveMagical, out var text);
            if (_log is not null)
            {
                _log.Line($"  {c.Label} repeats the save against {Cond.Name(active.Condition)}: {text} → {(success ? "ends it" : "still " + Cond.Name(active.Condition))}");
            }

            if (success)
            {
                RemoveAt(c, i);
                i--;
            }
        }

        SourceTurnEnds(c);

        if (c.VexTarget >= 0 && c.VexTurn < _turnId)
        {
            c.VexTarget = -1;
        }

        if (c.Surprised)
        {
            c.Surprised = false;
            c.ReactionAvailable = true;
        }
    }

    /// <summary>
    /// The end of <paramref name="c"/>'s turn for what it imposed on others: "until the end of the source's next turn"
    /// ends (unless imposed during this very turn), and round counts (a Rounds duration, a save-ends cap) tick down. It
    /// runs at the end of a turn in which it died, and at a dead creature's place (<see cref="DeadCreaturesPlace"/>).
    /// </summary>
    private void SourceTurnEnds(Creature c)
    {
        foreach (var other in _c)
        {
            for (var i = 0; i < other.Conditions.Count; i++)
            {
                var active = other.Conditions[i];
                if (active.Source != c.Id)
                {
                    continue;
                }

                var remove = false;
                if (active.Duration == DurationKind.UntilEndOfSourceTurn)
                {
                    if (active.SkipTurnEnds > 0)
                    {
                        active.SkipTurnEnds--;
                        other.Conditions[i] = active;
                    }
                    else
                    {
                        remove = true;
                    }
                }
                else if (active.RoundsLeft > 0 && active.Duration is DurationKind.Rounds or DurationKind.SaveEnds)
                {
                    active.RoundsLeft--;
                    other.Conditions[i] = active;
                    remove = active.RoundsLeft == 0;
                }

                if (remove)
                {
                    if (_log is not null)
                    {
                        _log.Line($"  {other.Label} is no longer {Cond.Name(active.Condition)} (its duration ends)");
                    }

                    RemoveAt(other, i);
                    i--;
                }
            }
        }
    }

    internal void AfterTurn(Creature c)
    {
        // Pending reaction attacks against the creature whose turn just ended.
        foreach (var other in _c)
        {
            if (_over)
            {
                return;
            }

            if (other.Pc is { ReactionPending: true } && other.Side != c.Side && !c.Dead && other.Up && !other.Incapacitated && other.ReactionAvailable && !other.Surprised)
            {
                ReactionAttack(other, c);
            }
        }

        // Legendary actions: each other legendary creature may take one, in initiative order.
        foreach (var id in _order)
        {
            if (_over)
            {
                return;
            }

            var legendary = _c[id];
            if (legendary != c && legendary.T.LegendaryUses > 0 && legendary.LegendaryUsesLeft > 0 && legendary.Up && !legendary.Incapacitated && !legendary.Surprised)
            {
                LegendaryAction(legendary);
                CheckOver();
            }
        }
    }

    private void CheckOver()
    {
        if (_over || _setup.Dummy)
        {
            return;
        }

        var party = false;
        var enemies = false;
        foreach (var c in _c)
        {
            if (c.Up)
            {
                if (c.Side == 0)
                {
                    party = true;
                }
                else
                {
                    enemies = true;
                }
            }
        }

        if (!party)
        {
            _over = true;
            _outcome = SimulationValues.Outcomes.PartyDefeated;
            if (_log is not null)
            {
                _log.Line($"Every party member is down: the party is defeated in round {_round}.");
            }
        }
        else if (!enemies)
        {
            _over = true;
            _outcome = SimulationValues.Outcomes.PartyWins;
            if (_log is not null)
            {
                _log.Line($"Every enemy is down: the party wins in round {_round}.");
            }
        }
    }

    internal static string Status(Creature c)
    {
        var text = c.Dead ? "dead" : c.Down ? (!c.T.PcLike ? "0 HP" : c.Stable ? "0 HP, stable" : "0 HP, dying") : $"HP {c.Hp}/{c.MaxHp}";
        if (c.TempHp > 0 && !c.Dead)
        {
            text += $" +{c.TempHp} temp";
        }

        if (c.Conditions.Count > 0)
        {
            text += ", " + string.Join(", ", c.Conditions.Select(a => Cond.Name(a.Condition)).Distinct());
        }

        return text;
    }

    private static string Signed(int value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);
}
