using System.Text.Json.Nodes;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: the turn order and pointer behave as contract D14/D15 and §6.3 say — the pointer is an id (adding a higher
/// initiative mid-round or removing an earlier combatant never moves it; removing the actor moves to the next), a wrap
/// increments the round exactly once, <c>next</c> passes over the dead, the left, the defeated unconscious that make no
/// death saves and those without initiative (never a dying death-save maker), init groups roll once and never tie among
/// themselves, <c>prev</c> reverts its <c>next</c> exactly only when nothing happened since, the 2014 lair slot is
/// reminded at initiative 20 (never in 2024), and legendary actions are refused on the creature's own turn.
/// </summary>
public sealed class CombatTurnTests
{
    private static EncounterState Abc(string edition = E2024) => Fight(edition, ("A", 20), ("B", 15), ("C", 10));

    // ------------------------------------------------------------------------------------------------------------------
    // The pointer (the PWA bugs, D15)

    [Fact]
    public void Next_BeforeTheFirstInitiative_IsRefused()
    {
        var s = Add(Encounter(), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }).Next;
        var ex = Assert.Throws<DndInputException>(() => Step(s, new NextOp()));
        Assert.Contains("before the first initiative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Next_FromNotTheTurnHolder_IsRefusedAndChangesNothing()
    {
        var s = Abc();
        var ex = Assert.Throws<DndInputException>(() => Step(s, new NextOp("B")));
        Assert.Contains("the turn already moved to A", ex.Message, StringComparison.Ordinal);
        Assert.Equal("B", Step(s, new NextOp("A")).Next.TurnHolder!.Name);
    }

    [Fact]
    public void Next_TheOrderWraps_RoundIncrementsExactlyOnce()
    {
        var s = Abc();
        var rounds = new List<int>();
        for (var i = 0; i < 9; i++)
        {
            s = s.Next();
            rounds.Add(s.Round);
        }

        Assert.Equal([1, 1, 2, 2, 2, 3, 3, 3, 4], rounds);
    }

    [Fact]
    public void Add_AHigherInitiativeMidRound_KeepsTheActor()
    {
        var s = Abc().Next(); // B acts
        s = Add(s, new AddEntry { Name = "Z", Hp = HpChoice.Of(5) }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Z", Total: 30)] }).Next;
        Assert.Equal("B", s.TurnHolder!.Name);
        Assert.Equal(["Z", "A", "B", "C"], s.Order.Select(c => c.Name));
        s = s.Next().Next(); // C, then the wrap: Z first in round 2
        Assert.Equal(("Z", 2), (s.TurnHolder!.Name, s.Round));
    }

    [Fact]
    public void Leave_AnEarlierCombatant_KeepsTheActor_TheLeaverIsPassedOver()
    {
        var s = Abc().Next(); // B acts
        s = Step(s, new LeaveOp(["A"])).Next;
        Assert.Equal("B", s.TurnHolder!.Name);
        s = s.Next().Next(); // C, wrap: A has left, so B
        Assert.Equal(("B", 2), (s.TurnHolder!.Name, s.Round));
    }

    [Fact]
    public void Leave_TheActor_MovesTheTurnToTheNext()
    {
        var s = Abc().Next(); // B acts
        var left = Step(s, new LeaveOp(["B"]));
        Assert.Equal("C", left.Next.TurnHolder!.Name);
        Assert.Contains(left.Changes, c => c.Kind == L.Remove);
        Assert.Contains(left.Changes, c => c.Kind == L.Turn);
    }

    [Fact]
    public void Leave_TheLastActorOfTheRound_WrapsOnce()
    {
        var (s, _) = NextUntil(Abc(), "C");
        s = Step(s, new LeaveOp(["C"])).Next;
        Assert.Equal(("A", 2), (s.TurnHolder!.Name, s.Round));
    }

    [Fact]
    public void AddedAfterRound1_HasNoInitiative_NextPassesItAndItIsReminded()
    {
        var s = Abc();
        var added = Add(s, new AddEntry { Name = "Late", Hp = HpChoice.Of(5) });
        Assert.Null(added.Next.Named("Late").Initiative);
        var reminder = Assert.Single(added.Of(K.NoInitiative));
        Assert.Equal("combat {\"action\": \"initiative\", \"rolls\": [{\"combatant\": \"late\", \"total\": …}]}", reminder.Call);
        var (after, steps) = NextUntil(added.Next, "A");
        Assert.DoesNotContain(steps, r => r.Next.TurnHolder!.Name == "Late");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Next_PassesOverTheDefeatedUnconscious_NeverTheDyingDeathSaveMaker(bool deathSaves, bool skipped)
    {
        // A monster without death saves is knocked out (2024: 1 HP, unconscious); one with them drops dying. Both are defeated.
        var s = Encounter();
        s = Add(s, new AddEntry { Name = "A", Hp = HpChoice.Of(30) }, new AddEntry { Name = "Foe", Hp = HpChoice.Of(10), DeathSaves = deathSaves },
            new AddEntry { Name = "C", Hp = HpChoice.Of(30) }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("A", Total: 20), new("Foe", Total: 15), new("C", Total: 10)] }).Next;
        s = Step(s, new DamageOp(["Foe"]) { Amount = 11, KnockOut = !deathSaves }).Next;
        Assert.False(s.Named("Foe").Dead);
        Assert.True(s.Named("Foe").Defeated);
        Assert.True(s.Named("Foe").Has("unconscious"));
        Assert.Equal(skipped ? "C" : "Foe", s.Next().TurnHolder!.Name);
    }

    [Fact]
    public void Next_NoOneCanTakeATurn_IsRefused_LeavingTheLastActorKeepsThePointer()
    {
        var s = Fight(E2024, ("A", 20), ("B", 10));
        s = Step(s, new DamageOp(["B"]) { Amount = 99 }).Next;
        var left = Step(s, new LeaveOp(["A"]));
        Assert.Equal("A", left.Next.TurnHolder!.Name);
        Assert.Contains(left.Notes, n => n.Contains("No combatant is left to take a turn", StringComparison.Ordinal));
        var ex = Assert.Throws<DndInputException>(() => Step(left.Next, new NextOp()));
        Assert.Contains("No combatant can take a turn", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Initiative: groups, ties, order (D14, §5.11)

    [Fact]
    public void Initiative_InitGroup_OneServerRollForEveryMember()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy"), 3, HpChoice.Avg)).Next;
        var needs = CombatTracker.Needs(s, new InitiativeOp());
        var need = Assert.Single(needs);
        Assert.Equal(("1d20-1", CombatValues.Purposes.Initiative), (need.Expression, need.Purpose));
        s = Step(s, new InitiativeOp(), 12).Next;
        Assert.All(s.Combatants, c => Assert.Equal(11, c.Initiative));
        Assert.Equal(["Mummy", "Mummy 2", "Mummy 3"], s.Order.Select(c => c.Name));
    }

    [Fact]
    public void Initiative_TwoDifferentValuesForOneGroup_IsRefused()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy"), 2, HpChoice.Avg)).Next;
        var ex = Assert.Throws<DndInputException>(() => Step(s, new InitiativeOp { Rolls = [new("mummy", Face: 10), new("mummy-2", Face: 11)] }));
        Assert.Contains("share one value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Initiative_TiesBetweenDifferentCombatants_FlaggedWithTheCall_GroupMatesNever()
    {
        var s = Add(Encounter(), new AddEntry { Name = "Ignis", Hp = HpChoice.Of(5), InitBonus = 2 }, new AddEntry { Name = "Torch", Hp = HpChoice.Of(5), InitBonus = 2 },
            Monster(Block(E2024, "goblin-warrior"), 2, HpChoice.Avg)).Next;
        var result = Step(s, new InitiativeOp { Rolls = [new("Ignis", Total: 14), new("Torch", Total: 14), new("goblin-warrior", Total: 9)] });
        var tie = Assert.Single(result.Of(K.Tie));
        Assert.Equal("tied at 14: Ignis, Torch — give totals such as 14.5 to reorder", tie.Text);
        Assert.Equal("combat {\"action\": \"initiative\", \"rolls\": [{\"combatant\": \"ignis\", \"total\": 14.5}]}", tie.Call);
        var reordered = Step(result.Next, new InitiativeOp { Rolls = [new("Torch", Total: 14.5)] }).Next;
        Assert.Equal(["Torch", "Ignis"], reordered.Order.Take(2).Select(c => c.Name));
        Assert.Equal("Ignis", reordered.TurnHolder!.Name);
    }

    [Fact]
    public void Initiative_EqualTotals_HigherBonusFirstThenInsertionOrder()
    {
        var s = Add(Encounter(), new AddEntry { Name = "Slow", Hp = HpChoice.Of(5), InitBonus = 1 }, new AddEntry { Name = "Quick", Hp = HpChoice.Of(5), InitBonus = 4 },
            new AddEntry { Name = "Later", Hp = HpChoice.Of(5), InitBonus = 1 }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Slow", Total: 12), new("Quick", Total: 12), new("Later", Total: 12)] }).Next;
        Assert.Equal(["Quick", "Slow", "Later"], s.Order.Select(c => c.Name));
    }

    [Fact]
    public void Initiative_FirstCall_BeginsRound1OnTheTopOfTheOrder_WithATurnRow()
    {
        var s = Add(Encounter(), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }, new AddEntry { Name = "B", Hp = HpChoice.Of(5) }).Next;
        var result = Step(s, new InitiativeOp { Rolls = [new("A", Total: 3), new("B", Total: 7)] });
        Assert.Equal((1, "B"), (result.Next.Round, result.Next.TurnHolder!.Name));
        var turn = result.Changes.Single(c => c.Kind == L.Turn);
        Assert.True(CombatJson.ReadTurn(turn.Detail)!.Start);
        Assert.Equal(2, result.Changes.Count(c => c.Kind == L.Initiative));
        Assert.True(result.Says(K.Round, "Round 1"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(101.0)]
    public void Initiative_ATotalThatIsNotAFiniteNumberInRange_IsRefused(double total)
    {
        var s = Add(Encounter(), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }).Next;
        Assert.Throws<DndInputException>(() => Step(s, new InitiativeOp { Rolls = [new("A", Total: total)] }));
    }

    [Fact]
    public void Initiative_AServerRollForAnEnemyInAPlayerCampaign_SaysGiveTheDmsOrder()
    {
        var s = Add(Encounter(E2024, player: true), new AddEntry { Name = "Hero", Hp = HpChoice.Of(9), Side = "party" }, Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg)).Next;
        var result = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 12)] }, 10);
        Assert.Contains(result.Notes, n => n.Contains("Goblin Warrior", StringComparison.Ordinal) && n.EndsWith(CombatTracker.RolledHereNote + ".", StringComparison.Ordinal));
        var row = result.Changes.Single(c => c.Kind == L.Initiative && c.RollKey is not null);
        Assert.Equal(CombatTracker.RolledHereNote, System.Text.Json.Nodes.JsonNode.Parse(row.Detail!)!["note"]!.GetValue<string>());
        Assert.DoesNotContain(Step(s with { PlayerCampaign = false }, new InitiativeOp { Rolls = [new("Hero", Total: 12)] }, 10).Notes,
            n => n.Contains(CombatTracker.RolledHereNote, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("enemy", true)]
    [InlineData("neutral", true)]
    [InlineData("ally", false)]
    public void Initiative_AServerRollInAPlayerCampaign_TheNoteForEveryCombatantTheDmRuns(string side, bool note)
    {
        var s = Add(Encounter(E2024, player: true), new AddEntry { Name = "Hero", Hp = HpChoice.Of(9), Side = "party" }, new AddEntry { Name = "Other", Hp = HpChoice.Of(9), Side = side }).Next;
        var result = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 12)] }, 10);
        Assert.Equal(note, result.Notes.Any(n => n.StartsWith("Other", StringComparison.Ordinal) && n.Contains(CombatTracker.RolledHereNote, StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Context lines that must not outlive what they are about

    [Fact]
    public void Context_ADyingCombatantThatLeft_HasNoDyingLine()
    {
        var s = Fight(E2024, ["P"], ("A", 20), ("P", 15));
        var down = Step(s, new DamageOp(["P"]) { Amount = 30 });
        Assert.True(down.Says(K.Dying, "P: 0 HP"));
        var left = Step(down.Next, new LeaveOp(["P"]));
        Assert.Empty(left.Of(K.Dying));
    }

    [Fact]
    public void Context_ADeadCombatantWithoutInitiative_IsNotRemindedToRollIt()
    {
        var added = Add(Abc(), new AddEntry { Name = "Late", Hp = HpChoice.Of(5) });
        Assert.Single(added.Of(K.NoInitiative));
        var killed = Step(added.Next, new DamageOp(["Late"]) { Amount = 9 });
        Assert.True(killed.Next.Named("Late").Dead);
        Assert.Empty(killed.Of(K.NoInitiative));
    }

    [Fact]
    public void Context_AllEnemiesDown_OnlyWhenOneWasDefeated_NotWhenEveryOneFled()
    {
        var s = Fight(E2024, ["P"], ("P", 20), ("A", 15), ("B", 10));
        var fled = Step(Step(s, new LeaveOp(["A"])).Next, new LeaveOp(["B"]));
        Assert.Empty(fled.Of(K.AllEnemiesDown));
        var beaten = Step(Step(s, new LeaveOp(["A"])).Next, new DamageOp(["B"]) { Amount = 99 });
        var reminder = Assert.Single(beaten.Of(K.AllEnemiesDown));
        Assert.Equal("combat {\"action\": \"end\", \"outcome\": …}", reminder.Call);
    }

    [Fact]
    public void Initiative_FaceAndTotalTogether_IsRefused()
    {
        var s = Add(Encounter(), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }).Next;
        Assert.Throws<DndInputException>(() => Step(s, new InitiativeOp { Rolls = [new("A", Face: 10, Total: 12)] }));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // prev (§6.3)

    [Fact]
    public void Prev_AtRound1OnTheFirstTurn_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => Step(Abc(), new PrevOp()));
        Assert.Contains("round 1 on the first turn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Prev_NotTheLastChange_MovesThePointerOnlyAndKeepsTheChanges()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["frightened"], Duration = "until_end_of_source_turn", Source = "A" }).Next;
        var next = Step(s, new NextOp()); // A's turn ends: skip cleared (an automatic change)
        var damaged = Step(next.Next, new DamageOp(["C"]) { Amount = 4 });
        var turnRow = next.Changes.Single(c => c.Kind == L.Turn);
        // The last row is the damage, so prev cannot revert the next exactly.
        var prev = Step(damaged.Next, new PrevOp(damaged.Changes.Last() is var last ? new CombatLogEntry(last.Kind, last.Detail) : null));
        Assert.Equal("A", prev.Next.TurnHolder!.Name);
        Assert.Equal(26, prev.Next.Named("C").Hp);
        Assert.False(prev.Next.Named("C").Conditions.Single().SkipEnd);
        Assert.False(CombatJson.ReadTurn(prev.Changes.Single().Detail)!.Exact);
        Assert.Contains(prev.Notes, n => n.Contains("changes made since the last next were kept", StringComparison.Ordinal));
        Assert.NotNull(turnRow);
    }

    [Fact]
    public void Prev_RightAfterNext_RevertsItsAutomaticChangesExactly()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["frightened"], Duration = "until_end_of_source_turn", Source = "A" }).Next;
        var next = Step(s, new NextOp());
        var row = next.Changes.Single(c => c.Kind == L.Turn);
        var prev = Step(next.Next, new PrevOp(new CombatLogEntry(row.Kind, row.Detail)));
        Assert.Equal("A", prev.Next.TurnHolder!.Name);
        Assert.True(prev.Next.Named("C").Conditions.Single().SkipEnd);
        Assert.Equal(CombatJson.WriteConditions(s.Named("C").Conditions), CombatJson.WriteConditions(prev.Next.Named("C").Conditions));
    }

    [Fact]
    public void Prev_Twice_TheSecondMovesThePointerOnly()
    {
        var (s, _) = NextUntil(Abc(), "C");
        var next = Step(s, new NextOp()); // round 2, A
        var row = next.Changes.Single(c => c.Kind == L.Turn);
        var first = Step(next.Next, new PrevOp(new CombatLogEntry(row.Kind, row.Detail)));
        Assert.Equal(("C", 1), (first.Next.TurnHolder!.Name, first.Next.Round));
        var prevRow = first.Changes.Single();
        var second = Step(first.Next, new PrevOp(new CombatLogEntry(prevRow.Kind, prevRow.Detail)));
        Assert.Equal(("B", 1), (second.Next.TurnHolder!.Name, second.Next.Round));
        Assert.False(CombatJson.ReadTurn(second.Changes.Single().Detail)!.Exact);
    }

    [Fact]
    public void Prev_AfterTheTurnHolderLeft_NeverGoesBackToTheOneWhoLeft()
    {
        var s = Abc().Next(); // B acts
        var left = Step(s, new LeaveOp(["B"]));
        var row = left.Changes.Last();
        Assert.Equal(L.Turn, row.Kind);
        var prev = Step(left.Next, new PrevOp(new CombatLogEntry(row.Kind, row.Detail)));
        Assert.Equal("A", prev.Next.TurnHolder!.Name);
        Assert.False(CombatJson.ReadTurn(prev.Changes.Single().Detail)!.Exact);
    }

    [Fact]
    public void Prev_PointerOnlyAcrossTheWrap_GoesBackARound()
    {
        var (s, _) = NextUntil(Abc(), "C");
        s = s.Next(); // round 2, A
        var prev = Step(s, new PrevOp());
        Assert.Equal(("C", 1), (prev.Next.TurnHolder!.Name, prev.Next.Round));
    }

    [Fact]
    public void Prev_ExactRevert_RestoresALegendaryReset()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), new AddEntry { Name = "Hero", Hp = HpChoice.Of(40), Side = "party" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 20), new("Mummy Lord", Total: 10)] }).Next;
        s = Step(s, new LegendaryOp("Mummy Lord") { Amount = 2 }).Next;
        var next = Step(s, new NextOp()); // the Mummy Lord's turn: legendary reset
        Assert.Equal(0, next.Next.Named("Mummy Lord").Legendary!.Used);
        var row = next.Changes.Single(c => c.Kind == L.Turn);
        var prev = Step(next.Next, new PrevOp(new CombatLogEntry(row.Kind, row.Detail)));
        Assert.Equal(2, prev.Next.Named("Mummy Lord").Legendary!.Used);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Lair (§5.12: 2014 only, a DM-declared lair)

    [Fact]
    public void Lair2014_RemindedWhenTheOrderPassesInitiative20_LosingTies()
    {
        var s = Encounter(E2014, lair: true);
        s = Add(s, new AddEntry { Name = "Fast", Hp = HpChoice.Of(5) }, new AddEntry { Name = "Twenty", Hp = HpChoice.Of(5) }, new AddEntry { Name = "Slow", Hp = HpChoice.Of(5) }).Next;
        var started = Step(s, new InitiativeOp { Rolls = [new("Fast", Total: 22), new("Twenty", Total: 20), new("Slow", Total: 8)] });
        Assert.Empty(started.Of(K.LairAction));
        var toTwenty = Step(started.Next, new NextOp());
        Assert.Empty(toTwenty.Of(K.LairAction));
        var toSlow = Step(toTwenty.Next, new NextOp());
        Assert.True(toSlow.Says(K.LairAction, "lair action (initiative 20, losing ties)"));
        var wrap = Step(toSlow.Next, new NextOp());
        Assert.Empty(wrap.Of(K.LairAction));
    }

    [Fact]
    public void Lair2014_NobodyAt20_RemindedAtEveryRoundsStart()
    {
        var s = Encounter(E2014, lair: true);
        s = Add(s, new AddEntry { Name = "A", Hp = HpChoice.Of(5) }, new AddEntry { Name = "B", Hp = HpChoice.Of(5) }).Next;
        var started = Step(s, new InitiativeOp { Rolls = [new("A", Total: 15), new("B", Total: 5)] });
        Assert.Single(started.Of(K.LairAction));
        var b = Step(started.Next, new NextOp());
        Assert.Empty(b.Of(K.LairAction));
        Assert.Single(Step(b.Next, new NextOp()).Of(K.LairAction));
    }

    /// <summary>
    /// The 2014 lair loses ties at initiative 20 (review M, mutant DM12): with every combatant at 20, round 1 opens with no
    /// lair action (it acts after the last 20, not before the first), none comes between them, and one comes when the order
    /// wraps after the last of them.
    /// </summary>
    [Fact]
    public void Lair2014_EveryoneAt20_NoLairActionBeforeTheFirstTurn_OneAtTheWrap()
    {
        var s = Encounter(E2014, lair: true);
        s = Add(s, new AddEntry { Name = "A", Hp = HpChoice.Of(5) }, new AddEntry { Name = "B", Hp = HpChoice.Of(5) }).Next;

        var started = Step(s, new InitiativeOp { Rolls = [new("A", Total: 20), new("B", Total: 20)] });
        var b = Step(started.Next, new NextOp());
        var wrap = Step(b.Next, new NextOp());

        Assert.Empty(started.Of(K.LairAction));
        Assert.Empty(b.Of(K.LairAction));
        Assert.Single(wrap.Of(K.LairAction));
    }

    [Fact]
    public void Lair2024_NeverReminded()
    {
        var s = Encounter(E2024, lair: true);
        s = Add(s, new AddEntry { Name = "A", Hp = HpChoice.Of(5) }, new AddEntry { Name = "B", Hp = HpChoice.Of(5) }).Next;
        var started = Step(s, new InitiativeOp { Rolls = [new("A", Total: 25), new("B", Total: 5)] });
        Assert.Empty(started.Of(K.LairAction));
        Assert.Empty(Step(started.Next, new NextOp()).Of(K.LairAction));
        Assert.Empty(Step(Step(started.Next, new NextOp()).Next, new NextOp()).Of(K.LairAction));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Legendary actions (§5.12, §6.6)

    private static EncounterState LordFight(string edition = E2014)
    {
        var s = Add(Encounter(edition), Monster(Block(edition, edition == E2014 ? "mummy-lord" : "aboleth"), hp: HpChoice.Avg),
            new AddEntry { Name = "Hero", Hp = HpChoice.Of(40), Side = "party" }).Next;
        return Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 20), new(edition == E2014 ? "Mummy Lord" : "Aboleth", Total: 10)] }).Next;
    }

    [Fact]
    public void Legendary_TheStepPrintsItsDamageCall_TheLegendaryCreatureAsSource_SentAsPrintedTheRollIsItsOwn()
    {
        // LR03: a legendary action's damage sent with no source was the turn-holder's roll, open and labelled with the PC's
        // name in a DM campaign; the step that spends the action prints the damage call naming the creature as its source.
        var s = LordFight(E2024); // Hero's turn

        var step = Step(s, new LegendaryOp("aboleth") { Name = "Lash" });

        var reminder = Assert.Single(step.Of(K.OffTurnRoll));
        Assert.Equal("Aboleth acts outside its turn (legendary action Lash): give its damage \"source\", or the roll is Hero's:", reminder.Text);
        Assert.Equal("combat {\"action\": \"damage\", \"targets\": […], \"dice\": …, \"source\": \"aboleth\"}", reminder.Call);
        var printed = Assert.IsType<string>(reminder.Call);
        var call = JsonNode.Parse(printed[printed.IndexOf('{')..].Replace("[…]", "[\"hero\"]").Replace("…", "\"2d6+5\""))!;
        var op = new DamageOp([call["targets"]![0]!.GetValue<string>()]) { Dice = call["dice"]!.GetValue<string>(), Source = call["source"]!.GetValue<string>() };
        Assert.Equal(step.Next.Named("Aboleth").Id, Assert.Single(CombatTracker.Needs(step.Next, op)).CombatantId);

        // Legendary Resistance rolls no damage.
        Assert.Empty(Step(s, new LegendaryOp("aboleth") { Resistance = true }).Of(K.OffTurnRoll));
    }

    [Fact]
    public void Lair2014_TheReminderPrintsTheDamageCall_TheLairsLegendaryCreatureAsSource()
    {
        // LR03: a lair action is rolled at initiative 20, on nobody's turn: without a source its damage is the turn-holder's.
        var s = Add(Encounter(E2014, lair: true), Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), new AddEntry { Name = "Hero", Hp = HpChoice.Of(40), Side = "party" }).Next;
        var started = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 15), new("Mummy Lord", Total: 10)] });

        var reminder = Assert.Single(started.Of(K.LairAction));
        Assert.Equal("lair action (initiative 20, losing ties): give its damage \"source\", or the roll is the turn-holder's:", reminder.Text);
        Assert.Equal("combat {\"action\": \"damage\", \"targets\": […], \"dice\": …, \"source\": \"mummy-lord\"}", reminder.Call);

        // No legendary creature (or several): the source is the caller's to give.
        var plain = Add(Encounter(E2014, lair: true), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }, new AddEntry { Name = "B", Hp = HpChoice.Of(5) }).Next;
        Assert.Equal("combat {\"action\": \"damage\", \"targets\": […], \"dice\": …, \"source\": …}",
            Assert.Single(Step(plain, new InitiativeOp { Rolls = [new("A", Total: 15), new("B", Total: 5)] }).Of(K.LairAction)).Call);
    }

    [Fact]
    public void Legendary_DuringItsOwnTurn_IsRefused()
    {
        var s = LordFight().Next();
        var ex = Assert.Throws<DndInputException>(() => Step(s, new LegendaryOp("mummy-lord")));
        Assert.Contains("own turn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legendary_DuringAnotherTurn_SpendsTheCost_RefusedBeyondWhatIsLeft()
    {
        var s = LordFight();
        s = Step(s, new LegendaryOp("mummy-lord") { Amount = 2 }).Next;
        Assert.Equal(1, s.Named("Mummy Lord").Legendary!.ActionsLeft);
        var ex = Assert.Throws<DndInputException>(() => Step(s, new LegendaryOp("mummy-lord") { Amount = 2 }));
        Assert.Contains("only 1 of 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legendary_NamedActionWithACost_TakesItsCost()
    {
        var block = Block(E2014, "mummy-lord");
        var costly = block.Legendary!.Actions.First(a => a.LegendaryCost > 1);
        var s = Step(LordFight(), new LegendaryOp("mummy-lord") { Name = costly.Name }).Next;
        Assert.Equal(costly.LegendaryCost, s.Named("Mummy Lord").Legendary!.Used);
    }

    [Fact]
    public void Legendary_Incapacitated_IsRefused_AndNotOfferedInTheContext()
    {
        var s = Step(LordFight(), new ConditionOp(["mummy-lord"]) { Add = ["stunned"] });
        Assert.DoesNotContain(s.Reminders, r => r.Kind == K.LegendaryAvailable);
        Assert.Throws<DndInputException>(() => Step(s.Next, new LegendaryOp("mummy-lord")));
    }

    [Fact]
    public void Legendary_2014SurprisedBeforeItsFirstTurn_IsRefused()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), new AddEntry { Name = "Hero", Hp = HpChoice.Of(40), Side = "party" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 20), new("Mummy Lord", Total: 10)], Surprised = ["mummy-lord"] }).Next;
        Assert.Throws<DndInputException>(() => Step(s, new LegendaryOp("mummy-lord")));
        var lordTurn = Step(s, new NextOp());
        Assert.True(lordTurn.Says(K.Surprised, "Mummy Lord is surprised"));
        var after = lordTurn.Next.Next();
        Assert.False(after.Named("Mummy Lord").Surprised);
        Assert.Equal(1, Step(after, new LegendaryOp("mummy-lord")).Next.Named("Mummy Lord").Legendary!.Used);
    }

    [Fact]
    public void LegendaryResistance_AnyTime_EvenItsOwnTurn_NeverResetByTheTracker()
    {
        var s = LordFight(E2024).Next(); // the Aboleth's own turn
        s = Step(s, new LegendaryOp("aboleth") { Resistance = true }).Next;
        Assert.Equal(2, s.Named("Aboleth").Legendary!.ResistanceLeft);
        (s, _) = NextUntil(s, "Aboleth");
        Assert.Equal(2, s.Named("Aboleth").Legendary!.ResistanceLeft);
    }

    [Fact]
    public void ReactionUsed_ResetAtItsTurnStart()
    {
        var s = Abc();
        var c = s.Named("C");
        s = s with { Combatants = s.Combatants.Select(x => x.Id == c.Id ? x with { ReactionUsed = true } : x).ToList() };
        s = s.Next();
        Assert.True(s.Named("C").ReactionUsed);
        s = s.Next();
        Assert.False(s.Named("C").ReactionUsed);
    }

    [Fact]
    public void Recharge_SpentAtItsTurnStart_RemindedWithTheRestoreCall()
    {
        var s = Add(Encounter(E2024), Monster(Block(E2024, "red-dragon-wyrmling"), hp: HpChoice.Avg), new AddEntry { Name = "Hero", Hp = HpChoice.Of(40), Side = "party" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 20), new("Red Dragon Wyrmling", Total: 10)] }).Next;
        var breath = s.Named("Red Dragon Wyrmling").Resources.Single(r => r.Value.Kind == CombatValues.ResourceKinds.Recharge);
        s = Step(s, new UseOp(["red-dragon-wyrmling"]) { Resource = breath.Value.Name }).Next;
        Assert.False(s.Named("Red Dragon Wyrmling").Resources[breath.Key].Ready);
        var turn = Step(s, new NextOp());
        var reminder = Assert.Single(turn.Of(K.Recharge));
        Assert.Contains($"roll d6: {breath.Value.Name} recharges on {breath.Value.Min}-6", reminder.Text, StringComparison.Ordinal);
        var recharged = Step(turn.Next, new UseOp(["red-dragon-wyrmling"]) { Resource = breath.Value.Name, Amount = -1 }).Next;
        Assert.True(recharged.Named("Red Dragon Wyrmling").Resources[breath.Key].Ready);
    }

    /// <summary>
    /// A Recharge 6 ability (the 2024 ankheg's Acid Spray) is reminded "recharges on 6", never "6-6" (review M, mutant DM09):
    /// every other recharge test used a 5-6 breath weapon.
    /// </summary>
    [Fact]
    public void Recharge_ARecharge6Ability_RemindsRechargesOn6()
    {
        var s = Add(Encounter(E2024), Monster(Block(E2024, "ankheg"), hp: HpChoice.Avg), new AddEntry { Name = "Hero", Hp = HpChoice.Of(40), Side = "party" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 20), new("Ankheg", Total: 10)] }).Next;
        var spray = s.Named("Ankheg").Resources.Single(r => r.Value.Kind == CombatValues.ResourceKinds.Recharge);
        Assert.Equal(6, spray.Value.Min);
        s = Step(s, new UseOp(["ankheg"]) { Resource = spray.Value.Name }).Next;

        var reminder = Assert.Single(Step(s, new NextOp()).Of(K.Recharge));

        Assert.EndsWith($"roll d6: {spray.Value.Name} recharges on 6", reminder.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Surprised2014_LosesItsFirstTurnReminder_FlagClearedAtItsEnd()
    {
        var s = Add(Encounter(E2014), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }, new AddEntry { Name = "B", Hp = HpChoice.Of(5) }).Next;
        var started = Step(s, new InitiativeOp { Rolls = [new("A", Total: 15), new("B", Total: 5)], Surprised = ["A"] });
        Assert.True(started.Says(K.Surprised, "A is surprised"));
        var next = Step(started.Next, new NextOp());
        Assert.False(next.Next.Named("A").Surprised);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Surprised2024_TheFlagIsClearedOnceInitiativeIsRolled_ItsOnlyEffectIsSpent(bool rolled)
    {
        // C14: 2024 surprise is Disadvantage on the initiative roll and nothing after; a flag kept all fight read as
        // "still surprised" in every author table.
        var s = Add(Encounter(E2024), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }, new AddEntry { Name = "B", Hp = HpChoice.Of(5) }).Next;
        s = Step(s, new SurpriseOp(["A"])).Next;
        Assert.True(s.Named("A").Surprised);

        var started = rolled
            ? Step(s, new InitiativeOp { Rolls = [new("B", Total: 5)] }, 15, 6)
            : Step(s, new InitiativeOp { Rolls = [new("A", Total: 15), new("B", Total: 5)] });

        Assert.False(started.Next.Named("A").Surprised);
        Assert.False(started.Next.Next().Named("A").Surprised);
    }

    [Fact]
    public void Surprised2024_AnUnrolledCombatantKeepsTheFlagUntilItsRoll()
    {
        var s = Add(Encounter(E2024), new AddEntry { Name = "A", Hp = HpChoice.Of(5) }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("A", Total: 15)] }).Next;
        s = Add(s, new AddEntry { Name = "Late", Hp = HpChoice.Of(5) }).Next;
        s = Step(s, new SurpriseOp(["Late"])).Next;
        Assert.True(s.Named("Late").Surprised);
        Assert.Equal("2d20kl1", Assert.Single(CombatTracker.Needs(s, new InitiativeOp())).Expression);
        Assert.False(Step(s, new InitiativeOp(), 4, 9).Next.Named("Late").Surprised);
    }

    [Fact]
    public void APlannedFight_AStepIsRefused_TheStartCallItPrintsIsValidJsonForAnyName()
    {
        // The encounter name is JSON-escaped in the printed call, as every other call the tracker prints.
        var planned = Add(Encounter(name: "Bob's \"big\" fight") with { Status = "planned" }, new AddEntry { Name = "X", Hp = HpChoice.Of(5) }).Next;

        var ex = Assert.Throws<DndInputException>(() => Step(planned, new DamageOp(["X"]) { Amount = 1 }));

        const string call = "combat {\"action\": \"start\", \"encounter\": \"Bob's \\\"big\\\" fight\"}";
        Assert.Equal($"damage is refused: the fight \"Bob's \"big\" fight\" is planned; start it first ({call}).", ex.Message);
        Assert.Equal("Bob's \"big\" fight", System.Text.Json.Nodes.JsonNode.Parse(call["combat ".Length..])!["encounter"]!.GetValue<string>());
    }

    [Fact]
    public void Surprised2024_DisadvantageOnTheServerRoll_NoTurnLost()
    {
        var s = Add(Encounter(E2024), new AddEntry { Name = "A", Hp = HpChoice.Of(5), InitBonus = 2 }).Next;
        var need = Assert.Single(CombatTracker.Needs(s, new InitiativeOp { Surprised = ["A"] }));
        Assert.Equal("2d20kl1+2", need.Expression);
        var started = Step(s, new InitiativeOp { Surprised = ["A"] }, 15, 6);
        Assert.Equal(8, started.Next.Named("A").Initiative);
        Assert.DoesNotContain(started.Reminders, r => r.Kind == K.Surprised);
    }
}
