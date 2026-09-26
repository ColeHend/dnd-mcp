using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: <c>entity_fts</c>, declared exactly as in research/04 §4
/// (<c>tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3'</c>), finds inflected forms,
/// ignores accents in either direction, answers prefix queries, and ranks with per-column bm25 weights so a
/// name hit beats body chatter. These are what make campaign_search usable when someone types "bjorn" for
/// Björn or "dragons" for a note that says "dragon".
/// </summary>
public sealed class Fts5TokenizerTests : IDisposable
{
    private readonly SqliteConnection _db;

    public Fts5TokenizerTests()
    {
        _db = SqliteScratch.OpenInMemory();
        _db.Execute("""
            CREATE VIRTUAL TABLE entity_fts USING fts5(name, aliases, summary, body, secret, tags,
              tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');
            INSERT INTO entity_fts(rowid, name, aliases, summary, body, secret, tags) VALUES
              (1, 'Björn Mountainfell', '', 'a dwarf', 'He hunted Dragons across the north', '', ''),
              (2, 'Belmakor Silverwind', '', 'bladesinger', 'frontman of Mythril Zeppelin', '', ''),
              (3, 'Café Noir', '', 'a tavern', 'a naïve résumé pinned to the wall', '', ''),
              (4, 'Nadar', '', 'a warrior', 'carries the axe', '', ''),
              (5, 'Nguyễn', '', 'a cartographer', 'studied under Lǘ', '', '');
            """);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// PLAN.md §6 principle 4 / research §4 tokenizer: porter stemming. Without it "dragons" misses a note
    /// that says "dragon" and "hunting" misses "hunted", and campaign_search looks broken.
    /// </summary>
    [Theory]
    [InlineData("dragon")]
    [InlineData("dragons")]
    [InlineData("hunting")]
    [InlineData("hunts")]
    [InlineData("hunted")]
    public void Match_PorterStemming_FindsInflectedForms(string query)
    {
        Assert.Equal(new long[] { 1 }, Match(query));
    }

    /// <summary>
    /// research §4 tokenizer: <c>remove_diacritics 2</c> folds accents on both the indexed text and the
    /// query, so "bjorn" finds Björn and "Nádar" finds Nadar. Names in both campaigns carry diacritics that
    /// nobody types consistently.
    ///
    /// <para>
    /// Only the "nguyen" and "lu" rows tell <c>2</c> apart from <c>1</c>, which is unicode61's default. Their
    /// letters (ễ, ǘ) carry two diacritics each, and mode 1 leaves such letters unfolded. Every single-accent
    /// row passes in both modes. Without these two rows, a DDL that dropped the "2" would pass unnoticed.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("bjorn", 1L)]
    [InlineData("BJÖRN", 1L)]
    [InlineData("björn", 1L)]
    [InlineData("cafe", 3L)]
    [InlineData("naive resume", 3L)]
    [InlineData("Nádar", 4L)]
    [InlineData("nguyen", 5L)]
    [InlineData("lu", 5L)]
    public void Match_RemoveDiacritics_FoldsAccentsInBothDirections(string query, long expected)
    {
        Assert.Equal(new[] { expected }, Match(query));
    }

    /// <summary>
    /// Prefix queries return word starts at the lengths research §4 indexes (2 and 3) and beyond, in bare and
    /// quoted form. The quoted form is what <c>Fts5Query</c> emits for "word*", so it must work too.
    ///
    /// <para>
    /// This pins query BEHAVIOUR, not <c>prefix = '2 3'</c> itself. A prefix index only makes these queries
    /// faster. FTS5 answers them identically without one, so no result-based test can tell the option is
    /// there. It stays in the fixture DDL so the tests run against the declared table.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("be*")]
    [InlineData("bel*")]
    [InlineData("belma*")]
    [InlineData("\"belma\"*")]
    public void Match_PrefixQuery_FindsWordStarts(string query)
    {
        Assert.Equal(new long[] { 2 }, Match(query));
    }

    /// <summary>
    /// A quirk worth knowing before writing prefix search: porter stems the query prefix too, so
    /// "dragons*" becomes "dragon*" and still matches, and an accented prefix is folded before matching.
    /// </summary>
    [Theory]
    [InlineData("dragons*", 1L)]
    [InlineData("björ*", 1L)]
    [InlineData("mountainfell*", 1L)]
    public void Match_PrefixQuery_IsStemmedAndFoldedLikeWholeWords(string query, long expected)
    {
        Assert.Equal(new[] { expected }, Match(query));
    }

    /// <summary>
    /// research §4 "ranked with bm25(entity_fts, 10, 8, 4, 1, 1, 2)". The weights are positional per
    /// declared column (name, aliases, summary, body, secret, tags). With no weights, a body that repeats the
    /// word outranks a single hit in the name. With the design's weights the name hit must win, or searching
    /// "dragon" buries the entity called Dragon under every note that mentions one. Asserting both orders
    /// proves the weights, not the data, decide it.
    /// </summary>
    [Fact]
    public void Bm25_DesignColumnWeights_RankNameHitAboveRepeatedBodyHits()
    {
        using var db = SqliteScratch.OpenInMemory();
        db.Execute("""
            CREATE VIRTUAL TABLE entity_fts USING fts5(name, aliases, summary, body, secret, tags,
              tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');
            INSERT INTO entity_fts(rowid, name, aliases, summary, body, secret, tags) VALUES
              (1, 'Ignis', '', 'sky navy captain', 'Ignis fought a red dragon and later befriended a second dragon over the cloud sea', '', ''),
              (2, 'Dragon', '', 'an old wyrm', 'it sleeps beneath Flotsam and dreams of mithril salvage and the cloud sea', '', ''),
              (3, 'Flotsam', '', 'a salvage town', 'mithril salvage and cloud sea trade', '', ''),
              (4, 'Serif', '', 'a bard', 'songs about the winking moon', '', ''),
              (5, 'Tristan', '', 'a sailor', 'left the band after the storm', '', '');
            """);

        var unweighted = db.Query<long>(
            "SELECT rowid FROM entity_fts WHERE entity_fts MATCH 'dragon' ORDER BY bm25(entity_fts)").ToList();
        var weighted = db.Query<long>(
            "SELECT rowid FROM entity_fts WHERE entity_fts MATCH 'dragon' ORDER BY bm25(entity_fts, 10, 8, 4, 1, 1, 2)").ToList();

        Assert.Equal(new long[] { 1, 2 }, unweighted);
        Assert.Equal(new long[] { 2, 1 }, weighted);
    }

    /// <summary>
    /// bm25() returns NEGATIVE scores where more negative is a better match, so ranking is
    /// <c>ORDER BY bm25(...)</c> ascending. Writing DESC by habit returns the worst match first and looks
    /// plausible, so pin both the sign and the order. Belmakor matches in the name column (weight 10) and
    /// Björn only in the body (weight 1), so Belmakor must come first. If the sign convention flipped, the
    /// ascending order would put Björn first and this fails.
    /// </summary>
    [Fact]
    public void Bm25_AscendingOrder_PutsBestMatchFirstWithNegativeScores()
    {
        var ranked = _db.Query<(long Rowid, double Score)>(
            "SELECT rowid, bm25(entity_fts, 10, 8, 4, 1, 1, 2) FROM entity_fts WHERE entity_fts MATCH 'dragon OR belmakor' ORDER BY bm25(entity_fts, 10, 8, 4, 1, 1, 2)").ToList();

        Assert.Equal(new long[] { 2, 1 }, ranked.Select(r => r.Rowid));
        Assert.All(ranked, r => Assert.True(r.Score < 0, $"bm25 score {r.Score} is not negative."));
    }

    private long[] Match(string query) =>
        _db.Query<long>("SELECT rowid FROM entity_fts WHERE entity_fts MATCH @query ORDER BY rowid", new { query }).ToArray();
}
