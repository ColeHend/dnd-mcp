using System.ComponentModel;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Features;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DndMcp.Resources;

/// <summary>
/// The <c>campaign://</c> resources: <c>campaign://list</c> (a static resource, like <see cref="RulesResources"/>), one
/// <c>campaign://&lt;slug&gt;/summary</c> and <c>/threads</c> per campaign in resources/list, and the deep URIs
/// <c>/session/&lt;n&gt;</c>, <c>/entity/&lt;ref&gt;</c> and <c>/knowledge/&lt;perspective&gt;</c>, readable but never listed.
///
/// <para>
/// <b>Concrete URIs, never templates.</b> A URI with a <c>{parameter}</c> is listed only in resources/templates/list,
/// which the model's resource-listing tool never shows (ServerSurfaceTests pins that the server has no templates). So the
/// per-campaign URIs are listed by <see cref="ListAsync"/> (<c>WithListResourcesHandler</c>) at request time, and read by
/// <see cref="ReadAsync"/> (<c>WithReadResourceHandler</c>), which the SDK calls only for a URI no registered resource
/// matches: <c>rules://</c> reads never reach it. The deep URIs are unbounded (one per entity, session and perspective),
/// so they are served but not listed; results and <c>campaign://list</c> name them.
/// </para>
/// <para>
/// <b>The list handler is on Claude Code's connect path</b> (resources/list is sent at connect), so it must be fast, must
/// never create, migrate or back up campaigns.db (a user who only rolls dice must never find one), and must never throw:
/// the SDK appends the rules resources to this handler's result, and an exception would fail the whole list, rules
/// included. It therefore reads the campaign table over a connection of its own that is opened without create and only
/// SELECTs (never <see cref="CampaignDatabase.TryOpenExisting"/>, which migrates a file that is behind), only when the
/// file exists and has a schema, and on any failure lists nothing campaign-specific and logs a warning. It returns a new
/// result every call (the SDK appends to it). New campaigns appear after the <c>campaign</c> tool (create, rename) or
/// <c>campaign_history</c> (an undo that adds, removes or renames one) sends <c>notifications/resources/list_changed</c>.
/// </para>
/// <para>
/// <b>The read handler replaces the SDK's "unknown resource" fallback</b> for every URI the collection does not match, an
/// unknown <c>rules://tables/x</c> included, so it reproduces the SDK's error exactly (message and code: -32002 before
/// protocol 2026-07-28, -32602 from it on) for anything that is not <c>campaign://</c>.
/// </para>
/// <para>
/// <b>Views.</b> Summary, threads, session and entity are the author's view (resources are what the user at the keyboard
/// attaches). <c>/knowledge/&lt;perspective&gt;</c> is that perspective's view, through the same filtered reader as every
/// non-author read, under the banner. Every resource renders through the same readers and formatters as the tools.
/// </para>
/// </summary>
public sealed class CampaignResources
{
    public const string Scheme = "campaign://";
    public const string ListUri = Scheme + "list";
    public const string SummaryPath = "summary";
    public const string ThreadsPath = "threads";
    public const string SessionPath = "session";
    public const string EntityPath = "entity";
    public const string KnowledgePath = "knowledge";

    private const string MimeType = "text/markdown";

    // The threads resource pages through the reader's listing up to this many quests and threads.
    private const int MaxThreadsListed = 300;

    // The knowledge resource pages through what a view knows up to this many entries.
    private const int MaxKnownListed = 400;

    // SDK 2.2.0 answers an unknown resource with -32602 from this protocol on (McpProtocolVersions, internal there).
    private const string InvalidParamsForMissingResourceFrom = "2026-07-28";

    private readonly CampaignService _campaigns;

    public CampaignResources(CampaignService campaigns)
    {
        _campaigns = campaigns;
    }

    public static string SummaryUri(string slug) => $"{Scheme}{slug}/{SummaryPath}";

    public static string ThreadsUri(string slug) => $"{Scheme}{slug}/{ThreadsPath}";

    [McpServerResource(UriTemplate = ListUri, Name = "campaigns", Title = "Campaigns", MimeType = MimeType)]
    [Description(
        "Every campaign in campaigns.db (slug, role, ruleset, status) and the campaign:// resources each one can be read at: " +
        "summary, threads, and by URI entity/<ref>, session/<n> and knowledge/<perspective>.")]
    public string List()
    {
        // The mark is the campaign a call without campaign uses, by the rule the campaign tool's list marks it with.
        var campaigns = _campaigns.Store.List();
        return CampaignResourceMarkdown.List(campaigns, _campaigns.DefaultCampaignId(campaigns));
    }

