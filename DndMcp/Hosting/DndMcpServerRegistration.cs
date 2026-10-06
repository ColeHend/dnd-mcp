using System.Reflection;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Prompts;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Srd.Index;
using DndMcp.Resources;
using DndMcp.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DndMcp.Hosting;

/// <summary>
/// Every service, tool, prompt, resource and filter the server registers — everything except the transport.
///
/// <para>
/// This deliberately departs from the sibling repos' "all DI inline in Program.cs" rule. The integration
/// tests must run the exact registrations the real server runs; with the registrations inline in
/// Program.cs the tests would have to copy them, and a tool added to Program.cs but not to the copy
/// would be untested while every test stayed green. Program.cs adds the stdio transport, the tests add
/// an in-memory stream transport, and nothing else differs.
/// </para>
/// <para>
/// The only other difference is <see cref="DndMcpServerOptions"/>: Program.cs takes the defaults (content next to the
/// executable, the user's cache and data directories), and the tests move the cache under the test output so a test run
/// never reads or rewrites the user's srd.db, and give every server a data directory of its own so no test opens the
/// user's campaigns.db or another test's.
/// </para>
/// <para>
/// ServerSurfaceTests pins what these registrations expose (17 tools with their hints, 6 prompts with their arguments,
/// the static resources and the per-campaign ones, no templates), so a line added or dropped here fails there first.
/// </para>
/// </summary>
internal static class DndMcpServerRegistration
{
    public const string ServerName = "dnd";

    public static IMcpServerBuilder AddDndMcpServer(this IServiceCollection services, Action<DndMcpServerOptions>? configure = null)
    {
        var options = new DndMcpServerOptions();
        configure?.Invoke(options);

        // Singleton: settled once, before anything reads a path.
        services.AddSingleton(options);

        // Singleton: stateless, and RandomNumberGenerator is thread-safe.
        services.AddSingleton<IDiceRoller, CryptoDiceRoller>();

        // Hosted, first of all: a native SQLite that lacks a feature srd.db or campaigns.db needs stops the start here, with
        // every gap named, before the index build or initialize (SqliteCapabilityCheck). Hosted services start in order.
        services.AddHostedService<SqliteCapabilityCheck>();

        // Singleton: one index per process, opened once and shared (SrdIndex is thread-safe). The hosted service only
        // starts it early; registered before the transport so it starts first, and it returns without waiting.
        services.AddSingleton<SrdIndexService>();
        services.AddHostedService<SrdIndexWarmup>();

        // Singleton: stat blocks are normalized once per process and cached by ref, for balance_simulate, rules_get's
        // combatant format and balance_dpr's monster targets alike.
        services.AddSingleton<StatBlockService>();

        // Singleton: one campaigns.db handle per process (its write semaphore only serialises writers that share it), and
        // the process's current campaign lives here. It opens nothing until a campaign tool or a default lookup asks, so
        // registering it costs initialize nothing.
        services.AddSingleton<CampaignService>();

        // Singleton: stateless. The combat side of campaign_character's D5 routing: while a character is a sheet-seeded
        // combatant of the active fight, its damage, heal, temp_hp, use and condition change the combatant (written to the
        // sheet at combat end), not the sheet the write-back would overwrite. On the options' clock, like campaigns.db
        // itself: its combat_log rows must agree with the rest of the fight's (and with a test's fixed clock).
        services.AddSingleton<ICombatRouter>(sp => new CombatRouter(sp.GetRequiredService<DndMcpServerOptions>().Time));

        return services
            .AddMcpServer(serverOptions =>
            {
                serverOptions.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Title = "D&D 5e",
                    Version = typeof(DndMcpServerRegistration).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                };
                serverOptions.ServerInstructions = ServerInstructions.Text;
            })
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                if (context.MatchedPrimitive is McpServerTool tool)
                {
                    ToolArgumentGuard.Validate(
                        tool.ProtocolTool.Name,
                        tool.ProtocolTool.InputSchema,
                        context.Params?.Arguments,
                        tool.Metadata.OfType<MethodInfo>().FirstOrDefault());
                }

