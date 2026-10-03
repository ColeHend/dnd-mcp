using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>Who knows what, as a grid (author-facing): one row per entity or fact, one column per perspective.</summary>
/// <param name="Columns">The perspectives, in column order (<c>party</c>, <c>character:belmakor</c>, …).</param>
/// <param name="Rows">Entities first (awareness), then facts; at most <see cref="KnowledgeLedger.MaxRows"/>.</param>
/// <param name="AsOfSession">The point in time, when one was asked for.</param>
/// <param name="TotalRows">
/// How many rows the ledger has in all; more than <see cref="Rows"/> holds when it was cut, so the host can say how many
/// were left out (contract §3.10) rather than present a cut grid as the whole.
/// </param>
public sealed record Ledger(IReadOnlyList<string> Columns, IReadOnlyList<LedgerRow> Rows, int? AsOfSession, int TotalRows);

/// <summary>One entity or fact across the perspectives.</summary>
/// <param name="Ref">Author ref (<c>character:protector</c>, <c>f:12</c>).</param>
/// <param name="Label">The entity's name or the fact's statement (cut to a line).</param>
/// <param name="IsEntity">An entity row (cells say "not met" rather than "no record").</param>
/// <param name="Cells">One per column, in column order.</param>
public sealed record LedgerRow(string Ref, string Label, bool IsEntity, IReadOnlyList<LedgerCell> Cells);

/// <summary>One perspective's standing on one row.</summary>
/// <param name="Perspective">The column.</param>
/// <param name="Standing"><see cref="Standings"/>.</param>
/// <param name="State">The deciding row's state.</param>
/// <param name="KnownAs">The name or phrasing this perspective uses.</param>
/// <param name="LearnedSession">When it was learned.</param>
/// <param name="Text">
/// The cell as printed: "met, unrecognized (S1)", "aware as “the thing he wants” (S3)", "unaware", "uncertain: …",
/// "not met" for an entity with no record, "no record" for a fact with none, "not in play (planned)" for a player-side
/// column on a row that is not in play (standing <see cref="Standings.NotInPlay"/>), "author only" for a player-side
/// column whose rows claim an author-only row (standing <see cref="Standings.AuthorOnly"/>; <see cref="State"/>,
/// <see cref="KnownAs"/> and <see cref="LearnedSession"/> still say what those rows claim, for the author).
/// </param>
public sealed record LedgerCell(string Perspective, string Standing, string? State, string? KnownAs, int? LearnedSession, string Text);

/// <summary>What one perspective knows (the <c>campaign://&lt;slug&gt;/knowledge/&lt;perspective&gt;</c> resource).</summary>
/// <param name="Perspective">The perspective.</param>
/// <param name="Entities">Entities it has a knowledge record for, by the name it knows them by.</param>
/// <param name="Facts">Facts it knows, in its own phrasing.</param>
/// <param name="NextCursor">The next page.</param>
public sealed record PerspectiveKnowledge(string Perspective, IReadOnlyList<KnownEntity> Entities, IReadOnlyList<KnownFact> Facts, string? NextCursor);

/// <summary>An entity a perspective knows of.</summary>
public sealed record KnownEntity(string Ref, string Kind, string Name, string? State, int? LearnedSession, IReadOnlyList<string>? KnownBy);

/// <summary>A fact a perspective knows.</summary>
public sealed record KnownFact(string Ref, string? Code, string Text, string? State, int? LearnedSession, IReadOnlyList<string>? KnownBy);

/// <summary>
/// campaign_knowledge's ledger (contract §7): who knows each fact about an entity, in a grid, so the author can see at a
/// glance that the party met the Protector unrecognised in S1 and has not yet met the Mistaken One.
///
/// <para>
/// <b>Author-facing</b>, like the check: rows are labelled with true names and statements, because the ledger is the
/// author's view of every perspective at once. Each cell is that perspective's verdict (<see cref="KnowledgeVerdicts"/>,
/// the same one every read uses), rendered with the difference that matters: an entity with no record is "not met",
/// a fact with none is "no record", and neither is ever worded "does not know" (which is a claim someone recorded).
/// A row that is not in play (a planned fact, a struck invention, an unplayed session) is "not in play (planned)" in
/// every player-side column, as <see cref="ReadScope"/> hides it from those views: a party-visible planned "the axe is
/// fully assembled" shown as "knows" would contradict the party's own reads. For the same reason an author-only row whose
/// knowledge rows say a player-side view knows it, or may know it ("uncertain: … Serif was absent"), reads "author only"
/// in that column (author visibility is absolute, contract §3.2; <see cref="ReadScope.RowsClaimAuthorOnly"/>); where the
/// record already says the view does not know ("unaware", "no record") the cell keeps it. A row both not in play and
/// author-only reads "not in play": that holds for every player-side view whatever the rows and the visibility say. As of
/// a session, cells use the same verdicts as the reads of that time (<see cref="ReadScope.VerdictVisibility"/>,
/// <see cref="ReadScope.VerdictEntries"/>): the party does not "know" a fact established later by its visibility, nor by
/// its own row that records no session.
/// </para>
/// <para>
/// <see cref="KnownTo"/> is the other direction and is NOT author-facing: it lists what one perspective knows, through the
/// perspective filter, for the knowledge resource, so it carries only names and phrasings that perspective uses.
/// </para>
/// </summary>
public sealed class KnowledgeLedger
{
    /// <summary>At most this many rows (entity rows count) per ledger.</summary>
    public const int MaxRows = 100;