    /// <summary>
    /// resources/list: a summary and a threads resource per campaign (the SDK appends the static resources after these).
    /// Read-only and never throws (class summary).
    /// </summary>
    public static ValueTask<ListResourcesResult> ListAsync(RequestContext<ListResourcesRequestParams> request, CancellationToken cancellationToken)
    {
        var result = new ListResourcesResult();
        var services = request.Services;
        if (services is null)
        {
            return ValueTask.FromResult(result);
        }

        try
        {
            var database = services.GetRequiredService<CampaignService>().Database;
            foreach (var (slug, name, role) in ReadCampaignsReadOnly(database))
            {
                result.Resources.Add(new Resource
                {
                    Uri = SummaryUri(slug),
                    Name = slug + "-summary",
                    Title = name + ": summary",
                    Description = $"The {name} campaign ({role}) at a glance: party, place, last recap, open quests and threads, clocks; " +
                                  "the author's secrets, inventions and changes since the last session. Also: the campaign tool, action \"summary\".",
                    MimeType = MimeType,
                });
                result.Resources.Add(new Resource
                {
                    Uri = ThreadsUri(slug),
                    Name = slug + "-threads",
                    Title = name + ": quests and threads",
                    Description = $"Every quest and thread of the {name} campaign by status, open ones first. Also: campaign_search with kinds [\"quest\", \"thread\"].",
                    MimeType = MimeType,
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deliberately broad: a throw here fails the whole resources/list, the rules resources included, and nothing
            // from the exception reaches the client.
            services.GetService<ILogger<CampaignResources>>()?.LogWarning(ex,
                "Could not list the campaign resources; resources/list shows only the static ones.");
            result = new ListResourcesResult();
        }

        return ValueTask.FromResult(result);
    }

    /// <summary>resources/read for every URI the static resources do not match (class summary).</summary>
    /// <exception cref="McpProtocolException">An unknown URI (the SDK's own not-found error).</exception>
    public static ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken)
    {
        var uri = request.Params?.Uri ?? string.Empty;
        if (!uri.StartsWith(Scheme, StringComparison.Ordinal))
        {
            throw NotFound(request, uri, hint: null);
        }

        var services = request.Services ?? throw new InvalidOperationException("The MCP request has no service provider.");
        var campaigns = services.GetRequiredService<CampaignService>();
        var rest = uri[Scheme.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == rest.Length - 1)
        {
            throw NotFound(request, uri, "campaign resources are campaign://<slug>/summary, /threads, /entity/<ref>, /session/<n> or " +
                                         "/knowledge/<perspective>; campaign://list lists the campaigns");
        }

        var slug = Uri.UnescapeDataString(rest[..slash]);
        var path = rest[(slash + 1)..];
        var campaign = campaigns.Store.TryGet(slug) ?? throw NotFound(request, uri,
            $"there is no campaign \"{CampaignMarkdownText.Echo(slug)}\"; campaign://list lists the campaigns");
        var database = campaigns.Database;
        var markdown = Read(database, campaign, path) ?? throw NotFound(request, uri,
            $"campaign {campaign.Slug} has summary, threads, entity/<ref>, session/<n> and knowledge/<perspective>");
        return ValueTask.FromResult(new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = MimeType, Text = markdown }],
        });
    }

    // The markdown for one campaign path, or null when the path names no resource.
    private static string? Read(CampaignDatabase database, CampaignRow campaign, string path)
    {
        if (path == SummaryPath)
        {
            return SummaryMarkdown.Format(new CampaignSummary(database).Build(campaign, Perspective.Author), CampaignView.Author);
        }

        if (path == ThreadsPath)
        {
            return Threads(database, campaign);
        }

        var slash = path.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == path.Length - 1)
        {
            return null;
        }

        var head = path[..slash];
        var argument = Uri.UnescapeDataString(path[(slash + 1)..]);
        return head switch
        {
            EntityPath => EntityMarkdown.Format(
                new EntityReader(database).Get(campaign, [argument], EntityIncludes.All with { History = false }, Perspective.Author),
                CampaignView.Author, campaign.Slug, full: true),
            SessionPath => CampaignResourceMarkdown.Session(campaign.Name, campaign.Slug,
                new SessionReader(database).Get(campaign, SessionHandle(argument), Perspective.Author)),
            KnowledgePath => Knowledge(database, campaign, argument),
            _ => null,
        };
    }

    // "/session/3", "/session/live" and "/session/last" name session:3, session:live and session:last; the handle parser
    // decides what the word after "session:" may be, so the URI accepts exactly what a session handle does. A negative
    // number is refused first, with the words campaign_session uses: parsed as a handle, "-1" is the slug "1", and the
    // refusal would name session 1, which may exist.
    private static string SessionHandle(string argument)
    {
        var number = argument.StartsWith(CampaignValues.Kinds.Session + ":", StringComparison.OrdinalIgnoreCase)
            ? argument[(CampaignValues.Kinds.Session.Length + 1)..]
            : argument;
        if (number.Length > 1 && number[0] == '-' && number[1..].All(char.IsAsciiDigit))
        {
            throw DslProblems.Exception([$"session is {CampaignMarkdownText.Echo(number)}; it is a session number, 0 to " +
                                         $"{CampaignLimits.MaxSessionNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}."], "session");
        }

        return argument.Contains(':', StringComparison.Ordinal) ? argument : CampaignValues.Kinds.Session + ":" + argument;
    }

    private static string Threads(CampaignDatabase database, CampaignRow campaign)
    {
        var search = new CampaignSearch(database);
        var threads = new List<EntityHit>();
        string? cursor = null;
        do
        {
            var page = search.Search(campaign, new SearchRequest(Kinds: [CampaignValues.Kinds.Quest, CampaignValues.Kinds.Thread],
                Limit: CampaignLimits.MaxListLimit, Cursor: cursor));
            threads.AddRange(page.Entities);
            cursor = page.NextCursor;
        }
        while (cursor is not null && threads.Count < MaxThreadsListed);

        return SummaryMarkdown.Threads(campaign.Name, campaign.Slug, threads, more: cursor is not null);
    }

    private static string Knowledge(CampaignDatabase database, CampaignRow campaign, string perspectiveText)
    {
        var view = CampaignView.Resolve(database, campaign, perspectiveText);
        var ledger = new KnowledgeLedger(database);
        var entities = new List<KnownEntity>();
        var facts = new List<KnownFact>();
        string? cursor = null;
        do
        {
            var page = ledger.KnownTo(campaign, view.Perspective, null, CampaignLimits.MaxListLimit, cursor);
            entities.AddRange(page.Entities);
            facts.AddRange(page.Facts);
            cursor = page.NextCursor;
        }
        while (cursor is not null && entities.Count + facts.Count < MaxKnownListed);

        return CampaignResourceMarkdown.Knowledge(campaign.Name, view, entities, facts, more: cursor is not null);
    }

    /// <summary>
    /// The campaigns for resources/list, over a connection of its own that never creates, migrates or writes the file: none
    /// when campaigns.db does not exist or has no schema yet. Opened read-write WITHOUT create (so a missing file is an
    /// error, never a new file) and used only for SELECTs, like the house read connections: a read-only connection cannot
    /// remove the -wal and -shm files it makes, and would leave them beside the user's database after every listing.
    ///
    /// <para>
    /// <b>A schema newer than this build's is unreadable here</b>, as it is to every other reader
    /// (<see cref="CampaignDatabase.TryOpenExisting"/> refuses it on every use): the newer build may have changed what the
    /// campaign table's columns mean, so listing from it could show a newer build's data as this build reads it. It is
    /// refused with an exception, which the caller logs before listing only the static resources.
    /// </para>
    /// </summary>
    /// <exception cref="CampaignStoreUnavailableException">campaigns.db was written by a newer dnd-mcp.</exception>
    private static List<(string Slug, string Name, string Role)> ReadCampaignsReadOnly(CampaignDatabase database)
    {
        var campaigns = new List<(string, string, string)>();
        if (!database.Exists)
        {
            return campaigns;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt64(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (schema < 1)
            {
                return campaigns;
            }

            if (schema > CampaignDbMigrator.LatestVersion)
            {
                throw new CampaignStoreUnavailableException(
                    $"campaigns.db at {database.Path} has schema version {schema.ToString(System.Globalization.CultureInfo.InvariantCulture)}, newer than " +
                    $"this build's {CampaignDbMigrator.LatestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}; its campaigns are not listed.");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT slug, name, role FROM campaign ORDER BY slug";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            campaigns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return campaigns;
    }

    // The SDK's own not-found error for a URI nothing serves; a campaign:// URI adds what would have worked.
    private static McpProtocolException NotFound(RequestContext<ReadResourceRequestParams> request, string uri, string? hint)
    {
        var code = string.CompareOrdinal(request.Server.NegotiatedProtocolVersion ?? string.Empty, InvalidParamsForMissingResourceFrom) >= 0
            ? McpErrorCode.InvalidParams
            : McpErrorCode.ResourceNotFound;
        return new McpProtocolException(hint is null ? $"Unknown resource URI: '{uri}'" : $"Unknown resource URI: '{uri}': {hint}.", code);
    }
}
