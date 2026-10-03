using DndMcp.Domain.Campaign.Ops;
using DndMcp.Formatting.Campaign;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Invariant: the <c>campaign://</c> resources are concrete URIs, never templates: <c>campaign://list</c> always, one
/// <c>/summary</c> and one <c>/threads</c> per campaign in resources/list (with a name, title, description and the
/// markdown type), and the deep <c>/entity/&lt;ref&gt;</c>, <c>/session/&lt;n&gt;</c> and <c>/knowledge/&lt;perspective&gt;</c>
/// readable but never listed. The list handler never creates, migrates or fails: with no campaigns.db, an unmigrated one
/// or a damaged one it lists the static resources; and a URI nothing serves (an unknown <c>rules://</c> table included)
/// fails exactly as it did before the campaign read handler existed.
///
/// <para>
/// Why it fails silently: resources/list is sent when Claude Code connects, so a list handler that created campaigns.db
/// would leave one behind for every user who never asked for a campaign, one that migrated would change a file on
/// connect, and one that threw would make the rules resources vanish too (the client drops the whole list). The read
/// handler replaces the SDK's own "unknown resource" answer for every URI, so a changed error for a mistyped
/// <c>rules://tables/x</c> would go unnoticed without a test that compares it with a server that has no handler.
/// </para>
/// </summary>
public sealed class CampaignResourceTests : IAsyncLifetime
{
    private static readonly string[] StaticUris =
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

    private CampaignTestServer _s = null!;

    public async Task InitializeAsync() => _s = await CampaignTestServer.StartAsync();

    public async Task DisposeAsync() => await _s.DisposeAsync();

