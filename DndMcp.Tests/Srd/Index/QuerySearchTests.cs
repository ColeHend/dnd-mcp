using System.Diagnostics;
using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd.Index;

/// <summary>
/// <see cref="SrdIndex.Search"/>'s exact-name tier, its input bounds and its handling of copied text. Models follow hit
/// #1, so the first hit for a name must be what <c>rules_get</c> returns for that name: before, "fire bolt" put the
/// tiefling trait "You know the spell Fire Bolt" above the spell, and "darkvision" the 60-ft. trait above the spell.
/// </summary>
public sealed class QuerySearchTests : IClassFixture<SrdIndexFixture>
{
    private static readonly string[] Only2024 = ["2024"];
    private static readonly string[] BothEditions = ["2014", "2024"];

    private readonly SrdIndexFixture _fixture;
    private readonly SrdIndex _index;

    public QuerySearchTests(SrdIndexFixture fixture)
    {
        _fixture = fixture;
        _index = fixture.Index;
    }

    [Theory]
    [InlineData("fire bolt", "2024", "2024/spell/fire-bolt")]
    [InlineData("misty step", "2024", "2024/spell/misty-step")]
    [InlineData("monk", "2014", "2014/class/monk")]
    [InlineData("plate armor", "2014", "2014/equipment/plate-armor")]
    [InlineData("darkvision", "2024", "2024/spell/darkvision")]
    [InlineData("slow", "2024", "2024/spell/slow")]
    [InlineData("shield", "2024", "2024/spell/shield")]
    [InlineData("thieves tools", "2024", "2024/equipment/thieves-tools")]
    [InlineData("staff", "2024", "2024/equipment/staff")]
    [InlineData("goblin", "2024", "2024/monster/goblin-warrior")]
    [InlineData("druid", "2024", "2024/class/druid")]
    [InlineData("acolyte", "2024", "2024/background/acolyte")]
    [InlineData("berserker", "2024", "2024/monster/berserker")]
    [InlineData("potion of healing", "2024", "2024/equipment/potion-of-healing")]
    [InlineData("bardic inspiration", "2014", "2014/feature/bardic-inspiration-d6")]
    [InlineData("wild shape", "2014", "2014/feature/wild-shape-cr-1-4-or-below-no-flying-or-swim-speed")]
    public void Search_ExactNameSharedByKinds_FirstHitIsWhatRulesGetReturns(string query, string edition, string expected)
    {
        var first = _index.Search(query, [edition]).Hits[0];

        Assert.Equal(expected, first.Ref.ToString());
        Assert.True(first.ExactName);
    }

    // The spell and the rule by their own names, then the Darkvision (60 ft.) trait (and any other trait answering to
    // "Darkvision" by an alias), which used to come first.
    [Fact]
    public void Search_Darkvision2024_OrdersTheExactTierByKind()
    {
        var exact = _index.Search("darkvision", Only2024).Hits.TakeWhile(h => h.ExactName).ToList();

        Assert.Equal(["2024/spell/darkvision", "2024/rule/darkvision"], exact.Take(2).Select(h => h.Ref.ToString()));
        Assert.Contains("2024/trait/darkvision-60", exact.Select(h => h.Ref.ToString()));
        Assert.All(exact.Skip(2), h => Assert.Equal("trait", h.Kind));
    }

