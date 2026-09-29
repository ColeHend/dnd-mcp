using System.Globalization;
using DndMcp.Domain.Encounters;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// One profile's numbers for one CR: what a target without a stat block defaults to (<see cref="TargetProfiles.Row"/>).
/// </summary>
public sealed record TargetProfileRow
{
    /// <summary>A <see cref="DslValues.Profiles"/> value.</summary>
    public required string Profile { get; init; }

    /// <summary>The CR whose row this is (the numbers below are that CR's), as <see cref="TargetProfiles.Row"/> was asked for it.</summary>
    public required ChallengeRating ChallengeRating { get; init; }

    /// <summary>
    /// The row's AC: the DMG table's for dmg2014; for mm2014 / mm2024 the median AC of that edition's SRD monsters of the
    /// CR (interpolated where none has it), rounded to the nearest whole number with a half rounded UP (see
    /// <see cref="TargetProfiles"/>), so a profile never flatters a build by a rounding.
    /// </summary>
    public required int ArmorClass { get; init; }

    /// <summary>The AC is an upper bound, not a typical value (the DMG's CR 0 row, "13 or lower"): the result says so.</summary>
    public bool ArmorClassIsCeiling { get; init; }

    /// <summary>The save bonus on every ability.</summary>
    public required int SaveBonus { get; init; }

    /// <summary>How the row is cited: "DMG 2014 row for CR 5", "2024 SRD monster medians for CR 5".</summary>
    public required string RowText { get; init; }

    /// <summary>
    /// What the row rests on, for an empirical row: "42 monsters", or for a CR with no SRD monster "no 2024 SRD monster has
    /// CR 18: interpolated between CR 17 and CR 19". Null for the DMG table, which is a design target, not a census.
    /// </summary>
    public string? Basis { get; init; }

    /// <summary>
    /// The save bonus's source, as it follows the bonus itself ("+2, {this}"): results search it for
    /// <see cref="TypicalSaveBonus.Source"/> to cite The Finished Book, so the dmg2014 text keeps that citation whole.
    /// </summary>
    public required string SaveBonusText { get; init; }

    /// <summary>The DMG 2014 row the numbers came from (dmg2014 only): results cite the DMG table when it is set.</summary>
    public MonsterStatsRow? DmgRow { get; init; }

    /// <summary>The empirical row the numbers came from (mm2014 and mm2024 only), unrounded.</summary>
    public MonsterStatsEmpiricalRow? EmpiricalRow { get; init; }

    /// <summary>"2024 SRD monster medians for CR 5 (42 monsters)": <see cref="RowText"/> with its <see cref="Basis"/>.</summary>
    public string Citation => Basis is null ? RowText : $"{RowText} ({Basis})";
}

/// <summary>
/// The target profiles (<see cref="DslValues.Profiles"/>): where a target with no stat block gets its AC and save bonus for
/// a CR. <c>dmg2014</c> is Phase 4's target, unchanged: the DMG 2014 "Monster Statistics by Challenge Rating" row's AC and
/// The Finished Book's typical save bonus. <c>mm2014</c> / <c>mm2024</c> are the medians of that edition's SRD monsters
/// (<see cref="MonsterStatsEmpirical"/>), which settle whether 2024 monsters are harder to hit than the DMG table says (PLAN
/// Open question 6) by letting the caller measure against either.
///
/// <para>
/// <b>One lookup, one place.</b> Every target resolution, the level-equivalent slopes and the warlock reference curve go
/// through <see cref="Row"/>, so a profile means the same "typical CR N monster" everywhere in one result.
/// </para>
/// <para>
/// <b>Rounding the medians.</b> A median of an even count ends in .5 and an interpolated value has two decimals, while an
/// AC and a save bonus are whole numbers. Each is rounded to the nearest whole number with a half rounded UP (AC 14.5 →
/// 15, mean save bonus 0.5 → +1, −0.5 → 0): toward the harder target, so a profile never flatters a build by a rounding.
/// The note on every empirical target prints the unrounded medians beside the numbers used.
/// </para>
/// <para>
/// <b>The save bonus is the median of each monster's MEAN save bonus</b> (its six saves averaged), used for every
/// ability, as the dmg2014 profile's typical bonus is. A monster's proficient saves are usually its best; a save effect
/// against one ability is therefore read against a typical all-round save, and a caller who knows the ability matters
/// gives <c>saves</c>.
/// </para>
/// </summary>
public static class TargetProfiles
{
    /// <summary>The profile's row for <paramref name="cr"/>.</summary>
    /// <param name="profile">A canonical <see cref="DslValues.Profiles"/> value.</param>
    public static TargetProfileRow Row(string profile, ChallengeRating cr) => profile switch
    {
        V.Profiles.Dmg2014 => Dmg2014(cr),
        V.Profiles.Mm2014 or V.Profiles.Mm2024 => Empirical(profile, cr),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Not a target profile."),
    };

