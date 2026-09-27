using System.Globalization;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// The SRD markdown tables under <c>Encounters/Fixtures/</c>, read as text: each data row's cells, trimmed. The tests
/// hold the Domain tables to these rather than to a second typed copy, because a typed copy made from the same notes as
/// the code shares its typos.
/// </summary>
public static class SrdTableFixtures
{
    public static string FixtureDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Encounters", "Fixtures");

    /// <summary>Every table row of the fixture that is not the header or the separator, and has any text.</summary>
    public static IReadOnlyList<string[]> Rows(string fileName)
    {
        var lines = File.ReadAllLines(Path.Combine(FixtureDirectory, fileName))
            .Where(l => l.TrimStart().StartsWith('|'))
            .ToList();
        Assert.True(lines.Count >= 3, $"{fileName} has no table.");
        return lines
            .Skip(2)
            .Select(l => l.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray())
            .Where(cells => cells.Any(c => c.Length > 0))
            .ToList();
    }

    /// <summary>"1,100" → 1100.</summary>
    public static long Number(string cell) => long.Parse(cell.Replace(",", string.Empty), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}

/// <summary>
/// Invariant: each fixture is exactly its line range of the SRD markdown (Fixtures/README.md), so a parity test that
/// passes against a fixture passes against the SRD. Runs only where the serving-solid-characters repository is checked
/// out beside this one; elsewhere the committed bytes are what the README says they are.
/// </summary>
public sealed class SrdTableFixtureTests
{
    public static TheoryData<string, string, int, int> Ranges => new()
    {
        { "srd52-xp-budget-per-character.md", "dndsrd5.2_markdown-main/src/09_GameplayToolbox.md", 607, 630 },
        { "srd52-experience-points-by-challenge-rating.md", "dndsrd5.2_markdown-main/src/11_Monsters.md", 154, 191 },
        { "srd52-proficiency-bonus-by-challenge-rating.md", "dndsrd5.2_markdown-main/src/11_Monsters.md", 197, 208 },
        { "srd51-experience-points-by-challenge-rating.md", "dnd.srd.5.1-main/10_Monsters/Monsters.md", 239, 267 },
        { "srd51-proficiency-bonus-by-challenge-rating.md", "dnd.srd.5.1-main/10_Monsters/Monsters.md", 131, 168 },
    };

    [SiblingSrdMarkdownTheory]
    [MemberData(nameof(Ranges))]
    public void Fixture_EveryFile_IsItsLineRangeOfTheSrdMarkdown(string fixture, string source, int first, int last)
    {
        var sourceLines = File.ReadAllLines(Path.Combine(SiblingSrdMarkdownTheoryAttribute.DocsDirectory!, source));
        var expected = sourceLines.Skip(first - 1).Take(last - first + 1);

        Assert.Equal(expected, File.ReadAllLines(Path.Combine(SrdTableFixtures.FixtureDirectory, fixture)));
    }

    [Fact]
    public void Readme_EveryFixture_IsListed()
    {
        var readme = File.ReadAllText(Path.Combine(SrdTableFixtures.FixtureDirectory, "README.md"));
        var files = Directory.GetFiles(SrdTableFixtures.FixtureDirectory, "srd5*.md").Select(Path.GetFileName).ToList();

        Assert.Equal(Ranges.Count, files.Count);
        Assert.All(files, f => Assert.Contains($"`{f}`", readme, StringComparison.Ordinal));
    }
}

/// <summary>
/// A theory that runs only when the SRD markdown (serving-solid-characters' <c>Docs/</c>) is checked out beside this
/// repository, as on the machine the fixtures were copied on.
/// </summary>
public sealed class SiblingSrdMarkdownTheoryAttribute : TheoryAttribute
{
    public static string? DocsDirectory { get; } = Find();

    public SiblingSrdMarkdownTheoryAttribute()
    {
        if (DocsDirectory is null)
        {
            Skip = "The serving-solid-characters repository (its Docs/ SRD markdown) is not checked out beside this one.";
        }
    }

    private static string? Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var docs = Path.Combine(directory.FullName, "serving-solid-characters", "Docs");
            if (Directory.Exists(Path.Combine(docs, "dndsrd5.2_markdown-main")))
            {
                return docs;
            }
        }

        return null;
    }
}
