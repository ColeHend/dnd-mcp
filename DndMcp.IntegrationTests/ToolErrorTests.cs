using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Features;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
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
/// <para>
/// A third translation is the campaign tools' own: <see cref="CampaignStoreUnavailableException"/>, campaigns.db unusable
/// for a reason the user can fix (from a newer dnd-mcp, locked, read-only, damaged), whose message names the file and what
/// to do. The call-tool filter translates it for the tools, and a get-prompt and a read-resource filter repeat that for
/// the prompts and the campaign:// resources, which the call-tool filter never sees. Each fails silently too (the SDK's
/// bare error, and the user is never told to update), so each is pinned here against a campaigns.db from a newer version.
/// The same message is owed when a read statement, not the open, meets the failure (a damaged page, another process's
/// lock past the busy timeout): the filters map such a SqliteException through the store's own mapping, for the campaign
/// tools, the prompts and the campaign:// resources only (any other tool's SQLite failure is srd.db's), pinned against a
/// campaigns.db with one damaged table page.
/// </para>
/// <para>
/// The class fixture's server has no campaigns (and must never make campaigns.db); the campaign rows that need one to
/// exist, to get past "There are no campaigns yet" to a tool's own checks, use <see cref="OneCampaignServer"/>, and the
/// unusable-store rows use <see cref="NewerCampaignsDatabaseServer"/> and <see cref="DamagedCampaignsDatabaseServer"/>.
/// </para>
/// </summary>
public sealed partial class ToolErrorTests : IClassFixture<McpServerHarness>, IClassFixture<OneCampaignServer>, IClassFixture<NewerCampaignsDatabaseServer>,
    IClassFixture<DamagedCampaignsDatabaseServer>
{
    private const string Prefix = "An error occurred invoking 'dice_roll': ";

    private const string AcceptedParameters =
        "dice_roll accepts: expression (string, required), times (integer, optional), label (string, optional), secret (boolean, optional), " +
        "seed (integer, optional).";

    private const string RulesSearchParameters =
        "rules_search accepts: query (string, required), edition (string, optional), kinds (array of string, optional), limit (integer, optional).";

    private const string RulesGetParameters =
        "rules_get accepts: ref (string, optional), name (string, optional), kind (string, optional), edition (string, optional), format (string, optional).";

    private readonly McpServerHarness _server;
    private readonly McpServerHarness _campaign;
    private readonly NewerCampaignsDatabaseServer _newer;
    private readonly DamagedCampaignsDatabaseServer _damaged;

    public ToolErrorTests(McpServerHarness server, OneCampaignServer campaign, NewerCampaignsDatabaseServer newer, DamagedCampaignsDatabaseServer damaged)
    {
        _server = server;
        _campaign = campaign.Harness;
        _newer = newer;
        _damaged = damaged;
    }

    [GeneratedRegex(@"^#\d+: \*\*-?\d+\*\*", RegexOptions.Multiline)]
    private static partial Regex NumberedResultLineRegex();

    // A run of the 'x' a huge argument is made of: how much of it an error echoed.
    [GeneratedRegex("x+")]
    private static partial Regex EchoRunRegex();

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

    [Theory]
    [InlineData("rules_search", "{}", "missing required argument 'query'", RulesSearchParameters)]
    [InlineData("rules_search", """{"query":["fire"]}""", "argument 'query' should be string but was an array", RulesSearchParameters)]
    // A model often writes the edition as a number; the binder will not turn 2024 into "2024", so the guard must say so.
    [InlineData("rules_search", """{"query":"fire","edition":2024}""", "argument 'edition' should be string or null but was the number 2024", RulesSearchParameters)]
    [InlineData("rules_search", """{"query":"fire","limit":"ten"}""", "argument 'limit' should be integer but was the string \"ten\"", RulesSearchParameters)]
    [InlineData("rules_get", """{"name":"Fireball","edition":2014}""", "argument 'edition' should be string or null but was the number 2014", RulesGetParameters)]
    [InlineData("rules_get", """{"name":{"en":"Fireball"}}""", "argument 'name' should be string or null but was an object", RulesGetParameters)]
    [InlineData("rules_get", """{"reference":"spell/fireball"}""", "unknown argument 'reference'", RulesGetParameters)]
    public async Task CallTool_RulesToolArgumentOfTheWrongShape_NamesItAndListsAcceptedParameters(
        string tool, string argumentsJson, string problem, string accepted)
    {
        var result = await _server.CallToolJsonAsync(tool, argumentsJson);

        Assert.Equal($"An error occurred invoking '{tool}': Invalid arguments: {problem}. {accepted}", _server.ErrorText(result));
    }

    private const string BalanceDprParameters =
        "balance_dpr accepts: build (object, required), target (object, optional), levels (array of integer, optional), " +
        "ac_range (array of integer, optional), horizon (string, optional), rounds (integer, optional), rest_preset (string, " +
        "optional), encounters_per_day (number, optional), short_rests (integer, optional), rulings (object, optional).";

    private const string BalanceCompareParameters =
        "balance_compare accepts: baseline (object, required), variant (object, optional), feature (object, optional), target " +
        "(object, optional), levels (array of integer, optional), horizon (string, optional), rounds (integer, optional), " +
        "rest_preset (string, optional), encounters_per_day (number, optional), short_rests (integer, optional), rulings " +
        "(object, optional).";

    private const string Fighter =
        """{"name":"F","level":5,"abilities":{"str":18},"attacks":[{"name":"Greatsword","count":2,"damage":"2d6","damage_type":"slashing"}]}""";

    [Theory]
    [InlineData("balance_dpr", "{}", "missing required argument 'build'", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":"a fighter"}""", "argument 'build' should be object but was the string \"a fighter\"", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5,"attacks":[{"name":"A","damage":"1d8","cuont":2}]}}""",
        "argument 'build' field 'attacks' item 1 has unknown field 'cuont' (fields: name, count, action, to_hit, damage, damage_type, " +
        "ability_to_damage, properties, offhand, mastery, cantrip, from_level, until_level)", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":"five"}}""", "argument 'build' field 'level' should be integer but was the string \"five\"", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5,"attacks":[{"name":"A","damage":"1d8","to_hit":{"bonus":99999999999}}]}}""",
        "argument 'build' field 'attacks' item 1 field 'to_hit' field 'bonus' was the number 99999999999, which is too large to be valid", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5,"modifiers":[{"kind":"lucky","attacks":"A"}]}}""",
        "argument 'build' field 'modifiers' item 1 field 'attacks' should be array but was the string \"A\"", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5},"target":{"armor":15}}""",
        "argument 'target' has unknown field 'armor' (fields: monster, ac, cr, profile, save_bonus, saves, hp, resistances, vulnerabilities, immunities, " +
        "magic_resistance, evasion, condition, cover, legendary_resistance, save_dice, second_target_rate)", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5},"rulings":{"hew_gets_pb":"yes"}}""",
        "argument 'rulings' field 'hew_gets_pb' should be boolean but was the string \"yes\"", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5},"levels":[5,"ten"]}""", "argument 'levels' item 2 should be integer but was the string \"ten\"", BalanceDprParameters)]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5},"encounters_per_day":"many"}""",
        "argument 'encounters_per_day' should be number or null but was the string \"many\"", BalanceDprParameters)]
    [InlineData("balance_compare", """{"feature":{"name":"X"}}""", "missing required argument 'baseline'", BalanceCompareParameters)]
    [InlineData("balance_compare", """{"baseline":{"name":"F","level":5},"variant":{"name":"G","level":5,"attacks":[{"name":"A","cuont":2}]}}""",
        "argument 'variant' field 'attacks' item 1 has unknown field 'cuont' (fields: name, count, action, to_hit, damage, damage_type, " +
        "ability_to_damage, properties, offhand, mastery, cantrip, from_level, until_level)", BalanceCompareParameters)]
    [InlineData("balance_compare", """{"baseline":{"name":"F","level":5},"variant":"F with GWM"}""",
        "argument 'variant' should be object but was the string \"F with GWM\"", BalanceCompareParameters)]
    [InlineData("balance_compare", """{"baseline":{"name":"F","level":5},"feature":{"name":"X","modifiers":[{"knd":"lucky"}]}}""",
        "argument 'feature' field 'modifiers' item 1 has unknown field 'knd'", BalanceCompareParameters)]
    public async Task CallTool_BalanceArgumentOfTheWrongShape_NamesTheFieldAndListsAcceptedParameters(
        string tool, string argumentsJson, string problem, string accepted)
    {
        var text = _server.ErrorText(await _server.CallToolJsonAsync(tool, argumentsJson));

        // The field list of a modifier is long; the part that names the problem and the parameter list are pinned.
        Assert.StartsWith($"An error occurred invoking '{tool}': Invalid arguments: {problem}", text, StringComparison.Ordinal);
        Assert.EndsWith(". " + accepted, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"kind":"smite"}""")]
    [InlineData("""{"kind":"extra_damage","dice":"2d8kh1"}""")]
    [InlineData("""{"kind":"to_hit","type":"fire","amount":1}""")]
    public async Task CallTool_BalanceDslError_ReturnsTheDomainMessageVerbatim(string modifier)
    {
        // The DSL's validators own every message (DndMcp.Tests pins the wording); the tool must pass it through untouched.
        var build = $$"""{"name":"F","level":5,"abilities":{"str":18},"attacks":[{"name":"Greatsword","count":2,"damage":"2d6","damage_type":"slashing"}],"modifiers":[{{modifier}}]}""";
        var expected = Assert.Throws<DndInputException>(() =>
            BuildResolver.Resolve(DslJson.Deserialize<BuildSpec>(build, "build"), (IReadOnlyList<int>?)null, null, "build")).Message;

        var result = await _server.CallToolJsonAsync("balance_dpr", $$"""{"build":{{build}}}""");

        Assert.Equal("An error occurred invoking 'balance_dpr': " + expected, _server.ErrorText(result));
    }

    [Fact]
    public async Task CallTool_BalanceToolsAfterAnError_KeepServing()
    {
        _server.ErrorText(await _server.CallToolJsonAsync("balance_compare", $$"""{"baseline":{{Fighter}}}"""));

        var good = _server.SuccessText(await _server.CallToolJsonAsync("balance_dpr", $$"""{"build":{{Fighter}}}"""));
        Assert.StartsWith("# Damage per round: F", good, StringComparison.Ordinal);
    }

    private const string BalanceSimulateParameters =
        "balance_simulate accepts: party (array of object, optional), enemies (array of object, optional), encounter (string, optional), " +
        "from_state (boolean, optional), campaign (string, optional), iterations (integer, " +
        "optional), seed (integer, optional), round_cap (integer, optional), edition (string, optional), surprise (string, " +
        "optional), enemy_hp (string, optional), precision (number, optional), replay (integer, optional), policies (object, " +
        "optional), compare (object, optional), rulings (object, optional).";

    private const string Ogre = """{"monster":"ogre"}""";

    [Theory]
    // party and enemies are optional (an encounter can give the sides): a call with one side and no encounter is the tool's
    // refusal, past the guard, naming what is missing and the fix.
    [InlineData("""{"party":[OGRE]}""", "enemies is missing")]
    [InlineData("""{"enemies":[OGRE]}""", "party is missing")]
    [InlineData("""{}""", "neither was given")]
    public async Task CallTool_BalanceSimulateWithoutBothSidesOrAnEncounter_IsTheToolsRefusalWithTheFix(string template, string missing)
    {
        var text = _server.ErrorText(await _server.CallToolJsonAsync("balance_simulate", template.Replace("OGRE", Ogre, StringComparison.Ordinal)));

        Assert.StartsWith(
            "An error occurred invoking 'balance_simulate': give party and enemies, or encounter (a stored fight: \"current\", \"last\" or its name); " +
            missing + ". Example: {\"party\": [{\"archetype\": \"fighter\"",
            text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "campaigns.db")), "The refusal reached campaigns.db.");
    }

    [Theory]
    // enemies is published untyped (party's shape) and checked by the guard exactly as party is.
    [InlineData("""{"party":[OGRE],"enemies":[{"monster":"ogre","cuont":3}]}""",
        "argument 'enemies' item 1 has unknown field 'cuont' (fields: name, monster, build, archetype, character, level, edition, hp, ac, " +
        "save_proficiencies, saves, initiative_bonus, position, count, death_saves)")]
    [InlineData("""{"party":[{"monster":"ogre","cuont":3}],"enemies":[OGRE]}""",
        "argument 'party' item 1 has unknown field 'cuont' (fields: name, monster, build, archetype, character, level, edition, hp, ac, " +
        "save_proficiencies, saves, initiative_bonus, position, count, death_saves)")]
    [InlineData("""{"party":[OGRE],"enemies":{"monster":"ogre"}}""", "argument 'enemies' should be array but was an object")]
    [InlineData("""{"party":[OGRE],"enemies":["ogre"]}""", "argument 'enemies' item 1 should be object but was the string \"ogre\"")]
    [InlineData("""{"party":[OGRE],"enemies":[{"monster":"ogre","count":"three"}]}""", "argument 'enemies' item 1 field 'count' should be integer but was the string \"three\"")]
    [InlineData("""{"party":[OGRE],"enemies":[{"monster":"ogre","build":{"name":"B","attacks":[{"name":"A","cuont":2}]}}]}""",
        "argument 'enemies' item 1 field 'build' field 'attacks' item 1 has unknown field 'cuont' (fields: name, count, action, to_hit, " +
        "damage, damage_type, ability_to_damage, properties, offhand, mastery, cantrip, from_level, until_level)")]
    // compare is published untyped (its feature is a build's worth of schema) and checked as CompareSpec.
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"compare":"GWM"}""", "argument 'compare' should be object but was the string \"GWM\"")]
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"compare":{"member":"one"}}""", "argument 'compare' field 'member' should be integer but was the string \"one\"")]
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"compare":{"membr":1}}""", "argument 'compare' has unknown field 'membr' (fields: member, feature)")]
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"compare":{"member":1,"feature":{"name":"X","modifiers":[{"knd":"lucky"}]}}}""",
        "argument 'compare' field 'feature' field 'modifiers' item 1 has unknown field 'knd'")]
    // seed is a ulong: 0 to 18446744073709551615, as a number or a decimal string.
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"seed":-1}""", "argument 'seed' was the number -1, which is too small to be valid")]
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"seed":"18446744073709551616"}""", "argument 'seed' was the string \"18446744073709551616\", which is too large to be valid")]
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"seed":1.5}""", "argument 'seed' should be integer or null but was the number 1.5")]
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"seed":"lucky"}""", "argument 'seed' should be integer or null but was the string \"lucky\"")]
    [InlineData("""{"party":[OGRE],"enemies":[OGRE],"policies":{"party":"focus_fire","enemy":"spread"}}""",
        "argument 'policies' has unknown field 'enemy' (fields: party, enemies, legendary_resistance, healing, finish_downed, pcs_win_ties)")]
    public async Task CallTool_BalanceSimulateArgumentOfTheWrongShape_NamesTheFieldAndListsAcceptedParameters(string template, string problem)
    {
        var text = _server.ErrorText(await _server.CallToolJsonAsync("balance_simulate", template.Replace("OGRE", Ogre, StringComparison.Ordinal)));

        Assert.StartsWith("An error occurred invoking 'balance_simulate': Invalid arguments: " + problem, text, StringComparison.Ordinal);
        Assert.EndsWith(". " + BalanceSimulateParameters, text, StringComparison.Ordinal);
    }

    [Theory]
    // HUGE is 100,000 characters. A monster name is echoed cut, and the lookup's close names do not grow with it.
    [InlineData("""{"party":[{"monster":"ogre"}],"enemies":[{"monster":"HUGE"}]}""")]
    [InlineData("""{"party":[{"monster":"ogre"}],"enemies":[{"monster":"ogre","name":"HUGE"}],"iterations":0}""")]
    [InlineData("""{"party":[{"monster":"ogre"}],"enemies":[{"monster":"ogre"}],"surprise":"HUGE"}""")]
    public async Task CallTool_HugeArgumentInASimulateError_IsEchoedShortened(string argumentsTemplate)
    {
        var argumentsJson = argumentsTemplate.Replace("HUGE", new string('x', 100_000), StringComparison.Ordinal);

        var error = _server.ErrorText(await _server.CallToolJsonAsync("balance_simulate", argumentsJson));

        Assert.True(error.Length < 1_500, $"{error.Length} characters: {error[..Math.Min(300, error.Length)]}");
        Assert.Contains("…", error, StringComparison.Ordinal);
    }

    [Theory]
    // HUGE is 100,000 characters, filled in at run time so no test name carries it.
    [InlineData("rules_get", """{"name":"Fireball","edition":"HUGE"}""")]
    [InlineData("rules_get", """{"name":"Fireball","format":"HUGE"}""")]
    [InlineData("rules_search", """{"query":"fireball","edition":"HUGE"}""")]
    // "monster" followed by 100,000 hyphens canonicalises to a real kind, so the kind/ref mismatch used to quote all of it.
    [InlineData("rules_get", """{"ref":"2024/spell/fireball","kind":"monsterDASHES"}""")]
    [InlineData("rules_get", """{"name":"Fireball","HUGE":1}""")]
    [InlineData("dice_roll", """{"expression":"1d6","HUGE":1}""")]
    public async Task CallTool_HugeArgumentInAnError_IsEchoedShortened(string tool, string argumentsTemplate)
    {
        // A pasted page as an edition, format, kind or argument name came back whole: 1 MB in gave 1 MB of error for the
        // model to read back.
        var argumentsJson = argumentsTemplate
            .Replace("HUGE", new string('x', 100_000), StringComparison.Ordinal)
            .Replace("DASHES", new string('-', 100_000), StringComparison.Ordinal);

        var error = _server.ErrorText(await _server.CallToolJsonAsync(tool, argumentsJson));

        Assert.True(error.Length < 600, $"{error.Length} characters: {error[..Math.Min(300, error.Length)]}");
        Assert.Contains("…", error, StringComparison.Ordinal);
    }

    [Theory]
    // HUGE is 100,000 characters. A build's messages list its fields or kinds and the tool's parameters, so the ceiling is
    // higher than for dice_roll; what matters is that the pasted text is cut, not echoed.
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5,"attacks":[{"name":"A","damage":"1d8"}]},"horizon":"HUGE"}""")]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5,"attacks":[{"name":"A","damage":"1d8","HUGE":1}]}}""")]
    [InlineData("balance_dpr", """{"build":{"name":"HUGE","level":5,"attacks":[{"name":"A","damage":"1d8"}]}}""")]
    [InlineData("balance_dpr", """{"build":{"name":"F","level":5,"attacks":[{"name":"A","damage":"1d8","damage_type":"HUGE"}]}}""")]
    [InlineData("balance_compare", """{"baseline":{"name":"F","level":5,"attacks":[{"name":"A","damage":"1d8"}]},"feature":{"name":"X","modifiers":[{"kind":"HUGE"}]}}""")]
    [InlineData("balance_compare", """{"baseline":{"name":"F","level":5,"attacks":[{"name":"A","damage":"1d8"}]},"rest_preset":"HUGE"}""")]
    public async Task CallTool_HugeArgumentInABalanceError_IsEchoedShortened(string tool, string argumentsTemplate)
    {
        var argumentsJson = argumentsTemplate.Replace("HUGE", new string('x', 100_000), StringComparison.Ordinal);

        var error = _server.ErrorText(await _server.CallToolJsonAsync(tool, argumentsJson));

        Assert.True(error.Length < 1_500, $"{error.Length} characters: {error[..Math.Min(300, error.Length)]}");
        Assert.Contains("…", error, StringComparison.Ordinal);
    }

    // What each campaign tool's guard refusal lists: every parameter in schema order, with its JSON type and whether it is
    // required. The model reads this list to repair a call, so a parameter renamed or retyped must be a deliberate diff here.
    private const string CampaignParameters =
        "campaign accepts: action (string, required), campaign (string, optional), name (string, optional), role (string, optional), " +
        "ruleset (string, optional), dm_name (string, optional), slug (string, optional), settings (object, optional), " +
        "party_name (string, optional), my_character (string, optional), status (string, optional), summary_md (string, optional), " +
        "current_location (string, optional), current_ingame (string, optional), perspective (string, optional), reason (string, optional), " +
        "session (integer, optional), dry_run (boolean, optional).";

    private const string CampaignSearchParameters =
        "campaign_search accepts: query (string, optional), kinds (array of string, optional), statuses (array of string, optional), " +
        "tags (array of string, optional), perspective (string, optional), include_facts (boolean, optional), as_of_session (integer, optional), " +
        "limit (integer, optional), cursor (string, optional), campaign (string, optional).";

    private const string CampaignGetParameters =
        "campaign_get accepts: refs (array of string, required), include (array of string, optional), detail (string, optional), " +
        "perspective (string, optional), as_of_session (integer, optional), campaign (string, optional).";

    private const string CampaignWriteParameters =
        "campaign_write accepts: ops (array of object, required), campaign (string, optional), session (integer, optional), " +
        "reason (string, optional), dry_run (boolean, optional).";

    private const string CampaignKnowledgeParameters =
        "campaign_knowledge accepts: action (string, required), campaign (string, optional), targets (array of string, optional), " +
        "knowers (array of object, optional), facts (array of string, optional), secret (string, optional), handout (string, optional), " +
        "to (array of string, optional), how (string, optional), who (array of string, optional), text (string, optional), " +
        "perspective (string, optional), diegetic (boolean, optional), audience (string, optional), about (array of string, optional), " +
        "perspectives (array of string, optional), as_of_session (integer, optional), session (integer, optional), reason (string, optional), " +
        "dry_run (boolean, optional).";

    private const string CampaignSessionParameters =
        "campaign_session accepts: action (string, required), campaign (string, optional), session (integer, optional), title (string, optional), " +
        "arc (string, optional), prep_md (string, optional), played_on (string, optional), precision (string, optional), " +
        "attendance (array of object, optional), ingame (string, optional), ingame_end (string, optional), notes (array of string, optional), " +
        "recap_md (string, optional), next_hooks (array of string, optional), confidence (string, optional), status (string, optional), " +
        "limit (integer, optional), cursor (string, optional), perspective (string, optional), reason (string, optional), dry_run (boolean, optional).";

    private const string CampaignHistoryParameters =
        "campaign_history accepts: action (string, required), since (string, optional), session (integer, optional), targets (array of string, optional), " +
        "ref (string, optional), refs (array of string, optional), detail (string, optional), batch_id (string, optional), dry_run (boolean, optional), " +
        "reason (string, optional), limit (integer, optional), cursor (string, optional), campaign (string, optional).";

    private const string CombatParameters =
        "combat accepts: action (string, required), campaign (string, optional), encounter (string, optional), name (string, optional), " +
        "add_party (boolean, optional), lair (boolean, optional), edition (string, optional), combatants (array of object, optional), " +
        "targets (array of string, optional), amount (integer, optional), dice (string, optional), damage_type (string, optional), " +
        "parts (array of object, optional), critical (boolean, optional), magical (boolean, optional), half (array of string, optional), " +
        "raw (boolean, optional), knock_out (boolean, optional), source (string, optional), secret (boolean, optional), temp (boolean, optional), " +
        "item (string, optional), add (array of string, optional), remove (array of string, optional), duration (string, optional), " +
        "dc (integer, optional), ability (string, optional), level (integer, optional), round (integer, optional), effect (object, optional), " +
        "resource (string, optional), spell (string, optional), slot_level (integer, optional), pact (boolean, optional), drop (boolean, optional), " +
        "total (integer, optional), face (integer, optional), stable (boolean, optional), resistance (boolean, optional), " +
        "rolls (array of object, optional), surprised (array of string, optional), from (string, optional), perspective (string, optional), " +
        "outcome (string, optional), xp (integer, optional), loot (array of object, optional), currency (array of object, optional), " +
        "discard (boolean, optional), force (boolean, optional), dry_run (boolean, optional), reason (string, optional).";

    private const string CampaignCharacterParameters =
        "campaign_character accepts: action (string, required), character (string, optional), campaign (string, optional), perspective (string, optional), " +
        "sheet (object, optional), sim_profile (object, optional), amount (integer, optional), damage_type (string, optional), slot_level (integer, optional), " +
        "pact (boolean, optional), resource (string, optional), kind (string, optional), hit_dice (integer, optional), rolls (array of integer, optional), " +
        "add (array of string, optional), remove (array of string, optional), level (integer, optional), class (string, optional), items (array of object, optional), " +
        "coins (object, optional), session (integer, optional), reason (string, optional), dry_run (boolean, optional).";

    [Theory]
    [InlineData("campaign", "{}", "missing required argument 'action'", CampaignParameters)]
    [InlineData("campaign", """{"action":"list","dry_run":"yes"}""", "argument 'dry_run' should be boolean or null but was the string \"yes\"", CampaignParameters)]
    [InlineData("campaign", """{"action":"use","campaign":["belmakor"]}""", "argument 'campaign' should be string or null but was an array", CampaignParameters)]
    [InlineData("campaign_search", """{"kinds":"character"}""", "argument 'kinds' should be array or null but was the string \"character\"", CampaignSearchParameters)]
    [InlineData("campaign_search", """{"query":"old king","perspecitve":"party"}""", "unknown argument 'perspecitve'", CampaignSearchParameters)]
    [InlineData("campaign_search", """{"as_of_session":"three"}""", "argument 'as_of_session' should be integer or null but was the string \"three\"", CampaignSearchParameters)]
    [InlineData("campaign_get", "{}", "missing required argument 'refs'", CampaignGetParameters)]
    [InlineData("campaign_get", """{"refs":"character:old-king"}""", "argument 'refs' should be array but was the string \"character:old-king\"", CampaignGetParameters)]
    [InlineData("campaign_get", """{"refs":["f:1",2]}""", "argument 'refs' item 2 should be string but was the number 2", CampaignGetParameters)]
    [InlineData("campaign_write", "{}", "missing required argument 'ops'", CampaignWriteParameters)]
    [InlineData("campaign_write", """{"ops":{"op":"upsert"}}""", "argument 'ops' should be array but was an object", CampaignWriteParameters)]
    [InlineData("campaign_write", """{"ops":[{"op":"tick","ref":"clock:storm","amount":"two"}]}""",
        "argument 'ops' item 1 field 'amount' should be integer but was the string \"two\"", CampaignWriteParameters)]
    [InlineData("campaign_write", """{"ops":[{"op":"fact","statement":"X","known_by":[{"who":"party","knwo":true}]}]}""",
        "argument 'ops' item 1 field 'known_by' item 1 has unknown field 'knwo' (fields: who, state, known_as, how, via, session, note)", CampaignWriteParameters)]
    [InlineData("campaign_knowledge", "{}", "missing required argument 'action'", CampaignKnowledgeParameters)]
    [InlineData("campaign_knowledge", """{"action":"record","knowers":[{"whom":"party"}]}""",
        "argument 'knowers' item 1 has unknown field 'whom' (fields: who, state, known_as, how, via, session, note)", CampaignKnowledgeParameters)]
    [InlineData("campaign_knowledge", """{"action":"reveal","to":"party"}""", "argument 'to' should be array or null but was the string \"party\"", CampaignKnowledgeParameters)]
    [InlineData("campaign_knowledge", """{"action":"check","text":"x","diegetic":"yes"}""", "argument 'diegetic' should be boolean or null but was the string \"yes\"", CampaignKnowledgeParameters)]
    [InlineData("campaign_session", "{}", "missing required argument 'action'", CampaignSessionParameters)]
    [InlineData("campaign_session", """{"action":"end","attendance":[{"charcter":"character:serif"}]}""",
        "argument 'attendance' item 1 has unknown field 'charcter' (fields: character, present, note)", CampaignSessionParameters)]
    [InlineData("campaign_session", """{"action":"end","attendance":[{"character":"character:serif","present":"no"}]}""",
        "argument 'attendance' item 1 field 'present' should be boolean but was the string \"no\"", CampaignSessionParameters)]
    [InlineData("campaign_session", """{"action":"list","limit":1.5}""", "argument 'limit' should be integer or null but was the number 1.5", CampaignSessionParameters)]
    [InlineData("campaign_history", "{}", "missing required argument 'action'", CampaignHistoryParameters)]
    [InlineData("campaign_history", """{"action":"undo","batch_id":5}""", "argument 'batch_id' should be string or null but was the number 5", CampaignHistoryParameters)]
    [InlineData("campaign_history", """{"action":"since","targets":"f:1"}""", "argument 'targets' should be array or null but was the string \"f:1\"", CampaignHistoryParameters)]
    [InlineData("campaign_history", """{"action":"since","session":99999999999}""", "argument 'session' was the number 99999999999, which is too large to be valid", CampaignHistoryParameters)]
    // campaign_character: the sheet and the inventory and coin entries are typed (a misspelt field is named, never dropped);
    // sim_profile is published untyped and checked as a build, exactly as balance_dpr's build.
    [InlineData("campaign_character", "{}", "missing required argument 'action'", CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"update","sheet":{"levle":5}}""",
        "argument 'sheet' has unknown field 'levle' (fields: player, ruleset, species, lineage, background, classes, level, xp, abilities, ac, max_hp, " +
        "max_hp_reduction, hp, temp_hp, speed, initiative_bonus, passive_perception, spell_save_dc, spell_attack, save_proficiencies, save_bonus, defenses, " +
        "slots, pact, resources, feats, features, spells, languages, inspiration, notes, sheet_source)", CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"update","sheet":{"classes":[{"class":"wizard","lvl":3}]}}""",
        "argument 'sheet' field 'classes' item 1 has unknown field 'lvl' (fields: class, subclass, level, hit_die)", CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"update","sim_profile":{"name":"B","attacks":[{"name":"A","cuont":2}]}}""",
        "argument 'sim_profile' field 'attacks' item 1 has unknown field 'cuont' (fields: name, count, action, to_hit, damage, damage_type, ability_to_damage, " +
        "properties, offhand, mastery, cantrip, from_level, until_level)", CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"update","sim_profile":{"nmae":"B","level":5}}""",
        "argument 'sim_profile' has unknown field 'nmae' (fields: name, preset, edition, level, abilities, proficiency_bonus, fighting_style, attacks, " +
        "modifiers)", CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"update","sim_profile":"GWM"}""", "argument 'sim_profile' should be object but was the string \"GWM\"",
        CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"inventory","items":[{"item":"Rope","qtty":2}]}""",
        "argument 'items' item 1 has unknown field 'qtty' (fields: item, qty, srd, equipped, attuned, notes)", CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"currency","coins":{"gold":5}}""", "argument 'coins' has unknown field 'gold' (fields: cp, sp, ep, gp, pp)",
        CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"rest","rolls":[3,"four"]}""", "argument 'rolls' item 2 should be integer but was the string \"four\"",
        CampaignCharacterParameters)]
    [InlineData("campaign_character", """{"action":"damage","amount":"lots"}""", "argument 'amount' should be integer or null but was the string \"lots\"",
        CampaignCharacterParameters)]
    // combat: every entry list and the effect are typed (a misspelt field is named, never dropped); only an entry's hp is untyped.
    [InlineData("combat", "{}", "missing required argument 'action'", CombatParameters)]
    [InlineData("combat", """{"action":"add","combatants":[{"srd":"Bandit","cuont":2}]}""",
        "argument 'combatants' item 1 has unknown field 'cuont' (fields: srd, character, name, count, hp, ac, init_bonus, side, hidden, death_saves, " +
        "max_hp_reduction)", CombatParameters)]
    [InlineData("combat", """{"action":"damage","targets":"torch","amount":3}""", "argument 'targets' should be array or null but was the string \"torch\"",
        CombatParameters)]
    [InlineData("combat", """{"action":"damage","targets":["torch"],"parts":[{"amount":3,"typ":"fire"}]}""",
        "argument 'parts' item 1 has unknown field 'typ' (fields: amount, dice, type)", CombatParameters)]
    [InlineData("combat", """{"action":"initiative","rolls":[{"combatant":"vars","fcae":12}]}""",
        "argument 'rolls' item 1 has unknown field 'fcae' (fields: combatant, face, total)", CombatParameters)]
    [InlineData("combat", """{"action":"condition","targets":["x"],"add":["Bladesong"],"effect":{"acc":5}}""",
        "argument 'effect' has unknown field 'acc' (fields: ac, resist, immune, vulnerable, except)", CombatParameters)]
    [InlineData("combat", """{"action":"end","loot":[{"itme":"Rope"}]}""", "argument 'loot' item 1 has unknown field 'itme' (fields: item, srd, qty, to)",
        CombatParameters)]
    [InlineData("combat", """{"action":"end","currency":[{"gold":5}]}""",
        "argument 'currency' item 1 has unknown field 'gold' (fields: to, cp, sp, ep, gp, pp)", CombatParameters)]
    [InlineData("combat", """{"action":"next","from":["belmakor"]}""", "argument 'from' should be string or null but was an array", CombatParameters)]
    public async Task CallTool_CampaignToolArgumentOfTheWrongShape_NamesItAndListsAcceptedParameters(
        string tool, string argumentsJson, string problem, string accepted)
    {
        // The guard runs before the tool, so these need no campaign: every one would otherwise reach the SDK's binder and
        // come back as the bare generic error (a typed op field, a knower, an attendance entry included).
        var result = await _server.CallToolJsonAsync(tool, argumentsJson);

        Assert.Equal($"An error occurred invoking '{tool}': Invalid arguments: {problem}. {accepted}", _server.ErrorText(result));
    }

    [Theory]
    [InlineData("""[{"op":"upsert","kind":"character","name":"Iron Guts","stauts":"dead"}]""", "argument 'ops' item 1 has unknown field 'stauts' (fields: op, ref, kind, name, ")]
    [InlineData("""[{"op":"upsert","kind":"character","name":"A"},{"op":"fact","statement":"X","gate":{"afetr":["f:1"]}}]""",
        "argument 'ops' item 2 field 'gate' has unknown field 'afetr' (fields: after, with, prefer, seeds, routes, ")]
    [InlineData("""[{"op":"fact","statement":"X","gate":{"routes":[{"id":"r1","clue":["f:1"]}]}}]""",
        "argument 'ops' item 1 field 'gate' field 'routes' item 1 has unknown field 'clue' (fields: id, clues, min_clues)")]
    public async Task CallTool_CampaignOpsFieldTheSchemaLacks_NamesTheItemAndFieldAndListsAcceptedParameters(string ops, string problem)
    {
        // A misspelt field anywhere in an op (a gate key, a route's clues) would bind and vanish, and the model would report
        // a write that never happened. The field list of an op is long; the part naming the problem and the parameters are
        // pinned.
        var text = _server.ErrorText(await _server.CallToolJsonAsync("campaign_write", $$"""{"ops":{{ops}}}"""));

        Assert.StartsWith("An error occurred invoking 'campaign_write': Invalid arguments: " + problem, text, StringComparison.Ordinal);
        Assert.EndsWith(". " + CampaignWriteParameters, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign_search", """{"query":"old king"}""")]
    [InlineData("campaign_get", """{"refs":["character:old-king"]}""")]
    [InlineData("campaign_write", """{"ops":[{"op":"upsert","kind":"character","name":"Iron Guts"}]}""")]
    [InlineData("campaign_knowledge", """{"action":"check","text":"Old king, come down"}""")]
    [InlineData("campaign_session", """{"action":"list"}""")]
    [InlineData("campaign_history", """{"action":"since"}""")]
    [InlineData("campaign_character", """{"action":"get"}""")]
    [InlineData("campaign_character", """{"action":"damage","character":"character:aria-vale","amount":3}""")]
    // party "campaign" resolves the campaign as every campaign call does (not a campaign tool to the filter, the same answer).
    [InlineData("encounter_difficulty", """{"party":"campaign","monsters":[{"cr":"1"}]}""")]
    [InlineData("combat", """{"action":"state"}""")]
    [InlineData("combat", """{"action":"state","perspective":"party"}""")]
    [InlineData("combat", """{"action":"damage","targets":["torch"],"amount":3}""")]
    [InlineData("combat", """{"action":"start"}""")]
    // An encounter or a character entry is a campaign's (the same answer as every campaign call).
    [InlineData("balance_simulate", """{"encounter":"current"}""")]
    [InlineData("balance_simulate", """{"party":[{"character":"character:torch"}],"enemies":[{"monster":"ogre"}]}""")]
    public async Task CallTool_CampaignToolWithNoCampaigns_SaysHowToCreateOneAndCreatesNoDatabase(string tool, string argumentsJson)
    {
        // The first campaign call a new user makes. The answer must say what to do next, and asking must not leave a
        // campaigns.db behind: a user who never creates a campaign must never find one.
        var result = await _server.CallToolJsonAsync(tool, argumentsJson);

        Assert.Equal(
            $"An error occurred invoking '{tool}': There are no campaigns yet. Create one with campaign " +
            "{\"action\": \"create\", \"name\": \"…\", \"role\": \"player\" or \"dm\", \"ruleset\": \"2024\"}.",
            _server.ErrorText(result));
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "campaigns.db")), "Asking about campaigns created campaigns.db.");
    }

    [Theory]
    // Each reaches the tool's own checks (a campaign exists), past the guard: what was wrong, then how to put it right.
    [InlineData("campaign", """{"action":"destroy"}""", "action \"destroy\" is not one of list, summary, get, create, update, use", "Example: {\"action\": \"create\"")]
    [InlineData("campaign", """{"action":"use"}""", "action \"use\" needs campaign", "Example: {\"action\": \"use\", \"campaign\": \"belmakor\"}")]
    [InlineData("campaign_search", """{"campaign":"nope"}""", "No campaign \"nope\".", "Campaigns: surface (player, 2024). Pass one of those slugs.")]
    [InlineData("campaign_get", """{"refs":["character:nobody"]}""", "refs item 1: \"character:nobody\": nothing in this campaign has that handle.", "campaign_search finds")]
    [InlineData("campaign_get", """{"refs":["character:iron-guts"],"detail":"verbose"}""", "(got \"verbose\")", "detail must be \"concise\" (the default) or \"full\"")]
    [InlineData("campaign_write", """{"ops":[{"op":"upsert","kind":"character"}]}""", "ops item 1 (upsert character): name is required", "e.g. {\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"Iron Guts\"}")]
    [InlineData("campaign_write", """{"ops":[{"op":"smite"}]}""", "ops item 1: op \"smite\" is not an op", "ops are upsert, delete, restore, link")]
    [InlineData("campaign_knowledge", """{"action":"check","perspective":"character:nobody","text":"x"}""", "no character nobody in this campaign", "campaign_search with kinds [\"character\"] lists them")]
    [InlineData("campaign_knowledge", """{"action":"reveal","text":"x"}""", "reveal does not take \"text\"", "reveal takes facts, secret, handout, to")]
    [InlineData("campaign_session", """{"action":"start","recap_md":"x"}""", "start does not take \"recap_md\"", "start takes session, played_on, precision")]
    [InlineData("campaign_history", """{"action":"entity"}""", "action \"entity\" needs ref", "Example: {\"action\": \"entity\", \"ref\": ")]
    [InlineData("campaign_history", """{"action":"since","since":"yesterday"}""", "since \"yesterday\" is not a date", "such as \"2026-09-19\"")]
    [InlineData("campaign_character", """{"action":"heal","amount":3}""", "Aria Vale (character:aria-vale) has no sheet yet",
        "campaign_character {\"action\": \"update\", \"character\": \"character:aria-vale\", \"sheet\": {\"level\": …}, \"campaign\": \"surface\"}")]
    // An amount with no positive int (Math.Abs(int.MinValue) overflows): the refusal, never the SDK's bare error.
    [InlineData("campaign_character", """{"action":"use","slot_level":1,"amount":-2147483648}""", "amount is -2147483648",
        "give 1 to 999 uses spent, or a negative number to restore")]
    [InlineData("campaign_character", """{"action":"get","character":"character:nobody"}""", "\"character:nobody\": no character by that handle in surface",
        "The party: character:aria-vale.")]
    [InlineData("campaign_character", """{"action":"rest","perspective":"party"}""", "rest does not take \"perspective\"",
        "rest takes kind, hit_dice, rolls, character, campaign, session, reason, dry_run. Example: {\"action\": \"rest\"")]
    [InlineData("campaign_character", """{"action":"rest","kind":"nap"}""", "kind is \"nap\"", "give \"short\" or \"long\"")]
    // D8: the party's levels come from the sheets; Aria Vale has none, so the refusal carries the call that gives one.
    [InlineData("encounter_difficulty", """{"party":"campaign","monsters":[{"cr":"1"}]}""", "1 member has no level on one: Aria Vale (no sheet)",
        "campaign_character {\"action\": \"update\", \"character\": \"character:aria-vale\", \"sheet\": {\"level\": <n>}, \"campaign\": \"surface\"}")]
    [InlineData("combat", """{"action":"fight"}""", "action \"fight\" is not a combat action; give prepare, start, add, set, leave, initiative",
        "Example: {\"action\": \"damage\"")]
    [InlineData("combat", """{"action":"damage","targets":["torch"],"spell":"Bless"}""", "combat damage does not take \"spell\"",
        "damage takes targets, amount, dice, parts, damage_type, critical, magical, half, raw, knock_out, source, secret, campaign, encounter. Example:")]
    [InlineData("combat", """{"action":"state","perspective":"character:nobody"}""", "perspective \"character:nobody\": no character nobody in this campaign",
        "campaign_search with kinds [\"character\"] lists them")]
    [InlineData("combat", """{"action":"legendary","name":"Lash"}""", "legendary needs source", "{\"action\": \"legendary\", \"source\": \"<name>\"")]
    [InlineData("combat", """{"action":"prepare","name":"Probe","combatants":[{"srd":"Beholder"}]}""",
        "combatants item 1: no monster in the 2014 or 2024 SRD is named \"Beholder\"", "add it by name with hp, ac and init_bonus instead of srd")]
    [InlineData("combat", """{"action":"add","combatants":[{"name":"Imp","max_hp_reduction":5}]}""", "max_hp_reduction is set only",
        "add it, then set its max_hp_reduction")]
    [InlineData("combat", """{"action":"next"}""", "No combat is running in surface", "combat {\"action\": \"start\"")]
    [InlineData("balance_simulate", """{"party":[{"character":"character:aria-vale"}],"enemies":[{"monster":"ogre"}]}""",
        "party item 1 (character:aria-vale): Aria Vale has no sheet to simulate yet",
        "campaign_character {\"action\": \"update\", \"character\": \"character:aria-vale\", \"sheet\": {\"level\": …}, \"campaign\": \"surface\"}")]
    [InlineData("balance_simulate", """{"encounter":"current"}""", "No combat is running in surface", "start one with combat {\"action\": \"start\"")]
    [InlineData("balance_simulate", """{"from_state":true,"party":[{"monster":"ogre"}]}""", "from_state resumes the fight exactly as it stands",
        "leave them out")]
    public async Task CallTool_CampaignToolBadInput_SaysWhatWasWrongAndHowToFixIt(string tool, string argumentsJson, string wrong, string fix)
    {
        // The wording belongs to the tools and the repository services, whose own tests pin it whole; what this pins is
        // that it reaches the model after the prefix, naming what was wrong and carrying the way out.
        var text = _campaign.ErrorText(await _campaign.CallToolJsonAsync(tool, argumentsJson));

        Assert.StartsWith($"An error occurred invoking '{tool}': ", text, StringComparison.Ordinal);
        Assert.Contains(wrong, text, StringComparison.Ordinal);
        Assert.Contains(fix, text, StringComparison.Ordinal);
    }

    [Theory]
    // HUGE is 100,000 characters. The ceiling on the echo is per row: the argument guard cuts a name it refuses at 40
    // characters, the campaign tools cut a value at 80 (CampaignMarkdownText.Echo) and the repository's messages at 60 or
    // 40. A length ceiling alone does not see the cut grow: the guard's refusal lists every parameter (campaign_session has
    // 21, 778 characters in all), so a ceiling it fits under also fits a value echoed at 1,000 characters.
    [InlineData("campaign", """{"action":"HUGE"}""", 80)]
    [InlineData("campaign", """{"action":"list","HUGE":1}""", 40)]
    [InlineData("campaign_search", """{"campaign":"HUGE"}""", 80)]
    [InlineData("campaign_get", """{"refs":["HUGE"]}""", 80)]
    [InlineData("campaign_get", """{"refs":["character:iron-guts"],"detail":"HUGE"}""", 80)]
    [InlineData("campaign_write", """{"ops":[{"op":"HUGE"}]}""", 80)]
    [InlineData("campaign_write", """{"ops":[{"op":"upsert","kind":"character","name":"HUGE"}]}""", 80)]
    [InlineData("campaign_knowledge", """{"action":"HUGE"}""", 80)]
    [InlineData("campaign_knowledge", """{"action":"check","perspective":"HUGE","text":"x"}""", 80)]
    [InlineData("campaign_session", """{"action":"HUGE"}""", 80)]
    [InlineData("campaign_session", """{"action":"list","HUGE":1}""", 40)]
    [InlineData("campaign_history", """{"action":"HUGE"}""", 80)]
    [InlineData("campaign_history", """{"action":"since","since":"HUGE"}""", 80)]
    [InlineData("campaign_character", """{"action":"HUGE"}""", 80)]
    [InlineData("campaign_character", """{"action":"get","character":"HUGE"}""", 60)]
    [InlineData("campaign_character", """{"action":"get","perspective":"HUGE"}""", 60)]
    [InlineData("campaign_character", """{"action":"rest","kind":"HUGE"}""", 60)]
    [InlineData("campaign_character", """{"action":"get","HUGE":1}""", 40)]
    [InlineData("combat", """{"action":"HUGE"}""", 80)]
    [InlineData("combat", """{"action":"state","perspective":"HUGE"}""", 80)]
    [InlineData("combat", """{"action":"state","encounter":"HUGE"}""", 80)]
    public async Task CallTool_HugeArgumentInACampaignError_IsEchoedShortened(string tool, string argumentsTemplate, int longestEcho)
    {
        var argumentsJson = argumentsTemplate.Replace("HUGE", new string('x', 100_000), StringComparison.Ordinal);

        var error = _campaign.ErrorText(await _campaign.CallToolJsonAsync(tool, argumentsJson));
        var echoed = EchoRunRegex().Matches(error).Select(m => m.Length).DefaultIfEmpty(0).Max();

        Assert.True(echoed <= longestEcho, $"{echoed} characters of the argument came back (at most {longestEcho}): {error[..Math.Min(300, error.Length)]}");
        Assert.True(error.Length < 1_000, $"{error.Length} characters: {error[..Math.Min(300, error.Length)]}");
        Assert.Contains("…", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign", """{"action":"list"}""")]
    [InlineData("campaign", """{"action":"create","name":"Sky","role":"dm","ruleset":"2024"}""")]
    [InlineData("campaign_search", """{"query":"old king"}""")]
    [InlineData("campaign_get", """{"refs":["character:old-king"]}""")]
    [InlineData("campaign_write", """{"ops":[{"op":"upsert","kind":"character","name":"Iron Guts"}]}""")]
    [InlineData("campaign_knowledge", """{"action":"check","text":"Old king, come down"}""")]
    [InlineData("campaign_session", """{"action":"list"}""")]
    [InlineData("campaign_history", """{"action":"since"}""")]
    [InlineData("campaign_character", """{"action":"get"}""")]
    [InlineData("campaign_character", """{"action":"update","character":"character:aria-vale","sheet":{"level":3}}""")]
    [InlineData("encounter_difficulty", """{"party":"campaign","monsters":[{"cr":"1"}]}""")]
    [InlineData("combat", """{"action":"state"}""")]
    [InlineData("combat", """{"action":"start","name":"Probe"}""")]
    [InlineData("balance_simulate", """{"encounter":"current"}""")]
    public async Task CallTool_CampaignsDatabaseFromANewerVersion_ReturnsTheStoresMessageAndChangesNothing(string tool, string argumentsJson)
    {
        // The user's fix (update dnd-mcp, or point DND_MCP_DB elsewhere) is only in this message: without the call-tool
        // filter's translation every campaign tool fails with the SDK's bare "An error occurred invoking '<tool>'.". The
        // file must also be left exactly as it was (a newer build's data, which this one must neither migrate nor write).
        var expected = _newer.StoreMessage();

        var result = await _newer.Harness.CallToolJsonAsync(tool, argumentsJson);

        Assert.StartsWith(
            $"campaigns.db at {_newer.DatabasePath} was written by a newer version of dnd-mcp (schema version {NewerCampaignsDatabaseServer.SchemaVersion};",
            expected,
            StringComparison.Ordinal);
        Assert.Equal($"An error occurred invoking '{tool}': {expected}", _newer.Harness.ErrorText(result));
        Assert.Equal(NewerCampaignsDatabaseServer.SchemaVersion, _newer.UserVersion());
    }

    [Theory]
    [InlineData("knowledge_check", """{"character":"aria-vale"}""")]
    [InlineData("in_character", """{"character":"aria-vale"}""")]
    [InlineData("session_prep", "{}")]
    [InlineData("session_recap", """{"session":"2"}""")]
    [InlineData("continuity_check", "{}")]
    public async Task GetPrompt_CampaignsDatabaseFromANewerVersion_FailsWithTheStoresMessage(string prompt, string argumentsJson)
    {
        // A prompt reads the campaign before it writes its instructions, outside the call-tool filter: the get-prompt
        // filter's translation is what puts the store's message, rather than the SDK's bare internal error, in front of the
        // user who typed /mcp__dnd__<prompt>.
        using var document = JsonDocument.Parse(argumentsJson);
        var arguments = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString());

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _newer.Harness.Client.GetPromptAsync(prompt, arguments).AsTask());

        Assert.Equal(McpErrorCode.InternalError, error.ErrorCode);
        Assert.Equal("Request failed (remote): " + _newer.StoreMessage(), error.Message);
        Assert.Equal(NewerCampaignsDatabaseServer.SchemaVersion, _newer.UserVersion());
    }

    [Theory]
    // campaign://list is a registered resource; the others are served by the campaign read handler. One filter covers both.
    [InlineData("campaign://list")]
    [InlineData("campaign://sky/summary")]
    [InlineData("campaign://sky/knowledge/party")]
    [InlineData("campaign://sky/party")]
    [InlineData("campaign://sky/combat/current")]
    public async Task ReadResource_CampaignsDatabaseFromANewerVersion_FailsWithTheStoresMessage(string uri)
    {
        // An @dnd: mention of a campaign resource, read outside the call-tool filter: the read-resource filter's translation
        // is what tells the user why it cannot be read.
        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _newer.Harness.Client.ReadResourceAsync(uri).AsTask());

        Assert.Equal(McpErrorCode.InternalError, error.ErrorCode);
        Assert.Equal("Request failed (remote): " + _newer.StoreMessage(), error.Message);
        Assert.Equal(NewerCampaignsDatabaseServer.SchemaVersion, _newer.UserVersion());
    }

    /// <summary>
    /// FH1 (R04): a read that meets a damaged page (header and schema intact, so the open passes) returns the store's
    /// message naming the file and its backups, as the same server's campaign_write does, never the SDK's bare "An error
    /// occurred invoking '&lt;tool&gt;'.": the raw SqliteException of a read statement is mapped by the call-tool filter.
    /// </summary>
    [Theory]
    [InlineData("campaign_get", """{"refs":["character:old-hero"]}""")]
    [InlineData("campaign", """{"action":"summary"}""")]
    [InlineData("campaign_history", """{"action":"since"}""")]
    [InlineData("campaign_write", """{"ops":[{"op":"upsert","kind":"character","name":"Old Hero","summary":"changed"}]}""")]
    [InlineData("campaign_character", """{"action":"get","character":"character:old-hero"}""")]
    [InlineData("campaign_character", """{"action":"get","character":"character:old-hero","perspective":"party"}""")]
    [InlineData("campaign_character", """{"action":"get"}""")]
    [InlineData("campaign_character", """{"action":"update","character":"character:old-hero","sheet":{"level":3}}""")]
    // Not a campaign tool to the filter (its SQLite failures are srd.db's): the party read maps the store's failure itself.
    [InlineData("encounter_difficulty", """{"party":"campaign","monsters":[{"cr":"1"}]}""")]
    // combat is a campaign tool to the filter (IsCampaignTool): a character's view is resolved (its entity read) before the
    // board, outside the Repository's own mapping, so only the filter can give the store's message here.
    [InlineData("combat", """{"action":"state","perspective":"character:old-hero"}""")]
    [InlineData("combat", """{"action":"start","name":"Probe"}""")]
    // balance_simulate is not (its SQLite failures are srd.db's): a character entry's sheet read maps the store's failure itself.
    [InlineData("balance_simulate", """{"party":[{"character":"character:old-hero"}],"enemies":[{"monster":"ogre"}]}""")]
    public async Task CallTool_ReadOfADamagedCampaignsDatabasePage_ReturnsTheStoresMessage(string tool, string argumentsJson)
    {
        var result = await _damaged.Harness.CallToolJsonAsync(tool, argumentsJson);

        Assert.Equal($"An error occurred invoking '{tool}': {_damaged.StoreMessage()}", _damaged.Harness.ErrorText(result));
    }

    /// <summary>FH1 (R04): the same read behind a prompt (it resolves the character) is mapped by the get-prompt filter.</summary>
    [Fact]
    public async Task GetPrompt_ReadOfADamagedCampaignsDatabasePage_FailsWithTheStoresMessage()
    {
        var arguments = new Dictionary<string, object?> { ["character"] = "old-hero", ["campaign"] = DamagedCampaignsDatabaseServer.Slug };

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _damaged.Harness.Client.GetPromptAsync("in_character", arguments).AsTask());

        Assert.Equal(McpErrorCode.InternalError, error.ErrorCode);
        Assert.Equal("Request failed (remote): " + _damaged.StoreMessage(), error.Message);
    }

    /// <summary>FH1 (R04): the same read behind a campaign:// resource is mapped by the read-resource filter.</summary>
    [Fact]
    public async Task ReadResource_ReadOfADamagedCampaignsDatabasePage_FailsWithTheStoresMessage()
    {
        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            _damaged.Harness.Client.ReadResourceAsync($"campaign://{DamagedCampaignsDatabaseServer.Slug}/summary").AsTask());

        Assert.Equal(McpErrorCode.InternalError, error.ErrorCode);
        Assert.Equal("Request failed (remote): " + _damaged.StoreMessage(), error.Message);
    }

    /// <summary>
    /// FH1 (R04): another process holds an exclusive lock on campaigns.db past the busy timeout (a sqlite3 shell with
    /// locking_mode EXCLUSIVE does): a read tool says the file is locked by another process and to try again, as a write
    /// does, instead of the SDK's bare error after waiting it out.
    /// </summary>
    [Fact]
    public async Task CallTool_ReadWhileAnotherProcessHoldsAnExclusiveLock_SaysTheDatabaseIsLocked()
    {
        await using var server = await Campaign.CampaignTestServer.StartAsync();
        await server.Call("campaign", """{"action": "create", "name": "Probe", "role": "dm", "ruleset": "2014"}""");
        await server.Call("campaign_write", """{"ops": [{"op": "upsert", "kind": "character", "name": "Old Hero", "summary": "a hero"}]}""");
        using var holder = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = server.DatabasePath, Pooling = false }.ToString());
        holder.Open();
        using (var command = holder.CreateCommand())
        {
            command.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; UPDATE campaign SET summary_md = 'held';";
            command.ExecuteNonQuery();
        }

        try
        {
            var error = await server.Error("campaign_search", """{"query": "hero"}""");

            Assert.StartsWith($"An error occurred invoking 'campaign_search': campaigns.db at {server.DatabasePath} is locked by another dnd-mcp process", error,
                StringComparison.Ordinal);
        }
        finally
        {
            using var rollback = holder.CreateCommand();
            rollback.CommandText = "ROLLBACK";
            rollback.ExecuteNonQuery();
        }
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
