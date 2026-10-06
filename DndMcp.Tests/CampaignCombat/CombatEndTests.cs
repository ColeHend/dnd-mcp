using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using Xunit;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: <c>end</c> (contract §6.7, D6) writes back only what the fight owns and only when the sheet still holds what
/// the combatant started from: drift (a field changed meanwhile, a sheet or a consumed holding gone) refuses the end
/// naming each field and both values, unless <c>force</c>; a consumed item is written as the holding's CURRENT quantity −
/// used; a discard, or a planned or paused fight, ends with nothing written (the call Z's undo guard prints works for
/// planned, paused and active fights alike); a fight ends once (a second end says how to take the write-back back, or
/// that it was undone and how to redo it); an end raced by another call between its pre-read and its transaction never
/// acts on what it did not plan for; a fight that changed nothing ends with no batch; XP is split by the rules; loot and
/// coins go to a character or the party; a persisted condition's source is the party-safe name.
/// </summary>
public sealed partial class CombatEndTests
{
    private const string Fight = "Endings";

    private static CombatWorld HeroInAFight(string? heroJson = null)
    {
        var w = CombatWorld.Dm();
        try
        {
            w.Sheet("character:hero", heroJson ?? CombatLifecycleTests.HeroJson);
            w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    [Fact]
    public void Drift_TheSheetChangedMeanwhile_RefusedNamingBothValues_ForceWritesOverIt()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 10 });
        w.Sheet("character:hero", """{ "hp": 30 }""");
        var dump = w.F.Dump();

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, new EndRequest()));

        Assert.Contains("Hero Prime hp: 44 when it joined, 30 now", refused.Message, StringComparison.Ordinal);
        Assert.Contains("force: true", refused.Message, StringComparison.Ordinal);
        Assert.Equal(dump, w.F.Dump());
        Assert.Equal(ES.Active, w.Encounter(Fight).Status);

        var forced = w.Combat.End(w.Campaign, null, new EndRequest { Force = true });

        Assert.Equal(34, w.SheetOf("character:hero").Hp);
        var item = Assert.Single(forced.Overwritten);
        Assert.Equal(("hp", "44", "30"), (item.Field, item.WhenAdded, item.Now));
    }

    [Fact]
    public void MissingSheet_IsDrift_ForceSkipsThatCombatant()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 10 });
        w.F.Query<int>("DELETE FROM character_sheet WHERE entity_id = @id RETURNING 1", new { id = w.Id("character:hero") });

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, new EndRequest()));
        Assert.Contains("Hero Prime sheet: existed when it joined, no longer exists now", refused.Message, StringComparison.Ordinal);

        var forced = w.Combat.End(w.Campaign, null, new EndRequest { Force = true });

        Assert.True(forced.NothingChanged);
        Assert.Null(forced.BatchId);
        Assert.Contains(forced.Summary, l => l.Contains("the sheet no longer exists", StringComparison.Ordinal));
        Assert.Equal(ES.Ended, w.Encounter(Fight).Status);
    }

    [Fact]
    public void ConsumedPotion_WrittenAsTheCurrentQuantityMinusUsed_NeverDeleted()
    {
        using var w = HeroInAFight();
        w.Characters.Inventory(w.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = 1 }], WriteContext.Default);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 20 });
        w.Roller.Push(2, 3);
        w.Combat.Heal(w.Campaign, null, new HealOp(["hero"]) { Dice = "2d4+2", Item = "Potion of Healing" });
        w.Characters.Inventory(w.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = 2 }], WriteContext.Default);

        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        var item = Assert.Single(end.Items);
        Assert.Equal((3.0, 2.0, 1), (item.Before, item.After, item.Used));
        Assert.Equal(2.0, w.F.Scalar<double>("SELECT quantity FROM holding WHERE name = 'Potion of Healing'"));
    }

    [Fact]
    public void ConsumedPotionThatIsGone_IsDrift_ForceSkipsIt_ALastPotionIsWrittenAs0()
    {
        using var w = HeroInAFight();
        w.Characters.Inventory(w.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = 1 }], WriteContext.Default);
        w.Roller.Push(1, 1);
        w.Combat.Heal(w.Campaign, null, new HealOp(["hero"]) { Dice = "2d4+2", Item = "Potion of Healing" });
        w.Characters.Inventory(w.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = -1 }], WriteContext.Default);

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, new EndRequest()));
        Assert.Contains("Hero Prime holding Potion of Healing: used 1 in the fight; the holding no longer exists", refused.Message, StringComparison.Ordinal);
        var forced = w.Combat.End(w.Campaign, null, new EndRequest { Force = true });
        Assert.Empty(forced.Items);

        using var x = HeroInAFight();
        x.Characters.Inventory(x.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = 1 }], WriteContext.Default);
        x.Roller.Push(1, 1);
        x.Combat.Heal(x.Campaign, null, new HealOp(["hero"]) { Dice = "2d4+2", Item = "Potion of Healing" });
        x.Combat.End(x.Campaign, null, new EndRequest());
        Assert.Equal(0.0, x.F.Scalar<double>("SELECT quantity FROM holding WHERE name = 'Potion of Healing'"));
    }

    [Fact]
    public void Discard_EndsWritingNothing_TheSheetsKeepTheirValues()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 10 });
        var rows = w.ChangeRows();

        var end = w.Combat.End(w.Campaign, null, new EndRequest { Discard = true, Outcome = "Never happened." });

        Assert.True(end.Discarded);
        Assert.Null(end.BatchId);
        Assert.Equal(rows, w.ChangeRows());
        Assert.Equal(44, w.SheetOf("character:hero").Hp);
        var encounter = w.Encounter(Fight);
        Assert.Equal((ES.Ended, (string?)null, "Never happened."), (encounter.Status, encounter.WritebackBatchId, encounter.OutcomeMd));
        Assert.Contains("\"discarded\":true", w.Log(Fight)[^1].Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// R01/R03: a fight with a row nobody can read (written by another program, or a damaged page) is refused by every
    /// step and read with the store message, which names the combatant, the fight and the campaign and prints the way out;
    /// that way out, a discard, reads no combatant, so it ends the fight, writing nothing, and the next fight can start.
    /// </summary>
    [Theory]
    [InlineData("UPDATE combatant SET conditions = 'garbage' WHERE name = 'Hero Prime'", "Hero Prime")]
    [InlineData("UPDATE combatant SET sheet_snapshot = '[]' WHERE name = 'Hero Prime'", "Hero Prime")]
    [InlineData("UPDATE combatant SET statblock = 'garbage' WHERE name = 'Ogre'", "Ogre")]
    [InlineData("UPDATE combatant SET legendary = '[]' WHERE name = 'Ogre'", "Ogre")]
    [InlineData("UPDATE combatant SET death_saves = '[]' WHERE name = 'Hero Prime'", "Hero Prime")]
    [InlineData("UPDATE combatant SET initiative = 9e999 WHERE name = 'Ogre'", "Ogre")]
    [InlineData("UPDATE combatant SET side = 'martian' WHERE name = 'Ogre'", "Ogre")]
    [InlineData("UPDATE encounter SET round = -1", null)]
    [InlineData("UPDATE encounter SET ruleset = '1999'", null)]
    public void Discard_AFightWithARowNobodyCanRead_TheRefusalsPrintedCallEndsIt_WritingNothing(string damage, string? combatant)
    {
        using var w = HeroInAFight();
        w.F.Db.WriteBehindTheServer(damage);
        var rows = w.ChangeRows();

        var refused = Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.State(w.Campaign));
        Assert.Throws<CampaignStoreUnavailableException>(() => w.Combat.End(w.Campaign, null, new EndRequest { Force = true }));

        Assert.StartsWith(
            combatant is null ? "The fight \"Endings\" in campaign sea cannot be read" : $"Combatant \"{combatant}\" of the fight \"Endings\" in campaign sea cannot be read",
            refused.Message, StringComparison.Ordinal);
        var call = JsonNode.Parse(Regex.Match(refused.Message, @"with combat (\{[^}]*\});").Groups[1].Value)!.AsObject();
        Assert.Equal(["action", "encounter", "discard", "campaign"], call.Select(p => p.Key));
        var end = w.Combat.End(w.Campaign, call["encounter"]!.GetValue<string>(), new EndRequest { Discard = call["discard"]!.GetValue<bool>() });

        Assert.True(end.Discarded);
        Assert.Equal(ES.Ended, w.Encounter(Fight).Status);
        Assert.Equal(rows, w.ChangeRows());
        Assert.Equal(44, w.SheetOf("character:hero").Hp);
        Assert.True(w.Combat.Start(w.Campaign, new StartRequest { Name = "Next", AddParty = false }).Created);
    }

    [Fact]
    public void Discard_AnUnreadablePlannedFight_EndsWithoutDiscardToo_AnEndedOnesRefusalOffersOnlyABackup()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Prepare(w.Campaign, new PrepareRequest(Fight) { Combatants = [CombatWorld.Monster("2024", "ogre")] });
        w.F.Db.WriteBehindTheServer("UPDATE combatant SET conditions = 'garbage'");

        var end = w.Combat.End(w.Campaign, Fight, new EndRequest());
        var refused = Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.State(w.Campaign, EncounterResolver.Last));

        Assert.True(end.Discarded);
        Assert.EndsWith("the file is damaged; restore a backup from the backups directory beside it.", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("discard", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ES.Planned)]
    [InlineData(ES.Paused)]
    public void PlannedOrPausedFight_EndsAsADiscard_LootRefused(string status)
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Prepare(w.Campaign, new PrepareRequest(Fight) { Combatants = [new CombatantRequest { Character = "character:hero" }] });
        w.F.Query<int>("UPDATE encounter SET status = @status RETURNING 1", new { status });

        Assert.Contains("never ran here", Assert.Throws<DndInputException>(() =>
            w.Combat.End(w.Campaign, Fight, new EndRequest { Loot = [new LootRequest("Gold idol")] })).Message, StringComparison.Ordinal);
        var end = w.Combat.End(w.Campaign, Fight, new EndRequest());

        Assert.True(end.Discarded);
        Assert.Null(end.BatchId);
        Assert.Equal(ES.Ended, w.Encounter(Fight).Status);
        Assert.StartsWith($"\"{Fight}\" was {status} and never ran here", end.Summary[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SecondEnd_IsRefused_NamingTheBatchToUndo()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 10 });
        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, "last", new EndRequest()));

        Assert.Contains("already ended", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"\"batch_id\": \"{end.BatchId}\"", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondEnd_AfterTheWriteBackWasUndone_SaysTheFightStaysEnded_TheRedoUndoesTheUndo()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 10 });
        var end = w.Combat.End(w.Campaign, null, new EndRequest());
        var undo = w.F.History.Undo(w.Campaign, end.BatchId!, WriteContext.Default);

        var undone = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, "last", new EndRequest())).Message;

        Assert.Contains("already ended", undone, StringComparison.Ordinal);
        Assert.Contains("Its write-back was undone, and the fight stays ended", undone, StringComparison.Ordinal);
        Assert.Contains($"campaign_history {{\"action\": \"undo\", \"batch_id\": \"{undo.UndoBatchId}\", \"campaign\": \"sea\"}}", undone, StringComparison.Ordinal);
        Assert.DoesNotContain(end.BatchId!, undone, StringComparison.Ordinal);
        Assert.DoesNotContain("undo batch", undone, StringComparison.Ordinal);
        Assert.Equal(ES.Ended, w.Encounter(Fight).Status);

        // The printed redo works; after it, a second end names the redo as the batch to undo (the only one that can be).
        var redo = w.F.History.Undo(w.Campaign, undo.UndoBatchId!, WriteContext.Default);
        Assert.Equal(34, w.SheetOf("character:hero").Hp);

        var again = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, "last", new EndRequest())).Message;

        Assert.Contains($"To take its write-back back, undo batch {redo.UndoBatchId}: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{redo.UndoBatchId}\"", again,
            StringComparison.Ordinal);
    }

    [Fact]
    public void End_TheFightEndedAndAnotherStartedAfterItsPreRead_SaysItAlreadyEnded_TheNewFightIsUntouched()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 10 });
        var other = new CombatService(w.Database, w.Roller);
        string? dump = null;
        w.Combat.AfterEndPreRead = () =>
        {
            other.End(w.Campaign, null, new EndRequest());
            other.Start(w.Campaign, new StartRequest { Name = "Next fight" });
            dump = w.F.Dump();
        };

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, new EndRequest()));

        Assert.StartsWith($"\"{Fight}\" already ended (round 0); a fight ends once.", refused.Message, StringComparison.Ordinal);
        Assert.Equal(dump, w.F.Dump());
        Assert.Equal(ES.Active, w.Encounter("Next fight").Status);
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM combat_log WHERE kind = 'end'"));
        Assert.Equal(34, w.SheetOf("character:hero").Hp);
    }

    [Fact]
    public void End_APlannedFightStartedAfterItsPreRead_IsRefused_SendTheEndAgain()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Prepare(w.Campaign, new PrepareRequest(Fight) { Combatants = [new CombatantRequest { Character = "character:hero" }] });
        var other = new CombatService(w.Database, w.Roller);
        w.Combat.AfterEndPreRead = () => other.Start(w.Campaign, new StartRequest { Encounter = Fight, AddParty = false });

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, Fight, new EndRequest()));

        Assert.Equal($"\"{Fight}\" changed while this end was being prepared (another call reached it first); nothing was changed: send the end again.", refused.Message);
        Assert.Equal(ES.Active, w.Encounter(Fight).Status);
        w.Combat.AfterEndPreRead = null;
        Assert.False(w.Combat.End(w.Campaign, Fight, new EndRequest()).Discarded);
    }

    [Fact]
    public void End_TheFightsSessionChangedAfterItsPreRead_IsRefused_SendTheEndAgain()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 10 });
        w.StartSession(1);
        w.Reload();
        var session = w.Id("session:1");
        w.Combat.AfterEndPreRead = () => w.F.Query<int>("UPDATE encounter SET session_id = @session RETURNING 1", new { session });

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, new EndRequest()));

        Assert.EndsWith("send the end again.", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ES.Active, w.Encounter(Fight).Status);
        Assert.Equal(44, w.SheetOf("character:hero").Hp);
    }

    [Fact]
    public void PersistedCondition_SourceIsThePartySafeName_NeverATypedName()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Station (probe)", Combatants = [CombatWorld.Monster("2024", "aboleth", name: "The Nester", hp: HpChoice.Avg)] });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["fishman-monk"]) { Add = ["cursed (Mucus Cloud)"], Source = "the-nester", Duration = "until removed" });

        w.Combat.End(w.Campaign, null, new EndRequest { Xp = 0 });

        var curse = Assert.Single(w.SheetOf("character:fishman-monk").Conditions);
        Assert.Equal(("cursed (Mucus Cloud)", "Aboleth"), (curse.Name, curse.Source));
        Assert.DoesNotContain("Nester", w.F.Scalar<string>("SELECT conditions FROM character_sheet WHERE entity_id = @id", new { id = w.Id("character:fishman-monk") }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void FightThatChangedNothing_EndsWithNoBatch()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, AddParty = false, Combatants = [CombatWorld.Monster("2024", "ogre")] });
        var rows = w.ChangeRows();

        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        Assert.True(end.NothingChanged);
        Assert.Null(end.BatchId);
        Assert.Equal(rows, w.ChangeRows());
        Assert.Equal(ES.Ended, w.Encounter(Fight).Status);
        Assert.Null(w.Encounter(Fight).WritebackBatchId);
        Assert.Equal(L.End, w.Log(Fight)[^1].Kind);
    }

    [Theory]
    [InlineData(ES.Planned)]
    [InlineData(ES.Paused)]
    [InlineData(ES.Active)]
    public void UndoGuard_ThePrintedEndCallWorks_ThenTheUndoGoesThrough(string status)
    {
        using var w = CombatWorld.Dm();
        var created = w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Prepare(w.Campaign, new PrepareRequest(Fight) { Combatants = [new CombatantRequest { Character = "character:hero" }] });
        if (status == ES.Active)
        {
            w.Combat.Start(w.Campaign, new StartRequest { Encounter = Fight, AddParty = false });
        }
        else if (status == ES.Paused)
        {
            w.F.Query<int>("UPDATE encounter SET status = 'paused' RETURNING 1");
        }

        var refused = Assert.Throws<DndInputException>(() => w.F.History.Undo(w.Campaign, created.BatchId!, WriteContext.Default));
        var call = JsonNode.Parse(PrintedCall().Match(refused.Message).Groups["json"].Value)!;
        Assert.Equal(("end", "sea", Fight, true), (call["action"]!.GetValue<string>(), call["campaign"]!.GetValue<string>(), call["encounter"]!.GetValue<string>(), call["discard"]!.GetValue<bool>()));

        w.Combat.End(w.Campaign, call["encounter"]!.GetValue<string>(), new EndRequest { Discard = call["discard"]!.GetValue<bool>() });
        w.F.History.Undo(w.Campaign, created.BatchId!, WriteContext.Default);

        using var connection = w.Open();
        Assert.Null(CharacterSheetStore.Read(connection, w.Id("character:hero")));
    }

    [GeneratedRegex(@"combat (?<json>\{[^}]*\})")]
    private static partial Regex PrintedCall();

    [Fact]
    public void Xp_GivenIsSplitFloor_TheRemainderSaid_MilestoneSheetsGetTheAwardOnly()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", """{ "classes": [{ "class": "fighter", "level": 5 }], "abilities": { "con": 14 }, "max_hp": 44, "xp": 13960 }""");
        w.Sheet("character:sidekick", CombatLifecycleTests.SidekickJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight });

        var end = w.Combat.End(w.Campaign, null, new EndRequest { Xp = 101 });

        Assert.Equal((101, 50, 1, 2), (end.Xp.Awarded, end.Xp.Each, end.Xp.Remainder, end.Xp.Recipients));
        Assert.Contains("1 XP undistributed", end.Xp.Text, StringComparison.Ordinal);
        Assert.Equal(14_010, w.SheetOf("character:hero").Xp);
        Assert.Null(w.SheetOf("character:sidekick").Xp);
        Assert.Equal(2L, w.F.Count("SELECT count(*) FROM award WHERE amount = 50"));
        var levelUp = Assert.Single(end.Reminders, r => r.Kind == CombatValues.ReminderKinds.LevelUp);
        Assert.Contains("level 6", levelUp.Text, StringComparison.Ordinal);
        Assert.Equal("campaign_character {\"action\": \"level_up\", \"character\": \"character:hero\"}", levelUp.Call);
    }

    [Fact]
    public void Xp_DryRun_SaysItWouldAward_WritesNothing_TheRealEndAwards()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", """{ "classes": [{ "class": "fighter", "level": 5 }], "abilities": { "con": 14 }, "max_hp": 44, "xp": 13960 }""");
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight });
        var dump = w.F.Dump();

        var preview = w.Combat.End(w.Campaign, null, new EndRequest { Xp = 100, DryRun = true });

        Assert.Equal("Would award 100 XP: 100 each to Hero Prime.", preview.Xp.Text);
        Assert.Contains("Would award 100 XP: 100 each to Hero Prime.", preview.Summary);
        Assert.DoesNotContain(preview.Summary, l => l.Contains("Awarded", StringComparison.Ordinal));
        Assert.Contains("would reach level 6", Assert.Single(preview.Reminders, r => r.Kind == CombatValues.ReminderKinds.LevelUp).Text,
            StringComparison.Ordinal);
        Assert.Equal(dump, w.F.Dump());

        var end = w.Combat.End(w.Campaign, null, new EndRequest { Xp = 100 });

        Assert.Equal("Awarded 100 XP: 100 each to Hero Prime.", end.Xp.Text);
        Assert.Equal(14_060, w.SheetOf("character:hero").Xp);
    }

    [Fact]
    public void Xp_NoEnemyDefeated_TheSheetsTrackXp_SaysTheFightIsWorthNothing()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", """{ "classes": [{ "class": "fighter", "level": 5 }], "abilities": { "con": 14 }, "max_hp": 44, "xp": 13960 }""");
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)] });

        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        Assert.Equal(
            "XP not awarded: no enemy was defeated or left. By the 2024 rules its enemies are worth 450 XP, 450 each for 1. If the party beat them, " +
            "add the share to its sheet: campaign_character {\"action\": \"xp\", \"character\": \"character:hero\", \"amount\": 450} " +
            "(it adds to the sheet's XP total and records no award).",
            end.Xp.Text);
        Assert.Equal(13_960, w.SheetOf("character:hero").Xp);
    }

    [Fact]
    public void Xp_GivenWithNoPartySheet_IsRefused_ZeroAwardsNothing()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, AddParty = false, Combatants = [CombatWorld.Monster("2024", "ogre")] });

        Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, new EndRequest { Xp = 100 }));
        var end = w.Combat.End(w.Campaign, null, new EndRequest { Xp = 0 });
        Assert.Equal(0, end.Xp.Awarded);
        Assert.Equal(0L, w.F.Count("SELECT count(*) FROM award"));
    }

    [Theory]
    [InlineData("character:villain", null)]
    [InlineData("location:the-harbor", "is a location")]
    [InlineData("one-piece/character:keras", "names another campaign's entity")]
    [InlineData("sea/character:villain", "loot item 1: to \"sea/character:villain\": sea is this campaign's own slug; give the handle without it: \"character:villain\".")]
    [InlineData("character:nobody", "names nothing in sea")]
    public void Loot_ToACharacterOrTheParty_AnythingElseRefused(string to, string? refusal)
    {
        using var w = HeroInAFight();
        var request = new EndRequest { Loot = [new LootRequest("Gold idol") { To = to }], Currency = [new CurrencyRequest { Gp = 5 }] };
        if (refusal is not null)
        {
            Assert.Contains(refusal, Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, request)).Message, StringComparison.Ordinal);
            Assert.Equal(ES.Active, w.Encounter(Fight).Status);
            return;
        }

        var end = w.Combat.End(w.Campaign, null, request);

        Assert.Equal("character:villain", Assert.Single(end.Loot).Holder);
        Assert.Equal("faction:the-party", Assert.Single(end.Currency).Holder);
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM holding WHERE name = 'Gold idol' AND quantity = 1 AND notes = 'loot: Endings'"));
    }

    [Theory]
    [InlineData(-1, "Coin", "xp is -1")]
    [InlineData(null, "Coin", "loot item 1: qty")]
    [InlineData(0, "!!!", "loot item 1: give item a name with a letter or digit.")]
    [InlineData(0, "—", "loot item 1: give item a name with a letter or digit.")]
    public void BadRequest_RefusedBeforeAnythingIsRead(int? xp, string item, string expected)
    {
        using var w = HeroInAFight();
        var request = new EndRequest { Xp = xp is -1 ? xp : null, Loot = xp is -1 ? [] : [new LootRequest(item) { Qty = xp is null ? double.NaN : 1 }] };

        Assert.Contains(expected, Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, request)).Message, StringComparison.Ordinal);
        Assert.Equal(ES.Active, w.Encounter(Fight).Status);
    }

    [Fact]
    public void PcThatDied_WrittenWithHp0AndThreeFailures_ItsEntityProposedDead()
    {
        using var w = HeroInAFight();
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 90 });
        Assert.True(w.Combatant(Fight, "Hero Prime").Dead);

        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        var hero = w.SheetOf("character:hero");
        Assert.Equal((0, 0, 3, false), (hero.Hp, hero.DeathSaves.Successes, hero.DeathSaves.Failures, hero.DeathSaves.Stable));
        var proposal = Assert.Single(end.Proposals);
        Assert.Equal("character:hero", proposal.EntityHandle);
        Assert.NotEqual("dead", w.F.Entity(w.Campaign, "character:hero").Status);
    }

    [Fact]
    public void PcLeftDying_WrittenDyingWithItsTallies_TheWayOutReminded()
    {
        using var w = HeroInAFight();
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("hero", Total: 15), new("ogre", Total: 10), new("sidekick", Total: 5)] });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 50 });
        w.Combat.DeathSave(w.Campaign, null, new DeathSaveOp(["hero"]) { Face = 5 });

        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        var hero = w.SheetOf("character:hero");
        Assert.Equal((0, 0, 1), (hero.Hp, hero.DeathSaves.Successes, hero.DeathSaves.Failures));
        Assert.Contains(end.Reminders, r => r.Kind == CombatValues.ReminderKinds.DyingAtEnd && r.Call!.Contains("\"action\": \"heal\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ConsumedPotionBelowZeroNow_IsDrift_NamingWhatIsLeft()
    {
        using var w = HeroInAFight();
        w.Characters.Inventory(w.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = 2 }], WriteContext.Default);
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 30 });
        w.Roller.Push(1, 1, 1, 1);
        w.Combat.Heal(w.Campaign, null, new HealOp(["hero"]) { Dice = "2d4+2", Item = "Potion of Healing" });
        w.Combat.Heal(w.Campaign, null, new HealOp(["hero"]) { Dice = "2d4+2", Item = "Potion of Healing" });
        w.Characters.Inventory(w.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = -1 }], WriteContext.Default);

        var refused = Assert.Throws<DndInputException>(() => w.Combat.End(w.Campaign, null, new EndRequest()));

        Assert.Contains("Potion of Healing: used 2 in the fight; only 1 is left", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Xp_ByDefault_TheDefeatedAndFledEnemies_ALeftPartyMemberGetsNoShare()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", """{ "classes": [{ "class": "fighter", "level": 5 }], "abilities": { "con": 14 }, "max_hp": 44, "xp": 6500 }""");
        w.Sheet("character:sidekick", """{ "classes": [{ "class": "rogue", "level": 5 }], "abilities": { "con": 12 }, "max_hp": 33, "xp": 6500 }""");
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, Combatants = [CombatWorld.Monster("2024", "ogre"), CombatWorld.Monster("2024", "goblin-warrior")] });
        w.Combat.Leave(w.Campaign, null, new LeaveOp(["ogre", "sidekick"]));
        w.Combat.Damage(w.Campaign, null, new DamageOp(["goblin-warrior"]) { Amount = 50 });

        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        Assert.Equal((500, 500, 500, 1), (end.Xp.Worth, end.Xp.Awarded, end.Xp.Each, end.Xp.Recipients));
        Assert.Equal(7000, w.SheetOf("character:hero").Xp);
        Assert.Equal(6500, w.SheetOf("character:sidekick").Xp);
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM award"));
    }

    [Fact]
    public void WriteBack_IsFiledUnderTheFightsSession_AfterThatSessionEnded()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.StartSession(1);
        w.Reload();
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, AddParty = true });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 4 });
        w.F.Sessions.End(w.Campaign, "Paused mid-fight.");
        w.Reload();

        var end = w.Combat.End(w.Campaign, null, new EndRequest());

        Assert.Equal(1, end.SessionNumber);
        Assert.All(w.F.Log(end.BatchId), r => Assert.Equal(w.Id("session:1"), r.SessionId));
    }

    [Fact]
    public void UndoGuard_AHoldingTheFightDrawsOn_ThePrintedEndCallWorks()
    {
        using var w = HeroInAFight();
        var bought = w.Characters.Inventory(w.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Qty = 1 }], WriteContext.Default);
        w.Roller.Push(1, 1);
        w.Combat.Heal(w.Campaign, null, new HealOp(["hero"]) { Dice = "2d4+2", Item = "Potion of Healing" });

        var refused = Assert.Throws<DndInputException>(() => w.F.History.Undo(w.Campaign, bought.BatchId!, WriteContext.Default));
        var call = JsonNode.Parse(PrintedCall().Match(refused.Message).Groups["json"].Value)!;
        w.Combat.End(w.Campaign, call["encounter"]!.GetValue<string>(), new EndRequest { Discard = call["discard"]!.GetValue<bool>() });
        w.F.History.Undo(w.Campaign, bought.BatchId!, WriteContext.Default);

        Assert.Equal(0L, w.F.Count("SELECT count(*) FROM holding"));
    }

    [Fact]
    public void PersistedCondition_IsStoredExactlyAsSection67Says()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        CombatScripts.EndB(w);

        var stored = w.F.Scalar<string>("SELECT conditions FROM character_sheet WHERE entity_id = @id", new { id = w.Id("character:fishman-monk") });

        Assert.Equal("""[{"name":"cursed (Mucus Cloud)","source":"Aboleth","duration":"until_removed","note":"from The dark station (fixture), round 1"}]""", stored);
    }
}
