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
        Assert.False(troll.Standing); // down: a side with nobody above 0 HP is beaten, even a troll's

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

        var big = Scripted.Begin([Fighter], [SimKit.Monster(boar)]);
        var other = big.Named("Wereboar");
        other.Hp = 5;
        big.ApplyDamage(null, other, Scripted.Damage(15), false, false, false, false, false); // more than 14: no Relentless
        Assert.True(other.Dead);
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
