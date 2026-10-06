using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Characters;

/// <summary>
/// <c>campaign_character condition {add, remove, level}</c>: conditions and named effects on the sheet, outside a fight.
///
/// <para>
/// <b>Defaults and rules.</b> A sheet condition lasts <see cref="SheetValues.Durations.UntilRemoved"/> (a curse, mummy
/// rot): out of a fight there is no turn order to time anything by. An SRD condition is matched forgivingly and stored by
/// its canonical name; any other name is a named effect, kept as typed and noted as such. A condition the sheet is immune
/// to (<c>defenses.condition_immune</c>, exhaustion included, checked before anything else about it) is refused, as the
/// tracker refuses it (contract §5.1); so is a duplicate. Exhaustion is never an entry: it is the
/// <see cref="CharacterSheet.Exhaustion"/> column, changed by <c>level</c> levels (default 1, capped 0-6); 6 means the
/// character dies (a reminder says so), and in 2014 the maximum halves from 4 (<see cref="Rules.HitPointMath"/>), so
/// current hit points above it are lowered.
/// </para>
/// <para>
/// <b>Concentration</b> ends, with a note, when the call adds a condition that is or includes Incapacitated
/// (<see cref="SheetValues.Conditions.Incapacitating"/>) or brings exhaustion to 6 (death): contract §5.8 is shared by the
/// tracker, the sheet and the simulator, and a sheet left concentrating while incapacitated would seed a combatant that is.
/// </para>
/// <para>
/// <b>"concentration" is not a condition to add</b>: <c>remove: ["concentration"]</c> ends the sheet's concentration column
/// (the Repository's), so an effect stored under that name could never be removed by any call; it is refused, pointing at
/// the column.
/// </para>
/// <para>
/// Removals are applied before additions. Every problem in the call is reported together (up to five) and nothing is
/// applied when there is one.
/// </para>
/// </summary>
public static class SheetConditions
{
    /// <summary>What refusals call the input: "Invalid condition: …".</summary>
    public const string Subject = "condition";

    /// <summary>The name <c>add</c> refuses: the sheet's <c>concentration</c> column, which <c>remove</c> ends by this name.</summary>
    public const string ConcentrationName = "concentration";

    /// <summary>The sheet with the conditions removed and added.</summary>
    /// <param name="edition">The campaign's ruleset, used when the sheet names none.</param>
    /// <exception cref="DndInputException">Nothing to add or remove, a bad name or level, an immunity, a duplicate, or a condition the sheet does not have.</exception>
    public static SheetResult Apply(CharacterSheet sheet, IReadOnlyList<string>? add, IReadOnlyList<string>? remove, int? level, string edition)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        SheetUpdate.CheckEdition(edition);
        var problems = new List<string>();
        var notes = new List<string>();
        var reminders = new List<SheetReminder>();
        if ((add?.Count ?? 0) + (remove?.Count ?? 0) == 0)
        {
            throw new DndInputException("give add or remove: condition names, e.g. {\"add\": [\"poisoned\"]}, or exhaustion with level.");
        }

        var mentionsExhaustion = (add ?? []).Concat(remove ?? []).Any(IsExhaustion);
        if (level is not null && !mentionsExhaustion)
        {
            problems.Add("level is for exhaustion only: {\"add\": [\"exhaustion\"], \"level\": 1}.");
        }

        if (level is { } l && (l < 1 || l > SheetLimits.MaxExhaustion))
        {
            problems.Add($"level is {N(l)}; exhaustion levels are 1 to 6.");
        }

        var steps = level ?? 1;
        var conditions = sheet.Conditions.ToList();
        var exhaustion = sheet.Exhaustion;

        for (var i = 0; i < (remove?.Count ?? 0); i++)
        {
            var text = remove![i];
            if (string.IsNullOrWhiteSpace(text))
            {
                problems.Add($"remove item {N(i + 1)} is empty; give a condition's name.");
            }
            else if (IsExhaustion(text))
            {
                if (exhaustion == 0)
                {
                    problems.Add("remove: the sheet has no exhaustion.");
                }

                exhaustion = Math.Max(0, exhaustion - steps);
            }
            else if (conditions.FindIndex(c => Same(c.Name, text)) is var at and >= 0)
            {
                conditions.RemoveAt(at);
            }
            else
            {
                var names = conditions.Count == 0 ? "none" : string.Join(", ", conditions.Select(c => c.Name));
                problems.Add($"remove item {N(i + 1)}: the sheet has no \"{DslText.Echo(text)}\" (its conditions: {names}).");
            }
        }

