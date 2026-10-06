using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant (fix F1, review U04; contract D5): while a character is a sheet-seeded combatant of the live fight, every
/// AUTHOR read of its sheet (<c>campaign_character get</c>, its DM-campaign list form, <c>campaign_get include sheet</c>
/// and <c>campaign://&lt;slug&gt;/party</c>) still prints the SHEET (D5: get acts on the sheet) and adds one line with
/// where the fight has the character now — hit points, conditions, concentration, what it has spent — and the
/// <c>combat state</c> call. No other view gets the line, and nothing does once the fight ends; a character that left the
/// fight keeps the line, marked "(left the fight)", since the fight still holds its sheet's state until it ends (fix F2,
/// review CR07).
///
/// <para>
/// Why it fails silently: D5 sends <c>campaign_character damage</c> to the fight, so a damage followed by a get read the
/// pre-fight HP with no word of the fight; the headless models answered right only because they also read
/// <c>combat state</c>, and every get was a wasted, misleading call.
/// </para>
/// </summary>
public sealed class CampaignCharacterToolLiveFightTests : IAsyncLifetime
{
    private const string Line =
        "In the live fight \"Reef\": HP 65/85, poisoned (Ogre), concentrating on Bless (level 1), 1st-level slots 1/2 left, Rage 3/4 left, " +
        "Potion of Healing 1 used; the sheet catches up when it ends: combat {\"action\": \"state\", \"campaign\": \"deep\"}";

    private readonly McpServerHarness _server = McpServerHarness.WithExtraTools(builder =>
        builder.Services.AddSingleton<IDiceRoller>(new ScriptedDiceRoller()));

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await CharacterToolSetup.CreateDeepAsync(_server);
        await Character("""{"action": "update", "character": "character:bjorn-mountainfell", "sheet": {"slots": [2], "resources": [{"name": "Rage", "max": 4, "recharge": "long_rest"}]}}""");
        await Character("""{"action": "inventory", "character": "character:bjorn-mountainfell", "items": [{"item": "Potion of Healing", "qty": 2}]}""");
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Character(string argumentsJson) => ScenarioCalls.Call(_server, "campaign_character", argumentsJson);

    private Task<string> Combat(string argumentsJson) => ScenarioCalls.Call(_server, "combat", argumentsJson);

