using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Dpr;

/// <summary>
/// <c>balance_compare</c>'s arguments as the host binds them. Exactly one of <see cref="Variant"/> (the whole changed
/// build) and <see cref="Feature"/> (only what to add to the baseline, merged by <see cref="FeatureMerge"/>).
/// </summary>
public sealed record CompareRequest
{
    public required BuildSpec Baseline { get; init; }

    public BuildSpec? Variant { get; init; }

    public FeatureSpec? Feature { get; init; }

    public TargetSpec? Target { get; init; }

    public RulingsSpec? Rulings { get; init; }

    /// <summary>Levels to compare at (1–20); null or empty: the baseline's own level.</summary>
    public IReadOnlyList<int>? Levels { get; init; }

    public string? Horizon { get; init; }

    public int? Rounds { get; init; }

    public string? RestPreset { get; init; }

    public double? EncountersPerDay { get; init; }

    public int? ShortRests { get; init; }

    /// <summary>The call's work budget (<see cref="DprLimits.WorkBudget"/>); lowered only by tests, to reach the overrun message.</summary>
    internal long WorkBudget { get; init; } = DprLimits.WorkBudget;
}

/// <summary>The two action-economy slots a feature can compete for.</summary>
public static class ActionSlots
{
    public const string BonusAction = "bonus_action";

    public const string Reaction = "reaction";

    /// <summary>"Bonus Action", "Reaction".</summary>
    public static string Display(string slot) => slot switch
    {
        BonusAction => "Bonus Action",
        Reaction => "Reaction",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "Not an action slot."),
    };
}

/// <summary>
/// What uses one action-economy slot in each build (research A9, the balance skill's check 2), over every level compared.
/// </summary>
/// <param name="Slot">An <see cref="ActionSlots"/> value.</param>
/// <param name="Baseline">The baseline's consumers of the slot (labels, first appearance first).</param>
/// <param name="Variant">The variant's.</param>
/// <param name="Added">The variant's consumers the baseline does not have.</param>
/// <param name="OpportunityCost">
/// Whether, at some level, the variant adds a consumer while the baseline already had one: the new option then competes
/// with the old one, and Δ is what the better choice each turn gains, not the new option's own damage.
/// </param>
public sealed record ActionSlotReport(string Slot, IReadOnlyList<string> Baseline, IReadOnlyList<string> Variant, IReadOnlyList<string> Added, bool OpportunityCost)
{
    /// <summary>The contract's wording, repeated wherever a collision is reported.</summary>
    public const string OpportunityCostText = "opportunity cost: the engine picks the better use each turn, so Δ is net";
}

/// <summary>
/// How reliably a condition-imposing effect lands (the balance skill's check 4, failure rate): per turn, and at least once
/// in a fight of <see cref="Rounds"/> rounds from fresh resources.
/// </summary>
/// <param name="Kind"><see cref="V.Kinds.ConditionOnHit"/> or <see cref="V.Kinds.SaveEffect"/>.</param>
/// <param name="New">The baseline has no effect of that kind and name (it comes with the variant).</param>
/// <param name="LandChancePerTurn">P(the condition lands in a turn), averaged over the fight's rounds.</param>
/// <param name="LandChancePerFight">P(it lands at least once in the fight).</param>
public sealed record SignatureEffect(string Name, string Kind, string Condition, bool New, double LandChancePerTurn, double LandChancePerFight, int Rounds);

/// <summary>One level of a comparison: both builds' results under identical assumptions, Δ and the level-equivalent.</summary>
public sealed record ComparisonLevel
{
    public required int Level { get; init; }

    public required HorizonResult Baseline { get; init; }

    public required HorizonResult Variant { get; init; }

    /// <summary>Variant − baseline DPR; a difference within the engine's 1e-9 precision is 0.</summary>
    public required double Delta { get; init; }

    public required LevelEquivalent LevelEquivalent { get; init; }

