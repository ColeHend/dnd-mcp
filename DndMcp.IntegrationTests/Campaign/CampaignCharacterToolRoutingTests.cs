using System.Collections.Concurrent;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant (contract D5): <c>campaign_character</c> hands the <see cref="ICombatRouter"/> the container registers to its
/// writer, so while the character is a sheet-seeded combatant of the live fight, damage, heal, temp_hp, use and condition
/// are "Applied to the live fight …" with NO batch id and no undo line (nothing was logged; the sheet is written when the
/// fight ends) and the sheet is unchanged; a character in the fight from a stat block is changed on the sheet in one
/// batch with the note that the fight was not; a rest is refused; update and the other actions act on the sheet without
/// asking the router. With no router registered (the production registration until the combat layer adds one) every action
/// acts on the sheet, which every other campaign_character test runs.
///
/// <para>
/// Why it fails silently: a tool constructed without the registered router would apply every in-fight change to the sheet,
/// where the fight's write-back then overwrites it, and an undo line printed for a routed call names a batch that does not
/// exist. The router here is a fake that touches no table: the real one (the combat layer's) is pinned by its own tests.
/// </para>
/// </summary>
public sealed class CampaignCharacterToolRoutingTests : IAsyncLifetime
{
    private readonly FakeRouter _router = new();
    private readonly McpServerHarness _server;

    public CampaignCharacterToolRoutingTests()
    {
        _server = McpServerHarness.WithExtraTools(builder => builder.Services.AddSingleton<ICombatRouter>(_router));
    }

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await CharacterToolSetup.CreateSkyAsync(_server);
        await CharacterToolSetup.BelmakorAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Call(string argumentsJson) => ScenarioCalls.Call(_server, "campaign_character", argumentsJson);

    [Theory]
    [InlineData("""{"action": "damage", "amount": 14, "damage_type": "fire"}""", "damage")]
    [InlineData("""{"action": "heal", "amount": 5}""", "heal")]
    [InlineData("""{"action": "temp_hp", "amount": 9}""", "temp_hp")]
    [InlineData("""{"action": "use", "resource": "Bladesong"}""", "use")]
    [InlineData("""{"action": "condition", "add": ["poisoned"]}""", "condition")]
    public async Task RoutableAction_SheetSeededInTheLiveFight_IsAppliedToTheFightWithNoBatch(string argumentsJson, string kind)
    {
        _router.Outcome = RouteOutcomes.Applied;
        var before = await Call("""{"action": "get"}""");

        var text = await Call(argumentsJson);

        Assert.StartsWith(
            $"# campaign_character {kind}: Belmakor (`character:belmakor`, sky)\n\n" +
            "Applied to the live fight The crypt; written to the sheet when it ends.\nBelmakor: the fight's own line.\n",
            text, StringComparison.Ordinal);
        Assert.Contains("\n## Reminders\n- Concentration: a fake reminder. combat {\"action\": \"state\", \"campaign\": \"sky\"}\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", text, StringComparison.Ordinal);
        Assert.DoesNotContain("To undo it", text, StringComparison.Ordinal);
        Assert.Equal(before, await Call("""{"action": "get"}"""));
        Assert.Equal(kind, Assert.Single(_router.Asked).Kind);
    }

    [Fact]
    public async Task Condition_Routed_KeepsTheSheetsDefaultDurationUntilRemoved()
    {
        _router.Outcome = RouteOutcomes.Applied;

        await Call("""{"action": "condition", "add": ["poisoned"], "remove": ["concentration"]}""");

        var asked = Assert.Single(_router.Asked);
        Assert.Equal(("condition", "until_removed"), (asked.Kind, asked.Duration));
        Assert.Equal(["poisoned"], asked.Add!);
        Assert.Equal(["concentration"], asked.Remove!);
    }

    [Fact]
    public async Task Damage_InTheFightFromAStatBlock_ChangesTheSheetInOneBatchAndSaysTheFightWasNot()
    {
        _router.Outcome = RouteOutcomes.StatBlock;

        var text = await Call("""{"action": "damage", "amount": 14}""");

        ScenarioCalls.BatchId(text);
        Assert.Contains("- hp: 110 → 103\n", text, StringComparison.Ordinal);
        Assert.Contains("is in the live fight as Lich from a stat block: this changed the sheet, not the fight (use combat to change the fight).", text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RouteOutcomes.Applied)]
    [InlineData(RouteOutcomes.StatBlock)]
    public async Task Rest_InTheLiveFight_IsRefusedAndNothingIsWritten(string outcome)
    {
        _router.Outcome = outcome;
        var before = await Call("""{"action": "get"}""");

        var text = await ScenarioCalls.Fail(_server, "campaign_character", """{"action": "rest", "kind": "long"}""");

        Assert.Equal(
            $"An error occurred invoking 'campaign_character': Belmakor is in the live fight \"The crypt\" as {(outcome == RouteOutcomes.Applied ? "Belmakor" : "Lich")}: " +
            "rest once it ends (combat {\"action\": \"end\", \"campaign\": \"sky\"} ends it).",
            text);
        Assert.Equal(before, await Call("""{"action": "get"}"""));
    }

    [Theory]
    [InlineData("""{"action": "update", "sheet": {"ac": 18}}""")]
    [InlineData("""{"action": "xp", "amount": 300}""")]
    [InlineData("""{"action": "level_up"}""")]
    [InlineData("""{"action": "inventory", "items": [{"item": "Rope"}]}""")]
    [InlineData("""{"action": "currency", "coins": {"gp": 3}}""")]
    public async Task SheetAction_DuringTheFight_ActsOnTheSheetWithoutAskingTheRouter(string argumentsJson)
    {
        _router.Outcome = RouteOutcomes.Applied;

        var text = await Call(argumentsJson);

        ScenarioCalls.BatchId(text);
        Assert.Empty(_router.Asked);
    }

    [Fact]
    public async Task Damage_NotInTheFight_IsTheSheetsAsWithNoRouter()
    {
        _router.Outcome = null;

        var text = await Call("""{"action": "damage", "amount": 14}""");

        ScenarioCalls.BatchId(text);
        Assert.Contains("- hp: 110 → 103\n", text, StringComparison.Ordinal);
        Assert.Single(_router.Asked);
    }

    /// <summary>
    /// A router that reports the configured outcome for every character (null: not in a fight) and records what it was
    /// asked; it writes nothing, so the sheet shows exactly what the writer did.
    /// </summary>
    private sealed class FakeRouter : ICombatRouter
    {
        public string? Outcome { get; set; }

        public ConcurrentQueue<CharacterAction> Asked { get; } = new();

        public bool TryRoute(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, string characterEntityId,
            CharacterAction action, out RoutedResult result)
        {
            Asked.Enqueue(action);
            result = new RoutedResult(Outcome ?? RouteOutcomes.InFight, "The crypt", Outcome == RouteOutcomes.Applied ? "Belmakor" : "Lich",
                ["Belmakor: the fight's own line."], [new RoutedReminder("concentration_save", "Concentration: a fake reminder.", "combat {\"action\": \"state\"}")]);
            return Outcome is not null;
        }
    }
}
