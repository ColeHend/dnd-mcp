using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignRead;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant (the contract §8 stage-2 decisions, pinned on the One Piece scenario): unplayed sessions (planned, prepped,
/// cancelled) are invisible to every non-author view, while knowledge written "in" such a session still counts. The
/// scenario's steps T1-T5 write under sessions 8-12, which the fixture plans and never plays, so both halves meet here: the
/// party knows what T1 told it, but its session list, search and get never show the planned session 8 ("Craftsmen isle —
/// return visit" is prep, and prep is the author's). The research tables do not say either way; this is the design's
/// answer, and a change to it should be a decision, not an accident.
/// </summary>
public sealed class PlannedSessionScenarioTests : IDisposable
{
    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private IReadOnlyList<int> Sessions(string perspective) =>
        _world.Reads.SessionReader.List(_world.Campaign, null, 50, null, Domain.Campaign.Perspective.Parse(perspective)).Sessions.Select(s => s.Number).ToList();

    [Fact]
    public void List_ThePlayerViews_SeeOnlyThePlayedSessionsTheAuthorSeesAll()
    {
        Assert.Equal([1, 5, 6, 7, 8, 9, 10, 11, 12], Sessions("author"));
        Assert.All(OnePieceScenario.PlayerViews.Where(p => p != "public"), p => Assert.Equal([1, 5, 6, 7], Sessions(p)));
    }

    [Fact]
    public void List_AfterTheWholeScenario_StepsWrittenInPlannedSessionsDoNotMakeThemVisible()
    {
        _world.ThroughT4();
        _world.T5();

        Assert.Equal([1, 5, 6, 7], Sessions("party"));
    }

    [Fact]
    public void SearchAndGet_ThePlannedSessionsTitle_IsAuthorOnly()
    {
        var author = _world.Reads.Search(_world.Campaign, "return visit");
        var party = _world.Reads.Search(_world.Campaign, "return visit", "party");

        Assert.Contains(author.Entities, e => e.Kind == "session");
        Assert.Empty(party.Entities);
        Assert.Throws<DndInputException>(() => _world.Reads.Get(_world.Campaign, "party", EntityIncludes.All, null, "session:8"));
        Assert.Throws<DndInputException>(() => _world.Reads.SessionReader.Get(_world.Campaign, "8", Domain.Campaign.Perspective.Parse("party")));
    }

    /// <summary>
    /// T1 is written in planned session 8: the party knows the testimony from S8 (the verdict does not look at the
    /// session's status), and a party member with no attendance recorded for S8 knows it too ("attendance was not
    /// recorded"), as §3.3 says for NotRecorded.
    /// </summary>
    [Theory]
    [InlineData("party")]
    [InlineData("character:bjorn-mountainfell")]
    public void Ledger_KnowledgeLearnedInAPlannedSession_StillCounts(string perspective)
    {
        _world.T1();

        var cell = _world.Reads.Cell(_world.Campaign, _world.Facts.TProtector, perspective);

        Assert.Equal((Standings.Knows, 8), (cell.Standing, cell.LearnedSession));
        Assert.NotNull(_world.Reads.TryGet(_world.Campaign, perspective, _world.Facts.TProtector));
    }

    /// <summary>The planned sessions leave no trace in a player's view of the fact learned there: no session ref, no title.</summary>
    [Fact]
    public void Get_AFactLearnedInAPlannedSession_NamesNoHiddenSession()
    {
        _world.T1();

        var result = _world.Reads.Get(_world.Campaign, "party", EntityIncludes.All, null, _world.Facts.TProtector);

        LeakAssert.Clean(result, ["Craftsmen isle", "return visit", "session:8"], "a fact learned in planned S8");
    }
}
