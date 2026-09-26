using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: a <c>dice_roll</c> result through the real client shows every die, and its total can be recomputed from
/// what it shows — counted faces summed per group, struck-through (dropped) faces ignored, the expression's operators
/// applied — with the randomness source on the last line. A user can audit any roll, and a seeded or constant roller
/// wired in by mistake is caught.
///
/// <para>
/// <see cref="Audit"/> recomputes the total from the text alone, knowing only the display notation (not the dice
/// rules), so it pins the renderer and the evaluator against each other through everything in between: argument
/// binding, the shared JSON options, and the call-tool filter.
/// </para>
/// </summary>
public sealed partial class DiceToolTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public DiceToolTests(McpServerHarness server)
    {
        _server = server;
    }

    // "**12**  (2d6+3 → [6, 3] + 3)", optionally followed by " · ≥ 15? **yes**".
    [GeneratedRegex(@"^\*\*(?<total>-?\d+)\*\*  \((?<expr>.+?) → (?<breakdown>.+)\)(?: · (?<cmp>[<>=≤≥]+) (?<target>-?\d+)\? \*\*(?<verdict>yes|no)\*\*)?$")]
    private static partial Regex SingleRollRegex();

    // "#2: **12**  ([6, 3] + 3)"; the breakdown is left out when the output budget runs short.
    [GeneratedRegex(@"^#(?<n>\d+): \*\*(?<total>-?\d+)\*\*(?:  \((?<breakdown>.+)\))?(?: · .+)?$")]
    private static partial Regex NumberedRollRegex();

    // A label, shown in italics after its term.
    [GeneratedRegex(@" _[^_]+_")]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"\[(?<faces>[^\[\]]*)\]")]
    private static partial Regex GroupRegex();

    private static string RandomnessLine => $"_Randomness: {new CryptoDiceRoller().Source}._";

    [Theory]
    [InlineData("2d6+3", 5, 15)]
    [InlineData("1d20+5", 6, 25)]
    [InlineData("d20", 1, 20)]
    [InlineData("1d20-1", 0, 19)]
    [InlineData("8d6", 8, 48)]
    [InlineData("2d6 + 3", 5, 15)]
    [InlineData("-1d4", -4, -1)]
    [InlineData("1d8-1d4", -3, 7)]
    [InlineData("4d6kh3", 3, 18)]
    [InlineData("4d6dl1+1d4", 4, 22)]
    [InlineData("adv+5", 6, 25)]
    [InlineData("dis-1", 0, 19)]
    [InlineData("ea", 1, 20)]
    [InlineData("2d6ro<=2+5", 7, 17)]
    [InlineData("4d4r1", 8, 16)]
    [InlineData("1d20min10+7", 17, 27)]
    [InlineData("3d6max4", 3, 12)]
    [InlineData("(1d4+2)*3", 9, 18)]
    [InlineData("(1d6+1)/2", 1, 3)]
    [InlineData("10d10cs>=8cf=1", -10, 10)]
    [InlineData("-1d4/2", -2, 0)]
    [InlineData("-(1d6)*2", -12, -2)]
    [InlineData("2d6[fire]+1d6[radiant]+5[str]", 8, 23)]
    [InlineData("3d6!kh3", 3, 18 * 101)]
    [InlineData("4d6!dl1", 3, 24 * 101)]
    public async Task CallTool_Expression_TotalIsRecomputableFromTheDiceShown(string expression, long min, long max)
    {
        var lines = await RollLinesAsync(new() { ["expression"] = expression });

        var roll = SingleRollRegex().Match(lines[0]);
        Assert.True(roll.Success, $"Result line not in the expected shape: {lines[0]}");
        Assert.Equal(DiceExpression.Parse(expression).TotalText, roll.Groups["expr"].Value);

        var total = long.Parse(roll.Groups["total"].Value, CultureInfo.InvariantCulture);
        Assert.Equal(total, Audit(roll.Groups["breakdown"].Value));
        Assert.InRange(total, min, max);
        Assert.Equal(RandomnessLine, lines[^1]);
    }

    [Theory]
    [InlineData("1d6!+1")]
    [InlineData("3d6!!")]
    [InlineData("2d6!p")]
    [InlineData("2d4!>=3")]
    public async Task CallTool_ExplodingDice_TotalIsRecomputable(string expression)
    {
        // Explosions are rare on any one roll, so roll many times; every line must still add up.
        var lines = await RollLinesAsync(new() { ["expression"] = expression, ["times"] = 100 });

        foreach (var line in lines.Where(l => l.StartsWith('#')))
        {
            var roll = NumberedRollRegex().Match(line);
            Assert.True(roll.Success, $"Result line not in the expected shape: {line}");
            Assert.Equal(long.Parse(roll.Groups["total"].Value, CultureInfo.InvariantCulture), Audit(roll.Groups["breakdown"].Value));
        }
    }

    [Theory]
    [InlineData("8d6", 8, 6)]
    [InlineData("1d20", 1, 20)]
    [InlineData("3d1000", 3, 1000)]
    [InlineData("d%", 1, 100)]
    public async Task CallTool_PlainDice_ShowsOneFacePerDieWithinTheDie(string expression, int dice, int sides)
    {
        var lines = await RollLinesAsync(new() { ["expression"] = expression });

        var faces = Faces(SingleRollRegex().Match(lines[0]).Groups["breakdown"].Value);
        Assert.Equal(dice, faces.Count);
        Assert.All(faces, f => Assert.InRange(long.Parse(f, CultureInfo.InvariantCulture), 1, sides));
    }

    [Fact]
    public async Task CallTool_KeepHighest_StrikesExactlyTheDroppedDieAndExplainsIt()
    {
        var lines = await RollLinesAsync(new() { ["expression"] = "4d6kh3" });

        var faces = Faces(SingleRollRegex().Match(lines[0]).Groups["breakdown"].Value);
        Assert.Equal(4, faces.Count);
        Assert.Single(faces, f => f.StartsWith("~~", StringComparison.Ordinal));
        Assert.Contains("~~struck~~ = dropped", lines[^2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TimesThree_ReturnsHeaderThenThreeNumberedRollsThenSource()
    {
        var lines = await RollLinesAsync(new() { ["expression"] = "2d6+3", ["times"] = 3 });

        Assert.Equal(5, lines.Length);
        Assert.Equal("2d6+3, 3 rolls:", lines[0]);
        for (var i = 1; i <= 3; i++)
        {
            var roll = NumberedRollRegex().Match(lines[i]);
            Assert.True(roll.Success, $"Result line not in the expected shape: {lines[i]}");
            Assert.Equal(i, int.Parse(roll.Groups["n"].Value, CultureInfo.InvariantCulture));
            Assert.Equal(long.Parse(roll.Groups["total"].Value, CultureInfo.InvariantCulture), Audit(roll.Groups["breakdown"].Value));
        }

        // The source is stated once for the batch, not per roll.
        Assert.Equal(RandomnessLine, lines[4]);
    }

    [Fact]
    public async Task CallTool_ManyRolls_AreNotAllIdentical()
    {
        // 100 rolls of 1d1000 are all equal with probability 1000^-99. A constant or stuck roller fails this; the
        // registered roller being the CSPRNG is pinned by the source line.
        var lines = await RollLinesAsync(new() { ["expression"] = "1d1000", ["times"] = 100 });

        var totals = lines.Select(l => NumberedRollRegex().Match(l)).Where(m => m.Success).Select(m => m.Groups["total"].Value).ToList();
        Assert.Equal(100, totals.Count);
        Assert.True(totals.Distinct().Count() > 1, "Every one of 100 rolls of 1d1000 came up the same.");
    }

    [Fact]
    public async Task CallTool_Label_PrefixesTheResult()
    {
        var lines = await RollLinesAsync(new() { ["expression"] = "1d20+5", ["label"] = "Stealth" });

        Assert.StartsWith("Stealth: **", lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CallTool_BlankLabel_IsIgnored(string label)
    {
        var lines = await RollLinesAsync(new() { ["expression"] = "1d20+5", ["label"] = label });

        Assert.StartsWith("**", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTools_DiceDescriptions_QuoteTheLimitsTheCodeEnforces()
    {
        // The descriptions are attribute text and cannot interpolate the constants; this keeps them from drifting apart.
        var tools = await _server.Client.ListToolsAsync();
        var roll = Assert.Single(tools, t => t.Name == "dice_roll").Description;
        var odds = Assert.Single(tools, t => t.Name == "dice_odds").Description;

        Assert.Contains($"Limits: {DiceLimits.MaxDice} dice and {DiceLimits.MaxSides} sides per expression, {DndMcp.Tools.DiceTools.MaxTimes} rolls per call.", roll, StringComparison.Ordinal);
        Assert.Contains($"at most {DndMcp.Formatting.DiceOddsMarkdown.MaxTableRows} possible totals", odds, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TermLabels_AppearAfterTheirDice()
    {
        var lines = await RollLinesAsync(new() { ["expression"] = "2d6[fire]+1d6[radiant]" });

        // Italic, so "[+10]"-like labels can never read as arithmetic (and the parser refuses such labels anyway).
        Assert.Matches(@"→ \[\d, \d\] _fire_ \+ \[\d\] _radiant_\)$", lines[0]);
    }

    [Theory]
    [InlineData("1d20+100>=50", "yes")]
    [InlineData("1d20>=50", "no")]
    public async Task CallTool_TrailingComparison_AnswersYesOrNo(string expression, string verdict)
    {
        var lines = await RollLinesAsync(new() { ["expression"] = expression });

        var roll = SingleRollRegex().Match(lines[0]);
        Assert.Equal(verdict, roll.Groups["verdict"].Value);
        Assert.Equal("≥", roll.Groups["cmp"].Value);
        Assert.Equal("50", roll.Groups["target"].Value);
    }

    [Fact]
    public async Task CallTool_TrailingComparisonRolledSeveralTimes_CountsHowManyMetIt()
    {
        var lines = await RollLinesAsync(new() { ["expression"] = "1d20>=50", ["times"] = 5 });

        Assert.Contains("≥ 50 on 0 of 5 rolls.", lines);
    }

    [Fact]
    public async Task CallTool_SameSeedTwice_GivesIdenticalResultsLabelledReproducible()
    {
        var arguments = new Dictionary<string, object?> { ["expression"] = "4d6kh3+adv+1d6!", ["times"] = 5, ["seed"] = 42L };

        var first = _server.SuccessText(await _server.Client.CallToolAsync("dice_roll", arguments));
        var second = _server.SuccessText(await _server.Client.CallToolAsync("dice_roll", arguments));

        Assert.Equal(first, second);
        Assert.EndsWith("_Randomness: pseudo-random, reproducible (xoshiro256**, seed 42)._", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_DifferentSeeds_GiveDifferentRolls()
    {
        var first = await RollLinesAsync(new() { ["expression"] = "10d1000", ["seed"] = 1L });
        var second = await RollLinesAsync(new() { ["expression"] = "10d1000", ["seed"] = 2L });

        Assert.NotEqual(first[0], second[0]);
    }

    public static TheoryData<string, int> LargestRolls => new()
    {
        { "1000d1000", 100 },
        { "1000d6!", 100 },
        { "1000d6!!", 100 },
        { "1000d2r1", 100 },
        { "400d20kh200+300d8ro<=2+300d10cs>=6", 100 },
        { "1000d1000", 1 },
        // Many small groups: few dice, but every group and operator is text. Unbounded these were 135k characters.
        { "d6" + string.Concat(Enumerable.Repeat("+d6", 84)), 100 },
        { "d%" + string.Concat(Enumerable.Repeat("+d%", 84)), 100 },
        { "9" + string.Concat(Enumerable.Repeat("+9", 127)), 100 },
        // Long tokens: every die is a rerolled, compounded chain.
        { "100d1000r<990!!>=991", 1 },
    };

    [Theory]
    [MemberData(nameof(LargestRolls))]
    public async Task CallTool_LargestRolls_StayUnderTheOutputCeilingAndRemainCheckable(string expression, int times)
    {
        // The ceiling is ~4k tokens (characters / 4), against Claude Code's 25k-token cap on a result. Dice past the
        // budget are summarised with their group's value, so every breakdown shown still adds up.
        var text = _server.SuccessText(await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = expression, ["times"] = times }));

        Assert.True(text.Length < 16_000, $"dice_roll returned {text.Length} characters for {expression} x {times}.");

        var audited = 0;
        foreach (var line in SplitLines(text).Where(l => l.StartsWith("**", StringComparison.Ordinal) || l.StartsWith('#')))
        {
            var match = times == 1 ? SingleRollRegex().Match(line) : NumberedRollRegex().Match(line);
            Assert.True(match.Success, $"Result line not in the expected shape: {line[..Math.Min(200, line.Length)]}");
            if (match.Groups["breakdown"].Success)
            {
                Assert.Equal(long.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture), Audit(match.Groups["breakdown"].Value));
                audited++;
            }
        }

        // Omitting a breakdown is the last resort, and the result must say it happened.
        Assert.True(audited > 0 || text.Contains("_Dice omitted for ", StringComparison.Ordinal), text[..Math.Min(500, text.Length)]);
    }

    [Fact]
    public async Task CallTool_ManySmallGroupsManyTimes_OmitsBreakdownsAndSaysSo()
    {
        var expression = "d6" + string.Concat(Enumerable.Repeat("+d6", 84));

        var text = _server.SuccessText(await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = expression, ["times"] = 100 }));

        Assert.Contains("_Dice omitted for 100 rolls to keep this result short; roll fewer times to see every die._", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^#100: \*\*\d+\*\*$", text);
    }

    [Fact]
    public async Task CallTool_ManySmallGroupsOnce_SummarisesGroupsRatherThanOmitting()
    {
        // One roll gets the whole budget: every group's dice fit, and nothing is omitted.
        var lines = await RollLinesAsync(new() { ["expression"] = "d6" + string.Concat(Enumerable.Repeat("+d6", 84)) });

        var roll = SingleRollRegex().Match(lines[0]);
        Assert.Equal(85, Faces(roll.Groups["breakdown"].Value).Count);
        Assert.Equal(long.Parse(roll.Groups["total"].Value, CultureInfo.InvariantCulture), Audit(roll.Groups["breakdown"].Value));
    }

    [Fact]
    public async Task CallTool_PathologicalExpression_FailsWithTheRollBudgetMessage()
    {
        // Explodes on 2+ of a d1000: ~101 rolls per die, 101,000 per roll, past the per-call budget within ten rolls.
        var result = await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = "1000d1000!>=2", ["times"] = 100 });

        Assert.Equal(
            $"An error occurred invoking 'dice_roll': Rolling \"1000d1000!>=2\" needed more than {DiceLimits.MaxPhysicalRollsPerCall} individual dice (explosions and rerolls included). Roll fewer dice, or fewer times.",
            _server.ErrorText(result));
    }

    private async Task<string[]> RollLinesAsync(Dictionary<string, object?> arguments) =>
        SplitLines(_server.SuccessText(await _server.Client.CallToolAsync("dice_roll", arguments)));

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

    private static List<string> Faces(string breakdown) =>
        GroupRegex().Matches(breakdown).SelectMany(m => m.Groups["faces"].Value.Split(", ")).ToList();

    /// <summary>
    /// Recomputes a roll's total from its breakdown using only the display notation: each "[…]" becomes the sum of its
    /// counted faces (or its stated "= n"), struck faces count nothing, and what remains is plain arithmetic with
    /// floor division.
    /// </summary>
    private static long Audit(string breakdown)
    {
        var arithmetic = GroupRegex().Replace(LabelRegex().Replace(breakdown, string.Empty), m => GroupValue(m.Groups["faces"].Value).ToString(CultureInfo.InvariantCulture));
        return new Arithmetic(arithmetic).Evaluate();
    }

    private static long GroupValue(string faces)
    {
        var stated = faces.LastIndexOf(" = ", StringComparison.Ordinal);
        if (stated >= 0)
        {
            return long.Parse(faces[(stated + 3)..], CultureInfo.InvariantCulture);
        }

        return faces.Split(", ").Where(f => !f.StartsWith("~~", StringComparison.Ordinal)).Sum(DieValue);
    }

    private static long DieValue(string token)
    {
        // Notation, most specific first: "3⇒10" (clamped), "6!+6!+2=14" (compound), "5-1" (penetrating), "1⟳5" (rerolled), "6!".
        if (token.Contains('⇒'))
        {
            return long.Parse(token[(token.LastIndexOf('⇒') + 1)..], CultureInfo.InvariantCulture);
        }

        if (token.Contains('='))
        {
            token = token[(token.LastIndexOf('=') + 1)..];
        }

        var penetrating = token.EndsWith("-1", StringComparison.Ordinal) && token.Length > 2;
        if (penetrating)
        {
            token = token[..^2];
        }

        token = token[(token.LastIndexOf('⟳') + 1)..].TrimEnd('!');
        return long.Parse(token, CultureInfo.InvariantCulture) - (penetrating ? 1 : 0);
    }

    /// <summary>+, -, * and floor / over whole numbers, with parentheses and unary minus — the renderer's operators.</summary>
    private sealed class Arithmetic
    {
        private readonly string _text;
        private int _pos;

        public Arithmetic(string text)
        {
            _text = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        }

        public long Evaluate()
        {
            var value = Sum();
            Assert.True(_pos == _text.Length, $"Unparsed breakdown text: {_text[_pos..]}");
            return value;
        }

        private long Sum()
        {
            var value = Term();
            while (_pos < _text.Length && _text[_pos] is '+' or '-')
            {
                var op = _text[_pos++];
                var right = Term();
                value = op == '+' ? value + right : value - right;
            }

            return value;
        }

        private long Term()
        {
            var value = Factor();
            while (_pos < _text.Length && _text[_pos] is '*' or '/')
            {
                var op = _text[_pos++];
                var right = Factor();
                value = op == '*' ? value * right : (long)Math.Floor((double)value / right);
            }

            return value;
        }

        private long Factor()
        {
            if (_text[_pos] == '-')
            {
                _pos++;
                return -Factor();
            }

            if (_text[_pos] == '(')
            {
                _pos++;
                var inner = Sum();
                _pos++; // ')'
                return inner;
            }

            var start = _pos;
            while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos]))
            {
                _pos++;
            }

            return long.Parse(_text[start.._pos], CultureInfo.InvariantCulture);
        }
    }
}
