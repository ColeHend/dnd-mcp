using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: the parser reads every form the tool descriptions advertise into the intended tree, and refuses
/// everything else with a <see cref="DndInputException"/> that quotes the input and says what would be accepted —
/// never a silent misreading such as "2d6 3" becoming 2d63, "2 d6" becoming 2d6, or "8d6!&gt;=30" quietly exploding
/// on a face no d6 has.
/// </summary>
public sealed class DiceExpressionTests
{
    [Theory]
    [InlineData("2d6+3", "(2d6 + 3)")]
    [InlineData("2d6 + 3", "(2d6 + 3)")]
    [InlineData("2d6\t+\t3", "(2d6 + 3)")]
    [InlineData("  1d8  ", "1d8")]
    [InlineData("d20", "1d20")]
    [InlineData("D20", "1d20")]
    [InlineData("d%", "1d100")]
    [InlineData("-1d4", "neg(1d4)")]
    [InlineData("+2d6", "2d6")]
    [InlineData("1d20-1", "(1d20 - 1)")]
    [InlineData("1d8-1d4", "(1d8 - 1d4)")]
    [InlineData("2d6+1d4+3", "((2d6 + 1d4) + 3)")]
    [InlineData("5", "5")]
    [InlineData("5-2", "(5 - 2)")]
    [InlineData("2d6*2", "(2d6 * 2)")]
    [InlineData("(1d8+3)/2", "({(1d8 + 3)} / 2)")]
    [InlineData("1d6*2/3", "((1d6 * 2) / 3)")]
    [InlineData("2*3+1", "((2 * 3) + 1)")]
    [InlineData("-(1d4+1)", "neg({(1d4 + 1)})")]
    [InlineData("( 1d4 )", "{1d4}")]
    public void Parse_Arithmetic_BuildsTreeWithSignsKept(string expression, string tree)
    {
        Assert.Equal(tree, Tree(expression));
    }

    [Theory]
    [InlineData("4d6kh3", "4d6 kh3")]
    [InlineData("4d6k3", "4d6 kh3")]
    [InlineData("4d6KH3", "4d6 kh3")]
    [InlineData("2d20kh", "2d20 kh1")]
    [InlineData("2d20kl1", "2d20 kl1")]
    [InlineData("4d6dl1", "4d6 kh3")]
    [InlineData("4d6dl", "4d6 kh3")]
    [InlineData("5d8dh2", "5d8 kl3")]
    [InlineData("4d6kh4", "4d6")]
    [InlineData("adv", "2d20 kh1")]
    [InlineData("dis", "2d20 kl1")]
    [InlineData("ea", "3d20 kh1")]
    [InlineData("ADV", "2d20 kh1")]
    [InlineData("1d20r1", "1d20 r=1")]
    [InlineData("2d6r<3", "2d6 r<3")]
    [InlineData("2d6ro<=2", "2d6 ro<=2")]
    [InlineData("1d6!", "1d6 !=6")]
    [InlineData("1d6!>=5", "1d6 !>=5")]
    [InlineData("1d6!5", "1d6 !=5")]
    [InlineData("1d6!!", "1d6 !!=6")]
    [InlineData("1d6!p", "1d6 !p=6")]
    [InlineData("1d20min10", "1d20 min10")]
    [InlineData("1d6max4", "1d6 max4")]
    [InlineData("10d10cs>=8", "10d10 cs>=8")]
    [InlineData("10d10cs>=8cf=1", "10d10 cs>=8 cf=1")]
    [InlineData("10d10cf=1cs>=8", "10d10 cs>=8 cf=1")]
    [InlineData("4d6r1kh3", "4d6 kh3 r=1")]
    [InlineData("3d6!!kh2min2", "3d6 kh2 !!=6 min2")]
    [InlineData("4d6!dl1", "4d6 dl1 !=6")]
    [InlineData("4d6dl1!", "4d6 dl1 !=6")]
    [InlineData("5d6!pdh2", "5d6 dh2 !p=6")]
    [InlineData("3d6!kh3", "3d6 kh3 !=6")]
    [InlineData("2d6!kh3", "2d6 kh3 !=6")]
    [InlineData("3d6!!kh3", "3d6 !!=6")]
    [InlineData("4d6!!dl1", "4d6 kh3 !!=6")]
    [InlineData("1d6ro<6!", "1d6 ro<6 !=6")]
    [InlineData("1d6r<5!", "1d6 r<5 !=6")]
    [InlineData("2d6[fire]", "2d6 [fire]")]
    [InlineData("2d6 [Fire Damage]", "2d6 [Fire Damage]")]
    [InlineData("adv[attack]", "2d20 kh1 [attack]")]
    [InlineData("1d6[Hunter's Mark]", "1d6 [Hunter's Mark]")]
    [InlineData("1d8[off-hand 2nd]", "1d8 [off-hand 2nd]")]
    [InlineData("5[str]", "5[str]")]
    public void Parse_DiceWithModifiers_NormalisesModifiers(string expression, string tree)
    {
        Assert.Equal(tree, Tree(expression));
    }

