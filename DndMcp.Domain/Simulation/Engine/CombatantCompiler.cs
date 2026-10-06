using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Turns a stat block or a resolved build into a <see cref="CombatantTemplate"/>, once per run.
///
/// <para>
/// <b>Monsters are read as the normalizer wrote them</b>: kinds decide everything, and an action or trait the simulator
/// cannot run is simply not used (its warning already travels with the stat block into the result). Two things are
/// derived here because the stat block model has no member for them, and each is said where it is used: the troll's
/// "dies only if it starts its turn with 0 hit points" (read from the Regeneration trait's text), and whether a parry
/// works only against melee attacks (the Parry text says "melee attack"; the Shield spell does not).
/// </para>
/// <para>
/// <b>Builds keep their resolved records</b>: the turn logic reads <see cref="ResolvedBuild"/>'s riders, extra attacks,
/// save effects and so on directly, with the per-attack indexes and resource slots precomputed here.
/// </para>
/// </summary>
internal static class CombatantCompiler
{
    /// <param name="lair">The fight is in a lair (<see cref="SimulationSpec.Lair"/>): the in-lair legendary action and Legendary Resistance counts where the block has them.</param>
    public static CombatantTemplate FromStatBlock(StatBlock block, CombatantSpec spec, int id, int side, string label, bool rollHp, bool lair = false)
    {
        var all = new List<MonsterAction>();
        MonsterAction Compile(StatBlockAction action)
        {
            var compiled = CompileAction(action);
            all.Add(compiled);
            return compiled;
        }

        var actions = block.Actions.Select(Compile).ToList();
        var bonus = block.BonusActions.Select(Compile).ToList();
        var spells = block.Spells.Select(Compile).ToList();
        var reactions = block.Reactions.Select(Compile).ToList();
        var legendary = (block.Legendary?.Actions ?? []).Select(Compile).ToList();
        for (var i = 0; i < legendary.Count; i++)
        {
            legendary[i].LegendaryIndex = i;
        }

        var byName = new Dictionary<string, MonsterAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions.Concat(spells).Concat(bonus).Concat(reactions).Concat(legendary))
        {
            byName.TryAdd(action.Name, action);
        }

        (MonsterAction, int)[] Resolve(IEnumerable<ActionUse> uses) =>
            uses.Where(u => byName.ContainsKey(u.ActionName) && u.Count > 0).Select(u => (byName[u.ActionName], u.Count)).ToArray();

        // use_actions: legendary actions ("make one Tail attack"), and actions too (a 2014 swallow uses its Bite).
        foreach (var action in all.Where(a => a.IsUseActions))
        {
            action.Uses = Resolve(action.Source.Uses).Where(u => u.Item1 != action).ToArray();
        }

        // Immunity after a success: one number per action or rider that grants it, which a target records per source
        // creature once it succeeds or shakes the condition off (Frightful Presence, Horrifying Visage, Moan...).
        var immunities = 0;
        foreach (var action in all)
        {
            if (action.Source.ImmuneAfterSuccess)
            {
                action.ImmunityIndex = immunities++;
            }

            for (var r = 0; r < action.OnHit.Length; r++)
            {
                if (action.Source.OnHit[r].ImmuneAfterSuccess)
                {
                    action.OnHit[r].ImmunityIndex = immunities++;
                }
            }
        }

        var multiattacks = block.Multiattacks
            .Select(m => new MultiattackPlan
            {
                Label = m.Label,
                Steps = Resolve(m.Steps),
                Choose = m.Choose,
                Options = m.Options.Where(o => byName.ContainsKey(o.ActionName)).Select(o => byName[o.ActionName]).ToArray(),
            })
            .Where(m => m.Steps.Length > 0 || (m.Choose > 0 && m.Options.Length > 0))
            .ToArray();

