using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: every duration of contract §5.13 ends exactly at its boundary — the turn-anchored kinds at their anchor's
/// next start or end (one applied during its anchor's own turn skips that turn's end), <c>rounds</c> at the START of its
/// anchor's turn in round applied + N (anchored on the top of the order when applied before initiative), <c>end_of_round</c>
/// at the wrap after its round, <c>concentration</c> with its concentration, <c>until_escape</c> when its source is
/// incapacitated, dead or left, <c>zero_hp</c> when hit points rise above 0 — and a dead or left anchor's place still fires
/// what it anchors. A, B and C act in that order (20, 15, 10); the fight starts in round 1 on A's turn.
/// </summary>
public sealed class CombatDurationTests
{
    private static EncounterState Abc(string edition = E2024) => Fight(edition, ("A", 20), ("B", 15), ("C", 10));

    private static EncounterState Apply(EncounterState s, string target, string name, string? duration, string? source = null, int? dc = null, string? ability = null) =>
        Step(s, new ConditionOp([target]) { Add = [name], Duration = duration, Source = source, Dc = dc, Ability = ability }).Next;

    private static bool Has(EncounterState s, string who, string name) => s.Named(who).Has(name);

    // ------------------------------------------------------------------------------------------------------------------
    // The source's turn

    [Fact]
    public void UntilEndOfSourceTurn_AppliedDuringTheSourcesTurn_SkipsThatEndAndEndsAtTheNext()
    {
        var s = Apply(Abc(), "C", "frightened", D.UntilEndOfSourceTurn, source: "A");
        Assert.True(s.Named("C").Conditions.Single().SkipEnd);
        s = s.Next(); // A's turn ends: skipped, flag cleared
        Assert.True(Has(s, "C", "frightened"));
        Assert.False(s.Named("C").Conditions.Single().SkipEnd);
        (s, _) = NextUntil(s, "A");
        Assert.True(Has(s, "C", "frightened"));
        Assert.Equal(2, s.Round);
        var ended = Step(s, new NextOp());
        Assert.False(Has(ended.Next, "C", "frightened"));
        Assert.True(ended.Says(K.Expired, "frightened (A) ended on C"));
    }

    [Fact]
    public void UntilEndOfSourceTurn_AppliedDuringAnotherTurn_EndsAtTheSourcesNextTurnEnd()
    {
        var s = Apply(Abc(), "C", "frightened", D.UntilEndOfSourceTurn, source: "B");
        Assert.False(s.Named("C").Conditions.Single().SkipEnd);
        s = s.Next(); // A ends, B's turn
        Assert.True(Has(s, "C", "frightened"));
        s = s.Next(); // B ends
        Assert.False(Has(s, "C", "frightened"));
        Assert.Equal(1, s.Round);
    }

    [Fact]
    public void UntilStartOfSourceTurn_AppliedDuringTheSourcesTurn_EndsAtItsNextStart()
    {
        var s = Apply(Abc(), "C", "stunned", D.UntilStartOfSourceTurn, source: "A");
        (s, var steps) = NextUntil(s, "C");
        Assert.True(Has(s, "C", "stunned"));
        s = s.Next(); // C ends; wrap; A starts
        Assert.Equal(2, s.Round);
        Assert.False(Has(s, "C", "stunned"));
    }

    [Fact]
    public void UntilStartOfSourceTurn_AppliedDuringAnotherTurn_EndsAtTheSourcesStartThisRound()
    {
        var s = Apply(Abc(), "C", "stunned", D.UntilStartOfSourceTurn, source: "B");
        s = s.Next(); // B starts
        Assert.False(Has(s, "C", "stunned"));
    }

