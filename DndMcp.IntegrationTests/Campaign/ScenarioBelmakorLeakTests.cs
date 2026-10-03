using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: understand-belmakor.md §3's leak rule (its preamble and rows 20-35), read through the tools and the resource
/// the model reads, on the Belmakor world built through the tools themselves (<see cref="ScenarioBelmakorWorld"/>). For
/// every non-author perspective (dm, table, party, public, character:belmakor, character:serif), nothing that
/// <c>campaign_search</c> (ordinary words, secret words, and the two mixed, where the any-word retry runs),
/// <c>campaign_get</c> (pages and refusals alike), <c>campaign</c> (summary, get), <c>campaign_session</c> (list, get),
/// the <c>campaign://belmakor/knowledge/&lt;perspective&gt;</c> resource or the perspective's column of
/// <c>campaign_knowledge ledger</c> returns contains "Keras", "Axiom", "Cage", "Baal", "Third Silence" or "one-piece"
/// (<see cref="ScenarioLeak.ForbiddenFor"/>: nor the author's words about the import, nor any other author-only text of
/// the world, nor, but for Belmakor and the dm, his ambition), nor the true name or author ref of anything it is shown
/// only by an <c>e:&lt;n&gt;</c> ref (<see cref="ScenarioReach.HiddenNamesAsync"/>), the dm meeting "Keras" only inside
/// the text of the one fact she was told; every view that knows them names the two things "the old king" and "the thing
/// he wants"; and the cross-link between the old king and One Piece's Keras is shown to no player view of either
/// campaign (rows 30-33).
///
/// <para>
/// Why it fails silently: the repository readers are proven against worlds seeded behind the tools' back; here every
/// alias visibility, known_as, attendance row and cross-link was written by the tools a model uses, and every result is
/// the text the model reads. A write tool that stored the author alias as party-visible, or a formatter that printed a
/// relation's far end by its true name, passes every repository test and fails here; so does a probe widened for the dm
/// (<see cref="ScenarioLeakTests"/>).
/// </para>
/// </summary>
public sealed class ScenarioBelmakorLeakTests : IClassFixture<ScenarioBelmakorWorld>
{
    /// <summary>The non-author perspectives the leak test sweeps.</summary>
    public static readonly IReadOnlyList<string> Perspectives = ["dm", "table", "party", "public", "character:belmakor", "character:serif"];

    /// <summary>The views that know the old king and the thing he wants: all but the public.</summary>
    public static readonly IReadOnlyList<string> PartySide = ["dm", "table", "party", "character:belmakor", "character:serif"];

    // Words a player view may type that are no secret: the sweep reads everything each one finds.
    private static readonly string[] OrdinaryQueries =
        ["old king", "thing he wants", "sorcerer king", "errand", "king", "sorcer*", "Belmakor", "Tristan", "statue", "level", "the thing"];

    // Ordinary words beside a secret one: nothing has every word, so the any-word retry answers with what the ordinary
    // words find, and snippets are built for hits whose visible text lacks the secret word (where a snippet that quoted a
    // column the view cannot see would show it).
    private static readonly string[] MixedQueries = ["errand Axiom", "old king Cage", "thing he wants Cage", "errand Cole"];

    // The ordinary words the mixed-query test puts a secret word beside.
    private static readonly string[] OrdinaryParts = ["errand", "old king", "thing he wants"];

    // The rows of the ledger each view's column is read from: every entity the goldens name, and (linked) every fact.
    private const string LedgerAbout =
        "\"character:old-king\", \"item:thing-he-wants\", \"thread:old-kings-errand\", \"secret:belmakors-ambition\", \"character:belmakor\", \"character:tristan\"";

    private readonly ScenarioBelmakorWorld _w;

    public ScenarioBelmakorLeakTests(ScenarioBelmakorWorld world)
    {
        _w = world;
    }

    /// <summary><see cref="Perspectives"/> as theory data.</summary>
    public static TheoryData<string> AllPerspectives() => new(Perspectives);

