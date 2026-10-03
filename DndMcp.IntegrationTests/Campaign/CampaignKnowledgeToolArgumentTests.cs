using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: every argument a <c>campaign_knowledge</c> action does not take is refused, never ignored, with what the
/// action does take: for every action and every argument outside its list.
///
/// <para>
/// Why it fails silently: the tool is one flat parameter list (the SDK has no per-action schemas), so an argument meant for
/// another action binds without complaint. Ignored, it changes what the model believes happened: <c>ledger</c> with
/// "perspective" (it takes "perspectives") returns the default columns as if they were the party's own ledger; a
/// <c>ledger</c> or <c>check</c> with "session" (meaning "as of session 3") returns today's answer as session 3's; a
/// <c>record</c> with "as_of_session" files the row under the current session context. Only a few pairs were pinned, and
/// mutants dropping "perspective", "session" and "as_of_session" from the refusal survived (FH10, M07: A17, Q02, Q03).
/// </para>
/// </summary>
public sealed class CampaignKnowledgeToolArgumentTests : IClassFixture<McpServerHarness>
{
    // Every argument but action and campaign (which every action takes), with a value of the type it binds to.
    private static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["targets"] = """["f:1"]""",
        ["knowers"] = """[{"who": "party"}]""",
        ["facts"] = """["f:1"]""",
        ["secret"] = "\"secret:the-seal\"",
        ["handout"] = "\"handout:the-letter\"",
        ["to"] = """["party"]""",
        ["how"] = "\"told\"",
        ["who"] = """["party"]""",
        ["text"] = "\"Old king, come down\"",
        ["perspective"] = "\"party\"",
        ["diegetic"] = "true",
        ["audience"] = "\"party\"",
        ["about"] = """["character:iron-guts"]""",
        ["perspectives"] = """["party"]""",
        ["as_of_session"] = "2",
        ["session"] = "3",
        ["reason"] = "\"why\"",
        ["dry_run"] = "true",
    };

    // What each action takes, as the refusal lists it: the contract of each action.
    private static readonly IReadOnlyDictionary<string, string> Takes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["record"] = "targets, knowers, session, reason, dry_run, campaign",
        ["reveal"] = "facts, secret, handout, to, how, session, reason, dry_run, campaign",
        ["retract"] = "targets, who, session, reason, dry_run, campaign",
        ["check"] = "text, perspective, diegetic, audience, as_of_session, campaign",
        ["ledger"] = "about, facts, perspectives, as_of_session, campaign",
    };

    private readonly McpServerHarness _server;

    public CampaignKnowledgeToolArgumentTests(McpServerHarness server)
    {
        _server = server;
    }

    /// <summary>
    /// The refusal theory below covers every argument the tool has: the schema's properties are exactly
    /// <see cref="Values"/>' keys plus action and campaign. An argument added to the tool but not to that list would never be
    /// offered to an action that does not take it, and its refusal could be dropped unseen.
    /// </summary>
    [Fact]
    public async Task ToolSchema_EveryArgument_IsInTheRefusalTheory()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_knowledge");

        Assert.Equal(Values.Keys.Append("action").Append("campaign").Order(StringComparer.Ordinal),
            tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>Every (action, argument) pair where the action does not take the argument.</summary>
    public static TheoryData<string, string> ArgumentsActionsDoNotTake()
    {
        var rows = new TheoryData<string, string>();
        foreach (var (action, takes) in Takes)
        {
            var taken = takes.Split(", ");
            foreach (var argument in Values.Keys.Where(a => !taken.Contains(a)))
            {
                rows.Add(action, argument);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(ArgumentsActionsDoNotTake))]
    public async Task CallTool_ArgumentTheActionDoesNotTake_IsRefusedNamingWhatItTakes(string action, string argument)
    {
        var text = _server.ErrorText(await _server.CallToolJsonAsync("campaign_knowledge",
            $$"""{"action": "{{action}}", "{{argument}}": {{Values[argument]}}}"""));

        Assert.StartsWith(
            $"An error occurred invoking 'campaign_knowledge': campaign_knowledge {action} does not take \"{argument}\"; {action} takes {Takes[action]}. Example: {{",
            text, StringComparison.Ordinal);
    }
}
