using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// A real server (its own data directory) holding the Belmakor fixture of understand-belmakor.md §2 ("the old king" /
/// "the thing he wants" / the Axiom Cage), the One Piece campaign its cross-links point at, and a small "veil" campaign
/// whose secret is an entity's true NAME (see <see cref="BuildVeil"/>), built through the write path's services over the
/// server's own <see cref="CampaignService"/>, with Belmakor made the current campaign.
///
/// <para>
/// It is the same data as the write path's <c>BelmakorFixture</c> (DndMcp.Tests cannot be referenced from here), so the
/// tool-level leak tests run against the shape the repository goldens were written for: an NPC whose true name is an
/// author alias, an item known only by a party phrasing, secret text on a thread, a restricted secret only Belmakor
/// knows, a fact only the dm knows, and cross-links to another campaign. The class fixture is read-only by convention:
/// tests that write build their own server.
/// </para>
/// </summary>
public sealed class BelmakorServer : IAsyncLifetime
{
    /// <summary>What no non-author view of this campaign may ever contain (case-insensitive), whatever the tool.</summary>
    public static readonly IReadOnlyList<string> AlwaysForbidden =
        ["Axiom", "Cage", "Baal", "Third Silence", "one-piece", "One Piece", "Cole", "imported", "[!secret]"];

    /// <summary>Every non-author perspective the tests sweep.</summary>
    public static readonly IReadOnlyList<string> NonAuthorPerspectives =
        ["party", "table", "dm", "public", "character:belmakor", "character:vars", "character:serif", "character:tristan"];

    /// <summary>
    /// What no non-author view of the veil campaign may contain: the veiled woman's true name (also as the slug spelling
    /// it), her summary, her author alias, the author-only queen she serves and the relation to her, and the statement of
    /// the restricted fact the party knows only in its own phrasing.
    /// </summary>
    public static readonly IReadOnlyList<string> VeilForbidden =
        ["Mirelle", "Duskbane", "assassin", "Nightglass", "Ysolde", "serves", "poisoned", "[!secret]", "Cole"];

    /// <summary>Every non-author perspective of the veil campaign.</summary>
    public static readonly IReadOnlyList<string> VeilPerspectives = ["party", "table", "dm", "public", "character:aria"];

    public static TheoryData<string> VeilPerspectiveData() => new(VeilPerspectives);

    public McpServerHarness Server { get; } = new();

    public CampaignService Campaigns => Server.Services.GetRequiredService<CampaignService>();

    public CampaignRow Belmakor { get; private set; } = null!;

    public CampaignRow OnePiece { get; private set; } = null!;

    public CampaignRow Veil { get; private set; } = null!;

    public static TheoryData<string> Perspectives() => new(NonAuthorPerspectives);

    /// <summary>
    /// The strings <paramref name="perspective"/> must not see: <see cref="AlwaysForbidden"/>, "Keras" for everyone but the
    /// dm (who knows f:2, "The old king's name is Keras."), and Belmakor's ambition for everyone but him and the dm.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenFor(string perspective)
    {
        var list = new List<string>(AlwaysForbidden);
        if (perspective != "dm")
        {
            list.Add("Keras");
        }

        if (perspective is not ("dm" or "character:belmakor"))
        {
            list.AddRange(["reclaim", "blighted", "outstrips"]);
        }

        return list;
    }

    /// <summary>Asserts <paramref name="text"/> contains none of <paramref name="forbidden"/> (case-insensitive).</summary>
    public static void AssertClean(string text, IEnumerable<string> forbidden, string because)
    {
        foreach (var word in forbidden)
        {
            Assert.True(text.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0, $"{because}: the output contains \"{word}\":\n{text}");
        }
    }

    public async Task InitializeAsync()
    {
        await Server.InitializeAsync();
        (Belmakor, OnePiece) = Build(Campaigns.Database);
        Veil = BuildVeil(Campaigns.Database);
        Campaigns.Use("belmakor");
    }

    public Task DisposeAsync() => Server.DisposeAsync();