    [Fact]
    public void SourceTurnDuration_NoSourceBeforeInitiative_IsRefused()
    {
        var s = Encounter();
        s = Add(s, new AddEntry { Name = "A", Hp = HpChoice.Of(10) }, new AddEntry { Name = "C", Hp = HpChoice.Of(10) }).Next;
        var ex = Assert.Throws<DndInputException>(() => Apply(s, "C", "frightened", D.UntilEndOfSourceTurn));
        Assert.Contains("needs a source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceTurnDuration_SourceNamingNoCombatant_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => Apply(Abc(), "C", "frightened", D.UntilEndOfSourceTurn, source: "a ghost in the walls"));
        Assert.Contains("is not one", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The target's turn

    [Fact]
    public void UntilEndOfTargetTurn_AppliedDuringAnotherTurn_EndsAtTheTargetsTurnEnd()
    {
        var s = Apply(Abc(), "C", "blinded", "until the end of its next turn");
        Assert.Equal(D.UntilEndOfTargetTurn, s.Named("C").Conditions.Single().Duration);
        (s, _) = NextUntil(s, "C");
        Assert.True(Has(s, "C", "blinded"));
        s = s.Next();
        Assert.False(Has(s, "C", "blinded"));
    }

    [Fact]
    public void UntilEndOfTargetTurn_AppliedDuringTheTargetsTurn_SkipsThatEnd()
    {
        var (s, _) = NextUntil(Abc(), "C");
        s = Apply(s, "C", "blinded", D.UntilEndOfTargetTurn);
        s = s.Next(); // C ends (skipped), round 2
        Assert.True(Has(s, "C", "blinded"));
        (s, _) = NextUntil(s, "C");
        s = s.Next();
        Assert.False(Has(s, "C", "blinded"));
        Assert.Equal(3, s.Round);
    }

    [Fact]
    public void UntilStartOfTargetTurn_EndsAtTheTargetsNextStart_AppliedInItsOwnTurnLastsTheRound()
    {
        var s = Apply(Abc(), "C", "restrained", D.UntilStartOfTargetTurn);
        s = s.Next().Next(); // C starts
        Assert.False(Has(s, "C", "restrained"));

        s = Apply(s, "C", "restrained", D.UntilStartOfTargetTurn);
        s = s.Next(); // round 2, A
        Assert.True(Has(s, "C", "restrained"));
        (s, _) = NextUntil(s, "C");
        Assert.False(Has(s, "C", "restrained"));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Rounds and the round's end

    [Fact]
    public void Rounds_ThreeRoundsInBsTurn_EndAtTheStartOfBsTurnInRound4()
    {
        var s = Abc().Next(); // B's turn, round 1
        s = Apply(s, "C", "poisoned", "3 rounds");
        var poisoned = s.Named("C").Conditions.Single();
        Assert.Equal(new ConditionExpiry(4, CombatValues.ExpiryPoints.Start, s.Named("B").Id), poisoned.Expires);
        (s, _) = NextUntil(s, "A");
        (s, _) = NextUntil(s, "A");
        (s, _) = NextUntil(s, "A"); // round 4, A
        Assert.Equal(4, s.Round);
        Assert.True(Has(s, "C", "poisoned"));
        var started = Step(s, new NextOp()); // B starts in round 4
        Assert.False(Has(started.Next, "C", "poisoned"));
        Assert.True(started.Says(K.Expired, "poisoned"));
    }

    [Theory]
    [InlineData("1 minute", 10)]
    [InlineData("10 minutes", 100)]
    [InlineData("1 hour", 600)]
    [InlineData("3 rounds", 3)]
    [InlineData("1 round", 1)]
    public void Rounds_PhrasesCountFromTheRoundApplied(string phrase, int rounds)
    {
        var s = Apply(Abc(), "C", "Bless", phrase);
        Assert.Equal(1 + rounds, s.Named("C").Conditions.Single().Expires!.Round);
    }

    [Fact]
    public void Rounds_AppliedBeforeInitiative_CountFromRound1AndAnchorOnTheTopOfTheOrder()
    {
        var s = Encounter();
        s = Add(s, new AddEntry { Name = "A", Hp = HpChoice.Of(10) }, new AddEntry { Name = "B", Hp = HpChoice.Of(10) }).Next;
        s = Apply(s, "B", "Haste", "1 minute");
        Assert.Equal(new ConditionExpiry(11, CombatValues.ExpiryPoints.Start, null), s.Named("B").Conditions.Single().Expires);
        s = Step(s, new InitiativeOp { Rolls = [new("A", Total: 5), new("B", Total: 12)] }).Next;
        Assert.Equal(new ConditionExpiry(11, CombatValues.ExpiryPoints.Start, s.Named("B").Id), s.Named("B").Conditions.Single().Expires);
    }

    [Fact]
    public void EndOfRound_EndsAtTheWrapAfterItsRound_DefaultThisRound()
    {
        var s = Apply(Abc(), "C", "stunned", "end of round 2");
        Assert.Equal(new ConditionExpiry(2, CombatValues.ExpiryPoints.RoundEnd, null), s.Named("C").Conditions.Single().Expires);
        (s, _) = NextUntil(s, "C");
        s = s.Next(); // wrap to round 2
        Assert.True(Has(s, "C", "stunned"));
        (s, _) = NextUntil(s, "C");
        Assert.True(Has(s, "C", "stunned"));
        var wrap = Step(s, new NextOp());
        Assert.Equal(3, wrap.Next.Round);
        Assert.False(Has(wrap.Next, "C", "stunned"));
        Assert.True(wrap.Says(K.Expired, "stunned", "round 2 is over"));

        var now = Apply(Abc(), "B", "deafened", "end of round");
        Assert.Equal(1, now.Named("B").Conditions.Single().Expires!.Round);
    }

    [Fact]
    public void EndOfRound_APastRound_IsRefused()
    {
        var (s, _) = NextUntil(Abc(), "C");
        s = s.Next(); // round 2
        var ex = Assert.Throws<DndInputException>(() => Apply(s, "C", "stunned", "end of round 1"));
        Assert.Contains("already over", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Saves, concentration, escape, standing, zero HP

    [Fact]
    public void SaveEnds_PromptsAtTheEndOfTheTargetsTurn_EndsOnlyWhenRemoved()
    {
        var s = Apply(Abc(), "C", "paralyzed", "save ends", dc: 14, ability: "wis");
        (s, _) = NextUntil(s, "C");
        var ended = Step(s, new NextOp());
        Assert.True(Has(ended.Next, "C", "paralyzed"));
        var prompt = Assert.Single(ended.Of(K.SaveEnds));
        Assert.Contains("Wis save DC 14", prompt.Text, StringComparison.Ordinal);
        Assert.Equal("combat {\"action\": \"condition\", \"targets\": [\"c\"], \"remove\": [\"paralyzed\"]}", prompt.Call);
        var removed = Step(ended.Next, new ConditionOp(["C"]) { Remove = ["paralyzed"] }).Next;
        Assert.False(Has(removed, "C", "paralyzed"));
    }

    [Fact]
    public void SaveEnds_WithoutDcOrAbility_IsRefused()
    {
        Assert.Throws<DndInputException>(() => Apply(Abc(), "C", "paralyzed", "save ends", dc: 14));
    }

    [Fact]
    public void TimedWithASave_PromptsEveryTurnEndAndStillEndsByTime()
    {
        var s = Apply(Abc(), "C", "frightened", "1 round", dc: 14, ability: "wis");
        (s, _) = NextUntil(s, "C");
        var ended = Step(s, new NextOp());
        Assert.Single(ended.Of(K.SaveEnds));
        (s, _) = NextUntil(ended.Next, "A");
        Assert.False(Has(s, "C", "frightened"));
    }

    [Fact]
    public void Concentration_HeldConditionEndsWhenTheConcentrationIsDropped()
    {
        var s = Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Hold Person" }).Next;
        s = Apply(s, "C", "paralyzed", "concentration");
        Assert.Equal(s.Named("A").Id, s.Named("C").Conditions.Single().HeldBy);
        var dropped = Step(s, new ConcentrationOp(["A"]) { Drop = true });
        Assert.False(Has(dropped.Next, "C", "paralyzed"));
        Assert.Null(dropped.Next.Named("A").Concentration);
    }

    [Fact]
    public void Concentration_SourceNotConcentrating_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => Apply(Abc(), "C", "paralyzed", "concentration"));
        Assert.Contains("not concentrating", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stunned")]
    [InlineData("paralyzed")]
    [InlineData("unconscious")]
    [InlineData("incapacitated")]
    [InlineData("petrified")]
    public void UntilEscape_EndsWhenTheGrapplerIsIncapacitated(string incapacitating)
    {
        var s = Apply(Abc(), "C", "grappled", null, source: "A", dc: 13);
        Assert.Equal((D.UntilEscape, 13), (s.Named("C").Conditions.Single().Duration, s.Named("C").Conditions.Single().EscapeDc));
        var result = Step(s, new ConditionOp(["A"]) { Add = [incapacitating] });
        Assert.False(Has(result.Next, "C", "grappled"));
        Assert.True(result.Says(K.GrappleEnded, "grappled (A) ended on C"));
    }

    [Fact]
    public void UntilEscape_EndsWhenTheGrapplerDiesOrLeaves()
    {
        var s = Apply(Abc(), "C", "grappled", null, source: "A", dc: 13);
        Assert.False(Has(Step(s, new DamageOp(["A"]) { Amount = 99 }).Next, "C", "grappled"));
        Assert.False(Has(Step(s, new LeaveOp(["A"])).Next, "C", "grappled"));
        Assert.True(Has(Step(s, new DamageOp(["A"]) { Amount = 5 }).Next, "C", "grappled"));
    }

    [Fact]
    public void UntilStands_StaysThroughTurnsUntilRemoved()
    {
        var s = Apply(Abc(), "C", "prone", null);
        Assert.Equal(D.UntilStands, s.Named("C").Conditions.Single().Duration);
        (s, _) = NextUntil(s, "A");
        (s, _) = NextUntil(s, "A");
        Assert.True(Has(s, "C", "prone"));
    }

    [Fact]
    public void ZeroHp_TheUnconsciousOfADropEndsWhenHealed_ProneStays()
    {
        var s = Fight(E2024, ["P"], ("P", 20), ("M", 10));
        s = Step(s, new DamageOp(["P"]) { Amount = 31 }).Next;
        var unconscious = s.Named("P").Conditions.Single(c => c.Name == "unconscious");
        Assert.Equal(D.ZeroHp, unconscious.Duration);
        s = Step(s, new HealOp(["P"]) { Amount = 3 }).Next;
        Assert.False(Has(s, "P", "unconscious"));
        Assert.True(Has(s, "P", "prone"));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Dead and left anchors keep firing at their place

    [Fact]
    public void DeadAnchor_ItsRoundsEffectStillEndsAtItsPlace()
    {
        var s = Abc().Next(); // B's turn
        s = Apply(s, "C", "Bless", "2 rounds"); // expires at the start of B's turn in round 3
        s = Step(s, new DamageOp(["B"]) { Amount = 99 }).Next;
        Assert.True(s.Named("B").Dead);
        (s, _) = NextUntil(s, "A");
        (s, _) = NextUntil(s, "A"); // round 3, A
        Assert.True(Has(s, "C", "Bless"));
        var passed = Step(s, new NextOp()); // B's place passes, C's turn
        Assert.Equal("C", passed.Next.TurnHolder!.Name);
        Assert.False(Has(passed.Next, "C", "Bless"));
    }

    [Fact]
    public void DeadSource_ItsUntilEndOfTurnEffectEndsWhereItsTurnWouldEnd()
    {
        var s = Abc().Next(); // B's turn
        s = Apply(s, "C", "frightened", D.UntilEndOfSourceTurn); // source: B, the turn-holder; skip its own end
        s = Step(s, new DamageOp(["B"]) { Amount = 99 }).Next; // B dies on its own turn
        s = s.Next(); // B's turn ends: skipped end
        Assert.True(Has(s, "C", "frightened"));
        s = s.Next(); // C ends, round 2, A
        Assert.True(Has(s, "C", "frightened"));
        s = s.Next(); // A ends; B's place (dead) fires its end; C's turn
        Assert.Equal("C", s.TurnHolder!.Name);
        Assert.False(Has(s, "C", "frightened"));
    }

    [Fact]
    public void RoundOne_ADeadCombatantsPlaceBeforeTheFirstActor_FiresWhatItAnchors()
    {
        // Before initiative X frightens Y until the end of X's next turn, then dies; X still gets its place (20) ahead of
        // Y, and round 1's start passes it: the effect ends there (§6.2 "dead anchors keep firing at their place").
        var s = Add(Encounter(), new AddEntry { Name = "X", Hp = HpChoice.Of(5) }, new AddEntry { Name = "Y", Hp = HpChoice.Of(30) }).Next;
        s = Apply(s, "Y", "frightened", D.UntilEndOfSourceTurn, source: "X");
        s = Step(s, new DamageOp(["X"]) { Amount = 9 }).Next;
        Assert.True(s.Named("X").Dead);
        var begun = Step(s, new InitiativeOp { Rolls = [new("X", Total: 20), new("Y", Total: 10)] });
        Assert.Equal(("Y", 1), (begun.Next.TurnHolder!.Name, begun.Next.Round));
        Assert.False(Has(begun.Next, "Y", "frightened"));
        Assert.True(begun.Says(K.Expired, "frightened (X) ended on Y"));
    }

    [Fact]
    public void Leave_TheTurnHolder_ItsTurnEndStillFiresWhatItAnchors()
    {
        var s = Abc().Next(); // B's turn
        s = Apply(s, "C", "frightened", D.UntilEndOfSourceTurn, source: "A"); // not A's turn: no skipped end
        (s, _) = NextUntil(s, "A"); // round 2, A acting
        Assert.True(Has(s, "C", "frightened"));
        var left = Step(s, new LeaveOp(["A"]));
        Assert.Equal("B", left.Next.TurnHolder!.Name);
        Assert.False(Has(left.Next, "C", "frightened"));
        Assert.True(left.Says(K.Expired, "frightened (A) ended on C"));
    }

    [Fact]
    public void Expiring_NotSaidOnTheTurnItsEndIsSkipped_SaidOnTheTurnItEnds()
    {
        var applied = Step(Abc(), new ConditionOp(["C"]) { Add = ["frightened"], Duration = D.UntilEndOfSourceTurn, Source = "A" });
        Assert.True(applied.Next.Named("C").Conditions.Single().SkipEnd);
        Assert.Empty(applied.Of(K.Expiring));
        var (s, steps) = NextUntil(applied.Next, "A"); // round 2: A's next turn, the one that ends it
        Assert.All(steps.Take(steps.Count - 1), step => Assert.Empty(step.Of(K.Expiring)));
        Assert.True(steps[^1].Says(K.Expiring, "frightened (A) on C ends at the end of this turn"));
        Assert.Equal(2, s.Round);
    }

    [Fact]
    public void FightDurationWithDcAndAbility_PromptsASaveAtTheEndOfTheTargetsTurn()
    {
        var s = Apply(Abc(), "B", "frightened", null, source: "A", dc: 13, ability: "wis");
        var frightened = s.Named("B").Conditions.Single();
        Assert.Equal((D.Fight, "wis", 13), (frightened.Duration, frightened.Save!.Ability, frightened.Save.Dc));
        s = s.Next(); // B's turn
        var ended = Step(s, new NextOp());
        var prompt = Assert.Single(ended.Of(K.SaveEnds));
        Assert.Contains("Wis save DC 13", prompt.Text, StringComparison.Ordinal);
        Assert.True(Has(ended.Next, "B", "frightened"));
    }

    [Fact]
    public void LeftAnchor_ItsUntilStartOfTurnEffectEndsAtItsFormerPlace()
    {
        var s = Apply(Abc(), "C", "stunned", D.UntilStartOfSourceTurn, source: "B");
        s = Step(s, new LeaveOp(["B"])).Next;
        Assert.True(Has(s, "C", "stunned"));
        s = s.Next(); // A ends, B's place passes (left), C's turn
        Assert.Equal("C", s.TurnHolder!.Name);
        Assert.False(Has(s, "C", "stunned"));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Defaults and phrases

    [Theory]
    [InlineData("grappled", "A", D.UntilEscape)]
    [InlineData("restrained", "A", D.UntilEscape)]
    [InlineData("prone", null, D.UntilStands)]
    [InlineData("frightened", "A", D.Fight)]
    [InlineData("poisoned", null, D.Fight)]
    [InlineData("Bladesong", null, D.Fight)]
    public void DefaultDuration_PerContractTable(string name, string? source, string expected)
    {
        var s = Apply(Abc(), "C", name, null, source: source);
        Assert.Equal(expected, s.Named("C").Conditions.First(c => c.Name == name).Duration);
    }

    [Fact]
    public void DefaultDuration_RestrainedWithNoCombatantSource_LastsTheFight()
    {
        var s = Apply(Abc(), "C", "restrained", null, source: "a net");
        var restrained = s.Named("C").Conditions.Single();
        Assert.Equal((D.Fight, null, "a net"), (restrained.Duration, restrained.Source, restrained.SourceNote));
    }

    [Theory]
    [InlineData("until_start_of_source_turn", D.UntilStartOfSourceTurn, null, null)]
    [InlineData("Until End Of Target Turn", D.UntilEndOfTargetTurn, null, null)]
    [InlineData("until the end of its next turn", D.UntilEndOfTargetTurn, null, null)]
    [InlineData("until the end of your next turn", D.UntilEndOfSourceTurn, null, null)]
    [InlineData("until the end of the source's next turn", D.UntilEndOfSourceTurn, null, null)]
    [InlineData("until the start of your next turn", D.UntilStartOfSourceTurn, null, null)]
    [InlineData("until the start of its next turn", D.UntilStartOfTargetTurn, null, null)]
    [InlineData("save ends", D.SaveEnds, null, null)]
    [InlineData("concentration", D.Concentration, null, null)]
    [InlineData("until escape", D.UntilEscape, null, null)]
    [InlineData("end of round 2", D.EndOfRound, null, 2)]
    [InlineData("end of round", D.EndOfRound, null, null)]
    [InlineData("until removed", D.UntilRemoved, null, null)]
    [InlineData("fight", D.Fight, null, null)]
    [InlineData("1 minute", D.Rounds, 10, null)]
    [InlineData("10 minutes", D.Rounds, 100, null)]
    [InlineData("1 hour", D.Rounds, 600, null)]
    [InlineData("an hour", D.Rounds, 600, null)]
    [InlineData("3 rounds", D.Rounds, 3, null)]
    [InlineData("up to 1 minute", D.Rounds, 10, null)]
    public void Parse_ForgivingPhrases(string text, string kind, int? rounds, int? round)
    {
        Assert.Equal(new ParsedDuration(kind, rounds, round), CombatDurations.Parse(text));
    }

    [Theory]
    [InlineData("rounds")]
    [InlineData("0 rounds")]
    [InlineData("forever and a day")]
    [InlineData("end of round 0")]
    [InlineData("")]
    public void Parse_Unknown_IsRefusedListingTheNames(string text)
    {
        var ex = Assert.Throws<DndInputException>(() => CombatDurations.Parse(text));
        Assert.Contains("until_end_of_source_turn", ex.Message, StringComparison.Ordinal);
    }
}
