using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>The One Piece fixture's fact handles (<c>f:&lt;n&gt;</c>), by their names in understand-onepiece.md §2.</summary>
public sealed record OnePieceFacts(
    string Seal,
    string NadarPlan,
    string AxeAssembled,
    string HoldsFruitAndShard,
    string NoUneatenFruits,
    string TProtector,
    string TPeaceful,
    string TMistaken,
    string TDutiful,
    string ShieldedChild,
    string IllusionRewatched,
    string BreachesClimbing,
    string FruitAppearancesFalling,
    string TooSlow,
    string HadHelp,
    string TotalFault,
    string NearBestCase,
    string TimelineOld,
    string Timeline20260830,
    string Fight500900,
    string Fragments400,
    string LineageCovers,
    string Q7Answer,
    string F1,
    string F2);

/// <summary>
/// The One Piece reveal-gate fixture of understand-onepiece.md §2, built entirely through the write path's services
/// (campaign create, campaign_write ops, campaign_session record_past / plan, campaign_knowledge record), so building it
/// is itself a test that the write path can express everything the scenario needs. The baseline is "as of session 7";
/// <see cref="T1"/>..<see cref="T5"/> are the test steps of §3 (sessions 8-12).
///
/// <para>
/// <b>Three places the fixture departs from the research table</b>, each because the table's version would show the
/// party what it must not see, or could never show it what it should:
/// </para>
/// <list type="bullet">
/// <item>
/// Session 1 is titled <see cref="SessionOneTitle"/>, not "…the dragon and the Protector": a played session is
/// party-visible, so its title would tell the party the true name of the fragment it knows only as "the advisor in
/// Serret" (row 39), and the party's search for "protector" would find the session.
/// </item>
/// <item>
/// <see cref="T1"/> records the Protector's testimony for the party in the party's own words
/// (<see cref="TestimonyAsThePartyHeardIt"/>): the research step names no phrasing, and a row without one shows the party
/// the bare statement, "The Protector kept the Cage engaging", which is the same leak arriving at T1. Route counting reads
/// only the row's state, so rows 18-23 are unchanged.
/// </item>
/// <item>
/// The gated facts, <c>@seal</c> and <c>@nadar-plan</c>, are <c>restricted</c> (the fact default), not <c>author</c>:
/// author visibility is absolute (contract §3.2), so an author-only fact could never be shown to the party even after T5
/// reveals it, and row 40 could never pass (contract §8's decision for fixtures).
/// </item>
/// </list>
/// </summary>
public sealed class OnePieceFixture
{
    public const string Secret = "secret:fruits-are-the-seal";

    /// <summary>Session 1's party-safe title (the research's "…and the Protector" names a disguised fragment; class summary).</summary>
    public const string SessionOneTitle = "Small town — the dragon";

    /// <summary>The party's own words for the Protector's testimony (T1's known_as): what the advisor in Serret told them.</summary>
    public const string TestimonyAsThePartyHeardIt = "The advisor in Serret said the Cage kept engaging: \"It held.\"";

    private OnePieceFixture(WriteFixture f, CampaignRow campaign, OnePieceFacts facts)
    {
        F = f;
        Campaign = campaign;
        Facts = facts;
    }

    public WriteFixture F { get; }

    public CampaignRow Campaign { get; }

    public OnePieceFacts Facts { get; }

