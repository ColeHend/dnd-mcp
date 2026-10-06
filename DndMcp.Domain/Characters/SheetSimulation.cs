using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Domain.Simulation.Archetypes;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Characters;

/// <summary>
/// A sheet as one <c>balance_simulate</c> party entry (contract D7): what <c>{"character": "character:belmakor"}</c>, an
/// encounter's sheet-seeded combatants and <c>party: "campaign"</c> simulations turn into.
///
/// <para>
/// <b>With a sim_profile:</b> a <c>build</c> entry: the profile, resolved at the sheet's level, with the sheet's effective
/// maximum as <c>hp</c>, its <c>ac</c>, saving throw proficiencies and initiative bonus. A build has no HP, AC or saves of
/// its own, so a sheet without a level, AC or maximum HP is refused. A profile that names no edition takes the sheet's
/// (else the campaign's), as the host fills a build's edition.
/// </para>
/// <para>
/// <b>Without:</b> an <c>archetype</c> entry: the archetype of the class with the most levels (ties: the first listed) at
/// the TOTAL character level, in the sheet's ruleset (else the campaign's), with the sheet's effective maximum, AC, save
/// proficiencies and initiative bonus laid over it where the sheet has them (the simulator's overlay rules apply). No
/// <c>effective_level_offset</c>. A sheet with no classes, or whose main class has no archetype (artificer, homebrew), is
/// refused, naming the character and the fix ("give the sheet a sim_profile or leave X out").
/// </para>
/// <para>
/// <c>sheet.ac</c> is the BASE AC (no Bladesong, no Shield), so a profile's <c>ac</c> modifier adds to it without counting
/// twice. A dead sheet (<see cref="CharacterSheet.IsDead"/>: three failed death saves, exhaustion 6 or an effective
/// maximum of 0) is refused by name ("X is dead"), first: the roster may not have caught up with the death yet (the
/// status proposal is the author's to apply), and a dead character must not fight at full hit points; the encounter form
/// leaves it out with a note. The assumption lines say which members used an archetype and how a multiclass was read,
/// for the report.
/// </para>
/// <para>
/// <b>Fields a call gives beside the character</b> (<c>balance_simulate</c>'s <c>{"character": …, "level": 5, "hp": 20}</c>)
/// replace the sheet's: the call's word wins, as everywhere else. The assumption line then describes the entry AS
/// SIMULATED, naming each such field as the call gave it ("with the call's level 5, HP 20; the sheet's AC 12") and never
/// the sheet's value it replaced: a line that said "the sheet's HP 74" above a table row of HP 20 would contradict the
/// report it explains.
/// </para>
/// </summary>
public static class SheetSimulation
{
    /// <summary>The entry for a character's sheet, and the assumption lines that go with it.</summary>
    /// <param name="name">The character's name as the report should print it (the caller's view of it).</param>
    /// <param name="edition">The campaign's ruleset, used when the sheet names none.</param>
    /// <param name="given">
    /// The call's own entry beside the character, or null: its <c>level</c>, <c>hp</c>, <c>ac</c>,
    /// <c>save_proficiencies</c>, <c>saves</c>, <c>initiative_bonus</c>, <c>position</c> and <c>death_saves</c>, where given,
    /// replace the sheet's (class summary). Its other fields are not read here.
    /// </param>
    /// <exception cref="DndInputException">The sheet cannot be simulated: the message names the character and the fix.</exception>
    public static SheetSimulationEntry Entry(CharacterSheet sheet, string name, string edition, CombatantSpec? given = null)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        SheetUpdate.CheckEdition(edition);
        var ruleset = sheet.EditionOr(edition);
        var hp = sheet.EffectiveMaxHp(ruleset);
        if (hp == 0)
        {
            throw new DndInputException($"{name} has a hit point maximum of 0 (dead): leave {name} out.");
        }

        if (sheet.IsDead(ruleset))
        {
            var cause = sheet.DeathSaves.Failures >= 3 ? "three failed death saves" : "exhaustion 6";
            throw new DndInputException($"{name} is dead ({cause}): leave {name} out.");
        }

        var abilities = sheet.Saves.Abilities;
        var proficiencies = abilities.Count > 0 ? abilities : null;
        var overlays = Overlays(sheet, hp, given);

        if (sheet.SimProfile is { } stored)
        {
            var missing = new[]
                {
                    ("level", sheet.Level is null && given?.Level is null), ("ac", sheet.Ac is null && given?.Ac is null),
                    ("max_hp", sheet.MaxHp is null && given?.Hp is null),
                }
                .Where(m => m.Item2).Select(m => m.Item1).ToList();
            if (missing.Count > 0)
            {
                throw new DndInputException(
                    $"{name}'s sheet has a sim_profile but no {string.Join(" or ", missing)}: a build needs the sheet's level, AC and " +
                    $"hit points; give {string.Join(", ", missing)} with campaign_character update, or leave {name} out.");
            }

            var build = SimProfile.Read(stored);
            build = build with { Edition = build.Edition ?? ruleset };
            var entry = Overlay(new CombatantSpec
            {
                Name = name,
                Build = build,
                Level = sheet.Level,
                Hp = hp,
                Ac = sheet.Ac,
                SaveProficiencies = proficiencies,
                InitiativeBonus = sheet.InitiativeBonus,
            }, given);
            return new SheetSimulationEntry(
                entry,
                [$"{name}: the sheet's sim_profile (\"{build.Name}\") at level {N(entry.Level!.Value)}, with {overlays}."]);
        }

        if (sheet.Classes.Count == 0)
        {
            throw new DndInputException(
                $"{name}'s sheet has no classes{(sheet.Level is null ? " and no level" : $" (only level {N(sheet.Level.Value)})")}, so there is " +
                $"no archetype to simulate: give the sheet a sim_profile (and classes) or leave {name} out.");
        }

