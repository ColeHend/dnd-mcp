using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>What to search for (contract §3.9). Every list is optional; null or blank <see cref="Query"/> lists instead of searching.</summary>
/// <param name="Query">Words, ANDed; a trailing <c>*</c> makes a word a prefix. At most 500 characters and 32 words.</param>
/// <param name="Kinds">Entity kinds to keep (forgiving spelling).</param>
/// <param name="Statuses">Statuses to keep, compared with the status the perspective is shown (a withheld question is open outside the author view).</param>
/// <param name="Tags">Tags to keep (an entity must have at least one); never matches a disguised entity, whose tags are not shown.</param>
/// <param name="Perspective">Whose view (default author).</param>
/// <param name="IncludeFacts">Search facts too (default true); only when no kind, status or tag filter is given and there is a query.</param>
/// <param name="AsOfSession">A point in time: rows as they stood at the end of that session (FTS text is always today's).</param>
/// <param name="Limit">Page size (default 15, at most 50).</param>
/// <param name="Cursor">The <see cref="SearchResult.NextCursor"/> of the previous page.</param>
public sealed record SearchRequest(
    string? Query = null,
    IReadOnlyList<string>? Kinds = null,
    IReadOnlyList<string>? Statuses = null,
    IReadOnlyList<string>? Tags = null,
    Perspective? Perspective = null,
    bool IncludeFacts = true,
    int? AsOfSession = null,
    int? Limit = null,
    string? Cursor = null);

/// <summary>One entity found.</summary>
/// <param name="Ref">The perspective-safe ref.</param>
/// <param name="Kind">The kind.</param>
/// <param name="DisplayName">The name this perspective knows it by.</param>
/// <param name="Status">
/// The status as shown (withheld/lean → open outside the author view); null for a disguised entity. For a beat in the
/// author view that can happen next, followed by <see cref="CampaignSearch.ReachableMark"/> ("pending · reachable now").
/// </param>
/// <param name="Snippet">Words around the match, built in C# from text the perspective may see; null when there is none.</param>
/// <param name="SnippetFrom">Which field the snippet quotes: one of <see cref="SnippetFields"/> (secret and hidden_aliases for the author only).</param>
/// <param name="MatchTier">One of <see cref="SearchTiers"/>.</param>
public sealed record EntityHit(string Ref, string Kind, string DisplayName, string? Status, string? Snippet, string? SnippetFrom, string MatchTier);

/// <summary>One fact found.</summary>
/// <param name="Ref"><c>f:&lt;n&gt;</c>.</param>
/// <param name="Code">Its register code, if any.</param>
/// <param name="Text">The statement (author) or the perspective's phrasing of it.</param>
/// <param name="Truth">Author view only.</param>
/// <param name="CanonStatus">Author view only.</param>
/// <param name="KnownBy">Author view only: every knower holding it in an aware state (<c>party</c>, <c>character:belmakor</c>, …).</param>
public sealed record FactHit(string Ref, string? Code, string Text, string? Truth, string? CanonStatus, IReadOnlyList<string>? KnownBy);

/// <summary>A page of results.</summary>
/// <param name="Entities">Entities on this page (they come before facts in the combined list).</param>
/// <param name="Facts">Facts on this page.</param>
/// <param name="NextCursor">The cursor for the next page; null on the last.</param>
/// <param name="PartialMatch">Nothing visible matched every word, so these match any word (say so).</param>
/// <param name="Total">How many results the whole (filtered) list has.</param>
public sealed record SearchResult(IReadOnlyList<EntityHit> Entities, IReadOnlyList<FactHit> Facts, string? NextCursor, bool PartialMatch, int Total);

/// <summary>The field an <see cref="EntityHit.Snippet"/> quotes.</summary>
public static class SnippetFields
{
    public const string Summary = "summary";
    public const string Body = "body";
    public const string Aliases = "aliases";
    public const string Tags = "tags";

    /// <summary>Author view only.</summary>
    public const string Secret = "secret";

    /// <summary>Author view only: restricted and author aliases.</summary>
    public const string HiddenAliases = "hidden_aliases";
}

