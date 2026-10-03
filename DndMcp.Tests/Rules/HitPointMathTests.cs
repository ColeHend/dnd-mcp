using DndMcp.Domain.Features;
using DndMcp.Domain.Rules;
using Xunit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: the sheet, the tracker and the simulator seeding read one effective maximum: the reduction first (floored at
/// 0, which means death), then the 2014 Exhaustion-4 halving, rounding down. A copy that halved before subtracting would
/// disagree by up to half the reduction and make the end-of-combat write-back see drift that never happened.
/// </summary>
public sealed class HitPointMathTests
{
    [Theory]
    [InlineData(110, 0, 0, "2014", 110)]
    [InlineData(110, 0, 0, "2024", 110)]
    [InlineData(110, 10, 0, "2014", 100)]
    [InlineData(110, 0, 3, "2014", 110)]
    [InlineData(110, 0, 4, "2014", 55)]
    [InlineData(111, 0, 4, "2014", 55)]
    [InlineData(110, 10, 4, "2014", 50)]
    [InlineData(111, 10, 5, "2014", 50)]
    [InlineData(110, 10, 6, "2014", 50)]
    [InlineData(110, 0, 4, "2024", 110)]
    [InlineData(110, 10, 6, "2024", 100)]
    [InlineData(12, 12, 0, "2014", 0)]
    [InlineData(12, 20, 0, "2024", 0)]
    [InlineData(1, 0, 4, "2014", 0)]
    public void EffectiveMaxHp_ReductionThen2014ExhaustionHalving_RoundsDown(
        int maxHp, int reduction, int exhaustion, string edition, int expected)
    {
        Assert.Equal(expected, HitPointMath.EffectiveMaxHp(maxHp, reduction, exhaustion, edition));
    }

    [Fact]
    public void EffectiveMaxHp_HalvingFromLevelFour_IsThe2014ConditionTableRow()
    {
        Assert.Equal(4, HitPointMath.Exhaustion2014HalvesMaximumAt);
        Assert.Equal(60, HitPointMath.EffectiveMaxHp(120, 0, 3, DslValues.Editions.E2014) / 2);
        Assert.Equal(60, HitPointMath.EffectiveMaxHp(120, 0, 4, DslValues.Editions.E2014));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(10, -1, 0)]
    [InlineData(10, 0, -1)]
    public void EffectiveMaxHp_NegativeInput_IsRefusedAsAHostBug(int maxHp, int reduction, int exhaustion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => HitPointMath.EffectiveMaxHp(maxHp, reduction, exhaustion, DslValues.Editions.E2024));
    }
}