    /// <summary>At most this many perspectives (columns) per ledger.</summary>
    public const int MaxColumns = 12;

    private readonly CampaignDatabase _database;

    public KnowledgeLedger(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// The grid for some entities (each followed by its facts linked <c>about</c> or <c>clue_for</c> it) and/or some facts.
    /// Columns: the given perspectives; else every knower with a row on these targets plus party, table and public
    /// (and the dm in a player campaign).
    /// </summary>
    /// <exception cref="DndInputException">Neither about nor facts, an unknown handle or perspective, too many of either.</exception>
    public Ledger Build(
        CampaignRow campaign,
        IReadOnlyList<string>? about = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? perspectives = null,
        int? asOfSession = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        return Build(connection, campaign, about, facts, perspectives, asOfSession);
    }

    internal static Ledger Build(
        SqliteConnection connection,
        CampaignRow campaign,
        IReadOnlyList<string>? about,
        IReadOnlyList<string>? facts,
        IReadOnlyList<string>? perspectives,
        int? asOfSession)
    {
        if ((about is null || about.Count == 0) && (facts is null || facts.Count == 0))
        {
            throw new DndInputException(
                "Give about (entity handles, e.g. [\"character:protector\"]) or facts (fact handles, e.g. [\"f:12\"]), or both.");
        }

        if (perspectives is { Count: > MaxColumns })
        {
            throw new DndInputException($"perspectives has {perspectives.Count} items; give at most {MaxColumns}.");
        }

        var scope = ReadScope.Open(connection, campaign, Perspective.Author, asOfSession);
        var problems = new List<string>();
        var entities = new List<EntityState>();
        var factStates = new List<FactState>();
        var resolver = new HandleResolver(connection, campaign.Id);
        Resolve(scope, about, "about", problems, (handle, text) =>
        {
            if (scope.ResolveEntity(handle) is { } entity)
            {
                entities.Add(entity);
                return true;
            }

            return false;
        });
        Resolve(scope, facts, "facts", problems, (handle, _) =>
        {
            if (resolver.TryFact(handle) is { } row && scope.Fact(row.Id) is { } fact)
            {
                factStates.Add(fact);
                return true;
            }

            return false;
        });
        DslProblems.ThrowIfAny(problems, "ledger");

        // Rows: each entity, then its facts; then the facts asked for by handle (each once).
        var rows = new List<(EntityState? Entity, FactState? Fact)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in entities.DistinctBy(e => e.Row.Id))
        {
            rows.Add((entity, null));
            foreach (var (fact, roles) in EntityReader.LinkedFacts(scope, entity.Row.Id))
            {
                if (roles.Any(r => r is CampaignValues.FactLinkRoles.About or CampaignValues.FactLinkRoles.ClueFor) && seen.Add(fact.Row.Id))
                {
                    rows.Add((null, fact));
                }
            }
        }

        foreach (var fact in factStates.Where(f => seen.Add(f.Row.Id)))
        {
            rows.Add((null, fact));
        }

        var totalRows = rows.Count;
        if (rows.Count > MaxRows)
        {
            rows = rows.Take(MaxRows).ToList();
        }

        var columns = Columns(scope, perspectives, rows);
        var ledgerRows = rows.Select(r => r.Entity is { } entity
                ? new LedgerRow(entity.Ref!, entity.Row.Name, true,
                    columns.Select(c => Cell(c, scope.VerdictFor(c, entity), isEntity: true, scope.NotInPlayReason(entity.Row),
                        entity.Row.Visibility)).ToList())
                : new LedgerRow(r.Fact!.Ref, ReadText.Excerpt(r.Fact.Row.Statement, 120), false,
                    columns.Select(c => Cell(c, scope.VerdictFor(c, r.Fact), isEntity: false,
                        ReadScope.NotInPlay(r.Fact.Row.CanonStatus) ? r.Fact.Row.CanonStatus : null, r.Fact.Row.Visibility)).ToList()))
            .ToList();
        return new Ledger(columns.Select(c => c.Perspective.Text).ToList(), ledgerRows, asOfSession, totalRows);
    }

