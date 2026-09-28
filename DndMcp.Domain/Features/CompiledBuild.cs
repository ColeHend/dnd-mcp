using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// A validated spec with every field parsed, canonical and defaulted, but still level-independent: the step values are
/// <see cref="LevelValue{T}"/>s. The step between validation and resolution, so a build evaluated at 20 levels is parsed
/// once, and so the per-level validator (<see cref="BuildLevelValidator"/>) and <see cref="BuildResolver"/> read the same
/// parsed values and the same attack filters.
///
/// <para>
/// <see cref="Compile"/> assumes <see cref="BuildSpecValidator"/> passed. Parsing again cannot fail then; if it does, the
/// <see cref="Core.DndInputException"/> still carries a usable message, but that is a validator gap to fix.
/// </para>
/// </summary>
internal sealed class CompiledBuild
{
    public required string Name { get; init; }

    public required string Edition { get; init; }

    public required int Level { get; init; }

    public int? ProficiencyBonusOverride { get; init; }

    public string? FightingStyle { get; init; }

    /// <summary>All six abilities (10 when not given), keyed by ability key.</summary>
    public required IReadOnlyDictionary<string, LevelValue<int>> Abilities { get; init; }

    public required IReadOnlyList<CompiledAttack> Attacks { get; init; }

    public required IReadOnlyList<CompiledModifier> Modifiers { get; init; }

    /// <summary>
    /// Any step value with two or more steps, any from_level/until_level, or any cantrip scaling (contract §3.1).
    /// </summary>
    public bool ScalesWithLevel =>
        Abilities.Values.Any(a => a.Scales) ||
        Attacks.Any(a => a.FromLevel is not null || a.UntilLevel is not null || a.Cantrip is not null || a.Count.Scales || a.Damage.Scales) ||
        Modifiers.Any(m => m.FromLevel is not null || m.UntilLevel is not null || m.Cantrip || m.StepValues().Any(s => s.Value.Scales));

    public int ProficiencyBonusAt(int level) => ProficiencyBonusOverride ?? DslLimits.ProficiencyBonus(level);

    public ResolvedAbilities AbilitiesAt(int level) => new(
        Abilities[V.Abilities.Str].At(level),
        Abilities[V.Abilities.Dex].At(level),
        Abilities[V.Abilities.Con].At(level),
        Abilities[V.Abilities.Int].At(level),
        Abilities[V.Abilities.Wis].At(level),
        Abilities[V.Abilities.Cha].At(level));

    public IReadOnlyList<CompiledAttack> ActiveAttacks(int level) => Attacks.Where(a => a.IsActiveAt(level)).ToList();

    public IReadOnlyList<CompiledModifier> ActiveModifiers(int level) => Modifiers.Where(m => m.IsActiveAt(level)).ToList();

