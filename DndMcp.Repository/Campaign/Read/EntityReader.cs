using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Microsoft.Data.Sqlite;
using K = DndMcp.Domain.Campaign.CampaignValues.Kinds;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// campaign_get's reader (contract §7): entities (and facts) by handle, with what the call includes, through the
/// perspective filter of <see cref="ReadScope"/>.
///
/// <para>
/// <b>What each view gets.</b> The author: everything, including secret text in <see cref="AuthorEntityDetail"/>, every
/// alias with its visibility, cross-links to other campaigns, the change history and every knower's row. Any other view:
/// a disguised entity shows only its display name, kind, <c>e:&lt;n&gt;</c>, the party aliases its disguise shares with
/// the view (<see cref="EntityViews.ShownAliases"/>) and the facts the view knows about it; a shown entity its name,
/// summary, body, status (withheld / lean questions as open), the aliases the view may see (public ones; party ones for
/// the party, the table, the dm and current members), tags and
/// the relations, children and facts the view may see. A relation needs its own visibility and both ends visible; a
/// planned relation is prep and is left out; cross-links, history, data, secret text and via names are never there.
/// </para>
/// <para>
/// <b>Point in time</b>: entity, fact, alias, relation, link, child, objective and clock rows are replayed to the end of
/// the session (<see cref="AsOfRows"/>); knowledge is judged by learned / valid-until sessions.
/// </para>
/// <para>
/// <b>Sheets</b> (<c>include: ["sheet"]</c>, Phase 7) come from <see cref="Characters.SheetReader"/>: the author view for
/// the author; for any other view the public line of a current party member it is shown, every other character reading as
/// one with no sheet; as of a session, the membership, the view and the sheet are those of that session.
/// </para>
/// </summary>
public sealed class EntityReader
{
    /// <summary>The most history batches a get lists per entity or fact (the newest; the detail's total counts all).</summary>
    public const int HistoryBatchesShown = 15;

    private readonly CampaignDatabase _database;

    public EntityReader(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Reads up to <see cref="CampaignLimits.MaxRefsPerGet"/> entities or facts by handle.</summary>
    /// <exception cref="DndInputException">
    /// No handles or too many, a malformed handle, a handle that names nothing this perspective may see (worded the same
    /// whether nothing has it or it is hidden, with suggestions from names the perspective knows), an unknown character
    /// perspective, or an as_of_session out of range. Up to five problems are listed.
    /// </exception>
    public GetResult Get(CampaignRow campaign, IReadOnlyList<string> handles, EntityIncludes? includes = null, Perspective? perspective = null, int? asOfSession = null)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        using var connection = ReadConnection.Open(_database);
        return Get(connection, campaign, handles, includes ?? EntityIncludes.Default, perspective, asOfSession);
    }

    /// <summary>
    /// The same over an open connection; <paramref name="wording"/> names the caller's arguments in an as_of refusal
    /// (campaign_history's as_of passes its own, review CR01).
    /// </summary>
    internal static GetResult Get(SqliteConnection connection, CampaignRow campaign, IReadOnlyList<string> handles, EntityIncludes includes, Perspective? perspective,
        int? asOfSession, AsOfWording? wording = null)
    {
        if (handles is null || handles.Count == 0)
        {
            throw new DndInputException("refs is required: give 1 to 10 handles, e.g. [\"character:belmakor\", \"f:12\"].");
        }

        if (handles.Count > CampaignLimits.MaxRefsPerGet)
        {
            throw new DndInputException(
                $"refs has {handles.Count} handles; give at most {CampaignLimits.MaxRefsPerGet} per call and page through the rest.");
        }

        var scope = ReadScope.Open(connection, campaign, perspective, asOfSession, wording);
        var problems = new List<string>();
        var found = new List<(EntityState? Entity, FactState? Fact)>();
        for (var i = 0; i < handles.Count; i++)
        {
            var text = handles[i] ?? string.Empty;
            if (!CampaignHandle.TryParse(text, out var handle, out var problem))
            {
                problems.Add($"refs item {i + 1}: {problem}");
                continue;
            }

            var item = scope.ResolveItem(handle);
            if (item.Entity is null && item.Fact is null)
            {
                problems.Add($"refs item {i + 1}: " +
                             (scope.MadeLaterProblem(handle, text.Trim()) ?? scope.NotFoundProblem(text.Trim(), (handle as CampaignHandle.EntityBySlug)?.Kind)));
                continue;
            }

            found.Add(item);
        }

        DslProblems.ThrowIfAny(problems, "refs");
        var entities = new List<EntityDetail>();
        var states = new List<EntityState>();
        var facts = new List<FactDetail>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (entity, fact) in found)
        {
            if (entity is not null && seen.Add(entity.Row.Id))
            {
                entities.Add(Detail(scope, entity, includes));
                states.Add(entity);
            }
            else if (fact is not null && seen.Add(fact.Row.Id))
            {
                facts.Add(FactDetailOf(scope, fact, includes));
            }
        }

