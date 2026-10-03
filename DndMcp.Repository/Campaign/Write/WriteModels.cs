using DndMcp.Domain.Campaign;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// Who, when and why for one write call: the session it happened in, the reason, whether it is a dry run, and the tool
/// label change_log records.
///
/// <para>
/// <b>The session context</b> (contract §3.6) is <see cref="Session"/> when given, else the live session, else none. It
/// becomes change_log.session_id (so point-in-time replay knows which session a change belongs to), the default
/// <c>learned_session</c> of every knowledge row the batch writes and the default <c>established_session</c> of a fact
/// written as played. Guessing it wrong silently files a reveal under the wrong night, which then decides who was
/// "present" for it, so an explicit session that does not exist is refused rather than ignored. One exception: with no
/// explicit session, campaign_session start and record_past are filed under the session they write (see
/// <see cref="SessionWriter"/>), since that session is what the change is about.
/// </para>
/// </summary>
public sealed record WriteContext
{
    /// <summary>
    /// The session the write belongs to: a number ("12"), <c>session:12</c>, <c>session:live</c> or
    /// <c>session:last</c>. Null: the live session, else none.
    /// </summary>
    public string? Session { get; init; }

    /// <summary>Why; stored on every change_log row of the batch (gate warnings are appended to it).</summary>
    public string? Reason { get; init; }

    /// <summary>Run everything, report what would happen, roll back: nothing persists and no batch exists.</summary>
    public bool DryRun { get; init; }

    /// <summary>change_log.tool, e.g. "campaign_write". Null: the writer's own label.</summary>
    public string? Tool { get; init; }

    /// <summary><see cref="CampaignValues.Actors"/>: <c>claude</c> (tools, the default) or <c>cli</c>.</summary>
    public string Actor { get; init; } = CampaignValues.Actors.Claude;

    /// <summary>The default context: the live session, no reason, a real run.</summary>
    public static WriteContext Default { get; } = new();

    /// <summary>A context for a tool call that takes the session as a number.</summary>
    public static WriteContext For(int? session, string? reason = null, bool dryRun = false, string? tool = null) => new()
    {
        Session = session?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Reason = reason,
        DryRun = dryRun,
        Tool = tool,
    };
}

/// <summary>The outcome words of an applied op (wire strings, as the host prints them).</summary>
public static class WriteOutcomes
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
    public const string Deleted = "deleted";
    public const string Restored = "restored";
    public const string Linked = "linked";
    public const string Unlinked = "unlinked";
    public const string Ticked = "ticked";
    public const string Answered = "answered";
}

/// <summary>What one op (or one knowledge row of a campaign_knowledge call) did.</summary>
/// <param name="OpIndex">The op's 0-based position in the call (messages print it 1-based as "ops item N").</param>
/// <param name="Op">The op (upsert, fact, link, …; record, reveal, retract for knowledge).</param>
/// <param name="Ref">
/// What it acted on, as the author view prints it: <c>kind:slug</c> for an entity, <c>f:&lt;n&gt;</c> for a fact, a
/// link as "from rel to".
/// </param>
/// <param name="Outcome">One of <see cref="WriteOutcomes"/>.</param>
/// <param name="ChangedFields">The columns (and <c>data.&lt;key&gt;</c>, aliases, tags, known_by …) that changed.</param>
/// <param name="Code">The register code the entity or fact carries after the op (F3, Q22), if any.</param>
/// <param name="Knower">For a knowledge row: who (party, character:belmakor).</param>
/// <param name="Gate">For a fact whose gate the op set: the gate as stored, printed back with fact handles.</param>
public sealed record AppliedOp(
    int OpIndex,
    string Op,
    string Ref,
    string Outcome,
    IReadOnlyList<string> ChangedFields,
    string? Code = null,
    string? Knower = null,
    GateSpec? Gate = null);

/// <summary>Warning severities: a warning is worth reading before the table; an advisory is a "probably".</summary>
public static class WarningSeverities
{
    public const string Warning = "warning";
    public const string Advisory = "advisory";
}