    /// <summary><see cref="PartySide"/> as theory data.</summary>
    public static TheoryData<string> PartySideViews() => new(PartySide);

    /// <summary>
    /// Rows 20, 21, 22, 26 and 28 for every perspective: the words that live only in author aliases, secret text, the other
    /// campaign and (for all but Belmakor and the dm) the restricted ambition find nothing, and the result reads word for
    /// word like a search for words that exist nowhere: no "hidden result" count or wording (row 20).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPerspectives))]
    public async Task Search_Rows20To22And26And28TheSecretWords_FindNothingAndReadLikeWordsThatExistNowhere(string perspective)
    {
        var nothing = ScenarioLeak.SearchBody(await Search(perspective, "Zzyzx Qwerty"));

        Assert.EndsWith("\n\nNothing matches. Try fewer or different words, a prefix such as \"sorcer*\", or leave query out to list by kinds.\n", nothing,
            StringComparison.Ordinal);
        foreach (var word in SecretWords(perspective))
        {
            var body = ScenarioLeak.SearchBody(await Search(perspective, word));
            Assert.True(body == nothing, $"\"{word}\" as {perspective} found something:\n{body}");
        }
    }

    /// <summary>
    /// Rows 20-22, 26 and 28 where the any-word retry runs: a secret word beside ordinary ones ("errand Axiom") finds what
    /// the ordinary words find, through the same retry, with the same snippets and order, word for word as a nonsense
    /// word beside them ("errand Zzyzx"), for every view. A secret word alone finds nothing, so only a mixed query makes
    /// the search build a snippet for a hit whose secret text holds the word (the errand thread's names the Cage), and
    /// only a mixed query can rank by a column the view cannot see.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPerspectives))]
    public async Task Search_Rows20To22And26And28ASecretWordBesideOrdinaryOnes_ReadsLikeANonsenseWordBesideThem(string perspective)
    {
        foreach (var ordinary in OrdinaryParts)
        {
            var nonsense = ScenarioLeak.SearchBody(await Search(perspective, ordinary + " Zzyzx"));
            Assert.True(perspective == "public" || nonsense.Contains("\n_Nothing has every word, so these match any of them._\n\n## Entities\n", StringComparison.Ordinal),
                $"\"{ordinary} Zzyzx\" as {perspective} ran no any-word retry:\n{nonsense}");
            foreach (var word in SecretWords(perspective))
            {
                var body = ScenarioLeak.SearchBody(await Search(perspective, $"{ordinary} {word}"));
                Assert.True(body == nonsense, $"\"{ordinary} {word}\" as {perspective} reads otherwise than \"{ordinary} Zzyzx\":\n{body}");
            }
        }
    }

    /// <summary>
    /// Row 27, pinned as the design answers it (contract §13): the dm finds the one fact she was told, which names him,
    /// but never the old king through his author alias "Keras" (no per-knower alias resolution).
    /// </summary>
    [Fact]
    public async Task Search_Row27KerasAsTheDm_FindsOnlyTheFactSheWasToldNeverTheOldKing()
    {
        var body = ScenarioLeak.SearchBody(await Search("dm", "Keras"));

        Assert.Equal(
            "_Perspective: dm. Names are the ones this view knows; author-only text is withheld._\n\n" +
            $"## Facts\n1. `{_w.NameFact}`: {ScenarioBelmakorWorld.NameStatement}\n\n1 result.\n" +
            "campaign_get {\"campaign\": \"belmakor\", \"refs\": [...], \"perspective\": \"dm\"} reads any of these in full.\n",
            body);
    }

