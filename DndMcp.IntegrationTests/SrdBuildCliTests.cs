using DndMcp.Cli;
using DndMcp.Hosting;
using DndMcp.IntegrationTests.Infrastructure;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>DndMcp srd-build</c> builds srd.db at exactly the path the server would use (or reports that the one
/// there is current), prints what it found and exits 0; every failure exits 1 with the reason on stderr; anything it does
/// not understand exits 2 with the usage; and plain <c>DndMcp</c> is still the server.
///
/// <para>
/// This is the check a person runs after copying a publish into place, so it must fail loudly and specifically: an
/// install missing its <c>content/</c> directory has to say so here rather than in the middle of a Claude session. The
/// built-binary tests prove Program.cs routes to the command before any host exists (a host would print its startup
/// log and wait on stdin); the in-process tests cover each outcome without a process per case.
/// </para>
/// </summary>
public sealed class SrdBuildCliTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task BuiltHost_SrdBuildTwice_BuildsThenReusesAtTheIsolatedCachePath()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("dnd-mcp-srd-build-").FullName;
        try
        {
            var database = Path.Combine(workingDirectory, "cache", "srd.db");

            var first = await BuiltServerProcess.RunCommandAsync(["srd-build"], workingDirectory, CommandTimeout);
            Assert.True(first.ExitCode == 0, first.Describe());
            Assert.StartsWith($"srd.db rebuilt in ", first.Stdout, StringComparison.Ordinal);
            Assert.Contains($" ms (no index at {database}).\n", first.Stdout, StringComparison.Ordinal);
            AssertSummary(first.Stdout, database);
            Assert.Equal(string.Empty, first.Stderr);
            Assert.True(File.Exists(database), first.Describe());

            var second = await BuiltServerProcess.RunCommandAsync(["srd-build"], workingDirectory, CommandTimeout);
            Assert.True(second.ExitCode == 0, second.Describe());
            Assert.StartsWith("srd.db is current; reused in ", second.Stdout, StringComparison.Ordinal);
            AssertSummary(second.Stdout, database);

            var forced = await BuiltServerProcess.RunCommandAsync(["srd-build", "--force"], workingDirectory, CommandTimeout);
            Assert.True(forced.ExitCode == 0, forced.Describe());
            Assert.Contains(" ms (rebuild requested).\n", forced.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(new[] { "frobnicate" }, "Unknown command 'frobnicate'.\n")]
    [InlineData(new[] { "srd-build", "--forse" }, "Unknown option '--forse' for srd-build.\n")]
    public async Task BuiltHost_UnknownCommandOrOption_PrintsUsageToStderrAndExits2(string[] arguments, string firstLine)
    {
        var workingDirectory = Directory.CreateTempSubdirectory("dnd-mcp-srd-build-").FullName;
        try
        {
            var result = await BuiltServerProcess.RunCommandAsync(arguments, workingDirectory, CommandTimeout);

            Assert.True(result.ExitCode == 2, result.Describe());
            Assert.Equal(firstLine + DndMcpCli.Usage, result.Stderr.ReplaceLineEndings("\n"));
            Assert.Equal(string.Empty, result.Stdout);
            Assert.False(Directory.Exists(Path.Combine(workingDirectory, "cache")), "A refused command still built an index.");
        }
        finally
        {
            Directory.Delete(workingDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Run_Help_PrintsUsageToStdoutAndExits0(string argument)
    {
        var (exitCode, output, error) = Run([argument], new DndMcpServerOptions());

        Assert.Equal(DndMcpCli.ExitOk, exitCode);
        Assert.Equal(DndMcpCli.Usage, output);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Run_SrdBuildWithNoContent_PrintsTheReasonToStderrAndExits1()
    {
        var content = Directory.CreateTempSubdirectory("dnd-mcp-no-content-").FullName;
        try
        {
            var (exitCode, output, error) = Run(["srd-build"], new DndMcpServerOptions
            {
                ContentRoot = content,
                CacheDirectory = McpServerHarness.SharedCacheDirectory,
            });

            Assert.Equal(DndMcpCli.ExitFailed, exitCode);
            Assert.Equal(
                $"srd-build failed: No content manifest at {Path.Combine(content, "5e-database", "manifest.json")}. " +
                "Vendor the data with scripts/fetch-5e-database.sh.\n",
                error.ReplaceLineEndings("\n"));
            Assert.Equal(string.Empty, output);
        }
        finally
        {
            Directory.Delete(content, recursive: true);
        }
    }

    [Fact]
    public void Run_SrdBuildIntoAnUnwritableCache_PrintsTheReasonAndExits1RatherThanBuildingElsewhere()
    {
        // The server falls back to a temporary directory; this command must not, or it would report success for an
        // index the server will never find.
        var parent = Directory.CreateTempSubdirectory("dnd-mcp-srd-build-").FullName;
        try
        {
            var notADirectory = Path.Combine(parent, "cache");
            File.WriteAllText(notADirectory, "x");

            var (exitCode, output, error) = Run(["srd-build"], new DndMcpServerOptions { CacheDirectory = notADirectory });

            Assert.Equal(DndMcpCli.ExitFailed, exitCode);
            Assert.StartsWith($"srd-build failed: cannot write {Path.Combine(notADirectory, "srd.db")}: ", error, StringComparison.Ordinal);
            Assert.Equal(string.Empty, output);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Run_SrdBuildWithTheSharedCache_ReportsTheCountsTheServerServes()
    {
        var (exitCode, output, error) = Run(["srd-build"], new DndMcpServerOptions { CacheDirectory = McpServerHarness.SharedCacheDirectory });

        Assert.Equal(DndMcpCli.ExitOk, exitCode);
        AssertSummary(output, Path.Combine(McpServerHarness.SharedCacheDirectory, "srd.db"));
        Assert.Equal(string.Empty, error);
    }

    private static void AssertSummary(string stdout, string database)
    {
        var lines = stdout.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal(
            [
                $"  path:     {database}",
                "  content:  5e-database-v7.0.0, Rules Glossary sha256 f64a894fd3ab99d0e3e5600a72dcbf43c30eb32716cc3b7a7e8645adbb0f776a",
                "  2014:     2415 documents",
                "  2024:     2187 documents",
                "  total:    4602 documents",
                "",
            ],
            lines.Skip(1));
    }

    private static (int ExitCode, string Output, string Error) Run(string[] arguments, DndMcpServerOptions options)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = DndMcpCli.Run(arguments, options, output, error);
        return (exitCode, output.ToString(), error.ToString());
    }
}
