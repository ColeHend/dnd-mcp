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

    /// <summary>
    /// The stat block <see cref="TargetSpec.Monster"/> names, looked up by the host; required when the target names one
    /// (<see cref="TargetResolver.Resolve"/>).
    /// </summary>
    public Simulation.StatBlock? TargetMonster { get; init; }

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
public sealed record SignatureEffect(string Name, string Kind, string Condition, bool New, double LandChancePerTurn, double LandChancePerFight, int Rounds)
{
    /// <summary>The target is immune to the condition (a stat block's condition immunity): the 0% chances are that, not bad luck.</summary>
    public bool Immune { get; init; }
}

/// <summary>
/// The yardstick for a feat: what an Ability Score Improvement adds to the BASELINE at the detail level, on the same slope
/// as the headline (contract §4.6's LE and bands are unchanged; this puts them in scale). A general feat taken at an ASI
/// level replaces the ASI, so what it adds beyond this (<see cref="NetDelta"/>) is what it is worth there; for an origin
/// feat, a class feature, an item or a boon the net figure is not a verdict, which the result says.
///
/// <para>
/// The ability raised is the to_hit ability of the attack line that deals the most damage in the baseline's detail
/// evaluation, or for a build with only save effects the dc_ability of the one that deals the most. It rises by 2, to at
/// most 20, and the baseline is evaluated again with the same horizon, target and rulings. Skipped, with
/// <see cref="SkipReason"/>, when that ability is none, the DC is given rather than from an ability, the score is already
/// 20 or more, or nothing deals damage.
/// </para>
/// </summary>
public sealed record AsiYardstick
{
    /// <summary>The ability key raised, or null when skipped.</summary>
    public string? Ability { get; init; }

    public int From { get; init; }

    public int To { get; init; }

    /// <summary>The baseline's DPR with the ASI (the headline horizon).</summary>
    public double AsiDamagePerRound { get; init; }

    /// <summary>ASI − baseline.</summary>
    public double Delta { get; init; }

    /// <summary>Δ_ASI ÷ the same slope as the headline's level-equivalent.</summary>
    public LevelEquivalent? LevelEquivalent { get; init; }

    /// <summary>The feature's Δ minus the ASI's: what it adds beyond an ASI.</summary>
    public double NetDelta { get; init; }

    public LevelEquivalent? NetLevelEquivalent { get; init; }

    /// <summary>Why there is no yardstick, or null.</summary>
    public string? SkipReason { get; init; }

    public static AsiYardstick Skipped(string reason) => new() { SkipReason = reason };
}

/// <summary>A resource-limited feature of a compared build (a day horizon feature), named for the limited-uses line.</summary>
public sealed record LimitedFeature(string Name, ResolvedResource Resource);

