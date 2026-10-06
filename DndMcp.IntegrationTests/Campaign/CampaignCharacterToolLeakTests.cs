using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The Belmakor world (<see cref="ScenarioBelmakorWorld"/>, a player campaign: its dm is a player view) with sheets written
/// through <c>campaign_character</c>: fixture A's party (contract §16) plus, on purpose, text that must never reach a
/// player: every private field of Belmakor's sheet carries a marker word, a subclass, a species, a homebrew class and an
/// effect name each carry a word only author text holds (the old king's true name, the Cage), the dead Tristan has a sheet,
/// and the old king (an NPC, not in the party) has one whose every text names his secret. "Harbour Master" is a party-known
/// NPC with no sheet, the shape every sheet a view may not see must read as. Read-only once built.
/// </summary>
public sealed class BelmakorSheetsWorld : IAsyncLifetime
{
    /// <summary>The marker words of Belmakor's private sheet fields (contract §7.4: never in a non-author view).</summary>
    public static readonly IReadOnlyList<string> PrivateMarkers =
        ["LINEAGEWORD", "BACKGROUNDWORD", "FEATWORD", "FEATUREWORD", "SPELLWORD", "LANGWORD", "RESOURCEWORD", "NOTESWORD", "ITEMWORD"];

    /// <summary>The author view's labels and values for those fields: none is printed outside it.</summary>
    public static readonly IReadOnlyList<string> AuthorOnlyText =
        ["Lineage", "player ", "source fixture", "Spell slots", "Resources", "sim_profile", "Scimitar", "Coins", "1,500 gp", "Hit Dice", "Abilities",
         "Saves", "Bladesong 4/4", "PB +", "Init +"];

    public ScenarioBelmakorWorld World { get; } = new();

    public McpServerHarness Server => World.Server;

    public async Task InitializeAsync()
    {
        await World.InitializeAsync();
        await World.Call("campaign_write", """
            {"campaign": "belmakor", "ops": [{"op": "upsert", "kind": "character", "name": "Harbour Master", "subtype": "npc", "visibility": "party"}]}
            """);
        await Sheet("character:belmakor", """
            {"player": "Cole", "ruleset": "2014", "species": "High Elf", "lineage": "LINEAGEWORD", "background": "BACKGROUNDWORD",
             "classes": [{"class": "wizard", "subclass": "Bladesinger", "level": 12}],
             "abilities": {"str": 11, "dex": 20, "con": 16, "int": 20, "wis": 13, "cha": 12}, "ac": 17, "max_hp": 110, "temp_hp": 7,
             "resources": [{"name": "Bladesong", "max": 4}, {"name": "RESOURCEWORD", "max": 1}],
             "feats": ["FEATWORD"], "features": ["FEATUREWORD: Contingency"], "spells": ["SPELLWORD"], "languages": ["LANGWORD"],
             "notes": "NOTESWORD: Keras's errand; reclaim the blighted surface.", "sheet_source": "fixture"}
            """);
        await World.Call("campaign_character", $$"""{"action": "update", "campaign": "belmakor", "character": "character:belmakor", "sim_profile": {{CharacterToolSetup.BelmakorProfile}} }""");
        await World.Call("campaign_character", """{"action": "inventory", "campaign": "belmakor", "character": "character:belmakor", "items": [{"item": "ITEMWORD statuette"}]}""");
        await World.Call("campaign_character", """{"action": "currency", "campaign": "belmakor", "character": "character:belmakor", "coins": {"gp": 1500}}""");
        await World.Call("campaign_character", """{"action": "condition", "campaign": "belmakor", "character": "character:belmakor", "add": ["poisoned"]}""");
        await Sheet("character:vars", """{"classes": [{"class": "ranger", "level": 6}, {"class": "rogue", "level": 6}]}""");
        await Sheet("character:aiden-ironstar", """{"classes": [{"class": "paladin", "subclass": "Oath of the Axiom Cage", "level": 6}, {"class": "sorcerer", "level": 6}]}""");
        await Sheet("character:ignis", """{"classes": [{"class": "bard", "level": 12}], "species": "Keras-born"}""");
        await Sheet("character:serif", """{"classes": [{"class": "artificer", "subclass": "Battle Smith", "level": 12, "hit_die": 8}]}""");
        await Sheet("character:torch", """{"classes": [{"class": "wizard", "level": 11}, {"class": "Keras's Apprentice", "level": 1, "hit_die": 6}], "max_hp": 74}""");
        await World.Call("campaign_character", """{"action": "condition", "campaign": "belmakor", "character": "character:torch", "add": ["Third Silence's chill"]}""");
        await Sheet("character:tristan", """{"level": 11, "max_hp": 80, "ac": 15}""");
        await Sheet("character:old-king", """
            {"classes": [{"class": "sorcerer", "subclass": "Axiom Cage warden", "level": 20}], "species": "Keras's lich-form", "max_hp": 300, "ac": 19,
             "notes": "Keras, imported from Cole's other campaign."}
            """);
    }

