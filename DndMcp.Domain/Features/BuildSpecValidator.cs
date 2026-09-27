using FluentValidation;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// Stage 1 of build validation: everything that does not depend on a level. Required fields, ranges, wire values, the
/// per-kind field table, step-value and dice syntax, unique attack names and attack references. Stage 2
/// (<see cref="BuildLevelValidator"/>) then checks each evaluated level on the compiled build.
///
/// <para>
/// Every message is complete on its own (location, what was wrong, what is accepted) because it reaches the model
/// verbatim; <see cref="DslProblems"/> bundles them into one exception. The validator never throws for bad input: an
/// exception from a parser is caught and reported as a problem, so one call reports up to five of them.
/// </para>
/// </summary>
public sealed class BuildSpecValidator : AbstractValidator<BuildSpec>
{
    public static BuildSpecValidator Instance { get; } = new();

    public BuildSpecValidator()
    {
        RuleFor(b => b.Name).Custom((name, context) => BuildNameProblems(name, new Problems<BuildSpec>(context, string.Empty)));

        RuleFor(b => b.Preset).Custom((preset, context) =>
        {
            var problems = new Problems<BuildSpec>(context, string.Empty);
            if (preset is null || !problems.Known(V.Presets.Set, "preset", preset))
            {
                return;
            }

            var build = context.InstanceToValidate;
            var extra = new[]
                {
                    ("abilities", build.Abilities is not null),
                    ("proficiency_bonus", build.ProficiencyBonus is not null),
                    ("fighting_style", build.FightingStyle is not null),
                    ("attacks", build.Attacks is not null),
                    ("modifiers", build.Modifiers is not null),
                }
                .Where(f => f.Item2)
                .Select(f => f.Item1)
                .ToList();
            if (extra.Count > 0)
            {
                problems.Add(
                    $"with preset \"{V.Presets.Set.Values[0]}\" give only name, edition and level; remove {string.Join(", ", extra)}. " +
                    "To change a preset, write the build out in full.");
            }
        });

        RuleFor(b => b.Edition).Custom((edition, context) =>
            new Problems<BuildSpec>(context, string.Empty).Known(V.Editions.Set, "edition", edition));

        RuleFor(b => b.Level).Custom((level, context) =>
        {
            var problems = new Problems<BuildSpec>(context, string.Empty);
            if (level is null)
            {
                problems.Add("level is required: the character level 1-20 the build describes, e.g. \"level\": 5.");
            }
            else
            {
                problems.InRange("level", level, DslLimits.MinLevel, DslLimits.MaxLevel);
            }
        });

        RuleFor(b => b.ProficiencyBonus).Custom((pb, context) =>
            new Problems<BuildSpec>(context, string.Empty).InRange(
                "proficiency_bonus", pb, DslLimits.MinProficiencyBonus, DslLimits.MaxProficiencyBonus, "leave it out to use the level's"));

        RuleFor(b => b.FightingStyle).Custom((style, context) =>
            new Problems<BuildSpec>(context, string.Empty).Known(V.FightingStyles.Set, "fighting_style", style));

        RuleFor(b => b.Abilities).Custom((abilities, context) =>
            AbilityProblems(abilities, new Problems<BuildSpec>(context, string.Empty)));

        RuleFor(b => b.Attacks).Custom((attacks, context) =>
        {
            var problems = new Problems<BuildSpec>(context, string.Empty);
            var build = context.InstanceToValidate;
            if (attacks is { Count: > DslLimits.MaxAttacks })
            {
                problems.Add($"attacks has {DslText.Number(attacks.Count)} items; at most {DslLimits.MaxAttacks} are accepted.");
            }

            if (build.Preset is null && (attacks ?? []).Count == 0 && (build.Modifiers ?? []).Count == 0)
            {
                problems.Add(
                    "a build needs at least one attack (or a save_effect modifier), e.g. \"attacks\": [{\"name\": \"Longsword\", " +
                    "\"damage\": \"1d8\", \"damage_type\": \"slashing\", \"properties\": [\"melee\", \"versatile\"]}], or a preset.");
            }
        });

        RuleFor(b => b.Modifiers).Custom((modifiers, context) =>
        {
            if (modifiers is { Count: > DslLimits.MaxModifiers })
            {
                new Problems<BuildSpec>(context, string.Empty).Add(
                    $"modifiers has {DslText.Number(modifiers.Count)} items; at most {DslLimits.MaxModifiers} are accepted.");
            }
        });

        RuleForEach(b => AttackItem.Of(b.Attacks)).SetValidator(AttackItemValidator.Instance).OverridePropertyName("attacks");
        RuleForEach(b => ModifierItem.Of(b.Modifiers, b.Attacks)).SetValidator(ModifierItemValidator.Instance).OverridePropertyName("modifiers");
    }

