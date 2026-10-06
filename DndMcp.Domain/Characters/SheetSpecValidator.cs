using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using FluentValidation;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// The checks a <see cref="SheetSpec"/> passes before anything is applied: every range, every name against its vocabulary
/// (abilities, damage types, SRD conditions, recharges, editions), the class list (one entry per class, levels summing to
/// at most 20, a hit die that is a die, and the SRD's own for an SRD class), slot counts, and the resource entries.
/// The checks that need the existing sheet (a level on a sheet that has classes, a hit die for a non-SRD class the sheet
/// does not already have, hp above the maximum, used above a resource's maximum) are <see cref="SheetUpdate"/>'s and use
/// the same message style.
///
/// <para>
/// <b>One refusal, up to five problems</b> (<see cref="DslProblems"/>): a sheet is a large object written in one go, and a
/// round trip per mistake turns four mistakes into five calls. Each problem says where it is the way the host's argument
/// guard does ("classes item 2 (Rogue): level is 0; it is 1 to 20."), counting items from 1, and what to send instead.
/// Messages quote only what the caller sent, never stored sheet text (another view's text must not reach a refusal).
/// </para>
/// </summary>
public sealed class SheetSpecValidator : AbstractValidator<SheetSpec>
{
    /// <summary>What the refusal calls the input: "Invalid sheet: …".</summary>
    public const string Subject = "sheet";

    public static SheetSpecValidator Instance { get; } = new();

    public SheetSpecValidator()
    {
        RuleFor(s => s).Custom((spec, context) => Check(spec, new Problems<SheetSpec>(context, string.Empty), context));
    }

    /// <summary>Validates the spec and throws every problem found (up to five listed) as one <see cref="DndInputException"/>.</summary>
    /// <exception cref="DndInputException">The spec has at least one problem.</exception>
    public static void ThrowIfInvalid(SheetSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        DslProblems.ThrowIfAny(DslProblems.Messages(Instance.Validate(spec)), Subject);
    }

    private static void Check(SheetSpec spec, Problems<SheetSpec> problems, ValidationContext<SheetSpec> context)
    {
        Name(problems, "player", spec.Player);
        problems.Known(V.Editions.Set, "ruleset", spec.Ruleset);
        Name(problems, "species", spec.Species);
        Name(problems, "lineage", spec.Lineage);
        Name(problems, "background", spec.Background);
        Classes(spec, problems, context);

        if (spec.Level is not null && spec.Classes is not null)
        {
            problems.Add("level is the sum of the class levels: give classes or level, not both.");
        }

        problems.InRange("level", spec.Level, DslLimits.MinLevel, DslLimits.MaxLevel);
        problems.InRange("xp", spec.Xp, 0, SheetLimits.MaxXp);
        foreach (var (ability, score) in spec.Abilities?.Given() ?? [])
        {
            problems.InRange($"abilities {ability}", score, DslLimits.MinAbilityScore, DslLimits.MaxAbilityScore);
        }

        problems.InRange("ac", spec.Ac, SheetLimits.MinAc, SheetLimits.MaxAc);
        problems.InRange("max_hp", spec.MaxHp, SheetLimits.MinMaxHp, SheetLimits.MaxHp);
        problems.InRange("max_hp_reduction", spec.MaxHpReduction, 0, SheetLimits.MaxHp);
        problems.InRange("hp", spec.Hp, 0, SheetLimits.MaxHp);
        problems.InRange("temp_hp", spec.TempHp, 0, SheetLimits.MaxHp);
        problems.InRange("speed", spec.Speed, 0, SheetLimits.MaxSpeed);
        problems.InRange("initiative_bonus", spec.InitiativeBonus, SheetLimits.MinInitiativeBonus, SheetLimits.MaxInitiativeBonus);
        problems.InRange("passive_perception", spec.PassivePerception, SheetLimits.MinPassivePerception, SheetLimits.MaxPassivePerception);
        problems.InRange("spell_save_dc", spec.SpellSaveDc, SheetLimits.MinSpellSaveDc, SheetLimits.MaxSpellSaveDc);
        problems.InRange("spell_attack", spec.SpellAttack, SheetLimits.MinSpellAttack, SheetLimits.MaxSpellAttack);

        ListOf(problems, "save_proficiencies", spec.SaveProficiencies, V.Abilities.Set, "str, dex, con, int, wis or cha");
        foreach (var (ability, bonus) in spec.SaveBonus?.Given() ?? [])
        {
            problems.InRange($"save_bonus {ability}", bonus, -SheetLimits.MaxSaveBonus, SheetLimits.MaxSaveBonus);
        }

        if (spec.Defenses is { } defenses)
        {
            ListOf(problems, "defenses resist", defenses.Resist, V.DamageTypes.Set, V.DamageTypes.Set.List);
            ListOf(problems, "defenses immune", defenses.Immune, V.DamageTypes.Set, V.DamageTypes.Set.List);
            ListOf(problems, "defenses vulnerable", defenses.Vulnerable, V.DamageTypes.Set, V.DamageTypes.Set.List);
            ListOf(problems, "defenses condition_immune", defenses.ConditionImmune, SheetValues.Conditions.Set, SheetValues.Conditions.Set.List);
        }

        Slots(spec, problems);
        Resources(spec, problems, context);
        foreach (var (field, list) in new[] { ("feats", spec.Feats), ("features", spec.Features), ("spells", spec.Spells), ("languages", spec.Languages) })
        {
            Names(problems, field, list);
        }

        if (spec.Notes is { Length: > SheetLimits.MaxNotesLength })
        {
            problems.Add($"notes is {DslText.Number(spec.Notes.Length)} characters; the most is {DslText.Number(SheetLimits.MaxNotesLength)}.");
        }

        Name(problems, "sheet_source", spec.SheetSource);
    }

