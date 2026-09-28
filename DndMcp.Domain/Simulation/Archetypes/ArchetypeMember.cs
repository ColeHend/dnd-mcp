using DndMcp.Domain.Features;

namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// One party archetype at one level and edition: everything a simulated party member needs besides its label — the
/// DSL build (resolved by the simulator at <see cref="Level"/> with <c>BuildUse.Simulation</c>), hit points, AC, saving
/// throws and front/back position — plus the assumptions that produced them, in words a result can echo.
///
/// <para>
/// <b>Why a record of its own rather than the simulator's <c>CombatantSpec</c>:</b> the archetypes are data about the
/// rules (what a subclass-free level 7 cleric is), the combatant spec is the tool's input shape. Keeping them apart lets
/// the catalogue be tested without the simulator and lets the adapter (<c>PartyArchetypes.Expand</c>) stay a thin
/// mapping: Build, Hp, Ac, SaveProficiencies and Position copy across as they are.
/// </para>
/// <para>
/// <b>What breaks if these drift apart:</b> <see cref="Abilities"/> must equal what <see cref="Build"/> resolves to at
/// <see cref="Level"/> (HP, AC and <see cref="Saves"/> are computed from them); the catalogue tests pin that for every
/// archetype, level and edition.
/// </para>
/// </summary>
public sealed record ArchetypeMember
{
    /// <summary>The archetype's name as <see cref="ArchetypeCatalog.Names"/> lists it ("fighter").</summary>
    public required string Archetype { get; init; }

    /// <summary><see cref="DslValues.Editions"/>: "2014" or "2024".</summary>
    public required string Edition { get; init; }

    /// <summary>Character level 1–20 (also <see cref="Build"/>'s level).</summary>
    public required int Level { get; init; }

    /// <summary>The DSL build, with step values for everything that scales; valid for <c>BuildUse.Simulation</c> at 1–20.</summary>
    public required BuildSpec Build { get; init; }

    /// <summary>Ability scores at <see cref="Level"/>, as the build resolves them.</summary>
    public required ResolvedAbilities Abilities { get; init; }

    /// <summary>Hit point maximum: the hit die's maximum at level 1, the average after, plus the Con modifier per level.</summary>
    public required int Hp { get; init; }

    /// <summary>Armour Class by the archetype's armour plan (<see cref="AcSource"/> says what it is).</summary>
    public required int Ac { get; init; }

    /// <summary>"splint + shield", "Unarmored Defense", "Mage Armor": where <see cref="Ac"/> comes from.</summary>
    public required string AcSource { get; init; }

    /// <summary>
    /// Ability keys of the saving throws the character is proficient in at this level (the class's two, plus Slippery
    /// Mind and the monks' level 14 feature), in str … cha order: a save is the modifier plus the proficiency bonus.
    /// </summary>
    public required IReadOnlyList<string> SaveProficiencies { get; init; }

    /// <summary>
    /// All six saving throw totals: modifier, plus the proficiency bonus when proficient, plus anything else that always
    /// applies (a paladin's own Aura of Protection from level 6). Equal to what <see cref="SaveProficiencies"/> gives
    /// except where such a bonus exists, so an adapter can pass these as per-ability overrides.
    /// </summary>
    public required IReadOnlyDictionary<string, int> Saves { get; init; }

    /// <summary>"front" (melee, or armoured to hold the line) or "back" (ranged and spellcasting).</summary>
    public required string Position { get; init; }

    /// <summary>
    /// The choices behind the numbers, one short sentence each: the routine, the ability plan, the HP rule, the armour,
    /// what the class has that the DSL models and what it leaves out. Results echo them.
    /// </summary>
    public required IReadOnlyList<string> Assumptions { get; init; }

    /// <summary>
    /// The class-specific part of <see cref="Assumptions"/> on one line — the routine and what is modelled or left out —
    /// for a simulation report, which states the rules every archetype shares once (<see cref="ArchetypeCatalog.SharedRules"/>)
    /// and shows HP and AC in its own table.
    /// </summary>
    public required string Summary { get; init; }
}
