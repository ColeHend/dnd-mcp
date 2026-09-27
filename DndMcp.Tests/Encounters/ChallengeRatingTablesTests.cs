using DndMcp.Domain.Encounters;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// Invariant: XP and proficiency bonus by CR equal both SRDs' tables, read as text, row for row; and the DMG 2014
/// Monster Statistics by Challenge Rating keeps the shape its source has (contiguous hit-point and damage bands, values
/// that never fall as CR rises), so a transcription slip in any cell fails.
/// </summary>
public sealed class ChallengeRatingTablesTests
{
    [Fact]
    public void Xp_EveryCr_EqualsTheSrd52Table()
    {
        var rows = SrdTableFixtures.Rows("srd52-experience-points-by-challenge-rating.md");

        Assert.Equal(34, rows.Count);
        foreach (var row in rows)
        {
            var cr = ChallengeRating.Parse(row[0]);
            if (cr == ChallengeRating.Zero)
            {
                Assert.Equal("0 or 10", row[1]);
                Assert.Equal(ChallengeRatingTables.DefaultCrZeroXp, ChallengeRatingTables.Xp(cr));
            }
            else
            {
                Assert.Equal(SrdTableFixtures.Number(row[1]), ChallengeRatingTables.Xp(cr));
            }
        }
    }

    [Fact]
    public void Xp_EveryRowTheSrd51MarkdownHas_EqualsIt()
    {
        // The local SRD 5.1 markdown skips CR 9–13 and 26–30; the rows it has agree with the 2024 table.
        var rows = SrdTableFixtures.Rows("srd51-experience-points-by-challenge-rating.md");

        Assert.Equal(24, rows.Count);
        Assert.All(rows.Where(r => r[0] != "0"), row => Assert.Equal(SrdTableFixtures.Number(row[1]), ChallengeRatingTables.Xp(ChallengeRating.Parse(row[0]))));
        Assert.DoesNotContain(rows, r => r[0] is "9" or "13" or "26" or "30");
    }

    // PLAN.md: CR→XP includes CR 9–13 and CR 26–30, the rows the local SRD 5.1 markdown lacks.
    [Theory]
    [InlineData("9", 5_000)]
    [InlineData("10", 5_900)]
    [InlineData("11", 7_200)]
    [InlineData("12", 8_400)]
    [InlineData("13", 10_000)]
    [InlineData("26", 90_000)]
    [InlineData("27", 105_000)]
    [InlineData("28", 120_000)]
    [InlineData("29", 135_000)]
    [InlineData("30", 155_000)]
    public void Xp_RowsTheSrd51MarkdownSkips_HaveTheirValues(string cr, int xp)
    {
        Assert.Equal(xp, ChallengeRatingTables.Xp(ChallengeRating.Parse(cr)));
    }

    [Fact]
    public void ProficiencyBonus_EveryCr_EqualsTheSrd52RangeTable()
    {
        foreach (var row in SrdTableFixtures.Rows("srd52-proficiency-bonus-by-challenge-rating.md"))
        {
            var bounds = row[0].Split('–');
            var low = ChallengeRating.Parse(bounds[0]);
            var high = ChallengeRating.Parse(bounds[1]);
            var bonus = (int)SrdTableFixtures.Number(row[1]);
            Assert.All(ChallengeRating.All.Where(cr => cr >= low && cr <= high), cr => Assert.Equal(bonus, ChallengeRatingTables.ProficiencyBonus(cr)));
        }
    }

    [Fact]
    public void ProficiencyBonus_EveryCr_EqualsTheSrd51Table()
    {
        var rows = SrdTableFixtures.Rows("srd51-proficiency-bonus-by-challenge-rating.md");

        Assert.Equal(34, rows.Count);
        Assert.All(rows, row => Assert.Equal(SrdTableFixtures.Number(row[1]), ChallengeRatingTables.ProficiencyBonus(ChallengeRating.Parse(row[0]))));
    }

    [Theory]
    [InlineData("0", 0, true)]
    [InlineData("0", 10, true)]
    [InlineData("0", 25, false)]
    [InlineData("1/4", 50, true)]
    [InlineData("1/4", 25, false)]
    [InlineData("1", 100, false)]
    [InlineData("12", 8_000, false)]
    [InlineData("12", 8_400, true)]
    public void IsStatBlockXp_ZeroAndTenForCrZeroOnly(string cr, long xp, bool expected)
    {
        Assert.Equal(expected, ChallengeRatingTables.IsStatBlockXp(ChallengeRating.Parse(cr), xp));
    }

