using System.Text.RegularExpressions;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The exit criterion's "round-trips" through <c>campaign_history</c> (FIX §4.4 U1-U3 and §2.3's undo, contract §2 D6):
/// undoing a fight's write-back restores every sheet-side row exactly (every column but updated_at: hp, temp HP, slots,
/// resources, conditions, concentration, exhaustion, XP; the potion's quantity; the loot, coins and awards hard-deleted),
/// the sheets read through <c>campaign_get</c> exactly as before the fight, and the fight says its write-back was undone;
/// the undo is its own batch (undo_of the write-back, touching only sheet-side tables) and redoing it reapplies exactly the
/// "After" column; and a sheet edit made after the fight makes the undo REFUSE, naming that batch, so the edit is never
/// silently overwritten (U2). Each test plays its fixture in a world of its own, through the tools, because each writes.
/// What breaks if these fail: undoing a fight loses or invents sheet state, or overwrites a later edit.
/// </summary>
public sealed class ScenarioCombatUndoTests
{
    private static readonly Regex NewBatch = new("as a new batch `([0-9a-f-]{36})`", RegexOptions.CultureInvariant);

    /// <summary>A fixture's fight through the tools, session started and the steps sent (B's faces queued per step), not yet ended.</summary>
    private static async Task FightAsync(ScenarioCombatServer world, string session, IEnumerable<(string Step, string Arguments, int[] Faces)> steps)
    {
        await world.Call("campaign_session", session);
        foreach (var (_, arguments, faces) in steps)
        {
            world.Dice.Enqueue(faces);
            await world.Combat(arguments);
        }

        Assert.Equal(0, world.Dice.Remaining);
    }

    private static string Undo(string batch, string campaign) => $$"""{"action": "undo", "batch_id": "{{batch}}", "campaign": "{{campaign}}"}""";

    // The one campaign_history call a result prints (the end's undo, the undo's redo), as its arguments: sent verbatim.
    private static string PrintedHistoryCall(string text) =>
        ScenarioCombatText.Arguments(Assert.Single(ScenarioCombatText.PrintedCalls(text), c => c.StartsWith("campaign_history {\"action\": \"undo\"", StringComparison.Ordinal)));

    private static string UndoBatch(string undoResult)
    {
        var match = NewBatch.Match(undoResult);
        Assert.True(match.Success, undoResult);
        return match.Groups[1].Value;
    }

    private const string OnePieceSheets =
        """{"campaign": "one-piece", "refs": ["character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk"], "include": ["sheet"]}""";