/// <summary>The kinds of <see cref="WriteWarning"/>, as wire strings.</summary>
public static class WarningKinds
{
    /// <summary>A gated fact was given to a non-author knower while a gate condition is not met (warn and apply).</summary>
    public const string Gate = "gate";

    /// <summary>A knowledge write completed enough routes to a secret while its <c>after</c> is unmet.</summary>
    public const string ReachableBeforeGate = "reachable_before_gate";

    /// <summary>A fact was superseded: its dependents and the entities to re-check.</summary>
    public const string Superseded = "superseded";

    /// <summary>A fact already superseded by one fact was made superseded by another.</summary>
    public const string SupersessionRepointed = "supersession_repointed";

    /// <summary>A rename gave an entity the name of another live entity of its kind: by-name upserts become ambiguous.</summary>
    public const string NameTwin = "name_twin";

    /// <summary>A derived slug was taken; the new entity got a suffix.</summary>
    public const string SlugCollision = "slug_collision";

    /// <summary>A rename; the slug stays.</summary>
    public const string SlugKept = "slug_kept";

    /// <summary>An aware knowledge row on an author-visibility row: author visibility hides it regardless.</summary>
    public const string AuthorVisibility = "author_visibility";

    /// <summary>A secret's status was set by hand but is derived from its gated facts.</summary>
    public const string DerivedStatus = "derived_status";

    /// <summary>A character is now dead: its membership and knowledge may need review.</summary>
    public const string CharacterDead = "character_dead";

    /// <summary>A question was marked answered with status rather than answer.</summary>
    public const string UseAnswer = "use_answer";

    /// <summary>Nothing to do: an unlink of a missing link, a retract of a missing row, a remove of a missing alias or tag.</summary>
    public const string Nothing = "nothing_to_do";

    /// <summary>A tick went past the clock's ends and was clamped.</summary>
    public const string ClockClamped = "clock_clamped";

    /// <summary>
    /// A fact written as played, or set to canon, ruled or accepted while a gate or reveal rule waits for it to be in play,
    /// has no established session, so it is not in play.
    /// </summary>
    public const string NotInPlay = "not_in_play";

    /// <summary>A soft delete of something already deleted, or a restore of something not deleted.</summary>
    public const string AlreadyThere = "already";

    /// <summary>
    /// Text the batch made readable to a player-side view uses a word an active gate or reveal rule forbids
    /// (<see cref="PlayerTextWarning"/>).
    /// </summary>
    public const string ForbiddenWord = "forbidden_word";

    /// <summary>
    /// Text the batch made readable to a player-side view names something by a name that view does not use: an author or
    /// restricted alias, or the true name of an entity it knows only under a disguise (<see cref="PlayerTextWarning"/>).
    /// </summary>
    public const string HiddenName = "hidden_name";

    /// <summary>A planned session was started or recorded with its prep title, which players now see.</summary>
    public const string PlannedTitle = "planned_title";

    /// <summary>A character joined the party during a session: the membership's since was set to that session.</summary>
    public const string MemberSince = "member_since";
}

/// <summary>The fields a <see cref="PlayerTextWarning"/> names, as wire strings.</summary>
public static class PlayerTextFields
{
    public const string Name = "name";
    public const string Summary = "summary";
    public const string Body = "body";
    public const string Alias = "alias";
    public const string KnownAs = "known_as";
    public const string Statement = "statement";
    public const string Title = "title";
    public const string Recap = "recap";
}

