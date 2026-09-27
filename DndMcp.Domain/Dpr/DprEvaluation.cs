using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using DndMcp.Domain.Probability;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// One run of <see cref="DprEngine.Evaluate(ResolvedBuild, ResolvedTarget, DprOptions?, WorkMeter?)"/>: the plan, the
/// per-sample evaluators, the horizon, and the result's assembly.
///
/// <para>
/// <b>Turns.</b> A turn from a carried state (first round or not, Vex pending, uses left) is the mixture over the
/// advantage-rate samples of that sample's turn, each with its best power-attack choice; memoised per carried state, so a
/// fight evaluates each distinct carried state once. The reaction is a separate mini-turn, the same every round.
/// </para>
/// </summary>
internal sealed class DprEvaluation
{
    private readonly ResolvedBuild _build;
    private readonly ResolvedTarget _target;
    private readonly DprOptions _options;
    private readonly TurnPlan _plan;
    private readonly DamageModel _damage;
    private readonly IReadOnlyList<SaveEffectDamage> _saves;
    private readonly TallyLayout _layout;
    private readonly Pmf<double>? _saveDice;
    private readonly bool _trackCarry;
    private readonly List<(double Probability, ulong Present)> _samples;
    private readonly int _powerFixedOn;
    private readonly List<int> _powerCombos;
    private readonly Dictionary<(ulong Present, int PowerOn), TurnEvaluator> _evaluators = [];
    private readonly Dictionary<(bool First, bool Vex, ulong Uses), TurnResult> _turns = [];
    private TurnEvaluator? _reactionEvaluator;

    public DprEvaluation(ResolvedBuild build, ResolvedTarget target, DprOptions options, WorkMeter meter)
    {
        _build = build;
        _target = target;
        _options = options;
        _plan = new TurnPlan(build, target, options, meter);
        _damage = new DamageModel(_plan);
        _saves = _plan.SaveEffects.Select(e => new SaveEffectDamage(e, _damage.SaveRoll(e), target)).ToList();
        _layout = new TallyLayout(_plan);
        _saveDice = target.SaveDice is { } dice ? DamageDice.Sum(dice.Dice, meter) : null;
        _trackCarry = options.Horizon == DprHorizons.Fight;
        _samples = AdvantageSamples(_plan.AdvantageSources);

        for (var i = 0; i < _plan.PowerAttacks.Count; i++)
        {
            if (_plan.PowerAttacks[i].Policy == V.PowerAttackPolicies.Always)
            {
                _powerFixedOn |= 1 << i;
            }
        }

        _powerCombos = PowerCombos(_plan.PowerAttacks, _powerFixedOn);
    }

    /// <summary>One turn from a carried state: the mixture over advantage samples, each with its power-attack choice.</summary>
    private sealed record TurnResult(
        double Ev,
        double[] Tally,
        Dictionary<CarryKey, double> Carry,
        double[] PowerOn,
        IReadOnlyList<(double Probability, ulong Present, TurnEvaluator Evaluator)> Choices,
        TurnState Start);

    private sealed record ReactionResult(int Extra, double TriggerProbability, NodeValue Value, TurnEvaluator Evaluator, TurnState Start, IReadOnlyList<NamedAmount> Considered);

