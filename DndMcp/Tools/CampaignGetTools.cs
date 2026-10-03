using System.ComponentModel;
using DndMcp.Domain.Core;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>campaign_get</c>: up to ten entities or facts by handle, with what the call includes, from one perspective. Thin by
/// design: resolve the campaign and the view, ask <see cref="EntityReader"/>, render with <see cref="EntityMarkdown"/>.
///
/// <para>
/// <b>A handle the view cannot see reads as one that does not exist.</b> The reader words both alike and suggests only
/// names the view knows, so asking the party's view for <c>item:axiom-cage</c> cannot confirm that it exists. A disguised
/// entity (the party knows "the old king" but not his name) comes back under the name the view knows, as
/// <c>e:&lt;n&gt;</c>, with only the facts that view knows: never its summary, aliases or true slug.
/// </para>
/// <para>
/// <b>Concise by default</b>: bodies and secret text are cut to their first lines; <c>detail: "full"</c> shows them whole
/// (within the output cap, shared between the refs asked for).
/// </para>
/// <para>Hints: read-only, idempotent, closed-world (campaigns.db only).</para>
/// </summary>
public sealed class CampaignGetTools
{
    private const string Example = "{\"refs\": [\"character:old-king\"], \"include\": [\"relations\", \"facts\", \"knowledge\"], \"perspective\": \"party\"}";

    private readonly CampaignService _campaigns;

    public CampaignGetTools(CampaignService campaigns)
    {
        _campaigns = campaigns;
    }

    // Read-only, idempotent and closed-world: reads campaigns.db only, and never creates it.
    [McpServerTool(Name = "campaign_get", Title = "Read campaign entries", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Read up to 10 campaign entries in full by handle: people, places, items, quests, secrets, sessions (any entity) and " +
        "facts, with their relations, facts, who knows what and more, as one perspective sees them. Find handles with " +
        "campaign_search.\n" +
        "- refs (required): 1-10 handles: \"character:belmakor\" (kind:slug), \"belmakor\" (slug), \"e:12\", \"f:7\" (a fact), " +
        "a register code (\"Q22\", \"F36\") or \"session:3\".\n" +
        "- include: what to add, from relations, facts, knowledge, children, sessions, history. Default relations, facts, " +
        "children; [] for none. knowledge: the view's verdict on the entry and each fact (the author sees every knower); " +
        "history (author only): the change batches that touched it.\n" +
        "- detail: \"concise\" (default: bodies cut to their opening) or \"full\" (whole bodies and, for the author, secret text).\n" +
        "- perspective: \"author\" (default: everything, secret text and author-only names included), \"dm\", \"table\", " +
        "\"party\", \"public\" or \"character:<slug>\". Other views see only what they know, under the names they know: an entry " +
        "they know by another name shows that name and an e:<n> ref.\n" +
        "- as_of_session: the entries as they stood at the end of that session number, e.g. 3.\n" +
        "- campaign: the campaign's slug (default: the current campaign).\n" +
        "Example: " + Example)]
    public string Get(
        [Description("1-10 handles, e.g. [\"character:belmakor\", \"f:12\", \"Q22\"].")] string[] refs,
        [Description("What to add: relations, facts, knowledge, children, sessions, history. Default [\"relations\", \"facts\", \"children\"].")]
        string[]? include = null,
        [Description("\"concise\" (default) or \"full\" (whole bodies and secret text).")] string? detail = null,
        [Description("Whose view: \"author\" (default), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\".")] string? perspective = null,
        [Description("Read as of the end of this session number, e.g. 3. Default: now.")][AIParameterName("as_of_session")] int? asOfSession = null,
        [Description("The campaign's slug, e.g. \"belmakor\". Omit for the current campaign.")] string? campaign = null)
    {
        var full = Detail(detail);
        var includes = EntityIncludes.Parse(include);
        var row = _campaigns.Resolve(string.IsNullOrWhiteSpace(campaign) ? null : campaign);
        var view = CampaignView.Resolve(_campaigns.Database, row, perspective, asOfSession);
        var result = new EntityReader(_campaigns.Database).Get(row, refs ?? [], includes, view.Perspective, asOfSession);
        return EntityMarkdown.Format(result, view, row.Slug, full);
    }

    /// <summary>detail as a flag: false for concise (the default), true for full.</summary>
    internal static bool Detail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return false;
        }

        return EntityMarkdown.DetailSet.TryMatch(detail, out var value)
            ? value == EntityMarkdown.Full
            : throw new DndInputException($"detail must be \"concise\" (the default) or \"full\" (got \"{CampaignMarkdownText.Echo(detail)}\").");
    }
}
