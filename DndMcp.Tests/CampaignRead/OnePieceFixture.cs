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
/// The One Piece reveal-gate fixture of <c>understand-onepiece.md</c> §2 at its baseline (session 7), seeded with raw SQL.
/// The gate of <c>@seal</c> is stored with fact ids, as the write path stores it; <c>@seal</c> and <c>@nadar-plan</c> are
/// <c>restricted</c>, not the research's <c>author</c> (contract §8). Tests that step the scenario forward
/// (T4 assembles the axe in session 11; #29 supersedes the old timeline) build their own instance and call
/// <see cref="AssembleAxe"/> or <see cref="SupersedeTimeline"/>.
/// </summary>
public sealed class OnePieceFixture : IDisposable
{
    public OnePieceFixture()
    {
        Db = new CampaignTestDb();
        Connection = Db.Open();
        var seed = new CampaignSeed(Connection);
        var c = seed.Campaign(name: "One Piece", slug: "one-piece", role: CampaignValues.Roles.Dm, settings: "{\"effective_level_offset\":1}",
            partyName: "The crew");
        Campaign = c;
        Sessions = new Dictionary<int, SeededSession>
        {
            // The research titles this session "…the dragon and the Protector"; a party-visible title naming the Protector
            // would itself be the leak (the party knows him only as the advisor), so the fixture leaves him out of it.
            [1] = seed.Session(c.Id, 1, title: "Small town — the dragon"),
            [5] = seed.Session(c.Id, 5, title: "Isle of Craftsmen"),
            [6] = seed.Session(c.Id, 6, title: "Ohara — the arch mage's vision"),
            [7] = seed.Session(c.Id, 7, title: "Blood-moon island"),
            [8] = seed.Session(c.Id, 8, CampaignValues.SessionStatuses.Planned, title: "Craftsmen isle — return visit"),
        };
        foreach (var n in new[] { 9, 10, 11, 12 })
        {
            Sessions[n] = seed.Session(c.Id, n, CampaignValues.SessionStatuses.Planned);
        }

        Bjorn = seed.Entity(c.Id, "character", "Björn Mountainfell", subtype: "pc");
        DragonSlayer = seed.Entity(c.Id, "character", "The amethyst Dragon Slayer", slug: "dragon-slayer", subtype: "pc");
        seed.MemberOf(c.Id, Bjorn.Id, c.Party!.Id);
        seed.MemberOf(c.Id, DragonSlayer.Id, c.Party.Id);
        Protector = seed.Entity(c.Id, "character", "The Protector", slug: "protector", subtype: "npc", visibility: V.Restricted,
            summary: "Serret's Magical / Battle Advisor.", secret: "A Keras fragment (simulacrum); Serret's Magical / Battle Advisor.");
        PeacefulOne = seed.Entity(c.Id, "character", "The Peaceful One", slug: "peaceful-one", subtype: "npc", visibility: V.Restricted);
        MistakenOne = seed.Entity(c.Id, "character", "The Mistaken One", slug: "mistaken-one", subtype: "npc", visibility: V.Author);
        DutifulOne = seed.Entity(c.Id, "character", "The Dutiful One", slug: "dutiful-one", subtype: "npc", visibility: V.Author);
        Nadar = seed.Entity(c.Id, "character", "Nadar", subtype: "deity");
        ArchMage = seed.Entity(c.Id, "character", "The Arch Mage", slug: "arch-mage", subtype: "npc");
        Axe = seed.Entity(c.Id, "quest", "The War God's axe", slug: "war-gods-axe");
        seed.Objective(Axe.Id, "Assemble the War God's axe", progress: 2);
        Secret = seed.Entity(c.Id, "secret", "Fruits are the seal", slug: "fruits-are-the-seal", visibility: V.Author);
        GodsCo = seed.Entity(c.Id, "faction", "G.O.D.S. Co.", slug: "gods-co", subtype: "company",
            body: "Protecting the coal mining islanders while squeezing them dry of money.",
            secret: "Its founders destroyed Silk Isle to take the island's rope en masse and build their fleet (direction, not locked).");
        seed.Alias(GodsCo.Id, "Global Order Dissemination Service Company", V.Party);
        SilkIsle = seed.Entity(c.Id, "location", "Silk Isle", slug: "silk-isle");
        Q7 = seed.Entity(c.Id, "question", "What destroyed Silk Isle?", slug: "q7", code: "Q7", subtype: "gap", status: "withheld",
            body: "He believes the island destroyed, but keeps finding his people's rope- and weave-work out in the world.",
            secret: "Direction (not yet locked): the founders of G.O.D.S. Co., to take the island's rope en masse for building their fleet.");
        Q21 = seed.Entity(c.Id, "question", "How rare is the long-lived lineage?", slug: "q21", code: "Q21", subtype: "design", visibility: V.Author);
        Q22 = seed.Entity(c.Id, "question", "How long ago was the Keras–Baal fight, on the corrected timeline?", slug: "q22", code: "Q22",
            subtype: "design", visibility: V.Author);

        // Restricted, not author (the research's): author visibility is absolute (contract §3.2), so the party could never
        // know an author-only gated fact, and the secret could never read revealed however the party learned it (§8's
        // decision for fixtures). Restricted, they are known to exactly the knowers their rows name.
        Seal = seed.Fact(c.Id, "Eating a devil fruit breaks part of Baal's seal.", visibility: V.Restricted, factType: "secret");
        NadarPlan = seed.Fact(c.Id, "Nadar routes fruits and axe shards to the party knowing every eaten fruit thins Baal's seal; it is " +
                                    "part of his plan to have them kill Baal.", visibility: V.Restricted, factType: "secret");
        AxeAssembled = seed.Fact(c.Id, "The War God's axe is fully assembled.", visibility: V.Party, canonStatus: "planned");
        HoldsFruit = seed.Fact(c.Id, "The party holds at least one devil fruit and at least one axe shard.", visibility: V.Party,
            canonStatus: "played", establishedSessionId: Sessions[5].EntityId);
        NoUneatenFruits = seed.Fact(c.Id, "The party has no uneaten devil fruits on hand.", canonStatus: "planned");
        TProtector = Clue(seed, "The Protector kept the Cage engaging: \"It held.\"");
        TPeaceful = Clue(seed, "The Peaceful One kept the split itself, including wrong-place moonlight.");
        TMistaken = Clue(seed, "The Mistaken One kept the aftermath: \"It broke. Because I was slow.\"");
        TDutiful = Clue(seed, "The Dutiful One kept the orders, briefed within minutes.");
        ShieldedChild = seed.Fact(c.Id, "In the arch mage's vision, the moon goddess was protecting the arch mage as a child.",
            factType: "clue", canonStatus: "played", establishedSessionId: Sessions[6].EntityId);
        IllusionRewatched = Clue(seed, "Re-watched with a specific question, the arch mage's illusion shows moonlight wrapping the Cage's shards.");
        BreachesClimbing = Clue(seed, "Planar breach counts are climbing.");
        FruitFalling = Clue(seed, "Temple records show devil fruit appearances falling over the same period.");
        TooSlow = seed.Fact(c.Id, "Keras was barely too slow to pull off the Cage properly.", canonStatus: "ruled");
        HadHelp = seed.Fact(c.Id, "Keras was not alone at the Cage; he had help.", canonStatus: "ruled");
        TotalFault = seed.Fact(c.Id, "The Cage broke because Keras alone was slow; the fault is all his.", factType: "belief", truth: "partial");
        NearBestCase = seed.Fact(c.Id, "Whole Keras believed he alone shattered and called the outcome near-best-case.",
            canonStatus: "superseded", supersededBy: HadHelp.Id);
        TimelineOld = seed.Fact(c.Id, "Sky-world is ~100–200 years after the first campaign; the present is ≈500–900 years after it.");
        Timeline2026 = seed.Fact(c.Id, "Sky-world is at least 1,000 years after the first campaign; the present is hundreds of years — " +
                                       "maybe a thousand — after sky-world.", canonStatus: "ruled", confidence: "approximate");
        Fight500 = seed.Fact(c.Id, "The Keras–Baal fight was ~500–900 years ago.");
        Fragments400 = seed.Fact(c.Id, "The fragments have been running down for roughly 400 years.");
        LineageCovers = seed.Fact(c.Id, "The long-lived lineage covers the ~500–900 years since the Keras–Baal fight comfortably.");
        Q7Answer = seed.Fact(c.Id, "The founders of G.O.D.S. Co. destroyed Silk Isle to take its rope en masse for their fleet.",
            visibility: V.Author, factType: "secret", canonStatus: "lean");
        seed.Fact(c.Id, "INVENTED accepted invention", code: "F1", canonStatus: "accepted");
        seed.Fact(c.Id, "INVENTED struck invention", code: "F2", canonStatus: "struck");

        var gate = new GateSpec
        {
            After = [AxeAssembled.Id, HoldsFruit.Id],
            With = [NadarPlan.Id],
            Prefer = [NoUneatenFruits.Id],
            Seeds = [ShieldedChild.Id],
            Routes =
            [
                new RouteSpec { Id = "testimonies", Clues = [TProtector.Id, TPeaceful.Id, TMistaken.Id, TDutiful.Id], MinClues = 4 },
                new RouteSpec { Id = "illusion", Clues = [IllusionRewatched.Id], MinClues = 1 },
                new RouteSpec { Id = "temple-arithmetic", Clues = [BreachesClimbing.Id, FruitFalling.Id], MinClues = 2 },
            ],
            MinRoutes = 2,
            ForbiddenTerms = ["seal"],
            ForbiddenUntil = [AxeAssembled.Id],
            PreferredTerms = ["shell", "wrapping", "what keeps it in"],
            Note = "Reveal order — do not break this (canon-core.md:172-195)",
        };
        Connection.Execute("UPDATE fact SET gate = @gate WHERE id = @id", new { gate = FactGates.Serialize(gate), id = Seal.Id });
        Connection.Execute("UPDATE fact SET gate = @gate WHERE id = @id",
            new { gate = FactGates.Serialize(new GateSpec { With = [Seal.Id], Note = "the two land together or not at all" }), id = NadarPlan.Id });

        seed.FactLink(Seal.Id, Secret.Id);
        foreach (var clue in new[] { TProtector, TPeaceful, TMistaken, TDutiful, IllusionRewatched, BreachesClimbing, FruitFalling, ShieldedChild })
        {
            seed.FactLink(clue.Id, Secret.Id, CampaignValues.FactLinkRoles.ClueFor);
        }

        seed.FactDependency(Fight500.Id, TimelineOld.Id);
        seed.FactDependency(Fragments400.Id, TimelineOld.Id);
        seed.FactDependency(LineageCovers.Id, Fight500.Id);
        seed.FactLink(Fight500.Id, Q21.Id);
        seed.FactLink(Fight500.Id, ArchMage.Id);
        seed.FactLink(Fragments400.Id, Q22.Id);
        seed.FactLink(Fragments400.Id, Protector.Id);
        seed.FactLink(LineageCovers.Id, Q21.Id);
        seed.FactLink(Q7Answer.Id, Q7.Id);
        seed.FactLink(Q7Answer.Id, GodsCo.Id);
        seed.FactLink(Q7Answer.Id, SilkIsle.Id);

        seed.Knowledge(c.Id, KK.Party, null, KS.Unrecognized, entityId: Protector.Id, knownAs: "the advisor in Serret",
            learnedSessionId: Sessions[1].EntityId, how: "witnessed");
        seed.Knowledge(c.Id, KK.Party, null, KS.Unrecognized, entityId: PeacefulOne.Id, knownAs: "the man napping under the tree",
            learnedSessionId: Sessions[5].EntityId, how: "witnessed");
        seed.Knowledge(c.Id, KK.Party, null, KS.Knows, factId: ShieldedChild.Id, learnedSessionId: Sessions[6].EntityId, viaEntityId: ArchMage.Id,
            how: "witnessed");
        seed.Knowledge(c.Id, KK.Party, null, KS.Knows, factId: HoldsFruit.Id, learnedSessionId: Sessions[5].EntityId);
        seed.Knowledge(c.Id, KK.Character, Protector.Id, KS.Knows, factId: TProtector.Id);
        seed.Knowledge(c.Id, KK.Character, PeacefulOne.Id, KS.Knows, factId: TPeaceful.Id, note: "doesn't know it matters");
        seed.Knowledge(c.Id, KK.Character, MistakenOne.Id, KS.Knows, factId: TMistaken.Id);
        seed.Knowledge(c.Id, KK.Character, DutifulOne.Id, KS.Knows, factId: TDutiful.Id);
        seed.Knowledge(c.Id, KK.Character, MistakenOne.Id, KS.Knows, factId: TooSlow.Id);
        seed.Knowledge(c.Id, KK.Character, MistakenOne.Id, KS.Believes, factId: TotalFault.Id);
        seed.Knowledge(c.Id, KK.Character, MistakenOne.Id, KS.Unaware, factId: HadHelp.Id);
        seed.Knowledge(c.Id, KK.Character, Nadar.Id, KS.Knows, factId: Seal.Id);
        seed.Knowledge(c.Id, KK.Character, Nadar.Id, KS.Knows, factId: NadarPlan.Id);
        CampaignRow = seed.LoadCampaign(c.Id);

        SeededFact Clue(CampaignSeed s, string statement) => s.Fact(c.Id, statement, factType: "clue");
    }

