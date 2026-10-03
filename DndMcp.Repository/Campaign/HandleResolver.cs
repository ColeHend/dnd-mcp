using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>What <see cref="HandleResolver.Suggest(string?, string, Func{EntityRow, SuggestionView?}, int)"/> may match and print for one entity.</summary>
/// <param name="Name">The name the caller's perspective knows it by (matched against the typed text).</param>
/// <param name="Handle">
/// The handle to print for it (<c>kind:slug</c>, or <c>e:12</c> when the slug would spell a hidden name). A session entity's
/// <c>kind:slug</c> (<c>session:session-3</c>) is printed as <c>session:3</c>, the form every reader prints and tools take.
/// </param>
public sealed record SuggestionView(string Name, string Handle);

/// <summary>
/// Turns handles (<see cref="CampaignHandle"/>), names and campaign slugs into rows of one campaign, for the write and read
/// paths alike. Perspective-agnostic: it finds what exists; callers decide what a perspective may see, and pass that
/// decision into <see cref="Suggest(string?, string, Func{EntityRow, SuggestionView?}, int)"/> so an error never names
/// what the reader cannot see.
///
/// <para>
/// Resolution rules (contract §3.1): <c>kind:slug</c> must match the kind as well (a mismatch is "not found", never the
/// other entity); a code (<c>Q22</c>) is an entity or fact code, and an entity code that matches nothing falls back to
/// the lower-cased text as a slug (a bare word that merely looks like a code: "b12"); <c>session:n</c>/<c>live</c>/
/// <c>last</c> give the session's entity; <c>campaign-slug/…</c> resolves in the named campaign. Soft-deleted rows are
/// found only when asked for (restore needs them). Names compare by <see cref="CampaignText.Key"/>, then by
/// <see cref="CampaignText.KeyWithoutArticle"/>, so "the Old King" and "old king" are one name.
/// </para>
/// </summary>
public sealed class HandleResolver
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;

    /// <param name="connection">An open connection.</param>
    /// <param name="campaignId">The campaign handles resolve in.</param>
    /// <param name="transaction">The open transaction, when there is one (every command must carry it).</param>
    public HandleResolver(SqliteConnection connection, string campaignId, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        CampaignDatabase.EnsureDapperConfigured();
        _connection = connection;
        _transaction = transaction;
        CampaignId = campaignId;
    }

    public string CampaignId { get; }

    /// <summary>Every campaign, by slug.</summary>
    public static IReadOnlyList<CampaignRow> Campaigns(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        CampaignDatabase.EnsureDapperConfigured();
        return connection.Query<CampaignRow>($"SELECT {CampaignRow.Columns} FROM campaign ORDER BY slug", transaction: transaction).ToList();
    }

    /// <summary>
    /// A campaign by slug (case-insensitive), else by name (<see cref="CampaignText.Key"/>); null when none. Two campaigns
    /// with the same name and neither slug given is refused, naming both slugs.
    /// </summary>
    /// <exception cref="DndInputException">The name matches several campaigns.</exception>
    public static CampaignRow? TryCampaign(SqliteConnection connection, string slugOrName, SqliteTransaction? transaction = null)
    {
        var text = (slugOrName ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var all = Campaigns(connection, transaction);
        var bySlug = all.FirstOrDefault(c => string.Equals(c.Slug, text.ToLowerInvariant(), StringComparison.Ordinal));
        if (bySlug is not null)
        {
            return bySlug;
        }

        var key = CampaignText.Key(text);
        var byName = all.Where(c => CampaignText.Key(c.Name) == key).ToList();
        return byName.Count switch
        {
            0 => null,
            1 => byName[0],
            _ => throw new DndInputException(
                $"Several campaigns are named \"{Echo(text)}\": {string.Join(", ", byName.Select(c => c.Slug))}. Give the slug."),
        };
    }

    /// <summary>The entity a handle names in this campaign, or null.</summary>
    public EntityRow? TryEntity(CampaignHandle handle, bool includeDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        switch (handle)
        {
            case CampaignHandle.EntityBySlug bySlug:
                var entity = EntityWhere("slug = @slug", new { slug = bySlug.Slug }, includeDeleted);
                return entity is not null && (bySlug.Kind is null || entity.Kind == bySlug.Kind) ? entity : null;
            case CampaignHandle.EntityBySeq bySeq:
                return EntityWhere("seq = @seq", new { seq = bySeq.Seq }, includeDeleted);
            case CampaignHandle.ByCode byCode:
                return EntityWhere("code = @code", new { code = byCode.Code }, includeDeleted) ??
                       EntityWhere("slug = @slug", new { slug = byCode.Code.ToLowerInvariant() }, includeDeleted);
            case CampaignHandle.SessionByNumber or CampaignHandle.SessionLive or CampaignHandle.SessionLast:
                return TrySession(handle) is { } session ? EntityWhere("id = @id", new { id = session.EntityId }, includeDeleted) : null;
            case CampaignHandle.CrossCampaign cross:
                return TryCampaign(_connection, cross.CampaignSlug, _transaction) is { } other
                    ? new HandleResolver(_connection, other.Id, _transaction).TryEntity(cross.Inner, includeDeleted)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>The fact a handle names in this campaign (<c>f:n</c> or a fact code), or null.</summary>
    public FactRow? TryFact(CampaignHandle handle, bool includeDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle switch
        {
            CampaignHandle.FactBySeq bySeq => FactWhere("seq = @seq", new { seq = bySeq.Seq }, includeDeleted),
            CampaignHandle.ByCode byCode => FactWhere("code = @code", new { code = byCode.Code }, includeDeleted),
            _ => null,
        };
    }

    /// <summary>
    /// An entity or a fact: <c>f:n</c> is a fact, a code may be either (codes share one sequence per letter across both
    /// tables), anything else an entity. A code on both is refused rather than guessed.
    /// </summary>
    /// <exception cref="DndInputException">The code names both an entity and a fact (the message gives <c>e:</c>/<c>f:</c> handles only).</exception>
    public (EntityRow? Entity, FactRow? Fact) TryEntityOrFact(CampaignHandle handle, bool includeDeleted = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle is CampaignHandle.FactBySeq)
        {
            return (null, TryFact(handle, includeDeleted));
        }

        if (handle is CampaignHandle.ByCode byCode)
        {
            var entity = EntityWhere("code = @code", new { code = byCode.Code }, includeDeleted);
            var fact = FactWhere("code = @code", new { code = byCode.Code }, includeDeleted);
            if (entity is not null && fact is not null)
            {
                throw new DndInputException(
                    $"{byCode.Code} names both {entity.SeqHandle} and {fact.SeqHandle}; use one of those handles.");
            }

            if (entity is not null || fact is not null)
            {
                return (entity, fact);
            }
        }

        return (TryEntity(handle, includeDeleted), null);
    }

    /// <summary>
    /// The non-deleted entity of <paramref name="kind"/> with this name: by <see cref="CampaignText.Key"/>, else by
    /// <see cref="CampaignText.KeyWithoutArticle"/> on both sides; null when none. It matches TRUE names and ignores
    /// visibility, so it is for the author's paths (the write path's name lookups); a non-author read resolves a name
    /// against what its perspective knows (known_as, visible aliases) instead, or a hit or refusal here would confirm
    /// that something hidden has that name.
    /// </summary>
    /// <exception cref="DndInputException">
    /// Two entities share the name at the first tier that matches. The message lists them as <c>e:&lt;n&gt;</c> handles
    /// only: a <c>kind:slug</c> ref spells a name, and one of the two may be hidden or known to the reader by another name.
    /// </exception>
    public EntityRow? EntityByName(string kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var key = CampaignText.Key(name);
        if (key.Length == 0)
        {
            return null;
        }

        var candidates = _connection.Query<(long Seq, string Name)>(
            "SELECT seq, name FROM entity WHERE campaign_id = @campaignId AND kind = @kind AND deleted_at IS NULL",
            new { campaignId = CampaignId, kind }, _transaction).ToList();
        var exact = candidates.Where(c => CampaignText.Key(c.Name) == key).ToList();
        if (exact.Count == 0)
        {
            var bare = CampaignText.KeyWithoutArticle(name);
            exact = candidates.Where(c => CampaignText.KeyWithoutArticle(c.Name) == bare).ToList();
        }

        switch (exact.Count)
        {
            case 0:
                return null;
            case 1:
                return EntityWhere("seq = @seq", new { seq = exact[0].Seq }, includeDeleted: false);
            default:
                var rows = exact.Select(c => EntityWhere("seq = @seq", new { seq = c.Seq }, includeDeleted: false)!).ToList();
                throw new DndInputException(
                    $"{rows.Count} {kind} entities are named \"{Echo(name)}\": " +
                    $"{string.Join(", ", rows.Select(r => r.SeqHandle))}. Use one of those handles instead of the name.");
        }
    }

    /// <summary>
    /// A session: <c>session:n</c>, <c>session:live</c> (the one live session), <c>session:last</c> (the highest-numbered
    /// played session), or any entity handle naming a session entity. Null when none.
    /// </summary>
    public SessionRow? TrySession(CampaignHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle switch
        {
            CampaignHandle.SessionByNumber n => SessionByNumber(n.Number),
            CampaignHandle.SessionLive => LiveSession(),
            CampaignHandle.SessionLast => SessionWhere(
                "status = @played ORDER BY number DESC LIMIT 1", new { played = CampaignValues.SessionStatuses.Played }),
            _ => TryEntity(handle) is { Kind: CampaignValues.Kinds.Session } entity
                ? SessionWhere("entity_id = @id", new { id = entity.Id })
                : null,
        };
    }

    /// <summary>The session numbered <paramref name="number"/>, or null.</summary>
    public SessionRow? SessionByNumber(int number) => SessionWhere("number = @number", new { number });

    /// <summary>The live session, or null.</summary>
    public SessionRow? LiveSession() => SessionWhere("status = @live", new { live = CampaignValues.SessionStatuses.Live });

    /// <summary>
    /// Up to <paramref name="max"/> handles close to <paramref name="text"/>, for "not found" messages, among entities
    /// <paramref name="visible"/> admits; printed as <c>kind:slug</c> (a session as <c>session:&lt;n&gt;</c>) and matched on
    /// the entity's own name and slug.
    /// For a non-author reader use the <see cref="SuggestionView"/> overload: matching an entity by its true name when the
    /// reader knows it by another would itself reveal that the typed name is close to something hidden.
    /// </summary>
    public IReadOnlyList<string> Suggest(string? kind, string text, Func<EntityRow, bool> visible, int max = 5)
    {
        ArgumentNullException.ThrowIfNull(visible);
        return Suggest(kind, text, e => visible(e) ? new SuggestionView(e.Name, e.Handle) : null, max, matchSlug: true);
    }

    /// <summary>
    /// Up to <paramref name="max"/> handles close to <paramref name="text"/>: <paramref name="view"/> returns, per entity,
    /// the name the reader knows it by and the handle to print, or null to leave it out. Only that name is matched.
    /// </summary>
    public IReadOnlyList<string> Suggest(string? kind, string text, Func<EntityRow, SuggestionView?> view, int max = 5) =>
        Suggest(kind, text, view, max, matchSlug: false);

    private IReadOnlyList<string> Suggest(string? kind, string text, Func<EntityRow, SuggestionView?> view, int max, bool matchSlug)
    {
        ArgumentNullException.ThrowIfNull(view);
        var key = CampaignText.KeyWithoutArticle(text);
        if (key.Length == 0 || max <= 0)
        {
            return [];
        }

        var sql = $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND deleted_at IS NULL" +
                  (kind is null ? string.Empty : " AND kind = @kind");
        var scored = new List<(int Score, string Name, string Handle)>();
        Dictionary<string, long>? sessionNumbers = null;
        foreach (var entity in _connection.Query<EntityRow>(sql, new { campaignId = CampaignId, kind }, _transaction))
        {
            if (view(entity) is not { } shown)
            {
                continue;
            }

            var score = Score(key, CampaignText.KeyWithoutArticle(shown.Name));
            if (matchSlug)
            {
                score = Math.Min(score, Score(key, entity.Slug.Replace('-', ' ')));
            }

            if (score < int.MaxValue)
            {
                scored.Add((score, shown.Name, Printed(entity, shown.Handle)));
            }
        }

        return scored
            .OrderBy(s => s.Score).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Handle, StringComparer.Ordinal)
            .Select(s => s.Handle)
            .Distinct(StringComparer.Ordinal)
            .Take(max)
            .ToList();

        // A session's kind:slug ("session:session-3") resolves, but reads like a typo of the session:<n> every reader prints
        // and every tool takes, so whichever caller built it, it is printed as session:<n>. Any other handle (e:<n> for a
        // disguised session) is the caller's decision and stays as given.
        string Printed(EntityRow entity, string handle)
        {
            if (entity.Kind != CampaignValues.Kinds.Session || handle != entity.Handle)
            {
                return handle;
            }

            sessionNumbers ??= _connection.Query<(string EntityId, long Number)>(
                    "SELECT entity_id, number FROM session WHERE campaign_id = @campaignId", new { campaignId = CampaignId }, _transaction)
                .ToDictionary(s => s.EntityId, s => s.Number, StringComparer.Ordinal);
            return sessionNumbers.TryGetValue(entity.Id, out var number) ? "session:" + number.ToString(CultureInfo.InvariantCulture) : handle;
        }
    }

    // Lower is closer; int.MaxValue is "not close". Exact, prefix, word prefix, containment, then small edit distances
    // (the whole name, then one word of it).
    internal static int Score(string typed, string candidate)
    {
        if (candidate.Length == 0)
        {
            return int.MaxValue;
        }

        if (candidate == typed)
        {
            return 0;
        }

        if (candidate.StartsWith(typed, StringComparison.Ordinal) || typed.StartsWith(candidate, StringComparison.Ordinal))
        {
            return 1;
        }

        if (candidate.Split(' ').Any(w => w.StartsWith(typed, StringComparison.Ordinal)))
        {
            return 2;
        }

        if (candidate.Contains(typed, StringComparison.Ordinal) || (candidate.Length >= 4 && typed.Contains(candidate, StringComparison.Ordinal)))
        {
            return 3;
        }

        var limit = Math.Max(1, Math.Min(typed.Length, candidate.Length) / 4);
        var distance = EditDistance(typed, candidate, limit);
        if (distance <= limit)
        {
            return 4 + distance;
        }

        // A one-word typo of one word of a longer name ("belmakr" for "Belmakor Silverwind").
        if (!typed.Contains(' ', StringComparison.Ordinal))
        {
            var best = candidate.Split(' ')
                .Where(w => w.Length >= 4)
                .Select(w => (Word: w, Limit: Math.Max(1, Math.Min(typed.Length, w.Length) / 4)))
                .Select(w => (Distance: EditDistance(typed, w.Word, w.Limit), w.Limit))
                .Where(w => w.Distance <= w.Limit)
                .Select(w => w.Distance)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            if (best != int.MaxValue)
            {
                return 8 + best;
            }
        }

        return int.MaxValue;
    }

    // Levenshtein distance, stopping early once every cell of a row exceeds the limit.
    private static int EditDistance(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return limit + 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }

            if (best > limit)
            {
                return limit + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private EntityRow? EntityWhere(string predicate, object parameters, bool includeDeleted)
    {
        var args = new DynamicParameters(parameters);
        args.Add("campaignId", CampaignId);
        return _connection.QueryFirstOrDefault<EntityRow>(
            $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND {predicate}" +
            (includeDeleted ? string.Empty : " AND deleted_at IS NULL"),
            args, _transaction);
    }

    private FactRow? FactWhere(string predicate, object parameters, bool includeDeleted)
    {
        var args = new DynamicParameters(parameters);
        args.Add("campaignId", CampaignId);
        return _connection.QueryFirstOrDefault<FactRow>(
            $"SELECT {FactRow.Columns} FROM fact WHERE campaign_id = @campaignId AND {predicate}" +
            (includeDeleted ? string.Empty : " AND deleted_at IS NULL"),
            args, _transaction);
    }

    // predicate may end in ORDER BY … LIMIT.
    private SessionRow? SessionWhere(string predicate, object parameters)
    {
        var args = new DynamicParameters(parameters);
        args.Add("campaignId", CampaignId);
        return _connection.QueryFirstOrDefault<SessionRow>(
            $"SELECT {SessionRow.Columns} FROM session WHERE campaign_id = @campaignId AND {predicate}", args, _transaction);
    }

    private static string Echo(string text) => text.Length <= 60 ? text : text[..60] + "…";
}
