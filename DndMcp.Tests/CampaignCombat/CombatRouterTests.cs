using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using Xunit;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: D5's routing through <see cref="CombatRouter"/>. While a character is a sheet-seeded combatant of the active
/// fight (one that left included, until the fight ends: F2, review CR07), <c>campaign_character</c> damage, heal, temp_hp,
/// use and condition change the combatant
/// (unlogged: combat_log rows, no change_log row, no batch id) and reach the sheet only at <c>end</c>; a routed condition
/// keeps the sheet's <c>until_removed</c>, so it persists; a character in the fight from a stat block, or with no sheet,
/// is acted on as out of combat (one logged batch, with the note); <c>rest</c> is refused in the fight; the fight's own
/// refusals refuse the call.
/// </summary>
public sealed class CombatRouterTests
{
    private const string Fight = "Routed";

    private static CombatWorld HeroInAFight()
    {
        var w = CombatWorld.Dm();
        try
        {
            w.Sheet("character:hero", """
                { "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14, "int": 18 }, "ac": 12, "max_hp": 32,
                  "resources": [{ "name": "Arcane Recovery", "max": 1, "recharge": "long_rest" }] }
                """);
            w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, AddParty = false, Combatants = [new CombatantRequest { Character = "character:hero" }] });
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    [Fact]
    public void Damage_SheetSeeded_ChangesTheCombatantOnly_NoBatch_TheSheetCatchesUpAtEnd()
    {
        using var w = HeroInAFight();
        var rows = w.ChangeRows();

        var result = w.Characters.Damage(w.Campaign, "character:hero", 7, "fire", WriteContext.Default);

        Assert.Null(result.BatchId);
        Assert.True(result.Routed!.Applied);
        Assert.Equal("Applied to the live fight Routed; written to the sheet when it ends.", result.Notes[0]);
        Assert.Equal(rows, w.ChangeRows());
        Assert.Equal(25, w.Combatant(Fight, "Hero Prime").Hp);
        Assert.Equal(32, w.SheetOf("character:hero").Hp);
        Assert.Equal(L.Damage, w.Log(Fight)[^1].Kind);

        w.Combat.End(w.Campaign, null, new EndRequest());

        Assert.Equal(25, w.SheetOf("character:hero").Hp);
    }

    [Fact]
    public void HealTempUse_AreRoutedWithTheFightsRules()
    {
        using var w = HeroInAFight();
        w.Characters.Damage(w.Campaign, "character:hero", 10, null, WriteContext.Default);

        w.Characters.Heal(w.Campaign, "character:hero", 4, WriteContext.Default);
        w.Characters.TempHp(w.Campaign, "character:hero", 5, WriteContext.Default);
        w.Characters.Use(w.Campaign, "character:hero", null, false, "Arcane Recovery", 1, WriteContext.Default);

        var hero = w.Combatant(Fight, "Hero Prime");
        Assert.Equal((26, 5, 1), (hero.Hp, hero.TempHp, hero.Resources["arcane-recovery"].Used));
        Assert.Contains(L.TempHp, w.Log(Fight).Select(r => r.Kind));
        Assert.Contains(L.Resource, w.Log(Fight).Select(r => r.Kind));
    }

    [Fact]
    public void Condition_RoutedKeepsUntilRemoved_ItPersistsToTheSheetAtEnd()
    {
        using var w = HeroInAFight();

        var result = w.Characters.Condition(w.Campaign, "character:hero", ["cursed"], null, null, WriteContext.Default);

        Assert.True(result.Routed!.Applied);
        Assert.Equal(CombatValues.Durations.UntilRemoved, w.Combatant(Fight, "Hero Prime").Conditions.Single().Duration);
        w.Combat.End(w.Campaign, null, new EndRequest());
        var kept = Assert.Single(w.SheetOf("character:hero").Conditions);
        Assert.Equal(("cursed", "until_removed"), (kept.Name, kept.Duration));
    }

