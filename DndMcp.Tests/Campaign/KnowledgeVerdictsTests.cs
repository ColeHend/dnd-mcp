using System.Globalization;
using DndMcp.Domain.Campaign;
using Xunit;
using static DndMcp.Tests.Campaign.Knowers;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: the knowledge verdict follows the contract §3.3 precedence exactly, for every perspective kind, and never
/// says more than the rows do: a character's own row beats the party's, the party's row speaks for a member only through
/// membership and attendance (a missing attendance row is never "present"), knowing flows from a contained knower to a
/// containing one but not knowing does not (an unaware party row is not evidence about the DM or the table), table / dm /
/// author rows never speak for a character, and "no record" is never "does not know".
/// </summary>
public sealed class KnowledgeVerdictsTests
{
    private const string Me = "c1";

    /// <summary>
    /// One row of the precedence table. <paramref name="rows"/>: comma-separated <c>knower:state[@learned][~validUntil]</c>,
    /// knower one of self (the perspective's character), other (another character), party, table, dm, public, author.
    /// <paramref name="membership"/>: "-" (never a member), "member", "since:N" or "until:N".
    /// </summary>
    [Theory]
    // Author view: always knows, whatever the rows and visibility say.
    [InlineData("author", "player", "party:unaware,self:forgot", "author", "-", "absent", null, "Knows", "author")]
    [InlineData("dm", "dm", "", "author", "-", "absent", null, "Knows", "author")]
    // character: 1. its own row decides, aware or not.
    [InlineData("character", "player", "self:met,party:unaware", "restricted", "member", "present", null, "Knows", "explicit")]
    [InlineData("character", "player", "self:unaware,party:knows@3", "party", "member", "present", null, "DoesNotKnow", "explicit")]
    [InlineData("character", "player", "self:forgot,public:knows", "public", "member", "present", null, "DoesNotKnow", "explicit")]
    [InlineData("character", "player", "self:unrecognized", "restricted", "-", "present", null, "Knows", "explicit")]
    [InlineData("character", "player", "other:knows", "restricted", "member", "present", null, "NoRecord", "none")]
    // FD2 (review L05) changed this row from Knows (party_present): as of S3 his own row, learned in S5, says he did not know
    // it yet, and a later row is a wall, not a gap to fall through to the party's.
    [InlineData("character", "player", "self:knows@5,party:knows@2", "restricted", "member", "present", 3, "NoRecord", "none")]
    [InlineData("character", "player", "self:knows@5,party:knows@2", "restricted", "member", "present", 5, "Knows", "explicit")]
    [InlineData("character", "player", "self:knows@1~3", "restricted", "member", "present", null, "NoRecord", "none")]
    [InlineData("character", "player", "self:knows@1~3", "restricted", "member", "present", 2, "Knows", "explicit")]
    // character: 2. the party row, through membership and attendance.
    [InlineData("character", "player", "party:knows", "restricted", "member", "absent", null, "Knows", "party")]
    [InlineData("character", "player", "party:knows@3", "restricted", "member", "present", null, "Knows", "party_present")]
    [InlineData("character", "player", "party:knows@3", "restricted", "member", "absent", null, "Uncertain", "party")]
    [InlineData("character", "player", "party:knows@3", "restricted", "member", "notlisted", null, "Uncertain", "party")]
    [InlineData("character", "player", "party:knows@3", "restricted", "member", "notrecorded", null, "Knows", "party_attendance_not_recorded")]
    [InlineData("character", "player", "party:knows@3", "restricted", "since:4", "present", null, "Uncertain", "party")]
    [InlineData("character", "player", "party:knows@3", "restricted", "since:3", "present", null, "Knows", "party_present")]
    [InlineData("character", "player", "party:knows@5", "restricted", "until:3", "present", null, "Uncertain", "party")]
    [InlineData("character", "player", "party:knows@3", "restricted", "until:3", "present", null, "Knows", "party_present")]
    [InlineData("character", "player", "party:knows@2", "restricted", "until:3", "present", null, "Knows", "party_present")]
    // A membership marked former with no until (review of FD1): he left in a session nobody recorded, so attendance
    // decides; with none recorded he is uncertain, not knowing. An undated row is still his.
    [InlineData("character", "player", "party:knows@3", "restricted", "former", "present", null, "Knows", "party_present")]
    [InlineData("character", "player", "party:knows@3", "restricted", "former", "notrecorded", null, "Uncertain", "party")]
    [InlineData("character", "player", "party:knows@3", "restricted", "former", "absent", null, "Uncertain", "party")]
    [InlineData("character", "player", "party:knows", "restricted", "former", "notrecorded", null, "Knows", "party")]
    [InlineData("character", "player", "party:unaware", "party", "former", "notrecorded", null, "DoesNotKnow", "party")]
    [InlineData("character", "player", "party:unaware", "public", "member", "present", null, "DoesNotKnow", "party")]
    [InlineData("character", "player", "party:forgot@2", "public", "since:5", "present", null, "DoesNotKnow", "party")]
    [InlineData("character", "player", "party:suspects@3", "restricted", "member", "present", null, "Knows", "party_present")]
    [InlineData("character", "player", "party:knows@3", "restricted", "-", "present", null, "NoRecord", "none")]
    [InlineData("character", "player", "party:knows@3", "party", "-", "present", null, "NoRecord", "none")]
    // FD2 (review L05) changed this row from Knows (public): the party's row speaks for a member, and as of S3 it says the
    // party learned it only in S4.
    [InlineData("character", "player", "party:knows@4,public:knows", "restricted", "member", "present", 3, "NoRecord", "none")]
    [InlineData("character", "player", "party:knows@4,public:knows", "restricted", "-", "present", 3, "Knows", "public")]
    // character: 3. a public row speaks both ways; it comes after the party's.
    [InlineData("character", "player", "public:knows", "restricted", "-", "present", null, "Knows", "public")]
    [InlineData("character", "player", "public:unaware", "public", "member", "present", null, "DoesNotKnow", "public")]
    [InlineData("character", "player", "party:knows@3,public:unaware", "public", "member", "absent", null, "Uncertain", "party")]
    // character: 4. the visibility default; 5. no record. Table, dm and author rows never speak for a character.
    [InlineData("character", "player", "", "public", "-", "present", null, "Knows", "visibility")]
    [InlineData("character", "player", "", "party", "member", "present", null, "Knows", "visibility")]
    [InlineData("character", "player", "", "party", "-", "present", null, "NoRecord", "none")]
    [InlineData("character", "player", "", "restricted", "member", "present", null, "NoRecord", "none")]
    [InlineData("character", "player", "table:knows,dm:knows,author:knows", "restricted", "member", "present", null, "NoRecord", "none")]
    [InlineData("character", "dm", "table:knows,dm:knows,author:knows", "restricted", "member", "present", null, "NoRecord", "none")]
    // party.
    [InlineData("party", "player", "party:unaware,public:knows", "public", "-", "present", null, "DoesNotKnow", "explicit")]
    [InlineData("party", "player", "party:knows@2", "restricted", "-", "present", null, "Knows", "explicit")]
    [InlineData("party", "player", "party:knows@2", "restricted", "-", "present", 1, "NoRecord", "none")]
    [InlineData("party", "player", "public:knows", "restricted", "-", "present", null, "Knows", "public")]
    [InlineData("party", "player", "public:unaware", "party", "-", "present", null, "DoesNotKnow", "public")]
    [InlineData("party", "player", "", "public", "-", "present", null, "Knows", "visibility")]
    [InlineData("party", "player", "", "party", "-", "present", null, "Knows", "visibility")]
    [InlineData("party", "player", "self:knows,table:knows,dm:knows", "restricted", "-", "present", null, "NoRecord", "none")]
    // table.
    [InlineData("table", "player", "table:knows@2", "restricted", "-", "present", null, "Knows", "explicit")]
    [InlineData("table", "player", "table:unaware,party:knows", "party", "-", "present", null, "DoesNotKnow", "explicit")]
    [InlineData("table", "player", "party:knows@2", "restricted", "-", "present", null, "Knows", "party")]
    [InlineData("table", "player", "party:unaware", "restricted", "-", "present", null, "NoRecord", "none")]
    [InlineData("table", "player", "public:believes", "restricted", "-", "present", null, "Knows", "public")]
    [InlineData("table", "player", "public:unaware", "restricted", "-", "present", null, "NoRecord", "none")]
    // An unaware contained row is not "does not know", but it rules out the visibility default: the table never sees what
    // the party is recorded as not knowing.
    [InlineData("table", "player", "party:unaware", "party", "-", "present", null, "NoRecord", "none")]
    [InlineData("table", "player", "party:unaware", "public", "-", "present", null, "NoRecord", "none")]
    [InlineData("table", "player", "public:unaware", "public", "-", "present", null, "NoRecord", "none")]
    [InlineData("table", "player", "party:forgot@2", "party", "-", "present", null, "NoRecord", "none")]
    [InlineData("table", "player", "party:unaware,public:knows", "public", "-", "present", null, "Knows", "public")]
    [InlineData("table", "player", "", "public", "-", "present", null, "Knows", "visibility")]
    [InlineData("table", "player", "", "party", "-", "present", null, "Knows", "visibility")]
    [InlineData("table", "player", "dm:knows,self:knows", "restricted", "-", "present", null, "NoRecord", "none")]
    // dm in a player campaign.
    [InlineData("dm", "player", "dm:knows", "restricted", "-", "present", null, "Knows", "explicit")]
    [InlineData("dm", "player", "dm:unaware,table:knows,party:knows", "party", "-", "present", null, "DoesNotKnow", "explicit")]
    [InlineData("dm", "player", "table:knows@4,party:knows@2", "restricted", "-", "present", null, "Knows", "table")]
    [InlineData("dm", "player", "table:unaware,party:knows@2", "restricted", "-", "present", null, "Knows", "party")]
    [InlineData("dm", "player", "party:knows@2", "restricted", "-", "present", null, "Knows", "party")]
    [InlineData("dm", "player", "party:unaware", "author", "-", "present", null, "NoRecord", "none")]
    [InlineData("dm", "player", "public:knows", "restricted", "-", "present", null, "Knows", "public")]
    [InlineData("dm", "player", "public:unaware", "public", "-", "present", null, "NoRecord", "none")]
    [InlineData("dm", "player", "party:unaware", "party", "-", "present", null, "NoRecord", "none")]
    [InlineData("dm", "player", "table:unaware,party:unaware", "public", "-", "present", null, "NoRecord", "none")]
    [InlineData("dm", "player", "table:unaware,public:knows", "public", "-", "present", null, "Knows", "public")]
    [InlineData("dm", "player", "", "public", "-", "present", null, "Knows", "visibility")]
    [InlineData("dm", "player", "", "party", "-", "present", null, "Knows", "visibility")]
    [InlineData("dm", "player", "self:knows,author:knows", "restricted", "-", "present", null, "NoRecord", "none")]
    // public.
    [InlineData("public", "player", "public:misbelieves", "restricted", "-", "present", null, "Knows", "explicit")]
    [InlineData("public", "player", "public:unaware", "public", "-", "present", null, "DoesNotKnow", "explicit")]
    [InlineData("public", "player", "party:knows,table:knows,dm:knows", "restricted", "-", "present", null, "NoRecord", "none")]
    [InlineData("public", "player", "", "public", "-", "present", null, "Knows", "visibility")]
    [InlineData("public", "player", "", "party", "-", "present", null, "NoRecord", "none")]
    [InlineData("public", "dm", "", "party", "-", "present", null, "NoRecord", "none")]
    public void Evaluate_PrecedenceTable_GivesTheContractsVerdict(
        string perspective,
        string role,
        string rows,
        string visibility,
        string membership,
        string attendance,
        int? asOf,
        string standing,
        string basis)
    {
        var verdict = KnowledgeVerdicts.Evaluate(Who(perspective, role, membership), Rows(rows), visibility, Attendance(attendance), asOf);

        Assert.Equal(standing, verdict.Standing.ToString());
        Assert.Equal(basis, verdict.Basis);
        Assert.Equal(verdict.Standing == KnowledgeStanding.Knows, verdict.Knows);
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData(5, null, null, true)]
    [InlineData(null, 3, null, false)]
    [InlineData(2, 3, null, false)]
    [InlineData(null, null, 0, true)]
    [InlineData(2, null, 2, true)]
    [InlineData(2, null, 1, false)]
    [InlineData(2, 4, 3, true)]
    [InlineData(2, 4, 4, false)]
    [InlineData(2, 4, 5, false)]
    [InlineData(null, 4, 3, true)]
    public void Applies_LearnedAndValidUntil_BoundTheSessionsARowCovers(int? learned, int? validUntil, int? asOf, bool applies)
    {
        var row = Row("party", "knows", learned, validUntil: validUntil);

        Assert.Equal(applies, KnowledgeVerdicts.Applies(row, asOf));
    }

