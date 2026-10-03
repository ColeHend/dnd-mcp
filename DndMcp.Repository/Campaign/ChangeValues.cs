using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>
/// How column values travel between C#, SQLite and change_log, in one place so the recorder that writes history, the undo
/// engine that reverses it and the replay that reads it can never disagree.
///
/// <para>
/// <b>Generic row values</b> (<see cref="ChangeRecorder.Read"/>, <see cref="ChangeRecorder.Insert"/>,
/// <see cref="ChangeReplay.RowAsOf"/>) are <see cref="string"/> (TEXT, and JSON columns as compact JSON text),
/// <see cref="long"/> (INTEGER) or <see cref="double"/> (REAL), or null. Callers may pass <c>int</c>, <c>bool</c>,
/// <see cref="JsonNode"/> and the like; <see cref="Normalize"/> converts them, and refuses anything that would reach a
/// STRICT column as the wrong type (STRICT still accepts '123' into an INTEGER column, so binding the right type is ours).
/// </para>
/// <para>
/// <b>change_log values.</b> A create or delete row's value is the whole row as a JSON object, JSON columns embedded as
/// JSON (not as strings), every column present. An update row's value for a column is the column's SQL value as text
/// (TEXT as is, INTEGER and REAL in invariant digits, a JSON column as its compact JSON); SQL NULL means the column was
/// NULL. For a per-key object column (<c>field_path</c> = <c>data.&lt;key&gt;</c>) it is the key's value as JSON, and
/// SQL NULL means the key was absent (a merge patch cannot store a JSON null, so the two never collide). Decode with
/// <see cref="Decode"/>; the table's column type says which reading applies.
/// </para>
/// </summary>
public static class ChangeValues
{
    /// <summary>
    /// The column and (for <c>data.&lt;key&gt;</c>) the top-level key a change_log field path names. The column name
    /// never contains a dot, so everything after the first dot is the key, even a key that itself contains dots.
    /// </summary>
    public static (CampaignColumn Column, string? Key) FieldOf(CampaignTable table, string fieldPath)
    {
        var dot = fieldPath.IndexOf('.');
        if (dot < 0)
        {
            return (table.Column(fieldPath), null);
        }

        var column = table.Column(fieldPath[..dot]);
        if (!column.LoggedPerKey)
        {
            throw new ArgumentException($"{table.Name}.{column.Name} is not logged per key.", nameof(fieldPath));
        }

        return (column, fieldPath[(dot + 1)..]);
    }

    /// <summary>
    /// A change_log update value, typed: for a column its value (<see cref="string"/>, <see cref="long"/>,
    /// <see cref="double"/>, JSON text as <see cref="string"/>, or null); for a <c>data.&lt;key&gt;</c> path the key's
    /// value as a <see cref="JsonNode"/> (null: the key was absent).
    /// </summary>
    public static object? Decode(string table, string fieldPath, string? text)
    {
        var (column, key) = FieldOf(CampaignTables.Get(table), fieldPath);
        if (key is not null)
        {
            return text is null ? null : JsonNode.Parse(text);
        }

        return FromLogText(column, text);
    }

    /// <summary>
    /// Converts a caller's value to the type the column stores (see the class summary).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The value cannot be stored in that column: the wrong type, a non-finite REAL, JSON that is not valid or not the
    /// column's JSON type, or null for a NOT NULL column. Always a caller bug, never input: validation happens before.
    /// </exception>
    public static object? Normalize(CampaignTable table, CampaignColumn column, object? value)
    {
        if (value is null || value is DBNull)
        {
            if (!column.Nullable)
            {
                throw new ArgumentException($"{table.Name}.{column.Name} cannot be NULL.", nameof(value));
            }

            return null;
        }

        return column.Type switch
        {
            CampaignColumnType.Text => value as string ??
                throw new ArgumentException($"{table.Name}.{column.Name} is TEXT; got {value.GetType().Name}.", nameof(value)),
            CampaignColumnType.Integer => value switch
            {
                long l => l,
                int i => (long)i,
                short s => (long)s,
                byte b => (long)b,
                bool flag => flag ? 1L : 0L,
                _ => throw new ArgumentException($"{table.Name}.{column.Name} is INTEGER; got {value.GetType().Name}.", nameof(value)),
            },
            CampaignColumnType.Real => Finite(table, column, value switch
            {
                double d => d,
                float f => f,
                long l => l,
                int i => i,
                decimal m => (double)m,
                _ => throw new ArgumentException($"{table.Name}.{column.Name} is REAL; got {value.GetType().Name}.", nameof(value)),
            }),
            _ => NormalizeJson(table, column, value),
        };
    }

