using DndMcp.Domain.Core;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Srd.Combatants;
using Xunit;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// <see cref="SimulationStartNames.Match"/>: which live-state names a combatant's simulation knows, by the very rule a
/// <see cref="CombatantStart"/> is matched with — so the tracker's loader can pass a named effect as an active setup only
/// when it is one and say "not simulated" itself otherwise, and the report never says it twice. Pinned field by field
/// against what a run actually matches.
/// </summary>
public sealed class SimulationStartNamesTests
{
    private static readonly SimulationCombatant Barbarian = new(new CombatantSpec { Archetype = "barbarian", Level = 5, Edition = "2024" });

    private static readonly SimulationCombatant SpiritCleric = new(new CombatantSpec { Archetype = "cleric", Level = 5, Edition = "2024" });

    private static readonly SimulationCombatant Warlock = SimKit.Pc("""{ "name": "Warlock", "preset": "warlock_baseline", "level": 5 }""", hp: 38, ac: 13, name: "Warlock");

    private static readonly SimulationCombatant Cleric = SimKit.Pc("""
        { "name": "Cleric", "edition": "2024", "level": 9, "abilities": {"wis": 18},
          "attacks": [{ "name": "Mace", "damage": "1d6", "damage_type": "bludgeoning", "properties": ["melee"] }],
          "modifiers": [{ "kind": "heal", "name": "Healing Word", "dice": "2d4", "amount": "wis", "action_cost": "bonus_action", "resource": {"uses": 3, "per": "long_rest"} },
                        { "kind": "save_effect", "name": "Hold Monster", "ability": "wis", "dc": 16, "condition": "paralyzed", "concentration": true, "resource": {"uses": 1, "per": "long_rest"} }] }
        """, hp: 60, ac: 18, name: "Cleric");

    private static readonly SimulationCombatant Dragon = SimKit.Monster(TestStatBlocks.AdultRedDragon);

    private static readonly SimulationCombatant Fighter = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter");

    private static readonly SimulationCombatant Ogre = SimKit.Monster(TestStatBlocks.Ogre);

    [Fact]
    public void Match_ABuildsSetupModifier_IsAnActiveSetup_AnythingElseIsUnmatched()
    {
        var match = SimulationStartNames.Match(Barbarian, ["rage", "Bladesong", "RAGE"]);
        Assert.Equal(["rage", "RAGE"], match.ActiveSetups);
        Assert.Equal(["Bladesong"], match.Unmatched);
        Assert.Empty(match.Spent);
        Assert.Empty(match.Concentration);
    }

    [Theory]
    [InlineData("cleric", "spirit guardians")] // a concentration modifier with a setup
    [InlineData("warlock", "HEX")] // a no-setup concentration rider
    public void Match_TheConcentrationModifier_IsConcentrationNeverAnActiveSetup(string who, string name)
    {
        var match = SimulationStartNames.Match(who == "cleric" ? SpiritCleric : Warlock, [name]);
        Assert.Equal([name], match.Concentration);
        Assert.Empty(match.ActiveSetups);
        Assert.Empty(match.Unmatched);
    }

    [Fact]
    public void Match_ABuildsResources_AreUsesLeft()
    {
        var match = SimulationStartNames.Match(Cleric, ["healing-word", "Hold Monster", "Bardic Inspiration"]);
        Assert.Equal(["healing-word", "Hold Monster"], match.UsesLeft);
        Assert.Equal(["Hold Monster"], match.Concentration);
        Assert.Empty(match.Spent);
        Assert.Equal(["Bardic Inspiration"], match.Unmatched);
    }

    [Fact]
    public void Match_AMonstersRecharge_IsUsesLeftAndSpent_AndAMonsterHasNoSetups()
    {
        var match = SimulationStartNames.Match(Dragon, ["fire breath", "Rage"]);
        Assert.Equal(["fire breath"], match.UsesLeft);
        Assert.Equal(["fire breath"], match.Spent);
        Assert.Empty(match.ActiveSetups);
        Assert.Equal(["Rage"], match.Unmatched);
    }

