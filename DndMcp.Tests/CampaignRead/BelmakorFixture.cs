using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Tests.CampaignDb;
using Microsoft.Data.Sqlite;
using KS = DndMcp.Domain.Campaign.CampaignValues.KnowledgeStates;
using KK = DndMcp.Domain.Campaign.CampaignValues.KnowerKinds;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// The Belmakor fixture of <c>understand-belmakor.md</c> §2, seeded with raw SQL (<see cref="CampaignSeed"/>), plus the
/// One Piece campaign that holds the cross-link endpoints. Read-only once built, so test classes share one instance.
///
/// <para>
/// Row numbers (k1…k22, f:1…f:9) follow the research tables; the facts are created in that order so their handles are
/// f:1…f:9 of this campaign's sequence only when the campaign's facts are the first facts written (the One Piece
/// campaign is seeded with no facts). Two additions the goldens need and the fixture table leaves out, each marked where
/// it is made: f:3 and f:4 are also linked about the old king (row 30 expects them on his page), and Belmakor has a
/// knowledge row on his own ambition entity (row 29 finds the restricted secret entity for him, which needs a row).
/// </para>
/// </summary>
public sealed class BelmakorFixture : IDisposable
{
    /// <summary>Strings no Belmakor-campaign view other than the author's (and, for "Keras", the dm's) may ever contain.</summary>
    public static readonly IReadOnlyList<string> AlwaysForbidden =
        ["Axiom", "Cage", "Baal", "Third Silence", "one-piece", "Cole", "imported", "memory files"];

