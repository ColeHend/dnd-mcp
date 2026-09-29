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
    public void UntilEndOfTargetTurn_ImposedAsItsOwnTurnStarts_LastsThroughItsNextTurn()
    {
        // Imposed during the target's own turn (an aura as the turn starts), "until the end of its next turn" must not end
        // with this turn: it covers the next one and ends there.
        var ghast = Monster("Ghast", [TestStatBlocks.Attack("Claws", 5, "2d6+3", "slashing")], traits:
        [
            new StatBlockTrait
            {
                Name = "Stench",
                Kind = K.TraitKinds.AuraDamage,
                Save = new SaveSpec("con", 30, K.OnSuccess.None),
                Condition = Effect("poisoned", K.Durations.UntilEndOfTargetTurn),
                Area = new AreaSpec(K.Shapes.Emanation, 5),
                Text = "Any creature that starts its turn within 5 feet of the ghast must succeed on a DC 30 Constitution saving throw or be poisoned until the end of its next turn.",
            },
        ]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(ghast)]);
        var pc = fight.Named("Fighter");
        fight.TakeTurn(pc);
        Assert.True(pc.Has(Cond.Poisoned)); // this turn's end is not its next turn's

        fight.ApplyDamage(null, fight.Named("Ghast"), Scripted.Damage(1000), false, false, false, false, false);
        fight.TakeTurn(pc);
        Assert.False(pc.Has(Cond.Poisoned)); // ended with the next turn, and nothing renews it
    }

    [Fact]
    public void ConditionOnlyAura_ABackLinerIsOutOfReach()
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
                Text = "Any creature that starts its turn within 5 feet of the ghast must succeed on a DC 30 Constitution saving throw or be poisoned until the start of its next turn.",
            },
        ]);
        var back = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Back", position: "back");
        var fight = Scripted.Begin([Fighter, back], [SimKit.Monster(ghast)]);
        fight.StartOfTurn(fight.Named("Back"));
        Assert.False(fight.Named("Back").Has(Cond.Poisoned));
        fight.StartOfTurn(fight.Named("Fighter"));
        Assert.True(fight.Named("Fighter").Has(Cond.Poisoned));
    }

    [Fact]
    public void OnHitCondition_SizeLimit_IncludesThatSize()
    {
        // "Medium or smaller" grapples a Medium creature.
        var grabber = Monster("Grabber", [TestStatBlocks.Attack("Grab", 30, "1", "bludgeoning", onHit:
            [new ActionEffect { Kind = K.EffectKinds.Condition, Condition = Effect("restrained", K.Durations.UntilEscape, 30), MaxSize = "Medium" }])]);
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(grabber)], seed);
            var pc = fight.Named("Fighter");
            fight.TakeTurn(fight.Named("Grabber"));
            Assert.Equal(pc.TakenRaw > 0, pc.Has(Cond.Restrained));
        }
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
    public void SharedRecharge_ARoutineOverItsOwnSpells_SpendsItOnceAndCastsThemAll()
    {
        // 2024 pit fiend "Hellfire Spellcasting (Recharge 4-6). The pit fiend casts Fireball ... twice": the routine and the
        // spell share one recharge, which the routine spends once; both Fireballs land (DC 30: the fighter always fails).
        var recharge = new UsageSpec(K.UsageKinds.Recharge, RechargeMin: 4, Pool: "recharge:Hellfire Spellcasting");
        var fireball = TestStatBlocks.SaveAction("Fireball", "dex", 30, "1", "fire", new AreaSpec(K.Shapes.Sphere, 20), usage: recharge, spell: true);
        var hellfire = new StatBlockAction
        {
            Name = "Hellfire Spellcasting",
            Kind = K.ActionKinds.UseActions,
            Slot = K.ActionSlots.Action,
            Uses = [new ActionUse("Fireball", 2)],
            Usage = recharge,
            Text = "The pit fiend casts Fireball twice.",
        };
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(Monster("Fiend", [hellfire, fireball]))]);
        var fiend = fight.Named("Fiend");
        fight.TakeTurn(fiend);

        Assert.Equal((2, false), (fight.Named("Fighter").TakenRaw, fiend.RechargeReady[0]));
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
    public void PowerWordKill_2024_ACreatureWith100HitPointsOrFewerDies()
    {
        // 2024 "If the target has 100 Hit Points or fewer, it dies. Otherwise, it takes 12d12 Psychic damage": a 90-HP
        // fighter is dead, not dying (a dying PC is usually healed back up, so the kill moves "a party member dies").
        var lich = DndMcp.Tests.Srd.Combatants.CorrectedSrd.Shipped.StatBlock("2024", "lich");
        var powerWordKill = lich.Spells.Single(s => s.Name == "Power Word Kill");
        Assert.Equal(100, powerWordKill.KillAtOrBelowHp);
        var caster = lich with
        {
            Actions = [],
            Multiattacks = [],
            BonusActions = [],
            Reactions = [],
            Legendary = null,
            Spells = [powerWordKill],
        };
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 90, ac: 18, name: "Fighter")], [SimKit.Monster(caster)], seed);
            fight.TakeTurn(fight.Named("Lich"));
            Assert.True(fight.Named("Fighter").Dead, $"seed {seed}: a 90-HP creature survived Power Word Kill");
        }
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void KillAtOrBelowHp_AtOrBelowTheThresholdDies_AboveItTakesTheDamage(int hp, bool dies)
    {
        // The engine half of Power Word Kill on a hand-built block ("100 Hit Points or fewer, it dies. Otherwise, it takes
        // 12d12"): dead outright, no death saves; one hit point more and the damage applies as usual.
        var word = new StatBlockAction
        {
            Name = "Word",
            Kind = K.ActionKinds.AutoHit,
            Slot = K.ActionSlots.Action,
            Damage = [TestStatBlocks.Roll("1", "psychic")],
            KillAtOrBelowHp = 100,
            Text = "If the target has 100 Hit Points or fewer, it dies. Otherwise, it takes 1 Psychic damage.",
        };
        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: hp, ac: 18, name: "Fighter")], [SimKit.Monster(Monster("Speaker", [word]))]);
        fight.TakeTurn(fight.Named("Speaker"));

        var fighter = fight.Named("Fighter");
        Assert.Equal((dies, dies ? 0 : hp - 1), (fighter.Dead, fighter.Dead ? 0 : fighter.Hp));
    }

    [Fact]
    public void ImmuneAfterSuccess_ACreatureThatSaved_IsNotMadeToSaveAgainstThatActionAgain()
    {
        // "If a creature's saving throw is successful ... the creature is immune to the dragon's Frightful Presence for the
        // next 24 hours": once the fighter saves, the same creature's Presence never rolls against it again this fight.
        var presence = TestStatBlocks.SaveAction("Presence", "wis", 12, "1d6", "psychic", onSuccess: K.OnSuccess.None,
            condition: Effect("frightened", K.Durations.Rounds) with { Rounds = 10 }) with { ImmuneAfterSuccess = true };
        var checkedSeeds = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(Monster("Dragon", [presence]))])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            for (var turn = 0; turn < 4; turn++)
            {
                fight.TakeTurn(fight.Named("Dragon"));
            }

            var saves = log.Finish(string.Empty).Split('\n').Where(l => l.Contains("Fighter vs Presence:", StringComparison.Ordinal)).ToList();
            var first = saves.FindIndex(l => l.EndsWith("success", StringComparison.Ordinal));
            if (first < 0)
            {
                continue;
            }

            checkedSeeds++;
            Assert.Empty(saves.Skip(first + 1).Select(l => $"seed {seed}, after the success: {l.Trim()}"));
        }

        Assert.True(checkedSeeds > 0);
    }

    [Fact]
    public void ImmuneAfterSuccess_ARider_ACreatureThatSaved_IsNotMadeToSaveAgainstItAgain()
    {
        // The rider flag (ActionEffect.ImmuneAfterSuccess): a fear that rides every hit of a sure Claw. Once the fighter
        // saves (Wis +0 against DC 12), later hits still deal their damage but roll no save against it.
        var gaze = new ActionEffect
        {
            Kind = K.EffectKinds.Save,
            Save = new SaveSpec("wis", 12, K.OnSuccess.None),
            Condition = Effect("frightened", K.Durations.Rounds) with { Rounds = 10 },
            ImmuneAfterSuccess = true,
        };
        var claw = TestStatBlocks.Attack("Claw", 30, "1", "slashing", onHit: [gaze]);
        var checkedSeeds = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(Monster("Gazer", [claw]))])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            for (var turn = 0; turn < 4; turn++)
            {
                fight.TakeTurn(fight.Named("Gazer"));
            }

            var lines = log.Finish(string.Empty).Split('\n');
            var first = Array.FindIndex(lines, l => l.Contains("Fighter vs Claw:", StringComparison.Ordinal) && l.EndsWith("success", StringComparison.Ordinal));
            if (first < 0 || !lines.Skip(first + 1).Any(l => l.Contains("Claw vs Fighter", StringComparison.Ordinal) && l.EndsWith("hit", StringComparison.Ordinal)))
            {
                continue; // no success, or no hit after it
            }

            checkedSeeds++;
            Assert.Empty(lines.Skip(first + 1).Where(l => l.Contains("Fighter vs Claw:", StringComparison.Ordinal)).Select(l => $"seed {seed}, after the success: {l.Trim()}"));
        }

        Assert.True(checkedSeeds > 0);
    }

    [Fact]
    public void ImmuneAfterSuccess_TheConditionEnds_TheActionNoLongerAffectsIt()
    {
        // "If a creature's saving throw is successful or the effect ends for it, the creature is immune": a DC 30 Presence
        // always lands, frightened until the end of the fighter's next turn; once that turn has ended the fear, the
        // Presence never frightens it again.
        var presence = TestStatBlocks.SaveAction("Presence", "wis", 30, "", "psychic", onSuccess: K.OnSuccess.None,
            condition: Effect("frightened", K.Durations.UntilEndOfTargetTurn)) with { ImmuneAfterSuccess = true };
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(Monster("Dragon", [presence]))]);
        var dragon = fight.Named("Dragon");
        var pc = fight.Named("Fighter");
        fight.TakeTurn(dragon);
        Assert.True(pc.Has(Cond.Frightened));

        fight.TakeTurn(pc);
        Assert.False(pc.Has(Cond.Frightened)); // its turn ended the fear

        fight.TakeTurn(dragon);
        Assert.False(pc.Has(Cond.Frightened));
        Assert.True(pc.IsImmuneToEffect(dragon.Id, dragon.T.Actions.Single().ImmunityIndex));
    }

    [Fact]
    public void ImmuneAfterSuccess_AgainstAnImmuneTarget_TheMonsterUsesAnotherAction()
    {
        // The AI values the action at nothing against a creature immune to it: once the fighter has shaken the Presence
        // off, the dragon pokes rather than spend its turn on an effect that cannot work (before, the Presence's fear —
        // a quarter of the fighter's threat — outranked the 1-damage Poke).
        var presence = TestStatBlocks.SaveAction("Presence", "wis", 30, "", "psychic", onSuccess: K.OnSuccess.None,
            condition: Effect("frightened", K.Durations.UntilEndOfTargetTurn)) with { ImmuneAfterSuccess = true };
        var dragon = Monster("Dragon", [TestStatBlocks.Attack("Poke", 30, "1", "piercing"), presence]);
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(dragon)])).Setup);
        var log = new CombatLog(1_000_000);
        fight.Begin(1, log);
        fight.TakeTurn(fight.Named("Dragon"));
        Assert.True(fight.Named("Fighter").Has(Cond.Frightened)); // the Presence first
        fight.TakeTurn(fight.Named("Fighter"));

        var before = log.Finish(string.Empty).Length;
        fight.TakeTurn(fight.Named("Dragon"));
        var after = log.Finish(string.Empty)[before..];
        Assert.Contains("Poke vs Fighter", after);
        Assert.DoesNotContain("Presence", after);
    }

    [Fact]
    public void ImmuneAfterSuccess_ARiderAgainstAnImmuneTarget_TheMonsterUsesAnotherAttack()
    {
        // The rider half of the AI's immunity: the Gaze's fear (DC 30: it always lands, until the end of the fighter's next
        // turn) outranks the Poke's extra point of damage until it has ended once; from then on the fighter is immune to
        // it, the Gaze is worth its 1 damage alone, and the dragon pokes.
        var fear = new ActionEffect
        {
            Kind = K.EffectKinds.Save,
            Save = new SaveSpec("wis", 30, K.OnSuccess.None),
            Condition = Effect("frightened", K.Durations.UntilEndOfTargetTurn),
            ImmuneAfterSuccess = true,
        };
        var dragon = Monster("Dragon", [TestStatBlocks.Attack("Gaze", 30, "1", "psychic", onHit: [fear]), TestStatBlocks.Attack("Poke", 30, "2", "piercing")]);
        var checkedSeeds = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(dragon)])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            fight.TakeTurn(fight.Named("Dragon"));
            Assert.Contains("Gaze vs Fighter", log.Finish(string.Empty)); // the Gaze first
            if (!fight.Named("Fighter").Has(Cond.Frightened))
            {
                continue; // a natural 1
            }

            checkedSeeds++;
            fight.TakeTurn(fight.Named("Fighter")); // the fear ends: immune from now on
            var before = log.Finish(string.Empty).Length;
            fight.TakeTurn(fight.Named("Dragon"));
            var after = log.Finish(string.Empty)[before..];
            Assert.Contains("Poke vs Fighter", after);
            Assert.DoesNotContain("Gaze", after);
        }

        Assert.True(checkedSeeds > 0);
    }

    [Fact]
    public void ImmuneAfterSuccess_ASingleTargetEffect_IsAimedAtACreatureNotImmuneToIt()
    {
        // The caster chooses its target: of two fighters, one already immune to the Glare, the Glare goes at the other every
        // time (under the spread policy a coin flip would otherwise waste it on the immune one).
        var glare = TestStatBlocks.SaveAction("Glare", "wis", 30, "1", "psychic", onSuccess: K.OnSuccess.None) with { ImmuneAfterSuccess = true };
        var party = new[] { SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "A"), SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "B") };
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec(party, [SimKit.Monster(Monster("Gazer", [glare]))])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            var gazer = fight.Named("Gazer");
            fight.Named("A").BecomeImmuneToEffect(gazer.Id, gazer.T.Actions.Single().ImmunityIndex);
            fight.TakeTurn(gazer);

            Assert.DoesNotContain("immune", log.Finish(string.Empty));
            Assert.Equal((0, 1), (fight.Named("A").TakenRaw, fight.Named("B").TakenRaw));
        }
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void ImmuneAfterSuccess_AnAreaThatCouldReachOnlyImmuneCreatures_IsNotUsed(int immune, bool used)
    {
        // "The dragon can use its Frightful Presence", a step of its Multiattack: once every creature it could reach is
        // immune, the step is skipped (no area drawn, no line for each immune creature) and the Claw still comes. With one
        // fighter still open to it, the Presence goes on as before.
        var presence = TestStatBlocks.SaveAction("Presence", "wis", 12, "", "psychic", new AreaSpec(K.Shapes.Sphere, 60), onSuccess: K.OnSuccess.None,
            condition: Effect("frightened", K.Durations.Rounds) with { Rounds = 10 }) with { ImmuneAfterSuccess = true };
        var dragon = TestStatBlocks.Create("Dragon", 12, 500, "10d10", (14, 12, 14, 10, 10, 10), [presence, TestStatBlocks.Attack("Claw", 30, "1", "slashing")],
            multiattacks: [TestStatBlocks.Multiattack(("Presence", 1), ("Claw", 1))], initiative: -5);
        var party = new[] { SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "A"), SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "B") };
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec(party, [SimKit.Monster(dragon)])).Setup);
        var log = new CombatLog(1_000_000);
        fight.Begin(1, log);
        var source = fight.Named("Dragon");
        foreach (var name in new[] { "A", "B" }.Take(immune))
        {
            fight.Named(name).BecomeImmuneToEffect(source.Id, source.T.Actions.Single(a => a.Name == "Presence").ImmunityIndex);
        }

        fight.TakeTurn(source);
        var text = log.Finish(string.Empty);
        Assert.Equal(used, text.Contains("Presence (DC 12 wis) on", StringComparison.Ordinal));
        Assert.Equal(used ? 1 : 0, text.Split('\n').Count(l => l.Contains("is immune to Dragon's Presence", StringComparison.Ordinal)));
        Assert.Contains("Claw vs", text);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(200, false)]
    public void KillAtOrBelowHp_ASaveRider_AFailedSaveKillsAtOrBelowTheThreshold(int hp, bool dies)
    {
        // The rider half (ActionEffect.KillAtOrBelowHp), as the 2014 solar's Slaying Longbow: "If the target is a creature
        // that has 100 hit points or fewer, it must succeed on a DC 15 Constitution saving throw or die." At DC 30 the
        // fighter (Con +3) always fails: at 90 HP it dies outright; at 200 the rider does nothing past the arrow's 1.
        var slaying = new ActionEffect { Kind = K.EffectKinds.Save, Save = new SaveSpec("con", 30, K.OnSuccess.None), KillAtOrBelowHp = 100 };
        var bow = TestStatBlocks.Attack("Slaying Longbow", 30, "1", "piercing", K.AttackRanges.Ranged, onHit: [slaying]);
        var hits = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: hp, ac: 18, name: "Fighter")], [SimKit.Monster(Monster("Solar", [bow]))], seed);
            var solar = fight.Named("Solar");
            fight.TakeTurn(solar);
            var pc = fight.Named("Fighter");
            if (pc.TakenRaw == 0 && !pc.Dead)
            {
                continue; // a natural 1
            }

            hits++;
            Assert.Equal((dies, dies ? 0 : hp - 1), (pc.Dead, pc.Hp));
            Assert.Equal(dies ? 1 : 0, solar.Kills);
        }

        Assert.True(hits > 0);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void KillAtOrBelowHp_ASaveAction_AFailedSaveKillsInsteadOfDealingItsDamage(int hp, bool dies)
    {
        // The 2024 solar's Slaying Bow: "Failure: If the creature has 100 Hit Points or fewer, it dies. It otherwise takes
        // 24 (4d8 + 6) Piercing damage plus 36 (8d8) Radiant damage." At DC 30 the fighter (Dex +1) always fails: at 100 HP
        // it dies and none of the 10 is dealt or taken; at 101 it takes the 10 and lives.
        var bow = TestStatBlocks.SaveAction("Slaying Bow", "dex", 30, "10", "piercing", onSuccess: K.OnSuccess.None) with { KillAtOrBelowHp = 100 };
        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: hp, ac: 18, name: "Fighter")], [SimKit.Monster(Monster("Solar", [bow]))]);
        var solar = fight.Named("Solar");
        fight.TakeTurn(solar);

        var pc = fight.Named("Fighter");
        Assert.Equal((dies, dies ? 0 : 10, dies ? 0 : 10, dies ? 1 : 0), (pc.Dead, pc.TakenRaw, solar.DealtRaw, solar.Kills));
    }

    [Theory]
    [InlineData(K.ActionKinds.AutoHit)]
    [InlineData(K.ActionKinds.Save)]
    [InlineData(K.ActionKinds.Attack)]
    public void KillAtOrBelowHp_TheKillIsRankedAtTheHitPointsItTakes(string kind)
    {
        // What lets an outright kill be chosen at all: the 2014 Power Word Kill does nothing above 100 hit points, so its own
        // damage (none for the auto-hit, 1 for the others) is worth less than a 5-damage option of the same kind. Ranked at
        // the 90 hit points it takes from the fighter, the kill is used, and (when it lands: an attack can miss) kills.
        var (kill, other) = kind switch
        {
            K.ActionKinds.AutoHit => (
                new StatBlockAction { Name = "Word", Kind = kind, Slot = K.ActionSlots.Action, Damage = [], KillAtOrBelowHp = 100, Text = "Word" },
                new StatBlockAction { Name = "Bolt", Kind = kind, Slot = K.ActionSlots.Action, Damage = [TestStatBlocks.Roll("5", "force")], Text = "Bolt" }),
            K.ActionKinds.Save => (
                TestStatBlocks.SaveAction("Word", "con", 30, "1", "necrotic", onSuccess: K.OnSuccess.None) with { KillAtOrBelowHp = 100 },
                TestStatBlocks.SaveAction("Bolt", "con", 30, "5", "fire", onSuccess: K.OnSuccess.None)),
            _ => (TestStatBlocks.Attack("Word", 30, "1", "piercing") with { KillAtOrBelowHp = 100 }, TestStatBlocks.Attack("Bolt", 30, "5", "piercing")),
        };
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 90, ac: 18, name: "Fighter")], [SimKit.Monster(Monster("Speaker", [other, kill]))])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            fight.TakeTurn(fight.Named("Speaker"));

            var text = log.Finish(string.Empty);
            Assert.Contains("Word", text);
            Assert.DoesNotContain("Bolt", text);
            var fighter = fight.Named("Fighter");
            Assert.True(fighter.Dead || (kind == K.ActionKinds.Attack && text.Contains("→ miss", StringComparison.Ordinal)), $"seed {seed}: {text}");
        }
    }

    [Theory]
    [InlineData(SimulationValues.Targeting.Threat)]
    [InlineData(SimulationValues.Targeting.Spread)]
    [InlineData(SimulationValues.Targeting.FocusFire)]
    public void PowerWordKill_2014_IsAimedAtACreatureItKills(string policy)
    {
        // 2014 "If the creature you choose has 100 hit points or fewer, it dies. Otherwise, the spell has no effect": the
        // lich chooses the 80-HP wizard, not the 200-HP tank that its side's policy would pick (threat: the first of two
        // equal threats; spread: either, at random), on whom the spell does nothing.
        var lich = DndMcp.Tests.Srd.Combatants.CorrectedSrd.Shipped.StatBlock("2014", "lich");
        var powerWordKill = lich.Spells.Single(s => s.Name == "Power Word Kill");
        var caster = lich with { Actions = [], Multiattacks = [], BonusActions = [], Reactions = [], Legendary = null, Spells = [powerWordKill] };
        var party = new[] { SimKit.Pc(SimKit.Fighter2024, hp: 200, ac: 18, name: "Tank"), SimKit.Pc(SimKit.Fighter2024, hp: 80, ac: 14, name: "Wizard") };
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = Scripted.Begin(party, [SimKit.Monster(caster)], seed, policies: new PolicySpec { Enemies = policy });
            fight.TakeTurn(fight.Named("Lich"));
            Assert.True(fight.Named("Wizard").Dead, $"seed {seed}: the wizard lives");
            Assert.Equal(200, fight.Named("Tank").Hp);
        }
    }

    [Theory]
    [InlineData(K.ActionKinds.AutoHit, SimulationValues.Targeting.Threat)]
    [InlineData(K.ActionKinds.AutoHit, SimulationValues.Targeting.Spread)]
    [InlineData(K.ActionKinds.AutoHit, SimulationValues.Targeting.FocusFire)]
    [InlineData(K.ActionKinds.Save, SimulationValues.Targeting.Threat)]
    [InlineData(K.ActionKinds.Save, SimulationValues.Targeting.Spread)]
    [InlineData(K.ActionKinds.Save, SimulationValues.Targeting.FocusFire)]
    public void KillAtOrBelowHp_ASingleTargetKill_IsAimedAndValuedAtACreatureItKills(string kind, string policy)
    {
        // The kill goes to the 80-HP wizard (it does nothing to the 200-HP tank), so it is worth the wizard's 80 hit points
        // and outranks a 60-damage Blast; averaged over both fighters it looked worth 40 and lost to the Blast. A save
        // (DC 30: both always fail) is aimed the same way as an auto-hit.
        var (word, blast) = kind == K.ActionKinds.AutoHit
            ? (new StatBlockAction { Name = "Word", Kind = kind, Slot = K.ActionSlots.Action, Damage = [], KillAtOrBelowHp = 100, Text = "Word" },
               new StatBlockAction { Name = "Blast", Kind = kind, Slot = K.ActionSlots.Action, Damage = [TestStatBlocks.Roll("60", "force")], Text = "Blast" })
            : (TestStatBlocks.SaveAction("Word", "con", 30, "", "necrotic", onSuccess: K.OnSuccess.None) with { KillAtOrBelowHp = 100 },
               TestStatBlocks.SaveAction("Blast", "con", 30, "60", "force", onSuccess: K.OnSuccess.None));
        var party = new[] { SimKit.Pc(SimKit.Fighter2024, hp: 200, ac: 18, name: "Tank"), SimKit.Pc(SimKit.Fighter2024, hp: 80, ac: 14, name: "Wizard") };
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin(party, [SimKit.Monster(Monster("Speaker", [blast, word]))], seed, policies: new PolicySpec { Enemies = policy });
            fight.TakeTurn(fight.Named("Speaker"));
            Assert.True(fight.Named("Wizard").Dead, $"seed {seed}: the wizard lives");
            Assert.Equal(0, fight.Named("Tank").TakenRaw + fight.Named("Wizard").TakenRaw);
        }
    }

    [Fact]
    public void KillAtOrBelowHp_UnderFinishDowned_IsAimedAtAStandingCreatureNotADyingOne()
    {
        // A dying fighter is a candidate under finish_downed, and focus fire's first (0 HP), but it is out of the fight
        // already and the kill would take nothing: the word goes to the standing 80-HP wizard, not the Poke to the dying.
        var word = new StatBlockAction { Name = "Word", Kind = K.ActionKinds.AutoHit, Slot = K.ActionSlots.Action, Damage = [], KillAtOrBelowHp = 100, Text = "Word" };
        var party = new[]
        {
            SimKit.Pc(SimKit.Fighter2024, hp: 200, ac: 18, name: "Tank"),
            SimKit.Pc(SimKit.Fighter2024, hp: 80, ac: 14, name: "Wizard"),
            SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Dying"),
        };
        var speaker = Monster("Speaker", [TestStatBlocks.Attack("Poke", 30, "1", "piercing"), word]);
        var fight = Scripted.Begin(party, [SimKit.Monster(speaker)], policies: new PolicySpec { Enemies = SimulationValues.Targeting.FocusFire, FinishDowned = true });
        var dying = fight.Named("Dying");
        fight.ApplyDamage(null, dying, Scripted.Damage(44), false, false, false, false, false);
        Assert.True(dying.Down);

        fight.TakeTurn(fight.Named("Speaker"));
        Assert.True(fight.Named("Wizard").Dead);
        Assert.False(dying.Dead);
    }

    [Fact]
    public void KillAtOrBelowHp_LegendaryResistance_IsSpentOnAFailedSaveThatWouldKill()
    {
        // A failure that kills matters as much as a condition that does (the "conditions" policy): a 90-HP legendary
        // creature spends its one use against a DC 30 kill that carries neither damage nor a condition, and lives.
        var doom = TestStatBlocks.SaveAction("Doom", "con", 30, "", "necrotic", onSuccess: K.OnSuccess.None) with { KillAtOrBelowHp = 100 };
        var fight = Scripted.Begin([SimKit.Monster(TestStatBlocks.Sandbag("Legend", hp: 90, legendaryResistance: 1))], [SimKit.Monster(Monster("Slayer", [doom]))]);
        fight.TakeTurn(fight.Named("Slayer"));

        var legend = fight.Named("Legend");
        Assert.Equal((false, 1), (legend.Dead, legend.LegendaryResistanceSpent));
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
