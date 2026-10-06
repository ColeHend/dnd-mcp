using DndMcp.Domain.Characters;
using DndMcp.Domain.Simulation.Archetypes;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: hit points by the fixed values are the starting class's hit die maximum + Con at level 1, then die ÷ 2 + 1 +
/// Con per later level of any class (2024: at least 1 per level; 2014: no minimum), never below 1 in total, and the party
/// archetypes' formula is the same one without the 2024 minimum.
/// </summary>
public sealed class LevelHitPointsTests
{
    [Theory]
    [InlineData(6, 3, E2014, 7)]
    [InlineData(12, 3, E2024, 10)]
    [InlineData(10, 2, E2024, 8)]
    [InlineData(6, -5, E2014, -1)]
    [InlineData(6, -5, E2024, 1)]
    [InlineData(8, -4, E2024, 1)]
    public void FixedGain_DieAndCon_IsTheAverageRoundedUpPlusCon(int die, int con, string edition, int gain)
    {
        Assert.Equal(gain, LevelHitPoints.FixedGain(die, con, edition));
    }

    [Theory]
    [InlineData(6, 12, 3, E2014, 86)] // Belmakor without Tough: 9 + 11 × 7 (FIX §1.2).
    [InlineData(6, 12, 2, E2014, 74)] // Torch (FIX §1.4).
    [InlineData(12, 8, 3, E2024, 85)] // Björn (FIX §3).
    [InlineData(8, 8, 2, E2024, 59)] // The fishman monk (FIX §3).
    [InlineData(10, 8, 2, E2024, 68)] // The Dragon Slayer, d10 (FIX §3).
    [InlineData(6, 1, -5, E2014, 1)] // 6 − 5 = 1.
    [InlineData(6, 3, -5, E2014, 1)] // 1 − 1 − 1 floored at 1 in total.
    [InlineData(6, 3, -5, E2024, 3)] // 1 + 1 + 1 (at least 1 per later level).
    public void FixedTotal_OneClass_IsLevelOneMaximumThenTheFixedValue(int die, int level, int con, string edition, int total)
    {
        Assert.Equal(total, LevelHitPoints.FixedTotal([(die, level)], con, edition));
    }

    [Fact]
    public void FixedTotal_Multiclass_GivesLevelOneMaximumOnlyForTheStartingClass()
    {
        // Paladin 6 then sorcerer 6, Con +2: (10 + 2) + 5 × (6 + 2) + 6 × (4 + 2) = 12 + 40 + 36 = 88.
        Assert.Equal(88, LevelHitPoints.FixedTotal([(10, 6), (6, 6)], 2, E2014));

        // Sorcerer first: (6 + 2) + 5 × 6 + 6 × 8 = 8 + 30 + 48 = 86.
        Assert.Equal(86, LevelHitPoints.FixedTotal([(6, 6), (10, 6)], 2, E2014));
    }

    [Fact]
    public void FixedTotal_NoClasses_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => LevelHitPoints.FixedTotal([], 0, E2014));
        Assert.Throws<ArgumentException>(() => LevelHitPoints.FixedTotal([(6, 0)], 0, E2014));
    }

    [Theory]
    [InlineData(12, 20, 5)]
    [InlineData(6, 1, 0)]
    [InlineData(8, 13, 2)]
    [InlineData(10, 7, -1)]
    public void ArchetypeHitPoints_AreTheSameFormula(int die, int level, int con)
    {
        Assert.Equal(die + (level - 1) * (die / 2 + 1) + level * con, ArchetypeHitPoints.At(die, level, con));
        Assert.Equal(LevelHitPoints.FixedTotal([(die, level)], con, minimumOnePerLevel: false), ArchetypeHitPoints.At(die, level, con));
    }
}
