using Dapper;
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
/// Invariant: campaign_search's mechanics are perspective-safe beyond the goldens: pages and cursors count only what the
/// view may see; the partial-match retry is decided on visible results; status and tag filters cannot match what a view
/// is not shown; the known_as branch is the only way to a disguised entity; the true name of an entity known under an
/// alias is never printed or searchable (stage-1 finding 13); as_of hides rows created later; ranking uses all seven
/// bm25 weights; caps and bad input are refused with actionable messages.
/// </summary>
public sealed class CampaignSearchTests : IDisposable
{
    private readonly CampaignTestDb _db = new();
    private readonly SqliteConnection _connection;
    private readonly CampaignSeed _seed;
    private readonly SeededCampaign _campaign;

    public CampaignSearchTests()
    {
        _connection = _db.Open();
        _seed = new CampaignSeed(_connection);
        _campaign = _seed.Campaign(name: "Test", role: CampaignValues.Roles.Dm);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _db.Dispose();
    }

    private CampaignRow Row => _seed.LoadCampaign(_campaign.Id);

    private SearchResult Search(string? query, string perspective = "author", int? limit = null, string? cursor = null,
        IReadOnlyList<string>? kinds = null, IReadOnlyList<string>? statuses = null, IReadOnlyList<string>? tags = null, int? asOf = null) =>
        new CampaignSearch(_db.Database).Search(Row, new SearchRequest(query, kinds, statuses, tags, Perspective.Parse(perspective),
            AsOfSession: asOf, Limit: limit, Cursor: cursor));

    /// <summary>
    /// Stage-1 finding 13: an entity whose NAME is the secret ("Keras"), known to the party by its party alias. It must be
    /// disguised: the party sees "the old king" and <c>e:&lt;n&gt;</c>, never "Keras", its summary or <c>character:keras</c>.
    /// </summary>
    [Fact]
    public void Search_EntityNamedWithItsTrueNameKnownByAPartyAlias_IsShownOnlyAsTheAlias()
    {
        var keras = _seed.Entity(_campaign.Id, "character", "Keras", subtype: "npc", summary: "Keras rules the ruined throne.");
        _seed.Alias(keras.Id, "the old king", V.Party);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: keras.Id, knownAs: "the old king");

        var byAlias = Search("old king", "party");
        var byName = Search("Keras", "party");
        var bySummary = Search("throne", "party");
        var listing = Search(null, "party");

