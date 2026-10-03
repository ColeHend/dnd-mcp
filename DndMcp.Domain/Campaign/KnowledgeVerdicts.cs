using System.Globalization;
using S = DndMcp.Domain.Campaign.CampaignValues.KnowledgeStates;
using K = DndMcp.Domain.Campaign.CampaignValues.KnowerKinds;
using P = DndMcp.Domain.Campaign.CampaignValues.PerspectiveKinds;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// Whether a perspective knows a fact or an entity: the one place the knowledge precedence lives (contract §3.3), so the
/// search, get, check, ledger and reveal paths can never disagree about who knows what.
///
/// <para>
/// <b>Precedence, first that applies decides.</b> A character: its own row; else the party row (if it has ever been a
/// member), read through membership and attendance; else a public row; else the target's visibility default; else no
/// record. The party / table / dm / public perspectives each read their own row first, then the rows of the knowers they
/// contain (a dm has seen what the table has, the table what the party has, everyone what the public has). The author
/// view always knows.
/// </para>
/// <para>
/// <b>Knowing flows upward, not knowing does not.</b> A row of a knower the perspective belongs to (a character is in the
/// party and the public; the party is in the public) speaks both ways: "the party is unaware" is true of every member
/// without a row of their own. A row of a knower the perspective merely contains (the party's or the public's row read
/// for the table or the dm; the table's read for the dm) decides only when it is aware: the party knowing implies the
/// table heard it, but the party not knowing says nothing about what the DM knows. Reading an unaware party row as "the
/// DM does not know" is the Belmakor fixture's row 8 mistake: the answer there is "no record".
/// </para>
/// <para>
/// <b>An unaware contained row still stops the visibility default.</b> When the only contained rows are unaware (and the
/// perspective has no row of its own) the verdict is "no record", never the <c>public</c> / <c>party</c> visibility
/// default. The default is a presumption ("a party-visible thing is known to the party"); an explicit "the party is
/// unaware" row contradicts it. Falling through would show the table (the players, out of character) a fact the party is
/// recorded as not knowing, explained as "party-visible: the party knows it" — the opposite of the row. This is the one
/// point where the contract §3.3 table ("party row → by state") and golden row 8 cannot both hold; "no record" satisfies
/// row 8 and hides the target just as DoesNotKnow would.
/// </para>
/// <para>
/// <b>The party-visibility default has a date</b> (review L02). A party-visible target has a session it entered the
/// party's knowledge in (the caller's <c>targetSinceSession</c>: an entity's introduced session, a fact's established
/// session, a session entity's own number). For a character the default then reads exactly like a party row learned in
/// that session, through the same membership window and attendance: present, or attendance not recorded, knows; absent,
/// not listed, joined later or left before is uncertain. Sessions are party-visible and have no knowledge rows, so an
/// undated default let a member who was absent read the recap of the night he missed (Serif and Tristan's death, the
/// event of golden row 11), and a member who had left read every session, place and fact that came after. Both records
/// of one event (a party row, or party visibility alone) now give the same answer. With no target session the default
/// is undated, as before. <c>public</c> visibility is never dated: everyone knows a public thing.
/// </para>
/// <para>
/// <b>As of a session, a later row is a wall, not a gap</b> (review L05). A row learned after the session asked for
/// records that its knower did not know it then. When the row that would decide (the perspective's own, or a group's row
/// that speaks for it both ways: the party's for a member, the public's for a character or the party) applies only from
/// a later session, the verdict is no record ("not yet: learned in S3") and nothing below it is read. Skipping it, as a
/// plain "does it apply" filter does, fell through to the visibility default: the party met Morwen Vashkar in S3 as "the
/// veiled woman", and as of S2 the party saw her true name, summary and slug because she is party-visible. A contained
/// knower's later row (the party's or the public's for the table; the table's for the dm) reads like an unaware one, for
/// the reason above: it does not decide, and it rules out the visibility default. A dated party default whose session
/// comes after the one asked for is "not yet" for every perspective it speaks for (a member, the party, the table, the dm).
/// </para>
/// <para>
/// <b>A later unaware or forgot row is a wall too, worded as one.</b> Forgetting is recorded by changing the row's state,
/// which moves its session to the one it was forgotten in, so "forgot in S5" no longer says when it was learned. As of
/// S3 the knower may well have known it, but the row that would say so, and under which name, is gone; falling through
/// could show the target under its true name where the lost row had a known_as. So it stops there too, and says only
/// what is recorded: "no record as of S3 (the row dates from S5)", never "learned".
/// </para>
/// <para>
/// <b>What breaks if this drifts:</b> counting a missing attendance row as present hands an absent character everything
/// the party learned that night; letting a table or dm row speak for a character puts the players' metagame knowledge in
/// a character's mouth; wording <see cref="KnowledgeStanding.NoRecord"/> like "does not know" asserts something nobody
/// recorded. Explanations name sessions, knower kinds and the perspective's own character only, never a known_as, a
/// via-name or a note, because they are shown to non-author views.
/// </para>
/// </summary>
public static class KnowledgeVerdicts
{
    /// <summary>
    /// The explanation of a <see cref="KnowledgeStanding.NoRecord"/> verdict when nothing is recorded. An as_of verdict
    /// stopped by a row dated later says "not yet: learned in S&lt;m&gt;" (an aware row) or "no record as of S&lt;n&gt;
    /// (the row dates from S&lt;m&gt;)" (an unaware or forgot row) instead; it never says "does not know" either.
    /// </summary>
    public const string NoRecordText = "no record";

