using System.Text.Json.Nodes;

namespace DndMcp.Domain.Characters;

/// <summary>
/// What a sheet operation returns: the new sheet, what changed (for the Repository to log as ONE batch), notes saying
/// what was derived or done, and reminders of what the caller still has to do.
/// </summary>
/// <param name="Sheet">The sheet after the operation.</param>
/// <param name="Diff">What changed; empty when nothing did (the Repository then logs nothing and says so).</param>
/// <param name="Notes">What happened, for the result text ("Rage: 4/4 (long rest)").</param>
/// <param name="Reminders">What to do next, each with the call that does it where there is one.</param>
public sealed record SheetResult(CharacterSheet Sheet, SheetDiff Diff, IReadOnlyList<string> Notes, IReadOnlyList<SheetReminder> Reminders);

/// <summary>
/// What <see cref="SheetUpdate.Apply"/> returns: <see cref="SheetResult"/>'s fields and whether the sheet is new (the
/// Repository inserts it whole, <see cref="SheetJson.ToColumns"/>, instead of updating).
/// </summary>
public sealed record SheetUpdateResult(
    CharacterSheet Sheet,
    bool Created,
    SheetDiff Diff,
    IReadOnlyList<string> Notes,
    IReadOnlyList<SheetReminder> Reminders);

/// <summary>What <see cref="SheetRest"/> returns: <see cref="SheetResult"/>'s fields and the Hit Dice spent.</summary>
/// <param name="HitDice">Each Hit Die spent on a short rest, in spending order (largest die first).</param>
/// <param name="HitPointsRegained">Hit points actually regained (capped at the effective maximum).</param>
public sealed record SheetRestResult(
    CharacterSheet Sheet,
    SheetDiff Diff,
    IReadOnlyList<string> Notes,
    IReadOnlyList<SheetReminder> Reminders,
    IReadOnlyList<HitDieRoll> HitDice,
    int HitPointsRegained);

/// <summary>One Hit Die spent: its size, the face rolled or given, and the hit points it gave (face + Con, floored by edition).</summary>
public sealed record HitDieRoll(int Sides, int Face, int Gain);

/// <summary>
/// Dice an operation needs before it can apply (the Domain rolls nothing: contract §0). The Repository rolls
/// <see cref="Expression"/> once with its <c>IDiceRoller</c>, logs it as one dice_roll row (purpose
/// <see cref="Purpose"/>, label "&lt;name&gt;: hit dice"), and passes the faces back in need order.
/// </summary>
/// <param name="Key">Stable within one call: "hit-dice-d10".</param>
/// <param name="Purpose">"hit dice" (contract §6.10's purposes).</param>
/// <param name="Expression">What is rolled: "2d10".</param>
/// <param name="Sides">The die.</param>
/// <param name="Count">How many faces the roll gives back.</param>
public sealed record SheetRollNeed(string Key, string Purpose, string Expression, int Sides, int Count);

/// <summary>
/// Something the caller should do next. <see cref="Kind"/> is a <see cref="SheetValues.ReminderKinds"/> value; when a
/// <c>campaign_character</c> call resolves it, <see cref="Action"/> and <see cref="Arguments"/> say which, and
/// <see cref="CallFor"/> writes it for a character handle.
/// </summary>
public sealed record SheetReminder(string Kind, string Text, string? Action = null, IReadOnlyDictionary<string, object?>? Arguments = null)
{
    /// <summary>
    /// <c>campaign_character {"action": "level_up", "character": "character:belmakor"}</c>: the resolving call for a
    /// character, or null when no single call resolves it.
    /// </summary>
    public string? CallFor(string handle)
    {
        if (Action is null)
        {
            return null;
        }

        // Written as the tool descriptions and refusals write calls: {"action": "level_up", "character": "…"}.
        var parts = new List<string> { Pair("action", Action), Pair("character", handle) };
        foreach (var (name, value) in Arguments ?? new Dictionary<string, object?>())
        {
            if (value is ReminderPlaceholder placeholder)
            {
                parts.Add($"{Quote(name)}: {placeholder.Text}");
                continue;
            }

            JsonNode? node = value switch
            {
                null => null,
                JsonNode n => n.DeepClone(),
                string s => s,
                int i => i,
                long l => l,
                bool b => b,
                _ => JsonValue.Create(value.ToString()),
            };
            parts.Add($"{Quote(name)}: {Format(node)}");
        }

        return "campaign_character {" + string.Join(", ", parts) + "}";
    }

    /// <summary>JSON with a space after every colon and comma, as calls are written for the model to read.</summary>
    private static string Format(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(", ", o.Select(p => $"{Quote(p.Key)}: {Format(p.Value)}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(Format)) + "]",
        _ => node.ToJsonString(CallOptions),
    };

    private static string Pair(string name, string value) => $"{Quote(name)}: {Quote(value)}";

    private static string Quote(string text) => JsonValue.Create(text).ToJsonString(CallOptions);

    private static readonly System.Text.Json.JsonSerializerOptions CallOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// An argument of a reminder's call that the caller fills in, written as is: <c>{"slots": [4, 3, …]}</c>. A call that
/// needs values only the caller knows (multiclass slots, a new sim_profile) shows their shape this way, as the Phase 6
/// prompts write <c>"ops": [...]</c>.
/// </summary>
public sealed record ReminderPlaceholder(string Text);
