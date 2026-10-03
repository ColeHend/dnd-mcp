using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: every kind of write the write path makes can be undone end to end, and undo puts every loggable table
/// back exactly as it was (updated_at aside, which undo re-stamps); undo is one new batch, refused for an unknown or
/// already-undone batch and for one that later batches build on, and itself undoable (redo). An undo whose old values
/// would break a rule spanning columns or rows (a clock's filled within its segments, an objective's progress within its
/// max, one live session, one row per code) is refused with a message naming what to do first, never a raw constraint
/// error the host cannot explain. This is the PLAN exit criterion "undo works" exercised through the services rather than
/// raw recorder calls.
/// </summary>
public sealed class HistoryWriterTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _c;

    public HistoryWriterTests()
    {
        _c = _f.Campaign("Belmakor", role: "player", myCharacter: "Belmakor");
        var other = _f.Campaign("One Piece");
        _f.Apply(other, Op.Upsert("character", "Keras"));
        _f.Played(_c, 1, ("character:belmakor", true));
        _f.Sessions.Plan(_c, 2);
        _f.Apply(_c,
            Op.Upsert("character", "Ignis", "pc"),
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "The Old King", Aliases = [new AliasSpec { Alias = "Keras", Visibility = "author" }], Tags = ["void"] },
            Op.Upsert("character", "Lance"),
            new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "Why can't he fetch it?", Code = "Q1" },
            Op.Upsert("quest", "The errand"),
            new CampaignOpSpec { Op = "objective", Ref = "quest:the-errand", Text = "Find it" },
            Op.Upsert("beat", "Statue"), Op.Upsert("beat", "Fight"),
            new CampaignOpSpec { Op = "upsert", Kind = "clock", Name = "Void", Clock = new ClockSpec { Segments = 2 } },
            Op.Upsert("secret", "The thing he wants"),
            Op.Link("character:ignis", "ally_of", "character:belmakor"),
            new CampaignOpSpec { Op = "fact", Statement = "The king sent them.", Visibility = "party", KnownBy = [Op.Knower("party", session: 1)] },
            new CampaignOpSpec { Op = "fact", Statement = "It is the Cage.", About = ["secret:the-thing-he-wants"], Gate = new GateSpec { After = ["f:1"] } },
            new CampaignOpSpec { Op = "delete", Ref = "character:lance" });
    }

    public void Dispose() => _f.Dispose();

    public static TheoryData<string> WriteKinds =>
    [
        "upsert create", "upsert update", "delete entity", "restore entity", "delete fact", "link relation", "link update", "unlink",
        "cross link", "beat edge", "fact create", "fact supersede", "status beat", "objective", "tick to full", "answer",
        "knowledge record", "knowledge reveal secret", "knowledge retract", "session plan", "session start", "session end",
        "session record_past", "campaign update",
    ];

    [Theory]
    [MemberData(nameof(WriteKinds))]
    public void Undo_EveryWriteKind_PutsEveryTableBackExactly(string kind)
    {
        if (kind == "session end")
        {
            _f.Sessions.Start(_c, 2);
        }

        var before = _f.Dump();

        var batch = Write(kind);

        Assert.NotEqual(before, _f.Dump());
        var undo = _f.History.Undo(_c, batch, new WriteContext { Reason = "oops" });
        Assert.Equal(before, _f.Dump());
        Assert.Equal(batch, undo.UndoneBatchId);
        Assert.True(undo.RowsReversed > 0);
        Assert.All(_f.Log(undo.UndoBatchId), r => Assert.Equal((batch, "undo", "oops", "campaign_history/undo"), (r.UndoOf, r.Action, r.Reason, r.Tool)));
    }

    private string Write(string kind)
    {
        WriteResult Ops(params CampaignOpSpec[] ops) => _f.Apply(_c, ops);
        return kind switch
        {
            "upsert create" => Ops(new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Tristan", Subtype = "pc", Aliases = [new AliasSpec { Alias = "Tris" }], Tags = ["fallen", "void"],
                Data = Op.Data("{\"died\": 1}"), KnownBy = [Op.Knower("party", "met")], CanonStatus = "proposed",
            }).BatchId!,
            "upsert update" => Ops(new CampaignOpSpec
            {
                Op = "upsert", Ref = "character:the-old-king", Name = "The Sorcerer King", Summary = "Beaten.", Status = "unknown",
                Aliases = [new AliasSpec { Alias = "the sorcerer king" }, new AliasSpec { Alias = "keras", Visibility = "restricted" }],
                RemoveTags = ["void"], Tags = ["king"], Data = Op.Data("{\"hp\": 0}"), Parent = "quest:the-errand",
            }).BatchId!,
            "delete entity" => Ops(new CampaignOpSpec { Op = "delete", Ref = "character:ignis" }).BatchId!,
            "restore entity" => Ops(new CampaignOpSpec { Op = "restore", Ref = "character:lance" }).BatchId!,
            "delete fact" => Ops(new CampaignOpSpec { Op = "delete", Ref = "f:1" }).BatchId!,
            "link relation" => Ops(Op.Link("character:ignis", "member_of", "faction:the-party")).BatchId!,
            "link update" => Ops(new CampaignOpSpec { Op = "link", From = "character:ignis", Rel = "ally_of", To = "character:belmakor", Attitude = 50, Since = 1, Data = Op.Data("{\"x\": true}") }).BatchId!,
            "unlink" => Ops(new CampaignOpSpec { Op = "unlink", From = "character:ignis", Rel = "ally_of", To = "character:belmakor" }).BatchId!,
            "cross link" => Ops(new CampaignOpSpec { Op = "link", From = "character:the-old-king", Rel = "same_as", To = "one-piece/character:keras", Note = "Cole's PC" }).BatchId!,
            "beat edge" => Ops(new CampaignOpSpec { Op = "link", From = "beat:statue", Rel = "leads_to", To = "beat:fight", Mode = "all_of" }).BatchId!,
            "fact create" => _f.Apply(_c, WriteContext.For(1), new CampaignOpSpec
            {
                Op = "fact", Statement = "Tristan died.", CanonStatus = "played", About = ["character:ignis"],
                Links = [new FactLinkSpec { Ref = "secret:the-thing-he-wants", Role = "clue_for" }], DependsOn = ["f:1"],
                Gate = new GateSpec { With = ["f:2"], ForbiddenTerms = ["drowned"] }, KnownBy = [Op.Knower("party"), Op.Knower("character:ignis", "unaware")],
            }).BatchId!,
            "fact supersede" => Ops(new CampaignOpSpec { Op = "fact", Statement = "The king sent them twice.", Supersedes = "f:1", Code = "F9" }).BatchId!,
            "status beat" => Ops(new CampaignOpSpec { Op = "status", Ref = "beat:statue", Status = "met" }).BatchId!,
            "objective" => Ops(new CampaignOpSpec { Op = "objective", Ref = "quest:the-errand", Objective = 1, Status = "done", Progress = 1, ProgressMax = 1 },
                new CampaignOpSpec { Op = "objective", Ref = "quest:the-errand", Text = "Bring it back" }).BatchId!,
            "tick to full" => Ops(new CampaignOpSpec { Op = "tick", Ref = "clock:void", Amount = 2 }).BatchId!,
            "answer" => Ops(new CampaignOpSpec { Op = "answer", Ref = "Q1", AnswerMd = "The Cage.", AnsweredBy = "character:the-old-king" }).BatchId!,
            "knowledge record" => _f.Knowledge.Record(_c, ["f:2", "character:the-old-king"], [Op.Knower("character:belmakor", "unaware"), Op.Knower("dm")], WriteContext.For(1)).BatchId!,
            "knowledge reveal secret" => _f.Knowledge.Reveal(_c, null, "secret:the-thing-he-wants", null, null, "told", WriteContext.For(1)).BatchId!,
            "knowledge retract" => _f.Knowledge.Retract(_c, ["f:1"], ["party"], WriteContext.Default).BatchId!,
            "session plan" => _f.Sessions.Plan(_c, 3, "Next", prepMd: "Prep.", playedOn: "2026-10").BatchId!,
            "session start" => _f.Sessions.Start(_c, 2, attendance: [new AttendanceSpec { Character = "character:ignis" }]).BatchId!,
            "session end" => _f.Sessions.End(_c, "Recap.", attendance: [new AttendanceSpec { Character = "character:ignis", Present = false }], nextHooks: ["Hook"]).BatchId!,
            "session record_past" => _f.Sessions.RecordPast(_c, 5, "Past", recapMd: "It happened.", attendance: [new AttendanceSpec { Character = "character:belmakor" }]).BatchId!,
            "campaign update" => _f.Store.Update(_c, new CampaignUpdate
            {
                Name = "Belmakor 2", Settings = Op.Data("{\"effective_level_offset\": 1}"), CurrentLocation = null, MyCharacter = "character:ignis",
            }).BatchId!,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    [Fact]
    public void Undo_DryRun_ReportsAndKeepsNothing()
    {
        var batch = _f.Apply(_c, Op.Upsert("character", "Tristan")).BatchId!;
        var before = _f.Dump();

        var undo = _f.History.Undo(_c, batch, new WriteContext { DryRun = true });

        Assert.True(undo.DryRun);
        Assert.Null(undo.UndoBatchId);
        Assert.Equal(["entity character:tristan (create reversed)"], undo.Changes);
        Assert.Equal(before, _f.Dump());
    }

    [Fact]
    public void Undo_Twice_IsRefusedNamingTheUndo_AndRedoWorks()
    {
        var batch = _f.Apply(_c, Op.Upsert("character", "Tristan")).BatchId!;
        var undo = _f.History.Undo(_c, batch, WriteContext.Default);

        var ex = Assert.Throws<DndInputException>(() => _f.History.Undo(_c, batch, WriteContext.Default));
        var redo = _f.History.Undo(_c, undo.UndoBatchId!, WriteContext.Default);

        Assert.Contains($"Batch {batch} was already undone by batch {undo.UndoBatchId}", ex.Message);
        Assert.True(redo.WasRedo);
        Assert.Equal("Tristan", _f.Entity(_c, "character:tristan").Name);
    }

    [Fact]
    public void Undo_WithALaterChangeToTheSameField_IsRefusedAndChangesNothing()
    {
        var batch = _f.Apply(_c, new CampaignOpSpec { Op = "upsert", Ref = "character:ignis", Summary = "One." }).BatchId!;
        var later = _f.Apply(_c, new CampaignOpSpec { Op = "upsert", Ref = "character:ignis", Summary = "Two." }).BatchId!;
        var before = _f.Dump();

        var ex = Assert.Throws<DndInputException>(() => _f.History.Undo(_c, batch, WriteContext.Default));

        Assert.Contains($"- {later}", ex.Message);
        Assert.Equal(before, _f.Dump());
    }

    [Fact]
    public void Undo_ByPrefix_Works()
    {
        var batch = _f.Apply(_c, Op.Upsert("character", "Tristan")).BatchId!;

        var undo = _f.History.Undo(_c, batch[..13], WriteContext.Default);

        Assert.Equal(batch, undo.UndoneBatchId);
    }

    [Fact]
    public void Undo_UnknownBatch_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.History.Undo(_c, "0199aaaa-0000-7000-8000-000000000000", WriteContext.Default));

        Assert.Contains("No batch 0199aaaa-0000-7000-8000-000000000000 in this campaign", ex.Message);
    }

    [Fact]
    public void Undo_InALiveSession_IsFiledUnderIt()
    {
        var batch = _f.Apply(_c, Op.Upsert("character", "Tristan")).BatchId!;
        _f.Sessions.Start(_c, 2);

        var undo = _f.History.Undo(_c, batch, WriteContext.Default);

        Assert.All(_f.Log(undo.UndoBatchId), r => Assert.Equal(_f.Entity(_c, "session:2").Id, r.SessionId));
    }

    public static TheoryData<string> RuleBreakingUndos =>
    [
        "end while a later session is live", "clock segments", "clock segments changed twice in the batch", "objective progress_max",
        "redo of a code handed out since",
    ];

    [Theory]
    [MemberData(nameof(RuleBreakingUndos))]
    public void Undo_ThatWouldBreakARuleTheDataKeeps_IsRefusedWithWhatToDoFirst(string kind)
    {
        var (batch, expected, later) = RuleBreaking(kind);
        var before = _f.Dump();
        var logBefore = _f.Log().Count;

        var ex = Assert.Throws<DndInputException>(() => _f.History.Undo(_c, batch, WriteContext.Default));

        Assert.StartsWith($"Batch {batch} cannot be undone: ", ex.Message);
        Assert.Contains(expected, ex.Message);
        Assert.Contains($"- {later}", ex.Message);
        Assert.EndsWith("Nothing was changed.", ex.Message);
        Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(ex.InnerException);
        Assert.DoesNotContain("clock:void", ex.Message);
        Assert.DoesNotContain("quest:the-errand", ex.Message);
        Assert.DoesNotContain("character:", ex.Message);
        Assert.DoesNotContain("item:", ex.Message);
        Assert.Equal(before, _f.Dump());
        Assert.Equal(logBefore, _f.Log().Count);
    }

    // The batch to undo, what the refusal must say, and the later batch it must name.
    private (string Batch, string Expected, string Later) RuleBreaking(string kind)
    {
        switch (kind)
        {
            case "end while a later session is live":
            {
                _f.Sessions.Start(_c, 2);
                var end = _f.Sessions.End(_c, "Recap.").BatchId!;
                var start = _f.Sessions.Start(_c, 3).BatchId!;
                return (end, "it makes session 2 live again, and session 3 is live now (one live session at a time). End session 3 first", start);
            }

            case "clock segments":
            {
                var grow = _f.Apply(_c, new CampaignOpSpec { Op = "upsert", Ref = "clock:void", Clock = new ClockSpec { Segments = 8 } }).BatchId!;
                var tick = _f.Apply(_c, new CampaignOpSpec { Op = "tick", Ref = "clock:void", Amount = 6 }).BatchId!;
                var clock = _f.Entity(_c, "clock:void").SeqHandle;
                return (grow, $"it changed clock {clock}'s segments from 2 to 8, and the clock has 6 filled now; 2 segments cannot hold 6.", tick);
            }

            case "clock segments changed twice in the batch":
            {
                // Undo writes old values newest first, so segments ends at the FIRST change's old value (2), not 4.
                var grow = _f.Apply(_c,
                    new CampaignOpSpec { Op = "upsert", Ref = "clock:void", Clock = new ClockSpec { Segments = 4 } },
                    new CampaignOpSpec { Op = "upsert", Ref = "clock:void", Clock = new ClockSpec { Segments = 8 } }).BatchId!;
                var tick = _f.Apply(_c, new CampaignOpSpec { Op = "tick", Ref = "clock:void", Amount = 3 }).BatchId!;
                var clock = _f.Entity(_c, "clock:void").SeqHandle;
                return (grow, $"it changed clock {clock}'s segments from 2 to 8, and the clock has 3 filled now; 2 segments cannot hold 3.", tick);
            }

            case "redo of a code handed out since":
            {
                // Undoing the auto_code frees C1; a later auto_code takes it; redoing the first would give C1 twice.
                var assign = _f.Apply(_c, new CampaignOpSpec { Op = "upsert", Ref = "character:ignis", AutoCode = "C" }).BatchId!;
                var undo = _f.History.Undo(_c, assign, WriteContext.Default).UndoBatchId!;
                var reuse = _f.Apply(_c, new CampaignOpSpec { Op = "upsert", Kind = "item", Name = "Cage key", AutoCode = "C" }).BatchId!;
                var ignis = _f.Entity(_c, "character:ignis").SeqHandle;
                var key = _f.Entity(_c, "item:cage-key").SeqHandle;
                return (undo, $"it gives {ignis} its code C1 back, and {key} holds C1 now", reuse);
            }

            default:
            {
                _f.Apply(_c, new CampaignOpSpec { Op = "objective", Ref = "quest:the-errand", Objective = 1, Progress = 1, ProgressMax = 2 });
                var grow = _f.Apply(_c, new CampaignOpSpec { Op = "objective", Ref = "quest:the-errand", Objective = 1, ProgressMax = 5 }).BatchId!;
                var progress = _f.Apply(_c, new CampaignOpSpec { Op = "objective", Ref = "quest:the-errand", Objective = 1, Progress = 4 }).BatchId!;
                var quest = _f.Entity(_c, "quest:the-errand").SeqHandle;
                return (grow, $"it would put objective 1 of {quest} back to progress 4 of at most 2", progress);
            }
        }
    }

    [Fact]
    public void Undo_EndRefusedWhileALaterSessionWasLive_WorksOnceThatOneHasEnded()
    {
        _f.Sessions.Start(_c, 2);
        var end = _f.Sessions.End(_c, "Recap.").BatchId!;
        _f.Sessions.Start(_c, 3);
        Assert.Throws<DndInputException>(() => _f.History.Undo(_c, end, WriteContext.Default));
        _f.Sessions.End(_c, "Session 3.");

        _f.History.Undo(_c, end, WriteContext.Default);

        Assert.Equal("live", _f.Scalar<string>("SELECT status FROM session WHERE number = 2 AND campaign_id = @id", new { id = _c.Id }));
    }
}