    [Fact]
    public void MonsterStats_HitPointAndDamageBands_AreContiguousFromOne()
    {
        var rows = ChallengeRatingTables.AllMonsterStats;

        Assert.Equal(ChallengeRating.All, rows.Select(r => r.ChallengeRating));
        Assert.Equal((1, 6), (rows[0].HitPointsMin, rows[0].HitPointsMax));
        Assert.Equal((0, 1), (rows[0].DamagePerRoundMin, rows[0].DamagePerRoundMax));
        Assert.All(rows.Zip(rows.Skip(1)), pair =>
        {
            Assert.Equal(pair.First.HitPointsMax + 1, pair.Second.HitPointsMin);
            Assert.Equal(pair.First.DamagePerRoundMax + 1, pair.Second.DamagePerRoundMin);
        });
        Assert.All(rows, r => Assert.True(r.HitPointsMin < r.HitPointsMax && r.DamagePerRoundMin < r.DamagePerRoundMax));
    }

    [Fact]
    public void MonsterStats_AcAttackAndSaveDc_NeverFallAsCrRises()
    {
        var rows = ChallengeRatingTables.AllMonsterStats;

        Assert.All(rows.Zip(rows.Skip(1)), pair =>
        {
            Assert.True(pair.Second.ArmorClass >= pair.First.ArmorClass, $"AC falls at CR {pair.Second.ChallengeRating}");
            Assert.True(pair.Second.AttackBonus >= pair.First.AttackBonus, $"attack falls at CR {pair.Second.ChallengeRating}");
            Assert.True(pair.Second.SaveDc >= pair.First.SaveDc, $"DC falls at CR {pair.Second.ChallengeRating}");
        });
    }

    // Spot rows from the DMG scan (research 03 §B1), including the cells other sources get wrong: AsmodeusXI's CR 25
    // DC 11 (the DMG prints 21) and dndlounge's CR 29/30 hit points (761–805 and 806–850).
    [Theory]
    [InlineData("1/8", 13, 7, 35, 3, 2, 3, 13)]
    [InlineData("3", 13, 101, 115, 4, 21, 26, 13)]
    [InlineData("5", 15, 131, 145, 6, 33, 38, 15)]
    [InlineData("10", 17, 206, 220, 7, 63, 68, 16)]
    [InlineData("20", 19, 356, 400, 10, 123, 140, 19)]
    [InlineData("25", 19, 581, 625, 12, 213, 230, 21)]
    [InlineData("29", 19, 761, 805, 13, 285, 302, 22)]
    [InlineData("30", 19, 806, 850, 14, 303, 320, 23)]
    public void MonsterStats_SpotRow_MatchesTheDmg(string cr, int ac, int hpMin, int hpMax, int attack, int dmgMin, int dmgMax, int dc)
    {
        var row = ChallengeRatingTables.MonsterStats(ChallengeRating.Parse(cr));

        Assert.Equal((ac, hpMin, hpMax, attack, dmgMin, dmgMax, dc),
            (row.ArmorClass, row.HitPointsMin, row.HitPointsMax, row.AttackBonus, row.DamagePerRoundMin, row.DamagePerRoundMax, row.SaveDc));
    }

    [Fact]
    public void MonsterStats_OnlyCrZero_IsACeiling()
    {
        Assert.Equal([ChallengeRating.Zero], ChallengeRatingTables.AllMonsterStats.Where(r => r.IsCeiling).Select(r => r.ChallengeRating));
    }

    [Fact]
    public void MonsterStats_ProficiencyBonus_IsTheCrTables()
    {
        Assert.All(ChallengeRatingTables.AllMonsterStats, r => Assert.Equal(ChallengeRatingTables.ProficiencyBonus(r.ChallengeRating), r.ProficiencyBonus));
    }

    // Every AC, attack and save DC cell, typed from the DMG as columns: the monotonic and spot-row checks above pass many
    // one-cell typos (CR 7 AC 16, CR 4 DC 13).
    private static readonly int[] DmgAc = [13, 13, 13, 13, 13, 13, 13, 14, 15, 15, 15, 16, 16, 17, 17, 17, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19, 19];
    private static readonly int[] DmgAttack = [3, 3, 3, 3, 3, 3, 4, 5, 6, 6, 6, 7, 7, 7, 8, 8, 8, 8, 8, 9, 10, 10, 10, 10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14];
    private static readonly int[] DmgSaveDc = [13, 13, 13, 13, 13, 13, 13, 14, 15, 15, 15, 16, 16, 16, 17, 17, 18, 18, 18, 18, 19, 19, 19, 19, 20, 20, 20, 21, 21, 21, 22, 22, 22, 23];

    [Fact]
    public void MonsterStats_AcAttackAndSaveDc_AreTheDmgColumns()
    {
        var rows = ChallengeRatingTables.AllMonsterStats;

        Assert.Equal(DmgAc, rows.Select(r => r.ArmorClass));
        Assert.Equal(DmgAttack, rows.Select(r => r.AttackBonus));
        Assert.Equal(DmgSaveDc, rows.Select(r => r.SaveDc));
    }
}
