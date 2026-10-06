using System.Globalization;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;

namespace DndMcp.Domain.Combat;

/// <summary>
/// The live combat tracker's state machine (contract §6, §12): every <c>combat</c> action but <c>prepare</c>,
/// <c>start</c>, <c>state</c> and <c>end</c> as a pure step from one <see cref="EncounterState"/> to the next. The
/// Repository runs a step inside one transaction: it asks <see cref="Needs"/> what to roll, rolls and logs each roll,
/// then <see cref="Apply"/>s the step with the rolled values and writes the changed combatants and the combat_log rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deterministic.</b> The same state, input and rolls always give the same result: no clock, no randomness, no ids
/// made here (an <c>add</c> takes them from the caller). <see cref="Needs"/> validates the input completely, so a step
/// that would be refused is refused before anything is rolled; <see cref="Apply"/> validates again (it never trusts that
/// <see cref="Needs"/> was called) and throws <see cref="ArgumentException"/> for a roll it needs and was not given (a
/// host bug).
/// </para>
/// <para>
/// <b>Rules.</b> Hit points, damage, healing, death saves, concentration, exhaustion and initiative go through the shared
/// <c>CombatRules</c> (contract §5), never re-derived; the tracker owns the turns, durations, reminders and addressing.
/// </para>
/// </remarks>
public static partial class CombatTracker
{
    /// <summary>What <paramref name="op"/> must roll before it applies (empty when everything was given).</summary>
    /// <exception cref="DndInputException">The step is refused (the message says why and what would be accepted).</exception>
    public static IReadOnlyList<RollNeed> Needs(EncounterState state, CombatOp op)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(op);
        return op switch
        {
            AddOp add => AddNeeds(state, add),
            InitiativeOp initiative => InitiativeNeeds(state, initiative),
            DamageOp damage => DamageNeeds(state, damage),
            HealOp heal => HealNeeds(state, heal),
            ConcentrationOp concentration => ConcentrationNeeds(state, concentration),
            DeathSaveOp deathSave => DeathSaveNeeds(state, deathSave),
            SetOp or LeaveOp or SurpriseOp or NextOp or PrevOp or ConditionOp or UseOp or LegendaryOp => Validated(state, op),
            _ => throw new ArgumentException($"Not a combat step: {op.GetType().Name}.", nameof(op)),
        };
    }

    /// <summary>Applies <paramref name="op"/> with the rolled values of its <see cref="Needs"/>, keyed by <see cref="RollNeed.Key"/>.</summary>
    /// <exception cref="DndInputException">The step is refused.</exception>
    /// <exception cref="ArgumentException">A needed roll is missing (a host bug).</exception>
    public static CombatStepResult Apply(EncounterState state, CombatOp op, IReadOnlyDictionary<string, RolledValue>? rolls = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(op);
        rolls ??= new Dictionary<string, RolledValue>();
        var result = op switch
        {
            AddOp add => Add(state, add, rolls),
            SetOp set => Set(state, set),
            LeaveOp leave => Leave(state, leave),
            SurpriseOp surprise => Surprise(state, surprise),
            InitiativeOp initiative => Initiative(state, initiative, rolls),
            NextOp next => Next(state, next),
            PrevOp prev => Prev(state, prev),
            DamageOp damage => Damage(state, damage, rolls),
            HealOp heal => Heal(state, heal, rolls),
            ConditionOp condition => Condition(state, condition),
            ConcentrationOp concentration => Concentration(state, concentration, rolls),
            UseOp use => Use(state, use),
            LegendaryOp legendary => Legendary(state, legendary),
            DeathSaveOp deathSave => DeathSave(state, deathSave, rolls),
            _ => throw new ArgumentException($"Not a combat step: {op.GetType().Name}.", nameof(op)),
        };

        return CiteRolls(state, op, rolls, result);
    }

    /// <summary>
    /// Makes every server roll of the step cited by at least one combat_log row (the exit criterion "combat rolls link to
    /// dice_roll rows"): a roll no row cites — the second and later dice parts of a damage call (its rows cite the first
    /// part, and name each part's roll in <c>detail.parts[i].roll</c>), or dice rolled for targets that were all dead or had
    /// left — gets one <c>note</c> row of its own, citing it, with the roll's key, purpose, expression and total.
    /// </summary>
    /// <remarks>
    /// The Repository sets each row's <c>roll_id</c> from its <see cref="CombatChange.RollKey"/>, so every
    /// <c>dice_roll</c> row the step logged is linked from exactly the rows that used it; a <c>note</c> row changes no state
    /// (a later <c>prev</c> treats it as any other change since the last <c>next</c>: the pointer moves, nothing is undone).
    /// </remarks>
    private static CombatStepResult CiteRolls(EncounterState state, CombatOp op, IReadOnlyDictionary<string, RolledValue> rolls, CombatStepResult result)
    {
        IReadOnlyList<RollNeed> needs = op switch
        {
            AddOp add => AddNeeds(state, add),
            InitiativeOp initiative => InitiativeNeeds(state, initiative),
            DamageOp damage => DamageNeeds(state, damage),
            HealOp heal => HealNeeds(state, heal),
            ConcentrationOp concentration => ConcentrationNeeds(state, concentration),
            DeathSaveOp deathSave => DeathSaveNeeds(state, deathSave),
            _ => [],
        };

        var cited = result.Changes.Select(c => c.RollKey).Where(k => k is not null).ToHashSet(StringComparer.Ordinal);
        var uncited = needs.Where(n => !cited.Contains(n.Key) && rolls.ContainsKey(n.Key)).ToList();
        if (uncited.Count == 0)
        {
            return result;
        }

        var changes = new List<CombatChange>(result.Changes);
        foreach (var need in uncited)
        {
            var rolled = rolls[need.Key];
            var detail = new System.Text.Json.Nodes.JsonObject
            {
                ["roll"] = need.Key,
                ["purpose"] = need.Purpose,
                ["expression"] = need.Expression,
                ["total"] = rolled.Total,
            };
            var subject = need.CombatantId is { } id && result.Next.Find(id) is not null ? id : null;
            changes.Add(new CombatChange(CampaignValues.CombatLogKinds.Note, state.Round, state.TurnCombatantId, subject, null, rolled.Total, CombatJson.Serialize(detail), need.Key));
        }

        return result with { Changes = changes };
    }

    // Steps that never roll are validated by applying them (pure, so nothing escapes).
    private static IReadOnlyList<RollNeed> Validated(EncounterState state, CombatOp op)
    {
        _ = Apply(state, op);
        return [];
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Shared checks

    /// <summary>
    /// A step that changes a running fight: the encounter is active (a planned one only takes add, set and leave). The start
    /// call it prints JSON-escapes the encounter's name, as every call the tracker prints does: a quote in the name would
    /// otherwise make the call the model copies invalid JSON.
    /// </summary>
    private static void RequireActive(EncounterState state, string action)
    {
        if (state.Status != CampaignValues.EncounterStatuses.Active)
        {
            throw new DndInputException(state.Status == CampaignValues.EncounterStatuses.Ended
                ? $"{action} is refused: the fight \"{state.Name}\" has ended."
                : $"{action} is refused: the fight \"{state.Name}\" is {state.Status}; start it first ({CombatCalls.Combat("start", ("encounter", state.Name))}).");
        }
    }

    /// <summary>A step that needs a fight not yet ended (add, set, leave also work on a planned one).</summary>
    private static void RequireNotEnded(EncounterState state, string action)
    {
        if (state.Status == CampaignValues.EncounterStatuses.Ended)
        {
            throw new DndInputException($"{action} is refused: the fight \"{state.Name}\" has ended.");
        }
    }

    /// <summary>
    /// Validates each item of a list (add entries, set entries, initiative rolls, damage parts), collecting each item's
    /// refusal instead of stopping at the first (contract §0: item numbering and up to five problems, <see cref="DslProblems"/>):
    /// a call with three bad items is fixed in one round trip, not three. One problem is thrown exactly as its check
    /// worded it; several become one numbered report.
    /// </summary>
    /// <param name="subject">The call, for the report's first line ("add" → "Invalid add (2 problems):").</param>
    private static void EachItem(int count, string subject, Action<int> validate)
    {
        var problems = new List<DndInputException>();
        for (var i = 0; i < count; i++)
        {
            try
            {
                validate(i);
            }
            catch (DndInputException ex)
            {
                problems.Add(ex);
            }
        }

        if (problems.Count == 1)
        {
            throw problems[0];
        }

        DslProblems.ThrowIfAny(problems.Select(p => p.Message).ToList(), subject);
    }

    /// <summary>The roll under <paramref name="key"/>, or a host-bug exception.</summary>
    private static RolledValue Rolled(IReadOnlyDictionary<string, RolledValue> rolls, string key) =>
        rolls.TryGetValue(key, out var value) ? value : throw new ArgumentException($"The roll \"{key}\" this step needs was not given.", nameof(rolls));

    /// <summary>The first kept d20 face of a roll (1-20), or a host-bug exception.</summary>
    private static int D20Face(RolledValue value, string key)
    {
        if (value.Faces.Count == 0 || value.Faces[0] is < 1 or > 20)
        {
            throw new ArgumentException($"The roll \"{key}\" has no kept d20 face.", nameof(value));
        }

        return value.Faces[0];
    }

    /// <summary>A canonical damage type, or a refusal.</summary>
    private static string? DamageType(string? text, string field)
    {
        if (text is null)
        {
            return null;
        }

        return DslValues.DamageTypes.Set.TryMatch(text, out var canonical)
            ? canonical
            : throw new DndInputException($"{field} \"{DslText.Echo(text)}\" is not a damage type; give one of {DslValues.DamageTypes.Set.List}.");
    }

    /// <summary>A whole number in range, or a refusal naming the field.</summary>
    private static void Range(string field, int? value, int min, int max)
    {
        if (value is { } v && (v < min || v > max))
        {
            throw new DndInputException($"{field} is {N(v)}; it is {N(min)} to {N(max)}.");
        }
    }

    /// <summary>Exactly one target, or a refusal (single-target actions take a list of one, contract §6.0).</summary>
    private static CombatantState One(EncounterState state, IReadOnlyList<string>? targets, string action)
    {
        var resolved = CombatAddressing.ResolveMany(state.Combatants, targets, "targets");
        if (resolved.Count != 1)
        {
            throw new DndInputException($"{action} takes exactly one target (a list of one, e.g. [\"{resolved[0].Address}\"]); targets named {N(resolved.Count)}.");
        }

        return resolved[0];
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