    public BelmakorFixture()
    {
        Db = new CampaignTestDb();
        Connection = Db.Open();
        var seed = new CampaignSeed(Connection);
        Seed = seed;

        // The other campaign first, so both of its entities exist for the cross-links (and it seeds no facts).
        var op = seed.Campaign(name: "One Piece", slug: "one-piece", role: CampaignValues.Roles.Dm, withParty: true, partyName: "The Straw Party");
        OnePiece = op;
        OpKeras = seed.Entity(op.Id, "character", "Keras", subtype: "npc", visibility: V.Party,
            body: "Centuries ago Keras sealed Baal inside the Axiom Cage.");
        OpCage = seed.Entity(op.Id, "item", "The Axiom Cage", slug: "axiom-cage", subtype: "artifact", visibility: V.Author);
        seed.Alias(OpCage.Id, "The Third Silence", V.Author);

        var c = seed.Campaign(name: "Belmakor — sky-world", slug: "belmakor", role: CampaignValues.Roles.Player,
            ruleset: CampaignValues.Rulesets.R2014, partyName: "The party",
            settings: "{\"dm_pronouns\":\"she/her\",\"table_style\":\"near-TPK\"}");
        Campaign = c;
        S1 = seed.Session(c.Id, 1, title: "The Iron Guts job", recap: "Tristan and Robin die on the ocean job.");
        S2 = seed.Session(c.Id, 2, title: "The airship and the T-rex", recap: "The Contingency fires for the first time.");
        S3 = seed.Session(c.Id, 3, title: "Kraken, the statue, the old king",
            recap: "A crumbling statue of a sorcerer king. The fight. Afterward the old king sends the party on an errand.");

        Belmakor = seed.Entity(c.Id, "character", "Belmakor Silverwind", slug: "belmakor", subtype: "pc", status: "alive",
            summary: "High Elf Bladesinger, Mythril Zeppelin frontman");
        seed.Alias(Belmakor.Id, "Belmakor", V.Party);
        seed.Alias(Belmakor.Id, "Silverwind", V.Party);
        Vars = seed.Entity(c.Id, "character", "Vars Nocturne", slug: "vars", subtype: "pc", status: "alive");
        Ignis = seed.Entity(c.Id, "character", "Ignis", subtype: "pc", status: "alive");
        Serif = seed.Entity(c.Id, "character", "Serif", subtype: "pc", status: "alive", summary: "joined after Tristan died");
        Torch = seed.Entity(c.Id, "character", "Lieutenant James Torch", slug: "torch", subtype: "pc", status: "alive");
        seed.Alias(Torch.Id, "Torch", V.Party);
        Aiden = seed.Entity(c.Id, "character", "Aiden Ironstar", subtype: "pc", status: "alive");
        seed.Alias(Aiden.Id, "Ironstar", V.Party);
        Tristan = seed.Entity(c.Id, "character", "Tristan", subtype: "pc", status: "dead", summary: "died protecting Sky on the ocean job");
        foreach (var pc in new[] { Belmakor, Vars, Ignis, Serif, Torch, Aiden, Tristan })
        {
            seed.MemberOf(c.Id, pc.Id, c.Party!.Id);
        }

        seed.SetMyCharacter(c.Id, Belmakor.Id);

        OldKing = seed.Entity(c.Id, "character", "The Old King", slug: "old-king", subtype: "npc", status: "unknown",
            summary: "Ancient, Void-controlled sorcerer king; revealed by a crumbling statue; fought and beaten; sent the party on an errand.",
            secret: "Keras, Cole's old PC from his other campaign, imported at Cole's request; Cole ran him in the fight.");
        seed.Alias(OldKing.Id, "the sorcerer king", V.Party);
        seed.Alias(OldKing.Id, "Keras", V.Author);
        Thing = seed.Entity(c.Id, "item", "The thing the old king wants", slug: "thing-he-wants", subtype: "artifact",
            summary: "Something more dangerous than anything they've seen; bring it back to him; don't use it.",
            secret: "It is the Axiom Cage (Cole-tier).");
        seed.Alias(Thing.Id, "the thing he wants", V.Party);
        seed.Alias(Thing.Id, "Axiom Cage", V.Author);
        seed.Alias(Thing.Id, "the Cage", V.Author);
        Errand = seed.Entity(c.Id, "thread", "The old king's errand", slug: "old-kings-errand", status: "open",
            summary: "What the old king wants fetched, where it is, and what he is to the party now.",
            secret: "Cole-tier: it's the Axiom Cage.");
        Ambition = seed.Entity(c.Id, "secret", "Belmakor's ambition", slug: "belmakors-ambition", visibility: V.Restricted, status: "hidden",
            body: "Reclaim the blighted surface, make new habitable land, become a legend who outstrips his parents.");
        NoNameRule = seed.Entity(c.Id, "rule", "No name, no timespan for the old king", slug: "old-king-no-name-no-timespan",
            subtype: "reveal_rule", visibility: V.Author,
            data: "{\"forbidden_terms\":[\"Keras\"],\"forbidden_patterns\":[\"<n>-year-old\",\"<n> years\"]}");
        seed.Entity(c.Id, "arc", "The Iron Guts job", slug: "iron-guts-job");
        seed.Entity(c.Id, "arc", "Mind flayers and the Void", slug: "mind-flayers", confidence: "reconstructed");

        F1 = seed.Fact(c.Id, "The thing the old king wants fetched is the Axiom Cage.", visibility: V.Author, factType: "secret",
            establishedSessionId: S3.EntityId);
        F2 = seed.Fact(c.Id, "The old king's name is Keras.", visibility: V.Restricted, factType: "secret");
        F3 = seed.Fact(c.Id, "After the fight the old king sent the party to fetch something more dangerous than anything they had seen, " +
                              "bring it back to him, and not use it.", visibility: V.Party, canonStatus: "played", establishedSessionId: S3.EntityId);
        F4 = seed.Fact(c.Id, "The party took \"don't use it\" as \"don't even touch it.\"", visibility: V.Party, factType: "belief",
            truth: "unknown", canonStatus: "played", establishedSessionId: S3.EntityId);
        F5 = seed.Fact(c.Id, "Belmakor intends to reclaim the blighted surface, make new habitable land, and become a legend who " +
                              "outstrips his parents.", visibility: V.Restricted, factType: "secret");
        F6 = seed.Fact(c.Id, "Belmakor keeps a Contingency: Polymorph into a T-rex when he drops low.", visibility: V.Restricted,
            canonStatus: "played", establishedSessionId: S2.EntityId);
        F7 = seed.Fact(c.Id, "Tristan died on the ocean job, dragged into the deep by the void octopus while protecting Sky.",
            visibility: V.Party, canonStatus: "played", establishedSessionId: S1.EntityId);
        F8 = seed.Fact(c.Id, "Belmakor is level 11.", visibility: V.Party, canonStatus: "superseded", confidence: "approximate",
            source: "party-and-band.md:19");
        F9 = seed.Fact(c.Id, "Belmakor is level 12 (as of August 2026).", visibility: V.Party, establishedSessionId: S3.EntityId,
            source: "belmakor-build.md:19");
        Connection.Execute("UPDATE fact SET superseded_by = @f9 WHERE id = @f8", new { f9 = F9.Id, f8 = F8.Id });

        seed.FactLink(F1.Id, Thing.Id);
        seed.FactLink(F1.Id, OldKing.Id);
        seed.FactLink(F2.Id, OldKing.Id);
        seed.FactLink(F3.Id, Errand.Id);
        seed.FactLink(F3.Id, Thing.Id);
        seed.FactLink(F4.Id, Errand.Id);
        seed.FactLink(F5.Id, Belmakor.Id);
        seed.FactLink(F5.Id, Ambition.Id);
        seed.FactLink(F6.Id, Belmakor.Id);
        seed.FactLink(F7.Id, Tristan.Id);
        seed.FactLink(F8.Id, Belmakor.Id);
        seed.FactLink(F9.Id, Belmakor.Id);
        // Addition for row 30: the errand facts are also about the old king.
        seed.FactLink(F3.Id, OldKing.Id);
        seed.FactLink(F4.Id, OldKing.Id);

        seed.Knowledge(c.Id, KK.Character, Belmakor.Id, KS.Met, entityId: OldKing.Id, knownAs: "the old king", learnedSessionId: S3.EntityId, how: "witnessed");
        seed.Knowledge(c.Id, KK.Party, null, KS.Met, entityId: OldKing.Id, knownAs: "the old king", learnedSessionId: S3.EntityId, how: "witnessed");
        seed.Knowledge(c.Id, KK.Author, null, KS.Knows, entityId: OldKing.Id, knownAs: "Keras", how: "backstory");
        seed.Knowledge(c.Id, KK.Party, null, KS.Aware, entityId: Thing.Id, knownAs: "the thing he wants", learnedSessionId: S3.EntityId,
            viaEntityId: OldKing.Id, how: "told", note: "Cole-tier: the Axiom Cage");
        seed.Knowledge(c.Id, KK.Character, Belmakor.Id, KS.Aware, entityId: Thing.Id, knownAs: "the thing he wants",
            learnedSessionId: S3.EntityId, viaEntityId: OldKing.Id, how: "told");
        seed.Knowledge(c.Id, KK.Author, null, KS.Knows, entityId: Thing.Id, knownAs: "the Axiom Cage", how: "backstory");
        seed.Knowledge(c.Id, KK.Character, Belmakor.Id, KS.Unaware, factId: F1.Id);
        seed.Knowledge(c.Id, KK.Party, null, KS.Unaware, factId: F1.Id);
        seed.Knowledge(c.Id, KK.Author, null, KS.Knows, factId: F1.Id);
        seed.Knowledge(c.Id, KK.Party, null, KS.Unaware, factId: F2.Id);
        seed.Knowledge(c.Id, KK.Author, null, KS.Knows, factId: F2.Id);
        seed.Knowledge(c.Id, KK.Dm, null, KS.Knows, factId: F2.Id, how: "told");
        seed.Knowledge(c.Id, KK.Party, null, KS.Knows, factId: F3.Id, learnedSessionId: S3.EntityId, viaEntityId: OldKing.Id, how: "told");
        seed.Knowledge(c.Id, KK.Party, null, KS.Believes, factId: F4.Id, learnedSessionId: S3.EntityId, how: "deduced");
        seed.Knowledge(c.Id, KK.Character, Belmakor.Id, KS.Knows, factId: F5.Id, how: "backstory");
        seed.Knowledge(c.Id, KK.Dm, null, KS.Knows, factId: F5.Id);
        seed.Knowledge(c.Id, KK.Author, null, KS.Knows, factId: F5.Id);
        seed.Knowledge(c.Id, KK.Party, null, KS.Unaware, factId: F5.Id);
        seed.Knowledge(c.Id, KK.Character, Belmakor.Id, KS.Knows, factId: F6.Id, how: "backstory");
        seed.Knowledge(c.Id, KK.Table, null, KS.Knows, factId: F6.Id, learnedSessionId: S2.EntityId, how: "witnessed");
        seed.Knowledge(c.Id, KK.Party, null, KS.Knows, factId: F6.Id, learnedSessionId: S2.EntityId, how: "witnessed");
        seed.Knowledge(c.Id, KK.Party, null, KS.Knows, factId: F7.Id, learnedSessionId: S1.EntityId, how: "witnessed");
        // Addition for row 29: Belmakor knows his own secret entity.
        seed.Knowledge(c.Id, KK.Character, Belmakor.Id, KS.Knows, entityId: Ambition.Id, how: "backstory");

        seed.Attendance(S1.EntityId, Belmakor.Id);
        seed.Attendance(S1.EntityId, Ignis.Id);
        seed.Attendance(S1.EntityId, Tristan.Id, note: "died here");
        seed.Attendance(S1.EntityId, Serif.Id, present: false, note: "not yet in the party; recruited via Ignis's fliers after Tristan died");
        seed.Attendance(S2.EntityId, Belmakor.Id);
        seed.Attendance(S2.EntityId, Serif.Id, note: "peeled off to save another wizard");
        foreach (var pc in new[] { Vars, Belmakor, Aiden, Ignis, Serif, Torch })
        {
            seed.Attendance(S3.EntityId, pc.Id, note: pc == Belmakor ? "Cole also ran the old king" : null);
        }

        seed.CrossLink(OldKing.Id, OpKeras.Id, "Same being: Cole's old PC, imported as the Void-controlled sorcerer king");
        seed.CrossLink(Thing.Id, OpCage.Id, "The errand's object is the Cage");

        // f:6 was restricted until session 2 made it party-visible: one logged change in S2 (Belmakor row 17).
        Db.Batch(c.Id, r => r.Update("fact", F6.Id, new Dictionary<string, object?> { ["visibility"] = V.Party }, "fact"), sessionId: S2.EntityId);
        CampaignRow = seed.LoadCampaign(c.Id);
        OnePieceRow = seed.LoadCampaign(op.Id);
    }

