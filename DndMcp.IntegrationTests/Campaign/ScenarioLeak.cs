using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The leak rule (contract §0) as the scenarios check it on the text a tool or resource returns: a non-author view of the
/// Belmakor campaign contains none of understand-belmakor.md §3's forbidden strings ("Keras", "Axiom", "Cage", "Baal",
/// "Third Silence", "one-piece"), nor the author's own words about the import (secret text, attendance notes), nor the
/// item's true name and author ref, nor any other author-only text of the world (<see cref="AuthorOnly"/>), nor, but for
/// Belmakor and the dm, his hidden ambition; and the dm, who was told the one fact that names the old king, meets "Keras"
/// only inside that fact's own text.
///
/// <para>
/// Why the dm's exception is cut this narrowly (the repository scenario's rule, stage3a-X-report): exempting the word
/// for her altogether would let every other way it can leak pass for her (the author alias on the old king's page, a
/// ref, another fact's text, a known_as), which is exactly what the check is for. So only the fact's statement where an
/// output presents it as that fact's own text is taken out (<see cref="WithoutToldText"/>), and <see cref="ScenarioLeakTests"/>
/// pins that nothing wider is.
/// </para>
/// </summary>
internal static class ScenarioLeak
{
    /// <summary>
    /// understand-belmakor.md §3's list: the other campaign's names for the two things, its villain and its slug, which no
    /// non-author view of the Belmakor campaign may contain anywhere (refs, snippets, known_by lists, warnings).
    /// </summary>
    public static readonly IReadOnlyList<string> GoldenForbidden = ["Keras", "Axiom", "Cage", "Baal", "Third Silence", "one-piece"];

    /// <summary>The other campaign's name as a title, and the author's words about the import (the old king's secret text, session 3's attendance note, the cross-links' notes).</summary>
    public static readonly IReadOnlyList<string> ImportWords = ["One Piece", "Cole", "imported"];

    /// <summary>
    /// The item's true name and author ref: every party-side view is shown it only as "the thing he wants" under an
    /// <c>e:&lt;n&gt;</c> ref, and the public not at all (contract §0: "no true name of an entity it knows under another
    /// name (including inside a kind:slug ref)"). Listed outright, beside what the sweep derives from the refs a view is
    /// shown (<see cref="ScenarioReach.HiddenNamesAsync"/>), because a reader that stopped disguising the item altogether
    /// would show no <c>e:&lt;n&gt;</c> ref to derive them from.
    /// </summary>
    public static readonly IReadOnlyList<string> ItemTrueName = ["The thing the old king wants", "item:thing-he-wants"];

    /// <summary>
    /// The author-only text of the Belmakor world that carries none of the words above, so only these catch it reaching a
    /// player view: the headings of the author's own sections (the secret callout, a page's or session's author block, the
    /// history of a page or a session), the campaign's settings, f:8's source, the reveal rule's name and note, the
    /// attendance notes, and the batches' reasons.
    /// </summary>
    public static readonly IReadOnlyList<string> AuthorOnly =
    [
        "[!secret]", "## Author", "## History", "What changed",
        "she/her", "near-TPK", "party-and-band.md", "No name, no timespan", "never learns his name",
        "fliers", "died here", "peeled off", "level drift", "public at the table now", "What sessions 1-3 established",
    ];

    /// <summary>What no non-author view of the Belmakor campaign may contain (compared case-insensitively).</summary>
    public static readonly IReadOnlyList<string> AlwaysForbidden = [.. GoldenForbidden, .. ImportWords, .. ItemTrueName, .. AuthorOnly];

    /// <summary>Belmakor's hidden ambition: only he and the dm (who were both recorded knowing it) may meet these words.</summary>
    public static readonly IReadOnlyList<string> AmbitionWords = ["reclaim", "blighted", "outstrips"];

    /// <summary>The strings <paramref name="perspective"/> must never be shown.</summary>
    public static IReadOnlyList<string> ForbiddenFor(string perspective) =>
        perspective is "dm" or "character:belmakor" ? AlwaysForbidden : [.. AlwaysForbidden, .. AmbitionWords];

    /// <summary>
    /// <paramref name="text"/> with the statement of fact <paramref name="factRef"/> removed where an output presents it as
    /// that fact's own text, and nowhere else: a fact line (<c>`f:2`: statement</c>, in search results, a get's fact list
    /// and the knowledge resource) and the quoted text of a get of the fact itself (<c># Fact `f:2`</c> ... <c>&gt; statement</c>).
    /// </summary>
    public static string WithoutToldText(string text, string factRef, string statement)
    {
        var stripped = text.Replace($"`{factRef}`: {statement}", $"`{factRef}`: ", StringComparison.Ordinal);
        return stripped.StartsWith($"# Fact `{factRef}`\n", StringComparison.Ordinal)
            ? stripped.Replace($"\n> {statement}\n", "\n> \n", StringComparison.Ordinal)
            : stripped;
    }

