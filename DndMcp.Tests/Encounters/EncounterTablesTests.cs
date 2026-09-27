using DndMcp.Domain.Encounters;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// Invariant: the 2014 DMG tables and the 2024 SRD budget hold their published values, and the relationships between
/// the editions that the encounter output states ("Low equals Medium up to level 7", …) are true of these tables.
///
/// <para>
/// The 2014 tables are not in either SRD, so they are pinned as columns typed from the DMG scan (research 03 §E) apart
/// from the row-major code, plus the two errors a well-known source makes (dndR): thresholds derived as Easy × 2 / × 3 /
/// × 4, and × 6 / × 5 / × 4 for 15+ monsters. The 2024 table is pinned against the SRD 5.2 markdown, read as text.
/// </para>
/// </summary>
public sealed class EncounterTablesTests
{
    private static readonly long[] Easy = [25, 50, 75, 125, 250, 300, 350, 450, 550, 600, 800, 1000, 1100, 1250, 1400, 1600, 2000, 2100, 2400, 2800];
    private static readonly long[] Medium = [50, 100, 150, 250, 500, 600, 750, 900, 1100, 1200, 1600, 2000, 2200, 2500, 2800, 3200, 3900, 4200, 4900, 5700];
    private static readonly long[] Hard = [75, 150, 225, 375, 750, 900, 1100, 1400, 1600, 1900, 2400, 3000, 3400, 3800, 4300, 4800, 5900, 6300, 7300, 8500];
    private static readonly long[] Deadly = [100, 200, 400, 500, 1100, 1400, 1700, 2100, 2400, 2800, 3600, 4500, 5100, 5700, 6400, 7200, 8800, 9500, 10900, 12700];
    private static readonly int[] Day = [300, 600, 1200, 1700, 3500, 4000, 5000, 6000, 7500, 9000, 10500, 11500, 13500, 15000, 18000, 20000, 25000, 27000, 30000, 40000];

    public static TheoryData<int> Levels => new(Enumerable.Range(1, 20));

    [Theory]
    [MemberData(nameof(Levels))]
    public void XpThresholds2014_EveryLevel_IsTheDmgRow(int level)
    {
        var i = level - 1;

        Assert.Equal(new XpThresholds2014(Easy[i], Medium[i], Hard[i], Deadly[i]), EncounterTables2014.XpThresholds(level));
    }

    [Fact]
    public void XpThresholds2014_Level3Deadly_Is400NotTheDerived300()
    {
        Assert.Equal(400, EncounterTables2014.XpThresholds(3).Deadly);
    }

