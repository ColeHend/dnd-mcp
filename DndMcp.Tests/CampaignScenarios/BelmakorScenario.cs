using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The Belmakor world of <c>understand-belmakor.md</c> §2 ("the old king" / "the thing he wants" / the Axiom Cage) for
/// the exit-criteria scenarios: W's <see cref="BelmakorFixture"/>, built entirely through the write path's services, plus
/// two additions the §3 goldens need and the §2 tables leave out, each written through the same services:
/// <list type="bullet">
/// <item>
/// Belmakor's own knowledge row on <c>secret:belmakors-ambition</c> (row 29): the entity is <c>restricted</c>, which means
/// "known by exactly the knowers its rows name" (contract §3.7), so without a row not even he could find his own secret.
/// </item>
/// <item>
/// f:3 and f:4 linked <c>about</c> the old king (row 30 expects both on his page; §2.4 links them to the thread and the
/// item only).
/// </item>
/// </list>
/// Read-only once built, so a test class shares one instance (<see cref="Xunit.IClassFixture{TFixture}"/>); scenarios that
/// write build their own.
/// </summary>
public sealed class BelmakorScenario : IDisposable
{
    /// <summary>
    /// Strings no Belmakor-campaign view but the author's may contain (the §3 list, plus the author's own words about the
    /// import: "Cole" and "imported" appear only in secret_md and attendance notes).
    /// </summary>
    public static readonly IReadOnlyList<string> AlwaysForbidden = ["Axiom", "Cage", "Baal", "Third Silence", "one-piece", "Cole", "imported"];

    /// <summary>Words only Belmakor (and the dm, who knows f:5) may see: his hidden ambition.</summary>
    public static readonly IReadOnlyList<string> AmbitionWords = ["reclaim", "blighted", "outstrips"];

    /// <summary>
    /// Every non-author perspective of this player campaign: the party-side knowers, every PC (Tristan is dead but still a
    /// member), and the old king himself as a character perspective (the entity whose true name is the secret).
    /// </summary>
    public static readonly IReadOnlyList<string> NonAuthorPerspectives =
    [
        "party", "table", "dm", "public", "character:belmakor", "character:vars", "character:ignis", "character:serif",
        "character:torch", "character:aiden-ironstar", "character:tristan", "character:old-king",
    ];

    public BelmakorScenario()
    {
        F = new WriteFixture();
        try
        {
            var built = BelmakorFixture.Build(F);
            F.Knowledge.Record(built.Belmakor, ["secret:belmakors-ambition"],
                [new KnowerSpec { Who = "character:belmakor", State = "knows", How = "backstory" }], WriteContext.Default);
            F.Apply(built.Belmakor,
                new CampaignOpSpec { Op = "fact", Ref = "f:3", About = ["character:old-king"] },
                new CampaignOpSpec { Op = "fact", Ref = "f:4", About = ["character:old-king"] });
            Campaign = F.Reload(built.Belmakor);
            OnePiece = F.Reload(built.OnePiece);
            Reads = new ScenarioReads(F.Db.Database);
        }
        catch
        {
            F.Dispose();
            throw;
        }
    }

    /// <summary>The write-path services and the database (W's fixture): scenarios that write go through these.</summary>
    public WriteFixture F { get; }

    /// <summary>The Belmakor campaign (player, 2014).</summary>
    public CampaignRow Campaign { get; }

    /// <summary>The One Piece campaign that holds the cross-link endpoints (Keras, the Axiom Cage).</summary>
    public CampaignRow OnePiece { get; }

    /// <summary>The read-path readers over the same database, called as the tools call them.</summary>
    public ScenarioReads Reads { get; }

    /// <summary>
    /// The forbidden strings for one perspective (the golden's list names the dm too: "party, table, dm, public,
    /// character:*"): "Keras" for every one of them, the ambition for all but Belmakor and the dm. The dm was told f:2 ("The
    /// old king's name is Keras."), so f:2's own text is the one place she may meet the name: check her results with
    /// <see cref="LeakProbe.CleanExceptToldFacts"/> and <see cref="ToldFacts"/>, never by dropping the word.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenFor(string perspective)
    {
        var list = new List<string>(AlwaysForbidden) { "Keras" };
        if (perspective is not ("dm" or "character:belmakor"))
        {
            list.AddRange(AmbitionWords);
        }

        return list;
    }

    /// <summary>
    /// The facts whose own text may carry one of <see cref="ForbiddenFor"/>'s strings for this perspective: f:2 for the dm,
    /// who was told it.
    /// </summary>
    public static IReadOnlyList<string> ToldFacts(string perspective) => perspective == "dm" ? ["f:2"] : [];

    public void Dispose() => F.Dispose();
}
