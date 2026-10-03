using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: understand-onepiece.md §3 rows 9-15, 20 and 23, the seal gate's "warn and apply, never refuse" (contract
/// §3.4, §11), through the tools on the One Piece world the tools built: a reveal of the seal before its gate is met,
/// through <c>campaign_knowledge reveal</c> or through a <c>campaign_write</c> fact op's <c>known_by</c> alike, is written
/// (the ledger shows the party knowing it, in the session given) and its result names every unmet condition (after: the
/// axe, judged at the session the reveal is filed under; with: Nadar's plan, both ways, or landed separately; routes: too
/// few, by id), the forbidden "seal" in each statement the party now reads while the axe is not in play, and the advisory
/// prefer, with the gate's note, the secret's new status and the gate warnings kept in the batch's history; a dry run
/// shows the same and writes nothing; the call that completes the second route warns "reachable before
/// the gate", and no other call does; T5 after T4 warns only the advisory.
///
/// <para>
/// Why it fails silently: a refused reveal would leave the campaign contradicting what was said at the table, and a
/// warning lost between the service and the text would let a session-recap batch reveal the twist early with nothing for
/// the author to read. Each test builds its own world in its own server (a reveal changes what every later read sees).
/// </para>
/// </summary>
public sealed class ScenarioOnePieceGateTests : IAsyncLifetime
{
    private const string Warnings = "Applied anyway; read them before the table does.\n";