    [Fact]
    public void XpThresholds2014_EasyTimesTwoThreeFour_HoldsAtOnly3Levels()
    {
        // dndR derives Medium/Hard/Deadly as Easy × 2/3/4, which is wrong at 17 of the 20 levels.
        var derivable = Enumerable.Range(1, 20).Where(level =>
        {
            var t = EncounterTables2014.XpThresholds(level);
            return t.Medium == 2 * t.Easy && t.Hard == 3 * t.Easy && t.Deadly == 4 * t.Easy;
        });

        Assert.Equal([1, 2, 4], derivable);
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void AdventuringDayXp2014_EveryLevel_IsTheDmgRow(int level)
    {
        Assert.Equal(Day[level - 1], EncounterTables2014.AdventuringDayXp(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Tables_LevelOutsideOneToTwenty_Throw(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EncounterTables2014.XpThresholds(level));
        Assert.Throws<ArgumentOutOfRangeException>(() => EncounterTables2014.AdventuringDayXp(level));
        Assert.Throws<ArgumentOutOfRangeException>(() => EncounterTables2024.XpBudget(level));
    }

    // (monsters, characters) → multiplier. Every row boundary, and each party-size column: fewer than 3 moves up a step,
    // 6 or more down. The 15+ row is × 5 / × 4 / × 3, not dndR's × 6 / × 5 / × 4.
    [Theory]
    [InlineData(1, 4, 1.0)]
    [InlineData(2, 4, 1.5)]
    [InlineData(3, 4, 2.0)]
    [InlineData(6, 4, 2.0)]
    [InlineData(7, 4, 2.5)]
    [InlineData(10, 4, 2.5)]
    [InlineData(11, 4, 3.0)]
    [InlineData(14, 4, 3.0)]
    [InlineData(15, 4, 4.0)]
    [InlineData(1000, 4, 4.0)]
    [InlineData(1, 3, 1.0)]
    [InlineData(15, 5, 4.0)]
    [InlineData(1, 2, 1.5)]
    [InlineData(1, 1, 1.5)]
    [InlineData(2, 2, 2.0)]
    [InlineData(6, 2, 2.5)]
    [InlineData(10, 2, 3.0)]
    [InlineData(14, 2, 4.0)]
    [InlineData(15, 2, 5.0)]
    [InlineData(1, 6, 0.5)]
    [InlineData(2, 6, 1.0)]
    [InlineData(3, 8, 1.5)]
    [InlineData(7, 6, 2.0)]
    [InlineData(11, 6, 2.5)]
    [InlineData(15, 6, 3.0)]
    public void Multiplier2014_CountAndPartySize_IsTheDmgValue(int monsters, int characters, double expected)
    {
        Assert.Equal((decimal)expected, EncounterTables2014.Multiplier(monsters, characters).Value);
    }

    [Theory]
    [InlineData(1, 4, "1", 0)]
    [InlineData(5, 4, "3–6", 0)]
    [InlineData(20, 4, "15+", 0)]
    [InlineData(5, 2, "3–6", 1)]
    [InlineData(5, 7, "3–6", -1)]
    public void Multiplier2014_ReportsItsRowAndShift(int monsters, int characters, string row, int shift)
    {
        var multiplier = EncounterTables2014.Multiplier(monsters, characters);

        Assert.Equal(row, multiplier.Row.Label);
        Assert.Equal(shift, multiplier.PartySizeShift);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(3, 0)]
    public void Multiplier2014_NoMonstersOrNoParty_Throws(int monsters, int characters)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EncounterTables2014.Multiplier(monsters, characters));
    }

    [Fact]
    public void XpBudget2024_EveryLevel_EqualsTheSrd52Table()
    {
        var rows = SrdTableFixtures.Rows("srd52-xp-budget-per-character.md");

        Assert.Equal(20, rows.Count);
        foreach (var row in rows)
        {
            var level = (int)SrdTableFixtures.Number(row[0]);
            Assert.Equal(
                new XpBudget2024(SrdTableFixtures.Number(row[1]), SrdTableFixtures.Number(row[2]), SrdTableFixtures.Number(row[3])),
                EncounterTables2024.XpBudget(level));
        }
    }

    // What the encounter output's "The editions compared" says about the bands, true of the tables: 2024 Low equals
    // 2014 Medium up to level 7, Moderate equals Hard up to 5, High equals Deadly up to 8, and no further.
    [Fact]
    public void EditionBands_ComparedLevelByLevel_EqualOnlyUpToTheStatedLevels()
    {
        static int LastEqual(Func<int, bool> equal) => Enumerable.Range(1, 20).TakeWhile(equal).Last();
        static bool EqualAnywhereAbove(int level, Func<int, bool> equal) => Enumerable.Range(level + 1, 20 - level).Any(equal);

        bool LowIsMedium(int l) => EncounterTables2024.XpBudget(l).Low == EncounterTables2014.XpThresholds(l).Medium;
        bool ModerateIsHard(int l) => EncounterTables2024.XpBudget(l).Moderate == EncounterTables2014.XpThresholds(l).Hard;
        bool HighIsDeadly(int l) => EncounterTables2024.XpBudget(l).High == EncounterTables2014.XpThresholds(l).Deadly;

        Assert.Equal((7, 5, 8), (LastEqual(LowIsMedium), LastEqual(ModerateIsHard), LastEqual(HighIsDeadly)));
        Assert.False(EqualAnywhereAbove(7, LowIsMedium));
        Assert.False(EqualAnywhereAbove(5, ModerateIsHard));
        Assert.False(EqualAnywhereAbove(8, HighIsDeadly));
    }
}
