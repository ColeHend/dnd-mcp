using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: sessions follow §3.6: numbered explicitly, else plan takes the next after the highest and start /
/// record_past the next to play; at most one live per campaign (refused with a message, backed by the index); the live
/// session is every write's default context, while start and record_past are filed under the session they write (so
/// point-in-time replay sees a session start and get played when it did); the live log is appended without change_log
/// rows; end marks the session played with its recap and returns the checklist (and a session-end backup after the
/// commit); record_past writes a played session in one batch; a DM campaign's planned session stays hidden from the party
/// until it is played, and start or record_past that keep its planned title say that players now see it (start, end and
/// record_past take a title); attendance given to end or record_past replaces the session's list, naming who was added
/// and removed; start's default date is the table's local date.
/// </summary>
public sealed class SessionWriterTests : IDisposable
{
    private readonly WriteFixture _f = new();
    private readonly CampaignRow _c;

    public SessionWriterTests()
    {
        _c = _f.Campaign("Belmakor", role: "player", myCharacter: "Belmakor");
        _f.Apply(_c, Op.Upsert("character", "Serif", "pc"));
    }

    public void Dispose() => _f.Dispose();

    private SessionRow Session(int number) =>
        Assert.Single(_f.Query<SessionRow>($"SELECT {SessionRow.Columns} FROM session WHERE number = @number AND campaign_id = @id", new { number, id = _c.Id }));

    private static AttendanceSpec? At(string character, bool present = true) => new() { Character = character, Present = present };

    private IReadOnlyList<(string Character, long Present)> Attendance(int number) =>
        _f.Query<(string, long)>("SELECT e.slug, a.present FROM session_attendance a JOIN entity e ON e.id = a.character_id " +
                                 "WHERE a.session_id = @id ORDER BY e.seq", new { id = _f.Entity(_c, "session:" + number).Id });

    // ---- titles (review L01) ------------------------------------------------------------------------------------------