    /// <summary>Builds the baseline (§2), as of session 7.</summary>
    public static OnePieceFixture Build(WriteFixture f)
    {
        var campaign = f.Store.Create("One Piece", "dm", "2024", slug: "one-piece", settings: Op.Data("{\"effective_level_offset\": 1}")).Campaign;
        f.Apply(campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Björn Mountainfell", Subtype = "pc", Visibility = "party" },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The amethyst Dragon Slayer", Slug = "dragon-slayer", Subtype = "pc", Visibility = "party" },
            Op.Link("character:bjorn-mountainfell", "member_of", "faction:the-party"),
            Op.Link("character:dragon-slayer", "member_of", "faction:the-party"),
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "The Protector", Slug = "protector", Subtype = "npc", Visibility = "restricted",
                SecretMd = "A Keras fragment (simulacrum); Serret's Magical / Battle Advisor.",
            },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Peaceful One", Slug = "peaceful-one", Subtype = "npc", Visibility = "restricted" },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Mistaken One", Slug = "mistaken-one", Subtype = "npc", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Dutiful One", Slug = "dutiful-one", Subtype = "npc", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Nadar", Subtype = "deity", Visibility = "party" },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Arch mage", Subtype = "npc", Visibility = "party" },
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "The War God's axe", Slug = "war-gods-axe", Visibility = "party" },
            new CampaignOpSpec { Op = "objective", Ref = "quest:war-gods-axe", Text = "Assemble the War God's axe", Progress = 2 },
            new CampaignOpSpec { Op = "upsert", Kind = "secret", Name = "Fruits are the seal", Visibility = "author" },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "faction", Name = "G.O.D.S. Co.", Slug = "gods-co", Subtype = "company", Visibility = "party",
                Aliases = [new AliasSpec { Alias = "Global Order Dissemination Service Company", Visibility = "party" }],
                BodyMd = "Protecting the coal mining islanders while squeezing them dry of money.",
                SecretMd = "Its founders destroyed Silk Isle to take the island's rope en masse and build their fleet (direction, not locked).",
            },
            new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Silk Isle", Visibility = "party" },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "question", Name = "What destroyed Silk Isle?", Slug = "q7", Code = "Q7", Subtype = "gap", Status = "withheld", Visibility = "party",
                BodyMd = "He believes the island destroyed, but keeps finding his people's rope- and weave-work out in the world.",
                SecretMd = "Direction (not yet locked): the founders of G.O.D.S. Co., to take the island's rope en masse for building their fleet.",
            },
            new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "How rare is the long-lived lineage?", Slug = "q21", Code = "Q21", Subtype = "design", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "How long ago was the Keras–Baal fight, on the corrected timeline?", Slug = "q22", Code = "Q22", Subtype = "design", Visibility = "author" });

        var everyone = new[] { ("character:bjorn-mountainfell", true), ("character:dragon-slayer", true) };
        foreach (var (number, title) in new[] { (1, SessionOneTitle), (5, "Isle of Craftsmen"), (6, "Ohara — the arch mage's vision"), (7, "Blood-moon island") })
        {
            f.Sessions.RecordPast(campaign, number, title, precision: "unknown", confidence: "approximate",
                attendance: everyone.Select(a => (AttendanceSpec?)new AttendanceSpec { Character = a.Item1, Present = a.Item2 }).ToList());
        }

        f.Sessions.Plan(campaign, 8, "Craftsmen isle — return visit");
        foreach (var number in new[] { 9, 10, 11, 12 })
        {
            f.Sessions.Plan(campaign, number);
        }

        // Every fact first (so gates can name them), capturing each f:<n> from the result.
        static CampaignOpSpec Clue(string statement) => new()
        {
            Op = "fact", Statement = statement, FactType = "clue", Visibility = "restricted",
            Links = [new FactLinkSpec { Ref = Secret, Role = "clue_for" }],
        };

        var created = f.Apply(campaign,
            new CampaignOpSpec { Op = "fact", Statement = "The War God's axe is fully assembled.", CanonStatus = "planned", Visibility = "party" },
            new CampaignOpSpec { Op = "fact", Statement = "The party holds at least one devil fruit and at least one axe shard.", CanonStatus = "played", EstablishedSession = 5, Visibility = "party" },
            new CampaignOpSpec { Op = "fact", Statement = "The party has no uneaten devil fruits on hand.", CanonStatus = "planned" },
            Clue("The Protector kept the Cage engaging: \"It held.\""),
            Clue("The Peaceful One kept the split itself, including wrong-place moonlight."),
            Clue("The Mistaken One kept the aftermath: \"It broke. Because I was slow.\""),
            Clue("The Dutiful One kept the orders, briefed within minutes."),
            new CampaignOpSpec
            {
                Op = "fact", Statement = "In the arch mage's vision, the moon goddess was protecting the arch mage as a child.", FactType = "clue",
                CanonStatus = "played", EstablishedSession = 6, Links = [new FactLinkSpec { Ref = Secret, Role = "clue_for" }],
            },
            Clue("Re-watched with a specific question, the arch mage's illusion shows moonlight wrapping the Cage's shards."),
            Clue("Planar breach counts are climbing."),
            Clue("Temple records show devil fruit appearances falling over the same period."),
            new CampaignOpSpec
            {
                // Restricted, not author: the party must be able to be shown it once T5 reveals it (class summary).
                Op = "fact", Statement = "Nadar routes fruits and axe shards to the party knowing every eaten fruit thins Baal's seal; it is part of his plan to have them kill Baal.",
                FactType = "secret", Visibility = "restricted",
            },
            new CampaignOpSpec { Op = "fact", Statement = "Keras was barely too slow to pull off the Cage properly.", CanonStatus = "ruled" },
            new CampaignOpSpec { Op = "fact", Statement = "Keras was not alone at the Cage; he had help.", CanonStatus = "ruled" },
            new CampaignOpSpec { Op = "fact", Statement = "The Cage broke because Keras alone was slow; the fault is all his.", FactType = "belief", Truth = "partial" },
            new CampaignOpSpec { Op = "fact", Statement = "Sky-world is ~100–200 years after the first campaign; the present is ≈500–900 years after it.", CanonStatus = "canon" },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Sky-world is at least 1,000 years after the first campaign; the present is hundreds of years — maybe a thousand — after sky-world.",
                CanonStatus = "ruled", Confidence = "approximate",
            },
            new CampaignOpSpec { Op = "fact", Statement = "INVENTED accepted invention", Code = "F1", CanonStatus = "accepted" },
            new CampaignOpSpec { Op = "fact", Statement = "INVENTED struck invention", Code = "F2", CanonStatus = "struck" });
        var h = created.Applied.Select(a => a.Ref).ToList();
        var (axe, holds, nuf, tProtector, tPeaceful, tMistaken, tDutiful, shielded, illusion, breaches, falling, nadarPlan, tooSlow, hadHelp, totalFault, timelineOld, timeline2026, f1, f2) =
            (h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], h[8], h[9], h[10], h[11], h[12], h[13], h[14], h[15], h[16], h[17], h[18]);

        var more = f.Apply(campaign,
            new CampaignOpSpec
            {
                // Restricted, not author, like @nadar-plan (class summary).
                Op = "fact", Statement = "Eating a devil fruit breaks part of Baal's seal.", FactType = "secret", Truth = "true", CanonStatus = "canon",
                Visibility = "restricted", About = [Secret],
                Gate = new GateSpec
                {
                    After = [axe, holds],
                    With = [nadarPlan],
                    Prefer = [nuf],
                    ForbiddenTerms = ["seal"],
                    ForbiddenUntil = [axe],
                    PreferredTerms = ["shell", "wrapping", "what keeps it in"],
                    Seeds = [shielded],
                    Routes =
                    [
                        new RouteSpec { Id = "testimonies", Clues = [tProtector, tPeaceful, tMistaken, tDutiful], MinClues = 4 },
                        new RouteSpec { Id = "illusion", Clues = [illusion], MinClues = 1 },
                        new RouteSpec { Id = "temple-arithmetic", Clues = [breaches, falling], MinClues = 2 },
                    ],
                    MinRoutes = 2,
                    Note = "Reveal order — do not break this (canon-core.md:172-195)",
                },
            },
            new CampaignOpSpec { Op = "fact", Statement = "Whole Keras believed he alone shattered and called the outcome near-best-case.", SupersededBy = hadHelp },
            new CampaignOpSpec { Op = "fact", Statement = "The Keras–Baal fight was ~500–900 years ago.", DependsOn = [timelineOld], About = ["question:q21", "character:arch-mage"] },
            new CampaignOpSpec { Op = "fact", Statement = "The fragments have been running down for roughly 400 years.", DependsOn = [timelineOld], About = ["question:q22", "character:protector"] },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The founders of G.O.D.S. Co. destroyed Silk Isle to take its rope en masse for their fleet.", FactType = "secret",
                CanonStatus = "lean", Visibility = "author", About = ["question:q7", "faction:gods-co", "location:silk-isle"],
            });
        var seal = more.Applied[0].Ref;
        var nearBest = more.Applied[1].Ref;
        var fight = more.Applied[2].Ref;
        var fragments = more.Applied[3].Ref;
        var q7Answer = more.Applied[4].Ref;
        var lineage = Single(f.Apply(campaign, new CampaignOpSpec
        {
            Op = "fact", Statement = "The long-lived lineage covers the ~500–900 years since the Keras–Baal fight comfortably.",
            DependsOn = [fight], About = ["question:q21"],
        }));
        f.Apply(campaign, new CampaignOpSpec { Op = "fact", Ref = nadarPlan, Gate = new GateSpec { With = [seal], Note = "the two land together or not at all (CC:177-178)" } });

        // Knowledge, as of session 7. What the party learned at the table is written in the session it was learned, so
        // the change_log files it (and the secret-status update it derives) under that session and point-in-time replay
        // sees it arrive then: as of session 5 the secret is hidden, from session 6 (the seed) seeded (row 16). The
        // fragments' own knowledge is timeless backstory.
        var context = WriteContext.Default;
        f.Knowledge.Record(campaign, ["character:protector"],
            [new KnowerSpec { Who = "party", State = "unrecognized", Session = 1, How = "witnessed", KnownAs = "the advisor in Serret" }], WriteContext.For(1));
        f.Knowledge.Record(campaign, ["character:peaceful-one"],
            [new KnowerSpec { Who = "party", State = "unrecognized", Session = 5, How = "witnessed", KnownAs = "the man napping under the tree" }], WriteContext.For(5));
        f.Knowledge.Record(campaign, [shielded], [new KnowerSpec { Who = "party", Session = 6, Via = "character:arch-mage", How = "witnessed" }], WriteContext.For(6));
        f.Knowledge.Record(campaign, [holds], [new KnowerSpec { Who = "party", Session = 5 }], WriteContext.For(5));
        f.Knowledge.Record(campaign, [tProtector], [Op.Knower("character:protector")], context);
        f.Knowledge.Record(campaign, [tPeaceful], [Op.Knower("character:peaceful-one")], context);
        f.Knowledge.Record(campaign, [tMistaken, tooSlow], [Op.Knower("character:mistaken-one")], context);
        f.Knowledge.Record(campaign, [tDutiful], [Op.Knower("character:dutiful-one")], context);
        f.Knowledge.Record(campaign, [totalFault], [Op.Knower("character:mistaken-one", "believes")], context);
        f.Knowledge.Record(campaign, [hadHelp], [Op.Knower("character:mistaken-one", "unaware")], context);
        f.Knowledge.Record(campaign, [seal, nadarPlan], [Op.Knower("character:nadar")], context);

        return new OnePieceFixture(f, f.Reload(campaign), new OnePieceFacts(
            seal, nadarPlan, axe, holds, nuf, tProtector, tPeaceful, tMistaken, tDutiful, shielded, illusion, breaches, falling,
            tooSlow, hadHelp, totalFault, nearBest, timelineOld, timeline2026, fight, fragments, lineage, q7Answer, f1, f2));
    }

    /// <summary>
    /// T1, session 8: the party learns the Protector's testimony, in its own words (<see cref="TestimonyAsThePartyHeardIt"/>):
    /// campaign_knowledge record with a known_as, as a DM records what the table heard from someone it knows under another
    /// name. Without the phrasing the party is shown the statement, which names the Protector (class summary).
    /// </summary>
    public WriteResult T1() => F.Knowledge.Record(Campaign, [Facts.TProtector],
        [new KnowerSpec { Who = "party", State = "knows", KnownAs = TestimonyAsThePartyHeardIt }], WriteContext.For(8));

    /// <summary>T2, session 9: the party learns the re-watched illusion.</summary>
    public WriteResult T2() => Record(9, Facts.IllusionRewatched);

    /// <summary>T3, session 10: the party learns both temple clues in one call.</summary>
    public WriteResult T3() => Record(10, Facts.BreachesClimbing, Facts.FruitAppearancesFalling);

    /// <summary>T4, session 11: the axe is assembled (played, established in 11).</summary>
    public WriteResult T4() => F.Apply(Campaign, WriteContext.For(11),
        new CampaignOpSpec { Op = "fact", Ref = Facts.AxeAssembled, CanonStatus = "played", EstablishedSession = 11 });

    /// <summary>T5, session 12: the seal and Nadar's plan are revealed to the party in one call.</summary>
    public WriteResult T5(bool dryRun = false) => Reveal(12, dryRun, Facts.Seal, Facts.NadarPlan);

    public WriteResult Record(int session, params string[] facts) =>
        F.Knowledge.Record(Campaign, facts, [Op.Knower("party")], WriteContext.For(session));

    public WriteResult Reveal(int session, bool dryRun, params string[] facts) =>
        F.Knowledge.Reveal(Campaign, facts, null, null, ["party"], null, WriteContext.For(session, dryRun: dryRun));

    /// <summary>The secret entity's stored (derived) status.</summary>
    public string? SecretStatus() => F.Entity(Campaign, Secret).Status;

    private static string Single(WriteResult result) => result.Applied.Single().Ref;
}
