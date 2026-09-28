using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Formatting;
using DndMcp.IntegrationTests.Infrastructure;
using ModelContextProtocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>balance_simulate</c> through the real client resolves SRD monsters the way <c>encounter_difficulty</c>
/// does, runs the fights the arguments describe, and answers with one bounded markdown result that a seed reproduces
/// exactly; every mistake comes back as a tool error naming the item and what to send instead.
///
/// <para>
/// What only the host can break, and so what is tested here (the simulator's own rules are <c>DndMcp.Tests</c>'s): the
/// monster lookup and its messages, the seed (drawn from the OS when absent, echoed, accepted back as a number or a
/// decimal string beyond 2^63), progress notifications, the untyped <c>enemies</c> and <c>compare</c> parameters and their
/// guard checks, the output's shape and its size ceilings.
/// </para>
/// <para>
/// <b>Statistical assertions are loose on purpose</b>, each with its reason: they pin that the host wired the right fight
/// (four level 5 fighters crush three ogres; see <see cref="CallTool_ThreeOgresAgainstFourLevel5Fighters_ThePartyWinsNearlyAlways"/>),
/// not the simulator's exact numbers, which its own tests hold to the closed form. Every call has a fixed seed, so a
/// pass is reproducible.
/// </para>
/// </summary>
public sealed partial class SimulateToolTests : IClassFixture<McpServerHarness>
{
    private const string Tool = "balance_simulate";

    private const string Prefix = "An error occurred invoking 'balance_simulate': ";

    private readonly McpServerHarness _server;

    public SimulateToolTests(McpServerHarness server)
    {
        _server = server;
    }

    /// <summary>A plain level 5 fighter build (the contract's §5.8 sanity fight): Str 18, a greatsword twice, +7 to hit.</summary>
    internal static string Fighter(string edition) =>
        $$"""{"name": "Fighter", "edition": "{{edition}}", "level": 5, "abilities": {"str": 18, "dex": 12, "con": 16}, "attacks": [{"name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"]}]}""";

    /// <summary>Four fighters (hp 44, AC 18: chain mail and a shield's worth) as one party entry.</summary>
    internal static string FighterParty(string edition, int count = 4) =>
        $$"""[{"name": "Fighter", "build": {{Fighter(edition)}}, "hp": 44, "ac": 18, "save_proficiencies": ["str", "con"], "count": {{count}}}]""";

    private Task<string> Simulate(string argumentsJson) => SimulateAsync(_server, argumentsJson);

    internal static async Task<string> SimulateAsync(McpServerHarness server, string argumentsJson) =>
        server.SuccessText(await server.CallToolJsonAsync(Tool, argumentsJson));

    private async Task<string> Error(string argumentsJson) => _server.ErrorText(await _server.CallToolJsonAsync(Tool, argumentsJson));

