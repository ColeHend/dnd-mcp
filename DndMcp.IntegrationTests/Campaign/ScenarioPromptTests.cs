using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the <c>knowledge_check</c> prompt, fetched as Claude Code fetches it for
/// <c>/mcp__dnd__knowledge_check belmakor</c>, is a plan the tools carry out as written on the Belmakor world: it names
/// <c>campaign_knowledge check</c> (by its bare name: a prompt drives the tools of the server it comes from) with the
/// character's own perspective, the campaign and a diegetic draft; its one call, filled in with a draft, runs as it stands
/// and catches the Axiom Cage lyric while passing "Old king, come down"; and it carries none of the campaign's content
/// (the check reads that through the perspective filter).
///
/// <para>
/// Why it fails silently: a prompt is instructions the model follows without questioning them. One that named the
/// wrong perspective (the author's), dropped the campaign (the check would run in whatever campaign is active) or left
/// out diegetic would make every song check "pass" while the check itself stayed correct, and every tool test would
/// stay green.
/// </para>
/// </summary>
public sealed class ScenarioPromptTests : IClassFixture<ScenarioBelmakorWorld>
{
    private static readonly Regex CheckCall = new("""(?<![A-Za-z_])campaign_knowledge (\{"action": "check", .*?"diegetic": true\})""", RegexOptions.CultureInvariant);

    private readonly ScenarioBelmakorWorld _w;

    public ScenarioPromptTests(ScenarioBelmakorWorld world)
    {
        _w = world;
    }

    /// <summary>The plan names the check, the campaign and the character's perspective, in the order the model runs it; it quotes nothing of the campaign.</summary>
    [Theory]
    [InlineData("belmakor", "character:belmakor")]
    [InlineData("character:belmakor", "character:belmakor")]
    [InlineData("serif", "character:serif")]
    public async Task GetPrompt_KnowledgeCheck_NamesTheCheckWithTheCharactersPerspectiveAndTheCampaign(string character, string perspective)
    {
        var text = await Prompt(character);

        Assert.StartsWith($"Check the most recent draft in this conversation against what {perspective} knows in campaign `belmakor`. " +
                          "If there is no draft yet, ask me for it and stop.\n", text, StringComparison.Ordinal);
        Assert.Contains($"\n1. Call campaign_knowledge {{\"action\": \"check\", \"campaign\": \"belmakor\", \"perspective\": \"{perspective}\", " +
                        "\"text\": <the draft, verbatim>, \"diegetic\": true}.", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Report the hard flags first", StringComparison.Ordinal) < text.IndexOf("Then the things to review", StringComparison.Ordinal),
            text);
        Assert.EndsWith("Never put its words into the draft.", text, StringComparison.Ordinal);
        ScenarioLeak.AssertClean(text, [.. ScenarioLeak.AlwaysForbidden, .. ScenarioLeak.AmbitionWords, "thing he wants", "Silverwind", "Tristan"],
            $"the knowledge_check prompt for {character}");

        // "an old king" is there as the prompt's own example of imprecision; the campaign's name for him is not.
        Assert.DoesNotContain("Old King", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool plan, sanity-checked: the prompt's call with a draft put where it says (the only blank) is a valid call as
    /// it stands, and it does the prompt's job: the lyric naming the Axiom Cage is flagged in Belmakor's name and the one
    /// the golden passes (row 36) passes.
    /// </summary>
    [Theory]
    [InlineData("Old king, come down", "# Knowledge check: pass (belmakor)\n\nSpeaker: character:belmakor · diegetic, audience: party.\n")]
    [InlineData("We'll haul the Axiom Cage back up to the old king",
        "# Knowledge check: 2 hard flags (belmakor)\n\nSpeaker: character:belmakor · diegetic, audience: party.\n")]
    public async Task GetPrompt_KnowledgeChecksCallFilledWithADraft_RunsAsWrittenAndJudgesTheDraft(string draft, string start)
    {
        var call = CheckCall.Match(await Prompt("belmakor"));
        Assert.True(call.Success, "the prompt names no campaign_knowledge check call");

        var result = await _w.Call("campaign_knowledge", call.Groups[1].Value.Replace("<the draft, verbatim>", JsonSerializer.Serialize(draft), StringComparison.Ordinal));

        Assert.StartsWith(start, result, StringComparison.Ordinal);
    }

    private async Task<string> Prompt(string character)
    {
        var result = await _w.Server.Client.GetPromptAsync("knowledge_check", new Dictionary<string, object?> { ["character"] = character, ["campaign"] = "belmakor" });
        var message = Assert.Single(result.Messages);
        Assert.Equal(Role.User, message.Role);
        return Assert.IsType<TextContentBlock>(message.Content).Text;
    }
}
