using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignRead;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: the One Piece fixture built through the write path shows the party nothing that names the Protector, the
/// fragment it knows only as "the advisor in Serret", at any step: not session 1's title, not the testimony T1 gives it
/// (recorded in the party's own words), and not a search for "protector"; and its gated facts are restricted, so the party
/// is shown both once T5 reveals them (row 40). Every scenario built on this fixture (the read path's, the exit-criteria
/// ones) inherits these choices, so a fixture that leaked here would make every leak test downstream start from a leak.
/// </summary>
public sealed class OnePieceFixtureTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly OnePieceFixture _op;

    public OnePieceFixtureTests()
    {
        _op = OnePieceFixture.Build(_f);
    }

    public void Dispose() => _f.Dispose();

    private SearchResult Search(string query, string perspective) =>
        new CampaignSearch(_f.Db.Database).Search(_f.Reload(_op.Campaign), new SearchRequest(query, Perspective: Perspective.Parse(perspective)));

    // The party's knowledge row on a fact (by its f:<n> handle): its known_as, state and learned session's number.
    private (string? KnownAs, string State, long? Learned) PartyRow(string fact) =>
        _f.Query<(string? KnownAs, string State, long? Learned)>(
            "SELECT k.known_as, k.state, s.number FROM knowledge k JOIN fact f ON f.id = k.fact_id " +
            "LEFT JOIN session s ON s.entity_id = k.learned_session_id WHERE f.seq = @seq AND k.knower_kind = 'party'",
            new { seq = long.Parse(fact[2..], System.Globalization.CultureInfo.InvariantCulture) }).Single();

    [Fact]
    public void Build_SessionOne_IsTitledWithoutTheProtector()
    {
        var session = _f.Entity(_op.Campaign, "session:1");

        Assert.Equal(OnePieceFixture.SessionOneTitle, session.Name);
        Assert.DoesNotContain("Protector", session.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Search("protector", "party").Entities);
    }

    /// <summary>
    /// T1 records the party's row with its own words, so the party is shown those words (and finds the testimony by them),
    /// never the statement "The Protector kept the Cage engaging"; the row is still "knows", so the route counts it.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("character:bjorn-mountainfell")]
    public void T1_ThePartysRowInItsOwnWords_IsWhatEveryPlayerViewIsShown(string perspective)
    {
        _op.T1();

        var byItsWords = Search("advisor serret", perspective);
        var byTheName = Search("protector", perspective);
        var get = new EntityReader(_f.Db.Database).Get(_f.Reload(_op.Campaign), [_op.Facts.TProtector], EntityIncludes.All, Perspective.Parse(perspective));

        // Learned in session 8, T1's session (the research's step), so the route's timing (rows 18-23) is the research's.
        Assert.Equal((OnePieceFixture.TestimonyAsThePartyHeardIt, "knows", (long?)8), PartyRow(_op.Facts.TProtector));
        Assert.Equal(OnePieceFixture.TestimonyAsThePartyHeardIt, Assert.Single(byItsWords.Facts, f => f.Ref == _op.Facts.TProtector).Text);
        Assert.Empty(byTheName.Facts);
        Assert.Empty(byTheName.Entities);
        LeakAssert.Clean(new object[] { byItsWords, get }, ["Protector", "kept the Cage engaging"], $"T1 as {perspective}");
    }

    /// <summary>
    /// The gated facts are restricted (contract §8): after T1-T5 the party's search for "seal" returns exactly @seal and
    /// @nadar-plan (row 40). As author-only facts they would be written and never shown.
    /// </summary>
    [Fact]
    public void T5_TheGatedFactsAreRestricted_SoThePartyIsShownBothOnceRevealed()
    {
        Assert.All(new[] { _op.Facts.Seal, _op.Facts.NadarPlan },
            f => Assert.Equal(CampaignValues.Visibilities.Restricted, _f.Fact(_op.Campaign, f).Visibility));
        Assert.Empty(Search("seal", "party").Facts);

        _op.T1();
        _op.T2();
        _op.T3();
        _op.T4();
        _op.T5();

        Assert.Equal(new[] { _op.Facts.Seal, _op.Facts.NadarPlan }.Order(StringComparer.Ordinal),
            Search("seal", "party").Facts.Select(f => f.Ref).Order(StringComparer.Ordinal));
        Assert.Equal("revealed", _op.SecretStatus());
    }
}
