using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// <c>balance_dpr</c>'s arguments as the host binds them: the spec classes directly, the rest raw, so every check and its
/// message lives here where <c>DndMcp.Tests</c> can pin it.
/// </summary>
public sealed record DprRequest
{
    public required BuildSpec Build { get; init; }

    public TargetSpec? Target { get; init; }

    /// <summary>
    /// The stat block <see cref="TargetSpec.Monster"/> names, looked up by the host (the Domain cannot resolve names);
    /// required when the target names one (<see cref="TargetResolver.Resolve"/>). The same creature at every level.
    /// </summary>
    public Simulation.StatBlock? TargetMonster { get; init; }

    public RulingsSpec? Rulings { get; init; }

    /// <summary>Levels to evaluate (1–20; duplicates and order ignored); null or empty: the build's own level.</summary>
    public IReadOnlyList<int>? Levels { get; init; }

    /// <summary>[low, high] target ACs for a level × AC grid, or null.</summary>
    public IReadOnlyList<int>? AcRange { get; init; }

    /// <summary><see cref="DprHorizons"/> value; null: fight.</summary>
    public string? Horizon { get; init; }

    public int? Rounds { get; init; }

    public string? RestPreset { get; init; }

    public double? EncountersPerDay { get; init; }

    public int? ShortRests { get; init; }

    /// <summary>The call's work budget (<see cref="DprLimits.WorkBudget"/>); lowered only by tests, to reach the overrun message.</summary>
    internal long WorkBudget { get; init; } = DprLimits.WorkBudget;
}

/// <summary>What a level curve marks, as wire values.</summary>
public static class LevelMarkKinds
{
    /// <summary>
    /// An attack's own count rose since the previous level evaluated (Extra Attack). A cantrip's beams rising is a
    /// <see cref="CantripUpgrade"/>, not an Extra Attack: the rules terms a model repeats to a user must be right.
    /// </summary>
    public const string ExtraAttack = "extra_attack";

    /// <summary>A cantrip's scaling rose (the 2024 spells' "Cantrip Upgrade"): Eldritch Blast's beams, Fire Bolt's dice.</summary>
    public const string CantripUpgrade = "cantrip_upgrade";

    /// <summary>An ability score rose since the previous level evaluated.</summary>
    public const string Asi = "asi";
}

/// <summary>A jump in the curve at <see cref="Level"/>, relative to the previous level evaluated.</summary>
/// <param name="Kind">A <see cref="LevelMarkKinds"/> value.</param>
/// <param name="Text">"Extra Attack: Greatsword 1 → 2 attacks", "Cantrip Upgrade: Eldritch Blast 1 → 2 beams", "ASI: Str 18 → 20".</param>
public sealed record LevelMark(int Level, string Kind, string Text);

/// <summary>One evaluated level: the build and target at that level, the horizon's result, and the reference values.</summary>
public sealed record LevelResult(int Level, HorizonResult Result, ReferencePoint Reference)
{
    public ResolvedBuild Build => Result.Evaluation.Build;

    public ResolvedTarget Target => Result.Evaluation.Target;

    public double DamagePerRound => Result.DamagePerRound;
}

/// <summary>
/// The level × AC matrix of <c>ac_range</c>: each level's build against its target with the AC replaced (cover, saves and
/// everything else as the target spec gives them at that level).
/// </summary>
public sealed record AcGrid(IReadOnlyList<int> ArmorClasses, IReadOnlyList<AcGridRow> Rows);

public sealed record AcGridRow(int Level, IReadOnlyList<AcGridCell> Cells);

/// <summary>One cell of the grid.</summary>
/// <param name="PowerAttackOnChance">
/// Each power attack's on-chance per round, in build order (empty without one): where the −5/+10 switches off, which is
/// the research table's "(no PA)".
/// </param>
public sealed record AcGridCell(int ArmorClass, double DamagePerRound, IReadOnlyList<double> PowerAttackOnChance);

/// <summary>
/// Everything <c>balance_dpr</c> reports: the horizon echoed, each level evaluated (with the full breakdown and all
/// three horizons at <see cref="DetailLevel"/>), the curve's marks, the AC grid, and notes.
/// </summary>
public sealed record DprReport
{
    public required HorizonSettings Horizon { get; init; }

    /// <summary>The levels evaluated, ascending.</summary>
    public required IReadOnlyList<LevelResult> Levels { get; init; }

