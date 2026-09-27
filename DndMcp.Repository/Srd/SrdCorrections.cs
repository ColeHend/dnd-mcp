using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Repository.Srd;

/// <summary>
/// Curated corrections to upstream records that are provably wrong: <c>content/srd-corrections.json</c>, applied to a
/// record's JSON before it is indexed.
///
/// <para>
/// Why this exists. The vendored 5e-database data renders faithfully and is still wrong in places, and faithfully
/// relaying it is worse than useless because every answer is labelled "SRD 5.2.1". The 2024 magic items have text
/// shifted between records (Potion of Heroism carries the Gaseous Form potion's effect), flattened tables run into one
/// word, and 2024 category lists contradict their own items (Hide Armor claims Light Armor while the Medium Armor list
/// holds it). The vendored bytes stay untouched — they are pinned by sha256 and re-vendored by script — so fixes live
/// here, beside them, each with a reason and the source its replacement text was copied from.
/// </para>
/// <para>
/// What a correction may do: replace whole top-level properties that the record already has (<c>set</c>), and add
/// top-level properties the record does not have (<c>add</c>), for a section upstream dropped: the Pirate Captain's and
/// Unicorn's <c>bonus_actions</c>, the Entertainer's Pack's <c>weight</c>, a 2014 spell's <c>higher_level</c>. Nothing
/// else. Both are checked against the record when the file is applied, in both directions: <c>set</c> naming a property
/// that does not exist is refused, so a typo such as <c>"descr"</c> cannot quietly add a field no formatter reads while
/// the real one stays wrong; <c>add</c> naming a property that exists is refused, so an addition cannot silently
/// overwrite upstream text it was never checked against (that is <c>set</c>'s job, and says so). Every applied
/// correction's reason travels with the document (<see cref="SrdDocument.Corrections"/>), so corrected text is always
/// labelled as such.
/// </para>
/// <para>
/// The file's sha256 is part of srd.db's staleness key: editing a correction rebuilds the index on next start.
/// </para>
/// </summary>
public sealed class SrdCorrections
{
    public const string FileName = "srd-corrections.json";

    private static readonly JsonSerializerOptions FileJson = CreateOptions();

    private static readonly JsonWriterOptions CompactJson = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<SrdRef, SrdCorrection> _byRef;

    private SrdCorrections(IReadOnlyList<SrdCorrection> entries, string sha256)
    {
        Entries = entries;
        Sha256 = sha256;
        _byRef = entries.ToDictionary(e => e.Target);
    }

    /// <summary>Every correction, in file order.</summary>
    public IReadOnlyList<SrdCorrection> Entries { get; }

    /// <summary>Lowercase hex sha256 of the file's bytes: its part of the staleness key.</summary>
    public string Sha256 { get; }

    /// <summary>Reads and validates <c>srd-corrections.json</c> from the content root (the directory holding <c>5e-database/</c>).</summary>
    /// <exception cref="FileNotFoundException">The file is missing: it ships with the content, so its absence means a broken install.</exception>
    /// <exception cref="InvalidDataException">The file is malformed or breaks a rule described on the type.</exception>
    public static SrdCorrections Load(string contentRoot)
    {
        var path = Path.Combine(contentRoot, FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No SRD corrections file at {path}; it ships with the content directory.", path);
        }

        return Parse(File.ReadAllBytes(path));
    }