    [Fact]
    public void Match_ASlotPool_IsUsesLeftOnly()
    {
        var mage = CorrectedSrd.Shipped.StatBlocks("2014").First(b => b.SpellSlots.Count > 1 && b.Spells.Select(s => s.Usage.Pool).Distinct().Count() > 2);
        var pool = Scripted.Begin([Fighter], [SimKit.Monster(mage)]).Named(mage.Name).T.PoolNames[0];
        var match = SimulationStartNames.Match(SimKit.Monster(mage), [pool.ToUpperInvariant()]);
        Assert.Equal([pool.ToUpperInvariant()], match.UsesLeft);
        Assert.Empty(match.Spent);
        Assert.Empty(match.Unmatched);
    }

    [Fact]
    public void Match_NullAndBlankNames_AreInNoList()
    {
        var match = SimulationStartNames.Match(Barbarian, ["", null, "  ", "Rage"]);
        Assert.Equal(["Rage"], match.ActiveSetups);
        Assert.Empty(match.Unmatched);
    }

    [Fact]
    public void Match_ANameOnlyPlaceholder_MatchesNothing()
    {
        var placeholder = new SimulationCombatant(new CombatantSpec { Name = "Fallen" }, Start: new CombatantStart { Placeholder = true });
        Assert.Equal(["Rage", "Fire Breath"], SimulationStartNames.Match(placeholder, ["Rage", "Fire Breath"]).Unmatched);
    }

    private static readonly SimulationCombatant Cleric2014 = new(new CombatantSpec { Archetype = "cleric", Level = 5, Edition = "2014" });

    [Fact]
    public void Match_ABuildsModifierOrAttackByItsLabel_CanBeUnavailable_AResourcesFreshUsesAreSaid()
    {
        // CR03/CR04: the tracker's loader asks which once-a-fight spells the build has at its level (a modifier, or an
        // attack such as Spiritual Weapon) and how many uses a slot-funded modifier has fresh, before it shares the slots.
        var match = SimulationStartNames.Match(Cleric2014, ["Spirit Guardians", "spiritual weapon", "Healing Word", "Fireball"]);

        Assert.Equal(["Spirit Guardians", "spiritual weapon", "Healing Word"], match.Unavailable);
        Assert.Equal(4, Assert.Single(match.FreshUses, p => p.Key == "Healing Word").Value);
        Assert.Single(match.FreshUses);
        Assert.Equal(["Fireball"], match.Unmatched);
    }