    [Theory]
    [InlineData("present", "the party learned it in S3 and Ignis was present")]
    [InlineData("absent", "the party learned it in S3; Ignis was absent")]
    [InlineData("notlisted", "the party learned it in S3; no attendance recorded for Ignis in S3")]
    [InlineData("notrecorded", "the party learned it in S3; attendance was not recorded for S3")]
    public void Evaluate_PartyRowThroughAttendance_ExplainsWithSessionAndOwnNameOnly(string attendance, string explanation)
    {
        var who = Character(Me, "Ignis", new PartyMembership(null, null));

        var verdict = KnowledgeVerdicts.Evaluate(who, [Row("party", "knows", 3, "the thing he wants")], "restricted", Attendance(attendance));

        Assert.Equal(explanation, verdict.Explanation);
    }

    [Fact]
    public void Evaluate_JoinedAfterThePartyLearnedIt_SaysBeforeTheyJoined()
    {
        var who = Character(Me, "Serif", new PartyMembership(2, null));

        var verdict = KnowledgeVerdicts.Evaluate(who, [Row("party", "knows", 1)], "party", FakeAttendance.Always(AttendanceAnswer.Present));

        Assert.Equal(KnowledgeStanding.Uncertain, verdict.Standing);
        Assert.Equal("the party learned it in S1, before Serif joined in S2", verdict.Explanation);
    }

