using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Rules;
using DndMcp.Formatting.Campaign;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign.Combat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>combat state</c> is the author's table (the reminders the fight's state calls for now, the last change,
/// an ended fight's write-back), succeeds with the listing when no fight runs (contract §6.1), and for any other view
/// prints the party BOARD from the Repository's whitelist model and nothing else (§6.12): no encounter name, no "what
/// changed", no reminders, no calls, numbers only on the rows the model gives numbers.
/// </summary>
public sealed class CombatToolStateTests : IAsyncLifetime
{
    private const string PartyBanner = "_Perspective: party. Names are the ones this view knows; author-only text is withheld._";

    private readonly ScriptedDiceRoller _dice = new();
    private readonly McpServerHarness _server;

    public CombatToolStateTests()
    {
        _server = McpServerHarness.WithExtraTools(builder => builder.Services.AddSingleton<IDiceRoller>(_dice));
    }

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await CharacterToolSetup.CreateDeepAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Combat(string argumentsJson) => ScenarioCalls.Call(_server, "combat", argumentsJson);

    private async Task ReefAsync()
    {
        await Combat("""{"action": "start", "name": "Reef of secrets", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre", "count": 2}, {"srd": "Ogre", "name": "Grumm", "hidden": true}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "ogre", "total": 10}, {"combatant": "grumm", "total": 3}]}""");
        await Combat("""{"action": "damage", "targets": ["ogre"], "amount": 40, "source": "bjorn-mountainfell"}""");
        await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 9, "source": "ogre"}""");
        await Combat("""{"action": "condition", "targets": ["ogre-2"], "add": ["frightened"], "source": "bjorn-mountainfell"}""");
    }

    [Fact]
    public async Task State_NoFight_SucceedsWithTheListing()
    {
        Assert.Equal(
            "# deep: no combat running\n\n" +
            "Start a fight with combat {\"action\": \"start\", \"name\": …, \"campaign\": \"deep\"} (the party joins from their sheets), or prepare one for " +
            "later with combat {\"action\": \"prepare\", \"name\": …, \"campaign\": \"deep\"}.\n",
            await Combat("""{"action": "state", "campaign": "deep"}"""));
    }

    [Fact]
    public async Task State_NoFight_ListsThePlannedPausedAndLastEndedFightsWithTheirCalls()
    {
        await Combat("""{"action": "prepare", "name": "Ambush", "campaign": "deep", "combatants": [{"srd": "Ogre", "count": 3}]}""");
        await Combat("""{"action": "start", "name": "Skirmish", "add_party": false, "combatants": [{"name": "Shark", "hp": 30, "ac": 12}]}""");
        await Combat("""{"action": "end", "outcome": "The shark fled."}""");

        var text = await Combat("""{"action": "state"}""");

        Assert.StartsWith(
            "# deep: no combat running\n\nPlanned:\n- \"Ambush\" (3 combatants): start it with combat {\"action\": \"start\", \"encounter\": \"Ambush\", \"campaign\": \"deep\"}\n\n" +
            "Last ended: \"Skirmish\", in round 0, at ",
            text, StringComparison.Ordinal);
        Assert.Contains("; it wrote nothing back. Outcome: The shark fled. combat {\"action\": \"state\", \"encounter\": \"last\", \"campaign\": \"deep\"} shows it.\n",
            text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Party victory", "Outcome: Party victory. combat {")]
    [InlineData("Won!", "Outcome: Won! combat {")]
    [InlineData("They asked \"why?\"", "Outcome: They asked \"why?\" combat {")]
    [InlineData("The reef (for now)", "Outcome: The reef (for now). combat {")]
    public async Task State_NoFight_AnOutcomeWithNoClosingStop_EndsItsSentenceBeforeTheCall(string outcome, string expected)
    {
        // Fix F1, U12: "Outcome: Party victory combat {…} shows it." ran the excerpt into the call.
        await Combat("""{"action": "start", "name": "Skirmish", "campaign": "deep", "add_party": false, "combatants": [{"name": "Shark", "hp": 30, "ac": 12}]}""");
        await Combat($$"""{"action": "end", "outcome": {{System.Text.Json.JsonSerializer.Serialize(outcome)}}}""");

        Assert.Contains(expected, await Combat("""{"action": "state"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_NoFight_ListsAPausedFight_AndItsResumeCallWorksAsPrinted()
    {
        // No combat action pauses a fight (contract §6.0): the status is reached through the file, as Z's undo guard test does.
        await Combat("""{"action": "prepare", "name": "Held \"line\"", "campaign": "deep", "combatants": [{"srd": "Ogre", "count": 2}]}""");
        SetStatus("paused");

        var text = await Combat("""{"action": "state"}""");
        var resume = Regex.Match(text, @"resume it with combat (\{[^\n]*\})\n").Groups[1].Value;

        Assert.StartsWith(
            "# deep: no combat running\n\nPaused:\n- \"Held \\\"line\\\"\" (round 0, 2 combatants): resume it with combat {\"action\": \"start\", " +
            "\"encounter\": \"Held \\\"line\\\"\", \"campaign\": \"deep\"}\n\n",
            text, StringComparison.Ordinal);
        Assert.StartsWith("# Held \"line\" — round 0", await Combat(resume), StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_AnEndedFight_SaysWhetherItsWriteBackStandsWasUndoneOrWasAppliedAgain()
    {
        await ReefAsync();
        var ended = await Combat("""{"action": "end", "outcome": "The reef is quiet."}""");
        var batch = ScenarioCalls.BatchId(ended);

        var applied = await Combat("""{"action": "state", "encounter": "last"}""");
        var appliedListing = await Combat("""{"action": "state"}""");
        var undo = await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "undo", "batch_id": "{{batch}}", "campaign": "deep"}""");
        var undoneListing = await Combat("""{"action": "state"}""");
        var undoId = Regex.Match(undo, "as a new batch `([0-9a-f-]{36})`").Groups[1].Value;
        await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "undo", "batch_id": "{{undoId}}", "campaign": "deep"}""");
        var again = await Combat("""{"action": "state", "encounter": "last"}""");
        var againListing = await Combat("""{"action": "state"}""");

        Assert.Contains(
            $"\n\nIts write-back is batch `{batch}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{batch}\", \"campaign\": \"deep\"}}.\n" +
            "Outcome: The reef is quiet.\n", applied, StringComparison.Ordinal);
        Assert.Contains("; its write-back stands. Outcome: The reef is quiet.", appliedListing, StringComparison.Ordinal);
        Assert.Contains("; its write-back was undone. Outcome:", undoneListing, StringComparison.Ordinal);
        Assert.Contains($"\n\nIts write-back (batch `{batch}`) was undone and then applied again (a redo).\n", again, StringComparison.Ordinal);
        Assert.Contains("; its write-back was applied again. Outcome:", againListing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_AKnockedOutEnemy_IsStruckThroughAndSaidDefeated_NotDead()
    {
        await ReefAsync();

        await Combat("""{"action": "damage", "targets": ["ogre-2"], "amount": 200, "knock_out": true, "source": "bjorn-mountainfell"}""");
        var text = await Combat("""{"action": "state"}""");

        Assert.Contains("| ~~Ogre 2~~ | enemy | 1/68 · defeated | 11 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_DmInAPlayerCampaign_IsTheBoardNotTheAuthorTable()
    {
        // In a player campaign "dm" is a non-author view (contract D10, §6.12): the board, never the encounter's name, the
        // author's table, its reminders or a call.
        await CharacterToolSetup.CreateSkyAsync(_server);
        await CharacterToolSetup.BelmakorAsync(_server);
        await Combat("""{"action": "start", "name": "Crypt of secrets", "campaign": "sky", "combatants": [{"srd": "2014/monster/mummy", "hp": 58}]}""");
        await Combat("""{"action": "damage", "targets": ["mummy"], "amount": 10}""");

        var text = await Combat("""{"action": "state", "perspective": "dm"}""");

        Assert.StartsWith("# Combat — not started\n\n_Perspective: dm.", text, StringComparison.Ordinal);
        Assert.Contains("\n| Turn | # | Name | Status | Conditions |\n|---|---|---|---|---|\n", text, StringComparison.Ordinal);
        Assert.Contains("| Mummy | hurt | — |\n", text, StringComparison.Ordinal);
        foreach (var authorOnly in new[] { "Crypt", "secrets", "## Initiative", "## Reminders", "combat {", "48", "58", "enemy" })
        {
            Assert.DoesNotContain(authorOnly, text, StringComparison.Ordinal);
        }

        Assert.StartsWith("# Crypt of secrets — round 0", await Combat("""{"action": "state", "perspective": "author"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_Author_IsTheTableWithTheRemindersOfTheTurnAndTheLastChange()
    {
        await ReefAsync();
        await Combat("""{"action": "condition", "targets": ["ogre-2"], "remove": ["frightened"]}""");
        await Combat("""{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["poisoned"], "source": "ogre-2"}""");

        var text = await Combat("""{"action": "state"}""");

        Assert.StartsWith("# Reef of secrets — round 1 · Björn Mountainfell's turn\n\ncombat state · deep · 2024 rules\n\n" +
                          "Last change: condition in round 1, by Ogre 2, to Björn Mountainfell.\n\n## Initiative\n", text, StringComparison.Ordinal);
        Assert.Contains("|  | 3 | 10 | Ogre | enemy | 28/68 | 11 | — |\n", text, StringComparison.Ordinal);
        Assert.Contains("|  | 4 | 10 | Ogre 2 | enemy | 68/68 | 11 | — |\n", text, StringComparison.Ordinal);
        Assert.Contains("|  | 5 | 3 | Grumm (hidden) | enemy | 68/68 | 11 | — |\n", text, StringComparison.Ordinal);
        Assert.Contains("| ▶ | 1 | 17 | Björn Mountainfell (character:bjorn-mountainfell) | party | 76/85 | 15 | poisoned (Ogre 2) |\n", text,
            StringComparison.Ordinal);
        Assert.EndsWith("|\n\n## Reminders\n- Björn Mountainfell is poisoned", text[..text.LastIndexOf(':')], StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_DefeatedAndLeftCombatants_AreStruckThroughAndSaidSo()
    {
        await ReefAsync();
        await Combat("""{"action": "damage", "targets": ["ogre"], "amount": 40}""");
        await Combat("""{"action": "leave", "targets": ["ogre-2"]}""");

        var text = await Combat("""{"action": "state"}""");

        Assert.Contains("| ~~Ogre~~ | enemy | dead | 11 |", text, StringComparison.Ordinal);
        Assert.Contains("| Ogre 2 (left) | enemy |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_Party_IsTheBoardFromTheWhitelistModelAndNothingElse()
    {
        await ReefAsync();

        var text = await Combat("""{"action": "state", "perspective": "party"}""");

        Assert.Equal(
            "# Combat — round 1\n\n" + PartyBanner + "\n\n" +
            "| Turn | # | Name | Status | Conditions |\n|---|---|---|---|---|\n" +
            "| ▶ | 1 | Björn Mountainfell (character:bjorn-mountainfell) | HP 76/85 · AC 15 | — |\n" +
            "|  | 2 | Kaz (character:kaz) | HP unknown | — |\n" +
            "|  | 3 | Ogre | bloodied | — |\n" +
            "|  | 4 | Ogre 2 | unhurt | frightened |\n",
            text);
        foreach (var authorOnly in new[] { "Reef", "secrets", "Grumm", "hidden", "28", "68", "combat {", "Reminders", "What changed", "Initiative", "enemy" })
        {
            Assert.DoesNotContain(authorOnly, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task State_Party_APlannedFightOrNoneOrAnUnknownName_IsOneWording()
    {
        await Combat("""{"action": "prepare", "name": "Ambush", "campaign": "deep", "combatants": [{"srd": "Ogre"}]}""");
        var nothing = "# No combat to show for this perspective.\n\n" + PartyBanner + "\n";

        Assert.Equal(nothing, await Combat("""{"action": "state", "perspective": "party"}"""));
        Assert.Equal(nothing, await Combat("""{"action": "state", "perspective": "party", "encounter": "Ambush"}"""));
        Assert.Equal(nothing, await Combat("""{"action": "state", "perspective": "party", "encounter": "No such fight"}"""));
    }

    [Fact]
    public async Task State_Party_AnEndedFight_SaysItEndedAndMarksNoTurn()
    {
        await ReefAsync();
        await Combat("""{"action": "end", "outcome": "The reef is quiet.", "discard": true}""");

        var text = await Combat("""{"action": "state", "perspective": "party", "encounter": "last"}""");

        Assert.StartsWith("# Combat — ended in round 1\n\n" + PartyBanner + "\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("▶", text, StringComparison.Ordinal);
        Assert.DoesNotContain("quiet", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_PerspectiveOnAnotherAction_IsRefused()
    {
        var text = await ScenarioCalls.Fail(_server, "combat", """{"action": "next", "perspective": "party"}""");

        Assert.StartsWith("An error occurred invoking 'combat': combat next does not take \"perspective\"; next takes from, campaign, encounter.", text,
            StringComparison.Ordinal);
    }

    // The status of the one fight that has not ended, written through the file (no combat action pauses a fight).
    private void SetStatus(string status)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(_server.DataDirectory, "campaigns.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE encounter SET status = $status WHERE status <> 'ended'";
        command.Parameters.AddWithValue("$status", status);
        Assert.Equal(1, command.ExecuteNonQuery());
    }
}

/// <summary>
/// Invariant: the board formatter prints only the board model's fields (contract §6.12): numbers on a row only when the
/// model marks it <see cref="CombatBoardRow.PartySide"/> (it carries the party's numbers), the HP word on every other row
/// whatever numbers it might carry, the ref beside the name, the turn marker, conditions by name.
/// </summary>
public sealed class CombatMarkdownBoardTests
{
    private static CombatBoardRow Row(int number, string name, bool partySide, string? hpWord = null, bool turn = false, string? reference = null,
        int? hp = null, int? max = null, int? temp = null, int? ac = null, int? exhaustion = null, DeathSaveTally? saves = null,
        bool concentrating = false, string? spell = null, string[]? conditions = null) =>
        new(number, name, reference, turn, partySide, hp, max, temp, ac, exhaustion, saves, concentrating, spell, hpWord, conditions ?? []);

    [Fact]
    public void FormatBoard_PartyRows_CarryEveryNumberTheModelGives()
    {
        var board = new CombatBoard(true, 3, false,
        [
            Row(1, "Belmakor", partySide: true, reference: "character:belmakor", turn: true, hp: 82, max: 110, temp: 7, ac: 17, exhaustion: 1,
                concentrating: true, spell: "Circle of Power", conditions: ["Bladesong"]),
            Row(2, "Torch", partySide: true, hp: 0, max: 90, ac: 18, saves: new DeathSaveTally(1, 2, false), conditions: ["unconscious", "prone"]),
            Row(3, "Vars", partySide: true, ac: 16, concentrating: true),
        ]);

        Assert.Equal(
            "# Combat — round 3\n\n_banner_\n\n| Turn | # | Name | Status | Conditions |\n|---|---|---|---|---|\n" +
            "| ▶ | 1 | Belmakor (character:belmakor) | HP 82/110 (+7 temp) · AC 17 · exhaustion 1 · concentrating on Circle of Power | Bladesong |\n" +
            "|  | 2 | Torch | HP 0/90 · AC 18 · death saves: 1 success, 2 failures | unconscious, prone |\n" +
            "|  | 3 | Vars | HP unknown · AC 16 · concentrating | — |\n",
            CombatMarkdown.FormatBoard(board, "_banner_"));
    }

    [Fact]
    public void FormatBoard_ARowNotMarkedPartySide_PrintsItsHpWordAndNoNumberEvenIfItHasSome()
    {
        // A model built wrongly (numbers on a stand-in) still prints only the word: the formatter never re-derives the side.
        var board = new CombatBoard(true, 0, false,
        [
            Row(1, "an unknown creature", partySide: false, hpWord: "bloodied", hp: 41, max: 90, ac: 17, exhaustion: 2, concentrating: true, spell: "Bless"),
            Row(2, "Mummy 2", partySide: false, hpWord: "down", saves: new DeathSaveTally(0, 1, false), conditions: ["an effect"]),
        ]);

        var text = CombatMarkdown.FormatBoard(board, null);

        Assert.Equal(
            "# Combat — not started\n\n| Turn | # | Name | Status | Conditions |\n|---|---|---|---|---|\n" +
            "|  | 1 | an unknown creature | bloodied | — |\n|  | 2 | Mummy 2 | down | an effect |\n",
            text);
    }

    [Fact]
    public void FormatBoard_Nothing_IsTheOneWordingUnderTheBanner()
    {
        Assert.Equal("# No combat to show for this perspective.\n\n_banner_\n", CombatMarkdown.FormatBoard(CombatBoard.Nothing, "_banner_"));
    }
}
