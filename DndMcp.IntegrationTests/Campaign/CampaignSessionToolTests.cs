using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: <c>campaign_session</c> takes a session from plan through start, the live log and end to its recap, through
/// the real server: every write prints its batch and undo call (a dry run says it wrote nothing), <c>start</c> says the
/// live session is now every write's context, <c>end</c> returns the checklist of loose ends and the backup it took, a
/// non-author <c>get</c> or <c>list</c> starts with the perspective banner and shows nothing of the author's (no prep, no
/// live log, no history, no unplayed session), and an argument the action does not take is refused.
///
/// <para>
/// Why it fails silently: the session writer and reader can be right while the rendering undoes them. An author section
/// printed for the party (even an empty "Prep" heading) tells the table there is prep; a checklist rendered as nothing
/// when it has items loses the unknown names a recap left behind; a <c>recap_md</c> sent with <c>start</c> that was
/// dropped leaves the model believing a recap was written.
/// </para>
/// </summary>
public sealed partial class CampaignSessionToolTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public CampaignSessionToolTests(McpServerHarness server)
    {
        _server = server;
    }

    [GeneratedRegex("Batch `([0-9a-f-]{36})`")]
    private static partial Regex BatchIdRegex();

    [Fact]
    public async Task ToolSurface_CampaignSession_HasTheContractsAnnotationsTitleAndDescribedSchema()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_session");
        var annotations = tool.ProtocolTool.Annotations!;

        Assert.Equal((false, false, false, false),
            (annotations.ReadOnlyHint!.Value, annotations.DestructiveHint!.Value, annotations.IdempotentHint!.Value, annotations.OpenWorldHint!.Value));
        Assert.Equal("Campaign sessions", tool.ProtocolTool.Title);
        CampaignWriteSetup.AssertDescribedAtEveryDepth("campaign_session", tool.JsonSchema.GetProperty("properties"));
        Assert.Equal(new[] { "action" }, tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task ToolDescription_CampaignSession_NamesEveryArgumentAndEachActionsArgumentsWithinTheLimit()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_session");
        var description = tool.Description!;

        Assert.True(description.Length <= CampaignWriteSetup.DescriptionLimit, $"{description.Length} characters");
        foreach (var property in tool.JsonSchema.GetProperty("properties").EnumerateObject())
        {
            Assert.Contains(property.Name, description, StringComparison.Ordinal);
        }

        foreach (var action in new[] { "- list: status", "- get: session", "- recap: session", "- plan: session", "- start: session", "- log: notes", "- end: recap_md", "- record_past: session" })
        {
            Assert.Contains(action, description, StringComparison.Ordinal);
        }

        Assert.EndsWith("\nExample: " + CampaignSessionTools.Example, description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_TheDescriptionsExample_EndsTheLiveSessionAsWritten()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server, role: "player", slug: "belmakor", myCharacter: "Belmakor", myCharacterSlug: "belmakor");
            await CampaignWriteSetup.WriteAsync(server, slug, """[{"op": "upsert", "kind": "character", "name": "Serif", "visibility": "party"}]""");
            await Session(server, slug, """{"action": "start"}""");
            using var example = JsonDocument.Parse(CampaignSessionTools.Example);
            var arguments = example.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

            var text = server.SuccessText(await server.Client.CallToolAsync("campaign_session", arguments));

            Assert.StartsWith("# Session 1 ended (belmakor)\n\nBatch `", text, StringComparison.Ordinal);
            Assert.Contains("\nsession:1 is played.\n", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CallTool_PlanThenStart_StartsThePlannedSessionAndSaysItIsNowTheWriteContext()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var plan = await Session(_server, slug, """{"action": "plan", "session": 1, "title": "The Sky Fair", "prep_md": "- Iron Guts at the gate"}""");
        var start = await Session(_server, slug, """{"action": "start", "played_on": "2026-09-12"}""");
        var write = await CampaignWriteSetup.WriteAsync(_server, slug, """[{"op": "upsert", "kind": "character", "name": "Iron Guts"}]""");

        var planId = BatchIdRegex().Match(plan).Groups[1].Value;
        Assert.Equal($"# Session 1 planned ({slug})\n\nBatch `{planId}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{planId}\", " +
                     $"\"campaign\": \"{slug}\"}}.\n\nsession:1 is prepped.\nChanged: session.\n", plan);
        Assert.StartsWith($"# Session 1 started ({slug})\n\nBatch `", start, StringComparison.Ordinal);
        Assert.Contains("\nsession:1 is live. While it is live, every campaign write without a session belongs to it, and knowledge learned defaults to it.\n" +
                        $"{slug} is now the current campaign: calls without campaign use it, and dice_roll logs to its live session.\n" +
                        "Changed: status, started_at, played_on", start, StringComparison.Ordinal);
        Assert.Contains("Session context: session 1.", write, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Log_AppendsToTheLiveSessionAndSaysItIsNotHistory()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "start"}""");

        var text = await Session(_server, slug, """{"action": "log", "notes": ["Serif bargains with the harbourmaster", "Belmakor sings"]}""");

        // No end call is printed: sent as printed, it would end the session now, with no recap.
        Assert.Equal($"# Logged 2 notes to session 1 ({slug})\n\nThe live log of session:1 holds 2 notes. It is a scratchpad for the recap: " +
                     "not in the history and not undoable. When the session ends, the end action checks its names along with the recap's.\n", text);
    }

    [Fact]
    public async Task CallTool_GetForTheAuthor_ShowsThePrepTheLiveLogAndWhatChanged()
    {
        var slug = await LiveSessionAsync(_server);

        var text = await Session(_server, slug, """{"action": "get"}""");

        Assert.StartsWith($"# Session 1: The Sky Fair (live)\n\nsession:1 · {slug} · played on 2026-09-12\n\n## Recap\n_No recap yet._\n\n## Attendance\n" +
                          "- Belmakor Silverwind (character:belmakor): present\n\n## Author only\nVisibility party · started ", text, StringComparison.Ordinal);
        Assert.Contains("\n### Prep\n- Meet the harbourmaster in secret\n", text, StringComparison.Ordinal);
        Assert.Contains("\n### Live log (1)\n", text, StringComparison.Ordinal);
        Assert.Contains(": They almost guessed who the harbourmaster is\n", text, StringComparison.Ordinal);
        // Plan was made between sessions (timeless); start is filed under the session it starts; the live log is no batch.
        Assert.Contains("\n### What changed (1 batch)\n- `", text, StringComparison.Ordinal);
        Assert.Contains("` campaign_session/start\n  - session:1 status: prepped → live\n", text, StringComparison.Ordinal);
        Assert.True(text.Length < 3_000, $"A typical session get is {text.Length} characters.");
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("dm")]
    [InlineData("character:belmakor")]
    public async Task CallTool_GetForAPlayerView_StartsWithTheBannerAndShowsNothingOfTheAuthors(string perspective)
    {
        var slug = await LiveSessionAsync(_server);

        var text = await Session(_server, slug, $$"""{"action": "get", "perspective": "{{perspective}}"}""");

        Assert.StartsWith($"# Session 1: The Sky Fair (live)\n\n_Perspective: {perspective}", text, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Author only", "Prep", "harbourmaster", "Live log", "What changed", "campaign_session/start", "Batch", "Visibility" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task CallTool_GetAndListForAViewThatCannotSeeTheSession_ReadAsIfThereWereNone()
    {
        // The session entity is party-visible: the public does not know it exists, so it gets the "no such session" answer
        // and an empty list, never "hidden" or a count.
        var slug = await LiveSessionAsync(_server);

        var get = await CampaignWriteSetup.FailAsync(_server, "campaign_session", $$"""{"campaign": "{{slug}}", "action": "get", "perspective": "public"}""");
        var list = await Session(_server, slug, """{"action": "list", "perspective": "public"}""");

        Assert.Equal("An error occurred invoking 'campaign_session': No session session:last for this perspective. campaign_session " +
                     $"{{\"action\": \"list\", \"campaign\": \"{slug}\", \"perspective\": \"public\"}} lists the sessions it can see.", get);
        // Only the author plans sessions: a player view with none to see is not told to plan one.
        Assert.Equal($"# Sessions: {slug} (0)\n\n_Perspective: public. Names are the ones this view knows; author-only text is withheld._\n\n" +
                     "No sessions yet.\n", list);
    }

    [Fact]
    public async Task CallTool_GetForACharacter_NamesTheCharacterInTheBanner()
    {
        var slug = await LiveSessionAsync(_server);

        var text = await Session(_server, slug, """{"action": "get", "perspective": "character:belmakor"}""");

        Assert.Contains("\n\n_Perspective: character:belmakor (Belmakor Silverwind). Names are the ones this view knows; author-only text is withheld._\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_End_MarksItPlayedTakesTheBackupAndReturnsTheChecklist()
    {
        var slug = await LiveSessionAsync(_server);
        await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "upsert", "kind": "clock", "name": "Storm front", "clock": {"segments": 4}, "status": "running"},
             {"op": "fact", "statement": "The harbourmaster owes Serif.", "canon_status": "played"}]
            """);

        var text = await Session(_server, slug, """
            {"action": "end", "recap_md": "The band played the Sky Fair. Captain Vortigern watched from the rigging.", "next_hooks": ["Find the harbourmaster"]}
            """);

        var id = BatchIdRegex().Match(text).Groups[1].Value;
        Assert.StartsWith($"# Session 1 ended ({slug})\n\nBatch `{id}`. To undo it: campaign_history {{\"action\": \"undo\", \"batch_id\": \"{id}\", " +
                          $"\"campaign\": \"{slug}\"}}.\n\nsession:1 is played.\nChanged: status, ended_at, data.next_hooks, body_md.\nSession-end backup: ",
            text, StringComparison.Ordinal);
        var backup = Regex.Match(text, "Session-end backup: (.+)\n").Groups[1].Value;
        Assert.True(File.Exists(backup), $"No backup at {backup}.");
        Assert.EndsWith("-session-end.db", backup, StringComparison.Ordinal);
        var checklist = text[text.IndexOf("## Checklist\n", StringComparison.Ordinal)..];
        Assert.Matches(
            "^## Checklist\n" +
            "- \\[ \\] Names that match no entry: \"Captain Vortigern\" — add the new people, places or things; ignore ordinary words\\.\n" +
            "- \\[ \\] Running clocks not ticked this session: tick them \\(campaign_write tick\\) if time passed: clock:storm-front\\.\n" +
            "- \\[ \\] Facts established this session that no player-side knower holds: record who learned them \\(campaign_knowledge record\\): f:\\d+\\.\n$",
            checklist);
    }

    [Fact]
    public async Task CallTool_EndWithNothingLeftToDo_SaysSo()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server, role: "player", myCharacter: "Belmakor Silverwind", myCharacterSlug: "belmakor");
        await Session(_server, slug, """{"action": "start", "attendance": [{"character": "character:belmakor"}]}""");

        var text = await Session(_server, slug, """{"action": "end", "recap_md": "Belmakor sang."}""");

        Assert.EndsWith("\n## Checklist\nNothing left to do: every name is known, clocks are ticked, facts have knowers, attendance is recorded.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EndWithoutAttendance_AsksForIt()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "start"}""");

        var text = await Session(_server, slug, """{"action": "end", "recap_md": "Quiet night."}""");

        Assert.Contains("- [ ] Attendance was not recorded: what the party learned this session reads as \"attendance not recorded\" for every " +
                        "character; give attendance [{character, present}] (record_past can add it).\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_Recap_ListsTheFactsEstablishedAndWhatWasLearned()
    {
        var slug = await LiveSessionAsync(_server);
        var fact = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "fact", "statement": "The harbourmaster owes Serif.", "canon_status": "played", "known_by": [{"who": "party"}]}]
            """))[0];
        await Session(_server, slug, """{"action": "end", "recap_md": "The band played the Sky Fair."}""");

        var text = await Session(_server, slug, """{"action": "recap", "session": 1}""");

        Assert.Equal($"# Recap of session 1: The Sky Fair ({slug})\n\nsession:1 · played · played on 2026-09-12\n\nThe band played the Sky Fair.\n\n" +
                     "## Attendance\nBelmakor Silverwind (present)\n\n" +
                     $"## Facts established (1)\n- {fact} \"The harbourmaster owes Serif.\" · played · known by party\n\n" +
                     $"## Learned this session (1)\n- party: knows {fact} \"The harbourmaster owes Serif.\"\n", text);
    }

    [Fact]
    public async Task CallTool_ListForThePartyAfterPlanningAhead_ShowsOnlyPlayedAndLiveSessions()
    {
        var slug = await LiveSessionAsync(_server);
        await Session(_server, slug, """{"action": "plan", "session": 2, "title": "The harbourmaster's price"}""");

        var author = await Session(_server, slug, """{"action": "list"}""");
        var party = await Session(_server, slug, """{"action": "list", "perspective": "party"}""");

        Assert.Equal($"# Sessions: {slug} (2)\n\n| Session | Title | Status | Played on | Arc |\n|---|---|---|---|---|\n" +
                     "| session:1 | The Sky Fair | live | 2026-09-12 | — |\n| session:2 | The harbourmaster's price | planned | — | — |\n", author);
        Assert.Equal($"# Sessions: {slug} (1)\n\n_Perspective: party. Names are the ones this view knows; author-only text is withheld._\n\n" +
                     "| Session | Title | Status | Played on | Arc |\n|---|---|---|---|---|\n| session:1 | The Sky Fair | live | 2026-09-12 | — |\n", party);
    }

    [Fact]
    public async Task CallTool_RecordPastDryRun_IsLabelledAndLeavesNoSessionBehind()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server, role: "player", myCharacter: "Belmakor Silverwind", myCharacterSlug: "belmakor");
        const string Call = """{"action": "record_past", "session": 0, "title": "Session zero", "recap_md": "We built the band.", "attendance": [{"character": "character:belmakor"}]}""";

        var preview = await Session(_server, slug, Call[..^1] + ", \"dry_run\": true}");
        var list = await Session(_server, slug, """{"action": "list"}""");
        var real = await Session(_server, slug, Call);

        Assert.StartsWith($"# Dry run: Session 0 recorded ({slug}): nothing written\n\n**Dry run: nothing was written and no batch exists.**", preview, StringComparison.Ordinal);
        Assert.Contains("\nsession:0 is played.\n", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign_history", preview, StringComparison.Ordinal);
        Assert.StartsWith($"# Sessions: {slug} (0)\n", list, StringComparison.Ordinal);
        Assert.StartsWith($"# Session 0 recorded ({slug})\n\nBatch `", real, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action": "start", "recap_md": "x"}""",
        "campaign_session start does not take \"recap_md\"; start takes session, played_on, precision, attendance, ingame, title, reason, dry_run, campaign. " +
        "Example: {\"action\": \"start\", \"attendance\": [{\"character\": \"character:serif\"}]}")]
    [InlineData("""{"action": "log", "notes": ["x"], "dry_run": true}""", "campaign_session log does not take \"dry_run\"; log takes notes, campaign.")]
    [InlineData("""{"action": "list", "session": 1, "recap_md": "x"}""", "campaign_session list does not take \"session\" or \"recap_md\"; list takes status, limit, cursor, perspective, campaign.")]
    [InlineData("""{"action": "recap", "perspective": "party"}""", "campaign_session recap does not take \"perspective\"; recap takes session, campaign.")]
    [InlineData("""{"action": "adjourn"}""", "action \"adjourn\" is not a campaign_session action; give list, get, plan, start, log, end, recap, record_past. Example: {")]
    [InlineData("""{"action": "log", "notes": ["x"]}""",
        "No session is live: campaign_session {\"action\": \"start\", \"campaign\": \"{slug}\"} starts one (log notes are for the session at the table).")]
    [InlineData("""{"action": "end", "recap_md": "x"}""",
        "Invalid session: no session is live; to record a session after the fact use campaign_session {\"action\": \"record_past\", \"campaign\": \"{slug}\"}.")]
    [InlineData("""{"action": "plan", "session": -1}""", "Invalid session: session is -1; it is a session number, 0 to 100000.")]
    [InlineData("""{"action": "get", "session": -1}""", "Invalid session: session is -1; it is a session number, 0 to 100000.")]
    [InlineData("""{"action": "recap", "session": -1}""", "Invalid session: session is -1; it is a session number, 0 to 100000.")]
    [InlineData("""{"action": "get", "session": 100001}""", "Invalid session: session is 100001; it is a session number, 0 to 100000.")]
    public async Task CallTool_ArgumentsTheActionDoesNotTakeOrCannotDo_AreRefusedWithWhatToDo(string arguments, string message)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);

        var text = await CampaignWriteSetup.FailAsync(_server, "campaign_session", WithCampaign(slug, arguments));

        // {slug}: the call a refusal prints names the campaign the refused call wrote to.
        Assert.StartsWith("An error occurred invoking 'campaign_session': " + message.Replace("{slug}", slug, StringComparison.Ordinal), text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// FH8 (C09): a negative session number is refused as one, before it becomes a handle. Passed on as text, "-1" was read
    /// as the slug "1", and the refusal named session 1, which here is live: "No session 1 in this campaign".
    /// </summary>
    [Theory]
    [InlineData("get")]
    [InlineData("recap")]
    public async Task CallTool_NegativeSessionWhileSessionOneIsLive_IsRefusedAsOutOfRangeNeverAsSessionOne(string action)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "start"}""");

        var text = await CampaignWriteSetup.FailAsync(_server, "campaign_session", WithCampaign(slug, $$"""{"action": "{{action}}", "session": -1}"""));

        Assert.Equal("An error occurred invoking 'campaign_session': Invalid session: session is -1; it is a session number, 0 to 100000.", text);
    }

    /// <summary>
    /// FH3 (U12): start or end of a campaign named explicitly makes it the current campaign, and says so, so the night's
    /// dice_roll calls log to its live session and writes without campaign go to it. With one-piece current, starting
    /// belmakor's session left every roll out of belmakor's log (a secret roll even said no session was live) and filed
    /// writes without campaign in one-piece, while the start result said the session was every write's context. A dry run
    /// changes nothing but says what the real call would do, and a start that names the campaign already current says
    /// nothing more.
    /// </summary>
    [Fact]
    public async Task CallTool_StartOrEndOfANamedCampaignThatIsNotCurrent_MakesItCurrentSoTheNightsRollsAndWritesGoThere()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            var belmakor = CampaignWriteSetup.CreateCampaign(server, role: "player", slug: "belmakor", myCharacter: "Belmakor", myCharacterSlug: "belmakor");
            await CampaignWriteSetup.CallAsync(server, "campaign", """{"action": "create", "name": "One Piece", "role": "dm", "ruleset": "2024", "slug": "one-piece"}""");

            var dry = await Session(server, belmakor, """{"action": "start", "dry_run": true}""");
            Assert.Contains("\nbelmakor would become the current campaign: calls without campaign would use it, and dice_roll would log to its live session.\n",
                dry, StringComparison.Ordinal);
            Assert.DoesNotContain("is now the current campaign", dry, StringComparison.Ordinal);
            Assert.Equal("one-piece", campaigns.Resolve(null).Slug);

            var start = await Session(server, belmakor, """{"action": "start"}""");
            var roll = await CampaignWriteSetup.CallAsync(server, "dice_roll", """{"expression": "1d20+5", "label": "Perception"}""");
            var write = await CampaignWriteSetup.CallAsync(server, "campaign_write",
                """{"ops": [{"op": "upsert", "kind": "location", "name": "Flotsam"}], "dry_run": true}""");

            Assert.Contains("\nsession:1 is live. While it is live, every campaign write without a session belongs to it, and knowledge learned defaults to it.\n" +
                            "belmakor is now the current campaign: calls without campaign use it, and dice_roll logs to its live session.\n", start, StringComparison.Ordinal);
            Assert.EndsWith("Logged to belmakor, session 1.", roll.TrimEnd(), StringComparison.Ordinal);
            Assert.Contains("(belmakor): nothing written", write, StringComparison.Ordinal);

            await CampaignWriteSetup.CallAsync(server, "campaign", """{"action": "use", "campaign": "one-piece"}""");
            var end = await Session(server, belmakor, """{"action": "end", "recap_md": "They met at the docks."}""");
            Assert.Contains("\nsession:1 is played.\nbelmakor is now the current campaign: calls without campaign use it.\n", end, StringComparison.Ordinal);
            Assert.Equal("belmakor", campaigns.Resolve(null).Slug);

            var again = await Session(server, belmakor, """{"action": "start"}""");
            Assert.DoesNotContain("is now the current campaign", again, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A start or end without <c>campaign</c> went to the campaign calls without it already use (the only campaign, or the
    /// persisted active one, in a process that has chosen none), so it leaves this process's current campaign as it was and
    /// says nothing about it. Only a start or end that names its campaign moves the current campaign (FH3): one that
    /// resolved by default and still made itself current would turn a session's start into a silent use of whichever
    /// campaign happened to be active, in a process that never chose one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallTool_StartAndEndWithoutCampaign_LeaveTheCurrentCampaignAsItWasAndSayNothingOfIt(bool anotherCampaign)
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            var slug = CampaignWriteSetup.CreateCampaign(server, slug: "harbour");
            if (anotherCampaign)
            {
                CampaignWriteSetup.CreateCampaign(server, slug: "inland");
                campaigns.Store.SetActive(campaigns.Store.TryGet(slug)!.Id);
            }

            var start = await CampaignWriteSetup.CallAsync(server, "campaign_session", """{"action": "start"}""");
            var end = await CampaignWriteSetup.CallAsync(server, "campaign_session", """{"action": "end", "recap_md": "They met at the docks."}""");

            Assert.StartsWith("# Session 1 started (harbour)\n", start, StringComparison.Ordinal);
            Assert.StartsWith("# Session 1 ended (harbour)\n", end, StringComparison.Ordinal);
            Assert.DoesNotContain("current campaign", start + end, StringComparison.Ordinal);
            Assert.Null(campaigns.CurrentCampaignId);
        });
    }

    // Every argument but action and campaign (which every action takes), with a value of the type it binds to.
    private static readonly IReadOnlyDictionary<string, string> ArgumentValues = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["session"] = "3",
        ["title"] = "\"The docks\"",
        ["arc"] = "\"arc:sky-fair\"",
        ["prep_md"] = "\"- Open on the docks\"",
        ["played_on"] = "\"2026-09-01\"",
        ["precision"] = "\"day\"",
        ["attendance"] = """[{"character": "character:serif"}]""",
        ["ingame"] = "\"Day 1\"",
        ["ingame_end"] = "\"Day 2\"",
        ["notes"] = """["Serif bargains"]""",
        ["recap_md"] = "\"They met.\"",
        ["next_hooks"] = """["The harbourmaster's price"]""",
        ["confidence"] = "\"confirmed\"",
        ["status"] = "\"played\"",
        ["limit"] = "5",
        ["cursor"] = "\"abc\"",
        ["perspective"] = "\"party\"",
        ["reason"] = "\"why\"",
        ["dry_run"] = "true",
    };

    // What each action takes, as the refusal lists it: the contract of each action.
    private static readonly IReadOnlyDictionary<string, string> ActionTakes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["list"] = "status, limit, cursor, perspective, campaign",
        ["get"] = "session, perspective, campaign",
        ["recap"] = "session, campaign",
        ["plan"] = "session, title, arc, prep_md, played_on, reason, dry_run, campaign",
        ["start"] = "session, played_on, precision, attendance, ingame, title, reason, dry_run, campaign",
        ["log"] = "notes, campaign",
        ["end"] = "session, recap_md, attendance, ingame_end, next_hooks, title, reason, dry_run, campaign",
        ["record_past"] = "session, title, played_on, precision, recap_md, attendance, arc, ingame, confidence, reason, dry_run, campaign",
    };

    /// <summary>
    /// The refusal theory below covers every argument the tool has: the schema's properties are exactly
    /// <see cref="ArgumentValues"/>'s keys plus action and campaign. An argument added to the tool but not to that list
    /// would never be offered to an action that does not take it, and its refusal could be dropped unseen.
    /// </summary>
    [Fact]
    public async Task ToolSchema_EveryArgument_IsInTheRefusalTheory()
    {
        var tool = await CampaignWriteSetup.ToolAsync(_server, "campaign_session");

        Assert.Equal(ArgumentValues.Keys.Append("action").Append("campaign").Order(StringComparer.Ordinal),
            tool.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    /// <summary>Every (action, argument) pair where the action does not take the argument.</summary>
    public static TheoryData<string, string> ArgumentsActionsDoNotTake()
    {
        var rows = new TheoryData<string, string>();
        foreach (var (action, takes) in ActionTakes)
        {
            var taken = takes.Split(", ");
            foreach (var argument in ArgumentValues.Keys.Where(a => !taken.Contains(a)))
            {
                rows.Add(action, argument);
            }
        }

        return rows;
    }

    /// <summary>
    /// Every argument an action does not take is refused, never dropped, with what the action takes, for every action and
    /// every argument outside its list (FH10, M07: a mutant that stopped refusing next_hooks, so record_past wrote the recap
    /// and silently dropped the hooks, survived; only a few pairs were pinned).
    /// </summary>
    [Theory]
    [MemberData(nameof(ArgumentsActionsDoNotTake))]
    public async Task CallTool_ArgumentTheActionDoesNotTake_IsRefusedNamingWhatItTakes(string action, string argument)
    {
        var text = await CampaignWriteSetup.FailAsync(_server, "campaign_session",
            $$"""{"action": "{{action}}", "{{argument}}": {{ArgumentValues[argument]}}}""");

        Assert.StartsWith(
            $"An error occurred invoking 'campaign_session': campaign_session {action} does not take \"{argument}\"; {action} takes {ActionTakes[action]}.",
            text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills A19 (FH10, M15): recap with no session reads the live session when one is live (the documented default: the
    /// live session, else the last played), so the DM sees mid-session what has been established so far.
    /// </summary>
    [Fact]
    public async Task CallTool_RecapWithNoSessionWhileOneIsLive_RecapsTheLiveSession()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "record_past", "session": 1, "played_on": "2026-09-01", "recap_md": "They met."}""");
        await Session(_server, slug, """{"action": "start"}""");

        var recap = await Session(_server, slug, """{"action": "recap"}""");

        Assert.StartsWith("# Recap of session 2", recap, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills N01 (FH10, M15): in a DM campaign the dm is the author (§3.2), so campaign_session's own view resolution gives
    /// the dm no banner and the author's list, as every other tool does through CampaignView.
    /// </summary>
    [Fact]
    public async Task CallTool_ListAsTheDmInADmCampaign_IsTheAuthorsListWithNoBanner()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "plan", "session": 1, "title": "The docks"}""");

        var dm = await Session(_server, slug, """{"action": "list", "perspective": "dm"}""");

        Assert.Equal(await Session(_server, slug, """{"action": "list"}"""), dm);
        Assert.DoesNotContain("_Perspective:", dm, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills B10 (FH10, M16): a session with more rolls than get shows says which part is shown, as the live log does;
    /// without it, 30 rows under "Dice (35)" never said the oldest 5 were left out.
    /// </summary>
    [Fact]
    public async Task CallTool_GetOfASessionWithMoreRollsThanShown_SaysTheLatestNOfM()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            await Session(server, slug, """{"action": "start"}""");
            var rolled = await CampaignWriteSetup.CallAsync(server, "dice_roll", """{"expression": "1d20", "times": 35}""");
            Assert.EndsWith($"Logged to {slug}, session 1.", rolled.TrimEnd(), StringComparison.Ordinal);

            var text = await Session(server, slug, """{"action": "get"}""");

            Assert.Contains("\n## Dice (35)\n_The latest 30 of 35._\n", text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Kills Q09 (FH10, M18): plan, start and record_past keep their reason with the batch, like every other write (the
    /// history is where the DM finds why a session was planned or corrected).
    /// </summary>
    [Theory]
    [InlineData("""{"action": "plan", "session": 1, "title": "The docks", "reason": "Prep from the group chat"}""")]
    [InlineData("""{"action": "start", "reason": "Prep from the group chat"}""")]
    [InlineData("""{"action": "record_past", "session": 1, "played_on": "2026-09-01", "recap_md": "They met.", "reason": "Prep from the group chat"}""")]
    public async Task CallTool_SessionWrite_KeepsItsReasonInTheHistory(string call)
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, call);

        var history = await CampaignWriteSetup.CallAsync(_server, "campaign_history", $$"""{"action": "since", "campaign": "{{slug}}"}""");

        Assert.Contains("\nreason: Prep from the group chat\n", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_StartWhileOneIsLiveOrEndOfAnotherSession_IsRefused()
    {
        var slug = await LiveSessionAsync(_server);
        await Session(_server, slug, """{"action": "plan", "session": 2}""");

        var start = await CampaignWriteSetup.FailAsync(_server, "campaign_session", $$"""{"campaign": "{{slug}}", "action": "start"}""");
        var end = await CampaignWriteSetup.FailAsync(_server, "campaign_session", $$"""{"campaign": "{{slug}}", "action": "end", "session": 2, "recap_md": "x"}""");

        Assert.Equal("An error occurred invoking 'campaign_session': Invalid session: session 1 is live; end it with campaign_session " +
                     $"{{\"action\": \"end\", \"campaign\": \"{slug}\"}} before starting another (one live session at a time).", start);
        Assert.Equal("An error occurred invoking 'campaign_session': Invalid session: session 2 is not the live one (session 1 is); end ends the live session.", end);
    }

    [Fact]
    public async Task CallTool_NullForEveryOptionalArgument_MeansTheDefaults()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);

            var text = server.SuccessText(await server.CallToolJsonAsync("campaign_session", """
                {"action": "list", "campaign": null, "session": null, "title": null, "arc": null, "prep_md": null, "played_on": null,
                 "precision": null, "attendance": null, "ingame": null, "ingame_end": null, "notes": null, "recap_md": null,
                 "next_hooks": null, "confidence": null, "status": null, "limit": null, "cursor": null, "perspective": null,
                 "reason": null, "dry_run": null}
                """));

            Assert.Equal($"# Sessions: {slug} (0)\n\nNo sessions yet. campaign_session {{\"action\": \"plan\", \"campaign\": \"{slug}\"}} plans one; " +
                         "\"start\" starts one; \"record_past\" records one played earlier.\n", text);
        });
    }

    [Fact]
    public void FormatGet_WorstCaseSession_IsCappedWithEverySectionBounded()
    {
        // A 50,000-character recap, 30 attendees, 200 rolls, 2,000 live-log notes and 100 batches of changes.
        var summary = new SessionSummary("session:12", 12, "A title", "played", "2026-09-12", "day", null);
        var attendance = Enumerable.Range(0, 30)
            .Select(i => new AttendanceView(new EntityLink($"character:c{i}", "character", $"Character {i}"), i % 2 == 0, new string('n', 300)))
            .ToList();
        var dice = Enumerable.Range(0, 200)
            .Select(i => new DiceRollView("8d6+4d8>=30", new string('l', 100), 30, true, false, "2026-09-12T20:00:00.000Z", null))
            .ToList();
        var log = Enumerable.Range(0, 2_000).Select(i => new LiveLogEntry("2026-09-12T20:00:00.000Z", new string('x', 500))).ToList();
        var changes = Enumerable.Range(0, 100)
            .Select(i => new HistoryBatch($"batch-{i}", "2026-09-12T20:00:00.000Z", "claude", "campaign_write", new string('r', 500), 12, null,
                Enumerable.Range(0, 20).Select(j => new HistoryChange(j, "update", "update", "fact", "f:1", "statement", null, null, new string('c', 400))).ToList()))
            .ToList();
        var author = new AuthorSessionDetail("party", new string('p', 20_000), log, "2026-09-12T19:00:00.000Z", "2026-09-12T23:00:00.000Z", 5, "{}", changes);
        var detail = new SessionDetail(summary, new string('r', 50_000), null, null, attendance, true, dice, 200, author);

        var text = SessionMarkdown.FormatGet(CampaignWriteSetup.Row("big"), detail, banner: null);

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.Contains("_… cut at 12,000 of 50,000 characters; campaign_session {\"action\": \"recap\", \"session\": 12, \"campaign\": \"big\"} shows more._",
            text, StringComparison.Ordinal);
        // The attendance after the long recap survives it.
        Assert.Contains("- Character 29 (character:c29): absent", text, StringComparison.Ordinal);
        // The output cap's own pointer names the session and the campaign too.
        Assert.EndsWith("_Output cut at 24,000 characters; campaign_session {\"action\": \"recap\", \"session\": 12, \"campaign\": \"big\"} gives the recap on its own._",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRecap_WorstCaseRecap_KeepsBothListsAndCutsTheRecapToTheRoomTheyLeave()
    {
        // A 50,000-character recap (the body limit) with 500 long facts established and 500 rows learned. The lists are
        // what recap is for, so they stay (each bounded and counted) and the recap text is cut, saying so; before, the recap
        // came first in full and the output cap took both lists.
        var summary = new SessionSummary("session:12", 12, "A title", "played", "2026-09-12", "day", null);
        var facts = Enumerable.Range(0, 500).Select(i => new RecapFact($"f:{i}", null, new string('s', 1_000), "played", ["party"])).ToList();
        var learned = Enumerable.Range(0, 500).Select(i => new RecapLearned($"f:{i}", new string('t', 500), "party", "knows", new string('k', 500))).ToList();
        var recap = new SessionRecap(summary, new string('r', 50_000), [], facts, learned);

        var text = SessionMarkdown.FormatRecap(CampaignWriteSetup.Row("big"), recap);

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.DoesNotContain("_Output cut at", text, StringComparison.Ordinal);
        Assert.Matches(@"\n\n_… cut at [\d,]+ of 50,000 characters so the lists below fit the output limit; the stored recap is whole\._\n\n## Facts established \(500\)\n", text);
        Assert.Matches(@"\n_… and \d+ more facts\._\n\n## Learned this session \(500\)\n- party: knows f:0 ", text);
        Assert.Matches(@"\n_… and \d+ more rows\._\n$", text);
    }

    [Fact]
    public void FormatRecap_RecapLongerThanGetShows_IsShownWholeWhenTheListsLeaveRoom()
    {
        var summary = new SessionSummary("session:12", 12, "A title", "played", "2026-09-12", "day", null);
        var text = new string('r', 20_000);

        var result = SessionMarkdown.FormatRecap(CampaignWriteSetup.Row("big"),
            new SessionRecap(summary, text, [], [new RecapFact("f:1", null, "A fact.", "played", [])], []));

        Assert.Contains("\n\n" + text + "\n\n## Facts established (1)\n- f:1 \"A fact.\" · played · known by nobody on record\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("cut at", result, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatRecap_FactKnownByManyKnowers_NamesEightAndCountsTheRest()
    {
        var summary = new SessionSummary("session:12", 12, "A title", "played", null, "unknown", null);
        var knowers = Enumerable.Range(0, 30).Select(i => $"character:crew-{i}").ToList();

        var text = SessionMarkdown.FormatRecap(CampaignWriteSetup.Row("big"),
            new SessionRecap(summary, "Recap.", [], [new RecapFact("f:1", "F3", "A fact.", "played", knowers)], []));

        Assert.Contains("\n- f:1 (F3) \"A fact.\" · played · known by character:crew-0, character:crew-1, character:crew-2, character:crew-3, " +
                        "character:crew-4, character:crew-5, character:crew-6, character:crew-7, … and 22 more\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_EndWithNextHooks_GetShowsThemToTheAuthorAfterTheRecapAndToNoOneElse()
    {
        // session_prep reads the last session's hooks through get; end stores them in the session's data (author-only).
        var slug = await LiveSessionAsync(_server);
        await Session(_server, slug, """{"action": "end", "recap_md": "The band played.", "next_hooks": ["Follow the envoy", "Pay the harbourmaster"]}""");

        var author = await Session(_server, slug, """{"action": "get", "session": 1}""");
        var party = await Session(_server, slug, """{"action": "get", "session": 1, "perspective": "party"}""");

        Assert.Contains("\n## Recap\nThe band played.\n\n## Next hooks\n- Follow the envoy\n- Pay the harbourmaster\n\n## Attendance\n", author, StringComparison.Ordinal);
        Assert.DoesNotContain("Next hooks", party, StringComparison.Ordinal);
        Assert.DoesNotContain("envoy", party, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    [InlineData("{\"next_hooks\": \"one\"}", 0)]
    [InlineData("{\"next_hooks\": [\"a\", \" \", 3]}", 2)]
    [InlineData("[1, 2]", 0)]
    public void NextHooks_WhateverTheDataHolds_ReadsTheHookListOrNone(string? data, int count)
    {
        Assert.Equal(count, SessionMarkdown.NextHooks(data).Count);
    }

    [Fact]
    public async Task CallTool_ListWithALimit_GivesACursorThatReadsTheNextPage()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "record_past", "session": 1, "title": "The Sky Fair"}""");
        await Session(_server, slug, """{"action": "plan", "session": 2, "title": "The harbourmaster's price"}""");

        var first = await Session(_server, slug, """{"action": "list", "limit": 1}""");
        var cursor = Regex.Match(first, "\nMore: pass cursor \"([^\"]+)\" for the next page\\.\n$").Groups[1].Value;
        var second = await Session(_server, slug, $$"""{"action": "list", "limit": 1, "cursor": "{{cursor}}"}""");

        Assert.NotEqual(string.Empty, cursor);
        Assert.Contains("| session:1 | The Sky Fair | played |", first, StringComparison.Ordinal);
        Assert.Equal($"# Sessions: {slug} (2)\n\n| Session | Title | Status | Played on | Arc |\n|---|---|---|---|---|\n" +
                     "| session:2 | The harbourmaster's price | planned | — | — |\n", second);
    }

    [Fact]
    public async Task CallTool_ListByStatus_SaysTheStatusInItsTitle()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "record_past", "session": 1, "title": "The Sky Fair"}""");
        await Session(_server, slug, """{"action": "plan", "session": 2, "title": "The harbourmaster's price"}""");

        var text = await Session(_server, slug, """{"action": "list", "status": "planned"}""");

        Assert.Equal($"# Sessions: {slug} (1 planned)\n\n| Session | Title | Status | Played on | Arc |\n|---|---|---|---|---|\n" +
                     "| session:2 | The harbourmaster's price | planned | — | — |\n", text);
    }

    [Fact]
    public async Task CallTool_RecapOfAMonthDatedSession_ReadsExactlyAsWrittenWithTheNameEachKnowerUses()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "record_past", "session": 1, "title": "The Sky Fair", "played_on": "2026-09", "recap_md": "The band played."}""");
        var fact = CampaignWriteSetup.Refs(await CampaignWriteSetup.WriteAsync(_server, slug, """
            [{"op": "fact", "statement": "The harbourmaster owes Serif.", "canon_status": "played", "known_by": [{"who": "party", "known_as": "the debt"}]}]
            """, "\"session\": 1"))[0];

        var text = await Session(_server, slug, """{"action": "recap", "session": 1}""");

        Assert.Equal($"# Recap of session 1: The Sky Fair ({slug})\n\nsession:1 · played · played on 2026-09 (month)\n\nThe band played.\n\n" +
                     $"## Facts established (1)\n- {fact} \"The harbourmaster owes Serif.\" · played · known by party\n\n" +
                     $"## Learned this session (1)\n- party: knows {fact} \"The harbourmaster owes Serif.\" as \"the debt\"\n", text);
    }

    [Fact]
    public async Task CallTool_PlanThatChangesNothingAndGetOfAnUntitledSession_SayWhatHappenedWithoutRepeatingThemselves()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "plan", "session": 2}""");

        var again = await Session(_server, slug, """{"action": "plan", "session": 2}""");
        var get = await Session(_server, slug, """{"action": "get", "session": 2}""");
        var recap = await Session(_server, slug, """{"action": "recap", "session": 2}""");

        Assert.Equal($"# Session 2 plan unchanged ({slug})\n\nNothing changed: everything already matched what is stored, so there is no batch to undo.\n\n" +
                     "session:2 is planned.\n", again);
        Assert.StartsWith($"# Session 2 (planned)\n\nsession:2 · {slug}\n", get, StringComparison.Ordinal);
        Assert.StartsWith($"# Recap of session 2 ({slug})\n", recap, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatGet_SessionWithDiceAnInGameSpanAndAMonthDate_RendersEachPartInOrder()
    {
        var summary = new SessionSummary("session:3", 3, "Session 3", "played", "2026-09", "month", new EntityLink("arc:sky-fair", "arc", "The Sky Fair"));
        var detail = new SessionDetail(summary, "The band played.", "Day 3 of the Thaw", "Day 4",
            [new AttendanceView(new EntityLink("character:belmakor", "character", "Belmakor"), true, null),
             new AttendanceView(new EntityLink("character:serif", "character", "Serif"), false, "sick")], true,
            [new DiceRollView("1d20+5>=15", "Stealth", 18, true, false, "2026-09-12T20:00:00.000Z", null),
             new DiceRollView("1d20>=12", "Save", 4, false, false, "2026-09-12T20:03:00.000Z", null),
             new DiceRollView("2d6", null, 7, null, true, "2026-09-12T20:05:00.000Z", null)], 3,
            new AuthorSessionDetail("party", string.Empty, [], null, null, null, "{\"next_hooks\": [\"Follow the envoy\"]}", []));

        var text = SessionMarkdown.FormatGet(CampaignWriteSetup.Row("big"), detail, banner: null);

        Assert.Equal(
            "# Session 3 (played)\n\nsession:3 · big · played on 2026-09 (month) · arc arc:sky-fair (The Sky Fair)\nIn game: Day 3 of the Thaw → Day 4\n\n" +
            "## Recap\nThe band played.\n\n## Next hooks\n- Follow the envoy\n\n" +
            "## Attendance\n- Belmakor (character:belmakor): present\n- Serif (character:serif): absent — sick\n\n" +
            "## Dice (3)\n| At | Roll | Label | Total |\n|---|---|---|---|\n" +
            "| 2026-09-12T20:00:00.000Z | 1d20+5>=15 | Stealth | 18 (success) |\n" +
            "| 2026-09-12T20:03:00.000Z | 1d20>=12 | Save | 4 (failure) |\n" +
            "| 2026-09-12T20:05:00.000Z | 2d6 | — (secret) | 7 |\n\n" +
            "## Author only\nVisibility party\n",
            text);
    }

    [Fact]
    public void FormatGet_LongLiveLogAndBigBatches_ShowsTheNewestNotesAndPointsAtTheHistoryForTheRest()
    {
        var summary = new SessionSummary("session:3", 3, "The Sky Fair", "live", null, "unknown", null);
        var log = Enumerable.Range(0, 100).Select(i => new LiveLogEntry(null, $"note {i:D3}")).ToList();
        var batches = Enumerable.Range(0, 25)
            .Select(i => new HistoryBatch($"batch-{i}", "2026-09-12T20:00:00.000Z", "claude", "campaign_write", null, 3, null,
                Enumerable.Range(0, 12).Select(j => new HistoryChange(j, "update", "update", "fact", "f:1", "statement", null, null, $"change {i}.{j}")).ToList()))
            .ToList();
        var detail = new SessionDetail(summary, null, null, null, [], false, [], 0,
            new AuthorSessionDetail("party", string.Empty, log, null, null, null, "{}", batches));

        var text = SessionMarkdown.FormatGet(CampaignWriteSetup.Row("big"), detail, banner: null);

        Assert.Contains("\n### Live log (100)\n- note 060\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- note 099\n_The latest 40 of 100 notes are shown._\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("note 059", text, StringComparison.Ordinal);
        Assert.Contains("\n  - change 0.7\n  - … and 4 more (campaign_history {\"action\": \"batch\", \"batch_id\": \"batch-0\", \"campaign\": \"big\"} shows them)\n",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("change 0.8", text, StringComparison.Ordinal);
        Assert.EndsWith("\n_… and 5 more batches (campaign_history {\"action\": \"since\", \"session\": 3, \"campaign\": \"big\"} shows them)._\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatWrite_SessionEndBackupThatFailed_SaysItWasNotWritten()
    {
        var result = new SessionWriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, "session:3", 3, "played", WriteOutcomes.Updated,
            ["status"], [], new SessionChecklist([], [], [], [], [], false), null, "disk full");

        var text = SessionMarkdown.FormatWrite(CampaignWriteSetup.Row("big"), "end", result);

        Assert.Equal("# Session 3 ended (big)\n\nBatch `0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000`. To undo it: campaign_history {\"action\": \"undo\", " +
                     "\"batch_id\": \"0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000\", \"campaign\": \"big\"}.\n\nsession:3 is played.\nChanged: status.\nBackup not written: disk full\n\n" +
                     "## Checklist\nNothing left to do: every name is known, clocks are ticked, facts have knowers, attendance is recorded.\n", text);
    }

    [Fact]
    public void FormatWrite_WorstCaseChecklist_ListsEachEntryBoundedAndCountsTheRest()
    {
        var many = Enumerable.Range(0, 400).Select(i => $"Name number {i.ToString(CultureInfo.InvariantCulture)}").ToList();
        var checklist = new SessionChecklist(many, many, many, many, many, true);
        var result = new SessionWriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, "session:12", 12, "played", WriteOutcomes.Updated,
            ["status"], [], checklist, "/data/backups/campaigns-x-session-end.db");

        var text = SessionMarkdown.FormatWrite(CampaignWriteSetup.Row("big"), "end", result);

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.Equal(4, Regex.Matches(text, ", … and 360 more\\.\n").Count);
        Assert.Contains(", … and 360 more — add the new people, places or things; ignore ordinary words.\n", text, StringComparison.Ordinal);
        Assert.Contains("- [ ] Attendance was not recorded:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH7 (C06): one checklist value longer than the output cap (a recap that is one long capitalised run gives one
    /// "unknown name" of tens of thousands of characters) is cut, and every entry after it stays. Unbounded, the entry was
    /// one line longer than the cap, and the cap dropped it with everything after it: "## Checklist" and the cut note.
    /// </summary>
    [Fact]
    public void FormatWrite_ChecklistValueLongerThanTheCap_IsCutAndEveryEntryStays()
    {
        var huge = string.Join(' ', Enumerable.Range(0, 3_000).Select(i => "Recap Nm" + i.ToString(CultureInfo.InvariantCulture)));
        var checklist = new SessionChecklist([huge, "Serif"], ["clock:storm"], [], [], [], true);
        var result = new SessionWriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, "session:12", 12, "played", WriteOutcomes.Updated,
            ["status"], [], checklist, "/data/backups/campaigns-x-session-end.db");

        var text = SessionMarkdown.FormatWrite(CampaignWriteSetup.Row("run"), "end", result);

        Assert.DoesNotContain("Output cut at", text, StringComparison.Ordinal);
        var names = Assert.Single(text.Split('\n'), line => line.Contains("Recap Nm0 Recap Nm1 ", StringComparison.Ordinal));
        Assert.True(names.Length < 1_000, $"the names entry is {names.Length} characters");
        Assert.Contains("\"Serif\"", names, StringComparison.Ordinal);
        Assert.Contains("\n- [ ] Running clocks not ticked this session: tick them (campaign_write tick) if time passed: clock:storm.\n", text, StringComparison.Ordinal);
        Assert.Contains("\n- [ ] Attendance was not recorded:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH7 (C06): an entry of many long values stops once it passes <see cref="SessionMarkdown.MaxEntryChars"/> and counts
    /// the rest. Each value is cut at <see cref="SessionMarkdown.MaxItemChars"/>, but forty of them are still one line of
    /// 8,000 characters per entry, and six such entries pass the output cap, which drops the last whole.
    /// </summary>
    [Fact]
    public void FormatWrite_ChecklistEntryOfManyLongValues_StopsPastTheEntryBudgetAndCountsTheRest()
    {
        var names = Enumerable.Range(0, SessionMarkdown.MaxItems)
            .Select(i => "Name " + i.ToString("D2", CultureInfo.InvariantCulture) + " " + new string('n', SessionMarkdown.MaxItemChars)).ToList();
        var checklist = new SessionChecklist(names, [], [], [], [], false);
        var result = new SessionWriteResult("0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000", false, "session:12", 12, "played", WriteOutcomes.Updated,
            ["status"], [], checklist, "/data/backups/campaigns-x-session-end.db");

        var text = SessionMarkdown.FormatWrite(CampaignWriteSetup.Row("run"), "end", result);

        const string advice = " — add the new people, places or things; ignore ordinary words.";
        var entry = Assert.Single(text.Split('\n'), line => line.StartsWith("- [ ] Names that match no entry: ", StringComparison.Ordinal));
        var shown = Regex.Matches(entry, "\"Name \\d\\d n").Count;
        Assert.InRange(entry.Length, SessionMarkdown.MaxEntryChars, SessionMarkdown.MaxEntryChars + SessionMarkdown.MaxItemChars + 40 + advice.Length);
        Assert.InRange(shown, 1, SessionMarkdown.MaxItems - 1);
        Assert.EndsWith($", … and {(SessionMarkdown.MaxItems - shown).ToString(CultureInfo.InvariantCulture)} more{advice}", entry, StringComparison.Ordinal);
    }

    /// <summary>The same through the tool (C06b): an end whose recap is one long capitalised run still returns its checklist.</summary>
    [Fact]
    public async Task CallTool_EndWithAHugeNameRun_KeepsTheChecklist()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "start"}""");
        var recap = string.Join(" ", Enumerable.Range(0, 3_000).Select(i => "Recap Nm" + i.ToString(CultureInfo.InvariantCulture)));

        var end = await Session(_server, slug, $$"""{"action": "end", "recap_md": "{{recap}}"}""");

        Assert.DoesNotContain("Output cut at", end, StringComparison.Ordinal);
        Assert.Contains("\n- [ ] ", end, StringComparison.Ordinal);
        Assert.Contains("\n- [ ] Attendance was not recorded:", end, StringComparison.Ordinal);
    }

    /// <summary>
    /// The calls an empty list and a session write print name their campaign, so sent exactly as printed while another
    /// campaign is current they act on the one the result was about: the plan hint plans there, and the write's undo
    /// reverses it there. Without <c>campaign</c> the plan went to the current campaign and the undo found no batch.
    /// </summary>
    [Fact]
    public async Task CallTool_PlanHintAndUndoCallAsPrinted_ActOnTheirCampaignWhileAnotherIsCurrent()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            var empty = await Session(server, slug, """{"action": "list"}""");
            var campaigns = server.Services.GetRequiredService<CampaignService>();
            campaigns.Use(other);

            var planned = await CampaignWriteSetup.CallAsync(server, "campaign_session", Assert.Single(CampaignWriteSetup.PrintedCalls(empty, "campaign_session")));
            var afterPlan = await Session(server, slug, """{"action": "list"}""");
            var undo = await CampaignWriteSetup.CallAsync(server, "campaign_history", Assert.Single(CampaignWriteSetup.PrintedCalls(planned, "campaign_history")));

            Assert.StartsWith($"# Session 1 planned ({slug})\n", planned, StringComparison.Ordinal);
            Assert.StartsWith($"# Sessions: {slug} (1)\n", afterPlan, StringComparison.Ordinal);
            Assert.StartsWith("# Undo of batch ", undo, StringComparison.Ordinal);
            Assert.StartsWith($"# Sessions: {slug} (0)\n", await Session(server, slug, """{"action": "list"}"""), StringComparison.Ordinal);
            Assert.StartsWith($"# Sessions: {other} (0)\n", await Session(server, other, """{"action": "list"}"""), StringComparison.Ordinal);
            Assert.Equal(other, campaigns.Resolve(null).Slug);
        });
    }

    /// <summary>
    /// A session get points at more than it shows: the rest of a long recap, the rest of a batch's changes, the batches past
    /// the first twenty. Each pointer names the campaign and the session it is about, so sent as printed while another
    /// campaign is current it reads that session of that campaign: the recap of session 1 (not the last played, session
    /// 2, which a recap call without session reads), the batch, and the history from session 1 on.
    /// </summary>
    [Fact]
    public async Task CallTool_SessionGetsPointersAsPrinted_ReadThatSessionOfThatCampaignWhileAnotherIsCurrent()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            await Session(server, slug, $$"""{"action": "record_past", "session": 1, "title": "The Sky Fair", "recap_md": "{{new string('r', SessionMarkdown.MaxRecap + 1_000)}}"}""");
            await Session(server, slug, """{"action": "record_past", "session": 2, "title": "The harbour"}""");
            var sailors = string.Join(", ", Enumerable.Range(0, SessionMarkdown.MaxChangesPerBatch + 1)
                .Select(i => $$"""{"op": "upsert", "kind": "character", "name": "Sailor {{i}}"}"""));
            await CampaignWriteSetup.WriteAsync(server, slug, $"[{sailors}]", "\"session\": 1");
            for (var i = 0; i < SessionMarkdown.MaxBatches; i++)
            {
                await CampaignWriteSetup.WriteAsync(server, slug, $$"""[{"op": "upsert", "kind": "location", "name": "Dock {{i}}"}]""", "\"session\": 1");
            }

            var get = await Session(server, slug, """{"action": "get", "session": 1}""");
            server.Services.GetRequiredService<CampaignService>().Use(other);

            var recaps = CampaignWriteSetup.PrintedCalls(get, "campaign_session");
            var history = CampaignWriteSetup.PrintedCalls(get, "campaign_history");
            Assert.NotEmpty(recaps);
            Assert.Contains(history, c => c.Contains("\"action\": \"batch\"", StringComparison.Ordinal));
            Assert.Contains(history, c => c.Contains("\"action\": \"since\"", StringComparison.Ordinal));
            foreach (var call in recaps)
            {
                Assert.StartsWith($"# Recap of session 1: The Sky Fair ({slug})\n", await CampaignWriteSetup.CallAsync(server, "campaign_session", call),
                    StringComparison.Ordinal);
            }

            foreach (var call in history)
            {
                var text = await CampaignWriteSetup.CallAsync(server, "campaign_history", call);
                Assert.True(text.StartsWith("# Batch ", StringComparison.Ordinal) || text.StartsWith($"# History of {slug} since session 1", StringComparison.Ordinal),
                    $"{call} read: {text[..Math.Min(200, text.Length)]}");
            }
        });
    }

    /// <summary>
    /// The "no such session" refusal's list call (the session reader's) is JSON that names the campaign and the view, so
    /// sent as printed while another campaign is current it lists what that view of that campaign can see.
    /// </summary>
    [Fact]
    public async Task CallTool_NoSuchSessionsListCallAsPrinted_ListsThatViewOfThatCampaignWhileAnotherIsCurrent()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = await LiveSessionAsync(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            server.Services.GetRequiredService<CampaignService>().Use(other);

            var refusal = await CampaignWriteSetup.FailAsync(server, "campaign_session", $$"""{"campaign": "{{slug}}", "action": "get", "perspective": "public"}""");
            var list = await CampaignWriteSetup.CallAsync(server, "campaign_session", Assert.Single(CampaignWriteSetup.PrintedCalls(refusal, "campaign_session")));

            Assert.Equal($"# Sessions: {slug} (0)\n\n_Perspective: public. Names are the ones this view knows; author-only text is withheld._\n\nNo sessions yet.\n", list);
        });
    }

    /// <summary>
    /// With a status filter an empty list says that status has no sessions, not "no sessions yet" (there may be others),
    /// and only the author, with no filter, is told how to plan one.
    /// </summary>
    [Theory]
    [InlineData(null, null, "No sessions yet. campaign_session {\"action\": \"plan\", \"campaign\": \"big\"} plans one; \"start\" starts one; \"record_past\" records one played earlier.\n")]
    [InlineData(null, "cancelled", "No cancelled sessions.\n")]
    [InlineData("_Perspective: party. Names are the ones this view knows; author-only text is withheld._", null, "No sessions yet.\n")]
    [InlineData("_Perspective: party. Names are the ones this view knows; author-only text is withheld._", "planned", "No planned sessions.\n")]
    public void FormatList_NoSessionsToShow_SaysSoAndOnlyTellsTheAuthorToPlan(string? banner, string? status, string expected)
    {
        var text = SessionMarkdown.FormatList(CampaignWriteSetup.Row("big"), new SessionPage([], null, 0), banner, status);

        Assert.EndsWith("\n" + expected, text, StringComparison.Ordinal);
        Assert.Equal(banner is null && status is null, text.Contains("\"action\": \"plan\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The session writer's refusals print the call that fixes them, and it names the campaign the refused call wrote to:
    /// sent as printed while another campaign with a live session is current, each acts on the refused call's campaign. A
    /// printed start (after a log with nothing live), record_past (after an end with nothing live) and end (after a start
    /// or a record_past of the live session; the model adds the recap) without campaign started, recorded or ended a
    /// session in the current campaign instead, ending its live session with a recap meant for another. A start or end that
    /// names its campaign then makes it the current one (FH3: the night's rolls and writes follow it); a record_past leaves
    /// the current campaign as it was.
    /// </summary>
    [Theory]
    [InlineData("""{"action": "log", "notes": ["x"]}""", false, "started")]
    [InlineData("""{"action": "end", "recap_md": "We played."}""", false, "recorded")]
    [InlineData("""{"action": "start"}""", true, "ended")]
    [InlineData("""{"action": "record_past", "session": 1}""", true, "ended")]
    public async Task CallTool_SessionRefusalsCallAsPrinted_ActsOnTheRefusedCallsCampaignWhileAnotherIsCurrent(string refused, bool live, string outcome)
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            await Session(server, other, """{"action": "start"}""");
            if (live)
            {
                await Session(server, slug, """{"action": "start"}""");
            }

            var campaigns = server.Services.GetRequiredService<CampaignService>();
            campaigns.Use(other);

            var refusal = await CampaignWriteSetup.FailAsync(server, "campaign_session", WithCampaign(slug, refused));
            var printed = Assert.Single(CampaignWriteSetup.PrintedCalls(refusal, "campaign_session"));
            var sent = printed.Contains("\"end\"", StringComparison.Ordinal) ? printed[..^1] + ", \"recap_md\": \"We played.\"}" : printed;
            var text = await CampaignWriteSetup.CallAsync(server, "campaign_session", sent);

            Assert.StartsWith($"# Session 1 {outcome} ({slug})\n", text, StringComparison.Ordinal);
            Assert.StartsWith($"# Sessions: {other} (1 live)\n", await Session(server, other, """{"action": "list", "status": "live"}"""), StringComparison.Ordinal);
            Assert.Equal(outcome == "recorded" ? other : slug, campaigns.Resolve(null).Slug);
        });
    }

    /// <summary>
    /// An undo that would make an ended session live again while another session of that campaign is live is refused
    /// with the end call that clears the way, naming the campaign: sent (with its recap) while another campaign with a live
    /// session is current, it ends the refused campaign's live session, not the current campaign's.
    /// </summary>
    [Fact]
    public async Task CallTool_UndoOfAnEndWhileAnotherSessionIsLive_PrintsAnEndCallThatEndsThatCampaignsSession()
    {
        await CampaignWriteSetup.WithServerAsync(async server =>
        {
            var slug = CampaignWriteSetup.CreateCampaign(server);
            var other = CampaignWriteSetup.CreateCampaign(server);
            await Session(server, slug, """{"action": "start"}""");
            var end = BatchIdRegex().Match(await Session(server, slug, """{"action": "end", "recap_md": "Session one."}""")).Groups[1].Value;
            await Session(server, slug, """{"action": "start"}""");
            await Session(server, other, """{"action": "start"}""");
            server.Services.GetRequiredService<CampaignService>().Use(other);

            var refusal = await CampaignWriteSetup.FailAsync(server, "campaign_history",
                $$"""{"campaign": "{{slug}}", "action": "undo", "batch_id": "{{end}}"}""");
            var printed = Assert.Single(CampaignWriteSetup.PrintedCalls(refusal, "campaign_session"));
            var text = await CampaignWriteSetup.CallAsync(server, "campaign_session", printed[..^1] + ", \"recap_md\": \"Session two.\"}");

            Assert.Contains("End session 2 first (campaign_session ", refusal, StringComparison.Ordinal);
            Assert.StartsWith($"# Session 2 ended ({slug})\n", text, StringComparison.Ordinal);
            Assert.StartsWith($"# Sessions: {other} (1 live)\n", await Session(server, other, """{"action": "list", "status": "live"}"""), StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A player view's get of a session with a long recap says where the recap was cut and points nowhere: the recap action
    /// is the author's (it lists what the session established and who learned what, and takes no perspective), so a model
    /// drafting in character must not be sent to it. The author's get points at it, for that session of that campaign. The
    /// output cap's own note follows the same rule.
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("_Perspective: party. Names are the ones this view knows; author-only text is withheld._", false)]
    public void FormatGet_LongRecap_PointsOnlyTheAuthorAtTheRecapAction(string? banner, bool pointed)
    {
        var summary = new SessionSummary("session:12", 12, "A title", "played", null, "unknown", null);
        var detail = new SessionDetail(summary, new string('r', SessionMarkdown.MaxRecap + 5_000), null, null, [], true, [], 0, null);

        var text = SessionMarkdown.FormatGet(CampaignWriteSetup.Row("big"), detail, banner);

        Assert.Contains(pointed
            ? "_… cut at 12,000 of 17,000 characters; campaign_session {\"action\": \"recap\", \"session\": 12, \"campaign\": \"big\"} shows more._"
            : "_… cut at 12,000 of 17,000 characters._", text, StringComparison.Ordinal);
        Assert.Equal(pointed, text.Contains("campaign_session", StringComparison.Ordinal));
    }

    /// <summary>
    /// The output cap of a player view's get names no call either (here a session whose arc name alone passes the cap): it
    /// says only that the rest is not shown.
    /// </summary>
    [Fact]
    public void FormatGet_PlayerViewPastTheCap_SaysTheRestIsNotShownWithoutACall()
    {
        var summary = new SessionSummary("session:12", 12, "A title", "played", null, "unknown",
            new EntityLink("arc:long", "arc", new string('a', CampaignMarkdownText.MaxChars)));
        var detail = new SessionDetail(summary, "Short recap.", null, null, [], true, [], 0, null);

        var text = SessionMarkdown.FormatGet(CampaignWriteSetup.Row("big"), detail,
            "_Perspective: party. Names are the ones this view knows; author-only text is withheld._");

        Assert.True(text.Length <= CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.EndsWith("_Output cut at 24,000 characters; the rest of this session is not shown._", text, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign_session", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The status filter is printed as the reader matched it: a blank one filters nothing, so an empty list is "no sessions
    /// yet" with the author's plan hint (not "No  sessions." and no hint), and a forgiving spelling prints the status
    /// itself, in the heading and the empty line.
    /// </summary>
    [Theory]
    [InlineData(" ", "# Sessions: big (0)\n", "No sessions yet. campaign_session {\"action\": \"plan\", \"campaign\": \"big\"} plans one;")]
    [InlineData("", "# Sessions: big (0)\n", "No sessions yet. campaign_session {\"action\": \"plan\", \"campaign\": \"big\"} plans one;")]
    [InlineData("PLANNED", "# Sessions: big (0 planned)\n", "No planned sessions.\n")]
    [InlineData(" Cancelled ", "# Sessions: big (0 cancelled)\n", "No cancelled sessions.\n")]
    public void FormatList_StatusAsTyped_IsPrintedAsTheReaderMatchedIt(string status, string heading, string line)
    {
        var text = SessionMarkdown.FormatList(CampaignWriteSetup.Row("big"), new SessionPage([], null, 0), null, status);

        Assert.StartsWith(heading, text, StringComparison.Ordinal);
        Assert.Contains("\n\n" + line, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review L01: a planned session's title is prep, hidden from every player view until the session is live. Start with
    /// that title says players now see it; end (or start) with a title renames the session, and players see that name.
    /// </summary>
    [Fact]
    public async Task CallTool_StartKeepingThePlannedTitle_WarnsAndATitleGivenToEndRenamesTheSession()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "plan", "session": 1, "title": "Morwen unmasked at the lighthouse"}""");

        var start = await Session(_server, slug, """{"action": "start"}""");
        var end = await Session(_server, slug, """{"action": "end", "recap_md": "We reached the lighthouse.", "title": "The lighthouse"}""");
        var list = await Session(_server, slug, """{"action": "list", "perspective": "party"}""");

        Assert.Contains("\n- **warning** · planned title: session:1's planned title \"Morwen unmasked at the lighthouse\" is now visible to players; " +
                        "pass title to end to change it.\n", start, StringComparison.Ordinal);
        Assert.Contains("\nChanged: status, ended_at, body_md, name.\n", end, StringComparison.Ordinal);
        Assert.Contains("The lighthouse", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Morwen", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallTool_StartWithATitle_NamesThePlannedSessionWithoutAWarning()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server);
        await Session(_server, slug, """{"action": "plan", "session": 1, "title": "Morwen unmasked at the lighthouse"}""");

        var start = await Session(_server, slug, """{"action": "start", "title": "Night one"}""");
        var list = await Session(_server, slug, """{"action": "list", "perspective": "party"}""");

        Assert.DoesNotContain("planned title", start, StringComparison.Ordinal);
        Assert.Contains("\nChanged: status, started_at, played_on, played_on_precision, visibility, name.\n", start, StringComparison.Ordinal);
        Assert.Contains("Night one", list, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review C14: record_past corrects a played session, attendance included: the list given replaces the recorded one,
    /// and the result names who was removed (before, the wrongly listed character stayed and the result said nothing changed).
    /// </summary>
    [Fact]
    public async Task CallTool_RecordPastCorrectingAttendance_ReplacesItAndSaysWhoWasRemoved()
    {
        var slug = CampaignWriteSetup.CreateCampaign(_server, role: "player", myCharacter: "Belmakor Silverwind", myCharacterSlug: "belmakor");
        await CampaignWriteSetup.WriteAsync(_server, slug, """[{"op": "upsert", "kind": "character", "name": "Serif", "subtype": "pc"}]""");
        await Session(_server, slug, """{"action": "record_past", "session": 1, "recap_md": "One.", "attendance": [{"character": "character:belmakor"}, {"character": "character:serif"}]}""");

        var text = await Session(_server, slug, """{"action": "record_past", "session": 1, "recap_md": "One.", "attendance": [{"character": "character:belmakor"}]}""");
        var get = await Session(_server, slug, """{"action": "get", "session": 1}""");

        Assert.StartsWith($"# Session 1 record updated ({slug})\n", text, StringComparison.Ordinal);
        Assert.Contains("\nChanged: attendance (removed character:serif).\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Attendance\n- Belmakor Silverwind (character:belmakor): present\n\n", get, StringComparison.Ordinal);
    }

    // A live session 1 "The Sky Fair" with prep, attendance and one live-log note, in a player campaign whose PC is Belmakor.
    private static async Task<string> LiveSessionAsync(McpServerHarness server)
    {
        var slug = CampaignWriteSetup.CreateCampaign(server, role: "player", myCharacter: "Belmakor Silverwind", myCharacterSlug: "belmakor");
        await Session(server, slug, """{"action": "plan", "session": 1, "title": "The Sky Fair", "prep_md": "- Meet the harbourmaster in secret"}""");
        await Session(server, slug, """{"action": "start", "played_on": "2026-09-12", "attendance": [{"character": "character:belmakor"}]}""");
        await Session(server, slug, """{"action": "log", "notes": ["They almost guessed who the harbourmaster is"]}""");
        return slug;
    }

    private static Task<string> Session(McpServerHarness server, string slug, string arguments) =>
        CampaignWriteSetup.CallAsync(server, "campaign_session", WithCampaign(slug, arguments));

    // {"action": …} → {"campaign": "<slug>", "action": …}: every call names its campaign (the class shares one harness).
    private static string WithCampaign(string slug, string arguments) => $"{{\"campaign\": \"{slug}\", {arguments.Trim()[1..]}";
}
