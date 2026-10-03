using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the six campaign prompts are listed with their names, titles, descriptions and single-token arguments (only
/// the character of <c>knowledge_check</c> and <c>in_character</c> required); each returns one user message of
/// instructions that drive the tools in order (dry run first, apply after approval) and carries no campaign content; and a
/// prompt for a campaign or character that does not exist fails with the message written for the user, through the
/// get-prompt filter.
///
/// <para>
/// Why it fails silently: Claude Code maps typed tokens to arguments by position and drops extras, so a reordered or
/// added argument changes what <c>/mcp__dnd__knowledge_check belmakor</c> means without any error; a prompt that pasted
/// the author's view of the campaign into the conversation would put "the Axiom Cage" in front of the model writing
/// Belmakor's lyric, bypassing every perspective filter; and a <c>DndInputException</c> that escaped the filter would reach
/// the user as the SDK's bare "An error occurred.".
/// </para>
/// </summary>
public sealed partial class CampaignPromptTests : IClassFixture<McpServerHarness>
{
    // Every prompt's arguments, in the order Claude Code maps typed tokens to them, with the required flag.
    private static readonly Dictionary<string, (string Name, bool Required)[]> ExpectedArguments = new(StringComparer.Ordinal)
    {
        ["continuity_check"] = [("campaign", false)],
        ["homebrew_review"] = [("campaign", false)],
        ["in_character"] = [("character", true), ("campaign", false)],
        ["knowledge_check"] = [("character", true), ("campaign", false)],
        ["session_prep"] = [("campaign", false), ("session", false)],
        ["session_recap"] = [("campaign", false), ("session", false)],
    };

    private readonly McpServerHarness _server;

    // A prompt command a prompt tells the user to type: the prompt's bare name, then its first argument as a placeholder
    // ("homebrew_review <campaign>"). Not preceded by a quote, so a JSON field's placeholder ("text": <the draft>) is not one.
    [GeneratedRegex(@"(?<![A-Za-z_""])(?<prompt>[a-z]+(?:_[a-z]+)+) <(?<argument>[a-z_]+)>")]
    private static partial Regex PromptCommandRegex();

    public CampaignPromptTests(McpServerHarness server)
    {
        _server = server;
    }

    [Fact]
    public async Task ListPrompts_Server_ExposesExactlyTheSixCampaignPrompts()
    {
        var prompts = await _server.Client.ListPromptsAsync();

        Assert.Equal(ExpectedArguments.Keys.Order(StringComparer.Ordinal), prompts.Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("session_recap", "Recap a session")]
    [InlineData("session_prep", "Prepare a session")]
    [InlineData("knowledge_check", "Does this character know that?")]
    [InlineData("continuity_check", "Continuity check")]
    [InlineData("in_character", "Write in character")]
    [InlineData("homebrew_review", "Review homebrew balance")]
    public async Task ListPrompts_EachPrompt_HasItsTitleADescriptionWithUsageAndSingleTokenArgumentsInOrder(string name, string title)
    {
        var prompt = (await _server.Client.ListPromptsAsync()).Single(p => p.Name == name).ProtocolPrompt;

        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", prompt.Name);
        Assert.Equal(title, prompt.Title);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Description));
        Assert.True(prompt.Description!.Length <= CampaignWriteSetup.DescriptionLimit, $"{prompt.Description.Length} characters");
        var arguments = prompt.Arguments ?? [];
        Assert.Equal(ExpectedArguments[name], arguments.Select(a => (a.Name, a.Required ?? false)).ToArray());
        // The usage line names the prompt alone: the command's server prefix is the client's (/mcp__dnd__ for a server
        // registered as "dnd", /mcp__dnd-dev__ for a dev build), which the server cannot know.
        var usage = "Usage: " + name + string.Concat(arguments.Select(a => a.Required == true ? $" <{a.Name}>" : $" [{a.Name}]")) + ".";
        Assert.EndsWith(usage, prompt.Description, StringComparison.Ordinal);
        foreach (var argument in arguments)
        {
            Assert.Matches("^[a-zA-Z0-9_.-]{1,64}$", argument.Name);
            // One token each: Claude Code splits the command line on whitespace.
            Assert.Contains("one word", argument.Description ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetPrompt_KnowledgeCheck_DrivesTheCheckWithTheCharactersPerspectiveAndReportsHardFlagsFirst()
    {
        var slug = await KnowledgeScenario.BuildAsync(_server).ContinueWith(t => t.Result.Slug);

        var text = await Text("knowledge_check", ("character", "belmakor"), ("campaign", slug));

        Assert.StartsWith($"Check the most recent draft in this conversation against what character:belmakor knows in campaign `{slug}`. " +
                          "If there is no draft yet, ask me for it and stop.", text, StringComparison.Ordinal);
        Assert.Contains($"\n1. Call campaign_knowledge {{\"action\": \"check\", \"campaign\": \"{slug}\", \"perspective\": \"character:belmakor\", " +
                        "\"text\": <the draft, verbatim>, \"diegetic\": true}", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("hard flags first", StringComparison.Ordinal) < text.IndexOf("the things to review", StringComparison.Ordinal));
        Assert.Contains("Never put its words into the draft.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPrompt_InCharacter_ReadsTheCharactersViewBeforeDraftingAndChecksBeforeShowing()
    {
        var slug = await KnowledgeScenario.BuildAsync(_server).ContinueWith(t => t.Result.Slug);

        var text = await Text("in_character", ("character", "character:belmakor"), ("campaign", slug));

        var view = text.IndexOf($"campaign://{slug}/knowledge/character:belmakor", StringComparison.Ordinal);
        var draft = text.IndexOf("2. Draft.", StringComparison.Ordinal);
        var check = text.IndexOf("\"action\": \"check\"", StringComparison.Ordinal);
        Assert.True(view > 0 && view < draft && draft < check, text);
        Assert.Contains("perspective \"character:belmakor\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPrompt_SessionRecap_DryRunsFirstAndOrdersTheWritesSoTheSessionExistsAndEndIsLast()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await Text("session_recap", ("campaign", slug), ("session", "12"));

        Assert.StartsWith($"Recap session 12 of campaign `{slug}` from my account of it in this conversation.", text, StringComparison.Ordinal);
        Assert.Contains($"campaign_session {{\"action\": \"get\", \"campaign\": \"{slug}\", \"session\": 12}}", text, StringComparison.Ordinal);
        Assert.Contains("every call as a dry run first (dry_run: true)", text, StringComparison.Ordinal);
        Assert.Contains("for real only after I approve", text, StringComparison.Ordinal);
        var recordPast = text.IndexOf($"campaign_session {{\"action\": \"record_past\", \"campaign\": \"{slug}\", \"session\": 12, " +
                                      "\"title\": ..., \"played_on\": ..., \"recap_md\": ..., \"attendance\": [...]} FIRST", StringComparison.Ordinal);
        var batch = text.IndexOf($"then campaign_write {{\"campaign\": \"{slug}\", \"session\": 12, \"ops\": [...]}}.", StringComparison.Ordinal);
        Assert.True(recordPast > 0 && recordPast < batch, text);
        Assert.Contains($"campaign_session {{\"action\": \"end\", \"campaign\": \"{slug}\", \"recap_md\": ..., \"attendance\": [...]}} LAST", text, StringComparison.Ordinal);
        Assert.Contains($"campaign_history {{\"action\": \"undo\", \"campaign\": \"{slug}\"", text, StringComparison.Ordinal);
        // A session named by the user needs no guessing about which one it is.
        Assert.DoesNotContain("With no session number from me", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPrompt_SessionPrep_SavesThePrepOnlyAfterADryRun()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await Text("session_prep", ("campaign", slug), ("session", "13"));

        Assert.StartsWith($"Prepare session 13 of campaign `{slug}` with me", text, StringComparison.Ordinal);
        foreach (var tool in new[] { "campaign {", "campaign_session {", "campaign_search {", "campaign_get ", "encounter_difficulty " })
        {
            Assert.Contains(tool, text, StringComparison.Ordinal);
        }

        Assert.EndsWith($"campaign_session {{\"action\": \"plan\", \"campaign\": \"{slug}\", \"session\": 13, \"title\": ..., " +
                        "\"prep_md\": <the run-sheet>, \"dry_run\": true}, then the same call without dry_run.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPrompt_ContinuityCheck_IsTheFiveStepPassWithGatesAndForbiddenWords()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await Text("continuity_check", ("campaign", slug));

        foreach (var step in new[] { "\n1. Name the canon objects", "\n2. Confirm each", "\n3. Check against what was played", "\n4. Check who could know this", "\n5. Flag, don't fix" })
        {
            Assert.Contains(step, text, StringComparison.Ordinal);
        }

        Assert.Contains("forbidden words of active reveal gates and reveal rules", text, StringComparison.Ordinal);
        Assert.Contains("check its gate (after, with, routes)", text, StringComparison.Ordinal);

        // Step 4 is the who-could-know pass: the check runs for the draft's speaker. Without the perspective it runs for the
        // author (its default), for whom every name is ok and no gate or reveal-rule vocabulary applies: it would report nothing.
        Assert.Contains($"\n4. Check who could know this: campaign_knowledge {{\"action\": \"check\", \"campaign\": \"{slug}\", " +
                        "\"perspective\": <the speaker, e.g. \"character:<slug>\" or \"party\">, \"text\": <the draft>, ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPrompt_HomebrewReview_ComparesAgainstTheOfficialBaselineOnTheBandScale()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await Text("homebrew_review", ("campaign", slug));

        Assert.Contains("call balance_compare with the OFFICIAL option it replaces", text, StringComparison.Ordinal);
        Assert.Contains("(Under, On budget, Creeping, Over, Breaking)", text, StringComparison.Ordinal);
        Assert.Contains($"campaign_search {{\"campaign\": \"{slug}\", \"kinds\": [\"rule\"], \"query\": \"balance\"}}", text, StringComparison.Ordinal);
        Assert.Contains("\"dry_run\": true}, then the same call without dry_run.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPrompt_HomebrewReviewWithNoCampaignAtAll_WorksWithoutOneAndCreatesNothing()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var result = await server.Client.GetPromptAsync("homebrew_review");

            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
            Assert.Contains("There is no campaign to take house standards from; use the published rules.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("campaign_write", text, StringComparison.Ordinal);
            AssertNamesItsToolsBareAsThisServers(text);
            Assert.False(File.Exists(Path.Combine(server.DataDirectory, "campaigns.db")), "A prompt must not create campaigns.db.");
        });
    }

    [Theory]
    [InlineData("session_recap")]
    [InlineData("session_prep")]
    [InlineData("knowledge_check")]
    [InlineData("continuity_check")]
    [InlineData("in_character")]
    [InlineData("homebrew_review")]
    public async Task GetPrompt_EveryPrompt_IsOneUserMessageOfInstructionsWithNoCampaignContent(string name)
    {
        // Author-only text, secrets, true names and statements live in the campaign; a prompt names it by slug and leaves
        // reading it to the tools (and their perspective filter).
        var slug = (await KnowledgeScenario.BuildAsync(_server)).Slug;
        var arguments = new Dictionary<string, object?> { ["campaign"] = slug };
        if (ExpectedArguments[name].Any(a => a.Name == "character"))
        {
            arguments["character"] = "belmakor";
        }

        var result = await _server.Client.GetPromptAsync(name, arguments);

        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);
        var text = Assert.IsType<TextContentBlock>(message.Content).Text;
        Assert.Matches("(?<![A-Za-z_])[a-z]+(?:_[a-z]+)* \\{\"", text); // instructions that drive tools: a call template
        foreach (var content in new[] { "Keras", "Axiom", "Old King", "old king wants", "army marches", "thing he wants", "Silverwind", "never learns" })
        {
            Assert.DoesNotContain(content, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetPrompt_SessionRecapWithNoSession_TellsTheModelToCorrectASessionAlreadyRecorded()
    {
        // With nothing live, record_past without a number records the NEXT session to play; if the night being recapped
        // was already ended, that would file it twice. Only get (step 1) can tell, so the prompt says what to do with it.
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await Text("session_recap", ("campaign", slug));

        Assert.StartsWith($"Recap the session just played (the live one, if one is live) of campaign `{slug}`", text, StringComparison.Ordinal);
        Assert.Contains($"campaign_session {{\"action\": \"record_past\", \"campaign\": \"{slug}\", \"title\": ...", text, StringComparison.Ordinal);
        Assert.Contains($"then campaign_write {{\"campaign\": \"{slug}\", \"session\": <the number record_past reported>, \"ops\": [...]}}.", text, StringComparison.Ordinal);
        Assert.Contains("\n   - With no session number from me: if step 1 shows the session my account describes already recorded (played), " +
                        "give record_past its \"session\" number, which corrects it; leave session out only for a session not recorded yet " +
                        "(record_past then takes the next one to play).\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("session_recap")]
    [InlineData("session_prep")]
    [InlineData("knowledge_check")]
    [InlineData("continuity_check")]
    [InlineData("in_character")]
    [InlineData("homebrew_review")]
    public async Task GetPrompt_EveryPrompt_NamesTheCampaignOnEveryCampaignToolCall(string name)
    {
        // A prompt does not make its campaign the current one, so a call template without "campaign" would run in the
        // active campaign: "/mcp__dnd__session_recap one-piece" while belmakor is active would write the recap into
        // belmakor. Every campaign tool call template carries it, and the prompt says so for the calls in prose.
        var slug = (await KnowledgeScenario.BuildAsync(_server)).Slug;
        var arguments = new List<(string Name, string Value)> { ("campaign", slug) };
        if (ExpectedArguments[name].Any(a => a.Name == "character"))
        {
            arguments.Add(("character", "belmakor"));
        }

        var text = await Text(name, [.. arguments]);

        Assert.Contains($"Pass \"campaign\": \"{slug}\" on every call to its campaign tools, as the calls below do: without it a call " +
                        "goes to the active campaign, which may be another.", text, StringComparison.Ordinal);
        var calls = Regex.Matches(text, @"(?<![A-Za-z_])campaign(?:_[a-z]+)? \{[^}]*").Select(m => m.Value).ToList();
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Contains($"\"campaign\": \"{slug}\"", call, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetPrompt_InCharacter_ChecksTheDraftInTheNamedCampaignWithTheCharactersPerspective()
    {
        var slug = (await KnowledgeScenario.BuildAsync(_server)).Slug;

        var text = await Text("in_character", ("character", "belmakor"), ("campaign", slug));

        Assert.Contains($"3. Before showing me, run campaign_knowledge {{\"action\": \"check\", \"campaign\": \"{slug}\", " +
                        "\"perspective\": \"character:belmakor\", \"text\": <the draft>, \"diegetic\": true for a song or anything performed}, " +
                        "fix every hard flag, and tell me what it listed to review.\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "1")]
    [InlineData("record_past 1; plan", "2")]
    [InlineData("record_past 1; record_past 2", "3")]
    [InlineData("record_past 3; plan 2", "4")]
    [InlineData("record_past 1; plan 4; plan 3", "3")]
    [InlineData("plan 0", "0")]
    [InlineData("record_past 1; prep 2", "2")]
    public async Task GetPrompt_SessionPrepWithNoSession_PlansTheSessionStartWouldStart(string setup, string expected)
    {
        // plan's own default is always a new session (max + 1), so a prompt that left session out would create session 3
        // while the next session, 2, sits prepped. The prompt resolves the next session to play by start's rule; the dry
        // run of start pins that the two agree. Session zero counts when nothing is played yet (no "last played" means
        // every session is after it), and a session already prepped (plan with prep_md) is still the next to play, so
        // running the prompt again revises it rather than planning the one after.
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            foreach (var step in setup.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = step.Split(' ');
                var number = parts.Length > 1 ? $", \"session\": {parts[1]}" : string.Empty;
                var action = parts[0] == "prep" ? "plan\", \"prep_md\": \"- Open on the docks" : parts[0];
                await CampaignWriteSetup.CallAsync(server, "campaign_session", $$"""{"campaign": "{{slug}}", "action": "{{action}}"{{number}}}""");
            }

            var result = await server.Client.GetPromptAsync("session_prep", new Dictionary<string, object?> { ["campaign"] = slug });
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
            var start = await CampaignWriteSetup.CallAsync(server, "campaign_session", $$"""{"campaign": "{{slug}}", "action": "start", "dry_run": true}""");

            Assert.StartsWith($"Prepare the next session to play, session {expected}, of campaign `{slug}`", text, StringComparison.Ordinal);
            Assert.EndsWith($"campaign_session {{\"action\": \"plan\", \"campaign\": \"{slug}\", \"session\": {expected}, \"title\": ..., " +
                            "\"prep_md\": <the run-sheet>, \"dry_run\": true}, then the same call without dry_run.", text, StringComparison.Ordinal);
            Assert.StartsWith($"# Dry run: Session {expected} started ({slug})", start, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GetPrompt_SessionRecapWhenACampaignIsNamedLikeANumber_KeepsTheTokenAsTheCampaign()
    {
        // "/mcp__dnd__session_recap 2024" is the campaign 2024 when one has that slug; only a number no campaign has is
        // read as the session.
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            CampaignWriteSetup.CreateCampaign(server, slug: "2024");
            CampaignWriteSetup.CreateCampaign(server, slug: "other");

            var result = await server.Client.GetPromptAsync("session_recap", new Dictionary<string, object?> { ["campaign"] = "2024" });

            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
            Assert.StartsWith("Recap the session just played (the live one, if one is live) of campaign `2024`", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task GetPrompt_HomebrewReviewWithCampaignsButNoneChosen_SaysToNameOne()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            CampaignWriteSetup.CreateCampaign(server, slug: "belmakor");
            CampaignWriteSetup.CreateCampaign(server, slug: "one-piece");

            var result = await server.Client.GetPromptAsync("homebrew_review");

            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
            Assert.Contains("\n1. No campaign is chosen, so no house standards apply; use the published rules. To apply a campaign's, run " +
                            "this prompt again with its slug (homebrew_review <campaign>; campaign {\"action\": \"list\"} lists them).\n", text, StringComparison.Ordinal);
            Assert.EndsWith("5. Keep the verdict in the conversation; with no campaign chosen there is none to record it in.", text, StringComparison.Ordinal);
            AssertNamesItsToolsBareAsThisServers(text);

            // The command it tells the user to run is a real prompt, its argument that prompt's first (the token typed first).
            var commands = PromptCommandRegex().Matches(text);
            Assert.NotEmpty(commands);
            Assert.All(commands, m =>
            {
                Assert.Contains(m.Groups["prompt"].Value, ExpectedArguments.Keys);
                Assert.Equal(ExpectedArguments[m.Groups["prompt"].Value][0].Name, m.Groups["argument"].Value);
            });
        });
    }

    [Theory]
    [InlineData("12", "Recap session 12 of campaign")]
    [InlineData("session:12", "Recap session 12 of campaign")]
    public async Task GetPrompt_SessionRecapWithASessionNumberWhereTheCampaignGoes_ReadsItAsTheSession(string token, string start)
    {
        // "/mcp__dnd__session_recap 12" with the campaign chosen: the one token lands in campaign, and no campaign is "12".
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var result = await server.Client.GetPromptAsync("session_recap", new Dictionary<string, object?> { [token.StartsWith("session", StringComparison.Ordinal) ? "session" : "campaign"] = token });

            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
            Assert.StartsWith($"{start} `{slug}`", text, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("knowledge_check", "character", "nobody",
        "perspective \"character:nobody\": no character nobody in this campaign. Characters are named character:<slug>; campaign_search with kinds [\"character\"] lists them.")]
    [InlineData("session_recap", "session", "twelve", "session \"twelve\" is not a session number; give one, e.g. 12.")]
    [InlineData("session_prep", "session", "999999", "session 999999 is out of range: session numbers are 0 to 100,000.")]
    [InlineData("session_prep", "session", "100001", "session 100001 is out of range: session numbers are 0 to 100,000.")]
    [InlineData("session_recap", "session", "-3", "session \"-3\" is not a session number; give one, e.g. 12.")]
    public async Task GetPrompt_UnknownCharacterOrBadSession_FailsWithTheMessageWrittenForTheUser(string name, string argument, string value, string message)
    {
        var slug = (await KnowledgeScenario.BuildAsync(_server)).Slug;
        var arguments = new Dictionary<string, object?> { ["campaign"] = slug, [argument] = value };

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _server.Client.GetPromptAsync(name, arguments).AsTask());

        Assert.Equal(McpErrorCode.InternalError, error.ErrorCode);
        Assert.Equal("Request failed (remote): " + message, error.Message);
    }

    [Fact]
    public async Task GetPrompt_RequiredCharacterMissing_IsTheSdksGenericErrorSinceClaudeCodeRefusesItFirst()
    {
        // The SDK checks a required argument before the prompt runs and throws a plain ArgumentException, which the
        // get-prompt filter deliberately does not translate (only DndInputException is written for the user). Claude Code
        // never sends this: it refuses "/mcp__dnd__knowledge_check" with no token itself ("Missing required argument:
        // character"). Pinned so a change in either is noticed.
        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _server.Client.GetPromptAsync("knowledge_check").AsTask());

        Assert.Equal("Request failed (remote): An error occurred.", error.Message);
    }

    [Fact]
    public async Task GetPrompt_UnknownCampaign_ListsTheCampaignsThroughTheFilter()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server, role: "player", ruleset: "2014");

            var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
                server.Client.GetPromptAsync("continuity_check", new Dictionary<string, object?> { ["campaign"] = "nope" }).AsTask());

            Assert.Equal($"Request failed (remote): No campaign \"nope\". Campaigns: {slug} (player, 2014). Pass one of those slugs.", error.Message);
        });
    }

    [Fact]
    public async Task GetPrompt_NoCampaignsYet_SaysHowToCreateOneAndCreatesNothing()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var error = await Assert.ThrowsAsync<McpProtocolException>(() => server.Client.GetPromptAsync("session_prep").AsTask());

            Assert.Equal("Request failed (remote): There are no campaigns yet. Create one with campaign {\"action\": \"create\", \"name\": \"…\", " +
                         "\"role\": \"player\" or \"dm\", \"ruleset\": \"2024\"}.", error.Message);
            Assert.False(File.Exists(Path.Combine(server.DataDirectory, "campaigns.db")), "A refused prompt must not create campaigns.db.");
        });
    }

    /// <summary>
    /// FH4 (U06): a prompt names its tools bare and says once that they are the tools of the server it came from; its
    /// description's usage line names the prompt without a server. The server cannot know the name the client registered it
    /// under: under the README's development setup ("dnd" installed, "dnd-dev" local) a prompt naming mcp__dnd__ tools
    /// steered the model from the dev server's prompt to the installed server's tools, and so into the user's real
    /// campaigns.db; under any other name the tools it named did not exist at all.
    /// </summary>
    [Theory]
    [InlineData("session_recap")]
    [InlineData("session_prep")]
    [InlineData("knowledge_check")]
    [InlineData("continuity_check")]
    [InlineData("in_character")]
    [InlineData("homebrew_review")]
    public async Task GetPrompt_EveryPrompt_NamesItsToolsBareAsTheToolsOfTheServerItCameFrom(string name)
    {
        var slug = (await KnowledgeScenario.BuildAsync(_server)).Slug;
        var arguments = new List<(string Name, string Value)> { ("campaign", slug) };
        if (ExpectedArguments[name].Any(a => a.Name == "character"))
        {
            arguments.Add(("character", "belmakor"));
        }

        var text = await Text(name, [.. arguments]);
        var description = (await _server.Client.ListPromptsAsync()).Single(p => p.Name == name).Description;

        AssertNamesItsToolsBareAsThisServers(text);
        Assert.DoesNotContain("mcp__", description, StringComparison.Ordinal);
    }

    // FH4 (U06): no server-prefixed tool name, and the one line saying whose tools these are (with or without a campaign).
    private static void AssertNamesItsToolsBareAsThisServers(string text)
    {
        Assert.DoesNotContain("mcp__", text, StringComparison.Ordinal);
        Assert.Contains("\nThe tools named below are those of the MCP server this prompt came from.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills D02 (FH10, M25): session 100,000 is the documented maximum ("session numbers are 0 to 100,000"), so a prompt
    /// for it is accepted; 100,001 is refused (a row of the bad-session theory above).
    /// </summary>
    [Fact]
    public async Task GetPrompt_SessionAtTheMaximum_IsAccepted()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await Text("session_recap", ("campaign", slug), ("session", "100000"));

        Assert.StartsWith($"Recap session 100000 of campaign `{slug}`", text, StringComparison.Ordinal);
    }

    private async Task<string> Text(string name, params (string Name, string Value)[] arguments)
    {
        var result = await _server.Client.GetPromptAsync(name, arguments.ToDictionary(a => a.Name, a => (object?)a.Value));
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;
    }
}
