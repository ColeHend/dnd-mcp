using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// <c>campaign_character level_up {class, amount}</c>: one level in a class (contract §7.1, §5.16, D18).
///
/// <para>
/// <b>What it does.</b> +1 level in <c>class</c> (default: the only class; required when the sheet has several; an SRD
/// class not yet on the sheet is added at level 1, which is how a character multiclasses). Hit points: the given amount,
/// else the fixed value (die ÷ 2 + 1 + Con; 2024 at least 1), added to the maximum and to current hit points above 0.
/// Hit Dice maxima follow the classes; a single-class SRD caster's slots follow its table, and a warlock's Pact Magic slots
/// follow the warlock level even when multiclassed; a multiclass sheet with a Spellcasting class keeps its other slots and
/// gets a reminder to give them (the multiclassing rules are not in this server's data); a sheet whose classes do not
/// cast keeps every slot it was given (<see cref="SheetUpdate"/>'s derivation). The proficiency bonus follows the level
/// (it is not stored).
/// </para>
/// <para>
/// <b>Reminders</b>, because the choices are the player's: an Ability Score Improvement (or a feat) at the class levels the
/// 5e data gives it (2014: 4, 8, 12, 16, 19; 2024: 4, 8, 12, 16, with an Epic Boon at 19; the fighter also 6 and 14, the
/// rogue 10), "update resources for the new level" every time (resources are never derived), and a stored sim_profile
/// that no longer validates at the new level.
/// </para>
/// </summary>
public static class SheetLevelUp
{
    /// <summary>What refusals call the input: "Invalid level_up: …".</summary>
    public const string Subject = "level_up";

    /// <summary>The sheet one level higher.</summary>
    /// <param name="className">The class to raise (forgiving for SRD classes; a homebrew class by its name).</param>
    /// <param name="amount">The hit points gained this level (rolled at the table), else the fixed value.</param>
    /// <param name="edition">The campaign's ruleset, used when the sheet names none.</param>
    /// <exception cref="DndInputException">No classes, level 20, no class named on a multiclassed sheet, an unknown homebrew class, an amount out of range.</exception>
    public static SheetResult Apply(CharacterSheet sheet, string? className, int? amount, string edition)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        SheetUpdate.CheckEdition(edition);
        edition = sheet.EditionOr(edition);
        if (sheet.Classes.Count == 0)
        {
            throw new DndInputException(
                "level_up needs the sheet's classes: give them with update, e.g. {\"action\": \"update\", \"sheet\": {\"classes\": [{\"class\": \"wizard\", \"level\": 12}]}}.");
        }

        var level = sheet.Level ?? sheet.Classes.Sum(c => c.Level);
        if (level >= DslLimits.MaxLevel)
        {
            throw new DndInputException("The character is level 20, the highest level.");
        }

        var min = edition == V.Editions.E2024 ? SheetLimits.MinLevelUpHp2024 : SheetLimits.MinLevelUpHp;
        if (amount is { } given && (given < min || given > SheetLimits.MaxLevelUpHp))
        {
            throw new DndInputException(
                $"amount is {N(given)}; the hit points gained this level are {N(min)} to {N(SheetLimits.MaxLevelUpHp)}" +
                (edition == V.Editions.E2024 ? " (2024: at least 1)." : "."));
        }

        var index = Target(sheet, className);
        var classes = sheet.Classes.ToList();
        if (index < 0)
        {
            var srd = ClassTable.Find(className)!;
            classes.Add(new SheetClass(srd.Index, null, 1, srd.HitDie));
            index = classes.Count - 1;
        }
        else
        {
            classes[index] = classes[index] with { Level = classes[index].Level + 1 };
        }

        var raised = classes[index];
        var notes = new List<string>();
        var reminders = new List<SheetReminder>();
        var after = sheet with { Classes = classes, Level = level + 1 };
        notes.Add($"Level {N(level)} → {N(level + 1)}: {Display(raised)} {N(raised.Level)}.");

