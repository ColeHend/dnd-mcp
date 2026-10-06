using DndMcp.Domain.Characters;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: the advancement table is SRD 5.1 / 5.2's (the same both editions), "equals or exceeds" reaches a level, the
/// proficiency bonus matches the vendored level records, and the Ability Score Improvement and Epic Boon class levels are
/// the vendored level records' features in each edition.
/// </summary>
public sealed class AdvancementTests
{
    [Fact]
    public void Rows_AreTheCharacterAdvancementTable()
    {
        Assert.Equal(
            [0, 300, 900, 2_700, 6_500, 14_000, 23_000, 34_000, 48_000, 64_000, 85_000, 100_000, 120_000, 140_000, 165_000, 195_000,
             225_000, 265_000, 305_000, 355_000],
            Advancement.Rows.Select(r => r.Xp));
        Assert.Equal(Enumerable.Range(1, 20), Advancement.Rows.Select(r => r.Level));
        Assert.Equal([2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6], Advancement.Rows.Select(r => r.ProficiencyBonus));
    }

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(299, 1)]
    [InlineData(300, 2)]
    [InlineData(34_000, 8)]
    [InlineData(47_999, 8)]
    [InlineData(48_000, 9)]
    [InlineData(354_999, 19)]
    [InlineData(355_000, 20)]
    [InlineData(10_000_000, 20)]
    public void LevelForXp_EqualsOrExceeds_ReachesTheLevel(int xp, int level)
    {
        Assert.Equal(level, Advancement.LevelForXp(xp));
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(8, 48_000)]
    [InlineData(19, 355_000)]
    [InlineData(20, null)]
    public void NextThreshold_Level_IsTheNextLevelsXp(int level, int? next)
    {
        Assert.Equal(next, Advancement.NextThreshold(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void XpFor_NotALevel_IsAHostBug(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Advancement.XpFor(level));
        Assert.Throws<ArgumentOutOfRangeException>(() => Advancement.NextThreshold(level));
    }

    [Theory]
    [InlineData(E2014)]
    [InlineData(E2024)]
    public void ProficiencyBonus_MatchesEveryVendoredLevelRecord(string edition)
    {
        foreach (var record in SpellSlotTablesTests.Records(edition).Where(r => !r.TryGetProperty("subclass", out _)))
        {
            Assert.Equal(record.GetProperty("prof_bonus").GetInt32(), Advancement.ProficiencyBonus(record.GetProperty("level").GetInt32()));
        }
    }

    [Theory]
    [InlineData(E2014)]
    [InlineData(E2024)]
    public void ImprovementAt_EveryClassLevel_MatchesTheVendoredFeatures(string edition)
    {
        foreach (var record in SpellSlotTablesTests.Records(edition).Where(r => !r.TryGetProperty("subclass", out _)))
        {
            var classIndex = record.GetProperty("class").GetProperty("index").GetString()!;
            var level = record.GetProperty("level").GetInt32();
            var features = record.GetProperty("features").EnumerateArray().Select(f => f.GetProperty("index").GetString()!).ToList();
            var expected = features.Any(f => f.EndsWith("epic-boon", StringComparison.Ordinal))
                ? SheetValues.ReminderKinds.EpicBoon
                : features.Any(f => f.Contains("ability-score-improvement", StringComparison.Ordinal))
                    ? SheetValues.ReminderKinds.AbilityScoreImprovement
                    : null;

            Assert.True(expected == Advancement.ImprovementAt(edition, classIndex, level), $"{edition} {classIndex} {level}");
        }
    }

    [Fact]
    public void AsiLevels_PerEdition_AreTheStatedLists()
    {
        Assert.Equal([4, 8, 12, 16, 19], Advancement.AsiLevels(E2014, "wizard"));
        Assert.Equal([4, 8, 12, 16], Advancement.AsiLevels(E2024, "wizard"));
        Assert.Equal([4, 6, 8, 12, 14, 16, 19], Advancement.AsiLevels(E2014, "fighter"));
        Assert.Equal([4, 8, 10, 12, 16], Advancement.AsiLevels(E2024, "rogue"));
    }
}