    /// <summary>
    /// The level whose result carries the round-1 distribution and is shown in full: the build's own level when it was
    /// evaluated, otherwise the lowest level.
    /// </summary>
    public required int DetailLevel { get; init; }

    public LevelResult Detail => Levels.First(l => l.Level == DetailLevel);

    /// <summary>The detail level on all three horizons: round 1, the fight and the day side by side.</summary>
    public required HorizonSummary Summary { get; init; }

    /// <summary>Extra Attack and ASI jumps along the curve, by level.</summary>
    public required IReadOnlyList<LevelMark> Marks { get; init; }

    /// <summary>The level × AC grid, or null without <c>ac_range</c>.</summary>
    public AcGrid? Grid { get; init; }

    /// <summary>
    /// Call-level notes: a build that does not scale with level evaluated at other levels. Each level's own notes are in
    /// its <see cref="DprResult.Notes"/> (and the day's in <see cref="DayResult.Notes"/>).
    /// </summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Engine runs the call made (diagnostic).</summary>
    public required int Runs { get; init; }
}

/// <summary>
/// <c>balance_dpr</c> (contract §4.5): a build's damage per round at one level or along a level curve, optionally over a
/// grid of target ACs, on the round1, fight or day horizon.
///
/// <para>
/// <b>Per level</b> the build is resolved at that level and fights the target at that level: the target spec's fixed AC
/// or CR if it gives one, otherwise the DMG row for CR = level, whose AC rises with level. Validation covers every level
/// first (<see cref="BuildResolver"/>), so a curve fails up front or not at all.
/// </para>
/// <para>
/// <b>Marks and the scaling warning</b> come from the resolved builds, not the spec's text: an attack's count rising is an
/// "Extra Attack" (a cantrip's beams included), an ability score rising an "ASI". A build that does not change with level
/// changes only its proficiency bonus across the curve, which understates any real character badly (no Extra Attack, no
/// ASI), so evaluating one at other levels says so.
/// </para>
/// <para>
/// <b>One budget for the call</b> (<see cref="DprLimits.WorkBudget"/>): the grid's cells, the levels and the day
/// horizon's fight runs share it, and running out names what the call asked for.
/// </para>
/// </summary>
public static class DprAnalysis
{
    /// <summary>Evaluates the request.</summary>
    /// <exception cref="DndInputException">Any argument or the build or target is not valid, or the call is too large.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static DprReport Analyze(DprRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Build);

        var horizon = HorizonSettings.Resolve(request.Horizon, request.Rounds, request.RestPreset, request.EncountersPerDay, request.ShortRests);
        var levels = DistinctLevels(request.Levels);
        var armorClasses = AcRange(request.AcRange);
        TargetResolver.Validate(request.Target, request.TargetMonster);
        var builds = BuildResolver.Resolve(request.Build, levels, request.Rulings, "build");
        if (armorClasses is not null && builds.Count * armorClasses.Count > DprLimits.MaxGridCells)
        {
            throw new DndInputException(
                $"{builds.Count} levels × {armorClasses.Count} ACs is {builds.Count * armorClasses.Count} grid cells; at most " +
                $"{DprLimits.MaxGridCells} are evaluated at once. Give fewer levels or a narrower ac_range.");
        }

        var ownLevel = request.Build.Level ?? builds[0].Level;
        var detailLevel = builds.Any(b => b.Level == ownLevel) ? ownLevel : builds[0].Level;
        var runner = new DprRunner(cancellationToken, request.WorkBudget);
        var results = new List<LevelResult>(builds.Count);

