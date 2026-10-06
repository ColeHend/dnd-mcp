using System.Text.Json.Nodes;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The exit criterion through the MCP tools, fixture A (FIX §2 as amended by contract §16): "a full scripted combat
/// round-trips to the sheet correctly". Belmakor's 2014 player campaign, its sheets written by <c>campaign_character</c>,
/// fights the crypt with <c>combat</c> calls of §16's shapes, and every numbered step pins what the MODEL reads: the
/// heading, every "what changed" line with its arithmetic, every reminder with the call that resolves it (the campaign
/// named last), and the table rows FIX names. A32: no step writes change_log, touches a sheet row or rolls a die (the
/// server's roller holds no face, so a roll would fail the play). A33 / §2.3: <c>end</c> is one batch filed under session 4
/// that writes exactly the "After" column, and <c>campaign_get include:["sheet"]</c> shows it. §2.4-§2.6: the difficulty, the
/// encounter-form simulation (equal to the explicit call after its notes) and <c>from_state</c> (deterministic) through the
/// tools.
/// What breaks if these fail: the model cannot run the fight FIX scripts, or the sheet it reads afterwards is wrong.
/// </summary>
[Collection(ScenarioCombatBelmakorCollection.Name)]
public sealed class ScenarioCombatBelmakorTests(ScenarioCombatBelmakorPlay play)
{
    private const string Crypt = "# The crypt (fixture) — ";
    private const string Subtitle = " · belmakor · 2014 rules · session 4\n\n";

    /// <summary>A legendary-available reminder (§6.3) as the step prints it, its call naming the campaign last.</summary>
    private static string Legendary(string holder, int left) =>
        $"when {holder}'s turn ends, Mummy Lord may take a legendary action ({left}/3), before next combat " +
        "{\"action\": \"legendary\", \"source\": \"mummy-lord\", \"amount\": 1, \"name\": …, \"campaign\": \"belmakor\"}";

    /// <summary>The legendary step's damage call (fix F2, review LR03): the roll is the Mummy Lord's, not the turn-holder's.</summary>
    private static string LegendaryRoll(string holder, string action) =>
        $"Mummy Lord acts outside its turn (legendary action {action}): give its damage \"source\", or the roll is {holder}'s: combat " +
        "{\"action\": \"damage\", \"targets\": […], \"dice\": …, \"source\": \"mummy-lord\", \"campaign\": \"belmakor\"}";

    private static string Dying(int failures) => $"Lieutenant James Torch: 0 HP, 0 successes, {failures} failures";

    /// <summary>The 2014 death-save procedure the drop's reminder carries (fix F1 U05).</summary>
    private const string DeathSaves =
        "Death saving throws at the start of each of its turns: 10 or more succeeds (with Disadvantage from exhaustion 3); 3 successes, stable; " +
        "3 failures, dead; a 20 regains 1 hit point; a 1 counts as two failures; damage at 0 HP is a failure, a critical hit two (and damage of " +
        "at least its hit point maximum kills).";

    private const string Frightened =
        "Lieutenant James Torch is frightened (Mummy): disadvantage on attack rolls and ability checks while its source is in sight; can't willingly move closer to it";

    private const string Prone =
        "Lieutenant James Torch is prone: its attack rolls have disadvantage; attack rolls against it have advantage within 5 ft, disadvantage beyond; standing costs half its speed";

    private static string NoHp(string name, string handle) =>
        $"{name} has no hit points: give hp to track them combat {{\"action\": \"set\", \"combatants\": [{{\"character\": \"character:{handle}\", \"hp\": …}}], \"campaign\": \"belmakor\"}}";