    [Fact]
    public async Task OnePiece_U1_U3_UndoRestoresEverySheetRowExactly_ItsOwnBatchOfSheetTablesOnly_RedoReappliesTheAfterColumn()
    {
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        var sheetsBefore = await world.Call("campaign_get", OnePieceSheets);
        var tablesBefore = world.Store.SheetTables("one-piece", withUpdatedAt: false);
        await FightAsync(world, ScenarioCombatFixtures.SessionB, ScenarioCombatFixtures.StepsB);
        var encounter = world.Store.EncounterId("one-piece", ScenarioCombatFixtures.StationName);
        var log = world.Store.CombatLog(encounter).Count;

        var ended = await world.Combat(ScenarioCombatFixtures.EndB);
        var end = ScenarioCombatText.Batch(ended);
        var sheetsAfter = await world.Call("campaign_get", OnePieceSheets);
        var tablesAfter = world.Store.SheetTables("one-piece", withUpdatedAt: false);
        var rowsAfterEnd = world.Store.ChangeRows();
        Assert.NotEqual(tablesBefore, tablesAfter);

        // U1: the undo the end printed, sent verbatim, restores every sheet-side row byte for byte (but updated_at): sheets,
        // the potion back to 2, the ledger, the water-breathing potion, the coins and the three awards gone; the sheets read
        // as before the fight.
        Assert.Equal(Undo(end, "one-piece"), PrintedHistoryCall(ended));
        var undo = await world.Call("campaign_history", PrintedHistoryCall(ended));
        var undoBatch = UndoBatch(undo);
        Assert.StartsWith($"# Undo of batch {end}\n\nReversed the batch's 17 logged changes as a new batch `{undoBatch}`.\n" +
                          $"To put them back (redo): campaign_history {{\"action\": \"undo\", \"batch_id\": \"{undoBatch}\", \"campaign\": \"one-piece\"}}.\n", undo,
            StringComparison.Ordinal);
        Assert.Equal(tablesBefore, world.Store.SheetTables("one-piece", withUpdatedAt: false));
        Assert.Equal(sheetsBefore, await world.Call("campaign_get", OnePieceSheets));
        Assert.Contains("- **Inventory:** Potion of Healing ×2 (`2024/equipment/potion-of-healing`)\n", sheetsBefore, StringComparison.Ordinal);

        // The fight stays ended and says its write-back was undone; its log and its three dice rolls are untouched.
        Assert.Contains($"Its write-back (batch `{end}`) was undone: the sheets are as they were before the fight.\n",
            await world.Combat("""{"action": "state", "encounter": "last"}"""), StringComparison.Ordinal);
        Assert.Equal(("ended", end), world.Store.Encounter(encounter));
        Assert.Equal(log + 1, world.Store.CombatLog(encounter).Count);
        Assert.Equal(3, world.Store.Dice("one-piece").Count(d => d.EncounterId == encounter));

        // U3: the undo is a batch of its own in change_log, undo_of the write-back, over the sheet-side tables only.
        Assert.Equal(end, world.Store.UndoOf(undoBatch));
        Assert.Equal(17, world.Store.BatchRows(undoBatch));
        Assert.Equal(["award", "character_sheet", "currency_txn", "holding"], world.Store.BatchTables(undoBatch));
        Assert.Equal(rowsAfterEnd + 17, world.Store.ChangeRows());
        Assert.Contains($"· campaign_history/undo · S13\nbatch `{undoBatch}` · by claude · undoes `{end}`\n",
            await world.Call("campaign_history", $$"""{"action": "batch", "batch_id": "{{undoBatch}}", "campaign": "one-piece"}"""), StringComparison.Ordinal);

        // Redo (the undo's printed call: undo the undo) reapplies exactly the After column.
        var redo = await world.Call("campaign_history", PrintedHistoryCall(undo));
        Assert.StartsWith($"# Redo (undo of an undo) of batch {undoBatch}\n", redo, StringComparison.Ordinal);
        Assert.Equal(tablesAfter, world.Store.SheetTables("one-piece", withUpdatedAt: false));
        Assert.Equal(sheetsAfter, await world.Call("campaign_get", OnePieceSheets));
        Assert.Contains($"Its write-back (batch `{end}`) was undone and then applied again (a redo).\n",
            await world.Combat("""{"action": "state", "encounter": "last"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnePiece_U2_ASheetEditAfterTheFight_MakesTheUndoRefuse_NamingItsBatch_TheEditStands()
    {
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        await FightAsync(world, ScenarioCombatFixtures.SessionB, ScenarioCombatFixtures.StepsB);
        var end = ScenarioCombatText.Batch(await world.Combat(ScenarioCombatFixtures.EndB));

        var heal = await world.Call("campaign_character", """{"action": "heal", "campaign": "one-piece", "character": "fishman-monk", "amount": 10}""");
        var healBatch = ScenarioCombatText.Batch(heal);
        Assert.Contains("- hp: 7 → 17\n", heal, StringComparison.Ordinal);
        var tables = world.Store.SheetTables("one-piece");
        var rows = world.Store.ChangeRows();

        var refusal = await world.Fail("campaign_history", Undo(end, "one-piece"));

        Assert.StartsWith(
            $"An error occurred invoking 'campaign_history': Batch {end} cannot be undone: later changes build on what it changed. Undo these first, newest first, " +
            $"then undo {end} again:\n- {healBatch} (",
            refusal, StringComparison.Ordinal);
        Assert.EndsWith(", campaign_character/heal): changes a character sheet row hp", refusal, StringComparison.Ordinal);

        // Never silently back to 59: the heal stands, and nothing else moved.
        Assert.Equal("17", world.Store.SheetColumn("one-piece", "fishman-monk", "hp"));
        Assert.Equal(tables, world.Store.SheetTables("one-piece"));
        Assert.Equal(rows, world.Store.ChangeRows());
        Assert.Contains("HP 17/59", await world.Call("campaign_character", """{"action": "get", "campaign": "one-piece", "character": "character:fishman-monk"}"""),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Belmakor_23_ThePrintedUndoOfTheWriteBack_RestoresTheBeforeColumn_TheFirstSlotSpentBeforeStays_ThePrintedRedoReappliesIt()
    {
        await using var world = await ScenarioCombatServer.BelmakorAsync();
        const string Sheets = """{"campaign": "belmakor", "refs": ["character:belmakor", "character:torch"], "include": ["sheet"]}""";
        var sheetsBefore = await world.Call("campaign_get", Sheets);
        var tablesBefore = world.Store.SheetTables("belmakor", withUpdatedAt: false);
        await FightAsync(world, ScenarioCombatFixtures.SessionA, ScenarioCombatFixtures.StepsA.Select(s => (s.Step, s.Arguments, Array.Empty<int>())));

        var ended = await world.Combat(ScenarioCombatFixtures.EndA);
        var end = ScenarioCombatText.Batch(ended);
        var sheetsAfter = await world.Call("campaign_get", Sheets);
        var tablesAfter = world.Store.SheetTables("belmakor", withUpdatedAt: false);

        // The undo the end printed, sent verbatim.
        Assert.Equal(Undo(end, "belmakor"), PrintedHistoryCall(ended));
        var undo = await world.Call("campaign_history", PrintedHistoryCall(ended));
        var undoBatch = UndoBatch(undo);

        Assert.StartsWith($"# Undo of batch {end}\n\nReversed the batch's 6 logged changes as a new batch `{undoBatch}`.\n", undo, StringComparison.Ordinal);
        Assert.Equal(end, world.Store.UndoOf(undoBatch));
        Assert.Equal(["character_sheet"], world.Store.BatchTables(undoBatch));
        Assert.Contains($"Its write-back (batch `{end}`) was undone: the sheets are as they were before the fight.\n",
            await world.Combat("""{"action": "state", "encounter": "last"}"""), StringComparison.Ordinal);
        Assert.Equal(tablesBefore, world.Store.SheetTables("belmakor", withUpdatedAt: false));
        Assert.Equal(sheetsBefore, await world.Call("campaign_get", Sheets));
        Assert.Contains("HP 110/110 (+7 temp) · AC 17", sheetsBefore, StringComparison.Ordinal);
        Assert.Contains("- **Spell slots:** 1st 3/4 · 2nd 3/3 · 3rd 3/3 · 4th 3/3 · 5th 2/2 · 6th 1/1\n", sheetsBefore, StringComparison.Ordinal);
        Assert.Equal("{\"max\":4,\"used\":1}", System.Text.Json.Nodes.JsonNode.Parse(world.Store.SheetColumn("belmakor", "belmakor", "spell_slots")!)!["1"]!.ToJsonString());
        Assert.Equal("ended", world.Store.Encounter(world.Store.EncounterId("belmakor", ScenarioCombatFixtures.CryptName)).Status);
        Assert.Empty(world.Store.Dice("belmakor"));

        // Redo: the undo's printed call (undo the undo), verbatim.
        Assert.Equal(Undo(undoBatch, "belmakor"), PrintedHistoryCall(undo));
        var redo = await world.Call("campaign_history", PrintedHistoryCall(undo));
        Assert.StartsWith($"# Redo (undo of an undo) of batch {undoBatch}\n", redo, StringComparison.Ordinal);
        Assert.Equal(tablesAfter, world.Store.SheetTables("belmakor", withUpdatedAt: false));
        Assert.Equal(sheetsAfter, await world.Call("campaign_get", Sheets));
        Assert.Contains($"Its write-back (batch `{end}`) was undone and then applied again (a redo).\n",
            await world.Combat("""{"action": "state", "encounter": "last"}"""), StringComparison.Ordinal);
    }
}