        var con = sheet.Modifier(V.Abilities.Con);
        var gain = amount ?? LevelHitPoints.FixedGain(raised.Die!.Value, con ?? 0, edition);
        if (sheet.MaxHp is { } max)
        {
            var newMax = Math.Max(SheetLimits.MinMaxHp, max + gain);
            after = after with { MaxHp = newMax };
            if (sheet.Hp is { } hp and > 0)
            {
                after = after with { Hp = Math.Clamp(hp + gain, 0, after.EffectiveMaxHp(edition) ?? newMax) };
            }

            notes.Add($"Hit point maximum {N(max)} → {N(newMax)} ({(amount is null ? $"the fixed d{N(raised.Die!.Value)} value {N(raised.Die.Value / 2 + 1)}, Con {Signed(con ?? 0)}" : "as given")}).");
            if (amount is null && con is null)
            {
                notes.Add("No Con score on the sheet: the fixed value adds +0 for Con.");
            }
        }
        else
        {
            notes.Add("No hit point maximum on the sheet: give max_hp with update.");
        }

        after = SheetUpdate.DeriveHitDice(after);
        after = SheetUpdate.DeriveSlots(after, edition, notes, reminders);

        var pbBefore = DslLimits.ProficiencyBonus(level);
        var pbAfter = DslLimits.ProficiencyBonus(level + 1);
        if (pbAfter != pbBefore)
        {
            notes.Add($"Proficiency bonus +{N(pbBefore)} → +{N(pbAfter)}.");
        }

        if (raised.Srd is { } info && Advancement.ImprovementAt(edition, info.Index, raised.Level) is { } improvement)
        {
            reminders.Add(improvement == SheetValues.ReminderKinds.EpicBoon
                ? new SheetReminder(
                    improvement,
                    $"{info.Name} {N(raised.Level)}: an Epic Boon feat (2024): add it to feats.",
                    "update",
                    new Dictionary<string, object?> { ["sheet"] = new ReminderPlaceholder("{\"feats\": [\"…\", \"Boon of …\"]}") })
                : new SheetReminder(
                    improvement,
                    $"{info.Name} {N(raised.Level)}: an Ability Score Improvement (or a feat): update abilities or feats.",
                    "update",
                    new Dictionary<string, object?> { ["sheet"] = new ReminderPlaceholder("{\"abilities\": {\"…\": …}} or {\"feats\": [\"…\"]}") }));
        }

        reminders.Add(new SheetReminder(
            SheetValues.ReminderKinds.UpdateResources,
            "Update resources for the new level (uses of Rage, Ki, Bladesong, Channel Divinity …): they are never derived.",
            "update",
            new Dictionary<string, object?> { ["sheet"] = new ReminderPlaceholder("{\"resources\": [{\"name\": \"…\", \"max\": …}]}") }));

        if (after.SimProfile is { } stored && SimProfile.ProblemAt(stored, level + 1) is { } problem)
        {
            reminders.Add(new SheetReminder(
                SheetValues.ReminderKinds.SimProfile,
                $"The stored sim_profile does not work at level {N(level + 1)}: {problem} Give one that does.",
                "update",
                new Dictionary<string, object?> { ["sim_profile"] = new ReminderPlaceholder("{…}") }));
        }

        return new SheetResult(after, SheetDiff.Between(sheet, after), notes, reminders);
    }

    /// <summary>The index of the class to raise, or -1 for an SRD class to add.</summary>
    private static int Target(CharacterSheet sheet, string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            if (sheet.Classes.Count == 1)
            {
                return 0;
            }

            throw new DndInputException(
                $"The sheet has several classes ({sheet.ClassSummary}): give class, the one that gains the level.");
        }

        var srd = ClassTable.Find(className);
        var key = srd?.Index ?? CampaignText.Key(className);
        var index = sheet.Classes.ToList().FindIndex(c => (c.Srd?.Index ?? CampaignText.Key(c.Class)) == key);
        if (index >= 0)
        {
            if (sheet.Classes[index].Die is null)
            {
                throw new DndInputException(
                    $"{sheet.Classes[index].Class} has no hit die on the sheet: give it with update (classes item hit_die) first.");
            }

            return index;
        }

        if (srd is null)
        {
            throw new DndInputException(
                $"class \"{DslText.Echo(className)}\" is not on the sheet ({sheet.ClassSummary}) and is not an SRD class ({ClassTable.List}): " +
                "add a homebrew class with update, with its hit_die.");
        }

        return -1;
    }

    private static string Display(SheetClass c) => c.Srd?.Name ?? c.Class;

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Signed(int value) => value >= 0 ? "+" + N(value) : N(value);
}
