using DndMcp.Domain.Dice;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// What the rest of a turn is worth from one state: expected damage, the objective the policies maximise (expected
/// damage minus use_value for each optional use spent), expected counts for the breakdown (<see cref="TallyLayout"/>),
/// and — in the fight horizon — the distribution of what the turn hands the next one.
///
/// <para>
/// Everything here is an expectation, so a node is the probability-weighted sum of its branches: that is what lets the
/// breakdown (hits per attack line, uses per rider, conditions imposed) come out of the same backward pass as the
/// damage, with no second simulation that could disagree with it.
/// </para>
/// </summary>
internal sealed class NodeValue
{
    public NodeValue(double ev, double objective, double[] tally, Dictionary<CarryKey, double>? carry)
    {
        Ev = ev;
        Objective = objective;
        Tally = tally;
        Carry = carry;
    }

    /// <summary>E[damage from here to the end of the turn].</summary>
    public double Ev { get; }

    /// <summary>E[damage] − Σ use_value × uses, from here on: what optimal riders and the Bonus Action choice maximise.</summary>
    public double Objective { get; }

    public double[] Tally { get; }

    /// <summary>P(each end-of-turn carry), when the fight horizon asks for it; otherwise null.</summary>
    public Dictionary<CarryKey, double>? Carry { get; }
}

/// <summary>Accumulates a node's branches: immediate damage, costs, tally entries and children, each weighted by its probability.</summary>
internal sealed class NodeBuilder
{
    private readonly double[] _tally;
    private readonly Dictionary<CarryKey, double>? _carry;
    private double _ev;
    private double _objective;

    public NodeBuilder(int tallySize, bool trackCarry)
    {
        _tally = new double[tallySize];
        _carry = trackCarry ? [] : null;
    }

    /// <summary>Damage dealt on a branch of probability <paramref name="probability"/>, with the use_value it cost.</summary>
    public void Damage(double probability, double damage, double cost = 0)
    {
        _ev += probability * damage;
        _objective += probability * (damage - cost);
    }

    public void Tally(int index, double amount) => _tally[index] += amount;

    public void Child(double probability, NodeValue child)
    {
        _ev += probability * child.Ev;
        _objective += probability * child.Objective;
        var tally = child.Tally;
        for (var i = 0; i < tally.Length; i++)
        {
            _tally[i] += probability * tally[i];
        }

        if (_carry is not null && child.Carry is not null)
        {
            foreach (var (key, value) in child.Carry)
            {
                _carry[key] = _carry.GetValueOrDefault(key) + (probability * value);
            }
        }
    }

    public void Carry(CarryKey key, double probability)
    {
        if (_carry is not null)
        {
            _carry[key] = _carry.GetValueOrDefault(key) + probability;
        }
    }

    public NodeValue Build() => new(_ev, _objective, _tally, _carry);
}

/// <summary>
/// Where each expected count lives in a <see cref="NodeValue.Tally"/>. Laid out once per plan so every node, sample and
/// round adds up the same vector.
/// </summary>
internal sealed class TallyLayout
{
    private const int PerLine = 4;

    private readonly int _lines;
    private readonly int _riders;
    private readonly int _rerolls;
    private readonly int _conditions;
    private readonly int _extras;
    private readonly int _saves;
    private readonly int _bonusActions;
    private readonly int _applied;
    private readonly int _landed;

    public TallyLayout(TurnPlan plan)
    {
        var next = 0;
        int Take(int count)
        {
            var start = next;
            next += count;
            return start;
        }

        _lines = Take(PerLine * plan.Lines.Count);
        _riders = Take(2 * plan.Riders.Count);
        _rerolls = Take(2 * plan.DamageRerolls.Count);
        _conditions = Take(2 * plan.ConditionsOnHit.Count);
        _extras = Take(plan.ExtraAttacks.Count);
        _saves = Take(3 * plan.SaveEffects.Count);
        _bonusActions = Take(plan.BonusActionOptions.Count + 1);
        _applied = Take(TurnPlan.UnconsciousBit + 1);
        _landed = Take(plan.TrackedSaveConditions.Length);
        Sap = Take(1);
        Size = next;
    }

    public int Size { get; }

    /// <summary>P(at least one hit with a Sap weapon this turn).</summary>
    public int Sap { get; }

    public int LineMade(int line) => _lines + (PerLine * line);

    public int LineHits(int line) => _lines + (PerLine * line) + 1;

    public int LineCrits(int line) => _lines + (PerLine * line) + 2;

    public int LineDamage(int line) => _lines + (PerLine * line) + 3;

    /// <summary>A rider's marginal damage.</summary>
    public int RiderDamage(int rider) => _riders + (2 * rider);

    /// <summary>Hits (misses, for on_miss) a rider went on.</summary>
    public int RiderUses(int rider) => _riders + (2 * rider) + 1;

    public int RerollDamage(int reroll) => _rerolls + (2 * reroll);

    public int RerollUses(int reroll) => _rerolls + (2 * reroll) + 1;

    public int ConditionAttempts(int condition) => _conditions + (2 * condition);

    public int ConditionLands(int condition) => _conditions + (2 * condition) + 1;

    public int ExtraUses(int extra) => _extras + extra;

    public int SaveCasts(int save) => _saves + (3 * save);

    public int SaveDamage(int save) => _saves + (3 * save) + 1;

    public int SaveFailures(int save) => _saves + (3 * save) + 2;

    /// <summary>A Bonus Action option chosen; the index after the last option is "none".</summary>
    public int BonusActionChoice(int option) => _bonusActions + option;

    /// <summary>P(the condition was imposed this turn), by condition bit.</summary>
    public int ConditionApplied(int bit) => _applied + bit;

    /// <summary>P(the tracked save effect landed on its first target this turn).</summary>
    public int SaveLanded(int tracked) => _landed + tracked;
}

/// <summary>
/// Sums weighted, possibly unnormalised damage distributions into one (a mixture), keeping every reachable value.
/// </summary>
internal sealed class PmfAccumulator
{
    private readonly Dictionary<long, double> _weights = [];

    public void Add(double probability, Pmf<double> pmf)
    {
        var values = pmf.Values;
        var weights = pmf.Weights;
        for (var i = 0; i < values.Length; i++)
        {
            _weights[values[i]] = _weights.GetValueOrDefault(values[i]) + (probability * weights[i] / pmf.Total);
        }
    }

    public Pmf<double> Build() => _weights.Count == 0 ? Pmf<double>.Point(0) : Pmf<double>.FromPairs(_weights, 1.0);
}
