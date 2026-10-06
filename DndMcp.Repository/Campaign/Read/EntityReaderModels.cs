using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// What <see cref="EntityReader.Get"/> adds to each entity. Every flag is opt-in except the defaults
/// (<see cref="Default"/>: relations, facts, children), because a get of ten refs with every include can run long, and
/// the host caps each result.
/// </summary>
/// <param name="Sheet">
/// A character's sheet (Phase 7, contract §7.3/§7.4): the author view for the author, the public line for a current party
/// member another view is shown, nothing otherwise (<see cref="Characters.SheetReader"/>). Last, with a default, so every
/// Phase 6 caller's positional includes keep their meaning.
/// </param>
public sealed record EntityIncludes(
    bool Relations = false,
    bool Facts = false,
    bool Knowledge = false,
    bool Children = false,
    bool Sessions = false,
    bool History = false,
    bool Sheet = false)
{
    public const string RelationsName = "relations";
    public const string FactsName = "facts";
    public const string KnowledgeName = "knowledge";
    public const string ChildrenName = "children";
    public const string SessionsName = "sessions";
    public const string HistoryName = "history";
    public const string SheetName = "sheet";

    /// <summary>The include vocabulary (forgiving spelling).</summary>
    public static readonly DslValueSet Set = new("include", [RelationsName, FactsName, KnowledgeName, ChildrenName, SessionsName, HistoryName, SheetName]);

    /// <summary>What a get shows when the call names no includes.</summary>
    public static EntityIncludes Default { get; } = new(Relations: true, Facts: true, Children: true);

    /// <summary>Everything.</summary>
    public static EntityIncludes All { get; } = new(true, true, true, true, true, true, true);

    /// <summary>The includes a call named; null gives <see cref="Default"/>, an empty list none.</summary>
    /// <exception cref="DndInputException">A name that is not an include (all problems listed).</exception>
    public static EntityIncludes Parse(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return Default;
        }

        var problems = new List<string>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            if (Set.TryMatch(names[i], out var name))
            {
                set.Add(name);
            }
            else
            {
                problems.Add($"include item {i + 1} (\"{Echo(names[i])}\") is not an include; use {Set.List}.");
            }
        }

        DslProblems.ThrowIfAny(problems, "include");
        return new EntityIncludes(set.Contains(RelationsName), set.Contains(FactsName), set.Contains(KnowledgeName),
            set.Contains(ChildrenName), set.Contains(SessionsName), set.Contains(HistoryName), set.Contains(SheetName));
    }

    private static string Echo(string? text) => text is { Length: > 40 } ? text[..40] + "…" : text ?? string.Empty;
}

/// <summary>The entities and facts a get found, in the order asked.</summary>
/// <param name="Entities">One per entity handle.</param>
/// <param name="Facts">One per fact handle (<c>f:12</c>, a fact code).</param>
/// <param name="AsOfSession">The point in time, when one was asked for.</param>
public sealed record GetResult(IReadOnlyList<EntityDetail> Entities, IReadOnlyList<FactDetail> Facts, int? AsOfSession);

