using System.Text.RegularExpressions;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// Reads a resistance, immunity or vulnerability string into <see cref="DamageAdjustment"/>s with their qualifier.
///
/// <para>
/// <b>Never split on commas first.</b> "bludgeoning, piercing, and slashing from nonmagical weapons that aren't
/// silvered" is ONE entry of the data: three types sharing one qualifier. Splitting it on commas yields "bludgeoning",
/// "piercing" and "and slashing from nonmagical…", and a werewolf becomes immune to every bludgeoning and piercing
/// blow, magical or not (the bug in the other dataset this project read). The qualifier is cut off first, then the
/// type list is split.
/// </para>
/// </summary>
internal static partial class DamageAdjustmentReader
{
    /// <summary>What <see cref="Read"/>'s callers pass for a vulnerability, the one list an unread qualifier is left out of.</summary>
    public const string Vulnerability = "vulnerability";

    /// <summary>
    /// The entries one data string holds. A string with no damage type at all ("damage from spells") returns none and
    /// a warning. An unknown qualifier on a resistance or immunity becomes
    /// <see cref="StatBlockValues.DamageQualifiers.Other"/> (applied always) with a warning: erring towards a tougher
    /// monster. On a vulnerability the same reading would double every such hit (the rakshasa's "piercing from magic
    /// weapons wielded by good creatures" would double every arrow), so an unknown qualifier there is left out with a
    /// warning instead: the error then leans the same safe way.
    /// </summary>
    public static IReadOnlyList<DamageAdjustment> Read(string text, string what, NormalizationLog log)
    {
        var normalized = ProseText.Normalize(text);
        var lower = normalized.ToLowerInvariant();
        var split = QualifierStart().Match(lower);
        var typesPart = split.Success ? lower[..split.Index] : lower;
        var qualifierText = split.Success ? lower[(split.Index + split.Length)..].Trim() : null;

        var types = TypeList().Split(typesPart)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Select(ProseText.DamageType)
            .ToList();
        if (types.Count == 0 || types.Any(t => t is null))
        {
            log.NotModelled(what, $"\"{normalized}\" names no damage type the simulator knows; it is not applied.");
            return [];
        }

        var qualifier = qualifierText switch
        {
            null => null,
            _ when NotSilvered().IsMatch(qualifierText) => StatBlockValues.DamageQualifiers.NonmagicalNotSilvered,
            _ when NotAdamantine().IsMatch(qualifierText) => StatBlockValues.DamageQualifiers.NonmagicalNotAdamantine,
            _ when Nonmagical().IsMatch(qualifierText) => StatBlockValues.DamageQualifiers.Nonmagical,
            _ => StatBlockValues.DamageQualifiers.Other,
        };

        if (qualifier == StatBlockValues.DamageQualifiers.Other && what == Vulnerability)
        {
            log.NotModelled(what, $"\"{normalized}\": the condition \"{qualifierText}\" is not simulated, so the vulnerability is not applied (applying it to all such damage would double every hit).");
            return [];
        }

        if (qualifier == StatBlockValues.DamageQualifiers.Other)
        {
            log.Approximated(what, $"\"{normalized}\": the condition \"{qualifierText}\" is not simulated, so it applies to all such damage.");
        }

        return types.Select(t => new DamageAdjustment(t!, qualifier, normalized)).ToList();
    }

    // Where the qualifier starts: "from nonmagical…", "damage from…", "that…".
    [GeneratedRegex(@"\s+(?:damage\s+)?from\s+|\s+that\s+")]
    private static partial Regex QualifierStart();

    [GeneratedRegex(@",\s*and\s+|\s+and\s+|,\s*")]
    private static partial Regex TypeList();

    [GeneratedRegex(@"^nonmagical (?:weapons|attacks) that aren't silvered$")]
    private static partial Regex NotSilvered();

    [GeneratedRegex(@"^nonmagical (?:weapons|attacks) that aren't adamantine$")]
    private static partial Regex NotAdamantine();

    [GeneratedRegex(@"^nonmagical (?:weapons|attacks)(?: \(from stoneskin\))?$")]
    private static partial Regex Nonmagical();
}