    /// <summary>
    /// A planned session's title is prep, hidden from every player view until the session is live or played. Starting or
    /// recording it with that title shows it to the players, so the result says so (in a DM campaign and a player one).
    /// </summary>
    [Theory]
    [InlineData("player", "start")]
    [InlineData("dm", "start")]
    [InlineData("player", "record_past")]
    [InlineData("dm", "record_past")]
    public void StartOrRecordPast_APlannedSessionKeepingItsPlannedTitle_WarnsThatPlayersNowSeeIt(string role, string action)
    {
        var campaign = role == "player" ? _c : _f.Campaign("Veil");
        _f.Sessions.Plan(campaign, 4, "The ambush at the lighthouse", prepMd: "She unmasks.");

        var result = action == "start" ? _f.Sessions.Start(campaign, 4) : _f.Sessions.RecordPast(campaign, 4, recapMd: "It happened.");

        var warning = Assert.Single(result.Warnings, w => w.Kind == WarningKinds.PlannedTitle);
        Assert.Equal(WarningSeverities.Warning, warning.Severity);
        Assert.Equal($"session:4's planned title \"The ambush at the lighthouse\" is now visible to players; pass title to {(action == "start" ? "end" : "record_past")} to change it.",
            warning.Message);
        Assert.Equal(("The ambush at the lighthouse", "party"), (_f.Entity(campaign, "session:4").Name, _f.Entity(campaign, "session:4").Visibility));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("record_past")]
    public void StartOrRecordPast_APlannedSessionWithATitle_TakesItWithoutAWarning(string action)
    {
        _f.Sessions.Plan(_c, 4, "The ambush at the lighthouse");

        var result = action == "start"
            ? _f.Sessions.Start(_c, 4, title: "Night of the lighthouse")
            : _f.Sessions.RecordPast(_c, 4, "Night of the lighthouse");

        Assert.DoesNotContain(result.Warnings, w => w.Kind == WarningKinds.PlannedTitle);
        Assert.Contains("name", result.ChangedFields);
        Assert.Equal("Night of the lighthouse", _f.Entity(_c, "session:4").Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Start_APlannedSessionWithTheDefaultTitleOrANewSession_DoesNotWarn(bool planned)
    {
        if (planned)
        {
            _f.Sessions.Plan(_c, 4);
        }

        var result = _f.Sessions.Start(_c, 4);

        Assert.DoesNotContain(result.Warnings, w => w.Kind == WarningKinds.PlannedTitle);
        Assert.Equal("Session 4", _f.Entity(_c, "session:4").Name);
    }

    [Fact]
    public void Start_NewSessionWithATitle_IsNamedSo()
    {
        var result = _f.Sessions.Start(_c, 4, title: "  The Sky Fair ");

        Assert.Equal((WriteOutcomes.Created, "The Sky Fair"), (result.Outcome, _f.Entity(_c, "session:4").Name));
    }

    [Fact]
    public void End_WithATitle_RenamesTheLiveSession()
    {
        _f.Sessions.Plan(_c, 4, "The ambush at the lighthouse");
        _f.Sessions.Start(_c, 4);

        var result = _f.Sessions.End(_c, "Recap.", title: "  Night of the lighthouse ");

        Assert.Contains("name", result.ChangedFields);
        Assert.Equal("Night of the lighthouse", _f.Entity(_c, "session:4").Name);
    }

    /// <summary>
    /// The warning is for a title players have not seen: correcting a played session's recap keeps the title they already
    /// read, with nothing to say.
    /// </summary>
    [Fact]
    public void RecordPast_CorrectingAPlayedSessionWithATitle_DoesNotWarnAboutIt()
    {
        _f.Sessions.RecordPast(_c, 1, "The well", recapMd: "One.");

        var result = _f.Sessions.RecordPast(_c, 1, recapMd: "One, corrected.");

        Assert.DoesNotContain(result.Warnings, w => w.Kind == WarningKinds.PlannedTitle);
        Assert.Equal("The well", _f.Entity(_c, "session:1").Name);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    public void StartOrEnd_ABlankTitle_IsRefused(string action)
    {
        if (action == "end")
        {
            _f.Sessions.Start(_c, 1);
        }

        var ex = Assert.Throws<DndInputException>(() => action == "start" ? _f.Sessions.Start(_c, 1, title: " ") : _f.Sessions.End(_c, "Recap.", title: " "));

        Assert.Contains("title is blank; give text or leave it out.", ex.Message);
    }

    // ---- attendance (review C14) --------------------------------------------------------------------------------------

    /// <summary>
    /// record_past corrects a played session, attendance included: the list given replaces the recorded one (a character
    /// wrongly listed is removed, as a logged delete that undo puts back), and the result names who changed.
    /// </summary>
    [Fact]
    public void RecordPast_CorrectionWithAttendance_ReplacesTheListAndSaysWhoWasRemoved()
    {
        _f.Sessions.RecordPast(_c, 1, recapMd: "One.", attendance: [At("character:belmakor"), At("character:serif")]);
        var before = _f.Dump();

        var result = _f.Sessions.RecordPast(_c, 1, recapMd: "One.", attendance: [At("character:belmakor", present: false)]);

        Assert.Equal(WriteOutcomes.Updated, result.Outcome);
        Assert.Equal(["attendance (changed character:belmakor; removed character:serif)"], result.ChangedFields);
        Assert.Empty(result.AttendanceAdded!);
        Assert.Equal(["character:serif"], result.AttendanceRemoved);
        Assert.Equal([("belmakor", 0L)], Attendance(1));
        Assert.Contains(_f.Log(result.BatchId), r => r is { TargetTable: "session_attendance", Op: "delete", Action: "record_past" });
        _f.History.Undo(_c, result.BatchId!, WriteContext.Default);
        Assert.Equal(before, _f.Dump());
    }

    [Fact]
    public void End_WithAttendance_ReplacesWhatStartRecorded()
    {
        _f.Sessions.Start(_c, 1, attendance: [At("character:belmakor"), At("character:serif")]);

        var result = _f.Sessions.End(_c, "Recap.", attendance: [At("character:serif")]);

        Assert.Contains("attendance (removed character:belmakor)", result.ChangedFields);
        Assert.Equal(["character:belmakor"], result.AttendanceRemoved);
        Assert.Equal([("serif", 1L)], Attendance(1));
    }

    [Fact]
    public void StartAndRecordPast_NewAttendance_AreReportedAsAdded()
    {
        var start = _f.Sessions.Start(_c, 1, attendance: [At("character:belmakor"), At("character:serif")]);
        _f.Sessions.End(_c, "Recap.");

        var past = _f.Sessions.RecordPast(_c, 2, recapMd: "Two.", attendance: [At("character:serif")]);

        Assert.Contains("attendance (added character:belmakor, character:serif)", start.ChangedFields);
        Assert.Equal(["character:belmakor", "character:serif"], start.AttendanceAdded);
        Assert.Equal(["session", "attendance (added character:serif)"], past.ChangedFields);
        Assert.Empty(past.AttendanceRemoved!);
    }

    [Fact]
    public void RecordPast_CorrectionWithoutAttendance_KeepsTheRecordedList()
    {
        _f.Sessions.RecordPast(_c, 1, recapMd: "One.", attendance: [At("character:belmakor"), At("character:serif")]);

        var result = _f.Sessions.RecordPast(_c, 1, recapMd: "One, corrected.");

        Assert.Equal(["body_md"], result.ChangedFields);
        Assert.Equal([("belmakor", 1L), ("serif", 1L)], Attendance(1));
    }

    // ---- played on (review U13) ---------------------------------------------------------------------------------------

    /// <summary>
    /// start's default played_on is the table's date, the clock's local date: 22:00 on 30 September six hours west of UTC
    /// is 04:00 UTC on 1 October, and the session was played on the 30th. Timestamps stay UTC.
    /// </summary>
    [Fact]
    public void Start_NoPlayedOn_IsTheLocalDateOfTheClock()
    {
        using var directory = new CampaignTestDb(create: false);
        using var database = new CampaignDatabase(directory.DatabasePath, new EveningWestOfUtc(), directory.Logger);
        database.EnsureReady();
        var campaign = new CampaignStore(database).Create("Sky", "player", "2014").Campaign;

        new SessionWriter(database).Start(campaign, 1);

        using var connection = database.OpenRead();
        var session = Dapper.SqlMapper.QuerySingle<SessionRow>(connection, $"SELECT {SessionRow.Columns} FROM session");
        Assert.Equal(("2026-09-30", "day", "2026-10-01T04:00:00.000Z"), (session.PlayedOn, session.PlayedOnPrecision, session.StartedAt));
    }

    /// <summary>2026-09-30 22:00 in a zone six hours west of UTC: 2026-10-01 04:00 UTC.</summary>
    private sealed class EveningWestOfUtc : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("UTC-06", TimeSpan.FromHours(-6), "UTC-06", "UTC-06");

        public override DateTimeOffset GetUtcNow() => new(2026, 10, 1, 4, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void Plan_New_IsAPlannedSessionEntityAndRow()
    {
        var result = _f.Sessions.Plan(_c, 12, "The kraken", prepMd: null, playedOn: "2026-10-02");

        Assert.Equal(("session:12", 12, "planned", WriteOutcomes.Created), (result.Session, result.Number, result.Status, result.Outcome));
        var entity = _f.Entity(_c, "session:12");
        Assert.Equal(("session-12", "The kraken", "session"), (entity.Slug, entity.Name, entity.Kind));
        Assert.Equal(("2026-10-02", "day"), (Session(12).PlayedOn, Session(12).PlayedOnPrecision));
    }

    [Fact]
    public void Plan_WithPrep_IsPrepped_AndPlanningAgainUpdates()
    {
        _f.Sessions.Plan(_c, 3);

        var result = _f.Sessions.Plan(_c, 3, "Statue", prepMd: "Run-sheet.");

        Assert.Equal(("prepped", WriteOutcomes.Updated), (result.Status, result.Outcome));
        Assert.Equal("Statue", _f.Entity(_c, "session:3").Name);
        Assert.Equal(["prep_md", "status", "name"], result.ChangedFields);
    }

    [Fact]
    public void Plan_NoNumber_IsTheNextAfterTheHighest()
    {
        _f.Played(_c, 0);
        _f.Played(_c, 4);

        Assert.Equal(5, _f.Sessions.Plan(_c).Number);
    }

    [Fact]
    public void Plan_APlayedSession_IsRefused()
    {
        _f.Played(_c, 1);

        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.Plan(_c, 1, "x"));

        Assert.Contains("session 1 is played", ex.Message);
    }

    [Fact]
    public void Start_NoNumber_StartsTheNextPlannedAfterTheLastPlayed()
    {
        _f.Played(_c, 1);
        _f.Sessions.Plan(_c, 2);
        _f.Sessions.Plan(_c, 3);

        var result = _f.Sessions.Start(_c, attendance: [new AttendanceSpec { Character = "character:serif" }], ingame: "day 12");

        Assert.Equal((2, "live", WriteOutcomes.Updated), (result.Number, result.Status, result.Outcome));
        var session = Session(2);
        Assert.Equal((ManualNow(), "2026-09-01", "day", "day 12"), (session.StartedAt, session.PlayedOn, session.PlayedOnPrecision, session.IngameStart));
        Assert.Equal(1, _f.Count("SELECT count(*) FROM session_attendance WHERE present = 1"));
        Assert.Equal("party", _f.Entity(_c, "session:2").Visibility);
    }

    private string ManualNow() => _f.Db.Database.Now();

    [Fact]
    public void Start_NothingPlanned_CreatesTheNextNumber()
    {
        _f.Played(_c, 7);

        var result = _f.Sessions.Start(_c);

        Assert.Equal((8, WriteOutcomes.Created), (result.Number, result.Outcome));
    }

    [Fact]
    public void Start_WhileAnotherIsLive_IsRefused()
    {
        _f.Sessions.Start(_c, 1);

        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.Start(_c, 2));

        Assert.Contains("session 1 is live; end it", ex.Message);
        Assert.Equal(1, _f.Count("SELECT count(*) FROM session WHERE status = 'live'"));
    }

    [Fact]
    public void OneLiveSession_IsAlsoEnforcedByTheSchema()
    {
        _f.Sessions.Start(_c, 1);
        _f.Sessions.Plan(_c, 2);

        using var connection = _f.Open();
        var ex = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            Dapper.SqlMapper.Execute(connection, "UPDATE session SET status = 'live' WHERE number = 2"));

        Assert.Equal(19, ex.SqliteErrorCode);
    }

    [Fact]
    public void Start_APlayedSession_IsRefused()
    {
        _f.Played(_c, 1);

        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.Start(_c, 1));

        Assert.Contains("session 1 was played", ex.Message);
    }

