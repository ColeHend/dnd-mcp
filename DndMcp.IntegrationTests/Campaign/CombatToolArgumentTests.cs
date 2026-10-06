using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>combat</c>'s seventeen actions take exactly the arguments of contract §6.0 (each refuses any other, saying
/// what it takes and giving its example), and that refusal comes FIRST: before the campaign is resolved, a monster looked
/// up or a die rolled, so a wrong argument never costs a lookup, a roll or a campaigns.db. The description and the
/// parameter descriptions carry what the schema cannot (the action lines, face vs total, the critical rule, "a list, even
/// for one", the durations).
///
/// <para>
/// The server here has no campaigns.db and a scripted roller with no faces: anything resolved, looked up or rolled before
/// the refusal would answer with another message (no campaigns, an unknown monster, the roller's own error).
/// </para>
/// </summary>
public sealed class CombatToolArgumentTests : IClassFixture<CombatToolArgumentTests.EmptyServer>
{
    private const string NoCampaigns =
        "An error occurred invoking 'combat': There are no campaigns yet. Create one with campaign {\"action\": \"create\", \"name\": \"…\", " +
        "\"role\": \"player\" or \"dm\", \"ruleset\": \"2024\"}.";

    private readonly McpServerHarness _server;

    public CombatToolArgumentTests(EmptyServer fixture)
    {
        _server = fixture.Harness;
    }

    /// <summary>A server with no campaigns.db and dice that throw if rolled.</summary>
    public sealed class EmptyServer : IAsyncLifetime
    {
        public McpServerHarness Harness { get; } = McpServerHarness.WithExtraTools(builder =>
            builder.Services.AddSingleton<IDiceRoller>(new ScriptedDiceRoller()));

        public Task InitializeAsync() => Harness.InitializeAsync();

        public Task DisposeAsync() => Harness.DisposeAsync();
    }

    private Task<string> Refused(string argumentsJson) => ScenarioCalls.Fail(_server, "combat", argumentsJson);

