using DndMcp.Domain.Simulation;
using Xunit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// The stat block members the normalizer added in Phase 5 (target-turn durations, extra conditions, several attack
/// rolls per use, self-only heals, shared recharges, area auto-hits, use-actions in the Action slot, condition-only
/// auras, forms), each on a hand-built stat block, and one fight with all of them together that must run clean.
/// </summary>
public sealed class StatBlockFeatureTests
{
    private static readonly SimulationCombatant Fighter = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter");

    private static StatBlock Monster(string name, IReadOnlyList<StatBlockAction> actions, IReadOnlyList<StatBlockTrait>? traits = null, int hp = 500) =>
        TestStatBlocks.Create(name, 12, hp, "10d10", (14, 12, 14, 10, 10, 10), actions, traits: traits, initiative: -5);

    private static ConditionEffect Effect(string condition, string duration, int? escapeDc = null) =>
        new() { Condition = condition, Duration = duration, EscapeDc = escapeDc };

    [Fact]
    public void UntilEndOfTargetTurn_CoversTheTargetsNextTurn_ThenEnds()
    {
        var shocker = Monster("Shocker", [TestStatBlocks.Attack("Jolt", 30, "1", "lightning", onHit:
            [new ActionEffect { Kind = K.EffectKinds.Condition, Condition = Effect("incapacitated", K.Durations.UntilEndOfTargetTurn) }])]);
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(shocker)], seed);
            var pc = fight.Named("Fighter");
            fight.TakeTurn(fight.Named("Shocker"));
            if (!pc.Has(Cond.Incapacitated))
            {
                continue; // a natural 1
            }

            fight.TakeTurn(pc);
            Assert.Equal(0, pc.DealtRaw); // no action on the covered turn
            Assert.False(pc.Has(Cond.Incapacitated)); // and it ends with that turn
        }
    }

    [Fact]
    public void ConditionOnlyAura_ImposedAsTheTargetStartsItsTurn_UntilItsNextTurnStarts()
    {
        var ghast = Monster("Ghast", [TestStatBlocks.Attack("Claws", 5, "2d6+3", "slashing")], traits:
        [
            new StatBlockTrait
            {
                Name = "Stench",
                Kind = K.TraitKinds.AuraDamage,
                Save = new SaveSpec("con", 30, K.OnSuccess.None),
                Condition = Effect("poisoned", K.Durations.UntilStartOfTargetTurn),
                Area = new AreaSpec(K.Shapes.Emanation, 5),
                Text = "Any creature that starts its turn within 5 feet of the ghast must succeed on a DC 10 Constitution saving throw or be poisoned until the start of its next turn.",
            },
        ]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(ghast)]);
        var pc = fight.Named("Fighter");
        fight.StartOfTurn(pc);
        Assert.True(pc.Has(Cond.Poisoned));
        fight.EndOfTurn(pc);
        Assert.True(pc.Has(Cond.Poisoned)); // it covers the whole turn

        fight.ApplyDamage(null, fight.Named("Ghast"), Scripted.Damage(1000), false, false, false, false, false);
        fight.StartOfTurn(pc);
        Assert.False(pc.Has(Cond.Poisoned)); // ended as the next turn began, and no aura is left to renew it
    }

    [Fact]
    public void ExtraConditions_OnASaveAction_AllLand()
    {
        var kraken = Monster("Inker", [TestStatBlocks.SaveAction("Toxic Ink", "con", 30, "", "poison", condition: Effect("blinded", K.Durations.Fight)) with
        {
            ExtraConditions = [Effect("poisoned", K.Durations.Fight)],
        }]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(kraken)]);
        fight.TakeTurn(fight.Named("Inker"));
        var pc = fight.Named("Fighter");
        Assert.True(pc.Has(Cond.Blinded));
        Assert.True(pc.Has(Cond.Poisoned));
    }

    [Fact]
    public void ExtraConditions_OnAHit_GrappleAndRestraint_EndTogetherOnEscape()
    {
        var constrictor = Monster("Constrictor", [TestStatBlocks.Attack("Constrict", 30, "1d8", "bludgeoning", onHit:
        [
            new ActionEffect
            {
                Kind = K.EffectKinds.Condition,
                Condition = Effect("restrained", K.Durations.UntilEscape, 1),
                ExtraConditions = [Effect("grappled", K.Durations.UntilEscape, 1)],
            },
        ])]);
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(constrictor)], seed);
            var pc = fight.Named("Fighter");
            fight.TakeTurn(fight.Named("Constrictor"));
            if (!pc.Has(Cond.Restrained))
            {
                continue;
            }

            Assert.True(pc.Has(Cond.Grappled));
            fight.TakeTurn(pc); // escape DC 1: it spends its Action and gets free of both
            Assert.False(pc.Has(Cond.Restrained));
            Assert.False(pc.Has(Cond.Grappled));
        }
    }

    [Fact]
    public void AttackRolls_OneUseMakesEveryRoll()
    {
        var ray = TestStatBlocks.Attack("Scorching Ray", 30, "2d6", "fire", K.AttackRanges.Ranged) with { AttackRolls = 3, IsSpell = true, Magical = true };
        var mage = Monster("Mage", [ray]);
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(mage)])).Setup);
        var log = new CombatLog();
        fight.Begin(4, log);
        fight.TakeTurn(fight.Named("Mage"));
        var text = log.Finish(string.Empty);
        Assert.Equal(3, text.Split("Scorching Ray vs Fighter").Length - 1);
    }

    [Fact]
    public void SelfOnlyHeal_NeverHealsAnAlly()
    {
        var unicorn = Monster("Unicorn", [TestStatBlocks.Attack("Horn", 5, "1d8+4", "piercing"), new StatBlockAction
        {
            Name = "Heal Self",
            Kind = K.ActionKinds.Heal,
            Slot = K.ActionSlots.Action,
            Healing = Domain.Features.DamageFormula.ParseDamage("2d8+2", "healing"),
            SelfOnly = true,
            Text = "The unicorn magically regains 11 (2d8 + 2) hit points.",
        }], hp: 100);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(unicorn), SimKit.Monster(TestStatBlocks.Ogre)]);
        var self = fight.Named("Unicorn");
        var ogre = fight.Named("Ogre");
        ogre.Hp = 5; // an ally at 8%
        fight.TakeTurn(self);
        Assert.Equal(5, ogre.Hp);

        self.Hp = 20;
        fight.TakeTurn(self);
        Assert.InRange(self.Hp, 24, 38);
    }

    [Fact]
    public void SharedRecharge_OneRollRechargesThemAll_UsingOneSpendsThemAll()
    {
        var fire = TestStatBlocks.SaveAction("Fire Breath", "dex", 30, "1", "fire", new AreaSpec(K.Shapes.Cone, 30), usage: new UsageSpec(K.UsageKinds.Recharge, RechargeMin: 5, Pool: "recharge:Breath Weapons"));
        var sleep = TestStatBlocks.SaveAction("Sleep Breath", "con", 30, "", "poison", new AreaSpec(K.Shapes.Cone, 30), condition: Effect("unconscious", K.Durations.Rounds), usage: new UsageSpec(K.UsageKinds.Recharge, RechargeMin: 5, Pool: "recharge:Breath Weapons"));
        var dragon = TestStatBlocks.Create("Brass Dragon", 40, 5000, "1d4", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Poke", -10, "1", "bludgeoning"), fire, sleep], initiative: 20);

        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 5000, ac: 40, name: "Wall")], [SimKit.Monster(dragon)]);
        var brass = fight.Named("Brass Dragon");
        Assert.Single(brass.T.Limited);
        fight.TakeTurn(brass);
        Assert.False(brass.RechargeReady[0]); // one breath used: both spent

        // Over a fight: 1 + Binomial(19, 1/3) breaths in all, one recharge roll per turn for the pair.
        const int fights = 5_000;
        var report = Simulator.Run(SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 5000, ac: 40, name: "Wall")], [SimKit.Monster(dragon)], iterations: fights), 21);
        var used = report.Combatants[1].Resources.Single(r => r.Name == "Breath Weapons").MeanUsed;
        var se = Math.Sqrt(19 * (1.0 / 3) * (2.0 / 3) / fights);
        Assert.True(Math.Abs(used - (1 + (19.0 / 3))) <= 4 * se, $"{used}");
    }

    [Fact]
    public void AreaAutoHit_DamagesEveryoneInTheAreaWithoutARollOrASave()
    {
        var teleport = new StatBlockAction
        {
            Name = "Deathly Teleport",
            Kind = K.ActionKinds.AutoHit,
            Slot = K.ActionSlots.Action,
            Damage = [TestStatBlocks.Roll("2d10", "necrotic")],
            Area = new AreaSpec(K.Shapes.Emanation, 10),
            Text = "Each creature within 10 feet takes 11 (2d10) Necrotic damage.",
        };
        var lich = Monster("Lich", [teleport]);
        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 2)], [SimKit.Monster(lich)]);
        fight.TakeTurn(fight.Named("Lich"));
        var one = fight.Named("Fighter").TakenRaw;
        Assert.InRange(one, 2, 20);
        Assert.Equal(one, fight.Named("Fighter 2").TakenRaw); // one roll for the area
    }

    [Fact]
    public void UseActionsInTheActionSlot_UsesTheNamedAction()
    {
        var swallow = new StatBlockAction { Name = "Swallow", Kind = K.ActionKinds.UseActions, Slot = K.ActionSlots.Action, Uses = [new ActionUse("Bite", 2)], Text = "Swallow" };
        var toad = Monster("Giant Toad", [TestStatBlocks.Attack("Bite", 30, "1d10+2", "piercing"), swallow]);
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(toad)])).Setup);
        var log = new CombatLog();
        fight.Begin(2, log);
        fight.TakeTurn(fight.Named("Giant Toad"));
        var text = log.Finish(string.Empty);
        Assert.Equal(2, text.Split("Bite vs Fighter").Length - 1); // Swallow (two Bites) beats one Bite
    }

    [Fact]
    public void UseActionsCycle_IsCutOff()
    {
        var a = new StatBlockAction { Name = "A", Kind = K.ActionKinds.UseActions, Slot = K.ActionSlots.Action, Uses = [new ActionUse("B", 1)], Text = "A" };
        var b = new StatBlockAction { Name = "B", Kind = K.ActionKinds.UseActions, Slot = K.ActionSlots.Action, Uses = [new ActionUse("A", 1)], Text = "B" };
        var loop = Monster("Loop", [a, b, TestStatBlocks.Attack("Claw", 3, "1d4", "slashing")]);
        var report = Simulator.Run(SimKit.Spec([Fighter], [SimKit.Monster(loop)], iterations: 50), 1);
        Assert.Equal(50, report.Iterations);
    }

    [Fact]
    public void KitchenSink_EveryFeatureTogether_RunsClean()
    {
        var sink = TestStatBlocks.Create("Kitchen Sink", 16, 180, "20d10+70", (18, 14, 18, 14, 14, 16),
            [
                TestStatBlocks.Attack("Bite", 8, "2d8+4", "piercing", onHit:
                [
                    new ActionEffect { Kind = K.EffectKinds.Condition, Condition = Effect("restrained", K.Durations.UntilEscape, 15), ExtraConditions = [Effect("grappled", K.Durations.UntilEscape, 15)] },
                ]),
                TestStatBlocks.Attack("Ray", 7, "2d6", "fire", K.AttackRanges.Ranged) with { AttackRolls = 3, IsSpell = true, Magical = true },
                TestStatBlocks.SaveAction("Fire Breath", "dex", 16, "8d6", "fire", new AreaSpec(K.Shapes.Cone, 30), usage: new UsageSpec(K.UsageKinds.Recharge, RechargeMin: 5, Pool: "recharge:Breath Weapons")),
                TestStatBlocks.SaveAction("Stun Breath", "con", 16, "", "thunder", new AreaSpec(K.Shapes.Cone, 30), condition: Effect("stunned", K.Durations.UntilEndOfTargetTurn),
                    usage: new UsageSpec(K.UsageKinds.Recharge, RechargeMin: 5, Pool: "recharge:Breath Weapons")) with { ExtraConditions = [Effect("prone", K.Durations.UntilStands)] },
                new StatBlockAction { Name = "Blast", Kind = K.ActionKinds.AutoHit, Slot = K.ActionSlots.Action, Damage = [TestStatBlocks.Roll("2d10", "necrotic")], Area = new AreaSpec(K.Shapes.Emanation, 10), Usage = new UsageSpec(K.UsageKinds.PerDay, Uses: 1), Text = "Blast" },
                new StatBlockAction { Name = "Swallow", Kind = K.ActionKinds.UseActions, Slot = K.ActionSlots.Action, Uses = [new ActionUse("Bite", 1)], Text = "Swallow" },
                new StatBlockAction { Name = "Mend", Kind = K.ActionKinds.Heal, Slot = K.ActionSlots.Action, Healing = Domain.Features.DamageFormula.ParseDamage("4d8", "healing"), SelfOnly = true, Usage = new UsageSpec(K.UsageKinds.PerDay, Uses: 2), Text = "Mend" },
            ],
            multiattacks: [TestStatBlocks.Multiattack(("Bite", 1), ("Ray", 1))],
            traits:
            [
                new StatBlockTrait
                {
                    Name = "Fear Aura", Kind = K.TraitKinds.AuraDamage, Save = new SaveSpec("wis", 14, K.OnSuccess.None),
                    Condition = Effect("frightened", K.Durations.UntilStartOfTargetTurn), Area = new AreaSpec(K.Shapes.Emanation, 10),
                    Text = "Any enemy that starts its turn in the aura must succeed on a DC 14 Wisdom save or be frightened until the start of its next turn.",
                },
                TestStatBlocks.Trait("Magic Resistance", K.TraitKinds.MagicResistance),
                TestStatBlocks.Trait("Regeneration", K.TraitKinds.Regeneration, amount: 10, types: ["radiant"], text: "Regains 10 hit points at the start of its turn if it has at least 1 hit point."),
            ],
            legendary: new LegendaryActions(3, 4,
            [
                new StatBlockAction { Name = "Bite Attack", Kind = K.ActionKinds.UseActions, Slot = K.ActionSlots.Legendary, Uses = [new ActionUse("Bite", 1)], Text = "Bite Attack" },
                new StatBlockAction { Name = "Roar", Kind = K.ActionKinds.Save, Slot = K.ActionSlots.Legendary, Save = new SaveSpec("wis", 14, K.OnSuccess.None),
                    Condition = Effect("frightened", K.Durations.UntilEndOfTargetTurn), Area = new AreaSpec(K.Shapes.Emanation, 30), LegendaryCost = 2, OncePerRound = true, Text = "Roar" },
            ]),
            legendaryResistance: 3) with { Forms = ["2024/monster/kitchen-sink-other-form"] };

        var party = new List<SimulationCombatant>
        {
            SimKit.Pc(SimKit.Fighter2024, hp: 80, ac: 18, name: "Fighter", count: 3),
            SimKit.Pc("""
                { "name": "Cleric", "edition": "2024", "level": 9, "abilities": {"wis": 18},
                  "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
                  "modifiers": [{ "kind": "heal", "name": "Healing Word", "dice": "2d4", "amount": "wis", "action_cost": "bonus_action", "resource": {"uses": 3, "per": "long_rest"} },
                                { "kind": "save_effect", "name": "Hold Monster", "ability": "wis", "dc": 16, "condition": "paralyzed", "duration": "save_ends", "concentration": true, "resource": {"uses": 2, "per": "long_rest"} }] }
                """, hp: 60, ac: 18, name: "Cleric"),
        };
        var report = Simulator.Run(SimKit.Spec(party, [SimKit.Monster(sink)], iterations: 3_000, replay: 5, policies: new PolicySpec { Healing = "below_half" }), 31);
        Assert.Equal(3_000, report.PartyWins.Count + report.PartyDefeated.Count + report.Draw.Count);
        Assert.Contains("Summary of fight 5:", report.ReplayLog);
        var monster = report.Combatants.Single(c => c.Name == "Kitchen Sink");
        Assert.True(monster.Resources.Single(r => r.Name == "Breath Weapons").MeanUsed > 0.5);
        Assert.True(monster.LegendaryActions > 0);
    }
}
