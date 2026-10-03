using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation.Archetypes;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation;

/// <summary>
/// Named party archetypes as <c>balance_simulate</c> combatants: the thin adapter between a party entry's
/// <c>archetype</c> + <c>level</c> + <c>edition</c> and <see cref="ArchetypeCatalog"/>, where the archetypes themselves
/// (twelve subclass-free classes, both editions, levels 1–20) and their rules live.
///
/// <para>
/// <b>What an expansion carries</b>: the DSL build (with the edition inside it), hit points, AC, the save proficiencies,
/// the front/back position and a short label ("Fighter"). <see cref="CombatantSpec.Saves"/> is set only where a save is
/// not modifier + proficiency — today only a paladin's own Aura of Protection from level 6 — because a
/// <see cref="SavesSpec"/> value overrides everything else for that ability. Initiative, count and death saves are left
/// to the entry and the simulator's defaults (Dex modifier; one copy; party builds make death saves).
/// </para>
/// <para>
/// <b>Overrides.</b> The simulator lays the entry's own fields over the expansion (<c>SimulationPreparation</c>):
/// <c>name</c>, <c>hp</c>, <c>ac</c>, <c>position</c>, <c>count</c>, <c>initiative_bonus</c>, <c>death_saves</c> replace the
/// archetype's; <c>saves</c> replace it per ability; <c>save_proficiencies</c> replace the archetype's proficiencies (and
/// with them any computed save such as the aura). The entry's <c>edition</c>, or else the fight's, picks the rules (with a
/// campaign active the host fills an entry's edition first; see <see cref="Expand"/>).
/// </para>
/// </summary>
public static class PartyArchetypes
{
    /// <summary>The archetype names <see cref="Expand"/> accepts, for messages and the tool description.</summary>
    public static IReadOnlyList<string> Names => ArchetypeCatalog.Names;

    /// <summary>The archetype at a level as a full combatant: a build, hit points, AC, save proficiencies and position.</summary>
    /// <param name="edition">
    /// "2014" or "2024"; null means 2024. The simulator passes the entry's edition, else the fight's. With a 2014 or 2024
    /// campaign active the host has already given an entry that names none the fight's edition, else the first party
    /// entry's, else the campaign's ruleset (the host's <c>CampaignEditionFill</c>), so null does not reach here. With none
    /// active the host fills nothing, and a first party entry's edition never reaches an archetype entry: null arrives
    /// whenever neither the entry nor the fight names an edition (even beside a party build that names 2014), and 2024 is
    /// then the same last resort the rules tools use.
    /// </param>
    /// <exception cref="DndInputException">
    /// The archetype is not one of <see cref="Names"/> (the message lists them), the level is outside 1–20, or the edition is
    /// unknown — all in one message, without an item prefix (the simulator adds "party item 2 (…): ").
    /// </exception>
    public static CombatantSpec Expand(string archetype, int level, string? edition) =>
        ToCombatant(ArchetypeCatalog.Build(archetype, level, edition));

    /// <summary>
    /// The catalogue's name for an archetype as given (" WIZARD " → "wizard"), as <see cref="Expand"/> matches it, or null
    /// when it is not one: what a report names, rather than the caller's spelling.
    /// </summary>
    public static string? Canonical(string archetype) => ArchetypeCatalog.TryMatch(archetype, out var name) ? name : null;

    /// <summary>
    /// The lines a simulation report adds to its assumptions for the archetypes in the fight: the rules they share, once,
    /// then each distinct (archetype, level, edition)'s <see cref="ArchetypeMember.Summary"/>. Empty when there are none.
    /// </summary>
    /// <param name="members">Each archetype entry as it was simulated: its name (as given), level and edition.</param>
    public static IReadOnlyList<string> ReportAssumptions(IEnumerable<(string Archetype, int Level, string Edition)> members)
    {
        var lines = new List<string>();
        var seen = new HashSet<(string, int, string)>();
        foreach (var (archetype, level, edition) in members)
        {
            if (!ArchetypeCatalog.TryMatch(archetype, out var name) || !seen.Add((name, level, edition)))
            {
                continue;
            }

            lines.Add(ArchetypeCatalog.Build(name, level, edition).Summary);
        }

        return lines.Count == 0 ? [] : [ArchetypeCatalog.SharedRules, .. lines];
    }

    /// <summary>The member as the simulator's input: what copies across, and saves only where they are not modifier + PB.</summary>
    internal static CombatantSpec ToCombatant(ArchetypeMember member)
    {
        var pb = DslLimits.ProficiencyBonus(member.Level);
        int? Differs(string ability)
        {
            var plain = DslLimits.AbilityModifier(member.Abilities.Score(ability)) + (member.SaveProficiencies.Contains(ability) ? pb : 0);
            return member.Saves[ability] == plain ? null : member.Saves[ability];
        }

        var saves = new SavesSpec
        {
            Str = Differs(V.Abilities.Str),
            Dex = Differs(V.Abilities.Dex),
            Con = Differs(V.Abilities.Con),
            Int = Differs(V.Abilities.Int),
            Wis = Differs(V.Abilities.Wis),
            Cha = Differs(V.Abilities.Cha),
        };

        return new CombatantSpec
        {
            Name = Title(member.Archetype),
            Build = member.Build,
            Level = member.Level,
            Hp = member.Hp,
            Ac = member.Ac,
            SaveProficiencies = member.SaveProficiencies,
            Saves = V.Abilities.All.Any(a => saves.Get(a) is not null) ? saves : null,
            Position = member.Position,
        };
    }

    private static string Title(string archetype) => char.ToUpperInvariant(archetype[0]) + archetype[1..];
}