/// <summary>
/// One entity as one perspective sees it (contract §3.2). For a non-author view everything author-only is simply absent:
/// <see cref="Author"/> is null, and a disguised entity carries only its ref, kind, display name and the facts the
/// perspective knows about it (every other field null or empty). There is deliberately no "disguised" flag: the shape
/// itself must not say that a truer name exists.
/// </summary>
/// <param name="Ref">The perspective-safe ref.</param>
/// <param name="Kind">The kind.</param>
/// <param name="DisplayName">The name this perspective knows it by.</param>
/// <param name="Subtype">Its subtype (null when disguised).</param>
/// <param name="Code">Its register code (null when disguised).</param>
/// <param name="Status">The status as shown (a question's withheld or lean is open outside the author view).</param>
/// <param name="Summary">The one-line summary.</param>
/// <param name="BodyMd">The body.</param>
/// <param name="Aliases">The aliases this view may print (visibility filled in for the author only).</param>
/// <param name="Tags">Tags.</param>
/// <param name="Parent">The parent, when visible.</param>
/// <param name="Clock">A clock's segments (non-author views: only when shown to players).</param>
/// <param name="Objectives">A quest's or thread's objectives this view may see.</param>
/// <param name="Relations">Relations (when included).</param>
/// <param name="Facts">Linked facts this view may see (when included).</param>
/// <param name="Knowledge">Verdicts (when included): the perspective's on the entity and each shown fact; the author gets every knower's row.</param>
/// <param name="Children">Child entities (when included).</param>
/// <param name="Sessions">Sessions it is tied to (when included).</param>
/// <param name="Author">Everything only the author may see; null in every other view.</param>
public sealed record EntityDetail(
    string Ref,
    string Kind,
    string DisplayName,
    string? Subtype,
    string? Code,
    string? Status,
    string? Summary,
    string? BodyMd,
    IReadOnlyList<AliasView> Aliases,
    IReadOnlyList<string> Tags,
    EntityLink? Parent,
    ClockView? Clock,
    IReadOnlyList<ObjectiveView> Objectives,
    IReadOnlyList<RelationView>? Relations,
    IReadOnlyList<LinkedFact>? Facts,
    IReadOnlyList<KnowledgeLine>? Knowledge,
    IReadOnlyList<EntityLink>? Children,
    IReadOnlyList<SessionLink>? Sessions,
    AuthorEntityDetail? Author)
{
    /// <summary>
    /// A beat's place in the story web: its prerequisites, the beats it leads to and whether it can happen next. The author
    /// view only (the web is prep; <see cref="ReadBeats"/>), and only for a beat; null otherwise.
    /// </summary>
    public BeatView? Beat { get; init; }

    /// <summary>
    /// A character's sheet when the get includes <c>sheet</c> (<see cref="EntityIncludes.Sheet"/>): the author view, or for
    /// any other view the public line of a current party member it is shown undisguised; null otherwise, and always null
    /// for anything but a character. A non-author view reads a character it may not be shown a sheet for (an NPC, a dead
    /// or departed member, a disguised one) exactly as one with no sheet.
    /// </summary>
    public Characters.SheetView? Sheet { get; init; }
}

/// <summary>An alias; <paramref name="Visibility"/> is filled in for the author view only.</summary>
public sealed record AliasView(string Alias, string? Visibility);

/// <summary>The author-only part of an entity.</summary>
/// <param name="Visibility">The entity's visibility.</param>
/// <param name="CanonStatus">canon, played, proposed, …</param>
/// <param name="Confidence">confirmed, approximate, reconstructed, unverified.</param>
/// <param name="Source">Where it came from ("party-and-band.md:19").</param>
/// <param name="SecretMd">The secret text (render it in a <c>&gt; [!secret]</c> block).</param>
/// <param name="Data">The data object as JSON text.</param>
/// <param name="IntroducedSession">The session it was introduced in, as <c>session:&lt;n&gt;</c>.</param>
/// <param name="SortKey">Its sort key.</param>
/// <param name="CrossLinks">The same being in other campaigns.</param>
/// <param name="Secret">For a secret: its stored and derived status and each gated fact's gate status.</param>
/// <param name="History">Its change history, newest batch first (when included; at most <see cref="EntityReader.HistoryBatchesShown"/>).</param>
/// <param name="HistoryTotal">How many batches changed it in all (when history is included), so the host can say how many were cut.</param>
public sealed record AuthorEntityDetail(
    string Visibility,
    string CanonStatus,
    string Confidence,
    string? Source,
    string SecretMd,
    string Data,
    string? IntroducedSession,
    double? SortKey,
    IReadOnlyList<CrossLinkView> CrossLinks,
    SecretStatusView? Secret,
    IReadOnlyList<HistoryBatch>? History,
    int? HistoryTotal)
{
    /// <summary>
    /// The entity's <c>e:&lt;n&gt;</c> handle, printed beside its <c>kind:slug</c> (review U05): the one handle every
    /// view accepts. A perspective that knows the entity under another name refuses the author's <c>kind:slug</c> (it
    /// spells the true name), so without it the author could not go from <c>item:axiom-cage</c> to what Belmakor sees of
    /// it except through a search. Null only for a detail built by hand.
    /// </summary>
    public string? SeqRef { get; init; }
}

