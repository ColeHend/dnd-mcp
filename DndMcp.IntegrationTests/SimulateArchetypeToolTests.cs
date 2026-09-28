using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: a party of named archetypes (<c>ArchetypeCatalog</c>, Phase 5 stage 2) reaches the simulator through the
/// tool and fights: the description's own example runs, and the contract's sanity fight — four level 5 archetypes against
/// three ogres — is won nearly always in both editions.
///
/// <para>
/// Kept apart from <see cref="SimulateToolTests"/> because it depends on the archetypes, which landed after the host was
/// written: these are the tests to check after merging the two. The host adds nothing for archetypes (the Domain expands
/// them), so a failure here is an archetype or merge problem, not a host one.
/// </para>
/// </summary>
public sealed class SimulateArchetypeToolTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public SimulateArchetypeToolTests(McpServerHarness server)
    {
        _server = server;
    }

    [Fact]
    public async Task CallTool_TheDescriptionsExample_Runs()
    {
        // The one example the model copies must work as written.
        var tool = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "balance_simulate");
        var example = tool.Description[(tool.Description.IndexOf("\nExample: ", StringComparison.Ordinal) + "\nExample: ".Length)..].Trim();

        var text = await SimulateToolTests.SimulateAsync(_server, example);

        Assert.StartsWith("# Fight simulation: Fighter ×2, Cleric, Wizard vs Ogre ×3\n", text, StringComparison.Ordinal);
        Assert.Contains("| archetype fighter (level 5, 2024) |", text, StringComparison.Ordinal);
        Assert.Contains("seed 42*", text, StringComparison.Ordinal);
    }

    [Theory]
    // Four level 5 archetypes (a fighter, rogue, cleric and wizard: Extra Attack, Sneak Attack, Spirit Guardians and
    // Fireball) against three ogres (CR 2 each, 450 XP; a "Moderate" 2024 fight for four level 5s, "Medium" by the 2014 DMG).
    // The archetypes' own Domain test sees ~100% wins in about two rounds; 97% and 2-4 rounds leave room for seed noise
    // and any tuning of the archetypes while still failing if the party or the ogres were wired wrong.
    [InlineData("2024")]
    [InlineData("2014")]
    public async Task CallTool_FourLevel5ArchetypesAgainstThreeOgres_ThePartyWinsNearlyAlways(string edition)
    {
        var party = string.Join(", ", new[] { "fighter", "rogue", "cleric", "wizard" }.Select(a => $$"""{"archetype": "{{a}}", "level": 5}"""));

        var text = await SimulateToolTests.SimulateAsync(
            _server, $$"""{"party": [{{party}}], "enemies": [{"monster": "ogre", "count": 3}], "edition": "{{edition}}", "seed": 5150}""");

        Assert.True(SimulateToolTests.Headline(text) >= 0.97, text);
        Assert.InRange(SimulateToolTests.MeanRounds(text), 1.5, 4.0);
        Assert.Contains($"| monster {edition}/monster/ogre |", text, StringComparison.Ordinal);
        Assert.Contains($"| archetype wizard (level 5, {edition}) |", text, StringComparison.Ordinal);
        Assert.True(text.Length < 8_000, $"{text.Length} characters: a typical result must stay under 8,000.");
    }
}
