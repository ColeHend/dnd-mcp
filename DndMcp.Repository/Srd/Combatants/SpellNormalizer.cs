using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd.Index;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// One spell record (plus its overlay entry) → a <see cref="SpellProfile"/>: what the simulator does when a monster
/// casts it, or why it does nothing.
///
/// <para>
/// <b>Order of authority.</b> The overlay (facts from the SRD text) wins over the catalogue below, which wins over what
/// a 2014 record's structure suggests. The catalogue has to beat the structure: 2014 Dimension Door carries 4d6 force
/// "damage" (for arriving in an occupied space) and Sleep a 5d8 "damage" that is hit points of creatures put to sleep;
/// read as structure, a lich would teleport for damage and an archmage would Sleep a party for 22 force.
/// </para>
/// <para>
/// <b>The catalogue is a judgment, and says so.</b> "No combat effect" means no effect the simulator's gridless,
/// lightless, one-fight model could show (Detect Magic, Misty Step, Mage Armor, which the stat block's AC already
/// includes). "Not modelled" means the spell changes a fight but is not cast (Counterspell, Mirror Image, Banishment,
/// Greater Invisibility); each caster gets one warning naming them, so a result never hides that its lich can
/// Counterspell. <c>SpellCatalogueTests</c> pins the classification of every spell any monster casts, in both editions.
/// </para>
/// </summary>
public static class SpellNormalizer
{
    /// <summary>Spells with no effect in the simulator's model of a fight, by index, with why.</summary>
    public static IReadOnlyDictionary<string, string> NoCombatEffect { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["animal-friendship"] = "charms a Beast outside combat",
        ["animal-messenger"] = "a message, not a fight",
        ["augury"] = "divination",
        ["barkskin"] = "its AC is the stat block's second AC entry, which the simulator does not use",
        ["clairvoyance"] = "divination",
        ["commune"] = "divination",
        ["control-weather"] = "takes 10 minutes to cast",
        ["create-food-and-water"] = "not a fight",
        ["creation"] = "not a fight",
        ["dancing-lights"] = "light (the simulator has no light model)",
        ["darkness"] = "darkness (the simulator has no light or vision model)",
        ["detect-evil-and-good"] = "divination",
        ["detect-magic"] = "divination",
        ["detect-thoughts"] = "divination",
        ["dimension-door"] = "teleportation (no grid)",
        ["disguise-self"] = "an illusion outside combat",
        ["divination"] = "divination",
        ["dream"] = "cast over a minute on a sleeping creature",
        ["druidcraft"] = "a minor trick",
        ["elementalism"] = "a minor trick",
        ["etherealness"] = "leaves the fight's plane",
        ["feather-fall"] = "falling",
        ["find-familiar"] = "an hour-long ritual",
        ["fog-cloud"] = "obscurement (the simulator has no vision model)",
        ["gaseous-form"] = "movement (no grid)",
        ["geas"] = "a minute-long command",
        ["goodberry"] = "healing outside combat",
        ["heroes-feast"] = "takes 10 minutes to cast",
        ["identify"] = "divination",
        ["legend-lore"] = "divination",
        ["light"] = "light (the simulator has no light model)",
        ["locate-object"] = "divination",
        ["longstrider"] = "speed (no grid)",
        ["mage-armor"] = "its AC is already the stat block's",
        ["mage-hand"] = "a minor trick",
        ["major-image"] = "an illusion",
        ["mending"] = "a minor trick",
        ["minor-illusion"] = "an illusion",
        ["misty-step"] = "teleportation (no grid)",
        ["modify-memory"] = "not a fight",
        ["nondetection"] = "divination",
        ["pass-without-trace"] = "stealth",
        ["prestidigitation"] = "a minor trick",
        ["project-image"] = "an illusion",
        ["raise-dead"] = "takes an hour to cast",
        ["remove-curse"] = "not a fight",
        ["resurrection"] = "takes an hour to cast",
        ["scrying"] = "divination",
        ["sending"] = "a message",
        ["shillelagh"] = "the stat block's attack already includes it",
        ["spare-the-dying"] = "monsters die at 0 hit points",
        ["speak-with-animals"] = "communication",
        ["speak-with-dead"] = "communication",
        ["teleport"] = "teleportation (no grid)",
        ["thaumaturgy"] = "a minor trick",
        ["tongues"] = "communication",
        ["unseen-servant"] = "a minor servant",
        ["water-breathing"] = "breathing",
        ["wind-walk"] = "travel",
        ["word-of-recall"] = "teleportation",
        ["zone-of-truth"] = "not a fight",
    };

