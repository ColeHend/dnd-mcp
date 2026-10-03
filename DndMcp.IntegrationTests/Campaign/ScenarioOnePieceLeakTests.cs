using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the leak rule (contract §0) in a DM campaign, where the dm is the author and every other view is a
/// player's: on the One Piece world the tools built, at its baseline (as of session 7), nothing the party, the table, the
/// public or Björn reads through the tools (<see cref="ScenarioReach.ReadAllAsync"/>: the listing, searches with the
/// queries of understand-onepiece.md §3 rows 31-35 and 38-39 among them, a get of every handle in the campaign and every
/// fact with every include in full, the summary, the campaign record, the sessions played and one only planned, the
/// knowledge resource) contains an author section (secret callout, author block, history), the secret text of a
/// party-visible entity (G.O.D.S. Co., Q7) or of the Protector, the twist and its words, a fragment's true name, an
/// author-only question, the unplayed session, the campaign's settings, a question's "withheld" status (row 37: it shows
/// as open), or the true name or author ref of anything it is shown by an <c>e:&lt;n&gt;</c> ref (the advisor in Serret,
/// the man napping under the tree).
///
/// <para>
/// Why it fails silently: whether a view is the author's depends on the campaign's role as well as the perspective
/// (contract §3.2: author, or dm in a DM campaign), and the Belmakor world is a player campaign. A reader that asked the
/// role instead of the view, and so handed a DM campaign's author record to every view, passes every Belmakor test; only
/// a DM campaign's player views can show it.
/// </para>
/// </summary>
public sealed class ScenarioOnePieceLeakTests : IClassFixture<ScenarioOnePieceBaseline>
{
    /// <summary>The player views the sweep reads as: the party, the table, the public and one party member.</summary>
    public static readonly IReadOnlyList<string> Perspectives = ["party", "table", "public", "character:bjorn-mountainfell"];

    /// <summary>
    /// What none of <see cref="Perspectives"/> may be shown at the baseline (compared case-insensitively): the author's
    /// sections; the secret text of G.O.D.S. Co., Q7 and the Protector; the twist (the seal, Baal, Keras, the Cage) and
    /// the fragments, the Protector's and the Peaceful One's true names among them (the party knows them only as the
    /// advisor in Serret and the man napping under the tree); the author-only questions and the facts on the old
    /// timeline; session 8, planned and unplayed; the campaign's settings; and Q7's "withheld" status, which a player view
    /// shows as "open" (the banner's own "author-only text is withheld" is the contract's wording, so the status is what
    /// is matched).
    /// </summary>
    public static readonly IReadOnlyList<string> Forbidden =
    [
        "[!secret]", "## Author", "## History", "What changed",
        "simulacrum", "founders", "fleet", "en masse",
        "seal", "Baal", "Keras", "Cage", "fragment", "Protector", "Peaceful", "Mistaken", "Dutiful",
        "lineage", "Sky-world", "return visit", "effective_level_offset", "status withheld",
    ];

    // The words a player may search: rows 31-35, 38 and 39's queries, and the names the party does use.
    private static readonly string[] Queries =
        ["silk isle", "rope fleet", "G.O.D.S. founders", "seal", "moon goddess protecting", "lineage", "protector", "advisor", "napping", "axe", "devil fruit"];

    private readonly ScenarioOnePieceWorld _w;

    /// <summary>Takes the baseline world (its own server: <see cref="ScenarioOnePieceBaseline"/>).</summary>
    public ScenarioOnePieceLeakTests(ScenarioOnePieceBaseline baseline)
    {
        _w = baseline.World;
    }

    /// <summary><see cref="Perspectives"/> as theory data.</summary>
    public static TheoryData<string> PlayerViews() => new(Perspectives);

