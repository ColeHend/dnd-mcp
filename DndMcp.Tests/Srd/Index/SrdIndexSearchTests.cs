using DndMcp.Domain.Core;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdIndex.Search"/> against the real index: what ranks first, what the filters let through, when it falls
/// back to any word, and that no query text can reach FTS5 as syntax.
///
/// <para>
/// Ranking is pinned by its consequences rather than by bm25 numbers: a document named exactly what was asked comes
/// first; any document whose NAME matches outranks one that only mentions the words; ties come back in the same order
/// every time.
/// </para>
/// </summary>
public sealed class SrdIndexSearchTests : IClassFixture<SrdIndexFixture>
{
    private static readonly string[] Only2024 = ["2024"];
    private static readonly string[] Only2014 = ["2014"];
    private static readonly string[] BothEditions = ["2014", "2024"];

    private readonly SrdIndexFixture _fixture;
    private readonly SrdIndex _index;

    public SrdIndexSearchTests(SrdIndexFixture fixture)
    {
        _fixture = fixture;
        _index = fixture.Index;
    }

    [Fact]
    public void Search_SpellName_FindsTheSpellFirst()
    {
        var result = _index.Search("fireball", Only2024);

        Assert.True(result.MatchedAllWords);
        Assert.Equal("2024/spell/fireball", result.Hits[0].Ref.ToString());
        Assert.True(result.Hits[0].ExactName);
        Assert.Equal("Fireball", result.Hits[0].Name);
        Assert.All(result.Hits.Skip(1), hit => Assert.False(hit.ExactName, hit.Ref.ToString()));
    }

    [Fact]
    public void Search_BothEditions_PutsBothExactMatchesFirst()
    {
        var hits = _index.Search("fireball", BothEditions).Hits;

        Assert.Equal(["2014/spell/fireball", "2024/spell/fireball"], hits.Take(2).Select(h => h.Ref.ToString()).Order(StringComparer.Ordinal));
    }

    // "Thug" is only an alias in 2024 (the counterpart's 2014 name), matched through the aliases column.
    [Theory]
    [InlineData("thug", "2024", "2024/monster/tough")]
    [InlineData("tough", "2014", "2014/monster/thug")]
    [InlineData("college of lore", "2014", "2014/subclass/lore")]
    [InlineData("feeblemind", "2024", "2024/spell/befuddlement")]
    public void Search_OtherEditionsName_FindsTheRenamedEntryFirst(string query, string edition, string expected)
    {
        var first = _index.Search(query, [edition]).Hits[0];

        Assert.Equal(expected, first.Ref.ToString());
        Assert.True(first.ExactName);
    }

    // The glossary entry answers to its bare name as well as the weapon property itself.
    [Fact]
    public void Search_Finesse_PutsTheWeaponPropertyAndTheGlossaryEntryFirst()
    {
        var hits = _index.Search("finesse", Only2024).Hits;

        Assert.Equal(
            ["2024/rule/finesse-weapon-property", "2024/weapon-property/finesse"],
            hits.Take(2).Select(h => h.Ref.ToString()).Order(StringComparer.Ordinal));
        Assert.All(hits.Take(2), hit => Assert.True(hit.ExactName));
    }

    // bm25 alone ranks Dwarven Toughness first (the stem "tough" twice in a short name); a document named exactly
    // what was asked must still come first, and the rest keep bm25 order.
    [Fact]
    public void Search_ExactName_OutranksABetterBm25Score()
    {
        var hits = _index.Search("tough", Only2024).Hits.Select(h => h.Ref.ToString());

        Assert.Equal(
            ["2024/monster/tough", "2024/trait/dwarven-toughness", "2024/monster/tough-boss", "2024/species/dwarf", "2024/magic-item/belt-of-dwarvenkind"],
            hits);
    }

    // Name weight 10 against text weight 1: with equal weights the Pit Fiend, which only lists Fireball among its
    // spells, outranks the spell Delayed Blast Fireball.
    [Fact]
    public void Search_NameMatch_OutranksATextOnlyMatch()
    {
        var hits = _index.Search("fireball", Only2024).Hits.Take(5).Select(h => h.Ref.ToString()).ToList();

        Assert.Equal(
            ["2024/spell/fireball", "2024/magic-item/necklace-of-fireballs", "2024/magic-item/wand-of-fireballs", "2024/spell/delayed-blast-fireball", "2024/monster/pit-fiend"],
            hits);
    }

    [Fact]
    public void Search_TrailingStar_IsAPrefixSearch()
    {
        var hits = _index.Search("fire*", Only2024, limit: 50).Hits;

        Assert.Equal("2024/damage-type/fire", hits[0].Ref.ToString());
        Assert.Contains(hits, h => h.Ref.ToString() == "2024/spell/fire-bolt");
        Assert.Contains(hits, h => h.Ref.ToString() == "2024/spell/fireball");
    }

