using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Characters;

/// <summary>
/// <c>campaign_character use {slot_level | pact | resource, amount = 1}</c>: spending (or, with a negative amount,
/// restoring) one spell slot level, the Pact Magic slots, or a counted resource. Never beyond what is left, never below
/// none used: a refusal says how many there are, because a silent clamp would hide a miscount the table should hear about.
/// </summary>
public static class SheetUse
{
    /// <summary>The sheet with the uses spent or restored.</summary>
    /// <param name="slotLevel">A spell slot level 1-9.</param>
    /// <param name="pact">The Pact Magic slots.</param>
    /// <param name="resource">A resource by name (its slug, its name, or a unique start of its slug).</param>
    /// <param name="amount">Uses spent; negative restores.</param>
    /// <exception cref="DndInputException">Not exactly one of the three; the sheet has no such slots or resource; a tracker;
    /// more than is left (or restored than is used); an amount of 0.</exception>
    public static SheetResult Apply(CharacterSheet sheet, int? slotLevel = null, bool pact = false, string? resource = null, int amount = 1)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var given = (slotLevel is not null ? 1 : 0) + (pact ? 1 : 0) + (resource is not null ? 1 : 0);
        if (given != 1)
        {
            throw new DndInputException("give exactly one of slot_level (1-9), pact (true) or resource (its name), and amount (default 1; negative restores).");
        }

        // As a long: Math.Abs(int.MinValue) has no int and throws (as SheetXp guards its amount).
        if (amount == 0 || Math.Abs((long)amount) > SheetLimits.MaxUseAmount)
        {
            throw new DndInputException($"amount is {N(amount)}; give 1 to {N(SheetLimits.MaxUseAmount)} uses spent, or a negative number to restore.");
        }

        if (resource is not null)
        {
            var (key, entry) = FindResource(sheet, resource);
            if (!entry.IsCounted)
            {
                throw new DndInputException($"{entry.Name} is a tracker (state \"{entry.State}\"), not counted uses: change its state with update.");
            }

            var used = Spend(entry.Used ?? 0, entry.Max!.Value, amount, entry.Name);
            var after = sheet with { Resources = SheetMaps.With(sheet.Resources, key, entry with { Used = used }) };
            return Result(sheet, after, $"{entry.Name}: {Verb(amount)}, {N(entry.Max.Value - used)}/{N(entry.Max.Value)} left.");
        }

        var slotKey = pact ? SpellSlotEntry.PactKey : SlotKey(slotLevel!.Value);
        var label = pact ? "Pact Magic slots" : $"{Ordinal(slotLevel!.Value)}-level slots";
        if (!sheet.SpellSlots.TryGetValue(slotKey, out var slot) || slot.Max == 0)
        {
            throw new DndInputException(
                $"the sheet has no {label} (its slots: {SheetUpdate.SlotText(sheet.SpellSlots)}); give the slots with update first.");
        }

        var usedSlots = Spend(slot.Used, slot.Max, amount, label);
        var changed = sheet with { SpellSlots = SheetMaps.With(sheet.SpellSlots, slotKey, slot with { Used = usedSlots }) };
        return Result(sheet, changed, $"{char.ToUpperInvariant(label[0])}{label[1..]}: {Verb(amount)}, {N(slot.Max - usedSlots)}/{N(slot.Max)} left.");
    }

    /// <summary>
    /// The resource a name stands for: the slug of the name, then the name's comparison key, then a unique start of a slug.
    /// </summary>
    /// <exception cref="DndInputException">No resource, or several, match (the sheet's resource names are listed: author-only).</exception>
    public static (string Key, SheetResource Resource) FindResource(CharacterSheet sheet, string name)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var slug = SheetResource.KeyOf(name ?? string.Empty);
        if (slug.Length > 0 && sheet.Resources.TryGetValue(slug, out var exact))
        {
            return (slug, exact);
        }

        var byName = sheet.Resources.Where(p => CampaignText.Key(p.Value.Name) == CampaignText.Key(name)).ToList();
        if (byName.Count == 1)
        {
            return (byName[0].Key, byName[0].Value);
        }

        var byPrefix = slug.Length == 0 ? [] : sheet.Resources.Where(p => p.Key.StartsWith(slug, StringComparison.Ordinal)).ToList();
        if (byPrefix.Count == 1)
        {
            return (byPrefix[0].Key, byPrefix[0].Value);
        }

        var names = sheet.Resources.Count == 0 ? "none" : string.Join(", ", sheet.Resources.Values.Select(r => r.Name));
        throw new DndInputException(byPrefix.Count > 1
            ? $"resource \"{DslText.Echo(name)}\" matches several: {string.Join(", ", byPrefix.Select(p => p.Value.Name))}; give the full name."
            : $"resource \"{DslText.Echo(name)}\" is not on the sheet (its resources: {names}); add it with update.");
    }

    private static int Spend(int used, int max, int amount, string what)
    {
        var after = used + amount;
        if (after > max)
        {
            throw new DndInputException($"{what}: only {N(max - used)} of {N(max)} left; cannot spend {N(amount)}.");
        }

        if (after < 0)
        {
            throw new DndInputException($"{what}: only {N(used)} used; cannot restore {N(-amount)}.");
        }

        return after;
    }

    private static SheetResult Result(CharacterSheet before, CharacterSheet after, string note) =>
        new(after, SheetDiff.Between(before, after), [note], []);

    private static string SlotKey(int level)
    {
        if (level is < 1 or > SheetLimits.MaxSpellLevel)
        {
            throw new DndInputException($"slot_level is {N(level)}; it is 1 to 9.");
        }

        return SpellSlotEntry.KeyOf(level);
    }

    private static string Verb(int amount) => amount > 0 ? $"{N(amount)} used" : $"{N(-amount)} restored";

    internal static string Ordinal(int level) => level switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => N(level) + "th",
    };

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