    /// <summary>Row 23, the teeth of rows 20-22: the author's own search finds the item by its hidden alias, the thread by its secret text and the fact.</summary>
    [Fact]
    public async Task Search_Row23AxiomCageAsTheAuthor_FindsTheItemTheThreadAndTheFact()
    {
        var text = await _w.Call("campaign_search", """{"campaign": "belmakor", "query": "Axiom Cage"}""");

        Assert.Contains("\n1. **The thing the old king wants** · item · `item:thing-he-wants`\n   It is the Axiom Cage (Cole-tier). _(secret)_\n", text, StringComparison.Ordinal);
        Assert.Contains("\n2. **The old king's errand** · thread · open · `thread:old-kings-errand`\n   Cole-tier: it's the Axiom Cage. _(secret)_\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n3. `{_w.AxiomFact}`: The thing the old king wants fetched is the Axiom Cage.\n", text, StringComparison.Ordinal);
    }

    /// <summary>Row 24: Belmakor's "old king" puts the old king first, under the name the table uses and his own handle.</summary>
    [Fact]
    public async Task Search_Row24OldKingAsBelmakor_PutsTheOldKingFirstUnderTheNameHeKnows()
    {
        var text = await Search("character:belmakor", "old king");

        Assert.Contains("\n## Entities\n1. **The Old King** · character · unknown · `character:old-king`\n", text, StringComparison.Ordinal);
        ScenarioLeak.AssertBelmakorClean(ScenarioLeak.SearchBody(text), "character:belmakor", _w.NameFact, "row 24");
    }

    /// <summary>Row 25: the party finds the old king by its own alias for him, "the sorcerer king".</summary>
    [Fact]
    public async Task Search_Row25SorcererKingAsTheParty_FindsTheOldKing()
    {
        var text = await Search("party", "sorcerer king");

        Assert.Contains("\n1. **The Old King** · character · unknown · `character:old-king`\n", text, StringComparison.Ordinal);
    }