    /// <summary>Δ as a fraction of the baseline (0.14 = +14%); null when the baseline deals nothing.</summary>
    public double? RelativeDelta => Baseline.DamagePerRound > 0 ? Delta / Baseline.DamagePerRound : null;
}

/// <summary>Everything <c>balance_compare</c> reports.</summary>
public sealed record ComparisonReport
{
    public required HorizonSettings Horizon { get; init; }

    public required string BaselineName { get; init; }

    public required string VariantName { get; init; }

    /// <summary>The feature's name when the variant is baseline + feature, else null.</summary>
    public string? FeatureName { get; init; }

    /// <summary>The levels compared, ascending.</summary>
    public required IReadOnlyList<ComparisonLevel> Levels { get; init; }

    /// <summary>The level shown in full (both breakdowns, round-1 distributions): the baseline's own level if compared, else the lowest.</summary>
    public required int DetailLevel { get; init; }

    public ComparisonLevel Detail => Levels.First(l => l.Level == DetailLevel);

    /// <summary>The baseline at the detail level on all three horizons.</summary>
    public required HorizonSummary BaselineSummary { get; init; }

    /// <summary>The variant at the detail level on all three horizons (Δ per horizon is the difference).</summary>
    public required HorizonSummary VariantSummary { get; init; }

    /// <summary>The slope of each tier the compared levels fall in, with its source.</summary>
    public required IReadOnlyList<TierSlope> Slopes { get; init; }

    public required ActionSlotReport BonusAction { get; init; }

    public required ActionSlotReport Reaction { get; init; }

    /// <summary>The variant's condition-imposing effects at <see cref="DetailLevel"/>, measured over a fight.</summary>
    public required IReadOnlyList<SignatureEffect> SignatureEffects { get; init; }

    /// <summary>The baseline's, for comparison.</summary>
    public required IReadOnlyList<SignatureEffect> BaselineSignatureEffects { get; init; }

    /// <summary>Call-level notes: mismatched builds, slope fallbacks, collisions.</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>Engine runs the call made (diagnostic).</summary>
    public required int Runs { get; init; }
}

/// <summary>
/// <c>balance_compare</c> (contract §4.6, research A10): the delta method. ΔDPR = DPR(variant) − DPR(baseline) under
/// identical assumptions (horizon, target, rulings, levels), absolute and relative, as level-equivalents with their band,
/// with the action-economy collisions and the chance a signature effect lands.
///
/// <para>
/// <b>Validation order</b> follows what the model wrote: the baseline first ("Invalid baseline: …"), then the feature on
/// its own numbering ("Invalid feature: …") or the variant ("Invalid variant: …"), so a mistake is reported where it is.
/// </para>
/// <para>
/// <b>The slope</b> of a level's tier comes from the baseline's own curve at the tier's edges (<see cref="LevelEquivalents"/>),
/// evaluated with the same horizon against the same target spec at those levels, even when they are not among the levels
/// compared. When the baseline does not scale with level, cannot be evaluated at an edge (a step value starting at 5
/// when the slope needs level 4), or gains less than <see cref="DprLimits.MinTierSlope"/> a level, RPGBOT's reference
/// slope is used and a note says why.
/// </para>
/// </summary>
public static class DprComparison
{
    /// <summary>A difference within this (relative to the baseline, at least absolute) is the engine's precision, not a feature.</summary>
    internal const double DeltaPrecision = 1e-9;

    /// <summary>Compares the request's two builds.</summary>
    /// <exception cref="DndInputException">An argument, the baseline, the feature, the variant or the target is not valid, or the call is too large.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static ComparisonReport Compare(CompareRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);

        var horizon = HorizonSettings.Resolve(request.Horizon, request.Rounds, request.RestPreset, request.EncountersPerDay, request.ShortRests);
        if (request.Variant is null == request.Feature is null)
        {
            throw new DndInputException(request.Variant is null
                ? "Give exactly one of variant (the whole changed build) or feature (only what to add to the baseline), e.g. " +
                  "\"feature\": {\"name\": \"Great Weapon Master\", \"modifiers\": [{\"kind\": \"bonus_damage\", \"amount\": \"pb\", " +
                  "\"attack_action_only\": true}]}."
                : "Give variant or feature, not both: variant is the whole changed build, feature only what to add to the baseline.");
        }

