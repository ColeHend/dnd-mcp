namespace DndMcp.Domain.Encounters;

/// <summary>
/// The 2014 encounter-building tables: XP thresholds by character level, the encounter multipliers with the party-size
/// shift, and Adventuring Day XP.
///
/// <para>
/// <b>Source and licence.</b> None of these is in SRD 5.1. They are the Dungeon Master's Guide (2014), pp. 82–84, also
/// published in the free 2014 Basic Rules ("Building Combat Encounters"); research matched the two row for row. They are
/// not CC-BY, which is fine for a personal local server and needs a decision before any publishing (PLAN.md, open
/// question 1). Every result that uses them names the DMG as its source, so nobody reads them as SRD text.
/// </para>
/// <para>
/// <b>Do not re-derive these.</b> The dndR R package computes the thresholds as Easy × 2 / × 3 / × 4, which is wrong at
/// 17 of 20 levels (level 3 Deadly is 400, not 300), and uses × 6 / × 5 / × 4 for 15+ monsters where the DMG gives
/// × 5 / × 4 / × 3. Tests pin both.
/// </para>
/// </summary>
public static class EncounterTables2014
{
    // DMG 2014 p. 82, "XP Thresholds by Character Level": Easy, Medium, Hard, Deadly for levels 1–20.
    private static readonly XpThresholds2014[] Thresholds =
    [
        new(25, 50, 75, 100),
        new(50, 100, 150, 200),
        new(75, 150, 225, 400),
        new(125, 250, 375, 500),
        new(250, 500, 750, 1_100),
        new(300, 600, 900, 1_400),
        new(350, 750, 1_100, 1_700),
        new(450, 900, 1_400, 2_100),
        new(550, 1_100, 1_600, 2_400),
        new(600, 1_200, 1_900, 2_800),
        new(800, 1_600, 2_400, 3_600),
        new(1_000, 2_000, 3_000, 4_500),
        new(1_100, 2_200, 3_400, 5_100),
        new(1_250, 2_500, 3_800, 5_700),
        new(1_400, 2_800, 4_300, 6_400),
        new(1_600, 3_200, 4_800, 7_200),
        new(2_000, 3_900, 5_900, 8_800),
        new(2_100, 4_200, 6_300, 9_500),
        new(2_400, 4_900, 7_300, 10_900),
        new(2_800, 5_700, 8_500, 12_700),
    ];

    // DMG 2014 p. 84, "Adventuring Day XP": adjusted XP per character a party can handle before a long rest.
    private static readonly int[] AdventuringDay =
    [
        300, 600, 1_200, 1_700, 3_500, 4_000, 5_000, 6_000, 7_500, 9_000,
        10_500, 11_500, 13_500, 15_000, 18_000, 20_000, 25_000, 27_000, 30_000, 40_000,
    ];

    /// <summary>
    /// DMG 2014 p. 82, "Encounter Multipliers", as the rows of monster counts (1, 2, 3–6, 7–10, 11–14, 15+). The DMG's
    /// party-size rule moves along one ladder: fewer than three characters use the next higher multiplier, six or more
    /// the next lower, so the ladder has one step below × 1 (× 0.5, for one monster against a big party) and one above
    /// × 4 (× 5, for 15+ monsters against a small one).
    /// </summary>
    public static IReadOnlyList<MultiplierRow> MultiplierRows { get; } =
    [
        new(1, 1, 1m),
        new(2, 2, 1.5m),
        new(3, 6, 2m),
        new(7, 10, 2.5m),
        new(11, 14, 3m),
        new(15, null, 4m),
    ];

    private static readonly decimal[] Ladder = [0.5m, 1m, 1.5m, 2m, 2.5m, 3m, 4m, 5m];

    /// <summary>A party smaller than this uses the next higher multiplier.</summary>
    public const int SmallPartyBelow = 3;

    /// <summary>A party this large or larger uses the next lower multiplier.</summary>
    public const int LargePartyFrom = 6;

    /// <summary>One character's four thresholds at <paramref name="level"/> (1–20).</summary>
    public static XpThresholds2014 XpThresholds(int level) => Thresholds[RowFor(level)];

    /// <summary>One character's Adventuring Day XP at <paramref name="level"/> (1–20).</summary>
    public static int AdventuringDayXp(int level) => AdventuringDay[RowFor(level)];

    /// <summary>
    /// The multiplier for <paramref name="monsterCount"/> monsters against a party of <paramref name="partySize"/>
    /// characters, with the row it came from and how the party size moved it.
    /// </summary>
    public static EncounterMultiplier Multiplier(int monsterCount, int partySize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(monsterCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(partySize, 1);

        var row = 0;
        while (row + 1 < MultiplierRows.Count && monsterCount >= MultiplierRows[row + 1].MinMonsters)
        {
            row++;
        }

        var shift = partySize < SmallPartyBelow ? 1 : partySize >= LargePartyFrom ? -1 : 0;
        return new EncounterMultiplier(Ladder[row + 1 + shift], MultiplierRows[row], shift);
    }

    private static int RowFor(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, EncounterLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, EncounterLimits.MaxLevel);
        return level - 1;
    }
}

/// <summary>One character's (or, summed, a party's) 2014 XP thresholds.</summary>
public sealed record XpThresholds2014(long Easy, long Medium, long Hard, long Deadly)
{
    public static XpThresholds2014 operator +(XpThresholds2014 a, XpThresholds2014 b) =>
        new(a.Easy + b.Easy, a.Medium + b.Medium, a.Hard + b.Hard, a.Deadly + b.Deadly);

    public static XpThresholds2014 Zero { get; } = new(0, 0, 0, 0);
}

/// <summary>A row of the 2014 multiplier table: monster counts <see cref="MinMonsters"/>–<see cref="MaxMonsters"/> (null: no upper bound).</summary>
public sealed record MultiplierRow(int MinMonsters, int? MaxMonsters, decimal Standard)
{
    /// <summary>"1", "2", "3–6", "15+": the row as the DMG labels it.</summary>
    public string Label => MaxMonsters switch
    {
        null => $"{MinMonsters}+",
        var max when max == MinMonsters => $"{MinMonsters}",
        var max => $"{MinMonsters}–{max}",
    };
}

/// <summary>
/// The multiplier applied, the table row it came from (<see cref="Row"/>, whose <see cref="MultiplierRow.Standard"/> is
/// the value for three to five characters) and the party-size shift: +1 for fewer than three characters, −1 for six or
/// more.
/// </summary>
public sealed record EncounterMultiplier(decimal Value, MultiplierRow Row, int PartySizeShift);