    [Theory]
    [InlineData("prepare", "name, lair, edition, combatants, campaign")]
    [InlineData("start", "name, add_party, lair, edition, combatants, surprised, campaign, encounter")]
    [InlineData("add", "combatants, campaign, encounter")]
    [InlineData("set", "combatants, campaign, encounter")]
    [InlineData("leave", "targets, campaign, encounter")]
    [InlineData("initiative", "rolls, surprised, secret, campaign, encounter")]
    [InlineData("next", "from, campaign, encounter")]
    [InlineData("prev", "campaign, encounter")]
    [InlineData("damage", "targets, amount, dice, parts, damage_type, critical, magical, half, raw, knock_out, source, secret, campaign, encounter")]
    [InlineData("heal", "targets, amount, dice, temp, item, source, secret, campaign, encounter")]
    [InlineData("condition", "targets, add, remove, duration, source, dc, ability, level, round, effect, resource, campaign, encounter")]
    [InlineData("concentration", "targets, spell, slot_level, duration, drop, total, face, secret, campaign, encounter")]
    [InlineData("use", "targets, slot_level, pact, resource, item, amount, campaign, encounter")]
    [InlineData("legendary", "source, amount, name, resistance, campaign, encounter")]
    [InlineData("death_save", "targets, face, total, stable, secret, campaign, encounter")]
    [InlineData("state", "perspective, campaign, encounter")]
    [InlineData("end", "outcome, xp, loot, currency, discard, force, dry_run, reason, campaign, encounter")]
    public async Task Takes_EachAction_IsContractSection6_0(string action, string takes)
    {
        // An argument only end takes (outcome), or for end one only state takes (perspective), draws the refusal that lists
        // what the action takes.
        var stranger = action == "end" ? "perspective" : "outcome";

        var text = await Refused($$"""{"action": "{{action}}", "{{stranger}}": "x"}""");

        Assert.StartsWith($"An error occurred invoking 'combat': combat {action} does not take \"{stranger}\"; {action} takes {takes}. Example: {{\"action\": \"{action}\"",
            text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "campaigns.db")));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("start")]
    [InlineData("add")]
    [InlineData("set")]
    [InlineData("leave")]
    [InlineData("initiative")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("damage")]
    [InlineData("heal")]
    [InlineData("condition")]
    [InlineData("concentration")]
    [InlineData("use")]
    [InlineData("legendary")]
    [InlineData("death_save")]
    [InlineData("state")]
    [InlineData("end")]
    public async Task Example_EachActionsExample_PassesTheGuardAndItsActionsCheck(string action)
    {
        // The example a refusal gives is the call the model copies next: every argument in it is one the action takes, so the
        // only thing left to say here (no campaigns.db) is that there are no campaigns.
        var stranger = action == "end" ? "perspective" : "outcome";
        var refusal = await Refused($$"""{"action": "{{action}}", "{{stranger}}": "x"}""");
        var example = refusal[(refusal.IndexOf("Example: ", StringComparison.Ordinal) + "Example: ".Length)..];

        Assert.Equal(action, JsonNode.Parse(example)!["action"]!.GetValue<string>());
        Assert.Equal(NoCampaigns, await Refused(example));
    }

    [Theory]
    // An unknown monster would be looked up, a perspective resolved, dice rolled, a campaign resolved: the refusal of the
    // argument the action does not take comes before every one of them.
    [InlineData("""{"action": "add", "combatants": [{"srd": "Beholder"}], "outcome": "x"}""", "combat add does not take \"outcome\"")]
    [InlineData("""{"action": "start", "name": "X", "combatants": [{"srd": "Beholder"}], "dc": 3}""", "combat start does not take \"dc\"")]
    [InlineData("""{"action": "damage", "targets": ["x"], "dice": "8d6", "perspective": "party"}""", "combat damage does not take \"perspective\"")]
    [InlineData("""{"action": "state", "perspective": "character:nobody", "dry_run": true}""", "combat state does not take \"dry_run\"")]
    [InlineData("""{"action": "initiative", "dice": "1d20", "surprised": ["x"]}""", "combat initiative does not take \"dice\"")]
    [InlineData("""{"action": "prepare", "encounter": "x", "name": "y"}""", "combat prepare does not take \"encounter\"")]
    [InlineData("""{"action": "heal", "targets": ["x"], "resource": "Rage", "half": ["x"]}""", "combat heal does not take \"half\" or \"resource\"")]
    public async Task Refusal_ComesBeforeAnythingIsResolvedLookedUpOrRolled(string call, string refusal)
    {
        var text = await Refused(call);

        Assert.StartsWith("An error occurred invoking 'combat': " + refusal, text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "campaigns.db")));
    }

    [Fact]
    public async Task Description_ListsEveryActionWithItsArgumentsAndStaysWithinTheLimit()
    {
        var description = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "combat").Description!;

        Assert.InRange(description.Length, 1, 2_048);
        Assert.EndsWith("\nExample: " + Tools.CombatTools.Example, description, StringComparison.Ordinal);
        foreach (var line in new[]
                 {
                     "The server rolls what you don't give.",
                     "HP ticks stay out of campaign_history; end writes the sheets back as one undoable batch.",
                     "\n- leave: targets (out of the fight).\n",
                     "\n- heal: targets, amount or dice, temp (temporary HP), item, source.\n",
                     "resistance (Legendary Resistance).\n",
                     "\n- legendary: source (the legendary creature), amount, name;",
                     "\n- death_save: targets, face or total, stable.\n",
                     ", resource.\n- concentration:",
                     "\nEvery action takes campaign; all but prepare take encounter (default \"current\"). damage, heal, initiative, concentration and death_save take secret.\n",
                 })
        {
            Assert.Contains(line, description, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("targets", "A list, even for one.")]
    [InlineData("dice", "Give the normal dice; critical doubles them.")]
    [InlineData("critical", "doubles the dice of a server roll (a given amount is taken as given)")]
    [InlineData("face", "the d20 as rolled")]
    [InlineData("total", "the save's final number")]
    [InlineData("rolls", "everyone else without one is rolled")]
    [InlineData("duration", "1 minute, 10 minutes, 3 rounds, save ends, until escape, end of round 2, until removed, fight")]
    [InlineData("add_party", "Default true.")]
    [InlineData("xp", "Default: the defeated and left enemies' XP when a party sheet tracks XP; 0 for none.")]
    [InlineData("perspective", "Other views see only the party board.")]
    [InlineData("item", "the source's, else the target's")]
    [InlineData("resource", "a resource each target spends as the effect starts")]
    // Fix F2, review LR03: "default the turn-holder" led to a legendary action's damage with no source, logged as the PC's
    // roll whose turn had just ended (open in a DM campaign).
    [InlineData("source", "damage, heal: who rolled it; give it for any roll made outside that creature's own turn (legendary actions, reactions, " +
        "opportunity attacks): without it, damage belongs to the creature whose turn it is.")]
    public async Task ParameterDescriptions_CarryTheGlossesTheToolDescriptionLeavesOut(string parameter, string gloss)
    {
        var schema = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "combat").JsonSchema;

        Assert.Contains(gloss, schema.GetProperty("properties").GetProperty(parameter).GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitiativeTotal_NotAFiniteNumber_IsRefusedByPosition()
    {
        // JSON has no NaN; the guard reads numeric strings as numbers only when they are finite, so the tool's own check is
        // the backstop and names the item.
        var text = await Refused("""{"action": "initiative", "rolls": [{"combatant": "vars", "total": "NaN"}]}""");

        Assert.Contains("rolls", text, StringComparison.Ordinal);
        Assert.DoesNotContain("There are no campaigns", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownAction_ListsTheSeventeenInTheContractsOrder()
    {
        var text = await Refused("""{"action": "attack"}""");

        Assert.StartsWith(
            "An error occurred invoking 'combat': action \"attack\" is not a combat action; give prepare, start, add, set, leave, initiative, next, prev, " +
            "damage, heal, condition, concentration, use, legendary, death_save, state, end. Example: ",
            text, StringComparison.Ordinal);
        Assert.Matches(new Regex("""Example: \{"action": "damage", """), text);
    }

    // Words of the fixtures' fights (fixture A's crypt): an example printed in every campaign's refusals must name none of
    // them (fix F1, X2-N: a One Piece refusal once told the model to damage "torch" in "belmakor").
    private static readonly string[] FixtureWords = ["belmakor", "torch", "vars", "mummy", "crypt", "bladesong", "circle of power", "serif", "aiden"];

    [Theory]
    [InlineData("prepare")]
    [InlineData("start")]
    [InlineData("add")]
    [InlineData("set")]
    [InlineData("leave")]
    [InlineData("initiative")]
    [InlineData("next")]
    [InlineData("prev")]
    [InlineData("damage")]
    [InlineData("heal")]
    [InlineData("condition")]
    [InlineData("concentration")]
    [InlineData("use")]
    [InlineData("legendary")]
    [InlineData("death_save")]
    [InlineData("state")]
    [InlineData("end")]
    public async Task Example_EachActionsExample_NamesNoFixturesCreatureAndAnyCampaignLast(string action)
    {
        // The refusal comes before the campaign is resolved, so its example cannot use the campaign's own handles: it names
        // placeholders (<name>, character:<slug>) and, when it names a campaign, names it last, as every printed call does.
        var stranger = action == "end" ? "perspective" : "outcome";
        var refusal = await Refused($$"""{"action": "{{action}}", "{{stranger}}": "x"}""");

        AssertNeutral(refusal[(refusal.IndexOf("Example: ", StringComparison.Ordinal) + "Example: ".Length)..]);
    }

    [Fact]
    public async Task Example_TheDescriptionsAndTheUnknownActions_IsNeutralWithTheCampaignLast()
    {
        var description = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "combat").Description!;
        var refusal = await Refused("""{"action": "attack"}""");

        Assert.EndsWith(", \"campaign\": \"<slug>\"}", Tools.CombatTools.Example, StringComparison.Ordinal);
        AssertNeutral(Tools.CombatTools.Example);
        AssertNeutral(description[(description.IndexOf("\nExample: ", StringComparison.Ordinal) + "\nExample: ".Length)..]);
        AssertNeutral(refusal[(refusal.IndexOf("Example: ", StringComparison.Ordinal) + "Example: ".Length)..]);
    }

    private static void AssertNeutral(string example)
    {
        foreach (var word in FixtureWords)
        {
            Assert.DoesNotContain(word, example, StringComparison.OrdinalIgnoreCase);
        }

        var keys = JsonNode.Parse(example)!.AsObject().Select(p => p.Key).ToList();
        Assert.True(!keys.Contains("campaign") || keys[^1] == "campaign", example);
    }

    [Fact]
    public async Task Description_SetLine_NamesEveryFieldSetChanges()
    {
        // Fix F1, C15: set also takes death_saves and max_hp_reduction (the latter set only); the line names them, within the
        // 2,048-character budget (add_party's default lives in its parameter description, as every default does).
        var description = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "combat").Description!;

        Assert.Contains("\n- set: combatants already in (hp, ac, init_bonus, side, hidden, death_saves, max_hp_reduction).\n", description, StringComparison.Ordinal);
        Assert.Contains("\n- start: name or encounter (a prepared fight), add_party, lair, edition, combatants, surprised.\n", description, StringComparison.Ordinal);
        Assert.InRange(description.Length, 1, 2_048);
    }

    [Fact]
    public async Task Surface_NoTextOffersAWayToPauseAFight()
    {
        // paused is a reserved status no action sets (contract §6.0, fix F1): nothing the model reads may promise a pause.
        var tool = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "combat");
        var parameters = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Value.GetRawText());

        Assert.DoesNotContain("pause", tool.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.All(parameters, p => Assert.DoesNotContain("pause", p, StringComparison.OrdinalIgnoreCase));
    }
}