/// <summary>How an entity matched, best first.</summary>
public static class SearchTiers
{
    /// <summary>The query is the entity's shown name, a shown alias, or the perspective's name for it.</summary>
    public const string Exact = "exact";

    /// <summary>The query's words are in the name the perspective knows it by (a known_as).</summary>
    public const string KnownAs = "known_as";

    /// <summary>Full-text match, ranked by bm25.</summary>
    public const string Text = "text";

    /// <summary>No query: a filtered listing.</summary>
    public const string List = "list";
}

/// <summary>
/// campaign_search's reader (contract §3.9): full-text search and filtered listing of one campaign's entities and facts,
/// through the perspective filter.
///
/// <para>
/// <b>How a non-author search stays leak-free, layer by layer.</b>
/// (1) The query reaches FTS only through <see cref="Fts5Query"/>, column-filtered to the player-visible columns
/// (name, aliases, summary, body, tags): <c>secret</c> and <c>hidden_aliases</c> are never searched, so a word that only
/// appears there matches nothing. (2) Every candidate then goes through <see cref="ReadScope"/>: kept only when visible
/// and NOT disguised, because a disguised entity's own name, summary and slug are in the player columns too, and finding
/// "the advisor in Serret" by typing "protector" would confirm what the disguise hides. (3) The known_as branch finds
/// entities by the names the perspective knows them by (in C#; known_as is not indexed): its known_as and, under a
/// disguise, the party aliases it shares with the view, which is the only way a disguised entity is ever found. (4) Facts
/// match on their statement only when the perspective's deciding row has no known_as; otherwise only on the phrasing it
/// knows. (5) Snippets are built in C# from the fields the perspective may see (never FTS5 <c>snippet()</c>/
/// <c>highlight()</c>). (6) The partial-match retry and the cursor count only visible results, so neither the "partial"
/// flag nor a page size can reveal that something hidden matched. (7) A text hit is kept for a non-author view only when
/// the text that view is shown matches too: the indexed aliases column holds party aliases the public and non-members do
/// not see, and as of a session FTS still matches today's text (contract §3.5), so an entity visible then and made
/// author-only since, with "Secretly a spy" added to its summary, must not be found by "spy" in a view of the time before
/// (<see cref="MatchesTheViewsText"/>).
/// </para>
/// <para>
/// <b>Ranking:</b> exact name, alias or known_as matches first, then known_as-branch matches, then bm25 with the weights
/// name 10, aliases 8, summary 4, body 1, secret 1, tags 2, hidden_aliases 8 (seven weights: with six, hidden_aliases
/// silently gets 1); facts after entities. When nothing visible matches every word of a multi-word query, the search
/// is retried with any word and <see cref="SearchResult.PartialMatch"/> says so.
/// </para>
/// </summary>
public sealed class CampaignSearch
{
    /// <summary>The entity_fts columns a non-author perspective may search (not secret, not hidden_aliases).</summary>
    public static readonly IReadOnlyList<string> PlayerColumns = ["name", "aliases", "summary", "body", "tags"];

    /// <summary>The bm25 call for entity_fts: one weight per column, in column order (contract §0).</summary>
    public const string EntityRank = "bm25(entity_fts, 10, 8, 4, 1, 1, 2, 8)";

    /// <summary>Appended to the status of an author-view beat hit that can happen next (<see cref="EntityHit.Status"/>).</summary>
    public const string ReachableMark = " · reachable now";

    // Candidates read from FTS before the perspective filter; a campaign's matches for one query are far fewer.
    private const int MaxCandidates = 2_000;

    // The player columns but the aliases, which hold party aliases some views do not see (MatchesTheViewsText).
    private static readonly IReadOnlyList<string> PlayerColumnsWithoutAliases = PlayerColumns.Where(c => c != "aliases").ToList();

    private static readonly DslValueSet AnyStatus = new("status",
        CampaignValues.Statuses.ByKind.Values.SelectMany(s => s.Values).Distinct(StringComparer.Ordinal).ToList());

    private readonly CampaignDatabase _database;