    /// <summary>A build or feature name: required, one line, at most <see cref="DslLimits.MaxBuildNameLength"/> characters.</summary>
    internal static void BuildNameProblems<T>(string? name, Problems<T> problems)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add("name is required: a label such as \"L5 Fighter, GWM\".");
        }
        else if (!DslText.IsOneLine(name.Trim(), DslLimits.MaxBuildNameLength))
        {
            problems.Add($"name must be one line of at most {DslLimits.MaxBuildNameLength} characters (got \"{DslText.Echo(name)}\").");
        }
    }

    /// <summary>Each ability a step value of an integer 1–30.</summary>
    internal static void AbilityProblems<T>(AbilitiesSpec? abilities, Problems<T> problems)
    {
        if (abilities is null)
        {
            return;
        }

        foreach (var ability in V.Abilities.All)
        {
            problems.Parse(() => LevelValue.ParseInt(
                abilities.Get(ability), $"abilities {ability}", DslLimits.MinAbilityScore, DslLimits.MaxAbilityScore,
                "{\"1\": 16, \"4\": 18, \"8\": 20}"));
        }
    }
}

/// <summary>An attack item with its position, for messages ("attacks item 2 (Greatsword)").</summary>
/// <param name="SameNameAs">The earlier item with the same name (case ignored), if any.</param>
internal sealed record AttackItem(int Number, AttackSpec? Spec, int? SameNameAs)
{
    public string Where => Spec?.Name is { } name && !string.IsNullOrWhiteSpace(name)
        ? $"attacks item {DslText.Number(Number)} ({DslText.Echo(name.Trim())})"
        : $"attacks item {DslText.Number(Number)}";

    public static IEnumerable<AttackItem> Of(IReadOnlyList<AttackSpec?>? attacks)
    {
        var list = attacks ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            var name = list[i]?.Name?.Trim();
            int? first = null;
            for (var j = 0; j < i && name is { Length: > 0 }; j++)
            {
                if (string.Equals(list[j]?.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    first = j + 1;
                    break;
                }
            }

            yield return new AttackItem(i + 1, list[i], first);
        }
    }
}

/// <summary>
/// Stage 1 checks of one attack item. See <see cref="BuildSpecValidator"/>.
/// </summary>
internal sealed class AttackItemValidator : AbstractValidator<AttackItem>
{
    public static AttackItemValidator Instance { get; } = new();

