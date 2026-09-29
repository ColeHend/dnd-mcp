using DndMcp.Domain.Simulation;
using Xunit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Rules whose check is a count or a probability over whole fights run by <see cref="Simulator.Run"/>, read back from the
/// report: recharge, legendary actions (cadence, costs, once per round), the Legendary Resistance policies, and the AoE's
/// shared damage roll. Fixed seeds; tolerances stated per test.
/// </summary>
public sealed class SimulationStatisticalRulesTests
{
    /// <summary>A party member who cannot be hurt much and cannot end the fight: fights run to the round cap.</summary>
    private static SimulationCombatant Wall(string name = "Wall") => SimKit.Pc(SimKit.Fighter2024, hp: 5000, ac: 40, name: name);

    private static StatBlock Boss(LegendaryActions? legendary = null, int legendaryResistance = 0, IReadOnlyList<StatBlockAction>? actions = null) =>
        TestStatBlocks.Create("Boss", 40, 5000, "1d4", (10, 10, 10, 10, 10, 10),
            actions ?? [TestStatBlocks.Attack("Poke", -10, "1", "bludgeoning")], legendary: legendary, legendaryResistance: legendaryResistance, initiative: 20);

    [Theory]
    [InlineData(5, 1.0 / 3)]
    [InlineData(6, 1.0 / 6)]
    [InlineData(4, 1.0 / 2)]
    public void Recharge_RechargesWithProbabilityOfTheFacesThatRecharge(int rechargeMin, double p)
    {
        // Ready on turn 1, used whenever ready, and a spent one recharges on d6 ≥ min at each later start of turn:
        // uses = 1 + Binomial(19, p) over 20 turns. Tolerance 4 SE of that binomial over 10,000 fights.
        var breath = TestStatBlocks.SaveAction("Breath", "dex", 30, "1", "fire", new AreaSpec(K.Shapes.Cone, 30),
            usage: new UsageSpec(K.UsageKinds.Recharge, RechargeMin: rechargeMin));
        var boss = Boss(actions: [TestStatBlocks.Attack("Poke", -10, "1", "bludgeoning"), breath]);
        const int fights = 10_000;
        var report = Simulator.Run(SimKit.Spec([Wall()], [SimKit.Monster(boss)], iterations: fights), 11);
        Assert.Equal(fights, report.Draw.Count);

        var used = report.Combatants[1].Resources.Single(r => r.Name == "Breath").MeanUsed;
        var expected = 1 + (19 * p);
        var se = Math.Sqrt(19 * p * (1 - p) / fights);
        Assert.True(Math.Abs(used - expected) <= 4 * se, $"mean uses {used}, expected {expected} ± {se}");
    }

    [Theory]
    [InlineData(1, false, 60)]
    [InlineData(2, false, 20)]
    [InlineData(1, true, 20)]
    public void LegendaryActions_OnePerOtherTurn_WhileUsesLast_CostsAndOncePerRound(int cost, bool oncePerRound, int perFight)
    {
        // The boss acts first each round (initiative +20) with 3 uses; four party members' turns follow. Cost 1: one after
        // each of the first three turns → 3 a round; cost 2: one a round (3 − 2 = 1 left); once per round: one a round.
        var action = new StatBlockAction
        {
            Name = "Tail Attack",
            Kind = K.ActionKinds.UseActions,
            Slot = K.ActionSlots.Legendary,
            Uses = [new ActionUse("Poke", 1)],
            LegendaryCost = cost,
            OncePerRound = oncePerRound,
            Text = "Tail Attack",
        };
        var boss = Boss(legendary: new LegendaryActions(3, null, [action]));
        var report = Simulator.Run(SimKit.Spec([Wall("A"), Wall("B"), Wall("C"), Wall("D")], [SimKit.Monster(boss)], iterations: 200), 3);
        Assert.Equal(200, report.Draw.Count);
        Assert.Equal(perFight, report.Combatants[4].LegendaryActions);
    }

