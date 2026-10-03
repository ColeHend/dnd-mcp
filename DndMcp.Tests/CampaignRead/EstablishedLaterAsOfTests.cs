using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using DndMcp.Tests.CampaignWrite;
using Microsoft.Data.Sqlite;
using Xunit;
using KK = DndMcp.Domain.Campaign.CampaignValues.KnowerKinds;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: a read as of session n shows a non-author view no fact established in a later session, unless a knowledge
/// row that applied at n records the view knowing it then: a character's own row learned in no session (backstory), or a
/// row learned at or before n. A group's row (party, table, public, dm) learned in no session, which is what a batch with
/// no session writes, counts from the established session. Search, get (the fact and an entity's linked facts and
/// knowledge lines), the knowledge resource, the ledger and the check all agree, because they share one verdict rule
/// (<c>ReadScope.VerdictVisibility</c> and <c>ReadScope.VerdictEntries</c>). The author view still shows the fact, with its
/// established session, which marks it as later than the read.
///
/// <para>
/// Why it fails silently: the rows exist as of n (a fact written between sessions is timeless for replay), so every
/// reader returned it, and the party's knowledge came from its visibility default: "party-visible" read as "the party knew
/// it then". Nothing looked wrong in a read of now. The world: sessions 1-3 played; "The bridge fell." is party-visible,
/// played and established in session 3, which the party learned in session 3 and Hero knew all along (backstory); "A comet
/// crossed the sky." is public and established in session 3 with no rows; "The harbor is closed." is party-visible and
/// established in session 1 (in play as of 2: visibility still counts for it); "The tower stands." has no established
/// session at all (timeless, known by its visibility at every point).
/// </para>
/// </summary>
public sealed class EstablishedLaterAsOfTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;
    private readonly SeededEntity _hero;
    private readonly IReadOnlyList<SeededSession> _sessions;
    private readonly SeededFact _bridge;
    private readonly SeededFact _comet;
    private readonly SeededFact _harbor;
    private readonly SeededFact _tower;

    public EstablishedLaterAsOfTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Sky", role: CampaignValues.Roles.Player);
        _hero = _seed.Entity(_campaign.Id, "character", "Hero", subtype: "pc");
        _seed.MemberOf(_campaign.Id, _hero.Id, _campaign.Party!.Id);
        var sessions = Enumerable.Range(1, 3).Select(n => _seed.Session(_campaign.Id, n)).ToList();
        _sessions = sessions;
        _bridge = _seed.Fact(_campaign.Id, "The bridge fell.", visibility: V.Party, canonStatus: "played", establishedSessionId: sessions[2].EntityId);
        _seed.Knowledge(_campaign.Id, KK.Party, factId: _bridge.Id, learnedSessionId: sessions[2].EntityId);
        _seed.Knowledge(_campaign.Id, KK.Character, _hero.Id, factId: _bridge.Id, how: "backstory");
        _comet = _seed.Fact(_campaign.Id, "A comet crossed the sky.", visibility: V.Public, establishedSessionId: sessions[2].EntityId);
        _harbor = _seed.Fact(_campaign.Id, "The harbor is closed.", visibility: V.Party, establishedSessionId: sessions[0].EntityId);
        _tower = _seed.Fact(_campaign.Id, "The tower stands.", visibility: V.Party);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private CampaignRow Row => _seed.LoadCampaign(_campaign.Id);

    private SearchResult Search(string query, string perspective, int? asOf) =>
        new CampaignSearch(_db.Database).Search(Row, new SearchRequest(query, Perspective: Perspective.Parse(perspective), AsOfSession: asOf));

    private FactDetail? TryGet(string perspective, int? asOf, string reference)
    {
        try
        {
            return Assert.Single(new EntityReader(_db.Database).Get(Row, [reference], EntityIncludes.All, Perspective.Parse(perspective), asOf).Facts);
        }
        catch (DndInputException ex)
        {
            // A fact the view may not see is refused exactly like a handle that names nothing.
            Assert.Contains("nothing by that handle for this perspective", ex.Message, StringComparison.Ordinal);
            return null;
        }
    }

    private PerspectiveKnowledge KnownTo(string perspective, int? asOf) =>
        new KnowledgeLedger(_db.Database).KnownTo(Row, Perspective.Parse(perspective), asOf, limit: 50);

    private LedgerCell Cell(string fact, string perspective, int? asOf) =>
        new KnowledgeLedger(_db.Database).Build(Row, null, [fact], [perspective], asOf).Rows.Single().Cells.Single();

    /// <summary>
    /// The bridge as of session 2: only Hero, whose backstory row applied then, knew it; the party learned it in session 3,
    /// and its party visibility says nothing about a time before it happened. From session 3 on, and now, the party knows it.
    /// </summary>
    [Theory]
    [InlineData("party", 2, false)]
    [InlineData("table", 2, false)]
    [InlineData("dm", 2, false)]
    [InlineData("public", 2, false)]
    [InlineData("character:hero", 2, true)]
    [InlineData("character:hero", 1, true)]
    [InlineData("party", 3, true)]
    [InlineData("party", null, true)]
    [InlineData("table", 3, true)]
    [InlineData("author", 2, true)]
    public void EveryReader_AFactAsOfBeforeItsEstablishedSession_IsShownOnlyToAViewWhoseRowAppliedThen(string perspective, int? asOf, bool shown)
    {
        var search = Search("bridge", perspective, asOf);
        var get = TryGet(perspective, asOf, _bridge.SeqHandle);
        var known = KnownTo(perspective, asOf);
        var cell = Cell(_bridge.SeqHandle, perspective, asOf);

        Assert.Equal(shown, search.Facts.Any(f => f.Ref == _bridge.SeqHandle));
        Assert.Equal(shown, get is not null);
        Assert.Equal(shown, known.Facts.Any(f => f.Ref == _bridge.SeqHandle));
        Assert.Equal(shown ? Standings.Knows : Standings.NoRecord, cell.Standing);
        if (!shown)
        {
            LeakAssert.Clean(new object[] { search, known }, ["bridge", _bridge.SeqHandle + "\""], $"{perspective} as of {asOf}");
        }
    }

    /// <summary>
    /// A group's row with no learned session on a fact established in session 3 (what a batch with no session writes:
    /// prep between sessions, or any write while nothing is live) counts from session 3: as of 2 that group's view, and a
    /// member reading through the party's row, is shown nothing of it; from 3 on, and now, the row applies. Read as timeless,
    /// it showed the party as of 2 what happened in 3. The fact is restricted, so the row is all that makes it known.
    /// </summary>
    [Theory]
    [InlineData(KK.Party, "party")]
    [InlineData(KK.Party, "character:hero")]
    [InlineData(KK.Table, "table")]
    [InlineData(KK.Public, "public")]
    [InlineData(KK.Dm, "dm")]
    public void EveryReader_AGroupsRowWithNoSessionOnALaterFact_CountsFromTheEstablishedSession(string knower, string perspective)
    {
        var vow = _seed.Fact(_campaign.Id, "The captain swore a vow.", canonStatus: "played", establishedSessionId: _sessions[2].EntityId);
        _seed.Knowledge(_campaign.Id, knower, factId: vow.Id);

        foreach (var (asOf, shown) in new (int?, bool)[] { (2, false), (1, false), (3, true), (null, true) })
        {
            var cell = Cell(vow.SeqHandle, perspective, asOf);
            Assert.Equal(shown, Search("vow", perspective, asOf).Facts.Any(f => f.Ref == vow.SeqHandle));
            Assert.Equal(shown, TryGet(perspective, asOf, vow.SeqHandle) is not null);
            Assert.Equal(shown, KnownTo(perspective, asOf).Facts.Any(f => f.Ref == vow.SeqHandle));
            Assert.Equal(shown ? Standings.Knows : Standings.NoRecord, cell.Standing);
        }
    }

    /// <summary>
    /// Kept as stored: a character's own row with no session is backstory (row 19: Hero knew it before the table did), and
    /// a group's row with a learned session says when the group learned it, even before the story established it (the
    /// author recorded the party hearing of the vow in session 1). Only a group's sessionless row waits for the session.
    /// </summary>
    [Theory]
    [InlineData(KK.Character, false, true)]
    [InlineData(KK.Party, true, true)]
    [InlineData(KK.Party, false, false)]
    public void Cell_ARowOnALaterFact_AppliesAsOfAnEarlierSessionOnlyWhenItSaysSo(string knower, bool learnedInSession1, bool knowsAsOf2)
    {
        var vow = _seed.Fact(_campaign.Id, "The captain swore a vow.", canonStatus: "played", establishedSessionId: _sessions[2].EntityId);
        _seed.Knowledge(_campaign.Id, knower, knower == KK.Character ? _hero.Id : null, factId: vow.Id,
            learnedSessionId: learnedInSession1 ? _sessions[0].EntityId : null);

        var perspective = knower == KK.Character ? "character:hero" : "party";

        Assert.Equal(knowsAsOf2 ? Standings.Knows : Standings.NoRecord, Cell(vow.SeqHandle, perspective, 2).Standing);
        Assert.Equal(Standings.Knows, Cell(vow.SeqHandle, perspective, 3).Standing);
    }

    /// <summary>A public fact established later: the public, and everyone else, knew nothing of it before its session.</summary>
    [Theory]
    [InlineData("public", 2, false)]
    [InlineData("party", 2, false)]
    [InlineData("character:hero", 2, false)]
    [InlineData("public", 3, true)]
    [InlineData("character:hero", 3, true)]
    [InlineData("public", null, true)]
    public void Search_APublicFactAsOfBeforeItsEstablishedSession_IsNotKnownByItsVisibility(string perspective, int? asOf, bool found)
    {
        Assert.Equal(found, Search("comet", perspective, asOf).Facts.Any(f => f.Ref == _comet.SeqHandle));
        Assert.Equal(found, KnownTo(perspective, asOf).Facts.Any(f => f.Ref == _comet.SeqHandle));
    }

    /// <summary>
    /// The rule looks only at facts established after the session asked for: a fact established in or before it, and a
    /// fact with no established session (timeless world-building), are known by their visibility at every point.
    /// </summary>
    [Theory]
    [InlineData("harbor", 1)]
    [InlineData("harbor", 2)]
    [InlineData("tower", 0)]
    [InlineData("tower", 2)]
    public void Search_AFactInPlayAsOfTheSession_IsStillKnownByItsVisibility(string word, int asOf)
    {
        var fact = word == "harbor" ? _harbor : _tower;

        Assert.Equal(fact.SeqHandle, Assert.Single(Search(word, "party", asOf).Facts).Ref);
        Assert.Equal((Standings.Knows, "knows"), (Cell(fact.SeqHandle, "party", asOf).Standing, Cell(fact.SeqHandle, "party", asOf).Text));
    }

    /// <summary>
    /// The author view decides nothing here: it sees every fact that existed then, and its author detail carries the
    /// established session, so a get as of 2 says "established S3" (later than the read) rather than hiding the fact.
    /// </summary>
    [Fact]
    public void Get_AuthorAsOfBeforeTheEstablishedSession_ShowsTheFactWithItsLaterSession()
    {
        var fact = TryGet("author", 2, _bridge.SeqHandle)!;

        Assert.Equal("The bridge fell.", fact.Text);
        Assert.Equal(3, fact.Author!.Fact.EstablishedSession);
        Assert.Contains(Search("bridge", "author", 2).Facts, f => f.Ref == _bridge.SeqHandle);
    }

    /// <summary>
    /// The check follows the same verdicts: as of 2 the party does not know the bridge (no record), while Hero knows it,
    /// so Hero singing it to the party as of 2 puts it at risk, and as of 3 (the party learned it) no longer does.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void Check_AsOfASession_KnowsTheLaterFactOnlyThroughARowThatAppliedThen(int asOf, bool atRisk)
    {
        var check = new KnowledgeCheck(_db.Database);

        var party = check.Check(Row, new CheckRequest("The bridge fell into the river.", Perspective.Parse("party"), AsOfSession: asOf));
        var song = check.Check(Row, new CheckRequest("The bridge fell into the river.", Perspective.Parse("character:hero"), Diegetic: true, AsOfSession: asOf));

        Assert.Equal(!atRisk, party.Related.Any(f => f.Ref == _bridge.SeqHandle));
        Assert.Equal(atRisk, party.UnknownFacts.Any(f => f.Ref == _bridge.SeqHandle && f.Standing == Standings.NoRecord));
        Assert.Contains(song.Related, f => f.Ref == _bridge.SeqHandle);
        Assert.Equal(atRisk, song.SecretsAtRisk.Any(r => r.Fact.Ref == _bridge.SeqHandle && r.Reason == RiskReasons.RelatedToText));
    }
}