    [Fact]
    public void Search_KindsFilter_ReturnsOnlyThoseKinds()
    {
        var hits = _index.Search("fire", Only2024, kinds: ["spell", "monster"], limit: 50).Hits;

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Contains(h.Kind, new[] { "spell", "monster" }));
        Assert.Contains(hits, h => h.Kind == "monster");
    }

    [Theory]
    [InlineData("2014")]
    [InlineData("2024")]
    public void Search_EditionFilter_ReturnsOnlyThatEdition(string edition)
    {
        var hits = _index.Search("grapple", [edition], limit: 50).Hits;

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal(edition, h.Edition));
    }

    // 577 level rows named "Fighter 5" and so on would bury the class; they are reachable by ref only.
    [Fact]
    public void Search_LevelRecords_AreNeverReturned()
    {
        Assert.DoesNotContain(_index.Search("fighter", BothEditions, limit: 50).Hits, h => h.Kind == "level");
        Assert.Empty(_index.Search("fighter", BothEditions, kinds: ["level"], limit: 50).Hits);
        Assert.Equal("2014/class/fighter", _index.Search("fighter", Only2014).Hits[0].Ref.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(50)]
    public void Search_Limit_IsHonoured(int limit)
    {
        Assert.Equal(limit, _index.Search("damage", BothEditions, limit: limit).Hits.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Search_LimitOutOfRange_Throws(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _index.Search("fire", Only2024, limit: limit));
    }

    [Fact]
    public void Search_NoEditionsOrUnknownEdition_Throws()
    {
        Assert.Throws<ArgumentException>(() => _index.Search("fire", []));
        Assert.Throws<ArgumentException>(() => _index.Search("fire", ["both"]));
    }

    [Fact]
    public void Search_SameQueryTwice_ReturnsTheSameOrder()
    {
        var first = _index.Search("attack", BothEditions, limit: 50).Hits.Select(h => h.Ref.ToString());
        var second = _index.Search("attack", BothEditions, limit: 50).Hits.Select(h => h.Ref.ToString());

        Assert.Equal(first, second);
    }

    // The text column keeps paragraph breaks; a snippet is printed on one line under its hit, so they become spaces.
    [Fact]
    public void Search_Snippet_IsOneLineWithTheMatchInBold()
    {
        var hit = Assert.Single(_index.Search("grappled", Only2024).Hits, h => h.Ref.ToString() == "2024/condition/grappled");

        Assert.Equal(
            "While you have the **Grappled** condition, you experience the following effects. Speed 0. Your Speed is…",
            hit.Snippet);
    }

    [Fact]
    public void Search_NoHitMatchesEveryWord_FallsBackToAnyWordAndSaysSo()
    {
        var result = _index.Search("grapple zzzqqq", Only2024);

        Assert.False(result.MatchedAllWords);
        Assert.NotEmpty(result.Hits);
        Assert.All(result.Hits, h => Assert.Contains("**", h.Snippet + h.Name, StringComparison.Ordinal));
        Assert.Equal(_index.Search("grapple", Only2024).Hits.Select(h => h.Ref), result.Hits.Select(h => h.Ref));
    }

    // With one word the any-word query is the same query, so nothing is retried and the answer is simply "no hits".
    [Fact]
    public void Search_OneUnmatchedWord_ReturnsNoHitsWithoutFallingBack()
    {
        var result = _index.Search("zzzqqq", BothEditions);

        Assert.True(result.MatchedAllWords);
        Assert.Empty(result.Hits);
    }

    // Every hit must contain both words: exactly the rows FTS5 itself returns for "grapple AND escape".
    [Fact]
    public void Search_AllWordsMatch_ReturnsOnlyRowsWithEveryWord()
    {
        var result = _index.Search("grapple escape", Only2024, limit: 50);
        var both = _fixture.Column(
            "SELECT d.edition || '/' || d.kind || '/' || d.slug FROM doc_fts JOIN doc d ON d.id = doc_fts.rowid " +
            "WHERE doc_fts MATCH 'grapple AND escape' AND d.edition = '2024';");

        Assert.True(result.MatchedAllWords);
        Assert.Contains("2024/rule/grappling", result.Hits.Select(h => h.Ref.ToString()));
        Assert.Equal(both.Order(StringComparer.Ordinal), result.Hits.Select(h => h.Ref.ToString()).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Text that would be FTS5 syntax if passed through (quotes, parentheses, OR/NOT/NEAR, column filters, a leading
    /// minus, a bare star) is searched as words and never throws. An FTS5 syntax error would reach the model as the
    /// bare "An error occurred invoking 'rules_search'".
    /// </summary>
    [Theory]
    [InlineData("\"fire")]
    [InlineData("fire)")]
    [InlineData("(fire")]
    [InlineData("fire OR ice")]
    [InlineData("NOT fire")]
    [InlineData("fire AND")]
    [InlineData("NEAR(fire ice)")]
    [InlineData("-fire")]
    [InlineData("name:fire")]
    [InlineData("{name text}: fire")]
    [InlineData("fire * ice")]
    [InlineData("fi\"re\"ball")]
    [InlineData("^fire")]
    [InlineData("fire\0ball")]
    public void Search_FtsSyntaxInTheQuery_IsSearchedAsWords(string query)
    {
        var exception = Record.Exception(() => _index.Search(query, BothEditions));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("\")")]
    [InlineData("- ( ) \" : ^")]
    public void Search_NoSearchableWord_ThrowsDndInputExceptionWithAnExample(string query)
    {
        var error = Assert.Throws<DndInputException>(() => _index.Search(query, Only2024));

        Assert.StartsWith("The query needs at least one word to search for", error.Message, StringComparison.Ordinal);
        Assert.Contains("\"fireball\"", error.Message, StringComparison.Ordinal);
    }
}
