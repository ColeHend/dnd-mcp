using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Simulation.Archetypes;

/// <summary>
/// The named party archetypes: twelve subclass-free classes in both editions at levels 1–20, so "simulate this fight
/// for a level 7 party" does not start with the model writing four builds (contract §7, §10).
///
/// <para>
/// <b>What an archetype is.</b> One honest, simple build per class and edition: the class's signature damage routine,
/// one limited area or big spell for full casters, a heal for healers, and the defensive features the DSL can say
/// (Rage's resistances), with step values for everything that changes by level. The shared rules are stated once and
/// hold for every class: the standard array with +2/+1 and the ASIs in a fixed order (<see cref="AbilityTrack"/>), hit
/// points from the hit die's maximum then its average plus Con (<see cref="ArchetypeHitPoints"/>), armour by the DMG's
/// wealth tiers (<see cref="ArmorPlan"/>), a +1 weapon from 11 (<see cref="ArchetypeParts"/>). No subclasses, feats
/// (2024 origin feats included), species traits or other magic items. Each class's own choices are on its definition
/// (<see cref="MartialArchetypes"/>, <see cref="CasterArchetypes"/>) and in every member's
/// <see cref="ArchetypeMember.Assumptions"/>, so a result can say what it simulated.
/// </para>
/// <para>
/// <b>Validity is a test, not a hope:</b> every archetype resolves with <c>BuildUse.Simulation</c> at every level in both
/// editions (the catalogue tests run all 480). A change to the DSL that breaks one fails there, not in a user's fight.
/// </para>
/// </summary>
public static class ArchetypeCatalog
{
    /// <summary>The archetype names, as a party entry's <c>archetype</c> takes them, in the order messages list them.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["fighter", "barbarian", "paladin", "ranger", "rogue", "monk", "cleric", "druid", "wizard", "sorcerer", "warlock", "bard"];

    private static readonly DslValueSet Set = new("archetype", Names);

    private static readonly Lazy<IReadOnlyDictionary<(string Name, string Edition), ArchetypeDefinition>> Definitions = new(CreateDefinitions);

    /// <summary>"fighter, barbarian, …, bard": the names as a message or a tool description lists them.</summary>
    public static string NamesList => Set.List;

    /// <summary>
    /// The rules every archetype shares, in one line: what a report states once beside each member's
    /// <see cref="ArchetypeMember.Summary"/>.
    /// </summary>
    public const string SharedRules =
        "Party archetypes: subclass-free class builds; standard array with +2 to the primary ability and +1 to the second, " +
        "ASIs in a fixed order (+1/+1, primary to 20, then the second); HP = the hit die's maximum at level 1, then its " +
        "average, plus Con each level; armour: starting kit at 1-4, the best up to 500 gp at 5-10, any mundane armour from " +
        "11; no feats, species traits or magic items except a +1 weapon from 11; spells at their own level (no upcasting).";

    /// <summary>An example a refusal gives.</summary>
    public const string Example = "{\"archetype\": \"cleric\", \"level\": 5, \"edition\": \"2024\"}";

    /// <summary>The canonical archetype name <paramref name="text"/> stands for (case, spaces, hyphens ignored), if any.</summary>
    public static bool TryMatch(string? text, out string name)
    {
        var matched = Set.TryMatch(text, out var canonical);
        name = canonical ?? string.Empty;
        return matched;
    }

    /// <summary>
    /// The refusal for a name that is not an archetype: names it and lists every archetype with an example. It has no
    /// item prefix, so a caller can add its own ("party item 2: …").
    /// </summary>
    public static string Refusal(string? archetype) =>
        string.IsNullOrWhiteSpace(archetype)
            ? $"archetype is empty; archetypes are {NamesList} (subclass-free, levels 1-20, editions 2014 and 2024), e.g. {Example}."
            : $"archetype \"{DslText.Echo(archetype)}\" is not a party archetype; archetypes are {NamesList} (subclass-free, " +
              $"levels 1-20, editions 2014 and 2024), e.g. {Example}.";

