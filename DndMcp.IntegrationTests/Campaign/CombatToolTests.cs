using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>combat</c> through the real client runs a fight step by step, each step answering with the encounter's
/// heading, what changed (one line per target, with its arithmetic), the rolls the SERVER made with the injected dice
/// (never one the host made: a scripted roller that runs out fails the call), the initiative table and the reminders,
/// each with the call that resolves it, the campaign named last. Every argument of every action reaches the tracker
/// (contract §6.0), the monsters are resolved in the fight's own edition, and a step the tracker refuses rolls nothing.
///
/// <para>
/// The tracker's rules and the Repository's persistence are pinned in DndMcp.Tests (Combat*, CampaignCombat*); these pin
/// what the host decides: which service method an action reaches with which arguments, the stat block an <c>srd</c>
/// becomes, the dice it hands down, and the text the model reads.
/// </para>
/// </summary>
public sealed class CombatToolTests : IAsyncLifetime
{
    private readonly ScriptedDiceRoller _dice = new();
    private readonly McpServerHarness _server;

    public CombatToolTests()
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

    private Task<string> Refused(string argumentsJson) => ScenarioCalls.Fail(_server, "combat", argumentsJson);

    // The dice_roll rows' secret column in log order: what every non-author session view filters the rolls on.
    private IReadOnlyList<long> LoggedSecrecy()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(_server.DataDirectory, "campaigns.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret FROM dice_roll ORDER BY seq";
        using var reader = command.ExecuteReader();
        var secrecy = new List<long>();
        while (reader.Read())
        {
            secrecy.Add(reader.GetInt64(0));
        }

        return secrecy;
    }

