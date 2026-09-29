using DndMcp.Domain.Simulation;

namespace DndMcp.Domain.Encounters;

/// <summary>
/// What the SRD's own monsters look like at each Challenge Rating, per edition: the medians of their armour class, hit
/// points, best attack bonus, best save DC and mean save bonus, computed from the normalized stat blocks of every SRD
/// monster (334 in 2014, 341 in 2024). The empirical counterpart of <see cref="ChallengeRatingTables.MonsterStats"/>
/// (the DMG 2014 "Monster Statistics by Challenge Rating", a design target rather than a census).
///
/// <para>
/// <b>Why it exists.</b> PLAN Open question 6 asked whether 2024 monsters have lower AC than 2014's (one source) or +1
/// (another). The DMG 2014 table answers neither: it is what monsters should be built to, and the 2024 DMG has no
/// replacement. This table is what the SRD monsters ARE, so a balance target of "a CR 5 monster" can mean the SRD's
/// typical CR 5 monster of the edition being played (<c>target.profile "mm2014"</c> / <c>"mm2024"</c>).
/// </para>
/// <para>
/// <b>Pinned, not computed at runtime.</b> Domain has no data access; the rows below are literals, and
/// <c>MonsterStatsEmpiricalTests</c> recomputes every cell with <see cref="Compute"/> from the normalized vendored data
/// and fails on any difference. A re-vendor or a normalizer change that moves a median is therefore a reviewed diff,
/// never a silent drift.
/// </para>
/// <para>
/// <b>How each monster is counted</b> (the choices a reader needs to trust a median):
/// <list type="bullet">
/// <item>A shapechanger's forms (lycanthropes and vampires: three records each, linked by <see cref="StatBlock.Forms"/>)
/// count once, as the form it fights in: the <c>-hybrid</c> record, else the record whose slug repeats its name
/// (<c>vampire-vampire</c>). Other variants (Giant Rat and Giant Rat (Diseased), swarms) are separate stat blocks and
/// count separately.</item>
/// <item>Best attack bonus: the highest <see cref="StatBlockAction.AttackBonus"/> among its attacks in
/// <see cref="StatBlock.Actions"/>, <see cref="StatBlock.BonusActions"/> and <see cref="StatBlock.Spells"/>
/// (spell attacks count; legendary actions and reactions do not). A monster with no attack is left out of that
/// median, and the row's <see cref="MonsterStatsEmpiricalRow.AttackCount"/> says how many had one.</item>
/// <item>Best save DC: the highest DC among its save actions and spells in those same lists and the saves its attacks
/// impose on a hit. A monster with none is left out of that median (<see cref="MonsterStatsEmpiricalRow.SaveDcCount"/>).
/// Computed DCs (Undead Fortitude) are not actions and never count.</item>
/// <item>Mean save bonus: the mean of its six save bonuses, then the median of those means.</item>
/// <item>Hit points: the average the stat block prints. AC: its default (first) AC.</item>
/// <item>A median of an even count is the mean of the middle two, so a cell can end in .5.</item>
/// </list>
/// </para>
/// </summary>
public static class MonsterStatsEmpirical
{
    public const string Source =
        "Medians of the SRD 5.1 (2014) or SRD 5.2.1 (2024) monsters of that Challenge Rating, as this server normalizes them";

    /// <summary>The rows of one edition, lowest CR first, only CRs at which the SRD has a monster.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The edition is not "2014" or "2024".</exception>
    public static IReadOnlyList<MonsterStatsEmpiricalRow> Rows(string edition) => edition switch
    {
        "2014" => Rows2014,
        "2024" => Rows2024,
        _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Only 2014 and 2024 have SRD monsters."),
    };

