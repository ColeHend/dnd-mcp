namespace DndMcp.Domain.Encounters;

/// <summary>
/// The tables keyed by Challenge Rating: XP, proficiency bonus, and the 2014 DMG's Monster Statistics by Challenge Rating.
///
/// <para>
/// XP by CR is the same in both editions and is the one input both encounter methods share, so a wrong row misstates
/// every encounter that uses it. The local SRD 5.1 markdown's copy skips CR 9–13 and CR 26–30; the full table below is
/// the SRD 5.2.1's (Monsters › Challenge Rating › Experience Points), which the DMG 2014 (p. 275) and the 2014 Basic Rules
/// match row for row. Tests pin it against both SRD markdown tables and against the XP printed in every SRD stat block.
/// </para>
/// </summary>
public static class ChallengeRatingTables
{
    /// <summary>
    /// The XP a CR 0 monster is worth when nothing says otherwise. The table gives "0 or 10". In the 2014 rules a CR 0
    /// creature with no effective attacks is worth 0 and its stat block says which (the Frog, the Sea Horse); most 2024
    /// stat blocks print "XP 0 or 10" themselves (all but the Seahorse and the Shrieker Fungus, which print 0), and the
    /// data stores 10. A CR typed without a stat block gets 10, and the result says so.
    /// </summary>
    public const int DefaultCrZeroXp = 10;

    // SRD 5.2.1 Monsters › Experience Points by Challenge Rating (CR 0 is "0 or 10"); identical to the DMG 2014 p. 275.
    private static readonly int[] XpByRow =
    [
        DefaultCrZeroXp, 25, 50, 100,
        200, 450, 700, 1_100, 1_800, 2_300, 2_900, 3_900, 5_000, 5_900,
        7_200, 8_400, 10_000, 11_500, 13_000, 15_000, 18_000, 20_000, 22_000, 25_000,
        33_000, 41_000, 50_000, 62_000, 75_000, 90_000, 105_000, 120_000, 135_000, 155_000,
    ];

    /// <summary>
    /// DMG 2014 pp. 274–275, "Monster Statistics by Challenge Rating", every row. Not in either SRD, and the 2024 DMG has
    /// no replacement (it dropped the monster-creation maths). Cross-checked in research against the DMG scan, the
    /// AsmodeusXI CR calculator (whose CR 25 DC 11 is a typo for 21) and Chronicle Codex. The CR 0 row's AC, attack bonus
    /// and save DC are maxima ("≤ 13", "≤ +3"); see <see cref="MonsterStatsRow.IsCeiling"/>.
    /// </summary>
    private static readonly MonsterStatsRow[] MonsterStatsRows =
    [
        new(ChallengeRating.All[0], 13, 1, 6, 3, 0, 1, 13),
        new(ChallengeRating.All[1], 13, 7, 35, 3, 2, 3, 13),
        new(ChallengeRating.All[2], 13, 36, 49, 3, 4, 5, 13),
        new(ChallengeRating.All[3], 13, 50, 70, 3, 6, 8, 13),
        new(ChallengeRating.All[4], 13, 71, 85, 3, 9, 14, 13),
        new(ChallengeRating.All[5], 13, 86, 100, 3, 15, 20, 13),
        new(ChallengeRating.All[6], 13, 101, 115, 4, 21, 26, 13),
        new(ChallengeRating.All[7], 14, 116, 130, 5, 27, 32, 14),
        new(ChallengeRating.All[8], 15, 131, 145, 6, 33, 38, 15),
        new(ChallengeRating.All[9], 15, 146, 160, 6, 39, 44, 15),
        new(ChallengeRating.All[10], 15, 161, 175, 6, 45, 50, 15),
        new(ChallengeRating.All[11], 16, 176, 190, 7, 51, 56, 16),
        new(ChallengeRating.All[12], 16, 191, 205, 7, 57, 62, 16),
        new(ChallengeRating.All[13], 17, 206, 220, 7, 63, 68, 16),
        new(ChallengeRating.All[14], 17, 221, 235, 8, 69, 74, 17),
        new(ChallengeRating.All[15], 17, 236, 250, 8, 75, 80, 17),
        new(ChallengeRating.All[16], 18, 251, 265, 8, 81, 86, 18),
        new(ChallengeRating.All[17], 18, 266, 280, 8, 87, 92, 18),
        new(ChallengeRating.All[18], 18, 281, 295, 8, 93, 98, 18),
        new(ChallengeRating.All[19], 18, 296, 310, 9, 99, 104, 18),
        new(ChallengeRating.All[20], 19, 311, 325, 10, 105, 110, 19),
        new(ChallengeRating.All[21], 19, 326, 340, 10, 111, 116, 19),
        new(ChallengeRating.All[22], 19, 341, 355, 10, 117, 122, 19),
        new(ChallengeRating.All[23], 19, 356, 400, 10, 123, 140, 19),
        new(ChallengeRating.All[24], 19, 401, 445, 11, 141, 158, 20),
        new(ChallengeRating.All[25], 19, 446, 490, 11, 159, 176, 20),
        new(ChallengeRating.All[26], 19, 491, 535, 11, 177, 194, 20),
        new(ChallengeRating.All[27], 19, 536, 580, 12, 195, 212, 21),
        new(ChallengeRating.All[28], 19, 581, 625, 12, 213, 230, 21),
        new(ChallengeRating.All[29], 19, 626, 670, 12, 231, 248, 21),
        new(ChallengeRating.All[30], 19, 671, 715, 13, 249, 266, 22),
        new(ChallengeRating.All[31], 19, 716, 760, 13, 267, 284, 22),
        new(ChallengeRating.All[32], 19, 761, 805, 13, 285, 302, 22),
        new(ChallengeRating.All[33], 19, 806, 850, 14, 303, 320, 23),
    ];

