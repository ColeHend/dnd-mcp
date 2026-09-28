using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
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
/// It checks required-ness and the JSON type of each top-level argument, the type of each item of an array argument
/// (<c>kinds: ["spell", 3]</c> fails in the binder just as a wrong top-level type does), and, inside object arguments and
/// object items (<c>encounter_difficulty</c>'s <c>monsters</c>), every field against the schema: unknown fields, wrong
/// types, integers out of the property's CLR range, recursively. A malformed object otherwise fails in the SDK's binder
/// before any tool code runs, and the model gets the bare generic error. <b>Unknown fields are refused</b>, although
/// System.Text.Json would ignore them: <c>{"monster": "Ogre", "qty": 3}</c> would bind as an empty entry with count 1,
/// and a misspelt count silently becomes 1. Last, any object argument that passed is test-deserialized with
/// <c>McpJson.Options</c>, so whatever the schema cannot express still comes back as a message naming the field (from
/// <c>JsonException.Path</c>), not the generic error.
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
/// <para>
/// A parameter marked <see cref="SameShapeAsAttribute"/> is published untyped (its schema would repeat another
/// parameter's) and checked here exactly as that parameter is: against its schema's fields, then test-deserialized as its
/// CLR type; when that parameter is an array (<c>balance_simulate</c>'s <c>enemies</c>, shaped like <c>party</c>), item by
/// item. A parameter marked <see cref="CheckedAsAttribute"/> is published untyped too and checked the same way against the
/// schema of the type it names, generated with the server's JSON options. Without either, an untyped value would reach
/// the tool unchecked, and a misspelt field in it would be ignored.
/// </para>
/// </summary>
internal static partial class ToolArgumentGuard
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
    /// The tool's C# method (the SDK puts it first in <c>McpServerTool.Metadata</c>). Its parameter types give the
    /// integer ranges (top level, array items and object fields), the field and element types nested checks recurse
    /// with, and the type the test-deserialize backstop binds. Null skips those three checks rather than guessing; the
    /// schema checks still run. The SDK always supplies it.
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
        var sameShapes = SameShapeParameters(method);
        var checkedAs = CheckedAsParameters(method);

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

                if (sameShapes?.GetValueOrDefault(name) is { } shapeOf &&
                    properties.TryGetProperty(shapeOf, out var shapeSchema) &&
                    parameterTypes?.GetValueOrDefault(shapeOf) is { } shapeType)
                {
                    problems.AddRange(SameShapeProblems(name, value, shapeSchema, shapeType));
                    continue;
                }

                if (checkedAs?.GetValueOrDefault(name) is { } checkedType)
                {
                    problems.AddRange(SameShapeProblems(name, value, SchemaOf(checkedType), checkedType));
                    continue;
                }

                var allowed = AllowedTypes(property);
                if (allowed.Count > 0 && !allowed.Any(type => Matches(type, value)))
                {
                    problems.Add($"argument '{name}' should be {string.Join(" or ", allowed)} but was {Describe(value)}");
                    continue;
                }

                var clrType = parameterTypes?.GetValueOrDefault(name);
                if (value.ValueKind == JsonValueKind.Array)
                {
                    var itemProblems = ItemProblems(name, property, value).ToList();
                    if (itemProblems.Count == 0)
                    {
                        itemProblems = Capped(name, ItemValueProblems(name, property, value, ElementType(clrType)));
                    }

                    problems.AddRange(itemProblems.Count > 0 ? itemProblems : BindingProblems(name, value, clrType));
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Object)
                {
                    var fieldProblems = Capped(name, FieldProblems($"argument '{name}'", property, value, clrType));
                    problems.AddRange(fieldProblems.Count > 0 ? fieldProblems : BindingProblems(name, value, clrType));
                    continue;
                }

                if (clrType is not null &&
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
                $"Invalid arguments: {string.Join("; ", problems)}. {toolName} accepts: {DescribeParameters(inputSchema, properties, UntypedSchemas(properties, sameShapes, checkedAs))}.");
        }
    }

    /// <summary>
    /// One problem per array item whose JSON type the schema's <c>items</c> does not allow, numbered from 1 as a person
    /// counts, and at most <see cref="MaxItemProblems"/> of them so a long wrong array cannot bury the parameter list.
    /// </summary>
    /// <remarks>
    /// Every item is held to the same type rules as a top-level value (numeric strings for integers, and so on), because
    /// the binder reads each item with the same System.Text.Json converter it uses for a scalar parameter. Ranges and the
    /// fields of object items are checked afterwards, by <see cref="ItemValueProblems"/>, once every item has the right
    /// type.
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

    /// <summary>
    /// What is wrong inside each item of an array argument whose items are all the right JSON type: an integer outside
    /// the element's CLR range (<c>party: [5, 99999999999]</c>), and every field problem of an object item ("argument
    /// 'monsters' item 2 field 'count' should be integer but was the string \"three\""), in item order.
    /// </summary>
    private static IEnumerable<string> ItemValueProblems(string name, JsonElement property, JsonElement array, Type? itemType)
    {
        if (!property.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var position = 0;
        foreach (var item in array.EnumerateArray())
        {
            position++;
            foreach (var problem in ValueProblems($"argument '{name}' item {position.ToString(CultureInfo.InvariantCulture)}", items, item, itemType))
            {
                yield return problem;
            }
        }
    }

    /// <summary>
    /// Every problem with the fields of <paramref name="value"/>, an object checked against <paramref name="schema"/>:
    /// fields the schema does not list, missing required fields, wrong JSON types (as for a top-level value, without
    /// "or null" in the message), integers outside the CLR property's range, and the same again inside nested objects
    /// and arrays. <paramref name="clrType"/> supplies the ranges; null skips only that check.
    /// </summary>
    private static IEnumerable<string> FieldProblems(string label, JsonElement schema, JsonElement value, Type? clrType)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var fieldTypes = JsonPropertyTypes(clrType);
        foreach (var (field, fieldValue) in value.EnumerateObject().Select(p => (p.Name, p.Value)))
        {
            if (!properties.TryGetProperty(field, out var fieldSchema))
            {
                var known = string.Join(", ", properties.EnumerateObject().Select(p => p.Name));
                yield return $"{label} has unknown field '{Truncate(field)}' (fields: {known})";
                continue;
            }

            foreach (var problem in ValueProblems($"{label} field '{field}'", fieldSchema, fieldValue, fieldTypes?.GetValueOrDefault(field)))
            {
                yield return problem;
            }
        }

        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in required.EnumerateArray().Select(r => r.GetString()).OfType<string>())
            {
                if (!value.TryGetProperty(field, out _))
                {
                    yield return $"{label} is missing required field '{field}'";
                }
            }
        }
    }

    private static IEnumerable<string> ValueProblems(string label, JsonElement schema, JsonElement value, Type? clrType)
    {
        var allowed = AllowedTypes(schema);
        if (allowed.Count > 0 && !allowed.Any(type => Matches(type, value)))
        {
            yield return $"{label} should be {string.Join(" or ", allowed.Where(t => t != "null").DefaultIfEmpty("null"))} but was {Describe(value)}";
            yield break;
        }

        var underlying = clrType is null ? null : Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (underlying is not null && IntegerRanges.TryGetValue(underlying, out var range) && IsIntegerShaped(value) &&
            OutOfRange(value, range) is { } direction)
        {
            yield return $"{label} was {Describe(value)}, which is too {direction} to be valid";
            yield break;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var problem in FieldProblems(label, schema, value, clrType))
            {
                yield return problem;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            var position = 0;
            foreach (var item in value.EnumerateArray())
            {
                position++;
                foreach (var problem in ValueProblems($"{label} item {position.ToString(CultureInfo.InvariantCulture)}", items, item, ElementType(clrType)))
                {
                    yield return problem;
                }
            }
        }
    }

    /// <summary>
    /// An argument published untyped that must have another parameter's shape (<see cref="SameShapeAsAttribute"/>): null,
    /// or an object whose fields pass that parameter's schema and which binds as that parameter's CLR type, or — when the
    /// other parameter is an array (<c>balance_simulate</c>'s <c>enemies</c>, shaped like <c>party</c>) — an array whose
    /// items pass that parameter's item schema and which binds as its CLR type, item problems counted as for a typed array.
    /// </summary>
    private static IEnumerable<string> SameShapeProblems(string name, JsonElement value, JsonElement shapeSchema, Type shapeType)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (AllowedTypes(shapeSchema).Contains("array"))
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                return [$"argument '{name}' should be array but was {Describe(value)}"];
            }

            var itemProblems = ItemProblems(name, shapeSchema, value).ToList();
            if (itemProblems.Count == 0)
            {
                itemProblems = Capped(name, ItemValueProblems(name, shapeSchema, value, ElementType(shapeType)));
            }

            return itemProblems.Count > 0 ? itemProblems : BindingProblems(name, value, shapeType).ToList();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return [$"argument '{name}' should be object but was {Describe(value)}"];
        }

        var fieldProblems = Capped(name, FieldProblems($"argument '{name}'", shapeSchema, value, shapeType));
        return fieldProblems.Count > 0 ? fieldProblems : BindingProblems(name, value, shapeType).ToList();
    }

    /// <summary>
    /// At most <see cref="MaxItemProblems"/> problems for one argument, then a count of the rest, so one badly wrong
    /// array cannot bury the parameter list the message ends with.
    /// </summary>
    private static List<string> Capped(string name, IEnumerable<string> problems)
    {
        var all = problems.ToList();
        if (all.Count <= MaxItemProblems)
        {
            return all;
        }

        var rest = all.Count - MaxItemProblems;
        return [.. all.Take(MaxItemProblems), $"argument '{name}' has {rest.ToString(CultureInfo.InvariantCulture)} more problem(s)"];
    }

    /// <summary>
    /// The backstop for object and array arguments the schema checks passed: deserialize with the options the SDK binds
    /// with, and report where it failed ("argument 'monsters' item 1 field 'count' could not be read"). Everything the
    /// schema can express is caught above with a better message; this keeps the rest off the generic error.
    /// </summary>
    private static IEnumerable<string> BindingProblems(string name, JsonElement value, Type? clrType)
    {
        if (clrType is null || clrType == typeof(JsonElement) || clrType == typeof(object))
        {
            yield break;
        }

        string? problem = null;
        try
        {
            JsonSerializer.Deserialize(value, clrType, McpJson.Options);
        }
        catch (JsonException ex)
        {
            problem = $"argument '{name}'{PathText(ex.Path)} could not be read as the tool expects";
        }

        if (problem is not null)
        {
            yield return problem;
        }
    }

    // "$[0].count" → " item 1 field 'count'"; "$" or null → "".
    private static string PathText(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (Match segment in PathSegment().Matches(path))
        {
            if (segment.Groups[1].Success)
            {
                text.Append(" item ").Append((int.Parse(segment.Groups[1].Value, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                var field = segment.Groups[2].Success ? segment.Groups[2].Value : segment.Groups[3].Value;
                text.Append(" field '").Append(Truncate(field)).Append('\'');
            }
        }

        return text.ToString();
    }

    // The JSON name → CLR type of each property the serializer reads, or null for a type with no properties (a scalar,
    // object, JsonElement): the same names and types the SDK's binder uses.
    private static Dictionary<string, Type>? JsonPropertyTypes(Type? type)
    {
        if (type is null || type == typeof(object) || type == typeof(string) || type == typeof(JsonElement) || type.IsPrimitive)
        {
            return null;
        }

        var info = McpJson.Options.GetTypeInfo(Nullable.GetUnderlyingType(type) ?? type);
        return info.Kind == JsonTypeInfoKind.Object
            ? info.Properties.ToDictionary(p => p.Name, p => p.PropertyType, StringComparer.Ordinal)
            : null;
    }

    private static Type? ElementType(Type? type)
    {
        if (type is null || type == typeof(string))
        {
            return null;
        }

        return type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];
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
    /// C# parameter types keyed by the name the model sees in the schema: for the integer range checks, the nested
    /// field and element types, and the backstop's target type.
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

    /// <summary>
    /// The parameters marked <see cref="SameShapeAsAttribute"/>, by schema name, each with the schema name of the parameter
    /// whose shape it has; null when the method has none.
    /// </summary>
    private static Dictionary<string, string>? SameShapeParameters(MethodInfo? method)
    {
        Dictionary<string, string>? shapes = null;
        foreach (var parameter in method?.GetParameters() ?? [])
        {
            if (parameter.GetCustomAttribute<SameShapeAsAttribute>() is { } attribute && SchemaName(parameter) is { } name)
            {
                (shapes ??= new Dictionary<string, string>(StringComparer.Ordinal))[name] = attribute.Parameter;
            }
        }

        return shapes;
    }

    /// <summary>The parameters marked <see cref="CheckedAsAttribute"/>, by schema name, each with its type; null when none.</summary>
    private static Dictionary<string, Type>? CheckedAsParameters(MethodInfo? method)
    {
        Dictionary<string, Type>? types = null;
        foreach (var parameter in method?.GetParameters() ?? [])
        {
            if (parameter.GetCustomAttribute<CheckedAsAttribute>() is { } attribute && SchemaName(parameter) is { } name)
            {
                (types ??= new Dictionary<string, Type>(StringComparer.Ordinal))[name] = attribute.Type;
            }
        }

        return types;
    }

    private static readonly ConcurrentDictionary<Type, JsonElement> Schemas = new();

    /// <summary>
    /// The JSON schema of a <see cref="CheckedAsAttribute"/> parameter's type, made by the generator and options the SDK
    /// makes typed parameters' schemas with, so the checks match what a typed parameter would get. Cached per type.
    /// </summary>
    private static JsonElement SchemaOf(Type type) =>
        Schemas.GetOrAdd(type, t => AIJsonUtilities.CreateJsonSchema(t, serializerOptions: McpJson.Options));

    /// <summary>
    /// The schema that describes each untyped parameter in the accepted-parameters list: the named parameter's for
    /// <see cref="SameShapeAsAttribute"/>, the type's for <see cref="CheckedAsAttribute"/>.
    /// </summary>
    private static Dictionary<string, JsonElement>? UntypedSchemas(
        JsonElement properties, Dictionary<string, string>? sameShapes, Dictionary<string, Type>? checkedAs)
    {
        if (sameShapes is null && checkedAs is null)
        {
            return null;
        }

        var schemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, shapeOf) in sameShapes ?? [])
        {
            if (properties.TryGetProperty(shapeOf, out var shape))
            {
                schemas[name] = shape;
            }
        }

        foreach (var (name, type) in checkedAs ?? [])
        {
            schemas[name] = SchemaOf(type);
        }

        return schemas;
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

    // An untyped parameter's schema is untyped on purpose (SameShapeAsAttribute, CheckedAsAttribute); the list says what it takes.
    private static string DescribeParameters(JsonElement schema, JsonElement properties, Dictionary<string, JsonElement>? untyped)
    {
        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToHashSet()
            : [];

        return string.Join(", ", properties.EnumerateObject().Select(p =>
        {
            // An untyped parameter is described by the schema it is checked against.
            var schemaOf = untyped is not null && untyped.TryGetValue(p.Name, out var shape) ? shape : p.Value;
            var typeText = TypeText(schemaOf);
            if (typeText == "array" && schemaOf.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object &&
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

    // One JsonException.Path segment: "[3]", ".count" or "['odd key']".
    [GeneratedRegex(@"\[(\d+)\]|\.([^.\[]+)|\['([^']*)'\]")]
    private static partial Regex PathSegment();
}