    private AttackItemValidator()
    {
        RuleFor(i => i.Spec).Custom((spec, context) =>
        {
            if (spec is null)
            {
                new Problems<AttackItem>(context, context.InstanceToValidate.Where).Add(
                    "is null; give an attack, e.g. {\"name\": \"Longsword\", \"damage\": \"1d8\", \"properties\": [\"melee\"]}.");
            }
        });

        When(i => i.Spec is not null, () =>
        {
            RuleFor(i => i.Spec!.Name).Custom((name, context) =>
            {
                var item = context.InstanceToValidate;
                var problems = new Problems<AttackItem>(context, item.Where);
                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add("name is required: modifiers refer to the attack by it, e.g. \"Greatsword\".");
                }
                else if (!DslText.IsOneLine(name.Trim(), DslLimits.MaxItemNameLength))
                {
                    problems.Add($"name must be one line of at most {DslLimits.MaxItemNameLength} characters.");
                }
                else if (item.SameNameAs is { } first)
                {
                    problems.Add($"name \"{DslText.Echo(name.Trim())}\" is also attacks item {DslText.Number(first)}'s; names must differ (case is ignored).");
                }
            });

            RuleFor(i => i.Spec!).Custom((spec, context) =>
            {
                var problems = new Problems<AttackItem>(context, context.InstanceToValidate.Where);
                problems.Parse(() => LevelValue.ParseInt(spec.Count, "count", 1, DslLimits.MaxAttackCount));
                problems.Known(V.AttackActions.Set, "action", spec.Action);

                if (spec.ToHit is { } toHit)
                {
                    problems.Known(V.Abilities.ToHitSet, "to_hit ability", toHit.Ability);
                    problems.InRange("to_hit bonus", toHit.Bonus, DslLimits.MinToHitBonus, DslLimits.MaxToHitBonus);
                    problems.InRange("to_hit total", toHit.Total, DslLimits.MinToHitTotal, DslLimits.MaxToHitTotal);
                }

                if (!LevelValue.IsGiven(spec.Damage))
                {
                    problems.Add("damage is required: dice and whole numbers such as \"2d6\" or \"1d8+1\" (the ability modifier is added for you).");
                }
                else
                {
                    problems.Parse(() => LevelValue.ParseDamage(spec.Damage, "damage"));
                }

                problems.Known(V.DamageTypes.Set, "damage_type", spec.DamageType);
                problems.Known(V.Masteries.Set, "mastery", spec.Mastery);
                problems.Known(V.Cantrips.Set, "cantrip", spec.Cantrip);
                PropertyProblems(spec.Properties, problems);
                LevelRangeProblems(spec.FromLevel, spec.UntilLevel, problems);
            });
        });
    }

    /// <summary>from_level and until_level: each 1–20, and not reversed.</summary>
    internal static void LevelRangeProblems<T>(int? from, int? until, Problems<T> problems)
    {
        var fromOk = problems.InRange("from_level", from, DslLimits.MinLevel, DslLimits.MaxLevel);
        var untilOk = problems.InRange("until_level", until, DslLimits.MinLevel, DslLimits.MaxLevel);
        if (fromOk && untilOk && from > until)
        {
            problems.Add($"from_level {DslText.Number(from!.Value)} is after until_level {DslText.Number(until!.Value)}.");
        }
    }

    private static void PropertyProblems(IReadOnlyList<string?>? properties, Problems<AttackItem> problems)
    {
        var known = new List<string>();
        foreach (var text in properties ?? [])
        {
            if (V.Properties.Set.TryMatch(text, out var property))
            {
                known.Add(property);
            }
            else
            {
                problems.Add(
                    $"properties has \"{DslText.Echo(text)}\", which is not an attack property; they are {V.Properties.Set.List}.");
            }
        }

        if (known.Contains(V.Properties.Melee) && known.Contains(V.Properties.Ranged))
        {
            problems.Add(
                "properties has both melee and ranged; an attack is one or the other (a thrown weapon used both ways is two " +
                "attacks, or pick how it is used).");
        }
    }
}

/// <summary>A modifier item with its position and the build's attacks (for attack references).</summary>
internal sealed record ModifierItem(int Number, ModifierSpec? Spec, IReadOnlyList<AttackSpec?> Attacks)
{
    /// <summary>The canonical kind, or null when missing or unknown.</summary>
    public string? Kind => V.Kinds.Set.TryMatch(Spec?.Kind, out var kind) ? kind : null;

    public string Where => WhereOf(Number, Kind, Spec?.Name);

    /// <summary>"modifiers item 3 (extra_damage \"Hex\")", "modifiers item 3 (extra_damage)", "modifiers item 3".</summary>
    public static string WhereOf(int number, string? kind, string? name)
    {
        var label = string.IsNullOrWhiteSpace(name) ? string.Empty : $" \"{DslText.Echo(name.Trim())}\"";
        return kind is null
            ? $"modifiers item {DslText.Number(number)}"
            : $"modifiers item {DslText.Number(number)} ({kind}{label})";
    }

    public static IEnumerable<ModifierItem> Of(IReadOnlyList<ModifierSpec?>? modifiers, IReadOnlyList<AttackSpec?>? attacks)
    {
        var list = modifiers ?? [];
        for (var i = 0; i < list.Count; i++)
        {
            yield return new ModifierItem(i + 1, list[i], attacks ?? []);
        }
    }

