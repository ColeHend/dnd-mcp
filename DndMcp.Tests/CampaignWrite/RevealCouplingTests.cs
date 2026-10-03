using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: the parts of §3.4 the One Piece fixture cannot isolate: <c>with</c> is symmetric even when only one fact's
/// gate names the other; with no session context, only facts revealed in the same batch land together; and a
/// <c>clue_for</c> fact the party knows seeds its secret even when no gate lists it.
/// </summary>
public sealed class RevealCouplingTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _c;

    public RevealCouplingTests()
    {
        _c = _f.Campaign("Coupled");
        _f.Apply(_c,
            Op.Upsert("secret", "The heir"),
            new CampaignOpSpec { Op = "fact", Statement = "The heir lives.", Code = "S1" },
            new CampaignOpSpec { Op = "fact", Statement = "The heir is the cook.", About = ["secret:the-heir"], Gate = new GateSpec { With = ["S1"] } },
            new CampaignOpSpec { Op = "fact", Statement = "The cook knows court manners.", Links = [new FactLinkSpec { Ref = "secret:the-heir", Role = "clue_for" }] });
    }

    public void Dispose() => _f.Dispose();

    private static IReadOnlyList<GateWarning> With(WriteResult result) =>
        result.Warnings.OfType<GateWarning>().Where(w => w.Gate == GateConditions.With).ToList();

    [Fact]
    public void RevealingTheUngatedPartnerAlone_BreaksTheCouplingItIsNamedIn()
    {
        var result = _f.Knowledge.Reveal(_c, ["S1"], null, null, null, null, WriteContext.Default);

        var warning = Assert.Single(With(result));
        Assert.Equal("f:1", warning.Fact);
        Assert.Equal(["f:2"], warning.Unmet);
    }

    [Fact]
    public void WithNoSessionContext_BothInOneBatch_LandTogether()
    {
        var result = _f.Knowledge.Reveal(_c, ["f:1", "f:2"], null, null, null, null, WriteContext.Default);

        Assert.Null(result.SessionNumber);
        Assert.Empty(With(result));
    }

    [Fact]
    public void WithNoSessionContext_InTwoBatches_LandSeparately()
    {
        _f.Knowledge.Reveal(_c, ["f:1"], null, null, null, null, WriteContext.Default);

        var result = _f.Knowledge.Reveal(_c, ["f:2"], null, null, null, null, WriteContext.Default);

        var warning = Assert.Single(With(result), w => w.Fact == "f:2");
        Assert.Equal([new LandedSeparatelyItem("f:1", null)], warning.LandedSeparately);
    }

    [Fact]
    public void AClueForFactTheGatesDoNotList_StillSeedsTheSecret()
    {
        Assert.Equal("hidden", _f.Entity(_c, "secret:the-heir").Status);

        _f.Knowledge.Record(_c, ["f:3"], [Op.Knower("party")], WriteContext.Default);

        Assert.Equal("seeded", _f.Entity(_c, "secret:the-heir").Status);
    }
}