    /// <summary>
    /// The horizon. Round1 is the first turn. A fight chains R turns as a Markov chain over what carries — Vex pending,
    /// uses left, and, for "P(it lands at least once per fight)", which conditions and save effects have landed so far —
    /// with the round's reaction added each round. Every per-round figure is the total over the rounds ÷ R.
    /// </summary>
    public DprResult Run()
    {
        var initialUses = TurnState.PackUses(_plan.Resources.Select(r => r.Unlimited ? TurnState.Unlimited : (ulong)r.Resource.Uses).ToList());
        var reaction = Reaction(initialUses);
        var rounds = _options.RoundsEvaluated;

        var tally = new double[_layout.Size];
        var powerOn = new double[_plan.PowerAttacks.Count];
        var byRound = new List<double>();
        var chain = new Dictionary<(bool Vex, ulong Uses, int Ever), double> { [(false, initialUses, 0)] = 1.0 };
        for (var round = 0; round < rounds; round++)
        {
            var roundEv = 0.0;
            var next = new Dictionary<(bool, ulong, int), double>();
            foreach (var ((vex, uses, landed), p) in chain)
            {
                var turn = Turn(round == 0, vex, uses);
                roundEv += p * turn.Ev;
                Add(tally, turn.Tally, p);
                Add(powerOn, turn.PowerOn, p);
                foreach (var (carry, q) in turn.Carry)
                {
                    var key = (carry.Vex, carry.Uses, landed | carry.Landed);
                    next[key] = next.GetValueOrDefault(key) + (p * q);
                }
            }

            if (reaction is not null)
            {
                roundEv += reaction.TriggerProbability * reaction.Value.Ev;
                Add(tally, reaction.Value.Tally, reaction.TriggerProbability);
                tally[_layout.ExtraUses(reaction.Extra)] += reaction.TriggerProbability;
            }

            byRound.Add(roundEv);
            chain = _trackCarry ? next : chain;
        }

        Scale(tally, 1.0 / rounds);
        Scale(powerOn, 1.0 / rounds);
        var ever = new Dictionary<int, double>();
        if (_options.Horizon == DprHorizons.Fight)
        {
            foreach (var ((_, _, mask), p) in chain)
            {
                for (var bit = 0; bit < 31; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                    {
                        ever[bit] = ever.GetValueOrDefault(bit) + p;
                    }
                }
            }
        }

        var first = Turn(true, false, initialUses);
        var distribution = _options.IncludeDistribution ? Round1Distribution(first, reaction) : null;
        return Assemble(byRound, tally, powerOn, ever, first, reaction, distribution);
    }

    private static void Add(double[] into, double[] values, double weight)
    {
        for (var i = 0; i < values.Length; i++)
        {
            into[i] += weight * values[i];
        }
    }

    private static void Scale(double[] values, double factor)
    {
        for (var i = 0; i < values.Length; i++)
        {
            values[i] *= factor;
        }
    }