        // The reference curves follow the target's profile (the typical CR = level monster it names); a stat block target
        // takes none, so it is read against the default.
        var referenceProfile = request.TargetMonster is null
            ? CompiledBuild.Match(DslValues.Profiles.Set, request.Target?.Profile) ?? DslValues.Profiles.Default
            : DslValues.Profiles.Default;
        AcGrid? grid = null;
        HorizonSummary summary;
        try
        {
            foreach (var build in builds)
            {
                var target = TargetResolver.Resolve(request.Target, build.Level, request.TargetMonster);
                var result = HorizonEvaluator.Full(build, target, horizon, build.Level == detailLevel, runner);
                results.Add(new LevelResult(build.Level, result, ReferenceCurves.At(build.Level, referenceProfile)));
            }

            summary = HorizonEvaluator.Summary(results.First(r => r.Level == detailLevel).Result, runner);
            if (armorClasses is not null)
            {
                grid = Grid(results, armorClasses, horizon, runner);
            }
        }
        catch (WorkBudgetExceededException ex)
        {
            // The runs the call asked for (the summary's are the detail level's own business): with more than one, fewer
            // levels, ACs or the fight horizon is the cheap fix; with one, only the build can shrink.
            var planned = builds.Sum(b => HorizonEvaluator.FullRuns(b, horizon) + ((armorClasses?.Count ?? 0) * HorizonEvaluator.HeadlineRuns(b, horizon)));
            throw planned > 1 ? DprLimits.RequestTooLarge(Describe(builds, armorClasses, horizon), ex) : DprLimits.BuildTooLarge(ex);
        }

