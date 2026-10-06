using System.Text.RegularExpressions;
using DndMcp.Domain.Dice;
using DndMcp.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant (fix F1, "one order everywhere"): every call a campaign tool, resource, prompt or refusal prints for the model
/// to send names the campaign LAST and once — <c>{"action": …, …, "campaign": "&lt;slug&gt;"}</c> — whoever wrote it (a
/// formatter, a prompt, a reader's refusal, a tool's example). Swept over the Belmakor world as the author and as the
/// party: the reads, their "read more" calls, the history hints, the session lists and their not-found refusals, a write's
/// dry run, the six prompts and the campaign resources.
///
/// <para>
/// Why it matters: a printed call is sent as it stands, often while another campaign is current, so it must name its
/// campaign; and with Phase 7 printing hundreds of calls, one order is one thing for the model to learn and for every
/// test to check (contract §15 H1 ruling 8). Phase 6 printed the campaign first in some calls and second in others, and
/// Phase 7's combat calls last; a model copying one shape into another call put the campaign anywhere.
/// </para>
/// </summary>
public sealed partial class CampaignPrintedCallTests : IClassFixture<ScenarioBelmakorWorld>
{
    private readonly ScenarioBelmakorWorld _w;

    public CampaignPrintedCallTests(ScenarioBelmakorWorld world)
    {
        _w = world;
    }

    /// <summary>
    /// Every call <paramref name="text"/> prints — a campaign tool's name and the JSON object after it, or an example a
    /// refusal or description gives ("Example: {…}") — that names a campaign at all, each from its opening brace to the one
    /// that closes it (braces in strings skipped).
    /// </summary>
    internal static IReadOnlyList<string> CallsNamingACampaign(string text) =>
        PrintedCalls(text).Where(call => call.Contains("\"campaign\":", StringComparison.Ordinal)).ToList();

    /// <summary>Every call <paramref name="text"/> prints, named or not, each whole (<see cref="CallsNamingACampaign"/>).</summary>
    internal static IReadOnlyList<string> PrintedCalls(string text) =>
        CallStart().Matches(text).Select(m => Whole(text, m.Index + m.Length - 1) is { } close ? text[m.Index..(close + 1)] : null)
            .OfType<string>()
            .ToList();

    /// <summary>
    /// Asserts every call <paramref name="text"/> prints that names a campaign names it last, and once; with
    /// <paramref name="campaign"/>, that it names that campaign or a placeholder ("&lt;slug&gt;"), never another one's (fix F1,
    /// X2-N: a One Piece refusal printed a call for "belmakor"). Returns how many calls it checked.
    /// </summary>
    internal static int AssertCampaignLast(string text, string label, string? campaign = null)
    {
        var calls = CallsNamingACampaign(text);
        foreach (var call in calls)
        {
            var last = CampaignLast().Match(call);
            Assert.True(last.Success, $"{label}: the campaign is not last in {call}");
            Assert.True(Regex.Matches(call, "\"campaign\":").Count == 1, $"{label}: the campaign is named twice in {call}");
            Assert.True(campaign is null || last.Groups[1].Value == $"\"{campaign}\"" || last.Groups[1].Value.StartsWith("\"<", StringComparison.Ordinal),
                $"{label}: another campaign is named in {call}");
        }

        return calls.Count;
    }

    // The index of the brace that closes the one at open (strings and their escapes skipped), or null.
    private static int? Whole(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }

