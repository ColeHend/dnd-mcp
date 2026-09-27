using System.Globalization;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// One build against one target over one horizon (contract §4.4): the headline and the run behind it.
///
/// <para>
/// <b>Which run is <see cref="Evaluation"/>.</b> For round1 and fight it is that run. For the day horizon it is the full
/// build over one fight from fresh resources (what a single fight of the day looks like, limited features included),
/// while <see cref="Day"/> holds the amortization that produced the headline. So the breakdown always shows every
/// attack line and rider the build has, and the day block shows the arithmetic from the fight without limited features
/// to the day's figure.
/// </para>
/// </summary>
public sealed record HorizonResult
{
    /// <summary>The horizon and its parameters, echoed.</summary>
    public required HorizonSettings Horizon { get; init; }

    /// <summary>The headline: E[damage] per round over the horizon.</summary>
    public required double DamagePerRound { get; init; }

    /// <summary>The run whose breakdown a result shows (see the type's remarks).</summary>
    public required DprResult Evaluation { get; init; }

    /// <summary>The day horizon's amortization; null for round1 and fight.</summary>
    public DayResult? Day { get; init; }

    /// <summary>E[damage] in round 1 of a fight from fresh resources: the nova, whatever the horizon.</summary>
    public double Round1Damage => Evaluation.Round1Damage;

    /// <summary>E[damage] per round over one R-round fight from fresh resources; null for the round1 horizon.</summary>
    public double? FightDamagePerRound => Horizon.Horizon == DprHorizons.Round1 ? null : Evaluation.DamagePerRound;
}

/// <summary>
/// One build against one target on all three horizons (research A9: "always report three horizons"): the nova, the
/// R-round fight from fresh resources, and the adventuring day. A result headlines one; this puts the other two beside it,
/// so a feature that shines in round 1 and fades over a day (or the reverse) is visible whatever the call asked for.
/// </summary>
/// <param name="Round1Damage">E[damage] in round 1 with every resource available: the round1 horizon.</param>
/// <param name="Rounds">R, the fight's rounds.</param>
/// <param name="FightDamagePerRound">The fight horizon's DPR.</param>
/// <param name="Day">The day the day figure assumes.</param>
/// <param name="DayDamagePerRound">The day horizon's DPR.</param>
public sealed record HorizonSummary(double Round1Damage, int Rounds, double FightDamagePerRound, DayAssumptions Day, double DayDamagePerRound)
{
    /// <summary>The figure for a <see cref="DprHorizons"/> value.</summary>
    public double For(string horizon) => horizon switch
    {
        DprHorizons.Round1 => Round1Damage,
        DprHorizons.Fight => FightDamagePerRound,
        DprHorizons.Day => DayDamagePerRound,
        _ => throw new ArgumentOutOfRangeException(nameof(horizon), horizon, "Not a horizon."),
    };
}

/// <summary>
/// The day horizon's arithmetic (contract §4.4, research A9): the fight DPR with every resource-limited feature removed,
/// plus for each such feature min(U, u·E·R) · d ÷ (E·R). U is its uses per day, u its uses per round and d its damage per
/// use, both measured in a fight where that feature alone is added back with unlimited uses.
///
/// <para>
/// <b>Why the exact marginal and not the feature's own damage.</b> d is (DPR with the feature − DPR without) ÷ u, so it
/// carries everything a use changes: Action Surge's two extra swings also raise the chance of the 2014 GWM crit bonus
/// attack, and d = 19.52 rather than the 2 × 9.35 = 18.7 of the swings alone (contract §9: research's +3.1 is +3.25).
/// </para>
/// <para>
/// <b>What it approximates.</b> Uses are spread evenly over the day's E·R rounds, capped at u a round (a feature with
/// more uses than a day wants is simply always on); a table that novas early is the fight horizon's picture. Several
/// limited features are amortized one at a time and added, so their interactions (two smites competing for one hit, two
/// Bonus Action options) are approximated as additive, and <see cref="Notes"/> says so.
/// </para>
/// </summary>
public sealed record DayResult
{
    public required DayAssumptions Assumptions { get; init; }

    /// <summary>R, the rounds in each of the day's fights.</summary>
    public required int Rounds { get; init; }

    /// <summary>The fight without any resource-limited feature: the day's floor.</summary>
    public required DprResult Base { get; init; }