    [Fact]
    public void Evaluate_PartyDerivedVerdict_CarriesThePartyRowsKnownAsAndSession()
    {
        var who = Character(Me, "Ignis", new PartyMembership(null, null));

        var verdict = KnowledgeVerdicts.Evaluate(who, [Row("party", "aware", 3, "the thing he wants")], "party", FakeAttendance.Always(AttendanceAnswer.Present));

        Assert.Equal("the thing he wants", verdict.KnownAs);
        Assert.Equal(3, verdict.LearnedSession);
        Assert.Equal("aware", verdict.State);
    }

    /// <summary>
    /// FD1 (review L02): a party-visible target that entered the party's knowledge in S3 is known to a member exactly as a
    /// party row learned in S3 would be, through the same membership window and attendance; the two records of one event
    /// can never disagree. Until is the session the membership ended in, so a member who left in S3 was there for it.
    /// </summary>
    [Theory]
    [InlineData("member", "present", "Knows", "party-visible since S3 and Belmakor Silverwind was present")]
    [InlineData("member", "notrecorded", "Knows", "party-visible since S3; attendance was not recorded for S3")]
    [InlineData("member", "absent", "Uncertain", "party-visible since S3; Belmakor Silverwind was absent")]
    [InlineData("member", "notlisted", "Uncertain", "party-visible since S3; no attendance recorded for Belmakor Silverwind in S3")]
    [InlineData("since:4", "present", "Uncertain", "party-visible since S3, before Belmakor Silverwind joined in S4")]
    [InlineData("since:3", "present", "Knows", "party-visible since S3 and Belmakor Silverwind was present")]
    [InlineData("since:3", "absent", "Uncertain", "party-visible since S3; Belmakor Silverwind was absent")]
    [InlineData("until:2", "present", "Uncertain", "party-visible since S3, after Belmakor Silverwind left in S2")]
    [InlineData("until:2", "notrecorded", "Uncertain", "party-visible since S3, after Belmakor Silverwind left in S2")]
    [InlineData("until:3", "present", "Knows", "party-visible since S3 and Belmakor Silverwind was present")]
    [InlineData("until:5", "notlisted", "Uncertain", "party-visible since S3; no attendance recorded for Belmakor Silverwind in S3")]
    [InlineData("former", "present", "Knows", "party-visible since S3 and Belmakor Silverwind was present")]
    [InlineData("former", "notrecorded", "Uncertain",
        "party-visible since S3; attendance was not recorded for S3, and Belmakor Silverwind left the party in a session not recorded")]
    public void Evaluate_DatedPartyDefault_ReadsLikeAPartyRowLearnedInThatSession(string membership, string attendance, string standing, string explanation)
    {
        var who = Who("character", "player", membership);

        var byVisibility = KnowledgeVerdicts.Evaluate(who, [], "party", Attendance(attendance), targetSinceSession: 3);
        var byPartyRow = KnowledgeVerdicts.Evaluate(who, Rows("party:knows@3"), "restricted", Attendance(attendance));

        Assert.Equal(standing, byVisibility.Standing.ToString());
        Assert.Equal(byPartyRow.Standing, byVisibility.Standing);
        Assert.Equal(explanation, byVisibility.Explanation);
        Assert.Equal(KnowledgeBases.Visibility, byVisibility.Basis);
        Assert.Equal("knows", byVisibility.State);
        Assert.Null(byVisibility.KnownAs);
        Assert.Null(byVisibility.LearnedSession);
    }

