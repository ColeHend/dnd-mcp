namespace DndMcp.Repository;

/// <summary>
/// Where the server keeps its files: the disposable cache (srd.db) and, from Phase 6, the data that must survive
/// (campaigns.db, backups, exports).
///
/// <para>
/// Resolution, first match wins:
/// <list type="bullet">
/// <item>Cache: <c>DND_MCP_CACHE_DIR</c>, else <c>$XDG_CACHE_HOME/dnd-mcp</c>, else <c>~/.cache/dnd-mcp</c>.</item>
/// <item>Data: <c>DND_MCP_DATA_DIR</c>, else <c>$XDG_DATA_HOME/dnd-mcp</c>, else <c>~/.local/share/dnd-mcp</c>.</item>
/// <item>campaigns.db: <c>DND_MCP_DB</c> (a file), else <c>&lt;data directory&gt;/campaigns.db</c>. Its backups go beside
/// it, in <c>backups/</c>.</item>
/// </list>
/// An empty or blank variable counts as unset. Every variable must hold an absolute path, and a relative one is ignored
/// (falling through to the next rule): Claude Code launches the server in the user's project, so a relative cache would
/// scatter one srd.db per project (possibly committed with it), and a relative data directory would silently give each
/// project its own campaigns. The XDG Base Directory spec requires exactly this for its variables; the <c>DND_MCP_*</c>
/// overrides follow it too, with one convenience: a leading <c>~</c> or <c>~/</c> means the home directory, as in a
/// shell, because MCP configs are JSON and nothing expands <c>"~/.cache/dnd-mcp"</c> there. Before, that value created
/// a directory literally named <c>~</c> holding srd.db inside the user's project. An ignored override is reported in
/// <see cref="Warnings"/> for the host to log, so the user learns why their setting had no effect.
/// </para>
/// <para>
/// The environment is passed in as a function so tests resolve paths without touching the real environment or the
/// developer's home directory. The integration tests and <c>StdoutPurityTests</c> set exactly these variables to keep a
/// test run away from <c>~/.cache/dnd-mcp</c>.
/// </para>
/// </summary>
public sealed class DndMcpPaths
{
    public const string CacheDirectoryVariable = "DND_MCP_CACHE_DIR";
    public const string DataDirectoryVariable = "DND_MCP_DATA_DIR";
    public const string XdgCacheHomeVariable = "XDG_CACHE_HOME";
    public const string XdgDataHomeVariable = "XDG_DATA_HOME";

    /// <summary>
    /// The campaigns.db FILE (not a directory), for a user who keeps campaigns somewhere else (a synced folder) while the
    /// rest of the data stays put. Same rules as the directory overrides: absolute or <c>~/</c>, else ignored with a
    /// warning.
    /// </summary>
    public const string CampaignDatabaseVariable = "DND_MCP_DB";

    /// <summary>The campaign store's file name inside <see cref="DataDirectory"/>.</summary>
    public const string CampaignDatabaseFileName = "campaigns.db";

    /// <summary>The rules index file name inside <see cref="CacheDirectory"/>.</summary>
    public const string SrdDatabaseFileName = "srd.db";

    private const string AppDirectoryName = "dnd-mcp";

    private readonly Lazy<string> _cacheDirectory;
    private readonly Lazy<string> _dataDirectory;
    private readonly string? _campaignDatabaseOverride;

    /// <param name="environment">Reads one environment variable; null, empty or blank means unset.</param>
    /// <param name="homeDirectory">The user's home directory, used for the defaults and to expand a leading <c>~</c>.</param>
    public DndMcpPaths(Func<string, string?> environment, string? homeDirectory)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var home = !string.IsNullOrEmpty(homeDirectory) && Path.IsPathFullyQualified(homeDirectory) ? homeDirectory : null;
        var cacheOverride = Override(environment, CacheDirectoryVariable, home, out var cacheWarning);
        var dataOverride = Override(environment, DataDirectoryVariable, home, out var dataWarning);
        _campaignDatabaseOverride = FileOverride(environment, CampaignDatabaseVariable, home, out var databaseWarning);
        Warnings = new[] { cacheWarning, dataWarning, databaseWarning }.OfType<string>().ToList();