    /// <summary>The text of a successful tool call.</summary>
    public async Task<string> Call(string tool, string argumentsJson) => Server.SuccessText(await Server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>The text of one resource read.</summary>
    public async Task<string> Read(string uri)
    {
        var result = await Server.Client.ReadResourceAsync(uri);
        return Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
    }

    /// <summary>Builds both campaigns through the write services (see the class summary); returns (belmakor, one-piece).</summary>
    public static (CampaignRow Belmakor, CampaignRow OnePiece) Build(CampaignDatabase database)
    {
        var store = new CampaignStore(database);
        var writer = new CampaignWriter(database);
        var knowledge = new KnowledgeWriter(database);
        var sessions = new SessionWriter(database);
        void Apply(CampaignRow campaign, params CampaignOpSpec[] ops) => writer.Apply(campaign, ops, WriteContext.Default);

        var onePiece = store.Create("One Piece", "dm", "2024", slug: "one-piece").Campaign;
        Apply(onePiece,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Keras", Subtype = "npc", Visibility = "party", BodyMd = "Centuries ago Keras sealed Baal inside the Axiom Cage." },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "item", Name = "The Axiom Cage", Slug = "axiom-cage", Subtype = "artifact", Visibility = "author",
                Aliases = [new AliasSpec { Alias = "The Third Silence", Visibility = "author" }],
            });

        var c = store.Create("Belmakor — sky-world", "player", "2014", slug: "belmakor", partyName: "The party", partySlug: "party",
            myCharacter: "Belmakor Silverwind", myCharacterSlug: "belmakor",
            settings: Data("{\"dm_pronouns\": \"she/her\", \"table_style\": \"near-TPK\"}")).Campaign;

        static CampaignOpSpec Pc(string name, string? slug = null, string status = "alive", string? alias = null, string? summary = null) => new()
        {
            Op = "upsert", Kind = "character", Name = name, Slug = slug, Subtype = "pc", Visibility = "party", Status = status, Summary = summary,
            Aliases = alias is null ? null : [new AliasSpec { Alias = alias, Visibility = "party" }],
        };

        static CampaignOpSpec Link(string from, string rel, string to) => new() { Op = "link", From = from, Rel = rel, To = to };

        Apply(c,
            new CampaignOpSpec
            {
                Op = "upsert", Ref = "character:belmakor", Status = "alive", Summary = "High Elf Bladesinger, Mythril Zeppelin frontman",
                Aliases = [new AliasSpec { Alias = "Belmakor", Visibility = "party" }, new AliasSpec { Alias = "Silverwind", Visibility = "party" }],
            },
            Pc("Vars Nocturne", "vars"),
            Pc("Ignis"),
            Pc("Serif", summary: "joined after Tristan died"),
            Pc("Lieutenant James Torch", "torch", alias: "Torch"),
            Pc("Aiden Ironstar", alias: "Ironstar"),
            Pc("Tristan", status: "dead", summary: "died protecting Sky on the ocean job"),
            Link("character:vars", "member_of", "faction:party"),
            Link("character:ignis", "member_of", "faction:party"),
            Link("character:serif", "member_of", "faction:party"),
            Link("character:torch", "member_of", "faction:party"),
            Link("character:aiden-ironstar", "member_of", "faction:party"),
            Link("character:tristan", "member_of", "faction:party"),
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "The Old King", Slug = "old-king", Subtype = "npc", Visibility = "party", Status = "unknown",
                Aliases = [new AliasSpec { Alias = "the sorcerer king", Visibility = "party" }, new AliasSpec { Alias = "Keras", Visibility = "author" }],
                Summary = "Ancient, Void-controlled sorcerer king; revealed by a crumbling statue; fought and beaten; sent the party on an errand.",
                SecretMd = "Keras, Cole's old PC from his other campaign, imported at Cole's request; Cole ran him in the fight.",
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "item", Name = "The thing the old king wants", Slug = "thing-he-wants", Subtype = "artifact", Visibility = "party",
                Aliases =
                [
                    new AliasSpec { Alias = "the thing he wants", Visibility = "party" }, new AliasSpec { Alias = "Axiom Cage", Visibility = "author" },
                    new AliasSpec { Alias = "the Cage", Visibility = "author" },
                ],
                Summary = "Something more dangerous than anything they've seen; bring it back to him; don't use it.",
                SecretMd = "It is the Axiom Cage (Cole-tier).",
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "thread", Name = "The old king's errand", Slug = "old-kings-errand", Visibility = "party", Status = "open",
                SecretMd = "Cole-tier: it's the Axiom Cage.",
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "secret", Name = "Belmakor's ambition", Slug = "belmakors-ambition", Visibility = "restricted",
                BodyMd = "Reclaim the blighted surface, make new habitable land, become a legend who outstrips his parents.",
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "rule", Subtype = "reveal_rule", Name = "No name, no timespan for the old king", Slug = "old-king-no-name-no-timespan",
                Visibility = "author", Data = Data("{\"forbidden_terms\": [\"Keras\"], \"forbidden_patterns\": [\"<n>-year-old\", \"<n> years\"]}"),
            },
            new CampaignOpSpec { Op = "upsert", Kind = "arc", Name = "The Iron Guts job", Slug = "iron-guts-job", Visibility = "party", Confidence = "confirmed" },
            new CampaignOpSpec { Op = "link", From = "character:old-king", Rel = "same_as", To = "one-piece/character:keras", Note = "Same being: Cole's old PC, imported as the Void-controlled sorcerer king" },
            new CampaignOpSpec { Op = "link", From = "item:thing-he-wants", Rel = "same_as", To = "one-piece/item:axiom-cage", Note = "The errand's object is the Cage" },
            Link("character:old-king", "wants", "item:thing-he-wants"));

