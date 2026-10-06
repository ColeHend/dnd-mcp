using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using SD = DndMcp.Domain.Simulation.StatBlockValues.Durations;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: an encounter becomes the simulator's input per contract §6.11 — the party from sheets in <c>order_key</c>
/// order through <see cref="SheetSimulation.Entry"/> (members it refuses left out with the fix), enemies from their
/// snapshots grouped when identical (HP and AC only when they differ from the snapshot), neutral and left combatants left
/// out with a note; and, with <c>from_state</c>, one entry per creature with its live state, the tracker's order and turn,
/// and the duration mapping table (rounds_left with 0 legal, the resumed turn's flag, until_removed → fight, Prone →
/// until_stands, zero_hp → nothing, end_of_round → rounds on the last creature, concentration held, placeholders for dead
/// sources) — and the simulator accepts and runs what it produces.
/// </summary>
public sealed class TrackerSimulationTests
{
    private const string BelmakorProfile = """
        {"name":"Belmakor L12 Bladesinger (fixture)","edition":"2014","level":12,
         "abilities":{"str":11,"dex":20,"con":16,"int":20,"wis":13,"cha":12},
         "attacks":[
          {"name":"Scimitar","count":2,"to_hit":{"ability":"dex"},"damage":"1d6","damage_type":"slashing","properties":["melee","finesse","light","magical"]},
          {"name":"Offhand scimitar","action":"bonus_action","offhand":true,"to_hit":{"ability":"dex"},"damage":"1d6","damage_type":"slashing","properties":["melee","finesse","light","magical"]}],
         "modifiers":[{"kind":"ac","name":"Bladesong","amount":"int","setup":"bonus_action","resource":{"uses":4,"per":"long_rest"}}]}
        """;

    private const string SlayerProfile = """
        {"name":"Amethyst Dragon Slayer L8 (fixture)","edition":"2024","level":8,
         "abilities":{"str":18,"dex":14,"con":14,"int":10,"wis":12,"cha":10},
         "attacks":[{"name":"Draconic Strike","count":2,"damage":"1d8","damage_type":"bludgeoning","properties":["melee"]}],
         "modifiers":[{"kind":"extra_damage","name":"Amethyst rider","dice":"1d6","type":"psychic","when":"first_hit_per_turn"}]}
        """;

    private static CharacterSheet WithProfile(CharacterSheet sheet, string profile, string edition) =>
        SheetUpdate.Apply(sheet, null, DslJson.Deserialize<BuildSpec>(profile, "sim_profile"), edition).Sheet;

    private static IReadOnlyList<AddEntry> PartyAWithProfile() =>
        [.. PartyA().Select(p => p.EntityId == "e-belmakor" ? p with { Sheet = WithProfile(p.Sheet!, BelmakorProfile, E2014) } : p)];

    private static Dictionary<string, CharacterSheet> Sheets(IEnumerable<AddEntry> party) => party.ToDictionary(p => p.EntityId!, p => p.Sheet!);

    private static SimulationReport Run(TrackerSimulationResult result, string edition) =>
        Simulator.Run(new SimulationSpec
        {
            Party = result.Party,
            Enemies = result.Enemies,
            Resume = result.Resume,
            Lair = result.Lair,
            Edition = edition,
            Iterations = 40,
        }, 7);

    /// <summary>Fixture A from A1 to A19: the start of round 2, Vars to act (FIX §2.6).</summary>
    private static EncounterState FixtureAAtRound2(IReadOnlyList<AddEntry> party)
    {
        var s = Encounter(E2014, "The crypt (fixture)", player: true);
        s = Add(s, [.. party]).Next;
        s = Add(s, Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), Monster(Block(E2014, "mummy"), 2, HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp
        {
            Rolls =
            [
                new("belmakor", Face: 17), new("mummy-lord", Face: 18), new("mummy", Face: 10), new("vars", Total: 25),
                new("serif", Total: 16), new("ignis", Total: 14), new("torch", Total: 12), new("aiden-ironstar", Total: 7),
            ],
        }).Next;
        s = s.Next();
        s = Step(s, new ConditionOp(["belmakor"]) { Add = ["Bladesong"], Effect = new EffectInput(Ac: 5), Duration = "1 minute", Resource = "Bladesong" }).Next;
        s = Step(s, new ConcentrationOp(["belmakor"]) { Spell = "Circle of Power", SlotLevel = 5, Duration = "10 minutes" }).Next;
        s = s.Next();
        s = Step(s, new DamageOp(["belmakor"]) { Parts = [new(14, null, "bludgeoning"), new(21, null, "necrotic")], Source = "mummy-lord" }).Next;
        s = Step(s, new ConcentrationOp(["belmakor"]) { Total = 20 }).Next;
        s = s.Next().Next();
        s = Step(s, new LegendaryOp("mummy-lord") { Amount = 1 }).Next;
        s = Step(s, new DamageOp(["torch"]) { Parts = [new(14, null, "bludgeoning"), new(21, null, "necrotic")], Source = "mummy-lord" }).Next;
        s = s.Next();
        s = Step(s, new DamageOp(["mummy-lord", "mummy", "mummy-2"]) { Parts = [new(28, null, "fire")] }).Next;
        s = s.Next();
        s = Step(s, new ConditionOp(["torch"]) { Add = ["frightened"], Source = "mummy", Duration = "until_end_of_source_turn" }).Next;
        s = Step(s, new DamageOp(["torch"]) { Parts = [new(10, null, "bludgeoning"), new(10, null, "necrotic")], Source = "mummy" }).Next;
        s = s.Next();
        s = Step(s, new DamageOp(["torch"]) { Parts = [new(10, null, "bludgeoning"), new(10, null, "necrotic")], Source = "mummy-2" }).Next;
        s = s.Next().Next();
        Assert.Equal((2, "Vars Nocturne"), (s.Round, s.TurnHolder!.Name));
        return s;
    }

