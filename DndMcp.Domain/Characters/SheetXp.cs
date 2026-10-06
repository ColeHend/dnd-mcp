using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Characters;

/// <summary>
/// <c>campaign_character xp {amount}</c>: experience points added (or, negative, taken away) on the sheet. A sheet with no
/// XP total starts one at 0 when XP is given to it (said as "no XP total", never "milestone levelling": review U02, a
/// model read that as a policy the DM had chosen and declined to give the XP). Reaching a level's threshold never levels
/// the character up by itself: hit points, features and choices come with a level, so the result REMINDS with the
/// <c>level_up</c> call (contract §7.1), or, on a sheet with a level and no classes (which <c>level_up</c> refuses), with
/// the <c>update</c> call that gives the new level.
/// </summary>
public static class SheetXp
{
    /// <summary>The sheet with <paramref name="amount"/> XP added.</summary>
    /// <exception cref="DndInputException">The amount is 0 or out of range, or the total would fall below 0.</exception>
    public static SheetResult Apply(CharacterSheet sheet, int amount)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (amount == 0 || Math.Abs((long)amount) > SheetLimits.MaxXpAmount)
        {
            throw new DndInputException(
                $"amount is {N(amount)}; give the XP gained, 1 to {N(SheetLimits.MaxXpAmount)} (negative to take XP away).");
        }

        var was = sheet.Xp ?? 0;
        var now = (long)was + amount;
        if (now < 0)
        {
            throw new DndInputException($"amount is {N(amount)}, but the sheet has {N(was)} XP: XP cannot go below 0.");
        }

        if (now > SheetLimits.MaxXp)
        {
            throw new DndInputException($"the sheet would have {N(now)} XP; the most is {N(SheetLimits.MaxXp)}.");
        }

        var after = sheet with { Xp = (int)now };
        var notes = new List<string>();
        if (sheet.Xp is null)
        {
            notes.Add("The sheet had no XP total; it starts at 0.");
        }

        notes.Add($"XP {N(was)} → {N(now)} ({(amount > 0 ? "+" : string.Empty)}{N(amount)}).");
        var reminders = new List<SheetReminder>();
        if (sheet.Level is { } level)
        {
            if (Advancement.LevelForXp((int)now) > level)
            {
                reminders.Add(LevelDue((int)now, level, sheet.Classes.Count > 1, levelOnly: sheet.Classes.Count == 0));
            }
            else if (Advancement.NextThreshold(level) is { } next)
            {
                notes.Add($"Level {N(level + 1)} at {N(next)} XP.");
            }
        }

        return new SheetResult(after, SheetDiff.Between(sheet, after), notes, reminders);
    }

    /// <summary>The reminder that an XP total reaches a level the sheet does not have yet.</summary>
    /// <param name="multiclass">The sheet has several classes, so the call must name one.</param>
    /// <param name="levelOnly">The sheet has a level and no classes: <c>level_up</c> refuses it, so the call is <c>update</c> with the new level.</param>
    public static SheetReminder LevelDue(int xp, int level, bool multiclass = false, bool levelOnly = false)
    {
        var reached = Advancement.LevelForXp(xp);
        var levels = reached - level;
        if (levelOnly)
        {
            return new SheetReminder(
                SheetValues.ReminderKinds.LevelDue,
                $"{N(xp)} XP reaches level {N(reached)} ({N(Advancement.XpFor(reached))} XP): the sheet has a level and no classes, so give " +
                "the new level with update (or its classes, for level_up).",
                "update",
                new Dictionary<string, object?> { ["sheet"] = new System.Text.Json.Nodes.JsonObject { ["level"] = reached } });
        }

        return new SheetReminder(
            SheetValues.ReminderKinds.LevelDue,
            $"{N(xp)} XP reaches level {N(reached)} ({N(Advancement.XpFor(reached))} XP): level up with level_up" +
            (levels > 1 ? $", once per level ({N(levels)} levels)." : "."),
            "level_up",
            multiclass ? new Dictionary<string, object?> { ["class"] = new ReminderPlaceholder("\"…\"") } : null);
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