        static AttendanceSpec? At(string character, bool present = true, string? note = null) => new() { Character = character, Present = present, Note = note };
        sessions.RecordPast(c, 1, "The Iron Guts job", precision: "unknown", recapMd: "The ocean job. Tristan fell to the void octopus.", attendance:
        [
            At("character:belmakor"), At("character:ignis"), At("character:tristan", note: "died here"),
            At("character:serif", false, "not yet in the party"),
        ]);
        sessions.RecordPast(c, 2, "The airship / T-rex", precision: "unknown", attendance: [At("character:belmakor"), At("character:serif")]);
        sessions.RecordPast(c, 3, "Kraken, the statue, the old king", playedOn: "2026-08", precision: "approx",
            recapMd: "A crumbling statue revealed the old king. We fought him and won; he sent us to fetch the thing he wants.", attendance:
            [
                At("character:vars"), At("character:belmakor", note: "Cole also ran the old king"), At("character:aiden-ironstar"),
                At("character:ignis"), At("character:serif"), At("character:torch"),
            ]);

        Apply(c,
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The thing the old king wants fetched is the Axiom Cage.", FactType = "secret", Visibility = "author",
                EstablishedSession = 3, About = ["item:thing-he-wants", "character:old-king"],
            },
            new CampaignOpSpec { Op = "fact", Statement = "The old king's name is Keras.", FactType = "secret", Visibility = "restricted", About = ["character:old-king"] },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "After the fight the old king sent the party to fetch something more dangerous than anything they had seen, bring it back to him, and not use it.",
                CanonStatus = "played", Visibility = "party", EstablishedSession = 3, About = ["thread:old-kings-errand", "item:thing-he-wants", "character:old-king"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The party took \"don't use it\" as \"don't even touch it.\"", FactType = "belief", Truth = "unknown",
                CanonStatus = "played", Visibility = "party", EstablishedSession = 3, About = ["thread:old-kings-errand", "character:old-king"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Belmakor intends to reclaim the blighted surface, make new habitable land, and become a legend who outstrips his parents.",
                FactType = "secret", Visibility = "restricted", About = ["character:belmakor", "secret:belmakors-ambition"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Belmakor keeps a Contingency: Polymorph into a T-rex when he drops low.", CanonStatus = "played",
                Visibility = "party", EstablishedSession = 2, About = ["character:belmakor"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Tristan died on the ocean job, dragged into the deep by the void octopus while protecting Sky.", CanonStatus = "played",
                Visibility = "party", EstablishedSession = 1, About = ["character:tristan"],
            });

        void Record(string target, string who, string state, string? knownAs = null, int? session = null, string? via = null, string? how = null) =>
            knowledge.Record(c, [target], [new KnowerSpec { Who = who, State = state, KnownAs = knownAs, Session = session, Via = via, How = how }], WriteContext.Default);

        Record("character:old-king", "character:belmakor", "met", "the old king", 3, how: "witnessed");
        Record("character:old-king", "party", "met", "the old king", 3, how: "witnessed");
        Record("character:old-king", "author", "knows", "Keras", how: "backstory");
        Record("item:thing-he-wants", "party", "aware", "the thing he wants", 3, "character:old-king", "told");
        Record("item:thing-he-wants", "character:belmakor", "aware", "the thing he wants", 3, "character:old-king", "told");
        Record("item:thing-he-wants", "author", "knows", "the Axiom Cage", how: "backstory");
        Record("f:1", "character:belmakor", "unaware");
        Record("f:1", "party", "unaware");
        Record("f:2", "party", "unaware");
        Record("f:2", "dm", "knows", how: "told");
        Record("f:3", "party", "knows", session: 3, via: "character:old-king", how: "told");
        Record("f:4", "party", "believes", session: 3, how: "deduced");
        Record("f:5", "character:belmakor", "knows", how: "backstory");
        Record("f:5", "dm", "knows");
        Record("f:5", "party", "unaware");
        Record("secret:belmakors-ambition", "character:belmakor", "knows", how: "backstory");
        Record("f:6", "character:belmakor", "knows", how: "backstory");
        Record("f:6", "party", "knows", session: 2, how: "witnessed");
        Record("f:7", "party", "knows", session: 1, how: "witnessed");
        return (store.TryGet("belmakor")!, store.TryGet("one-piece")!);
    }

    /// <summary>
    /// The veil campaign (player, 2024): the party met "the veiled woman" in session 1, whose true name, Mirelle Duskbane,
    /// is the entity's own name (so the disguise rule, not an alias filter, is what hides it, and her slug
    /// <c>mirelle-duskbane</c> spells it); her summary says what she is; she serves an author-only queen; the party knows
    /// the restricted "Mirelle Duskbane poisoned the envoy." only as its own suspicion "Someone killed the envoy.". She is
    /// seen at a party-visible market, so the market's relations point back at her.
    /// </summary>
    public static CampaignRow BuildVeil(CampaignDatabase database)
    {
        var store = new CampaignStore(database);
        var writer = new CampaignWriter(database);
        var knowledge = new KnowledgeWriter(database);
        var veil = store.Create("The Veil", "player", "2024", slug: "veil", myCharacter: "Aria", myCharacterSlug: "aria").Campaign;
        writer.Apply(veil,
        [
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Mirelle Duskbane", Subtype = "npc", Visibility = "party", Status = "alive",
                Summary = "Secretly the queen's assassin.", BodyMd = "Mirelle Duskbane trained in the Nightglass school.",
                SecretMd = "Cole means her to turn on the queen.", Aliases = [new AliasSpec { Alias = "Nightglass", Visibility = "author" }],
            },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Queen Ysolde", Subtype = "npc", Visibility = "author" },
            new CampaignOpSpec { Op = "link", From = "character:mirelle-duskbane", Rel = "serves", To = "character:queen-ysolde", Visibility = "party" },
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "The Rose Market", Visibility = "party" },
            new CampaignOpSpec { Op = "link", From = "character:mirelle-duskbane", Rel = "seen_at", To = "location:the-rose-market", Visibility = "party" },
        ], WriteContext.Default);
        new SessionWriter(database).RecordPast(veil, 1, "The black rose", recapMd: "A veiled woman watched us from the market.",
            attendance: [new AttendanceSpec { Character = "character:aria" }]);
        var facts = writer.Apply(veil,
        [
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The veiled woman left a black rose at the market.", CanonStatus = "played", EstablishedSession = 1,
                Visibility = "party", About = ["character:mirelle-duskbane", "location:the-rose-market"],
            },
            new CampaignOpSpec { Op = "fact", Statement = "Mirelle Duskbane poisoned the envoy.", FactType = "secret", About = ["character:mirelle-duskbane"] },
        ], WriteContext.Default);
        VeilRoseFact = facts.Applied[0].Ref;
        VeilEnvoyFact = facts.Applied[1].Ref;
        knowledge.Record(veil, ["character:mirelle-duskbane"], [new KnowerSpec { Who = "party", State = "met", KnownAs = "the veiled woman", Session = 1 }],
            WriteContext.Default);
        knowledge.Record(veil, [VeilRoseFact], [new KnowerSpec { Who = "party", Session = 1 }], WriteContext.Default);
        knowledge.Record(veil, [VeilEnvoyFact], [new KnowerSpec { Who = "party", State = "suspects", KnownAs = "Someone killed the envoy.", Session = 1 }],
            WriteContext.Default);
        return store.TryGet("veil")!;
    }

    /// <summary>The veil's party fact "The veiled woman left a black rose at the market." (fact handles are numbered across campaigns).</summary>
    public static string VeilRoseFact { get; private set; } = string.Empty;

    /// <summary>The veil's restricted "Mirelle Duskbane poisoned the envoy.", known to the party only as "Someone killed the envoy.".</summary>
    public static string VeilEnvoyFact { get; private set; } = string.Empty;

    /// <summary>A JSON object as the <c>data</c> / <c>settings</c> dictionaries take it.</summary>
    public static Dictionary<string, JsonElement> Data(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}