    /// <summary>
    /// F2R09: a condition the sheet tool routes into the fight has no source: nobody in the fight imposed it. The tracker's
    /// default (the turn-holder) made it "Hero Prime: poisoned (Aria)", persisted "from Aria" on the sheet, though Aria
    /// poisoned no one; the sheet now stores a plain poisoned, as when it is added out of combat.
    /// </summary>
    [Fact]
    public void Condition_RoutedOnAnotherCombatantsTurn_HasNoSource_TheSheetStoresNone()
    {
        using var w = HeroInAFight();
        w.Combat.Add(w.Campaign, null, [new CombatantRequest { Name = "Aria", Hp = HpChoice.Of(20), Ac = 12, Side = "ally" }]);
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("aria", Total: 20), new("hero", Total: 5)] });
        Assert.Equal("Aria", w.State(Fight).TurnHolder!.Name);

        var result = w.Characters.Condition(w.Campaign, "character:hero", ["poisoned"], null, null, WriteContext.Default);

        var poisoned = Assert.Single(w.Combatant(Fight, "Hero Prime").Conditions);
        Assert.Equal(("poisoned", (string?)null, (string?)null), (poisoned.Name, poisoned.Source, poisoned.SourceNote));
        Assert.DoesNotContain(result.Routed!.Lines, l => l.Contains("Aria", StringComparison.Ordinal));
        w.Combat.End(w.Campaign, null, new EndRequest { Xp = 0 });
        var kept = Assert.Single(w.SheetOf("character:hero").Conditions);
        Assert.Equal(("poisoned", (string?)null, "until_removed"), (kept.Name, kept.Source, kept.Duration));
    }

    [Fact]
    public void Condition_RemoveConcentration_EndsTheCombatantsConcentration()
    {
        using var w = HeroInAFight();
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["hero"]) { Spell = "Fly" });

        w.Characters.Condition(w.Campaign, "character:hero", null, ["concentration"], null, WriteContext.Default);

        Assert.Null(w.Combatant(Fight, "Hero Prime").Concentration);
    }

    [Fact]
    public void Exhaustion_IsRoutedByLevel()
    {
        using var w = HeroInAFight();

        w.Characters.Condition(w.Campaign, "character:hero", ["exhaustion"], null, 2, WriteContext.Default);

        Assert.Equal(2, w.Combatant(Fight, "Hero Prime").Exhaustion);
        Assert.Equal(0, w.SheetOf("character:hero").Exhaustion);
    }

    [Fact]
    public void Rest_DuringTheFight_IsRefused()
    {
        using var w = HeroInAFight();

        var refused = Assert.Throws<DndInputException>(() => w.Characters.Rest(w.Campaign, "character:hero", "short", null, null, w.Roller, WriteContext.Default));

        Assert.Contains("is in the live fight \"Routed\"", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InTheFightFromAStatBlock_ActsOnTheSheet_OneBatch_TheFightUnchanged()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight, AddParty = false, Combatants = [CombatWorld.Monster("2024", "ogre", character: "character:hero", side: "ally")] });

        var result = w.Characters.Damage(w.Campaign, "character:hero", 7, null, WriteContext.Default);

        Assert.NotNull(result.BatchId);
        Assert.Equal(RouteOutcomes.StatBlock, result.Routed!.Outcome);
        Assert.Contains(result.Notes, n => n == "Hero Prime is in the live fight as Ogre from a stat block: this changed the sheet, not the fight (use combat to change the fight).");
        Assert.Equal(37, w.SheetOf("character:hero").Hp);
        Assert.Equal(w.Combatant(Fight, "Ogre").MaxHp, w.Combatant(Fight, "Ogre").Hp);
    }

    [Fact]
    public void NotInTheFight_OrNoFight_IsNotRouted()
    {
        using var w = HeroInAFight();
        using (var connection = w.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Assert.False(w.Router.TryRoute(connection, transaction, w.Campaign, w.Id("character:sidekick"), new CharacterAction { Kind = CharacterActions.Damage, Amount = 1 }, out _));
        }

        w.Combat.End(w.Campaign, null, new EndRequest());
        var result = w.Characters.Damage(w.Campaign, "character:hero", 3, null, WriteContext.Default);

        Assert.NotNull(result.BatchId);
        Assert.Null(result.Routed);
        Assert.Equal(29, w.SheetOf("character:hero").Hp);
    }

    /// <summary>
    /// CR07 (F2, contract §6.2, D5): a sheet-seeded combatant that left stays tied to its fight until the fight ends. Its
    /// character's sheet actions are routed to it exactly as before it left (a heal between leave and end changes the
    /// combatant, says the combatant left, logs no batch), a rest is still refused, and end writes back that one state:
    /// before, the heal acted on the sheet and end wrote the fight's HP over it with no word.
    /// </summary>
    [Fact]
    public void Left_SheetSeeded_IsStillRoutedUntilTheFightEnds_EndWritesBackThatState()
    {
        using var w = HeroInAFight();
        w.Characters.Damage(w.Campaign, "character:hero", 10, null, WriteContext.Default);
        w.Combat.Leave(w.Campaign, null, new LeaveOp(["hero"]));

        var heal = w.Characters.Heal(w.Campaign, "character:hero", 5, WriteContext.Default);
        var rest = Assert.Throws<DndInputException>(() => w.Characters.Rest(w.Campaign, "character:hero", "short", null, null, w.Roller, WriteContext.Default));

        Assert.True(heal.Routed!.Applied);
        Assert.Null(heal.BatchId);
        Assert.Equal("Applied to the live fight Routed, which Hero Prime left; written to the sheet when it ends.", heal.Notes[0]);
        Assert.Equal((27, true), (w.Combatant(Fight, "Hero Prime").Hp, w.Combatant(Fight, "Hero Prime").Removed));
        Assert.Equal(32, w.SheetOf("character:hero").Hp);
        Assert.StartsWith("Hero Prime is in the live fight \"Routed\" as Hero Prime (it left the fight): rest once it ends", rest.Message, StringComparison.Ordinal);

        w.Combat.End(w.Campaign, null, new EndRequest());

        Assert.Equal(27, w.SheetOf("character:hero").Hp);
    }

    /// <summary>
    /// R01: membership is asked first, reading no combatant: while another combatant of the fight is unreadable, a
    /// character who is not in the fight is acted on on its sheet; the unreadable combatant's own routed action is refused
    /// with the store message naming it.
    /// </summary>
    [Fact]
    public void UnreadableCombatant_ACharacterNotInTheFight_ActsOnItsSheet_TheUnreadableOneIsRefusedNamingIt()
    {
        using var w = HeroInAFight();
        w.Sheet("character:sidekick", CombatLifecycleTests.SidekickJson);
        w.F.Db.WriteBehindTheServer("UPDATE combatant SET conditions = 'garbage' WHERE name = 'Hero Prime'");

        var sidekick = w.Characters.Damage(w.Campaign, "character:sidekick", 3, null, WriteContext.Default);
        var damaged = w.SheetOf("character:sidekick").Hp;
        var rest = w.Characters.Rest(w.Campaign, "character:sidekick", "long", null, null, w.Roller, WriteContext.Default);
        var refused = Assert.Throws<CampaignStoreUnavailableException>(() => w.Characters.Damage(w.Campaign, "character:hero", 3, null, WriteContext.Default));

        Assert.NotNull(sidekick.BatchId);
        Assert.Null(sidekick.Routed);
        Assert.Equal((30, 33), (damaged, w.SheetOf("character:sidekick").Hp));
        Assert.Null(rest.Routed);
        Assert.StartsWith("Combatant \"Hero Prime\" of the fight \"Routed\" in campaign sea cannot be read", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FightRefusal_RefusesTheCall_WritingNothing()
    {
        using var w = HeroInAFight();
        var log = w.Log(Fight).Count;

        Assert.Throws<DndInputException>(() => w.Characters.Use(w.Campaign, "character:hero", null, false, "Arcane Recovery", 2, WriteContext.Default));

        Assert.Equal(log, w.Log(Fight).Count);
    }

    [Fact]
    public void DryRun_RollsTheRoutedChangeBack()
    {
        using var w = HeroInAFight();

        w.Characters.Damage(w.Campaign, "character:hero", 7, null, new WriteContext { DryRun = true });

        Assert.Equal(32, w.Combatant(Fight, "Hero Prime").Hp);
    }

    [Fact]
    public void RoutedReminders_AreAboutTheRoutedCombatantOnly_NeverAnotherCombatantsTurn()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = Fight,
            AddParty = false,
            Combatants = [new CombatantRequest { Character = "character:hero" }, CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg)],
        });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("ogre", Total: 20), new("hero", Total: 10)] });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["ogre"]) { Add = ["poisoned"], Source = "hero" });
        var ogre = w.Combatant(Fight, "Ogre").Id;

        var step = w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 1 });
        var routed = w.Characters.Damage(w.Campaign, "character:hero", 1, null, WriteContext.Default).Routed!;

        Assert.Contains(step.Reminders, r => r.CombatantId == ogre && r.Text.Contains("poisoned", StringComparison.Ordinal));
        Assert.True(routed.Applied);
        Assert.DoesNotContain(routed.Reminders, r => r.Text.Contains("poisoned", StringComparison.Ordinal));
    }

    [Fact]
    public void Ops_ConditionWithConcentrationAndAnAdd_IsTheDropThenTheAdd()
    {
        var ops = CombatRouter.Ops(new CharacterAction { Kind = CharacterActions.Condition, Add = ["poisoned"], Remove = ["Concentration"], Duration = "until_removed" }, "hero");

        Assert.Collection(ops,
            op => Assert.True(Assert.IsType<ConcentrationOp>(op).Drop),
            op =>
            {
                var condition = Assert.IsType<ConditionOp>(op);
                Assert.Equal(["poisoned"], condition.Add!);
                Assert.Null(condition.Remove);
                Assert.Equal("until_removed", condition.Duration);
            });
    }
}
