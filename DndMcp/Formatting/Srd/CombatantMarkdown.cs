using System.Globalization;
using System.Text;
using AreaRules = DndMcp.Domain.Dpr.DprTables;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using K = DndMcp.Domain.Simulation.StatBlockValues;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// <c>rules_get</c>'s <c>combatant</c> format: a monster's normalized <see cref="StatBlock"/>, shown as the simulator
/// reads it — every number it uses, what each trait does in a simulated fight (by kind), each action's parsed parts
/// (to-hit, damage per hit with its average, riders, saves, areas and targets, usage and recharge, an outright kill by
/// hit points, immunity after a successful save), the multiattack routines, the combat spells and slots, legendary
/// actions with their costs, and the normalizer's notes and warnings.
///
/// <para>
/// <b>Why a separate view.</b> The concise stat block is the SRD's text; a simulation result is numbers derived from it.
/// When a result looks wrong ("why does the dragon never breathe on two people?"), this is where the model and the user
/// see the reading the numbers came from: the breath's area is a 60-ft cone that the DMG's rule of thumb fills with 6
/// creatures, the Wing Attack is not simulated, Frightful Presence is a warning. Nothing the normalizer decided is hidden:
/// its notes and warnings are listed whole here (a simulation result condenses them).
/// </para>
/// <para>
/// A BODY like every per-kind formatter's (<see cref="SrdMarkdown"/> adds the title and the meta line): headings are
/// <c>###</c> or deeper, so it can sit under <c>## 2014</c> in a comparison. <c>SrdMarkdownRenderAllTests</c> renders every
/// monster of both editions in this format within <see cref="SrdMarkdown.MaxChars"/> and untruncated.
/// </para>
/// </summary>
internal static class CombatantMarkdown
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Body(StatBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return SrdMarkdownText.Blocks(
        [
            "*The stat block as balance_simulate reads it (and balance_dpr's target.monster): the numbers used, what each trait " +
            "and action does in a simulated fight, and what is left out. The SRD text itself is the concise format.*",
            Numbers(block),
            Adjustments(block),
            Traits(block),
            ActionSection("Actions", block.Actions),
            Multiattacks(block),
            Spells(block),
            ActionSection("Bonus actions", block.BonusActions),
            ActionSection("Reactions", block.Reactions),
            Legendary(block),
            List("Notes", block.Notes.Select(n => n)),
            List("Warnings (what the simulation leaves out or simplifies)", block.Warnings.Select(w => $"{w.Where} ({w.Code}): {w.Message}")),
        ]);
    }

    private static string Numbers(StatBlock block)
    {
        var lines = new List<string?>
        {
            $"{block.Size} {block.CreatureType} · CR {block.ChallengeRating} ({Number(block.Xp)} XP" +
            (block.XpInLair is { } lair ? $", {Number(lair)} in its lair" : string.Empty) +
            $") · Proficiency Bonus {Signed(block.ProficiencyBonus)} · {block.Edition} rules",
            $"**AC** {Number(block.ArmorClass)}" + (block.ArmorClassNote is { } note ? $" ({ArmorNote(note, block.ArmorClass)})" : string.Empty) +
            $" · **HP** {Number(block.HitPoints)} (rolled: {block.HitDice}) · **Initiative** {Signed(block.InitiativeBonus)} · **Speed** {Speeds(block)}",
            "**Abilities** " + string.Join(" · ", DslValues.Abilities.All.Select(a =>
                $"{Title(a)} {Number(block.Abilities.Score(a))} ({Signed(block.Modifier(a))})")),
            "**Saves** " + string.Join(" · ", DslValues.Abilities.All.Select(a =>
            {
                var bonus = block.SaveBonuses.TryGetValue(a, out var value) ? value : block.Modifier(a);
                return bonus != block.Modifier(a) ? $"**{Title(a)} {Signed(bonus)}**" : $"{Title(a)} {Signed(bonus)}";
            })) + " (bold: proficient)",
        };

        if (block.LegendaryResistance > 0)
        {
            lines.Add($"**Legendary Resistance** {Number(block.LegendaryResistance)}/day" +
                      (block.LegendaryResistanceInLair is { } inLair ? $" ({Number(inLair)} in its lair; the simulator is never in a lair)" : string.Empty) +
                      ": a failed save becomes a success, spent as the legendary_resistance policy says.");
        }

        if (block.Forms.Count > 0)
        {
            lines.Add("**Other forms** " + string.Join(", ", block.Forms.Select(f => $"`{f}`")) + " (each is its own stat block).");
        }

        return "### Numbers\n\n" + SrdMarkdownText.Lines(lines);
    }

    // "natural armor" stays as it is; "15 with Mage Armor" says which AC the simulator fights with.
    private static string ArmorNote(string note, int armorClass) =>
        note.Any(char.IsDigit) ? $"{note}; the simulator uses {Number(armorClass)}" : note;

    private static string Speeds(StatBlock block)
    {
        if (block.Speeds.Count == 0)
        {
            return "—";
        }

        var parts = block.Speeds
            .OrderBy(s => s.Key == "walk" ? 0 : 1)
            .Select(s => s.Key == "walk" ? $"{Number(s.Value)} ft." : $"{s.Key} {Number(s.Value)} ft.")
            .ToList();
        return string.Join(", ", parts) + (block.Hovers ? " (hover)" : string.Empty);
    }

    private static string Adjustments(StatBlock block)
    {
        var lines = new List<string?>
        {
            Adjustment("Resistances", block.Resistances),
            Adjustment("Immunities", block.Immunities),
            Adjustment("Vulnerabilities", block.Vulnerabilities),
            block.ConditionImmunities.Count > 0 ? "**Condition immunities** " + string.Join(", ", block.ConditionImmunities) : null,
        };

        return lines.Any(l => l is not null)
            ? "### Damage and conditions\n\n" + SrdMarkdownText.Lines(lines) +
              (lines.Take(3).Any(l => l is not null)
                  ? "\n\nResistance halves (rounded down), vulnerability doubles, immunity ignores the damage of that type; a qualified " +
                    "entry applies only to damage that is not magical (or not silvered / adamantine, as it says)."
                  : string.Empty)
            : "### Damage and conditions\n\nNo resistances, immunities, vulnerabilities or condition immunities.";
    }

    private static string? Adjustment(string label, IReadOnlyList<DamageAdjustment> adjustments) =>
        adjustments.Count == 0
            ? null
            : $"**{label}** " + string.Join(", ", adjustments.Select(a => a.Qualifier is null ? a.DamageType : $"{a.DamageType} ({Qualifier(a.Qualifier)})"));

    private static string Qualifier(string qualifier) => qualifier switch
    {
        K.DamageQualifiers.Nonmagical => "nonmagical only",
        K.DamageQualifiers.NonmagicalNotSilvered => "nonmagical and not silvered only",
        K.DamageQualifiers.NonmagicalNotAdamantine => "nonmagical and not adamantine only",
        _ => "a qualifier the simulator does not read; applied always",
    };

    private static string? Traits(StatBlock block)
    {
        if (block.Traits.Count == 0)
        {
            return null;
        }

        return "### Traits\n\n" + string.Join("\n", block.Traits.Select(t => $"- **{t.Name}** ({Kind(t.Kind)}): {TraitText(t)}"));
    }

    private static string Kind(string kind) => kind.Replace('_', ' ');

    private static string TraitText(StatBlockTrait trait) => trait.Kind switch
    {
        K.TraitKinds.MagicResistance => "Advantage on saves against spells and magical effects.",
        K.TraitKinds.PackTactics => "Advantage on its attack when an ally is engaged with the target (both on the front line).",
        K.TraitKinds.LegendaryResistance => $"{Number(trait.Uses ?? 0)}/day: a failed save becomes a success.",
        K.TraitKinds.Regeneration =>
            $"regains {Number(trait.Amount ?? 0)} HP at the start of its turn" +
            (trait.DamageTypes.Count > 0 ? $" unless it took {string.Join(" or ", trait.DamageTypes)} damage since its last turn." : "."),
        K.TraitKinds.UndeadFortitude => "damage that would drop it to 0 HP: a Con save (DC 5 + the damage) leaves it at 1 HP instead, unless the damage is radiant or from a critical hit.",
        K.TraitKinds.Relentless => $"once per fight, damage of {Number(trait.Amount ?? 0)} or less that would drop it to 0 HP leaves it at 1 HP.",
        K.TraitKinds.Evasion => "a Dex save for half damage takes none on a success and half on a failure.",
        K.TraitKinds.SneakAttack => $"once per turn, +{trait.Dice} ({Average(trait.Dice)}) on a hit with Advantage or with an ally engaged with the target.",
        K.TraitKinds.MartialAdvantage => $"once per turn, +{trait.Dice} ({Average(trait.Dice)}) on a hit when an ally is engaged with the target.",
        K.TraitKinds.BloodFrenzy => "Advantage on melee attacks against a creature missing any hit points.",
        K.TraitKinds.AdvantageWhileBloodied => "Advantage on its attacks while it has half its hit points or fewer.",
        K.TraitKinds.Reckless => "Advantage on its melee attacks; attacks against it have Advantage until its next turn.",
        K.TraitKinds.MagicWeapons => "its weapon attacks are magical (they overcome nonmagical resistance).",
        K.TraitKinds.RetaliationDamage => $"a creature that hits it with a melee attack takes {Rolls(trait.Damage)}{SaveSuffix(trait.Save)}.",
        K.TraitKinds.AuraDamage => AuraText(trait),
        K.TraitKinds.DeathBurst =>
            $"when it dies: {SaveText(trait.Save)}{AreaText(trait.Area, 0)}" +
            (trait.Damage.Count > 0 ? $", {Rolls(trait.Damage)} on a failure{OnSuccess(trait.Save)}" : string.Empty) +
            (trait.Condition is { } condition ? $"; {ConditionText(condition)} on a failure" : string.Empty) + ".",
        K.TraitKinds.NoCombatEffect => "no effect in a fight without a grid, light, terrain or senses.",
        K.TraitKinds.NotModelled => "not simulated (see the warnings).",
        _ => "—",
    };

    private static string AuraText(StatBlockTrait trait)
    {
        var area = AreaText(trait.Area, 0);
        if (trait.Damage.Count > 0)
        {
            return $"each enemy engaged with it{(area.Length > 0 ? $" ({area.TrimStart(',', ' ')})" : string.Empty)} takes {Rolls(trait.Damage)}" +
                   $"{SaveSuffix(trait.Save)} each turn.";
        }

        return trait.Condition is { } condition
            ? $"each enemy near it{(area.Length > 0 ? $" ({area.TrimStart(',', ' ')})" : string.Empty)}: {SaveText(trait.Save)} or {ConditionText(condition)}."
            : "an aura with no effect the simulator reads.";
    }

    private static string? ActionSection(string title, IReadOnlyList<StatBlockAction> actions) =>
        actions.Count == 0 ? null : $"### {title}\n\n" + string.Join("\n", actions.Select(a => "- " + ActionLine(a)));

    /// <summary>One action as the simulator reads it: "**Bite** — melee attack +14, 2d10+8 piercing + 2d6 fire (avg 26) per hit".</summary>
    private static string ActionLine(StatBlockAction action)
    {
        var tags = new List<string>();
        if (Usage(action.Usage, action.Name) is { } usage)
        {
            tags.Add(usage);
        }

        if (action.Slot == K.ActionSlots.Legendary)
        {
            tags.Add($"costs {Number(action.LegendaryCost)}");
            if (action.OncePerRound)
            {
                tags.Add("once per round");
            }
        }

        if (action.Concentration)
        {
            tags.Add("concentration");
        }

        var head = $"**{action.Name}**" + (tags.Count > 0 ? $" ({string.Join(", ", tags)})" : string.Empty);
        var text = $"{head} — {ActionBody(action)}{Outcomes(action)}";
        if (action.Notes.Count > 0)
        {
            text += $" *{string.Join(" ", action.Notes)}*";
        }

        return text;
    }

    private static string ActionBody(StatBlockAction action) => action.Kind switch
    {
        K.ActionKinds.Attack => AttackText(action),
        K.ActionKinds.Save => SaveActionText(action),
        K.ActionKinds.AutoHit => AutoHitText(action),
        K.ActionKinds.Heal =>
            $"heals {(action.SelfOnly ? "itself" : Plural(action.Targets, "ally", "allies"))} {action.Healing} ({Average(action.Healing)}) each.",
        // The simulator keeps a spell parry's AC up until the caster's next turn (Shield: "including against the triggering
        // attack"); a true Parry turns only the attack that triggered it, and only a melee one when its text says so (both
        // editions' Parry: "one melee attack that would hit it", "hit by a melee attack roll"), the engine's own test.
        K.ActionKinds.Parry => action.IsSpell
            ? $"+{Number(action.AcBonus ?? 0)} AC until the start of its next turn, including against the attack that would hit it (its reaction)."
            : $"+{Number(action.AcBonus ?? 0)} AC against one {(ParriesMeleeOnly(action) ? "melee " : string.Empty)}attack that would hit it (its reaction).",
        K.ActionKinds.UseActions =>
            "uses " + string.Join(", ", action.Uses.Select(u => u.Count == 1 ? u.ActionName : $"{u.ActionName} ×{Number(u.Count)}")) + ".",
        K.ActionKinds.NotModelled => "not simulated (see the warnings).",
        _ => action.Kind,
    };

    /// <summary>
    /// Whether a Parry works only against melee attacks: the simulator's own test (<c>CombatantCompiler</c>'s
    /// <c>ParryMeleeOnly</c>: the text says "melee attack"), so the view cannot claim an erinyes turns an arrow the engine
    /// lets through. The 2024 mummy lord's Whirlwind of Sand ("hit by an attack roll") turns any attack.
    /// </summary>
    private static bool ParriesMeleeOnly(StatBlockAction action) =>
        action.Text.Contains("melee attack", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An action with no attack roll and no save: its targets and the damage each takes. An area's targets are its
    /// creatures by the DMG's count, as the engine counts them (the 2024 lich's Deathly Teleport hits everyone in its
    /// 10-ft emanation, not "1 creature"). With no damage (2014 Power Word Kill, whose only effect is the kill that
    /// <see cref="Outcomes"/> states) it names only the targets.
    /// </summary>
    private static string AutoHitText(StatBlockAction action)
    {
        var who = action.Area is { } area ? AreaText(area, 0).TrimStart(',', ' ') : Plural(action.Targets, "creature", "creatures");
        return action.Damage.Count > 0
            ? $"no attack roll or save: {who}, {Rolls(action.Damage)} each{Magical(action)}."
            : $"no attack roll or save: {who}{Magical(action)}.";
    }

    /// <summary>
    /// What the action does beyond its damage and conditions, as the simulator applies it: an outright kill by hit points
    /// (Power Word Kill) and the immunity a successful save grants for the rest of the fight (Frightful Presence's 24
    /// hours). Without them a reader of "12d12 psychic" could not tell why a 90-HP fighter simply died, nor why a creature
    /// that saved once is never frightened by the same dragon again. The kill is said as the engine does it: outright for
    /// an action with no roll, on a hit for an attack, and only on a FAILED save for a save action (the 2024 solar's
    /// Slaying Bow: "Failure: If the creature has 100 Hit Points or fewer, it dies"; a target that saves is not killed).
    /// Empty for nearly every action, and for one that is not simulated.
    /// </summary>
    private static string Outcomes(StatBlockAction action)
    {
        if (action.Kind == K.ActionKinds.NotModelled)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        if (action.KillAtOrBelowHp is { } hp)
        {
            text.Append(action.Kind switch
                {
                    K.ActionKinds.Save => $" On a failure, a target with {Number(hp)} HP or fewer dies (no death saves); above that, ",
                    K.ActionKinds.Attack => $" On a hit, a target with {Number(hp)} HP or fewer dies (no death saves); above that, ",
                    _ => $" A target dies at {Number(hp)} HP or fewer (no death saves); above that, ",
                })
                .Append(action.Kind == K.ActionKinds.Save
                    ? (action.Damage.Count > 0, action.Condition is not null) switch
                    {
                        (true, true) => "it takes the damage and the condition applies.",
                        (true, false) => "it takes the damage.",
                        (false, true) => "the condition applies.",
                        _ => "a failure does nothing.",
                    }
                    : action.Damage.Count > 0 ? "the damage applies." : "it has no effect.");
        }

        if (action.ImmuneAfterSuccess)
        {
            text.Append(" A creature is immune for the rest of the fight after a successful save (or once its condition ends).");
        }

        return text.ToString();
    }

    private static string AttackText(StatBlockAction action)
    {
        var range = action.Range == K.AttackRanges.Ranged ? "ranged" : action.AlsoRanged ? "melee or ranged" : "melee";
        var rolls = action.AttackRolls > 1 ? $", {Number(action.AttackRolls)} attack rolls (each at a target the policy picks)" : string.Empty;
        var text = new StringBuilder($"{range} {(action.IsSpell ? "spell " : string.Empty)}attack {Signed(action.AttackBonus ?? 0)}{rolls}: ");
        text.Append(action.Damage.Count > 0 ? $"{Rolls(action.Damage)} per hit" : "no damage");
        foreach (var effect in action.OnHit)
        {
            text.Append("; ").Append(OnHitText(effect));
        }

        return text.Append(Magical(action)).Append('.').ToString();
    }

    private static string OnHitText(ActionEffect effect)
    {
        var size = effect.MaxSize is { } max ? $" ({max} or smaller)" : string.Empty;
        return effect.Kind switch
        {
            K.EffectKinds.Damage => $"plus {Rolls(effect.Damage)}",
            K.EffectKinds.Save =>
                $"on a hit, {SaveText(effect.Save)}{size}: " +
                string.Join(" and ", new[]
                {
                    effect.Damage.Count > 0 ? $"{Rolls(effect.Damage)} on a failure{OnSuccess(effect.Save)}" : null,
                    effect.Condition is { } c ? ConditionsText(c, effect.ExtraConditions) + " on a failure" : null,
                    effect.KillAtOrBelowHp is { } hp ? $"dies at {Number(hp)} HP or fewer on a failure" : null,
                }.OfType<string>()) +
                (effect.ImmuneAfterSuccess ? "; the target is immune for the rest of the fight after a successful save" : string.Empty),
            K.EffectKinds.Condition =>
                $"on a hit the target is {(effect.Condition is { } condition ? ConditionsText(condition, effect.ExtraConditions) : "affected")}{size}",
            _ => effect.Kind,
        };
    }

    private static string SaveActionText(StatBlockAction action)
    {
        var who = action.Area is { } area
            ? AreaText(area, 0).TrimStart(',', ' ')
            : Plural(action.Targets, "creature", "creatures");
        var parts = new List<string>();
        if (action.Damage.Count > 0)
        {
            parts.Add($"{Rolls(action.Damage)} on a failure{OnSuccess(action.Save)}");
        }

        if (action.Condition is { } condition)
        {
            parts.Add(ConditionsText(condition, action.ExtraConditions) + " on a failure");
        }

        return $"{SaveText(action.Save)}, {who}: {(parts.Count > 0 ? string.Join("; ", parts) : "no effect the simulator reads")}{Magical(action)}.";
    }

    private static string Magical(StatBlockAction action) =>
        action.IsSpell ? " (a spell: magical)" : action.Magical ? " (magical)" : string.Empty;

    private static string? Multiattacks(StatBlock block)
    {
        if (block.Multiattacks.Count == 0)
        {
            return null;
        }

        var lines = block.Multiattacks.Select(routine =>
        {
            var steps = routine.Steps.Select(s => s.Count == 1 ? s.ActionName : $"{s.ActionName} ×{Number(s.Count)}").ToList();
            var choice = routine.Choose > 0
                ? $"{(steps.Count > 0 ? "then " : string.Empty)}{Number(routine.Choose)} of: {string.Join(", ", routine.Options.Select(o => o.ActionName))}"
                : null;
            return $"- **{routine.Label}** ({Plural(routine.TotalUses, "use", "uses")}): " +
                   string.Join("; ", new[] { steps.Count > 0 ? string.Join(", ", steps) : null, choice }.OfType<string>());
        });

        var intro = block.Multiattacks.Count > 1 ? "Each turn the simulator takes the routine (or single action) with the highest expected damage.\n\n" : string.Empty;
        return "### Multiattack\n\n" + intro + string.Join("\n", lines);
    }

    private static string? Spells(StatBlock block)
    {
        if (block.Spells.Count == 0 && block.SpellSlots.Count == 0)
        {
            return null;
        }

        var text = new StringBuilder("### Spells (as actions)\n\n");
        if (block.SpellSlots.Count > 0)
        {
            text.Append("**Spell slots** ")
                .Append(string.Join(" · ", block.SpellSlots.OrderBy(s => s.Key).Select(s => $"{Ordinal(s.Key)} {Number(s.Value)}")))
                .Append(" (a spell uses a slot of its own level; no upcasting)\n\n");
        }

        text.Append(string.Join("\n", block.Spells.Select(s =>
            $"- {(s.SpellLevel is { } level ? $"{(level == 0 ? "cantrip" : Ordinal(level))}{(s.Slot == K.ActionSlots.Action ? string.Empty : $", {Slot(s.Slot)}")}: " : string.Empty)}{ActionLine(s)}")));
        return text.ToString().TrimEnd();
    }

    private static string Slot(string slot) => slot switch
    {
        K.ActionSlots.BonusAction => "bonus action",
        K.ActionSlots.Reaction => "reaction",
        K.ActionSlots.Legendary => "legendary action",
        _ => slot,
    };

    private static string? Legendary(StatBlock block)
    {
        if (block.Legendary is not { } legendary)
        {
            return null;
        }

        var uses = $"{Plural(legendary.Uses, "use", "uses")} per round" +
                   (legendary.UsesInLair is { } lair ? $" ({Number(lair)} in its lair; the simulator is never in a lair)" : string.Empty) +
                   ", reset at the start of its turn, spent after other creatures' turns, the best value first.";
        return "### Legendary actions\n\n" + uses + "\n\n" + string.Join("\n", legendary.Actions.Select(a => "- " + ActionLine(a)));
    }

    private static string? List(string title, IEnumerable<string> items)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList();
        return list.Count == 0 ? null : $"### {title}\n\n" + string.Join("\n", list.Select(i => "- " + i));
    }

    // "Recharge 5–6", "3/day", "a 3rd-level slot"; null at will. A shared recharge names its pool ("shared with Breath
    // Weapons", "shared with Hellfire Spellcasting"), except on the pool's namesake (the 2024 pit fiend's Hellfire
    // Spellcasting routine), which would read as shared with itself.
    private static string? Usage(UsageSpec usage, string actionName) => usage.Kind switch
    {
        K.UsageKinds.Recharge =>
            (usage.RechargeMin is 6 ? "Recharge 6" : $"Recharge {Number(usage.RechargeMin ?? 6)}–6") +
            (usage.Pool?.Replace("recharge:", string.Empty) is not { } pool
                ? string.Empty
                : pool == actionName ? ", one recharge shared with its spells and options" : $", one recharge shared with {pool}"),
        K.UsageKinds.PerDay => $"{Number(usage.Uses ?? 1)}/day",
        K.UsageKinds.Pool => usage.Pool is { } slot && slot.StartsWith("slot:", StringComparison.Ordinal) &&
                             int.TryParse(slot.AsSpan(5), NumberStyles.None, Invariant, out var level)
            ? $"a {Ordinal(level)}-level slot"
            : $"pool {usage.Pool}",
        _ => null,
    };

    // "Dex save DC 21".
    private static string SaveText(SaveSpec? save) =>
        save is null ? "a save" : $"{Title(save.Ability)} save DC {Number(save.Dc)}";

    private static string SaveSuffix(SaveSpec? save) =>
        save is null ? string.Empty : $" ({SaveText(save)}{OnSuccess(save).Replace(", ", "; ", StringComparison.Ordinal)})";

    private static string OnSuccess(SaveSpec? save) =>
        save?.OnSuccess == K.OnSuccess.Half ? ", half on a success" : save is null ? string.Empty : ", none on a success";

    // ", a 60-ft cone (6 creatures by the DMG's count)".
    private static string AreaText(AreaSpec? area, int _)
    {
        if (area is null)
        {
            return string.Empty;
        }

        var shape = area.Shape == K.Shapes.Emanation ? K.Shapes.Sphere : area.Shape;
        var rule = AreaRules.AreaTargets.FirstOrDefault(r => r.Shape == shape);
        var count = rule is null || area.Size < 1 ? 1 : Math.Max(1, (area.Size + rule.Divisor - 1) / rule.Divisor);
        return $", a {Number(area.Size)}-ft {area.Shape} ({Plural(count, "creature", "creatures")} by the DMG's count)";
    }

    private static string ConditionsText(ConditionEffect condition, IReadOnlyList<ConditionEffect> extra) =>
        string.Join(" and ", new[] { condition }.Concat(extra).Select(ConditionText));

    private static string ConditionText(ConditionEffect condition)
    {
        var duration = condition.Duration switch
        {
            K.Durations.UntilStartOfSourceTurn => "until the start of the monster's next turn",
            K.Durations.UntilEndOfSourceTurn => "until the end of the monster's next turn",
            K.Durations.SaveEnds => condition.SaveEnds is { } save
                ? $"save ends: {SaveText(save)} at the end of each of the target's turns{(condition.Rounds is { } cap ? $", at most {Plural(cap, "round", "rounds")}" : string.Empty)}"
                : "save ends",
            K.Durations.Rounds => $"for {Plural(condition.Rounds ?? 1, "round", "rounds")}",
            K.Durations.UntilEscape => condition.EscapeDc is { } dc ? $"until it escapes: DC {Number(dc)}" : "until it escapes",
            K.Durations.UntilStands => "until it stands up",
            K.Durations.Fight => "for the rest of the fight",
            K.Durations.UntilEndOfTargetTurn => "until the end of the target's next turn",
            K.Durations.UntilStartOfTargetTurn => "until the start of the target's next turn",
            _ => condition.Duration,
        };
        var ongoing = condition.OngoingDamage.Count > 0 ? $", taking {Rolls(condition.OngoingDamage)} at the start of each of its turns" : string.Empty;
        return $"{condition.Condition} ({duration}{ongoing})";
    }

    /// <summary>"2d10+8 piercing + 2d6 fire (avg 26)": every roll with its type, and the total average.</summary>
    private static string Rolls(IReadOnlyList<DamageRoll> rolls)
    {
        if (rolls.Count == 0)
        {
            return "no damage";
        }

        var parts = rolls.Select(r => r.DamageType is null ? $"{r.Dice} untyped" : $"{r.Dice} {r.DamageType}");
        return $"{string.Join(" + ", parts)} ({Average(rolls.Sum(r => Mean(r.Dice)))})";
    }

    private static string Average(DamageFormula? formula) => formula is null ? "avg 0" : Average(Mean(formula));

    private static string Average(double mean) => "avg " + mean.ToString("0.#", Invariant);

    /// <summary>A formula's expected value: each die's (sides + 1) ÷ 2, plus the flat part.</summary>
    internal static double Mean(DamageFormula formula) =>
        formula.Dice.Sum(d => (d.Negative ? -1 : 1) * d.Count * (d.Sides + 1) / 2.0) + formula.Flat;

    private static string Title(string ability) => ability.Length == 0 ? ability : char.ToUpperInvariant(ability[0]) + ability[1..];

    private static string Signed(int value) => SrdMarkdownText.Signed(value);

    private static string Number(int value) => value.ToString("N0", Invariant);

    private static string Plural(int count, string one, string many) => $"{Number(count)} {(count == 1 ? one : many)}";

    private static string Ordinal(int level) => level switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{Number(level)}th",
    };
}
