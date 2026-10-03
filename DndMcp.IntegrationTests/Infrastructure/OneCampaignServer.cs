using Xunit;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>
/// Class fixture: the production server with one small campaign in its own data directory, made through the tools
/// themselves: <c>campaign create</c> (a player campaign, so it is this server's current campaign and has a player
/// character in the party) and one <c>campaign_write</c> batch (an NPC the party knows and a fact about him).
///
/// <para>
/// Why it exists: with no campaign, every campaign tool but <c>campaign</c> stops at "There are no campaigns yet", so a
/// surface test on an empty server could never reach a tool's own argument checks, its binding of null arguments, or a
/// prompt's character argument. Why a fixture of its own rather than a campaign written into the plain
/// <see cref="McpServerHarness"/> fixture: that server's empty data directory is itself pinned (ServerSurfaceTests' exact
/// resource list, the no-campaign error rows), and a campaign made current there would also become the edition default of
/// every rules and balance call in the class.
/// </para>
/// <para>
/// Tests that use it must not depend on what another test in the same class wrote: xUnit runs a class's tests one at a
/// time but in no fixed order, so a test that writes adds entries with names of its own and compares only results it
/// produced itself.
/// </para>
/// </summary>
public sealed class OneCampaignServer : IAsyncLifetime
{
    /// <summary>The campaign's slug (its name is "Surface"; player campaign, 2024 rules).</summary>
    public const string Slug = "surface";

    /// <summary>The player character <c>campaign create</c> made and put in the party.</summary>
    public const string Character = "character:aria-vale";

    /// <summary>An NPC the party knows (visibility party), with one fact about him the party knows.</summary>
    public const string Npc = "character:iron-guts";

    public McpServerHarness Harness { get; } = new();

    public async Task InitializeAsync()
    {
        await Harness.InitializeAsync();
        Harness.SuccessText(await Harness.CallToolJsonAsync("campaign",
            $$"""{"action": "create", "name": "Surface", "role": "player", "ruleset": "2024", "slug": "{{Slug}}", "my_character": "Aria Vale"}"""));
        Harness.SuccessText(await Harness.CallToolJsonAsync("campaign_write",
            """
            {"ops": [{"op": "upsert", "kind": "character", "name": "Iron Guts", "subtype": "npc", "visibility": "party"},
                     {"op": "fact", "statement": "Iron Guts owes the band a favour.", "about": ["character:iron-guts"], "known_by": [{"who": "party"}]}],
             "reason": "fixture"}
            """));
    }

    public Task DisposeAsync() => Harness.DisposeAsync();
}
