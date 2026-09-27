using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Features;

/// <summary>One step of a step value: <see cref="Value"/> holds from <see cref="Level"/> on.</summary>
public readonly record struct LevelStep<T>(int Level, T Value);

/// <summary>
/// A DSL field whose value depends on character level ("LV" in the contract): a scalar, or a step map
/// <c>{"1": 1, "5": 2, "11": 3}</c> whose value at level L is the one under the largest key ≤ L.
///
/// <para>
/// <b>Why step maps</b>: one build must describe a character across levels 1–20 for the level curves (Extra Attack at 5,
/// an ASI at 4 and 8, a cantrip's dice), and a list of 20 separate builds would be both unreadable and unchecked. A
/// scalar is a one-step map from level 1, so every consumer reads both the same way through <see cref="At"/>.
/// </para>
/// <para>
/// A step map need not start at level 1 (a feature gained at 5 may simply be given from 5), but then it has no value
/// below its first key; the validator refuses any level where the owning attack or modifier is active and no key covers
/// it, naming the field, the level and the first key. <see cref="At"/> after validation therefore never fails; if it does,
/// that is a bug, and it throws <see cref="InvalidOperationException"/> rather than a message for the model.
/// </para>
/// </summary>
public sealed class LevelValue<T>
{
    private readonly LevelStep<T>[] _steps;

    internal LevelValue(IEnumerable<LevelStep<T>> steps, bool isStepMap)
    {
        _steps = steps.OrderBy(s => s.Level).ToArray();
        if (_steps.Length == 0)
        {
            throw new ArgumentException("A step value needs at least one step.", nameof(steps));
        }

        IsStepMap = isStepMap;
    }

    /// <summary>The steps, lowest level first. A scalar is one step at level 1.</summary>
    public IReadOnlyList<LevelStep<T>> Steps => _steps;

    /// <summary>Whether it was written as a step map (even a one-key one) rather than a scalar.</summary>
    public bool IsStepMap { get; }

    /// <summary>The lowest level with a value.</summary>
    public int FirstLevel => _steps[0].Level;

    /// <summary>
    /// Whether the value changes with level: two or more steps. One of the tests for "the build scales with level"
    /// (<see cref="BuildResolver.ScalesWithLevel"/>).
    /// </summary>
    public bool Scales => _steps.Length >= 2;

    /// <summary>A scalar: the same value at every level.</summary>
    public static LevelValue<T> Of(T value) => new([new LevelStep<T>(DslLimits.MinLevel, value)], isStepMap: false);

    /// <summary>A step map from (level, value) pairs.</summary>
    public static LevelValue<T> StepMap(params (int Level, T Value)[] steps) =>
        new(steps.Select(s => new LevelStep<T>(s.Level, s.Value)), isStepMap: true);

