using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;

namespace DndMcp.Hosting;

/// <summary>
/// Checks a call's top-level arguments against the tool's own input schema before the tool runs.
///
/// <para>
/// Without this, the two most common model mistakes — leaving out a required argument, or sending a
/// string where a number belongs — fail inside the SDK's argument binding with an ArgumentException or
/// JsonException. The SDK reports those as the bare "An error occurred invoking '&lt;tool&gt;'.", which gives
/// the model nothing to correct. Validating against the schema the model was shown turns them into a
/// message that names the argument and lists what the tool accepts.
/// </para>
/// <para>
/// Deliberately shallow: required-ness and the JSON type of each top-level argument, plus the type of each item of an
/// array argument (<c>kinds: ["spell", 3]</c> fails in the binder just as a wrong top-level type does). <b>Nested objects
/// are not checked yet</b>, and FluentValidation in Domain cannot stand in for that: a malformed object fails in the SDK's
/// binder before any validator runs, and the model gets the bare generic error. The first object parameter (Phase 4
/// builds, Phase 6 campaign operations) needs this guard to test-deserialize the argument with <c>McpJson.Options</c> and
/// report the <c>JsonException.Path</c>; only then can FluentValidation's rules reach the model.
/// </para>
/// <para>
/// Whatever this accepts, the SDK's binder must also accept — anything accepted here and refused there
/// falls through to the generic message again. Two gaps are closed for that reason. Numeric strings are
/// held to the text System.Text.Json reads, which is stricter than .NET's own parsers (see
/// <see cref="IsIntegerText"/>). And when the tool's method is known, an integer must fit the parameter's
/// CLR type, because the schema says only "integer" for int and long alike. ToolErrorTests pins the dice
/// cases and ArgumentBindingAgreementTests pins number and renamed parameters; the reverse direction
/// (refusing what the binder would take) only costs the model a retry and is allowed for "NaN"/"Infinity".
/// </para>
/// </summary>
internal static class ToolArgumentGuard
{
    private static readonly SearchValues<char> NumberCharacters = SearchValues.Create("0123456789+-.eE");

    private const int MaxItemProblems = 5;

    private static readonly Dictionary<Type, (decimal Min, decimal Max)> IntegerRanges = new()
    {
        [typeof(sbyte)] = (sbyte.MinValue, sbyte.MaxValue),
        [typeof(byte)] = (byte.MinValue, byte.MaxValue),
        [typeof(short)] = (short.MinValue, short.MaxValue),
        [typeof(ushort)] = (ushort.MinValue, ushort.MaxValue),
        [typeof(int)] = (int.MinValue, int.MaxValue),
        [typeof(uint)] = (uint.MinValue, uint.MaxValue),
        [typeof(long)] = (long.MinValue, long.MaxValue),
        [typeof(ulong)] = (ulong.MinValue, ulong.MaxValue),
    };

    /// <param name="method">
    /// The tool's C# method (the SDK puts it first in <c>McpServerTool.Metadata</c>), used only for the
    /// integer range check. Null skips that check rather than guessing a range.
    /// </param>
    public static void Validate(string toolName, JsonElement inputSchema, IDictionary<string, JsonElement>? arguments, MethodInfo? method = null)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object ||
            !inputSchema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var problems = new List<string>();