    [Fact]
    public void LiveSession_IsTheDefaultContextOfWrites()
    {
        _f.Sessions.Start(_c, 4);

        var result = _f.Apply(_c, new CampaignOpSpec { Op = "fact", Statement = "The kraken fled.", CanonStatus = "played", KnownBy = [Op.Knower("party")] });

        Assert.Equal(4, result.SessionNumber);
        var session = _f.Entity(_c, "session:4").Id;
        Assert.Equal(session, _f.Fact(_c, "f:1").EstablishedSessionId);
        Assert.Equal(session, _f.Scalar<string>("SELECT learned_session_id FROM knowledge"));
        Assert.All(_f.Log(result.BatchId), r => Assert.Equal(session, r.SessionId));
    }

    [Fact]
    public void Log_AppendsNotesWithoutChangeLogRows()
    {
        _f.Sessions.Start(_c, 1);
        var before = _f.Log().Count;

        var first = _f.Sessions.Log(_c, ["Serif bargains with the harbourmaster"]);
        var second = _f.Sessions.Log(_c, ["The kraken surfaces", "Belmakor sings"]);

        Assert.Equal((1, 1, 1), (first.Number, first.NotesAdded, first.NotesTotal));
        Assert.Equal((2, 3), (second.NotesAdded, second.NotesTotal));
        Assert.Equal(before, _f.Log().Count);
        Assert.Contains("\"text\":\"Belmakor sings\"", Session(1).LiveLog);
    }

