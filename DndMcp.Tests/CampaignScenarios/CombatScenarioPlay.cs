using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Combat;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignCombat;
using DndMcp.Tests.CampaignRead;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// One step of an exit-criteria fight as the Repository left it once the step's transaction committed (contract §15 X):
/// the author result the step returned, the fight as a FRESH connection reads it back (so a column the store forgot to
/// write, or wrote and cannot read, shows up as a difference from <see cref="CombatOutcome.Encounter"/>'s state), the
/// change_log row count (A32/B27: a step never logs), the sheet-side tables as text (D6: nothing reaches a sheet, a
/// holding, a coin or an award before <c>end</c>), the combat_log and dice_roll rows the step appended, and the party
/// board of every non-author view at that moment (§6.12), so the leak sweeps cover the whole fight rather than one
/// snapshot of it.
/// </summary>
/// <param name="Step">FIX's step number, as K's <see cref="CombatScripts"/> keys it ("A8", "A12-legendary", "B16").</param>
/// <param name="Boards">Every non-author view's board right after the step, keyed by perspective text.</param>
public sealed record CombatStepRecord(
    string Step,
    CombatOutcome Outcome,
    EncounterState Persisted,
    long ChangeRows,
    string SheetTables,
    IReadOnlyList<CombatLogRow> Log,
    IReadOnlyList<DiceRollRow> Dice,
    IReadOnlyDictionary<string, CombatBoard> Boards)
{
    /// <summary>A combatant of the fight as stored after the step, by its exact tracker name.</summary>
    public CombatantState Named(string name) => Persisted.Combatants.Single(c => c.Name == name);

    /// <summary>The step's reminders of one kind (<see cref="CombatValues.ReminderKinds"/>).</summary>
    public IReadOnlyList<CombatReminder> Of(string kind) => Outcome.Reminders.Where(r => r.Kind == kind).ToList();

    /// <summary>Whether a reminder of the kind carries every token (FIX lists reminders as tokens, not whole sentences).</summary>
    public bool Says(string kind, params string[] tokens) =>
        Outcome.Reminders.Any(r => r.Kind == kind && tokens.All(t => r.Text.Contains(t, StringComparison.Ordinal)));

    /// <summary>Whether a line of what changed carries every token.</summary>
    public bool Line(params string[] tokens) => Outcome.Lines.Any(l => tokens.All(t => l.Contains(t, StringComparison.Ordinal)));

    /// <summary>The kinds of the combat_log rows the step appended, in order.</summary>
    public IReadOnlyList<string> Kinds => Log.Select(r => r.Kind).ToList();
}

/// <summary>
/// An exit-criteria fight played once through the Repository, step by step, with what the store held after every step
/// (<see cref="CombatStepRecord"/>) and around <c>end</c>: the scenario tests read these records and never write, so one
/// play serves a whole test class (<see cref="Xunit.IClassFixture{TFixture}"/>). The fights are K's scripts
/// (<see cref="CombatScripts.StepsA"/>, <see cref="CombatScripts.StepsB"/>: FIX §2.2 and §4.2 with contract §16's call
/// shapes) on the Phase 6 scenario worlds with C's <c>FixtureSheets</c>; nothing here re-derives a call, so a change to
/// the scripts is a change to what these scenarios pin.
/// </summary>
public abstract class CombatPlay : IDisposable
{
    private readonly List<CombatStepRecord> _steps = [];
    private long _logSeq;
    private long _diceSeq;

    private protected CombatPlay(CombatWorld world, IReadOnlyList<string> views)
    {
        World = world;
        Views = views;
    }

    internal CombatWorld World { get; }

    /// <summary>The non-author perspectives whose boards are read after every step.</summary>
    public IReadOnlyList<string> Views { get; }

    /// <summary>The session start (A0/B0).</summary>
    public SessionWriteResult SessionStart { get; private set; } = null!;

    /// <summary>The change_log row count once the session is live (FIX's N0).</summary>
    public long N0 { get; private set; }

    /// <summary>The sheet-side tables before the first combat call (<see cref="CombatStepRecord.SheetTables"/>).</summary>
    public string SheetTablesBefore { get; private set; } = string.Empty;

    public IReadOnlyList<CombatStepRecord> Steps => _steps;

    public CombatStepRecord this[string step] => _steps.Single(s => s.Step == step);

    /// <summary>Every logged table but updated_at, just before <c>end</c> (<see cref="CampaignWrite.WriteFixture.Dump"/>).</summary>
    public string DumpBeforeEnd { get; private set; } = string.Empty;

    /// <summary>The same, just after <c>end</c>.</summary>
    public string DumpAfterEnd { get; private set; } = string.Empty;

    /// <summary>Every character_sheet row, every column, keyed by the character's handle, just before and just after <c>end</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> SheetRowsBefore { get; private set; } = null!;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> SheetRowsAfter { get; private set; } = null!;

    /// <summary><c>end</c>'s author result.</summary>
    public CombatEndOutcome End { get; private set; } = null!;

