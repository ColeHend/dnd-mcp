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
        Assert.StartsWith("this simulation is too large: 100,000 fights × 40 combatants × round cap 100 = 400,000,000, over the limit of 60,000,000.", message);
        Assert.Contains("Lower iterations", message);
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