    /// <summary>The attack named <paramref name="name"/> (case ignored), or null.</summary>
    public AttackSpec? FindAttack(string name) =>
        Attacks.FirstOrDefault(a => string.Equals(a?.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>"\"Greatsword\", \"Longbow\"": the build's attack names for a message.</summary>
    public string AttackNames =>
        Attacks.Count == 0
            ? "none"
            : string.Join(", ", Attacks.Where(a => !string.IsNullOrWhiteSpace(a?.Name)).Select(a => $"\"{DslText.Echo(a!.Name!.Trim())}\""));
}

/// <summary>
/// Stage 1 checks of one modifier item: its kind, the per-kind field table (<see cref="ModifierFields"/>), the fields
/// every kind takes, then the kind's own fields and the rules between them.
/// </summary>
internal sealed class ModifierItemValidator : AbstractValidator<ModifierItem>
{
    public static ModifierItemValidator Instance { get; } = new();

    private ModifierItemValidator()
    {
        RuleFor(i => i.Spec).Custom((spec, context) =>
        {
            var problems = new Problems<ModifierItem>(context, context.InstanceToValidate.Where);
            if (spec is null)
            {
                problems.Add("is null; give a modifier with a kind, e.g. {\"kind\": \"to_hit\", \"amount\": 1}.");
            }
            else if (spec.Kind is null)
            {
                problems.Add($"kind is required; kinds are {V.Kinds.Set.List}.");
            }
            else if (context.InstanceToValidate.Kind is null)
            {
                problems.Add($"kind \"{DslText.Echo(spec.Kind)}\" is not a modifier kind; kinds are {V.Kinds.Set.List}.");
            }
        });

        When(i => i.Kind is not null, () =>
        {
            RuleFor(i => i.Spec!).Custom((spec, context) =>
            {
                var item = context.InstanceToValidate;
                var refused = ModifierFields.Refused(item.Kind!, spec);
                if (refused.Count > 0)
                {
                    var names = string.Join(" or ", refused.Select(f => $"\"{f}\""));
                    new Problems<ModifierItem>(context, item.Where).Add(
                        $"does not take {names}; {item.Kind} takes {ModifierFields.Describe(item.Kind!)}.");
                }
            });

            RuleFor(i => i.Spec!).Custom((spec, context) => CommonProblems(context.InstanceToValidate, spec, new Problems<ModifierItem>(context, context.InstanceToValidate.Where)));

            RuleFor(i => i.Spec!).Custom((spec, context) => KindProblems(context.InstanceToValidate, spec, new Problems<ModifierItem>(context, context.InstanceToValidate.Where)));
        });
    }

    private static void CommonProblems(ModifierItem item, ModifierSpec spec, Problems<ModifierItem> problems)
    {
        if (spec.Name is not null && (string.IsNullOrWhiteSpace(spec.Name) || !DslText.IsOneLine(spec.Name.Trim(), DslLimits.MaxItemNameLength)))
        {
            problems.Add($"name must be one line of 1 to {DslLimits.MaxItemNameLength} characters.");
        }

        if (spec.Attacks is { } names && ModifierFields.Takes(item.Kind!, "attacks"))
        {
            if (names.Count == 0)
            {
                problems.Add("attacks is empty; leave it out to apply to every attack it can, or name attacks.");
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name) || item.FindAttack(name) is null)
                {
                    problems.Add(
                        $"attacks names \"{DslText.Echo(name)}\", which is not an attack of this build; its attacks are {item.AttackNames}.");
                }
            }
        }

        AttackItemValidator.LevelRangeProblems(spec.FromLevel, spec.UntilLevel, problems);
        problems.Known(V.Setup.Set, "setup", spec.Setup);

        if (spec.Resource is { } resource && ModifierFields.Takes(item.Kind!, "resource"))
        {
            if (!LevelValue.IsGiven(resource.Uses) || resource.Per is null)
            {
                problems.Add(
                    "resource needs uses (1-20, or a step map by level) and per (\"short_rest\" or \"long_rest\"), e.g. " +
                    "{\"uses\": 2, \"per\": \"short_rest\"}.");
            }

            problems.Parse(() => LevelValue.ParseInt(resource.Uses, "resource uses", 1, DslLimits.MaxResourceUses, "{\"1\": 1, \"17\": 2}"));
            problems.Known(V.Rests.Set, "resource per", resource.Per);
        }
    }