    [Fact]
    public void LegendaryActions_NeverAtTheEndOfItsOwnTurn()
    {
        // Legendary actions come at the end of ANOTHER creature's turn: with one wall to act, one a round (20 in a
        // 20-round fight), though three uses would allow another after the boss's own turn.
        var action = new StatBlockAction
        {
            Name = "Tail Attack",
            Kind = K.ActionKinds.UseActions,
            Slot = K.ActionSlots.Legendary,
            Uses = [new ActionUse("Poke", 1)],
            LegendaryCost = 1,
            Text = "Tail Attack",
        };
        var boss = Boss(legendary: new LegendaryActions(3, null, [action]));
        var report = Simulator.Run(SimKit.Spec([Wall()], [SimKit.Monster(boss)], iterations: 100), 3);
        Assert.Equal(100, report.Draw.Count);
        Assert.Equal(20, report.Combatants[1].LegendaryActions);
    }

    [Theory]
    [InlineData("conditions", true, 3)]
    [InlineData("never", true, 0)]
    [InlineData("conditions", false, 0)]
    [InlineData("always", false, 3)]
    public void LegendaryResistance_SpentPerPolicy(string policy, bool condition, double spent)
    {
        // DC 30 against a +0 save always fails. "conditions" spends a use only when the failure would paralyze (or kill);
        // a damage-only effect (1d4 against 5,000 HP) is let through; "always" spends all three either way.
        var effect = condition
            ? """{ "kind": "save_effect", "name": "Hold", "ability": "wis", "dc": 30, "condition": "paralyzed" }"""
            : """{ "kind": "save_effect", "name": "Zap", "ability": "wis", "dc": 30, "dice": "1d4", "type": "fire" }""";
        var caster = SimKit.Pc($$"""{ "name": "Caster", "edition": "2024", "level": 5, "abilities": {"wis": 16}, "modifiers": [{{effect}}] }""", hp: 5000, ac: 40);
        var report = Simulator.Run(SimKit.Spec([caster], [SimKit.Monster(Boss(legendaryResistance: 3))], iterations: 100, roundCap: 10,
            policies: new PolicySpec { LegendaryResistance = policy }), 4);
        Assert.Equal(spent, report.Combatants[1].LegendaryResistanceSpent);
    }

