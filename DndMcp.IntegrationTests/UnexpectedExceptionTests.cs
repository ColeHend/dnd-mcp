using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: an exception that is not a <see cref="DndInputException"/> reaches the model as exactly the SDK's bare
/// "An error occurred invoking '&lt;tool&gt;'." — none of its message — while the full exception still reaches the
/// server log.
///
/// <para>
/// This is the other half of ToolErrorTests' contract, and the half that fails dangerously. The call-tool filter
/// translates DndInputException only. Widening it to "translate every exception" makes every error richer, keeps all
/// of ToolErrorTests green, and from Phase 2 on sends whatever a repository exception carries — SQL, file paths,
/// DM-only campaign text — to whoever drives the client. The test tool throws text that stands in for exactly that.
/// </para>
/// </summary>
public sealed class UnexpectedExceptionTests : IClassFixture<TestOnlyToolsServer>
{
    private const string Tool = "throw_on_demand";

    private readonly McpServerHarness _server;

    public UnexpectedExceptionTests(TestOnlyToolsServer fixture)
    {
        _server = fixture.Harness;
    }

    [Theory]
    [InlineData("invalid_operation")]
    [InlineData("argument")]
    [InlineData("overflow")]
    [InlineData("json")]
    public async Task CallTool_NonInputException_ReturnsOnlyTheGenericText(string kind)
    {
        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["kind"] = kind });

        Assert.Equal($"An error occurred invoking '{Tool}'.", _server.ErrorText(result));

        // Not only the text block: nothing anywhere in the result (structured content, _meta) may carry it.
        var wholeResult = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        Assert.DoesNotContain("secret", wholeResult, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A SqliteException from a tool that is not a campaign tool (srd.db's, under a rules tool) stays the generic error,
    /// even once campaigns.db's path is resolved and the same exception from a campaign tool would be mapped. The call-tool
    /// filter gives the campaign store's message ("campaigns.db at … is damaged …", FH1) only for the campaign tools: from
    /// any other tool it would send the user to repair a file that is fine, while the broken one goes unnamed.
    /// </summary>
    [Theory]
    [InlineData("sqlite_busy")]
    [InlineData("sqlite_corrupt")]
    [InlineData("sqlite_notadb")]
    public async Task CallTool_SqliteExceptionFromANonCampaignTool_ReturnsOnlyTheGenericTextNeverCampaignsDbs(string kind)
    {
        await _server.CallToolJsonAsync("campaign", """{"action": "list"}""");
        var code = kind switch { "sqlite_busy" => 5, "sqlite_corrupt" => 11, _ => 26 };
        Assert.True(_server.Services.GetRequiredService<CampaignService>().TryMapStoreFailure(new SqliteException("from a campaign tool", code), out _),
            "campaigns.db's path is not resolved, so nothing would be mapped and this test could not fail.");

        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["kind"] = kind });

        Assert.Equal($"An error occurred invoking '{Tool}'.", _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_NonInputException_KeepsTheDetailInTheServerLog()
    {
        // Hiding the detail from the model is acceptable only because stderr keeps it; a filter that swallowed the
        // exception to "clean up" the error would leave a bug with no trace at all.
        await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["kind"] = "invalid_operation" });

        Assert.Contains(
            _server.ServerLog.Entries,
            e => e.Exception is InvalidOperationException { Message: TestOnlyTools.SecretDetail });
    }

    [Fact]
    public async Task CallTool_DndInputException_MessageReachesTheModel()
    {
        // The control: same tool, only the exception type differs. Without it, a filter that stopped translating
        // anything at all would pass the tests above.
        var result = await _server.Client.CallToolAsync(Tool, new Dictionary<string, object?> { ["kind"] = "input" });

        Assert.Equal($"An error occurred invoking '{Tool}': {TestOnlyTools.FixableMessage}", _server.ErrorText(result));
    }
}
