using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using ModelContextProtocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: every mistake a caller can fix comes back as a tool error (IsError = true) whose text is
/// "An error occurred invoking '&lt;tool&gt;': " followed by a message that names what was wrong and what is
/// accepted — never the SDK's bare "An error occurred invoking '&lt;tool&gt;'." with nothing after it.
///
/// <para>
/// The model's only way to recover from a bad call is to read this text and retry. Two pieces of host code
/// make it actionable, and both fail silently: the call-tool filter that translates <see cref="DndInputException"/>
/// (drop it and every domain message vanishes), and <c>ToolArgumentGuard</c> (drop it and missing or mistyped
/// arguments die in the SDK's binder). The expected texts here are exact so either regression fails a test.
/// </para>
/// </summary>
public sealed partial class ToolErrorTests : IClassFixture<McpServerHarness>
{
    private const string Prefix = "An error occurred invoking 'dice_roll': ";

    private const string AcceptedParameters =
        "dice_roll accepts: expression (string, required), times (integer, optional), label (string, optional), seed (integer, optional).";

    private readonly McpServerHarness _server;

    public ToolErrorTests(McpServerHarness server)
    {
        _server = server;
    }

    [GeneratedRegex(@"^#\d+: \*\*-?\d+\*\*", RegexOptions.Multiline)]
    private static partial Regex NumberedResultLineRegex();

    [Theory]
    [InlineData("2d6 3")]
    [InlineData("2 d6")]
    [InlineData("banana")]
    [InlineData("2d6d4")]
    [InlineData("1d20+")]
    [InlineData("0d6")]
    [InlineData("1001d6")]
    [InlineData("4d6kh5")]
    [InlineData("1d6r<=6")]
    [InlineData("8d6!>=30")]
    [InlineData("")]
    public async Task CallTool_DomainInputError_ReturnsDomainMessageVerbatimAfterPrefix(string expression)
    {
        var result = await _server.Client.CallToolAsync("dice_roll", new Dictionary<string, object?> { ["expression"] = expression });

        Assert.Equal(Prefix + DomainMessageFor(expression), _server.ErrorText(result));
    }

