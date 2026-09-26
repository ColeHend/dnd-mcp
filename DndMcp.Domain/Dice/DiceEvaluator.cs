using System.Globalization;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Dice;

/// <summary>
/// The caps that bound one die's work. Rolling and the exact distributions read the same instance, so what
/// <c>dice_odds</c> computes is the distribution of what <c>dice_roll</c> does, caps included. Tests lower them to
/// make every roll path enumerable.
/// </summary>
public sealed record DiceCaps(int MaxExplosionsPerDie, int MaxRerollsPerDie)
{
    public static DiceCaps Default { get; } = new(DiceLimits.MaxExplosionsPerDie, DiceLimits.MaxRerollsPerDie);
}

/// <summary>One physical roll of one die.</summary>
/// <param name="Rerolled">Replaced by a reroll (r/ro): shown, never counted.</param>
/// <param name="Exploded">Counted, and triggered another roll.</param>
public sealed record RolledFace(int Face, bool Rerolled, bool Exploded);

/// <summary>
/// One member of a group's pool. Usually one counted face; with <c>!!</c> it is the whole compounded chain.
/// </summary>
/// <param name="Faces">Every physical roll behind this die, in order, rerolled-away faces included.</param>
/// <param name="Raw">The value before clamping: the counted faces summed, minus 1 if <see cref="Penetrated"/>.</param>
/// <param name="Value">After min/max: what keep/drop compares and what the sum counts.</param>
/// <param name="Exploded">With <c>!</c>/<c>!p</c>: the next die in the pool was added by this one.</param>
/// <param name="Penetrated">Added by a <c>!p</c> explosion, so it counts one less than its face.</param>
/// <param name="Dropped">Removed by keep/drop.</param>
/// <param name="Score">+1 success, −1 failure, 0 otherwise; only meaningful when the group counts successes.</param>
public sealed record RolledDie(IReadOnlyList<RolledFace> Faces, long Raw, long Value, bool Exploded, bool Penetrated, bool Dropped, int Score);

public sealed record RolledGroup(DiceGroup Group, IReadOnlyList<RolledDie> Dice, long Value);

/// <summary>A finished roll: the total, every group's dice in expression order, and the comparison's verdict if any.</summary>
public sealed record DiceRoll(DiceExpression Expression, long Total, IReadOnlyList<RolledGroup> Groups, bool? ComparisonMet);

/// <summary>
/// Counts physical die rolls across one tool call (all of its <c>times</c>), so a pathological expression fails with
/// a message instead of running for minutes. See <see cref="DiceLimits.MaxPhysicalRollsPerCall"/>.
/// </summary>
public sealed class DiceRollBudget
{
    public DiceRollBudget(long maxPhysicalRolls = DiceLimits.MaxPhysicalRollsPerCall)
    {
        MaxPhysicalRolls = maxPhysicalRolls;
    }

    public long MaxPhysicalRolls { get; }

    public long PhysicalRolls { get; internal set; }
}

/// <summary>
/// Rolls a <see cref="DiceExpression"/>. Per die: roll → reroll → explode → clamp; then per pool: keep/drop → count
/// successes or sum. The same code draws Monte Carlo samples for <see cref="DiceDistribution"/> (without recording
/// faces), so an estimate and a real roll can never disagree about what an expression means.
/// </summary>
public static class DiceEvaluator
{
    public static DiceRoll Roll(DiceExpression expression, IDiceRoller roller, DiceRollBudget? budget = null, DiceCaps? caps = null)
    {
        var evaluator = new Evaluator(expression, roller, budget ?? new DiceRollBudget(), caps ?? DiceCaps.Default, record: true);
        var total = evaluator.Evaluate(expression.Root);
        return new DiceRoll(expression, total, evaluator.Groups, expression.Comparison?.Matches(total));
    }

