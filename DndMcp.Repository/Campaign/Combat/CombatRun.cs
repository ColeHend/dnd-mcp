using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using Microsoft.Data.Sqlite;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// One combat call inside its transaction: the encounter and its state (read in this transaction, CRIT Q10), and the
/// tracker steps it runs one after another (<c>start</c> runs several: re-seed, add the party, add, surprise). Each step
/// asks the tracker what to roll, rolls and logs it (<see cref="CombatDice"/>), applies it, and writes the changed rows
/// and the combat_log rows with their <c>roll_id</c> (<see cref="CombatStore.Save"/>), so a later step of the same call
/// reads what the earlier one wrote. What every step said is collected for the call's one result.
/// </summary>
internal sealed class CombatRun
{
    private readonly IDiceRoller? _roller;
    private readonly Dictionary<string, RollSubject> _newSubjects = new(StringComparer.Ordinal);

    // The turn context's dying lines among Reminders, by reference (the very objects the steps returned), so
    // DistinctReminders can tell them from a step's own dying line for the same creature.
    private readonly HashSet<CombatReminder> _contextDying = new(ReferenceEqualityComparer.Instance);

    public CombatRun(SqliteConnection connection, SqliteTransaction transaction, CampaignRow campaign, EncounterRow row, EncounterState state,
        string at, CombatSubjects subjects, IDiceRoller? roller, bool? secret = null)
    {
        Connection = connection;
        Transaction = transaction;
        Campaign = campaign;
        Row = row;
        State = state;
        At = at;
        Subjects = subjects;
        _roller = roller;
        Secret = secret;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    public CampaignRow Campaign { get; }

    /// <summary>The encounter row (its lifecycle columns; refreshed by <see cref="Reload"/>).</summary>
    public EncounterRow Row { get; private set; }

    /// <summary>The tracker state as the last step left it.</summary>
    public EncounterState State { get; private set; }

    public string At { get; }

    public CombatSubjects Subjects { get; }

    /// <summary>The call's <c>secret</c> (overrides §6.10's default for every roll of the call).</summary>
    public bool? Secret { get; }

    public List<string> Lines { get; } = [];

    public List<CombatReminder> Reminders { get; } = [];

    public List<CombatRoll> Rolls { get; } = [];

    /// <summary>
    /// Says a new add entry's combatants (their ids, before they exist) roll as that entry's subject: its precomputed
    /// name (<see cref="CombatSubjects.EntryKey"/>), its side and hidden flag as the tracker will set them.
    /// </summary>
    public void ExpectNew(string id, RollSubject subject) => _newSubjects[id] = subject;

    /// <summary>
    /// Runs one tracker step: needs, rolls (logged), apply, write. A refusal (<see cref="DndInputException"/>) comes from
    /// the tracker before anything is rolled; the whole call's transaction then rolls back.
    /// </summary>
    public CombatStepResult Apply(CombatOp op)
    {
        var needs = CombatTracker.Needs(State, op);
        CombatDice.Rolled rolled;
        if (needs.Count == 0)
        {
            rolled = CombatDice.Rolled.Empty;
        }
        else
        {
            var roller = _roller ?? throw new InvalidOperationException("This combat call cannot roll dice (no roller): give the values.");
            rolled = CombatDice.Roll(Connection, Transaction, Campaign, Row, needs, SubjectOf, Targets(op), Subjects, Secret, roller, At);
        }

        var result = CombatTracker.Apply(State, op, rolled.Values);
        CombatStore.Save(Connection, Transaction, State, result, rolled.Ids, At);
        Lines.AddRange(result.Notes);
        _contextDying.UnionWith(ContextDying(result));
        Reminders.AddRange(result.Reminders);
        Rolls.AddRange(rolled.Views);
        State = result.Next;
        return result;
    }

    /// <summary>Re-reads the encounter row (after the lifecycle changed it) and its state.</summary>
    public void Reload()
    {
        Row = CombatStore.Encounter(Connection, Row.Id, Transaction) ?? throw new InvalidOperationException("The encounter vanished inside its own transaction.");
        State = CombatStore.Load(Connection, Campaign, Row, Transaction);
    }

    /// <summary>Replaces the state (a lifecycle change written by the caller, e.g. a re-seed).</summary>
    public void Use(EncounterState state) => State = state;

    /// <summary>
    /// The reminders of the call, each said once, with one dying line per creature (contract §6.3: "one dying line per
    /// OTHER combatant at 0 HP that is dying"). A hit on a dying creature makes the step say why its tallies moved
    /// ("Lieutenant James Torch: 0 HP, 0 successes, 2 failures (a critical hit: two death save failures)") while the turn's
    /// context says it is dying ("… 0 successes, 2 failures"): the step's own line is kept and the context's line for that
    /// creature dropped. A dying creature no step spoke of keeps the context's line. Without this, FIX A23 and B20 print the
    /// same tallies twice, and a routed <c>campaign_character damage</c> on a dying character does too.
    /// </summary>
    public IReadOnlyList<CombatReminder> DistinctReminders()
    {
        var saidByAStep = Reminders
            .Where(r => r.Kind == K.Dying && r.CombatantId is not null && !_contextDying.Contains(r))
            .Select(r => r.CombatantId!)
            .ToHashSet(StringComparer.Ordinal);
        return Reminders
            .Where(r => !(_contextDying.Contains(r) && r.CombatantId is not null && saidByAStep.Contains(r.CombatantId)))
            .DistinctBy(r => (r.Kind, r.CombatantId, r.Text, r.Call))
            .ToList();
    }

    /// <summary>The author result of the call.</summary>
    public CombatOutcome Outcome(string action, IReadOnlyList<string> warnings, bool created = false)
    {
        Row = CombatStore.Encounter(Connection, Row.Id, Transaction) ?? Row;
        return new CombatOutcome(
            action,
            Campaign.Slug,
            CombatViews.Encounter(Connection, Transaction, Row, State),
            Lines.ToList(),
            DistinctReminders(),
            Rolls.ToList(),
            warnings,
            created);
    }

    // A step's turn-context dying lines: its result is its own reminders followed by the context of the state it left
    // (CombatWork.Finish appends CombatContext.Reminders of that state; recomputed here from the same state, the tail is
    // equal). When the tail is not the context, nothing is marked, so nothing is dropped.
    private static IEnumerable<CombatReminder> ContextDying(CombatStepResult result)
    {
        var context = CombatContext.Reminders(result.Next);
        var own = result.Reminders.Count - context.Count;
        if (own < 0 || !result.Reminders.Skip(own).SequenceEqual(context))
        {
            return [];
        }

        return result.Reminders.Skip(own).Where(r => r.Kind == K.Dying);
    }

    private RollSubject? SubjectOf(string? id)
    {
        if (id is null)
        {
            return null;
        }

        if (State.Find(id) is { } c)
        {
            return RollSubject.Of(c);
        }

        return _newSubjects.TryGetValue(id, out var subject) ? subject : new RollSubject(id, S.Enemy, true, null);
    }

    // The step's targets, for the no-subject rule (several targets and no source: secret when any target alone would be).
    private IReadOnlyList<RollSubject> Targets(CombatOp op)
    {
        IReadOnlyList<string>? targets = op switch
        {
            DamageOp d => d.Targets,
            HealOp h => h.Targets,
            _ => null,
        };
        if (targets is null || targets.Count == 0)
        {
            return [];
        }

        try
        {
            return CombatAddressing.ResolveMany(State.Combatants, targets, "targets").Select(RollSubject.Of).ToList();
        }
        catch (DndInputException)
        {
            // The tracker refuses the step itself; for secrecy, an unresolvable target is the safe side.
            return [new RollSubject(string.Empty, S.Enemy, true, null)];
        }
    }
}
