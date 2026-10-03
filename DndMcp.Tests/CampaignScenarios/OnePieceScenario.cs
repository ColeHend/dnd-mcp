using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The One Piece reveal-gate world of <c>understand-onepiece.md</c> §2 for the exit-criteria scenarios: W's
/// <see cref="OnePieceFixture"/> (built entirely through the write path's services: campaign create, campaign_write ops,
/// campaign_session record_past / plan, campaign_knowledge record), extended by three batches written through the same
/// services (two corrections and one addition), with T1 written in the party's own words, and read back through the read
/// path's readers (<see cref="ScenarioReads"/>). Baseline is "as of session 7"; <see cref="T1"/>..<see cref="T5"/> are
/// the test steps of §3 (sessions 8-12).
///
/// <para>
/// The fixture is referenced, not copied (contract §10), so a W change that breaks the scenario fails here as well as in
/// W's own tests. Two corrections, each needed for a golden to be reachable at all, one step written with the party's own
/// words, and one addition for the leak property:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>@seal</c> and <c>@nadar-plan</c> become <c>restricted</c> (the fact default) instead of <c>author</c>: author
/// visibility is absolute (contract §3.2), so an author-visibility fact can never be shown to the party even after T5
/// reveals it, and row 40 ("after T5, a party search for seal returns @seal and @nadar-plan") could never pass. This is
/// the stage-2 decision recorded in contract §8. Written with no session context, so it is timeless for point-in-time
/// reads (as of session 5 the facts are already restricted).
/// </item>
/// <item>
/// Session 1 is re-titled "Small town — the dragon", dropping the research's "…and the Protector" (a record_past of the
/// played session, which campaign_session files under session 1 itself): a played session is party-visible, so a title
/// naming the Protector would tell the party the true name of the fragment it knows only as "the advisor in Serret"
/// (row 39's point), and a party search for "protector" would find the session.
/// </item>
/// <item>
/// <see cref="T1"/> records the Protector's testimony for the party under the party's own phrasing
/// (<see cref="TestimonyAsThePartyHeardIt"/>). The research's step names no phrasing, and W's fixture records the bare
/// statement "The Protector kept the Cage engaging", which the party would then be shown: the same leak of the advisor's
/// true name as the session title, arriving at T1 instead. Route counting reads only the row's state, so rows 18-23 are
/// unchanged; <c>OnePieceSearchScenarioTests</c> pins what W's unphrased step shows the party.
/// </item>
/// <item>
/// Nadar (party-visible) has a party-visible relation to the author-only Mistaken One. The research world has no
/// relation whose other end a view cannot see, so without one no leak property could notice a reader that prints such an
/// edge (contract §3.2: a relation is visible only when both ends are). Nothing in §3's goldens reads Nadar's relations.
/// </item>
/// </list>
/// <para>
/// The steps write in sessions 8-12, which stay <c>planned</c> (nobody plays them through campaign_session): verdicts do
/// not depend on a session's status, but every non-author reader hides unplayed sessions (contract §8), and
/// <see cref="PlannedSessionScenarioTests"/> pins both halves of that.
/// </para>
/// </summary>
public sealed class OnePieceScenario : IDisposable
{
    public const string Secret = OnePieceFixture.Secret;

    /// <summary>Session 1's party-safe title (the correction above).</summary>
    public const string SessionOneTitle = "Small town — the dragon";

    /// <summary>The party's own words for the Protector's testimony (T1's known_as): what the advisor in Serret told them.</summary>
    public const string TestimonyAsThePartyHeardIt = "The advisor in Serret said the Cage kept engaging: \"It held.\"";

    /// <summary>The player-side views of this DM campaign (dm is the author view in a DM campaign, so it is not one).</summary>
    public static readonly IReadOnlyList<string> PlayerViews =
        ["party", "table", "public", "character:bjorn-mountainfell", "character:dragon-slayer"];

    /// <summary>
    /// Every non-author perspective the leak property runs over: the player views plus NPC characters with knowledge of
    /// their own (the Mistaken One knows his half of the Cage story, Nadar knows the seal, the Protector and the arch mage).
    /// </summary>
    public static readonly IReadOnlyList<string> NonAuthorPerspectives =
    [
        .. PlayerViews, "character:mistaken-one", "character:nadar", "character:protector", "character:arch-mage",
    ];