    public CompiledAttack? FindAttack(string name) =>
        Attacks.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The active attacks a modifier applies to at a level: its explicit <c>attacks</c> list (restricted to attacks active
    /// there), or by default every active attack its kind can apply to — Elven Accuracy: Dex/Int/Wis/Cha attacks; GWF
    /// remaps: two-handed or versatile melee weapons; Savage Attacker and power attacks: weapons; everything else: all.
    /// </summary>
    public IReadOnlyList<CompiledAttack> AttacksFor(CompiledModifier modifier, int level)
    {
        var active = ActiveAttacks(level);
        if (modifier.AttackFilter is { } names)
        {
            return active.Where(a => names.Contains(a.Name, StringComparer.Ordinal)).ToList();
        }

        return modifier.Kind switch
        {
            V.Kinds.ElvenAccuracy => active.Where(a => V.Abilities.ElvenAccuracyAbilities.Contains(a.Ability)).ToList(),
            V.Kinds.DamageDieRemap when V.Remaps.IsGreatWeaponFighting(modifier.Remap!) => active.Where(a => a.TakesGreatWeaponFighting).ToList(),
            V.Kinds.RerollDamageTakeBest or V.Kinds.PowerAttack => active.Where(a => a.IsWeapon).ToList(),
            _ => active,
        };
    }

    /// <summary>Compiles a spec that <see cref="BuildSpecValidator"/> accepted (and whose preset is expanded).</summary>
    public static CompiledBuild Compile(BuildSpec spec)
    {
        if (spec.Preset is not null)
        {
            throw new InvalidOperationException("Expand the preset (BuildPresets.Expand) before compiling.");
        }

        var abilities = new Dictionary<string, LevelValue<int>>(StringComparer.Ordinal);
        foreach (var ability in V.Abilities.All)
        {
            abilities[ability] = LevelValue.ParseInt(spec.Abilities?.Get(ability), $"abilities {ability}", DslLimits.MinAbilityScore, DslLimits.MaxAbilityScore)
                                 ?? LevelValue<int>.Of(DslLimits.DefaultAbilityScore);
        }

        var attacks = (spec.Attacks ?? []).Select((a, i) => CompiledAttack.Compile(i + 1, a)).ToList();
        var modifiers = (spec.Modifiers ?? []).Select((m, i) => CompiledModifier.Compile(i + 1, m, attacks)).ToList();

        return new CompiledBuild
        {
            Name = spec.Name!.Trim(),
            Edition = Match(V.Editions.Set, spec.Edition) ?? V.Editions.Default,
            Level = spec.Level!.Value,
            ProficiencyBonusOverride = spec.ProficiencyBonus,
            FightingStyle = Match(V.FightingStyles.Set, spec.FightingStyle),
            Abilities = abilities,
            Attacks = attacks,
            Modifiers = modifiers,
        };
    }

    /// <summary>The canonical value of an optional field the validator accepted, or null when not given.</summary>
    internal static string? Match(DslValueSet set, string? text) =>
        text is null ? null : set.TryMatch(text, out var canonical) ? canonical : throw new InvalidOperationException($"Unvalidated {set.What} \"{text}\".");
}

/// <summary>An attack item, parsed and defaulted.</summary>
internal sealed class CompiledAttack
{
    public required int Number { get; init; }

    public required string Name { get; init; }

    public required LevelValue<int> Count { get; init; }

    public required string Action { get; init; }

    public required string Ability { get; init; }

    /// <summary>
    /// Whether to_hit.ability was given. The default is Str even for a ranged, finesse or spell attack (contract §3.3), so
    /// the resolver notes an attack that would do better with the ability the rules give it.
    /// </summary>
    public required bool AbilityGiven { get; init; }

    public required bool Proficient { get; init; }

    public required int ToHitBonus { get; init; }

    public int? ToHitTotal { get; init; }

    /// <summary>proficient or bonus was given beside total (ignored then; the result notes it).</summary>
    public required bool PartsBesideTotal { get; init; }

    public required LevelValue<DamageFormula> Damage { get; init; }

    public string? DamageType { get; init; }

    /// <summary>As given; null means the default (true, or false for an offhand attack).</summary>
    public bool? AbilityToDamage { get; init; }

    public required IReadOnlyList<string> Properties { get; init; }

    public required bool Offhand { get; init; }

    public string? Mastery { get; init; }

    public string? Cantrip { get; init; }

    public int? FromLevel { get; init; }

    public int? UntilLevel { get; init; }

    public string Where => $"attacks item {DslText.Number(Number)} ({DslText.Echo(Name)})";

    public bool IsRanged => Properties.Contains(V.Properties.Ranged);

    public bool IsMelee => !IsRanged;

    public bool IsSpell => Properties.Contains(V.Properties.Spell);

    public bool IsWeapon => !IsSpell;

