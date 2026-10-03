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
/// Invariant: campaign_get's parts each follow the perspective rules: a renamed entity's slug (its old name) is never
/// printed or accepted for a player view; relations need their own visibility and two visible ends and are never
/// planned; objectives, clocks, children and attendance show only what the view may see (with no gaps that count what
/// was left out); and as_of reads replay names, aliases and relations to the end of the session.
/// </summary>
public sealed class EntityReaderTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;

    public EntityReaderTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Test", role: CampaignValues.Roles.Player);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private GetResult Get(string perspective, EntityIncludes includes, int? asOf, params string[] refs) =>
        new EntityReader(_db.Database).Get(_seed.LoadCampaign(_campaign.Id), refs, includes, Perspective.Parse(perspective), asOf);

    private EntityDetail One(string perspective, string reference, EntityIncludes? includes = null, int? asOf = null) =>
        Assert.Single(Get(perspective, includes ?? EntityIncludes.All, asOf, reference).Entities);

    /// <summary>
    /// A renamed entity keeps its slug, which now spells its OLD name. A player view sees the new name under e:&lt;n&gt;,
    /// and typing the old slug finds nothing (it would confirm the old name belongs to it).
    /// </summary>
    [Fact]
    public void Get_RenamedEntityAsParty_NeverPrintsOrAcceptsTheSlugOfItsOldName()
    {
        var king = _seed.Entity(_campaign.Id, "character", "The Old King", slug: "keras");
        // The old name kept as an author alias: a slug may be printed when it spells a SHOWN alias, never a hidden one.
        _seed.Alias(king.Id, "Keras", V.Author);

        var bySeq = One("party", "e:" + king.Seq);
        var bySlug = Assert.Throws<DndInputException>(() => Get("party", EntityIncludes.All, null, "character:keras"));
        var author = One("author", "character:keras");

        Assert.Equal(("e:" + king.Seq, "The Old King"), (bySeq.Ref, bySeq.DisplayName));
        Assert.Contains("nothing by that handle", bySlug.Message, StringComparison.Ordinal);
        Assert.Equal("character:keras", author.Ref);
        LeakAssert.Clean(bySeq, ["keras"], "renamed entity");
    }

    [Fact]
    public void Get_Relations_ShowOnlyVisibleCurrentEdgesBetweenVisibleEnds()
    {
        var hero = _seed.Entity(_campaign.Id, "character", "Hero");
        var ally = _seed.Entity(_campaign.Id, "character", "Ally");
        var hiddenVillain = _seed.Entity(_campaign.Id, "character", "Hidden Villain", visibility: V.Author);
        var clerk = _seed.Entity(_campaign.Id, "character", "The Hollow Prince", visibility: V.Restricted);
        _seed.Knowledge(_campaign.Id, "party", state: "unrecognized", entityId: clerk.Id, knownAs: "the quiet clerk");
        _seed.Relation(_campaign.Id, hero.Id, "ally_of", ally.Id, label: "sworn");
        _seed.Relation(_campaign.Id, hero.Id, "enemy_of", hiddenVillain.Id);
        _seed.Relation(_campaign.Id, hero.Id, "rival_of", ally.Id, visibility: V.Author);
        _seed.Relation(_campaign.Id, hero.Id, "serves", ally.Id, status: "planned");
        _seed.Relation(_campaign.Id, clerk.Id, "watches", hero.Id);

        var party = One("party", "character:hero");
        var author = One("author", "character:hero");

        Assert.Equal([("ally_of", "out", "Ally"), ("watches", "in", "the quiet clerk")],
            party.Relations!.Select(r => (r.Rel, r.Direction, r.Other.Name)));
        Assert.Equal("e:" + clerk.Seq, party.Relations!.Single(r => r.Rel == "watches").Other.Ref);
        Assert.All(party.Relations!, r => Assert.Null(r.Visibility));
        Assert.Equal(5, author.Relations!.Count);
        Assert.Contains(author.Relations!, r => r.Rel == "enemy_of" && r.Other.Name == "Hidden Villain" && r.Visibility == "party");
        LeakAssert.Clean(party, ["Hidden Villain", "Hollow", "rival_of", "serves", "enemy_of"], "relations");
    }

    [Fact]
    public void Get_Objectives_HideAuthorAndHiddenOnesWithoutGapsInTheNumbering()
    {
        var quest = _seed.Entity(_campaign.Id, "quest", "Find the axe");
        _seed.Objective(quest.Id, "Find the first shard");
        _seed.Objective(quest.Id, "The shard is a lie", visibility: V.Author);
        _seed.Objective(quest.Id, "Secret step", status: "hidden");
        _seed.Objective(quest.Id, "Reforge it", progress: 1, progressMax: 3);

        var party = One("party", "quest:find-the-axe");
        var author = One("author", "quest:find-the-axe");

        Assert.Equal([(1, "Find the first shard"), (2, "Reforge it")], party.Objectives.Select(o => (o.Index, o.Text)));
        Assert.Equal([1, 2, 3, 4], author.Objectives.Select(o => o.Index));
        Assert.Equal((1L, (long?)3L), (party.Objectives[1].Progress!.Value, party.Objectives[1].ProgressMax));
    }

    [Fact]
    public void Get_ClockNotShownToPlayers_IsAbsentForThemAndFullForTheAuthor()
    {
        var hidden = _seed.Entity(_campaign.Id, "clock", "Doom", status: "running");
        _seed.Clock(hidden.Id, 6, filled: 2, onFillMd: "The city burns.");
        var shown = _seed.Entity(_campaign.Id, "clock", "Storm", status: "running");
        _seed.Clock(shown.Id, 4, filled: 1, shownToPlayers: true, onFillMd: "The storm breaks the dam.");

        Assert.Null(One("party", "clock:doom").Clock);
        var storm = One("party", "clock:storm").Clock!;
        Assert.Equal((4L, 1L, (string?)null, (bool?)null), (storm.Segments, storm.Filled, storm.OnFillMd, storm.ShownToPlayers));
        Assert.Equal("The city burns.", One("author", "clock:doom").Clock!.OnFillMd);
    }

    [Fact]
    public void Get_Children_AreOnlyTheVisibleOnes()
    {
        var city = _seed.Entity(_campaign.Id, "location", "Flotsam");
        _seed.Entity(_campaign.Id, "location", "The Docks", parentId: city.Id, sortKey: 2);
        _seed.Entity(_campaign.Id, "location", "The Market", parentId: city.Id, sortKey: 1);
        _seed.Entity(_campaign.Id, "location", "The Smugglers' Den", parentId: city.Id, visibility: V.Author);

        var party = One("party", "location:flotsam");

        Assert.Equal(["The Market", "The Docks"], party.Children!.Select(c => c.Name));
        Assert.Equal(3, One("author", "location:flotsam").Children!.Count);
    }

    [Fact]
    public void Get_SessionsInclude_ShowsAttendanceWithTheNoteForTheAuthorOnly()
    {
        var pc = _seed.Entity(_campaign.Id, "character", "Belmakor", subtype: "pc");
        _seed.MemberOf(_campaign.Id, pc.Id, _campaign.Party!.Id);
        var s1 = _seed.Session(_campaign.Id, 1, title: "The Iron Guts job");
        var s2 = _seed.Session(_campaign.Id, 2);
        _seed.Attendance(s1.EntityId, pc.Id, note: "ran the villain too");
        _seed.Attendance(s2.EntityId, pc.Id, present: false);

        var party = One("party", "character:belmakor", new EntityIncludes(Sessions: true));
        var author = One("author", "character:belmakor", new EntityIncludes(Sessions: true));

        Assert.Equal([("session:1", "attended", (string?)null), ("session:2", "absent", null)],
            party.Sessions!.Select(s => (s.Ref, s.Relation, s.Note)));
        Assert.Equal("ran the villain too", author.Sessions![0].Note);
    }

    [Fact]
    public void Get_AsOfEarlierSession_ReplaysNameAliasesAndRelations()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var npc = _seed.Entity(_campaign.Id, "character", "Stranger");
        var inn = _seed.Entity(_campaign.Id, "location", "Inn");
        _seed.Alias(npc.Id, "the hooded man", V.Party);
        _db.Batch(_campaign.Id, r =>
        {
            r.Update("entity", npc.Id, new Dictionary<string, object?> { ["name"] = "Vars Nocturne" }, "upsert");
            r.Insert("entity_alias", new Dictionary<string, object?> { ["entity_id"] = npc.Id, ["alias"] = "Vars", ["visibility"] = V.Party }, "upsert");
            r.Delete("entity_alias", [npc.Id, "the hooded man"], "upsert");
            r.Insert("relation", new Dictionary<string, object?>
            {
                ["campaign_id"] = _campaign.Id, ["from_id"] = npc.Id, ["rel"] = "located_in", ["to_id"] = inn.Id, ["data"] = "{}",
            }, "link");
        }, sessionId: s2.EntityId);

        var then = One("author", "e:" + npc.Seq, asOf: 1);
        var now = One("author", "e:" + npc.Seq);

        Assert.Equal("Stranger", then.DisplayName);
        Assert.Equal(["the hooded man"], then.Aliases.Select(a => a.Alias));
        Assert.Empty(then.Relations!);
        Assert.Equal("Vars Nocturne", now.DisplayName);
        Assert.Equal(["Vars"], now.Aliases.Select(a => a.Alias));
        Assert.Single(now.Relations!);
    }

    [Fact]
    public void Get_EntityCreatedAfterTheSession_IsNotFoundAsOfThatSession()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        _db.Batch(_campaign.Id, r => r.Insert("entity", new Dictionary<string, object?>
        {
            ["campaign_id"] = _campaign.Id, ["kind"] = "location", ["slug"] = "new-port", ["name"] = "New Port",
        }, "upsert"), sessionId: s2.EntityId);

        var author = Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default, 1, "location:new-port"));
        var party = Assert.Throws<DndInputException>(() => Get("party", EntityIncludes.Default, 1, "location:new-port"));
        Assert.Single(Get("author", EntityIncludes.Default, 2, "location:new-port").Entities);

        // The author is told what happened (review C08), not "nothing has that handle. Did you mean location:new-port?"; any
        // other view gets the not-found that a hidden entry gets too, so "made later" never confirms a hidden one.
        Assert.Contains("\"location:new-port\" did not exist as of S1: it was made in S2. Leave out as_of_session to read it as it is now.",
            author.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", author.Message, StringComparison.Ordinal);
        Assert.Contains("\"location:new-port\": nothing by that handle for this perspective.", party.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not exist", party.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An author read as of a session labels each fact with who knew it then (review C07): the party learned it in S2, so as
    /// of S1 it is known by no one yet, as the knowledge section of the same page says; the search's labels agree.
    /// </summary>
    [Fact]
    public void Get_AuthorAsOfASession_LabelsEachFactWithItsKnowersThen()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var mira = _seed.Entity(_campaign.Id, "character", "Mira");
        var spy = _seed.Fact(_campaign.Id, "Mira is a spy.");
        _seed.FactLink(spy.Id, mira.Id);
        _seed.Knowledge(_campaign.Id, "party", factId: spy.Id, learnedSessionId: s2.EntityId);

        Assert.Empty(Assert.Single(One("author", mira.Handle, asOf: 1).Facts!).Author!.KnownBy);
        Assert.Equal(["party"], Assert.Single(One("author", mira.Handle, asOf: 2).Facts!).Author!.KnownBy);
        Assert.Equal(["party"], Assert.Single(One("author", mira.Handle).Facts!).Author!.KnownBy);
        var search = new CampaignSearch(_db.Database).Search(_seed.LoadCampaign(_campaign.Id), new SearchRequest("spy", AsOfSession: 1));
        Assert.Empty(Assert.Single(search.Facts).KnownBy!);
    }

    /// <summary>
    /// A disguised entity's page lists the party aliases its disguise shares with the view (review U01, FD4): the party
    /// knows Keras as "the ancient sorcerer king" and calls him "the old king" and "the sorcerer king" too, names its search
    /// finds him by and its check accepts, and the page showed none of them. Never his true name, an alias that spells it,
    /// a public epithet of his true identity or an author alias, and no visibility labels.
    /// </summary>
    [Fact]
    public void Get_DisguisedEntity_ListsThePartyAliasesItsDisguiseSharesAndNothingElse()
    {
        var keras = _seed.Entity(_campaign.Id, "character", "Keras", subtype: "npc", visibility: V.Party, summary: "Rules the ruined throne.");
        _seed.Alias(keras.Id, "the old king", V.Party);
        _seed.Alias(keras.Id, "the sorcerer king", V.Party);
        _seed.Alias(keras.Id, "King Keras", V.Party);
        _seed.Alias(keras.Id, "the Lich King", V.Public);
        _seed.Alias(keras.Id, "the Undying", V.Author);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: keras.Id, knownAs: "the ancient sorcerer king");

        var party = One("party", "e:" + keras.Seq);

        Assert.Equal(("the ancient sorcerer king", "e:" + keras.Seq), (party.DisplayName, party.Ref));
        Assert.Equal(["the old king", "the sorcerer king"], party.Aliases.Select(a => a.Alias).Order(StringComparer.Ordinal));
        Assert.All(party.Aliases, a => Assert.Null(a.Visibility));
        Assert.Null(party.Summary);
        LeakAssert.Clean(party, ["Keras", "Lich", "Undying", "throne"], "a disguised entity's page");
    }

    /// <summary>
    /// An identity relation (same_as) to an end the view knows only under a disguise is not listed on the other end's page
    /// (review L07): "the Lich Queen ← same_as from the veiled woman" told the party who the veiled woman is. Other relations
    /// to the disguised end stay, under its disguise; the author sees them all.
    /// </summary>
    [Fact]
    public void Get_SameAsToAnEndTheViewKnowsUnderADisguise_IsNotListed()
    {
        var morwen = _seed.Entity(_campaign.Id, "character", "Morwen Vashkar", subtype: "npc", visibility: V.Restricted);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: morwen.Id, knownAs: "the veiled woman");
        var legend = _seed.Entity(_campaign.Id, "lore", "The Lich Queen", summary: "A legend sung in every port.");
        _seed.Relation(_campaign.Id, morwen.Id, CampaignValues.Rels.SameAs, legend.Id, label: "her true identity");
        _seed.Relation(_campaign.Id, morwen.Id, "haunts", legend.Id);

        var party = One("party", legend.Handle);
        var author = One("author", legend.Handle);

        var relation = Assert.Single(party.Relations!);
        Assert.Equal(("haunts", "the veiled woman"), (relation.Rel, relation.Other.Name));
        Assert.Equal(["haunts", "same_as"], author.Relations!.Select(r => r.Rel));
        LeakAssert.Clean(party, ["same_as", "true identity", "Morwen"], "an identity relation to a disguised end");
    }

    /// <summary>
    /// A beat's page shows the author its place in the story web (review C03): what it waits on (each edge's mode and that
    /// beat's status), what it leads to, and whether it can happen next. The web is the author's prep: no other view gets it.
    /// </summary>
    [Fact]
    public void Get_Beat_ShowsTheAuthorItsPrerequisitesWhatItLeadsToAndWhetherItIsReachable()
    {
        var map = _seed.Entity(_campaign.Id, "beat", "Find the map");
        var key = _seed.Entity(_campaign.Id, "beat", "Steal the key", status: CampaignValues.Statuses.BeatMet);
        var vault = _seed.Entity(_campaign.Id, "beat", "Storm the vault");
        var escape = _seed.Entity(_campaign.Id, "beat", "Escape");
        var cut = _seed.Entity(_campaign.Id, "beat", "Bribe the guard", status: CampaignValues.Statuses.BeatCut);
        _seed.BeatEdge(_campaign.Id, map.Id, vault.Id);
        _seed.BeatEdge(_campaign.Id, key.Id, vault.Id);
        _seed.BeatEdge(_campaign.Id, cut.Id, vault.Id);
        _seed.BeatEdge(_campaign.Id, vault.Id, escape.Id, CampaignValues.BeatEdgeModes.AnyOf);

        var stormed = One("author", vault.Handle).Beat!;
        var found = One("author", map.Handle).Beat!;

        Assert.Equal((BeatStandings.Blocked, BeatGates.AllOf, 1, 2), (stormed.Standing, stormed.Gate, stormed.MetCount, stormed.TotalCount));
        Assert.Equal([map.Handle], stormed.BlockedBy.Select(b => b.Ref));
        Assert.Equal(["Bribe the guard all_of cut", "Find the map all_of pending", "Steal the key all_of met"],
            stormed.After.Select(e => $"{e.Beat.Name} {e.Mode} {e.Status}"));
        Assert.Equal(["Escape any_of pending"], stormed.LeadsTo.Select(e => $"{e.Beat.Name} {e.Mode} {e.Status}"));
        Assert.Equal((BeatStandings.Reachable, 0), (found.Standing, found.TotalCount));
        Assert.Equal(BeatStandings.Met, One("author", key.Handle).Beat!.Standing);
        Assert.Equal(BeatStandings.Cut, One("author", cut.Handle).Beat!.Standing);
        Assert.Equal((BeatStandings.Blocked, BeatGates.AnyOf), (One("author", escape.Handle).Beat!.Standing, One("author", escape.Handle).Beat!.Gate));
        Assert.Null(One("party", vault.Handle).Beat);
        Assert.Null(One("author", "faction:the-party").Beat);
    }

    /// <summary>
    /// The author's detail carries the entity's e:&lt;n&gt; (review U05): the one handle every view accepts, where a view
    /// that knows it under another name refuses its kind:slug. No other view's detail has it (its Author record is null).
    /// </summary>
    [Fact]
    public void Get_AuthorDetail_CarriesTheSeqHandleEveryViewAccepts()
    {
        var cage = _seed.Entity(_campaign.Id, "item", "Axiom Cage");
        _seed.Knowledge(_campaign.Id, "party", state: "heard", entityId: cage.Id, knownAs: "what the old king sent us for");

        var author = One("author", cage.Handle);

        Assert.Equal((cage.Handle, cage.SeqHandle), (author.Ref, author.Author!.SeqRef));
        Assert.Equal("what the old king sent us for", One("party", author.Author.SeqRef!).DisplayName);
        Assert.Throws<DndInputException>(() => One("party", cage.Handle));
    }

    /// <summary>The same for a fact made in a later session, read by <c>f:&lt;n&gt;</c> or by its code.</summary>
    [Fact]
    public void Get_FactMadeAfterTheSession_SaysItDidNotExistThenToTheAuthor()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var id = CampaignDatabase.NewId();
        _db.Batch(_campaign.Id, r => r.Insert("fact", new Dictionary<string, object?>
        {
            ["id"] = id, ["campaign_id"] = _campaign.Id, ["code"] = "F7", ["statement"] = "The bridge fell.",
        }, "fact"), sessionId: s2.EntityId);
        var seq = Dapper.SqlMapper.ExecuteScalar<long>(_connection, "SELECT seq FROM fact WHERE id = @id", new { id });

        foreach (var handle in new[] { "f:" + seq, "F7" })
        {
            var error = Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default, 1, handle));
            Assert.Contains($"\"{handle}\" did not exist as of S1: it was made in S2.", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Get_EntityDeletedAfterTheSession_IsFoundAsOfThatSession()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var port = _seed.Entity(_campaign.Id, "location", "Old Port");
        _db.Batch(_campaign.Id, r => r.SoftDelete("entity", port.Id), sessionId: s2.EntityId);

        Assert.Single(Get("author", EntityIncludes.Default, 1, "location:old-port").Entities);
        Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default, null, "location:old-port"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void Get_AsOfSessionOutOfRange_IsRefused(int session)
    {
        _seed.Entity(_campaign.Id, "location", "Port");

        var ex = Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default, session, "location:port"));

        Assert.Contains("as_of_session", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_CodeNamingBothAnEntityAndAFact_IsRefusedForTheAuthorNamingBoth()
    {
        _seed.Entity(_campaign.Id, "location", "Tern Rock", code: "F3");
        var fact = _seed.Fact(_campaign.Id, "Tern Rock floats.", code: "F3");

        var ex = Assert.Throws<DndInputException>(() => Get("author", EntityIncludes.Default, null, "F3"));

        Assert.Contains("location:tern-rock", ex.Message, StringComparison.Ordinal);
        Assert.Contains(fact.SeqHandle, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>For a player view only what it may see counts: a code on a visible entity and a hidden fact is the entity.</summary>
    [Fact]
    public void Get_CodeOnAVisibleEntityAndAHiddenFact_IsTheEntityForThePartyWithNoMentionOfTheFact()
    {
        _seed.Entity(_campaign.Id, "location", "Tern Rock", code: "Q3");
        var fact = _seed.Fact(_campaign.Id, "Tern Rock is the Cage's anchor.", code: "Q3");

        var result = Get("party", EntityIncludes.All, null, "Q3");

        Assert.Equal("location:tern-rock", Assert.Single(result.Entities).Ref);
        LeakAssert.Clean(result, [fact.SeqHandle + "\"", "anchor", "Cage"], "a shared code");
    }

    [Fact]
    public void Get_IncludesParse_AcceptsLooseSpellingAndRefusesUnknownNames()
    {
        Assert.Equal(new EntityIncludes(Relations: true, History: true), EntityIncludes.Parse(["Relations", "HISTORY"]));
        Assert.Equal(EntityIncludes.Default, EntityIncludes.Parse(null));
        Assert.Equal(new EntityIncludes(), EntityIncludes.Parse([]));
        var ex = Assert.Throws<DndInputException>(() => EntityIncludes.Parse(["facts", "secrets"]));
        Assert.Contains("include item 2 (\"secrets\")", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A knowledge row's via entity and note are author text: a player view's knowledge lines never carry them.</summary>
    [Fact]
    public void Get_KnowledgeIncludeForAPlayerView_NeverPrintsTheViaNameOrTheNote()
    {
        var informant = _seed.Entity(_campaign.Id, "character", "Keras", visibility: V.Author);
        var map = _seed.Entity(_campaign.Id, "item", "The Map");
        var fact = _seed.Fact(_campaign.Id, "The map leads north.");
        _seed.FactLink(fact.Id, map.Id);
        _seed.Knowledge(_campaign.Id, "party", factId: fact.Id, viaEntityId: informant.Id, how: "told", note: "Keras lied about the rest");

        var party = One("party", "item:the-map");
        var author = One("author", "item:the-map");

        Assert.Equal(2, party.Knowledge!.Count);
        LeakAssert.Clean(party, ["Keras", "lied", "told"], "via and note");
        var row = Assert.Single(author.Knowledge!, k => k.Target == fact.SeqHandle);
        Assert.Equal(("character:keras", "Keras lied about the rest", "told"), (row.Author!.Via, row.Author.Note, row.Author.How));
    }

    /// <summary>
    /// "misbelieves" is the author's verdict that a belief is false: the believer's own view shows "believes" (and no
    /// truth), or the view would know its belief is wrong.
    /// </summary>
    [Fact]
    public void Get_AFactACharacterMisbelieves_ShowsBelievesToThatCharacter()
    {
        // A party member, so his own view sees his own entity and the explanation names him (review L04).
        var pc = _seed.Entity(_campaign.Id, "character", "Bard", subtype: "pc");
        _seed.MemberOf(_campaign.Id, pc.Id, _campaign.Party!.Id);
        var rumor = _seed.Fact(_campaign.Id, "The duke is loyal.", truth: "false");
        _seed.Knowledge(_campaign.Id, "character", pc.Id, "misbelieves", factId: rumor.Id);

        var mine = Assert.Single(Get("character:bard", new EntityIncludes(Knowledge: true), null, rumor.SeqHandle).Facts);
        var author = Assert.Single(Get("author", new EntityIncludes(Knowledge: true), null, rumor.SeqHandle).Facts);

        var line = Assert.Single(mine.Knowledge!);
        Assert.Equal(("believes", "Bard's own record: believes"), (line.State, line.Explanation));
        LeakAssert.Clean(mine, ["misbelieves", "false"], "a misbelief");
        Assert.Equal("misbelieves", Assert.Single(author.Knowledge!).State);
    }

    /// <summary>A parent the view cannot see is simply absent: printing it would name an author-only faction.</summary>
    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("dm")]
    public void Get_ChildOfAnAuthorOnlyParent_HasNoParentForAPlayerView(string perspective)
    {
        var boss = _seed.Entity(_campaign.Id, "faction", "The Hidden Hand", visibility: V.Author);
        var mira = _seed.Entity(_campaign.Id, "character", "Mira", subtype: "npc", parentId: boss.Id);

        var party = One(perspective, mira.Handle);
        var author = One("author", mira.Handle);

        Assert.Null(party.Parent);
        Assert.Equal(("faction:the-hidden-hand", "The Hidden Hand"), (author.Parent!.Ref, author.Parent.Name));
        LeakAssert.Clean(party, ["Hidden Hand", "hidden-hand", "e:" + boss.Seq], $"hidden parent as {perspective}");
    }

    [Fact]
    public void Get_HistoryInclude_ListsTheNewestBatchesAndCountsThemAll()
    {
        var place = _seed.Entity(_campaign.Id, "location", "Quay");
        var fact = _seed.Fact(_campaign.Id, "The quay floods.");
        var batches = EntityReader.HistoryBatchesShown + 2;
        for (var i = 1; i <= batches; i++)
        {
            var text = "Version " + i;
            _db.Batch(_campaign.Id, r =>
            {
                r.Update("entity", place.Id, new Dictionary<string, object?> { ["summary"] = text }, "upsert");
                r.Update("fact", fact.Id, new Dictionary<string, object?> { ["statement"] = text + "." }, "fact");
            });
        }

        var result = Get("author", EntityIncludes.All, null, place.Handle, fact.SeqHandle);

        var entity = result.Entities.Single().Author!;
        Assert.Equal((EntityReader.HistoryBatchesShown, batches), (entity.History!.Count, entity.HistoryTotal));
        Assert.Contains("Version " + batches, entity.History[0].Changes[0].Text, StringComparison.Ordinal);
        var factAuthor = result.Facts.Single().Author!;
        Assert.Equal((EntityReader.HistoryBatchesShown, batches), (factAuthor.History!.Count, factAuthor.HistoryTotal));
        Assert.Null(One("author", place.Handle, EntityIncludes.Default).Author!.HistoryTotal);
    }

    /// <summary>
    /// Contract §3.4 on the read side: a secret is seeded when the party knows any clue_for fact, even one no gate names;
    /// a clue it merely suspects does not seed it (the write path's rule, so stored and derived statuses agree).
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.KnowledgeStates.Knows, "seeded")]
    [InlineData(CampaignValues.KnowledgeStates.Suspects, "hidden")]
    [InlineData(CampaignValues.KnowledgeStates.Unaware, "hidden")]
    public void Get_SecretWithAClueForFactOutsideItsGate_IsSeededWhenThePartyKnowsTheClue(string partyState, string derived)
    {
        var secret = _seed.Entity(_campaign.Id, "secret", "The duke's plot", visibility: V.Author);
        var coup = _seed.Fact(_campaign.Id, "The coup is set for the festival.");
        var plot = _seed.Fact(_campaign.Id, "The duke plots against the crown.", visibility: V.Author,
            gate: FactGates.Serialize(new GateSpec { After = [coup.Id] }));
        _seed.FactLink(plot.Id, secret.Id);
        var letter = _seed.Fact(_campaign.Id, "A letter bears the duke's seal.");
        _seed.FactLink(letter.Id, secret.Id, CampaignValues.FactLinkRoles.ClueFor);
        _seed.Knowledge(_campaign.Id, "party", state: partyState, factId: letter.Id);

        var status = One("author", secret.Handle).Author!.Secret!;

        Assert.Equal(derived, status.DerivedStatus);
        Assert.Equal([plot.SeqHandle], status.Gates.Select(g => g.Fact));
    }
}
