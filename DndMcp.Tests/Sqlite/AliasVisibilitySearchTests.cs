using Dapper;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DndMcp.Tests.Sqlite;

/// <summary>
/// Invariant: a player-perspective search never matches an alias whose own visibility is <c>restricted</c> or
/// <c>author</c>, even when the entity it names is party-visible. This is PLAN.md §6 principle 4 (visibility,
/// perspective, "secrets never leak through search") applied to aliases, and it is the Phase 6 exit scenario
/// "Axiom Cage vs the old king": Belmakor knows the entity as "the old king", and only Cole (the author) knows
/// it as "the Axiom Cage".
///
/// <para>
/// A column filter cannot provide this on its own. It decides which COLUMNS a query reads, and
/// <see cref="Fts5Query.ColumnFiltered"/> makes that airtight, but the filter is only as safe as what the
/// triggers put in each column. research/04 §4 gives <c>entity_alias</c> its own <c>visibility</c> and then
/// fills the player-visible <c>aliases</c> column with <c>group_concat(alias)</c> over EVERY alias. The
/// later join to <c>entity</c> filters on the entity's visibility, not the alias's, so it lets the hit
/// through. The first test pins that leak against the research DDL. The rest pin a trigger shape that closes
/// it, which Phase 6 must adopt in place of the research one.
/// </para>
/// <para>
/// The fix routes aliases by visibility. <c>public</c> and <c>party</c> aliases go in <c>aliases</c>.
/// <c>restricted</c> and <c>author</c> aliases go in a <c>hidden_aliases</c> column that player filters leave
/// out, the same way they leave out <c>secret</c>. It is appended last, so research's positional bm25 weights
/// (10, 8, 4, 1, 1, 2) keep their meaning and it gets one more (8, like <c>aliases</c>). Folding hidden
/// aliases into <c>secret</c> would close the leak just as well, but would rank them at the secret column's
/// weight of 1 in author searches. Restricted aliases are hidden from every non-author search here. Matching
/// them for the characters who know them needs per-knower resolution after the join, which is left to Phase 6.
/// </para>
/// </summary>
public sealed class AliasVisibilitySearchTests : IDisposable
{
    private const string EntityName = "The Old King";
    private const string SecretAlias = "Axiom Cage";

    private static readonly string[] PlayerColumns = { "name", "aliases", "summary", "body", "tags" };

    /// <summary>
    /// The research §4 tables that feed entity_fts, trimmed to the columns the triggers read. The alias and
    /// tag triggers re-index by "touching" the owning entity, as in research, so they are shared by both FTS
    /// shapes below.
    /// </summary>
    private const string SourceTablesDdl = """
        CREATE TABLE entity (
          seq INTEGER PRIMARY KEY,
          id TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          summary TEXT NOT NULL DEFAULT '',
          body_md TEXT NOT NULL DEFAULT '',
          secret_md TEXT NOT NULL DEFAULT '',
          visibility TEXT NOT NULL DEFAULT 'party' CHECK (visibility IN ('public','party','restricted','author')),
          deleted_at TEXT
        ) STRICT;
        CREATE TABLE entity_alias (
          entity_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
          alias TEXT NOT NULL COLLATE NOCASE,
          visibility TEXT NOT NULL DEFAULT 'party' CHECK (visibility IN ('public','party','restricted','author')),
          PRIMARY KEY (entity_id, alias)
        ) STRICT, WITHOUT ROWID;
        CREATE TABLE tag (id TEXT PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE) STRICT;
        CREATE TABLE entity_tag (entity_id TEXT NOT NULL REFERENCES entity(id) ON DELETE CASCADE,
          tag_id TEXT NOT NULL REFERENCES tag(id) ON DELETE CASCADE, PRIMARY KEY (entity_id, tag_id)) STRICT, WITHOUT ROWID;
        CREATE TRIGGER entity_alias_ai AFTER INSERT ON entity_alias BEGIN UPDATE entity SET name = name WHERE id = new.entity_id; END;
        CREATE TRIGGER entity_alias_ad AFTER DELETE ON entity_alias BEGIN UPDATE entity SET name = name WHERE id = old.entity_id; END;
        CREATE TRIGGER entity_tag_ai  AFTER INSERT ON entity_tag  BEGIN UPDATE entity SET name = name WHERE id = new.entity_id; END;
        CREATE TRIGGER entity_tag_ad  AFTER DELETE ON entity_tag  BEGIN UPDATE entity SET name = name WHERE id = old.entity_id; END;
        """;