    public Task DisposeAsync() => World.DisposeAsync();

    private Task<string> Sheet(string character, string sheet) =>
        World.Call("campaign_character", $$"""{"action": "update", "campaign": "belmakor", "character": "{{character}}", "sheet": {{sheet}} }""");
}

/// <summary>
/// Invariant (contract §7.4, §0's leak rule): a non-author view of a character's sheet, through <c>campaign_character get</c>
/// (one character, or the default) and <c>campaign_get include: ["sheet"]</c>, is the public line of a current party member
/// it is shown, and nothing else: no private field (player, lineage, background, abilities, saves, resources, slots,
/// spells, feats, features, inventory, coins, XP, sim_profile, notes, source) and no label of one; a subclass, species,
/// homebrew class or effect name that holds a word only author text holds is left out ("an effect" for an effect); every
/// other character (an NPC with a sheet, a dead member) reads exactly like a character with no sheet; and a refusal for a
/// name the view may not use reads like one for a name nothing has. Swept for every player view of the Belmakor world,
/// the dm included (a player campaign's).
///
/// <para>
/// Why it fails silently: the author view is the default and prints everything, so a host that rendered the reader's
/// author record for a perspective, or passed the perspective nowhere, looks right to the author and leaks the player's
/// name, the planned Contingency and the old king's secret to the table. The repository's own tests pin the readers; these
/// pin that the tool and campaign_get ask them for the view and print only what they return.
/// </para>
/// </summary>
public sealed class CampaignCharacterToolLeakTests : IClassFixture<BelmakorSheetsWorld>
{
    /// <summary>Every player view of the Belmakor campaign (its dm is a player view: a player campaign).</summary>
    public static readonly IReadOnlyList<string> Perspectives = ["party", "table", "public", "dm", "character:belmakor", "character:vars", "character:torch"];

    private static readonly string[] Handles =
    [
        "character:belmakor", "belmakor", "character:vars", "character:aiden-ironstar", "character:ignis", "character:serif", "character:torch",
        "character:tristan", "character:old-king", "character:harbour-master", "character:keras", "keras", "one-piece/character:keras",
        "item:thing-he-wants", "character:nobody",
    ];

    private readonly BelmakorSheetsWorld _w;

    public CampaignCharacterToolLeakTests(BelmakorSheetsWorld world)
    {
        _w = world;
    }

    public static TheoryData<string> Views() => new(Perspectives);

    [Theory]
    [MemberData(nameof(Views))]
    public async Task Sweep_EverySheetReadAPlayerViewMakes_ContainsNothingForbiddenToIt(string perspective)
    {
        var outputs = await ReadAllAsync(perspective);

        foreach (var (label, output) in outputs)
        {
            var text = WithoutTypedHandle(output, label);
            ScenarioLeak.AssertBelmakorClean(text, perspective, _w.World.NameFact, $"{label} as {perspective}");
            ScenarioLeak.AssertClean(text, BelmakorSheetsWorld.PrivateMarkers, $"{label} as {perspective}");
            ScenarioLeak.AssertClean(text, BelmakorSheetsWorld.AuthorOnlyText, $"{label} as {perspective}");
            ScenarioLeak.AssertClean(text, ["lich-form", "warden", "Apprentice", "Oath of"], $"{label} as {perspective}");
        }

        Assert.True(perspective == "public" || outputs.Any(o => o.Text.Contains("— level 12 Wizard (Bladesinger), High Elf · HP 110/110 (+7 temp) · AC 17", StringComparison.Ordinal)),
            $"{perspective} never read Belmakor's public line");
    }

