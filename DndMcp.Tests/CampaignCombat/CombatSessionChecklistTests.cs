using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: a session that ends with a fight still running (active or paused) says so in its checklist (contract §14:
/// <see cref="SessionChecklist.ActiveEncounters"/>): the fight's sheet-seeded characters are written back only at
/// <c>combat end</c>, so a session closed over a live fight would leave their sheets behind. Ended and planned fights are
/// not running, and recording a PAST session lists none (it has nothing to do with today's fight).
/// </summary>
public sealed class CombatSessionChecklistTests
{
    [Fact]
    public void SessionEnd_WithFightsRunning_ListsThem_EndedAndPlannedAreNot()
    {
        using var w = CombatWorld.Dm();
        w.StartSession(1);
        w.Reload();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Over", AddParty = false });
        w.Combat.End(w.Campaign, null, new EndRequest());
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Later"));
        w.Combat.Prepare(w.Campaign, new PrepareRequest("Interrupted"));
        w.F.Query<int>("UPDATE encounter SET status = 'paused' WHERE name = 'Interrupted' RETURNING 1");
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Still going", AddParty = false });

        var ended = w.F.Sessions.End(w.Campaign, "The party fought.");

        var checklist = Assert.IsType<SessionChecklist>(ended.Checklist);
        Assert.Equal(["Interrupted", "Still going"], checklist.ActiveEncounters);
        Assert.False(checklist.IsEmpty);
    }

    [Fact]
    public void SessionEnd_NoFightRunning_NothingToList()
    {
        using var w = CombatWorld.Dm();
        w.StartSession(1);
        w.Reload();

        var ended = w.F.Sessions.End(w.Campaign, "Quiet night.");

        Assert.Empty(ended.Checklist!.ActiveEncounters!);
    }

    [Fact]
    public void RecordPast_ListsNoRunningFight_APastSessionHasNothingToDoWithIt()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Still going", AddParty = false });

        var recorded = w.F.Sessions.RecordPast(w.Campaign, 1, recapMd: "An old night.");

        Assert.Empty(recorded.Checklist!.ActiveEncounters!);
        Assert.Equal("active", w.Encounter("Still going").Status);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "The crypt" }, false)]
    public void IsEmpty_CountsTheRunningFights(string[]? running, bool empty)
    {
        var checklist = new SessionChecklist([], [], [], [], [], false, running);

        Assert.Equal(empty, checklist.IsEmpty);
    }
}
