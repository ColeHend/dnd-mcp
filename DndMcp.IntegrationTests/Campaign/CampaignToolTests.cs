using System.Text.RegularExpressions;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the <c>campaign</c> tool creates, lists, chooses and changes campaigns exactly as contract §3.7 and §9 say:
/// create and use make the campaign this process's current one (and the persisted active one), every write prints its
/// batch id in full with an undo call that works exactly as printed even while another campaign is current, reason and
/// session reach the change log, a created or renamed campaign is announced with resources/list_changed, and an argument
/// the chosen action does not take is refused with what it does take.
///
/// <para>
/// Why it fails silently: a create that left the previous campaign current would file the model's next write into the
/// wrong campaign without any error; an undo call without the campaign fails only once the model has moved on to another
/// campaign; a missing list_changed leaves the new campaign out of Claude Code's <c>@</c> menu until a restart; and an
/// ignored argument (a perspective on create, a reason on update) lets the model believe something happened that did not.
/// </para>
/// </summary>
public sealed partial class CampaignToolTests : IAsyncLifetime
{
    private const string Accepted =
        "campaign accepts: action (string, required), campaign (string, optional), name (string, optional), role (string, optional), " +
        "ruleset (string, optional), dm_name (string, optional), slug (string, optional), settings (object, optional), " +
        "party_name (string, optional), my_character (string, optional), status (string, optional), summary_md (string, optional), " +
        "current_location (string, optional), current_ingame (string, optional), perspective (string, optional), reason (string, optional), " +
        "session (integer, optional), dry_run (boolean, optional).";

    private CampaignTestServer _s = null!;

    public async Task InitializeAsync() => _s = await CampaignTestServer.StartAsync();

    public async Task DisposeAsync() => await _s.DisposeAsync();

    [GeneratedRegex("Batch `([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})`\\. To undo it: campaign_history \\{\"action\": \"undo\", \"batch_id\": \"\\1\", \"campaign\": \"([a-z0-9-]+)\"\\}\\.")]
    private static partial Regex BatchLineRegex();

    // The campaign_history call a result prints, as the model would copy it.
    [GeneratedRegex("campaign_history (\\{[^}]*\\})")]
    private static partial Regex HistoryCallRegex();

    [Fact]
    public async Task List_NoDatabase_SaysHowToCreateOneAndCreatesNoFile()
    {
        var text = await _s.Call("campaign", """{"action":"list"}""");

        Assert.Equal(
            "# Campaigns\n\nNo campaigns yet. Create one: campaign {\"action\": \"create\", \"name\": \"…\", \"role\": \"player\" or \"dm\", " +
            "\"ruleset\": \"2014\", \"2024\" or \"mixed\"}.\n",
            text);
        Assert.False(File.Exists(_s.DatabasePath), "listing campaigns created campaigns.db");
    }

    [Theory]
    [InlineData("""{"action":"summary"}""")]
    [InlineData("""{"action":"get"}""")]
    [InlineData("""{"action":"use","campaign":"sky"}""")]
    [InlineData("""{"action":"update","name":"X"}""")]
    public async Task Action_NoCampaignsYet_SaysHowToCreateOneAndCreatesNoFile(string arguments)
    {
        var text = await _s.Error("campaign", arguments);

        Assert.Equal(
            "An error occurred invoking 'campaign': There are no campaigns yet. Create one with campaign {\"action\": \"create\", " +
            "\"name\": \"…\", \"role\": \"player\" or \"dm\", \"ruleset\": \"2024\"}.",
            text);
        Assert.False(File.Exists(_s.DatabasePath), "a refused read created campaigns.db");
    }