    [Fact]
    public async Task CallTool_BuildPartyAgainstSrdMonstersByName_WithASeed_IsTheSameResultTwice()
    {
        var arguments = $$"""{"party": {{FighterParty("2024")}}, "enemies": [{"monster": "ogre", "count": 3}], "seed": 42, "iterations": 2000}""";

        var first = await Simulate(arguments);
        var second = await Simulate(arguments);

        Assert.Equal(first, second);
        Assert.StartsWith("# Fight simulation: Fighter ×4 vs Ogre ×3\n\n**The party wins ", first, StringComparison.Ordinal);
        Assert.Contains("| Ogre ×3 | enemy | monster 2024/monster/ogre | 11 | 68.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| Fighter ×4 | party | build \"Fighter\" (level 5, 2024) | 18 | 44.0 |", first, StringComparison.Ordinal);
        Assert.Contains("*2024 rules · 2,000 fights · round cap 20 · enemy HP average · no surprise · seed 42*", first, StringComparison.Ordinal);
        Assert.EndsWith("Seed 42 (given): the same call gives this result again, fight for fight.\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_WithoutASeed_DrawsOneAndPassingItBackReproducesTheResult()
    {
        var arguments = $$"""{"party": {{FighterParty("2024", count: 2)}}, "enemies": [{"monster": "Ogre", "count": 3}], "iterations": 1000""";

        var drawn = await Simulate(arguments + "}");
        var seed = SeedHintRegex().Match(drawn);
        Assert.True(seed.Success, drawn);
        var again = await Simulate(arguments + $$""", "seed": "{{seed.Groups[1].Value}}"}""");

        // Everything but the seed's own wording is identical: the headline, every table cell, the assumptions.
        Assert.Equal(WithoutSeedLines(drawn), WithoutSeedLines(again));
        Assert.Contains($"seed {seed.Groups[1].Value} (random)*", drawn, StringComparison.Ordinal);
    }

    [GeneratedRegex("""pass "seed": "(\d+)" with the same arguments to reproduce this result exactly\.""")]
    private static partial Regex SeedHintRegex();

    private static string WithoutSeedLines(string text) =>
        string.Join("\n", text.Split('\n').Where(l => !l.Contains("seed", StringComparison.OrdinalIgnoreCase)));

    [Theory]
    // Beyond long: a ulong seed as a JSON number and as a decimal string (a JavaScript client rounds numbers above 2^53).
    [InlineData("18446744073709551615", "18446744073709551615")]
    [InlineData("\"18446744073709551615\"", "18446744073709551615")]
    [InlineData("\"12345678901234567890\"", "12345678901234567890")]
    [InlineData("0", "0")]
    public async Task CallTool_SeedAsANumberOrADecimalString_IsUsedAndEchoed(string seedJson, string echoed)
    {
        var text = await Simulate($$"""{"party": {{FighterParty("2024", 1)}}, "enemies": [{"monster": "goblin"}], "iterations": 64, "seed": {{seedJson}}}""");

        Assert.Contains($"seed {echoed}*", text, StringComparison.Ordinal);
        Assert.Contains($"Seed {echoed} (given)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SameSeedAsNumberAndAsString_GivesTheSameResult()
    {
        var head = $$"""{"party": {{FighterParty("2014", 2)}}, "enemies": [{"monster": "orc", "count": 4}], "iterations": 500, "seed": """;

        Assert.Equal(await Simulate(head + "9007199254740993}"), await Simulate(head + "\"9007199254740993\"}"));
    }

    [Theory]
    // The fight: four fighters at +7 against AC 11 (hit on 4+, ~19 damage each a round, ~78 for the party) against three
    // ogres with 204 (2024) or 177 (2014) hit points in all, who hit AC 18 on 12+ for ~6 a swing (~19 a round against 176
    // party HP). The closed form ends it in about three rounds with the ogres dealing some 60 damage spread over four
    // fighters: a party loss needs every fighter down, so P(win) ≥ 97% is a floor with room, and the rounds a loose band.
    [InlineData("2024", "2024/monster/ogre", "68.0")]
    [InlineData("2014", "2014/monster/ogre", "59.0")]
    public async Task CallTool_ThreeOgresAgainstFourLevel5Fighters_ThePartyWinsNearlyAlways(string edition, string ogreRef, string ogreHp)
    {
        var text = await Simulate($$"""{"party": {{FighterParty(edition)}}, "enemies": [{"monster": "Ogre", "count": 3}], "seed": 20260927}""");

        var wins = Headline(text);
        Assert.True(wins >= 0.97, $"P(party wins) {wins} in {edition}:\n{text}");
        var rounds = MeanRounds(text);
        Assert.InRange(rounds, 2.0, 5.0);
        Assert.Contains($"| Ogre ×3 | enemy | monster {ogreRef} | 11 | {ogreHp} |", text, StringComparison.Ordinal);
        Assert.Contains($"*{edition} rules · 10,000 fights", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Replay_ShowsThatFightTurnByTurnWithItsSummary()
    {
        var text = await Simulate($$"""{"party": {{FighterParty("2024", 2)}}, "enemies": [{"monster": "Ogre", "count": 2}], "iterations": 50, "seed": 7, "replay": 3}""");

        var at = text.IndexOf("### Fight 3, turn by turn (", StringComparison.Ordinal);
        Assert.True(at > 0, text);
        var replay = text[at..];
        Assert.Contains("```text\n", replay, StringComparison.Ordinal);
        Assert.Contains("Initiative: ", replay, StringComparison.Ordinal);
        Assert.Contains("Round 1", replay, StringComparison.Ordinal);
        Assert.Contains("Summary of fight 3: ", replay, StringComparison.Ordinal);
        Assert.EndsWith("```\n", replay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Compare_ShowsBothRunsAndThePairedDifference()
    {
        // Two fighters against three ogres is a fight the party can lose, so +2 to hit has room to show.
        var text = await Simulate(
            $$"""{"party": {{FighterParty("2024", 2)}}, "enemies": [{"monster": "Ogre", "count": 3}], "iterations": 4000, "seed": 11, """ +
            "\"compare\": {\"member\": 1, \"feature\": {\"name\": \"Plus Two\", \"modifiers\": [{\"kind\": \"to_hit\", \"amount\": 2}]}}}");

        Assert.Contains("### Compare: Fighter (party entry 1) with Plus Two\n", text, StringComparison.Ordinal);
        Assert.Contains("| Per fight | Without | With | Difference (95% CI) |", text, StringComparison.Ordinal);
        var winsRow = text.Split('\n').Single(l => l.StartsWith("| Party wins |", StringComparison.Ordinal));
        // The paired difference of a strictly better build: positive, with a CI that excludes 0 at 4,000 paired fights.
        Assert.Matches(@"\| \+\d+\.\d\d points \(\+\d+\.\d\d to \+\d+\.\d\d\) \|$", winsRow);
    }

    [Fact]
    public async Task CallTool_Precision_RunsBatchesUntilTheHalfWidthIsReached()
    {
        var text = await Simulate($$"""{"party": {{FighterParty("2024", 3)}}, "enemies": [{"monster": "Ogre", "count": 3}], "seed": 5, "precision": 0.02}""");

        Assert.Matches(@"precision ±2% reached \(±\d", text);
        Assert.Contains(" · 10,000 fights · ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Progress_IsReportedWhileFightingAndAlwaysIncreases()
    {
        // 20,000 fights of 40 creatures: about two seconds here. The first chunk is reported at once, then at most every
        // quarter second, from one loop: sent back to back from the simulator's worker threads, notifications overtook
        // each other on the wire, and the MCP spec requires progress to increase with every one.
        var progress = new RecordingProgress();
        var arguments = new Dictionary<string, object?>
        {
            ["party"] = System.Text.Json.JsonDocument.Parse(FighterParty("2024", count: 20)).RootElement.Clone(),
            ["enemies"] = System.Text.Json.JsonDocument.Parse("""[{"monster": "Ogre", "count": 20}]""").RootElement.Clone(),
            ["iterations"] = 20_000,
            ["seed"] = 3,
        };

        _server.SuccessText(await _server.Client.CallToolAsync(Tool, arguments, progress));

        // Notifications can trail the response by a moment on the in-memory transport.
        await Task.Delay(200);
        var reports = progress.Reports;
        var fights = reports.Where(r => r.Total == 20_000).ToList();
        Assert.True(fights.Count >= 2, $"{fights.Count} fight reports.");
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.First.Progress < p.Second.Progress), string.Join(", ", reports.Select(r => r.Progress)));
        Assert.All(fights, f => Assert.InRange(f.Progress, 1, 20_000));
        Assert.All(fights, f => Assert.Matches(@"^Simulated [\d,]+ of 20,000 fights\.$", f.Message));
    }

    [Fact]
    public async Task CallTool_CancelledMidRun_StopsAndTheServerKeepsServing()
    {
        // A long run (40 creatures, 15,000 fights: seconds of work) cancelled as soon as the first chunk reports. The token
        // reaches the simulator between fights; the next call must not queue behind an abandoned run.
        using var cancel = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancel);
        var arguments = new Dictionary<string, object?>
        {
            ["party"] = System.Text.Json.JsonDocument.Parse(FighterParty("2024", count: 20)).RootElement.Clone(),
            ["enemies"] = System.Text.Json.JsonDocument.Parse("""[{"monster": "Ogre", "count": 20}]""").RootElement.Clone(),
            ["iterations"] = 15_000,
            ["round_cap"] = 100,
            ["seed"] = 8,
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _server.Client.CallToolAsync(Tool, arguments, progress, cancellationToken: cancel.Token).AsTask());

        var next = await Simulate($$"""{"party": {{FighterParty("2024", 1)}}, "enemies": [{"monster": "Goblin"}], "iterations": 16, "seed": 1}""");
        Assert.StartsWith("# Fight simulation: ", next, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"{started.Elapsed} to cancel and serve the next call.");
    }

    private sealed class CancelOnFirstReport(CancellationTokenSource cancel) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value)
        {
            if (value.Total is > 0)
            {
                cancel.Cancel();
            }
        }
    }

    [Fact]
    public async Task CallTool_MonsterByRefFromTheOtherEdition_IsUsedAsGivenWithANote()
    {
        var text = await Simulate($$"""{"party": {{FighterParty("2024", 2)}}, "enemies": [{"monster": "2014/monster/ogre"}], "iterations": 64, "seed": 1}""");

        Assert.Contains("- Ogre: `2014/monster/ogre` is a 2014 stat block, used as given in this 2024 fight.", text, StringComparison.Ordinal);
        Assert.Contains("| Ogre | enemy | monster 2014/monster/ogre |", text, StringComparison.Ordinal);
    }

    [Theory]
    // The monster's edition: its entry's, else the fight's, else the party's (the first entry's build edition).
    [InlineData("""{"monster": "Ogre"}""", "2014", null, "2014/monster/ogre")]
    [InlineData("""{"monster": "Ogre"}""", "2014", "\"2024\"", "2024/monster/ogre")]
    [InlineData("""{"monster": "Ogre", "edition": "2024"}""", "2014", null, "2024/monster/ogre")]
    [InlineData("""{"monster": "Ogre"}""", "2024", null, "2024/monster/ogre")]
    public async Task CallTool_MonsterName_IsLookedUpInTheEntryFightOrPartyEdition(string enemy, string partyEdition, string? fightEdition, string expected)
    {
        var edition = fightEdition is null ? string.Empty : $", \"edition\": {fightEdition}";
        var text = await Simulate($$"""{"party": {{FighterParty(partyEdition, 1)}}, "enemies": [{{enemy}}], "iterations": 32, "seed": 1{{edition}}}""");

        Assert.Contains($"| monster {expected} |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SrdMonstersOnBothSides_ShowTheirWarningsEnemiesFirst()
    {
        // The 2014 mummy's Rotting Fist curse and the 2024 lich's uncast spells are warnings (the normalizer's spot checks).
        var text = await Simulate("""{"party": [{"monster": "Lich"}], "enemies": [{"monster": "Mummy", "edition": "2014", "count": 2}], "iterations": 200, "seed": 2}""");

        var warnings = text[text.IndexOf("### What the simulation leaves out", StringComparison.Ordinal)..];
        var mummy = warnings.IndexOf("**Mummy** (`2014/monster/mummy`, enemies):", StringComparison.Ordinal);
        var lich = warnings.IndexOf("**Lich** (`2024/monster/lich`, party):", StringComparison.Ordinal);
        Assert.True(mummy > 0 && lich > mummy, warnings);
    }

    [Fact]
    public async Task CallTool_UnknownMonster_ListsCloseNamesAndSaysToGiveABuild()
    {
        var text = await Error($$"""{"party": {{FighterParty("2024", 1)}}, "enemies": [{"monster": "Orge", "count": 3}]}""");

        Assert.StartsWith(Prefix + "enemies item 1 (Orge): no monster in the 2014 or 2024 SRD is named \"Orge\". Close SRD names: Ogre", text, StringComparison.Ordinal);
        Assert.Contains("give it as a build with hp and ac instead of monster: {\"name\": \"Orge\", \"build\": ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_SeveralUnknownMonsters_AreReportedTogether()
    {
        var text = await Error($$"""{"party": [{"monster": "Gobblin"}], "enemies": [{"monster": "Orge"}, {"monster": "2024/monster/no-such-thing"}]}""");

        var lines = text[Prefix.Length..].Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("party item 1 (Gobblin): no monster", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("enemies item 1 (Orge): no monster", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("enemies item 2 (2024/monster/no-such-thing): no 2024 monster has the slug \"no-such-thing\"", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("For a creature not in the SRD, give it as a build with hp and ac instead of monster.", lines[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"monster": "2024/spell/fireball"}""",
        "enemies item 1 (2024/spell/fireball): ref `2024/spell/fireball` is a spell, not a monster. Give a monster's ref (e.g. \"2024/monster/ogre\") or its name; a creature the SRD lacks is a build with hp and ac.")]
    [InlineData("""{"monster": "  "}""",
        "enemies item 1: monster is empty; give an SRD monster's name or ref, e.g. {\"monster\": \"Ogre\", \"count\": 3}, or use build or archetype instead.")]
    public async Task CallTool_MonsterThatIsNotOne_IsRefusedWithWhatToGive(string enemy, string message)
    {
        Assert.Equal(Prefix + message, await Error($$"""{"party": {{FighterParty("2024", 1)}}, "enemies": [{{enemy}}]}"""));
    }

    [Theory]
    // The Domain's own checks, verbatim after the prefix (DndMcp.Tests pins their wording): the host adds nothing.
    [InlineData("""{"party": [{"build": FIGHTER, "ac": 18}], "enemies": [{"monster": "Ogre"}]}""", "Invalid simulation: party item 1 (Fighter): hp is required with a build, e.g. \"hp\": 44.")]
    [InlineData("""{"party": [], "enemies": [{"monster": "Ogre"}]}""", "Invalid simulation: party is empty; give at least one combatant")]
    [InlineData("""{"party": PARTY, "enemies": []}""", "Invalid simulation: enemies is empty; give at least one combatant")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Ogre"}], "iterations": 0}""", "Invalid simulation: iterations is 0; it is 1 to 100,000 (default 10,000).")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Ogre"}], "policies": {"enemies": "nearest"}}""", "Invalid simulation: policies enemies \"nearest\" is not")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Ogre"}], "compare": {"member": 3, "feature": {"name": "X"}}}""", "Invalid simulation: compare: member is 3; the party has 1 entry, so give 1 to 1.")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Ogre", "count": 20}, {"monster": "Goblin", "count": 20}], "iterations": 100000, "round_cap": 100}""", "Invalid simulation: the fight has 44 combatants (counting copies); at most 40 are simulated.")]
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Ogre", "count": 20}], "iterations": 100000, "round_cap": 100}""", "this simulation is too large: 100,000 fights × 24 combatants × round cap 100 = 240,000,000, over the limit of 60,000,000.")]
    [InlineData("""{"party": [{"archetype": "paladinn", "level": 5}], "enemies": [{"monster": "Ogre"}]}""", "Invalid simulation: party item 1 (paladinn): archetype")]
    public async Task CallTool_InputTheDomainRefuses_ReturnsItsMessageWithTheToolPrefix(string argumentsTemplate, string expectedStart)
    {
        var arguments = argumentsTemplate.Replace("PARTY", FighterParty("2024", 4), StringComparison.Ordinal).Replace("FIGHTER", Fighter("2024"), StringComparison.Ordinal);

        Assert.StartsWith(Prefix + expectedStart, await Error(arguments), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_AfterAnError_KeepsServing()
    {
        await Error($$"""{"party": {{FighterParty("2024", 1)}}, "enemies": [{"monster": "Orge"}]}""");

        Assert.StartsWith("# Fight simulation: ", await Simulate($$"""{"party": {{FighterParty("2024", 1)}}, "enemies": [{"monster": "Goblin"}], "iterations": 16, "seed": 1}"""), StringComparison.Ordinal);
    }

    /// <summary>The headline's P(party wins), as a fraction.</summary>
    internal static double Headline(string text)
    {
        var match = Regex.Match(text, @"\*\*The party wins (\d+(?:\.\d+)?)%\*\*");
        Assert.True(match.Success, text);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) / 100;
    }

    internal static double MeanRounds(string text)
    {
        var match = Regex.Match(text, @"Rounds: mean (\d+\.\d+) ");
        Assert.True(match.Success, text);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Records every report synchronously, in arrival order (Progress&lt;T&gt; would post them and could reorder).</summary>
    private sealed class RecordingProgress : IProgress<ProgressNotificationValue>
    {
        private readonly List<ProgressNotificationValue> _reports = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<ProgressNotificationValue> Reports
        {
            get
            {
                lock (_gate)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(ProgressNotificationValue value)
        {
            lock (_gate)
            {
                _reports.Add(value);
            }
        }
    }
}
