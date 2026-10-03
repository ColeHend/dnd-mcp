using DndMcp.Repository;

namespace DndMcp.Hosting;

/// <summary>
/// Where the server reads its shipped content and keeps its cache. Program.cs and the <c>srd-build</c> command use the
/// defaults; the integration tests override them.
///
/// <para>
/// This exists so no test ever touches the developer's real files. The in-memory harness runs the server inside the
/// test process, with the developer's environment and home directory: left at the defaults, every test run would read
/// and rewrite <c>~/.cache/dnd-mcp/srd.db</c>, and a test that points the server at broken content would break the
/// user's own index. <c>McpServerHarness</c> sets <see cref="CacheDirectory"/> and <see cref="DataDirectory"/> to
/// directories under the test output, and the index-failure tests set <see cref="ContentRoot"/> to an empty one.
/// </para>
/// </summary>
public sealed class DndMcpServerOptions
{
    /// <summary>
    /// The <c>content</c> directory (5e-database, the 2024 Rules Glossary, the licences). Defaults to <c>content/</c>
    /// next to the executable, where the build and the publish put it; a single-file publish keeps it on disk there.
    /// </summary>
    public string ContentRoot { get; set; } = Path.Combine(AppContext.BaseDirectory, "content");

    /// <summary>
    /// The directory srd.db lives in. Null means <see cref="DndMcpPaths"/> decides from the environment
    /// (<c>DND_MCP_CACHE_DIR</c>, <c>$XDG_CACHE_HOME/dnd-mcp</c>, <c>~/.cache/dnd-mcp</c>), resolved when first needed.
    /// </summary>
    public string? CacheDirectory { get; set; }

    /// <summary>
    /// The directory durable data lives in (campaigns.db, its backups, later its exports). Null means
    /// <see cref="DndMcpPaths"/> decides from the environment (<c>DND_MCP_DATA_DIR</c>, <c>$XDG_DATA_HOME/dnd-mcp</c>,
    /// <c>~/.local/share/dnd-mcp</c>). The test harness sets it under the test output: in-memory tests run with the
    /// developer's home directory and environment, and a server left to them would open the user's real campaigns. Set, it
    /// also decides campaigns.db (<c>&lt;DataDirectory&gt;/campaigns.db</c>), overriding <c>DND_MCP_DB</c>, for the same reason:
    /// a developer's exported <c>DND_MCP_DB</c> must never reach an in-memory test.
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>
    /// campaigns.db's full path, when a caller needs it somewhere other than <see cref="DataDirectory"/> (tests that share
    /// one database between two servers, as two Claude sessions do). Null means <see cref="ResolveCampaignDatabasePath"/>
    /// decides.
    /// </summary>
    public string? CampaignDatabase { get; set; }

    /// <summary>
    /// How long a rules tool waits for the index before giving up with a "still building" error. A build takes about
    /// a second, so this is only reached on a very slow machine or a stuck disk; it must stay under Claude Code's 30 s
    /// MCP request timeout so the model gets an explanation rather than a dropped call. Settable for tests only.
    /// </summary>
    internal TimeSpan IndexWaitTimeout { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// How long stopping the server waits for an index build still running (a session that ends during the warm-up build
    /// after an install). A build takes about a second; exiting under it lets the runtime kill the build thread before it
    /// cleans up, leaving a multi-megabyte <c>srd.db.&lt;pid&gt;.&lt;guid&gt;.tmp</c> in the user's cache. Bounded so a stuck
    /// disk cannot keep the process alive after Claude Code closes it. Settable for tests only.
    /// </summary>
    internal TimeSpan ShutdownBuildWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The clock campaigns.db stamps changes, backups and session times with. Settable for tests only (a fixed clock makes
    /// backup names and "first write of the day" deterministic).
    /// </summary>
    internal TimeProvider Time { get; set; } = TimeProvider.System;

    /// <summary>
    /// Where the environment says the cache is, used when <see cref="CacheDirectory"/> is not set. A function so tests can
    /// stand in an environment with no home directory (the fallback that must still give a working index) without
    /// touching the developer's real one. Settable for tests only.
    /// </summary>
    internal Func<DndMcpPaths> Paths { get; set; } = DndMcpPaths.FromEnvironment;

    /// <summary>srd.db's full path under <see cref="CacheDirectory"/>, or under the environment's cache directory.</summary>
    /// <exception cref="InvalidOperationException">No variable decides the cache path and there is no home directory.</exception>
    public string ResolveSrdDatabasePath() =>
        CacheDirectory is { Length: > 0 } directory
            ? Path.Combine(Path.GetFullPath(directory), DndMcpPaths.SrdDatabaseFileName)
            : Paths().SrdDatabasePath;

    /// <summary>
    /// <see cref="DataDirectory"/> as a full path, or the environment's data directory when it is not set. Phase 6 opens
    /// campaigns.db here, never through <see cref="DndMcpPaths"/> directly, so a test's override always applies.
    /// </summary>
    /// <exception cref="InvalidOperationException">No variable decides the data path and there is no home directory.</exception>
    public string ResolveDataDirectory() =>
        DataDirectory is { Length: > 0 } directory ? Path.GetFullPath(directory) : Paths().DataDirectory;

    /// <summary>
    /// campaigns.db's full path: <see cref="CampaignDatabase"/> when set; else <c>&lt;DataDirectory&gt;/campaigns.db</c> when
    /// <see cref="DataDirectory"/> is set; else the environment's (<c>DND_MCP_DB</c>, else the data directory's
    /// campaigns.db). Every campaign code path opens the database through this, never through <see cref="DndMcpPaths"/>
    /// directly, so a test's override always applies. Its backups live in a <c>backups</c> directory beside it.
    /// </summary>
    /// <exception cref="InvalidOperationException">No variable decides the data path and there is no home directory.</exception>
    public string ResolveCampaignDatabasePath() =>
        CampaignDatabase is { Length: > 0 } database ? Path.GetFullPath(database)
        : DataDirectory is { Length: > 0 } directory ? Path.Combine(Path.GetFullPath(directory), DndMcpPaths.CampaignDatabaseFileName)
        : Paths().CampaignDatabasePath;

    /// <summary>
    /// The environment overrides the path resolution ignored (a relative <c>DND_MCP_CACHE_DIR</c>, a <c>~</c> with no
    /// home directory), each as a sentence for the log. Without them a user whose setting silently had no effect would
    /// find the index somewhere else with no idea why. A variable whose directory is set explicitly here
    /// (<see cref="CacheDirectory"/>, <see cref="DataDirectory"/>) is not consulted, so its warning is left out; an
    /// explicit <see cref="DataDirectory"/> also decides campaigns.db (<c>&lt;DataDirectory&gt;/campaigns.db</c>), so
    /// <c>DND_MCP_DB</c>'s warning goes with it.
    /// </summary>
    public IReadOnlyList<string> PathWarnings()
    {
        var cacheSet = CacheDirectory is { Length: > 0 };
        var dataSet = DataDirectory is { Length: > 0 };
        var databaseSet = CampaignDatabase is { Length: > 0 };
        if (cacheSet && dataSet)
        {
            return [];
        }

        return Paths().Warnings
            .Where(w => !(cacheSet && w.StartsWith(DndMcpPaths.CacheDirectoryVariable, StringComparison.Ordinal)) &&
                        !(dataSet && w.StartsWith(DndMcpPaths.DataDirectoryVariable, StringComparison.Ordinal)) &&
                        !((dataSet || databaseSet) && w.StartsWith(DndMcpPaths.CampaignDatabaseVariable, StringComparison.Ordinal)))
            .ToList();
    }
}
