using DndMcp.Domain.Core;
using DndMcp.Domain.Simulation;
using Xunit;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Bad input is one <see cref="DndInputException"/> that names the item the way the host's argument guard does and says
/// what is accepted (contract §0, §5.1); a run over the work budget is refused up front with what to reduce.
/// </summary>
public sealed class SimulatorValidationTests
{
    private static readonly SimulationCombatant Fighter = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter");
    private static readonly SimulationCombatant Ogre = SimKit.Monster(TestStatBlocks.Ogre);

    private static string Refusal(SimulationSpec spec) => Assert.Throws<DndInputException>(() => Simulator.Run(spec, 1)).Message;

    [Fact]
    public void EmptySides_AreRefused()
    {
        var message = Refusal(SimKit.Spec([], []));
        Assert.StartsWith("Invalid simulation (2 problems):", message);
        Assert.Contains("party is empty", message);
        Assert.Contains("enemies is empty", message);
    }

    [Fact]
    public void BuildWithoutHpOrAc_NamesTheItem()
    {
        var message = Refusal(SimKit.Spec([new SimulationCombatant(new CombatantSpec { Name = "Fighter", Build = SimKit.Build(SimKit.Fighter2024) })], [Ogre]));
        Assert.Contains("party item 1 (Fighter): hp is required with a build, e.g. \"hp\": 44.", message);
        Assert.Contains("party item 1 (Fighter): ac is required with a build", message);
    }

    [Theory]
    [InlineData(true, true, "give only one of monster, build and archetype")]
    [InlineData(false, false, "give exactly one of monster")]
    public void SourcesOtherThanExactlyOne_AreRefused(bool build, bool archetype, string expected)
    {
        var spec = new CombatantSpec { Build = build ? SimKit.Build(SimKit.Fighter2024) : null, Archetype = archetype ? "fighter" : null, Level = 5, Hp = 40, Ac = 16 };
        Assert.Contains($"enemies item 1{(build ? " (Fighter)" : archetype ? " (fighter)" : "")}: {expected}", Refusal(SimKit.Spec([Fighter], [new SimulationCombatant(spec)])));
    }

    [Fact]
    public void Archetype_UnknownName_IsRefusedWithTheNames()
    {
        var message = Refusal(SimKit.Spec([new SimulationCombatant(new CombatantSpec { Archetype = "necromancer", Level = 5 })], [Ogre]));
        Assert.Contains("party item 1 (necromancer): archetype \"necromancer\" is not a party archetype; archetypes are fighter, barbarian, paladin, ranger, rogue, monk, cleric, druid, wizard, sorcerer, warlock, bard", message);
        Assert.Equal(12, PartyArchetypes.Names.Count);
    }

    [Fact]
    public void Ranges_AreCheckedAndCollected()
    {
        var bad = new SimulationCombatant(new CombatantSpec
        {
            Name = "Fighter", Build = SimKit.Build(SimKit.Fighter2024), Hp = 0, Ac = 41, Count = 21, Position = "middle", InitiativeBonus = 30,
        });
        var message = Refusal(SimKit.Spec([bad], [Ogre], iterations: 0, roundCap: 101));
        Assert.StartsWith("Invalid simulation (", message);
        Assert.Contains("iterations is 0; it is 1 to 100,000", message);
        Assert.Contains("round_cap is 101; it is 1 to 100", message);
        Assert.Contains("party item 1 (Fighter): hp is 0; it is 1 to 5000.", message);
        Assert.Contains("… and", message); // more than five problems: the rest are counted
    }

    [Fact]
    public void UnknownPolicy_ListsTheAcceptedValues()
    {
        var message = Refusal(SimKit.Spec([Fighter], [Ogre], policies: new PolicySpec { Party = "sideways" }));
        Assert.Contains("policies party \"sideways\" is not a party targeting policy; give focus_fire, spread or threat.", message);
    }

    [Fact]
    public void Policies_AreMatchedForgivingly()
    {
        var report = Simulator.Run(SimKit.Spec([Fighter], [Ogre], iterations: 10, policies: new PolicySpec { Party = "Focus Fire", Enemies = "break-concentration" }), 1);
        Assert.Equal("focus_fire", report.Policies[0].Value);
        Assert.Equal("break_concentration", report.Policies[1].Value);
    }

