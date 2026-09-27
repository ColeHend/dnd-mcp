using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdIndex"/>'s connection pool once srd.db changes under a running server. The README tells users that
/// deleting the cache is always safe, and a newer dnd-mcp in another Claude session rebuilds srd.db by renaming a new
/// file over it. Before, an overlapping call opened a fresh connection by path, found the file gone or different, and
/// failed: Claude's routine parallel rules calls then failed all but one at a time, for the rest of the session.
///
/// <para>
/// Every connection is now opened, and checked, while the validated file is in place; on Unix each keeps reading that
/// file whatever happens to the path. Parallel calls wait for a connection instead of opening one.
/// </para>
/// </summary>
public sealed class QueryPoolTests : IDisposable
{
    private const int ParallelCalls = 24;

    private readonly SrdContentCopy _copy = new();

    public void Dispose() => _copy.Dispose();

    [UnixOnlyFact]
    public async Task Index_SrdDbDeletedWhileRunning_EveryParallelCallStillAnswers()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey)!;
        File.Delete(_copy.DatabasePath);

        var answers = await RunInParallel(index);

        Assert.All(answers, a => Assert.Equal("Fireball|2024/spell/fireball|2024/spell/fireball", a));
        Assert.False(File.Exists(_copy.DatabasePath));
    }

    /// <summary>
    /// Another build, from other content, is renamed over srd.db. This index keeps answering, and from the file it
    /// validated (Fireball is still "Fireball"), never from rows it did not verify.
    /// </summary>
    [UnixOnlyFact]
    public async Task Index_SrdDbReplacedByADifferentBuild_EveryParallelCallAnswersFromTheValidatedFile()
    {
        var original = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, original.StalenessKey)!;
        _copy.RewriteRecords("2024", "5e-SRD-Spells.json", records =>
        {
            foreach (var record in records)
            {
                if ((string?)record!["index"] == "fireball")
                {
                    record["name"] = "Replaced Fireball";
                }
            }
        });
        var otherPath = Path.Combine(_copy.RootPath, "other", "srd.db");
        var other = SrdIndexBuilder.Build(_copy.ContentRoot, otherPath);
        Assert.NotEqual(original.StalenessKey, other.StalenessKey);
        File.Move(otherPath, _copy.DatabasePath, overwrite: true);

        var answers = await RunInParallel(index);

        Assert.All(answers, a => Assert.Equal("Fireball|2024/spell/fireball|2024/spell/fireball", a));
        using var replacement = SrdIndex.TryOpen(_copy.DatabasePath, other.StalenessKey)!;
        Assert.Equal("Replaced Fireball", replacement.Get("2024", "spell", "fireball")!.Name);
    }

    // More overlapping calls than pooled connections: the extra ones wait their turn rather than failing.
    [Fact]
    public async Task Index_MoreParallelCallsThanConnections_AllAnswer()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey)!;

        var answers = await RunInParallel(index, calls: 64);

        Assert.Equal(64, answers.Count);
        Assert.All(answers, a => Assert.Equal("Fireball|2024/spell/fireball|2024/spell/fireball", a));
    }

    /// <summary>
    /// srd.db overwritten in place (not renamed over: the same file, so the open connections see it) becomes
    /// unreadable. That is <see cref="SrdIndexUnavailableException"/>, the signal for the host to drop this index and
    /// open or rebuild a current one, not a raw SQLite error that reaches the model as a bare tool failure.
    /// </summary>
    [UnixOnlyFact]
    public void Index_SrdDbDamagedInPlace_ThrowsSrdIndexUnavailableException()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey)!;
        using (var stream = new FileStream(_copy.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(new byte[8192]);
        }

        var error = Assert.Throws<SrdIndexUnavailableException>(() => index.Get("2024", "spell", "fireball"));

        Assert.StartsWith($"srd.db at {Path.GetFullPath(_copy.DatabasePath)} can no longer be read (", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            "It was changed or damaged while this server was running; the next rules call opens it again, rebuilding it if needed.",
            error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Restart", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// srd.db truncated in place (<c>: &gt; srd.db</c>, an interrupted copy over it): SQLite reads the now-empty schema and
    /// reports "no such table", a code that says nothing about the file. It must still be
    /// <see cref="SrdIndexUnavailableException"/>, or the host never reopens the index and every later rules call fails
    /// with a bare error until the server restarts.
    /// </summary>
    [UnixOnlyFact]
    public void Index_SrdDbTruncatedInPlace_ThrowsSrdIndexUnavailableException()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        using var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey)!;
        Assert.NotNull(index.Get("2024", "spell", "fireball"));
        using (var stream = new FileStream(_copy.DatabasePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.SetLength(0);
        }

        Assert.Throws<SrdIndexUnavailableException>(() => index.FindByName("Fireball", "2024"));
        Assert.Throws<SrdIndexUnavailableException>(() => index.Search("fireball", ["2024"]));
    }

    [Fact]
    public async Task Index_DisposedWhileCallsAreRunning_LaterCallsThrowObjectDisposed()
    {
        var summary = SrdIndexBuilder.Build(_copy.ContentRoot, _copy.DatabasePath);
        var index = SrdIndex.TryOpen(_copy.DatabasePath, summary.StalenessKey)!;
        var running = RunInParallel(index, calls: 64, tolerateDisposal: true);

        index.Dispose();
        await running;

        Assert.Throws<ObjectDisposedException>(() => index.Get("2024", "spell", "fireball"));
    }

    // Each call does what a rules_get by name does: look the name up, fetch by ref, search.
    private static async Task<IReadOnlyList<string>> RunInParallel(SrdIndex index, int calls = ParallelCalls, bool tolerateDisposal = false)
    {
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, calls).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try
            {
                var byName = index.FindByName("Fireball", "2024").Best!.Document;
                var byRef = index.Get("2024", "spell", "fireball")!;
                var searched = index.Search("fireball", ["2024"]).Hits[0];
                return $"{byName.Name}|{byRef.Ref}|{searched.Ref}";
            }
            catch (ObjectDisposedException) when (tolerateDisposal)
            {
                return "disposed";
            }
        })).ToList();
        start.Set();
        return await Task.WhenAll(tasks);
    }
}