    private static void KindProblems(ModifierItem item, ModifierSpec spec, Problems<ModifierItem> problems)
    {
        switch (item.Kind)
        {
            case V.Kinds.ToHit:
                if (!LevelValue.IsGiven(spec.Amount) && !LevelValue.IsGiven(spec.Dice))
                {
                    problems.Add("to_hit needs amount (e.g. 1) or dice (e.g. \"1d4\" for Bless, \"-1d4\" for Bane).");
                }

                problems.Parse(() => LevelValue.ParseAmount(spec.Amount, "amount"));
                problems.Parse(() => LevelValue.ParseBonusDice(spec.Dice, "dice", CompiledModifier.ToHitDiceFlatHint));
                break;

            case V.Kinds.ExtraDamage:
                ExtraDamageProblems(spec, problems);
                break;

            case V.Kinds.BonusDamage:
                if (!LevelValue.IsGiven(spec.Amount))
                {
                    problems.Add("bonus_damage needs amount: a whole number, \"pb\" or an ability such as \"cha\".");
                }

                problems.Parse(() => LevelValue.ParseAmount(spec.Amount, "amount"));
                break;

            case V.Kinds.CritRange:
                if (!LevelValue.IsGiven(spec.Min))
                {
                    problems.Add("crit_range needs min: the lowest d20 roll that crits, 2-20, e.g. 19.");
                }

                problems.Parse(() => LevelValue.ParseInt(spec.Min, "min", 2, 20, "{\"3\": 19, \"15\": 18}"));
                break;

            case V.Kinds.Advantage:
                problems.Known(V.AdvantageModes.Set, "mode", spec.Mode);
                problems.InRange("rate", spec.Rate, 0, 1);
                break;

            case V.Kinds.ElvenAccuracy:
                foreach (var name in spec.Attacks ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(name) && item.FindAttack(name) is { } attack &&
                        V.Abilities.ToHitSet.TryMatch(attack.ToHit?.Ability ?? V.Abilities.Str, out var ability) &&
                        !V.Abilities.ElvenAccuracyAbilities.Contains(ability))
                    {
                        problems.Add(
                            $"Elven Accuracy works only on attack rolls using Dex, Int, Wis or Cha; \"{DslText.Echo(attack.Name!.Trim())}\" " +
                            $"uses {V.Abilities.Display(ability)}.");
                    }
                }

                break;

            case V.Kinds.DamageDieRemap:
                if (spec.Remap is null)
                {
                    problems.Add($"damage_die_remap needs remap: {Problems<ModifierItem>.Or(V.Remaps.Set)}.");
                }
                else if (problems.Known(V.Remaps.Set, "remap", spec.Remap) && V.Remaps.Set.TryMatch(spec.Remap, out var remap))
                {
                    if (remap == V.Remaps.ElementalAdept && spec.Type is null)
                    {
                        problems.Add("elemental_adept needs type: the damage type it works on, e.g. \"fire\".");
                    }
                    else if (remap != V.Remaps.ElementalAdept && spec.Type is not null)
                    {
                        problems.Add($"type is only for elemental_adept; {remap} works on the attack's own dice whatever their type.");
                    }
                }

                problems.Known(V.DamageTypes.Set, "type", spec.Type);
                break;

            case V.Kinds.RerollDamageTakeBest:
                PolicyProblems(spec, problems);
                break;

            case V.Kinds.ExtraAttack:
                ExtraAttackProblems(item, spec, problems);
                break;

            case V.Kinds.PowerAttack:
                problems.InRange("penalty", spec.Penalty, 1, DslLimits.MaxPowerAttackPenalty);
                problems.InRange("bonus", spec.Bonus, 1, DslLimits.MaxPowerAttackBonus);
                problems.Known(V.PowerAttackPolicies.Set, "policy", spec.Policy);
                break;

            case V.Kinds.SaveEffect:
                SaveEffectProblems(spec, problems);
                break;

            case V.Kinds.ConditionOnHit:
                if (spec.Condition is null)
                {
                    problems.Add($"condition_on_hit needs condition: {Problems<ModifierItem>.Or(V.Conditions.OnHitSet)}.");
                }

                problems.Known(V.Conditions.OnHitSet, "condition", spec.Condition);
                SaveProblems(spec, problems, dcRequired: false);
                problems.Known(V.When.ConditionOnHitSet, "when", spec.When);
                PolicyProblems(spec, problems);
                break;

            case V.Kinds.Ac or V.Kinds.Resistance or V.Kinds.TempHp:
                problems.Parse(() => LevelValue.ParseAmount(spec.Amount, "amount"));
                problems.Known(V.DamageTypes.Set, "type", spec.Type);
                break;
        }
    }

    private static void ExtraDamageProblems(ModifierSpec spec, Problems<ModifierItem> problems)
    {
        if (!LevelValue.IsGiven(spec.Dice) && !LevelValue.IsGiven(spec.Amount))
        {
            problems.Add("extra_damage needs dice (e.g. \"1d6\") or amount (e.g. 2).");
        }

        problems.Parse(() => LevelValue.ParseDamage(spec.Dice, "dice"));
        problems.Parse(() => LevelValue.ParseAmount(spec.Amount, "amount"));
        problems.Known(V.DamageTypes.Set, "type", spec.Type);
        problems.Known(V.ActionCosts.RiderSet, "action_cost", spec.ActionCost);
        if (!problems.Known(V.When.ExtraDamageSet, "when", spec.When))
        {
            return;
        }

        V.When.ExtraDamageSet.TryMatch(spec.When ?? V.When.EveryHit, out var when);
        if (when is V.When.OnCrit or V.When.OnMiss)
        {
            var given = new[]
                {
                    ("policy", spec.Policy is not null),
                    ("use_value", spec.UseValue is not null),
                    ("resource", spec.Resource is not null),
                    ("action_cost", spec.ActionCost is not null),
                    ("crit_doubles", spec.CritDoubles is not null),
                }
                .Where(f => f.Item2)
                .Select(f => f.Item1)
                .ToList();
            if (given.Count > 0)
            {
                problems.Add(
                    $"an {when} rider is applied every time it can be, with its dice added once (not doubled), so it takes no " +
                    $"{string.Join(", ", given)}; remove {(given.Count == 1 ? "it" : "them")}, or use when \"first_hit_per_turn\" for a " +
                    "rider that is a choice.");
            }

            return;
        }

        var optional = when == V.When.FirstHitPerTurn || spec.Resource is not null || spec.ActionCost is not null;
        if (spec.Policy is not null && !optional)
        {
            problems.Add(
                "policy applies only to optional riders (first_hit_per_turn, or every_hit with a resource or action_cost); this " +
                "every_hit rider is always applied. Remove policy, or add the resource that limits it.");
            return;
        }

        PolicyProblems(spec, problems);
    }

    private static void PolicyProblems(ModifierSpec spec, Problems<ModifierItem> problems)
    {
        if (!problems.Known(V.Policies.Set, "policy", spec.Policy))
        {
            return;
        }

        if (!problems.InRange("use_value", spec.UseValue, 0, DslLimits.MaxUseValue))
        {
            return;
        }

        if (spec.UseValue is not null && !(V.Policies.Set.TryMatch(spec.Policy, out var policy) && policy == V.Policies.Optimal))
        {
            problems.Add("use_value is used only with policy \"optimal\" (the damage one use is worth elsewhere); add the policy or remove use_value.");
        }
    }

    private static void ExtraAttackProblems(ModifierItem item, ModifierSpec spec, Problems<ModifierItem> problems)
    {
        if (string.IsNullOrWhiteSpace(spec.Attack))
        {
            problems.Add($"extra_attack needs attack: the name of the attack it makes; this build's attacks are {item.AttackNames}.");
        }
        else if (item.FindAttack(spec.Attack) is null)
        {
            problems.Add($"attack \"{DslText.Echo(spec.Attack)}\" is not an attack of this build; its attacks are {item.AttackNames}.");
        }

        problems.Parse(() => LevelValue.ParseInt(spec.Count, "count", 1, DslLimits.MaxAttackCount));
        if (spec.Action is null)
        {
            problems.Add(
                "extra_attack needs action: \"bonus_action\" (e.g. Great Weapon Master's crit attack), \"action\" (Action Surge, with " +
                "a resource) or \"reaction\" (with trigger_probability).");
            return;
        }

        if (!problems.Known(V.ExtraAttackActions.Set, "action", spec.Action) || !problems.Known(V.Triggers.Set, "trigger", spec.Trigger))
        {
            return;
        }

        V.ExtraAttackActions.Set.TryMatch(spec.Action, out var action);
        V.Triggers.Set.TryMatch(spec.Trigger ?? V.Triggers.Always, out var trigger);
        if (action == V.ExtraAttackActions.Action && spec.Resource is null)
        {
            problems.Add(
                "an extra_attack with action \"action\" (Action Surge) needs a resource, e.g. {\"uses\": 1, \"per\": \"short_rest\"}; " +
                "for attacks made every turn, raise the attack's count instead.");
        }

        if (trigger != V.Triggers.Always && action != V.ExtraAttackActions.BonusAction)
        {
            problems.Add($"trigger \"{trigger}\" is only for bonus_action extra attacks; {action} extra attacks take trigger \"always\" (or none).");
        }

        if (action == V.ExtraAttackActions.Reaction && spec.TriggerProbability is null)
        {
            problems.Add("a reaction extra_attack needs trigger_probability: the chance per round it happens, 0-1, e.g. 0.3.");
        }
        else if (action != V.ExtraAttackActions.Reaction && spec.TriggerProbability is not null)
        {
            problems.Add("trigger_probability is only for reaction extra attacks; remove it.");
        }

        problems.InRange("trigger_probability", spec.TriggerProbability, 0, 1);
    }

    private static void SaveEffectProblems(ModifierSpec spec, Problems<ModifierItem> problems)
    {
        SaveProblems(spec, problems, dcRequired: true);
        problems.Parse(() => LevelValue.ParseDamage(spec.Dice, "dice"));
        problems.Parse(() => LevelValue.ParseAmount(spec.Amount, "amount"));
        if (!LevelValue.IsGiven(spec.Dice) && !LevelValue.IsGiven(spec.Amount) && spec.Condition is null)
        {
            problems.Add("save_effect does nothing: give dice (e.g. \"8d6\"), amount, or a condition.");
        }

        if (spec.Cantrip == true && !LevelValue.IsGiven(spec.Dice))
        {
            problems.Add("cantrip scales the dice, so it needs dice, e.g. \"1d8\" for Sacred Flame.");
        }

        problems.Known(V.DamageTypes.Set, "type", spec.Type);
        problems.Known(V.OnSuccess.Set, "on_success", spec.OnSuccess);
        problems.Known(V.Conditions.SaveEffectSet, "condition", spec.Condition);
        problems.Known(V.ActionCosts.SaveEffectSet, "action_cost", spec.ActionCost);
        problems.InRange("targets", spec.Targets, 1, DslLimits.MaxTargets);

        if (spec.Targets is not null && (spec.Shape is not null || spec.Size is not null))
        {
            problems.Add("give targets or an area (shape and size), not both.");
        }
        else if ((spec.Shape is null) != (spec.Size is null))
        {
            problems.Add("an area needs both shape and size, e.g. \"shape\": \"sphere\", \"size\": 20 for Fireball.");
        }
        else if (problems.Known(V.Shapes.Set, "shape", spec.Shape) &&
                 problems.InRange("size", spec.Size, 1, DslLimits.MaxAreaSize) &&
                 spec.Shape is not null && V.Shapes.Set.TryMatch(spec.Shape, out var shape))
        {
            var targets = V.Shapes.Targets(shape, spec.Size!.Value);
            if (targets > DslLimits.MaxTargets)
            {
                problems.Add(
                    $"a {shape} of {DslText.Number(spec.Size.Value)} ft covers {DslText.Number(targets)} creatures by the DMG's table; at " +
                    $"most {DslLimits.MaxTargets} are modelled. Give targets instead.");
            }
        }
    }

    /// <summary>
    /// The save and its DC: ability required; dc or dc_ability (not both). A save effect must give one of them and may add
    /// dc_bonus to dc_ability; a condition on hit defaults to its attack's ability and takes no dc_bonus.
    /// </summary>
    private static void SaveProblems(ModifierSpec spec, Problems<ModifierItem> problems, bool dcRequired)
    {
        if (spec.Ability is null)
        {
            problems.Add("ability is required: the saving throw the target makes, e.g. \"dex\".");
        }

        problems.Known(V.Abilities.Set, "ability", spec.Ability);
        problems.Known(V.Abilities.Set, "dc_ability", spec.DcAbility);
        problems.InRange("dc", spec.Dc, DslLimits.MinDc, DslLimits.MaxDc);
        if (dcRequired)
        {
            problems.InRange("dc_bonus", spec.DcBonus, -DslLimits.MaxDcBonus, DslLimits.MaxDcBonus);
        }

        if (spec.Dc is not null && spec.DcAbility is not null)
        {
            problems.Add("give dc or dc_ability, not both.");
        }
        else if (dcRequired && spec.Dc is null && spec.DcAbility is null)
        {
            problems.Add("needs dc (e.g. 15) or dc_ability (e.g. \"cha\" for 8 + proficiency bonus + Cha).");
        }

        if (dcRequired && spec.DcBonus is not null && spec.DcAbility is null)
        {
            problems.Add("dc_bonus adds to a DC from dc_ability; with dc, give the final DC.");
        }
    }
}
