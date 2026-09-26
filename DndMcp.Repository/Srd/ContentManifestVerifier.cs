using System.Security.Cryptography;

namespace DndMcp.Repository.Srd;

/// <summary>
/// Recomputes the sha256 and size of every file the <see cref="ContentManifest"/> lists and compares the vendored
/// tree with it, in both directions.
///
/// <para>
/// The Phase 2 index bootstrapper calls this before it (re)builds <c>srd.db</c>, and it compares the manifest's
/// fingerprint (one part of its staleness key; see <see cref="ContentManifest.ComputeFingerprint"/>) with the one
/// stored in the index to decide whether a rebuild is needed at all. Without the check, a
/// hand-edited monster file or a re-vendor interrupted halfway would be indexed as if it were the pinned upstream
/// data. Every rules answer would then quietly disagree with the tag the manifest claims, with no way to tell.
/// </para>
/// <para>
/// Both directions matter. <see cref="ContentVerificationResult.Missing"/> and
/// <see cref="ContentVerificationResult.Mismatched"/> catch damaged pinned files.
/// <see cref="ContentVerificationResult.Extra"/> catches files the manifest does not know about, such as a stale
/// version directory left beside the new one or a file dropped in by hand, which an importer that walks the directory
/// would otherwise pick up. Hidden files count too: "the tree is exactly the manifest" is easier to reason about than
/// a list of exceptions.
/// </para>
/// </summary>
public sealed class ContentManifestVerifier
{
    /// <summary>Loads <c>manifest.json</c> from <paramref name="contentRoot"/> and verifies the tree against it.</summary>
    /// <param name="contentRoot">The <c>content/5e-database</c> directory.</param>
    /// <exception cref="FileNotFoundException">
    /// The directory has no manifest, or does not exist. Checked explicitly: a missing directory would otherwise
    /// surface as <see cref="DirectoryNotFoundException"/>, which is not a <see cref="FileNotFoundException"/>, and a
    /// caller that catches only the documented type would crash.
    /// </exception>
    /// <exception cref="InvalidDataException">The manifest is malformed (see <see cref="ContentManifest"/>).</exception>
    public ContentVerificationResult Verify(string contentRoot)
    {
        var manifestPath = Path.Combine(contentRoot, ContentManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"No content manifest at {manifestPath}. Vendor the data with scripts/fetch-5e-database.sh.", manifestPath);
        }

        return Verify(contentRoot, ContentManifest.Load(manifestPath));
    }

    /// <summary>
    /// Verifies the tree under <paramref name="contentRoot"/> against an already-loaded manifest. A root that does not
    /// exist is a tree with every file missing, not an exception: "nothing is vendored" is a verification result like
    /// any other, and <see cref="ContentVerificationResult.Describe"/> says so.
    /// </summary>
    /// <param name="contentRoot">The <c>content/5e-database</c> directory the manifest paths are relative to.</param>
    /// <param name="manifest">The manifest to check against.</param>
    public ContentVerificationResult Verify(string contentRoot, ContentManifest manifest)
    {
        if (!Directory.Exists(contentRoot))
        {
            return new ContentVerificationResult
            {
                Missing = manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToList(),
                Extra = [],
                Mismatched = [],
            };
        }

        var missing = new List<string>();
        var mismatched = new List<ContentFileMismatch>();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in manifest.Files)
        {
            listed.Add(file.Path);
            var fullPath = Path.Combine(contentRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(fullPath))
            {
                missing.Add(file.Path);
                continue;
            }

            var actualBytes = new FileInfo(fullPath).Length;
            string actualSha256;
            using (var stream = File.OpenRead(fullPath))
            {
                actualSha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            if (actualBytes != file.Bytes || !string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
            {
                mismatched.Add(new ContentFileMismatch
                {
                    Path = file.Path,
                    ExpectedSha256 = file.Sha256,
                    ActualSha256 = actualSha256,
                    ExpectedBytes = file.Bytes,
                    ActualBytes = actualBytes,
                });
            }
        }

        // AttributesToSkip = None: the default options skip hidden files, and a dot-file left in the tree is exactly
        // the kind of stray this check exists to report.
        var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.None };
        var extra = Directory.EnumerateFiles(contentRoot, "*", enumeration)
            .Select(full => Path.GetRelativePath(contentRoot, full).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => relative != ContentManifest.FileName && !listed.Contains(relative))
            .ToList();

        missing.Sort(StringComparer.Ordinal);
        extra.Sort(StringComparer.Ordinal);
        mismatched.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new ContentVerificationResult
        {
            Missing = missing,
            Extra = extra,
            Mismatched = mismatched,
        };
    }
}

/// <summary>
/// The outcome of <see cref="ContentManifestVerifier"/>. <see cref="IsIntact"/> is the only thing a caller should
/// branch on; the lists and <see cref="Describe"/> are for the log line explaining why the index was not built.
/// </summary>
public sealed class ContentVerificationResult
{
    /// <summary>Listed in the manifest but not on disk.</summary>
    public required IReadOnlyList<string> Missing { get; init; }

    /// <summary>On disk under the content root but not listed (the manifest itself excluded).</summary>
    public required IReadOnlyList<string> Extra { get; init; }

    /// <summary>On disk, but the bytes differ from the pinned size or sha256.</summary>
    public required IReadOnlyList<ContentFileMismatch> Mismatched { get; init; }

    /// <summary>True only when the tree is exactly what the manifest pins: nothing missing, changed or unlisted.</summary>
    public bool IsIntact => Missing.Count == 0 && Extra.Count == 0 && Mismatched.Count == 0;

    /// <summary>A one-line explanation naming every offending path, suitable for a log line or startup error.</summary>
    public string Describe()
    {
        if (IsIntact)
        {
            return "Vendored content matches the manifest.";
        }

        var parts = new List<string>();
        if (Missing.Count > 0)
        {
            parts.Add($"{Missing.Count} missing ({string.Join(", ", Missing)})");
        }

        if (Mismatched.Count > 0)
        {
            parts.Add($"{Mismatched.Count} changed ({string.Join(", ", Mismatched.Select(DescribeChange))})");
        }

        if (Extra.Count > 0)
        {
            parts.Add($"{Extra.Count} not in the manifest ({string.Join(", ", Extra)})");
        }

        return $"Vendored content does not match the manifest: {string.Join("; ", parts)}. " +
               "Re-vendor with scripts/fetch-5e-database.sh instead of editing files by hand.";
    }

    // A same-size edit (one changed digit in a CR or a DC) is the most common hand edit. Reporting only sizes would
    // print "6 bytes, expected 6", which reads as if nothing differs, so equal sizes show short hashes instead.
    private static string DescribeChange(ContentFileMismatch m) =>
        m.ActualBytes != m.ExpectedBytes
            ? $"{m.Path}: {m.ActualBytes} bytes, expected {m.ExpectedBytes}"
            : $"{m.Path}: same size, sha256 {ShortHash(m.ActualSha256)}, expected {ShortHash(m.ExpectedSha256)}";

    private static string ShortHash(string sha256) => sha256.Length > 12 ? sha256[..12] + "…" : sha256;
}

/// <summary>A vendored file whose bytes differ from the manifest's pin.</summary>
public sealed class ContentFileMismatch
{
    public required string Path { get; init; }

    public required string ExpectedSha256 { get; init; }

    public required string ActualSha256 { get; init; }

    public required long ExpectedBytes { get; init; }

    public required long ActualBytes { get; init; }
}
