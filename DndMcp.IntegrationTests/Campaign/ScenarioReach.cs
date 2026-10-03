using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Every read a perspective can make of one campaign through the tools and the resource, labelled, for the leak sweeps:
/// the listing; a search for each query given (without its title, which echoes the query); a get of every handle the
/// author's listing names, every fact given and every ref the view's own listing shows, with every include in full (the
/// page, or the refusal without its echo of the typed handle); the summary and the campaign record; the session list and
/// each session given; and the knowledge resource. <see cref="HiddenNamesAsync"/> then derives, from what the view was
/// shown, the true names and author refs it must never see.
///
/// <para>
/// Why the gets take the author's handles: a view's own listing names only what it may see, so a sweep of those alone
/// would never knock on a door that must stay shut; with the author's handles, a refusal has to be as clean as a page.
/// And why it reads everything rather than the pages a test author thought of: the leak rule is about everything a view
/// is shown (contract §0), and a leak lands where nobody looked (a session's attendance note, a fact page's links).
/// </para>
/// </summary>
internal static class ScenarioReach
{
    /// <summary>The includes every get of a sweep asks for: everything a page can carry.</summary>
    public const string AllIncludes = """["relations", "facts", "knowledge", "children", "sessions", "history"]""";

    private static readonly Regex ListedRef = new(@"^\d+\. \*\*.+\*\* · .+ · `([^`]+)`$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly Regex HiddenRef = new("`(e:[0-9]+)`", RegexOptions.CultureInvariant);

    private static readonly Regex PageTitle = new(@"\A# (.+) \(`([^`]+)`(?: · `e:\d+`)?\)\n", RegexOptions.CultureInvariant);

    /// <summary>Every read <paramref name="perspective"/> can make of <paramref name="campaign"/> (see the class summary), labelled.</summary>
    public static async Task<List<(string Label, string Text)>> ReadAllAsync(
        McpServerHarness server, string campaign, string perspective, IReadOnlyList<string> queries, IReadOnlyList<string> facts, IReadOnlyList<int> sessions)
    {
        var outputs = new List<(string Label, string Text)>();
        var listing = await ScenarioCalls.Call(server, "campaign_search", $$"""{"campaign": "{{campaign}}", "perspective": "{{perspective}}", "limit": 50}""");
        outputs.Add(("listing", listing));
        foreach (var query in queries)
        {
            var found = await ScenarioCalls.Call(server, "campaign_search",
                $$"""{"campaign": "{{campaign}}", "perspective": "{{perspective}}", "query": {{JsonSerializer.Serialize(query)}}, "limit": 50}""");
            outputs.Add(($"search \"{query}\"", ScenarioLeak.SearchBody(found)));
        }

        var authorListing = await ScenarioCalls.Call(server, "campaign_search", $$"""{"campaign": "{{campaign}}", "limit": 50}""");
        foreach (var handle in ListedRefs(authorListing).Concat(facts).Concat(ListedRefs(listing)).Distinct(StringComparer.Ordinal))
        {
            var result = await server.CallToolJsonAsync("campaign_get", $$"""
                {"campaign": "{{campaign}}", "perspective": "{{perspective}}", "refs": ["{{handle}}"], "include": {{AllIncludes}}, "detail": "full"}
                """);
            outputs.Add(($"get {handle}", ScenarioLeak.WithoutTypedHandle(server.SingleText(result), handle)));
        }

        outputs.Add(("summary", await ScenarioCalls.Call(server, "campaign", $$"""{"action": "summary", "campaign": "{{campaign}}", "perspective": "{{perspective}}"}""")));
        outputs.Add(("campaign get", await ScenarioCalls.Call(server, "campaign", $$"""{"action": "get", "campaign": "{{campaign}}", "perspective": "{{perspective}}"}""")));
        outputs.Add(("session list", await ScenarioCalls.Call(server, "campaign_session", $$"""{"action": "list", "campaign": "{{campaign}}", "perspective": "{{perspective}}"}""")));
        foreach (var session in sessions)
        {
            var result = await server.CallToolJsonAsync("campaign_session",
                $$"""{"action": "get", "campaign": "{{campaign}}", "session": {{session}}, "perspective": "{{perspective}}"}""");
            outputs.Add(($"session {session}", server.SingleText(result)));
        }

        outputs.Add(("knowledge resource", await ScenarioCalls.Read(server, $"campaign://{campaign}/knowledge/{perspective}")));
        return outputs;
    }

    /// <summary>
    /// What <paramref name="perspective"/> must never be shown about the entities it is shown only by an
    /// <c>e:&lt;n&gt;</c> ref in <paramref name="outputs"/> (contract §3.2): each one's author ref (the <c>kind:slug</c> the
    /// e-ref stands in for), and its true name when the view knows it by another (the disguise). Derived from the view's
    /// own page of each such ref and the author's, so a disguise the world gains later is checked with no list to keep up;
    /// a ref the view is shown but cannot open fails here.
    /// </summary>
    public static async Task<IReadOnlyList<string>> HiddenNamesAsync(McpServerHarness server, string campaign, string perspective, IEnumerable<string> outputs)
    {
        var hidden = new List<string>();
        foreach (var handle in outputs.SelectMany(o => HiddenRef.Matches(o).Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal))
        {
            var (shownName, _) = Title(await ScenarioCalls.Call(server, "campaign_get",
                $$"""{"campaign": "{{campaign}}", "perspective": "{{perspective}}", "refs": ["{{handle}}"], "include": []}"""));
            var (trueName, authorRef) = Title(await ScenarioCalls.Call(server, "campaign_get", $$"""{"campaign": "{{campaign}}", "refs": ["{{handle}}"], "include": []}"""));
            hidden.Add(authorRef);
            if (!string.Equals(shownName, trueName, StringComparison.OrdinalIgnoreCase))
            {
                hidden.Add(trueName);
            }
        }

        return hidden;
    }

    /// <summary>The refs of a listing or search result: the backticked handle that ends each numbered entity line.</summary>
    public static IEnumerable<string> ListedRefs(string result) => ListedRef.Matches(result).Select(m => m.Groups[1].Value);

    // A one-entity page's title: "# <name> (`<ref>`)".
    private static (string Name, string Ref) Title(string page)
    {
        var match = PageTitle.Match(page);
        Assert.True(match.Success, $"Not an entity page:\n{page}");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }
}