    /// <summary>
    /// Every step's heading (after the fight's name), action, "what changed" lines and reminders, exactly as FIX §2.2 (as
    /// amended by §16) has them, checked against the rules: A8 35 with 7 absorbed is DC 17; A14 fire ×2; A18 19 − 20 drops
    /// Torch without massive damage; A21 a monster dies at 0; A22 the reset with what it had; A23 a critical at 0 HP is two
    /// failures, said in ONE dying line (the step's own, never also the turn's); A24 the death save due with its call; A25 a
    /// natural 20; A27/A28 the frightened that ends with Mummy's turn; A28a prev restores it; A29 Rejuvenation at the defeat;
    /// A31 all enemies down.
    /// </summary>
    public static TheoryData<string, string, string, string[]?, string[]> Steps() => new()
    {
        { "A1", "round 0 (roll initiative to begin round 1)", "start",
            [
                "Started \"The crypt (fixture)\" (2014): round 0; roll initiative to begin round 1.", "Not added: Tristan (dead).",
                "Aiden Ironstar (party): HP not tracked, initiative +0, from its sheet.",
                "Belmakor Silverwind (party): 110/110 HP (+7 temp), AC 17, initiative +5, from its sheet.",
                "Ignis (party): HP not tracked, initiative +0, from its sheet.", "Lieutenant James Torch (party): 74/74 HP, initiative +0, from its sheet.",
                "Serif (party): HP not tracked, initiative +0, from its sheet.", "Vars Nocturne (party): HP not tracked, initiative +0, from its sheet.",
            ],
            [NoHp("Aiden Ironstar", "aiden-ironstar"), NoHp("Ignis", "ignis"), NoHp("Serif", "serif"), NoHp("Vars Nocturne", "vars")] },
        { "A2", "round 0 (roll initiative to begin round 1)", "add",
            [
                "Mummy Lord (enemy): 97/97 HP, AC 17, initiative +0, legendary actions 3/3.", "Mummy (enemy): 58/58 HP, AC 11, initiative −1.",
                "Mummy 2 (enemy): 58/58 HP, AC 11, initiative −1.",
            ],
            [] },
        { "A3", "round 1 · Vars Nocturne's turn", "initiative",
            [
                "Belmakor Silverwind: initiative 22 (17 + 5).", "Mummy Lord: initiative 18 (18 + 0).", "Mummy: initiative 9 (10 − 1).",
                "Mummy 2: initiative 9 (10 − 1).", "Vars Nocturne: initiative 25 (given).", "Serif: initiative 16 (given).", "Ignis: initiative 14 (given).",
                "Lieutenant James Torch: initiative 12 (given).", "Aiden Ironstar: initiative 7 (given).",
            ],
            ["Round 1", Legendary("Vars Nocturne", 3)] },
        { "A4", "round 1 · Belmakor Silverwind's turn", "next", ["Belmakor Silverwind's turn (round 1)."], [Legendary("Belmakor Silverwind", 3)] },
        { "A5", "round 1 · Belmakor Silverwind's turn", "condition",
            [
                "Belmakor Silverwind: Bladesong 1 used, 3/4 left.", "Bladesong is not an SRD condition: tracked as an effect.",
                "Belmakor Silverwind: Bladesong (Belmakor Silverwind), until the start of Belmakor Silverwind's turn in round 11.",
            ],
            [Legendary("Belmakor Silverwind", 3)] },
        { "A6", "round 1 · Belmakor Silverwind's turn", "concentration",
            [
                "Belmakor Silverwind: 5th-level slot 1 used, 1/2 left.",
                "Belmakor Silverwind concentrates on Circle of Power for 100 rounds (until the start of Belmakor Silverwind's turn in round 101).",
            ],
            [Legendary("Belmakor Silverwind", 3)] },
        { "A7", "round 1 · Mummy Lord's turn", "next", ["Mummy Lord's turn (round 1)."], ["Mummy Lord: legendary actions reset: 3/3"] },
        { "A8", "round 1 · Mummy Lord's turn", "damage", ["Belmakor Silverwind: 14 bludgeoning + 21 necrotic = 35; temporary HP 7 → 0; 110 → 82"],
            [
                "Belmakor Silverwind: concentration save DC 17 to keep Circle of Power (Con save +7) " +
                "combat {\"action\": \"concentration\", \"targets\": [\"belmakor\"], \"total\": …, \"campaign\": \"belmakor\"}",
            ] },
        { "A9", "round 1 · Mummy Lord's turn", "concentration", ["Belmakor Silverwind keeps concentrating on Circle of Power (20 against DC 17)."], [] },
        { "A10", "round 1 · Serif's turn", "next", ["Serif's turn (round 1)."], [Legendary("Serif", 3)] },
        { "A11", "round 1 · Ignis's turn", "next", ["Ignis's turn (round 1)."], [Legendary("Ignis", 3)] },
        { "A12-legendary", "round 1 · Ignis's turn", "legendary", ["Mummy Lord: legendary action Attack (Rotting Fist) (1), 2/3 left."],
            [LegendaryRoll("Ignis", "Attack (Rotting Fist)"), Legendary("Ignis", 2)] },
        { "A12", "round 1 · Ignis's turn", "damage", ["Lieutenant James Torch: 14 bludgeoning + 21 necrotic = 35; 74 → 39"], [Legendary("Ignis", 2)] },
        { "A13", "round 1 · Lieutenant James Torch's turn", "next", ["Lieutenant James Torch's turn (round 1)."], [Legendary("Lieutenant James Torch", 2)] },
        { "A14", "round 1 · Lieutenant James Torch's turn", "damage",
            ["Mummy Lord: 28 fire ×2 (vulnerable) = 56; 97 → 41", "Mummy: 28 fire ×2 (vulnerable) = 56; 58 → 2", "Mummy 2: 28 fire ×2 (vulnerable) = 56; 58 → 2"],
            [Legendary("Lieutenant James Torch", 2)] },
        { "A15", "round 1 · Mummy's turn", "next", ["Mummy's turn (round 1)."], [Legendary("Mummy", 2)] },
        { "A16", "round 1 · Mummy's turn", "condition", ["Lieutenant James Torch: frightened (Mummy), until the end of Mummy's next turn."], [Legendary("Mummy", 2)] },
        { "A17", "round 1 · Mummy's turn", "damage", ["Lieutenant James Torch: 10 bludgeoning + 10 necrotic = 20; 39 → 19"], [Legendary("Mummy", 2)] },
        { "A18-next", "round 1 · Mummy 2's turn", "next", ["Mummy 2's turn (round 1)."], [Legendary("Mummy 2", 2)] },
        { "A18", "round 1 · Mummy 2's turn", "damage", ["Lieutenant James Torch: 10 bludgeoning + 10 necrotic = 20; 19 → 0"],
            ["Lieutenant James Torch drops to 0 HP: unconscious, dying (0 successes, 0 failures). " + DeathSaves, Dying(0), Legendary("Mummy 2", 2)] },
        { "A19-aiden", "round 1 · Aiden Ironstar's turn", "next", ["Aiden Ironstar's turn (round 1)."], [Dying(0), Legendary("Aiden Ironstar", 2)] },
        { "A19", "round 2 · Vars Nocturne's turn", "next", ["Vars Nocturne's turn (round 2)."], ["Round 2", Dying(0), Legendary("Vars Nocturne", 2)] },
        { "A20", "round 2 · Belmakor Silverwind's turn", "next", ["Belmakor Silverwind's turn (round 2)."], [Dying(0), Legendary("Belmakor Silverwind", 2)] },
        { "A21", "round 2 · Belmakor Silverwind's turn", "damage", ["Mummy 2: 8 slashing; 2 → 0"],
            ["Mummy 2 dies (dropped to 0 hit points) and is defeated.", Dying(0), Legendary("Belmakor Silverwind", 2)] },
        { "A22", "round 2 · Mummy Lord's turn", "next", ["Mummy Lord's turn (round 2)."], ["Mummy Lord: legendary actions reset: 3/3 (it had 2/3 left)", Dying(0)] },
        { "A23", "round 2 · Mummy Lord's turn", "damage", ["Lieutenant James Torch: 25 bludgeoning + 42 necrotic = 67; 0 → 0"],
            ["Lieutenant James Torch is unconscious: a hit from within 5 ft is a critical hit", Dying(2) + " (a critical hit: two death save failures)"] },
        { "A24-serif", "round 2 · Serif's turn", "next", ["Serif's turn (round 2)."], [Dying(2), Legendary("Serif", 3)] },
        { "A24-ignis", "round 2 · Ignis's turn", "next", ["Ignis's turn (round 2)."], [Dying(2), Legendary("Ignis", 3)] },
        { "A24", "round 2 · Lieutenant James Torch's turn", "next", ["Lieutenant James Torch's turn (round 2)."],
            [
                "Lieutenant James Torch: death saving throw due (0 successes, 2 failures) " +
                "combat {\"action\": \"death_save\", \"targets\": [\"torch\"], \"face\": …, \"campaign\": \"belmakor\"}",
                Frightened,
                "Lieutenant James Torch is unconscious: incapacitated, can't move or speak, unaware; falls prone; automatically fails Strength and Dexterity " +
                "saving throws; attack rolls against it have advantage; a hit from within 5 ft is a critical hit",
                Prone, Legendary("Lieutenant James Torch", 3),
            ] },
        // A25's "What changed" (fix F1 U06): a death save that ends dying says what it changed, as every step does.
        { "A25", "round 2 · Lieutenant James Torch's turn", "death_save",
            ["Lieutenant James Torch: death save succeeds (a natural 20): hp 0 → 1; death saves 0 successes, 2 failures → 0 successes, 0 failures."],
            [
                "Lieutenant James Torch rolls a natural 20: regains 1 HP and is conscious (still prone); death saves reset", Frightened, Prone,
                Legendary("Lieutenant James Torch", 3),
            ] },
        { "A26", "round 2 · Lieutenant James Torch's turn", "damage", ["Mummy Lord: 16 fire ×2 (vulnerable) = 32; 41 → 9"],
            [Frightened, Prone, Legendary("Lieutenant James Torch", 3)] },
        { "A27", "round 2 · Mummy's turn", "next", ["Mummy's turn (round 2)."],
            ["frightened (Mummy) on Lieutenant James Torch ends at the end of this turn", Legendary("Mummy", 3)] },
        { "A28", "round 2 · Aiden Ironstar's turn", "next", ["Aiden Ironstar's turn (round 2)."],
            ["frightened (Mummy) ended on Lieutenant James Torch", Legendary("Aiden Ironstar", 3)] },
        { "A28a-prev", "round 2 · Mummy's turn", "prev",
            ["Back to Mummy's turn (round 2); its automatic changes were undone: frightened (Mummy) ended on Lieutenant James Torch."],
            ["frightened (Mummy) on Lieutenant James Torch ends at the end of this turn", Legendary("Mummy", 3)] },
        { "A28a-next", "round 2 · Aiden Ironstar's turn", "next", ["Aiden Ironstar's turn (round 2)."],
            ["frightened (Mummy) ended on Lieutenant James Torch", Legendary("Aiden Ironstar", 3)] },
        { "A29", "round 2 · Aiden Ironstar's turn", "damage", ["Mummy Lord: 12 radiant; 9 → 0"],
            [
                "Mummy Lord dies (dropped to 0 hit points) and is defeated.",
                "Mummy Lord — Rejuvenation: A destroyed mummy lord gains a new body in 24 hours if its heart is intact, regaining all its hit points and " +
                "becoming active again. The new body appears within 5 feet of the mummy lord's heart.",
            ] },
        { "A30", "round 3 · Vars Nocturne's turn", "next", ["Vars Nocturne's turn (round 3)."], ["Round 3"] },
        { "A31", "round 3 · Vars Nocturne's turn", "damage", ["Mummy: 10 piercing; 2 → 0"],
            [
                "Mummy dies (dropped to 0 hit points) and is defeated.",
                "all enemies are defeated: end the combat? combat {\"action\": \"end\", \"outcome\": …, \"campaign\": \"belmakor\"}",
            ] },
    };

