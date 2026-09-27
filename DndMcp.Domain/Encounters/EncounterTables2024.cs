namespace DndMcp.Domain.Encounters;

/// <summary>
/// The 2024 encounter-building table: XP Budget per Character by level and difficulty (SRD 5.2.1, Gameplay Toolbox ›
/// Combat Encounter Difficulty › Step 2). CC-BY, unlike the 2014 tables.
///
/// <para>
/// There is deliberately nothing else here. The 2024 rules have no group multiplier and no adventuring-day budget; a
/// method that borrowed 2014's multiplier would inflate every group encounter, and no 2024 answer may use one. Tests pin
/// this table cell for cell against the SRD 5.2 markdown, read as text.
/// </para>
/// </summary>
public static class EncounterTables2024
{
    // SRD 5.2.1 Gameplay Toolbox, "XP Budget per Character": Low, Moderate, High for levels 1–20.
    private static readonly XpBudget2024[] Budgets =
    [
        new(50, 75, 100),
        new(100, 150, 200),
        new(150, 225, 400),
        new(250, 375, 500),
        new(500, 750, 1_100),
        new(600, 1_000, 1_400),
        new(750, 1_300, 1_700),
        new(1_000, 1_700, 2_100),
        new(1_300, 2_000, 2_600),
        new(1_600, 2_300, 3_100),
        new(1_900, 2_900, 4_100),
        new(2_200, 3_700, 4_700),
        new(2_600, 4_200, 5_400),
        new(2_900, 4_900, 6_200),
        new(3_300, 5_400, 7_800),
        new(3_800, 6_100, 9_800),
        new(4_500, 7_200, 11_700),
        new(5_000, 8_700, 14_200),
        new(5_500, 10_700, 17_200),
        new(6_400, 13_200, 22_000),
    ];

    /// <summary>One character's budget at <paramref name="level"/> (1–20).</summary>
    public static XpBudget2024 XpBudget(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, EncounterLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, EncounterLimits.MaxLevel);
        return Budgets[level - 1];
    }
}

/// <summary>One character's (or, summed, a party's) 2024 XP budget for each difficulty.</summary>
public sealed record XpBudget2024(long Low, long Moderate, long High)
{
    public static XpBudget2024 operator +(XpBudget2024 a, XpBudget2024 b) =>
        new(a.Low + b.Low, a.Moderate + b.Moderate, a.High + b.High);

    public static XpBudget2024 Zero { get; } = new(0, 0, 0);
}
