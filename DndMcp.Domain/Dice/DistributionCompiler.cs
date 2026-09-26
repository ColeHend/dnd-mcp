using System.Numerics;

namespace DndMcp.Domain.Dice;

/// <summary>
/// Thrown when an expression's distribution has no exact form here: plain <c>!</c> or <c>!p</c> explosions combined
/// with keep/drop, where the pool size itself is random. <see cref="DiceDistribution"/> answers with Monte Carlo.
/// </summary>
public sealed class NotExactlyComputableException : Exception
{
    public NotExactlyComputableException(string reason)
        : base(reason)
    {
    }
}

/// <summary>
/// Turns a parsed expression into its exact distribution, following the same per-die order as
/// <see cref="DiceEvaluator"/>: roll → reroll → explode → clamp, then keep/drop → count or sum.
/// </summary>
internal sealed class DistributionCompiler<T>
    where T : INumber<T>
{
    private readonly IPmfArithmetic<T> _math;
    private readonly WorkMeter _meter;
    private readonly DiceCaps _caps;

    public DistributionCompiler(IPmfArithmetic<T> math, WorkMeter meter, DiceCaps caps)
    {
        _math = math;
        _meter = meter;
        _caps = caps;
    }

    public Pmf<T> Compile(DiceNode node) => node switch
    {
        ConstantNode c => Pmf<T>.Point(c.Value),
        DiceGroupNode g => _math.Check(CompileGroup(g.Group)),
        NegateNode n => Compile(n.Operand).Map(v => -v, _meter),
        GroupingNode p => Compile(p.Inner),
        SumNode s => _math.Check(Compile(s.Left).Convolve(
            s.Subtract ? Compile(s.Right).Map(v => -v, _meter) : Compile(s.Right), _meter)),
        ScaleNode { Divide: true } d => Compile(d.Operand).Map(v => DiceMath.FloorDivide(v, d.Factor), _meter),
        ScaleNode m => Compile(m.Operand).Map(v => v * m.Factor, _meter),
        _ => throw new InvalidOperationException($"Unknown dice node {node.GetType().Name}."),
    };

    private Pmf<T> CompileGroup(DiceGroup group)
    {
        var physical = _math.FromIntegerWeights(PhysicalDie.Weights(group, out var total), total);
        Func<long, long> score = group.CountsSuccesses ? v => group.Score(v) : v => v;

        if (group.Explode is { Kind: ExplodeKind.Standard or ExplodeKind.Penetrating } separate)
        {
            if (group.Keep is not null)
            {
                throw new NotExactlyComputableException(
                    $"\"{group.Text}\" keeps or drops dice from a pool whose size depends on how many explode");
            }

            // Each die in the chain counts on its own, so clamping and scoring happen per die, before summing.
            var chain = Chain(physical, separate, (first, face) =>
                score(group.Clamp(first || separate.Kind == ExplodeKind.Standard ? face : face - 1)));
            return chain.Power(group.Count, _meter);
        }

        // With !! the chain is one die: clamp its compounded total, not each face.
        var perDie = group.Explode is { } compound
            ? Chain(physical, compound, (_, face) => face).Map(group.Clamp, _meter)
            : physical.Map(group.Clamp, _meter);

        if (group.Keep is { } keep)
        {
            return KeepDistribution.Compute(perDie, group.Count, (int)keep.KeptOf(group.Count), keep.Highest, score, _math, _meter);
        }

        return perDie.Map(score, _meter).Power(group.Count, _meter);
    }

    /// <summary>
    /// The distribution of one die's explosion chain, built from the deepest level up. The die at the cap does not
    /// explode — exactly as <see cref="DiceEvaluator"/> stops — and <paramref name="contribution"/> says what each
    /// face adds (the first die differs from later ones only for penetrating dice).
    /// </summary>
    /// <remarks>
    /// Always built to the full cap, in floating point too. Stopping once the chance of going deeper is negligible
    /// saves work, but it silently removes every total only a longer chain reaches: 4d6! reported its range as 4-600
    /// instead of 4-2424, and "0%" for totals that are possible. The support must stay exact (see <see cref="Pmf{T}"/>).
    /// </remarks>
    private Pmf<T> Chain(Pmf<T> physical, ExplodeRule explode, Func<bool, long, long> contribution)
    {
        var depth = _caps.MaxExplosionsPerDie;

        var explodingFaces = 0L;
        foreach (var face in physical.Values)
        {
            explodingFaces += explode.Condition.Matches(face) ? 1 : 0;
        }

        var chain = physical.Map(face => contribution(depth == 0, face), _meter);
        for (var level = depth - 1; level >= 0; level--)
        {
            var first = level == 0;
            var weights = new Dictionary<long, T>();
            _meter.Spend((physical.Count + (explodingFaces * chain.Count)) * WorkMeter.DictionaryCost);

            for (var i = 0; i < physical.Count; i++)
            {
                var face = physical.Values[i];
                var weight = physical.Weights[i];
                var added = contribution(first, face);

                if (explode.Condition.Matches(face))
                {
                    for (var j = 0; j < chain.Count; j++)
                    {
                        Add(weights, chain.Values[j] + added, weight * chain.Weights[j]);
                    }
                }
                else
                {
                    Add(weights, added, weight * chain.Total);
                }
            }

            chain = _math.Check(Pmf<T>.FromDictionary(weights, physical.Total * chain.Total));
        }

        return chain;
    }

    private static void Add(Dictionary<long, T> into, long key, T weight) =>
        into[key] = into.TryGetValue(key, out var existing) ? existing + weight : weight;
}

/// <summary>
/// One die's face distribution after rerolls, as integer weights over an integer total, shared by the compiler and
/// the explosion-cap bound so both describe the same die.
/// </summary>
internal static class PhysicalDie
{
    /// <summary>
    /// <list type="bullet">
    /// <item>No reroll: every face weight 1, total M.</item>
    /// <item><c>r</c> (until it stops matching): uniform over the non-matching faces.</item>
    /// <item><c>ro</c> (once): P(v) = [v does not match]/M + (matching faces/M)·(1/M), i.e. weights over M².</item>
    /// </list>
    /// </summary>
    public static List<KeyValuePair<long, long>> Weights(DiceGroup group, out long total)
    {
        var sides = group.Sides;
        var weights = new List<KeyValuePair<long, long>>(sides);

        if (group.Reroll is not { } reroll)
        {
            for (var face = 1; face <= sides; face++)
            {
                weights.Add(KeyValuePair.Create((long)face, 1L));
            }

            total = sides;
            return weights;
        }

        var matching = 0L;
        for (var face = 1; face <= sides; face++)
        {
            matching += reroll.Condition.Matches(face) ? 1 : 0;
        }

        for (var face = 1; face <= sides; face++)
        {
            var matches = reroll.Condition.Matches(face);
            var weight = reroll.Once ? (matches ? 0 : sides) + matching : (matches ? 0 : 1);
            if (weight > 0)
            {
                weights.Add(KeyValuePair.Create((long)face, weight));
            }
        }

        total = reroll.Once ? (long)sides * sides : sides - matching;
        return weights;
    }
}