    /// <summary>
    /// P and Q (sheet-seeded fighters, the party) and M and N (2024 goblin warriors, typed names), acting P, Q, M, N
    /// (20, 15, 10, 5): every one has a simulation route.
    /// </summary>
    private static (EncounterState State, Dictionary<string, CharacterSheet> Sheets) Routed(params string[] enemies)
    {
        var sheets = new Dictionary<string, CharacterSheet>
        {
            ["e-p"] = Sheet("e-p", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44, "ac": 18 }""", E2024),
            ["e-q"] = Sheet("e-q", """{ "classes": [{ "class": "cleric", "level": 5 }], "max_hp": 38, "ac": 16 }""", E2024),
        };
        var s = Add(Encounter(E2024), Pc("e-p", "P", "character:p", sheets["e-p"]), Pc("e-q", "Q", "character:q", sheets["e-q"])).Next;
        s = Add(s, [.. enemies.Select(name => Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg, name: name))]).Next;
        var totals = new[] { 20.0, 15, 10, 5, 4, 3 };
        s = Step(s, new InitiativeOp { Rolls = s.Combatants.Select((c, i) => new InitiativeRollInput(c.Name, Total: totals[i])).ToList() }).Next;
        return (s, sheets);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The encounter as a fresh fight

    [Fact]
    public void Fresh_FixtureA_PartyFromSheetsInOrderKeyOrder_SerifLeftOut_MummiesGrouped()
    {
        var s = Add(Add(Encounter(E2014, "The crypt"), [.. PartyAWithProfile()]).Next,
            Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), Monster(Block(E2014, "mummy"), 2, HpChoice.Avg)).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { Sheets = Sheets(PartyAWithProfile()) });
        Assert.Equal(["Aiden Ironstar", "Belmakor Silverwind", "Ignis", "Lieutenant James Torch", "Vars Nocturne"], result.Party.Select(p => p.Spec.Name));
        Assert.Equal("paladin", result.Party[0].Spec.Archetype);
        Assert.Equal("Belmakor L12 Bladesinger (fixture)", result.Party[1].Spec.Build!.Name);
        Assert.Equal(74, result.Party[3].Spec.Hp);
        Assert.All(result.Party, p => Assert.Null(p.Start));
        Assert.Equal([("2014/monster/mummy-lord", (int?)null), ("2014/monster/mummy", 2)], result.Enemies.Select(e => (e.Spec.Monster!, e.Spec.Count)));
        Assert.All(result.Enemies, e => Assert.Null(e.Spec.Hp));
        Assert.Contains(result.Notes, n => n.StartsWith("Serif is left out: Serif (artificer 12) has no sim_profile", StringComparison.Ordinal));
        Assert.Equal(5, result.Assumptions.Count);
        Assert.Null(result.Resume);
        Assert.False(Run(result, E2014).Resumed);
    }

    [Fact]
    public void Fresh_AMonsterWithOtherHpOrAc_IsItsOwnEntry_CustomNeutralAndLeftAreNoted()
    {
        var kit = Sheet("e-kit", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44, "ac": 18 }""", E2014);
        var s = Add(Encounter(E2014), Pc("e-kit", "Kit", "character:kit", kit), new AddEntry { Name = "Hero", Hp = HpChoice.Of(50), Side = "ally" }).Next;
        s = Add(s, Monster(Block(E2014, "mummy"), 3, HpChoice.Avg), new AddEntry { Name = "Thug", Hp = HpChoice.Of(20) },
            new AddEntry { Name = "Bystander", Hp = HpChoice.Of(4), Side = "neutral" }).Next;
        s = Step(s, new SetOp([new SetEntry("Mummy 3") { Hp = 70 }])).Next;
        s = Step(s, new SetOp([new SetEntry("Mummy 2") { Ac = 14 }])).Next;
        s = Add(s, new AddEntry { Name = "Coward", Hp = HpChoice.Of(4), Side = "ally" }).Next;
        s = Step(s, new LeaveOp(["Coward"])).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { Sheets = new Dictionary<string, CharacterSheet> { ["e-kit"] = kit } });
        Assert.Equal(["Kit"], result.Party.Select(p => p.Spec.Name));
        Assert.Equal([((int?)null, (int?)null, (int?)null), (null, 14, null), (70, null, null)], result.Enemies.Select(e => (e.Spec.Hp, e.Spec.Ac, e.Spec.Count)));
        Assert.Contains(result.Notes, n => n.Contains("Thug is left out: a custom combatant has no stat block", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n == "Neutral combatants are left out: Bystander.");
        Assert.Contains(result.Notes, n => n == "Combatants who left are left out: Coward.");
        Assert.Contains(result.Notes, n => n.Contains("Hero is left out", StringComparison.Ordinal));
    }

    [Fact]
    public void Fresh_APlannedEncounterWithNoPartySide_UsesTheCampaignsParty()
    {
        var s = Add(Encounter(E2024) with { Status = "planned" }, Monster(Block(E2024, "aboleth"), hp: HpChoice.Avg)).Next;
        var party = PartyB().Select(p => new PartyMemberSheet(p.EntityId!, p.EntityName!, p.EntityId == "e-slayer" ? WithProfile(p.Sheet!, SlayerProfile, E2024) : p.Sheet))
            .Append(new PartyMemberSheet("e-new", "Newcomer", null)).ToList();
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { CampaignParty = party });
        Assert.Equal(["Björn Mountainfell", "The amethyst Dragon Slayer", "The fishman monk"], result.Party.Select(p => p.Spec.Name));
        Assert.Contains(result.Notes, n => n.StartsWith("The party is the campaign's current party", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith("Newcomer is left out: no sheet", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_ASideWithNobody_ComesBackEmpty_RequireBothSidesRefusesOnlyWhatStaysEmpty()
    {
        // §6.11: explicit entries are appended first; the run is refused only when a side ends up empty.
        var onlyParty = Add(Encounter(E2024, "Station"), [.. PartyB()]).Next;
        var result = TrackerSimulation.Build(onlyParty, new TrackerSimulationInputs { Sheets = Sheets(PartyB()) });
        Assert.Equal(["Björn Mountainfell", "The fishman monk"], result.Party.Select(p => p.Spec.Name));
        Assert.Empty(result.Enemies);
        var refused = Assert.Throws<DndInputException>(() => TrackerSimulation.RequireBothSides(result.Party, result.Enemies, result.Notes, fromState: false)).Message;
        Assert.Contains("The encounter has no enemies to simulate", refused, StringComparison.Ordinal);
        Assert.Contains("The amethyst Dragon Slayer is left out", refused, StringComparison.Ordinal);
        Assert.EndsWith("or give enemies in the call.", refused, StringComparison.Ordinal);

        // "What if they meet an aboleth": the call's enemies appended, the run goes ahead.
        List<SimulationCombatant> enemies = [new SimulationCombatant(new CombatantSpec { Monster = "2024/monster/aboleth" }, Block(E2024, "aboleth"))];
        TrackerSimulation.RequireBothSides(result.Party, enemies, result.Notes, fromState: false);
        Assert.False(Simulator.Run(new SimulationSpec { Party = result.Party, Enemies = enemies, Edition = E2024, Iterations = 20 }, 7).Resumed);

        var onlyEnemies = Add(Encounter(E2024), Monster(Block(E2024, "aboleth"), hp: HpChoice.Avg)).Next;
        var noParty = TrackerSimulation.Build(onlyEnemies, new TrackerSimulationInputs());
        Assert.Empty(noParty.Party);
        Assert.Contains("no party", Assert.Throws<DndInputException>(() => TrackerSimulation.RequireBothSides(noParty.Party, noParty.Enemies, noParty.Notes, fromState: false)).Message, StringComparison.Ordinal);

        // Under from_state nothing is appended: a side of placeholders only is refused without "in the call".
        List<SimulationCombatant> placeholders = [new SimulationCombatant(new CombatantSpec { Name = "Lich" }, null, new CombatantStart { Placeholder = true })];
        var resumed = Assert.Throws<DndInputException>(() => TrackerSimulation.RequireBothSides(result.Party, placeholders, [], fromState: true)).Message;
        Assert.Equal("The encounter has no enemies to simulate: add enemies from stat blocks (combat add with srd).", resumed);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // from_state

    [Fact]
    public void FromState_BeforeRound1_IsRefused_UnknownHpEnemy_IsRefusedWithTheFix()
    {
        var planned = Add(Encounter(E2024), [.. PartyB()]).Next;
        Assert.Throws<DndInputException>(() => TrackerSimulation.Build(planned, new TrackerSimulationInputs { FromState = true, Sheets = Sheets(PartyB()) }));
        var s = Add(Encounter(E2024, player: true), [.. PartyB()]).Next;
        s = Add(s, Monster(Block(E2024, "ogre"))).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("ogre", Total: 3), new("bjorn-mountainfell", Total: 9), new("dragon-slayer", Total: 8), new("fishman-monk", Total: 7)] }).Next;
        var ex = Assert.Throws<DndInputException>(() => TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = Sheets(PartyB()) }));
        Assert.Contains("give hp with combat set", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromState_FixtureAAtRound2_LiveStatesOrderAndTurn_AndTheSimulatorRunsIt()
    {
        var s = FixtureAAtRound2(PartyAWithProfile());
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = Sheets(PartyAWithProfile()) });
        var names = result.Party.Concat(result.Enemies).Select(e => e.Spec.Name).ToList();
        Assert.Equal(["Aiden Ironstar", "Belmakor Silverwind", "Ignis", "Lieutenant James Torch", "Vars Nocturne", "Mummy Lord", "Mummy", "Mummy 2"], names);
        Assert.All(result.Party.Concat(result.Enemies), e => Assert.Null(e.Spec.Count));