    /// <summary>
    /// FD1 (review L02b): Tristan left the party in S1; the bridge at Sorrowmere fell in S3 (a party-visible fact with no
    /// rows). He is uncertain about it, as he is about what a party row learned in S3 says, while Belmakor, present, knows.
    /// </summary>
    [Fact]
    public void Evaluate_FormerMemberOnAPartyVisibleFactEstablishedAfterHeLeft_IsUncertain()
    {
        var attendance = new FakeAttendance().Row(3, "belmakor", true);
        var tristan = Character("tristan", "Tristan", new PartyMembership(null, 1));
        var belmakor = Character("belmakor", "Belmakor", new PartyMembership(null, null));

        var his = KnowledgeVerdicts.Evaluate(tristan, [], "party", attendance, targetSinceSession: 3);
        var hisNow = KnowledgeVerdicts.Evaluate(tristan, [], "party", attendance, asOfSession: 4, targetSinceSession: 3);
        var theirs = KnowledgeVerdicts.Evaluate(belmakor, [], "party", attendance, targetSinceSession: 3);

        Assert.Equal((KnowledgeStanding.Uncertain, "party-visible since S3, after Tristan left in S1"), (his.Standing, his.Explanation));
        Assert.Equal(KnowledgeStanding.Uncertain, hisNow.Standing);
        Assert.Equal((KnowledgeStanding.Knows, "party-visible since S3 and Belmakor was present"), (theirs.Standing, theirs.Explanation));
    }

