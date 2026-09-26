using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins that <see cref="ContentManifestVerifier"/> reports every way a vendored tree can drift from its manifest:
/// a file missing (up to the whole tree), a file whose bytes changed (same size or truncated), and a file the manifest
/// does not list, including hidden files and stale version directories. Only an exact match is intact. Phase 2
/// refuses to build srd.db from anything else, so a verifier that missed one of these would let damaged data be
/// indexed. The <see cref="ContentVerificationResult.Describe"/> line must make each problem visible, because it is
/// all the user sees.
///
/// <para>
/// Expected hashes are literal sha256 values of known content, not computed with the code under test.
/// </para>
/// </summary>
public sealed class ContentManifestVerifierTests : IDisposable
{
    private const string Hello = "hello\n";
    private const string HelloSha = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03";
    private const string JelloSha = "8b128914480c08c1d7a9c8a8ef78487f4f21cbc802a8134aa3850c9501571a15";
    private const string HellSha = "0ebdc3317b75839f643387d783535adc360ca01f33c75f7c1e7373adcd675c0b";
    private const string EmptySha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dndmcp-verifier-" + Guid.NewGuid().ToString("N"));
    private readonly ContentManifestVerifier _verifier = new();

    public ContentManifestVerifierTests()
    {
        Write("v7.0.0/2014/a.json", Hello);
        Write("v7.0.0/2024/b.json", string.Empty);
        File.WriteAllText(Path.Combine(_root, ContentManifest.FileName), $$"""
            {"tag":"5e-database-v7.0.0","repository":"r","source_path":"s","files":[
              {"path":"v7.0.0/2014/a.json","sha256":"{{HelloSha}}","bytes":6},
              {"path":"v7.0.0/2024/b.json","sha256":"{{EmptySha}}","bytes":0}]}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Verify_TreeMatchesManifest_IsIntact()
    {
        var result = _verifier.Verify(_root);

        Assert.True(result.IsIntact, result.Describe());
        Assert.Empty(result.Missing);
        Assert.Empty(result.Extra);
        Assert.Empty(result.Mismatched);
        Assert.Equal("Vendored content matches the manifest.", result.Describe());
    }

    [Fact]
    public void Verify_SameSizeDifferentBytes_ReportsMismatchWithActualHash()
    {
        Write("v7.0.0/2014/a.json", "jello\n");

        var result = _verifier.Verify(_root);

        Assert.False(result.IsIntact);
        var mismatch = Assert.Single(result.Mismatched);
        Assert.Equal("v7.0.0/2014/a.json", mismatch.Path);
        Assert.Equal(HelloSha, mismatch.ExpectedSha256);
        Assert.Equal(JelloSha, mismatch.ActualSha256);
        Assert.Equal(6, mismatch.ExpectedBytes);
        Assert.Equal(6, mismatch.ActualBytes);
    }

    // A same-size edit is the usual hand edit. "6 bytes, expected 6" would read as if nothing differed.
    [Fact]
    public void Describe_SameSizeChange_ShowsBothShortHashes()
    {
        Write("v7.0.0/2014/a.json", "jello\n");

        var description = _verifier.Verify(_root).Describe();

        Assert.Contains($"v7.0.0/2014/a.json: same size, sha256 {JelloSha[..12]}…, expected {HelloSha[..12]}…", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SizeChange_ShowsBothSizes()
    {
        Write("v7.0.0/2014/a.json", "hell");

        var description = _verifier.Verify(_root).Describe();

        Assert.Contains("v7.0.0/2014/a.json: 4 bytes, expected 6", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_TruncatedFile_ReportsMismatchWithActualSize()
    {
        Write("v7.0.0/2014/a.json", "hell");

        var mismatch = Assert.Single(_verifier.Verify(_root).Mismatched);

        Assert.Equal(4, mismatch.ActualBytes);
        Assert.Equal(HellSha, mismatch.ActualSha256);
    }

    // CRLF conversion is the realistic way this happens (git autocrlf): same text, different bytes.
    [Fact]
    public void Verify_LineEndingsRewritten_ReportsMismatch()
    {
        Write("v7.0.0/2014/a.json", "hello\r\n");

        Assert.Single(_verifier.Verify(_root).Mismatched);
    }

    [Fact]
    public void Verify_ListedFileDeleted_ReportsMissing()
    {
        File.Delete(Path.Combine(_root, "v7.0.0", "2024", "b.json"));

        var result = _verifier.Verify(_root);

        Assert.False(result.IsIntact);
        Assert.Equal(["v7.0.0/2024/b.json"], result.Missing);
        Assert.Empty(result.Mismatched);
    }

    [Theory]
    [InlineData("v7.0.0/2014/stray.json")]
    [InlineData("v6.0.0/2014/5e-SRD-Monsters.json")]   // stale version directory left behind by a re-vendor
    [InlineData("v7.0.0/2014/.a.json.swp")]            // hidden files are not exempt
    [InlineData("README.md")]
    public void Verify_UnlistedFile_ReportsExtra(string relativePath)
    {
        Write(relativePath, "{}");

        var result = _verifier.Verify(_root);

        Assert.False(result.IsIntact);
        Assert.Equal([relativePath], result.Extra);
        Assert.Contains(relativePath, result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_ManifestItself_IsNeverReportedAsExtra()
    {
        var result = _verifier.Verify(_root);

        Assert.DoesNotContain(ContentManifest.FileName, result.Extra);
    }

    [Fact]
    public void Verify_SeveralProblems_ReportsEachSortedAndDescribesAll()
    {
        File.Delete(Path.Combine(_root, "v7.0.0", "2024", "b.json"));
        Write("v7.0.0/2014/a.json", "jello\n");
        Write("z.json", "{}");
        Write("b.json", "{}");

        var result = _verifier.Verify(_root);
        var description = result.Describe();

        Assert.Equal(["v7.0.0/2024/b.json"], result.Missing);
        Assert.Equal(["b.json", "z.json"], result.Extra);
        Assert.Single(result.Mismatched);
        Assert.Contains("1 missing (v7.0.0/2024/b.json)", description, StringComparison.Ordinal);
        Assert.Contains("1 changed (v7.0.0/2014/a.json", description, StringComparison.Ordinal);
        Assert.Contains("2 not in the manifest (b.json, z.json)", description, StringComparison.Ordinal);
        Assert.Contains("scripts/fetch-5e-database.sh", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_NoManifestInRoot_ThrowsFileNotFound()
    {
        File.Delete(Path.Combine(_root, ContentManifest.FileName));

        Assert.Throws<FileNotFoundException>(() => _verifier.Verify(_root));
    }

    // Without the explicit check this is a DirectoryNotFoundException, which is not a FileNotFoundException, and a
    // caller catching the documented type would crash.
    [Fact]
    public void Verify_RootDoesNotExist_ThrowsFileNotFound()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => _verifier.Verify(Path.Combine(_root, "nowhere")));

        Assert.Contains("scripts/fetch-5e-database.sh", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_LoadedManifestButRootDoesNotExist_ReportsEveryFileMissing()
    {
        var manifest = ContentManifest.Load(Path.Combine(_root, ContentManifest.FileName));

        var result = _verifier.Verify(Path.Combine(_root, "nowhere"), manifest);

        Assert.False(result.IsIntact);
        Assert.Equal(["v7.0.0/2014/a.json", "v7.0.0/2024/b.json"], result.Missing);
        Assert.Empty(result.Extra);
        Assert.Empty(result.Mismatched);
        Assert.Contains("2 missing", result.Describe(), StringComparison.Ordinal);
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