    /// <summary>
    /// A thrown weapon made at range, read as a MELEE weapon thrown (javelin, dagger, handaxe, spear: the SRD's thrown
    /// weapons are melee weapons, except the dart and the net). For the fighting styles only: Archery ("ranged weapons")
    /// skips it and Dueling ("a melee weapon") takes it. How the attack is MADE (Prone, GWF, Cleave) still reads
    /// <see cref="IsRanged"/>.
    /// </summary>
    public bool IsThrownMeleeWeapon => IsRanged && IsWeapon && Properties.Contains(V.Properties.Thrown);

    /// <summary>Great Weapon Fighting's reach: a melee weapon with the Two-Handed or Versatile property.</summary>
    public bool TakesGreatWeaponFighting =>
        IsMelee && IsWeapon && (Properties.Contains(V.Properties.TwoHanded) || Properties.Contains(V.Properties.Versatile));

    public bool IsActiveAt(int level) => (FromLevel ?? DslLimits.MinLevel) <= level && level <= (UntilLevel ?? DslLimits.MaxLevel);

    public static CompiledAttack Compile(int number, AttackSpec spec)
    {
        var toHit = spec.ToHit;
        var properties = new List<string>();
        foreach (var text in spec.Properties ?? [])
        {
            var property = CompiledBuild.Match(V.Properties.Set, text)!;
            if (!properties.Contains(property))
            {
                properties.Add(property);
            }
        }

        return new CompiledAttack
        {
            Number = number,
            Name = spec.Name!.Trim(),
            Count = LevelValue.ParseInt(spec.Count, "count", 1, DslLimits.MaxAttackCount) ?? LevelValue<int>.Of(1),
            Action = CompiledBuild.Match(V.AttackActions.Set, spec.Action) ?? V.AttackActions.Action,
            Ability = CompiledBuild.Match(V.Abilities.ToHitSet, toHit?.Ability) ?? V.Abilities.Str,
            AbilityGiven = toHit?.Ability is not null,
            Proficient = toHit?.Proficient ?? true,
            ToHitBonus = toHit?.Bonus ?? 0,
            ToHitTotal = toHit?.Total,
            PartsBesideTotal = toHit is { Total: not null } && (toHit.Proficient is not null || toHit.Bonus is not null),
            Damage = LevelValue.ParseDamage(spec.Damage, "damage", hint: DamageFormula.AttackHint)!,
            DamageType = CompiledBuild.Match(V.DamageTypes.Set, spec.DamageType),
            AbilityToDamage = spec.AbilityToDamage,
            Properties = properties,
            Offhand = spec.Offhand ?? false,
            Mastery = CompiledBuild.Match(V.Masteries.Set, spec.Mastery),
            Cantrip = CompiledBuild.Match(V.Cantrips.Set, spec.Cantrip),
            FromLevel = spec.FromLevel,
            UntilLevel = spec.UntilLevel,
        };
    }
}

/// <summary>A resource, parsed.</summary>
internal sealed record CompiledResource(LevelValue<int> Uses, string Per);

/// <summary>
/// A modifier item, parsed and with its kind's defaults applied. Fields a kind does not take are null/default (the
/// validator refused them), so the resolver can read them without re-checking the kind.
/// </summary>
internal sealed class CompiledModifier
{
    /// <summary>The flat-bonus hint for to_hit dice with a whole number in them.</summary>
    public const string ToHitDiceFlatHint = "put a flat bonus in amount";

    public required int Number { get; init; }

    public required string Kind { get; init; }

    public string? Name { get; init; }

    /// <summary>Canonical attack names from <c>attacks</c>, or null for the kind's default.</summary>
    public IReadOnlyList<string>? AttackFilter { get; init; }

    public int? FromLevel { get; init; }

    public int? UntilLevel { get; init; }

    public CompiledResource? Resource { get; init; }

    public required bool Concentration { get; init; }

    public string? Setup { get; init; }

    public LevelValue<DslAmount>? Amount { get; init; }

    /// <summary>Damage dice (extra_damage, save_effect) or bonus dice (to_hit).</summary>
    public LevelValue<DamageFormula>? Dice { get; init; }

    public string? DamageType { get; init; }