    public CampaignTestDb Db { get; }

    public SqliteConnection Connection { get; }

    public CampaignSeed Seed { get; }

    public SeededCampaign Campaign { get; }

    public SeededCampaign OnePiece { get; }

    public CampaignRow CampaignRow { get; }

    public CampaignRow OnePieceRow { get; }

    public SeededSession S1 { get; }

    public SeededSession S2 { get; }

    public SeededSession S3 { get; }

    public SeededEntity Belmakor { get; }

    public SeededEntity Vars { get; }

    public SeededEntity Ignis { get; }

    public SeededEntity Serif { get; }

    public SeededEntity Torch { get; }

    public SeededEntity Aiden { get; }

    public SeededEntity Tristan { get; }

    public SeededEntity OldKing { get; }

    public SeededEntity Thing { get; }

    public SeededEntity Errand { get; }

    public SeededEntity Ambition { get; }

    public SeededEntity NoNameRule { get; }

    public SeededEntity OpKeras { get; }

    public SeededEntity OpCage { get; }

    public SeededFact F1 { get; }

    public SeededFact F2 { get; }

    public SeededFact F3 { get; }

    public SeededFact F4 { get; }

    public SeededFact F5 { get; }

    public SeededFact F6 { get; }

    public SeededFact F7 { get; }

    public SeededFact F8 { get; }

    public SeededFact F9 { get; }

    /// <summary>
    /// The strings a perspective must never see in this campaign: <see cref="AlwaysForbidden"/>, "Keras" for everyone but
    /// the dm (who knows f:2, "The old king's name is Keras."), and Belmakor's ambition for everyone but him and the dm.
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

    /// <summary>Every non-author perspective of this player campaign.</summary>
    public static IReadOnlyList<string> NonAuthorPerspectives { get; } =
    [
        "party", "table", "dm", "public", "character:belmakor", "character:vars", "character:ignis", "character:serif",
        "character:aiden-ironstar", "character:tristan",
    ];

    public void Dispose()
    {
        Connection.Dispose();
        Db.Dispose();
    }
}