        var levels = DprAnalysis.DistinctLevels(request.Levels);
        TargetResolver.Validate(request.Target);
        var baselines = BuildResolver.Resolve(request.Baseline, levels, request.Rulings, "baseline");
        var evaluated = baselines.Select(b => b.Level).ToList();
        var variantSpec = request.Variant ?? FeatureMerge.Merge(request.Baseline, request.Feature!);
        var variants = BuildResolver.Resolve(variantSpec, evaluated, request.Rulings, "variant");

        var ownLevel = request.Baseline.Level ?? evaluated[0];
        var detailLevel = evaluated.Contains(ownLevel) ? ownLevel : evaluated[0];
        var runner = new DprRunner(cancellationToken, request.WorkBudget);
        var notes = new List<string>();
        try
        {
            var results = new List<(int Level, HorizonResult Baseline, HorizonResult Variant, double Delta)>();
            var baselineDamage = new Dictionary<int, double>();
            for (var i = 0; i < evaluated.Count; i++)
            {
                var level = evaluated[i];
                var target = TargetResolver.Resolve(request.Target, level);
                var detail = level == detailLevel;
                var baseline = HorizonEvaluator.Full(baselines[i], target, horizon, detail, runner);
                var variant = HorizonEvaluator.Full(variants[i], target, horizon, detail, runner);
                baselineDamage[level] = baseline.DamagePerRound;
                results.Add((level, baseline, variant, Delta(baseline.DamagePerRound, variant.DamagePerRound)));
            }

            var slopes = new Slopes(request, horizon, runner, baselines[0].ScalesWithLevel, baselineDamage);
            var compared = results
                .Select(r => new ComparisonLevel
                {
                    Level = r.Level,
                    Baseline = r.Baseline,
                    Variant = r.Variant,
                    Delta = r.Delta,
                    LevelEquivalent = LevelEquivalents.Of(r.Delta, slopes.For(LevelEquivalents.Tier(r.Level))),
                })
                .ToList();

            var tierSlopes = slopes.All;
            notes.AddRange(tierSlopes.Where(s => s.FallbackReason is not null).Select(FallbackNote));
            notes.AddRange(MismatchNotes(request, baselines[0], variants[0], levels));

            var bonusAction = Slot(ActionSlots.BonusAction, baselines, variants, b => b.BonusActionConsumers);
            var reaction = Slot(ActionSlots.Reaction, baselines, variants, b => b.ReactionConsumers);
            notes.AddRange(new[] { bonusAction, reaction }.Where(s => s.OpportunityCost).Select(CollisionNote));

            // The landing chances need a fight ("at least once per fight"): the results' own fight runs, or one more each on
            // the round1 horizon, shared with the summaries.
            var detailResult = compared.First(c => c.Level == detailLevel);
            var baselineFight = HorizonEvaluator.FightOf(detailResult.Baseline, runner);
            var variantFight = HorizonEvaluator.FightOf(detailResult.Variant, runner);
            var baselineSummary = HorizonEvaluator.Summary(detailResult.Baseline, runner, baselineFight);
            var variantSummary = HorizonEvaluator.Summary(detailResult.Variant, runner, variantFight);
            var baselineEffects = Signature(baselineFight, horizon.Rounds, baseline: null);
            var variantEffects = Signature(variantFight, horizon.Rounds, baselineEffects);

            return new ComparisonReport
            {
                Horizon = horizon,
                BaselineName = baselines[0].Name,
                VariantName = variants[0].Name,
                FeatureName = request.Feature?.Name,
                Levels = compared,
                DetailLevel = detailLevel,
                BaselineSummary = baselineSummary,
                VariantSummary = variantSummary,
                Slopes = tierSlopes,
                BonusAction = bonusAction,
                Reaction = reaction,
                SignatureEffects = variantEffects,
                BaselineSignatureEffects = baselineEffects,
                Notes = notes.Distinct().ToList(),
                Runs = runner.Runs,
            };
        }
        catch (WorkBudgetExceededException ex)
        {
            var what = $"comparing two builds at {DprRunner.Count(evaluated.Count, "level")} on the {horizon.Horizon} horizon";
            if (horizon.Horizon == DprHorizons.Day)
            {
                what += $" (up to {HorizonEvaluator.FullRuns(variants.MaxBy(v => HorizonEvaluator.LimitedFeatures(v).Count)!, horizon)} fight runs per build and level)";
            }

            throw DprLimits.RequestTooLarge(what, ex);
        }
    }

    /// <summary>Variant − baseline, with a difference within the engine's precision read as none (two runs of one build differ by rounding only).</summary>
    internal static double Delta(double baseline, double variant)
    {
        var delta = variant - baseline;
        return Math.Abs(delta) <= DeltaPrecision * Math.Max(1.0, Math.Abs(baseline)) ? 0 : delta;
    }

    private static List<SignatureEffect> Signature(DprResult fight, int rounds, IReadOnlyList<SignatureEffect>? baseline)
    {
        bool IsNew(string kind, string name) => baseline is not null && !baseline.Any(b => b.Kind == kind && b.Name == name);

        var effects = new List<SignatureEffect>();
        foreach (var condition in fight.Conditions)
        {
            effects.Add(new SignatureEffect(
                condition.Name, V.Kinds.ConditionOnHit, condition.Condition, IsNew(V.Kinds.ConditionOnHit, condition.Name),
                condition.LandChancePerTurn, condition.LandChancePerFight ?? condition.LandChancePerTurn, rounds));
        }

        foreach (var save in fight.SaveEffects)
        {
            if (save.Condition is not null && save.LandChancePerTurn is { } perTurn)
            {
                effects.Add(new SignatureEffect(
                    save.Name, V.Kinds.SaveEffect, save.Condition, IsNew(V.Kinds.SaveEffect, save.Name), perTurn, save.LandChancePerFight ?? perTurn, rounds));
            }
        }

        return effects;
    }

    private static ActionSlotReport Slot(
        string slot, IReadOnlyList<ResolvedBuild> baselines, IReadOnlyList<ResolvedBuild> variants, Func<ResolvedBuild, IReadOnlyList<string>> consumers)
    {
        var baseline = new List<string>();
        var variant = new List<string>();
        var added = new List<string>();
        var cost = false;
        for (var i = 0; i < baselines.Count; i++)
        {
            var before = consumers(baselines[i]);
            var after = consumers(variants[i]);
            var adds = after.Where(c => !before.Contains(c, StringComparer.Ordinal)).ToList();
            Union(baseline, before);
            Union(variant, after);
            Union(added, adds);
            cost |= adds.Count > 0 && before.Count > 0;
        }

        return new ActionSlotReport(slot, baseline, variant, added, cost);

        static void Union(List<string> into, IEnumerable<string> items) =>
            into.AddRange(items.Where(item => !into.Contains(item, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal));
    }

    private static string CollisionNote(ActionSlotReport slot) =>
        $"{ActionSlots.Display(slot.Slot)}: the variant adds {string.Join(", ", slot.Added)} where the baseline already uses " +
        $"{string.Join(", ", slot.Baseline)}; {ActionSlotReport.OpportunityCostText}.";

    private static string FallbackNote(TierSlope slope) => string.Create(
        CultureInfo.InvariantCulture,
        $"Level-equivalents in tier {slope.Tier} (levels {slope.FirstLevel}–{slope.LastLevel}) divide by RPGBOT's reference slope, " +
        $"{slope.PerLevel:0.###} DPR per level (the CR = level row's maximum HP ÷ 12 from level {slope.FromLevel} to {slope.ToLevel}), " +
        $"because {slope.FallbackReason}.");

    private static IEnumerable<string> MismatchNotes(CompareRequest request, ResolvedBuild baseline, ResolvedBuild variant, IReadOnlyList<int>? levels)
    {
        if (baseline.Edition != variant.Edition)
        {
            yield return $"The baseline follows the {baseline.Edition} rules and the variant the {variant.Edition} rules, so Δ includes the edition change, not only the difference between the builds.";
        }

        if (levels is null && request.Variant?.Level is { } variantLevel && request.Baseline.Level is { } baselineLevel && variantLevel != baselineLevel)
        {
            yield return
                $"The variant says level {Text(variantLevel)} and the baseline level {Text(baselineLevel)}; both are compared at level " +
                $"{Text(baselineLevel)}, the baseline's (give levels to compare at others).";
        }
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Each tier's slope, computed once: the baseline's DPR at the tier's edges (already known for compared levels,
    /// evaluated otherwise), or RPGBOT's when the baseline's cannot serve.
    /// </summary>
    private sealed class Slopes(CompareRequest request, HorizonSettings horizon, DprRunner runner, bool scales, Dictionary<int, double> damage)
    {
        private readonly Dictionary<int, TierSlope> _byTier = [];
        private readonly Dictionary<int, string> _unavailable = [];

        public IReadOnlyList<TierSlope> All => _byTier.OrderBy(p => p.Key).Select(p => p.Value).ToList();

        public TierSlope For(int tier)
        {
            if (!_byTier.TryGetValue(tier, out var slope))
            {
                slope = Compute(tier);
                _byTier[tier] = slope;
            }

            return slope;
        }

        private TierSlope Compute(int tier)
        {
            if (!scales)
            {
                return LevelEquivalents.ReferenceSlope(
                    tier, "the baseline does not change with level (no step values, from_level/until_level or cantrip scaling), so its curve has no slope of its own");
            }

            var (from, to) = LevelEquivalents.SlopeLevels(tier);
            if (!TryDamage(from, out var fromDamage, out var why) || !TryDamage(to, out var toDamage, out why))
            {
                return LevelEquivalents.ReferenceSlope(tier, why!);
            }

            var own = LevelEquivalents.BaselineSlope(tier, fromDamage, toDamage);
            return own.PerLevel >= DprLimits.MinTierSlope
                ? own
                : LevelEquivalents.ReferenceSlope(tier, FormattableString.Invariant(
                    $"the baseline's own slope from level {from} to {to} is {own.PerLevel:0.###} DPR per level ({fromDamage:0.##} → {toDamage:0.##}), below {DprLimits.MinTierSlope}"));
        }

        private bool TryDamage(int level, out double value, out string? why)
        {
            why = null;
            if (damage.TryGetValue(level, out value))
            {
                return true;
            }

            if (_unavailable.TryGetValue(level, out why))
            {
                return false;
            }

            ResolvedBuild build;
            try
            {
                build = BuildResolver.Resolve(request.Baseline, level, request.Rulings, "baseline");
            }
            catch (DndInputException ex)
            {
                why = $"the baseline cannot be evaluated at level {Text(level)}: {FirstProblem(ex.Message)}";
                _unavailable[level] = why;
                return false;
            }

            value = HorizonEvaluator.Headline(build, TargetResolver.Resolve(request.Target, level), horizon, runner).DamagePerRound;
            damage[level] = value;
            return true;
        }

        /// <summary>A validation message's first problem, without the "Invalid baseline" heading, for a one-line reason.</summary>
        private static string FirstProblem(string message)
        {
            var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var first = lines.Length > 1 ? lines[1].TrimStart('-', ' ') : lines[0];
            const string heading = "Invalid baseline: ";
            first = first.StartsWith(heading, StringComparison.Ordinal) ? first[heading.Length..] : first;
            first = first.TrimEnd('.');
            return first.Length <= 160 ? first : first[..159] + "…";
        }
    }
}
