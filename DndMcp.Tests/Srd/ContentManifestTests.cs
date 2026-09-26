using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins what <see cref="ContentManifest.Parse"/> accepts. The verifier opens every path a manifest lists, so a path
/// that climbs out of the content directory, a malformed hash or a misspelt field must be rejected when the manifest
/// is loaded. It must not end up as a file read outside the tree or an entry that pins nothing.
///
/// <para>
/// Also pins <see cref="ContentManifest.ComputeFingerprint"/>, the key Phase 2 uses to decide whether srd.db is
/// stale. It must change whenever any pinned byte changes, and must not change when the manifest is merely
/// reformatted or reordered.
/// </para>
/// </summary>
public sealed class ContentManifestTests
{
    private const string ShaA = "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03";
    private const string ShaB = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static string Manifest(string filesJson, string tag = "5e-database-v7.0.0") =>
        $$"""{"tag":"{{tag}}","repository":"https://github.com/5e-bits/5e-srd-api","source_path":"packages/5e-database/src/{edition}/en","files":[{{filesJson}}]}""";

    private static string Entry(string path, string sha = ShaA, long bytes = 6) =>
        $$"""{"path":"{{path}}","sha256":"{{sha}}","bytes":{{bytes}}}""";

    [Fact]
    public void Parse_WellFormedManifest_ReadsEveryField()
    {
        var manifest = ContentManifest.Parse(Manifest(Entry("v7.0.0/2014/a.json") + "," + Entry("v7.0.0/2024/b.json", ShaB, 0)));

        Assert.Equal("5e-database-v7.0.0", manifest.Tag);
        Assert.Equal("https://github.com/5e-bits/5e-srd-api", manifest.Repository);
        Assert.Equal("packages/5e-database/src/{edition}/en", manifest.SourcePath);
        Assert.Collection(
            manifest.Files,
            f =>
            {
                Assert.Equal("v7.0.0/2014/a.json", f.Path);
                Assert.Equal(ShaA, f.Sha256);
                Assert.Equal(6, f.Bytes);
            },
            f =>
            {
                Assert.Equal("v7.0.0/2024/b.json", f.Path);
                Assert.Equal(ShaB, f.Sha256);
                Assert.Equal(0, f.Bytes);
            });
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("v7.0.0/../../outside.json")]
    [InlineData("/etc/passwd")]
    [InlineData("v7.0.0\\\\2014\\\\a.json")]
    [InlineData("C:/content/a.json")]
    [InlineData("./v7.0.0/a.json")]
    [InlineData("v7.0.0//a.json")]
    [InlineData("v7.0.0/2014/")]
    [InlineData("")]
    public void Parse_UnsafePath_ThrowsNamingThePath(string path)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(Manifest(Entry(path))));

        Assert.Contains("must be relative to content/5e-database", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("5891B5B522D5DF086D0FF0B110FBD9D21BB4FC7163AF34D08286A2E846F6BE03")]
    [InlineData("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be0")]
    [InlineData("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be033")]
    [InlineData("zz91b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03")]
    [InlineData("")]
    public void Parse_MalformedSha256_Throws(string sha)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(Manifest(Entry("v7.0.0/a.json", sha))));

        Assert.Contains("64 lowercase hex characters", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NegativeSize_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(Manifest(Entry("v7.0.0/a.json", bytes: -1))));

        Assert.Contains("negative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DuplicatePath_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            ContentManifest.Parse(Manifest(Entry("v7.0.0/a.json") + "," + Entry("v7.0.0/a.json", ShaB))));

        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NoFiles_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(Manifest(string.Empty)));

        Assert.Contains("lists no files", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A misspelt field must not be skipped: the entry would then pin nothing.
    [InlineData("""{"tag":"t","repository":"r","source_path":"s","files":[{"path":"a.json","sha265":"x","bytes":1}]}""")]
    [InlineData("""{"tag":"t","repository":"r","source_path":"s","generated_at":"2026-09-26","files":[]}""")]
    // Missing required fields.
    [InlineData("""{"repository":"r","source_path":"s","files":[]}""")]
    [InlineData("""{"tag":"t","repository":"r","source_path":"s","files":[{"path":"a.json","bytes":1}]}""")]
    // Nulls where a value is required.
    [InlineData("""{"tag":null,"repository":"r","source_path":"s","files":[]}""")]
    // Not a manifest at all.
    [InlineData("""[]""")]
    [InlineData("""{"tag":""")]
    public void Parse_NotAValidManifestDocument_ThrowsInvalidData(string json)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(json));

        Assert.StartsWith("The content manifest is not valid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_JsonNull_ThrowsInvalidData()
    {
        Assert.Throws<InvalidDataException>(() => ContentManifest.Parse("null"));
    }

    // RespectNullableAnnotations does not cover list elements, so a null entry reaches validation. It must be the
    // documented InvalidDataException, which the Phase 2 bootstrapper catches, and not a NullReferenceException.
    [Theory]
    [InlineData("null")]
    [InlineData("null," + """{"path":"v7.0.0/a.json","sha256":"5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03","bytes":6}""")]
    public void Parse_NullFileEntry_ThrowsInvalidData(string filesJson)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(Manifest(filesJson)));

        Assert.Contains("null entry", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_BlankTag_Throws(string tag)
    {
        Assert.Throws<InvalidDataException>(() => ContentManifest.Parse(Manifest(Entry("v7.0.0/a.json"), tag)));
    }

    [Fact]
    public void ComputeFingerprint_ReorderedOrReformattedManifest_IsUnchanged()
    {
        var sorted = ContentManifest.Parse(Manifest(Entry("v7.0.0/2014/a.json") + "," + Entry("v7.0.0/2024/b.json", ShaB, 0)));
        var reordered = ContentManifest.Parse(
            "{\n  \"files\": [" + Entry("v7.0.0/2024/b.json", ShaB, 0) + ",\n " + Entry("v7.0.0/2014/a.json") + "],\n" +
            "  \"source_path\": \"packages/5e-database/src/{edition}/en\",\n  \"repository\": \"https://github.com/5e-bits/5e-srd-api\",\n" +
            "  \"tag\": \"5e-database-v7.0.0\"\n}\n");

        Assert.Equal(sorted.ComputeFingerprint(), reordered.ComputeFingerprint());
        Assert.Matches("^[0-9a-f]{64}$", sorted.ComputeFingerprint());
    }

    [Theory]
    [InlineData("v7.0.0/2014/a.json", ShaB, 6, "5e-database-v7.0.0")]     // content changed
    [InlineData("v7.0.0/2014/a.json", ShaA, 7, "5e-database-v7.0.0")]     // size changed
    [InlineData("v7.0.0/2014/renamed.json", ShaA, 6, "5e-database-v7.0.0")] // file renamed
    [InlineData("v7.0.0/2014/a.json", ShaA, 6, "5e-database-v7.0.1")]     // re-tagged
    public void ComputeFingerprint_AnyPinnedValueChanged_Changes(string path, string sha, long bytes, string tag)
    {
        var baseline = ContentManifest.Parse(Manifest(Entry("v7.0.0/2014/a.json", ShaA, 6)));
        var changed = ContentManifest.Parse(Manifest(Entry(path, sha, bytes), tag));

        Assert.NotEqual(baseline.ComputeFingerprint(), changed.ComputeFingerprint());
    }
}
