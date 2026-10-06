using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// Spell slots by class level, as the SRD class tables give them (the 5e data's level records,
/// <c>spellcasting.spell_slots_level_1</c> … <c>_9</c>): the full casters' table (bard, cleric, druid, sorcerer, wizard;
/// identical in 2014 and 2024), the half casters' (paladin, ranger: identical from class level 2; at level 1 the 2014
/// tables have NO slots and the 2024 tables two 1st-level slots), and Pact Magic (warlock, both editions: all slots of one
/// level). A test reads both editions' level files and matches every class at every level against these rows.
///
/// <para>
/// <b>Single-class only.</b> A character with two or more spellcasting classes combines them by the Multiclass
/// Spellcaster table, which is in the multiclassing rules, and those are not in this server's data: such a sheet keeps
/// the slots it is given (contract D18). Pact Magic is always its own <c>pact</c> key.
/// </para>
/// <para>
/// The party archetypes read the same rows (<c>ArchetypeKit</c>'s <c>SpellSlots</c> forwards here), so an archetype's
/// slot-limited spells and a sheet's slots cannot disagree.
/// </para>
/// </summary>
public static class SpellSlotTables
{
    // Rows: class level 1–20; columns: slot levels 1–9.
    private static readonly int[][] FullRows =
    [
        [2], [3], [4, 2], [4, 3], [4, 3, 2], [4, 3, 3], [4, 3, 3, 1], [4, 3, 3, 2], [4, 3, 3, 3, 1], [4, 3, 3, 3, 2],
        [4, 3, 3, 3, 2, 1], [4, 3, 3, 3, 2, 1], [4, 3, 3, 3, 2, 1, 1], [4, 3, 3, 3, 2, 1, 1], [4, 3, 3, 3, 2, 1, 1, 1],
        [4, 3, 3, 3, 2, 1, 1, 1], [4, 3, 3, 3, 2, 1, 1, 1, 1], [4, 3, 3, 3, 3, 1, 1, 1, 1], [4, 3, 3, 3, 3, 2, 1, 1, 1],
        [4, 3, 3, 3, 3, 2, 2, 1, 1],
    ];

    // Paladin and ranger from class level 2 (the level 1 row is the edition's: see Half).
    private static readonly int[][] HalfRows =
    [
        [], [2], [3], [3], [4, 2], [4, 2], [4, 3], [4, 3], [4, 3, 2], [4, 3, 2],
        [4, 3, 3], [4, 3, 3], [4, 3, 3, 1], [4, 3, 3, 1], [4, 3, 3, 2], [4, 3, 3, 2], [4, 3, 3, 3, 1], [4, 3, 3, 3, 1],
        [4, 3, 3, 3, 2], [4, 3, 3, 3, 2],
    ];

    /// <summary>The 2024 half casters' level 1 row: two 1st-level slots (the 2014 row is empty).</summary>
    private static readonly int[] HalfLevelOne2024 = [2];

    /// <summary>A full caster's slots at a class level: index 0 is 1st level; trailing levels with no slots are left off.</summary>
    public static IReadOnlyList<int> Full(int classLevel)
    {
        CheckLevel(classLevel);
        return FullRows[classLevel - 1];
    }

    /// <summary>A half caster's slots at a class level (2014 level 1: none; 2024 level 1: two 1st-level slots).</summary>
    public static IReadOnlyList<int> Half(int classLevel, string edition)
    {
        CheckLevel(classLevel);
        if (classLevel == 1)
        {
            return edition == V.Editions.E2014 ? [] : HalfLevelOne2024;
        }

        return HalfRows[classLevel - 1];
    }

    /// <summary>
    /// Pact Magic at a warlock level (both editions): 1 slot of 1st at 1; 2 of 1st at 2; 2 of 2nd at 3-4; 2 of 3rd at 5-6;
    /// 2 of 4th at 7-8; 2 of 5th at 9-10; 3 of 5th at 11-16; 4 of 5th at 17-20.
    /// </summary>
    public static PactSlots Pact(int classLevel)
    {
        CheckLevel(classLevel);
        return classLevel switch
        {
            1 => new PactSlots(1, 1),
            2 => new PactSlots(1, 2),
            <= 4 => new PactSlots(2, 2),
            <= 6 => new PactSlots(3, 2),
            <= 8 => new PactSlots(4, 2),
            <= 10 => new PactSlots(5, 2),
            <= 16 => new PactSlots(5, 3),
            _ => new PactSlots(5, 4),
        };
    }

    /// <summary>
    /// The slots one class gives at a class level: per spell level for a full or half caster, the <c>pact</c> slots for a
    /// warlock, nothing for a class that does not cast.
    /// </summary>
    /// <param name="casterKind"><see cref="SheetValues.CasterKinds"/>.</param>
    public static SlotRow For(string casterKind, int classLevel, string edition) => casterKind switch
    {
        SheetValues.CasterKinds.Full => new SlotRow(Full(classLevel), null),
        SheetValues.CasterKinds.Half => new SlotRow(Half(classLevel, edition), null),
        SheetValues.CasterKinds.Pact => new SlotRow([], Pact(classLevel)),
        SheetValues.CasterKinds.None => SlotRow.None,
        _ => throw new ArgumentOutOfRangeException(nameof(casterKind), casterKind, "Not a caster kind."),
    };

    private static void CheckLevel(int classLevel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(classLevel, DslLimits.MinLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(classLevel, DslLimits.MaxLevel);
    }
}

/// <summary>A class's slots at one class level: per spell level (index 0 = 1st) and/or Pact Magic.</summary>
public sealed record SlotRow(IReadOnlyList<int> Slots, PactSlots? Pact)
{
    public static SlotRow None { get; } = new([], null);

    /// <summary>The slots of one spell level (0 when the row has none).</summary>
    public int At(int spellLevel) => spellLevel >= 1 && spellLevel <= Slots.Count ? Slots[spellLevel - 1] : 0;
}

/// <summary>Pact Magic: every slot is of <see cref="Level"/>; <see cref="Count"/> of them.</summary>
public sealed record PactSlots(int Level, int Count);
