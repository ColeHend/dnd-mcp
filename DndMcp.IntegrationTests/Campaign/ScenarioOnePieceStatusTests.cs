using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: understand-onepiece.md §3 rows 16-24, the secret's routes and status across the scenario, as the author
/// reads them on the secret's <c>campaign_get</c> page after each step played through the tools: hidden as of session 5
/// (before the seed), seeded at the baseline (the seed, learned in session 6) and after T1 (one testimony of four),
/// partial after T2 (the illusion route complete) and T3 (both routes, the axe still unmet: reachable before the gate),
/// partial and ready after T4 (the axe assembled; it must land with Nadar's plan; the forbidden words lifted), revealed
/// after T5. The page's stored status (written by the write path in the batch that changed the knowledge) and the status
/// derived from the gates now agree at every step, and the header line shows the same.
///
/// <para>
/// Why it fails silently: the stored status is what the summary and a player-facing prep show, the derived one what the
/// secret's page computes; if the write path stopped re-deriving (or a point-in-time read stopped replaying it), the two
/// would drift and each screen would look right on its own.
/// </para>
/// </summary>
public sealed class ScenarioOnePieceStatusTests : IAsyncLifetime
{
    private readonly McpServerHarness _server = new();
    private ScenarioOnePieceWorld _w = null!;

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        _w = await ScenarioOnePieceWorld.BuildAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    /// <summary>Rows 16-24 in one walk through the scenario, each step played through the tools.</summary>
    [Fact]
    public async Task Get_Rows16To24TheSecretAfterEachStep_GoesHiddenSeededPartialRevealed()
    {
        AssertSecret(await _w.SecretPage(asOf: 5), "hidden",
            $"not ready · after unmet: {_w.Axe} · routes 0/2 (testimonies 0/4, illusion 0/1, temple-arithmetic 0/2) · prefer unmet: {_w.NoUneatenFruits} · " +
            $"lands with {_w.NadarPlan} · forbidden words active", "row 16");
        AssertSecret(await _w.SecretPage(), "seeded",
            $"not ready · after unmet: {_w.Axe} · routes 0/2 (testimonies 0/4, illusion 0/1, temple-arithmetic 0/2) · prefer unmet: {_w.NoUneatenFruits} · " +
            $"lands with {_w.NadarPlan} · forbidden words active · seeded", "row 17");

        await _w.T1();
        AssertSecret(await _w.SecretPage(), "seeded",
            $"not ready · after unmet: {_w.Axe} · routes 0/2 (testimonies 1/4, illusion 0/1, temple-arithmetic 0/2) · prefer unmet: {_w.NoUneatenFruits} · " +
            $"lands with {_w.NadarPlan} · forbidden words active · seeded", "row 18");

        await _w.T2();
        AssertSecret(await _w.SecretPage(), "partial",
            $"not ready · after unmet: {_w.Axe} · routes 1/2 (testimonies 1/4, illusion 1/1 complete, temple-arithmetic 0/2) · " +
            $"prefer unmet: {_w.NoUneatenFruits} · lands with {_w.NadarPlan} · forbidden words active · seeded", "row 19");

        await _w.T3();
        AssertSecret(await _w.SecretPage(), "partial",
            $"not ready · after unmet: {_w.Axe} · routes 2/2 (testimonies 1/4, illusion 1/1 complete, temple-arithmetic 2/2 complete) · " +
            $"prefer unmet: {_w.NoUneatenFruits} · lands with {_w.NadarPlan} · forbidden words active · seeded · reachable before the gate", "row 21");

        await _w.T4();
        AssertSecret(await _w.SecretPage(), "partial",
            $"ready to reveal · routes 2/2 (testimonies 1/4, illusion 1/1 complete, temple-arithmetic 2/2 complete) · prefer unmet: {_w.NoUneatenFruits} · " +
            $"lands with {_w.NadarPlan} · seeded", "row 22");

        await _w.T5();
        AssertSecret(await _w.SecretPage(), "revealed",
            $"known to the party · routes 2/2 (testimonies 1/4, illusion 1/1 complete, temple-arithmetic 2/2 complete) · prefer unmet: {_w.NoUneatenFruits} · " +
            "seeded", "row 24");
    }

    /// <summary>
    /// Row 16 as a point-in-time read after the whole scenario: as of each session the page replays the stored status and
    /// derives the status from what the party knew then, and the two agree: each step's change is filed under the live
    /// session it was played in.
    /// </summary>
    [Fact]
    public async Task Get_Row16AsOfEachSessionAfterT5_FollowsTheSessionsEachStepWasPlayedIn()
    {
        await _w.ThroughT4();
        await _w.T5();

        foreach (var (session, status) in new[] { (5, "hidden"), (6, "seeded"), (8, "seeded"), (9, "partial"), (10, "partial"), (11, "partial"), (12, "revealed") })
        {
            var page = await _w.SecretPage(asOf: session);
            Assert.True(page.Contains($"\nsecret · status {status}\n", StringComparison.Ordinal) &&
                        page.Contains($"\n- secret status: {status} (stored), {status} (from its gates now)\n", StringComparison.Ordinal),
                $"as of session {session}:\n{page}");
        }
    }

    // The secret's page: the status in its header line, stored and derived alike, and its one gate's line.
    private void AssertSecret(string page, string status, string gate, string row)
    {
        Assert.True(page.Contains($"\nsecret · status {status}\n", StringComparison.Ordinal), $"{row}:\n{page}");
        Assert.True(page.Contains($"\n- secret status: {status} (stored), {status} (from its gates now)\n  - `{_w.Seal}`: {gate}\n", StringComparison.Ordinal),
            $"{row}:\n{page}");
    }
}