    /// <summary>The change_log rows of the write-back batch.</summary>
    public IReadOnlyList<ChangeRow> EndRows { get; private set; } = [];

    /// <summary>The change_log row count after <c>end</c>.</summary>
    public long ChangeRowsAfterEnd { get; private set; }

    /// <summary>The combat_log and dice_roll rows <c>end</c> appended.</summary>
    public IReadOnlyList<CombatLogRow> EndLog { get; private set; } = [];

    public IReadOnlyList<DiceRollRow> EndDice { get; private set; } = [];

    /// <summary>Every non-author view's board of the ended fight ("last"), and of "current" (none is running).</summary>
    public IReadOnlyDictionary<string, CombatBoard> LastBoards { get; private set; } = null!;

    public IReadOnlyDictionary<string, CombatBoard> CurrentBoards { get; private set; } = null!;

    /// <summary>The encounter row after <c>end</c>.</summary>
    public EncounterRow Encounter { get; private set; } = null!;

    /// <summary>The live session's entity id.</summary>
    public string SessionId { get; private set; } = string.Empty;

    /// <summary>Starts the fight's session live with its attendance (A0/B0) and takes the baselines.</summary>
    private protected void Begin(int session, IReadOnlyList<string> present)
    {
        SessionStart = World.F.Sessions.Start(World.Campaign, session, attendance: present.Select(p => (AttendanceSpec?)new AttendanceSpec { Character = p }).ToList());
        World.Reload();
        SessionId = World.Id("session:" + session.ToString(CultureInfo.InvariantCulture));
        N0 = World.ChangeRows();
        SheetTablesBefore = SheetTables();
        _logSeq = World.F.Scalar<long>("SELECT coalesce(max(seq), 0) FROM combat_log");
        _diceSeq = World.F.Scalar<long>("SELECT coalesce(max(seq), 0) FROM dice_roll");
    }

    /// <summary>Runs every step, recording what the store holds after each one.</summary>
    private protected void Play(IEnumerable<(string Step, Func<CombatOutcome> Call)> steps)
    {
        foreach (var (step, call) in steps)
        {
            Tick();
            var outcome = call();
            _steps.Add(new CombatStepRecord(
                step,
                outcome,
                World.State(outcome.Encounter.Name),
                World.ChangeRows(),
                SheetTables(),
                NewLog(),
                NewDice(),
                Views.ToDictionary(v => v, v => World.Reader.Board(World.Campaign, Perspective.Parse(v)), StringComparer.Ordinal)));
        }
    }

    /// <summary>Ends the fight, recording the store on both sides of the write-back.</summary>
    private protected void Finish(Func<CombatEndOutcome> end)
    {
        var name = _steps[^1].Outcome.Encounter.Name;
        DumpBeforeEnd = World.F.Dump();
        SheetRowsBefore = SheetRows(World);
        Tick();
        End = end();
        DumpAfterEnd = World.F.Dump();
        SheetRowsAfter = SheetRows(World);
        EndRows = End.BatchId is null ? [] : World.F.Log(End.BatchId);
        ChangeRowsAfterEnd = World.ChangeRows();
        EndLog = NewLog();
        EndDice = NewDice();
        Encounter = World.Encounter(name);
        LastBoards = Views.ToDictionary(v => v, v => World.Reader.Board(World.Campaign, Perspective.Parse(v), EncounterResolver.Last), StringComparer.Ordinal);
        CurrentBoards = Views.ToDictionary(v => v, v => World.Reader.Board(World.Campaign, Perspective.Parse(v)), StringComparer.Ordinal);
    }

    /// <summary>
    /// Moves the test clock on before each call, as a real table's would: a call that re-stamped a row it should not have
    /// touched (a sheet's updated_at during the fight) then shows in <see cref="CombatStepRecord.SheetTables"/>, and what a
    /// call writes carries its own time (an end's rows are stamped after everything the fixtures wrote).
    /// </summary>
    private void Tick() => World.F.Db.Time.Advance(TimeSpan.FromSeconds(30));

    /// <summary>The persisted fight with the row timestamps cleared (the tracker's own state never carries them).</summary>
    public static string Comparable(EncounterState state) =>
        LeakAssert.Serialize(state with { Combatants = state.Combatants.Select(c => c with { CreatedAt = null, UpdatedAt = null }).ToList() });