    /// <summary>
    /// FD1: with no target session the party default is undated and every member, current or former, knows a party-visible
    /// target as before; public visibility is never dated, whatever the attendance.
    /// </summary>
    [Theory]
    [InlineData("member", "party", null, "party-visible: the party knows it")]
    [InlineData("until:1", "party", null, "party-visible: the party knows it")]
    [InlineData("since:9", "party", null, "party-visible: the party knows it")]
    [InlineData("member", "public", 3, "public: everyone knows it")]
    [InlineData("until:1", "public", 3, "public: everyone knows it")]
    [InlineData("-", "public", 3, "public: everyone knows it")]
    public void Evaluate_UndatedPartyOrPublicDefault_KnowsAsBefore(string membership, string visibility, int? since, string explanation)
    {
        var verdict = KnowledgeVerdicts.Evaluate(Who("character", "player", membership), [], visibility, Attendance("absent"), targetSinceSession: since);

        Assert.Equal((KnowledgeStanding.Knows, explanation), (verdict.Standing, verdict.Explanation));
    }

    /// <summary>
    /// FD1: the dated default speaks for the party, the table and the dm as a party row learned that session does: known
    /// now, and "not yet" in a read as of an earlier session; a non-member and the public still have no record of a party
    /// target, dated or not.
    /// </summary>
    [Theory]
    [InlineData("party", null, "Knows", "party-visible since S3: the party knows it")]
    [InlineData("table", null, "Knows", "party-visible since S3: the party knows it")]
    [InlineData("dm", null, "Knows", "party-visible since S3: the party knows it")]
    [InlineData("party", 3, "Knows", "party-visible since S3: the party knows it")]
    [InlineData("party", 2, "NoRecord", "not yet: party-visible since S3")]
    [InlineData("table", 2, "NoRecord", "not yet: party-visible since S3")]
    [InlineData("dm", 2, "NoRecord", "not yet: party-visible since S3")]
    [InlineData("member", 2, "NoRecord", "not yet: party-visible since S3")]
    [InlineData("member", 3, "Knows", "party-visible since S3; attendance was not recorded for S3")]
    [InlineData("stranger", null, "NoRecord", "no record")]
    [InlineData("public", null, "NoRecord", "no record")]
    public void Evaluate_DatedPartyDefault_SpeaksForEveryPartySideViewFromItsSession(string perspective, int? asOf, string standing, string explanation)
    {
        var who = perspective switch
        {
            "member" => Who("character", "player", "member"),
            "stranger" => Who("character", "player", "-"),
            _ => Of(perspective),
        };

        var verdict = KnowledgeVerdicts.Evaluate(who, [], "party", Attendance("notrecorded"), asOf, targetSinceSession: 3);

        Assert.Equal((standing, explanation), (verdict.Standing.ToString(), verdict.Explanation));
    }