    /// <summary>One entry per resource-limited feature, in build order.</summary>
    public required IReadOnlyList<DayFeature> Features { get; init; }

    /// <summary>The headline: <see cref="Base"/>'s DPR plus every feature's contribution.</summary>
    public required double DamagePerRound { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>E·R: the rounds a day's uses are spread over.</summary>
    public double RoundsPerDay => Assumptions.EncountersPerDay * Rounds;
}

/// <summary>
/// One resource-limited feature over the day: <see cref="Contribution"/> = min(U, u·E·R) · d ÷ (E·R).
/// </summary>
/// <param name="UsesPerDay">U: uses per long rest, or per short rest × (S + 1).</param>
/// <param name="UsesPerRound">u: uses per round in a fight where it never runs out (Action Surge: 1).</param>
/// <param name="DamagePerUse">d: (that fight's DPR − the base's) ÷ u; null when it is never used (u = 0).</param>
/// <param name="UsesWantedPerDay">u·E·R: the uses a day would take if it never ran out.</param>
/// <param name="UsesSpentPerDay">min(U, u·E·R).</param>
/// <param name="Contribution">What it adds to the day's damage per round.</param>
/// <param name="UnlimitedDamagePerRound">The fight DPR with this feature alone, unlimited (the run u and d come from).</param>
public sealed record DayFeature(
    ModifierRef Source,
    ResolvedResource Resource,
    int UsesPerDay,
    double UsesPerRound,
    double? DamagePerUse,
    double UsesWantedPerDay,
    double UsesSpentPerDay,
    double Contribution,
    double UnlimitedDamagePerRound)
{
    /// <summary>Whether the day runs out of it (U &lt; u·E·R), so its uses per day, not its uses per round, set its value.</summary>
    public bool LimitedByUses => UsesPerDay < UsesWantedPerDay;
}

/// <summary>
/// Evaluates a resolved build on any horizon, the day horizon included (which <see cref="DprEngine"/> leaves to this
/// layer, since it is built from several fight runs).
/// </summary>
public static class HorizonEvaluator
{
    /// <summary>
    /// The build's DPR over <paramref name="horizon"/>, with the run behind it. One budgeted meter covers every run (the day
    /// horizon's base and per-feature fights included).
    /// </summary>
    /// <param name="includeDistribution">Whether <see cref="HorizonResult.Evaluation"/> carries the round-1 distribution.</param>
    /// <exception cref="Core.DndInputException">The work budget ran out (with what to reduce).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static HorizonResult Evaluate(
        ResolvedBuild build,
        ResolvedTarget target,
        HorizonSettings horizon,
        bool includeDistribution = true,
        CancellationToken cancellationToken = default) =>
        Evaluate(build, target, horizon, includeDistribution, cancellationToken, DprLimits.WorkBudget);

    /// <summary>The same with a given budget; lowered only by tests, to reach the overrun messages.</summary>
    internal static HorizonResult Evaluate(
        ResolvedBuild build,
        ResolvedTarget target,
        HorizonSettings horizon,
        bool includeDistribution,
        CancellationToken cancellationToken,
        long budget)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(horizon);