    /// <summary>
    /// The XP a monster of this CR is worth. For CR 0 that is <see cref="DefaultCrZeroXp"/>; a stat block may say 0
    /// instead (<see cref="IsStatBlockXp"/>).
    /// </summary>
    public static int Xp(ChallengeRating cr) => XpByRow[cr.Row];

    /// <summary>
    /// Whether a stat block's XP agrees with its CR: the table's value, or for CR 0 either 0 or 10. A disagreement is
    /// damaged data (the 2014 Dretch's 25 XP at CR 1/4 upstream), not a rule.
    /// </summary>
    public static bool IsStatBlockXp(ChallengeRating cr, long xp) =>
        cr.Row == 0 ? xp is 0 or DefaultCrZeroXp : xp == Xp(cr);

    /// <summary>
    /// Proficiency Bonus by Challenge Rating (SRD 5.2.1 Monsters › Proficiency Bonus; the SRD 5.1 table agrees): +2 up to
    /// CR 4, then +1 every four CRs, +9 at CR 29–30.
    /// </summary>
    public static int ProficiencyBonus(ChallengeRating cr) => cr.Whole is { } whole ? Math.Max(2, (whole + 3) / 4 + 1) : 2;

    /// <summary>The DMG 2014 "Monster Statistics by Challenge Rating" row for this CR.</summary>
    public static MonsterStatsRow MonsterStats(ChallengeRating cr) => MonsterStatsRows[cr.Row];

    /// <summary>Every row of <see cref="MonsterStats"/>, lowest CR first.</summary>
    public static IReadOnlyList<MonsterStatsRow> AllMonsterStats { get; } = Array.AsReadOnly(MonsterStatsRows);
}

/// <summary>
/// One row of the DMG 2014 "Monster Statistics by Challenge Rating" table. Damage per round is the DMG's "average damage
/// if every attack hits", over the first three rounds; accuracy enters only through the attack bonus.
/// </summary>
public sealed record MonsterStatsRow(
    ChallengeRating ChallengeRating,
    int ArmorClass,
    int HitPointsMin,
    int HitPointsMax,
    int AttackBonus,
    int DamagePerRoundMin,
    int DamagePerRoundMax,
    int SaveDc)
{
    /// <summary>The proficiency bonus the DMG row lists, which is the monster PB for this CR.</summary>
    public int ProficiencyBonus => ChallengeRatingTables.ProficiencyBonus(ChallengeRating);

    /// <summary>
    /// True for the CR 0 row, whose AC, attack bonus and save DC are upper bounds ("13 or lower"), not targets.
    /// </summary>
    public bool IsCeiling => ChallengeRating.Row == 0;
}
