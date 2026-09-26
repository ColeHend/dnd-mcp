using DndMcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP JSON-RPC stream and nothing else: a single stray line breaks the session.
// Every log level therefore goes to stderr (Claude Code captures it with `claude --debug=mcp`), and
// nothing in this process may use Console.Write*. StdoutPurityTests enforces this against the built binary.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddDndMcpServer()
    .WithStdioServerTransport();

await builder.Build().RunAsync();