        var runner = new DprRunner(cancellationToken, budget);
        try
        {
            return Full(build, target, horizon, includeDistribution, runner);
        }
        catch (WorkBudgetExceededException ex)
        {
            var runs = FullRuns(build, horizon);
            throw runs > 1
                ? DprLimits.RequestTooLarge($"the day horizon ({DprRunner.Count(runs, "fight run")})", ex)
                : DprLimits.BuildTooLarge(ex);
        }
    }

    /// <summary>
    /// The resource-limited features the day horizon amortizes: riders, extra attacks, save effects and conditions on hit
    /// with a resource, in build order. Defensive modifiers with a resource deal no damage and stay in every run.
    /// </summary>
    public static IReadOnlyList<(ModifierRef Source, ResolvedResource Resource)> LimitedFeatures(ResolvedBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.Riders.Where(r => r.Resource is not null).Select(r => (r.Source, r.Resource!))
            .Concat(build.ExtraAttacks.Where(e => e.Resource is not null).Select(e => (e.Source, e.Resource!)))
            .Concat(build.SaveEffects.Where(s => s.Resource is not null).Select(s => (s.Source, s.Resource!)))
            .Concat(build.ConditionsOnHit.Where(c => c.Resource is not null).Select(c => (c.Source, c.Resource!)))
            .OrderBy(f => f.Source.Number)
            .ToList();
    }

    /// <summary>Engine runs a full result takes: one, or on the day horizon the full fight, the base and one per feature.</summary>
    internal static int FullRuns(ResolvedBuild build, HorizonSettings horizon)
    {
        if (horizon.Horizon != DprHorizons.Day)
        {
            return 1;
        }

        var features = LimitedFeatures(build).Count;
        return features == 0 ? 1 : 2 + features;
    }

    /// <summary>Engine runs a headline takes: one, or on the day horizon the base and one per feature.</summary>
    internal static int HeadlineRuns(ResolvedBuild build, HorizonSettings horizon) =>
        horizon.Horizon == DprHorizons.Day ? 1 + LimitedFeatures(build).Count : 1;

    /// <summary>The full result: the horizon's own run (or, for the day, the full fight), plus the day's amortization.</summary>
    internal static HorizonResult Full(ResolvedBuild build, ResolvedTarget target, HorizonSettings horizon, bool includeDistribution, DprRunner runner)
    {
        var run = runner.Run(build, target, horizon.EngineOptions(includeDistribution));
        if (horizon.Horizon != DprHorizons.Day)
        {
            return new HorizonResult { Horizon = horizon, DamagePerRound = run.DamagePerRound, Evaluation = run };
        }

        var day = Day(build, target, horizon, runner, run);
        return new HorizonResult { Horizon = horizon, DamagePerRound = day.DamagePerRound, Evaluation = run, Day = day };
    }

    /// <summary>
    /// All three horizons for a result already computed on one of them, reusing its runs: round 1 is the fight's first
    /// round (the same turn from fresh resources), the fight is the result's own fight run (one more run for a round1
    /// result), and the day needs the base and one run per limited feature (none without limited features, when the day is
    /// the fight).
    /// </summary>
    /// <param name="fight">The result's fight run when the caller already has it (a round1 result's extra fight), or null.</param>
    internal static HorizonSummary Summary(HorizonResult result, DprRunner runner, DprResult? fight = null)
    {
        var settings = result.Horizon;
        var build = result.Evaluation.Build;
        var target = result.Evaluation.Target;
        fight ??= FightOf(result, runner);
        var day = result.Day?.DamagePerRound ?? Day(build, target, settings, runner, fight).DamagePerRound;
        return new HorizonSummary(fight.Round1Damage, settings.Rounds, fight.DamagePerRound, settings.Day, day);
    }

    /// <summary>
    /// A fight run of the result's build from fresh resources: its own run on the fight and day horizons (the day's full
    /// fight), one more run for a round1 result.
    /// </summary>
    internal static DprResult FightOf(HorizonResult result, DprRunner runner) =>
        result.Horizon.Horizon == DprHorizons.Round1
            ? runner.Run(result.Evaluation.Build, result.Evaluation.Target, DprOptions.Fight(result.Horizon.Rounds) with { IncludeDistribution = false })
            : result.Evaluation;

    /// <summary>
    /// Only the headline (a grid cell, a slope point): no distribution, and on the day horizon no full-fight run. The power
    /// attacks' on-chances come from the run the headline rests on (the day's base).
    /// </summary>
    internal static HorizonHeadline Headline(ResolvedBuild build, ResolvedTarget target, HorizonSettings horizon, DprRunner runner)
    {
        if (horizon.Horizon != DprHorizons.Day)
        {
            var run = runner.Run(build, target, horizon.EngineOptions(includeDistribution: false));
            return new HorizonHeadline(run.DamagePerRound, run.PowerAttacks.Select(p => p.OnChancePerRound).ToList());
        }

        var day = Day(build, target, horizon, runner, fullFight: null);
        return new HorizonHeadline(day.DamagePerRound, day.Base.PowerAttacks.Select(p => p.OnChancePerRound).ToList());
    }

    private static DayResult Day(ResolvedBuild build, ResolvedTarget target, HorizonSettings horizon, DprRunner runner, DprResult? fullFight)
    {
        var assumptions = horizon.Day;
        var rounds = horizon.Rounds;
        var roundsPerDay = assumptions.EncountersPerDay * rounds;
        var options = DprOptions.Fight(rounds) with { IncludeDistribution = false };
        var limited = LimitedFeatures(build);
        var numbers = limited.Select(f => f.Source.Number).ToList();

        // With nothing limited, the fight without limited features is the fight itself.
        var baseRun = limited.Count == 0 && fullFight is not null
            ? fullFight
            : runner.Run(build, target, options with { RemovedModifiers = numbers });

        var features = new List<DayFeature>();
        var notes = new List<string>();
        foreach (var (source, resource) in limited)
        {
            var alone = runner.Run(build, target, options with
            {
                RemovedModifiers = numbers.Where(n => n != source.Number).ToList(),
                UnlimitedResources = [source.Number],
            });
            var usesPerRound = alone.Resources.FirstOrDefault(r => r.Source.Number == source.Number)?.UsesPerRound ?? 0;
            double? perUse = usesPerRound > 0 ? (alone.DamagePerRound - baseRun.DamagePerRound) / usesPerRound : null;
            var perDay = assumptions.UsesPerDay(resource);
            var wanted = usesPerRound * roundsPerDay;
            var spent = Math.Min(perDay, wanted);
            var contribution = perUse is { } d ? spent * d / roundsPerDay : 0;
            features.Add(new DayFeature(source, resource, perDay, usesPerRound, perUse, wanted, spent, contribution, alone.DamagePerRound));

            if (perUse is null)
            {
                notes.Add($"{source.Label}: not used even with unlimited uses, so it adds nothing to the day.");
            }
            else if (perUse < 0)
            {
                notes.Add(FormattableString.Invariant(
                    $"{source.Label}: each use lowers the damage per round (d = {perUse.Value:0.###}); its policy spends it anyway."));
            }
        }

        var roundsText = FormattableString.Invariant($"{DayAssumptions.Format(assumptions.EncountersPerDay)} × {rounds} = {roundsPerDay:0.##}");
        if (limited.Count == 0)
        {
            notes.Insert(0, "No resource-limited features: the day horizon equals the fight horizon.");
        }
        else
        {
            notes.Insert(0,
                "Day horizon: each resource-limited feature is measured alone in a fight where it never runs out (uses per round u, " +
                $"damage per use d, over the fight without any limited feature), then its uses per day U are spread over the day's " +
                $"E × R = {roundsText} rounds: it adds min(U, u × E × R) × d ÷ (E × R).");
            if (limited.Count > 1)
            {
                notes.Insert(1,
                    "The limited features are amortized one at a time and added: their interactions (two competing for the same " +
                    "hits or the same Bonus Action) are approximated as additive.");
            }
        }

        return new DayResult
        {
            Assumptions = assumptions,
            Rounds = rounds,
            Base = baseRun,
            Features = features,
            DamagePerRound = baseRun.DamagePerRound + features.Sum(f => f.Contribution),
            Notes = notes,
        };
    }
}