    /// <summary>
    /// A reusable sampler that returns only totals, for Monte Carlo. Reuses its buffers between samples, so it is
    /// not thread-safe.
    /// </summary>
    internal static Func<long> CreateSampler(DiceExpression expression, IDiceRoller roller, DiceRollBudget budget, DiceCaps caps)
    {
        var evaluator = new Evaluator(expression, roller, budget, caps, record: false);
        return () => evaluator.Evaluate(expression.Root);
    }

    private sealed class Evaluator
    {
        private readonly DiceExpression _expression;
        private readonly IDiceRoller _roller;
        private readonly DiceRollBudget _budget;
        private readonly DiceCaps _caps;
        private readonly bool _record;
        private readonly List<PoolEntry> _pool = [];
        private readonly List<RolledFace> _faces = [];
        private int[] _order = new int[16];

        public Evaluator(DiceExpression expression, IDiceRoller roller, DiceRollBudget budget, DiceCaps caps, bool record)
        {
            _expression = expression;
            _roller = roller;
            _budget = budget;
            _caps = caps;
            _record = record;
        }

        public List<RolledGroup> Groups { get; } = [];

        public long Evaluate(DiceNode node) => node switch
        {
            ConstantNode c => c.Value,
            DiceGroupNode g => RollGroup(g.Group),
            NegateNode n => -Evaluate(n.Operand),
            GroupingNode p => Evaluate(p.Inner),
            SumNode s => s.Subtract ? Evaluate(s.Left) - Evaluate(s.Right) : Evaluate(s.Left) + Evaluate(s.Right),
            ScaleNode { Divide: true } d => DiceMath.FloorDivide(Evaluate(d.Operand), d.Factor),
            ScaleNode m => Evaluate(m.Operand) * m.Factor,
            _ => throw new InvalidOperationException($"Unknown dice node {node.GetType().Name}."),
        };

        private long RollGroup(DiceGroup group)
        {
            _pool.Clear();
            _faces.Clear();

            for (var i = 0; i < group.Count; i++)
            {
                if (group.Explode is { Kind: ExplodeKind.Compound } compound)
                {
                    RollCompoundDie(group, compound);
                }
                else
                {
                    RollSeparateDice(group);
                }
            }

            MarkDropped(group);

            long value = 0;
            foreach (var entry in _pool)
            {
                if (!entry.Dropped)
                {
                    value += group.CountsSuccesses ? entry.Score : entry.Value;
                }
            }

            if (_record)
            {
                var dice = new List<RolledDie>(_pool.Count);
                foreach (var e in _pool)
                {
                    dice.Add(new RolledDie(_faces.GetRange(e.FaceStart, e.FaceCount), e.Raw, e.Value, e.Exploded, e.Penetrated, e.Dropped, e.Score));
                }

                Groups.Add(new RolledGroup(group, dice, value));
            }

            return value;
        }

        /// <summary><c>!!</c>: every explosion adds into one pool die, and min/max clamp the compounded total.</summary>
        private void RollCompoundDie(DiceGroup group, ExplodeRule explode)
        {
            var faceStart = _faces.Count;
            long raw = 0;

            for (var depth = 0; ; depth++)
            {
                var face = RollPhysical(group);
                var explodes = depth < _caps.MaxExplosionsPerDie && explode.Condition.Matches(face);
                AddFace(face, rerolled: false, explodes);
                raw += face;

                if (!explodes)
                {
                    break;
                }
            }

            var value = group.Clamp(raw);
            _pool.Add(new PoolEntry(raw, value, group.Score(value), false, false, faceStart, _faces.Count - faceStart));
        }

        /// <summary>No explosion, <c>!</c> or <c>!p</c>: every roll that counts is its own pool die, clamped on its own.</summary>
        private void RollSeparateDice(DiceGroup group)
        {
            for (var depth = 0; ; depth++)
            {
                var faceStart = _faces.Count;
                var face = RollPhysical(group);
                var explodes = group.Explode is { } explode && depth < _caps.MaxExplosionsPerDie && explode.Condition.Matches(face);
                AddFace(face, rerolled: false, explodes);

                var penetrated = depth > 0 && group.Explode?.Kind == ExplodeKind.Penetrating;
                long raw = penetrated ? face - 1 : face;
                var value = group.Clamp(raw);
                _pool.Add(new PoolEntry(raw, value, group.Score(value), explodes, penetrated, faceStart, _faces.Count - faceStart));

                if (!explodes)
                {
                    break;
                }
            }
        }

