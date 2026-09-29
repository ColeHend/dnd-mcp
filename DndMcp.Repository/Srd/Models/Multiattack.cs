using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;
using DndMcp.Repository.Srd.Json;

namespace DndMcp.Repository.Srd.Models;

/// <summary>
/// How many times a multiattack uses an action. Usually a fixed number, but not always: the hydra's is
/// <c>"Number of Heads"</c> (both editions) and the 2014 violet fungus's is <c>"1d4"</c>.
///
/// <para>
/// <see cref="Value"/> is null for those, and the simulator must decide what they mean (Phase 5 normalizer). Parsing
/// them as 0 or 1 would make the hydra a one-bite monster without any warning. <see cref="Text"/> always keeps the
/// original token for display and for the normalizer's warning.
/// </para>
/// </summary>
[JsonConverter(typeof(MultiattackCountConverter))]
public sealed class MultiattackCount
{
    private MultiattackCount(string text, int? value, bool wasJsonNumber)
    {
        Text = text;
        Value = value;
        WasJsonNumber = wasJsonNumber;
    }

    /// <summary>The count as upstream wrote it: <c>"2"</c>, <c>"Number of Heads"</c>, <c>"1d4"</c>.</summary>
    public string Text { get; }

    /// <summary>The fixed count, or null when <see cref="Text"/> is not a plain non-negative integer.</summary>
    public int? Value { get; }

    /// <summary>True when the count is a fixed number the simulator can use directly.</summary>
    public bool IsFixed => Value is not null;

    /// <summary>
    /// True when the JSON token was a number rather than a string, so writing reproduces the vendored JSON. Only the
    /// converter needs it.
    /// </summary>
    internal bool WasJsonNumber { get; }

    /// <summary>A fixed count, as read from a JSON number.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static MultiattackCount FromNumber(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return new MultiattackCount(value.ToString(CultureInfo.InvariantCulture), value, wasJsonNumber: true);
    }

    /// <summary>A count read from a JSON string; digits become a fixed count, anything else is kept as text.</summary>
    /// <exception cref="FormatException"><paramref name="text"/> is empty, or all digits but too large for an int.</exception>
    public static MultiattackCount FromText(string text) =>
        TryCreate(text, out var count, out var error) ? count : throw new FormatException(error);

    /// <summary>The shared parsing rule for <see cref="FromText"/> and the converter, so both reject the same inputs.</summary>
    internal static bool TryCreate(
        string text,
        [NotNullWhen(true)] out MultiattackCount? count,
        [NotNullWhen(false)] out string? error)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            count = null;
            error = "A multiattack count cannot be an empty string; expected e.g. \"2\", \"Number of Heads\" or \"1d4\".";
            return false;
        }

        if (!trimmed.All(char.IsAsciiDigit))
        {
            count = new MultiattackCount(text, null, wasJsonNumber: false);
            error = null;
            return true;
        }

        if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            count = null;
            error = $"The multiattack count \"{text}\" is too large.";
            return false;
        }

        count = new MultiattackCount(text, value, wasJsonNumber: false);
        error = null;
        return true;
    }

    public override string ToString() => Text;
}

/// <summary>
/// One step of a multiattack's fixed routine (<c>actions[]</c>): use <see cref="ActionName"/>
/// <see cref="Count"/> times.
///
/// <para>
/// <see cref="ActionName"/> does not always match an action on the same monster. 2014 has 6 mismatches (dragon
/// turtle "Claws", vampire form-qualified names). 2024 has 21: 20 name a spell inside a Spellcasting action
/// ("Scorching Ray"), and one names erinyes "Entangling Rope" where the action is "Entangling Rope (Requires Magic
/// Rope)". Resolution must be spell-aware and fuzzy (Phase 5).
/// </para>
/// </summary>
public sealed class MultiattackAction
{
    public required string ActionName { get; init; }

    public required MultiattackCount Count { get; init; }

    /// <summary>2014: <c>melee</c> / <c>ranged</c> / <c>ability</c>; 2024: <c>melee</c> / <c>special</c>.</summary>
    public required string Type { get; init; }
}

/// <summary>
/// One option in a multiattack's <c>action_options</c> choice. It is either a single action (<c>option_type</c>
/// <c>"action"</c>) or a bundle of actions taken together (<c>"multiple"</c>, with <see cref="Items"/>).
///
/// <para>
/// When a monster has both <c>actions[]</c> and <c>action_options</c> (41 multiattacks in 2024, none in 2014), the
/// options REPLACE part of the routine; they are not an alternative to it. The red dragon makes 2 Rend attacks plus 1
/// chosen from {Rend, Scorching Ray}, for 3 in total. Reading the options as "instead of <c>actions[]</c>" undercounts
/// the attacks.
/// </para>
/// </summary>
public sealed class MultiattackOption
{
    /// <summary><see cref="MultiattackOptionTypes"/>: <c>action</c> or <c>multiple</c>.</summary>
    public required string OptionType { get; init; }

    /// <summary>Set when <see cref="OptionType"/> is <c>action</c> (and on every item of a <c>multiple</c>).</summary>
    public string? ActionName { get; init; }

    public MultiattackCount? Count { get; init; }

    public string? Type { get; init; }

    /// <summary>A condition on the option (2014 oni: <c>"If in Oni form"</c>).</summary>
    public string? Desc { get; init; }

    /// <summary>The actions taken together when <see cref="OptionType"/> is <c>multiple</c>.</summary>
    public IReadOnlyList<MultiattackOption>? Items { get; init; }
}

/// <summary>
/// <see cref="MultiattackOption.OptionType"/> values. A reader that compares the literals instead would silently read
/// a bundle ("one Bite and one Claw") as one use of an action with no name.
/// </summary>
public static class MultiattackOptionTypes
{
    /// <summary>One action: <see cref="MultiattackOption.ActionName"/>, <see cref="MultiattackOption.Count"/> times.</summary>
    public const string Action = "action";

    /// <summary>A bundle of actions taken together: <see cref="MultiattackOption.Items"/>.</summary>
    public const string Multiple = "multiple";
}

/// <summary>
/// <c>multiattack_type</c> values. The field alone does not describe the routine. The 41 2024 multiattacks that
/// have both lists are all typed <see cref="Actions"/>, so a consumer that switches on this field and reads only the
/// matching list drops their <c>action_options</c>. Read whichever lists are present.
/// </summary>
public static class MultiattackTypes
{
    public const string Actions = "actions";
    public const string ActionOptions = "action_options";
}