    [Theory]
    [InlineData("8d6>=30", ">=30", "8d6")]
    [InlineData("8d6 >= 30", ">=30", "8d6")]
    [InlineData("1d20+5>=15", ">=15", "(1d20 + 5)")]
    [InlineData("adv+5 > 14", ">14", "(2d20 kh1 + 5)")]
    [InlineData("1d4-5<=-2", "<=-2", "(1d4 - 5)")]
    [InlineData("2d6=7", "=7", "2d6")]
    [InlineData("1d6! >= 5", ">=5", "1d6 !=6")]
    [InlineData("10d10cs>=8>=3", ">=3", "10d10 cs>=8")]
    public void Parse_TrailingComparison_IsTheQueryNotAModifier(string expression, string comparison, string tree)
    {
        var parsed = DiceExpression.Parse(expression);

        Assert.Equal(comparison, parsed.Comparison.ToString());
        Assert.Equal(tree, Tree(parsed.Root));
    }

    [Theory]
    [InlineData("8d6>=30", "8d6")]
    [InlineData(" 1d20 + 5 >= 15 ", "1d20 + 5")]
    [InlineData("10d10cs>=8>=3", "10d10cs>=8")]
    [InlineData("2d6+3", "2d6+3")]
    public void Parse_TotalText_IsTheInputBeforeTheComparison(string expression, string totalText)
    {
        Assert.Equal(totalText, DiceExpression.Parse(expression).TotalText);
    }

    [Fact]
    public void Parse_Groups_AreListedInEvaluationOrder()
    {
        var parsed = DiceExpression.Parse("1d20+adv-2d6[fire]");

        Assert.Equal(["1d20", "adv", "2d6"], parsed.Groups.Select(g => g.Text));
        Assert.Equal("fire", parsed.Groups[2].Label);
    }

    [Fact]
    public void Parse_Text_IsTheTrimmedInputAsTyped()
    {
        Assert.Equal("2D6 + 3", DiceExpression.Parse("  2D6 + 3 ").Text);
    }

