using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Combat;
using Xunit;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: every <c>combat</c> step persists exactly the state it returns (contract §14.6 "every action's persistence
/// round trip"): reading the encounter back from campaigns.db gives the step's own result, combatant for combatant, with
/// the round and the turn; each step writes its combat_log rows (kinds pinned, not counts, §6.9); and no step writes a
/// change_log row (PLAN 7: HP ticks stay out of history). A refused step writes nothing.
/// </summary>
public sealed class CombatStepPersistenceTests
{
    private const string Fight = "Persistence";

    private static void AssertPersisted(CombatWorld w, CombatOutcome outcome)
    {
        var stored = w.State(Fight);
        var returned = outcome.Encounter.State;
        Assert.Equal((returned.Round, returned.TurnCombatantId, returned.Status), (stored.Round, stored.TurnCombatantId, stored.Status));
        Assert.Equal(returned.Combatants.Select(c => c.Id), stored.Combatants.Select(c => c.Id));
        foreach (var c in returned.Combatants)
        {
            Assert.True(CombatJson.SameState(c, stored.Find(c.Id)!), $"{c.Name} after {outcome.Action} differs from the stored row.");
        }
    }

    private static CombatWorld Fighting()
    {
        var w = CombatWorld.Dm();
        try
        {
            w.Sheet("character:hero", """
                { "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14, "dex": 12, "int": 18 }, "ac": 12, "max_hp": 32,
                  "resources": [{ "name": "Arcane Recovery", "max": 1, "recharge": "long_rest" }] }
                """);
            w.Sheet("character:sidekick", CombatLifecycleTests.SidekickJson);
            return w;
        }
        catch
        {
            w.Dispose();
            throw;
        }
    }

    [Fact]
    public void EveryStep_StoresWhatItReturns_LogsItsKinds_AndNoChangeLogRow()
    {
        using var w = Fighting();
        var c = w.Campaign;
        var s = w.Combat;
        var history = w.ChangeRows();
        var steps = new List<(CombatOutcome Outcome, string[] Kinds)>();
        void Step(CombatOutcome outcome, params string[] kinds)
        {
            AssertPersisted(w, outcome);
            steps.Add((outcome, kinds));
        }

        Step(s.Start(c, new StartRequest { Name = Fight }), L.Start, L.Add);
        Step(s.Add(c, null, [CombatWorld.Monster("2024", "ogre", hp: HpChoice.Avg), new CombatantRequest { Name = "Cultist", Hp = HpChoice.Of(9), Ac = 12, Count = 2 }]), L.Add);
        Step(s.Set(c, null, new SetOp([new SetEntry("cultist-2") { Hp = 11, Hidden = true }])), L.Add);
        Step(s.Initiative(c, null, new InitiativeOp { Rolls = [new("hero", Total: 18), new("sidekick", Total: 15), new("ogre", Total: 12), new("cultist", Total: 8)] }), L.Initiative);
        Step(s.Concentration(c, null, new ConcentrationOp(["hero"]) { Spell = "Hold Person", SlotLevel = 2, Duration = "1 minute" }), L.Concentration);
        Step(s.Condition(c, null, new ConditionOp(["ogre"]) { Add = ["paralyzed"], Duration = "concentration", Source = "hero" }), L.Condition);
        Step(s.Next(c, null, "hero"), L.Turn);
        Step(s.Damage(c, null, new DamageOp(["ogre"]) { Amount = 20, DamageType = "piercing" }), L.Damage);
        Step(s.Use(c, null, new UseOp(["hero"]) { Resource = "Arcane Recovery" }), L.Resource);
        Step(s.Next(c, null), L.Turn);
        Step(s.Damage(c, null, new DamageOp(["hero"]) { Amount = 40, DamageType = "bludgeoning", Source = "ogre" }), L.Damage);
        Step(s.DeathSave(c, null, new DeathSaveOp(["hero"]) { Face = 12 }), L.DeathSave);
        Step(s.Heal(c, null, new HealOp(["hero"]) { Amount = 6, Source = "sidekick" }), L.Heal);
        Step(s.Heal(c, null, new HealOp(["sidekick"]) { Amount = 5, Temp = true }), L.TempHp);
        Step(s.Prev(c, null), L.Turn);
        Step(s.Leave(c, null, new LeaveOp(["cultist"])), L.Remove);

        var log = w.Log(Fight).Select(r => r.Kind).ToList();
        foreach (var (outcome, kinds) in steps)
        {
            foreach (var kind in kinds)
            {
                Assert.Contains(kind, log);
            }
        }

        Assert.Equal(history, w.ChangeRows());
        Assert.Equal(0, w.Roller.Left);
    }

    [Fact]
    public void Legendary_SpendsAndPersistsTheCounts()
    {
        using var w = Fighting();
        var c = w.Campaign;
        w.Combat.Start(c, new StartRequest
        {
            Name = Fight,
            AddParty = false,
            Combatants = [CombatWorld.Monster("2024", "adult-red-dragon", hp: HpChoice.Avg), new CombatantRequest { Name = "Knight", Hp = HpChoice.Of(50), Side = "ally" }],
        });
        w.Combat.Initiative(c, null, new InitiativeOp { Rolls = [new("knight", Total: 20), new("adult-red-dragon", Total: 10)] });

        var outcome = w.Combat.Legendary(c, null, new LegendaryOp("adult-red-dragon") { Amount = 1 });

        AssertPersisted(w, outcome);
        Assert.Equal(1, w.Combatant(Fight, "Adult Red Dragon").Legendary!.Used);
        Assert.Equal(L.Legendary, w.Log(Fight)[^1].Kind);
    }

    [Fact]
    public void RefusedStep_WritesNothing()
    {
        using var w = Fighting();
        w.Combat.Start(w.Campaign, new StartRequest { Name = Fight });
        var log = w.Log(Fight).Count;
        var state = w.State(Fight);

        Assert.Throws<DndInputException>(() => w.Combat.Damage(w.Campaign, null, new DamageOp(["nobody"]) { Amount = 3 }));
        Assert.Throws<DndInputException>(() => w.Combat.Next(w.Campaign, null));

        Assert.Equal(log, w.Log(Fight).Count);
        Assert.All(w.State(Fight).Combatants, x => Assert.True(CombatJson.SameState(x, state.Find(x.Id)!)));
    }

    [Fact]
    public void Next_FromAnotherTurnHolder_IsRefused_TheTurnStays()
    {
        using var w = Fighting();
        var c = w.Campaign;
        w.Combat.Start(c, new StartRequest { Name = Fight });
        w.Combat.Initiative(c, null, new InitiativeOp { Rolls = [new("hero", Total: 18), new("sidekick", Total: 15)] });
        w.Combat.Next(c, null, "hero");

        var refused = Assert.Throws<DndInputException>(() => w.Combat.Next(c, null, "hero"));

        Assert.Contains("Sidekick", refused.Message, StringComparison.Ordinal);
        Assert.Equal("Sidekick", w.State(Fight).TurnHolder!.Name);
    }

    [Fact]
    public void Prev_StraightAfterNext_RestoresTheExpiredCondition()
    {
        using var w = Fighting();
        var c = w.Campaign;
        w.Combat.Start(c, new StartRequest { Name = Fight, Combatants = [new CombatantRequest { Name = "Cultist", Hp = HpChoice.Of(9) }] });
        w.Combat.Initiative(c, null, new InitiativeOp { Rolls = [new("hero", Total: 18), new("sidekick", Total: 15), new("cultist", Total: 5)] });
        w.Combat.Condition(c, null, new ConditionOp(["cultist"]) { Add = ["frightened"], Duration = "until_end_of_source_turn", Source = "sidekick" });
        w.Combat.Next(c, null);
        var before = w.Combatant(Fight, "Cultist").Conditions;
        w.Combat.Next(c, null);
        Assert.Empty(w.Combatant(Fight, "Cultist").Conditions);

        var back = w.Combat.Prev(c, null);

        AssertPersisted(w, back);
        Assert.Equal(CombatJson.WriteConditions(before), CombatJson.WriteConditions(w.Combatant(Fight, "Cultist").Conditions));
        Assert.Equal("Sidekick", w.State(Fight).TurnHolder!.Name);
    }
}
