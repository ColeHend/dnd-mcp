using DndMcp.Domain.Campaign;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// The wire spelling of <see cref="KnowledgeStanding"/> in read results. The enum is in-process state; results are what
/// the host renders and tests serialize, and the house rule is that anything leaving the process is a string constant
/// (an enum's name is not a contract, and a renamed member would silently change every result).
/// </summary>
public static class Standings
{
    public const string Knows = "knows";
    public const string DoesNotKnow = "does_not_know";
    public const string Uncertain = "uncertain";
    public const string NoRecord = "no_record";

    /// <summary>
    /// Not a verdict: the fact or entity is not in play (canon status proposed, planned, lean, struck or superseded; or an
    /// unplayed session), so no player-side view knows it whatever its rows say. The author-facing check and ledger use it
    /// where a read would simply leave the row out (<see cref="ReadScope"/>): a party-visible planned "the axe is fully
    /// assembled" printed as "knows" would tell the author the party already knows something that has not happened.
    /// </summary>
    public const string NotInPlay = "not_in_play";

    /// <summary>
    /// Not a verdict either: the row's visibility is <c>author</c> and knowledge rows say this non-author view knows it
    /// anyway, or may know it (uncertain: a party row and a member who was absent). Author visibility is absolute
    /// (contract §3.2), so no player-side view knows it and none of its reads show it; the author-facing check and ledger
    /// say "author only" there, because the verdict's "knows" would tell the author the party knows what no party read will
    /// ever show, and a song checked against that would pass a line it must not sing. A verdict that already says the view
    /// does not know is reported as it is (the record's "unaware" stays), and "not in play" comes before it.
    /// </summary>
    public const string AuthorOnly = "author_only";

    /// <summary>The wire value of a standing.</summary>
    public static string Of(KnowledgeStanding standing) => standing switch
    {
        KnowledgeStanding.Knows => Knows,
        KnowledgeStanding.DoesNotKnow => DoesNotKnow,
        KnowledgeStanding.Uncertain => Uncertain,
        _ => NoRecord,
    };
}

/// <summary>
/// An entity as another result points at it: the ref and name this perspective uses (a disguised entity's display name
/// and <c>e:&lt;n&gt;</c>, never its true name or slug), and its kind.
/// </summary>
/// <param name="Ref">The perspective-safe ref (contract §3.2): <c>kind:slug</c>, <c>session:&lt;n&gt;</c> or <c>e:&lt;n&gt;</c>.</param>
/// <param name="Kind">The entity's kind (shown even for a disguised entity).</param>
/// <param name="Name">The display name for this perspective.</param>
public sealed record EntityLink(string Ref, string Kind, string Name);

/// <summary>
/// One perspective's verdict on one fact or entity, in the form a non-author result may carry: never the via name, the
/// note or any other knower's row (those are author-only; <see cref="KnowledgeLoader"/>'s class summary says why).
/// </summary>
/// <param name="Target">The fact or entity ref.</param>
/// <param name="Knower">Whose verdict: the perspective (<c>party</c>, <c>character:belmakor</c>), or for an author row the knower.</param>
/// <param name="Standing">One of <see cref="Standings"/>.</param>
/// <param name="State">The deciding row's state (<c>met</c>, <c>unaware</c>, …); null when no row decided.</param>
/// <param name="KnownAs">The name or phrasing this knower uses; null when theirs is the true one.</param>
/// <param name="LearnedSession">The session number it was learned in, when recorded.</param>
/// <param name="Explanation">Why (<see cref="KnowledgeVerdict.Explanation"/>); null for the author's raw rows.</param>
/// <param name="Author">The row's author-only details; null in every non-author view.</param>
public sealed record KnowledgeLine(
    string Target,
    string Knower,
    string Standing,
    string? State,
    string? KnownAs,
    int? LearnedSession,
    string? Explanation,
    KnowledgeRowDetail? Author = null);

/// <summary>A knowledge row's author-only columns (the author view lists every knower's row with these).</summary>
/// <param name="How">witnessed, told, backstory, … (free text).</param>
/// <param name="Via">The ref of who or what it came through (author refs: the true <c>kind:slug</c>).</param>
/// <param name="Note">The author's note.</param>
/// <param name="ValidUntilSession">The session from which it no longer applies (forgotten).</param>
/// <param name="LearnedIngame">The in-game date it was learned.</param>
public sealed record KnowledgeRowDetail(string? How, string? Via, string? Note, int? ValidUntilSession, string? LearnedIngame);

/// <summary>
/// Stored status values the read path filters on that <see cref="CampaignValues.Statuses"/> lists only inside its
/// per-kind vocabularies. Named here once so a summary, a search and a status mask cannot drift apart on a spelling
/// (a typo in an inline "runing" would silently drop every clock from every summary, with no test of the vocabulary
/// failing).
/// </summary>
internal static class ReadStatuses
{
    /// <summary>A question the author leans toward an answer on; shown as open outside the author view (like withheld).</summary>
    public const string QuestionLean = "lean";

    /// <summary>A clock that is ticking: the summary lists these.</summary>
    public const string ClockRunning = "running";

    /// <summary>The quest and thread statuses the summary counts as open work.</summary>
    public static readonly IReadOnlySet<string> OpenThread = new HashSet<string>(StringComparer.Ordinal) { "open", "active", "blocked" };
}