/// <summary>A beat's place in the story web (author view): what it waits on, what it opens, and whether it can happen next.</summary>
/// <param name="Standing">One of <see cref="BeatStandings"/>.</param>
/// <param name="Gate">How its prerequisites gate it: one of <see cref="BeatGates"/> (none, all_of, any_of, mixed).</param>
/// <param name="MetCount">Prerequisites met (the "1" of "1 of 3").</param>
/// <param name="TotalCount">Prerequisites counted (edges from cut beats are not counted).</param>
/// <param name="BlockedBy">The unmet prerequisites, in the order their edges were given.</param>
/// <param name="After">Its prerequisites: the beats with an edge into it, each with the edge's mode and that beat's status.</param>
/// <param name="LeadsTo">The beats it is a prerequisite of, each with the edge's mode and that beat's status.</param>
public sealed record BeatView(
    string Standing,
    string Gate,
    int MetCount,
    int TotalCount,
    IReadOnlyList<EntityLink> BlockedBy,
    IReadOnlyList<BeatEdgeView> After,
    IReadOnlyList<BeatEdgeView> LeadsTo);

/// <summary>One edge of the story web as seen from a beat: the beat at the other end, the edge's mode, that beat's status.</summary>
public sealed record BeatEdgeView(EntityLink Beat, string Mode, string Status);

/// <summary>Where a beat stands (<see cref="BeatView.Standing"/>).</summary>
public static class BeatStandings
{
    /// <summary>It happened (the stored status).</summary>
    public const string Met = "met";

    /// <summary>Not met, and its prerequisites are satisfied: it can happen next.</summary>
    public const string Reachable = "reachable";

    /// <summary>Not met, and waiting on prerequisites (<see cref="BeatView.BlockedBy"/>).</summary>
    public const string Blocked = "blocked";

    /// <summary>Taken out of the story: neither reachable nor blocking.</summary>
    public const string Cut = "cut";
}

/// <summary>An entity of another campaign that is the same being (author view only: the cross-campaign firewall).</summary>
/// <param name="Campaign">The other campaign's slug.</param>
/// <param name="Ref">Its handle there, prefixed with the campaign: <c>one-piece/character:keras</c>.</param>
/// <param name="Name">Its name there.</param>
/// <param name="Note">Why they are linked.</param>
public sealed record CrossLinkView(string Campaign, string Ref, string Name, string? Note);

/// <summary>A relation as seen from the entity being read.</summary>
/// <param name="Rel">The relation name (member_of, serves, …).</param>
/// <param name="Direction"><see cref="RelationDirections.Out"/> (this entity is the subject) or <see cref="RelationDirections.In"/>.</param>
/// <param name="Other">The entity at the other end.</param>
/// <param name="Label">Its label.</param>
/// <param name="Status">current, former, planned, rumored.</param>
/// <param name="SinceSession">Since session number.</param>
/// <param name="UntilSession">Until session number.</param>
/// <param name="Attitude">−100..100.</param>
/// <param name="Symmetric">Reads the same both ways.</param>
/// <param name="Visibility">Author view only.</param>
/// <param name="Data">Author view only: the data object as JSON text.</param>
public sealed record RelationView(
    string Rel,
    string Direction,
    EntityLink Other,
    string? Label,
    string Status,
    int? SinceSession,
    int? UntilSession,
    long? Attitude,
    bool Symmetric,
    string? Visibility,
    string? Data);

/// <summary>Which end of a relation the entity being read is (<see cref="RelationView.Direction"/>).</summary>
public static class RelationDirections
{
    /// <summary>The entity being read is the relation's subject (<c>from</c>).</summary>
    public const string Out = "out";

    /// <summary>The entity being read is the relation's object (<c>to</c>).</summary>
    public const string In = "in";
}

/// <summary>A fact linked to the entity being read.</summary>
/// <param name="Ref"><c>f:&lt;n&gt;</c>.</param>
/// <param name="Code">Its code.</param>
/// <param name="Text">The statement (author) or the perspective's phrasing.</param>
/// <param name="Roles">How it is linked (about, clue_for, …).</param>
/// <param name="Author">Author-only details; null otherwise.</param>
public sealed record LinkedFact(string Ref, string? Code, string Text, IReadOnlyList<string> Roles, AuthorFact? Author);

/// <summary>A fact's author-only details.</summary>
/// <param name="FactType">canon, secret, belief, …</param>
/// <param name="Truth">true, false, partial, unknown.</param>
/// <param name="CanonStatus">canon, played, superseded, …</param>
/// <param name="Confidence">confirmed, approximate, …</param>
/// <param name="Visibility">Its visibility.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="SupersededBy">The fact that replaced it.</param>
/// <param name="EstablishedSession">The session it was established in.</param>
/// <param name="Gate">Its gate with fact handles (never ids), or null.</param>
/// <param name="GateUnreadable">The stored gate could not be read (it is ignored everywhere until rewritten).</param>
/// <param name="KnownBy">Every knower holding it in an aware state.</param>
/// <param name="DependsOn">Facts it rests on.</param>
public sealed record AuthorFact(
    string FactType,
    string Truth,
    string CanonStatus,
    string Confidence,
    string Visibility,
    string? Source,
    string? SupersededBy,
    int? EstablishedSession,
    GateSpec? Gate,
    bool GateUnreadable,
    IReadOnlyList<string> KnownBy,
    IReadOnlyList<string> DependsOn);