    /// <summary>The value at <paramref name="level"/>, or false when the first step is above it.</summary>
    public bool TryAt(int level, out T value)
    {
        for (var i = _steps.Length - 1; i >= 0; i--)
        {
            if (_steps[i].Level <= level)
            {
                value = _steps[i].Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>The value at <paramref name="level"/>. Validation guarantees one exists wherever the owner is active.</summary>
    /// <exception cref="InvalidOperationException">No step covers the level (a validation bug).</exception>
    public T At(int level) =>
        TryAt(level, out var value)
            ? value
            : throw new InvalidOperationException($"The step value has no value at level {level}; its first step is level {FirstLevel}.");

    /// <summary>The same steps with each value mapped.</summary>
    public LevelValue<TOut> Select<TOut>(Func<T, TOut> map) =>
        new(_steps.Select(s => new LevelStep<TOut>(s.Level, map(s.Value))), IsStepMap);
}

/// <summary>
/// Parsing of step values from what the binder produced (a <see cref="JsonElement"/>) or from C# values (presets, tests),
/// with one message style: the field first, then what was wrong, then what is accepted with an example. The item prefix
/// ("attacks item 2 (Greatsword): ") is added by the validator that knows the item.
/// </summary>
public static class LevelValue
{
    /// <summary>The example step map used in messages when a field has no better one.</summary>
    public const string GenericExample = "{\"1\": 1, \"5\": 2}";

    /// <summary>True when a raw field was given (not null, not a JSON null).</summary>
    public static bool IsGiven(object? raw) => raw is not null && raw is not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined };

    /// <summary>
    /// The raw field as a <see cref="JsonElement"/>: as bound, or a C# value (int, string, dictionary) serialised with
    /// <see cref="DslJson.Options"/> so presets and tests go through exactly the path a model's JSON does.
    /// </summary>
    public static JsonElement? ToElement(object? raw) => raw switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        JsonElement element => element,
        _ => JsonSerializer.SerializeToElement(raw, raw.GetType(), DslJson.Options),
    };

    /// <summary>
    /// A step value: null when not given, otherwise a scalar or a step map whose values <paramref name="scalar"/> reads.
    /// </summary>
    /// <param name="field">The field as messages name it ("count", "abilities cha").</param>
    /// <param name="scalar">
    /// Reads one value; its second argument is the field text to use in its own message, and its third is true inside a
    /// step map (where some fields accept fewer forms).
    /// </param>
    /// <param name="example">A step map example suited to the field, e.g. {"1": 16, "4": 18}.</param>
    /// <exception cref="DndInputException">A malformed key, a duplicate level, an empty map, or a bad value.</exception>
    public static LevelValue<T>? Parse<T>(object? raw, string field, Func<JsonElement, string, bool, T> scalar, string example = GenericExample)
    {
        if (ToElement(raw) is not { } element)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return LevelValue<T>.Of(scalar(element, field, false));
        }

        var steps = new List<LevelStep<T>>();
        foreach (var property in element.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var level) ||
                level.ToString(CultureInfo.InvariantCulture) != property.Name ||
                level is < DslLimits.MinLevel or > DslLimits.MaxLevel)
            {
                throw new DndInputException(
                    $"{field}: step map key \"{DslText.Echo(property.Name)}\" is not a level; keys are levels {DslLimits.MinLevel} to " +
                    $"{DslLimits.MaxLevel} written as whole numbers, e.g. {example}.");
            }

            if (steps.Any(s => s.Level == level))
            {
                throw new DndInputException($"{field}: step map gives level {level} twice; give each level once, e.g. {example}.");
            }

