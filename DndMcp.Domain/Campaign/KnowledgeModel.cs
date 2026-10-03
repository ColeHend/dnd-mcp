namespace DndMcp.Domain.Campaign;

/// <summary>
/// The perspective a verdict is computed for, resolved against one campaign by the repository: the character's entity,
/// whether (and when) the character belongs to the party, and the campaign's role.
///
/// <para>
/// Pure data so the verdict rules (<c>KnowledgeVerdicts</c>) stay in Domain and are pinned by table-driven tests with no
/// database: the repository loads the rows and the attendance, and Domain decides.
/// </para>
/// </summary>
/// <param name="Perspective">The perspective as parsed.</param>
/// <param name="CampaignRole"><c>player</c> or <c>dm</c>. In a DM campaign the <c>dm</c> perspective is the author.</param>
/// <param name="CharacterId">The character entity's id when <see cref="Perspective"/> is a character; null otherwise.</param>
/// <param name="CharacterName">The character's name, for explanations ("Serif was absent in S1").</param>
/// <param name="Membership">
/// The character's party membership (a <c>member_of</c> relation to the campaign's party faction, current or former);
/// null when the character has never been a member, in which case party rows do not speak for it.
/// </param>
public sealed record PerspectiveContext(
    Perspective Perspective,
    string CampaignRole,
    string? CharacterId = null,
    string? CharacterName = null,
    PartyMembership? Membership = null)
{
    /// <summary>
    /// The author's view: the author perspective, or <c>dm</c> in a DM campaign (Cole is the DM there). Sees every row.
    /// </summary>
    public bool IsAuthorView =>
        Perspective.IsAuthor ||
        (Perspective.Kind == CampaignValues.PerspectiveKinds.Dm && CampaignRole == CampaignValues.Roles.Dm);

    /// <summary>A context for a perspective that is not a character (author, dm, table, party, public).</summary>
    public static PerspectiveContext For(Perspective perspective, string campaignRole) => new(perspective, campaignRole);
}

/// <summary>
/// When a character belonged to the party, as session numbers from its <c>member_of</c> relation (<c>since</c> /
/// <c>until</c>; null when not recorded). A party row learned before <see cref="SinceSession"/> does not speak for a
/// character who joined later (Serif joined after Tristan died), nor one learned after <see cref="UntilSession"/> (the
/// session the membership ended in, which he was still there for) for a character who left; the dated party-visibility
/// default reads the same window (<see cref="KnowledgeVerdicts"/>). A member who has left (<see cref="HasLeft"/>) no
/// longer sees rows with no session to compare (party relations, objectives and aliases: <see cref="Audience"/>).
/// </summary>
public sealed record PartyMembership(int? SinceSession, int? UntilSession)
{
    /// <summary>
    /// The relation's status is <c>former</c>: the character has left the party, whether or not <see cref="UntilSession"/>
    /// says when. Without it a former membership with no until read as a current one, so the character kept seeing the
    /// party's relations, objectives and aliases, and (attendance not recorded) knew everything the party learned after
    /// he left. With no until the verdicts cannot place his leaving, so they ask attendance and, when it was not recorded,
    /// answer uncertain rather than knows (<see cref="KnowledgeVerdicts"/>). Set it from the relation's status.
    /// </summary>
    public bool Former { get; init; }

    /// <summary>The character has left the party: an until session is recorded, or the membership is <see cref="Former"/>.</summary>
    public bool HasLeft => UntilSession is not null || Former;
}

/// <summary>
/// One knowledge row as the verdict rules read it: session references already turned into session numbers, so the rules
/// can order them and read attendance without the database.
/// </summary>
/// <param name="KnowerKind">One of <see cref="CampaignValues.KnowerKinds"/>.</param>
/// <param name="KnowerId">The character entity's id for a <c>character</c> row; null for every other knower.</param>
/// <param name="State">One of <see cref="CampaignValues.KnowledgeStates"/>.</param>
/// <param name="KnownAs">The name (for an entity) or phrasing (for a fact) this knower uses; null when theirs is the true one.</param>
/// <param name="LearnedSession">The session number it was learned in; null for backstory or unknown.</param>
/// <param name="ValidUntilSession">The row stops applying from this session number on (forgotten); null when it still applies.</param>
/// <param name="How">witnessed, told, read, deduced, backstory, sang, … (free text).</param>
/// <param name="ViaName">Who or what it came through, by name, for explanations.</param>
/// <param name="Note">Free text.</param>
public sealed record KnowledgeEntry(
    string KnowerKind,
    string? KnowerId,
    string State,
    string? KnownAs = null,
    int? LearnedSession = null,
    int? ValidUntilSession = null,
    string? How = null,
    string? ViaName = null,
    string? Note = null);

/// <summary>
/// Whether a character was at a session, as far as the record says. <see cref="NotListed"/> (the session has attendance
/// rows, none for this character) is not <see cref="Present"/>: reading a missing row as present would give an absent
/// character everything the party learned that night. <see cref="NotRecorded"/> (no attendance rows at all for that
/// session) is the common case for campaigns that never record attendance.
/// </summary>
public enum AttendanceAnswer
{
    Present,
    Absent,
    NotListed,
    NotRecorded,
}

/// <summary>Attendance lookup by character entity id and session number; the repository implements it over session_attendance.</summary>
public interface IAttendance
{
    AttendanceAnswer Of(string characterId, int sessionNumber);
}

/// <summary>
/// The outcome of a verdict. <see cref="DoesNotKnow"/> is an explicit record (an <c>unaware</c> or <c>forgot</c> row, the
/// character's own or the party's); <see cref="NoRecord"/> is the absence of one. The two must never be worded alike:
/// "the DM does not know" and "nothing records whether the DM knows" are different claims (the Belmakor skill's
/// "no record" cases).
/// </summary>
public enum KnowledgeStanding
{
    Knows,
    DoesNotKnow,
    Uncertain,
    NoRecord,
}

/// <summary>Why a verdict came out as it did; shown beside the verdict so the model can say it.</summary>
public static class KnowledgeBases
{
    public const string Author = "author";
    public const string Explicit = "explicit";
    public const string Party = "party";
    public const string PartyPresent = "party_present";
    public const string PartyAttendanceNotRecorded = "party_attendance_not_recorded";
    public const string Table = "table";
    public const string Dm = "dm";
    public const string Public = "public";
    public const string Visibility = "visibility";
    public const string None = "none";
}

/// <summary>
/// Whether a perspective knows a fact or an entity, under which name, since when and why.
/// </summary>
/// <param name="Standing">The outcome.</param>
/// <param name="State">The knowledge state of the row that decided it (knows, met, unrecognized, unaware, …); null when no row did.</param>
/// <param name="KnownAs">The name or phrasing this perspective uses, from the deciding row (a party row's for a present member).</param>
/// <param name="Basis">One of <see cref="KnowledgeBases"/>.</param>
/// <param name="LearnedSession">The session number of the deciding row, if any.</param>
/// <param name="Explanation">
/// One sentence for the model: "the party learned it in S3 and Ignis was present", "party learned it in S1; Serif was
/// absent", "no record". Never contains author-only text: it names sessions, knower kinds and the perspective's own
/// character, nothing else.
/// </param>
public sealed record KnowledgeVerdict(
    KnowledgeStanding Standing,
    string? State,
    string? KnownAs,
    string Basis,
    int? LearnedSession,
    string Explanation)
{
    /// <summary>True when the perspective knows of it (an aware state).</summary>
    public bool Knows => Standing == KnowledgeStanding.Knows;
}
