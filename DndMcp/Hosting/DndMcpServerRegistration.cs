using System.Reflection;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Repository.Srd.Index;
using DndMcp.Resources;
using DndMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DndMcp.Hosting;

/// <summary>
/// Every service, tool, resource and filter the server registers — everything except the transport.
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
/// executable, the user's cache directory), and the tests move the cache under the test output so a test run never
/// reads or rewrites the user's srd.db.
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

        // Singleton: one index per process, opened once and shared (SrdIndex is thread-safe). The hosted service only
        // starts it early; registered before the transport so it starts first, and it returns without waiting.
        services.AddSingleton<SrdIndexService>();
        services.AddHostedService<SrdIndexWarmup>();

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
            }))
            // Generic WithTools<T>() rather than WithToolsFromAssembly: explicit, and trim/AOT-safe.
            .WithTools<DiceTools>(McpJson.Options)
            .WithTools<RulesTools>(McpJson.Options)
            .WithResources<RulesResources>();
    }
}
