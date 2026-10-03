using DndMcp.Cli;
using DndMcp.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// A command (`DndMcp srd-build`) runs and exits before any host exists, so it can never start the stdio transport.
// Claude Code launches the server with no arguments; that path below is unchanged. In command mode stdout is the
// command's own output, not a protocol stream, which is why this is the one place Console.Out is handed out.
if (args.Length > 0)
{
    return DndMcpCli.Run(args, new DndMcpServerOptions(), Console.Out, Console.Error);
}

// The content root is the executable's directory, never the current directory: Claude Code launches the server in the
// user's project, and a host rooted there loads that project's appsettings.json (a malformed one aborts startup before
// initialize is answered; one meant for the user's own app reconfigures this server) and, with reload-on-change, puts a
// recursive file watch on every directory of the project, which in a large repository times every open session exhausts
// the inotify watches the user's other tools need. Nothing here reloads configuration, so nothing is watched at all.
var configuration = new ConfigurationManager();
configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["hostBuilder:reloadConfigOnChange"] = "false" });
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    Configuration = configuration,
});

// stdout carries the MCP JSON-RPC stream and nothing else: a single stray line breaks the session.
// Every log level therefore goes to stderr (Claude Code captures it with `claude --debug=mcp`), and
// nothing in this process may use Console.Write*. StdoutPurityTests enforces this against the built binary.
// A start a service refused (SqliteCapabilityCheck) is logged by that service and summarised in one line below; the
// host's own stack-trace repeat of it is dropped, and every other host entry is kept (ReportedStartFailureLogFilter).
DndMcpCli.AddServerLogging(builder.Logging);

builder.Services
    .AddDndMcpServer()
    .WithStdioServerTransport();

// A start that fails (a SQLite library missing what the databases need) exits 1 with one line on stderr instead of an
// unhandled-exception dump; the failing service has logged the detail above it.
return await DndMcpCli.RunServerAsync(builder.Build(), Console.Error);