    /// <inheritdoc cref="Load(string)"/>
    public static SrdCorrections Parse(byte[] utf8Json)
    {
        SrdCorrectionsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SrdCorrectionsFile>(utf8Json, FileJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{FileName} is not valid: {ex.Message}", ex);
        }

        if (file is null)
        {
            throw new InvalidDataException($"{FileName} is JSON null.");
        }

        var seen = new HashSet<SrdRef>();
        foreach (var entry in file.Corrections)
        {
            if (entry is null)
            {
                throw new InvalidDataException($"{FileName} has a null entry in \"corrections\".");
            }

            var parts = entry.Ref.Split('/');
            if (parts.Length != 3 || !SrdEdition.All.Contains(parts[0]) || !SrdKinds.ExistsIn(parts[1], parts[0]) || parts[2].Length == 0)
            {
                throw new InvalidDataException(
                    $"{FileName}: \"{entry.Ref}\" is not a ref of the form edition/kind/slug with a kind that exists in that edition.");
            }

            if (!seen.Add(entry.Target))
            {
                throw new InvalidDataException($"{FileName} corrects \"{entry.Ref}\" more than once; merge the entries.");
            }

            if (entry.Set.Count == 0 && entry.Add.Count == 0)
            {
                throw new InvalidDataException($"{FileName}: the correction for \"{entry.Ref}\" sets nothing and adds nothing.");
            }

            if (entry.Set.Keys.Intersect(entry.Add.Keys, StringComparer.Ordinal).FirstOrDefault() is { } both)
            {
                throw new InvalidDataException(
                    $"{FileName}: the correction for \"{entry.Ref}\" both sets and adds \"{both}\"; a property the record has is set, one it lacks is added.");
            }

            if (string.IsNullOrWhiteSpace(entry.Reason) || string.IsNullOrWhiteSpace(entry.Source))
            {
                throw new InvalidDataException($"{FileName}: the correction for \"{entry.Ref}\" needs a reason and a source.");
            }
        }

        return new SrdCorrections(file.Corrections, Convert.ToHexStringLower(SHA256.HashData(utf8Json)));
    }

    /// <summary>The correction for <paramref name="target"/>, or null.</summary>
    public SrdCorrection? For(SrdRef target) => _byRef.GetValueOrDefault(target);

    /// <summary>
    /// <paramref name="record"/> with the correction for <paramref name="target"/> applied, as compact JSON, or null when
    /// there is no correction for it. Set properties keep their place; added properties follow the record's own, in file
    /// order.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The correction sets a property the record does not have, or adds one it already has.
    /// </exception>
    public string? Apply(SrdRef target, JsonElement record)
    {
        if (For(target) is not { } correction)
        {
            return null;
        }

        foreach (var name in correction.Set.Keys)
        {
            if (!record.TryGetProperty(name, out _))
            {
                throw new InvalidDataException(
                    $"{FileName}: the correction for \"{correction.Ref}\" sets \"{name}\", which the record does not have. " +
                    "\"set\" replaces existing properties only; a section upstream dropped goes in \"add\".");
            }
        }

        foreach (var name in correction.Add.Keys)
        {
            if (record.TryGetProperty(name, out _))
            {
                throw new InvalidDataException(
                    $"{FileName}: the correction for \"{correction.Ref}\" adds \"{name}\", which the record already has. " +
                    "\"add\" is for properties upstream dropped; replacing one the record has is \"set\".");
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, CompactJson))
        {
            writer.WriteStartObject();
            foreach (var property in record.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                (correction.Set.TryGetValue(property.Name, out var replacement) ? replacement : property.Value).WriteTo(writer);
            }

            foreach (var (name, value) in correction.Add)
            {
                writer.WritePropertyName(name);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class SrdCorrectionsFile
    {
        public required IReadOnlyList<SrdCorrection> Corrections { get; init; }
    }
}

/// <summary>One entry of <c>srd-corrections.json</c>.</summary>
public sealed class SrdCorrection
{
    /// <summary>The corrected record, <c>edition/kind/slug</c>, e.g. <c>2024/magic-item/potion-of-heroism</c>.</summary>
    public required string Ref { get; init; }

    /// <summary>Top-level properties to replace, with their full new values. Each must exist on the record.</summary>
    public Dictionary<string, JsonElement> Set { get; init; } = [];

    /// <summary>
    /// Top-level properties upstream dropped, with their values. Each must NOT exist on the record: an addition is text
    /// the record never had, so it cannot stand in for a replacement nobody checked against upstream's value.
    /// </summary>
    public Dictionary<string, JsonElement> Add { get; init; } = [];

    /// <summary>What was wrong, in one sentence a reader can act on ("Upstream text is the Gaseous Form potion's").</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Where the replacement came from: file and heading in the SRD 5.2 markdown (2024 records) or the SRD 5.1 markdown
    /// (2014 records).
    /// </summary>
    public required string Source { get; init; }

    [JsonIgnore]
    public SrdRef Target
    {
        get
        {
            var parts = Ref.Split('/');
            return new SrdRef(parts[0], parts[1], parts[2]);
        }
    }
}
