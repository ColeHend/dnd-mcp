using DndMcp.Domain.Features;
using C = DndMcp.Domain.Simulation.StatBlockValues.Conditions;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The 15 SRD conditions as the tracker names and explains them (contract §6.3, §6.4, §6.8: T owns the
/// <c>condition_effects</c> and <c>unconscious_crit</c> texts per edition). The texts are the condition rules quoted
/// short from the SRD (2014: SRD 5.1 Conditions; 2024: SRD 5.2.1 Rules Glossary, research RULES §4 and §11), reminders
/// only: the tracker never decides an attack or a save it does not resolve.
/// </summary>
public static class CombatConditions
{
    /// <summary>The 15 condition names, canonical (lower case), matched forgivingly.</summary>
    public static readonly DslValueSet Set = new("condition", C.All);

    /// <summary>The conditions whose effects depend on their source (the charmer, the source of fear, the grappler).</summary>
    public static readonly IReadOnlyList<string> SourceDependent = [C.Charmed, C.Frightened, C.Grappled];

    /// <summary>
    /// The conditions that change the bearer's own attack rolls (§6.4's actor line): blinded, frightened, poisoned, prone,
    /// restrained, and 2024 grappled (Disadvantage against anyone but the grappler).
    /// </summary>
    public static IReadOnlyList<string> AttackAffecting(string edition) =>
        edition == DslValues.Editions.E2024
            ? [C.Blinded, C.Frightened, C.Grappled, C.Poisoned, C.Prone, C.Restrained]
            : [C.Blinded, C.Frightened, C.Poisoned, C.Prone, C.Restrained];

    /// <summary>Whether <paramref name="name"/> is an SRD condition (exactly a canonical name, case ignored).</summary>
    public static bool IsSrd(string name) => C.All.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical SRD condition <paramref name="text"/> names ("Frightened", "frightened "), if any.</summary>
    public static bool TryMatch(string? text, out string canonical)
    {
        if (Set.TryMatch(text, out var match))
        {
            canonical = match;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>
    /// What the condition does, per edition, as one clause list ("disadvantage on attack rolls and ability checks while
    /// its source is in sight; can't willingly move closer to it"). Empty for a named effect.
    /// </summary>
    public static string Effects(string condition, string edition)
    {
        var is2024 = edition == DslValues.Editions.E2024;
        return condition.ToLowerInvariant() switch
        {
            C.Blinded => is2024
                ? "can't see; attack rolls against it have Advantage, its attack rolls have Disadvantage"
                : "can't see; attack rolls against it have advantage, its attack rolls have disadvantage",
            C.Charmed => is2024
                ? "can't attack its charmer or target it with damaging abilities or magical effects; the charmer has Advantage on ability checks to interact with it socially"
                : "can't attack its charmer or target it with harmful abilities or magical effects; the charmer has advantage on social checks with it",
            C.Deafened => is2024 ? "can't hear; automatically fails ability checks that require hearing" : "can't hear; automatically fails checks that require hearing",
            C.Frightened => is2024
                ? "Disadvantage on attack rolls and ability checks while its source is in sight; can't willingly move closer to it"
                : "disadvantage on attack rolls and ability checks while its source is in sight; can't willingly move closer to it",
            C.Grappled => is2024
                ? "Speed 0; Disadvantage on attacks against targets other than the grappler; the grappler can drag it"
                : "speed 0; ends if the grappler is incapacitated or it is moved out of the grappler's reach",
            C.Incapacitated => is2024
                ? "can't take any action, Bonus Action or Reaction; can't speak; its Concentration is broken"
                : "can't take actions or reactions",
            C.Invisible => is2024
                ? "attack rolls against it have Disadvantage, its attack rolls have Advantage; Advantage on Initiative"
                : "attack rolls against it have disadvantage, its attack rolls have advantage",
            C.Paralyzed => is2024
                ? "Incapacitated; Speed 0; automatically fails Strength and Dexterity saving throws; attack rolls against it have Advantage; a hit from within 5 ft is a Critical Hit"
                : "incapacitated, can't move or speak; automatically fails Strength and Dexterity saving throws; attack rolls against it have advantage; a hit from within 5 ft is a critical hit",
            C.Petrified => is2024
                ? "Incapacitated; Speed 0; attack rolls against it have Advantage; automatically fails Strength and Dexterity saving throws; Resistance to all damage; Immunity to the Poisoned condition"
                : "incapacitated, can't move or speak; attack rolls against it have advantage; automatically fails Strength and Dexterity saving throws; resistance to all damage; immune to poison and disease",
            C.Poisoned => is2024 ? "Disadvantage on attack rolls and ability checks" : "disadvantage on attack rolls and ability checks",
            C.Prone => is2024
                ? "its attack rolls have Disadvantage; attack rolls against it have Advantage within 5 ft, Disadvantage beyond; standing costs half its Speed"
                : "its attack rolls have disadvantage; attack rolls against it have advantage within 5 ft, disadvantage beyond; standing costs half its speed",
            C.Restrained => is2024
                ? "Speed 0; attack rolls against it have Advantage, its attack rolls have Disadvantage; Disadvantage on Dexterity saving throws"
                : "speed 0; attack rolls against it have advantage, its attack rolls have disadvantage; disadvantage on Dexterity saving throws",
            C.Stunned => is2024
                ? "Incapacitated; automatically fails Strength and Dexterity saving throws; attack rolls against it have Advantage"
                : "incapacitated, can't move, speaks falteringly; automatically fails Strength and Dexterity saving throws; attack rolls against it have advantage",
            C.Unconscious => is2024
                ? "Incapacitated and Prone; Speed 0; unaware; automatically fails Strength and Dexterity saving throws; attack rolls against it have Advantage; a hit from within 5 ft is a Critical Hit; it stays Prone when this ends"
                : "incapacitated, can't move or speak, unaware; falls prone; automatically fails Strength and Dexterity saving throws; attack rolls against it have advantage; a hit from within 5 ft is a critical hit",
            C.Exhaustion => string.Empty,
            _ => string.Empty,
        };
    }

    /// <summary>The <c>unconscious_crit</c> line (Paralyzed and Unconscious, both editions).</summary>
    public static string UnconsciousCrit(string edition) =>
        edition == DslValues.Editions.E2024 ? "a hit from within 5 ft is a Critical Hit" : "a hit from within 5 ft is a critical hit";

    /// <summary>Whether a hit against a creature with this condition from within 5 ft is a critical hit.</summary>
    public static bool MakesCrits(string condition) =>
        string.Equals(condition, C.Paralyzed, StringComparison.OrdinalIgnoreCase) || string.Equals(condition, C.Unconscious, StringComparison.OrdinalIgnoreCase);
}
