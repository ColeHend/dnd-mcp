using St = DndMcp.Domain.Campaign.CampaignValues.Statuses;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// A <c>secret</c> entity's status, derived from what the party knows of its gated facts and clues (contract §3.4).
///
/// <para>
/// <b>Why derived, and stored:</b> "how close is the party to the fruit reveal" must never be a status someone forgot to
/// update, so the write path recomputes it in every batch that changes knowledge of the secret's gated facts, their route
/// clues or seeds. It is still written to <c>entity.status</c> as an ordinary logged update, so searches and summaries
/// filter on it like any status and undoing the reveal's batch puts the old status back. A secret with no gated fact
/// keeps a hand-set status: there is nothing to derive it from.
/// </para>
/// </summary>
public static class SecretStatuses
{
    /// <summary>
    /// <c>revealed</c> when the party knows every gated fact; else <c>partial</c> when a gated fact has a complete route
    /// or the party suspects one; else <c>seeded</c> when the party knows a seed, route clue or <c>clue_for</c> fact;
    /// else <c>hidden</c>.
    /// </summary>
    public static string Derive(bool allGatedKnownToParty, bool anyGatedRouteCompleteOrSuspected, bool anySeedOrClueKnown) =>
        allGatedKnownToParty ? St.SecretRevealed
        : anyGatedRouteCompleteOrSuspected ? St.SecretPartial
        : anySeedOrClueKnown ? St.SecretSeeded
        : St.SecretHidden;

    /// <summary>The statuses <see cref="Derive"/> produces; a secret's status set by hand is one of these and will be recomputed.</summary>
    public static IReadOnlyList<string> All { get; } = [St.SecretHidden, St.SecretSeeded, St.SecretPartial, St.SecretRevealed];
}