    [Fact]
    public void Belmakor_TheScript_IsEveryStepOfFixA_InOrder()
    {
        Assert.Equal(Steps().Select(row => (string)row[0]), play.Steps.Select(s => s.Step));
    }

    [Theory]
    [MemberData(nameof(Steps))]
    public void Belmakor_EachStep_SaysWhatFixSays_HeadingChangesAndRemindersWithTheirCalls(string step, string heading, string action, string[]? changed, string[] reminders)
    {
        var text = play[step].Text;

        Assert.StartsWith(Crypt + heading + "\n\ncombat " + action + Subtitle, text, StringComparison.Ordinal);
        if (changed is not null)
        {
            Assert.Equal(changed, ScenarioCombatText.Section(text, "What changed"));
        }

        Assert.Equal(reminders, ScenarioCombatText.Section(text, "Reminders"));
        Assert.Empty(ScenarioCombatText.Section(text, "Rolls"));
        Assert.Empty(ScenarioCombatText.Section(text, "Warnings"));
    }

    [Fact]
    public void Belmakor_A0_Session4StartsLive_TheSixLivingPcsAttend_ItsOwnBatch()
    {
        Assert.StartsWith("# Session 4 started (belmakor)\n\nBatch `", play.SessionStart, StringComparison.Ordinal);
        Assert.Contains(
            "Changed: session, attendance (added character:belmakor, character:vars, character:ignis, character:serif, character:torch, character:aiden-ironstar).",
            play.SessionStart, StringComparison.Ordinal);
        Assert.Equal(play.N0, play.Steps[0].ChangeRows);
    }