    /// <summary>Spells that change a fight but that the simulator does not cast, by index, with what they would do.</summary>
    public static IReadOnlyDictionary<string, string> NotModelled { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["animate-dead"] = "summons",
        ["banishment"] = "removes a creature",
        ["bestow-curse"] = "a curse",
        ["bless"] = "bonus dice for allies",
        ["blur"] = "Disadvantage on attacks against the caster",
        ["calm-emotions"] = "suppresses charm and fear",
        ["charm-monster"] = "charms",
        ["charm-person"] = "charms",
        ["command"] = "a one-word command",
        ["confusion"] = "random behaviour",
        ["conjure-elemental"] = "summons",
        ["contagion"] = "a disease",
        ["control-water"] = "moves water",
        ["counterspell"] = "stops a spell",
        ["create-undead"] = "creates undead",
        ["dispel-evil-and-good"] = "wards and banishes",
        ["dispel-magic"] = "ends spells",
        ["dominate-monster"] = "control",
        ["dominate-person"] = "control",
        ["enlarge-reduce"] = "size and damage",
        ["faerie-fire"] = "Advantage against outlined creatures",
        ["fear"] = "frightens and makes creatures flee",
        ["fire-shield"] = "retaliation damage and resistance",
        ["fly"] = "flight",
        ["freedom-of-movement"] = "immunity to restraint",
        ["globe-of-invulnerability"] = "blocks spells",
        ["greater-invisibility"] = "invisibility while attacking",
        ["greater-restoration"] = "ends conditions",
        ["guardian-of-faith"] = "a guardian that damages creatures entering its area",
        ["heat-metal"] = "damage to a creature in metal",
        ["hypnotic-pattern"] = "charms and incapacitates",
        ["invisibility"] = "invisibility until the caster attacks",
        ["lesser-restoration"] = "ends a condition",
        ["levitate"] = "lifts a creature out of reach",
        ["mind-blank"] = "mental protection",
        ["mirror-image"] = "duplicates that absorb attacks",
        ["plane-shift"] = "banishes a creature to another plane",
        ["power-word-kill"] = "kills a creature with 100 hit points or fewer (2014; the 2024 overlay casts its 12d12 alternative)",
        ["power-word-stun"] = "stuns a creature with 150 hit points or fewer",
        ["protection-from-poison"] = "poison resistance",
        ["ray-of-enfeeblement"] = "halves Strength-based damage",
        ["sanctuary"] = "wards a creature from attacks",
        ["shapechange"] = "transformation",
        ["shield-of-faith"] = "+2 AC for an ally",
        ["silence"] = "blocks verbal spells",
        ["sleep"] = "puts creatures to sleep",
        ["slow"] = "slows creatures",
        ["stoneskin"] = "resistance to weapon damage",
        ["suggestion"] = "a compelled course of action",
        ["telekinesis"] = "moves creatures",
        ["time-stop"] = "extra turns",
        ["true-seeing"] = "sees through invisibility and illusions",
        ["wall-of-force"] = "a barrier",
    };