        // Limited uses: recharge and per-day actions get a slot each; spell-slot pools one slot per pool.
        var limited = new List<MonsterAction>();
        var limitedNames = new List<string>();
        var sharedRecharge = new Dictionary<string, int>(StringComparer.Ordinal);
        var pools = new List<string>();
        var poolSizes = new List<int>();
        foreach (var action in all)
        {
            switch (action.Source.Usage.Kind)
            {
                case K.UsageKinds.Recharge when action.Source.Usage.Pool is { } shared:
                    if (!sharedRecharge.TryGetValue(shared, out var slot))
                    {
                        slot = limited.Count;
                        sharedRecharge[shared] = slot;
                        limited.Add(action);
                        var colon = shared.IndexOf(':');
                        limitedNames.Add(colon >= 0 ? shared[(colon + 1)..] : shared);
                    }

                    action.UsageSlot = slot;
                    break;
                case K.UsageKinds.Recharge:
                case K.UsageKinds.PerDay:
                    action.UsageSlot = limited.Count;
                    limited.Add(action);
                    limitedNames.Add(action.Name);
                    break;
                case K.UsageKinds.Pool when action.Source.Usage.Pool is { } pool:
                    var index = pools.IndexOf(pool);
                    if (index < 0)
                    {
                        index = pools.Count;
                        pools.Add(pool);
                        poolSizes.Add(PoolSize(block, pool));
                    }

                    action.PoolSlot = index;
                    break;
            }
        }

        var traits = block.Traits;
        StatBlockTrait? Trait(string kind) => traits.FirstOrDefault(t => t.Kind == kind);
        var regeneration = Trait(K.TraitKinds.Regeneration);
        var hasMelee = all.Any(a => a.IsAttack && a.Melee);
        var saves = V.Abilities.All.Select(a => spec.Saves?.Get(a) ?? (block.SaveBonuses.TryGetValue(a, out var b) ? b : block.Modifier(a))).ToArray();