/// <summary>
/// Invariant: the stage-3a repro through every reader and every non-author view of the Belmakor fixture: as of session 2,
/// no view is shown the errand facts (f:3, f:4) or Belmakor's level 12 (f:9), all established in session 3 and known to
/// the party only from session 3 (f:9 by its visibility alone), and none of their knowledge appears.
/// <c>campaign_search {"query": "old king", "as_of_session": 2, "perspective": "party"}</c> listed f:3.
/// </summary>
public sealed class BelmakorEstablishedLaterAsOfTests(BelmakorFixture fixture) : IClassFixture<BelmakorFixture>
{
    public static TheoryData<string> Perspectives() => new(BelmakorFixture.NonAuthorPerspectives);

    private IReadOnlyList<string> Later => [fixture.F3.SeqHandle, fixture.F4.SeqHandle, fixture.F9.SeqHandle];

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Search_OldKingAsOfSession2_ListsNoFactEstablishedInSession3(string perspective)
    {
        var before = new CampaignSearch(fixture.Db.Database).Search(fixture.CampaignRow,
            new SearchRequest("old king", Perspective: Perspective.Parse(perspective), AsOfSession: 2));
        var levels = new CampaignSearch(fixture.Db.Database).Search(fixture.CampaignRow,
            new SearchRequest("level", Perspective: Perspective.Parse(perspective), AsOfSession: 2));

        Assert.DoesNotContain(before.Facts, f => Later.Contains(f.Ref));
        Assert.DoesNotContain(levels.Facts, f => Later.Contains(f.Ref));
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void KnownToAndLedger_AsOfSession2_HaveNoFactEstablishedInSession3(string perspective)
    {
        var known = new KnowledgeLedger(fixture.Db.Database).KnownTo(fixture.CampaignRow, Perspective.Parse(perspective), 2, limit: 50);
        var ledger = new KnowledgeLedger(fixture.Db.Database).Build(fixture.CampaignRow, null, Later, [perspective], 2);

        Assert.DoesNotContain(known.Facts, f => Later.Contains(f.Ref));
        Assert.All(ledger.Rows, r => Assert.NotEqual(Standings.Knows, r.Cells.Single().Standing));
    }

    /// <summary>The errand thread as of session 2: its session-3 facts are not among its facts, nor in its knowledge lines.</summary>
    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Get_TheErrandAsOfSession2_HasNoFactOrKnowledgeLineEstablishedInSession3(string perspective)
    {
        GetResult result;
        try
        {
            result = new EntityReader(fixture.Db.Database).Get(fixture.CampaignRow, [fixture.Errand.Handle], EntityIncludes.All,
                Perspective.Parse(perspective), 2);
        }
        catch (DndInputException)
        {
            return; // The public cannot see the errand at all.
        }

        var errand = Assert.Single(result.Entities);
        Assert.DoesNotContain(errand.Facts!, f => Later.Contains(f.Ref));
        Assert.DoesNotContain(errand.Knowledge!, k => Later.Contains(k.Target));
        foreach (var fact in Later)
        {
            Assert.Throws<DndInputException>(() => new EntityReader(fixture.Db.Database).Get(fixture.CampaignRow, [fact], EntityIncludes.All,
                Perspective.Parse(perspective), 2));
        }
    }

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void Check_AsOfSession2_NeverCallsAFactEstablishedInSession3Known(string perspective)
    {
        var result = new KnowledgeCheck(fixture.Db.Database).Check(fixture.CampaignRow,
            new CheckRequest("After the fight the old king sent the party to fetch it; don't even touch it. Level 12.",
                Perspective.Parse(perspective), AsOfSession: 2));

        Assert.DoesNotContain(result.Related, f => Later.Contains(f.Ref));
        Assert.DoesNotContain(result.MistakenBeliefs, f => Later.Contains(f.Ref));
    }

    /// <summary>
    /// Row 19 stands: the rule takes away knowledge by visibility, not knowledge on record. Belmakor's backstory row on his
    /// Contingency (f:6, established in session 2) has no session, so as of session 1 he knew it; the table did not.
    /// </summary>
    [Fact]
    public void LedgerAndGet_Row19BelmakorsBackstoryRowAsOfSession1_StillKnowsTheContingency()
    {
        var belmakor = new KnowledgeLedger(fixture.Db.Database).Build(fixture.CampaignRow, null, [fixture.F6.SeqHandle],
            ["character:belmakor", "table"], 1).Rows.Single().Cells;

        Assert.Equal([Standings.Knows, Standings.NoRecord], belmakor.Select(c => c.Standing));
        Assert.Single(new EntityReader(fixture.Db.Database).Get(fixture.CampaignRow, [fixture.F6.SeqHandle], EntityIncludes.All,
            Perspective.Parse("character:belmakor"), 1).Facts);
    }
}

/// <summary>
/// Invariant: the established-later rule holds for knowledge written the way the tools write it, not only for seeded rows.
/// A campaign_write fact op naming its established_session, or a campaign_knowledge record, made while no session is
/// live writes the party's row with no learned session; as of an earlier session the party is still shown nothing of the
/// fact. The stage-3a review reached the defect exactly this way: <c>known_by party</c> on a session-3 fact written
/// between sessions, then a party search as of session 2.
/// </summary>
public sealed class EstablishedLaterWrittenAsOfTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _campaign;