/// <summary>
/// A word in text the batch made readable to a player-side view that the view must not read (contract §0's leak rule
/// and §3.4's vocabulary rules, checked when the text is written: warn and apply). Kind
/// <see cref="WarningKinds.ForbiddenWord"/> or <see cref="WarningKinds.HiddenName"/>. Unlike the other warnings it quotes
/// the word as written, because the author has to find it: the author's own text, reported to the author only, and never
/// appended to change_log.reason.
/// </summary>
/// <param name="Target">What the text belongs to: <c>kind:slug</c>, <c>f:&lt;n&gt;</c> or <c>session:&lt;n&gt;</c>.</param>
/// <param name="Field">One of <see cref="PlayerTextFields"/>.</param>
/// <param name="Word">The words as the text spells them ("Keras's", "Sealed").</param>
/// <param name="Reader">The first player-side view that now reads the text (party, table, public, dm, character:slug).</param>
/// <param name="Source">The rule that forbids the word (<c>f:12</c>, <c>rule:no-name</c>), or the entity the name belongs to.</param>
/// <param name="Remedy">What to do instead, as the message ends ("give known_as with the party's phrasing").</param>
public sealed record PlayerTextWarning(
    string Kind,
    string Message,
    int? OpIndex,
    string Target,
    string Field,
    string Word,
    string Reader,
    string Source,
    string Remedy)
    : WriteWarning(Kind, WarningSeverities.Warning, Message, OpIndex);

/// <summary>
/// Something the author should know about a write that was applied anyway, in the result only the author reads. The
/// reveal checks' warnings (<see cref="GateWarning"/>, <see cref="ReachableBeforeGateWarning"/>) are also appended to
/// change_log.reason, which history shows long after, and are what the table must not hear yet: they name things by
/// handle and never quote a statement or a secret. The others are not appended, and may quote the author's own text when
/// the author has to find it: the planned title now shown to players (<see cref="WarningKinds.PlannedTitle"/>), the word
/// in a recap (<see cref="PlayerTextWarning"/>). None of them is an exception message (contract §0 keeps those free of
/// author text).
/// </summary>
/// <param name="Kind">One of <see cref="WarningKinds"/>.</param>
/// <param name="Severity">One of <see cref="WarningSeverities"/>.</param>
/// <param name="Message">One line for the model.</param>
/// <param name="OpIndex">The op (0-based) it is about, when it is about one.</param>
public record WriteWarning(string Kind, string Severity, string Message, int? OpIndex = null);

/// <summary>
/// A route-count or <c>after</c>/<c>with</c>/<c>prefer</c> condition not met when a gated fact reached a knower. Kind
/// <see cref="WarningKinds.Gate"/>; severity advisory for <c>prefer</c> (the gate's "probably"), warning otherwise.
/// </summary>
/// <param name="Gate"><see cref="GateConditions"/>: after, with, routes or prefer.</param>
/// <param name="Fact">The gated fact (<c>f:&lt;n&gt;</c>).</param>
/// <param name="Knower">Who it reached (party, character:belmakor).</param>
/// <param name="Unmet">after/prefer: facts not in play; with: coupled facts the knower does not have.</param>
/// <param name="LandedSeparately">with: coupled facts the knower learned in another session, with that session.</param>
/// <param name="RevealSession">The session the gated fact reached the knower in (null: no session context).</param>
/// <param name="RoutesComplete">routes: routes complete.</param>
/// <param name="MinRoutes">routes: routes needed.</param>
/// <param name="CompleteRouteIds">routes: the complete routes' ids.</param>
/// <param name="Note">The gate's note (why it exists).</param>
public sealed record GateWarning(
    string Message,
    int? OpIndex,
    string Gate,
    string Fact,
    string Knower,
    IReadOnlyList<string> Unmet,
    IReadOnlyList<LandedSeparatelyItem> LandedSeparately,
    int? RevealSession,
    int? RoutesComplete,
    int? MinRoutes,
    IReadOnlyList<string> CompleteRouteIds,
    string? Note)
    : WriteWarning(WarningKinds.Gate, Gate == GateConditions.Prefer ? WarningSeverities.Advisory : WarningSeverities.Warning, Message, OpIndex);

/// <summary>A coupled fact the knower already had, and the session it was learned in (null: no session recorded).</summary>
public sealed record LandedSeparatelyItem(string Fact, int? Session);

