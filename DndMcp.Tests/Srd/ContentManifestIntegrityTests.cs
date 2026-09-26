using System.Security.Cryptography;
using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins the vendored tree to its manifest. The files under <c>content/5e-database/</c> are exactly the bytes
/// <c>scripts/fetch-5e-database.sh</c> downloaded at the pinned tag: none missing, none edited, none unlisted.
///
/// <para>
/// The hashes are recomputed here independently of <see cref="ContentManifestVerifier"/>, so a bug in the verifier
/// cannot hide a damaged file. A hand edit to any vendored JSON (or a git line-ending rewrite; see .gitattributes)
/// fails these tests.
/// </para>
/// </summary>
public sealed class ContentManifestIntegrityTests
{
    [Fact]
    public void Manifest_PinnedTag_NamesMonorepoAndSourcePath()
    {
        var manifest = SrdTestContent.Manifest;

        Assert.Equal(SrdTestContent.PinnedTag, manifest.Tag);
        Assert.Equal("https://github.com/5e-bits/5e-srd-api", manifest.Repository);
        Assert.Equal("packages/5e-database/src/{edition}/en", manifest.SourcePath);
    }

    [Theory]
    [InlineData(SrdEdition.Edition2014, 24)]
    [InlineData(SrdEdition.Edition2024, 25)]
    public void Manifest_Files_ListEveryEditionFileUnderTheTagVersion(string edition, int expectedFiles)
    {
        var prefix = $"{SrdTestContent.VersionDirectory}/{edition}/";

        var files = SrdTestContent.Manifest.Files.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        Assert.Equal(expectedFiles, files.Count);
        Assert.All(files, f => Assert.EndsWith(".json", f.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Manifest_Files_AllBelongToOneVersionAndEdition()
    {
        var allowed = SrdEdition.All.Select(e => $"{SrdTestContent.VersionDirectory}/{e}/").ToList();

        Assert.All(SrdTestContent.Manifest.Files, f =>
            Assert.Contains(allowed, prefix => f.Path.StartsWith(prefix, StringComparison.Ordinal)));
    }

    // The fetch script sorts entries so that a re-run at the same tag writes a byte-identical manifest. An unsorted
    // manifest means it was edited by hand or by something else.
    [Fact]
    public void Manifest_Files_AreSortedOrdinallyByPath()
    {
        var paths = SrdTestContent.Manifest.Files.Select(f => f.Path).ToList();

        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
    }

    [Fact]
    public void ManifestEntries_OnDisk_MatchPinnedSha256AndSize()
    {
        var problems = new List<string>();

        foreach (var entry in SrdTestContent.Manifest.Files)
        {
            var fullPath = Path.Combine(SrdTestContent.ContentRoot, entry.Path);
            if (!File.Exists(fullPath))
            {
                problems.Add($"{entry.Path}: missing");
                continue;
            }

            var bytes = File.ReadAllBytes(fullPath);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (bytes.LongLength != entry.Bytes || sha256 != entry.Sha256)
            {
                problems.Add($"{entry.Path}: {bytes.LongLength} bytes sha256 {sha256}, pinned {entry.Bytes} bytes sha256 {entry.Sha256}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void JsonFilesUnderContent_AreAllListedInManifest()
    {
        var listed = SrdTestContent.Manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        var unlisted = Directory
            .EnumerateFiles(SrdTestContent.ContentRoot, "*.json", SearchOption.AllDirectories)
            .Select(full => Path.GetRelativePath(SrdTestContent.ContentRoot, full).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => relative != ContentManifest.FileName && !listed.Contains(relative))
            .ToList();

        Assert.Empty(unlisted);
    }

    [Fact]
    public void Verify_VendoredTree_IsIntact()
    {
        var result = new ContentManifestVerifier().Verify(SrdTestContent.ContentRoot);

        Assert.True(result.IsIntact, result.Describe());
    }

    // The MIT notice must ship with the data (README → Attribution). Losing it is a licence breach, not a test nit.
    [Fact]
    public void LicenseFile_VendoredBesideTheData_IsTheUpstreamMitNotice()
    {
        var license = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "content", "LICENSES", "5e-database-MIT.txt"));

        Assert.StartsWith("MIT License", license, StringComparison.Ordinal);
        Assert.Contains("Adrian Padua, Christopher Ward", license, StringComparison.Ordinal);
        Assert.Contains("The above copyright notice and this permission notice shall be included", license, StringComparison.Ordinal);
    }
}
