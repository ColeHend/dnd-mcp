using System.ComponentModel;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign_search</c>: find a campaign's entities and facts by words, or list them by kind, status and tag, from one
/// perspective. Thin by design: resolve the campaign and the view, ask <see cref="CampaignSearch"/>, render with
/// <see cref="SearchMarkdown"/>.
///
/// <para>
/// <b>The perspective is the point.</b> <c>perspective: "character:belmakor"</c> is how the model asks "what can Belmakor
/// find", and the answer must never contain what he cannot see: not a secret's text, not an author alias, not the true
/// name of someone he knows by another, not even a count or a "nothing visible" wording that says hidden results exist.
/// The reader enforces that (column-filtered FTS, the disguise rule, snippets built from visible fields); this tool adds
/// the banner and prints only what the reader returned, so nothing it formats can reintroduce a hidden string.
/// </para>
/// <para>
/// <b>as_of_session</b> reads rows as they stood at the end of that session (the reader replays the change log); the text
/// match itself is always against today's words, which the result says. The view is resolved as of that session too (a
/// character is named as he knew himself then), and the footer's call to read the hits keeps the session (review LR01,
/// LR02).
/// </para>
/// <para>Hints: read-only, idempotent, closed-world (campaigns.db only).</para>
/// </summary>
public sealed class CampaignSearchTools
{
    private const string Example = "{\"query\": \"old king\", \"perspective\": \"character:belmakor\"}";

    private readonly CampaignService _campaigns;

    public CampaignSearchTools(CampaignService campaigns)
    {
        _campaigns = campaigns;
    }

    // Read-only, idempotent and closed-world: reads campaigns.db only, and never creates it.
    [McpServerTool(Name = "campaign_search", Title = "Search a campaign", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Find people, places, quests, secrets, facts and anything else in a campaign by words, or list them by kind, status " +
        "and tag, as one perspective sees them. Returns refs (character:belmakor, f:12) to read in full with campaign_get. Use " +
        "a perspective to ask what a character or the party can know: its results use only the names it knows and never " +
        "include text it cannot see.\n" +
        "- query: words, all must match; end a word with * for a prefix (\"sorcer*\"). Exact names first. If nothing has " +
        "every word, results with any word are returned and the result says so. Omit query to list by kinds, statuses, tags.\n" +
        "- kinds: e.g. [\"character\", \"quest\"]. Kinds: character, location, faction, item, lore, rule, homebrew, quest, " +
        "thread, question, secret, arc, beat, scene, session, event, handout, work, clock, front, note.\n" +
        "- statuses: e.g. [\"open\", \"active\"]; tags: e.g. [\"undead\"] (any of them).\n" +
        "- perspective: \"author\" (default: everything), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\".\n" +
        "- include_facts: search facts too (default true; only with a query and no filters).\n" +
        "- as_of_session: rows as they stood at the end of that session number, e.g. 3.\n" +
        "- limit: results per page, 1-50 (default 15); cursor: the next-page cursor a result gives.\n" +
        "- campaign: the campaign's slug (default: the current campaign).\n" +
        "Example: " + Example)]
    public string Search(
        [Description("Words to find, e.g. \"old king\" or \"sorcer*\". Omit to list by kinds, statuses or tags.")] string? query = null,
        [Description("Kinds to keep, e.g. [\"character\", \"location\"].")] string[]? kinds = null,
        [Description("Statuses to keep, e.g. [\"open\", \"active\"].")] string[]? statuses = null,
        [Description("Tags to keep (any of them), e.g. [\"undead\"].")] string[]? tags = null,
        [Description("Whose view: \"author\" (default), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\".")] string? perspective = null,
        [Description("Search facts too. Default true.")][AIParameterName("include_facts")] bool? includeFacts = null,
        [Description("Read as of the end of this session number, e.g. 3. Default: now.")][AIParameterName("as_of_session")] int? asOfSession = null,
        [Description("Results per page, 1-50. Default 15.")] int? limit = null,
        [Description("The cursor a previous page gave, to read the next page.")] string? cursor = null,
        [Description("The campaign's slug, e.g. \"belmakor\". Omit for the current campaign.")] string? campaign = null)
    {
        var row = _campaigns.Resolve(string.IsNullOrWhiteSpace(campaign) ? null : campaign);
        var view = CampaignView.Resolve(_campaigns.Database, row, perspective, asOfSession);
        var queryText = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var result = new CampaignSearch(_campaigns.Database).Search(row, new SearchRequest(
            queryText, NonEmpty(kinds), NonEmpty(statuses), NonEmpty(tags), view.Perspective, includeFacts ?? true, asOfSession, limit,
            string.IsNullOrWhiteSpace(cursor) ? null : cursor));
        return SearchMarkdown.Format(result, view, row.Slug, queryText, Filters(kinds, statuses, tags));
    }

    // "kinds character, quest; statuses open" for the title (the caller's own words, shortened).
    private static string Filters(string[]? kinds, string[]? statuses, string[]? tags)
    {
        var parts = new List<string>();
        foreach (var (label, values) in new[] { ("kinds", kinds), ("statuses", statuses), ("tags", tags) })
        {
            if (NonEmpty(values) is { } list)
            {
                parts.Add(label + " " + string.Join(", ", list.Take(8).Select(CampaignMarkdownText.Echo)) + (list.Count > 8 ? ", …" : string.Empty));
            }
        }

        return string.Join("; ", parts);
    }

    // An empty list is "no filter" (models send [] for "none"); items are passed as given, so the reader's
    // "kinds item 2" numbering matches the caller's list.
    private static IReadOnlyList<string>? NonEmpty(string[]? values) => values is { Length: > 0 } ? values : null;
}
