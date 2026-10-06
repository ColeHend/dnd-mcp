using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The calls the exit-criteria scenarios make, over one real server: every one goes through the MCP client, the argument
/// guard, the call-tool filter and the formatter, exactly as the model's calls do, and returns the text the model reads.
///
/// <para>
/// Why the scenarios build their worlds through the tools and never through the repository services: the repository-level
/// scenarios (DndMcp.Tests/CampaignScenarios) already prove the services; what they cannot prove is that the tools expose
/// everything those worlds need (a gate on a fact op, a known_as on a knower, an attendance note, a session to file a
/// batch under) and that the text the model reads keeps every rule. A world seeded behind the tools' back would keep
/// passing while, say, <c>campaign_write</c> silently dropped <c>known_by</c>.
/// </para>
/// </summary>
internal static class ScenarioCalls
{
    private static readonly Regex BatchPattern = new("Batch `([0-9a-f-]{36})`", RegexOptions.CultureInvariant);

    /// <summary>A successful tool call's text (the test fails, with the server log, when the call is an error).</summary>
    public static async Task<string> Call(McpServerHarness server, string tool, string argumentsJson) =>
        server.SuccessText(await server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>A tool call that must be refused: the error text the model reads.</summary>
    public static async Task<string> Fail(McpServerHarness server, string tool, string argumentsJson) =>
        server.ErrorText(await server.CallToolJsonAsync(tool, argumentsJson));

    /// <summary>One resource's text.</summary>
    public static async Task<string> Read(McpServerHarness server, string uri)
    {
        var result = await server.Client.ReadResourceAsync(uri);
        return Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
    }

    /// <summary>The batch id a write result printed (the test fails when it printed none).</summary>
    public static string BatchId(string writeResult)
    {
        var match = BatchPattern.Match(writeResult);
        Assert.True(match.Success, $"The write printed no batch id:\n{writeResult}");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// The handles a write result's table names, in op order (<see cref="CampaignWriteSetup.Refs"/>): fact and entity
    /// numbers are shared by every campaign in the file, so the scenarios never guess an <c>f:&lt;n&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> Refs(string writeResult) => CampaignWriteSetup.Refs(writeResult);
}

/// <summary>
/// The Belmakor world of understand-belmakor.md §2 ("the old king" / "the thing he wants" / the Axiom Cage), built only
/// through the MCP tools in a server of its own: <c>campaign create</c> (the One Piece campaign that holds the
/// cross-link endpoints, then Belmakor as a player campaign whose PC joins the party), <c>campaign_write</c> (the cast, the
/// old king with his author alias "Keras", the item known to the party only as "the thing he wants", the thread with its
/// secret text, the restricted ambition, the no-name-no-timespan reveal rule, the <c>same_as</c> links, every fact, the
/// level supersession, the Contingency made party-visible in session 2), <c>campaign_session record_past</c> (sessions 1-3
/// with attendance: Serif absent from session 1, Aiden not listed there), each fact's knowledge rows as its <c>known_by</c>
/// and <c>campaign_knowledge record</c> for the entities' rows (every row of §2.5, author rows included).
///
/// <para>
/// Differences from §2, each deliberate: the PC is created as "Belmakor" (so his handle is <c>character:belmakor</c>, the
/// perspective the goldens use) and renamed "Belmakor Silverwind" by an upsert (the slug stays, contract §3.1);
/// the party faction is "The party" (<c>faction:the-party</c>: the tool takes no party slug); the reveal rule's data drops
/// the INVENTED <c>applies_to</c> key (reveal rules take forbidden terms and patterns, preferred terms, until and a note)
/// and gains the preferred term "an old king" and a note; f:3 and f:4 are also about the old king, and Belmakor knows his
/// own restricted ambition entity (the repository scenario's additions, which rows 29-30 need); the old king
/// <c>wants</c> the item (a party relation whose far end the party knows only by its disguise).
/// </para>
/// <para>
/// Read-only once built, by convention: a test class takes it as <see cref="IClassFixture{TFixture}"/> (its own server
/// and data directory); a test that writes builds a world of its own.
/// </para>
/// </summary>
public sealed class ScenarioBelmakorWorld : IAsyncLifetime
{
    /// <summary>The old king's true name as the author records it: an author alias, and the text of the fact only the dm was told.</summary>
    public const string NameStatement = "The old king's name is Keras.";

    /// <summary>Belmakor's ambition, as the restricted fact states it (he and the dm know it; the party is recorded unaware).</summary>
    public const string AmbitionStatement =
        "Belmakor intends to reclaim the blighted surface, make new habitable land, and become a legend who outstrips his parents.";

    /// <summary>The world in a server of its own (the class-fixture form: <see cref="InitializeAsync"/> starts it and builds the world).</summary>
    public ScenarioBelmakorWorld()
        : this(new McpServerHarness())
    {
    }

    // The world in a server someone else made (BuildAsync): Phase 7's combat scenarios give theirs a scripted dice roller.
    private ScenarioBelmakorWorld(McpServerHarness server)
    {
        Server = server;
    }

    /// <summary>The server the world lives in (its own data directory, deleted on dispose).</summary>
    public McpServerHarness Server { get; }

    /// <summary>
    /// Builds the world in <paramref name="server"/>, already initialized and holding no campaign, which the caller owns
    /// (starts and disposes): the form for a server with test-only services, such as Phase 7's scripted dice roller
    /// (<c>McpServerHarness.WithExtraTools</c>), which the class-fixture form cannot take.
    /// </summary>
    public static async Task<ScenarioBelmakorWorld> BuildAsync(McpServerHarness server)
    {
        var world = new ScenarioBelmakorWorld(server);
        await world.BuildAsync();
        return world;
    }

    /// <summary>"The thing the old king wants fetched is the Axiom Cage." (author visibility; Belmakor and the party unaware).</summary>
    public string AxiomFact { get; private set; } = string.Empty;

    /// <summary>"The old king's name is Keras." (restricted; the party unaware, the dm told).</summary>
    public string NameFact { get; private set; } = string.Empty;

    /// <summary>The errand (played, party; the party knows it from session 3).</summary>
    public string ErrandFact { get; private set; } = string.Empty;

    /// <summary>"The party took "don't use it" as "don't even touch it."" (a belief the party holds).</summary>
    public string TouchFact { get; private set; } = string.Empty;

    /// <summary>Belmakor's ambition (restricted; he and the dm know it, the party is recorded unaware).</summary>
    public string AmbitionFact { get; private set; } = string.Empty;

    /// <summary>The Contingency (party from session 2; Belmakor knew it before).</summary>
    public string ContingencyFact { get; private set; } = string.Empty;

    /// <summary>Tristan's death (the party knows it from session 1, where Serif was absent and Aiden not listed).</summary>
    public string TristanFact { get; private set; } = string.Empty;

    /// <summary>"Belmakor is level 11." (superseded by <see cref="Level12Fact"/>).</summary>
    public string Level11Fact { get; private set; } = string.Empty;

    /// <summary>"Belmakor is level 12 (as of August 2026)." (party; established in session 3; supersedes <see cref="Level11Fact"/>).</summary>
    public string Level12Fact { get; private set; } = string.Empty;

    /// <summary>
    /// The <c>e:&lt;n&gt;</c> ref every non-author view is shown in place of <c>item:thing-he-wants</c> (the item is
    /// disguised: the name it is known by is not its own, and its slug is its author's). Found through the author's own gets,
    /// so no perspective read decides what the perspective tests expect.
    /// </summary>
    public string ThingRef { get; private set; } = string.Empty;

    /// <summary>A successful call's text (<see cref="ScenarioCalls.Call"/>).</summary>
    public Task<string> Call(string tool, string argumentsJson) => ScenarioCalls.Call(Server, tool, argumentsJson);

    /// <summary>A refused call's error text (<see cref="ScenarioCalls.Fail"/>).</summary>
    public Task<string> Fail(string tool, string argumentsJson) => ScenarioCalls.Fail(Server, tool, argumentsJson);

    /// <summary>One resource's text (<see cref="ScenarioCalls.Read"/>).</summary>
    public Task<string> Read(string uri) => ScenarioCalls.Read(Server, uri);

    /// <summary>Starts the server and builds the world through the tools.</summary>
    public async Task InitializeAsync()
    {
        await Server.InitializeAsync();
        await BuildAsync();
    }

    /// <summary>Stops the server and deletes its data directory.</summary>
    public Task DisposeAsync() => Server.DisposeAsync();

    private async Task BuildAsync()
    {
        await Call("campaign", """{"action": "create", "name": "One Piece", "role": "dm", "ruleset": "2024", "slug": "one-piece"}""");
        await Call("campaign_write", """
            {"campaign": "one-piece", "reason": "The cross-link endpoints",
             "ops": [{"op": "upsert", "kind": "character", "name": "Keras", "subtype": "npc", "visibility": "party",
                      "body_md": "Centuries ago Keras sealed Baal inside the Axiom Cage."},
                     {"op": "upsert", "kind": "item", "name": "The Axiom Cage", "slug": "axiom-cage", "subtype": "artifact", "visibility": "author",
                      "aliases": [{"alias": "The Third Silence", "visibility": "author"}]}]}
            """);

        await Call("campaign", """
            {"action": "create", "name": "Belmakor — sky-world", "role": "player", "ruleset": "2014", "slug": "belmakor",
             "party_name": "The party", "my_character": "Belmakor", "settings": {"dm_pronouns": "she/her", "table_style": "near-TPK"}}
            """);
        await Call("campaign_write", """
            {"campaign": "belmakor", "reason": "The cast, the old king, the thing he wants",
             "ops": [
              {"op": "upsert", "ref": "character:belmakor", "name": "Belmakor Silverwind", "status": "alive",
               "summary": "High Elf Bladesinger, Mythril Zeppelin frontman",
               "aliases": [{"alias": "Belmakor", "visibility": "party"}, {"alias": "Silverwind", "visibility": "party"}]},
              {"op": "upsert", "kind": "character", "name": "Vars Nocturne", "slug": "vars", "subtype": "pc", "visibility": "party", "status": "alive"},
              {"op": "upsert", "kind": "character", "name": "Ignis", "subtype": "pc", "visibility": "party", "status": "alive"},
              {"op": "upsert", "kind": "character", "name": "Serif", "subtype": "pc", "visibility": "party", "status": "alive",
               "summary": "joined after Tristan died"},
              {"op": "upsert", "kind": "character", "name": "Lieutenant James Torch", "slug": "torch", "subtype": "pc", "visibility": "party",
               "status": "alive", "aliases": [{"alias": "Torch", "visibility": "party"}]},
              {"op": "upsert", "kind": "character", "name": "Aiden Ironstar", "subtype": "pc", "visibility": "party", "status": "alive",
               "aliases": [{"alias": "Ironstar", "visibility": "party"}]},
              {"op": "upsert", "kind": "character", "name": "Tristan", "subtype": "pc", "visibility": "party", "status": "dead",
               "summary": "died protecting Sky on the ocean job"},
              {"op": "link", "from": "character:vars", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:ignis", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:serif", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:torch", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:aiden-ironstar", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:tristan", "rel": "member_of", "to": "faction:the-party"},
              {"op": "upsert", "kind": "character", "name": "The Old King", "slug": "old-king", "subtype": "npc", "visibility": "party", "status": "unknown",
               "aliases": [{"alias": "the sorcerer king", "visibility": "party"}, {"alias": "Keras", "visibility": "author"}],
               "summary": "Ancient, Void-controlled sorcerer king; revealed by a crumbling statue; fought and beaten; sent the party on an errand.",
               "secret_md": "Keras, Cole's old PC from his other campaign, imported at Cole's request; Cole ran him in the fight."},
              {"op": "upsert", "kind": "item", "name": "The thing the old king wants", "slug": "thing-he-wants", "subtype": "artifact", "visibility": "party",
               "aliases": [{"alias": "the thing he wants", "visibility": "party"}, {"alias": "Axiom Cage", "visibility": "author"},
                           {"alias": "the Cage", "visibility": "author"}],
               "summary": "Something more dangerous than anything they've seen; bring it back to him; don't use it.",
               "secret_md": "It is the Axiom Cage (Cole-tier)."},
              {"op": "upsert", "kind": "thread", "name": "The old king's errand", "slug": "old-kings-errand", "visibility": "party", "status": "open",
               "secret_md": "Cole-tier: it's the Axiom Cage."},
              {"op": "upsert", "kind": "secret", "name": "Belmakor's ambition", "slug": "belmakors-ambition", "visibility": "restricted",
               "body_md": "Reclaim the blighted surface, make new habitable land, become a legend who outstrips his parents."},
              {"op": "upsert", "kind": "rule", "subtype": "reveal_rule", "name": "No name, no timespan for the old king",
               "slug": "old-king-no-name-no-timespan", "visibility": "author",
               "data": {"forbidden_terms": ["Keras"], "forbidden_patterns": ["<n>-year-old", "<n> years"], "preferred_terms": ["an old king"],
                        "note": "Belmakor never learns his name or age."}},
              {"op": "upsert", "kind": "arc", "name": "The Iron Guts job", "slug": "iron-guts-job", "visibility": "party", "confidence": "confirmed"},
              {"op": "upsert", "kind": "arc", "name": "Mind flayers and the Void", "slug": "mind-flayers", "visibility": "party", "confidence": "reconstructed"},
              {"op": "link", "from": "character:old-king", "rel": "same_as", "to": "one-piece/character:keras",
               "note": "Same being: Cole's old PC, imported as the Void-controlled sorcerer king"},
              {"op": "link", "from": "item:thing-he-wants", "rel": "same_as", "to": "one-piece/item:axiom-cage", "note": "The errand's object is the Cage"},
              {"op": "link", "from": "character:old-king", "rel": "wants", "to": "item:thing-he-wants"}]}
            """);

        await Call("campaign_session", """
            {"campaign": "belmakor", "action": "record_past", "session": 1, "title": "The Iron Guts job", "precision": "unknown",
             "recap_md": "The ocean job. Tristan fell to the void octopus.",
             "attendance": [{"character": "character:belmakor"}, {"character": "character:ignis"},
                            {"character": "character:tristan", "note": "died here"},
                            {"character": "character:serif", "present": false, "note": "not yet in the party; recruited via Ignis's fliers after Tristan died"}]}
            """);
        await Call("campaign_session", """
            {"campaign": "belmakor", "action": "record_past", "session": 2, "title": "The airship / T-rex", "precision": "unknown",
             "attendance": [{"character": "character:belmakor"}, {"character": "character:serif", "note": "peeled off to save another wizard"}]}
            """);
        await Call("campaign_session", """
            {"campaign": "belmakor", "action": "record_past", "session": 3, "title": "Kraken, the statue, the old king", "played_on": "2026-08",
             "precision": "approx", "recap_md": "A crumbling statue revealed the old king. We fought him and won; he sent us to fetch the thing he wants.",
             "attendance": [{"character": "character:vars"}, {"character": "character:belmakor", "note": "Cole also ran the old king"},
                            {"character": "character:aiden-ironstar"}, {"character": "character:ignis"}, {"character": "character:serif"},
                            {"character": "character:torch"}]}
            """);

        // Each fact with who knows it (known_by: the rows of §2.5 k7-k22), as a recap batch writes them.
        var facts = ScenarioCalls.Refs(await Call("campaign_write", """
            {"campaign": "belmakor", "reason": "What sessions 1-3 established, and who knows it",
             "ops": [
              {"op": "fact", "statement": "The thing the old king wants fetched is the Axiom Cage.", "fact_type": "secret", "visibility": "author",
               "established_session": 3, "about": ["item:thing-he-wants", "character:old-king"],
               "known_by": [{"who": "character:belmakor", "state": "unaware"}, {"who": "party", "state": "unaware"}, {"who": "author", "state": "knows"}]},
              {"op": "fact", "statement": "The old king's name is Keras.", "fact_type": "secret", "visibility": "restricted", "about": ["character:old-king"],
               "known_by": [{"who": "party", "state": "unaware"}, {"who": "author", "state": "knows"}, {"who": "dm", "state": "knows", "how": "told"}]},
              {"op": "fact", "statement": "After the fight the old king sent the party to fetch something more dangerous than anything they had seen, bring it back to him, and not use it.",
               "canon_status": "played", "visibility": "party", "established_session": 3,
               "about": ["thread:old-kings-errand", "item:thing-he-wants", "character:old-king"],
               "known_by": [{"who": "party", "state": "knows", "session": 3, "via": "character:old-king", "how": "told"}]},
              {"op": "fact", "statement": "The party took \"don't use it\" as \"don't even touch it.\"", "fact_type": "belief", "truth": "unknown",
               "canon_status": "played", "visibility": "party", "established_session": 3, "about": ["thread:old-kings-errand", "character:old-king"],
               "known_by": [{"who": "party", "state": "believes", "session": 3, "how": "deduced"}]},
              {"op": "fact", "statement": "Belmakor intends to reclaim the blighted surface, make new habitable land, and become a legend who outstrips his parents.",
               "fact_type": "secret", "visibility": "restricted", "about": ["character:belmakor", "secret:belmakors-ambition"],
               "known_by": [{"who": "character:belmakor", "state": "knows", "how": "backstory"}, {"who": "dm", "state": "knows"},
                            {"who": "author", "state": "knows"}, {"who": "party", "state": "unaware"}]},
              {"op": "fact", "statement": "Belmakor keeps a Contingency: Polymorph into a T-rex when he drops low.", "canon_status": "played",
               "visibility": "restricted", "established_session": 2, "about": ["character:belmakor"],
               "known_by": [{"who": "character:belmakor", "state": "knows", "how": "backstory"}, {"who": "table", "state": "knows", "session": 2, "how": "witnessed"},
                            {"who": "party", "state": "knows", "session": 2, "how": "witnessed"}]},
              {"op": "fact", "statement": "Tristan died on the ocean job, dragged into the deep by the void octopus while protecting Sky.",
               "canon_status": "played", "visibility": "party", "established_session": 1, "about": ["character:tristan"],
               "known_by": [{"who": "party", "state": "knows", "session": 1, "how": "witnessed"}]},
              {"op": "fact", "statement": "Belmakor is level 11.", "confidence": "approximate", "visibility": "party", "about": ["character:belmakor"],
               "source": "party-and-band.md:19"},
              {"op": "fact", "statement": "Belmakor is level 12 (as of August 2026).", "visibility": "party", "established_session": 3,
               "about": ["character:belmakor"]}]}
            """));
        (AxiomFact, NameFact, ErrandFact, TouchFact, AmbitionFact, ContingencyFact, TristanFact, Level11Fact, Level12Fact) =
            (facts[0], facts[1], facts[2], facts[3], facts[4], facts[5], facts[6], facts[7], facts[8]);

        await Call("campaign_write", $$"""
            {"campaign": "belmakor", "reason": "The level drift: 12 as of August 2026",
             "ops": [{"op": "fact", "ref": "{{Level11Fact}}", "superseded_by": "{{Level12Fact}}"}]}
            """);
        await Call("campaign_write", $$"""
            {"campaign": "belmakor", "session": 2, "reason": "The Contingency fired; it is public at the table now.",
             "ops": [{"op": "fact", "ref": "{{ContingencyFact}}", "visibility": "party"}]}
            """);

        // The entities' knowledge rows (§2.5 k1-k6, and Belmakor's own ambition), with the names each knower uses.
        await Record(["character:old-king"],
            """{"who": "character:belmakor", "state": "met", "known_as": "the old king", "session": 3, "how": "witnessed"}""",
            """{"who": "party", "state": "met", "known_as": "the old king", "session": 3, "how": "witnessed"}""",
            """{"who": "author", "state": "knows", "known_as": "Keras", "how": "backstory"}""");
        await Record(["item:thing-he-wants"],
            """{"who": "party", "state": "aware", "known_as": "the thing he wants", "session": 3, "via": "character:old-king", "how": "told"}""",
            """{"who": "character:belmakor", "state": "aware", "known_as": "the thing he wants", "session": 3, "via": "character:old-king", "how": "told"}""",
            """{"who": "author", "state": "knows", "known_as": "the Axiom Cage", "how": "backstory"}""");
        await Record(["secret:belmakors-ambition"], """{"who": "character:belmakor", "state": "knows", "how": "backstory"}""");

        for (var n = 1; n <= 60 && ThingRef.Length == 0; n++)
        {
            var handle = "e:" + n.ToString(CultureInfo.InvariantCulture);
            var result = await Server.CallToolJsonAsync("campaign_get", $$"""{"campaign": "belmakor", "refs": ["{{handle}}"], "include": []}""");
            if (result.IsError != true && Server.SingleText(result).StartsWith("# The thing the old king wants (`item:thing-he-wants` · `" + handle + "`)\n", StringComparison.Ordinal))
            {
                ThingRef = handle;
            }
        }

        if (ThingRef.Length == 0)
        {
            throw new InvalidOperationException("No e:<n> handle opens item:thing-he-wants for the author.");
        }
    }

    private Task<string> Record(IReadOnlyList<string> targets, params string[] knowers) =>
        Call("campaign_knowledge", $$"""
            {"campaign": "belmakor", "action": "record", "targets": [{{ScenarioOnePieceWorld.List(targets)}}],
             "knowers": [{{string.Join(", ", knowers)}}]}
            """);
}

/// <summary>
/// The One Piece reveal-gate world of understand-onepiece.md §2, built only through the MCP tools in the server it is
/// given: <c>campaign create</c> (a DM campaign, 2024, effective level offset 1), <c>campaign_write</c> (the cast, the
/// secret, the questions, every fact with the seal's gate and Nadar's coupling, the supersession chain, and the
/// fragments' own timeless knowledge as the facts' <c>known_by</c>), <c>campaign_session record_past</c> (sessions 1, 5, 6
/// and 7, both PCs present) and <c>plan</c> (8-12), and <c>campaign_knowledge record</c> (the rest of the baseline as of
/// session 7, each party row filed under the session it was learned in, so point-in-time reads see it arrive then). The test steps T1-T5 of §3 are sessions 8-12 played live:
/// <c>campaign_session start</c>, the step's write with no session (so it belongs to the live session: the learned
/// session of T1-T3 and T5, the established session of T4's axe), then <c>end</c> with a recap.
///
/// <para>
/// Three things differ from §2, each so a golden is reachable at all or the party is not told a name it must not hear
/// (the repository scenario's corrections, stage3a-X-report): the gated facts <c>@seal</c> and <c>@nadar-plan</c> are
/// <c>restricted</c> (the fact default), not <c>author</c>, because author visibility is absolute and row 40's party
/// search after T5 could never find them (contract §8); session 1 is titled "Small town — the dragon", not "…and the
/// Protector", because a played session is party-visible and the party knows the Protector only as "the advisor in
/// Serret"; and T1 records the Protector's testimony for the party in the party's own words
/// (<see cref="TestimonyAsThePartyHeardIt"/>), for the same reason. Nadar also <c>watches</c> the author-only Mistaken
/// One (a party relation whose far end no player view may see).
/// </para>
/// <para>
/// Built per test by the classes that write (a write changes what every later read sees); fact handles come from the
/// write results, since fact numbers are shared by every campaign in the file.
/// </para>
/// </summary>
public sealed class ScenarioOnePieceWorld
{
    /// <summary>The secret the seal's gate guards (author visibility; its status derived from its gated fact's routes).</summary>
    public const string Secret = "secret:fruits-are-the-seal";

    /// <summary>Session 1's party-safe title (see the class summary).</summary>
    public const string SessionOneTitle = "Small town — the dragon";

    /// <summary>The party's own words for the Protector's testimony (T1's known_as): what the advisor in Serret told them.</summary>
    public const string TestimonyAsThePartyHeardIt = "The advisor in Serret said the Cage kept engaging: \"It held.\"";

    /// <summary>The gate's note, as the fact op writes it.</summary>
    public const string GateNote = "Reveal order — do not break this (canon-core.md:172-195)";

    private ScenarioOnePieceWorld(McpServerHarness server)
    {
        Server = server;
    }

    /// <summary>The server the world was built in (owned by the caller).</summary>
    public McpServerHarness Server { get; }

    /// <summary>@axe-assembled: planned until T4 plays it in session 11.</summary>
    public string Axe { get; private set; } = string.Empty;

    /// <summary>@holds-fruit-and-shard: played, established in session 5 (the gate's other after fact, met).</summary>
    public string Holds { get; private set; } = string.Empty;

    /// <summary>@no-uneaten-fruits: planned (the gate's advisory prefer).</summary>
    public string NoUneatenFruits { get; private set; } = string.Empty;

    /// <summary>@t-protector: the Protector's testimony, a clue of the testimonies route (the party learns it in T1, in its own words).</summary>
    public string TProtector { get; private set; } = string.Empty;

    /// <summary>@t-peaceful: the Peaceful One's testimony (testimonies route).</summary>
    public string TPeaceful { get; private set; } = string.Empty;

    /// <summary>@t-mistaken: the Mistaken One's testimony (testimonies route).</summary>
    public string TMistaken { get; private set; } = string.Empty;

    /// <summary>@t-dutiful: the Dutiful One's testimony (testimonies route).</summary>
    public string TDutiful { get; private set; } = string.Empty;

    /// <summary>@shielded-child: the seed, known to the party from session 6.</summary>
    public string ShieldedChild { get; private set; } = string.Empty;

    /// <summary>@illusion-rewatched: the illusion route's one clue (T2).</summary>
    public string IllusionRewatched { get; private set; } = string.Empty;

    /// <summary>@breaches-climbing: one of the temple-arithmetic route's two clues (T3).</summary>
    public string BreachesClimbing { get; private set; } = string.Empty;

    /// <summary>@fruit-appearances-falling: the other temple-arithmetic clue (T3, in the same call).</summary>
    public string FruitAppearancesFalling { get; private set; } = string.Empty;

    /// <summary>@nadar-plan: coupled with the seal (must land together).</summary>
    public string NadarPlan { get; private set; } = string.Empty;

    /// <summary>@seal: the gated fact, about the secret.</summary>
    public string Seal { get; private set; } = string.Empty;

    /// <summary>@timeline-old: the old timeline (canon until row 29 supersedes it; three facts rest on it).</summary>
    public string TimelineOld { get; private set; } = string.Empty;

    /// <summary>@timeline-2026-08-30: the corrected timeline (ruled, approximate) that supersedes <see cref="TimelineOld"/>.</summary>
    public string Timeline20260830 { get; private set; } = string.Empty;

    /// <summary>@fight-500-900: depends on the old timeline (depth 1).</summary>
    public string Fight500900 { get; private set; } = string.Empty;

    /// <summary>@fragments-400: depends on the old timeline (depth 1).</summary>
    public string Fragments400 { get; private set; } = string.Empty;

    /// <summary>@lineage-covers: depends on the fight (depth 2, via it).</summary>
    public string LineageCovers { get; private set; } = string.Empty;

    /// <summary>A successful call's text (<see cref="ScenarioCalls.Call"/>).</summary>
    public Task<string> Call(string tool, string argumentsJson) => ScenarioCalls.Call(Server, tool, argumentsJson);

    /// <summary>A refused call's error text (<see cref="ScenarioCalls.Fail"/>).</summary>
    public Task<string> Fail(string tool, string argumentsJson) => ScenarioCalls.Fail(Server, tool, argumentsJson);

    /// <summary>One resource's text (<see cref="ScenarioCalls.Read"/>).</summary>
    public Task<string> Read(string uri) => ScenarioCalls.Read(Server, uri);

    /// <summary>Builds the baseline (§2, as of session 7) in <paramref name="server"/>, which holds no other campaign.</summary>
    public static async Task<ScenarioOnePieceWorld> BuildAsync(McpServerHarness server)
    {
        var world = new ScenarioOnePieceWorld(server);
        await world.BuildAsync();
        return world;
    }

    /// <summary>"a", "b" as a JSON list's items.</summary>
    internal static string List(IEnumerable<string> handles) => string.Join(", ", handles.Select(h => $"\"{h}\""));

    /// <summary>T1, session 8 played live: the party learns the Protector's testimony, in its own words.</summary>
    public Task<string> T1() => Live(8, "The advisor in Serret compared notes with us on the Isle of Craftsmen.", $$"""
        {"campaign": "one-piece", "action": "record", "targets": ["{{TProtector}}"],
         "knowers": [{"who": "party", "state": "knows", "known_as": {{JsonSerializer.Serialize(TestimonyAsThePartyHeardIt)}}}]}
        """);

    /// <summary>T2, session 9 played live: the party learns the re-watched illusion.</summary>
    public Task<string> T2() => Live(9, "We re-watched the arch mage's illusion with a sharper question.",
        $$"""{"campaign": "one-piece", "action": "record", "targets": ["{{IllusionRewatched}}"], "knowers": [{"who": "party"}]}""");

    /// <summary>T3, session 10 played live: the party learns both temple clues in one call.</summary>
    public Task<string> T3() => Live(10, "The temple records did the arithmetic for us.",
        $$"""{"campaign": "one-piece", "action": "record", "targets": ["{{BreachesClimbing}}", "{{FruitAppearancesFalling}}"], "knowers": [{"who": "party"}]}""");

    /// <summary>T4, session 11 played live: the axe is assembled (played; established in the live session).</summary>
    public Task<string> T4() => Live(11, "The War God's axe is whole.",
        $$"""{"campaign": "one-piece", "ops": [{"op": "fact", "ref": "{{Axe}}", "canon_status": "played"}], "reason": "The axe assembled at the table"}""",
        tool: "campaign_write");

    /// <summary>T5, session 12 played live: the seal and Nadar's plan are revealed to the party in one call.</summary>
    public Task<string> T5() => Live(12, "Nadar's plan came out at last.",
        $$"""{"campaign": "one-piece", "action": "reveal", "facts": ["{{Seal}}", "{{NadarPlan}}"], "to": ["party"], "how": "told"}""");

    /// <summary>T1 to T4 in order ("after T4").</summary>
    public async Task ThroughT4()
    {
        await T1();
        await T2();
        await T3();
        await T4();
    }

    /// <summary>campaign_knowledge reveal of <paramref name="facts"/> to the party, filed under <paramref name="session"/>.</summary>
    public Task<string> Reveal(int session, bool dryRun, params string[] facts) => Call("campaign_knowledge", $$"""
        {"campaign": "one-piece", "action": "reveal", "facts": [{{List(facts)}}], "to": ["party"], "session": {{session}}{{(dryRun ? ", \"dry_run\": true" : string.Empty)}}}
        """);

    /// <summary>The author's campaign_get of the secret (its stored status and each gate's routes), now or as of a session.</summary>
    public Task<string> SecretPage(int? asOf = null) => Call("campaign_get", $$"""
        {"campaign": "one-piece", "refs": ["{{Secret}}"], "include": []{{(asOf is { } n ? $", \"as_of_session\": {n}" : string.Empty)}}}
        """);

    /// <summary>How many batches the campaign's history holds (its first page of one says "1 of N batches").</summary>
    public async Task<int> BatchCount()
    {
        var page = await Call("campaign_history", """{"campaign": "one-piece", "action": "since", "limit": 1}""");
        var match = Regex.Match(page, @"^1 of (\d+) batches on this page", RegexOptions.Multiline);
        Assert.True(match.Success, page);
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The ledger cell of <paramref name="perspective"/> on one fact (the author's campaign_knowledge ledger).</summary>
    public async Task<string> Cell(string fact, string perspective = "party")
    {
        var ledger = await Call("campaign_knowledge", $$"""{"campaign": "one-piece", "action": "ledger", "facts": ["{{fact}}"], "perspectives": ["{{perspective}}"]}""");
        return ScenarioLeak.LedgerCells(ledger)[fact];
    }

    /// <summary>
    /// The day session <paramref name="session"/> (8-12) was played: Saturdays of August 2026, a week apart. Given to start
    /// because its default is today's date, and no scenario's text may depend on the day it runs.
    /// </summary>
    public static string PlayedOn(int session) => "2026-08-" + ((session - 8) * 7 + 1).ToString("00", CultureInfo.InvariantCulture);

    // start, the step's own call with no session (the live session is its context), then end with a recap.
    private async Task<string> Live(int session, string recap, string argumentsJson, string tool = "campaign_knowledge")
    {
        await Call("campaign_session", $$"""
            {"campaign": "one-piece", "action": "start", "session": {{session}}, "played_on": "{{PlayedOn(session)}}", "precision": "day",
             "attendance": [{"character": "character:bjorn-mountainfell"}, {"character": "character:dragon-slayer"}]}
            """);
        var result = await Call(tool, argumentsJson);
        await Call("campaign_session", $$"""{"campaign": "one-piece", "action": "end", "recap_md": "{{recap}}"}""");
        return result;
    }

    private async Task BuildAsync()
    {
        await Call("campaign", """
            {"action": "create", "name": "One Piece", "role": "dm", "ruleset": "2024", "slug": "one-piece", "settings": {"effective_level_offset": 1}}
            """);
        await Call("campaign_write", """
            {"campaign": "one-piece", "reason": "The cast, the secret and the open questions",
             "ops": [
              {"op": "upsert", "kind": "character", "name": "Björn Mountainfell", "subtype": "pc", "visibility": "party"},
              {"op": "upsert", "kind": "character", "name": "The amethyst Dragon Slayer", "slug": "dragon-slayer", "subtype": "pc", "visibility": "party"},
              {"op": "link", "from": "character:bjorn-mountainfell", "rel": "member_of", "to": "faction:the-party"},
              {"op": "link", "from": "character:dragon-slayer", "rel": "member_of", "to": "faction:the-party"},
              {"op": "upsert", "kind": "character", "name": "The Protector", "slug": "protector", "subtype": "npc", "visibility": "restricted",
               "secret_md": "A Keras fragment (simulacrum); Serret's Magical / Battle Advisor."},
              {"op": "upsert", "kind": "character", "name": "The Peaceful One", "slug": "peaceful-one", "subtype": "npc", "visibility": "restricted"},
              {"op": "upsert", "kind": "character", "name": "The Mistaken One", "slug": "mistaken-one", "subtype": "npc", "visibility": "author"},
              {"op": "upsert", "kind": "character", "name": "The Dutiful One", "slug": "dutiful-one", "subtype": "npc", "visibility": "author"},
              {"op": "upsert", "kind": "character", "name": "Nadar", "subtype": "deity", "visibility": "party"},
              {"op": "upsert", "kind": "character", "name": "Arch mage", "subtype": "npc", "visibility": "party"},
              {"op": "link", "from": "character:nadar", "rel": "watches", "to": "character:mistaken-one", "visibility": "party",
               "label": "knows which fragment broke the Cage"},
              {"op": "upsert", "kind": "quest", "name": "The War God's axe", "slug": "war-gods-axe", "visibility": "party"},
              {"op": "objective", "ref": "quest:war-gods-axe", "text": "Assemble the War God's axe", "progress": 2},
              {"op": "upsert", "kind": "secret", "name": "Fruits are the seal", "visibility": "author"},
              {"op": "upsert", "kind": "faction", "name": "G.O.D.S. Co.", "slug": "gods-co", "subtype": "company", "visibility": "party",
               "aliases": [{"alias": "Global Order Dissemination Service Company", "visibility": "party"}],
               "body_md": "Protecting the coal mining islanders while squeezing them dry of money.",
               "secret_md": "Its founders destroyed Silk Isle to take the island's rope en masse and build their fleet (direction, not locked)."},
              {"op": "upsert", "kind": "location", "name": "Silk Isle", "visibility": "party"},
              {"op": "upsert", "kind": "question", "name": "What destroyed Silk Isle?", "slug": "q7", "code": "Q7", "subtype": "gap", "status": "withheld",
               "visibility": "party", "body_md": "He believes the island destroyed, but keeps finding his people's rope- and weave-work out in the world.",
               "secret_md": "Direction (not yet locked): the founders of G.O.D.S. Co., to take the island's rope en masse for building their fleet."},
              {"op": "upsert", "kind": "question", "name": "How rare is the long-lived lineage?", "slug": "q21", "code": "Q21", "subtype": "design",
               "visibility": "author"},
              {"op": "upsert", "kind": "question", "name": "How long ago was the Keras–Baal fight, on the corrected timeline?", "slug": "q22",
               "code": "Q22", "subtype": "design", "visibility": "author"}]}
            """);

        foreach (var (number, title) in new[] { (1, SessionOneTitle), (5, "Isle of Craftsmen"), (6, "Ohara — the arch mage's vision"), (7, "Blood-moon island") })
        {
            await Call("campaign_session", $$"""
                {"campaign": "one-piece", "action": "record_past", "session": {{number}}, "title": "{{title}}", "precision": "unknown",
                 "confidence": "approximate", "attendance": [{"character": "character:bjorn-mountainfell"}, {"character": "character:dragon-slayer"}]}
                """);
        }

        await Call("campaign_session", """{"campaign": "one-piece", "action": "plan", "session": 8, "title": "Craftsmen isle — return visit"}""");
        foreach (var number in new[] { 9, 10, 11, 12 })
        {
            await Call("campaign_session", $$"""{"campaign": "one-piece", "action": "plan", "session": {{number}}}""");
        }

        var h = ScenarioCalls.Refs(await Call("campaign_write", """
            {"campaign": "one-piece", "reason": "Every fact the gate and the timeline name",
             "ops": [
              {"op": "fact", "statement": "The War God's axe is fully assembled.", "canon_status": "planned", "visibility": "party"},
              {"op": "fact", "statement": "The party holds at least one devil fruit and at least one axe shard.", "canon_status": "played",
               "established_session": 5, "visibility": "party"},
              {"op": "fact", "statement": "The party has no uneaten devil fruits on hand.", "canon_status": "planned"},
              {"op": "fact", "statement": "The Protector kept the Cage engaging: \"It held.\"", "fact_type": "clue", "visibility": "restricted",
               "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}], "known_by": [{"who": "character:protector"}]},
              {"op": "fact", "statement": "The Peaceful One kept the split itself, including wrong-place moonlight.", "fact_type": "clue",
               "visibility": "restricted", "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}], "known_by": [{"who": "character:peaceful-one"}]},
              {"op": "fact", "statement": "The Mistaken One kept the aftermath: \"It broke. Because I was slow.\"", "fact_type": "clue",
               "visibility": "restricted", "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}], "known_by": [{"who": "character:mistaken-one"}]},
              {"op": "fact", "statement": "The Dutiful One kept the orders, briefed within minutes.", "fact_type": "clue", "visibility": "restricted",
               "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}], "known_by": [{"who": "character:dutiful-one"}]},
              {"op": "fact", "statement": "In the arch mage's vision, the moon goddess was protecting the arch mage as a child.", "fact_type": "clue",
               "canon_status": "played", "established_session": 6, "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}]},
              {"op": "fact", "statement": "Re-watched with a specific question, the arch mage's illusion shows moonlight wrapping the Cage's shards.",
               "fact_type": "clue", "visibility": "restricted", "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}]},
              {"op": "fact", "statement": "Planar breach counts are climbing.", "fact_type": "clue", "visibility": "restricted",
               "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}]},
              {"op": "fact", "statement": "Temple records show devil fruit appearances falling over the same period.", "fact_type": "clue",
               "visibility": "restricted", "links": [{"ref": "secret:fruits-are-the-seal", "role": "clue_for"}]},
              {"op": "fact", "statement": "Nadar routes fruits and axe shards to the party knowing every eaten fruit thins Baal's seal; it is part of his plan to have them kill Baal.",
               "fact_type": "secret"},
              {"op": "fact", "statement": "Keras was barely too slow to pull off the Cage properly.", "canon_status": "ruled",
               "known_by": [{"who": "character:mistaken-one"}]},
              {"op": "fact", "statement": "Keras was not alone at the Cage; he had help.", "canon_status": "ruled",
               "known_by": [{"who": "character:mistaken-one", "state": "unaware"}]},
              {"op": "fact", "statement": "The Cage broke because Keras alone was slow; the fault is all his.", "fact_type": "belief", "truth": "partial",
               "known_by": [{"who": "character:mistaken-one", "state": "believes"}]},
              {"op": "fact", "statement": "Sky-world is ~100–200 years after the first campaign; the present is ≈500–900 years after it.", "canon_status": "canon"},
              {"op": "fact", "statement": "Sky-world is at least 1,000 years after the first campaign; the present is hundreds of years — maybe a thousand — after sky-world.",
               "canon_status": "ruled", "confidence": "approximate"}]}
            """));
        (Axe, Holds, NoUneatenFruits, TProtector, TPeaceful, TMistaken, TDutiful, ShieldedChild, IllusionRewatched, BreachesClimbing,
            FruitAppearancesFalling, NadarPlan) = (h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], h[8], h[9], h[10], h[11]);
        var hadHelp = h[13];
        (TimelineOld, Timeline20260830) = (h[15], h[16]);

        var more = ScenarioCalls.Refs(await Call("campaign_write", $$"""
            {"campaign": "one-piece", "reason": "The seal's gate, and the facts resting on the old timeline",
             "ops": [
              {"op": "fact", "statement": "Eating a devil fruit breaks part of Baal's seal.", "fact_type": "secret", "truth": "true", "canon_status": "canon",
               "about": ["{{Secret}}"],
               "gate": {"after": ["{{Axe}}", "{{Holds}}"], "with": ["{{NadarPlan}}"], "prefer": ["{{NoUneatenFruits}}"], "forbidden_terms": ["seal"],
                        "forbidden_until": ["{{Axe}}"], "preferred_terms": ["shell", "wrapping", "what keeps it in"], "seeds": ["{{ShieldedChild}}"],
                        "routes": [{"id": "testimonies", "clues": [{{List([TProtector, TPeaceful, TMistaken, TDutiful])}}], "min_clues": 4},
                                   {"id": "illusion", "clues": ["{{IllusionRewatched}}"], "min_clues": 1},
                                   {"id": "temple-arithmetic", "clues": ["{{BreachesClimbing}}", "{{FruitAppearancesFalling}}"], "min_clues": 2}],
                        "min_routes": 2, "note": "{{GateNote}}"} },
              {"op": "fact", "statement": "Whole Keras believed he alone shattered and called the outcome near-best-case.", "superseded_by": "{{hadHelp}}"},
              {"op": "fact", "statement": "The Keras–Baal fight was ~500–900 years ago.", "depends_on": ["{{TimelineOld}}"],
               "about": ["question:q21", "character:arch-mage"]},
              {"op": "fact", "statement": "The fragments have been running down for roughly 400 years.", "depends_on": ["{{TimelineOld}}"],
               "about": ["question:q22", "character:protector"]},
              {"op": "fact", "statement": "The founders of G.O.D.S. Co. destroyed Silk Isle to take its rope en masse for their fleet.", "fact_type": "secret",
               "canon_status": "lean", "visibility": "author", "about": ["question:q7", "faction:gods-co", "location:silk-isle"]}]}
            """));
        (Seal, Fight500900, Fragments400) = (more[0], more[2], more[3]);
        LineageCovers = ScenarioCalls.Refs(await Call("campaign_write", $$"""
            {"campaign": "one-piece", "ops": [{"op": "fact", "statement": "The long-lived lineage covers the ~500–900 years since the Keras–Baal fight comfortably.",
              "depends_on": ["{{Fight500900}}"], "about": ["question:q21"]}]}
            """))[0];
        await Call("campaign_write", $$"""
            {"campaign": "one-piece", "ops": [{"op": "fact", "ref": "{{NadarPlan}}",
              "gate": {"with": ["{{Seal}}"], "note": "the two land together or not at all (CC:177-178)"} }]}
            """);

        // The baseline as of session 7: what the party learned at the table is filed under the session it learned it in (the
        // secret-status update it derives with it), and Nadar learns the coupled pair in one call, so the two land together.
        await Record(1, ["character:protector"],
            """{"who": "party", "state": "unrecognized", "session": 1, "how": "witnessed", "known_as": "the advisor in Serret"}""");
        await Record(5, ["character:peaceful-one"],
            """{"who": "party", "state": "unrecognized", "session": 5, "how": "witnessed", "known_as": "the man napping under the tree"}""");
        await Record(6, [ShieldedChild], """{"who": "party", "session": 6, "via": "character:arch-mage", "how": "witnessed"}""");
        await Record(5, [Holds], """{"who": "party", "session": 5}""");
        await Record(null, [Seal, NadarPlan], """{"who": "character:nadar"}""");
    }

    private Task<string> Record(int? session, IReadOnlyList<string> targets, string knower) => Call("campaign_knowledge", $$"""
        {"campaign": "one-piece", "action": "record", "targets": [{{List(targets)}}], "knowers": [{{knower}}]{{(session is { } n ? $", \"session\": {n}" : string.Empty)}}}
        """);
}

/// <summary>
/// The One Piece world at its baseline (as of session 7), built through the tools in a server of its own, for the test
/// classes that only read it (each class that takes it as <see cref="IClassFixture{TFixture}"/> gets its own instance, so
/// its own server and data directory). A class that writes builds a world per test instead
/// (<see cref="ScenarioOnePieceWorld.BuildAsync"/>), since a write changes what every later read sees.
/// </summary>
public sealed class ScenarioOnePieceBaseline : IAsyncLifetime
{
    /// <summary>The server the world lives in (its own data directory, deleted on dispose).</summary>
    public McpServerHarness Server { get; } = new();

    /// <summary>The baseline world, built once by <see cref="InitializeAsync"/>.</summary>
    public ScenarioOnePieceWorld World { get; private set; } = null!;

    /// <summary>Starts the server and builds the baseline through the tools.</summary>
    public async Task InitializeAsync()
    {
        await Server.InitializeAsync();
        World = await ScenarioOnePieceWorld.BuildAsync(Server);
    }

    /// <summary>Stops the server and deletes its data directory.</summary>
    public Task DisposeAsync() => Server.DisposeAsync();
}