    [Fact]
    public void Log_NoLiveSession_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.Log(_c, ["x"]));

        Assert.Contains("No session is live", ex.Message);
    }

    [Theory]
    [InlineData(new string[0], "notes is required")]
    [InlineData(new[] { " " }, "notes item 1 is blank")]
    public void Log_BadNotes_AreRefused(string[] notes, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.Log(_c, notes));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void End_MarksPlayedWritesTheRecapAndHooksAndTakesABackup()
    {
        _f.Sessions.Start(_c, 1);

        var result = _f.Sessions.End(_c, "The party met Serif.", ingameEnd: "day 13",
            attendance: [new AttendanceSpec { Character = "character:belmakor" }, new AttendanceSpec { Character = "character:serif", Present = false }],
            nextHooks: ["Find the harbourmaster"]);

        Assert.Equal(("played", WriteOutcomes.Updated), (result.Status, result.Outcome));
        var session = Session(1);
        Assert.Equal((ManualNow(), "day 13"), (session.EndedAt, session.IngameEnd));
        Assert.Equal("{\"next_hooks\":[\"Find the harbourmaster\"]}", session.Data);
        Assert.Equal("The party met Serif.", _f.Entity(_c, "session:1").BodyMd);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Contains("session-end", result.BackupPath);
        Assert.Null(result.BackupProblem);
        Assert.Null(_f.Scalar<string?>("SELECT entity_id FROM session WHERE status = 'live'"));
    }

    [Fact]
    public void End_DryRun_KeepsNothingAndTakesNoBackup()
    {
        _f.Sessions.Start(_c, 1);

        var result = _f.Sessions.End(_c, "Recap.", context: new WriteContext { DryRun = true });

        Assert.Equal(("played", true), (result.Status, result.DryRun));
        Assert.Null(result.BackupPath);
        Assert.Equal("live", Session(1).Status);
    }

    [Fact]
    public void End_NoLiveSession_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.End(_c, "Recap."));

        Assert.Contains("no session is live; to record a session after the fact use", ex.Message);
    }

    [Fact]
    public void End_ContextNamingAnotherSession_IsRefused()
    {
        _f.Played(_c, 1);
        _f.Sessions.Start(_c, 2);

        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.End(_c, "Recap.", context: WriteContext.For(1)));

        Assert.Contains("session 1 is not the live one (session 2 is)", ex.Message);
    }

    [Fact]
    public void PlanAndRecordPast_TakeAnArc()
    {
        _f.Apply(_c, Op.Upsert("arc", "The Iron Guts job"));

        _f.Sessions.Plan(_c, 4, arc: "arc:the-iron-guts-job");
        _f.Sessions.RecordPast(_c, 5, arc: "arc:the-iron-guts-job");

        var arc = _f.Entity(_c, "arc:the-iron-guts-job").Id;
        Assert.Equal((arc, arc), (Session(4).ArcId, Session(5).ArcId));
        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.Plan(_c, 6, arc: "character:serif"));
        Assert.Contains("no arc", ex.Message);
    }

    [Fact]
    public void End_BlankRecap_IsRefused()
    {
        _f.Sessions.Start(_c, 1);

        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.End(_c, " "));

        Assert.Contains("recap_md is required", ex.Message);
    }

    [Fact]
    public void End_Checklist_ListsWhatTheSessionLeftToDo()
    {
        _f.Apply(_c, new CampaignOpSpec { Op = "upsert", Kind = "clock", Name = "The Void rises", Clock = new ClockSpec { Segments = 4 } },
            new CampaignOpSpec { Op = "upsert", Kind = "clock", Name = "Kraken hunger", Clock = new ClockSpec { Segments = 4 } },
            Op.Upsert("secret", "The winking moon"));
        _f.Sessions.Start(_c, 1);
        _f.Sessions.Log(_c, ["The Silver Gull docks."]);
        _f.Apply(_c,
            new CampaignOpSpec { Op = "tick", Ref = "clock:kraken-hunger" },
            new CampaignOpSpec { Op = "fact", Statement = "The moon winked.", CanonStatus = "played", Visibility = "restricted" },
            new CampaignOpSpec { Op = "fact", Statement = "Serif owes Belmakor.", CanonStatus = "played", Visibility = "restricted", KnownBy = [Op.Knower("party")] },
            new CampaignOpSpec { Op = "fact", Statement = "A made-up bard.", CanonStatus = "proposed" },
            new CampaignOpSpec { Op = "fact", Statement = "The before.", Code = "S1" },
            new CampaignOpSpec { Op = "fact", Statement = "Gated truth.", About = ["secret:the-winking-moon"], Gate = new GateSpec { After = ["S1"] }, CanonStatus = "played" });
        _f.Knowledge.Reveal(_c, ["f:5"], null, null, null, null, WriteContext.Default);

        var result = _f.Sessions.End(_c, "Belmakor and Serif met Captain Varro at Flotsam. Belmakor sang.");

        var checklist = result.Checklist!;
        Assert.Equal(["Captain Varro", "Flotsam", "Silver Gull"], checklist.UnknownNames.Order());
        Assert.Equal(["clock:the-void-rises"], checklist.ClocksNotTicked);
        Assert.Equal(["f:1"], checklist.FactsWithoutKnowers);
        Assert.Equal(["f:5 to party"], checklist.ClosedGateReveals);
        Assert.Equal(["F1 f:3"], checklist.ProposedInventions);
        Assert.True(checklist.AttendanceNotRecorded);
        Assert.False(checklist.IsEmpty);
    }

    [Fact]
    public void RecordPast_WritesAPlayedSessionInOneBatch()
    {
        var result = _f.Sessions.RecordPast(_c, 3, "Kraken, the statue, the old king", playedOn: "2026-08", precision: "approx",
            recapMd: "The party fought the old king.", attendance: [new AttendanceSpec { Character = "character:serif" }], ingame: "day 90",
            confidence: "Reconstructed");

        Assert.Equal(("session:3", "played", WriteOutcomes.Created), (result.Session, result.Status, result.Outcome));
        var entity = _f.Entity(_c, "session:3");
        Assert.Equal(("Kraken, the statue, the old king", "The party fought the old king.", "reconstructed", "party"),
            (entity.Name, entity.BodyMd, entity.Confidence, entity.Visibility));
        Assert.Equal(("2026-08", "approx", "day 90"), (Session(3).PlayedOn, Session(3).PlayedOnPrecision, Session(3).IngameStart));
        Assert.Equal(["entity", "session", "session_attendance"], _f.Log(result.BatchId).Select(r => r.TargetTable));
        Assert.NotNull(result.Checklist);
        Assert.Null(result.BackupPath);
    }

    [Fact]
    public void RecordPast_ExistingPlayed_CorrectsIt()
    {
        _f.Sessions.RecordPast(_c, 3, recapMd: "Old recap.");

        var result = _f.Sessions.RecordPast(_c, 3, recapMd: "New recap.");

        Assert.Equal(WriteOutcomes.Updated, result.Outcome);
        Assert.Equal(["body_md"], result.ChangedFields);
        Assert.Equal("New recap.", _f.Entity(_c, "session:3").BodyMd);
    }

    [Fact]
    public void RecordPast_ALiveSession_IsRefused()
    {
        _f.Sessions.Start(_c, 2);

        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.RecordPast(_c, 2, recapMd: "x"));

        Assert.Contains("session 2 is live; end it", ex.Message);
    }

    [Fact]
    public void RecordPast_SessionZero_IsAllowed()
    {
        Assert.Equal("session:0", _f.Sessions.RecordPast(_c, 0, "Session zero").Session);
    }

    [Theory]
    [InlineData("2026-8-1", null, "played_on \"2026-8-1\" is not a date")]
    [InlineData("2026-02-30", null, "is not a date")]
    [InlineData("2026-08", "weekly", "precision \"weekly\" is not a date precision")]
    public void RecordPast_BadDates_AreRefused(string playedOn, string? precision, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Sessions.RecordPast(_c, 1, playedOn: playedOn, precision: precision));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("2026-08-28", null, "day")]
    [InlineData("2026-08", null, "month")]
    [InlineData("2026", null, "approx")]
    [InlineData("2026-08", "approx", "approx")]
    public void RecordPast_DatePrecision_FollowsTheDateUnlessGiven(string playedOn, string? precision, string expected)
    {
        _f.Sessions.RecordPast(_c, 1, playedOn: playedOn, precision: precision);

        Assert.Equal(expected, Session(1).PlayedOnPrecision);
    }

    [Fact]
    public void RecordPast_AttendanceOfANonCharacter_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() =>
            _f.Sessions.RecordPast(_c, 1, attendance: [new AttendanceSpec { Character = "faction:the-party" }]));

        Assert.Contains("attendance item 1: character", ex.Message);
    }

    [Fact]
    public void Start_NoNumber_SkipsAPlannedSessionBeforeTheLastPlayed()
    {
        _f.Sessions.Plan(_c, 2);
        _f.Played(_c, 3);
        _f.Sessions.Plan(_c, 5);

        Assert.Equal(5, _f.Sessions.Start(_c).Number);
    }

    [Fact]
    public void RecordPast_NoNumber_RecordsTheNextSessionToPlay()
    {
        _f.Played(_c, 7);
        _f.Sessions.Plan(_c, 8);
        _f.Sessions.Plan(_c, 9);

        var first = _f.Sessions.RecordPast(_c, recapMd: "Played without starting it.");
        var second = _f.Sessions.RecordPast(_c, recapMd: "And the next.");
        var third = _f.Sessions.RecordPast(_c, recapMd: "Nothing planned for this one.");

        Assert.Equal((8, WriteOutcomes.Updated, "played"), (first.Number, first.Outcome, first.Status));
        Assert.Equal((9, WriteOutcomes.Updated), (second.Number, second.Outcome));
        Assert.Equal((10, WriteOutcomes.Created), (third.Number, third.Outcome));
    }

    [Theory]
    [InlineData(true, "planned")]
    [InlineData(false, null)]
    public void Start_IsFiledUnderTheSessionItStarts_SoReplayBeforeItHasNoLiveSession(bool planned, string? statusAsOfFive)
    {
        _f.Played(_c, 1);
        if (planned)
        {
            _f.Sessions.Plan(_c, 12);
        }

        var start = _f.Sessions.Start(_c, 12);
        var end = _f.Sessions.End(_c, "Recap.");

        var id = _f.Entity(_c, "session:12").Id;
        Assert.Equal(12, start.Number);
        Assert.All(_f.Log(start.BatchId).Concat(_f.Log(end.BatchId)), r => Assert.Equal(id, r.SessionId));
        using var connection = _f.Open();
        Assert.Equal(statusAsOfFive, ChangeReplay.RowAsOf(connection, "session", id, 5)?["status"]);
        Assert.Equal("played", ChangeReplay.RowAsOf(connection, "session", id, 12)!["status"]);
    }

    [Fact]
    public void RecordPast_WhileAnotherSessionIsLive_IsFiledUnderTheRecordedSession()
    {
        _f.Sessions.Start(_c, 12);

        var past = _f.Sessions.RecordPast(_c, 3, "Old", recapMd: "It happened.");

        var id = _f.Entity(_c, "session:3").Id;
        Assert.All(_f.Log(past.BatchId), r => Assert.Equal(id, r.SessionId));
        using var connection = _f.Open();
        Assert.Equal("played", ChangeReplay.RowAsOf(connection, "session", id, 5)!["status"]);
        Assert.Equal("It happened.", ChangeReplay.RowAsOf(connection, "entity", id, 3)!["body_md"]);
        Assert.Null(ChangeReplay.RowAsOf(connection, "session", id, 2));
    }

    [Fact]
    public void RecordPast_WithAnExplicitSession_IsFiledUnderThatSession()
    {
        _f.Played(_c, 1);

        var past = _f.Sessions.RecordPast(_c, 3, context: WriteContext.For(1));

        Assert.All(_f.Log(past.BatchId), r => Assert.Equal(_f.Entity(_c, "session:1").Id, r.SessionId));
    }

    [Theory]
    [InlineData("plan", "restricted")]
    [InlineData("start", "party")]
    [InlineData("end", "party")]
    [InlineData("record_past", "party")]
    public void DmCampaign_APlannedSessionStaysHiddenUntilItIsPlayed(string action, string expected)
    {
        var dm = _f.Campaign("Prep table");
        _f.Sessions.Plan(dm, 4, "The ambush at the ford");

        switch (action)
        {
            case "start":
                _f.Sessions.Start(dm, 4);
                break;
            case "end":
                _f.Sessions.Start(dm, 4);
                _f.Sessions.End(dm, "Recap.");
                break;
            case "record_past":
                _f.Sessions.RecordPast(dm, 4);
                break;
        }

        Assert.Equal(expected, _f.Entity(dm, "session:4").Visibility);
    }

    [Fact]
    public void End_Checklist_ReadsKnowledgeAndGatesAsTheVerdictsDo()
    {
        _f.Apply(_c, Op.Upsert("character", "Keras", "npc"), Op.Upsert("secret", "The cage"));
        _f.Knowledge.Record(_c, ["character:keras"], [Op.Knower("party", "met", knownAs: "the Old Sorcerer King")], WriteContext.Default);
        _f.Sessions.Start(_c, 1);
        _f.Apply(_c,
            new CampaignOpSpec { Op = "fact", Statement = "Nobody saw the moon.", CanonStatus = "played", KnownBy = [Op.Knower("party", "unaware")] },
            new CampaignOpSpec { Op = "fact", Statement = "The before.", Code = "S1", CanonStatus = "played", Visibility = "party" },
            new CampaignOpSpec { Op = "fact", Statement = "A clue.", Code = "C1" },
            new CampaignOpSpec
            {
                Op = "fact", Statement = "The truth.", About = ["secret:the-cage"], CanonStatus = "played",
                Gate = new GateSpec { After = ["S1"], Routes = [new RouteSpec { Id = "clues", Clues = ["C1"] }] },
            });
        _f.Knowledge.Reveal(_c, ["f:4"], null, null, null, null, WriteContext.Default);

        var checklist = _f.Sessions.End(_c, "Belmakor sang to the Old Sorcerer King.").Checklist!;

        // An unaware row is a record that the party does NOT know: the fact still has no knower.
        Assert.Equal(["f:1"], checklist.FactsWithoutKnowers);
        // A name the party knows the NPC by is a name, not an invention.
        Assert.Empty(checklist.UnknownNames);
        // The gate's after is met (S1 was played), but its one route is not: not ready, so a closed-gate reveal.
        Assert.Equal(["f:4 to party"], checklist.ClosedGateReveals);
    }
}
