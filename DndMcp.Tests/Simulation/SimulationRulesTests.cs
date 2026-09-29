using DndMcp.Domain.Simulation;
using Xunit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// The simulator's rules (contract §5.4), each in a small scripted fight driven through the engine's own methods, or
/// over many seeds where the rule is a probability. Seeds are fixed, so every pass is reproducible.
/// </summary>
public sealed class SimulationRulesTests
{
    private static readonly SimulationCombatant Fighter = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter");

    /// <summary>A build that always hits (to_hit total 30, a natural 1 still misses) for 1d6 slashing.</summary>
    private static string Sure(string modifiers = "", string edition = "2024", int count = 1) => $$"""
        { "name": "Sure", "edition": "{{edition}}", "level": 5, "abilities": {"str": 10, "wis": 20},
          "attacks": [{ "name": "Blade", "count": {{count}}, "to_hit": {"total": 30}, "damage": "1d6", "damage_type": "slashing", "properties": ["melee"] }],
          "modifiers": [{{modifiers}}] }
        """;

    // ------------------------------------------------------------------------------------------------------------------
    // Initiative and surprise.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Initiative_CopiesOfOneEntry_ShareOneRollAndActTogether()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Goblin, count: 3)], seed);
            var goblins = fight.Creatures.Where(c => c.Side == 1).ToList();
            Assert.Single(goblins.Select(g => g.Initiative).Distinct());
            var positions = goblins.Select(g => fight.Order.ToList().IndexOf(g.Id)).Order().ToList();
            Assert.Equal(positions[0] + 2, positions[2]);
        }
    }

    [Theory]
    [InlineData(5, 2, 0)]
    [InlineData(2, 5, 1)]
    public void Initiative_TiedTotals_HigherModifierGoesFirst(int partyBonus, int enemyBonus, int firstSide)
    {
        var party = new SimulationCombatant(new CombatantSpec { Name = "P", Build = SimKit.Build(SimKit.Fighter2024), Hp = 44, Ac = 18, InitiativeBonus = partyBonus });
        var enemy = new SimulationCombatant(new CombatantSpec { Name = "E", Monster = "Ogre", InitiativeBonus = enemyBonus }, TestStatBlocks.Ogre);
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([party], [enemy])).Setup);
        var ties = 0;
        for (ulong seed = 1; seed <= 400; seed++)
        {
            fight.Begin(seed);
            if (fight.Named("P").Initiative == fight.Named("E").Initiative)
            {
                ties++;
                Assert.Equal(firstSide, fight.Creatures[fight.Order[0]].Side);
            }
        }

        Assert.True(ties > 5, $"only {ties} ties");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Initiative_TiesWithEqualModifiers_PcsWinTiesSettlesThem(bool pcsWinTies)
    {
        var enemy = new SimulationCombatant(new CombatantSpec { Name = "E", Monster = "Ogre", InitiativeBonus = 0 }, TestStatBlocks.Ogre);
        var party = new SimulationCombatant(new CombatantSpec { Name = "P", Build = SimKit.Build(SimKit.Fighter2024), Hp = 44, Ac = 18, InitiativeBonus = 0 });
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([party], [enemy], policies: new PolicySpec { PcsWinTies = pcsWinTies })).Setup);
        var partyFirst = 0;
        var ties = 0;
        for (ulong seed = 1; seed <= 1_000; seed++)
        {
            fight.Begin(seed);
            if (fight.Named("P").Initiative == fight.Named("E").Initiative)
            {
                ties++;
                partyFirst += fight.Creatures[fight.Order[0]].Side == 0 ? 1 : 0;
            }
        }

        Assert.True(ties > 20);
        if (pcsWinTies)
        {
            Assert.Equal(ties, partyFirst);
        }
        else
        {
            // A seeded roll-off: both orders happen.
            Assert.InRange(partyFirst, 1, ties - 1);
        }
    }

    [Fact]
    public void Surprise_2014_SurprisedEnemiesLoseTheirFirstTurn()
    {
        var spec = SimKit.Spec([Fighter], [SimKit.Monster(TestStatBlocks.Goblin, count: 4)], iterations: 2_000, roundCap: 1, edition: "2014", surprise: "enemies");
        var surprised = Simulator.Run(spec, 5);
        Assert.Equal(0, surprised.Combatants[0].DamageTaken.Mean);

        var alert = Simulator.Run(SimKit.Spec([Fighter], [SimKit.Monster(TestStatBlocks.Goblin, count: 4)], iterations: 2_000, roundCap: 1, edition: "2014"), 5);
        Assert.True(alert.Combatants[0].DamageTaken.Mean > 1);
    }

    [Fact]
    public void Surprise_2024_SurprisedEnemiesStillTakeTheirFirstTurn()
    {
        // 2024 surprise is only Disadvantage on initiative: in a one-round fight the goblins still attack (2014: they don't).
        var spec = SimKit.Spec([Fighter], [SimKit.Monster(TestStatBlocks.Goblin, count: 4)], iterations: 2_000, roundCap: 1, edition: "2024", surprise: "enemies");
        Assert.True(Simulator.Run(spec, 5).Combatants[0].DamageTaken.Mean > 1);
    }

    [Fact]
    public void Surprise_2024_SurprisedCreaturesRollInitiativeWithDisadvantage()
    {
        // E[lowest of two d20s] = 7.175 (vs 10.5); goblins' initiative bonus is +2.
        const int fights = 20_000;
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(TestStatBlocks.Goblin)], edition: "2024", surprise: "enemies")).Setup);
        double sum = 0, squares = 0;
        for (var i = 0; i < fights; i++)
        {
            fight.Begin((ulong)(i + 1));
            var roll = fight.Creatures[1].Initiative - 2.0;
            sum += roll;
            squares += roll * roll;
        }

        var mean = sum / fights;
        var se = Math.Sqrt(((squares / fights) - (mean * mean)) / fights);
        Assert.True(Math.Abs(mean - 7.175) <= 4 * se, $"mean {mean} ± {se}");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Damage, death and healing.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void MassiveDamage_LeftoverAtLeastTheMaximum_Kills()
    {
        // Contract §9: at 5 HP of 20, 25 damage leaves 20 past 0 — at least the maximum — and kills outright.
        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 20, ac: 18, name: "Fighter")], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        pc.Hp = 5;
        fight.ApplyDamage(null, pc, Scripted.Damage(25), false, false, false, false, false);
        Assert.True(pc.Dead);

        var again = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 20, ac: 18, name: "Fighter")], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var other = again.Named("Fighter");
        other.Hp = 5;
        again.ApplyDamage(null, other, Scripted.Damage(24), false, false, false, false, false);
        Assert.False(other.Dead);
        Assert.True(other.Down);
        Assert.True(other.Has(Cond.Prone));
    }

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 44, true)]
    public void DamageAtZeroHp_AddsFailuresOrKills(bool crit, int failures, bool dead)
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        fight.ApplyDamage(null, pc, Scripted.Damage(44), false, false, false, false, false);
        Assert.True(pc.Down);

        fight.ApplyDamage(null, pc, Scripted.Damage(dead ? 44 : 3), false, false, false, crit, false);
        Assert.Equal(dead, pc.Dead);
        if (!dead)
        {
            Assert.Equal(failures, pc.DeathFailures);
        }
    }

    [Fact]
    public void AreaEffect_CatchesADyingPartyMember_WhoTakesADeathSaveFailure()
    {
        // A dying creature lies where it fell, inside the area (a 20-foot sphere catches 4, more than the party), and
        // 2014 "If you take any damage while you have 0 hit points, you suffer a death saving throw failure".
        var breath = TestStatBlocks.SaveAction("Breath", "dex", 30, "2d6", "fire", new AreaSpec(K.Shapes.Sphere, 20));
        var fight = Scripted.Begin([Fighter, SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Second")],
            [SimKit.Monster(TestStatBlocks.Sandbag(actions: [breath]))]);
        var downed = fight.Named("Fighter");
        fight.ApplyDamage(null, downed, Scripted.Damage(44), false, false, false, false, false);
        Assert.True(downed.Down);

        fight.TakeTurn(fight.Named("Sandbag"));
        Assert.True(fight.Named("Second").TakenRaw > 0); // the breath was used
        Assert.True(downed.DeathFailures >= 1 || downed.Dead, "the dying fighter in the area took no damage");
    }

    [Fact]
    public void AreaEffect_StandingCreaturesFillTheCountFirst()
    {
        // A 5-foot sphere catches one creature (the DMG count): the standing fighter, never the dying one beside it — the
        // downed only fill what the count has left.
        var blast = TestStatBlocks.SaveAction("Blast", "dex", 30, "5", "fire", area: new AreaSpec(K.Shapes.Sphere, 5));
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var fight = Scripted.Begin([Fighter, SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Second")],
                [SimKit.Monster(TestStatBlocks.Sandbag(actions: [blast]))], seed);
            var downed = fight.Named("Fighter");
            fight.ApplyDamage(null, downed, Scripted.Damage(44), false, false, false, false, false);
            fight.TakeTurn(fight.Named("Sandbag"));
            Assert.Equal(5, fight.Named("Second").TakenRaw);
            Assert.Equal(0, downed.DeathFailures);
        }
    }

    [Fact]
    public void DamageToAStableCreature_StartsItDyingAgain()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        fight.ApplyDamage(null, pc, Scripted.Damage(44), false, false, false, false, false);
        pc.Stable = true;
        fight.ApplyDamage(null, pc, Scripted.Damage(2), false, false, false, false, false);
        Assert.False(pc.Stable);
        Assert.Equal(1, pc.DeathFailures);
    }

    [Fact]
    public void DeathSave_BecomingStable_ResetsBothCounts()
    {
        // "The number of both is reset to zero when you ... become stable": a stable creature hit again starts from 0/0,
        // not from its three successes (which would make the next success stabilise it at once).
        var stabilised = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)], seed);
            var pc = fight.Named("Fighter");
            fight.ApplyDamage(null, pc, Scripted.Damage(44), false, false, false, false, false);
            pc.DeathSuccesses = 2;
            fight.StartOfTurn(pc);
            if (!pc.Stable)
            {
                continue;
            }

            stabilised++;
            Assert.Equal((0, 0), (pc.DeathSuccesses, pc.DeathFailures));
        }

        Assert.True(stabilised > 5, $"only {stabilised} of 40 seeds stabilised");
    }

    [Fact]
    public void HealingFromZero_WakesTheCreature_ResetsDeathSaves_LeavesItProne()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        fight.ApplyDamage(null, pc, Scripted.Damage(44), false, false, false, false, false);
        fight.ApplyDamage(null, pc, Scripted.Damage(1), false, false, false, false, false);
        Assert.Equal(1, pc.DeathFailures);

        fight.Heal(pc, pc, 7, "test");
        Assert.Equal(7, pc.Hp);
        Assert.False(pc.Down);
        Assert.Equal(0, pc.DeathFailures);
        Assert.True(pc.Has(Cond.Prone));

        fight.Heal(pc, pc, 100, "test");
        Assert.Equal(44, pc.Hp); // capped at the maximum
    }

    [Fact]
    public void TemporaryHitPoints_DoNotStack_AndAbsorbFirst()
    {
        var build = SimKit.Fighter2024.Replace("\"attacks\"", "\"modifiers\": [{ \"kind\": \"temp_hp\", \"name\": \"Inspiring Leader\", \"amount\": 8 }], \"attacks\"");
        var fight = Scripted.Begin([SimKit.Pc(build, hp: 44, ac: 18, name: "Fighter")], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        Assert.Equal(8, pc.TempHp);

        fight.GainTempHp(pc, 5);
        Assert.Equal(8, pc.TempHp);
        fight.GainTempHp(pc, 10);
        Assert.Equal(10, pc.TempHp);

        var result = fight.ApplyDamage(null, pc, Scripted.Damage(13), false, false, false, false, false);
        Assert.Equal(0, pc.TempHp);
        Assert.Equal(41, pc.Hp);
        Assert.Equal(13, result.Effective);
    }

    [Theory]
    [InlineData(false, false, false, 5)]
    [InlineData(true, false, false, 10)]
    [InlineData(false, true, false, 10)]
    [InlineData(false, false, true, 5)]
    public void QualifiedResistance_AppliesOnlyToNonmagicalUnsilveredDamage(bool magical, bool silvered, bool adamantine, int taken)
    {
        var werewolf = TestStatBlocks.Create("Werewolf", 12, 58, "9d8+18", (15, 13, 14, 10, 11, 10), [TestStatBlocks.Attack("Bite", 4, "1d8+2", "piercing")],
            resistances: [new DamageAdjustment("slashing", K.DamageQualifiers.NonmagicalNotSilvered, "slashing from nonmagical attacks that aren't silvered")]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(werewolf)]);
        var target = fight.Named("Werewolf");
        var result = fight.ApplyDamage(null, target, Scripted.Damage(10), magical, silvered, adamantine, false, false);
        Assert.Equal(taken, result.Dealt);
    }

    [Fact]
    public void QualifiedResistance_ASpellAttackIsMagical()
    {
        // Resistance to bludgeoning "from nonmagical attacks": a spell attack's 10 bludgeoning goes through whole.
        var werebeast = TestStatBlocks.Create("Werebeast", 12, 500, "50d8", (15, 13, 14, 10, 11, 10), [TestStatBlocks.Attack("Claw", 4, "1d6", "slashing")],
            resistances: [new DamageAdjustment("bludgeoning", StatBlockValues.DamageQualifiers.Nonmagical, "bludgeoning from nonmagical attacks")]);
        var slinger = SimKit.Pc("""{ "name": "Slinger", "edition": "2024", "level": 5, "abilities": {"wis": 16}, "attacks": [{ "name": "Magic Stone", "to_hit": {"total": 30}, "damage": "10", "damage_type": "bludgeoning", "properties": ["ranged", "spell"] }] }""", hp: 40, ac: 15, name: "Slinger");
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([slinger], [SimKit.Monster(werebeast)], seed);
            var target = fight.Named("Werebeast");
            fight.TakeTurn(fight.Named("Slinger"));
            Assert.Contains(target.TakenRaw, new long[] { 0, 10 });
        }
    }

    [Fact]
    public void ResistanceVulnerabilityImmunity_ApplyPerTypeWithFloorsAsTheClosedForm()
    {
        var block = TestStatBlocks.Create("Mixed", 12, 100, "10d8", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Bite", 4, "1d8", "piercing")],
            resistances: [new DamageAdjustment("fire", null, "fire")], immunities: [new DamageAdjustment("poison", null, "poison")]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(block)]);
        var target = fight.Named("Mixed");
        var damage = Scripted.Damage(7, "fire");
        damage[DamageTypes.Of("poison")] = 9;
        damage[DamageTypes.Of("slashing")] = 4;
        // fire 7 → 3 (floor), poison 9 → 0, slashing 4 → 4; a successful save halves each type first: 3 → 1, 4 → 2.
        Assert.Equal(7, fight.ApplyDamage(null, target, damage, false, false, false, false, false).Dealt);
        Assert.Equal(3, fight.ApplyDamage(null, target, damage, false, false, false, false, halve: true).Dealt);
    }

    [Theory]
    [InlineData(70, true, 30)]
    [InlineData(70, false, 35)]
    [InlineData(5, true, 10)]
    [InlineData(25, false, 12)]
    [InlineData(61, true, 30)]
    public void ConcentrationDc_IsHalfTheDamage_AtLeast10_CappedAt30In2024(int damage, bool is2024, int dc) =>
        Assert.Equal(dc, Fight.ConcentrationDc(damage, is2024));

    [Fact]
    public void Concentration_LostToDamage_SwitchesTheModifierOff()
    {
        const string warlock = """
            { "name": "Warlock", "edition": "2024", "level": 5, "abilities": {"cha": 18, "con": 10},
              "attacks": [{ "name": "Eldritch Blast", "to_hit": {"ability": "cha"}, "damage": "1d10", "damage_type": "force", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "beams" }],
              "modifiers": [{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "concentration": true, "setup": "bonus_action" }] }
            """;
        var fight = Scripted.Begin([SimKit.Pc(warlock, hp: 200, ac: 12, name: "Warlock")], [SimKit.Monster(TestStatBlocks.Sandbag())]);
        var pc = fight.Named("Warlock");
        var hex = pc.Pc!.Build.Setups[0].Source.Number;
        Assert.False(pc.Pc.Active[hex]); // waits for its setup
        fight.TakeTurn(pc);
        Assert.NotEqual(0, pc.ConcentrationToken);
        Assert.True(pc.Pc.Active[hex]);

        // 100 damage: DC 50, which a +0 Constitution save cannot make.
        fight.ApplyDamage(null, pc, Scripted.Damage(100), false, false, false, false, false);
        Assert.Equal(0, pc.ConcentrationToken);
        Assert.False(pc.Pc.Active[hex]);
    }

    [Fact]
    public void Concentration_LostToDamage_SwitchesAnUngatedModifierOffToo()
    {
        // Hex with Concentration but no setup (warlock_baseline's shape) is up from the start. Once damage breaks the
        // concentration it must stop applying, as the set-up Hex above does: a flat 1 that always hits then deals 1.
        const string warlock = """
            { "name": "Warlock", "edition": "2024", "level": 5, "abilities": {"cha": 18, "con": 10},
              "attacks": [{ "name": "Blast", "to_hit": {"total": 30}, "damage": "1", "damage_type": "force", "ability_to_damage": false, "properties": ["ranged", "spell"] }],
              "modifiers": [{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "concentration": true }] }
            """;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(warlock, hp: 200, ac: 12, name: "Warlock")], [SimKit.Monster(TestStatBlocks.Sandbag(ac: 10))], seed);
            var pc = fight.Named("Warlock");
            Assert.NotEqual(0, pc.ConcentrationToken);

            // 100 damage: DC 30 in 2024, which a +0 Constitution save cannot make.
            fight.ApplyDamage(null, pc, Scripted.Damage(100), false, false, false, false, false);
            Assert.Equal(0, pc.ConcentrationToken);

            fight.TakeTurn(pc);
            Assert.InRange(pc.DealtRaw, 0, 1);
            Assert.False(pc.Pc!.IsActive(pc.Pc.Build.ConcentrationNumber));
        }
    }

    [Fact]
    public void Concentration_DamageTheTemporaryHitPointsAbsorb_StillSetsTheDc()
    {
        // 60 damage all into 60 temporary hit points is still 60 damage taken: DC 30 (2014), which Con +9 cannot make;
        // read as the 0 that got through, it would be DC 10, which +9 always makes.
        var tough = new SimulationCombatant(new CombatantSpec { Name = "Tough", Build = SimKit.Build(SimKit.Fighter2024), Hp = 44, Ac = 18, Saves = new Domain.Features.SavesSpec { Con = 9 } });
        var fight = Scripted.Begin([tough], [SimKit.Monster(TestStatBlocks.Ogre)], edition: "2014");
        var pc = fight.Named("Tough");
        fight.StartConcentration(pc, "Bless", 0, deactivates: false);
        fight.GainTempHp(pc, 60);
        fight.ApplyDamage(null, pc, Scripted.Damage(60), false, false, false, false, false);
        Assert.Equal(44, pc.Hp);
        Assert.Equal(0, pc.ConcentrationToken);
    }

    [Fact]
    public void Concentration_EndsWhenTheConcentratorIsIncapacitated()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        fight.StartConcentration(pc, "Bless", 0, deactivates: false);
        fight.AddCondition(null, pc, new ConditionTemplate { Condition = Cond.Stunned, Duration = DurationKind.Fight }, 0);
        Assert.Equal(0, pc.ConcentrationToken);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Conditions.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2024", false)]
    [InlineData("2014", true)]
    public void StunningStrike_EditionDefaultDuration_EndsAtTheRightEdgeOfTheMonksNextTurn(string edition, bool stunnedDuringNextTurn)
    {
        var monk = Sure("""{ "kind": "condition_on_hit", "name": "Stunning Strike", "condition": "stunned", "ability": "con", "dc": 40, "when": "first_hit_per_turn", "resource": {"uses": 1, "per": "short_rest"} }""", edition);
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(monk, hp: 50, ac: 15, name: "Monk")], [SimKit.Monster(TestStatBlocks.Sandbag(ac: 10))], seed, edition: edition);
            var pc = fight.Named("Monk");
            var target = fight.Named("Sandbag");
            fight.TakeTurn(pc);
            if (!target.Has(Cond.Stunned))
            {
                continue; // a natural 1 missed
            }

            fight.TakeTurn(target);
            Assert.True(target.Has(Cond.Stunned));
            fight.StartOfTurn(pc);
            Assert.Equal(stunnedDuringNextTurn, target.Has(Cond.Stunned));
            fight.EndOfTurn(pc);
            Assert.False(target.Has(Cond.Stunned));
        }
    }

    [Fact]
    public void ConditionImmunity_ConditionNeverLands()
    {
        var zombie = TestStatBlocks.Zombie;
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(zombie)]);
        var target = fight.Named("Zombie");
        var poisoned = new ConditionTemplate { Condition = Cond.Poisoned, Duration = DurationKind.Fight };
        Assert.False(fight.AddCondition(null, target, poisoned, 0));
        Assert.False(target.Has(Cond.Poisoned));
    }

    [Fact]
    public void Prone_StandsUpAtTheStartOfItsTurn_UnlessRestrained()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var ogre = fight.Named("Ogre");
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Prone, Duration = DurationKind.UntilStands }, 0);
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Restrained, Duration = DurationKind.Fight }, 0);
        fight.StartOfTurn(ogre);
        Assert.True(ogre.Has(Cond.Prone));

        var free = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var other = free.Named("Ogre");
        free.AddCondition(null, other, new ConditionTemplate { Condition = Cond.Prone, Duration = DurationKind.UntilStands }, 0);
        free.StartOfTurn(other);
        Assert.False(other.Has(Cond.Prone));
    }

    [Fact]
    public void Exhaustion_SixLevels_Kill()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        for (var level = 1; level <= 6; level++)
        {
            Assert.False(pc.Dead, $"dead at exhaustion {level - 1}");
            fight.AddCondition(null, pc, new ConditionTemplate { Condition = Cond.Exhaustion, Duration = DurationKind.Fight }, 0);
        }

        Assert.True(pc.Dead);
    }

    [Fact]
    public void Unconscious_IsAlsoProne()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var ogre = fight.Named("Ogre");
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Unconscious, Duration = DurationKind.Fight }, 0);
        Assert.True(ogre.Has(Cond.Prone));
    }

    [Theory]
    [InlineData("2014", 10)]
    [InlineData("2024", 20)]
    public void Evasion_WhileStunned_2014KeepsIt_2024LosesIt(string edition, int taken)
    {
        // A stunned creature fails the Dex save; 2014 Evasion still halves the damage on a failure, 2024's does not work
        // while it is incapacitated. 20 flat fire damage, half on a success.
        var caster = SimKit.Pc("""
            { "name": "Caster", "edition": "2024", "level": 5, "abilities": {"int": 18},
              "modifiers": [{ "kind": "save_effect", "name": "Flame Wave", "ability": "dex", "dc": 15, "dice": "20", "type": "fire" }] }
            """, hp: 30, ac: 12, name: "Caster");
        var rogue = TestStatBlocks.Create("Rogue", 15, 500, "50d8", (10, 16, 10, 10, 10, 10), [TestStatBlocks.Attack("Dagger", 4, "1d4+2", "piercing")],
            edition: edition, traits: [TestStatBlocks.Trait("Evasion", K.TraitKinds.Evasion)]);
        var fight = Scripted.Begin([caster], [SimKit.Monster(rogue)], edition: edition);
        var target = fight.Named("Rogue");
        fight.AddCondition(null, target, new ConditionTemplate { Condition = Cond.Stunned, Duration = DurationKind.Fight }, 0);
        fight.TakeTurn(fight.Named("Caster"));
        Assert.Equal(taken, target.TakenRaw);
    }

    [Theory]
    [InlineData(Cond.Grappled)]
    [InlineData(Cond.Restrained)]
    public void Dodge_IsLostAtSpeedZero(int condition)
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var ogre = fight.Named("Ogre");
        var pc = fight.Named("Fighter");
        ogre.Dodging = true;
        Assert.Equal(Domain.Probability.D20Mode.Disadvantage, fight.AttackMode(pc, ogre, true, false, false, out _, consume: false));
        fight.AddCondition(pc, ogre, new ConditionTemplate { Condition = condition, Duration = DurationKind.Fight }, 0);
        var expected = condition == Cond.Restrained ? Domain.Probability.D20Mode.Advantage : Domain.Probability.D20Mode.Normal;
        Assert.Equal(expected, fight.AttackMode(pc, ogre, true, false, false, out _, consume: false));
    }

    [Fact]
    public void Prone_AStunnedCreatureStaysDown()
    {
        // Standing up takes movement, which a stunned (paralyzed, unconscious, petrified) creature cannot use.
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var ogre = fight.Named("Ogre");
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Prone, Duration = DurationKind.UntilStands }, 0);
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Stunned, Duration = DurationKind.Fight }, 0);
        fight.StartOfTurn(ogre);
        Assert.True(ogre.Has(Cond.Prone));
    }

    [Fact]
    public void RoundsDuration_LastsThatManyOfTheSourcesTurnEnds()
    {
        // "For 1 minute" is 10 rounds, counted down at the end of each of the source's turns: a 2-round condition survives
        // the first and ends at the second.
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        var ogre = fight.Named("Ogre");
        fight.AddCondition(ogre, pc, new ConditionTemplate { Condition = Cond.Blinded, Duration = DurationKind.Rounds, Rounds = 2 }, 0);
        fight.EndOfTurn(ogre);
        Assert.True(pc.Has(Cond.Blinded));
        fight.EndOfTurn(ogre);
        Assert.False(pc.Has(Cond.Blinded));
    }

    [Theory]
    [InlineData(K.Durations.UntilStartOfSourceTurn, null)]
    [InlineData(K.Durations.UntilEndOfSourceTurn, null)]
    [InlineData(K.Durations.Rounds, 2)]
    public void SourceTurnDuration_ItsSourceDies_TheConditionStillEndsWhereItsTurnComesRound(string duration, int? rounds)
    {
        // The 2024 lich's Paralyzing Touch: "the target has the Paralyzed condition until the start of the lich's next
        // turn". A dead lich takes no turn, but its place in the order still comes round and the paralysis ends there; it
        // must not hold the fighter (open to Advantage and automatic critical hits) for the rest of the fight. A 1-HP
        // toucher goes first (+30 initiative), paralyzes the front-line fighter, and the back-line archer shoots it dead in
        // round 1; from round 2 the fighter's turns are its own. The same holds for a duration counted at the end of the
        // source's turns (a round count included).
        var paralysis = new ConditionEffect { Condition = "paralyzed", Duration = duration, Rounds = rounds };
        var touch = TestStatBlocks.Attack("Touch", 30, "1", "cold", onHit: [new ActionEffect { Kind = K.EffectKinds.Condition, Condition = paralysis }]);
        var toucher = TestStatBlocks.Create("Toucher", 5, 1, "1d4", (10, 10, 10, 10, 10, 10), [touch], initiative: 30);
        const string archer = """
            { "name": "Archer", "edition": "2024", "level": 5, "abilities": {"dex": 10},
              "attacks": [{ "name": "Bow", "count": 3, "to_hit": {"total": 30}, "damage": "5", "damage_type": "piercing", "properties": ["ranged"] }] }
            """;
        var party = new[]
        {
            SimKit.Pc(SimKit.Fighter2024, hp: 500, ac: 18, name: "Fighter"),
            SimKit.Pc(archer, hp: 500, ac: 18, name: "Archer", position: SimulationValues.Positions.Back),
        };
        var checkedFights = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var spec = SimKit.Spec(party, [SimKit.Monster(toucher, hp: 1), SimKit.Monster(TestStatBlocks.Sandbag())], roundCap: 3);
            var fight = new Fight(SimulationPreparation.Prepare(spec).Setup);
            var log = new CombatLog(1_000_000);
            fight.Run(seed, log);
            var lines = log.Finish(string.Empty).Split('\n').Select(l => l.TrimEnd()).ToArray();
            var round2 = Array.IndexOf(lines, "Round 2");
            var died = Array.IndexOf(lines, "    Toucher dies");
            if (died < 0 || died > round2 || !lines.Take(died).Any(l => l.Contains("Fighter is paralyzed", StringComparison.Ordinal)))
            {
                continue; // the touch missed (a natural 1), or the toucher lived through round 1
            }

            checkedFights++;
            var later = lines.Skip(round2).Where(l => l.StartsWith("Fighter's turn", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, later.Count);
            Assert.All(later, l => Assert.DoesNotContain("paralyzed", l));
        }

        Assert.True(checkedFights > 0);
    }

    [Fact]
    public void SourceTurnDuration_ItsSourceDiesDuringItsOwnTurn_ThatTurnStillEnds()
    {
        // "Until the end of the source's next turn", imposed on the source's own turn: that turn's end does not count, the
        // next one does. A 1-HP toucher that paralyzes a fire-bodied creature and burns to death on the touch never ends that
        // turn alive, but the turn still ends; so the paralysis ends where its next turn comes round, not a round later.
        var paralysis = new ConditionEffect { Condition = "paralyzed", Duration = K.Durations.UntilEndOfSourceTurn };
        var touch = TestStatBlocks.Attack("Touch", 30, "1", "cold", onHit: [new ActionEffect { Kind = K.EffectKinds.Condition, Condition = paralysis }]);
        var toucher = TestStatBlocks.Create("Toucher", 5, 1, "1d4", (10, 10, 10, 10, 10, 10), [touch]);
        var burning = Brute([TestStatBlocks.Attack("Slam", 5, "1d6", "bludgeoning")], traits:
            [new StatBlockTrait { Name = "Heated Body", Kind = K.TraitKinds.RetaliationDamage, Damage = [TestStatBlocks.Roll("10", "fire")], Text = "A creature that touches it or hits it with a melee attack takes 10 fire damage." }]);
        var touched = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Monster(burning, name: "Burning")], [SimKit.Monster(toucher, hp: 1), SimKit.Monster(TestStatBlocks.Sandbag())], seed);
            var target = fight.Named("Burning");
            var source = fight.Named("Toucher");
            fight.TakeTurn(source);
            if (!target.Has(Cond.Paralyzed))
            {
                continue; // a natural 1
            }

            touched++;
            Assert.True(source.Dead, "the burn killed the toucher on its own turn");
            fight.TakeTurn(source); // its place in the order comes round: it is dead, and its next turn ends
            Assert.False(target.Has(Cond.Paralyzed));
            Assert.Equal(1, source.TurnsTaken); // a place in the order, not a turn
        }

        Assert.True(touched > 0);
    }

    [Fact]
    public void Saves_AStunnedCreatureFailsStrengthSavesAutomatically()
    {
        // Str 18 (+4) against DC 1 never fails a Str save it rolls; stunned, it fails without rolling (Str and Dex).
        var shover = TestStatBlocks.Create("Shover", 12, 50, "10d8", (10, 10, 10, 10, 10, 10), [TestStatBlocks.SaveAction("Shove", "str", 1, "1", "bludgeoning", onSuccess: K.OnSuccess.None)]);
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(shover)], seed);
            var pc = fight.Named("Fighter");
            fight.AddCondition(null, pc, new ConditionTemplate { Condition = Cond.Stunned, Duration = DurationKind.Fight }, 0);
            fight.TakeTurn(fight.Named("Shover"));
            Assert.Equal(1, pc.TakenRaw);
        }
    }

    [Theory]
    [InlineData("2014", 2, Domain.Probability.D20Mode.Normal)]
    [InlineData("2014", 3, Domain.Probability.D20Mode.Disadvantage)]
    [InlineData("2024", 5, Domain.Probability.D20Mode.Normal)]
    public void Exhaustion_2014DisadvantageFromLevelThree_2024NoDisadvantage(string edition, int levels, Domain.Probability.D20Mode expected)
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)], edition: edition);
        var pc = fight.Named("Fighter");
        for (var i = 0; i < levels; i++)
        {
            fight.AddCondition(null, pc, new ConditionTemplate { Condition = Cond.Exhaustion, Duration = DurationKind.Fight }, 0);
        }

        Assert.Equal(expected, fight.AttackMode(pc, fight.Named("Ogre"), true, false, false, out _, consume: false));
    }

    [Fact]
    public void Exhaustion_2024_TwoPerLevelOffEveryD20Test()
    {
        // 2024: −2 × level on d20 tests. An ogre (+6) at exhaustion 2 attacks at +2, as its logged roll shows.
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)], edition: "2024")).Setup);
        var log = new CombatLog();
        fight.Begin(1, log);
        var ogre = fight.Named("Ogre");
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Exhaustion, Duration = DurationKind.Fight }, 0);
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Exhaustion, Duration = DurationKind.Fight }, 0);
        fight.TakeTurn(ogre);
        Assert.Matches(@"Greatclub vs Fighter: d20 (\S+ )*\d+\+2 = ", log.Finish(string.Empty));
    }

    [Theory]
    [InlineData("2014", Domain.Probability.D20Mode.Normal)]
    [InlineData("2024", Domain.Probability.D20Mode.Disadvantage)]
    public void Grappled_AttackingSomeoneElse_DisadvantageOnlyIn2024(string edition, Domain.Probability.D20Mode expected)
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre, count: 2)], edition: edition);
        var pc = fight.Named("Fighter");
        fight.AddCondition(fight.Named("Ogre"), pc, new ConditionTemplate { Condition = Cond.Grappled, Duration = DurationKind.UntilEscape, EscapeDc = 13 }, 0);
        Assert.Equal(expected, fight.AttackMode(pc, fight.Named("Ogre 2"), true, false, false, out _, consume: false));
        Assert.Equal(Domain.Probability.D20Mode.Normal, fight.AttackMode(pc, fight.Named("Ogre"), true, false, false, out _, consume: false));
    }

    [Fact]
    public void Frightened_DisadvantageEndsWhenItsSourceDies()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre, count: 2)]);
        var pc = fight.Named("Fighter");
        var source = fight.Named("Ogre");
        fight.AddCondition(source, pc, new ConditionTemplate { Condition = Cond.Frightened, Duration = DurationKind.Fight }, 0);
        Assert.Equal(Domain.Probability.D20Mode.Disadvantage, fight.AttackMode(pc, fight.Named("Ogre 2"), true, false, false, out _, consume: false));
        fight.ApplyDamage(null, source, Scripted.Damage(1000), false, false, false, false, false);
        Assert.Equal(Domain.Probability.D20Mode.Normal, fight.AttackMode(pc, fight.Named("Ogre 2"), true, false, false, out _, consume: false));
    }

    [Fact]
    public void Petrified_ResistsEveryDamageType()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var ogre = fight.Named("Ogre");
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Petrified, Duration = DurationKind.Fight }, 0);
        fight.ApplyDamage(null, ogre, Scripted.Damage(11, "fire"), false, false, false, false, false);
        Assert.Equal(5, ogre.TakenRaw);
    }

    [Fact]
    public void Attacks_ConditionsSetAdvantageAndDisadvantage_AndMeleeAutoCritsAnUnconsciousTarget()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var pc = fight.Named("Fighter");
        var ogre = fight.Named("Ogre");
        Assert.Equal(Domain.Probability.D20Mode.Normal, fight.AttackMode(pc, ogre, true, false, false, out _, consume: false));

        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Prone, Duration = DurationKind.UntilStands }, 0);
        Assert.Equal(Domain.Probability.D20Mode.Advantage, fight.AttackMode(pc, ogre, true, false, false, out _, consume: false));
        Assert.Equal(Domain.Probability.D20Mode.Disadvantage, fight.AttackMode(pc, ogre, false, false, false, out _, consume: false));

        fight.AddCondition(null, pc, new ConditionTemplate { Condition = Cond.Poisoned, Duration = DurationKind.Fight }, 0);
        Assert.Equal(Domain.Probability.D20Mode.Normal, fight.AttackMode(pc, ogre, true, false, false, out _, consume: false)); // cancel

        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Unconscious, Duration = DurationKind.Fight }, 0);
        fight.AttackMode(pc, ogre, true, false, false, out var autoCrit, consume: false);
        Assert.True(autoCrit);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Monster traits.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void UndeadFortitude_SavesAboutNineTwentiethsOfTheTime_NeverAgainstRadiantOrACrit()
    {
        // At 5 HP taking 10 bludgeoning: Con save (+3) against DC 15 succeeds on 12–20, 9/20 = 0.45.
        var saved = 0;
        const int fights = 4_000;
        for (var i = 0; i < fights; i++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Zombie)], (ulong)(i + 1));
            var zombie = fight.Named("Zombie");
            zombie.Hp = 5;
            fight.ApplyDamage(null, zombie, Scripted.Damage(10, "bludgeoning"), false, false, false, false, false);
            saved += zombie.Hp == 1 ? 1 : 0;
            if (zombie.Hp != 1)
            {
                Assert.True(zombie.Dead);
            }
        }

        var interval = SimulationStatistics.Wilson(saved, fights, SimulationStatistics.Z999);
        Assert.InRange(0.45, interval.Low, interval.High);

        for (ulong seed = 1; seed <= 30; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Zombie)], seed);
            var zombie = fight.Named("Zombie");
            zombie.Hp = 5;
            fight.ApplyDamage(null, zombie, Scripted.Damage(6, "radiant"), false, false, false, false, false);
            Assert.True(zombie.Dead);

            var other = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Zombie)], seed);
            var crit = other.Named("Zombie");
            crit.Hp = 5;
            other.ApplyDamage(null, crit, Scripted.Damage(6, "bludgeoning"), false, false, false, crit: true, false);
            Assert.True(crit.Dead);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("fire", true)]
    [InlineData("acid", true)]
    public void Regeneration_TrollAtZeroHp_ComesBackUnlessFireOrAcidStoppedIt(string? burn, bool dies)
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Troll)]);
        var troll = fight.Named("Troll");
        fight.ApplyDamage(null, troll, Scripted.Damage(84), false, false, false, false, false);
        Assert.True(troll.Down);
        Assert.False(troll.Dead);
        Assert.False(troll.Up); // down: a side with nobody above 0 HP is beaten, even a troll's

        if (burn is not null)
        {
            fight.ApplyDamage(null, troll, Scripted.Damage(3, burn), false, false, false, false, false);
        }

        fight.StartOfTurn(troll);
        Assert.Equal(dies, troll.Dead);
        Assert.Equal(dies ? 0 : 10, troll.Hp);
    }

    [Fact]
    public void Regeneration_ALoneTrollAtZero_EndsTheFightDownButNotDead()
    {
        // Four level 5 fighters without fire or acid: the troll can never die, but a side with nobody above 0 HP is
        // beaten, so the party wins when it drops it.
        var report = Simulator.Run(SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 4)], [SimKit.Monster(TestStatBlocks.Troll)], iterations: 2_000), 6);
        var troll = report.Combatants[1];
        Assert.True(report.PartyWins.Estimate > 0.9, $"P(win) {report.PartyWins.Estimate}");
        Assert.Equal(0, troll.DeadAtEnd.Count);
        Assert.True(troll.DroppedToZero.Estimate > 0.9);
    }

    [Fact]
    public void Regeneration_ATrollDownWhileAnAllyFights_GetsUpAtItsTurn()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Troll), SimKit.Monster(TestStatBlocks.Sandbag())]);
        var troll = fight.Named("Troll");
        fight.ApplyDamage(fight.Named("Fighter"), troll, Scripted.Damage(84), false, false, false, false, false);
        fight.TakeTurn(troll);
        Assert.False(fight.Over);
        Assert.True(troll.Up);
        Assert.Equal(10, troll.Hp); // regenerated 10 from 0, then took its turn
    }

    [Fact]
    public void Regeneration_ThatNeedsOneHitPoint_DoesNotSaveItAtZero()
    {
        // A vampire regenerates only "if it has at least 1 hit point": at 0 it dies like any monster (the troll's text,
        // "dies only if it starts its turn with 0 hit points", is what keeps a regenerator down but alive).
        var vampire = TestStatBlocks.Create("Vampire", 16, 144, "17d8+68", (18, 18, 18, 17, 15, 18), [TestStatBlocks.Attack("Bite", 9, "1d6+4", "piercing")],
            traits: [TestStatBlocks.Trait("Regeneration", K.TraitKinds.Regeneration, amount: 20, types: ["radiant"],
                text: "The vampire regains 20 hit points at the start of its turn if it has at least 1 hit point and isn't in sunlight or running water.")]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(vampire)]);
        var target = fight.Named("Vampire");
        fight.ApplyDamage(null, target, Scripted.Damage(144), false, false, false, false, false);
        Assert.True(target.Dead);
    }

    [Fact]
    public void Regeneration_AboveZero_HealsTenAtTheStartOfItsTurn()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Troll)]);
        var troll = fight.Named("Troll");
        fight.ApplyDamage(null, troll, Scripted.Damage(30), false, false, false, false, false);
        fight.StartOfTurn(troll);
        Assert.Equal(64, troll.Hp);
        fight.ApplyDamage(null, troll, Scripted.Damage(5, "fire"), false, false, false, false, false);
        fight.StartOfTurn(troll);
        Assert.Equal(59, troll.Hp);
    }

    [Fact]
    public void PackTactics_NeedsAnAllyEngagedWithTheTarget()
    {
        var pair = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Wolf, count: 2)]);
        Assert.True(pair.TraitAdvantage(pair.Named("Wolf"), pair.Named("Fighter"), true));

        var alone = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Wolf)]);
        Assert.False(alone.TraitAdvantage(alone.Named("Wolf"), alone.Named("Fighter"), true));

        // The other wolf down: no ally engaged.
        pair.ApplyDamage(null, pair.Named("Wolf 2"), Scripted.Damage(50), false, false, false, false, false);
        Assert.False(pair.TraitAdvantage(pair.Named("Wolf"), pair.Named("Fighter"), true));
    }

    [Fact]
    public void Engagement_MeleeReachesTheFrontLineWhileItStands_FliersReachTheBackLine()
    {
        var caster = new SimulationCombatant(new CombatantSpec
        {
            Name = "Caster",
            Build = SimKit.Build("""
                { "name": "Caster", "edition": "2024", "level": 5, "abilities": {"int": 18},
                  "attacks": [{ "name": "Fire Bolt", "to_hit": {"ability": "int"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "dice" }] }
                """),
            Hp = 30,
            Ac = 12,
        });
        var fight = Scripted.Begin([Fighter, caster], [SimKit.Monster(TestStatBlocks.Ogre), SimKit.Monster(TestStatBlocks.AdultRedDragon)]);
        var ogre = fight.Named("Ogre");
        var dragon = fight.Named("Adult Red Dragon");
        var fighter = fight.Named("Fighter");
        var wizard = fight.Named("Caster");
        Assert.False(wizard.T.Front);

        Assert.True(fight.Reachable(ogre, fighter, melee: true));
        Assert.False(fight.Reachable(ogre, wizard, melee: true));
        Assert.True(fight.Reachable(ogre, wizard, melee: false));
        Assert.True(fight.Reachable(dragon, wizard, melee: true)); // it flies
        Assert.Same(fighter, fight.PickTarget(ogre, melee: true));

        fight.ApplyDamage(null, fighter, Scripted.Damage(44), false, false, false, false, false);
        Assert.True(fight.Reachable(ogre, wizard, melee: true));
        Assert.Same(wizard, fight.PickTarget(ogre, melee: true)); // the fighter is down: not a target
    }

    [Fact]
    public void Engagement_RangedAttacksReachTheBackLineWhileTheFrontStands()
    {
        // Focus fire picks the lowest current HP: the back-line caster (30) over the front-line fighter (44), for a ranged
        // attack; a melee attack still only reaches the fighter.
        var caster = SimKit.Pc("""{ "name": "Caster", "edition": "2024", "level": 5, "abilities": {"int": 18}, "attacks": [{ "name": "Fire Bolt", "to_hit": {"ability": "int"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"] }] }""", hp: 30, ac: 12, name: "Caster");
        var fight = Scripted.Begin([Fighter, caster], [SimKit.Monster(TestStatBlocks.Ogre)], policies: new PolicySpec { Enemies = "focus_fire" });
        var ogre = fight.Named("Ogre");
        Assert.Same(fight.Named("Caster"), fight.PickTarget(ogre, melee: false));
        Assert.Same(fight.Named("Fighter"), fight.PickTarget(ogre, melee: true));
    }

    [Fact]
    public void Engagement_AnAreaCatchesTheFrontLineFirst()
    {
        // A 5-foot sphere catches one creature (the DMG count): the front-liner, never the caster behind it.
        var caster = SimKit.Pc("""{ "name": "Caster", "edition": "2024", "level": 5, "abilities": {"int": 18}, "attacks": [{ "name": "Fire Bolt", "to_hit": {"ability": "int"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"] }] }""", hp: 30, ac: 12, name: "Caster");
        var bomber = TestStatBlocks.Create("Bomber", 12, 50, "10d8", (10, 10, 10, 10, 10, 10),
            [TestStatBlocks.SaveAction("Blast", "dex", 30, "5", "fire", area: new AreaSpec(K.Shapes.Sphere, 5))]);
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var fight = Scripted.Begin([Fighter, caster], [SimKit.Monster(bomber)], seed);
            fight.TakeTurn(fight.Named("Bomber"));
            Assert.Equal(5, fight.Named("Fighter").TakenRaw);
            Assert.Equal(0, fight.Named("Caster").TakenRaw);
        }
    }

    [Fact]
    public void PackTactics_ATargetOnTheBackLine_HasNoAllyEngagedWithIt()
    {
        var archer = SimKit.Pc("""{ "name": "Archer", "edition": "2024", "level": 5, "abilities": {"dex": 18}, "attacks": [{ "name": "Longbow", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["ranged"] }] }""", hp: 30, ac: 14, name: "Archer");
        var fight = Scripted.Begin([Fighter, archer], [SimKit.Monster(TestStatBlocks.Wolf, count: 2)]);
        Assert.True(fight.TraitAdvantage(fight.Named("Wolf"), fight.Named("Fighter"), true));
        Assert.False(fight.TraitAdvantage(fight.Named("Wolf"), fight.Named("Archer"), true));
    }

    [Fact]
    public void FinishDowned_IsTheEnemiesPolicy_ThePartyLeavesADyingNpcAlone()
    {
        var bandit = new SimulationCombatant(new CombatantSpec { Name = "Bandit", Build = SimKit.Build(SimKit.Commoner), Hp = 10, Ac = 12, DeathSaves = true });
        var fight = Scripted.Begin([Fighter], [bandit], policies: new PolicySpec { FinishDowned = true });
        var npc = fight.Named("Bandit");
        fight.ApplyDamage(null, npc, Scripted.Damage(10), false, false, false, false, false);
        Assert.True(npc.Down);
        Assert.Null(fight.PickTarget(fight.Named("Fighter"), melee: true));
    }

    [Fact]
    public void FinishDowned_EnemiesKeepAttackingAPartyMemberAtZero()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)], policies: new PolicySpec { FinishDowned = true });
        var fighter = fight.Named("Fighter");
        fight.ApplyDamage(null, fighter, Scripted.Damage(44), false, false, false, false, false);
        Assert.Same(fighter, fight.PickTarget(fight.Named("Ogre"), melee: true));

        var plain = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        plain.ApplyDamage(null, plain.Named("Fighter"), Scripted.Damage(44), false, false, false, false, false);
        Assert.Null(plain.PickTarget(plain.Named("Ogre"), melee: true));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Monster turns.
    // ------------------------------------------------------------------------------------------------------------------

    private static StatBlock Brute(IReadOnlyList<StatBlockAction> actions, IReadOnlyList<StatBlockTrait>? traits = null, IReadOnlyList<MultiattackRoutine>? multiattacks = null, int hp = 500) =>
        TestStatBlocks.Create("Brute", 12, hp, "50d10", (14, 12, 14, 10, 10, 10), actions, traits: traits, multiattacks: multiattacks, initiative: -5);

    private static string Log(Fight fight, Creature actor, Action<Fight>? before = null)
    {
        var log = new CombatLog();
        fight.Begin(1, log);
        before?.Invoke(fight);
        fight.TakeTurn(actor.Label is { } label ? fight.Named(label) : actor);
        return log.Finish(string.Empty);
    }

    [Fact]
    public void Multiattack_ChooseTwo_IsValuedAndMadeAsTwoPicks()
    {
        // "Two Claw attacks" as choose 2 of {Claw} (10 each, 20 in all) beats one Maul (15); valued as one pick (10) it
        // would lose, and made one pick too many it would claw three times.
        var brute = Brute(
            [TestStatBlocks.Attack("Claw", 30, "10", "slashing"), TestStatBlocks.Attack("Maul", 30, "15", "bludgeoning")],
            multiattacks: [new MultiattackRoutine { Label = "Multiattack", Steps = [], Choose = 2, Options = [new ActionUse("Claw", 1)] }]);
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(brute)])).Setup);
        var text = Log(fight, fight.Creatures[1]);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "Claw vs Fighter").Count);
        Assert.DoesNotContain("Maul vs", text);
    }

    [Fact]
    public void Concentration_AMonsterDoesNotStartASecondConcentrationSpell()
    {
        var hold = TestStatBlocks.SaveAction("Hold Person", "wis", 30, "", "psychic", onSuccess: K.OnSuccess.None, spell: true, concentration: true,
            condition: new ConditionEffect { Condition = "paralyzed", Duration = K.Durations.Fight });
        var brute = Brute([TestStatBlocks.Attack("Poke", 0, "1", "bludgeoning"), hold]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(brute)]);
        var monster = fight.Named("Brute");
        fight.StartConcentration(monster, "Bless", 0, deactivates: false);
        fight.TakeTurn(monster);
        Assert.False(fight.Named("Fighter").Has(Cond.Paralyzed));
    }

    [Fact]
    public void Concentration_AMonsterEveryTargetResisted_DropsTheSpell()
    {
        // The only target fails the DC 30 save but turns it with Legendary Resistance: nothing is held, so the
        // concentration the cast started ends at once.
        var hold = TestStatBlocks.SaveAction("Hold Person", "wis", 30, "", "psychic", onSuccess: K.OnSuccess.None, spell: true, concentration: true,
            condition: new ConditionEffect { Condition = "paralyzed", Duration = K.Durations.Fight });
        var fight = Scripted.Begin([SimKit.Monster(TestStatBlocks.Sandbag(legendaryResistance: 3), name: "Wall")], [SimKit.Monster(Brute([hold]))]);
        var monster = fight.Named("Brute");
        fight.TakeTurn(monster);
        Assert.Equal(1, fight.Named("Wall").LegendaryResistanceSpent);
        Assert.Equal(0, monster.ConcentrationToken);
    }

    [Fact]
    public void MonsterAttack_ACriticalHitDoublesTheOnHitRidersDice()
    {
        // Against a paralyzed target every melee hit is critical: the weapon's flat 1 stays 1, the rider's 1d1 becomes 2d1.
        var brute = Brute([TestStatBlocks.Attack("Sting", 30, "1", "piercing", onHit: [new ActionEffect { Kind = K.EffectKinds.Damage, Damage = [TestStatBlocks.Roll("1d1", "poison")] }])]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(brute)]);
        var pc = fight.Named("Fighter");
        fight.AddCondition(null, pc, new ConditionTemplate { Condition = Cond.Paralyzed, Duration = DurationKind.Fight }, 0);
        fight.TakeTurn(fight.Named("Brute"));
        Assert.Equal(3, pc.TakenRaw);
    }

    [Fact]
    public void MonsterSneakAttack_OncePerTurn_AndNotWithDisadvantage()
    {
        var sneak = TestStatBlocks.Trait("Sneak Attack", K.TraitKinds.SneakAttack, dice: "10");
        var twoStabs = Brute([TestStatBlocks.Attack("Stab", 30, "1", "piercing")], traits: [sneak], multiattacks: [TestStatBlocks.Multiattack(("Stab", 2))]);
        var sawTwelve = false;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            // Advantage against a prone target: at most one Sneak Attack a turn (1 + 1 + 10).
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(twoStabs)], seed);
            var pc = fight.Named("Fighter");
            fight.AddCondition(null, pc, new ConditionTemplate { Condition = Cond.Prone, Duration = DurationKind.Fight }, 0);
            fight.TakeTurn(fight.Named("Brute"));
            Assert.True(pc.TakenRaw <= 12, $"seed {seed}: {pc.TakenRaw}");
            sawTwelve |= pc.TakenRaw == 12;
        }

        Assert.True(sawTwelve);

        // An ally engaged with the target but the attacker poisoned (Disadvantage): no Sneak Attack.
        var oneStab = Brute([TestStatBlocks.Attack("Stab", 30, "1", "piercing")], traits: [sneak]);
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var fight = Scripted.Begin([Fighter], [SimKit.Monster(oneStab), SimKit.Monster(TestStatBlocks.Ogre)], seed);
            var brute = fight.Named("Brute");
            fight.AddCondition(null, brute, new ConditionTemplate { Condition = Cond.Poisoned, Duration = DurationKind.Fight }, 0);
            fight.TakeTurn(brute);
            Assert.True(fight.Named("Fighter").TakenRaw <= 1, $"seed {seed}: {fight.Named("Fighter").TakenRaw}");
        }
    }

    [Fact]
    public void Retaliation_OnlyAMeleeHitIsBurned()
    {
        // A fire-bodied ally in the party: the enemy's melee hit on it takes 10 fire back, its ranged hit does not.
        var burning = Brute([TestStatBlocks.Attack("Touch", 5, "1d6", "fire")], traits:
            [new StatBlockTrait { Name = "Heated Body", Kind = K.TraitKinds.RetaliationDamage, Damage = [TestStatBlocks.Roll("10", "fire")], Text = "A creature that hits it with a melee attack takes 10 fire damage." }]);
        foreach (var (range, burned) in new[] { (K.AttackRanges.Melee, 10L), (K.AttackRanges.Ranged, 0L) })
        {
            var attacker = TestStatBlocks.Create("Attacker", 12, 500, "50d10", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Hit", 30, "1", "bludgeoning", range: range)], initiative: -5);
            for (ulong seed = 1; seed <= 5; seed++)
            {
                var fight = Scripted.Begin([SimKit.Monster(burning, name: "Burning")], [SimKit.Monster(attacker)], seed);
                var hitter = fight.Named("Attacker");
                fight.TakeTurn(hitter);
                if (fight.Named("Burning").TakenRaw > 0)
                {
                    Assert.Equal(burned, hitter.TakenRaw);
                }
            }
        }
    }

    [Fact]
    public void Retaliation_APcsMeleeHitIsBurnedToo()
    {
        // Heated Body burns "a creature that ... hits it with a melee attack": a PC's blade as much as a monster's claw.
        var burning = Brute([TestStatBlocks.Attack("Touch", 5, "1d6", "fire")], traits:
            [new StatBlockTrait { Name = "Heated Body", Kind = K.TraitKinds.RetaliationDamage, Damage = [TestStatBlocks.Roll("10", "fire")], Text = "A creature that hits it with a melee attack takes 10 fire damage." }]);
        var hits = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(Sure(), hp: 50, ac: 15, name: "Sure")], [SimKit.Monster(burning)], seed);
            var pc = fight.Named("Sure");
            fight.TakeTurn(pc);
            if (fight.Named("Brute").TakenRaw > 0)
            {
                hits++;
                Assert.Equal(10, pc.TakenRaw);
            }
        }

        Assert.True(hits > 0, "the blade never hit");
    }

    [Fact]
    public void Retaliation_ABurnThatDropsTheBuild_EndsItsTurn()
    {
        // At 5 HP the blade's hit on the burning brute takes 10 fire back and drops the build: a creature at 0 HP keeps no
        // effect going that turn, so the flat 7 aura (action_cost none) that follows the Action never comes.
        var burning = Brute([TestStatBlocks.Attack("Touch", 5, "1d6", "fire")], traits:
            [new StatBlockTrait { Name = "Heated Body", Kind = K.TraitKinds.RetaliationDamage, Damage = [TestStatBlocks.Roll("10", "fire")], Text = "A creature that hits it with a melee attack takes 10 fire damage." }]);
        var build = Sure("""{ "kind": "save_effect", "name": "Aura", "ability": "dex", "dc": 30, "dice": "7", "type": "radiant", "on_success": "none", "action_cost": "none" }""");
        var dropped = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(build, hp: 50, ac: 15, name: "Sure")], [SimKit.Monster(burning)], seed);
            var pc = fight.Named("Sure");
            pc.Hp = 5;
            fight.TakeTurn(pc);
            if (!pc.Down)
            {
                continue; // a natural 1: no hit, no burn
            }

            dropped++;
            Assert.InRange(fight.Named("Brute").TakenRaw, 1, 6); // the blade's 1d6 alone
        }

        Assert.True(dropped > 0, "the burn never dropped the build");
    }

    [Fact]
    public void Retaliation_ABurnThatDropsTheBuild_TakesNoBonusAction()
    {
        // The Bonus Action comes after the Action: a build the burn dropped at its own blade's hit is at 0 HP and takes
        // none, so the flat 7 of its Bonus Action flare (DC 30: the brute always fails) never lands.
        var burning = Brute([TestStatBlocks.Attack("Touch", 5, "1d6", "fire")], traits:
            [new StatBlockTrait { Name = "Heated Body", Kind = K.TraitKinds.RetaliationDamage, Damage = [TestStatBlocks.Roll("10", "fire")], Text = "A creature that hits it with a melee attack takes 10 fire damage." }]);
        var build = Sure("""{ "kind": "save_effect", "name": "Flare", "ability": "dex", "dc": 30, "dice": "7", "type": "radiant", "on_success": "none", "action_cost": "bonus_action" }""");
        var dropped = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(build, hp: 50, ac: 15, name: "Sure")], [SimKit.Monster(burning)])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            var pc = fight.Named("Sure");
            pc.Hp = 5;
            fight.TakeTurn(pc);
            if (!pc.Down)
            {
                continue; // a natural 1: no hit, no burn
            }

            dropped++;
            Assert.InRange(fight.Named("Brute").TakenRaw, 1, 6); // the blade's 1d6 alone
            Assert.DoesNotContain("Flare", log.Finish(string.Empty));
        }

        Assert.True(dropped > 0, "the burn never dropped the build");
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(50, true)]
    public void Retaliation_ABurnThatDropsTheBuild_MakesNoCleaveAttack(int hp, bool cleaves)
    {
        // Cleave's attack against a second creature comes after the hit's effects, the burn included: dropped by it (5 HP),
        // the build makes none; standing after it (50 HP), the same hit cleaves into the second brute.
        var burning = Brute([TestStatBlocks.Attack("Touch", 5, "1d6", "fire")], traits:
            [new StatBlockTrait { Name = "Heated Body", Kind = K.TraitKinds.RetaliationDamage, Damage = [TestStatBlocks.Roll("10", "fire")], Text = "A creature that hits it with a melee attack takes 10 fire damage." }]);
        const string cleaver = """
            { "name": "Cleaver", "edition": "2024", "level": 5, "abilities": {"str": 10},
              "attacks": [{ "name": "Greataxe", "to_hit": {"total": 30}, "damage": "1d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"], "mastery": "cleave" }] }
            """;
        var hits = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(cleaver, hp: 50, ac: 15, name: "Cleaver")], [SimKit.Monster(burning, count: 2)])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            var pc = fight.Named("Cleaver");
            pc.Hp = hp;
            fight.TakeTurn(pc);
            var text = log.Finish(string.Empty);
            if (!text.Contains("Heated Body on Cleaver", StringComparison.Ordinal))
            {
                continue; // a natural 1: no hit, no burn
            }

            hits++;
            Assert.Equal(!cleaves, pc.Down);
            Assert.Equal(cleaves, text.Contains("(Cleave attack) vs", StringComparison.Ordinal));
        }

        Assert.True(hits > 0, "the axe never hit");
    }

    [Theory]
    [InlineData(K.EffectKinds.Condition, 0, "Touch")]
    [InlineData(K.EffectKinds.Save, 30, "Touch")]
    [InlineData(K.EffectKinds.Save, 1, "Claw")]
    public void MonsterChoice_AnAttacksConditionOnAHit_IsWorthTheConditionTimesTheChanceItLands(string kind, int dc, string expected)
    {
        // A paralyzing Touch (1 damage) against a Claw (10): paralysis takes the fighter's turns, worth its threat (about
        // 17.6 a round), times the hit (0.95) and, for a save rider, the failed save — certain at DC 30 against Con +3, never
        // at DC 1, where the Claw's 9.5 wins. Valued at its damage alone, the Touch would always lose.
        var paralyze = new ConditionEffect { Condition = "paralyzed", Duration = K.Durations.UntilEndOfTargetTurn };
        var rider = kind == K.EffectKinds.Save
            ? new ActionEffect { Kind = kind, Save = new SaveSpec("con", dc, K.OnSuccess.None), Condition = paralyze }
            : new ActionEffect { Kind = kind, Condition = paralyze };
        var brute = Brute([TestStatBlocks.Attack("Claw", 30, "10", "slashing"), TestStatBlocks.Attack("Touch", 30, "1", "cold", onHit: [rider])]);
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(brute)])).Setup);
        var text = Log(fight, fight.Creatures[1]);
        Assert.Contains($"{expected} vs Fighter", text);
        Assert.DoesNotContain($"{(expected == "Touch" ? "Claw" : "Touch")} vs", text);
    }

    [Fact]
    public void TraitAdvantage_BloodiedAtExactlyHalf_BloodFrenzyOnlyAgainstTheWounded()
    {
        var fury = Brute([TestStatBlocks.Attack("Claw", 5, "1d6", "slashing")], traits: [TestStatBlocks.Trait("Bloodied Fury", K.TraitKinds.AdvantageWhileBloodied)], hp: 40);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(fury)]);
        var monster = fight.Named("Brute");
        monster.Hp = 20; // exactly half: Bloodied
        Assert.True(fight.TraitAdvantage(monster, fight.Named("Fighter"), true));

        var frenzy = Brute([TestStatBlocks.Attack("Bite", 5, "1d6", "piercing")], traits: [TestStatBlocks.Trait("Blood Frenzy", K.TraitKinds.BloodFrenzy)]);
        var shark = Scripted.Begin([Fighter], [SimKit.Monster(frenzy)]);
        var pc = shark.Named("Fighter");
        Assert.False(shark.TraitAdvantage(shark.Named("Brute"), pc, true)); // unhurt
        pc.Hp -= 1;
        Assert.True(shark.TraitAdvantage(shark.Named("Brute"), pc, true));
    }

    [Fact]
    public void MonsterTurn_NothingUsable_TakesTheDodge()
    {
        var spent = Brute([TestStatBlocks.SaveAction("Breath", "dex", 15, "6d6", "fire", area: new AreaSpec(K.Shapes.Cone, 15), usage: new UsageSpec(K.UsageKinds.PerDay, Uses: 1))]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(spent)]);
        var monster = fight.Named("Brute");
        fight.TakeTurn(monster); // breathes
        fight.TakeTurn(monster); // nothing left
        Assert.True(monster.IsDodging);
    }

    [Fact]
    public void MonsterAttack_AMeleeOrRangedWeaponIsThrownFromTheBackLine()
    {
        // An ogre placed on the back line throws its javelin at range: focus fire then reaches the back-line caster (30 HP)
        // past the fighter (44 HP); used as a melee weapon it could only reach the fighter.
        var caster = SimKit.Pc("""{ "name": "Caster", "edition": "2024", "level": 5, "abilities": {"int": 18}, "attacks": [{ "name": "Fire Bolt", "to_hit": {"ability": "int"}, "damage": "1d10", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"] }] }""", hp: 30, ac: 12, name: "Caster");
        var backOgre = new SimulationCombatant(new CombatantSpec { Monster = "Ogre", Position = "back" }, TestStatBlocks.Ogre);
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([Fighter, caster], [backOgre], policies: new PolicySpec { Enemies = "focus_fire" })).Setup);
        Assert.Contains("Javelin vs Caster", Log(fight, fight.Creatures[2]));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Build turns.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CritOrKill_TheKillTriggersTheBonusAttack()
    {
        static double MeanKills(string trigger)
        {
            var build = Sure($$"""{ "kind": "extra_attack", "name": "Hew", "attack": "Blade", "action": "bonus_action", "trigger": "{{trigger}}" }""");
            var kills = 0;
            for (ulong seed = 1; seed <= 400; seed++)
            {
                var fight = Scripted.Begin([SimKit.Pc(build, hp: 50, ac: 15, name: "Sure")], [SimKit.Monster(TestStatBlocks.Goblin, count: 3, hp: 1)], seed);
                var pc = fight.Named("Sure");
                fight.TakeTurn(pc);
                kills += pc.Kills;
            }

            return kills / 400.0;
        }

        // One attack that nearly always kills a 1-HP goblin, and a bonus attack that kills another when triggered.
        // E[kills]: crit_or_kill 0.95 + 0.95² = 1.8525; crit 0.95 + 0.05 × 0.95 = 0.9975 (400 turns: SE ≈ 0.02).
        Assert.InRange(MeanKills("crit_or_kill"), 1.75, 1.95);
        Assert.InRange(MeanKills("crit"), 0.9, 1.1);
    }

    [Fact]
    public void CritOrKill_DroppingADyingNpcToZeroAlsoTriggers()
    {
        // "Reduce a creature to 0 hit points": an NPC that makes death saves is only dying, not dead, and still triggers it.
        // A flat 6 drops a 6 HP bandit to exactly 0: dying, not dead (no massive damage), and the Hew follows.
        const string build = """
            { "name": "Sure", "edition": "2024", "level": 5, "abilities": {"str": 10},
              "attacks": [{ "name": "Blade", "to_hit": {"total": 30}, "damage": "6", "damage_type": "slashing", "properties": ["melee"] }],
              "modifiers": [{ "kind": "extra_attack", "name": "Hew", "attack": "Blade", "action": "bonus_action", "trigger": "crit_or_kill" }] }
            """;
        var npc = new SimulationCombatant(new CombatantSpec { Name = "Bandit", Build = SimKit.Build(SimKit.Commoner), Hp = 6, Ac = 5, DeathSaves = true, Count = 2 });
        var both = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(build, hp: 50, ac: 15, name: "Sure")], [npc], seed);
            fight.TakeTurn(fight.Named("Sure"));
            Assert.False(fight.Named("Bandit").Dead);
            both += fight.Named("Bandit").Down && fight.Named("Bandit 2").Down ? 1 : 0;
        }

        Assert.True(both >= 15, $"both bandits down in {both} of 20 turns");
    }

    [Fact]
    public void Vex_AHitThatDealsNoDamage_GrantsNoAdvantage()
    {
        // 2024 Vex: "If you hit a creature with this weapon and deal damage to the creature". A piercing-immune target takes
        // nothing from the shortsword, so no Vex is pending after the turn.
        const string vex = """
            { "name": "Vex", "edition": "2024", "level": 5, "abilities": {"dex": 18},
              "attacks": [{ "name": "Shortsword", "to_hit": {"total": 30}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"], "mastery": "vex" }] }
            """;
        var golem = TestStatBlocks.Create("Golem", 15, 500, "50d10", (18, 9, 18, 3, 11, 1), [TestStatBlocks.Attack("Slam", 7, "2d8+4", "bludgeoning")],
            immunities: [new DamageAdjustment("piercing", null, "piercing")]);
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(vex, hp: 40, ac: 15, name: "Vex")], [SimKit.Monster(golem)], seed);
            var pc = fight.Named("Vex");
            fight.TakeTurn(pc);
            Assert.Equal(-1, pc.VexTarget);
        }
    }

    [Fact]
    public void ActionChoice_ASaveTheTargetFailsAutomatically_IsValuedAsCertain()
    {
        // Against a stunned ogre (Str +4) a DC 11 Str save effect for a flat 4 fails for sure: worth 4, more than the flat 3 of
        // the blade (read as a normal save, 30% × 4 = 1.2, it would lose to the blade).
        var build = SimKit.Pc("""
            { "name": "Shover", "edition": "2024", "level": 5, "abilities": {"str": 10},
              "attacks": [{ "name": "Blade", "to_hit": {"total": 30}, "damage": "3", "damage_type": "slashing", "properties": ["melee"] }],
              "modifiers": [{ "kind": "save_effect", "name": "Shove", "ability": "str", "dc": 11, "dice": "4", "type": "bludgeoning", "on_success": "none" }] }
            """, hp: 40, ac: 15, name: "Shover");
        var fight = Scripted.Begin([build], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var ogre = fight.Named("Ogre");
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Stunned, Duration = DurationKind.Fight }, 0);
        fight.TakeTurn(fight.Named("Shover"));
        Assert.Equal(4, ogre.TakenRaw);
    }

    [Fact]
    public void Healing_DownedPolicy_LeavesADownedMonsterAllyToItsOwnRegeneration()
    {
        // "downed" heals allies that make death saves; a troll ally at 0 HP gets up by itself and is not healed.
        var healer = SimKit.Pc("""
            { "name": "Healer", "edition": "2024", "level": 5, "abilities": {"wis": 18},
              "attacks": [{ "name": "Mace", "to_hit": {"total": 5}, "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
              "modifiers": [{ "kind": "heal", "name": "Healing Word", "dice": "2d4", "amount": "wis", "action_cost": "bonus_action", "resource": {"uses": 3, "per": "long_rest"} }] }
            """, hp: 40, ac: 15, name: "Healer");
        var fight = Scripted.Begin([healer, SimKit.Monster(TestStatBlocks.Troll)], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var troll = fight.Named("Troll");
        fight.ApplyDamage(null, troll, Scripted.Damage(84), false, false, false, false, false);
        Assert.True(troll.Down);
        fight.TakeTurn(fight.Named("Healer"));
        Assert.True(troll.Down);
        Assert.Equal(0, troll.Hp);
    }

    [Fact]
    public void Healing_DownedPolicy_HealsTheFallenAllyWithTheBonusAction()
    {
        const string cleric = """
            { "name": "Cleric", "edition": "2024", "level": 5, "abilities": {"wis": 18},
              "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
              "modifiers": [{ "kind": "heal", "name": "Healing Word", "dice": "2d4", "amount": "wis", "action_cost": "bonus_action", "resource": {"uses": 3, "per": "long_rest"} }] }
            """;
        var fight = Scripted.Begin([Fighter, SimKit.Pc(cleric, hp: 38, ac: 18, name: "Cleric")], [SimKit.Monster(TestStatBlocks.Sandbag())]);
        var fighter = fight.Named("Fighter");
        var healer = fight.Named("Cleric");

        fight.TakeTurn(healer);
        Assert.Equal(0, healer.Pc!.Used[0]); // nobody is down: the downed policy holds the heal

        fight.ApplyDamage(null, fighter, Scripted.Damage(44), false, false, false, false, false);
        Assert.True(fighter.Down);
        fight.TakeTurn(healer);
        Assert.False(fighter.Down);
        Assert.InRange(fighter.Hp, 6, 12);
        Assert.Equal(1, healer.Pc.Used[0]);
    }

    [Fact]
    public void Restrained_WithAnEscapeDc_SpendsTheActionEscaping()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Sandbag())]);
        var fighter = fight.Named("Fighter");
        var restraint = new ConditionTemplate { Condition = Cond.Restrained, Duration = DurationKind.UntilEscape, EscapeDc = 1 };
        fight.AddCondition(fight.Named("Sandbag"), fighter, restraint, 0);
        fight.TakeTurn(fighter);
        Assert.False(fighter.Has(Cond.Restrained)); // DC 1: always free
        Assert.Equal(0, fighter.DealtRaw); // the Action went to the escape
    }

    [Fact]
    public void Restrained_EscapeCheckUsesTheBetterOfStrengthAndDexterity()
    {
        // Str 8 (−1), Dex 18 (+4) against escape DC 5: d20 + 4 always escapes; with Str alone a 1–5 would not.
        var nimble = SimKit.Pc("""{ "name": "Nimble", "edition": "2024", "level": 5, "abilities": {"str": 8, "dex": 18}, "attacks": [{ "name": "Rapier", "to_hit": {"ability": "dex"}, "damage": "1d8", "damage_type": "piercing", "properties": ["melee", "finesse"] }] }""", hp: 40, ac: 15, name: "Nimble");
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = Scripted.Begin([nimble], [SimKit.Monster(TestStatBlocks.Ogre)], seed);
            var pc = fight.Named("Nimble");
            fight.AddCondition(fight.Named("Ogre"), pc, new ConditionTemplate { Condition = Cond.Restrained, Duration = DurationKind.UntilEscape, EscapeDc = 5 }, 0);
            fight.TakeTurn(pc);
            Assert.False(pc.Has(Cond.Restrained), $"seed {seed}");
        }
    }

    [Fact]
    public void Grapple_EndsWhenTheGrapplerIsIncapacitated()
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var fighter = fight.Named("Fighter");
        var ogre = fight.Named("Ogre");
        fight.AddCondition(ogre, fighter, new ConditionTemplate { Condition = Cond.Grappled, Duration = DurationKind.UntilEscape, EscapeDc = 13 }, 0);
        Assert.True(fighter.Has(Cond.Grappled));
        fight.AddCondition(null, ogre, new ConditionTemplate { Condition = Cond.Stunned, Duration = DurationKind.Fight }, 0);
        Assert.False(fighter.Has(Cond.Grappled));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // More traits, reactions and policies.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Relentless_OncePerFight_LeavesItAtOneHp()
    {
        var boar = TestStatBlocks.Create("Wereboar", 11, 78, "12d8+24", (17, 10, 15, 10, 11, 8), [TestStatBlocks.Attack("Tusks", 5, "2d6+3", "slashing")],
            traits: [TestStatBlocks.Trait("Relentless", K.TraitKinds.Relentless, amount: 14)]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(boar)]);
        var target = fight.Named("Wereboar");
        target.Hp = 5;
        fight.ApplyDamage(null, target, Scripted.Damage(12), false, false, false, false, false);
        Assert.Equal(1, target.Hp);
        fight.ApplyDamage(null, target, Scripted.Damage(12), false, false, false, false, false);
        Assert.True(target.Dead);

        var exactly = Scripted.Begin([Fighter], [SimKit.Monster(boar)]);
        var boundary = exactly.Named("Wereboar");
        boundary.Hp = 5;
        exactly.ApplyDamage(null, boundary, Scripted.Damage(14), false, false, false, false, false); // "14 damage or less"
        Assert.Equal(1, boundary.Hp);

        var big = Scripted.Begin([Fighter], [SimKit.Monster(boar)]);
        var other = big.Named("Wereboar");
        other.Hp = 5;
        big.ApplyDamage(null, other, Scripted.Damage(15), false, false, false, false, false); // more than 14: no Relentless
        Assert.True(other.Dead);
    }

    [Fact]
    public void Relentless_WithoutAThreshold_TurnsAnyDroppingBlow()
    {
        // A Relentless trait with no damage threshold (Relentless Endurance: "reduced to 0 hit points but not killed
        // outright") leaves it at 1 HP whatever the blow, once.
        var orc = TestStatBlocks.Create("Orc", 13, 15, "2d8+6", (16, 12, 16, 7, 11, 10), [TestStatBlocks.Attack("Greataxe", 5, "1d12+3", "slashing")],
            traits: [TestStatBlocks.Trait("Relentless", K.TraitKinds.Relentless)]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(orc)]);
        var target = fight.Named("Orc");
        fight.ApplyDamage(null, target, Scripted.Damage(500), false, false, false, false, false);
        Assert.Equal(1, target.Hp);
        fight.ApplyDamage(null, target, Scripted.Damage(500), false, false, false, false, false);
        Assert.True(target.Dead);
    }

    [Fact]
    public void ShieldSpell_TheBonusLastsUntilTheCastersNextTurn_NotOneAttack()
    {
        // 2024 Shield: "+5 bonus to AC, including against the triggering attack, until the start of your next turn" (2014
        // alike), so the rest of the swarm's attacks that turn meet AC 20, not the mage's AC 15.
        var mage = DndMcp.Tests.Srd.Combatants.CorrectedSrd.Shipped.StatBlock("2024", "mage"); // AC 15, Protective Magic 3/day
        const string swarm = """
            { "name": "Swarm", "edition": "2024", "level": 5, "abilities": {"str": 10},
              "attacks": [{ "name": "Blade", "count": 8, "to_hit": {"total": 5}, "damage": "1", "damage_type": "slashing", "properties": ["melee"] }] }
            """;
        var checkedTurns = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(swarm, hp: 500, ac: 30, name: "Swarm")], [SimKit.Monster(mage)])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            fight.TakeTurn(fight.Named("Swarm"));
            var lines = log.Finish(string.Empty).Split('\n');
            var shield = Array.FindIndex(lines, l => l.Contains("uses Shield", StringComparison.Ordinal));
            if (shield < 0)
            {
                continue;
            }

            // The line after the Shield line is the shielded attack itself; every attack after it meets AC 20.
            var later = lines.Skip(shield + 2).Where(l => l.Contains("(attack) vs Mage", StringComparison.Ordinal)).ToList();
            if (later.Count == 0)
            {
                continue;
            }

            checkedTurns++;
            Assert.All(later, l => Assert.Contains("vs AC 20", l));
        }

        Assert.True(checkedTurns > 0);
    }

    [Fact]
    public void ShieldSpell_TheCastersNextTurnStarts_TheBonusEnds()
    {
        // The other end of Shield's "until the start of your next turn": once the mage's turn has started, the swarm's next
        // attack meets its own AC 15 again (a Shield cast anew raises it only for the attacks after that one).
        var mage = DndMcp.Tests.Srd.Combatants.CorrectedSrd.Shipped.StatBlock("2024", "mage");
        const string swarm = """
            { "name": "Swarm", "edition": "2024", "level": 5, "abilities": {"str": 10},
              "attacks": [{ "name": "Blade", "count": 8, "to_hit": {"total": 5}, "damage": "1", "damage_type": "slashing", "properties": ["melee"] }] }
            """;
        var checkedTurns = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(swarm, hp: 500, ac: 30, name: "Swarm")], [SimKit.Monster(mage)])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            var caster = fight.Named("Mage");
            fight.TakeTurn(fight.Named("Swarm"));
            if (caster.ShieldAc == 0)
            {
                continue; // no Shield this turn
            }

            fight.TakeTurn(caster);
            Assert.Equal(0, caster.ShieldAc);

            var before = log.Finish(string.Empty).Length;
            fight.TakeTurn(fight.Named("Swarm"));
            var first = log.Finish(string.Empty)[before..].Split('\n').First(l => l.Contains("(attack) vs Mage", StringComparison.Ordinal));
            checkedTurns++;
            Assert.Contains("vs AC 15 ", first);
        }

        Assert.True(checkedTurns > 0);
    }

    [Fact]
    public void Parry_TurnsAHitIntoAMissWhenTheBonusIsEnough_OncePerRound()
    {
        // One attack at +5 against AC 10: it hits on 5+ (0.8); with a +10 parry only 15+ still hits (0.3).
        var duelist = TestStatBlocks.Create("Duelist", 10, 5000, "1d4", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Poke", -10, "1", "piercing")],
            reactions: [new StatBlockAction { Name = "Parry", Kind = K.ActionKinds.Parry, Slot = K.ActionSlots.Reaction, AcBonus = 10, Text = "The duelist adds 10 to its AC against one melee attack that would hit it." }]);
        const string jab = """
            { "name": "Jab", "edition": "2024", "level": 1, "attacks": [{ "name": "Jab", "to_hit": {"total": 5}, "damage": "1d4", "damage_type": "piercing", "properties": ["melee"] }] }
            """;
        var hits = 0;
        const int turns = 1_000;
        for (ulong seed = 1; seed <= turns; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(jab, hp: 10, ac: 10, name: "Jab")], [SimKit.Monster(duelist)], seed);
            var pc = fight.Named("Jab");
            fight.TakeTurn(pc);
            hits += pc.DealtRaw > 0 ? 1 : 0;
        }

        var rate = SimulationStatistics.Wilson(hits, turns, SimulationStatistics.Z999);
        Assert.InRange(0.3, rate.Low, rate.High);
    }

    [Fact]
    public void Parry_ATrueParry_CoversOnlyTheAttackItTurned()
    {
        // "The duelist adds 10 to its AC against one melee attack that would hit it": unlike Shield, the attacks after the
        // parried one meet its own AC 10 again.
        var duelist = TestStatBlocks.Create("Duelist", 10, 5000, "1d4", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Poke", -10, "1", "piercing")],
            reactions: [new StatBlockAction { Name = "Parry", Kind = K.ActionKinds.Parry, Slot = K.ActionSlots.Reaction, AcBonus = 10, Text = "The duelist adds 10 to its AC against one melee attack that would hit it." }]);
        const string flurry = """
            { "name": "Flurry", "edition": "2024", "level": 1, "attacks": [{ "name": "Jab", "count": 8, "to_hit": {"total": 5}, "damage": "1", "damage_type": "piercing", "properties": ["melee"] }] }
            """;
        var checkedTurns = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(flurry, hp: 500, ac: 30, name: "Flurry")], [SimKit.Monster(duelist)])).Setup);
            var log = new CombatLog(1_000_000);
            fight.Begin(seed, log);
            fight.TakeTurn(fight.Named("Flurry"));
            var lines = log.Finish(string.Empty).Split('\n');
            var parry = Array.FindIndex(lines, l => l.Contains("uses Parry", StringComparison.Ordinal));
            var later = parry < 0 ? [] : lines.Skip(parry + 2).Where(l => l.Contains("(attack) vs Duelist", StringComparison.Ordinal)).ToList();
            if (later.Count == 0)
            {
                continue;
            }

            checkedTurns++;
            Assert.All(later, l => Assert.Contains("vs AC 10 ", l));
        }

        Assert.True(checkedTurns > 0);
    }

    [Fact]
    public void Parry_ARangedAttackIsNotParried()
    {
        // The Parry text says "melee attack": a +5 arrow against AC 10 still hits on 5+ (0.8).
        var duelist = TestStatBlocks.Create("Duelist", 10, 5000, "1d4", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Poke", -10, "1", "piercing")],
            reactions: [new StatBlockAction { Name = "Parry", Kind = K.ActionKinds.Parry, Slot = K.ActionSlots.Reaction, AcBonus = 10, Text = "The duelist adds 10 to its AC against one melee attack that would hit it." }]);
        const string shot = """
            { "name": "Shot", "edition": "2024", "level": 1, "attacks": [{ "name": "Shortbow", "to_hit": {"total": 5}, "damage": "1d4", "damage_type": "piercing", "properties": ["ranged"] }] }
            """;
        var hits = 0;
        const int turns = 1_000;
        for (ulong seed = 1; seed <= turns; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(shot, hp: 10, ac: 10, name: "Shot")], [SimKit.Monster(duelist)], seed);
            var pc = fight.Named("Shot");
            fight.TakeTurn(pc);
            hits += pc.DealtRaw > 0 ? 1 : 0;
        }

        var rate = SimulationStatistics.Wilson(hits, turns, SimulationStatistics.Z999);
        Assert.InRange(0.8, rate.Low, rate.High);
    }

    [Fact]
    public void Resistance_AnUnqualifiedEntryCoversMagicalDamageEvenBesideAQualifiedOne()
    {
        var both = TestStatBlocks.Create("Both", 12, 500, "50d8", (10, 10, 10, 10, 10, 10), [TestStatBlocks.Attack("Claw", 4, "1d6", "slashing")],
            resistances: [new DamageAdjustment("slashing", StatBlockValues.DamageQualifiers.Nonmagical, "slashing from nonmagical attacks"), new DamageAdjustment("slashing", null, "slashing")]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(both)]);
        var target = fight.Named("Both");
        fight.ApplyDamage(null, target, Scripted.Damage(10), true, false, false, false, false);
        Assert.Equal(5, target.TakenRaw);
    }

    [Fact]
    public void DeathBurst_DamagesTheEnemiesAroundItWhenItDies()
    {
        var magmin = TestStatBlocks.Create("Magmin", 14, 9, "2d6+2", (7, 15, 12, 8, 11, 10), [TestStatBlocks.Attack("Touch", 4, "2d6", "fire")],
            traits: [new StatBlockTrait
            {
                Name = "Death Burst",
                Kind = K.TraitKinds.DeathBurst,
                Damage = [TestStatBlocks.Roll("2d6", "fire")],
                Save = new SaveSpec("dex", 30, K.OnSuccess.Half),
                Area = new AreaSpec(K.Shapes.Sphere, 10),
                Text = "When the magmin dies, it explodes.",
            }]);
        var fight = Scripted.Begin([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 2)], [SimKit.Monster(magmin)]);
        fight.ApplyDamage(fight.Named("Fighter"), fight.Named("Magmin"), Scripted.Damage(20), false, false, false, false, false);
        Assert.True(fight.Named("Magmin").Dead);
        Assert.True(fight.Named("Fighter").TakenRaw >= 2);
        Assert.True(fight.Named("Fighter 2").TakenRaw >= 2);
        Assert.Equal(1, fight.Named("Fighter").Kills);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(40, true)]
    public void SaveEnds_TheTargetRepeatsTheSaveAtTheEndOfItsTurn(int dc, bool stillHeld)
    {
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(TestStatBlocks.Ogre)]);
        var ogre = fight.Named("Ogre");
        var held = new ConditionTemplate { Condition = Cond.Paralyzed, Duration = DurationKind.SaveEnds, SaveAbility = "wis", SaveDc = dc };
        fight.AddCondition(fight.Named("Fighter"), ogre, held, 0);
        fight.TakeTurn(ogre);
        Assert.Equal(stillHeld, ogre.Has(Cond.Paralyzed));
        Assert.Equal(0, fight.Named("Fighter").TakenRaw); // paralyzed: no action
    }

    [Fact]
    public void Sap_TheSappedCreaturesNextAttackRollHasDisadvantage()
    {
        var sapper = Sure().Replace("\"properties\": [\"melee\"]", "\"properties\": [\"melee\"], \"mastery\": \"sap\"");
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(sapper, hp: 50, ac: 15, name: "Sure")], [SimKit.Monster(TestStatBlocks.Ogre)], seed);
            var pc = fight.Named("Sure");
            var ogre = fight.Named("Ogre");
            fight.TakeTurn(pc);
            if (pc.DealtRaw == 0)
            {
                continue;
            }

            Assert.Equal(pc.Id, ogre.SappedBy);
            Assert.Equal(Domain.Probability.D20Mode.Disadvantage, fight.AttackMode(ogre, pc, true, false, false, out _));
            Assert.Equal(Domain.Probability.D20Mode.Normal, fight.AttackMode(ogre, pc, true, false, false, out _)); // spent
        }
    }

    [Fact]
    public void Sap_TheSapperDiesBeforeTheSappedCreatureAttacks_ItEndsWhereTheSappersTurnComesRound()
    {
        // Sap lasts until the start of the sapper's next turn; a sapper killed outright (massive damage) takes no turn, but
        // its place in the order still comes round and the Disadvantage ends there.
        var sapper = Sure().Replace("\"properties\": [\"melee\"]", "\"properties\": [\"melee\"], \"mastery\": \"sap\"");
        var sapped = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            var fight = Scripted.Begin([SimKit.Pc(sapper, hp: 50, ac: 15, name: "Sure")], [SimKit.Monster(TestStatBlocks.Ogre)], seed);
            var pc = fight.Named("Sure");
            var ogre = fight.Named("Ogre");
            fight.TakeTurn(pc);
            if (ogre.SappedBy != pc.Id)
            {
                continue; // a natural 1
            }

            sapped++;
            fight.ApplyDamage(null, pc, Scripted.Damage(200), false, false, false, false, false);
            Assert.True(pc.Dead);
            Assert.Equal(pc.Id, ogre.SappedBy);
            fight.TakeTurn(pc);
            Assert.Equal(-1, ogre.SappedBy);
        }

        Assert.True(sapped > 0);
    }

    [Theory]
    [InlineData("focus_fire", "Commoner")]
    [InlineData("threat", "Fighter")]
    [InlineData("healer_first", "Cleric")]
    [InlineData("break_concentration", "Warlock")]
    public void EnemyTargeting_PicksPerPolicy(string policy, string expected)
    {
        var cleric = SimKit.Pc("""
            { "name": "Cleric", "edition": "2024", "level": 5, "abilities": {"wis": 16},
              "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
              "modifiers": [{ "kind": "heal", "name": "Cure Wounds", "dice": "2d8", "amount": "wis" }] }
            """, hp: 30, ac: 18, name: "Cleric");
        var warlock = SimKit.Pc("""
            { "name": "Warlock", "edition": "2024", "level": 5, "abilities": {"cha": 16},
              "attacks": [{ "name": "Pact blade", "damage": "1d8", "damage_type": "slashing", "properties": ["melee"] }],
              "modifiers": [{ "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "concentration": true }] }
            """, hp: 30, ac: 18, name: "Warlock");
        var fight = Scripted.Begin([SimKit.Pc(SimKit.Commoner, hp: 4, ac: 10, name: "Commoner"), Fighter, cleric, warlock], [SimKit.Monster(TestStatBlocks.Ogre)],
            policies: new PolicySpec { Enemies = policy });
        Assert.Equal(expected, fight.PickTarget(fight.Named("Ogre"), melee: true)!.Label);
    }

    [Fact]
    public void SelfOnlyHeal_SecondWindAtHalfHp()
    {
        var build = SimKit.Fighter2024.Replace("\"attacks\"", "\"modifiers\": [{ \"kind\": \"heal\", \"name\": \"Second Wind\", \"dice\": \"1d10\", \"amount\": 5, \"action_cost\": \"bonus_action\", \"self_only\": true, \"resource\": {\"uses\": 1, \"per\": \"short_rest\"} }], \"attacks\"");
        var fight = Scripted.Begin([SimKit.Pc(build, hp: 44, ac: 18, name: "Fighter")], [SimKit.Monster(TestStatBlocks.Sandbag())]);
        var pc = fight.Named("Fighter");
        fight.TakeTurn(pc);
        Assert.Equal(44, pc.Hp); // above half: kept

        pc.Hp = 20;
        fight.TakeTurn(pc);
        Assert.InRange(pc.Hp, 26, 35);
        Assert.Equal(1, pc.Pc!.Used[0]);
    }

    [Fact]
    public void PowerAttackAuto_NothingToGain_StaysOff()
    {
        // A tie keeps "auto" off (as in the closed form). Against a slashing-immune golem both choices are worth 0, so the
        // greatsword swings at +7, not at GWM's +2; against AC 10 the −5/+10 trade pays and it swings at +2.
        const string fighter = """
            { "name": "Fighter", "edition": "2014", "level": 5, "abilities": {"str": 18},
              "attacks": [{ "name": "Greatsword", "count": 2, "damage": "2d6", "damage_type": "slashing", "properties": ["melee", "heavy", "two-handed"] }],
              "modifiers": [{ "kind": "power_attack", "name": "Great Weapon Master" }] }
            """;
        var golem = TestStatBlocks.Create("Golem", 15, 500, "50d10", (18, 9, 18, 3, 11, 1), [TestStatBlocks.Attack("Slam", 7, "2d8+4", "bludgeoning")],
            immunities: [new DamageAdjustment("slashing", null, "slashing")]);
        foreach (var (target, bonus) in new[] { (golem, "+7"), (TestStatBlocks.Sandbag(ac: 10), "+2") })
        {
            var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(fighter, hp: 44, ac: 18, name: "Fighter")], [SimKit.Monster(target)])).Setup);
            var text = Log(fight, fight.Creatures[0]);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, $@"Greatsword \(attack\) vs {target.Name}: d20 \d+\{bonus} = ").Count);
        }
    }

    [Fact]
    public void AttackActionOnlyRider_RidesAWeaponAttack_NotASpellAttack()
    {
        // attack_action_only means "as part of the Attack action": a spell attack made with the Action is the spell's own
        // casting action. A flat 1 plus a 1d4 rider: the blade's hits deal 2 or more, Fire Bolt's hits deal exactly 1.
        const string rider = """{ "kind": "extra_damage", "name": "Strike", "dice": "1d4", "attack_action_only": true }""";
        var bolt = SimKit.Pc($$"""
            { "name": "Caster", "edition": "2024", "level": 5, "abilities": {"int": 18},
              "attacks": [{ "name": "Fire Bolt", "to_hit": {"total": 30}, "damage": "1", "damage_type": "fire", "ability_to_damage": false, "properties": ["ranged", "spell"] }],
              "modifiers": [{{rider}}] }
            """, hp: 40, ac: 15, name: "Caster");
        var blade = SimKit.Pc(Sure(rider).Replace("\"damage\": \"1d6\"", "\"damage\": \"1\""), hp: 40, ac: 15, name: "Blade");
        var hits = 0;
        for (ulong seed = 1; seed <= 10; seed++)
        {
            var spell = Scripted.Begin([bolt], [SimKit.Monster(TestStatBlocks.Sandbag(ac: 10))], seed);
            spell.TakeTurn(spell.Named("Caster"));
            Assert.InRange(spell.Named("Caster").DealtRaw, 0, 1);

            var weapon = Scripted.Begin([blade], [SimKit.Monster(TestStatBlocks.Sandbag(ac: 10))], seed);
            weapon.TakeTurn(weapon.Named("Blade"));
            Assert.NotEqual(1, weapon.Named("Blade").DealtRaw);
            hits += weapon.Named("Blade").DealtRaw > 0 ? 1 : 0;
        }

        Assert.True(hits > 5, $"the blade hit in {hits} of 10 turns");
    }

    [Fact]
    public void SetupConcentrationSaveEffect_StaysUpAfterItsFirstTurn_LaterActionsAttack()
    {
        // The cleric archetype's Spirit Guardians: the Action pays the setup once and the effect runs free every turn
        // after, so from round 2 the Action is the Attack action. The effect's own cast must not end the setup's concentration.
        var build = Sure("""{ "kind": "save_effect", "name": "Guardians", "ability": "wis", "dc": 15, "dice": "3d8", "type": "radiant", "on_success": "half", "targets": 1, "action_cost": "none", "concentration": true, "setup": "action" }""");
        var fight = Scripted.Begin([SimKit.Pc(build, hp: 200, ac: 15, name: "Sure")], [SimKit.Monster(TestStatBlocks.Sandbag(ac: 10))]);
        var pc = fight.Named("Sure");
        var sandbag = fight.Named("Sandbag");
        var guardians = pc.Pc!.Build.Setups[0].Source.Number;

        fight.TakeTurn(pc); // round 1: the Action pays the setup, the effect runs
        Assert.True(pc.Pc.Active[guardians], "the setup is still up after the turn that paid it");

        var before = sandbag.TakenRaw;
        fight.TakeTurn(pc); // round 2: no setup to pay, so the Action is the Attack action (a sure hit)
        Assert.True(sandbag.TakenRaw - before > 0);
        Assert.True(pc.Pc.Active[guardians]);
    }

    [Fact]
    public void OffhandAttack_OnlyAfterTheAttackAction_NotAfterASpellAttack()
    {
        const string build = """
            { "name": "Blade-lock", "edition": "2024", "level": 5, "abilities": {"cha": 18, "dex": 14},
              "attacks": [{ "name": "Eldritch Blast", "to_hit": {"ability": "cha"}, "damage": "1d10", "damage_type": "force", "ability_to_damage": false, "properties": ["ranged", "spell"], "cantrip": "beams" },
                          { "name": "Offhand dagger", "action": "bonus_action", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d4", "damage_type": "piercing", "properties": ["melee", "light", "finesse"] }] }
            """;
        var fight = new Fight(SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(build, hp: 40, ac: 15, name: "Blade-lock")], [SimKit.Monster(TestStatBlocks.Sandbag(ac: 5))])).Setup);
        var log = new CombatLog();
        fight.Begin(3, log);
        fight.TakeTurn(fight.Named("Blade-lock"));
        var text = log.Finish(string.Empty);
        Assert.Contains("Eldritch Blast", text);
        Assert.DoesNotContain("Offhand dagger", text);
    }

    [Fact]
    public void Reckless_AdvantageOnItsMeleeAttacks()
    {
        var berserker = TestStatBlocks.Create("Berserker", 13, 67, "9d8+27", (16, 12, 17, 9, 11, 9), [TestStatBlocks.Attack("Greataxe", 5, "1d12+3", "slashing")],
            traits: [TestStatBlocks.Trait("Reckless", K.TraitKinds.Reckless)]);
        var fight = Scripted.Begin([Fighter], [SimKit.Monster(berserker)]);
        Assert.True(fight.TraitAdvantage(fight.Named("Berserker"), fight.Named("Fighter"), melee: true));
        Assert.False(fight.TraitAdvantage(fight.Named("Berserker"), fight.Named("Fighter"), melee: false));
    }
}
