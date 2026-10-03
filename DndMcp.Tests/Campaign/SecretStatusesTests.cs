using DndMcp.Domain.Campaign;
using Xunit;

namespace DndMcp.Tests.Campaign;

/// <summary>
/// Invariant: a secret's derived status takes the strongest thing true of it: revealed (every gated fact known to the
/// party) over partial (a complete route or a suspicion) over seeded (a seed or clue known) over hidden; and every value
/// it produces is a secret status the schema accepts.
/// </summary>
public sealed class SecretStatusesTests
{
    [Theory]
    [InlineData(true, true, true, "revealed")]
    [InlineData(true, false, false, "revealed")]
    [InlineData(false, true, true, "partial")]
    [InlineData(false, true, false, "partial")]
    [InlineData(false, false, true, "seeded")]
    [InlineData(false, false, false, "hidden")]
    public void Derive_Flags_GiveTheStrongestStatus(bool allGatedKnown, bool routeCompleteOrSuspected, bool seedOrClueKnown, string status)
    {
        Assert.Equal(status, SecretStatuses.Derive(allGatedKnown, routeCompleteOrSuspected, seedOrClueKnown));
    }

    [Fact]
    public void All_AreExactlyTheSecretKindsStatuses()
    {
        Assert.Equal(CampaignValues.Statuses.ByKind[CampaignValues.Kinds.Secret].Values.Order(), SecretStatuses.All.Order());
    }
}
