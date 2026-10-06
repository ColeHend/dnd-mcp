using System.Text.RegularExpressions;
using DndMcp.IntegrationTests.Infrastructure;
using DndMcp.Repository.Srd;
using DndMcp.Resources;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: <c>rules://attribution</c> carries the SRD 5.1 and SRD 5.2.1 CC-BY statements word for word — the same
/// words as the shipped licence files and the README — plus the 5e-database MIT notice and the exact data versions, and
/// credits Wizards of the Coast nowhere else.
///
/// <para>
/// This is the licence obligation for every rules answer the server gives. It fails silently in both directions: a
/// statement edited in one of its three copies (code, LICENSES/, README) is no longer verbatim in the others, and extra
/// credit to Wizards is exactly what the SRD PDFs ask not to add. Both would pass any test that only checks the
/// resource "mentions the SRD".
/// </para>
/// </summary>
public sealed partial class RulesResourceTests : IClassFixture<McpServerHarness>
{
    private readonly McpServerHarness _server;

    public RulesResourceTests(McpServerHarness server)
    {
        _server = server;
    }

    private static string ContentRoot => Path.Combine(AppContext.BaseDirectory, "content");

    private static string LicenseDirectory => Path.Combine(ContentRoot, "LICENSES");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [Fact]
    public async Task ReadResource_Attribution_IsOneMarkdownTextBlock()
    {
        var result = await _server.Client.ReadResourceAsync(RulesResources.AttributionUri);

        var contents = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal(RulesResources.AttributionUri, contents.Uri);
        Assert.Equal("text/markdown", contents.MimeType);
        Assert.StartsWith("# SRD attribution\n", contents.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("## SRD 5.1 (2014 rules)", RulesResources.Srd51Statement)]
    [InlineData("## SRD 5.2.1 (2024 rules)", RulesResources.Srd521Statement)]
    public async Task ReadResource_Attribution_HasEachStatementVerbatimUnderItsHeading(string heading, string statement)
    {
        var text = await ReadAttributionAsync();

        Assert.Contains($"{heading}\n\n{statement}\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SRD-5.1-CC-BY-4.0.txt", RulesResources.Srd51Statement)]
    [InlineData("SRD-5.2.1-CC-BY-4.0.txt", RulesResources.Srd521Statement)]
    public void Statement_Constant_IsTheShippedLicenceFileWordForWord(string file, string statement)
    {
        Assert.Equal(File.ReadAllText(Path.Combine(LicenseDirectory, file)).Trim(), statement);
    }

    [Theory]
    [InlineData(RulesResources.Srd51Statement)]
    [InlineData(RulesResources.Srd521Statement)]
    public void Statement_Constant_IsInTheReadmeWordForWord(string statement)
    {
        // The README wraps lines; wording, not line breaks, is what must match.
        var readme = WhitespaceRegex().Replace(File.ReadAllText(ReadmePath()), " ");

        Assert.Contains(statement, readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResource_Attribution_CreditsWizardsOnlyInTheTwoStatements()
    {
        var text = await ReadAttributionAsync();

        // Case-sensitive: the SRD 5.1 statement's own URL (dnd.wizards.com) is part of the statement, not extra credit.
        Assert.Equal(2, Regex.Matches(text, "Wizards of the Coast").Count);
        Assert.Equal(2, Regex.Matches(text, "Wizards").Count);
        Assert.DoesNotContain("WotC", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadResource_Attribution_IncludesTheShippedMitNoticeVerbatim()
    {
        var text = await ReadAttributionAsync();
        var notice = File.ReadAllText(Path.Combine(LicenseDirectory, "5e-database-MIT.txt")).Trim();

        Assert.Contains($"## 5e-database licence (MIT)\n\n```text\n{notice}\n```\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tag `5e-database-v7.0.0` (https://github.com/5e-bits/5e-srd-api, `packages/5e-database/src/{edition}/en`), 49 files pinned by sha256")]
    [InlineData("SRD 5.2.1 Rules Glossary as structured JSON, from the serving-solid-characters project (https://github.com/ColeHend/serving-solid-characters), sha256 `f64a894fd3ab99d0e3e5600a72dcbf43c30eb32716cc3b7a7e8645adbb0f776a`.")]
    public async Task ReadResource_Attribution_NamesTheExactDataServed(string provenance)
    {
        var text = await ReadAttributionAsync();

        Assert.Contains(provenance, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResource_Attribution_ListsTheCuratedCorrectionsWithTheirCountSourcesAndHash()
    {
        // CC-BY 4.0 asks that modifications be indicated: the corrected records carry text copied from the SRD markdown,
        // not the upstream data, and this is the provenance record for all of them.
        var text = await ReadAttributionAsync();
        var corrections = SrdCorrections.Load(ContentRoot);
        var perEdition = SrdEdition.All
            .Select(e => (Edition: e, Count: corrections.Entries.Count(c => c.Target.Edition == e)))
            .Where(e => e.Count > 0)
            .Select(e => $"{e.Count} in {e.Edition}");

        var line = Assert.Single(text.Split('\n'), l => l.StartsWith("- Curated corrections: ", StringComparison.Ordinal));
        Assert.StartsWith(
            $"- Curated corrections: `content/srd-corrections.json`, {corrections.Entries.Count} records ({string.Join(", ", perEdition)}) ",
            line,
            StringComparison.Ordinal);
        Assert.Contains("the SRD 5.2 markdown for 2024 and the SRD 5.1 markdown for 2014", line, StringComparison.Ordinal);
        Assert.Contains("Every corrected entry says so under its title", line, StringComparison.Ordinal);
        Assert.EndsWith($"; sha256 `{corrections.Sha256}`.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Readme_Attribution_StatesTheCuratedCorrectionsCountBySourceEdition()
    {
        // The README is the provenance record outside the server; a count that no longer matches the file misstates it.
        var readme = WhitespaceRegex().Replace(File.ReadAllText(ReadmePath()), " ");
        var corrections = SrdCorrections.Load(ContentRoot);
        var count2024 = corrections.Entries.Count(c => c.Target.Edition == SrdEdition.Edition2024);
        var count2014 = corrections.Entries.Count(c => c.Target.Edition == SrdEdition.Edition2014);

        Assert.Contains(
            $"Curated corrections: {corrections.Entries.Count} of the served records ({count2024} from 2024, {count2014} from 2014)",
            readme,
            StringComparison.Ordinal);
        Assert.Contains("copied from the SRD 5.2 markdown for 2024 and the SRD 5.1 markdown for 2014", readme, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResource_ContentMissing_StillCarriesBothStatementsAndSaysWhatIsMissing()
    {
        // The statements must reach the reader even from a broken install; only what describes the content goes missing.
        var empty = Directory.CreateTempSubdirectory("dnd-mcp-no-content-");
        var server = McpServerHarness.WithOptions(o => o.ContentRoot = empty.FullName);
        await server.InitializeAsync();
        try
        {
            var result = await server.Client.ReadResourceAsync(RulesResources.AttributionUri);
            var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;

            Assert.Contains(RulesResources.Srd51Statement, text, StringComparison.Ordinal);
            Assert.Contains(RulesResources.Srd521Statement, text, StringComparison.Ordinal);
            Assert.Contains("Its manifest could not be read from " + Path.Combine(empty.FullName, "5e-database", "manifest.json"), text, StringComparison.Ordinal);
            Assert.Contains("The notice ships as content/LICENSES/5e-database-MIT.txt but could not be read from ", text, StringComparison.Ordinal);
            Assert.Contains("The file could not be read from " + Path.Combine(empty.FullName, "srd-corrections.json"), text, StringComparison.Ordinal);
        }
        finally
        {
            await server.DisposeAsync();
            empty.Delete(recursive: true);
        }
    }

    private async Task<string> ReadAttributionAsync()
    {
        var result = await _server.Client.ReadResourceAsync(RulesResources.AttributionUri);
        return Assert.IsType<TextResourceContents>(Assert.Single(result.Contents)).Text;
    }

    internal static string ReadmePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DndMcp.sln")))
            {
                return Path.Combine(directory.FullName, "README.md");
            }
        }

        throw new FileNotFoundException("Could not find the repository root (DndMcp.sln) above " + AppContext.BaseDirectory);
    }
}
