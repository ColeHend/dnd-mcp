using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// The Belmakor fixture of understand-belmakor.md §2 ("the old king" / "the thing he wants" / the Axiom Cage), built
/// entirely through the write path's services, with the One Piece campaign that holds the cross-link endpoints.
///
/// <para>
/// Differences from the §2 tables, each because the contract decides the point: the reveal rule's data drops the
/// INVENTED <c>applies_to</c> key (reveal rules take forbidden_terms, forbidden_patterns, preferred_terms, until and note
/// only); f:6's visibility is written restricted and then changed to party in session 2's context, so the change is in
/// change_log as the table says; f:8 is written canon and then superseded by f:9 once f:9 exists. Serif's membership has
/// no <c>since</c> (row 11's golden is "absent in S1", which the attendance row gives).
/// </para>
/// </summary>
public sealed class BelmakorFixture
{
    private BelmakorFixture(WriteFixture f, CampaignRow belmakor, CampaignRow onePiece)
    {
        F = f;
        Belmakor = belmakor;
        OnePiece = onePiece;
    }

    public WriteFixture F { get; }

    public CampaignRow Belmakor { get; }

    public CampaignRow OnePiece { get; }

    public static BelmakorFixture Build(WriteFixture f)
    {
        var onePiece = f.Store.Create("One Piece", "dm", "2024", slug: "one-piece").Campaign;
        f.Apply(onePiece,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Keras", Subtype = "npc", Visibility = "party", BodyMd = "Centuries ago Keras sealed Baal inside the Axiom Cage." },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "item", Name = "The Axiom Cage", Slug = "axiom-cage", Subtype = "artifact", Visibility = "author",
                Aliases = [new AliasSpec { Alias = "The Third Silence", Visibility = "author" }],
            });

        var c = f.Store.Create("Belmakor — sky-world", "player", "2014", slug: "belmakor", partyName: "The party", partySlug: "party",
            myCharacter: "Belmakor Silverwind", myCharacterSlug: "belmakor",
            settings: Op.Data("{\"dm_pronouns\": \"she/her\", \"table_style\": \"near-TPK\"}")).Campaign;

        static CampaignOpSpec Pc(string name, string? slug = null, string status = "alive", string? alias = null, string? summary = null) => new()
        {
            Op = "upsert", Kind = "character", Name = name, Slug = slug, Subtype = "pc", Visibility = "party", Status = status, Summary = summary,
            Aliases = alias is null ? null : [new AliasSpec { Alias = alias, Visibility = "party" }],
        };

