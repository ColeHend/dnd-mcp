using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    internal static partial Regex SingleRollRegex();

    // "#2: **12**  ([6, 3] + 3)"; the breakdown is left out when the output budget runs short.
    [GeneratedRegex(@"^#(?<n>\d+): \*\*(?<total>-?\d+)\*\*(?:  \((?<breakdown>.+)\))?(?: · .+)?$")]
    internal static partial Regex NumberedRollRegex();

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
    internal static long Audit(string breakdown)
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

/// <summary>
/// Invariant: a <c>dice_roll</c> made while the resolved campaign has a live session is logged there, one
/// <c>dice_roll</c> row per roll with its expression as typed, label, total, comparison outcome, detail (every face up to
/// 1,000 per roll) and secret flag, and the result's last line says where; a seeded roll, a roll with nothing live and a
/// roll the store could not take are not logged, and the result says so whenever the caller could expect otherwise. The
/// dice are shown either way: a log problem is never an error.
///
/// <para>
/// Why it fails silently: a roll that was not logged looks exactly like one that was unless the result says so, and the
/// DM finds the gap in the session log weeks later; a roll logged twice, or a seeded replay logged as a table roll, puts
/// dice in the history that nobody rolled; and a log failure reported as an error reads as "the roll failed", so the
/// model rolls again and the table sees two results. Each test reads campaigns.db itself rather than trusting the note.
/// </para>
/// </summary>
public sealed class DiceRollLoggingTests : IAsyncLifetime
{
    private const string Randomness = "_Randomness: cryptographic (OS CSPRNG)._";

    private McpServerHarness _server = null!;

    private CampaignService Campaigns => _server.Services.GetRequiredService<CampaignService>();

