using DndMcp.Repository.Srd.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// What the query side reads back from rows the builder writes: a document's curated corrections, and heading aliases
/// (a 2014 rule answering to one of its own <c>####</c> subsections). The tests build a real index and then set the
/// rows themselves, so they pin the reading and ranking whatever the current content happens to contain.
/// </summary>
public sealed class QueryIndexDataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dnd-mcp-query-data-" + Guid.NewGuid().ToString("N"));
    private readonly SrdIndex _index;

    public QueryIndexDataTests()
    {
        var path = Path.Combine(_directory, "srd.db");
        var summary = SrdIndexBuilder.Build(SrdIndexFixture.ContentRoot, path);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            Execute(connection, "UPDATE doc SET corrections = '' WHERE edition = '2024' AND kind = 'spell' AND slug = 'fireball';");
            Execute(
                connection,
                "UPDATE doc SET corrections = 'Effect text replaced: upstream spliced in Gaseous Form.' || char(10) || 'Rarity restored.' " +
                "WHERE edition = '2024' AND kind = 'magic-item' AND slug = 'potion-of-heroism';");
            foreach (var heading in new[] { "Contests in Combat", "Grappling", "Opportunity Attacks" })
            {
                // The builder writes these itself (decision 3); OR IGNORE keeps its row where it already has one.
                Execute(
                    connection,
                    $"""
                    INSERT OR IGNORE INTO alias (doc_id, alias, alias_key, source)
                    SELECT id, '{heading}', '{SrdNames.Key(heading)}', 'heading' FROM doc
                    WHERE edition = '2014' AND kind = 'rule' AND slug = 'melee-attacks';
                    """);
            }
        }

        _index = SrdIndex.TryOpen(path, summary.StalenessKey, out var reason) ?? throw new InvalidOperationException(reason);
    }

    public void Dispose()
    {
        _index.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    // One reason per line in the column; every way of reading a document carries them, so no path shows corrected text
    // as if it were the untouched upstream record.
    [Fact]
    public void Get_CorrectedDocument_HasItsCorrectionReasons()
    {
        string[] expected = ["Effect text replaced: upstream spliced in Gaseous Form.", "Rarity restored."];

        Assert.Equal(expected, _index.Get("2024", "magic-item", "potion-of-heroism")!.Corrections);
        Assert.Equal(expected, _index.FindByName("Potion of Heroism", "2024").Best!.Document.Corrections);
        Assert.Equal(expected, _index.Counterparts(_index.Get("2014", "magic-item", "potion-of-heroism")!).Single().Corrections);
    }

    [Fact]
    public void Get_UncorrectedDocument_HasNoCorrections()
    {
        Assert.Empty(_index.Get("2024", "spell", "fireball")!.Corrections);
    }

    /// <summary>
    /// The 2014 SRD has no entry of its own for many rules that are subsections of a longer one ("Grappling" is a
    /// <c>####</c> heading inside Melee Attacks). A heading alias makes the name resolve, says so (the tool tells the
    /// reader which entry covers it), and ranks that entry first in search.
    /// </summary>
    [Theory]
    [InlineData("Contests in Combat")]
    [InlineData("Grappling")]
    [InlineData("opportunity attacks")]
    public void FindByName_SubsectionHeading_MatchesTheContainingRuleAsAHeadingAlias(string name)
    {
        var best = _index.FindByName(name, "2014").Best!;

        Assert.Equal("2014/rule/melee-attacks", best.Document.Ref.ToString());
        Assert.Equal(SrdNames.Key(name), SrdNames.Key(best.MatchedAlias!));
        Assert.Equal(SrdAliasSources.Heading, best.Source);
        Assert.Contains(best.Document.Aliases, a => a.Source == SrdAliasSources.Heading && a.Name == best.MatchedAlias);
    }

    // Before, 2014 "grappling" ranked the grapple rules 19th, below the grappling hook, the Grappled condition and every
    // monster that grapples.
    [Theory]
    [InlineData("contests in combat")]
    [InlineData("grappling")]
    [InlineData("opportunity attacks")]
    public void Search_SubsectionHeading_RanksTheContainingRuleFirst(string query)
    {
        var first = _index.Search(query, ["2014"]).Hits[0];

        Assert.Equal("2014/rule/melee-attacks", first.Ref.ToString());
        Assert.True(first.ExactName);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