    public string? When { get; init; }

    public string? Policy { get; init; }

    public double UseValue { get; init; }

    public bool CritDoubles { get; init; }

    public bool AttackActionOnly { get; init; }

    public string? ActionCost { get; init; }

    public LevelValue<int>? Min { get; init; }

    public string? Mode { get; init; }

    public double Rate { get; init; }

    public string? Remap { get; init; }

    public string? Attack { get; init; }

    public LevelValue<int>? Count { get; init; }

    public string? Action { get; init; }

    public string? Trigger { get; init; }

    public double? TriggerProbability { get; init; }

    public int Penalty { get; init; }

    public int Bonus { get; init; }

    public string? Ability { get; init; }

    public int? Dc { get; init; }

    public string? DcAbility { get; init; }

    public int DcBonus { get; init; }

    public string? OnSuccess { get; init; }

    public int? Targets { get; init; }

    public string? Shape { get; init; }

    public int? Size { get; init; }

    public bool Magical { get; init; }

    public string? Condition { get; init; }

    public bool Cantrip { get; init; }

    /// <summary><see cref="V.Durations"/> value as given, or null for the default the simulator applies.</summary>
    public string? Duration { get; init; }

    public bool SelfOnly { get; init; }

    public ModifierRef Ref => new(Number, Kind, Name);

    /// <summary>The name, or "kind #N" when none was given: how results label it.</summary>
    public string Label => Ref.Label;

    public string Where => Ref.Where;

    public bool IsActiveAt(int level) => (FromLevel ?? DslLimits.MinLevel) <= level && level <= (UntilLevel ?? DslLimits.MaxLevel);

    /// <summary>Every step value of the modifier with its field name (for coverage checks and "scales with level").</summary>
    public IEnumerable<(string Field, IStepCoverage Value)> StepValues()
    {
        if (Amount is not null)
        {
            yield return ("amount", new StepCoverage<DslAmount>(Amount));
        }

        if (Dice is not null)
        {
            yield return ("dice", new StepCoverage<DamageFormula>(Dice));
        }

        if (Min is not null)
        {
            yield return ("min", new StepCoverage<int>(Min));
        }

        if (Count is not null)
        {
            yield return ("count", new StepCoverage<int>(Count));
        }

        if (Resource is not null)
        {
            yield return ("resource uses", new StepCoverage<int>(Resource.Uses));
        }
    }