/// <summary>
/// One real server with its own empty data directory, for tests that write: the tools' calls, the campaign service they
/// share, the write services for seeding (the tools under test here only read, undo and manage campaigns; entries are
/// written the way campaign_write would write them, through the same services), and a count of the
/// <c>notifications/resources/list_changed</c> messages the client received.
///
/// <para>
/// Why a server per test rather than a shared one: a write changes what every later read sees (the current campaign, the
/// change log, the resource list), so tests sharing a server would pass or fail by the order xUnit runs them in.
/// </para>
/// </summary>
public sealed class CampaignTestServer : IAsyncDisposable
{
    private readonly TaskCompletionSource _firstListChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IAsyncDisposable? _subscription;
    private int _listChanged;

    private CampaignTestServer()
    {
    }

    public McpServerHarness Server { get; } = new();

    public CampaignService Campaigns => Server.Services.GetRequiredService<CampaignService>();

    public CampaignDatabase Database => Campaigns.Database;

    /// <summary>Where this server keeps campaigns.db (it may not exist yet).</summary>
    public string DatabasePath => Path.Combine(Server.DataDirectory, "campaigns.db");

    /// <summary>How many resources/list_changed notifications the client has received.</summary>
    public int ListChangedCount => Volatile.Read(ref _listChanged);

