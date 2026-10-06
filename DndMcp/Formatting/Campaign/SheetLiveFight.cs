using System.Globalization;
using System.Text;
using DndMcp.Domain.Combat;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using ES = DndMcp.Domain.Campaign.CampaignValues.EncounterStatuses;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// The one line an AUTHOR read of a sheet adds while its character is in the live fight (fix F1, review U04): "In the live
/// fight "Reef": HP 65/85, poisoned (Ogre), 1st-level slots 1/2 left, …; the sheet catches up when it ends: combat
/// {"action": "state", "campaign": "deep"}". Printed by <c>campaign_character get</c> (one character and the list form),
/// <c>campaign_get include sheet</c>, <c>campaign://&lt;slug&gt;/party</c> and the entity resource, never in another view.
///
/// <para>
/// <b>Why a line and not the fight's numbers in place of the sheet's.</b> D5 keeps <c>get</c> acting on the sheet: the
/// sheet is what <c>end</c> writes back over, and what an undo restores. But D5 also sends <c>campaign_character damage</c>
/// to the fight, so a damage followed by a get read the pre-fight HP with no word of the fight: the headless models read
/// "HP 27/27" for a wizard the fight had at 15 and answered right only because they also read <c>combat state</c>. The line
/// says where the fight has the character now and the call that shows the whole fight, so the sheet's numbers can no
/// longer pass for the live ones.
/// </para>
/// <para>
/// <b>Who gets one:</b> a sheet-seeded combatant of the campaign's ACTIVE fight (one seeded from a stat block, or added
/// with no sheet, is not the sheet's). One that LEFT the fight keeps its line, which says "(left the fight)" (fix F2,
/// review CR07): it stays tied to the fight until the fight ends, <c>end</c> writes back the state it left in, and its
/// character's sheet actions still go to it (D5), so without the line the read showed the pre-fight numbers, a heal on
/// the sheet seemed to do nothing, and <c>end</c> then wrote the fight's HP over the sheet. The fight is read with the
/// combat layer's own author reader (<see cref="CombatReader.State"/>), so the line shows exactly what <c>combat
/// state</c> shows. A fight that cannot be read (an unreadable combatant row) gives no line: a sheet read never fails
/// because of a fight, and <c>combat state</c> says what is wrong with it.
/// </para>
/// <para>
/// <b>The fight's name is prose here</b> (fix F2, review CR10): it is printed as it is, in quotes, never JSON-escaped
/// ("Reef \"Night\"" read with its backslashes); only a call escapes a name, and this line's call names none.
/// </para>
/// </summary>
internal static class SheetLiveFight
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// The line of every character in <paramref name="campaign"/>'s active fight from its sheet, keyed by its handle
    /// (<c>character:slug</c>, as <see cref="Repository.Campaign.Characters.AuthorSheetView.Ref"/> names it); empty when no
    /// fight is running or it cannot be read.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Read(CampaignDatabase database, CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(campaign);
        CombatStateRead read;
        try
        {
            read = new CombatReader(database).State(campaign);
        }
        catch (CampaignStoreUnavailableException)
        {
            return None;
        }

        if (read.Encounter is not { Status: ES.Active } encounter)
        {
            return None;
        }

        // A character has one sheet-seeded combatant (a second for the same entity is refused, and add re-joins a left one),
        // so a left row and a present one never share a handle; were they to, the one still in the fight is the line.
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in encounter.Rows.Where(r => r is { SheetSeeded: true, EntityHandle: not null }).OrderByDescending(r => r.Left))
        {
            lines[row.EntityHandle!] = Line(encounter, row, read.Campaign);
        }

        return lines;
    }

    /// <summary>The line for <paramref name="reference"/>, or null when it is not in the fight from its sheet.</summary>
    public static string? For(IReadOnlyDictionary<string, string>? lines, string reference) =>
        lines is not null && lines.TryGetValue(reference, out var line) ? line : null;

    private static string Line(EncounterView encounter, CombatantView row, string campaign)
    {
        var parts = new List<string> { HitPoints(row) };
        if (row.Exhaustion > 0)
        {
            parts.Add("exhaustion " + N(row.Exhaustion));
        }

        parts.AddRange(row.Conditions.Select(OneLine));
        if (row.Concentration is { } concentration)
        {
            parts.Add("concentrating on " + OneLine(concentration));
        }

        if (row.DeathSaves is { } saves && row.Hp == 0 && !row.Dead)
        {
            parts.Add(saves.Stable ? "stable" : $"death saves: {Count(saves.Successes, "success", "successes")}, {Count(saves.Failures, "failure", "failures")}");
        }

        if (encounter.State.Find(row.Id) is { } state)
        {
            parts.AddRange(Spent(state));
        }

        var b = new StringBuilder("In the live fight \"").Append(OneLine(encounter.Name)).Append('"')
            .Append(row.Left ? " (left the fight)" : string.Empty).Append(": ")
            .Append(string.Join(", ", parts))
            .Append("; the sheet catches up when it ends: combat {\"action\": \"state\", \"campaign\": \"").Append(campaign).Append("\"}");
        return b.ToString();
    }

    private static string HitPoints(CombatantView row)
    {
        if (row.Dead)
        {
            return "dead";
        }

        var text = row.Hp is { } hp
            ? $"HP {N(hp)}/{(row.MaxHp is { } max ? N(max) : "?")}"
            : row.DamageTaken > 0 ? $"HP unknown, took {N(row.DamageTaken)}" : "HP unknown";
        return row.TempHp > 0 ? $"{text} (+{N(row.TempHp)} temp)" : text;
    }

    // What the fight has spent that the sheet still shows unspent: slots, Pact Magic, the sheet's resources and the items
    // used, each only when some of it is used ("3rd-level slots 1/2 left", "Rage 3/4 left", "Potion of Healing 1 used").
    private static IEnumerable<string> Spent(CombatantState state)
    {
        foreach (var (key, resource) in state.Resources)
        {
            if (resource.Used is not { } spent || spent <= 0)
            {
                continue;
            }

            var left = resource.Max is { } max ? $"{N(max - spent)}/{N(max)} left" : null;
            if (key.StartsWith(CombatValues.ResourceKeys.SlotPrefix, StringComparison.Ordinal)
                && int.TryParse(key[CombatValues.ResourceKeys.SlotPrefix.Length..], NumberStyles.None, Invariant, out var level))
            {
                yield return $"{Ordinal(level)}-level slots {left ?? N(spent) + " used"}";
            }
            else if (key == CombatValues.ResourceKeys.Pact)
            {
                yield return "pact slots" + (resource.Level is { } pactLevel ? $" ({Ordinal(pactLevel)})" : string.Empty) + " " + (left ?? N(spent) + " used");
            }
            else if (key.StartsWith(CombatValues.ResourceKeys.ItemPrefix, StringComparison.Ordinal))
            {
                yield return $"{OneLine(resource.Name ?? "an item")} {N(spent)} used";
            }
            else if (!key.StartsWith(CombatValues.ResourceKeys.LimitedPrefix, StringComparison.Ordinal)
                     && !key.StartsWith(CombatValues.ResourceKeys.PoolPrefix, StringComparison.Ordinal))
            {
                yield return $"{OneLine(resource.Name ?? key)} {left ?? N(spent) + " used"}";
            }
        }
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => N(n) + "th",
    };

    private static string Count(int count, string one, string many) => $"{N(count)} {(count == 1 ? one : many)}";

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    private static string N(int value) => value.ToString(Invariant);
}
