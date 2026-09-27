using FluentValidation;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>A compiled build and the levels it will be evaluated at: the input of the stage 2 validators.</summary>
internal sealed record BuildLevels(CompiledBuild Build, IReadOnlyList<int> Levels);

/// <summary>
/// Stage 2a: every step value has a value at every evaluated level where its owner (the build, an attack, a modifier) is
/// active. Runs before <see cref="BuildLevelValidator"/>, whose rules read values at those levels.
///
/// <para>
/// Each field is reported once, at its first uncovered level, naming the field, the level and the step map's first key:
/// a gap usually spans many levels, and twenty copies of one mistake would crowd out the others.
/// </para>
/// </summary>
internal sealed class StepCoverageValidator : AbstractValidator<BuildLevels>
{
    public static StepCoverageValidator Instance { get; } = new();

    private StepCoverageValidator()
    {
        RuleFor(x => x.Build).Custom((build, context) =>
        {
            var levels = context.InstanceToValidate.Levels;
            var problems = new Problems<BuildLevels>(context, string.Empty);
            foreach (var (ability, value) in build.Abilities)
            {
                if (levels.FirstOrDefault(l => !value.TryAt(l, out _)) is var level and > 0)
                {
                    problems.Add(LevelValue.MissingAt($"abilities {ability}", level, value.FirstLevel, owner: null));
                }
            }

            foreach (var attack in build.Attacks)
            {
                var attackProblems = new Problems<BuildLevels>(context, attack.Where);
                Check("count", new StepCoverage<int>(attack.Count), attack.IsActiveAt, "attack", attackProblems);
                Check("damage", new StepCoverage<DamageFormula>(attack.Damage), attack.IsActiveAt, "attack", attackProblems);
            }

            foreach (var modifier in build.Modifiers)
            {
                var modifierProblems = new Problems<BuildLevels>(context, modifier.Where);
                foreach (var (field, value) in modifier.StepValues())
                {
                    Check(field, value, modifier.IsActiveAt, "modifier", modifierProblems);
                }
            }

            void Check(string field, IStepCoverage value, Func<int, bool> active, string owner, Problems<BuildLevels> itemProblems)
            {
                if (levels.FirstOrDefault(l => active(l) && !value.Covers(l)) is var level and > 0)
                {
                    itemProblems.Add(LevelValue.MissingAt(field, level, value.FirstLevel, owner));
                }
            }
        });
    }
}

/// <summary>
/// Stage 2b: the rules that hold per level. A turn must have something to do; one Concentration effect; one use of the
/// Action; extra attacks name an attack that exists then; the totals the DPR engine's state space is sized for. Each
/// rule reports its first failing level only.
/// </summary>
internal sealed class BuildLevelValidator : AbstractValidator<BuildLevels>
{
    public static BuildLevelValidator Instance { get; } = new();