    /// <summary>research/04 §4 verbatim: every alias lands in <c>aliases</c>, whatever its visibility.</summary>
    private const string ResearchFtsDdl = """
        CREATE VIRTUAL TABLE entity_fts USING fts5(name, aliases, summary, body, secret, tags,
          tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');
        CREATE TRIGGER entity_fts_ai AFTER INSERT ON entity WHEN new.deleted_at IS NULL BEGIN
          INSERT INTO entity_fts(rowid,name,aliases,summary,body,secret,tags)
          VALUES (new.seq,new.name,'',new.summary,new.body_md,new.secret_md,'');
        END;
        CREATE TRIGGER entity_fts_au AFTER UPDATE OF name, summary, body_md, secret_md, deleted_at ON entity BEGIN
          DELETE FROM entity_fts WHERE rowid = old.seq;
          INSERT INTO entity_fts(rowid,name,aliases,summary,body,secret,tags)
          SELECT new.seq, new.name,
            coalesce((SELECT group_concat(alias,' ') FROM entity_alias WHERE entity_id = new.id),''),
            new.summary, new.body_md, new.secret_md,
            coalesce((SELECT group_concat(t.name,' ') FROM entity_tag et JOIN tag t ON t.id = et.tag_id
                      WHERE et.entity_id = new.id),'')
          WHERE new.deleted_at IS NULL;
        END;
        CREATE TRIGGER entity_fts_ad AFTER DELETE ON entity BEGIN DELETE FROM entity_fts WHERE rowid = old.seq; END;
        """;

    /// <summary>
    /// The fix. Aliases are split by their own visibility, and <c>entity_alias_au</c> re-indexes when an
    /// alias's visibility changes. research has no update trigger on entity_alias because it indexes every
    /// alias anyway. Once the split depends on visibility, a reveal (author to party) or a retcon (party to
    /// author) that is not re-indexed leaves the alias in the wrong column, and in the retcon case that is a leak.
    /// </summary>
    private const string VisibilityFilteredFtsDdl = """
        CREATE VIRTUAL TABLE entity_fts USING fts5(name, aliases, summary, body, secret, tags, hidden_aliases,
          tokenize = 'porter unicode61 remove_diacritics 2', prefix = '2 3');
        CREATE TRIGGER entity_fts_ai AFTER INSERT ON entity WHEN new.deleted_at IS NULL BEGIN
          INSERT INTO entity_fts(rowid,name,aliases,summary,body,secret,tags,hidden_aliases)
          VALUES (new.seq,new.name,'',new.summary,new.body_md,new.secret_md,'','');
        END;
        CREATE TRIGGER entity_fts_au AFTER UPDATE OF name, summary, body_md, secret_md, deleted_at ON entity BEGIN
          DELETE FROM entity_fts WHERE rowid = old.seq;
          INSERT INTO entity_fts(rowid,name,aliases,summary,body,secret,tags,hidden_aliases)
          SELECT new.seq, new.name,
            coalesce((SELECT group_concat(alias,' ') FROM entity_alias
                      WHERE entity_id = new.id AND visibility IN ('public','party')),''),
            new.summary, new.body_md, new.secret_md,
            coalesce((SELECT group_concat(t.name,' ') FROM entity_tag et JOIN tag t ON t.id = et.tag_id
                      WHERE et.entity_id = new.id),''),
            coalesce((SELECT group_concat(alias,' ') FROM entity_alias
                      WHERE entity_id = new.id AND visibility NOT IN ('public','party')),'')
          WHERE new.deleted_at IS NULL;
        END;
        CREATE TRIGGER entity_fts_ad AFTER DELETE ON entity BEGIN DELETE FROM entity_fts WHERE rowid = old.seq; END;
        CREATE TRIGGER entity_alias_au AFTER UPDATE OF alias, visibility ON entity_alias BEGIN
          UPDATE entity SET name = name WHERE id IN (old.entity_id, new.entity_id); END;
        """;

    private readonly SqliteConnection _db = SqliteScratch.OpenInMemory("Foreign Keys=True");

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// THE DESIGN BUG. With research's triggers, an author-only or restricted alias on a party-visible entity
    /// is found by a party search that goes through <see cref="Fts5Query.ColumnFiltered"/> and the
    /// entity-visibility join. The secret name confirms which entity it belongs to. If this starts failing,
    /// someone changed the research-shaped DDL above. It must stay research's, or this stops proving anything.
    /// </summary>
    [Theory]
    [InlineData("author")]
    [InlineData("restricted")]
    public void ResearchTriggers_HiddenAliasOnPartyEntity_MatchesPartySearch(string aliasVisibility)
    {
        Seed(ResearchFtsDdl, aliasVisibility);

        Assert.Equal(new[] { EntityName }, PartySearch(SecretAlias));
    }