        if (inputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()).OfType<string>())
            {
                if (arguments is null || !arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Undefined)
                {
                    problems.Add($"missing required argument '{name}'");
                }
            }
        }

        if (arguments is not null)
        {
            var parameterTypes = ParameterTypesBySchemaName(method);

            foreach (var (name, value) in arguments)
            {
                if (!properties.TryGetProperty(name, out var property))
                {
                    // Truncated: the name is the model's text, and a pasted page as a key must not come back whole.
                    problems.Add($"unknown argument '{Truncate(name)}'");
                    continue;
                }

                var allowed = AllowedTypes(property);
                if (allowed.Count > 0 && !allowed.Any(type => Matches(type, value)))
                {
                    problems.Add($"argument '{name}' should be {string.Join(" or ", allowed)} but was {Describe(value)}");
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Array)
                {
                    problems.AddRange(ItemProblems(name, property, value));
                    continue;
                }

                if (parameterTypes is not null &&
                    parameterTypes.TryGetValue(name, out var clrType) &&
                    IntegerRanges.TryGetValue(clrType, out var range) &&
                    IsIntegerShaped(value) &&
                    OutOfRange(value, range) is { } direction)
                {
                    // No CLR bounds in the text: "-2147483648 to 2147483647" reads as the tool's accepted range and
                    // invites a second failing call when the real range (e.g. times 1-100) is far narrower.
                    problems.Add($"argument '{name}' was {Describe(value)}, which is too {direction} to be valid");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new McpException(
                $"Invalid arguments: {string.Join("; ", problems)}. {toolName} accepts: {DescribeParameters(inputSchema, properties)}.");
        }
    }

    /// <summary>
    /// One problem per array item whose JSON type the schema's <c>items</c> does not allow, numbered from 1 as a person
    /// counts, and at most <see cref="MaxItemProblems"/> of them so a long wrong array cannot bury the parameter list.
    /// </summary>
    /// <remarks>
    /// Every item is held to the same type rules as a top-level value (numeric strings for integers, and so on), because
    /// the binder reads each item with the same System.Text.Json converter it uses for a scalar parameter. The integer
    /// range check is not applied to items: no array parameter has integer items yet, and the first one needs the element
    /// type looked up the way <see cref="ParameterTypesBySchemaName"/> looks up a parameter's.
    /// </remarks>
    private static IEnumerable<string> ItemProblems(string name, JsonElement property, JsonElement array)
    {
        if (!property.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var allowed = AllowedTypes(items);
        if (allowed.Count == 0)
        {
            yield break;
        }

        var position = 0;
        var reported = 0;
        var unreported = 0;
        foreach (var item in array.EnumerateArray())
        {
            position++;
            if (allowed.Any(type => Matches(type, item)))
            {
                continue;
            }

            if (reported == MaxItemProblems)
            {
                unreported++;
                continue;
            }

            reported++;

            // "string", not "string or null": the schema allows null items (nullable reference types), but a null kind is
            // never what the caller meant, and naming it would invite one.
            var expected = string.Join(" or ", allowed.Where(t => t != "null").DefaultIfEmpty("null"));
            yield return $"argument '{name}' item {position.ToString(CultureInfo.InvariantCulture)} should be {expected} but was {Describe(item)}";
        }

        if (unreported > 0)
        {
            yield return $"argument '{name}' has {unreported.ToString(CultureInfo.InvariantCulture)} more item(s) of the wrong type";
        }
    }

    private static List<string> AllowedTypes(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return [];
        }

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => type.EnumerateArray().Select(t => t.GetString()).OfType<string>().ToList(),
            _ => [],
        };
    }

    // Numbers sent as strings ("3") are accepted because the SDK's options set AllowReadingFromString and
    // would bind them fine; rejecting them here would refuse calls that work.
    private const NumberStyles IntegerStyles = NumberStyles.AllowLeadingSign;
    private const NumberStyles NumberStylesForStrings = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    // An integer too large for any CLR type still matches "integer": it is the right kind of value, and the range check
    // below then says "too large", which is the true problem. Refusing it here said "should be integer" about a number
    // like 99999999999999999999, which the model cannot act on. The range check needs the tool's MethodInfo; the SDK
    // always supplies it, and without it an oversized integer would reach the binder's generic error.
    private static bool Matches(string type, JsonElement value) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => IsIntegerShaped(value),
        "number" => value.ValueKind == JsonValueKind.Number ||
                    (value.ValueKind == JsonValueKind.String && IsNumberText(value.GetString())),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    /// <summary>A JSON number or numeric string written as a whole number: digits with an optional sign.</summary>
    private static bool IsIntegerShaped(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => IsIntegerText(value.GetRawText()),
        JsonValueKind.String => IsIntegerText(value.GetString()),
        _ => false,
    };

    /// <summary>
    /// "small" or "large" when an integer-shaped value does not fit <paramref name="range"/>, else null. A value too
    /// big even for decimal (about 29 digits) is out of every range; its sign says which way.
    /// </summary>
    private static string? OutOfRange(JsonElement value, (decimal Min, decimal Max) range)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        if (!decimal.TryParse(text, IntegerStyles, CultureInfo.InvariantCulture, out var number))
        {
            return text.StartsWith('-') ? "small" : "large";
        }

        return number < range.Min ? "small" : number > range.Max ? "large" : null;
    }

    /// <summary>
    /// An optional sign, then ASCII digits, and nothing else — the integer text System.Text.Json reads.
    /// </summary>
    /// <remarks>
    /// long.TryParse alone is not enough: it ignores trailing NUL characters, so "3\u0000" would pass here
    /// and then fail in the binder with the bare generic error. Whitespace, other Unicode digits and
    /// exponents ("1e2") are refused by both, but spelling the rule out keeps it from depending on that.
    /// </remarks>
    private static bool IsIntegerText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var digits = text.AsSpan(text[0] is '+' or '-' ? 1 : 0);
        return digits.Length > 0 && !digits.ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>
    /// A finite number written with ASCII digits, a sign, '.' and an exponent only.
    /// </summary>
    /// <remarks>
    /// double.TryParse is looser than the binder in three ways, each a bare generic error if let through:
    /// trailing NULs ("2.5\u0000"), case-insensitive "nan"/"infinity", and overflow to infinity ("1e400").
    /// Within this character set, and finite, the two agree (checked case by case against
    /// JsonSerializer with the server's options).
    /// </remarks>
    private static bool IsNumberText(string? text) =>
        !string.IsNullOrEmpty(text) &&
        !text.AsSpan().ContainsAnyExcept(NumberCharacters) &&
        double.TryParse(text, NumberStylesForStrings, CultureInfo.InvariantCulture, out var number) &&
        double.IsFinite(number);

    /// <summary>
    /// C# parameter types keyed by the name the model sees in the schema, for the integer range check.
    /// </summary>
    /// <remarks>
    /// [AIParameterName] renames a parameter in both the schema and the binder. Keying by the C# name would
    /// miss every renamed parameter and silently skip its range check, and an out-of-range value would reach
    /// the model as the bare generic error again.
    /// </remarks>
    private static Dictionary<string, Type>? ParameterTypesBySchemaName(MethodInfo? method)
    {
        if (method is null)
        {
            return null;
        }

        var types = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var parameter in method.GetParameters())
        {
            if (SchemaName(parameter) is { } name)
            {
                types.TryAdd(name, Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType);
            }
        }

        return types;
    }

    // MEAI001: the attribute is experimental in Microsoft.Extensions.AI 10.8.3. If it is renamed or removed,
    // the SDK's parameter naming changes with it, and a build break here is the right signal.
#pragma warning disable MEAI001
    private static string? SchemaName(ParameterInfo parameter) =>
        parameter.GetCustomAttribute<AIParameterNameAttribute>()?.Name ?? parameter.Name;
#pragma warning restore MEAI001

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string \"{Truncate(value.GetString())}\"",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => $"the boolean {value.GetRawText()}",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => value.ValueKind.ToString(),
    };

    private static string DescribeParameters(JsonElement schema, JsonElement properties)
    {
        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet()
            : [];

        return string.Join(", ", properties.EnumerateObject().Select(p =>
        {
            var typeText = TypeText(p.Value);
            if (typeText == "array" && p.Value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object &&
                TypeText(items) is var itemText and not "any")
            {
                typeText = $"array of {itemText}";
            }

            return $"{p.Name} ({typeText}, {(required.Contains(p.Name) ? "required" : "optional")})";
        }));
    }

    private static string TypeText(JsonElement schema)
    {
        var types = AllowedTypes(schema).Where(t => t != "null").ToList();
        return types.Count == 0 ? "any" : string.Join("|", types);
    }

    private static string Truncate(string? text) =>
        text is null ? string.Empty : text.Length <= 40 ? text : text[..40] + "…";
}