    /// <summary>
    /// The row for a CR, mirroring <see cref="ChallengeRatingTables.MonsterStats"/>: always a row. Where the SRD has no
    /// monster of that CR, each value is interpolated linearly (by CR) between the nearest rows below and above, or
    /// taken from the nearest row at either end, and <see cref="MonsterStatsEmpiricalRow.IsInterpolated"/> is set with a
    /// <see cref="MonsterStatsEmpiricalRow.Count"/> of 0, so a caller can say the value is not a census.
    /// </summary>
    public static MonsterStatsEmpiricalRow MonsterStats(string edition, ChallengeRating cr)
    {
        var rows = Rows(edition);
        if (rows.FirstOrDefault(r => r.ChallengeRating == cr) is { } exact)
        {
            return exact;
        }

        var below = rows.LastOrDefault(r => r.ChallengeRating < cr);
        var above = rows.FirstOrDefault(r => r.ChallengeRating > cr);
        if (below is null || above is null)
        {
            var nearest = below ?? above!;
            return nearest with { ChallengeRating = cr, Count = 0, AttackCount = 0, SaveDcCount = 0, IsInterpolated = true };
        }

        // Each value between the nearest rows that have it: CR 19's single SRD monster has no save DC to interpolate from.
        double Between(Func<MonsterStatsEmpiricalRow, double> value, Func<MonsterStatsEmpiricalRow, int> count)
        {
            var low = rows.LastOrDefault(r => r.ChallengeRating < cr && count(r) > 0);
            var high = rows.FirstOrDefault(r => r.ChallengeRating > cr && count(r) > 0);
            if (low is null || high is null)
            {
                return value((low ?? high)!);
            }

            var t = (cr.Value - low.ChallengeRating.Value) / (high.ChallengeRating.Value - low.ChallengeRating.Value);
            return Math.Round(value(low) + (value(high) - value(low)) * t, 2);
        }

        return new MonsterStatsEmpiricalRow(
            cr,
            0,
            Between(r => r.ArmorClass, r => r.Count),
            Between(r => r.HitPoints, r => r.Count),
            Between(r => r.AttackBonus, r => r.AttackCount),
            0,
            Between(r => r.SaveDc, r => r.SaveDcCount),
            0,
            Between(r => r.MeanSaveBonus, r => r.Count))
        {
            IsInterpolated = true,
        };
    }