    /// <summary>
    /// The exact tier and <see cref="SrdIndex.FindByName"/> order the same documents the same way, for every name more
    /// than one searchable document answers to in either edition (about 240 of them).
    /// </summary>
    [Fact]
    public void Search_EverySharedName_ExactTierOrderEqualsFindByName()
    {
        var shared = _fixture.Column(
            """
            SELECT edition || '|' || key FROM (
                SELECT d.edition, d.name_key AS key, d.id FROM doc d WHERE d.searchable = 1
                UNION ALL
                SELECT d.edition, a.alias_key, d.id FROM alias a JOIN doc d ON d.id = a.doc_id WHERE d.searchable = 1)
            GROUP BY edition, key HAVING COUNT(DISTINCT id) > 1 ORDER BY edition, key;
            """);
        Assert.True(shared.Count > 200, $"only {shared.Count} shared names");

        var mismatches = new List<string>();
        foreach (var row in shared)
        {
            var (edition, key) = (row[..4], row[5..]);
            var expected = _index.FindByName(key, edition).Matches
                .Where(m => m.Document.Kind != "level")
                .Select(m => m.Document.Ref.ToString())
                .Distinct()
                .Take(SrdIndex.MaxSearchLimit)
                .ToList();
            var actual = _index.Search(key, [edition], limit: SrdIndex.MaxSearchLimit).Hits
                .Where(h => h.ExactName)
                .Select(h => h.Ref.ToString())
                .ToList();
            if (!expected.SequenceEqual(actual))
            {
                mismatches.Add($"{edition} \"{key}\": search [{string.Join(", ", actual)}] vs name [{string.Join(", ", expected)}]");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches));
    }

    /// <summary>
    /// The exact tier reads names the way <see cref="SrdIndex.FindByName"/> does: without a trailing kind word ("stat
    /// block", "spell", "monster") and with the other number. Before, "lich stat block" in 2024 returned only Deck of
    /// Illusions, whose corrected table is the one text holding every word of it, and "1 entry" told the model nothing
    /// else existed. A dropped kind word keeps only that kind in the tier: "goblin stat block" is the monster, not also
    /// the language.
    /// </summary>
    [Theory]
    [InlineData("lich stat block", "2024", "2024/monster/lich")]
    [InlineData("goblin stat block", "2024", "2024/monster/goblin-warrior")]
    [InlineData("troll stat block", "2024", "2024/monster/troll")]
    [InlineData("adult red dragon stat block", "2024", "2024/monster/adult-red-dragon")]
    [InlineData("bandit monster", "2024", "2024/monster/bandit")]
    [InlineData("lich stat block", "2014", "2014/monster/lich")]
    [InlineData("healing word spell", "2024", "2024/spell/healing-word")]
    [InlineData("opportunity attack", "2014", "2014/rule/melee-attacks")]
    [InlineData("death saving throws", "2024", "2024/rule/death-saving-throw")]
    public void Search_LooseFormOfAName_PutsTheNamedEntryFirst(string query, string edition, string expected)
    {
        var hits = _index.Search(query, [edition], limit: 5).Hits;

        Assert.Equal(expected, hits[0].Ref.ToString());
        Assert.True(hits[0].ExactName);
    }

    [Fact]
    public void Search_DroppedKindWord_KeepsOnlyThatKindInTheExactTier()
    {
        var exact = _index.Search("goblin stat block", Only2024).Hits.Where(h => h.ExactName).Select(h => h.Ref.ToString());

        Assert.Equal(["2024/monster/goblin-warrior"], exact);
    }

    // Both editions: the same kind and slug in each come together, 2014 first.
    [Fact]
    public void Search_BothEditions_ExactTierIsInEditionOrderForTheSameEntry()
    {
        var exact = _index.Search("fireball", BothEditions).Hits.TakeWhile(h => h.ExactName).Select(h => h.Ref.ToString());

        Assert.Equal(["2014/spell/fireball", "2024/spell/fireball"], exact);
    }