    [Fact]
    public async Task ListResources_NoDatabase_IsTheStaticResourcesAndCreatesNoFile()
    {
        var resources = await _s.Server.Client.ListResourcesAsync();

        Assert.Equal(StaticUris, resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
        Assert.Empty(await _s.Server.Client.ListResourceTemplatesAsync());
        Assert.False(File.Exists(_s.DatabasePath), "resources/list created campaigns.db");

        // No file is the normal case for a user with no campaigns: it must not even try to open one (and log a warning on
        // every connect).
        Assert.DoesNotContain(_s.Server.ServerLog.Entries, e => e.Message.Contains("Could not list the campaign resources", StringComparison.Ordinal));
    }

    [Fact]
    public void Capabilities_Resources_AdvertiseListChanged()
    {
        // Claude Code subscribes to notifications/resources/list_changed only when the server says it sends them; without
        // the subscription a created campaign stays out of the @ menu until a restart.
        Assert.True(_s.Server.Client.ServerCapabilities.Resources?.ListChanged, "resources.listChanged is not advertised");
    }

    [Fact]
    public async Task ListResources_CampaignList_HasNameTitleDescriptionAndMarkdownType()
    {
        var resource = Assert.Single(await _s.Server.Client.ListResourcesAsync(), r => r.Uri == "campaign://list").ProtocolResource;

        Assert.Equal(("campaigns", "Campaigns", "text/markdown"), (resource.Name, resource.Title, resource.MimeType));
        Assert.StartsWith("Every campaign in campaigns.db", resource.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadList_NoDatabase_SaysThereAreNoCampaignsAndCreatesNoFile()
    {
        var text = await _s.Read("campaign://list");

        Assert.Equal("# Campaigns\n\nNo campaigns yet. The campaign tool creates one (action \"create\").\n", text);
        Assert.False(File.Exists(_s.DatabasePath), "reading campaign://list created campaigns.db");
    }

    [Fact]
    public async Task ReadSummary_NoDatabase_IsNotFoundAndCreatesNoFile()
    {
        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => _s.Server.Client.ReadResourceAsync("campaign://sky/summary").AsTask());

        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
        Assert.Contains("Unknown resource URI: 'campaign://sky/summary': there is no campaign \"sky\"; campaign://list lists the campaigns.", ex.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(_s.DatabasePath), "reading an unknown campaign created campaigns.db");
    }

    [Fact]
    public async Task ListResources_AfterCreate_ListsASummaryAndThreadsPerCampaignAndWasAnnounced()
    {
        await _s.Call("campaign", """{"action":"create","name":"Sky World","role":"player","ruleset":"2014","slug":"sky"}""");
        await _s.Create("Deep", ruleset: "2024");
        await _s.WaitForListChanged(2);

        var resources = await _s.Server.Client.ListResourcesAsync();

        Assert.Equal(
            StaticUris.Concat(["campaign://deep/summary", "campaign://deep/threads", "campaign://sky/summary", "campaign://sky/threads"]).Order(StringComparer.Ordinal),
            resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
        var summary = Assert.Single(resources, r => r.Uri == "campaign://sky/summary").ProtocolResource;
        Assert.Equal(("sky-summary", "Sky World: summary", "text/markdown"), (summary.Name, summary.Title, summary.MimeType));
        Assert.StartsWith("The Sky World campaign (player) at a glance: ", summary.Description, StringComparison.Ordinal);
        var threads = Assert.Single(resources, r => r.Uri == "campaign://deep/threads").ProtocolResource;
        Assert.Equal(("deep-threads", "Deep: quests and threads", "text/markdown"), (threads.Name, threads.Title, threads.MimeType));
        Assert.False(string.IsNullOrWhiteSpace(threads.Description));
        Assert.Empty(await _s.Server.Client.ListResourceTemplatesAsync());
    }

    [Fact]
    public async Task ListResources_AfterRename_TheTitleFollowsAndTheRenameWasAnnounced()
    {
        await _s.Create("Sky");
        await _s.Call("campaign", """{"action":"update","name":"Sky World"}""");
        await _s.WaitForListChanged(2);

        var resource = Assert.Single(await _s.Server.Client.ListResourcesAsync(), r => r.Uri == "campaign://sky/summary").ProtocolResource;

        Assert.Equal("Sky World: summary", resource.Title);
    }

    [Fact]
    public async Task ListResources_DamagedDatabase_StillListsTheStaticResourcesAndLogsAWarning()
    {
        Directory.CreateDirectory(_s.Server.DataDirectory);
        await File.WriteAllTextAsync(_s.DatabasePath, "this is not a SQLite database, only some bytes where one should be");

        var resources = await _s.Server.Client.ListResourcesAsync();

        Assert.Equal(StaticUris, resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
        Assert.Contains(_s.Server.ServerLog.Entries, e => e.Message.Contains("Could not list the campaign resources", StringComparison.Ordinal));
    }

    /// <summary>
    /// FI handoff (FI2): a campaigns.db another dnd-mcp moved to a newer schema while this server ran is unreadable to the
    /// list handler, as to every other reader: resources/list shows only the static resources and logs why, rather than
    /// listing the newer build's campaign rows as this build reads them (it had already passed the open, so nothing else
    /// stops it); campaign://list refuses with the store's newer-schema message. Nothing crashes, and nothing is written.
    /// </summary>
    [Fact]
    public async Task ListResources_CampaignsDbMovedToANewerSchema_ListsOnlyTheStaticResourcesAndCampaignListRefuses()
    {
        await _s.Create("Sky");
        Assert.Contains(await _s.Server.Client.ListResourcesAsync(), r => r.Uri == "campaign://sky/summary");
        var newer = CampaignDbMigrator.LatestVersion + 1;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _s.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {newer}";
            command.ExecuteNonQuery();
        }

        var resources = await _s.Server.Client.ListResourcesAsync();
        var list = await Assert.ThrowsAsync<McpProtocolException>(() => _s.Server.Client.ReadResourceAsync("campaign://list").AsTask());

        Assert.Equal(StaticUris, resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
        Assert.Contains(_s.Server.ServerLog.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                                                          e.Message.Contains("Could not list the campaign resources", StringComparison.Ordinal) &&
                                                          e.Exception?.Message.Contains($"has schema version {newer}, newer than", StringComparison.Ordinal) == true);
        Assert.StartsWith($"Request failed (remote): campaigns.db at {_s.DatabasePath} was written by a newer version of dnd-mcp", list.Message, StringComparison.Ordinal);
        Assert.Equal(newer, UserVersion(_s.DatabasePath));
    }

    /// <summary>
    /// M11 (FH10): campaign://list marks the campaign a call without campaign uses, by the rule the campaign tool's list
    /// marks it with (§3.7: this process's current one, else the persisted active one, else the only one). It stopped after
    /// the persisted active campaign, so a campaign nobody chose (created by another process) was marked by the tool's list
    /// ("calls use this one") and not here, though every call used it.
    /// </summary>
    [Fact]
    public async Task ReadList_OnlyCampaignNobodyChose_IsMarkedLikeTheToolMarksIt()
    {
        _s.Campaigns.Store.Create("Only World", "dm", "2024", slug: "only");
        Assert.Null(_s.Campaigns.CurrentCampaignId);

        var tool = await _s.Call("campaign", """{"action":"list"}""");
        var resource = await _s.Read("campaign://list");

        Assert.Contains("| `only` | Only World | dm | — | 2024 | active | calls use this one |", tool, StringComparison.Ordinal);
        Assert.Contains("\n## Only World (`only`) · the current campaign\n", resource, StringComparison.Ordinal);
    }

    /// <summary>
    /// M11, and kills A05 and C06 (FH10, M09, M10): after this process's current campaign's own creation is undone, its id
    /// names a campaign that no longer exists (and so does the persisted active one); calls then use the only campaign
    /// left, and both lists mark that one. Taking the dangling id marked nothing while the footer said "use the marked one".
    /// </summary>
    [Fact]
    public async Task ReadList_CurrentCampaignsCreateUndone_MarksTheCampaignCallsNowUse()
    {
        await _s.Call("campaign", """{"action":"create","name":"First","role":"dm","ruleset":"2024","slug":"first"}""");
        var second = await _s.Call("campaign", """{"action":"create","name":"Second","role":"dm","ruleset":"2024","slug":"second"}""");
        var batch = System.Text.RegularExpressions.Regex.Match(second, "Batch `([0-9a-f-]{36})`").Groups[1].Value;
        await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{batch}}","campaign":"second"}""");

        var tool = await _s.Call("campaign", """{"action":"list"}""");
        var resource = await _s.Read("campaign://list");

        Assert.Contains("| `first` | First | dm | — | 2024 | active | calls use this one |", tool, StringComparison.Ordinal);
        Assert.Contains("\n## First (`first`) · the current campaign\n", resource, StringComparison.Ordinal);
        Assert.StartsWith("# First (`first`)", await _s.Call("campaign", """{"action":"get"}"""), StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills C06 (FH10, M10): this process's current campaign was removed and another process made a different one active:
    /// the persisted active campaign is the one calls use, and the one marked.
    /// </summary>
    [Fact]
    public async Task ReadList_CurrentCampaignRemovedAndAnotherActive_MarksTheActiveOne()
    {
        await _s.Call("campaign", """{"action":"create","name":"First","role":"dm","ruleset":"2024","slug":"first"}""");
        await _s.Call("campaign", """{"action":"create","name":"Third","role":"dm","ruleset":"2024","slug":"third"}""");
        var second = await _s.Call("campaign", """{"action":"create","name":"Second","role":"dm","ruleset":"2024","slug":"second"}""");
        _s.Campaigns.Store.SetActive(_s.Campaigns.Store.TryGet("first")!.Id); // another process's campaign use
        var batch = System.Text.RegularExpressions.Regex.Match(second, "Batch `([0-9a-f-]{36})`").Groups[1].Value;
        await _s.Call("campaign_history", $$"""{"action":"undo","batch_id":"{{batch}}","campaign":"second"}""");

        var text = await _s.Read("campaign://list");

        Assert.Contains("\n## First (`first`) · the current campaign\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## Third (`third`) · the current campaign", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills U03 (FH10, M10): in a process that has chosen no campaign (a new Claude session), campaign://list marks the
    /// persisted active one (another session's campaign use), the one every call without campaign then uses.
    /// </summary>
    [Fact]
    public async Task ReadList_NoCampaignChosenInThisProcess_MarksThePersistedActiveOne()
    {
        _s.Campaigns.Store.Create("First", "dm", "2024", slug: "first");
        var second = _s.Campaigns.Store.Create("Second", "dm", "2024", slug: "second");
        _s.Campaigns.Store.SetActive(second.Campaign.Id);
        Assert.Null(_s.Campaigns.CurrentCampaignId);

        var text = await _s.Read("campaign://list");

        Assert.Contains("\n## Second (`second`) · the current campaign\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("## First (`first`) · the current campaign", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListResources_UnmigratedDatabase_ListsNoCampaignAndDoesNotMigrateIt()
    {
        Directory.CreateDirectory(_s.Server.DataDirectory);
        await File.WriteAllBytesAsync(_s.DatabasePath, []);

        var resources = await _s.Server.Client.ListResourcesAsync();

        Assert.Equal(StaticUris, resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
        Assert.Equal(0, UserVersion(_s.DatabasePath));
        Assert.DoesNotContain(_s.Server.ServerLog.Entries, e => e.Message.Contains("Could not list the campaign resources", StringComparison.Ordinal));
    }

    /// <summary>
    /// Kills C05 (FH10, M14): an unknown URI, campaign:// or rules://, is answered with the code the negotiated protocol
    /// gives it, as SDK 2.2.0 itself does: -32602 (InvalidParams) from 2026-07-28 on, -32002 before. Every other test
    /// negotiates 2025-11-25, so the newer branch and its boundary were never run; this one negotiates both, with the
    /// production registrations.
    /// </summary>
    [Theory]
    [InlineData("2026-07-28", McpErrorCode.InvalidParams)]
    [InlineData("2025-11-25", McpErrorCode.ResourceNotFound)]
    public async Task ReadUnknownUri_ErrorCodeFollowsTheNegotiatedProtocol(string protocol, McpErrorCode expected)
    {
        var data = Path.Combine(McpServerHarness.DataRoot, "protocol-" + Guid.NewGuid().ToString("N"));
        var clientToServer = new System.IO.Pipelines.Pipe();
        var serverToClient = new System.IO.Pipelines.Pipe();
        using var cts = new CancellationTokenSource();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDndMcpServer(options =>
            {
                options.CacheDirectory = McpServerHarness.SharedCacheDirectory;
                options.DataDirectory = data;
            })
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var serverTask = server.RunAsync(cts.Token);
        try
        {
            await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
                new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()),
                new ModelContextProtocol.Client.McpClientOptions { ProtocolVersion = protocol });
            Assert.Equal(protocol, client.NegotiatedProtocolVersion);

            var campaign = await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync("campaign://nowhere/summary").AsTask());
            var rules = await Assert.ThrowsAsync<McpProtocolException>(() => client.ReadResourceAsync("rules://tables/nope").AsTask());

            Assert.Equal(expected, campaign.ErrorCode);
            Assert.Equal(expected, rules.ErrorCode);
        }
        finally
        {
            await clientToServer.Writer.CompleteAsync();
            await cts.CancelAsync();
            try
            {
                await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }

            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    /// <summary>
    /// Kills C04 (FH10, M12): the knowledge page pages through what the view knows (up to 400 entries), so 60 entries are
    /// all listed, not the reader's first page of 50.
    /// </summary>
    [Fact]
    public async Task ReadKnowledge_SixtyPartyEntries_ListsEveryOne()
    {
        await _s.Call("campaign", """{"action":"create","name":"Lore World","role":"dm","ruleset":"2024","slug":"lore"}""");
        foreach (var range in new[] { Enumerable.Range(1, 30), Enumerable.Range(31, 30) })
        {
            var ops = string.Join(", ", range.Select(i => $$"""{"op": "upsert", "kind": "location", "name": "Place {{i:D2}}", "visibility": "party", "known_by": [{"who": "party"}]}"""));
            await _s.Call("campaign_write", $$"""{"campaign": "lore", "ops": [{{ops}}]}""");
        }

        var text = await _s.Read("campaign://lore/knowledge/party");

        Assert.Contains("location:place-01", text, StringComparison.Ordinal);
        Assert.Contains("location:place-60", text, StringComparison.Ordinal);
        Assert.DoesNotContain("More is recorded than fits here", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills B03 (FH10, M12): past 400 entries the knowledge page stops paging and says more is recorded, with the
    /// perspective-safe way to find the rest; nothing else on the page says the list was cut.
    /// </summary>
    [Fact]
    public async Task ReadKnowledge_MoreThanFourHundredEntries_SaysMoreIsRecorded()
    {
        await _s.Call("campaign", """{"action":"create","name":"Vast World","role":"dm","ruleset":"2024","slug":"vast"}""");
        for (var call = 0; call < 9; call++)
        {
            var ops = string.Join(", ", Enumerable.Range(call * 50 + 1, 50).Select(i =>
                $$"""{"op": "upsert", "kind": "location", "name": "P{{i:D3}}", "visibility": "party", "known_by": [{"who": "party"}]}"""));
            await _s.Call("campaign_write", $$"""{"campaign": "vast", "ops": [{{ops}}]}""");
        }

        var text = await _s.Read("campaign://vast/knowledge/party");

        Assert.DoesNotContain("Output cut at", text, StringComparison.Ordinal);
        Assert.EndsWith("_More is recorded than fits here; campaign_search with this perspective finds the rest by words._\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills C01 (FH10, M13): a deep URI may arrive percent-encoded (a client that escapes the ':' of a handle); the argument
    /// is unescaped before it is parsed, as the slug is, so it reads the same entry.
    /// </summary>
    [Fact]
    public async Task ReadEntity_PercentEncodedHandle_IsTheSameEntry()
    {
        await _s.Call("campaign", """{"action":"create","name":"Esc World","role":"dm","ruleset":"2024","slug":"esc"}""");
        await _s.Call("campaign_write", """{"campaign":"esc","ops":[{"op":"upsert","kind":"character","name":"Iron Guts","summary":"A dwarf."}]}""");

        Assert.Equal(await _s.Read("campaign://esc/entity/character:iron-guts"), await _s.Read("campaign://esc/entity/character%3Airon-guts"));
    }

    /// <summary>
    /// Kills C02 (FH10, M13): the entity resource is the author's record without the change history (campaign_history gives
    /// that), so a resource attached to a conversation is the entry, not its edit log.
    /// </summary>
    [Fact]
    public async Task ReadEntity_IsTheRecordWithoutItsHistory()
    {
        await _s.Call("campaign", """{"action":"create","name":"Res World","role":"dm","ruleset":"2024","slug":"res"}""");
        await _s.Call("campaign_write", """{"campaign":"res","ops":[{"op":"upsert","kind":"character","name":"Iron Guts","summary":"A dwarf."}]}""");
        await _s.Call("campaign_write", """{"campaign":"res","ops":[{"op":"upsert","ref":"character:iron-guts","summary":"A dwarf smith."}]}""");

        var text = await _s.Read("campaign://res/entity/character:iron-guts");

        Assert.Contains("A dwarf smith.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("History (newest first)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills U01 (FH10, M13): the session resource takes a session handle as results print it ("session:last"), as well as
    /// the word after "session:"; prefixing it again gave "session:session:last", which is refused.
    /// </summary>
    [Fact]
    public async Task ReadSession_FullSessionHandle_IsThatSession()
    {
        await _s.Call("campaign", """{"action":"create","name":"Handle World","role":"dm","ruleset":"2024","slug":"handle"}""");
        await _s.Call("campaign_session", """{"action":"record_past","campaign":"handle","session":1,"played_on":"2026-09-01","recap_md":"They met."}""");

        Assert.Equal(await _s.Read("campaign://handle/session/last"), await _s.Read("campaign://handle/session/session:last"));
    }

    /// <summary>
    /// Kills S04 (FH10, M13): the session page says when a session's attendance was not recorded (what the party learned in
    /// it then reads as "attendance not recorded" for every character), as campaign_session get does.
    /// </summary>
    [Fact]
    public async Task ReadSession_NoAttendanceRecorded_SaysSo()
    {
        await _s.Call("campaign", """{"action":"create","name":"Att World","role":"dm","ruleset":"2024","slug":"att"}""");
        await _s.Call("campaign_session", """{"action":"record_past","campaign":"att","session":1,"played_on":"2026-09-01","recap_md":"They met."}""");

        var text = await _s.Read("campaign://att/session/1");

        Assert.Contains("\n- **Attendance:** not recorded\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kills L01 (FH10, M25): session 100,000 is the largest session number (every session argument says 0 to 100,000),
    /// and the session resource, which reads it through a session handle, reads it rather than refusing it as too large.
    /// </summary>
    [Fact]
    public async Task ReadSession_SessionAtTheMaximum_IsRead()
    {
        await _s.Call("campaign", """{"action":"create","name":"Max World","role":"dm","ruleset":"2024","slug":"max"}""");
        await _s.Call("campaign_session", """{"action":"record_past","campaign":"max","session":100000,"played_on":"2026-09-01","recap_md":"The last night."}""");

        var text = await _s.Read("campaign://max/session/100000");

        Assert.StartsWith("# Max World: Session 100000 (`session:100000`)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadUnknownRulesUri_FailsExactlyAsWithoutTheCampaignReadHandler()
    {
        // The same server with the campaign list and read handlers taken out again: the SDK's own answer, as before them.
        var baseline = McpServerHarness.WithExtraTools(builder => builder.Services.Configure<McpServerOptions>(options =>
        {
            options.Handlers.ListResourcesHandler = null;
            options.Handlers.ReadResourceHandler = null;
        }));
        await baseline.InitializeAsync();
        const string uri = "rules://tables/no-such-table";
        McpProtocolException expected;
        try
        {
            expected = await Assert.ThrowsAsync<McpProtocolException>(() => baseline.Client.ReadResourceAsync(uri).AsTask());
        }
        finally
        {
            await baseline.DisposeAsync();
        }

        var actual = await Assert.ThrowsAsync<McpProtocolException>(() => _s.Server.Client.ReadResourceAsync(uri).AsTask());

        Assert.Equal((expected.ErrorCode, expected.Message), (actual.ErrorCode, actual.Message));
        Assert.Equal(McpErrorCode.ResourceNotFound, actual.ErrorCode);
        Assert.Contains($"Unknown resource URI: '{uri}'", actual.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRulesResource_WithTheCampaignHandlers_IsServedAsBefore()
    {
        var text = await _s.Read("rules://tables/cr-xp");

        Assert.StartsWith("# ", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("campaign://sky/bogus", "campaign sky has summary, threads, entity/<ref>, session/<n> and knowledge/<perspective>")]
    [InlineData("campaign://sky/entity/", "campaign sky has summary, threads, entity/<ref>, session/<n> and knowledge/<perspective>")]
    [InlineData("campaign://sky", "campaign resources are campaign://<slug>/summary, /threads, /entity/<ref>, /session/<n> or /knowledge/<perspective>; campaign://list lists the campaigns")]
    [InlineData("campaign://sky/", "campaign resources are campaign://<slug>/summary, /threads, /entity/<ref>, /session/<n> or /knowledge/<perspective>; campaign://list lists the campaigns")]
    [InlineData("campaign://nope/summary", "there is no campaign \"nope\"; campaign://list lists the campaigns")]
    public async Task ReadCampaignUri_NothingServesIt_IsNotFoundSayingWhatExists(string uri, string hint)
    {
        await _s.Create("Sky");

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => _s.Server.Client.ReadResourceAsync(uri).AsTask());

        Assert.Equal(McpErrorCode.ResourceNotFound, ex.ErrorCode);
        Assert.Contains($"Unknown resource URI: '{uri}': {hint}.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadDeepUris_AreReadableButNeverListed()
    {
        var sky = await _s.Create("Sky");
        _s.Apply(sky, WriteContext.Default, new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Flotsam", Visibility = "party", Summary = "A floating port." });
        new SessionWriter(_s.Database).RecordPast(sky, 1, "Arrival", recapMd: "We reached Flotsam.");

        var entity = await _s.Read("campaign://sky/entity/location:flotsam");
        var session = await _s.Read("campaign://sky/session/1");
        var last = await _s.Read("campaign://sky/session/last");
        var knowledge = await _s.Read("campaign://sky/knowledge/party");

        // The author's heading carries the e:<n> every view accepts beside the kind:slug (review U05, fix FQ14).
        Assert.Matches(@"\A# Flotsam \(`location:flotsam` · `e:\d+`\)\nlocation\n> A floating port\.\n", entity);
        Assert.StartsWith("# Sky: Arrival (`session:1`)\n\nplayed", session, StringComparison.Ordinal);
        Assert.Contains("## Recap\nWe reached Flotsam.\n", session, StringComparison.Ordinal);
        Assert.Equal(session, last);
        Assert.StartsWith("# Sky: what party knows\n_Perspective: party.", knowledge, StringComparison.Ordinal);
        var listed = (await _s.Server.Client.ListResourcesAsync()).Select(r => r.Uri).ToList();
        Assert.DoesNotContain(listed, u => u.Contains("/entity/", StringComparison.Ordinal) || u.Contains("/session/", StringComparison.Ordinal) ||
                                           u.Contains("/knowledge/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadSummary_IsTheCampaignToolsAuthorSummary()
    {
        await _s.Create("Sky");

        Assert.Equal(await _s.Call("campaign", """{"action":"summary"}"""), await _s.Read("campaign://sky/summary"));
    }

    [Fact]
    public async Task ReadEntity_AHandleNothingHas_IsTheReadersOwnMessage()
    {
        await _s.Create("Sky");

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => _s.Server.Client.ReadResourceAsync("campaign://sky/entity/character:nobody").AsTask());

        Assert.Contains("Invalid refs: refs item 1: \"character:nobody\": nothing in this campaign has that handle.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadKnowledge_UnknownPerspective_IsThePerspectiveMessage()
    {
        await _s.Create("Sky");

        var ex = await Assert.ThrowsAsync<McpProtocolException>(() => _s.Server.Client.ReadResourceAsync("campaign://sky/knowledge/villains").AsTask());

        Assert.Contains("perspective \"villains\" is not one of \"author\" (default), \"dm\", \"table\", \"party\", \"public\" or \"character:<slug>\".", ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadThreads_ListsOpenWorkFirst()
    {
        var sky = await _s.Create("Sky");
        _s.Apply(sky, WriteContext.Default,
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Find the lighthouse", Status = "active" },
            new CampaignOpSpec { Op = "upsert", Kind = "thread", Name = "Who sank the Gull", Status = "open" },
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Pay the harbour tax", Status = "resolved" });

        var text = await _s.Read("campaign://sky/threads");

        Assert.Equal(
            "# Sky: quests and threads\n\n## Open (2)\n- Find the lighthouse (`quest:find-the-lighthouse`) · quest · active\n" +
            "- Who sank the Gull (`thread:who-sank-the-gull`) · thread · open\n\n## Closed (1)\n- Pay the harbour tax (`quest:pay-the-harbour-tax`) · quest · resolved\n\n" +
            "campaign_get with a ref shows a quest's objectives, relations and facts.\n",
            text);
    }

    [Theory]
    [InlineData("quest")]
    [InlineData("thread")]
    public void Threads_TheGroupingStatuses_AreStatusesOfTheKind(string kind)
    {
        var statuses = DndMcp.Domain.Campaign.CampaignValues.Statuses.ByKind[kind];

        Assert.All(Formatting.Campaign.SummaryMarkdown.OpenStatuses.Append(Formatting.Campaign.SummaryMarkdown.DormantStatus),
            status => Assert.True(statuses.Contains(status), $"{status} is not a {kind} status"));
    }

    [Fact]
    public async Task ReadThreads_Largest_StaysUnderTheCap()
    {
        var sky = await _s.Create("Sky");
        for (var batch = 0; batch < 8; batch++)
        {
            _s.Apply(sky, WriteContext.Default, Enumerable.Range(1, 50).Select(i => new CampaignOpSpec
            {
                Op = "upsert", Kind = "thread", Status = "open",
                Name = $"An unresolved thread with a very long name that keeps going and going, number {batch:D1}{i:D2}",
            }).ToArray());
        }

        var text = await _s.Read("campaign://sky/threads");

        Assert.True(text.Length <= 24_000, $"threads is {text.Length} characters");
        Assert.StartsWith("# Sky: quests and threads\n\n## Open (300)\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadKnowledge_Largest_StaysUnderTheCap()
    {
        var sky = await _s.Create("Sky");
        for (var batch = 0; batch < 9; batch++)
        {
            _s.Apply(sky, WriteContext.Default, Enumerable.Range(1, 50).Select(i => new CampaignOpSpec
            {
                Op = "fact", Visibility = "party", Statement = $"Fact {batch}-{i}: " + string.Join(' ', Enumerable.Repeat("the sky remembers", 60)),
            }).ToArray());
        }

        var text = await _s.Read("campaign://sky/knowledge/party");

        Assert.True(text.Length <= 24_000, $"knowledge is {text.Length} characters");
        Assert.StartsWith("# Sky: what party knows\n_Perspective: party.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadList_MarksThisProcesssCurrentCampaignNotTheOneAnotherProcessMadeActive()
    {
        await _s.Create("Mine");
        await _s.Create("Theirs");
        await _s.Call("campaign", """{"action":"use","campaign":"mine"}""");
        using var otherProcess = new CampaignService(new DndMcpServerOptions { DataDirectory = _s.Server.DataDirectory },
            NullLogger<CampaignService>.Instance, NullLogger<CampaignDatabase>.Instance);
        otherProcess.Use("theirs");

        var text = await _s.Read("campaign://list");

        Assert.Contains("\n## Mine (`mine`) · the current campaign\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## Theirs (`theirs`)\n", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The session page budgets its recap as campaign_session get does (FH7, C06): a recap past
    /// <see cref="SessionMarkdown.MaxRecap"/> is cut at a line with a note saying how much of how much is shown and which
    /// call shows more, and everything after it (here the history) is still on the page.
    /// </summary>
    [Fact]
    public async Task ReadSession_LongRecap_IsBudgetedAndSaysWhereTheRestIs()
    {
        var sky = await _s.Create("Sky");
        var recap = string.Join('\n', Enumerable.Range(1, 700).Select(i => $"Line {i:D3} of the recap of the longest session anyone remembers."));
        new SessionWriter(_s.Database).RecordPast(sky, 1, "The long night", recapMd: recap);

        var text = await _s.Read("campaign://sky/session/1");

        Assert.True(text.Length <= 24_000, $"session is {text.Length} characters");
        Assert.Contains("\n## Recap\nLine 001 of the recap", text, StringComparison.Ordinal);
        Assert.Contains($"\n\n_… cut at 12,000 of {recap.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters; campaign_session {{\"action\": \"recap\", \"session\": 1, " +
                        "\"campaign\": \"sky\"} shows more._\n", text, StringComparison.Ordinal);
        Assert.Contains("## What changed in this session", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Output cut at", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// FH7 (C06): a one-paragraph recap of 40,000 characters (one line) shows its start. Appended whole, it was a line
    /// longer than the output cap, and the cap, which cut only at line breaks, dropped it whole: the page was "## Recap"
    /// and a note claiming the output was cut at 24,000 characters, in a result of about 200.
    /// </summary>
    [Fact]
    public async Task ReadSession_OneParagraphRecapOf40000Characters_ShowsTheStartOfTheRecap()
    {
        var sky = await _s.Create("Sky");
        var recap = string.Join(" ", Enumerable.Range(0, 2_100).Select(i => "the band played on" + (i % 9)));
        Assert.True(recap.Length >= 40_000 && !recap.Contains('\n', StringComparison.Ordinal));
        await _s.Call("campaign_session", $$"""{"action": "record_past", "campaign": "sky", "session": 1, "recap_md": "{{recap}}"}""");

        var text = await _s.Read("campaign://sky/session/1");

        Assert.Contains("\n## Recap\nthe band played on0 the band played on1", text, StringComparison.Ordinal);
        Assert.Contains($"_… cut at 12,000 of {recap.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} characters;", text, StringComparison.Ordinal);
        Assert.True(text.Length > 12_000, $"session is {text.Length} characters");
    }

    /// <summary>
    /// The largest session page (a hundred long live-log notes besides the recap) is still cut at the output cap, with the
    /// call that reads the session for the author. Each live-log note is an excerpt of at most 400 characters (FH7): a note
    /// may be 1,000, and a hundred whole ones would fill the page with the log alone.
    /// </summary>
    [Fact]
    public async Task ReadSession_Largest_IsCutAtTheCapAndSaysWhereElseToReadIt()
    {
        var sky = await _s.Create("Sky");
        await _s.Call("campaign_session", """{"action": "start", "campaign": "sky"}""");
        foreach (var call in Enumerable.Range(0, 2))
        {
            var notes = string.Join(", ", Enumerable.Range(1, 50).Select(i => "\"" + $"Note {call * 50 + i:D3} " + new string('n', 900) + "\""));
            await _s.Call("campaign_session", $$"""{"action": "log", "campaign": "sky", "notes": [{{notes}}]}""");
        }

        var text = await _s.Read("campaign://sky/session/1");

        Assert.True(text.Length <= 24_000, $"session is {text.Length} characters");
        Assert.EndsWith(
            "_Output cut at 24,000 characters; campaign_session {\"action\": \"get\", \"session\": 1, \"campaign\": \"sky\"} reads the session too._",
            text, StringComparison.Ordinal);
        Assert.Contains(" Note 001 nnn", text, StringComparison.Ordinal);
        var logLines = text.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal) && line.Contains(" Note ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(logLines);
        Assert.All(logLines, line => Assert.EndsWith("n…", line, StringComparison.Ordinal));
        Assert.All(logLines, line => Assert.True(line[(line.IndexOf(" Note ", StringComparison.Ordinal) + 1)..].Length <= 400, $"{line.Length} characters: {line[..60]}"));
    }

    /// <summary>
    /// FH8 (C09): a negative session number in the URI is refused as one, before it becomes a handle: read as a handle,
    /// "-1" is the slug "1", and the refusal named session 1, which here exists. The same with the full handle form.
    /// </summary>
    [Theory]
    [InlineData("campaign://sky/session/-1")]
    [InlineData("campaign://sky/session/session:-1")]
    public async Task ReadSession_NegativeNumber_IsRefusedAsOutOfRangeNeverAsSessionOne(string uri)
    {
        await _s.Create("Sky");
        await _s.Call("campaign_session", """{"action": "start", "campaign": "sky"}""");
        Assert.StartsWith("# Sky: Session 1 (`session:1`)", await _s.Read("campaign://sky/session/1"), StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _s.Server.Client.ReadResourceAsync(uri).AsTask());

        Assert.Equal("Request failed (remote): Invalid session: session is -1; it is a session number, 0 to 100000.", error.Message);
    }

    [Fact]
    public async Task ReadSession_MoreChangesThanListed_TheHistoryCallAsPrintedListsThemWhileAnotherCampaignIsCurrent()
    {
        var sky = await _s.Create("Sky");
        new SessionWriter(_s.Database).RecordPast(sky, 1, "One");
        for (var i = 1; i <= 21; i++)
        {
            _s.Apply(sky, WriteContext.For(1), new CampaignOpSpec { Op = "upsert", Kind = "character", Name = $"Sailor {i:D2}" });
        }

        var text = await _s.Read("campaign://sky/session/1");
        await _s.Create("Other");
        var call = System.Text.RegularExpressions.Regex.Match(text, "_… and 2 more; campaign_history (\\{[^}]*\\}) lists them all\\._").Groups[1].Value;
        var history = await _s.Call("campaign_history", call);

        Assert.Equal("{\"action\": \"since\", \"session\": 1, \"campaign\": \"sky\"}", call);
        Assert.StartsWith("# History of sky since session 1\n\n15 of 22 batches on this page", history, StringComparison.Ordinal);
    }

    private static long UserVersion(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)command.ExecuteScalar()!;
    }
}

/// <summary>
/// Invariant: the knowledge resource of every non-author perspective carries only what that view knows, under the names it
/// knows, with the banner (it is what a model reads before writing in a character's voice); the author's pages (summary,
/// threads, entity, session) render through the same readers and formatters as the tools.
///
/// <para>
/// Why it fails silently: the knowledge page is built from a different reader call than search and get, so a leak there
/// would pass every tool-level leak test.
/// </para>
/// </summary>
public sealed class CampaignResourceFixtureTests : IClassFixture<BelmakorServer>
{
    private readonly BelmakorServer _f;

    public CampaignResourceFixtureTests(BelmakorServer fixture)
    {
        _f = fixture;
    }

    [Theory]
    [MemberData(nameof(BelmakorServer.Perspectives), MemberType = typeof(BelmakorServer))]
    public async Task ReadKnowledge_NonAuthorPerspective_ContainsNothingThatViewCannotSee(string perspective)
    {
        var text = await _f.Read($"campaign://belmakor/knowledge/{perspective}");

        BelmakorServer.AssertClean(text, BelmakorServer.ForbiddenFor(perspective), $"knowledge of {perspective}");
        BelmakorServer.AssertClean(text, ["known by", "(author)", "via"], $"knowledge of {perspective}");
        Assert.StartsWith($"# Belmakor — sky-world: what {perspective} knows\n_Perspective: {perspective}", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BelmakorServer.VeilPerspectiveData), MemberType = typeof(BelmakorServer))]
    public async Task ReadKnowledge_VeilNonAuthorPerspective_NeverSpellsTheTrueName(string perspective)
    {
        var text = await _f.Read($"campaign://veil/knowledge/{perspective}");

        BelmakorServer.AssertClean(text, BelmakorServer.VeilForbidden, $"veil knowledge of {perspective}");
    }

    [Fact]
    public async Task ReadKnowledge_Party_ListsTheDisguisedItemUnderItsPartyNameAndTheFactsInThePartysPhrasing()
    {
        var text = await _f.Read("campaign://veil/knowledge/party");

        Assert.Matches(new System.Text.RegularExpressions.Regex(@"- the veiled woman \(`e:\d+`\) · character · met · S1\n"), text);
        Assert.Contains($"- `{BelmakorServer.VeilEnvoyFact}`: Someone killed the envoy. · suspects · S1\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadKnowledge_Belmakor_IncludesHisOwnAmbitionAndNamesOnlyHimInTheBanner()
    {
        var text = await _f.Read("campaign://belmakor/knowledge/character:belmakor");

        Assert.Equal(
            "_Perspective: character:belmakor (Belmakor Silverwind). Names are the ones this view knows; author-only text is withheld._",
            text.Split('\n')[1]);
        Assert.Contains("Belmakor intends to reclaim the blighted surface", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadKnowledge_Author_ListsWhoKnowsEachEntry()
    {
        var text = await _f.Read("campaign://belmakor/knowledge/author");

        Assert.DoesNotContain("_Perspective:", text, StringComparison.Ordinal);
        Assert.Contains("The old king's name is Keras. · known by dm", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadKnowledge_TypicalView_IsUnderTheTypicalBudget()
    {
        var text = await _f.Read("campaign://belmakor/knowledge/party");

        Assert.True(text.Length < 8_000, $"knowledge is {text.Length} characters");
    }

    [Fact]
    public async Task ReadEntity_IsTheAuthorsFullView()
    {
        var text = await _f.Read("campaign://belmakor/entity/character:old-king");

        Assert.Matches(@"\A# The Old King \(`character:old-king` · `e:\d+`\)\n", text);
        Assert.Contains("> [!secret]\n> Keras, Cole's old PC", text, StringComparison.Ordinal);
        Assert.Contains("## Knowledge\n", text, StringComparison.Ordinal);
        Assert.Contains("- same as `one-piece/character:keras`", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadSession_IsTheAuthorsViewWithAttendanceNotes()
    {
        var text = await _f.Read("campaign://belmakor/session/3");

        Assert.StartsWith("# Belmakor — sky-world: Kraken, the statue, the old king (`session:3`)\n\nplayed · 2026-08 (approx)\n", text, StringComparison.Ordinal);
        Assert.Contains("Belmakor Silverwind (`character:belmakor`) (Cole also ran the old king)", text, StringComparison.Ordinal);
        Assert.Contains("\n## What changed in this session\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadList_NamesEveryCampaignWithItsUrisAndTheCurrentOne()
    {
        var text = await _f.Read("campaign://list");

        Assert.Contains("\n## Belmakor — sky-world (`belmakor`) · the current campaign\nplayer campaign · 2014 rules · active\n" +
                        "- `campaign://belmakor/summary`: the state of play\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## One Piece (`one-piece`)\ndm campaign · 2024 rules · active\n", text, StringComparison.Ordinal);
        Assert.Contains("\n## The Veil (`veil`)\n", text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Invariant: the author's pages render every part the readers return (blocked work filed as open, dormant work on its own,
/// an absent attendee, the live log, every roll with the secret one marked), <c>/session/live</c> is the live session,
/// and no other view's knowledge page carries the golden campaign's author-only text.
///
/// <para>
/// Why it fails silently: each of those is a block only some sessions and threads have, so the Belmakor pages, which have
/// none of them, would stay green without it.
/// </para>
/// </summary>
public sealed class CampaignResourceGoldenTests : IClassFixture<GoldenCampaignServer>
{
    private readonly GoldenCampaignServer _g;

    public CampaignResourceGoldenTests(GoldenCampaignServer fixture)
    {
        _g = fixture;
    }

    [Fact]
    public async Task ReadThreads_FilesBlockedWorkAsOpenAndDormantWorkOnItsOwn()
    {
        var text = await _g.Read("campaign://golden/threads");

        Assert.Equal(
            "# Golden Isles: quests and threads\n\n## Open (2)\n- Pay the harbour tax (`quest:harbour-tax`) · quest · blocked\n" +
            "- Relight the lamp (`quest:relight-the-lamp`) · quest · active\n\n## Dormant (1)\n- The keeper's debt (`thread:keepers-debt`) · thread · dormant\n\n" +
            "campaign_get with a ref shows a quest's objectives, relations and facts.\n",
            text);
    }

    [Fact]
    public async Task ReadSession_Played_ShowsTheAbsentAttendeeWithTheNoteAndTheRecap()
    {
        var text = await _g.Read("campaign://golden/session/1");

        Assert.StartsWith(
            "# Golden Isles: Landfall (`session:1`)\n\nplayed\n- **Attendance:** Aria (`character:aria`), Brom (`character:brom`) (absent) (sick)\n\n" +
            "## Recap\nWe came ashore under the lighthouse.\nThe keeper watched us from the gallery.\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadSession_Live_ShowsTheLiveLogAndEveryRollForTheAuthor()
    {
        var text = await _g.Read("campaign://golden/session/live");

        Assert.StartsWith("# Golden Isles: Session 2 (`session:2`)\n\nlive · ", text, StringComparison.Ordinal);
        Assert.Matches(new System.Text.RegularExpressions.Regex("\n## Live log\n- \\S+ The tide turned against us\\.\n"), text);
        Assert.EndsWith("\n## Dice\n- Perception: `1d20+5>=15` = 18 (success)\n- Stealth: `1d20` = 7 (secret)\n", text, StringComparison.Ordinal);
        Assert.Equal(text, await _g.Read("campaign://golden/session/2"));
    }

    [Theory]
    [MemberData(nameof(GoldenCampaignServer.NonAuthorPerspectiveData), MemberType = typeof(GoldenCampaignServer))]
    public async Task ReadKnowledge_NonAuthorPerspective_ContainsNoAuthorOnlyText(string perspective)
    {
        var text = await _g.Read($"campaign://golden/knowledge/{perspective}");

        BelmakorServer.AssertClean(text, GoldenCampaignServer.AuthorOnly, $"golden knowledge of {perspective}");
    }
}
