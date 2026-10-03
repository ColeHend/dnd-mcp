using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: understand-onepiece.md §3 rows 1-6, the seal gate's forbidden vocabulary before the axe is assembled, as
/// <c>campaign_knowledge check</c> reports it on the world the tools built: "seal" and its inflections (seals, Sealed) in a
/// table-facing line are one forbidden flag each, naming the gated fact, the fact that lifts the rule and the words to say
/// instead; the preferred words themselves, "sealskin", and the author are never flagged; Nadar's read-aloud "Not seal
/// it" is flagged too (the server retrieves, the author judges the referent).
///
/// <para>
/// Why it fails silently: the vocabulary is stored inside a fact op's gate; if <c>campaign_write</c> dropped a gate key,
/// or the check read another perspective's rules, every NPC line would "pass" and the twist would leak in the prep.
/// </para>
/// </summary>
public sealed class ScenarioOnePieceVocabularyTests : IClassFixture<ScenarioOnePieceBaseline>
{
    private readonly ScenarioOnePieceWorld _w;

    public ScenarioOnePieceVocabularyTests(ScenarioOnePieceBaseline baseline)
    {
        _w = baseline.World;
    }

    /// <summary>Rows 1, 2 and 5: each form of the word is one forbidden flag, with the rule's source, its lifting fact and its preferred terms.</summary>
    [Theory]
    [InlineData(1, "The Peaceful One turns the fruit: \"See the white lines? That's the seal on it.\"", "\"seal\"")]
    [InlineData(2, "The seals are breaking.", "\"seals\" (matches \"seal\")")]
    [InlineData(2, "Sealed tight.", "\"Sealed\" (matches \"seal\")")]
    [InlineData(5, "Not bind it. Not seal it. Not stash it somewhere clever and call that a victory.", "\"seal\"")]
    public async Task Check_Rows1And2And5TheWordOrAnInflectionBeforeTheAxe_IsOneForbiddenFlag(int row, string text, string matched)
    {
        var result = await Check(text, "table");

        var forbidden = result.Split('\n').Where(l => l.StartsWith("- **forbidden**", StringComparison.Ordinal)).ToList();
        Assert.True(forbidden.Count == 1, $"row {row}:\n{result}");
        Assert.Equal($"- **forbidden** {matched}: {_w.Seal} forbids it; lifts when {_w.Axe} is in play; say instead: \"shell\", \"wrapping\", \"what keeps it in\"; " +
                     $"note: {ScenarioOnePieceWorld.GateNote}.", forbidden[0]);
    }

    /// <summary>Rows 3 and 4: the preferred words, and a word that only starts with the forbidden one, are not flagged.</summary>
    [Theory]
    [InlineData(3, "The white lines are a shell — what keeps it in.")]
    [InlineData(4, "a sealskin coat")]
    public async Task Check_Rows3And4ThePreferredWordsOrSealskin_AreNotFlagged(int row, string text)
    {
        var result = await Check(text, "table");

        Assert.True(!result.Contains("**forbidden**", StringComparison.Ordinal), $"row {row}:\n{result}");
    }

    /// <summary>Row 6: the author may use the word: no forbidden flag for the author's own text.</summary>
    [Fact]
    public async Task Check_Row6TheSameLineAsTheAuthor_IsNotFlagged()
    {
        var result = await Check("The Peaceful One turns the fruit: \"See the white lines? That's the seal on it.\"", perspective: null);

        Assert.StartsWith("# Knowledge check: author view: nothing to flag — pass perspective to check a character's, the party's or the public's knowledge (one-piece)\n\nSpeaker: author.\n", result, StringComparison.Ordinal);
    }

    private Task<string> Check(string text, string? perspective) => _w.Call("campaign_knowledge", $$"""
        {"campaign": "one-piece", "action": "check", "text": {{System.Text.Json.JsonSerializer.Serialize(text)}}{{(perspective is null ? string.Empty : $", \"perspective\": \"{perspective}\"")}}}
        """);
}

/// <summary>
/// Invariant: understand-onepiece.md §3 rows 7 and 8: the seal's vocabulary rule lifts when the axe is assembled (T4,
/// played live in session 11), not when the secret is revealed, and a point-in-time check as of a session before that
/// is flagged again. "The forbidden word 'seal' flagged before the axe and not after", through the tools.
///
/// <para>
/// Why it fails silently: a rule that lifted with the reveal instead would keep flagging every prep line after the axe,
/// and one that ignored as_of_session would clear a flashback scene set before it.
/// </para>
/// </summary>
public sealed class ScenarioOnePieceVocabularyAfterTheAxeTests : IAsyncLifetime
{
    private const string Line = "The Peaceful One turns the fruit: \"See the white lines? That's the seal on it.\"";

    private readonly McpServerHarness _server = new();
    private ScenarioOnePieceWorld _w = null!;

    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        _w = await ScenarioOnePieceWorld.BuildAsync(_server);
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    /// <summary>Rows 1 and 7: the same table-facing line is flagged before T4 and not after it, with the secret still unrevealed.</summary>
    [Fact]
    public async Task Check_Rows1And7TheSameLine_IsFlaggedBeforeTheAxeAndNotAfter()
    {
        var before = await Check(asOf: null);
        await _w.ThroughT4();

        var after = await Check(asOf: null);

        Assert.Contains($"\n- **forbidden** \"seal\": {_w.Seal} forbids it; lifts when {_w.Axe} is in play;", before, StringComparison.Ordinal);
        Assert.DoesNotContain("**forbidden**", after, StringComparison.Ordinal);
        Assert.Contains("\nsecret · status partial\n", await _w.SecretPage(), StringComparison.Ordinal);
    }

    /// <summary>Row 8: after T4, the check as of session 10 (before the axe) is flagged again; as of session 11 it is not.</summary>
    [Fact]
    public async Task Check_Row8AfterT4AsOfSession10_IsFlaggedAgainAndAsOf11IsNot()
    {
        await _w.ThroughT4();

        var asOf10 = await Check(asOf: 10);
        var asOf11 = await Check(asOf: 11);

        Assert.StartsWith("# Knowledge check: ", asOf10, StringComparison.Ordinal);
        Assert.Contains("\nSpeaker: table · as of session 10.\n", asOf10, StringComparison.Ordinal);
        Assert.Contains($"\n- **forbidden** \"seal\": {_w.Seal} forbids it; lifts when {_w.Axe} is in play;", asOf10, StringComparison.Ordinal);
        Assert.DoesNotContain("**forbidden**", asOf11, StringComparison.Ordinal);
    }

    private Task<string> Check(int? asOf) => _w.Call("campaign_knowledge", $$"""
        {"campaign": "one-piece", "action": "check", "perspective": "table", "text": {{System.Text.Json.JsonSerializer.Serialize(Line)}}{{(asOf is { } n ? $", \"as_of_session\": {n}" : string.Empty)}}}
        """);
}