/// <summary>The gate conditions a <see cref="GateWarning"/> names.</summary>
public static class GateConditions
{
    public const string After = "after";
    public const string With = "with";
    public const string Routes = "routes";
    public const string Prefer = "prefer";
}

/// <summary>
/// A knowledge write made enough routes complete while the gate's <c>after</c> conditions are unmet: the party can work
/// the secret out before the story is ready for it.
/// </summary>
/// <param name="Secret">The secret entity the gated fact is about (<c>secret:slug</c>), or null when none.</param>
/// <param name="Fact">The gated fact.</param>
/// <param name="RoutesComplete">Routes complete now.</param>
/// <param name="MinRoutes">Routes needed.</param>
/// <param name="AfterUnmet">The <c>after</c> facts not in play.</param>
public sealed record ReachableBeforeGateWarning(
    string Message,
    int? OpIndex,
    string? Secret,
    string Fact,
    int RoutesComplete,
    int MinRoutes,
    IReadOnlyList<string> AfterUnmet)
    : WriteWarning(WarningKinds.ReachableBeforeGate, WarningSeverities.Warning, Message, OpIndex);

/// <summary>One fact that rests on a superseded one.</summary>
/// <param name="Fact">The dependent (<c>f:&lt;n&gt;</c>).</param>
/// <param name="Depth">1: depends on the superseded fact directly; 2: through one other fact; …</param>
/// <param name="Via">The fact one step closer to the superseded one.</param>
public sealed record DependentItem(string Fact, int Depth, string Via);

/// <summary>
/// A fact became superseded: what rests on it, and the entities those facts are linked to. Flag, don't fix: the
/// dependents are left exactly as they were.
/// </summary>
/// <param name="Superseded">The superseded fact.</param>
/// <param name="SupersededBy">The fact that replaces it, if one was named.</param>
/// <param name="Dependents">Every fact that depends on it, nearest first.</param>
/// <param name="EntitiesToRecheck">Entities linked to those dependents (<c>kind:slug</c>).</param>
public sealed record SupersessionWarning(
    string Message,
    int? OpIndex,
    string Superseded,
    string? SupersededBy,
    IReadOnlyList<DependentItem> Dependents,
    IReadOnlyList<string> EntitiesToRecheck)
    : WriteWarning(WarningKinds.Superseded, WarningSeverities.Warning, Message, OpIndex);

/// <summary>The kinds of <see cref="Consequence"/>.</summary>
public static class ConsequenceKinds
{
    /// <summary>Beats that became reachable when a beat's status changed.</summary>
    public const string BeatsReachable = "beats_reachable";

    /// <summary>A clock filled.</summary>
    public const string ClockFilled = "clock_filled";

    /// <summary>A secret's derived status changed.</summary>
    public const string SecretStatus = "secret_status";
}

/// <summary>Something that followed from a write (not a problem): a beat now reachable, a clock filled, a secret's new status.</summary>
/// <param name="Kind">One of <see cref="ConsequenceKinds"/>.</param>
/// <param name="Message">One line for the model.</param>
/// <param name="Ref">The entity it is about.</param>
/// <param name="Refs">The entities it lists (the newly reachable beats).</param>
public sealed record Consequence(string Kind, string Message, string Ref, IReadOnlyList<string> Refs);

/// <summary>
/// What a write call did. <see cref="BatchId"/> is null for a dry run: nothing persisted, and there is no batch to undo.
/// </summary>
/// <param name="BatchId">The batch (UUIDv7), printed in full so it can be undone.</param>
/// <param name="DryRun">True when nothing was kept.</param>
/// <param name="SessionNumber">The batch's session context, if any.</param>
/// <param name="Applied">What each op did, in op order.</param>
/// <param name="Warnings">Applied anyway, worth reading.</param>
/// <param name="Consequences">What followed.</param>
public sealed record WriteResult(
    string? BatchId,
    bool DryRun,
    int? SessionNumber,
    IReadOnlyList<AppliedOp> Applied,
    IReadOnlyList<WriteWarning> Warnings,
    IReadOnlyList<Consequence> Consequences);
