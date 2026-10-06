using DndMcp.Domain.Probability;
using DndMcp.Domain.Rules;
using Xunit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: initiative per edition — the roll's mode (2024: surprised, Incapacitated or anything that includes it, or
/// Poisoned → Disadvantage, Invisible → Advantage; 2014: Exhaustion ≥ 1 or Poisoned → Disadvantage; Frightened only a
/// reminder; Advantage and Disadvantage cancel), a given face is the kept die (bonus and 2024's −2 × Exhaustion only), a
/// given total is taken as is, an init group takes one value, and the order is total → bonus → insertion with ties
/// between different combatants flagged (contract D14), never a roll-off.
/// </summary>
public sealed class InitiativeRulesTests
{
    [Theory]
    // edition, surprised, conditions, exhaustion → mode
    [InlineData("2024", false, "", 0, D20Mode.Normal)]
    [InlineData("2024", true, "", 0, D20Mode.Disadvantage)]
    [InlineData("2024", false, "stunned", 0, D20Mode.Disadvantage)]
    [InlineData("2024", false, "incapacitated", 0, D20Mode.Disadvantage)]
    [InlineData("2024", false, "unconscious", 0, D20Mode.Disadvantage)]
    [InlineData("2024", false, "poisoned", 0, D20Mode.Disadvantage)]
    [InlineData("2024", false, "invisible", 0, D20Mode.Advantage)]
    [InlineData("2024", true, "invisible", 0, D20Mode.Normal)]
    [InlineData("2024", false, "frightened", 0, D20Mode.Normal)]
    [InlineData("2024", false, "", 3, D20Mode.Normal)]
    [InlineData("2014", true, "", 0, D20Mode.Normal)]
    [InlineData("2014", false, "", 1, D20Mode.Disadvantage)]
    [InlineData("2014", false, "poisoned", 0, D20Mode.Disadvantage)]
    [InlineData("2014", false, "invisible", 0, D20Mode.Normal)]
    [InlineData("2014", false, "stunned", 0, D20Mode.Normal)]
    public void InitiativeRoll_ModePerEdition(string edition, bool surprised, string conditions, int exhaustion, D20Mode mode)
    {
        var plan = CombatRules.InitiativeRoll(2, edition, surprised, conditions.Split(',', StringSplitOptions.RemoveEmptyEntries), exhaustion);

        Assert.Equal(mode, plan.Roll.Mode);
    }

    [Fact]
    public void InitiativeRoll_SaysWhyAndRemindsOfFrightened()
    {
        var plan = CombatRules.InitiativeRoll(3, "2024", true, ["invisible", "poisoned", "frightened", "Bladesong"], 1);

        Assert.Equal(["invisible"], plan.Advantage);
        Assert.Equal([InitiativeReasons.Surprised, "poisoned"], plan.Disadvantage);
        Assert.Contains("frightened", Assert.Single(plan.Reminders), StringComparison.Ordinal);
        Assert.Equal("1d20+1", plan.Roll.Expression);
        Assert.Equal(["exhaustion 2"], CombatRules.InitiativeRoll(3, "2014", false, null, 2).Disadvantage);
    }

    [Theory]
    [InlineData(2, "2024", 1, "1d20")]
    [InlineData(5, "2014", 0, "1d20+5")]
    [InlineData(-1, "2014", 1, "2d20kl1-1")]
    public void InitiativeRoll_ExpressionCarriesTheModifier(int bonus, string edition, int exhaustion, string expression)
    {
        Assert.Equal(expression, CombatRules.InitiativeRoll(bonus, edition, false, null, exhaustion).Roll.Expression);
    }

    [Theory]
    // face, total, bonus, exhaustion, edition → total
    [InlineData(8, null, 2, 1, "2024", 8.0)]
    [InlineData(17, null, 5, 0, "2014", 22.0)]
    [InlineData(18, null, 0, 0, "2014", 18.0)]
    [InlineData(10, null, -1, 0, "2014", 9.0)]
    [InlineData(10, null, -1, 2, "2014", 9.0)]
    [InlineData(null, 25.0, 3, 0, "2014", 25.0)]
    [InlineData(null, 14.5, 0, 2, "2024", 14.5)]
    public void InitiativeTotal_GivenFaceIsTheKeptDieGivenTotalIsAsIs(int? face, double? total, int bonus, int exhaustion, string edition, double expected)
    {
        Assert.Equal(expected, CombatRules.InitiativeTotal(new InitiativeValue(face, total), bonus, exhaustion, edition));
    }

