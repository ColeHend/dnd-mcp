using System.Reflection;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DndMcp.Hosting;

/// <summary>
/// Every service, tool and filter the server registers — everything except the transport.
///
/// <para>
/// This deliberately departs from the sibling repos' "all DI inline in Program.cs" rule. The integration
/// tests must run the exact registrations the real server runs; with the registrations inline in
/// Program.cs the tests would have to copy them, and a tool added to Program.cs but not to the copy
/// would be untested while every test stayed green. Program.cs adds the stdio transport, the tests add
/// an in-memory stream transport, and nothing else differs.
/// </para>
/// </summary>
internal static class DndMcpServerRegistration
{
    public const string ServerName = "dnd";

    public static IMcpServerBuilder AddDndMcpServer(this IServiceCollection services)
    {
        // Singleton: stateless, and RandomNumberGenerator is thread-safe.
        services.AddSingleton<IDiceRoller, CryptoDiceRoller>();

        return services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Title = "D&D 5e",
                    Version = typeof(DndMcpServerRegistration).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                };
                options.ServerInstructions = ServerInstructions.Text;
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
            }))
            // Generic WithTools<T>() rather than WithToolsFromAssembly: explicit, and trim/AOT-safe.
            .WithTools<DiceTools>(McpJson.Options);
    }
}
