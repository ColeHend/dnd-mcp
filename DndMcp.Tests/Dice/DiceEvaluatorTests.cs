using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: a roll's total is exactly what its recorded faces say — the kept dice summed (or their successes
/// counted), constants and operators applied — and every face the roller produced is recorded, including the ones
/// that do not count, so the user can audit any result.
///
/// <para>
/// Faces are scripted, so every assertion is exact. What the scripted rolls pin that the distribution tests cannot:
/// which die is marked dropped, rerolled or exploded (the display depends on it), the order dice are requested in,
/// the reroll and explosion caps, and the per-call roll budget.
/// </para>
/// </summary>
public sealed class DiceEvaluatorTests
{
    [Theory]
    [InlineData("2d6+3", 15)]
    [InlineData("1d20-1", 19)]
    [InlineData("-1d4", -4)]
    [InlineData("1d8-1d4", 4)]
    [InlineData("d20", 20)]
    [InlineData("2d6+1d4+3", 19)]
    [InlineData("5", 5)]
    [InlineData("(1d6+2)*3", 24)]
    [InlineData("(1d6+1)/2", 3)]
    [InlineData("4d6kh3", 18)]
    [InlineData("adv+5", 25)]
    [InlineData("1d20min10", 20)]
    [InlineData("1d20max15", 15)]
    public void Roll_EveryDieHighestWithoutExplosions_TotalIsMaximum(string expression, long expected)
    {
        Assert.Equal(expected, Roll(expression, ScriptedDiceRoller.Highest()).Total);
    }

    [Theory]
    [InlineData("2d6+3", 5)]
    [InlineData("1d20-1", 0)]
    [InlineData("-1d4", -1)]
    [InlineData("1d8-1d4", 0)]
    [InlineData("(1d4-3)/2", -1)]
    [InlineData("1d20min10", 10)]
    [InlineData("4d6kh3", 3)]
    public void Roll_EveryDieLowest_TotalIsMinimum(string expression, long expected)
    {
        Assert.Equal(expected, Roll(expression, ScriptedDiceRoller.Lowest()).Total);
    }

    [Fact]
    public void Roll_KeepHighest_DropsTheLowestAndRecordsIt()
    {
        var roll = Roll("4d6kh3", ScriptedDiceRoller.Sequence(3, 6, 1, 5));

        var group = Assert.Single(roll.Groups);
        Assert.Equal(14, roll.Total);
        Assert.Equal([3L, 6, 1, 5], group.Dice.Select(d => d.Value));
        Assert.Equal([false, false, true, false], group.Dice.Select(d => d.Dropped));
    }

    [Fact]
    public void Roll_KeepHighestWithTie_DropsTheLaterOfEqualDice()
    {
        var roll = Roll("3d6dl1", ScriptedDiceRoller.Sequence(2, 5, 2));

        Assert.Equal(7, roll.Total);
        Assert.Equal([false, false, true], roll.Groups[0].Dice.Select(d => d.Dropped));
    }

    [Fact]
    public void Roll_Disadvantage_KeepsLowerD20()
    {
        var roll = Roll("dis+2", ScriptedDiceRoller.Sequence(17, 4));

        Assert.Equal(6, roll.Total);
        Assert.Equal([true, false], roll.Groups[0].Dice.Select(d => d.Dropped));
    }

    [Fact]
    public void Roll_RerollUntilNoMatch_RecordsEveryRerolledFaceThenTheOneThatCounts()
    {
        var roll = Roll("2d20r1", ScriptedDiceRoller.Sequence(1, 1, 7, 12));

        Assert.Equal(19, roll.Total);
        var first = roll.Groups[0].Dice[0];
        Assert.Equal([(1, true), (1, true), (7, false)], first.Faces.Select(f => (f.Face, f.Rerolled)));
        Assert.Equal(7, first.Value);
    }

    [Fact]
    public void Roll_RerollOnce_KeepsTheSecondFaceEvenIfItMatches()
    {
        var roll = Roll("2d6ro<=2", ScriptedDiceRoller.Sequence(1, 2, 5));

        Assert.Equal(7, roll.Total);
        Assert.Equal([(1, true), (2, false)], roll.Groups[0].Dice[0].Faces.Select(f => (f.Face, f.Rerolled)));
    }

    [Fact]
    public void Roll_RerollCapReached_DrawsDirectlyFromNonMatchingFaces()
    {
        // Two physical rerolls, then one draw among the 5 non-matching faces {2..6}: pick 3 → face 4.
        var roller = ScriptedDiceRoller.Sequence(1, 1, 1, 3);

        var roll = Roll("1d6r1", roller, new DiceCaps(MaxExplosionsPerDie: 100, MaxRerollsPerDie: 2));

        Assert.Equal(4, roll.Total);
        Assert.Equal([6, 6, 6, 5], roller.RequestedSides);
        Assert.Equal(3, roll.Groups[0].Dice[0].Faces.Count(f => f.Rerolled));
    }

