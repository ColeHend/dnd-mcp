using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignCombat;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The exit criterion's round trip back (FIX §4.4 U1-U3 and §2.3's undo, as amended by contract §16; D6): the end-of-combat
/// write-back is ONE logged batch, so <c>campaign_history undo</c> takes it back whole — every sheet row equal to its
/// pre-<c>end</c> self in every column but updated_at, the consumed potion back to 2, the created loot, coins and awards
/// gone — while the fight stays ended, its combat_log untouched and its dice rolled (undo never un-rolls); the undo is
/// itself a batch of rows that point at the write-back (<c>undo_of</c>) and hold nothing the fight's HP ticks did; undoing
/// the undo applies the write-back again. U2: a sheet edit after the fight is a logged batch, so undoing the write-back
/// under it is REFUSED naming that batch (never a silent reset of the monk's 17 HP to 59), and following the refusal's
/// own fix works. Each scenario writes, so each builds its own world.
/// </summary>
public sealed class CombatUndoScenarioTests
{
    private static readonly WriteContext Context = WriteContext.Default;

    [Fact]
    public void OnePiece_U1_UndoWriteBack_RestoresSheetsExactly()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var before = CombatPlay.SheetRows(w);
        var dumpBefore = w.F.Dump();
        Tick(w);
        var end = CombatScripts.EndB(w);
        var log = w.Log(CombatScripts.StationName).Count;
        var dice = w.Dice();
        Tick(w);

        var undo = w.F.History.Undo(w.Campaign, end.BatchId!, Context);