    [Fact]
    public void InitiativeTotal_BadValue_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => CombatRules.InitiativeTotal(new InitiativeValue(), 0, 0, "2024"));
        Assert.Throws<ArgumentException>(() => CombatRules.InitiativeTotal(new InitiativeValue(10, 12), 0, 0, "2024"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatRules.InitiativeTotal(new InitiativeValue(21), 0, 0, "2024"));
        Assert.Throws<ArgumentException>(() => CombatRules.InitiativeTotal(new InitiativeValue(Total: double.NaN), 0, 0, "2024"));
        Assert.Throws<ArgumentException>(() => CombatRules.InitiativeTotal(new InitiativeValue(Total: double.PositiveInfinity), 0, 0, "2024"));
    }

    [Fact]
    public void GroupValue_OneValueForAnyMemberAppliesToTheGroup()
    {
        Assert.Equal(new InitiativeGroupValue(new InitiativeValue(10), false), CombatRules.GroupValue([null, new InitiativeValue(10)]));
        Assert.Equal(new InitiativeGroupValue(new InitiativeValue(10), false), CombatRules.GroupValue([new InitiativeValue(10), new InitiativeValue(10)]));
        Assert.Equal(new InitiativeGroupValue(null, false), CombatRules.GroupValue([null, null]));
        Assert.True(CombatRules.GroupValue([new InitiativeValue(10), new InitiativeValue(Total: 9)]).Conflict);
        Assert.True(CombatRules.GroupValue([new InitiativeValue(10), new InitiativeValue(11)]).Conflict);
    }

    [Fact]
    public void OrderInitiative_FixtureA3_TheMummiesActTogetherAndNothingIsTied()
    {
        var order = CombatRules.OrderInitiative(
        [
            new InitiativeEntry("aiden", 7, 0, 1),
            new InitiativeEntry("belmakor", 22, 5, 2),
            new InitiativeEntry("ignis", 14, 2, 3),
            new InitiativeEntry("torch", 12, 1, 4),
            new InitiativeEntry("serif", 16, 1, 5),
            new InitiativeEntry("vars", 25, 4, 6),
            new InitiativeEntry("mummy-lord", 18, 0, 7),
            new InitiativeEntry("mummy", 9, -1, 8, "g1"),
            new InitiativeEntry("mummy-2", 9, -1, 9, "g1"),
        ]);

        Assert.Equal(["vars", "belmakor", "mummy-lord", "serif", "ignis", "torch", "mummy", "mummy-2", "aiden"], order.Order.Select(e => e.Id));
        Assert.Empty(order.Ties);
        Assert.Empty(order.Rulings);
    }

    [Fact]
    public void OrderInitiative_EqualTotals_OrderByBonusThenInsertionAndAreFlagged()
    {
        var order = CombatRules.OrderInitiative(
        [
            new InitiativeEntry("torch", 14, 2, 4),
            new InitiativeEntry("ignis", 14, 2, 3),
            new InitiativeEntry("serif", 14, 3, 5),
            new InitiativeEntry("vars", 14.5, 0, 6),
        ]);

        Assert.Equal(["vars", "serif", "ignis", "torch"], order.Order.Select(e => e.Id));
        var tie = Assert.Single(order.Ties);
        Assert.Equal(14, tie.Total);
        Assert.Equal(["serif", "ignis", "torch"], tie.Ids);
        Assert.Equal([RulingFlags.InitiativeTiesByBonusThenOrder], order.Rulings);
    }

    [Fact]
    public void OrderInitiative_AGroupTiedWithAnotherCombatant_IsFlaggedWithAllOfThem()
    {
        var order = CombatRules.OrderInitiative(
        [
            new InitiativeEntry("mummy", 9, -1, 1, "g1"),
            new InitiativeEntry("mummy-2", 9, -1, 2, "g1"),
            new InitiativeEntry("aiden", 9, 0, 3),
        ]);

        Assert.Equal(["aiden", "mummy", "mummy-2"], order.Order.Select(e => e.Id));
        Assert.Equal(["aiden", "mummy", "mummy-2"], Assert.Single(order.Ties).Ids);
    }

    [Theory]
    [InlineData(2, "m1,m2,x")]
    [InlineData(0, "x,m1,m2")]
    [InlineData(4, "m1,m2,x")]
    public void OrderInitiative_ATiedCombatant_NeverSplitsAnInitGroup(double xKey, string expected)
    {
        // A group acts on one roll at its first member's place: an order key between two members (keys reassigned after a
        // re-join) puts x before or after the whole group, never inside it.
        var order = CombatRules.OrderInitiative(
        [
            new InitiativeEntry("m1", 9, 0, 1, "g"),
            new InitiativeEntry("x", 9, 0, xKey),
            new InitiativeEntry("m2", 9, 0, 3, "g"),
        ]);

        Assert.Equal(expected.Split(','), order.Order.Select(e => e.Id));
        Assert.Equal(expected.Split(','), Assert.Single(order.Ties).Ids);
    }

    [Fact]
    public void OrderInitiative_NonFiniteOrRepeated_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => CombatRules.OrderInitiative([new InitiativeEntry("a", double.NaN, 0, 1)]));
        Assert.Throws<ArgumentException>(() => CombatRules.OrderInitiative([new InitiativeEntry("a", 3, 0, double.PositiveInfinity)]));
        Assert.Throws<ArgumentException>(() => CombatRules.OrderInitiative([new InitiativeEntry("a", 3, 0, 1), new InitiativeEntry("a", 4, 0, 2)]));
    }
}