/// <summary>A headline without its breakdown: a grid cell or a slope point.</summary>
/// <param name="PowerAttackOnChance">Each power attack's on-chance per round (build order), for "(no power attack)" marks.</param>
internal readonly record struct HorizonHeadline(double DamagePerRound, IReadOnlyList<double> PowerAttackOnChance);

/// <summary>
/// The engine runs of one call, on one budgeted meter (<see cref="DprLimits.WorkBudget"/>) that also carries the call's
/// cancellation. Counts its runs so an overrun can say how much the call asked for.
/// </summary>
internal sealed class DprRunner
{
    private readonly CancellationToken _cancellationToken;

    public DprRunner(CancellationToken cancellationToken, long budget = DprLimits.WorkBudget)
    {
        _cancellationToken = cancellationToken;
        Meter = new WorkMeter(budget, cancellationToken);
    }

    public WorkMeter Meter { get; }

    public int Runs { get; private set; }

    /// <exception cref="WorkBudgetExceededException">The call's budget ran out.</exception>
    public DprResult Run(ResolvedBuild build, ResolvedTarget target, DprOptions options)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        Runs++;
        return DprEngine.Run(build, target, options, Meter);
    }

    /// <summary>"20 levels × 16 ACs": a count with its noun, singular when one.</summary>
    public static string Count(int count, string noun) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";
}