    public CampaignTestDb Db { get; }

    public SqliteConnection Connection { get; }

    public SeededCampaign Campaign { get; }

    public CampaignRow CampaignRow { get; }

    public Dictionary<int, SeededSession> Sessions { get; }

    public SeededEntity Bjorn { get; }

    public SeededEntity DragonSlayer { get; }

    public SeededEntity Protector { get; }

    public SeededEntity PeacefulOne { get; }

    public SeededEntity MistakenOne { get; }

    public SeededEntity DutifulOne { get; }

    public SeededEntity Nadar { get; }

    public SeededEntity ArchMage { get; }

    public SeededEntity Axe { get; }

    public SeededEntity Secret { get; }

    public SeededEntity GodsCo { get; }

    public SeededEntity SilkIsle { get; }

    public SeededEntity Q7 { get; }

    public SeededEntity Q21 { get; }

    public SeededEntity Q22 { get; }

    public SeededFact Seal { get; }

    public SeededFact NadarPlan { get; }

    public SeededFact AxeAssembled { get; }

    public SeededFact HoldsFruit { get; }

    public SeededFact NoUneatenFruits { get; }

    public SeededFact TProtector { get; }

    public SeededFact TPeaceful { get; }

    public SeededFact TMistaken { get; }

    public SeededFact TDutiful { get; }

