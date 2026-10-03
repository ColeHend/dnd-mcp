using System.Text.RegularExpressions;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>campaign_search</c> run from any non-author perspective returns nothing that view cannot see, by any
/// query or filter (the leak rule, serialized whole and checked against every forbidden string), finds what the view does
/// know under the name it knows (a disguised entity by its known_as, as <c>e:&lt;n&gt;</c>), starts with the perspective
/// banner, and for the author prints the author-only columns.
///
/// <para>
/// Why it fails silently: a leak shows up only for a particular query and view ("Axiom" as the party, "sorcerer" as
/// Belmakor), and every other search stays correct; so the sweep crosses every non-author perspective with the queries
/// that would reach each secret by name, alias, secret text, cross-link and snippet.
/// </para>
/// </summary>
public sealed class CampaignSearchToolTests : IClassFixture<BelmakorServer>
{
    private const string Accepted =
        "campaign_search accepts: query (string, optional), kinds (array of string, optional), statuses (array of string, optional), " +
        "tags (array of string, optional), perspective (string, optional), include_facts (boolean, optional), as_of_session (integer, optional), " +
        "limit (integer, optional), cursor (string, optional), campaign (string, optional).";

    // Words that the secrets, author aliases, secret text, cross-links and restricted facts would be found by.
    private static readonly string[] LeakQueries =
    [
        "Keras", "Axiom", "Cage", "Axiom Cage", "Third Silence", "Baal", "Cole", "imported", "old king", "thing he wants", "the thing",
        "sorcerer", "sorcer*", "king", "reclaim", "blighted", "legend", "ambition", "Belmakor", "name", "PC", "fetched", "tier", "one-piece",
    ];

    // Author-only columns and wording of a search result.
    private static readonly string[] AuthorOnlyWording = ["known by", "truth ", "canon ", "(secret)", "(hidden aliases)", "hidden aliases"];

    private readonly BelmakorServer _f;

    public CampaignSearchToolTests(BelmakorServer fixture)
    {
        _f = fixture;
    }