    [Fact]
    public void Roll_Explode_AddsSeparateDiceMarkedExploded()
    {
        var roll = Roll("1d6!+1", ScriptedDiceRoller.Sequence(6, 6, 2));

        var dice = roll.Groups[0].Dice;
        Assert.Equal(15, roll.Total);
        Assert.Equal([6L, 6, 2], dice.Select(d => d.Value));
        Assert.Equal([true, true, false], dice.Select(d => d.Exploded));
    }

    [Fact]
    public void Roll_ExplodeOnThreshold_ExplodesOnEveryMatchingFace()
    {
        var roll = Roll("1d6!>=5", ScriptedDiceRoller.Sequence(5, 6, 1));

        Assert.Equal(12, roll.Total);
        Assert.Equal(3, roll.Groups[0].Dice.Count);
    }

    [Fact]
    public void Roll_CompoundExplode_IsOneDieWithAllFaces()
    {
        var roll = Roll("2d6!!", ScriptedDiceRoller.Sequence(6, 6, 2, 3));

        var dice = roll.Groups[0].Dice;
        Assert.Equal(17, roll.Total);
        Assert.Equal(2, dice.Count);
        Assert.Equal(14, dice[0].Value);
        Assert.Equal([(6, true), (6, true), (2, false)], dice[0].Faces.Select(f => (f.Face, f.Exploded)));
    }

    [Fact]
    public void Roll_CompoundWithMax_ClampsTheCompoundedTotalNotEachFace()
    {
        var roll = Roll("1d6!!max10", ScriptedDiceRoller.Sequence(6, 6, 2));

        Assert.Equal(10, roll.Total);
        Assert.Equal(14, roll.Groups[0].Dice[0].Raw);
    }

    [Fact]
    public void Roll_Penetrating_EveryAddedDieCountsOneLess()
    {
        var roll = Roll("1d6!p", ScriptedDiceRoller.Sequence(6, 6, 2));

        var dice = roll.Groups[0].Dice;
        Assert.Equal(6 + 5 + 1, roll.Total);
        Assert.Equal([false, true, true], dice.Select(d => d.Penetrated));
    }

    [Fact]
    public void Roll_ExplosionCap_LastDieDoesNotExplode()
    {
        var roll = Roll("1d6!", ScriptedDiceRoller.Highest(), new DiceCaps(MaxExplosionsPerDie: 2, MaxRerollsPerDie: 100));

        Assert.Equal(18, roll.Total);
        Assert.Equal([true, true, false], roll.Groups[0].Dice.Select(d => d.Exploded));
    }

    [Fact]
    public void Roll_DefaultExplosionCap_StopsAt101Dice()
    {
        var roll = Roll("1d6!", ScriptedDiceRoller.Highest());

        Assert.Equal(DiceLimits.MaxExplosionsPerDie + 1, roll.Groups[0].Dice.Count);
        Assert.Equal(6 * (DiceLimits.MaxExplosionsPerDie + 1), roll.Total);
    }

    [Fact]
    public void Roll_MinClamp_AppliesToEachDie()
    {
        var roll = Roll("2d20min10", ScriptedDiceRoller.Sequence(3, 15));

        Assert.Equal(25, roll.Total);
        Assert.Equal([(3L, 10L), (15L, 15L)], roll.Groups[0].Dice.Select(d => (d.Raw, d.Value)));
    }

    [Fact]
    public void Roll_SuccessCounting_CountsSuccessesMinusFailures()
    {
        var roll = Roll("5d10cs>=8cf=1", ScriptedDiceRoller.Sequence(8, 1, 10, 3, 7));

        Assert.Equal(1, roll.Total);
        Assert.Equal([1, -1, 1, 0, 0], roll.Groups[0].Dice.Select(d => d.Score));
    }

    [Fact]
    public void Roll_KeepThenCount_CountsOnlyKeptDice()
    {
        var roll = Roll("4d6kh2cs>=5", ScriptedDiceRoller.Sequence(5, 6, 5, 1));

        // Kept: 6 and the first 5; the second 5 is dropped even though it would have been a success.
        Assert.Equal(2, roll.Total);
        Assert.Equal([false, false, true, true], roll.Groups[0].Dice.Select(d => d.Dropped));
    }

    [Fact]
    public void Roll_ExplodingPoolKeepHighest_KeepsThreeOfTheWholeExplodedPool()
    {
        // Pool 6!,2 | 6!,1 | 3: five dice, and kh3 keeps 6, 6, 3 — not "all three dice typed".
        var roll = Roll("3d6!kh3", ScriptedDiceRoller.Sequence(6, 2, 6, 1, 3));

        Assert.Equal(15, roll.Total);
        Assert.Equal([false, true, false, true, false], roll.Groups[0].Dice.Select(d => d.Dropped));
    }

    [Fact]
    public void Roll_ExplodingPoolDropLowest_DropsOnlyOneDieOfTheExplodedPool()
    {
        // Pool 6!,2 | 6!,1 | 3 | 1: six dice; dl1 drops one 1 (the later), not "keep 3 of 4 typed".
        var roll = Roll("4d6!dl1", ScriptedDiceRoller.Sequence(6, 2, 6, 1, 3, 1));

        Assert.Equal(18, roll.Total);
        Assert.Equal(1, roll.Groups[0].Dice.Count(d => d.Dropped));
    }

