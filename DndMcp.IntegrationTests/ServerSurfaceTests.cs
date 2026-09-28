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
        "balance_compare",
        "balance_dpr",
        "balance_simulate",
        "dice_odds",
        "dice_roll",
        "encounter_difficulty",
        "rules_get",
        "rules_search",
    ];

    /// <summary>
    /// THE hints each tool declares (readOnly, destructive, idempotent, openWorld), as PLAN.md's tool table gives them.
    /// Claude Code decides what it auto-approves or warns about from these, so a flipped hint must be a deliberate diff
    /// here: checking only that each is set let openWorld become true or idempotent false with every test green.
    /// dice_roll and balance_simulate are the tools that are not idempotent: without a seed the same call rolls again.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld)> ExpectedAnnotations =
        new Dictionary<string, (bool, bool, bool, bool)>
        {
            ["balance_compare"] = (true, false, true, false),
            ["balance_dpr"] = (true, false, true, false),
            ["balance_simulate"] = (true, false, false, false),
            ["dice_odds"] = (true, false, true, false),
            ["dice_roll"] = (true, false, false, false),
            ["encounter_difficulty"] = (true, false, true, false),
            ["rules_get"] = (true, false, true, false),
            ["rules_search"] = (true, false, true, false),
        };

    /// <summary>
    /// THE list of resources, for the same reason. Claude Code offers each as an <c>@dnd:</c> mention and adds tools to
    /// read them, so a resource appearing or vanishing changes what the model can reach.
    /// </summary>
    public static readonly IReadOnlyList<string> ExpectedResourceUris =
    [
        "rules://attribution",
        "rules://tables/adventuring-day-xp-2014",
        "rules://tables/aoe-targets",
        "rules://tables/cr-xp",
        "rules://tables/dpr-targets-by-level",
        "rules://tables/encounter-multipliers-2014",
        "rules://tables/gwf-expected-values",
        "rules://tables/monster-stats-by-cr-2014",
        "rules://tables/monster-stats-by-cr-empirical",
        "rules://tables/xp-budget-2024",
        "rules://tables/xp-thresholds-2014",
    ];

    // Claude Code truncates tool descriptions and server instructions here (CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH).
    private const int DescriptionLimit = 2048;

    private readonly McpServerHarness _server;

    public ServerSurfaceTests(McpServerHarness server)
    {
        _server = server;
    }

    public static TheoryData<string> ToolNames => new(ExpectedToolNames);

    public static TheoryData<string> ResourceUris => new(ExpectedResourceUris);

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

    [Fact]
    public void ExpectedAnnotations_EveryTool_HasARow()
    {
        Assert.Equal(ExpectedToolNames.Order(StringComparer.Ordinal), ExpectedAnnotations.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolAnnotations_EveryTool_HasExactlyTheExpectedHints(string name)
    {
        var annotations = (await GetToolAsync(name)).ProtocolTool.Annotations;
        var expected = ExpectedAnnotations[name];

        Assert.NotNull(annotations);
        Assert.Equal(
            ((bool?)expected.ReadOnly, (bool?)expected.Destructive, (bool?)expected.Idempotent, (bool?)expected.OpenWorld),
            (annotations.ReadOnlyHint, annotations.DestructiveHint, annotations.IdempotentHint, annotations.OpenWorldHint));
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

        AssertPropertiesDescribed(name, properties);
    }

    // Every property, and every field of an object nested in it (encounter_difficulty's monsters items), has a valid name
    // and a description: the description is the only hint the model gets about format ("2d6+3", "1-100", "1/2").
    private static void AssertPropertiesDescribed(string path, JsonElement properties)
    {
        foreach (var property in properties.EnumerateObject())
        {
            Assert.Matches(SchemaPropertyNameRegex(), property.Name);

            var hasDescription = property.Value.TryGetProperty("description", out var description) &&
                                 !string.IsNullOrWhiteSpace(description.GetString());
            Assert.True(hasDescription, $"{path}.{property.Name} has no description.");

            foreach (var nested in new[] { property.Value, property.Value.TryGetProperty("items", out var items) ? items : default })
            {
                if (nested.ValueKind == JsonValueKind.Object && nested.TryGetProperty("properties", out var fields) && fields.ValueKind == JsonValueKind.Object)
                {
                    AssertPropertiesDescribed($"{path}.{property.Name}", fields);
                }
            }
        }
    }

    [Fact]
    public async Task ListResources_Server_ExposesExactlyTheExpectedResources()
    {
        var resources = await _server.Client.ListResourcesAsync();

        Assert.Equal(
            ExpectedResourceUris.Order(StringComparer.Ordinal),
            resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListResourceTemplates_Server_HasNone()
    {
        // A method whose URI gains a {parameter} silently moves from resources/list to resources/templates/list, where
        // Claude Code may not offer it as a mention at all (PLAN.md, SDK research). Pinned so that move is deliberate.
        var templates = await _server.Client.ListResourceTemplatesAsync();

        Assert.Empty(templates);
    }

    [Theory]
    [MemberData(nameof(ResourceUris))]
    public async Task Resource_EveryResource_HasNameTitleDescriptionAndMarkdownType(string uri)
    {
        var resources = await _server.Client.ListResourcesAsync();
        var resource = Assert.Single(resources, r => r.Uri == uri).ProtocolResource;

        Assert.False(string.IsNullOrWhiteSpace(resource.Name), $"{uri} has no name.");
        Assert.False(string.IsNullOrWhiteSpace(resource.Title), $"{uri} has no title; /mcp would show the raw name.");
        Assert.False(string.IsNullOrWhiteSpace(resource.Description), $"{uri} has no description.");
        Assert.Equal("text/markdown", resource.MimeType);
    }

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void ServerInstructions_EveryTool_IsNamed(string name)
    {
        // Under tool search the instructions are what make the model look a tool up at all; a tool they never mention is
        // found only by luck.
        Assert.Contains(name, _server.Client.ServerInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerInstructions_LaterBuildsLine_NoLongerPromisesRulesLookup()
    {
        // A promise of something that already exists tells the model the rules tools are not there yet.
        var later = _server.Client.ServerInstructions!.Split('\n').Single(l => l.StartsWith("More tools arrive", StringComparison.Ordinal));

        Assert.DoesNotContain("rules", later, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerInstructions_LaterBuildsLine_NoLongerPromisesEncounterDifficulty()
    {
        var later = _server.Client.ServerInstructions!.Split('\n').Single(l => l.StartsWith("More tools arrive", StringComparison.Ordinal));

        Assert.DoesNotContain("encounter", later, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerInstructions_LaterBuildsLine_NoLongerPromisesDamagePerRound()
    {
        // balance_dpr and balance_compare exist now; "damage-per-round … arrive in later builds" would tell the model they don't.
        var later = _server.Client.ServerInstructions!.Split('\n').Single(l => l.StartsWith("More tools arrive", StringComparison.Ordinal));

        Assert.DoesNotContain("damage", later, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("balance", later, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("campaign tracking", later, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerInstructions_LaterBuildsLine_NamesOnlyCampaignTracking()
    {
        // balance_simulate exists now; "Monte Carlo combat simulation arrives in later builds" would tell the model it doesn't.
        var later = _server.Client.ServerInstructions!.Split('\n').Single(l => l.StartsWith("More tools arrive", StringComparison.Ordinal));

        Assert.Equal("More tools arrive in later builds: campaign tracking.", later);
    }

    [Fact]
    public void ServerInstructions_SimulationAndCombatantFormat_AreNamedWithWhatTheyAnswer()
    {
        // Under tool search the instructions decide whether "can my party survive this?" reaches balance_simulate at all,
        // and whether a surprising result leads to the stat block as the simulator read it.
        var instructions = _server.Client.ServerInstructions!;

        Assert.Contains("- balance_simulate: Monte Carlo fights", instructions, StringComparison.Ordinal);
        Assert.Contains("class archetypes", instructions, StringComparison.Ordinal);
        Assert.Contains("format \"combatant\" shows a monster as the simulator reads it", instructions, StringComparison.Ordinal);
        // F's Phase 4 fix: a verdict compares against the official option.
        Assert.Contains("for a verdict, baseline = the official option (a feat: the ASI it replaces)", instructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("balance_dpr", "build", "target", "levels", "ac_range", "horizon", "rounds", "rest_preset", "encounters_per_day", "short_rests", "rulings")]
    [InlineData("balance_compare", "baseline", "variant", "feature", "target", "levels", "horizon", "rounds", "rest_preset", "encounters_per_day", "short_rests", "rulings")]
    [InlineData("balance_simulate", "party", "enemies", "iterations", "seed", "round_cap", "edition", "surprise", "enemy_hp", "precision", "replay", "policies", "compare", "rulings")]
    public async Task BalanceTools_Description_NamesEveryArgumentAndGivesAnExample(string name, params string[] arguments)
    {
        // MCP has no input_examples: the description is where the model learns each argument and sees one whole call.
        var tool = await GetToolAsync(name);
        var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Equal(arguments.Order(StringComparer.Ordinal), properties.Order(StringComparer.Ordinal));
        Assert.All(arguments, a => Assert.Contains(a, tool.Description, StringComparison.Ordinal));
        Assert.Contains("\nExample: {", tool.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("balance_dpr", "build")]
    [InlineData("balance_compare", "baseline")]
    [InlineData("balance_compare", "feature")]
    public async Task BalanceTools_BuildSchema_TypesNestedObjectsAndLeavesStepValuesUntyped(string name, string parameter)
    {
        // The spec classes are the schema: attacks and modifiers are typed objects the argument guard can check field by
        // field, while step values ("a number or {\"1\": 1, \"5\": 2}") must stay untyped or the SDK's binder refuses one form.
        var schema = (await GetToolAsync(name)).JsonSchema.GetProperty("properties").GetProperty(parameter);
        var attack = schema.GetProperty("properties").GetProperty("attacks").GetProperty("items");
        var modifier = schema.GetProperty("properties").GetProperty("modifiers").GetProperty("items");

        Assert.Contains("object", Types(attack));
        Assert.Contains("object", Types(modifier));
        foreach (var stepValue in new[] { attack.GetProperty("properties").GetProperty("count"), attack.GetProperty("properties").GetProperty("damage"), modifier.GetProperty("properties").GetProperty("amount") })
        {
            Assert.False(stepValue.TryGetProperty("type", out _), $"{name}.{parameter}: a step value has a type: {stepValue}");
            Assert.Contains("step map", stepValue.GetProperty("description").GetString(), StringComparison.Ordinal);
        }
    }

    // "object" or ["object", "null"]: the SDK writes either, depending on the property's nullability.
    private static List<string> Types(JsonElement schema) =>
        schema.GetProperty("type") is { ValueKind: JsonValueKind.Array } types
            ? types.EnumerateArray().Select(t => t.GetString()!).ToList()
            : [schema.GetProperty("type").GetString()!];

    [Fact]
    public async Task BalanceCompare_Variant_IsPublishedUntypedWithoutASecondBuildSchema()
    {
        // A second copy of the build schema made balance_compare's definition 38 KB; variant is the baseline's shape, checked
        // by the argument guard against baseline's schema (SameShapeAsAttribute). Pinned so the copy does not creep back.
        var schema = (await GetToolAsync("balance_compare")).JsonSchema;
        var variant = schema.GetProperty("properties").GetProperty("variant");

        Assert.False(variant.TryGetProperty("properties", out _));
        Assert.False(variant.TryGetProperty("type", out _));
        Assert.Contains("same fields as baseline", variant.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.True(schema.GetRawText().Length < 32_000, $"balance_compare's input schema is {schema.GetRawText().Length} characters.");
    }

    [Fact]
    public async Task BalanceSimulate_EnemiesAndCompare_ArePublishedUntypedAndTheSchemaStaysUnder32K()
    {
        // enemies has party's shape (SameShapeAsAttribute) and compare's feature is a whole build's worth of attacks and
        // modifiers (CheckedAsAttribute): typed, the two made the definition 31.8 KB of its 32 KB budget. The argument guard
        // checks both exactly as typed parameters (ToolErrorTests); pinned here so the copies do not creep back.
        var schema = (await GetToolAsync("balance_simulate")).JsonSchema;
        var properties = schema.GetProperty("properties");

        foreach (var untyped in new[] { "enemies", "compare" })
        {
            Assert.False(properties.GetProperty(untyped).TryGetProperty("properties", out _), untyped);
            Assert.False(properties.GetProperty(untyped).TryGetProperty("type", out _), untyped);
        }

        Assert.Contains("same fields as party", properties.GetProperty("enemies").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("attacks, modifiers", properties.GetProperty("compare").GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(["enemies", "party"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal));
        Assert.True(schema.GetRawText().Length < 24_000, $"balance_simulate's input schema is {schema.GetRawText().Length} characters.");
    }

    [Fact]
    public async Task BalanceSimulate_Description_NamesEveryArchetype()
    {
        // The archetypes are how "simulate this fight for a level 5 party" avoids four hand-written builds; a model that
        // is not told the names guesses them. The description is a constant, so this pins it to the catalogue.
        var description = (await GetToolAsync("balance_simulate")).Description;

        Assert.All(DndMcp.Domain.Simulation.Archetypes.ArchetypeCatalog.Names, name => Assert.Contains(name, description, StringComparison.Ordinal));
        Assert.Contains("{\"monster\": \"ogre\", \"count\": 3}", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesGet_Description_ListsEveryFormatFromOnePlace()
    {
        // The formats, the description and the format error share SrdMarkdown.FormatsText, so they cannot disagree.
        var description = (await GetToolAsync("rules_get")).Description;

        Assert.Contains("- format: " + DndMcp.Formatting.Srd.SrdMarkdown.FormatsText + ".", description, StringComparison.Ordinal);
        Assert.All(DndMcp.Formatting.Srd.SrdMarkdown.Formats, f => Assert.Contains($"\"{f}\"", DndMcp.Formatting.Srd.SrdMarkdown.FormatsText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RulesTables_InstructionsAndBothToolDescriptions_SayHowToReachThem()
    {
        // The tables are resources, which Claude Desktop only attaches by hand; the model must be told rules_get serves them.
        var tools = await _server.Client.ListToolsAsync();

        Assert.Contains("rules_get ref \"rules://tables\"", _server.Client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("\"rules://tables\" lists the rules tables", tools.Single(t => t.Name == "rules_get").Description, StringComparison.Ordinal);
        Assert.Contains("rules_get with ref \"rules://tables\"", tools.Single(t => t.Name == "encounter_difficulty").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesScope_InstructionsAndRulesGet_SayThe2024RulesAreTheRulesGlossary()
    {
        // 37 glossary entries end "See also 'Playing the Game' (…)", a chapter this server does not serve. Told nothing, a
        // model spends calls searching for the fuller 2024 chapter text and then answers from memory.
        var rulesGet = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "rules_get");

        Assert.Contains("2024 rules are the SRD 5.2.1 Rules Glossary", _server.Client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains("2024 rules are the SRD 5.2.1 Rules Glossary", rulesGet.Description, StringComparison.Ordinal);
    }

    public static TheoryData<string> Missing2024Chapters => new(DndMcp.Tools.RulesTools.Missing2024Chapters);

    [Theory]
    [MemberData(nameof(Missing2024Chapters))]
    public async Task RulesScope_InstructionsAndBothRulesTools_NameEveryChapterTheDataLacks(string chapter)
    {
        // Told only that "Playing the Game" was missing, models kept searching for multiclassing, travel pace or the
        // one-spell-slot-per-turn rule, then answered from memory as if quoting the SRD.
        var tools = await _server.Client.ListToolsAsync();

        Assert.Contains(chapter, _server.Client.ServerInstructions, StringComparison.Ordinal);
        Assert.Contains(chapter, tools.Single(t => t.Name == "rules_get").Description, StringComparison.Ordinal);
        Assert.Contains(chapter, tools.Single(t => t.Name == "rules_search").Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RulesScope_InstructionsAndBothRulesTools_SayMulticlassingRulesAreNotInTheData()
    {
        // FTS finds no multiclassing prose in either edition; only each class's prerequisites and proficiencies exist.
        var tools = await _server.Client.ListToolsAsync();

        foreach (var text in new[]
                 {
                     _server.Client.ServerInstructions!,
                     tools.Single(t => t.Name == "rules_get").Description,
                     tools.Single(t => t.Name == "rules_search").Description,
                 })
        {
            Assert.Contains("multiclassing rules in either edition", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RulesGet_Description_SaysTheAttributionRefWorks()
    {
        // The attribution resource is reachable through rules_get; a model is told so where it reads about refs.
        var rulesGet = (await _server.Client.ListToolsAsync()).Single(t => t.Name == "rules_get");

        Assert.Contains("\"rules://attribution\" gives the SRD licence text", rulesGet.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_ManyServersStartingAtOnce_EachExposesTheSameSchemas()
    {
        // ModelContextProtocol 2.2.0 intermittently leaves an injected parameter (IProgress, for the rules tools) in the
        // input schema when two threads build the same tool at once. Production builds one server per process; the test
        // suite builds dozens in parallel, so McpServerHarness builds them one at a time. Without that, other tests see a
        // phantom "progress" argument now and then; this makes the race happen on purpose.
        var expected = await SchemaPropertiesAsync(_server);
        var harnesses = Enumerable.Range(0, 12).Select(_ => new McpServerHarness()).ToList();
        using var barrier = new Barrier(harnesses.Count);
        try
        {
            await Task.WhenAll(harnesses.Select(h => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                await h.InitializeAsync();
            })));

            foreach (var harness in harnesses)
            {
                Assert.Equal(expected, await SchemaPropertiesAsync(harness));
            }
        }
        finally
        {
            foreach (var harness in harnesses)
            {
                await harness.DisposeAsync();
            }
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

    private static async Task<List<string>> SchemaPropertiesAsync(McpServerHarness server) =>
        (await server.Client.ListToolsAsync())
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => $"{t.Name}: {string.Join(", ", t.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name))}")
            .ToList();

    private async Task<McpClientTool> GetToolAsync(string name)
    {
        var tools = await _server.Client.ListToolsAsync();
        return Assert.Single(tools, t => t.Name == name);
    }
}
