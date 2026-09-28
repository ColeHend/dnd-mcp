using DndMcp.Domain.Core;
using FluentValidation;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// <c>balance_compare</c>'s "baseline + feature": merges a <see cref="FeatureSpec"/> into a baseline build to make the
/// variant, so a feature is described once, as what it adds, and the delta is measured under identical assumptions.
///
/// <para>
/// Rules: attacks are appended, except that an attack with a baseline attack's name (case ignored) REPLACES it in place
/// (a feat that changes the greatsword's damage); modifiers are appended; abilities SET the scores given (a feat's +1 Str
/// on an 18 is <c>{"str": 19}</c>, not +1, because the DSL cannot know the baseline's score at every level); a fighting
/// style replaces the baseline's. A preset baseline is expanded first, so a feature can be added to the warlock baseline.
/// </para>
/// <para>
/// The feature is validated on its own terms first (its items numbered as in the feature, with attack references checked
/// against the merged attacks), so a mistake in it is reported as "Invalid feature: modifiers item 1 …" rather than at a
/// shifted position in the merged build. The merged build is then validated as a whole by whoever resolves it; with the
/// origins <see cref="MergeWithOrigins"/> returns, that validation still names every item in the list the model wrote it
/// in (<see cref="ItemNames"/>), so a problem that only shows at a level ("dice has no value at level 1") reads
/// "Invalid feature: modifiers item 1 (extra_damage) …", never "Invalid variant: modifiers item 3 …".
/// </para>
/// </summary>
public static class FeatureMerge
{
    /// <summary>The variant: <paramref name="baseline"/> with <paramref name="feature"/> merged in.</summary>
    /// <exception cref="DndInputException">The feature is not valid (every problem, up to five), or the baseline's preset is unknown.</exception>
    public static BuildSpec Merge(BuildSpec baseline, FeatureSpec feature) => MergeWithOrigins(baseline, feature).Variant;

    /// <summary>
    /// The variant, and where each of its items came from: baseline attacks and modifiers keep their positions (merged
    /// 1..B are baseline 1..B), the feature's follow (merged B + k is feature item k), a feature attack that replaces a
    /// baseline attack sits at the baseline's position but is the feature's item; the ability scores and fighting style
    /// the feature gave are the feature's. A preset baseline's items are the preset's, which the model never wrote out.
    /// </summary>
    /// <exception cref="DndInputException">The feature is not valid, or the baseline's preset is unknown.</exception>
    internal static (BuildSpec Variant, ItemOrigins Origins) MergeWithOrigins(BuildSpec baseline, FeatureSpec feature)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(feature);

        var expanded = BuildPresets.Expand(baseline);
        var (attacks, attackOrigins) = MergeAttacks(expanded.Attacks ?? [], feature.Attacks ?? []);
        DslProblems.ThrowIfAny(DslProblems.Messages(FeatureSpecValidator.Instance.Validate(new FeatureInput(feature, attacks))), "feature");

        var baselineModifiers = expanded.Modifiers ?? [];
        var featureModifiers = feature.Modifiers ?? [];
        var origins = new ItemOrigins(
            attackOrigins,
            [
                .. baselineModifiers.Select((_, i) => new ItemOrigin(false, i + 1)),
                .. featureModifiers.Select((_, k) => new ItemOrigin(true, k + 1)),
            ],
            V.Abilities.All.Where(a => LevelValue.IsGiven(feature.Abilities?.Get(a))).ToHashSet(StringComparer.Ordinal),
            feature.FightingStyle is not null,
            baseline.Preset is { } preset && V.Presets.Set.TryMatch(preset, out var canonical) ? $"the {canonical} preset's" : "baseline");