    /// <summary>Every die face a <c>dice_roll.detail</c> stored (<c>DiceLogDetail.Json</c>: groups → dice → faces), in order.</summary>
    public static IReadOnlyList<int> Faces(string detail)
    {
        var faces = new List<int>();
        Walk(System.Text.Json.Nodes.JsonNode.Parse(detail));
        return faces;

        void Walk(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "face")
                        {
                            faces.Add(value!.GetValue<int>());
                        }
                        else
                        {
                            Walk(value);
                        }
                    }

                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var item in a)
                    {
                        Walk(item);
                    }

                    break;
            }
        }
    }

    /// <summary>A sheet column's stored value as text (NULL as null).</summary>
    public static string? Text(IReadOnlyDictionary<string, object?> row, string column) =>
        row[column] is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    /// <summary>The columns whose stored value differs between two snapshots of one sheet row.</summary>
    public static IReadOnlyList<string> ChangedColumns(IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after) =>
        before.Keys.Where(k => Text(before, k) != Text(after, k)).Order(StringComparer.Ordinal).ToList();

    public void Dispose()
    {
        World.Dispose();
        GC.SuppressFinalize(this);
    }

    // Every row of the tables a sheet's life is kept in, every column (updated_at included: a step that so much as re-stamped
    // a sheet would show), sorted: two snapshots are equal only when nothing on that side moved.
    private string SheetTables()
    {
        using var connection = World.Open();
        var lines = new List<string>();
        foreach (var table in new[] { CampaignTables.CharacterSheet, CampaignTables.Holding, CampaignTables.CurrencyTxn, CampaignTables.Award })
        {
            foreach (IDictionary<string, object?> row in connection.Query($"SELECT * FROM {table.Name}"))
            {
                lines.Add(table.Name + ": " + string.Join(" | ", row.Select(c => c.Key + "=" + Convert.ToString(c.Value, CultureInfo.InvariantCulture))));
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    /// <summary>Every character_sheet row of a world, every column as stored, keyed by the character's handle.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> SheetRows(CombatWorld world)
    {
        using var connection = world.Open();
        var rows = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (IDictionary<string, object?> row in connection.Query(
                     "SELECT e.kind || ':' || e.slug AS handle, s.* FROM character_sheet s JOIN entity e ON e.id = s.entity_id"))
        {
            var columns = row.Where(c => c.Key != "handle").ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
            rows[(string)row["handle"]!] = columns;
        }

        return rows;
    }

    private List<CombatLogRow> NewLog()
    {
        using var connection = World.Open();
        var rows = connection.Query<CombatLogRow>($"SELECT {CombatLogRow.Columns} FROM combat_log WHERE seq > @seq ORDER BY seq", new { seq = _logSeq }).ToList();
        _logSeq = rows.Count > 0 ? rows[^1].Seq : _logSeq;
        return rows;
    }

    private List<DiceRollRow> NewDice()
    {
        using var connection = World.Open();
        var rows = connection.Query<DiceRollRow>($"SELECT {DiceRollRow.Columns} FROM dice_roll WHERE seq > @seq ORDER BY seq", new { seq = _diceSeq }).ToList();
        _diceSeq = rows.Count > 0 ? rows[^1].Seq : _diceSeq;
        return rows;
    }
}

/// <summary>
/// Fixture A (FIX §2, contract §16) played once: session 4 started live with the six living PCs (A0), A1-A31 through
/// <see cref="CombatScripts.StepsA"/>, A33's end. Every non-author view of the Belmakor campaign is read after every step.
/// </summary>
public sealed class CombatBelmakorPlay : CombatPlay
{
    /// <summary>A0's attendance: the six living PCs (Tristan is dead).</summary>
    public static readonly IReadOnlyList<string> Present =
        ["character:belmakor", "character:vars", "character:ignis", "character:serif", "character:torch", "character:aiden-ironstar"];

    public CombatBelmakorPlay()
        : base(CombatWorld.Belmakor(), BelmakorScenario.NonAuthorPerspectives)
    {
        try
        {
            Begin(4, Present);
            Play(CombatScripts.StepsA(World));
            Finish(() => CombatScripts.EndA(World));
        }
        catch
        {
            World.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Fixture B (FIX §4, contract §16) played once: session 13 started live with the three PCs (B0), FIX §4.1's faces queued,
/// B1-B26 through <see cref="CombatScripts.StepsB"/>, B28's end. Every non-author view of the One Piece campaign (the
/// Phase 6 list and the fishman monk) is read after every step.
/// </summary>
public sealed class CombatOnePiecePlay : CombatPlay
{
    /// <summary>B0's attendance: the three PCs.</summary>
    public static readonly IReadOnlyList<string> Present = ["character:bjorn-mountainfell", "character:fishman-monk", "character:dragon-slayer"];

    /// <summary>The One Piece non-author views: the Phase 6 list (the player views and the NPCs with knowledge) and fixture B's monk.</summary>
    public static readonly IReadOnlyList<string> NonAuthorPerspectives = [.. OnePieceScenario.NonAuthorPerspectives, "character:fishman-monk"];

    /// <summary>The views of the players (FIX §5.2's list applies to these); the rest are NPC characters.</summary>
    public static readonly IReadOnlyList<string> PlayerViews = [.. OnePieceScenario.PlayerViews, "character:fishman-monk"];

    public CombatOnePiecePlay()
        : base(CombatWorld.OnePiece(), NonAuthorPerspectives)
    {
        try
        {
            Begin(13, Present);
            World.Roller.Push(5, 4, 5, 2, 3, 3, 2);
            Play(CombatScripts.StepsB(World));
            Finish(() => CombatScripts.EndB(World));
        }
        catch
        {
            World.Dispose();
            throw;
        }
    }
}