/// <summary>
/// A headline on the fight or round1 horizon when a compared build has resource-limited features: those horizons give
/// their uses fresh every fight (round 1 spends them freely), so the band can read a "3 per long rest" feat as
/// Breaking. This is the day's figure beside it: Δ on the day horizon (already in the summaries) and its level-equivalent
/// on the baseline's DAY-horizon slope (a fight slope would divide a day Δ by the wrong curve), with the day's own band.
/// </summary>
/// <param name="Features">The limited features, the variant's then any the baseline has alone.</param>
public sealed record LimitedUsesReport(IReadOnlyList<LimitedFeature> Features, double DayDelta, LevelEquivalent DayLevelEquivalent);

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

    /// <summary>What an ASI adds to the baseline at <see cref="DetailLevel"/> on the headline's slope (the scale a feat is read on).</summary>
    public required AsiYardstick Asi { get; init; }

    /// <summary>
    /// On a fight or round1 headline with resource-limited features: the day horizon's Δ and level-equivalent; null
    /// otherwise.
    /// </summary>
    public LimitedUsesReport? LimitedUses { get; init; }

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
/// A problem only the merged build shows (a feature step map with no value at a level compared, a Concentration clash
/// with a baseline modifier) is still "Invalid feature", naming each item in the list it came from
/// (<see cref="ItemNames"/>).
/// </para>
/// <para>
/// <b>Reading the band.</b> The level-equivalent is damage-only and measured against the baseline as given (for a
/// feature, against adding nothing). So the report also carries an ASI yardstick on the same slope
/// (<see cref="AsiYardstick"/>) and, when limited uses make the fight horizon generous, the day's Δ with its own
/// level-equivalent (<see cref="LimitedUsesReport"/>).
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
        TargetResolver.Validate(request.Target, request.TargetMonster);
        var baselines = BuildResolver.Resolve(request.Baseline, levels, request.Rulings, "baseline");
        var evaluated = baselines.Select(b => b.Level).ToList();
        IReadOnlyList<ResolvedBuild> variants;
        if (request.Variant is not null)
        {
            variants = BuildResolver.Resolve(request.Variant, evaluated, request.Rulings, "variant");
        }
        else
        {
            // A problem only the merged build shows is the feature's, named in the lists the model wrote.
            var (merged, origins) = FeatureMerge.MergeWithOrigins(request.Baseline, request.Feature!);
            variants = BuildResolver.Resolve(merged, evaluated, request.Rulings, "feature", BuildUse.Dpr, ItemNames.Merged(origins));
        }

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
                var target = TargetResolver.Resolve(request.Target, level, request.TargetMonster);
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
            var asi = Yardstick(request, detailResult, horizon, runner);
            var limitedUses = LimitedUses(request, detailResult, baselineSummary, variantSummary, horizon, runner, baselines[0].ScalesWithLevel);
            notes.AddRange(AbilityNotes(request, baselines));
            notes.AddRange(LegendaryResistanceNotes(detailResult));

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
                Asi = asi,
                LimitedUses = limitedUses,
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

    /// <summary>The ASI yardstick at the detail level (see <see cref="AsiYardstick"/>).</summary>
    private static AsiYardstick Yardstick(CompareRequest request, ComparisonLevel detail, HorizonSettings horizon, DprRunner runner)
    {
        var evaluation = detail.Baseline.Evaluation;
        var build = evaluation.Build;
        string? ability;
        var attack = evaluation.Attacks.Where(a => a.DamagePerRound > 0).OrderByDescending(a => a.DamagePerRound).FirstOrDefault();
        if (attack is not null)
        {
            ability = build.FindAttack(attack.Attack)!.Ability;
            if (ability == V.Abilities.None)
            {
                return AsiYardstick.Skipped(
                    $"the baseline's main attack, {attack.Attack}, has to_hit ability none (a fixed bonus), so an ASI does not change it");
            }
        }
        else
        {
            var save = evaluation.SaveEffects.Select((r, i) => (Report: r, Effect: build.SaveEffects[i]))
                .Where(p => p.Report.DamagePerRound > 0)
                .OrderByDescending(p => p.Report.DamagePerRound)
                .FirstOrDefault();
            if (save.Effect is null)
            {
                return AsiYardstick.Skipped("no attack or save effect of the baseline deals damage here, so an ASI has nothing to raise");
            }

            ability = save.Effect.DcAbility;
            if (ability is null)
            {
                return AsiYardstick.Skipped($"the baseline's {save.Effect.Source.Label} has a given DC, not one from an ability, so an ASI does not change it");
            }
        }

        var from = build.Abilities.Score(ability);
        if (from >= DprLimits.AsiCap)
        {
            return AsiYardstick.Skipped(
                $"the baseline's {V.Abilities.Display(ability)} is already {Text(from)} (an ASI raises a score to at most {Text(DprLimits.AsiCap)})");
        }

        var to = Math.Min(DprLimits.AsiCap, from + 2);
        ResolvedBuild raised;
        try
        {
            raised = BuildResolver.Resolve(WithAbility(BuildPresets.Expand(request.Baseline), ability, to), detail.Level, request.Rulings, "baseline");
        }
        catch (DndInputException ex)
        {
            // The baseline validated at this level, so only a bug gets here; the yardstick is a side figure, never the reason a call fails.
            return AsiYardstick.Skipped($"the baseline with {V.Abilities.Display(ability)} {Text(to)} could not be read ({ex.Message})");
        }

        var damage = HorizonEvaluator.Headline(raised, evaluation.Target, horizon, runner).DamagePerRound;
        var slope = detail.LevelEquivalent.Slope;
        var delta = Delta(detail.Baseline.DamagePerRound, damage);
        var net = detail.Delta - delta;
        return new AsiYardstick
        {
            Ability = ability,
            From = from,
            To = to,
            AsiDamagePerRound = damage,
            Delta = delta,
            LevelEquivalent = LevelEquivalents.Of(delta, slope),
            NetDelta = net,
            NetLevelEquivalent = LevelEquivalents.Of(net, slope),
        };
    }

    // The spec with one ability score set to a scalar: the yardstick evaluates one level, where a scalar is exact.
    private static BuildSpec WithAbility(BuildSpec spec, string ability, int score)
    {
        var abilities = spec.Abilities;
        object? Pick(string key) => key == ability ? score : abilities?.Get(key);
        return new BuildSpec
        {
            Name = spec.Name,
            Edition = spec.Edition,
            Level = spec.Level,
            Abilities = new AbilitiesSpec
            {
                Str = Pick(V.Abilities.Str),
                Dex = Pick(V.Abilities.Dex),
                Con = Pick(V.Abilities.Con),
                Int = Pick(V.Abilities.Int),
                Wis = Pick(V.Abilities.Wis),
                Cha = Pick(V.Abilities.Cha),
            },
            ProficiencyBonus = spec.ProficiencyBonus,
            FightingStyle = spec.FightingStyle,
            Attacks = spec.Attacks,
            Modifiers = spec.Modifiers,
        };
    }

    /// <summary>
    /// The day's figure beside a fight or round1 headline when either build has resource-limited features at the detail
    /// level (see <see cref="LimitedUsesReport"/>). The day slope is always measured on the day horizon: the baseline's own
    /// day curve at the tier's edges (its limited features amortized there too), or RPGBOT's when that cannot serve.
    /// </summary>
    private static LimitedUsesReport? LimitedUses(
        CompareRequest request, ComparisonLevel detail, HorizonSummary baseline, HorizonSummary variant, HorizonSettings horizon, DprRunner runner,
        bool scales)
    {
        if (horizon.Horizon == DprHorizons.Day)
        {
            return null;
        }

        var variantFeatures = HorizonEvaluator.LimitedFeatures(detail.Variant.Evaluation.Build);
        var baselineFeatures = HorizonEvaluator.LimitedFeatures(detail.Baseline.Evaluation.Build);
        var features = variantFeatures
            .Concat(baselineFeatures.Where(b => !variantFeatures.Any(v => v.Source.Label == b.Source.Label)))
            .Select(f => new LimitedFeature(f.Source.Label, f.Resource))
            .ToList();
        if (features.Count == 0)
        {
            return null;
        }

        var day = HorizonSettings.ForDay(horizon.Day, horizon.Rounds);
        var slopes = new Slopes(request, day, runner, scales, new Dictionary<int, double> { [detail.Level] = baseline.DayDamagePerRound });
        var dayDelta = Delta(baseline.DayDamagePerRound, variant.DayDamagePerRound);
        return new LimitedUsesReport(features, dayDelta, LevelEquivalents.Of(dayDelta, slopes.For(LevelEquivalents.Tier(detail.Level))));
    }

    /// <summary>
    /// A feature that gives an ability as one number over a baseline whose score changes across the levels compared:
    /// the feature's value replaces the baseline's step map at EVERY level (abilities SET), so the variant loses the
    /// baseline's later ASIs and gains the feat before its level. Said, since the Δ curve reads as the feature's.
    /// </summary>
    private static IEnumerable<string> AbilityNotes(CompareRequest request, IReadOnlyList<ResolvedBuild> baselines)
    {
        if (request.Feature?.Abilities is not { } abilities || baselines.Count < 2)
        {
            yield break;
        }

        var baseline = BuildPresets.Expand(request.Baseline);
        foreach (var ability in V.Abilities.All)
        {
            LevelValue<int>? given;
            LevelValue<int>? steps;
            try
            {
                given = LevelValue.ParseInt(abilities.Get(ability), ability, DslLimits.MinAbilityScore, DslLimits.MaxAbilityScore);
                steps = LevelValue.ParseInt(baseline.Abilities?.Get(ability), ability, DslLimits.MinAbilityScore, DslLimits.MaxAbilityScore);
            }
            catch (DndInputException)
            {
                continue;
            }

            if (given is null || given.IsStepMap || steps is null || !steps.Scales ||
                baselines.Select(b => b.Abilities.Score(ability)).Distinct().Count() < 2)
            {
                continue;
            }

            var display = V.Abilities.Display(ability);
            yield return
                $"The feature sets {display} {Text(given.At(1))} at every level, replacing the baseline's step map " +
                $"({string.Join("/", steps.Steps.Select(s => Text(s.Value)))} at {string.Join("/", steps.Steps.Select(s => Text(s.Level)))}): the " +
                "variant has it before the feat's level and loses the baseline's later increases. For a feat taken at one level, give " +
                $"the variant's whole step map, e.g. {{\"{ability}\": {{\"1\": {Text(steps.Steps[0].Value)}, \"<feat level>\": …}}}}.";
        }
    }

    /// <summary>
    /// Against Legendary Resistance the condition effects' landing chances (and a condition's Advantage on damage) assume
    /// every failed save sticks, so Δ and its band are not the rules-as-written value against a legendary creature.
    /// </summary>
    private static IEnumerable<string> LegendaryResistanceNotes(ComparisonLevel detail)
    {
        var target = detail.Baseline.Evaluation.Target;
        var effects = new[] { detail.Baseline.Evaluation, detail.Variant.Evaluation }
            .SelectMany(e => e.Conditions.Where(c => !c.Immune).Select(c => c.Name)
                .Concat(e.SaveEffects.Where(s => s.Condition is not null && !s.ConditionImmune).Select(s => s.Name)))
            .Distinct()
            .ToList();
        if (target.LegendaryResistance > 0 && effects.Count > 0)
        {
            yield return
                $"Against {Text(target.LegendaryResistance)} Legendary Resistance: {string.Join(", ", effects)} {(effects.Count == 1 ? "is" : "are")} " +
                "measured as if every failed save sticks, so Δ and the band are not what a creature that spends one on each failure " +
                "allows (the expected casts or attempts to land are in each build's breakdown).";
        }
    }

    private static List<SignatureEffect> Signature(DprResult fight, int rounds, IReadOnlyList<SignatureEffect>? baseline)
    {
        bool IsNew(string kind, string name) => baseline is not null && !baseline.Any(b => b.Kind == kind && b.Name == name);

        var effects = new List<SignatureEffect>();
        foreach (var condition in fight.Conditions)
        {
            effects.Add(new SignatureEffect(
                condition.Name, V.Kinds.ConditionOnHit, condition.Condition, IsNew(V.Kinds.ConditionOnHit, condition.Name),
                condition.LandChancePerTurn, condition.LandChancePerFight ?? condition.LandChancePerTurn, rounds)
            {
                Immune = condition.Immune,
            });
        }

        foreach (var save in fight.SaveEffects)
        {
            if (save.Condition is not null && save.LandChancePerTurn is { } perTurn)
            {
                effects.Add(new SignatureEffect(
                    save.Name, V.Kinds.SaveEffect, save.Condition, IsNew(V.Kinds.SaveEffect, save.Name), perTurn, save.LandChancePerFight ?? perTurn, rounds)
                {
                    Immune = save.ConditionImmune,
                });
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

            value = HorizonEvaluator.Headline(build, TargetResolver.Resolve(request.Target, level, request.TargetMonster), horizon, runner).DamagePerRound;
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