    [Fact]
    public void TooManyCombatants_IsRefused() =>
        Assert.Contains("the fight has 41 combatants (counting copies); at most 40",
            Refusal(SimKit.Spec([Fighter], [SimKit.Monster(TestStatBlocks.Goblin, count: 20), SimKit.Monster(TestStatBlocks.Goblin, count: 20)])));

    [Fact]
    public void EditionOnABuildEntry_IsRefused() =>
        Assert.Contains("edition goes inside the build",
            Refusal(SimKit.Spec([new SimulationCombatant(new CombatantSpec { Name = "Fighter", Build = SimKit.Build(SimKit.Fighter2024), Hp = 44, Ac = 18, Edition = "2014" })], [Ogre])));

    [Fact]
    public void InvalidBuild_IsReportedByTheResolverWithTheItem()
    {
        var broken = SimKit.Fighter2024.Replace("\"damage\": \"2d6\"", "\"damage\": \"2d6kh1\"");
        var message = Refusal(SimKit.Spec([SimKit.Pc(broken, hp: 44, ac: 18, name: "Fighter")], [Ogre]));
        Assert.StartsWith("Invalid party item 1 (Fighter) build:", message);
    }

    [Theory]
    [InlineData(0, "compare: member is 0; the party has 1 entry, so give 1 to 1.")]
    [InlineData(2, "compare: member is 2;")]
    public void Compare_MemberOutOfRange_IsRefused(int member, string expected)
    {
        var feature = SimKit.Feature("""{ "name": "Plus one", "modifiers": [{ "kind": "to_hit", "amount": 1 }] }""");
        Assert.Contains(expected, Refusal(SimKit.Spec([Fighter], [Ogre], compare: new CompareSpec { Member = member, Feature = feature })));
    }