    /// <summary>
    /// Repeated words are searched once: FTS5 evaluates every copy, so 2,000 × "fire*" took 14 s and 100 × "s*" almost
    /// 3 s, with no way to stop it.
    /// </summary>
    [Theory]
    [InlineData("fire*", 80)]
    [InlineData("s*", 100)]
    [InlineData("a", 150)]
    public void Search_OneWordRepeated_IsSearchedOnceAndQuickly(string word, int times)
    {
        var query = string.Join(' ', Enumerable.Repeat(word, times));
        _index.Search(word, BothEditions);

        var stopwatch = Stopwatch.StartNew();
        var result = _index.Search(query, BothEditions);
        stopwatch.Stop();

        // Two copies are already "not a name", so the exact tier is empty in both; beyond that, repeats change nothing.
        Assert.Equal(_index.Search($"{word} {word}", BothEditions).Hits.Select(h => h.Ref), result.Hits.Select(h => h.Ref));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"took {stopwatch.Elapsed.TotalMilliseconds:0} ms");
    }

    // The worst query the bounds allow (as many distinct short prefixes as are accepted, none matching together, so it
    // falls back to any word over both editions with the largest limit) still answers in well under a second or two.
    [Fact]
    public void Search_WorstQueryWithinTheBounds_AnswersQuickly()
    {
        var words = Enumerable.Range(0, SrdIndex.MaxQueryWords)
            .Select(i => $"{(char)('a' + (i % 26))}{(i < 26 ? string.Empty : ((char)('a' + (i / 26) - 1)).ToString())}*")
            .ToList();
        var query = string.Join(' ', words);
        Assert.True(query.Length <= SrdIndex.MaxQueryLength);
        _index.Search("warm up", BothEditions);

        var stopwatch = Stopwatch.StartNew();
        var result = _index.Search(query, BothEditions, limit: SrdIndex.MaxSearchLimit);
        stopwatch.Stop();

        Assert.NotEmpty(result.Hits);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void Search_QueryLongerThanTheLimit_ThrowsDndInputException()
    {
        var query = string.Join(' ', Enumerable.Repeat("grapple", 200));

        var error = Assert.Throws<DndInputException>(() => _index.Search(query, Only2024));

        Assert.Equal(
            $"The query is {query.Length.ToString("N0", CultureInfo.InvariantCulture)} characters long; search with at most " +
            $"{SrdIndex.MaxQueryLength} characters of key words, e.g. \"grapple escape\" or \"fire*\". To read one entry, use " +
            "rules_get with its name.",
            error.Message);
    }

    [Fact]
    public void Search_MoreDistinctWordsThanTheLimit_ThrowsDndInputException()
    {
        var query = string.Join(' ', Enumerable.Range(0, SrdIndex.MaxQueryWords + 1).Select(i => "w" + i.ToString(CultureInfo.InvariantCulture)));

        var error = Assert.Throws<DndInputException>(() => _index.Search(query, Only2024));

        Assert.Equal(
            $"The query has {SrdIndex.MaxQueryWords + 1} different words; search with at most {SrdIndex.MaxQueryWords} key " +
            "words, e.g. \"grapple escape\" or \"fire*\".",
            error.Message);
    }

    [Fact]
    public void Search_QueryAtTheLimits_IsSearched()
    {
        var words = string.Join(' ', Enumerable.Range(0, SrdIndex.MaxQueryWords).Select(i => "w" + i.ToString(CultureInfo.InvariantCulture)));
        var padded = words + new string(' ', SrdIndex.MaxQueryLength - words.Length);

        Assert.Null(Record.Exception(() => _index.Search(padded, Only2024)));
    }

    /// <summary>
    /// Ligatures, fullwidth letters, soft hyphens and zero-width spaces (all common in text copied from a PDF or a web
    /// page) are the plain word to search: before, "ﬂaming sphere" fell back to any word and "Ｆｉｒｅｂａｌｌ" found nothing.
    /// </summary>
    [Theory]
    [InlineData("ﬂaming sphere", "2024/spell/flaming-sphere")]
    [InlineData("ﬁre bolt", "2024/spell/fire-bolt")]
    [InlineData("Fire­ball", "2024/spell/fireball")]
    [InlineData("Fire​ball", "2024/spell/fireball")]
    [InlineData("Ｆｉｒｅｂａｌｌ", "2024/spell/fireball")]
    public void Search_CompatibilityOrInvisibleCharacters_FindsTheEntryFirst(string query, string expected)
    {
        var result = _index.Search(query, Only2024);

        Assert.True(result.MatchedAllWords);
        Assert.Equal(expected, result.Hits[0].Ref.ToString());
        Assert.True(result.Hits[0].ExactName);
    }
}
