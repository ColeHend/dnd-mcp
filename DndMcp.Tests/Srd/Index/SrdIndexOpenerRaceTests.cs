using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// Two dnd-mcp versions sharing one cache and starting together: the installed server beside a development
/// registration without its own cache directory, or two Claude sessions on different versions. Their content (and so
/// their staleness keys) differ, both build, and each renames its build over srd.db.
///
/// <para>
/// What breaks: the loser's open right after its own build met the other version's file and failed, and the first
/// rules call of that session got "…was built but could not be opened… Restart the server once only one version is
/// running", advice the model cannot follow. The open must retry (the other version's file is now in place, and one
/// more build makes this version's current again) and must never ask for a restart.
/// </para>
/// </summary>
public sealed class SrdIndexOpenerRaceTests : IDisposable
{
    private const int Rounds = 8;

    private readonly SrdContentCopy _versionA = new();
    private readonly SrdContentCopy _versionB = new();

    public SrdIndexOpenerRaceTests()
    {
        // One more newline in the glossary: a different glossary hash, so a different staleness key, as for two versions.
        File.AppendAllText(_versionB.GlossaryPath, "\n");
    }

    public void Dispose()
    {
        _versionA.Dispose();
        _versionB.Dispose();
    }

    [Fact]
    public async Task OpenOrBuild_TwoVersionsBuildingOneCacheAtOnce_BothOpenTheirOwnBuild()
    {
        var target = _versionA.DatabasePath;
        var keyA = SrdIndexContent.Load(_versionA.ContentRoot).StalenessKey;
        var keyB = SrdIndexContent.Load(_versionB.ContentRoot).StalenessKey;
        Assert.NotEqual(keyA, keyB);
        var failures = new List<string>();

        for (var round = 0; round < Rounds; round++)
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            using var start = new ManualResetEventSlim(false);
            var a = Task.Run(() => Open(_versionA.ContentRoot, target, start));
            var b = Task.Run(() => Open(_versionB.ContentRoot, target, start));
            start.Set();
            var results = await Task.WhenAll(a, b);

            if (results[0] != $"key:{keyA}")
            {
                failures.Add($"round {round}, version A: {results[0]}");
            }

            if (results[1] != $"key:{keyB}")
            {
                failures.Add($"round {round}, version B: {results[1]}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    // "key:<the opened index's key>", or the exception's message.
    private static string Open(string contentRoot, string target, ManualResetEventSlim start)
    {
        start.Wait();
        try
        {
            using var index = SrdIndexOpener.OpenOrBuild(contentRoot, target).Index;
            return $"key:{index.Info.StalenessKey}";
        }
        catch (SrdIndexUnavailableException ex)
        {
            return ex.Message;
        }
    }
}
