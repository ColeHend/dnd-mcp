using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The exit criterion through the MCP tools, fixture B (FIX §4 as amended by contract §16): the One Piece DM campaign's
/// sheets, the fishman monk and the Nester written by <c>campaign_write</c>/<c>campaign_character</c>, the Aboleth fought in
/// its lair with <c>combat</c> calls of §16's shapes, and the server's three rolls made with the scripted dice queued step by
/// step (B16 [5], B20 [4, 5, 2, 3], B22 [3, 2]). Every numbered step pins the text the model reads (heading, what changed
/// with the arithmetic, the rolls with their labels and secrecy, every reminder with its call); B27 pins the store (no
/// change_log, no sheet row touched, exactly the three dice_roll rows, each linked from one combat_log row whose amount is
/// its total); B28 / §4.3 the one write-back batch (sheets, the potion, the loot, the coins, the awards) as the end and
/// <c>campaign_get include:["sheet"]</c> print it and as the store holds it; §4.6-§4.8 the difficulty (in lair, book-only and
/// outside the lair), the simulations (the lair fight's form claiming no explicit equal) and <c>from_state</c>.
/// What breaks if these fail: the exit criterion "a full scripted combat round-trips to the sheet correctly", in the
/// campaign that rolls dice, awards XP and hands out loot.
/// </summary>
[Collection(ScenarioCombatOnePieceCollection.Name)]
public sealed class ScenarioCombatOnePieceTests(ScenarioCombatOnePiecePlay play)
{
    private const string Station = "# The dark station (fixture) — ";
    private const string Subtitle = " · one-piece · 2024 rules · in a lair · session 13\n\n";
    private const string Report = "# Fight simulation: ";

    private static string Legendary(string holder, int left) =>
        $"when {holder}'s turn ends, Aboleth may take a legendary action ({left}/4), before next combat " +
        "{\"action\": \"legendary\", \"source\": \"aboleth\", \"amount\": 1, \"name\": …, \"campaign\": \"one-piece\"}";

    /// <summary>The legendary step's damage call (fix F2, review LR03): the roll is the Aboleth's, never the turn-holder's.</summary>
    private static string LegendaryRoll(string holder, string action) =>
        $"Aboleth acts outside its turn (legendary action {action}): give its damage \"source\", or the roll is {holder}'s: combat " +
        "{\"action\": \"damage\", \"targets\": […], \"dice\": …, \"source\": \"aboleth\", \"campaign\": \"one-piece\"}";

    private static string Grappled(string who) =>
        $"{who} is grappled (Aboleth): Speed 0; Disadvantage on attacks against targets other than the grappler; the grappler can drag it";

    private static string Dying(int failures) => $"The fishman monk: 0 HP, 0 successes, {failures} failures";

    /// <summary>The 2024 death-save procedure the drop's reminder carries (fix F1 U05).</summary>
    private const string DeathSaves =
        "Death Saving Throws at the start of each of its turns: 10 or more succeeds (−2 per Exhaustion level); 3 successes, Stable; 3 failures, " +
        "dead; a 20 regains 1 Hit Point; a 1 counts as two failures; damage at 0 Hit Points is a failure, a Critical Hit two (and damage of at " +
        "least its Hit Point maximum kills).";

    private const string Monk = "The fishman monk";
    private const string Bjorn = "Björn Mountainfell";
    private const string Slayer = "The amethyst Dragon Slayer";
    private const string Exhaustion = "Björn Mountainfell — Exhaustion 1: D20 Tests −2; Speed −5 ft";

    private const string MonkProne =
        "The fishman monk is prone: its attack rolls have Disadvantage; attack rolls against it have Advantage within 5 ft, Disadvantage beyond; standing costs half its Speed";

