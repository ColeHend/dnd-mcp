using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// Invariant: the 2014 method sums per-character thresholds, multiplies the monsters' XP by the multiplier for the
/// monsters that count (party size shifting it), labels by the highest threshold reached, and never lets the
/// multiplier change the XP the party earns.
/// </summary>
public sealed class Encounter2014Tests
{
    private static EncounterMonster Monster(string cr, int count = 1, bool excluded = false, string name = "M") =>
        new(name, name, ChallengeRating.Parse(cr), ChallengeRatingTables.Xp(ChallengeRating.Parse(cr)), count, excluded);

    private static EncounterMonster WithXp(int xp, int count = 1, bool excluded = false, string name = "M") =>
        new(name, name, ChallengeRating.Zero, xp, count, excluded);

    [Fact]
    public void Assess_ThreeOgresVsFourLevelFives_IsMediumAt2700()
    {
        // PLAN.md's manual check: "Is 3 ogres Deadly for four level-5s in 2014?" 3 × 450 × 2 = 2,700; Medium is 2,000.
        var result = Encounter2014.Assess([5, 5, 5, 5], [Monster("2", 3)]);

        Assert.Equal(new XpThresholds2014(1_000, 2_000, 3_000, 4_400), result.Thresholds);
        Assert.Equal(1_350, result.MonsterXp);
        Assert.Equal(2m, result.Multiplier.Value);
        Assert.Equal(2_700m, result.AdjustedXp);
        Assert.Equal(EncounterDifficulty.Medium, result.Difficulty);
    }

    [Fact]
    public void Assess_FourMonstersWorth500_AdjustTo1000()
    {
        // The DMG's own multiplier example: four monsters worth 500 XP in all, × 2, 1,000 adjusted.
        var result = Encounter2014.Assess([3, 3, 3, 3], [WithXp(125, 4)]);

        Assert.Equal(1_000m, result.AdjustedXp);
        Assert.Equal(500, result.MonsterXp);
    }

    [Fact]
    public void Assess_MixedLevels_SumEachCharactersThresholds()
    {
        var result = Encounter2014.Assess([3, 3, 3, 2], [Monster("1")]);

        Assert.Equal(new XpThresholds2014(275, 550, 825, 1_400), result.Thresholds);
        Assert.Equal(1_200 * 3 + 600, result.AdventuringDayXp);
    }

    [Theory]
    [InlineData(99, EncounterDifficulty.Trivial)]
    [InlineData(100, EncounterDifficulty.Easy)]
    [InlineData(199, EncounterDifficulty.Easy)]
    [InlineData(200, EncounterDifficulty.Medium)]
    [InlineData(299, EncounterDifficulty.Medium)]
    [InlineData(300, EncounterDifficulty.Hard)]
    [InlineData(399, EncounterDifficulty.Hard)]
    [InlineData(400, EncounterDifficulty.Deadly)]
    [InlineData(10_000, EncounterDifficulty.Deadly)]
    public void Assess_OneMonster_LabelIsTheHighestThresholdReached(int xp, string expected)
    {
        // Four level 1 characters: 100 / 200 / 300 / 400. One monster is × 1, so the XP is the adjusted XP.
        Assert.Equal(expected, Encounter2014.Assess([1, 1, 1, 1], [WithXp(xp)]).Difficulty);
    }

    [Fact]
    public void Assess_OneMonsterVsSixCharacters_HalvesTheXp()
    {
        var result = Encounter2014.Assess([1, 1, 1, 1, 1, 1], [WithXp(75)]);

        Assert.Equal(0.5m, result.Multiplier.Value);
        Assert.Equal(37.5m, result.AdjustedXp);
        Assert.Equal(75, result.MonsterXp);
    }

    [Fact]
    public void Assess_FifteenMonstersVsTwoCharacters_TimesFive()
    {
        var result = Encounter2014.Assess([5, 5], [Monster("1/8", 15)]);

        Assert.Equal(5m, result.Multiplier.Value);
        Assert.Equal(15 * 25 * 5m, result.AdjustedXp);
    }

    [Fact]
    public void Assess_ExcludedMonsters_LeaveTheCountButKeepTheirXp()
    {
        var result = Encounter2014.Assess([10, 10, 10, 10], [Monster("10"), Monster("1/8", 10, excluded: true)]);

        Assert.Equal(11, result.MonsterCount);
        Assert.Equal(1, result.CountedMonsters);
        Assert.Equal(1m, result.Multiplier.Value);
        Assert.Equal(5_900 + 250, result.MonsterXp);
        Assert.Equal(6_150m, result.AdjustedXp);
    }

    [Fact]
    public void Assess_WithoutExclusion_TheWeakMonstersRaiseTheMultiplier()
    {
        var result = Encounter2014.Assess([10, 10, 10, 10], [Monster("10"), Monster("1/8", 10)]);

        Assert.Equal(11, result.CountedMonsters);
        Assert.Equal(3m, result.Multiplier.Value);
    }

    [Fact]
    public void Assess_EveryMonsterExcluded_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => Encounter2014.Assess([5], [Monster("1", 2, excluded: true)]));

        Assert.Contains("Every monster is marked exclude", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Assess_Classify_UsesAdjustedNotRawXp()
    {
        // 2 × 100 XP raw is Easy for four level 1s (≥ 100 < 200), but × 1.5 makes 300: Hard.
        Assert.Equal(EncounterDifficulty.Hard, Encounter2014.Assess([1, 1, 1, 1], [WithXp(100, 2)]).Difficulty);
    }
}