    [Fact]
    public void LegendaryResistance_Conditions_SpentOnADamageOnlyKillingBlow()
    {
        // A 10 HP boss failing a DC 30 save against exactly 10 damage would drop to 0: "conditions" spends a use on it
        // (a success halves it to 5), though the effect imposes no condition; at 5 HP the next 10 would drop it too, so a
        // second use goes, and the second halved 5 ends it: exactly two a fight.
        var fragile = TestStatBlocks.Create("Boss", 40, 10, "1d4", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Poke", -10, "1", "bludgeoning")], legendaryResistance: 3, initiative: 20);
        var caster = SimKit.Pc("""{ "name": "Caster", "edition": "2024", "level": 5, "abilities": {"wis": 16}, "modifiers": [{ "kind": "save_effect", "name": "Bolt", "ability": "dex", "dc": 30, "dice": "10", "type": "lightning" }] }""", hp: 5000, ac: 40);
        var report = Simulator.Run(SimKit.Spec([caster], [SimKit.Monster(fragile)], iterations: 50), 5);
        Assert.Equal(2.0, report.Combatants[1].LegendaryResistanceSpent);
    }

    [Fact]
    public void LegendaryResistance_Conditions_NotSpentOnAConditionItAlreadyHas()
    {
        // A DC 30 Wis save always fails; the boss is already frightened, so failing changes nothing and keeps its uses.
        var caster = SimKit.Pc("""{ "name": "Caster", "edition": "2024", "level": 5, "abilities": {"wis": 16}, "modifiers": [{ "kind": "save_effect", "name": "Scare", "ability": "wis", "dc": 30, "condition": "frightened" }] }""", hp: 5000, ac: 40, name: "Caster");
        var scared = Scripted.Begin([caster], [SimKit.Monster(Boss(legendaryResistance: 3))]);
        var target = scared.Named("Boss");
        scared.AddCondition(null, target, new ConditionTemplate { Condition = Cond.Frightened, Duration = DurationKind.Fight }, 0);
        scared.TakeTurn(scared.Named("Caster"));
        Assert.Equal(0, target.LegendaryResistanceSpent);
        Assert.Equal(3, target.LegendaryResistanceLeft);
    }

    [Fact]
    public void PerDayUses_OncePerDayMeansOnce_AndOneCastOfSeveralRollsIsOneUse()
    {
        // A 1/day blast and a 2/day three-ray spell, each better than the poke: used exactly 1 and 2 times in a fight.
        var blast = TestStatBlocks.SaveAction("Blast", "dex", 30, "20", "fire", area: new AreaSpec(K.Shapes.Sphere, 5), usage: new UsageSpec(K.UsageKinds.PerDay, Uses: 1));
        var rays = TestStatBlocks.Attack("Rays", 5, "2d6", "fire", range: K.AttackRanges.Ranged) with { AttackRolls = 3, Usage = new UsageSpec(K.UsageKinds.PerDay, Uses: 2), IsSpell = true };
        var boss = Boss(actions: [TestStatBlocks.Attack("Poke", -10, "1", "bludgeoning"), blast, rays]);
        var report = Simulator.Run(SimKit.Spec([Wall()], [SimKit.Monster(boss)], iterations: 100), 7);
        Assert.Equal(1.0, report.Combatants[1].Resources.Single(r => r.Name == "Blast").MeanUsed);
        Assert.Equal(2.0, report.Combatants[1].Resources.Single(r => r.Name == "Rays").MeanUsed);
    }

    [Fact]
    public void AreaDamage_IsRolledOnceForAllTargets_FireballAgainstFourGoblins()
    {
        // Contract §9 (research A11): Fireball (2014, DC 15, 8d6 fire) on 4 goblins (Dex +2, 7 HP). One shared roll:
        // P(all four die) = 0.999333059; independent rolls per goblin would give 0.998775. 400,000 casts (four runs of
        // 100,000, seeds 1–4): the 99.9% Wilson interval is about ±0.00014, so it holds the shared-roll value and excludes
        // the independent one (4 × its half-width away). Raw damage per cast 89.2 and effective 27.998608, within 4 SE.
        const string wizard = """
            { "name": "Wizard", "edition": "2014", "level": 5, "abilities": {"int": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Fireball", "ability": "dex", "dc": 15, "dice": "8d6", "type": "fire", "shape": "sphere", "size": 20 }] }
            """;
        long wins = 0, fights = 0;
        var raw = new List<MeanEstimate>();
        var effective = new List<MeanEstimate>();
        for (ulong seed = 1; seed <= 4; seed++)
        {
            // 2014 surprise: the goblins lose their first turn, so round 1 is exactly one Fireball; the cap ends it there.
            var report = Simulator.Run(SimKit.Spec([SimKit.Pc(wizard, hp: 30, ac: 12)], [SimKit.Monster(TestStatBlocks.Goblin, count: 4)],
                iterations: 100_000, roundCap: 1, edition: "2014", surprise: "enemies"), seed);
            wins += report.PartyWins.Count;
            fights += report.Iterations;
            raw.Add(report.Combatants[0].DamageDealt);
            effective.Add(report.Combatants[0].DamageDealtEffective);
        }

        var allDie = SimulationStatistics.Wilson(wins, fights, SimulationStatistics.Z999);
        Assert.InRange(0.999333059, allDie.Low, allDie.High);
        Assert.True(allDie.Low > 0.998775, $"P(all die) {allDie.Estimate} [{allDie.Low}, {allDie.High}]");

        var rawMean = raw.Average(m => m.Mean);
        var rawSe = Math.Sqrt(raw.Sum(m => m.StandardError * m.StandardError)) / raw.Count;
        Assert.True(Math.Abs(rawMean - 89.2) <= 4 * rawSe, $"raw {rawMean} ± {rawSe}");
        var effectiveMean = effective.Average(m => m.Mean);
        var effectiveSe = Math.Sqrt(effective.Sum(m => m.StandardError * m.StandardError)) / effective.Count;
        Assert.True(Math.Abs(effectiveMean - 27.998608) <= 4 * effectiveSe, $"effective {effectiveMean} ± {effectiveSe}");
    }

    [Fact]
    public void RolledEnemyHp_VariesAroundTheAverage()
    {
        var average = Simulator.Run(SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18)], [SimKit.Monster(TestStatBlocks.Ogre)], iterations: 2_000), 9);
        Assert.Equal(59, average.Combatants[1].MaxHp);

        var rolled = Simulator.Run(SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18)], [SimKit.Monster(TestStatBlocks.Ogre)], iterations: 2_000, enemyHp: "roll"), 9);
        // 7d10+21: mean 59.5, SD 7.6; over 2,000 fights the mean is within ±0.7 (4 SE).
        Assert.InRange(rolled.Combatants[1].MaxHp, 58.8, 60.2);
        Assert.Equal("roll", rolled.EnemyHp);
    }
}
