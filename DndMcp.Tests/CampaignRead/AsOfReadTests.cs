using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignDb;
using Microsoft.Data.Sqlite;
using Xunit;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: a read as of session n shows a player view the campaign as it stood then, with nothing of today mixed in
/// that the view cannot see today. The scenario is the dangerous one: Mira was a party-visible merchant through session
/// 3; in session 5 the author made her author-only, wrote "Secretly a spy for the Crown." into her summary and tagged her
/// "crown-spy". As of session 3 the party must see her as she was (her old tags, her old summary) and must not find
/// her by today's secret words or tags: FTS indexes only today's text, so a hit on "spy" would itself say the word is
/// about her. The same holds for a fact whose statement was rewritten, and for fact links made later.
/// </summary>
public sealed class AsOfReadTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;
    private readonly IReadOnlyList<SeededSession> _sessions;
    private readonly SeededSession _s5;
    private readonly SeededEntity _mira;

    public AsOfReadTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Sky", role: CampaignValues.Roles.Dm);
        var sessions = Enumerable.Range(1, 5).Select(n => _seed.Session(_campaign.Id, n)).ToList();
        _sessions = sessions;
        _s5 = sessions[4];
        _mira = _seed.Entity(_campaign.Id, "character", "Mira", subtype: "npc", summary: "A merchant.");
        var trader = _seed.Tag(_campaign.Id, _mira.Id, "trader");
        var spyTag = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity", _mira.Id, new Dictionary<string, object?>
            {
                ["visibility"] = V.Author,
                ["summary"] = "A merchant. Secretly a spy for the Crown.",
            }, "upsert");
            r.Insert("tag", new Dictionary<string, object?> { ["id"] = spyTag, ["campaign_id"] = _campaign.Id, ["name"] = "crown-spy" }, "upsert");
            r.Insert("entity_tag", new Dictionary<string, object?> { ["entity_id"] = _mira.Id, ["tag_id"] = spyTag }, "upsert");
            r.Delete("entity_tag", [_mira.Id, trader], "upsert");
        }, sessionId: _s5.EntityId);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private CampaignRow Row => _seed.LoadCampaign(_campaign.Id);

    private SearchResult Search(string? query, string perspective, int? asOf, IReadOnlyList<string>? tags = null) =>
        new CampaignSearch(_db.Database).Search(Row, new SearchRequest(query, Tags: tags, Perspective: Perspective.Parse(perspective), AsOfSession: asOf));

    private EntityDetail Get(string perspective, int? asOf, string reference) =>
        Assert.Single(new EntityReader(_db.Database).Get(Row, [reference], EntityIncludes.All, Perspective.Parse(perspective), asOf).Entities);

    [Fact]
    public void Get_AsOfBeforeTheTagsChanged_PrintsTheTagsItHadThen()
    {
        var then = Get("party", 3, _mira.Handle);
        var now = Get("author", null, _mira.Handle);
        var authorThen = Get("author", 3, _mira.Handle);

        Assert.Equal(["trader"], then.Tags);
        Assert.Equal("A merchant.", then.Summary);
        Assert.Equal(["crown-spy"], now.Tags);
        Assert.Equal(["trader"], authorThen.Tags);
        LeakAssert.Clean(then, ["crown-spy", "spy", "Crown"], "as_of get");
    }

    /// <summary>A tag renamed after the session prints (and filters) under the name it had then.</summary>
    [Fact]
    public void Get_AsOfBeforeATagWasRenamed_PrintsItsNameThen()
    {
        var harbor = _seed.Entity(_campaign.Id, "location", "Harbor");
        var tag = _seed.Tag(_campaign.Id, harbor.Id, "quiet");
        _db.Batch(_campaign.Id, r => r.Update("tag", tag, new Dictionary<string, object?> { ["name"] = "smugglers-den" }, "upsert"),
            sessionId: _s5.EntityId);

        Assert.Equal(["quiet"], Get("party", 3, harbor.Handle).Tags);
        Assert.Equal(["smugglers-den"], Get("party", null, harbor.Handle).Tags);
        Assert.Single(Search(null, "party", 3, tags: ["quiet"]).Entities);
        Assert.Empty(Search(null, "party", 3, tags: ["smugglers-den"]).Entities);
    }

    [Theory]
    [InlineData("crown-spy", null, false)]
    [InlineData("crown-spy", 3, false)]
    [InlineData("trader", 3, true)]
    [InlineData("trader", 5, false)]
    public void Search_TagFilterAsOfASession_UsesTheTagsThen(string tag, int? asOf, bool found)
    {
        var result = Search(null, "party", asOf, tags: [tag]);

        Assert.Equal(found, result.Entities.Any(e => e.Ref == _mira.Handle));
        LeakAssert.Clean(result, ["crown-spy", "spy"], "as_of tag filter");
    }

    [Fact]
    public void Search_TagFilterForTheAuthor_UsesTheTagsOfTheTime()
    {
        Assert.Single(Search(null, "author", null, tags: ["crown-spy"]).Entities);
        Assert.Empty(Search(null, "author", 3, tags: ["crown-spy"]).Entities);
        Assert.Single(Search(null, "author", 3, tags: ["trader"]).Entities);
    }

    [Theory]
    [InlineData("spy")]
    [InlineData("secretly crown")]
    [InlineData("crown*")]
    public void Search_AsOfWordsOnlyInTodaysHiddenText_FindsNothing(string query)
    {
        var then = Search(query, "party", 3);
        var now = Search(query, "party", null);

        Assert.Empty(then.Entities);
        Assert.Empty(now.Entities);
        LeakAssert.Clean(then, ["Mira", "merchant", _mira.Handle], $"as_of search for {query}");
    }

    [Fact]
    public void Search_AsOfWordsInTheTextOfThen_FindsItWithASnippetOfThen()
    {
        var hit = Assert.Single(Search("merchant", "party", 3).Entities);

        Assert.Equal((_mira.Handle, "A merchant."), (hit.Ref, hit.Snippet));
        Assert.Single(Search("spy", "author", 3).Entities);
    }

    [Fact]
    public void Search_AsOfAFactStatementRewrittenLater_MatchesOnlyTheStatementOfThen()
    {
        var duke = _seed.Fact(_campaign.Id, "The duke is loyal.", visibility: V.Party);
        _db.Batch(_campaign.Id, r => r.Update("fact", duke.Id, new Dictionary<string, object?>
        {
            ["statement"] = "The duke is loyal, but secretly plots against the crown.",
            ["visibility"] = V.Author,
        }, "fact"), sessionId: _s5.EntityId);

        var byNewWords = Search("plots", "party", 3);
        var byOldWords = Search("loyal", "party", 3);

        Assert.Empty(byNewWords.Facts);
        var fact = Assert.Single(byOldWords.Facts);
        Assert.Equal((duke.SeqHandle, "The duke is loyal."), (fact.Ref, fact.Text));
        Assert.Single(Search("plots", "author", 3).Facts);
    }

    [Fact]
    public void Get_FactLinkedAfterTheSession_IsNotListedAsOfIt()
    {
        var harbor = _seed.Entity(_campaign.Id, "location", "Harbor");
        var closed = _seed.Fact(_campaign.Id, "The harbor is closed.", visibility: V.Party);
        _db.Batch(_campaign.Id, r => r.Insert("fact_link", new Dictionary<string, object?>
        {
            ["fact_id"] = closed.Id, ["entity_id"] = harbor.Id, ["role"] = CampaignValues.FactLinkRoles.About,
        }, "fact"), sessionId: _s5.EntityId);

        Assert.Empty(Get("author", 3, harbor.Handle).Facts!);
        Assert.Equal([closed.SeqHandle], Get("author", 5, harbor.Handle).Facts!.Select(f => f.Ref));
        Assert.Equal([closed.SeqHandle], Get("party", null, harbor.Handle).Facts!.Select(f => f.Ref));
        var factThen = Assert.Single(new EntityReader(_db.Database).Get(Row, [closed.SeqHandle], EntityIncludes.All, Perspective.Author, 3).Facts);
        Assert.Empty(factThen.Links);
    }

    /// <summary>
    /// Knowledge is read as it stood then (review L05, fix FQ2b): the party met her as "the veiled woman" in S1, and in S3
    /// the same row was given her true name (state unchanged, so its learned session stayed S1). As of S2 the party still
    /// knows only the veiled woman. Judged by the learned session alone, the S3 edit reached back to S1, and a read as of
    /// S2 printed "Morwen Vashkar", her summary and her slug.
    /// </summary>
    [Fact]
    public void Read_AsOfBeforeAKnownAsWasEdited_UsesTheNameOfThen()
    {
        var morwen = _seed.Entity(_campaign.Id, "character", "Morwen Vashkar", subtype: "npc", visibility: V.Restricted, summary: "Rules the crypts.");
        var row = PartyRow(morwen.Id, "the veiled woman", learnedIn: 1, recordedIn: 1);
        _db.Batch(_campaign.Id, r => r.Update("knowledge", row, new Dictionary<string, object?> { ["known_as"] = "Morwen Vashkar" }, "record"),
            sessionId: _sessions[2].EntityId);

        var listing = Search(null, "party", 2);
        var then = Get("party", 2, morwen.SeqHandle);
        var now = Get("party", null, morwen.SeqHandle);

        Assert.Equal("the veiled woman", Assert.Single(listing.Entities, e => e.Ref == morwen.SeqHandle).DisplayName);
        Assert.Equal(("the veiled woman", morwen.SeqHandle), (then.DisplayName, then.Ref));
        Assert.Equal(("Morwen Vashkar", morwen.Handle), (now.DisplayName, now.Ref));
        foreach (var result in new object[] { listing, then })
        {
            LeakAssert.Clean(result, ["Morwen", "Vashkar", "crypts", "morwen-vashkar"], "a read as of S2");
        }
    }

    /// <summary>
    /// A knowledge edit dated by the knower's own session replays like one made in that session (review LR03): the party
    /// met Ilsa as "the harbour widow" in S1, and a record with no session of its own gave the party row her true name with
    /// the knower's session 3. The row's learned session moved to S3, but the change was logged with no session, so the
    /// replay took it as timeless: as of S2 the row was "learned in S3", a wall, and the party had no record of a woman it
    /// met in S1. Now the change counts as made in S3. A row first recorded that way is still a wall before its session
    /// (Rhee, FD2's "not yet").
    /// </summary>
    [Fact]
    public void Read_AsOfBeforeAKnownAsWasEditedWithTheKnowersOwnSession_UsesTheNameOfThen()
    {
        var ilsa = _seed.Entity(_campaign.Id, "character", "Ilsa Brandt", subtype: "npc", visibility: V.Restricted);
        var rhee = _seed.Entity(_campaign.Id, "character", "Rhee Sorn", subtype: "npc", visibility: V.Restricted);
        var row = PartyRow(ilsa.Id, "the harbour widow", learnedIn: 1, recordedIn: 1);
        _db.Batch(_campaign.Id, r => r.Update("knowledge", row, new Dictionary<string, object?>
        {
            ["known_as"] = "Ilsa Brandt", ["learned_session_id"] = _sessions[2].EntityId,
        }, "record"));
        PartyRow(rhee.Id, "the grey captain", learnedIn: 3, recordedIn: null);

        var listing = Search(null, "party", 2);
        var ledger = new KnowledgeLedger(_db.Database).Build(Row, [ilsa.Handle, rhee.Handle], [], ["party"], 2);
        var now = Get("party", null, ilsa.Handle);

        Assert.Equal("the harbour widow", Assert.Single(listing.Entities, e => e.Ref == ilsa.SeqHandle).DisplayName);
        Assert.DoesNotContain(listing.Entities, e => e.Ref == rhee.SeqHandle || e.Ref == rhee.Handle);
        Assert.Equal(["met as “the harbour widow” (S1)", "not met"], ledger.Rows.Select(r => r.Cells.Single().Text));
        Assert.Equal(("Ilsa Brandt", ilsa.Handle), (now.DisplayName, now.Ref));
        LeakAssert.Clean(listing, ["Ilsa", "Brandt", "ilsa-brandt"], "a read as of S2");
    }

    /// <summary>
    /// A batch that only moves a row's learned session is a correction, not an edit (LR03's recheck): the party's
    /// "learned in S1" of a restricted fact, corrected between sessions to S3 by the knower's own session, says the party
    /// did not know it in S2. Dated like an edit, the correction was reversed for every read before S3, and as of S2 the
    /// party "knew" the fact, which it had been hidden from before LR03's rule. Today and as of S3 it knows it since S3.
    /// </summary>
    [Fact]
    public void Ledger_AsOfBeforeALearnedSessionWasCorrectedWithTheKnowersOwnSession_IsNoRecordYet()
    {
        var lich = _seed.Fact(_campaign.Id, "The duke is a lich.", visibility: V.Restricted);
        var knows = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", new Dictionary<string, object?>
        {
            ["id"] = knows, ["campaign_id"] = _campaign.Id, ["fact_id"] = lich.Id, ["knower_kind"] = CampaignValues.KnowerKinds.Party,
            ["state"] = CampaignValues.KnowledgeStates.Knows, ["learned_session_id"] = _sessions[0].EntityId,
        }, "record"), sessionId: _sessions[0].EntityId);
        _db.Batch(_campaign.Id, r => r.Update("knowledge", knows, new Dictionary<string, object?> { ["learned_session_id"] = _sessions[2].EntityId }, "record"));
        var ledger = new KnowledgeLedger(_db.Database);

        var cells = new int?[] { 2, 3, null }.Select(n => ledger.Build(Row, [], [lich.SeqHandle], ["party"], n).Rows.Single().Cells.Single().Text);

        Assert.Equal(["no record", "knows (S3)", "knows (S3)"], cells);
        Assert.Empty(Search("lich", "party", 2).Facts);
        Assert.Equal([lich.SeqHandle], Search("lich", "party", 3).Facts.Select(f => f.Ref));
    }

    /// <summary>
    /// The knowledge replay rewinds a row's later changes newest first (LR03's recheck: the replay is the loader's own
    /// since LR03): the party knew her as "the widow" from S1, "the harbour widow" from S3 and "Ilsa Brandt" from S4. As of
    /// S2 she is "the widow"; rewound oldest first, the S4 change's old value ("the harbour widow") was applied last, and a
    /// read as of S2 named her by a name the party learned in S3.
    /// </summary>
    [Theory]
    [InlineData(2, "the widow")]
    [InlineData(3, "the harbour widow")]
    [InlineData(4, "Ilsa Brandt")]
    public void Read_AsOfBeforeTwoLaterRenames_UsesTheNameOfThen(int asOf, string name)
    {
        var ilsa = _seed.Entity(_campaign.Id, "character", "Ilsa Brandt", subtype: "npc", visibility: V.Restricted);
        var row = PartyRow(ilsa.Id, "the widow", learnedIn: 1, recordedIn: 1);
        foreach (var (session, knownAs) in new[] { (3, "the harbour widow"), (4, "Ilsa Brandt") })
        {
            _db.Batch(_campaign.Id, r => r.Update("knowledge", row, new Dictionary<string, object?> { ["known_as"] = knownAs }, "record"),
                sessionId: _sessions[session - 1].EntityId);
        }

        var listing = Search(null, "party", asOf);

        Assert.Equal(name, Assert.Single(listing.Entities, e => e.Ref == ilsa.SeqHandle || e.Ref == ilsa.Handle).DisplayName);
    }

    /// <summary>
    /// The knower-session rule dates updates, never a create (review LR03, FD2's "not yet" wall): one batch with no session
    /// that recorded the party's "the harbour widow" and then, by the knower's session 3, renamed it "Ilsa Brandt" made a
    /// row that existed before S3 under its first name. Dated too, the create made the row a wall before S3, and the party
    /// had no record of her as of S2.
    /// </summary>
    [Fact]
    public void Read_AsOfBeforeARowWasRecordedAndEditedInOneBatchWithTheKnowersSession_UsesItsFirstName()
    {
        var ilsa = _seed.Entity(_campaign.Id, "character", "Ilsa Brandt", subtype: "npc", visibility: V.Restricted);
        var row = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r =>
        {
            r.Insert("knowledge", new Dictionary<string, object?>
            {
                ["id"] = row, ["campaign_id"] = _campaign.Id, ["entity_id"] = ilsa.Id, ["knower_kind"] = CampaignValues.KnowerKinds.Party,
                ["state"] = CampaignValues.KnowledgeStates.Met, ["known_as"] = "the harbour widow", ["learned_session_id"] = _sessions[0].EntityId,
            }, "record");
            r.Update("knowledge", row, new Dictionary<string, object?> { ["known_as"] = "Ilsa Brandt", ["learned_session_id"] = _sessions[2].EntityId }, "record");
        });

        var then = Search(null, "party", 2);
        var now = Search(null, "party", null);

        Assert.Equal("the harbour widow", Assert.Single(then.Entities, e => e.Ref == ilsa.SeqHandle).DisplayName);
        Assert.Equal("Ilsa Brandt", Assert.Single(now.Entities, e => e.Ref == ilsa.Handle).DisplayName);
    }

    /// <summary>
    /// The replay reverses only changes of sessions after the one asked for (review MR03): a party row recorded and
    /// retracted inside S1, and put back under the same id in S2 (an undo of the retract), had been retracted by the end of
    /// S1, so as of S1 the party has no record; as of S2 it knows. Counting S1's own changes too made the put-back row a
    /// wall created in S1, and as of S1 the party "knew" what had been retracted.
    /// </summary>
    [Fact]
    public void Ledger_AsOfASessionARowWasRecordedAndRetractedIn_IsNoRecordAfterALaterPutBack()
    {
        var well = _seed.Fact(_campaign.Id, "The well was poisoned.", visibility: V.Restricted);
        var knows = CampaignDatabase.NewId();
        Dictionary<string, object?> Values() => new()
        {
            ["id"] = knows, ["campaign_id"] = _campaign.Id, ["fact_id"] = well.Id, ["knower_kind"] = CampaignValues.KnowerKinds.Party,
            ["state"] = CampaignValues.KnowledgeStates.Knows, ["learned_session_id"] = _sessions[0].EntityId,
        };
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", Values(), "record"), sessionId: _sessions[0].EntityId);
        _db.Batch(_campaign.Id, r => r.Delete("knowledge", knows, "retract"), sessionId: _sessions[0].EntityId);
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", Values(), "undo"), sessionId: _sessions[1].EntityId);
        var ledger = new KnowledgeLedger(_db.Database);

        var asOf1 = ledger.Build(Row, [], [well.SeqHandle], ["party"], 1).Rows.Single().Cells.Single().Text;
        var asOf2 = ledger.Build(Row, [], [well.SeqHandle], ["party"], 2).Rows.Single().Cells.Single().Text;

        Assert.Equal(("no record", "knows (S1)"), (asOf1, asOf2));
    }

    /// <summary>A knowledge row retracted after the session asked for still counts as of it: it was the record then.</summary>
    [Fact]
    public void Read_AsOfBeforeARowWasRetracted_StillReadsIt()
    {
        var morwen = _seed.Entity(_campaign.Id, "character", "Morwen Vashkar", subtype: "npc", visibility: V.Restricted);
        var row = PartyRow(morwen.Id, "the veiled woman", learnedIn: 1, recordedIn: 1);
        _db.Batch(_campaign.Id, r => r.Delete("knowledge", row, "retract"), sessionId: _sessions[2].EntityId);

        Assert.Equal("the veiled woman", Get("party", 2, morwen.SeqHandle).DisplayName);
        Assert.Throws<DndInputException>(() => Get("party", null, morwen.SeqHandle));
    }

    /// <summary>
    /// A knowledge row recorded in a later session did not exist yet, but it is a wall, not a gap (fix FQ2b on top of FD2): as
    /// of S2 a party-visible Morwen the party met in S3 as "the veiled woman" is neither shown under that name nor, by the
    /// party-visibility default, under her true one (review L05b). Recorded in S3 about S1 (a retroactive record), it counts
    /// from S3, the session the record was made in. Dropping the row instead let the visibility default name her.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void Read_AsOfBeforeARowWasRecorded_ShowsNeitherHerTrueNameNorTheLaterName(int learnedIn)
    {
        var morwen = _seed.Entity(_campaign.Id, "character", "Morwen Vashkar", subtype: "npc", visibility: V.Party, summary: "Rules the crypts.");
        var well = _seed.Fact(_campaign.Id, "Morwen Vashkar poisoned the well.", visibility: V.Party, canonStatus: CampaignValues.CanonStatuses.Played,
            establishedSessionId: _sessions[0].EntityId);
        PartyRow(morwen.Id, "the veiled woman", learnedIn, recordedIn: 3);
        var wellRow = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", new Dictionary<string, object?>
        {
            ["id"] = wellRow, ["campaign_id"] = _campaign.Id, ["fact_id"] = well.Id, ["knower_kind"] = CampaignValues.KnowerKinds.Party,
            ["state"] = CampaignValues.KnowledgeStates.Knows, ["known_as"] = "Someone poisoned the well.",
            ["learned_session_id"] = _sessions[learnedIn - 1].EntityId,
        }, "record"), sessionId: _sessions[2].EntityId);

        var listing = Search(null, "party", 2);
        var byWords = Search("well", "party", 2);
        var bySeq = Assert.Throws<DndInputException>(() => Get("party", 2, morwen.SeqHandle));
        var asOf3 = Get("party", 3, morwen.SeqHandle);

        Assert.DoesNotContain(listing.Entities, e => e.Ref == morwen.SeqHandle || e.Ref == morwen.Handle);
        Assert.Empty(byWords.Facts);
        Assert.Contains("nothing by that handle", bySeq.Message, StringComparison.Ordinal);
        Assert.Equal("the veiled woman", asOf3.DisplayName);
        foreach (var result in new object[] { listing, byWords })
        {
            LeakAssert.Clean(result, ["Morwen", "Vashkar", "crypts", "veiled", "poisoned"], "a read as of S2");
        }
    }

    /// <summary>
    /// The knowledge rows of then decide every verdict of an as_of read, not only the scope's own (fix FQ2b): the ledger's
    /// cells and the knowledge check's speaker judge another view's verdict through the scope, and judged by today's rows
    /// they disagreed with that view's own reads. As of S2 the party knows Morwen as "the veiled woman" (the S3 edit that
    /// gave the row her true name came later), so her true name in a line is another name; and it knows the well was
    /// poisoned (the row was retracted only in S3). Today's rows say the opposite of both.
    /// </summary>
    [Fact]
    public void VerdictFor_AsOfBeforeTheRowsChanged_JudgesOtherViewsByTheRowsOfThen()
    {
        var morwen = _seed.Entity(_campaign.Id, "character", "Morwen Vashkar", subtype: "npc", visibility: V.Restricted);
        var met = PartyRow(morwen.Id, "the veiled woman", learnedIn: 1, recordedIn: 1);
        _db.Batch(_campaign.Id, r => r.Update("knowledge", met, new Dictionary<string, object?> { ["known_as"] = "Morwen Vashkar" }, "record"),
            sessionId: _sessions[2].EntityId);
        var well = _seed.Fact(_campaign.Id, "The well was poisoned.", visibility: V.Restricted);
        var knows = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", new Dictionary<string, object?>
        {
            ["id"] = knows, ["campaign_id"] = _campaign.Id, ["fact_id"] = well.Id, ["knower_kind"] = CampaignValues.KnowerKinds.Party,
            ["state"] = CampaignValues.KnowledgeStates.Knows, ["learned_session_id"] = _sessions[0].EntityId,
        }, "record"), sessionId: _sessions[0].EntityId);
        _db.Batch(_campaign.Id, r => r.Delete("knowledge", knows, "retract"), sessionId: _sessions[2].EntityId);
        var ledger = new KnowledgeLedger(_db.Database);
        var check = new KnowledgeCheck(_db.Database);
        var line = new CheckRequest("Morwen Vashkar walks by the well", Perspective.Parse("party"));

        var then = ledger.Build(Row, [morwen.Handle], [well.SeqHandle], ["party"], 2);
        var now = ledger.Build(Row, [morwen.Handle], [well.SeqHandle], ["party"], null);
        var checkThen = check.Check(Row, line with { AsOfSession = 2 });
        var checkNow = check.Check(Row, line);

        Assert.Equal([morwen.Handle, well.SeqHandle], then.Rows.Select(r => r.Ref));
        Assert.Equal(["met as “the veiled woman” (S1)", "knows (S1)"], then.Rows.Select(r => r.Cells.Single().Text));
        Assert.Equal(["met as “Morwen Vashkar” (S1)", "no record"], now.Rows.Select(r => r.Cells.Single().Text));
        var name = Assert.Single(checkThen.Names, n => n.Matched == "Morwen Vashkar");
        Assert.Equal((NameClasses.OtherName, "the veiled woman"), (name.Classification, name.Candidates.Single().SpeakerName));
        Assert.Equal(NameClasses.Ok, Assert.Single(checkNow.Names, n => n.Matched == "Morwen Vashkar").Classification);
        Assert.Contains(checkThen.Related, f => f.Ref == well.SeqHandle);
        Assert.Contains(checkNow.UnknownFacts, f => f.Ref == well.SeqHandle);
    }

    // A party knowledge row on an entity, written in a logged batch filed under session recordedIn (null: between sessions).
    private string PartyRow(string entityId, string? knownAs, int? learnedIn, int? recordedIn, string state = CampaignValues.KnowledgeStates.Met)
    {
        var id = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r => r.Insert("knowledge", new Dictionary<string, object?>
        {
            ["id"] = id, ["campaign_id"] = _campaign.Id, ["entity_id"] = entityId, ["knower_kind"] = CampaignValues.KnowerKinds.Party,
            ["state"] = state, ["known_as"] = knownAs, ["learned_session_id"] = learnedIn is { } l ? _sessions[l - 1].EntityId : null,
        }, "record"), sessionId: recordedIn is { } s ? _sessions[s - 1].EntityId : null);
        return id;
    }
}
