using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Simulation;
using Xunit;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// A fight resumed mid-way (<see cref="FightResume"/>, contract §11.3-5): the tracker's order instead of an initiative roll,
/// the first round starting at the turn-holder in the tracker's round, the round cap counted from there; a resumed run is
/// as deterministic as any (byte-identical at 1, 4 and 16 threads and whatever the chunk size). And a fight in a lair
/// (<see cref="SimulationSpec.Lair"/>): the in-lair counts, and an assumption line that says so.
/// </summary>
public sealed partial class SimulationResumeTests
{
    private static readonly SimulationCombatant Fighter = SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter");

    private static SimulationCombatant Sandbag(string name) => SimKit.Monster(TestStatBlocks.Sandbag(name), name: name);

    private static SimulationSpec Resumed(IReadOnlyList<SimulationCombatant> party, IReadOnlyList<SimulationCombatant> enemies, FightResume resume,
                                          int iterations = 200, int roundCap = 20, int? replay = null, string? surprise = null) => new()
    {
        Party = party,
        Enemies = enemies,
        Iterations = iterations,
        RoundCap = roundCap,
        Replay = replay,
        Surprise = surprise,
        Resume = resume,
    };

    private static List<string> Turns(string log) => TurnLine().Matches(log).Select(m => m.Groups[1].Value).ToList();

    /// <summary>A turn's first line: "Fighter's turn (HP 44/44)", "Bag 2's turn (0 HP, dying)".</summary>
    [GeneratedRegex(@"^(\S+(?: \d+)?)'s turn \((?:HP|0 HP|dead)", RegexOptions.Multiline)]
    private static partial Regex TurnLine();

    [Fact]
    public void Run_Resume_StartsAtTheTurnHolderInTheTrackersRoundAndOrder()
    {
        // Entries: 0 Fighter, 1 A, 2 B; the tracker's order B, Fighter, A, resumed at Fighter's turn in round 4: B has
        // acted this round. Two rounds from the resume, then the cap.
        var report = Simulator.Run(Resumed([Fighter], [Sandbag("A"), Sandbag("B")], new FightResume([2, 0, 1], 1, 4), iterations: 5, roundCap: 2, replay: 1), 3);
        var log = report.ReplayLog!;
        Assert.Equal(["Fighter", "A", "B", "Fighter", "A"], Turns(log));
        Assert.Contains("Resumed in round 4 at Fighter's turn (its start of turn is played again).", log);
        Assert.Contains("Order: B, Fighter, A", log);
        Assert.Contains($"Round 4{Environment.NewLine}", log);
        Assert.Contains($"Round 5{Environment.NewLine}", log);
        Assert.DoesNotContain("Round 6", log);
        Assert.DoesNotContain("Initiative:", log);
        Assert.Contains(report.Assumptions, a => a.StartsWith("Resumed from a live fight in round 4 at Fighter's turn", StringComparison.Ordinal));
        Assert.True(report.Resumed);
    }

    [Fact]
    public void Run_Resume_TheRoundCapAndTheRoundsCountFromTheResumedRound()
    {
        var report = Simulator.Run(Resumed([Fighter], [Sandbag("A")], new FightResume([1, 0], 0, 7), iterations: 50, roundCap: 3), 1);
        Assert.Equal(50, report.Draw.Count);
        Assert.Equal(3, report.Rounds.Histogram.Count);
        Assert.Equal(50, report.Rounds.Histogram[2]);
        Assert.Equal(3, report.Rounds.Mean.Mean);
    }

    [Fact]
    public void Run_Resume_ACopiedEntryActsTogetherInItsPlace()
    {
        var report = Simulator.Run(Resumed([Fighter], [SimKit.Monster(TestStatBlocks.Sandbag("Bag"), count: 2, name: "Bag")], new FightResume([1, 0], 0, 1),
            iterations: 2, roundCap: 1, replay: 1), 1);
        Assert.Equal(["Bag", "Bag 2", "Fighter"], Turns(report.ReplayLog!));
    }

