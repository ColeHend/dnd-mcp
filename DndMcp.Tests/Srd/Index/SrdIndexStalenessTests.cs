using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// When srd.db is reused, when it is rebuilt, and what a build refuses. srd.db is a disposable cache that every server
/// start either reuses or rebuilds, so both failure directions matter: a stale index reused after the content changed
/// answers from old data with nothing to say so, and a current index rebuilt on every start costs every Claude session
/// its first rules call.
///
/// <para>
/// Each test works on a private copy of the real content (<see cref="SrdContentCopy"/>) and builds real indexes; the
/// key-derivation tests at the top need no build at all.
/// </para>
/// </summary>
public sealed class SrdIndexStalenessTests : IDisposable
{
    private readonly SrdContentCopy _copy = new();

    public void Dispose() => _copy.Dispose();

    // Each input moves the key on its own, and the labels keep a fingerprint from standing in for a glossary hash.
    [Theory]
    [InlineData(2, "fingerprint", "glossary", "corrections")]
    [InlineData(1, "fingerprint2", "glossary", "corrections")]
    [InlineData(1, "fingerprint", "glossary2", "corrections")]
    [InlineData(1, "fingerprint", "glossary", "corrections2")]
    [InlineData(1, "glossary", "fingerprint", "corrections")]
    [InlineData(1, "fingerprint", "corrections", "glossary")]
    public void ComputeStalenessKey_AnyInputChanged_ChangesTheKey(int schema, string fingerprint, string glossary, string corrections)
    {
        var baseline = SrdIndexContent.ComputeStalenessKey(1, "fingerprint", "glossary", "corrections");

        Assert.NotEqual(baseline, SrdIndexContent.ComputeStalenessKey(schema, fingerprint, glossary, corrections));
        Assert.Equal(baseline, SrdIndexContent.ComputeStalenessKey(1, "fingerprint", "glossary", "corrections"));
        Assert.Matches("^[0-9a-f]{64}$", baseline);
    }

