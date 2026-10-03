using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Invariant (PLAN exit criteria: "Undo works"; "undo reverses exactly one batch"): in the One Piece world, undoing a
/// batch B after an unrelated later batch C puts back exactly what B changed and nothing else. Stated over whole-database
/// dumps as set algebra: with D0 before B, D1 after B and D2 after C, the state after the undo is
/// <c>(D2 − (D1 − D0)) ∪ (D0 − D1)</c> — B's additions gone, B's removals back, C's work untouched — and change_log only
/// grows, by rows marked <c>undo_of = B</c>. A conflicting undo (a later batch changed a row B wrote) is refused, names
/// that batch, and writes nothing; undoing the later batch first clears the way. The batch kinds cover every write
/// service: a multi-op campaign_write, a knowledge record that moves a derived secret status, a supersession, a soft
/// delete and a campaign_session record_past.
/// </summary>
public sealed class UndoScenarioTests : IDisposable
{
    private readonly OnePieceScenario _world = OnePieceScenario.Build();

    public void Dispose() => _world.Dispose();

    private WriteFixture F => _world.F;

    /// <summary>One batch of each write kind the scenario uses, applied to the world; returns its batch id.</summary>
    private string ApplyBatch(string kind) => kind switch
    {
        "write" => F.Apply(_world.Campaign, WriteContext.For(7, "a new NPC with a fact the party knows"),
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Harbourmaster Quell", Subtype = "npc", Visibility = "party", Summary = "Runs the Serret docks." },
            Op.Link("character:harbourmaster-quell", "member_of", "faction:gods-co"),
            new CampaignOpSpec
            {
                Op = "fact", Statement = "Quell keeps the dock ledgers.", About = ["character:harbourmaster-quell"], Visibility = "party",
                KnownBy = [Op.Knower("party")],
            }).BatchId!,
        "knowledge" => _world.T2().BatchId!,
        "supersede" => F.Apply(_world.Campaign,
            new CampaignOpSpec { Op = "fact", Ref = _world.Facts.TimelineOld, CanonStatus = "superseded", SupersededBy = _world.Facts.Timeline20260830 }).BatchId!,
        "delete" => F.Apply(_world.Campaign, new CampaignOpSpec { Op = "delete", Ref = "location:silk-isle" }).BatchId!,
        "session" => F.Sessions.RecordPast(_world.Campaign, 13, "An extra night", precision: "unknown",
            attendance: [new AttendanceSpec { Character = "character:bjorn-mountainfell" }]).BatchId!,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A later batch that shares no row with any of the batches above.</summary>
    private string Unrelated() => F.Apply(_world.Campaign,
        new CampaignOpSpec { Op = "upsert", Kind = "location", Name = "Port Varra", Visibility = "party", Summary = "A harbour town." },
        new CampaignOpSpec { Op = "upsert", Ref = "character:nadar", Summary = "The War God." }).BatchId!;

    private static IReadOnlySet<string> Lines(string dump) => dump.Split('\n').ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData("write")]
    [InlineData("knowledge")]
    [InlineData("supersede")]
    [InlineData("delete")]
    [InlineData("session")]
    public void Undo_ABatchWithAnUnrelatedLaterBatch_ReversesExactlyThatBatch(string kind)
    {
        _world.T1();
        var d0 = Lines(F.Dump());
        var b = ApplyBatch(kind);
        var d1 = Lines(F.Dump());
        Unrelated();
        var d2 = Lines(F.Dump());
        var logBefore = F.Log();

        var undo = F.History.Undo(_world.Campaign, b, WriteContext.Default);

        var expected = d2.Except(d1.Except(d0)).Union(d0.Except(d1)).ToHashSet(StringComparer.Ordinal);
        var actual = Lines(F.Dump());
        Assert.True(expected.SetEquals(actual),
            $"undo of {kind}: missing {string.Join(" / ", expected.Except(actual))}; unexpected {string.Join(" / ", actual.Except(expected))}");
        Assert.NotEqual(d0, d1);
        Assert.Equal(logBefore, F.Log().Take(logBefore.Count));
        var added = F.Log().Skip(logBefore.Count).ToList();
        Assert.NotEmpty(added);
        Assert.All(added, r => Assert.Equal((b, "undo", undo.UndoBatchId), (r.UndoOf, r.Action, r.BatchId)));
        Assert.Equal(b, undo.UndoneBatchId);
        Assert.False(undo.WasRedo);
    }

    /// <summary>Redo is an undo of the undo: it brings back exactly the state before the undo (C's work included), for every batch kind.</summary>
    [Theory]
    [InlineData("write")]
    [InlineData("knowledge")]
    [InlineData("supersede")]
    [InlineData("delete")]
    [InlineData("session")]
    public void Undo_TheUndo_IsARedoThatPutsBackTheStateBeforeTheUndo(string kind)
    {
        _world.T1();
        var b = ApplyBatch(kind);
        Unrelated();
        var d2 = F.Dump();
        var undo = F.History.Undo(_world.Campaign, b, WriteContext.Default);

        var redo = F.History.Undo(_world.Campaign, undo.UndoBatchId!, WriteContext.Default);

        Assert.True(redo.WasRedo);
        Assert.Equal(d2, F.Dump());
    }