    public static CompiledModifier Compile(int number, ModifierSpec spec, IReadOnlyList<CompiledAttack> attacks)
    {
        var kind = CompiledBuild.Match(V.Kinds.Set, spec.Kind)!;
        var defaultPolicy = kind == V.Kinds.PowerAttack ? V.PowerAttackPolicies.Auto : V.Policies.AnyHit;
        var policySet = kind == V.Kinds.PowerAttack ? V.PowerAttackPolicies.Set : V.Policies.Set;
        var whenSet = kind == V.Kinds.ConditionOnHit ? V.When.ConditionOnHitSet : V.When.ExtraDamageSet;
        var conditionSet = kind == V.Kinds.SaveEffect ? V.Conditions.SaveEffectSet : V.Conditions.OnHitSet;
        var actionCostSet = kind switch
        {
            V.Kinds.SaveEffect => V.ActionCosts.SaveEffectSet,
            V.Kinds.Heal => V.ActionCosts.HealSet,
            _ => V.ActionCosts.RiderSet,
        };
        var shape = CompiledBuild.Match(V.Shapes.Set, spec.Shape);

        return new CompiledModifier
        {
            Number = number,
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(spec.Name) ? null : spec.Name.Trim(),
            AttackFilter = spec.Attacks?.Select(n => attacks.First(a => string.Equals(a.Name, n.Trim(), StringComparison.OrdinalIgnoreCase)).Name)
                .Distinct(StringComparer.Ordinal).ToList(),
            FromLevel = spec.FromLevel,
            UntilLevel = spec.UntilLevel,
            Resource = spec.Resource is { } resource
                ? new CompiledResource(
                    LevelValue.ParseInt(resource.Uses, "resource uses", 1, DslLimits.MaxResourceUses)!,
                    CompiledBuild.Match(V.Rests.Set, resource.Per)!)
                : null,
            Concentration = spec.Concentration ?? false,
            Setup = CompiledBuild.Match(V.Setup.Set, spec.Setup),
            Amount = LevelValue.ParseAmount(spec.Amount, "amount"),
            Dice = kind == V.Kinds.ToHit
                ? LevelValue.ParseBonusDice(spec.Dice, "dice", ToHitDiceFlatHint)
                : LevelValue.ParseDamage(spec.Dice, "dice"),
            DamageType = CompiledBuild.Match(V.DamageTypes.Set, spec.Type),
            When = CompiledBuild.Match(whenSet, spec.When) ?? (kind is V.Kinds.ExtraDamage or V.Kinds.ConditionOnHit ? V.When.EveryHit : null),
            Policy = CompiledBuild.Match(policySet, spec.Policy) ?? defaultPolicy,
            UseValue = spec.UseValue ?? 0,
            CritDoubles = spec.CritDoubles ?? true,
            AttackActionOnly = spec.AttackActionOnly ?? false,
            ActionCost = CompiledBuild.Match(actionCostSet, spec.ActionCost) ?? (kind is V.Kinds.SaveEffect or V.Kinds.Heal ? V.ActionCosts.Action : null),
            Min = LevelValue.ParseInt(spec.Min, "min", 2, 20),
            Mode = CompiledBuild.Match(V.AdvantageModes.Set, spec.Mode) ?? V.AdvantageModes.Advantage,
            Rate = spec.Rate ?? 1,
            Remap = CompiledBuild.Match(V.Remaps.Set, spec.Remap),
            Attack = spec.Attack is null ? null : attacks.First(a => string.Equals(a.Name, spec.Attack.Trim(), StringComparison.OrdinalIgnoreCase)).Name,
            Count = LevelValue.ParseInt(spec.Count, "count", 1, DslLimits.MaxAttackCount) ?? (kind == V.Kinds.ExtraAttack ? LevelValue<int>.Of(1) : null),
            Action = CompiledBuild.Match(V.ExtraAttackActions.Set, spec.Action),
            Trigger = CompiledBuild.Match(V.Triggers.Set, spec.Trigger) ?? V.Triggers.Always,
            TriggerProbability = spec.TriggerProbability,
            Penalty = spec.Penalty ?? 5,
            Bonus = spec.Bonus ?? 10,
            Ability = CompiledBuild.Match(V.Abilities.Set, spec.Ability),
            Dc = spec.Dc,
            DcAbility = CompiledBuild.Match(V.Abilities.Set, spec.DcAbility),
            DcBonus = spec.DcBonus ?? 0,
            OnSuccess = CompiledBuild.Match(V.OnSuccess.Set, spec.OnSuccess) ?? V.OnSuccess.Half,
            Targets = spec.Targets,
            Shape = shape,
            Size = spec.Size,
            Magical = spec.Magical ?? kind == V.Kinds.SaveEffect,
            Condition = CompiledBuild.Match(conditionSet, spec.Condition),
            Cantrip = spec.Cantrip ?? false,
            Duration = CompiledBuild.Match(V.Durations.Set, spec.Duration),
            SelfOnly = spec.SelfOnly ?? false,
        };
    }
}

/// <summary>A step value seen only for coverage: whether it has a value at a level, and its first level.</summary>
internal interface IStepCoverage
{
    bool Scales { get; }

    int FirstLevel { get; }

    bool Covers(int level);
}

internal sealed class StepCoverage<T>(LevelValue<T> value) : IStepCoverage
{
    public bool Scales => value.Scales;

    public int FirstLevel => value.FirstLevel;

    public bool Covers(int level) => value.TryAt(level, out _);
}
