using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using ModelContextProtocol.Client;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: everything Claude Code reads before it calls a tool — the tool list, each tool's name,
/// description, title, annotations and parameter names, and the server's name and instructions — is exactly
/// what we intend and within the limits Claude Code and the Claude API enforce.
///
/// <para>
/// None of these break loudly. A tool name with a dot is registered happily by the SDK and then rejected by the
/// Claude API; a description over 2,048 characters is silently truncated by Claude Code; a missing annotation
/// is read by the MCP spec as destructive and open-world. So each is pinned here, per tool.
/// </para>
/// <para>
/// Not covered here: the <c>mcp__dnd__</c> prefix on every tool name, which users' allow-lists match. Claude Code
/// builds it from the name the server is registered under (<c>claude mcp add ... dnd</c> in the README), not from
/// anything the server sends, so no in-process test can see it.
/// </para>
/// </summary>
public sealed partial class ServerSurfaceTests : IClassFixture<McpServerHarness>
{
    /// <summary>
    /// THE list of tools the server exposes. Adding, removing or renaming a tool must change this line — that is
    /// the point: the surface the model sees changes only on purpose, and every per-tool check below then runs
    /// against the new tool automatically.
    /// </summary>
    public static readonly IReadOnlyList<string> ExpectedToolNames =
    [
        "dice_odds",
        "dice_roll",
    ];

    // Claude Code truncates tool descriptions and server instructions here (CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH).
    private const int DescriptionLimit = 2048;

    private readonly McpServerHarness _server;

    public ServerSurfaceTests(McpServerHarness server)
    {
        _server = server;
    }

    public static TheoryData<string> ToolNames => new(ExpectedToolNames);

    // The Claude API tool-name rule, tightened to Claude Code's 64-character cap. The SDK allows '.', the API does not.
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNameRegex();

    // Claude's rule for input-schema property names.
    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,64}$")]
    private static partial Regex SchemaPropertyNameRegex();

    [Fact]
    public async Task ListTools_Server_ExposesExactlyTheExpectedTools()
    {
        var tools = await _server.Client.ListToolsAsync();
        var actual = tools.Select(t => t.Name).ToList();

        Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            ExpectedToolNames.Order(StringComparer.Ordinal),
            actual.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolName_EveryTool_MatchesClaudeApiNameRule(string name)
    {
        var tool = await GetToolAsync(name);

        Assert.Matches(ToolNameRegex(), tool.Name);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolDescription_EveryTool_IsNonEmptyAndWithinTruncationLimit(string name)
    {
        var tool = await GetToolAsync(name);

        Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{name} has no description.");
        Assert.True(
            tool.Description.Length <= DescriptionLimit,
            $"{name}'s description is {tool.Description.Length} characters; Claude Code truncates at {DescriptionLimit}.");
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolTitle_EveryTool_IsSet(string name)
    {
        var tool = await GetToolAsync(name);

        Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Title), $"{name} has no title; /mcp would show the raw name.");
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolAnnotations_EveryTool_SetsAllFourHintsExplicitly(string name)
    {
        var tool = await GetToolAsync(name);
        var annotations = tool.ProtocolTool.Annotations;

        // An omitted hint is not "false": the spec defaults destructiveHint and openWorldHint to TRUE.
        Assert.NotNull(annotations);
        Assert.True(annotations.ReadOnlyHint.HasValue, $"{name}: readOnlyHint is unset.");
        Assert.True(annotations.DestructiveHint.HasValue, $"{name}: destructiveHint is unset (spec default: true).");
        Assert.True(annotations.IdempotentHint.HasValue, $"{name}: idempotentHint is unset.");
        Assert.True(annotations.OpenWorldHint.HasValue, $"{name}: openWorldHint is unset (spec default: true).");
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolAnnotations_ReadOnlyTool_IsNotDestructive(string name)
    {
        var tool = await GetToolAsync(name);
        var annotations = tool.ProtocolTool.Annotations;

        if (annotations?.ReadOnlyHint == true)
        {
            Assert.False(annotations.DestructiveHint, $"{name} claims to be read-only and destructive at once.");
        }
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolInputSchema_EveryTool_IsObjectWithValidDescribedProperties(string name)
    {
        var tool = await GetToolAsync(name);
        var schema = tool.JsonSchema;

        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object,
            $"{name}'s input schema has no properties object.");

        foreach (var property in properties.EnumerateObject())
        {
            Assert.Matches(SchemaPropertyNameRegex(), property.Name);

            // The parameter description is the only hint the model gets about format ("2d6+3", "1-100").
            var hasDescription = property.Value.TryGetProperty("description", out var description) &&
                                 !string.IsNullOrWhiteSpace(description.GetString());
            Assert.True(hasDescription, $"{name}.{property.Name} has no description.");
        }
    }

    [Fact]
    public void ServerInfo_Name_IsDnd()
    {
        // serverInfo.name is only the server's self-reported identity: Claude Code shows it in server status and falls
        // back to it as a display name. It does not name the tools (see the class summary). Pinned so what the server
        // calls itself matches the "dnd" registration users are told to make.
        Assert.Equal("dnd", _server.Client.ServerInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(_server.Client.ServerInfo.Version));
    }

    [Fact]
    public void ServerInstructions_Handshake_AreNonEmptyAndWithinTruncationLimit()
    {
        var instructions = _server.Client.ServerInstructions;

        // Under tool search these instructions and the tool names are all the model sees at session start.
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.True(
            instructions.Length <= DescriptionLimit,
            $"Server instructions are {instructions.Length} characters; Claude Code truncates at {DescriptionLimit}.");
    }

    [Fact]
    public void Handshake_Protocol_IsTheVersionClaudeCodeUsesForStdio()
    {
        Assert.Equal(McpServerHarness.ProtocolVersion, _server.Client.NegotiatedProtocolVersion);
    }

    private async Task<McpClientTool> GetToolAsync(string name)
    {
        var tools = await _server.Client.ListToolsAsync();
        return Assert.Single(tools, t => t.Name == name);
    }
}