    public EstablishedLaterWrittenAsOfTests()
    {
        _campaign = _f.Campaign("Sky", CampaignValues.Roles.Player, myCharacter: "Hero");
        for (var n = 1; n <= 3; n++)
        {
            _f.Played(_campaign, n);
        }
    }

    public void Dispose() => _f.Dispose();

    private bool Shown(string fact, string perspective, int? asOf)
    {
        var row = _f.Reload(_campaign);
        var search = new CampaignSearch(_f.Db.Database).Search(row, new SearchRequest("bridge", Perspective: Perspective.Parse(perspective), AsOfSession: asOf));
        var known = new KnowledgeLedger(_f.Db.Database).KnownTo(row, Perspective.Parse(perspective), asOf, limit: 50);
        var cell = new KnowledgeLedger(_f.Db.Database).Build(row, null, [fact], [perspective], asOf).Rows.Single().Cells.Single();
        var shown = search.Facts.Any(f => f.Ref == fact);
        Assert.Equal(shown, known.Facts.Any(f => f.Ref == fact));
        Assert.Equal(shown, cell.Standing == Standings.Knows);
        return shown;
    }

    // The learned session of the one knowledge row a fact has (null: none recorded).
    private long? LearnedSession(string fact) =>
        _f.Query<long?>(
            "SELECT s.number FROM knowledge k JOIN fact f ON f.id = k.fact_id LEFT JOIN session s ON s.entity_id = k.learned_session_id WHERE f.seq = @seq",
            new { seq = long.Parse(fact[2..], System.Globalization.CultureInfo.InvariantCulture) }).Single();