    /// <summary>
    /// What one perspective knows: entities it has a knowledge record for (not those it merely may see by visibility),
    /// and every fact it may see. Filtered like every non-author read: display names, perspective-safe refs, its own
    /// phrasings; for the author, every entity and fact with a knowledge row and who holds it.
    /// </summary>
    public PerspectiveKnowledge KnownTo(CampaignRow campaign, Perspective perspective, int? asOfSession = null, int? limit = null, string? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(perspective);
        using var connection = ReadConnection.Open(_database);
        return KnownTo(connection, campaign, perspective, asOfSession, limit, cursor);
    }

    internal static PerspectiveKnowledge KnownTo(SqliteConnection connection, CampaignRow campaign, Perspective perspective, int? asOfSession, int? limit, string? cursor)
    {
        var take = ReadCursor.Limit(limit);
        var fingerprint = ReadCursor.Fingerprint("known", campaign.Id, perspective.Text, asOfSession);
        var offset = ReadCursor.Decode(cursor, fingerprint);
        var scope = ReadScope.Open(connection, campaign, perspective, asOfSession);
        var entityIds = connection.Query<string>(
            "SELECT DISTINCT k.entity_id FROM knowledge k JOIN entity e ON e.id = k.entity_id WHERE k.campaign_id = @campaignId " +
            "ORDER BY e.seq", new { campaignId = campaign.Id }).ToList();
        scope.LoadEntities(entityIds);
        var items = new List<object>();
        foreach (var entity in entityIds.Select(scope.Entity).OfType<EntityState>())
        {
            if (scope.IsAuthorView)
            {
                items.Add(new KnownEntity(entity.Ref!, entity.Row.Kind, entity.Row.Name, null, null,
                    CampaignSearch.KnownByLabels(entity.Entries, scope.AuthorRef, scope.AsOf)));
            }
            else if (entity.Visible && entity.Verdict.Basis != KnowledgeBases.Visibility)
            {
                items.Add(new KnownEntity(entity.Ref!, entity.Row.Kind, entity.Name, scope.ShownState(entity.Verdict.State), entity.Verdict.LearnedSession, null));
            }
        }

        var factIds = connection.Query<string>(
            scope.IsAuthorView
                ? "SELECT DISTINCT k.fact_id FROM knowledge k JOIN fact f ON f.id = k.fact_id WHERE k.campaign_id = @campaignId ORDER BY f.seq"
                : "SELECT id FROM fact WHERE campaign_id = @campaignId" + (asOfSession is null ? " AND deleted_at IS NULL" : string.Empty) + " ORDER BY seq",
            new { campaignId = campaign.Id }).ToList();
        scope.LoadFacts(factIds);
        foreach (var fact in factIds.Select(scope.Fact).OfType<FactState>().Where(f => f.Visible))
        {
            items.Add(scope.IsAuthorView
                ? new KnownFact(fact.Ref, fact.Row.Code, fact.Row.Statement, null, null, CampaignSearch.KnownByLabels(fact.Entries, scope.AuthorRef, scope.AsOf))
                : new KnownFact(fact.Ref, fact.Row.Code, fact.Text, scope.ShownState(fact.Verdict.State), fact.Verdict.LearnedSession, null));
        }

        var page = items.Skip(offset).Take(take).ToList();
        return new PerspectiveKnowledge(perspective.Text, page.OfType<KnownEntity>().ToList(), page.OfType<KnownFact>().ToList(),
            ReadCursor.Next(offset, take, items.Count, fingerprint));
    }