    [Fact]
    public void Load_RealContent_KeyIsDerivedFromSchemaFingerprintGlossaryAndCorrections()
    {
        var content = SrdIndexContent.Load(_copy.ContentRoot);
        var corrections = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_copy.CorrectionsPath)));

        Assert.Equal(RulesGlossaryTests.PinnedSha256, content.GlossarySha256);
        Assert.Equal(corrections, content.CorrectionsSha256);
        Assert.Equal(SrdTestContent.Manifest.ComputeFingerprint(), content.ContentFingerprint);
        Assert.Equal(
            SrdIndexContent.ComputeStalenessKey(SrdIndexSchema.Version, content.ContentFingerprint, RulesGlossaryTests.PinnedSha256, corrections),
            content.StalenessKey);
    }

    // Editing a correction must rebuild srd.db on the next start, or the fix never reaches an installed index.
    [Fact]
    public void Load_CorrectionsBytesChanged_ChangesTheKey()
    {
        var before = SrdIndexContent.Load(_copy.ContentRoot).StalenessKey;
        File.AppendAllText(_copy.CorrectionsPath, "\n");

        Assert.NotEqual(before, SrdIndexContent.Load(_copy.ContentRoot).StalenessKey);
    }

    // The corrections ship with the content like the glossary; without them known-corrupt upstream text would be
    // served unlabelled, so a missing or broken file refuses like a missing glossary does.
    [Fact]
    public void Load_MissingCorrections_FailsNamingThePath()
    {
        File.Delete(_copy.CorrectionsPath);

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexContent.Load(_copy.ContentRoot));

        Assert.StartsWith($"No SRD corrections file at {_copy.CorrectionsPath}.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MalformedCorrections_FailsNamingTheProblem()
    {
        File.WriteAllText(_copy.CorrectionsPath, """{"corrections": [{"ref": "2024/nonsense/x", "set": {"desc": "x"}, "reason": "r", "source": "s"}]}""");

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexContent.Load(_copy.ContentRoot));

        Assert.StartsWith($"Cannot use the SRD corrections at {_copy.CorrectionsPath}: srd-corrections.json: \"2024/nonsense/x\" is not a ref", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_GlossaryBytesChanged_ChangesTheKey()
    {
        var before = SrdIndexContent.Load(_copy.ContentRoot).StalenessKey;
        File.AppendAllText(_copy.GlossaryPath, "\n");

        var after = SrdIndexContent.Load(_copy.ContentRoot);

        Assert.NotEqual(before, after.StalenessKey);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_copy.GlossaryPath))), after.GlossarySha256);
    }

    [Fact]
    public void Load_ManifestFingerprintChanged_ChangesTheKey()
    {
        var before = SrdIndexContent.Load(_copy.ContentRoot).StalenessKey;
        _copy.RewriteManifest(manifest => manifest["tag"] = "5e-database-v7.0.1");

        Assert.NotEqual(before, SrdIndexContent.Load(_copy.ContentRoot).StalenessKey);
    }

    // The fingerprint ignores formatting; hashing the raw manifest bytes instead would rebuild on a harmless reformat.
    [Fact]
    public void Load_ManifestOnlyReformatted_KeepsTheKey()
    {
        var before = SrdIndexContent.Load(_copy.ContentRoot).StalenessKey;
        _copy.RewriteManifest(_ => { }, indented: false);

        Assert.Equal(before, SrdIndexContent.Load(_copy.ContentRoot).StalenessKey);
    }

    [Fact]
    public void OpenOrBuild_NoIndex_BuildsAndThenReusesWithoutRewriting()
    {
        var first = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath);
        var builtAt = first.Index.Info.BuiltAtUtc;
        first.Index.Dispose();
        var bytes = SHA256.HashData(File.ReadAllBytes(_copy.DatabasePath));

        using var second = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath).Index;

        Assert.True(first.Rebuilt);
        Assert.StartsWith("no index at ", first.Reason, StringComparison.Ordinal);
        Assert.Equal(builtAt, second.Info.BuiltAtUtc);
        Assert.Equal(bytes, SHA256.HashData(File.ReadAllBytes(_copy.DatabasePath)));
        Assert.NotNull(second.Get("2024", "spell", "fireball"));
    }

    [Fact]
    public void OpenOrBuild_CurrentIndex_ReportsReuse()
    {
        SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath).Index.Dispose();

        var result = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath);
        using var index = result.Index;

        Assert.False(result.Rebuilt);
        Assert.Equal("srd.db is current", result.Reason);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void OpenOrBuild_GlossaryChangedSinceTheBuild_RebuildsFromTheNewContent()
    {
        SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath).Index.Dispose();
        File.AppendAllText(_copy.GlossaryPath, "\n");

        var result = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath);
        using var index = result.Index;

        Assert.True(result.Rebuilt);
        Assert.Contains("staleness key differs", result.Reason, StringComparison.Ordinal);
        Assert.Equal(SrdIndexContent.Load(_copy.ContentRoot).StalenessKey, index.Info.StalenessKey);
        Assert.NotEqual(RulesGlossaryTests.PinnedSha256, index.Info.GlossarySha256);
    }

    [Fact]
    public void OpenOrBuild_ForceRebuild_RebuildsACurrentIndex()
    {
        SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath).Index.Dispose();

        var result = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath, forceRebuild: true);
        using var index = result.Index;

        Assert.True(result.Rebuilt);
        Assert.Equal("rebuild requested", result.Reason);
    }

    [Fact]
    public void OpenOrBuild_GarbageInSrdDb_RebuildsAWorkingIndex()
    {
        Directory.CreateDirectory(_copy.CacheDirectory);
        File.WriteAllText(_copy.DatabasePath, new string('x', 8192));

        Assert.Null(SrdIndex.TryOpen(_copy.DatabasePath, SrdIndexContent.Load(_copy.ContentRoot).StalenessKey, out var reason));
        Assert.Contains("is not a readable index", reason, StringComparison.Ordinal);

        var result = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath);
        using var index = result.Index;

        Assert.True(result.Rebuilt);
        Assert.Contains("is not a readable index", result.Reason, StringComparison.Ordinal);
        Assert.Equal("Fireball", index.Get("2014", "spell", "fireball")!.Name);
    }

    [Fact]
    public void Build_AbandonedTempFilesBeside_DeletesOnlyThoseOlderThanTheCutoff()
    {
        var directory = Path.GetDirectoryName(_copy.DatabasePath)!;
        Directory.CreateDirectory(directory);
        var old = Path.Combine(directory, "srd.db.4242.0123456789abcdef.tmp");
        var oldJournal = old + "-journal";
        var fresh = Path.Combine(directory, "srd.db.4343.fedcba9876543210.tmp");
        var freshJournal = fresh + "-journal";
        var unrelated = Path.Combine(directory, "notes.tmp");
        foreach (var path in new[] { old, oldJournal, fresh, freshJournal, unrelated })
        {
            File.WriteAllText(path, "x");
        }

        var longAgo = DateTime.UtcNow - SrdIndexBuilder.AbandonedTempFileAge - TimeSpan.FromMinutes(1);
        File.SetLastWriteTimeUtc(old, longAgo);
        File.SetLastWriteTimeUtc(oldJournal, longAgo);
        File.SetLastWriteTimeUtc(freshJournal, longAgo);
        File.SetLastWriteTimeUtc(unrelated, longAgo);

        SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(oldJournal), "An abandoned temp file's journal goes with it.");
        Assert.True(File.Exists(fresh), "A young temp file may be another server's build in progress.");
        Assert.True(File.Exists(freshJournal), "A journal is judged by its temp file, never by its own age.");
        Assert.True(File.Exists(unrelated), "Only the target's own temp files are ours to delete.");
    }

    /// <summary>
    /// A live build's rollback journal appears and disappears with every statement, so a journal is never deleted on
    /// its own: its age says nothing (a journal that vanished between the listing and the age check reads as
    /// 1601-01-01, "very old"), and deleting a live one fails that build with SQLite's disk I/O error. Only an old temp
    /// database takes its journal with it. This journal has no temp file beside it at all: it is left alone.
    /// </summary>
    [Fact]
    public void Build_JournalWithoutItsTempFile_IsNeverDeleted()
    {
        Directory.CreateDirectory(_copy.CacheDirectory);
        var journal = Path.Combine(_copy.CacheDirectory, "srd.db.4545.00112233445566778899aabbccddeeff.tmp-journal");
        File.WriteAllText(journal, "x");
        File.SetLastWriteTimeUtc(journal, DateTime.UtcNow - SrdIndexBuilder.AbandonedTempFileAge - TimeSpan.FromHours(1));

        SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);

        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void TryOpen_Missing_ReturnsNullWithReason()
    {
        Assert.Null(SrdIndex.TryOpen(_copy.DatabasePath, "key", out var reason));
        Assert.Equal($"no index at {Path.GetFullPath(_copy.DatabasePath)}", reason);
    }

    // SQLite treats an empty file as an empty database: user_version 0, so it is stale rather than unreadable.
    [Fact]
    public void TryOpen_EmptyFile_ReturnsNullAsAnotherSchemaVersion()
    {
        Directory.CreateDirectory(_copy.CacheDirectory);
        File.WriteAllBytes(_copy.DatabasePath, []);

        Assert.Null(SrdIndex.TryOpen(_copy.DatabasePath, "key", out var reason));
        Assert.Equal($"schema version 0, expected {SrdIndexSchema.Version}", reason);
    }

    [Fact]
    public void TryOpen_OtherKey_ReturnsNull()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);

        Assert.Null(SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey.Replace('a', 'b'), out var reason));
        Assert.Contains("staleness key differs", reason, StringComparison.Ordinal);
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey);
        Assert.NotNull(index);
    }

    [Fact]
    public void TryOpen_OtherSchemaVersion_ReturnsNull()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        using (var connection = new SqliteConnection($"Data Source={_copy.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        Assert.Null(SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey, out var reason));
        Assert.Equal($"schema version 99, expected {SrdIndexSchema.Version}", reason);
    }

    // A half-copied or half-written file: the header survives, the pages it points at do not.
    [Fact]
    public void TryOpen_TruncatedFile_ReturnsNull()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        var bytes = File.ReadAllBytes(_copy.DatabasePath);
        File.WriteAllBytes(_copy.DatabasePath, bytes[..(bytes.Length / 3)]);

        Assert.Null(SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey, out var reason));
        Assert.NotEqual("current", reason);
    }

    /// <summary>
    /// A damaged page deep in the file, with the header, schema version and meta all intact: every check but the
    /// structural one passes, and without it the damage would surface later as a query error inside a tool call.
    /// </summary>
    [Fact]
    public void TryOpen_DamagedPageBehindAnIntactHeader_ReturnsNullAndIsRebuilt()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        var bytes = File.ReadAllBytes(_copy.DatabasePath);
        var pageSize = (bytes[16] << 8) | bytes[17];
        var page = bytes.Length / pageSize / 2;
        bytes.AsSpan(page * pageSize, pageSize).Fill(0xFF);
        File.WriteAllBytes(_copy.DatabasePath, bytes);

        Assert.Null(SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey, out var reason));
        Assert.StartsWith("damaged (*** in database main *** ", reason, StringComparison.Ordinal);

        var result = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath);
        using var index = result.Index;
        Assert.True(result.Rebuilt);
    }

    [Fact]
    public void Build_TamperedVendoredFile_RefusesWithTheVerifiersDescription()
    {
        var relative = SrdContentCopy.RelativePath("2014", SrdFileNames.Spells);
        var path = _copy.FullPath(relative);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Fireball\"", "\"Firebolt\"", StringComparison.Ordinal));
        var expected = new ContentManifestVerifier().Verify(Path.Combine(_copy.ContentRoot, "5e-database")).Describe();

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        Assert.Equal($"{expected} {SrdIndexBuilder.ReinstallHint}", error.Message);
        Assert.Contains(relative + ": same size", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_copy.CacheDirectory) && Directory.EnumerateFileSystemEntries(_copy.CacheDirectory).Any());
    }

    [Fact]
    public void Build_MissingVendoredFile_Refuses()
    {
        var relative = SrdContentCopy.RelativePath("2024", SrdFileNames.Monsters);
        File.Delete(_copy.FullPath(relative));

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        Assert.Contains($"1 missing ({relative})", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A file the manifest does not list cannot change the index: the builder reads only the files the manifest pins.
    /// Refusing over one disabled rules lookup for good, and the commonest cause is harmless: <c>dotnet publish</c> into
    /// the install directory never deletes the previous version's <c>content/5e-database/v7.0.0/</c>, and macOS adds
    /// <c>.DS_Store</c>. So extras build, with a warning naming them; missing or changed pinned files still refuse.
    /// </summary>
    [Fact]
    public void Build_UnlistedFilesInTheTree_BuildsAndWarnsNamingThem()
    {
        var stale = Path.Combine(_copy.ContentRoot, "5e-database", "v6.0.0", "2014");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, SrdFileNames.Spells), "[]");
        File.WriteAllText(Path.Combine(_copy.ContentRoot, "5e-database", ".DS_Store"), "x");

        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);

        var warning = Assert.Single(summary.Warnings, w => w.Contains("not in the content manifest", StringComparison.Ordinal));
        Assert.Contains($"(.DS_Store, v6.0.0/2014/{SrdFileNames.Spells})", warning, StringComparison.Ordinal);
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey);
        Assert.Equal("Fireball", index!.Get("2014", "spell", "fireball")!.Name);
    }

    [Fact]
    public void OpenOrBuild_UnlistedFileInTheTree_ReportsTheWarning()
    {
        File.WriteAllText(Path.Combine(_copy.ContentRoot, "5e-database", "stray.json"), "[]");

        var result = SrdIndexOpener.OpenOrBuild(_copy.ContentRoot, _copy.DatabasePath);
        using var index = result.Index;

        Assert.True(result.Rebuilt);
        Assert.Contains(result.Warnings, w => w.Contains("(stray.json)", StringComparison.Ordinal));
    }

    /// <summary>
    /// A record the verifier accepts (re-pinned) but the builder cannot index fails the build with its file and
    /// position, AFTER the temp file exists, and the failure leaves the previous srd.db byte-for-byte intact and no
    /// temp file behind.
    /// </summary>
    [Fact]
    public void Build_RecordWithoutIndex_FailsNamingFileAndPositionAndKeepsThePreviousIndex()
    {
        SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        var before = SHA256.HashData(File.ReadAllBytes(_copy.DatabasePath));
        _copy.RewriteRecords("2014", SrdFileNames.Spells, records => records[3]!.AsObject().Remove("index"));

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        Assert.StartsWith(
            $"{SrdContentCopy.RelativePath("2014", SrdFileNames.Spells)} at $[3] has no string \"index\", so it cannot be indexed.",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(_copy.DatabasePath)));
        Assert.Equal(["srd.db"], Directory.EnumerateFileSystemEntries(_copy.CacheDirectory).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("url", "has no string \"url\"")]
    [InlineData("name", "has no string \"name\"")]
    public void Build_RecordWithoutRequiredField_FailsNamingIt(string field, string expected)
    {
        _copy.RewriteRecords("2024", SrdFileNames.Monsters, records => records[10]!.AsObject().Remove(field));

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        Assert.StartsWith($"{SrdContentCopy.RelativePath("2024", SrdFileNames.Monsters)} at $[10] {expected}", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_copy.CacheDirectory));
    }

    // 2014 level names are derived from the class; a level without one cannot be named or placed in a table.
    [Fact]
    public void Build_LevelWithoutClass_Fails()
    {
        _copy.RewriteRecords("2014", "5e-SRD-Levels.json", records => records[0]!.AsObject().Remove("class"));

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        Assert.StartsWith($"{SrdContentCopy.RelativePath("2014", "5e-SRD-Levels.json")} at $[0] has no \"class\" object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_DuplicateSlug_FailsNamingBothPositions()
    {
        _copy.RewriteRecords("2024", SrdFileNames.Spells, records => records[1]!["index"] = (string)records[0]!["index"]!);

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        var file = SrdContentCopy.RelativePath("2024", SrdFileNames.Spells);
        Assert.Contains($"{file} at $[1]: slug \"acid-arrow\" repeats {file} at $[0]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GlossaryRecordWithoutName_FailsNamingThePosition()
    {
        var glossary = JsonNode.Parse(File.ReadAllText(_copy.GlossaryPath))!.AsArray();
        glossary[5]!.AsObject().Remove("name");
        File.WriteAllText(_copy.GlossaryPath, glossary.ToJsonString());

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        Assert.StartsWith("rules-glossary-2024.json at $[5] has no string \"name\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_GlossaryNotJson_Fails()
    {
        File.WriteAllText(_copy.GlossaryPath, "[{\"name\": ");

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath));

        Assert.StartsWith("rules-glossary-2024.json is not valid JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingGlossary_FailsNamingThePath()
    {
        File.Delete(_copy.GlossaryPath);

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexContent.Load(_copy.ContentRoot));

        Assert.StartsWith($"No 2024 Rules Glossary at {_copy.GlossaryPath}.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingManifest_FailsNamingThePath()
    {
        File.Delete(_copy.ManifestPath);

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexContent.Load(_copy.ContentRoot));

        Assert.StartsWith($"No content manifest at {_copy.ManifestPath}.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingContentDirectory_SaysToCopyThePublishDirectory()
    {
        var missing = Path.Combine(_copy.RootPath, "nowhere");

        var error = Assert.Throws<SrdIndexUnavailableException>(() => SrdIndexContent.Load(missing));

        Assert.Equal(
            $"No SRD content directory at {missing}. The server reads content/ next to its executable; " +
            "copy the whole publish directory, not just the binary.",
            error.Message);
    }

    // One server per Claude session: several may build at once. Each writes its own temp file; the last rename wins.
    [Fact]
    public async Task Build_ConcurrentBuildsIntoOneTarget_AllSucceedAndLeaveOnlySrdDb()
    {
        var content = SrdIndexContent.Load(_copy.ContentRoot);

        var summaries = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => SrdIndexBuilder.Build(content, _copy.DatabasePath))));

        Assert.All(summaries, s => Assert.Equal(content.StalenessKey, s.StalenessKey));
        Assert.Equal(["srd.db"], Directory.EnumerateFileSystemEntries(_copy.CacheDirectory).Select(Path.GetFileName));
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, content.StalenessKey);
        Assert.NotNull(index);
    }

    [Fact]
    public void Index_Disposed_Throws()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey)!;
        index.Dispose();

        Assert.Throws<ObjectDisposedException>(() => index.Get("2024", "spell", "fireball"));
    }
}
