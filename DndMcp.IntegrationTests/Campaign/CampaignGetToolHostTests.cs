using System.Text.RegularExpressions;
using DndMcp.Formatting.Campaign;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the host's part of <c>campaign_get</c> around the reader: a blank optional argument means its default, and
/// the "… and N more" hints of a non-author view point only at reads of that same view, of that campaign.
///
/// <para>
/// Why it fails silently: models send "" for "not this one", so a blank detail refused instead of meaning concise costs a
/// retry with no hint why; and a hint without the perspective reads like a helpful pointer while it sends a model drafting
/// for the party to the author's view (campaign_get without a perspective is the author's): followed on a disguised
/// entity, it prints the true name and the secret text. Nothing in the party's own output shows either fault.
/// </para>
/// </summary>
public sealed partial class CampaignGetToolHostTests : IAsyncLifetime
{
    private CampaignTestServer _s = null!;

    public async Task InitializeAsync() => _s = await CampaignTestServer.StartAsync();

    public async Task DisposeAsync() => await _s.DisposeAsync();

    [GeneratedRegex("_… and \\d+ more; read the other end with campaign_get (\\{[^}]*\\})\\._")]
    private static partial Regex RelationsHintRegex();

    // Every "… and N more" line of a result: the hint after the count.
    [GeneratedRegex("^_… and \\d+ more; (.*)\\._$", RegexOptions.Multiline)]
    private static partial Regex MoreHintRegex();

    /// <summary>
    /// Kills Q05 (FH10, M18): a blank detail is the default (concise), like every blank optional argument of the campaign
    /// tools; campaign_history as_of reads it through the same helper.
    /// </summary>
    [Fact]
    public async Task Get_BlankDetail_IsTheConciseDefault()
    {
        await _s.Call("campaign", """{"action":"create","name":"Detail World","role":"dm","ruleset":"2024","slug":"det"}""");
        await _s.Call("campaign_write", """{"campaign":"det","ops":[{"op":"upsert","kind":"character","name":"Iron Guts","summary":"A dwarf smith."}]}""");

        var blank = await _s.Call("campaign_get", """{"campaign":"det","refs":["character:iron-guts"],"detail":""}""");

        Assert.Equal(await _s.Call("campaign_get", """{"campaign":"det","refs":["character:iron-guts"]}"""), blank);
    }

    /// <summary>
    /// FH6 (L10): a party view of an entry with more relations than listed points at campaign_get with the party's own
    /// perspective and the campaign, and that call sent as printed reads the party's view; the author keeps the bare hint.
    /// </summary>
    [Fact]
    public async Task Get_PartyViewWithMoreRelationsThanListed_PointsAtCampaignGetInThePartysView()
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky","role":"player","ruleset":"2024","slug":"sky","my_character":"Aria"}""");
        foreach (var range in new[] { Enumerable.Range(1, 25), Enumerable.Range(26, 20) })
        {
            var ops = string.Join(", ", range.SelectMany(i => new[]
            {
                $$"""{"op": "upsert", "kind": "location", "name": "Isle {{i:D2}}", "visibility": "party"}""",
                $$"""{"op": "link", "from": "character:aria", "rel": "visited", "to": "location:isle-{{i:D2}}", "visibility": "party"}""",
            }));
            await _s.Call("campaign_write", $$"""{"campaign": "sky", "ops": [{{ops}}]}""");
        }

        var party = await _s.Call("campaign_get", """{"campaign":"sky","refs":["character:aria"],"include":["relations"],"perspective":"party"}""");
        var author = await _s.Call("campaign_get", """{"campaign":"sky","refs":["character:aria"],"include":["relations"]}""");

        Assert.Matches("_… and \\d+ more; read the other end with campaign_get\\._", author);
        var call = RelationsHintRegex().Match(party).Groups[1].Value;
        Assert.Equal("{\"campaign\": \"sky\", \"refs\": [...], \"perspective\": \"party\"}", call);
        var read = await _s.Call("campaign_get", call.Replace("[...]", "[\"location:isle-45\"]", StringComparison.Ordinal));
        Assert.StartsWith("# Isle 45 (`location:isle-45`)\n_Perspective: party.", read, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH6 (L10): a non-author view of an entry whose facts, children, sessions and knowledge each run past their limit
    /// gets one "… and N more" hint per list, and every one is a call with this campaign and the same perspective: never
    /// the author's (campaign_search or campaign_session without a perspective, the knowledge ledger, a resource). Followed,
    /// an author-view hint lists author-only entries and true names to a model drafting for the party. The session-list
    /// call, sent as printed, reads the same view.
    /// </summary>
    [Theory]
    [InlineData("party", "")]
    [InlineData("character:aria", "")]
    // Review LR02: a read as of a session points at reads as of the same session (the session list takes none).
    [InlineData("party", ", \"as_of_session\": 41")]
    public async Task Get_NonAuthorViewWithEveryListPastItsLimit_EveryMoreHintReadsTheSameViewOfTheCampaign(string perspective, string asOf)
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky","role":"player","ruleset":"2024","slug":"sky","my_character":"Aria"}""");
        // Past each limit: facts (and so knowledge, one line per fact plus the entry's own) past twice the list limit.
        const int Listed = EntityMarkdown.MaxListed;
        foreach (var chunk in Enumerable.Range(1, (Listed * 2) + 1).Chunk(25))
        {
            var facts = string.Join(", ", chunk.Select(i =>
                $$"""{"op": "fact", "statement": "Aria knows tune {{i:D2}}.", "about": ["character:aria"], "visibility": "party", "known_by": [{"who": "party"}]}"""));
            await _s.Call("campaign_write", $$"""{"campaign": "sky", "ops": [{{facts}}]}""");
        }

        var items = string.Join(", ", Enumerable.Range(1, Listed + 1).Select(i =>
            $$"""{"op": "upsert", "kind": "item", "name": "Trinket {{i:D2}}", "parent": "character:aria", "visibility": "party"}"""));
        await _s.Call("campaign_write", $$"""{"campaign": "sky", "ops": [{{items}}]}""");
        for (var n = 1; n <= Listed + 1; n++)
        {
            await _s.Call("campaign_session",
                $$"""{"action": "record_past", "campaign": "sky", "session": {{n}}, "recap_md": "Night {{n}}.", "attendance": [{"character": "character:aria"}]}""");
        }

        var text = await _s.Call("campaign_get",
            $$"""{"campaign": "sky", "refs": ["character:aria"], "include": ["facts", "knowledge", "children", "sessions"], "perspective": "{{perspective}}"{{asOf}}}""");

        var view = $"\"perspective\": \"{perspective}\"{asOf}}}";
        var sessionList = $"\"perspective\": \"{perspective}\"}}";
        Assert.Equal(
            [
                $"campaign_search {{\"campaign\": \"sky\", \"query\": ..., {view} finds more",
                $"campaign_search {{\"campaign\": \"sky\", \"kinds\": [...], {view} lists them all",
                $"campaign_session {{\"campaign\": \"sky\", \"action\": \"list\", {sessionList} shows every session",
                $"campaign_search {{\"campaign\": \"sky\", \"query\": ..., {view} finds the rest by words",
            ],
            MoreHintRegex().Matches(text).Select(m => m.Groups[1].Value).ToArray());
        Assert.DoesNotContain("ledger", text, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign://", text, StringComparison.Ordinal);
        var sessions = await _s.Call("campaign_session", $$"""{"campaign": "sky", "action": "list", "perspective": "{{perspective}}"}""");
        Assert.Contains($"_Perspective: {perspective}", sessions, StringComparison.Ordinal);
    }
}