        f.Apply(c,
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
            Op.Link("character:vars", "member_of", "faction:party"),
            Op.Link("character:ignis", "member_of", "faction:party"),
            Op.Link("character:serif", "member_of", "faction:party"),
            Op.Link("character:torch", "member_of", "faction:party"),
            Op.Link("character:aiden-ironstar", "member_of", "faction:party"),
            Op.Link("character:tristan", "member_of", "faction:party"),
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
                Visibility = "author", Data = Op.Data("{\"forbidden_terms\": [\"Keras\"], \"forbidden_patterns\": [\"<n>-year-old\", \"<n> years\"]}"),
            },
            new CampaignOpSpec { Op = "upsert", Kind = "arc", Name = "The Iron Guts job", Slug = "iron-guts-job", Visibility = "party", Confidence = "confirmed" },
            new CampaignOpSpec { Op = "upsert", Kind = "arc", Name = "Mind flayers and the Void", Slug = "mind-flayers", Visibility = "party", Confidence = "reconstructed" },
            new CampaignOpSpec { Op = "link", From = "character:old-king", Rel = "same_as", To = "one-piece/character:keras", Note = "Same being: Cole's old PC, imported as the Void-controlled sorcerer king" },
            new CampaignOpSpec { Op = "link", From = "item:thing-he-wants", Rel = "same_as", To = "one-piece/item:axiom-cage", Note = "The errand's object is the Cage" });

        static AttendanceSpec? At(string character, bool present = true, string? note = null) =>
            new() { Character = character, Present = present, Note = note };
        f.Sessions.RecordPast(c, 1, "The Iron Guts job", precision: "unknown", attendance:
        [
            At("character:belmakor"), At("character:ignis"), At("character:tristan", note: "died here"),
            At("character:serif", false, "not yet in the party; recruited via Ignis's fliers after Tristan died"),
        ]);
        f.Sessions.RecordPast(c, 2, "The airship / T-rex", precision: "unknown", attendance: [At("character:belmakor"), At("character:serif", note: "peeled off to save another wizard")]);
        f.Sessions.RecordPast(c, 3, "Kraken, the statue, the old king", playedOn: "2026-08", precision: "approx", attendance:
        [
            At("character:vars"), At("character:belmakor", note: "Cole also ran the old king"), At("character:aiden-ironstar"),
            At("character:ignis"), At("character:serif"), At("character:torch"),
        ]);

        f.Apply(c,
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The thing the old king wants fetched is the Axiom Cage.", FactType = "secret", Visibility = "author",
                EstablishedSession = 3, About = ["item:thing-he-wants", "character:old-king"],
            },
            new CampaignOpSpec { Op = "fact", Statement = "The old king's name is Keras.", FactType = "secret", Visibility = "restricted", About = ["character:old-king"] },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "After the fight the old king sent the party to fetch something more dangerous than anything they had seen, bring it back to him, and not use it.",
                CanonStatus = "played", Visibility = "party", EstablishedSession = 3, About = ["thread:old-kings-errand", "item:thing-he-wants"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The party took \"don't use it\" as \"don't even touch it.\"", FactType = "belief", Truth = "unknown",
                CanonStatus = "played", Visibility = "party", EstablishedSession = 3, About = ["thread:old-kings-errand"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Belmakor intends to reclaim the blighted surface, make new habitable land, and become a legend who outstrips his parents.",
                FactType = "secret", Visibility = "restricted", About = ["character:belmakor", "secret:belmakors-ambition"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Belmakor keeps a Contingency: Polymorph into a T-rex when he drops low.", CanonStatus = "played",
                Visibility = "restricted", EstablishedSession = 2, About = ["character:belmakor"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Tristan died on the ocean job, dragged into the deep by the void octopus while protecting Sky.", CanonStatus = "played",
                Visibility = "party", EstablishedSession = 1, About = ["character:tristan"],
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Belmakor is level 11.", Confidence = "approximate", Visibility = "party", About = ["character:belmakor"],
                Source = "party-and-band.md:19",
            },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Belmakor is level 12 (as of August 2026).", Visibility = "party", EstablishedSession = 3, About = ["character:belmakor"],
            });
        f.Apply(c, new CampaignOpSpec { Op = "fact", Ref = "f:8", SupersededBy = "f:9" });
        f.Apply(c, WriteContext.For(2, reason: "The Contingency fired; it is public at the table now."),
            new CampaignOpSpec { Op = "fact", Ref = "f:6", Visibility = "party" });

        void Record(string target, string who, string state, string? knownAs = null, int? session = null, string? via = null, string? how = null) =>
            f.Knowledge.Record(c, [target], [new KnowerSpec { Who = who, State = state, KnownAs = knownAs, Session = session, Via = via, How = how }], WriteContext.Default);

        Record("character:old-king", "character:belmakor", "met", "the old king", 3, how: "witnessed");
        Record("character:old-king", "party", "met", "the old king", 3, how: "witnessed");
        Record("character:old-king", "author", "knows", "Keras", how: "backstory");
        Record("item:thing-he-wants", "party", "aware", "the thing he wants", 3, "character:old-king", "told");
        Record("item:thing-he-wants", "character:belmakor", "aware", "the thing he wants", 3, "character:old-king", "told");
        Record("item:thing-he-wants", "author", "knows", "the Axiom Cage", how: "backstory");
        Record("f:1", "character:belmakor", "unaware");
        Record("f:1", "party", "unaware");
        Record("f:1", "author", "knows");
        Record("f:2", "party", "unaware");
        Record("f:2", "author", "knows");
        Record("f:2", "dm", "knows", how: "told");
        Record("f:3", "party", "knows", session: 3, via: "character:old-king", how: "told");
        Record("f:4", "party", "believes", session: 3, how: "deduced");
        Record("f:5", "character:belmakor", "knows", how: "backstory");
        Record("f:5", "dm", "knows");
        Record("f:5", "author", "knows");
        Record("f:5", "party", "unaware");
        Record("f:6", "character:belmakor", "knows", how: "backstory");
        Record("f:6", "table", "knows", session: 2, how: "witnessed");
        Record("f:6", "party", "knows", session: 2, how: "witnessed");
        Record("f:7", "party", "knows", session: 1, how: "witnessed");
        return new BelmakorFixture(f, f.Reload(c), f.Reload(onePiece));
    }
}