    [Theory]
    [InlineData("2d6 3")]
    [InlineData("8d6!>=30")]
    [InlineData("1d20>=5>=3")]
    public async Task CallTool_DiceOddsInputError_ReturnsTheSameDomainMessage(string expression)
    {
        // Both dice tools share one parser; the model must get the same correction from either.
        var result = await _server.Client.CallToolAsync("dice_odds", new Dictionary<string, object?> { ["expression"] = expression });

        Assert.Equal("An error occurred invoking 'dice_odds': " + DomainMessageFor(expression), _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_DiceOddsMissingExpression_NamesItAndListsAcceptedParameters()
    {
        var result = await _server.CallToolJsonAsync("dice_odds", "{}");

        Assert.Equal(
            "An error occurred invoking 'dice_odds': Invalid arguments: missing required argument 'expression'. dice_odds accepts: expression (string, required).",
            _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""{"expression":"1d6","seed":"abc"}""", "argument 'seed' should be integer or null but was the string \"abc\"")]
    [InlineData("""{"expression":"1d6","seed":1.5}""", "argument 'seed' should be integer or null but was the number 1.5")]
    [InlineData("""{"expression":"1d6","label":7}""", "argument 'label' should be string or null but was the number 7")]
    public async Task CallTool_WrongTypeForOptionalParameter_NamesIt(string argumentsJson, string problem)
    {
        var result = await _server.CallToolJsonAsync("dice_roll", argumentsJson);

        Assert.Equal(Prefix + "Invalid arguments: " + problem + ". " + AcceptedParameters, _server.ErrorText(result));
    }

    [Theory]
    [InlineData("99999999999999999999", "the number 99999999999999999999", "large")]
    [InlineData("-9223372036854775809", "the number -9223372036854775809", "small")]
    [InlineData("\"99999999999999999999\"", "the string \"99999999999999999999\"", "large")]
    // Beyond decimal as well: still an integer, still "too large", never the binder's detail-free error.
    [InlineData("1000000000000000000000000000000000", "the number 1000000000000000000000000000000000", "large")]
    [InlineData("-1000000000000000000000000000000000", "the number -1000000000000000000000000000000000", "small")]
    public async Task CallTool_SeedBeyondLong_SaysTooLargeNotWrongType(string seedJson, string described, string direction)
    {
        var result = await _server.CallToolJsonAsync("dice_roll", $$"""{"expression":"1d6","seed":{{seedJson}}}""");

        Assert.Equal(
            Prefix + $"Invalid arguments: argument 'seed' was {described}, which is too {direction} to be valid. " + AcceptedParameters,
            _server.ErrorText(result));
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-9223372036854775808")]
    [InlineData("null")]
    public async Task CallTool_SeedAtLongBoundsOrNull_IsAccepted(string seedJson)
    {
        var result = await _server.CallToolJsonAsync("dice_roll", $$"""{"expression":"1d6","seed":{{seedJson}}}""");

        Assert.StartsWith("**", _server.SuccessText(result), StringComparison.Ordinal);
    }

    [Theory]
    // "|" stands for a newline, decoded below, so no test name carries a raw control character.
    [InlineData("line one|line two")]
    [InlineData("A label that is far too long for anyone to want it shown next to their roll, repeated to pass the limit of one hundred")]
    public async Task CallTool_UnusableLabel_SaysWhatALabelMayBe(string label)
    {
        var result = await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = "1d6", ["label"] = label.Replace('|', '\n') });

        Assert.Equal(Prefix + "label must be one line of at most 100 characters, e.g. \"Stealth\".", _server.ErrorText(result));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"times":2}""")]
    public async Task CallTool_MissingRequiredArgument_NamesItAndListsAcceptedParameters(string argumentsJson)
    {
        var result = await _server.CallToolJsonAsync("dice_roll", argumentsJson);

        Assert.Equal(
            Prefix + "Invalid arguments: missing required argument 'expression'. " + AcceptedParameters,
            _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""{"expression":5}""", "argument 'expression' should be string but was the number 5")]
    [InlineData("""{"expression":null}""", "argument 'expression' should be string but was null")]
    [InlineData("""{"expression":["1d6"]}""", "argument 'expression' should be string but was an array")]
    [InlineData("""{"expression":{"dice":"1d6"}}""", "argument 'expression' should be string but was an object")]
    [InlineData("""{"expression":"1d6","times":"three"}""", "argument 'times' should be integer but was the string \"three\"")]
    [InlineData("""{"expression":"1d6","times":true}""", "argument 'times' should be integer but was the boolean true")]
    [InlineData("""{"expression":"1d6","times":2.5}""", "argument 'times' should be integer but was the number 2.5")]
    [InlineData("""{"expression":"1d6","times":"2.5"}""", "argument 'times' should be integer but was the string \"2.5\"")]
    // System.Text.Json refuses whitespace around a numeric string; the guard must refuse it too, or the call
    // reaches the binder and the model gets the bare generic error.
    [InlineData("""{"expression":"1d6","times":" 3"}""", "argument 'times' should be integer but was the string \" 3\"")]
    [InlineData("""{"expression":"1d6","times":"3 "}""", "argument 'times' should be integer but was the string \"3 \"")]
    public async Task CallTool_WrongJsonType_NamesArgumentAndExpectedType(string argumentsJson, string problem)
    {
        var result = await _server.CallToolJsonAsync("dice_roll", argumentsJson);

        Assert.Equal(Prefix + "Invalid arguments: " + problem + ". " + AcceptedParameters, _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""{"expression":"1d6","times":99999999999}""", "the number 99999999999", "large")]
    [InlineData("""{"expression":"1d6","times":2147483648}""", "the number 2147483648", "large")]
    [InlineData("""{"expression":"1d6","times":"-2147483649"}""", "the string \"-2147483649\"", "small")]
    public async Task CallTool_IntegerOutsideParameterType_NamesArgumentWithoutClrBounds(string argumentsJson, string described, string direction)
    {
        // "integer" in the schema covers int and long alike, so only the C# parameter type knows 99999999999 cannot
        // bind to `int times`. Without the guard's range check this is the generic, detail-free error. The text must
        // not quote int's bounds either: the model would read them as the accepted range, which for times is 1-100.
        var result = await _server.CallToolJsonAsync("dice_roll", argumentsJson);

        Assert.Equal(
            Prefix + $"Invalid arguments: argument 'times' was {described}, which is too {direction} to be valid. " + AcceptedParameters,
            _server.ErrorText(result));
    }

    [Theory]
    // long.TryParse skips trailing NULs; System.Text.Json does not. Accepted by the guard, this reached the binder and
    // came back as the bare generic error.
    [InlineData("\"3\\u0000\"")]
    [InlineData("\"-2147483649\\u0000\"")]
    [InlineData("\"3\\n\"")]
    [InlineData("\"\\u0663\"")]
    [InlineData("\"1e2\"")]
    public async Task CallTool_IntegerStringTheBinderRefuses_GuardNamesTheArgument(string timesJson)
    {
        var result = await _server.CallToolJsonAsync("dice_roll", $$"""{"expression":"1d6","times":{{timesJson}}}""");

        // Decoded here rather than written into [InlineData], so no test name carries a raw NUL.
        var sent = System.Text.Json.JsonDocument.Parse(timesJson).RootElement.GetString();
        Assert.Equal(
            Prefix + $"Invalid arguments: argument 'times' should be integer but was the string \"{sent}\". " + AcceptedParameters,
            _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""{"expression":"1d6","dice":2}""", "unknown argument 'dice'")]
    [InlineData("""{"expr":"1d6"}""", "missing required argument 'expression'; unknown argument 'expr'")]
    public async Task CallTool_UnknownArgument_NamesItAndListsAcceptedParameters(string argumentsJson, string problems)
    {
        var result = await _server.CallToolJsonAsync("dice_roll", argumentsJson);

        Assert.Equal(Prefix + "Invalid arguments: " + problems + ". " + AcceptedParameters, _server.ErrorText(result));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("+3")]
    [InlineData("03")]
    public async Task CallTool_NumericStringForInteger_IsAcceptedAndHonoured(string times)
    {
        // Models often quote numbers. The binder accepts "3"; refusing it in the guard would break calls that work.
        var result = await _server.CallToolJsonAsync("dice_roll", $$"""{"expression":"1d6","times":"{{times}}"}""");

        Assert.Equal(3, NumberedResultLineRegex().Matches(_server.SuccessText(result)).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task CallTool_TimesOutOfRange_ReturnsAcceptedRange(int times)
    {
        var result = await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = "1d6", ["times"] = times });

        Assert.Equal(Prefix + $"times must be between 1 and 100 (got {times}).", _server.ErrorText(result));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task CallTool_TimesAtRangeBoundary_Succeeds(int times)
    {
        var result = await _server.Client.CallToolAsync(
            "dice_roll", new Dictionary<string, object?> { ["expression"] = "1d6", ["times"] = times });

        var text = _server.SuccessText(result);

        // A single roll is not numbered; several are "#1: ", "#2: ", ...
        Assert.Equal(times == 1 ? 0 : times, NumberedResultLineRegex().Matches(text).Count);
    }

    [Fact]
    public async Task CallTool_ExpressionOverLengthLimit_ReturnsDomainMessageNotAWrappedTotal()
    {
        // 2,148 terms of +1000000 overflowed the Phase 0 stub's int total and came back as a confident negative roll.
        var expression = "1000000" + string.Concat(Enumerable.Repeat("+1000000", 2147));

        var result = await _server.Client.CallToolAsync("dice_roll", new Dictionary<string, object?> { ["expression"] = expression });

        Assert.Equal(Prefix + DomainMessageFor(expression), _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_UnknownTool_IsProtocolErrorNamingTheTool()
    {
        // Unlike bad arguments, an unknown tool is a JSON-RPC InvalidParams error, not a tool result. Pinned so a
        // client-side change in how that surfaces is noticed.
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => _server.Client.CallToolAsync("no_such_tool").AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Contains("no_such_tool", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_AfterToolError_SessionKeepsServingCalls()
    {
        var bad = await _server.Client.CallToolAsync("dice_roll", new Dictionary<string, object?> { ["expression"] = "2d6 3" });
        _server.ErrorText(bad);

        var good = await _server.Client.CallToolAsync("dice_roll", new Dictionary<string, object?> { ["expression"] = "1d6" });
        Assert.StartsWith("**", _server.SuccessText(good), StringComparison.Ordinal);
    }

    /// <summary>
    /// The message Domain itself produces for <paramref name="expression"/>. Asking Domain (rather than copying its
    /// text here) makes the test pin the pass-through — "the model sees the domain message verbatim" — instead of
    /// the wording, which DndMcp.Tests owns.
    /// </summary>
    private static string DomainMessageFor(string expression) =>
        Assert.Throws<DndInputException>(() => DiceExpression.Parse(expression)).Message;
}
