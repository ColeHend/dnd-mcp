using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Characters;

/// <summary>
/// A sheet's <c>sim_profile</c>: the <see cref="BuildSpec"/> the simulator fights with instead of the class archetype
/// (contract D7). It is the build DSL only (attacks, modifiers, abilities): HP, AC, saves and initiative stay on the
/// sheet and are laid over it when the character enters a simulation (<see cref="SheetSimulation"/>), because a build has
/// none of them.
///
/// <para>
/// <b>Validated at the sheet's level, stored canonical.</b> <see cref="Prepare"/> runs the simulator's own validation
/// (<see cref="BuildResolver.Validate"/> with <see cref="BuildUse.Simulation"/>, which allows what one DPR turn cannot,
/// e.g. Fireball while slots last) at the level the simulator will resolve it at (the sheet's, not the build's own), then
/// stores <see cref="BuildCanonicalizer.Canonical"/> of it written with <see cref="DslJson.Options"/>: one spelling for
/// every vocabulary word, so the sheet, the export and two sheets' profiles compare as text, and a misspelt field is
/// refused on the way back in (<see cref="DslJson"/> refuses unknown members).
/// </para>
/// </summary>
public static class SimProfile
{
    /// <summary>What refusals call it: "Invalid sim_profile: …".</summary>
    public const string Subject = "sim_profile";

    /// <summary>
    /// Validates the profile at <paramref name="level"/> and returns its canonical JSON, with notes for the result (the
    /// build's own level differs from the sheet's).
    /// </summary>
    /// <exception cref="DndInputException">The profile does not validate at that level (every problem, up to five).</exception>
    public static PreparedSimProfile Prepare(BuildSpec spec, int level)
    {
        ArgumentNullException.ThrowIfNull(spec);
        BuildResolver.Validate(spec, [level], Subject, BuildUse.Simulation);
        var canonical = BuildCanonicalizer.Canonical(spec);
        var notes = new List<string>();
        if (spec.Level is { } own && own != level)
        {
            notes.Add($"sim_profile is written for level {own.ToString(System.Globalization.CultureInfo.InvariantCulture)}; simulations " +
                      $"resolve it at the sheet's level ({level.ToString(System.Globalization.CultureInfo.InvariantCulture)}).");
        }

        return new PreparedSimProfile(JsonSerializer.Serialize(canonical, DslJson.Options), notes);
    }

    /// <summary>The stored profile as a build (read back strictly: an unknown member is refused with where it is).</summary>
    /// <exception cref="DndInputException">The stored text is not a build.</exception>
    public static BuildSpec Read(string json) => DslJson.Deserialize<BuildSpec>(json, Subject);

    /// <summary>
    /// Why the stored profile does not work at <paramref name="level"/> (a step value that no longer covers it after a
    /// level-up), or null when it does.
    /// </summary>
    public static string? ProblemAt(string json, int level)
    {
        try
        {
            BuildResolver.Validate(Read(json), [level], Subject, BuildUse.Simulation);
            return null;
        }
        catch (DndInputException ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>A validated, canonical sim_profile ready to store, and the notes for the result.</summary>
public sealed record PreparedSimProfile(string Json, IReadOnlyList<string> Notes);