            steps.Add(new LevelStep<T>(level, scalar(property.Value, $"{field} at level {property.Name}", true)));
        }

        if (steps.Count == 0)
        {
            throw new DndInputException($"{field}: step map is empty; give a value, or levels as keys, e.g. {example}.");
        }

        return new LevelValue<T>(steps, isStepMap: true);
    }

    /// <summary>A whole number in [min, max], or a step map of them.</summary>
    public static LevelValue<int>? ParseInt(object? raw, string field, int min, int max, string example = GenericExample) =>
        Parse(raw, field, (element, where, inMap) => ReadInt(element, where, min, max, inMap ? null : example), example);

    /// <summary>A damage formula (<see cref="DamageFormula.ParseDamage"/>) or a whole number, or a step map of them.</summary>
    public static LevelValue<DamageFormula>? ParseDamage(object? raw, string field, string example = "{\"1\": \"1d8\", \"5\": \"2d8\"}") =>
        Parse(raw, field, (element, where, inMap) => ReadDamage(element, where, inMap ? null : example), example);

    /// <summary>Bonus dice (<see cref="DamageFormula.ParseBonusDice"/>), or a step map of them.</summary>
    public static LevelValue<DamageFormula>? ParseBonusDice(object? raw, string field, string flatHint) =>
        Parse(raw, field, (element, where, _) => element.ValueKind == JsonValueKind.String
            ? DamageFormula.ParseBonusDice(element.GetString(), where, flatHint)
            : throw new DndInputException(
                $"{where} must be dice such as \"1d4\" (Bless) or \"-1d4\" (Bane), but was {DslText.Describe(element)}."),
            "{\"1\": \"1d4\", \"10\": \"1d6\"}");

    /// <summary>
    /// An amount: a whole number −100…100, "pb", or an ability key (its modifier); a step map holds whole numbers only.
    /// </summary>
    public static LevelValue<DslAmount>? ParseAmount(object? raw, string field) =>
        Parse(raw, field, ReadAmount);

    /// <summary>
    /// The message for a level where a step value has no value: names the field, the level and the first key, and says
    /// how to fix it.
    /// </summary>
    /// <param name="owner">"attack" or "modifier" when the owner takes from_level (abilities do not: null).</param>
    public static string MissingAt(string field, int level, int firstLevel, string? owner) =>
        $"{field} has no value at level {level}: its step map starts at level {firstLevel}. Add a key \"{level}\" or lower " +
        (owner is null ? "(e.g. \"1\")." : $"(e.g. \"1\"), or give the {owner} from_level {firstLevel}.");

    private static int ReadInt(JsonElement element, string field, int min, int max, string? mapExample)
    {
        if (!TryReadWholeNumber(element, out var value))
        {
            var orMap = mapExample is null ? string.Empty : $", or a step map by level such as {mapExample}";
            throw new DndInputException(Invariant($"{field} must be a whole number {min} to {max}{orMap}, but was {DslText.Describe(element)}."));
        }

        if (value < min || value > max)
        {
            throw new DndInputException(Invariant($"{field} is {value}; it is {min} to {max}."));
        }

        return (int)value;
    }

    private static DamageFormula ReadDamage(JsonElement element, string field, string? mapExample)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return DamageFormula.ParseDamage(element.GetString(), field);
        }

        if (TryReadWholeNumber(element, out var value))
        {
            if (Math.Abs(value) > DslLimits.MaxFlat)
            {
                throw new DndInputException(Invariant($"{field} is {value}; a whole number here is -{DslLimits.MaxFlat} to {DslLimits.MaxFlat}."));
            }

            return DamageFormula.Constant((int)value);
        }

        var orMap = mapExample is null ? string.Empty : $", or a step map by level such as {mapExample}";
        throw new DndInputException(
            $"{field} must be dice such as \"2d6\" or \"1d8+1\"{orMap}, but was {DslText.Describe(element)}.");
    }

    private static DslAmount ReadAmount(JsonElement element, string field, bool inMap)
    {
        const string Forms = "a whole number -100 to 100, \"pb\" (proficiency bonus), an ability such as \"cha\" (its modifier), or a step map of whole numbers such as {\"1\": 1, \"5\": 2}";
        if (TryReadWholeNumber(element, out var value))
        {
            if (Math.Abs(value) > DslLimits.MaxFlat)
            {
                throw new DndInputException(Invariant($"{field} is {value}; an amount is -{DslLimits.MaxFlat} to {DslLimits.MaxFlat}."));
            }

            return DslAmount.Of((int)value);
        }

        if (inMap)
        {
            throw new DndInputException(
                $"{field} must be a whole number -{DslLimits.MaxFlat} to {DslLimits.MaxFlat}, but was {DslText.Describe(element)}: a " +
                "step map holds numbers only; for \"pb\" or an ability from some level on, give it as a scalar with from_level.");
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString() ?? string.Empty;
            if (DslValueSet.Key(text) == DslAmount.ProficiencyBonusKeyword ||
                DslValueSet.Key(text) == "proficiencybonus")
            {
                return DslAmount.ProficiencyBonus;
            }

            if (DslValues.Abilities.Set.TryMatch(text, out var ability))
            {
                return DslAmount.AbilityModifier(ability);
            }

            throw new DndInputException($"{field} \"{DslText.Echo(text)}\" is not an amount; give {Forms}.");
        }

        throw new DndInputException($"{field} must be {Forms}, but was {DslText.Describe(element)}.");
    }

    // Messages quote numbers the model sent, which may be negative: never with the host culture's minus sign.
    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>
    /// A JSON number written as a whole number, or a string holding one: the host binds with numbers readable from
    /// strings, so "2" works wherever 2 does. "2.0" and "1e1" are refused, as the host's integer fields refuse them.
    /// </summary>
    private static bool TryReadWholeNumber(JsonElement element, out long value)
    {
        value = 0;
        var text = element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => element.GetString()?.Trim(),
            _ => null,
        };

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var digits = text.AsSpan(text[0] is '-' or '+' ? 1 : 0);
        return digits.Length is > 0 and <= 18 && !digits.ContainsAnyExceptInRange('0', '9') &&
               long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
