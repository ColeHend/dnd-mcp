using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// The content an index is built from (<c>&lt;dir&gt;/content</c>: <c>5e-database/</c>, <c>rules-glossary-2024.json</c>
/// and <c>srd-corrections.json</c>) and the staleness key that identifies it, computed without building anything.
///
/// <para>
/// Every server start compares this key with the one stored in srd.db and rebuilds only when they differ, so the key
/// must change whenever anything that shapes the index changes, and must not change otherwise:
/// <list type="bullet">
/// <item><c>schema:</c> <see cref="SrdIndexSchema.Version"/>: the importer, search text, name keys, aliases and
/// counterparts. Without it an importer fix would leave every existing srd.db in place.</item>
/// <item><c>content:</c> <see cref="ContentManifest.ComputeFingerprint"/>: the pinned 5e-database bytes. The manifest is
/// what re-vendoring rewrites; hashing it is milliseconds, where hashing the 7 MB tree on every start is not needed
/// (the builder verifies the tree before it trusts it).</item>
/// <item><c>glossary:</c> the sha256 of <c>rules-glossary-2024.json</c>'s exact bytes. The glossary sits outside the
/// manifest, so the fingerprint alone would miss an update to it.</item>
/// <item><c>corrections:</c> the sha256 of <c>srd-corrections.json</c> (<see cref="SrdCorrections.Sha256"/>). Without
/// it a fixed record would reach an installed server only with the next unrelated rebuild.</item>
/// </list>
/// </para>
/// <para>
/// The glossary and corrections bytes are read once, here, and kept: the builder indexes exactly the bytes that were
/// hashed, so the key stored in srd.db always describes what the file holds, even if either is replaced mid-build.
/// </para>
/// </summary>
public sealed class SrdIndexContent
{
    /// <summary>The 5e-database directory under the content root.</summary>
    public const string DatabaseDirectoryName = "5e-database";

    private SrdIndexContent(string contentRoot, ContentManifest manifest, byte[] glossaryBytes, SrdCorrections corrections)
    {
        ContentRoot = contentRoot;
        Manifest = manifest;
        GlossaryBytes = glossaryBytes;
        GlossarySha256 = Convert.ToHexStringLower(SHA256.HashData(glossaryBytes));
        Corrections = corrections;
        ContentFingerprint = manifest.ComputeFingerprint();
        StalenessKey = ComputeStalenessKey(SrdIndexSchema.Version, ContentFingerprint, GlossarySha256, CorrectionsSha256);
    }

    /// <summary>The <c>content</c> directory, as given (made absolute).</summary>
    public string ContentRoot { get; }

    /// <summary><c>content/5e-database</c>, the directory the manifest's paths are relative to.</summary>
    public string DatabaseDirectory => Path.Combine(ContentRoot, DatabaseDirectoryName);

    public string GlossaryPath => Path.Combine(ContentRoot, SrdKinds.RulesGlossary2024FileName);

    public ContentManifest Manifest { get; }

    public string ContentFingerprint { get; }

    public string GlossarySha256 { get; }

    /// <summary>The glossary file's bytes, exactly as hashed into <see cref="GlossarySha256"/>.</summary>
    public ReadOnlyMemory<byte> GlossaryBytes { get; }

    /// <summary>The curated corrections the builder applies (<c>srd-corrections.json</c>), parsed and validated.</summary>
    public SrdCorrections Corrections { get; }

    /// <summary>The sha256 of <c>srd-corrections.json</c>'s bytes: its part of <see cref="StalenessKey"/>.</summary>
    public string CorrectionsSha256 => Corrections.Sha256;

    public string CorrectionsPath => Path.Combine(ContentRoot, SrdCorrections.FileName);

    public string StalenessKey { get; }

    /// <summary>
    /// Reads the manifest and the glossary under <paramref name="contentRoot"/> and computes the key. It does not verify
    /// the 5e-database files against the manifest; <see cref="SrdIndexBuilder"/> does that before building.
    /// </summary>
    /// <exception cref="SrdIndexUnavailableException">
    /// The directory, the manifest, the glossary or the corrections file is missing or unreadable, or the manifest or
    /// the corrections file is malformed. The message names the path and what to do.
    /// </exception>
    public static SrdIndexContent Load(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        var root = Path.GetFullPath(contentRoot);
        if (!Directory.Exists(root))
        {
            throw new SrdIndexUnavailableException(
                $"No SRD content directory at {root}. The server reads content/ next to its executable; " +
                "copy the whole publish directory, not just the binary.");
        }

        var manifestPath = Path.Combine(root, DatabaseDirectoryName, ContentManifest.FileName);
        ContentManifest manifest;
        try
        {
            manifest = ContentManifest.Load(manifestPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SrdIndexUnavailableException(
                $"No content manifest at {manifestPath}. Vendor the data with scripts/fetch-5e-database.sh.", ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new SrdIndexUnavailableException($"Cannot read the content manifest at {manifestPath}: {ex.Message}", ex);
        }

        var glossaryPath = Path.Combine(root, SrdKinds.RulesGlossary2024FileName);
        byte[] glossaryBytes;
        try
        {
            glossaryBytes = File.ReadAllBytes(glossaryPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SrdIndexUnavailableException(
                $"No 2024 Rules Glossary at {glossaryPath}. It ships in content/ beside 5e-database/; " +
                "restore it from the repository (see content/rules-glossary-2024.md).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SrdIndexUnavailableException($"Cannot read the 2024 Rules Glossary at {glossaryPath}: {ex.Message}", ex);
        }

        return new SrdIndexContent(root, manifest, glossaryBytes, LoadCorrections(root));
    }

    /// <summary>
    /// sha256 over <c>schema:{version}</c>, <c>content:{fingerprint}</c>, <c>glossary:{sha256}</c> and
    /// <c>corrections:{sha256}</c>, one per line, as lowercase hex. Public so tests can show each input moves the key on
    /// its own.
    /// </summary>
    public static string ComputeStalenessKey(int schemaVersion, string contentFingerprint, string glossarySha256, string correctionsSha256)
    {
        ArgumentNullException.ThrowIfNull(contentFingerprint);
        ArgumentNullException.ThrowIfNull(glossarySha256);
        ArgumentNullException.ThrowIfNull(correctionsSha256);

        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"schema:{schemaVersion}\ncontent:{contentFingerprint}\nglossary:{glossarySha256}\ncorrections:{correctionsSha256}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    // The corrections ship in content/ like the glossary, and like the glossary their absence refuses the build: an index
    // without them would serve known-corrupt upstream text (Potion of Heroism's Gaseous Form effect) as SRD fact.
    private static SrdCorrections LoadCorrections(string root)
    {
        var path = Path.Combine(root, SrdCorrections.FileName);
        try
        {
            return SrdCorrections.Load(root);
        }
        catch (FileNotFoundException ex)
        {
            throw new SrdIndexUnavailableException(
                $"No SRD corrections file at {path}. It ships in content/ beside 5e-database/; copy the whole publish " +
                "directory, or restore content/srd-corrections.json from the repository.", ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new SrdIndexUnavailableException(
                $"Cannot use the SRD corrections at {path}: {ex.Message} Restore content/srd-corrections.json from the repository.", ex);
        }
    }
}