    public CampaignSearch(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Searches (or lists) one campaign from one perspective.</summary>
    /// <exception cref="DndInputException">
    /// A query over the caps or with no words, an unknown kind or status, a bad limit or cursor, an unknown character
    /// perspective, or an as_of_session out of range.
    /// </exception>
    public SearchResult Search(CampaignRow campaign, SearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(request);
        using var connection = ReadConnection.Open(_database);
        return Search(connection, campaign, request);
    }

    /// <summary>The same over an open connection (composition and tests).</summary>
    internal static SearchResult Search(SqliteConnection connection, CampaignRow campaign, SearchRequest request)
    {
        var limit = ReadCursor.Limit(request.Limit);
        var query = string.IsNullOrWhiteSpace(request.Query) ? null : request.Query.Trim();
        CheckQuery(query);
        var problems = new List<string>();
        var kinds = Canonical(request.Kinds, "kinds", CampaignValues.Kinds.Set, problems);
        var statuses = Canonical(request.Statuses, "statuses", AnyStatus, problems);
        DslProblems.ThrowIfAny(problems, "search filters");
        var tags = TagKeys(request.Tags);
        var perspective = request.Perspective ?? Perspective.Author;
        var fingerprint = ReadCursor.Fingerprint("search", campaign.Id, query, kinds, statuses, tags, perspective.Text,
            request.IncludeFacts, request.AsOfSession);
        var offset = ReadCursor.Decode(request.Cursor, fingerprint);
        var scope = ReadScope.Open(connection, campaign, perspective, request.AsOfSession);
        var filters = new Filters(kinds, statuses, tags);
        var includeFacts = request.IncludeFacts && query is not null && filters.IsEmpty;

        List<object> items;
        var partial = false;
        if (query is null)
        {
            items = Listing(scope, filters).Cast<object>().ToList();
        }
        else
        {
            items = Match(scope, query, filters, includeFacts, all: true);
            if (items.Count == 0 && Fts5Query.WordCount(query) >= 2)
            {
                items = Match(scope, query, filters, includeFacts, all: false);
                partial = true;
            }
        }

        var page = items.Skip(offset).Take(limit).ToList();
        return new SearchResult(
            page.OfType<EntityHit>().ToList(),
            page.OfType<FactHit>().ToList(),
            ReadCursor.Next(offset, limit, items.Count, fingerprint),
            partial,
            items.Count);
    }

    private static void CheckQuery(string? query)
    {
        if (query is null)
        {
            return;
        }

        if (query.Length > CampaignLimits.MaxQueryLength)
        {
            throw new DndInputException(
                $"query is {query.Length.ToString("N0", CultureInfo.InvariantCulture)} characters long; search with at most " +
                $"{CampaignLimits.MaxQueryLength} characters of key words, e.g. \"old king\" or \"seal*\". To read one entity, use " +
                "campaign_get with its handle.");
        }

        var words = Fts5Query.WordCount(query);
        if (words > CampaignLimits.MaxQueryWords)
        {
            throw new DndInputException(
                $"query has {words.ToString(CultureInfo.InvariantCulture)} different words; search with at most " +
                $"{CampaignLimits.MaxQueryWords} key words, e.g. \"old king\" or \"seal*\".");
        }

        if (Fts5Query.Terms(query) is null)
        {
            throw new DndInputException(
                "query needs at least one word to search for (letters or digits), e.g. \"old king\" or a prefix like \"seal*\". " +
                "Leave query out to list entities by kinds, statuses or tags.");
        }
    }

    // The union of every result, entities (best tier first) then facts; offsets page through this list.
    private static List<object> Match(ReadScope scope, string query, Filters filters, bool includeFacts, bool all)
    {
        var tokens = ReadText.Tokens(query);
        var items = new List<object>();
        items.AddRange(MatchEntities(scope, query, tokens, filters, all));
        if (includeFacts)
        {
            items.AddRange(MatchFacts(scope, query, tokens, all));
        }

        return items;
    }

    private static IEnumerable<EntityHit> MatchEntities(ReadScope scope, string query, IReadOnlyList<QueryToken> tokens, Filters filters, bool all)
    {
        var match = scope.IsAuthorView
            ? all ? Fts5Query.Terms(query) : Fts5Query.AnyTerms(query)
            : all ? Fts5Query.ColumnFiltered(query, PlayerColumns.ToList()) : Fts5Query.ColumnFilteredAnyTerms(query, PlayerColumns.ToList());
        var textHits = scope.Connection.Query<string>(
            $"SELECT e.id FROM entity_fts JOIN entity e ON e.seq = entity_fts.rowid " +
            $"WHERE entity_fts MATCH @match AND e.campaign_id = @campaignId AND e.deleted_at IS NULL " +
            $"ORDER BY {EntityRank}, e.seq LIMIT {MaxCandidates}",
            new { match, campaignId = scope.Campaign.Id }).ToList();
        var knownAsCandidates = scope.Connection.Query<string>(
            "SELECT DISTINCT entity_id FROM knowledge WHERE campaign_id = @campaignId AND entity_id IS NOT NULL AND known_as IS NOT NULL",
            new { campaignId = scope.Campaign.Id }).ToList();
        scope.LoadEntities(textHits.Concat(knownAsCandidates));
        var shownHits = textHits.Select(scope.Entity).OfType<EntityState>().Where(s => s.Visible && (scope.IsAuthorView || s.Shown)).ToList();
        var viewMatches = MatchesTheViewsText(scope, shownHits, query, tokens, all);

        var queryKey = CampaignText.KeyWithoutArticle(query.Replace("*", " ", StringComparison.Ordinal));
        var ranked = new List<(int Tier, int Order, EntityState State)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < textHits.Count; i++)
        {
            // A disguised entity is never found through its own text (name, summary, slug are all the true ones).
            if (scope.Entity(textHits[i]) is { Visible: true } state && (scope.IsAuthorView || state.Shown) &&
                viewMatches.Contains(state.Row.Id) && seen.Add(state.Row.Id))
            {
                ranked.Add((IsExact(scope, state, queryKey) ? 0 : 2, i, state));
            }
        }

        foreach (var id in knownAsCandidates)
        {
            if (seen.Contains(id) || scope.Entity(id) is not { Visible: true } state || !KnownAsMatches(scope, state, tokens, all))
            {
                continue;
            }

            seen.Add(id);
            ranked.Add((IsExact(scope, state, queryKey) ? 0 : 1, 0, state));
        }

        var kept = ranked.Where(r => filters.Keep(scope, r.State)).ToList();
        scope.LoadTags(kept.Select(r => r.State.Row.Id));
        var beats = new Lazy<IReadOnlyDictionary<string, BeatProgress>>(() => ReadBeats.Progress(scope));
        return kept
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.Tier == 2 ? r.Order : 0)
            .ThenBy(r => r.State.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.State.Row.Seq)
            .Select(r =>
            {
                var tier = r.Tier switch { 0 => SearchTiers.Exact, 1 => SearchTiers.KnownAs, _ => SearchTiers.Text };
                var (snippet, from) = r.State.Shown || scope.IsAuthorView ? Snippet(scope, r.State, tokens) : (null, null);
                return new EntityHit(r.State.Ref!, r.State.Row.Kind, r.State.Name, HitStatus(scope, r.State, beats), snippet, from, tier);
            })
            .ToList();
    }

    /// <summary>
    /// What a hit prints after its kind: the status the view is shown, and for one of the author's beats that can happen
    /// next, " · reachable now" (review C03: session_prep reads a listing of beats to see which are reachable now, and the
    /// listing said only "pending"). The status filter compares the status alone (<see cref="ReadScope.DisplayStatus"/>).
    /// </summary>
    private static string? HitStatus(ReadScope scope, EntityState state, Lazy<IReadOnlyDictionary<string, BeatProgress>> beats)
    {
        var status = scope.DisplayStatus(state);
        return scope.IsAuthorView && state.Row.Kind == CampaignValues.Kinds.Beat &&
               beats.Value.TryGetValue(state.Row.Id, out var progress) && progress.Reachable
            ? (status ?? CampaignValues.Statuses.BeatPending) + ReachableMark
            : status;
    }

    private static IEnumerable<FactHit> MatchFacts(ReadScope scope, string query, IReadOnlyList<QueryToken> tokens, bool all)
    {
        var match = all ? Fts5Query.Terms(query) : Fts5Query.AnyTerms(query);
        var textHits = scope.Connection.Query<string>(
            $"SELECT f.id FROM fact_fts JOIN fact f ON f.seq = fact_fts.rowid " +
            $"WHERE fact_fts MATCH @match AND f.campaign_id = @campaignId AND f.deleted_at IS NULL " +
            $"ORDER BY bm25(fact_fts), f.seq LIMIT {MaxCandidates}",
            new { match, campaignId = scope.Campaign.Id }).ToList();
        var knownAsCandidates = scope.Connection.Query<string>(
            "SELECT DISTINCT fact_id FROM knowledge WHERE campaign_id = @campaignId AND fact_id IS NOT NULL AND known_as IS NOT NULL",
            new { campaignId = scope.Campaign.Id }).ToList();
        scope.LoadFacts(textHits.Concat(knownAsCandidates));
        var todaysStatements = scope.IsAuthorView || scope.AsOf is null
            ? null
            : CurrentValues(scope, "SELECT id, statement FROM fact WHERE id IN @ids", textHits);

        var hits = new List<(int Tier, int Order, FactState State)>();
        foreach (var id in knownAsCandidates)
        {
            if (scope.Fact(id) is not { Visible: true } state)
            {
                continue;
            }

            var phrasing = scope.IsAuthorView
                ? state.Entries.Any(e => ReadText.Matches(e.KnownAs, tokens, all))
                : !string.IsNullOrWhiteSpace(state.Verdict.KnownAs) && ReadText.Matches(state.Verdict.KnownAs, tokens, all);
            if (phrasing)
            {
                hits.Add((0, 0, state));
            }
        }

        for (var i = 0; i < textHits.Count; i++)
        {
            // Outside the author view a fact the perspective knows by its own phrasing matches only through that text; as of
            // a session, only when the statement it is shown then (not today's, which FTS matched) holds the words.
            if (scope.Fact(textHits[i]) is { Visible: true } state &&
                (scope.IsAuthorView || string.IsNullOrWhiteSpace(state.Verdict.KnownAs)) &&
                (todaysStatements is null || todaysStatements.GetValueOrDefault(state.Row.Id) == state.Row.Statement ||
                 ReadText.Matches(state.Row.Statement, tokens, all)) &&
                hits.All(h => h.State.Row.Id != state.Row.Id))
            {
                hits.Add((1, i, state));
            }
        }

        return hits.OrderBy(h => h.Tier).ThenBy(h => h.Order).ThenBy(h => h.State.Row.Seq).Select(h => FactHitOf(scope, h.State)).ToList();
    }

    private static List<EntityHit> Listing(ReadScope scope, Filters filters)
    {
        var ids = filters.Kinds.Count == 0
            ? scope.LoadAllEntities()
            : scope.Connection.Query<string>(
                "SELECT id FROM entity WHERE campaign_id = @campaignId AND kind IN @kinds" +
                (scope.AsOf is null ? " AND deleted_at IS NULL" : string.Empty),
                new { campaignId = scope.Campaign.Id, kinds = filters.Kinds }).ToList();
        scope.LoadEntities(ids);
        scope.LoadTags(ids);
        var beats = new Lazy<IReadOnlyDictionary<string, BeatProgress>>(() => ReadBeats.Progress(scope));
        return ids.Select(scope.Entity)
            .Where(s => s is { Visible: true } && filters.Keep(scope, s))
            .Select(s => s!)
            .OrderBy(s => s.Row.Kind, StringComparer.Ordinal)
            .ThenBy(s => s.Row.SortKey is null ? 1 : 0)
            .ThenBy(s => s.Row.SortKey ?? 0)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Row.Seq)
            .Select(s => new EntityHit(s.Ref!, s.Row.Kind, s.Name, HitStatus(scope, s, beats), null, null, SearchTiers.List))
            .ToList();
    }

    internal static FactHit FactHitOf(ReadScope scope, FactState state) =>
        scope.IsAuthorView
            ? new FactHit(state.Ref, state.Row.Code, state.Row.Statement, state.Row.Truth, state.Row.CanonStatus,
                KnownByLabels(state.Entries, scope.AuthorRef, scope.AsOf))
            : new FactHit(state.Ref, state.Row.Code, state.Text, null, null, null);

    /// <summary>
    /// Author view: every knower holding it in an aware state at <paramref name="asOfSession"/> (now when null), by author
    /// label (a character by its ref, so two characters never collapse into one "character"); <paramref name="characterRef"/>
    /// maps a character id to its ref. Judged by the rows' learned and valid-until sessions (review C07): an as_of read
    /// labelled each fact with today's knowers, so "known by party" sat beside the same page's Knowledge section, which
    /// showed the party learning it a session later.
    /// </summary>
    internal static IReadOnlyList<string> KnownByLabels(IReadOnlyList<KnowledgeEntry> entries, Func<string, string> characterRef, int? asOfSession) =>
        entries.Where(e => CampaignValues.KnowledgeStates.AwareStates.Contains(e.State) && KnowledgeVerdicts.Applies(e, asOfSession))
            .Select(e => e.KnowerKind == CampaignValues.KnowerKinds.Character && e.KnowerId is not null
                ? characterRef(e.KnowerId)
                : e.KnowerKind)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // The exact tier: the query is the name this perspective uses for it (display name, a shown alias, its known_as).
    private static bool IsExact(ReadScope scope, EntityState state, string queryKey)
    {
        if (queryKey.Length == 0)
        {
            return false;
        }

        var names = new List<string?> { state.Name, state.Verdict.KnownAs };
        names.AddRange(scope.ShownAliases(state).Select(a => a.Alias));
        return names.Any(n => n is not null && CampaignText.KeyWithoutArticle(n) == queryKey);
    }

    // The known_as branch: the perspective's own names for the entity (the author: any knower's). For another view that is
    // its known_as and, of a disguised entity, the party aliases it shares with the view (EntityView.UsedNames, review U01):
    // a party that knows Keras as "the ancient sorcerer king" and calls him by his party alias "the old king" finds him by
    // it, under the known_as and e:<n>; his true name and his own text are still never searched (class summary).
    private static bool KnownAsMatches(ReadScope scope, EntityState state, IReadOnlyList<QueryToken> tokens, bool all)
    {
        if (scope.IsAuthorView)
        {
            return state.Entries.Any(e => ReadText.Matches(e.KnownAs, tokens, all));
        }

        if (!state.Verdict.Knows)
        {
            return false;
        }

        var names = new List<string?> { state.Verdict.KnownAs };
        if (!state.Shown)
        {
            names.AddRange(scope.ShownAliases(state).Select(a => a.Alias));
        }

        return names.Any(n => !string.IsNullOrWhiteSpace(n) && ReadText.Matches(n, tokens, all));
    }

    // The first field the perspective may see that holds a query word, in the order a reader would look.
    private static (string? Snippet, string? From) Snippet(ReadScope scope, EntityState state, IReadOnlyList<QueryToken> tokens)
    {
        var aliases = scope.ShownAliases(state);
        var fields = new List<(string From, string? Text)>
        {
            (SnippetFields.Summary, state.Row.Summary),
            (SnippetFields.Body, state.Row.BodyMd),
            (SnippetFields.Aliases, string.Join(", ", aliases.Where(a => a.Visibility is CampaignValues.Visibilities.Public or CampaignValues.Visibilities.Party).Select(a => a.Alias))),
            (SnippetFields.Tags, string.Join(", ", scope.Tags(state.Row.Id))),
        };
        if (scope.IsAuthorView)
        {
            fields.Add((SnippetFields.Secret, state.Row.SecretMd));
            fields.Add((SnippetFields.HiddenAliases, string.Join(", ", aliases
                .Where(a => a.Visibility is not (CampaignValues.Visibilities.Public or CampaignValues.Visibilities.Party))
                .Select(a => a.Alias))));
        }

        foreach (var (from, text) in fields)
        {
            if (ReadText.Snippet(text, tokens) is { } snippet)
            {
                return (snippet, from);
            }
        }

        return string.IsNullOrWhiteSpace(state.Row.Summary) ? (null, null) : (ReadText.Excerpt(state.Row.Summary, 180), SnippetFields.Summary);
    }

    /// <summary>
    /// The ids of the FTS text hits a non-author search may keep: a hit counts when the query is in the player-column text
    /// the view is shown (name, the aliases it may see, summary, body, tags; as of the session when the search has one). The
    /// indexed text differs from that text in two ways. A party alias is in the indexed aliases column for every view, but
    /// the public and a character outside the party do not see it (review L11): matched through it, "Orsino" found The
    /// Masked Duke for the public under the ref <c>character:duke-orsino</c>, and the hit itself said the word is about him.
    /// And FTS indexes today's text only (contract §3.5): without the check, words that exist only in today's author-only
    /// text would find an entity in a view of the past. The author view keeps every hit (it may see every column).
    ///
    /// <para>
    /// <b>Who decides, per hit.</b> Text the view is shown as FTS indexed it (only the aliases differ) is asked of FTS
    /// again, word by word, without the aliases column (<see cref="MatchesWithoutAliases"/>), and the aliases the view sees
    /// in C#: FTS's porter stemmer is the one that matched, and <see cref="ReadText.Matches"/> forgives fewer endings, so a
    /// public search for "run" or "connect" dropped The Masked Duke, whose summary says "running" and "connections", only
    /// because he has a party alias, while The Plain Baron with the same summary was found (a difference that itself
    /// hinted at the hidden alias). Text that differs (an as_of read of text edited since) is matched in C#.
    /// </para>
    /// </summary>
    private static HashSet<string> MatchesTheViewsText(ReadScope scope, IReadOnlyList<EntityState> hits, string query, IReadOnlyList<QueryToken> tokens, bool all)
    {
        var keep = hits.Select(h => h.Row.Id).ToHashSet(StringComparer.Ordinal);
        if (scope.IsAuthorView || hits.Count == 0)
        {
            return keep;
        }

        var ids = hits.Select(h => h.Row.Id).ToList();
        var today = new Dictionary<string, (string Name, string Summary, string Body)>(StringComparer.Ordinal);
        foreach (var chunk in ids.Chunk(400))
        {
            foreach (var row in scope.Connection.Query<(string Id, string Name, string Summary, string BodyMd)>(
                         "SELECT id, name, summary, body_md FROM entity WHERE id IN @ids", new { ids = chunk }))
            {
                today[row.Id] = (row.Name, row.Summary, row.BodyMd);
            }
        }

        var todaysAliases = Grouped(scope,
            "SELECT entity_id, alias FROM entity_alias WHERE entity_id IN @ids AND visibility IN @shown", ids,
            [CampaignValues.Visibilities.Public, CampaignValues.Visibilities.Party]);
        var todaysTags = Grouped(scope,
            "SELECT et.entity_id, t.name FROM entity_tag et JOIN tag t ON t.id = et.tag_id WHERE et.entity_id IN @ids", ids);
        scope.LoadTags(ids);
        var aliasesDiffer = new List<(EntityState Hit, IReadOnlyList<string> Aliases)>();
        foreach (var hit in hits)
        {
            var id = hit.Row.Id;
            var aliases = scope.ShownAliases(hit).Select(a => a.Alias).ToList();
            var tags = scope.Tags(id);
            var sameText = today.TryGetValue(id, out var now) &&
                           now == (hit.Row.Name, hit.Row.Summary, hit.Row.BodyMd) &&
                           SameSet(todaysTags.GetValueOrDefault(id), tags);
            if (sameText && SameSet(todaysAliases.GetValueOrDefault(id), aliases))
            {
                continue;
            }

            if (sameText)
            {
                aliasesDiffer.Add((hit, aliases));
                continue;
            }

            var shown = new List<string> { hit.Row.Name, hit.Row.Summary, hit.Row.BodyMd };
            shown.AddRange(aliases);
            shown.AddRange(tags);
            if (!ReadText.Matches(string.Join("\n", shown), tokens, all))
            {
                keep.Remove(id);
            }
        }

        if (aliasesDiffer.Count > 0)
        {
            var byWord = MatchesWithoutAliases(scope, aliasesDiffer.Select(a => a.Hit.Row.Seq).ToList(), query);
            foreach (var (hit, aliases) in aliasesDiffer)
            {
                var aliasText = string.Join("\n", aliases);
                var matched = byWord.Select(w => w.Rowids.Contains(hit.Row.Seq) || ReadText.Matches(aliasText, ReadText.Tokens(w.Word), all: true));
                if (!(all ? matched.All(m => m) : matched.Any(m => m)))
                {
                    keep.Remove(hit.Row.Id);
                }
            }
        }

        return keep;

        static bool SameSet(IReadOnlyList<string>? a, IReadOnlyList<string> b) =>
            (a ?? []).Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>
    /// For each whitespace-separated word of the query (as <see cref="Fts5Query"/> splits it, so each is one FTS phrase),
    /// the entity_fts rowids among <paramref name="rowids"/> whose name, summary, body or tags hold it, as FTS itself
    /// matches it (porter stemming, prefixes).
    /// </summary>
    private static List<(string Word, HashSet<long> Rowids)> MatchesWithoutAliases(ReadScope scope, IReadOnlyList<long> rowids, string query)
    {
        var result = new List<(string, HashSet<long>)>();
        foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var found = new HashSet<long>();
            if (Fts5Query.ColumnFiltered(word, PlayerColumnsWithoutAliases) is not { } match)
            {
                continue;
            }

            foreach (var chunk in rowids.Chunk(400))
            {
                found.UnionWith(scope.Connection.Query<long>(
                    "SELECT rowid FROM entity_fts WHERE entity_fts MATCH @match AND rowid IN @rowids", new { match, rowids = chunk }));
            }

            result.Add((word, found));
        }

        return result;
    }

    // id → values of a two-column (id, value) query over ids (and an optional @shown list).
    private static Dictionary<string, IReadOnlyList<string>> Grouped(ReadScope scope, string sql, IReadOnlyList<string> ids, string[]? shown = null)
    {
        var rows = new List<(string Id, string Value)>();
        foreach (var chunk in ids.Chunk(400))
        {
            rows.AddRange(scope.Connection.Query<(string Id, string Value)>(sql, new { ids = chunk, shown = shown ?? [] }));
        }

        return rows.GroupBy(r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(r => r.Value).ToList(), StringComparer.Ordinal);
    }

    // id → the one value of a two-column (id, value) query over ids.
    private static Dictionary<string, string> CurrentValues(ReadScope scope, string sql, IReadOnlyList<string> ids) =>
        Grouped(scope, sql, ids).ToDictionary(p => p.Key, p => p.Value[0], StringComparer.Ordinal);

    private static IReadOnlyList<string> Canonical(IReadOnlyList<string>? values, string field, DslValueSet set, List<string> problems)
    {
        if (values is null)
        {
            return [];
        }

        var canonical = new List<string>();
        for (var i = 0; i < values.Count; i++)
        {
            if (set.TryMatch(values[i], out var value))
            {
                canonical.Add(value);
            }
            else
            {
                var shown = values[i] is { Length: > 40 } long_ ? long_[..40] + "…" : values[i];
                problems.Add($"{field} item {(i + 1).ToString(CultureInfo.InvariantCulture)} (\"{shown}\") is not a {set.What}; " +
                             (set.Values.Count <= 25 ? $"use one of {set.List}." : "e.g. open, active, resolved, alive, dead, revealed."));
            }
        }

        return canonical.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IReadOnlyList<string> TagKeys(IReadOnlyList<string>? tags) =>
        tags is null
            ? []
            : tags.Select(CampaignText.Key).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private sealed record Filters(IReadOnlyList<string> Kinds, IReadOnlyList<string> Statuses, IReadOnlyList<string> Tags)
    {
        public bool IsEmpty => Kinds.Count == 0 && Statuses.Count == 0 && Tags.Count == 0;

        // Status and tags are part of the full detail: a disguised entity shows neither, so neither filter can match it.
        public bool Keep(ReadScope scope, EntityState state)
        {
            if (Kinds.Count > 0 && !Kinds.Contains(state.Row.Kind))
            {
                return false;
            }

            if (Statuses.Count > 0 && (scope.DisplayStatus(state) is not { } status || !Statuses.Contains(status)))
            {
                return false;
            }

            return Tags.Count == 0 ||
                   ((scope.IsAuthorView || state.Shown) && scope.Tags(state.Row.Id).Any(t => Tags.Contains(CampaignText.Key(t))));
        }
    }
}