    /// <summary>True when two normalized values of one column are the same value (JSON compared structurally).</summary>
    public static bool Same(CampaignColumn column, object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (column.IsJson)
        {
            return JsonNode.DeepEquals(JsonNode.Parse((string)a), JsonNode.Parse((string)b));
        }

        return a switch
        {
            string s => b is string t && string.Equals(s, t, StringComparison.Ordinal),
            long l => b is long m && l == m,
            double d => b is double e && d.Equals(e),
            _ => Equals(a, b),
        };
    }

    /// <summary>A normalized column value as change_log update text (null stays SQL NULL).</summary>
    public static string? ToLogText(CampaignColumn column, object? value) => value switch
    {
        null => null,
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"{column.Name}: {value.GetType().Name} is not a stored value type.", nameof(value)),
    };

    /// <summary>The inverse of <see cref="ToLogText"/>.</summary>
    public static object? FromLogText(CampaignColumn column, string? text)
    {
        if (text is null)
        {
            return null;
        }

        return column.Type switch
        {
            CampaignColumnType.Integer => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
            CampaignColumnType.Real => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => text,
        };
    }

    /// <summary>A normalized value as the JSON it has inside a whole-row snapshot.</summary>
    public static JsonNode? ToJson(CampaignColumn column, object? value) => value switch
    {
        null => null,
        string s when column.IsJson => JsonNode.Parse(s),
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => throw new ArgumentException($"{column.Name}: {value.GetType().Name} is not a stored value type.", nameof(value)),
    };

    /// <summary>The inverse of <see cref="ToJson"/>.</summary>
    public static object? FromJson(CampaignColumn column, JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (column.IsJson)
        {
            return CampaignLogJson.Serialize(node);
        }

        return column.Type switch
        {
            CampaignColumnType.Integer => node.Deserialize<long>(),
            CampaignColumnType.Real => node.Deserialize<double>(),
            _ => node.Deserialize<string>(),
        };
    }

    /// <summary>A whole row as the JSON object change_log stores for a create or a delete (every column, table order).</summary>
    public static JsonObject RowToJson(CampaignTable table, IReadOnlyDictionary<string, object?> row)
    {
        var json = new JsonObject();
        foreach (var column in table.Columns)
        {
            json[column.Name] = ToJson(column, row.TryGetValue(column.Name, out var value) ? value : null);
        }

        return json;
    }

    /// <summary>
    /// The row a create or delete snapshot describes. Keys the table no longer has are ignored, and columns the snapshot
    /// lacks (added by a later migration) are left out, so re-inserting an old snapshot takes the column's default.
    /// </summary>
    public static Dictionary<string, object?> RowFromJson(CampaignTable table, string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ??
                   throw new ArgumentException($"A {table.Name} snapshot is not a JSON object.", nameof(json));
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in table.Columns)
        {
            if (node.TryGetPropertyValue(column.Name, out var value))
            {
                row[column.Name] = FromJson(column, value);
            }
        }

        return row;
    }

    /// <summary>A value read from SQLite as a generic row value.</summary>
    internal static object? FromReader(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal) switch
        {
            long l => l,
            double d => d,
            string s => s,
            var other => throw new InvalidOperationException($"Unexpected {other.GetType().Name} in column {reader.GetName(ordinal)}."),
        };

    /// <summary>The value as a SqliteParameter value.</summary>
    internal static object ToParameter(object? value) => value ?? DBNull.Value;

    private static double Finite(CampaignTable table, CampaignColumn column, double value) =>
        double.IsFinite(value)
            ? value
            : throw new ArgumentException($"{table.Name}.{column.Name} must be a finite number.", nameof(value));

    private static string NormalizeJson(CampaignTable table, CampaignColumn column, object value)
    {
        JsonNode? node;
        try
        {
            node = value switch
            {
                string s => JsonNode.Parse(s),
                JsonNode n => n,
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                _ => throw new ArgumentException(
                    $"{table.Name}.{column.Name} is JSON; give a JSON string or a JsonNode, not {value.GetType().Name}.", nameof(value)),
            };
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{table.Name}.{column.Name} is not valid JSON.", nameof(value), ex);
        }

        var ok = column.Type switch
        {
            CampaignColumnType.JsonObject => node is JsonObject,
            CampaignColumnType.JsonArray => node is JsonArray,
            _ => true,
        };
        if (!ok)
        {
            throw new ArgumentException(
                $"{table.Name}.{column.Name} must hold a JSON {(column.Type == CampaignColumnType.JsonObject ? "object" : "array")}.",
                nameof(value));
        }

        return CampaignLogJson.Serialize(node);
    }
}
