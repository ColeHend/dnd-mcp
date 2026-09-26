namespace DndMcp.Domain.Dice;

/// <summary>
/// Every bound the dice grammar enforces, in one place so the parser's messages, the tool descriptions and the tests
/// quote the same numbers.
///
/// <para>
/// Together they keep every value inside <see cref="MaxMagnitude"/>, so rolling and the exact distributions can use
/// plain <c>long</c> arithmetic without overflow checks: an expression whose static bounds exceed it is refused at
/// parse time instead of wrapping at roll time (the Phase 0 stub reported a wrapped negative total as a real roll).
/// </para>
/// </summary>
public static class DiceLimits
{
    /// <summary>Also bounds the expression echoed back on every result.</summary>
    public const int MaxExpressionLength = 256;

    /// <summary>Dice in one group, and in the whole expression (adv counts 2, ea counts 3).</summary>
    public const int MaxDice = 1000;

    public const int MaxSides = 1000;

    public const long MaxConstant = 1_000_000;

    /// <summary>For <c>* n</c> and <c>/ n</c>.</summary>
    public const long MaxFactor = 1_000_000;

    public const int MaxLabelLength = 40;

    /// <summary>
    /// Extra dice one die may add by exploding. Rolling stops there (the last die does not explode), and the exact
    /// distributions model exactly that cap, so dice_odds describes what dice_roll does.
    /// </summary>
    public const int MaxExplosionsPerDie = 100;

    /// <summary>
    /// Physical rerolls of one die under <c>r</c> before the roller draws the result directly from the faces that do
    /// not match. The final face has the same distribution either way; the cap only bounds the work (and the faces
    /// shown) for rerolls that match almost every face, such as d1000r&lt;999.
    /// </summary>
    public const int MaxRerollsPerDie = 100;

    /// <summary>
    /// Individual die rolls one dice_roll call may make, explosions and rerolls included. Only pathological
    /// expressions reach it (1000d1000!&gt;=2 rolled 100 times); it keeps a call from running for minutes.
    /// </summary>
    public const int MaxPhysicalRollsPerCall = 1_000_000;

    /// <summary>The largest |value| any sub-expression may reach, checked statically at parse time.</summary>
    public const long MaxMagnitude = 1_000_000_000_000;
}