    /// <summary>
    /// The rows the stat blocks of one edition give, by the rules on the type. What the pinned rows were generated with,
    /// and what the tests hold them to.
    /// </summary>
    public static IReadOnlyList<MonsterStatsEmpiricalRow> Compute(IEnumerable<StatBlock> statBlocks)
    {
        var blocks = statBlocks.ToList();
        var byRef = blocks.ToDictionary(b => b.Ref, StringComparer.Ordinal);
        var counted = blocks.Where(b => b.Forms.Count == 0 || Canonical(b, byRef) == b.Ref).ToList();
        return counted
            .GroupBy(b => b.ChallengeRating)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var attacks = g.Select(BestAttackBonus).OfType<int>().Select(v => (double)v).ToList();
                var dcs = g.Select(BestSaveDc).OfType<int>().Select(v => (double)v).ToList();
                return new MonsterStatsEmpiricalRow(
                    g.Key,
                    g.Count(),
                    Median(g.Select(b => (double)b.ArmorClass)),
                    Median(g.Select(b => (double)b.HitPoints)),
                    attacks.Count == 0 ? 0 : Median(attacks),
                    attacks.Count,
                    dcs.Count == 0 ? 0 : Median(dcs),
                    dcs.Count,
                    Math.Round(Median(g.Select(b => b.SaveBonuses.Values.Average())), 4));
            })
            .ToList();
    }

    /// <summary>The highest attack bonus among its actions, bonus actions and spells, or null when it has no attack.</summary>
    public static int? BestAttackBonus(StatBlock block)
    {
        var bonuses = Offensive(block)
            .Where(a => a.Kind == StatBlockValues.ActionKinds.Attack && a.AttackBonus is not null)
            .Select(a => a.AttackBonus!.Value)
            .ToList();
        return bonuses.Count == 0 ? null : bonuses.Max();
    }

    /// <summary>The highest save DC among its save actions and spells and its attacks' on-hit saves, or null.</summary>
    public static int? BestSaveDc(StatBlock block)
    {
        var dcs = Offensive(block)
            .SelectMany(a => a.OnHit.Select(e => e.Save).Append(a.Kind == StatBlockValues.ActionKinds.Save ? a.Save : null))
            .OfType<SaveSpec>()
            .Where(s => s.Dc > 0)
            .Select(s => s.Dc)
            .ToList();
        return dcs.Count == 0 ? null : dcs.Max();
    }

    private static IEnumerable<StatBlockAction> Offensive(StatBlock block) => block.Actions.Concat(block.BonusActions).Concat(block.Spells);

    // The form a shapechanger's group is counted as: the hybrid, else the form whose slug repeats its name.
    private static string Canonical(StatBlock block, IReadOnlyDictionary<string, StatBlock> byRef)
    {
        var group = block.Forms.Append(block.Ref).Where(byRef.ContainsKey).Order(StringComparer.Ordinal).ToList();
        string Slug(string reference) => reference[(reference.LastIndexOf('/') + 1)..];
        return group.FirstOrDefault(r => Slug(r).EndsWith("-hybrid", StringComparison.Ordinal))
               ?? group.FirstOrDefault(r => Slug(r).Split('-') is [var a, var b] && a == b)
               ?? group[0];
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    // The pinned rows: MonsterStatsEmpiricalTests regenerates them from the vendored data and prints this block when
    // they differ. Rows are table rows (ChallengeRating.All[row]); CRs with no SRD monster have none.
    private static readonly MonsterStatsEmpiricalRow[] Rows2014 =
    [
        new(ChallengeRating.All[0], 29, 11, 3, 2, 26, 9, 3, -1.3333),
        new(ChallengeRating.All[1], 19, 12, 9, 4, 19, 10.5, 2, -0.6667),
        new(ChallengeRating.All[2], 32, 12, 13, 4, 32, 11, 10, -0.3333),
        new(ChallengeRating.All[3], 33, 12, 22, 4, 33, 11, 7, -0.1667),
        new(ChallengeRating.All[4], 25, 12, 26, 4, 25, 11, 10, 0.3333),
        new(ChallengeRating.All[5], 41, 13, 45, 5, 41, 12, 14, 0.5),
        new(ChallengeRating.All[6], 20, 14, 58, 5, 20, 12, 9, 1.0833),
        new(ChallengeRating.All[7], 11, 12, 85, 5, 11, 13, 5, 0.6667),
        new(ChallengeRating.All[8], 25, 15, 95, 7, 25, 15, 8, 0.8333),
        new(ChallengeRating.All[9], 10, 14.5, 112, 6.5, 10, 14.5, 6, 1.8333),
        new(ChallengeRating.All[10], 6, 17, 126.5, 7, 6, 14, 4, 3.8333),
        new(ChallengeRating.All[11], 10, 15, 136, 7.5, 10, 14, 5, 3.25),
        new(ChallengeRating.All[12], 8, 17.5, 154.5, 9.5, 8, 16, 3, 5),
        new(ChallengeRating.All[13], 6, 17.5, 157, 9.5, 6, 16.5, 4, 5.5),
        new(ChallengeRating.All[14], 7, 17, 178, 10, 7, 16, 3, 4.1667),
        new(ChallengeRating.All[15], 2, 15, 126, 8.5, 2, 15.5, 2, 4.6667),
        new(ChallengeRating.All[16], 6, 17, 178, 10.5, 6, 17, 5, 6.0833),
        new(ChallengeRating.All[17], 3, 18, 184, 11, 3, 18, 3, 6.8333),
        new(ChallengeRating.All[18], 4, 18.5, 209.5, 11.5, 4, 18.5, 4, 6.25),
        new(ChallengeRating.All[19], 5, 19, 210, 12, 5, 19.5, 4, 7.3333),
        new(ChallengeRating.All[20], 4, 19, 256, 13.5, 4, 20.5, 4, 8),
        new(ChallengeRating.All[22], 1, 19, 262, 14, 1, 0, 0, 9),
        new(ChallengeRating.All[23], 3, 20, 300, 14, 3, 21, 3, 8),
        new(ChallengeRating.All[24], 4, 21, 296.5, 15, 4, 22, 4, 9.1667),
        new(ChallengeRating.All[25], 2, 21.5, 414.5, 15.5, 2, 22.5, 2, 9.4167),
        new(ChallengeRating.All[26], 3, 22, 481, 17, 3, 23, 3, 9.8333),
        new(ChallengeRating.All[27], 2, 22, 546, 17, 2, 24, 2, 10.3333),
        new(ChallengeRating.All[33], 1, 25, 676, 19, 1, 20, 1, 7.1667),
    ];

    private static readonly MonsterStatsEmpiricalRow[] Rows2024 =
    [
        new(ChallengeRating.All[0], 28, 11.5, 3, 2, 26, 12, 1, -1.3333),
        new(ChallengeRating.All[1], 19, 12, 9, 4, 19, 0, 0, -0.1667),
        new(ChallengeRating.All[2], 32, 12, 13, 4, 32, 11, 5, -0.3333),
        new(ChallengeRating.All[3], 27, 12, 21, 4, 27, 11, 5, -0.1667),
        new(ChallengeRating.All[4], 27, 13, 26, 5, 27, 11, 10, 0.3333),
        new(ChallengeRating.All[5], 42, 13, 45, 5, 42, 12, 16, 0.6667),
        new(ChallengeRating.All[6], 25, 15, 65, 5, 25, 12, 9, 1.1667),
        new(ChallengeRating.All[7], 16, 15, 76, 6, 16, 14, 8, 1.1667),
        new(ChallengeRating.All[8], 25, 15, 104, 7, 25, 14.5, 12, 0.8333),
        new(ChallengeRating.All[9], 11, 15, 123, 7, 11, 14, 10, 2.1667),
        new(ChallengeRating.All[10], 6, 17, 126.5, 7, 6, 14, 3, 3.1667),
        new(ChallengeRating.All[11], 10, 15.5, 136, 7, 10, 14, 5, 3.1667),
        new(ChallengeRating.All[12], 8, 16.5, 161.5, 9.5, 8, 17, 3, 4.3333),
        new(ChallengeRating.All[13], 6, 18, 178, 9.5, 6, 16.5, 4, 5.1667),
        new(ChallengeRating.All[14], 7, 17, 199, 10, 7, 17, 6, 3.5),
        new(ChallengeRating.All[15], 2, 17.5, 174, 8.5, 2, 16.5, 2, 4.3333),
        new(ChallengeRating.All[16], 6, 17.5, 197.5, 10.5, 6, 18, 6, 5.4167),
        new(ChallengeRating.All[17], 3, 18, 195, 11, 3, 18, 3, 5.3333),
        new(ChallengeRating.All[18], 4, 18, 209.5, 11.5, 4, 18.5, 4, 4.6667),
        new(ChallengeRating.All[19], 5, 19, 220, 12, 5, 19, 5, 5.8333),
        new(ChallengeRating.All[20], 4, 19, 249.5, 13.5, 4, 20.5, 4, 6.5),
        new(ChallengeRating.All[22], 1, 19, 287, 14, 1, 0, 0, 7),
        new(ChallengeRating.All[23], 3, 20, 333, 14, 3, 21, 3, 6.3333),
        new(ChallengeRating.All[24], 4, 21, 341, 15, 4, 21.5, 4, 7.3333),
        new(ChallengeRating.All[25], 2, 21.5, 423, 15.5, 2, 22.5, 2, 7.4167),
        new(ChallengeRating.All[26], 3, 22, 481, 17, 3, 24, 3, 7.8333),
        new(ChallengeRating.All[27], 2, 22, 526.5, 17, 2, 24, 2, 8.1667),
        new(ChallengeRating.All[33], 1, 25, 697, 19, 1, 27, 1, 8.6667),
    ];
}

/// <summary>
/// One CR's medians (<see cref="MonsterStatsEmpirical"/>). <see cref="AttackBonus"/> and <see cref="SaveDc"/> are
/// medians over the <see cref="AttackCount"/> / <see cref="SaveDcCount"/> monsters that have one, and 0 when none does
/// (2024 CR 1/8 has no save DC, CR 19 in both editions none): read them only with their count.
/// </summary>
public sealed record MonsterStatsEmpiricalRow(
    ChallengeRating ChallengeRating,
    int Count,
    double ArmorClass,
    double HitPoints,
    double AttackBonus,
    int AttackCount,
    double SaveDc,
    int SaveDcCount,
    double MeanSaveBonus)
{
    /// <summary>True for a row <see cref="MonsterStatsEmpirical.MonsterStats"/> made up for a CR with no SRD monster.</summary>
    public bool IsInterpolated { get; init; }
}