    private readonly OnePieceFixture _fixture;

    private OnePieceScenario(OnePieceFixture fixture)
    {
        _fixture = fixture;
        Reads = new ScenarioReads(fixture.F.Db.Database);
    }

    /// <summary>The write-path services and the database (W's fixture): scenarios that write go through these.</summary>
    public WriteFixture F => _fixture.F;

    /// <summary>The One Piece campaign (DM, 2024).</summary>
    public CampaignRow Campaign => _fixture.Campaign;

    /// <summary>The fact handles by their §2 names (<c>@seal</c> is <see cref="OnePieceFacts.Seal"/>).</summary>
    public OnePieceFacts Facts => _fixture.Facts;

    /// <summary>The read-path readers over the same database, called as the tools call them.</summary>
    public ScenarioReads Reads { get; }

    /// <summary>Builds the baseline (§2), as of session 7, in a database of its own.</summary>
    public static OnePieceScenario Build()
    {
        var f = new WriteFixture();
        try
        {
            var fixture = OnePieceFixture.Build(f);
            f.Apply(fixture.Campaign, new WriteContext { Reason = "Gated facts are restricted, so the party can be shown them once told (contract §8)." },
                new CampaignOpSpec { Op = "fact", Ref = fixture.Facts.Seal, Visibility = "restricted" },
                new CampaignOpSpec { Op = "fact", Ref = fixture.Facts.NadarPlan, Visibility = "restricted" });
            f.Sessions.RecordPast(fixture.Campaign, 1, SessionOneTitle);
            f.Apply(fixture.Campaign, new WriteContext { Reason = "An edge the party may not follow: its other end is author-only." },
                new CampaignOpSpec
                {
                    Op = "link", From = "character:nadar", Rel = "watches", To = "character:mistaken-one", Visibility = "party",
                    Label = "knows which fragment broke the Cage",
                });
            return new OnePieceScenario(fixture);
        }
        catch
        {
            f.Dispose();
            throw;
        }
    }

    /// <summary>
    /// T1, session 8: the party learns the Protector's testimony, in its own words (<see cref="TestimonyAsThePartyHeardIt"/>):
    /// campaign_knowledge record with a known_as, as a DM records what the table heard from someone it knows under another name.
    /// </summary>
    public WriteResult T1() => F.Knowledge.Record(Campaign, [Facts.TProtector],
        [new KnowerSpec { Who = "party", State = "knows", KnownAs = TestimonyAsThePartyHeardIt }], WriteContext.For(8));

    /// <summary>T2, session 9: the party learns the re-watched illusion.</summary>
    public WriteResult T2() => _fixture.T2();

    /// <summary>T3, session 10: the party learns both temple clues in one call.</summary>
    public WriteResult T3() => _fixture.T3();

    /// <summary>T4, session 11: the axe is assembled (played, established in 11).</summary>
    public WriteResult T4() => _fixture.T4();

    /// <summary>T5, session 12: the seal and Nadar's plan are revealed to the party in one call.</summary>
    public WriteResult T5(bool dryRun = false) => _fixture.T5(dryRun);

    /// <summary>T1..T4 in order (the state "after T4").</summary>
    public void ThroughT4()
    {
        T1();
        T2();
        T3();
        T4();
    }

    /// <summary>The party learns <paramref name="facts"/> in <paramref name="session"/> (campaign_knowledge record).</summary>
    public WriteResult Record(int session, params string[] facts) => _fixture.Record(session, facts);

    /// <summary>campaign_knowledge reveal of <paramref name="facts"/> to the party in <paramref name="session"/>.</summary>
    public WriteResult Reveal(int session, bool dryRun, params string[] facts) => _fixture.Reveal(session, dryRun, facts);

    /// <summary>The secret entity's stored (derived, written by the write path) status.</summary>
    public string? StoredSecretStatus() => _fixture.SecretStatus();

    /// <summary>The secret as the author's campaign_get shows it: stored status, derived status and each gate's routes.</summary>
    public SecretStatusView SecretView(int? asOf = null) =>
        Reads.Get(Campaign, "author", EntityIncludes.Default, asOf, Secret).Entities.Single().Author!.Secret!;

    public void Dispose() => F.Dispose();
}