    public static async Task<CampaignTestServer> StartAsync()
    {
        var server = new CampaignTestServer();
        await server.Server.InitializeAsync();
        server._subscription = server.Server.Client.RegisterNotificationHandler(
            NotificationMethods.ResourceListChangedNotification,
            (_, _) =>
            {
                Interlocked.Increment(ref server._listChanged);
                server._firstListChanged.TrySetResult();
                return ValueTask.CompletedTask;
            });
        return server;
    }

    /// <summary>Waits (at most 10 s) for the first list_changed notification.</summary>
    public Task FirstListChanged() => _firstListChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>
    /// Waits (at most 10 s) until <paramref name="count"/> list_changed notifications have arrived, then for a quiet period
    /// of <see cref="ListChangedQuietPeriod"/>, and asserts exactly that many did. The server sends a notification before the
    /// call's result, but the client hands notifications to its handler on its own schedule, so one can still be on its way
    /// when the result is read: asserting as soon as the count was reached missed a late extra notification (a dry run that
    /// announced a change it never made passed this way).
    /// </summary>
    public async Task WaitForListChanged(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ListChangedCount < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await Task.Delay(ListChangedQuietPeriod);
        Assert.Equal(count, ListChangedCount);
    }

    /// <summary>How long <see cref="WaitForListChanged"/> waits for a notification that must not come.</summary>
    public static readonly TimeSpan ListChangedQuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>The text of a successful tool call.</summary>
    public async Task<string> Call(string tool, string argumentsJson) => Server.SuccessText(await Server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>The text of a tool error.</summary>
    public async Task<string> Error(string tool, string argumentsJson) => Server.ErrorText(await Server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>The text of one resource read.</summary>
    public async Task<string> Read(string uri)
    {
        var result = await Server.Client.ReadResourceAsync(uri);
        return Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
    }

    /// <summary>Creates a campaign through the tool and returns its row.</summary>
    public async Task<CampaignRow> Create(string name, string role = "dm", string ruleset = "2024", string? slug = null)
    {
        var slugArgument = slug is null ? string.Empty : $$""","slug":"{{slug}}" """;
        await Call("campaign", $$"""{"action":"create","name":"{{name}}","role":"{{role}}","ruleset":"{{ruleset}}"{{slugArgument}}}""");
        return new CampaignStore(Database).TryGet(slug ?? name)!;
    }

    /// <summary>Applies ops through the write path (what campaign_write does) and returns the result.</summary>
    public WriteResult Apply(CampaignRow campaign, WriteContext context, params CampaignOpSpec[] ops) =>
        new CampaignWriter(Database).Apply(campaign, ops, context);

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }

        await Server.DisposeAsync();
    }
}

