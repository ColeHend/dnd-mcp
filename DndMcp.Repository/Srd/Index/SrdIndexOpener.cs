using System.Diagnostics;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// "Give me a current index": reuse srd.db when its staleness key matches the content, otherwise build it, then open
/// it. The one entry point the host's index service and the <c>srd-build</c> command share, so both make the same
/// reuse decision and report it the same way.
///
/// <para>
/// Reuse is the normal case (every server start after the first); a rebuild happens after a re-vendor, a glossary
/// update, an importer change (<see cref="SrdIndexSchema.Version"/>) or when srd.db is missing or unreadable. The
/// result says which, and why, for the log line: a rebuild nobody can explain looks like a bug.
/// </para>
/// <para>
/// <b>Two versions, one cache.</b> Another dnd-mcp version sharing the cache (the installed server beside a
/// development registration, or two sessions on different versions) builds with another key and renames its build over
/// srd.db, possibly between this build's rename and its open. The open then finds the other version's file, which is
/// not a fault of either: this version builds once more, and its file is in place again. Only a second collision in a
/// row fails, and that says the next rules call will try again, never "restart the server": the model cannot restart
/// it, and the next call does recover.
/// </para>
/// </summary>
public static class SrdIndexOpener
{
    /// <summary>
    /// Opens the index at <paramref name="databasePath"/> for the content under <paramref name="contentRoot"/>,
    /// building it first when it is missing, stale, unreadable or <paramref name="forceRebuild"/> is set.
    /// </summary>
    /// <exception cref="SrdIndexUnavailableException">
    /// The content is missing or damaged (nothing can be built), or another process replaced srd.db with a different
    /// build between this build and its opening, twice in a row.
    /// </exception>
    /// <exception cref="IOException">The database directory cannot be created or written.</exception>
    /// <exception cref="UnauthorizedAccessException">The database directory cannot be created or written.</exception>
    public static SrdIndexOpenResult OpenOrBuild(string contentRoot, string databasePath, bool forceRebuild = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var stopwatch = Stopwatch.StartNew();
        var content = SrdIndexContent.Load(contentRoot);
        var path = Path.GetFullPath(databasePath);

        string reason;
        if (forceRebuild)
        {
            reason = "rebuild requested";
        }
        else if (SrdIndex.TryOpen(path, content.StalenessKey, out var whyNot) is { } current)
        {
            return new SrdIndexOpenResult(current, Rebuilt: false, "srd.db is current", [], stopwatch.Elapsed);
        }
        else
        {
            reason = whyNot;
        }

        var afterBuild = string.Empty;
        for (var attempt = 1; attempt <= BuildAttempts; attempt++)
        {
            var summary = SrdIndexBuilder.Build(content, path);
            if (SrdIndex.TryOpen(path, summary.StalenessKey, out afterBuild) is { } index)
            {
                return new SrdIndexOpenResult(index, Rebuilt: true, reason, summary.Warnings, stopwatch.Elapsed);
            }
        }

        throw new SrdIndexUnavailableException(
            $"srd.db at {path} was built but could not be opened ({afterBuild}); another dnd-mcp version sharing this cache " +
            "may be rebuilding it at the same time. The next rules call tries again. To stop the two versions taking " +
            $"turns, give one of them its own {DndMcpPaths.CacheDirectoryVariable}.");
    }

    // Builds (each followed by an open) before giving up: one more than a single collision with another version needs.
    private const int BuildAttempts = 2;
}

/// <summary>
/// An open index and how it was obtained. <see cref="Reason"/> says why it was rebuilt ("no index at …", "schema
/// version 0, expected 1", "built from different content…", "rebuild requested") or "srd.db is current".
/// <see cref="Warnings"/> are the build's (manual counterparts the content no longer has); empty when reused.
/// </summary>
public sealed record SrdIndexOpenResult(
    SrdIndex Index,
    bool Rebuilt,
    string Reason,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);
