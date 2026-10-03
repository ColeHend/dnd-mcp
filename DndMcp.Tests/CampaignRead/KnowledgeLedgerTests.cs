using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: the ledger renders each perspective's verdict with the wording that matters: an entity nobody recorded
/// meeting is "not met", a fact with no record is "no record" (never "does not know"), an unrecognised meeting is "met,
/// unrecognized (S1)"; and it answers the Belmakor verdict goldens (rows 1-19) through the database, not just the pure
/// rules. <see cref="KnowledgeLedger.KnownTo"/> (the knowledge resource) stays inside the perspective filter (row 34).
/// </summary>
public sealed class BelmakorLedgerTests(BelmakorFixture fixture) : IClassFixture<BelmakorFixture>
{
    private Ledger Build(IReadOnlyList<string>? about = null, IReadOnlyList<string>? facts = null, IReadOnlyList<string>? perspectives = null, int? asOf = null) =>
        new KnowledgeLedger(fixture.Db.Database).Build(fixture.CampaignRow, about, facts, perspectives, asOf);

    private LedgerCell Cell(string target, string perspective, int? asOf = null)
    {
        var isFact = target.StartsWith("f:", StringComparison.Ordinal);
        var ledger = Build(isFact ? null : [target], isFact ? [target] : null, [perspective], asOf);
        return ledger.Rows.First(r => r.Ref == target).Cells.Single();
    }

    public static TheoryData<int, string, string, string, string?> VerdictRows() => new()
    {
        { 1, "f:1", "character:belmakor", Standings.DoesNotKnow, "unaware" },
        { 2, "item:thing-he-wants", "character:belmakor", Standings.Knows, "aware as “the thing he wants” (S3)" },
        { 3, "character:old-king", "character:belmakor", Standings.Knows, "met as “the old king” (S3)" },
        { 4, "f:2", "character:belmakor", Standings.DoesNotKnow, "unaware" },
        { 5, "f:1", "party", Standings.DoesNotKnow, "unaware" },
        { 6, "f:2", "author", Standings.Knows, "knows" },
        { 7, "f:2", "dm", Standings.Knows, "knows" },
        { 8, "f:1", "dm", Standings.NoRecord, "no record" },
        { 9, "f:3", "character:ignis", Standings.Knows, "knows (S3)" },
        { 10, "item:thing-he-wants", "character:ignis", Standings.Knows, "aware as “the thing he wants” (S3)" },
        { 11, "f:7", "character:serif", Standings.Uncertain, "uncertain: the party learned it in S1; Serif was absent" },
        { 12, "f:7", "character:aiden-ironstar", Standings.Uncertain, "uncertain: the party learned it in S1; no attendance recorded for Aiden Ironstar in S1" },
        { 13, "f:7", "character:belmakor", Standings.Knows, "knows (S1)" },
        { 14, "f:5", "character:belmakor", Standings.Knows, "knows" },
        { 15, "f:5", "character:vars", Standings.DoesNotKnow, "unaware" },
        { 16, "f:5", "public", Standings.NoRecord, "no record" },
    };

    [Theory]
    [MemberData(nameof(VerdictRows))]
    public void Build_BelmakorVerdictRow_RendersTheGoldenVerdict(int row, string target, string perspective, string standing, string? text)
    {
        var cell = Cell(target, perspective);

        Assert.True((standing, text) == (cell.Standing, cell.Text), $"row {row}: {cell.Standing} / {cell.Text}");
    }

    [Theory]
    [InlineData(1, "table", Standings.NoRecord)]
    [InlineData(2, "table", Standings.Knows)]
    [InlineData(1, "character:belmakor", Standings.Knows)]
    public void Build_Rows17To19ContingencyAsOfASession_UsesTheLearnedSessionsAndTheVisibilityThen(int asOf, string perspective, string standing)
    {
        Assert.Equal(standing, Cell("f:6", perspective, asOf).Standing);
    }

