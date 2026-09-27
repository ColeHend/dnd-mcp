using Dapper;
using DndMcp.Repository.Sqlite;
using DndMcp.Tests.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// How <see cref="Fts5Query"/> cleans each word before quoting it: repeated words once (FTS5 evaluates every copy,
/// so repeats made a search quadratic), and text copied from PDFs and web pages (ligatures, fullwidth letters, soft
/// hyphens, zero-width spaces) as the plain word the index holds.
/// </summary>
public sealed class QueryFts5WordsTests : IDisposable
{
    private readonly SqliteConnection _db;

    public QueryFts5WordsTests()
    {
        _db = SqliteScratch.OpenInMemory();
        _db.Execute("""
            CREATE VIRTUAL TABLE t USING fts5(name, tokenize = 'porter unicode61 remove_diacritics 2');
            INSERT INTO t(rowid, name) VALUES (1, 'Flaming Sphere'), (2, 'Fireball'), (3, 'Fire Bolt');
            """);
    }

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("fire fire fire", "\"fire\"")]
    [InlineData("Fire FIRE fire", "\"Fire\"")]                // case-insensitively; the first spelling is kept
    [InlineData("fire fire* fire", "\"fire\" \"fire\"*")]     // a prefix is a different word
    [InlineData("fire ice fire ice", "\"fire\" \"ice\"")]
    public void Terms_RepeatedWords_AreQuotedOnce(string text, string expected)
    {
        Assert.Equal(expected, Fts5Query.Terms(text));
    }

    [Theory]
    [InlineData("a a a a a", 1)]
    [InlineData("fire FIRE ice", 2)]
    public void WordCount_RepeatedWords_CountOnce(string text, int expected)
    {
        Assert.Equal(expected, Fts5Query.WordCount(text));
    }

    [Theory]
    [InlineData("ﬂaming", "\"flaming\"")]
    [InlineData("Ｆｉｒｅｂａｌｌ", "\"Fireball\"")]
    [InlineData("Fire­ball", "\"Fireball\"")]
    [InlineData("Fire​ball", "\"Fireball\"")]
    [InlineData("﻿fire", "\"fire\"")]
    [InlineData("​", null)]
    public void Terms_CompatibilityAndFormatCharacters_AreFoldedOrDropped(string text, string? expected)
    {
        Assert.Equal(expected, Fts5Query.Terms(text));
    }

    /// <summary>
    /// A lone surrogate (a JSON string can carry one as an escape) makes .NET's normalisation throw; the normaliser drops
    /// it instead, so a stray escape in a query or name is never a bare tool failure.
    /// </summary>
    [Fact]
    public void Normalize_LoneSurrogate_IsDroppedInsteadOfThrowing()
    {
        Assert.Throws<ArgumentException>(() => "fire\uD800ball".Normalize(System.Text.NormalizationForm.FormKC));

        Assert.Equal("\"fireball\"", Fts5Query.Terms("fire\uD800ball"));
        Assert.Equal("fireball", DndMcp.Repository.Srd.Index.SrdNames.Key("fire\uDC00ball"));
        Assert.Equal("\"fire😀\"", Fts5Query.Terms("fire😀\uD800"));
    }

    [Theory]
    [InlineData("ﬂaming sphere", 1L)]
    [InlineData("Fire­ball", 2L)]
    [InlineData("Ｆｉｒｅ bolt", 3L)]
    public void Terms_CopiedText_MatchesThePlainWords(string text, long expected)
    {
        Assert.Equal([expected], _db.Query<long>("SELECT rowid FROM t WHERE t MATCH @q;", new { q = Fts5Query.Terms(text) }));
    }
}
