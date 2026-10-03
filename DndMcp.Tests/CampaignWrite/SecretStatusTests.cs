using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: a secret with several gated facts is <c>revealed</c> only when the party knows every one of them, and
/// <c>partial</c> as soon as it holds any part (knows or suspects one gated fact), never <c>hidden</c> while the party
/// knows part of it; a clue alone makes it <c>seeded</c>. The One Piece secret has one gated fact, so it cannot tell "all"
/// from "any": a status that read "revealed" after half the secret came out would tell the author the table is further
/// along than it is.
/// </summary>
public sealed class SecretStatusTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _c;

    public SecretStatusTests()
    {
        _c = _f.Campaign("Two gates");
        _f.Apply(_c,
            Op.Upsert("secret", "The heir"),
            Op.Fact("The coronation happened."),
            new CampaignOpSpec { Op = "fact", Statement = "The smith's hands are too fine.", Links = [new FactLinkSpec { Ref = "secret:the-heir", Role = "clue_for" }] });
        _f.Apply(_c,
            new CampaignOpSpec { Op = "fact", Statement = "The heir lives.", About = ["secret:the-heir"], Gate = new GateSpec { After = ["f:1"] } },
            new CampaignOpSpec { Op = "fact", Statement = "The heir is the smith.", About = ["secret:the-heir"], Gate = new GateSpec { After = ["f:1"] } });
    }

    public void Dispose() => _f.Dispose();

    [Theory]
    [InlineData("", "", "hidden")]
    [InlineData("f:2", "", "seeded")]
    [InlineData("f:3", "", "partial")]
    [InlineData("f:4", "", "partial")]
    [InlineData("", "f:3", "partial")]
    [InlineData("f:3", "f:4", "partial")]
    [InlineData("f:3,f:4", "", "revealed")]
    [InlineData("f:2,f:3,f:4", "", "revealed")]
    public void DerivedStatus_TwoGatedFacts_RevealedOnlyWhenThePartyKnowsBoth(string knows, string suspects, string expected)
    {
        Record(knows, CampaignValues.KnowledgeStates.Knows);
        Record(suspects, CampaignValues.KnowledgeStates.Suspects);

        Assert.Equal(expected, _f.Entity(_c, "secret:the-heir").Status);
    }

    [Fact]
    public void DerivedStatus_TheSecondGatedFactLearnedLater_MovesPartialToRevealed()
    {
        var first = _f.Knowledge.Record(_c, ["f:3"], [Op.Knower("party")], WriteContext.Default);
        var second = _f.Knowledge.Record(_c, ["f:4"], [Op.Knower("party")], WriteContext.Default);

        Assert.Contains(first.Consequences, c => c.Message.Contains("hidden → partial", StringComparison.Ordinal));
        Assert.Contains(second.Consequences, c => c.Message.Contains("partial → revealed", StringComparison.Ordinal));
    }

    private void Record(string facts, string state)
    {
        var targets = facts.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (targets.Length > 0)
        {
            _f.Knowledge.Record(_c, targets, [Op.Knower("party", state)], WriteContext.Default);
        }
    }
}
