using System.Text.Json;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using Xunit;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: <see cref="EncounterSimulationLoader"/> feeds <c>balance_simulate {encounter}</c> (contract §6.11): the
/// party from the encounter's party-side combatants in order, each sheet-seeded member exactly as P's
/// <see cref="SheetSimulation.Entry"/> makes it from its CURRENT sheet (so the explicit <c>character</c> call is the same
/// entry), a member with no simulation route excluded with a note naming the fix (Serif, the artificer), identical
/// monsters grouped, the lair passed on; a planned fight with no party combatants takes the campaign's current party;
/// <c>from_state</c> seeds every creature's live state and the resume point (FIX §2.6); store failures are the store message.
/// </summary>
public sealed class EncounterSimulationLoaderTests
{
    private static string Json(object value) => JsonSerializer.Serialize(value);

    [Fact]
    public void FixtureA_ThePartyFromTheSheets_SerifExcludedWithTheFix_TheMummiesGrouped()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w, until: "A2");

        var sim = w.Loader.Load(w.Campaign);

        Assert.Equal((CombatScripts.CryptName, "2014", false), (sim.EncounterName, sim.Edition, sim.Lair));
        Assert.Equal(["Aiden Ironstar", "Belmakor Silverwind", "Ignis", "Lieutenant James Torch", "Vars Nocturne"], sim.Party.Select(p => p.Spec.Name));
        foreach (var (handle, entry) in new[] { "character:aiden-ironstar", "character:belmakor", "character:ignis", "character:torch", "character:vars" }.Zip(sim.Party))
        {
            var expected = SheetSimulation.Entry(w.SheetOf(handle), entry.Spec.Name!, "2014").Entry;
            Assert.Equal(Json(expected), Json(entry.Spec));
        }

        Assert.Equal(("paladin", 12), (sim.Party[0].Spec.Archetype, sim.Party[0].Spec.Level));
        Assert.Equal((110, 17), (sim.Party[1].Spec.Hp, sim.Party[1].Spec.Ac));
        Assert.NotNull(sim.Party[1].Spec.Build);
        Assert.Equal(74, sim.Party[3].Spec.Hp);
        Assert.Contains(sim.Notes, n => n.StartsWith("Serif is left out:", StringComparison.Ordinal) && n.Contains("sim_profile", StringComparison.Ordinal));
        Assert.Equal([("2014/monster/mummy-lord", (int?)null), ("2014/monster/mummy", 2)], sim.Enemies.Select(e => (e.Spec.Monster!, e.Spec.Count)));
        Assert.All(sim.Enemies, e => Assert.NotNull(e.Monster));
        Assert.Null(sim.Resume);
        Assert.NotEmpty(sim.Assumptions);
    }

    [Fact]
    public void FixtureB_InTheLair_TheLairIsPassedOn()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w, until: "B4");

        var sim = w.Loader.Load(w.Campaign);

        Assert.True(sim.Lair);
        Assert.Equal(["Björn Mountainfell", "The amethyst Dragon Slayer", "The fishman monk"], sim.Party.Select(p => p.Spec.Name));
        Assert.Equal(("barbarian", 8, 85), (sim.Party[0].Spec.Archetype, sim.Party[0].Spec.Level, sim.Party[0].Spec.Hp));
        Assert.Equal((68, 16), (sim.Party[1].Spec.Hp, sim.Party[1].Spec.Ac));
        Assert.NotNull(sim.Party[1].Spec.Build);
        Assert.Equal(("monk", 8, 59), (sim.Party[2].Spec.Archetype, sim.Party[2].Spec.Level, sim.Party[2].Spec.Hp));
        Assert.Equal("2024/monster/aboleth", Assert.Single(sim.Enemies).Spec.Monster);
        Assert.Contains(sim.Notes, n => n.Contains("lair", StringComparison.Ordinal));
    }

    [Fact]
    public void FixtureA_FromStateAtTheStartOfRound2_TheLiveStateAndTheResumePoint()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w, until: "A19");

        var sim = w.Loader.Load(w.Campaign, fromState: true);

        var resume = Assert.IsType<FightResume>(sim.Resume);
        Assert.Equal(2, resume.Round);
        var all = sim.Party.Concat(sim.Enemies).ToList();
        Assert.Equal(all.Count, resume.Order.Count);
        Assert.Equal("Vars Nocturne", all[resume.Order[resume.StartAt]].Spec.Name);
        var belmakor = all.Single(e => e.Spec.Name == "Belmakor Silverwind").Start!;
        Assert.Equal((82, 0), (belmakor.Hp, belmakor.TempHp));
        Assert.Equal("Circle of Power", belmakor.Concentration);
        var torch = all.Single(e => e.Spec.Name == "Lieutenant James Torch").Start!;
        Assert.Equal(0, torch.Hp);
        Assert.Contains(torch.Conditions, c => c.Condition == "frightened");
        var lord = all.Single(e => e.Spec.Name == "Mummy Lord").Start!;
        Assert.Equal((41, 2), (lord.Hp, lord.LegendaryActionsLeft));
        Assert.Contains(sim.Notes, n => n.StartsWith("Serif is left out:", StringComparison.Ordinal));
    }

    [Fact]
    public void FromState_BeforeRound1_IsRefused_UnknownHpEnemiesTooWithTheFix()
    {
        using var w = CombatWorld.Player();
        w.Sheet("character:aria-vale", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Unknowns", Combatants = [CombatWorld.Monster("2014", "ogre")] });

        Assert.StartsWith("from_state resumes a running fight", Assert.Throws<DndInputException>(() => w.Loader.Load(w.Campaign, fromState: true)).Message, StringComparison.Ordinal);
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("aria-vale", Total: 15), new("ogre", Total: 10), new("bram", Total: 5)] });

        var refused = Assert.Throws<DndInputException>(() => w.Loader.Load(w.Campaign, fromState: true));

        Assert.Contains("give hp with combat set", refused.Message, StringComparison.Ordinal);
        Assert.False(w.Combatant("Unknowns", "Ogre").HpKnown);
    }

    [Fact]
    public void PlannedFightWithNoParty_TakesTheCampaignsCurrentParty_TheDeadNamed()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Sheet("character:sidekick", CombatLifecycleTests.SidekickJson);
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Prepped") { Combatants = [CombatWorld.Monster("2024", "ogre", 2)] });

        var sim = w.Loader.Load(w.Campaign, "Prepped");

        Assert.Equal(["Hero Prime", "Sidekick"], sim.Party.Select(p => p.Spec.Name));
        Assert.Contains(sim.Notes, n => n.Contains("current party", StringComparison.Ordinal));
        Assert.Contains(sim.Notes, n => n.Contains("Fallen (dead)", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(sim.Enemies).Spec.Count);
    }

    [Fact]
    public void PlannedFightWithAnAllyAndNoPartyMember_ThePartyIsTheAlly_NotTheCampaignsParty()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Escort") { Combatants = [CombatWorld.Monster("2024", "ogre", side: "ally"), CombatWorld.Monster("2024", "goblin-warrior")] });

        var sim = w.Loader.Load(w.Campaign, "Escort");

        Assert.Equal("2024/monster/ogre", Assert.Single(sim.Party).Spec.Monster);
        Assert.DoesNotContain(sim.Notes, n => n.Contains("current party", StringComparison.Ordinal));
        Assert.DoesNotContain(sim.Notes, n => n.Contains("Fallen", StringComparison.Ordinal));
    }

    [Fact]
    public void ASideLeftEmpty_ComesBackEmpty_ForTheHostToAppendThenRefuse()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Lonely", AddParty = false, Combatants = [CombatWorld.Monster("2024", "ogre")] });

        var sim = w.Loader.Load(w.Campaign);

        Assert.Empty(sim.Party);
        Assert.Throws<DndInputException>(() => TrackerSimulation.RequireBothSides(sim.Party, sim.Enemies, sim.Notes, fromState: false));
    }

    [Fact]
    public void NoSuchEncounter_IsTheAuthorsRefusal()
    {
        using var w = CombatWorld.Dm();

        Assert.StartsWith("No combat is running in sea", Assert.Throws<DndInputException>(() => w.Loader.Load(w.Campaign)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DamagedCombatantTable_IsTheStoreMessage()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Damaged", AddParty = false, Combatants = [CombatWorld.Monster("2024", "ogre")] });
        w.F.Db.ScrambleRootPage("combatant");

        Assert.Throws<CampaignStoreUnavailableException>(() => w.Loader.Load(w.Campaign));
    }

    [Fact]
    public void FromState_ADeadEnemyThatImposedSomethingStillRunning_IsAPlaceholder()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Fear", Combatants = [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg), CombatWorld.Monster("2024", "goblin-warrior", hp: HpChoice.Avg)] });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("ogre", Total: 20), new("hero", Total: 15), new("goblin-warrior", Total: 12), new("sidekick", Total: 5)] });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["hero"]) { Add = ["frightened"], Duration = "1 minute", Source = "ogre" });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["ogre"]) { Amount = 200 });

        var sim = w.Loader.Load(w.Campaign, fromState: true);

        var ogre = Assert.Single(sim.Enemies, e => e.Start?.Placeholder == true);
        Assert.Equal("Ogre", ogre.Spec.Name);
        Assert.Contains(sim.Enemies, e => e.Start?.Placeholder != true);
    }

    [Fact]
    public void FixtureB_FromStateAtTheStartOfRound3_TheAbolethAt13_ItsLegendaryCountsLeft()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w, until: "B25");

        var sim = w.Loader.Load(w.Campaign, fromState: true);

        var resume = Assert.IsType<FightResume>(sim.Resume);
        var all = sim.Party.Concat(sim.Enemies).ToList();
        Assert.Equal(3, resume.Round);
        Assert.Equal(["The fishman monk", "Aboleth", "The amethyst Dragon Slayer", "Björn Mountainfell"], resume.Order.Select(i => all[i].Spec.Name));
        Assert.Equal(0, resume.StartAt);
        var aboleth = all.Single(e => e.Spec.Name == "Aboleth").Start!;
        Assert.Equal((13, 2, 2), (aboleth.Hp, aboleth.LegendaryActionsLeft, aboleth.LegendaryResistanceLeft));
        Assert.True(sim.Lair);
        var bjorn = all.Single(e => e.Spec.Name == "Björn Mountainfell").Start!;
        Assert.Equal((51, 1), (bjorn.Hp, bjorn.Exhaustion));
        Assert.Contains(bjorn.Conditions, c => c.Condition == "grappled");
        Assert.Equal(7, all.Single(e => e.Spec.Name == "The fishman monk").Start!.Hp);
        Assert.Equal(56, all.Single(e => e.Spec.Name == "The amethyst Dragon Slayer").Start!.Hp);
    }
}