        var variant = new BuildSpec
        {
            Name = VariantName(expanded.Name, feature.Name!),
            Edition = expanded.Edition,
            Level = expanded.Level,
            Abilities = MergeAbilities(expanded.Abilities, feature.Abilities),
            ProficiencyBonus = expanded.ProficiencyBonus,
            FightingStyle = feature.FightingStyle ?? expanded.FightingStyle,
            Attacks = attacks,
            Modifiers = [.. baselineModifiers, .. featureModifiers],
        };
        return (variant, origins);
    }

    private static (List<AttackSpec> Attacks, List<ItemOrigin> Origins) MergeAttacks(IReadOnlyList<AttackSpec> baseline, IReadOnlyList<AttackSpec> added)
    {
        var merged = baseline.ToList();
        var origins = baseline.Select((_, i) => new ItemOrigin(false, i + 1)).ToList();
        for (var k = 0; k < added.Count; k++)
        {
            var attack = added[k];
            var index = attack?.Name is { } name
                ? merged.FindIndex(a => string.Equals(a?.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                : -1;
            if (index >= 0)
            {
                merged[index] = attack!;
                origins[index] = new ItemOrigin(true, k + 1);
            }
            else
            {
                merged.Add(attack!);
                origins.Add(new ItemOrigin(true, k + 1));
            }
        }

        return (merged, origins);
    }

    private static AbilitiesSpec? MergeAbilities(AbilitiesSpec? baseline, AbilitiesSpec? feature)
    {
        if (feature is null)
        {
            return baseline;
        }

        return new AbilitiesSpec
        {
            Str = Pick(V.Abilities.Str),
            Dex = Pick(V.Abilities.Dex),
            Con = Pick(V.Abilities.Con),
            Int = Pick(V.Abilities.Int),
            Wis = Pick(V.Abilities.Wis),
            Cha = Pick(V.Abilities.Cha),
        };

        object? Pick(string ability) => LevelValue.IsGiven(feature.Get(ability)) ? feature.Get(ability) : baseline?.Get(ability);
    }

    // "Baseline + Feature", cut to the build name limit: the variant's name must pass the same validation as any build's.
    private static string? VariantName(string? baseline, string feature)
    {
        var name = $"{baseline?.Trim()} + {feature.Trim()}";
        return name.Length <= DslLimits.MaxBuildNameLength ? name : name[..(DslLimits.MaxBuildNameLength - 1)] + "…";
    }
}

/// <summary>A feature and the attacks of the build it will be merged into (for attack references).</summary>
internal sealed record FeatureInput(FeatureSpec Feature, IReadOnlyList<AttackSpec?> MergedAttacks);

/// <summary>
/// Checks a feature before merging: a name, at least one change, and each attack and modifier item as a build's items
/// are checked (<see cref="AttackItemValidator"/>, <see cref="ModifierItemValidator"/>).
/// </summary>
internal sealed class FeatureSpecValidator : AbstractValidator<FeatureInput>
{
    public static FeatureSpecValidator Instance { get; } = new();

    private FeatureSpecValidator()
    {
        RuleFor(x => x.Feature).Custom((feature, context) =>
        {
            var problems = new Problems<FeatureInput>(context, string.Empty);
            BuildSpecValidator.BuildNameProblems(feature.Name, problems);
            BuildSpecValidator.AbilityProblems(feature.Abilities, problems);
            problems.Known(V.FightingStyles.Set, "fighting_style", feature.FightingStyle);
            if ((feature.Attacks ?? []).Count == 0 && (feature.Modifiers ?? []).Count == 0 && feature.Abilities is null && feature.FightingStyle is null)
            {
                problems.Add(
                    "the feature changes nothing: give attacks, modifiers, abilities or fighting_style, e.g. \"modifiers\": " +
                    "[{\"kind\": \"bonus_damage\", \"amount\": \"pb\", \"attack_action_only\": true}].");
            }
        });

        RuleForEach(x => AttackItem.Of(x.Feature.Attacks)).SetValidator(AttackItemValidator.Instance).OverridePropertyName("attacks");
        RuleForEach(x => ModifierItem.Of(x.Feature.Modifiers, x.MergedAttacks)).SetValidator(ModifierItemValidator.Instance).OverridePropertyName("modifiers");
    }
}
