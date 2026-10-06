using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.Formatting.Campaign;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>campaign_character</c> through the real client reads a character's sheet in one screen (a character with
/// no sheet, and a DM campaign's party with none, succeed with the call that makes one), and each write action is ONE
/// logged batch under <c>campaign_character/&lt;action&gt;</c> whose id and undo call are printed only when a batch was
/// logged: a dry run says nothing was written, a call that changed nothing says so, and neither prints an undo that would
/// name a batch that does not exist. Every action refuses an argument it does not take, and an author refusal names what
/// to send instead.
///
/// <para>
/// The sheet rules and the repository writes are pinned in DndMcp.Tests (CharacterWriter*, Sheet*); these pin what the
/// host decides: which reader or writer an action reaches with which arguments, the defaults (my character, the current
/// campaign), the mapping of <c>items</c> and <c>coins</c>, the server's dice for a rest, and the text the model reads.
/// </para>
/// </summary>
public sealed class CampaignCharacterToolTests : IAsyncLifetime
{
    private const string PartyBanner = "_Perspective: party. Names are the ones this view knows; author-only text is withheld._";

    private readonly McpServerHarness _server = McpServerHarness.WithExtraTools(builder =>
        builder.Services.AddSingleton<IDiceRoller>(new CharacterToolSetup.FixedRoller([3, 5, 2, 6, 4, 1])));

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await CharacterToolSetup.CreateSkyAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Call(string argumentsJson) => ScenarioCalls.Call(_server, "campaign_character", argumentsJson);

    private Task<string> Fail(string argumentsJson) => ScenarioCalls.Fail(_server, "campaign_character", argumentsJson);

    [Fact]
    public async Task Get_MyCharacterWithNoSheet_SucceedsWithTheCallThatMakesOne()
    {
        var text = await Call("""{"action": "get"}""");

        Assert.Equal(
            "# Belmakor — no sheet yet\n\nNo sheet yet. Make one with campaign_character {\"action\": \"update\", \"character\": " +
            "\"character:belmakor\", \"sheet\": {\"level\": …}, \"campaign\": \"sky\"}; give classes, ac and max_hp as well when you have them.\n",
            text);
    }

