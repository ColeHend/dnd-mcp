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
/// <para>
/// The campaign tools have the most optional arguments of any (campaign_session has 20), and each action uses a few:
/// a model fills the rest with null. Their rows run against <see cref="OneCampaignServer"/>, whose campaign lets every
/// tool get past campaign resolution to the argument handling under test.
/// </para>
/// </summary>
public sealed class ArgumentBindingAgreementTests : IClassFixture<TestOnlyToolsServer>, IClassFixture<OneCampaignServer>
{
    private const string Prefix = "An error occurred invoking 'echo_numbers': ";

    private const string AcceptedParameters = "echo_numbers accepts: round_cap (integer, optional), ratio (number, optional).";

    private const string SearchPrefix = "An error occurred invoking 'rules_search': ";

    private const string SearchParameters =
        "rules_search accepts: query (string, required), edition (string, optional), kinds (array of string, optional), limit (integer, optional).";

    private readonly McpServerHarness _server;
    private readonly McpServerHarness _campaign;

    public ArgumentBindingAgreementTests(TestOnlyToolsServer fixture, OneCampaignServer campaign)
    {
        _server = fixture.Harness;
        _campaign = campaign.Harness;
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

    private const string Party = """[{"name":"F","build":{"name":"F","level":5,"abilities":{"str":18},"attacks":[{"name":"Greatsword","count":2,"damage":"2d6","damage_type":"slashing","properties":["melee"]}]},"hp":44,"ac":18,"count":2}]""";

    [Fact]
    public async Task CallTool_BalanceSimulateNullOptionalArguments_MeanTheDefaults()
    {
        // Models send null for "use the default"; every optional argument must bind and read as the default: 10,000 fights,
        // round cap 20, average enemy HP, no surprise, the party's edition, the default policies, a random seed.
        var text = _server.SuccessText(await _server.CallToolJsonAsync(
            "balance_simulate",
            $$"""{"party":{{Party}},"enemies":[{"monster":"Goblin"}],"iterations":null,"seed":null,"round_cap":null,"edition":null,"surprise":null,"enemy_hp":null,"precision":null,"replay":null,"policies":null,"compare":null,"rulings":null}"""));

        Assert.Contains("*2024 rules · 10,000 fights · round cap 20 · enemy HP average · no surprise · seed ", text, StringComparison.Ordinal);
        Assert.Contains("- party focus_fire: ", text, StringComparison.Ordinal);
        Assert.Contains("(drawn at random): pass \"seed\": \"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_BalanceSimulateQuotedNumbers_BindLikeNumbers()
    {
        // Numbers quoted at the top level, inside party (a typed array) and inside enemies and compare (published untyped,
        // read by the tool with the SDK's options) all bind as their numbers.
        var quoted = _server.SuccessText(await _server.CallToolJsonAsync(
            "balance_simulate",
            """{"party":[{"name":"F","build":{"name":"F","level":"5","abilities":{"str":"18"},"attacks":[{"name":"Greatsword","count":"2","damage":"2d6","damage_type":"slashing","properties":["melee"]}]},"hp":"44","ac":"18","count":"2"}]""" +
            ""","enemies":[{"monster":"Goblin","count":"2"}],"iterations":"300","seed":"7","round_cap":"10","compare":{"member":"1","feature":{"name":"T","modifiers":[{"kind":"to_hit","amount":"1"}]}}}"""));
        var plain = _server.SuccessText(await _server.CallToolJsonAsync(
            "balance_simulate",
            $$"""{"party":{{Party}},"enemies":[{"monster":"Goblin","count":2}],"iterations":300,"seed":7,"round_cap":10""" +
            ""","compare":{"member":1,"feature":{"name":"T","modifiers":[{"kind":"to_hit","amount":1}]}}}"""));

        Assert.Equal(plain, quoted);
        Assert.Contains("### Compare: F (party entry 1) with T", plain, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("campaign", "\"action\":\"list\"", "campaign", "name", "role", "ruleset", "dm_name", "slug", "settings", "party_name", "my_character",
        "status", "summary_md", "current_location", "current_ingame", "perspective", "reason", "session", "dry_run")]
    [InlineData("campaign", "\"action\":\"summary\"", "campaign", "name", "role", "ruleset", "dm_name", "slug", "settings", "party_name", "my_character",
        "status", "summary_md", "current_location", "current_ingame", "perspective", "reason", "session", "dry_run")]
    [InlineData("campaign_search", "", "query", "kinds", "statuses", "tags", "perspective", "include_facts", "as_of_session", "limit", "cursor", "campaign")]
    [InlineData("campaign_get", "\"refs\":[\"character:iron-guts\"]", "include", "detail", "perspective", "as_of_session", "campaign")]
    [InlineData("campaign_knowledge", "\"action\":\"check\",\"text\":\"Iron Guts owes us a favour.\"", "campaign", "targets", "knowers", "facts", "secret",
        "handout", "to", "how", "who", "perspective", "diegetic", "audience", "about", "perspectives", "as_of_session", "session", "reason", "dry_run")]
    [InlineData("campaign_knowledge", "\"action\":\"ledger\",\"about\":[\"character:iron-guts\"]", "campaign", "targets", "knowers", "facts", "secret",
        "handout", "to", "how", "who", "text", "perspective", "diegetic", "audience", "perspectives", "as_of_session", "session", "reason", "dry_run")]
    [InlineData("campaign_session", "\"action\":\"list\"", "campaign", "session", "title", "arc", "prep_md", "played_on", "precision", "attendance", "ingame",
        "ingame_end", "notes", "recap_md", "next_hooks", "confidence", "status", "limit", "cursor", "perspective", "reason", "dry_run")]
    [InlineData("campaign_history", "\"action\":\"since\"", "since", "session", "targets", "ref", "refs", "detail", "batch_id", "dry_run", "reason", "limit",
        "cursor", "campaign")]
    public async Task CallTool_CampaignToolNullForEveryOptionalArgument_MeansTheDefault(string tool, string given, params string[] optional)
    {
        // Models send null for "use the default" and for every argument the action does not use. The binder makes null and
        // "left out" the same C# null, so what breaks is the published schema: a parameter declared non-nullable (a
        // `string perspective = "author"` default, a non-nullable dictionary) is published without null, and the argument
        // guard then refuses a correct call ("should be string but was null") at the cost of a retry. The row must name
        // every optional argument the schema has, so a new one cannot skip this check.
        var schema = (await _campaign.Client.ListToolsAsync()).Single(t => t.Name == tool).JsonSchema;
        var plain = "{" + given + "}";
        var withNulls = "{" + string.Join(",", new[] { given }.Where(g => g.Length > 0).Concat(optional.Select(o => $"\"{o}\":null"))) + "}";
        using (var givenNames = JsonDocument.Parse(plain))
        {
            Assert.Equal(
                schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Except(givenNames.RootElement.EnumerateObject().Select(p => p.Name))
                    .Order(StringComparer.Ordinal),
                optional.Order(StringComparer.Ordinal));
        }

        var defaulted = _campaign.SuccessText(await _campaign.CallToolJsonAsync(tool, plain));
        var nulls = _campaign.SuccessText(await _campaign.CallToolJsonAsync(tool, withNulls));

        Assert.Equal(defaulted, nulls);
    }

    [Fact]
    public async Task CallTool_CampaignWriteNullOptionalArgumentsAndNullOpFields_WriteTheOpForReal()
    {
        // dry_run null must mean false (read as a preview, the batch the model reports was never written) and campaign null
        // the current campaign. A null field inside an op must pass the published item schema and count as not given: the
        // Domain validator refuses a field the op does not take, so a null statement or gate counted as given would refuse
        // an upsert models routinely send that way.
        var name = "Null Harbour " + Guid.NewGuid().ToString("N")[..8];
        var text = _campaign.SuccessText(await _campaign.CallToolJsonAsync("campaign_write",
            $$"""
            {"ops":[{"op":"upsert","kind":"location","name":"{{name}}","ref":null,"statement":null,"gate":null,"known_by":null,"data":null,"clock":null,"aliases":null,
                     "tags":null,"sort_key":null,"attitude":null,"symmetric":null,"amount":null,"introduced_session":null,"established_session":null}],
             "campaign":null,"session":null,"reason":null,"dry_run":null}
            """));

        Assert.StartsWith($"# campaign_write: 1 op applied ({OneCampaignServer.Slug})", text, StringComparison.Ordinal);
        Assert.Contains("Batch `", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dry run", text, StringComparison.Ordinal);
    }

    [Theory]
    // Dry runs, so each pair reads the same campaign: a knower, an attendance entry and an alias with every optional field
    // null, against the same item with those fields left out.
    [InlineData("campaign_knowledge",
        """{"action":"record","targets":["character:iron-guts"],"knowers":[{"who":"character:aria-vale","state":null,"known_as":null,"how":null,"via":null,"session":null,"note":null}],"dry_run":true}""",
        """{"action":"record","targets":["character:iron-guts"],"knowers":[{"who":"character:aria-vale"}],"dry_run":true}""")]
    [InlineData("campaign_session",
        """{"action":"start","attendance":[{"character":"character:aria-vale","present":null,"note":null}],"dry_run":true}""",
        """{"action":"start","attendance":[{"character":"character:aria-vale"}],"dry_run":true}""")]
    [InlineData("campaign_write",
        """{"ops":[{"op":"upsert","ref":"character:iron-guts","aliases":[{"alias":"Old Guts","visibility":null}]}],"dry_run":true}""",
        """{"ops":[{"op":"upsert","ref":"character:iron-guts","aliases":[{"alias":"Old Guts"}]}],"dry_run":true}""")]
    public async Task CallTool_CampaignItemWithNullFields_MeansItsDefaults(string tool, string withNulls, string without)
    {
        // The same rule one level down, where the schema comes from the spec classes (KnowerSpec, AttendanceSpec, AliasSpec):
        // a field declared non-nullable there is published without null and the guard refuses the whole item. A knower's
        // state null is "knows", an attendee's present null is "present", an alias's visibility null is the default.
        var defaulted = _campaign.SuccessText(await _campaign.CallToolJsonAsync(tool, without));
        var nulls = _campaign.SuccessText(await _campaign.CallToolJsonAsync(tool, withNulls));

        Assert.Equal(defaulted, nulls);
    }

    [Theory]
    // Quoted integers bind as their numbers (AllowReadingFromString), as they do for every other tool.
    [InlineData("campaign_search", """{"limit":"1"}""", """{"limit":1}""")]
    [InlineData("campaign_session", """{"action":"list","limit":"1"}""", """{"action":"list","limit":1}""")]
    [InlineData("campaign_history", """{"action":"since","limit":"1"}""", """{"action":"since","limit":1}""")]
    // An empty filter list is no filter: models send [] for "none".
    [InlineData("campaign_search", """{"kinds":[],"statuses":[],"tags":[]}""", "{}")]
    public async Task CallTool_CampaignToolArgumentsTheBinderReadsAlike_GiveTheSameResult(string tool, string oneForm, string otherForm)
    {
        var first = _campaign.SuccessText(await _campaign.CallToolJsonAsync(tool, oneForm));
        var second = _campaign.SuccessText(await _campaign.CallToolJsonAsync(tool, otherForm));

        Assert.Equal(second, first);
    }
}
