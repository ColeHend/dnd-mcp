using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// A private, writable copy of the shipped <c>content/</c> directory (plus a <c>cache/</c> directory beside it for
/// srd.db), deleted on Dispose. Tests damage or change the copy to see what the builder and the staleness key do, never
/// the real content.
///
/// <para>
/// <see cref="RewriteRecords"/> edits a vendored file AND re-pins it in the manifest, producing a tree the verifier
/// accepts. That is the only way to reach the builder's own record checks: an edit without the re-pin is refused
/// earlier, by the verifier, which is a different test.
/// </para>
/// </summary>
public sealed class SrdContentCopy : IDisposable
{
    public SrdContentCopy()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "dnd-mcp-srd-content-" + Guid.NewGuid().ToString("N"));
        ContentRoot = Path.Combine(RootPath, "content");
        CacheDirectory = Path.Combine(RootPath, "cache");
        CopyDirectory(SrdIndexFixture.ContentRoot, ContentRoot);
    }

    public string RootPath { get; }

    public string ContentRoot { get; }

    public string CacheDirectory { get; }

    public string DatabasePath => Path.Combine(CacheDirectory, "srd.db");

    public string GlossaryPath => Path.Combine(ContentRoot, SrdKinds.RulesGlossary2024FileName);

    public string CorrectionsPath => Path.Combine(ContentRoot, SrdCorrections.FileName);

    public string ManifestPath => Path.Combine(ContentRoot, "5e-database", ContentManifest.FileName);

    /// <summary>The manifest-relative path of a vendored file, e.g. <c>v7.0.0/2014/5e-SRD-Spells.json</c>.</summary>
    public static string RelativePath(string edition, string fileName) => $"{SrdTestContent.VersionDirectory}/{edition}/{fileName}";

    public string FullPath(string relativePath) =>
        Path.Combine(ContentRoot, "5e-database", relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Applies <paramref name="edit"/> to a vendored file's records, writes it, and re-pins it in the manifest.</summary>
    public void RewriteRecords(string edition, string fileName, Action<JsonArray> edit)
    {
        var relative = RelativePath(edition, fileName);
        var records = JsonNode.Parse(File.ReadAllBytes(FullPath(relative)))!.AsArray();
        edit(records);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(FullPath(relative), bytes);
        Repin(relative, bytes);
    }

    /// <summary>
    /// Replaces <c>srd-corrections.json</c> with <paramref name="corrections"/>, each entry an object literal
    /// (<c>{"ref": …, "set": {…}, "reason": …, "source": …}</c>), so a test controls exactly which corrections apply.
    /// </summary>
    public void WriteCorrections(params string[] corrections) =>
        File.WriteAllText(CorrectionsPath, $$"""{"corrections": [{{string.Join(",\n", corrections)}}]}""");

    /// <summary>Rewrites the manifest through <paramref name="edit"/> (whole-document edits such as the tag).</summary>
    public void RewriteManifest(Action<JsonObject> edit, bool indented = true)
    {
        var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
        edit(manifest);
        File.WriteAllText(ManifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private void Repin(string relativePath, byte[] bytes)
    {
        RewriteManifest(manifest =>
        {
            var entry = Assert.Single(manifest["files"]!.AsArray(), f => (string)f!["path"]! == relativePath)!;
            entry["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            entry["bytes"] = bytes.LongLength;
        });
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}

/// <summary>
/// A <c>[Fact]</c> that runs only where an open file can be renamed over. Windows refuses to replace a file another
/// handle has open, so the behaviour those tests pin (a server keeps reading the srd.db it opened while another server
/// renames a new one into place) cannot happen there, and the test would fail for a reason unrelated to the code.
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Windows cannot rename a file over one that is open.";
        }
    }
}