    public static TheoryData<string, string> PerspectivesAndQueries()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in BelmakorServer.NonAuthorPerspectives)
        {
            foreach (var query in LeakQueries)
            {
                data.Add(perspective, query);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PerspectivesAndQueries))]
    public async Task Search_NonAuthorPerspective_NeverReturnsWhatThatViewCannotSee(string perspective, string query)
    {
        var text = await _f.Call("campaign_search", $$"""{"query":"{{query}}","perspective":"{{perspective}}","limit":50}""");

        // The title echoes the caller's own query; everything after it is the search's.
        var body = text[(text.IndexOf('\n') + 1)..];
        BelmakorServer.AssertClean(body, BelmakorServer.ForbiddenFor(perspective), $"search \"{query}\" as {perspective}");
        BelmakorServer.AssertClean(body, AuthorOnlyWording, $"search \"{query}\" as {perspective}");
        Assert.StartsWith("_Perspective: " + perspective, body, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> PerspectivesAndListings()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in BelmakorServer.NonAuthorPerspectives)
        {
            data.Add(perspective, """{"kinds":["character","item","thread","secret","rule","arc","session","faction"]}""");
            data.Add(perspective, """{"statuses":["open","unknown","alive","hidden"]}""");
            data.Add(perspective, """{"kinds":["secret"]}""");
            data.Add(perspective, """{}""");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PerspectivesAndListings))]
    public async Task List_NonAuthorPerspective_NeverListsWhatThatViewCannotSee(string perspective, string filters)
    {
        var arguments = filters.Replace("{", $$"""{"perspective":"{{perspective}}","limit":50,""", StringComparison.Ordinal).Replace(",}", "}", StringComparison.Ordinal);

        var text = await _f.Call("campaign_search", arguments);

        BelmakorServer.AssertClean(text, BelmakorServer.ForbiddenFor(perspective), $"listing {filters} as {perspective}");
        BelmakorServer.AssertClean(text, AuthorOnlyWording, $"listing {filters} as {perspective}");
    }

    public static TheoryData<string, string> VeilPerspectivesAndQueries()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in BelmakorServer.VeilPerspectives)
        {
            foreach (var query in new[]
                     {
                         "Mirelle", "Duskbane", "Mirelle Duskbane", "mirelle-duskbane", "assassin", "Nightglass", "Ysolde", "queen", "veiled",
                         "veiled woman", "woman", "rose", "envoy", "poisoned", "killed", "secretly", "serves", "school", "turn",
                     })
            {
                data.Add(perspective, query);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VeilPerspectivesAndQueries))]
    public async Task Search_VeilNonAuthorPerspective_NeverSpellsTheTrueName(string perspective, string query)
    {
        var text = await _f.Call("campaign_search", $$"""{"query":"{{query}}","perspective":"{{perspective}}","limit":50,"campaign":"veil"}""");

        var body = text[(text.IndexOf('\n') + 1)..];
        BelmakorServer.AssertClean(body, BelmakorServer.VeilForbidden, $"veil search \"{query}\" as {perspective}");
        BelmakorServer.AssertClean(body, AuthorOnlyWording, $"veil search \"{query}\" as {perspective}");
    }

    [Theory]
    [MemberData(nameof(BelmakorServer.VeilPerspectiveData), MemberType = typeof(BelmakorServer))]
    public async Task List_VeilNonAuthorPerspective_NeverSpellsTheTrueName(string perspective)
    {
        var text = await _f.Call("campaign_search", $$"""{"perspective":"{{perspective}}","limit":50,"campaign":"veil"}""");

        BelmakorServer.AssertClean(text, BelmakorServer.VeilForbidden, $"veil listing as {perspective}");
    }

    [Theory]
    [InlineData("party")]
    [InlineData("character:aria")]
    [InlineData("table")]
    [InlineData("dm")]
    public async Task Search_VeilByTheNameThePartyUses_FindsHerAsASeqRefAndTheFactInItsOwnPhrasing(string perspective)
    {
        var woman = await _f.Call("campaign_search", $$"""{"query":"veiled woman","perspective":"{{perspective}}","campaign":"veil"}""");
        var envoy = await _f.Call("campaign_search", $$"""{"query":"envoy","perspective":"{{perspective}}","campaign":"veil"}""");

        Assert.Matches(new Regex(@"\n1\. \*\*the veiled woman\*\* · character · `e:\d+`\n"), woman);
        Assert.Contains($"`{BelmakorServer.VeilEnvoyFact}`: Someone killed the envoy.\n", envoy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_VeilAuthor_FindsHerByHerNameAndTheFactByItsStatement()
    {
        var text = await _f.Call("campaign_search", """{"query":"Duskbane","campaign":"veil"}""");

        Assert.Contains("**Mirelle Duskbane** · character · alive · `character:mirelle-duskbane`", text, StringComparison.Ordinal);
        Assert.Contains($"`{BelmakorServer.VeilEnvoyFact}`: Mirelle Duskbane poisoned the envoy.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_PartyForTheAxiomCage_FindsNothingAndSaysNothingIsHidden()
    {
        var text = await _f.Call("campaign_search", """{"query":"Axiom Cage","perspective":"party"}""");

        Assert.Equal(
            "# Search belmakor: \"Axiom Cage\"\n_Perspective: party. Names are the ones this view knows; author-only text is withheld._\n\n" +
            "Nothing matches. Try fewer or different words, a prefix such as \"sorcer*\", or leave query out to list by kinds.\n",
            text);
    }

    [Fact]
    public async Task Search_PartyByItsOwnName_FindsTheDisguisedItemAsASeqRefUnderThatName()
    {
        var text = await _f.Call("campaign_search", """{"query":"thing he wants","perspective":"party","include_facts":false}""");

        Assert.Matches(new Regex(@"\n1\. \*\*the thing he wants\*\* · item · `e:\d+`\n"), text);
        Assert.DoesNotContain("thing-he-wants", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The thing the old king wants", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_IncludeFactsFalse_ReturnsEntitiesOnly()
    {
        var with = await _f.Call("campaign_search", """{"query":"old king"}""");
        var without = await _f.Call("campaign_search", """{"query":"old king","include_facts":false}""");

        Assert.Contains("\n## Facts\n", with, StringComparison.Ordinal);
        Assert.DoesNotContain("## Facts", without, StringComparison.Ordinal);
        Assert.Contains("\n## Entities\n", without, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_Author_FindsTheItemByItsAuthorAliasAndPrintsTheAuthorColumns()
    {
        var text = await _f.Call("campaign_search", """{"query":"Axiom"}""");

        Assert.DoesNotContain("_Perspective:", text, StringComparison.Ordinal);
        Assert.Contains("**The thing the old king wants** · item · `item:thing-he-wants`", text, StringComparison.Ordinal);
        Assert.Contains("`f:1`: The thing the old king wants fetched is the Axiom Cage.\n   truth true · canon canon · known by no one yet\n", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_Dm_FindsTheFactItKnowsButNotTheEntityByItsAuthorAlias()
    {
        // Belmakor row 27 (pinned by the contract): the dm knows f:2 but finds the old king only by names it may see.
        var text = await _f.Call("campaign_search", """{"query":"Keras","perspective":"dm"}""");

        Assert.Contains("`f:2`: The old king's name is Keras.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Entities", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_NoEntryHasEveryWord_ReturnsAnyWordMatchesAndSaysSo()
    {
        var text = await _f.Call("campaign_search", """{"query":"errand dragon","perspective":"party"}""");

        Assert.Contains("\n_Nothing has every word, so these match any of them._\n", text, StringComparison.Ordinal);
        Assert.Contains("The old king's errand", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ByKindAsParty_ListsEveryVisibleCharacterWithPerspectiveSafeRefs()
    {
        var text = await _f.Call("campaign_search", """{"kinds":["character"],"perspective":"party"}""");

        Assert.StartsWith("# belmakor: listing (kinds character)\n_Perspective: party.", text, StringComparison.Ordinal);
        Assert.Contains("**Belmakor Silverwind** · character · alive · `character:belmakor`", text, StringComparison.Ordinal);
        Assert.Contains("**The Old King** · character · unknown · `character:old-king`", text, StringComparison.Ordinal);
        // "vars" spells nothing the party sees for Vars Nocturne (his name, no alias): the seq ref.
        Assert.Matches(new Regex(@"\*\*Vars Nocturne\*\* · character · alive · `e:\d+`"), text);
        // The footer reads the same view (FH6, L10): campaign_get without a perspective is the author's, and followed on a
        // disguised hit it would print the true name and the secret text.
        Assert.EndsWith("8 results.\ncampaign_get {\"campaign\": \"belmakor\", \"refs\": [...], \"perspective\": \"party\"} reads any of these in full.\n",
            text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review LR02: the footer of a non-author read as of a session reads the same view at the same point in time. Without
    /// as_of_session the call read today's view, which holds what the view learned after that session: the anachronism a
    /// read as of a session exists to prevent. Sent as printed (with a ref in place of the dots), it reads as of the session.
    /// </summary>
    [Theory]
    [InlineData("", "\"perspective\": \"party\"}")]
    [InlineData(", \"as_of_session\": 3", "\"perspective\": \"party\", \"as_of_session\": 3}")]
    public async Task List_NonAuthorView_TheFooterReadsTheSameViewAtTheSamePointInTime(string asOf, string ending)
    {
        var text = await _f.Call("campaign_search", $$"""{"kinds":["character"],"perspective":"party"{{asOf}}}""");

        var footer = $"campaign_get {{\"campaign\": \"belmakor\", \"refs\": [...], {ending} reads any of these in full.\n";
        Assert.EndsWith("\n" + footer, text, StringComparison.Ordinal);
        var read = await _f.Call("campaign_get", footer[13..footer.IndexOf(" reads", StringComparison.Ordinal)]
            .Replace("[...]", "[\"character:belmakor\"]", StringComparison.Ordinal));
        Assert.StartsWith("# Belmakor Silverwind (`character:belmakor`)\n_Perspective: party.", read, StringComparison.Ordinal);
        Assert.Equal(asOf.Length > 0, read.Contains("_As of the end of session 3._", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_Paged_TheCursorGivesTheNextPageAndThePagesDoNotOverlap()
    {
        var first = await _f.Call("campaign_search", """{"kinds":["character"],"limit":5}""");
        var cursor = Regex.Match(first, "cursor \"([^\"]+)\"").Groups[1].Value;
        var second = await _f.Call("campaign_search", $$"""{"kinds":["character"],"limit":5,"cursor":"{{cursor}}"}""");

        Assert.Contains("This page shows 5 of 8 results; next page: the same call with cursor \"", first, StringComparison.Ordinal);
        Assert.Contains("\nThe last page: 3 of 8 results.\n", second, StringComparison.Ordinal);
        var firstRefs = Regex.Matches(first, "`(character:[a-z-]+)`").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.All(Regex.Matches(second, "`(character:[a-z-]+)`").Select(m => m.Groups[1].Value), r => Assert.DoesNotContain(r, firstRefs));
    }

    [Fact]
    public async Task Search_CursorFromAnotherPerspective_IsRefused()
    {
        var first = await _f.Call("campaign_search", """{"kinds":["character"],"limit":5}""");
        var cursor = Regex.Match(first, "cursor \"([^\"]+)\"").Groups[1].Value;

        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_search", $$"""{"kinds":["character"],"limit":5,"cursor":"{{cursor}}","perspective":"party"}"""));

        Assert.StartsWith("An error occurred invoking 'campaign_search': ", text, StringComparison.Ordinal);
        Assert.Contains("cursor", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"limit":0}""", "limit is 0; give 1 to 50 (default 15), and page on with the cursor a result returns.")]
    [InlineData("""{"kinds":["character","dragon","npc"]}""",
        "Invalid search filters (2 problems):\n- kinds item 2 (\"dragon\") is not a kind; ")]
    [InlineData("""{"query":"!!!"}""", "query needs at least one word to search for (letters or digits), e.g. \"old king\" or a prefix like \"seal*\". ")]
    [InlineData("""{"perspective":"villains"}""", "perspective \"villains\" is not one of ")]
    [InlineData("""{"perspective":"character:nobody"}""", "perspective \"character:nobody\": no character nobody in this campaign.")]
    [InlineData("""{"as_of_session":-1}""", "as_of_session is -1; give a session number from 0 to ")]
    [InlineData("""{"campaign":"nope"}""", "No campaign \"nope\". Campaigns: belmakor (player, 2014), one-piece (dm, 2024), veil (player, 2024). Pass one of those slugs.")]
    public async Task Search_BadArgument_IsRefusedWithWhatIsAccepted(string arguments, string message)
    {
        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_search", arguments));

        Assert.StartsWith("An error occurred invoking 'campaign_search': " + message, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_QueryOverTheCap_IsRefusedShortly()
    {
        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_search", $$"""{"query":"{{new string('a', 100_000)}}"}"""));

        Assert.StartsWith("An error occurred invoking 'campaign_search': query is 100,000 characters long; search with at most 500 characters", text,
            StringComparison.Ordinal);
        Assert.True(text.Length < 600, $"error is {text.Length} characters");
    }

    [Fact]
    public async Task CallTool_WrongType_GuardNamesTheArgumentAndListsTheArguments()
    {
        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_search", """{"kinds":"character"}"""));

        Assert.Equal(
            "An error occurred invoking 'campaign_search': Invalid arguments: argument 'kinds' should be array or null but was the string \"character\". " + Accepted,
            text);
    }

    [Fact]
    public async Task CallTool_NullForEveryOptionalArgument_MeansTheDefault()
    {
        var text = await _f.Call("campaign_search",
            """{"query":null,"kinds":null,"statuses":null,"tags":null,"perspective":null,"include_facts":null,"as_of_session":null,"limit":null,"cursor":null,"campaign":null}""");
        var empty = await _f.Call("campaign_search", """{"kinds":[],"statuses":[],"tags":[],"query":" "}""");

        Assert.StartsWith("# belmakor: listing\n\n## Entities\n", text, StringComparison.Ordinal);
        Assert.Contains("This page shows 15 of ", text, StringComparison.Ordinal);
        Assert.Equal(text, empty);
    }

    [Fact]
    public async Task Search_TypicalQuery_IsUnderTheTypicalBudget()
    {
        var text = await _f.Call("campaign_search", """{"query":"old king"}""");

        Assert.True(text.Length < 8_000, $"search result is {text.Length} characters");
    }

    [Fact]
    public async Task Search_MatchInABody_ShowsTheSnippetAndTheFieldItCameFrom()
    {
        var text = await _f.Call("campaign_search", """{"query":"veiled woman","perspective":"party","campaign":"veil"}""");

        Assert.Contains("**The black rose** · session · `session:1`\n   A veiled woman watched us from the market. _(body)_\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_DmInTheDmCampaign_IsTheAuthorsViewWithNoBanner()
    {
        // In a DM campaign the dm is the author (contract §3.2): it finds the author-only item by its author alias.
        var text = await _f.Call("campaign_search", """{"query":"Third Silence","perspective":"dm","campaign":"one-piece"}""");

        Assert.StartsWith("# Search one-piece: \"Third Silence\"\n\n## Entities\n1. **The Axiom Cage** · item · `item:axiom-cage`\n", text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Invariant: no non-author search or listing of the golden campaign (author-only clock, objective, location, secret and
/// its gated fact, proposed invention, live log) returns any of its author-only text, whatever words it is asked for.
///
/// <para>
/// Why it fails silently: those rows are reachable only through their own words, and the Belmakor sweep has none of them.
/// </para>
/// </summary>
public sealed class CampaignSearchToolGoldenTests : IClassFixture<GoldenCampaignServer>
{
    private static readonly string[] Queries =
    [
        "cult", "awakens", "strikes", "floods", "betray", "hidden cove", "cove", "heir", "keeper", "blood", "vell", "captain", "sailed", "stealth", "tide",
        "drowned", "salt",
    ];

    private readonly GoldenCampaignServer _g;

    public CampaignSearchToolGoldenTests(GoldenCampaignServer fixture)
    {
        _g = fixture;
    }

    public static TheoryData<string, string> PerspectivesAndQueries()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in GoldenCampaignServer.NonAuthorPerspectives)
        {
            foreach (var query in Queries)
            {
                data.Add(perspective, query);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PerspectivesAndQueries))]
    public async Task Search_NonAuthorPerspective_ReturnsNoAuthorOnlyText(string perspective, string query)
    {
        var text = await _g.Call("campaign_search", $$"""{"query":"{{query}}","perspective":"{{perspective}}","limit":50}""");

        var body = text[(text.IndexOf('\n') + 1)..];
        BelmakorServer.AssertClean(body, GoldenCampaignServer.AuthorOnly, $"golden search \"{query}\" as {perspective}");
    }

    [Theory]
    [MemberData(nameof(GoldenCampaignServer.NonAuthorPerspectiveData), MemberType = typeof(GoldenCampaignServer))]
    public async Task List_NonAuthorPerspective_ListsNoAuthorOnlyEntry(string perspective)
    {
        foreach (var filters in new[] { "", ""","kinds":["clock","secret","quest","location","character"]""", ""","statuses":["running","open","hidden","partial"]""" })
        {
            var text = await _g.Call("campaign_search", $$"""{"perspective":"{{perspective}}","limit":50{{filters}}}""");

            BelmakorServer.AssertClean(text, GoldenCampaignServer.AuthorOnly, $"golden listing {filters} as {perspective}");
        }
    }
}

/// <summary>
/// Invariant: <c>campaign_search</c> reads as of a session (entries created later are absent, statuses as they stood) and
/// every result, the largest page included, stays under the output cap.
///
/// <para>
/// Why it fails silently: an as_of that the tool dropped on the way to the reader would return today's rows with an "as
/// of" line above them, which reads right; and the size of a page grows with the data, so only a deliberately large
/// campaign shows a missing cap.
/// </para>
/// </summary>
public sealed class CampaignSearchToolAsOfTests : IAsyncLifetime
{
    private CampaignTestServer _s = null!;
    private CampaignRow _c = null!;

    public async Task InitializeAsync()
    {
        _s = await CampaignTestServer.StartAsync();
        _c = await _s.Create("Sky");
    }

    public async Task DisposeAsync() => await _s.DisposeAsync();

    [Fact]
    public async Task Search_AsOfAnEarlierSession_LeavesOutWhatCameLaterAndSaysTheTextIsTodays()
    {
        var sessions = new SessionWriter(_s.Database);
        sessions.RecordPast(_c, 1, "One");
        _s.Apply(_c, WriteContext.For(1), new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", Status = "alive", Visibility = "party" });
        sessions.RecordPast(_c, 2, "Two");
        _s.Apply(_c, WriteContext.For(2),
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Maiden", Visibility = "party" },
            new CampaignOpSpec { Op = "status", Ref = "character:iron-guts", Status = "dead" });

        var then = await _s.Call("campaign_search", """{"query":"iron","as_of_session":1,"perspective":"party"}""");
        var now = await _s.Call("campaign_search", """{"query":"iron","perspective":"party"}""");

        Assert.Contains("_As of the end of session 1; words are matched against today's text._\n", then, StringComparison.Ordinal);
        Assert.Contains("**Iron Guts** · character · alive · `character:iron-guts`", then, StringComparison.Ordinal);
        Assert.DoesNotContain("Iron Maiden", then, StringComparison.Ordinal);
        Assert.Contains("**Iron Guts** · character · dead · `character:iron-guts`", now, StringComparison.Ordinal);
        Assert.Contains("Iron Maiden", now, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_LargestPage_StaysUnderTheCap()
    {
        for (var batch = 0; batch < 2; batch++)
        {
            var ops = Enumerable.Range(1, 30).Select(i => new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Visibility = "party",
                Name = $"Stormcaller of the endless northern reaches and the drowned western isles, number {batch:D1}{i:D2}",
                Summary = "Stormcaller " + string.Join(' ', Enumerable.Repeat("thunder and rain over the grey water", 25)),
                BodyMd = string.Join('\n', Enumerable.Repeat("Stormcaller lore: the sea remembers every name spoken over it.", 200)),
            }).ToArray();
            _s.Apply(_c, WriteContext.Default, ops);
        }

        var text = await _s.Call("campaign_search", """{"query":"stormcaller","limit":50}""");

        Assert.True(text.Length <= 24_000, $"search result is {text.Length} characters");
        Assert.StartsWith("# Search sky: \"stormcaller\"\n\n## Entities\n1. **Stormcaller", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_LargestAuthorPage_IsCutAtTheCapAndSaysSo()
    {
        // Every fact is known by 25 characters with long names, so each fact line carries a long author-only "known by" list.
        var names = Enumerable.Range(1, 25).Select(i => $"Watcher {i:D2} " + string.Join(' ', Enumerable.Repeat("of the northern light", 8))).ToList();
        _s.Apply(_c, WriteContext.Default, names.Select(n => new CampaignOpSpec { Op = "upsert", Kind = "character", Name = n }).ToArray());
        var knowers = names.Select(n => new KnowerSpec { Who = "character:" + CampaignSlugs.From(n, "character") }).ToArray();
        foreach (var chunk in Enumerable.Range(1, 50).Chunk(25))
        {
            _s.Apply(_c, WriteContext.Default, chunk.Select(i => new CampaignOpSpec
            {
                Op = "fact", Statement = $"Omen {i:D2}: " + string.Join(' ', Enumerable.Repeat("the light burns low", 30)), KnownBy = knowers,
            }).ToArray());
        }

        var text = await _s.Call("campaign_search", """{"query":"omen","limit":50}""");

        Assert.True(text.Length <= 24_000, $"search result is {text.Length} characters");
        Assert.EndsWith("_Output cut at 24,000 characters; pass a smaller limit, or narrow with kinds, statuses or tags._", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_OnAnotherCampaign_ByItsSlug_SearchesThatOne()
    {
        var other = await _s.Create("Other");
        _s.Apply(other, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Flotsam" });

        var text = await _s.Call("campaign_search", """{"query":"flotsam","campaign":"other"}""");
        var current = await _s.Call("campaign_search", """{"query":"flotsam","campaign":"sky"}""");

        Assert.Contains("`location:flotsam`", text, StringComparison.Ordinal);
        Assert.Contains("Nothing matches.", current, StringComparison.Ordinal);
    }
}
