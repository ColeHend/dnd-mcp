using System.Text.Json.Nodes;
using Dapper;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using Xunit;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: the exit-criteria fights round-trip to the sheets through the Repository (contract §6.7, D6; FIX §2.3 and
/// §4.3 as amended by §16): every step is its own transaction that writes no change_log row (HP ticks stay out of history),
/// and <c>end</c> is exactly ONE logged batch (tool combat/end, filed under the fight's session) that writes the
/// combat-owned fields that changed, the consumed potion as current − used, the XP, the loot and the coins; undoing it
/// restores every sheet exactly and the fight says "write-back undone"; undoing the undo reapplies it ("applied again").
/// </summary>
public sealed class CombatFixtureWriteBackTests
{
    [Fact]
    public void FixtureA_StepsLogNothing_EndIsOneBatch_TheSheetsOfSection23()
    {
        using var w = CombatWorld.Belmakor();

        CombatScripts.FixtureA(w);

        // A32: the session start is the only batch since the sheets were written; the fight logged nothing.
        var afterSteps = w.ChangeRows();
        Assert.Equal(1, w.F.Count("SELECT count(DISTINCT batch_id) FROM change_log WHERE seq > (SELECT max(seq) FROM change_log WHERE tool = 'campaign_character/update')"));
        Assert.Empty(w.Dice());
        var end = CombatScripts.EndA(w);

        Assert.NotNull(end.BatchId);
        var rows = w.F.Log(end.BatchId);
        Assert.Equal(w.ChangeRows() - afterSteps, rows.Count);
        Assert.All(rows, r => Assert.Equal("character_sheet", r.TargetTable));
        Assert.All(rows, r => Assert.Equal(CombatService.EndTool, r.Tool));
        Assert.All(rows, r => Assert.Equal(w.F.Entity(w.Campaign, "session:4").Id, r.SessionId));
        var belmakor = w.SheetOf("character:belmakor");
        Assert.Equal((82, 0), (belmakor.Hp, belmakor.TempHp));
        Assert.Equal((1, 1), (belmakor.SpellSlots["1"].Used, belmakor.SpellSlots["5"].Used));
        Assert.Equal(1, belmakor.Resources["bladesong"].Used);
        Assert.Equal("set", belmakor.Resources["contingency"].State);
        Assert.Empty(belmakor.Conditions);
        Assert.Equal(("Circle of Power", 5, 98), (belmakor.Concentration!.Spell, belmakor.Concentration.Level, belmakor.Concentration.RemainingRounds));
        Assert.Equal("from The crypt (fixture), round 1", belmakor.Concentration.Note);
        var torch = w.SheetOf("character:torch");
        Assert.Equal(1, torch.Hp);
        Assert.Empty(torch.Conditions);
        Assert.True(torch.DeathSaves.IsReset);
        foreach (var handle in new[] { "character:vars", "character:aiden-ironstar", "character:ignis", "character:serif" })
        {
            Assert.DoesNotContain(rows, r => r.EntityId == w.Id(handle));
        }

        // Belmakor's 1st-level slot was spent before the fight: never rewritten.
        Assert.DoesNotContain(rows, r => r.FieldPath == "spell_slots.1");
        Assert.Contains(rows, r => r.FieldPath == "spell_slots.5");
        Assert.Contains(rows, r => r.FieldPath == "resources.bladesong");
        Assert.Equal(0, w.F.Count("SELECT count(*) FROM award"));
        Assert.Contains("14,400", end.Xp.Text, StringComparison.Ordinal);
        Assert.Contains("2,400 each for 6", end.Xp.Text, StringComparison.Ordinal);
        Assert.Empty(end.Proposals);
        Assert.Contains(end.Summary, l => l.Contains("No hit points tracked", StringComparison.Ordinal) && l.Contains("Vars Nocturne", StringComparison.Ordinal));

        // A-L6 / D10: the fight appended nothing to the session's live log.
        Assert.Equal("[]", w.F.Scalar<string>("SELECT live_log FROM session WHERE number = 4 AND campaign_id = @id", new { id = w.Campaign.Id }));
    }

    [Fact]
    public void FixtureB_EndIsOneBatch_TheSheetsHoldingsCoinsAndAwardsOfSection43()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var stepsDone = w.ChangeRows();

        var end = CombatScripts.EndB(w);

        Assert.NotNull(end.BatchId);
        Assert.Equal(0, w.Roller.Left);
        var rows = w.F.Log(end.BatchId);
        Assert.Equal(w.ChangeRows() - stepsDone, rows.Count);
        Assert.Equal(["award", "character_sheet", "currency_txn", "holding"], rows.Select(r => r.TargetTable).Distinct().Order(StringComparer.Ordinal));
        Assert.All(rows, r => Assert.Equal(w.F.Entity(w.Campaign, "session:13").Id, r.SessionId));
        var bjorn = w.SheetOf("character:bjorn-mountainfell");
        Assert.Equal((51, 1, 1, 36_400), (bjorn.Hp, bjorn.Resources["rage"].Used, bjorn.Exhaustion, bjorn.Xp));
        Assert.Empty(bjorn.Conditions);
        var monk = w.SheetOf("character:fishman-monk");
        Assert.Equal((7, 4, 36_400), (monk.Hp, monk.Resources["focus"].Used, monk.Xp));
        Assert.True(monk.DeathSaves.IsReset);
        var curse = Assert.Single(monk.Conditions);
        Assert.Equal(("cursed (Mucus Cloud)", "Aboleth", "until_removed", "from The dark station (fixture), round 1"), (curse.Name, curse.Source, curse.Duration, curse.Note));
        var slayer = w.SheetOf("character:dragon-slayer");
        Assert.Equal((56, 36_400), (slayer.Hp, slayer.Xp));

        Assert.Equal(1.0, w.F.Scalar<double>("SELECT quantity FROM holding WHERE name = 'Potion of Healing'"));
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM holding WHERE name = 'The sixth station''s ledger' AND holder_id = @party", new { party = w.Id("faction:the-party") }));
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM holding WHERE name = 'Potion of Water Breathing' AND srd_ref = '2024/magic-item/potion-of-water-breathing' AND holder_id = @bjorn",
            new { bjorn = w.Id("character:bjorn-mountainfell") }));
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM currency_txn WHERE gp = 120 AND note = 'The dark station (fixture)' AND holder_id = @party", new { party = w.Id("faction:the-party") }));
        Assert.Equal(3L, w.F.Count("SELECT count(*) FROM award WHERE kind = 'xp' AND amount = 2400 AND source = 'encounter: The dark station (fixture)'"));

        // FIX §4.3 (amended): the awards, the loot and the coins are session 13's, like the batch that made them.
        var session = w.Id("session:13");
        Assert.Equal(3L, w.F.Count("SELECT count(*) FROM award WHERE session_id = @session", new { session }));
        Assert.Equal(2L, w.F.Count("SELECT count(*) FROM holding WHERE acquired_session_id = @session", new { session }));
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM currency_txn WHERE session_id = @session", new { session }));
        Assert.Equal((7_200, 7_200, 2_400, 0), (end.Xp.Worth, end.Xp.Awarded, end.Xp.Each, end.Xp.Remainder));

        // The Nester's death is a proposal, never applied: its status stays as it was.
        var proposal = Assert.Single(end.Proposals);
        Assert.Equal("character:the-nester", proposal.EntityHandle);
        Assert.Contains("\"op\": \"status\"", proposal.Call, StringComparison.Ordinal);
        Assert.Contains("\"dry_run\": true", proposal.Call, StringComparison.Ordinal);
        Assert.Equal("alive", w.F.Entity(w.Campaign, "character:the-nester").Status);
        Assert.Contains(end.Reminders, r => r.Kind == CombatValues.ReminderKinds.Trait && r.Text.Contains("Eldritch Restoration", StringComparison.Ordinal));

        var encounter = w.Encounter(CombatScripts.StationName);
        Assert.Equal(("ended", end.BatchId, "The sixth station is clear."), (encounter.Status, encounter.WritebackBatchId, encounter.OutcomeMd));
        Assert.Equal(L.End, w.Log(CombatScripts.StationName)[^1].Kind);

        // B-L4: the proposal and the outcome are author-only; the party's board of the ended fight says neither.
        var board = w.Reader.Board(w.Campaign, Domain.Campaign.Perspective.Parse("party"), "last");
        Tests.CampaignRead.LeakAssert.Clean(board, ["Nester", "Eldritch", "Proposed", "dead", "sixth station", "ledger", "120"], "the party's board after the end");
    }

    [Fact]
    public void FixtureB_UndoRestoresEverySheetExactly_StateSaysUndone_RedoAppliesAgain()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var beforeEnd = w.F.Dump();
        var end = CombatScripts.EndB(w);
        var afterEnd = w.F.Dump();
        Assert.Equal(CombatScripts.StationName, w.Reader.State(w.Campaign, "last").Encounter!.Name);
        Assert.Equal(WritebackStatuses.Applied, w.Reader.State(w.Campaign, "last").Encounter!.WritebackStatus);
        var dice = w.Dice().Count;

        var undo = w.F.History.Undo(w.Campaign, end.BatchId!, WriteContext.Default);

        Assert.Equal(beforeEnd, w.F.Dump());
        Assert.Equal("ended", w.Encounter(CombatScripts.StationName).Status);
        Assert.Equal(WritebackStatuses.Undone, w.Reader.State(w.Campaign, "last").Encounter!.WritebackStatus);
        Assert.Equal(dice, w.Dice().Count);
        Assert.All(w.F.Log(undo.UndoBatchId), r => Assert.Equal(end.BatchId, r.UndoOf));

        w.F.History.Undo(w.Campaign, undo.UndoBatchId!, WriteContext.Default);

        Assert.Equal(afterEnd, w.F.Dump());
        Assert.Equal(WritebackStatuses.AppliedAgain, w.Reader.State(w.Campaign, "last").Encounter!.WritebackStatus);
    }

    [Fact]
    public void FixtureB_ALaterSheetHealConflicts_UndoIsRefused_TheHealStays()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var end = CombatScripts.EndB(w);
        var heal = w.Characters.Heal(w.Campaign, "fishman-monk", 10, WriteContext.Default);
        Assert.NotNull(heal.BatchId);
        Assert.Equal(17, w.SheetOf("character:fishman-monk").Hp);

        var refused = Assert.Throws<Domain.Core.DndInputException>(() => w.F.History.Undo(w.Campaign, end.BatchId!, WriteContext.Default));

        Assert.Contains(heal.BatchId!, refused.Message, StringComparison.Ordinal);
        Assert.Equal(17, w.SheetOf("character:fishman-monk").Hp);
    }

    [Fact]
    public void FixtureA_DryRunEnd_PlansTheWriteBack_KeepsNothing_TheFightStaysActive()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w);
        var dump = w.F.Dump();
        var log = w.Log(CombatScripts.CryptName).Count;

        var dry = CombatScripts.EndA(w, dryRun: true);

        Assert.True(dry.DryRun);
        Assert.Null(dry.BatchId);
        Assert.Contains(dry.Changes, c => c.Ref == "character:belmakor" && c.Field == "hp" && c.After == "82");
        Assert.Equal("active", dry.Encounter.Status);
        Assert.Equal(dump, w.F.Dump());
        Assert.Equal("active", w.Encounter(CombatScripts.CryptName).Status);
        Assert.Null(w.Encounter(CombatScripts.CryptName).WritebackBatchId);
        Assert.Equal(log, w.Log(CombatScripts.CryptName).Count);

        var real = CombatScripts.EndA(w);

        Assert.NotNull(real.BatchId);
        Assert.Equal(dry.Changes.Select(c => (c.Ref, c.Field, c.Key, c.After)), real.Changes.Select(c => (c.Ref, c.Field, c.Key, c.After)));
    }
}