    [Fact]
    public void Build_AboutTheOldKingWithDefaultColumns_ListsTheKnowersWithRowsThenPartyTablePublicAndDm()
    {
        var ledger = Build(about: ["character:old-king"]);

        Assert.Equal(["character:belmakor", "party", "table", "public", "dm"], ledger.Columns);
        Assert.Equal(["character:old-king", fixture.F1.SeqHandle, fixture.F2.SeqHandle, fixture.F3.SeqHandle, fixture.F4.SeqHandle],
            ledger.Rows.Select(r => r.Ref));
        Assert.True(ledger.Rows[0].IsEntity);
        Assert.Equal("The Old King", ledger.Rows[0].Label);
    }

    [Fact]
    public void Build_WithoutAboutOrFacts_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => Build());

        Assert.Contains("Give about", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnknownHandlesAndPerspectives_AreListed()
    {
        var ex = Assert.Throws<DndInputException>(() => Build(about: ["character:nobody"], facts: ["f:999"], perspectives: ["party"]));

        Assert.Contains("about item 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("facts item 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownTo_Row34Belmakor_HasHisNamesAndNothingHidden()
    {
        var known = new KnowledgeLedger(fixture.Db.Database).KnownTo(fixture.CampaignRow, Perspective.Parse("character:belmakor"), limit: 50);

        Assert.Contains(known.Entities, e => e.Name == "The Old King");
        Assert.Contains(known.Entities, e => e.Name == "the thing he wants" && e.Ref == "e:" + fixture.Thing.Seq);
        Assert.Contains(known.Facts, f => f.Ref == fixture.F5.SeqHandle);
        Assert.DoesNotContain(known.Facts, f => f.Ref == fixture.F2.SeqHandle || f.Ref == fixture.F1.SeqHandle);
        LeakAssert.Clean(known, BelmakorFixture.ForbiddenFor("character:belmakor"), "row 34");
    }

    public static TheoryData<string> Perspectives() => new(BelmakorFixture.NonAuthorPerspectives);

    [Theory]
    [MemberData(nameof(Perspectives))]
    public void KnownTo_AnyPlayerView_NeverContainsWhatItMustNotSee(string perspective)
    {
        var known = new KnowledgeLedger(fixture.Db.Database).KnownTo(fixture.CampaignRow, Perspective.Parse(perspective), limit: 50);

        LeakAssert.Clean(known, BelmakorFixture.ForbiddenFor(perspective), $"knowledge of {perspective}");
    }

    [Fact]
    public void KnownTo_Author_ListsEveryRowHolderWithWhoKnows()
    {
        var known = new KnowledgeLedger(fixture.Db.Database).KnownTo(fixture.CampaignRow, Perspective.Author, limit: 50);

        var f2 = Assert.Single(known.Facts, f => f.Ref == fixture.F2.SeqHandle);
        Assert.Equal(["author", "dm"], f2.KnownBy!.Order(StringComparer.Ordinal));
        Assert.Contains(known.Entities, e => e.Ref == "character:old-king" && e.KnownBy!.Contains("character:belmakor"));
    }
}

/// <summary>Invariant: One Piece row 28: the ledger says the party met two fragments unrecognised and has not met the other two.</summary>
public sealed class OnePieceLedgerTests
{
    [Fact]
    public void Build_Row28TheFourFragmentsForTheParty_MetUnrecognizedOrNotMet()
    {
        using var world = new OnePieceFixture();

        var ledger = new KnowledgeLedger(world.Db.Database).Build(world.CampaignRow,
            ["character:protector", "character:peaceful-one", "character:mistaken-one", "character:dutiful-one"], null, ["party"]);

        var cells = ledger.Rows.Where(r => r.IsEntity).ToDictionary(r => r.Ref, r => r.Cells.Single().Text);
        Assert.Equal("met, unrecognized as “the advisor in Serret” (S1)", cells["character:protector"]);
        Assert.Equal("met, unrecognized as “the man napping under the tree” (S5)", cells["character:peaceful-one"]);
        Assert.Equal("not met", cells["character:mistaken-one"]);
        Assert.Equal("not met", cells["character:dutiful-one"]);
    }

    [Fact]
    public void Build_TheSecretsClues_AreRowsUnderIt()
    {
        using var world = new OnePieceFixture();

        var ledger = new KnowledgeLedger(world.Db.Database).Build(world.CampaignRow, ["secret:fruits-are-the-seal"], null, ["party"]);

        Assert.Equal(10, ledger.Rows.Count);
        Assert.Equal("knows (S6)", ledger.Rows.Single(r => r.Ref == world.ShieldedChild.SeqHandle).Cells[0].Text);
        Assert.Equal("no record", ledger.Rows.Single(r => r.Ref == world.Seal.SeqHandle).Cells[0].Text);
    }

    /// <summary>
    /// A party-visible planned fact is out of every player view's reads, so its player-side cells say "not in play
    /// (planned)", never "knows", until it is played (and again as of a session before that).
    /// </summary>
    [Fact]
    public void Build_PlannedFactForPlayerColumns_IsNotInPlayUntilItIsPlayed()
    {
        using var world = new OnePieceFixture();
        Ledger Axe(int? asOf = null) => new KnowledgeLedger(world.Db.Database).Build(world.CampaignRow, null, [world.AxeAssembled.SeqHandle],
            ["party", "table", "character:bjorn-mountainfell", "author"], asOf);

        var before = Axe();
        world.AssembleAxe();
        var after = Axe();
        var asOf10 = Axe(10);

        var notInPlay = (Standings.NotInPlay, "not in play (planned)");
        Assert.Equal([notInPlay, notInPlay, notInPlay, (Standings.Knows, "knows")], before.Rows.Single().Cells.Select(c => (c.Standing, c.Text)));
        Assert.All(after.Rows.Single().Cells, c => Assert.Equal(Standings.Knows, c.Standing));
        Assert.Equal([Standings.NotInPlay, Standings.NotInPlay, Standings.NotInPlay, Standings.Knows], asOf10.Rows.Single().Cells.Select(c => c.Standing));
    }
}

/// <summary>
/// Invariant: the ledger's rows are an entity and its facts linked <c>about</c> or <c>clue_for</c> it (contract §7), at
/// most <see cref="KnowledgeLedger.MaxRows"/> with the total counted; a row that is not in play (a proposed entity, an
/// unplayed session) reads "not in play" in player columns, and an author-only row that knowledge rows say a player-side
/// view knows reads "author only" there. <see cref="KnowledgeLedger.KnownTo"/>, the knowledge resource,
/// lists entities a view has a record for (not everything it may see by visibility) and facts in that view's own
/// phrasing, never the statement it knows them under another wording of.
/// </summary>
public sealed class KnowledgeLedgerTests : IDisposable
{
    private readonly DndMcp.Tests.CampaignDb.CampaignTestDb _db = new();
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly DndMcp.Tests.CampaignDb.CampaignSeed _seed;
    private readonly DndMcp.Tests.CampaignDb.SeededCampaign _campaign;
    private readonly DndMcp.Tests.CampaignDb.SeededEntity _hero;

    public KnowledgeLedgerTests()
    {
        _connection = _db.Open();
        _seed = new DndMcp.Tests.CampaignDb.CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Test", role: CampaignValues.Roles.Player);
        _hero = _seed.Entity(_campaign.Id, "character", "Hero", subtype: "pc");
        _seed.MemberOf(_campaign.Id, _hero.Id, _campaign.Party!.Id);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private Repository.Campaign.CampaignRow Row => _seed.LoadCampaign(_campaign.Id);

    private Ledger Build(IReadOnlyList<string>? about = null, IReadOnlyList<string>? facts = null, IReadOnlyList<string>? perspectives = null) =>
        new KnowledgeLedger(_db.Database).Build(Row, about, facts, perspectives);

    [Fact]
    public void Build_AboutAnEntity_ListsOnlyItsAboutAndClueForFacts()
    {
        var harbor = _seed.Entity(_campaign.Id, "location", "Harbor");
        var byRole = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in CampaignValues.FactLinkRoles.Set.Values)
        {
            var fact = _seed.Fact(_campaign.Id, $"A fact linked {role}.");
            _seed.FactLink(fact.Id, harbor.Id, role);
            byRole[role] = fact.SeqHandle;
        }

        var ledger = Build(about: [harbor.Handle], perspectives: ["party"]);

        Assert.Equal([harbor.Handle, byRole[CampaignValues.FactLinkRoles.About], byRole[CampaignValues.FactLinkRoles.ClueFor]],
            ledger.Rows.Select(r => r.Ref));
        Assert.Equal(3, ledger.TotalRows);
    }

    [Fact]
    public void Build_MoreRowsThanTheCap_KeepsTheFirstAndCountsThemAll()
    {
        var harbor = _seed.Entity(_campaign.Id, "location", "Harbor");
        for (var i = 0; i < KnowledgeLedger.MaxRows + 1; i++)
        {
            _seed.FactLink(_seed.Fact(_campaign.Id, $"Harbor fact {i}.").Id, harbor.Id);
        }

        var ledger = Build(about: [harbor.Handle], perspectives: ["party"]);

        Assert.Equal((KnowledgeLedger.MaxRows, KnowledgeLedger.MaxRows + 2), (ledger.Rows.Count, ledger.TotalRows));
        Assert.Equal(harbor.Handle, ledger.Rows[0].Ref);
    }

    [Fact]
    public void Build_ProposedEntityOrUnplayedSession_IsNotInPlayInPlayerColumns()
    {
        var tern = _seed.Entity(_campaign.Id, "location", "Tern Rock", canonStatus: CampaignValues.CanonStatuses.Proposed, code: "F1");
        var s2 = _seed.Session(_campaign.Id, 2, CampaignValues.SessionStatuses.Planned, title: "Next time");

        var ledger = Build(about: [tern.Handle, "e:" + s2.Seq], perspectives: ["party", "author"]);

        Assert.Equal([("not in play (proposed)", "knows"), ("not in play (planned)", "knows")],
            ledger.Rows.Select(r => (r.Cells[0].Text, r.Cells[1].Text)));
        Assert.All(ledger.Rows, r => Assert.Equal(Standings.NotInPlay, r.Cells[0].Standing));
    }

    /// <summary>
    /// Author visibility is absolute in the ledger too (contract §3.2): where knowledge rows say a player-side view knows an
    /// author-only entity or fact, that view's cell reads "author only", never "knows" (the view's reads never show it),
    /// and only the author's column knows it. A record that already says the view does not know is kept as recorded: the
    /// party's "unaware", and the table's "no record" behind it.
    /// </summary>
    [Fact]
    public void Build_AuthorOnlyRowsThatRowsSayAPlayerViewKnows_ReadAuthorOnlyInThatViewsColumn()
    {
        const string Author = CampaignValues.Visibilities.Author;
        var plotter = _seed.Entity(_campaign.Id, "character", "Plotter", subtype: "npc", visibility: Author);
        _seed.Knowledge(_campaign.Id, "party", state: CampaignValues.KnowledgeStates.Met, entityId: plotter.Id);
        var plan = _seed.Fact(_campaign.Id, "The plotter plans a coup.", visibility: Author);
        _seed.FactLink(plan.Id, plotter.Id);
        var s1 = _seed.Session(_campaign.Id, 1);
        _seed.Knowledge(_campaign.Id, "party", factId: plan.Id, knownAs: "the plotter's scheme", learnedSessionId: s1.EntityId);
        _seed.Knowledge(_campaign.Id, "character", _hero.Id, factId: plan.Id);
        var alibi = _seed.Fact(_campaign.Id, "The plotter was abroad.", visibility: Author);
        _seed.FactLink(alibi.Id, plotter.Id);
        _seed.Knowledge(_campaign.Id, "party", state: CampaignValues.KnowledgeStates.Unaware, factId: alibi.Id);

        var ledger = Build(about: [plotter.Handle], perspectives: ["party", "character:hero", "table", "author"]);

        var authorOnly = (Standings.AuthorOnly, "author only");
        var knows = (Standings.Knows, "knows");
        Assert.Equal([plotter.Handle, plan.SeqHandle, alibi.SeqHandle], ledger.Rows.Select(r => r.Ref));
        Assert.Equal([authorOnly, authorOnly, authorOnly, knows], ledger.Rows[0].Cells.Select(c => (c.Standing, c.Text)));
        Assert.Equal([authorOnly, authorOnly, authorOnly, knows], ledger.Rows[1].Cells.Select(c => (c.Standing, c.Text)));
        Assert.Equal([(Standings.DoesNotKnow, "unaware"), (Standings.DoesNotKnow, "unaware"), (Standings.NoRecord, "no record"), knows],
            ledger.Rows[2].Cells.Select(c => (c.Standing, c.Text)));
        // The cell keeps what the rows say for the author-facing reader (state, the view's phrasing, when it was learned),
        // beside the standing that overrides it.
        Assert.Equal((CampaignValues.KnowledgeStates.Knows, "the plotter's scheme", 1),
            (ledger.Rows[1].Cells[0].State, ledger.Rows[1].Cells[0].KnownAs, ledger.Rows[1].Cells[0].LearnedSession));
        Assert.Equal((CampaignValues.KnowledgeStates.Knows, (string?)null, (int?)null),
            (ledger.Rows[1].Cells[1].State, ledger.Rows[1].Cells[1].KnownAs, ledger.Rows[1].Cells[1].LearnedSession));
    }

    /// <summary>
    /// "May know" is a claim the rows make too: a party row on an author-only fact leaves a member who was absent when the
    /// party learned it, or who joined the party later, "uncertain" by the verdict, and that member's cell reads "author
    /// only" like "knows" does, never "uncertain: … absent" (which would tell the author the member may know what no read
    /// of theirs will ever show). The same rows on a restricted fact keep the verdict's "uncertain".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_AuthorOnlyFactAPartyRowLeavesAMemberUncertainOn_ReadsAuthorOnlyForThatMember(bool absent)
    {
        var s1 = _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var serif = _seed.Entity(_campaign.Id, "character", "Serif", subtype: "npc");
        _seed.MemberOf(_campaign.Id, serif.Id, _campaign.Party!.Id, sinceSessionId: absent ? null : s2.EntityId);
        if (absent)
        {
            _seed.Attendance(s1.EntityId, serif.Id, present: false);
            _seed.Attendance(s1.EntityId, _hero.Id);
        }

        var crown = _seed.Fact(_campaign.Id, "The duke hides a crown.", visibility: CampaignValues.Visibilities.Author);
        _seed.Knowledge(_campaign.Id, "party", factId: crown.Id, learnedSessionId: s1.EntityId);
        var ring = _seed.Fact(_campaign.Id, "The duke hides a ring.");
        _seed.Knowledge(_campaign.Id, "party", factId: ring.Id, learnedSessionId: s1.EntityId);

        var ledger = Build(facts: [crown.SeqHandle, ring.SeqHandle], perspectives: ["character:serif", "party", "character:hero"]);

        Assert.All(ledger.Rows[0].Cells, c => Assert.Equal((Standings.AuthorOnly, "author only"), (c.Standing, c.Text)));
        Assert.Equal([Standings.Uncertain, Standings.Knows, Standings.Knows], ledger.Rows[1].Cells.Select(c => c.Standing));
        Assert.Contains(absent ? "absent" : "joined", ledger.Rows[1].Cells[0].Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row both not in play and author-only reads "not in play" in a player-side column whose rows claim it: that holds
    /// for every player-side view whatever the rows and the visibility say, so it is the reason the ledger gives (the check
    /// orders the two the same way).
    /// </summary>
    [Fact]
    public void Build_AuthorOnlyPlannedFactThePartysRowSaysItKnows_IsNotInPlayForTheParty()
    {
        var plan = _seed.Fact(_campaign.Id, "The duke will seize the crown.", visibility: CampaignValues.Visibilities.Author,
            canonStatus: CampaignValues.CanonStatuses.Planned);
        _seed.Knowledge(_campaign.Id, "party", factId: plan.Id);

        var cells = Build(facts: [plan.SeqHandle], perspectives: ["party", "author"]).Rows.Single().Cells;

        Assert.Equal([(Standings.NotInPlay, "not in play (planned)"), (Standings.Knows, "knows")], cells.Select(c => (c.Standing, c.Text)));
    }

    /// <summary>The knowledge resource prints a fact as the view knows it: its phrasing, never the statement behind it.</summary>
    [Fact]
    public void KnownTo_FactThePartyKnowsUnderAPhrasing_ShowsOnlyThePhrasing()
    {
        var order = _seed.Fact(_campaign.Id, "Keras gave the order to burn the docks.");
        _seed.Knowledge(_campaign.Id, "party", factId: order.Id, knownAs: "The old king gave an order.");

        var known = new KnowledgeLedger(_db.Database).KnownTo(Row, Perspective.Parse("party"), limit: 50);

        var fact = Assert.Single(known.Facts, f => f.Ref == order.SeqHandle);
        Assert.Equal("The old king gave an order.", fact.Text);
        LeakAssert.Clean(known, ["Keras", "burn", "docks"], "knowledge resource phrasing");
    }

    /// <summary>
    /// The resource lists the entities a view has a record for: an entity it may merely see by visibility (another
    /// knower's row makes it a candidate) is not "known" to it, and listing it would pad the resource with the world.
    /// </summary>
    [Fact]
    public void KnownTo_EntityThePartySeesOnlyByVisibility_IsNotListedButTheCharacterWithARowSeesIt()
    {
        var mira = _seed.Entity(_campaign.Id, "character", "Mira", subtype: "npc");
        _seed.Knowledge(_campaign.Id, "character", _hero.Id, CampaignValues.KnowledgeStates.Met, entityId: mira.Id);

        var party = new KnowledgeLedger(_db.Database).KnownTo(Row, Perspective.Parse("party"), limit: 50);
        var hero = new KnowledgeLedger(_db.Database).KnownTo(Row, Perspective.Parse("character:hero"), limit: 50);

        Assert.DoesNotContain(party.Entities, e => e.Ref == mira.Handle);
        var met = Assert.Single(hero.Entities, e => e.Ref == mira.Handle);
        Assert.Equal(CampaignValues.KnowledgeStates.Met, met.State);
    }

    /// <summary>
    /// An entity is the party's from the session it entered the story in (review L02, fix FQ1): its introduced session, else
    /// the session it was written in. Captain Rhee was written during session 2, which Serif missed, with no introduced
    /// session given; dated by the introduced session alone he was undated, so every reader showed Serif his name and
    /// summary (and the check passed his name in Serif's mouth) while the fact written in the same batch was hidden from
    /// him. Written between sessions he is undated (world-building), and an introduced session given (S1, when Serif was
    /// there) wins over the session he was written in. The party and Hero, present in S2, know him either way.
    /// </summary>
    [Theory]
    [InlineData("written in S2", false)]
    [InlineData("written between sessions", true)]
    [InlineData("introduced in S1, written in S2", true)]
    public void EveryReader_AnEntityWrittenInASessionAMemberMissed_IsNotTheirs(string how, bool serifSees)
    {
        var s1 = _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var serif = _seed.Entity(_campaign.Id, "character", "Serif", subtype: "pc");
        _seed.MemberOf(_campaign.Id, serif.Id, _campaign.Party!.Id);
        _seed.Attendance(s1.EntityId, _hero.Id);
        _seed.Attendance(s1.EntityId, serif.Id);
        _seed.Attendance(s2.EntityId, _hero.Id);
        _seed.Attendance(s2.EntityId, serif.Id, present: false);
        _db.Batch(_campaign.Id, r => r.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["kind"] = "character", ["subtype"] = "npc", ["slug"] = "captain-rhee", ["name"] = "Captain Rhee",
            ["summary"] = "Commands the harbour fleet.", ["visibility"] = CampaignValues.Visibilities.Party,
            ["introduced_session_id"] = how.StartsWith("introduced", StringComparison.Ordinal) ? s1.EntityId : null,
        }, "upsert"), sessionId: how == "written between sessions" ? null : s2.EntityId);
        var serifView = Perspective.Parse("character:serif");

        var search = new CampaignSearch(_db.Database).Search(Row, new SearchRequest("Rhee", Perspective: serifView));
        var listing = new CampaignSearch(_db.Database).Search(Row, new SearchRequest(Kinds: ["character"], Perspective: serifView, Limit: 50));
        var cells = Build(about: ["character:captain-rhee"], perspectives: ["character:serif", "character:hero", "party"]).Rows[0].Cells;
        var check = new KnowledgeCheck(_db.Database).Check(Row, new CheckRequest("Captain Rhee commands the fleet", serifView));

        Assert.Equal(serifSees, search.Entities.Any(e => e.Ref == "character:captain-rhee"));
        Assert.Equal(serifSees, listing.Entities.Any(e => e.Ref == "character:captain-rhee"));
        Assert.Equal(serifSees ? Standings.Knows : Standings.Uncertain, cells[0].Standing);
        Assert.Equal([Standings.Knows, Standings.Knows], cells.Skip(1).Select(c => c.Standing));
        Assert.Equal(serifSees ? NameClasses.Ok : NameClasses.UnknownEntity, Assert.Single(check.Names).Classification);
        if (serifSees)
        {
            Assert.Single(new EntityReader(_db.Database).Get(Row, ["character:captain-rhee"], perspective: serifView).Entities);
            return;
        }

        Assert.Equal("uncertain: party-visible since S2; Serif was absent", cells[0].Text);
        Assert.Throws<DndInputException>(() => new EntityReader(_db.Database).Get(Row, ["character:captain-rhee"], perspective: serifView));
        foreach (var result in new object[] { search, listing })
        {
            LeakAssert.Clean(result, ["Rhee", "harbour"], "an NPC met in a session Serif missed");
        }
    }

    /// <summary>
    /// The ledger as of a session says an entry made later did not exist yet (review C08, as campaign_get as_of does), not
    /// "nothing in this campaign has that handle", which was false and sent the model to retry the same call.
    /// </summary>
    [Fact]
    public void Build_AsOfASessionBeforeAnEntryWasMade_SaysItDidNotExistThen()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        string factId = null!;
        _db.Batch(_campaign.Id, r =>
        {
            r.Insert("entity", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["kind"] = "character", ["slug"] = "bram", ["name"] = "Bram" }, "upsert");
            factId = (string)r.Insert("fact", new Dictionary<string, object?> { ["campaign_id"] = _campaign.Id, ["statement"] = "Bram arrived." }, "fact")["id"]!;
        }, sessionId: s2.EntityId);
        var fact = "f:" + Dapper.SqlMapper.ExecuteScalar<long>(_connection, "SELECT seq FROM fact WHERE id = @factId", new { factId });
        var ledger = new KnowledgeLedger(_db.Database);

        var about = Assert.Throws<DndInputException>(() => ledger.Build(Row, ["character:bram"], null, null, 1));
        var facts = Assert.Throws<DndInputException>(() => ledger.Build(Row, null, [fact], null, 1));

        Assert.Contains("about item 1 (character:bram): \"character:bram\" did not exist as of S1: it was made in S2. Leave out as_of_session",
            about.Message, StringComparison.Ordinal);
        Assert.Contains($"facts item 1 ({fact}): \"{fact}\" did not exist as of S1: it was made in S2.", facts.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing in this campaign", about.Message + facts.Message, StringComparison.Ordinal);
        Assert.Single(ledger.Build(Row, ["character:bram"], null, null, 2).Rows);
        Assert.Contains("nothing in this campaign has that handle", Assert.Throws<DndInputException>(() => ledger.Build(Row, ["character:nobody"], null, null, 1)).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The author's knowledge page as of a session labels each entry with who knew it then (review C07, fix FQ5): the party
    /// learned the fact in S2, so as of S1 nobody holds it yet, as the ledger's own cells say.
    /// </summary>
    [Fact]
    public void KnownTo_AuthorAsOfASession_LabelsEachEntryWithItsKnowersThen()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var mira = _seed.Entity(_campaign.Id, "character", "Mira", subtype: "npc", visibility: CampaignValues.Visibilities.Restricted);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: mira.Id, learnedSessionId: s2.EntityId);
        var fact = _seed.Fact(_campaign.Id, "The ship sank.");
        _seed.Knowledge(_campaign.Id, "party", factId: fact.Id, learnedSessionId: s2.EntityId);
        var reader = new KnowledgeLedger(_db.Database);

        var then = reader.KnownTo(Row, Perspective.Author, asOfSession: 1, limit: 50);
        var now = reader.KnownTo(Row, Perspective.Author, limit: 50);

        Assert.Empty(Assert.Single(then.Facts, f => f.Ref == fact.SeqHandle).KnownBy!);
        Assert.Empty(Assert.Single(then.Entities, e => e.Ref == mira.Handle).KnownBy!);
        Assert.Equal(["party"], Assert.Single(now.Facts, f => f.Ref == fact.SeqHandle).KnownBy);
        Assert.Equal(["party"], Assert.Single(now.Entities, e => e.Ref == mira.Handle).KnownBy);
    }

    /// <summary>
    /// A party-visible fact is the party's from the session it was established in (review L02): Tristan left the party in
    /// S1, so the bridge that fell in S3 is not in the knowledge page his in-character drafts read first, nor anywhere else
    /// of his, and the ledger says why; the party's own view and a member who was there are unchanged.
    /// </summary>
    [Fact]
    public void KnownTo_AFormerMember_HasNoPartyFactEstablishedAfterHeLeft()
    {
        var s1 = _seed.Session(_campaign.Id, 1);
        _seed.Session(_campaign.Id, 2);
        var s3 = _seed.Session(_campaign.Id, 3, title: "Sorrowmere", recap: "The bridge at Sorrowmere fell behind us.");
        var tristan = _seed.Entity(_campaign.Id, "character", "Tristan", subtype: "pc");
        _seed.MemberOf(_campaign.Id, tristan.Id, _campaign.Party!.Id, untilSessionId: s1.EntityId);
        var bridge = _seed.Fact(_campaign.Id, "The bridge at Sorrowmere fell.", visibility: CampaignValues.Visibilities.Party,
            canonStatus: CampaignValues.CanonStatuses.Played, establishedSessionId: s3.EntityId);
        var reader = new KnowledgeLedger(_db.Database);

        var his = reader.KnownTo(Row, Perspective.Parse("character:tristan"), limit: 50);
        var get = Assert.Throws<DndInputException>(() => new EntityReader(_db.Database).Get(Row, [bridge.SeqHandle], perspective: Perspective.Parse("character:tristan")));
        var search = new CampaignSearch(_db.Database).Search(Row, new SearchRequest("Sorrowmere", Perspective: Perspective.Parse("character:tristan")));
        var cell = Assert.Single(Build(facts: [bridge.SeqHandle], perspectives: ["character:tristan"]).Rows[0].Cells);
        var party = reader.KnownTo(Row, Perspective.Parse("party"), limit: 50);
        var hero = reader.KnownTo(Row, Perspective.Parse("character:hero"), limit: 50);

        Assert.DoesNotContain(his.Facts, f => f.Ref == bridge.SeqHandle);
        Assert.Contains("nothing by that handle", get.Message, StringComparison.Ordinal);
        Assert.Empty(search.Entities.Concat<object>(search.Facts));
        Assert.Equal((Standings.Uncertain, "uncertain: party-visible since S3, after Tristan left in S1"), (cell.Standing, cell.Text));
        LeakAssert.Clean(his, ["Sorrowmere", "bridge"], "a former member's knowledge page");
        Assert.Contains(party.Facts, f => f.Ref == bridge.SeqHandle);
        Assert.Contains(hero.Facts, f => f.Ref == bridge.SeqHandle);
    }
}
