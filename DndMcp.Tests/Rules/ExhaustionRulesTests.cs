using DndMcp.Domain.Rules;
using Xunit;

namespace DndMcp.Tests.Rules;

/// <summary>
/// Invariant: Exhaustion per edition — 2024 −2 × level on every D20 Test and −5 ft × level of Speed; 2014 the cumulative
/// table (1 Disadvantage on checks; 2 Speed halved; 3 Disadvantage on attacks and saves; 4 maximum halved; 5 Speed 0);
/// level 6 is death in both. The reminder line is the tracker's <c>exhaustion_effects</c> text (FIX B3).
/// </summary>
public sealed class ExhaustionRulesTests
{
    [Theory]
    // level, edition → D20 penalty, speed −ft, halved, zero, dis checks, dis attacks/saves, max halved, dead
    [InlineData(0, "2024", 0, 0, false, false, false, false, false, false)]
    [InlineData(1, "2024", 2, 5, false, false, false, false, false, false)]
    [InlineData(5, "2024", 10, 25, false, false, false, false, false, false)]
    [InlineData(6, "2024", 12, 30, false, false, false, false, false, true)]
    [InlineData(0, "2014", 0, 0, false, false, false, false, false, false)]
    [InlineData(1, "2014", 0, 0, false, false, true, false, false, false)]
    [InlineData(2, "2014", 0, 0, true, false, true, false, false, false)]
    [InlineData(3, "2014", 0, 0, true, false, true, true, false, false)]
    [InlineData(4, "2014", 0, 0, true, false, true, true, true, false)]
    [InlineData(5, "2014", 0, 0, false, true, true, true, true, false)]
    [InlineData(6, "2014", 0, 0, false, true, true, true, true, true)]
    public void Exhaustion_EffectsPerEdition(int level, string edition, int penalty, int speed, bool halved, bool zero, bool checks, bool attacks, bool maxHalved, bool dead)
    {
        var effects = CombatRules.Exhaustion(level, edition);

        Assert.Equal(
            (penalty, speed, halved, zero, checks, attacks, maxHalved, dead),
            (effects.D20Penalty, effects.SpeedPenaltyFeet, effects.SpeedHalved, effects.SpeedZero, effects.DisadvantageOnChecks,
                effects.DisadvantageOnAttacksAndSaves, effects.MaxHpHalved, effects.Dead));
        Assert.Equal(penalty, CombatRules.ExhaustionD20Penalty(level, edition));
        Assert.Equal(checks, CombatRules.ExhaustionDisadvantageOnChecks(level, edition));
        Assert.Equal(attacks, CombatRules.ExhaustionDisadvantageOnAttacksAndSaves(level, edition));
    }

    [Theory]
    [InlineData(0, "2024", "")]
    [InlineData(1, "2024", "Exhaustion 1: D20 Tests −2; Speed −5 ft")]
    [InlineData(3, "2024", "Exhaustion 3: D20 Tests −6; Speed −15 ft")]
    [InlineData(1, "2014", "Exhaustion 1: Disadvantage on ability checks")]
    [InlineData(2, "2014", "Exhaustion 2: Disadvantage on ability checks; Speed halved")]
    [InlineData(4, "2014", "Exhaustion 4: Disadvantage on ability checks; Speed halved; Disadvantage on attack rolls and saving throws; hit point maximum halved")]
    [InlineData(5, "2014", "Exhaustion 5: Disadvantage on ability checks; Speed 0; Disadvantage on attack rolls and saving throws; hit point maximum halved")]
    [InlineData(6, "2014", "Exhaustion 6: dead")]
    [InlineData(6, "2024", "Exhaustion 6: dead")]
    public void Exhaustion_ReminderLine(int level, string edition, string text)
    {
        Assert.Equal(text, CombatRules.Exhaustion(level, edition).Text);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void Exhaustion_OutsideZeroToSix_IsAHostBug(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CombatRules.Exhaustion(level, "2024"));
    }

    [Fact]
    public void Exhaustion_MaximumHalving_IsHitPointMathsThreshold()
    {
        Assert.Equal(HitPointMath.Exhaustion2014HalvesMaximumAt, Enumerable.Range(0, 7).First(l => CombatRules.Exhaustion(l, "2014").MaxHpHalved));
        Assert.Equal(CombatRules.ExhaustionDeathLevel, Enumerable.Range(0, 7).First(l => CombatRules.Exhaustion(l, "2024").Dead));
    }
}
