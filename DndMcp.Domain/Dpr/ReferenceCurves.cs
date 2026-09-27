using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Probability;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// What a character of level L "should" deal, by the two community conventions research B3 uses (contract §4.7), and
/// the per-level target table built from them: the yardsticks a DPR number is read against, and the fallback slope for
/// level-equivalents (<see cref="LevelEquivalents"/>).
///
/// <para>
/// <b>RPGBOT's target</b> is the top of the DMG 2014 hit-point range for a CR = L monster ÷ 12: a party of four killing it
/// in three rounds. It needs no evaluation, so it is the slope a level-equivalent falls back to when the baseline's own
/// curve is flat or cannot be evaluated.
/// </para>
/// <para>
/// <b>The warlock baseline</b> (Form of Dread's "Warlock Baseline") is the <c>warlock_baseline</c> preset evaluated by this
/// engine against the CR = L row, not a typed-in curve: the table the model reads and the maths behind every comparison
/// cannot disagree, and the preset's published values (6.30 … 38.20) are pinned by the tests. The preset has no resources
/// and no setup cost, so its round-1, fight and day figures are the same number.
/// </para>
/// <para>
/// Neither is a rule: both are conventions, and <see cref="RpgbotSource"/> and <see cref="WarlockSource"/> say whose.
/// </para>
/// </summary>
public static class ReferenceCurves
{
    public const string RpgbotSource =
        "RPGBOT's DPR target: the top of the DMG 2014 hit-point range for a monster of CR = level, ÷ 12 (a party of four " +
        "ending the fight in three rounds); a community convention, not a rule";

    public const string WarlockSource =
        "the community \"Warlock Baseline\" (Form of Dread): Eldritch Blast with Agonizing Blast and Hex, Cha 16/18/20 at " +
        "levels 1/4/8, against the DMG 2014 row for CR = level; evaluated by this engine from the warlock_baseline preset";

    private static readonly Lazy<IReadOnlyList<DprTargetRow>> Rows = new(BuildRows);

    /// <summary>The DMG 2014 "Monster Statistics by Challenge Rating" row for CR = <paramref name="level"/>.</summary>
    public static MonsterStatsRow Row(int level)
    {
        CheckLevel(level);
        return ChallengeRatingTables.MonsterStats(ChallengeRating.FromNumber(level)!.Value);
    }

    /// <summary>RPGBOT's target DPR at a level: the CR = level row's maximum hit points ÷ 12 (level 5: 145 ÷ 12 = 12.08).</summary>
    public static double RpgbotTarget(int level) => Row(level).HitPointsMax / 12.0;

    /// <summary>The warlock baseline's DPR at a level (level 5: 17.80), against the CR = level row.</summary>
    public static double WarlockBaseline(int level)
    {
        CheckLevel(level);
        return Rows.Value[level - 1].WarlockBaseline;
    }

    /// <summary>Both reference values at a level.</summary>
    public static ReferencePoint At(int level) => new(level, RpgbotTarget(level), WarlockBaseline(level));

    /// <summary>
    /// Levels 1–20: the target a level's DPR is measured against (the CR = level row's AC, attack bonus, save DC and
    /// maximum HP, the typical save bonus) with both reference curves: the <c>rules://tables/dpr-targets-by-level</c>
    /// table. Computed once, on first use (20 fight evaluations of the preset, well under a second).
    /// </summary>
    public static IReadOnlyList<DprTargetRow> TargetsByLevel => Rows.Value;

    private static IReadOnlyList<DprTargetRow> BuildRows()
    {
        var rows = new List<DprTargetRow>(DslLimits.MaxLevel);
        for (var level = DslLimits.MinLevel; level <= DslLimits.MaxLevel; level++)
        {
            var row = Row(level);
            var build = BuildResolver.Resolve(BuildPresets.WarlockBaseline(level), level);
            var target = TargetResolver.Resolve(null, level);
            var result = DprEngine.Evaluate(build, target, DprOptions.Fight() with { IncludeDistribution = false });
            var blast = build.Attacks[0];
            rows.Add(new DprTargetRow(
                level,
                DslLimits.ProficiencyBonus(level),
                row.ArmorClass,
                row.HitPointsMax,
                row.AttackBonus,
                row.SaveDc,
                TypicalSaveBonus.For(row.ChallengeRating),
                row.HitPointsMax / 12.0,
                result.DamagePerRound,
                blast.AttackBonus,
                AttackRoll.Odds(blast.AttackBonus, row.ArmorClass, blast.CritMin, D20Options.Normal).Hit));
        }

        return rows;
    }

    private static void CheckLevel(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, DslLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, DslLimits.MaxLevel);
    }
}

/// <summary>The two reference DPR values at one level.</summary>
public sealed record ReferencePoint(int Level, double RpgbotTarget, double WarlockBaseline);

/// <summary>
/// One level of the DPR-targets table: the CR = level monster a level-L character is measured against (DMG 2014 row;
/// the save bonus from The Finished Book, see <see cref="Features.TypicalSaveBonus"/>) and the two reference curves.
/// </summary>
/// <param name="ProficiencyBonus">The character's proficiency bonus at this level.</param>
/// <param name="ArmorClass">The row's AC: the default target AC at this level.</param>
/// <param name="AttackBonus">The row's monster attack bonus.</param>
/// <param name="SaveDc">The row's monster save DC.</param>
/// <param name="TypicalSaveBonus">The default target save bonus (not a DMG column).</param>
/// <param name="WarlockAttackBonus">The warlock baseline's spell attack bonus (Cha + proficiency).</param>
/// <param name="WarlockHitChance">Its chance to hit the row's AC on a plain roll: 65% at most levels (70% at level 9).</param>
public sealed record DprTargetRow(
    int Level,
    int ProficiencyBonus,
    int ArmorClass,
    int HitPointsMax,
    int AttackBonus,
    int SaveDc,
    int TypicalSaveBonus,
    double RpgbotTarget,
    double WarlockBaseline,
    int WarlockAttackBonus,
    double WarlockHitChance);