/// <summary>A fact read on its own (a get of <c>f:12</c>).</summary>
/// <param name="Ref"><c>f:&lt;n&gt;</c>.</param>
/// <param name="Code">Its code.</param>
/// <param name="Text">The statement (author) or the perspective's phrasing.</param>
/// <param name="Links">The entities it is linked to that this view may see, with the link role.</param>
/// <param name="Knowledge">Verdicts (when knowledge is included).</param>
/// <param name="Author">Author-only details, plus <see cref="AuthorFactDetail.Dependents"/>; null otherwise.</param>
public sealed record FactDetail(
    string Ref,
    string? Code,
    string Text,
    IReadOnlyList<FactEntityLink> Links,
    IReadOnlyList<KnowledgeLine>? Knowledge,
    AuthorFactDetail? Author);

/// <summary>An entity a fact is linked to, with the role.</summary>
public sealed record FactEntityLink(string Role, EntityLink Entity);

/// <summary>A fact's author-only details when read on its own: the linked-fact details plus what rests on it.</summary>
/// <param name="Fact">The details.</param>
/// <param name="Dependents">Facts that depend on it, nearest first (what a supersession would make stale).</param>
/// <param name="History">Its change history, newest batch first (when included; at most <see cref="EntityReader.HistoryBatchesShown"/>).</param>
/// <param name="HistoryTotal">How many batches changed it in all (when history is included).</param>
public sealed record AuthorFactDetail(AuthorFact Fact, IReadOnlyList<string> Dependents, IReadOnlyList<HistoryBatch>? History, int? HistoryTotal);

/// <summary>A clock. Outside the author view it appears only when shown to players, and never with its on-fill text.</summary>
public sealed record ClockView(long Segments, long Filled, string Unit, EntityLink? Front, bool? ShownToPlayers, string? OnFillMd);

/// <summary>An objective of a quest or thread; <paramref name="Index"/> is its 1-based position (the objective op's index).</summary>
public sealed record ObjectiveView(int Index, string Text, string Status, long? Progress, long? ProgressMax, string? Visibility);

/// <summary>A session an entity is tied to: one of <see cref="SessionLinkKinds"/> (attendance note: author only).</summary>
public sealed record SessionLink(string Ref, int Number, string Title, string Relation, string? Note);

/// <summary>How a session is tied to an entity (<see cref="SessionLink.Relation"/>).</summary>
public static class SessionLinkKinds
{
    /// <summary>The entity's introduced session.</summary>
    public const string Introduced = "introduced";

    /// <summary>The character's attendance row says present.</summary>
    public const string Attended = "attended";

    /// <summary>The character's attendance row says absent.</summary>
    public const string Absent = "absent";
}

/// <summary>A secret's status (author view): the stored one, the one its gates give now, and each gated fact's gate.</summary>
/// <param name="StoredStatus">The status column (derived and written by the write path).</param>
/// <param name="DerivedStatus">What the gates give at the read's point in time (<see cref="SecretStatuses.Derive"/>).</param>
/// <param name="Gates">One per gated fact linked <c>about</c> the secret.</param>
public sealed record SecretStatusView(string? StoredStatus, string DerivedStatus, IReadOnlyList<GateStatusView> Gates);

/// <summary>Where one gated fact stands (author view), with fact handles.</summary>
public sealed record GateStatusView(
    string Fact,
    bool KnownToParty,
    IReadOnlyList<string> UnmetAfter,
    IReadOnlyList<RouteView> Routes,
    int RoutesComplete,
    int MinRoutes,
    IReadOnlyList<string> CompleteRouteIds,
    IReadOnlyList<string> UnmetPrefer,
    IReadOnlyList<string> MustLandWith,
    bool ForbiddenActive,
    bool Seeded,
    bool Ready,
    bool ReachableBeforeGate,
    bool Unreadable);

/// <summary>One route of a gate: clues the party knows / needed / total.</summary>
public sealed record RouteView(string Id, int Known, int Needed, int Total, bool Complete);
