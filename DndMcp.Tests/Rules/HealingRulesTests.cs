using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Rules.RulesKit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: healing is capped at the EFFECTIVE maximum, refused on the dead, wakes a creature at 0 HP (or a 2024
/// knock-out) with its tallies reset and Prone left to the caller; temporary hit points never stack (the higher is kept
/// unless the grant replaces) and never wake anyone; a changed maximum (2014 Exhaustion 4, a reduction) costs the hit
/// points above it, and Exhaustion 6 or a maximum of 0 kills.
/// </summary>
public sealed class HealingRulesTests
{
    [Fact]
    public void Heal_14Of20Plus8_IsCappedAt20()
    {
        // SRD 5.2 "Healing": 14 of 20 regaining 8 regains 6.
        var outcome = CombatRules.Heal(State(14, 20), 8);

        Assert.Equal(20, outcome.After.Hp);
        Assert.Equal(6, outcome.Regained);
        Assert.False(outcome.Woke);
    }

    [Fact]
    public void Heal_TheDead_IsRefusedAndChangesNothing()
    {
        var dead = State(0, 20, dead: true);
        var outcome = CombatRules.Heal(dead, 10);

        Assert.True(outcome.Refused);
        Assert.Same(dead, outcome.After);
    }

    [Fact]
    public void Heal_FromZeroWithAFailure_WakesWithTheTalliesReset()
    {
        var outcome = CombatRules.Heal(Dying(44, successes: 1, failures: 1), 7);

        Assert.Equal(7, outcome.After.Hp);
        Assert.True(outcome.Woke);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
        Assert.False(outcome.After.Dying);
    }

    [Fact]
    public void Heal_FixtureB22_PotionOnTheDyingMonk_SevenHitPointsAndConscious()
    {
        var outcome = CombatRules.Heal(Dying(59, failures: 2), 3 + 2 + 2);

        Assert.Equal(7, outcome.After.Hp);
        Assert.True(outcome.Woke);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
    }

    [Fact]
    public void Heal_A2014KnockOutAtZero_Wakes()
    {
        var outcome = CombatRules.Heal(State(0, 30, "2014", saves: DeathSaveTally.Stabilized), 4);

        Assert.True(outcome.Woke);
        Assert.Equal(DeathSaveTally.Zero, outcome.After.DeathSaves);
        Assert.False(outcome.EndedKnockOut);
    }

    [Theory]
    [InlineData(30, 5, 6)]
    [InlineData(2, 3, 2)]
    public void Heal_A2024KnockOut_EndsOnAnyHealing(int max, int amount, int hp)
    {
        var outcome = CombatRules.Heal(State(1, max, knockedOut: true), amount);

        Assert.Equal(hp, outcome.After.Hp);
        Assert.True(outcome.Woke);
        Assert.True(outcome.EndedKnockOut);
        Assert.False(outcome.After.KnockedOut);
    }

    /// <summary>
    /// F2R04: a knock-out "remains Unconscious until it regains any Hit Points" (SRD 5.2.1, "Knocking Out a Creature"): a
    /// heal of a creature already at its maximum regains none, so it wakes no one (the knock-out of a 1-HP creature, or one
    /// a sheet stored at full hit points); first aid still ends it.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 3)]
    [InlineData(30, 30, 5)]
    public void Heal_A2024KnockOutRegainingNothing_StaysKnockedOut(int hp, int max, int amount)
    {
        var outcome = CombatRules.Heal(State(hp, max, knockedOut: true), amount);

        Assert.Equal((0, false, false, true), (outcome.Regained, outcome.Woke, outcome.EndedKnockOut, outcome.After.KnockedOut));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Heal_NothingRegained_WakesNoOne(int amount)
    {
        var dying = Dying(30, failures: 1);
        var outcome = CombatRules.Heal(dying, amount);

        Assert.Same(dying, outcome.After);
        Assert.False(outcome.Woke);
        Assert.False(outcome.Refused);
    }

    [Fact]
    public void Heal_IsCappedAtTheReducedMaximum_AndNeverLowersHitPoints()
    {
        Assert.Equal(15, CombatRules.Heal(State(10, 20, reduction: 5), 20).After.Hp);
        Assert.Equal(100, CombatRules.Heal(State(100, 110, "2014", exhaustion: 4), 5).After.Hp);
    }

    [Fact]
    public void Heal_NeverRestoresTemporaryHitPoints()
    {
        var outcome = CombatRules.Heal(State(10, 20, temp: 3), 5);

        Assert.Equal(3, outcome.After.TempHp);
        Assert.Equal(15, outcome.After.Hp);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Temporary hit points.
    // ------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(8, 5, false, 8, true)]
    [InlineData(8, 10, false, 10, true)]
    [InlineData(0, 10, false, 10, false)]
    [InlineData(8, 3, true, 3, false)]
    public void GrantTempHp_KeepsTheHigherUnlessReplacing(int current, int gained, bool replace, int expected, bool ruling)
    {
        var outcome = CombatRules.GrantTempHp(State(20, 20, temp: current), gained, replace);

        Assert.Equal(expected, outcome.After.TempHp);
        Assert.Equal(expected, CombatRules.GrantTempHp(current, gained, replace));
        Assert.Equal(ruling ? [RulingFlags.TempHpKeepHigher] : [], outcome.Rulings);
    }

    [Fact]
    public void GrantTempHp_AtZeroHitPoints_NeitherWakesNorStabilises()
    {
        var dying = Dying(30, failures: 1);
        var outcome = CombatRules.GrantTempHp(dying, 10);

        Assert.Equal(10, outcome.After.TempHp);
        Assert.Equal(0, outcome.After.Hp);
        Assert.True(outcome.After.Dying);
        Assert.Equal(dying.DeathSaves, outcome.After.DeathSaves);
    }

    [Fact]
    public void GrantTempHp_TheDead_IsRefused()
    {
        Assert.True(CombatRules.GrantTempHp(State(0, 20, dead: true), 5).Refused);
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatRules.GrantTempHp(State(5, 20), -1));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // A changed maximum.
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ApplyMaximum_2014ExhaustionFour_HalvesTheMaximumAndCostsTheHitPointsAboveIt()
    {
        var outcome = CombatRules.ApplyMaximum(State(100, 110, "2014", exhaustion: 4));

        Assert.Equal(55, outcome.After.Hp);
        Assert.Equal(45, outcome.HpLost);
        Assert.False(outcome.Died);
    }

    [Fact]
    public void ApplyMaximum_2024ExhaustionFour_LeavesTheMaximum()
    {
        var outcome = CombatRules.ApplyMaximum(State(100, 110, exhaustion: 4));

        Assert.Equal(100, outcome.After.Hp);
        Assert.Equal(0, outcome.HpLost);
    }

    [Fact]
    public void ApplyMaximum_AReduction_LowersHitPointsToTheNewMaximum()
    {
        Assert.Equal(12, CombatRules.ApplyMaximum(State(20, 20, reduction: 8)).After.Hp);
    }

    [Theory]
    [InlineData("2014", 6, 0, DeathCauses.Exhaustion)]
    [InlineData("2024", 6, 0, DeathCauses.Exhaustion)]
    [InlineData("2024", 0, 20, DeathCauses.MaxHpZero)]
    [InlineData("2014", 4, 30, DeathCauses.MaxHpZero)]
    public void ApplyMaximum_ExhaustionSixOrAMaximumOfZero_Kills(string edition, int exhaustion, int reduction, string cause)
    {
        var outcome = CombatRules.ApplyMaximum(State(10, 20, edition, temp: 4, exhaustion: exhaustion, reduction: reduction, concentrating: true));

        Assert.True(outcome.Died);
        Assert.Equal(cause, outcome.DeathCause);
        Assert.True(outcome.After.Dead);
        Assert.Equal(0, outcome.After.TempHp);
        Assert.False(outcome.After.Concentrating);
        Assert.Equal(new DeathSaveTally(0, 3, false), outcome.After.DeathSaves);
    }

    [Theory]
    [InlineData("2014", 6)]
    [InlineData("2024", 6)]
    [InlineData("2024", 0)]
    public void ApplyMaximum_TheDead_ChangesNothingAndDoNotDieAgain(string edition, int exhaustion)
    {
        var dead = State(0, 20, edition, exhaustion: exhaustion, reduction: 20, dead: true, saves: new DeathSaveTally(0, 3, false));
        var outcome = CombatRules.ApplyMaximum(dead);

        Assert.False(outcome.Died);
        Assert.Null(outcome.DeathCause);
        Assert.Equal(0, outcome.HpLost);
        Assert.Same(dead, outcome.After);
    }

    [Theory]
    [InlineData(0, 3, 0, 20, true)]
    [InlineData(0, 2, 0, 20, false)]
    [InlineData(0, 0, 6, 20, true)]
    [InlineData(0, 0, 5, 20, false)]
    [InlineData(0, 0, 0, 0, true)]
    public void IsDead_ThreeFailuresExhaustionSixOrNoMaximum(int successes, int failures, int exhaustion, int effectiveMax, bool dead)
    {
        Assert.Equal(dead, CombatRules.IsDead(new DeathSaveTally(successes, failures, false), exhaustion, effectiveMax));
    }

    [Fact]
    public void HitPointState_EffectiveMaximum_IsHitPointMath()
    {
        var state = State(10, 111, "2014", exhaustion: 4, reduction: 10);

        Assert.Equal(HitPointMath.EffectiveMaxHp(111, 10, 4, "2014"), state.EffectiveMaxHp);
        Assert.Equal(50, state.EffectiveMaxHp);
    }
}