    /// <summary>
    /// The fixed shape. A party search matches an alias exactly when the alias itself is public or party, and
    /// the author perspective (every column) always matches it. The public and party rows prove the fix did not
    /// simply stop indexing aliases.
    /// </summary>
    [Theory]
    [InlineData("public", true)]
    [InlineData("party", true)]
    [InlineData("restricted", false)]
    [InlineData("author", false)]
    public void VisibilityFilteredTriggers_AliasVisibility_DecidesPartyMatch(string aliasVisibility, bool partyMatches)
    {
        Seed(VisibilityFilteredFtsDdl, aliasVisibility);

        Assert.Equal(partyMatches ? new[] { EntityName } : Array.Empty<string>(), PartySearch(SecretAlias));
        Assert.Equal(new[] { EntityName }, AuthorSearch(SecretAlias));
    }

    /// <summary>
    /// Visibility changes after the alias was indexed. A reveal must become searchable, and a retcon to author
    /// must stop matching party searches at once. Without <c>entity_alias_au</c>, both rows fail. The second
    /// is the dangerous one, because the stale party-visible copy keeps answering party searches.
    /// </summary>
    [Theory]
    [InlineData("author", "party", true)]
    [InlineData("party", "author", false)]
    [InlineData("restricted", "public", true)]
    [InlineData("public", "restricted", false)]
    public void VisibilityFilteredTriggers_AliasVisibilityChanged_IsReindexed(string from, string to, bool partyMatches)
    {
        Seed(VisibilityFilteredFtsDdl, from);

        _db.Execute("UPDATE entity_alias SET visibility = @to WHERE alias = @SecretAlias", new { to, SecretAlias });

        Assert.Equal(partyMatches ? new[] { EntityName } : Array.Empty<string>(), PartySearch(SecretAlias));
        Assert.Equal(new[] { EntityName }, AuthorSearch(SecretAlias));
    }

    /// <summary>
    /// Deleting the hidden alias removes it from the author index as well, through research's
    /// <c>entity_alias_ad</c> touch. Otherwise a deleted secret name would still find its entity for the
    /// author, and it would come back in any export built from search.
    /// </summary>
    [Fact]
    public void VisibilityFilteredTriggers_HiddenAliasDeleted_NoLongerMatchesAnySearch()
    {
        Seed(VisibilityFilteredFtsDdl, "author");

        _db.Execute("DELETE FROM entity_alias WHERE alias = @SecretAlias", new { SecretAlias });

        Assert.Empty(PartySearch(SecretAlias));
        Assert.Empty(AuthorSearch(SecretAlias));
        Assert.Equal(new[] { EntityName }, PartySearch("old king"));
    }

    /// <summary>
    /// One party-visible entity named "The Old King", holding one alias at <paramref name="aliasVisibility"/>.
    /// Nothing else in the row contains "Axiom" or "Cage", so a party hit on either word came from the alias.
    /// </summary>
    private void Seed(string ftsDdl, string aliasVisibility)
    {
        _db.Execute(SourceTablesDdl);
        _db.Execute(ftsDdl);
        _db.Execute("""
            INSERT INTO entity(id, name, summary, body_md, secret_md, visibility)
            VALUES ('e1', @EntityName, 'a sealed power', 'sleeps beneath the winking moon', 'Keras''s prison', 'party')
            """, new { EntityName });
        _db.Execute(
            "INSERT INTO entity_alias(entity_id, alias, visibility) VALUES ('e1', @SecretAlias, @aliasVisibility)",
            new { SecretAlias, aliasVisibility });
    }

    /// <summary>
    /// The party perspective as the design describes it: the column-filtered FTS query, then the join to
    /// entity that drops rows the party cannot see.
    /// </summary>
    private string[] PartySearch(string userText) =>
        _db.Query<string>("""
            SELECT e.name FROM entity_fts JOIN entity e ON e.seq = entity_fts.rowid
            WHERE entity_fts MATCH @query AND e.visibility IN ('public','party')
            ORDER BY e.seq
            """, new { query = Fts5Query.ColumnFiltered(userText, PlayerColumns) }).ToArray();

    private string[] AuthorSearch(string userText) =>
        _db.Query<string>("""
            SELECT e.name FROM entity_fts JOIN entity e ON e.seq = entity_fts.rowid
            WHERE entity_fts MATCH @query
            ORDER BY e.seq
            """, new { query = Fts5Query.Terms(userText) }).ToArray();
}
