using DndMcp.Domain.Encounters;

namespace DndMcp.Domain.Features;

/// <summary>
/// A typical monster's saving throw bonus by Challenge Rating: the default target save bonus for save effects.
///
/// <para>
/// <b>Not a DMG table.</b> The DMG 2014's "Monster Statistics by Challenge Rating" gives AC, HP, attack bonus, damage and
/// save DC, but no save bonus; the 2024 books give no stat-by-CR table at all. This column is The Finished Book's
/// (tomedunn) "Baseline Monster Stats", the average save bonus of published monsters by CR (research B3). That page's AC,
/// HP and XP columns are a different model from the DMG's and are deliberately NOT used: only the save bonus is taken, and
/// every result that uses it says so (<see cref="Source"/>), because a model quoting "the DMG's save bonus" would be
/// repeating an invention.
/// </para>
/// <para>
/// CR ≤ 1: +0; 2–3 +1; 4–5 +2; 6–7 +3; 8–9 +4; 10–11 +5; 12–13 +6; 14–16 +7; 17–18 +8; 19–20 +9; 21–22 +10; 23–24 +11;
/// 25–26 +12; 27–28 +13; 29–30 +14. (14–16 is three CRs wide in the source; every other step is two.)
/// </para>
/// </summary>
public static class TypicalSaveBonus
{
    /// <summary>How results cite the table.</summary>
    public const string Source =
        "typical save bonus by CR from The Finished Book's \"Baseline Monster Stats\" (tomedunn; an average of published monsters, not a DMG table)";

    public const string SourceUrl = "https://tomedunn.github.io/the-finished-book/monsters/baseline-monster-stats/";

    // Index = whole CR 0..30; the fractional CRs share CR 0's row.
    private static readonly int[] ByWholeCr =
    [
        0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
    ];

    /// <summary>The typical save bonus for a CR (0, 1/8, 1/4, 1/2 and 1 are all +0).</summary>
    public static int For(ChallengeRating cr) => ByWholeCr[cr.Whole ?? 0];
}
