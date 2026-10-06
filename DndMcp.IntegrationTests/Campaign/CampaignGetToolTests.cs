using System.Text.RegularExpressions;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>campaign_get</c> from a non-author perspective shows only what that view knows, under the names it knows:
/// every handle it may type (and every one it may not) gives a result or an error with nothing hidden in it; a disguised
/// entity comes back under its known name as <c>e:&lt;n&gt;</c> with only the facts the view knows; a handle the view
/// cannot see reads exactly like one that does not exist. The author view shows secret text in a <c>&gt; [!secret]</c>
/// block, aliases with their visibility, cross-links and history.
///
/// <para>
/// Why it fails silently: a get is where everything about one entry is printed at once (relations, facts, knowledge,
/// children, sessions), so a single include that forgot the perspective leaks through a view that looks right in every
/// other part; and an error that said "hidden" instead of "not found" would confirm that a secret exists.
/// </para>
/// </summary>
public sealed partial class CampaignGetToolTests : IClassFixture<BelmakorServer>
{
    private const string Accepted =
        "campaign_get accepts: refs (array of string, required), include (array of string, optional), detail (string, optional), " +
        "perspective (string, optional), as_of_session (integer, optional), campaign (string, optional).";

    private const string AllIncludes = """["relations","facts","knowledge","children","sessions","history"]""";

    // Author-only sections and wording of a get.
    private static readonly string[] AuthorOnlyWording =
        ["[!secret]", "### Author", "## Author", "visibility ", "same as", "History", "known by", "truth ", "canon ", "(author)", "(restricted)", " via `", "note:"];

    private readonly BelmakorServer _f;

    public CampaignGetToolTests(BelmakorServer fixture)
    {
        _f = fixture;
    }

    [GeneratedRegex(@"`((?:[a-z]+:[a-z0-9-]+)|(?:e:\d+)|(?:f:\d+)|(?:session:\d+))`")]
    private static partial Regex RefRegex();

    public static TheoryData<string, string> PerspectivesAndHandles()
    {
        var data = new TheoryData<string, string>();
        // Entity and fact handles are numbered across campaigns: the sweep covers every number any campaign here uses, so the
        // other campaigns' entries (the One Piece Keras, the veil) are tried through Belmakor too.
        var handles = Enumerable.Range(1, 40).Select(i => $"e:{i}")
            .Concat(Enumerable.Range(1, 12).Select(i => $"f:{i}"))
            .Concat(["character:old-king", "item:thing-he-wants", "thread:old-kings-errand", "secret:belmakors-ambition", "rule:old-king-no-name-no-timespan",
                "one-piece/character:keras", "keras", "axiom-cage", "item:axiom-cage", "session:3", "session:last", "F1"]);
        foreach (var perspective in BelmakorServer.NonAuthorPerspectives)
        {
            foreach (var handle in handles)
            {
                data.Add(perspective, handle);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PerspectivesAndHandles))]
    public async Task Get_NonAuthorPerspective_AnyHandle_ShowsNothingThatViewCannotSee(string perspective, string handle)
    {
        var result = await _f.Server.CallToolJsonAsync("campaign_get",
            $$"""{"refs":["{{handle}}"],"include":{{AllIncludes}},"detail":"full","perspective":"{{perspective}}"}""");

        // Success or refusal, the whole text is checked; the caller's own handle is echoed in a refusal, so it is left out.
        var text = _f.Server.SingleText(result).Replace($"\"{handle}\"", "\"<handle>\"", StringComparison.Ordinal);
        BelmakorServer.AssertClean(text, BelmakorServer.ForbiddenFor(perspective), $"get {handle} as {perspective}");
        BelmakorServer.AssertClean(text, AuthorOnlyWording, $"get {handle} as {perspective}");
        if (result.IsError != true)
        {
            Assert.Equal("_Perspective: " + perspective, text.Split('\n')[1][..("_Perspective: " + perspective).Length]);
        }
    }

    [Theory]
    [MemberData(nameof(BelmakorServer.Perspectives), MemberType = typeof(BelmakorServer))]
    public async Task Get_NonAuthorPerspective_EveryEntryItCanList_FullyExpanded_ShowsNothingThatViewCannotSee(string perspective)
    {
        var listing = await _f.Call("campaign_search", $$"""{"perspective":"{{perspective}}","limit":50}""");
        var refs = RefRegex().Matches(listing).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (refs.Count == 0)
        {
            // The public knows nothing of this campaign (everything in it is party-visible or narrower).
            Assert.Equal("public", perspective);
            Assert.Contains("\nNothing matches these filters.\n", listing, StringComparison.Ordinal);
            return;
        }

        foreach (var chunk in refs.Chunk(10))
        {
            var text = await _f.Call("campaign_get",
                $$"""{"refs":[{{string.Join(",", chunk.Select(r => $"\"{r}\""))}}],"include":{{AllIncludes}},"detail":"full","perspective":"{{perspective}}"}""");

            BelmakorServer.AssertClean(text, BelmakorServer.ForbiddenFor(perspective), $"get {string.Join(", ", chunk)} as {perspective}");
            BelmakorServer.AssertClean(text, AuthorOnlyWording, $"get {string.Join(", ", chunk)} as {perspective}");
        }
    }

    public static TheoryData<string, string> VeilPerspectivesAndHandles()
    {
        var data = new TheoryData<string, string>();
        // Entity and fact handles are numbered across campaigns, so the sweep covers every number any campaign here uses.
        var handles = Enumerable.Range(1, 40).Select(i => $"e:{i}").Concat(Enumerable.Range(1, 12).Select(i => $"f:{i}"))
            .Concat(["character:mirelle-duskbane", "mirelle-duskbane", "nightglass", "character:queen-ysolde", "location:the-rose-market", "session:1"]);
        foreach (var perspective in BelmakorServer.VeilPerspectives)
        {
            foreach (var handle in handles)
            {
                data.Add(perspective, handle);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VeilPerspectivesAndHandles))]
    public async Task Get_VeilNonAuthorPerspective_AnyHandle_NeverSpellsTheTrueName(string perspective, string handle)
    {
        var result = await _f.Server.CallToolJsonAsync("campaign_get",
            $$"""{"refs":["{{handle}}"],"include":{{AllIncludes}},"detail":"full","perspective":"{{perspective}}","campaign":"veil"}""");

        var text = _f.Server.SingleText(result).Replace($"\"{handle}\"", "\"<handle>\"", StringComparison.Ordinal);
        BelmakorServer.AssertClean(text, BelmakorServer.VeilForbidden, $"veil get {handle} as {perspective}");
        BelmakorServer.AssertClean(text, AuthorOnlyWording, $"veil get {handle} as {perspective}");
    }

