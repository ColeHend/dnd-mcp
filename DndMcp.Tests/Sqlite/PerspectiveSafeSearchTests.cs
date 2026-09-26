using Dapper;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: a non-author search never matches text that exists only in the <c>secret</c> column. This is
/// PLAN.md §6 principle 4 ("FTS column filters exclude secret for non-author perspectives, so secrets never
/// leak through search") and the Verification line "campaign_search with perspective=party never matches
/// secret_md text".
///
/// <para>
/// The fixture is built so a leak is visible. Keras's row has "Keras" in its name, while "hoard", "Axiom" and
/// "Cage" appear ONLY in its secret column. Any query that returns row 3 through one of those words has read
/// the secret.
/// </para>
/// <para>
/// This class pins the QUERY half: which columns a query can read. It fills the FTS table directly and
/// assumes every column except <c>secret</c> holds only player-visible text, so "the old king" in
/// <c>aliases</c> is a party alias here. Keeping that assumption true is the triggers' job, and research's
/// triggers break it for aliases that have their own visibility. <see cref="AliasVisibilitySearchTests"/>
/// pins that leak and the trigger shape that closes it.
/// </para>
/// </summary>
public sealed class PerspectiveSafeSearchTests : IDisposable
{
    private const long KerasRow = 3;

    private static readonly string[] PlayerColumns = { "name", "aliases", "summary", "body", "tags" };

    private readonly SqliteConnection _db;

    public PerspectiveSafeSearchTests()
    {
        _db = SqliteScratch.OpenInMemory();
        _db.Execute("""
            CREATE VIRTUAL TABLE entity_fts USING fts5(name, aliases, summary, body, secret, tags,
              tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');
            INSERT INTO entity_fts(rowid, name, aliases, summary, body, secret, tags) VALUES
              (1, 'Björn Mountainfell', '', 'a dwarf', 'He hunts dragons in the north', '', ''),
              (2, 'Belmakor Silverwind', '', 'bladesinger', 'frontman of Mythril Zeppelin', '', ''),
              (3, 'Keras', 'the old king', 'a fragment', 'the protector of Serret', 'Keras guards the Axiom Cage hoard', '');
            """);
    }

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// The mechanism itself: a column filter that leaves out <c>secret</c> does not match secret-only text,
    /// while the same word unfiltered (the author perspective) does. The unfiltered half proves the word
    /// really is in the table, so the filtered half is not passing vacuously.
    /// </summary>
    [Theory]
    [InlineData("hoard")]
    [InlineData("axiom")]
    [InlineData("cage")]
    public void ColumnFilter_TermOnlyInSecretColumn_MatchesOnlyWithoutTheFilter(string term)
    {
        Assert.Empty(Match("{name aliases summary body tags} : " + term));
        Assert.Equal(new[] { KerasRow }, Match(term));
    }

    /// <summary>
    /// Filters on nested groups intersect. <c>{player columns} : (secret : hoard)</c> matches nothing,
    /// because secret is not among the outer columns. <see cref="Fts5Query"/> relies on this to put the whole
    /// user query inside one filtered group.
    /// </summary>
    [Fact]
    public void ColumnFilter_NestedFilterNamingSecret_IntersectsToNothing()
    {
        Assert.Empty(Match("{name aliases summary body tags} : (secret : hoard)"));
        Assert.Equal(new[] { KerasRow }, Match("secret : hoard"));
    }

    /// <summary>
    /// THE DESIGN BUG this spike found. research/04 §4 builds the query as
    /// <c>'{…} : ' || :q</c>. FTS5 binds a column filter to the ONE phrase after it, so any multi-word or
    /// operator-bearing text escapes it. "keras hoard" is an innocent player search that returns Keras
    /// because "hoard" matched the secret column. The other rows are deliberate escapes. If this test starts
    /// failing, FTS5 changed its binding rules. The builder test below must still pass either way.
    /// </summary>
    [Theory]
    [InlineData("keras hoard")]
    [InlineData("keras axiom cage")]
    [InlineData("dragon OR secret:hoard")]
    [InlineData("keras AND secret : hoard")]
    public void RawConcatenation_MultiWordOrOperatorInput_LeaksSecretColumn(string userText)
    {
        var results = Match("{name aliases summary body tags} : " + userText);

        Assert.Contains(KerasRow, results);
    }

    /// <summary>
    /// Why parentheses alone are not the fix. <c>'{…} : (' || :q || ')'</c> does stop the innocent
    /// multi-word leak, but text with an unbalanced <c>)</c> closes the group early, and whatever follows is
    /// unfiltered again. <see cref="Fts5Query"/> therefore quotes every word as well as grouping them.
    /// </summary>
    [Theory]
    [InlineData("hoard) OR secret:(hoard")]
    [InlineData("keras) AND (secret : hoard")]
    [InlineData("nothing) OR {secret} : (axiom")]
    public void ParenthesesWithoutQuoting_UnbalancedParenthesisInput_LeaksSecretColumn(string userText)
    {
        Assert.Empty(Match("{name aliases summary body tags} : (keras hoard)"));

        var results = Match("{name aliases summary body tags} : (" + userText + ")");

        Assert.Contains(KerasRow, results);
    }

