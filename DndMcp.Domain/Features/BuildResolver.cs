using DndMcp.Domain.Core;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// Turns a <see cref="BuildSpec"/> into <see cref="ResolvedBuild"/>s, one per level: the only way the DPR engine sees a
/// build.
///
/// <para>
/// <b>Validation happens once, for every level that will be evaluated</b>, before anything is resolved: stage 1
/// (<see cref="BuildSpecValidator"/>, level-independent), then stage 2 on the compiled build (step values covered at every
/// active level, then the per-level rules). The first stage that finds problems throws them as ONE
/// <see cref="DndInputException"/> (up to five listed); later stages would only report consequences of them. So a level
/// curve over 1–20 either fails up front or resolves all 20 levels; it never fails at level 14 after doing the work for
/// 13.
/// </para>
/// <para>
/// <b>Sugar is expanded here, edition-aware</b>, so the engine never sees a fighting style: <c>gwf</c> becomes the
/// edition's remap on two-handed or versatile melee weapon attacks (2014 reroll 1–2 once; 2024 1–2 count as 3),
/// <c>archery</c> a +2 to-hit part on ranged weapon attacks (not with to_hit.total), <c>dueling</c> a +2 damage part on
/// melee weapon attacks without Two-Handed (not while the build makes an offhand attack, since Dueling needs no other
/// weapon), <c>twf</c> the ability modifier on offhand attacks. A preset is expanded first (<see cref="BuildPresets"/>).
/// </para>
/// </summary>
public static class BuildResolver
{
    /// <summary>The build at one level. Validates for that level only.</summary>
    /// <param name="subject">What the build is, for the error's first words: "build", "baseline", "variant".</param>
    /// <exception cref="DndInputException">The spec is not valid at that level; every problem found (up to five) is listed.</exception>
    public static ResolvedBuild Resolve(BuildSpec spec, int level, RulingsSpec? rulings = null, string subject = "build") =>
        Resolve(spec, [level], rulings, subject)[0];

    /// <summary>
    /// The build at each of <paramref name="levels"/> (in the order given); with no levels, at the spec's own level.
    /// Validation covers all of them first.
    /// </summary>
    /// <exception cref="DndInputException">A level outside 1–20, or the spec is not valid at some level.</exception>
    public static IReadOnlyList<ResolvedBuild> Resolve(BuildSpec spec, IReadOnlyList<int>? levels, RulingsSpec? rulings = null, string subject = "build")
    {
        var (compiled, evaluated) = Compile(spec, levels, subject);
        var resolvedRulings = ResolvedRulings.From(rulings);
        return evaluated.Select(level => ResolveLevel(compiled, level, resolvedRulings)).ToList();
    }

    /// <summary>
    /// Validates the spec for <paramref name="levels"/> (default: its own level) without resolving it: the check
    /// <c>balance_compare</c> runs on the baseline before merging a feature into it.
    /// </summary>
    /// <exception cref="DndInputException">Every problem found, up to five, in one message.</exception>
    public static void Validate(BuildSpec spec, IReadOnlyList<int>? levels = null, string subject = "build") => Compile(spec, levels, subject);

    /// <summary>
    /// Whether the build changes with level beyond its proficiency bonus: a step value with two or more steps, a
    /// from_level/until_level, or cantrip scaling (a preset's included). The level-curve output warns when a build that
    /// does not is evaluated at many levels.
    /// </summary>
    /// <exception cref="DndInputException">The spec fails level-independent validation.</exception>
    public static bool ScalesWithLevel(BuildSpec spec) => CompileStructure(spec, "build").ScalesWithLevel;

    private static (CompiledBuild Build, IReadOnlyList<int> Levels) Compile(BuildSpec spec, IReadOnlyList<int>? levels, string subject)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var compiled = CompileStructure(spec, subject);
        var evaluated = levels is { Count: > 0 } ? levels : [compiled.Level];
        var outside = evaluated.Where(l => l is < DslLimits.MinLevel or > DslLimits.MaxLevel).Distinct().ToList();
        if (outside.Count > 0)
        {
            throw new DndInputException(
                $"levels: {string.Join(", ", outside.Select(DslText.Number))} {(outside.Count == 1 ? "is not a character level" : "are not character levels")}; " +
                $"levels are {DslLimits.MinLevel} to {DslLimits.MaxLevel}.");
        }