    [Fact]
    public void Roll_PenetratingPoolDropHighest_DropsOnlyTheHighestDie()
    {
        // Pool 6!, 5!(6-1), 5!(6-1), 2(3-1) | 2: dh1 drops the 6.
        var roll = Roll("2d6!pdh1", ScriptedDiceRoller.Sequence(6, 6, 6, 3, 2));

        Assert.Equal(14, roll.Total);
        Assert.Equal([true, false, false, false, false], roll.Groups[0].Dice.Select(d => d.Dropped));
    }

    [Fact]
    public void Roll_ExplodingPoolKeepMoreThanTyped_KeepsUpToThatMany()
    {
        // 2d6!kh3: without explosions both dice count; pool 6!, 6!, 1 | 4 keeps 6, 6, 4.
        Assert.Equal(7, Roll("2d6!kh3", ScriptedDiceRoller.Sequence(3, 4)).Total);
        Assert.Equal(16, Roll("2d6!kh3", ScriptedDiceRoller.Sequence(6, 6, 1, 4)).Total);
    }

    [Fact]
    public void Roll_TrailingComparison_ReportsWhetherTheTotalMeetsIt()
    {
        Assert.True(Roll("1d20+5>=15", ScriptedDiceRoller.Sequence(10)).ComparisonMet);
        Assert.False(Roll("1d20+5>=15", ScriptedDiceRoller.Sequence(9)).ComparisonMet);
        Assert.Null(Roll("1d20+5", ScriptedDiceRoller.Sequence(9)).ComparisonMet);
    }

    [Fact]
    public void Roll_MixedExpression_RequestsDiceLeftToRightAndGroupsInOrder()
    {
        var roller = ScriptedDiceRoller.Highest();

        var roll = Roll("2d6+1d4-2+adv", roller);

        Assert.Equal([6, 6, 4, 20, 20], roller.RequestedSides);
        Assert.Equal(["2d6", "1d4", "adv"], roll.Groups.Select(g => g.Group.Text));
        Assert.Equal([12L, 4, 20], roll.Groups.Select(g => g.Value));
    }

    [Fact]
    public void Roll_ConstantsOnly_RollsNoDice()
    {
        var roller = ScriptedDiceRoller.Highest();

        var roll = Roll("5-2", roller);

        Assert.Empty(roller.RequestedSides);
        Assert.Empty(roll.Groups);
        Assert.Equal(3, roll.Total);
    }

    [Fact]
    public void Roll_BudgetExhausted_ThrowsInputErrorNamingTheLimit()
    {
        var budget = new DiceRollBudget(maxPhysicalRolls: 5);

        var ex = Assert.Throws<DndInputException>(() =>
            DiceEvaluator.Roll(DiceExpression.Parse("10d6"), ScriptedDiceRoller.Highest(), budget));

        Assert.Contains("more than 5 individual dice", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Roll_BudgetExactlySpent_Succeeds()
    {
        // The budget is the most rolls allowed, not the first refused.
        var budget = new DiceRollBudget(maxPhysicalRolls: 5);

        Assert.Equal(30, DiceEvaluator.Roll(DiceExpression.Parse("5d6"), ScriptedDiceRoller.Highest(), budget).Total);
    }

    [Fact]
    public void Roll_ClampThenCount_CountsTheClampedValue()
    {
        // Per die: clamp, then count. A 3 raised to 10 by min10 is a success for cs>=10.
        var roll = Roll("2d20min10cs>=10", ScriptedDiceRoller.Sequence(3, 15));

        Assert.Equal(2, roll.Total);
    }

    [Fact]
    public void Roll_BudgetSharedAcrossRolls_CountsEveryPhysicalRoll()
    {
        var budget = new DiceRollBudget();
        var expression = DiceExpression.Parse("2d6r1");

        DiceEvaluator.Roll(expression, ScriptedDiceRoller.Sequence(1, 4, 5), budget);
        DiceEvaluator.Roll(expression, ScriptedDiceRoller.Sequence(2, 3), budget);

        Assert.Equal(5, budget.PhysicalRolls);
    }

    [Fact]
    public void Roll_RollerReturnsFaceOutOfRange_ThrowsAsABugNotAnInputError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Roll("1d6", new ScriptedDiceRoller(_ => 7)));

        Assert.Contains("returned 7 for a d6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Roll_SeededRoller_SameSeedGivesSameRoll()
    {
        var expression = DiceExpression.Parse("10d20!kh5+3d6ro1");

        var first = DiceEvaluator.Roll(expression, new SeededDiceRoller(42));
        var second = DiceEvaluator.Roll(expression, new SeededDiceRoller(42));

        Assert.Equal(first.Total, second.Total);
        Assert.Equal(
            first.Groups.SelectMany(g => g.Dice).SelectMany(d => d.Faces),
            second.Groups.SelectMany(g => g.Dice).SelectMany(d => d.Faces));
    }

    private static DiceRoll Roll(string expression, IDiceRoller roller, DiceCaps? caps = null) =>
        DiceEvaluator.Roll(DiceExpression.Parse(expression), roller, caps: caps);
}
