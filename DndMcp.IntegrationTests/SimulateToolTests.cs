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

    /// <summary>The stdio long run's fights: 40 creatures to round 100, the whole work budget (about 15 s on 16 cores).</summary>
    private const int LongRunFights = 5_000;

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

        // Fix F1, U10: the drawn seed is printed as a JSON number below 2^53, so a client that reads numbers as doubles
        // passes back the very seed it was shown (a 64-bit one came back rounded: another fight set, "(given)").
        Assert.True(ulong.Parse(seed.Groups[1].Value, CultureInfo.InvariantCulture) < 1UL << 53, drawn);
        var again = await Simulate(arguments + $$""", "seed": {{seed.Groups[1].Value}}}""");

        // Everything but the seed's own wording is identical: the headline, every table cell, the assumptions.
        Assert.Equal(WithoutSeedLines(drawn), WithoutSeedLines(again));
        Assert.Contains($"seed {seed.Groups[1].Value} (random)*", drawn, StringComparison.Ordinal);
        Assert.Contains($"Seed {seed.Groups[1].Value} (given)", again, StringComparison.Ordinal);
    }

    [GeneratedRegex("""pass "seed": (\d+) with the same arguments to reproduce this result exactly\.""")]
    private static partial Regex SeedHintRegex();

    [Fact]
    public void RandomSeed_EveryDraw_IsBelowTwoToThe53()
    {
        // A JSON number above 2^53 is rounded by a JavaScript client: a drawn seed must survive the trip back exactly.
        for (var i = 0; i < 10_000; i++)
        {
            Assert.InRange(Tools.SimulateTools.RandomSeed(), 0UL, (1UL << 53) - 1);
        }
    }

    [Fact]
    public async Task SeedSchema_SaysANumberOrADecimalString()
    {
        var seed = (await _server.Client.ListToolsAsync()).Single(t => t.Name == Tool).JsonSchema.GetProperty("properties").GetProperty("seed");

        Assert.StartsWith("A seed to repeat a result exactly, 0 to 18446744073709551615 (number or decimal string).", seed.GetProperty("description").GetString(),
            StringComparison.Ordinal);
    }

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
    public async Task CallTool_PrecisionPastTheWorkLimit_StopsThereAndSaysWhy()
    {
        // Ten commoners to round cap 100 fill the work budget at 20,000 fights (charged 100,000, this was refused): an even
        // fight cannot reach ±0.1% there, and the result must blame the work limit, not a fight limit.
        var text = await Simulate("""{"party": [{"monster": "Commoner", "count": 5}], "enemies": [{"monster": "Commoner", "count": 5}], "round_cap": 100, "seed": 3, "precision": 0.001}""");

        Assert.Contains(" · 20,000 fights · ", text, StringComparison.Ordinal);
        Assert.Matches(@"precision ±0\.1% not reached in 20,000 fights \(±[\d.]+%\), the most the work limit allows for 10 combatants to round cap 100; " +
                       "a lower round cap or fewer combatants allow more", text);
    }

    [Fact]
    public async Task CallTool_Progress_IsReportedWhileFightingAndAlwaysIncreases()
    {
        // 20,000 fights of 40 creatures: about two seconds here. The first chunk is reported at once, then at most every
        // quarter second, from one loop that awaits each send: sent fire-and-forget, notifications overtook each other on
        // the wire, and the MCP spec requires progress to increase with every one.
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
        // A long run (40 creatures, 5,000 fights to round 100: seconds of work) cancelled as soon as the first chunk reports. The token
        // reaches the simulator between fights; the next call must not queue behind an abandoned run.
        using var cancel = new CancellationTokenSource();
        var progress = new CancelOnFirstReport(cancel);
        var arguments = new Dictionary<string, object?>
        {
            ["party"] = System.Text.Json.JsonDocument.Parse(FighterParty("2024", count: 20)).RootElement.Clone(),
            ["enemies"] = System.Text.Json.JsonDocument.Parse("""[{"monster": "Ogre", "count": 20}]""").RootElement.Clone(),
            ["iterations"] = 5_000,
            ["round_cap"] = 100,
            ["seed"] = 8,
        };

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _server.Client.CallToolAsync(Tool, arguments, progress, cancellationToken: cancel.Token).AsTask());

        var next = await Simulate($$"""{"party": {{FighterParty("2024", 1)}}, "enemies": [{"monster": "Goblin"}], "iterations": 16, "seed": 1}""");
        Assert.StartsWith("# Fight simulation: ", next, StringComparison.Ordinal);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"{started.Elapsed} to cancel and serve the next call.");
    }

    [Fact]
    public async Task ProgressPump_ASlowSend_TheNextWaitsForItAndOnlyHigherValuesAreSent()
    {
        // The SDK's IProgress sends fire-and-forget, and under load a 1,024 arrived after an 8,736: the MCP spec requires
        // progress to increase with every notification. The pump starts no send before the last has finished (the first
        // send here takes 400 ms, the next report comes at once), and drops a value that does not increase (the index
        // wait counts seconds, the fights count fights).
        var arrived = new List<float>();
        var inFlight = 0;
        var overlapped = false;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = new DndMcp.Tools.SimulateTools.ProgressPump(async (value, cancellationToken) =>
        {
            overlapped |= Interlocked.Increment(ref inFlight) > 1;
            var first = firstStarted.TrySetResult();
            await Task.Delay(first ? 400 : 1, cancellationToken);
            lock (arrived)
            {
                arrived.Add(value.Progress);
            }

            Interlocked.Decrement(ref inFlight);
        });
        var stop = new TaskCompletionSource();
        var sending = pump.SendUntilAsync(stop.Task, CancellationToken.None);

        pump.Report((1_024, 10_000));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        pump.Report((2_048, 10_000));
        pump.Report(new ProgressNotificationValue { Progress = 5, Message = "Still building the SRD rules index (5 s)." });
        await ArrivedAsync(2);
        pump.Report((3_072, 10_000));
        await ArrivedAsync(3);
        stop.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(overlapped, "a send started before the last had finished");
        Assert.Equal([1_024f, 2_048f, 3_072f], arrived);

        async Task ArrivedAsync(int count)
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (arrived.Count < count && waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(10);
            }
        }
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
    public async Task CallTool_CancelledMidRun_TheFightsStop()
    {
        // The client's cancellation must reach the simulator, not just end the call: an abandoned run goes on burning every
        // core. Cancelled the way Claude Code cancels (notifications/cancelled over stdio; the in-memory harness's SDK client
        // never sends one) a second into a long run, the server must go idle within 5 s: under a quarter of a core over a
        // whole second, with no progress sent in it.
        await using var server = BuiltServerProcess.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await StartLongRunAsync(server, timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);

        await server.SendAsync(Cancel(3));
        using var process = System.Diagnostics.Process.GetProcessById(server.ProcessId);
        var sinceCancel = System.Diagnostics.Stopwatch.StartNew();
        var (used, later) = await IdleAfterCancelAsync(server, process, timeout.Token);

        Assert.True(used < TimeSpan.FromMilliseconds(250), $"{used.TotalSeconds:0.00} s of CPU a second, {sinceCancel.Elapsed.TotalSeconds:0.0} s after the cancel");
        Assert.False(later.Any(l => l.Contains("notifications/progress", StringComparison.Ordinal)), $"progress after the cancellation:{Environment.NewLine}{string.Join(Environment.NewLine, later)}");
    }

    [Fact]
    public async Task CallTool_LongRunOnEveryCore_ReportsProgressBeforeAQuarterOfTheFightsAreDone()
    {
        // Progress shows a watching user the run is moving and resets Claude Code's idle timeout, so the first chunks'
        // report must arrive while every core is fighting, not once the workers wind down near the end.
        await using var server = BuiltServerProcess.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await StartLongRunAsync(server, timeout.Token);
        using var process = System.Diagnostics.Process.GetProcessById(server.ProcessId);
        while (FightProgress(server, "fights3").Count == 0 && !server.StdoutLines.Any(l => l.Contains("\"id\":3", StringComparison.Ordinal)))
        {
            AssertRunning(process, server);
            await Task.Delay(10, timeout.Token);
        }

        await server.SendAsync(Cancel(3));
        var first = FightProgress(server, "fights3");
        Assert.True(first.Count > 0, $"no progress before the result:{server.Diagnostics()}");
        Assert.True(first[0] <= LongRunFights / 4, $"the first progress came at {first[0]:N0} of {LongRunFights:N0} fights");
    }

    [Fact]
    public async Task CallTool_TwoLongRunsAtOnce_OneFightsWhileTheOtherWaitsAndSaysSo()
    {
        // balance_simulate is read-only, so a client may run two at once ("party A, and party B"). They must share the fight
        // threads, not take a set each: with a set each, the thread pool starved again — a ping took 6 to 25 s, each call's
        // first progress came at 60% of its run, a cancel was read 3.3 s late. So one call fights while the other waits and
        // reports that it waits (its idle timer is reset too); a second into the two, a ping is answered within a second;
        // the fighting call cancelled, the waiting one starts; that one cancelled too, the server goes idle as for one call.
        await using var server = BuiltServerProcess.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await StartLongRunAsync(server, timeout.Token, 3, 4);
        await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);

        var ping = System.Diagnostics.Stopwatch.StartNew();
        await server.SendAsync("""{"jsonrpc":"2.0","id":5,"method":"ping"}""");
        await server.WaitForResponsesAsync([5], timeout.Token);
        var answered = ping.Elapsed;
        using var process = System.Diagnostics.Process.GetProcessById(server.ProcessId);
        string? fighting = null;
        string? waiting = null;
        while (ping.Elapsed < TimeSpan.FromSeconds(15))
        {
            AssertRunning(process, server);
            fighting = new[] { "fights3", "fights4" }.FirstOrDefault(t => FightProgress(server, t).Count > 0);
            waiting = fighting is null ? null : fighting == "fights3" ? "fights4" : "fights3";
            if (waiting is not null && WaitProgress(server, waiting) >= 2)
            {
                break;
            }

            await Task.Delay(10, timeout.Token);
        }

        Assert.True(answered < TimeSpan.FromSeconds(1), $"the ping took {answered.TotalSeconds:0.00} s behind two runs");
        Assert.True(fighting is not null && waiting is not null && WaitProgress(server, waiting) >= 2,
            $"not one call fighting and the other reporting its wait within 15 s:{server.Diagnostics()}");
        Assert.Empty(FightProgress(server, waiting!)); // one call's fights at a time

        await server.SendAsync(Cancel(fighting == "fights3" ? 3 : 4));
        var cancelled = System.Diagnostics.Stopwatch.StartNew();
        while (FightProgress(server, waiting!).Count == 0 && cancelled.Elapsed < TimeSpan.FromSeconds(10))
        {
            AssertRunning(process, server);
            await Task.Delay(10, timeout.Token);
        }

        Assert.True(FightProgress(server, waiting!).Count > 0, $"the waiting call did not start within 10 s of the other's cancel:{server.Diagnostics()}");
        await server.SendAsync(Cancel(waiting == "fights3" ? 3 : 4));
        var (used, later) = await IdleAfterCancelAsync(server, process, timeout.Token);
        Assert.True(used < TimeSpan.FromMilliseconds(250), $"{used.TotalSeconds:0.00} s of CPU a second after both were cancelled");
        Assert.False(later.Any(l => l.Contains("notifications/progress", StringComparison.Ordinal)), $"progress after the cancellations:{Environment.NewLine}{string.Join(Environment.NewLine, later)}");
    }

    [Fact]
    public async Task CallTool_ClientClosesStdinMidRun_TheFightsStopAndTheServerExits()
    {
        // Closing stdin is how a stdio client ends the session (the MCP spec's shutdown; Claude Code's too). The SDK does
        // not cancel a request still running then: it waits for it, so an abandoned run kept every core but one busy for
        // 30 s more before the process could exit. Closed a second in, the server must be gone within 5 s.
        await using var server = BuiltServerProcess.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await StartLongRunAsync(server, timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);

        server.CloseInput();
        var exitCode = await server.WaitForExitAsync(TimeSpan.FromSeconds(5));

        Assert.True(exitCode is not null, $"the server was still running 5 s after stdin closed:{server.Diagnostics()}");
        Assert.True(exitCode == 0, $"the server exited with code {exitCode}:{server.Diagnostics()}");

        // A normal shutdown, logged once at information level; not the SDK's "threw an unhandled exception" with a stack
        // trace at fail level, which the stopped fights' cancellation gave while it went unhandled.
        Assert.DoesNotContain(server.StderrLines, line => line.StartsWith("fail:", StringComparison.Ordinal));
        Assert.Single(server.StderrLines, line => line.Contains(DndMcp.Tools.SimulateTools.SessionEndedText, StringComparison.Ordinal));
    }

    /// <summary>
    /// Over stdio to the built host: initializes, builds the rules index (a <c>rules_get</c>), then starts one call per id
    /// (default 3; progress token "fights" + id): <see cref="LongRunFights"/> fights of 20 walls against 20 that never
    /// fall, to round 100.
    /// </summary>
    private static async Task StartLongRunAsync(BuiltServerProcess server, CancellationToken cancellationToken, params int[] ids)
    {
        const string initialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"VERSION","capabilities":{},"clientInfo":{"name":"long-run-test","version":"1.0.0"}}}""";
        const string call = """{"jsonrpc":"2.0","id":ID,"method":"tools/call","params":{"name":"balance_simulate","_meta":{"progressToken":"fightsID"},"arguments":{"party":[{"name":"Wall","build":FIGHTER,"hp":5000,"ac":30,"count":20}],"enemies":[{"monster":"Ogre","hp":5000,"ac":30,"count":20}],"iterations":FIGHTS,"round_cap":100,"seed":8}}}""";
        await server.SendAsync(initialize.Replace("VERSION", McpServerHarness.ProtocolVersion, StringComparison.Ordinal));
        await server.WaitForResponsesAsync([1], cancellationToken);
        await server.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await server.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rules_get","arguments":{"ref":"2024/monster/ogre"}}}""");
        await server.WaitForResponsesAsync([2], cancellationToken);
        foreach (var id in ids.Length == 0 ? [3] : ids)
        {
            await server.SendAsync(call.Replace("ID", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                                       .Replace("FIGHTS", LongRunFights.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                                       .Replace("FIGHTER", Fighter("2024"), StringComparison.Ordinal));
        }
    }

    private static string Cancel(int id) =>
        $$$"""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":{{{id.ToString(CultureInfo.InvariantCulture)}}},"reason":"test"}}""";

    /// <summary>
    /// After a cancel: waits (up to 5 s) for a whole second with under a quarter of a core of server CPU, and returns that
    /// second's CPU with the stdout lines written during it.
    /// </summary>
    private static async Task<(TimeSpan Used, IReadOnlyList<string> Later)> IdleAfterCancelAsync(
        BuiltServerProcess server, System.Diagnostics.Process process, CancellationToken cancellationToken)
    {
        var sinceCancel = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan used;
        int seen;
        do
        {
            seen = server.StdoutLines.Count;
            var before = CpuTime(process, server);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            used = CpuTime(process, server) - before;
        }
        while (used >= TimeSpan.FromMilliseconds(250) && sinceCancel.Elapsed < TimeSpan.FromSeconds(5));

        return (used, server.StdoutLines.Skip(seen).ToList());
    }

    /// <summary>The fights completed that the call with this progress token has reported so far, in order.</summary>
    private static List<double> FightProgress(BuiltServerProcess server, string token) =>
        ProgressOf(server, token)
            .Where(p => p.TryGetProperty("total", out var total) && total.GetDouble() == LongRunFights)
            .Select(p => p.GetProperty("progress").GetDouble())
            .ToList();

    /// <summary>How many times the call with this progress token has reported waiting for another call's fights.</summary>
    private static int WaitProgress(BuiltServerProcess server, string token) =>
        ProgressOf(server, token).Count(p => p.TryGetProperty("message", out var message) &&
                                           message.GetString()!.StartsWith("Waiting for another simulation", StringComparison.Ordinal));

    private static IEnumerable<System.Text.Json.JsonElement> ProgressOf(BuiltServerProcess server, string token) =>
        server.StdoutLines
            .Where(l => l.Contains("notifications/progress", StringComparison.Ordinal) && l.Contains($"\"progressToken\":\"{token}\"", StringComparison.Ordinal))
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("params").Clone());

    /// <summary>The server's CPU time so far; fails with its log if it has exited.</summary>
    private static TimeSpan CpuTime(System.Diagnostics.Process process, BuiltServerProcess server)
    {
        AssertRunning(process, server);
        return process.TotalProcessorTime;
    }

    private static void AssertRunning(System.Diagnostics.Process process, BuiltServerProcess server)
    {
        process.Refresh();
        Assert.False(process.HasExited, $"the server exited:{server.Diagnostics()}");
    }

    [Fact]
    public async Task CallTool_MonsterByRefFromTheOtherEdition_IsUsedAsGivenWithANote()
    {
        var text = await Simulate($$"""{"party": {{FighterParty("2024", 2)}}, "enemies": [{"monster": "2014/monster/ogre"}], "iterations": 64, "seed": 1}""");

        Assert.Contains("- Ogre: `2014/monster/ogre` is a 2014 stat block, used as given in this 2024 fight.", text, StringComparison.Ordinal);
        Assert.Contains("| Ogre | enemy | monster 2014/monster/ogre |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TwoEntriesOfOneOtherEditionRef_GiveItsNoteOnce()
    {
        var text = await Simulate($$"""{"party": {{FighterParty("2024", 2)}}, "enemies": [{"monster": "2014/monster/ogre", "name": "Big"}, {"monster": "2014/monster/ogre", "name": "Small"}], "iterations": 32, "seed": 1}""");

        Assert.Single(Regex.Matches(text, Regex.Escape("- Ogre: `2014/monster/ogre` is a 2014 stat block, used as given in this 2024 fight.")));
    }

    [Theory]
    // The monster's edition: its entry's, else the fight's, else the party's (the first entry's build edition).
    [InlineData("""{"monster": "Ogre"}""", "2014", null, "2014/monster/ogre")]
    [InlineData("""{"monster": "Ogre"}""", "2014", "\"2024\"", "2024/monster/ogre")]
    [InlineData("""{"monster": "Ogre", "edition": "2024"}""", "2014", null, "2024/monster/ogre")]
    [InlineData("""{"monster": "Ogre"}""", "2024", null, "2024/monster/ogre")]
    [InlineData("""{"monster": "Ogre", "edition": "2014"}""", "2024", "\"2024\"", "2014/monster/ogre")]
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
    [InlineData("""{"party": PARTY, "enemies": [{"monster": "Ogre", "count": 20}], "iterations": 100000, "round_cap": 100}""", "this simulation is too large: 100,000 fights × 24 combatants × round cap 100 = 240,000,000, over the limit of 20,000,000.")]
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

/// <summary>
/// Invariant: <c>balance_simulate {encounter}</c> fights a stored fight (contract §6.11): its party-side combatants from
/// their CURRENT sheets (D7) and its enemies from their snapshots, the call's own <c>party</c> and <c>enemies</c>
/// appended, and the report after the encounter's notes block equals, byte for byte, the report of the equivalent explicit
/// call (the same seed and entries, each sheet-seeded member as a <c>character</c> entry named by its tracker name);
/// <c>from_state</c> resumes the running fight from its live state, deterministically; a <c>character</c> entry is its
/// sheet's entry (the Domain never sees a campaign reference), refused with the fix when the sheet cannot fight; and the
/// tool stays read-only throughout.
/// </summary>
public sealed class SimulateToolEncounterTests : IAsyncLifetime
{
    private const string Report = "# Fight simulation: ";

    private readonly McpServerHarness _server = new();

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await Campaign.CharacterToolSetup.CreateDeepAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Simulate(string argumentsJson) => SimulateToolTests.SimulateAsync(_server, argumentsJson);

    private async Task<string> Refused(string argumentsJson) => _server.ErrorText(await _server.CallToolJsonAsync("balance_simulate", argumentsJson));

    private Task<string> Combat(string argumentsJson) => Campaign.ScenarioCalls.Call(_server, "combat", argumentsJson);

    // The report an encounter-form result carries, its notes block dropped (contract §6.11: "tests strip it").
    private static string ReportOf(string text)
    {
        var at = text.IndexOf(Report, StringComparison.Ordinal);
        Assert.True(at > 0, text);
        return text[at..];
    }

    private async Task ReefAsync(bool initiative = false)
    {
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre", "count": 2}, {"srd": "Ogre", "name": "Grumm"}]}""");
        if (initiative)
        {
            await Combat("""
                {"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12},
                  {"combatant": "ogre", "total": 10}, {"combatant": "grumm", "total": 3}]}
                """);
        }
    }

    [Fact]
    public async Task Encounter_FreshFight_IsTheExplicitCallsReportAfterItsNotes()
    {
        await ReefAsync();

        var encounter = await Simulate("""{"encounter": "current", "iterations": 300, "seed": 7}""");
        var explicitCall = await Simulate("""
            {"party": [{"character": "character:bjorn-mountainfell"}], "enemies": [{"monster": "2024/monster/ogre", "count": 3}],
             "iterations": 300, "seed": 7, "campaign": "deep"}
            """);

        Assert.Equal(
            "# Encounter \"Reef\" as a simulation\n\n" +
            "deep · active · 2024 rules · as a fresh fight from its combatants. The report below is the one an explicit call with the same " +
            "entries and seed gives; these notes are this encounter's own.\n\n" +
            "- Kaz is left out: it has no sheet in the fight and no stat block (give character:kaz a sheet with campaign_character update, then re-seed it: " +
            "combat {\"action\": \"add\", \"combatants\": [{\"character\": \"character:kaz\"}], \"campaign\": \"deep\"}; or add it with srd).\n\n",
            encounter[..encounter.IndexOf(Report, StringComparison.Ordinal)]);
        Assert.Equal(explicitCall, ReportOf(encounter));
        Assert.StartsWith("# Fight simulation: Björn Mountainfell vs Ogre ×3\n", explicitCall, StringComparison.Ordinal);
        Assert.Contains("| Björn Mountainfell | party | archetype barbarian (level 8, 2024) | 15 | 85.0 |", explicitCall, StringComparison.Ordinal);
        Assert.Contains(
            "### Assumptions\n\n- Björn Mountainfell: no sim_profile, simulated as the level 8 barbarian archetype (2024), with the sheet's HP 85, AC 15, " +
            "save proficiencies Str, Con; its attacks follow the archetype's ability plan (store a sim_profile for the character's own).\n",
            explicitCall, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_TrackerNameUnlikeTheEntitys_EqualsTheExplicitCallThatNamesTheCharacterSo()
    {
        await Combat("""{"action": "start", "name": "Den", "campaign": "deep", "add_party": false, "combatants": [{"character": "character:bjorn-mountainfell", "name": "The Bear"}, {"srd": "2024/monster/ogre"}]}""");

        var encounter = await Simulate("""{"encounter": "Den", "iterations": 200, "seed": 3}""");
        var explicitCall = await Simulate("""
            {"party": [{"character": "character:bjorn-mountainfell", "name": "The Bear"}], "enemies": [{"monster": "2024/monster/ogre"}],
             "iterations": 200, "seed": 3, "campaign": "deep"}
            """);

        Assert.Equal(explicitCall, ReportOf(encounter));
        Assert.Contains("- The Bear: no sim_profile, simulated as the level 8 barbarian archetype (2024)", explicitCall, StringComparison.Ordinal);
        Assert.Contains("\nEvery combatant of the encounter is in the fight below.\n\n# Fight simulation: The Bear vs Ogre\n", encounter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_TheCallsPartyAndEnemies_AreAppendedAfterTheEncounters()
    {
        await ReefAsync();

        var encounter = await Simulate("""
            {"encounter": "current", "party": [{"archetype": "cleric", "level": 8}], "enemies": [{"monster": "Ogre", "name": "Late ogre"}],
             "iterations": 200, "seed": 5}
            """);
        var explicitCall = await Simulate("""
            {"party": [{"character": "character:bjorn-mountainfell"}, {"archetype": "cleric", "level": 8}],
             "enemies": [{"monster": "2024/monster/ogre", "count": 3}, {"monster": "Ogre", "name": "Late ogre"}],
             "iterations": 200, "seed": 5, "campaign": "deep"}
            """);

        Assert.Equal(explicitCall, ReportOf(encounter));
        Assert.StartsWith("# Fight simulation: Björn Mountainfell, Cleric vs Ogre ×3, Late ogre\n", explicitCall, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_APlannedFightWithNoParty_FightsTheCampaignsCurrentParty()
    {
        await Combat("""{"action": "prepare", "name": "Ambush", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre", "count": 2}]}""");

        var text = await Simulate("""{"encounter": "Ambush", "campaign": "deep", "iterations": 200, "seed": 1}""");

        Assert.StartsWith("# Encounter \"Ambush\" as a simulation\n\ndeep · planned · 2024 rules · as a fresh fight from its combatants.", text, StringComparison.Ordinal);
        Assert.Contains("\n- The party is the campaign's current party (Björn Mountainfell, Kaz): the encounter has no party combatants yet.\n", text,
            StringComparison.Ordinal);
        Assert.Contains("\n- Kaz is left out: no sheet (give it one with campaign_character update).\n", text, StringComparison.Ordinal);
        Assert.Contains("\n# Fight simulation: Björn Mountainfell vs Ogre ×2\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_ASideLeftEmpty_IsRefusedWithTheNotes_UntilTheCallGivesIt()
    {
        await Combat("""{"action": "start", "name": "Lone", "campaign": "deep", "add_party": false, "combatants": [{"srd": "2024/monster/ogre"}, {"name": "Crab", "hp": 5, "ac": 10, "side": "ally"}]}""");

        var refused = await Refused("""{"encounter": "Lone", "seed": 1, "iterations": 100}""");
        var given = await Simulate("""{"encounter": "Lone", "party": [{"archetype": "fighter", "level": 5}], "seed": 1, "iterations": 100}""");

        Assert.Equal(
            "An error occurred invoking 'balance_simulate': The encounter has no party to simulate (Crab is left out: a custom combatant has no stat " +
            "block (add it with srd to simulate it).): add party members with sheets (combat add with character), or give party in the call.",
            refused);
        Assert.Contains("\n# Fight simulation: Fighter vs Ogre\n", given, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_TheFightsEdition_IsTheEncounters_NotItsFirstSheets()
    {
        // Björn's sheet follows the 2014 rules; the fight he is in was started under deep's 2024 rules: the fight is 2024.
        await Campaign.ScenarioCalls.Call(_server, "campaign_character", """{"action": "update", "campaign": "deep", "character": "character:bjorn-mountainfell", "sheet": {"ruleset": "2014"}}""");
        await Combat("""{"action": "start", "name": "Den", "campaign": "deep", "add_party": false, "combatants": [{"character": "character:bjorn-mountainfell"}, {"srd": "2024/monster/ogre"}]}""");

        var text = await Simulate("""{"encounter": "Den", "iterations": 100, "seed": 3}""");

        Assert.Contains("| Björn Mountainfell | party | archetype barbarian (level 8, 2014) |", text, StringComparison.Ordinal);
        Assert.Contains("*2024 rules · 100 fights", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_ACharacterTheCallAppends_FightsUnderTheEncountersRuleset()
    {
        // deep is a 2024 campaign; this fight runs under the 2014 rules, and Björn's sheet names no ruleset: appended to it, he
        // fights as the encounter's own members would, in 2014.
        await Combat("""{"action": "start", "name": "Old road", "campaign": "deep", "edition": "2014", "add_party": false, "combatants": [{"srd": "Ogre"}]}""");

        var text = await Simulate("""{"encounter": "Old road", "party": [{"character": "character:bjorn-mountainfell"}], "iterations": 100, "seed": 3}""");

        Assert.Contains("| Björn Mountainfell | party | archetype barbarian (level 8, 2014) |", text, StringComparison.Ordinal);
        Assert.Contains("| Ogre | enemy | monster 2014/monster/ogre |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_FoughtInALair_SimulatesTheInLairCountsAndSaysSo()
    {
        await Combat("""{"action": "start", "name": "Station", "campaign": "deep", "lair": true, "combatants": [{"srd": "2024/monster/aboleth"}]}""");

        var text = await Simulate("""{"encounter": "current", "iterations": 100, "seed": 2}""");

        Assert.StartsWith("# Encounter \"Station\" as a simulation\n\ndeep · active · 2024 rules · in a lair · as a fresh fight", text, StringComparison.Ordinal);
        Assert.Contains("\n- Fought in a lair: in-lair legendary counts are simulated; lair actions are not.\n", text, StringComparison.Ordinal);
        Assert.Contains("- In a lair: legendary action and Legendary Resistance counts are the in-lair ones where the stat block has them", text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A lair fight has no explicit form (contract §6.11: "A lair encounter is pinned in the encounter form only"):
    /// balance_simulate takes no lair, so the same entries given explicitly fight outside it, with other legendary counts and
    /// another report. Its notes say so, worded as from_state's are, and never claim the explicit call's report, which would
    /// send the model after a report no call it can make reproduces (stage-5 X2 finding).
    /// </summary>
    [Fact]
    public async Task Encounter_FoughtInALair_ClaimsNoExplicitForm_TheSameEntriesGivenExplicitlyReportOtherwise()
    {
        await Combat("""
            {"action": "start", "name": "Station", "campaign": "deep", "lair": true, "add_party": false,
             "combatants": [{"character": "character:bjorn-mountainfell"}, {"srd": "2024/monster/aboleth"}]}
            """);

        var encounter = await Simulate("""{"encounter": "current", "iterations": 300, "seed": 2}""");
        var explicitCall = await Simulate("""
            {"party": [{"character": "character:bjorn-mountainfell"}], "enemies": [{"monster": "2024/monster/aboleth"}], "iterations": 300, "seed": 2, "campaign": "deep"}
            """);

        Assert.Equal(
            "# Encounter \"Station\" as a simulation\n\n" +
            "deep · active · 2024 rules · in a lair · as a fresh fight from its combatants. No explicit call can put a fight in a lair, so this report has " +
            "no explicit form; the same call with the same seed repeats it. These notes are this encounter's own.\n\n" +
            "- Fought in a lair: in-lair legendary counts are simulated; lair actions are not.\n\n",
            encounter[..encounter.IndexOf(Report, StringComparison.Ordinal)]);
        Assert.NotEqual(explicitCall, ReportOf(encounter));
        Assert.Contains("- In a lair: legendary action and Legendary Resistance counts are the in-lair ones", ReportOf(encounter), StringComparison.Ordinal);
        Assert.Contains("- No lair actions: the fight is not in a lair", explicitCall, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromState_ResumesTheLiveFight_FromItsHitPointsAndTurn_TheSameSeedTwiceAlike()
    {
        await ReefAsync(initiative: true);
        await Combat("""{"action": "damage", "targets": ["ogre"], "amount": 30, "source": "bjorn-mountainfell"}""");

        var first = await Simulate("""{"from_state": true, "iterations": 300, "seed": 7}""");
        var second = await Simulate("""{"encounter": "current", "from_state": true, "iterations": 300, "seed": 7}""");

        Assert.Equal(first, second);
        Assert.StartsWith("# Encounter \"Reef\" as a simulation\n\ndeep · active · 2024 rules · resumed from its live state (HP, conditions, concentration, " +
                          "uses left, the turn). No explicit call can resume a fight, so this report has no explicit form; the same call with the same " +
                          "seed repeats it. These notes are this encounter's own.\n", first, StringComparison.Ordinal);
        Assert.DoesNotContain("an explicit call with the same entries and seed gives", first, StringComparison.Ordinal);
        Assert.Contains("| Combatant | Side | From | AC | HP at start | Dropped to 0 |", first, StringComparison.Ordinal);
        Assert.Contains("| Ogre | enemy | monster 2024/monster/ogre | 11 | 38.0 |", first, StringComparison.Ordinal);
        Assert.Contains("- Resumed from a live fight in round 1 at Björn Mountainfell's turn, in its order:", first, StringComparison.Ordinal);
    }

    // A fight too big to simulate: Björn and 40 enemies (Kaz has no sheet, so the simulation leaves him out).
    private async Task HordeAsync(bool initiative)
    {
        await Combat("""{"action": "start", "name": "Horde", "campaign": "deep", "combatants": [{"srd": "goblin warrior", "count": 20}, {"srd": "2024/monster/ogre", "count": 20}]}""");
        if (initiative)
        {
            await Combat("""
                {"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "total": 15}, {"combatant": "kaz", "total": 12},
                  {"combatant": "goblin-warrior", "total": 10}, {"combatant": "ogre", "total": 3}]}
                """);
        }
    }

    [Fact]
    public async Task Encounter_MoreThanFortyCreatures_IsRefusedWithTheEncounterFormsFix()
    {
        // Fix F1, C09: "Lower some counts" is the explicit call's fix; an encounter's combatants are not counts the call gives.
        await HordeAsync(initiative: false);

        var text = await Refused("""{"encounter": "current", "iterations": 100, "seed": 1}""");

        Assert.Equal(
            "An error occurred invoking 'balance_simulate': Invalid simulation: the fight \"Horde\" has 41 combatants to simulate (counting copies); at most 40 are " +
            "simulated, and the encounter form simulates every one of the encounter's. Simulate part of it with an explicit call instead: party and enemies with " +
            "no encounter (a character entry plays its sheet).",
            text);
    }

    [Fact]
    public async Task Encounter_MoreThanFortyWithTheCallsOwn_SaysHowManyTheCallAppended()
    {
        await Combat("""{"action": "start", "name": "Horde", "campaign": "deep", "combatants": [{"srd": "goblin warrior", "count": 20}, {"srd": "2024/monster/ogre", "count": 19}]}""");

        var text = await Refused("""{"encounter": "current", "enemies": [{"monster": "Ogre", "count": 2}], "iterations": 100, "seed": 1}""");

        Assert.Contains("the fight \"Horde\" has 42 combatants to simulate (counting copies: 40 from the encounter, 2 appended); at most 40 are simulated, " +
                        "and the encounter form simulates every one of the encounter's. Append fewer, or simulate part of it with an explicit call instead",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromState_MoreThanFortyCreatures_IsRefusedWithTheResumesFix()
    {
        await HordeAsync(initiative: true);

        var text = await Refused("""{"from_state": true, "iterations": 100, "seed": 1}""");

        Assert.Equal(
            "An error occurred invoking 'balance_simulate': Invalid simulation: the live fight \"Horde\" has 41 creatures to resume; at most 40 are simulated, and " +
            "from_state resumes every one of them. Simulate part of it with an explicit call instead: party and enemies with no encounter or from_state " +
            "(a fresh fight: a character entry starts at its sheet's maximum HP).",
            text);
    }

    [Fact]
    public async Task Explicit_MoreThanFortyCreatures_KeepsTheExplicitFix()
    {
        var text = await Refused("""{"party": [{"archetype": "fighter", "level": 5}], "enemies": [{"monster": "Ogre", "count": 20}, {"monster": "Ogre", "count": 20}]}""");

        Assert.Contains("the fight has 41 combatants (counting copies); at most 40 are simulated. Lower some counts.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromState_ABatchThatChangesNothing_TheFreshFormKeepsTheHpHeader()
    {
        await ReefAsync();

        var text = await Simulate("""{"encounter": "current", "iterations": 100, "seed": 7}""");

        Assert.Contains("| Combatant | Side | From | AC | HP | Dropped to 0 |", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"from_state": true, "party": [{"archetype": "fighter", "level": 5}]}""",
        "from_state resumes the fight exactly as it stands, so party and enemies cannot be added to it: leave them out, or simulate the encounter without " +
        "from_state (a fresh fight) to add them.")]
    [InlineData("""{"from_state": true, "enemies": [{"monster": "Ogre"}]}""", "from_state resumes the fight exactly as it stands")]
    [InlineData("""{"from_state": true, "surprise": "party"}""", "from_state resumes a fight already under way, so nobody is surprised: leave surprise out.")]
    [InlineData("""{"from_state": true}""", "from_state resumes a running fight (round 1 or later): roll initiative first, or simulate the encounter without from_state.")]
    [InlineData("""{"encounter": "No such fight"}""", "No such fight")]
    public async Task FromState_WhatItCannotResume_IsRefused(string call, string message)
    {
        await ReefAsync();

        var text = await Refused(call);

        Assert.StartsWith("An error occurred invoking 'balance_simulate': ", text, StringComparison.Ordinal);
        Assert.Contains(message, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Character_IsTheSheetsEntry_TheReportOfTheSameEntryGivenOutright()
    {
        // D7: no sim_profile, so the barbarian archetype at the sheet's level with its HP, AC and saves. The same entry given
        // by hand reports alike apart from the sheet's assumption line.
        var character = await Simulate("""{"party": [{"character": "character:bjorn-mountainfell"}], "enemies": [{"monster": "Ogre"}], "iterations": 200, "seed": 9, "campaign": "deep"}""");
        var byHand = await Simulate("""
            {"party": [{"name": "Björn Mountainfell", "archetype": "barbarian", "level": 8, "edition": "2024", "hp": 85, "ac": 15, "save_proficiencies": ["str", "con"]}],
             "enemies": [{"monster": "Ogre"}], "iterations": 200, "seed": 9, "campaign": "deep"}
            """);

        var line = "- Björn Mountainfell: no sim_profile, simulated as the level 8 barbarian archetype (2024), with the sheet's HP 85, AC 15, save " +
                   "proficiencies Str, Con; its attacks follow the archetype's ability plan (store a sim_profile for the character's own).\n";
        Assert.Contains(line, character, StringComparison.Ordinal);
        Assert.Equal(byHand, character.Replace(line, string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Character_FieldsGivenBesideIt_AreLaidOverTheSheets_AndTheLineSaysSo()
    {
        var text = await Simulate("""
            {"party": [{"character": "bjorn-mountainfell", "name": "Bear", "hp": 50, "ac": 18, "level": 5, "position": "back"}], "enemies": [{"monster": "Ogre"}],
             "iterations": 100, "seed": 9, "campaign": "deep"}
            """);

        // The line describes the entry as simulated: each field the call gave is named as the call's, never the sheet's
        // value it replaced (level 8, HP 85, AC 15).
        Assert.Contains("| Bear | party | archetype barbarian (level 5, 2024) | 18 | 50.0 |", text, StringComparison.Ordinal);
        Assert.Contains(
            "- Bear: no sim_profile, simulated as the level 5 barbarian archetype (2024), with the call's level 5, HP 50, AC 18, position back; the sheet's " +
            "save proficiencies Str, Con; its attacks follow the archetype's ability plan (store a sim_profile for the character's own).\n",
            text, StringComparison.Ordinal);
        foreach (var stale in new[] { "level 8", "HP 85", "AC 15" })
        {
            Assert.DoesNotContain(stale, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Character_PositionGivenBesideIt_ReachesTheFight()
    {
        // Behind a standing fighter, the ogre's melee cannot reach Björn: the same seed fights differently for him.
        const string Rest = """, {"archetype": "fighter", "level": 8}], "enemies": [{"monster": "Ogre"}], "iterations": 300, "seed": 4, "campaign": "deep"}""";
        var back = await Simulate("""{"party": [{"character": "character:bjorn-mountainfell", "position": "back"}""" + Rest);
        var front = await Simulate("""{"party": [{"character": "character:bjorn-mountainfell"}""" + Rest);

        static string Row(string text) => text.Split('\n').First(l => l.StartsWith("| Björn Mountainfell | party |", StringComparison.Ordinal));
        Assert.NotEqual(Row(front), Row(back));
        Assert.Contains("with the call's position back; the sheet's HP 85", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Character_ASheetWithNoRuleset_FightsUnderTheCampaignsNotTheCallsEdition()
    {
        // D7: "in the sheet's ruleset (else the campaign's)": a 2014 campaign's ruleset-less sheet is the 2014 archetype even
        // in a fight the call puts under the 2024 rules.
        await Campaign.ScenarioCalls.Call(_server, "campaign", """{"action": "create", "name": "Old", "role": "dm", "ruleset": "2014", "slug": "old"}""");
        await Campaign.ScenarioCalls.Call(_server, "campaign_write", """{"campaign": "old", "ops": [{"op": "upsert", "kind": "character", "name": "Torch", "subtype": "pc", "visibility": "party"}]}""");
        await Campaign.ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "update", "campaign": "old", "character": "character:torch", "sheet": {"classes": [{"class": "fighter", "level": 5}], "ac": 16, "max_hp": 44}}""");

        var text = await Simulate("""
            {"party": [{"character": "character:torch"}], "enemies": [{"monster": "Ogre"}], "edition": "2024", "iterations": 100, "seed": 9, "campaign": "old"}
            """);

        Assert.Contains("| Torch | party | archetype fighter (level 5, 2014) | 16 | 44.0 |", text, StringComparison.Ordinal);
        Assert.Contains("- Torch: no sim_profile, simulated as the level 5 fighter archetype (2014)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Character_SeveralEntriesWrong_AreReportedTogether()
    {
        var text = await Refused("""
            {"party": [{"character": "character:kaz"}, {"character": "character:bjorn-mountainfell", "count": 2, "edition": "2014"}],
             "enemies": [{"character": "character:nobody"}, {"monster": "Ogre"}], "campaign": "deep", "seed": 1}
            """);

        Assert.StartsWith("An error occurred invoking 'balance_simulate': Invalid simulation (4 problems):\n" +
                          "- party item 1 (character:kaz): Kaz has no sheet to simulate yet;", text, StringComparison.Ordinal);
        Assert.Contains("\n- party item 2 (character:bjorn-mountainfell): a character is one creature; leave count out.\n" +
                        "- party item 2 (character:bjorn-mountainfell): a character fights under its sheet's ruleset;", text, StringComparison.Ordinal);
        Assert.Contains("\n- enemies item 1 (character:nobody): ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Character_AsAnEnemy_IsExpandedToo()
    {
        var text = await Simulate("""
            {"party": [{"archetype": "fighter", "level": 8}], "enemies": [{"character": "character:bjorn-mountainfell"}], "iterations": 100, "seed": 9, "campaign": "deep"}
            """);

        Assert.StartsWith("# Fight simulation: Fighter vs Björn Mountainfell\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"character": "character:bjorn-mountainfell", "monster": "Ogre"}""", "party item 1 (character:bjorn-mountainfell): give only one of monster, build, archetype and character.")]
    [InlineData("""{"character": "character:bjorn-mountainfell", "count": 2}""", "party item 1 (character:bjorn-mountainfell): a character is one creature; leave count out.")]
    [InlineData("""{"character": "character:bjorn-mountainfell", "edition": "2014"}""", "party item 1 (character:bjorn-mountainfell): a character fights under its sheet's ruleset")]
    [InlineData("""{"character": "character:nobody"}""", "party item 1 (character:nobody): ")]
    [InlineData("""{"character": "character:kaz"}""",
        "party item 1 (character:kaz): Kaz has no sheet to simulate yet; make one with campaign_character {\"action\": \"update\", \"character\": \"character:kaz\", " +
        "\"sheet\": {\"level\": …}, \"campaign\": \"deep\"}, or leave Kaz out.")]
    [InlineData("""{"character": "character:harbour-master"}""",
        "party item 1 (character:harbour-master): Harbour Master's sheet has no classes (only level 4), so there is no archetype to simulate: give the sheet " +
        "a sim_profile (and classes) or leave Harbour Master out.")]
    [InlineData("""{"character": " "}""", "party item 1: character is empty")]
    public async Task Character_ASheetThatCannotFight_IsRefusedNamingTheItemAndTheFix(string entry, string message)
    {
        var text = await Refused($$"""{"party": [{{entry}}], "enemies": [{"monster": "Ogre"}], "campaign": "deep", "seed": 1}""");

        // One problem, in the Domain's own words for a simulation's problems (DslProblems).
        Assert.StartsWith("An error occurred invoking 'balance_simulate': Invalid simulation: " + message, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_MoreNotesThanTheBlockHolds_AreCutWithACount_AndTheResultStaysUnderTheCap()
    {
        // 20 allies and 20 neutrals with no stat block (70-character names): twenty "left out" notes and one neutral note of
        // some 1,500 characters, more than the block's 3,000.
        string Name(string side) => side + " " + new string('n', 70 - side.Length - 1);
        await Combat($$"""
            {"action": "start", "name": "Crowd", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre"},
              {"name": "{{Name("Friend")}}", "count": 20, "hp": 9, "ac": 10, "side": "ally"},
              {"name": "{{Name("Bystander")}}", "count": 20, "hp": 9, "ac": 10, "side": "neutral"}]}
            """);

        var text = await Simulate("""{"encounter": "current", "iterations": 100, "seed": 1}""");

        var block = text[..text.IndexOf(Report, StringComparison.Ordinal)];
        Assert.InRange(block.Length, 2_000, SimulationMarkdown.MaxEncounterNotesChars);
        Assert.Matches(@"\n- … and \d+ more notes?\.\n\n$", block);
        Assert.All(block.Split('\n'), line => Assert.True(line.Length <= SimulationMarkdown.MaxEncounterNoteChars + 2, line));
        Assert.True(text.Length <= SimulationMarkdown.MaxChars, $"{text.Length} characters");
        Assert.StartsWith("# Fight simulation: Björn Mountainfell vs Ogre\n", ReportOf(text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromState_AnEnemyWithUnknownHp_IsRefusedWithACallThatNamesTheCampaign()
    {
        await Combat("""{"action": "start", "name": "Fog", "campaign": "deep", "add_party": false, "combatants": [{"srd": "2024/monster/ogre", "name": "Wraith", "hp": "unknown"}, {"character": "character:bjorn-mountainfell"}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "wraith", "total": 9}, {"combatant": "bjorn-mountainfell", "total": 12}]}""");

        var text = await Refused("""{"from_state": true, "seed": 1}""");

        Assert.Equal(
            "An error occurred invoking 'balance_simulate': from_state needs the enemies' hit points; Wraith has none: give hp with combat set " +
            "(combat {\"action\": \"set\", \"combatants\": [{\"name\": \"Wraith\", \"hp\": …}], \"campaign\": \"deep\"}).",
            text);
    }

    [Fact]
    public async Task Encounter_TypicalAndLargest_StayUnderTheirCeilings()
    {
        await ReefAsync();
        var typical = await Simulate("""{"encounter": "current", "iterations": 200, "seed": 1}""");
        await Combat("""{"action": "end", "discard": true}""");

        // 20 allies and 20 enemies from stat blocks (no two enemies alike, so 40 entries), 10 custom combatants left out
        // with 80-character names (the notes block's longest), a 100-round cap and a replay.
        string Name(string side, int i) => $"{side} {i:00} " + new string('n', 80 - side.Length - 4);
        var allies = Enumerable.Range(1, 20).Select(i => $$"""{"srd": "2024/monster/ogre", "name": "{{Name("Ally", i)}}", "side": "ally"}""");
        var enemies = Enumerable.Range(1, 20).Select(i => $$"""{"srd": "2024/monster/ogre", "name": "{{Name("Foe", i)}}", "hp": {{40 + i}}}""");
        var customs = Enumerable.Range(1, 10).Select(i => $$"""{"name": "{{Name("Odd", i)}}", "hp": 9, "ac": 10}""");
        await Combat($$"""{"action": "start", "name": "{{new string('W', 80)}}", "add_party": false, "combatants": [{{string.Join(", ", allies.Concat(enemies))}}]}""");
        await Combat($$"""{"action": "add", "combatants": [{{string.Join(", ", customs)}}]}""");

        var largest = await Simulate("""{"encounter": "current", "iterations": 50, "round_cap": 100, "seed": 3, "replay": 1}""");

        Assert.True(typical.Length < 8_000, $"{typical.Length} characters");
        Assert.True(largest.Length <= SimulationMarkdown.MaxChars, $"{largest.Length} characters");
        Assert.StartsWith("# Encounter \"" + new string('W', 80) + "\" as a simulation\n", largest, StringComparison.Ordinal);
        Assert.Contains("\n# Fight simulation: ", largest, StringComparison.Ordinal);
        Assert.Equal(0, largest.Split('\n').Count(l => l.StartsWith("```", StringComparison.Ordinal)) % 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Character_ACountBesideIt_IsRefused_ACharacterIsOneCreature(int count)
    {
        // Review M (mutant S05): only a count above 1 was pinned; with "count > 1" a count of 0 was silently one creature
        // (the sheet's overlay carries no count).
        var text = await Refused($$"""
            {"campaign": "deep", "party": [{"character": "character:bjorn-mountainfell", "count": {{count}}}], "enemies": [{"monster": "Ogre"}], "iterations": 100, "seed": 1}
            """);

        Assert.Contains("a character is one creature; leave count out", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Encounter_WithAnAppendedCharacter_TheAssumptionsAreInEntryOrder()
    {
        // Review M (mutant S07): the encounter's own sheet assumptions come before those of the call's appended entries, as
        // the entries do; swapped, nothing failed.
        await Campaign.ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "update", "campaign": "deep", "character": "character:harbour-master", "sheet": {"classes": [{"class": "fighter", "level": 4}]}}""");
        await ReefAsync();

        var text = await Simulate("""{"encounter": "Reef", "campaign": "deep", "party": [{"character": "character:harbour-master"}], "iterations": 100, "seed": 7}""");

        var assumptions = text[text.IndexOf("### Assumptions", StringComparison.Ordinal)..];
        var bjorn = assumptions.IndexOf("Björn Mountainfell", StringComparison.Ordinal);
        var master = assumptions.IndexOf("Harbour Master", StringComparison.Ordinal);
        Assert.True(bjorn >= 0 && master > bjorn, assumptions);
    }
}

/// <summary>
/// Invariant: an encounter's notes block (<see cref="SimulationMarkdown.EncounterNotes"/>) never passes
/// <see cref="SimulationMarkdown.MaxEncounterNotesChars"/>, whatever the encounter holds — many notes are cut with a count,
/// one huge note is excerpted, a long name is shortened — so the report after it always has its room and the result can
/// never pass the cap (a block left unbounded made the report's reserve too large to honour).
/// </summary>
public sealed class SimulationMarkdownEncounterNotesTests
{
    private static Repository.Campaign.Combat.EncounterSimulation Encounter(string name, IReadOnlyList<string> notes) =>
        new(name, "2024", "active", [], [], null, false, notes, []);

    public static TheoryData<string, int, int> Shapes => new()
    {
        // name length, notes, each note's length
        { "Short", 1, 50_000 },
        { "Short", 2_000, 100 },
        { new string('N', 5_000), 40, 2_000 },
        { "Short", 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EncounterNotes_AnyEncounter_StaysWithinItsBound(string name, int count, int length)
    {
        var notes = Enumerable.Range(0, count).Select(i => $"{i} " + new string('x', Math.Max(0, length - 6))).ToList();

        var block = SimulationMarkdown.EncounterNotes(Encounter(name, notes), "deep", fromState: count % 2 == 0);

        Assert.InRange(block.Length, 1, SimulationMarkdown.MaxEncounterNotesChars);
        Assert.EndsWith("\n\n", block, StringComparison.Ordinal);
        if (count > 1)
        {
            Assert.Matches(@"\n- … and [\d,]+ more notes?\.\n\n$", block);
        }

        if (count == 1)
        {
            Assert.Contains("…\n", block, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The block claims the explicit call's report only for a fresh fight out of a lair (contract §6.11): a resumed fight
    /// has no explicit form (no call carries a live state), nor has a lair fight (balance_simulate takes no lair); each says
    /// so in the same words, the resumed one winning when both hold.
    /// </summary>
    [Theory]
    [InlineData(false, false, "The report below is the one an explicit call with the same entries and seed gives; these notes are this encounter's own.")]
    [InlineData(false, true,
        "No explicit call can put a fight in a lair, so this report has no explicit form; the same call with the same seed repeats it. These notes are this encounter's own.")]
    [InlineData(true, false,
        "No explicit call can resume a fight, so this report has no explicit form; the same call with the same seed repeats it. These notes are this encounter's own.")]
    [InlineData(true, true,
        "No explicit call can resume a fight, so this report has no explicit form; the same call with the same seed repeats it. These notes are this encounter's own.")]
    public void EncounterNotes_TheExplicitFormClaim_IsMadeOnlyForAFreshFightOutOfALair(bool fromState, bool lair, string sentence)
    {
        var block = SimulationMarkdown.EncounterNotes(new("Station", "2024", "active", [], [], null, lair, [], []), "deep", fromState);

        var how = fromState ? "resumed from its live state (HP, conditions, concentration, uses left, the turn)" : "as a fresh fight from its combatants";
        Assert.Equal(
            $"# Encounter \"Station\" as a simulation\n\ndeep · active · 2024 rules{(lair ? " · in a lair" : string.Empty)} · {how}. {sentence}\n\n" +
            "Every combatant of the encounter is in the fight below.\n\n",
            block);
    }
}