        var belmakor = result.Party[1].Start!;
        Assert.Equal((82, 0, "Circle of Power"), (belmakor.Hp, belmakor.TempHp, belmakor.Concentration));
        Assert.Equal(["Bladesong"], belmakor.ActiveSetups);
        Assert.Null(belmakor.UsesLeft);
        Assert.True(belmakor.HasActed);
        Assert.Contains("Belmakor Silverwind: Arcane Recovery is not simulated.", result.Notes);

        var torch = result.Party[3].Start!;
        Assert.Equal((0, 0, 0), (torch.Hp, torch.DeathSuccesses, torch.DeathFailures));
        var frightened = torch.Conditions.Single(c => c.Condition == "frightened");
        Assert.Equal((SD.UntilEndOfSourceTurn, 6, false), (frightened.Duration, frightened.SourceEntry!.Value, frightened.ImposedDuringResumedTurn));
        Assert.Equal(SD.UntilStands, torch.Conditions.Single(c => c.Condition == "prone").Duration);
        Assert.DoesNotContain(torch.Conditions, c => c.Condition == "unconscious");

        var lord = result.Enemies[0].Start!;
        Assert.Equal((41, 2), (lord.Hp, lord.LegendaryActionsLeft));
        Assert.Equal((2, 2), (result.Enemies[1].Start!.Hp!.Value, result.Enemies[2].Start!.Hp!.Value));