        return new DprReport
        {
            Horizon = horizon,
            Levels = results,
            DetailLevel = detailLevel,
            Summary = summary,
            Marks = Marks(builds),
            Grid = grid,
            Notes = ScalingNotes(builds, ownLevel),
            Runs = runner.Runs,
        };
    }

    /// <summary>The levels to evaluate: null (the build's own) for none, else distinct and ascending; the resolver checks the range.</summary>
    internal static IReadOnlyList<int>? DistinctLevels(IReadOnlyList<int>? levels) =>
        levels is null || levels.Count == 0 ? null : levels.Distinct().Order().ToList();

    /// <summary>The ACs of <c>ac_range</c> [low, high], or null when it is absent or empty.</summary>
    /// <exception cref="DndInputException">Not two values, an AC outside 1–40, backwards, or wider than 16 ACs.</exception>
    internal static IReadOnlyList<int>? AcRange(IReadOnlyList<int>? range)
    {
        if (range is null || range.Count == 0)
        {
            return null;
        }

        var example = $"e.g. [13, 19] (at most {DprLimits.MaxAcRangeWidth + 1} ACs)";
        if (range.Count != 2)
        {
            throw new DndInputException($"ac_range has {Text(range.Count)} value{(range.Count == 1 ? "" : "s")}; give two, [low, high], {example}.");
        }

        var (low, high) = (range[0], range[1]);
        foreach (var ac in range)
        {
            if (ac is < DslLimits.MinTargetAc or > DslLimits.MaxTargetAc)
            {
                throw new DndInputException(
                    $"ac_range: AC {Text(ac)} is outside {Text(DslLimits.MinTargetAc)} to {Text(DslLimits.MaxTargetAc)}; give [low, high], {example}.");
            }
        }

        if (low > high)
        {
            throw new DndInputException($"ac_range [{Text(low)}, {Text(high)}] runs backwards; give [low, high], e.g. [{Text(high)}, {Text(low)}].");
        }

        if (high - low > DprLimits.MaxAcRangeWidth)
        {
            throw new DndInputException(
                $"ac_range [{Text(low)}, {Text(high)}] spans {Text(high - low + 1)} ACs; at most {Text(DprLimits.MaxAcRangeWidth + 1)} " +
                $"(high − low ≤ {Text(DprLimits.MaxAcRangeWidth)}), e.g. [{Text(low)}, {Text(low + DprLimits.MaxAcRangeWidth)}].");
        }

        return Enumerable.Range(low, high - low + 1).ToList();
    }

    /// <summary>
    /// The curve's jumps from one evaluated level to the next: an attack whose own count rose (Extra Attack), a cantrip
    /// whose scaling rose (Cantrip Upgrade: more beams or more dice), and the ability scores that rose (an ASI or a feat's
    /// +1). Compared on the resolved builds, so a step value, a from_level and a preset all count.
    /// </summary>
    public static IReadOnlyList<LevelMark> Marks(IReadOnlyList<ResolvedBuild> builds)
    {
        ArgumentNullException.ThrowIfNull(builds);
        var marks = new List<LevelMark>();
        for (var i = 1; i < builds.Count; i++)
        {
            var (before, after) = (builds[i - 1], builds[i]);
            foreach (var attack in after.Attacks)
            {
                if (before.FindAttack(attack.Name) is not { } earlier)
                {
                    continue;
                }

                var (ownBefore, ownAfter) = (OwnCount(earlier), OwnCount(attack));
                if (ownAfter > ownBefore)
                {
                    marks.Add(new LevelMark(after.Level, LevelMarkKinds.ExtraAttack, $"Extra Attack: {attack.Name} {Text(ownBefore)} → {Text(ownAfter)} attacks"));
                }

                if (attack.Cantrip is { } cantrip && attack.CantripMultiplier > earlier.CantripMultiplier)
                {
                    marks.Add(new LevelMark(after.Level, LevelMarkKinds.CantripUpgrade, cantrip == DslValues.Cantrips.Beams
                        ? $"Cantrip Upgrade: {attack.Name} {Text(earlier.Count)} → {Text(attack.Count)} beams"
                        : $"Cantrip Upgrade: {attack.Name} {earlier.Damage.Text} → {attack.Damage.Text}"));
                }
            }

            var rises = DslValues.Abilities.All
                .Where(a => after.Abilities.Score(a) > before.Abilities.Score(a))
                .Select(a => $"{Capitalized(a)} {Text(before.Abilities.Score(a))} → {Text(after.Abilities.Score(a))}")
                .ToList();
            if (rises.Count > 0)
            {
                marks.Add(new LevelMark(after.Level, LevelMarkKinds.Asi, $"ASI: {string.Join(", ", rises)}"));
            }
        }

        return marks;
    }

    /// <summary>
    /// The warning for a build that does not change with level, evaluated at a level other than its own: across the curve
    /// only its proficiency bonus moves (or nothing, with a fixed proficiency_bonus), while the default target's AC rises.
    /// </summary>
    internal static IReadOnlyList<string> ScalingNotes(IReadOnlyList<ResolvedBuild> builds, int ownLevel)
    {
        if (builds.Count == 0 || builds[0].ScalesWithLevel || builds.All(b => b.Level == ownLevel))
        {
            return [];
        }

        var changes = builds.Select(b => b.ProficiencyBonus).Distinct().Count() > 1
            ? "only its proficiency bonus changes"
            : "nothing about it changes (its proficiency_bonus is fixed)";
        return
        [
            $"{builds[0].Name} does not change with level (no step values, from_level/until_level or cantrip scaling), so across " +
            $"these levels {changes}, and a real character gains attacks, ability scores and features: this curve understates one. Give " +
            "step values, e.g. \"count\": {\"1\": 1, \"5\": 2} for Extra Attack and \"str\": {\"1\": 16, \"4\": 18, \"8\": 20} for ASIs.",
        ];
    }

    private static AcGrid Grid(IReadOnlyList<LevelResult> levels, IReadOnlyList<int> armorClasses, HorizonSettings horizon, DprRunner runner)
    {
        var rows = new List<AcGridRow>(levels.Count);
        foreach (var level in levels)
        {
            var cells = new List<AcGridCell>(armorClasses.Count);
            foreach (var ac in armorClasses)
            {
                var headline = HorizonEvaluator.Headline(level.Build, level.Target with { ArmorClass = ac }, horizon, runner);
                cells.Add(new AcGridCell(ac, headline.DamagePerRound, headline.PowerAttackOnChance));
            }

            rows.Add(new AcGridRow(level.Level, cells));
        }

        return new AcGrid(armorClasses, rows);
    }

    /// <summary>"20 levels and a 20 × 16 AC grid on the day horizon (up to 4 fight runs each)": what an overrun reports.</summary>
    private static string Describe(IReadOnlyList<ResolvedBuild> builds, IReadOnlyList<int>? armorClasses, HorizonSettings horizon)
    {
        var what = DprRunner.Count(builds.Count, "level");
        if (armorClasses is not null)
        {
            what += $" and a {Text(builds.Count)} × {Text(armorClasses.Count)} AC grid";
        }

        what += $" on the {horizon.Horizon} horizon";
        if (horizon.Horizon == DprHorizons.Day)
        {
            what += $" (up to {Text(builds.Max(b => HorizonEvaluator.FullRuns(b, horizon)))} fight runs each)";
        }

        return what;
    }

    private static string Capitalized(string ability) => char.ToUpperInvariant(ability[0]) + ability[1..];

    // The attack's count before a cantrip's beams multiply it.
    private static int OwnCount(ResolvedAttack attack) =>
        attack.Cantrip == DslValues.Cantrips.Beams ? attack.Count / attack.CantripMultiplier : attack.Count;

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