        return null;
    }

    [Theory]
    [InlineData("campaign", """{"action": "list"}""")]
    [InlineData("campaign", """{"action": "get", "campaign": "belmakor"}""")]
    [InlineData("campaign", """{"action": "summary", "campaign": "belmakor"}""")]
    [InlineData("campaign", """{"action": "summary", "campaign": "belmakor", "perspective": "party"}""")]
    [InlineData("campaign_search", """{"campaign": "belmakor", "query": "king"}""")]
    [InlineData("campaign_search", """{"campaign": "belmakor", "query": "king", "perspective": "party"}""")]
    [InlineData("campaign_search", """{"campaign": "belmakor", "kinds": ["fact"]}""")]
    [InlineData("campaign_get", """{"campaign": "belmakor", "refs": ["character:belmakor", "character:old-king", "thread:old-kings-errand"]}""")]
    [InlineData("campaign_get", """{"campaign": "belmakor", "refs": ["character:belmakor", "character:old-king"], "perspective": "party"}""")]
    [InlineData("campaign_get", """{"campaign": "belmakor", "refs": ["character:belmakor"], "include": ["history", "sessions", "knowledge"], "detail": "full"}""")]
    [InlineData("campaign_history", """{"action": "since", "campaign": "belmakor"}""")]
    [InlineData("campaign_history", """{"action": "since", "campaign": "belmakor", "session": 2}""")]
    [InlineData("campaign_history", """{"action": "entity", "ref": "character:belmakor", "campaign": "belmakor"}""")]
    [InlineData("campaign_session", """{"action": "list", "campaign": "belmakor"}""")]
    [InlineData("campaign_session", """{"action": "list", "campaign": "belmakor", "perspective": "party"}""")]
    [InlineData("campaign_session", """{"action": "get", "campaign": "belmakor", "session": 3}""")]
    [InlineData("campaign_session", """{"action": "get", "campaign": "belmakor", "session": 3, "perspective": "party"}""")]
    [InlineData("campaign_session", """{"action": "recap", "campaign": "belmakor", "session": 3, "perspective": "party"}""")]
    [InlineData("campaign_knowledge", """{"action": "ledger", "campaign": "belmakor", "about": ["character:old-king"]}""")]
    [InlineData("campaign_knowledge", """{"action": "check", "campaign": "belmakor", "perspective": "character:belmakor", "text": "Keras sent us for the Axiom Cage."}""")]
    [InlineData("campaign_write", """{"campaign": "belmakor", "dry_run": true, "ops": [{"op": "upsert", "kind": "location", "name": "The probe dock"}]}""")]
    [InlineData("combat", """{"action": "state", "campaign": "belmakor"}""")]
    [InlineData("campaign_character", """{"action": "get", "campaign": "belmakor", "character": "character:vars"}""")]
    [InlineData("encounter_difficulty", """{"party": "campaign", "campaign": "belmakor", "monsters": [{"ref": "2014/monster/mummy"}]}""")]
    public async Task Tool_EveryCallItPrints_NamesTheCampaignLast(string tool, string argumentsJson)
    {
        var result = await _w.Server.CallToolJsonAsync(tool, argumentsJson);

        AssertCampaignLast(_w.Server.SingleText(result), $"{tool} {argumentsJson}", "belmakor");
    }

    [Theory]
    [InlineData("campaign_session", """{"action": "get", "campaign": "belmakor", "session": 99}""")]
    [InlineData("campaign_session", """{"action": "get", "campaign": "belmakor", "session": 99, "perspective": "party"}""")]
    [InlineData("campaign_session", """{"action": "get", "campaign": "belmakor", "session": 99, "perspective": "character:serif"}""")]
    [InlineData("campaign_history", """{"action": "undo", "campaign": "belmakor", "batch_id": "00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("campaign_character", """{"action": "heal", "campaign": "belmakor", "character": "character:vars", "amount": 3}""")]
    [InlineData("encounter_difficulty", """{"party": "campaign", "campaign": "belmakor", "monsters": [{"ref": "2014/monster/mummy"}]}""")]
    [InlineData("campaign_character", """{"action": "attack", "campaign": "belmakor"}""")]
    [InlineData("campaign_knowledge", """{"action": "attack", "campaign": "belmakor"}""")]
    [InlineData("campaign_session", """{"action": "attack", "campaign": "belmakor"}""")]
    [InlineData("campaign", """{"action": "attack"}""")]
    [InlineData("combat", """{"action": "attack"}""")]
    [InlineData("combat", """{"action": "damage", "campaign": "belmakor", "targets": ["torch"], "amount": 1, "perspective": "party"}""")]
    public async Task Refusal_EveryCallOrExampleItPrints_NamesTheCampaignLast(string tool, string argumentsJson)
    {
        // The session not-found refusal printed {"action": "list", "campaign": …, "perspective": …}; the unknown-action
        // refusals print each tool's example, which named the campaign second.
        var result = await _w.Server.CallToolJsonAsync(tool, argumentsJson);

        AssertCampaignLast(_w.Server.SingleText(result), $"{tool} {argumentsJson}", "belmakor");
    }

    [Theory]
    [InlineData("session_recap", "3")]
    [InlineData("session_recap", null)]
    [InlineData("session_prep", "4")]
    [InlineData("knowledge_check", null)]
    [InlineData("continuity_check", null)]
    [InlineData("in_character", null)]
    [InlineData("homebrew_review", null)]
    public async Task Prompt_EveryCallItPlans_NamesTheCampaignLast(string name, string? session)
    {
        var arguments = new Dictionary<string, object?> { ["campaign"] = "belmakor" };
        if (name is "knowledge_check" or "in_character")
        {
            arguments["character"] = "belmakor";
        }

        if (session is not null)
        {
            arguments["session"] = session;
        }

        var result = await _w.Server.Client.GetPromptAsync(name, arguments);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Messages).Content).Text;

        Assert.True(AssertCampaignLast(text, name, "belmakor") > 0, text);
    }

    [Theory]
    [InlineData("campaign://list")]
    [InlineData("campaign://belmakor/summary")]
    [InlineData("campaign://belmakor/threads")]
    [InlineData("campaign://belmakor/party")]
    [InlineData("campaign://belmakor/session/3")]
    [InlineData("campaign://belmakor/entity/character:old-king")]
    [InlineData("campaign://belmakor/knowledge/party")]
    [InlineData("campaign://belmakor/combat/current")]
    public async Task Resource_EveryCallItPrints_NamesTheCampaignLast(string uri)
    {
        AssertCampaignLast(await ScenarioCalls.Read(_w.Server, uri), uri, "belmakor");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:(?:campaign(?:_[a-z]+)?|combat|balance_simulate|encounter_difficulty|dice_roll) |Example: |\()\{")]
    private static partial Regex CallStart();

    [GeneratedRegex("\"campaign\": (\"[^\"]*\"|<[^>]*>)\\}$")]
    private static partial Regex CampaignLast();
}

/// <summary>
/// Invariant (fix F2, review UR02): a <c>campaign_character</c> refusal that comes from the live fight (D5 hands a routed
/// action to the tracker, whose refusals print <c>combat</c> calls with no campaign: the Domain never knows the slug) prints
/// every call naming the character's campaign LAST, exactly as <c>combat</c>'s own refusals do. The tool runs its refusals
/// through the same completion pass (<c>CombatMarkdown.CampaignLastIn</c>) once the campaign is resolved.
///
/// <para>
/// Why it fails silently: a printed call is sent as it stands, often while another campaign is current. With no campaign
/// in it, "end it with combat {…, "drop": true}" went to the current campaign ("No combat is running in other") or, worse,
/// to a fight there whose combatant has the same name. The sweeps of <see cref="CampaignPrintedCallTests"/> check only the
/// calls that name a campaign, so a call that names none passed them.
/// </para>
/// </summary>
public sealed class CampaignPrintedCallRoutedTests : IAsyncLifetime
{
    private readonly McpServerHarness _server = McpServerHarness.WithExtraTools(builder =>
        builder.Services.AddSingleton<IDiceRoller>(new ScriptedDiceRoller()));

    // Björn in the reef fight of "deep", concentrating on Bless; then "other" is created, which makes it the current campaign.
    public async Task InitializeAsync()
    {
        await _server.InitializeAsync();
        await CharacterToolSetup.CreateDeepAsync(_server);
        await Combat("""{"action": "start", "name": "Reef", "campaign": "deep", "combatants": [{"srd": "2024/monster/ogre"}]}""");
        await Combat("""{"action": "initiative", "campaign": "deep", "rolls": [{"combatant": "bjorn-mountainfell", "total": 15}, {"combatant": "kaz", "total": 12}, {"combatant": "ogre", "total": 3}]}""");
        await Combat("""{"action": "concentration", "campaign": "deep", "targets": ["bjorn-mountainfell"], "spell": "Bless", "duration": "1 minute"}""");
        await ScenarioCalls.Call(_server, "campaign", """{"action": "create", "name": "Other", "role": "dm", "ruleset": "2024", "slug": "other"}""");
    }

    public Task DisposeAsync() => _server.DisposeAsync();

    private Task<string> Combat(string argumentsJson) => ScenarioCalls.Call(_server, "combat", argumentsJson);

    [Fact]
    public async Task RoutedRefusal_AnotherCampaignCurrent_NamesTheFightsCampaignLast_AndItsCallWorksVerbatim()
    {
        var refusal = await ScenarioCalls.Fail(_server, "campaign_character",
            """{"action": "condition", "character": "character:bjorn-mountainfell", "add": ["concentration"], "campaign": "deep"}""");

        Assert.Equal(
            "An error occurred invoking 'campaign_character': concentration is not a condition: start one with combat {\"action\": \"concentration\", " +
            "\"targets\": [\"bjorn-mountainfell\"], \"spell\": …, \"campaign\": \"deep\"} and end it with combat {\"action\": " +
            "\"concentration\", \"targets\": [\"bjorn-mountainfell\"], \"drop\": true, \"campaign\": \"deep\"}.",
            refusal);
        var drop = Regex.Match(refusal, @"end it with combat (\{.*\})\.$").Groups[1].Value;
        var dropped = await Combat(drop);
        Assert.StartsWith("# Reef — round 1 · Björn Mountainfell's turn\n\ncombat concentration · deep · ", dropped, StringComparison.Ordinal);
        Assert.Contains("\n- Björn Mountainfell's concentration on Bless ended: it dropped it\n", dropped, StringComparison.Ordinal);
    }
}
