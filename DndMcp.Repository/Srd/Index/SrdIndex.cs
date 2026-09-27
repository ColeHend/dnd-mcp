using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// Read-only queries over srd.db: full-text search, name lookup, fetch by ref, 2014↔2024 counterparts, and the
/// <see cref="ISrdLookup"/> the formatters render with.
///
/// <para>
/// <b>Thread-safe and synchronous, on connections opened once.</b> <see cref="TryOpen(string, string, out string)"/>
/// opens every pooled connection while the file it validated is in place, and checks the staleness key on each. A call
/// rents one (waiting for one when all are busy) and returns it; nothing ever opens srd.db by path again. On Unix an open
/// connection keeps reading the file it opened, so deleting the cache ("always safe", the README says) or another server
/// renaming a different build over srd.db changes nothing for this index, and parallel calls cannot fail on it. Before,
/// an overlapping call opened a fresh connection by path, met the missing or different file, and failed: Claude's
/// routine parallel rules calls then failed all but one at a time for the rest of the session. A
/// <see cref="SqliteConnection"/> must never serve two threads at once, hence the pool rather than one shared connection.
/// </para>
/// <para>
/// <b>What still fails.</b> A file damaged or overwritten in place (the same file, not a rename) can become unreadable
/// under the open connections. That is an <see cref="SrdIndexUnavailableException"/>, the host's signal to drop this
/// index and open (or rebuild) a current one, never a raw SQLite error.
/// </para>
/// <para>
/// <b>Input the model typed is never SQL or FTS syntax, and never unbounded.</b> Search text goes through
/// <see cref="Fts5Query"/>, which quotes every word once; names go through <see cref="SrdNames.Key"/>; everything else is
/// a bound parameter. Queries and names are capped (<see cref="MaxQueryLength"/>, <see cref="MaxQueryWords"/>,
/// <see cref="MaxNameLength"/>) because FTS5's cost grows much faster than the input: a pasted page of text took tens of
/// seconds and gigabytes, and nothing can stop a query once it runs. Text over a cap, or with no searchable word, is a
/// <see cref="DndInputException"/> with an example, not an FTS error.
/// </para>
/// </summary>
public sealed partial class SrdIndex : ISrdLookup, IDisposable
{
    /// <summary>The most hits <see cref="Search"/> returns in one call.</summary>
    public const int MaxSearchLimit = 50;

    /// <summary>How many suggestions a failed name lookup offers.</summary>
    public const int MaxSuggestions = 5;

    /// <summary>
    /// The longest name <see cref="FindByName"/> looks up. The longest SRD name is 53 characters; this leaves room for
    /// any honest name with extra words ("The Fiend warlock subclass") and refuses pasted text.
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// The longest query <see cref="Search"/> runs: a sentence of key words. Together with <see cref="MaxQueryWords"/> it
    /// bounds the worst query to well under a second over both editions.
    /// </summary>
    public const int MaxQueryLength = 500;

    /// <summary>The most distinct words a query may have (repeats count once; FTS5 would evaluate every copy).</summary>
    public const int MaxQueryWords = 32;

    // Connections opened up front. Parallel MCP calls rarely overlap by more; a fifth waits for one (queries take ms).
    private const int PoolSize = 4;

    // A failed lookup's words beyond this many are not tried for suggestions; names have at most 11 words.
    private const int MaxSuggestionWords = 12;

    // Aliases come from the alias table with their source (unit separator between source and name, record separator
    // between aliases), in the order the builder chose: the doc.aliases column is FTS text and has lost which name came
    // from where. Corrections are one reason per line.
    private const string DocColumns =
        "d.edition, d.kind, d.slug, d.name, d.json, " +
        "(SELECT group_concat(a.source || char(31) || a.alias, char(30) ORDER BY a.position) FROM alias a WHERE a.doc_id = d.id), " +
        "d.corrections, d.level";

    private const int DocColumnCount = 8;

    // How often TryOpen reopens the pool when its connections turn out to hold two different files (another server
    // renamed its build over srd.db between two opens).
    private const int MaxOpenAttempts = 3;

    // SQLite primary result codes that mean the file under an open connection can no longer be read: I/O error, corrupt
    // image, cannot open, schema changed under the statement, not a database.
    private static readonly HashSet<int> UnreadableFileCodes = [10, 11, 14, 17, 26];