    // A handle that resolves to nothing is "nothing in this campaign has that handle", except in a read as of a session of an
    // entry made after it (review C08, as campaign_get and campaign_history as_of say): the ledger is author-facing, and
    // "nothing has that handle" about a handle that exists was false, and sent the model to retry the identical call.
    private static void Resolve(ReadScope scope, IReadOnlyList<string>? handles, string field, List<string> problems, Func<CampaignHandle, string, bool> resolve)
    {
        if (handles is null)
        {
            return;
        }

        if (handles.Count > CampaignLimits.MaxRefsPerGet * 3)
        {
            problems.Add($"{field} has {handles.Count} handles; give at most {CampaignLimits.MaxRefsPerGet * 3}.");
            return;
        }

        for (var i = 0; i < handles.Count; i++)
        {
            var text = handles[i] ?? string.Empty;
            if (!CampaignHandle.TryParse(text, out var handle, out var problem))
            {
                problems.Add($"{field} item {i + 1}: {problem}");
            }
            else if (!resolve(handle, text))
            {
                problems.Add($"{field} item {i + 1} ({handle}): " +
                             (scope.MadeLaterProblem(handle, text.Trim()) ?? "nothing in this campaign has that handle."));
            }
        }
    }

    private static List<PerspectiveContext> Columns(ReadScope scope, IReadOnlyList<string>? perspectives, IReadOnlyList<(EntityState? Entity, FactState? Fact)> rows)
    {
        if (perspectives is { Count: > 0 })
        {
            var problems = new List<string>();
            var given = new List<PerspectiveContext>();
            for (var i = 0; i < perspectives.Count; i++)
            {
                try
                {
                    given.Add(scope.Loader.Resolve(Perspective.Parse(perspectives[i]), scope.AsOf));
                }
                catch (DndInputException ex)
                {
                    problems.Add($"perspectives item {i + 1}: {ex.Message}");
                }
            }

            DslProblems.ThrowIfAny(problems, "ledger");
            return given.DistinctBy(c => c.Perspective.Text).ToList();
        }

        // Default: every knower with a row on these targets, then party / table / public (and the dm of a player campaign).
        var entries = rows.SelectMany(r => r.Entity?.Entries ?? r.Fact!.Entries).ToList();
        var columns = new List<string>();
        foreach (var characterId in entries.Where(e => e.KnowerKind == CampaignValues.KnowerKinds.Character && e.KnowerId is not null)
                     .Select(e => e.KnowerId!).Distinct(StringComparer.Ordinal))
        {
            if (scope.Entity(characterId) is { Row.Kind: CampaignValues.Kinds.Character } character)
            {
                columns.Add(CampaignValues.PerspectiveKinds.CharacterPrefix + character.Row.Slug);
            }
        }

        columns.Add(CampaignValues.PerspectiveKinds.Party);
        columns.Add(CampaignValues.PerspectiveKinds.Table);
        columns.Add(CampaignValues.PerspectiveKinds.Public);
        if (scope.Campaign.Role == CampaignValues.Roles.Player && entries.Any(e => e.KnowerKind == CampaignValues.KnowerKinds.Dm))
        {
            columns.Add(CampaignValues.PerspectiveKinds.Dm);
        }

        return columns.Select(c => scope.Loader.Resolve(Perspective.Parse(c), scope.AsOf)).ToList();
    }

    // notInPlay: why the row is out of every player-side view (its canon or session status), or null when it is in play.
    // visibility: the row's own; an author-only row that rows say this column knows or may know reads "author only" (class
    // summary), after the not-in-play test, which decides first.
    private static LedgerCell Cell(PerspectiveContext column, KnowledgeVerdict verdict, bool isEntity, string? notInPlay, string visibility)
    {
        if (notInPlay is not null && !column.IsAuthorView)
        {
            return new LedgerCell(column.Perspective.Text, Standings.NotInPlay, verdict.State, verdict.KnownAs, verdict.LearnedSession,
                $"not in play ({notInPlay})");
        }

        if (ReadScope.RowsClaimAuthorOnly(column, visibility, verdict))
        {
            return new LedgerCell(column.Perspective.Text, Standings.AuthorOnly, verdict.State, verdict.KnownAs, verdict.LearnedSession,
                "author only");
        }

        var standing = Standings.Of(verdict.Standing);
        string text;
        switch (verdict.Standing)
        {
            case KnowledgeStanding.NoRecord:
                text = isEntity ? "not met" : KnowledgeVerdicts.NoRecordText;
                break;
            case KnowledgeStanding.Uncertain:
                text = "uncertain: " + verdict.Explanation;
                break;
            default:
                var state = isEntity && verdict.State == CampaignValues.KnowledgeStates.Unrecognized ? "met, unrecognized" : verdict.State ?? standing;
                var name = string.IsNullOrWhiteSpace(verdict.KnownAs) ? string.Empty : $" as “{verdict.KnownAs}”";
                var when = verdict.LearnedSession is { } s ? $" (S{s.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
                text = state + name + when;
                break;
        }

        return new LedgerCell(column.Perspective.Text, standing, verdict.State, verdict.KnownAs, verdict.LearnedSession, text);
    }
}