    [Fact]
    public async Task Create_PlayerCampaign_WritesThePartyAndTheCharacterAndPrintsTheWholeBatchId()
    {
        var text = await _s.Call("campaign",
            """{"action":"create","name":"Sky World","role":"player","ruleset":"2014","dm_name":"Sam","my_character":"Belmakor Silverwind","reason":"new game"}""");

        Assert.StartsWith(
            "# Created campaign Sky World (`sky-world`)\n\nplayer campaign · 2014 rules · DM: Sam\n" +
            "- **Party:** `faction:the-party` (party knowledge and membership hang on it)\n" +
            "- **My character:** `character:belmakor-silverwind`, a member of the party\n\n" +
            "It is now the current campaign: campaign tools use it when campaign is omitted.\n\nBatch `",
            text, StringComparison.Ordinal);
        var batch = BatchLineRegex().Match(text);
        Assert.True(batch.Success, text);
        Assert.Equal("sky-world", batch.Groups[2].Value);
        Assert.Contains("Resources: `campaign://sky-world/summary`, `campaign://sky-world/threads`", text, StringComparison.Ordinal);

        var history = await _s.Call("campaign_history", $$"""{"action":"batch","batch_id":"{{batch.Groups[1].Value}}"}""");
        Assert.Contains("campaign/create", history, StringComparison.Ordinal);
        Assert.Contains("reason: new game", history, StringComparison.Ordinal);
        Assert.Contains("character:belmakor-silverwind member_of faction:the-party created", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_MakesItThisProcesssCurrentAndThePersistedActiveCampaign()
    {
        var first = await _s.Create("First");
        var second = await _s.Create("Second");

        Assert.Equal(second.Id, _s.Campaigns.CurrentCampaignId);
        Assert.Equal(second.Id, new CampaignStore(_s.Database).ActiveCampaignId());
        Assert.StartsWith("# Second (`second`)\n", await _s.Call("campaign", """{"action":"get"}"""), StringComparison.Ordinal);
        Assert.Contains("| `second` | Second | dm | — | 2024 | active | calls use this one |", await _s.Call("campaign", """{"action":"list"}"""),
            StringComparison.Ordinal);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task Create_AnotherProcessChoosesOtherwiseLater_ThisProcessKeepsItsOwnCampaign()
    {
        var mine = await _s.Create("Mine");
        await _s.Create("Theirs");
        await _s.Call("campaign", """{"action":"use","campaign":"mine"}""");
        using var otherProcess = new CampaignService(new DndMcpServerOptions { DataDirectory = _s.Server.DataDirectory },
            NullLogger<CampaignService>.Instance, NullLogger<CampaignDatabase>.Instance);

        otherProcess.Use("theirs");

        Assert.StartsWith("# Mine (`mine`)\n", await _s.Call("campaign", """{"action":"get"}"""), StringComparison.Ordinal);
        Assert.Equal(mine.Id, _s.Campaigns.Resolve(null).Id);
    }

    [Fact]
    public async Task Use_SwitchesThisProcessAndThePersistedActiveCampaignAndSaysTheEditionDefault()
    {
        var first = await _s.Create("First", ruleset: "2014");
        await _s.Create("Second");

        var text = await _s.Call("campaign", """{"action":"use","campaign":"first"}""");

        Assert.Equal(
            "# Using campaign First (`first`)\n\ndm campaign · 2014 rules · active\n\nCampaign tools now use it when campaign is omitted, " +
            "in this session and in sessions started later; tools that take an edition default to its 2014 rules.\n" +
            "campaign {\"action\": \"summary\", \"campaign\": \"first\"} shows where things stand.\n",
            text);
        Assert.Equal(first.Id, _s.Campaigns.CurrentCampaignId);
        Assert.Equal(first.Id, new CampaignStore(_s.Database).ActiveCampaignId());
    }

    /// <summary>
    /// The summary call use prints names the campaign, so sent as printed after another use it still reads the campaign it
    /// was printed for, not the one current by then.
    /// </summary>
    [Fact]
    public async Task Use_TheSummaryCallAsPrinted_ReadsThatCampaignAfterAnotherUse()
    {
        await _s.Create("First");
        await _s.Create("Second");
        var used = await _s.Call("campaign", """{"action":"use","campaign":"first"}""");
        await _s.Call("campaign", """{"action":"use","campaign":"second"}""");

        var printed = Regex.Match(used, "campaign (\\{[^}]*\\}) shows where things stand").Groups[1].Value;
        var summary = await _s.Call("campaign", printed);

        Assert.Equal("{\"action\": \"summary\", \"campaign\": \"first\"}", printed);
        Assert.StartsWith("# First", summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cap notes of campaign get and of the write results name the campaign, and the batch when there is one, like every
    /// call a campaign result prints: get points at the same campaign's summary for the same view; a create or update at
    /// its own batch; a result with no batch (a dry run of an update, an update that changed nothing, use) at the campaign's
    /// record; a dry-run create (no campaign exists yet) at running it for real. A note naming neither read or changed
    /// whatever campaign was current, and "campaign_history batch" with no batch id had nothing to list.
    /// </summary>
    [Theory]
    [InlineData("get", "use campaign {\"action\": \"summary\", \"campaign\": \"big\"} for the state of play")]
    [InlineData("get party", "use campaign {\"action\": \"summary\", \"perspective\": \"party\", \"campaign\": \"big\"} for the state of play")]
    [InlineData("create", "campaign_history {\"action\": \"batch\", \"batch_id\": \"0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000\", \"campaign\": \"big\"} lists what the batch changed")]
    [InlineData("create dry run", "nothing was written; run the call without dry_run to create it")]
    [InlineData("update", "campaign_history {\"action\": \"batch\", \"batch_id\": \"0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000\", \"campaign\": \"big\"} lists what the batch changed")]
    [InlineData("update dry run", "campaign {\"action\": \"get\", \"campaign\": \"big\"} shows the campaign")]
    [InlineData("update unchanged", "campaign {\"action\": \"get\", \"campaign\": \"big\"} shows the campaign")]
    [InlineData("use", "campaign {\"action\": \"get\", \"campaign\": \"big\"} shows the campaign")]
    public void Format_ResultPastTheCap_PointsAtItsOwnCampaignAndBatch(string result, string hint)
    {
        const string Batch = "0199aaaa-bbbb-7ccc-8ddd-eeeeffff0000";
        // A name past the cap is the one field every one of these prints whole.
        var campaign = new CampaignRow("id-big", "big", new string('n', 30_000), "dm", "2024", "active", null, null, null, null, null, "{}",
            string.Empty, "2026-01-01T00:00:00.000Z", "2026-01-01T00:00:00.000Z");
        var links = new Repository.Campaign.Read.CampaignSummaryView("big", campaign.Name, "dm", "2024", "active", null, null, null, null, [], null,
            null, null, null, [], 0, 0, [], null);
        var party = new Formatting.Campaign.CampaignView(Perspective.Parse("party"), false,
            "_Perspective: party. Names are the ones this view knows; author-only text is withheld._");
        var text = result switch
        {
            "get" => Formatting.Campaign.CampaignMarkdown.Get(campaign, links, Formatting.Campaign.CampaignView.Author, isDefault: true),
            "get party" => Formatting.Campaign.CampaignMarkdown.Get(campaign, links, party, isDefault: true),
            "create" => Formatting.Campaign.CampaignMarkdown.Created(new CampaignCreateResult(campaign, Batch, false, "faction:the-party", null, [])),
            "create dry run" => Formatting.Campaign.CampaignMarkdown.Created(new CampaignCreateResult(campaign, null, true, "faction:the-party", null, [])),
            "update" => Formatting.Campaign.CampaignMarkdown.Updated(new CampaignUpdateResult(campaign, Batch, false, ["name"], [])),
            "update dry run" => Formatting.Campaign.CampaignMarkdown.Updated(new CampaignUpdateResult(campaign, null, true, ["name"], [])),
            "update unchanged" => Formatting.Campaign.CampaignMarkdown.Updated(new CampaignUpdateResult(campaign, null, false, [], [])),
            _ => Formatting.Campaign.CampaignMarkdown.Used(campaign),
        };

        Assert.True(text.Length <= Formatting.Campaign.CampaignMarkdownText.MaxChars, $"{text.Length} characters.");
        Assert.EndsWith($"_Output cut at 24,000 characters; {hint}._", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Use_UnknownCampaign_ListsTheCampaigns()
    {
        await _s.Create("First");

        var text = await _s.Error("campaign", """{"action":"use","campaign":"nope"}""");

        Assert.Equal("An error occurred invoking 'campaign': No campaign \"nope\". Campaigns: first (dm, 2024). Pass one of those slugs.", text);
    }

    [Fact]
    public async Task Create_ThenListChanged_IsSentForTheNewCampaign()
    {
        await _s.Create("Sky");

        await _s.FirstListChanged();
        Assert.Equal(1, _s.ListChangedCount);
    }

    [Fact]
    public async Task Create_DryRun_WritesNothingSendsNothingAndKeepsTheCurrentCampaign()
    {
        var existing = await _s.Create("Existing");
        await _s.FirstListChanged();

        var text = await _s.Call("campaign", """{"action":"create","name":"Maybe","role":"dm","ruleset":"2024","dry_run":true}""");
        await _s.Call("campaign", """{"action":"list"}""");

        Assert.StartsWith("# Dry run: would create campaign Maybe (`maybe`)\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\nNothing was written. Run the same call without dry_run to create it.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Batch `", text, StringComparison.Ordinal);
        Assert.Null(new CampaignStore(_s.Database).TryGet("maybe"));
        Assert.Equal(existing.Id, _s.Campaigns.CurrentCampaignId);
        Assert.Equal(1, _s.ListChangedCount);
    }

    [Fact]
    public async Task Update_Rename_KeepsTheSlugAnnouncesItAndUndoPutsTheNameBack()
    {
        await _s.Create("Sky");
        await _s.FirstListChanged();

        var text = await _s.Call("campaign", """{"action":"update","name":"Sky World","reason":"better name"}""");

        Assert.StartsWith(
            "# Updated campaign Sky World (`sky`)\n\nChanged: name.\nThe slug stays `sky` (slugs never change, so handles and resource URIs keep working).\n",
            text, StringComparison.Ordinal);
        var batch = BatchLineRegex().Match(text);
        Assert.True(batch.Success, text);
        Assert.Equal("sky", batch.Groups[2].Value);
        await _s.WaitForListChanged(2);

        var undo = await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{batch.Groups[1].Value}}"}""");

        Assert.Contains("## The batch it reversed\n", undo, StringComparison.Ordinal);
        Assert.Contains("- campaign sky name: Sky → \"Sky World\"\n", undo, StringComparison.Ordinal);
        Assert.Equal("Sky", new CampaignStore(_s.Database).TryGet("sky")!.Name);
        await _s.WaitForListChanged(3);
    }

    [Fact]
    public async Task Update_SettingsAndPlace_ChangesOnlyThoseAndSendsNoListChanged()
    {
        var campaign = await _s.Create("Sky");
        _s.Apply(campaign, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Flotsam" });
        await _s.FirstListChanged();

        var text = await _s.Call("campaign",
            """{"action":"update","settings":{"effective_level_offset":1,"default_visibility":"party"},"current_location":"location:flotsam","current_ingame":"3rd of Frostmoon"}""");
        await _s.Call("campaign", """{"action":"list"}""");

        // The changed fields are named as the arguments that changed them, not as the columns (current_location_id).
        Assert.StartsWith(
            "# Updated campaign Sky (`sky`)\n\nChanged: current_ingame, current_location, settings.effective_level_offset, settings.default_visibility.\n",
            text, StringComparison.Ordinal);

        var get = await _s.Call("campaign", """{"action":"get"}""");
        Assert.Contains("- **Where:** Flotsam (`location:flotsam`)\n", get, StringComparison.Ordinal);
        Assert.Contains("- **In-game date:** 3rd of Frostmoon\n", get, StringComparison.Ordinal);
        Assert.Contains("- **Settings:** `{\"effective_level_offset\":1,\"default_visibility\":\"party\"}`\n", get, StringComparison.Ordinal);
        Assert.Equal(1, _s.ListChangedCount);
    }

    [Fact]
    public async Task Update_DryRun_ReportsTheChangeAndWritesNothing()
    {
        await _s.Create("Sky");

        var text = await _s.Call("campaign", """{"action":"update","status":"hiatus","dry_run":true}""");

        Assert.Equal(
            "# Dry run: would update campaign Sky (`sky`)\n\nChanged: status.\n\nNothing was written. Run the same call without dry_run to apply it.\n",
            text);
        Assert.Equal("active", new CampaignStore(_s.Database).TryGet("sky")!.Status);
    }

    [Fact]
    public async Task Update_EmptyText_ClearsTheDmNameAndTheInGameDate()
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky","role":"player","ruleset":"2024","dm_name":"Sam"}""");
        await _s.Call("campaign", """{"action":"update","current_ingame":"Day 3"}""");

        var text = await _s.Call("campaign", """{"action":"update","dm_name":"","current_ingame":""}""");

        Assert.StartsWith("# Updated campaign Sky (`sky`)\n\nChanged: dm_name, current_ingame.\n", text, StringComparison.Ordinal);
        var row = new CampaignStore(_s.Database).TryGet("sky")!;
        Assert.Equal((null, null), (row.DmName, row.CurrentIngame));
    }

    /// <summary>
    /// "" clears a field on its own (the descriptions say so): an update whose only change is "" for one clearable field is
    /// that change, not "nothing to change". Kills A01, A02 and A03 (FH10, M08): only dm_name's clear was pinned.
    /// </summary>
    [Theory]
    [InlineData("dm_name")]
    [InlineData("summary_md")]
    [InlineData("current_location")]
    [InlineData("current_ingame")]
    public async Task Update_OnlyAnEmptyValue_ClearsThatField(string field)
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky","role":"player","ruleset":"2024","dm_name":"Sam"}""");
        await _s.Call("campaign_write", """{"campaign":"sky","ops":[{"op":"upsert","kind":"location","name":"Flotsam"}]}""");
        await _s.Call("campaign", """{"action":"update","summary_md":"Old summary","current_location":"location:flotsam","current_ingame":"3rd of Frostmoon"}""");

        var text = await _s.Call("campaign", $$"""{"action":"update","{{field}}":""}""");

        Assert.StartsWith($"# Updated campaign Sky (`sky`)\n\nChanged: {field}.\n", text, StringComparison.Ordinal);
        var row = new CampaignStore(_s.Database).TryGet("sky")!;
        Assert.Null(field switch
        {
            "dm_name" => row.DmName,
            "summary_md" => string.IsNullOrEmpty(row.SummaryMd) ? null : row.SummaryMd,
            "current_location" => row.CurrentLocationId,
            _ => row.CurrentIngame,
        });
    }

    /// <summary>
    /// Kills A04 (FH10, M09): one campaign that nobody chose (made by another process, so neither this process's current
    /// campaign nor the persisted active one is set) is the one calls without campaign use (§3.7: the only campaign), so the
    /// list marks it and get says it is the default.
    /// </summary>
    [Fact]
    public async Task ListAndGet_OnlyCampaignNobodyChose_IsMarkedAsTheOneCallsUse()
    {
        _s.Campaigns.Store.Create("Only World", "dm", "2024", slug: "only");
        Assert.Null(_s.Campaigns.CurrentCampaignId);

        var list = await _s.Call("campaign", """{"action":"list"}""");
        var get = await _s.Call("campaign", """{"action":"get"}""");

        Assert.Contains("| `only` | Only World | dm | — | 2024 | active | calls use this one |", list, StringComparison.Ordinal);
        Assert.Contains("Calls without campaign use the marked one", list, StringComparison.Ordinal);
        Assert.Contains("- **Default for calls without campaign:** yes", get, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills S05 (FH10, M17): using a mixed campaign says nothing about an edition default, since the edition tools fall back
    /// to 2024 for a mixed campaign; claiming "tools that take an edition default to its mixed rules" would be false.
    /// </summary>
    [Fact]
    public async Task Use_MixedCampaign_ClaimsNoEditionDefault()
    {
        await _s.Call("campaign", """{"action":"create","name":"Mixed World","role":"dm","ruleset":"mixed","slug":"mix"}""");
        await _s.Call("campaign", """{"action":"create","name":"Other World","role":"dm","ruleset":"2014","slug":"other"}""");

        var text = await _s.Call("campaign", """{"action":"use","campaign":"mix"}""");

        Assert.Equal(
            "# Using campaign Mixed World (`mix`)\n\ndm campaign · mixed rules · active\n\nCampaign tools now use it when campaign is omitted, " +
            "in this session and in sessions started later.\ncampaign {\"action\": \"summary\", \"campaign\": \"mix\"} shows where things stand.\n",
            text);
    }

    /// <summary>
    /// Kills A07 (FH10, M23): a dry-run rename writes nothing, so the resource list did not change and no list_changed is
    /// sent; the real rename after it is the only notification besides create's. WaitForListChanged waits out a quiet
    /// period, so a late extra notification from the dry run is counted.
    /// </summary>
    [Fact]
    public async Task Update_DryRunRename_SendsNoListChanged()
    {
        await _s.Create("Sky");
        await _s.WaitForListChanged(1);

        await _s.Call("campaign", """{"action":"update","name":"Sky World","dry_run":true}""");
        await _s.WaitForListChanged(1);

        await _s.Call("campaign", """{"action":"update","name":"Sky World"}""");
        await _s.WaitForListChanged(2);
    }

    [Fact]
    public async Task Update_NothingToChange_IsRefusedNamingWhatCanChange()
    {
        await _s.Create("Sky");

        var text = await _s.Error("campaign", """{"action":"update","reason":"why not"}""");

        Assert.Equal(
            "An error occurred invoking 'campaign': Invalid campaign call: action \"update\" needs at least one of name, status, ruleset, dm_name, " +
            "settings, summary_md, current_location, current_ingame, my_character to change. Example: {\"action\": \"update\", \"current_ingame\": " +
            "\"3rd of Frostmoon\", \"campaign\": \"belmakor\"}",
            text);
    }

    [Theory]
    [InlineData("""{"action":"list","name":"X"}""", "action \"list\" does not take name; list takes no other arguments.")]
    [InlineData("""{"action":"create","name":"X","role":"dm","ruleset":"2024","perspective":"party"}""",
        "action \"create\" does not take perspective; create takes name, role, ruleset, dm_name, slug, settings, party_name, my_character, reason, dry_run.")]
    [InlineData("""{"action":"summary","dry_run":true}""", "action \"summary\" does not take dry_run; summary takes campaign, perspective.")]
    [InlineData("""{"action":"use","campaign":"x","session":3}""", "action \"use\" does not take session; use takes campaign.")]
    [InlineData("""{"action":"get","status":"ended","summary_md":"x"}""", "action \"get\" does not take status, summary_md; get takes campaign, perspective.")]
    [InlineData("""{"action":"list","campaign":"sky"}""", "action \"list\" does not take campaign; list takes no other arguments.")]
    [InlineData("""{"action":"create","name":"X","role":"dm","ruleset":"2024","campaign":"sky"}""",
        "action \"create\" does not take campaign; create takes name, role, ruleset, dm_name, slug, settings, party_name, my_character, reason, dry_run.")]
    public async Task Action_ArgumentItDoesNotTake_IsRefusedWithWhatItTakes(string arguments, string problem)
    {
        var text = await _s.Error("campaign", arguments);

        Assert.Equal("An error occurred invoking 'campaign': Invalid campaign call: " + problem, text);
    }

    [Theory]
    [InlineData("""{"action":"create","name":"X"}""", "action \"create\" needs role, ruleset.")]
    [InlineData("""{"action":"create","role":"dm","ruleset":"2024","name":"  "}""", "action \"create\" needs name.")]
    [InlineData("""{"action":"use"}""", "action \"use\" needs campaign.")]
    public async Task Action_MissingRequiredArgument_NamesItAndGivesAnExample(string arguments, string problem)
    {
        var text = await _s.Error("campaign", arguments);

        Assert.StartsWith("An error occurred invoking 'campaign': Invalid campaign call: " + problem + " Example: {\"action\": ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Action_ExtraAndMissingTogether_AreReportedInOneError()
    {
        var text = await _s.Error("campaign", """{"action":"create","name":"X","perspective":"party"}""");

        Assert.StartsWith("An error occurred invoking 'campaign': Invalid campaign call (2 problems):\n- action \"create\" does not take perspective; ",
            text, StringComparison.Ordinal);
        Assert.Contains("\n- action \"create\" needs role, ruleset. Example: ", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("")]
    public async Task Action_Unknown_ListsTheActions(string action)
    {
        var text = await _s.Error("campaign", $$"""{"action":"{{action}}"}""");

        Assert.StartsWith(
            $"An error occurred invoking 'campaign': Invalid campaign call: action \"{action}\" is not one of list, summary, get, create, update, use. Example: ",
            text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LIST")]
    [InlineData(" list ")]
    public async Task Action_ForgivingSpelling_IsAccepted(string action)
    {
        Assert.StartsWith("# Campaigns", await _s.Call("campaign", $$"""{"action":"{{action}}"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_BadRoleAndRuleset_ReportsBothAndWritesNothing()
    {
        var text = await _s.Error("campaign", """{"action":"create","name":"X","role":"gm","ruleset":"5e"}""");

        Assert.Equal(
            "An error occurred invoking 'campaign': Invalid campaign (2 problems):\n- role \"gm\" is not a role; give player (you play in it) or dm (you run it).\n" +
            "- ruleset \"5e\" is not a ruleset; give 2014, 2024 or mixed.",
            text);
        Assert.Empty(new CampaignStore(_s.Database).List());
    }

    [Fact]
    public async Task Create_DmCampaignWithMyCharacter_IsRefusedAndWritesNothing()
    {
        var text = await _s.Error("campaign", """{"action":"create","name":"X","role":"dm","ruleset":"2024","my_character":"Bel"}""");

        Assert.Equal(
            "An error occurred invoking 'campaign': Invalid campaign: my_character is for a player campaign (your own character); in a DM " +
            "campaign add the PCs with campaign_write.",
            text);
        Assert.Empty(new CampaignStore(_s.Database).List());
    }

    [Fact]
    public async Task CallTool_MissingAction_GuardNamesItAndListsTheArguments()
    {
        var text = await _s.Error("campaign", "{}");

        Assert.Equal("An error occurred invoking 'campaign': Invalid arguments: missing required argument 'action'. " + Accepted, text);
    }

    [Fact]
    public async Task CallTool_NullForEveryOptionalArgument_MeansTheDefault()
    {
        await _s.Create("Sky");

        var text = await _s.Call("campaign",
            """{"action":"summary","campaign":null,"name":null,"role":null,"ruleset":null,"dm_name":null,"slug":null,"settings":null,"party_name":null""" +
            ""","my_character":null,"status":null,"summary_md":null,"current_location":null,"current_ingame":null,"perspective":null,"reason":null""" +
            ""","session":null,"dry_run":null}""");

        Assert.StartsWith("# Sky (`sky`)\n\ndm campaign · 2024 rules · active\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ManyCampaigns_StaysUnderTheCapAndSaysItWasCut()
    {
        var store = new CampaignStore(_s.Database);
        for (var i = 0; i < 260; i++)
        {
            store.Create($"A campaign with a long name to fill the table quickly, number {i:D3}", "dm", "2024");
        }

        var text = await _s.Call("campaign", """{"action":"list"}""");

        Assert.True(text.Length <= 24_000, $"campaign list is {text.Length} characters");
        Assert.EndsWith("_Output cut at 24,000 characters; the list is long; pass campaign to the other tools directly._", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH5 (U03): campaign list reads only what it prints, the campaign rows and each player campaign's character handle,
    /// so a damaged table it does not print (relation, which holds party membership) leaves it working: list is the call a
    /// user turns to when something is wrong. It first built each player campaign's whole author summary to find the handle,
    /// reading every entity, relation, session and clock, so any damaged page there failed the list.
    /// </summary>
    [Fact]
    public async Task List_DamagedTableTheListDoesNotPrint_StillListsEveryCampaignWithItsCharacter()
    {
        await _s.Call("campaign", """{"action":"create","name":"Belmakor","role":"player","ruleset":"2014","slug":"belmakor","my_character":"Belmakor"}""");
        await _s.Call("campaign", """{"action":"create","name":"One Piece","role":"dm","ruleset":"2024","slug":"one-piece"}""");
        DamagedCampaignsDatabaseServer.ScrambleRootPage(_s.DatabasePath, "relation");

        var text = await _s.Call("campaign", """{"action":"list"}""");

        Assert.Contains("\n| `belmakor` | Belmakor | player | `character:belmakor` | 2014 |", text, StringComparison.Ordinal);
        Assert.Contains("\n| `one-piece` | One Piece | dm | — | 2024 |", text, StringComparison.Ordinal);
        Assert.StartsWith("An error occurred invoking 'campaign': campaigns.db at ",
            await _s.Error("campaign", """{"action":"summary","campaign":"belmakor"}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_LargeParty_ListsThirtyMembersAndSaysHowManyMore()
    {
        var campaign = await _s.Create("Big");
        for (var batch = 0; batch < 3; batch++)
        {
            var ops = new List<CampaignOpSpec>();
            for (var i = 0; i < 25; i++)
            {
                var name = $"Adventurer {batch * 25 + i:D2} of the very long roll of heroes who answered the call";
                ops.Add(new CampaignOpSpec { Op = "upsert", Kind = "character", Name = name, Visibility = "party" });
                ops.Add(new CampaignOpSpec { Op = "link", From = "character:" + CampaignSlugs.From(name, "character"), Rel = "member_of", To = "faction:the-party" });
            }

            _s.Apply(campaign, WriteContext.Default, [.. ops]);
        }

        var text = await _s.Call("campaign", """{"action":"summary"}""");

        var partyLine = text.Split('\n').Single(l => l.StartsWith("- **Party:** ", StringComparison.Ordinal));
        Assert.Equal(30, Regex.Matches(partyLine, "`character:adventurer-").Count);
        Assert.EndsWith(", … and 45 more (campaign_search with kinds [\"character\"] lists them)", partyLine, StringComparison.Ordinal);
        Assert.Contains("## Open quests and threads", text, StringComparison.Ordinal);
        Assert.True(text.Length <= 24_000, $"summary is {text.Length} characters");
    }

    [Fact]
    public async Task Get_AuthorView_ShowsTheRecordAndTheResources()
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky","role":"player","ruleset":"2014","my_character":"Bel","settings":{"table_style":"near-TPK"}}""");
        await _s.Call("campaign", """{"action":"update","summary_md":"A sky world of floating islands.","current_ingame":"Day 3"}""");

        var text = await _s.Call("campaign", """{"action":"get"}""");

        Assert.StartsWith(
            "# Sky (`sky`)\n\n- **Role:** player (you play in it)\n- **Ruleset:** 2014 · **Status:** active\n- **Party:** The Party (`faction:the-party`)\n" +
            "- **My character:** Bel (`character:bel`)\n- **In-game date:** Day 3\n- **Settings:** `{\"table_style\":\"near-TPK\"}`\n- **Created:** ",
            text, StringComparison.Ordinal);
        Assert.Contains("- **Default for calls without campaign:** yes\n\n## Summary\nA sky world of floating islands.\n", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "Resources: `campaign://sky/summary`, `campaign://sky/threads`, `campaign://sky/party`; also readable by URI: `campaign://sky/entity/<ref>`, " +
            "`campaign://sky/session/<n>`, `campaign://sky/knowledge/<perspective>`.\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_OfAnotherCampaign_TheUndoAsPrintedWorksWhileTheOtherIsCurrent()
    {
        await _s.Create("Alpha");
        await _s.Create("Beta");

        var text = await _s.Call("campaign", """{"action":"update","campaign":"alpha","current_ingame":"Day 9"}""");
        var undo = await _s.Call("campaign_history", HistoryCalls(text).Single());

        Assert.StartsWith("# Undo of batch ", undo, StringComparison.Ordinal);
        Assert.Null(new CampaignStore(_s.Database).TryGet("alpha")!.CurrentIngame);
        Assert.Equal("beta", _s.Campaigns.Resolve(null).Slug);
    }

    [Fact]
    public async Task Create_ThenAnotherCampaign_TheUndoAsPrintedRemovesItAndSaysThereIsNoRedo()
    {
        var created = await _s.Call("campaign", """{"action":"create","name":"Short-lived","role":"dm","ruleset":"2024"}""");
        await _s.Create("Keep");

        var undo = await _s.Call("campaign_history", HistoryCalls(created).First());

        Assert.Null(new CampaignStore(_s.Database).TryGet("short-lived"));
        Assert.Contains(
            "\nThat batch created the campaign short-lived itself, so the campaign is gone and there is no campaign left to redo it in. To have " +
            "it back, create it again with campaign {\"action\": \"create\"}.\n",
            undo, StringComparison.Ordinal);
        Assert.DoesNotContain("(redo)", undo, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_ReasonAndSession_AreKeptWithItsBatch()
    {
        var sky = await _s.Create("Sky");
        new SessionWriter(_s.Database).RecordPast(sky, 1, "One");

        var text = await _s.Call("campaign", """{"action":"update","current_ingame":"Day 2","reason":"after the fight","session":1}""");
        var batch = await _s.Call("campaign_history", $$"""{"action":"batch","batch_id":"{{BatchLineRegex().Match(text).Groups[1].Value}}"}""");

        Assert.Matches(new Regex("^# Batch \\S+\n\n\\S+ · campaign/update · S1\nbatch `\\S+` · by claude\nreason: after the fight\n"), batch);
    }

    [Fact]
    public async Task Update_SessionThatDoesNotExist_IsRefusedAndWritesNothing()
    {
        await _s.Create("Sky");

        var text = await _s.Error("campaign", """{"action":"update","current_ingame":"Day 2","session":42}""");

        Assert.StartsWith("An error occurred invoking 'campaign': session 42: no such session in this campaign.", text, StringComparison.Ordinal);
        Assert.Null(new CampaignStore(_s.Database).TryGet("sky")!.CurrentIngame);
    }

    [Fact]
    public async Task Update_MyCharacter_IsNamedAsTheArgument()
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky","role":"player","ruleset":"2024","my_character":"Bel"}""");
        _s.Apply(new CampaignStore(_s.Database).TryGet("sky")!, WriteContext.Default,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Ria", Visibility = "party" });

        var text = await _s.Call("campaign", """{"action":"update","my_character":"character:ria"}""");

        Assert.StartsWith("# Updated campaign Sky (`sky`)\n\nChanged: my_character.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_SavingItAsTheActiveCampaignFails_ReportsTheCampaignMakesItCurrentHereAndAnnouncesIt()
    {
        var first = await _s.Create("First");
        await _s.FirstListChanged();
        BlockAppStateWrites(_s.DatabasePath);

        var text = await _s.Call("campaign", """{"action":"create","name":"Second","role":"dm","ruleset":"2024"}""");

        Assert.StartsWith("# Created campaign Second (`second`)\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "\nIt is now the current campaign in this session: campaign tools use it when campaign is omitted. It could not be saved as the " +
            "campaign later sessions start with; campaign {\"action\": \"use\", \"campaign\": \"second\"} saves it.\n",
            text, StringComparison.Ordinal);
        Assert.Matches(BatchLineRegex(), text);
        var second = new CampaignStore(_s.Database).TryGet("second")!;
        Assert.Equal(second.Id, _s.Campaigns.CurrentCampaignId);
        Assert.Equal(first.Id, new CampaignStore(_s.Database).ActiveCampaignId());
        await _s.WaitForListChanged(2);
        Assert.Contains(_s.Server.ServerLog.Entries, e => e.Level == LogLevel.Warning &&
                                                         e.Message.Contains("could not save it as the active campaign", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_SlugTaken_SaysWhichSlugTheNewCampaignGot()
    {
        await _s.Create("Sky");

        var text = await _s.Call("campaign", """{"action":"create","name":"Sky","role":"dm","ruleset":"2024"}""");

        Assert.StartsWith("# Created campaign Sky (`sky-2`)\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Warnings\n- campaign slug sky is taken, so this campaign is sky-2.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_ACampaignCallsDoNotDefaultTo_SaysSo()
    {
        await _s.Create("First");
        await _s.Create("Second");

        var first = await _s.Call("campaign", """{"action":"get","campaign":"first"}""");
        var second = await _s.Call("campaign", """{"action":"get","campaign":"second"}""");

        Assert.Contains("\n- **Default for calls without campaign:** no\n", first, StringComparison.Ordinal);
        Assert.Contains("\n- **Default for calls without campaign:** yes\n", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("public")]
    [InlineData("character:bel")]
    public async Task Read_CurrentLocationTheViewCannotSee_IsLeftOut(string perspective)
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky","role":"player","ruleset":"2024","my_character":"Bel"}""");
        _s.Apply(new CampaignStore(_s.Database).TryGet("sky")!, WriteContext.Default,
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Hidden Lair", Visibility = "author" });
        await _s.Call("campaign", """{"action":"update","current_location":"location:hidden-lair"}""");

        var author = await _s.Call("campaign", """{"action":"get"}""");
        Assert.Contains("\n- **Where:** Hidden Lair (`location:hidden-lair`)\n", author, StringComparison.Ordinal);
        foreach (var action in new[] { "get", "summary" })
        {
            var text = await _s.Call("campaign", $$"""{"action":"{{action}}","perspective":"{{perspective}}"}""");

            BelmakorServer.AssertClean(text, ["Hidden Lair", "hidden-lair", "Where"], $"campaign {action} as {perspective}");
        }
    }

    [Fact]
    public async Task Summary_NoSessionPlayed_SaysSoAndTheHistoryCallAsPrintedListsEveryChange()
    {
        await _s.Create("Alpha");
        await _s.Call("campaign", """{"action":"update","current_ingame":"Day 9"}""");
        var summary = await _s.Call("campaign", """{"action":"summary"}""");
        await _s.Create("Beta");

        var history = await _s.Call("campaign_history", HistoryCalls(summary).Last());

        Assert.Contains("\n- **Sessions:** none played yet\n", summary, StringComparison.Ordinal);
        Assert.Contains("\n2 batches since the campaign began; the newest:\n", summary, StringComparison.Ordinal);
        Assert.EndsWith("\ncampaign_history {\"action\": \"since\", \"campaign\": \"alpha\"} lists every change.\n", summary, StringComparison.Ordinal);
        Assert.StartsWith("# History of alpha\n\n2 batches (one tool call that wrote is one batch).\n", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_AfterASession_TheHistoryCallAsPrintedStartsAtThatSession()
    {
        var alpha = await _s.Create("Alpha");
        new SessionWriter(_s.Database).RecordPast(alpha, 1, "One", recapMd: "It began.");
        _s.Apply(alpha, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Prep NPC" });
        var summary = await _s.Call("campaign", """{"action":"summary"}""");
        await _s.Create("Beta");

        var history = await _s.Call("campaign_history", HistoryCalls(summary).Last());

        Assert.Contains("\n1 batch since session 1 ended; the newest:\n", summary, StringComparison.Ordinal);
        Assert.EndsWith(
            "\ncampaign_history {\"action\": \"since\", \"session\": 1, \"campaign\": \"alpha\"} lists every change from session 1 on, that " +
            "session's own included.\n",
            summary, StringComparison.Ordinal);
        Assert.StartsWith("# History of alpha since session 1\n\n2 batches (one tool call that wrote is one batch).\n", history, StringComparison.Ordinal);
        Assert.Contains("character:prep-npc created", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_Largest_IsCutAtTheCapAndSaysSo()
    {
        var big = await _s.Create("Big");
        static string Long(string what, int i) => $"{what} {i:D2} " + string.Join(' ', Enumerable.Repeat("of the long and winding road", 5));
        _s.Apply(big, WriteContext.Default, Enumerable.Range(1, 35).Select(i => new CampaignOpSpec
        {
            Op = "upsert", Kind = "clock", Name = Long("Clock", i), Visibility = "party", Clock = new ClockSpec { Segments = 8, Filled = 1, Unit = "day" },
        }).ToArray());
        _s.Apply(big, WriteContext.Default, Enumerable.Range(1, 35).Select(i => new CampaignOpSpec
        {
            Op = "upsert", Kind = "secret", Name = Long("Secret", i),
        }).ToArray());
        _s.Apply(big, WriteContext.Default, Enumerable.Range(1, 35).Select(i => new CampaignOpSpec
        {
            Op = "upsert", Kind = "character", Name = Long("Invented", i), CanonStatus = "proposed",
        }).ToArray());
        foreach (var chunk in Enumerable.Range(1, 35).Chunk(20))
        {
            _s.Apply(big, WriteContext.Default, chunk.SelectMany(i => new[]
            {
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = Long("Member", i), Visibility = "party" },
                new CampaignOpSpec { Op = "link", From = "character:" + CampaignSlugs.From(Long("Member", i), "character"), Rel = "member_of", To = "faction:the-party" },
            }).ToArray());
        }

        var text = await _s.Call("campaign", """{"action":"summary"}""");

        Assert.True(text.Length <= 24_000, $"summary is {text.Length} characters");
        Assert.EndsWith("_Output cut at 24,000 characters; read campaign://big/threads or campaign_search for the full lists._", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH6 (L10): a player view's summary points only at reads of its own view. With more open quests than the summary
    /// lists, the author is pointed at the threads resource; the party at a campaign_search with its own perspective, which
    /// sent as printed lists the party's quests and never the author-only one. The threads resource is the author's view
    /// (author-only quests included), and so is a search without a perspective, so a model drafting for the party that
    /// followed its own summary's hint would read what the party must not.
    /// </summary>
    [Fact]
    public async Task Summary_PartyViewWithMoreOpenQuestsThanListed_PointsOnlyAtAReadOfItsOwnView()
    {
        await _s.Call("campaign", """{"action":"create","name":"Hint World","role":"dm","ruleset":"2024","slug":"hint"}""");
        var ops = string.Join(", ", Enumerable.Range(1, 9).Select(i =>
            $$"""{"op": "upsert", "kind": "quest", "name": "Party quest {{i}}", "status": "active", "visibility": "party"}"""));
        await _s.Call("campaign_write",
            $$"""{"campaign": "hint", "ops": [{{ops}}, {"op": "upsert", "kind": "quest", "name": "Free the prisoner of the Axiom Cage", "status": "active", "visibility": "author"}]}""");

        var party = await _s.Call("campaign", """{"action":"summary","campaign":"hint","perspective":"party"}""");
        var author = await _s.Call("campaign", """{"action":"summary","campaign":"hint"}""");

        Assert.Contains("_… and 2 more; campaign://hint/threads or campaign_search with kinds [\"quest\", \"thread\"] lists them all._", author, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign://", party, StringComparison.Ordinal);
        var call = Regex.Match(party, "_… and 1 more; campaign_search (\\{[^}]*\\}) lists them all\\._").Groups[1].Value;
        Assert.Equal("{\"kinds\": [\"quest\", \"thread\"], \"perspective\": \"party\", \"campaign\": \"hint\"}", call);
        var listed = await _s.Call("campaign_search", call);
        Assert.Contains("Party quest 9", listed, StringComparison.Ordinal);
        Assert.DoesNotContain("Axiom", listed, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH6 (L10): a non-author summary with more party members and running clocks than it lists points, for each, at a
    /// campaign_search with this campaign and its own perspective; the author keeps the bare search. The clocks call, sent as
    /// printed, reads that view. A bare search is the author's view, so a model drafting for the party that followed its
    /// summary's hint would list author-only members and clocks by their true names.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("character:member-01")]
    public async Task Summary_NonAuthorViewWithMoreMembersAndClocksThanListed_EachHintSearchesItsOwnView(string perspective)
    {
        var hint = await _s.Create("Hint World", slug: "hint");
        foreach (var chunk in Enumerable.Range(1, 31).Chunk(16))
        {
            _s.Apply(hint, WriteContext.Default, chunk.SelectMany(i => new[]
            {
                new CampaignOpSpec { Op = "upsert", Kind = "character", Name = $"Member {i:D2}", Visibility = "party" },
                new CampaignOpSpec { Op = "link", From = $"character:member-{i:D2}", Rel = "member_of", To = "faction:the-party", Visibility = "party" },
                new CampaignOpSpec
                {
                    Op = "upsert", Kind = "clock", Name = $"Clock {i:D2}", Visibility = "party",
                    Clock = new ClockSpec { Segments = 4, Filled = 1, Unit = "day", ShownToPlayers = true },
                },
            }).ToArray());
        }

        var text = await _s.Call("campaign", $$"""{"action": "summary", "campaign": "hint", "perspective": "{{perspective}}"}""");
        var author = await _s.Call("campaign", """{"action": "summary", "campaign": "hint"}""");

        string Search(string kind) => $"campaign_search {{\"kinds\": [\"{kind}\"], \"perspective\": \"{perspective}\", \"campaign\": \"hint\"}}";
        Assert.EndsWith($", … and 1 more ({Search("character")} lists them)", PartyLine(text), StringComparison.Ordinal);
        Assert.Contains($"\n_… and 1 more; {Search("clock")} lists them all._\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign_search with", text, StringComparison.Ordinal);
        Assert.EndsWith(", … and 1 more (campaign_search with kinds [\"character\"] lists them)", PartyLine(author), StringComparison.Ordinal);
        Assert.Contains("\n_… and 1 more; campaign_search with kinds [\"clock\"] lists them all._\n", author, StringComparison.Ordinal);
        var clocks = await _s.Call("campaign_search", Search("clock")["campaign_search ".Length..]);
        Assert.Contains($"_Perspective: {perspective}", clocks, StringComparison.Ordinal);
        Assert.Contains("`clock:clock-01`", clocks, StringComparison.Ordinal);

        static string PartyLine(string summary) => summary.Split('\n').Single(l => l.StartsWith("- **Party:** ", StringComparison.Ordinal));
    }

    /// <summary>
    /// FH6 (L10): a non-author summary cut at the output cap says to read the rest with a campaign_search in its own view of
    /// this campaign, never the threads resource (the author's view, author-only quests included) or a bare search. The
    /// limits on names and lists keep a real non-author summary under the cap, so the summary is built here.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("character:member-01")]
    public void FormatSummary_NonAuthorViewPastTheOutputCap_TheCapNoteSearchesItsOwnView(string perspective)
    {
        static EntityLink Link(string kind, int i) =>
            new($"{kind}:{kind}-{i:D2}", kind, $"{kind} {i:D2} " + string.Concat(Enumerable.Repeat("of the long road ", 40)));
        var summary = new CampaignSummaryView("hint", "Hint World", "dm", "2024", "active", null, null, null, Link("faction", 0),
            Enumerable.Range(1, 30).Select(i => Link("character", i)).ToList(), null, null, null, null,
            Enumerable.Range(1, 8).Select(i => new ThreadLine(Link("quest", i), "active")).ToList(), 8, 0,
            Enumerable.Range(1, 30).Select(i => new ClockLine(Link("clock", i), 1, 4, "day")).ToList(), null);
        var parsed = Perspective.Parse(perspective);

        var text = SummaryMarkdown.Format(summary, new CampaignView(parsed, false, CampaignMarkdownText.Banner(parsed, false)));

        Assert.EndsWith(
            $"\n\n_Output cut at 24,000 characters; campaign_search {{\"perspective\": \"{perspective}\", \"campaign\": \"hint\"}} with kinds gives the full lists._",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain("campaign://", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH5 (C13): the examples in the tool descriptions work when sent verbatim, in order: the campaign tool's create
    /// example makes the character every other example names (character:belmakor), so campaign_search's and
    /// campaign_knowledge's examples then work instead of being refused with "no character belmakor".
    /// </summary>
    [Fact]
    public async Task CallTool_TheCreateExampleThenTheSearchAndKnowledgeExamples_WorkAsWritten()
    {
        var tools = await _s.Server.Client.ListToolsAsync();
        string Example(string tool) => Assert.Single(tools, t => t.Name == tool).Description.Split("\nExample: ")[^1];

        var created = await _s.Call("campaign", Example("campaign"));
        var search = await _s.Call("campaign_search", Example("campaign_search"));
        var check = await _s.Call("campaign_knowledge", Example("campaign_knowledge"));

        Assert.Contains("- **My character:** `character:belmakor`, a member of the party\n", created, StringComparison.Ordinal);
        Assert.StartsWith("# Search belmakor: \"old king\"\n_Perspective: character:belmakor (Belmakor).", search, StringComparison.Ordinal);
        Assert.StartsWith("# Knowledge check: ", check, StringComparison.Ordinal);
    }

    // Every campaign_history call a result prints, as the model would copy it (the JSON between the braces).
    private static List<string> HistoryCalls(string text) => HistoryCallRegex().Matches(text).Select(m => m.Groups[1].Value).ToList();

    // Triggers that refuse every write to app_state: after its batch, a create's one other write is the active campaign.
    private static void BlockAppStateWrites(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER test_block_app_state_insert BEFORE INSERT ON app_state BEGIN SELECT RAISE(ABORT, 'blocked by the test'); END;" +
            "CREATE TRIGGER test_block_app_state_update BEFORE UPDATE ON app_state BEGIN SELECT RAISE(ABORT, 'blocked by the test'); END;";
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Invariant: <c>campaign summary</c> and <c>campaign get</c> for a non-author perspective carry nothing that view cannot
/// see (the leak rule): no author-only name, secret text, cross-campaign name, settings, history, secret list or count,
/// and they start with the perspective banner naming only the perspective's own character.
///
/// <para>
/// Why it fails silently: the summary is assembled from many readers' pieces (party, threads, sessions, the author's
/// block), and a formatter that printed one author-only field for every view would look right in every author-view test.
/// </para>
/// </summary>
public sealed class CampaignToolReadTests : IClassFixture<BelmakorServer>
{
    // Words only the author's block prints; none may appear in another view (the banner line is checked on its own).
    private static readonly string[] AuthorOnlyWording =
        ["## Author", "Secrets", "secret", "Inventions", " lean", "Settings", "near-TPK", "dm_pronouns", "batch `", "Since the last session", "Default for calls"];

    private readonly BelmakorServer _f;

    public CampaignToolReadTests(BelmakorServer fixture)
    {
        _f = fixture;
    }

    public static TheoryData<string, string> PerspectivesAndActions()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in BelmakorServer.NonAuthorPerspectives)
        {
            data.Add(perspective, "summary");
            data.Add(perspective, "get");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PerspectivesAndActions))]
    public async Task Read_NonAuthorPerspective_ContainsNothingThatViewCannotSee(string perspective, string action)
    {
        var text = await _f.Call("campaign", $$"""{"action":"{{action}}","perspective":"{{perspective}}"}""");

        BelmakorServer.AssertClean(text, BelmakorServer.ForbiddenFor(perspective), $"campaign {action} as {perspective}");
        var lines = text.Split('\n');
        Assert.StartsWith("_Perspective: " + perspective, lines[1], StringComparison.Ordinal);
        BelmakorServer.AssertClean(string.Join('\n', lines.Where((_, i) => i != 1)), AuthorOnlyWording, $"campaign {action} as {perspective}");
    }

    [Theory]
    [MemberData(nameof(BelmakorServer.VeilPerspectiveData), MemberType = typeof(BelmakorServer))]
    public async Task Read_VeilNonAuthorPerspective_NeverSpellsTheTrueName(string perspective)
    {
        foreach (var action in new[] { "summary", "get" })
        {
            var text = await _f.Call("campaign", $$"""{"action":"{{action}}","perspective":"{{perspective}}","campaign":"veil"}""");

            BelmakorServer.AssertClean(text, BelmakorServer.VeilForbidden, $"veil {action} as {perspective}");
        }
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("get")]
    public async Task Read_AuthorView_HasNoBannerAndShowsTheAuthorsPart(string action)
    {
        var text = await _f.Call("campaign", $$"""{"action":"{{action}}"}""");

        Assert.DoesNotContain("_Perspective:", text, StringComparison.Ordinal);
        Assert.Contains("near-TPK", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_AuthorView_ListsTheSecretAndTheChangesSinceTheLastSession()
    {
        var text = await _f.Call("campaign", """{"action":"summary"}""");

        Assert.Contains("### Secrets\n- Belmakor's ambition (`secret:belmakors-ambition`) · hidden\n", text, StringComparison.Ordinal);
        Assert.Contains("\n### Since the last session\n", text, StringComparison.Ordinal);
        Assert.EndsWith(
            "\ncampaign_history {\"action\": \"since\", \"session\": 3, \"campaign\": \"belmakor\"} lists every change from session 3 on, that " +
            "session's own included.\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_CharacterPerspective_BannerNamesOnlyTheCharacterItself()
    {
        var text = await _f.Call("campaign", """{"action":"summary","perspective":"character:belmakor"}""");

        Assert.Equal(
            "_Perspective: character:belmakor (Belmakor Silverwind). Names are the ones this view knows; author-only text is withheld._",
            text.Split('\n')[1]);
    }

    [Fact]
    public async Task Summary_PartyPerspective_NamesTheOpenThreadAndTheLastSession()
    {
        var text = await _f.Call("campaign", """{"action":"summary","perspective":"party"}""");

        Assert.Contains("- **Last played:** Kraken, the statue, the old king (`session:3`) · 2026-08 (approx)\n", text, StringComparison.Ordinal);
        Assert.Contains("## Open quests and threads\n- The old king's errand (`thread:old-kings-errand`) · open\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_NonAuthorPerspective_PointsOnlyAtItsOwnKnowledgePage()
    {
        var text = await _f.Call("campaign", """{"action":"get","perspective":"party"}""");

        Assert.EndsWith("What this view knows, on one page: `campaign://belmakor/knowledge/party`.\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/summary", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_UnknownCharacterPerspective_IsRefusedWithoutNamingHiddenCharacters()
    {
        var text = _f.Server.ErrorText(await _f.Server.CallToolJsonAsync("campaign", """{"action":"summary","perspective":"character:keras"}"""));

        // Only the caller's own words are echoed: no suggestion may map "keras" to the old king, whose author alias it is.
        Assert.Equal(
            "An error occurred invoking 'campaign': perspective \"character:keras\": no character keras in this campaign. Characters are " +
            "named character:<slug>; campaign_search with kinds [\"character\"] lists them.",
            text);
    }

    [Fact]
    public async Task Summary_TypicalCampaign_IsWellUnderTheTypicalBudget()
    {
        var text = await _f.Call("campaign", """{"action":"summary"}""");

        Assert.True(text.Length < 8_000, $"summary is {text.Length} characters");
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("get")]
    public async Task Read_Public_NamesNeitherThePartyNorThePlayersCharacter(string action)
    {
        var text = await _f.Call("campaign", $$"""{"action":"{{action}}","perspective":"public"}""");

        // Both are party-visible, so the public's view of the campaign's record leaves its party and character links out.
        BelmakorServer.AssertClean(text, ["The party", "faction:party", "Belmakor Silverwind", "character:belmakor", "Party:", "My character"],
            $"campaign {action} as public");
    }

    [Fact]
    public async Task Summary_PublicSeesNoSession_DoesNotSayNoneWerePlayed()
    {
        var text = await _f.Call("campaign", """{"action":"summary","perspective":"public"}""");

        Assert.DoesNotContain("Sessions", text, StringComparison.Ordinal);
        Assert.DoesNotContain("played", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("summary", "\n## Author\n")]
    [InlineData("get", "\n- **Default for calls without campaign:** no\n")]
    public async Task Read_DmInTheDmCampaign_IsTheAuthorsViewWithNoBanner(string action, string authorPart)
    {
        var text = await _f.Call("campaign", $$"""{"action":"{{action}}","perspective":"dm","campaign":"one-piece"}""");

        Assert.StartsWith("# One Piece (`one-piece`)\n\n", text, StringComparison.Ordinal);
        Assert.Contains(authorPart, text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Invariant: the author's summary renders every part of a campaign (where the party is, the live session, the last recap,
/// blocked quests among the open ones, every running clock, the secrets with their statuses, the proposed inventions), and
/// every other view of the same campaign carries none of its author-only text.
///
/// <para>
/// Why it fails silently: each of those parts is one optional block in the formatter, and a block that is never printed
/// (or printed for the wrong view) leaves every other line of the summary looking right.
/// </para>
/// </summary>
public sealed class CampaignToolGoldenTests : IClassFixture<GoldenCampaignServer>
{
    private readonly GoldenCampaignServer _g;

    public CampaignToolGoldenTests(GoldenCampaignServer fixture)
    {
        _g = fixture;
    }

    public static TheoryData<string, string> PerspectivesAndActions()
    {
        var data = new TheoryData<string, string>();
        foreach (var perspective in GoldenCampaignServer.NonAuthorPerspectives)
        {
            data.Add(perspective, "summary");
            data.Add(perspective, "get");
        }

        return data;
    }

    [Fact]
    public async Task Summary_Author_RendersEveryPart()
    {
        var text = await _g.Call("campaign", """{"action":"summary"}""");

        Assert.StartsWith(
            "# Golden Isles (`golden`)\n\nplayer campaign · 2024 rules · active\n- **My character:** Aria (`character:aria`)\n" +
            "- **Party:** The Crew (`faction:the-crew`): Aria (`character:aria`), Brom (`character:brom`)\n" +
            "- **Where:** The Lighthouse (`location:lighthouse`)\n- **In-game date:** Day 12\n",
            text, StringComparison.Ordinal);
        Assert.Matches(new Regex(
            "\n- \\*\\*Live now:\\*\\* Session 2 \\(`session:2`\\) · \\d{4}-\\d{2}-\\d{2}\n- \\*\\*Last played:\\*\\* Landfall \\(`session:1`\\)\n" +
            "  > We came ashore under the lighthouse\\.\n  > The keeper watched us from the gallery\\.\n"), text);
        Assert.Contains(
            "\n## Open quests and threads\n- Pay the harbour tax (`quest:harbour-tax`) · blocked\n- Relight the lamp (`quest:relight-the-lamp`) · active\n",
            text, StringComparison.Ordinal);
        Assert.Contains("\n## Running clocks\n- The Cult Awakens (`clock:cult-awakens`): 1/4 days\n- The Tide Rises (`clock:tide-rises`): 2/6 segments\n",
            text, StringComparison.Ordinal);
        Assert.Contains("\n### Secrets\n- The keeper's heir (`secret:keepers-heir`) · partial\n", text, StringComparison.Ordinal);
        Assert.Contains(
            $"\n### Inventions (accept / strike)\n- F1 `character:vell`: Captain Vell\n- F2 `{_g.ProposedFact}`: Vell once sailed with the keeper.\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_Party_ShowsTheClockItMaySeeAndTheRecapButNoAuthorPart()
    {
        var text = await _g.Call("campaign", """{"action":"summary","perspective":"party"}""");

        Assert.Contains("\n  > We came ashore under the lighthouse.\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n## Running clocks\n- The Tide Rises (`clock:tide-rises`): 2/6 segments\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PerspectivesAndActions))]
    public async Task Read_NonAuthorPerspective_ContainsNoAuthorOnlyText(string perspective, string action)
    {
        var text = await _g.Call("campaign", $$"""{"action":"{{action}}","perspective":"{{perspective}}"}""");

        BelmakorServer.AssertClean(text, GoldenCampaignServer.AuthorOnly, $"golden {action} as {perspective}");
        BelmakorServer.AssertClean(text, ["## Author", "Secrets", "Inventions", "Settings", "Default for calls"], $"golden {action} as {perspective}");
    }
}

/// <summary>
/// Invariant: what the model reads before calling a campaign read/admin tool (its description, argument names and
/// descriptions, title and the four hints) is exactly what contract §9 sets: every description within Claude Code's 2,048
/// characters, naming every argument and ending with an example; every argument described; no SDK-injected parameter in the
/// schema; the hints of the §9 table. campaign_write's description and op fields say what a write does with what it is
/// given.
///
/// <para>
/// Why it fails silently: a description over the limit is truncated by the client without a word, an argument the
/// description never names is one the model never sends, and a flipped hint changes what Claude Code auto-approves. These
/// rows join ServerSurfaceTests when the tool list there is updated; until then they are pinned here.
/// </para>
/// </summary>
public sealed class CampaignToolSurfaceTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public CampaignToolSurfaceTests(McpServerHarness server)
    {
        _server = server;
    }

    public static TheoryData<string, string[]> ToolsAndArguments() => new()
    {
        {
            "campaign",
            ["action", "campaign", "name", "role", "ruleset", "dm_name", "slug", "settings", "party_name", "my_character", "status", "summary_md",
                "current_location", "current_ingame", "perspective", "reason", "session", "dry_run"]
        },
        { "campaign_search", ["query", "kinds", "statuses", "tags", "perspective", "include_facts", "as_of_session", "limit", "cursor", "campaign"] },
        { "campaign_get", ["refs", "include", "detail", "perspective", "as_of_session", "campaign"] },
        {
            "campaign_history",
            ["action", "since", "session", "targets", "ref", "refs", "detail", "batch_id", "dry_run", "reason", "limit", "cursor", "campaign"]
        },
        {
            "campaign_character",
            ["action", "character", "campaign", "perspective", "sheet", "sim_profile", "amount", "damage_type", "slot_level", "pact", "resource", "kind",
                "hit_dice", "rolls", "add", "remove", "level", "class", "items", "coins", "session", "reason", "dry_run"]
        },
        {
            "combat",
            ["action", "campaign", "encounter", "name", "add_party", "lair", "edition", "combatants", "targets", "amount", "dice", "damage_type", "parts",
                "critical", "magical", "half", "raw", "knock_out", "source", "secret", "temp", "item", "add", "remove", "duration", "dc", "ability", "level",
                "round", "effect", "resource", "spell", "slot_level", "pact", "drop", "total", "face", "stable", "resistance", "rolls", "surprised", "from",
                "perspective", "outcome", "xp", "loot", "currency", "discard", "force", "dry_run", "reason"]
        },
    };

    [Theory]
    [MemberData(nameof(ToolsAndArguments))]
    public async Task Schema_ExactlyTheseArguments_EachDescribedAndNamedInTheDescriptionWithAnExample(string name, string[] arguments)
    {
        var tool = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == name);
        var properties = tool.JsonSchema.GetProperty("properties").EnumerateObject().ToList();

        Assert.Equal(arguments, properties.Select(p => p.Name).ToArray());
        Assert.All(properties, p => Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString()), $"{name}.{p.Name}"));
        Assert.True(tool.Description.Length <= 2_048, $"{name}'s description is {tool.Description.Length} characters");
        Assert.All(arguments, a => Assert.Contains(a, tool.Description, StringComparison.Ordinal));
        Assert.Contains("\nExample: {", tool.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign", "Campaigns", false, false, false, false)]
    [InlineData("campaign_search", "Search a campaign", true, false, true, false)]
    [InlineData("campaign_get", "Read campaign entries", true, false, true, false)]
    [InlineData("campaign_history", "Campaign history and undo", false, true, false, false)]
    [InlineData("campaign_character", "Character sheets", false, true, false, false)]
    [InlineData("combat", "Live combat", false, false, false, false)]
    public async Task Annotations_TheContractsHints(string name, string title, bool readOnly, bool destructive, bool idempotent, bool openWorld)
    {
        var tool = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == name).ProtocolTool;

        Assert.Equal(title, tool.Title);
        Assert.Equal(((bool?)readOnly, (bool?)destructive, (bool?)idempotent, (bool?)openWorld),
            (tool.Annotations!.ReadOnlyHint, tool.Annotations.DestructiveHint, tool.Annotations.IdempotentHint, tool.Annotations.OpenWorldHint));
    }

    [Theory]
    [InlineData("campaign", "action")]
    [InlineData("campaign_get", "refs")]
    [InlineData("campaign_history", "action")]
    [InlineData("campaign_character", "action")]
    [InlineData("combat", "action")]
    public async Task Schema_OnlyTheOneArgumentIsRequired(string name, string required)
    {
        var tool = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == name);

        Assert.Equal(new[] { required }, tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    /// <summary>
    /// FH9 (U11): the history tool asks for batch ids in full and never offers an 8-character prefix. Batch ids are UUIDv7,
    /// whose first 8 hex digits are the top of the millisecond clock: every batch made within about a minute shares them, so
    /// the short form a description promised was usually refused as ambiguous.
    /// </summary>
    [Fact]
    public async Task Description_BatchId_AsksForTheWholeIdAndOffersNoEightCharacterPrefix()
    {
        var tool = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == "campaign_history");
        var batchId = tool.JsonSchema.GetProperty("properties").GetProperty("batch_id").GetProperty("description").GetString()!;

        foreach (var text in new[] { tool.Description, batchId })
        {
            Assert.Contains("in full", text, StringComparison.Ordinal);
            Assert.Contains("batches made within a minute share their first 8 characters", text, StringComparison.Ordinal);
            Assert.DoesNotContain("prefix", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// FH3 (U12): the campaign tool says how a call without campaign picks one, in the order contract §3.7 resolves it:
    /// this session's current campaign (set by use, create, or a session start or end that names its campaign), else the
    /// one last chosen with use or create (both persist it), else the only one. Without create in the second step, the
    /// description told the model that a campaign it had just created would not be the default in a new session.
    /// </summary>
    [Fact]
    public async Task Description_Campaign_SaysHowACallWithoutCampaignChoosesOne()
    {
        var campaign = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == "campaign").Description;

        Assert.Contains(
            "omitted, it is this session's current one (set by use, create, or a campaign_session start or end that names its " +
            "campaign), else the one last chosen with use or create, else the only one.\n",
            campaign, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Descriptions_ListTheVocabulariesTheToolsAccept()
    {
        var tools = await _server.Client.ListToolsAsync();
        var campaign = Assert.Single(tools, t => t.Name == "campaign").Description;
        var search = Assert.Single(tools, t => t.Name == "campaign_search").Description;

        Assert.All(CampaignValues.Kinds.Set.Values, kind => Assert.Contains(kind, search, StringComparison.Ordinal));
        Assert.All(CampaignValues.Roles.Set.Values, role => Assert.Contains($"\"{role}\"", campaign, StringComparison.Ordinal));
        Assert.All(CampaignValues.Rulesets.Set.Values, ruleset => Assert.Contains($"\"{ruleset}\"", campaign, StringComparison.Ordinal));
        Assert.All(CampaignValues.CampaignStatuses.Set.Values, status => Assert.Contains(status, campaign, StringComparison.Ordinal));
        Assert.All(Tools.CampaignTools.Actions.Values, action => Assert.Contains($"action \"{action}\"", campaign, StringComparison.Ordinal));
        Assert.All(Tools.CampaignHistoryTools.Actions.Values, action =>
            Assert.Contains($"action \"{action}\"", Assert.Single(tools, t => t.Name == "campaign_history").Description, StringComparison.Ordinal));
    }

    /// <summary>
    /// UR02, UR3, UR4: campaign_write's schema says what writes do, where the model reads it before writing. A public alias
    /// of an entity the party knows under another name is never shown to the party (a public epithet would give the
    /// identity away), so "Only public and party aliases are searchable by players" had a model record one expecting the
    /// party to find the entity by it. Nothing said f:&lt;n&gt; numbers are shared by all campaigns, or that a code reaches
    /// a fact made earlier in the batch, so a new campaign's batch that gated after "f:1" was refused. And with only the
    /// knowledge check named, models told users a forbidden word is caught only in a draft, when a write that puts one
    /// into text players can read warns too. Each row is a field's whole description, by its path under ops.
    /// </summary>
    [Theory]
    [InlineData("",
        "The ops, applied in order as one batch (at most 50). An op may use an entity or fact an earlier op created: an entity " +
        "by kind:slug, a fact by the code you gave it (code \"A1\"); f:<n> numbers are shared by all campaigns, so a new " +
        "campaign's first fact is not f:1. Each field's description names the ops that take it.")]
    [InlineData("aliases.visibility",
        "Who may see the alias: public, party (default), restricted or author. Players find an entity by its public and party " +
        "aliases; one they know under another name (known_as) only by that name and its party aliases (a public epithet " +
        "would give the identity away).")]
    [InlineData("gate.forbidden_terms",
        "Words no player-facing text may use while this rule is active, e.g. [\"seal\"]; inflections match too. " +
        "campaign_knowledge check flags them in a draft, and a write that puts them into text players can read warns (and " +
        "still applies).")]
    public async Task Schema_CampaignWriteOpsField_SaysWhatTheWriteDoes(string path, string description)
    {
        var tool = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == "campaign_write");
        var field = tool.JsonSchema.GetProperty("properties").GetProperty("ops");
        foreach (var name in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            // Into an op (ops' items), then down through each field; a list field (aliases) describes its items' fields.
            field = field.TryGetProperty("items", out var items) ? items : field;
            field = field.GetProperty("properties").GetProperty(name);
        }

        Assert.Equal(description, field.GetProperty("description").GetString());
    }

    /// <summary>
    /// UR4: campaign_write's description names everything it applies with a warning instead of refusing, including text
    /// players can read that uses a forbidden word or a name they do not use (the write-time player-text check). Without
    /// it, a model told the user a forbidden word is caught only when a draft is checked.
    /// </summary>
    [Fact]
    public async Task Description_CampaignWrite_SaysPlayerTextWithAForbiddenWordOrAHiddenNameWarnsAndApplies()
    {
        var description = Assert.Single(await _server.Client.ListToolsAsync(), t => t.Name == "campaign_write").Description;

        Assert.Contains(
            "A gated fact reaching a knower before its gate is met, a superseded fact's dependents, and text players can read " +
            "that uses a forbidden word or a name they do not use are applied with warnings, never refused.",
            description, StringComparison.Ordinal);
    }
}
