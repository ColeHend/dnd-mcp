using System.Text.Json.Nodes;

namespace DndMcp.Domain.Characters;

/// <summary>
/// What changed between two versions of one sheet, in the form the Repository logs it: whole new values for the plain
/// columns, and RFC 7396 merge patches for the four per-key columns (<see cref="SheetColumns.PerKey"/>), so a change to
/// one slot level or one resource is logged (and undone) as that key alone.
///
/// <para>
/// <b>Why patches and not whole objects for those columns:</b> a patch applies to the column as it is when it is
/// applied. The end-of-combat write-back and a later rest both change <c>spell_slots</c>; written whole, the second would
/// carry the first's stale value of every other key, and undoing one would revert keys the other changed. The patch holds
/// a removed key as <c>null</c> and diffs nested objects key by key, so merging it into the stored value gives exactly
/// the new value (<see cref="Between"/> pins that).
/// </para>
/// <para>
/// Values are compared structurally (key order inside an object does not count), as the Repository's change detection
/// compares them, so a diff is never non-empty for text that only differs in spelling.
/// </para>
/// </summary>
/// <param name="Columns">Plain columns that changed → their new stored value (<see cref="SheetJson.Column"/>).</param>
/// <param name="Patches">Per-key columns that changed → the merge patch that turns the old object into the new.</param>
/// <param name="Fields">Every change for a result to show: one per plain column, one per changed key of a per-key column.</param>
public sealed record SheetDiff(
    IReadOnlyDictionary<string, object?> Columns,
    IReadOnlyDictionary<string, JsonObject> Patches,
    IReadOnlyList<SheetFieldChange> Fields)
{
    /// <summary>Nothing changed: the Repository logs no batch and says "Nothing changed".</summary>
    public bool IsEmpty => Fields.Count == 0;

    /// <summary>The columns that changed (plain and per-key), in table order.</summary>
    public IReadOnlyList<string> ChangedColumns =>
        SheetColumns.All.Where(c => Columns.ContainsKey(c) || Patches.ContainsKey(c)).ToList();

    /// <summary>
    /// The diff from <paramref name="before"/> (null: a sheet being created, compared with <see cref="CharacterSheet.New"/>)
    /// to <paramref name="after"/>. The managed columns (entity_id, created_at, updated_at) are never part of it.
    /// </summary>
    /// <exception cref="ArgumentException">The two sheets belong to different entities.</exception>
    public static SheetDiff Between(CharacterSheet? before, CharacterSheet after)
    {
        ArgumentNullException.ThrowIfNull(after);
        before ??= CharacterSheet.New(after.EntityId);
        if (!string.Equals(before.EntityId, after.EntityId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A diff is between two versions of one sheet.", nameof(after));
        }

        var columns = new OrderedDictionary<string, object?>(StringComparer.Ordinal);
        var patches = new OrderedDictionary<string, JsonObject>(StringComparer.Ordinal);
        var fields = new List<SheetFieldChange>();
        foreach (var column in SheetColumns.All.Except(SheetColumns.Managed))
        {
            var old = SheetJson.Column(before, column);
            var now = SheetJson.Column(after, column);
            if (Same(old, now))
            {
                continue;
            }

            if (SheetColumns.PerKey.Contains(column))
            {
                var oldObject = SheetJson.AsObject(old as string) ?? new JsonObject();
                var newObject = SheetJson.AsObject(now as string) ?? new JsonObject();
                var patch = (JsonObject)MergeDiff(oldObject, newObject)!;
                patches[column] = patch;
                foreach (var (key, _) in patch)
                {
                    fields.Add(new SheetFieldChange(
                        column, key, Text(oldObject.TryGetPropertyValue(key, out var o) ? o : null), Text(newObject.TryGetPropertyValue(key, out var n) ? n : null)));
                }
            }
            else
            {
                columns[column] = now;
                fields.Add(new SheetFieldChange(column, null, ValueText(old), ValueText(now)));
            }
        }

        return new SheetDiff(columns, patches, fields);
    }

    /// <summary>
    /// An RFC 7396 merge patch from <paramref name="old"/> to <paramref name="now"/>: removed keys as null, nested objects
    /// diffed, anything else replaced. Null when they are equal.
    /// </summary>
    public static JsonNode? MergeDiff(JsonNode? old, JsonNode? now)
    {
        if (old is JsonObject oldObject && now is JsonObject newObject)
        {
            var patch = new JsonObject();
            foreach (var (name, _) in oldObject)
            {
                if (!newObject.ContainsKey(name))
                {
                    patch[name] = null;
                }
            }

            foreach (var (name, value) in newObject)
            {
                var previous = oldObject.TryGetPropertyValue(name, out var p) ? p : null;
                if (!oldObject.ContainsKey(name))
                {
                    patch[name] = value?.DeepClone();
                }
                else if (!JsonNode.DeepEquals(previous, value))
                {
                    patch[name] = previous is JsonObject && value is JsonObject ? MergeDiff(previous, value) : value?.DeepClone();
                }
            }

            return patch;
        }

        return JsonNode.DeepEquals(old, now) ? null : now?.DeepClone();
    }

    private static bool Same(object? a, object? b)
    {
        if (a is string s && b is string t)
        {
            var x = SheetJson.AsObject(s) ?? (JsonNode?)TryArray(s);
            var y = SheetJson.AsObject(t) ?? (JsonNode?)TryArray(t);
            return x is not null && y is not null ? JsonNode.DeepEquals(x, y) : string.Equals(s, t, StringComparison.Ordinal);
        }

        return Equals(a, b);
    }

    private static JsonArray? TryArray(string text)
    {
        try
        {
            return text.TrimStart().StartsWith('[') ? JsonNode.Parse(text) as JsonArray : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is null ? null : SheetJson.Serialize(node);

    private static string? ValueText(object? value) => value switch
    {
        null => null,
        long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
        string s => s,
        _ => value.ToString(),
    };
}

/// <summary>
/// One change for a result to show: a column (and, for a per-key column, the key: a slot level, a die, a resource slug,
/// an ability) with its value before and after as stored (JSON text for JSON values, the number or the text otherwise),
/// null where there was or is none.
/// </summary>
public sealed record SheetFieldChange(string Column, string? Key, string? Before, string? After);
