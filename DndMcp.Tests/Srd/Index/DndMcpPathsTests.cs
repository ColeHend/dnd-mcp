using DndMcp.Repository;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="DndMcpPaths"/> with a fake environment: which variable wins, which are ignored, and what a missing home
/// directory does. A wrong answer here puts srd.db (or later campaigns.db) somewhere the user never looks, or puts a
/// test run's files in the developer's real cache.
/// </summary>
public sealed class DndMcpPathsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dnd-mcp-paths");
    private static readonly string Home = Path.Combine(Root, "home", "dm");

    [Fact]
    public void Paths_NoVariables_UseTheHomeDirectoryDefaults()
    {
        var paths = new DndMcpPaths(Environment(), Home);

        Assert.Equal(Path.Combine(Home, ".cache", "dnd-mcp"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(Home, ".local", "share", "dnd-mcp"), paths.DataDirectory);
        Assert.Equal(Path.Combine(Home, ".cache", "dnd-mcp", "srd.db"), paths.SrdDatabasePath);
    }

    [Fact]
    public void Paths_AbsoluteXdgVariables_AreUsedWithTheAppDirectory()
    {
        var xdgCache = Path.Combine(Root, "xdg-cache");
        var xdgData = Path.Combine(Root, "xdg-data");

        var paths = new DndMcpPaths(Environment(("XDG_CACHE_HOME", xdgCache), ("XDG_DATA_HOME", xdgData)), Home);

        Assert.Equal(Path.Combine(xdgCache, "dnd-mcp"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(xdgData, "dnd-mcp"), paths.DataDirectory);
    }

    // The XDG spec: relative values are invalid and ignored. Honouring one would put a cache in whatever directory
    // Claude Code launched the server from.
    [Theory]
    [InlineData("cache")]
    [InlineData("./cache")]
    [InlineData("~/.cache")]
    [InlineData("")]
    public void Paths_RelativeOrEmptyXdgVariable_IsIgnored(string value)
    {
        var paths = new DndMcpPaths(Environment(("XDG_CACHE_HOME", value), ("XDG_DATA_HOME", value)), Home);

        Assert.Equal(Path.Combine(Home, ".cache", "dnd-mcp"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(Home, ".local", "share", "dnd-mcp"), paths.DataDirectory);
    }

    [Fact]
    public void Paths_DndMcpOverrides_WinOverXdg()
    {
        var cache = Path.Combine(Root, "my-cache");
        var data = Path.Combine(Root, "my-data");

        var paths = new DndMcpPaths(
            Environment(
                ("DND_MCP_CACHE_DIR", cache), ("DND_MCP_DATA_DIR", data),
                ("XDG_CACHE_HOME", Path.Combine(Root, "xdg-cache")), ("XDG_DATA_HOME", Path.Combine(Root, "xdg-data"))),
            Home);

        Assert.Equal(cache, paths.CacheDirectory);
        Assert.Equal(data, paths.DataDirectory);
        Assert.Equal(Path.Combine(cache, "srd.db"), paths.SrdDatabasePath);
        Assert.Empty(paths.Warnings);
    }

    /// <summary>
    /// MCP configs are JSON, and nothing expands a <c>~</c> in them, so <c>"DND_MCP_CACHE_DIR": "~/.cache/dnd-mcp"</c>
    /// reaches the server literally. Before, that created a directory named <c>~</c> holding srd.db inside the user's
    /// project; a leading <c>~</c> now means the home directory, as it would in a shell.
    /// </summary>
    [Theory]
    [InlineData("~", "")]
    [InlineData("~/.cache/dnd-mcp", ".cache/dnd-mcp")]
    [InlineData("~/", "")]
    public void Paths_DndMcpOverrideWithLeadingTilde_IsUnderTheHomeDirectory(string value, string underHome)
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_CACHE_DIR", value), ("DND_MCP_DATA_DIR", value)), Home);

        var expected = Path.GetFullPath(Path.Combine(Home, underHome.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(expected, paths.CacheDirectory);
        Assert.Equal(expected, paths.DataDirectory);
        Assert.Empty(paths.Warnings);
    }

    /// <summary>
    /// Any other relative override is ignored, like a relative XDG value: Claude Code starts the server in the user's
    /// project, so a relative cache would scatter one srd.db per project (possibly committed), and a relative data
    /// directory would silently give each project its own campaigns. The warning says why, for the host to log.
    /// </summary>
    [Theory]
    [InlineData("relative-cache")]
    [InlineData("./cache")]
    [InlineData("~user/cache")]
    public void Paths_RelativeDndMcpOverride_IsIgnoredWithAWarning(string value)
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_CACHE_DIR", value)), Home);

        Assert.Equal(Path.Combine(Home, ".cache", "dnd-mcp"), paths.CacheDirectory);
        var warning = Assert.Single(paths.Warnings);
        Assert.Equal(
            $"DND_MCP_CACHE_DIR is \"{value}\", which is not an absolute path, so it is ignored: a relative path would put " +
            "dnd-mcp's files inside whichever directory the server was started from. Set it to an absolute path " +
            "(a leading ~/ means your home directory).",
            warning);
    }

    [Fact]
    public void Paths_TildeOverrideWithoutAHome_IsIgnoredWithAWarning()
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_DATA_DIR", "~/campaigns")), homeDirectory: null);

        var warning = Assert.Single(paths.Warnings);
        Assert.Equal(
            "DND_MCP_DATA_DIR is \"~/campaigns\", but there is no home directory to expand ~ against, so it is ignored. " +
            "Set it to an absolute path.",
            warning);
        Assert.Throws<InvalidOperationException>(() => paths.DataDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Paths_EmptyDndMcpOverride_CountsAsUnset(string value)
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_CACHE_DIR", value)), Home);

        Assert.Equal(Path.Combine(Home, ".cache", "dnd-mcp"), paths.CacheDirectory);
        Assert.Empty(paths.Warnings);
    }

    // Each directory resolves on its own: the cache override alone is enough to build srd.db without a home directory.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/home")]
    public void Paths_NoUsableHome_FailsOnlyTheDirectoryThatNeedsIt(string? home)
    {
        var cache = Path.Combine(Root, "my-cache");
        var paths = new DndMcpPaths(Environment(("DND_MCP_CACHE_DIR", cache)), home);

        Assert.Equal(cache, paths.CacheDirectory);
        var error = Assert.Throws<InvalidOperationException>(() => paths.DataDirectory);
        Assert.Contains("DND_MCP_DATA_DIR", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CampaignDatabasePath_NoVariable_IsCampaignsDbInTheDataDirectory()
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_DATA_DIR", Path.Combine(Root, "my-data"))), Home);

        Assert.Equal(Path.Combine(Root, "my-data", "campaigns.db"), paths.CampaignDatabasePath);
        Assert.Equal("campaigns.db", DndMcpPaths.CampaignDatabaseFileName);
    }

    /// <summary>
    /// DND_MCP_DB names the campaigns.db file itself (a synced folder, say) and wins over the data directory for it,
    /// leaving the data directory alone for everything else.
    /// </summary>
    [Fact]
    public void CampaignDatabasePath_AbsoluteDndMcpDb_WinsOverTheDataDirectory()
    {
        var file = Path.Combine(Root, "sync", "dnd.sqlite");
        var data = Path.Combine(Root, "my-data");

        var paths = new DndMcpPaths(Environment(("DND_MCP_DB", file), ("DND_MCP_DATA_DIR", data)), Home);

        Assert.Equal(file, paths.CampaignDatabasePath);
        Assert.Equal(data, paths.DataDirectory);
        Assert.Empty(paths.Warnings);
    }

    [Theory]
    [InlineData("~/dnd/campaigns.db", "dnd/campaigns.db")]
    [InlineData("~/campaigns.db", "campaigns.db")]
    public void CampaignDatabasePath_DndMcpDbWithLeadingTilde_IsUnderTheHomeDirectory(string value, string underHome)
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_DB", value)), Home);

        Assert.Equal(Path.GetFullPath(Path.Combine(Home, underHome.Replace('/', Path.DirectorySeparatorChar))), paths.CampaignDatabasePath);
        Assert.Empty(paths.Warnings);
    }

    /// <summary>With DND_MCP_DB set, campaigns work without a home directory, as the cache does with its override.</summary>
    [Fact]
    public void CampaignDatabasePath_AbsoluteDndMcpDbWithoutAHome_NeedsNoHome()
    {
        var file = Path.Combine(Root, "campaigns.db");

        var paths = new DndMcpPaths(Environment(("DND_MCP_DB", file)), homeDirectory: null);

        Assert.Equal(file, paths.CampaignDatabasePath);
        Assert.Throws<InvalidOperationException>(() => paths.DataDirectory);
    }

    [Theory]
    [InlineData("campaigns.db")]
    [InlineData("./data/campaigns.db")]
    [InlineData("~user/campaigns.db")]
    public void CampaignDatabasePath_RelativeDndMcpDb_IsIgnoredWithAWarning(string value)
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_DB", value)), Home);

        Assert.Equal(Path.Combine(Home, ".local", "share", "dnd-mcp", "campaigns.db"), paths.CampaignDatabasePath);
        var warning = Assert.Single(paths.Warnings);
        Assert.StartsWith($"DND_MCP_DB is \"{value}\", which is not an absolute path, so it is ignored", warning, StringComparison.Ordinal);
    }

    /// <summary>DND_MCP_DB names a file: a directory-looking value would otherwise open a database named after the directory.</summary>
    [Theory]
    [InlineData("~")]
    [InlineData("~/")]
    [InlineData("~/dnd/")]
    public void CampaignDatabasePath_DndMcpDbNamingADirectory_IsIgnoredWithAWarning(string value)
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_DB", value)), Home);

        Assert.Equal(Path.Combine(Home, ".local", "share", "dnd-mcp", "campaigns.db"), paths.CampaignDatabasePath);
        Assert.Equal(
            $"DND_MCP_DB is \"{value}\", which names a directory, so it is ignored: it names the campaigns database file, " +
            "e.g. \"~/dnd/campaigns.db\". To move every data file, set DND_MCP_DATA_DIR instead.",
            Assert.Single(paths.Warnings));
    }

    [Fact]
    public void CampaignDatabasePath_AbsoluteDirectoryWithTrailingSeparator_IsIgnoredWithAWarning()
    {
        var value = Path.Combine(Root, "dnd") + Path.DirectorySeparatorChar;

        var paths = new DndMcpPaths(Environment(("DND_MCP_DB", value)), Home);

        Assert.Equal(Path.Combine(Home, ".local", "share", "dnd-mcp", "campaigns.db"), paths.CampaignDatabasePath);
        Assert.Contains("names a directory", Assert.Single(paths.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void CampaignDatabasePath_TildeWithoutAHome_IsIgnoredWithAWarning()
    {
        var paths = new DndMcpPaths(Environment(("DND_MCP_DB", "~/campaigns.db"), ("DND_MCP_DATA_DIR", Path.Combine(Root, "d"))), homeDirectory: null);

        Assert.Equal(Path.Combine(Root, "d", "campaigns.db"), paths.CampaignDatabasePath);
        Assert.Contains("no home directory to expand ~ against", Assert.Single(paths.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void FromEnvironment_RealEnvironment_ResolvesToAbsolutePaths()
    {
        var paths = DndMcpPaths.FromEnvironment();

        Assert.True(Path.IsPathFullyQualified(paths.CacheDirectory));
        Assert.EndsWith("srd.db", paths.SrdDatabasePath, StringComparison.Ordinal);
    }

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables)
    {
        var map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }
}