        if (includes.Sheet)
        {
            // Every sheet of the render at once: one view-text check for all the public lines (contract §6.12).
            var sheets = Characters.SheetReader.ForEntities(scope, states);
            entities = entities.Select((detail, i) => sheets.TryGetValue(states[i].Row.Id, out var sheet) ? detail with { Sheet = sheet } : detail).ToList();
        }

        return new GetResult(entities, facts, asOfSession);
    }

    /// <summary>One entity's detail for the scope's perspective (shared by resources and history as_of).</summary>
    internal static EntityDetail Detail(ReadScope scope, EntityState entity, EntityIncludes includes)
    {
        var id = entity.Row.Id;
        var linkedFacts = LinkedFacts(scope, id);
        var shownFacts = linkedFacts.Where(f => f.Fact.Visible).ToList();
        IReadOnlyList<LinkedFact>? facts = includes.Facts
            ? shownFacts.Select(f => LinkedFactOf(scope, f.Fact, f.Roles)).ToList()
            : null;
        IReadOnlyList<KnowledgeLine>? knowledge = includes.Knowledge ? KnowledgeOf(scope, entity, shownFacts.Select(f => f.Fact).ToList()) : null;

        if (!scope.IsAuthorView && !entity.Shown)
        {
            // Disguised: the display name, kind, e:<n>, the party aliases the disguise shares with this view (names the view
            // itself uses for it, EntityViews.ShownAliases; review U01: "the old king" for a party that knows Keras as "the
            // ancient sorcerer king", which its search finds him by and its check accepts) and the facts this view knows
            // about it. Nothing else.
            var shared = scope.ShownAliases(entity).Select(a => new AliasView(a.Alias, null)).ToList();
            return new EntityDetail(entity.Ref!, entity.Row.Kind, entity.Name, null, null, null, null, null, shared, [], null, null, [],
                includes.Relations ? [] : null, facts, knowledge, includes.Children ? [] : null, includes.Sessions ? [] : null, null);
        }

        var row = entity.Row;
        var aliases = scope.ShownAliases(entity)
            .Select(a => new AliasView(a.Alias, scope.IsAuthorView ? a.Visibility : null))
            .ToList();
        var parent = scope.VisibleEntity(row.ParentId)?.Link;
        var detail = new EntityDetail(
            entity.Ref!,
            row.Kind,
            entity.Name,
            row.Subtype,
            row.Code,
            scope.DisplayStatus(entity),
            row.Summary,
            row.BodyMd,
            aliases,
            scope.Tags(id),
            parent,
            row.Kind == K.Clock ? Clock(scope, id) : null,
            row.Kind is K.Quest or K.Thread ? Objectives(scope, id) : [],
            includes.Relations ? Relations(scope, entity) : null,
            facts,
            knowledge,
            includes.Children ? Children(scope, id) : null,
            includes.Sessions ? Sessions(scope, entity) : null,
            scope.IsAuthorView ? AuthorDetail(scope, entity, includes) : null);
        return detail with { Beat = ReadBeats.View(scope, entity) };
    }

    internal static FactDetail FactDetailOf(ReadScope scope, FactState fact, EntityIncludes includes)
    {
        var links = FactLinks(scope, fact.Row.Id)
            .Select(l => (l.Role, Entity: scope.VisibleEntity(l.EntityId)))
            .Where(l => l.Entity is not null)
            .Select(l => new FactEntityLink(l.Role, l.Entity!.Link))
            .ToList();
        IReadOnlyList<KnowledgeLine>? knowledge = null;
        if (includes.Knowledge)
        {
            knowledge = scope.IsAuthorView ? AuthorRows(scope, [], [fact.Row.Id]) : [Verdict(scope, fact.Ref, fact.Verdict)];
        }

        AuthorFactDetail? author = null;
        if (scope.IsAuthorView)
        {
            var edges = scope.Connection.Query<(string FactId, string DependsOn)>(
                "SELECT d.fact_id, d.depends_on FROM fact_dependency d JOIN fact f ON f.id = d.fact_id WHERE f.campaign_id = @campaignId",
                new { campaignId = scope.Campaign.Id }).ToList();
            var dependents = Supersession.Dependents(fact.Row.Id, edges).Select(d => ReadGates.FactHandle(scope, d.FactId)).ToList();
            var history = includes.History ? HistoryReader.ForTarget(scope.Connection, scope.Campaign, null, fact.Row.Id, HistoryBatchesShown) : null;
            author = new AuthorFactDetail(AuthorFactOf(scope, fact), dependents, history?.Batches, history?.Total);
        }

        return new FactDetail(fact.Ref, fact.Row.Code, fact.Text, links, knowledge, author);
    }

    internal static LinkedFact LinkedFactOf(ReadScope scope, FactState fact, IReadOnlyList<string> roles) =>
        new(fact.Ref, fact.Row.Code, fact.Text, roles, scope.IsAuthorView ? AuthorFactOf(scope, fact) : null);

    internal static AuthorFact AuthorFactOf(ReadScope scope, FactState fact)
    {
        var row = fact.Row;
        var (gate, unreadable) = ReadGates.Parse(row);
        var dependsOn = scope.Connection.Query<string>("SELECT depends_on FROM fact_dependency WHERE fact_id = @id ORDER BY depends_on",
            new { id = row.Id }).Select(f => ReadGates.FactHandle(scope, f)).ToList();
        return new AuthorFact(
            row.FactType,
            row.Truth,
            row.CanonStatus,
            row.Confidence,
            row.Visibility,
            row.Source,
            row.SupersededBy is null ? null : ReadGates.FactHandle(scope, row.SupersededBy),
            scope.SessionNumber(row.EstablishedSessionId),
            gate is null ? null : ReadGates.WithHandles(scope, gate),
            unreadable,
            CampaignSearch.KnownByLabels(fact.Entries, scope.AuthorRef, scope.AsOf),
            dependsOn.OrderBy(h => h, StringComparer.Ordinal).ToList());
    }

    /// <summary>Facts linked to an entity as of the scope's session, each with its roles, in fact order.</summary>
    internal static List<(FactState Fact, IReadOnlyList<string> Roles)> LinkedFacts(ReadScope scope, string entityId)
    {
        var links = scope.Connection.Query<FactLinkRow>(
            $"SELECT {FactLinkRow.Columns} FROM fact_link WHERE entity_id = @entityId", new { entityId }).ToList();
        if (scope.AsOf is { } session)
        {
            var table = CampaignTables.FactLink;
            links = AsOfRows.ForEntities<FactLinkRow>(scope.Connection, table,
                links.Select(l => table.TargetId([l.FactId, l.EntityId, l.Role])), [entityId], session);
        }

        scope.LoadFacts(links.Select(l => l.FactId));
        return links.GroupBy(l => l.FactId, StringComparer.Ordinal)
            .Select(g => (Fact: scope.Fact(g.Key), Roles: (IReadOnlyList<string>)g.Select(l => l.Role).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList()))
            .Where(p => p.Fact is not null)
            .Select(p => (p.Fact!, p.Roles))
            .OrderBy(p => p.Item1.Row.Seq)
            .ToList();
    }

    /// <summary>
    /// An entity's relations this view may see: its own visibility admits the view, both ends are visible to it, it is not
    /// planned, and (review L07) it is not an identity relation (<c>same_as</c>) to an end the view knows only under a
    /// disguise. A disguised entity shows no relations on its own page; listed from the other end, "the Lich Queen ←
    /// same_as from the veiled woman" told the party exactly who the veiled woman is. Other relations to a disguised end
    /// stay (they name it by its disguise and say nothing of who it is).
    /// </summary>
    internal static IReadOnlyList<RelationView> Relations(ReadScope scope, EntityState entity)
    {
        var id = entity.Row.Id;
        var rows = scope.Connection.Query<RelationRow>(
            $"SELECT {RelationRow.Columns} FROM relation WHERE from_id = @id OR to_id = @id", new { id }).ToList();
        if (scope.AsOf is { } session)
        {
            rows = AsOfRows.ForEntities<RelationRow>(scope.Connection, CampaignTables.Relation, rows.Select(r => r.Id), [id], session);
        }

        scope.LoadEntities(rows.Select(r => r.FromId == id ? r.ToId : r.FromId));
        var views = new List<(RelationRow Row, RelationView View)>();
        foreach (var row in rows)
        {
            var outgoing = row.FromId == id;
            var other = scope.VisibleEntity(outgoing ? row.ToId : row.FromId);
            if (!scope.IsAuthorView &&
                (other is null || row.Status == CampaignValues.RelationStatuses.Planned ||
                 !Audience.RelationVisible(scope.Who, row.Visibility, true, true) ||
                 (row.Rel == CampaignValues.Rels.SameAs && (!other.Shown || !entity.Shown))))
            {
                continue;
            }

            var link = other?.Link ?? new EntityLink(scope.AuthorRef(outgoing ? row.ToId : row.FromId), "entity", "(not in this campaign's view)");
            views.Add((row, new RelationView(
                row.Rel,
                outgoing ? RelationDirections.Out : RelationDirections.In,
                link,
                row.Label,
                row.Status,
                scope.SessionNumber(row.SinceSessionId),
                scope.SessionNumber(row.UntilSessionId),
                row.Attitude,
                row.Symmetric != 0,
                scope.IsAuthorView ? row.Visibility : null,
                scope.IsAuthorView ? row.Data : null)));
        }

        return views
            .OrderBy(v => v.View.Rel, StringComparer.Ordinal)
            .ThenBy(v => v.View.Direction, StringComparer.Ordinal)
            .ThenBy(v => v.View.Other.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.View)
            .ToList();
    }

    private static IReadOnlyList<EntityLink> Children(ReadScope scope, string id)
    {
        var ids = scope.Connection.Query<string>("SELECT id FROM entity WHERE parent_id = @id", new { id }).ToList();
        if (scope.AsOf is { } session)
        {
            // Children then: today's children plus entities whose parent changed after the session (their log row's
            // other_entity_id is the parent).
            ids.AddRange(scope.Connection.Query<string>(
                "SELECT DISTINCT cl.target_id FROM change_log cl JOIN session s ON s.entity_id = cl.session_id " +
                "WHERE cl.target_table = 'entity' AND cl.other_entity_id = @id AND s.number > @session", new { id, session }));
        }

        scope.LoadEntities(ids);
        return ids.Distinct(StringComparer.Ordinal)
            .Select(scope.VisibleEntity)
            .Where(c => c is not null && c.Row.ParentId == id)
            .Select(c => c!)
            .OrderBy(c => c.Row.SortKey is null ? 1 : 0)
            .ThenBy(c => c.Row.SortKey ?? 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Link)
            .ToList();
    }

    private static IReadOnlyList<SessionLink> Sessions(ReadScope scope, EntityState entity)
    {
        var links = new List<SessionLink>();
        if (scope.VisibleEntity(entity.Row.IntroducedSessionId) is { } introduced && scope.SessionNumber(introduced.Row.Id) is { } introducedNumber)
        {
            links.Add(new SessionLink(introduced.Ref!, introducedNumber, introduced.Name, SessionLinkKinds.Introduced, null));
        }

        var attendance = scope.Connection.Query<AttendanceRow>(
            $"SELECT {AttendanceRow.Columns} FROM session_attendance WHERE character_id = @id", new { id = entity.Row.Id }).ToList();
        if (scope.AsOf is { } session)
        {
            var table = CampaignTables.SessionAttendance;
            attendance = AsOfRows.ForEntities<AttendanceRow>(scope.Connection, table,
                attendance.Select(a => table.TargetId([a.SessionId, a.CharacterId])), [entity.Row.Id], session);
        }

        scope.LoadEntities(attendance.Select(a => a.SessionId));
        foreach (var row in attendance)
        {
            if (scope.VisibleEntity(row.SessionId) is { } sessionEntity && scope.SessionNumber(row.SessionId) is { } number)
            {
                links.Add(new SessionLink(sessionEntity.Ref!, number, sessionEntity.Name, row.Present != 0 ? SessionLinkKinds.Attended : SessionLinkKinds.Absent,
                    scope.IsAuthorView ? row.Note : null));
            }
        }

        return links.OrderBy(l => l.Number).ThenBy(l => l.Relation, StringComparer.Ordinal).ToList();
    }

    private static ClockView? Clock(ReadScope scope, string id)
    {
        var values = scope.AsOf is { } session
            ? ChangeReplay.RowAsOf(scope.Connection, CampaignTables.Clock.Name, id, session)
            : null;
        var clock = values is not null
            ? CampaignRows.FromValues<ClockRow>(values)
            : scope.AsOf is null
                ? scope.Connection.QueryFirstOrDefault<ClockRow>($"SELECT {ClockRow.Columns} FROM clock WHERE entity_id = @id", new { id })
                : null;
        if (clock is null || (!scope.IsAuthorView && clock.ShownToPlayers == 0))
        {
            return null;
        }

        var front = scope.VisibleEntity(clock.FrontId)?.Link;
        return scope.IsAuthorView
            ? new ClockView(clock.Segments, clock.Filled, clock.Unit, front, clock.ShownToPlayers != 0, clock.OnFillMd)
            : new ClockView(clock.Segments, clock.Filled, clock.Unit, front, null, null);
    }

    private static IReadOnlyList<ObjectiveView> Objectives(ReadScope scope, string id)
    {
        var rows = scope.Connection.Query<ObjectiveRow>(
            $"SELECT {ObjectiveRow.Columns} FROM objective WHERE quest_id = @id", new { id }).ToList();
        if (scope.AsOf is { } session)
        {
            rows = AsOfRows.ForEntities<ObjectiveRow>(scope.Connection, CampaignTables.Objective, rows.Select(o => o.Id), [id], session);
        }

        // The index is the position among all objectives (what the objective op addresses), counted before filtering.
        return rows.OrderBy(o => o.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal)
            .Select((o, i) => (Row: o, Index: i + 1))
            .Where(o => scope.IsAuthorView ||
                        (o.Row.Status != CampaignValues.ObjectiveStatuses.Hidden && Audience.Sees(scope.Who, o.Row.Visibility)))
            .Select(o => new ObjectiveView(scope.IsAuthorView ? o.Index : 0, o.Row.Text, o.Row.Status, o.Row.Progress, o.Row.ProgressMax,
                scope.IsAuthorView ? o.Row.Visibility : null))
            .Select((o, i) => scope.IsAuthorView ? o : o with { Index = i + 1 })
            .ToList();
    }

    private static AuthorEntityDetail AuthorDetail(ReadScope scope, EntityState entity, EntityIncludes includes)
    {
        var row = entity.Row;
        var crossLinks = scope.Connection.Query<CrossLinkRow>(
                $"SELECT {CrossLinkRow.Columns} FROM cross_link WHERE a_id = @id OR b_id = @id", new { id = row.Id })
            .Select(c =>
            {
                var otherId = c.AId == row.Id ? c.BId : c.AId;
                var other = scope.Connection.QueryFirstOrDefault<(string? Slug, string? Name)>(
                    "SELECT c.slug, e.name FROM entity e JOIN campaign c ON c.id = e.campaign_id WHERE e.id = @otherId", new { otherId });
                return new CrossLinkView(other.Slug ?? "(deleted)", scope.AuthorRef(otherId), other.Name ?? "(deleted entity)", c.Note);
            })
            .OrderBy(c => c.Ref, StringComparer.Ordinal)
            .ToList();
        var introduced = row.IntroducedSessionId is null ? null : scope.AuthorRef(row.IntroducedSessionId);
        var history = includes.History ? HistoryReader.ForTarget(scope.Connection, scope.Campaign, row.Id, null, HistoryBatchesShown) : null;
        return new AuthorEntityDetail(
            row.Visibility,
            row.CanonStatus,
            row.Confidence,
            row.Source,
            row.SecretMd,
            row.Data,
            introduced,
            row.SortKey,
            crossLinks,
            row.Kind == K.Secret ? ReadGates.Secret(scope, entity) : null,
            history?.Batches,
            history?.Total)
        {
            SeqRef = row.SeqHandle,
        };
    }

    // Non-author: the perspective's verdict on the entity and on each fact it may see. Author: every knower's row.
    private static IReadOnlyList<KnowledgeLine> KnowledgeOf(ReadScope scope, EntityState entity, IReadOnlyList<FactState> facts)
    {
        if (scope.IsAuthorView)
        {
            return AuthorRows(scope, [entity.Row.Id], facts.Select(f => f.Row.Id).ToList());
        }

        var lines = new List<KnowledgeLine> { Verdict(scope, entity.Ref!, entity.Verdict) };
        lines.AddRange(facts.Select(f => Verdict(scope, f.Ref, f.Verdict)));
        return lines;
    }

    private static KnowledgeLine Verdict(ReadScope scope, string target, KnowledgeVerdict verdict) =>
        new(target, scope.Who.Perspective.Text, Standings.Of(verdict.Standing), scope.ShownState(verdict.State), verdict.KnownAs,
            verdict.LearnedSession, scope.ShownExplanation(verdict));

    /// <summary>Every knower's row about the entities and facts (author view), applicable at the scope's session.</summary>
    internal static IReadOnlyList<KnowledgeLine> AuthorRows(ReadScope scope, IReadOnlyList<string> entityIds, IReadOnlyList<string> factIds)
    {
        var rows = new List<KnowledgeRow>();
        if (entityIds.Count > 0)
        {
            rows.AddRange(scope.Connection.Query<KnowledgeRow>(
                $"SELECT {KnowledgeRow.Columns} FROM knowledge WHERE entity_id IN @entityIds", new { entityIds }));
        }

        foreach (var chunk in factIds.Chunk(400))
        {
            rows.AddRange(scope.Connection.Query<KnowledgeRow>(
                $"SELECT {KnowledgeRow.Columns} FROM knowledge WHERE fact_id IN @chunk", new { chunk }));
        }

        var lines = new List<(int Order, string Knower, KnowledgeLine Line)>();
        foreach (var row in rows)
        {
            var learned = scope.SessionNumber(row.LearnedSessionId);
            var validUntil = scope.SessionNumber(row.ValidUntilSessionId);
            if (!KnowledgeVerdicts.Applies(new KnowledgeEntry(row.KnowerKind, row.KnowerId, row.State, row.KnownAs, learned, validUntil), scope.AsOf))
            {
                continue;
            }

            string target;
            int order;
            if (row.FactId is not null)
            {
                target = ReadGates.FactHandle(scope, row.FactId);
                order = 1 + factIds.ToList().IndexOf(row.FactId);
            }
            else
            {
                target = scope.AuthorRef(row.EntityId);
                order = 0;
            }

            var knower = row.KnowerKind == CampaignValues.KnowerKinds.Character ? scope.AuthorRef(row.KnowerId) : row.KnowerKind;
            var standing = CampaignValues.KnowledgeStates.AwareStates.Contains(row.State) ? Standings.Knows : Standings.DoesNotKnow;
            var detail = new KnowledgeRowDetail(row.How, row.ViaEntityId is null ? null : scope.AuthorRef(row.ViaEntityId), row.Note,
                validUntil, row.LearnedIngame);
            lines.Add((order, knower, new KnowledgeLine(target, knower, standing, row.State, row.KnownAs, learned, null, detail)));
        }

        return lines.OrderBy(l => l.Order).ThenBy(l => KnowerOrder(l.Knower)).ThenBy(l => l.Knower, StringComparer.Ordinal)
            .Select(l => l.Line).ToList();
    }

    private static IReadOnlyList<(string EntityId, string Role)> FactLinks(ReadScope scope, string factId)
    {
        var links = scope.Connection.Query<FactLinkRow>(
            $"SELECT {FactLinkRow.Columns} FROM fact_link WHERE fact_id = @factId", new { factId }).ToList();
        if (scope.AsOf is { } session)
        {
            var table = CampaignTables.FactLink;
            links = AsOfRows.ForFacts<FactLinkRow>(scope.Connection, table,
                links.Select(l => table.TargetId([l.FactId, l.EntityId, l.Role])), [factId], session);
        }

        scope.LoadEntities(links.Select(l => l.EntityId));
        return links.Where(l => l.FactId == factId)
            .OrderBy(l => l.Role, StringComparer.Ordinal).ThenBy(l => scope.Entity(l.EntityId)?.Row.Seq ?? 0)
            .Select(l => (l.EntityId, l.Role)).ToList();
    }

    private static int KnowerOrder(string knower) => knower switch
    {
        CampaignValues.KnowerKinds.Author => 0,
        CampaignValues.KnowerKinds.Dm => 1,
        CampaignValues.KnowerKinds.Table => 2,
        CampaignValues.KnowerKinds.Party => 3,
        CampaignValues.KnowerKinds.Public => 4,
        _ => 5,
    };

    internal static string SessionHandle(int number) => "session:" + number.ToString(CultureInfo.InvariantCulture);
}
