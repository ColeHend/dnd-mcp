using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: a <c>dice_roll</c> call through the real client returns one text block whose total equals the dice it
/// shows plus the modifier, with every face on the die it claims, and whose last line names the cryptographic
/// source — so the user can audit any roll, and a seeded or constant roller wired in by mistake is caught.
///
/// <para>
/// Phase 0 runs over the stub parser. Phase 1 promises to keep only the tool name and the <c>expression</c>
/// parameter. The line format parsed here is the stub's, and Phase 1's keep/drop (<c>4d6kh3</c>) and exploding dice
/// will have to show dice that do not count, so expect to rewrite <see cref="ResultLineRegex"/> then. What should
/// carry over is the invariant above, stated against whatever format replaces it.
/// </para>
/// </summary>
public sealed partial class DiceToolTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public DiceToolTests(McpServerHarness server)
    {
        _server = server;
    }

    // "**12**  (2d6+3 → [d6:6, d6:3] + 3)", optionally prefixed "#2: " when times > 1.
    [GeneratedRegex(@"^(?:#(?<n>\d+): )?\*\*(?<total>-?\d+)\*\*  \((?<expr>.+) → \[(?<faces>[^\]]*)\](?: (?<sign>[+-]) (?<mod>\d+))?\)$")]
    private static partial Regex ResultLineRegex();

    [GeneratedRegex(@"^d(?<sides>\d+):(?<face>\d+)$")]
    private static partial Regex FaceRegex();

    private static string RandomnessLine => $"_Randomness: {new CryptoDiceRoller().Source}._";

    [Theory]
    [InlineData("2d6+3", 2, 6, 3, 5, 15)]
    [InlineData("1d20+5", 1, 20, 5, 6, 25)]
    [InlineData("d20", 1, 20, 0, 1, 20)]
    [InlineData("1d20-1", 1, 20, -1, 0, 19)]
    [InlineData("8d6", 8, 6, 0, 8, 48)]
    [InlineData("2d6 + 3", 2, 6, 3, 5, 15)]
    public async Task CallTool_Expression_TotalIsFacesPlusModifierWithinBounds(
        string expression, int diceCount, int sides, int modifier, int min, int max)
    {
        var result = await _server.Client.CallToolAsync("dice_roll", new Dictionary<string, object?> { ["expression"] = expression });
        var lines = SplitLines(_server.SuccessText(result));

        Assert.Equal(2, lines.Length);
        var roll = ParseResultLine(lines[0]);

        Assert.Equal(expression, roll.Expression);
        Assert.Equal(diceCount, roll.Faces.Count);
        Assert.All(roll.Faces, f =>
        {
            Assert.Equal(sides, f.Sides);
            Assert.InRange(f.Face, 1, sides);
        });
        Assert.Equal(modifier, roll.Modifier);
        Assert.Equal(roll.Faces.Sum(f => f.Face) + modifier, roll.Total);
        Assert.InRange(roll.Total, min, max);
        Assert.Equal(RandomnessLine, lines[1]);
    }

    [Fact]
    public async Task CallTool_NegativeDiceTerm_SubtractsTheFace()
    {
        var result = await _server.Client.CallToolAsync("dice_roll", new Dictionary<string, object?> { ["expression"] = "-1d4" });
        var roll = ParseResultLine(SplitLines(_server.SuccessText(result))[0]);

        var face = Assert.Single(roll.Faces);
        Assert.Equal(4, face.Sides);
        Assert.Equal(-face.Face, roll.Total);
    }

    [Fact]
    public async Task CallTool_TimesThree_ReturnsThreeNumberedRollsThenSource()
    {
        var result = await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = "2d6+3", ["times"] = 3 });
        var lines = SplitLines(_server.SuccessText(result));

        Assert.Equal(4, lines.Length);
        for (var i = 0; i < 3; i++)
        {
            var roll = ParseResultLine(lines[i]);
            Assert.Equal(i + 1, roll.Number);
            Assert.Equal(2, roll.Faces.Count);
            Assert.Equal(roll.Faces.Sum(f => f.Face) + 3, roll.Total);
        }

        // The source is stated once for the batch, not per roll.
        Assert.Equal(RandomnessLine, lines[3]);
    }

    [Fact]
    public async Task CallTool_ManyRolls_AreNotAllIdentical()
    {
        // 100 rolls of 1d1000 are all equal with probability 1000^-99. A constant or stuck roller fails this; the
        // registered roller being the CSPRNG is pinned by the source line above.
        var result = await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = "1d1000", ["times"] = 100 });
        var totals = SplitLines(_server.SuccessText(result))
            .SkipLast(1)
            .Select(line => ParseResultLine(line).Total)
            .ToList();

        Assert.Equal(100, totals.Count);
        Assert.True(totals.Distinct().Count() > 1, "Every one of 100 rolls of 1d1000 came up the same.");
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();

    private static ParsedRoll ParseResultLine(string line)
    {
        var match = ResultLineRegex().Match(line);
        Assert.True(match.Success, $"Result line not in the expected shape: {line}");

        var facesText = match.Groups["faces"].Value;
        var faces = facesText.Length == 0
            ? []
            : facesText.Split(", ").Select(f =>
            {
                var face = FaceRegex().Match(f);
                Assert.True(face.Success, $"Face not in the expected 'dN:F' shape: {f}");
                return (Sides: Parse(face.Groups["sides"].Value), Face: Parse(face.Groups["face"].Value));
            }).ToList();

        var modifier = match.Groups["mod"].Success
            ? (match.Groups["sign"].Value == "-" ? -1 : 1) * Parse(match.Groups["mod"].Value)
            : 0;

        return new ParsedRoll(
            match.Groups["n"].Success ? Parse(match.Groups["n"].Value) : null,
            Parse(match.Groups["total"].Value),
            match.Groups["expr"].Value,
            faces,
            modifier);
    }

    private static int Parse(string digits) => int.Parse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private sealed record ParsedRoll(int? Number, int Total, string Expression, IReadOnlyList<(int Sides, int Face)> Faces, int Modifier);
}