    public SeededFact ShieldedChild { get; }

    public SeededFact IllusionRewatched { get; }

    public SeededFact BreachesClimbing { get; }

    public SeededFact FruitFalling { get; }

    public SeededFact TooSlow { get; }

    public SeededFact HadHelp { get; }

    public SeededFact TotalFault { get; }

    public SeededFact NearBestCase { get; }

    public SeededFact TimelineOld { get; }

    public SeededFact Timeline2026 { get; }

    public SeededFact Fight500 { get; }

    public SeededFact Fragments400 { get; }

    public SeededFact LineageCovers { get; }

    public SeededFact Q7Answer { get; }

    /// <summary>T4: the axe is assembled, played in session 11 (one logged batch in S11, so as_of 10 still has it planned).</summary>
    public void AssembleAxe() =>
        Db.Batch(Campaign.Id, r => r.Update("fact", AxeAssembled.Id, new Dictionary<string, object?>
        {
            ["canon_status"] = CampaignValues.CanonStatuses.Played,
            ["established_session_id"] = Sessions[11].EntityId,
        }, "fact"), sessionId: Sessions[11].EntityId);

    /// <summary>Row 29: the old timeline is superseded by the corrected one (dependents unchanged).</summary>
    public void SupersedeTimeline() =>
        Connection.Execute("UPDATE fact SET canon_status = 'superseded', superseded_by = @by WHERE id = @id",
            new { by = Timeline2026.Id, id = TimelineOld.Id });

    /// <summary>The party learns a fact in a session (raw SQL, as the reveal would write it).</summary>
    public void PartyLearns(SeededFact fact, int session) =>
        new CampaignSeed(Connection).Knowledge(Campaign.Id, KK.Party, null, KS.Knows, factId: fact.Id, learnedSessionId: Sessions[session].EntityId);

    public void Dispose()
    {
        Connection.Dispose();
        Db.Dispose();
    }
}
