using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the reference curves and the rules-table data are the research's numbers (contract §4.7, §8.4, §8.5,
/// §8.11; research A8, B3) computed by the engine's own functions: RPGBOT's target is HP max ÷ 12 of the CR = level row,
/// the warlock baseline is the preset evaluated against that row, the per-level targets are the DMG row with the
/// typical save bonus, the GWF and Savage Attacker tables are the exact per-die values, and the area table agrees with
/// the target counts save effects actually use.
/// </summary>
public sealed class ReferenceCurvesTests
{
    private const double Exact = 1e-9;

    [Fact]
    public void RpgbotTarget_IsTheHitPointMaximumOverTwelve()
    {
        // §8.11: 7.08, 8.33, … 29.58, 33.33 (CR 20's range tops out at 400, not 370).
        double[] printed = [7.08, 8.33, 9.58, 10.83, 12.08, 13.33, 14.58, 15.83, 17.08, 18.33, 19.58, 20.83, 22.08, 23.33, 24.58, 25.83, 27.08, 28.33, 29.58, 33.33];

        Assert.Equal(printed, Enumerable.Range(1, 20).Select(l => Math.Round(ReferenceCurves.RpgbotTarget(l), 2)));
        Assert.Equal(145.0 / 12, ReferenceCurves.RpgbotTarget(5), Exact);
        Assert.Equal(400.0 / 12, ReferenceCurves.RpgbotTarget(20), Exact);
    }

    [Fact]
    public void WarlockBaseline_IsThePublishedCurve()
    {
        // §8.11 (research B3), exactly: Eldritch Blast + Agonizing Blast + Hex against the CR = level row.
        double[] published = [6.30, 8.25, 8.25, 8.90, 17.80, 17.80, 17.80, 19.10, 20.50, 19.10, 28.65, 28.65, 28.65, 28.65, 28.65, 28.65, 38.20, 38.20, 38.20, 38.20];

        for (var level = 1; level <= 20; level++)
        {
            Assert.Equal(published[level - 1], ReferenceCurves.WarlockBaseline(level), Exact);
            Assert.Equal(published[level - 1], ReferenceCurves.At(level).WarlockBaseline, Exact);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Curves_OutsideOneToTwenty_AreACallerBug(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceCurves.RpgbotTarget(level));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceCurves.WarlockBaseline(level));
    }

    [Theory]
    // level, PB, AC, HP max, monster attack, save DC, typical save, warlock attack, warlock hit chance (research B3's table)
    [InlineData(1, 2, 13, 85, 3, 13, 0, 5, 0.65)]
    [InlineData(2, 2, 13, 100, 3, 13, 1, 5, 0.65)]
    [InlineData(4, 2, 14, 130, 5, 14, 2, 6, 0.65)]
    [InlineData(5, 3, 15, 145, 6, 15, 2, 7, 0.65)]
    [InlineData(8, 3, 16, 190, 7, 16, 4, 8, 0.65)]
    [InlineData(9, 4, 16, 205, 7, 16, 4, 9, 0.70)]
    [InlineData(10, 4, 17, 220, 7, 16, 5, 9, 0.65)]
    [InlineData(13, 5, 18, 265, 8, 18, 6, 10, 0.65)]
    [InlineData(16, 5, 18, 310, 9, 18, 7, 10, 0.65)]
    [InlineData(17, 6, 19, 325, 10, 19, 8, 11, 0.65)]
    [InlineData(20, 6, 19, 400, 10, 19, 9, 11, 0.65)]
    public void TargetsByLevel_IsTheDmgRowWithBothCurves(int level, int pb, int ac, int hp, int attack, int dc, int save, int warlockAttack, double hit)
    {
        var row = ReferenceCurves.TargetsByLevel[level - 1];

        Assert.Equal((level, pb, ac, hp, attack, dc, save, warlockAttack), (row.Level, row.ProficiencyBonus, row.ArmorClass, row.HitPointsMax, row.AttackBonus, row.SaveDc, row.TypicalSaveBonus, row.WarlockAttackBonus));
        Assert.Equal(hit, row.WarlockHitChance, 1e-12);
        Assert.Equal(hp / 12.0, row.RpgbotTarget, Exact);
        Assert.Equal(ReferenceCurves.WarlockBaseline(level), row.WarlockBaseline);
    }

    [Fact]
    public void TargetsByLevel_HasEveryLevelOnce()
    {
        Assert.Equal(Enumerable.Range(1, 20), ReferenceCurves.TargetsByLevel.Select(r => r.Level));
    }