    /// <summary>
    /// <paramref name="text"/> with a refusal's echo of the handle the caller typed taken out
    /// (<c>refs item 1: "item:thing-he-wants": nothing by that handle</c>): the caller's own words, as a search's title
    /// echoes its query, not something the view was shown. Only that quoted echo in a refusal goes; the handle anywhere
    /// else (a page, a "did you mean") still counts.
    /// </summary>
    public static string WithoutTypedHandle(string text, string handle) =>
        text.StartsWith("An error occurred", StringComparison.Ordinal)
            ? text.Replace($"refs item 1: \"{handle}\": ", "refs item 1: \"…\": ", StringComparison.Ordinal)
            : text;

    /// <summary>Asserts <paramref name="text"/> contains none of <paramref name="forbidden"/> (case-insensitive), naming the output and the word.</summary>
    public static void AssertClean(string text, IEnumerable<string> forbidden, string because)
    {
        foreach (var word in forbidden)
        {
            Assert.True(text.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0, $"{because}: the output contains \"{word}\":\n{text}");
        }
    }

    /// <summary>
    /// The Belmakor check for one output shown to <paramref name="perspective"/>: <see cref="ForbiddenFor"/> after, for the dm,
    /// the told fact's own text (<paramref name="nameFact"/>, "The old king's name is Keras.") is taken out.
    /// </summary>
    public static void AssertBelmakorClean(string text, string perspective, string nameFact, string because)
    {
        var shown = perspective == "dm" ? WithoutToldText(text, nameFact, ScenarioBelmakorWorld.NameStatement) : text;
        AssertClean(shown, ForbiddenFor(perspective), because);
    }

    /// <summary>A search result without its title line, which echoes the caller's own query (the model's words, not the campaign's).</summary>
    public static string SearchBody(string text) => text[(text.IndexOf('\n', StringComparison.Ordinal) + 1)..];

    /// <summary>
    /// The cells of a one-perspective ledger (<c>perspectives: [p]</c>) by row ref (<c>character:old-king</c>, <c>f:3</c>):
    /// the perspective's own column. The row labels are the author's (true names) by design, so a leak check reads only
    /// the cells, which are that perspective's verdicts and the names it uses.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LedgerCells(string ledger) => LedgerTable(ledger).ToDictionary(r => r.Key, r => r.Value[0], StringComparer.Ordinal);

    /// <summary>A ledger's rows by row ref, each with its cells in column order.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LedgerTable(string ledger)
    {
        var rows = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var line in ledger.Split('\n').Where(l => l.StartsWith("| ", StringComparison.Ordinal) && !l.StartsWith("| Row |", StringComparison.Ordinal)))
        {
            var cells = line[2..^2].Split(" | ");
            rows[cells[0].Split(' ')[0].Trim('*')] = cells[1..];
        }

        return rows;
    }
}

/// <summary>
/// Invariant: the leak probe the scenarios rely on is exactly as narrow as its summary says: for the dm, "Keras" passes
/// only inside the told fact's own text; every other way the word can reach her (the author alias on a page, a ref,
/// another fact's text, a fact's page that is not the told one, a knowledge line) still fails; for every other view the
/// told fact's text is not excepted at all; a leak fails in any letter case; and a refusal loses only its echo of the
/// handle the caller typed.
///
/// <para>
/// Why it fails silently: a probe that dropped "Keras" from the dm's list, stripped the statement wherever it appears,
/// compared case-sensitively (a lower-case <c>character:keras</c> ref) or stripped a handle wherever it appears would let
/// the leak tests pass whatever the readers print; the tests that use it could not notice.
/// </para>
/// </summary>
public sealed class ScenarioLeakTests
{
    private const string NameFact = "f:2";

    private const string Refusal =
        "An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"item:thing-he-wants\": nothing by that handle for this perspective. Did you mean e:13?";

    /// <summary>The told fact's own text, as a search, the knowledge resource and its own page present it, passes for the dm.</summary>
    [Theory]
    [InlineData("1. `f:2`: The old king's name is Keras.\n")]
    [InlineData("- `f:2`: The old king's name is Keras. · knows\n")]
    [InlineData("# Fact `f:2`\n_Perspective: dm._\n> The old king's name is Keras.\n- **About:** The Old King (`character:old-king`)\n")]
    public void AssertBelmakorClean_TheToldFactsOwnTextForTheDm_Passes(string text)
    {
        ScenarioLeak.AssertBelmakorClean(text, "dm", NameFact, "probe");
    }