        var main = sheet.MainClass!;
        if (main.Srd is not { } srd || !ArchetypeCatalog.TryMatch(srd.Index, out var archetype))
        {
            throw new DndInputException(
                $"{name} ({sheet.ClassSummary}) has no sim_profile and {main.Class} is not a party archetype ({ArchetypeCatalog.NamesList}): " +
                $"give the sheet a sim_profile or leave {name} out.");
        }

        var level = given?.Level ?? sheet.Level ?? sheet.Classes.Sum(c => c.Level);
        var archetypeEntry = Overlay(new CombatantSpec
        {
            Name = name,
            Archetype = archetype,
            Level = level,
            Edition = ruleset,
            Hp = hp,
            Ac = sheet.Ac,
            SaveProficiencies = proficiencies,
            InitiativeBonus = sheet.InitiativeBonus,
        }, given);

        var multiclass = sheet.Classes.Count > 1
            ? $" (multiclass {sheet.ClassSummary}: the class with the most levels{(sheet.Classes.Count(c => c.Level == main.Level) > 1 ? ", the first listed on a tie" : string.Empty)}{(given?.Level is null ? ", at the total level" : string.Empty)})"
            : string.Empty;
        return new SheetSimulationEntry(
            archetypeEntry,
            [
                $"{name}: no sim_profile, simulated as the level {N(level)} {archetype} archetype ({ruleset}){multiclass}, with " +
                $"{overlays}; its attacks follow the archetype's ability plan (store a sim_profile for the character's own).",
            ]);
    }

    // The call's fields laid over the sheet's entry (Entry's given): each one given replaces the sheet's.
    private static CombatantSpec Overlay(CombatantSpec entry, CombatantSpec? given) => given is null
        ? entry
        : entry with
        {
            Level = given.Level ?? entry.Level,
            Hp = given.Hp ?? entry.Hp,
            Ac = given.Ac ?? entry.Ac,
            SaveProficiencies = given.SaveProficiencies ?? entry.SaveProficiencies,
            Saves = given.Saves ?? entry.Saves,
            InitiativeBonus = given.InitiativeBonus ?? entry.InitiativeBonus,
            Position = given.Position ?? entry.Position,
            DeathSaves = given.DeathSaves ?? entry.DeathSaves,
        };

    // "the sheet's HP 74, AC 12", "the call's level 5, HP 20; the sheet's AC 12": each number as the entry has it, said
    // to be the call's when the call gave it (never the sheet's value it replaced).
    private static string Overlays(CharacterSheet sheet, int? hp, CombatantSpec? given)
    {
        var call = new List<string>();
        var parts = new List<string>();
        if (given?.Level is { } level)
        {
            call.Add($"level {N(level)}");
        }

        Either(given?.Hp, hp, h => $"HP {N(h)}");
        Either(given?.Ac, sheet.Ac, ac => $"AC {N(ac)}");
        if (given?.SaveProficiencies is { } named)
        {
            call.Add(named.Count == 0 ? "no save proficiencies" : $"save proficiencies {string.Join(", ", named.Select(Ability))}");
        }
        else if (sheet.Saves.Abilities is { Count: > 0 } abilities)
        {
            parts.Add($"save proficiencies {string.Join(", ", abilities.Select(V.Abilities.Display))}");
        }

        if (given?.Saves is { } saves && Saves(saves) is { } savesText)
        {
            call.Add($"saves {savesText}");
        }

        Either(given?.InitiativeBonus, sheet.InitiativeBonus, init => $"initiative {Signed(init)}");
        if (given?.Position is { } position && !string.IsNullOrWhiteSpace(position))
        {
            call.Add($"position {position.Trim()}");
        }

        if (given?.DeathSaves is { } deathSaves)
        {
            call.Add(deathSaves ? "death saves" : "no death saves");
        }

        var said = new List<string>();
        if (call.Count > 0)
        {
            said.Add("the call's " + string.Join(", ", call));
        }

        if (parts.Count > 0)
        {
            said.Add("the sheet's " + string.Join(", ", parts));
        }

        return said.Count == 0 ? "the archetype's own numbers" : string.Join("; ", said);

        void Either(int? fromCall, int? fromSheet, Func<int, string> text)
        {
            if (fromCall is { } c)
            {
                call.Add(text(c));
            }
            else if (fromSheet is { } s)
            {
                parts.Add(text(s));
            }
        }
    }

    // "Str +5, Wis +4": the save bonuses a call sets, in ability order; null when it sets none.
    private static string? Saves(SavesSpec saves)
    {
        var set = new (string Ability, int? Bonus)[]
            {
                ("str", saves.Str), ("dex", saves.Dex), ("con", saves.Con), ("int", saves.Int), ("wis", saves.Wis), ("cha", saves.Cha),
            }
            .Where(s => s.Bonus is not null)
            .Select(s => $"{V.Abilities.Display(s.Ability)} {Signed(s.Bonus!.Value)}")
            .ToList();
        return set.Count == 0 ? null : string.Join(", ", set);
    }

    // An ability as the call gave it, by its short name when it is one (the simulator refuses one that is not, later).
    private static string Ability(string? given) =>
        given is not null && V.Abilities.Set.TryMatch(given, out var canonical) ? V.Abilities.Display(canonical) : given?.Trim() ?? string.Empty;

    private static string Signed(int value) => $"{(value >= 0 ? "+" : string.Empty)}{N(value)}";

    private static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A sheet's simulation entry and the assumption lines a report prints for it.</summary>
public sealed record SheetSimulationEntry(CombatantSpec Entry, IReadOnlyList<string> Assumptions);