        var hit = Assert.Single(byAlias.Entities);
        Assert.Equal("e:" + keras.Seq, hit.Ref);
        Assert.Equal("the old king", hit.DisplayName);
        Assert.Empty(byName.Entities);
        Assert.Empty(bySummary.Entities);
        Assert.Contains(listing.Entities, e => e.Ref == "e:" + keras.Seq && e.DisplayName == "the old king");
        foreach (var result in new object[] { byAlias, byName, bySummary, listing })
        {
            LeakAssert.Clean(result, ["Keras", "throne", "character:keras"], "finding 13");
        }
    }

    [Fact]
    public void Search_PagesThroughVisibleResults_CountsNothingHidden()
    {
        for (var i = 1; i <= 12; i++)
        {
            _seed.Entity(_campaign.Id, "location", $"Harbor {i:00}", visibility: V.Party);
            _seed.Entity(_campaign.Id, "location", $"Harbor secret {i:00}", visibility: V.Author);
        }

        var first = Search("harbor", "party", limit: 5);
        var second = Search("harbor", "party", limit: 5, cursor: first.NextCursor);
        var third = Search("harbor", "party", limit: 5, cursor: second.NextCursor);

        Assert.Equal(12, first.Total);
        Assert.Equal(5, first.Entities.Count);
        Assert.Equal(5, second.Entities.Count);
        Assert.Equal(2, third.Entities.Count);
        Assert.Null(third.NextCursor);
        var all = first.Entities.Concat(second.Entities).Concat(third.Entities).Select(e => e.Ref).ToList();
        Assert.Equal(12, all.Distinct().Count());
        Assert.All(all, r => Assert.DoesNotContain("secret", r, StringComparison.Ordinal));
        Assert.Equal(24, Search("harbor", limit: 50).Total);
    }

    [Fact]
    public void Search_CursorFromAnotherQueryOrPerspective_IsRefused()
    {
        for (var i = 1; i <= 3; i++)
        {
            _seed.Entity(_campaign.Id, "location", $"Harbor {i}");
        }

        var first = Search("harbor", "party", limit: 1);

        var otherQuery = Assert.Throws<DndInputException>(() => Search("harb*", "party", limit: 1, cursor: first.NextCursor));
        var otherView = Assert.Throws<DndInputException>(() => Search("harbor", "table", limit: 1, cursor: first.NextCursor));
        var garbage = Assert.Throws<DndInputException>(() => Search("harbor", "party", cursor: "not-a-cursor"));
        Assert.Contains("without cursor", otherQuery.Message, StringComparison.Ordinal);
        Assert.Contains("cursor", otherView.Message, StringComparison.Ordinal);
        Assert.Contains("cursor", garbage.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-3)]
    public void Search_LimitOutOfRange_IsRefusedNamingTheRange(int limit)
    {
        var ex = Assert.Throws<DndInputException>(() => Search("x", limit: limit));

        Assert.Contains("1 to 50", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The partial-match retry is decided on what the view may see: here only a hidden entity matches both words, so the
    /// party's search retries with any word (and says so) exactly as it would if nothing matched at all.
    /// </summary>
    [Fact]
    public void Search_OnlyAHiddenEntityMatchesEveryWord_TheRetryStillRunsForThePlayerView()
    {
        _seed.Entity(_campaign.Id, "location", "Black Harbor", visibility: V.Author);
        _seed.Entity(_campaign.Id, "location", "Black Rock");

        var party = Search("black harbor", "party");
        var author = Search("black harbor");

        Assert.True(party.PartialMatch);
        Assert.Equal(["Black Rock"], party.Entities.Select(e => e.DisplayName));
        Assert.False(author.PartialMatch);
        Assert.Equal("Black Harbor", author.Entities[0].DisplayName);
    }

    [Fact]
    public void Search_OneWordQueryWithNoMatch_IsNotRetried()
    {
        _seed.Entity(_campaign.Id, "location", "Black Rock");

        var result = Search("harbor");

        Assert.False(result.PartialMatch);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void Search_TagFilterOnADisguisedEntity_NeverMatches()
    {
        var villain = _seed.Entity(_campaign.Id, "character", "The Hollow Prince", visibility: V.Restricted);
        _seed.Tag(_campaign.Id, villain.Id, "villain");
        _seed.Knowledge(_campaign.Id, "party", state: "unrecognized", entityId: villain.Id, knownAs: "the quiet clerk");
        var shown = _seed.Entity(_campaign.Id, "character", "Captain Vey");
        _seed.Tag(_campaign.Id, shown.Id, "Villain");

        var party = Search(null, "party", tags: ["villain"]);
        var author = Search(null, tags: ["VILLAIN"]);

        Assert.Equal(["Captain Vey"], party.Entities.Select(e => e.DisplayName));
        Assert.Equal(["Captain Vey", "The Hollow Prince"], author.Entities.Select(e => e.DisplayName).Order(StringComparer.Ordinal));
    }

    /// <summary>A disguised entity has no status for the view, so a status filter can neither find it nor reveal its status.</summary>
    [Fact]
    public void Search_StatusFilterOnADisguisedEntity_NeverMatches()
    {
        var villain = _seed.Entity(_campaign.Id, "character", "The Hollow Prince", status: "alive", visibility: V.Restricted);
        _seed.Knowledge(_campaign.Id, "party", state: "unrecognized", entityId: villain.Id, knownAs: "the quiet clerk");

        Assert.Empty(Search(null, "party", statuses: ["alive"]).Entities);
        Assert.Single(Search(null, statuses: ["alive"]).Entities);
    }

    [Fact]
    public void Search_LeanQuestionAsParty_ShowsOpen()
    {
        _seed.Entity(_campaign.Id, "question", "Who sank the Maru?", status: "lean");

        var hit = Assert.Single(Search("maru", "party").Entities);

        Assert.Equal("open", hit.Status);
        Assert.Equal("lean", Assert.Single(Search("maru").Entities).Status);
    }

    [Fact]
    public void Search_FactKnownUnderAPhrasing_MatchesOnlyThatPhrasingForTheView()
    {
        var fact = _seed.Fact(_campaign.Id, "Eating a devil fruit breaks part of the seal.");
        _seed.Knowledge(_campaign.Id, "party", factId: fact.Id, knownAs: "the white lines are a shell");

        var byPhrasing = Search("white shell", "party");
        var byStatement = Search("devil fruit", "party");
        var authorByStatement = Search("devil fruit");

        var hit = Assert.Single(byPhrasing.Facts);
        Assert.Equal("the white lines are a shell", hit.Text);
        Assert.Empty(byStatement.Facts);
        Assert.Equal(fact.SeqHandle, Assert.Single(authorByStatement.Facts).Ref);
        LeakAssert.Clean(byPhrasing, ["seal", "devil"], "a fact's phrasing");
    }

    /// <summary>
    /// The seven-weight bm25 (hidden_aliases 8): an author search where one entity has the word in its body (weight 1)
    /// and another only in a hidden alias ranks the alias first. With a six-weight call hidden_aliases gets 1 and the
    /// short body wins.
    /// </summary>
    [Fact]
    public void Search_AuthorRanking_WeighsAHiddenAliasAboveTheBody()
    {
        _seed.Entity(_campaign.Id, "location", "Quay", body: "zephyr");
        var aliased = _seed.Entity(_campaign.Id, "location", "Long Street",
            body: "a long body of words about harbors and nets and gulls and ropes and tides and the smell of tar in the morning");
        _seed.Alias(aliased.Id, "zephyr", V.Author);

        var result = Search("zephyr");

        Assert.Equal(["Long Street", "Quay"], result.Entities.Select(e => e.DisplayName));
        Assert.Equal("bm25(entity_fts, 10, 8, 4, 1, 1, 2, 8)", CampaignSearch.EntityRank);
    }

    [Fact]
    public void Search_AsOfBeforeAnEntityWasCreated_DoesNotListIt()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var now = _db.Database.Now();
        string? id = null;
        _db.Batch(_campaign.Id, r =>
        {
            id = CampaignDatabase.NewId();
            r.Insert("entity", new Dictionary<string, object?>
            {
                ["id"] = id, ["campaign_id"] = _campaign.Id, ["kind"] = "location", ["slug"] = "new-port", ["name"] = "New Port",
                ["summary"] = string.Empty, ["body_md"] = string.Empty, ["secret_md"] = string.Empty, ["visibility"] = V.Party,
                ["canon_status"] = "canon", ["confidence"] = "confirmed", ["data"] = "{}", ["created_at"] = now, ["updated_at"] = now,
            }, "upsert");
        }, sessionId: s2.EntityId);

        Assert.Empty(Search("port", asOf: 1).Entities);
        Assert.Single(Search("port", asOf: 2).Entities);
        Assert.Single(Search("port").Entities);
    }

    [Fact]
    public void Search_AsOfBeforeAVisibilityChange_UsesTheOldVisibility()
    {
        _seed.Session(_campaign.Id, 1);
        var s2 = _seed.Session(_campaign.Id, 2);
        var place = _seed.Entity(_campaign.Id, "location", "Hidden Cove", visibility: V.Author);
        _db.Batch(_campaign.Id, r => r.Update("entity", place.Id, new Dictionary<string, object?> { ["visibility"] = V.Party }, "upsert"),
            sessionId: s2.EntityId);

        Assert.Empty(Search("cove", "party", asOf: 1).Entities);
        Assert.Single(Search("cove", "party").Entities);
    }

    [Theory]
    [InlineData("!!! ???", "at least one word")]
    [InlineData("*", "at least one word")]
    public void Search_QueryWithNoWords_IsRefused(string query, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => Search(query));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_QueryOverTheCaps_IsRefused()
    {
        var tooLong = Assert.Throws<DndInputException>(() => Search(new string('a', 501)));
        var tooMany = Assert.Throws<DndInputException>(() => Search(string.Join(' ', Enumerable.Range(1, 33).Select(i => "w" + i))));

        Assert.Contains("at most 500 characters", tooLong.Message, StringComparison.Ordinal);
        Assert.Contains("at most 32 key words", tooMany.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_UnknownKindOrStatus_IsRefusedListingEachProblem()
    {
        var ex = Assert.Throws<DndInputException>(() => Search(null, kinds: ["monster", "Character"], statuses: ["vanished"]));

        Assert.Contains("kinds item 1 (\"monster\") is not a kind", ex.Message, StringComparison.Ordinal);
        Assert.Contains("statuses item 1 (\"vanished\")", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("kinds item 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_KindFilterSpeltLoosely_MatchesTheKind()
    {
        _seed.Entity(_campaign.Id, "location", "Quay");
        _seed.Entity(_campaign.Id, "character", "Quay Warden");

        var result = Search("quay", kinds: ["LOCATION"]);

        Assert.Equal(["location"], result.Entities.Select(e => e.Kind).Distinct());
    }

    [Fact]
    public void Search_UnknownCharacterPerspective_IsRefusedWithoutNamingHiddenEntities()
    {
        _seed.Entity(_campaign.Id, "character", "Keras", visibility: V.Author);

        var ex = Assert.Throws<DndInputException>(() => Search("x", "character:kera"));

        Assert.Contains("no character", ex.Message, StringComparison.Ordinal);
        LeakAssert.CleanMessage(ex.Message, "character:kera", ["Keras", "keras"], "unknown perspective");
    }

    [Fact]
    public void Search_SnippetForAPlayerView_QuotesOnlyVisibleFields()
    {
        _seed.Entity(_campaign.Id, "location", "Lantern Row", summary: "A street of lamps.", body: "The lamplighters meet at dusk.",
            secret: "The lamplighters are the Guild's spies.");

        var party = Search("lamplighters", "party");
        var author = Search("spies");

        Assert.Equal("body", party.Entities[0].SnippetFrom);
        Assert.Contains("lamplighters meet", party.Entities[0].Snippet!, StringComparison.Ordinal);
        Assert.Equal("secret", author.Entities[0].SnippetFrom);
        LeakAssert.Clean(party, ["spies", "Guild"], "snippet");
    }

    [Fact]
    public void Search_DeletedEntity_IsNeverFound()
    {
        _seed.Entity(_campaign.Id, "location", "Sunken Quay", deletedAt: "2026-09-01T12:00:00.000Z");

        Assert.Empty(Search("sunken").Entities);
        Assert.DoesNotContain(Search(null).Entities, e => e.DisplayName == "Sunken Quay");
    }

    /// <summary>
    /// A party alias is the party's, not the world's (review L11, fix FQ3): the indexed aliases column holds it for every
    /// view, but the public and a character outside the party do not see it, so a hit only it explains is dropped (FTS
    /// found The Masked Duke for the public by "Orsino", under <c>character:duke-orsino</c>). Members, the party and the
    /// table still find him by it, and every view finds him by what it does see.
    /// </summary>
    [Theory]
    [InlineData("public", false)]
    [InlineData("character:old-tom", false)]
    [InlineData("party", true)]
    [InlineData("table", true)]
    [InlineData("character:aria", true)]
    public void Search_AWordOnlyInAPartyAlias_FindsTheEntityOnlyForViewsThatSeePartyAliases(string perspective, bool found)
    {
        var duke = _seed.Entity(_campaign.Id, "character", "The Masked Duke", slug: "duke-orsino", subtype: "npc", visibility: V.Public,
            summary: "Hosts the masquerade.");
        _seed.Alias(duke.Id, "Duke Orsino", V.Party);
        var aria = _seed.Entity(_campaign.Id, "character", "Aria", subtype: "pc");
        _seed.MemberOf(_campaign.Id, aria.Id, _campaign.Party!.Id);
        _seed.Entity(_campaign.Id, "character", "Old Tom", subtype: "npc", visibility: V.Public);

        var byAlias = Search("Orsino", perspective);
        var bySummary = Search("masquerade", perspective);

        Assert.Equal(found, byAlias.Entities.Any(e => e.DisplayName == "The Masked Duke"));
        Assert.Single(bySummary.Entities, e => e.DisplayName == "The Masked Duke");
        if (!found)
        {
            Assert.Empty(byAlias.Entities);
            LeakAssert.Clean(bySummary, ["Orsino"], $"a party alias for {perspective}");
        }
    }

    /// <summary>
    /// A view that does not see an entity's party alias still finds it by its own text exactly as FTS matches that text
    /// (fix FQ3): the public's search for "run" or "connect" finds The Masked Duke, whose summary says "running" and
    /// "connections", as it finds The Plain Baron with the same summary and no alias. Rechecked with the C# word match,
    /// which forgives fewer endings than FTS's porter stemmer, the Duke alone was dropped, and the difference hinted at his
    /// hidden alias. A word of an alias the view does see counts with the text ("Revels run" for the public).
    /// </summary>
    [Theory]
    [InlineData("run", "public", new[] { "The Masked Duke", "The Plain Baron" })]
    [InlineData("connect", "public", new[] { "The Masked Duke", "The Plain Baron" })]
    [InlineData("connect*", "character:old-tom", new[] { "The Masked Duke", "The Plain Baron" })]
    [InlineData("running guild", "public", new[] { "The Masked Duke", "The Plain Baron" })]
    [InlineData("Revels run", "public", new[] { "The Masked Duke" })]
    [InlineData("Revels run", "party", new[] { "The Masked Duke" })]
    public void Search_AStemmedWordOfAnEntityWithAPartyAliasTheViewDoesNotSee_FindsItAsFtsDoes(string query, string perspective, string[] found)
    {
        var duke = _seed.Entity(_campaign.Id, "character", "The Masked Duke", slug: "duke-orsino", subtype: "npc", visibility: V.Public,
            summary: "He is running the masquerade and connections to every guild.");
        _seed.Alias(duke.Id, "Duke Orsino", V.Party);
        _seed.Alias(duke.Id, "Lord of Revels", V.Public);
        _seed.Entity(_campaign.Id, "character", "The Plain Baron", subtype: "npc", visibility: V.Public,
            summary: "He is running the harbour and connections to every guild.");
        _seed.Entity(_campaign.Id, "character", "Old Tom", subtype: "npc", visibility: V.Public);

        var result = Search(query, perspective);

        Assert.False(result.PartialMatch);
        Assert.Equal(found, result.Entities.Select(e => e.DisplayName).Order(StringComparer.Ordinal));
        if (perspective != "party")
        {
            LeakAssert.Clean(result, ["Orsino"], $"a party alias for {perspective}");
        }
    }

    /// <summary>
    /// A disguised entity is found by every name its view uses (review U01, fix FQ3): the party knows Keras as "the ancient
    /// sorcerer king" and calls him by his party alias "the old king" too, so a search for "old king" finds him, by name,
    /// under the known_as and <c>e:&lt;n&gt;</c> (it found him only through "king", as a partial match); his true name and
    /// his own text still find nothing.
    /// </summary>
    [Fact]
    public void Search_APartyAliasOfADisguisedEntity_FindsItUnderItsKnownName()
    {
        var keras = _seed.Entity(_campaign.Id, "character", "Keras", subtype: "npc", visibility: V.Party, summary: "Keras rules the ruined throne.");
        _seed.Alias(keras.Id, "the old king", V.Party);
        _seed.Alias(keras.Id, "the sorcerer king", V.Party);
        _seed.Knowledge(_campaign.Id, "party", state: "met", entityId: keras.Id, knownAs: "the ancient sorcerer king");

        var byAlias = Search("old king", "party");
        var byName = Search("Keras", "party");
        var bySummary = Search("throne", "party");

        Assert.False(byAlias.PartialMatch);
        var hit = Assert.Single(byAlias.Entities);
        Assert.Equal(("e:" + keras.Seq, "the ancient sorcerer king", SearchTiers.Exact), (hit.Ref, hit.DisplayName, hit.MatchTier));
        Assert.Empty(byName.Entities);
        Assert.Empty(bySummary.Entities);
        foreach (var result in new object[] { byAlias, byName, bySummary })
        {
            LeakAssert.Clean(result, ["Keras", "throne", "character:keras"], "a party alias of a disguised entity");
        }
    }

    /// <summary>
    /// The author's listing of beats marks the ones that can happen next (review C03): session_prep reads it for "which are
    /// reachable now", and it said only "pending". The status filter still compares the status alone; no other view gets
    /// the mark (the web is the author's prep).
    /// </summary>
    [Fact]
    public void Search_BeatsForTheAuthor_MarkTheOnesReachableNow()
    {
        var map = _seed.Entity(_campaign.Id, "beat", "Find the map", visibility: V.Party);
        var key = _seed.Entity(_campaign.Id, "beat", "Steal the key", status: CampaignValues.Statuses.BeatMet, visibility: V.Party);
        var vault = _seed.Entity(_campaign.Id, "beat", "Storm the vault", visibility: V.Party);
        _seed.BeatEdge(_campaign.Id, map.Id, vault.Id);
        _seed.BeatEdge(_campaign.Id, key.Id, vault.Id);

        var author = Search(null, kinds: ["beat"]);
        var byWords = Search("map");
        var pending = Search(null, kinds: ["beat"], statuses: [CampaignValues.Statuses.BeatPending]);
        var party = Search(null, "party", kinds: ["beat"]);

        Assert.Equal(
            [$"{map.Handle}: pending · reachable now", $"{key.Handle}: met", $"{vault.Handle}: pending"],
            author.Entities.Select(e => $"{e.Ref}: {e.Status}").Order(StringComparer.Ordinal));
        Assert.Equal("pending · reachable now", Assert.Single(byWords.Entities).Status);
        Assert.Equal([map.Handle, vault.Handle], pending.Entities.Select(e => e.Ref).Order());
        Assert.Equal(3, party.Entities.Count);
        Assert.All(party.Entities, e => Assert.DoesNotContain("reachable", e.Status ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Search_ProposedInventionAsParty_IsNotShown()
    {
        _seed.Entity(_campaign.Id, "location", "Tern Rock", canonStatus: "proposed", code: "F1");

        Assert.Empty(Search("tern", "party").Entities);
        Assert.Single(Search("tern").Entities);
    }

    [Fact]
    public void Search_IncludeFactsFalse_ReturnsEntitiesOnly()
    {
        _seed.Entity(_campaign.Id, "location", "Harbor");
        _seed.Fact(_campaign.Id, "The harbor is closed.");

        var with = new CampaignSearch(_db.Database).Search(Row, new SearchRequest("harbor"));
        var without = new CampaignSearch(_db.Database).Search(Row, new SearchRequest("harbor", IncludeFacts: false));

        Assert.Equal((1, 1, 2), (with.Entities.Count, with.Facts.Count, with.Total));
        Assert.Equal((1, 0, 1), (without.Entities.Count, without.Facts.Count, without.Total));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Search_LastPageExactlyFull_HasNoNextCursor(int limit)
    {
        for (var i = 1; i <= 4; i++)
        {
            _seed.Entity(_campaign.Id, "location", $"Harbor {i}");
        }

        var page = Search("harbor", limit: limit);
        while (page.NextCursor is { } cursor)
        {
            page = Search("harbor", limit: limit, cursor: cursor);
        }

        Assert.Equal(limit, page.Entities.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public void Search_CursorFromADifferentAsOfSession_IsRefused()
    {
        _seed.Session(_campaign.Id, 1);
        for (var i = 1; i <= 3; i++)
        {
            _seed.Entity(_campaign.Id, "location", $"Harbor {i}");
        }

        var now = Search("harbor", "party", limit: 1);
        var then = Search("harbor", "party", limit: 1, asOf: 1);

        Assert.Throws<DndInputException>(() => Search("harbor", "party", limit: 1, cursor: now.NextCursor, asOf: 1));
        Assert.Throws<DndInputException>(() => Search("harbor", "party", limit: 1, cursor: then.NextCursor));
        Assert.Single(Search("harbor", "party", limit: 1, cursor: then.NextCursor, asOf: 1).Entities);
    }

    /// <summary>
    /// A cursor belongs to one campaign's list: pasted into the same call for another campaign it is refused, by every
    /// paged reader, instead of silently skipping into the other campaign's list.
    /// </summary>
    [Fact]
    public void Cursor_FromAnotherCampaign_IsRefusedByEveryPagedReader()
    {
        var other = _seed.Campaign(name: "Other", role: CampaignValues.Roles.Dm);
        foreach (var campaign in new[] { _campaign, other })
        {
            for (var i = 1; i <= 3; i++)
            {
                _seed.Entity(campaign.Id, "location", $"Harbor {i}");
                _seed.Session(campaign.Id, i);
                var fact = _seed.Fact(campaign.Id, $"Harbor fact {i}.", visibility: V.Party);
                _seed.Knowledge(campaign.Id, "party", factId: fact.Id);
            }

            _db.Batch(campaign.Id, r => r.Update("campaign", campaign.Id, new Dictionary<string, object?> { ["summary_md"] = "a" }, "update"));
            _db.Batch(campaign.Id, r => r.Update("campaign", campaign.Id, new Dictionary<string, object?> { ["summary_md"] = "b" }, "update"));
        }

        var mine = Row;
        var theirs = _seed.LoadCampaign(other.Id);
        var party = Perspective.Parse("party");
        var search = new CampaignSearch(_db.Database).Search(mine, new SearchRequest("harbor", Perspective: party, Limit: 1)).NextCursor;
        var sessions = new SessionReader(_db.Database).List(mine, limit: 1, perspective: party).NextCursor;
        var known = new KnowledgeLedger(_db.Database).KnownTo(mine, party, limit: 1).NextCursor;
        var history = new HistoryReader(_db.Database).Since(mine, limit: 1).NextCursor;

        Assert.All(new[] { search, sessions, known, history }, Assert.NotNull);
        Assert.Throws<DndInputException>(() => new CampaignSearch(_db.Database).Search(theirs, new SearchRequest("harbor", Perspective: party, Limit: 1, Cursor: search)));
        Assert.Throws<DndInputException>(() => new SessionReader(_db.Database).List(theirs, limit: 1, cursor: sessions, perspective: party));
        Assert.Throws<DndInputException>(() => new KnowledgeLedger(_db.Database).KnownTo(theirs, party, limit: 1, cursor: known));
        Assert.Throws<DndInputException>(() => new HistoryReader(_db.Database).Since(theirs, limit: 1, cursor: history));
        Assert.Single(new CampaignSearch(_db.Database).Search(mine, new SearchRequest("harbor", Perspective: party, Limit: 1, Cursor: search)).Entities);
    }
}