    /// <summary>
    /// The per-turn mixtures over rate sources (contract §4.2): a source with rate 1 is always present, one with rate 0
    /// never; each other source is present on a turn with its rate, independently, and every attack of that turn shares
    /// the draw. At most 2³ samples (the DSL allows three rate sources).
    /// </summary>
    private static List<(double, ulong)> AdvantageSamples(IReadOnlyList<ResolvedAdvantageSource> sources)
    {
        ulong always = 0;
        var variable = new List<int>();
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i].Rate >= 1)
            {
                always |= 1UL << i;
            }
            else if (sources[i].Rate > 0)
            {
                variable.Add(i);
            }
        }

        var samples = new List<(double, ulong)>();
        for (var bits = 0; bits < 1 << variable.Count; bits++)
        {
            var probability = 1.0;
            var present = always;
            for (var j = 0; j < variable.Count; j++)
            {
                // The first source varies slowest, as in a nested loop over the sources in build order.
                var on = (bits & (1 << (variable.Count - 1 - j))) != 0;
                var rate = sources[variable[j]].Rate;
                probability *= on ? rate : 1 - rate;
                if (on)
                {
                    present |= 1UL << variable[j];
                }
            }

            samples.Add((probability, present));
        }

        return samples;
    }

    /// <summary>
    /// The power-attack combinations to try per turn: the "always" ones on, every subset of the "auto" ones on top, smallest
    /// subsets first so a tie keeps the power attack off.
    /// </summary>
    private static List<int> PowerCombos(IReadOnlyList<ResolvedPowerAttack> powerAttacks, int fixedOn)
    {
        var auto = Enumerable.Range(0, powerAttacks.Count).Where(i => powerAttacks[i].Policy == V.PowerAttackPolicies.Auto).ToList();
        var combos = new List<int>();
        for (var size = 0; size <= auto.Count; size++)
        {
            foreach (var subset in Subsets(auto, size, 0))
            {
                combos.Add(fixedOn | subset);
            }
        }

        return combos;

        static IEnumerable<int> Subsets(List<int> items, int size, int from)
        {
            if (size == 0)
            {
                yield return 0;
                yield break;
            }

            for (var i = from; i <= items.Count - size; i++)
            {
                foreach (var rest in Subsets(items, size - 1, i + 1))
                {
                    yield return (1 << items[i]) | rest;
                }
            }
        }
    }

    private TurnEvaluator Evaluator(ulong present, int powerOn)
    {
        if (!_evaluators.TryGetValue((present, powerOn), out var evaluator))
        {
            evaluator = new TurnEvaluator(_plan, _damage, _saves, _layout, present, powerOn, _trackCarry, _saveDice, _options.TurnStateLimit);
            _evaluators[(present, powerOn)] = evaluator;
        }

        return evaluator;
    }

    private TurnResult Turn(bool first, bool vex, ulong uses)
    {
        if (_turns.TryGetValue((first, vex, uses), out var known))
        {
            return known;
        }

        var start = new TurnState(_plan.StartSegment, 0, -1, 0, uses, (first ? TurnState.FirstRoundFlag : 0) | (vex ? TurnState.VexFlag : 0));
        var ev = 0.0;
        var tally = new double[_layout.Size];
        var carry = new Dictionary<CarryKey, double>();
        var powerOn = new double[_plan.PowerAttacks.Count];
        var choices = new List<(double, ulong, TurnEvaluator)>();
        foreach (var (probability, present) in _samples)
        {
            TurnEvaluator? best = null;
            NodeValue? bestValue = null;
            foreach (var combo in _powerCombos)
            {
                var evaluator = Evaluator(present, combo);
                var value = evaluator.Value(start);

                // Power attack auto maximises the turn's expected damage (not the use_value objective); a tie keeps it off.
                if (bestValue is null || DprLimits.Beats(value.Ev, bestValue.Ev))
                {
                    best = evaluator;
                    bestValue = value;
                }
            }

            ev += probability * bestValue!.Ev;
            Add(tally, bestValue.Tally, probability);
            if (bestValue.Carry is not null)
            {
                foreach (var (key, q) in bestValue.Carry)
                {
                    carry[key] = carry.GetValueOrDefault(key) + (probability * q);
                }
            }

            for (var i = 0; i < powerOn.Length; i++)
            {
                if ((best!.PowerAttacksOn & (1 << i)) != 0)
                {
                    powerOn[i] += probability;
                }
            }

            choices.Add((probability, present, best!));
        }

        var result = new TurnResult(ev, tally, carry, powerOn, choices, start);
        _turns[(first, vex, uses)] = result;
        return result;
    }

    /// <summary>
    /// The round's reaction (contract §4.2): the reaction extra attack with the best trigger probability × expected damage,
    /// as a mini-turn of its own — fresh once-per-turn riders (it happens on another creature's turn), no resources, no
    /// Bonus Action, none of this turn's Vex, conditions or rate sources; only "always" power attacks.
    /// </summary>
    private ReactionResult? Reaction(ulong uses)
    {
        ReactionResult? best = null;
        var considered = new List<NamedAmount>();
        for (var e = 0; e < _plan.ExtraAttacks.Count; e++)
        {
            var segment = _plan.ReactionSegments[e];
            if (segment < 0)
            {
                continue;
            }

            _reactionEvaluator ??= new TurnEvaluator(_plan, _damage, _saves, _layout, 0, _powerFixedOn, trackCarry: false, _saveDice, _options.TurnStateLimit);
            // The uses are there, but a reaction attack may not spend them (TurnEvaluator.Available enforces it).
            var start = new TurnState(segment, 0, -1, 0, uses, 0);
            var value = _reactionEvaluator.Value(start);
            var probability = _plan.ExtraAttacks[e].TriggerProbability ?? 1.0;
            considered.Add(new NamedAmount(_plan.ExtraAttacks[e].Source.Label, probability * value.Ev));
            if (best is null || DprLimits.Beats(probability * value.Ev, best.TriggerProbability * best.Value.Ev))
            {
                best = new ReactionResult(e, probability, value, _reactionEvaluator, start, considered);
            }
        }

        return best;
    }

    private Pmf<double> Round1Distribution(TurnResult first, ReactionResult? reaction)
    {
        var mixture = new PmfAccumulator();
        foreach (var (probability, _, evaluator) in first.Choices)
        {
            mixture.Add(probability, evaluator.Distribution(first.Start));
        }

        var turn = mixture.Build();
        if (reaction is null)
        {
            return turn;
        }

        var reactionMixture = new PmfAccumulator();
        reactionMixture.Add(reaction.TriggerProbability, reaction.Evaluator.Distribution(reaction.Start));
        if (reaction.TriggerProbability < 1)
        {
            reactionMixture.Add(1 - reaction.TriggerProbability, Pmf<double>.Point(0));
        }

        return turn.Convolve(reactionMixture.Build(), _plan.Meter);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Assembly.
    // ------------------------------------------------------------------------------------------------------------------

    private DprResult Assemble(
        List<double> byRound,
        double[] tally,
        double[] powerOn,
        Dictionary<int, double> ever,
        TurnResult first,
        ReactionResult? reaction,
        Pmf<double>? distribution)
    {
        var fight = _options.Horizon == DprHorizons.Fight;
        var states = _evaluators.Values.Sum(e => e.StateCount) + (_reactionEvaluator?.StateCount ?? 0);
        return new DprResult
        {
            BuildName = _build.Name,
            Edition = _build.Edition,
            Level = _build.Level,
            Build = _build,
            Target = _target,
            Horizon = _options.Horizon,
            Rounds = _options.RoundsEvaluated,
            DamagePerRound = byRound.Sum() / byRound.Count,
            Round1Damage = byRound[0],
            DamageByRound = byRound,
            TargetArmorClass = _target.ArmorClass,
            Attacks = AttackReports(tally),
            Riders = RiderReports(tally),
            ExtraAttacks = ExtraAttackReports(tally),
            Conditions = ConditionReports(tally, fight ? ever : null),
            SaveEffects = SaveEffectReports(tally, fight ? ever : null),
            PowerAttacks = PowerAttackReports(powerOn, first),
            Resources = ResourceUses(tally),
            BonusActionChoices = BonusActionChoices(tally),
            Reaction = reaction is null ? null : ReactionReport(reaction),
            // P(turn damage ≥ HP) means "the turn alone drops one creature" only when the turn's damage is one creature's:
            // an area effect's total is summed over several targets, so it is left out (the save effect's kill figures
            // answer that question instead).
            Round1Distribution = distribution is null
                ? null
                : DamageDistribution.From(distribution, _plan.SaveEffects.All(s => s.Targets == 1) ? _target.HitPoints : null),
            SapChancePerTurn = _plan.TracksSap ? tally[_layout.Sap] : null,
            Rulings = Rulings(),
            Notes = Notes(tally),
            TurnStates = states,
        };
    }

    private static int KindOrder(AttackKind kind) => kind switch
    {
        AttackKind.Action => 0,
        AttackKind.Surge => 1,
        AttackKind.BonusAction => 2,
        AttackKind.Hew => 3,
        AttackKind.Cleave => 4,
        _ => 5,
    };

    private List<AttackReport> AttackReports(double[] tally) =>
        _plan.Lines
            .OrderBy(l => KindOrder(l.Kind))
            .ThenBy(l => l.ExtraIndex)
            .ThenBy(l => l.AttackIndex)
            .Select(line =>
            {
                var made = tally[_layout.LineMade(line.Index)];
                return new AttackReport(
                    line.Attack.Name,
                    line.Kind.Use(),
                    line.ExtraIndex >= 0 ? _plan.ExtraAttacks[line.ExtraIndex].Source.Label : null,
                    line.Attack.AttackBonus,
                    line.Attack.CritMin,
                    line.ArmorClass,
                    made,
                    made > 0 ? tally[_layout.LineHits(line.Index)] / made : 0,
                    made > 0 ? tally[_layout.LineCrits(line.Index)] / made : 0,
                    tally[_layout.LineDamage(line.Index)]);
            })
            .ToList();

    private List<RiderReport> RiderReports(double[] tally)
    {
        var reports = new List<RiderReport>();
        for (var r = 0; r < _plan.Riders.Count; r++)
        {
            var rider = _plan.Riders[r];
            reports.Add(new RiderReport(
                rider.Source.Label,
                V.Kinds.ExtraDamage,
                rider.When,
                rider.IsOptional ? rider.Policy : null,
                rider.IsOptional && rider.Policy == V.Policies.Optimal ? rider.UseValue : null,
                rider.Resource,
                tally[_layout.RiderDamage(r)],
                tally[_layout.RiderUses(r)]));
        }

        for (var s = 0; s < _plan.DamageRerolls.Count; s++)
        {
            var reroll = _plan.DamageRerolls[s];
            reports.Add(new RiderReport(
                reroll.Source.Label,
                V.Kinds.RerollDamageTakeBest,
                null,
                reroll.Policy,
                reroll.Policy == V.Policies.Optimal ? reroll.UseValue : null,
                null,
                tally[_layout.RerollDamage(s)],
                tally[_layout.RerollUses(s)]));
        }

        return reports;
    }

    private List<ExtraAttackReport> ExtraAttackReports(double[] tally) =>
        _plan.ExtraAttacks
            .Select((extra, e) => new ExtraAttackReport(
                extra.Source.Label,
                extra.Attack,
                extra.Action,
                extra.Trigger,
                extra.Resource,
                tally[_layout.ExtraUses(e)],
                _plan.Lines.Where(l => l.ExtraIndex == e).Sum(l => tally[_layout.LineDamage(l.Index)])))
            .ToList();

    private List<ConditionReport> ConditionReports(double[] tally, Dictionary<int, double>? ever) =>
        _plan.ConditionsOnHit
            .Select((condition, c) =>
            {
                var bit = TurnPlan.ConditionBit(condition.Condition);
                return new ConditionReport(
                    condition.Source.Label,
                    condition.Condition,
                    condition.Ability,
                    tally[_layout.ConditionAttempts(c)],
                    tally[_layout.ConditionLands(c)],
                    tally[_layout.ConditionApplied(bit)],
                    ever is null ? null : ever.GetValueOrDefault(bit));
            })
            .ToList();

    /// <summary>P(the target fails) against the target as given: its initial condition, cover, save dice, Magic Resistance.</summary>
    private double InitialFailChance(ResolvedSaveEffect effect)
    {
        var conditions = _plan.InitialConditions;
        var dex = effect.Ability == V.Abilities.Dex;
        const int autoFailConditions = (1 << TurnPlan.StunnedBit) | (1 << TurnPlan.ParalyzedBit) | (1 << TurnPlan.UnconsciousBit);
        var autoFail = (dex || effect.Ability == V.Abilities.Str) && (conditions & autoFailConditions) != 0;
        var advantage = (effect.Magical && _target.MagicResistance) || (dex && (conditions & (1 << TurnPlan.DodgingBit)) != 0);
        var disadvantage = dex && (conditions & (1 << TurnPlan.RestrainedBit)) != 0;
        var bonus = _target.SaveBonus(effect.Ability) + (dex ? _target.CoverBonus : 0);
        return SavingThrow.FailChance(effect.Dc, bonus, D20.Resolve(advantage, disadvantage), _saveDice, autoFail);
    }

    private List<SaveEffectReport> SaveEffectReports(double[] tally, Dictionary<int, double>? ever)
    {
        var reports = new List<SaveEffectReport>();
        for (var i = 0; i < _plan.SaveEffects.Count; i++)
        {
            var effect = _plan.SaveEffects[i];
            var damage = _saves[i];
            var fail = InitialFailChance(effect);
            var kills = _target.HitPoints is { } hp && effect.Damage is not null ? damage.Kills(fail, hp) : null;
            double? castsToLand = effect.Condition is not null && _target.LegendaryResistance > 0
                ? SavingThrow.ExpectedCastsToLand(fail, _target.LegendaryResistance)
                : null;
            var landedBit = _plan.SaveLandedBits[i];
            double? landPerTurn = landedBit >= 0 ? tally[_layout.SaveLanded(landedBit)] : null;
            double? landPerFight = ever is not null && landedBit >= 0 ? ever.GetValueOrDefault(8 + landedBit) : null;
            reports.Add(new SaveEffectReport(
                effect.Source.Label,
                effect.Ability,
                effect.Dc,
                effect.Targets,
                effect.TargetsFromArea,
                effect.Condition,
                fail,
                damage.MeanPerTarget(fail),
                damage.MeanTotal(fail),
                tally[_layout.SaveCasts(i)],
                tally[_layout.SaveDamage(i)],
                kills?.EffectiveDamage,
                kills?.EachDies,
                kills?.AllDie,
                kills?.ExpectedKills,
                kills?.Distribution,
                castsToLand,
                landPerTurn,
                landPerFight));
        }

        return reports;
    }

    private List<ResourceUse> ResourceUses(double[] tally)
    {
        var uses = new List<ResourceUse>();
        void Add(ModifierRef source, int slot, double perRound)
        {
            if (slot >= 0)
            {
                var resource = _plan.Resources[slot];
                uses.Add(new ResourceUse(source, resource.Resource, resource.Unlimited, perRound));
            }
        }

        for (var r = 0; r < _plan.Riders.Count; r++)
        {
            Add(_plan.Riders[r].Source, _plan.RiderSlots[r], tally[_layout.RiderUses(r)]);
        }

        for (var e = 0; e < _plan.ExtraAttacks.Count; e++)
        {
            Add(_plan.ExtraAttacks[e].Source, _plan.ExtraSlots[e], tally[_layout.ExtraUses(e)]);
        }

        for (var i = 0; i < _plan.SaveEffects.Count; i++)
        {
            Add(_plan.SaveEffects[i].Source, _plan.SaveSlots[i], tally[_layout.SaveCasts(i)]);
        }

        for (var c = 0; c < _plan.ConditionsOnHit.Count; c++)
        {
            Add(_plan.ConditionsOnHit[c].Source, _plan.ConditionSlots[c], tally[_layout.ConditionAttempts(c)]);
        }

        return uses.OrderBy(u => u.Source.Number).ToList();
    }

    private List<PowerAttackReport> PowerAttackReports(double[] powerOn, TurnResult first)
    {
        var reports = new List<PowerAttackReport>();
        for (var i = 0; i < _plan.PowerAttacks.Count; i++)
        {
            var power = _plan.PowerAttacks[i];
            var choices = first.Choices
                .Select(c => new PowerAttackChoice(
                    c.Probability,
                    _plan.AdvantageSources.Where((_, s) => (c.Present & (1UL << s)) != 0 && _plan.AdvantageSources[s].Rate < 1).Select(s => s.Source.Label).ToList(),
                    (c.Evaluator.PowerAttacksOn & (1 << i)) != 0))
                .ToList();
            var toggles = new List<PowerAttackToggle>();
            foreach (var line in _plan.Lines.Where(l => l.Kind is AttackKind.Action or AttackKind.BonusAction && (l.PowerAttacks & (1 << i)) != 0))
            {
                var attack = line.Attack;
                var options = new D20Options(D20Mode.Normal, attack.Lucky);
                var plain = AttackRoll.Odds(attack.AttackBonus, line.ArmorClass, attack.CritMin, options, line.BonusDice).Hit;
                var penalised = AttackRoll.Odds(attack.AttackBonus - power.Penalty, line.ArmorClass, attack.CritMin, options, line.BonusDice).Hit;
                var onHit = _damage.Hit(line, false, _damage.MandatoryRiders(line, false), false, 0).Mean;
                var threshold = onHit + power.Bonus > 0 ? onHit / (onHit + power.Bonus) : 0;
                toggles.Add(new PowerAttackToggle(attack.Name, plain, penalised, onHit, threshold, penalised * (onHit + power.Bonus) > plain * onHit));
            }

            reports.Add(new PowerAttackReport(power.Source.Label, power.Penalty, power.Bonus, power.Policy, powerOn[i], choices, toggles));
        }

        return reports;
    }

    private List<BonusActionChoice> BonusActionChoices(double[] tally)
    {
        if (_plan.BonusActionOptions.Count == 0)
        {
            return [];
        }

        var choices = _plan.BonusActionOptions.Select(o => new BonusActionChoice(o.Label, tally[_layout.BonusActionChoice(o.Index)])).ToList();
        choices.Add(new BonusActionChoice("none", tally[_layout.BonusActionChoice(_plan.BonusActionOptions.Count)]));
        return choices;
    }

    private ReactionReport ReactionReport(ReactionResult reaction)
    {
        var extra = _plan.ExtraAttacks[reaction.Extra];
        return new ReactionReport(
            extra.Source.Label,
            extra.Attack,
            reaction.TriggerProbability,
            reaction.Value.Ev,
            reaction.TriggerProbability * reaction.Value.Ev,
            reaction.Considered);
    }

    private List<RulingUsed> Rulings()
    {
        var rulings = new List<RulingUsed>();
        var r = _build.Rulings;
        var aaoParts = _build.Attacks.Any(a => a.DamageParts.Any(p => p.AttackActionOnly));
        if (_plan.ExtraAttacks.Any(e => e.Action == V.ExtraAttackActions.BonusAction) && aaoParts)
        {
            rulings.Add(new RulingUsed("hew_gets_pb", r.HewGetsPb,
                "Bonus Action extra attacks (Hew) get attack_action_only flat damage (2024 GWM's +PB); RAW they are not part of the Attack action."));
        }

        if (_build.Attacks.Any(a => a.Mastery == V.Masteries.Cleave) && (aaoParts || _plan.Riders.Any(x => x.AttackActionOnly)))
        {
            rulings.Add(new RulingUsed("cleave_part_of_attack_action", r.CleavePartOfAttackAction,
                "Cleave's attack counts as part of the Attack action (attack_action_only damage and riders)."));
        }

        if (_build.Attacks.Any(a => a.WeaponRemap is not null) && _plan.Riders.Any(x => x.Damage.HasDice))
        {
            rulings.Add(new RulingUsed("gwf_on_riders", r.GwfOnRiders, "Great Weapon Fighting also remaps rider dice (smites, Hex), not only the weapon's."));
        }

        if (_plan.DamageRerolls.Count > 0)
        {
            rulings.Add(new RulingUsed("savage_attacker_on_crit_dice", r.SavageAttackerOnCritDice,
                "On a crit, Savage Attacker rerolls the whole doubled set of weapon dice; otherwise one set, with the extra crit set rolled once."));
        }

        return rulings;
    }

    private List<string> Notes(double[] tally)
    {
        var notes = new List<string>();
        foreach (var extra in _plan.ExtraAttacks.Where(e => e.Action == V.ExtraAttackActions.BonusAction && e.Trigger == V.Triggers.Crit))
        {
            notes.Add(
                $"{extra.Source.Label}: only its critical-hit trigger is modelled; \"reduce a creature to 0 hit points\" needs the target's HP " +
                "over the turn and is left to the simulator.");
        }

        if (_plan.ReactionSegments.Any(s => s >= 0))
        {
            notes.Add(
                "The reaction attack happens on another creature's turn: it gets fresh once-per-turn riders but spends no resources, and " +
                "none of this turn's Vex, imposed conditions or advantage-rate sources apply to it. One reaction a round: the best by " +
                "trigger probability × damage.");
        }

        if (_plan.TracksSap)
        {
            notes.Add(FormattableString.Invariant(
                $"Sap adds no damage: P(at least one sapping hit) per turn is {tally[_layout.Sap]:0.###}, each giving the target Disadvantage on its next attack roll."));
        }

        foreach (var mastery in new[] { V.Masteries.Nick, V.Masteries.Push, V.Masteries.Slow })
        {
            var attacks = _build.Attacks.Where(a => a.Mastery == mastery).Select(a => a.Name).ToList();
            if (attacks.Count == 0)
            {
                continue;
            }

            notes.Add(mastery == V.Masteries.Nick
                ? $"{string.Join(", ", attacks)}: Nick changes only the action economy; model it by making the Light weapon's extra attack an action attack (count) instead of a bonus_action one."
                : $"{string.Join(", ", attacks)}: {mastery} adds no damage per round in v1 (there is no grid or movement).");
        }

        if (_build.Attacks.Any(a => a.Mastery == V.Masteries.Cleave) && _target.SecondTargetRate == 0)
        {
            notes.Add("Cleave never triggers here: set target.second_target_rate (the chance a second creature is within reach) above 0.");
        }

        if ((_plan.InitialConditions & (1 << TurnPlan.UnconsciousBit)) != 0 && _build.Attacks.Any(a => a.IsRanged))
        {
            notes.Add("An Unconscious creature is also Prone, so a ranged attack's Advantage (Unconscious) and Disadvantage (Prone) cancel.");
        }

        foreach (var setup in _plan.SetupCosts)
        {
            notes.Add(setup.Cost == V.Setup.Action
                ? $"{setup.Source.Label}: set up with round 1's Action (no Attack action that round), active from then on."
                : $"{setup.Source.Label}: set up with round 1's Bonus Action, active from then on.");
        }

        foreach (var effect in _plan.SaveEffects)
        {
            if (effect.TargetsFromArea)
            {
                notes.Add(
                    $"{effect.Source.Label}: {DslText.Number(effect.Targets)} targets from the DMG's \"Targets in Areas of Effect\" for a " +
                    $"{DslText.Number(effect.Size ?? 0)}-ft {effect.Shape} (±1d3 for how bunched the creatures are).");
            }

            if (effect.Condition is not null && effect.ConditionIsMechanical)
            {
                notes.Add($"{effect.Source.Label}: its {effect.Condition} is reported (chance it lands) but does not change later attacks this turn.");
            }

            if (effect.Condition is null && effect.Damage is not null && _target.LegendaryResistance > 0)
            {
                notes.Add($"{effect.Source.Label}: Legendary Resistance is assumed not spent on a damage-only effect.");
            }
        }

        if (_plan.ActionSaveIndex >= 0 && _plan.SurgeExtras.Count > 0)
        {
            notes.Add("Action Surge is not used with a save-effect routine: its extra action would be another cast, which this build does not describe.");
        }

        foreach (var extra in _plan.ExtraAttacks.Where(e => e.Action == V.ExtraAttackActions.Reaction && e.Resource is not null))
        {
            notes.Add($"{extra.Source.Label}: its own resource is not tracked; it is treated as available every round.");
        }

        notes.AddRange(_build.Notes);
        notes.AddRange(_target.Notes);
        notes.AddRange(TargetResolver.Warnings(_build, _target));
        return notes.Distinct().ToList();
    }
}
