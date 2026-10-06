using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Combat;

/// <summary>
/// How a <c>targets</c>, <c>source</c> or <c>rolls</c> entry names a combatant (contract §6.2): (1) an exact tracker name,
/// normalised ("Mummy 2" = "mummy-2" = "mummy 2"); then (2) the linked entity's handle (<c>character:torch</c>) or slug
/// (<c>torch</c>); then (3) a unique prefix of a tracker name. The first stage with exactly one match wins; several in a
/// stage are refused, listing them. <c>"name*"</c> is every non-left, non-dead combatant named <c>name</c> or
/// <c>name N</c> (all the copies). Left combatants are found by stages 1 and 2 only, never by a prefix or a star, so a
/// fled Mummy is never hit by "mummy*" or "mum".
/// </summary>
/// <remarks>
/// Refusals name tracker names: <c>combat</c> output is the author's view (D10); <c>state {perspective}</c> never
/// resolves an address. A cross-campaign handle (<c>campaign-slug/kind:slug</c>) is refused (contract §0).
/// </remarks>
public static class CombatAddressing
{
    /// <summary>The suffix that names every copy: <c>"mummy*"</c>.</summary>
    public const string AllCopies = "*";

    /// <summary>The one combatant <paramref name="address"/> names.</summary>
    /// <param name="field">The argument named in a refusal ("targets", "source", "rolls item 2").</param>
    /// <exception cref="DndInputException">No combatant, or several, match; or a star address where one is needed.</exception>
    public static CombatantState Resolve(IReadOnlyList<CombatantState> combatants, string? address, string field)
    {
        var all = ResolveAll(combatants, address, field);
        if (all.Count != 1)
        {
            throw new DndInputException($"{field} \"{DslText.Echo(address)}\" names {all.Count} combatants ({Names(all)}); give one.");
        }

        return all[0];
    }

    /// <summary>The combatants <paramref name="address"/> names: one, or every copy for a star address.</summary>
    /// <exception cref="DndInputException">Nothing matches, a stage matches several, or a cross-campaign handle.</exception>
    public static IReadOnlyList<CombatantState> ResolveAll(IReadOnlyList<CombatantState> combatants, string? address, string field)
    {
        ArgumentNullException.ThrowIfNull(combatants);
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new DndInputException($"{field} has an empty combatant name; give a tracker name, a handle such as \"character:torch\", or \"mummy*\" for every copy.");
        }

        var text = address.Trim();
        if (IsCrossCampaign(text))
        {
            throw new DndInputException($"{field} \"{DslText.Echo(text)}\" names an entity of another campaign; a combatant is addressed by its tracker name or this campaign's handle.");
        }