    // The reef fight of deep: Björn (sheet-seeded), Kaz (no sheet), two ogres and a third named Grumm; initiative given for
    // all but the ogres, whose group roll is the one face queued.
    private async Task ReefAsync(bool initiative = true)
    {
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre", "count": 2}, {"srd": "Ogre", "name": "Grumm"}]}""");
        if (initiative)
        {
            _dice.Enqueue(11);
            await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "grumm", "total": 3}]}""");
        }
    }

    [Fact]
    public async Task Start_DmCampaign_AddsTheCurrentPartyFromSheetsAndPrintsTheHeadingChangesTableAndReminders()
    {
        var text = await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre", "count": 2}, {"srd": "Ogre", "name": "Grumm"}]}""");

        Assert.Equal(
            "# Reef — round 0 (roll initiative to begin round 1)\n\n" +
            "combat start · deep · 2024 rules\n\n" +
            "## What changed\n" +
            "- Started \"Reef\" (2024): round 0; roll initiative to begin round 1.\n" +
            "- Not added: Old Tom (dead).\n" +
            "- No sheet, so no hit points: Kaz. Give each a sheet with campaign_character update, then seed it into this fight with " +
            "combat {\"action\": \"add\", \"combatants\": [{\"character\": \"character:kaz\"}], \"campaign\": \"deep\"}; or give hp with combat set.\n" +
            "- Björn Mountainfell (party): 85/85 HP, AC 15, initiative +2, from its sheet.\n" +
            "- Kaz (party): HP not tracked, initiative +0.\n" +
            "- Ogre (enemy): 68/68 HP, AC 11, initiative −1.\n" +
            "- Ogre 2 (enemy): 68/68 HP, AC 11, initiative −1.\n" +
            "- Grumm (enemy): 68/68 HP, AC 11, initiative −1.\n\n" +
            "## Initiative\n" +
            "| Turn | # | Init | Name | Side | HP | AC | Conditions |\n" +
            "|---|---|---|---|---|---|---|---|\n" +
            "|  | — | — | Björn Mountainfell (character:bjorn-mountainfell) | party | 85/85 | 15 | — |\n" +
            "|  | — | — | Kaz (character:kaz) | party | unknown | — | — |\n" +
            "|  | — | — | Ogre | enemy | 68/68 | 11 | — |\n" +
            "|  | — | — | Ogre 2 | enemy | 68/68 | 11 | — |\n" +
            "|  | — | — | Grumm | enemy | 68/68 | 11 | — |\n\n" +
            "## Reminders\n" +
            "- Kaz has no hit points: give hp to track them combat {\"action\": \"set\", \"combatants\": [{\"character\": \"character:kaz\", \"hp\": …}], \"campaign\": \"deep\"}\n",
            text);
        Assert.Equal(0, _dice.Remaining);
        Assert.Empty(_dice.Rolled);
    }

    [Fact]
    public async Task Initiative_TheServerRollsOnlyWhatIsNotGiven_WithTheInjectedDiceAndShowsEachRoll()
    {
        await ReefAsync(initiative: false);
        _dice.Enqueue(11);

        var text = await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "grumm", "total": 3}]}""");

        // One d20 for the ogres' group (Grumm's total was given), and nothing else.
        Assert.Equal([(20, 11)], _dice.Rolled);
        Assert.Equal(0, _dice.Remaining);
        Assert.StartsWith("# Reef — round 1 · Björn Mountainfell's turn\n\ncombat initiative · deep · 2024 rules\n\n## What changed\n" +
                          "- Björn Mountainfell: initiative 17 (15 + 2).\n- Kaz: initiative 12 (given).\n- Grumm: initiative 3 (given).\n" +
                          "- Ogre, Ogre 2: initiative 10 (rolled 1d20-1: 11).\n\n## Rolls\n" +
                          "- initiative: `1d20-1` [11] = 10 · logged as \"Ogre: initiative\" (secret)\n\n## Initiative\n", text, StringComparison.Ordinal);
        Assert.Contains("| ▶ | 1 | 17 | Björn Mountainfell (character:bjorn-mountainfell) | party | 85/85 | 15 | — |\n", text, StringComparison.Ordinal);
        Assert.Contains("|  | 5 | 3 | Grumm | enemy | 68/68 | 11 | — |\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n## Reminders\n- Round 1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Damage_CriticalDice_AreRolledOnceDoubledAndTheLineCarriesTheArithmetic()
    {
        await ReefAsync();
        _dice.Enqueue(4, 5, 2, 3);

        var text = await Combat("""{"action": "damage", "targets": ["ogre"], "dice": "2d6+5", "critical": true, "source": "bjorn-mountainfell"}""");

        Assert.Equal([(6, 4), (6, 5), (6, 2), (6, 3)], _dice.Rolled.Skip(1));
        Assert.Contains("## What changed\n- Ogre: 19 untyped; 68 → 49\n", text, StringComparison.Ordinal);
        Assert.Contains("## Rolls\n- damage (critical): `4d6+5` [4, 5, 2, 3] = 19 · logged as \"Björn Mountainfell: damage (critical)\"\n", text,
            StringComparison.Ordinal);
        Assert.Contains("|  | 3 | 10 | Ogre | enemy | 49/68 | 11 | — |\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_SaysWhatChanged_FieldByField_TheInitBonusToo()
    {
        // Fix F1 U06: set says what it changed, as every other step does; an init_bonus change showed nowhere else.
        await ReefAsync();

        var text = await Combat("""{"action": "set", "combatants": [{"name": "Grumm", "init_bonus": 4, "ac": 17}]}""");

        Assert.Equal(["Grumm: ac 11 → 17, init_bonus −1 → +4."], ScenarioCombatText.Section(text, "What changed"));
    }

    [Fact]
    public async Task Step_EveryArgumentOfEveryStepAction_ReachesTheTracker()
    {
        // One scripted fight in a lair: each call's argument changes what the step says, so an argument the tool dropped or
        // mapped onto another field shows here (contract §6.0's table, action by action).
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "inventory", "campaign": "deep", "character": "character:bjorn-mountainfell", "items": [{"item": "Potion of Healing", "qty": 2}]}""");
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "update", "campaign": "deep", "character": "character:bjorn-mountainfell", "sheet": {"resources": [{"name": "Rage", "max": 3, "recharge": "long_rest"}]}}""");
        var start = await Combat("""
            {"action": "start", "name": "Station", "campaign": "deep", "lair": true, "surprised": ["ogre*"], "combatants": [
              {"srd": "2024/monster/aboleth"}, {"srd": "2024/monster/ogre", "count": 2},
              {"name": "Cultist", "hp": 9, "ac": 12, "side": "neutral", "hidden": true, "init_bonus": 3}]}
            """);
        Assert.Contains("- Aboleth (enemy): 150/150 HP, AC 17, initiative +3, legendary actions 4/4, Legendary Resistance 4/4.\n", start, StringComparison.Ordinal);
        Assert.Contains("- Cultist (neutral): 9/9 HP, AC 12, initiative +3.\n", start, StringComparison.Ordinal);
        Assert.Contains("- Ogre is surprised: Disadvantage on its initiative roll.\n- Ogre 2 is surprised: Disadvantage on its initiative roll.\n", start,
            StringComparison.Ordinal);
        var initiative = await Combat("""
            {"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12},
              {"combatant": "aboleth", "total": 13}, {"combatant": "ogre", "total": 3}, {"combatant": "cultist", "total": 1}]}
            """);
        Assert.Contains("- Aboleth: initiative 13 (given).\n", initiative, StringComparison.Ordinal);
        Assert.Contains("| ▶ | 1 | 17 | Björn", initiative, StringComparison.Ordinal);
        Assert.Contains("Cultist (hidden)", initiative, StringComparison.Ordinal);

        async Task Says(string call, string expected)
        {
            var text = await Combat(call);
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        await Says("""{"action": "damage", "targets": ["ogre", "ogre-2"], "amount": 10, "damage_type": "fire", "half": ["ogre-2"]}""",
            "- Ogre: 10 fire; 68 → 58\n- Ogre 2: 10 fire ½ (save) = 5; 68 → 63\n");
        await Says("""{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["Rage"], "effect": {"resist": ["all"], "except": ["psychic"]}, "resource": "Rage"}""",
            "- Björn Mountainfell: Rage 1 used, 2/3 left.\n");
        await Says("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 20, "damage_type": "bludgeoning", "source": "aboleth"}""",
            "- Björn Mountainfell: 20 bludgeoning ½ (resistant: Rage) = 10; 85 → 75\n");
        await Says("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 20, "damage_type": "bludgeoning", "raw": true, "source": "aboleth"}""",
            "- Björn Mountainfell: 20 bludgeoning; 75 → 55\n");
        _dice.Enqueue(3, 4);
        await Says("""{"action": "heal", "targets": ["bjorn-mountainfell"], "dice": "2d4+2", "item": "Potion of Healing"}""",
            "- Björn Mountainfell: heals 9; 55 → 64\n- Björn Mountainfell: Potion of Healing used (1 left; the inventory is written when the fight ends).\n");
        await Says("""{"action": "heal", "targets": ["bjorn-mountainfell"], "amount": 5, "temp": true, "source": "kaz"}""",
            "- Björn Mountainfell: temporary HP 0 → 5.\n");
        await Says("""{"action": "condition", "targets": ["kaz"], "add": ["grappled"], "source": "aboleth", "dc": 14}""",
            "- Kaz: grappled (Aboleth), until it escapes (DC 14).\n");
        await Says("""{"action": "condition", "targets": ["bjorn-mountainfell"], "add": ["exhaustion"], "level": 2}""",
            "- Björn Mountainfell: exhaustion 0 → 2.\n");
        await Says("""{"action": "condition", "targets": ["ogre"], "add": ["frightened"], "source": "bjorn-mountainfell", "duration": "save ends", "dc": 13, "ability": "wis"}""",
            "- Ogre: frightened (Björn Mountainfell), until it saves (Wis DC 13 at the end of each of its turns).\n");
        await Says("""{"action": "condition", "targets": ["ogre-2"], "add": ["stunned"], "duration": "end of round 2", "round": 2}""",
            "- Ogre 2: stunned (Björn Mountainfell), until the end of round 2.\n");
        await Says("""{"action": "condition", "targets": ["ogre-2"], "remove": ["stunned"]}""", "- stunned (Björn Mountainfell) removed from Ogre 2.\n");
        await Says("""{"action": "use", "targets": ["bjorn-mountainfell"], "resource": "Rage", "amount": -1}""", "- Björn Mountainfell: Rage 1 restored, 3/3 left.\n");
        await Says("""{"action": "legendary", "source": "aboleth", "name": "Lash"}""", "- Aboleth: legendary action Lash (1), 3/4 left.\n");
        await Says("""{"action": "legendary", "source": "aboleth", "resistance": true}""", "- Aboleth uses Legendary Resistance: it succeeds instead (3/4 left).\n");
        await Says("""{"action": "legendary", "source": "aboleth", "amount": 2}""", "- Aboleth: legendary action (2), 1/4 left.\n");
        Assert.Contains("from names Aboleth, but the turn already moved to Björn Mountainfell; nothing was changed.",
            await Refused("""{"action": "next", "from": "aboleth"}"""), StringComparison.Ordinal);
        await Says("""{"action": "next", "from": "bjorn-mountainfell"}""", "# Station — round 1 · Aboleth's turn\n");
        await Says("""{"action": "prev"}""", "- Back to Björn Mountainfell's turn (round 1); its automatic changes were undone");
        await Says("""{"action": "set", "combatants": [{"character": "character:kaz", "hp": 20, "ac": 13}, {"name": "Cultist", "hidden": false}]}""",
            "- Cultist is revealed (no longer hidden).\n");
        await Says("""{"action": "damage", "targets": ["kaz"], "amount": 20, "knock_out": true, "source": "ogre"}""",
            "- Kaz is knocked out at 1 HP: unconscious, not dying; it wakes when healed or given first aid\n");
        await Says("""{"action": "leave", "targets": ["cultist"]}""", "- Cultist leaves the fight.\n");
        await Says("""{"action": "concentration", "targets": ["aboleth"], "spell": "Dominate Monster", "duration": "1 minute"}""",
            "- Aboleth concentrates on Dominate Monster for 10 rounds (until the start of Björn Mountainfell's turn in round 11).\n");
        await Says("""{"action": "damage", "targets": ["aboleth"], "amount": 30}""",
            "- Aboleth: concentration save DC 15 to keep Dominate Monster (Con save +6) combat {\"action\": \"concentration\", \"targets\": [\"aboleth\"], \"total\": …, \"campaign\": \"deep\"}\n");
        await Says("""{"action": "concentration", "targets": ["aboleth"], "total": 25}""", "- Aboleth keeps concentrating on Dominate Monster (25 against DC 15).\n");
        await Says("""{"action": "damage", "targets": ["aboleth"], "amount": 10}""", "concentration save DC 10 to keep Dominate Monster");
        await Says("""{"action": "concentration", "targets": ["aboleth"], "face": 3}""", "(9 against DC 10)");
        await Says("""{"action": "concentration", "targets": ["aboleth"], "spell": "Dominate Monster", "duration": "1 minute"}""", "- Aboleth concentrates on Dominate Monster");
        await Says("""{"action": "concentration", "targets": ["aboleth"], "drop": true}""", "- Aboleth's concentration on Dominate Monster ended: it dropped it\n");
        await Says("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 64, "damage_type": "psychic"}""",
            "- Björn Mountainfell: 64 psychic; temporary HP 5 → 0; 64 → 5\n");
        await Says("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 10, "damage_type": "psychic"}""", "- Björn Mountainfell: 10 psychic; 5 → 0");
        await Says("""{"action": "death_save", "targets": ["bjorn-mountainfell"], "face": 4}""",
            "- Björn Mountainfell: death save fails (0): 0 successes, 1 failure.\n");
        await Says("""{"action": "death_save", "targets": ["bjorn-mountainfell"], "total": 12}""",
            "- Björn Mountainfell: death save succeeds (12): 1 success, 1 failure.\n");
        await Says("""{"action": "death_save", "targets": ["bjorn-mountainfell"], "stable": true}""",
            "- Björn Mountainfell is stable at 0 HP (unconscious, no more death saves unless it takes damage)\n");
        Assert.Equal(0, _dice.Remaining);
    }

    [Fact]
    public async Task Step_SlotsPactItemsMagicSurpriseAndTheSetFields_ReachTheTracker()
    {
        // The arguments the station fight cannot show: a caster's slot and a warlock's pact slot, an item used, a magical
        // attack against a nonmagical resistance (2014 gargoyle), surprise given at initiative, a heal's source as the roll's
        // subject, an end-of-round duration's round, and set's side, death saves and maximum reduction.
        await ScenarioCalls.Call(_server, "campaign_write", """
            {"campaign": "deep", "ops": [{"op": "upsert", "kind": "character", "name": "Mage", "subtype": "pc", "visibility": "party"},
              {"op": "upsert", "kind": "character", "name": "Hexer", "subtype": "pc", "visibility": "party"}]}
            """);
        await ScenarioCalls.Call(_server, "campaign_character", """{"action": "update", "campaign": "deep", "character": "character:mage", "sheet": {"classes": [{"class": "wizard", "level": 5}], "ac": 12, "max_hp": 30}}""");
        await ScenarioCalls.Call(_server, "campaign_character", """{"action": "update", "campaign": "deep", "character": "character:hexer", "sheet": {"classes": [{"class": "warlock", "level": 3}], "ac": 13, "max_hp": 24}}""");
        await ScenarioCalls.Call(_server, "campaign_character", """{"action": "inventory", "campaign": "deep", "character": "character:bjorn-mountainfell", "items": [{"item": "Potion of Healing", "qty": 2}]}""");
        var start = await Combat("""
            {"action": "start", "name": "Test", "campaign": "deep", "edition": "2014", "add_party": false,
             "combatants": [{"character": "character:bjorn-mountainfell"}, {"character": "character:kaz", "hp": 20}, {"character": "character:mage"},
               {"character": "character:hexer"}, {"srd": "Gargoyle", "hp": "avg"}, {"srd": "Ogre"}, {"srd": "Ogre", "name": "Brute", "hp": 10, "death_saves": true}]}
            """);
        var initiative = await Combat("""
            {"action": "initiative", "surprised": ["gargoyle"], "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12},
              {"combatant": "mage", "total": 11}, {"combatant": "hexer", "total": 10}, {"combatant": "gargoyle", "total": 9}, {"combatant": "ogre", "total": 3},
              {"combatant": "brute", "total": 2}]}
            """);

        Assert.StartsWith("# Test — round 0 (roll initiative to begin round 1)\n\ncombat start · deep · 2014 rules\n", start, StringComparison.Ordinal);
        Assert.Contains("- Ogre (enemy): 59/59 HP, AC 11, initiative −1.\n", start, StringComparison.Ordinal);
        Assert.Contains("| Gargoyle (surprised) | enemy |", initiative, StringComparison.Ordinal);

        async Task Says(string call, string expected)
        {
            var text = await Combat(call);
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        await Says("""{"action": "damage", "targets": ["gargoyle"], "amount": 10, "damage_type": "slashing"}""",
            "- Gargoyle: 10 slashing ½ (resistant: nonmagical, not adamantine) = 5; 52 → 47\n");
        await Says("""{"action": "damage", "targets": ["gargoyle"], "amount": 10, "damage_type": "slashing", "magical": true}""", "- Gargoyle: 10 slashing; 47 → 37\n");
        await Says("""{"action": "concentration", "targets": ["mage"], "spell": "Hold Person", "slot_level": 2, "duration": "1 minute"}""",
            "- Mage: 2nd-level slot 1 used, 2/3 left.\n");
        await Says("""{"action": "use", "targets": ["mage"], "slot_level": 1}""", "- Mage: 1st-level slot 1 used, 3/4 left.\n");
        await Says("""{"action": "use", "targets": ["hexer"], "pact": true}""", "- Hexer: 2nd-level Pact Magic slot 1 used, 1/2 left.\n");
        await Says("""{"action": "use", "targets": ["bjorn-mountainfell"], "item": "Potion of Healing"}""",
            "- Björn Mountainfell: Potion of Healing used (1 left; the inventory is written when the fight ends).\n");
        _dice.Enqueue(3);
        await Says("""{"action": "heal", "targets": ["mage"], "dice": "1d4", "source": "kaz"}""", "- heal: `1d4` [3] = 3 · logged as \"Kaz: heal\"\n");
        await Says("""{"action": "condition", "targets": ["ogre"], "add": ["stunned"], "duration": "end of round", "round": 2}""",
            "- Ogre: stunned (Björn Mountainfell), until the end of round 2.\n");
        await Says("""{"action": "set", "combatants": [{"name": "Ogre", "side": "neutral", "death_saves": true, "max_hp_reduction": 10}]}""",
            "- Ogre's hit point maximum is now 49: 59 → 49 HP.\n");
        await Says("""{"action": "damage", "targets": ["ogre"], "amount": 49}""", "| ~~Ogre~~ | neutral | 0/49 · death saves: 0 successes, 0 failures | 11 |");
        await Says("""{"action": "damage", "targets": ["brute"], "amount": 10}""", "| ~~Brute~~ | enemy | 0/10 · death saves: 0 successes, 0 failures | 11 |");
        Assert.Equal(0, _dice.Remaining);
    }

    [Fact]
    public async Task Step_TheTrackerRefusesIt_RollsNothingAndChangesNothing()
    {
        await ReefAsync();
        var before = await Combat("""{"action": "state"}""");

        // No face is queued: a roll before the refusal would fail the call with the roller's own error instead.
        var text = await Refused("""{"action": "damage", "targets": ["nobody"], "dice": "8d6", "damage_type": "fire"}""");

        Assert.StartsWith("An error occurred invoking 'combat': ", text, StringComparison.Ordinal);
        Assert.Contains("nobody", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptedDiceRoller", text, StringComparison.Ordinal);
        Assert.Single(_dice.Rolled);
        Assert.Equal(before, await Combat("""{"action": "state"}"""));
    }

    [Fact]
    public async Task Step_EveryPrintedCall_NamesTheCampaignLast()
    {
        // A printed call goes to whatever campaign is current when it is sent: each names this one, last, exactly once (the
        // tracker's calls name none; the proposals name it first).
        var outputs = new List<string>
        {
            await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/aboleth"}]}"""),
            await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "aboleth", "total": 3}]}"""),
            await Combat("""{"action": "concentration", "targets": ["aboleth"], "spell": "Dominate Monster"}"""),
            await Combat("""{"action": "damage", "targets": ["aboleth"], "amount": 30}"""),
            await Combat("""{"action": "damage", "targets": ["kaz"], "amount": 30}"""),
            await Combat("""{"action": "set", "combatants": [{"character": "character:kaz", "hp": 30}]}"""),
            await Combat("""{"action": "damage", "targets": ["kaz"], "amount": 60}"""),
            await Combat("""{"action": "end", "dry_run": true}"""),
        };

        var calls = outputs.SelectMany(o => Regex.Matches(o, @"(?:combat|campaign_character|campaign_write) \{[^\n]*\}").Select(m => m.Value)).ToList();
        Assert.True(calls.Count >= 4, string.Join("\n", calls));
        Assert.Contains(calls, c => c.StartsWith("campaign_write {", StringComparison.Ordinal));
        Assert.Contains(calls, c => c.StartsWith("combat {\"action\": \"concentration\"", StringComparison.Ordinal));
        Assert.All(calls, call => Assert.EndsWith(", \"campaign\": \"deep\"}", call, StringComparison.Ordinal));
        Assert.All(calls, call => Assert.Single(Regex.Matches(call, "\"campaign\":")));
    }

    [Fact]
    public async Task Step_TheTrackersAndTheRepositorysOwnCalls_NameTheCampaignLast_InResultsAndRefusals()
    {
        // The tracker never knows the slug (its calls name no campaign) and the Repository names it second: every call combat
        // prints, a refusal's included, names this campaign last and once, so it reaches this fight whichever is current.
        var prepared = await Combat("""{"action": "prepare", "name": "Later", "campaign": "deep", "combatants": [{"srd": "Ogre"}]}""");
        var planned = await Refused("""{"action": "damage", "encounter": "Later", "targets": ["ogre"], "amount": 3}""");
        await ReefAsync(initiative: false);
        var early = await Refused("""{"action": "next"}""");
        var second = await Refused("""{"action": "start", "name": "Another"}""");
        _dice.Enqueue(11);
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "grumm", "total": 3}]}""");
        var notConcentrating = await Refused("""{"action": "condition", "targets": ["ogre"], "add": ["restrained"], "duration": "concentration", "source": "bjorn-mountainfell"}""");
        var everyoneLeft = await Combat("""{"action": "leave", "targets": ["bjorn-mountainfell", "kaz", "ogre*", "grumm"]}""");

        Assert.Contains("start it with combat {\"action\": \"start\", \"encounter\": \"Later\", \"campaign\": \"deep\"}.", prepared, StringComparison.Ordinal);
        Assert.EndsWith("start it first (combat {\"action\": \"start\", \"encounter\": \"Later\", \"campaign\": \"deep\"}).", planned, StringComparison.Ordinal);
        Assert.EndsWith("roll initiative first (combat {\"action\": \"initiative\", \"campaign\": \"deep\"}).", early, StringComparison.Ordinal);
        Assert.Contains("combat {\"action\": \"end\", \"encounter\": \"Reef\", \"campaign\": \"deep\"}, then start this one.", second, StringComparison.Ordinal);
        Assert.Contains("(combat {\"action\": \"concentration\", \"targets\": [\"bjorn-mountainfell\"], \"spell\": …, \"campaign\": \"deep\"})", notConcentrating,
            StringComparison.Ordinal);
        Assert.Contains("- No combatant is left to take a turn: end the fight (combat {\"action\": \"end\", \"campaign\": \"deep\"}).\n", everyoneLeft,
            StringComparison.Ordinal);
        foreach (var text in new[] { prepared, planned, early, second, notConcentrating, everyoneLeft })
        {
            var calls = Regex.Matches(text, @"(?:combat|campaign_[a-z]+) \{[^\n]*?\}(?=\)|\.|,|\n|$)").Select(m => m.Value).ToList();
            Assert.NotEmpty(calls);
            Assert.All(calls, call => Assert.EndsWith("\"campaign\": \"deep\"}", call, StringComparison.Ordinal));
            Assert.All(calls, call => Assert.Single(Regex.Matches(call, "\"campaign\":")));
        }
    }

    [Fact]
    public async Task Step_SecretGiven_OverridesTheRuleBothWays()
    {
        await ReefAsync();

        // Björn's roll is open by the rules (party-side, shown to the party): secret true keeps it from the party.
        _dice.Enqueue(3, 4);
        var kept = await Combat("""{"action": "damage", "targets": ["ogre"], "dice": "2d6", "source": "bjorn-mountainfell", "secret": true}""");
        // An ogre's roll is secret by the rules (an enemy in a DM campaign): secret false shows it.
        _dice.Enqueue(5, 6);
        var shown = await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "dice": "2d6", "source": "ogre", "secret": false}""");
        _dice.Enqueue(2, 2);
        var byRule = await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "dice": "2d6", "source": "ogre"}""");

        Assert.Contains("- damage: `2d6` [3, 4] = 7 · logged as \"Björn Mountainfell: damage\" (secret)\n", kept, StringComparison.Ordinal);
        Assert.Contains("- damage: `2d6` [5, 6] = 11 · logged as \"Ogre: damage\"\n", shown, StringComparison.Ordinal);
        Assert.Contains("- damage: `2d6` [2, 2] = 4 · logged as \"Ogre: damage\" (secret)\n", byRule, StringComparison.Ordinal);
        Assert.Equal([1L, 0L, 1L], LoggedSecrecy().TakeLast(3));
    }

    [Fact]
    public void Readme_RollSecrecy_SaysWhoseRollADamageAndAHealingRollIs()
    {
        // Fix F1, L01 (contract §6.10 amended): a damage roll with no source is the turn-holder's (the §6.4 actor, who rolls it),
        // a healing roll's is the creature healed; the README's "else to its one target" for both inverted secrecy for damage.
        var readme = Regex.Replace(File.ReadAllText(RulesResourceTests.ReadmePath()), @"\s+", " ");

        Assert.Contains(
            "A roll belongs to the creature it is rolled for: a damage roll to its `source`, else to the creature whose turn it is (before the first turn, " +
            "to its one target); a healing or temporary-HP roll to its `source`, else to its one target, the creature healed. It is secret,",
            readme, StringComparison.Ordinal);
        Assert.Contains("A roll that belongs to no one (several targets, no source, and for damage no turn running) is secret when any one target's would be.",
            readme, StringComparison.Ordinal);
        Assert.DoesNotContain("a damage or healing roll to its `source`, else to its one target", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Routing_SaysASheetsActionsGoToTheFightUntilItEnds_EvenAfterTheCharacterLeaves()
    {
        // Fix F2, review CR07 (contract §6.2 amended): a sheet-seeded combatant that leaves stays tied to its fight until the
        // fight ends, so its sheet actions still go to it and end writes back one state; "while a character is in the fight"
        // read as if leaving handed the sheet back.
        var readme = Regex.Replace(File.ReadAllText(RulesResourceTests.ReadmePath()), @"\s+", " ");

        Assert.Contains(
            "While a character is in the fight from its sheet (until the fight ends, even after it leaves), `campaign_character`'s damage, healing, " +
            "temporary HP, use and conditions go to the fight instead (the sheet gets them at `end`), and a rest is refused until the fight is over.",
            readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_ANameThePartyDoesNotUse_IsWarnedUnderWarnings()
    {
        // D20b: the author is told what the party board will show instead, for a combatant's typed name and an effect's.
        await ScenarioCalls.Call(_server, "campaign_write", """{"campaign": "deep", "ops": [{"op": "upsert", "kind": "character", "name": "Keras", "visibility": "author"}]}""");
        await ReefAsync(initiative: false);

        var added = await Combat("""{"action": "add", "combatants": [{"srd": "2024/monster/ogre", "name": "Keras"}]}""");
        var effect = await Combat("""{"action": "condition", "targets": ["grumm"], "add": ["Cage of Keras"]}""");

        Assert.Contains("\n## Warnings\n- 'Keras' is a name the party does not use (character:keras): party views show this combatant as 'Ogre'; add it with " +
                        "character:keras to show the name the party knows.\n\n## Initiative\n", added, StringComparison.Ordinal);
        Assert.Contains("\n## Warnings\n- 'Cage of Keras' ", effect, StringComparison.Ordinal);
        Assert.Contains(": party views show this effect as 'an effect'.\n", effect, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_ANameStartingWithAPossibleNameOfHiddenProse_IsShown_AndWarnedUnderWarnings()
    {
        // F3, review F2R02: a word that hidden prose capitalises mid-sentence and that no name records ("every eaten fruit
        // thins Baal's seal") is only a possible name: the typed "Baalite cultist" is shown on the party board, and the step
        // warns the author, who decides.
        var fact = ScenarioCalls.Refs(await ScenarioCalls.Call(_server, "campaign_write",
            """{"campaign": "deep", "ops": [{"op": "fact", "statement": "Every eaten fruit thins Baal's seal.", "visibility": "restricted"}]}"""))[0];
        await ReefAsync(initiative: false);

        var added = await Combat("""{"action": "add", "combatants": [{"name": "Baalite cultist", "hp": 12, "ac": 12}]}""");
        var board = await Combat("""{"action": "state", "perspective": "party"}""");

        Assert.Contains($"\n## Warnings\n- 'Baalite cultist' starts with 'Baal', a name in hidden text ({fact}): the party may read it as that name; " +
                        "party views show this combatant as written.\n\n## Initiative\n", added, StringComparison.Ordinal);
        Assert.Contains("| Baalite cultist | unhurt |", board, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Board_SaysANameThatOnlyStartsLikeAHiddenWord_IsShownWithAWarning()
    {
        // F3, review F2R02: prose is warn-only; the README's "no hidden creature, secret name or roll" must not read as if
        // every such name were withheld.
        var readme = Regex.Replace(File.ReadAllText(RulesResourceTests.ReadmePath()), @"\s+", " ");

        Assert.Contains(
            "A name you type that only starts like a word your hidden notes capitalise (\"Baalite cultist\" where only a secret says Baal) is shown, and the " +
            "step that typed it warns you, so you decide.",
            readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_APlannedLairFight_KeepsItsLairWhenStartIsNotTold()
    {
        await Combat("""{"action": "prepare", "name": "Station", "campaign": "deep", "lair": true, "combatants": [{"srd": "2024/monster/aboleth"}]}""");

        var text = await Combat("""{"action": "start", "encounter": "Station", "add_party": false}""");

        Assert.StartsWith("# Station — round 0 (roll initiative to begin round 1)\n\ncombat start · deep · 2024 rules · in a lair\n", text, StringComparison.Ordinal);
        Assert.Contains("| Aboleth | enemy | 150/150 | 17 | legendary actions 4/4; Legendary Resistance 4/4 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_SeveralWrong_AreReportedTogether()
    {
        await ReefAsync();

        var added = await Refused("""{"action": "add", "combatants": [{"srd": "Ogre", "hp": true}, {"name": "Imp", "hp": "lots", "max_hp_reduction": 2}]}""");
        var parts = await Refused("""{"action": "damage", "targets": ["ogre"], "parts": [null, {"amount": 3}, null]}""");
        var ended = await Refused("""{"action": "end", "loot": [null], "currency": [{"gp": 1}, null]}""");

        Assert.StartsWith(
            "An error occurred invoking 'combat': Invalid combatants (3 problems):\n" +
            "- combatants item 1: hp must be \"avg\", \"roll\", \"unknown\" or a whole number of hit points, e.g. 45.\n" +
            "- combatants item 2: max_hp_reduction is set only (a combatant joins at its maximum); add it, then set its max_hp_reduction.\n" +
            "- combatants item 2: hp \"lots\" is not hit points", added, StringComparison.Ordinal);
        Assert.StartsWith("An error occurred invoking 'combat': Invalid damage (2 problems):\n- parts item 1 is null; ", parts, StringComparison.Ordinal);
        Assert.Contains("\n- parts item 3 is null; ", parts, StringComparison.Ordinal);
        Assert.StartsWith("An error occurred invoking 'combat': Invalid end (2 problems):\n- loot item 1 is null; ", ended, StringComparison.Ordinal);
        Assert.Contains("\n- currency item 2 is null; ", ended, StringComparison.Ordinal);
        Assert.StartsWith("# Reef — round 1", await Combat("""{"action": "state"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monster_IsResolvedInTheFightsEditionNotTheCampaigns()
    {
        // deep is a 2024 campaign; the prepared fight is 2014, so "Ogre" is the 2014 ogre (59 HP), and a 2024 fight's is 68.
        var planned = await Combat("""{"action": "prepare", "name": "Old road", "edition": "2014", "lair": true, "campaign": "deep", "combatants": [{"srd": "Ogre"}]}""");
        var added = await Combat("""{"action": "add", "encounter": "Old road", "combatants": [{"srd": "Ogre", "name": "Second ogre"}]}""");
        await Combat("""{"action": "start", "name": "New road", "add_party": false, "combatants": [{"srd": "Ogre"}]}""");

        Assert.StartsWith("# Old road — planned\n\ncombat prepare · deep · 2014 rules · in a lair\n", planned, StringComparison.Ordinal);
        Assert.Contains("|  | — | — | Ogre | enemy | 59/59 | 11 | — |\n", planned, StringComparison.Ordinal);
        Assert.Contains("|  | — | — | Second ogre | enemy | 59/59 | 11 | — |\n", added, StringComparison.Ordinal);
        Assert.Contains("|  | — | — | Ogre | enemy | 68/68 | 11 | — |\n", await Combat("""{"action": "state"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Monster_TheSrdLacksIt_IsRefusedWithCombatsFallbackAndNothingIsCreated()
    {
        var text = await Refused("""{"action": "start", "name": "Lair", "campaign": "deep", "combatants": [{"srd": "Ogre"}, {"srd": "Beholder"}]}""");

        Assert.Equal(
            "An error occurred invoking 'combat': combatants item 2: no monster in the 2014 or 2024 SRD is named \"Beholder\". For a creature the SRD " +
            "does not have (it has only some of the Monster Manual), add it by name with hp, ac and init_bonus instead of srd: " +
            "{\"name\": \"Beholder\", \"hp\": 45, \"ac\": 15, \"init_bonus\": 2}.",
            text);
        Assert.StartsWith("# deep: no combat running\n", await Combat("""{"action": "state"}"""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"avg\"", "68/68")]
    [InlineData("45", "45/45")]
    [InlineData("\"45\"", "45/45")]
    [InlineData("\"unknown\"", "unknown")]
    public async Task Add_HpAWordOrANumber_BindsAsTheTrackersChoice(string hp, string shown)
    {
        await ReefAsync(initiative: false);

        var text = await Combat($$"""{"action": "add", "combatants": [{"srd": "2024/monster/ogre", "name": "Late ogre", "hp": {{hp}}}]}""");

        Assert.Contains($"| Late ogre | enemy | {shown} |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_HpRoll_IsRolledByTheServerFromTheStatBlocksHitDice()
    {
        await ReefAsync(initiative: false);
        _dice.Enqueue(3, 4, 5, 6, 7, 8, 9, 10);

        var text = await Combat("""{"action": "add", "combatants": [{"srd": "2024/monster/ogre", "name": "Rolled ogre", "hp": "roll"}]}""");

        Assert.Equal(0, _dice.Remaining);
        Assert.Contains("- hit points: `8d10+24` [3, 4, 5, 6, 7, 8, 9, 10] = 76 · logged as \"Ogre: hit points\" (secret)\n", text, StringComparison.Ordinal);
        Assert.Contains("| Rolled ogre | enemy | 76/76 |", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action": "add", "combatants": [{"srd": "Ogre", "hp": true}]}""", "combatants item 1: hp must be \"avg\", \"roll\", \"unknown\" or a whole number")]
    [InlineData("""{"action": "add", "combatants": [{"srd": "Ogre", "hp": "lots"}]}""", "combatants item 1: hp \"lots\" is not hit points")]
    [InlineData("""{"action": "add", "combatants": [{"name": "Imp", "max_hp_reduction": 5}]}""", "max_hp_reduction is set only")]
    [InlineData("""{"action": "add", "combatants": [{"srd": " "}]}""", "combatants item 1: srd is empty")]
    [InlineData("""{"action": "add", "combatants": [null]}""", "combatants item 1 is null")]
    [InlineData("""{"action": "set", "combatants": [{"srd": "Ogre", "hp": 3}]}""", "set changes a combatant already in the fight, so it takes no srd")]
    [InlineData("""{"action": "set", "combatants": [{"name": "Ogre", "count": 2}]}""", "so it takes no count")]
    [InlineData("""{"action": "set", "combatants": [{"hp": 3}]}""", "give the combatant to change as character (its handle) or name")]
    [InlineData("""{"action": "set", "combatants": [{"character": "character:kaz", "name": "Big", "hp": 3}]}""",
        "combatants item 1: give the combatant to change as character (its handle) or name (its tracker name), not both; set renames no one.")]
    [InlineData("""{"action": "set", "combatants": [{"name": "Ogre", "hp": "avg"}]}""", "hp in set is the combatant's current hit points, a whole number")]
    [InlineData("""{"action": "initiative", "rolls": [{"face": 3}]}""", "rolls item 1: give the combatant and its face or total")]
    [InlineData("""{"action": "damage", "targets": ["ogre"], "parts": [null]}""", "parts item 1 is null")]
    [InlineData("""{"action": "legendary", "name": "Lash"}""", "legendary needs source: the legendary creature acting")]
    [InlineData("""{"action": "end", "loot": [null]}""", "loot item 1 is null")]
    [InlineData("""{"action": "end", "currency": [null]}""", "currency item 1 is null")]
    public async Task Entries_WhatOnlyTheToolCanCheck_IsRefusedNamingTheItem(string call, string message)
    {
        await ReefAsync(initiative: false);

        Assert.Contains(message, await Refused(call), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_GivesHitPointsToAMemberWithoutASheet()
    {
        await ReefAsync();

        var text = await Combat("""{"action": "set", "combatants": [{"character": "character:kaz", "hp": 30, "ac": 14}]}""");

        Assert.Contains("| Kaz (character:kaz) | party | 30/30 | 14 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_NamingACampaignThatIsNotCurrent_MakesItCurrentAndSaysSo()
    {
        await ScenarioCalls.Call(_server, "campaign", """{"action": "create", "name": "Other", "role": "dm", "ruleset": "2014", "slug": "other"}""");
        await ScenarioCalls.Call(_server, "campaign", """{"action": "use", "campaign": "other"}""");

        var text = await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "add_party": false}""");
        var state = await Combat("""{"action": "state"}""");
        await Combat("""{"action": "end", "discard": true}""");
        var again = await Combat("""{"action": "start", "name": "Second", "campaign": "deep", "add_party": false}""");

        Assert.Contains("- deep is now the current campaign: calls without campaign use it.\n", text, StringComparison.Ordinal);
        Assert.StartsWith("# Reef — round 0", state, StringComparison.Ordinal);
        Assert.DoesNotContain("now the current campaign", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_AddPartyFalse_AddsNobodyButTheCombatants()
    {
        var text = await Combat("""{"action": "start", "name": "Ambush", "campaign": "deep", "add_party": false, "combatants": [{"name": "Shark", "hp": 30, "ac": 12}]}""");

        Assert.DoesNotContain("Björn", text, StringComparison.Ordinal);
        Assert.Contains("| Shark | enemy | 30/30 | 12 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_APreparedFightByName_ReseedsItsCharactersFromTheirSheetsNow()
    {
        await Combat("""{"action": "prepare", "name": "Later", "campaign": "deep", "combatants": [{"character": "character:bjorn-mountainfell"}, {"srd": "Ogre"}]}""");
        await ScenarioCalls.Call(_server, "campaign_character", """{"action": "damage", "campaign": "deep", "character": "character:bjorn-mountainfell", "amount": 5}""");

        var text = await Combat("""{"action": "start", "encounter": "Later", "add_party": false}""");

        Assert.StartsWith("# Later — round 0 (roll initiative to begin round 1)\n", text, StringComparison.Ordinal);
        Assert.Contains("- Björn Mountainfell: re-seeded from its sheet as it is now.\n", text, StringComparison.Ordinal);
        Assert.Contains("| Björn Mountainfell (character:bjorn-mountainfell) | party | 80/85 |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_HpAsAWholeNumberInAString_SetsIt()
    {
        // Review M (mutant H09): set took hp only as a JSON number; a client that quotes numbers sends "40", which add's hp
        // already took.
        await ReefAsync();

        var text = await Combat("""{"action": "set", "combatants": [{"name": "Grumm", "hp": "40"}]}""");

        Assert.Contains("Grumm: hp 68 → 40", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Condition_AnEffectsVulnerability_DoublesTheDamage_EachDefenceAsItself()
    {
        // Review M (mutant H20): effect.immune and effect.vulnerable crossed on their way to the tracker inverted every later
        // damage step; only resist, except and ac were exercised here.
        await ReefAsync();
        await Combat("""{"action": "condition", "targets": ["grumm"], "add": ["Brittle"], "effect": {"vulnerable": ["fire"]}}""");
        await Combat("""{"action": "condition", "targets": ["ogre"], "add": ["Stoneskin ward"], "effect": {"immune": ["cold"]}}""");

        var fire = await Combat("""{"action": "damage", "targets": ["grumm"], "amount": 10, "damage_type": "fire"}""");
        var cold = await Combat("""{"action": "damage", "targets": ["ogre"], "amount": 10, "damage_type": "cold"}""");

        Assert.Contains("| Grumm | enemy | 48/68 |", fire, StringComparison.Ordinal);
        Assert.Contains("| Ogre | enemy | 68/68 |", cold, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Damage_ToACombatantWithUnknownHitPoints_TheTableSaysTheDamageTaken()
    {
        // Review M (mutant M02): D17's damage taken is the only number the DM has for Kaz (no sheet); the cell said "unknown".
        await ReefAsync();

        var text = await Combat("""{"action": "damage", "targets": ["kaz"], "amount": 5, "source": "grumm"}""");

        Assert.Contains("unknown, took 5", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initiative_ADecimalTotalGivenToBreakATie_IsPrintedAsGiven()
    {
        // Review M (mutant M04): D14 lets a decimal total order a tie; printed with no decimals, 12.5 read 13.
        await ReefAsync(initiative: false);
        _dice.Enqueue(11);

        var text = await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12.5}, {"combatant": "grumm", "total": 3}]}""");

        Assert.Contains("| 12.5 | Kaz (character:kaz) |", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_CurrencyElectrumAndPlatinum_LandInTheirOwnColumns()
    {
        // Review M (mutant H34): end's coins were only ever given in gp, sp and cp; ep and pp swapped went unnoticed.
        await ReefAsync();

        await Combat("""{"action": "end", "currency": [{"ep": 3, "pp": 1, "to": "character:bjorn-mountainfell"}]}""");

        Assert.Contains("- **Coins:** 1 pp, 3 ep\n", await Bjorn(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_AWriteBackWhoseOnlyItemIsOneConsumed_SaysHowToMoveOneItem()
    {
        // Review M (mutant M01; fix F1, review U03's third case): a potion used in the fight is an item change too, so the
        // result says how to move one item, as it does for loot and coins.
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "inventory", "campaign": "deep", "character": "character:bjorn-mountainfell", "items": [{"item": "Potion of Healing", "qty": 2}]}""");
        await ReefAsync();
        await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 10, "source": "grumm"}""");
        await Combat("""{"action": "heal", "targets": ["bjorn-mountainfell"], "amount": 4, "item": "Potion of Healing"}""");

        var text = await Combat("""{"action": "end", "xp": 0}""");

        Assert.Contains(DndMcp.Formatting.Campaign.CombatMarkdown.MoveOneItem, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_APcStabilizedAtZero_TheSheetSaysStableAtZero()
    {
        // Review M (mutant K08): the author sheet of a PC the fight left stable at 0 HP never said so in a test.
        await ReefAsync();
        await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 85, "source": "grumm"}""");
        await Combat("""{"action": "death_save", "targets": ["bjorn-mountainfell"], "stable": true}""");

        await Combat("""{"action": "end", "xp": 0}""");

        Assert.Contains("Stable at 0 HP", await Bjorn(), StringComparison.Ordinal);
    }

    private Task<string> Bjorn() =>
        ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "campaign": "deep", "character": "character:bjorn-mountainfell"}""");
}

/// <summary>
/// Invariant: <c>combat end</c> prints like a write result: the batch paragraph with the undo call when, and only when, a
/// batch was logged (or a dry run, which says nothing was written), then every sheet field written back with both values
/// and the summary; and the call Z's undo guard prints (<c>combat {"action": "end", "campaign": …, "encounter": …,
/// "discard": true}</c>) works verbatim for a planned, a paused and an active fight (contract §6.7).
/// </summary>
public sealed class CombatToolEndTests : IAsyncLifetime
{
    private readonly McpServerHarness _server = McpServerHarness.WithExtraTools(builder =>
        builder.Services.AddSingleton<IDiceRoller>(new ScriptedDiceRoller()));

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await CharacterToolSetup.CreateDeepAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Combat(string argumentsJson) => ScenarioCalls.Call(_server, "combat", argumentsJson);

    private Task<string> Bjorn() => ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "campaign": "deep", "character": "character:bjorn-mountainfell"}""");

    private async Task ReefAsync()
    {
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"name": "Shark", "hp": 30, "ac": 12}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "face": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "shark", "total": 3}]}""");
        await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "parts": [{"amount": 14, "type": "bludgeoning"}, {"amount": 5, "type": "fire"}], "source": "shark"}""");
    }

    [Fact]
    public async Task End_WritesTheSheetsBackAsOneBatch_PrintedLikeAWriteResult_AndTheUndoRestoresThem()
    {
        await ReefAsync();

        var text = await Combat("""{"action": "end", "outcome": "The reef is quiet.", "reason": "the reef fight"}""");
        var batch = ScenarioCalls.BatchId(text);

        Assert.Equal(
            "# Reef — ended in round 1\n\ncombat end · deep · 2024 rules\n\n" +
            $"Batch `{batch}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{batch}\", \"campaign\": \"deep\"}}.\n\n" +
            "## Written back\n- Björn Mountainfell (`character:bjorn-mountainfell`) hp: 85 → 66\n\n" +
            "## Summary\n- XP not awarded (no party sheet tracks XP). By the 2024 rules this fight is worth 0 XP, 0 each for 1.\n" +
            "- Björn Mountainfell: hp 85 → 66.\n",
            text);
        Assert.Contains("HP 66/85", await Bjorn(), StringComparison.Ordinal);
        Assert.Contains("the reef fight", await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "batch", "batch_id": "{{batch}}", "campaign": "deep"}"""),
            StringComparison.Ordinal);

        await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "undo", "batch_id": "{{batch}}", "campaign": "deep"}""");
        Assert.Contains("HP 85/85", await Bjorn(), StringComparison.Ordinal);
        Assert.Contains("Its write-back (batch `" + batch + "`) was undone",
            await Combat("""{"action": "state", "encounter": "last"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_DryRun_SaysNothingWasWritten_AndTheFightRunsOn()
    {
        await ReefAsync();

        var text = await Combat("""{"action": "end", "dry_run": true}""");

        Assert.StartsWith("# Dry run: Reef — would end in round 1\n\ncombat end · deep · 2024 rules\n\n**Dry run: nothing was written and no batch exists.** " +
                          "The fight is still running; this is what ending it would write. Send the same call without dry_run to end it.\n\n", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("codes", text, StringComparison.Ordinal);
        Assert.Contains("## Would be written back\n- Björn Mountainfell (`character:bjorn-mountainfell`) hp: 85 → 66\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", text, StringComparison.Ordinal);
        Assert.Contains("HP 85/85", await Bjorn(), StringComparison.Ordinal);
        Assert.StartsWith("# Reef — round 1 · Björn Mountainfell's turn\n", await Combat("""{"action": "state"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_Discard_WritesNothingBack_AndASecondEndIsTheToolsRefusal()
    {
        await ReefAsync();

        var text = await Combat("""{"action": "end", "discard": true}""");
        var second = await ScenarioCalls.Fail(_server, "combat", """{"action": "end", "encounter": "Reef"}""");

        Assert.StartsWith("# Reef — ended in round 1\n\ncombat end · deep · 2024 rules\n\n" +
                          "Ended with nothing written back: no sheet, item or award changed, so there is no batch to undo.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", text, StringComparison.Ordinal);
        Assert.Contains("HP 85/85", await Bjorn(), StringComparison.Ordinal);
        Assert.Equal("An error occurred invoking 'combat': \"Reef\" already ended (round 1); a fight ends once. It wrote nothing back.", second);
    }

    [Fact]
    public async Task End_XpLootCurrencyAndReason_AreTheBatchs()
    {
        await ReefAsync();

        var text = await Combat("""
            {"action": "end", "xp": 300, "reason": "the reef fight", "loot": [{"item": "Ledger", "qty": 2, "srd": "2024/adventuring-gear/book", "to": "character:bjorn-mountainfell"}],
             "currency": [{"gp": 120, "sp": 3, "to": "character:bjorn-mountainfell"}, {"cp": 5}]}
            """);
        var batch = ScenarioCalls.BatchId(text);

        Assert.Contains("- Awarded 300 XP: 300 each to Björn Mountainfell.\n", text, StringComparison.Ordinal);
        Assert.Contains("- Loot: Ledger ×2 to character:bjorn-mountainfell.\n- Coins to character:bjorn-mountainfell: 120 gp, 3 sp.\n" +
                        "- Coins to faction:the-party: 5 cp.\n", text, StringComparison.Ordinal);
        var sheet = await Bjorn();
        Assert.Contains("Ledger ×2 (`2024/adventuring-gear/book`; loot: Reef)", sheet, StringComparison.Ordinal);
        Assert.Contains("- **Coins:** 120 gp, 3 sp\n", sheet, StringComparison.Ordinal);
        Assert.Contains("the reef fight", await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "batch", "batch_id": "{{batch}}", "campaign": "deep"}"""),
            StringComparison.Ordinal);
    }

    // The line a write-back that moved items or coins carries right under its batch paragraph (fix F1, U03).
    private const string MoveOneItem =
        "To move one item to someone else, use campaign_character inventory on both sheets (currency for coins); undoing the write-back reverts " +
        "everything it wrote.";

    [Theory]
    [InlineData("""{"action": "end", "loot": [{"item": "+1 Shortsword", "to": "character:bjorn-mountainfell"}]}""")]
    [InlineData("""{"action": "end", "currency": [{"gp": 50, "to": "character:bjorn-mountainfell"}]}""")]
    public async Task End_AWriteBackThatGaveItemsOrCoins_SaysHowToMoveOneWithoutUndoingTheRest(string end)
    {
        // "Undo that, I gave the sword to the wrong person": the undo reverted every sheet with it. The end result says the
        // one-item fix is an inventory call on both sheets, and what the undo would take back.
        await ReefAsync();

        var text = await Combat(end);
        var batch = ScenarioCalls.BatchId(text);

        Assert.Contains($"\"campaign\": \"deep\"}}.\n{MoveOneItem}\n\n## Written back\n", text, StringComparison.Ordinal);
        Assert.StartsWith("# Reef — ended in round 1\n\ncombat end · deep · 2024 rules\n\n" + $"Batch `{batch}`.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action": "end", "loot": [{"item": "+1 Shortsword"}], "dry_run": true}""")]
    [InlineData("""{"action": "end", "discard": true}""")]
    [InlineData("""{"action": "end"}""")]
    public async Task End_NoItemOrCoinWrittenBack_SaysNothingAboutMovingOne(string end)
    {
        await ReefAsync();

        Assert.DoesNotContain("To move one item", await Combat(end), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_ASheetChangedSinceTheFightBegan_IsRefusedUntilForceWritesOverIt()
    {
        await ReefAsync();
        await ScenarioCalls.Call(_server, "campaign_character", """{"action": "update", "campaign": "deep", "character": "character:bjorn-mountainfell", "sheet": {"hp": 80}}""");

        var refused = await ScenarioCalls.Fail(_server, "combat", """{"action": "end"}""");
        var forced = await Combat("""{"action": "end", "force": true}""");

        Assert.Contains("end is refused: these changed since the fight began, and the write-back would overwrite them (give force: true to write over them, " +
                        "or end with discard: true to write nothing): Björn Mountainfell hp: 85 when it joined, 80 now.", refused, StringComparison.Ordinal);
        Assert.Contains("## Written back\n- Björn Mountainfell (`character:bjorn-mountainfell`) hp: 80 → 66\n", forced, StringComparison.Ordinal);
        Assert.Contains("## Written over (force)\n- Björn Mountainfell hp: 85 when it joined, 80 before this end\n", forced, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_AnItemUsedUpSinceTheFightBegan_IsRefused_AndForceSaysWhatTheFightUsedAndWhatIsLeft()
    {
        // Left open by F1 (FH): a forced holding read with the column wording, "used 2 in the fight when it joined, only 1 is
        // left before this end". A holding has no value "when it joined": it reads as the refusal does, what the fight used
        // and what is left, and force writes nothing for it.
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "inventory", "campaign": "deep", "character": "character:bjorn-mountainfell", "items": [{"item": "Potion of Healing", "qty": 2}]}""");
        await ReefAsync();
        await Combat("""{"action": "use", "targets": ["bjorn-mountainfell"], "item": "Potion of Healing", "amount": 2}""");
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "inventory", "campaign": "deep", "character": "character:bjorn-mountainfell", "items": [{"item": "Potion of Healing", "qty": -1}]}""");

        var refused = await ScenarioCalls.Fail(_server, "combat", """{"action": "end"}""");
        var forced = await Combat("""{"action": "end", "force": true}""");

        Assert.EndsWith(": Björn Mountainfell holding Potion of Healing: used 2 in the fight; only 1 is left.", refused, StringComparison.Ordinal);
        // F2R07: what force could not write is KEPT as the sheet has it, on a line of its own, never "written over".
        Assert.Contains("\n\nKept (force): Björn Mountainfell holding Potion of Healing: used 2 in the fight; only 1 is left (the sheet's count is kept)\n", forced,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Written over (force)", forced, StringComparison.Ordinal);
        Assert.Contains("Potion of Healing ×1", await Bjorn(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_SplitTheXpBetweenSheetsWithNoXpTotal_PrintsOneXpCallPerRecipient_ThatPutsItsShareOnItsSheetVerbatim()
    {
        // Fix F2, review U02, as headless session B ran it ("we won: end the fight and split the XP between the party"): the
        // sheets had no XP total, so the award was recorded and no sheet changed. Each recipient's call is printed, names
        // the campaign last, and sent as printed gives that sheet the amount it says.
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "update", "campaign": "deep", "character": "character:kaz", "sheet": {"level": 5, "ac": 14, "max_hp": 40}}""");
        await ReefAsync();

        var text = await Combat("""{"action": "end", "xp": 600}""");
        Assert.DoesNotContain("milestone", text, StringComparison.OrdinalIgnoreCase);

        var calls = Regex.Matches(text, @"campaign_character (\{""action"": ""xp"", [^\n]*?\})").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["character:bjorn-mountainfell", "character:kaz"], calls.Select(c => JsonNode.Parse(c)!["character"]!.GetValue<string>()).Order());
        foreach (var call in calls)
        {
            Assert.EndsWith(", \"campaign\": \"deep\"}", call, StringComparison.Ordinal);
            var node = JsonNode.Parse(call)!;
            await ScenarioCalls.Call(_server, "campaign_character", call);
            var sheet = await ScenarioCalls.Call(_server, "campaign_character",
                $$"""{"action": "get", "campaign": "deep", "character": "{{node["character"]!.GetValue<string>()}}"}""");
            Assert.Contains($"\n- **XP:** {node["amount"]!.GetValue<int>().ToString("N0", CultureInfo.InvariantCulture)} (", sheet, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task End_EnemiesDefeatedAndNoSheetHasAnXpTotal_PrintsOneXpCallPerRecipient_AndNeverSaysMilestone()
    {
        // Fix F2, review U02 (the deferred defeated-enemies path): with xp omitted and no party sheet holding an XP total, the
        // end printed only what the fight was worth, with no call; and "the sheet levels by milestone" claimed a policy nobody
        // chose. Each recipient now gets its call, which sent as printed gives that sheet the amount it says.
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "update", "campaign": "deep", "character": "character:kaz", "sheet": {"level": 5, "ac": 14, "max_hp": 40}}""");
        await Combat("""{"action": "start", "name": "Shore", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre"}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "total": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "ogre", "total": 3}]}""");
        await Combat("""{"action": "damage", "targets": ["ogre"], "amount": 68, "source": "bjorn-mountainfell"}""");

        var text = await Combat("""{"action": "end"}""");

        Assert.DoesNotContain("milestone", text, StringComparison.OrdinalIgnoreCase);
        var calls = Regex.Matches(text, @"campaign_character (\{""action"": ""xp"", [^\n]*?\})").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["character:bjorn-mountainfell", "character:kaz"], calls.Select(c => JsonNode.Parse(c)!["character"]!.GetValue<string>()).Order());
        foreach (var call in calls)
        {
            Assert.EndsWith(", \"campaign\": \"deep\"}", call, StringComparison.Ordinal);
            var node = JsonNode.Parse(call)!;
            await ScenarioCalls.Call(_server, "campaign_character", call);
            var sheet = await ScenarioCalls.Call(_server, "campaign_character",
                $$"""{"action": "get", "campaign": "deep", "character": "{{node["character"]!.GetValue<string>()}}"}""");
            Assert.Contains($"\n- **XP:** {node["amount"]!.GetValue<int>().ToString("N0", CultureInfo.InvariantCulture)} (", sheet, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task End_DryRunWithSheetsWithNoXpTotal_PrintsNoXpCall_TheRealEndsCallsCountTheShareOnce()
    {
        // F3, review F2R06: the dry run printed the start-a-total calls; sent before the real end (its own advice read as a
        // step to take), each started a sheet's total at its share, and the real end, now finding a total, added it again:
        // "xp: 33 → 66". The dry run names no call and says the real end prints them; the real end's, sent, count it once.
        await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "update", "campaign": "deep", "character": "character:kaz", "sheet": {"level": 5, "ac": 14, "max_hp": 40}}""");
        await Combat("""{"action": "start", "name": "Shore", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre"}]}""");
        await Combat("""{"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "total": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "ogre", "total": 3}]}""");
        await Combat("""{"action": "damage", "targets": ["ogre"], "amount": 68, "source": "bjorn-mountainfell"}""");

        var dry = await Combat("""{"action": "end", "dry_run": true}""");
        var text = await Combat("""{"action": "end"}""");

        Assert.DoesNotContain("campaign_character", dry, StringComparison.Ordinal);
        Assert.Contains("- XP not written to Kaz: the sheet has no XP total.\n- The real end prints the calls to start XP totals.\n", dry, StringComparison.Ordinal);
        var calls = Regex.Matches(text, @"campaign_character (\{""action"": ""xp"", [^\n]*?\})").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, calls.Count);
        foreach (var call in calls)
        {
            await ScenarioCalls.Call(_server, "campaign_character", call);
        }

        var kaz = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "campaign": "deep", "character": "character:kaz"}""");
        Assert.Contains("\n- **XP:** 225 (", kaz, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_ASheetWithHpButNoMaxHp_SetGaveItHp_WritesMaxHpWithHp()
    {
        // Review M02 (contract §6.2, D17): a sheet with hp and no max_hp joins with unknown hit points; set gives it 25, and
        // end writes max_hp with hp. It wrote hp alone, leaving the sheet "HP 25/—".
        await ScenarioCalls.Call(_server, "campaign_character", """{"action": "update", "campaign": "deep", "character": "character:kaz", "sheet": {"level": 3, "hp": 30}}""");
        await ReefAsync();
        await Combat("""{"action": "set", "combatants": [{"character": "character:kaz", "hp": 25}]}""");

        var end = await Combat("""{"action": "end", "xp": 0}""");

        Assert.Contains("- Kaz (`character:kaz`) max_hp: none → 25\n- Kaz (`character:kaz`) hp: 30 → 25\n", end, StringComparison.Ordinal);
        Assert.Contains("- Kaz: the sheet had no max_hp; the fight gave it 25, written as max_hp with hp.\n", end, StringComparison.Ordinal);
        Assert.Contains("HP 25/25", await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "campaign": "deep", "character": "character:kaz"}"""),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_APcLeftDying_RemindsTheWayOutWithCallsThatNameTheCampaignLast_OneBlankLineBetweenSections()
    {
        await ReefAsync();
        await Combat("""{"action": "damage", "targets": ["bjorn-mountainfell"], "amount": 66, "source": "shark"}""");

        var text = await Combat("""{"action": "end"}""");

        var reminders = text[text.IndexOf("\n\n## Reminders\n", StringComparison.Ordinal)..];
        Assert.Contains("continue with combat {\"action\": \"start\", …, \"campaign\": \"deep\"} and death_save", reminders, StringComparison.Ordinal);
        Assert.Contains("campaign_character {\"action\": \"heal\", \"character\": \"character:bjorn-mountainfell\", \"amount\": …, \"campaign\": \"deep\"}",
            reminders, StringComparison.Ordinal);
        Assert.Contains("\n\n## Summary\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_NothingToWrite_EndsWithNoBatchAndSaysSo()
    {
        await Combat("""{"action": "start", "name": "Quiet", "campaign": "deep", "add_party": false, "combatants": [{"name": "Shark", "hp": 30, "ac": 12}]}""");

        var text = await Combat("""{"action": "end"}""");

        Assert.Contains("\n\nNothing changed on any sheet: the fight ended with no batch to undo.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Z's undo guard (contract §6.7) refuses to undo the batch that created a sheet a fight that has not ended was seeded
    /// from, and prints the end call that clears it: that call, sent exactly as printed, must end the fight whatever its
    /// status, and the undo then goes through.
    /// </summary>
    [Theory]
    [InlineData("planned")]
    [InlineData("paused")]
    [InlineData("active")]
    public async Task End_TheUndoGuardsPrintedCall_EndsAPlannedPausedOrActiveFightVerbatim(string status)
    {
        var created = ScenarioCalls.BatchId(await ScenarioCalls.Call(_server, "campaign_character",
            """{"action": "update", "campaign": "deep", "character": "character:kaz", "sheet": {"level": 5, "ac": 14, "max_hp": 40}}"""));
        if (status == "active")
        {
            await Combat("""{"action": "start", "name": "Guarded \"one\"", "campaign": "deep", "add_party": false, "combatants": [{"character": "character:kaz"}]}""");
        }
        else
        {
            await Combat("""{"action": "prepare", "name": "Guarded \"one\"", "campaign": "deep", "combatants": [{"character": "character:kaz"}]}""");
            if (status == "paused")
            {
                SetStatus("paused");
            }
        }

        var refusal = await ScenarioCalls.Fail(_server, "campaign_history", $$"""{"action": "undo", "batch_id": "{{created}}", "campaign": "deep"}""");
        var printed = Regex.Match(refusal, @"combat (\{""action"": ""end"", [^\n]*?""campaign"": ""deep""\})").Groups[1].Value;

        Assert.Equal("{\"action\": \"end\", \"encounter\": \"Guarded \\\"one\\\"\", \"discard\": true, \"campaign\": \"deep\"}", printed);
        var ended = await Combat(printed);
        Assert.StartsWith("# Guarded \"one\" — ended in round 0\n", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", ended, StringComparison.Ordinal);
        Assert.StartsWith("# Undo of batch", await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "undo", "batch_id": "{{created}}", "campaign": "deep"}"""),
            StringComparison.Ordinal);
    }

    // No combat action pauses a fight (contract §6.0); the Repository's paused status is reached through the file.
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