    /// <summary>
    /// The fix: <see cref="Fts5Query.ColumnFiltered"/> quotes every word and filters the whole group, so the
    /// same inputs (and other syntax-bearing ones) run without error and never return Keras through a
    /// secret-only word.
    /// </summary>
    [Theory]
    [InlineData("keras hoard")]
    [InlineData("keras axiom cage")]
    [InlineData("dragon OR secret:hoard")]
    [InlineData("hoard) OR secret:(hoard")]
    [InlineData("keras AND secret : hoard")]
    [InlineData("secret : hoard")]
    [InlineData("{secret} : hoard")]
    [InlineData("- {name aliases summary body tags} : hoard")]
    [InlineData("NEAR(keras hoard)")]
    [InlineData("\"hoard\"")]
    [InlineData("keras\" OR secret:\"hoard")]
    [InlineData("^hoard")]
    [InlineData("hoard*")]
    [InlineData("ho*")]
    [InlineData("keras hoa\0rd")]
    public void ColumnFiltered_HostileOrMultiWordInput_NeverMatchesSecretOnlyText(string userText)
    {
        var query = Fts5Query.ColumnFiltered(userText, PlayerColumns);

        Assert.NotNull(query);
        Assert.DoesNotContain(KerasRow, Match(query));
    }

    /// <summary>
    /// The fix must not break ordinary search. Words in visible columns still match, including with
    /// stemming, accent folding, prefixes and several words at once.
    /// </summary>
    [Theory]
    [InlineData("keras", KerasRow)]
    [InlineData("old king", KerasRow)]
    [InlineData("protector serret", KerasRow)]
    [InlineData("bjorn dragons", 1L)]
    [InlineData("belm*", 2L)]
    [InlineData("  Mythril   zeppelin  ", 2L)]
    public void ColumnFiltered_WordsInVisibleColumns_StillMatch(string userText, long expected)
    {
        var query = Fts5Query.ColumnFiltered(userText, PlayerColumns);

        Assert.Equal(new[] { expected }, Match(query!));
    }

    /// <summary>
    /// The author perspective searches every column, secrets included. Its words are still quoted, because
    /// unquoted "- hoard" is an FTS5 syntax error that would reach the model as a bare tool failure.
    /// </summary>
    [Fact]
    public void Terms_AuthorPerspective_MatchesSecretColumnAndSurvivesSyntax()
    {
        Assert.Throws<SqliteException>(() => Match("- hoard"));

        Assert.Equal(new[] { KerasRow }, Match(Fts5Query.Terms("keras hoard")!));
        Assert.Equal(new[] { KerasRow }, Match(Fts5Query.Terms("- hoard")!));
    }

    /// <summary>
    /// FTS5 has no "match everything" query, and an empty MATCH is an error. Text with no searchable words
    /// yields null so the caller lists without FTS instead of sending MATCH ''.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("***")]
    [InlineData("- : ( ) \" { }")]
    public void ColumnFiltered_NoSearchableWords_ReturnsNull(string? userText)
    {
        Assert.Null(Fts5Query.ColumnFiltered(userText, PlayerColumns));
        Assert.Null(Fts5Query.Terms(userText));
    }

    /// <summary>
    /// The exact shape, so a reader of a logged query can see the safety at a glance. Every word is quoted
    /// with inner quotes doubled, a trailing * sits outside the quotes, and there is one filtered group.
    /// </summary>
    [Theory]
    [InlineData("keras hoard*", "{name aliases summary body tags} : (\"keras\" \"hoard\"*)")]
    [InlineData("say \"hi\"", "{name aliases summary body tags} : (\"say\" \"\"\"hi\"\"\")")]
    public void ColumnFiltered_Output_IsQuotedWordsInOneFilteredGroup(string userText, string expected)
    {
        Assert.Equal(expected, Fts5Query.ColumnFiltered(userText, PlayerColumns));
    }

    /// <summary>
    /// Column names are spliced into the query text, so they must be plain identifiers. Otherwise a caller
    /// could pass input through the column list and reopen the leak.
    /// </summary>
    [Theory]
    [InlineData("secret} : hoard OR {name")]
    [InlineData("name body")]
    [InlineData("")]
    public void ColumnFiltered_NonIdentifierColumn_Throws(string column)
    {
        Assert.Throws<ArgumentException>(() => Fts5Query.ColumnFiltered("keras", new[] { "name", column }));
    }

    private long[] Match(string query) =>
        _db.Query<long>("SELECT rowid FROM entity_fts WHERE entity_fts MATCH @query ORDER BY rowid", new { query }).ToArray();
}