    /// <summary>
    /// The archetype at a level and edition. Every problem (unknown name, missing or out-of-range level, unknown edition)
    /// is reported in one <see cref="DndInputException"/>.
    /// </summary>
    /// <param name="archetype">One of <see cref="Names"/>; case, spaces and hyphens are ignored.</param>
    /// <param name="level">Character level 1–20 (required).</param>
    /// <param name="edition">"2014" or "2024"; null means <see cref="DslValues.Editions.Default"/> (2024).</param>
    public static ArchetypeMember Build(string? archetype, int? level, string? edition = null)
    {
        var problems = new List<string>();
        if (!TryMatch(archetype, out var name))
        {
            problems.Add(Refusal(archetype));
        }

        if (level is null)
        {
            problems.Add($"level is required with an archetype: a character level {DslLimits.MinLevel}-{DslLimits.MaxLevel}, e.g. {Example}.");
        }
        else if (level is < DslLimits.MinLevel or > DslLimits.MaxLevel)
        {
            problems.Add($"level {level.Value.ToString(CultureInfo.InvariantCulture)} is not a character level; archetypes exist at levels " +
                         $"{DslLimits.MinLevel}-{DslLimits.MaxLevel}.");
        }

        var canonicalEdition = V.Editions.Default;
        if (edition is not null && !V.Editions.Set.TryMatch(edition, out canonicalEdition))
        {
            // Not "(default "2024")": left out, an entry's edition is the fight's; with a 2014 or 2024 campaign active the host
            // fills the rest before this runs, from the first party entry's edition, else the campaign's ruleset
            // (CampaignEditionFill); 2024 is only what is left when none of those says. The order is balance_simulate's.
            problems.Add($"edition \"{DslText.Echo(edition)}\" is not an edition; give \"2014\" or \"2024\" (left out: the fight's edition; " +
                         "else, with a 2014 or 2024 campaign active, the first party entry's, else the campaign's; else \"2024\").");
            canonicalEdition = V.Editions.Default;
        }

        if (problems.Count > 0)
        {
            throw new DndInputException(string.Join(" ", problems));
        }

        return Member(Definitions.Value[(name, canonicalEdition!)], level!.Value);
    }

    /// <summary>The member a definition makes at a level: the build at that level plus the derived HP, AC and saves.</summary>
    internal static ArchetypeMember Member(ArchetypeDefinition definition, int level)
    {
        var abilities = definition.Abilities;
        var con = abilities.Modifier(V.Abilities.Con, level);
        var hp = ArchetypeHitPoints.At(definition.HitDie, level, con);
        var (ac, acSource) = definition.Armor.At(level, abilities);
        var proficient = definition.SavesAt(level);
        var pb = DslLimits.ProficiencyBonus(level);
        var saveBonus = definition.SaveBonus?.Invoke(level, abilities) ?? 0;
        var saves = V.Abilities.All.ToDictionary(
            a => a, a => abilities.Modifier(a, level) + (proficient.Contains(a) ? pb : 0) + saveBonus);

        var build = new BuildSpec
        {
            Name = $"{definition.Title} archetype ({definition.Edition})",
            Edition = definition.Edition,
            Level = level,
            Abilities = definition.AbilitiesSpec,
            FightingStyle = definition.FightingStyle,
            Attacks = definition.Attacks,
            Modifiers = definition.Modifiers,
        };

        var average = definition.HitDie / 2 + 1;
        return new ArchetypeMember
        {
            Archetype = definition.Name,
            Edition = definition.Edition,
            Level = level,
            Build = build,
            Abilities = abilities.At(level),
            Hp = hp,
            Ac = ac,
            AcSource = acSource,
            SaveProficiencies = proficient,
            Saves = saves,
            Position = definition.Position,
            Assumptions =
            [
                $"{definition.Title} ({definition.Edition}) level {Number(level)}, no subclass: {definition.Routine}.",
                $"Abilities {abilities.Describe(level)}: {abilities.Plan}.",
                $"HP {Number(hp)}: d{Number(definition.HitDie)} maximum at level 1, {Number(average)} per later level, Con " +
                $"{Signed(con)} per level.",
                $"AC {Number(ac)} ({acSource}): starting armour at 1-4, the best trained armour up to 500 gp at 5-10, any mundane " +
                "armour from 11; no magic armour.",
                .. definition.Notes,
                "No feats, species traits or magic items except a +1 weapon from level 11 for weapon users.",
            ],
            Summary = $"{definition.Title} ({definition.Edition}) level {Number(level)}: {definition.Routine}. " +
                      string.Join(" ", definition.Notes.Where(n => n != ArchetypeParts.NoUpcasting)),
        };
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Signed(int value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<(string, string), ArchetypeDefinition> CreateDefinitions()
    {
        var byName = new Dictionary<string, Func<string, ArchetypeDefinition>>
        {
            ["fighter"] = MartialArchetypes.Fighter,
            ["barbarian"] = MartialArchetypes.Barbarian,
            ["paladin"] = MartialArchetypes.Paladin,
            ["ranger"] = MartialArchetypes.Ranger,
            ["rogue"] = MartialArchetypes.Rogue,
            ["monk"] = MartialArchetypes.Monk,
            ["cleric"] = CasterArchetypes.Cleric,
            ["druid"] = CasterArchetypes.Druid,
            ["wizard"] = CasterArchetypes.Wizard,
            ["sorcerer"] = CasterArchetypes.Sorcerer,
            ["warlock"] = CasterArchetypes.Warlock,
            ["bard"] = CasterArchetypes.Bard,
        };

        var definitions = new Dictionary<(string, string), ArchetypeDefinition>();
        foreach (var name in Names)
        {
            foreach (var edition in V.Editions.Set.Values)
            {
                var definition = byName[name](edition);
                if (definition.Name != name || definition.Edition != edition)
                {
                    throw new InvalidOperationException($"Archetype {name} ({edition}) is defined as {definition.Name} ({definition.Edition}).");
                }

                definitions.Add((name, edition), definition);
            }
        }

        return definitions;
    }
}