        Assert.NotNull(undo.UndoBatchId);
        var after = CombatPlay.SheetRows(w);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (handle, row) in before)
        {
            Assert.DoesNotContain(CombatPlay.ChangedColumns(row, after[handle]), c => c != "updated_at");
        }

        Assert.Equal(("85", "0", "[]", "34000", "0"), Columns(after["character:bjorn-mountainfell"], "hp", "exhaustion", "conditions", "xp", "temp_hp"));
        Assert.Equal(("59", "[]", "34000", """{"successes":0,"failures":0,"stable":false}""", "0"), Columns(after["character:fishman-monk"], "hp", "conditions", "xp", "death_saves", "exhaustion"));
        Assert.Equal(("68", "34000"), (CombatPlay.Text(after["character:dragon-slayer"], "hp"), CombatPlay.Text(after["character:dragon-slayer"], "xp")));
        Assert.Contains("\"rage\":{\"name\":\"Rage\",\"max\":4,\"used\":0", CombatPlay.Text(after["character:bjorn-mountainfell"], "resources"), StringComparison.Ordinal);
        Assert.Contains("\"focus\":{\"name\":\"Focus\",\"max\":8,\"used\":0", CombatPlay.Text(after["character:fishman-monk"], "resources"), StringComparison.Ordinal);
        Assert.Equal(dumpBefore, w.F.Dump());

        // The potion is back to 2; what the end created is hard-deleted.
        Assert.Equal(2.0, w.F.Scalar<double>("SELECT quantity FROM holding WHERE name = 'Potion of Healing'"));
        foreach (var loot in end.Loot)
        {
            Assert.Equal(0L, w.F.Count("SELECT count(*) FROM holding WHERE id = @id", new { id = loot.HoldingId }));
        }

        Assert.Equal(0L, w.F.Count("SELECT count(*) FROM currency_txn WHERE id = @id", new { id = end.Currency.Single().Id }));
        Assert.Equal(0L, w.F.Count("SELECT count(*) FROM award"));

        // The fight stays ended, says its write-back was undone, and keeps its log and its three dice.
        var fight = w.Encounter(CombatScripts.StationName);
        Assert.Equal((Domain.Campaign.CampaignValues.EncounterStatuses.Ended, end.BatchId), (fight.Status, fight.WritebackBatchId));
        Assert.Equal(WritebackStatuses.Undone, w.Reader.State(w.Campaign, EncounterResolver.Last).Encounter!.WritebackStatus);
        Assert.Equal(log, w.Log(CombatScripts.StationName).Count);
        Assert.Equal(dice.Select(d => d.Id), w.Dice().Select(d => d.Id));
        Assert.Equal(3, dice.Count);
    }

    [Fact]
    public void OnePiece_U2_ALaterSheetHealConflicts_TheUndoIsRefusedNamingIt_TheMonkStaysAt17()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var end = CombatScripts.EndB(w);
        Tick(w);
        var heal = w.Characters.Heal(w.Campaign, "fishman-monk", 10, Context);
        Assert.NotNull(heal.BatchId);
        Assert.Equal(17, w.SheetOf("character:fishman-monk").Hp);
        var dump = w.F.Dump();
        var changes = w.ChangeRows();

        var refused = Assert.Throws<DndInputException>(() => w.F.History.Undo(w.Campaign, end.BatchId!, Context));

        Assert.Contains(heal.BatchId!, refused.Message, StringComparison.Ordinal);
        Assert.Contains("Undo these first", refused.Message, StringComparison.Ordinal);
        Assert.Equal(17, w.SheetOf("character:fishman-monk").Hp);
        Assert.Equal(dump, w.F.Dump());
        Assert.Equal(changes, w.ChangeRows());
        Assert.Equal(WritebackStatuses.Applied, w.Reader.State(w.Campaign, EncounterResolver.Last).Encounter!.WritebackStatus);

        // The refusal's own fix: undo the heal first, then the write-back comes back whole.
        w.F.History.Undo(w.Campaign, heal.BatchId!, Context);
        w.F.History.Undo(w.Campaign, end.BatchId!, Context);
        Assert.Equal(59, w.SheetOf("character:fishman-monk").Hp);
    }

    [Fact]
    public void OnePiece_U3_TheUndoIsABatchOfRowsPointingAtTheWriteBack_NoTickRows_RedoAppliesSection43Again()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var end = CombatScripts.EndB(w);
        var endRows = w.F.Log(end.BatchId!);
        var afterEnd = w.F.Dump();
        Tick(w);

        var undo = w.F.History.Undo(w.Campaign, end.BatchId!, Context);

        var undoRows = w.F.Log(undo.UndoBatchId!);
        Assert.Equal(endRows.Count, undoRows.Count);
        Assert.All(undoRows, r => Assert.Equal(end.BatchId, r.UndoOf));
        Assert.All(undoRows, r => Assert.Contains(r.TargetTable, new[] { "character_sheet", "holding", "currency_txn", "award" }));
        Assert.Equal(
            endRows.Select(r => (r.TargetTable, r.TargetId, r.FieldPath)).Order(),
            undoRows.Select(r => (r.TargetTable, r.TargetId, r.FieldPath)).Order());

        Tick(w);
        var redo = w.F.History.Undo(w.Campaign, undo.UndoBatchId!, Context);

        Assert.All(w.F.Log(redo.UndoBatchId!), r => Assert.Equal(undo.UndoBatchId, r.UndoOf));
        Assert.Equal(afterEnd, w.F.Dump());
        Assert.Equal(WritebackStatuses.AppliedAgain, w.Reader.State(w.Campaign, EncounterResolver.Last).Encounter!.WritebackStatus);
        var bjorn = w.SheetOf("character:bjorn-mountainfell");
        Assert.Equal((51, 1, 1, 36_400), (bjorn.Hp, bjorn.Resources["rage"].Used, bjorn.Exhaustion, bjorn.Xp));
        var monk = w.SheetOf("character:fishman-monk");
        Assert.Equal((7, 4, 36_400, "cursed (Mucus Cloud)"), (monk.Hp, monk.Resources["focus"].Used, monk.Xp, monk.Conditions.Single().Name));
        Assert.Equal((56, 36_400), (w.SheetOf("character:dragon-slayer").Hp, w.SheetOf("character:dragon-slayer").Xp));
        Assert.Equal(1.0, w.F.Scalar<double>("SELECT quantity FROM holding WHERE name = 'Potion of Healing'"));
        Assert.Equal(3L, w.F.Count("SELECT count(*) FROM award WHERE amount = 2400"));
    }

    [Fact]
    public void Belmakor_A33_UndoRestoresTheBeforeColumn_TheFightStaysEnded_ThePreFightSlotStaysSpent()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w);
        var before = CombatPlay.SheetRows(w);
        var dump = w.F.Dump();
        Tick(w);
        var end = CombatScripts.EndA(w);
        Tick(w);

        w.F.History.Undo(w.Campaign, end.BatchId!, Context);

        var after = CombatPlay.SheetRows(w);
        foreach (var (handle, row) in before)
        {
            Assert.DoesNotContain(CombatPlay.ChangedColumns(row, after[handle]), c => c != "updated_at");
        }

        Assert.Equal(dump, w.F.Dump());
        var belmakor = w.SheetOf("character:belmakor");
        Assert.Equal((110, 7, 0, 1, 0), (belmakor.Hp, belmakor.TempHp, belmakor.SpellSlots["5"].Used, belmakor.SpellSlots["1"].Used, belmakor.Resources["bladesong"].Used));
        Assert.Null(belmakor.Concentration);
        Assert.Equal(74, w.SheetOf("character:torch").Hp);
        Assert.Equal(Domain.Campaign.CampaignValues.EncounterStatuses.Ended, w.Encounter(CombatScripts.CryptName).Status);
        Assert.Equal(WritebackStatuses.Undone, w.Reader.State(w.Campaign, EncounterResolver.Last).Encounter!.WritebackStatus);
    }

    private static void Tick(CombatWorld w) => w.F.Db.Time.Advance(TimeSpan.FromMinutes(1));

    private static (string?, string?, string?, string?, string?) Columns(IReadOnlyDictionary<string, object?> row, string a, string b, string c, string d, string e) =>
        (CombatPlay.Text(row, a), CombatPlay.Text(row, b), CombatPlay.Text(row, c), CombatPlay.Text(row, d), CombatPlay.Text(row, e));
}