/// <summary>
/// Invariant: the fixture every campaign tool test reads is the golden shape (understand-belmakor.md §2) as the tools see
/// it: the old king exists for the author under his true alias, the party knows him only as "the old king", and the thing
/// he wants is the author's Axiom Cage.
///
/// <para>
/// Why it fails silently: every leak test below passes trivially if the fixture never wrote the secret it checks for (a
/// renamed op field, a refused op swallowed by a helper), so the author's view must be shown to contain each forbidden
/// string before the other views are shown not to.
/// </para>
/// </summary>
public sealed class CampaignToolFixtureTests : IClassFixture<BelmakorServer>
{
    private readonly BelmakorServer _fixture;

    public CampaignToolFixtureTests(BelmakorServer fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("character:old-king", "Keras")]
    [InlineData("character:old-king", "Cole's old PC")]
    [InlineData("item:thing-he-wants", "Axiom Cage")]
    [InlineData("thread:old-kings-errand", "Cole-tier")]
    [InlineData("secret:belmakors-ambition", "blighted")]
    public async Task Get_AuthorView_SeesEachSecretTheLeakTestsForbid(string handle, string secret)
    {
        var text = await _fixture.Call("campaign_get", $$"""{"refs":["{{handle}}"],"include":["relations","facts","knowledge"],"detail":"full"}""");

        Assert.Contains(secret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_AuthorView_VeilShowsEveryStringItsLeakTestsForbid()
    {
        var text = await _fixture.Call("campaign_get",
            $$"""{"refs":["character:mirelle-duskbane","{{BelmakorServer.VeilEnvoyFact}}"],"include":["relations","facts"],"detail":"full","campaign":"veil"}""");

        Assert.All(BelmakorServer.VeilForbidden, word => Assert.Contains(word, text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_AuthorView_ShowsTheCrossLinkToTheOtherCampaign()
    {
        var text = await _fixture.Call("campaign_get", """{"refs":["character:old-king"]}""");

        Assert.Contains("same as `one-piece/character:keras` (Keras, campaign one-piece)", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list marks the campaign calls without campaign use, and shows a player campaign's character handle (FH5, U03):
    /// the slug comes from the name given at create, so a fresh session that guessed it spent a call on a refusal. A DM
    /// campaign has no character of the user's.
    /// </summary>
    [Fact]
    public async Task Resolve_FixtureMadeBelmakorCurrent_CallsWithoutCampaignUseIt()
    {
        var text = await _fixture.Call("campaign", """{"action":"list"}""");

        Assert.Contains("| Campaign | Name | Role | My character | Ruleset | Status | |\n", text, StringComparison.Ordinal);
        Assert.Contains("| `belmakor` | Belmakor — sky-world | player | `character:belmakor` | 2014 | active | calls use this one |", text, StringComparison.Ordinal);
        Assert.Contains("| `one-piece` | One Piece | dm | — | 2024 | active |  |", text, StringComparison.Ordinal);
    }
}

/// <summary>
/// A real server holding one campaign, "golden" (player, 2024), that has one of every part the read formatters render: a
/// live session with a live log and dice (one roll secret), a played session with a recap and an absent attendee, a
/// running clock shown to players (with a front, fill and on-fill text) and an author-only one, a quest with objectives
/// (one of them author-only), a blocked quest and a dormant thread, a region with a tagged child location, a secret with a
/// gated fact, a clue and a route, a proposed invention (an entity and a fact), an author-only location, and the party at
/// a known location on a known in-game date. Built through the write path's services; read-only by convention.
///
/// <para>
/// Why a fixture of its own: the Belmakor fixture is shaped by the leak goldens and has none of these parts, so without
/// this campaign a formatter could drop a clock, an objective or a gate line (or print one for the wrong view) and every
/// test would stay green.
/// </para>
/// </summary>
public sealed class GoldenCampaignServer : IAsyncLifetime
{
    /// <summary>
    /// Author-only text of the golden campaign that no other view may contain: the author-only clock, the visible clock's
    /// on-fill text and player flag, the author-only objective, the author-only location, the secret (its name, body, gated
    /// fact and the clue link to it), the proposed invention, the secret roll and the live log.
    /// </summary>
    public static readonly IReadOnlyList<string> AuthorOnly =
    [
        "Cult Awakens", "cult-awakens", "cult strikes", "lower town floods", "shown to players", "Betray the keeper", "Hidden Cove", "hidden-cove",
        "keeper's heir", "keepers-heir", "heir to the Drowned", "his true blood", "clue_for", "Captain Vell", "sailed with the keeper", "Stealth",
        "tide turned", "[!secret]",
    ];

    /// <summary>Every non-author perspective the golden campaign's leak sweep runs (dm is not the author's view in a player campaign).</summary>
    public static readonly IReadOnlyList<string> NonAuthorPerspectives = ["party", "table", "dm", "public", "character:aria", "character:brom"];

    public static TheoryData<string> NonAuthorPerspectiveData() => new(NonAuthorPerspectives);

    public McpServerHarness Server { get; } = new();

    public CampaignService Campaigns => Server.Services.GetRequiredService<CampaignService>();

    public CampaignRow Golden { get; private set; } = null!;

    /// <summary>The clue "Salt stains mark the keeper's boots." (party-visible, known to the party since session 1).</summary>
    public string ClueFact { get; private set; } = string.Empty;

    /// <summary>The gated secret fact "The keeper is the heir to the Drowned Court.".</summary>
    public string GatedFact { get; private set; } = string.Empty;

    /// <summary>The proposed fact "Vell once sailed with the keeper.".</summary>
    public string ProposedFact { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await Server.InitializeAsync();
        var database = Campaigns.Database;
        var store = new CampaignStore(database);
        var writer = new CampaignWriter(database);
        var knowledge = new KnowledgeWriter(database);
        var sessions = new SessionWriter(database);
        var c = store.Create("Golden Isles", "player", "2024", slug: "golden", partyName: "The Crew", myCharacter: "Aria", myCharacterSlug: "aria").Campaign;
        writer.Apply(c,
        [
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Brom", Subtype = "pc", Visibility = "party", Status = "alive" },
            new CampaignOpSpec { Op = "link", From = "character:brom", Rel = "member_of", To = "faction:the-crew" },
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "The Archipelago", Slug = "archipelago", Visibility = "party", Tags = ["region", "sea"] },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "location", Name = "The Lighthouse", Slug = "lighthouse", Visibility = "party", Parent = "location:archipelago",
                Tags = ["landmark"], Summary = "A tower of white stone.", BodyMd = "The lamp room holds a great lens.",
            },
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Hidden Cove", Visibility = "author", Summary = "Where the cult meets." },
            new CampaignOpSpec { Op = "upsert", Kind = "front", Name = "The Drowned Court", Slug = "drowned-court", Visibility = "party" },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "clock", Name = "The Tide Rises", Slug = "tide-rises", Visibility = "party",
                Clock = new ClockSpec { Segments = 6, Filled = 2, Unit = "segment", Front = "front:drowned-court", ShownToPlayers = true, OnFillMd = "The lower town floods." },
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "clock", Name = "The Cult Awakens", Slug = "cult-awakens", Visibility = "author",
                Clock = new ClockSpec { Segments = 4, Filled = 1, Unit = "day", ShownToPlayers = false, OnFillMd = "The cult strikes." },
            },
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Relight the lamp", Slug = "relight-the-lamp", Visibility = "party", Status = "active" },
            new CampaignOpSpec { Op = "objective", Ref = "quest:relight-the-lamp", Text = "Find oil for the lamp", Status = "open", Progress = 1, ProgressMax = 3, Visibility = "party" },
            new CampaignOpSpec { Op = "objective", Ref = "quest:relight-the-lamp", Text = "Betray the keeper", Status = "open", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Pay the harbour tax", Slug = "harbour-tax", Visibility = "party", Status = "blocked" },
            new CampaignOpSpec { Op = "upsert", Kind = "thread", Name = "The keeper's debt", Slug = "keepers-debt", Visibility = "party", Status = "dormant" },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "secret", Name = "The keeper's heir", Slug = "keepers-heir", Visibility = "restricted",
                BodyMd = "The keeper hides his true blood.",
            },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Captain Vell", Slug = "vell", Visibility = "party", CanonStatus = "proposed" },
        ], WriteContext.Default);
        var facts = writer.Apply(c,
        [
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Salt stains mark the keeper's boots.", Visibility = "party", CanonStatus = "played",
                Links = [new FactLinkSpec { Ref = "location:lighthouse" }, new FactLinkSpec { Ref = "secret:keepers-heir", Role = "clue_for" }],
            },
            new CampaignOpSpec { Op = "fact", Statement = "Vell once sailed with the keeper.", CanonStatus = "proposed", About = ["character:vell"] },
        ], WriteContext.Default);
        ClueFact = facts.Applied[0].Ref;
        ProposedFact = facts.Applied[1].Ref;
        GatedFact = writer.Apply(c,
        [
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The keeper is the heir to the Drowned Court.", FactType = "secret", About = ["secret:keepers-heir"],
                Gate = new GateSpec { After = [ClueFact], Routes = [new RouteSpec { Id = "boots", Clues = [ClueFact], MinClues = 1 }] },
            },
        ], WriteContext.Default).Applied[0].Ref;

        sessions.RecordPast(c, 1, "Landfall", recapMd: "We came ashore under the lighthouse.\nThe keeper watched us from the gallery.", attendance:
        [
            new AttendanceSpec { Character = "character:aria" },
            new AttendanceSpec { Character = "character:brom", Present = false, Note = "sick" },
        ]);
        knowledge.Record(c, ["location:lighthouse"], [new KnowerSpec { Who = "party", State = "met", Session = 1 }], WriteContext.Default);
        knowledge.Record(c, [ClueFact], [new KnowerSpec { Who = "party", Session = 1 }], WriteContext.Default);
        store.Update(c, new CampaignUpdate { CurrentLocation = "location:lighthouse", CurrentIngame = "Day 12" });
        sessions.Start(c, 2, attendance: [new AttendanceSpec { Character = "character:aria" }, new AttendanceSpec { Character = "character:brom" }]);
        sessions.Log(c, ["The tide turned against us."]);
        var dice = new DiceLogWriter(database);
        dice.TryLog(c.Id, [new DiceLogRoll("1d20+5>=15", "Perception", 18, true, "{}")], secret: false);
        dice.TryLog(c.Id, [new DiceLogRoll("1d20", "Stealth", 7, null, "{}")], secret: true);
        Golden = store.TryGet("golden")!;
        Campaigns.Use("golden");
    }

    public Task DisposeAsync() => Server.DisposeAsync();

    /// <summary>The text of a successful tool call.</summary>
    public async Task<string> Call(string tool, string argumentsJson) => Server.SuccessText(await Server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>The text of one resource read.</summary>
    public async Task<string> Read(string uri)
    {
        var result = await Server.Client.ReadResourceAsync(uri);
        return Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
    }
}
