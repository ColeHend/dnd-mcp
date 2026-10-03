using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Xunit;

namespace DndMcp.IntegrationTests.Infrastructure;

/// <summary>
/// Tools that exist only in tests. <see cref="TestOnlyToolsServer"/> registers them on top of the production
/// registrations; <c>AddDndMcpServer</c> never does, so the model never sees them.
///
/// <para>
/// They reach host code that serves every future tool but that no Phase 0 tool can drive on demand: the call-tool
/// filter's handling of an arbitrary exception type, and ToolArgumentGuard's handling of a floating-point parameter
/// and of a parameter renamed with [AIParameterName]. Pinning those through a test tool now means the first real
/// tool to need them (Phase 2's repository exceptions, Phase 3's snake_case integer options) inherits working
/// behaviour instead of discovering the gap in production.
/// </para>
/// </summary>
public sealed class TestOnlyTools
{
    /// <summary>
    /// Stands in for what an unexpected exception can carry once Repository code exists: SQL text, file paths,
    /// DM-only campaign notes. It must never reach the model.
    /// </summary>
    public const string SecretDetail = "secret: /home/dm/campaigns.db says the villain is the king";

    /// <summary>A DndInputException message, which must reach the model verbatim — the control case.</summary>
    public const string FixableMessage = "kind \"input\" is refused on purpose; send another kind.";

    [McpServerTool(Name = "throw_on_demand", Title = "Throw on demand (test only)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Test only: throws the exception named by kind.")]
    public string ThrowOnDemand([Description("invalid_operation, argument, overflow, json, input, sqlite_busy, sqlite_corrupt or sqlite_notadb.")] string kind)
    {
        switch (kind)
        {
            // What srd.db can raise under a rules tool: SQLite codes the campaign store maps to "campaigns.db is locked /
            // damaged / not a database", which must stay the generic error from any tool that is not a campaign tool.
            case "sqlite_busy":
                throw new SqliteException(SecretDetail, 5);
            case "sqlite_corrupt":
                throw new SqliteException(SecretDetail, 11);
            case "sqlite_notadb":
                throw new SqliteException(SecretDetail, 26);
            case "invalid_operation":
                throw new InvalidOperationException(SecretDetail);
            case "argument":
                throw new ArgumentException(SecretDetail, nameof(kind));
            case "overflow":
                throw new OverflowException(SecretDetail);
            case "json":
                throw new JsonException(SecretDetail);
            case "input":
                throw new DndInputException(FixableMessage);
            default:
                throw new DndInputException($"Unknown kind \"{kind}\".");
        }
    }

    // MEAI001: [AIParameterName] is experimental in Microsoft.Extensions.AI 10.8.3; ToolArgumentGuard depends on it too.
#pragma warning disable MEAI001
    [McpServerTool(Name = "echo_numbers", Title = "Echo numbers (test only)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Test only: echoes the numbers it was given, as bound.")]
    public string EchoNumbers(
        [AIParameterName("round_cap"), Description("An int whose schema name differs from its C# name.")] int roundCap = 0,
        [Description("A floating-point number.")] double ratio = 0) =>
        string.Create(CultureInfo.InvariantCulture, $"round_cap={roundCap}; ratio={ratio:R}");
#pragma warning restore MEAI001

    [McpServerTool(Name = "echo_shape", Title = "Echo shape (test only)", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Test only: echoes an object argument and an array of objects, as bound.")]
    public string EchoShape(
        [Description("An object argument.")] EchoShapeInput shape,
        [Description("An array of objects.")] EchoShapeInput[]? more = null,
        [Description("Untyped, with shape's fields (SameShapeAsAttribute, as balance_compare's variant).")][SameShapeAs("shape")] object? copy = null) =>
        $"name={shape.Name}; more={more?.Length ?? 0}" +
        (copy is JsonElement { ValueKind: JsonValueKind.Object } element ? $"; copy={element.Deserialize<EchoShapeInput>(McpJson.Options)!.Name}" : string.Empty);
}

/// <summary>An object parameter with a required field, a field the schema cannot fully check, and a nested array.</summary>
public sealed class EchoShapeInput
{
    [Description("Required.")]
    public required string Name { get; init; }

    [Description("A date: the schema says string, only the binder knows the format.")]
    public DateOnly? When { get; init; }

    [Description("Tags.")]
    public string[]? Tags { get; init; }
}

/// <summary>
/// Class fixture: the production server plus <see cref="TestOnlyTools"/>. Its own type because xUnit builds class
/// fixtures through a parameterless constructor, and the plain <see cref="McpServerHarness"/> must stay
/// production-only so ServerSurfaceTests keeps checking the exact tool list the model sees.
/// </summary>
public sealed class TestOnlyToolsServer : IAsyncLifetime
{
    public McpServerHarness Harness { get; } =
        McpServerHarness.WithExtraTools(server => server.WithTools<TestOnlyTools>(McpJson.Options));

    public Task InitializeAsync() => Harness.InitializeAsync();

    public Task DisposeAsync() => Harness.DisposeAsync();
}