        return Match(combatants, text, field) ?? throw NotFound(combatants, text, field);
    }

    /// <summary>
    /// A <c>source</c>: the combatant it names, or (when it names no combatant at all) the text itself, kept as the
    /// condition's source note (contract §6.5: "a name of no combatant → source_note").
    /// </summary>
    /// <exception cref="DndInputException">It names several combatants, or every copy.</exception>
    public static (CombatantState? Combatant, string? Note) ResolveSource(IReadOnlyList<CombatantState> combatants, string address, string field)
    {
        ArgumentNullException.ThrowIfNull(combatants);
        var text = address.Trim();
        if (text.Length == 0)
        {
            return (null, null);
        }

        if (IsCrossCampaign(text))
        {
            throw new DndInputException($"{field} \"{DslText.Echo(text)}\" names an entity of another campaign; a combatant is addressed by its tracker name or this campaign's handle.");
        }

        var found = text.EndsWith(AllCopies, StringComparison.Ordinal) ? null : Match(combatants, text, field);
        return found switch
        {
            null => (null, text),
            { Count: 1 } => (found[0], null),
            _ => throw new DndInputException($"{field} \"{DslText.Echo(text)}\" names {found.Count} combatants; give one."),
        };
    }

    // The three stages (or the star); null when nothing matches, a refusal when a stage matches several.
    private static IReadOnlyList<CombatantState>? Match(IReadOnlyList<CombatantState> combatants, string text, string field)
    {
        if (text.EndsWith(AllCopies, StringComparison.Ordinal))
        {
            var stem = CampaignText.Key(text[..^1]);
            var copies = combatants.Where(c => !c.Removed && !c.Dead && IsCopyOf(c.Name, stem)).ToList();
            return stem.Length == 0 || copies.Count == 0 ? null : copies;
        }

        var key = CampaignText.Key(text);
        var exact = combatants.Where(c => CampaignText.Key(c.Name) == key).ToList();
        if (exact.Count > 0)
        {
            return Single(exact, text, field, "share that name");
        }

        var byEntity = combatants.Where(c => c.EntityHandle is { } handle &&
            (string.Equals(handle, text, StringComparison.OrdinalIgnoreCase) || CampaignText.Key(c.EntitySlug) == key)).ToList();
        if (byEntity.Count > 0)
        {
            return Single(byEntity, text, field, "are that character");
        }

        var byPrefix = key.Length == 0
            ? []
            : combatants.Where(c => !c.Removed && CampaignText.Key(c.Name).StartsWith(key, StringComparison.Ordinal)).ToList();
        return byPrefix.Count > 0 ? Single(byPrefix, text, field, "start with that") : null;
    }

    /// <summary>
    /// Every combatant the addresses name, each once, in the order first named (a star expands in tracker order).
    /// </summary>
    /// <exception cref="DndInputException">Any address that names nothing, or names several in one stage.</exception>
    public static IReadOnlyList<CombatantState> ResolveMany(IReadOnlyList<CombatantState> combatants, IReadOnlyList<string>? addresses, string field)
    {
        if (addresses is null || addresses.Count == 0)
        {
            throw new DndInputException($"{field} is empty; give a list of combatants, even for one: [\"mummy-2\"].");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<CombatantState>();
        for (var i = 0; i < addresses.Count; i++)
        {
            var where = addresses.Count == 1 ? field : $"{field} item {(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            foreach (var c in ResolveAll(combatants, addresses[i], where))
            {
                if (seen.Add(c.Id))
                {
                    result.Add(c);
                }
            }
        }

        return result;
    }

    /// <summary>Whether the tracker name is <paramref name="stemKey"/> or "stem N".</summary>
    internal static bool IsCopyOf(string name, string stemKey)
    {
        var key = CampaignText.Key(name);
        if (key == stemKey)
        {
            return true;
        }

        return key.StartsWith(stemKey + " ", StringComparison.Ordinal) && key[(stemKey.Length + 1)..].All(char.IsAsciiDigit);
    }

    private static IReadOnlyList<CombatantState> Single(List<CombatantState> matches, string text, string field, string why)
    {
        if (matches.Count == 1)
        {
            return matches;
        }

        throw new DndInputException($"{field} \"{DslText.Echo(text)}\" matches several combatants that {why}: {Names(matches)}; give the full tracker name.");
    }

    private static DndInputException NotFound(IReadOnlyList<CombatantState> combatants, string text, string field)
    {
        var names = combatants.Count == 0 ? "none yet" : Names(combatants);
        return new DndInputException($"{field} \"{DslText.Echo(text)}\" is not a combatant in this fight (its combatants: {names}).");
    }

    private static string Names(IEnumerable<CombatantState> combatants) =>
        string.Join(", ", combatants.Select(c => c.Removed ? $"{c.Name} (left)" : c.Name));

    // "belmakor/character:keras": a campaign slug, a slash, then a handle.
    private static bool IsCrossCampaign(string text)
    {
        var slash = text.IndexOf('/');
        return slash > 0 && CampaignSlugs.IsValid(text[..slash]) && text.IndexOf(':', slash) > slash;
    }
}