    /// <summary>
    /// FD2 (review L05b): the party met Morwen Vashkar in S3 as "the veiled woman"; she is party-visible and was written
    /// timelessly. As of S2 no party-side view knows her: the row that would decide was learned later, so the verdict
    /// stops there ("not yet") instead of falling through to the visibility default, which showed her true name, summary
    /// and slug. As of S3 and now the row decides, under her disguise.
    /// </summary>
    [Theory]
    [InlineData("party", "party:met@3", "party", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("member", "party:met@3", "party", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("table", "party:met@3", "party", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("dm", "party:met@3", "party", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("party", "party:met@3", "public", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("public", "public:met@3", "public", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("party", "public:met@3", "public", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("member", "public:met@3", "public", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("member", "self:met@3,party:met@1", "party", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("table", "table:met@3,party:met@1", "party", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("table", "party:unaware,public:met@3", "public", 2, "NoRecord", "not yet: learned in S3")]
    [InlineData("party", "party:met@3", "party", 3, "Knows", "the party's own record: met, S3")]
    [InlineData("member", "party:met@3", "party", null, "Knows", "the party learned it in S3; attendance was not recorded for S3")]
    public void Evaluate_AsOfBeforeTheDecidingRowWasLearned_IsNotYetNeverTheVisibilityDefault(
        string perspective, string rows, string visibility, int? asOf, string standing, string explanation)
    {
        var who = perspective == "member" ? Who("character", "player", "member") : Of(perspective);
        var entries = Rows(rows).Select(r => r.KnowerKind is "party" or "public" or "table" && r.State == "met" ? r with { KnownAs = "the veiled woman" } : r).ToList();

        var verdict = KnowledgeVerdicts.Evaluate(who, entries, visibility, Attendance("notrecorded"), asOf);

        Assert.Equal((standing, explanation), (verdict.Standing.ToString(), verdict.Explanation));
        if (verdict.Standing == KnowledgeStanding.NoRecord)
        {
            Assert.Null(verdict.KnownAs);
            Assert.Null(verdict.State);
            Assert.Null(verdict.LearnedSession);
            Assert.Equal(KnowledgeBases.None, verdict.Basis);
        }
        else
        {
            Assert.Equal("the veiled woman", verdict.KnownAs);
        }
    }

    /// <summary>
    /// FD2 (review L05b, the fact): "Morwen Vashkar poisoned the well." is party-visible and established in S1, and the
    /// party learned it only in S3, in its own words. As of S2 a member present in S1 has no record of it (the party's row
    /// is the wall), though the dated default alone would have given him the true statement.
    /// </summary>
    [Fact]
    public void Evaluate_AsOfBeforeThePartysOwnPhrasingWasLearned_NeverFallsBackToTheDatedDefault()
    {
        var who = Character(Me, "Belmakor", new PartyMembership(null, null));
        var attendance = new FakeAttendance().Row(1, Me, true).Row(3, Me, true);
        KnowledgeEntry[] rows = [Row("party", "knows", 3, "Someone poisoned the well.")];

        var then = KnowledgeVerdicts.Evaluate(who, rows, "party", attendance, asOfSession: 2, targetSinceSession: 1);
        var now = KnowledgeVerdicts.Evaluate(who, rows, "party", attendance, targetSinceSession: 1);
        var withoutTheRow = KnowledgeVerdicts.Evaluate(who, [], "party", attendance, asOfSession: 2, targetSinceSession: 1);

        Assert.Equal((KnowledgeStanding.NoRecord, (string?)null), (then.Standing, then.KnownAs));
        Assert.Equal((KnowledgeStanding.Knows, "Someone poisoned the well."), (now.Standing, now.KnownAs));
        Assert.Equal(KnowledgeStanding.Knows, withoutTheRow.Standing);
    }

    /// <summary>
    /// FD2 for contained knowers: a later row of a knower the table or the dm merely contains reads like an unaware one. It
    /// does not decide (an earlier aware row of another contained knower still does: the public heard it in S1), and it
    /// rules out the visibility default.
    /// </summary>
    [Theory]
    [InlineData("table", "party:knows@3,public:knows@1", "party", 2, "Knows", "public")]
    [InlineData("dm", "table:knows@4,party:knows@2", "restricted", 3, "Knows", "party")]
    [InlineData("dm", "table:knows@4", "party", 3, "NoRecord", "none")]
    [InlineData("table", "party:unaware@3", "party", 2, "NoRecord", "none")]
    [InlineData("table", "public:knows@3", "public", 2, "NoRecord", "none")]
    public void Evaluate_LaterContainedRow_DoesNotDecideButStopsTheDefault(string perspective, string rows, string visibility, int asOf, string standing, string basis)
    {
        var verdict = KnowledgeVerdicts.Evaluate(Of(perspective), Rows(rows), visibility, Attendance("present"), asOf);

        Assert.Equal((standing, basis), (verdict.Standing.ToString(), verdict.Basis));
    }

    /// <summary>
    /// A verdict stopped by a later row names the session of the row that stopped it: of several later contained rows,
    /// the earliest. A later unaware or forgot row is worded by the session it is dated from, never as "learned": forgetting
    /// moves a row's session to the one it was forgotten in, so "learned in S5" would misstate it (review of FD2). It is
    /// still a wall: the forgotten row may have carried the name the knower used.
    /// </summary>
    [Theory]
    [InlineData("table", "party:knows@4,public:knows@3", "party", 2, "not yet: learned in S3")]
    [InlineData("table", "party:knows@3,public:knows@4", "party", 2, "not yet: learned in S3")]
    [InlineData("dm", "table:knows@5,party:knows@4,public:knows@3", "party", 2, "not yet: learned in S3")]
    [InlineData("member", "self:forgot@5,party:knows@2", "restricted", 3, "no record as of S3 (the row dates from S5)")]
    [InlineData("member", "self:unaware@5", "party", 3, "no record as of S3 (the row dates from S5)")]
    [InlineData("member", "party:forgot@4,public:knows", "public", 2, "no record as of S2 (the row dates from S4)")]
    [InlineData("party", "party:forgot@4", "party", 2, "no record as of S2 (the row dates from S4)")]
    [InlineData("table", "party:forgot@3", "party", 2, "no record as of S2 (the row dates from S3)")]
    [InlineData("table", "party:forgot@3,public:knows@4", "public", 2, "no record as of S2 (the row dates from S3)")]
    public void Evaluate_AsOfStoppedByALaterRow_NamesTheEarliestRowsSessionAndNeverSaysLearnedOfAForgetting(
        string perspective, string rows, string visibility, int asOf, string explanation)
    {
        var who = perspective == "member" ? Who("character", "player", "member") : Of(perspective);
        var entries = Rows(rows).Select(r => r with { KnownAs = "the veiled woman" }).ToList();

        var verdict = KnowledgeVerdicts.Evaluate(who, entries, visibility, Attendance("present"), asOf);

        Assert.Equal((KnowledgeStanding.NoRecord, explanation), (verdict.Standing, verdict.Explanation));
        Assert.Equal(((string?)null, (string?)null, (int?)null, KnowledgeBases.None), (verdict.State, verdict.KnownAs, verdict.LearnedSession, verdict.Basis));
    }

    [Theory]
    [InlineData("dm")]
    [InlineData("table")]
    [InlineData("public")]
    [InlineData("party")]
    public void Evaluate_NoRecord_IsWordedAsNoRecordNeverAsDoesNotKnow(string perspective)
    {
        var verdict = KnowledgeVerdicts.Evaluate(Of(perspective), [Row("author", "knows")], "restricted", FakeAttendance.Always(AttendanceAnswer.Present));

        Assert.Equal(KnowledgeStanding.NoRecord, verdict.Standing);
        Assert.Equal("no record", verdict.Explanation);
        Assert.Null(verdict.State);
        Assert.DoesNotContain("not know", verdict.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("table", "party", "public")]
    [InlineData("table", "party", "party")]
    [InlineData("table", "public", "public")]
    [InlineData("dm", "party", "public")]
    [InlineData("dm", "table", "party")]
    [InlineData("dm", "public", "public")]
    public void Evaluate_OnlyUnawareContainedRows_IsNoRecordNotTheVisibilityDefault(string perspective, string knower, string visibility)
    {
        var verdict = KnowledgeVerdicts.Evaluate(Of(perspective), [Row(knower, "unaware", 3)], visibility, FakeAttendance.Always(AttendanceAnswer.Present));

        Assert.Equal(KnowledgeStanding.NoRecord, verdict.Standing);
        Assert.False(verdict.Knows);
        Assert.Equal("no record", verdict.Explanation);
        Assert.Null(verdict.State);
        Assert.Null(verdict.LearnedSession);
        Assert.Equal(KnowledgeBases.None, verdict.Basis);
    }

    [Theory]
    [InlineData("character")]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("dm")]
    [InlineData("public")]
    public void Evaluate_Explanation_NeverRepeatsKnownAsViaOrNote(string perspective)
    {
        var rows = new[] { "self", "party", "table", "dm", "public", "author" }
            .Select(k => new KnowledgeEntry(k == "self" ? "character" : k, k == "self" ? Me : null, "knows", "Keras", 3, null, "told", "the Axiom Cage",
                "secret note"))
            .ToList();
        foreach (var drop in Enumerable.Range(0, rows.Count + 1))
        {
            // As of S2 every row here is learned later (FD2's "not yet"); a dated party default (FD1) explains itself too.
            foreach (var (asOf, since, visibility) in new (int?, int?, string)[] { (null, null, "restricted"), (2, null, "party"), (null, 3, "party"), (2, 3, "party") })
            {
                var subset = rows.Skip(drop).ToList();
                var verdict = KnowledgeVerdicts.Evaluate(Who(perspective, "player", "member"), subset, visibility,
                    FakeAttendance.Always(AttendanceAnswer.Present), asOf, since);

                Assert.DoesNotContain("Keras", verdict.Explanation, StringComparison.Ordinal);
                Assert.DoesNotContain("Axiom", verdict.Explanation, StringComparison.Ordinal);
                Assert.DoesNotContain("secret", verdict.Explanation, StringComparison.Ordinal);
                Assert.DoesNotContain("told", verdict.Explanation, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Evaluate_AuthorView_HasNoKnownAs()
    {
        var verdict = KnowledgeVerdicts.Evaluate(Author(), [Row("author", "knows", knownAs: "Keras")], "author", FakeAttendance.Always(AttendanceAnswer.Absent));

        Assert.Equal(KnowledgeStanding.Knows, verdict.Standing);
        Assert.Null(verdict.KnownAs);
    }

    [Fact]
    public void Evaluate_CharacterPerspectiveWithoutItsId_Throws()
    {
        var who = new PerspectiveContext(Perspective.Parse("character:belmakor"), "player");

        Assert.Throws<ArgumentException>(() => KnowledgeVerdicts.Evaluate(who, [], "public", FakeAttendance.Always(AttendanceAnswer.Present)));
    }

    private static PerspectiveContext Who(string perspective, string role, string membership)
    {
        if (perspective != "character")
        {
            return perspective == "author" ? Author(role) : Of(perspective, role);
        }

        PartyMembership? member = membership switch
        {
            "-" => null,
            "member" => new PartyMembership(null, null),
            "former" => new PartyMembership(null, null) { Former = true },
            _ when membership.StartsWith("since:", StringComparison.Ordinal) => new PartyMembership(Number(membership[6..]), null),
            _ when membership.StartsWith("until:", StringComparison.Ordinal) => new PartyMembership(null, Number(membership[6..])),
            _ => throw new ArgumentException(membership),
        };
        return Character(Me, "Belmakor Silverwind", member, role);
    }

    private static List<KnowledgeEntry> Rows(string spec)
    {
        var rows = new List<KnowledgeEntry>();
        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = item.IndexOf(':');
            var knower = item[..colon];
            var rest = item[(colon + 1)..];
            int? until = null;
            int? learned = null;
            var tilde = rest.IndexOf('~');
            if (tilde >= 0)
            {
                until = Number(rest[(tilde + 1)..]);
                rest = rest[..tilde];
            }

            var at = rest.IndexOf('@');
            if (at >= 0)
            {
                learned = Number(rest[(at + 1)..]);
                rest = rest[..at];
            }

            var (kind, id) = knower switch
            {
                "self" => ("character", Me),
                "other" => ("character", "c2"),
                _ => (knower, (string?)null),
            };
            rows.Add(new KnowledgeEntry(kind, id, rest, null, learned, until));
        }

        return rows;
    }

    private static IAttendance Attendance(string answer) => FakeAttendance.Always(answer switch
    {
        "present" => AttendanceAnswer.Present,
        "absent" => AttendanceAnswer.Absent,
        "notlisted" => AttendanceAnswer.NotListed,
        "notrecorded" => AttendanceAnswer.NotRecorded,
        _ => throw new ArgumentException(answer),
    });

    private static int Number(string text) => int.Parse(text, CultureInfo.InvariantCulture);
}
