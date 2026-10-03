using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Every reader of the read path (contract §7) over one database, called the way the host will call them: a perspective
/// given as the text a tool argument carries ("character:belmakor"), the result returned whole. The scenarios write
/// through W's services and read through these, so a golden that passes here is true of the data the write path stores,
/// not of data seeded to look like it (Q's own tests seed with raw SQL; W's own tests read rows back with SQL).
/// </summary>
public sealed class ScenarioReads
{
    public ScenarioReads(CampaignDatabase database)
    {
        Database = database;
        Searcher = new CampaignSearch(database);
        Reader = new EntityReader(database);
        Checker = new KnowledgeCheck(database);
        Ledgers = new KnowledgeLedger(database);
        Summaries = new CampaignSummary(database);
        Histories = new HistoryReader(database);
        SessionReader = new SessionReader(database);
    }

    public CampaignDatabase Database { get; }

    public CampaignSearch Searcher { get; }

    public EntityReader Reader { get; }

    public KnowledgeCheck Checker { get; }

    public KnowledgeLedger Ledgers { get; }

    public CampaignSummary Summaries { get; }

    public HistoryReader Histories { get; }

    public SessionReader SessionReader { get; }

    /// <summary>campaign_search: one page of at most 50.</summary>
    public SearchResult Search(CampaignRow campaign, string? query, string perspective = "author", IReadOnlyList<string>? kinds = null,
        IReadOnlyList<string>? statuses = null, int? asOf = null, string? cursor = null) =>
        Searcher.Search(campaign, new SearchRequest(query, Kinds: kinds, Statuses: statuses, Perspective: Perspective.Parse(perspective),
            AsOfSession: asOf, Limit: CampaignLimits.MaxListLimit, Cursor: cursor));

    /// <summary>campaign_search, every page (a listing can pass 50).</summary>
    public (IReadOnlyList<EntityHit> Entities, IReadOnlyList<FactHit> Facts, IReadOnlyList<SearchResult> Pages) SearchAll(
        CampaignRow campaign, string? query, string perspective)
    {
        var pages = new List<SearchResult>();
        string? cursor = null;
        do
        {
            var page = Search(campaign, query, perspective, cursor: cursor);
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return (pages.SelectMany(p => p.Entities).ToList(), pages.SelectMany(p => p.Facts).ToList(), pages);
    }

    /// <summary>campaign_get.</summary>
    public GetResult Get(CampaignRow campaign, string perspective, EntityIncludes includes, int? asOf, params string[] refs) =>
        Reader.Get(campaign, refs, includes, Perspective.Parse(perspective), asOf);

    /// <summary>campaign_get of one handle, or null when the perspective may not see it ("nothing by that handle").</summary>
    public GetResult? TryGet(CampaignRow campaign, string perspective, string handle, EntityIncludes? includes = null, int? asOf = null)
    {
        try
        {
            return Get(campaign, perspective, includes ?? new EntityIncludes(), asOf, handle);
        }
        catch (DndInputException)
        {
            return null;
        }
    }

    /// <summary>campaign_knowledge check.</summary>
    public CheckResult Check(CampaignRow campaign, string text, string speaker, bool diegetic = false, string? audience = null, int? asOf = null) =>
        Checker.Check(campaign, new CheckRequest(text, Perspective.Parse(speaker), diegetic, audience is null ? null : Perspective.Parse(audience), asOf));

    /// <summary>campaign_knowledge ledger.</summary>
    public Ledger Ledger(CampaignRow campaign, IReadOnlyList<string>? about, IReadOnlyList<string>? facts, IReadOnlyList<string>? perspectives, int? asOf = null) =>
        Ledgers.Build(campaign, about, facts, perspectives, asOf);

    /// <summary>One ledger cell: <paramref name="perspective"/>'s standing on one fact (f:n) or entity handle.</summary>
    public LedgerCell Cell(CampaignRow campaign, string target, string perspective, int? asOf = null)
    {
        var isFact = target.StartsWith("f:", StringComparison.Ordinal);
        var ledger = Ledger(campaign, isFact ? null : [target], isFact ? [target] : null, [perspective], asOf);
        return ledger.Rows.First(r => r.Ref == target).Cells.Single();
    }

    /// <summary>The knowledge resource's content for a perspective, every page.</summary>
    public IReadOnlyList<PerspectiveKnowledge> KnownTo(CampaignRow campaign, string perspective)
    {
        var pages = new List<PerspectiveKnowledge>();
        string? cursor = null;
        do
        {
            var page = Ledgers.KnownTo(campaign, Perspective.Parse(perspective), null, CampaignLimits.MaxListLimit, cursor);
            pages.Add(page);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return pages;
    }

    /// <summary>The number of change_log batches in the campaign (history since the beginning).</summary>
    public int BatchCount(CampaignRow campaign) => Histories.Since(campaign, null, null, null, 1, null).Total;
}
