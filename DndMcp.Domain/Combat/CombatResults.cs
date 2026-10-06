using System.Text.Json.Nodes;
using DndMcp.Domain.Dice;

namespace DndMcp.Domain.Combat;

/// <summary>
/// A die roll a step needs before it can apply (contract §12.2): the Domain rolls nothing, so it says what to roll. The
/// Repository rolls <see cref="Expression"/> with its <c>IDiceRoller</c>, logs it as one <c>dice_roll</c> row (label
/// <c>&lt;name&gt;: &lt;purpose&gt;</c> from the subject, secrecy per §6.10) and passes the result back under
/// <see cref="Key"/>.
/// </summary>
/// <param name="Key">Stable within one call ("damage", "initiative:&lt;id&gt;", "hp:&lt;id&gt;"): <see cref="CombatChange.RollKey"/> cites it.</param>
/// <param name="CombatantId">
/// The roll's subject (§6.10 as amended by F1): the combatant rolled for; for damage the source, else the turn-holder while a
/// turn runs (the actor, who rolls it), else the single target; for healing the source, else the single target; null when
/// none of these applies (several targets, no source).
/// </param>
/// <param name="Purpose"><see cref="CombatValues.Purposes"/>.</param>
/// <param name="Expression">What to roll, already reflecting Advantage or Disadvantage and critical doubling ("2d20kh1+3", "4d6+5"): the <c>dice_roll.expression</c>.</param>
public sealed record RollNeed(string Key, string? CombatantId, string Purpose, string Expression);

/// <summary>
/// A rolled need: the total, and the KEPT dice faces in roll order (a <c>2d20kl1</c>'s lower die; a <c>4d6+5</c>'s four
/// faces). A d20 rule (initiative, a death or concentration save) reads the first kept face, so a natural 1 or 20 and the
/// rule's own modifiers are applied by the tracker, never doubled.
/// </summary>
public sealed record RolledValue(int Total, IReadOnlyList<int> Faces)
{
    /// <summary>The value of a roll the Repository made with <see cref="DiceEvaluator"/>: its total and every die not dropped.</summary>
    public static RolledValue From(DiceRoll roll)
    {
        ArgumentNullException.ThrowIfNull(roll);
        var faces = roll.Groups.SelectMany(g => g.Dice).Where(d => !d.Dropped).Select(d => (int)d.Value).ToList();
        return new RolledValue(checked((int)roll.Total), faces);
    }
}

/// <summary>
/// One combat_log row a step writes (contract §6.9): its kind (<c>CampaignValues.CombatLogKinds</c>), the round and turn
/// it happened in, the actor (the source, else the turn-holder) and the target, an amount, the detail object as text
/// (arithmetic, given faces with <c>"given": true</c>, ruling flags, a turn row's automatic changes), and the key of the
/// server roll it cites (the Repository sets <c>roll_id</c> to that roll's <c>dice_roll</c> id).
/// </summary>
public sealed record CombatChange(
    string Kind,
    int Round,
    string? TurnCombatantId,
    string? ActorId,
    string? TargetId,
    int? Amount,
    string? Detail,
    string? RollKey = null);

/// <summary>
/// A reminder (contract §6.8): what to remember or do next, about one combatant (or the fight), with the exact call that
/// resolves it where one exists (<c>combat {"action": "concentration", "targets": ["belmakor"], "total": …}</c>). Author
/// output: it may name tracker names and handles.
/// </summary>
/// <param name="Kind"><see cref="CombatValues.ReminderKinds"/>.</param>
public sealed record CombatReminder(string Kind, string? CombatantId, string Text, string? Call = null);

/// <summary>
/// What one step did: the next state, the combat_log rows to write, the reminders (the step's own, then the turn's: the
/// turn-holder's conditions and exhaustion, what ends at the end of its turn, the dying, the legendary creatures that may
/// act), and the per-target lines of what changed ("Belmakor Silverwind: 14 bludgeoning + 21 necrotic = 35; temporary HP 7
/// → 0; 110 → 82").
/// </summary>
public sealed record CombatStepResult(
    EncounterState Next,
    IReadOnlyList<CombatChange> Changes,
    IReadOnlyList<CombatReminder> Reminders,
    IReadOnlyList<string> Notes)
{
    /// <summary>The combatants whose rows changed or were added (the Repository writes these).</summary>
    public IReadOnlyList<string> ChangedCombatants { get; init; } = [];
}

/// <summary>
/// Writes the calls reminders carry as the tool descriptions write them: <c>combat {"action": "set", "combatants":
/// [{"character": "character:vars", "hp": …}]}</c>, a space after every colon and comma, "…" where the caller fills a value.
/// </summary>
public static class CombatCalls
{
    /// <summary>A value the caller fills in, written as "…".</summary>
    public static readonly object Fill = new();

    /// <summary><c>combat {"action": "&lt;action&gt;", …}</c> with the arguments in order (null values left out).</summary>
    public static string Combat(string action, params (string Name, object? Value)[] arguments) => Tool("combat", action, arguments);

    /// <summary>
    /// <c>&lt;tool&gt; {"action": "&lt;action&gt;", …}</c> for another tool the tracker's results point at
    /// (<c>campaign_character</c>): every value JSON-escaped, so a name with a quote still makes a call the model can copy.
    /// </summary>
    public static string Tool(string tool, string action, params (string Name, object? Value)[] arguments)
    {
        var parts = new List<string> { $"{Quote("action")}: {Quote(action)}" };
        foreach (var (name, value) in arguments)
        {
            if (value is null)
            {
                continue;
            }

            parts.Add($"{Quote(name)}: {Format(value)}");
        }

        return tool + " {" + string.Join(", ", parts) + "}";
    }

    /// <summary>The value as JSON with spaces, <see cref="Fill"/> as "…".</summary>
    public static string Format(object? value) => value switch
    {
        null => "null",
        _ when ReferenceEquals(value, Fill) => "…",
        string s => Quote(s),
        bool b => b ? "true" : "false",
        int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        double d => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        IEnumerable<(string Name, object? Value)> fields => "{" + string.Join(", ", fields.Where(f => f.Value is not null).Select(f => $"{Quote(f.Name)}: {Format(f.Value)}")) + "}",
        System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(Format)) + "]",
        _ => Quote(value.ToString() ?? string.Empty),
    };

    /// <summary>An object argument: its fields in order (null values left out).</summary>
    public static IEnumerable<(string Name, object? Value)> Object(params (string Name, object? Value)[] fields) => fields;

    private static string Quote(string text) => JsonValue.Create(text).ToJsonString(Options);

    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