        /// <summary>One die's face after rerolls (r: until it stops matching; ro: once).</summary>
        private int RollPhysical(DiceGroup group)
        {
            var face = Draw(group.Sides);
            if (group.Reroll is not { } reroll)
            {
                return face;
            }

            for (var rerolls = 0; reroll.Condition.Matches(face); rerolls++)
            {
                AddFace(face, rerolled: true, exploded: false);

                if (reroll.Once)
                {
                    return Draw(group.Sides);
                }

                if (rerolls == _caps.MaxRerollsPerDie)
                {
                    // "Reroll until it stops matching" ends on a uniformly random non-matching face. Drawing that face
                    // directly gives the same distribution and bounds the work for rerolls that match almost every face.
                    return DrawNonMatching(group.Sides, reroll.Condition);
                }

                face = Draw(group.Sides);
            }

            return face;
        }

        private int DrawNonMatching(int sides, DiceCondition condition)
        {
            var allowed = 0;
            for (var face = 1; face <= sides; face++)
            {
                allowed += condition.Matches(face) ? 0 : 1;
            }

            var pick = Draw(allowed);
            for (var face = 1; face <= sides; face++)
            {
                if (!condition.Matches(face) && --pick == 0)
                {
                    return face;
                }
            }

            throw new InvalidOperationException("No non-matching face; the parser should have refused this reroll.");
        }

        private int Draw(int sides)
        {
            if (++_budget.PhysicalRolls > _budget.MaxPhysicalRolls)
            {
                throw new DndInputException(string.Create(CultureInfo.InvariantCulture,
                    $"Rolling \"{_expression.Text}\" needed more than {_budget.MaxPhysicalRolls} individual dice (explosions and rerolls included). Roll fewer dice, or fewer times."));
            }

            var face = _roller.Roll(sides);
            if (face < 1 || face > sides)
            {
                throw new InvalidOperationException($"{_roller.Source} returned {face} for a d{sides}.");
            }

            return face;
        }

        private void AddFace(int face, bool rerolled, bool exploded)
        {
            if (_record)
            {
                _faces.Add(new RolledFace(face, rerolled, exploded));
            }
        }

        /// <summary>
        /// Marks the dice keep/drop removes. Ties keep the earlier die, so the dropped one is the later of two equal
        /// faces; the total is the same either way, only the display depends on it.
        /// </summary>
        private void MarkDropped(DiceGroup group)
        {
            if (group.Keep is not { } keep)
            {
                return;
            }

            // Measured against the pool actually rolled: with ! or !p it is larger than the dice typed.
            var kept = (int)keep.KeptOf(_pool.Count);
            if (kept >= _pool.Count)
            {
                return;
            }

            if (_order.Length < _pool.Count)
            {
                _order = new int[Math.Max(_pool.Count, _order.Length * 2)];
            }

            var order = _order.AsSpan(0, _pool.Count);
            for (var i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            var pool = _pool;
            order.Sort((a, b) =>
            {
                var byValue = keep.Highest ? pool[b].Value.CompareTo(pool[a].Value) : pool[a].Value.CompareTo(pool[b].Value);
                return byValue != 0 ? byValue : a.CompareTo(b);
            });

            for (var i = kept; i < order.Length; i++)
            {
                _pool[order[i]] = _pool[order[i]] with { Dropped = true };
            }
        }
    }

    private readonly record struct PoolEntry(long Raw, long Value, int Score, bool Exploded, bool Penetrated, int FaceStart, int FaceCount)
    {
        public bool Dropped { get; init; }
    }
}