    /// <summary>
    /// Every step's heading, action, "what changed" lines, rolls and reminders, as FIX §4.2 (as amended by §16) has them,
    /// checked against the rules: B4 Björn's 8 + 2 − 2 (2024 exhaustion on initiative); B7/B17 the reset with what it had;
    /// B8/B15 Bloodied at the first crossing only; B13/B24 Rage halves (12 → 6) but not psychic (B18); B16 the Aboleth's
    /// heal rolled secret, labelled "Aboleth"; B20 the critical doubles the dice the server rolls (4d6+5) and makes two
    /// failures, said in ONE dying line; B22 the potion's 2d4+2, open, from Björn's inventory; B26 the grapples end with the
    /// grappler, Eldritch Restoration, all enemies down.
    /// </summary>
    public static TheoryData<string, string, string, string[], string[], string[]> Steps() => new()
    {
        { "B1", "round 0 (roll initiative to begin round 1)", "start",
            [
                "Started \"The dark station (fixture)\" (2024, in a lair): round 0; roll initiative to begin round 1.",
                "Björn Mountainfell (party): 85/85 HP, AC 15, initiative +2, from its sheet.",
                "The amethyst Dragon Slayer (party): 68/68 HP, AC 16, initiative +2, from its sheet.",
                "The fishman monk (party): 59/59 HP, AC 16, initiative +3, from its sheet.",
            ], [], [] },
        { "B2", "round 0 (roll initiative to begin round 1)", "add",
            ["Aboleth (enemy): 150/150 HP, AC 17, initiative +3, legendary actions 4/4, Legendary Resistance 4/4."], [], [] },
        { "B3", "round 0 (roll initiative to begin round 1)", "condition", ["Björn Mountainfell: exhaustion 0 → 1."], [], [Exhaustion] },
        { "B4", "round 1 · The fishman monk's turn", "initiative",
            [
                "The fishman monk: initiative 19 (16 + 3).", "The amethyst Dragon Slayer: initiative 11 (9 + 2).",
                "Björn Mountainfell: initiative 8 (8 + 2 − 2 exhaustion).", "Aboleth: initiative 13 (given).",
            ], [], ["Round 1", Legendary(Monk, 4)] },
        { "B5-focus", "round 1 · The fishman monk's turn", "use", ["The fishman monk: Focus 2 used, 6/8 left."], [], [Legendary(Monk, 4)] },
        { "B5", "round 1 · The fishman monk's turn", "damage", ["Aboleth: 18 bludgeoning; 150 → 132"], [], [Legendary(Monk, 4)] },
        { "B5-resistance", "round 1 · The fishman monk's turn", "legendary", ["Aboleth uses Legendary Resistance: it succeeds instead (3/4 left)."], [], [Legendary(Monk, 4)] },
        { "B6-legendary", "round 1 · The fishman monk's turn", "legendary", ["Aboleth: legendary action Lash (1), 3/4 left."], [], [LegendaryRoll(Monk, "Lash"), Legendary(Monk, 3)] },
        { "B6", "round 1 · The fishman monk's turn", "damage", ["The fishman monk: 12 bludgeoning; 59 → 47"], [], [Legendary(Monk, 3)] },
        { "B7", "round 1 · Aboleth's turn", "next", ["Aboleth's turn (round 1)."], [], ["Aboleth: legendary actions reset: 4/4 (it had 3/4 left)"] },
        { "B8-monk", "round 1 · Aboleth's turn", "damage", ["The fishman monk: 12 bludgeoning; 47 → 35"], [], [] },
        { "B8-grapple-monk", "round 1 · Aboleth's turn", "condition", ["The fishman monk: grappled (Aboleth), until it escapes (DC 14)."], [], [] },
        { "B8-bjorn", "round 1 · Aboleth's turn", "damage", ["Björn Mountainfell: 12 bludgeoning; 85 → 73"], [], [] },
        { "B8-grapple-bjorn", "round 1 · Aboleth's turn", "condition", ["Björn Mountainfell: grappled (Aboleth), until it escapes (DC 14)."], [], [] },
        { "B8", "round 1 · Aboleth's turn", "damage", ["The fishman monk: 10 psychic; 35 → 25"], [], ["The fishman monk is Bloodied (25/59)"] },
        { "B9", "round 1 · Aboleth's turn", "condition",
            [
                "cursed (Mucus Cloud) is not an SRD condition: tracked as an effect.",
                "The fishman monk: cursed (Mucus Cloud) (Aboleth), until removed (it stays on the sheet after the fight).",
            ], [], [] },
        { "B10-next", "round 1 · The amethyst Dragon Slayer's turn", "next", ["The amethyst Dragon Slayer's turn (round 1)."], [], [Legendary(Slayer, 4)] },
        { "B10", "round 1 · The amethyst Dragon Slayer's turn", "damage", ["Aboleth: 22 bludgeoning + 4 psychic = 26; 132 → 106"], [], [Legendary(Slayer, 4)] },
        { "B11-legendary", "round 1 · The amethyst Dragon Slayer's turn", "legendary", ["Aboleth: legendary action Lash (1), 3/4 left."], [], [LegendaryRoll(Slayer, "Lash"), Legendary(Slayer, 3)] },
        { "B11", "round 1 · The amethyst Dragon Slayer's turn", "damage", ["The amethyst Dragon Slayer: 12 bludgeoning; 68 → 56"], [], [Legendary(Slayer, 3)] },
        { "B12-next", "round 1 · Björn Mountainfell's turn", "next", ["Björn Mountainfell's turn (round 1)."], [], [Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 3)] },
        { "B12-rage", "round 1 · Björn Mountainfell's turn", "condition",
            ["Björn Mountainfell: Rage 1 used, 3/4 left.", "Rage is not an SRD condition: tracked as an effect.", "Björn Mountainfell: Rage (Björn Mountainfell), for the fight."],
            [], [Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 3)] },
        { "B12", "round 1 · Björn Mountainfell's turn", "damage", ["Aboleth: 24 slashing; 106 → 82"], [], [Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 3)] },
        { "B13-legendary", "round 1 · Björn Mountainfell's turn", "legendary", ["Aboleth: legendary action Lash (1), 2/4 left."], [],
            [LegendaryRoll(Bjorn, "Lash"), Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 2)] },
        { "B13", "round 1 · Björn Mountainfell's turn", "damage", ["Björn Mountainfell: 12 bludgeoning ½ (resistant: Rage) = 6; 73 → 67"], [],
            [Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 2)] },
        { "B14", "round 2 · The fishman monk's turn", "next", ["The fishman monk's turn (round 2)."], [], ["Round 2", Grappled(Monk), Legendary(Monk, 2)] },
        { "B15-focus", "round 2 · The fishman monk's turn", "use", ["The fishman monk: Focus 2 used, 4/8 left."], [], [Grappled(Monk), Legendary(Monk, 2)] },
        { "B15", "round 2 · The fishman monk's turn", "damage", ["Aboleth: 20 bludgeoning; 82 → 62"], [], ["Aboleth is Bloodied (62/150)", Grappled(Monk), Legendary(Monk, 2)] },
        { "B15-resistance", "round 2 · The fishman monk's turn", "legendary", ["Aboleth uses Legendary Resistance: it succeeds instead (2/4 left)."], [],
            [Grappled(Monk), Legendary(Monk, 2)] },
        { "B16-legendary", "round 2 · The fishman monk's turn", "legendary", ["Aboleth: legendary action Psychic Drain (1), 1/4 left."], [],
            [LegendaryRoll(Monk, "Psychic Drain"), Grappled(Monk), Legendary(Monk, 1)] },
        { "B16-damage", "round 2 · The fishman monk's turn", "damage", ["The fishman monk: 10 psychic; 25 → 15"], [], [Grappled(Monk), Legendary(Monk, 1)] },
        { "B16", "round 2 · The fishman monk's turn", "heal", ["Aboleth: heals 5; 62 → 67"], ["heal: `1d10` [5] = 5 · logged as \"Aboleth: heal\" (secret)"],
            [Grappled(Monk), Legendary(Monk, 1)] },
        { "B17", "round 2 · Aboleth's turn", "next", ["Aboleth's turn (round 2)."], [], ["Aboleth: legendary actions reset: 4/4 (it had 1/4 left)"] },
        { "B18-first", "round 2 · Aboleth's turn", "damage", ["The fishman monk: 12 bludgeoning; 15 → 3"], [], [] },
        { "B18-second", "round 2 · Aboleth's turn", "damage", ["The fishman monk: 12 bludgeoning; 3 → 0"], [],
            ["The fishman monk drops to 0 HP: unconscious, dying (0 successes, 0 failures). " + DeathSaves, Dying(0)] },
        { "B18", "round 2 · Aboleth's turn", "damage", ["Björn Mountainfell: 10 psychic; 67 → 57"], [], [Dying(0)] },
        { "B19-next", "round 2 · The amethyst Dragon Slayer's turn", "next", ["The amethyst Dragon Slayer's turn (round 2)."], [], [Dying(0), Legendary(Slayer, 4)] },
        { "B19", "round 2 · The amethyst Dragon Slayer's turn", "damage", ["Aboleth: 24 bludgeoning + 4 psychic = 28; 67 → 39"], [], [Dying(0), Legendary(Slayer, 4)] },
        { "B20-legendary", "round 2 · The amethyst Dragon Slayer's turn", "legendary", ["Aboleth: legendary action Lash (1), 3/4 left."], [],
            [LegendaryRoll(Slayer, "Lash"), Dying(0), Legendary(Slayer, 3)] },
        { "B20", "round 2 · The amethyst Dragon Slayer's turn", "damage", ["The fishman monk: 19 bludgeoning; 0 → 0"],
            ["damage (critical): `4d6+5` [4, 5, 2, 3] = 19 · logged as \"Aboleth: damage (critical)\" (secret)"],
            ["The fishman monk is unconscious: a hit from within 5 ft is a Critical Hit", Dying(2) + " (a critical hit: two death save failures)", Legendary(Slayer, 3)] },
        { "B21", "round 2 · Björn Mountainfell's turn", "next", ["Björn Mountainfell's turn (round 2)."], [], [Grappled(Bjorn), Exhaustion, Dying(2), Legendary(Bjorn, 3)] },
        { "B22", "round 2 · Björn Mountainfell's turn", "heal",
            ["The fishman monk: heals 7; 0 → 7", "Björn Mountainfell: Potion of Healing used (1 left; the inventory is written when the fight ends)."],
            ["heal: `2d4+2` [3, 2] = 7 · logged as \"Björn Mountainfell: heal\""],
            ["The fishman monk regains consciousness at 7 HP (still prone)", Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 3)] },
        { "B23", "round 2 · Björn Mountainfell's turn", "damage", ["Aboleth: 26 slashing; 39 → 13"], [], [Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 3)] },
        { "B24-legendary", "round 2 · Björn Mountainfell's turn", "legendary", ["Aboleth: legendary action Lash (1), 2/4 left."], [],
            [LegendaryRoll(Bjorn, "Lash"), Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 2)] },
        { "B24", "round 2 · Björn Mountainfell's turn", "damage", ["Björn Mountainfell: 12 bludgeoning ½ (resistant: Rage) = 6; 57 → 51"], [],
            [Grappled(Bjorn), Exhaustion, Legendary(Bjorn, 2)] },
        { "B25", "round 3 · The fishman monk's turn", "next", ["The fishman monk's turn (round 3)."], [], ["Round 3", Grappled(Monk), MonkProne, Legendary(Monk, 2)] },
        { "B26", "round 3 · The fishman monk's turn", "damage", ["Aboleth: 18 bludgeoning; 13 → 0"], [],
            [
                "Aboleth dies (dropped to 0 hit points) and is defeated.", "grappled (Aboleth) ended on Björn Mountainfell: Aboleth is dead",
                "grappled (Aboleth) ended on The fishman monk: Aboleth is dead",
                "Aboleth — Eldritch Restoration: If destroyed, the aboleth gains a new body in 5d10 days, reviving with all its Hit Points in the Far Realm " +
                "or another location chosen by the GM.",
                MonkProne, "all enemies are defeated: end the combat? combat {\"action\": \"end\", \"outcome\": …, \"campaign\": \"one-piece\"}",
            ] },
    };

    [Fact]
    public void OnePiece_TheScript_IsEveryStepOfFixB_InOrder()
    {
        Assert.Equal(Steps().Select(row => (string)row[0]), play.Steps.Select(s => s.Step));
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void OnePiece_EachStep_SaysWhatFixSays_HeadingChangesRollsAndReminders(string step, string heading, string action, string[] changed, string[] rolls, string[] reminders)
    {
        var text = play[step].Text;

        Assert.StartsWith(Station + heading + "\n\ncombat " + action + Subtitle, text, StringComparison.Ordinal);
        Assert.Equal(changed, ScenarioCombatText.Section(text, "What changed"));
        Assert.Equal(rolls, ScenarioCombatText.Section(text, "Rolls"));
        Assert.Equal(reminders, ScenarioCombatText.Section(text, "Reminders"));
        Assert.Empty(ScenarioCombatText.Section(text, "Warnings"));
    }

    [Theory]
    [InlineData("B1", "|  | — | — | The fishman monk (character:fishman-monk) | party | 59/59 | 16 | — |")]
    [InlineData("B2", "|  | — | — | Aboleth (character:the-nester) | enemy | 150/150 | 17 | legendary actions 4/4; Legendary Resistance 4/4 |")]
    [InlineData("B4", "|  | 2 | 13 | Aboleth (character:the-nester) | enemy | 150/150 | 17 | legendary actions 4/4; Legendary Resistance 4/4 |")]
    [InlineData("B4", "|  | 4 | 8 | Björn Mountainfell (character:bjorn-mountainfell) | party | 85/85 | 15 | exhaustion 1 |")]
    [InlineData("B15", "|  | 2 | 13 | Aboleth (character:the-nester) | enemy | 62/150 | 17 | legendary actions 2/4; Legendary Resistance 3/4 |")]
    [InlineData("B20", "|  | 1 | 19 | The fishman monk (character:fishman-monk) | party | 0/59 · death saves: 0 successes, 2 failures | 16 | grappled (Aboleth); cursed (Mucus Cloud) (Aboleth); unconscious; prone |")]
    [InlineData("B22", "|  | 1 | 19 | The fishman monk (character:fishman-monk) | party | 7/59 | 16 | grappled (Aboleth); cursed (Mucus Cloud) (Aboleth); prone |")]
    [InlineData("B22", "| ▶ | 4 | 8 | Björn Mountainfell (character:bjorn-mountainfell) | party | 57/85 | 15 | grappled (Aboleth); Rage (Björn Mountainfell); exhaustion 1 |")]
    [InlineData("B26", "|  | 2 | 13 | ~~Aboleth (character:the-nester)~~ | enemy | dead | 17 | legendary actions 2/4; Legendary Resistance 2/4 |")]
    [InlineData("B26", "|  | 4 | 8 | Björn Mountainfell (character:bjorn-mountainfell) | party | 51/85 | 15 | Rage (Björn Mountainfell); exhaustion 1 |")]
    public void OnePiece_TheTableRowsFixNames_AtTheirSteps(string step, string row)
    {
        Assert.Contains(row + "\n", play[step].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnePiece_B0_Session13StartsLive_TheThreePcsAttend()
    {
        Assert.StartsWith("# Session 13 started (one-piece)\n\nBatch `", play.SessionStart, StringComparison.Ordinal);
        Assert.Contains("Changed: session, attendance (added character:bjorn-mountainfell, character:fishman-monk, character:dragon-slayer).",
            play.SessionStart, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B27: the invariants and the dice

    [Fact]
    public void OnePiece_B27_NoStepWritesChangeLog_OrTouchesASheetRow_TheServerRollsOnlyAtB16B20B22()
    {
        Assert.All(play.Steps, s => Assert.Equal(play.N0, s.ChangeRows));
        Assert.All(play.Steps, s => Assert.Equal(play.SheetTablesBefore, s.SheetTables));

        // The faces queued before each call were the only dice rolled, in FIX §4.1's order, and only at those three steps.
        Assert.Equal([(10, 5), (6, 4), (6, 5), (6, 2), (6, 3), (4, 3), (4, 2)], play.World.Dice.Rolled.Take(7));
        var rolledAt = play.Steps.Zip(play.Steps.Skip(1)).Where(p => p.Second.Rolled > p.First.Rolled).Select(p => p.Second.Step);
        Assert.Equal(["B16", "B20", "B22"], rolledAt);
        Assert.Equal([1, 2, 3], new[] { "B16", "B20", "B22" }.Select(s => play[s].DiceRows));
        Assert.Equal(0, play["B15"].DiceRows);
    }

    [Fact]
    public void OnePiece_B27_ThreeDiceRolls_LinkedToTheFight_SecretSecretOpen_EachFromOneCombatLogRowOfItsTotal()
    {
        var dice = play.World.Store.Dice("one-piece").Where(d => d.EncounterId == play.EncounterId).ToList();
        var log = play.World.Store.CombatLog(play.EncounterId);

        Assert.Equal(
            [("1d10", "Aboleth: heal", 5L, true), ("4d6+5", "Aboleth: damage (critical)", 19L, true), ("2d4+2", "Björn Mountainfell: heal", 7L, false)],
            dice.Select(d => (d.Expression, d.Label!, d.Total, d.Secret)));
        Assert.All(dice, d => Assert.Equal(13L, d.SessionNumber));
        Assert.All(dice, d => Assert.Equal("combat", JsonNode.Parse(d.Detail)!["source"]!.GetValue<string>()));
        Assert.Equal([[5], [4, 5, 2, 3], [3, 2]], dice.Select(d => Faces(d.Detail)));
        foreach (var roll in dice)
        {
            var row = Assert.Single(log, r => r.RollId == roll.Id);
            Assert.Equal(roll.Total, row.Amount);
        }

        Assert.Equal(["heal", "damage", "heal"], dice.Select(d => log.Single(r => r.RollId == d.Id).Kind));
        Assert.Equal(3, log.Count(r => r.RollId is not null));
        Assert.All(log.Where(r => r.Kind == "damage" && r.RollId is null), r => Assert.True(JsonNode.Parse(r.Detail!)!["given"]!.GetValue<bool>(), r.Detail));
        foreach (var kind in new[] { "start", "add", "initiative", "turn", "damage", "heal", "condition", "legendary", "resource", "defeat", "end" })
        {
            Assert.Contains(log, r => r.Kind == kind);
        }

        Assert.Single(log, r => r.Kind == "defeat");
        Assert.Equal(6, log.Count(r => r.Kind == "legendary" && JsonNode.Parse(r.Detail ?? "{}")!["resistance"] is null));
    }

    [Fact]
    public void OnePiece_EveryStep_EachOtherDyingCreature_HasExactlyOneDyingLine_TheTurnHolderNone()
    {
        var dyingSteps = new List<string>();
        foreach (var step in play.Steps)
        {
            var reminders = ScenarioCombatText.Section(step.Text, "Reminders");
            foreach (var row in ScenarioCombatText.Table(step.Text).Where(r => !r.Struck && r.Hp.StartsWith("0/", StringComparison.Ordinal) && r.Hp.Contains("death saves", StringComparison.Ordinal)))
            {
                var lines = reminders.Count(l => l.StartsWith(row.Name + ": 0 HP, ", StringComparison.Ordinal));
                Assert.True(lines == (row.Turn ? 0 : 1), $"{step.Step}: {lines} dying lines for {row.Name}");
                dyingSteps.Add(step.Step);
            }
        }

        Assert.Equal(["B18-second", "B18", "B19-next", "B19", "B20-legendary", "B20", "B21"], dyingSteps);
    }

    [Fact]
    public void OnePiece_EveryPrintedCall_NamesTheCampaignLastAndOnce()
    {
        var texts = play.Steps.Select(s => (s.Step, s.Text))
            .Append(("end", play.End)).Append(("end dry run", play.EndDryRun)).Append(("last", play.LastAuthor)).Append(("prepare", play.Prepared))
            .Append(("lair simulation", play.LairSimulation)).Append(("from_state", play.FromState.First)).Append(("session start", play.SessionStart))
            .Append(("Björn's rest", play.Rests.Bjorn)).Append(("the Nester's rest", play.Rests.Nester)).Append(("the Nester's sheet", play.NesterSheet))
            .Append(("the arch mage's rest", play.Rests.ArchMage)).Append(("the arch mage's sheet", play.ArchMage.Sheet)).Append(("the arch mage's damage", play.ArchMage.Damage))
            .Append(("the planned simulation", play.PlannedSimulation)).Append(("the no-lair difficulty", play.DifficultyNoLair));

        var calls = 0;
        foreach (var (label, text) in texts)
        {
            foreach (var call in ScenarioCombatText.PrintedCalls(text))
            {
                calls++;
                Assert.True(call.EndsWith(", \"campaign\": \"one-piece\"}", StringComparison.Ordinal), $"{label}: {call}");
                Assert.Single(Regex.Matches(call, "\"campaign\":"));
            }
        }

        Assert.Equal(51, calls);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B28 and §4.3

    [Fact]
    public void OnePiece_B28_EndIsOneBatchUnderSession13_WritingExactlyFixFourPointThree()
    {
        Assert.Equal(
            "# The dark station (fixture) — ended in round 3\n\ncombat end · one-piece · 2024 rules · in a lair\n\n" +
            $"Batch `{play.EndBatch}`. Session context: session 13. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{play.EndBatch}\", \"campaign\": \"one-piece\"}}.\n" +
            Formatting.Campaign.CombatMarkdown.MoveOneItem + "\n\n",
            play.End[..(play.End.IndexOf("## Written back", StringComparison.Ordinal))]);
        Assert.Equal(
            [
                "Björn Mountainfell (`character:bjorn-mountainfell`) xp: 34000 → 36400",
                "Björn Mountainfell (`character:bjorn-mountainfell`) hp: 85 → 51",
                "Björn Mountainfell (`character:bjorn-mountainfell`) resources rage: {\"name\":\"Rage\",\"max\":4,\"used\":0,\"recharge\":\"short_rest_one\"} → " +
                "{\"name\":\"Rage\",\"max\":4,\"used\":1,\"recharge\":\"short_rest_one\"}",
                "Björn Mountainfell (`character:bjorn-mountainfell`) exhaustion: 0 → 1",
                "The amethyst Dragon Slayer (`character:dragon-slayer`) xp: 34000 → 36400",
                "The amethyst Dragon Slayer (`character:dragon-slayer`) hp: 68 → 56",
                "The fishman monk (`character:fishman-monk`) xp: 34000 → 36400",
                "The fishman monk (`character:fishman-monk`) hp: 59 → 7",
                "The fishman monk (`character:fishman-monk`) resources focus: {\"name\":\"Focus\",\"max\":8,\"used\":0,\"recharge\":\"short_rest\"} → " +
                "{\"name\":\"Focus\",\"max\":8,\"used\":4,\"recharge\":\"short_rest\"}",
                "The fishman monk (`character:fishman-monk`) conditions: [] → [{\"name\":\"cursed (Mucus Cloud)\",\"source\":\"Aboleth\",\"duration\":\"until_removed\"," +
                "\"note\":\"from The dark station (fixture), round 1\"}]",
            ],
            ScenarioCombatText.Section(play.End, "Written back"));
        Assert.Equal(
            [
                "Björn Mountainfell: Potion of Healing 2 → 1.",
                "Awarded 7,200 XP: 2,400 each to Björn Mountainfell, The amethyst Dragon Slayer, The fishman monk.",
                "Björn Mountainfell: hp 85 → 51.", "Björn Mountainfell: exhaustion → 1.", "Björn Mountainfell: Rage 3/4 left.", "Björn Mountainfell: XP 34,000 → 36,400.",
                "The amethyst Dragon Slayer: hp 68 → 56.", "The amethyst Dragon Slayer: XP 34,000 → 36,400.",
                "The fishman monk: hp 59 → 7.", "The fishman monk: conditions kept on the sheet: cursed (Mucus Cloud).", "The fishman monk: Focus 4/8 left.",
                "The fishman monk: XP 34,000 → 36,400.",
                "Ended with the fight: Rage (Björn Mountainfell); prone (The fishman monk).",
                "Loot: The sixth station's ledger ×1 to faction:the-party.", "Loot: Potion of Water Breathing ×1 to character:bjorn-mountainfell.",
                "Coins to faction:the-party: 120 gp.",
            ],
            ScenarioCombatText.Section(play.End, "Summary"));

        // D19: the Nester's status proposal, author-only and NOT applied; it works verbatim as the dry run it is.
        Assert.Equal(
            [
                "Proposed: mark character:the-nester dead (Aboleth died in this fight); it has Eldritch Restoration, so consider \"unknown\". Not applied. " +
                "campaign_write {\"ops\": [{\"op\": \"status\", \"ref\": \"character:the-nester\", \"status\": \"dead\"}], \"dry_run\": true, \"campaign\": \"one-piece\"}",
            ],
            ScenarioCombatText.Section(play.End, "Proposals (not applied)"));
        Assert.Equal(
            "# Dry run: campaign_write, 1 op (one-piece): nothing written\n\n" +
            "**Dry run: nothing was written and no batch exists.** This is exactly what the call would do, including the codes it would assign; send the same call" +
            " without dry_run to apply it. Session context: session 13.\n\n" +
            "| # | Op | Ref | Outcome | Changed |\n" +
            "|---|---|---|---|---|\n" +
            "| 1 | status | character:the-nester | updated | status |\n\n" +
            "## Warnings (1)\n" +
            "Applied anyway; read them before the table does.\n" +
            "- **warning** · character dead · ops item 1: character:the-nester is dead now: review its member_of relation (status former, until) and what it knows" +
            " (its knowledge rows are kept).\n",
            play.ProposalDryRun);

        // FIX §4.3: the Nester stays alive until the proposal is applied (the end applies nothing to its entity).
        Assert.Contains("\ncharacter · npc · status alive\n", play.NesterAfterEnd, StringComparison.Ordinal);
        Assert.Equal("alive", play.World.Store.EntityStatus("one-piece", "the-nester"));
        Assert.Equal(
            ["Aboleth — Eldritch Restoration: If destroyed, the aboleth gains a new body in 5d10 days, reviving with all its Hit Points in the Far Realm or another " +
             "location chosen by the GM."],
            ScenarioCombatText.Section(play.End, "Reminders"));

        // The rows the end created, as stored (§4.3): the loot filed under session 13 with the fight's note, the coins' note,
        // the three awards' session, kind and source; the potion's quantity 1 (bought before any session, so none).
        Assert.Equal(
            [
                "holding who=bjorn-mountainfell | name=Potion of Healing | srd_ref=2024/equipment/potion-of-healing | quantity=1 | equipped=0 | attuned=0 | charges=NULL | " +
                "session=NULL | notes=NULL",
                "holding who=bjorn-mountainfell | name=Potion of Water Breathing | srd_ref=2024/magic-item/potion-of-water-breathing | quantity=1 | equipped=0 | attuned=0 | " +
                "charges=NULL | session=13 | notes=loot: The dark station (fixture)",
                "holding who=the-party | name=The sixth station's ledger | srd_ref=NULL | quantity=1 | equipped=0 | attuned=0 | charges=NULL | session=13 | " +
                "notes=loot: The dark station (fixture)",
                "coins who=the-party | session=13 | cp=0 | sp=0 | ep=0 | gp=120 | pp=0 | note=The dark station (fixture)",
                "award who=bjorn-mountainfell | session=13 | kind=xp | amount=2400 | note=NULL | source=encounter: The dark station (fixture)",
                "award who=dragon-slayer | session=13 | kind=xp | amount=2400 | note=NULL | source=encounter: The dark station (fixture)",
                "award who=fishman-monk | session=13 | kind=xp | amount=2400 | note=NULL | source=encounter: The dark station (fixture)",
            ],
            play.World.Store.LedgerRows("one-piece"));

        // One batch: sheets, the potion's quantity, the two loot holdings, the coins and the three awards; no combat table.
        Assert.Equal(play.N0 + 17, play.ChangeRowsAfterEnd);
        Assert.Equal(17, play.World.Store.BatchRows(play.EndBatch));
        Assert.Equal(["award", "character_sheet", "currency_txn", "holding"], play.World.Store.BatchTables(play.EndBatch));
        Assert.Equal(("ended", play.EndBatch), play.World.Store.Encounter(play.EncounterId));
    }

    [Fact]
    public void OnePiece_B28_TheDryRun_WouldAward_WritesNothing_AndSaysSo()
    {
        Assert.StartsWith(
            "# Dry run: The dark station (fixture) — would end in round 3\n\ncombat end · one-piece · 2024 rules · in a lair\n\n" +
            "**Dry run: nothing was written and no batch exists.** The fight is still running; this is what ending it would write. " +
            "Send the same call without dry_run to end it. Session context: session 13.\n\n## Would be written back\n",
            play.EndDryRun, StringComparison.Ordinal);
        Assert.Equal(ScenarioCombatText.Section(play.End, "Written back"), ScenarioCombatText.Section(play.EndDryRun, "Would be written back"));
        Assert.Contains("Would award 7,200 XP: 2,400 each to Björn Mountainfell, The amethyst Dragon Slayer, The fishman monk.",
            ScenarioCombatText.Section(play.EndDryRun, "Summary"));
        Assert.DoesNotContain("Awarded", play.EndDryRun, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", play.EndDryRun, StringComparison.Ordinal);
    }

    /// <summary>
    /// §4.3 through <c>campaign_get include:["sheet"]</c>, each PC's whole sheet block before the fight and after the end: HP,
    /// Rage 3/4, Focus 4/8, Björn's exhaustion 1 (a column, never also a condition), the curse kept with its source and
    /// note, XP 36,400 (the next level at 48,000), the potion 2 → 1 and the loot; and nothing the fight held that the
    /// write-back must drop (grapples, Rage, Prone, Unconscious, death saves).
    /// </summary>
    [Fact]
    public void OnePiece_43_TheSheetsBeforeAndAfter_ThroughCampaignGet_EachWholeBlock()
    {
        Assert.Equal(
            "### Sheet\n" +
            "level 8 Barbarian (Path of the Totem Warrior), 2024\n" +
            "`character:bjorn-mountainfell` · source fixture\n" +
            "HP 85/85 · AC 15 · Init +2 · PB +3 · Exhaustion 0\n\n" +
            "- **Resources:** Rage 4/4 (short rest one)\n" +
            "- **Abilities:** Str 18 (+4) · Dex 14 (+2) · Con 16 (+3)\n" +
            "- **Saves:** proficient Str, Con\n" +
            "- **Hit Dice:** d12 8/8 left\n" +
            "- **XP:** 34,000 (the next level at 48,000)\n" +
            "- **Inventory:** Potion of Healing ×2 (`2024/equipment/potion-of-healing`)\n" +
            "- **Coins:** none\n",
            SheetBlock(play.SheetsBefore, Bjorn));
        Assert.Equal(
            "### Sheet\n" +
            "level 8 dragon slayer (Amethyst), 2024\n" +
            "`character:dragon-slayer` · source fixture\n" +
            "HP 68/68 · AC 16 · Init +2 · PB +3 · Exhaustion 0\n\n" +
            "- **Abilities:** Str 18 (+4) · Dex 14 (+2) · Con 14 (+2) · Int 10 (+0) · Wis 12 (+1) · Cha 10 (+0)\n" +
            "- **Saves:** proficient Con, Str\n" +
            "- **Hit Dice:** d10 8/8 left\n" +
            "- **XP:** 34,000 (the next level at 48,000)\n" +
            "- **Coins:** none\n" +
            "- **sim_profile:** Amethyst Dragon Slayer L8 (fixture), 2024, level 8; attacks Draconic Strike; modifiers Amethyst rider\n",
            SheetBlock(play.SheetsBefore, Slayer));
        Assert.Equal(
            "### Sheet\n" +
            "level 8 Monk (Order of the Deep Sea), 2024\n" +
            "`character:fishman-monk` · source fixture\n" +
            "HP 59/59 · AC 16 · Init +3 · PB +3 · Exhaustion 0\n\n" +
            "- **Resources:** Focus 8/8 (short rest)\n" +
            "- **Abilities:** Dex 16 (+3) · Con 14 (+2) · Wis 16 (+3)\n" +
            "- **Saves:** proficient Str, Dex\n" +
            "- **Hit Dice:** d8 8/8 left\n" +
            "- **XP:** 34,000 (the next level at 48,000)\n" +
            "- **Other:** Spell save DC 14\n" +
            "- **Coins:** none\n",
            SheetBlock(play.SheetsBefore, Monk));

        Assert.Equal(
            "### Sheet\n" +
            "level 8 Barbarian (Path of the Totem Warrior), 2024\n" +
            "`character:bjorn-mountainfell` · source fixture\n" +
            "HP 51/85 · AC 15 · Init +2 · PB +3 · Exhaustion 1\n\n" +
            "- **Resources:** Rage 3/4 (short rest one)\n" +
            "- **Abilities:** Str 18 (+4) · Dex 14 (+2) · Con 16 (+3)\n" +
            "- **Saves:** proficient Str, Con\n" +
            "- **Hit Dice:** d12 8/8 left\n" +
            "- **XP:** 36,400 (the next level at 48,000)\n" +
            "- **Inventory:** Potion of Healing ×1 (`2024/equipment/potion-of-healing`) · Potion of Water Breathing ×1" +
            " (`2024/magic-item/potion-of-water-breathing`; loot: The dark station (fixture))\n" +
            "- **Coins:** none\n",
            SheetBlock(play.SheetsAfter, Bjorn));
        Assert.Equal(
            "### Sheet\n" +
            "level 8 dragon slayer (Amethyst), 2024\n" +
            "`character:dragon-slayer` · source fixture\n" +
            "HP 56/68 · AC 16 · Init +2 · PB +3 · Exhaustion 0\n\n" +
            "- **Abilities:** Str 18 (+4) · Dex 14 (+2) · Con 14 (+2) · Int 10 (+0) · Wis 12 (+1) · Cha 10 (+0)\n" +
            "- **Saves:** proficient Con, Str\n" +
            "- **Hit Dice:** d10 8/8 left\n" +
            "- **XP:** 36,400 (the next level at 48,000)\n" +
            "- **Coins:** none\n" +
            "- **sim_profile:** Amethyst Dragon Slayer L8 (fixture), 2024, level 8; attacks Draconic Strike; modifiers Amethyst rider\n",
            SheetBlock(play.SheetsAfter, Slayer));
        Assert.Equal(
            "### Sheet\n" +
            "level 8 Monk (Order of the Deep Sea), 2024\n" +
            "`character:fishman-monk` · source fixture\n" +
            "HP 7/59 · AC 16 · Init +3 · PB +3 · Exhaustion 0\n\n" +
            "- **Resources:** Focus 4/8 (short rest)\n" +
            "- **Conditions:** cursed (Mucus Cloud) (until removed; from Aboleth; from The dark station (fixture), round 1)\n" +
            "- **Abilities:** Dex 16 (+3) · Con 14 (+2) · Wis 16 (+3)\n" +
            "- **Saves:** proficient Str, Dex\n" +
            "- **Hit Dice:** d8 8/8 left\n" +
            "- **XP:** 36,400 (the next level at 48,000)\n" +
            "- **Other:** Spell save DC 14\n" +
            "- **Coins:** none\n",
            SheetBlock(play.SheetsAfter, Monk));
    }

    [Fact]
    public void OnePiece_B28_TheEndedFight_AsTheAuthorReadsItLast()
    {
        Assert.StartsWith(
            "# The dark station (fixture) — ended in round 3\n\ncombat state · one-piece · 2024 rules · in a lair · session 13\n\n" +
            $"Its write-back is batch `{play.EndBatch}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{play.EndBatch}\", \"campaign\": \"one-piece\"}}.\n" +
            "Outcome: The sixth station is clear.\n",
            play.LastAuthor, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // §4.6-§4.8 through the tools

    /// <summary>
    /// §4.6 through the tool, whole: the party read from the sheets ([8, 8, 8]) with the campaign's offset +1 (its note),
    /// both editions' rows at the book and the effective level (2014 thresholds L8 1,350 / 2,700 / 4,200 / 6,300 and L9
    /// 1,650 / 3,300 / 4,800 / 7,200; 2024 budgets L8 3,000 / 5,100 / 6,300 and L9 3,900 / 6,000 / 7,800), 2014 Hard both ways
    /// (5,900 × 1; no in-lair XP in 2014), 2024 Beyond High at the book level and High at the effective level (7,200 in lair),
    /// the adventuring day (about 18,000, 32%) and the troubleshooting line.
    /// </summary>
    [Fact]
    public void OnePiece_46_EncounterDifficultyOfTheCampaignsParty_ThreeAtEight_OffsetOne_Hard2014_BeyondHigh2024_HighAtEffectiveLevel()
    {
        Assert.Equal(
            "# Encounter difficulty\n\n" +
            "**Party:** 3 characters at level 8. **Effective level:** +1 (3 characters at level 9).\n\n" +
            "| Monster | Count | 2014 | 2024 |\n" +
            "|---|---|---|---|\n" +
            "| Aboleth (`2014/monster/aboleth`, `2024/monster/aboleth`) | 1 | CR 10, 5,900 XP | CR 10, 7,200 XP (in lair) |\n" +
            "| **Total** | **1** | **5,900 XP** | **7,200 XP** |\n\n" +
            "## 2014 rules: Hard (the same at effective level +1)\n\n" +
            "| Party | Easy | Medium | Hard | Deadly |\n" +
            "|---|---|---|---|---|\n" +
            "| 3 × level 8 | 1,350 | 2,700 | 4,200 | 6,300 |\n" +
            "| Effective level (+1) | 1,650 | 3,300 | 4,800 | 7,200 |\n\n" +
            "- Monster XP 5,900 × 1 = **5,900 adjusted XP**: × 1 for 1 monster (the 1 row, with 3–5 characters).\n" +
            "- That reaches Hard (4,200) but not Deadly (6,300).\n" +
            "- At effective level +1 it reaches Hard (4,800) but not Deadly (7,200).\n" +
            "- Adventuring day: this party can handle about 18,000 adjusted XP before a long rest, so this fight is 32% of a day.\n" +
            "- The party earns 5,900 XP (about 1,966 each); the multiplier only judges difficulty.\n\n" +
            "## 2024 rules: Beyond High (High at effective level +1)\n\n" +
            "| Party | Low | Moderate | High |\n" +
            "|---|---|---|---|\n" +
            "| 3 × level 8 | 3,000 | 5,100 | 6,300 |\n" +
            "| Effective level (+1) | 3,900 | 6,000 | 7,800 |\n\n" +
            "- Monster XP **7,200** is over the High budget (6,300) by 900 (1.14× the budget). The SRD's difficulties stop at High, so expect worse than High.\n" +
            "- At effective level +1 it is over the Moderate budget (6,000) and fits High (7,800).\n" +
            "- No multiplier for groups in 2024. The party earns 7,200 XP (2,400 each).\n\n" +
            "**Troubleshooting (SRD 5.2.1):**\n" +
            "- Aboleth is CR 10, above the party's level (8): it might take out one or more characters with a single action. Also check it has no feature those" +
            " characters can't easily overcome.\n\n" +
            "## The editions compared\n\n" +
            "- The 2024 budgets are not the 2014 thresholds renamed: Low equals Medium only up to level 7, Moderate equals Hard up to level 5 and High equals" +
            " Deadly up to level 8. For this party: Low 3,000 vs Medium 2,700; Moderate 5,100 vs Hard 4,200; High 6,300 vs Deadly 6,300.\n" +
            "- The labels are read in opposite directions: 2014 names a fight by the highest threshold its adjusted XP reaches, 2024 by the smallest budget its XP" +
            " fits. So with equal numbers, XP between Medium and Hard is 2014 Medium but 2024 Moderate, and anything up to the Low budget is 2024 Low even where" +
            " 2014 calls it Trivial.\n" +
            "- 2014's multiplier is × 1 here, so each edition judges its own plain XP total.\n" +
            "- The monsters' XP differs between the editions (5,900 in 2014, 7,200 in 2024): Aboleth is worth 5,900 XP in 2014 and 7,200 (in lair) in 2024.\n\n" +
            "**Notes:**\n" +
            "- Party: the one-piece campaign's 3 current members (Björn Mountainfell 8, The amethyst Dragon Slayer 8, The fishman monk 8).\n" +
            "- Effective level +1: the active campaign's (one-piece) effective_level_offset; pass effective_level_offset 0 for the book levels alone.\n" +
            "- Aboleth (`2014/monster/aboleth`): its stat block gives no in-lair XP, so lair changes nothing; its normal XP is used.\n" +
            "- 2024 classifies a fight as the lowest difficulty whose budget its XP fits, as the SRD's worked examples do (the SRD describes only building to a" +
            " budget).\n\n" +
            "*Sources: 2014 thresholds, multipliers and adventuring-day XP: Dungeon Master's Guide (2014), pp. 82–84, also in the free 2014 Basic Rules (not SRD" +
            " 5.1); 2024 budget and troubleshooting: SRD 5.2.1, Gameplay Toolbox › Combat Encounter Difficulty; CR and XP: the SRD stat blocks, or the XP by CR" +
            " table for a monster given by CR. The tables: rules_get ref \"rules://tables\".*\n",
            play.Difficulty);
    }

    /// <summary>§4.6's book-only variant: an explicit <c>effective_level_offset: 0</c> wins over the campaign's 1, so the effective rows and the offset note go.</summary>
    [Fact]
    public void OnePiece_46_TheBookLevelsAlone_AnExplicitZeroWinsOverTheCampaignsOffset()
    {
        Assert.Equal(
            "# Encounter difficulty\n\n" +
            "**Party:** 3 characters at level 8.\n\n" +
            "| Monster | Count | 2014 | 2024 |\n" +
            "|---|---|---|---|\n" +
            "| Aboleth (`2014/monster/aboleth`, `2024/monster/aboleth`) | 1 | CR 10, 5,900 XP | CR 10, 7,200 XP (in lair) |\n" +
            "| **Total** | **1** | **5,900 XP** | **7,200 XP** |\n\n" +
            "## 2014 rules: Hard\n\n" +
            "| Party | Easy | Medium | Hard | Deadly |\n" +
            "|---|---|---|---|---|\n" +
            "| 3 × level 8 | 1,350 | 2,700 | 4,200 | 6,300 |\n\n" +
            "- Monster XP 5,900 × 1 = **5,900 adjusted XP**: × 1 for 1 monster (the 1 row, with 3–5 characters).\n" +
            "- That reaches Hard (4,200) but not Deadly (6,300).\n" +
            "- Adventuring day: this party can handle about 18,000 adjusted XP before a long rest, so this fight is 32% of a day.\n" +
            "- The party earns 5,900 XP (about 1,966 each); the multiplier only judges difficulty.\n\n" +
            "## 2024 rules: Beyond High\n\n" +
            "| Party | Low | Moderate | High |\n" +
            "|---|---|---|---|\n" +
            "| 3 × level 8 | 3,000 | 5,100 | 6,300 |\n\n" +
            "- Monster XP **7,200** is over the High budget (6,300) by 900 (1.14× the budget). The SRD's difficulties stop at High, so expect worse than High.\n" +
            "- No multiplier for groups in 2024. The party earns 7,200 XP (2,400 each).\n\n" +
            "**Troubleshooting (SRD 5.2.1):**\n" +
            "- Aboleth is CR 10, above the party's level (8): it might take out one or more characters with a single action. Also check it has no feature those" +
            " characters can't easily overcome.\n\n" +
            "## The editions compared\n\n" +
            "- The 2024 budgets are not the 2014 thresholds renamed: Low equals Medium only up to level 7, Moderate equals Hard up to level 5 and High equals" +
            " Deadly up to level 8. For this party: Low 3,000 vs Medium 2,700; Moderate 5,100 vs Hard 4,200; High 6,300 vs Deadly 6,300.\n" +
            "- The labels are read in opposite directions: 2014 names a fight by the highest threshold its adjusted XP reaches, 2024 by the smallest budget its XP" +
            " fits. So with equal numbers, XP between Medium and Hard is 2014 Medium but 2024 Moderate, and anything up to the Low budget is 2024 Low even where" +
            " 2014 calls it Trivial.\n" +
            "- 2014's multiplier is × 1 here, so each edition judges its own plain XP total.\n" +
            "- The monsters' XP differs between the editions (5,900 in 2014, 7,200 in 2024): Aboleth is worth 5,900 XP in 2014 and 7,200 (in lair) in 2024.\n\n" +
            "**Notes:**\n" +
            "- Party: the one-piece campaign's 3 current members (Björn Mountainfell 8, The amethyst Dragon Slayer 8, The fishman monk 8).\n" +
            "- Aboleth (`2014/monster/aboleth`): its stat block gives no in-lair XP, so lair changes nothing; its normal XP is used.\n" +
            "- 2024 classifies a fight as the lowest difficulty whose budget its XP fits, as the SRD's worked examples do (the SRD describes only building to a" +
            " budget).\n\n" +
            "*Sources: 2014 thresholds, multipliers and adventuring-day XP: Dungeon Master's Guide (2014), pp. 82–84, also in the free 2014 Basic Rules (not SRD" +
            " 5.1); 2024 budget and troubleshooting: SRD 5.2.1, Gameplay Toolbox › Combat Encounter Difficulty; CR and XP: the SRD stat blocks, or the XP by CR" +
            " table for a monster given by CR. The tables: rules_get ref \"rules://tables\".*\n",
            play.DifficultyBookOnly);
    }

    /// <summary>
    /// §4.6's not-in-lair variant (FIX: DERIVED from the same rows): the Aboleth's 5,900 XP is over Moderate (5,100) and
    /// within High (6,300), so 2024 High, and within Moderate (6,000) at the effective level, so Moderate there; 2014 is
    /// unchanged (it never had in-lair XP).
    /// </summary>
    [Fact]
    public void OnePiece_46_OutsideItsLair_High2024_ModerateAtEffectiveLevel()
    {
        var text = play.DifficultyNoLair;

        Assert.Equal(
            "## 2024 rules: High (Moderate at effective level +1)\n\n" +
            "| Party | Low | Moderate | High |\n" +
            "|---|---|---|---|\n" +
            "| 3 × level 8 | 3,000 | 5,100 | 6,300 |\n" +
            "| Effective level (+1) | 3,900 | 6,000 | 7,800 |\n\n" +
            "- Monster XP **5,900** is over the Moderate budget (5,100) and fits High (6,300).\n" +
            "- At effective level +1 it is over the Low budget (3,900) and fits Moderate (6,000).\n" +
            "- No multiplier for groups in 2024. The party earns 5,900 XP (about 1,966 each).\n\n",
            text[text.IndexOf("## 2024 rules", StringComparison.Ordinal)..text.IndexOf("**Troubleshooting", StringComparison.Ordinal)]);
        Assert.Equal(
            play.Difficulty[play.Difficulty.IndexOf("## 2014 rules", StringComparison.Ordinal)..play.Difficulty.IndexOf("## 2024 rules", StringComparison.Ordinal)],
            text[text.IndexOf("## 2014 rules", StringComparison.Ordinal)..text.IndexOf("## 2024 rules", StringComparison.Ordinal)]);
        Assert.Contains("| Aboleth (`2014/monster/aboleth`, `2024/monster/aboleth`) | 1 | CR 10, 5,900 XP | CR 10, 5,900 XP |\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnePiece_47_ThePlannedFightsEncounterForm_IsTheExplicitCallsReport_ThePartyFromSheets()
    {
        var at = play.PlannedSimulation.IndexOf(Report, StringComparison.Ordinal);

        Assert.Equal(
            "# Encounter \"The dark station without its lair (planned)\" as a simulation\n\n" +
            "one-piece · planned · 2024 rules · as a fresh fight from its combatants. The report below is the one an explicit call with the same entries " +
            "and seed gives; these notes are this encounter's own.\n\n" +
            "- The party is the campaign's current party (Björn Mountainfell, The amethyst Dragon Slayer, The fishman monk): the encounter has no party " +
            "combatants yet.\n\n",
            play.PlannedSimulation[..at]);
        Assert.Equal(play.SimulationExplicit, play.PlannedSimulation[at..]);

        // As measured on D7's rules (no level offset in the archetype: FIX §4.7's 92.55% was at level 9).
        var text = play.SimulationExplicit;
        Assert.StartsWith("# Fight simulation: Björn Mountainfell, The amethyst Dragon Slayer, The fishman monk vs Aboleth\n\n" +
                          "**The party wins 85.4%** of 2,000 fights (95% CI 83.78–86.88%).\n" +
                          "Party defeated (every member at 0 HP) 14.6% (13.12–16.22%) · draw at round 20 0% (0–0.19%) · a party member dies 1.5% (1.05–2.13%) · " +
                          "a party member is left dying 47.65% (45.47–49.84%).\n", text, StringComparison.Ordinal);
        Assert.Contains("Rounds: mean 3.55 (95% CI 3.52–3.59), median 3, 90th percentile 5.\n", text, StringComparison.Ordinal);
        Assert.Contains("| Björn Mountainfell | party | archetype barbarian (level 8, 2024) | 15 | 85.0 |", text, StringComparison.Ordinal);
        Assert.Contains("| The amethyst Dragon Slayer | party | build \"Amethyst Dragon Slayer L8 (fixture)\" (level 8, 2024) | 16 | 68.0 |", text, StringComparison.Ordinal);
        Assert.Contains("| The fishman monk | party | archetype monk (level 8, 2024) | 16 | 59.0 |", text, StringComparison.Ordinal);
        Assert.Contains("- No lair actions: the fight is not in a lair", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnePiece_47_TheLairFightsEncounterForm_InLairCounts_AsMeasured()
    {
        var at = play.LairSimulation.IndexOf(Report, StringComparison.Ordinal);

        Assert.StartsWith(
            "# Encounter \"The dark station (fixture)\" as a simulation\n\none-piece · active · 2024 rules · in a lair · as a fresh fight from its combatants. ",
            play.LairSimulation, StringComparison.Ordinal);
        Assert.EndsWith("\n\n- Fought in a lair: in-lair legendary counts are simulated; lair actions are not.\n\n", play.LairSimulation[..at], StringComparison.Ordinal);
        var report = play.LairSimulation[at..];
        Assert.StartsWith("# Fight simulation: Björn Mountainfell, The amethyst Dragon Slayer, The fishman monk vs Aboleth\n\n" +
                          "**The party wins 85.4%** of 2,000 fights (95% CI 83.78–86.88%).\n" +
                          "Party defeated (every member at 0 HP) 14.6% (13.12–16.22%) · draw at round 20 0% (0–0.19%) · a party member dies 1.5% (1.05–2.13%) · " +
                          "a party member is left dying 47.7% (45.52–49.89%).\n", report, StringComparison.Ordinal);
        Assert.Contains("- In a lair: legendary action and Legendary Resistance counts are the in-lair ones where the stat block has them; lair actions themselves " +
                        "are never simulated.\n", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lair fight's encounter form says it has no explicit form, and pins that sentence exactly (contract §6.11: "A lair
    /// encounter is pinned in the encounter form only"): balance_simulate takes no lair, so the explicit call with the same
    /// entries and seed fights outside it and reports otherwise (the planned copy's report). Found by X2 at stage 5, where
    /// the notes claimed "The report below is the one an explicit call with the same entries and seed gives"; fixed in H2's
    /// SimulationMarkdown.EncounterNotes (X2's OUTSIDE-OWNERSHIP.patch, the orchestrator's ruling).
    /// </summary>
    [Fact]
    public void OnePiece_47_TheLairFightsEncounterForm_SaysItHasNoExplicitForm_AndTheExplicitCallReportsOtherwise()
    {
        var at = play.LairSimulation.IndexOf(Report, StringComparison.Ordinal);

        Assert.Equal(
            "# Encounter \"The dark station (fixture)\" as a simulation\n\n" +
            "one-piece · active · 2024 rules · in a lair · as a fresh fight from its combatants. No explicit call can put a fight in a lair, so this report has " +
            "no explicit form; the same call with the same seed repeats it. These notes are this encounter's own.\n\n" +
            "- Fought in a lair: in-lair legendary counts are simulated; lair actions are not.\n\n",
            play.LairSimulation[..at]);
        Assert.NotEqual(play.SimulationExplicit, play.LairSimulation[at..]);
    }

    [Fact]
    public void OnePiece_48_FromState_ResumesRoundThreeFromTheLiveState_TheSameCallRepeatsIt_NoExplicitFormClaimed()
    {
        var (first, again) = play.FromState;

        Assert.Equal(first, again);
        Assert.Equal(
            "# Encounter \"The dark station (fixture)\" as a simulation\n\n" +
            "one-piece · active · 2024 rules · in a lair · resumed from its live state (HP, conditions, concentration, uses left, the turn). No explicit call " +
            "can resume a fight, so this report has no explicit form; the same call with the same seed repeats it. These notes are this encounter's own.\n\n" +
            "- The fishman monk: cursed (Mucus Cloud), Focus are not simulated.\n\n",
            first[..first.IndexOf(Report, StringComparison.Ordinal)]);

        // FIX §4.8: Björn 51, the monk 7, the Dragon Slayer 56, the Aboleth 13 of 150; the turn on the monk in round 3.
        Assert.Contains("| Björn Mountainfell | party | archetype barbarian (level 8, 2024) | 15 | 51.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| The amethyst Dragon Slayer | party | build \"Amethyst Dragon Slayer L8 (fixture)\" (level 8, 2024) | 16 | 56.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| The fishman monk | party | archetype monk (level 8, 2024) | 16 | 7.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| Aboleth | enemy | monster 2024/monster/aboleth | 17 | 13.0 |", first, StringComparison.Ordinal);
        Assert.Contains("- Resumed from a live fight in round 3 at The fishman monk's turn, in its order:", first, StringComparison.Ordinal);
        Assert.Contains("**The party wins 99.65%** of 2,000 fights (95% CI 99.28–99.83%).\n", first, StringComparison.Ordinal);
        Assert.Contains("Rounds: mean 1.04 (95% CI 1.03–1.05), median 1, 90th percentile 1.\n", first, StringComparison.Ordinal);
    }

    // A PC's "### Sheet" block of an author campaign_get (up to its "### Author" section).
    private static string SheetBlock(string text, string name)
    {
        var start = text.IndexOf("## " + name + " (", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {name} in:\n{text}");
        var sheet = text.IndexOf("### Sheet\n", start, StringComparison.Ordinal);
        return text[sheet..text.IndexOf("\n### Author", sheet, StringComparison.Ordinal)];
    }

    private static int[] Faces(string detail) =>
        [.. JsonNode.Parse(detail)!["groups"]!.AsArray().SelectMany(g => g!["dice"]?.AsArray() ?? []).SelectMany(d => d!["faces"]!.AsArray())
            .Select(f => f!["face"]!.GetValue<int>())];
}
