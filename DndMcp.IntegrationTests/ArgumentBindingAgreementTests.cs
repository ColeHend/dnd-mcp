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
/// <para>
/// Array parameters add one more gap: the binder reads every item with the element type's converter, so
/// <c>kinds: ["spell", 3]</c> fails inside it exactly like a wrong top-level type. rules_search's <c>kinds</c> is the
/// first array parameter, and the fixture's server includes the production tools, so it is pinned here directly.
/// </para>
/// </summary>
public sealed class ArgumentBindingAgreementTests : IClassFixture<TestOnlyToolsServer>
{
    private const string Prefix = "An error occurred invoking 'echo_numbers': ";

    private const string AcceptedParameters = "echo_numbers accepts: round_cap (integer, optional), ratio (number, optional).";

    private const string SearchPrefix = "An error occurred invoking 'rules_search': ";

    private const string SearchParameters =
        "rules_search accepts: query (string, required), edition (string, optional), kinds (array of string, optional), limit (integer, optional).";

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

    [Theory]
    [InlineData("""["spell"]""")]
    [InlineData("""["Spells","MAGIC ITEM"]""")]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    public async Task CallTool_KindsArrayTheGuardAccepts_Binds(string kindsJson)
    {
        var result = await _server.CallToolJsonAsync("rules_search", $$"""{"query":"fire","kinds":{{kindsJson}}}""");

        Assert.StartsWith("**", _server.SuccessText(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rules_search", """{"query":"fireball","edition":null}""", "`2024/spell/fireball`")]
    [InlineData("rules_search", """{"query":"fireball","edition":null,"kinds":null}""", "`2024/spell/fireball`")]
    [InlineData("rules_get", """{"name":"Fireball","edition":null,"format":null,"kind":null,"ref":null}""", "`2024/spell/fireball`")]
    public async Task CallTool_NullForAnOptionalString_MeansTheDefault(string tool, string argumentsJson, string expected)
    {
        // Models send null for "use the default". The binder takes it, so the guard must too, and the tool must read it
        // as the default rather than as a value to refuse.
        var result = await _server.CallToolJsonAsync(tool, argumentsJson);

        Assert.Contains(expected, _server.SuccessText(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_NullKindsItem_BindsAndGetsTheToolsOwnMessage()
    {
        // The schema's items allow null (a nullable reference type), so the guard lets it through; the binder must then
        // accept it too, which the tool's own "empty kind" message (not the generic error) proves.
        var result = await _server.CallToolJsonAsync("rules_search", """{"query":"fire","kinds":["spell",null]}""");

        Assert.StartsWith(SearchPrefix + "The kind is empty. Kinds in the 2024 SRD: ", _server.ErrorText(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""["spell",3]""", "argument 'kinds' item 2 should be string but was the number 3")]
    [InlineData("""[true]""", "argument 'kinds' item 1 should be string but was the boolean true")]
    [InlineData("""[["spell"]]""", "argument 'kinds' item 1 should be string but was an array")]
    [InlineData("""[{"kind":"spell"}]""", "argument 'kinds' item 1 should be string but was an object")]
    [InlineData("""["spell",1,"monster",2]""",
        "argument 'kinds' item 2 should be string but was the number 1; argument 'kinds' item 4 should be string but was the number 2")]
    // Exactly one past the five reported: the count line must still appear for a single unreported item.
    [InlineData("""[1,2,3,4,5,6]""",
        "argument 'kinds' item 1 should be string but was the number 1; argument 'kinds' item 2 should be string but was the number 2; " +
        "argument 'kinds' item 3 should be string but was the number 3; argument 'kinds' item 4 should be string but was the number 4; " +
        "argument 'kinds' item 5 should be string but was the number 5; argument 'kinds' has 1 more item(s) of the wrong type")]
    [InlineData("""[1,2,3,4,5,6,7]""",
        "argument 'kinds' item 1 should be string but was the number 1; argument 'kinds' item 2 should be string but was the number 2; " +
        "argument 'kinds' item 3 should be string but was the number 3; argument 'kinds' item 4 should be string but was the number 4; " +
        "argument 'kinds' item 5 should be string but was the number 5; argument 'kinds' has 2 more item(s) of the wrong type")]
    [InlineData("\"spell\"", "argument 'kinds' should be array or null but was the string \"spell\"")]
    public async Task CallTool_KindsArrayTheBinderRefuses_GuardNamesTheItem(string kindsJson, string problems)
    {
        var result = await _server.CallToolJsonAsync("rules_search", $$"""{"query":"fire","kinds":{{kindsJson}}}""");

        Assert.Equal(SearchPrefix + "Invalid arguments: " + problems + ". " + SearchParameters, _server.ErrorText(result));
    }

    // The decoded string, so a test name never carries a raw control character (a NUL breaks TRX output).
    private static string JsonString(string json) => JsonDocument.Parse(json).RootElement.GetString()!;

    private const string EchoShapePrefix = "An error occurred invoking 'echo_shape': Invalid arguments: ";

    // Object arguments and objects nested in arrays (echo_shape, test-only): a required field, a field only the binder can
    // check (DateOnly), and a nested array. encounter_difficulty's monsters reaches the item-field path; these reach the
    // rest, which Phase 4's build objects will use.
    [Theory]
    [InlineData("""{"shape":{"when":"2024-01-01"}}""", "argument 'shape' is missing required field 'name'")]
    [InlineData("""{"shape":{"name":"a","when":"not a date"}}""", "argument 'shape' field 'when' could not be read as the tool expects")]
    [InlineData("""{"shape":{"name":"a"},"more":[{"name":"b"},{"name":"c","when":"x"}]}""", "argument 'more' item 2 field 'when' could not be read as the tool expects")]
    [InlineData("""{"shape":{"name":"a","tags":["x",3]}}""", "argument 'shape' field 'tags' item 2 should be string but was the number 3")]
    public async Task CallTool_ObjectArgument_GuardNamesTheField(string arguments, string problem)
    {
        var text = _server.ErrorText(await _server.CallToolJsonAsync("echo_shape", arguments));

        Assert.StartsWith(EchoShapePrefix + problem + ". echo_shape accepts:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_WellFormedObject_Binds()
    {
        var text = _server.SuccessText(await _server.CallToolJsonAsync("echo_shape", """{"shape":{"name":"a","when":"2024-01-01","tags":["x"]},"more":[{"name":"b"}]}"""));

        Assert.Equal("name=a; more=1", text);
    }

    // A parameter published untyped with another parameter's shape (SameShapeAsAttribute, balance_compare's variant) gets
    // every check the typed one gets: unknown and missing fields, JSON types, nested items, and the test-deserialize
    // backstop as the other parameter's CLR type, which only a DateOnly field can reach here.
    [Theory]
    [InlineData("""{"shape":{"name":"a"},"copy":{"name":"b","colour":"red"}}""", "argument 'copy' has unknown field 'colour' (fields: name, when, tags)")]
    [InlineData("""{"shape":{"name":"a"},"copy":{"when":"2024-01-01"}}""", "argument 'copy' is missing required field 'name'")]
    [InlineData("""{"shape":{"name":"a"},"copy":{"name":"b","tags":["x",3]}}""", "argument 'copy' field 'tags' item 2 should be string but was the number 3")]
    [InlineData("""{"shape":{"name":"a"},"copy":{"name":"b","when":"not a date"}}""", "argument 'copy' field 'when' could not be read as the tool expects")]
    [InlineData("""{"shape":{"name":"a"},"copy":"b"}""", "argument 'copy' should be object but was the string \"b\"")]
    [InlineData("""{"shape":{"name":"a"},"copy":[{"name":"b"}]}""", "argument 'copy' should be object but was an array")]
    public async Task CallTool_SameShapeArgument_GuardChecksItAsTheOtherParameter(string arguments, string problem)
    {
        var text = _server.ErrorText(await _server.CallToolJsonAsync("echo_shape", arguments));

        Assert.StartsWith(EchoShapePrefix + problem + ". echo_shape accepts:", text, StringComparison.Ordinal);
        Assert.EndsWith(", copy (object, optional).", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"shape":{"name":"a"},"copy":{"name":"b","when":"2024-01-01"}}""", "name=a; more=0; copy=b")]
    [InlineData("""{"shape":{"name":"a"},"copy":null}""", "name=a; more=0")]
    public async Task CallTool_SameShapeArgumentTheGuardAccepts_Binds(string arguments, string bound)
    {
        Assert.Equal(bound, _server.SuccessText(await _server.CallToolJsonAsync("echo_shape", arguments)));
    }

    private const string Fighter =
        """{"name":"F","level":5,"abilities":{"str":18},"attacks":[{"name":"Greatsword","count":2,"damage":"2d6","damage_type":"slashing","properties":["melee","heavy","two-handed"]}]}""";

    [Theory]
    // Numbers quoted inside a build bind like quoted top-level numbers (AllowReadingFromString), step values included.
    [InlineData("""{"name":"F","level":"5","abilities":{"str":"18"},"attacks":[{"name":"Greatsword","count":"2","damage":"2d6","damage_type":"slashing","properties":["melee","heavy","two-handed"],"to_hit":{"bonus":"0"}}]}""")]
    // A step map with a quoted value.
    [InlineData("""{"name":"F","level":5,"abilities":{"str":{"1":"18"}},"attacks":[{"name":"Greatsword","count":{"1":2},"damage":"2d6","damage_type":"slashing","properties":["melee","heavy","two-handed"]}]}""")]
    public async Task CallTool_BalanceBuildWithQuotedNumbers_BindsToTheSameBuild(string build)
    {
        var quoted = _server.SuccessText(await _server.CallToolJsonAsync("balance_dpr", $$"""{"build":{{build}},"target":{"ac":"15"},"horizon":"round1"}"""));
        var plain = _server.SuccessText(await _server.CallToolJsonAsync("balance_dpr", $$"""{"build":{{Fighter}},"target":{"ac":15},"horizon":"round1"}"""));

        Assert.Equal(plain, quoted);
        Assert.Contains("**15.00** damage per round at level 5 against AC 15", plain, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("balance_dpr", "build", ""","target":null,"levels":null,"ac_range":null,"horizon":null,"rounds":null,"rest_preset":null,"encounters_per_day":null,"short_rests":null,"rulings":null""")]
    [InlineData("balance_dpr", "build", ""","levels":[],"ac_range":[]""")]
    [InlineData("balance_compare", "baseline", ""","feature":{"name":"Lucky","modifiers":[{"kind":"lucky"}]},"variant":null,"target":null,"levels":null,"horizon":null,"rounds":null,"rest_preset":null,"encounters_per_day":null,"short_rests":null,"rulings":null""")]
    public async Task CallTool_BalanceNullOrEmptyOptionalArguments_MeanTheDefaults(string tool, string buildArgument, string rest)
    {
        // Models send null (or an empty list) for "use the default"; each must bind and read as the default: a 3-round
        // fight, the build's own level, the CR = level target, the 2014 DMG day, every ruling off.
        var text = _server.SuccessText(await _server.CallToolJsonAsync(tool, $$"""{"{{buildArgument}}":{{Fighter}}{{rest}}}"""));

        Assert.Contains("at level 5 against AC 15 — a 3-round fight (the mean per round)", text, StringComparison.Ordinal);
        Assert.Contains("2014 DMG: 6–8 encounters, 2 short rests", text, StringComparison.Ordinal);
    }
}