    [Fact]
    public void Run_AnUnavailableSpell_IsNeverCast_AnUnknownNameIsAnAssumption_ABlankOneIsRefused()
    {
        // CR03: Spirit Guardians (a setup) and Spiritual Weapon (a Bonus Action attack) with no slot left are never used.
        static SimulationReport RunWith(IReadOnlyList<string> unavailable) =>
            Simulator.Run(SimKit.Spec([Cleric2014 with { Start = new CombatantStart { Unavailable = unavailable } }], [Ogre], iterations: 200), 3);

        var full = RunWith([]);
        var guardiansOnly = RunWith(["Spirit Guardians"]);
        var none = RunWith(["Spirit Guardians", "Spiritual Weapon"]);
        var unknown = RunWith(["Fireball"]);

        Assert.True(full.Combatants[0].DamageDealt.Mean > guardiansOnly.Combatants[0].DamageDealt.Mean);
        Assert.True(guardiansOnly.Combatants[0].DamageDealt.Mean > none.Combatants[0].DamageDealt.Mean); // the attack is never made
        Assert.Equal(full.Combatants[0].DamageDealt, unknown.Combatants[0].DamageDealt);
        Assert.Contains(unknown.Assumptions, a => a.Contains("the live state's \"Fireball\" match", StringComparison.Ordinal));
        var blank = Assert.Throws<DndInputException>(() => RunWith(["Spirit Guardians", " "]));
        Assert.Contains("start unavailable has a blank name; give each spell's modifier or attack label.", blank.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_AnUnavailableSpellItConcentratesOn_KeepsWorking_ButIsNotCastAgainOnceItEnds()
    {
        // CR03: Call Lightning held at the resume calls a bolt every turn; with no slot left, a broken concentration ends it
        // for the fight, where with a slot left it is cast again.
        var druid = new SimulationCombatant(new CombatantSpec { Archetype = "druid", Level = 5, Edition = "2014" });
        static SimulationReport RunWith(SimulationCombatant druid, IReadOnlyList<string> unavailable) =>
            Simulator.Run(SimKit.Spec([druid with { Start = new CombatantStart { Concentration = "Call Lightning", Unavailable = unavailable } }], [Ogre, Ogre], iterations: 300), 5);

        var recast = RunWith(druid, []);
        var held = RunWith(druid, ["Call Lightning"]);
        var never = Simulator.Run(SimKit.Spec([druid with { Start = new CombatantStart { Unavailable = ["Call Lightning"] } }], [Ogre, Ogre], iterations: 300), 5);

        Assert.True(recast.Combatants[0].DamageDealt.Mean > held.Combatants[0].DamageDealt.Mean);
        Assert.True(held.Combatants[0].DamageDealt.Mean > never.Combatants[0].DamageDealt.Mean);
    }

    private static readonly string[] Names = ["Rage", "Fire Breath", "Healing Word", "Hex", "Spirit Guardians", "Bladesong", "Hold Monster", "Wing Attack"];

    public static IEnumerable<object[]> AgreementRows() =>
        from who in new[] { "barbarian", "spirit cleric", "warlock", "cleric", "dragon" }
        from field in new[] { "uses_left", "spent", "active_setups" }
        select new object[] { who, field };

    [Theory]
    [MemberData(nameof(AgreementRows))]
    public void Match_AgreesWithTheRun_FieldByField(string who, string field)
    {
        var combatant = who switch
        {
            "barbarian" => Barbarian,
            "spirit cleric" => SpiritCleric,
            "warlock" => Warlock,
            "cleric" => Cleric,
            _ => Dragon,
        };
        var match = SimulationStartNames.Match(combatant, Names);
        var matched = field switch
        {
            "uses_left" => match.UsesLeft,
            "spent" => match.Spent,
            _ => match.ActiveSetups,
        };

        // What the helper says a field sets is matched by the run: no unmatched-names line.
        Assert.DoesNotContain(Run(combatant, field, matched).Assumptions, a => a.Contains("the live state's", StringComparison.Ordinal));

        // What it says matches nothing is exactly what the run's line names.
        var unmatched = Run(combatant, field, match.Unmatched);
        var line = Assert.Single(unmatched.Assumptions, a => a.Contains("the live state's", StringComparison.Ordinal));
        Assert.StartsWith($"{unmatched.Combatants.Single(c => c.Name != "Fighter" && c.Name != "Ogre").Name}: the live state's {string.Join(", ", match.Unmatched.Select(n => $"\"{n}\""))} match",
            line);
    }

    private static SimulationReport Run(SimulationCombatant combatant, string field, IReadOnlyList<string> names)
    {
        var start = field switch
        {
            "uses_left" => new CombatantStart { UsesLeft = names.ToDictionary(n => n, _ => 0) },
            "spent" => new CombatantStart { Spent = names },
            _ => new CombatantStart { ActiveSetups = names },
        };
        var seeded = combatant with { Start = start };
        var spec = combatant.Monster is null ? SimKit.Spec([seeded], [Ogre], iterations: 5) : SimKit.Spec([Fighter], [seeded], iterations: 5);
        return Simulator.Run(spec, 1);
    }

    [Fact]
    public void Match_AnEntryARunWouldRefuse_IsRefusedNamingTheCombatant()
    {
        var ex = Assert.Throws<DndInputException>(() => SimulationStartNames.Match(new SimulationCombatant(new CombatantSpec { Archetype = "cleric" }), ["Rage"]));
        Assert.Contains("combatant (cleric): archetype needs a level", ex.Message);
        Assert.Contains("edition \"2030\" is not",
            Assert.Throws<DndInputException>(() => SimulationStartNames.Match(Barbarian, ["Rage"], edition: "2030")).Message);
    }

    [Fact]
    public void Match_AnUnexpandedCharacter_IsAHostBug() =>
        Assert.Throws<ArgumentException>(() => SimulationStartNames.Match(new SimulationCombatant(new CombatantSpec { Character = "character:torch" }), ["Rage"]));

    [Theory]
    [InlineData("2024", true)] // Hunter's Mark from level 1 (Favored Enemy)
    [InlineData("2014", false)] // from level 2
    public void Match_AnArchetypeWithoutItsOwnEdition_FollowsTheFightsEdition(string edition, bool hasHuntersMark)
    {
        var ranger = new SimulationCombatant(new CombatantSpec { Archetype = "ranger", Level = 1 });
        var match = SimulationStartNames.Match(ranger, ["Hunter's Mark"], edition: edition);
        Assert.Equal(hasHuntersMark, match.Concentration.Count == 1);
        Assert.Equal(!hasHuntersMark, match.Unmatched.Count == 1);
    }
}
