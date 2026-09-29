using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Domain.Simulation;

namespace DndMcp.Repository.Srd.Combatants;

/// <summary>
/// Normalization facts the monster data cannot give: <c>content/overrides/monsters.{2014,2024}.json</c>, keyed
/// <c>"{edition}/{index}"</c>. The 2024 Initiative modifier every stat block prints and the data lacks; the five 2014
/// save-for-half actions the data labels <c>"none"</c>; anything else a stat block states that its record gets wrong
/// in a way the normalizer cannot read.
///
/// <para>
/// <b>Facts, never text.</b> An override changes how the normalizer READS a record (this save halves, this monster's
/// initiative is +7); it never replaces rules text. A record whose text is wrong is fixed in
/// <c>content/srd-corrections.json</c>, where every consumer (lookup, search, the normalizer) sees the fix. Putting a
/// text fix here would leave <c>rules_get</c> quoting the broken text while the simulator ran the repaired one.
/// </para>
/// <para>
/// <b>Every override says why</b> (<c>note</c>) and must still change something: <c>MonsterOverridesTests</c> removes
/// each one in turn and fails if the normalized stat block is unchanged, so an override the data no longer needs (a
/// re-vendor fixed the record) is noticed rather than kept as dead weight that would mask the next upstream change.
/// Unknown fields are refused, so a misspelled <c>"on_sucess"</c> fails at load instead of silently doing nothing.
/// </para>
/// </summary>
public sealed class MonsterOverrides
{
    /// <summary>The directory under the content root that holds the override files.</summary>
    public const string DirectoryName = "overrides";

    private static readonly JsonSerializerOptions FileJson = CreateOptions();

    private readonly IReadOnlyDictionary<string, MonsterOverride> _byKey;

    private MonsterOverrides(IReadOnlyDictionary<string, MonsterOverride> byKey)
    {
        _byKey = byKey;
    }

    /// <summary>No overrides at all: what the data alone says.</summary>
    public static MonsterOverrides Empty { get; } = new(new Dictionary<string, MonsterOverride>());

    /// <summary>Every override, by <c>"{edition}/{index}"</c>.</summary>
    public IReadOnlyDictionary<string, MonsterOverride> Entries => _byKey;

    /// <summary>The file name for an edition: <c>monsters.2024.json</c>.</summary>
    public static string FileName(string edition) => $"monsters.{edition}.json";

    /// <summary>Reads both editions' files from <c>&lt;contentRoot&gt;/overrides/</c>.</summary>
    /// <exception cref="FileNotFoundException">A file is missing: they ship with the content, so this is a broken install.</exception>
    /// <exception cref="InvalidDataException">A file is malformed or breaks a rule described on the type.</exception>
    public static MonsterOverrides Load(string contentRoot)
    {
        var files = new List<(string, string)>();
        foreach (var edition in SrdEdition.All)
        {
            var path = Path.Combine(contentRoot, DirectoryName, FileName(edition));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"No monster overrides file at {path}; it ships with the content directory.", path);
            }