    [Fact]
    public void Belmakor_A1_A3_TheTableFromTheSheets_ThenTheOrder25_22_18_16_14_12_9_9_7()
    {
        var a1 = ScenarioCombatText.Table(play["A1"].Text);
        Assert.Equal(["Aiden Ironstar", "Belmakor Silverwind", "Ignis", "Lieutenant James Torch", "Serif", "Vars Nocturne"], a1.Select(r => r.Name));
        Assert.Equal(["unknown", "110/110 (+7 temp)", "unknown", "74/74", "unknown", "unknown"], a1.Select(r => r.Hp));
        Assert.Contains("| Belmakor Silverwind (character:belmakor) | party | 110/110 (+7 temp) | 17 | — |", play["A1"].Text, StringComparison.Ordinal);

        Assert.Contains(
            "| ▶ | 1 | 25 | Vars Nocturne (character:vars) | party | unknown | — | — |\n" +
            "|  | 2 | 22 | Belmakor Silverwind (character:belmakor) | party | 110/110 (+7 temp) | 17 | — |\n" +
            "|  | 3 | 18 | Mummy Lord | enemy | 97/97 | 17 | legendary actions 3/3 |\n" +
            "|  | 4 | 16 | Serif (character:serif) | party | unknown | — | — |\n" +
            "|  | 5 | 14 | Ignis (character:ignis) | party | unknown | — | — |\n" +
            "|  | 6 | 12 | Lieutenant James Torch (character:torch) | party | 74/74 | — | — |\n" +
            "|  | 7 | 9 | Mummy | enemy | 58/58 | 11 | — |\n" +
            "|  | 8 | 9 | Mummy 2 | enemy | 58/58 | 11 | — |\n" +
            "|  | 9 | 7 | Aiden Ironstar (character:aiden-ironstar) | party | unknown | — | — |\n",
            play["A3"].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("A5", "| ▶ | 2 | 22 | Belmakor Silverwind (character:belmakor) | party | 110/110 (+7 temp) | 22 | Bladesong (Belmakor Silverwind) |")]
    [InlineData("A6", "| ▶ | 2 | 22 | Belmakor Silverwind (character:belmakor) | party | 110/110 (+7 temp) | 22 | Bladesong (Belmakor Silverwind); concentrating on Circle of Power (level 5) |")]
    [InlineData("A8", "|  | 2 | 22 | Belmakor Silverwind (character:belmakor) | party | 82/110 | 22 | Bladesong (Belmakor Silverwind); concentrating on Circle of Power (level 5) |")]
    [InlineData("A12-legendary", "|  | 3 | 18 | Mummy Lord | enemy | 97/97 | 17 | legendary actions 2/3 |")]
    [InlineData("A14", "|  | 3 | 18 | Mummy Lord | enemy | 41/97 | 17 | legendary actions 2/3 |")]
    [InlineData("A18", "|  | 6 | 12 | Lieutenant James Torch (character:torch) | party | 0/74 · death saves: 0 successes, 0 failures | — | frightened (Mummy); unconscious; prone |")]
    [InlineData("A21", "|  | 8 | 9 | ~~Mummy 2~~ | enemy | dead | 11 | — |")]
    [InlineData("A23", "|  | 6 | 12 | Lieutenant James Torch (character:torch) | party | 0/74 · death saves: 0 successes, 2 failures | — | frightened (Mummy); unconscious; prone |")]
    [InlineData("A25", "| ▶ | 6 | 12 | Lieutenant James Torch (character:torch) | party | 1/74 | — | frightened (Mummy); prone |")]
    [InlineData("A28", "|  | 6 | 12 | Lieutenant James Torch (character:torch) | party | 1/74 | — | prone |")]
    [InlineData("A28a-prev", "|  | 6 | 12 | Lieutenant James Torch (character:torch) | party | 1/74 | — | frightened (Mummy); prone |")]
    [InlineData("A31", "|  | 7 | 9 | ~~Mummy~~ | enemy | dead | 11 | — |")]
    public void Belmakor_TheTableRowsFixNames_AtTheirSteps(string step, string row)
    {
        Assert.Contains(row + "\n", play[step].Text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // A32: the invariants of the whole fight

    [Fact]
    public void Belmakor_A32_NoStepWritesChangeLog_TouchesASheetRow_OrRollsADie()
    {
        Assert.All(play.Steps, s => Assert.Equal(play.N0, s.ChangeRows));
        Assert.All(play.Steps, s => Assert.Equal(play.SheetTablesBefore, s.SheetTables));
        Assert.All(play.Steps, s => Assert.Equal((0, 0), (s.DiceRows, s.Rolled)));
        Assert.Empty(play.World.Dice.Rolled);
    }

    [Fact]
    public void Belmakor_A32_TheCombatLogHoldsEveryChange_GivenValuesWithNoRollIds_AndTheEnd()
    {
        var log = play.World.Store.CombatLog(play.EncounterId);

        Assert.All(log, r => Assert.Null(r.RollId));
        foreach (var kind in new[] { "start", "add", "initiative", "turn", "damage", "condition", "concentration", "death_save", "legendary", "resource", "defeat", "end" })
        {
            Assert.Contains(log, r => r.Kind == kind);
        }

        var damage = log.Where(r => r.Kind == "damage").ToList();
        Assert.Equal(12, damage.Count);
        Assert.All(damage, r => Assert.True(JsonNode.Parse(r.Detail!)!["given"]!.GetValue<bool>(), r.Detail));
        Assert.Equal([35L, 35L, 56L, 56L, 56L, 20L, 20L, 8L, 67L, 32L, 12L, 10L], damage.Select(r => r.Amount!.Value));
        Assert.Equal(3, log.Count(r => r.Kind == "defeat"));
        Assert.Equal("end", log[^1].Kind);
        Assert.Equal([0L, 1L, 2L, 3L], log.Select(r => r.Round).Distinct().Order());
    }

    [Fact]
    public void Belmakor_EveryStep_EachOtherDyingCreature_HasExactlyOneDyingLine_TheTurnHolderNone()
    {
        // §6.3 (and the stage-4 merge): one dying line per OTHER combatant at 0 HP that is dying, the step's own line when
        // it said one (A23's critical), never a second from the turn context.
        var dyingSteps = new List<string>();
        foreach (var step in play.Steps)
        {
            var reminders = ScenarioCombatText.Section(step.Text, "Reminders");
            foreach (var row in ScenarioCombatText.Table(step.Text).Where(r => !r.Struck && r.Hp.StartsWith("0/", StringComparison.Ordinal) && r.Hp.Contains("death saves", StringComparison.Ordinal)))
            {
                var lines = reminders.Count(l => l.StartsWith(row.Name + ": 0 HP, ", StringComparison.Ordinal));
                Assert.True(lines == (row.Turn ? 0 : 1), $"{step.Step}: {lines} dying lines for {row.Name}");
                if (!row.Turn)
                {
                    dyingSteps.Add(step.Step);
                }
            }
        }

        Assert.Equal(["A18", "A19-aiden", "A19", "A20", "A21", "A22", "A23", "A24-serif", "A24-ignis"], dyingSteps);
    }

    [Fact]
    public void Belmakor_EveryPrintedCall_NamesTheCampaignLastAndOnce()
    {
        var texts = play.Steps.Select(s => (s.Step, s.Text))
            .Append(("end", play.End)).Append(("end dry run", play.EndDryRun)).Append(("last", play.LastAuthor))
            .Append(("simulation", play.Simulation)).Append(("from_state", play.FromState.First)).Append(("session start", play.SessionStart));

        var calls = 0;
        foreach (var (label, text) in texts)
        {
            foreach (var call in ScenarioCombatText.PrintedCalls(text))
            {
                calls++;
                Assert.True(call.EndsWith(", \"campaign\": \"belmakor\"}", StringComparison.Ordinal), $"{label}: {call}");
                Assert.Single(System.Text.RegularExpressions.Regex.Matches(call, "\"campaign\":"));
            }
        }

        // 45: the dry run prints none of the six campaign_character xp calls the end does (review F2R06).
        Assert.Equal(45, calls);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // A33 and §2.3: the write-back

    [Fact]
    public void Belmakor_A33_EndIsOneBatchUnderSession4_WritingExactlyFixTwoPointThree()
    {
        Assert.Equal(
            "# The crypt (fixture) — ended in round 3\n\ncombat end · belmakor · 2014 rules\n\n" +
            $"Batch `{play.EndBatch}`. Session context: session 4. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{play.EndBatch}\", \"campaign\": \"belmakor\"}}.\n\n" +
            "## Written back\n" +
            "- Belmakor Silverwind (`character:belmakor`) hp: 110 → 82\n" +
            "- Belmakor Silverwind (`character:belmakor`) temp_hp: 7 → 0\n" +
            "- Belmakor Silverwind (`character:belmakor`) spell_slots 5: {\"max\":2,\"used\":0} → {\"max\":2,\"used\":1}\n" +
            "- Belmakor Silverwind (`character:belmakor`) resources bladesong: {\"name\":\"Bladesong\",\"max\":4,\"used\":0,\"recharge\":\"long_rest\"} → " +
            "{\"name\":\"Bladesong\",\"max\":4,\"used\":1,\"recharge\":\"long_rest\"}\n" +
            "- Belmakor Silverwind (`character:belmakor`) concentration: none → {\"spell\":\"Circle of Power\",\"level\":5,\"remaining_rounds\":98," +
            "\"note\":\"from The crypt (fixture), round 1\"}\n" +
            "- Lieutenant James Torch (`character:torch`) hp: 74 → 1\n\n" +
            "## Summary\n" +
            "- XP not awarded (no party sheet tracks XP). By the 2014 rules this fight is worth 14,400 XP, 2,400 each for 6.\n" +
            "- XP not written to Aiden Ironstar: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
            "\"character:aiden-ironstar\", \"amount\": 2400, \"campaign\": \"belmakor\"} (it sets the sheet's XP total and records no award).\n" +
            "- XP not written to Belmakor Silverwind: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
            "\"character:belmakor\", \"amount\": 2400, \"campaign\": \"belmakor\"} (it sets the sheet's XP total and records no award).\n" +
            "- XP not written to Ignis: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
            "\"character:ignis\", \"amount\": 2400, \"campaign\": \"belmakor\"} (it sets the sheet's XP total and records no award).\n" +
            "- XP not written to Lieutenant James Torch: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
            "\"character:torch\", \"amount\": 2400, \"campaign\": \"belmakor\"} (it sets the sheet's XP total and records no award).\n" +
            "- XP not written to Serif: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
            "\"character:serif\", \"amount\": 2400, \"campaign\": \"belmakor\"} (it sets the sheet's XP total and records no award).\n" +
            "- XP not written to Vars Nocturne: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
            "\"character:vars\", \"amount\": 2400, \"campaign\": \"belmakor\"} (it sets the sheet's XP total and records no award).\n" +
            "- Belmakor Silverwind: hp 110 → 82.\n" +
            "- Belmakor Silverwind: temporary HP → 0.\n" +
            "- Belmakor Silverwind: concentration on Circle of Power kept (98 rounds left).\n" +
            "- Belmakor Silverwind: level 5 slots 1/2 left.\n" +
            "- Belmakor Silverwind: Bladesong 3/4 left.\n" +
            "- Lieutenant James Torch: hp 74 → 1.\n" +
            "- Ended with the fight: Bladesong (Belmakor Silverwind); prone (Lieutenant James Torch).\n" +
            "- No hit points tracked (nothing written for them): Aiden Ironstar, Ignis, Serif, Vars Nocturne.\n\n" +
            "## Reminders\n" +
            "- Mummy Lord — Rejuvenation: A destroyed mummy lord gains a new body in 24 hours if its heart is intact, regaining all its hit points and " +
            "becoming active again. The new body appears within 5 feet of the mummy lord's heart.\n",
            play.End);

        // One batch, only sheet rows (no combat table is logged; the 1st-level slot spent before the fight is not rewritten).
        Assert.Equal(play.N0 + play.World.Store.BatchRows(play.EndBatch), play.ChangeRowsAfterEnd);
        Assert.Equal(6, play.World.Store.BatchRows(play.EndBatch));
        Assert.Equal(["character_sheet"], play.World.Store.BatchTables(play.EndBatch));
        Assert.Equal(("ended", play.EndBatch), play.World.Store.Encounter(play.EncounterId));
    }

    [Fact]
    public void Belmakor_A33_TheDryRun_SaysWhatEndWouldWrite_AndWritesNothing()
    {
        Assert.StartsWith(
            "# Dry run: The crypt (fixture) — would end in round 3\n\ncombat end · belmakor · 2014 rules\n\n" +
            "**Dry run: nothing was written and no batch exists.** The fight is still running; this is what ending it would write. " +
            "Send the same call without dry_run to end it. Session context: session 4.\n\n## Would be written back\n",
            play.EndDryRun, StringComparison.Ordinal);
        Assert.Equal(ScenarioCombatText.Section(play.End, "Written back"), ScenarioCombatText.Section(play.EndDryRun, "Would be written back"));

        // The summary is the end's, but for the six sheets with no XP total (review F2R06): the dry run prints no
        // campaign_character xp call (sent before the real end, each started a total the real end then added to again) and
        // says the real end prints them.
        var real = ScenarioCombatText.Section(play.End, "Summary");
        var xp = real.Where(l => l.StartsWith("XP not written to ", StringComparison.Ordinal)).ToList();
        Assert.Equal(6, xp.Count);
        Assert.Equal(
            [
                real[0],
                .. xp.Select(l => l[..l.IndexOf("; start one with: ", StringComparison.Ordinal)] + "."),
                "The real end prints the calls to start XP totals.",
                .. real.Skip(1 + xp.Count),
            ],
            ScenarioCombatText.Section(play.EndDryRun, "Summary"));
        Assert.Equal("XP not written to Aiden Ironstar: the sheet has no XP total.", ScenarioCombatText.Section(play.EndDryRun, "Summary")[1]);
        Assert.DoesNotContain("campaign_character", play.EndDryRun, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", play.EndDryRun, StringComparison.Ordinal);
    }

    /// <summary>
    /// §2.3 through <c>campaign_get include:["sheet"]</c>: Belmakor's and Torch's whole sheet blocks before the fight and
    /// after the end (HP 82 and temp 0, the 5th-level slot, Bladesong 3/4, the concentration persisting with 98 rounds
    /// left and where it came from; Torch at 1 HP with no condition and no death saves left over), and the four HP-less
    /// sheets byte for byte as they were.
    /// </summary>
    [Fact]
    public void Belmakor_23_TheSheetsBeforeAndAfter_ThroughCampaignGet_BelmakorAndTorchWritten_TheRestUntouched()
    {
        string Sheet(string text, string name)
        {
            var start = text.IndexOf("## " + name + " (", StringComparison.Ordinal);
            var sheet = text.IndexOf("### Sheet\n", start, StringComparison.Ordinal);
            return text[sheet..text.IndexOf("\n### Author", sheet, StringComparison.Ordinal)];
        }

        Assert.Equal(
            "### Sheet\n" +
            "level 12 Wizard (Bladesinger), 2014\n" +
            "`character:belmakor` · player Cole · High Elf · background Noble · source fixture\n" +
            "HP 110/110 (+7 temp) · AC 17 · Init +5 · PB +4 · Exhaustion 0\n\n" +
            "- **Spell slots:** 1st 3/4 · 2nd 3/3 · 3rd 3/3 · 4th 3/3 · 5th 2/2 · 6th 1/1\n" +
            "- **Resources:** Bladesong 4/4 (long rest) · Arcane Recovery 1/1 (long rest) · Contingency: set — Polymorph (T-rex) when he drops low\n" +
            "- **Abilities:** Str 11 (+0) · Dex 20 (+5) · Con 16 (+3) · Int 20 (+5) · Wis 13 (+1) · Cha 12 (+1)\n" +
            "- **Saves:** proficient Int, Wis, Con\n" +
            "- **Hit Dice:** d6 12/12 left\n" +
            "- **XP:** none recorded (add a total with campaign_character xp to track it)\n" +
            "- **Other:** Spell save DC 17 · Spell attack +9\n" +
            "- **Feats:** War Caster · Resilient (Constitution) · Fey Touched · Tough\n" +
            "- **Spells:** Circle of Power · Fly · Contingency · Polymorph · Mirror Image · False Life · Shield\n" +
            "- **Languages:** Common · Elvish\n" +
            "- **Coins:** none\n" +
            "- **sim_profile:** Belmakor L12 Bladesinger (fixture), 2014, level 12; attacks Scimitar, Offhand scimitar; modifiers Bladesong\n\n" +
            "**Notes:**\n" +
            "Planned: Hold Monster, Wall of Force; Forcecage or Simulacrum at 13.\n",
            Sheet(play.SheetsBefore, "Belmakor Silverwind"));
        Assert.Equal(
            "### Sheet\n" +
            "level 12 Wizard (Bladesinger), 2014\n" +
            "`character:belmakor` · player Cole · High Elf · background Noble · source fixture\n" +
            "HP 82/110 · AC 17 · Init +5 · PB +4 · Exhaustion 0\n\n" +
            "- **Spell slots:** 1st 3/4 · 2nd 3/3 · 3rd 3/3 · 4th 3/3 · 5th 1/2 · 6th 1/1\n" +
            "- **Resources:** Bladesong 3/4 (long rest) · Arcane Recovery 1/1 (long rest) · Contingency: set — Polymorph (T-rex) when he drops low\n" +
            "- **Concentration:** Circle of Power (5th level; 98 rounds left; from The crypt (fixture), round 1)\n" +
            "- **Abilities:** Str 11 (+0) · Dex 20 (+5) · Con 16 (+3) · Int 20 (+5) · Wis 13 (+1) · Cha 12 (+1)\n" +
            "- **Saves:** proficient Int, Wis, Con\n" +
            "- **Hit Dice:** d6 12/12 left\n" +
            "- **XP:** none recorded (add a total with campaign_character xp to track it)\n" +
            "- **Other:** Spell save DC 17 · Spell attack +9\n" +
            "- **Feats:** War Caster · Resilient (Constitution) · Fey Touched · Tough\n" +
            "- **Spells:** Circle of Power · Fly · Contingency · Polymorph · Mirror Image · False Life · Shield\n" +
            "- **Languages:** Common · Elvish\n" +
            "- **Coins:** none\n" +
            "- **sim_profile:** Belmakor L12 Bladesinger (fixture), 2014, level 12; attacks Scimitar, Offhand scimitar; modifiers Bladesong\n\n" +
            "**Notes:**\n" +
            "Planned: Hold Monster, Wall of Force; Forcecage or Simulacrum at 13.\n",
            Sheet(play.SheetsAfter, "Belmakor Silverwind"));
        Assert.Equal(
            "### Sheet\n" +
            "level 12 Wizard, 2014\n" +
            "`character:torch` · source fixture\n" +
            "HP 74/74 · AC — · Init +0 · PB +4 · Exhaustion 0\n\n" +
            "- **Spell slots:** 1st 4/4 · 2nd 3/3 · 3rd 3/3 · 4th 3/3 · 5th 2/2 · 6th 1/1\n" +
            "- **Saves:** proficient Int, Wis\n" +
            "- **Hit Dice:** d6 12/12 left\n" +
            "- **XP:** none recorded (add a total with campaign_character xp to track it)\n" +
            "- **Coins:** none\n",
            Sheet(play.SheetsBefore, "Lieutenant James Torch"));
        Assert.Equal(
            "### Sheet\n" +
            "level 12 Wizard, 2014\n" +
            "`character:torch` · source fixture\n" +
            "HP 1/74 · AC — · Init +0 · PB +4 · Exhaustion 0\n\n" +
            "- **Spell slots:** 1st 4/4 · 2nd 3/3 · 3rd 3/3 · 4th 3/3 · 5th 2/2 · 6th 1/1\n" +
            "- **Saves:** proficient Int, Wis\n" +
            "- **Hit Dice:** d6 12/12 left\n" +
            "- **XP:** none recorded (add a total with campaign_character xp to track it)\n" +
            "- **Coins:** none\n",
            Sheet(play.SheetsAfter, "Lieutenant James Torch"));
        foreach (var name in new[] { "Aiden Ironstar", "Ignis", "Serif", "Vars Nocturne" })
        {
            Assert.Equal(Sheet(play.SheetsBefore, name), Sheet(play.SheetsAfter, name));
        }
    }

    [Fact]
    public void Belmakor_A33_TheEndedFight_AsTheAuthorReadsItLast_ItsWriteBackAndUndoCall()
    {
        Assert.StartsWith(
            "# The crypt (fixture) — ended in round 3\n\ncombat state · belmakor · 2014 rules · session 4\n\n" +
            $"Its write-back is batch `{play.EndBatch}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{play.EndBatch}\", \"campaign\": \"belmakor\"}}.\n" +
            "Outcome: The crypt is cleared.\n",
            play.LastAuthor, StringComparison.Ordinal);
        Assert.Contains("|  | 3 | 18 | ~~Mummy Lord~~ | enemy | dead | 17 | legendary actions 3/3 |\n", play.LastAuthor, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // §2.4-§2.6 through the tools

    /// <summary>
    /// §2.4 through the tool, whole: the party read from the sheets (six at 12; Tristan left out, dead, with the note), no
    /// offset (no effective-level rows); 2014 Hard (thresholds 6,000 / 12,000 / 18,000 / 27,000; 14,400 × 1.5 = 21,600, the
    /// six-or-more step down; it reaches Hard but not Deadly; the adventuring day about 69,000 = 6 × 11,500, 31%); 2024
    /// Moderate (budgets 13,200 / 22,200 / 28,200); 14,400 XP earned, 2,400 each; the Mummy Lord's troubleshooting line.
    /// With no edition, the campaign's 2014 alone with the default's note.
    /// </summary>
    [Fact]
    public void Belmakor_24_EncounterDifficultyOfTheCampaignsParty_SixAtTwelve_TristanLeftOut_Hard2014_Moderate2024()
    {
        Assert.Equal(
            "# Encounter difficulty\n\n" +
            "**Party:** 6 characters at level 12.\n\n" +
            "| Monster | Count | 2014 | 2024 |\n" +
            "|---|---|---|---|\n" +
            "| Mummy Lord (`2014/monster/mummy-lord`, `2024/monster/mummy-lord`) | 1 | CR 15, 13,000 XP | CR 15, 13,000 XP |\n" +
            "| Mummy (`2014/monster/mummy`, `2024/monster/mummy`) | 2 | CR 3, 700 XP | CR 3, 700 XP |\n" +
            "| **Total** | **3** | **14,400 XP** | **14,400 XP** |\n\n" +
            "## 2014 rules: Hard\n\n" +
            "| Party | Easy | Medium | Hard | Deadly |\n" +
            "|---|---|---|---|---|\n" +
            "| 6 × level 12 | 6,000 | 12,000 | 18,000 | 27,000 |\n\n" +
            "- Monster XP 14,400 × 1.5 = **21,600 adjusted XP**: × 1.5 for 3 monsters (the 3–6 row's × 2, one step down for a party of 6 or more).\n" +
            "- That reaches Hard (18,000) but not Deadly (27,000).\n" +
            "- Adventuring day: this party can handle about 69,000 adjusted XP before a long rest, so this fight is 31% of a day.\n" +
            "- The party earns 14,400 XP (2,400 each); the multiplier only judges difficulty.\n" +
            "- The DMG says not to count monsters whose CR is significantly below the others' average toward the multiplier; mark such a monster exclude: true to" +
            " apply it.\n\n" +
            "## 2024 rules: Moderate\n\n" +
            "| Party | Low | Moderate | High |\n" +
            "|---|---|---|---|\n" +
            "| 6 × level 12 | 13,200 | 22,200 | 28,200 |\n\n" +
            "- Monster XP **14,400** is over the Low budget (13,200) and fits Moderate (22,200).\n" +
            "- No multiplier for groups in 2024. The party earns 14,400 XP (2,400 each).\n\n" +
            "**Troubleshooting (SRD 5.2.1):**\n" +
            "- Mummy Lord is CR 15, above the party's level (12): it might take out one or more characters with a single action. Also check it has no feature" +
            " those characters can't easily overcome.\n\n" +
            "## The editions compared\n\n" +
            "- The 2024 budgets are not the 2014 thresholds renamed: Low equals Medium only up to level 7, Moderate equals Hard up to level 5 and High equals" +
            " Deadly up to level 8. For this party: Low 13,200 vs Medium 12,000; Moderate 22,200 vs Hard 18,000; High 28,200 vs Deadly 27,000.\n" +
            "- The labels are read in opposite directions: 2014 names a fight by the highest threshold its adjusted XP reaches, 2024 by the smallest budget its XP" +
            " fits. So with equal numbers, XP between Medium and Hard is 2014 Medium but 2024 Moderate, and anything up to the Low budget is 2024 Low even where" +
            " 2014 calls it Trivial.\n" +
            "- 2014 judges 21,600 adjusted XP (× 1.5); 2024 judges the plain 14,400.\n\n" +
            "**Notes:**\n" +
            "- Party: the belmakor campaign's 6 current members (Aiden Ironstar 12, Belmakor Silverwind 12, Ignis 12, Lieutenant James Torch 12, Serif 12, Vars" +
            " Nocturne 12).\n" +
            "- Left out: Tristan (dead); a dead or departed member is not in the party.\n" +
            "- 2024 classifies a fight as the lowest difficulty whose budget its XP fits, as the SRD's worked examples do (the SRD describes only building to a" +
            " budget).\n\n" +
            "*Sources: 2014 thresholds, multipliers and adventuring-day XP: Dungeon Master's Guide (2014), pp. 82–84, also in the free 2014 Basic Rules (not SRD" +
            " 5.1); 2024 budget and troubleshooting: SRD 5.2.1, Gameplay Toolbox › Combat Encounter Difficulty; CR and XP: the SRD stat blocks, or the XP by CR" +
            " table for a monster given by CR. The tables: rules_get ref \"rules://tables\".*\n",
            play.Difficulty);
        Assert.Equal(
            "# Encounter difficulty\n\n" +
            "**Party:** 6 characters at level 12.\n\n" +
            "| Monster | CR | XP each | Count | XP |\n" +
            "|---|---|---|---|---|\n" +
            "| Mummy Lord (`2014/monster/mummy-lord`) | 15 | 13,000 | 1 | 13,000 |\n" +
            "| Mummy (`2014/monster/mummy`) | 3 | 700 | 2 | 1,400 |\n" +
            "| **Total** | — | — | **3** | **14,400** |\n\n" +
            "## 2014 rules: Hard\n\n" +
            "| Party | Easy | Medium | Hard | Deadly |\n" +
            "|---|---|---|---|---|\n" +
            "| 6 × level 12 | 6,000 | 12,000 | 18,000 | 27,000 |\n\n" +
            "- Monster XP 14,400 × 1.5 = **21,600 adjusted XP**: × 1.5 for 3 monsters (the 3–6 row's × 2, one step down for a party of 6 or more).\n" +
            "- That reaches Hard (18,000) but not Deadly (27,000).\n" +
            "- Adventuring day: this party can handle about 69,000 adjusted XP before a long rest, so this fight is 31% of a day.\n" +
            "- The party earns 14,400 XP (2,400 each); the multiplier only judges difficulty.\n" +
            "- The DMG says not to count monsters whose CR is significantly below the others' average toward the multiplier; mark such a monster exclude: true to" +
            " apply it.\n\n" +
            "**Notes:**\n" +
            "- Party: the belmakor campaign's 6 current members (Aiden Ironstar 12, Belmakor Silverwind 12, Ignis 12, Lieutenant James Torch 12, Serif 12, Vars" +
            " Nocturne 12).\n" +
            "- Left out: Tristan (dead); a dead or departed member is not in the party.\n" +
            "- 2014 rules: the active campaign's (belmakor) ruleset.\n\n" +
            "*Sources: 2014 thresholds, multipliers and adventuring-day XP: Dungeon Master's Guide (2014), pp. 82–84, also in the free 2014 Basic Rules (not SRD" +
            " 5.1); CR and XP: the SRD stat blocks, or the XP by CR table for a monster given by CR. The tables: rules_get ref \"rules://tables\".*\n",
            play.DifficultyDefaultEdition);
    }

    [Fact]
    public void Belmakor_25_TheEncounterForm_IsTheExplicitCallsReport_AfterItsNotes_SerifLeftOutWithTheFix()
    {
        const string Report = "# Fight simulation: ";
        var at = play.Simulation.IndexOf(Report, StringComparison.Ordinal);

        Assert.Equal(
            "# Encounter \"The crypt (fixture)\" as a simulation\n\n" +
            "belmakor · active · 2014 rules · as a fresh fight from its combatants. The report below is the one an explicit call with the same entries " +
            "and seed gives; these notes are this encounter's own.\n\n" +
            "- Serif is left out: Serif (artificer 12) has no sim_profile and artificer is not a party archetype (fighter, barbarian, paladin, ranger, " +
            "rogue, monk, cleric, druid, wizard, sorcerer, warlock, bard): give the sheet a sim_profile or leave Serif out.\n\n",
            play.Simulation[..at]);
        Assert.Equal(play.SimulationExplicit, play.Simulation[at..]);

        // The numbers, as measured on D7's rules (FIX §2.5's 0.05% / 23.45% were on another archetype rule; contract §15).
        var text = play.SimulationExplicit;
        Assert.StartsWith("# Fight simulation: Aiden Ironstar, Belmakor Silverwind, Ignis and 2 more entries vs Mummy Lord, Mummy ×2\n\n" +
                          "**The party wins 100%** of 2,000 fights (all 2,000; 95% CI 99.81–100%).\n" +
                          "Party defeated (every member at 0 HP) 0% (0–0.19%) · draw at round 20 0% (0–0.19%) · a party member dies 0% (0–0.19%) · " +
                          "a party member is left dying 24.75% (22.91–26.69%).\n", text, StringComparison.Ordinal);
        Assert.Contains("Rounds: mean 1.79 (95% CI 1.77–1.81), median 2, 90th percentile 2.\n", text, StringComparison.Ordinal);
        Assert.Contains("| Belmakor Silverwind | party | build \"Belmakor L12 Bladesinger (fixture)\" (level 12, 2014) | 22 | 110.0 |", text, StringComparison.Ordinal);
        Assert.Contains("| Lieutenant James Torch | party | archetype wizard (level 12, 2014) | 14 | 74.0 |", text, StringComparison.Ordinal);
        Assert.Contains("| Aiden Ironstar | party | archetype paladin (level 12, 2014) |", text, StringComparison.Ordinal);
        Assert.Contains("| Vars Nocturne | party | archetype ranger (level 12, 2014) |", text, StringComparison.Ordinal);
        Assert.Contains("| Mummy ×2 | enemy | monster 2014/monster/mummy | 11 | 58.0 |", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Serif", text, StringComparison.Ordinal);
        Assert.Contains("- Aiden Ironstar: no sim_profile, simulated as the level 12 paladin archetype (2014) (multiclass paladin 6 / sorcerer 6: the class with " +
                        "the most levels, the first listed on a tie, at the total level)", text, StringComparison.Ordinal);
        Assert.Contains("- Belmakor Silverwind: the sheet's sim_profile (\"Belmakor L12 Bladesinger (fixture)\") at level 12, with the sheet's HP 110, AC 17, " +
                        "save proficiencies Int, Wis, Con, initiative +5.\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Seed 7 (given): the same call gives this result again, fight for fight.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Belmakor_26_FromState_ResumesRoundTwoFromTheLiveState_TheSameCallRepeatsIt_NoExplicitFormClaimed()
    {
        var (first, again) = play.FromState;

        Assert.Equal(first, again);
        Assert.Equal(
            "# Encounter \"The crypt (fixture)\" as a simulation\n\n" +
            "belmakor · active · 2014 rules · resumed from its live state (HP, conditions, concentration, uses left, the turn). No explicit call can " +
            "resume a fight, so this report has no explicit form; the same call with the same seed repeats it. These notes are this encounter's own.\n\n" +
            "- Serif is left out: Serif (artificer 12) has no sim_profile and artificer is not a party archetype (fighter, barbarian, paladin, ranger, " +
            "rogue, monk, cleric, druid, wizard, sorcerer, warlock, bard): give the sheet a sim_profile or leave Serif out.\n" +
            "- Aiden Ironstar: Divine Smite has no slots left (the sheet records no spell slots; give the sheet its slots with campaign_character update).\n" +
            "- Belmakor Silverwind: Arcane Recovery is not simulated.\n" +
            "- Belmakor Silverwind: spell slots are not simulated: 1st-level 3/4 left, 2nd-level 3/3 left, 3rd-level 3/3 left, 4th-level 3/3 left, " +
            "5th-level 1/2 left, 6th-level 1/1 left.\n" +
            "- Lieutenant James Torch: spell slots are not simulated: 1st-level 4/4 left, 2nd-level 3/3 left.\n" +
            "- Vars Nocturne: the sheet records no spell slots, so the resumed fight does not cast Hunter's Mark (1st level or higher) (give the sheet " +
            "its slots with campaign_character update).\n" +
            "- Mummy Lord: spell slots are not simulated: 3rd-level 3/3 left, 4th-level 3/3 left.\n\n",
            first[..first.IndexOf("# Fight simulation: ", StringComparison.Ordinal)]);
        Assert.Contains("# Fight simulation: Aiden Ironstar, Belmakor Silverwind, Ignis and 2 more entries vs Mummy Lord, Mummy, Mummy 2\n", first, StringComparison.Ordinal);

        // FIX §2.6's state: Belmakor 82 (temp 0), Torch 0 and dying, the Mummy Lord 41, the mummies 2 each; "HP at start".
        Assert.Contains("| Combatant | Side | From | AC | HP at start |", first, StringComparison.Ordinal);
        Assert.Contains("| Belmakor Silverwind | party | build \"Belmakor L12 Bladesinger (fixture)\" (level 12, 2014) | 22 | 82.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| Lieutenant James Torch | party | archetype wizard (level 12, 2014) | 14 | 0.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| Mummy Lord | enemy | monster 2014/monster/mummy-lord | 17 | 41.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| Mummy | enemy | monster 2014/monster/mummy | 11 | 2.0 |", first, StringComparison.Ordinal);
        Assert.Contains("| Mummy 2 | enemy | monster 2014/monster/mummy | 11 | 2.0 |", first, StringComparison.Ordinal);
        Assert.Contains("**The party wins 100%** of 2,000 fights", first, StringComparison.Ordinal);
        // Fix F2, review CR03: Vars's sheet records no spell slots, so he casts no Hunter's Mark in the resumed fight. F3,
        // review F2R05: Aiden's (paladin 6 / sorcerer 6) records none either, so his Divine Smite has none, and a note says so.
        Assert.Contains("a party member is left dying 19.45% (17.77–21.24%).\n", first, StringComparison.Ordinal);
        Assert.Contains("Rounds: mean 1.13 (95% CI 1.11–1.14), median 1, 90th percentile 2.\n", first, StringComparison.Ordinal);
    }
}
