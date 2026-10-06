using DndMcp.Domain.Probability;
using DndMcp.Domain.Rules;
using DndMcp.Domain.Simulation;
using Xunit;
using static DndMcp.Tests.Rules.RulesKit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: the death-save wrapper is the simulator's <see cref="DeathSaves.Step"/> plus what the rule adds: the face
/// decides 1 and 20, a total decides the rest (the face in 2014, the face − 2 × Exhaustion in 2024, or the caller's
/// total), three successes reset both tallies, a 20 revives at 1 HP. Enumerated over every face it gives the engine's
/// exact Markov goldens, so the tracker's dying creature and the simulator's die at the same rate.
/// </summary>
public sealed class DeathSaveRulesTests
{
    private const double Death = 0.404875;
    private const double Stable = 0.41375;
    private const double Revive = 0.181375;

    [Theory]
    // edition, exhaustion, successes, failures, face, total, → successes, failures, success, failuresAdded, value
    [InlineData("2014", 0, 0, 0, 10, null, 1, 0, true, 0, 10)]
    [InlineData("2014", 0, 0, 0, 9, null, 0, 1, false, 1, 9)]
    [InlineData("2014", 3, 0, 0, 10, null, 1, 0, true, 0, 10)]
    [InlineData("2024", 0, 0, 0, 10, null, 1, 0, true, 0, 10)]
    [InlineData("2024", 1, 0, 0, 11, null, 0, 1, false, 1, 9)]
    [InlineData("2024", 1, 0, 0, 12, null, 1, 0, true, 0, 10)]
    [InlineData("2014", 0, 1, 0, 8, 12, 2, 0, true, 0, 12)]
    [InlineData("2014", 0, 0, 0, 15, 9, 0, 1, false, 1, 9)]
    [InlineData("2014", 0, 0, 0, null, 10, 1, 0, true, 0, 10)]
    [InlineData("2014", 0, 0, 0, 1, null, 0, 2, false, 2, 1)]
    [InlineData("2014", 0, 0, 0, 1, 15, 0, 2, false, 2, 15)]
    public void DeathSave_FaceDecidesOneAndTwentyTotalDecidesTheRest(
        string edition, int exhaustion, int successes, int failures, int? face, int? total, int s, int f, bool success, int added, int value)
    {
        var outcome = CombatRules.DeathSave(Dying(40, edition, successes, failures, exhaustion: exhaustion), face, total);

        Assert.Equal(new DeathSaveTally(s, f, false), outcome.After.DeathSaves);
        Assert.Equal(success, outcome.Success);
        Assert.Equal(added, outcome.FailuresAdded);
        Assert.Equal(value, outcome.Total);
        Assert.False(outcome.Died || outcome.Revived || outcome.Stabilized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5)]
    public void DeathSave_FixtureA25_NaturalTwentyRevivesAtOneHitPointWithTheTalliesReset(int? total)
    {
        var outcome = CombatRules.DeathSave(Dying(74, "2014", failures: 2), 20, total);

        Assert.True(outcome.Revived);
        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.After.Hp);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
        Assert.False(outcome.After.Dying);
        Assert.Equal(DeathSaveState.Revived, outcome.State);
    }

    [Fact]
    public void DeathSave_ATotalOfTwentyWithoutAFace_IsASuccessNotARevival()
    {
        var outcome = CombatRules.DeathSave(Dying(40), null, 25);

        Assert.False(outcome.Revived);
        Assert.Equal(new DeathSaveTally(1, 0, false), outcome.After.DeathSaves);
    }

    [Fact]
    public void DeathSave_ThirdSuccess_IsStableWithBothTalliesReset()
    {
        var outcome = CombatRules.DeathSave(Dying(40, successes: 2, failures: 2), 14);

        Assert.True(outcome.Stabilized);
        Assert.Equal(DeathSaveTally.Stabilized, outcome.After.DeathSaves);
        Assert.False(outcome.After.Dying);
        Assert.Equal(DeathSaveState.Stable, outcome.State);
        Assert.Equal(0, outcome.After.Hp);
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(1, 1)]
    public void DeathSave_ThirdFailure_IsDead(int failures, int face)
    {
        var outcome = CombatRules.DeathSave(Dying(40, failures: failures, temp: 4), face);

        Assert.True(outcome.Died);
        Assert.True(outcome.After.Dead);
        Assert.Equal(new DeathSaveTally(0, 3, false), outcome.After.DeathSaves);
        Assert.Equal(0, outcome.After.TempHp);
    }

    [Fact]
    public void DeathSave_ACreatureNotDying_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => CombatRules.DeathSave(State(5, 40), 10));
        Assert.Throws<ArgumentException>(() => CombatRules.DeathSave(State(0, 40, saves: DeathSaveTally.Stabilized), 10));
        Assert.Throws<ArgumentException>(() => CombatRules.DeathSave(State(0, 40, pc: false), 10));
        Assert.Throws<ArgumentException>(() => CombatRules.DeathSave(Dying(40), null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatRules.DeathSave(Dying(40), 21));
    }

    [Theory]
    [InlineData("2014", 0, D20Mode.Normal, "1d20")]
    [InlineData("2014", 2, D20Mode.Normal, "1d20")]
    [InlineData("2014", 3, D20Mode.Disadvantage, "2d20kl1")]
    [InlineData("2024", 0, D20Mode.Normal, "1d20")]
    [InlineData("2024", 2, D20Mode.Normal, "1d20-4")]
    public void DeathSaveRoll_ExhaustionPerEdition(string edition, int exhaustion, D20Mode mode, string expression)
    {
        var plan = CombatRules.DeathSaveRoll(Dying(40, edition, exhaustion: exhaustion));

        Assert.Equal(mode, plan.Mode);
        Assert.Equal(expression, plan.Expression);
    }

    [Fact]
    public void Stabilize_ADyingCreature_IsStableWithBothTalliesReset()
    {
        var outcome = CombatRules.Stabilize(Dying(40, successes: 1, failures: 2));

        Assert.True(outcome.Stabilized);
        Assert.False(outcome.EndedKnockOut);
        Assert.Equal(DeathSaveTally.Stabilized, outcome.After.DeathSaves);
        Assert.Equal(0, outcome.After.Hp);
        Assert.False(outcome.After.Dying);
    }

    [Fact]
    public void Stabilize_AConsciousStableOrDeadCreature_ChangesNothing()
    {
        var conscious = State(5, 40);
        var stable = State(0, 40, saves: DeathSaveTally.Stabilized);
        var dead = State(0, 40, dead: true, saves: new DeathSaveTally(0, 3, false));

        Assert.Same(conscious, CombatRules.Stabilize(conscious).After);
        Assert.False(CombatRules.Stabilize(stable).Stabilized);
        Assert.Equal(stable, CombatRules.Stabilize(stable).After);
        Assert.Same(dead, CombatRules.Stabilize(dead).After);
        Assert.False(CombatRules.Stabilize(dead).Stabilized || CombatRules.Stabilize(conscious).Stabilized);
    }

    [Fact]
    public void Stabilize_A2024KnockOut_IsFirstAidAndEndsIt()
    {
        // SRD 5.2.1 Knocking Out a Creature: it "remains Unconscious until it regains any Hit Points or until someone uses
        // an action to administer first aid to it".
        var knockedOut = CombatRules.Damage(State(5, 30), Hit(10) with { KnockOut = true }).After;
        var outcome = CombatRules.Stabilize(knockedOut);

        Assert.True(knockedOut.KnockedOut);
        Assert.True(outcome.EndedKnockOut);
        Assert.False(outcome.Stabilized);
        Assert.False(outcome.After.KnockedOut);
        Assert.Equal(1, outcome.After.Hp);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
    }

    [Fact]
    public void Stabilize_A2014KnockOut_IsAlreadyStable()
    {
        var knockedOut = CombatRules.Damage(State(5, 30, "2014"), Hit(10) with { KnockOut = true }).After;
        var outcome = CombatRules.Stabilize(knockedOut);

        Assert.False(outcome.Stabilized || outcome.EndedKnockOut);
        Assert.Equal(DeathSaveTally.Stabilized, outcome.After.DeathSaves);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Agreement with the simulator.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void DeathSave_EveryFaceFromEveryTally_AgreesWithTheEngineStep()
    {
        foreach (var edition in new[] { "2014", "2024" })
        {
            for (var s = 0; s <= 2; s++)
            {
                for (var f = 0; f <= 2; f++)
                {
                    for (var face = 1; face <= 20; face++)
                    {
                        var (es, ef, state) = DeathSaves.Step(s, f, face);
                        var outcome = CombatRules.DeathSave(Dying(40, edition, s, f), face);
                        Assert.Equal(state, outcome.State);
                        var expected = state switch
                        {
                            DeathSaveState.Dying => new DeathSaveTally(es, ef, false),
                            DeathSaveState.Stable => DeathSaveTally.Stabilized,
                            DeathSaveState.Dead => new DeathSaveTally(0, 3, false),
                            _ => DeathSaveTally.Zero,
                        };
                        Assert.Equal(expected, outcome.After.DeathSaves);
                    }
                }
            }
        }
    }

    [Fact]
    public void DeathSave_EnumeratedFromZero_GivesTheEngineGoldens()
    {
        var death = 0.0;
        var stable = 0.0;
        var revive = 0.0;
        var states = new Dictionary<DeathSaveTally, double> { [DeathSaveTally.Zero] = 1.0 };
        while (states.Count > 0)
        {
            var next = new Dictionary<DeathSaveTally, double>();
            foreach (var (tally, p) in states)
            {
                for (var face = 1; face <= 20; face++)
                {
                    var outcome = CombatRules.DeathSave(Dying(40, "2024", tally.Successes, tally.Failures), face);
                    if (outcome.Died)
                    {
                        death += p / 20;
                    }
                    else if (outcome.Stabilized)
                    {
                        stable += p / 20;
                    }
                    else if (outcome.Revived)
                    {
                        revive += p / 20;
                    }
                    else
                    {
                        next[outcome.After.DeathSaves] = next.GetValueOrDefault(outcome.After.DeathSaves) + p / 20;
                    }
                }
            }

            states = next;
        }

        Assert.Equal(Death, death, 12);
        Assert.Equal(Stable, stable, 12);
        Assert.Equal(Revive, revive, 12);
    }
}
