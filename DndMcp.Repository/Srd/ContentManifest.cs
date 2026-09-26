using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DndMcp.Repository.Srd;

/// <summary>
/// The pin for the vendored 5e-database content (<c>content/5e-database/manifest.json</c>): the upstream tag and
/// the exact sha256 and size of every file. Only <c>scripts/fetch-5e-database.sh</c> writes it.
///
/// <para>
/// Every rules answer the server gives comes from those bytes, so "which bytes, from which tag" must always be
/// answerable. The Phase 2 index bootstrapper stores <see cref="ComputeFingerprint"/> in <c>srd.db</c>, as one part of
/// its staleness key, and rebuilds the index when it changes. Before indexing, it runs
/// <see cref="ContentManifestVerifier"/>, so a hand-edited or half re-vendored tree is refused rather than silently
/// indexed.
/// </para>
/// <para>
/// <see cref="Parse(string)"/> validates the paths as well as the JSON. The verifier opens every listed path, so an
/// entry such as <c>../../etc/passwd</c> would make it read outside the vendored tree. Manifests with rooted,
/// climbing, backslashed or duplicate paths are rejected when they are loaded.
/// </para>
/// </summary>
public sealed partial class ContentManifest
{
    public const string FileName = "manifest.json";

    // The manifest format is ours, so unknown fields are an error: a typo such as "sha265" would otherwise be
    // skipped and the file would look intact while pinning nothing.
    private static readonly JsonSerializerOptions ManifestJson = CreateOptions();

    /// <summary>Upstream tag, e.g. <c>5e-database-v7.0.0</c>.</summary>
    public required string Tag { get; init; }

    /// <summary>Upstream repository URL (the 5e-bits monorepo, not the archived 5e-database repo).</summary>
    public required string Repository { get; init; }

    /// <summary>Path inside the repository that the files came from, with an <c>{edition}</c> placeholder.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Every vendored file, sorted by <see cref="ContentManifestFile.Path"/>.</summary>
    public required IReadOnlyList<ContentManifestFile> Files { get; init; }

    /// <summary>
    /// A sha256 over the tag and every (path, sha256, bytes) triple, in ordinal path order. It changes exactly when
    /// the vendored 5e-database content changes, and it ignores the manifest's whitespace and property order; the raw
    /// manifest bytes would also change on a harmless reformat.
    ///
    /// <para>
    /// It is only the 5e-database PART of the "is srd.db stale?" key. srd.db also holds the 2024 rules glossary
    /// (<c>content/rules-glossary-2024.json</c>, outside this manifest), and its rows are shaped by the importer. Phase 2
    /// must combine this value with the glossary's hash and an importer/schema version. Keyed on this alone, a
    /// glossary edit or an importer fix would leave a stale index in place with nothing to say so.
    /// </para>
    /// </summary>
    public string ComputeFingerprint()
    {
        var canonical = new StringBuilder().Append(Tag).Append('\n');
        foreach (var file in Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            canonical.Append(file.Path).Append('\t').Append(file.Sha256).Append('\t').Append(file.Bytes).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>Reads and validates the manifest at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">The manifest is not valid JSON or breaks a rule described on the type.</exception>
    public static ContentManifest Load(string path) => Parse(File.ReadAllText(path));

    /// <inheritdoc cref="Load(string)"/>
    public static ContentManifest Parse(string json)
    {
        ContentManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ContentManifest>(json, ManifestJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The content manifest is not valid: {ex.Message}", ex);
        }

        if (manifest is null)
        {
            throw new InvalidDataException("The content manifest is empty (JSON null).");
        }

        manifest.Validate();
        return manifest;
    }

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Tag) || string.IsNullOrWhiteSpace(Repository) || string.IsNullOrWhiteSpace(SourcePath))
        {
            throw new InvalidDataException("The content manifest must name its tag, repository and source_path.");
        }

        if (Files.Count == 0)
        {
            throw new InvalidDataException("The content manifest lists no files; an index built from it would be empty.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            // RespectNullableAnnotations covers properties, not list elements, so "files": [null] gets this far.
            if (file is null)
            {
                throw new InvalidDataException("The content manifest has a null entry in \"files\".");
            }

            if (!IsSafeRelativePath(file.Path))
            {
                throw new InvalidDataException(
                    $"Content manifest path \"{file.Path}\" must be relative to content/5e-database, use '/' separators, " +
                    "and contain no empty, '.' or '..' segments.");
            }

            if (!seen.Add(file.Path))
            {
                throw new InvalidDataException($"Content manifest lists \"{file.Path}\" more than once.");
            }

            if (!Sha256Hex().IsMatch(file.Sha256))
            {
                throw new InvalidDataException(
                    $"Content manifest sha256 for \"{file.Path}\" must be 64 lowercase hex characters (got \"{file.Sha256}\").");
            }

            if (file.Bytes < 0)
            {
                throw new InvalidDataException($"Content manifest size for \"{file.Path}\" is negative ({file.Bytes}).");
            }
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/'))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Hex();

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
}

/// <summary>One vendored file, as pinned by the manifest.</summary>
public sealed class ContentManifestFile
{
    /// <summary>Relative to <c>content/5e-database</c>, '/'-separated, e.g. <c>v7.0.0/2014/5e-SRD-Monsters.json</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Lowercase hex sha256 of the file's exact bytes (no line-ending normalisation; see .gitattributes).</summary>
    public required string Sha256 { get; init; }

    /// <summary>File size in bytes. Checked alongside the hash so a truncated file is reported as such.</summary>
    public required long Bytes { get; init; }
}
