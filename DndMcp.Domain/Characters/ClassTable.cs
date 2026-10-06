using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// The 12 SRD classes as the sheet needs them: the class index, its display name, its hit die, its two saving throw
/// proficiencies and its kind of spellcasting. The 5e data's class records (<c>5e-SRD-Classes.json</c>
/// <c>hit_die</c>, <c>saving_throws</c>, <c>spellcasting</c>) give the same values in both editions; a test reads both
/// files and pins every one, so this table cannot drift from the data it copies.
///
/// <para>
/// <b>Why a table in the Domain and not a lookup in srd.db:</b> the sheet rules (max HP derivation, Hit Dice, the
/// starting class's saves, slot derivation) are pure Domain functions with no I/O, and they run inside the Repository's
/// write transaction. Twelve rows that never change are cheaper to copy and pin than to thread a reader through.
/// </para>
/// <para>
/// Anything else (artificer, a homebrew "Dragon Slayer") is a non-SRD class: the sheet stores its name as given and needs
/// its hit die; nothing about it is derived.
/// </para>
/// </summary>
public static class ClassTable
{
    /// <summary>The 12 classes in the 5e data's order (alphabetical).</summary>
    public static IReadOnlyList<ClassInfo> All { get; } =
    [
        new("barbarian", "Barbarian", 12, [V.Abilities.Str, V.Abilities.Con], SheetValues.CasterKinds.None),
        new("bard", "Bard", 8, [V.Abilities.Dex, V.Abilities.Cha], SheetValues.CasterKinds.Full),
        new("cleric", "Cleric", 8, [V.Abilities.Wis, V.Abilities.Cha], SheetValues.CasterKinds.Full),
        new("druid", "Druid", 8, [V.Abilities.Int, V.Abilities.Wis], SheetValues.CasterKinds.Full),
        new("fighter", "Fighter", 10, [V.Abilities.Str, V.Abilities.Con], SheetValues.CasterKinds.None),
        new("monk", "Monk", 8, [V.Abilities.Str, V.Abilities.Dex], SheetValues.CasterKinds.None),
        new("paladin", "Paladin", 10, [V.Abilities.Wis, V.Abilities.Cha], SheetValues.CasterKinds.Half),
        new("ranger", "Ranger", 10, [V.Abilities.Str, V.Abilities.Dex], SheetValues.CasterKinds.Half),
        new("rogue", "Rogue", 8, [V.Abilities.Dex, V.Abilities.Int], SheetValues.CasterKinds.None),
        new("sorcerer", "Sorcerer", 6, [V.Abilities.Con, V.Abilities.Cha], SheetValues.CasterKinds.Full),
        new("warlock", "Warlock", 8, [V.Abilities.Wis, V.Abilities.Cha], SheetValues.CasterKinds.Pact),
        new("wizard", "Wizard", 6, [V.Abilities.Int, V.Abilities.Wis], SheetValues.CasterKinds.Full),
    ];

    /// <summary>The class indexes, matched forgivingly ("Wizard", " WIZARD ").</summary>
    public static readonly DslValueSet Set = new("class", All.Select(c => c.Index).ToList());

    private static readonly IReadOnlyDictionary<string, ClassInfo> ByIndex = All.ToDictionary(c => c.Index, StringComparer.Ordinal);

    /// <summary>The SRD class a name stands for (case, spaces, hyphens and underscores ignored), if any.</summary>
    public static ClassInfo? Find(string? name) => Set.TryMatch(name, out var index) ? ByIndex[index] : null;

    /// <summary>"barbarian, bard, …, wizard".</summary>
    public static string List => Set.List;
}

/// <summary>One SRD class (the same in 2014 and 2024).</summary>
/// <param name="Index">The 5e data's index, stored in <c>classes[].class</c>: "wizard".</param>
/// <param name="Name">"Wizard": how a view names it.</param>
/// <param name="HitDie">6, 8, 10 or 12.</param>
/// <param name="Saves">The two saving throw proficiencies the class gives as a STARTING class.</param>
/// <param name="CasterKind"><see cref="SheetValues.CasterKinds"/>.</param>
public sealed record ClassInfo(string Index, string Name, int HitDie, IReadOnlyList<string> Saves, string CasterKind)
{
    public bool IsCaster => CasterKind != SheetValues.CasterKinds.None;
}
