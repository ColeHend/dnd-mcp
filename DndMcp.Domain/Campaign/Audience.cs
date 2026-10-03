using P = DndMcp.Domain.Campaign.CampaignValues.PerspectiveKinds;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// Who sees a row that has no knowledge rows of its own: relations, objectives and aliases (contract §3.2).
///
/// <para>
/// <c>public</c> is everyone; <c>party</c> is the party, the table, the dm and characters who are party members now;
/// <c>author</c> is the author view alone. Relations and objectives cannot be <c>restricted</c> (the CHECK refuses it)
/// because there is no knowledge row to resolve it against; if one is ever passed here it is treated as author-only, the
/// strictest reading, so a bad caller hides a row rather than leaking it.
/// </para>
/// <para>
/// <b>A member who left does not see party rows</b> (review L02). These rows carry no session, so nothing says whether
/// one came before he left or after: the party's ally made in session 9, the objective added in session 12, the nickname
/// the party coined last week. "Are or were members" handed every one of them to a character who left in session 1. A
/// knowledge verdict can date what he knew (<see cref="KnowledgeVerdicts"/> reads membership by session); this rule
/// cannot, so it keeps to the members of now: not one whose membership has an <c>until</c>, nor one marked former
/// without it (<see cref="PartyMembership.HasLeft"/>).
/// </para>
/// <para>
/// A relation also needs both endpoints visible (<see cref="RelationVisible"/>): a party-visible "serves" edge from a
/// hidden villain would otherwise tell the party the villain exists.
/// </para>
/// </summary>
public static class Audience
{
    /// <summary>Whether <paramref name="who"/> sees a row of visibility <paramref name="rowVisibility"/>.</summary>
    /// <exception cref="ArgumentException">A visibility that is not a stored visibility (public, party, restricted, author).</exception>
    public static bool Sees(PerspectiveContext who, string rowVisibility)
    {
        ArgumentNullException.ThrowIfNull(who);
        if (!V.Set.Contains(rowVisibility))
        {
            throw new ArgumentException($"\"{rowVisibility}\" is not a stored visibility.", nameof(rowVisibility));
        }

        if (who.IsAuthorView)
        {
            return true;
        }

        return rowVisibility switch
        {
            V.Public => true,
            V.Party => who.Perspective.Kind switch
            {
                P.Party or P.Table or P.Dm => true,
                P.Character => who.Membership is { HasLeft: false },
                _ => false,
            },
            _ => false,
        };
    }

    /// <summary>
    /// A relation is shown when its own visibility admits the perspective and both of its endpoints are visible to it
    /// (the strictest of the three wins).
    /// </summary>
    public static bool RelationVisible(PerspectiveContext who, string relationVisibility, bool fromVisible, bool toVisible) =>
        fromVisible && toVisible && Sees(who, relationVisibility);
}