    // Björn in the reef fight: 20 damage from the ogre, poisoned by it, concentrating on Bless, a slot, a Rage and a potion spent.
    private async Task ReefAsync()
    {
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre"}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "total": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "ogre", "total": 3}]}""");
        await Character("""{"action": "damage", "character": "character:bjorn-mountainfell", "amount": 20}""");
        await Combat("""{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["poisoned"], "source": "ogre"}""");
        await Combat("""{"action": "concentration", "targets": ["bjorn-mountainfell"], "spell": "Bless", "slot_level": 1, "duration": "1 minute"}""");
        await Combat("""{"action": "use", "targets": ["bjorn-mountainfell"], "resource": "Rage"}""");
        await Combat("""{"action": "use", "targets": ["bjorn-mountainfell"], "item": "Potion of Healing"}""");
    }

    [Fact]
    public async Task Get_ACharacterInTheLiveFight_PrintsTheSheetAndOneLineOfWhereTheFightHasIt()
    {
        await ReefAsync();

        var text = await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}""");

        Assert.Contains("HP 85/85 · AC 15", text, StringComparison.Ordinal);
        Assert.Contains("\n\n- " + Line + "\n", text, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "In the live fight"));
    }

    [Fact]
    public async Task GetList_EveryMemberInTheLiveFight_HasTheLineUnderItsOwn()
    {
        await ReefAsync();

        var text = await Character("""{"action": "get"}""");

        Assert.Contains("(`character:bjorn-mountainfell`) — level 8 Barbarian (Path of the Totem Warrior), 2024 · HP 85/85 · AC 15 · Init +2\n  - " + Line + "\n",
            text, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, "In the live fight"));
    }

    [Fact]
    public async Task CampaignGet_IncludeSheet_HasTheLineInTheSheetSection()
    {
        await ReefAsync();

        var text = await ScenarioCalls.Call(_server, "campaign_get", """{"refs": ["character:bjorn-mountainfell"], "include": ["sheet"]}""");

        Assert.Contains("\n- " + Line + "\n", text[text.IndexOf("Sheet\n", StringComparison.Ordinal)..], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PartyResource_HasTheLineUnderTheMember()
    {
        await ReefAsync();

        var text = await ScenarioCalls.Read(_server, "campaign://deep/party");

        Assert.Contains("\n  - " + Line + "\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign_character", """{"action": "get", "character": "character:bjorn-mountainfell", "perspective": "party"}""")]
    [InlineData("campaign_character", """{"action": "get", "perspective": "character:kaz"}""")]
    [InlineData("campaign_get", """{"refs": ["character:bjorn-mountainfell"], "include": ["sheet"], "perspective": "party"}""")]
    [InlineData("campaign_character", """{"action": "get", "character": "character:kaz"}""")]
    [InlineData("campaign_character", """{"action": "get", "character": "character:harbour-master"}""")]
    public async Task Get_AnotherViewOrACharacterNotSeededInTheFight_HasNoLine(string tool, string argumentsJson)
    {
        // A non-author view reads only the public line (contract §7.4); Kaz is in the fight without a sheet, the harbour master
        // is not in it at all.
        await ReefAsync();

        Assert.DoesNotContain("live fight", await ScenarioCalls.Call(_server, tool, argumentsJson), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_TheFightEnded_HasNoLine()
    {
        await ReefAsync();
        await Combat("""{"action": "end"}""");

        Assert.DoesNotContain("live fight", await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_TheCharacterLeftTheFight_StillHasTheLineSayingItLeft()
    {
        // Fix F2, review CR07: a sheet-seeded combatant that leaves stays tied to its fight until the fight ends: end writes
        // back the state it left in, and the character's sheet actions still go to it. With no line the read showed the
        // pre-fight numbers as if the fight were done with it. Leaving ended its concentration; the rest is as it left.
        await ReefAsync();
        await Combat("""{"action": "leave", "targets": ["bjorn-mountainfell"]}""");

        var text = await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}""");

        Assert.Contains(
            "\n\n- In the live fight \"Reef\" (left the fight): HP 65/85, poisoned (Ogre), 1st-level slots 1/2 left, Rage 3/4 left, Potion of Healing 1 used; " +
            "the sheet catches up when it ends: combat {\"action\": \"state\", \"campaign\": \"deep\"}\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heal_AfterTheCharacterLeftTheFight_GoesToTheFight_AndEndWritesItBack()
    {
        // Fix F2, review CR07: a sheet-seeded combatant that left stays tied to its fight until it ends, so a heal on its sheet
        // goes to the fight (D5) and end writes back that one state. Before, the heal changed nothing and end wrote the fight's
        // HP over the sheet.
        await ReefAsync();
        await Combat("""{"action": "leave", "targets": ["bjorn-mountainfell"]}""");

        var healed = await Character("""{"action": "heal", "character": "character:bjorn-mountainfell", "amount": 10}""");
        await Combat("""{"action": "end", "xp": 0}""");

        Assert.StartsWith(
            "# campaign_character heal: Björn Mountainfell (`character:bjorn-mountainfell`, deep)\n\n" +
            "Applied to the live fight Reef, which Björn Mountainfell left; written to the sheet when it ends.\n",
            healed, StringComparison.Ordinal);
        Assert.Contains("HP 75/85", await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}"""), StringComparison.Ordinal);
    }

    /// <summary>
    /// Review M01 (its LeftPc probes, on F2's texts; fixed by F2's CR07): after combat leave, Björn's sheet actions still go
    /// to the fight that holds his sheet's state, so nothing is lost and end writes one state with no drift: damage taken
    /// out of the fight after leaving reaches the sheet with the fight's (55 − 10 = 45), a heal undoes the fight's damage
    /// (85), and a rest is refused until the fight ends, which then writes the state he left in (55). The read in between
    /// shows where the fight has him. Before CR07 the heal said "Nothing changed" and was lost at end, and the damage made
    /// end refuse for drift, then force dropped it.
    /// </summary>
    [Theory]
    [InlineData("""{"action": "damage", "character": "character:bjorn-mountainfell", "amount": 10}""", "HP 45/85")]
    [InlineData("""{"action": "heal", "character": "character:bjorn-mountainfell", "amount": 30}""", "HP 85/85")]
    [InlineData("""{"action": "rest", "character": "character:bjorn-mountainfell", "kind": "long"}""", "HP 55/85")]
    public async Task SheetAction_AfterTheCharacterLeftTheFight_NothingIsLost_EndWritesOneStateWithNoDrift(string action, string after)
    {
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"name": "Shark", "hp": 30, "ac": 12}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "total": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "shark", "total": 3}]}""");
        await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 30, "source": "shark"}""");
        await Combat("""{"action": "leave", "targets": ["bjorn-mountainfell"]}""");
        Assert.Contains("In the live fight \"Reef\" (left the fight): HP 55/85;", await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}"""),
            StringComparison.Ordinal);

        var result = await _server.Client.CallToolAsync("campaign_character", System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(action)!);
        var text = string.Join("\n", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
        var end = await Combat("""{"action": "end", "xp": 0}""");

        if (action.Contains("\"rest\"", StringComparison.Ordinal))
        {
            Assert.True(result.IsError);
            Assert.Contains("(it left the fight): rest once it ends", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("Applied to the live fight Reef, which Björn Mountainfell left; written to the sheet when it ends.", text[(text.IndexOf("\n\n", StringComparison.Ordinal) + 2)..],
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain("refused", end, StringComparison.Ordinal);
        Assert.DoesNotContain("Written over", end, StringComparison.Ordinal);
        Assert.Contains(after, await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AFightNamedWithQuotes_PrintsTheNamePlainInTheSentence()
    {
        // Fix F2, review CR10: the name was JSON-escaped inside prose ("In the live fight "Reef \"Night\""); only a call
        // escapes it, and this line's call names no fight.
        await Combat("""{"action": "start", "name": "Reef \"Night\"", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre"}]}""");

        var text = await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}""");

        Assert.Contains(
            "\n\n- In the live fight \"Reef \"Night\"\": HP 85/85; the sheet catches up when it ends: combat {\"action\": \"state\", \"campaign\": \"deep\"}\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NoFightRunning_HasNoLine()
    {
        Assert.DoesNotContain("live fight", await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}"""), StringComparison.Ordinal);
    }

    // The reef fight with nothing spent yet: Björn first, Kaz, the ogre.
    private async Task FightAsync()
    {
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre"}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "total": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "ogre", "total": 3}]}""");
    }

    // Björn's live-fight line on his author sheet.
    private async Task<string> BjornLineAsync()
    {
        var sheet = await Character("""{"action": "get", "character": "character:bjorn-mountainfell"}""");
        var at = sheet.IndexOf("In the live fight", StringComparison.Ordinal);
        Assert.True(at >= 0, sheet);
        return sheet[at..sheet.IndexOf('\n', at)];
    }

    [Fact]
    public async Task Get_OnlyWhatTheFightSpent_IsOnTheLine()
    {
        // Review M (mutant LF03): every U04 test spent every slot and resource, so a line listing the unspent ones too went
        // unnoticed. One 1st-level slot spent of [2, 1] and Rage 4: the 2nd-level slot and Rage are not on the line.
        await Character("""{"action": "update", "character": "character:bjorn-mountainfell", "sheet": {"slots": [2, 1]}}""");
        await FightAsync();
        await Combat("""{"action": "use", "targets": ["bjorn-mountainfell"], "slot_level": 1}""");

        var line = await BjornLineAsync();

        Assert.Contains("1st-level slots 1/2 left", line, StringComparison.Ordinal);
        Assert.DoesNotContain("2nd-level", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Rage", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action": "heal", "targets": ["bjorn-mountainfell"], "amount": 7, "temp": true}""", "HP 85/85 (+7 temp)")]
    [InlineData("""{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["exhaustion"], "level": 2}""", "exhaustion 2")]
    public async Task Get_TheLine_CarriesTempHpAndExhaustion(string step, string part)
    {
        // Review M (mutants LF07, LF08): the F1 report said U04 covered temporary hit points and exhaustion on the line; no
        // test asserted either.
        await FightAsync();
        await Combat(step);

        Assert.Contains(part, await BjornLineAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_TheCharacterDiedInTheFight_TheLineSaysDead_WithNoDeathSaveTally()
    {
        // Review M (mutant LF02): a combatant killed outright (170 damage, twice its 85) is dead; a death-save tally after it
        // reads as if it were dying.
        await FightAsync();
        await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 170, "source": "ogre"}""");

        var line = await BjornLineAsync();

        Assert.StartsWith("In the live fight \"Reef\": dead;", line, StringComparison.Ordinal);
        Assert.DoesNotContain("death saves", line, StringComparison.Ordinal);
    }
}
