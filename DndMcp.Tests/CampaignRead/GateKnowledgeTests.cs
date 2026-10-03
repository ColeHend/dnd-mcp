using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignRead;

/// <summary>
/// Invariant: for gates, "the party knows" is what the party's own reads show, in the secret status the write path
/// stores and in everything the read path derives (the gate view, the secret's derived status, the check's active
/// vocabulary). An author-only fact is known to no player-side view whatever its rows say (contract §3.2), so a party row
/// on one reveals nothing, completes no route and lifts no vocabulary; and as of a session a fact the story establishes
/// later is not known then by its visibility (contract §3.4), so a route resting on it is incomplete before its session.
///
/// <para>
/// Why it fails silently: the verdict alone says "knows" in both cases, so the stored and derived statuses agreed with
/// each other while disagreeing with every party read, and the check let the party say "seal" because of a row no party
/// read honours. The data is written through the write path, as the tools write it, so the status W stores is checked
/// beside the one the reader derives.
/// </para>
/// </summary>
public sealed class GateKnowledgeTests : IDisposable
{
    private const string Seal = "seal";

    private readonly WriteFixture _f = new();
    private readonly CampaignRow _campaign;
    private readonly string _secret;

    public GateKnowledgeTests()
    {
        _campaign = _f.Campaign("Deep");
        _secret = _f.Apply(_campaign, Op.Upsert("secret", "Fruits are the seal")).Applied.Single().Ref;
    }

    public void Dispose() => _f.Dispose();

    // The secret's stored and derived status, with its gates, as the author's get prints them.
    private SecretStatusView Secret(int? asOf = null) =>
        new EntityReader(_f.Db.Database).Get(_f.Reload(_campaign), [_secret], EntityIncludes.Default, Perspective.Author, asOf)
            .Entities.Single().Author!.Secret!;

    private CheckResult PartyCheck(string text, int? asOf = null) =>
        new KnowledgeCheck(_f.Db.Database).Check(_f.Reload(_campaign), new CheckRequest(text, Perspective.Parse("party"), AsOfSession: asOf));

    private string Fact(string statement, string visibility, GateSpec? gate = null, bool aboutTheSecret = false, int? establishedSession = null) =>
        _f.Apply(_campaign, new CampaignOpSpec
        {
            Op = "fact", Statement = statement, Visibility = visibility, Gate = gate, About = aboutTheSecret ? [_secret] : null,
            CanonStatus = establishedSession is null ? null : CampaignValues.CanonStatuses.Played, EstablishedSession = establishedSession,
        }).Applied.Single().Ref;

