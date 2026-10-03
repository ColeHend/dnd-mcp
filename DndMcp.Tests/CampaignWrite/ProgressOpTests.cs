using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: the progress ops (status, objective, tick, answer) and delete / restore do exactly their one thing with
/// the outcome word the host prints, check the resolved kind (a tick of a non-clock is refused even through
/// <c>e:&lt;n&gt;</c>), and report what followed (beats now reachable, a clock filled). A clock that silently overflowed
/// or a delete that reported "deleted" for something already gone would mislead the author about the table's state.
/// </summary>
public sealed class ProgressOpTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _dm;

    public ProgressOpTests()
    {
        _dm = _f.Campaign("One Piece");
    }

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Status_Beat_ReportsTheBeatsThatBecameReachable()
    {
        _f.Apply(_dm, Op.Upsert("beat", "Dock fight"), Op.Upsert("beat", "Harbourmaster talks"), Op.Upsert("beat", "Blood moon"),
            new CampaignOpSpec { Op = "link", From = "beat:dock-fight", Rel = "leads_to", To = "beat:blood-moon", Mode = "all_of" },
            new CampaignOpSpec { Op = "link", From = "beat:harbourmaster-talks", Rel = "leads_to", To = "beat:blood-moon", Mode = "all_of" });

        var first = _f.Apply(_dm, new CampaignOpSpec { Op = "status", Ref = "beat:dock-fight", Status = "met" });
        var second = _f.Apply(_dm, new CampaignOpSpec { Op = "status", Ref = "beat:harbourmaster-talks", Status = "met" });

        Assert.Empty(first.Consequences);
        var consequence = Assert.Single(second.Consequences);
        Assert.Equal((ConsequenceKinds.BeatsReachable, "beat:harbourmaster-talks"), (consequence.Kind, consequence.Ref));
        Assert.Equal(["beat:blood-moon"], consequence.Refs);
        Assert.Equal(WriteOutcomes.Updated, Assert.Single(second.Applied).Outcome);
    }

    [Theory]
    [InlineData("quest", "Resolved", "resolved")]
    [InlineData("thread", "blocked", "blocked")]
    [InlineData("character", "Missing", "missing")]
    [InlineData("work", "performed", "performed")]
    public void Status_SetsTheKindsCanonicalStatus(string kind, string status, string stored)
    {
        _f.Apply(_dm, Op.Upsert(kind, "Thing"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "status", Ref = $"{kind}:thing", Status = status });

        Assert.Equal(stored, _f.Entity(_dm, $"{kind}:thing").Status);
        Assert.Equal(["status"], Assert.Single(result.Applied).ChangedFields);
    }

    [Fact]
    public void Status_ThatDoesNotFitTheResolvedKind_IsRefused()
    {
        _f.Apply(_dm, Op.Upsert("location", "Serret"));
        var seq = _f.Entity(_dm, "location:serret").SeqHandle;

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "status", Ref = seq, Status = "dead" }));

        Assert.Contains($"ops item 1 (status {seq}): status \"dead\" is not a location status", ex.Message);
    }

    [Fact]
    public void Status_QuestionAnswered_AdvisesTheAnswerOp()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "What destroyed Silk Isle?", Code = "Q7" });

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "status", Ref = "Q7", Status = "answered" });

        var warning = Assert.Single(result.Warnings);
        Assert.Equal((WarningKinds.UseAnswer, WarningSeverities.Advisory), (warning.Kind, warning.Severity));
    }

    [Fact]
    public void Objective_AddThenUpdateByPosition()
    {
        _f.Apply(_dm, Op.Upsert("quest", "The War God's axe", visibility: "party"));

        var added = _f.Apply(_dm,
            new CampaignOpSpec { Op = "objective", Ref = "quest:the-war-gods-axe", Text = "Assemble the War God's axe", Progress = 2 },
            new CampaignOpSpec { Op = "objective", Ref = "quest:the-war-gods-axe", Text = "Keep it hidden", Visibility = "author" });
        var updated = _f.Apply(_dm, new CampaignOpSpec { Op = "objective", Ref = "quest:the-war-gods-axe", Objective = 1, Progress = 3, ProgressMax = 5 });

        Assert.Equal(["objective 1", "objective 2"], added.Applied.Select(a => a.ChangedFields.Single()));
        Assert.All(added.Applied, a => Assert.Equal(WriteOutcomes.Created, a.Outcome));
        Assert.Equal(["objective 1 progress", "objective 1 progress_max"], Assert.Single(updated.Applied).ChangedFields);
        var objectives = _f.Query<ObjectiveRow>($"SELECT {ObjectiveRow.Columns} FROM objective ORDER BY ordinal");
        Assert.Equal([("Assemble the War God's axe", 3L, 5L, "party"), ("Keep it hidden", (long?)null, (long?)null, "author")],
            objectives.Select(o => (o.Text, o.Progress, o.ProgressMax, o.Visibility)));
    }

    [Fact]
    public void Objective_DoneInASession_RecordsTheResolvedSession()
    {
        _f.Apply(_dm, Op.Upsert("quest", "Rescue"), new CampaignOpSpec { Op = "objective", Ref = "quest:rescue", Text = "Find the arch mage" });
        _f.Played(_dm, 4);

        _f.Apply(_dm, WriteContext.For(4), new CampaignOpSpec { Op = "objective", Ref = "quest:rescue", Objective = 1, Status = "done" });

        Assert.Equal(_f.Entity(_dm, "session:4").Id, _f.Scalar<string>("SELECT resolved_session_id FROM objective"));
    }

    [Theory]
    [InlineData(3, null, "no objective 3")]
    [InlineData(1, 9, "progress 9 would be more than progress_max 5")]
    public void Objective_OutOfRangeOrOverMax_IsRefused(int index, int? progress, string expected)
    {
        _f.Apply(_dm, Op.Upsert("quest", "Rescue"),
            new CampaignOpSpec { Op = "objective", Ref = "quest:rescue", Text = "One", ProgressMax = 5 },
            new CampaignOpSpec { Op = "objective", Ref = "quest:rescue", Text = "Two" });

        var ex = Assert.Throws<DndInputException>(() =>
            _f.Apply(_dm, new CampaignOpSpec { Op = "objective", Ref = "quest:rescue", Objective = index, Progress = progress, Text = progress is null ? "x" : null }));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Objective_OnANonQuestBySeqHandle_IsRefused()
    {
        _f.Apply(_dm, Op.Upsert("location", "Serret"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "objective", Ref = "e:2", Text = "x" }));

        Assert.Contains("objectives belong to a quest or a thread, not to kind location", ex.Message);
    }

    private void Clock(int segments, int filled = 0) =>
        _f.Apply(_dm, new CampaignOpSpec
        {
            Op = "upsert", Kind = "clock", Name = "Blood moon", Clock = new ClockSpec { Segments = segments, Filled = filled, OnFillMd = "The moon rises." },
        });

    [Fact]
    public void Tick_DefaultOne_AddsASegment()
    {
        Clock(6);

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "tick", Ref = "clock:blood-moon" });

        Assert.Equal(1, _f.Scalar<long>("SELECT filled FROM clock"));
        var applied = Assert.Single(result.Applied);
        Assert.Equal(WriteOutcomes.Ticked, applied.Outcome);
        Assert.Equal(["clock.filled"], applied.ChangedFields);
    }

    [Fact]
    public void Tick_ToTheLastSegment_IsDoneWithTheOnFillConsequence()
    {
        Clock(6, 4);

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "tick", Ref = "clock:blood-moon", Amount = 2 });

        Assert.Equal("done", _f.Entity(_dm, "clock:blood-moon").Status);
        var consequence = Assert.Single(result.Consequences);
        Assert.Equal(ConsequenceKinds.ClockFilled, consequence.Kind);
        Assert.Equal("Clock Blood moon (clock:blood-moon) filled: The moon rises.", consequence.Message);
        Assert.Equal(["clock.filled", "status"], Assert.Single(result.Applied).ChangedFields);
    }

    [Theory]
    [InlineData(4, 5, 6, "is past its end, so filled is 6")]
    [InlineData(1, -3, 0, "is past its start, so filled is 0")]
    public void Tick_PastEitherEnd_IsClampedWithAWarning(int filled, int amount, long expected, string message)
    {
        Clock(6, filled);

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "tick", Ref = "clock:blood-moon", Amount = amount });

        Assert.Equal(expected, _f.Scalar<long>("SELECT filled FROM clock"));
        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.ClockClamped && w.Message.Contains(message));
    }

    [Fact]
    public void Tick_BackBelowFull_RunsAgain()
    {
        Clock(3, 2);
        _f.Apply(_dm, new CampaignOpSpec { Op = "tick", Ref = "clock:blood-moon" });

        _f.Apply(_dm, new CampaignOpSpec { Op = "tick", Ref = "clock:blood-moon", Amount = -1 });

        Assert.Equal("running", _f.Entity(_dm, "clock:blood-moon").Status);
    }

    [Fact]
    public void Tick_FullClockTickedAgain_IsUnchanged()
    {
        Clock(3, 3);

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "tick", Ref = "clock:blood-moon" });

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(result.Applied).Outcome);
    }

    [Fact]
    public void Tick_NotAClockBySeqHandle_IsRefused()
    {
        _f.Apply(_dm, Op.Upsert("location", "Serret"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "tick", Ref = "e:2" }));

        Assert.Contains("only a clock is ticked, not kind location", ex.Message);
    }

    [Fact]
    public void Answer_WithAnEntity_LinksAnsweredByAndStoresTheAnswer()
    {
        _f.Apply(_dm, new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "What destroyed Silk Isle?", Code = "Q7", Status = "withheld" },
            Op.Upsert("faction", "GODS Co", "company"));

        var result = _f.Apply(_dm, new CampaignOpSpec { Op = "answer", Ref = "Q7", AnswerMd = "Its **founders**.", AnsweredBy = "faction:gods-co" });

        var question = _f.Entity(_dm, "Q7");
        Assert.Equal("answered", question.Status);
        Assert.Equal("{\"answer_md\":\"Its **founders**.\"}", question.Data);
        Assert.Equal(1, _f.Count("SELECT count(*) FROM relation WHERE rel = 'answered_by' AND from_id = @q", new { q = question.Id }));
        var applied = Assert.Single(result.Applied);
        Assert.Equal((WriteOutcomes.Answered, "Q7"), (applied.Outcome, applied.Code));
        Assert.Equal(["status", "data.answer_md", "answered_by"], applied.ChangedFields);
    }

    [Fact]
    public void Answer_WithAFact_StoresItsHandleInData()
    {
        _f.Apply(_dm, Op.Upsert("question", "How long ago?"), Op.Fact("About a thousand years."));

        _f.Apply(_dm, new CampaignOpSpec { Op = "answer", Ref = "question:how-long-ago", AnsweredBy = "f:1" });

        Assert.Equal("{\"answered_by\":\"f:1\"}", _f.Entity(_dm, "question:how-long-ago").Data);
        Assert.Equal(0, _f.Count("SELECT count(*) FROM relation WHERE rel = 'answered_by'"));
    }

    [Fact]
    public void Answer_NotAQuestion_IsRefused()
    {
        _f.Apply(_dm, Op.Upsert("character", "Nadar"));

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "answer", Ref = "e:2", AnswerMd = "x" }));

        Assert.Contains("only a question is answered, not kind character", ex.Message);
    }

    [Fact]
    public void DeleteThenRestore_EntityKeepsItsHandleAndSlug()
    {
        _f.Apply(_dm, Op.Upsert("character", "Lance"));

        var deleted = _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "character:lance" });
        var hidden = _f.TryEntity(_dm, "character:lance");
        var restored = _f.Apply(_dm, new CampaignOpSpec { Op = "restore", Ref = "character:lance" });

        Assert.Equal(WriteOutcomes.Deleted, Assert.Single(deleted.Applied).Outcome);
        Assert.Null(hidden);
        Assert.Equal(WriteOutcomes.Restored, Assert.Single(restored.Applied).Outcome);
        Assert.Equal("e:2", _f.Entity(_dm, "character:lance").SeqHandle);
        var rows = _f.Log(deleted.BatchId).Concat(_f.Log(restored.BatchId)).ToList();
        Assert.Equal([("delete", "deleted_at"), ("restore", "deleted_at")], rows.Select(r => (r.Action, r.FieldPath!)));
    }

    [Fact]
    public void DeleteTwice_SecondIsUnchangedWithAWarning()
    {
        _f.Apply(_dm, Op.Fact("A fact."));
        _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "f:1" });

        var again = _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "f:1" });
        var restoreLive = _f.Apply(_dm, new CampaignOpSpec { Op = "restore", Ref = "f:1" });
        var restoreAgain = _f.Apply(_dm, new CampaignOpSpec { Op = "restore", Ref = "f:1" });

        Assert.Equal(WriteOutcomes.Unchanged, Assert.Single(again.Applied).Outcome);
        Assert.Contains(again.Warnings, w => w.Kind == WarningKinds.AlreadyThere && w.Message.Contains("f:1 was already deleted"));
        Assert.Equal(WriteOutcomes.Restored, Assert.Single(restoreLive.Applied).Outcome);
        Assert.Contains(restoreAgain.Warnings, w => w.Message.Contains("f:1 is not deleted"));
        Assert.Empty(_f.Log(again.BatchId));
    }

    [Fact]
    public void Delete_ThePartyFaction_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "faction:the-party" }));

        Assert.Contains("is the campaign's party faction", ex.Message);
    }

    [Fact]
    public void Delete_ASession_IsRefused()
    {
        _f.Played(_dm, 1);

        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "session:1" }));

        Assert.Contains("a session cannot be deleted", ex.Message);
    }

    [Fact]
    public void Delete_Unknown_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Apply(_dm, new CampaignOpSpec { Op = "delete", Ref = "character:nobody" }));

        Assert.Contains("no entity or fact character:nobody in this campaign", ex.Message);
    }

    [Theory]
    [InlineData("public", "public")]
    [InlineData("party", "party")]
    [InlineData("restricted", "author")]
    [InlineData("author", "author")]
    public void Objective_AddWithNoVisibility_FollowsAPublicOrPartyQuestElseIsAuthorOnly(string questVisibility, string expected)
    {
        _f.Apply(_dm, Op.Upsert("quest", "The errand", visibility: questVisibility),
            new CampaignOpSpec { Op = "objective", Ref = "quest:the-errand", Text = "Find it" });

        Assert.Equal(expected, _f.Scalar<string>("SELECT visibility FROM objective"));
    }
}