    /// <summary>
    /// The party's row written outside a session (a fact op's known_by on a party-visible fact; a record on a restricted
    /// one) carries no learned session; the party is shown the fact from its established session on, never before.
    /// </summary>
    [Theory]
    [InlineData("campaign_write")]
    [InlineData("campaign_knowledge")]
    public void PartyReads_ALaterFactThePartyWasGivenOutsideASession_AreEmptyBeforeItsSession(string how)
    {
        var fact = _f.Apply(_campaign, WriteContext.Default, new CampaignOpSpec
        {
            Op = "fact", Statement = "The bridge fell.", CanonStatus = CampaignValues.CanonStatuses.Played, EstablishedSession = 3,
            Visibility = how == "campaign_write" ? CampaignValues.Visibilities.Party : CampaignValues.Visibilities.Restricted,
            KnownBy = how == "campaign_write" ? [Op.Knower("party")] : null,
        }).Applied.Single().Ref;
        if (how == "campaign_knowledge")
        {
            _f.Knowledge.Record(_campaign, [fact], [Op.Knower("party")], WriteContext.Default);
        }

        Assert.Null(LearnedSession(fact));
        Assert.False(Shown(fact, "party", 2));
        Assert.False(Shown(fact, "character:hero", 2));
        Assert.True(Shown(fact, "party", 3));
        Assert.True(Shown(fact, "party", null));
    }

    /// <summary>
    /// The one row the rule keeps: a character's own row written outside a session reads as backstory (row 19's rule), so
    /// Hero, given the fact by name between sessions, knows it as of session 2. The author decides it by giving a session.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(3, false)]
    public void HeroReads_ALaterFactHisOwnRowNamesNoSessionFor_ReadAsBackstory(int? learned, bool knownAsOf2)
    {
        var fact = _f.Apply(_campaign, WriteContext.Default, new CampaignOpSpec
        {
            Op = "fact", Statement = "The bridge fell.", CanonStatus = CampaignValues.CanonStatuses.Played, EstablishedSession = 3,
            KnownBy = [Op.Knower("character:hero", session: learned)],
        }).Applied.Single().Ref;

        Assert.Equal(knownAsOf2, Shown(fact, "character:hero", 2));
        Assert.True(Shown(fact, "character:hero", 3));
        Assert.False(Shown(fact, "party", 2));
    }
}