    [Fact]
    public async Task Get_VeilTheMarketAsParty_PointsAtHerByTheNameThePartyUses()
    {
        var text = await _f.Call("campaign_get", """{"refs":["location:the-rose-market"],"perspective":"party","campaign":"veil"}""");

        Assert.Matches(new Regex(@"- ← seen_at from the veiled woman \(`e:\d+`\) · current"), text);
        BelmakorServer.AssertClean(text, BelmakorServer.VeilForbidden, "the market as the party");
    }

    [Fact]
    public async Task Get_VeilHerAsParty_ShowsOnlyHerKnownNameAndTheFactsInThePartysPhrasing()
    {
        var listing = await _f.Call("campaign_search", """{"query":"veiled woman","perspective":"party","campaign":"veil","include_facts":false}""");
        var seqRef = Regex.Match(listing, "`(e:\\d+)`").Groups[1].Value;

        var text = await _f.Call("campaign_get", $$"""{"refs":["{{seqRef}}"],"include":{{AllIncludes}},"detail":"full","perspective":"party","campaign":"veil"}""");

        Assert.StartsWith($"# the veiled woman (`{seqRef}`)\n_Perspective: party.", text, StringComparison.Ordinal);
        Assert.Contains($"`{BelmakorServer.VeilRoseFact}`: The veiled woman left a black rose at the market.", text, StringComparison.Ordinal);
        Assert.Contains($"`{BelmakorServer.VeilEnvoyFact}`: Someone killed the envoy.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Relations", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("character:belmakor")]
    [InlineData("public")]
    public async Task Get_AHandleTheViewCannotSee_ReadsExactlyLikeOneThatDoesNotExist(string perspective)
    {
        var hidden = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_get", $$"""{"refs":["f:1"],"perspective":"{{perspective}}"}"""));
        var missing = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_get", $$"""{"refs":["f:99"],"perspective":"{{perspective}}"}"""));

        Assert.Equal(missing.Replace("f:99", "f:1", StringComparison.Ordinal), hidden);
    }

    [Fact]
    public async Task Get_PartyTheDisguisedItem_ShowsOnlyItsKnownNameKindSeqRefAndTheFactsThePartyKnows()
    {
        var listing = await _f.Call("campaign_search", """{"query":"thing he wants","perspective":"party","include_facts":false}""");
        var seqRef = Regex.Match(listing, "`(e:\\d+)`").Groups[1].Value;

        var text = await _f.Call("campaign_get", $$"""{"refs":["{{seqRef}}"],"include":{{AllIncludes}},"detail":"full","perspective":"party"}""");

        Assert.StartsWith($"# the thing he wants (`{seqRef}`)\n_Perspective: party. Names are the ones this view knows; author-only text is withheld._\nitem\n",
            text, StringComparison.Ordinal);
        Assert.Contains("`f:3`: After the fight the old king sent the party to fetch something", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Something more dangerous than anything they've seen", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Aliases", text, StringComparison.Ordinal);
        Assert.DoesNotContain("thing-he-wants", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_PartyByTheTrueSlug_OfADisguisedItem_IsNotFound()
    {
        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_get", """{"refs":["item:thing-he-wants"],"perspective":"party"}"""));

        Assert.StartsWith("An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"item:thing-he-wants\": ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The thing the old king wants", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AuthorView_ShowsTheSecretAliasesWithVisibilityCrossLinksKnowledgeAndHistory()
    {
        var text = await _f.Call("campaign_get", $$"""{"refs":["character:old-king"],"include":{{AllIncludes}},"detail":"full"}""");

        Assert.Matches(@"\A# The Old King \(`character:old-king` · `e:\d+`\)\ncharacter · npc · status unknown\n", text);
        Assert.Contains("- **Aliases:** Keras (author), the sorcerer king (party)\n", text, StringComparison.Ordinal);
        Assert.Contains("> [!secret]\n> Keras, Cole's old PC from his other campaign, imported at Cole's request; Cole ran him in the fight.\n", text,
            StringComparison.Ordinal);
        Assert.Contains("- character:old-king · party: knows (met) as “the old king” · S3 · witnessed\n", text, StringComparison.Ordinal);
        Assert.Contains("- same as `one-piece/character:keras` (Keras, campaign one-piece): Same being:", text, StringComparison.Ordinal);
        Assert.Contains("## History (newest first)\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("_Perspective:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_CharacterPerspective_SeesItsOwnRestrictedFactAndTheBannerNamesOnlyItself()
    {
        var text = await _f.Call("campaign_get", """{"refs":["character:belmakor"],"include":["facts","knowledge"],"perspective":"character:belmakor"}""");

        Assert.Equal(
            "_Perspective: character:belmakor (Belmakor Silverwind). Names are the ones this view knows; author-only text is withheld._",
            text.Split('\n')[1]);
        Assert.Contains("Belmakor intends to reclaim the blighted surface", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_SeveralRefs_TitlesTheResultAndGivesEachItsOwnSection()
    {
        var text = await _f.Call("campaign_get", """{"refs":["character:old-king","f:3","session:3"],"include":[],"perspective":"party"}""");

        Assert.StartsWith("# 3 entries from belmakor\n_Perspective: party.", text, StringComparison.Ordinal);
        Assert.Contains("\n## The Old King (`character:old-king`)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Kraken, the statue, the old king (`session:3`)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Fact `f:3`\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"refs":[]}""", "refs is required: give 1 to 10 handles, e.g. [\"character:belmakor\", \"f:12\"].")]
    [InlineData("""{"refs":["e:1","e:2","e:3","e:4","e:5","e:6","e:7","e:8","e:9","e:10","e:11"]}""",
        "refs has 11 handles; give at most 10 per call and page through the rest.")]
    [InlineData("""{"refs":["e:1"],"include":["relations","bogus","more"]}""",
        "Invalid include (2 problems):\n- include item 2 (\"bogus\") is not an include; use relations, facts, knowledge, children, sessions, history, sheet.\n" +
        "- include item 3 (\"more\") is not an include; use relations, facts, knowledge, children, sessions, history, sheet.")]
    [InlineData("""{"refs":["e:1"],"detail":"verbose"}""", "detail must be \"concise\" (the default) or \"full\" (got \"verbose\").")]
    [InlineData("""{"refs":["character:nobody","::"]}""",
        "Invalid refs (2 problems):\n- refs item 1: \"character:nobody\": nothing in this campaign has that handle.")]
    public async Task Get_BadArgument_IsRefusedWithWhatIsAccepted(string arguments, string message)
    {
        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_get", arguments));

        Assert.StartsWith("An error occurred invoking 'campaign_get': " + message, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_MissingRefs_GuardNamesItAndListsTheArguments()
    {
        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign_get", "{}"));

        Assert.Equal("An error occurred invoking 'campaign_get': Invalid arguments: missing required argument 'refs'. " + Accepted, text);
    }

    [Fact]
    public async Task CallTool_NullForEveryOptionalArgument_MeansTheDefault()
    {
        var withNulls = await _f.Call("campaign_get",
            """{"refs":["character:old-king"],"include":null,"detail":null,"perspective":null,"as_of_session":null,"campaign":null}""");
        var plain = await _f.Call("campaign_get", """{"refs":["character:old-king"],"include":["relations","facts","children"],"detail":"concise"}""");

        Assert.Equal(plain, withNulls);
        Assert.Contains("## Relations\n", withNulls, StringComparison.Ordinal);
        Assert.DoesNotContain("## Knowledge", withNulls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_TypicalEntry_IsUnderTheTypicalBudget()
    {
        var text = await _f.Call("campaign_get", $$"""{"refs":["character:old-king"],"include":{{AllIncludes}}}""");

        Assert.True(text.Length < 8_000, $"get is {text.Length} characters");
    }

    [Fact]
    public async Task Get_DmInTheDmCampaign_IsTheAuthorsViewWithNoBanner()
    {
        // In a DM campaign the dm is the author (contract §3.2): the author-only item and its author alias are shown.
        var text = await _f.Call("campaign_get", """{"refs":["item:axiom-cage"],"perspective":"dm","campaign":"one-piece"}""");

        Assert.Matches(@"\A# The Axiom Cage \(`item:axiom-cage` · `e:\d+`\)\nitem · artifact\n- \*\*Aliases:\*\* The Third Silence \(author\)\n", text);
        Assert.Contains("\n## Author\n- visibility author ", text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Invariant: <c>campaign_get</c> renders every part of an entry the reader returns: a clock's fill, front, player flag and
/// on-fill text, a quest's objectives, tags, the parent and what is inside it, the sessions a character was in (absences
/// included), a secret's stored and derived status with each gate, and why a view knows each entry; and for every other
/// view none of the golden campaign's author-only text.
///
/// <para>
/// Why it fails silently: each of those is an optional block that only some entries have, so a block the formatter stopped
/// printing (or printed for every view) is invisible to tests on entries that lack it.
/// </para>
/// </summary>
public sealed class CampaignGetToolGoldenTests : IClassFixture<GoldenCampaignServer>
{
    private const string AllIncludes = """["relations","facts","knowledge","children","sessions","history"]""";

    private readonly GoldenCampaignServer _g;

    public CampaignGetToolGoldenTests(GoldenCampaignServer fixture)
    {
        _g = fixture;
    }

    public static TheoryData<string, string> PerspectivesAndHandles()
    {
        var data = new TheoryData<string, string>();
        var handles = Enumerable.Range(1, 30).Select(i => $"e:{i}").Concat(Enumerable.Range(1, 5).Select(i => $"f:{i}"))
            .Concat(["clock:tide-rises", "clock:cult-awakens", "quest:relight-the-lamp", "location:hidden-cove", "secret:keepers-heir", "character:vell",
                "F1", "F2", "session:1", "session:2", "session:live"]);
        foreach (var perspective in GoldenCampaignServer.NonAuthorPerspectives)
        {
            foreach (var handle in handles)
            {
                data.Add(perspective, handle);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PerspectivesAndHandles))]
    public async Task Get_NonAuthorPerspective_AnyHandle_ShowsNoAuthorOnlyText(string perspective, string handle)
    {
        var result = await _g.Server.CallToolJsonAsync("campaign_get",
            $$"""{"refs":["{{handle}}"],"include":{{AllIncludes}},"detail":"full","perspective":"{{perspective}}"}""");

        var text = _g.Server.SingleText(result).Replace($"\"{handle}\"", "\"<handle>\"", StringComparison.Ordinal);
        BelmakorServer.AssertClean(text, GoldenCampaignServer.AuthorOnly, $"golden get {handle} as {perspective}");
    }

    [Fact]
    public async Task Get_Clock_ShowsItsFillFrontPlayerFlagAndOnFillToTheAuthor()
    {
        var text = await _g.Call("campaign_get", """{"refs":["clock:tide-rises"]}""");

        Assert.Matches(@"\A# The Tide Rises \(`clock:tide-rises` · `e:\d+`\)\n", text);
        Assert.Contains(
            ")\nclock · status running\n- **Clock:** 2 of 6 segments filled · front The Drowned Court " +
            "(`front:drowned-court`) · shown to players · when it fills: The lower town floods.\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Clock_PartyGetsItsFillAndFrontOnly()
    {
        var text = await _g.Call("campaign_get", """{"refs":["clock:tide-rises"],"perspective":"party"}""");

        Assert.Contains("\n- **Clock:** 2 of 6 segments filled · front The Drowned Court (`front:drowned-court`)\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Quest_ListsItsObjectivesAndThePartyOnlyItsOwn()
    {
        var author = await _g.Call("campaign_get", """{"refs":["quest:relight-the-lamp"]}""");
        var party = await _g.Call("campaign_get", """{"refs":["quest:relight-the-lamp"],"perspective":"party"}""");

        Assert.Contains("\n- **Objectives:**\n  1. Find oil for the lamp · open · 1/3 · party\n  2. Betray the keeper · open · author\n", author, StringComparison.Ordinal);
        Assert.Contains("\n- **Objectives:**\n  1. Find oil for the lamp · open · 1/3\n", party, StringComparison.Ordinal);
        Assert.DoesNotContain("Betray", party, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Region_ShowsItsTagsAndWhatIsInsideIt()
    {
        var text = await _g.Call("campaign_get", """{"refs":["location:archipelago"]}""");

        Assert.Matches(@"\A# The Archipelago \(`location:archipelago` · `e:\d+`\)\n", text);
        Assert.Contains(
            ")\nlocation\n- **Tags:** region, sea\n\n## Inside it\n- The Lighthouse (`location:lighthouse`) · location\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Location_ShowsItsSummaryTagsParentAndBody()
    {
        var text = await _g.Call("campaign_get", """{"refs":["location:lighthouse"],"perspective":"party"}""");

        Assert.Contains(
            "\nlocation\n> A tower of white stone.\n- **Tags:** landmark\n- **Part of:** The Archipelago (`location:archipelago`)\n\nThe lamp room holds a great lens.\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Character_SessionsListsEachSessionItWasInWithItsAbsence()
    {
        var text = await _g.Call("campaign_get", """{"refs":["character:brom"],"include":["sessions"]}""");

        Assert.Contains("\n## Sessions\n- `session:1` Landfall · absent · sick\n- `session:2` Session 2 · attended\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Secret_ShowsItsStatusGatesAndCluesToTheAuthor()
    {
        var text = await _g.Call("campaign_get", """{"refs":["secret:keepers-heir"]}""");

        Assert.Contains($"- `{_g.ClueFact}`: Salt stains mark the keeper's boots. _(clue_for)_\n", text, StringComparison.Ordinal);
        Assert.Contains(
            $"\n- secret status: partial (stored), partial (from its gates now)\n  - `{_g.GatedFact}`: not ready · after unmet: {_g.ClueFact} · " +
            "routes 1/1 (boots 1/1 complete) · seeded · reachable before the gate\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_PartyKnowledge_SaysWhyTheViewKnowsEachEntry()
    {
        var text = await _g.Call("campaign_get", """{"refs":["location:lighthouse"],"include":["knowledge"],"perspective":"party"}""");

        Assert.Contains(
            "\n## Knowledge\n- location:lighthouse · party: knows (met) · S1 — the party's own record: met, S1\n" +
            $"- {_g.ClueFact} · party: knows · S1 — the party's own record: knows, S1\n",
            text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Invariant: <c>campaign_get</c> reads as of a session, cuts long bodies in concise output (saying how to see the rest),
/// and keeps its largest result (ten long entries with every include) under the output cap with every entry still in it.
///
/// <para>
/// Why it fails silently: without a shared budget the first long body eats the whole cap and the other nine entries
/// vanish behind "output cut", which reads like a complete answer about one entry.
/// </para>
/// </summary>
public sealed class CampaignGetToolWriteTests : IAsyncLifetime
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
    public async Task Get_AsOfAnEarlierSession_ShowsTheEntryAsItStoodThen()
    {
        var sessions = new SessionWriter(_s.Database);
        sessions.RecordPast(_c, 1, "One");
        _s.Apply(_c, WriteContext.For(1), new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", Status = "alive", Summary = "A dwarf.", Visibility = "party" });
        sessions.RecordPast(_c, 2, "Two");
        _s.Apply(_c, WriteContext.For(2), new CampaignOpSpec { Op = "upsert", Ref = "character:iron-guts", Status = "dead", Summary = "A dead dwarf." });

        var then = await _s.Call("campaign_get", """{"refs":["character:iron-guts"],"as_of_session":1,"perspective":"party"}""");

        Assert.StartsWith(
            "# Iron Guts (`character:iron-guts`)\n_Perspective: party. Names are the ones this view knows; author-only text is withheld._\n" +
            "_As of the end of session 1._\ncharacter · status alive\n> A dwarf.\n",
            then, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review LR01: a character's view as of a session names him as he knew himself then. Keras knew himself only as "the
    /// stranger" until session 3; read as of session 2, the banner printed today's "(Keras Dawnbreaker)" above a listing of
    /// himself as "the stranger", and "character:keras" completed by today's own view, though while his own view is
    /// disguised a prefix never completes (its full handle spells the name he does not know). Today both are as before.
    /// </summary>
    [Fact]
    public async Task Search_AsOfBeforeACharacterLearnedHisOwnName_NamesHimAsHeKnewHimselfThen()
    {
        await KerasLearnsHisOwnNameInSession3Async();

        var then = await _s.Call("campaign_search", """{"kinds": ["character"], "perspective": "character:keras-dawnbreaker", "as_of_session": 2}""");
        var prefixThen = await _s.Error("campaign_search", """{"kinds": ["character"], "perspective": "character:keras", "as_of_session": 2}""");
        var prefixNow = await _s.Call("campaign_search", """{"kinds": ["character"], "perspective": "character:keras"}""");

        Assert.Contains("\n_Perspective: character:keras-dawnbreaker (the stranger). Names are the ones this view knows;", then, StringComparison.Ordinal);
        Assert.Contains("**the stranger** · character", then, StringComparison.Ordinal);
        Assert.DoesNotContain("Dawnbreaker)", then, StringComparison.Ordinal);
        Assert.Contains("perspective \"character:keras\": no character keras in this campaign.", prefixThen, StringComparison.Ordinal);
        Assert.Contains("\n_Perspective: character:keras-dawnbreaker (Keras Dawnbreaker), the only character whose handle starts with character:keras.",
            prefixNow, StringComparison.Ordinal);
    }

    /// <summary>
    /// LR01's recheck: every read of a character's view as of a session resolves him as he stood then, not only
    /// campaign_search's banner. campaign_get's banner and its explanations name him as he knew himself in S2 ("the
    /// stranger's own record"), and campaign_knowledge's check and ledger refuse the prefix as campaign_search does.
    /// Resolved today, the get's banner said "(Keras Dawnbreaker)" and its knowledge line "Keras Dawnbreaker's own record",
    /// a name he learned in S3, and the check and the ledger completed "character:keras" to the handle that spells it.
    /// </summary>
    [Fact]
    public async Task Get_AsOfBeforeACharacterLearnedHisOwnName_EveryReadOfHisViewThenNamesHimSo()
    {
        await KerasLearnsHisOwnNameInSession3Async();
        var listing = await _s.Call("campaign_search", """{"kinds": ["character"], "perspective": "character:keras-dawnbreaker", "as_of_session": 2}""");
        var self = Regex.Match(listing, "`(e:[0-9]+)`").Groups[1].Value;

        var get = await _s.Call("campaign_get",
            $$"""{"refs": ["{{self}}"], "include": ["knowledge"], "perspective": "character:keras-dawnbreaker", "as_of_session": 2}""");
        var checkThen = await _s.Error("campaign_knowledge", """{"action": "check", "perspective": "character:keras", "text": "The stranger sings.", "as_of_session": 2}""");
        var ledgerThen = await _s.Error("campaign_knowledge",
            """{"action": "ledger", "about": ["character:keras-dawnbreaker"], "perspectives": ["character:keras"], "as_of_session": 2}""");
        var checkNow = await _s.Call("campaign_knowledge", """{"action": "check", "perspective": "character:keras", "text": "The stranger sings."}""");
        var ledgerNow = await _s.Call("campaign_knowledge", """{"action": "ledger", "about": ["character:keras-dawnbreaker"], "perspectives": ["character:keras"]}""");

        Assert.StartsWith($"# the stranger (`{self}`)\n_Perspective: character:keras-dawnbreaker (the stranger). Names are the ones this view knows;", get,
            StringComparison.Ordinal);
        Assert.Contains(" — the stranger's own record", get, StringComparison.Ordinal);
        Assert.DoesNotContain("Keras Dawnbreaker", get, StringComparison.Ordinal);
        Assert.Contains("no character keras in this campaign", checkThen, StringComparison.Ordinal);
        Assert.Contains("perspectives item 1: perspective \"character:keras\": no character keras in this campaign", ledgerThen, StringComparison.Ordinal);
        Assert.Contains("Speaker: character:keras-dawnbreaker", checkNow, StringComparison.Ordinal);
        Assert.Contains("character:keras-dawnbreaker", ledgerNow, StringComparison.Ordinal);
    }

    // Keras Dawnbreaker, a party PC, knew himself as "the stranger" from S1 and by his own name from S3 (sessions 1-3 played).
    private async Task KerasLearnsHisOwnNameInSession3Async()
    {
        var sessions = new SessionWriter(_s.Database);
        foreach (var n in new[] { 1, 2, 3 })
        {
            sessions.RecordPast(_c, n, "Night");
        }

        await _s.Call("campaign_write", """
            {"campaign": "sky", "ops": [{"op": "upsert", "kind": "character", "name": "Keras Dawnbreaker", "subtype": "pc", "visibility": "party"},
              {"op": "link", "from": "character:keras-dawnbreaker", "rel": "member_of", "to": "faction:the-party", "visibility": "party"}]}
            """);
        foreach (var (session, name) in new[] { (1, "the stranger"), (3, "Keras Dawnbreaker") })
        {
            await _s.Call("campaign_knowledge", $$"""
                {"campaign": "sky", "action": "record", "targets": ["character:keras-dawnbreaker"], "session": {{session}},
                 "knowers": [{"who": "character:keras-dawnbreaker", "state": "met", "known_as": "{{name}}"}]}
                """);
        }
    }

    [Fact]
    public async Task Get_ConciseLongBody_IsCutAndSaysHowToSeeTheRest()
    {
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "lore", Name = "The Long History", BodyMd = string.Join(' ', Enumerable.Repeat("word", 1_500)) });

        var concise = await _s.Call("campaign_get", """{"refs":["lore:the-long-history"]}""");
        var full = await _s.Call("campaign_get", """{"refs":["lore:the-long-history"],"detail":"full"}""");

        Assert.Matches(new Regex(@"\n\n_… [\d,]+ more characters; detail ""full"" shows them\._\n"), concise);
        Assert.Contains(string.Join(' ', Enumerable.Repeat("word", 1_500)), full, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_LargestResult_TenLongEntriesWithEveryInclude_StaysUnderTheCapWithEveryEntry()
    {
        var names = Enumerable.Range(1, 10).Select(i => $"Colossus {i:D2}").ToList();
        _s.Apply(_c, WriteContext.Default, names.Select(n => new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = n, Summary = new string('s', 1_000),
            BodyMd = string.Join('\n', Enumerable.Repeat("A long paragraph of lore about this colossus and its deeds.", 800)),
            SecretMd = string.Join('\n', Enumerable.Repeat("A long paragraph of secret lore that only the author reads.", 800)),
        }).ToArray());
        foreach (var name in names)
        {
            var slug = "character:" + DndMcp.Domain.Campaign.CampaignSlugs.From(name, "character");
            _s.Apply(_c, WriteContext.Default, names.Where(o => o != name).Select(o => new CampaignOpSpec
            {
                Op = "link", From = slug, Rel = "rival_of", To = "character:" + DndMcp.Domain.Campaign.CampaignSlugs.From(o, "character"), Label = new string('l', 100),
            }).ToArray());
            _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 20).Select(i => new CampaignOpSpec
            {
                Op = "fact", Statement = $"{name} did deed {i}: " + new string('d', 400), About = [slug], Visibility = "party",
            }).ToArray());
        }

        var refs = string.Join(",", names.Select(n => $"\"character:{DndMcp.Domain.Campaign.CampaignSlugs.From(n, "character")}\""));
        var text = await _s.Call("campaign_get", $$"""{"refs":[{{refs}}],"include":["relations","facts","knowledge","children","sessions","history"],"detail":"full"}""");

        Assert.True(text.Length <= 24_000, $"get is {text.Length} characters");
        Assert.All(names, n => Assert.Contains($"## {n} (`character:", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_FullOneLongBody_IsReadWholeThroughTheToolAndTheResource()
    {
        var body = Lines("of the long lore, one paragraph after another.", 400);
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "lore", Name = "The Long Lore", BodyMd = body });

        var text = await _s.Call("campaign_get", """{"refs":["lore:the-long-lore"],"detail":"full"}""");
        var resource = await _s.Read("campaign://sky/entity/lore:the-long-lore");

        Assert.True(body.Length > 20_000, $"the body is {body.Length} characters");
        Assert.Contains(body, text, StringComparison.Ordinal);
        Assert.Contains(body, resource, StringComparison.Ordinal);
        Assert.DoesNotContain("more characters", text, StringComparison.Ordinal);
        Assert.Contains("\n## Author\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_FullOneBodyLongerThanAResult_ShowsWhatFitsSaysHowMuchIsLeftAndHowToMakeRoom()
    {
        var body = Lines("of the endless lore, one paragraph after another.", 800);
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "lore", Name = "The Endless Lore", BodyMd = body });
        _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 8).Select(i => new CampaignOpSpec
        {
            Op = "fact", Statement = $"Deed {i}: " + new string('d', 900), About = ["lore:the-endless-lore"],
        }).ToArray());

        var withFacts = await _s.Call("campaign_get", """{"refs":["lore:the-endless-lore"],"detail":"full"}""");
        var bare = await _s.Call("campaign_get", """{"refs":["lore:the-endless-lore"],"detail":"full","include":[]}""");

        Assert.True(withFacts.Length <= 24_000, $"get is {withFacts.Length} characters");
        Assert.Matches(new Regex(
            "\n\n_… [\\d,]+ more characters that do not fit in one result beside the rest of this entry; include \\[\\] leaves out the relations, " +
            "facts and other sections to make room\\._\n"), withFacts);
        Assert.Contains("\n## Facts\n", withFacts, StringComparison.Ordinal);
        Assert.Contains("\n## Author\n", withFacts, StringComparison.Ordinal);
        Assert.DoesNotContain("Output cut", withFacts, StringComparison.Ordinal);
        Assert.Matches(new Regex("\n\n_… [\\d,]+ more characters that do not fit in one result\\._\n"), bare);
        Assert.True(BodyLinesShown(bare) > BodyLinesShown(withFacts), "include [] did not make room for more of the body");
        Assert.True(BodyLinesShown(bare) * body.Split('\n')[0].Length > 20_000, $"only {BodyLinesShown(bare)} lines of the body were shown");

        static int BodyLinesShown(string text) => Regex.Matches(text, "of the endless lore").Count;
    }

    [Fact]
    public async Task Get_ConciseEntityWithManyLongFacts_KeepsItsKnowledgeAndAuthorSections()
    {
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Sage", Visibility = "party" });
        _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 30).Select(i => new CampaignOpSpec
        {
            Op = "fact", Visibility = "party", About = ["character:the-sage"],
            Statement = $"Saying {i:D2}: " + string.Join(' ', Enumerable.Repeat("the long road the sage walked", 30)),
        }).ToArray());
        new KnowledgeWriter(_s.Database).Record(_c, ["character:the-sage"], [new KnowerSpec { Who = "party", State = "met" }], WriteContext.Default);

        var text = await _s.Call("campaign_get", """{"refs":["character:the-sage"],"include":["relations","facts","knowledge"]}""");

        Assert.DoesNotContain("Output cut", text, StringComparison.Ordinal);
        Assert.Contains("\n## Knowledge\n- character:the-sage · party: knows (met)", text, StringComparison.Ordinal);
        Assert.Contains("\n## Author\n", text, StringComparison.Ordinal);
        Assert.Equal(30, Regex.Matches(text, "Saying \\d\\d: the long road[^\n]{300,400}…\n").Count);
    }

    [Fact]
    public async Task Get_FullEntityWithManyLongFacts_IsCutAtTheCapAndSaysSo()
    {
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Chronicler" });
        _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 40).Select(i => new CampaignOpSpec
        {
            Op = "fact", About = ["character:the-chronicler"], Statement = $"Entry {i:D2}: " + string.Join(' ', Enumerable.Repeat("a long chronicle", 200)),
        }).ToArray());

        var text = await _s.Call("campaign_get", """{"refs":["character:the-chronicler"],"detail":"full"}""");

        Assert.True(text.Length <= 24_000, $"get is {text.Length} characters");
        Assert.EndsWith("_Output cut at 24,000 characters; ask for fewer refs, leave out include, or use detail \"concise\"._", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ConciseEntityOverTheCap_IsCutSayingToLeaveOutInclude()
    {
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Hub" });
        _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 40).Select(i => new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = $"Spoke {i:D2} " + string.Join(' ', Enumerable.Repeat("of the great wheel", 8)),
        }).ToArray());
        _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 40).Select(i => new CampaignOpSpec
        {
            Op = "link", From = "character:the-hub", Rel = "rival_of",
            To = "character:" + CampaignSlugs.From($"Spoke {i:D2} " + string.Join(' ', Enumerable.Repeat("of the great wheel", 8)), "character"),
            Label = new string('l', 190),
        }).ToArray());
        _s.Apply(_c, WriteContext.Default, Enumerable.Range(1, 40).Select(i => new CampaignOpSpec
        {
            Op = "fact", About = ["character:the-hub"], Statement = $"Rumour {i:D2}: " + string.Join(' ', Enumerable.Repeat("they say the hub turns", 40)),
        }).ToArray());

        var text = await _s.Call("campaign_get", """{"refs":["character:the-hub"]}""");

        Assert.True(text.Length <= 24_000, $"get is {text.Length} characters");
        Assert.EndsWith("_Output cut at 24,000 characters; ask for fewer refs, or leave out include (facts, knowledge and history are the long ones)._", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ConciseLongSecret_IsCutToItsOpeningInsideTheSecretBlock()
    {
        var secret = Lines("of what only the author knows.", 100);
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Spy", SecretMd = secret });

        var concise = await _s.Call("campaign_get", """{"refs":["character:the-spy"]}""");
        var full = await _s.Call("campaign_get", """{"refs":["character:the-spy"],"detail":"full"}""");

        Assert.Matches(new Regex("\n> \\[!secret\\]\n> Line 001 of what only the author knows\\.\n(> Line \\d{3}[^\n]*\n)+> \n> _… [\\d,]+ more characters; detail \"full\" shows them\\._\n"),
            concise);
        Assert.DoesNotContain("Line 100 ", concise, StringComparison.Ordinal);
        Assert.Contains("> Line 100 of what only the author knows.\n", full, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_HistoryLongerThanShown_TheCallAsPrintedPagesThroughItWhileAnotherCampaignIsCurrent()
    {
        _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Iron Guts", Status = "alive" });
        for (var i = 0; i < 17; i++)
        {
            _s.Apply(_c, WriteContext.Default, new CampaignOpSpec { Op = "status", Ref = "character:iron-guts", Status = i % 2 == 0 ? "dead" : "alive" });
        }

        var text = await _s.Call("campaign_get", """{"refs":["character:iron-guts"],"include":["history"]}""");
        await _s.Create("Other");
        var call = Regex.Match(text, "_… and 3 more; campaign_history (\\{[^}]*\\}) pages through the rest\\._").Groups[1].Value;
        var history = await _s.Call("campaign_history", call);

        Assert.Equal("{\"action\": \"entity\", \"ref\": \"character:iron-guts\", \"campaign\": \"sky\"}", call);
        Assert.StartsWith("# History of character:iron-guts in sky (newest first)\n\n15 of 18 batches on this page", history, StringComparison.Ordinal);
    }

    /// <summary>
    /// The author's heading prints the entry's <c>e:&lt;n&gt;</c> beside its <c>kind:slug</c> (review U05): the handle that
    /// reads the same entry in a perspective that knows it under another name, where the <c>kind:slug</c> (it spells the
    /// true name) is refused. A view's heading prints the one ref that view uses.
    /// </summary>
    [Fact]
    public async Task Get_AuthorHeading_PrintsTheSeqHandleThatReadsTheEntryInADisguisedView()
    {
        await _s.Call("campaign_write", """
            {"ops": [{"op": "upsert", "kind": "item", "name": "Axiom Cage", "visibility": "party",
              "known_by": [{"who": "party", "state": "heard", "known_as": "what the sorcerer king sent us for"}]}]}
            """);

        var author = await _s.Call("campaign_get", """{"refs":["item:axiom-cage"]}""");
        var seq = Regex.Match(author, @"\A# Axiom Cage \(`item:axiom-cage` · `(e:\d+)`\)\n").Groups[1].Value;
        var party = await _s.Call("campaign_get", $$"""{"refs":["{{seq}}"],"perspective":"party"}""");
        var refused = await _s.Error("campaign_get", """{"refs":["item:axiom-cage"],"perspective":"party"}""");

        Assert.NotEmpty(seq);
        Assert.StartsWith($"# what the sorcerer king sent us for (`{seq}`)\n_Perspective: party.", party, StringComparison.Ordinal);
        Assert.Contains("\"item:axiom-cage\": nothing by that handle for this perspective.", refused, StringComparison.Ordinal);
    }

    /// <summary>
    /// A perspective typed as the start of exactly one character's slug reads as that character, and the banner says which
    /// character it took (review U03): campaign create makes <c>character:belmakor-silverwind</c> from "Belmakor
    /// Silverwind", and every fresh session's first call said <c>character:belmakor</c> and was refused. The full handle
    /// gets the plain banner.
    /// </summary>
    [Fact]
    public async Task Get_PerspectiveByTheStartOfOneCharactersSlug_ReadsAsThatCharacterAndTheBannerSaysSo()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            await CampaignWriteSetup.CallAsync(server, "campaign",
                """{"action": "create", "name": "sky", "slug": "sky", "role": "player", "ruleset": "2014", "my_character": "Belmakor Silverwind"}""");
            await CampaignWriteSetup.WriteAsync(server, "sky", """[{"op": "upsert", "kind": "location", "name": "Flotsam", "summary": "A floating port."}]""");

            var get = await CampaignWriteSetup.CallAsync(server, "campaign_get",
                """{"campaign": "sky", "refs": ["location:flotsam"], "perspective": "character:belmakor"}""");
            var sessions = await CampaignWriteSetup.CallAsync(server, "campaign_session",
                """{"campaign": "sky", "action": "list", "perspective": "character:belmakor"}""");
            var full = await CampaignWriteSetup.CallAsync(server, "campaign_get",
                """{"campaign": "sky", "refs": ["location:flotsam"], "perspective": "character:belmakor-silverwind"}""");

            const string Banner = "_Perspective: character:belmakor-silverwind (Belmakor Silverwind), the only character whose handle starts with " +
                                  "character:belmakor. Names are the ones this view knows; author-only text is withheld._";
            Assert.StartsWith("# Flotsam (`location:flotsam`)\n" + Banner + "\n", get, StringComparison.Ordinal);
            Assert.Contains(Banner, sessions, StringComparison.Ordinal);
            Assert.StartsWith("# Flotsam (`location:flotsam`)\n_Perspective: character:belmakor-silverwind (Belmakor Silverwind). Names", full,
                StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A beat's page shows the author its story web, and the author's beat listing marks the reachable ones (review C03):
    /// <c>link … leads_to</c> between beats was stored and never shown by any read, while session_prep sends the model to
    /// the beats to see "which are reachable now and which are locked behind which".
    /// </summary>
    [Fact]
    public async Task Get_Beat_ShowsTheAuthorItsStoryWebAndTheListingMarksTheReachableBeats()
    {
        await _s.Call("campaign_write", """
            {"ops": [{"op": "upsert", "kind": "beat", "name": "Find the map"}, {"op": "upsert", "kind": "beat", "name": "Storm the vault"},
              {"op": "link", "from": "beat:find-the-map", "rel": "leads_to", "to": "beat:storm-the-vault", "mode": "all_of"}]}
            """);

        var vault = await _s.Call("campaign_get", """{"refs":["beat:storm-the-vault"]}""");
        var map = await _s.Call("campaign_get", """{"refs":["beat:find-the-map"],"include":[]}""");
        var listing = await _s.Call("campaign_search", """{"kinds":["beat"]}""");
        var dm = await _s.Call("campaign_get", """{"refs":["beat:storm-the-vault"],"perspective":"dm"}""");

        Assert.Contains(
            "\n## Story web\n- blocked by Find the map (`beat:find-the-map`) (0 of 1 prerequisite met; all of them are needed)\n" +
            "- after Find the map (`beat:find-the-map`) · all_of · pending\n", vault, StringComparison.Ordinal);
        Assert.Contains("\n## Story web\n- reachable now (no prerequisites)\n- leads to Storm the vault (`beat:storm-the-vault`) · all_of · pending\n",
            map, StringComparison.Ordinal);
        Assert.Contains("\n1. **Find the map** · beat · pending · reachable now · `beat:find-the-map`\n", listing, StringComparison.Ordinal);
        Assert.Contains("\n2. **Storm the vault** · beat · pending · `beat:storm-the-vault`\n", listing, StringComparison.Ordinal);
        Assert.Contains("## Story web", dm, StringComparison.Ordinal);
    }

    // "Line 001 <text>\nLine 002 <text>…": a long body with a line to cut at every 50-odd characters.
    private static string Lines(string text, int count) => string.Join('\n', Enumerable.Range(1, count).Select(i => $"Line {i:D3} {text}"));
}

/// <summary>
/// Invariant (contract §7.3, §7.4): <c>campaign_get include: ["sheet"]</c> adds a character's sheet under its entry: the
/// author's whole sheet (as of a session with <c>as_of_session</c>, through the change history), any other view's public
/// line of a current party member it is shown, and nothing at all (no heading) for a character with no sheet, a character
/// the view may not be shown a sheet for, or an entry that is not a character.
///
/// <para>
/// Why it fails silently: a section printed with nothing under it, or a "no sheet" line, says a sheet exists or does not;
/// a sheet read today under an as-of banner shows a later HP as the one the party had then.
/// </para>
/// </summary>
public sealed class CampaignGetToolSheetTests : IAsyncLifetime
{
    private CampaignTestServer _s = null!;

    public async Task InitializeAsync()
    {
        _s = await CampaignTestServer.StartAsync();
        await CharacterToolSetup.CreateSkyAsync(_s.Server);
    }

    public async Task DisposeAsync() => await _s.DisposeAsync();

    [Fact]
    public async Task Get_IncludeSheet_TheAuthorReadsTheWholeSheetAfterTheEntrysOwnLines()
    {
        await CharacterToolSetup.BelmakorAsync(_s.Server);

        var text = await _s.Call("campaign_get", """{"refs": ["character:belmakor"], "include": ["sheet"]}""");

        Assert.Matches(new Regex(
            @"\A# Belmakor \(`character:belmakor` · `e:\d+`\)\ncharacter · pc · status alive\n\n## Sheet\nlevel 12 Wizard \(Bladesinger\), 2014\n" +
            @"`character:belmakor` · player Cole · High Elf · background Noble · source fixture\nHP 110/110 \(\+7 temp\) · AC 17 · Init \+5 · PB \+4 · Exhaustion 0\n"),
            text);
        Assert.Contains("- **Feats:** War Caster · Resilient (Constitution) · Fey Touched · Tough\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("dm")]
    [InlineData("character:belmakor")]
    public async Task Get_IncludeSheet_AnyOtherViewReadsThePublicLineOnly(string perspective)
    {
        await CharacterToolSetup.BelmakorAsync(_s.Server);

        var text = await _s.Call("campaign_get", $$"""{"refs": ["character:belmakor"], "include": ["sheet"], "perspective": "{{perspective}}"}""");

        Assert.EndsWith("\n\n## Sheet\n**Belmakor** (`character:belmakor`) — level 12 Wizard (Bladesinger), High Elf · HP 110/110 (+7 temp) · AC 17\n", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Cole", text, StringComparison.Ordinal);
        Assert.DoesNotContain("War Caster", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("author")]
    [InlineData("party")]
    public async Task Get_IncludeSheetForACharacterWithNoSheet_HasNoSheetSection(string perspective)
    {
        var text = await _s.Call("campaign_get", $$"""{"refs": ["character:belmakor"], "include": ["sheet"], "perspective": "{{perspective}}"}""");

        Assert.DoesNotContain("Sheet", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_IncludeSheetForAnNpcWithASheet_TheAuthorReadsItAndThePartyReadsNoSection()
    {
        await _s.Call("campaign_write", """{"ops": [{"op": "upsert", "kind": "character", "name": "Iron Guts", "subtype": "npc", "visibility": "party"}]}""");
        await _s.Call("campaign_character", """{"action": "update", "character": "character:iron-guts", "sheet": {"level": 5, "max_hp": 40, "ac": 16}}""");

        var author = await _s.Call("campaign_get", """{"refs": ["character:iron-guts"], "include": ["sheet"]}""");
        var party = await _s.Call("campaign_get", """{"refs": ["character:iron-guts"], "include": ["sheet"], "perspective": "party"}""");

        Assert.Contains("\n## Sheet\nlevel 5, 2014\n", author, StringComparison.Ordinal);
        Assert.DoesNotContain("Sheet", party, StringComparison.Ordinal);
        Assert.DoesNotContain("HP", party, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_IncludeSheetOnAnEntryThatIsNoCharacter_AddsNothing()
    {
        await _s.Call("campaign_write", """{"ops": [{"op": "upsert", "kind": "location", "name": "Flotsam", "visibility": "party"}]}""");

        var withSheet = await _s.Call("campaign_get", """{"refs": ["location:flotsam"], "include": ["sheet"]}""");

        Assert.Equal(await _s.Call("campaign_get", """{"refs": ["location:flotsam"], "include": []}"""), withSheet);
    }

    [Fact]
    public async Task Get_IncludeSheetWithABodyLongerThanAResult_SaysIncludeEmptyMakesRoom()
    {
        // The sheet is an included section like relations and facts: when the body is cut beside it, the note says
        // include [] makes room (the body is shown whole only without the sections).
        await CharacterToolSetup.BelmakorAsync(_s.Server);
        var body = string.Join("\\n", Enumerable.Range(1, 800).Select(i => $"Line {i:D3} of the long life, one paragraph after another."));
        await _s.Call("campaign_write", $$"""{"ops": [{"op": "upsert", "ref": "character:belmakor", "body_md": "{{body}}"}]}""");

        var withSheet = await _s.Call("campaign_get", """{"refs": ["character:belmakor"], "include": ["sheet"], "detail": "full"}""");
        var bare = await _s.Call("campaign_get", """{"refs": ["character:belmakor"], "include": [], "detail": "full"}""");

        Assert.Contains("\n## Sheet\n", withSheet, StringComparison.Ordinal);
        Assert.Matches(new Regex(
            "\n\n_… [\\d,]+ more characters that do not fit in one result beside the rest of this entry; include \\[\\] leaves out the relations, " +
            "facts and other sections to make room\\._\n"), withSheet);
        Assert.Matches(new Regex("\n\n_… [\\d,]+ more characters that do not fit in one result\\._\n"), bare);
    }

    [Fact]
    public async Task Get_IncludeSheetAsOfASession_IsTheSheetAsItStoodThen()
    {
        await _s.Call("campaign_session", """{"action": "record_past", "session": 1, "played_on": "2026-08-01"}""");
        await _s.Call("campaign_character", """{"action": "update", "session": 1, "sheet": {"level": 11, "max_hp": 100, "ac": 17}}""");
        await _s.Call("campaign_session", """{"action": "record_past", "session": 2, "played_on": "2026-08-08"}""");
        await _s.Call("campaign_character", """{"action": "damage", "session": 2, "amount": 30}""");
        await _s.Call("campaign_character", """{"action": "update", "session": 2, "sheet": {"level": 12}}""");

        var then = await _s.Call("campaign_get", """{"refs": ["character:belmakor"], "include": ["sheet"], "as_of_session": 1}""");
        var now = await _s.Call("campaign_get", """{"refs": ["character:belmakor"], "include": ["sheet"]}""");

        Assert.Contains("\n## Sheet\nlevel 11, 2014\n", then, StringComparison.Ordinal);
        Assert.Contains("HP 100/100 · AC 17", then, StringComparison.Ordinal);
        Assert.Contains("\n## Sheet\nlevel 12, 2014\n", now, StringComparison.Ordinal);
        Assert.Contains("HP 70/100 · AC 17", now, StringComparison.Ordinal);
    }
}