    /// <summary>
    /// A party row on an author-only gated fact (written with a warning, and applied) does not make the party know it, nor
    /// suspect it: the gate's vocabulary, which lifts when the party knows the gated fact, stays active for a party speaker;
    /// the gate view says the party does not know it; and the secret is neither revealed nor partial, in the status W stores
    /// nor in the one the reader derives. With the fact restricted the same row reveals it (or, suspected, makes it partial).
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.Visibilities.Author, CampaignValues.KnowledgeStates.Knows, 1, false, CampaignValues.Statuses.SecretHidden)]
    [InlineData(CampaignValues.Visibilities.Restricted, CampaignValues.KnowledgeStates.Knows, 0, true, CampaignValues.Statuses.SecretRevealed)]
    [InlineData(CampaignValues.Visibilities.Author, CampaignValues.KnowledgeStates.Suspects, 1, false, CampaignValues.Statuses.SecretHidden)]
    [InlineData(CampaignValues.Visibilities.Restricted, CampaignValues.KnowledgeStates.Suspects, 1, false, CampaignValues.Statuses.SecretPartial)]
    public void PartyRowOnAGatedFact_CountsOnlyWhenTheFactIsNotAuthorOnly(string visibility, string state, int flags, bool knownToParty, string status)
    {
        var gated = Fact("Every eaten fruit breaks part of the seal.", visibility, new GateSpec { ForbiddenTerms = [Seal] }, aboutTheSecret: true);

        var record = _f.Knowledge.Record(_campaign, [gated], [Op.Knower("party", state)], WriteContext.Default);

        var secret = Secret();
        Assert.Equal(visibility == CampaignValues.Visibilities.Author, record.Warnings.Any(w => w.Kind == WarningKinds.AuthorVisibility));
        Assert.Equal(flags, PartyCheck("The seal holds.").Forbidden.Count(f => f.Source == gated));
        Assert.Equal((knownToParty, knownToParty), (Assert.Single(secret.Gates).KnownToParty, !Assert.Single(secret.Gates).ForbiddenActive));
        Assert.Equal((status, status), (secret.StoredStatus, secret.DerivedStatus));
    }

    /// <summary>
    /// The clue side of the same rule: a party row on an author-only route clue completes no route and seeds nothing, so
    /// the gate is not ready and the secret stays hidden, stored and derived; a restricted clue with the same row completes
    /// the route (partial).
    /// </summary>
    [Theory]
    [InlineData(CampaignValues.Visibilities.Author, false, CampaignValues.Statuses.SecretHidden)]
    [InlineData(CampaignValues.Visibilities.Restricted, true, CampaignValues.Statuses.SecretPartial)]
    public void PartyRowOnARouteClue_CompletesTheRouteOnlyWhenTheClueIsNotAuthorOnly(string visibility, bool complete, string status)
    {
        var clue = Fact("Temple records show the fruits thinning.", visibility);
        Fact("Every eaten fruit breaks part of the seal.", CampaignValues.Visibilities.Restricted,
            new GateSpec { Routes = [new RouteSpec { Id = "records", Clues = [clue] }] }, aboutTheSecret: true);

        _f.Knowledge.Record(_campaign, [clue], [Op.Knower("party")], WriteContext.Default);

        var gate = Assert.Single(Secret().Gates);
        Assert.Equal((complete, complete, complete), (Assert.Single(gate.Routes).Complete, gate.Ready, gate.Seeded));
        Assert.Equal((status, status), (Secret().StoredStatus, Secret().DerivedStatus));
    }

    /// <summary>
    /// As of a session, a route clue the story establishes later is not known to the party by its visibility, so the
    /// route is incomplete then: the gate is not ready, the derived status is not partial, and the gated fact's vocabulary
    /// is active for a party speaker. From the clue's session on, the route is complete and the gate ready. The stored
    /// status was derived by W in a batch with no session (timeless: the replay never reverses it), so as of the earlier
    /// session it already counts the clue; the derived status is the one that answers "as of n" (ReadGates' class summary).
    /// </summary>
    [Fact]
    public void GateAsOfASession_ARouteClueEstablishedLater_IsNotKnownToThePartyBeforeItsSession()
    {
        for (var n = 1; n <= 5; n++)
        {
            _f.Played(_campaign, n);
        }

        var clue = Fact("Temple records show the fruits thinning.", CampaignValues.Visibilities.Party, establishedSession: 5);
        var gated = Fact("Every eaten fruit breaks part of the seal.", CampaignValues.Visibilities.Restricted,
            new GateSpec { Routes = [new RouteSpec { Id = "records", Clues = [clue] }], ForbiddenTerms = [Seal] }, aboutTheSecret: true);

        var asOf4 = Secret(4);
        var asOf5 = Secret(5);

        var before = Assert.Single(asOf4.Gates);
        var after = Assert.Single(asOf5.Gates);
        Assert.Equal((false, false, true), (Assert.Single(before.Routes).Complete, before.Ready, before.ForbiddenActive));
        Assert.Equal((CampaignValues.Statuses.SecretHidden, CampaignValues.Statuses.SecretPartial), (asOf4.DerivedStatus, asOf4.StoredStatus));
        Assert.Single(PartyCheck("The seal holds.", 4).Forbidden, f => f.Source == gated);
        Assert.Equal((true, true), (Assert.Single(after.Routes).Complete, after.Ready));
        Assert.Equal((CampaignValues.Statuses.SecretPartial, CampaignValues.Statuses.SecretPartial), (asOf5.DerivedStatus, asOf5.StoredStatus));
        Assert.Equal(CampaignValues.Statuses.SecretPartial, Secret().DerivedStatus);
    }
}
