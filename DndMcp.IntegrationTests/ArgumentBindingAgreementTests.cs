using System.Text.Json;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: every argument ToolArgumentGuard lets through also binds, so a caller's mistake is either named by the
/// guard or was not a mistake — never the SDK's bare "An error occurred invoking '&lt;tool&gt;'.".
///
/// <para>
/// The guard and the SDK's binder read the same JSON with different parsers, and .NET's are looser than
/// System.Text.Json's: long.TryParse and double.TryParse skip trailing NULs, double.TryParse reads "nan" and turns
/// "1e400" into infinity. The guard's integer range check also has to find the parameter by the name the model sees,
/// which [AIParameterName] makes differ from the C# name. Each gap passes a call the binder then refuses with no
/// detail. dice_roll has no floating-point or renamed parameter, so these run against <see cref="TestOnlyTools"/>;
/// they protect every later tool that has one.
/// </para>
/// </summary>
public sealed class ArgumentBindingAgreementTests : IClassFixture<TestOnlyToolsServer>
{
    private const string Prefix = "An error occurred invoking 'echo_numbers': ";

    private const string AcceptedParameters = "echo_numbers accepts: round_cap (integer, optional), ratio (number, optional).";

    private readonly McpServerHarness _server;

    public ArgumentBindingAgreementTests(TestOnlyToolsServer fixture)
    {
        _server = fixture.Harness;
    }

    [Theory]
    [InlineData("2.5", "2.5")]
    [InlineData("\"2.5\"", "2.5")]
    [InlineData("\"+2.5\"", "2.5")]
    [InlineData("\".5\"", "0.5")]
    [InlineData("\"5.\"", "5")]
    [InlineData("\"1E5\"", "100000")]
    [InlineData("\"-1e-5\"", "-1E-05")]
    public async Task CallTool_NumberTheBinderReads_IsAcceptedAndBound(string ratioJson, string bound)
    {
        // The guard must not refuse what binds: that would cost the model a retry for a call that was fine.
        var result = await _server.CallToolJsonAsync("echo_numbers", $$"""{"ratio":{{ratioJson}}}""");

        Assert.Equal($"round_cap=0; ratio={bound}", _server.SuccessText(result));
    }

    [Theory]
    // Each of these passes double.TryParse and is refused by the binder.
    [InlineData("\"2.5\\u0000\"")]
    [InlineData("\"nan\"")]
    [InlineData("\"1e400\"")]
    // Refused by both; pinned so a looser parser here cannot start letting them through.
    [InlineData("\" 2.5\"")]
    [InlineData("\"2,5\"")]
    [InlineData("\"\\u0663\"")]
    // The binder would take these, but no D&D quantity is NaN or infinite; refusing costs one retry, accepting would
    // carry NaN into the maths.
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    public async Task CallTool_UnreadableOrNonFiniteNumberString_GuardNamesTheArgument(string ratioJson)
    {
        var result = await _server.CallToolJsonAsync("echo_numbers", $$"""{"ratio":{{ratioJson}}}""");

        Assert.Equal(
            Prefix + $"Invalid arguments: argument 'ratio' should be number but was the string \"{JsonString(ratioJson)}\". " + AcceptedParameters,
            _server.ErrorText(result));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("\"7\"")]
    public async Task CallTool_RenamedIntegerParameter_BindsUnderItsSchemaName(string roundCapJson)
    {
        // Proves the premise of the range test below: the model's name for the parameter is round_cap, not roundCap.
        var result = await _server.CallToolJsonAsync("echo_numbers", $$"""{"round_cap":{{roundCapJson}}}""");

        Assert.Equal("round_cap=7; ratio=0", _server.SuccessText(result));
    }

    [Theory]
    [InlineData("99999999999", "the number 99999999999", "large")]
    [InlineData("\"-2147483649\"", "the string \"-2147483649\"", "small")]
    public async Task CallTool_RenamedIntegerOutsideItsType_GuardNamesTheArgument(string roundCapJson, string described, string direction)
    {
        // Keyed by the C# name, the guard's range lookup misses round_cap and this falls through to the binder's
        // detail-free error.
        var result = await _server.CallToolJsonAsync("echo_numbers", $$"""{"round_cap":{{roundCapJson}}}""");

        Assert.Equal(
            Prefix + $"Invalid arguments: argument 'round_cap' was {described}, which is too {direction} to be valid. " + AcceptedParameters,
            _server.ErrorText(result));
    }

    // The decoded string, so a test name never carries a raw control character (a NUL breaks TRX output).
    private static string JsonString(string json) => JsonDocument.Parse(json).RootElement.GetString()!;
}
