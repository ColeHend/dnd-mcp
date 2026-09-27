using Dapper;
using DndMcp.Repository.Sqlite;
using DndMcp.Tests.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// The any-word builders <c>rules_search</c> falls back to (<see cref="Fts5Query.AnyTerms"/>) and name suggestions
/// use (<see cref="Fts5Query.ColumnFilteredAnyTerms"/>). They add the one operator the builder emits itself, OR, so the
/// properties <c>PerspectiveSafeSearchTests</c> pins for the all-words form must hold for OR too: every word stays
/// quoted, user "OR" stays a word, and a column filter still covers every phrase in the group.
/// </summary>
public sealed class Fts5AnyTermsTests : IDisposable
{
    private readonly SqliteConnection _db;

    public Fts5AnyTermsTests()
    {
        _db = SqliteScratch.OpenInMemory();
        _db.Execute("""
            CREATE VIRTUAL TABLE t USING fts5(name, secret, tokenize = 'porter unicode61 remove_diacritics 2');
            INSERT INTO t(rowid, name, secret) VALUES
              (1, 'fire bolt', ''),
              (2, 'ice knife', ''),
              (3, 'keras', 'hoard');
            """);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("fire ice", "\"fire\" OR \"ice\"")]
    [InlineData("fire*", "\"fire\"*")]
    [InlineData("fire OR ice", "\"fire\" OR \"OR\" OR \"ice\"")]      // the user's OR is a quoted word
    [InlineData("a:b \"c", "\"a:b\" OR \"\"\"c\"")]
    [InlineData("( ) -", null)]
    [InlineData("", null)]
    public void AnyTerms_Text_QuotesEveryWordAndJoinsWithOr(string text, string? expected)
    {
        Assert.Equal(expected, Fts5Query.AnyTerms(text));
    }

    [Fact]
    public void AnyTerms_MatchesRowsWithAnyWord_WhereTermsNeedsAll()
    {
        Assert.Empty(Match(Fts5Query.Terms("fire ice")!));
        Assert.Equal([1L, 2L], Match(Fts5Query.AnyTerms("fire ice")!));
    }

    [Fact]
    public void ColumnFilteredAnyTerms_Text_FiltersTheWholeOrGroup()
    {
        Assert.Equal("{name} : (\"fire\"* OR \"hoard\")", Fts5Query.ColumnFilteredAnyTerms("fire* hoard", ["name"]));
    }

    // The leak the all-words builder closes must stay closed with OR: "hoard" is only in the secret column.
    [Theory]
    [InlineData("hoard")]
    [InlineData("fire hoard")]
    [InlineData("fire OR secret:hoard")]
    [InlineData("hoard) OR (secret : hoard")]
    public void ColumnFilteredAnyTerms_WordOnlyInAnotherColumn_NeverMatchesIt(string text)
    {
        var rows = Match(Fts5Query.ColumnFilteredAnyTerms(text, ["name"])!);

        Assert.DoesNotContain(3L, rows);
        Assert.Equal([3L], Match(Fts5Query.AnyTerms("hoard")!));
    }

    [Fact]
    public void ColumnFilteredAnyTerms_NonIdentifierColumn_Throws()
    {
        Assert.Throws<ArgumentException>(() => Fts5Query.ColumnFilteredAnyTerms("fire", ["name) OR (secret"]));
        Assert.Throws<ArgumentException>(() => Fts5Query.ColumnFilteredAnyTerms("fire", []));
    }

    [Theory]
    [InlineData("fire ice", 2)]
    [InlineData("fire*", 1)]
    [InlineData("fire - ( ice", 2)]
    [InlineData("   ", 0)]
    public void WordCount_Text_CountsSearchableWords(string text, int expected)
    {
        Assert.Equal(expected, Fts5Query.WordCount(text));
    }

    private List<long> Match(string query) =>
        _db.Query<long>("SELECT rowid FROM t WHERE t MATCH @query ORDER BY rowid;", new { query }).ToList();
}