    private readonly McpServerHarness _server = new();
    private ScenarioOnePieceWorld _w = null!;

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        _w = await ScenarioOnePieceWorld.BuildAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    /// <summary>
    /// Row 9: after T3 (both routes complete, the axe not assembled) the seal revealed to the party in session 10 is
    /// written, and the result warns the unmet after (the axe only: holding a fruit and a shard is met) and the unmet with
    /// (Nadar's plan), with the gate's note once and the advisory prefer last; the secret is revealed; and the batch's
    /// history keeps every warning in its reason.
    /// </summary>
    [Fact]
    public async Task Reveal_Row9AfterT3BeforeTheAxe_IsWrittenWithTheAfterAndWithWarningsKeptInItsHistory()
    {
        await ThroughT3();

        var text = await _w.Call("campaign_knowledge", $$"""
            {"campaign": "one-piece", "action": "reveal", "facts": ["{{_w.Seal}}"], "to": ["party"], "session": 10, "reason": "The Peaceful One said too much"}
            """);

        Assert.Contains($"\n| {_w.Seal} | party | created | state |\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Warnings (4)\n" + Warnings + After(10) + With() + Forbidden(_w.Seal) + Prefer() + "\n## Consequences (1)\n" +
                        "- secret status: secret:fruits-are-the-seal: partial → revealed.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_w.Holds, text, StringComparison.Ordinal);
        Assert.Equal("knows (S10)", await _w.Cell(_w.Seal));
        var history = await _w.Call("campaign_history", $$"""{"campaign": "one-piece", "action": "batch", "batch_id": "{{ScenarioCalls.BatchId(text)}}"}""");
        Assert.Contains($"\nreason: The Peaceful One said too much; warning: {AfterMessage(10)}; warning: {WithMessage()}; warning: {PreferMessage()}\n",
            history, StringComparison.Ordinal);
    }

    /// <summary>Row 10: the same reveal as a dry run gives the same warnings and consequence, writes nothing and leaves no batch.</summary>
    [Fact]
    public async Task Reveal_Row10DryRun_GivesTheSameWarningsWritesNothingAndLeavesNoBatch()
    {
        await ThroughT3();
        var batches = await _w.BatchCount();

        var text = await _w.Reveal(10, dryRun: true, _w.Seal);

        Assert.StartsWith("# Dry run: campaign_knowledge reveal, 1 row (one-piece): nothing written\n\n**Dry run: nothing was written and no batch exists.**",
            text, StringComparison.Ordinal);
        Assert.Contains("\n## Warnings (4)\n" + Warnings + After(10) + With() + Forbidden(_w.Seal) + Prefer() + "\n## Consequences (1)\n" +
                        "- secret status: secret:fruits-are-the-seal: partial → revealed.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign_history", text, StringComparison.Ordinal);
        Assert.Equal("no record", await _w.Cell(_w.Seal));
        Assert.Contains("\nsecret · status partial\n", await _w.SecretPage(), StringComparison.Ordinal);
        Assert.Equal(batches, await _w.BatchCount());
    }

    /// <summary>
    /// Rows 9, 11 and 23 through either tool (one gate evaluator serves both, contract §3.4): after T2 only (one route
    /// complete, the axe not assembled, Nadar's plan not told) the seal reaching the party in session 10, by a
    /// <c>campaign_knowledge reveal</c> or by a <c>campaign_write</c> fact op's <c>known_by</c>, is written, and the warnings
    /// name all three hard conditions (after, with, routes by id) and the advisory, word for word the same in both tools but
    /// for campaign_write's op number.
    /// </summary>
    [Theory]
    [InlineData("campaign_knowledge")]
    [InlineData("campaign_write")]
    public async Task Reveal_Rows9And11And23BeforeTheGateThroughEitherTool_IsWrittenWithWarningsNamingAfterWithAndRoutes(string tool)
    {
        await _w.T1();
        await _w.T2();

        var text = tool == "campaign_knowledge"
            ? await _w.Reveal(10, dryRun: false, _w.Seal)
            : await _w.Call("campaign_write", $$"""
                {"campaign": "one-piece", "session": 10, "ops": [{"op": "fact", "ref": "{{_w.Seal}}", "known_by": [{"who": "party", "state": "knows"}]}]}
                """);

        var op = tool == "campaign_write" ? " · ops item 1" : string.Empty;
        Assert.Contains("\n## Warnings (5)\n" + Warnings + After(10, op) + With(op) + Routes(10, "1 of 2", "illusion", op) + Forbidden(_w.Seal, op) + Prefer(op),
            text, StringComparison.Ordinal);
        Assert.Contains("\n## Consequences (1)\n- secret status: secret:fruits-are-the-seal: partial → revealed.\n", text, StringComparison.Ordinal);
        Assert.Equal("knows (S10)", await _w.Cell(_w.Seal));
        Assert.Contains("\nsecret · status revealed\n", await _w.SecretPage(), StringComparison.Ordinal);
    }

    /// <summary>Row 12: after T4 (the axe assembled) the seal alone in session 12 warns only that it must land with Nadar's plan (and the advisory).</summary>
    [Fact]
    public async Task Reveal_Row12AfterT4TheSealAlone_OnlyTheWithCouplingWarns()
    {
        await _w.ThroughT4();

        var text = await _w.Reveal(12, dryRun: false, _w.Seal);

        Assert.Contains("\n## Warnings (2)\n" + Warnings + With(note: true) + Prefer(), text, StringComparison.Ordinal);
        Assert.DoesNotContain("gate (after)", text, StringComparison.Ordinal);
        Assert.Equal("knows (S12)", await _w.Cell(_w.Seal));
    }

    /// <summary>
    /// Row 12's converse, where "after is judged at the session the reveal is filed under" bites (contract §3.4: in play
    /// at session n): after T4 (the axe assembled in session 11) a late recap revealing the seal into session 10, before
    /// the axe was in play, still warns the unmet after (with the gate's note) and the with, and is applied in session 10.
    /// A gate that judged after by the axe's state now would find it met and say nothing.
    /// </summary>
    [Fact]
    public async Task Reveal_IntoSession10AfterT4_WarnsTheAfterUnmetAtThatSessionAndIsApplied()
    {
        await _w.ThroughT4();

        var text = await _w.Reveal(10, dryRun: false, _w.Seal);

        Assert.Contains("\n## Warnings (3)\n" + Warnings + After(10) + With() + Prefer() + "\n## Consequences (1)\n" +
                        "- secret status: secret:fruits-are-the-seal: partial → revealed.\n", text, StringComparison.Ordinal);
        Assert.Equal("knows (S10)", await _w.Cell(_w.Seal));
    }

    /// <summary>Row 13: the coupling is symmetric: Nadar's plan alone warns that it must land with the seal, under its own gate's note.</summary>
    [Fact]
    public async Task Reveal_Row13NadarsPlanAlone_WarnsTheCouplingTheOtherWay()
    {
        await _w.ThroughT4();

        var text = await _w.Reveal(12, dryRun: false, _w.NadarPlan);

        Assert.Contains("\n## Warnings (1)\n" + Warnings +
                        $"- **warning** · gate (with) · {_w.NadarPlan} → party: {_w.NadarPlan} must land with {_w.Seal} (same knower, same session): " +
                        $"{_w.Seal} has not reached party (applied anyway).\n  - Gate note: the two land together or not at all (CC:177-178)\n", text, StringComparison.Ordinal);
        Assert.Equal("knows (S12)", await _w.Cell(_w.NadarPlan));
    }

    /// <summary>Row 14: "land together" means the same session: Nadar's plan told in session 8 and the seal in 12 landed separately, and says both sessions.</summary>
    [Fact]
    public async Task Reveal_Row14NadarsPlanToldInSession8_TheSealInSession12LandedSeparately()
    {
        await _w.ThroughT4();
        await _w.Reveal(8, dryRun: false, _w.NadarPlan);

        var text = await _w.Reveal(12, dryRun: false, _w.Seal);

        Assert.Contains($"\n- **warning** · gate (with) · {_w.Seal} → party: {_w.Seal} must land with {_w.NadarPlan} (same knower, same session): " +
                        $"landed separately: {_w.NadarPlan} in S8, {_w.Seal} in S12 (applied anyway).\n", text, StringComparison.Ordinal);
        Assert.Equal(("knows (S8)", "knows (S12)"), (await _w.Cell(_w.NadarPlan), await _w.Cell(_w.Seal)));
    }

    /// <summary>
    /// Row 15: T5 after T4 (the gate ready, both facts in one call during the live session 12) warns nothing but the
    /// advisory prefer, under the gate's note, and the secret becomes revealed.
    /// </summary>
    [Fact]
    public async Task Reveal_Row15T5AfterT4_WarnsOnlyTheAdvisoryAndRevealsTheSecret()
    {
        await _w.ThroughT4();

        var text = await _w.T5();

        Assert.Contains($"\n| {_w.Seal} | party | created | state |\n| {_w.NadarPlan} | party | created | state |\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Warnings (1)\n" + Warnings + Prefer() + $"  - Gate note: {ScenarioOnePieceWorld.GateNote}\n\n## Consequences (1)\n" +
                        "- secret status: secret:fruits-are-the-seal: partial → revealed.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("**warning**", text, StringComparison.Ordinal);
        Assert.Equal(("knows (S12)", "knows (S12)"), (await _w.Cell(_w.Seal), await _w.Cell(_w.NadarPlan)));
    }

    /// <summary>
    /// Row 20: the call that completes the second route (T3, in the live session 10) is applied and warns that the secret
    /// is reachable before its gate (2 of 2 routes, the axe unmet); T1, T2 and T4 (before it and after the gate opens) do
    /// not, and neither does a later clue recorded while the state still holds.
    /// </summary>
    [Fact]
    public async Task Record_Row20T3CompletesTheSecondRoute_WarnsReachableBeforeTheGateOnThatCallAlone()
    {
        var t1 = await _w.T1();
        var t2 = await _w.T2();
        var t3 = await _w.T3();
        var later = await _w.Call("campaign_knowledge", $$"""
            {"campaign": "one-piece", "action": "record", "targets": ["{{_w.TPeaceful}}"], "knowers": [{"who": "party"}], "session": 10}
            """);
        var t4 = await _w.T4();

        Assert.Contains("\n## Warnings (1)\n" + Warnings + "- **warning** · reachable before gate: secret:fruits-are-the-seal is reachable before the gate: " +
                        $"2 of 2 needed routes to {_w.Seal} are complete while its after is unmet ({_w.Axe}); the party can work it out before the story is ready for it.\n",
            t3, StringComparison.Ordinal);
        Assert.All(new[] { t1, t2, later, t4 }, text => Assert.DoesNotContain("reachable before", text, StringComparison.Ordinal));
        Assert.Equal(("knows (S10)", "knows (S10)"), (await _w.Cell(_w.BreachesClimbing), await _w.Cell(_w.FruitAppearancesFalling)));
    }

    /// <summary>
    /// Row 23: T5 with only T1 and T2 played (one route, the axe not assembled) is still applied, warning the unmet after
    /// and too few routes (1 of 2: illusion), and the secret is revealed: the table is the source of truth.
    /// </summary>
    [Fact]
    public async Task Reveal_Row23T5WithOnlyOneRoute_IsAppliedWarningTheAfterAndTooFewRoutes()
    {
        await _w.T1();
        await _w.T2();

        var text = await _w.T5();

        Assert.Contains("\n## Warnings (5)\n" + Warnings + After(12) + Routes(12, "1 of 2", "illusion") + Forbidden(_w.Seal) + Forbidden(_w.NadarPlan) + Prefer(),
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("gate (with)", text, StringComparison.Ordinal);
        Assert.Equal(("knows (S12)", "knows (S12)"), (await _w.Cell(_w.Seal), await _w.Cell(_w.NadarPlan)));
        Assert.Contains("\nsecret · status revealed\n", await _w.SecretPage(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Rows 39 and 40 after the whole scenario: the party finds the two facts it was told in T5 by their words, but not the
    /// author-only secret itself, and still never the Protector by his name (session 1's title and T1 are in the party's
    /// words, which is why the world writes them so).
    /// </summary>
    [Fact]
    public async Task Search_Rows39And40AfterT5_ThePartyFindsTheToldFactsButNeitherTheSecretNorTheProtector()
    {
        await _w.ThroughT4();
        await _w.T5();

        var seal = await _w.Call("campaign_search", """{"campaign": "one-piece", "perspective": "party", "query": "seal"}""");
        var protector = await _w.Call("campaign_search", """{"campaign": "one-piece", "perspective": "party", "query": "protector"}""");
        var knowledge = await _w.Read("campaign://one-piece/knowledge/party");

        Assert.Contains($"\n## Facts\n1. `{_w.Seal}`: Eating a devil fruit breaks part of Baal's seal.\n2. `{_w.NadarPlan}`: Nadar routes fruits", seal, StringComparison.Ordinal);
        Assert.DoesNotContain("## Entities", seal, StringComparison.Ordinal);
        Assert.DoesNotContain("fruits-are-the-seal", seal, StringComparison.Ordinal);
        Assert.EndsWith("\n\nNothing matches. Try fewer or different words, a prefix such as \"sorcer*\", or leave query out to list by kinds.\n", protector,
            StringComparison.Ordinal);
        Assert.Contains($"\n- `{_w.TProtector}`: {ScenarioOnePieceWorld.TestimonyAsThePartyHeardIt} · knows · S8\n", knowledge, StringComparison.Ordinal);
        Assert.DoesNotContain("Protector", knowledge, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Row 40's "the secret entity is still not returned", where it bites: revealing the secret itself (its gated fact and
    /// the party's awareness of the secret) after T4 tells the party the seal, while the secret, author-only, stays unseen
    /// by every player read (contract §3.2: author visibility is absolute, whatever rows it has), and the write says so.
    /// </summary>
    [Fact]
    public async Task Reveal_Row40TheSecretItselfAfterT4_TellsTheSealButTheAuthorOnlySecretStaysUnseen()
    {
        await _w.ThroughT4();

        var text = await _w.Call("campaign_knowledge", $$"""
            {"campaign": "one-piece", "action": "reveal", "secret": "{{ScenarioOnePieceWorld.Secret}}", "to": ["party"], "session": 12}
            """);
        var search = await _w.Call("campaign_search", """{"campaign": "one-piece", "perspective": "party", "query": "seal"}""");
        var listing = await _w.Call("campaign_search", """{"campaign": "one-piece", "perspective": "party", "kinds": ["secret"]}""");

        Assert.Contains($"\n| {_w.Seal} | party | created | state |\n| {ScenarioOnePieceWorld.Secret} | party | created | state |\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n- **warning** · author visibility: {ScenarioOnePieceWorld.Secret} has author visibility: the row for party is stored, but author " +
                        "visibility hides it from every view but the author's regardless of who knows it; give it visibility restricted to let its knowers " +
                        "see it.\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n## Facts\n1. `{_w.Seal}`: Eating a devil fruit breaks part of Baal's seal.\n", search, StringComparison.Ordinal);
        Assert.DoesNotContain("## Entities", search, StringComparison.Ordinal);
        Assert.EndsWith("\n\nNothing matches these filters.\n", listing, StringComparison.Ordinal);
        Assert.Equal(
            $"An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"{ScenarioOnePieceWorld.Secret}\": nothing by that handle for this perspective. " +
            "campaign_search finds entities and facts by name.",
            await _w.Fail("campaign_get", $$"""{"campaign": "one-piece", "perspective": "party", "refs": ["{{ScenarioOnePieceWorld.Secret}}"]}"""));
    }

    private async Task ThroughT3()
    {
        await _w.T1();
        await _w.T2();
        await _w.T3();
    }

    private string AfterMessage(int session) =>
        $"{_w.Seal} reached party in S{session} before its gate's after is met: {_w.Axe} not in play yet (applied anyway).";

    private string WithMessage() =>
        $"{_w.Seal} must land with {_w.NadarPlan} (same knower, same session): {_w.NadarPlan} has not reached party (applied anyway).";

    private string PreferMessage() =>
        $"Advisory: {_w.Seal}'s gate would rather {_w.NoUneatenFruits} were in play first (a \"probably\", not a condition).";

    // The gate's note is printed once, under the first warning about the gate.
    private string After(int session, string op = "") =>
        $"- **warning** · gate (after){op} · {_w.Seal} → party: {AfterMessage(session)}\n  - Gate note: {ScenarioOnePieceWorld.GateNote}\n";

    private string With(string op = "", bool note = false) =>
        $"- **warning** · gate (with){op} · {_w.Seal} → party: {WithMessage()}\n" + (note ? $"  - Gate note: {ScenarioOnePieceWorld.GateNote}\n" : string.Empty);

    private string Routes(int session, string complete, string ids, string op = "") =>
        $"- **warning** · gate (routes){op} · {_w.Seal} → party: {_w.Seal} reached party in S{session} with {complete} needed routes complete ({ids}) (applied anyway).\n";

    private string Prefer(string op = "") => $"- **advisory** · gate (prefer){op} · {_w.Seal} → party: {PreferMessage()}\n";

    // The write-time player-text check (review L12): a statement the party now reads uses "seal" while the gate's
    // vocabulary rule holds (the axe not in play). The seal's own statement counts too: contract §3.4 keeps the word out of
    // every non-author text while the rule is active, as campaign_knowledge check does, and the remedy (the party's own
    // phrasing as known_as) is the one the table needs.
    private string Forbidden(string fact, string op = "") =>
        $"- **warning** · forbidden word{op}: {fact}'s statement uses \"seal\", forbidden by {_w.Seal}'s gate while it holds: " +
        "give known_as with the party's phrasing; say \"shell\" instead (or \"wrapping\", \"what keeps it in\").\n";
}