    /// <summary>
    /// A batch can be undone once: the second attempt is refused as already undone (contract §3.5: "say by which batch"),
    /// with the way to get the change back (undo the undo), and writes nothing. The wording matters, not just the id: the
    /// undo batch touched the same rows, so a conflict refusal would also name it, but would tell the user to undo it
    /// "first" and then try again, which would redo the change and then undo it once more.
    /// </summary>
    [Fact]
    public void Undo_TheSameBatchTwice_IsRefusedAsAlreadyUndoneAndWritesNothing()
    {
        var b = ApplyBatch("write");
        var undo = F.History.Undo(_world.Campaign, b, WriteContext.Default);
        var dump = F.Dump();
        var log = F.Log().Count;

        var ex = Assert.Throws<DndInputException>(() => F.History.Undo(_world.Campaign, b, WriteContext.Default));

        Assert.Contains($"already undone by batch {undo.UndoBatchId}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"undo {undo.UndoBatchId} to redo it", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("first", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((dump, log), (F.Dump(), F.Log().Count));
    }

    /// <summary>
    /// A conflicting undo is refused: T2 wrote the party's row on the re-watched illusion; a later batch re-phrased that
    /// same row. Undoing T2 would silently throw the later edit away, so it is refused, the refusal names the later batch
    /// and says to undo it first, and nothing is written. Undoing the later batch and then T2 works, and the secret's
    /// status follows (partial → seeded).
    /// </summary>
    [Fact]
    public void Undo_ABatchALaterBatchBuiltOn_IsRefusedNamingItThenWorksOnceThatIsUndone()
    {
        _world.T1();
        var t2 = _world.T2().BatchId!;
        var later = F.Knowledge.Record(_world.Campaign, [_world.Facts.IllusionRewatched],
            [new KnowerSpec { Who = "party", State = "knows", KnownAs = "the re-watched vision" }], WriteContext.For(9)).BatchId!;
        var dump = F.Dump();
        var log = F.Log().Count;

        var ex = Assert.Throws<DndInputException>(() => F.History.Undo(_world.Campaign, t2, WriteContext.Default));

        Assert.Contains(later, ex.Message, StringComparison.Ordinal);
        Assert.Contains("first", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((dump, log), (F.Dump(), F.Log().Count));
        Assert.Equal("partial", _world.StoredSecretStatus());

        F.History.Undo(_world.Campaign, later, WriteContext.Default);
        F.History.Undo(_world.Campaign, t2, WriteContext.Default);

        Assert.Equal("seeded", _world.StoredSecretStatus());
        Assert.Equal("seeded", _world.SecretView().DerivedStatus);
    }

    /// <summary>
    /// A later batch that references an id B created (a relation to the NPC B created) is also a conflict: undoing B would
    /// delete the entity out from under it.
    /// </summary>
    [Fact]
    public void Undo_ABatchWhoseCreatedEntityALaterBatchLinksTo_IsRefused()
    {
        var b = ApplyBatch("write");
        var later = F.Apply(_world.Campaign, Op.Link("character:bjorn-mountainfell", "knows", "character:harbourmaster-quell")).BatchId!;
        var dump = F.Dump();

        var ex = Assert.Throws<DndInputException>(() => F.History.Undo(_world.Campaign, b, WriteContext.Default));

        Assert.Contains(later, ex.Message, StringComparison.Ordinal);
        Assert.Equal(dump, F.Dump());
    }

    /// <summary>A dry-run undo reports what it would reverse and keeps nothing: no rows, no log, no batch.</summary>
    [Fact]
    public void Undo_DryRun_ReportsTheReversalAndKeepsNothing()
    {
        var b = ApplyBatch("write");
        var dump = F.Dump();
        var log = F.Log().Count;

        var dry = F.History.Undo(_world.Campaign, b, new WriteContext { DryRun = true });

        Assert.True(dry.DryRun);
        Assert.Null(dry.UndoBatchId);
        Assert.True(dry.RowsReversed > 0);
        Assert.Equal((dump, log), (F.Dump(), F.Log().Count));
    }

    /// <summary>Undo keeps the full-text index right: the author finds the NPC B created, and after the undo finds nothing.</summary>
    [Fact]
    public void Undo_ACreatedEntity_IsGoneFromSearchToo()
    {
        var b = ApplyBatch("write");
        Assert.Contains(_world.Reads.Search(_world.Campaign, "Quell").Entities, e => e.Ref == "character:harbourmaster-quell");

        F.History.Undo(_world.Campaign, b, WriteContext.Default);

        Assert.Empty(_world.Reads.Search(_world.Campaign, "Quell").Entities);
        Assert.Empty(_world.Reads.Search(_world.Campaign, "ledgers").Facts);
    }

    /// <summary>Undo of a soft delete restores the entity to every view that saw it before, the party's search included.</summary>
    [Fact]
    public void Undo_ASoftDelete_BringsTheEntityBackForTheParty()
    {
        var b = ApplyBatch("delete");
        Assert.DoesNotContain(_world.Reads.Search(_world.Campaign, "silk isle", "party").Entities, e => e.Ref == "location:silk-isle");

        F.History.Undo(_world.Campaign, b, WriteContext.Default);

        Assert.Contains(_world.Reads.Search(_world.Campaign, "silk isle", "party").Entities, e => e.Ref == "location:silk-isle");
    }
}