    private static readonly Dictionary<string, string> ShapeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sphere"] = StatBlockValues.Shapes.Sphere,
        ["cone"] = StatBlockValues.Shapes.Cone,
        ["cube"] = StatBlockValues.Shapes.Cube,
        ["cylinder"] = StatBlockValues.Shapes.Cylinder,
        ["line"] = StatBlockValues.Shapes.Line,
    };

    /// <summary>
    /// A spell record → its profile. Never throws for a vendored spell: a record whose structure cannot be read is a
    /// <see cref="SpellProfileKinds.NotModelled"/> profile whose reason says so.
    /// </summary>
    /// <exception cref="ArgumentException">The document is not a spell.</exception>
    public static SpellProfile Normalize(SrdDocument spell, SpellOverlay overlay)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(overlay);
        if (spell.Kind != SrdKinds.Spell)
        {
            throw new ArgumentException($"`{spell.Ref}` is a {spell.Kind}, not a spell.", nameof(spell));
        }

        var entry = overlay.For(spell.Edition, spell.Slug);
        var (level, concentration) = LevelAndConcentration(spell);
        var basic = new SpellProfile
        {
            Ref = spell.Ref.ToString(),
            Name = spell.Name,
            Kind = SpellProfileKinds.NotModelled,
            Level = level,
            Concentration = concentration,
        };

        if (entry?.Kind is null)
        {
            if (NoCombatEffect.TryGetValue(spell.Slug, out var why))
            {
                return basic with { Kind = SpellProfileKinds.NoCombatEffect, Reason = why };
            }

            if (NotModelled.TryGetValue(spell.Slug, out var what))
            {
                return basic with { Kind = SpellProfileKinds.NotModelled, Reason = what };
            }
        }

        var derived = spell.Edition == SrdEdition.Edition2014 ? Derive2014(spell, basic) : basic;
        return entry is null ? Unclassified(derived) : Unclassified(Merge(derived, entry));
    }

    // A spell no source classifies stays not modelled, with a reason that says the catalogue lacks it.
    private static SpellProfile Unclassified(SpellProfile profile) =>
        profile.Kind == SpellProfileKinds.NotModelled && profile.Reason is null
            ? profile with { Reason = "not classified: its record has no attack, save or healing the normalizer can read" }
            : profile;

    /// <summary>
    /// A profile cast by one monster → the action the simulator runs. <paramref name="castLevel"/> is the level it is
    /// cast at (2014: the spell's own; 2024: the data's cast level); cantrips scale by <paramref name="cantripLevel"/>.
    /// </summary>
    internal static StatBlockAction Cast(
        SpellProfile profile,
        string name,
        int castLevel,
        int cantripLevel,
        int? dc,
        int? attackBonus,
        int abilityModifier,
        string slot,
        UsageSpec usage)
    {
        var notes = new List<string>(profile.Notes);
        var above = Math.Max(0, castLevel - profile.Level);
        var tier = cantripLevel >= 17 ? 4 : cantripLevel >= 11 ? 3 : cantripLevel >= 5 ? 2 : 1;
        var targets = profile.Targets + profile.UpcastTargets * above;
        IReadOnlyList<DamageRoll> damage;
        if (profile.Level == 0)
        {
            if (profile.DamageByCharacterLevel.Count > 0)
            {
                var key = profile.DamageByCharacterLevel.Keys.Where(k => k <= cantripLevel).DefaultIfEmpty(profile.DamageByCharacterLevel.Keys.Min()).Max();
                damage = profile.DamageByCharacterLevel[key];
            }
            else
            {
                damage = profile.CantripScaling == "dice" ? profile.Damage.Select(d => d with { Dice = d.Dice.ScaleDice(tier) }).ToList() : profile.Damage;
            }

            if (profile.CantripScaling == "beams")
            {
                targets = tier;
            }

            if (tier > 1)
            {
                notes.Add(string.Create(CultureInfo.InvariantCulture, $"Cantrip at caster level {cantripLevel}."));
            }
        }
        else if (profile.DamageBySlot.TryGetValue(castLevel, out var byTable))
        {
            damage = byTable;
        }
        else
        {
            damage = profile.Damage.Concat(Enumerable.Range(0, above).SelectMany(_ => profile.UpcastDamage))
                .GroupBy(d => d.DamageType)
                .Select(g => new DamageRoll(g.Select(d => d.Dice).Aggregate((a, b) => a.Plus(b)), g.Key))
                .ToList();
        }

        if (profile.DamageAddsModifier && damage.Count > 0)
        {
            damage = [damage[0] with { Dice = damage[0].Dice.Plus(DamageFormula.Constant(abilityModifier)) }, .. damage.Skip(1)];
        }

        if (castLevel > profile.Level && profile.Level > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"Cast at level {castLevel} (its own level is {profile.Level})."));
        }

        var action = new StatBlockAction
        {
            Name = name,
            Kind = profile.Kind,
            Slot = profile.Kind == SpellProfileKinds.Parry ? StatBlockValues.ActionSlots.Reaction : slot,
            IsSpell = true,
            Magical = true,
            SpellLevel = profile.Level == 0 ? 0 : castLevel,
            Concentration = profile.Concentration,
            Usage = usage,
            Text = profile.Name,
            Notes = notes,
        };

        switch (profile.Kind)
        {
            case SpellProfileKinds.Attack:
                return action with
                {
                    AttackBonus = attackBonus,
                    Range = profile.AttackRange ?? StatBlockValues.AttackRanges.Ranged,
                    Damage = damage,
                    AttackRolls = Math.Max(1, targets),
                    OnHit = profile.Condition is { } onHit ? [new ActionEffect { Kind = StatBlockValues.EffectKinds.Condition, Condition = Condition(onHit, null, dc) }] : [],
                };
            case SpellProfileKinds.Save:
            {
                var save = new SaveSpec(profile.SaveAbility!, dc ?? 0, profile.OnSuccess ?? StatBlockValues.OnSuccess.None);
                return action with
                {
                    Save = save,
                    Damage = damage,
                    Area = profile.Area,
                    Targets = profile.Area is null ? Math.Max(1, targets) : 1,
                    Condition = profile.Condition is { } condition ? Condition(condition, save, dc) : null,
                };
            }

            case SpellProfileKinds.AutoHit:
                return action with { Damage = damage, Targets = Math.Max(1, targets) };
            case SpellProfileKinds.Heal:
            {
                var healing = profile.Healing ?? DamageFormula.Zero;
                for (var i = 0; i < above; i++)
                {
                    healing = profile.UpcastHealing is { } more ? healing.Plus(more) : healing;
                }

                if (profile.HealingAddsModifier)
                {
                    healing = healing.Plus(DamageFormula.Constant(abilityModifier));
                }

                return action with { Healing = healing, Targets = Math.Max(1, targets) };
            }

            case SpellProfileKinds.Parry:
                return action with { AcBonus = profile.AcBonus };
            default:
                throw new InvalidOperationException($"{profile.Ref} is not a combat spell ({profile.Kind}).");
        }
    }

    private static ConditionEffect Condition(SpellOverlayCondition condition, SaveSpec? save, int? dc) => new()
    {
        Condition = condition.Condition,
        Duration = condition.Duration,
        Rounds = condition.Rounds,
        SaveEnds = condition.Duration == StatBlockValues.Durations.SaveEnds && save is not null
            ? save with { OnSuccess = StatBlockValues.OnSuccess.None }
            : null,
        EscapeDc = condition.Duration == StatBlockValues.Durations.UntilEscape ? dc : null,
    };

    private static (int Level, bool Concentration) LevelAndConcentration(SrdDocument spell)
    {
        var root = spell.Root;
        var level = root.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        var concentration = root.TryGetProperty("concentration", out var c) && c.ValueKind == JsonValueKind.True;
        return (level, concentration);
    }

    // 2014 records: attack type, DC, area, damage and healing tables. Each becomes the profile's default, which the
    // overlay may then correct (Flame Strike's area is recorded as its 40-foot height).
    private static SpellProfile Derive2014(SrdDocument document, SpellProfile basic)
    {
        Spell2014 spell;
        try
        {
            spell = JsonSerializer.Deserialize<Spell2014>(document.Json, SrdJson.Options)!;
        }
        catch (JsonException ex)
        {
            return basic with { Reason = $"unreadable record: {ex.Message}" };
        }

        var notes = new List<string>();
        var bySlot = new Dictionary<int, IReadOnlyList<DamageRoll>>();
        var byCharacter = new Dictionary<int, IReadOnlyList<DamageRoll>>();
        var addsModifier = false;
        foreach (var entry in spell.Damage ?? [])
        {
            var type = entry.DamageType?.Index;
            foreach (var (table, target) in new[] { (entry.DamageAtSlotLevel, bySlot), (entry.DamageAtCharacterLevel, byCharacter) })
            {
                foreach (var (key, text) in table ?? new Dictionary<int, string>())
                {
                    var (dice, mod, choice) = SpellDice(text);
                    if (dice is null)
                    {
                        continue;
                    }

                    addsModifier |= mod;
                    if (choice)
                    {
                        notes.Add($"The record offers \"{text}\" at level {key}; the first is used.");
                    }

                    target[key] = [.. target.GetValueOrDefault(key) ?? [], new DamageRoll(dice, type)];
                }
            }
        }

        var baseDamage = bySlot.GetValueOrDefault(spell.Level) ?? byCharacter.GetValueOrDefault(1) ?? [];
        string? kind = null;
        if (spell.HealAtSlotLevel is { Count: > 0 })
        {
            kind = SpellProfileKinds.Heal;
        }
        else if (spell.AttackType is not null && baseDamage.Count > 0)
        {
            kind = SpellProfileKinds.Attack;
        }
        else if (spell.Dc is not null && baseDamage.Count > 0)
        {
            kind = SpellProfileKinds.Save;
        }

        var healing = spell.HealAtSlotLevel?.GetValueOrDefault(spell.Level) is { } heal ? SpellDice(heal) : (null, false, false);
        return basic with
        {
            Kind = kind ?? basic.Kind,
            AttackRange = spell.AttackType,
            SaveAbility = spell.Dc?.DcType.Index,
            OnSuccess = spell.Dc is null ? null : spell.Dc.DcSuccess == SaveSuccessTypes.Half ? StatBlockValues.OnSuccess.Half : StatBlockValues.OnSuccess.None,
            Damage = baseDamage,
            DamageBySlot = bySlot,
            DamageByCharacterLevel = byCharacter,
            DamageAddsModifier = addsModifier,
            Area = spell.AreaOfEffect is { } area && ShapeNames.TryGetValue(area.Type, out var shape) ? new AreaSpec(shape, area.Size) : null,
            Healing = healing.Item1,
            HealingAddsModifier = healing.Item2,
            UpcastHealing = healing.Item1,
            Notes = notes,
        };
    }

    // "1d8 + MOD" → (1d8, adds modifier); "4d6 OR 5d6" → (4d6, choice); "3d4 + 3" → 3d4+3.
    private static (DamageFormula? Dice, bool AddsModifier, bool Choice) SpellDice(string text)
    {
        var choice = text.Contains(" OR ", StringComparison.Ordinal);
        var first = choice ? text[..text.IndexOf(" OR ", StringComparison.Ordinal)] : text;
        var mod = first.Contains("MOD", StringComparison.Ordinal);
        var bare = first.Replace("+ MOD", string.Empty, StringComparison.Ordinal).Replace("+MOD", string.Empty, StringComparison.Ordinal).Trim();
        return (ProseText.Dice(bare), mod, choice);
    }

    private static SpellProfile Merge(SpellProfile derived, SpellOverlayEntry entry)
    {
        var damage = entry.Damage?.Select(Roll).ToList();
        var notes = entry.Note is null ? derived.Notes : [.. derived.Notes, entry.Note];
        return derived with
        {
            Kind = entry.Kind ?? derived.Kind,
            AttackRange = entry.Attack ?? derived.AttackRange,
            SaveAbility = entry.Save ?? derived.SaveAbility,
            OnSuccess = entry.OnSuccess ?? derived.OnSuccess,
            Damage = damage ?? derived.Damage,
            // An overlay that states the damage replaces the record's tables: they would otherwise win at their levels.
            DamageBySlot = damage is null ? derived.DamageBySlot : new Dictionary<int, IReadOnlyList<DamageRoll>>(),
            DamageByCharacterLevel = damage is null || entry.Cantrip is null ? derived.DamageByCharacterLevel : new Dictionary<int, IReadOnlyList<DamageRoll>>(),
            UpcastDamage = entry.Upcast?.Select(Roll).ToList() ?? derived.UpcastDamage,
            CantripScaling = entry.Cantrip ?? derived.CantripScaling,
            DamageAddsModifier = entry.DamageAddsModifier ?? derived.DamageAddsModifier,
            Targets = entry.Targets ?? derived.Targets,
            UpcastTargets = entry.UpcastTargets ?? derived.UpcastTargets,
            Area = entry.NoArea == true ? null : entry.Area is { } area ? new AreaSpec(area.Shape, area.Size) : derived.Area,
            Condition = entry.Condition ?? derived.Condition,
            Healing = entry.Heal is { } heal ? ProseText.Dice(heal) : derived.Healing,
            HealingAddsModifier = entry.HealModifier ?? derived.HealingAddsModifier,
            UpcastHealing = entry.UpcastHeal is { } up ? ProseText.Dice(up) : derived.UpcastHealing,
            AcBonus = entry.AcBonus ?? derived.AcBonus,
            Approximation = entry.Approximation ?? derived.Approximation,
            Reason = null,
            Notes = notes,
        };
    }

    private static DamageRoll Roll(SpellOverlayDamage d) => new(ProseText.Dice(d.Dice)!, d.Type);
}
