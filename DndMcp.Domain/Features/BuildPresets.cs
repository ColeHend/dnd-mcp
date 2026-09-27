using DndMcp.Domain.Core;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Domain.Features;

/// <summary>
/// Ready-made builds, written in the DSL itself (so they go through the same validation and resolution as a model's
/// build, and a test can print them).
///
/// <para>
/// <b>warlock_baseline</b> is the community "Warlock Baseline" (Form of Dread, "Which baseline should I use?", 2022; research
/// B3): Eldritch Blast with Agonizing Blast and Hex against the AC of the CR = level row, Cha 16 at levels 1–3, 18 at
/// 4–7, 20 from 8. Eldritch Blast is a ranged spell attack of 1d10 force per beam, one beam at level 1, two at 5, three at
/// 11, four at 17; Agonizing Blast adds the Cha modifier to each beam from level 2 (an invocation with a level 2
/// prerequisite in both editions); Hex adds 1d6 necrotic to every hit and needs Concentration.
/// </para>
/// <para>
/// <b>Hex has NO setup cost here, deliberately.</b> Casting Hex takes a Bonus Action, and a build that models that would
/// lose it in round 1; but the published baseline curve (6.30 at level 1 … 38.20 at 17) assumes Hex is already up on
/// every turn. The preset reproduces the published numbers, so round-1 and fight DPR are equal for it; a warlock that
/// casts Hex in the fight is <c>"setup": "bonus_action"</c> on its own build.
/// </para>
/// </summary>
public static class BuildPresets
{
    private const string WarlockBaselineJson = """
        {
          "name": "Warlock baseline",
          "level": 1,
          "abilities": { "cha": { "1": 16, "4": 18, "8": 20 } },
          "attacks": [
            {
              "name": "Eldritch Blast",
              "to_hit": { "ability": "cha", "proficient": true },
              "damage": "1d10",
              "damage_type": "force",
              "ability_to_damage": false,
              "properties": ["ranged", "spell"],
              "cantrip": "beams"
            }
          ],
          "modifiers": [
            { "kind": "bonus_damage", "name": "Agonizing Blast", "amount": "cha", "attacks": ["Eldritch Blast"], "from_level": 2 },
            { "kind": "extra_damage", "name": "Hex", "dice": "1d6", "type": "necrotic", "when": "every_hit", "concentration": true }
          ]
        }
        """;

    private static readonly Lazy<BuildSpec> WarlockBaselineTemplate = new(() => DslJson.Deserialize<BuildSpec>(WarlockBaselineJson, "preset"));

    /// <summary>The preset names, as <c>preset</c> takes them.</summary>
    public static IReadOnlyList<string> Names => V.Presets.Set.Values;

    /// <summary>
    /// The spec itself when it names no preset; otherwise the preset's build with the spec's name, edition and level (the
    /// only fields a preset spec may give; <see cref="BuildSpecValidator"/> refuses the rest).
    /// </summary>
    /// <exception cref="DndInputException">The preset is not one of <see cref="Names"/>.</exception>
    public static BuildSpec Expand(BuildSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Preset is null)
        {
            return spec;
        }

        if (!V.Presets.Set.TryMatch(spec.Preset, out var preset))
        {
            throw new DndInputException($"preset \"{DslText.Echo(spec.Preset)}\" is not a preset; presets are {V.Presets.Set.List}.");
        }

        var template = preset switch
        {
            V.Presets.WarlockBaseline => WarlockBaselineTemplate.Value,
            _ => throw new InvalidOperationException($"Preset {preset} has no template."),
        };

        return new BuildSpec
        {
            Name = spec.Name ?? template.Name,
            Edition = spec.Edition ?? template.Edition,
            Level = spec.Level ?? template.Level,
            Abilities = template.Abilities,
            ProficiencyBonus = template.ProficiencyBonus,
            FightingStyle = template.FightingStyle,
            Attacks = template.Attacks,
            Modifiers = template.Modifiers,
        };
    }

    /// <summary>
    /// The warlock baseline as a spec (for the reference curve and the <c>dpr-targets-by-level</c> table).
    /// </summary>
    public static BuildSpec WarlockBaseline(int level, string edition = V.Editions.Default) =>
        Expand(new BuildSpec { Name = "Warlock baseline", Preset = V.Presets.WarlockBaseline, Edition = edition, Level = level });
}
