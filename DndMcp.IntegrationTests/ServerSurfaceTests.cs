using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: everything Claude Code reads before it calls a tool — the tool list, each tool's name,
/// description, title, annotations and parameter names, the prompts and their arguments, the resource list, and the
/// server's name, version and instructions — is exactly what we intend and within the limits Claude Code and the Claude
/// API enforce.
///
/// <para>
/// None of these break loudly. A tool name with a dot is registered happily by the SDK and then rejected by the
/// Claude API; a description over 2,048 characters is silently truncated by Claude Code; a missing annotation
/// is read by the MCP spec as destructive and open-world; a prompt argument moved one place changes what a typed
/// <c>/mcp__dnd__knowledge_check belmakor</c> means; a resource URI that gains a parameter vanishes from the model's
/// resource list. So each is pinned here, per tool, prompt and resource.
/// </para>
/// <para>
/// The class fixture's server has an empty data directory (no campaigns.db), so its resource list is exactly the static
/// one; tests that need a campaign use <see cref="OneCampaignServer"/> or a server of their own.
/// </para>
/// <para>
/// Not covered here: the <c>mcp__dnd__</c> prefix on every tool name, which users' allow-lists match. Claude Code
/// builds it from the name the server is registered under (<c>claude mcp add ... dnd</c> in the README), not from
/// anything the server sends, so no in-process test can see it.
/// </para>
/// </summary>
public sealed partial class ServerSurfaceTests : IClassFixture<McpServerHarness>, IClassFixture<OneCampaignServer>
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
        "campaign",
        "campaign_get",
        "campaign_history",
        "campaign_knowledge",
        "campaign_search",
        "campaign_session",
        "campaign_write",
        "dice_odds",
        "dice_roll",
        "encounter_difficulty",
        "rules_get",
        "rules_search",
    ];

    /// <summary>
    /// THE hints each tool declares (readOnly, destructive, idempotent, openWorld), as PLAN.md's tool table and the Phase 6
    /// contract give them.
    /// Claude Code decides what it auto-approves or warns about from these, so a flipped hint must be a deliberate diff
    /// here: checking only that each is set let openWorld become true or idempotent false with every test green.
    /// Of the rules, dice and balance tools, only dice_roll and balance_simulate are not idempotent: without a seed the same
    /// call rolls again. dice_roll is not read-only: while a campaign session is live it appends its rolls to campaigns.db.
    /// Of the campaign tools only campaign_search and campaign_get are read-only (and so idempotent); every other one writes
    /// campaigns.db and is not idempotent (a second call is a second batch, a second campaign, a second start refused).
    /// campaign_write (the delete op), campaign_knowledge (retract deletes rows) and campaign_history (undo deletes what a
    /// batch created) are destructive; campaign and campaign_session never delete anything. All are closed-world: they
    /// touch nothing but campaigns.db.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (bool ReadOnly, bool Destructive, bool Idempotent, bool OpenWorld)> ExpectedAnnotations =
        new Dictionary<string, (bool, bool, bool, bool)>
        {
            ["balance_compare"] = (true, false, true, false),
            ["balance_dpr"] = (true, false, true, false),
            ["balance_simulate"] = (true, false, false, false),
            ["campaign"] = (false, false, false, false),
            ["campaign_get"] = (true, false, true, false),
            ["campaign_history"] = (false, true, false, false),
            ["campaign_knowledge"] = (false, true, false, false),
            ["campaign_search"] = (true, false, true, false),
            ["campaign_session"] = (false, false, false, false),
            ["campaign_write"] = (false, true, false, false),
            ["dice_odds"] = (true, false, true, false),
            ["dice_roll"] = (false, false, false, false),
            ["encounter_difficulty"] = (true, false, true, false),
            ["rules_get"] = (true, false, true, false),
            ["rules_search"] = (true, false, true, false),
        };

    /// <summary>
    /// THE list of resources with no campaigns, for the same reason. Claude Code offers each as an <c>@dnd:</c> mention and
    /// adds tools to read them, so a resource appearing or vanishing changes what the model can reach. Each campaign then
    /// adds its own <c>campaign://&lt;slug&gt;/summary</c> and <c>/threads</c>, listed at request time
    /// (<see cref="ListResources_OneCampaign_AddsItsSummaryAndThreadsAndStillNoTemplates"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> ExpectedResourceUris =
    [
        "campaign://list",
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

    /// <summary>
    /// THE prompts (Claude Code's <c>/mcp__dnd__&lt;name&gt;</c> commands), each with its arguments IN ORDER and whether
    /// each is required. Claude Code splits a typed command on whitespace and maps the tokens to the arguments by position,
    /// dropping extras, so the order is the interface: an argument added, moved or renamed changes what
    /// <c>/mcp__dnd__knowledge_check belmakor</c> means with no error anywhere, and an injected parameter leaking into the
    /// list (the SDK race the harness serializes) would appear here as an argument.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Name, bool Required)[]> ExpectedPrompts =
        new Dictionary<string, (string Name, bool Required)[]>(StringComparer.Ordinal)
        {
            ["continuity_check"] = [("campaign", false)],
            ["homebrew_review"] = [("campaign", false)],
            ["in_character"] = [("character", true), ("campaign", false)],
            ["knowledge_check"] = [("character", true), ("campaign", false)],
            ["session_prep"] = [("campaign", false), ("session", false)],
            ["session_recap"] = [("campaign", false), ("session", false)],
        };

    /// <summary>
    /// THE version this build ships (serverInfo.version, from DndMcp.csproj, and both versions in DndMcp/.mcp/server.json).
    /// Bumped by hand with each phase's release, together with those three. Checking only that they agree passes with all
    /// three left at the last release, and then the registry and <c>claude mcp list</c> show a new server under an old
    /// version, which is how an update looks like nothing changed.
    /// </summary>
    public const string ExpectedVersion = "0.6.0";

    // Claude Code truncates tool descriptions and server instructions here (CLAUDE_CODE_MAX_MCP_DESCRIPTION_LENGTH).
    private const int DescriptionLimit = 2048;

    // PLAN's budget for one tool's input schema: the whole definition is loaded into context when the tool is picked.
    private const int SchemaLimit = 32_000;

    // The contract's budget for campaign_write's typed ops before they must be published untyped (CheckedAsAttribute).
    private const int CampaignWriteSchemaLimit = 24_000;

    private const string LaterBuildsLine = "More tools arrive in later builds: character sheets, combat tracking, markdown export and import.";

    private readonly McpServerHarness _server;
    private readonly OneCampaignServer _campaign;

    public ServerSurfaceTests(McpServerHarness server, OneCampaignServer campaign)
    {
        _server = server;
        _campaign = campaign;
    }

    public static TheoryData<string> ToolNames => new(ExpectedToolNames);

    public static TheoryData<string> ResourceUris => new(ExpectedResourceUris);

    public static TheoryData<string> PromptNames => new(ExpectedPrompts.Keys.Order(StringComparer.Ordinal));

    // The Claude API tool-name rule, tightened to Claude Code's 64-character cap. The SDK allows '.', the API does not.
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolNameRegex();

    // Claude's rule for input-schema property names.
    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,64}$")]
    private static partial Regex SchemaPropertyNameRegex();

    // A tool call a prompt's text prints for the model to send: the tool's bare name, a space, then its JSON arguments.
    [GeneratedRegex(@"(?<![A-Za-z_])([a-z]+(?:_[a-z]+)*) \{""")]
    private static partial Regex PromptToolCallRegex();

    // The action list a campaign tool's refusal of an unknown action gives: "is not one of a, b, c." or "give a, b, c.".
    [GeneratedRegex(@"(?:is not one of|give) (?<actions>[a-z_]+(?:, [a-z_]+)+)\. Example")]
    private static partial Regex ActionListRegex();

    // A sentence that says which actions take an argument: "record, reveal and retract take session …".
    [GeneratedRegex(@"(?<![A-Za-z0-9_])takes?(?![A-Za-z0-9_])")]
    private static partial Regex TakeWordRegex();

    // A quoted value in a description ("full", "2026-09-19"): an example of a value, not the name of an argument.
    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex QuotedValueRegex();

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

    [Theory]
    [MemberData(nameof(ToolNames))]
    public async Task ToolInputSchema_EveryTool_StaysUnderTheSchemaBudget(string name)
    {
        // The whole input schema is loaded into context when tool search picks the tool; balance_compare, the largest,
        // has its own tighter pin below. A new typed spec (a campaign op field, a nested object) grows one of these quietly.
        var length = (await GetToolAsync(name)).JsonSchema.GetRawText().Length;

        Assert.True(length < SchemaLimit, $"{name}'s input schema is {length} characters; the budget is {SchemaLimit}.");
    }

    // Every property, and every field of an object nested in it (encounter_difficulty's monsters items, campaign_write's
    // ops and their gates, knowers and routes), has a valid name and a description: the description is the only hint the
    // model gets about format ("2d6+3", "1-100", "1/2", "character:<slug>").
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

    [Fact]
    public async Task ListResources_OneCampaign_AddsItsSummaryAndThreadsAndStillNoTemplates()
    {
        // A campaign's resources are listed by a handler at request time, not registered, so nothing above sees them. A
        // server of its own: a campaign in the class fixture's data directory would change the list pinned above. The
        // client learns of them only through list_changed, which Claude Code answers by listing again.
        var server = new McpServerHarness();
        await server.InitializeAsync();
        try
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = server.Client.RegisterNotificationHandler(
                NotificationMethods.ResourceListChangedNotification,
                (_, _) =>
                {
                    changed.TrySetResult();
                    return ValueTask.CompletedTask;
                });

            server.SuccessText(await server.CallToolJsonAsync("campaign", """{"action": "create", "name": "Sky World", "role": "dm", "ruleset": "2014", "slug": "sky"}"""));
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var resources = await server.Client.ListResourcesAsync();
            string[] added = ["campaign://sky/summary", "campaign://sky/threads"];
            Assert.Equal(
                ExpectedResourceUris.Concat(added).Order(StringComparer.Ordinal),
                resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
            foreach (var uri in added)
            {
                var resource = Assert.Single(resources, r => r.Uri == uri).ProtocolResource;
                Assert.False(string.IsNullOrWhiteSpace(resource.Name), $"{uri} has no name.");
                Assert.False(string.IsNullOrWhiteSpace(resource.Title), $"{uri} has no title; /mcp would show the raw name.");
                Assert.False(string.IsNullOrWhiteSpace(resource.Description), $"{uri} has no description.");
                Assert.Equal("text/markdown", resource.MimeType);

                var contents = Assert.IsType<TextResourceContents>(Assert.Single((await server.Client.ReadResourceAsync(uri)).Contents));
                Assert.Equal(uri, contents.Uri);
                Assert.Equal("text/markdown", contents.MimeType);
                Assert.StartsWith("# ", contents.Text, StringComparison.Ordinal);
            }

            Assert.Empty(await server.Client.ListResourceTemplatesAsync());
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ListPrompts_Server_ExposesExactlyTheExpectedPrompts()
    {
        var prompts = await _server.Client.ListPromptsAsync();
        var actual = prompts.Select(p => p.Name).ToList();

        Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ExpectedPrompts.Keys.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(PromptNames))]
    public async Task PromptName_EveryPrompt_MatchesClaudeApiNameRule(string name)
    {
        // Claude Code runs it as /mcp__dnd__<name>, using the name verbatim.
        var prompt = await GetPromptAsync(name);

        Assert.Matches(ToolNameRegex(), prompt.Name);
    }

    [Theory]
    [MemberData(nameof(PromptNames))]
    public async Task PromptTitle_EveryPrompt_IsSet(string name)
    {
        var prompt = await GetPromptAsync(name);

        Assert.False(string.IsNullOrWhiteSpace(prompt.ProtocolPrompt.Title), $"{name} has no title; /mcp would show the raw name.");
    }

    [Theory]
    [MemberData(nameof(PromptNames))]
    public async Task PromptDescription_EveryPrompt_IsNonEmptyAndWithinTruncationLimit(string name)
    {
        var description = (await GetPromptAsync(name)).ProtocolPrompt.Description;

        Assert.False(string.IsNullOrWhiteSpace(description), $"{name} has no description.");
        Assert.True(
            description.Length <= DescriptionLimit,
            $"{name}'s description is {description.Length} characters; Claude Code truncates at {DescriptionLimit}.");
    }

    /// <summary>
    /// Each prompt's description ends with its usage as the user types it: the prompt's own bare name, then its arguments in
    /// <see cref="ExpectedPrompts"/>' order, required ones as &lt;name&gt; and optional ones as [name]. The usage is what a user
    /// copies, so a usage line naming another prompt, or arguments in another order, puts each typed token in the wrong
    /// argument; and it carries no server prefix (Claude Code builds /mcp__&lt;server&gt;__ from the name the server was
    /// registered under, which the server cannot know: a dev server's usage line said /mcp__dnd__, FH4).
    /// </summary>
    [Theory]
    [MemberData(nameof(PromptNames))]
    public async Task PromptDescription_EveryPrompt_EndsWithItsOwnUsageInArgumentOrder(string name)
    {
        var description = (await GetPromptAsync(name)).ProtocolPrompt.Description;
        var usage = string.Join(' ', ExpectedPrompts[name].Select(a => a.Required ? $"<{a.Name}>" : $"[{a.Name}]"));

        Assert.EndsWith($" Usage: {name} {usage}.", description, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PromptNames))]
    public async Task PromptArguments_EveryPrompt_AreExactlyTheExpectedDescribedArgumentsInOrder(string name)
    {
        var arguments = (await GetPromptAsync(name)).ProtocolPrompt.Arguments ?? [];

        Assert.Equal(ExpectedPrompts[name], arguments.Select(a => (a.Name, a.Required ?? false)).ToArray());
        Assert.All(arguments, a =>
        {
            Assert.Matches(SchemaPropertyNameRegex(), a.Name);
            Assert.False(string.IsNullOrWhiteSpace(a.Description), $"{name}.{a.Name} has no description; the command's usage hint would be bare.");
        });
    }

    [Theory]
    [InlineData("session_recap", "campaign_session", "campaign_search", "campaign_get", "campaign_write", "campaign_history")]
    [InlineData("session_prep", "campaign", "campaign_session", "campaign_search", "campaign_get", "encounter_difficulty", "balance_simulate")]
    [InlineData("knowledge_check", "campaign_knowledge")]
    [InlineData("continuity_check", "campaign_search", "campaign_get", "campaign_session", "campaign_history", "campaign_knowledge")]
    [InlineData("in_character", "campaign_search", "campaign_get", "campaign_knowledge")]
    [InlineData("homebrew_review", "balance_compare", "campaign_search", "campaign_write")]
    public async Task GetPrompt_EveryPrompt_IsOneUserMessageDrivingOnlyToolsTheServerHas(string name, params string[] drives)
    {
        // A prompt is instructions naming tools by their bare names (campaign_write: the server cannot know the name the
        // client registered it under, so never mcp__dnd__campaign_write), which tool search matches. A tool renamed or
        // dropped would leave every prompt that names it steering the model at a tool that does not exist, and nothing else
        // checks a prompt's text against the tool list: every call it prints must name a tool the server has, and every
        // tool it drives must be named.
        Assert.Contains(name, ExpectedPrompts.Keys);
        var arguments = ExpectedPrompts[name].ToDictionary(
            a => a.Name,
            a => (object?)(a.Name switch
            {
                "character" => OneCampaignServer.Character["character:".Length..],
                "session" => "1",
                _ => OneCampaignServer.Slug,
            }));

        var result = await _campaign.Harness.Client.GetPromptAsync(name, arguments);

        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);
        var text = Assert.IsType<TextContentBlock>(message.Content).Text;
        var called = PromptToolCallRegex().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(called);
        Assert.All(called, tool => Assert.Contains(tool, ExpectedToolNames));
        var named = ExpectedToolNames.Where(tool => Regex.IsMatch(text, $"(?<![A-Za-z_]){tool}(?![A-Za-z_])")).ToHashSet(StringComparer.Ordinal);
        Assert.All(drives, tool => Assert.Contains(tool, named));
        Assert.DoesNotContain("mcp__", text, StringComparison.Ordinal);
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
        var later = InstructionLine("More tools arrive");

        Assert.DoesNotContain("rules", later, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerInstructions_LaterBuildsLine_NoLongerPromisesEncounterDifficulty()
    {
        var later = InstructionLine("More tools arrive");

        Assert.DoesNotContain("encounter", later, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerInstructions_LaterBuildsLine_NoLongerPromisesDamagePerRoundOrSimulation()
    {
        // balance_dpr, balance_compare and balance_simulate exist; "damage-per-round … arrive in later builds" or "combat
        // simulation arrives" would tell the model they don't. ("combat tracking" is Phase 7's live tracker, not these.)
        var later = InstructionLine("More tools arrive");

        Assert.DoesNotContain("damage", later, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("balance", later, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("simulat", later, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerInstructions_LaterBuildsLine_NamesOnlyCharacterSheetsCombatTrackingAndMarkdownTransfer()
    {
        // The seven campaign tools exist now; "More tools arrive in later builds: campaign tracking." would tell the model
        // they don't. What is still to come: Phase 7's character sheets and combat tracker, Phase 8's markdown export/import.
        var later = InstructionLine("More tools arrive");

        Assert.Equal(LaterBuildsLine, later);
        Assert.DoesNotContain("campaign", later, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerInstructions_EditionDefault_IsTheActiveCampaignsRulesetElse2024()
    {
        // The rules, encounter and balance tools take a campaign's ruleset as their default edition. Told "Rules default to
        // 2024", a model in a 2014 campaign passes edition 2024 explicitly and overrides the default it was meant to get.
        var instructions = _server.Client.ServerInstructions!;

        Assert.Contains("Editions default to the active campaign's ruleset, else 2024.", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("default to 2024", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerInstructions_DiceRoll_SaysRollsAreLoggedToALiveSession()
    {
        // During play a roll the model narrates instead of making is lost to the session's record. The sentence is pinned
        // whole: a substring would still pass if the line said the rolls were "never logged to a live campaign session".
        Assert.StartsWith(
            "- dice_roll: any roll the user wants made, logged to a live campaign session.",
            InstructionLine("- dice_roll: "),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("- campaign: ", "create", "use", "summary")]
    [InlineData("- campaign_search, campaign_get: ", "find and read entries and facts", "perspective \"character:<slug>\"", "only what they know",
        "by the names they know")]
    [InlineData("- campaign_write: ", "one batch of ops", "dry_run first", "F-codes")]
    [InlineData("- campaign_knowledge: ", "record", "reveal", "check a draft", "\"does X know this?\"", "gates", "forbidden words",
        "ledger (who knows what)")]
    [InlineData("- campaign_session: ", "plan", "start", "end (saves the recap)", "record_past (a past night)", "recap (what was learned)")]
    [InlineData("- campaign_history: ", "since", "as_of", "undo a batch")]
    public void ServerInstructions_CampaignTools_EachLineSaysWhatTheToolIsFor(string line, params string[] words)
    {
        // Under tool search this line is all the model knows of the tool until it picks it. "Does Belmakor know the old
        // king's name?" reaches campaign_knowledge only if the line says so; answered from the conversation instead, it
        // uses the author's view, true names included. The glosses settle the requests whose words two tools share:
        // "record what happened tonight" is the session's end (which saves the recap), not campaign_knowledge's record;
        // "what did the party learn last session?" is the session recap; "who knows that?" is the knowledge ledger.
        var text = InstructionLine(line);

        Assert.All(words, word => Assert.Contains(word, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RulesScope_InstructionsAndRulesGet_ExceptTheEncounterBudgetFromTheMissingGameplayToolbox()
    {
        // The 2024 encounter budget is the one part of the Gameplay Toolbox this server has (encounter_difficulty and
        // rules://tables/xp-budget-2024). Told that the whole chapter is missing and to say so "rather than searching again",
        // a model answers a 2024 encounter-building question with "not in this server's data" (the Phase 3 review fix).
        var rulesGet = await GetToolAsync("rules_get");

        Assert.Contains("Gameplay Toolbox chapters (except its encounter budget)", InstructionLine("- rules_get: "), StringComparison.Ordinal);
        Assert.Contains("2024 XP budget", InstructionLine("- encounter_difficulty: "), StringComparison.Ordinal);
        Assert.Contains("Gameplay Toolbox chapters (apart from its encounter budget", rulesGet.Description, StringComparison.Ordinal);
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
    [InlineData("campaign", "action", "campaign", "name", "role", "ruleset", "dm_name", "slug", "settings", "party_name", "my_character", "status",
        "summary_md", "current_location", "current_ingame", "perspective", "reason", "session", "dry_run")]
    [InlineData("campaign_search", "query", "kinds", "statuses", "tags", "perspective", "include_facts", "as_of_session", "limit", "cursor", "campaign")]
    [InlineData("campaign_get", "refs", "include", "detail", "perspective", "as_of_session", "campaign")]
    [InlineData("campaign_write", "ops", "campaign", "session", "reason", "dry_run")]
    [InlineData("campaign_knowledge", "action", "campaign", "targets", "knowers", "facts", "secret", "handout", "to", "how", "who", "text", "perspective",
        "diegetic", "audience", "about", "perspectives", "as_of_session", "session", "reason", "dry_run")]
    [InlineData("campaign_session", "action", "campaign", "session", "title", "arc", "prep_md", "played_on", "precision", "attendance", "ingame",
        "ingame_end", "notes", "recap_md", "next_hooks", "confidence", "status", "limit", "cursor", "perspective", "reason", "dry_run")]
    [InlineData("campaign_history", "action", "since", "session", "targets", "ref", "refs", "detail", "batch_id", "dry_run", "reason", "limit",
        "cursor", "campaign")]
    public async Task ToolDescription_BalanceAndCampaignTools_NamesEveryArgumentAndGivesAnExample(string name, params string[] arguments)
    {
        // MCP has no input_examples: the description is where the model learns each argument and sees one whole call. Each
        // name must appear as a word of its own ("ref" inside "refs" does not count) outside the example and outside quoted
        // values (the example's "seed": 42 documents nothing), and the list is the schema's exactly, so an argument added to
        // the tool fails here until the description (and this row) names it. Where each campaign argument must be named is
        // pinned by the two tests below: a whole-word search is satisfied by "to" in "to an audience".
        var tool = await GetToolAsync(name);
        var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        var prose = Prose(tool.Description);

        Assert.Equal(arguments.Order(StringComparer.Ordinal), properties.Order(StringComparer.Ordinal));
        Assert.All(arguments, a => Assert.Matches($@"(?<![A-Za-z0-9_]){Regex.Escape(a)}(?![A-Za-z0-9_])", prose));
        Assert.Contains("\nExample: {", tool.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign_search")]
    [InlineData("campaign_get")]
    [InlineData("campaign_write")]
    public async Task ToolDescription_CampaignToolsWithoutActions_DocumentEveryArgumentOnALineOrClauseOfItsOwn(string name)
    {
        // Most of these names are also words ("query", "include", "detail", "session", "reason"), so a whole-word search finds
        // them whether or not the argument is documented. These descriptions give every argument a line ("- detail: …",
        // "- refs (required): …") or a clause ("…; cursor: …"), which is where the model looks for it; one that loses its
        // line fails here even while the word survives elsewhere.
        var tool = await GetToolAsync(name);
        var undocumented = tool.JsonSchema.GetProperty("properties").EnumerateObject()
            .Select(p => p.Name)
            .Where(argument => !tool.Description.Split('\n').Any(line => ArgumentLine(argument).IsMatch(line)))
            .ToList();

        Assert.True(undocumented.Count == 0, $"{name}'s description has no line or clause for: {string.Join(", ", undocumented)}.");
    }

    [Theory]
    [InlineData("campaign", "- action \"{0}\"")]
    [InlineData("campaign_history", "- action \"{0}\"")]
    [InlineData("campaign_knowledge", "- {0}:")]
    [InlineData("campaign_session", "- {0}:")]
    public async Task ToolDescription_ActionTools_DocumentEveryArgumentOfEachActionWhereThatActionIsDescribed(string name, string actionLine)
    {
        // One flat parameter list serves every action, so a word search over the whole description proves nothing: with
        // their documentation deleted, "to" and "how" still occur in prose and in a knower's field list, and "since" as the
        // since action's own name. What each action takes is the tool's own table, which its refusal prints ("reveal takes
        // facts, secret, handout, to, how, …"). Each of those arguments must be named AS AN ARGUMENT (glossed, "since (a UTC
        // date …)", or in an argument list) where the model reads about that action: on the action's own line, in a sentence
        // saying which actions take it ("record, reveal and retract take session …", "Every action takes campaign"), or on a
        // line or clause of its own ("- limit: …; cursor: …"). Quoted values are not documentation, nor is the example.
        var lines = (await GetToolAsync(name)).Description.Split('\n');
        var actions = await ActionsAsync(name);
        var actionLines = actions.ToDictionary(
            action => action,
            action => Assert.Single(lines, l => l.StartsWith(string.Format(CultureInfo.InvariantCulture, actionLine, action), StringComparison.Ordinal)));
        var otherLines = lines.Where(l => !actionLines.ContainsValue(l) && !l.StartsWith("Example: ", StringComparison.Ordinal)).ToList();
        var sentences = otherLines.SelectMany(l => Unquoted(l).Split(". ")).ToList();
        var undocumented = new List<string>();

        foreach (var action in actions)
        {
            var own = Unquoted(actionLines[action][string.Format(CultureInfo.InvariantCulture, actionLine, action).Length..]).TrimStart(' ', ':');
            var takers = sentences
                .Where(s => TakeWordRegex().IsMatch(s) && (WholeWord(action).IsMatch(s) || s.Contains("Every action", StringComparison.Ordinal)))
                .ToList();
            foreach (var argument in await ActionTakesAsync(name, action))
            {
                if (!NamedAsArgument(own, argument) && !takers.Any(s => NamedAsArgument(s, argument)) && !otherLines.Any(l => ArgumentLine(argument).IsMatch(l)))
                {
                    undocumented.Add($"{action} {argument}");
                }
            }
        }

        Assert.True(undocumented.Count == 0, $"{name}'s description does not document, where the action is described: {string.Join("; ", undocumented)}.");
    }

    [Theory]
    [InlineData("campaign", "- action \"{0}\"")]
    [InlineData("campaign_history", "- action \"{0}\"")]
    [InlineData("campaign_knowledge", "- {0}: ")]
    [InlineData("campaign_session", "- {0}: ")]
    public async Task ToolDescription_ActionTools_DescribeEveryActionTheToolAccepts(string name, string actionLine)
    {
        // The actions a tool accepts are its own vocabulary; its refusal of an unknown action lists them. A new action the
        // description never mentions is one the model will not use, and one the description promises but the tool refuses
        // costs a failed call; both fail here.
        var actions = await ActionsAsync(name);
        var description = (await GetToolAsync(name)).Description;

        Assert.All(actions, action => Assert.Contains(string.Format(CultureInfo.InvariantCulture, actionLine, action), description, StringComparison.Ordinal));
    }

    // The actions an action tool accepts, as its refusal of an unknown action lists them.
    private async Task<string[]> ActionsAsync(string name)
    {
        var refusal = _server.ErrorText(await _server.CallToolJsonAsync(name, """{"action": "no_such_action"}"""));
        var actions = ActionListRegex().Match(refusal).Groups["actions"].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries);

        Assert.True(actions.Length >= 5, $"No action list in {name}'s refusal: {refusal}");
        return actions;
    }

    // What one action takes, as the tool's refusal of an argument that action does not take lists it ("start takes session,
    // played_on, …" or "list takes no other arguments."). Every argument a test can give simply (a scalar or a list of
    // strings) is given, so the action refuses some; the refusal comes before the campaign is resolved, so the plain
    // fixture (no campaigns.db, none created) serves.
    private async Task<IReadOnlyList<string>> ActionTakesAsync(string name, string action)
    {
        var arguments = new JsonObject { ["action"] = action };
        foreach (var property in (await GetToolAsync(name)).JsonSchema.GetProperty("properties").EnumerateObject().Where(p => p.Name != "action"))
        {
            if (SimpleValue(property.Value) is { } value)
            {
                arguments[property.Name] = value;
            }
        }

        var refusal = _server.ErrorText(await _server.CallToolJsonAsync(name, arguments.ToJsonString()));
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "campaigns.db")), $"{name} {action} reached campaigns.db before refusing: {refusal}");
        if (refusal.Contains($" {action} takes no other arguments.", StringComparison.Ordinal))
        {
            return [];
        }

        var takes = new Regex($@"(?<![a-z_]){Regex.Escape(action)} takes (?<takes>[a-z_]+(?:, [a-z_]+)*)\.").Match(refusal);
        Assert.True(takes.Success, $"{name} {action}: the refusal does not say what {action} takes: {refusal}");
        return takes.Groups["takes"].Value.Split(", ");
    }

    // A value of the property's type that the argument guard accepts, or null for an object (or untyped) property.
    private static JsonNode? SimpleValue(JsonElement property)
    {
        if (!property.TryGetProperty("type", out _))
        {
            return null;
        }

        var types = Types(property);
        if (types.Contains("string"))
        {
            return "x";
        }

        if (types.Contains("integer") || types.Contains("number"))
        {
            return 1;
        }

        if (types.Contains("boolean"))
        {
            return true;
        }

        return types.Contains("array") && property.TryGetProperty("items", out var items) && items.TryGetProperty("type", out _) && Types(items).Contains("string")
            ? new JsonArray("x")
            : null;
    }

    // Named as an argument: glossed ("since (a UTC date …)", "perspective: …") or in an argument list ("…: campaign,
    // perspective.", "(required: batch_id)", "optional dm_name, slug", "reason and dry_run", "takes campaign").
    private static bool NamedAsArgument(string text, string argument) =>
        Regex.IsMatch(text,
            $@"(?<![A-Za-z0-9_]){Regex.Escape(argument)}(?= \(|:)|(?:^|: |, |; |\. |\(required: | and | or |and/or |any of |optional |takes? ){Regex.Escape(argument)}(?![A-Za-z0-9_])");

    // A line or clause of the argument's own: "- detail: …", "- refs (required): …", "…; cursor: …".
    private static Regex ArgumentLine(string argument) => new($@"(?:^- |; ){Regex.Escape(argument)}(?::| \()");

    private static Regex WholeWord(string word) => new($@"(?<![A-Za-z0-9_]){Regex.Escape(word)}(?![A-Za-z0-9_])");

    // The description without its example line and with every quoted value emptied: what is left documents the arguments.
    private static string Prose(string description) =>
        Unquoted(string.Join('\n', description.Split('\n').Where(l => !l.StartsWith("Example: ", StringComparison.Ordinal))));

    private static string Unquoted(string text) => QuotedValueRegex().Replace(text, "\"\"");

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
    public async Task CampaignWrite_Ops_IsPublishedTypedAndTheSchemaStaysUnder24K()
    {
        // ops is published typed (CampaignOpSpec[], every op's fields in one flat union), so the argument guard refuses a
        // misspelt field inside any op, knower, gate or route before the tool runs; untyped, System.Text.Json would drop it
        // and the model would believe it had written it. The contract keeps it typed while the whole input schema stays under
        // 24,000 characters (13,581 when written); past that, ops is published untyped with [CheckedAs(typeof(CampaignOpSpec[]))]
        // and the model learns the fields only from the description: a change this makes deliberate.
        var schema = (await GetToolAsync("campaign_write")).JsonSchema;
        var ops = schema.GetProperty("properties").GetProperty("ops");
        var fields = ops.GetProperty("items").GetProperty("properties");

        Assert.Contains("array", Types(ops));
        Assert.Contains("object", Types(ops.GetProperty("items")));
        foreach (var field in new[] { "op", "ref", "kind", "name", "statement", "known_by", "gate", "data", "clock" })
        {
            Assert.True(fields.TryGetProperty(field, out _), $"campaign_write's ops items have no {field} field.");
        }

        Assert.Equal(["ops"], schema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!));
        Assert.True(
            schema.GetRawText().Length < CampaignWriteSchemaLimit,
            $"campaign_write's input schema is {schema.GetRawText().Length} characters; past {CampaignWriteSchemaLimit}, publish ops untyped.");
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
        // phantom "progress" argument now and then; this makes the race happen on purpose. Prompts are built the same way,
        // so their argument lists are compared too.
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
    public void ServerInfo_Version_MatchesThePackageManifest()
    {
        // serverInfo.version is DndMcp.csproj's <Version>; the MCP registry reads DndMcp/.mcp/server.json, which names the
        // version twice. Bumped apart, the registry advertises one version while `claude mcp list` shows another.
        using var manifest = JsonDocument.Parse(File.ReadAllText(ServerManifestPath()));
        var version = manifest.RootElement.GetProperty("version").GetString();

        Assert.Equal(version, _server.Client.ServerInfo.Version);
        Assert.All(manifest.RootElement.GetProperty("packages").EnumerateArray(), package => Assert.Equal(version, package.GetProperty("version").GetString()));
    }

    [Fact]
    public void ServerInfo_Version_IsThisPhasesRelease()
    {
        // The test above holds the three versions together; this one says which version they are (ExpectedVersion).
        Assert.Equal(ExpectedVersion, _server.Client.ServerInfo.Version);
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
            .Concat((await server.Client.ListPromptsAsync())
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => $"prompt {p.Name}: {string.Join(", ", (p.ProtocolPrompt.Arguments ?? []).Select(a => a.Name))}"))
            .ToList();

    // DndMcp/.mcp/server.json, found by walking up from the test output to the solution (the layout-proof way BuiltHost
    // finds the host).
    private static string ServerManifestPath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DndMcp.sln")))
            {
                return Path.Combine(directory.FullName, "DndMcp", ".mcp", "server.json");
            }
        }

        throw new FileNotFoundException($"No DndMcp.sln above {AppContext.BaseDirectory}, so DndMcp/.mcp/server.json cannot be found.");
    }

    // The one line of the instructions that starts with prefix.
    private string InstructionLine(string prefix) =>
        Assert.Single(_server.Client.ServerInstructions!.Split('\n'), l => l.StartsWith(prefix, StringComparison.Ordinal));

    private async Task<McpClientTool> GetToolAsync(string name)
    {
        var tools = await _server.Client.ListToolsAsync();
        return Assert.Single(tools, t => t.Name == name);
    }

    private async Task<McpClientPrompt> GetPromptAsync(string name)
    {
        var prompts = await _server.Client.ListPromptsAsync();
        return Assert.Single(prompts, p => p.Name == name);
    }
}