    // Words a name uses to join its real words ("Swarm of Beetles", "Way of the Open Hand"). As a prefix on their own
    // they match hundreds of unrelated names, so suggestions never rank by them.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "at", "by", "for", "from", "in", "into", "of", "on", "or", "the", "to", "with",
    };

    // Words people put after a name that are not part of it ("Healing Word spell", "Goblin stat block", "Dodge action"),
    // with the kinds each means, likeliest first. Longer phrases first, so "magic item" is never read as "item".
    private static readonly (string Words, string[] Kinds)[] TrailingKindWords =
    [
        ("magic item", [SrdKinds.MagicItem]), ("stat block", [SrdKinds.Monster]), ("damage type", [SrdKinds.DamageType]),
        ("weapon property", [SrdKinds.WeaponProperty]), ("mastery property", [SrdKinds.WeaponMastery]),
        ("class feature", [SrdKinds.Feature]), ("spell", [SrdKinds.Spell]), ("cantrip", [SrdKinds.Spell]),
        ("monster", [SrdKinds.Monster]), ("creature", [SrdKinds.Monster]), ("statblock", [SrdKinds.Monster]),
        ("npc", [SrdKinds.Monster]), ("class", [SrdKinds.Class]), ("subclass", [SrdKinds.Subclass]),
        ("feat", [SrdKinds.Feat]), ("feature", [SrdKinds.Feature]), ("trait", [SrdKinds.Trait]),
        ("condition", [SrdKinds.Condition]), ("background", [SrdKinds.Background]), ("species", [SrdKinds.Species]),
        ("race", [SrdKinds.Race]), ("item", [SrdKinds.MagicItem, SrdKinds.Equipment]),
        ("weapon", [SrdKinds.Equipment, SrdKinds.MagicItem]), ("armor", [SrdKinds.Equipment, SrdKinds.MagicItem]),
        ("armour", [SrdKinds.Equipment, SrdKinds.MagicItem]), ("gear", [SrdKinds.Equipment]),
        ("equipment", [SrdKinds.Equipment]), ("action", [SrdKinds.Rule]), ("rule", [SrdKinds.Rule]),
        ("property", [SrdKinds.WeaponProperty, SrdKinds.WeaponMastery]), ("mastery", [SrdKinds.WeaponMastery]),
        ("language", [SrdKinds.Language]), ("skill", [SrdKinds.Skill]), ("damage", [SrdKinds.DamageType]),
        ("school", [SrdKinds.MagicSchool]), ("poison", [SrdKinds.Poison]),
    ];

    private static readonly string[] LeadingArticles = ["the", "a", "an"];

    private readonly ConcurrentBag<SqliteConnection> _pool;
    private readonly SemaphoreSlim _available;
    private volatile bool _disposed;

    private SrdIndex(string path, IReadOnlyCollection<SqliteConnection> connections, SrdIndexInfo info)
    {
        DatabasePath = path;
        Info = info;
        _pool = new ConcurrentBag<SqliteConnection>(connections);
        _available = new SemaphoreSlim(connections.Count, connections.Count);
    }

    /// <summary>The srd.db file this index reads.</summary>
    public string DatabasePath { get; }

    /// <summary>What the file says about itself: key, content tag, glossary hash, build time, document count.</summary>
    public SrdIndexInfo Info { get; }

    /// <inheritdoc cref="TryOpen(string, string, out string)"/>
    public static SrdIndex? TryOpen(string path, string expectedKey) => TryOpen(path, expectedKey, out _);

    /// <summary>
    /// Opens srd.db read-only if it is a complete index built from the content <paramref name="expectedKey"/> describes,
    /// else returns null and says why in <paramref name="reason"/>. It never throws for the file's state: missing,
    /// empty, garbage, truncated, another schema version or another key all mean "rebuild", and the reason is for the
    /// log line that says why the server rebuilt.
    ///
    /// <para>
    /// Every connection the index will ever use is opened here, back to back before any of them reads, and then each is
    /// checked against <paramref name="expectedKey"/>, so all of them read the file that was validated. Opening them
    /// back to back matters: another dnd-mcp version renaming its build over srd.db between two opens splits the pool
    /// across two files, and when the first connection ran the whole-file check before the others opened, that window
    /// was the length of a 14 MB read and two versions starting together lost the race most of the time. A pool that
    /// still ends up split is opened again; a file that is not current from the first connection on is "not current".
    /// </para>
    /// </summary>
    public static SrdIndex? TryOpen(string path, string expectedKey, out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKey);

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            reason = $"no index at {fullPath}";
            return null;
        }

        // Pooling=False: this class pools its own connections so it controls exactly when the file is opened (here,
        // only) and closed (Dispose), which tests that delete or overwrite srd.db and Windows file locking depend on.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        for (var attempt = 1; ; attempt++)
        {
            var connections = new List<SqliteConnection>(PoolSize);
            try
            {
                while (connections.Count < PoolSize)
                {
                    var connection = new SqliteConnection(connectionString);
                    connections.Add(connection);
                    connection.Open();
                }

                if (Check(connections[0], expectedKey) is { } problem)
                {
                    DisposeAll(connections);
                    reason = problem;
                    return null;
                }

                if (connections.Skip(1).Select(c => Check(c, expectedKey)).FirstOrDefault(p => p is not null) is { } changed)
                {
                    DisposeAll(connections);
                    if (attempt < MaxOpenAttempts)
                    {
                        continue;
                    }

                    reason = $"{fullPath} changed while it was being opened ({changed})";
                    return null;
                }

                // The whole-file check once; the other connections are open on the same file by now.
                if (Damage(connections[0]) is { } damage)
                {
                    DisposeAll(connections);
                    reason = damage;
                    return null;
                }

                reason = "current";
                return new SrdIndex(fullPath, connections, ReadInfo(connections[0]));
            }
            catch (Exception ex) when (IsUnreadable(ex))
            {
                DisposeAll(connections);
                reason = $"{fullPath} is not a readable index ({ex.Message})";
                return null;
            }
        }
    }

    /// <summary>
    /// Full-text search over every searchable document (everything but level records) in <paramref name="editions"/>,
    /// optionally only <paramref name="kinds"/> (wire names, already normalised; see <see cref="SrdKindNames"/>).
    ///
    /// <para>
    /// Words are ANDed; a trailing <c>*</c> makes a word a prefix; a repeated word counts once. When nothing matches
    /// every word and there is more than one word, the search runs again matching ANY word and the result says so
    /// (<see cref="SrdSearchResult.MatchedAllWords"/> false), so the caller can tell the model the hits are partial.
    /// </para>
    /// <para>
    /// Ranking: first the exact tier, the documents <see cref="FindByName"/> finds for the query in each edition (so
    /// "fireball" finds the spell, not every spell mentioning it), in its order (<see cref="SrdNameMatch.Tier"/>, kind
    /// priority, level, slug; then edition), so hit #1 is what <c>rules_get</c> returns for the same text. That includes
    /// its loose forms: "lich stat block" is the Lich (only the monster kind, for a dropped kind word), though the Lich's
    /// text has no "stat block"; before, the one text holding every word (Deck of Illusions' table) was the only hit.
    /// And before that, same-name hits went by bm25, which put stubs first ("fire bolt" gave the tiefling trait "You know
    /// the spell Fire Bolt" above the spell). Everything else follows by bm25 with name weighted 10, aliases 5 and text
    /// 1, then edition, kind and slug so equal scores always come back in the same order.
    /// </para>
    /// </summary>
    /// <exception cref="DndInputException">
    /// The query is longer than <see cref="MaxQueryLength"/>, has more than <see cref="MaxQueryWords"/> distinct words,
    /// or has no word to search for.
    /// </exception>
    public SrdSearchResult Search(string query, IReadOnlyCollection<string> editions, IReadOnlyCollection<string>? kinds = null, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(editions);
        query ??= string.Empty;
        if (limit is < 1 or > MaxSearchLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"limit must be 1–{MaxSearchLimit}.");
        }

        if (editions.Count == 0 || editions.Any(e => !SrdEdition.All.Contains(e)))
        {
            throw new ArgumentException($"editions must be a non-empty subset of {string.Join(", ", SrdEdition.All)}.", nameof(editions));
        }

        if (query.Length > MaxQueryLength)
        {
            throw new DndInputException(
                $"The query is {query.Length.ToString("N0", CultureInfo.InvariantCulture)} characters long; search with at most " +
                $"{MaxQueryLength} characters of key words, e.g. \"grapple escape\" or \"fire*\". To read one entry, use " +
                "rules_get with its name.");
        }

        var wordCount = Fts5Query.WordCount(query);
        if (wordCount > MaxQueryWords)
        {
            throw new DndInputException(
                $"The query has {wordCount} different words; search with at most {MaxQueryWords} key words, e.g. " +
                "\"grapple escape\" or \"fire*\".");
        }

        var allWords = Fts5Query.Terms(query) ?? throw new DndInputException(
            $"The query needs at least one word to search for (letters or digits); \"{query}\" has none. " +
            "Example: \"fireball\", \"grapple escape\" or a prefix like \"fire*\".");

        var exact = ExactTier(SrdNames.Key(query.Replace("*", " ", StringComparison.Ordinal)), editions, kinds, limit);
        var rest = RunSearch(allWords, editions, kinds, limit + exact.Count);
        var matchedAllWords = rest.Count > 0 || wordCount < 2;
        if (!matchedAllWords)
        {
            rest = RunSearch(Fts5Query.AnyTerms(query)!, editions, kinds, limit + exact.Count);
        }

        var hits = exact.Count == 0 ? [] : ExactHits(exact, allWords);
        var inExactTier = hits.Select(h => h.Ref).ToHashSet();
        hits.AddRange(rest.Where(h => !inExactTier.Contains(h.Ref)).Take(limit - hits.Count));
        return new SrdSearchResult(hits, matchedAllWords);
    }

    /// <summary>
    /// The documents in <paramref name="edition"/> named <paramref name="name"/> (compared by <see cref="SrdNames.Key"/>),
    /// optionally only of <paramref name="kind"/>.
    ///
    /// <para>
    /// <b>Order</b> (<see cref="SrdNameLookup.Matches"/>, best first): the name-match tier (<see cref="SrdNameMatch.Tier"/>:
    /// a content kind's own name; another name of a content kind; a reference kind's own name; another name of a
    /// reference kind; a 2014 heading or section name), then kind priority (<see cref="SrdKindNames.LookupPriority"/>),
    /// then the lower class level (graded features), then slug. So 2024 "Goblin" is the monster Goblin Warrior (by its
    /// 2014 name) before the Goblin language, "Acolyte" the background before the monster whose 2014 name it was,
    /// "Shield" the spell before the item and the armor, and 2014 "Bardic Inspiration" the d6 (level 1) before the d10.
    /// Each match says how it matched (<see cref="SrdNameMatch.Source"/>): its own name or one of
    /// <see cref="SrdAliasSources"/>.
    /// </para>
    /// <para>
    /// <b>Loose names.</b> When the name as given matches nothing, it is tried without a leading "the"/"a"/"an", without
    /// a trailing kind word ("Healing Word spell", "Goblin stat block", "Dodge action"), and with its last word's plural
    /// "s" dropped or added ("Death Saving Throws"); the first form that matches is the answer, flagged
    /// <see cref="SrdNameLookup.LooseMatch"/>. A dropped kind word must fit (an entry of that kind must match: "Tough
    /// feat" is not the Tough monster) and puts its kind first ("Acolyte background" is the background, not the NPC).
    /// </para>
    /// <para>
    /// <b>Misses.</b> When nothing matches, the result carries up to <see cref="MaxSuggestions"/> close names in this
    /// edition (<see cref="SrdNameLookup.SuggestionMatches"/> says which alias a suggestion was close to: "Grapling" is
    /// close to Melee Attacks' "Grappling") and the matches in the other edition, so "not found" can say "did you mean"
    /// and "the 2014 SRD has it".
    /// Suggestions, most precise first, each step only when the ones before found nothing: names containing every real
    /// word typed (as prefixes; "of", "the"… never count), shortest first; names within fewer than the most typing errors
    /// allowed of the whole name ("Fierball"); names by how many of the typed words they contain, then names at the most
    /// typing errors allowed ("Fyrebal"); names sharing a word's first four letters. Before, the first typo within reach
    /// ended the search ("The Fiend" offered only Pit Fiend) and a single generic word decided ("Way of the Open Hand"
    /// offered Handaxe first).
    /// </para>
    /// </summary>
    /// <exception cref="DndInputException">The name is longer than <see cref="MaxNameLength"/> or has no letters or digits.</exception>
    public SrdNameLookup FindByName(string name, string edition, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        RequireEdition(edition);
        if (name.Length > MaxNameLength)
        {
            throw new DndInputException(
                $"The name is {name.Length.ToString("N0", CultureInfo.InvariantCulture)} characters long; names are at most " +
                $"{MaxNameLength} characters. Pass just the entry's name, e.g. \"Fireball\", or search for longer text with rules_search.");
        }

        var key = SrdNames.Key(name);
        if (key.Length == 0)
        {
            throw new DndInputException(
                $"The name needs at least one letter or digit; \"{name}\" has none. Example: name \"Fireball\".");
        }

        var found = Resolve(key, edition, kind is null ? null : [kind]);
        if (found.Matches.Count > 0)
        {
            return new SrdNameLookup(found.Matches, [], []) { LooseMatch = found.Loose };
        }

        var otherEdition = edition == SrdEdition.Edition2014 ? SrdEdition.Edition2024 : SrdEdition.Edition2014;
        string? otherKind = null;
        var searchOther = true;
        if (kind is not null)
        {
            otherKind = KindIn(kind, otherEdition);
            searchOther = SrdKinds.ExistsIn(otherKind, otherEdition);
        }

        var other = searchOther ? Resolve(key, otherEdition, otherKind is null ? null : [otherKind]) : NameResolution.None;
        var suggestions = Suggestions(key, edition, kind);
        return new SrdNameLookup([], suggestions.Select(m => m.Document).ToList(), other.Matches)
        {
            LooseMatch = other.Loose,
            SuggestionMatches = suggestions,
        };
    }

    /// <summary>
    /// The other edition's documents that are the same entry as <paramref name="doc"/>. Usually one; several where the
    /// SRD split an entry (2014 <c>succubus-incubus</c> → 2024 <c>incubus</c>, <c>succubus</c>) or 2014 graded or split
    /// what 2024 kept whole (four Bardic Inspiration dice, four insect swarms); none for an entry in one edition only.
    ///
    /// <para>
    /// Ordered so the first is the one to compare when nothing else says which: the record with the same slug (2024 Swarm
    /// of Insects → 2014 Swarm of Insects, not Swarm of Beetles), then by kind, then the lower class level (the grade a
    /// class gains first: Bardic Inspiration's d6 at level 1, not the d10 that slug order put first), then slug.
    /// </para>
    /// </summary>
    public IReadOnlyList<SrdDocument> Counterparts(SrdDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var (self, other) = doc.Edition == SrdEdition.Edition2014 ? ("doc_2014", "doc_2024") : ("doc_2024", "doc_2014");
        return Query(
            $"""
            SELECT {DocColumns}
            FROM doc s
            JOIN counterpart c ON c.{self} = s.id
            JOIN doc d ON d.id = c.{other}
            WHERE s.edition = $edition AND s.kind = $kind AND s.slug = $slug
            ORDER BY d.slug = s.slug DESC, d.kind, d.level IS NULL, d.level, d.slug;
            """,
            ("$edition", doc.Edition), ("$kind", doc.Kind), ("$slug", doc.Slug));
    }

    /// <summary>
    /// <see cref="Counterparts(SrdDocument)"/>, with the ones that answer to <paramref name="preferName"/> (their own name
    /// or an alias, <see cref="SrdDocument.AnswersTo"/>) first and otherwise in the same order. For edition "both": the
    /// name the caller typed says which of several counterparts they meant ("Swarm of Wasps" reaches 2024 Swarm of
    /// Insects, whose first counterpart is 2014 Swarm of Insects; the wasps are the ones to compare).
    /// </summary>
    public IReadOnlyList<SrdDocument> Counterparts(SrdDocument doc, string? preferName)
    {
        var counterparts = Counterparts(doc);
        return preferName is null || counterparts.Count < 2
            ? counterparts
            : [.. counterparts.OrderBy(d => d.AnswersTo(preferName) ? 0 : 1)];
    }

    /// <inheritdoc />
    public SrdDocument? Get(string edition, string kind, string slug)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(slug);
        return Query(
            $"SELECT {DocColumns} FROM doc d WHERE d.edition = $edition AND d.kind = $kind AND d.slug = $slug;",
            ("$edition", edition), ("$kind", kind), ("$slug", slug)).SingleOrDefault();
    }

    /// <summary>The document at <paramref name="reference"/>, or null.</summary>
    public SrdDocument? Get(SrdRef reference) => Get(reference.Edition, reference.Kind, reference.Slug);

    /// <inheritdoc />
    public IReadOnlyList<SrdDocument> ClassLevels(string edition, string classSlug) =>
        Query(
            $"""
            SELECT {DocColumns} FROM doc d
            WHERE d.kind = 'level' AND d.edition = $edition AND d.class_slug = $slug AND d.subclass_slug IS NULL
            ORDER BY d.level, d.slug;
            """,
            ("$edition", edition), ("$slug", classSlug));

    /// <inheritdoc />
    public IReadOnlyList<SrdDocument> SubclassLevels(string edition, string subclassSlug) =>
        Query(
            $"""
            SELECT {DocColumns} FROM doc d
            WHERE d.kind = 'level' AND d.edition = $edition AND d.subclass_slug = $slug
            ORDER BY d.level, d.slug;
            """,
            ("$edition", edition), ("$slug", subclassSlug));

    /// <summary>Documents per (edition, kind), counted from the rows themselves, ordered by edition then kind.</summary>
    public IReadOnlyList<SrdKindCount> Counts() =>
        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT edition, kind, COUNT(*) FROM doc GROUP BY edition, kind ORDER BY edition, kind;";
            using var reader = command.ExecuteReader();
            var counts = new List<SrdKindCount>();
            while (reader.Read())
            {
                counts.Add(new SrdKindCount(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            }

            return counts;
        });

    /// <summary>
    /// Closes every connection: idle ones now, ones a call is using when that call returns them. Calls after this (and
    /// calls still waiting for a connection) throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        DrainPool();
    }

    private List<SrdSearchHit> RunSearch(
        string match,
        IReadOnlyCollection<string> editions,
        IReadOnlyCollection<string>? kinds,
        int limit)
    {
        var parameters = new List<(string, object)> { ("$match", match), ("$limit", limit) };
        var editionList = InList("$e", editions, parameters);
        var kindFilter = kinds is { Count: > 0 } ? $"AND d.kind IN ({InList("$k", kinds, parameters)})" : string.Empty;

        // snippet() and bm25() need doc_fts to be the table the MATCH is on, so the join goes from it to doc.
        var sql = $"""
            SELECT d.edition, d.kind, d.slug, d.name, snippet(doc_fts, 2, '**', '**', '…', 16)
            FROM doc_fts
            JOIN doc d ON d.id = doc_fts.rowid
            WHERE doc_fts MATCH $match
              AND d.searchable = 1
              AND d.edition IN ({editionList})
              {kindFilter}
            ORDER BY bm25(doc_fts, 10.0, 5.0, 1.0), d.edition, d.kind, d.slug
            LIMIT $limit;
            """;

        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = command.ExecuteReader();
            var hits = new List<SrdSearchHit>();
            while (reader.Read())
            {
                hits.Add(new SrdSearchHit(
                    new SrdRef(reader.GetString(0), reader.GetString(1), reader.GetString(2)),
                    reader.GetString(3),
                    Whitespace().Replace(reader.IsDBNull(4) ? string.Empty : reader.GetString(4), " ").Trim(),
                    ExactName: false));
            }

            return hits;
        });
    }

    // The search's exact tier: in each edition, what FindByName finds for the key (a dropped kind word keeping only its
    // kinds), minus level records, which search never shows; ordered as FindByName orders one edition, then by edition.
    private List<(SrdNameMatch Match, string Key)> ExactTier(
        string key, IReadOnlyCollection<string> editions, IReadOnlyCollection<string>? kinds, int limit)
    {
        if (key.Length == 0)
        {
            return [];
        }

        var found = new List<(SrdNameMatch Match, int Wanted, string Key)>();
        foreach (var edition in editions.Distinct(StringComparer.Ordinal))
        {
            var resolution = Resolve(key, edition, kinds is { Count: > 0 } ? kinds : null);
            foreach (var match in resolution.Matches.Where(m => m.Document.Kind != SrdKinds.Level))
            {
                var wanted = resolution.KindWordKinds?.IndexOf(match.Document.Kind) ?? 0;
                if (wanted >= 0)
                {
                    found.Add((match, wanted, resolution.Key));
                }
            }
        }

        return found
            .OrderBy(f => f.Wanted)
            .ThenBy(f => f.Match.Tier)
            .ThenBy(f => SrdKindNames.PriorityOf(f.Match.Document.Kind))
            .ThenBy(f => f.Match.Document.Level ?? int.MaxValue)
            .ThenBy(f => f.Match.Document.Slug, StringComparer.Ordinal)
            .ThenBy(f => f.Match.Document.Edition, StringComparer.Ordinal)
            .DistinctBy(f => f.Match.Document.Ref)
            .Take(limit)
            .Select(f => (f.Match, f.Key))
            .ToList();
    }

    // The exact tier as hits. Each snippet shows the query's words where the entry has them all, else the words of the
    // name it matched by (a loose form: the Lich's text has "lich" but no "stat block").
    private List<SrdSearchHit> ExactHits(List<(SrdNameMatch Match, string Key)> exact, string allWords)
    {
        var refs = exact.Select(e => e.Match.Document.Ref.ToString()).ToList();
        var snippets = Snippets(allWords, refs);
        foreach (var group in exact.Where(e => !snippets.ContainsKey(e.Match.Document.Ref.ToString())).GroupBy(e => e.Key))
        {
            if (Fts5Query.Terms(group.Key) is { } nameWords)
            {
                foreach (var (reference, snippet) in Snippets(nameWords, group.Select(e => e.Match.Document.Ref.ToString()).ToList()))
                {
                    snippets[reference] = snippet;
                }
            }
        }

        return exact
            .Select(e => new SrdSearchHit(
                e.Match.Document.Ref,
                e.Match.Document.Name,
                snippets.GetValueOrDefault(e.Match.Document.Ref.ToString(), string.Empty),
                ExactName: true))
            .ToList();
    }

    // Snippets of the given documents (by ref) that match the FTS text.
    private Dictionary<string, string> Snippets(string match, IReadOnlyCollection<string> refs)
    {
        var parameters = new List<(string, object)> { ("$match", match) };
        var refList = InList("$r", refs, parameters);
        var sql = $"""
            SELECT d.edition || '/' || d.kind || '/' || d.slug, snippet(doc_fts, 2, '**', '**', '…', 16)
            FROM doc_fts
            JOIN doc d ON d.id = doc_fts.rowid
            WHERE doc_fts MATCH $match AND d.edition || '/' || d.kind || '/' || d.slug IN ({refList});
            """;

        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = command.ExecuteReader();
            var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                snippets[reader.GetString(0)] = Whitespace().Replace(reader.IsDBNull(1) ? string.Empty : reader.GetString(1), " ").Trim();
            }

            return snippets;
        });
    }

    // The exact matches for the key, else for the first loose form of it that has any.
    private NameResolution Resolve(string key, string edition, IReadOnlyCollection<string>? kinds)
    {
        var exact = ExactMatches(key, edition, kinds);
        if (exact.Count > 0)
        {
            return new NameResolution(exact, key, Loose: false, KindWordKinds: null);
        }

        foreach (var (looseKey, kindWordKinds) in LooseForms(key))
        {
            var matches = ExactMatches(looseKey, edition, kinds);
            if (kindWordKinds is null)
            {
                if (matches.Count > 0)
                {
                    return new NameResolution(matches, looseKey, Loose: true, KindWordKinds: null);
                }

                continue;
            }

            // A dropped kind word must be right: "Tough feat" is not the Tough monster. Its kinds come first, in its
            // order ("Acolyte background" is the background, "Shield armor" the armor before the magic shield), then
            // the rest as before, so the footer still lists them.
            var wanted = kindWordKinds.Select(k => KindIn(k, edition)).ToList();
            if (matches.Any(m => wanted.Contains(m.Document.Kind)))
            {
                var ordered = matches.OrderBy(m => wanted.IndexOf(m.Document.Kind) is var i and >= 0 ? i : wanted.Count).ToList();
                return new NameResolution(ordered, looseKey, Loose: true, KindWordKinds: wanted);
            }
        }

        return NameResolution.None;
    }

    // The name key without a leading article and without a trailing kind word, then that with its last word's plural "s"
    // dropped or added. Each form comes with the kinds its dropped kind word means (null when none was dropped). A kind
    // phrase that is the whole rest of the name is kept: "Magic Item" is not "Magic".
    private static IEnumerable<(string Key, string[]? Kinds)> LooseForms(string key)
    {
        var words = key.Split(' ');
        var start = words.Length > 1 && LeadingArticles.Contains(words[0]) ? 1 : 0;
        var end = words.Length;
        string[]? kinds = null;
        foreach (var (kindWords, kindNames) in TrailingKindWords)
        {
            var count = kindWords.Count(ch => ch == ' ') + 1;
            if (end - start >= count && string.Join(' ', words[(end - count)..end]) == kindWords)
            {
                if (end - start > count)
                {
                    end -= count;
                    kinds = kindNames;
                }

                break;
            }
        }

        var core = words[start..end];
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        var candidates = new List<(string, string[]?)>();
        void Add(IEnumerable<string> form)
        {
            var text = string.Join(' ', form);
            if (seen.Add(text))
            {
                candidates.Add((text, kinds));
            }
        }

        Add(core);
        var last = core[^1];
        var otherNumber = last.EndsWith('s') && !last.EndsWith("ss", StringComparison.Ordinal) && last.Length > 3
            ? last[..^1]
            : last + "s";
        Add(core[..^1].Append(otherNumber));
        return candidates;
    }

    private List<SrdNameMatch> ExactMatches(string key, string edition, IReadOnlyCollection<string>? kinds)
    {
        var parameters = new List<(string, object)> { ("$edition", edition), ("$key", key) };
        var kindFilter = kinds is null ? string.Empty : $"AND d.kind IN ({InList("$k", kinds, parameters)})";
        var sql = $"""
            SELECT {DocColumns}, NULL, NULL
            FROM doc d
            WHERE d.edition = $edition AND d.name_key = $key {kindFilter}
            UNION ALL
            SELECT {DocColumns}, a.alias, a.source
            FROM alias a
            JOIN doc d ON d.id = a.doc_id
            WHERE d.edition = $edition AND a.alias_key = $key {kindFilter};
            """;

        var matches = WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = command.ExecuteReader();
            var found = new List<SrdNameMatch>();
            while (reader.Read())
            {
                found.Add(reader.IsDBNull(DocColumnCount)
                    ? new SrdNameMatch(ReadDocument(reader), null, SrdNameMatch.OwnName)
                    : new SrdNameMatch(ReadDocument(reader), reader.GetString(DocColumnCount), reader.GetString(DocColumnCount + 1)));
            }

            return found;
        });

        return matches
            .OrderBy(m => m.Tier)
            .ThenBy(m => SrdKindNames.PriorityOf(m.Document.Kind))
            .ThenBy(m => m.Document.Level ?? int.MaxValue)
            .ThenBy(m => m.Document.Slug, StringComparer.Ordinal)
            .ToList();
    }

    // Close names for a failed lookup; see FindByName for the order and why.
    private List<SrdNameMatch> Suggestions(string key, string edition, string? kind)
    {
        var allWords = key.Split(' ').Distinct(StringComparer.Ordinal).ToList();
        var words = allWords.Where(w => !StopWords.Contains(w)).Take(MaxSuggestionWords).ToList();
        if (words.Count == 0)
        {
            words = allWords.Take(MaxSuggestionWords).ToList();
        }

        // 1. Every real word as a prefix of a name word ("fire bol" → Fire Bolt), shortest name first.
        if (Fts5Query.ColumnFiltered(string.Join(' ', words.Select(w => w + "*")), ["name"]) is { } everyWord &&
            NameSearch(everyWord, edition, kind) is { Count: > 0 } found)
        {
            return Matches(edition, found.OrderBy(c => c.NameLength).ThenBy(c => c.Priority).ThenBy(c => c.Slug, StringComparer.Ordinal));
        }

        // 2. Close to the whole name, to it without stop words, or to a loose form of it: "Fierball", "magic misile",
        // "Healing Wurd spell"; a name or an alias ("Grapling" is Melee Attacks' "Grappling"). A typo at the most edits
        // allowed is weak evidence; it waits behind word matches.
        var keys = new List<string> { key, string.Join(' ', words) };
        keys.AddRange(LooseForms(key).Select(f => f.Key));
        var near = NearNames(keys.Distinct(StringComparer.Ordinal).ToList(), edition, kind);
        var strong = near.Where(n => n.Distance < n.MaxEdits).ToList();
        if (strong.Count > 0)
        {
            return Matches(edition, strong.Select(n => n.Candidate));
        }

        // 3–4. Names by how many of the typed words (real words of 2+ letters) they contain, most first, then the weak
        // typos: "Way of the Open Hand" is Open Hand (two words) before Handaxe (one).
        var significant = words.Where(w => w.Length > 1).ToList();
        var ranked = ByWordsMatched(significant.Select(w => w + "*"), edition, kind)
            .Concat(near.Select(n => n.Candidate))
            .ToList();
        if (ranked.Count > 0)
        {
            return Matches(edition, ranked);
        }

        // 5. Nothing closer: names sharing a word's first four letters, a last resort for badly mistyped long words.
        return Matches(edition, ByWordsMatched(significant.Where(w => w.Length > 4).Select(w => w[..4] + "*"), edition, kind));
    }

    // Names matching any of the prefix patterns, by how many they match, then shortest, kind priority and slug.
    private List<Candidate> ByWordsMatched(IEnumerable<string> patterns, string edition, string? kind)
    {
        var hits = new Dictionary<long, (Candidate Candidate, int Words)>();
        foreach (var pattern in patterns.Distinct(StringComparer.Ordinal))
        {
            foreach (var candidate in NameSearch(Fts5Query.ColumnFiltered(pattern, ["name"])!, edition, kind))
            {
                hits[candidate.Id] = (candidate, hits.GetValueOrDefault(candidate.Id).Words + 1);
            }
        }

        return hits.Values
            .OrderByDescending(h => h.Words)
            .ThenBy(h => h.Candidate.NameLength)
            .ThenBy(h => h.Candidate.Priority)
            .ThenBy(h => h.Candidate.Slug, StringComparer.Ordinal)
            .Select(h => h.Candidate)
            .ToList();
    }

    // The first MaxSuggestions distinct candidates, as matches (the alias a candidate was close to, if not its name).
    private List<SrdNameMatch> Matches(string edition, IEnumerable<Candidate> candidates) =>
        candidates
            .DistinctBy(c => c.Id)
            .Take(MaxSuggestions)
            .ToList()
            .Select(c => new SrdNameMatch(Get(edition, c.Kind, c.Slug)!, c.Alias, c.Source))
            .ToList();

    // Searchable documents whose name matches the FTS text (a column-filtered name query).
    private List<Candidate> NameSearch(string match, string edition, string? kind)
    {
        var kindFilter = kind is null ? string.Empty : "AND d.kind = $kind";
        return WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT d.id, d.kind, d.slug, length(d.name)
                FROM doc_fts
                JOIN doc d ON d.id = doc_fts.rowid
                WHERE doc_fts MATCH $match AND d.searchable = 1 AND d.edition = $edition {kindFilter};
                """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$edition", edition);
            if (kind is not null)
            {
                command.Parameters.AddWithValue("$kind", kind);
            }

            using var reader = command.ExecuteReader();
            var found = new List<Candidate>();
            while (reader.Read())
            {
                var candidateKind = reader.GetString(1);
                found.Add(new Candidate(reader.GetInt64(0), candidateKind, reader.GetString(2), reader.GetInt32(3), SrdKindNames.PriorityOf(candidateKind)));
            }

            return found;
        });
    }

    // Searchable documents whose name or an alias is within a few edits of any of the keys: one edit for keys up to 4
    // characters, two up to 8, three beyond. Per document its closest name (its own before an alias at the same
    // distance); closest first, then shortest, kind priority and slug. A few thousand short strings per edition, so a
    // scan is cheaper than any index for them, and a key much longer than every name costs one length comparison each.
    private List<(Candidate Candidate, int Distance, int MaxEdits)> NearNames(IReadOnlyList<string> keys, string edition, string? kind)
    {
        var kindFilter = kind is null ? string.Empty : "AND d.kind = $kind";
        var near = WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT d.id, d.kind, d.slug, length(d.name), d.name_key, NULL, NULL
                FROM doc d WHERE d.edition = $edition AND d.searchable = 1 {kindFilter}
                UNION ALL
                SELECT d.id, d.kind, d.slug, length(a.alias), a.alias_key, a.alias, a.source
                FROM alias a JOIN doc d ON d.id = a.doc_id WHERE d.edition = $edition AND d.searchable = 1 {kindFilter};
                """;
            command.Parameters.AddWithValue("$edition", edition);
            if (kind is not null)
            {
                command.Parameters.AddWithValue("$kind", kind);
            }

            using var reader = command.ExecuteReader();
            var found = new List<(Candidate Candidate, int Distance, int MaxEdits)>();
            while (reader.Read())
            {
                var nameKey = reader.GetString(4);
                (int Distance, int MaxEdits)? best = null;
                foreach (var key in keys)
                {
                    var maxEdits = key.Length <= 4 ? 1 : key.Length <= 8 ? 2 : 3;
                    var distance = SrdNames.EditDistance(key, nameKey, maxEdits);
                    if (distance <= maxEdits && (best is null || distance < best.Value.Distance))
                    {
                        best = (distance, maxEdits);
                    }
                }

                if (best is { } hit)
                {
                    var candidateKind = reader.GetString(1);
                    var candidate = reader.IsDBNull(5)
                        ? new Candidate(reader.GetInt64(0), candidateKind, reader.GetString(2), reader.GetInt32(3), SrdKindNames.PriorityOf(candidateKind))
                        : new Candidate(reader.GetInt64(0), candidateKind, reader.GetString(2), reader.GetInt32(3), SrdKindNames.PriorityOf(candidateKind))
                        {
                            Alias = reader.GetString(5),
                            Source = reader.GetString(6),
                        };
                    found.Add((candidate, hit.Distance, hit.MaxEdits));
                }
            }

            return found;
        });

        return near
            .OrderBy(n => n.Distance)
            .ThenBy(n => n.Candidate.Alias is null ? 0 : 1)
            .ThenBy(n => n.Candidate.NameLength)
            .DistinctBy(n => n.Candidate.Id)
            .OrderBy(n => n.Distance)
            .ThenBy(n => n.Candidate.NameLength)
            .ThenBy(n => n.Candidate.Priority)
            .ThenBy(n => n.Candidate.Slug, StringComparer.Ordinal)
            .ToList();
    }

    private List<SrdDocument> Query(string sql, params (string Name, object Value)[] parameters) =>
        WithConnection(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            using var reader = command.ExecuteReader();
            var documents = new List<SrdDocument>();
            while (reader.Read())
            {
                documents.Add(ReadDocument(reader));
            }

            return documents;
        });

    // Columns in DocColumns order: edition, kind, slug, name, json, aliases, corrections, level.
    private static SrdDocument ReadDocument(SqliteDataReader reader)
    {
        var aliases = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        var corrections = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
        return new SrdDocument
        {
            Edition = reader.GetString(0),
            Kind = reader.GetString(1),
            Slug = reader.GetString(2),
            Name = reader.GetString(3),
            Json = reader.GetString(4),
            Aliases = aliases.Length == 0
                ? []
                : aliases.Split('\u001e').Select(a => a.Split('\u001f', 2)).Select(p => new SrdAlias(p[1], p[0])).ToList(),
            Corrections = corrections.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Level = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        };
    }

    // Runs one query on a pooled connection. Never call it from inside another's action: with every connection held by
    // a call waiting for a second one, the pool would deadlock.
    private T WithConnection<T>(Func<SqliteConnection, T> action)
    {
        var connection = Rent();
        try
        {
            return action(connection);
        }
        catch (SqliteException ex)
        {
            // Some damage has no telling code: srd.db truncated in place leaves an empty schema, and every query fails
            // with "no such table" (code 1, the code of an ordinary SQL error). So any other error re-checks that the
            // file under this connection is still the index it opened; only when it is does the error stand as a bug.
            if (UnreadableFileCodes.Contains(ex.SqliteErrorCode) || !StillCurrent(connection))
            {
                throw new SrdIndexUnavailableException(
                    $"srd.db at {DatabasePath} can no longer be read ({ex.Message}). It was changed or damaged while this " +
                    "server was running; the next rules call opens it again, rebuilding it if needed.",
                    ex);
            }

            throw;
        }
        finally
        {
            Return(connection);
        }
    }

    // Whether this connection still reads an index of this schema and key. Never throws: an unreadable file is "no".
    private bool StillCurrent(SqliteConnection connection)
    {
        try
        {
            return Check(connection, Info.StalenessKey) is null;
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            return false;
        }
    }

    private SqliteConnection Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _available.Wait();
        if (_disposed || !_pool.TryTake(out var connection))
        {
            _available.Release();
            throw new ObjectDisposedException(nameof(SrdIndex));
        }

        return connection;
    }

    private void Return(SqliteConnection connection)
    {
        if (_disposed)
        {
            connection.Dispose();
        }
        else
        {
            _pool.Add(connection);

            // Dispose may have drained the pool between the check and the Add; drain again so nothing stays open.
            if (_disposed)
            {
                DrainPool();
            }
        }

        _available.Release();
    }

    private void DrainPool()
    {
        while (_pool.TryTake(out var connection))
        {
            connection.Dispose();
        }
    }

    private static void DisposeAll(IEnumerable<SqliteConnection> connections)
    {
        foreach (var connection in connections)
        {
            connection.Dispose();
        }
    }

    // Null when the connection holds an index of this schema version and key; otherwise what is wrong with it.
    private static string? Check(SqliteConnection connection, string expectedKey)
    {
        var userVersion = Convert.ToInt64(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (userVersion != SrdIndexSchema.Version)
        {
            return $"schema version {userVersion.ToString(CultureInfo.InvariantCulture)}, expected {SrdIndexSchema.Version.ToString(CultureInfo.InvariantCulture)}";
        }

        var key = Scalar(connection, $"SELECT value FROM meta WHERE key = '{SrdIndexSchema.MetaStalenessKey}';") as string;
        return string.Equals(key, expectedKey, StringComparison.Ordinal)
            ? null
            : "built from different content or by a different importer (staleness key differs)";
    }

    // Null when the whole file reads back intact; otherwise what SQLite found. A full read of the file, so done once.
    private static string? Damage(SqliteConnection connection) =>
        Scalar(connection, "PRAGMA quick_check(1);") is string verdict && verdict != "ok"
            ? $"damaged ({verdict.ReplaceLineEndings(" ")})"
            : null;

    private static SrdIndexInfo ReadInfo(SqliteConnection connection)
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, value FROM meta;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                meta[reader.GetString(0)] = reader.GetString(1);
            }
        }

        string Required(string key) =>
            meta.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"srd.db meta has no '{key}'.");

        return new SrdIndexInfo(
            Required(SrdIndexSchema.MetaStalenessKey),
            int.Parse(Required(SrdIndexSchema.MetaSchemaVersion), NumberStyles.None, CultureInfo.InvariantCulture),
            Required(SrdIndexSchema.MetaContentTag),
            Required(SrdIndexSchema.MetaContentFingerprint),
            Required(SrdIndexSchema.MetaGlossarySha256),
            DateTimeOffset.Parse(Required(SrdIndexSchema.MetaBuiltAtUtc), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            int.Parse(Required(SrdIndexSchema.MetaDocumentCount), NumberStyles.None, CultureInfo.InvariantCulture));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    // Anything reading a damaged or foreign file can throw: SQLite errors (not a database, malformed image, missing
    // table), I/O and permission errors, and bad or missing meta values.
    private static bool IsUnreadable(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException
            or OverflowException or InvalidCastException or InvalidOperationException;

    private static string InList(string prefix, IReadOnlyCollection<string> values, List<(string, object)> parameters)
    {
        var names = new List<string>();
        foreach (var value in values.Distinct(StringComparer.Ordinal))
        {
            var name = prefix + names.Count.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add((name, value));
        }

        return string.Join(", ", names);
    }

    // race ⇄ species and subrace ⇄ subspecies, towards whichever the edition has.
    private static string KindIn(string kind, string edition) =>
        edition == SrdEdition.Edition2024 ? SrdCounterparts.KindIn2024(kind) : SrdCounterparts.KindIn2014(kind);

    private static void RequireEdition(string edition)
    {
        if (!SrdEdition.All.Contains(edition))
        {
            throw new ArgumentException($"edition must be one of {string.Join(", ", SrdEdition.All)} (got \"{edition}\").", nameof(edition));
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // A suggestion candidate before its document is fetched: close by its own name, or by Alias (from Source).
    private readonly record struct Candidate(long Id, string Kind, string Slug, int NameLength, int Priority)
    {
        public string? Alias { get; init; }

        public string Source { get; init; } = SrdNameMatch.OwnName;
    }

    // What Resolve found: the matches, the key (the name's or a loose form's) they matched, whether it was a loose form,
    // and the kinds a dropped trailing kind word named (they come first; search keeps only them).
    private sealed record NameResolution(List<SrdNameMatch> Matches, string Key, bool Loose, List<string>? KindWordKinds)
    {
        public static NameResolution None => new([], string.Empty, false, null);
    }
}

/// <summary>What srd.db records about its own build (the <c>meta</c> table).</summary>
public sealed record SrdIndexInfo(
    string StalenessKey,
    int SchemaVersion,
    string ContentTag,
    string ContentFingerprint,
    string GlossarySha256,
    DateTimeOffset BuiltAtUtc,
    int DocumentCount);

/// <summary>
/// One search hit. <see cref="Snippet"/> is a window of the matched text with the matching words in <c>**bold**</c>,
/// on one line. <see cref="ExactName"/> is true for the exact tier: the documents <see cref="SrdIndex.FindByName"/>
/// finds for the query (its name or an alias IS the query, or a loose form of it: "lich stat block" is the Lich).
/// </summary>
public sealed record SrdSearchHit(SrdRef Ref, string Name, string Snippet, bool ExactName)
{
    public string Edition => Ref.Edition;

    public string Kind => Ref.Kind;
}

/// <summary>
/// Hits in rank order. <see cref="MatchedAllWords"/> is false when no text has every word and the hits after the
/// exact tier match only some of them; say so to the reader, or a partial match reads as a precise one.
/// </summary>
public sealed record SrdSearchResult(IReadOnlyList<SrdSearchHit> Hits, bool MatchedAllWords);

/// <summary>
/// A name-lookup match. <see cref="MatchedAlias"/> is the alias that matched, or null for the document's own name;
/// <see cref="Source"/> says where that name comes from: <see cref="OwnName"/>, or one of <see cref="SrdAliasSources"/>
/// (a glossary bare name, the other edition's name, a curated name, a 2014 rule's subsection heading, a 2024 name a 2014
/// section covers). The reader needs to be told which: "Thug" is the 2014 name of 2024's Tough, and "Grappling" is
/// covered by 2014 Melee Attacks, not an entry of its own.
/// </summary>
public sealed record SrdNameMatch(SrdDocument Document, string? MatchedAlias, string Source)
{
    /// <summary><see cref="Source"/> for a match on the document's own name.</summary>
    public const string OwnName = "name";

    public bool IsOwnName => MatchedAlias is null;

    /// <summary>
    /// How strong the match is, 0 (best) to 4; name lookups and search's exact tier order by it before kind priority:
    /// <list type="table">
    /// <item><term>0</term><description>the own name of a content kind (anything but <see cref="SrdKindNames.ReferenceKinds"/>): 2024 "Acolyte" the background</description></item>
    /// <item><term>1</term><description>a qualifier, counterpart or curated alias of a content kind: 2024 "Goblin" the monster Goblin Warrior, "Shove" Unarmed Strike</description></item>
    /// <item><term>2</term><description>the own name of a reference kind: the Goblin language</description></item>
    /// <item><term>3</term><description>any other alias of a reference kind</description></item>
    /// <item><term>4</term><description>a 2014 subsection heading or section name: "Grappling" in Melee Attacks</description></item>
    /// </list>
    /// A name another edition uses therefore never beats an entry that owns it in this one ("Berserker" is the monster,
    /// not the subclass whose 2014 name it was), while a vocabulary record never beats the thing a person means.
    /// </summary>
    public int Tier => Source switch
    {
        SrdAliasSources.Heading or SrdAliasSources.Section => 4,
        _ when SrdKindNames.ReferenceKinds.Contains(Document.Kind) => IsOwnName ? 2 : 3,
        _ => IsOwnName ? 0 : 1,
    };
}

/// <summary>
/// The result of <see cref="SrdIndex.FindByName"/>. <see cref="Matches"/> is best first; when it is empty,
/// <see cref="Suggestions"/> (close names in the same edition) and <see cref="OtherEdition"/> (matches in the other
/// edition) say where to look instead.
/// </summary>
public sealed record SrdNameLookup(
    IReadOnlyList<SrdNameMatch> Matches,
    IReadOnlyList<SrdDocument> Suggestions,
    IReadOnlyList<SrdNameMatch> OtherEdition)
{
    private readonly IReadOnlyList<SrdNameMatch>? _suggestionMatches;

    public SrdNameMatch? Best => Matches.Count > 0 ? Matches[0] : null;

    /// <summary>
    /// <see cref="Suggestions"/> as matches, in the same order: each says which of the document's names the typed one
    /// was close to (<see cref="SrdNameMatch.MatchedAlias"/>, null for its own name). "Grapling" in 2014 suggests Melee
    /// Attacks by its "Grappling" heading; a reader shown only "Melee Attacks" would not see why.
    /// </summary>
    public IReadOnlyList<SrdNameMatch> SuggestionMatches
    {
        get => _suggestionMatches ?? Suggestions.Select(d => new SrdNameMatch(d, null, SrdNameMatch.OwnName)).ToList();
        init => _suggestionMatches = value;
    }

    /// <summary>
    /// True when nothing is named exactly what was asked and <see cref="Matches"/> (or, when that is empty,
    /// <see cref="OtherEdition"/>) are for the name without a leading "the", without a trailing kind word ("Healing Word
    /// spell"), or with its plural "s" dropped or added. A caller may say so: "No entry is named exactly …".
    /// </summary>
    public bool LooseMatch { get; init; }
}