        for (var i = 0; i < (add?.Count ?? 0); i++)
        {
            var text = add![i];
            if (string.IsNullOrWhiteSpace(text) || !DslText.IsOneLine(text.Trim(), SheetLimits.MaxNameLength))
            {
                problems.Add($"add item {N(i + 1)} must be a condition or effect name, one line of at most {N(SheetLimits.MaxNameLength)} characters.");
                continue;
            }

            if (IsExhaustion(text))
            {
                if (sheet.Defenses.ConditionImmune.Contains(SheetValues.Conditions.Exhaustion, StringComparer.Ordinal))
                {
                    problems.Add($"add item {N(i + 1)}: the sheet is immune to exhaustion (defenses.condition_immune).");
                    continue;
                }

                if (exhaustion >= SheetLimits.MaxExhaustion)
                {
                    problems.Add("add: the sheet is already at exhaustion 6.");
                }

                exhaustion = Math.Min(SheetLimits.MaxExhaustion, exhaustion + steps);
                continue;
            }

            // Concentration is the sheet's own column (review C06): stored as an effect it could never be removed, since
            // remove ["concentration"] ends the column's concentration.
            if (CampaignText.Key(text) == ConcentrationName)
            {
                problems.Add(
                    $"add item {N(i + 1)}: \"concentration\" is not a condition: the sheet holds a concentration in its own column " +
                    "(a fight's write-back sets it; {\"remove\": [\"concentration\"]} ends it).");
                continue;
            }

            var srd = SheetValues.Conditions.Set.TryMatch(text, out var canonical);
            var name = srd ? canonical! : text.Trim();
            if (srd && sheet.Defenses.ConditionImmune.Contains(canonical!, StringComparer.Ordinal))
            {
                problems.Add($"add item {N(i + 1)}: the sheet is immune to {canonical} (defenses.condition_immune).");
            }
            else if (conditions.Any(c => Same(c.Name, name)))
            {
                problems.Add($"add item {N(i + 1)}: the sheet already has \"{DslText.Echo(name)}\".");
            }
            else
            {
                conditions.Add(new SheetCondition(name, null, SheetValues.Durations.UntilRemoved, null, null));
                if (!srd)
                {
                    notes.Add($"\"{name}\" is not an SRD condition: tracked as an effect.");
                }
            }
        }

        DslProblems.ThrowIfAny(problems, Subject);

        var after = sheet with { Conditions = conditions, Exhaustion = exhaustion };
        if (exhaustion != sheet.Exhaustion)
        {
            notes.Add($"Exhaustion {N(sheet.Exhaustion)} → {N(exhaustion)}.");
            after = CapHitPoints(after, edition, notes);
            if (exhaustion >= SheetLimits.MaxExhaustion)
            {
                reminders.Add(Died("Exhaustion 6: the character dies."));
            }
        }

        foreach (var c in conditions.Except(sheet.Conditions))
        {
            notes.Add($"Added {c.Name} (until removed).");
        }

        foreach (var c in sheet.Conditions.Except(conditions))
        {
            notes.Add($"Removed {c.Name}.");
        }

        if (sheet.Concentration is { } held && EndsConcentration(conditions.Except(sheet.Conditions), exhaustion, sheet.Exhaustion) is { } reason)
        {
            notes.Add($"Concentration on {held.Display} ended: {reason}.");
            after = after with { Concentration = null };
        }

        return new SheetResult(after, SheetDiff.Between(sheet, after), notes, reminders);
    }

    /// <summary>Current hit points lowered to the effective maximum when it fell below them (2014 exhaustion 4+).</summary>
    internal static CharacterSheet CapHitPoints(CharacterSheet sheet, string edition, List<string> notes)
    {
        if (sheet.Hp is { } hp && sheet.EffectiveMaxHp(edition) is { } effective && hp > effective)
        {
            notes.Add($"Hit point maximum now {N(effective)}: hp {N(hp)} → {N(effective)}.");
            return sheet with { Hp = effective };
        }

        return sheet;
    }

    /// <summary>Why the change ends concentration (contract §5.8), or null when it does not.</summary>
    private static string? EndsConcentration(IEnumerable<SheetCondition> added, int exhaustion, int exhaustionBefore)
    {
        if (exhaustion >= SheetLimits.MaxExhaustion && exhaustionBefore < SheetLimits.MaxExhaustion)
        {
            return "exhaustion 6, the character died";
        }

        return added.FirstOrDefault(c => SheetValues.Conditions.Incapacitating.Contains(c.Name, StringComparer.Ordinal)) is { } condition
            ? condition.Name == "incapacitated" ? "incapacitated" : $"{condition.Name} (incapacitated)"
            : null;
    }

    /// <summary>The reminder that the character has died (no call resolves it; a status proposal is the Repository's).</summary>
    internal static SheetReminder Died(string text) => new(SheetValues.ReminderKinds.Died, text);

    private static bool IsExhaustion(string? text) =>
        SheetValues.Conditions.Set.TryMatch(text, out var canonical) && canonical == SheetValues.Conditions.Exhaustion;

    /// <summary>The same condition: both match one SRD condition, or their names compare equal (<see cref="CampaignText.Key"/>).</summary>
    private static bool Same(string stored, string typed)
    {
        if (SheetValues.Conditions.Set.TryMatch(typed, out var canonical) && SheetValues.Conditions.Set.TryMatch(stored, out var storedCanonical))
        {
            return canonical == storedCanonical;
        }

        return CampaignText.Key(stored) == CampaignText.Key(typed);
    }

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