                try
                {
                    return await next(context, cancellationToken);
                }
                catch (DndInputException ex)
                {
                    // Only McpException's message reaches the model; anything else becomes the SDK's generic
                    // "An error occurred invoking '<tool>'." Translating here, once, means no tool can forget
                    // to. ToolErrorTests pins the exact text the model receives.
                    throw new McpException(ex.Message, ex);
                }
                catch (SrdIndexUnavailableException ex)
                {
                    // The rules index cannot be built or has been replaced under this server. The message is written for
                    // the user (which file, what to do), and without it rules lookup would fail with no explanation at all.
                    // Every other repository exception stays generic: it may carry SQL or data the model must not see.
                    throw new McpException(ex.Message, ex);
                }
                catch (CampaignStoreUnavailableException ex)
                {
                    // campaigns.db cannot be used (locked by another process past the busy timeout, read-only, full, from
                    // a newer dnd-mcp, damaged). The message names the file and what to do; a raw SqliteException would
                    // reach the model as the generic error and could carry SQL. ToolErrorTests pins it for every campaign
                    // tool against a campaigns.db from a newer dnd-mcp.
                    throw new McpException(ex.Message, ex);
                }
                catch (SqliteException ex) when (context.MatchedPrimitive is McpServerTool { ProtocolTool.Name: var name } && IsCampaignTool(name) &&
                                                 StoreFailure(context.Services, ex) is { } unavailable)
                {
                    // The same failures met by a read statement rather than at the open or in a write (a damaged page, a
                    // lock held past the busy timeout): the store maps those only where it opens and writes, and every
                    // campaign tool reads first (resolving the campaign is a read). Only the campaign tools: any other
                    // tool's SqliteException is srd.db's, and must not be reported as campaigns.db's.
                    throw new McpException(unavailable.Message, unavailable);
                }
            })
            // Prompts and resource reads bypass the call-tool filter, so the same translations are repeated for them:
            // without these a DndInputException from a prompt (an unknown campaign) or a resource (a bad handle in a URI),
            // or an unusable campaigns.db behind either, becomes the SDK's bare internal error. ToolErrorTests pins the store
            // translations for both, CampaignPromptTests and CampaignResourceTests the input one. Every prompt is a campaign
            // prompt (CampaignPrompts is the only prompt class); of the resources only campaign:// ones read campaigns.db.
            .AddGetPromptFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex) when (ex is DndInputException or CampaignStoreUnavailableException)
                {
                    throw new McpException(ex.Message, ex);
                }
                catch (SqliteException ex) when (StoreFailure(context.Services, ex) is { } unavailable)
                {
                    throw new McpException(unavailable.Message, unavailable);
                }
            })
            .AddReadResourceFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex) when (ex is DndInputException or CampaignStoreUnavailableException)
                {
                    throw new McpException(ex.Message, ex);
                }
                catch (SqliteException ex) when ((context.Params?.Uri ?? string.Empty).StartsWith(CampaignResources.Scheme, StringComparison.Ordinal) &&
                                                 StoreFailure(context.Services, ex) is { } unavailable)
                {
                    throw new McpException(unavailable.Message, unavailable);
                }
            }))
            // Generic WithTools<T>() rather than WithToolsFromAssembly: explicit, and trim/AOT-safe. The campaign tools get
            // CampaignService by constructor, never as a method parameter: an injected method parameter can leak into the
            // published schema when two servers build a tool at once (SDK 2.2.0; McpServerHarness).
            .WithTools<DiceTools>(McpJson.Options)
            .WithTools<RulesTools>(McpJson.Options)
            .WithTools<EncounterTools>(McpJson.Options)
            .WithTools<BalanceTools>(McpJson.Options)
            .WithTools<SimulateTools>(McpJson.Options)
            .WithTools<CampaignTools>(McpJson.Options)
            .WithTools<CampaignSearchTools>(McpJson.Options)
            .WithTools<CampaignGetTools>(McpJson.Options)
            .WithTools<CampaignWriteTools>(McpJson.Options)
            .WithTools<CampaignKnowledgeTools>(McpJson.Options)
            .WithTools<CampaignSessionTools>(McpJson.Options)
            .WithTools<CampaignHistoryTools>(McpJson.Options)
            // campaign_character takes the combat layer's ICombatRouter when one is registered (contract D5), else none.
            .WithTools<CampaignCharacterTools>(McpJson.Options)
            // combat gets the dice (handed to the Repository, which rolls and logs) and the stat blocks its srd entries need.
            .WithTools<CombatTools>(McpJson.Options)
            // Prompts: instructions that drive the campaign tools; one instance per prompts/get (constructor DI).
            .WithPrompts<CampaignPrompts>(McpJson.Options)
            // Static resources, fixed when the container is built: the attribution, the rules tables, campaign://list.
            .WithResources<RulesResources>()
            .WithResources(RulesTableResources.Create())
            .WithResources<CampaignResources>()
            // campaign://<slug>/summary, /threads and /party are listed per request and the deep campaign:// URIs (entity,
            // session, knowledge and combat/current) read on demand: concrete URIs rather than templates, which the model's
            // resource listing never shows. The read handler is
            // reached only for URIs no registered resource matches, so rules:// reads are unchanged. One handler per slot:
            // a second WithListResourcesHandler or WithReadResourceHandler would replace these, not add to them.
            .WithListResourcesHandler(CampaignResources.ListAsync)
            .WithReadResourceHandler(CampaignResources.ReadAsync);
    }

    /// <summary>
    /// The tools that read campaigns.db and can fail on it: the eight campaign tools, all named <c>campaign</c> or
    /// <c>campaign_*</c>, and <c>combat</c>, whose every step reads and writes campaigns.db (ServerSurfaceTests pins the
    /// tool list; ToolErrorTests' damaged-page row for <c>combat state</c> with a character's perspective proves it is
    /// covered: resolving that view reads the entity table outside the Repository's own mapping, which the party's board
    /// with no fight running never does). A tool added under another name is not covered until it is named here. dice_roll
    /// and the rules, encounter and balance tools read it too, but never let a failure out (a roll stands; a default falls
    /// back) or map it themselves (encounter_difficulty's and balance_simulate's campaign reads), so a SqliteException from
    /// them is srd.db's.
    /// </summary>
    internal static bool IsCampaignTool(string name) =>
        name == "campaign" || name.StartsWith("campaign_", StringComparison.Ordinal) || name == "combat";

    // The store's message for a campaigns.db failure a person can fix, or null (another code, or no campaigns.db opened).
    private static CampaignStoreUnavailableException? StoreFailure(IServiceProvider? services, SqliteException exception) =>
        services?.GetService<CampaignService>() is { } campaigns && campaigns.TryMapStoreFailure(exception, out var unavailable) ? unavailable : null;
}