    /// <summary>Row 29: Belmakor alone (the restricted secret and fact name him as a knower) finds his own ambition.</summary>
    [Fact]
    public async Task Search_Row29ReclaimSurfaceAsBelmakor_FindsHisAmbitionAndTheFact()
    {
        var text = await Search("character:belmakor", "reclaim surface");

        Assert.Contains("**Belmakor's ambition** · secret · hidden · `secret:belmakors-ambition`\n", text, StringComparison.Ordinal);
        Assert.Contains($"`{_w.AmbitionFact}`: {ScenarioBelmakorWorld.AmbitionStatement}\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The leak test proper: every read a perspective can make through the tools (<see cref="ScenarioReach.ReadAllAsync"/>:
    /// the listing, a search for each ordinary word and each mixed query, a get of every handle in the campaign with every
    /// include in full, whether it opens or is refused, the summary and the campaign record, the session list and each
    /// session, the knowledge resource; and its ledger column) contains nothing forbidden to it, and nothing that names
    /// what it is shown by an <c>e:&lt;n&gt;</c> ref: its author ref, nor (the disguise) its true name. For every
    /// party-side view that derivation has teeth: it holds the item's true name and ref.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPerspectives))]
    public async Task Read_EveryCallAPerspectiveMakes_ContainsNothingForbiddenToIt(string perspective)
    {
        var outputs = await ReachAsync(perspective);
        var hidden = await ScenarioReach.HiddenNamesAsync(_w.Server, "belmakor", perspective, outputs.Select(o => o.Text));

        foreach (var (label, text) in outputs)
        {
            ScenarioLeak.AssertBelmakorClean(text, perspective, _w.NameFact, $"{label} as {perspective}");
            ScenarioLeak.AssertClean(text, hidden, $"{label} as {perspective} (the author ref or true name of something it is shown by e:<n>)");
        }

        var opened = outputs.Count(o => o.Label.StartsWith("get ", StringComparison.Ordinal) && !o.Text.StartsWith("An error occurred", StringComparison.Ordinal));
        Assert.True(perspective == "public" ? opened == 0 : opened >= 8, $"{perspective} opened {opened} entries");
        Assert.True(perspective == "public" || (hidden.Contains("The thing the old king wants") && hidden.Contains("item:thing-he-wants")),
            $"{perspective} derived only: {string.Join(", ", hidden)}");
    }

    /// <summary>
    /// The sweep has teeth: every string it forbids the other views is in the file for the author to read, through the
    /// same reads (the alias, the secret texts, the import notes, the cross-links, the ambition, the item's true name and
    /// ref, the author's sections, settings, sources, rule, attendance notes and batch reasons in Belmakor's author view;
    /// "Baal", "The Third Silence" and "One Piece" in the other campaign the cross-links point at). Were one missing (an op
    /// the tools refused, a field one of them dropped), the sweep would pass without testing that string.
    /// </summary>
    [Fact]
    public async Task Read_EveryCallAsTheAuthor_ShowsEveryStringTheSweepForbidsTheOtherViews()
    {
        var author = string.Join("\n", (await ReachAsync("author")).Select(o => o.Text));
        var other = await _w.Call("campaign", """{"action": "list"}""") +
                    await _w.Call("campaign_get", """{"campaign": "one-piece", "refs": ["character:keras", "item:axiom-cage"], "detail": "full"}""");

        foreach (var word in ScenarioLeak.AlwaysForbidden.Concat(ScenarioLeak.AmbitionWords))
        {
            var where = word is "Baal" or "Third Silence" or "One Piece" ? other : author;
            Assert.True(where.Contains(word, StringComparison.OrdinalIgnoreCase), $"\"{word}\" is nowhere the author reads");
        }
    }

    /// <summary>
    /// The dm's exception is real and narrow: she reads "Keras" in f:2 (row 7) wherever it is listed, and nowhere else;
    /// the sweep above passes for her only because <see cref="ScenarioLeak.WithoutToldText"/> takes out exactly that text.
    /// </summary>
    [Fact]
    public async Task Read_Row7EveryCallAsTheDm_ShowsTheNameOnlyInTheFactSheWasTold()
    {
        var outputs = await ReachAsync("dm");

        var withName = outputs.Where(o => o.Text.Contains("Keras", StringComparison.Ordinal)).Select(o => o.Label).ToList();
        Assert.Contains($"get {_w.NameFact}", withName);
        Assert.Contains("get character:old-king", withName);
        Assert.Contains("knowledge resource", withName);
        Assert.All(outputs.Where(o => o.Text.Contains("Keras", StringComparison.Ordinal)),
            o => Assert.Contains(ScenarioBelmakorWorld.NameStatement, o.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// Rows 2, 3, 5 and 10 in every reader: a view that knows them names the old king by his table name (never "Keras")
    /// and the item only as "the thing he wants" under its <c>e:&lt;n&gt;</c> ref (its true name and slug disguised), in
    /// the knowledge resource, a search, the old king's page and its relation, the item's own page, and the ledger.
    /// </summary>
    [Theory]
    [MemberData(nameof(PartySideViews))]
    public async Task Read_Rows2To5And10AViewThatKnowsThem_NamesTheOldKingAndTheThingHeWants(string perspective)
    {
        var thing = _w.ThingRef;

        var knowledge = await _w.Read($"campaign://belmakor/knowledge/{perspective}");
        Assert.Contains("\n- The Old King (`character:old-king`) · character · met · S3\n", knowledge, StringComparison.Ordinal);
        Assert.Contains($"\n- the thing he wants (`{thing}`) · item · aware · S3\n", knowledge, StringComparison.Ordinal);

        Assert.Contains($"\n1. **the thing he wants** · item · `{thing}`\n", await Search(perspective, "thing he wants"), StringComparison.Ordinal);

        var king = await Get(perspective, "character:old-king");
        Assert.StartsWith("# The Old King (`character:old-king`)\n", king, StringComparison.Ordinal);
        Assert.Contains($"\n- wants → the thing he wants (`{thing}`) · current\n", king, StringComparison.Ordinal);

        var item = await Get(perspective, thing);
        Assert.StartsWith($"# the thing he wants (`{thing}`)\n", item, StringComparison.Ordinal);

        var cells = ScenarioLeak.LedgerCells(await Ledger(perspective));
        Assert.Equal(("met as “the old king” (S3)", "aware as “the thing he wants” (S3)"), (cells["character:old-king"], cells["item:thing-he-wants"]));

        var summary = await _w.Call("campaign", $$"""{"action": "summary", "campaign": "belmakor", "perspective": "{{perspective}}"}""");
        Assert.Contains("\n## Open quests and threads\n- The old king's errand (`thread:old-kings-errand`) · open\n", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The disguised ref is the item's own: the author's get of the <c>e:&lt;n&gt;</c> the other views are shown opens
    /// "The thing the old king wants" (so the views above are shown the item, not some other entity under its name).
    /// </summary>
    [Fact]
    public async Task Get_TheRefTheViewsAreShownForTheItem_IsTheItemForTheAuthor()
    {
        var text = await _w.Call("campaign_get", $$"""{"campaign": "belmakor", "refs": ["{{_w.ThingRef}}"], "include": []}""");

        Assert.StartsWith($"# The thing the old king wants (`item:thing-he-wants` · `{_w.ThingRef}`)\n", text, StringComparison.Ordinal);
    }

    /// <summary>Rows 5 and 16 for the public: it knows neither thing by any name, and every reader says so without a trace of what exists.</summary>
    [Fact]
    public async Task Read_Rows5And16AsThePublic_KnowsNeitherTheOldKingNorTheThingHeWants()
    {
        Assert.Equal(
            "# Belmakor — sky-world: what public knows\n_Perspective: public. Names are the ones this view knows; author-only text is withheld._\n\n" +
            "Nothing is recorded yet.\n",
            await _w.Read("campaign://belmakor/knowledge/public"));
        var cells = ScenarioLeak.LedgerCells(await Ledger("public"));
        Assert.Equal(("not met", "not met"), (cells["character:old-king"], cells["item:thing-he-wants"]));
        Assert.EndsWith("\n\nNothing matches these filters.\n", await _w.Call("campaign_search", """{"campaign": "belmakor", "perspective": "public"}"""),
            StringComparison.Ordinal);
        Assert.Equal(
            "An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"character:old-king\": nothing by that handle for this perspective. " +
            "campaign_search finds entities and facts by name.",
            await _w.Fail("campaign_get", """{"campaign": "belmakor", "perspective": "public", "refs": ["character:old-king"]}"""));
    }

    /// <summary>
    /// Row 30: Belmakor's page of the old king: his table name, only the party alias, the errand facts (f:3, f:4), his own
    /// verdict "met as the old king"; never f:2, the secret text, the author alias or the cross-link to the other campaign.
    /// </summary>
    [Fact]
    public async Task Get_Row30TheOldKingAsBelmakor_ShowsThePartyAliasAndErrandFactsButNoNameFactSecretOrCrossLink()
    {
        var text = await Get("character:belmakor", "character:old-king", """["relations", "facts", "knowledge"]""");

        Assert.StartsWith(
            "# The Old King (`character:old-king`)\n" +
            "_Perspective: character:belmakor (Belmakor Silverwind). Names are the ones this view knows; author-only text is withheld._\n" +
            "character · npc · status unknown\n" +
            "> Ancient, Void-controlled sorcerer king; revealed by a crumbling statue; fought and beaten; sent the party on an errand.\n" +
            "- **Aliases:** the sorcerer king\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n## Facts\n- `{_w.ErrandFact}`: After the fight the old king sent the party", text, StringComparison.Ordinal);
        Assert.Contains($"\n- `{_w.TouchFact}`: The party took \"don't use it\" as \"don't even touch it.\"\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- character:old-king · character:belmakor: knows (met) as “the old king” · S3 — ", text, StringComparison.Ordinal);
        Assert.DoesNotContain($"`{_w.NameFact}`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("same as", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Author", text, StringComparison.Ordinal);
        ScenarioLeak.AssertBelmakorClean(text, "character:belmakor", _w.NameFact, "row 30");
    }

    /// <summary>
    /// Row 31, the teeth of row 30: the author's page of the old king has the author alias, the secret text, the fact that
    /// names him, the author's own name for him and the cross-link to One Piece's Keras.
    /// </summary>
    [Fact]
    public async Task Get_Row31TheOldKingAsTheAuthor_ShowsTheAliasTheSecretTheNameFactAndTheCrossLink()
    {
        var text = await _w.Call("campaign_get", """{"campaign": "belmakor", "refs": ["character:old-king"], "include": ["relations", "facts", "knowledge"], "detail": "full"}""");

        Assert.Contains("\n- **Aliases:** Keras (author), the sorcerer king (party)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n> [!secret]\n> Keras, Cole's old PC from his other campaign, imported at Cole's request; Cole ran him in the fight.\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n- `{_w.NameFact}`: {ScenarioBelmakorWorld.NameStatement}\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- character:old-king · author: knows as “Keras” · backstory\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- same as `one-piece/character:keras` (Keras, campaign one-piece): Same being: Cole's old PC, imported as the Void-controlled sorcerer king\n",
            text, StringComparison.Ordinal);
    }

    /// <summary>Row 32: no other view is shown the cross-link (or any author section); the public cannot open the page at all.</summary>
    [Theory]
    [InlineData("dm")]
    [InlineData("table")]
    [InlineData("party")]
    [InlineData("character:serif")]
    public async Task Get_Row32TheOldKingAsAnotherView_NeverShowsTheCrossLink(string perspective)
    {
        var text = await Get(perspective, "character:old-king", """["relations", "facts", "knowledge", "children", "sessions", "history"]""");

        Assert.StartsWith("# The Old King (`character:old-king`)\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("same as", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Author", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## History", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 33, the firewall the other way: One Piece's party and table open its Keras (party-visible there, a DM campaign)
    /// and are shown no cross-link to Belmakor's old king, no author section, no history and none of the author's words
    /// about the import; One Piece's author is shown the cross-link (the teeth: it is there to leak).
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    public async Task Get_Row33KerasAsAOnePiecePlayerView_HasNoCrossLinkToBelmakor(string perspective)
    {
        var text = await _w.Call("campaign_get",
            $$"""{"campaign": "one-piece", "perspective": "{{perspective}}", "refs": ["character:keras"], "include": {{ScenarioReach.AllIncludes}}, "detail": "full"}""");
        var author = await _w.Call("campaign_get", """{"campaign": "one-piece", "refs": ["character:keras"], "include": ["relations"], "detail": "full"}""");

        Assert.StartsWith("# Keras (`character:keras`)\n", text, StringComparison.Ordinal);
        ScenarioLeak.AssertClean(text, ["same as", "belmakor", "old king", "## Author", "## History", "Cole", "imported"], $"row 33: one-piece's Keras as {perspective}");
        Assert.Contains("\n- same as `belmakor/character:old-king` (The Old King, campaign belmakor): ", author, StringComparison.Ordinal);
    }

    /// <summary>Row 34: Belmakor's knowledge page names both things as he knows them, lists his own ambition, and nothing forbidden to him.</summary>
    [Fact]
    public async Task ReadResource_Row34BelmakorsKnowledge_NamesTheOldKingAndTheThingHeWantsAndNothingForbidden()
    {
        var text = await _w.Read("campaign://belmakor/knowledge/character:belmakor");

        Assert.StartsWith(
            "# Belmakor — sky-world: what character:belmakor knows\n" +
            "_Perspective: character:belmakor (Belmakor Silverwind). Names are the ones this view knows; author-only text is withheld._\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- The Old King (`character:old-king`) · character · met · S3\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n- the thing he wants (`{_w.ThingRef}`) · item · aware · S3\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n- `{_w.AmbitionFact}`: {ScenarioBelmakorWorld.AmbitionStatement} · knows\n", text, StringComparison.Ordinal);
        ScenarioLeak.AssertBelmakorClean(text, "character:belmakor", _w.NameFact, "row 34");
    }

    /// <summary>
    /// Row 35: the author's page of Belmakor shows level 12 as canon and level 11 only as superseded by it, and the level-11
    /// fact keeps its source (party-and-band.md:19); a player view sees only level 12 (a superseded fact is out of play).
    /// </summary>
    [Fact]
    public async Task Get_Row35BelmakorsLevel_Is12WithLevel11OnlySupersededAndSourced()
    {
        var author = await _w.Call("campaign_get", """{"campaign": "belmakor", "refs": ["character:belmakor"], "include": ["facts"]}""");
        var level11 = await _w.Call("campaign_get", $$"""{"campaign": "belmakor", "refs": ["{{_w.Level11Fact}}"]}""");
        var party = await Get("party", "character:belmakor", """["facts"]""");

        Assert.Contains($"\n- `{_w.Level12Fact}`: Belmakor is level 12 (as of August 2026).\n  type canon · truth true · canon canon · ", author, StringComparison.Ordinal);
        Assert.Contains($"\n- `{_w.Level11Fact}`: Belmakor is level 11.\n  type canon · truth true · canon superseded · confidence approximate · visibility party · " +
                        $"superseded by `{_w.Level12Fact}` · ", author, StringComparison.Ordinal);
        Assert.Contains("\n- source: party-and-band.md:19\n", level11, StringComparison.Ordinal);
        Assert.Contains($"\n- `{_w.Level12Fact}`: Belmakor is level 12 (as of August 2026).\n", party, StringComparison.Ordinal);
        Assert.DoesNotContain("level 11", party, StringComparison.Ordinal);
    }

    private Task<string> Search(string perspective, string query) =>
        _w.Call("campaign_search", $$"""{"campaign": "belmakor", "perspective": "{{perspective}}", "query": "{{query}}", "limit": 50}""");

    private Task<string> Get(string perspective, string handle, string include = "null") =>
        _w.Call("campaign_get", $$"""{"campaign": "belmakor", "perspective": "{{perspective}}", "refs": ["{{handle}}"], "include": {{include}}}""");

    private Task<string> Ledger(string perspective) =>
        _w.Call("campaign_knowledge", $$"""{"campaign": "belmakor", "action": "ledger", "perspectives": ["{{perspective}}"], "about": [{{LedgerAbout}}]}""");

    /// <summary>
    /// Every read <paramref name="perspective"/> can make of the Belmakor campaign (<see cref="ScenarioReach.ReadAllAsync"/>
    /// with the ordinary and mixed queries, every fact and sessions 1-3), labelled, and each cell of its ledger column.
    /// </summary>
    private async Task<List<(string Label, string Text)>> ReachAsync(string perspective)
    {
        var outputs = await ScenarioReach.ReadAllAsync(_w.Server, "belmakor", perspective, [.. OrdinaryQueries, .. MixedQueries],
            [_w.AxiomFact, _w.NameFact, _w.ErrandFact, _w.TouchFact, _w.AmbitionFact, _w.ContingencyFact, _w.TristanFact, _w.Level11Fact, _w.Level12Fact],
            [1, 2, 3]);
        var gets = outputs.Count(o => o.Label.StartsWith("get ", StringComparison.Ordinal));
        Assert.True(gets >= 27, $"the sweep covers {gets} handles");
        foreach (var (row, cell) in ScenarioLeak.LedgerCells(await Ledger(perspective)))
        {
            outputs.Add(($"ledger cell {row}", cell));
        }

        return outputs;
    }

    // The words that live only where this view may not look (author aliases, secret text, the other campaign, the
    // author's notes; for all but Belmakor and the dm, the restricted ambition), as rows 20-22, 26 and 28 search them.
    private static List<string> SecretWords(string perspective)
    {
        var words = new List<string> { "Axiom Cage", "Axiom", "Cage", "Baal", "Third Silence", "Cole", "imported" };
        if (perspective != "dm")
        {
            words.Add("Keras");
        }

        if (perspective is not ("dm" or "character:belmakor"))
        {
            words.AddRange(["reclaim", "surface", "blighted", "outstrips"]);
        }

        return words;
    }
}