    /// <summary>The edition whose SRD monsters an empirical profile is the medians of; null for dmg2014.</summary>
    public static string? Edition(string profile) => profile switch
    {
        V.Profiles.Mm2014 => V.Editions.E2014,
        V.Profiles.Mm2024 => V.Editions.E2024,
        _ => null,
    };

    /// <summary>
    /// A median rounded to a whole number, a half up (toward the harder target). On the decimal value, so 14.5 is exactly
    /// a half and an interpolated 14.49 stays 14.
    /// </summary>
    public static int RoundMedian(double median) => (int)Math.Floor((decimal)median + 0.5m);

    private static TargetProfileRow Dmg2014(ChallengeRating cr)
    {
        var row = ChallengeRatingTables.MonsterStats(cr);
        return new TargetProfileRow
        {
            Profile = V.Profiles.Dmg2014,
            ChallengeRating = cr,
            ArmorClass = row.ArmorClass,
            ArmorClassIsCeiling = row.IsCeiling,
            SaveBonus = TypicalSaveBonus.For(cr),
            RowText = $"DMG 2014 row for CR {cr}",
            SaveBonusText = $"the {TypicalSaveBonus.Source} for CR {cr}",
            DmgRow = row,
        };
    }

    /// <summary>
    /// The empirical profiles: <see cref="MonsterStatsEmpirical.MonsterStats"/> of the profile's edition for the CR, AC
    /// and mean save bonus rounded (<see cref="RoundMedian"/>). A CR with no SRD monster (18 and 25–29 in both editions)
    /// gets the table's interpolated row, and <see cref="TargetProfileRow.Basis"/> names the CRs it lies between.
    /// </summary>
    private static TargetProfileRow Empirical(string profile, ChallengeRating cr)
    {
        var edition = Edition(profile)!;
        var row = MonsterStatsEmpirical.MonsterStats(edition, cr);
        string basis;
        if (row.IsInterpolated)
        {
            var rows = MonsterStatsEmpirical.Rows(edition);
            var below = rows.LastOrDefault(r => r.ChallengeRating < cr && r.Count > 0);
            var above = rows.FirstOrDefault(r => r.ChallengeRating > cr && r.Count > 0);
            var from = below is not null && above is not null
                ? $"interpolated between CR {below.ChallengeRating} and CR {above.ChallengeRating}"
                : $"taken from the nearest, CR {(below ?? above)!.ChallengeRating}";
            basis = $"no {edition} SRD monster has CR {cr}: {from}";
        }
        else
        {
            basis = row.Count == 1 ? "1 monster" : $"{row.Count.ToString(CultureInfo.InvariantCulture)} monsters";
        }

        var mean = row.MeanSaveBonus.ToString("0.##", CultureInfo.InvariantCulture);
        return new TargetProfileRow
        {
            Profile = profile,
            ChallengeRating = cr,
            ArmorClass = RoundMedian(row.ArmorClass),
            SaveBonus = RoundMedian(row.MeanSaveBonus),
            RowText = $"{edition} SRD monster medians for CR {cr}",
            Basis = basis,
            SaveBonusText = row.IsInterpolated
                ? $"the {edition} SRD monsters' median mean save bonus interpolated to CR {cr} ({mean}, rounded)"
                : $"the median of the {edition} SRD's CR {cr} monsters' mean save bonuses ({mean}, rounded)",
            EmpiricalRow = row,
        };
    }
}