    /// <summary>
    /// Whether a row applies at <paramref name="asOfSession"/>: learned by then (or never dated) and not yet forgotten.
    /// With no session it is "now": every row that has not been forgotten (<c>valid_until</c> unset) applies, including
    /// rows learned in future-numbered sessions, because "now" has no upper bound.
    /// </summary>
    public static bool Applies(KnowledgeEntry row, int? asOfSession)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (asOfSession is not { } n)
        {
            return row.ValidUntilSession is null;
        }

        return (row.LearnedSession is null || row.LearnedSession <= n) && (row.ValidUntilSession is null || row.ValidUntilSession > n);
    }

    /// <summary>
    /// The verdict for <paramref name="who"/> on one fact or entity, from that target's knowledge rows (any knower) and
    /// its visibility (as of <paramref name="asOfSession"/>, which the caller replays; this method does not).
    /// </summary>
    /// <param name="who">The resolved perspective. A character perspective must carry its character id.</param>
    /// <param name="rows">
    /// Every knowledge row of the target, whatever its sessions. Do not filter them by <see cref="Applies"/> first: a row
    /// learned after <paramref name="asOfSession"/> does not apply, but it still stops the precedence where it stands.
    /// </param>
    /// <param name="targetVisibility">The target's visibility (public, party, restricted or author).</param>
    /// <param name="attendance">Attendance, read for a party row with a learned session and for a dated party default.</param>
    /// <param name="asOfSession">A point in time, or null for now.</param>
    /// <param name="targetSinceSession">
    /// The session the target entered the party's knowledge in by its visibility: an entity's introduced session, a fact's
    /// established session, a session entity's own number. Null when there is none (backstory, world-building never
    /// introduced at the table): the party-visibility default is then undated, as it always was.
    /// </param>
    /// <exception cref="ArgumentException">A character perspective without a character id (a resolver bug).</exception>
    public static KnowledgeVerdict Evaluate(
        PerspectiveContext who,
        IReadOnlyList<KnowledgeEntry> rows,
        string targetVisibility,
        IAttendance attendance,
        int? asOfSession = null,
        int? targetSinceSession = null)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(attendance);
        if (who.IsAuthorView)
        {
            return new KnowledgeVerdict(KnowledgeStanding.Knows, S.Knows, null, KnowledgeBases.Author, null, "the author knows everything");
        }

        var at = new RowsAt(rows, asOfSession);
        return who.Perspective.Kind switch
        {
            P.Character => ForCharacter(who, at, targetVisibility, attendance, targetSinceSession),
            P.Party => at.Decide(K.Party, r => ByState(r, KnowledgeBases.Explicit, "the party's own record")) ??
                       at.Decide(K.Public, r => ByState(r, KnowledgeBases.Public, "the public record")) ??
                       ForGroup(targetVisibility, asOfSession, targetSinceSession),
            P.Table => at.Decide(K.Table, r => ByState(r, KnowledgeBases.Explicit, "the table's own record")) ??
                       Contained(at, [K.Party, K.Public]) ??
                       ForGroup(targetVisibility, asOfSession, targetSinceSession),
            P.Dm => at.Decide(K.Dm, r => ByState(r, KnowledgeBases.Explicit, "the DM's own record")) ??
                    Contained(at, [K.Table, K.Party, K.Public]) ??
                    ForGroup(targetVisibility, asOfSession, targetSinceSession),
            P.Public => at.Decide(K.Public, r => ByState(r, KnowledgeBases.Explicit, "the public record")) ??
                        ByVisibility(targetVisibility, partyMember: false),
            _ => throw new ArgumentException($"Unknown perspective kind \"{who.Perspective.Kind}\".", nameof(who)),
        };
    }

    private static KnowledgeVerdict ForCharacter(PerspectiveContext who, RowsAt at, string visibility, IAttendance attendance, int? since)
    {
        var id = who.CharacterId ?? throw new ArgumentException("A character perspective needs its character id.", nameof(who));
        var name = string.IsNullOrWhiteSpace(who.CharacterName) ? "this character" : who.CharacterName.Trim();

        // 1. The character's own row always wins, aware or not.
        if (at.Decide(K.Character, r => ByState(r, KnowledgeBases.Explicit, $"{name}'s own record"), id) is { } own)
        {
            return own;
        }

        // 2. The party's row speaks for a member, read through membership and attendance.
        if (who.Membership is { } membership &&
            at.Decide(K.Party, party => ThroughParty(id, name, membership, party, attendance)) is { } byParty)
        {
            return byParty;
        }

        // 3. A character is one of the public: a public row speaks both ways.
        if (at.Decide(K.Public, r => ByState(r, KnowledgeBases.Public, "the public record")) is { } pub)
        {
            return pub;
        }

        // 4. The visibility default; dated, a party default reads like a party row learned in that session. 5. Nothing.
        if (visibility == V.Party && who.Membership is { } member && since is { } dated)
        {
            if (at.AsOf is { } n && dated > n)
            {
                return NotYet($"party-visible since {Sn(dated)}");
            }

            var (standing, _, explanation) = ThroughMembership(id, name, member, dated, attendance, $"party-visible since {Sn(dated)}");
            return new KnowledgeVerdict(standing, S.Knows, null, KnowledgeBases.Visibility, null, explanation);
        }

        return ByVisibility(visibility, partyMember: who.Membership is not null);
    }

    /// <summary>A party row as a member reads it: by state, then (aware and dated) through the membership window and attendance.</summary>
    private static KnowledgeVerdict ThroughParty(string id, string name, PartyMembership membership, KnowledgeEntry party, IAttendance attendance)
    {
        if (!S.AwareStates.Contains(party.State))
        {
            return ByState(party, KnowledgeBases.Party, "the party's record");
        }

        if (party.LearnedSession is not { } learned)
        {
            return Verdict(KnowledgeStanding.Knows, party, KnowledgeBases.Party, "the party knows it (no session recorded)");
        }

        var (standing, basis, explanation) = ThroughMembership(id, name, membership, learned, attendance, $"the party learned it in {Sn(learned)}");
        return Verdict(standing, party, basis, explanation);
    }

    /// <summary>
    /// Whether what the party came to know in <paramref name="session"/> reached the member: the one membership-window and
    /// attendance rule, shared by a party row and the dated party-visibility default so the two records of one event can
    /// never disagree. <paramref name="lead"/> opens the explanation ("the party learned it in S1", "party-visible since S1").
    /// </summary>
    private static (KnowledgeStanding Standing, string Basis, string Explanation) ThroughMembership(
        string id, string name, PartyMembership membership, int session, IAttendance attendance, string lead)
    {
        if (membership.SinceSession is { } since && since > session)
        {
            return (KnowledgeStanding.Uncertain, KnowledgeBases.Party, $"{lead}, before {name} joined in {Sn(since)}");
        }

        // Until is the session the membership ended in: the member was there for it, and missed only what came after.
        if (membership.UntilSession is { } until && session > until)
        {
            return (KnowledgeStanding.Uncertain, KnowledgeBases.Party, $"{lead}, after {name} left in {Sn(until)}");
        }

        return attendance.Of(id, session) switch
        {
            AttendanceAnswer.Present => (KnowledgeStanding.Knows, KnowledgeBases.PartyPresent, $"{lead} and {name} was present"),
            AttendanceAnswer.Absent => (KnowledgeStanding.Uncertain, KnowledgeBases.Party, $"{lead}; {name} was absent"),
            AttendanceAnswer.NotListed => (KnowledgeStanding.Uncertain, KnowledgeBases.Party,
                $"{lead}; no attendance recorded for {name} in {Sn(session)}"),

            // He left in a session nobody recorded: with no attendance either, nothing says he was still there for it.
            _ when membership is { Former: true, UntilSession: null } => (KnowledgeStanding.Uncertain, KnowledgeBases.Party,
                $"{lead}; attendance was not recorded for {Sn(session)}, and {name} left the party in a session not recorded"),
            _ => (KnowledgeStanding.Knows, KnowledgeBases.PartyAttendanceNotRecorded, $"{lead}; attendance was not recorded for {Sn(session)}"),
        };
    }

    /// <summary>
    /// Rows of knowers the perspective contains, in order: the first aware one that applies decides. Unaware ones, and
    /// ones dated after the session, are not evidence that the perspective does not know, but they do rule out the
    /// visibility default: when every contained row found is one of those the verdict is "no record" (see the class
    /// summary), naming the earliest later row when there is one. Null when there is no contained row at all.
    /// </summary>
    private static KnowledgeVerdict? Contained(RowsAt at, IReadOnlyList<string> knowerKinds)
    {
        var unawareSeen = false;
        KnowledgeEntry? earliestLater = null;
        foreach (var kind in knowerKinds)
        {
            var slot = at.Find(kind);
            if (slot.Row is not { } row)
            {
                if (slot.Later is { } later && (earliestLater is null || later.LearnedSession < earliestLater.LearnedSession))
                {
                    earliestLater = later;
                }

                continue;
            }

            if (!S.AwareStates.Contains(row.State))
            {
                unawareSeen = true;
                continue;
            }

            var (basis, what) = kind switch
            {
                K.Table => (KnowledgeBases.Table, "the table's record"),
                K.Party => (KnowledgeBases.Party, "the party's record"),
                _ => (KnowledgeBases.Public, "the public record"),
            };
            return ByState(row, basis, what);
        }

        return earliestLater is { } first ? at.Stopped(first)
            : unawareSeen ? NoRecord
            : null;
    }

    /// <summary>
    /// The visibility default of the party, the table and the dm: public and party targets are known. A dated party target
    /// is "not yet" in a read as of an earlier session, as a party row learned in its session would be.
    /// </summary>
    private static KnowledgeVerdict ForGroup(string visibility, int? asOf, int? since)
    {
        if (visibility != V.Party || since is not { } dated)
        {
            return ByVisibility(visibility, partyMember: true);
        }

        return asOf is { } n && dated > n
            ? NotYet($"party-visible since {Sn(dated)}")
            : new KnowledgeVerdict(KnowledgeStanding.Knows, S.Knows, null, KnowledgeBases.Visibility, null,
                $"party-visible since {Sn(dated)}: the party knows it");
    }

    private static KnowledgeVerdict ByState(KnowledgeEntry row, string basis, string what)
    {
        var aware = S.AwareStates.Contains(row.State);
        var when = row.LearnedSession is { } s ? $", {Sn(s)}" : string.Empty;
        return Verdict(aware ? KnowledgeStanding.Knows : KnowledgeStanding.DoesNotKnow, row, basis, $"{what}: {row.State}{when}");
    }

    private static KnowledgeVerdict ByVisibility(string visibility, bool partyMember) => visibility switch
    {
        V.Public => new KnowledgeVerdict(KnowledgeStanding.Knows, S.Knows, null, KnowledgeBases.Visibility, null, "public: everyone knows it"),
        V.Party when partyMember => new KnowledgeVerdict(KnowledgeStanding.Knows, S.Knows, null, KnowledgeBases.Visibility, null,
            "party-visible: the party knows it"),
        _ => NoRecord,
    };

    /// <summary>The "no record" verdict: no state, no basis, no session; worded so it never claims "does not know".</summary>
    private static KnowledgeVerdict NoRecord { get; } = new(KnowledgeStanding.NoRecord, null, null, KnowledgeBases.None, null, NoRecordText);

    /// <summary>
    /// "No record" in an as_of read stopped by something dated after the session asked for. Like <see cref="NoRecord"/> it
    /// carries no state, known_as or session (the later row's name for the target must not reach a view of the time before
    /// it was learned); the explanation names only sessions.
    /// </summary>
    private static KnowledgeVerdict NotYet(string why) => Unrecorded("not yet: " + why);

    private static KnowledgeVerdict Unrecorded(string explanation) =>
        new(KnowledgeStanding.NoRecord, null, null, KnowledgeBases.None, null, explanation);

    private static KnowledgeVerdict Verdict(KnowledgeStanding standing, KnowledgeEntry row, string basis, string explanation) =>
        new(standing, row.State, row.KnownAs, basis, row.LearnedSession, explanation);

    private static string Sn(int session) => "S" + session.ToString(CultureInfo.InvariantCulture);

    /// <summary>One knower's rows at the point in time: the row that applies, else the earliest one learned after it.</summary>
    private readonly record struct Slot(KnowledgeEntry? Row, KnowledgeEntry? Later);

    /// <summary>A target's rows read at one point in time (<see cref="Applies"/>), keeping the ones learned after it in view.</summary>
    private sealed class RowsAt(IReadOnlyList<KnowledgeEntry> rows, int? asOf)
    {
        public int? AsOf { get; } = asOf;

        /// <summary>
        /// A row that speaks for the perspective both ways: the one that applies decides (<paramref name="decide"/>); one
        /// dated after the session stops the precedence there (<see cref="Stopped"/>), and nothing below it is read; null
        /// when the knower has no row at all.
        /// </summary>
        public KnowledgeVerdict? Decide(string knowerKind, Func<KnowledgeEntry, KnowledgeVerdict> decide, string? knowerId = null)
        {
            var slot = Find(knowerKind, knowerId);
            return slot.Row is { } row ? decide(row)
                : slot.Later is { } later ? Stopped(later)
                : null;
        }

        /// <summary>
        /// The no-record verdict of a row dated after the session asked for: "not yet: learned in S3" for an aware row; for
        /// an unaware or forgot row, whose session is when it was recorded, not learned, "no record as of S2 (the row dates
        /// from S3)" (class summary).
        /// </summary>
        public KnowledgeVerdict Stopped(KnowledgeEntry later)
        {
            // Find keeps a later row only when the read has a session and the row a learned session after it.
            var dated = Sn(later.LearnedSession.GetValueOrDefault());
            return S.AwareStates.Contains(later.State)
                ? NotYet($"learned in {dated}")
                : Unrecorded($"no record as of {Sn(AsOf.GetValueOrDefault())} (the row dates from {dated})");
        }

        /// <summary>The row of a knower kind (for a character, of that id) that applies, else the earliest learned after the session.</summary>
        public Slot Find(string knowerKind, string? knowerId = null)
        {
            KnowledgeEntry? later = null;
            foreach (var row in rows)
            {
                if (row.KnowerKind != knowerKind || (knowerId is not null && row.KnowerId != knowerId))
                {
                    continue;
                }

                if (Applies(row, AsOf))
                {
                    return new Slot(row, null);
                }

                if (AsOf is { } n && row.LearnedSession is { } learned && learned > n && (later is null || learned < later.LearnedSession))
                {
                    later = row;
                }
            }

            return new Slot(null, later);
        }
    }
}
