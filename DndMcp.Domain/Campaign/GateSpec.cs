using System.ComponentModel;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// A fact's reveal gate: what must be in play before it may be revealed, what must land with it, the clue routes that
/// lead to it, and the words no one may use until it is out (contract §3.4). The One Piece "eating a fruit breaks the
/// seal" reveal is the model: after the axe, together with Nadar's plan, any two of three routes, never the word "seal".
///
/// <para>
/// <b>One class for the wire and the database.</b> Tools take and print fact handles (<c>f:12</c>, <c>F36</c>);
/// campaigns.db stores fact ids, so a later renumbering or code change can never re-point a gate
/// (<see cref="FactGates.Rewrite"/> maps one to the other). Every property is nullable so "not given" is visible to the
/// validator and to the serializer, which omits it.
/// </para>
/// <para>
/// A gate never refuses a write: an unmet condition is a warning on the reveal (the table is the source of truth; players
/// can deduce a secret early). The gate exists so that prep and the knowledge check can see the problem before it
/// reaches the table.
/// </para>
/// </summary>
public sealed class GateSpec
{
    [Description("Facts that must be in play (established, not planned or struck) before this fact may be revealed, e.g. [\"f:31\"].")]
    public IReadOnlyList<string>? After { get; init; }

    [Description("Facts that must reach the same knowers in the same session as this one (they land together or not at all).")]
    public IReadOnlyList<string>? With { get; init; }

    [Description("Advisory conditions (\"probably when …\"): facts that should be in play first; unmet ones are reported, never enforced.")]
    public IReadOnlyList<string>? Prefer { get; init; }

    [Description("Clue facts that hint at this one without completing a route.")]
    public IReadOnlyList<string>? Seeds { get; init; }

    [Description("Independent routes to the reveal: each is complete when the party knows min_clues of its clues.")]
    public IReadOnlyList<RouteSpec?>? Routes { get; init; }

    [Description("How many routes must be complete before the reveal is ready. Default: all of them.")]
    public int? MinRoutes { get; init; }

    [Description("Words no player-facing text may use while this rule is active, e.g. [\"seal\"]; inflections match too. campaign_knowledge check flags them in a draft, and a write that puts them into text players can read warns (and still applies).")]
    public IReadOnlyList<string>? ForbiddenTerms { get; init; }

    [Description("Forbidden word patterns with <number> for any number, e.g. [\"<number>-year-old\", \"<number> years\"].")]
    public IReadOnlyList<string>? ForbiddenPatterns { get; init; }

    [Description("The forbidden words are allowed once all of these facts are in play. Default: once the party knows this fact.")]
    public IReadOnlyList<string>? ForbiddenUntil { get; init; }

    [Description("What to say instead of the forbidden words, e.g. [\"shell\", \"what keeps it in\"].")]
    public IReadOnlyList<string>? PreferredTerms { get; init; }

    [Description("Why the gate exists, for the author.")]
    public string? Note { get; init; }
}

/// <summary>One independent route to a gated reveal: a set of clues, complete when enough of them are known.</summary>
public sealed class RouteSpec
{
    [Description("Required. A short slug naming the route, unique in the gate, e.g. \"testimonies\".")]
    public string? Id { get; init; }

    [Description("Required. The clue facts of this route, e.g. [\"f:40\", \"f:41\"].")]
    public IReadOnlyList<string>? Clues { get; init; }

    [Description("How many clues the party must know to complete the route, 1 to the number of clues. Default: all of them.")]
    public int? MinClues { get; init; }
}