    [Theory]
    [InlineData("2d6 3", "terms must be joined with + or -")]
    [InlineData("1d6 1d6", "terms must be joined with + or -")]
    [InlineData("2 d6", "dice are written without spaces")]
    [InlineData("4d6 kh3", "modifiers must follow the dice without a space")]
    [InlineData("1d6 !", "modifiers must follow the dice without a space")]
    [InlineData("2d6d4", "two dice terms must be joined with + or -")]
    [InlineData("2d64d6", "")]
    [InlineData("banana", "expected a number")]
    [InlineData("2x6", "terms must be joined with + or -")]
    [InlineData("d", "expected a number")]
    [InlineData("1d", "\"d\" must be followed by the number of sides")]
    [InlineData("2d6+", "the expression ends where")]
    [InlineData("+", "the expression ends where")]
    [InlineData("2d6++3", "two signs in a row")]
    [InlineData("2d6+-3", "two signs in a row")]
    [InlineData("(1d6", "a \")\" is missing")]
    [InlineData("1d6)", "without a matching \"(\"")]
    [InlineData("2d6*1d4", "must be followed by a whole number")]
    [InlineData("1d6x", "unknown modifier")]
    [InlineData("1d20>=", "a number must follow")]
    [InlineData("1d20>=5>=3", "only one comparison")]
    [InlineData("advkh1", "expected a number")]
    [InlineData("adv!", "adv takes no modifiers")]
    [InlineData("10d10cs8", "\"cs\" needs a comparison")]
    [InlineData("1d6r", "\"r\" must be followed by a face number or a comparison")]
    [InlineData("2d6[fire", "has no closing")]
    [InlineData("4d6kh-1", "\"kh\" must be followed by how many dice, not a sign")]
    [InlineData("4d6dl+1", "\"dl\" must be followed by how many dice, not a sign")]
    [InlineData("4d6k-1", "\"k\" must be followed by how many dice, not a sign")]
    [InlineData("2d6\n+3", "one line")]
    public void Parse_Malformed_ThrowsWithReason(string expression, string reason)
    {
        var ex = Assert.Throws<DndInputException>(() => DiceExpression.Parse(expression));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_SaysEmptyAndGivesExamples(string expression)
    {
        var ex = Assert.Throws<DndInputException>(() => DiceExpression.Parse(expression));

        Assert.StartsWith("The dice expression is empty.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"4d6kh3\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Juxtaposed_MessageQuotesInputAndShowsHowToJoin()
    {
        // Stripping all whitespace would read this as 2d63. The message is what the model uses to fix its call.
        var ex = Assert.Throws<DndInputException>(() => DiceExpression.Parse("2d6 3"));

        Assert.Equal(
            "Could not read the dice expression \"2d6 3\" at \"3\": terms must be joined with + or -. E.g. \"2d6 + 3\".",
            ex.Message);
    }

    [Theory]
    [InlineData("0d6", "needs at least one die")]
    [InlineData("1d0", "needs at least one side")]
    [InlineData("1001d6", "rolls 1001 dice; the limit is 1000")]
    [InlineData("99999999999d6", "the limit is 1000")]
    [InlineData("1d1001", "has 1001 sides; the limit is 1000")]
    [InlineData("600d6+401d4", "1001 dice in total; the limit is 1000")]
    [InlineData("500d20+adv+499d6", "1001 dice in total")]
    [InlineData("1000001", "numbers go up to 1000000")]
    [InlineData("2d6*0", "the multiplier must be at least 1")]
    [InlineData("2d6/0", "the divisor must be at least 1")]
    [InlineData("4d6kh5", "keeps 5 of only 4 dice")]
    [InlineData("4d6kh0", "keeps no dice")]
    [InlineData("4d6dl4", "drops every die")]
    [InlineData("4d6dl0", "drops no dice")]
    [InlineData("4d6kh3kl1", "more than one keep/drop modifier")]
    [InlineData("1d6r1ro2", "more than one reroll modifier")]
    [InlineData("1d6!!!", "more than one explode modifier")]
    [InlineData("1d6min2min3", "more than one min")]
    [InlineData("1d6cs>4cs>5", "more than one cs")]
    [InlineData("1d6min5max2", "has min 5 above max 2")]
    [InlineData("5d6cf=1", "counts failures (cf) without successes (cs)")]
    [InlineData("1d6r<=6", "rerolls every face of a d6, so it would never finish")]
    [InlineData("1d6ro>0", "rerolls every face of a d6")]
    [InlineData("1d6r7", "never rerolls: no face of a d6 is =7")]
    [InlineData("1d6!>=1", "explodes on every face of a d6, so it would never finish")]
    [InlineData("1d1!", "explodes on every face of a d1")]
    [InlineData("1d6r<6!", "explodes on every face of a d6 left after r<6, so it would never finish")]
    [InlineData("1d6!r<6", "explodes on every face of a d6 left after r<6")]
    [InlineData("1d6r6!", "never explodes: no face of a d6 left after r=6 is =6")]
    [InlineData("1d1000r<990!>=990", "explodes on every face of a d1000 left after r<990")]
    [InlineData("4d6!dl4", "drops every die unless one explodes")]
    [InlineData("1d6!=7", "never explodes")]
    [InlineData("2d6[]", "labels must be 1-40 characters")]
    [InlineData("1d20[+10]", "labels must be 1-40 characters of letters, digits")]
    [InlineData("1d20[= 20]+2", "labels must be 1-40 characters of letters, digits")]
    [InlineData("1d20[a_b]", "labels must be 1-40 characters of letters, digits")]
    [InlineData("2d6[a[b]", "labels must be 1-40 characters of letters, digits")]
    public void Parse_OutsideLimitsOrUnworkable_ThrowsNamingTheProblem(string expression, string reason)
    {
        var ex = Assert.Throws<DndInputException>(() => DiceExpression.Parse(expression));

        Assert.StartsWith($"The dice expression \"{expression}\" cannot be used: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("8d6!>=30", "8d6! >= 30")]
    [InlineData("8d6!>30", "8d6! > 30")]
    public void Parse_ExplodeTargetBeyondTheDie_HintsAtSpacingForATotalComparison(string expression, string spaced)
    {
        // "8d6!>=30" reads as "explode on 30+", which no d6 face is. The user almost certainly meant the query.
        var ex = Assert.Throws<DndInputException>(() => DiceExpression.Parse(expression));

        Assert.Contains($"leave a space before the comparison: \"{spaced}\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_CompoundMinAboveSides_IsAcceptedBecauseCompoundTotalsCanReachIt()
    {
        // A compounded d1000 reaches 101,000, so min1500 is meaningful; capping min/max at the die size would refuse it.
        Assert.Equal(1500, DiceExpression.Parse("1d1000!!min1500").Groups[0].Min);
    }

    [Theory]
    [InlineData("1000d6")]
    [InlineData("1d1000")]
    [InlineData("600d6+400d4")]
    [InlineData("1000000")]
    [InlineData("1000d1000!!")]
    public void Parse_AtLimit_IsAccepted(string expression)
    {
        Assert.NotNull(DiceExpression.Parse(expression).Root);
    }

    [Fact]
    public void Parse_ExpressionAtLengthLimit_IsAccepted()
    {
        var expression = "10" + string.Concat(Enumerable.Repeat("+1", 127));
        Assert.Equal(DiceLimits.MaxExpressionLength, expression.Length);

        Assert.Equal(137, DiceExpression.Parse(expression).MaxValue);
    }

    [Fact]
    public void Parse_ExpressionOverLengthLimit_ThrowsNamingTheLimit()
    {
        var expression = "10" + string.Concat(Enumerable.Repeat("+1", 128));

        var ex = Assert.Throws<DndInputException>(() => DiceExpression.Parse(expression));

        Assert.Contains($"the limit is {DiceLimits.MaxExpressionLength}", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1000000*1000000*1000000")]
    [InlineData("1000d1000!!*1000000")]
    [InlineData("-(1000000*1000000*2)")]
    public void Parse_StaticBoundsBeyondMagnitudeLimit_ThrowsInsteadOfOverflowingLater(string expression)
    {
        // Rolling and the distributions use unchecked long arithmetic because every value is bounded here first.
        var ex = Assert.Throws<DndInputException>(() => DiceExpression.Parse(expression));

        Assert.Contains("beyond the supported ±10^12", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2d6+3", 5, 15)]
    [InlineData("1d8-1d4", -3, 7)]
    [InlineData("(1d4-3)/2", -1, 0)]
    [InlineData("4d6kh3", 3, 18)]
    [InlineData("10d10cs>=8cf=1", -10, 10)]
    [InlineData("1d20min10", 10, 20)]
    [InlineData("1d6!", 1, 606)]
    [InlineData("3d6!pkl1", 0, 6)]
    [InlineData("3d6!kh3", 3, 18)]
    // 404 sixes with one dropped: 403 x 6.
    [InlineData("4d6!dl1", 3, 2418)]
    public void Parse_StaticBounds_ContainEveryPossibleTotal(string expression, long min, long max)
    {
        var parsed = DiceExpression.Parse(expression);

        Assert.True(parsed.MinValue <= min, $"MinValue {parsed.MinValue} excludes {min}.");
        Assert.True(parsed.MaxValue >= max, $"MaxValue {parsed.MaxValue} excludes {max}.");
    }

    [Theory]
    [InlineData(7, 2, 3)]
    [InlineData(-1, 2, -1)]
    [InlineData(-4, 2, -2)]
    [InlineData(-5, 3, -2)]
    [InlineData(0, 5, 0)]
    public void FloorDivide_NegativeValues_RoundsTowardsNegativeInfinity(long value, long divisor, long expected)
    {
        Assert.Equal(expected, DiceMath.FloorDivide(value, divisor));
    }

    private static string Tree(string expression) => Tree(DiceExpression.Parse(expression).Root);

    private static string Tree(DiceNode node) => node switch
    {
        ConstantNode c => c.Value.ToString(CultureInfo.InvariantCulture) + (c.Label is null ? "" : $"[{c.Label}]"),
        DiceGroupNode g => Group(g.Group),
        NegateNode n => $"neg({Tree(n.Operand)})",
        GroupingNode p => $"{{{Tree(p.Inner)}}}",
        SumNode s => $"({Tree(s.Left)} {(s.Subtract ? "-" : "+")} {Tree(s.Right)})",
        ScaleNode m => $"({Tree(m.Operand)} {(m.Divide ? "/" : "*")} {m.Factor})",
        _ => throw new InvalidOperationException(),
    };

    private static string Group(DiceGroup g)
    {
        var parts = new List<string> { $"{g.Count}d{g.Sides}" };
        if (g.Keep is { } keep)
        {
            parts.Add(keep.Drop ? $"{(keep.Highest ? "dl" : "dh")}{keep.Count}" : $"{(keep.Highest ? "kh" : "kl")}{keep.Count}");
        }

        if (g.Reroll is { } reroll)
        {
            parts.Add($"{(reroll.Once ? "ro" : "r")}{reroll.Condition}");
        }

        if (g.Explode is { } explode)
        {
            parts.Add((explode.Kind switch { ExplodeKind.Compound => "!!", ExplodeKind.Penetrating => "!p", _ => "!" }) + explode.Condition);
        }

        if (g.Min is { } min)
        {
            parts.Add($"min{min}");
        }

        if (g.Max is { } max)
        {
            parts.Add($"max{max}");
        }

        if (g.Success is { } success)
        {
            parts.Add($"cs{success}");
        }

        if (g.Failure is { } failure)
        {
            parts.Add($"cf{failure}");
        }

        if (g.Label is { } label)
        {
            parts.Add($"[{label}]");
        }

        return string.Join(" ", parts);
    }
}