    [Fact]
    public async Task Sweep_TheAuthorReadsEveryMarkerTheSweepForbids()
    {
        // The teeth: the sweep passes trivially if the fixture never wrote what it checks for.
        var author = string.Join("\n", (await ReadAllAsync("author")).Select(o => o.Text));

        Assert.All([.. BelmakorSheetsWorld.PrivateMarkers, "Cole", "Keras-born", "Oath of the Axiom Cage", "Keras's Apprentice", "Third Silence's chill",
                    "Axiom Cage warden", "Keras's lich-form", "blighted", "Scimitar", "1,500 gp", "lineage LINEAGEWORD"],
            word => Assert.Contains(word, author, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("dm")]
    [InlineData("character:vars")]
    public async Task Get_PublicLine_LeavesOutWhatNamesAHiddenWordAndKeepsTheRest(string perspective)
    {
        var torch = await Text("campaign_character", $$"""{"action": "get", "character": "character:torch", "perspective": "{{perspective}}"}""");
        var aiden = await Text("campaign_character", $$"""{"action": "get", "character": "character:aiden-ironstar", "perspective": "{{perspective}}"}""");
        var ignis = await Text("campaign_character", $$"""{"action": "get", "character": "character:ignis", "perspective": "{{perspective}}"}""");
        var serif = await Text("campaign_character", $$"""{"action": "get", "character": "character:serif", "perspective": "{{perspective}}"}""");

        Assert.EndsWith("(`character:torch`) — level 12 Wizard 11 · HP 74/74 · an effect\n", torch, StringComparison.Ordinal);
        Assert.EndsWith("(`character:aiden-ironstar`) — level 12 Paladin 6 / Sorcerer 6\n", aiden, StringComparison.Ordinal);
        Assert.EndsWith("(`character:ignis`) — level 12 Bard\n", ignis, StringComparison.Ordinal);
        // The artificer is in neither SRD: a homebrew class, printed as typed once it passes the view-text check.
        Assert.EndsWith("(`character:serif`) — level 12 artificer (Battle Smith)\n", serif, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("party", "character:old-king")]
    [InlineData("party", "character:tristan")]
    [InlineData("dm", "character:old-king")]
    [InlineData("character:belmakor", "character:old-king")]
    [InlineData("table", "character:tristan")]
    public async Task Get_ASheetTheViewIsNotShown_ReadsExactlyLikeACharacterWithNoSheet(string perspective, string handle)
    {
        // An NPC's sheet and a dead member's: no line, no hint that a sheet exists, the same text as the harbour master,
        // who has none (contract §7.4).
        var other = await Text("campaign_character", $$"""{"action": "get", "character": "character:harbour-master", "perspective": "{{perspective}}"}""");
        var text = await Text("campaign_character", $$"""{"action": "get", "character": "{{handle}}", "perspective": "{{perspective}}"}""");
        var name = text["# ".Length..text.IndexOf(" — no sheet yet\n", StringComparison.Ordinal)];

        Assert.Equal(
            other.Replace("Harbour Master", "<name>", StringComparison.Ordinal).Replace("character:harbour-master", "<ref>", StringComparison.Ordinal),
            text.Replace(name, "<name>", StringComparison.Ordinal).Replace(handle, "<ref>", StringComparison.Ordinal));
        Assert.DoesNotContain("## Sheet", await Text("campaign_get", $$"""{"refs": ["{{handle}}"], "include": ["sheet"], "perspective": "{{perspective}}"}"""),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("party", "character:old-king", "The Old King")]
    [InlineData("party", "character:harbour-master", "Harbour Master")]
    [InlineData("dm", "character:tristan", "Tristan")]
    [InlineData("character:belmakor", "character:old-king", "The Old King")]
    public async Task Get_NoSheetInANonAuthorView_IsTheHeadingAndBannerOnly_NeverAWriteCall(string perspective, string handle, string name)
    {
        // Fix F1, L06: the "no sheet yet" of a view prints no update call: the model is the author, and following it for a
        // sheet the view is not shown (the old king's) patched that real sheet instead of making one.
        var text = await Text("campaign_character", $$"""{"action": "get", "character": "{{handle}}", "perspective": "{{perspective}}"}""");

        Assert.Matches($"^# {System.Text.RegularExpressions.Regex.Escape(name)} — no sheet yet\n_Perspective: [^\n]+_\n$", text);
        Assert.DoesNotContain("campaign_character {", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("public")]
    [InlineData("character:vars")]
    public async Task Get_ANameTheViewMayNotUse_IsRefusedLikeANameNothingHas(string perspective)
    {
        // "keras" is the old king's author alias; for a player view it must be a miss worded as one, never "not shown".
        var keras = await Text("campaign_character", $$"""{"action": "get", "character": "character:keras", "perspective": "{{perspective}}"}""");
        var nobody = await Text("campaign_character", $$"""{"action": "get", "character": "character:nobody", "perspective": "{{perspective}}"}""");

        Assert.StartsWith("An error occurred invoking 'campaign_character': ", keras, StringComparison.Ordinal);
        Assert.Equal(nobody.Replace("nobody", "<x>", StringComparison.Ordinal), keras.Replace("keras", "<x>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CampaignGet_IncludeSheetForTheParty_IsThePublicLineUnderTheEntrysHeading()
    {
        var text = await Text("campaign_get", """{"refs": ["character:belmakor"], "include": ["sheet"], "perspective": "party"}""");

        Assert.Contains(
            "\n## Sheet\n**Belmakor Silverwind** (`character:belmakor`) — level 12 Wizard (Bladesinger), High Elf · HP 110/110 (+7 temp) · AC 17 · poisoned\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CampaignGet_IncludeSheetForTheAuthor_IsTheWholeSheet()
    {
        var text = await Text("campaign_get", """{"refs": ["character:belmakor"], "include": ["sheet"]}""");

        Assert.Contains("\n## Sheet\nlevel 12 Wizard (Bladesinger), 2014\n`character:belmakor` · player Cole · High Elf · lineage LINEAGEWORD", text,
            StringComparison.Ordinal);
        Assert.Contains("- **sim_profile:** Belmakor, level 12; attacks Scimitar", text, StringComparison.Ordinal);
    }

    // Every sheet read a perspective can make: get by default and by each handle, and campaign_get's sheet include for each.
    private async Task<List<(string Label, string Text)>> ReadAllAsync(string perspective)
    {
        var view = perspective == "author" ? string.Empty : $", \"perspective\": \"{perspective}\"";
        var outputs = new List<(string, string)> { ("get", await Text("campaign_character", $$"""{"action": "get"{{view}}}""")) };
        foreach (var handle in Handles.Append(_w.World.ThingRef))
        {
            outputs.Add(($"get {handle}", await Text("campaign_character", $$"""{"action": "get", "character": "{{handle}}"{{view}}}""")));
            outputs.Add(($"campaign_get {handle}", await Text("campaign_get", $$"""{"refs": ["{{handle}}"], "include": ["sheet"]{{view}}}""")));
        }

        return outputs;
    }

    // A refusal's echo of the handle the caller typed ("character: "character:keras": nothing by that handle"), taken out:
    // the caller's own words, not something the view was shown (ScenarioLeak.WithoutTypedHandle's rule for campaign_get).
    internal static string WithoutTypedHandle(string text, string label)
    {
        var handle = label[(label.IndexOf(' ', StringComparison.Ordinal) + 1)..];
        return label.Contains(' ', StringComparison.Ordinal) && text.StartsWith("An error occurred", StringComparison.Ordinal)
            ? text.Replace($"\"{handle}\"", "\"…\"", StringComparison.Ordinal)
            : text;
    }

    // A result's text, success or refusal (a refusal is output too).
    private async Task<string> Text(string tool, string argumentsJson) =>
        _w.Server.SingleText(await _w.Server.CallToolJsonAsync(tool, argumentsJson.Contains("\"campaign\"", StringComparison.Ordinal)
            ? argumentsJson
            : argumentsJson.Insert(1, "\"campaign\": \"belmakor\", ")));
}

/// <summary>
/// The One Piece world (<see cref="ScenarioOnePieceWorld"/>, a DM campaign: its dm is the author) at its baseline, with
/// fixture B's party (contract §16, FIX §3-§4.1): the fishman monk joins the party, Björn, the monk and the Dragon Slayer
/// get sheets, and The Nester (a restricted NPC with fixture B's secret) gets one whose every text names it. Björn carries
/// an effect named for the twist and the Dragon Slayer a species that names the Cage, so the public line has something to
/// leave out. Read-only once built.
/// </summary>
public sealed class OnePieceSheetsWorld : IAsyncLifetime
{
    /// <summary>Fixture B's added secret words (FIX §5.2) and the sheet texts that name the twist.</summary>
    public static readonly IReadOnlyList<string> Added = ["Nester", "unmourned", "Void-touched", "whisper", "Cage-born", "the-nester"];

    public McpServerHarness Server { get; } = new();

    public ScenarioOnePieceWorld World { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Server.InitializeAsync();
        World = await ScenarioOnePieceWorld.BuildAsync(Server);
        await World.Call("campaign_write", """
            {"campaign": "one-piece", "ops": [
              {"op": "upsert", "kind": "character", "name": "The fishman monk", "slug": "fishman-monk", "subtype": "pc", "visibility": "party"},
              {"op": "link", "from": "character:fishman-monk", "rel": "member_of", "to": "faction:the-party"},
              {"op": "upsert", "kind": "character", "name": "The Nester", "slug": "the-nester", "subtype": "npc", "visibility": "restricted", "status": "alive",
               "secret_md": "Void-touched; what nests here is close to what the Wraiths are: the drowned, unmourned dead."}]}
            """);
        await Sheet("character:bjorn-mountainfell", """
            {"classes": [{"class": "barbarian", "subclass": "Path of the Totem Warrior", "level": 8}],
             "abilities": {"str": 18, "dex": 14, "con": 16, "int": 8, "wis": 12, "cha": 10}, "ac": 15, "max_hp": 85,
             "resources": [{"name": "Rage", "max": 4, "recharge": "short_rest_one"}], "xp": 34000, "notes": "Baal's seal, the founders' fleet."}
            """);
        await World.Call("campaign_character", """{"action": "condition", "campaign": "one-piece", "character": "character:bjorn-mountainfell", "add": ["Baal's whisper", "poisoned"]}""");
        await Sheet("character:fishman-monk", """
            {"classes": [{"class": "monk", "subclass": "Order of the Deep Sea", "level": 8}], "abilities": {"str": 10, "dex": 16, "con": 14, "wis": 16},
             "ac": 16, "max_hp": 59, "xp": 34000}
            """);
        await Sheet("character:dragon-slayer", """
            {"classes": [{"class": "Dragon Slayer", "subclass": "Amethyst", "level": 8, "hit_die": 10}], "species": "Cage-born", "ac": 16, "max_hp": 68,
             "xp": 34000}
            """);
        await Sheet("character:the-nester", """
            {"level": 10, "species": "Void-touched aberration", "max_hp": 150, "ac": 17, "notes": "The Nester: the drowned, unmourned dead."}
            """);
    }

    public Task DisposeAsync() => Server.DisposeAsync();

    private Task<string> Sheet(string character, string sheet) =>
        World.Call("campaign_character", $$"""{"action": "update", "campaign": "one-piece", "character": "{{character}}", "sheet": {{sheet}} }""");
}

/// <summary>
/// Invariant (contract §7.4) in a DM campaign, where the dm is the author: the party, the table, the public and each PC
/// read the party's list (<c>get</c> with no character) as the lines of the members they are shown and nothing about the
/// rest, every member's public line without what names the twist, and nothing of The Nester's sheet, by any handle; the
/// author (and the dm) reads every member, every sheet and the secret words.
///
/// <para>
/// Why it fails silently: a list form built from the author's roster prints a line (or a "no sheet" row, or a count) for a
/// character a view must not know exists; only a DM campaign has a list form, so the Belmakor sweep cannot see it.
/// </para>
/// </summary>
public sealed class CampaignCharacterToolDmLeakTests : IClassFixture<OnePieceSheetsWorld>
{
    /// <summary>The player views of a DM campaign.</summary>
    public static readonly IReadOnlyList<string> Perspectives =
        ["party", "table", "public", "character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk"];

    private static readonly string[] Handles =
    [
        "character:bjorn-mountainfell", "bjorn", "character:dragon-slayer", "character:fishman-monk", "character:the-nester", "the-nester",
        "character:protector", "character:nadar", "faction:the-party",
    ];

    private readonly OnePieceSheetsWorld _w;

    public CampaignCharacterToolDmLeakTests(OnePieceSheetsWorld world)
    {
        _w = world;
    }

    public static TheoryData<string> Views() => new(Perspectives);

    [Theory]
    [MemberData(nameof(Views))]
    public async Task Sweep_EverySheetReadAPlayerViewMakes_ContainsNothingForbiddenToIt(string perspective)
    {
        var outputs = await ReadAllAsync(perspective);

        foreach (var (label, output) in outputs)
        {
            var text = CampaignCharacterToolLeakTests.WithoutTypedHandle(output, label);
            ScenarioLeak.AssertClean(text, ScenarioOnePieceLeakTests.Forbidden, $"{label} as {perspective}");
            ScenarioLeak.AssertClean(text, OnePieceSheetsWorld.Added, $"{label} as {perspective}");
            ScenarioLeak.AssertClean(text, ["Rage 4/4", "34,000", "Spell slots", "Resources", "player ", "no sheet yet: campaign_character"],
                $"{label} as {perspective}");
        }
    }

    [Fact]
    public async Task Sweep_TheAuthorReadsEverySecretTheSweepForbids()
    {
        var author = string.Join("\n", (await ReadAllAsync("author")).Select(o => o.Text));

        Assert.All(["The Nester", "Void-touched aberration", "unmourned", "Baal's whisper", "Cage-born", "Rage 4/4", "34,000"],
            word => Assert.Contains(word, author, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:fishman-monk")]
    public async Task Get_ListForAPlayerView_IsTheLinesOfTheMembersItIsShownAndNothingElse(string perspective)
    {
        var text = await Text($$"""{"action": "get", "perspective": "{{perspective}}"}""");
        var lines = text.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).ToList();

        Assert.StartsWith("# One Piece: the party's sheets\n_Perspective: ", text, StringComparison.Ordinal);
        // The Dragon Slayer's slug is not its name's, so a player view is shown it by e:<n> (the Phase 6 ref rule).
        Assert.Equal(3, lines.Count);
        Assert.Equal(
            "- **Björn Mountainfell** (`character:bjorn-mountainfell`) — level 8 Barbarian (Path of the Totem Warrior) · HP 85/85 · AC 15 · an effect, poisoned",
            lines[0]);
        Assert.Matches(@"^- \*\*The amethyst Dragon Slayer\*\* \(`e:\d+`\) — level 8 Dragon Slayer \(Amethyst\) · HP 68/68 · AC 16$", lines[1]);
        Assert.Equal("- **The fishman monk** (`character:fishman-monk`) — level 8 Monk (Order of the Deep Sea) · HP 59/59 · AC 16", lines[2]);
    }

    [Fact]
    public async Task Get_ListForTheDm_IsTheAuthorsList()
    {
        // In a DM campaign the dm is the author (contract §3.2): every member with the author's line, no banner.
        var dm = await Text("""{"action": "get", "perspective": "dm"}""");

        Assert.Equal(await Text("""{"action": "get"}"""), dm);
        Assert.Contains("Baal's whisper", dm, StringComparison.Ordinal);
    }

    // Every sheet read a perspective can make: the list form, get by each handle, and campaign_get's sheet include for each.
    private async Task<List<(string Label, string Text)>> ReadAllAsync(string perspective)
    {
        var view = perspective == "author" ? string.Empty : $", \"perspective\": \"{perspective}\"";
        var outputs = new List<(string, string)> { ("get", await Text($$"""{"action": "get"{{view}}}""")) };
        foreach (var handle in Handles)
        {
            outputs.Add(($"get {handle}", await Text($$"""{"action": "get", "character": "{{handle}}"{{view}}}""")));
            outputs.Add(($"campaign_get {handle}",
                _w.Server.SingleText(await _w.Server.CallToolJsonAsync("campaign_get", $$"""{"campaign": "one-piece", "refs": ["{{handle}}"], "include": ["sheet"]{{view}}}"""))));
        }

        return outputs;
    }

    private async Task<string> Text(string argumentsJson) =>
        _w.Server.SingleText(await _w.Server.CallToolJsonAsync("campaign_character", argumentsJson.Insert(1, "\"campaign\": \"one-piece\", ")));
}