    private BuildLevelValidator()
    {
        RuleFor(x => x.Build).Custom((build, context) =>
        {
            var problems = new Problems<BuildLevels>(context, string.Empty);
            foreach (var level in context.InstanceToValidate.Levels.Distinct().Order())
            {
                if (build.ActiveAttacks(level).Count == 0 && !build.ActiveModifiers(level).Any(m => m.Kind == V.Kinds.SaveEffect))
                {
                    problems.Add(
                        $"at level {DslText.Number(level)} no attack and no save_effect is active, so the build does nothing that " +
                        "turn; give one from that level (see from_level and until_level) or evaluate other levels.");
                    break;
                }
            }
        });

        RuleFor(x => x.Build).Custom((build, context) =>
        {
            foreach (var modifier in build.Modifiers.Where(m => m.Kind == V.Kinds.ExtraAttack))
            {
                var attack = build.FindAttack(modifier.Attack!)!;
                var level = FirstLevel(context, l => modifier.IsActiveAt(l) && !attack.IsActiveAt(l));
                if (level > 0)
                {
                    new Problems<BuildLevels>(context, modifier.Where).Add(
                        $"at level {DslText.Number(level)} it makes \"{attack.Name}\", which is not active then" +
                        $"{ActiveRange(attack.FromLevel, attack.UntilLevel)}; give the modifier the same from_level/until_level.");
                }
            }
        });

        RuleFor(x => x.Build).Custom((build, context) =>
        {
            foreach (var modifier in build.Modifiers.Where(m => m.Kind == V.Kinds.ConditionOnHit && m.Dc is null && m.DcAbility is null))
            {
                var level = FirstLevel(context, l => modifier.IsActiveAt(l) && build.AttacksFor(modifier, l).Any(a => a.Ability == V.Abilities.None));
                if (level > 0)
                {
                    var attack = build.AttacksFor(modifier, level).First(a => a.Ability == V.Abilities.None);
                    new Problems<BuildLevels>(context, modifier.Where).Add(
                        $"its DC defaults to 8 + proficiency + the attack's ability, but \"{attack.Name}\" has to_hit ability none; give dc " +
                        "or dc_ability.");
                }
            }
        });

        RuleFor(x => x.Build).Custom((build, context) =>
        {
            var problems = new Problems<BuildLevels>(context, string.Empty);
            Once(context, build, problems, level =>
            {
                var concentration = build.ActiveModifiers(level).Where(m => m.Concentration).ToList();
                return concentration.Count > 1
                    ? $"at level {DslText.Number(level)} {Both(concentration)} need Concentration, and a character concentrates on one " +
                      "effect at a time; compare them as two builds."
                    : null;
            });

            Once(context, build, problems, level =>
            {
                var actionEffects = build.ActiveModifiers(level)
                    .Where(m => m.Kind == V.Kinds.SaveEffect && m.ActionCost == V.ActionCosts.Action)
                    .ToList();
                if (actionEffects.Count > 1)
                {
                    return $"at level {DslText.Number(level)} {Both(actionEffects)} both use the Action; a build is one turn's routine, " +
                           "so compare them as two builds (or give one action_cost \"bonus_action\" or \"none\").";
                }

                var actionAttack = build.ActiveAttacks(level).FirstOrDefault(a => a.Action == V.AttackActions.Action);
                return actionEffects.Count == 1 && actionAttack is not null
                    ? $"at level {DslText.Number(level)} {actionEffects[0].Where} uses the Action, and {actionAttack.Where} is made with the " +
                      "Attack action; a build is one turn's routine, so compare the two routines as two builds (or give the " +
                      "save_effect action_cost \"bonus_action\" or \"none\")."
                    : null;
            });

            Once(context, build, problems, level =>
            {
                var attacks = build.ActiveAttacks(level)
                    .Sum(a => a.Count.At(level) * (a.Cantrip == V.Cantrips.Beams ? V.Cantrips.Multiplier(level) : 1));
                var extra = build.ActiveModifiers(level).Where(m => m.Kind == V.Kinds.ExtraAttack).Sum(m => m.Count!.At(level));
                return attacks + extra > DslLimits.MaxAttacksPerTurn
                    ? $"at level {DslText.Number(level)} the build makes up to {DslText.Number(attacks + extra)} attacks a turn " +
                      $"({DslText.Number(attacks)} from attacks, {DslText.Number(extra)} from extra_attack modifiers); at most " +
                      $"{DslLimits.MaxAttacksPerTurn} are modelled."
                    : null;
            });

            Once(context, build, problems, level => AtMost(
                level, build.ActiveModifiers(level).Where(m => m.Resource is not null).ToList(), DslLimits.MaxResourceModifiers,
                "have a resource", "the engine tracks the uses left of each; give the rest as separate builds"));

            Once(context, build, problems, level => AtMost(
                level, build.ActiveModifiers(level).Where(m => m.Kind == V.Kinds.Advantage && m.Rate < 1).ToList(),
                DslLimits.MaxRateAdvantageSources, "are advantage sources with a rate below 1",
                "each turn is mixed over every on/off combination; merge sources with similar rates"));

            Once(context, build, problems, level => AtMost(
                level, build.ActiveModifiers(level).Where(m => m.Kind == V.Kinds.PowerAttack).ToList(), DslLimits.MaxPowerAttacks,
                "are power attacks", "each turn tries every on/off combination"));

            foreach (var cost in V.Setup.Set.Values)
            {
                Once(context, build, problems, level =>
                {
                    var setups = build.ActiveModifiers(level).Where(m => m.Setup == cost).ToList();
                    return setups.Count > 1
                        ? $"at level {DslText.Number(level)} {Both(setups)} both have setup \"{cost}\", and the first round has one " +
                          $"{(cost == V.Setup.Action ? "Action" : "Bonus Action")}; give one no setup (assume it is already up) or compare " +
                          "them as two builds."
                        : null;
                });
            }

            Once(context, build, problems, level =>
            {
                foreach (var attack in build.ActiveAttacks(level))
                {
                    var sources = GreatWeaponFightingSources(build, attack, level);
                    if (sources.Count > 1)
                    {
                        return $"at level {DslText.Number(level)} {attack.Where} gets Great Weapon Fighting twice ({string.Join(" and ", sources)}); " +
                               "remove one.";
                    }
                }

                return null;
            });
        });
    }

    /// <summary>Where a Great Weapon Fighting remap on an attack comes from: the fighting style and/or modifiers.</summary>
    internal static IReadOnlyList<string> GreatWeaponFightingSources(CompiledBuild build, CompiledAttack attack, int level)
    {
        var sources = new List<string>();
        if (build.FightingStyle == V.FightingStyles.Gwf && attack.TakesGreatWeaponFighting)
        {
            sources.Add("fighting_style gwf");
        }

        sources.AddRange(build.ActiveModifiers(level)
            .Where(m => m.Kind == V.Kinds.DamageDieRemap && V.Remaps.IsGreatWeaponFighting(m.Remap!) && build.AttacksFor(m, level).Contains(attack))
            .Select(m => m.Where));
        return sources;
    }

    private static int FirstLevel(ValidationContext<BuildLevels> context, Func<int, bool> fails) =>
        context.InstanceToValidate.Levels.Distinct().Order().FirstOrDefault(fails);

    // Runs a per-level rule in level order and reports its first failure only.
    private static void Once(ValidationContext<BuildLevels> context, CompiledBuild build, Problems<BuildLevels> problems, Func<int, string?> rule)
    {
        foreach (var level in context.InstanceToValidate.Levels.Distinct().Order())
        {
            if (rule(level) is { } message)
            {
                problems.Add(message);
                return;
            }
        }
    }

    private static string? AtMost(int level, IReadOnlyList<CompiledModifier> modifiers, int max, string what, string why) =>
        modifiers.Count > max
            ? $"at level {DslText.Number(level)}, {DslText.Number(modifiers.Count)} modifiers {what} ({string.Join(", ", modifiers.Select(m => m.Where))}); at most " +
              $"{DslText.Number(max)} are accepted, because {why}."
            : null;

    private static string Both(IReadOnlyList<CompiledModifier> modifiers) =>
        string.Join(" and ", modifiers.Select(m => m.Where));

    private static string ActiveRange(int? from, int? until) => (from, until) switch
    {
        (null, null) => string.Empty,
        ({ } f, null) => $" (it has from_level {DslText.Number(f)})",
        (null, { } u) => $" (it has until_level {DslText.Number(u)})",
        ({ } f, { } u) => $" (it is active at levels {DslText.Number(f)}-{DslText.Number(u)})",
    };
}