    [Fact]
    public void Run_Resume_StartAtAfterACopiedEntry_IsAnEntryPosition()
    {
        // Entries: 0 Fighter, 1 Bag ×2, 2 C; the order Bag ×2, Fighter, C, resumed at C's turn: entry position 2, creature
        // position 3 (read as a creature position it would resume at the Fighter).
        var report = Simulator.Run(Resumed([Fighter], [SimKit.Monster(TestStatBlocks.Sandbag("Bag"), count: 2, name: "Bag"), Sandbag("C")], new FightResume([1, 0, 2], 2, 1),
            iterations: 2, roundCap: 2, replay: 1), 1);
        Assert.Equal(["C", "Bag", "Bag 2", "Fighter", "C"], Turns(report.ReplayLog!));
        Assert.Contains("Resumed in round 1 at C's turn", report.ReplayLog);
    }

    [Fact]
    public void Run_Resume_ASideAlreadyBeatenEndsBeforeAnyTurn()
    {
        var down = Fighter with { Start = new CombatantStart { Hp = 0 } };
        var report = Simulator.Run(Resumed([down], [Sandbag("A")], new FightResume([0, 1], 0, 3), iterations: 20, replay: 1), 1);
        Assert.Equal(20, report.PartyDefeated.Count);
        Assert.Equal(1, report.Rounds.Mean.Mean);
        Assert.Empty(Turns(report.ReplayLog!));
        Assert.Contains("Every party member is down: the party is defeated in round 3.", report.ReplayLog);
        Assert.Equal(20, report.AnyPartyDying.Count);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Determinism (contract §11.5).
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>A mid-fight state with something of everything: HP, temp HP, a dying PC, concentration holding a condition, a grapple, exhaustion, spent resources, a placeholder.</summary>
    private static SimulationSpec MidFight(int iterations = 2_000, int roundCap = 20) => Resumed(
        [
            Fighter with { Start = new CombatantStart { Hp = 17, Exhaustion = 1, Conditions = [new StartCondition("grappled", K.Durations.UntilEscape, SourceEntry: 4, EscapeDc: 14)] } },
            SimKit.Pc("""{ "name": "Warlock", "preset": "warlock_baseline", "level": 5 }""", hp: 38, ac: 13, name: "Warlock") with
            {
                Start = new CombatantStart { Hp = 0, DeathFailures = 1 },
            },
            new SimulationCombatant(new CombatantSpec { Archetype = "cleric", Level = 5, Edition = "2024" },
                Start: new CombatantStart { Hp = 30, TempHp = 4, Concentration = "Spirit Guardians", UsesLeft = new Dictionary<string, int> { ["Healing Word"] = 1 }, HasActed = true }),
        ],
        [
            SimKit.Monster(TestStatBlocks.Ogre, name: "Ogre") with
            {
                Start = new CombatantStart { Hp = 21, Conditions = [new StartCondition("frightened", K.Durations.Rounds, SourceEntry: 2, RoundsLeft: 4)] },
            },
            SimKit.Monster(TestStatBlocks.Ogre, name: "Grappler") with { Start = new CombatantStart { Hp = 40, ReactionUsed = true } },
            SimKit.Monster(TestStatBlocks.Troll) with { Start = new CombatantStart { Hp = 0 } },
            new SimulationCombatant(new CombatantSpec { Name = "Dead Mage" }, Start: new CombatantStart { Placeholder = true }),
            SimKit.Monster(TestStatBlocks.Wolf, count: 2),
        ],
        new FightResume([3, 0, 4, 1, 6, 2, 5, 7], 2, 3), iterations, roundCap, replay: 2);

    private static string Canonical(SimulationReport report) => JsonSerializer.Serialize(report);

    [Fact]
    public void Run_FromState_ByteIdenticalReportAt1_4And16Threads()
    {
        var one = Canonical(Simulator.Run(MidFight(), 42, maxThreads: 1));
        Assert.Equal(one, Canonical(Simulator.Run(MidFight(), 42, maxThreads: 4)));
        Assert.Equal(one, Canonical(Simulator.Run(MidFight(), 42, maxThreads: 16)));
        Assert.NotEqual(one, Canonical(Simulator.Run(MidFight(), 43, maxThreads: 4)));
    }

    [Fact]
    public void Run_FromState_ByteIdenticalWhateverTheChunkSize()
    {
        // At round cap 100 a fight is 900 work units: 72 fights a chunk on one thread, 14 on seven (100 fights over at least
        // seven chunks), 6 on sixteen.
        var spec = MidFight(iterations: 100, roundCap: 100);
        Assert.Equal((72, 14, 6), (Simulator.ChunkFights(900, 100, 1), Simulator.ChunkFights(900, 100, 7), Simulator.ChunkFights(900, 100, 16)));
        var one = Canonical(Simulator.Run(spec, 9, maxThreads: 1));
        Assert.Equal(one, Canonical(Simulator.Run(spec, 9, maxThreads: 7)));
        Assert.Equal(one, Canonical(Simulator.Run(spec, 9, maxThreads: 16)));
    }

    [Fact]
    public void Run_FromState_IsTheSumOfItsFights_EachReproducibleAlone()
    {
        const int fights = 40;
        var report = Simulator.Run(MidFight(fights), 7);
        var wins = 0;
        for (var i = 0; i < fights; i++)
        {
            wins += Simulator.RunOne(MidFight(fights), 7, i).Outcome.Outcome == SimulationValues.Outcomes.PartyWins ? 1 : 0;
        }

        Assert.Equal(report.PartyWins.Count, wins);
    }

    [Fact]
    public void Run_FromState_ReportsEveryoneButThePlaceholder()
    {
        var report = Simulator.Run(MidFight(200), 5);
        Assert.Equal(["Fighter", "Warlock", "Cleric", "Ogre", "Grappler", "Troll", "Wolf"], report.Combatants.Select(c => c.Name));
        Assert.Equal([17.0, 0.0, 30.0], report.Combatants.Take(3).Select(c => c.MaxHp));
        Assert.Equal(1, report.Combatants[2].Resources.Single(r => r.Name == "Healing Word").Available);
        Assert.Contains("Start: Dead Mage (dead; a placeholder holding its place in the order)", report.ReplayLog);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Lair (contract §11.2: in-lair counts in the Domain, a conditional assumption).
    // ------------------------------------------------------------------------------------------------------------------

    private static StatBlock LairDragon => TestStatBlocks.Create("Lair Dragon", 18, 200, "16d12+96", (23, 10, 21, 14, 11, 19),
        [TestStatBlocks.Attack("Rend", 11, "2d8+6", "slashing")], edition: "2024", cr: "15",
        legendary: new LegendaryActions(3, 4, [TestStatBlocks.Attack("Tail", 11, "2d8+6", "bludgeoning")]), legendaryResistance: 3) with { LegendaryResistanceInLair = 4 };

    private const string NotInLair = "No lair actions: the fight is not in a lair (legendary action and Legendary Resistance counts are the non-lair ones).";

    private const string InLair = "In a lair: legendary action and Legendary Resistance counts are the in-lair ones where the stat block has them; lair actions themselves are never simulated.";

    [Theory]
    [InlineData(false, 3, 3)]
    [InlineData(true, 4, 4)]
    public void Prepare_Lair_UsesTheInLairCounts(bool lair, int actions, int resistance)
    {
        var spec = new SimulationSpec { Party = [Fighter], Enemies = [SimKit.Monster(LairDragon)], Lair = lair };
        var dragon = Scripted.Begin(spec).Named("Lair Dragon");
        Assert.Equal((actions, resistance), (dragon.T.LegendaryUses, dragon.T.LegendaryResistance));
        Assert.Equal((actions, resistance), (dragon.LegendaryUsesLeft, dragon.LegendaryResistanceLeft));
    }

    [Fact]
    public void Prepare_LairWithoutInLairCounts_KeepsTheOnlyCounts()
    {
        // The 2014 data has no in-lair counts: a lair changes nothing but the assumption line.
        var spec = new SimulationSpec { Party = [Fighter], Enemies = [SimKit.Monster(TestStatBlocks.AdultRedDragon)], Lair = true };
        var dragon = Scripted.Begin(spec).Named("Adult Red Dragon");
        Assert.Equal((3, 3), (dragon.T.LegendaryUses, dragon.T.LegendaryResistance));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_Lair_TheAssumptionSaysWhichCountsAndThatLairActionsAreNeverSimulated(bool lair)
    {
        var report = Simulator.Run(new SimulationSpec { Party = [Fighter], Enemies = [SimKit.Monster(LairDragon)], Iterations = 20, Lair = lair }, 1);
        Assert.Contains(lair ? InLair : NotInLair, report.Assumptions);
        Assert.DoesNotContain(lair ? NotInLair : InLair, report.Assumptions);
    }
}
