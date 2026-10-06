using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using Xunit;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: two processes on one campaigns.db (two <see cref="CampaignDatabase"/> instances, each with its own write
/// semaphore) never lose a combat update (CRIT Q10): every step reads the encounter inside its own BEGIN IMMEDIATE
/// transaction, so of two <c>next {from}</c> calls naming the same turn-holder exactly one moves the turn and the other is
/// refused ("the turn already moved"), of two <c>start</c>s exactly one makes an active fight and the other gets the
/// one-fight refusal, never a SQLITE_CONSTRAINT, and of two <c>end</c>s exactly one writes back (one batch, one end row)
/// and the other is told the fight ended.
/// </summary>
public sealed class CombatConcurrencyTests
{
    private static (int Succeeded, List<Exception> Failed) Race(params Action[] calls)
    {
        using var gate = new Barrier(calls.Length);
        var failures = new List<Exception>();
        var succeeded = 0;
        var tasks = calls.Select(call => Task.Run(() =>
        {
            gate.SignalAndWait();
            try
            {
                call();
                Interlocked.Increment(ref succeeded);
            }
            catch (Exception ex)
            {
                lock (failures)
                {
                    failures.Add(ex);
                }
            }
        })).ToArray();
        Task.WaitAll(tasks);
        return (succeeded, failures);
    }

    [Fact]
    public void TwoProcesses_NextFromTheSameTurnHolder_OneMovesTheTurn_TheOtherIsRefused()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Sheet("character:sidekick", CombatLifecycleTests.SidekickJson);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Race" });
        w.Combat.Initiative(w.Campaign, null, new Domain.Combat.InitiativeOp { Rolls = [new("hero", Total: 18), new("sidekick", Total: 15)] });
        using var other = w.F.Db.OtherProcess();
        var second = new CombatService(other, new QueueRoller());
        var turns = w.Log("Race").Count(r => r.Kind == "turn");

        var (succeeded, failed) = Race(
            () => w.Combat.Next(w.Campaign, null, "hero"),
            () => second.Next(w.Campaign, null, "hero"));

        Assert.Equal(1, succeeded);
        var refused = Assert.IsType<DndInputException>(Assert.Single(failed));
        Assert.Contains("Sidekick", refused.Message, StringComparison.Ordinal);
        Assert.Equal("Sidekick", w.State("Race").TurnHolder!.Name);
        Assert.Equal(turns + 1, w.Log("Race").Count(r => r.Kind == "turn"));
    }

    [Fact]
    public void TwoProcesses_EndAtOnce_ExactlyOneWriteBack_TheOtherIsToldTheFightEnded()
    {
        // Three tries: which end wins, and whether the loser's pre-read saw the fight still running, is up to the scheduler.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var w = CombatWorld.Dm();
            w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
            w.Combat.Start(w.Campaign, new StartRequest { Name = "Race" });
            w.Combat.Damage(w.Campaign, null, new Domain.Combat.DamageOp(["hero"]) { Amount = 10 });
            var batches = w.F.Count("SELECT count(DISTINCT batch_id) FROM change_log");
            using var other = w.F.Db.OtherProcess();
            var second = new CombatService(other, new QueueRoller());

            var (succeeded, failed) = Race(
                () => w.Combat.End(w.Campaign, null, new EndRequest()),
                () => second.End(w.Campaign, null, new EndRequest()));

            Assert.Equal(1, succeeded);
            var refused = Assert.IsType<DndInputException>(Assert.Single(failed));
            Assert.True(refused.Message.StartsWith("\"Race\" already ended", StringComparison.Ordinal) ||
                        refused.Message.StartsWith("No combat is running in sea", StringComparison.Ordinal), refused.Message);
            Assert.Equal(batches + 1, w.F.Count("SELECT count(DISTINCT batch_id) FROM change_log"));
            Assert.Equal(1L, w.F.Count("SELECT count(*) FROM combat_log WHERE kind = 'end'"));
            Assert.Equal(34, w.SheetOf("character:hero").Hp);
        }
    }

    [Fact]
    public void TwoProcesses_StartAtOnce_OneFightIsActive_TheOtherGetsTheRefusal()
    {
        using var w = CombatWorld.Dm();
        using var other = w.F.Db.OtherProcess();
        var second = new CombatService(other, new QueueRoller());

        var (succeeded, failed) = Race(
            () => w.Combat.Start(w.Campaign, new StartRequest { Name = "One", AddParty = false }),
            () => second.Start(w.Campaign, new StartRequest { Name = "Two", AddParty = false }));

        Assert.Equal(1, succeeded);
        var refused = Assert.IsType<DndInputException>(Assert.Single(failed));
        Assert.Contains("is already running in sea (one fight at a time)", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM encounter WHERE status = 'active'"));
        Assert.Equal(1L, w.F.Count("SELECT count(*) FROM encounter"));
    }
}