    [Fact]
    public async Task Update_FirstCall_CreatesTheSheetInOneBatchThatUndoRemoves()
    {
        var text = await Call($$"""{"action": "update", "sheet": {{CharacterToolSetup.BelmakorSheet}}, "reason": "the fixture sheet"}""");
        var batch = ScenarioCalls.BatchId(text);

        Assert.StartsWith(
            "# campaign_character update: Belmakor (`character:belmakor`, sky)\n\n" +
            $"Batch `{batch}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{batch}\", \"campaign\": \"sky\"}}.\n\n" +
            "Created the sheet.\n",
            text, StringComparison.Ordinal);
        Assert.Contains("- max_hp: none → 110\n", text, StringComparison.Ordinal);
        Assert.Contains("- hp: none → 110\n", text, StringComparison.Ordinal);
        Assert.Contains("Spell slots from the Wizard table at level 12 (2014): 1st 4, 2nd 3, 3rd 3, 4th 3, 5th 2, 6th 1.", text, StringComparison.Ordinal);

        var history = await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "batch", "batch_id": "{{batch}}"}""");
        Assert.Contains("campaign_character/update", history, StringComparison.Ordinal);
        Assert.Contains("the fixture sheet", history, StringComparison.Ordinal);

        await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "undo", "batch_id": "{{batch}}"}""");
        Assert.StartsWith("# Belmakor — no sheet yet\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Author_IsTheWholeSheetOnOneScreen()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "get", "character": "character:belmakor"}""");

        Assert.StartsWith(
            "# Belmakor — level 12 Wizard (Bladesinger), 2014\n" +
            "`character:belmakor` · player Cole · High Elf · background Noble · source fixture\n" +
            "HP 110/110 (+7 temp) · AC 17 · Init +5 · PB +4 · Exhaustion 0\n\n" +
            "- **Spell slots:** 1st 4/4 · 2nd 3/3 · 3rd 3/3 · 4th 3/3 · 5th 2/2 · 6th 1/1\n" +
            "- **Resources:** Bladesong 4/4 (long rest) · Arcane Recovery 1/1 (long rest)\n" +
            "- **Abilities:** Str 11 (+0) · Dex 20 (+5) · Con 16 (+3) · Int 20 (+5) · Wis 13 (+1) · Cha 12 (+1)\n" +
            "- **Saves:** proficient Int, Wis, Con\n" +
            "- **Hit Dice:** d6 12/12 left\n" +
            "- **XP:** none recorded (add a total with campaign_character xp to track it)\n",
            text, StringComparison.Ordinal);
        Assert.Contains("- **Feats:** War Caster · Resilient (Constitution) · Fey Touched · Tough\n", text, StringComparison.Ordinal);
        Assert.Contains("- **Spells:** Circle of Power · Mirror Image · Contingency\n", text, StringComparison.Ordinal);
        Assert.Contains("- **Coins:** none\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ByShortSlugOrSlugOrSeqHandle_IsTheSameSheet()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var byHandle = await Call("""{"action": "get", "character": "character:belmakor"}""");

        Assert.Equal(byHandle, await Call("""{"action": "get", "character": "belmakor"}"""));
        Assert.Equal(byHandle, await Call("""{"action": "get"}"""));
    }

    [Fact]
    public async Task Damage_TempHpFirstThenHp_IsOneBatchUnderTheActionsToolName()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "damage", "amount": 14, "damage_type": "fire"}""");
        var batch = ScenarioCalls.BatchId(text);

        Assert.StartsWith("# campaign_character damage: Belmakor (`character:belmakor`, sky)\n\nBatch `", text, StringComparison.Ordinal);
        Assert.Contains("- temp_hp: 7 → 0\n", text, StringComparison.Ordinal);
        Assert.Contains("- hp: 110 → 103\n", text, StringComparison.Ordinal);
        Assert.Contains("campaign_character/damage", await ScenarioCalls.Call(_server, "campaign_history", $$"""{"action": "batch", "batch_id": "{{batch}}"}"""),
            StringComparison.Ordinal);
        Assert.Contains("HP 103/110 · AC 17", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Damage_DryRun_WritesNothingAndPrintsNoBatchOrUndo()
    {
        await CharacterToolSetup.BelmakorAsync(_server);
        var before = await Call("""{"action": "get"}""");

        var text = await Call("""{"action": "damage", "amount": 30, "dry_run": true}""");

        Assert.StartsWith("# Dry run: campaign_character damage: Belmakor (`character:belmakor`, sky): nothing written\n\n**Dry run: nothing was written", text,
            StringComparison.Ordinal);
        Assert.Contains("- hp: 110 → 87\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", text, StringComparison.Ordinal);
        Assert.DoesNotContain("To undo it", text, StringComparison.Ordinal);
        Assert.Equal(before, await Call("""{"action": "get"}"""));
    }

    [Fact]
    public async Task Update_TheSameValuesAgain_SaysNothingChangedAndPrintsNoBatch()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "update", "sheet": {"ac": 17}}""");

        Assert.Contains("Nothing changed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", text, StringComparison.Ordinal);
        Assert.DoesNotContain("To undo it", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_SlotThenResourceThenRestore_SpendsAndGivesBack()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var slot = await Call("""{"action": "use", "slot_level": 3, "amount": 2}""");
        var resource = await Call("""{"action": "use", "resource": "Bladesong"}""");
        var restored = await Call("""{"action": "use", "slot_level": 3, "amount": -1}""");

        Assert.Contains("- spell_slots 3: {\"max\":3,\"used\":0} → {\"max\":3,\"used\":2}\n", slot, StringComparison.Ordinal);
        Assert.Contains("Bladesong: 1 used, 3/4 left.", resource, StringComparison.Ordinal);
        Assert.Contains("- spell_slots 3: {\"max\":3,\"used\":2} → {\"max\":3,\"used\":1}\n", restored, StringComparison.Ordinal);
        Assert.Contains("- **Spell slots:** 1st 4/4 · 2nd 3/3 · 3rd 2/3 ·", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rest_ShortWithoutRolls_TheServerRollsTheHitDiceAndShowsTheLabel()
    {
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "damage", "amount": 40}""");

        var text = await Call("""{"action": "rest", "kind": "short", "hit_dice": 2}""");

        Assert.Contains("\n## Rolls\n- `2d6` [3, 5] = 8 · logged as \"Belmakor: hit dice\"\n", text, StringComparison.Ordinal);
        Assert.Contains("Hit Dice spent: d6 3, d6 5, +3 Con each → +14 HP; HP 77 → 91.", text, StringComparison.Ordinal);
        ScenarioCalls.BatchId(text);
    }

    [Fact]
    public async Task Rest_ShortWithTheTablesRolls_UsesThemAndRollsNothing()
    {
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "damage", "amount": 40}""");

        var text = await Call("""{"action": "rest", "kind": "short", "hit_dice": 2, "rolls": [6, 6]}""");

        Assert.Contains("Hit Dice spent: d6 6, d6 6, +3 Con each → +18 HP; HP 77 → 95.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Rolls", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Condition_AddThenRemove_IsOnTheSheetBetween()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var added = await Call("""{"action": "condition", "add": ["poisoned"]}""");
        var sheet = await Call("""{"action": "get"}""");
        var removed = await Call("""{"action": "condition", "remove": ["poisoned"]}""");

        Assert.Contains("Added poisoned (until removed).", added, StringComparison.Ordinal);
        Assert.Contains("- **Conditions:** poisoned (until removed)\n", sheet, StringComparison.Ordinal);
        Assert.Contains("Removed poisoned.", removed, StringComparison.Ordinal);
        Assert.DoesNotContain("**Conditions:**", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Condition_ExhaustionByLevel_IsTheColumnNotACondition()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "condition", "add": ["exhaustion"], "level": 2}""");

        Assert.Contains("- exhaustion: 0 → 2\n", text, StringComparison.Ordinal);
        Assert.Contains("· Exhaustion 2\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Xp_PastTheNextThreshold_RemindsThatALevelIsDueWithTheCall()
    {
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "update", "sheet": {"xp": 100000}}""");

        var text = await Call("""{"action": "xp", "amount": 25000}""");

        Assert.Contains("- xp: 100000 → 125000\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Reminders\n- ", text, StringComparison.Ordinal);
        Assert.Matches(new Regex("## Reminders\\n- [^\\n]*campaign_character \\{\"action\": \"level_up\""), text);
        Assert.Contains("- **XP:** 125,000 (the next level at 120,000)", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ASheetWithNoXpTotal_SaysNoneIsRecordedAndHowToStartOne_NeverALevellingPolicy()
    {
        // Fix F2, review U02: "not tracked (milestone levels)" read as a policy the DM had chosen, so a model asked to split
        // the XP declined to start a total ("that changes how levelling works for the whole campaign"). The sheet simply
        // has no XP yet: the line says so and names the action that starts one, which then reads as a number.
        await CharacterToolSetup.BelmakorAsync(_server);

        var before = await Call("""{"action": "get"}""");
        await Call("""{"action": "xp", "amount": 300}""");

        Assert.Contains("\n- **XP:** none recorded (add a total with campaign_character xp to track it)\n", before, StringComparison.Ordinal);
        Assert.DoesNotContain("milestone", before, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\n- **XP:** 300 (the next level at ", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LevelUp_SingleClassCaster_AddsTheSlotAndRemindsToUpdateResources()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "level_up"}""");

        Assert.Contains("- level: 12 → 13\n", text, StringComparison.Ordinal);
        Assert.Contains("- spell_slots 7: none → {\"max\":1,\"used\":0}\n", text, StringComparison.Ordinal);
        Assert.Contains("Proficiency bonus +4 → +5.", text, StringComparison.Ordinal);
        Assert.Contains("\n## Reminders\n- Update resources for the new level", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventory_TakingTheLastOnes_DeletesTheHoldingInTheBatch()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var added = await Call("""{"action": "inventory", "items": [{"item": "Potion of Healing", "qty": 2, "srd": "2014/equipment/potion-of-healing"}, {"item": "Rope"}]}""");
        var listed = await Call("""{"action": "get"}""");
        var taken = await Call("""{"action": "inventory", "items": [{"item": "potion of healing", "qty": -2}]}""");

        Assert.Contains("- inventory Potion of Healing: none → 2\n", added, StringComparison.Ordinal);
        Assert.Contains("- inventory Rope: none → 1\n", added, StringComparison.Ordinal);
        // Holdings gained in one call share their time: their names order them (the reader's tie-break), every time.
        Assert.Contains("- **Inventory:** Potion of Healing ×2 (`2014/equipment/potion-of-healing`) · Rope ×1\n", listed, StringComparison.Ordinal);
        Assert.Contains("- inventory Potion of Healing: 2 → removed\n", taken, StringComparison.Ordinal);
        ScenarioCalls.BatchId(taken);
        Assert.Contains("- **Inventory:** Rope ×1\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"equipped\": true", "Potion of Healing ×2 (equipped)")]
    [InlineData("\"attuned\": true", "Potion of Healing ×2 (attuned)")]
    [InlineData("\"notes\": \"from Tom\"", "Potion of Healing ×2 (from Tom)")]
    [InlineData("\"srd\": \"2014/equipment/potion-of-healing\"", "Potion of Healing ×2 (`2014/equipment/potion-of-healing`)")]
    public async Task Inventory_AHeldItemGivenOnlyAFieldAndNoQty_ChangesOnlyThatField(string field, string shown)
    {
        // Fix F1, C11 (and F2: srd too): the qty text promises that an entry for a held item with equipped, attuned, notes or
        // srd and no qty changes only those; a second potion here would be an item nobody gained.
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "inventory", "items": [{"item": "Potion of Healing", "qty": 2}]}""");

        await Call($$"""{"action": "inventory", "items": [{"item": "Potion of Healing", {{field}}}]}""");

        Assert.Contains($"- **Inventory:** {shown}\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Currency_SpendingMoreThanHeld_AppliesWithTheWarning()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "currency", "coins": {"gp": 120, "sp": -3}, "reason": "the ferry"}""");

        Assert.Contains("- coins gp: 0 → 120\n", text, StringComparison.Ordinal);
        Assert.Contains("- coins sp: 0 → -3\n", text, StringComparison.Ordinal);
        Assert.Contains("## Warnings (1)", text, StringComparison.Ordinal);
        Assert.Contains("negative balance", text, StringComparison.Ordinal);
        Assert.Contains("- **Coins:** 120 gp, -3 sp\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inventory_EveryFieldOfAnItem_ReachesTheHolding()
    {
        // The tool maps items field by field (InventoryItemInput → InventoryItem): a field dropped or crossed with
        // another stores wrong data that no refusal would show.
        await CharacterToolSetup.BelmakorAsync(_server);

        await Call("""
            {"action": "inventory", "items": [{"item": "Ring", "equipped": true, "attuned": false, "notes": "from the vault"},
                                              {"item": "Cloak", "equipped": false, "attuned": true, "srd": "2014/magic-item/cloak-of-protection"}]}
            """);

        Assert.Contains(
            "- **Inventory:** Cloak ×1 (`2014/magic-item/cloak-of-protection`; attuned) · Ring ×1 (equipped; from the vault)\n",
            await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Currency_EachDenomination_LandsInItsOwnColumn()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "currency", "coins": {"cp": 1, "sp": 2, "ep": 3, "gp": 4, "pp": 5}}""");

        Assert.Contains("- coins cp: 0 → 1\n- coins sp: 0 → 2\n- coins ep: 0 → 3\n- coins gp: 0 → 4\n- coins pp: 0 → 5\n", text, StringComparison.Ordinal);
        Assert.Contains("- **Coins:** 5 pp, 4 gp, 3 ep, 2 sp, 1 cp\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Author_PrintsEveryStateTheSheetCanBeIn()
    {
        // §7.3's rarer states: Pact Magic slots by their level, a reduced maximum with what reduced it, dying with its saves,
        // and a coin balance below 0.
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "update", "sheet": {"pact": {"level": 3, "max": 2}, "max_hp_reduction": 10}}""");
        await Call("""{"action": "currency", "coins": {"gp": -5}}""");
        await Call("""{"action": "damage", "amount": 200}""");
        await Call("""{"action": "damage", "amount": 1}""");

        var text = await Call("""{"action": "get"}""");

        Assert.Contains("HP 0/100 (maximum 110, reduced by 10) · AC 17 · Init +5 · PB +4 · Exhaustion 0\n\n- **Dying: 0 successes, 1 failure**\n", text,
            StringComparison.Ordinal);
        Assert.Contains("- **Spell slots:** 1st 4/4 · 2nd 3/3 · 3rd 3/3 · 4th 3/3 · 5th 2/2 · 6th 1/1 · pact (3rd) 2/2\n", text, StringComparison.Ordinal);
        Assert.Contains("- **Coins:** -5 gp\n  - A coin balance is below 0: more was spent than the ledger holds.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_DeadOfMassiveDamage_SaysDeadWithNoCauseTheSheetCannotTell()
    {
        // Fix F1, C10: every death but exhaustion 6 and a maximum of 0 is stored as three failures, so a massive-damage death
        // must not read "three failed death saves" (a cause that did not happen); the sheet says "Dead" alone.
        await CharacterToolSetup.BelmakorAsync(_server);
        Assert.Contains("died (massive damage)", await Call("""{"action": "damage", "amount": 300}"""), StringComparison.Ordinal);

        var text = await Call("""{"action": "get"}""");

        Assert.Contains("· Exhaustion 0\n\n- **Dead**\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("three failed death saves", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action": "condition", "add": ["exhaustion"], "level": 6}""", "- **Dead (exhaustion 6)**\n")]
    [InlineData("""{"action": "update", "sheet": {"max_hp_reduction": 110}}""", "- **Dead (a hit point maximum of 0)**\n")]
    public async Task Get_DeadOfACauseTheSheetShows_SaysTheCause(string fatal, string line)
    {
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call(fatal);

        Assert.Contains(line, await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_PublicLine_IsTheBannerThenTheLineWithExhaustion()
    {
        // §7.4: exhaustion is in the public line; and a non-author read, like every one, starts with the view's banner.
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "condition", "add": ["exhaustion"], "level": 2}""");
        await Call("""{"action": "condition", "add": ["poisoned"]}""");

        var text = await Call("""{"action": "get", "perspective": "party"}""");

        Assert.Equal(
            "# Belmakor\n" + PartyBanner + "\n\n" +
            "**Belmakor** (`character:belmakor`) — level 12 Wizard (Bladesinger), High Elf · HP 110/110 (+7 temp) · AC 17 · Exhaustion 2 · poisoned\n",
            text);
    }

    [Fact]
    public async Task LevelUp_NewSrdClass_StartsItAtLevel1()
    {
        // level_up's class reaches the writer: a class the sheet does not have yet is a multiclass at its level 1.
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "level_up", "class": "fighter"}""");

        Assert.Contains("- level: 12 → 13\n", text, StringComparison.Ordinal);
        Assert.Contains("{\"class\":\"fighter\",\"level\":1", text, StringComparison.Ordinal);
        Assert.StartsWith("# Belmakor — level 13 Wizard 12 (Bladesinger) / Fighter 1, 2014\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Examples_EachActionsExample_WorksVerbatimInOrderOnTheSheetTheUpdateExampleMakes()
    {
        // A refusal's example is what a model sends next: each must work as printed, in order, on the sheet the update
        // example makes (the use example once named a resource that sheet does not have).
        await ScenarioCalls.Call(_server, "campaign", """{"action": "create", "name": "Belmakor", "role": "player", "ruleset": "2014", "my_character": "Belmakor"}""");
        string[] actions = ["update", "get", "damage", "heal", "temp_hp", "use", "rest", "condition", "level_up", "xp", "inventory", "currency"];

        foreach (var action in actions)
        {
            var refusal = await Fail(action == "get" ? """{"action": "get", "session": 1}""" : $$"""{"action": "{{action}}", "perspective": "party"}""");
            var example = refusal[(refusal.IndexOf("Example: ", StringComparison.Ordinal) + "Example: ".Length)..];

            Assert.StartsWith($$"""{"action": "{{action}}", """, example, StringComparison.Ordinal);
            Assert.DoesNotContain("An error occurred", await Call(example), StringComparison.Ordinal);
        }
    }

    [Theory]
    // §7.1's per-action table, one argument at a time, each sent with a valid shape to an action that does not take it:
    // sent together (ServerSurfaceTests' every-argument call), any one refusal hides another argument's missing entry,
    // and the action would then drop that argument silently ({"action": "damage", "coins": …} applying the damage alone).
    [InlineData("update", "\"perspective\": \"party\"", "perspective")]
    [InlineData("get", "\"sheet\": {\"ac\": 12}", "sheet")]
    [InlineData("damage", "\"sim_profile\": {\"name\": \"Belmakor\"}", "sim_profile")]
    [InlineData("inventory", "\"amount\": 3", "amount")]
    [InlineData("heal", "\"damage_type\": \"fire\"", "damage_type")]
    [InlineData("rest", "\"slot_level\": 1", "slot_level")]
    [InlineData("rest", "\"pact\": true", "pact")]
    [InlineData("condition", "\"resource\": \"Rage\"", "resource")]
    [InlineData("update", "\"kind\": \"long\"", "kind")]
    [InlineData("xp", "\"hit_dice\": 1", "hit_dice")]
    [InlineData("use", "\"rolls\": [3]", "rolls")]
    [InlineData("level_up", "\"add\": [\"poisoned\"]", "add")]
    [InlineData("xp", "\"remove\": [\"poisoned\"]", "remove")]
    [InlineData("get", "\"level\": 1", "level")]
    [InlineData("damage", "\"class\": \"fighter\"", "class")]
    [InlineData("currency", "\"items\": [{\"item\": \"Rope\"}]", "items")]
    [InlineData("heal", "\"coins\": {\"gp\": 1}", "coins")]
    [InlineData("get", "\"session\": 1", "session")]
    [InlineData("get", "\"reason\": \"why\"", "reason")]
    [InlineData("get", "\"dry_run\": true", "dry_run")]
    public async Task Action_EachArgumentAloneOnAnActionThatDoesNotTakeIt_IsRefused(string action, string argument, string name)
    {
        var text = await Fail($$"""{"action": "{{action}}", {{argument}}}""");

        Assert.StartsWith($"An error occurred invoking 'campaign_character': campaign_character {action} does not take \"{name}\"; {action} takes ", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_SimProfile_IsStoredAndSummarizedOnTheSheet()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call($$"""{"action": "update", "sim_profile": {{CharacterToolSetup.BelmakorProfile}} }""");

        Assert.Contains("- sim_profile: none → ", text, StringComparison.Ordinal);
        ScenarioCalls.BatchId(text);
        Assert.Contains("- **sim_profile:** Belmakor, level 12; attacks Scimitar", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_SimProfileOnASheetWithNoLevel_IsRefusedByName()
    {
        var text = await Fail($$"""{"action": "update", "sim_profile": {{CharacterToolSetup.BelmakorProfile}} }""");

        Assert.StartsWith("An error occurred invoking 'campaign_character': ", text, StringComparison.Ordinal);
        Assert.Contains("sim_profile", text, StringComparison.Ordinal);
        Assert.Contains("level", text, StringComparison.Ordinal);
        Assert.StartsWith("# Belmakor — no sheet yet\n", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_NothingGiven_SaysWhatUpdateTakes()
    {
        var text = await Fail("""{"action": "update"}""");

        Assert.Equal(
            "An error occurred invoking 'campaign_character': update needs sheet (the fields to set), sim_profile, or both, e.g. " +
            "{\"action\": \"update\", \"sheet\": {\"classes\": [{\"class\": \"wizard\", \"level\": 12}], \"ac\": 17}}.",
            text);
    }

    [Fact]
    public async Task Update_CharacterTheCampaignLacks_IsRefusedWithTheUpsertThatCreatesIt()
    {
        var text = await Fail("""{"action": "update", "character": "character:nobody", "sheet": {"level": 3}}""");

        Assert.Contains("character \"character:nobody\": no character by that handle in sky.", text, StringComparison.Ordinal);
        Assert.Contains("campaign_write {\"ops\": [{\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"…\"}], \"campaign\": \"sky\"}", text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action": "get", "character": "one-piece/character:keras"}""")]
    [InlineData("""{"action": "damage", "character": "one-piece/character:keras", "amount": 3}""")]
    [InlineData("""{"action": "update", "character": "one-piece/character:keras", "sheet": {"level": 3}}""")]
    public async Task AnyAction_AnotherCampaignsHandle_IsRefused(string argumentsJson)
    {
        var text = await Fail(argumentsJson);

        Assert.Contains("names another campaign's entity; give a character of sky", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("damage", "\"amount\": 3")]
    [InlineData("heal", "\"amount\": 3")]
    [InlineData("temp_hp", "\"amount\": 3")]
    [InlineData("use", "\"slot_level\": 1")]
    [InlineData("rest", "\"kind\": \"long\"")]
    [InlineData("condition", "\"add\": [\"poisoned\"]")]
    [InlineData("level_up", "\"class\": \"wizard\"")]
    [InlineData("xp", "\"amount\": 300")]
    [InlineData("inventory", "\"items\": [{\"item\": \"Rope\"}]")]
    [InlineData("currency", "\"coins\": {\"gp\": 1}")]
    [InlineData("update", "\"sheet\": {\"ac\": 12}")]
    public async Task WriteAction_Perspective_IsRefusedAndNothingIsWritten(string action, string arguments)
    {
        // A write in a player's view means nothing (contract D10): perspective belongs to get alone.
        await CharacterToolSetup.BelmakorAsync(_server);
        var before = await Call("""{"action": "get"}""");

        var text = await Fail($$"""{"action": "{{action}}", {{arguments}}, "perspective": "party"}""");

        Assert.StartsWith($"An error occurred invoking 'campaign_character': campaign_character {action} does not take \"perspective\"; {action} takes ", text,
            StringComparison.Ordinal);
        Assert.Equal(before, await Call("""{"action": "get"}"""));
    }

    [Theory]
    [InlineData("""{"action": "get", "session": 1}""", "get does not take \"session\"; get takes perspective, character, campaign.")]
    [InlineData("""{"action": "get", "dry_run": true}""", "get does not take \"dry_run\"; get takes perspective, character, campaign.")]
    [InlineData("""{"action": "heal", "damage_type": "fire"}""", "heal does not take \"damage_type\"; heal takes amount, character, campaign, session, reason, dry_run.")]
    [InlineData("""{"action": "rest", "add": ["poisoned"]}""", "rest does not take \"add\"; rest takes kind, hit_dice, rolls, character, campaign, session, reason, dry_run.")]
    [InlineData("""{"action": "update", "items": [{"item": "Rope"}]}""", "update does not take \"items\"; update takes sheet, sim_profile, character, campaign, session, reason, dry_run.")]
    public async Task Action_ArgumentItDoesNotTake_IsRefusedWithWhatItTakesAndAnExample(string argumentsJson, string message)
    {
        var text = await Fail(argumentsJson);

        Assert.StartsWith("An error occurred invoking 'campaign_character': campaign_character " + message + " Example: {\"action\": ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Action_UnknownAction_ListsTheActionsWithAnExample()
    {
        var text = await Fail("""{"action": "spend_slot"}""");

        Assert.Equal(
            "An error occurred invoking 'campaign_character': action \"spend_slot\" is not a campaign_character action; give get, update, damage, heal, " +
            "temp_hp, use, rest, condition, level_up, xp, inventory, currency. Example: " + Tools.CampaignCharacterTools.Example,
            text);
    }

    [Fact]
    public async Task Inventory_NullItem_IsRefusedByPosition()
    {
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Fail("""{"action": "inventory", "items": [{"item": "Rope"}, null]}""");

        Assert.Contains("items item 2: is null; give an object such as {\"item\": \"Rope\", \"qty\": 1}.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Typical_IsUnderEightThousandCharacters()
    {
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call($$"""{"action": "update", "sim_profile": {{CharacterToolSetup.BelmakorProfile}} }""");
        await Call("""{"action": "inventory", "items": [{"item": "Potion of Healing", "qty": 2}, {"item": "Spellbook"}, {"item": "Ivory statuette"}]}""");

        var text = await Call("""{"action": "get"}""");

        Assert.InRange(text.Length, 500, 8_000);
        Assert.DoesNotContain("Output cut at", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Largest_IsCutAtTheCapWithTheHint()
    {
        // Every list at its longest (feats, features, spells and languages at the sheet's 100 entries of 80 characters, 50
        // holdings, notes past what the view shows): the cap holds and says where the rest is.
        string Names(string prefix, int count) =>
            string.Join(", ", Enumerable.Range(1, count).Select(i => $"\"{prefix} {i:D3} {new string('x', 80 - prefix.Length - 5)}\""));
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call($$"""
            {"action": "update", "sheet": {"feats": [{{Names("Feat", 100)}}], "features": [{{Names("Feature", 100)}}], "spells": [{{Names("Spell", 100)}}],
             "languages": [{{Names("Language", 100)}}], "notes": "{{new string('n', 6_000)}}"} }
            """);
        for (var call = 0; call < 2; call++)
        {
            var items = string.Join(", ", Enumerable.Range(call * 25, 25).Select(i => $$"""{"item": "Holding {{i:D2}} {{new string('h', 60)}}", "notes": "{{new string('o', 150)}}"}"""));
            await Call($$"""{"action": "inventory", "items": [{{items}}]}""");
        }

        var text = await Call("""{"action": "get"}""");

        Assert.InRange(text.Length, 20_000, CampaignMarkdownText.MaxChars);
        Assert.EndsWith("_Output cut at 24,000 characters; the sheet is complete; read one character at a time, or campaign_get with include [\"sheet\"]._",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_LargestCreate_ListsSixtyChangesThenCounts()
    {
        var spells = string.Join(", ", Enumerable.Range(1, 80).Select(i => $"\"Spell {i:D2}\""));

        var text = await Call($$"""{"action": "update", "sheet": {"level": 5, "spells": [{{spells}}], "notes": "{{new string('n', 3_000)}}"} }""");

        Assert.True(text.Length < CampaignMarkdownText.MaxChars, $"{text.Length} characters");
        Assert.Contains("- notes_md: none → " + new string('n', SheetWriteMarkdown.MaxValueChars) + "…\n", text, StringComparison.Ordinal);
        Assert.Contains("- spells: [] → [{\"name\":\"Spell 01\"}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_MoreThanSixtyFieldsChanged_TheCutSaysHowManyMore()
    {
        // Review M (mutant W02): a write of more than SheetWriteMarkdown.MaxChanges fields cut its list with no word of what
        // was cut. A first update with player, species, background, speed, a class, six abilities, ac and 40 resources writes
        // 63 changes: 60 listed, then the count of the rest.
        var resources = string.Join(", ", Enumerable.Range(1, 40).Select(i => $$"""{"name": "Resource {{i}}", "max": 2}"""));

        var text = await Call(
            "{\"action\": \"update\", \"sheet\": {\"player\": \"Cole\", \"species\": \"High Elf\", \"background\": \"Noble\", \"speed\": 30, " +
            "\"classes\": [{\"class\": \"wizard\", \"level\": 12}], \"abilities\": {\"str\": 11, \"dex\": 20, \"con\": 16, \"int\": 20, \"wis\": 13, \"cha\": 12}, " +
            "\"ac\": 17, \"resources\": [" + resources + "]}}");

        Assert.Contains("\n- … and 3 more fields\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_DryRunOnACharacterWithNoSheet_SaysItWouldCreateIt()
    {
        // Review M (mutant W01): a dry run of the first update said "Created the sheet." though nothing was written.
        var text = await Call("""{"action": "update", "sheet": {"level": 3}, "dry_run": true}""");

        Assert.Contains("Would create the sheet.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Created the sheet.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_Pact_SpendsAPactMagicSlot()
    {
        // Review M (mutant C01): use {pact: true} reached the writer as false (only pact's per-action refusal was pinned).
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "update", "sheet": {"pact": {"level": 3, "max": 2}}}""");

        await Call("""{"action": "use", "pact": true}""");

        Assert.Contains("pact (3rd) 1/2", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LevelUp_Amount_IsTheHitPointsGained_NotTheFixedValue()
    {
        // Review M (mutant C04): with amount dropped every rolled level took the fixed value (Belmakor, wizard 12, Con +3:
        // 4 + 3 = 7 gives 117), a wrong maximum the sheet then kept.
        await CharacterToolSetup.BelmakorAsync(_server);

        var text = await Call("""{"action": "level_up", "amount": 9}""");

        Assert.Contains("- max_hp: 110 → 119\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Damage_DamageType_TheSheetsResistanceHalvesIt()
    {
        // Review M (mutant C05): with damage_type dropped every typed hit out of combat was taken untyped. 20 fire, resisted:
        // 10; Belmakor's 7 temporary hit points absorb 7 of it, so HP 110 → 107.
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "update", "sheet": {"defenses": {"resist": ["fire"]}}}""");

        await Call("""{"action": "damage", "amount": 20, "damage_type": "fire"}""");

        Assert.Contains("HP 107/110", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AFractionalQuantity_IsPrintedAsStored()
    {
        // Review M (mutant K02): holding.quantity is REAL and inventory takes 0.5; a format with no decimals printed ×1.
        await CharacterToolSetup.BelmakorAsync(_server);
        await Call("""{"action": "inventory", "items": [{"item": "Silk rope", "qty": 0.5}]}""");

        Assert.Contains("Silk rope ×0.5", await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, """{"classes": [{"class": "wizard", "level": 6}, {"class": "fighter", "level": 6}], "abilities": {"con": 14}}""", "- **Hit Dice:** d10 6/6 left · d6 6/6 left\n")]
    [InlineData(true, """{"inspiration": true}""", "- **Other:** Inspiration\n")]
    [InlineData(true, """{"defenses": {"condition_immune": ["poisoned"]}}""", "- **Defenses:** condition immune poisoned\n")]
    public async Task Get_TheAuthorSheet_ShowsWhatTheSheetHolds(bool belmakor, string sheet, string line)
    {
        // Review M (mutants K03, K04, K07): a multiclass sheet's Hit Dice largest die first, Inspiration, and condition
        // immunities on the Defenses line were never read back by a test.
        if (belmakor)
        {
            await CharacterToolSetup.BelmakorAsync(_server);
        }

        await Call($$"""{"action": "update", "sheet": {{sheet}}}""");

        Assert.Contains(line, await Call("""{"action": "get"}"""), StringComparison.Ordinal);
    }
}

/// <summary>
/// Invariant: in a DM campaign <c>get</c> with no character is the party's list (every current member's line for the
/// author, a member with no sheet with the call that makes one, the dead and departed named apart), a write with no
/// character is refused listing the party's handles, and the list is the <c>campaign://&lt;slug&gt;/party</c> resource's
/// text. An empty party succeeds with the call that would start one.
/// </summary>
public sealed class CampaignCharacterToolPartyTests : IAsyncLifetime
{
    private readonly McpServerHarness _server = new();

    public async Task InitializeAsync() => await _server.InitializeAsync();

    public Task DisposeAsync() => _server.DisposeAsync();

    [Fact]
    public async Task Get_DmCampaignWithNoMembers_SaysNoPartySheetsYetWithTheCall()
    {
        await ScenarioCalls.Call(_server, "campaign", """{"action": "create", "name": "Deep", "role": "dm", "ruleset": "2024", "slug": "deep"}""");

        var text = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get"}""");

        Assert.Equal(
            "# Deep: the party's sheets\n\nNo party sheets yet: campaign_character {\"action\": \"update\", \"character\": \"character:…\", " +
            "\"sheet\": {\"level\": …}, \"campaign\": \"deep\"} makes one for a member (link a character member_of the party first).\n",
            text);
    }

    [Fact]
    public async Task Get_ListForAViewShownNoMember_SaysNothingToShowAndNoCall()
    {
        // A non-author list says nothing about who it leaves out (§7.4): no "No party sheets yet" and no update call, which
        // are the author's.
        await ScenarioCalls.Call(_server, "campaign", """{"action": "create", "name": "Deep", "role": "dm", "ruleset": "2024", "slug": "deep"}""");

        var text = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "perspective": "party"}""");

        Assert.Equal(
            "# Deep: the party's sheets\n_Perspective: party. Names are the ones this view knows; author-only text is withheld._\n\nNothing to show.\n",
            text);
    }

    [Fact]
    public async Task Get_AConcentrationOnTheSheet_IsInTheListLineAndOnTheSheet()
    {
        // No campaign_character call sets a sheet's concentration (only a fight's write-back does), so it is written here
        // as the write-back stores it: the column's JSON object.
        await CharacterToolSetup.CreateDeepAsync(_server);
        CharacterToolSetup.SetColumn(_server, "bjorn-mountainfell", "concentration", """{"spell":"Fly","level":3,"remaining_rounds":98}""");

        var list = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get"}""");
        var sheet = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "character": "bjorn"}""");

        Assert.Contains(
            "- **Björn Mountainfell** (`character:bjorn-mountainfell`) — level 8 Barbarian (Path of the Totem Warrior), 2024 · HP 85/85 · AC 15 · Init +2 · " +
            "concentrating on Fly\n", list, StringComparison.Ordinal);
        Assert.Contains("- **Concentration:** Fly (3rd level; 98 rounds left)\n", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_DmCampaignWithoutCharacter_ListsEveryCurrentMemberAndTheDeadApart()
    {
        await CharacterToolSetup.CreateDeepAsync(_server);

        var text = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get"}""");

        Assert.Equal(
            "# Deep: the party's sheets\n\n" +
            "- **Björn Mountainfell** (`character:bjorn-mountainfell`) — level 8 Barbarian (Path of the Totem Warrior), 2024 · HP 85/85 · AC 15 · Init +2\n" +
            "- **Kaz** (`character:kaz`) — no sheet yet: campaign_character {\"action\": \"update\", \"character\": \"character:kaz\", \"sheet\": " +
            "{\"level\": …}, \"campaign\": \"deep\"}\n" +
            "\nNot in the party now: Old Tom (`character:old-tom`, dead).\n",
            text);
        Assert.Equal(text, await ScenarioCalls.Read(_server, "campaign://deep/party"));
    }

    [Fact]
    public async Task Get_DmCampaignMemberWithNoSheet_SucceedsWithTheCall()
    {
        await CharacterToolSetup.CreateDeepAsync(_server);

        var text = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "character": "kaz"}""");

        Assert.StartsWith("# Kaz — no sheet yet\n\nNo sheet yet. Make one with campaign_character {\"action\": \"update\", \"character\": \"character:kaz\"",
            text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action": "damage", "amount": 3}""")]
    [InlineData("""{"action": "update", "sheet": {"level": 2}}""")]
    public async Task WriteAction_DmCampaignWithoutCharacter_IsRefusedListingTheParty(string argumentsJson)
    {
        // D8's party (PartyRoster): Old Tom is linked member_of the party but dead, so he is not "one of the party".
        await CharacterToolSetup.CreateDeepAsync(_server);

        var text = await ScenarioCalls.Fail(_server, "campaign_character", argumentsJson);

        Assert.Equal(
            "An error occurred invoking 'campaign_character': character is required in a DM campaign: give the character's handle, one of the party: " +
            "character:bjorn-mountainfell, character:kaz.",
            text);
    }

    [Fact]
    public async Task Get_HandleThatNamesNothingInADmCampaign_ListsTheCurrentPartyOnly()
    {
        await CharacterToolSetup.CreateDeepAsync(_server);

        var text = await ScenarioCalls.Fail(_server, "campaign_character", """{"action": "get", "character": "character:nobody"}""");

        Assert.EndsWith("\"character:nobody\": no character by that handle in deep. The party: character:bjorn-mountainfell, character:kaz.", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAction_ACharacterWithNoSheet_IsRefusedWithTheCallThatMakesOne()
    {
        // One order for the call that makes a sheet wherever it is printed (the writer's refusal, get, /party,
        // encounter_difficulty): the sheet reminders' order, the campaign last.
        await CharacterToolSetup.CreateDeepAsync(_server);

        var text = await ScenarioCalls.Fail(_server, "campaign_character", """{"action": "damage", "character": "character:kaz", "amount": 3}""");

        Assert.Equal(
            "An error occurred invoking 'campaign_character': Kaz (character:kaz) has no sheet yet: create it first with campaign_character " +
            "{\"action\": \"update\", \"character\": \"character:kaz\", \"sheet\": {\"level\": …}, \"campaign\": \"deep\"}.",
            text);
        Assert.Contains(SheetMarkdown.UpdateCall("deep", "character:kaz") + ".", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NamedCampaignThatIsNotCurrent_ReadsThatCampaign()
    {
        await CharacterToolSetup.CreateDeepAsync(_server);
        await CharacterToolSetup.CreateSkyAsync(_server);

        var text = await ScenarioCalls.Call(_server, "campaign_character", """{"action": "get", "campaign": "deep", "character": "bjorn"}""");

        Assert.StartsWith("# Björn Mountainfell — level 8 Barbarian (Path of the Totem Warrior), 2024\n", text, StringComparison.Ordinal);
    }
}

/// <summary>The worlds and fakes the campaign_character tests share.</summary>
internal static class CharacterToolSetup
{
    /// <summary>Fixture A's Belmakor sheet (FIX §1, contract §16): 2014 Bladesinger 12, HP 110 (+7 temp from False Life), AC 17.</summary>
    public const string BelmakorSheet = """
        {"player": "Cole", "ruleset": "2014", "species": "High Elf", "background": "Noble",
         "classes": [{"class": "wizard", "subclass": "Bladesinger", "level": 12}],
         "abilities": {"str": 11, "dex": 20, "con": 16, "int": 20, "wis": 13, "cha": 12},
         "ac": 17, "max_hp": 110, "temp_hp": 7, "save_proficiencies": ["int", "wis", "con"],
         "resources": [{"name": "Bladesong", "max": 4, "recharge": "long_rest"}, {"name": "Arcane Recovery", "max": 1, "recharge": "long_rest"}],
         "feats": ["War Caster", "Resilient (Constitution)", "Fey Touched", "Tough"],
         "spells": ["Circle of Power", "Mirror Image", "Contingency"],
         "sheet_source": "fixture"}
        """;

    /// <summary>Belmakor's sim_profile (FIX §2.10: two magical scimitars, INVENTED).</summary>
    public const string BelmakorProfile = """
        {"name": "Belmakor", "level": 12, "abilities": {"dex": 20, "int": 20},
         "attacks": [{"name": "Scimitar", "count": 2, "damage": "1d6", "damage_type": "slashing", "properties": ["melee", "finesse", "light", "magical"]}]}
        """;

    /// <summary>The player campaign "sky" (2014) whose character is Belmakor, made current.</summary>
    public static Task<string> CreateSkyAsync(McpServerHarness server) => ScenarioCalls.Call(server, "campaign", """
        {"action": "create", "name": "Sky", "role": "player", "ruleset": "2014", "slug": "sky", "my_character": "Belmakor"}
        """);

    /// <summary>Belmakor's sheet in "sky".</summary>
    public static Task<string> BelmakorAsync(McpServerHarness server) =>
        ScenarioCalls.Call(server, "campaign_character", $$"""{"action": "update", "campaign": "sky", "character": "character:belmakor", "sheet": {{BelmakorSheet}} }""");

    /// <summary>
    /// The DM campaign "deep" (2024): Björn (level 8 with a sheet) and Kaz (no sheet) in the party, Old Tom a dead member, and
    /// an NPC with a sheet who is not in the party.
    /// </summary>
    public static async Task CreateDeepAsync(McpServerHarness server)
    {
        await ScenarioCalls.Call(server, "campaign", """{"action": "create", "name": "Deep", "role": "dm", "ruleset": "2024", "slug": "deep"}""");
        await ScenarioCalls.Call(server, "campaign_write", """
            {"campaign": "deep", "ops": [
              {"op": "upsert", "kind": "character", "name": "Björn Mountainfell", "subtype": "pc", "visibility": "party"},
              {"op": "upsert", "kind": "character", "name": "Kaz", "subtype": "pc", "visibility": "party"},
              {"op": "upsert", "kind": "character", "name": "Old Tom", "subtype": "pc", "visibility": "party", "status": "dead"},
              {"op": "upsert", "kind": "character", "name": "Harbour Master", "subtype": "npc", "visibility": "party"},
              {"op": "link", "from": "character:bjorn-mountainfell", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:kaz", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:old-tom", "rel": "member_of", "to": "faction:the-party"}]}
            """);
        await ScenarioCalls.Call(server, "campaign_character", """
            {"action": "update", "campaign": "deep", "character": "character:bjorn-mountainfell",
             "sheet": {"classes": [{"class": "barbarian", "subclass": "Path of the Totem Warrior", "level": 8}],
                       "abilities": {"str": 18, "dex": 14, "con": 16, "int": 8, "wis": 12, "cha": 10}, "ac": 15, "max_hp": 85}}
            """);
        await ScenarioCalls.Call(server, "campaign_character", """
            {"action": "update", "campaign": "deep", "character": "character:harbour-master", "sheet": {"level": 4, "ac": 12, "max_hp": 30}}
            """);
    }

    /// <summary>
    /// Writes one character_sheet column directly (a value no host call writes out of combat, such as concentration), on a
    /// connection of its own beside the server's.
    /// </summary>
    public static void SetColumn(McpServerHarness server, string slug, string column, string value)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(server.DataDirectory, "campaigns.db"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE character_sheet SET {column} = $value WHERE entity_id = (SELECT id FROM entity WHERE kind = 'character' AND slug = $slug)";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$slug", slug);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    /// <summary>A dice roller that gives the faces it was handed, in order (a server roll the test can predict).</summary>
    public sealed class FixedRoller(int[] faces) : IDiceRoller
    {
        private int _next;

        public string Source => "fixed (test fake)";

        public int Roll(int sides) => faces[_next++ % faces.Length];
    }
}

/// <summary>
/// Invariant: campaign_character's description tells the model what it cannot learn from the schema (contract §7.1, D5,
/// stage-2 C's deviation 2): that in the live fight the routable actions change the fight and rest waits, that
/// <c>remove: ["concentration"]</c> ends a sheet's concentration out of combat, and that a past sheet is read through
/// campaign_get; and that it stays within Claude Code's 2,048-character limit.
/// </summary>
public sealed class CampaignCharacterToolDescriptionTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public CampaignCharacterToolDescriptionTests(McpServerHarness server)
    {
        _server = server;
    }

    [Theory]
    [InlineData("While the character is in the active combat, damage, heal, temp_hp, use and condition act on the fight (written back at its end) and rest is refused.")]
    [InlineData("[\"concentration\"] ends the sheet's concentration")]
    [InlineData("a past sheet: campaign_get include [\"sheet\"] with as_of_session.\n")]
    [InlineData("sim_profile (a build as balance_dpr takes it: how balance_simulate fights them)")]
    [InlineData("Every action takes character (default: your character in a player campaign) and campaign (default: the active one); all but get take " +
        "session, reason and dry_run.\n")]
    public async Task Description_SaysWhatTheSchemaCannot(string sentence)
    {
        var description = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == "campaign_character").Description!;

        Assert.Contains(sentence, description, StringComparison.Ordinal);
        Assert.InRange(description.Length, 1, 2_048);
        Assert.EndsWith("\nExample: " + Tools.CampaignCharacterTools.Example, description, StringComparison.Ordinal);
    }

    [Fact]
    public void Description_Example_NamesTheCampaignLast()
    {
        // One order for every printed call (fix F1): {"action": …, …, "campaign": "<slug>"}.
        Assert.EndsWith(", \"campaign\": \"belmakor\"}", Tools.CampaignCharacterTools.Example, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Tools.CampaignCharacterTools.Example, "\"campaign\":"));
    }

    [Fact]
    public async Task ItemsSchema_Qty_SaysAHeldItemsFlagsChangeWithoutIt()
    {
        // Fix F1, C11 (F2: srd named too): for an item already held, an entry with equipped, attuned, notes or srd and no qty
        // changes only those (an unequipped ring is not a second ring); a new item's qty defaults to 1.
        var schema = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == "campaign_character").JsonSchema.GetProperty("properties").GetProperty("items");
        var qty = schema.GetProperty("items").GetProperty("properties").GetProperty("qty").GetProperty("description").GetString();

        Assert.Equal(
            "How many: positive adds, negative takes away; a holding that reaches 0 is removed. Default 1 for an item not held; for one held, " +
            "an entry with equipped, attuned, notes or srd and no qty changes only those.",
            qty);
        Assert.Equal("inventory: what to add (qty positive) or take away (qty negative), or a held item's equipped, attuned, notes or srd to change (no qty).",
            schema.GetProperty("description").GetString());
    }
}
