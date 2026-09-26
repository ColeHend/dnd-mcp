using System.Globalization;

namespace DndMcp.Domain.Dice;

public enum DiceComparison
{
    Equal,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

/// <summary>A comparison point: the trailing "&gt;=30" of a query, or the "&lt;3" of <c>r&lt;3</c>.</summary>
public readonly record struct DiceCondition(DiceComparison Comparison, long Target)
{
    public bool Matches(long value) => Comparison switch
    {
        DiceComparison.Equal => value == Target,
        DiceComparison.Less => value < Target,
        DiceComparison.LessOrEqual => value <= Target,
        DiceComparison.Greater => value > Target,
        DiceComparison.GreaterOrEqual => value >= Target,
        _ => throw new ArgumentOutOfRangeException(nameof(Comparison), Comparison, null),
    };

    /// <summary>Some whole number in [<paramref name="low"/>, <paramref name="high"/>] matches.</summary>
    public bool MatchesAnyIn(long low, long high) => low <= high && Comparison switch
    {
        DiceComparison.Equal => low <= Target && Target <= high,
        DiceComparison.Less => low < Target,
        DiceComparison.LessOrEqual => low <= Target,
        DiceComparison.Greater => high > Target,
        DiceComparison.GreaterOrEqual => high >= Target,
        _ => throw new ArgumentOutOfRangeException(nameof(Comparison), Comparison, null),
    };

    /// <summary>Every whole number in [<paramref name="low"/>, <paramref name="high"/>] matches.</summary>
    public bool MatchesAllIn(long low, long high) => low <= high && Matches(low) && Matches(high) &&
        (Comparison != DiceComparison.Equal || low == high);

    /// <summary>ASCII, as typed: <c>&gt;=</c>.</summary>
    public string Operator => Comparison switch
    {
        DiceComparison.Equal => "=",
        DiceComparison.Less => "<",
        DiceComparison.LessOrEqual => "<=",
        DiceComparison.Greater => ">",
        DiceComparison.GreaterOrEqual => ">=",
        _ => throw new ArgumentOutOfRangeException(nameof(Comparison), Comparison, null),
    };

    /// <summary>For prose: <c>≥</c>.</summary>
    public string Symbol => Comparison switch
    {
        DiceComparison.LessOrEqual => "≤",
        DiceComparison.GreaterOrEqual => "≥",
        _ => Operator,
    };

    public override string ToString() => Operator + Target.ToString(CultureInfo.InvariantCulture);
}

public enum ExplodeKind
{
    /// <summary><c>!</c>: each explosion adds a separate die to the pool.</summary>
    Standard,

    /// <summary><c>!!</c>: explosions add into the same die, so the pool size never changes.</summary>
    Compound,

    /// <summary><c>!p</c>: like <c>!</c>, but every die added by an explosion counts one less.</summary>
    Penetrating,
}

/// <summary>
/// Which dice of a pool count: the highest ones (<see cref="Highest"/>) or the lowest. Without <see cref="Drop"/>,
/// <see cref="Count"/> dice are kept (kh/kl); with it, all but <see cref="Count"/> are (dl keeps the highest, dh the
/// lowest).
/// </summary>
/// <remarks>
/// Drops stay drops only for exploding pools (<c>!</c>, <c>!p</c>), whose size is unknown until rolled: 4d6!dl1 must
/// drop one die of however many were rolled. The parser turns drops on fixed pools into keeps (4d6dl1 is 4d6kh3).
/// </remarks>
public sealed record KeepRule(bool Highest, long Count, bool Drop = false)
{
    /// <summary>How many dice of a pool of <paramref name="poolSize"/> are kept.</summary>
    public long KeptOf(long poolSize) => Drop ? Math.Max(0, poolSize - Count) : Math.Min(Count, poolSize);
}

/// <summary><c>r</c> rerolls until the face stops matching; <c>ro</c> (<see cref="Once"/>) rerolls one time.</summary>
public sealed record RerollRule(DiceCondition Condition, bool Once);

public sealed record ExplodeRule(DiceCondition Condition, ExplodeKind Kind);

/// <summary>
/// One <c>NdM…</c> term with its modifiers, already validated.
///
/// <para>
/// The modifiers are applied in a fixed order whatever order they were typed in: per die, roll → reroll → explode →
/// clamp (<see cref="Min"/>/<see cref="Max"/>); then, on the pool, keep/drop → count successes (when
/// <see cref="Success"/> is set) or sum. Rolling (<see cref="DiceEvaluator"/>) and the exact distributions
/// (<see cref="DiceDistribution"/>) both follow this order; <c>DiceSemanticsAgreementTests</c> pins that they agree.
/// </para>
/// </summary>
public sealed record DiceGroup(
    int Count,
    int Sides,
    KeepRule? Keep,
    RerollRule? Reroll,
    ExplodeRule? Explode,
    int? Min,
    int? Max,
    DiceCondition? Success,
    DiceCondition? Failure,
    string? Label,
    string Text)
{
    /// <summary>The group's result is a success count, not a sum.</summary>
    public bool CountsSuccesses => Success is not null;

    /// <summary>The value one die contributes after clamping.</summary>
    public long Clamp(long value) => Math.Min(Math.Max(value, Min ?? long.MinValue), Max ?? long.MaxValue);

    /// <summary>+1 for a success, −1 for a failure, 0 otherwise (count mode only).</summary>
    public int Score(long value) =>
        (Success is { } success && success.Matches(value) ? 1 : 0) - (Failure is { } failure && failure.Matches(value) ? 1 : 0);
}

/// <summary>A node of a parsed dice expression. Evaluated left to right, depth first.</summary>
public abstract record DiceNode;

public sealed record ConstantNode(long Value, string? Label) : DiceNode;

public sealed record DiceGroupNode(DiceGroup Group) : DiceNode;

public sealed record NegateNode(DiceNode Operand) : DiceNode;

public sealed record SumNode(DiceNode Left, DiceNode Right, bool Subtract) : DiceNode;

/// <summary><c>* Factor</c>, or <c>/ Factor</c> rounding down (towards −∞).</summary>
public sealed record ScaleNode(DiceNode Operand, long Factor, bool Divide) : DiceNode;

/// <summary>Parentheses. No effect on the value; kept so results can be shown the way they were typed.</summary>
public sealed record GroupingNode(DiceNode Inner) : DiceNode;

/// <summary>
/// Floor division. C#'s <c>/</c> truncates towards zero, which would make <c>(1d4-3)/2</c> give 0 for −1 where the
/// grammar promises −1.
/// </summary>
public static class DiceMath
{
    public static long FloorDivide(long value, long divisor)
    {
        var quotient = value / divisor;
        return (value % divisor != 0 && (value < 0) != (divisor < 0)) ? quotient - 1 : quotient;
    }
}