    [Fact]
    public void Compare_OnAMonster_IsRefused()
    {
        var feature = SimKit.Feature("""{ "name": "Plus one", "modifiers": [{ "kind": "to_hit", "amount": 1 }] }""");
        Assert.Contains("compare: member 1 is a monster", Refusal(SimKit.Spec([SimKit.Monster(TestStatBlocks.Ogre)], [Ogre], compare: new CompareSpec { Member = 1, Feature = feature })));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.9)]
    [InlineData(double.NaN)]
    public void Precision_OutOfRange_IsRefused(double precision) =>
        Assert.Contains("precision is", Refusal(SimKit.Spec([Fighter], [Ogre], precision: precision)));

    [Fact]
    public void Replay_OutsideTheRun_IsRefused() =>
        Assert.Contains("replay is 11; give the number of one fight, 1 to 10.", Refusal(SimKit.Spec([Fighter], [Ogre], iterations: 10, replay: 11)));

    [Fact]
    public void OverTheWorkBudget_IsRefusedWithWhatToReduce()
    {
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, count: 20)], [SimKit.Monster(TestStatBlocks.Goblin, count: 20)], iterations: 100_000, roundCap: 100);
        var message = Refusal(spec);
        Assert.StartsWith("this simulation is too large: 100,000 fights × 40 combatants × round cap 100 = 400,000,000, over the limit of 20,000,000.", message);
        Assert.Contains("Lower iterations", message);
    }

    [Fact]
    public void ArchetypeEntry_NameAsTyped_TheSourceAndLabelUseTheCataloguesName()
    {
        // " WIZARD " is matched as "wizard": the report names what was simulated, not the caller's spelling of it.
        var wizard = new SimulationCombatant(new CombatantSpec { Archetype = " WIZARD ", Level = 3 });
        var entry = SimulationPreparation.Prepare(SimKit.Spec([wizard], [Ogre])).Entries[0];
        Assert.Equal(("Wizard", "archetype wizard (level 3, 2024)"), (entry.Label, entry.Source));
    }

    [Theory]
    [InlineData("action", false)]      // the 2024 rogue archetype's way: Nick already modelled
    [InlineData("bonus_action", true)] // the Light weapon's extra attack still costs the Bonus Action
    public void Assumptions_Nick_IsNotedOnlyOnABonusActionAttack(string action, bool noted)
    {
        var build = $$"""
            { "name": "Dual Wielder", "edition": "2024", "level": 5, "abilities": {"dex": 16},
              "attacks": [
                { "name": "Shortsword", "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"], "mastery": "vex" },
                { "name": "Scimitar", "action": "{{action}}", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "slashing", "properties": ["melee", "finesse", "light"], "mastery": "nick" }] }
            """;
        var assumptions = SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(build, hp: 38, ac: 15)], [Ogre])).Assumptions;
        var nick = assumptions.Where(a => a.Contains("Nick", StringComparison.Ordinal)).ToList();
        Assert.Equal(noted ? 1 : 0, nick.Count);
        if (noted)
        {
            Assert.Equal(
                "Dual Wielder: Scimitar: Nick changes only the action economy; model it by making the Light weapon's extra attack an " +
                "action attack (count) instead of a bonus_action one.", nick[0]);
        }
    }

    [Fact]
    public void Assumptions_NickWeaponInTheActionBesideABonusActionOffhand_IsNotedNamingBoth()
    {
        // The Nick text does not say which Light weapon must carry it: a Nick dagger in the Attack action beside an offhand
        // shortsword that still costs the Bonus Action has not modelled Nick (balance_dpr's rule and words).
        const string build = """
            { "name": "Dual Wielder", "edition": "2024", "level": 5, "abilities": {"dex": 16},
              "attacks": [
                { "name": "Dagger", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d4", "damage_type": "piercing", "properties": ["melee", "finesse", "light"], "mastery": "nick" },
                { "name": "Shortsword", "action": "bonus_action", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"], "mastery": "vex" }] }
            """;
        var assumptions = SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(build, hp: 38, ac: 15)], [Ogre])).Assumptions;

        Assert.Equal(
            "Dual Wielder: Shortsword: Nick (on Dagger) changes only the action economy; model it by making the Light weapon's extra " +
            "attack an action attack (count) instead of a bonus_action one.", Assert.Single(assumptions, a => a.Contains("Nick", StringComparison.Ordinal)));
    }

    [Fact]
    public void Assumptions_NickExtraAttackAlreadyInTheActionBesideADualWielderAttack_IsNotNoted()
    {
        // The common 2024 Nick build: the scimitar (Nick) and the offhand shortsword both attack in the Attack action, and
        // the Dual Wielder feat adds a bonus_action Light attack that is not the one Nick moves (balance_dpr's rule).
        const string build = """
            { "name": "Dual Wielder", "edition": "2024", "level": 5, "abilities": {"dex": 16},
              "attacks": [
                { "name": "Scimitar", "count": 2, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "slashing", "properties": ["melee", "finesse", "light"], "mastery": "nick" },
                { "name": "Shortsword", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"], "mastery": "vex" },
                { "name": "Dual Wielder Shortsword", "action": "bonus_action", "offhand": true, "to_hit": {"ability": "dex"}, "damage": "1d6", "damage_type": "piercing", "properties": ["melee", "finesse", "light"] }] }
            """;
        var assumptions = SimulationPreparation.Prepare(SimKit.Spec([SimKit.Pc(build, hp: 38, ac: 15)], [Ogre])).Assumptions;

        Assert.DoesNotContain(assumptions, a => a.Contains("Nick", StringComparison.Ordinal));
    }

    [Fact]
    public void Assumptions_SayHowAreasReactionsAndConditionsAreChosen()
    {
        // The engine's rules the report must state: areas take the standing first and then those at 0 HP, Shield lasts
        // until the caster's next turn, and a condition an action imposes is worth something to the monster choosing it.
        var assumptions = SimulationPreparation.Prepare(SimKit.Spec([Fighter], [Ogre])).Assumptions;
        Assert.Contains(assumptions, a => a.StartsWith("Areas catch the DMG's typical number of creatures", StringComparison.Ordinal) &&
                                          a.Contains("the standing ones first", StringComparison.Ordinal) &&
                                          a.Contains("those lying at 0 HP", StringComparison.Ordinal));
        Assert.Contains(assumptions, a => a.Contains("Shield (+5 AC) lasts until the start of the caster's next turn", StringComparison.Ordinal));
        Assert.Contains(assumptions, a => a.Contains("a Parry whose text says melee attack covers only a melee attack", StringComparison.Ordinal));
        Assert.Contains(assumptions, a => a.Contains("A condition an action imposes adds its worth times the chance it lands (an attack's hit, and a failed save where there is one)", StringComparison.Ordinal) &&
                                          a.Contains("a tenth for exhaustion, nothing for deafened", StringComparison.Ordinal));
        Assert.DoesNotContain(assumptions, a => a.Contains("reactions other than Parry are not used", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2014", "lich", true)]    // Power Word Kill, an auto_hit spell action
    [InlineData("2024", "solar", true)]   // Slaying Bow, a save that is only a Multiattack option
    [InlineData("2014", "solar", false)]  // Slaying Longbow: a save rider on a hit, aimed like any attack
    [InlineData("2014", "ogre", false)]
    public void Assumptions_ASingleTargetKill_SaysItIsAimedAtACreatureItKills(string edition, string monster, bool stated)
    {
        // The monsters line says they act against "the targets their side's policy picks"; a save or auto-hit kill is aimed
        // at a standing creature it kills instead (Fight.KillableAmong), and the report must say so where one is fought.
        var block = DndMcp.Tests.Srd.Combatants.CorrectedSrd.Shipped.StatBlock(edition, monster);
        var assumptions = SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(block)])).Assumptions;

        Assert.Equal(stated, assumptions.Any(a => a.StartsWith(
            "An outright kill by hit points (Power Word Kill, the 2024 solar's Slaying Bow) is aimed at a standing creature it kills, the side's policy choosing among those",
            StringComparison.Ordinal)));
    }

    [Fact]
    public void OverTheWorkBudget_PrecisionCountsItsFirstBatchWhateverIterationsSays()
    {
        // Precision runs at least its first 10,000 fights, whatever iterations says: 10,000 × 40 × 60 = 24 million is over
        // the budget, though 1,000 iterations alone (2.4 million) would pass.
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, count: 20)], [SimKit.Monster(TestStatBlocks.Commoner, count: 20)],
            iterations: 1_000, roundCap: 60, precision: 0.01);
        var message = Refusal(spec);
        Assert.StartsWith("this simulation is too large: 10,000 fights × 40 combatants × round cap 60 = 24,000,000, over the limit of 20,000,000. ", message);
        Assert.EndsWith(
            "Lower the round cap (most fights end well before 20 rounds) or the number of combatants, or give fewer iterations instead of " +
            "precision (it runs at least one batch of 10,000 fights).", message);
    }

    [Theory]
    [InlineData(4, 2, true)]   // charged 100,000 fights: 100,000 × 6 × 20 × 2 = 24 million
    [InlineData(5, 6, false)]  // 100,000 × 11 × 20 = 22 million
    public void Precision_AnOrdinaryFightPastTheBudgetAt100000Fights_IsAcceptedAndReachedInItsFirstBatch(int fighters, int ogres, bool compare)
    {
        // Precision is charged its first batch, not the 100,000 fights it may run: these were refused, and reach ±1% in
        // that first batch. Its later batches stop at the budget instead (SimulatorRunTests).
        var feature = SimKit.Feature("""{ "name": "Plus one", "modifiers": [{ "kind": "to_hit", "amount": 1 }] }""");
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, count: fighters)], [SimKit.Monster(TestStatBlocks.Ogre, count: ogres)],
            precision: 0.01, compare: compare ? new CompareSpec { Member = 1, Feature = feature } : null);

        var report = Simulator.Run(spec, 1);

        Assert.True(report.PrecisionReached);
        Assert.Equal(SimulationLimits.PrecisionBatch, report.Iterations);
    }

    [Fact]
    public void OverTheWorkBudget_CompareCountsEveryFightTwice()
    {
        // 15,000 × 40 × 20 = 12 million alone, but a comparison fights every one twice: 24 million, over the budget.
        var feature = SimKit.Feature("""{ "name": "Plus one", "modifiers": [{ "kind": "to_hit", "amount": 1 }] }""");
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, count: 20)], [SimKit.Monster(TestStatBlocks.Commoner, count: 20)], iterations: 15_000,
            compare: new CompareSpec { Member = 1, Feature = feature });
        Assert.StartsWith("this simulation is too large: 15,000 fights × 40 combatants × round cap 20 × 2 (compare) = 24,000,000", Refusal(spec));
    }

    [Theory]
    [InlineData("cone", 15, 2)]      // 15 ÷ 10, rounded up
    [InlineData("sphere", 20, 4)]
    [InlineData("emanation", 15, 3)] // a 2024 Emanation counts as a sphere of its size
    [InlineData("line", 100, 4)]
    [InlineData("cube", 10, 2)]
    [InlineData("cube", 5, 1)]
    public void AreaCount_IsTheDmgTableRoundedUp(string shape, int size, int expected) =>
        Assert.Equal(expected, CombatantCompiler.AreaCount(new AreaSpec(shape, size)));

    [Fact]
    public void MonsterEntry_GivenSavesAndHp_OverrideTheStatBlock_AndHpIsNeverRolled()
    {
        var ogre = new SimulationCombatant(new CombatantSpec { Monster = "Ogre", Hp = 100, Saves = new Domain.Features.SavesSpec { Wis = 9 } }, TestStatBlocks.Ogre);
        var template = SimulationPreparation.Prepare(SimKit.Spec([Fighter], [ogre], enemyHp: "roll")).Setup.Templates[1];
        Assert.Equal(9, template.Saves[4]);
        Assert.Equal(-3, template.Saves[3]); // Int 5: the stat block's own
        Assert.Equal(100, template.AverageHp);
        Assert.Null(template.RolledHp);
    }

    [Fact]
    public void Build_AnAcModifier_RaisesTheCombatantsArmorClass()
    {
        var shielded = SimKit.Pc("""{ "name": "Shielded", "edition": "2024", "level": 5, "abilities": {"str": 16}, "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }], "modifiers": [{ "kind": "ac", "name": "Shield of Faith", "amount": 2 }] }""", hp: 40, ac: 18);
        Assert.Equal(20, SimulationPreparation.Prepare(SimKit.Spec([shielded], [Ogre])).Setup.Templates[0].ArmorClass);
    }

    [Fact]
    public void MonsterEntry_LegendaryUses_AreTheNonLairCount()
    {
        // The fight is never in a lair: a 3 (4 in lair) legendary monster gets 3 a round.
        var lairBoss = TestStatBlocks.Sandbag(legendary: new LegendaryActions(3, 4, [TestStatBlocks.Attack("Tail", 5, "1d8", "bludgeoning", slot: StatBlockValues.ActionSlots.Legendary)]));
        Assert.Equal(3, SimulationPreparation.Prepare(SimKit.Spec([Fighter], [SimKit.Monster(lairBoss)])).Setup.Templates[1].LegendaryUses);
    }

    [Fact]
    public void EnemyHpRoll_RollsOnlyTheEnemies()
    {
        // "roll" is enemy hit points: an SRD monster fighting on the party's side keeps its average.
        var setup = SimulationPreparation.Prepare(SimKit.Spec([SimKit.Monster(TestStatBlocks.Ogre, name: "Ally")], [Ogre], enemyHp: "roll")).Setup;
        Assert.Null(setup.Templates[0].RolledHp);
        Assert.NotNull(setup.Templates[1].RolledHp);
    }

    [Fact]
    public void Precision_ReplacesIterations_WhichIsThenNotChecked()
    {
        var report = Simulator.Run(SimKit.Spec([Fighter], [Ogre], iterations: 0, precision: 0.02), 1);
        Assert.True(report.PrecisionReached);
    }

    [Fact]
    public void MonsterWithoutItsStatBlock_IsAHostBug()
    {
        var unresolved = new SimulationCombatant(new CombatantSpec { Monster = "Ogre" });
        Assert.Throws<ArgumentException>(() => Simulator.Run(SimKit.Spec([Fighter], [unresolved]), 1));
    }

    [Fact]
    public void Labels_AreUniqueAcrossCopiesAndEntries()
    {
        var report = Simulator.Run(SimKit.Spec([Fighter], [SimKit.Monster(TestStatBlocks.Goblin, count: 2), SimKit.Monster(TestStatBlocks.Goblin)], iterations: 5, replay: 1), 1);
        Assert.Contains("Goblin, Goblin 2", report.ReplayLog);
        Assert.Contains("Goblin 3", report.ReplayLog);
    }
}