        return new CombatantTemplate
        {
            Id = id,
            Side = side,
            Label = label,
            Edition = block.Edition,
            PcLike = spec.DeathSaves == true,
            AverageHp = spec.Hp ?? block.HitPoints,
            RolledHp = spec.Hp is null && rollHp && block.HitDice.HasDice ? block.HitDice : null,
            ArmorClass = spec.Ac ?? block.ArmorClass,
            Saves = saves,
            InitiativeBonus = spec.InitiativeBonus ?? block.InitiativeBonus,
            Front = Front(spec, hasMelee),
            Flies = block.Speeds.TryGetValue("fly", out var fly) && fly > 0,
            CanStand = !block.Speeds.TryGetValue("walk", out var walk) || walk > 0,
            Resist = Qualifier.Table(block.Resistances),
            Immune = Qualifier.Table(block.Immunities),
            Vulnerable = Qualifier.Table(block.Vulnerabilities),
            ConditionImmunities = block.ConditionImmunities.Select(Cond.Of).Where(c => c >= 0).Aggregate(0, (mask, c) => mask | (1 << c)),
            Size = block.Size,
            StrMod = block.Modifier(V.Abilities.Str),
            DexMod = block.Modifier(V.Abilities.Dex),
            MagicResistance = Trait(K.TraitKinds.MagicResistance) is not null,
            PackTactics = Trait(K.TraitKinds.PackTactics) is not null,
            Evasion = Trait(K.TraitKinds.Evasion) is not null,
            BloodFrenzy = Trait(K.TraitKinds.BloodFrenzy) is not null,
            AdvantageWhileBloodied = Trait(K.TraitKinds.AdvantageWhileBloodied) is not null,
            Reckless = Trait(K.TraitKinds.Reckless) is not null,
            RegenerationAmount = regeneration?.Amount ?? 0,
            RegenerationStops = (regeneration?.DamageTypes ?? []).Select(DamageTypes.Of).Where(t => t != DamageTypes.Typeless).Aggregate(0, (mask, t) => mask | (1 << t)),
            RegeneratesFromZero = regeneration is not null &&
                                  regeneration.Text.Contains("starts its turn with 0 hit points", StringComparison.OrdinalIgnoreCase),
            UndeadFortitude = Trait(K.TraitKinds.UndeadFortitude) is not null,
            RelentlessAmount = Trait(K.TraitKinds.Relentless) is { } relentless ? relentless.Amount ?? int.MaxValue : 0,
            SneakAttack = Trait(K.TraitKinds.SneakAttack)?.Dice is { } sneak ? [new RollPart(sneak.Dice, sneak.Flat, DamageTypes.Typeless)] : [],
            MartialAdvantage = Trait(K.TraitKinds.MartialAdvantage)?.Dice is { } martial ? [new RollPart(martial.Dice, martial.Flat, DamageTypes.Typeless)] : [],
            Retaliation = Effect(Trait(K.TraitKinds.RetaliationDamage)),
            Aura = Effect(Trait(K.TraitKinds.AuraDamage)),
            DeathBurst = Effect(Trait(K.TraitKinds.DeathBurst)),
            LegendaryResistance = lair ? block.LegendaryResistanceInLair ?? block.LegendaryResistance : block.LegendaryResistance,
            LegendaryUses = block.Legendary is { } legendaryActions ? (lair ? legendaryActions.UsesInLair ?? legendaryActions.Uses : legendaryActions.Uses) : 0,
            LegendaryActions = legendary.ToArray(),
            Actions = [.. actions.Where(a => a.Usable), .. spells.Where(s => s.Source.Slot == K.ActionSlots.Action && s.Usable)],
            BonusActions = [.. bonus.Where(a => a.Usable), .. spells.Where(s => s.Source.Slot == K.ActionSlots.BonusAction && s.Usable)],
            Parries = [.. reactions.Where(r => r.Kind == K.ActionKinds.Parry && r.AcBonus > 0), .. spells.Where(s => s.Kind == K.ActionKinds.Parry && s.AcBonus > 0)],
            Multiattacks = multiattacks,
            Limited = limited.ToArray(),
            LimitedNames = limitedNames.ToArray(),
            PoolNames = pools.ToArray(),
            PoolSizes = poolSizes.ToArray(),
            HasHeal = all.Any(a => a.IsHeal),
            StatBlock = block,
        };
    }

    /// <summary>
    /// A placeholder with no simulation route of its own (<see cref="CombatantStart.Placeholder"/> on an entry that gives
    /// only a name): it is dead from the start and only holds its place in the order, so nothing about it but its label and
    /// side is ever read.
    /// </summary>
    public static CombatantTemplate Placeholder(int id, int side, string label) => new()
    {
        Id = id,
        Side = side,
        Label = label,
        Edition = V.Editions.Default,
        PcLike = false,
        AverageHp = 1,
        ArmorClass = 10,
        Saves = new int[V.Abilities.All.Count],
        InitiativeBonus = 0,
        Front = false,
        Resist = new int[DamageTypes.Count],
        Immune = new int[DamageTypes.Count],
        Vulnerable = new int[DamageTypes.Count],
        Inert = true,
    };

    private static bool Front(CombatantSpec spec, bool hasMelee) =>
        spec.Position is { } position && SimulationValues.Positions.Set.TryMatch(position, out var canonical)
            ? canonical == SimulationValues.Positions.Front
            : hasMelee;

    private static int PoolSize(StatBlock block, string pool)
    {
        var colon = pool.IndexOf(':');
        return colon >= 0 && int.TryParse(pool[(colon + 1)..], out var level) && block.SpellSlots.TryGetValue(level, out var slots) ? slots : 0;
    }

    private static TraitEffect? Effect(StatBlockTrait? trait) =>
        trait is null || (trait.Damage.Count == 0 && trait.Condition is null)
            ? null
            : new TraitEffect
            {
                Name = trait.Name,
                Damage = RollPart.From(trait.Damage),
                Save = trait.Save,
                Condition = ConditionTemplate.From(trait.Condition, trait.Save, false),
                AreaCount = trait.Area is { } area ? AreaCount(area) : 0,
                StartOfTargetTurn = trait.Kind == K.TraitKinds.AuraDamage && trait.Text.Contains("starts its turn", StringComparison.OrdinalIgnoreCase),
            };

    /// <summary>
    /// The DMG's count of creatures in an area (<see cref="DprTables.AreaTargets"/>: the measure ÷ the shape's divisor,
    /// rounded up, at least 1); a 2024 Emanation counts as a sphere of its size.
    /// </summary>
    public static int AreaCount(AreaSpec area)
    {
        var shape = area.Shape == K.Shapes.Emanation ? K.Shapes.Sphere : area.Shape;
        var rule = DprTables.AreaTargets.FirstOrDefault(r => r.Shape == shape);
        return rule is null || area.Size < 1 ? 1 : Math.Max(1, (area.Size + rule.Divisor - 1) / rule.Divisor);
    }

    private static MonsterAction CompileAction(StatBlockAction action)
    {
        var damage = RollPart.From(action.Damage);
        var onHit = action.OnHit.Select(e => new MonsterOnHit
        {
            Kind = e.Kind,
            Damage = RollPart.From(e.Damage),
            Save = e.Save,
            Condition = ConditionTemplate.From(e.Condition, e.Save, action.Magical || action.IsSpell),
            ExtraConditions = ConditionTemplate.FromAll(null, e.ExtraConditions, e.Save, action.Magical || action.IsSpell),
            MaxSize = e.MaxSize,
            KillAtOrBelowHp = e.KillAtOrBelowHp,
        }).ToArray();
        var riderMean = onHit.Where(e => e.Kind == K.EffectKinds.Damage).SelectMany(e => e.Damage).Sum(d => d.Mean);
        var riderCrit = onHit.Where(e => e.Kind == K.EffectKinds.Damage).SelectMany(e => e.Damage).Sum(d => d.DiceMean);

        return new MonsterAction
        {
            Source = action,
            Melee = action.Range != K.AttackRanges.Ranged,
            AlsoRanged = action.AlsoRanged,
            AttackBonus = action.AttackBonus ?? 0,
            Targets = Math.Max(1, action.Targets),
            Damage = damage,
            OnHit = onHit,
            Save = action.Save,
            AreaCount = action.Area is { } area ? AreaCount(area) : 0,
            Condition = ConditionTemplate.From(action.Condition, action.Save, action.Magical || action.IsSpell),
            ExtraConditions = ConditionTemplate.FromAll(null, action.ExtraConditions, action.Save, action.Magical || action.IsSpell),
            AttackRolls = Math.Max(1, action.AttackRolls),
            SelfOnly = action.SelfOnly,
            Healing = action.Healing,
            AcBonus = action.AcBonus ?? 0,
            ParryMeleeOnly = action.Text.Contains("melee attack", StringComparison.OrdinalIgnoreCase),
            Magical = action.Magical || action.IsSpell,
            MeanDamage = damage.Sum(d => d.Mean) + riderMean,
            MeanCritExtra = damage.Sum(d => d.DiceMean) + riderCrit,
        };
    }

    /// <summary>A build at its level as a combatant: HP, AC and saves from the spec, the rest from the resolved build.</summary>
    public static CombatantTemplate FromBuild(ResolvedBuild build, CombatantSpec spec, int id, int side, string label, bool pcLike)
    {
        var pc = CompileBuild(build);
        foreach (var attack in pc.Attacks)
        {
            attack.OddsKey = (id * 16) + attack.Index + 1;
        }

        var proficient = (spec.SaveProficiencies ?? [])
            .Select(s => V.Abilities.Set.TryMatch(s, out var a) ? a : null)
            .Where(a => a is not null)
            .ToHashSet();
        var saves = V.Abilities.All
            .Select(a => spec.Saves?.Get(a) ?? (build.Abilities.Modifier(a) + (proficient.Contains(a) ? build.ProficiencyBonus : 0)))
            .ToArray();
        var resist = new int[DamageTypes.Count];
        foreach (var defensive in build.Defensive.Where(d => d.Kind == V.Kinds.Resistance && d.DamageType is not null))
        {
            var type = DamageTypes.Of(defensive.DamageType);
            if (type != DamageTypes.Typeless)
            {
                resist[type] = Qualifier.Always;
            }
        }

        var acBonus = build.Defensive.Where(d => d.Kind == V.Kinds.Ac).Sum(d => d.Amount ?? 0);
        var tempHp = build.Defensive.Where(d => d.Kind == V.Kinds.TempHp).Select(d => d.Amount ?? 0).DefaultIfEmpty(0).Max();

        return new CombatantTemplate
        {
            Id = id,
            Side = side,
            Label = label,
            Edition = build.Edition,
            PcLike = pcLike,
            AverageHp = spec.Hp ?? 1,
            ArmorClass = (spec.Ac ?? 10) + acBonus,
            Saves = saves,
            InitiativeBonus = spec.InitiativeBonus ?? build.Abilities.Modifier(V.Abilities.Dex),
            Front = Front(spec, build.Attacks.Any(a => a.IsMelee)),
            Resist = resist,
            Immune = new int[DamageTypes.Count],
            Vulnerable = new int[DamageTypes.Count],
            StrMod = build.Abilities.Modifier(V.Abilities.Str),
            DexMod = build.Abilities.Modifier(V.Abilities.Dex),
            Pc = pc,
            TempHpAtStart = Math.Max(0, tempHp),
            HasHeal = build.Heals.Count > 0,
        };
    }

    public static PcBuild CompileBuild(ResolvedBuild build)
    {
        var resources = new List<PcResource>();
        int Slot(ModifierRef source, ResolvedResource? resource)
        {
            if (resource is null)
            {
                return -1;
            }

            resources.Add(new PcResource(source.Label, resource.Uses));
            return resources.Count - 1;
        }

        var riders = build.Riders.ToArray();
        var extras = build.ExtraAttacks.ToArray();
        var saves = build.SaveEffects.ToArray();
        var conditions = build.ConditionsOnHit.ToArray();
        var heals = build.Heals.ToArray();
        var riderSlot = riders.Select(r => Slot(r.Source, r.Resource)).ToArray();
        var extraSlot = extras.Select(e => Slot(e.Source, e.Resource)).ToArray();
        var saveSlot = saves.Select(s => Slot(s.Source, s.Resource)).ToArray();
        var conditionSlot = conditions.Select(c => Slot(c.Source, c.Resource)).ToArray();
        var healSlot = heals.Select(h => Slot(h.Source, h.Resource)).ToArray();

        var numbers = riders.Select(r => r.Source.Number)
            .Concat(extras.Select(e => e.Source.Number))
            .Concat(saves.Select(s => s.Source.Number))
            .Concat(conditions.Select(c => c.Source.Number))
            .Concat(build.AdvantageSources.Select(a => a.Source.Number))
            .Concat(build.PowerAttacks.Select(p => p.Source.Number))
            .Concat(build.DamageRerolls.Select(d => d.Source.Number))
            .Concat(build.SetupCosts.Select(s => s.Source.Number))
            .Concat(heals.Select(h => h.Source.Number))
            .Append(0)
            .Max();
        var gated = new bool[numbers + 1];
        foreach (var setup in build.SetupCosts)
        {
            gated[setup.Source.Number] = true;
        }

        var concentration = riders.Where(r => r.Concentration).Select(r => r.Source)
            .Concat(extras.Where(e => e.Concentration).Select(e => e.Source))
            .Concat(saves.Where(s => s.Concentration).Select(s => s.Source))
            .Concat(conditions.Where(c => c.Concentration).Select(c => c.Source))
            .FirstOrDefault();

        var attacks = build.Attacks.Select((a, i) => CompileAttack(build, a, i, riders, conditions)).ToArray();
        int IndexOf(string name) => Array.FindIndex(attacks, a => a.A.Name == name);

        return new PcBuild
        {
            Build = build,
            Attacks = attacks,
            Riders = riders,
            RiderSlot = riderSlot,
            Extras = extras,
            ExtraSlot = extraSlot,
            ExtraAttack = extras.Select(e => IndexOf(e.Attack)).ToArray(),
            SaveEffects = saves,
            SaveSlot = saveSlot,
            SaveCondition = saves.Select(s => s.Condition is { } c && Cond.IsMechanical(Cond.Of(c))
                ? ConditionTemplate.FromDsl(c, s.Duration, onHit: false, build.Edition, s.Ability, s.Dc, s.Magical)
                : null).ToArray(),
            SaveDamage = saves.Select(s => s.Damage is { } d ? new RollPart(d.Dice, d.Flat, DamageTypes.Of(s.DamageType)) : null).ToArray(),
            ConditionsOnHit = conditions,
            OnHitCondition = conditions.Select(c => ConditionTemplate.FromDsl(c.Condition, c.Duration, onHit: true, build.Edition, c.Ability, c.Dcs.Count > 0 ? c.Dcs[0].Dc : 10, c.Magical)).ToArray(),
            RiderDiceMean = riders.Select(r => DiceMean(r.Damage.Dice)).ToArray(),
            ConditionSlot = conditionSlot,
            Advantage = build.AdvantageSources.ToArray(),
            PowerAttacks = build.PowerAttacks.ToArray(),
            Rerolls = build.DamageRerolls.ToArray(),
            Heals = heals,
            HealSlot = healSlot,
            Resources = resources.ToArray(),
            Gated = gated,
            Setups = build.SetupCosts.ToArray(),
            ConcentrationNumber = concentration?.Number ?? 0,
            ConcentrationLabel = concentration?.Label,
            ActionQueue = attacks.Where(a => a.A.Action == V.AttackActions.Action).SelectMany(a => Enumerable.Repeat(a.Index, a.A.Count)).ToArray(),
            BonusQueue = attacks.Where(a => a.A.Action == V.AttackActions.BonusAction).SelectMany(a => Enumerable.Repeat(a.Index, a.A.Count)).ToArray(),
            SurgeExtras = Indexes(extras, e => e.Action == V.ExtraAttackActions.Action),
            BonusExtras = Indexes(extras, e => e.Action == V.ExtraAttackActions.BonusAction),
            ReactionExtras = Indexes(extras, e => e.Action == V.ExtraAttackActions.Reaction),
            ActionSaves = Indexes(saves, s => s.ActionCost == V.ActionCosts.Action),
            BonusSaves = Indexes(saves, s => s.ActionCost == V.ActionCosts.BonusAction),
            FreeSaves = Indexes(saves, s => s.ActionCost == V.ActionCosts.None),
        };
    }

    private static int[] Indexes<T>(IReadOnlyList<T> items, Func<T, bool> predicate) =>
        Enumerable.Range(0, items.Count).Where(i => predicate(items[i])).ToArray();

    private static PcAttack CompileAttack(ResolvedBuild build, ResolvedAttack attack, int index, ResolvedRider[] riders, ResolvedConditionOnHit[] conditions)
    {
        var savage = attack.Damage.HasDice && build.DamageRerolls.Any(r => r.AppliesTo(attack));
        var elementalAdept = new bool[DamageTypes.Count];
        foreach (var type in attack.ElementalAdeptTypes)
        {
            var t = DamageTypes.Of(type);
            if (t != DamageTypes.Typeless)
            {
                elementalAdept[t] = true;
            }
        }

        return new PcAttack
        {
            A = attack,
            Index = index,
            Dice = attack.Damage.Dice.ToArray(),
            Type = DamageTypes.Of(attack.DamageType),
            Magical = attack.IsSpell || attack.HasProperty(V.Properties.Magical),
            Silvered = attack.HasProperty(V.Properties.Silvered),
            Adamantine = attack.HasProperty(V.Properties.Adamantine),
            ToHitDice = attack.ToHitDice.SelectMany(d => d.Dice.Dice).ToArray(),
            ElementalAdept = elementalAdept,
            Riders = Indexes(riders, r => r.AppliesTo(attack)),
            Advantage = Indexes(build.AdvantageSources, a => a.AppliesTo(attack)),
            PowerAttacks = Indexes(build.PowerAttacks, p => p.AppliesTo(attack)),
            Rerolls = Indexes(build.DamageRerolls, r => r.AppliesTo(attack)),
            Conditions = Indexes(conditions, c => c.AppliesTo(attack)),
            DiceMean = DiceMean(attack.Damage.Dice, attack.WeaponRemap),
            Flat = Enum.GetValues<LineKind>().Select(kind => FlatDamage(attack, kind, build.Rulings)).ToArray(),
            ToHitPmf = attack.ToHitDice.Count == 0 ? null : DamageDice.Sum(attack.ToHitDice.SelectMany(d => d.Dice.Dice), WorkMeter.Unlimited),
            SavageGain = savage ? SavageGain(attack, 1) : 0,
            SavageGainCritAll = savage ? SavageGain(attack, 2) : 0,
        };
    }

    /// <summary>
    /// The flat damage of an attack made one way (the closed form's rule): attack_action_only parts only as part of the
    /// Attack action — a weapon attack of the Action or of Action Surge (a spell attack uses the casting action); Cleave
    /// under its ruling; Hew under hew_gets_pb — and Cleave drops a positive ability modifier ("unless that modifier is
    /// negative").
    /// </summary>
    private static int FlatDamage(ResolvedAttack attack, LineKind kind, ResolvedRulings rulings)
    {
        var attackAction = (kind is LineKind.Action or LineKind.Surge && attack.IsWeapon) ||
                           (kind == LineKind.Cleave && rulings.CleavePartOfAttackAction) ||
                           (kind == LineKind.Hew && rulings.HewGetsPb);
        var total = attack.Damage.Flat;
        foreach (var part in attack.DamageParts)
        {
            if (part.AttackActionOnly && !attackAction)
            {
                continue;
            }

            total += kind == LineKind.Cleave && part.Source == DamagePartSources.Ability && part.Value > 0 ? 0 : part.Value;
        }

        return total;
    }

    private static double SavageGain(ResolvedAttack attack, int times)
    {
        var meter = WorkMeter.Unlimited;
        var elementalAdept = attack.DamageType is { } type && attack.ElementalAdeptTypes.Contains(type);
        var roll = DamageDice.Sum(attack.Damage.Dice, meter, attack.WeaponRemap, elementalAdept, times);
        return DamageDice.Mean(DamageDice.MaxOfTwo(roll)) - DamageDice.Mean(roll);
    }

    /// <summary>E[dice] with a Great Weapon Fighting remap, from the closed form's own per-die distribution (policies only).</summary>
    public static double DiceMean(IEnumerable<DiceTerm> dice, string? remap = null) =>
        dice.Sum(d => (d.Negative ? -1 : 1) * d.Count * DamageDice.ExpectedValue(d.Sides, remap));
}