            files.Add((edition, File.ReadAllText(path)));
        }

        return Parse(files);
    }

    /// <summary>Parses override files, each (edition, JSON text); every key must name that edition.</summary>
    /// <exception cref="InvalidDataException">A file is malformed or breaks a rule described on the type.</exception>
    public static MonsterOverrides Parse(IEnumerable<(string Edition, string Json)> files)
    {
        var all = new Dictionary<string, MonsterOverride>(StringComparer.Ordinal);
        foreach (var (edition, json) in files)
        {
            Dictionary<string, MonsterOverride>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<Dictionary<string, MonsterOverride>>(json, FileJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{FileName(edition)} is not valid: {ex.Message}", ex);
            }

            foreach (var (key, entry) in entries ?? throw new InvalidDataException($"{FileName(edition)} is JSON null."))
            {
                Validate(edition, key, entry);
                if (!all.TryAdd(key, entry))
                {
                    throw new InvalidDataException($"The monster override \"{key}\" appears twice.");
                }
            }
        }

        return new MonsterOverrides(all);
    }

    /// <summary>The overrides for one monster, or null.</summary>
    public MonsterOverride? For(string edition, string index) => _byKey.GetValueOrDefault($"{edition}/{index}");

    /// <summary>
    /// These overrides without one entry, or without one action of it (<paramref name="action"/>): a test seam, what
    /// <c>MonsterOverridesTests</c> uses to prove each override still changes the stat block. With
    /// <paramref name="field"/> (a top-level field's wire name, as the file writes it), only that fact is dropped.
    /// Nothing in the server calls it.
    /// </summary>
    internal MonsterOverrides Without(string key, string? action = null, string? field = null)
    {
        var copy = new Dictionary<string, MonsterOverride>(_byKey, StringComparer.Ordinal);
        if (!copy.TryGetValue(key, out var entry))
        {
            return this;
        }

        if (action is not null)
        {
            var actions = new Dictionary<string, ActionOverride>(entry.Actions ?? new Dictionary<string, ActionOverride>(), StringComparer.Ordinal);
            actions.Remove(action);
            copy[key] = entry with { Actions = actions };
        }
        else if (field is not null)
        {
            copy[key] = field switch
            {
                "initiative" => entry with { Initiative = null },
                "legendary_uses" => entry with { LegendaryUses = null },
                "legendary_uses_in_lair" => entry with { LegendaryUsesInLair = null },
                _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not an override field."),
            };
        }
        else
        {
            copy.Remove(key);
        }

        return new MonsterOverrides(copy);
    }

    private static void Validate(string edition, string key, MonsterOverride entry)
    {
        var parts = key.Split('/');
        if (parts.Length != 2 || parts[0] != edition || parts[1].Length == 0)
        {
            throw new InvalidDataException($"{FileName(edition)}: \"{key}\" is not a key of the form \"{edition}/<monster index>\".");
        }

        var hasTopLevelFact = entry.Initiative is not null || entry.LegendaryUses is not null || entry.LegendaryUsesInLair is not null;
        if (!hasTopLevelFact && (entry.Actions is null || entry.Actions.Count == 0))
        {
            throw new InvalidDataException($"{FileName(edition)}: \"{key}\" overrides nothing.");
        }

        if (hasTopLevelFact && string.IsNullOrWhiteSpace(entry.Note))
        {
            throw new InvalidDataException($"{FileName(edition)}: \"{key}\" needs a \"note\" saying why.");
        }

        foreach (var (field, value, min, max) in new (string, int?, int, int)[]
                 {
                     ("initiative", entry.Initiative, -10, 20), ("legendary_uses", entry.LegendaryUses, 1, 5),
                     ("legendary_uses_in_lair", entry.LegendaryUsesInLair, 1, 6),
                 })
        {
            if (value is { } n && (n < min || n > max))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{FileName(edition)}: \"{key}\" {field} must be {min}-{max}; it is {n}."));
            }
        }

        foreach (var (name, action) in entry.Actions ?? new Dictionary<string, ActionOverride>())
        {
            var where = $"{FileName(edition)}: \"{key}\" action \"{name}\"";
            if (string.IsNullOrWhiteSpace(action.Note))
            {
                throw new InvalidDataException($"{where} needs a \"note\" saying why.");
            }

            if (action.OnSuccess is not null && !StatBlockValues.OnSuccess.All.Contains(action.OnSuccess))
            {
                throw new InvalidDataException($"{where}: on_success is \"{action.OnSuccess}\"; use {string.Join(" or ", StatBlockValues.OnSuccess.All)}.");
            }

            if (action.Area is { } area && (!StatBlockValues.Shapes.All.Contains(area.Shape) || area.Size is < 1 or > 1000))
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{where}: area needs a shape ({string.Join(", ", StatBlockValues.Shapes.All)}) and a size 1-1000; it has {area.Shape} {area.Size}."));
            }

            if (action.Targets is < 1 or > 20)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"{where}: targets must be 1-20; it is {action.Targets}."));
            }

            if (action.OnSuccess is null && action.Area is null && action.Targets is null)
            {
                throw new InvalidDataException($"{where} overrides nothing.");
            }
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>One monster's overrides. Every field is optional; <see cref="Note"/> is required when a top-level fact is set.</summary>
public sealed record MonsterOverride
{
    /// <summary>The Initiative modifier the stat block prints (2024; the data has none).</summary>
    public int? Initiative { get; init; }

    /// <summary>Legendary action uses per round, when not the default 3.</summary>
    public int? LegendaryUses { get; init; }

    /// <summary>Legendary action uses in the lair, when the default (4 with <c>xp_in_lair</c>) is wrong.</summary>
    public int? LegendaryUsesInLair { get; init; }

    /// <summary>Why the top-level facts are set, and where they come from.</summary>
    public string? Note { get; init; }

    /// <summary>Per-action facts, keyed by the action's name (legendary names without "(Costs N Actions)").</summary>
    public IReadOnlyDictionary<string, ActionOverride>? Actions { get; init; }
}

/// <summary>Facts about one action or legendary action. <see cref="Note"/> is required.</summary>
public sealed record ActionOverride
{
    /// <summary><see cref="StatBlockValues.OnSuccess"/>: what a successful save does, when the data says otherwise.</summary>
    public string? OnSuccess { get; init; }

    /// <summary>The area, when the prose names one the normalizer cannot read.</summary>
    public AreaOverride? Area { get; init; }

    /// <summary>The number of creatures affected, when the prose names it in a way the normalizer cannot read.</summary>
    public int? Targets { get; init; }

    public required string Note { get; init; }
}

/// <summary>An area in an override: a <see cref="StatBlockValues.Shapes"/> value and a size in feet.</summary>
public sealed record AreaOverride(string Shape, int Size);