    [Theory]
    // §8.5: plain / GWF 2014 / GWF 2024 per die, and Savage Attacker's better of two (1d8: 93/16).
    [InlineData(4, 2.5, 3.0, 13.0 / 4, 25.0 / 8)] // E[max of two dN] = Σ x(2x − 1) ÷ N²: d4 50/16
    [InlineData(6, 3.5, 25.0 / 6, 4.0, 161.0 / 36)]
    [InlineData(8, 4.5, 21.0 / 4, 39.0 / 8, 93.0 / 16)]
    [InlineData(10, 5.5, 63.0 / 10, 29.0 / 5, 143.0 / 20)]
    [InlineData(12, 6.5, 22.0 / 3, 27.0 / 4, 611.0 / 72)]
    public void GreatWeaponFighting_IsTheExactPerDieTable(int sides, double plain, double gwf2014, double gwf2024, double savage)
    {
        var row = Assert.Single(DprTables.GreatWeaponFighting, r => r.Sides == sides);

        Assert.Equal(plain, row.Plain, Exact);
        Assert.Equal(gwf2014, row.Gwf2014, Exact);
        Assert.Equal(gwf2024, row.Gwf2024, Exact);
        Assert.Equal(gwf2014 - plain, row.Gain2014, Exact);
        Assert.Equal(gwf2024 - plain, row.Gain2024, Exact);
        Assert.Equal(savage, row.SavageAttacker, Exact);
    }

    [Theory]
    // §8.4: hit → with Savage Attacker; crit (doubled) → with it under the crit-dice ruling.
    [InlineData(1, 8, null, 4.5, 93.0 / 16, 9.0, 5553.0 / 512)]
    [InlineData(1, 10, null, 5.5, 143.0 / 20, 11.0, 66583.0 / 5000)]
    [InlineData(1, 12, null, 6.5, 611.0 / 72, 13.0, 81835.0 / 5184)]
    [InlineData(2, 6, null, 7.0, 5425.0 / 648, 14.0, 6690481.0 / 419904)]
    [InlineData(2, 6, "gwf2024", 8.0, 2887.0 / 324, 16.0, 1210723.0 / 69984)]
    public void SavageAttacker_IsTheResearchTable(int count, int sides, string? remap, double hit, double hitWith, double crit, double critWithRuling)
    {
        var row = Assert.Single(DprTables.SavageAttacker, r => r.Count == count && r.Sides == sides && r.Remap == remap);

        Assert.Equal(hit, row.Hit, Exact);
        Assert.Equal(hitWith, row.HitWithSavageAttacker, Exact);
        Assert.Equal(hitWith - hit, row.HitGain, Exact);
        Assert.Equal(crit, row.Crit, Exact);
        Assert.Equal(critWithRuling, row.CritWithSavageAttackerOnCritDice, Exact);

        // Ruling off (the default): the better of two rolls of one set, plus the extra crit set rolled once.
        Assert.Equal(hitWith + hit, row.CritWithSavageAttacker, Exact);
        Assert.Equal($"{count}d{sides}", row.Dice);
    }

    [Fact]
    public void SavageAttacker_CoversEveryWeaponDieWithEachRemap()
    {
        Assert.Equal(DprTables.WeaponDice.Count * 3, DprTables.SavageAttacker.Count);
        Assert.All(DprTables.SavageAttacker, r => Assert.True(r.HitWithSavageAttacker > r.Hit && r.CritWithSavageAttackerOnCritDice > r.CritWithSavageAttacker));
    }

    [Fact]
    public void AreaTargets_AgreeWithTheCountsSaveEffectsUse()
    {
        Assert.Equal(DslValues.Shapes.Set.Values, DprTables.AreaTargets.Select(r => r.Shape));
        foreach (var rule in DprTables.AreaTargets)
        {
            Assert.Equal(1, DslValues.Shapes.Targets(rule.Shape, 1)); // at least 1
            for (var k = 1; k <= 10; k++)
            {
                Assert.Equal(k, DslValues.Shapes.Targets(rule.Shape, k * rule.Divisor)); // size ÷ divisor
                Assert.Equal(k + 1, DslValues.Shapes.Targets(rule.Shape, (k * rule.Divisor) + 1)); // rounded up
            }
        }
    }

    [Theory]
    [InlineData("Burning Hands", "cone", 15, 2)]
    [InlineData("Thunderwave", "cube", 15, 3)]
    [InlineData("Flame Strike", "cylinder", 10, 2)]
    [InlineData("Fireball", "sphere", 20, 4)]
    [InlineData("Lightning Bolt", "line", 100, 4)]
    [InlineData("Cone of Cold", "cone", 60, 6)]
    public void AreaTargetExamples_AreTheDmgCounts(string spell, string shape, int size, int targets)
    {
        Assert.Contains(new AreaTargetExample(spell, shape, size, targets), DprTables.AreaTargetExamples);
    }
}