        // Tracker order Vars, Belmakor, Mummy Lord, (Serif), Ignis, Torch, Mummy, Mummy 2, Aiden; Vars at the start of round 2.
        Assert.Equal([4, 1, 5, 2, 3, 6, 7, 0], result.Resume!.Order);
        Assert.Equal((0, 2), (result.Resume.StartAt, result.Resume.Round));
        Assert.Contains(result.Notes, n => n.StartsWith("Serif is left out", StringComparison.Ordinal));
        var report = Run(result, E2014);
        Assert.True(report.Resumed);
    }

    [Fact]
    public void FromState_FixtureBAtRound3_RageAsASetup_GrapplesUntilEscape_CurseNotSimulated()
    {
        var party = PartyB().Select(p => p.EntityId == "e-slayer" ? p with { Sheet = WithProfile(p.Sheet!, SlayerProfile, E2024) } : p).ToList();
        var s = Add(Encounter(E2024, "Station", lair: true), [.. party]).Next;
        s = Add(s, Monster(Block(E2024, "aboleth"), hp: HpChoice.Avg)).Next;
        s = Step(s, new ConditionOp(["bjorn-mountainfell"]) { Add = ["exhaustion"], Level = 1 }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("fishman-monk", Face: 16), new("dragon-slayer", Face: 9), new("bjorn-mountainfell", Face: 8), new("aboleth", Total: 13)] }).Next;
        s = s.Next(); // the Aboleth
        s = Step(s, new ConditionOp(["fishman-monk"]) { Add = ["grappled"], Source = "aboleth", Dc = 14 }).Next;
        s = Step(s, new ConditionOp(["bjorn-mountainfell"]) { Add = ["grappled"], Source = "aboleth", Dc = 14 }).Next;
        s = Step(s, new ConditionOp(["fishman-monk"]) { Add = ["cursed (Mucus Cloud)"], Source = "aboleth", Duration = "until removed" }).Next;
        s = s.Next().Next(); // Björn
        s = Step(s, new ConditionOp(["bjorn-mountainfell"]) { Add = ["Rage"], Effect = new EffectInput(Resist: ["all"], Except: ["psychic"]), Resource = "Rage" }).Next;
        s = Step(s, new DamageOp(["aboleth"]) { Amount = 137 }).Next;
        s = Step(s, new LegendaryOp("aboleth") { Resistance = true }).Next;
        s = Step(s, new LegendaryOp("aboleth") { Resistance = true }).Next;
        s = Step(s, new LegendaryOp("aboleth") { Amount = 2 }).Next;
        s = s.Next(); // round 2, the monk
        Assert.Equal((2, "The fishman monk"), (s.Round, s.TurnHolder!.Name));

        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = Sheets(party) });
        Assert.True(result.Lair);
        var bjorn = result.Party[0].Start!;
        Assert.Equal(1, bjorn.Exhaustion);
        Assert.Equal(["Rage"], bjorn.ActiveSetups);
        var grapple = bjorn.Conditions.Single();
        Assert.Equal((SD.UntilEscape, 3, 14), (grapple.Duration, grapple.SourceEntry!.Value, grapple.EscapeDc!.Value));
        Assert.Contains(result.Notes, n => n.StartsWith("The fishman monk: cursed (Mucus Cloud)", StringComparison.Ordinal) && n.EndsWith("not simulated.", StringComparison.Ordinal));
        var aboleth = result.Enemies.Single().Start!;
        Assert.Equal((13, 2, 2), (aboleth.Hp!.Value, aboleth.LegendaryResistanceLeft!.Value, aboleth.LegendaryActionsLeft!.Value));
        Assert.Equal(new[] { 2, 3, 1, 0 }, result.Resume!.Order);
        Assert.Equal((0, 2), (result.Resume.StartAt, result.Resume.Round));
        Assert.True(Run(result, E2024).Resumed);
    }

    [Fact]
    public void FromState_DurationMapping_RoundsLeftEndOfRoundConcentrationSaveAndTheResumedTurn()
    {
        var (s, sheets) = Routed("M", "N");
        s = Step(s, new ConcentrationOp(["P"]) { Spell = "Hold Person" }).Next;
        s = Step(s, new ConditionOp(["M"]) { Add = ["paralyzed"], Duration = "concentration" }).Next;
        s = Step(s, new ConditionOp(["N"]) { Add = ["poisoned"], Duration = "3 rounds" }).Next;
        s = Step(s, new ConditionOp(["N"]) { Add = ["blinded"], Duration = "end of round 2" }).Next;
        s = Step(s, new ConditionOp(["Q"]) { Add = ["frightened"], Duration = "1 round", Dc = 12, Ability = "wis" }).Next;
        s = Step(s, new ConditionOp(["Q"]) { Add = ["restrained"], Duration = "until removed" }).Next;
        s = Step(s, new ConditionOp(["N"]) { Add = ["stunned"], Duration = "until the start of your next turn" }).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });

        // Entries: P, Q (party), M, N (enemies); P holds the turn in round 1 and has not acted.
        var m = result.Enemies[0].Start!.Conditions.Single();
        Assert.Equal(("paralyzed", SD.Fight, 0, true), (m.Condition, m.Duration, m.SourceEntry!.Value, m.HeldBySourceConcentration));
        var n = result.Enemies[1].Start!.Conditions;
        Assert.Equal((SD.Rounds, 0, 3), (n[0].Duration, n[0].SourceEntry!.Value, n[0].RoundsLeft!.Value));
        Assert.Equal((SD.Rounds, 3, 2), (n[1].Duration, n[1].SourceEntry!.Value, n[1].RoundsLeft!.Value));
        Assert.Equal((SD.UntilStartOfSourceTurn, 0, true), (n[2].Duration, n[2].SourceEntry!.Value, n[2].ImposedDuringResumedTurn));
        Assert.All(n, c => Assert.True(c.ImposedDuringResumedTurn));
        var q = result.Party[1].Start!.Conditions;
        Assert.Equal((SD.SaveEnds, 1, "wis", 12), (q[0].Duration, q[0].RoundsLeft!.Value, q[0].SaveAbility, q[0].SaveDc!.Value));
        Assert.Equal(SD.Fight, q[1].Duration);
        Assert.Equal("Hold Person", result.Party[0].Start!.Concentration);
        Assert.True(Run(result, E2024).Resumed);
    }

    [Theory]
    [InlineData(12.0, 2, 1)] // between Q and M: M's turn, the same round
    [InlineData(1.0, 0, 2)] // last in the order: P's turn, the next round
    public void FromState_ANeutralHoldsTheTurn_ResumesAtTheNextSimulatedCreature(double total, int entry, int round)
    {
        var (s, sheets) = Routed("M");
        s = Add(s, new AddEntry { Name = "Bystander", Hp = HpChoice.Of(4), Side = "neutral" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Bystander", Total: total)] }).Next;
        (s, _) = NextUntil(s, "Bystander");
        Assert.Equal(1, s.Round);
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        Assert.Equal((entry, round), (result.Resume!.Order[result.Resume.StartAt], result.Resume.Round));
        Assert.True(Run(result, E2024).Resumed);
    }

    [Fact]
    public void FromState_ImposedOnTheSameCreaturesTurnInAnEarlierRound_IsNotTheResumedTurn()
    {
        var (s, sheets) = Routed("M");
        s = Step(s, new ConditionOp(["M"]) { Add = ["frightened"], Duration = "until the end of your next turn" }).Next; // P's turn, round 1
        (s, _) = NextUntil(s, "P"); // P's turn again, round 2
        var frightened = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets }).Enemies.Single().Start!.Conditions.Single();
        Assert.Equal((SD.UntilEndOfSourceTurn, false), (frightened.Duration, frightened.ImposedDuringResumedTurn));
    }

    [Fact]
    public void FromState_TheAnchorHasActed_RoundsLeftOneLess_ZeroIsLegal()
    {
        var (s, sheets) = Routed("M");
        s = Step(s, new ConditionOp(["M"]) { Add = ["poisoned"], Duration = "1 round" }).Next; // expires at P's turn start, round 2
        s = s.Next().Next(); // M's turn: P has acted
        var poisoned = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets }).Enemies.Single().Start!.Conditions.Single();
        Assert.Equal((SD.Rounds, 0, false), (poisoned.Duration, poisoned.RoundsLeft!.Value, poisoned.ImposedDuringResumedTurn));
    }

    [Fact]
    public void FromState_ASheetWithoutHitPointsGivenThemInTheFight_TakesTheLiveMaximum()
    {
        var vars = Sheet("e-vars", """{ "classes": [{ "class": "ranger", "level": 6 }, { "class": "rogue", "level": 6 }] }""", E2014);
        var s = Add(Encounter(E2014), Pc("e-vars", "Vars Nocturne", "character:vars", vars), Monster(Block(E2014, "mummy"), hp: HpChoice.Avg)).Next;
        s = Step(s, new SetOp([new SetEntry("vars") { Hp = 150 }])).Next;
        s = Step(s, new DamageOp(["vars"]) { Amount = 20 }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("vars", Total: 20), new("mummy", Total: 5)] }).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = new Dictionary<string, CharacterSheet> { ["e-vars"] = vars } });
        var entry = result.Party.Single();
        Assert.Equal((150, 130), (entry.Spec.Hp!.Value, entry.Start!.Hp!.Value));
        Assert.True(Run(result, E2014).Resumed);
    }

    [Fact]
    public void FromState_AnSrdAllyThatMakesDeathSaves_DyingAtZero_KeepsItsDeathSaves()
    {
        var (s, sheets) = Routed("M");
        s = Add(s, Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg, name: "Guide") with { Side = "ally", DeathSaves = true }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Guide", Total: 1)] }).Next;
        s = Step(s, new DamageOp(["Guide"]) { Amount = 12 }).Next;
        Assert.True(s.Named("Guide").Dying);
        var guide = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets }).Party.Single(p => p.Spec.Name == "Guide");
        Assert.Equal((true, 0), (guide.Spec.DeathSaves!.Value, guide.Start!.Hp!.Value));
    }

    [Fact]
    public void FromState_ADeadSourceOfARunningCondition_IsAPlaceholder_OtherDeadAreLeftOut()
    {
        var (s, sheets) = Routed("Lich", "Goon");
        s = s.Next().Next(); // the Lich's turn
        s = Step(s, new ConditionOp(["P"]) { Add = ["paralyzed"], Duration = "until the end of your next turn" }).Next;
        s = Step(s, new DamageOp(["Lich", "Goon"]) { Amount = 99 }).Next;
        s = Add(s, Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg, name: "Reinforcement")).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Reinforcement", Total: 1)] }).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        Assert.Equal(["Lich", "Reinforcement"], result.Enemies.Select(e => e.Spec.Name));
        Assert.True(result.Enemies[0].Start!.Placeholder);
        var paralyzed = result.Party[0].Start!.Conditions.Single();
        Assert.Equal((SD.UntilEndOfSourceTurn, 2, true), (paralyzed.Duration, paralyzed.SourceEntry!.Value, paralyzed.ImposedDuringResumedTurn));
        Assert.Equal(2, result.Resume!.Order[result.Resume.StartAt]);
        Assert.True(Run(result, E2024).Resumed);
    }


    // ------------------------------------------------------------------------------------------------------------------
    // The resumed turn's flag when the turn-holder is not simulated; down creatures without death saves

    [Theory]
    [InlineData("until the start of its next turn", SD.UntilStartOfTargetTurn)]
    [InlineData("until the end of its next turn", SD.UntilEndOfTargetTurn)]
    public void FromState_ANeutralHoldsTheTurn_WhatItsTurnImposedIsNotFlagged(string duration, string mapped)
    {
        // The resume starts at M, whose turn has not begun: no replayed turn, so nothing to protect (the checker's P05).
        var (s, sheets) = Routed("M");
        s = Add(s, new AddEntry { Name = "Bystander", Hp = HpChoice.Of(4), Side = "neutral" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Bystander", Total: 12)] }).Next;
        (s, _) = NextUntil(s, "Bystander");
        s = Step(s, new ConditionOp(["M"]) { Add = ["stunned"], Duration = duration }).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        Assert.Equal(2, result.Resume!.Order[result.Resume.StartAt]);
        var stunned = result.Enemies.Single().Start!.Conditions.Single();
        Assert.Equal((mapped, false), (stunned.Duration, stunned.ImposedDuringResumedTurn));
        Assert.True(Run(result, E2024).Resumed);
    }

    [Fact]
    public void FromState_AnExcludedMembersTurn_WhatItImposedIsNotFlagged()
    {
        // Serif (no sim route) holds the turn; the resume starts at Ignis (the checker's P15).
        var party = PartyAWithProfile();
        var s = Add(Encounter(E2014, "Crypt", player: true), [.. party]).Next;
        s = Add(s, Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp
        {
            Rolls = [new("belmakor", Total: 22), new("mummy-lord", Total: 18), new("vars", Total: 25), new("serif", Total: 16), new("ignis", Total: 14), new("torch", Total: 12), new("aiden-ironstar", Total: 7)],
        }).Next;
        (s, _) = NextUntil(s, "Serif");
        s = Step(s, new ConditionOp(["ignis"]) { Add = ["blinded"], Duration = "until the end of its next turn" }).Next;
        Assert.False(s.Named("Ignis").Conditions.Single().SkipEnd);
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = Sheets(party) });
        var ignis = result.Party.Select((p, i) => (p, i)).Single(x => x.p.Spec.Name == "Ignis");
        Assert.Equal(ignis.i, result.Resume!.Order[result.Resume.StartAt]);
        var blinded = ignis.p.Start!.Conditions.Single();
        Assert.Equal((SD.UntilEndOfTargetTurn, false), (blinded.Duration, blinded.ImposedDuringResumedTurn));
        Assert.True(Run(result, E2014).Resumed);
    }

    [Theory]
    [InlineData(E2014, "goblin", true)] // knocked out: 0 HP, unconscious, stable
    [InlineData(E2024, "goblin-warrior", true)] // knocked out: 1 HP, unconscious
    [InlineData(E2014, "zombie", false)] // held at 0 HP by Undead Fortitude
    public void FromState_AnAllyDownWithoutDeathSaves_IsLeftOut_TheSimulatorRunsIt(string edition, string slug, bool knockOut)
    {
        var (s, sheets) = Fighter(edition);
        s = Add(s, Monster(Block(edition, slug), hp: HpChoice.Avg, name: "Ally") with { Side = "ally" }, Monster(Block(edition, edition == E2014 ? "goblin" : "goblin-warrior"), hp: HpChoice.Avg, name: "Foe")).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("P", Total: 20), new("Ally", Total: 15), new("Foe", Total: 10)] }).Next;
        s = Step(s, new DamageOp(["Ally"]) { Amount = 30, DamageType = "slashing", KnockOut = knockOut }).Next;
        Assert.False(s.Named("Ally").Dead);
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        Assert.Equal(["P"], result.Party.Select(p => p.Spec.Name));
        Assert.Contains(result.Notes, n => n.StartsWith("Ally is left out: it is down", StringComparison.Ordinal));
        Assert.True(Run(result, edition).Resumed);
    }

    [Fact]
    public void FromState_ADownAllyThatImposedSomething_IsAPlaceholder()
    {
        var (s, sheets) = Fighter(E2014);
        s = Add(s, Monster(Block(E2014, "goblin"), hp: HpChoice.Avg, name: "Ally") with { Side = "ally" }, Monster(Block(E2014, "goblin"), hp: HpChoice.Avg, name: "Foe")).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("P", Total: 20), new("Ally", Total: 15), new("Foe", Total: 10)] }).Next;
        s = Step(s, new ConditionOp(["Foe"]) { Add = ["frightened"], Source = "Ally", Duration = "until the end of your next turn" }).Next;
        s = Step(s, new DamageOp(["Ally"]) { Amount = 30, KnockOut = true }).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        Assert.Equal(["P", "Ally"], result.Party.Select(p => p.Spec.Name));
        Assert.True(result.Party[1].Start!.Placeholder);
        Assert.Equal(1, result.Enemies.Single().Start!.Conditions.Single().SourceEntry);
        Assert.True(Run(result, E2014).Resumed);
    }

    [Fact]
    public void FromState_A2024KnockedOutPartyMember_StartsStableAtZero_NotUnconsciousForever()
    {
        var (s, sheets) = Routed("M");
        s = Step(s, new DamageOp(["P"]) { Amount = 60, KnockOut = true }).Next;
        Assert.Equal((1, true), (s.Named("P").Hp!.Value, s.Named("P").KnockedOut));
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        var p = result.Party[0].Start!;
        Assert.Equal((0, true), (p.Hp!.Value, p.Stable));
        Assert.DoesNotContain(p.Conditions, c => c.Condition == "unconscious");
        Assert.Equal(SD.UntilStands, p.Conditions.Single(c => c.Condition == "prone").Duration);
        Assert.Contains(result.Notes, n => n.StartsWith("P is knocked out", StringComparison.Ordinal));
        Assert.True(Run(result, E2024).Resumed);
    }

    /// <summary>
    /// F2R04: a sleeper (a stored Unconscious with no "knocked out" note) on a sheet at 32/32 is no knock-out: it is
    /// simulated at its 32 hit points, unconscious for the fight, as F1 did; F2 simulated it stable at 0 HP ("Dropped to 0
    /// 100%"). A knock-out is still simulated stable at 0, with its note.
    /// </summary>
    [Theory]
    [InlineData(null, 32, false)]
    [InlineData(CombatEnd.KnockedOutNote, 0, true)]
    public void FromState_AStoredUnconsciousAboveZero_ASleeperKeepsItsHitPoints_AKnockOutStartsStableAtZero(string? note, int hp, bool stable)
    {
        var sheet = Sheet("e-aria", Wizard5, E2024) with { Conditions = [new SheetCondition("unconscious", null, "until_removed", null, note)] };
        var s = Add(Encounter(E2024, "Ogres"), Pc("e-aria", "Aria", "character:aria", sheet)).Next;
        s = Add(s, Monster(Block(E2024, "ogre"), 1, HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("aria", Total: 20), new("ogre", Total: 5)] }).Next;

        var result = Resume(s, new Dictionary<string, CharacterSheet> { ["e-aria"] = sheet });

        var p = result.Party[0].Start!;
        Assert.Equal((hp, stable), (p.Hp!.Value, p.Stable));
        Assert.Equal(!stable, p.Conditions.Any(c => c.Condition == "unconscious"));
        Assert.Equal(stable, result.Notes.Any(n => n.StartsWith("Aria is knocked out (unconscious at 32 HP)", StringComparison.Ordinal)));
        var aria = Run(result, E2024).Combatants.Single(c => c.Name == "Aria");
        Assert.Equal(hp, aria.MaxHp);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Spell slots under from_state (C03/U01): a slot-funded modifier starts with the slots the fight has left

    /// <summary>Aria (a sheet-seeded caster) against three 2024 ogres, Aria to act in round 1.</summary>
    private static (EncounterState State, Dictionary<string, CharacterSheet> Sheets) Caster(string sheetJson, BuildSpec? profile = null, string edition = E2024)
    {
        var sheet = Sheet("e-aria", sheetJson, edition);
        if (profile is not null)
        {
            sheet = SheetUpdate.Apply(sheet, null, profile, edition).Sheet;
        }

        var s = Add(Encounter(edition, "Ogres"), Pc("e-aria", "Aria", "character:aria", sheet)).Next;
        s = Add(s, Monster(Block(edition, "ogre"), 3, HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("aria", Total: 20), new("ogre", Total: 5)] }).Next;
        return (s, new Dictionary<string, CharacterSheet> { ["e-aria"] = sheet });
    }

    private static TrackerSimulationResult Resume(EncounterState s, Dictionary<string, CharacterSheet> sheets) =>
        TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });

    private const string Wizard5 = """{ "classes": [{ "class": "wizard", "level": 5 }], "max_hp": 32, "ac": 12 }""";

    [Fact]
    public void FromState_AWizardWithNoThirdLevelSlotsLeft_CastsNoFireball_TheReportDiffers()
    {
        var (s, sheets) = Caster(Wizard5);
        var full = Resume(s, sheets);
        Assert.Equal(2, full.Party[0].Start!.UsesLeft!["Fireball"]);

        var spent = Resume(Step(s, new UseOp(["aria"]) { SlotLevel = 3, Amount = 2 }).Next, sheets);

        Assert.Equal(0, spent.Party[0].Start!.UsesLeft!["Fireball"]);
        Assert.Contains("Aria: spell slots are not simulated: 1st-level 4/4 left, 2nd-level 3/3 left.", spent.Notes);
        // F2R05: a slot-funded use with no slot left is said, as a spell cast once a fight is.
        Assert.Equal(["Aria: Fireball has no slots left (3rd level or higher)."], spent.Notes.Where(n => n.Contains("Fireball", StringComparison.Ordinal)));
        Assert.DoesNotContain(full.Notes, n => n.Contains("Fireball", StringComparison.Ordinal));
        Assert.DoesNotContain(spent.Notes, n => n.Contains("slot:", StringComparison.Ordinal));
        var before = Run(full, E2024).Combatants.Single(c => c.Name == "Aria");
        var after = Run(spent, E2024).Combatants.Single(c => c.Name == "Aria");
        Assert.True(before.Resources.Single(r => r.Name == "Fireball").MeanUsed > 0);
        Assert.Equal(0, after.Resources.Single(r => r.Name == "Fireball").MeanUsed);
        Assert.NotEqual(before.DamageDealt, after.DamageDealt);
    }

    [Fact]
    public void FromState_HigherSlotsFundTheAreaSpell_TheHealersFirstLevelSlotsFundOnlyItsHeal()
    {
        // Wizard 7 (4, 3, 3, 1): Fireball is every slot of 3rd level or higher; a bard 5 (4, 3, 2): Shatter 2nd+, Healing Word 1st.
        var (s, sheets) = Caster("""{ "classes": [{ "class": "wizard", "level": 7 }], "max_hp": 40, "ac": 12 }""");
        s = Step(s, new UseOp(["aria"]) { SlotLevel = 4 }).Next;
        Assert.Equal(3, Resume(s, sheets).Party[0].Start!.UsesLeft!["Fireball"]);

        var (b, bard) = Caster("""{ "classes": [{ "class": "bard", "level": 5 }], "max_hp": 38, "ac": 13 }""");
        b = Step(Step(b, new UseOp(["aria"]) { SlotLevel = 1, Amount = 3 }).Next, new UseOp(["aria"]) { SlotLevel = 3 }).Next;
        var start = Resume(b, bard);
        Assert.Equal((1, 4), (start.Party[0].Start!.UsesLeft!["Healing Word"], start.Party[0].Start!.UsesLeft!["Shatter"]));
        Assert.DoesNotContain(start.Notes, n => n.StartsWith("Aria: spell slots", StringComparison.Ordinal));
    }

    [Fact]
    public void FromState_APactCaster_ThePactSlotsOfTheSpellsLevelOrHigherFundItToo_LowerOnesAreNoted()
    {
        var (s, sheets) = Caster("""{ "classes": [{ "class": "sorcerer", "level": 5 }, { "class": "warlock", "level": 5 }], "max_hp": 50, "ac": 13, "slots": [4, 3, 2], "pact": { "level": 3, "max": 2 } }""");
        Assert.Equal(4, Resume(s, sheets).Party[0].Start!.UsesLeft!["Lightning Bolt"]);

        s = Step(Step(s, new UseOp(["aria"]) { Pact = true }).Next, new UseOp(["aria"]) { SlotLevel = 3 }).Next;
        var resumed = Resume(s, sheets);
        Assert.Equal(2, resumed.Party[0].Start!.UsesLeft!["Lightning Bolt"]);
        Assert.Contains("Aria: spell slots are not simulated: 1st-level 4/4 left, 2nd-level 3/3 left.", resumed.Notes);

        // A warlock's Pact Magic casts its Hex (CR03): simulated, so never "not simulated"; one slot left still casts it.
        var (w, warlock) = Caster("""{ "classes": [{ "class": "warlock", "level": 5 }], "max_hp": 38, "ac": 13, "pact": { "level": 3, "max": 2 } }""");
        w = Step(w, new UseOp(["aria"]) { Pact = true }).Next;
        var hex = Resume(w, warlock);
        Assert.DoesNotContain(hex.Notes, n => n.StartsWith("Aria:", StringComparison.Ordinal));
        Assert.Empty(hex.Party[0].Start!.Unavailable);
    }

    [Fact]
    public void FromState_ASimProfileModifierNamedAsASlotFundedSpell_TakesTheSlotsLeft()
    {
        var profile = DslJson.Deserialize<BuildSpec>("""
            {"name":"Aria (fixture)","level":5,"abilities":{"int":18,"dex":14},
             "attacks":[{"name":"Fire Bolt","to_hit":{"ability":"int"},"damage":"1d10","damage_type":"fire","properties":["ranged","spell"],"cantrip":"dice"}],
             "modifiers":[{"kind":"save_effect","name":"Fireball","ability":"dex","dc_ability":"int","dice":"8d6","type":"fire","shape":"sphere","size":20,
                           "resource":{"uses":2,"per":"long_rest"}}]}
            """, "sim_profile");
        var (s, sheets) = Caster(Wizard5, profile);
        s = Step(s, new UseOp(["aria"]) { SlotLevel = 3 }).Next;

        var resumed = Resume(s, sheets);

        Assert.Equal(1, resumed.Party[0].Start!.UsesLeft!["Fireball"]);
        Assert.Contains("Aria: spell slots are not simulated: 1st-level 4/4 left, 2nd-level 3/3 left.", resumed.Notes);
        Assert.True(Run(resumed, E2024).Resumed);
    }

    [Fact]
    public void FromState_SlotsNoModifierSpends_AreNotedInOrdinals_AMonstersPoolsToo()
    {
        // A fighter's sheet given slots, and the 2014 mummy lord's pools that its simulation does not cast from.
        var sheets = new Dictionary<string, CharacterSheet>
        {
            ["e-kit"] = Sheet("e-kit", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44, "ac": 18, "slots": [2] }""", E2014),
        };
        var s = Add(Encounter(E2014), Pc("e-kit", "Kit", "character:kit", sheets["e-kit"]), Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("kit", Total: 20), new("mummy-lord", Total: 5)] }).Next;
        s = Step(s, new UseOp(["mummy-lord"]) { SlotLevel = 3 }).Next;

        var resumed = Resume(s, sheets);

        Assert.Contains("Kit: spell slots are not simulated: 1st-level 2/2 left.", resumed.Notes);
        Assert.Contains("Mummy Lord: spell slots are not simulated: 3rd-level 2/3 left, 4th-level 3/3 left.", resumed.Notes);
        Assert.DoesNotContain(resumed.Notes, n => n.Contains("slot:", StringComparison.Ordinal));
        Assert.True(Run(resumed, E2014).Resumed);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Spells cast once a fight from a slot (CR03) and slots two spells share (CR04)

    [Theory]
    [InlineData("""{ "classes": [{ "class": "cleric", "level": 5 }], "max_hp": 38, "ac": 18 }""", false, "Spirit Guardians", "3rd")]
    [InlineData("""{ "classes": [{ "class": "druid", "level": 5 }], "max_hp": 38, "ac": 15 }""", false, "Call Lightning", "3rd")]
    [InlineData("""{ "classes": [{ "class": "warlock", "level": 5 }], "max_hp": 38, "ac": 13, "pact": { "level": 3, "max": 2 } }""", true, "Hex", "1st")]
    public void FromState_ASpellCastOnceAFight_NoSlotOfItsLevelLeft_IsNotCast_TheReportDiffers_TheNoteSaysWhy(string sheet, bool pact, string spell, string level)
    {
        // CR03: Spirit Guardians, Call Lightning and Hex are one slot a fight with no uses to count down, so a caster with
        // every such slot spent kept casting them: the resumed report was byte-identical to the full-slots one.
        var (s, sheets) = Caster(sheet);
        var full = Resume(s, sheets);
        var spent = Resume(Step(s, pact ? new UseOp(["aria"]) { Pact = true, Amount = 2 } : new UseOp(["aria"]) { SlotLevel = 3, Amount = 2 }).Next, sheets);

        Assert.Empty(full.Party[0].Start!.Unavailable);
        Assert.DoesNotContain(full.Notes, n => n.Contains(spell, StringComparison.Ordinal));
        Assert.Equal([spell], spent.Party[0].Start!.Unavailable);
        Assert.Contains($"Aria: no spell slot is left for {spell} ({level} level or higher), so the resumed fight does not cast it.", spent.Notes);
        Assert.NotEqual(Run(full, E2024).Combatants.Single(c => c.Name == "Aria").DamageDealt, Run(spent, E2024).Combatants.Single(c => c.Name == "Aria").DamageDealt);
    }

    [Fact]
    public void FromState_ConcentratingOnTheSpell_WithNoSlotLeft_KeepsItRunning_ButCastsItNoMore()
    {
        // CR03: a spell already held is the fight's (the resume models it running); with no slot left it is not cast again
        // once its concentration ends.
        var (s, sheets) = Caster("""{ "classes": [{ "class": "druid", "level": 5 }], "max_hp": 38, "ac": 15 }""");
        s = Step(s, new ConcentrationOp(["aria"]) { Spell = "Call Lightning", SlotLevel = 3, Duration = "10 minutes" }).Next;
        s = Step(s, new UseOp(["aria"]) { SlotLevel = 3 }).Next;

        var held = Resume(s, sheets);
        var dropped = Resume(Step(s, new ConcentrationOp(["aria"]) { Drop = true }).Next, sheets);

        Assert.Equal("Call Lightning", held.Party[0].Start!.Concentration);
        Assert.Equal(["Call Lightning"], held.Party[0].Start!.Unavailable);
        Assert.Contains("Aria: no spell slot is left to cast Call Lightning again (3rd level or higher): it runs while its concentration holds.", held.Notes);
        Assert.Contains("Aria: no spell slot is left for Call Lightning (3rd level or higher), so the resumed fight does not cast it.", dropped.Notes);
        Assert.True(Run(held, E2024).Combatants.Single(c => c.Name == "Aria").DamageDealt.Mean > Run(dropped, E2024).Combatants.Single(c => c.Name == "Aria").DamageDealt.Mean);
    }

    [Fact]
    public void FromState_TwoSpellsCastOnceAFight_ShareTheSlotsLeft_TheMainSpellFirst_SpiritualWeaponToo()
    {
        // CR03: the 2014 cleric's Spirit Guardians (3rd level or higher) is cast before Spiritual Weapon (2nd or higher), an
        // attack; one 3rd-level slot left and no 2nd funds Spirit Guardians only.
        const string Cleric = """{ "classes": [{ "class": "cleric", "level": 5 }], "max_hp": 38, "ac": 18 }""";
        var (s, sheets) = Caster(Cleric, edition: E2014);
        s = Step(s, new UseOp(["aria"]) { SlotLevel = 2, Amount = 3 }).Next;
        Assert.Empty(Resume(s, sheets).Party[0].Start!.Unavailable);

        var oneLeft = Step(s, new UseOp(["aria"]) { SlotLevel = 3 }).Next;
        var resumed = Resume(oneLeft, sheets);

        Assert.Equal(["Spiritual Weapon"], resumed.Party[0].Start!.Unavailable);
        Assert.Contains("Aria: no spell slot is left for Spiritual Weapon (2nd level or higher), so the resumed fight does not cast it.", resumed.Notes);
        var none = Resume(Step(oneLeft, new UseOp(["aria"]) { SlotLevel = 3 }).Next, sheets);
        Assert.Equal(["Spirit Guardians", "Spiritual Weapon"], none.Party[0].Start!.Unavailable);
        Assert.Contains(
            "Aria: no spell slot is left for Spirit Guardians (3rd level or higher) or Spiritual Weapon (2nd level or higher), so the resumed fight does not cast them.",
            none.Notes);
        Assert.DoesNotContain(none.Notes, n => n.StartsWith("Aria: spell slots are not simulated", StringComparison.Ordinal));
        Assert.NotEqual(Run(resumed, E2014).Combatants.Single(c => c.Name == "Aria").DamageDealt, Run(none, E2014).Combatants.Single(c => c.Name == "Aria").DamageDealt);

        // A 2014 Spiritual Weapon (no concentration) the table keeps as an effect is running: it needs no slot, and the
        // effect is simulated by it, never "not simulated".
        var kept = Step(Step(oneLeft, new UseOp(["aria"]) { SlotLevel = 3 }).Next, new ConditionOp(["aria"]) { Add = ["Spiritual Weapon"], Duration = "1 minute" }).Next;
        var running = Resume(kept, sheets);
        Assert.Equal(["Spirit Guardians"], running.Party[0].Start!.Unavailable);
        Assert.DoesNotContain(running.Notes, n => n.Contains("not simulated", StringComparison.Ordinal) && n.Contains("Spiritual Weapon", StringComparison.Ordinal));
    }

    [Fact]
    public void FromState_ASheetThatRecordsNoSlots_CastsNoSlotSpell_TheNoteSaysSoAndTheFix()
    {
        // CR03: a multiclass sheet's slots are never derived (D18); with none recorded the 2014 ranger has none to cast
        // Hunter's Mark from, as a slot-funded use has none, and the note says it is the sheet, not the fight.
        var (s, sheets) = Caster("""{ "classes": [{ "class": "ranger", "level": 6 }, { "class": "rogue", "level": 6 }], "max_hp": 80, "ac": 15 }""", edition: E2014);

        var resumed = Resume(s, sheets);

        Assert.Equal(["Hunter's Mark"], resumed.Party[0].Start!.Unavailable);
        Assert.Contains(
            "Aria: the sheet records no spell slots, so the resumed fight does not cast Hunter's Mark (1st level or higher) (give the sheet its slots with " +
            "campaign_character update).",
            resumed.Notes);
        Assert.True(Run(resumed, E2014).Resumed);
    }

    [Fact]
    public void FromState_ASimProfileSpellCastOnceAFight_IsReadByItsName()
    {
        // CR03: a sim_profile's Hex (a rider with a setup and concentration, as the archetype's) needs a slot too.
        var profile = DslJson.Deserialize<BuildSpec>("""
            {"name":"Aria (fixture)","level":5,"abilities":{"cha":18,"dex":14},
             "attacks":[{"name":"Eldritch Blast","to_hit":{"ability":"cha"},"damage":"1d10","damage_type":"force","properties":["ranged","spell"],"cantrip":"beams"}],
             "modifiers":[{"kind":"extra_damage","name":"Hex","dice":"1d6","type":"necrotic","concentration":true,"setup":"bonus_action"}]}
            """, "sim_profile");
        var (s, sheets) = Caster("""{ "classes": [{ "class": "warlock", "level": 5 }], "max_hp": 38, "ac": 13, "pact": { "level": 3, "max": 2 } }""", profile);

        var spent = Resume(Step(s, new UseOp(["aria"]) { Pact = true, Amount = 2 }).Next, sheets);

        Assert.Equal(["Hex"], spent.Party[0].Start!.Unavailable);
        Assert.True(Run(spent, E2024).Resumed);
    }

    [Fact]
    public void FromState_TwoSimProfileSpellsOnOneSlotLevel_ShareTheSlotsLeftInTheBuildsOrder()
    {
        // CR04: each was funded from every 3rd-level slot left, so one slot resumed as a Fireball AND a Lightning Bolt.
        var profile = DslJson.Deserialize<BuildSpec>("""
            {"name":"Aria (fixture)","level":5,"abilities":{"int":18,"dex":14},
             "attacks":[{"name":"Fire Bolt","to_hit":{"ability":"int"},"damage":"1d10","damage_type":"fire","properties":["ranged","spell"],"cantrip":"dice"}],
             "modifiers":[
               {"kind":"save_effect","name":"Fireball","ability":"dex","dc_ability":"int","dice":"8d6","type":"fire","shape":"sphere","size":20,"resource":{"uses":1,"per":"long_rest"}},
               {"kind":"save_effect","name":"Lightning Bolt","ability":"dex","dc_ability":"int","dice":"8d6","type":"lightning","shape":"line","size":100,"resource":{"uses":1,"per":"long_rest"}}]}
            """, "sim_profile");
        var (s, sheets) = Caster(Wizard5, profile);
        var both = Resume(s, sheets);
        Assert.Equal(1, both.Party[0].Start!.UsesLeft!["Lightning Bolt"]); // Fireball's 1 use takes one of the two
        Assert.True(both.Party[0].Start!.UsesLeft!["Fireball"] >= 1);
        Assert.DoesNotContain(both.Notes, n => n.Contains("shared", StringComparison.Ordinal));

        var resumed = Resume(Step(s, new UseOp(["aria"]) { SlotLevel = 3 }).Next, sheets);

        Assert.Equal((1, 0), (resumed.Party[0].Start!.UsesLeft!["Fireball"], resumed.Party[0].Start!.UsesLeft!["Lightning Bolt"]));
        Assert.Contains("Aria: the slots left are shared in the build's order: Fireball 1, Lightning Bolt 0.", resumed.Notes);
        var aria = Run(resumed, E2024).Combatants.Single(c => c.Name == "Aria");
        Assert.Equal(0, aria.Resources.Single(r => r.Name == "Lightning Bolt").MeanUsed);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The down state from a sheet (C08/CV01) and the re-seed call (C04)

    [Fact]
    public void FromState_ASheetSeededDown_IsTheEnginesDownState_HealedInTheFight_IsConscious()
    {
        var down = Sheet("e-p", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44, "ac": 18 }""", E2024) with
        {
            Hp = 0,
            DeathSaves = new SheetDeathSaves(0, 1, false),
            Conditions = [new SheetCondition("unconscious", null, "until_removed", null, null), new SheetCondition("prone", null, "until_removed", null, null)],
        };
        var sheets = new Dictionary<string, CharacterSheet> { ["e-p"] = down };
        var s = Add(Encounter(E2024), Pc("e-p", "P", "character:p", down), Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("p", Total: 20), new("goblin-warrior", Total: 5)] }).Next;

        var dying = Resume(s, sheets).Party.Single().Start!;
        Assert.Equal((0, 1), (dying.Hp!.Value, dying.DeathFailures));
        Assert.Equal([("prone", SD.UntilStands)], dying.Conditions.Select(c => (c.Condition, c.Duration)));

        var healed = Resume(Step(s, new HealOp(["p"]) { Amount = 5 }).Next, sheets);
        var up = healed.Party.Single().Start!;
        Assert.Equal(5, up.Hp);
        Assert.DoesNotContain(up.Conditions, c => c.Condition == "unconscious");
        Assert.True(Run(healed, E2024).Resumed);
    }

    [Theory]
    [InlineData("deep", ", \"campaign\": \"deep\"")]
    [InlineData(null, "")]
    public void Fresh_AMemberWithNoSheetInTheFight_TheNotePrintsTheReseedCall(string? slug, string campaign)
    {
        // C04: "give it a sheet and add it again" now works: add {character} re-seeds an unseeded combatant from its sheet.
        var s = Add(Encounter(E2024), new AddEntry { EntityId = "e-dana", EntityName = "Dana", EntityHandle = "character:dana", EntitySubtype = "pc" },
            Monster(Block(E2024, "ogre"), hp: HpChoice.Avg)).Next;

        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { CampaignSlug = slug });

        Assert.Contains(
            "Dana is left out: it has no sheet in the fight and no stat block (give character:dana a sheet with campaign_character update, then re-seed it: " +
            $"combat {{\"action\": \"add\", \"combatants\": [{{\"character\": \"character:dana\"}}]{campaign}}}; or add it with srd).",
            result.Notes);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // More of the mapping

    [Fact]
    public void FromState_FixtureAAfterA23_TorchsTwoDeathSaveFailures()
    {
        var s = FixtureAAtRound2(PartyAWithProfile()).Next(); // A20: Belmakor
        s = Step(s, new DamageOp(["mummy-2"]) { Parts = [new(8, null, "slashing")], Magical = true }).Next; // A21
        s = s.Next(); // A22: the Mummy Lord
        s = Step(s, new DamageOp(["torch"]) { Parts = [new(25, null, "bludgeoning"), new(42, null, "necrotic")], Critical = true, Source = "mummy-lord" }).Next; // A23
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = Sheets(PartyAWithProfile()) });
        var torch = result.Party.Single(p => p.Spec.Name == "Lieutenant James Torch").Start!;
        Assert.Equal((0, 0, 2), (torch.Hp!.Value, torch.DeathSuccesses, torch.DeathFailures));
        Assert.True(Run(result, E2014).Resumed);
    }

    [Fact]
    public void FromState_ADeadCreatureThatOnlyAnchorsARoundsEffect_IsAPlaceholder()
    {
        // During the Lich's turn Q blesses P for 3 rounds: Q is the source, the Lich (the turn-holder) the anchor.
        var (s, sheets) = Routed("Lich", "Goon");
        s = s.Next().Next(); // the Lich's turn
        s = Step(s, new ConditionOp(["P"]) { Add = ["poisoned"], Duration = "3 rounds", Source = "Q" }).Next;
        s = Step(s, new DamageOp(["Lich"]) { Amount = 99 }).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        Assert.Equal(["Lich", "Goon"], result.Enemies.Select(e => e.Spec.Name));
        Assert.True(result.Enemies[0].Start!.Placeholder);
        var poisoned = result.Party[0].Start!.Conditions.Single();
        Assert.Equal((SD.Rounds, 2, 3), (poisoned.Duration, poisoned.SourceEntry!.Value, poisoned.RoundsLeft!.Value));
        Assert.True(Run(result, E2024).Resumed);
    }

    [Fact]
    public void Fresh_AnSrdAllyThatLeft_IsNotInTheParty()
    {
        var (s, sheets) = Routed("M");
        s = Add(s, Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg, name: "Guide") with { Side = "ally" }).Next;
        s = Step(s, new LeaveOp(["Guide"])).Next;
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { Sheets = sheets });
        Assert.Equal(["P", "Q"], result.Party.Select(p => p.Spec.Name));
        Assert.Contains("Combatants who left are left out: Guide.", result.Notes);
    }

    [Fact]
    public void FromState_ADefeatedEnemyWithUnknownHitPoints_DoesNotBlockIt()
    {
        var (s, sheets) = Fighter(E2024);
        s = s with { PlayerCampaign = true };
        s = Add(s, Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg, name: "Foe"), Monster(Block(E2024, "ogre"))).Next;
        Assert.False(s.Named("Ogre").HpKnown);
        s = Step(s, new InitiativeOp { Rolls = [new("P", Total: 20), new("Foe", Total: 15), new("Ogre", Total: 10)] }).Next;
        s = Step(s, new ConditionOp(["Ogre"]) { Add = ["unconscious"] }).Next;
        Assert.True(s.Named("Ogre").Defeated);
        var result = TrackerSimulation.Build(s, new TrackerSimulationInputs { FromState = true, Sheets = sheets });
        Assert.Equal(["Foe"], result.Enemies.Select(e => e.Spec.Name));
        Assert.True(Run(result, E2024).Resumed);
    }

    /// <summary>P, a sheet-seeded level 5 fighter (44 HP, AC 18), alone in an encounter of <paramref name="edition"/>.</summary>
    private static (EncounterState State, Dictionary<string, CharacterSheet> Sheets) Fighter(string edition)
    {
        var sheets = new Dictionary<string, CharacterSheet>
        {
            ["e-p"] = Sheet("e-p", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44, "ac": 18 }""", edition),
        };
        return (Add(Encounter(edition), Pc("e-p", "P", "character:p", sheets["e-p"])).Next, sheets);
    }
}
