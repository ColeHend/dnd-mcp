using System.Numerics;
using DndMcp.Domain.Dice;

namespace DndMcp.Tests.Dice;

/// <summary>
/// The oracle for every exact distribution: runs the real <see cref="DiceEvaluator"/> once per possible sequence of
/// die faces and adds up each sequence's probability (the product of 1/sides over its rolls).
///
/// <para>
/// It knows nothing about keep/drop, rerolls or explosions — it only replays faces — so agreement with
/// <see cref="DiceDistribution"/> means the closed-form maths and the rolling code mean the same thing. Paths are
/// walked like an odometer: the faces asked for at position i depend only on the faces before it, so bumping the
/// last position that is not at its maximum and discarding everything after it visits every path exactly once.
/// Explosions and rerolls make paths unbounded, so callers lower <see cref="DiceCaps"/> to keep the count small.
/// </para>
/// </summary>
public static class RollPathEnumerator
{
    public static Dictionary<long, Fraction> Enumerate(string expression, DiceCaps caps, int maxPaths = 2_000_000)
    {
        var parsed = DiceExpression.Parse(expression);
        var totals = new Dictionary<long, (BigInteger Numerator, BigInteger Denominator)>();
        var path = new List<(int Sides, int Face)>();
        var paths = 0;

        while (true)
        {
            var roller = new ReplayRoller(path);
            var roll = DiceEvaluator.Roll(parsed, roller, new DiceRollBudget(long.MaxValue), caps);
            if (roller.Used != path.Count)
            {
                throw new InvalidOperationException("A path was not fully consumed; evaluation is not a function of its faces.");
            }

            var denominator = BigInteger.One;
            foreach (var (sides, _) in path)
            {
                denominator *= sides;
            }

            // n/d + 1/D over lcm(d, D): denominators are products of die sizes, so the lcm stays small where d·D would
            // grow with every path.
            if (totals.TryGetValue(roll.Total, out var sum))
            {
                var lcm = sum.Denominator / BigInteger.GreatestCommonDivisor(sum.Denominator, denominator) * denominator;
                totals[roll.Total] = ((sum.Numerator * (lcm / sum.Denominator)) + (lcm / denominator), lcm);
            }
            else
            {
                totals[roll.Total] = (BigInteger.One, denominator);
            }

            if (++paths > maxPaths)
            {
                throw new InvalidOperationException($"More than {maxPaths} roll paths; lower the caps or the dice.");
            }

            var position = path.Count - 1;
            while (position >= 0 && path[position].Face == path[position].Sides)
            {
                position--;
            }

            if (position < 0)
            {
                return totals.ToDictionary(t => t.Key, t => Fraction.Create(t.Value.Numerator, t.Value.Denominator));
            }

            path[position] = (path[position].Sides, path[position].Face + 1);
            path.RemoveRange(position + 1, path.Count - position - 1);
        }
    }

    /// <summary>Replays the path, and extends it with face 1 whenever the evaluator asks for a roll past its end.</summary>
    private sealed class ReplayRoller : IDiceRoller
    {
        private readonly List<(int Sides, int Face)> _path;

        public ReplayRoller(List<(int Sides, int Face)> path)
        {
            _path = path;
        }

        public int Used { get; private set; }

        public string Source => "enumerated (test oracle)";

        public int Roll(int sides)
        {
            if (Used == _path.Count)
            {
                _path.Add((sides, 1));
            }
            else if (_path[Used].Sides != sides)
            {
                throw new InvalidOperationException("The same prefix asked for a different die.");
            }

            return _path[Used++].Face;
        }
    }
}