    /// <summary>"Keras" anywhere but the told fact's own text fails for the dm: an alias, a ref, another fact, another fact's page, a known_as, a second mention.</summary>
    [Theory]
    [InlineData("- **Aliases:** the sorcerer king, Keras\n")]
    [InlineData("- wants → Keras (`character:keras`) · current\n")]
    [InlineData("1. `f:3`: The old king's name is Keras.\n")]
    [InlineData("# Fact `f:3`\n_Perspective: dm._\n> The old king's name is Keras.\n")]
    [InlineData("- character:old-king · dm: knows (met) as “Keras” · S3\n")]
    [InlineData("1. `f:2`: The old king's name is Keras. Keras sails tonight.\n")]
    public void AssertBelmakorClean_KerasAnywhereElseForTheDm_Fails(string text)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ScenarioLeak.AssertBelmakorClean(text, "dm", NameFact, "probe"));
    }

    /// <summary>The told fact's text is the dm's exception alone: for every other view it fails.</summary>
    [Theory]
    [InlineData("party")]
    [InlineData("character:belmakor")]
    [InlineData("table")]
    public void AssertBelmakorClean_TheToldFactsTextForAnyOtherView_Fails(string perspective)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            ScenarioLeak.AssertBelmakorClean("1. `f:2`: The old king's name is Keras.\n", perspective, NameFact, "probe"));
    }

    /// <summary>
    /// A leak fails in any letter case: a slug inside a ref is lower case, a name mid-sentence may be, and the true name
    /// and author ref of the disguised item, the author's headings and settings fail as the golden words do.
    /// </summary>
    [Theory]
    [InlineData("- the axiom cage (`e:13`)\n")]
    [InlineData("- same as `character:keras`\n")]
    [InlineData("1. **the third silence** · item · `e:20`\n")]
    [InlineData("- **About:** The thing the old king wants (`item:thing-he-wants`)\n")]
    [InlineData("## author\n- visibility party\n")]
    [InlineData("- **Settings:** `{\"dm_pronouns\":\"she/her\"}`\n")]
    public void AssertBelmakorClean_ALeakInAnyLetterCaseForTheParty_Fails(string text)
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ScenarioLeak.AssertBelmakorClean(text, "party", NameFact, "probe"));
    }

    /// <summary>The ambition is forbidden to every view but Belmakor's and the dm's.</summary>
    [Theory]
    [InlineData("party", "Belmakor intends to reclaim the surface.", false)]
    [InlineData("character:serif", "the blighted surface", false)]
    [InlineData("character:belmakor", "Belmakor intends to reclaim the surface.", true)]
    [InlineData("dm", "Belmakor intends to reclaim the surface.", true)]
    public void AssertBelmakorClean_TheAmbition_PassesOnlyForBelmakorAndTheDm(string perspective, string text, bool passes)
    {
        var failure = Record.Exception(() => ScenarioLeak.AssertBelmakorClean(text, perspective, NameFact, "probe"));

        Assert.Equal(passes, failure is null);
    }

    /// <summary>A refusal loses its echo of the typed handle and nothing else: the "did you mean" stays.</summary>
    [Fact]
    public void WithoutTypedHandle_ARefusalsEchoOfTheTypedHandle_IsTheOnlyTextTakenOut()
    {
        var stripped = ScenarioLeak.WithoutTypedHandle(Refusal, "item:thing-he-wants");

        Assert.Equal(
            "An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"…\": nothing by that handle for this perspective. Did you mean e:13?",
            stripped);
    }

    /// <summary>The handle stays wherever it is not the refusal's echo of what was typed: in a page, in a refusal's suggestion, in a refusal of another handle.</summary>
    [Theory]
    [InlineData("# Fact `f:3`\n- **About:** The thing the old king wants (`item:thing-he-wants`)\n", "item:thing-he-wants")]
    [InlineData("An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"e:99\": nothing by that handle. Did you mean item:thing-he-wants?", "e:99")]
    [InlineData(Refusal, "e:13")]
    public void WithoutTypedHandle_TheHandleAnywhereElse_IsKept(string text, string typed)
    {
        Assert.Contains("item:thing-he-wants", ScenarioLeak.WithoutTypedHandle(text, typed), StringComparison.Ordinal);
    }

    /// <summary>The ledger's parser gives each row's cell of a one-perspective column by its row ref.</summary>
    [Fact]
    public void LedgerCells_AOnePerspectiveLedger_GivesEachRowsCellByRef()
    {
        const string Ledger = "# Knowledge ledger: belmakor\n\nRows use true names (author view).\n\n| Row | party |\n|---|---|\n" +
                              "| **character:old-king** The Old King | met as “the old king” (S3) |\n" +
                              "| f:1 The thing the old king wants fetched is the Axiom Cage. | unaware |\n";

        var cells = ScenarioLeak.LedgerCells(Ledger);

        Assert.Equal(2, cells.Count);
        Assert.Equal("met as “the old king” (S3)", cells["character:old-king"]);
        Assert.Equal("unaware", cells["f:1"]);
    }
}