        // Lazy, so a missing home directory fails only the path that needs it: a server with DND_MCP_CACHE_DIR set
        // and no HOME can still build srd.db.
        _cacheDirectory = new Lazy<string>(() =>
            cacheOverride ?? Default(environment, CacheDirectoryVariable, XdgCacheHomeVariable, home, ".cache"));
        _dataDirectory = new Lazy<string>(() =>
            dataOverride ?? Default(environment, DataDirectoryVariable, XdgDataHomeVariable, home, Path.Combine(".local", "share")));
    }

    /// <summary>Paths from this process's environment and home directory.</summary>
    public static DndMcpPaths FromEnvironment() =>
        new(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The disposable cache; deleting it costs one index rebuild (a second or two) and nothing else.</summary>
    /// <exception cref="InvalidOperationException">No variable decides the path and there is no absolute home directory.</exception>
    public string CacheDirectory => _cacheDirectory.Value;

    /// <summary>Durable data (Phase 6: campaigns.db). Never delete it to "fix" something.</summary>
    /// <exception cref="InvalidOperationException">No variable decides the path and there is no absolute home directory.</exception>
    public string DataDirectory => _dataDirectory.Value;

    public string SrdDatabasePath => Path.Combine(CacheDirectory, SrdDatabaseFileName);

    /// <summary>
    /// campaigns.db: <c>DND_MCP_DB</c> when set and usable, else <c>&lt;DataDirectory&gt;/campaigns.db</c>. With the
    /// variable set, no home directory is needed. The host resolves the path through its options (an explicit data
    /// directory, as tests set, wins over the variable); this is the environment's answer.
    /// </summary>
    /// <exception cref="InvalidOperationException">Neither the variable nor a data directory decides the path.</exception>
    public string CampaignDatabasePath => _campaignDatabaseOverride ?? Path.Combine(DataDirectory, CampaignDatabaseFileName);

    /// <summary>
    /// One line per <c>DND_MCP_*</c> override that was set but ignored (a relative path, or a <c>~</c> with no home
    /// directory to expand it against), saying why and what to set instead. Empty when every setting was used. The host
    /// logs these to stderr; without them the user's setting would silently have no effect.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; }

    // The absolute path an override names, or null (with a warning when it was set but unusable).
    private static string? Override(Func<string, string?> environment, string variable, string? home, out string? warning)
    {
        warning = null;
        var value = environment(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) ||
            (Path.DirectorySeparatorChar == '\\' && value.StartsWith("~\\", StringComparison.Ordinal)))
        {
            if (home is null)
            {
                warning =
                    $"{variable} is \"{value}\", but there is no home directory to expand ~ against, so it is ignored. " +
                    "Set it to an absolute path.";
                return null;
            }

            return Path.GetFullPath(Path.Combine(home, value.Length > 2 ? value[2..] : string.Empty));
        }

        if (Path.IsPathFullyQualified(value))
        {
            return Path.GetFullPath(value);
        }

        warning =
            $"{variable} is \"{value}\", which is not an absolute path, so it is ignored: a relative path would put " +
            "dnd-mcp's files inside whichever directory the server was started from. Set it to an absolute path " +
            "(a leading ~/ means your home directory).";
        return null;
    }

    // Like Override, for a variable that names a file: a value naming a directory (a bare ~, or a trailing separator) is
    // ignored with a warning rather than opening a database file named after the directory.
    private static string? FileOverride(Func<string, string?> environment, string variable, string? home, out string? warning)
    {
        var path = Override(environment, variable, home, out warning);
        if (path is null)
        {
            return null;
        }

        var value = environment(variable)!.Trim();
        if (value == "~" || value.EndsWith('/') || value.EndsWith(Path.DirectorySeparatorChar) ||
            (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar && value.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            warning =
                $"{variable} is \"{value}\", which names a directory, so it is ignored: it names the campaigns database " +
                $"file, e.g. \"~/dnd/campaigns.db\". To move every data file, set {DataDirectoryVariable} instead.";
            return null;
        }

        return path;
    }

    private static string Default(Func<string, string?> environment, string overrideVariable, string xdgVariable, string? home, string homeRelative)
    {
        if (environment(xdgVariable) is { Length: > 0 } xdg && Path.IsPathFullyQualified(xdg))
        {
            return Path.Combine(xdg, AppDirectoryName);
        }

        if (home is null)
        {
            throw new InvalidOperationException(
                $"Cannot find a home directory to put dnd-mcp's files under. Set {overrideVariable} to an absolute path.");
        }

        return Path.Combine(home, homeRelative, AppDirectoryName);
    }
}