    public async Task InitializeAsync()
    {
        _server = new McpServerHarness();
        await _server.InitializeAsync();
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    /// <summary>
    /// Creates a campaign, makes it this process's and the persisted active one when <paramref name="use"/>, and starts
    /// session 1 when <paramref name="live"/>.
    /// </summary>
    private CampaignRow Campaign(string name = "Belmakor", bool live = true, bool use = true)
    {
        var campaign = Campaigns.Store.Create(name, "dm", "2024").Campaign;
        if (use)
        {
            Campaigns.Use(campaign.Slug);
        }

        if (live)
        {
            new SessionWriter(Campaigns.Database).Start(campaign);
        }

        return campaign;
    }

    private async Task<string[]> RollAsync(string arguments) =>
        _server.SuccessText(await _server.CallToolJsonAsync("dice_roll", arguments)).Split('\n');

    private sealed record LoggedRow(string Expression, string? Label, long Total, long? Outcome, string Detail, long Secret, long? Session, string Campaign);

    private List<LoggedRow> Rows()
    {
        var path = Campaigns.Database.Path;
        if (!File.Exists(path))
        {
            return [];
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT d.expression, d.label, d.total, d.outcome, d.detail, d.secret, s.number, c.slug FROM dice_roll d " +
            "LEFT JOIN session s ON s.entity_id = d.session_id JOIN campaign c ON c.id = d.campaign_id ORDER BY d.seq";
        using var reader = command.ExecuteReader();
        var rows = new List<LoggedRow>();
        while (reader.Read())
        {
            rows.Add(new LoggedRow(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetString(7)));
        }

        return rows;
    }

    [Fact]
    public async Task CallTool_LiveSession_LogsOneRowPerRollAndSaysWhereOnTheLastLine()
    {
        Campaign();

        var lines = await RollAsync("""{"expression": "1d20+5>=10", "times": 3, "label": "Stealth"}""");

        Assert.Equal("Logged to belmakor, session 1.", lines[^1]);
        Assert.Equal(Randomness, lines[^2]);
        var rows = Rows();
        Assert.Equal(3, rows.Count);
        for (var i = 0; i < 3; i++)
        {
            var shown = DiceToolTests.NumberedRollRegex().Match(lines[i + 1]);
            Assert.Equal(long.Parse(shown.Groups["total"].Value, CultureInfo.InvariantCulture), rows[i].Total);
            Assert.Equal("1d20+5>=10", rows[i].Expression);
            Assert.Equal("Stealth", rows[i].Label);
            Assert.Equal(rows[i].Total >= 10 ? 1L : 0L, rows[i].Outcome);
            Assert.Equal(0L, rows[i].Secret);
            Assert.Equal(1L, rows[i].Session);
            using var detail = JsonDocument.Parse(rows[i].Detail);
            Assert.Equal(i + 1, detail.RootElement.GetProperty("call").GetProperty("roll").GetInt32());
            Assert.Equal(3, detail.RootElement.GetProperty("call").GetProperty("of").GetInt32());
        }
    }

    [Fact]
    public async Task CallTool_Secret_IsLoggedAsSecretAndSaysSo()
    {
        Campaign();

        var lines = await RollAsync("""{"expression": "1d20", "secret": true}""");

        Assert.Equal("Logged to belmakor, session 1 (secret).", lines[^1]);
        var row = Assert.Single(Rows());
        Assert.Equal(1L, row.Secret);
        Assert.Null(row.Outcome);
        Assert.Null(row.Label);
    }

    [Theory]
    [InlineData("""{"expression": "2d6+3"}""")]
    [InlineData("""{"expression": "2d6+3", "secret": false}""")]
    [InlineData("""{"expression": "2d6+3", "secret": null}""")]
    public async Task CallTool_NoLiveSessionAndNoSecret_LogsNothingAndAddsNoLine(string arguments)
    {
        Campaign(live: false);

        var lines = await RollAsync(arguments);

        Assert.Equal(Randomness, lines[^1]);
        Assert.Equal(2, lines.Length);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task CallTool_SecretWithNothingLive_SaysNotLogged()
    {
        Campaign(live: false);

        var lines = await RollAsync("""{"expression": "1d20", "secret": true}""");

        Assert.Equal("Not logged: no session is live.", lines[^1]);
        Assert.Equal(Randomness, lines[^2]);
        Assert.Empty(Rows());
    }

    [Theory]
    [InlineData("""{"expression": "1d20", "secret": true}""", "Not logged: no session is live.")]
    [InlineData("""{"expression": "1d20"}""", null)]
    public async Task CallTool_NoCampaignsDatabase_CreatesNoneAndSaysSoOnlyForSecret(string arguments, string? note)
    {
        var lines = await RollAsync(arguments);

        Assert.Equal(note ?? Randomness, lines[^1]);
        Assert.False(File.Exists(Campaigns.Database.Path), "A dice roll created campaigns.db.");
    }

    /// <summary>
    /// Kills H01 (FH10, M22): campaigns.db exists but holds no campaign (the only one's creation was undone): a secret roll
    /// says no session is live. Treated like "several campaigns, none chosen", it said "no campaign is chosen" and offered a
    /// use call that cannot work.
    /// </summary>
    [Fact]
    public async Task CallTool_SecretWithACampaignsDatabaseHoldingNoCampaign_SaysNoSessionIsLive()
    {
        var created = _server.SuccessText(await _server.CallToolJsonAsync("campaign", """{"action": "create", "name": "Gone", "role": "dm", "ruleset": "2024"}"""));
        var batch = Regex.Match(created, "Batch `([0-9a-f-]{36})`").Groups[1].Value;
        _server.SuccessText(await _server.CallToolJsonAsync("campaign_history", $$"""{"action": "undo", "batch_id": "{{batch}}", "campaign": "gone"}"""));
        Assert.True(File.Exists(Campaigns.Database.Path));

        var lines = await RollAsync("""{"expression": "1d20", "secret": true}""");

        Assert.Equal("Not logged: no session is live.", lines[^1]);
    }

    /// <summary>
    /// Kills H06 (FH10, M03): with no path for campaigns.db (no home directory, no DND_MCP_DATA_DIR or DND_MCP_DB) there is
    /// no campaign to log to, so a roll is still a roll and a secret one says it was not logged. The environment is a
    /// stand-in with no home, so nothing reaches the user's files.
    /// </summary>
    [Fact]
    public void Roll_NoPathForCampaignsDb_RollsAndSaysNotLogged()
    {
        using var service = new CampaignService(new DndMcpServerOptions { Paths = () => new Repository.DndMcpPaths(_ => null, homeDirectory: null) },
            NullLogger<CampaignService>.Instance, NullLogger<CampaignDatabase>.Instance);
        var dice = new DiceTools(new SeededDiceRoller(7), service, NullLogger<DiceTools>.Instance);

        var plain = dice.Roll("1d20");
        var secret = dice.Roll("1d20", secret: true);

        Assert.DoesNotContain("Not logged", plain, StringComparison.Ordinal);
        Assert.EndsWith("Not logged: no session is live.", secret.TrimEnd(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallTool_SeedDuringALiveSession_IsNotLoggedAndSaysWhy(bool secret)
    {
        Campaign();

        var lines = await RollAsync($$"""{"expression": "4d6kh3", "seed": 42, "secret": {{(secret ? "true" : "false")}}}""");

        Assert.Equal("Not logged: a seeded roll is a replay, not a table roll.", lines[^1]);
        Assert.Equal("_Randomness: pseudo-random, reproducible (xoshiro256**, seed 42)._", lines[^2]);
        Assert.Empty(Rows());
    }

    [Theory]
    [InlineData("""{"expression": "1d20", "secret": true}""")]
    [InlineData("""{"expression": "1d20"}""")]
    public async Task CallTool_SeveralCampaignsNoneChosenOneLive_LogsNothingAndSaysWhy(string arguments)
    {
        // Another process's view: two campaigns, a live session in one, but nothing chosen for this process or persisted.
        // The DM running that session expects the table's rolls in its log, so even a plain roll says why it is not there,
        // and the use call it prints names the live one (use refuses a call without campaign).
        var live = Campaign(use: false);
        Campaign("One Piece", live: false, use: false);

        var lines = await RollAsync(arguments);

        Assert.Equal($"Not logged: no campaign is chosen (campaign {{\"action\": \"use\", \"campaign\": \"{live.Slug}\"}} chooses one).", lines[^1]);
        Assert.Equal(Randomness, lines[^2]);
        Assert.Empty(Rows());
    }

    /// <summary>
    /// The use call the note prints works sent exactly as printed: it chooses the campaign with the live session, and the
    /// next roll is logged to that session. Printed without campaign, use refused it.
    /// </summary>
    [Fact]
    public async Task CallTool_SeveralCampaignsNoneChosenOneLive_TheUseCallAsPrintedMakesTheNextRollLogThere()
    {
        var live = Campaign(use: false);
        Campaign("One Piece", live: false, use: false);
        var note = (await RollAsync("""{"expression": "1d20"}"""))[^1];

        var call = Regex.Match(note, "campaign (\\{[^}]*\\}) chooses one").Groups[1].Value;
        _server.SuccessText(await _server.CallToolJsonAsync("campaign", call));
        var logged = await RollAsync("""{"expression": "1d20"}""");

        Assert.Equal($"Logged to {live.Slug}, session 1.", logged[^1]);
        Assert.Equal(live.Slug, Assert.Single(Rows()).Campaign);
    }

    /// <summary>
    /// FH3 (U12): the current campaign has nothing live but another campaign's session is live (started in another Claude
    /// session, or named explicitly in a start here before this fix): every roll says so, naming the live campaign and the
    /// use call that sends the next roll there. Nothing said for a plain roll left the night's rolls out of the live log
    /// silently, and "no session is live" for a secret roll was false.
    /// </summary>
    [Theory]
    [InlineData("""{"expression": "1d20+5", "label": "Perception"}""")]
    [InlineData("""{"expression": "1d20+3", "label": "Stealth", "secret": true}""")]
    public async Task CallTool_CurrentCampaignHasNothingLiveButAnotherHas_EveryRollNamesTheLiveOne(string arguments)
    {
        var belmakor = Campaign();
        var onePiece = Campaign("One Piece", live: false);

        var lines = await RollAsync(arguments);

        Assert.Equal(
            $"Not logged: {belmakor.Slug} has a live session but {onePiece.Slug} is the current campaign: campaign {{\"action\": \"use\", " +
            $"\"campaign\": \"{belmakor.Slug}\"}}.", lines[^1]);
        Assert.Equal(Randomness, lines[^2]);
        Assert.Empty(Rows());
    }

    /// <summary>The use call that note prints works as printed: the next roll is logged to the live session.</summary>
    [Fact]
    public async Task CallTool_CurrentCampaignHasNothingLiveButAnotherHas_TheUseCallAsPrintedMakesTheNextRollLogThere()
    {
        var belmakor = Campaign();
        Campaign("One Piece", live: false);
        var note = (await RollAsync("""{"expression": "1d20"}"""))[^1];

        _server.SuccessText(await _server.CallToolJsonAsync("campaign", Regex.Match(note, "campaign (\\{[^}]*\\})\\.$").Groups[1].Value));
        var logged = await RollAsync("""{"expression": "1d20"}""");

        Assert.Equal($"Logged to {belmakor.Slug}, session 1.", logged[^1]);
        Assert.Equal(belmakor.Slug, Assert.Single(Rows()).Campaign);
    }

    /// <summary>
    /// Several campaigns live but not the current one: the note names them all, and the use call leaves the slug to fill.
    /// A seeded roll is never a table roll, so it says that instead.
    /// </summary>
    [Fact]
    public async Task CallTool_CurrentCampaignHasNothingLiveButTwoOthersHave_NamesBothAndASeededRollSaysItIsAReplay()
    {
        Campaign("Alpha");
        Campaign("Beta");
        Campaign("Gamma", live: false);

        var plain = await RollAsync("""{"expression": "1d20"}""");
        var seeded = await RollAsync("""{"expression": "1d20", "seed": 7}""");

        Assert.Equal("Not logged: alpha and beta have live sessions but gamma is the current campaign: campaign {\"action\": \"use\", " +
                     "\"campaign\": \"<slug>\"}.", plain[^1]);
        Assert.Equal("Not logged: a seeded roll is a replay, not a table roll.", seeded[^1]);
        Assert.Empty(Rows());
    }

    /// <summary>
    /// FH9 (L09): an open roll's label is printed in every player view of the session's dice (a secret roll's is not), so
    /// the label's description says so: a DM labelling an NPC's roll with a true name the party does not know would
    /// otherwise publish it to the players.
    /// </summary>
    [Fact]
    public async Task ToolSchema_Label_SaysAnOpenRollsLabelIsShownToThePlayersViews()
    {
        var tool = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == "dice_roll");

        var label = tool.JsonSchema.GetProperty("properties").GetProperty("label").GetProperty("description").GetString()!;

        Assert.Contains("an open roll's label is shown to the players' views of that session", label, StringComparison.Ordinal);
        Assert.Contains("or roll secret", label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SeveralCampaignsNoneChosenNoneLive_SaysWhyOnlyForSecret()
    {
        Campaign(live: false, use: false);
        Campaign("One Piece", live: false, use: false);

        var secret = await RollAsync("""{"expression": "1d20", "secret": true}""");
        var plain = await RollAsync("""{"expression": "1d20"}""");

        // No campaign is live, so none can be named: the call says where the slug goes.
        Assert.Equal("Not logged: no campaign is chosen (campaign {\"action\": \"use\", \"campaign\": \"<slug>\"} chooses one).", secret[^1]);
        Assert.Equal(Randomness, plain[^1]);
        Assert.Equal(2, plain.Length);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task CallTool_ProcessCurrentCampaignAndAnotherActive_LogsToTheProcessCurrentOne()
    {
        // Contract §3.11: process current, then the persisted active one (which another Claude session may have set),
        // then the only one. Both campaigns are live, so a roll sent to the wrong one would still be "logged".
        var belmakor = Campaign();
        var onePiece = Campaign("One Piece");
        Campaigns.Use(belmakor.Slug);
        Campaigns.SetCurrent(onePiece.Id);

        var lines = await RollAsync("""{"expression": "1d20"}""");

        Assert.Equal("Logged to one-piece, session 1.", lines[^1]);
        Assert.Equal("one-piece", Assert.Single(Rows()).Campaign);
    }

    [Fact]
    public async Task CallTool_OnlyCampaignNotChosen_IsResolvedAndLogged()
    {
        // Resolution is process current, else active, else the only campaign (contract §3.11).
        var campaign = Campaigns.Store.Create("Belmakor", "dm", "2024").Campaign;
        new SessionWriter(Campaigns.Database).Start(campaign);

        var lines = await RollAsync("""{"expression": "1d20"}""");

        Assert.Equal("Logged to belmakor, session 1.", lines[^1]);
        Assert.Single(Rows());
    }

    [Fact]
    public async Task CallTool_WriteLockedByAnotherProcess_ShowsTheDiceAndSaysNotLoggedWithoutAnError()
    {
        Campaign();
        using var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Campaigns.Database.Path, Pooling = false }.ToString());
        other.Open();
        using (var begin = other.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }

        // The write waits out busy_timeout (5 s), then gives up: the roll is still the answer.
        var result = await _server.CallToolJsonAsync("dice_roll", """{"expression": "2d6+3"}""");

        var lines = _server.SuccessText(result).Split('\n');
        Assert.Equal("Not logged: the campaign database could not be written; this roll stands.", lines[^1]);
        Assert.Equal(Randomness, lines[^2]);
        var roll = DiceToolTests.SingleRollRegex().Match(lines[0]);
        Assert.Equal(long.Parse(roll.Groups["total"].Value, CultureInfo.InvariantCulture), DiceToolTests.Audit(roll.Groups["breakdown"].Value));
        Assert.Contains(_server.ServerLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not logged", StringComparison.Ordinal));
        using (var rollback = other.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }

        Assert.Empty(Rows());
    }

    [Fact]
    public async Task CallTool_DamagedCampaignsDatabase_ShowsTheDiceAndSaysNotLogged()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Campaigns.Database.Path)!);
        await File.WriteAllTextAsync(Campaigns.Database.Path, "this is not a SQLite database, just some bytes");

        var lines = await RollAsync("""{"expression": "1d20"}""");

        // The store's own first sentence (written for the user: which file, what is wrong), then that the roll stands.
        Assert.Equal(
            $"Not logged: campaigns.db at {Campaigns.Database.Path} is damaged or is not a dnd-mcp database, so campaign tools " +
            "cannot use it; this roll stands.",
            lines[^1]);
        Assert.Matches(@"^\*\*\d+\*\*  \(1d20 → \[\d+\]\)$", lines[0]);
    }

    [Fact]
    public async Task CallTool_CampaignsDatabaseFromANewerVersion_SaysSoAndTheRollStands()
    {
        // The reason a user can act on (update dnd-mcp) is in the line, not only in a server log nobody reads.
        Directory.CreateDirectory(Path.GetDirectoryName(Campaigns.Database.Path)!);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Campaigns.Database.Path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        var lines = await RollAsync("""{"expression": "1d20", "secret": true}""");

        Assert.StartsWith($"Not logged: campaigns.db at {Campaigns.Database.Path} was written by a newer version of dnd-mcp (schema version 99;", lines[^1], StringComparison.Ordinal);
        Assert.EndsWith("so this version will not read or change it; this roll stands.", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("Update dnd-mcp", lines[^1], StringComparison.Ordinal);
        Assert.Equal(Randomness, lines[^2]);
        Assert.Contains(_server.ServerLog.Entries, e => e.Level == LogLevel.Warning && e.Category == typeof(DiceTools).FullName);
    }

    [Fact]
    public async Task CallTool_MigrationWaitsOnAnotherProcessesLock_SaysCouldNotBeReadNotTheStoresTryAgain()
    {
        // The store's lock message says "this call did nothing. Try again": beside a roll that stands, that reads as "roll
        // again", so the lock keeps the fixed wording. An empty campaigns.db needs its first migration, whose BEGIN
        // IMMEDIATE waits out busy_timeout (5 s) behind the other connection's write lock.
        Directory.CreateDirectory(Path.GetDirectoryName(Campaigns.Database.Path)!);
        await File.WriteAllBytesAsync(Campaigns.Database.Path, []);
        using var other = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Campaigns.Database.Path, Pooling = false }.ToString());
        other.Open();
        using (var begin = other.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            begin.ExecuteNonQuery();
        }

        var lines = await RollAsync("""{"expression": "1d20", "secret": true}""");

        Assert.Equal("Not logged: the campaign database could not be read; this roll stands.", lines[^1]);
        using (var rollback = other.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task CallTool_SessionTableUnreadable_ShowsTheDiceAndSaysNotLogged()
    {
        // A raw SQLite error after the open (a damaged table): the dice still stand, with the fixed wording (SQLite's own
        // text is not the user's), never the SDK's "An error occurred invoking 'dice_roll'".
        Campaign();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Campaigns.Database.Path, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON; ALTER TABLE session RENAME TO session_gone;";
            command.ExecuteNonQuery();
        }

        var lines = await RollAsync("""{"expression": "1d20"}""");

        Assert.Equal("Not logged: the campaign database could not be read; this roll stands.", lines[^1]);
        Assert.Matches(@"^\*\*\d+\*\*  \(1d20 → \[\d+\]\)$", lines[0]);
    }

    [Fact]
    public async Task CallTool_OpeningCampaignsDatabaseThrowsInvalidOperation_ShowsTheDiceAndSaysNotLogged()
    {
        // CampaignDatabase's own SQLite check throws InvalidOperationException, and so does a database already disposed (a
        // roll racing the server's shutdown, reproduced here): both are the store's failure at the open, not a bug.
        Campaign();
        Campaigns.Database.Dispose();

        var lines = await RollAsync("""{"expression": "1d20"}""");

        Assert.Equal("Not logged: the campaign database could not be read; this roll stands.", lines[^1]);
        Assert.Matches(@"^\*\*\d+\*\*  \(1d20 → \[\d+\]\)$", lines[0]);
    }

    [Fact]
    public async Task CallTool_KeepHighest_DetailHoldsEveryFaceAndTheDroppedDie()
    {
        Campaign();

        await RollAsync("""{"expression": "4d6kh3+2[str]"}""");

        var row = Assert.Single(Rows());
        using var detail = JsonDocument.Parse(row.Detail);
        var root = detail.RootElement;
        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("cryptographic (OS CSPRNG)", root.GetProperty("source").GetString());
        Assert.False(root.TryGetProperty("faces_omitted", out _));
        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        Assert.Equal("4d6kh3", group.GetProperty("term").GetString());
        Assert.Equal(JsonValueKind.Null, group.GetProperty("label").ValueKind);
        var dice = group.GetProperty("dice").EnumerateArray().ToList();
        Assert.Equal(4, dice.Count);
        Assert.Single(dice, d => d.TryGetProperty("dropped", out var dropped) && dropped.GetBoolean());
        Assert.All(dice, d => Assert.Equal(d.GetProperty("value").GetInt64(), Assert.Single(d.GetProperty("faces").EnumerateArray()).GetProperty("face").GetInt64()));
        var kept = dice.Where(d => !d.TryGetProperty("dropped", out _)).Sum(d => d.GetProperty("value").GetInt64());
        Assert.Equal(kept, group.GetProperty("value").GetInt64());
        Assert.Equal(kept + 2, row.Total);
    }

    [Fact]
    public async Task CallTool_RerollsExplosionsAndLabels_AreStoredAsFactsNotEnums()
    {
        // Unseeded (a seeded roll is never logged), so every assertion holds for any faces the dice show. Enough dice that
        // a reroll (all but 6^-10) and an explosion (all but 0.75^40) are all but certain to be there to check.
        Campaign();

        await RollAsync("""{"expression": "10d6ro<=5[fire]+40d4!"}""");

        var row = Assert.Single(Rows());
        using var detail = JsonDocument.Parse(row.Detail);
        var groups = detail.RootElement.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(["10d6ro<=5", "40d4!"], groups.Select(g => g.GetProperty("term").GetString()));
        Assert.Equal("fire", groups[0].GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, groups[1].GetProperty("label").ValueKind);

        // ro<=5 rerolls a die once when it shows 5 or less: the face rerolled away comes first, marked, and the last face
        // is the one that counts.
        Assert.All(groups[0].GetProperty("dice").EnumerateArray(), d =>
        {
            var faces = d.GetProperty("faces").EnumerateArray().ToList();
            Assert.InRange(faces.Count, 1, 2);
            if (faces.Count == 2)
            {
                Assert.True(faces[0].GetProperty("rerolled").GetBoolean());
                Assert.InRange(faces[0].GetProperty("face").GetInt32(), 1, 5);
            }
            else
            {
                Assert.Equal(6, faces[0].GetProperty("face").GetInt32());
            }

            Assert.False(faces[^1].TryGetProperty("rerolled", out _));
            Assert.Equal(faces[^1].GetProperty("face").GetInt64(), d.GetProperty("value").GetInt64());
        });

        // 40d4! adds a pool die for every 4 rolled: exactly the dice showing 4 are marked exploded, one added die each.
        var exploding = groups[1].GetProperty("dice").EnumerateArray().ToList();
        Assert.All(exploding, d => Assert.Equal(
            d.GetProperty("value").GetInt64() == 4,
            d.TryGetProperty("exploded", out var exploded) && exploded.GetBoolean()));
        Assert.Equal(40 + exploding.Count(d => d.TryGetProperty("exploded", out _)), exploding.Count);
        Assert.Equal(exploding.Sum(d => d.GetProperty("value").GetInt64()), groups[1].GetProperty("value").GetInt64());
        Assert.Equal(groups.Sum(g => g.GetProperty("value").GetInt64()), row.Total);

        // The rules come back from the expression text: no rule, operator or comparison is stored as a field or a number.
        foreach (var field in new[] { "\"keep\"", "\"reroll\"", "\"explode\"", "\"comparison\"", "\"kind\"", "\"sides\"" })
        {
            Assert.DoesNotContain(field, row.Detail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("1000d6", false)]
    [InlineData("1000d6!", true)]
    [InlineData("1000d6ro<=5", true)]
    [InlineData("1000d6!!", true)]
    public async Task CallTool_ManyFaces_StoresFacesUpTo1000PerRollThenOnlyGroupValues(string expression, bool omitted)
    {
        // 1000d6 is exactly 1,000 faces; 1000d6! explodes on about one die in six, so it passes 1,000 all but always. The
        // cap counts physical faces, not dice: 1000d6ro<=5 and 1000d6!! keep 1,000 dice but roll far more faces.
        Campaign();

        await RollAsync($$"""{"expression": "{{expression}}"}""");

        var row = Assert.Single(Rows());
        using var detail = JsonDocument.Parse(row.Detail);
        var root = detail.RootElement;
        var group = Assert.Single(root.GetProperty("groups").EnumerateArray());
        Assert.Equal(row.Total, group.GetProperty("value").GetInt64());
        Assert.Equal(omitted, root.TryGetProperty("faces_omitted", out var count));
        Assert.Equal(!omitted, group.TryGetProperty("dice", out _));
        if (omitted)
        {
            Assert.True(count.GetInt64() > DiceLogDetail.MaxFacesStored, $"faces_omitted {count.GetInt64()}");
        }
        else
        {
            Assert.Equal(1000, group.GetProperty("dice").GetArrayLength());
        }
    }

    [Theory]
    [InlineData("40d10cs>=8", false)]
    [InlineData("40d10cs>=8cf=1", true)]
    public async Task CallTool_SuccessCounting_DetailHoldsEachDiesScore(string expression, bool failures)
    {
        // Success counting sums scores, not values: without each die's score a re-render loses its ✓/✗ and the group
        // value cannot be checked. 40 dice: a success (and with cf=1 a failure) is all but certain to be there.
        Campaign();

        await RollAsync($$"""{"expression": "{{expression}}"}""");

        var row = Assert.Single(Rows());
        using var detail = JsonDocument.Parse(row.Detail);
        var group = Assert.Single(detail.RootElement.GetProperty("groups").EnumerateArray());
        var dice = group.GetProperty("dice").EnumerateArray().ToList();
        static int Score(JsonElement die) => die.TryGetProperty("score", out var score) ? score.GetInt32() : 0;
        Assert.All(dice, d =>
        {
            var value = d.GetProperty("value").GetInt64();
            Assert.Equal(value >= 8 ? 1 : failures && value == 1 ? -1 : 0, Score(d));
        });
        Assert.Contains(dice, d => Score(d) == 1);
        Assert.Equal(dice.Sum(Score), group.GetProperty("value").GetInt64());
        Assert.Equal(row.Total, group.GetProperty("value").GetInt64());
    }

    [Fact]
    public async Task CallTool_Penetrating_DetailMarksEveryAddedDie()
    {
        // !p adds a die per explosion that counts one less than its face: the flag is what makes raw = face - 1 readable.
        Campaign();

        await RollAsync("""{"expression": "40d6!p"}""");

        var row = Assert.Single(Rows());
        using var detail = JsonDocument.Parse(row.Detail);
        var dice = Assert.Single(detail.RootElement.GetProperty("groups").EnumerateArray()).GetProperty("dice").EnumerateArray().ToList();
        static bool Flag(JsonElement die, string name) => die.TryGetProperty(name, out var flag) && flag.GetBoolean();
        var penetrated = dice.Where(d => Flag(d, "penetrated")).ToList();
        Assert.NotEmpty(penetrated);
        Assert.Equal(dice.Count - 40, penetrated.Count);
        Assert.Equal(dice.Count(d => Flag(d, "exploded")), penetrated.Count);
        Assert.All(penetrated, d => Assert.Equal(
            Assert.Single(d.GetProperty("faces").EnumerateArray()).GetProperty("face").GetInt64() - 1, d.GetProperty("raw").GetInt64()));
    }

    [Fact]
    public async Task CallTool_Compounding_DetailMarksEveryExplodedFaceOfAChain()
    {
        // !! keeps one die per chain (6!+6!+2): the face-level flag is how a re-render shows which faces exploded.
        Campaign();

        await RollAsync("""{"expression": "40d6!!"}""");

        var row = Assert.Single(Rows());
        using var detail = JsonDocument.Parse(row.Detail);
        var dice = Assert.Single(detail.RootElement.GetProperty("groups").EnumerateArray()).GetProperty("dice").EnumerateArray().ToList();
        Assert.Equal(40, dice.Count);
        Assert.Contains(dice, d => d.GetProperty("faces").GetArrayLength() > 1);
        Assert.All(dice, d =>
        {
            var faces = d.GetProperty("faces").EnumerateArray().ToList();
            Assert.All(faces.SkipLast(1), f => Assert.True(f.TryGetProperty("exploded", out var e) && e.GetBoolean(), f.GetRawText()));
            Assert.False(faces[^1].TryGetProperty("exploded", out _), faces[^1].GetRawText());
            Assert.Equal(faces.Sum(f => f.GetProperty("face").GetInt64()), d.GetProperty("value").GetInt64());
        });
    }

    [Fact]
    public async Task CallTool_LargestLoggedRoll_StaysUnderTheOutputCeiling()
    {
        Campaign();

        var text = _server.SuccessText(await _server.CallToolJsonAsync("dice_roll", """{"expression": "1000d1000", "times": 100, "secret": true}"""));

        Assert.True(text.Length < 16_000, $"dice_roll returned {text.Length} characters.");
        Assert.EndsWith("\nLogged to belmakor, session 1 (secret).", text, StringComparison.Ordinal);
        Assert.Equal(100, Rows().Count);
    }
}