    private static void Classes(SheetSpec spec, Problems<SheetSpec> problems, ValidationContext<SheetSpec> context)
    {
        if (spec.Classes is not { } classes)
        {
            return;
        }

        if (classes.Count == 0)
        {
            problems.Add("classes is empty; give at least one, e.g. [{\"class\": \"wizard\", \"level\": 12}], or leave it out.");
            return;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        for (var i = 0; i < classes.Count; i++)
        {
            var item = classes[i];
            var where = item?.Class is { } named && !string.IsNullOrWhiteSpace(named)
                ? $"classes item {DslText.Number(i + 1)} ({DslText.Echo(named.Trim())})"
                : $"classes item {DslText.Number(i + 1)}";
            var at = new Problems<SheetSpec>(context, where);
            if (item is null)
            {
                at.Add("is null; give a class, e.g. {\"class\": \"wizard\", \"level\": 12}.");
                continue;
            }

            var srd = ClassTable.Find(item.Class);
            if (string.IsNullOrWhiteSpace(item.Class))
            {
                at.Add($"class is required: an SRD class ({ClassTable.List}) or a homebrew class's name with its hit_die.");
            }
            else if (!DslText.IsOneLine(item.Class.Trim(), SheetLimits.MaxNameLength))
            {
                at.Add($"class must be one line of at most {DslText.Number(SheetLimits.MaxNameLength)} characters.");
            }
            else
            {
                var key = srd?.Index ?? CampaignText.Key(item.Class);
                if (seen.TryGetValue(key, out var first))
                {
                    at.Add($"is the same class as item {DslText.Number(first)}; give each class once, with its total levels.");
                }
                else
                {
                    seen[key] = i + 1;
                }
            }

            if (item.Subclass is { } subclass && !DslText.IsOneLine(subclass.Trim(), SheetLimits.MaxNameLength))
            {
                at.Add($"subclass must be one line of at most {DslText.Number(SheetLimits.MaxNameLength)} characters.");
            }

            if (item.Level is null)
            {
                at.Add("level is required: the levels in this class, 1-20.");
            }
            else if (at.InRange("level", item.Level, DslLimits.MinLevel, DslLimits.MaxLevel))
            {
                total += item.Level.Value;
            }

            if (item.HitDie is { } die)
            {
                if (!SheetLimits.HitDice.Contains(die))
                {
                    at.Add($"hit_die is {DslText.Number(die)}; give 4, 6, 8, 10 or 12.");
                }
                else if (srd is not null && srd.HitDie != die)
                {
                    at.Add($"hit_die is {DslText.Number(die)}, but the {srd.Name}'s hit die is d{DslText.Number(srd.HitDie)}; leave hit_die out for an SRD class.");
                }
            }
        }

        if (total > DslLimits.MaxLevel)
        {
            problems.Add($"classes: the levels add up to {DslText.Number(total)}; a character level is {DslLimits.MinLevel} to {DslLimits.MaxLevel}.");
        }
    }

    private static void Slots(SheetSpec spec, Problems<SheetSpec> problems)
    {
        if (spec.Slots is { } slots)
        {
            if (slots.Count > SheetLimits.MaxSpellLevel)
            {
                problems.Add($"slots has {DslText.Number(slots.Count)} levels; give at most {SheetLimits.MaxSpellLevel} (index 0 = 1st level), e.g. [4, 3, 3].");
            }

            for (var i = 0; i < Math.Min(slots.Count, SheetLimits.MaxSpellLevel); i++)
            {
                problems.InRange($"slots item {DslText.Number(i + 1)} (level {DslText.Number(i + 1)} slots)", slots[i], 0, SheetLimits.MaxSlotsPerLevel);
            }
        }

        if (spec.Pact is { } pact)
        {
            if (pact.Max is null)
            {
                problems.Add("pact max is required: the Pact Magic slots, 0-4 (0 removes them), e.g. {\"level\": 3, \"max\": 2}.");
            }
            else
            {
                problems.InRange("pact max", pact.Max, 0, SheetLimits.MaxPactSlots);
            }

            if (pact.Level is null && pact.Max is > 0)
            {
                problems.Add("pact level is required: the slots' level, 1-5, e.g. {\"level\": 3, \"max\": 2}.");
            }
            else
            {
                problems.InRange("pact level", pact.Level, 1, SheetLimits.MaxPactLevel);
            }
        }
    }

    private static void Resources(SheetSpec spec, Problems<SheetSpec> problems, ValidationContext<SheetSpec> context)
    {
        if (spec.Resources is not { } resources)
        {
            return;
        }

        if (resources.Count > SheetLimits.MaxResources)
        {
            problems.Add($"resources has {DslText.Number(resources.Count)} items; a sheet holds at most {DslText.Number(SheetLimits.MaxResources)}.");
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < resources.Count; i++)
        {
            var item = resources[i];
            var where = item?.Name is { } named && !string.IsNullOrWhiteSpace(named)
                ? $"resources item {DslText.Number(i + 1)} ({DslText.Echo(named.Trim())})"
                : $"resources item {DslText.Number(i + 1)}";
            var at = new Problems<SheetSpec>(context, where);
            if (item is null)
            {
                at.Add("is null; give a resource, e.g. {\"name\": \"Bladesong\", \"max\": 4, \"recharge\": \"long_rest\"}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                at.Add("name is required, e.g. \"Bladesong\".");
            }
            else if (!DslText.IsOneLine(item.Name.Trim(), SheetLimits.MaxNameLength))
            {
                at.Add($"name must be one line of at most {DslText.Number(SheetLimits.MaxNameLength)} characters.");
            }
            else if (SheetResource.KeyOf(item.Name) is not { Length: > 0 } key)
            {
                at.Add("name needs a letter or a digit: resources are keyed by it.");
            }
            else if (seen.TryGetValue(key, out var first))
            {
                at.Add($"is the same resource as item {DslText.Number(first)}; give each resource once.");
            }
            else
            {
                seen[key] = i + 1;
            }

            if (item.Remove == true)
            {
                if (item.Max is not null || item.Used is not null || item.Recharge is not null || item.State is not null || item.Note is not null)
                {
                    at.Add("remove takes only the name: {\"name\": \"…\", \"remove\": true}.");
                }

                continue;
            }

            at.InRange("max", item.Max, 0, SheetLimits.MaxResourceUses);
            at.InRange("used", item.Used, 0, SheetLimits.MaxResourceUses);
            if (item.Max is { } max && item.Used is { } used && used > max)
            {
                at.Add($"used is {DslText.Number(used)}, more than max {DslText.Number(max)}.");
            }

            at.Known(SheetValues.Recharges.Set, "recharge", item.Recharge);
            if (item.State is not null && (item.Max is not null || item.Used is not null || item.Recharge is not null))
            {
                at.Add("a resource is counted (max, used, recharge) or a tracker (state), not both.");
            }

            if (item.State is { } state && !DslText.IsOneLine(state.Trim(), SheetLimits.MaxNameLength))
            {
                at.Add($"state must be one line of at most {DslText.Number(SheetLimits.MaxNameLength)} characters, e.g. \"set\".");
            }

            if (item.Note is { } note && !DslText.IsOneLine(note.Trim(), SheetLimits.MaxNoteLength))
            {
                at.Add($"note must be one line of at most {DslText.Number(SheetLimits.MaxNoteLength)} characters.");
            }
        }
    }

    /// <summary>A one-line name of at most <see cref="SheetLimits.MaxNameLength"/> characters (an empty string clears the field).</summary>
    private static void Name(Problems<SheetSpec> problems, string field, string? text)
    {
        if (text is not null && !DslText.IsOneLine(text.Trim(), SheetLimits.MaxNameLength))
        {
            problems.Add($"{field} must be one line of at most {DslText.Number(SheetLimits.MaxNameLength)} characters.");
        }
    }

    private static void Names(Problems<SheetSpec> problems, string field, IReadOnlyList<string>? list)
    {
        if (list is null)
        {
            return;
        }

        if (list.Count > SheetLimits.MaxListItems)
        {
            problems.Add($"{field} has {DslText.Number(list.Count)} items; the most is {DslText.Number(SheetLimits.MaxListItems)}.");
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(list[i]) || !DslText.IsOneLine(list[i].Trim(), SheetLimits.MaxNameLength))
            {
                problems.Add($"{field} item {DslText.Number(i + 1)} must be a name of one line, at most {DslText.Number(SheetLimits.MaxNameLength)} characters.");
            }
        }
    }

    private static void ListOf(Problems<SheetSpec> problems, string field, IReadOnlyList<string>? list, DslValueSet set, string accepted)
    {
        for (var i = 0; i < (list?.Count ?? 0); i++)
        {
            if (!set.TryMatch(list![i], out _))
            {
                problems.Add($"{field} item {DslText.Number(i + 1)} \"{DslText.Echo(list[i])}\" is not {Problems<SheetSpec>.Article(set.What)} {set.What}; give {accepted}.");
            }
        }
    }
}