        var input = new BuildLevels(compiled, evaluated);
        DslProblems.ThrowIfAny(DslProblems.Messages(StepCoverageValidator.Instance.Validate(input)), subject);
        DslProblems.ThrowIfAny(DslProblems.Messages(BuildLevelValidator.Instance.Validate(input)), subject);
        return (compiled, evaluated);
    }

    private static CompiledBuild CompileStructure(BuildSpec spec, string subject)
    {
        DslProblems.ThrowIfAny(DslProblems.Messages(BuildSpecValidator.Instance.Validate(spec)), subject);
        return CompiledBuild.Compile(BuildPresets.Expand(spec));
    }

    internal static ResolvedBuild ResolveLevel(CompiledBuild build, int level, ResolvedRulings rulings)
    {
        var context = new LevelContext(build, level);
        var attacks = context.Attacks.Select(context.ResolveAttack).ToList();

        return new ResolvedBuild
        {
            Name = build.Name,
            Edition = build.Edition,
            Level = level,
            ProficiencyBonus = context.ProficiencyBonus,
            Abilities = context.Abilities,
            FightingStyle = build.FightingStyle,
            Attacks = attacks,
            Riders = context.OfKind(V.Kinds.ExtraDamage).Select(context.ResolveRider).ToList(),
            ExtraAttacks = context.OfKind(V.Kinds.ExtraAttack).Select(context.ResolveExtraAttack).ToList(),
            SaveEffects = context.OfKind(V.Kinds.SaveEffect).Select(context.ResolveSaveEffect).ToList(),
            ConditionsOnHit = context.OfKind(V.Kinds.ConditionOnHit).Select(context.ResolveConditionOnHit).ToList(),
            AdvantageSources = context.OfKind(V.Kinds.Advantage)
                .Select(m => new ResolvedAdvantageSource(m.Ref, m.Mode!, m.Rate, context.Names(m)))
                .ToList(),
            PowerAttacks = context.OfKind(V.Kinds.PowerAttack)
                .Select(m => new ResolvedPowerAttack(m.Ref, m.Penalty, m.Bonus, m.Policy!, context.Names(m)))
                .ToList(),
            DamageRerolls = context.OfKind(V.Kinds.RerollDamageTakeBest)
                .Select(m => new ResolvedDamageReroll(m.Ref, m.Policy!, m.UseValue, context.Names(m)))
                .ToList(),
            Remaps = context.Remaps(),
            SetupCosts = context.Modifiers.Where(m => m.Setup is not null).Select(m => new ResolvedSetupCost(m.Ref, m.Setup!)).ToList(),
            Defensive = context.Modifiers.Where(m => V.Kinds.Defensive.Contains(m.Kind))
                .Select(m => new ResolvedDefensive(m.Ref, m.Kind, m.Amount?.At(level).Resolve(context.ProficiencyBonus, context.Abilities), m.DamageType, context.Resource(m)))
                .ToList(),
            Rulings = rulings,
            ScalesWithLevel = build.ScalesWithLevel,
            Notes = context.Notes(attacks),
        };
    }

    /// <summary>Everything about one build at one level that more than one resolution step reads.</summary>
    private sealed class LevelContext
    {
        private const string GwfStyleLabel = "Great Weapon Fighting (fighting style)";
        private const int FightingStyleBonus = 2;

        private readonly CompiledBuild _build;
        private readonly int _level;
        private readonly Dictionary<int, IReadOnlyList<CompiledAttack>> _filters;

        public LevelContext(CompiledBuild build, int level)
        {
            _build = build;
            _level = level;
            ProficiencyBonus = build.ProficiencyBonusAt(level);
            Abilities = build.AbilitiesAt(level);
            Attacks = build.ActiveAttacks(level);
            Modifiers = build.ActiveModifiers(level);
            _filters = Modifiers.ToDictionary(m => m.Number, m => build.AttacksFor(m, level));
        }

        public int ProficiencyBonus { get; }

        public ResolvedAbilities Abilities { get; }

        public IReadOnlyList<CompiledAttack> Attacks { get; }

        public IReadOnlyList<CompiledModifier> Modifiers { get; }

        private bool HasOffhand => Attacks.Any(a => a.Offhand);

        private int CantripMultiplier => V.Cantrips.Multiplier(_level);

        public IEnumerable<CompiledModifier> OfKind(string kind) => Modifiers.Where(m => m.Kind == kind);

        public IReadOnlyList<string> Names(CompiledModifier modifier) => _filters[modifier.Number].Select(a => a.Name).ToList();

        public ResolvedResource? Resource(CompiledModifier modifier) =>
            modifier.Resource is { } resource ? new ResolvedResource(resource.Uses.At(_level), resource.Per) : null;

        private int Amount(CompiledModifier modifier) => modifier.Amount?.At(_level).Resolve(ProficiencyBonus, Abilities) ?? 0;

        private IEnumerable<CompiledModifier> Applying(string kind, CompiledAttack attack) =>
            OfKind(kind).Where(m => _filters[m.Number].Contains(attack));

        public ResolvedAttack ResolveAttack(CompiledAttack attack)
        {
            var modifier = Abilities.Modifier(attack.Ability);
            var multiplier = attack.Cantrip is null ? 1 : CantripMultiplier;
            var damage = attack.Damage.At(_level);

            var remaps = BuildLevelValidator.GreatWeaponFightingSources(_build, attack, _level);
            var remapModifier = Applying(V.Kinds.DamageDieRemap, attack).FirstOrDefault(m => V.Remaps.IsGreatWeaponFighting(m.Remap!));
            var (weaponRemap, remapSource) = remaps.Count == 0
                ? ((string?)null, (string?)null)
                : remapModifier is not null
                    ? (remapModifier.Remap, remapModifier.Label)
                    : (V.Remaps.GreatWeaponFighting(_build.Edition), GwfStyleLabel);

            return new ResolvedAttack
            {
                Number = attack.Number,
                Name = attack.Name,
                Count = attack.Count.At(_level) * (attack.Cantrip == V.Cantrips.Beams ? multiplier : 1),
                Action = attack.Action,
                Ability = attack.Ability,
                AbilityModifier = modifier,
                ToHitParts = ToHitParts(attack, modifier),
                ToHitDice = Applying(V.Kinds.ToHit, attack)
                    .Where(m => m.Dice is not null)
                    .Select(m => new NamedDice(m.Label, m.Dice!.At(_level)))
                    .ToList(),
                CritMin = Applying(V.Kinds.CritRange, attack).Select(m => m.Min!.At(_level)).Append(20).Min(),
                Lucky = Applying(V.Kinds.Lucky, attack).Any(),
                ElvenAccuracy = Applying(V.Kinds.ElvenAccuracy, attack).Any(),
                Damage = attack.Cantrip == V.Cantrips.Dice ? damage.ScaleDice(multiplier) : damage,
                DamageType = attack.DamageType,
                DamageParts = DamageParts(attack, modifier),
                Properties = attack.Properties,
                Offhand = attack.Offhand,
                Mastery = attack.Mastery,
                MasterySaveDc = 8 + ProficiencyBonus + modifier,
                WeaponRemap = weaponRemap,
                WeaponRemapSource = remapSource,
                ElementalAdeptTypes = Applying(V.Kinds.DamageDieRemap, attack)
                    .Where(m => m.Remap == V.Remaps.ElementalAdept)
                    .Select(m => m.DamageType!)
                    .Distinct()
                    .ToList(),
                IgnoresCover = Applying(V.Kinds.IgnoreCover, attack).Any(),
                Cantrip = attack.Cantrip,
                CantripMultiplier = multiplier,
            };
        }

        private List<NamedValue> ToHitParts(CompiledAttack attack, int abilityModifier)
        {
            var parts = new List<NamedValue>();
            if (attack.ToHitTotal is { } total)
            {
                parts.Add(new NamedValue("to_hit total", total));
            }
            else
            {
                if (attack.Ability != V.Abilities.None)
                {
                    parts.Add(new NamedValue(V.Abilities.Display(attack.Ability), abilityModifier));
                }

                if (attack.Proficient)
                {
                    parts.Add(new NamedValue("proficiency", ProficiencyBonus));
                }

                if (attack.ToHitBonus != 0)
                {
                    parts.Add(new NamedValue("bonus", attack.ToHitBonus));
                }

                if (TakesArchery(attack))
                {
                    parts.Add(new NamedValue(V.FightingStyles.Display(V.FightingStyles.Archery), FightingStyleBonus));
                }
            }

            parts.AddRange(Applying(V.Kinds.ToHit, attack)
                .Where(m => m.Amount is not null)
                .Select(m => new NamedValue(m.Label, Amount(m))));
            return parts;
        }

        private List<DamagePart> DamageParts(CompiledAttack attack, int abilityModifier)
        {
            var parts = new List<DamagePart>();
            if (attack.Ability != V.Abilities.None)
            {
                var label = V.Abilities.Display(attack.Ability);
                if (attack.Offhand && _build.FightingStyle == V.FightingStyles.Twf)
                {
                    parts.Add(new DamagePart($"{label} ({V.FightingStyles.Display(V.FightingStyles.Twf)})", abilityModifier, DamagePartSources.Ability));
                }
                else if (attack.AbilityToDamage ?? !attack.Offhand)
                {
                    parts.Add(new DamagePart(label, abilityModifier, DamagePartSources.Ability));
                }
                else if (attack.Offhand && abilityModifier < 0)
                {
                    // The offhand attack adds no ability modifier "unless that modifier is negative" (both editions).
                    parts.Add(new DamagePart(label, abilityModifier, DamagePartSources.Ability));
                }
            }

            if (TakesDueling(attack))
            {
                parts.Add(new DamagePart(V.FightingStyles.Display(V.FightingStyles.Dueling), FightingStyleBonus, DamagePartSources.Dueling));
            }

            parts.AddRange(Applying(V.Kinds.BonusDamage, attack)
                .Select(m => new DamagePart(m.Label, Amount(m), DamagePartSources.BonusDamage, m.AttackActionOnly)));
            return parts;
        }

        private bool TakesArchery(CompiledAttack attack) =>
            _build.FightingStyle == V.FightingStyles.Archery && attack.IsRanged && attack.IsWeapon && attack.ToHitTotal is null;

        private bool TakesDueling(CompiledAttack attack) =>
            _build.FightingStyle == V.FightingStyles.Dueling && attack.IsMelee && attack.IsWeapon &&
            !attack.Properties.Contains(V.Properties.TwoHanded) && !HasOffhand;

        public ResolvedRider ResolveRider(CompiledModifier modifier) => new()
        {
            Source = modifier.Ref,
            Damage = (modifier.Dice?.At(_level) ?? DamageFormula.Zero).Plus(DamageFormula.Constant(Amount(modifier))),
            DamageType = modifier.DamageType,
            When = modifier.When!,
            Policy = modifier.Policy!,
            UseValue = modifier.UseValue,
            CritDoubles = modifier.CritDoubles && modifier.When is V.When.EveryHit or V.When.FirstHitPerTurn,
            AttackActionOnly = modifier.AttackActionOnly,
            ActionCost = modifier.ActionCost,
            Resource = Resource(modifier),
            Concentration = modifier.Concentration,
            Attacks = Names(modifier),
        };

        public ResolvedExtraAttack ResolveExtraAttack(CompiledModifier modifier) => new()
        {
            Source = modifier.Ref,
            Attack = modifier.Attack!,
            Count = modifier.Count!.At(_level),
            Action = modifier.Action!,
            Trigger = modifier.Trigger!,
            TriggerProbability = modifier.TriggerProbability,
            Resource = Resource(modifier),
            Concentration = modifier.Concentration,
        };

        public ResolvedSaveEffect ResolveSaveEffect(CompiledModifier modifier)
        {
            var multiplier = modifier.Cantrip ? CantripMultiplier : 1;
            var dice = modifier.Dice?.At(_level).ScaleDice(multiplier);
            var damage = dice is null && modifier.Amount is null
                ? null
                : (dice ?? DamageFormula.Zero).Plus(DamageFormula.Constant(Amount(modifier)));
            var (dc, parts) = Dc(modifier, modifier.DcAbility!);
            var elementalAdept = OfKind(V.Kinds.DamageDieRemap)
                .Where(m => m.Remap == V.Remaps.ElementalAdept && m.DamageType == modifier.DamageType)
                .Select(m => m.DamageType!)
                .Distinct()
                .ToList();

            return new ResolvedSaveEffect
            {
                Source = modifier.Ref,
                Ability = modifier.Ability!,
                Dc = dc,
                DcParts = parts,
                Damage = damage,
                DamageType = modifier.DamageType,
                OnSuccess = modifier.OnSuccess!,
                Targets = modifier.Targets ?? (modifier.Shape is { } shape ? V.Shapes.Targets(shape, modifier.Size!.Value) : 1),
                Shape = modifier.Shape,
                Size = modifier.Size,
                Magical = modifier.Magical,
                Condition = modifier.Condition,
                ActionCost = modifier.ActionCost!,
                Resource = Resource(modifier),
                Concentration = modifier.Concentration,
                CantripMultiplier = multiplier,
                ElementalAdeptTypes = elementalAdept,
            };
        }

        public ResolvedConditionOnHit ResolveConditionOnHit(CompiledModifier modifier) => new()
        {
            Source = modifier.Ref,
            Condition = modifier.Condition!,
            Ability = modifier.Ability!,
            Dcs = _filters[modifier.Number]
                .Select(attack =>
                {
                    var (dc, parts) = Dc(modifier, modifier.DcAbility ?? attack.Ability);
                    return new AttackDc(attack.Name, dc, parts);
                })
                .ToList(),
            When = modifier.When!,
            Policy = modifier.Policy!,
            UseValue = modifier.UseValue,
            Magical = modifier.Magical,
            Resource = Resource(modifier),
            Concentration = modifier.Concentration,
        };

        // A given DC as is; otherwise 8 + proficiency bonus + the ability's modifier (+ dc_bonus).
        private (int Dc, IReadOnlyList<NamedValue> Parts) Dc(CompiledModifier modifier, string ability)
        {
            if (modifier.Dc is { } given)
            {
                return (given, [new NamedValue("given", given)]);
            }

            List<NamedValue> parts =
            [
                new("base", 8),
                new("proficiency", ProficiencyBonus),
                new(V.Abilities.Display(ability), Abilities.Modifier(ability)),
            ];
            if (modifier.DcBonus != 0)
            {
                parts.Add(new NamedValue("dc_bonus", modifier.DcBonus));
            }

            return (parts.Sum(p => p.Value), parts);
        }

        public IReadOnlyList<ResolvedRemap> Remaps()
        {
            var remaps = new List<ResolvedRemap>();
            if (_build.FightingStyle == V.FightingStyles.Gwf)
            {
                remaps.Add(new ResolvedRemap(
                    GwfStyleLabel, null, V.Remaps.GreatWeaponFighting(_build.Edition), null,
                    Attacks.Where(a => a.TakesGreatWeaponFighting).Select(a => a.Name).ToList()));
            }

            remaps.AddRange(OfKind(V.Kinds.DamageDieRemap).Select(m => new ResolvedRemap(m.Label, m.Ref, m.Remap!, m.DamageType, Names(m))));
            return remaps;
        }

        public IReadOnlyList<string> Notes(IReadOnlyList<ResolvedAttack> attacks)
        {
            var notes = new List<string>();
            var level = DslText.Number(_level);

            var mastered = attacks.Where(a => a.Mastery is not null).ToList();
            if (_build.Edition == V.Editions.E2014 && mastered.Count > 0)
            {
                notes.Add(
                    $"Weapon masteries are a 2024 rule; {string.Join(", ", mastered.Select(a => $"{a.Name}'s {a.Mastery}"))} " +
                    "is used as given on this 2014 build.");
            }

            foreach (var modifier in OfKind(V.Kinds.PowerAttack).Where(_ => _build.Edition == V.Editions.E2024))
            {
                notes.Add(
                    $"{modifier.Label}: 2024 has no -5/+10 power attack (the 2024 Great Weapon Master and Sharpshooter dropped it); " +
                    "it is used as given on this 2024 build.");
            }

            foreach (var modifier in OfKind(V.Kinds.DamageDieRemap).Where(m => V.Remaps.IsGreatWeaponFighting(m.Remap!) && m.Remap != V.Remaps.GreatWeaponFighting(_build.Edition)))
            {
                notes.Add($"{modifier.Label}: {modifier.Remap} is the other edition's Great Weapon Fighting; it is used as given on this {_build.Edition} build.");
            }

            StyleNotes(notes, level);

            foreach (var attack in Attacks.Where(a => a.PartsBesideTotal))
            {
                notes.Add($"{attack.Name}: to_hit total {DslText.Number(attack.ToHitTotal!.Value)} is the whole attack bonus; its proficient and bonus are ignored.");
            }

            foreach (var modifier in Modifiers.Where(m => ModifierFields.Takes(m.Kind, "attacks") && _filters[m.Number].Count == 0))
            {
                notes.Add($"{modifier.Label} ({modifier.Kind}) applies to no attack active at level {level}.");
            }

            foreach (var modifier in Modifiers.Where(m => V.Kinds.Defensive.Contains(m.Kind)))
            {
                notes.Add($"{modifier.Label} ({modifier.Kind}) is defensive: kept for the simulator, it does not change damage dealt.");
            }

            foreach (var modifier in OfKind(V.Kinds.SaveEffect).Where(m => m.Condition is not null && !V.Conditions.IsMechanical(m.Condition)))
            {
                notes.Add($"{modifier.Label}: {modifier.Condition} is shown as a label; nothing here models its effect.");
            }

            foreach (var modifier in OfKind(V.Kinds.DamageDieRemap).Where(m => m.Remap == V.Remaps.ElementalAdept))
            {
                notes.Add(
                    $"{modifier.Label}: only Elemental Adept's 1-counts-as-2 is modelled; for its \"ignore resistance\" part, leave " +
                    $"{modifier.DamageType} out of the target's resistances.");
            }

            return notes;
        }

        private void StyleNotes(List<string> notes, string level)
        {
            switch (_build.FightingStyle)
            {
                case V.FightingStyles.Gwf when !Attacks.Any(a => a.TakesGreatWeaponFighting):
                    notes.Add($"fighting_style gwf applies to no attack at level {level}: it needs a melee weapon attack with two-handed or versatile.");
                    break;
                case V.FightingStyles.Archery when !Attacks.Any(TakesArchery):
                    notes.Add(Attacks.Any(a => a.IsRanged && a.IsWeapon)
                        ? "fighting_style archery is not added to attacks with to_hit total (the total is the whole bonus)."
                        : $"fighting_style archery applies to no attack at level {level}: it needs a ranged weapon attack.");
                    break;
                case V.FightingStyles.Dueling when HasOffhand:
                    notes.Add("fighting_style dueling is not applied: Dueling needs no other weapon, and this build makes an offhand attack.");
                    break;
                case V.FightingStyles.Dueling when !Attacks.Any(TakesDueling):
                    notes.Add($"fighting_style dueling applies to no attack at level {level}: it needs a melee weapon attack without two-handed.");
                    break;
                case V.FightingStyles.Dueling:
                    notes.Add("Dueling's +2 assumes the weapon is held in one hand and no other weapon is wielded.");
                    break;
                case V.FightingStyles.Twf when !HasOffhand:
                    notes.Add($"fighting_style twf changes nothing at level {level}: no attack is offhand.");
                    break;
            }
        }
    }
}
