using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.IntegrationTests.Infrastructure;
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