    /// <summary>
    /// The leak test in a DM campaign: every read a player view makes contains nothing of <see cref="Forbidden"/> and
    /// nothing that names what it is shown by an <c>e:&lt;n&gt;</c> ref; every view but the public opens pages, and the
    /// party side's derivation holds the Protector's true name and ref (the teeth).
    /// </summary>
    [Theory]
    [MemberData(nameof(PlayerViews))]
    public async Task Read_EveryCallAPlayerViewOfADmCampaignMakes_ContainsNothingForbiddenToIt(string perspective)
    {
        var outputs = await ReachAsync(perspective);
        var hidden = await ScenarioReach.HiddenNamesAsync(_w.Server, "one-piece", perspective, outputs.Select(o => o.Text));

        foreach (var (label, text) in outputs)
        {
            ScenarioLeak.AssertClean(text, Forbidden, $"{label} as {perspective}");
            ScenarioLeak.AssertClean(text, hidden, $"{label} as {perspective} (the author ref or true name of something it is shown by e:<n>)");
        }

        var opened = outputs.Count(o => o.Label.StartsWith("get ", StringComparison.Ordinal) && !o.Text.StartsWith("An error occurred", StringComparison.Ordinal));
        Assert.True(perspective == "public" ? opened == 0 : opened >= 12, $"{perspective} opened {opened} entries");
        Assert.True(perspective == "public" || (hidden.Contains("The Protector") && hidden.Contains("character:protector")),
            $"{perspective} derived only: {string.Join(", ", hidden)}");
    }

    /// <summary>
    /// The sweep has teeth: every string it forbids the player views is in the campaign for the author (One Piece's dm)
    /// to read, through the same reads. Were one missing (a secret_md, a gate key or a relation label the tools dropped),
    /// the sweep would pass without testing that string.
    /// </summary>
    [Fact]
    public async Task Read_EveryCallAsTheAuthor_ShowsEveryStringTheSweepForbidsThePlayerViews()
    {
        var author = string.Join("\n", (await ReachAsync("author")).Select(o => o.Text));

        foreach (var word in Forbidden)
        {
            Assert.True(author.Contains(word, StringComparison.OrdinalIgnoreCase), $"\"{word}\" is nowhere the author reads");
        }
    }

    /// <summary>
    /// Row 37: the party's page of Q7 shows the question as open, its body and nothing of the answer the author holds
    /// (its secret text, the lean fact that names the founders), so "withheld" never tells the players there is one.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    public async Task Get_Row37Q7AsAPlayerView_IsOpenWithItsBodyAndNoAnswer(string perspective)
    {
        var text = await _w.Call("campaign_get",
            $$"""{"campaign": "one-piece", "perspective": "{{perspective}}", "refs": ["question:q7"], "include": {{ScenarioReach.AllIncludes}}, "detail": "full"}""");

        Assert.StartsWith("# What destroyed Silk Isle? (`question:q7`)\n", text, StringComparison.Ordinal);
        Assert.Contains("\nquestion · gap · Q7 · status open\n\nHe believes the island destroyed, but keeps finding his people's rope- and weave-work out in the world.\n",
            text, StringComparison.Ordinal);
        ScenarioLeak.AssertClean(text, ["status withheld", "founders", "fleet", "G.O.D.S.", "[!secret]", "## Author"], $"row 37 as {perspective}");
    }

    // Every read the view makes of One Piece: the queries above, every fact the world names, the sessions played (1, 5,
    // 6, 7) and the first planned one (8, unplayed: invisible to a player view).
    private async Task<List<(string Label, string Text)>> ReachAsync(string perspective)
    {
        var outputs = await ScenarioReach.ReadAllAsync(_w.Server, "one-piece", perspective, Queries,
            [_w.Axe, _w.Holds, _w.NoUneatenFruits, _w.TProtector, _w.TPeaceful, _w.TMistaken, _w.TDutiful, _w.ShieldedChild, _w.IllusionRewatched,
             _w.BreachesClimbing, _w.FruitAppearancesFalling, _w.NadarPlan, _w.Seal, _w.TimelineOld, _w.Timeline20260830, _w.Fight500900,
             _w.Fragments400, _w.LineageCovers],
            [1, 5, 6, 7, 8]);
        var gets = outputs.Count(o => o.Label.StartsWith("get ", StringComparison.Ordinal));
        Assert.True(gets >= 40, $"the sweep covers {gets} handles");
        return outputs;
    }
}
