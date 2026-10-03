using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign;

/// <summary>
/// The database side of knowledge verdicts, shared by the write and read paths: resolving a <see cref="Perspective"/> to a
/// <see cref="PerspectiveContext"/>, loading knowledge rows as <see cref="KnowledgeEntry"/> values (session ids turned into
/// session numbers, "via" entities into names), attendance, and whether a fact is in play. Domain's
/// <c>KnowledgeVerdicts</c> decides; this only loads, so the precedence rules stay pure and table-tested.
///
/// <para>
/// One instance per operation (it caches the campaign's session numbers and attendance on first use; both are small), over
/// the operation's connection and transaction. Not thread-safe, like the connection.
/// </para>
/// <para>
/// <b>Author-only text inside the entries.</b> <see cref="KnowledgeEntry.ViaName"/> is the via entity's TRUE name,
/// whatever its visibility and whatever the reader knows it as ("told by Keras", where Keras is author-only and the party
/// knows him as the old king). The loader cannot know who will read the output, so it does not filter. Verdicts may use
/// it for the author; anything rendered for a non-author perspective must not print it unless that perspective sees the
/// via entity under that name, or it breaks the leak rule (contract §0). <c>Note</c> is the author's free text and follows
/// the same rule.
/// </para>
/// </summary>
public sealed class KnowledgeLoader
{
    /// <summary>The canon statuses under which a fact is not in play (not yet, or no longer, true in the story).</summary>
    public static readonly IReadOnlySet<string> NotInPlayCanonStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        CampaignValues.CanonStatuses.Proposed,
        CampaignValues.CanonStatuses.Planned,
        CampaignValues.CanonStatuses.Struck,
        CampaignValues.CanonStatuses.Superseded,
        CampaignValues.CanonStatuses.Lean,
    };

    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction? _transaction;
    private readonly Dictionary<int, KnowledgeHistory> _histories = [];
    private readonly Dictionary<string, int?> _createdIn = new(StringComparer.Ordinal);
    private Dictionary<string, int>? _sessionNumbers;
    private SessionAttendance? _attendance;

    public KnowledgeLoader(SqliteConnection connection, CampaignRow campaign, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        CampaignDatabase.EnsureDapperConfigured();
        _connection = connection;
        _transaction = transaction;
        Campaign = campaign;
    }

    public CampaignRow Campaign { get; }

    /// <summary>Session entity id → session number, for this campaign.</summary>
    public IReadOnlyDictionary<string, int> SessionNumbers => _sessionNumbers ??= _connection
        .Query<(string EntityId, long Number)>(
            "SELECT entity_id, number FROM session WHERE campaign_id = @campaignId", new { campaignId = Campaign.Id }, _transaction)
        .ToDictionary(s => s.EntityId, s => checked((int)s.Number), StringComparer.Ordinal);

    /// <summary>Attendance over session_attendance (<see cref="SessionAttendance"/>), loaded on first use.</summary>
    public IAttendance Attendance => _attendance ??= SessionAttendance.Load(_connection, Campaign.Id, _transaction);

    /// <summary>The session number of a session entity id; null for null or an id that is not a session of this campaign.</summary>
    public int? SessionNumber(string? sessionEntityId) =>
        sessionEntityId is not null && SessionNumbers.TryGetValue(sessionEntityId, out var number) ? number : null;

    /// <summary>
    /// The session an entity entered the story in, for the dated party-visibility default (review L02): its introduced
    /// session when one is recorded, else the session its creation was logged under (the batch's session context: written
    /// during session 2, or with <c>"session": 2</c>); null when it has neither (world-building and prep between sessions,
    /// which stays undated, as contract §3.5 treats it as timeless).
    ///
    /// <para>
    /// <b>Why the creation session too.</b> Nothing fills <c>introduced_session</c> unless the op gives it, while a fact
    /// written in a session gets its established session from the batch. Dated by the introduced session alone, an NPC
    /// met in session 2 was undated, so Serif, absent that night, read Captain Rhee's name and summary, and Tristan, gone
    /// since session 1, read the place the party reached in session 3, while the fact written in the same batch was
    /// rightly hidden from both. Call <see cref="LoadCreationSessions"/> first when dating many entities.
    /// </para>
    /// </summary>
    public int? IntroducedSession(EntityRow entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (SessionNumber(entity.IntroducedSessionId) is { } introduced)
        {
            return introduced;
        }

        LoadCreationSessions([entity.Id]);
        return _createdIn[entity.Id];
    }

    /// <summary>
    /// Reads, in a few queries, the session each entity's first logged create was filed under (see
    /// <see cref="IntroducedSession"/>); ids already read are skipped.
    /// </summary>
    public void LoadCreationSessions(IEnumerable<string> entityIds)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        var wanted = entityIds.Where(id => !_createdIn.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var chunk in wanted.Chunk(400))
        {
            foreach (var id in chunk)
            {
                _createdIn[id] = null;
            }

            var creates = _connection.Query<(string EntityId, string? SessionId)>(
                "SELECT entity_id, session_id FROM change_log WHERE entity_id IN @ids AND target_table = @table AND op = @create " +
                "AND target_id = entity_id ORDER BY seq",
                new { ids = chunk, table = CampaignTables.Entity.Name, create = CampaignValues.ChangeOps.Create }, _transaction);
            foreach (var group in creates.GroupBy(c => c.EntityId, StringComparer.Ordinal))
            {
                _createdIn[group.Key] = SessionNumber(group.First().SessionId);
            }
        }
    }

    /// <summary>
    /// The verdict context for a perspective. A character perspective must name a non-deleted character of this campaign;
    /// its membership is its <c>member_of</c> relation (current or former) to the campaign's party, with since/until as
    /// session numbers.
    ///
    /// <para>
    /// <b>A short slug finds the one character it begins</b> (review U03): when no character has the slug but exactly one
    /// character the party knows by its own name has a slug starting with it and a hyphen, the perspective is that
    /// character, and the context's <see cref="PerspectiveContext.Perspective"/> is its full handle
    /// (<c>character:belmakor</c> → <c>character:belmakor-silverwind</c>) so the banner can say which it took. Campaign
    /// create derives the PC's slug from the full name, while the user (and every example) says "Belmakor": every fresh
    /// session spent its first call on the refusal. Only characters the refusal could suggest are candidates (and only
    /// they are counted), so a typed prefix never completes to, or is refused because of, a character the party does not
    /// know by name; nor to one that knows itself only by another name (<see cref="UniqueBySlugPrefix"/>). In a DM
    /// campaign a PC written without visibility is restricted, and its member_of author-only, so nothing says the party
    /// knows it by name and it is typed in full (a deliberate limit: completing to it would let a prefix confirm an entity
    /// the party has no record of, which the refusal never does).
    /// </para>
    /// <para>
    /// <b><see cref="PerspectiveContext.CharacterName"/> is the name the character knows itself by</b> (review L04): its
    /// own view of its own entity, which is its own row's known_as when it has one ("the stranger"). Null when that view
    /// cannot see the entity (author visibility, not in play, no verdict that it knows) or when the perspective was typed as
    /// <c>e:&lt;n&gt;</c>, the handle a disguise is shown under; the banner then names only the handle. The stored name
    /// printed there and in every verdict explanation told a self-disguised character his true name, named an author-only
    /// character to whoever typed its slug, and turned <c>character:e:3</c> (the party's "the veiled woman") into "Morwen
    /// Vashkar".
    /// </para>
    /// <para>
    /// <b>As of a session</b> (<paramref name="asOfSession"/>, review LR01) the character's own view is the one it had then:
    /// its row and its knowledge rows replayed to the end of that session (<see cref="EntriesForEntitiesAsOf"/>, the replay
    /// and dated rule every as_of read uses), so the name it knew itself by then names it, and the completion's disguise
    /// guard asks whether it knew itself by another name then. Judged by today's rows, a read as of session 2 by a
    /// character who learned his name in session 3 printed "(Keras Dawnbreaker)" above a listing of himself as "the
    /// stranger", and "character:keras" completed to the handle that spells it.
    /// </para>
    /// </summary>
    /// <exception cref="DndInputException">
    /// No such character. The message says only that no character by that handle exists (never that the handle names
    /// something else: that would confirm a hidden entity), and suggests characters the party knows by their own names.
    /// </exception>
    public PerspectiveContext Resolve(Perspective perspective, int? asOfSession = null)
    {
        ArgumentNullException.ThrowIfNull(perspective);
        if (perspective.Kind != CampaignValues.PerspectiveKinds.Character)
        {
            return PerspectiveContext.For(perspective, Campaign.Role);
        }

        var handle = perspective.Character!;
        var resolver = new HandleResolver(_connection, Campaign.Id, _transaction);
        var character = resolver.TryEntity(handle);
        var resolved = perspective;
        if (character is not { Kind: CampaignValues.Kinds.Character } && handle is CampaignHandle.EntityBySlug bySlug &&
            UniqueBySlugPrefix(bySlug.Slug, asOfSession) is { } completed)
        {
            character = completed;
            resolved = Perspective.ForCharacter(new CampaignHandle.EntityBySlug(null, completed.Slug));
        }

        if (character is not { Kind: CampaignValues.Kinds.Character })
        {
            var suggestions = resolver.Suggest(CampaignValues.Kinds.Character, SearchText(handle), PartyKnownView());
            var hint = suggestions.Count == 0
                ? " Characters are named character:<slug>; campaign_search with kinds [\"character\"] lists them."
                : $" Did you mean {string.Join(", ", suggestions.Select(s => CampaignValues.PerspectiveKinds.CharacterPrefix + s.Replace("character:", string.Empty, StringComparison.Ordinal)))}?";
            throw new DndInputException($"perspective \"{perspective.Text}\": no character {handle.Text} in this campaign.{hint}");
        }

        var context = Context(resolved, character);
        return handle is CampaignHandle.EntityBySeq ? context : context with { CharacterName = OwnName(OwnView(context, character, asOfSession)) };
    }

    private PerspectiveContext Context(Perspective perspective, EntityRow character) =>
        new(perspective, Campaign.Role, character.Id, null, Membership(character.Id));

    /// <summary>
    /// The view a character's own perspective has of its own entity (see <see cref="Resolve"/>), with the not-in-play rule
    /// every read applies: hidden when it is not in play. As of a session, the row and the knowledge rows are those of the
    /// end of that session, and the verdict is dated as every as_of verdict is; a character that did not exist then (or
    /// was deleted by then) is hidden from itself. Its aliases are today's: they never decide whether the view is
    /// disguised, nor the name it is disguised under.
    /// </summary>
    private EntityView OwnView(PerspectiveContext self, EntityRow character, int? asOfSession)
    {
        var row = character;
        if (asOfSession is { } session)
        {
            if (ChangeReplay.RowAsOf(_connection, CampaignTables.Entity.Name, character.Id, session, _transaction) is not { } then)
            {
                return EntityView.Hidden;
            }

            row = CampaignRows.FromValues<EntityRow>(then);
        }

        if (row.DeletedAt is not null || NotInPlayCanonStatuses.Contains(row.CanonStatus))
        {
            return EntityView.Hidden;
        }

        var entries = asOfSession is { } n ? EntriesForEntitiesAsOf([row.Id], n) : EntriesForEntities([row.Id]);
        var verdict = KnowledgeVerdicts.Evaluate(self, entries[row.Id], row.Visibility, Attendance, asOfSession, IntroducedSession(row));
        var aliases = _connection.Query<(string Alias, string Visibility)>(
            "SELECT alias, visibility FROM entity_alias WHERE entity_id = @id", new { id = row.Id }, _transaction).ToList();
        return EntityViews.For(self, row.Kind, row.Name, row.Visibility, aliases, verdict);
    }

    /// <summary>
    /// The name a character's own view gives its own entity: its true name (as the view shows it) when that view shows it
    /// undisguised, the known_as it is disguised under, else null (hidden from itself, or unrecognised with no name: the
    /// view's stand-in "an unrecognized character" is nobody's name).
    /// </summary>
    private static string? OwnName(EntityView view)
    {
        if (!view.Visible)
        {
            return null;
        }

        return !view.Disguised || view.UsedNames.Count > 0 ? view.DisplayName : null;
    }

    /// <summary>
    /// The one character whose slug starts with <paramref name="slug"/> and a hyphen among those the "no character"
    /// refusal may suggest (printed by that slug); null when there is none or more than one (see <see cref="Resolve"/>).
    /// A character whose own view knows itself only under another name (its own row's known_as: "the stranger") is neither
    /// taken nor counted: the banner prints the handle it completed to, and that slug spells the true name the view hides
    /// from him, which the caller never typed (the reason a perspective typed as <c>e:&lt;n&gt;</c> gets no name). As of a
    /// session, that is his own view as of that session (see <see cref="Resolve"/>).
    /// </summary>
    private EntityRow? UniqueBySlugPrefix(string slug, int? asOfSession)
    {
        var prefix = slug + "-";
        var candidates = _connection.Query<EntityRow>(
            $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND kind = @kind AND deleted_at IS NULL " +
            "AND substr(slug, 1, @length) = @prefix",
            new { campaignId = Campaign.Id, kind = CampaignValues.Kinds.Character, length = prefix.Length, prefix }, _transaction).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var known = PartyKnownView();
        var named = candidates
            .Where(c => known(c) is { } shown && shown.Handle == c.Handle)
            .Where(c => !OwnView(Context(Perspective.ForCharacter(new CampaignHandle.EntityBySlug(null, c.Slug)), c), c, asOfSession).Disguised)
            .ToList();
        return named.Count == 1 ? named[0] : null;
    }

    /// <summary>
    /// The character's party membership, or null when it has never been a member (or the campaign has no party). A former
    /// <c>member_of</c> relation is <see cref="PartyMembership.Former"/> even with no until: read as current, it handed a
    /// member who had left the party's relations, objectives and aliases, and everything the party learned in sessions
    /// with no attendance recorded.
    /// </summary>
    public PartyMembership? Membership(string characterId)
    {
        if (Campaign.PartyId is null)
        {
            return null;
        }

        var relation = _connection.Query<(string? Since, string? Until, string Status)>(
            "SELECT since_session_id, until_session_id, status FROM relation WHERE campaign_id = @campaignId AND from_id = @characterId " +
            "AND rel = @rel AND to_id = @partyId AND status IN (@current, @former)",
            new
            {
                campaignId = Campaign.Id,
                characterId,
                rel = CampaignValues.Rels.MemberOf,
                partyId = Campaign.PartyId,
                current = CampaignValues.RelationStatuses.Current,
                former = CampaignValues.RelationStatuses.Former,
            },
            _transaction).ToList();
        return relation.Count == 0
            ? null
            : new PartyMembership(SessionNumber(relation[0].Since), SessionNumber(relation[0].Until))
            {
                Former = relation[0].Status == CampaignValues.RelationStatuses.Former,
            };
    }

    /// <summary>
    /// Knowledge rows about each fact (every requested id is a key; no rows gives an empty list). Each entry's
    /// <see cref="KnowledgeEntry.ViaName"/> is a true name that may be author-only (class summary): never print it for a
    /// non-author perspective as it is.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>> EntriesForFacts(IEnumerable<string> factIds) =>
        Entries("fact_id", factIds);

    /// <summary>
    /// Knowledge rows about each entity (awareness: met, heard, unrecognized, …; every requested id is a key). As
    /// <see cref="EntriesForFacts"/>, <see cref="KnowledgeEntry.ViaName"/> may be an author-only true name.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>> EntriesForEntities(IEnumerable<string> entityIds) =>
        Entries("entity_id", entityIds);

    /// <summary>
    /// <see cref="EntriesForFacts"/> as the rows stood at the end of session <paramref name="session"/>, for the verdicts
    /// of a read as of that session (see <see cref="EntriesAsOf"/>).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>> EntriesForFactsAsOf(IEnumerable<string> factIds, int session) =>
        EntriesAsOf("fact_id", factIds, session);

    /// <summary>
    /// <see cref="EntriesForEntities"/> as the rows stood at the end of session <paramref name="session"/>, for the
    /// verdicts of a read as of that session (see <see cref="EntriesAsOf"/>).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>> EntriesForEntitiesAsOf(IEnumerable<string> entityIds, int session) =>
        EntriesAsOf("entity_id", entityIds, session);

    /// <summary>The number of the session a fact was established in; null when none (or not a session of this campaign).</summary>
    public int? EstablishedSession(FactRow fact) => SessionNumber(fact.EstablishedSessionId);

    /// <summary>
    /// Whether a fact is in play at the end of session <paramref name="asOfSession"/> (null: now): established in a session
    /// numbered ≤ n, its canon status not proposed / planned / struck / superseded / lean, and not deleted (contract §3.4).
    /// A gate's <c>after</c> and a reveal rule's <c>until</c> read this. For a point-in-time check pass the fact as of
    /// that session (<see cref="ChangeReplay"/>), since its canon status may have changed since.
    /// </summary>
    public bool InPlay(FactRow fact, int? asOfSession)
    {
        ArgumentNullException.ThrowIfNull(fact);
        if (fact.DeletedAt is not null || NotInPlayCanonStatuses.Contains(fact.CanonStatus))
        {
            return false;
        }

        return EstablishedSession(fact) is { } established && (asOfSession is null || established <= asOfSession);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>> Entries(string targetColumn, IEnumerable<string> ids)
    {
        var wanted = ids.Distinct(StringComparer.Ordinal).ToList();
        var rows = Rows(targetColumn, wanted);
        var viaNames = ViaNames(rows);
        var result = wanted.ToDictionary(id => id, _ => new List<KnowledgeEntry>(), StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[Target(targetColumn, row)!].Add(Entry(row, viaNames));
        }

        return Frozen(result);
    }

    /// <summary>
    /// The knowledge rows about each target as they stood at the end of session <paramref name="session"/> (review L05,
    /// fix FQ2b): a row's state, known_as and learned and valid-until sessions are replayed from change_log
    /// (<see cref="ChangeReplay"/>, the same replay every other row of an as_of read gets), and a row deleted since is back.
    ///
    /// <para>
    /// <b>Why replay knowledge too.</b> A knowledge row is edited in place: a known_as recorded again with the state
    /// unchanged keeps its learned session. Read by its learned session alone, the row the party met "the veiled woman" by
    /// in session 1 and named "Morwen Vashkar" in session 3 says "Morwen Vashkar since session 1", and an as_of 2 read
    /// showed the party her true name, summary and slug. Replayed, the row as of 2 is the one session 2 had.
    /// </para>
    /// <para>
    /// <b>A row recorded after the session did not exist yet, but it still stops the visibility default.</b> Its content
    /// (state, known_as) is not used; it reaches the verdict as a row learned in the session it was recorded in (or its own
    /// learned session, when that is later), which <see cref="KnowledgeVerdicts"/> reads as "not yet" (the class summary's
    /// "a later row is a wall"). Dropped instead, the verdict fell through to the visibility default, which is exactly the
    /// leak the wall exists to stop: the party met Morwen in session 3, and as of session 2 a party-visible Morwen was
    /// "known" under her true name. A row recorded later about an earlier session (a retroactive record) therefore counts
    /// from the session it was recorded in, not before.
    /// </para>
    /// <para>
    /// Changes with no session context (prep between sessions) are timeless, as in every replay: never reversed. The one
    /// exception is an edit that gave the row a later learned session through the knower's own session (review LR03) and
    /// changed something else of it too, which is replayed as made in that session (<see cref="History"/>).
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>> EntriesAsOf(string targetColumn, IEnumerable<string> ids, int session)
    {
        var wanted = ids.Distinct(StringComparer.Ordinal).ToList();
        var wantedSet = wanted.ToHashSet(StringComparer.Ordinal);
        var history = History(session);
        var current = Rows(targetColumn, wanted);
        var currentIds = current.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var then = new List<KnowledgeRow>();
        var walls = new List<(KnowledgeRow Row, int RecordedIn)>();
        foreach (var row in current)
        {
            if (!history.Touched.Contains(row.Id))
            {
                then.Add(row);
            }
            else if (history.AsOf.GetValueOrDefault(row.Id) is { } before)
            {
                then.Add(before);
            }
            else
            {
                walls.Add((row, history.CreatedIn.GetValueOrDefault(row.Id, session + 1)));
            }
        }

        // Rows that existed then and have been deleted since.
        then.AddRange(history.AsOf
            .Where(p => p.Value is not null && !currentIds.Contains(p.Key) && Target(targetColumn, p.Value) is { } target && wantedSet.Contains(target))
            .Select(p => p.Value!));

        var viaNames = ViaNames(then);
        var result = wanted.ToDictionary(id => id, _ => new List<KnowledgeEntry>(), StringComparer.Ordinal);
        foreach (var row in then.OrderBy(r => r.KnowerKind, StringComparer.Ordinal).ThenBy(r => r.KnowerId, StringComparer.Ordinal)
                     .ThenBy(r => r.CreatedAt, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            result[Target(targetColumn, row)!].Add(Entry(row, viaNames));
        }

        foreach (var (row, recordedIn) in walls)
        {
            var learned = Math.Max(SessionNumber(row.LearnedSessionId) ?? recordedIn, recordedIn);
            result[Target(targetColumn, row)!].Add(new KnowledgeEntry(row.KnowerKind, row.KnowerId, row.State, LearnedSession: learned));
        }

        return Frozen(result);
    }

    /// <summary>
    /// The knowledge rows a later session's change_log touched, replayed to the end of the session (cached per session):
    /// <see cref="ChangeReplay"/>'s rule (changes filed under a session numbered above it are reversed, newest first; a
    /// change filed under no session is timeless), with one addition for knowledge.
    ///
    /// <para>
    /// <b>An edit dated by the knower's own session counts as made in that session</b> (review LR03). A knower given its own
    /// session in a call with none (<c>knowers: [{who: "party", known_as: "Ilsa Brandt", session: 3}]</c>) moves the row's
    /// learned session to S3, but the batch has no session, so its changes are logged with none. Read as timeless, the
    /// row as of S2 was today's row "learned in S3", a wall (class summary of <see cref="EntriesAsOf"/>), and the party had
    /// no record of the woman it met in S1 as "the harbour widow". So an update logged with no session, in a batch that
    /// moved that row's learned session to a later session m, is replayed as made in session m: every change that batch
    /// made to the row is reversed for a read as of a session before m. A create is not: a row first recorded that way
    /// did not exist before (it is the wall FD2 keeps), and its first learned session already dates it.
    /// </para>
    /// <para>
    /// <b>A batch that only moves the learned session is a correction, not an edit</b>: the knower's session is the only
    /// way to re-date a row, and "learned in S1" corrected to "learned in S3" says the knower did not know it in S2. So the
    /// rule dates only a batch that also changed something else of the row (its state, known_as, how or note). Replayed as
    /// made in S3, the correction was reversed for every read before S3, and a party read as of S2 showed a restricted
    /// fact it had been hidden from before the rule.
    /// </para>
    /// </summary>
    private KnowledgeHistory History(int session)
    {
        if (_histories.TryGetValue(session, out var cached))
        {
            return cached;
        }

        var meta = CampaignTables.Knowledge;
        var changes = _connection.Query<(string TargetId, string Op, string? FieldPath, string? OldValue, long Number)>(
            "SELECT cl.target_id, cl.op, cl.field_path, cl.old_value, coalesce(s.number, dated.number) AS number " +
            "FROM change_log cl LEFT JOIN session s ON s.entity_id = cl.session_id " +
            "LEFT JOIN (SELECT dl.batch_id, dl.target_id, max(ds.number) AS number FROM change_log dl " +
            "JOIN session ds ON ds.entity_id = dl.new_value AND ds.campaign_id = dl.campaign_id " +
            "WHERE dl.campaign_id = @campaignId AND dl.target_table = @table AND dl.op = @update AND dl.field_path = @learned " +
            "AND dl.session_id IS NULL AND EXISTS (SELECT 1 FROM change_log ol WHERE ol.batch_id = dl.batch_id " +
            "AND ol.target_table = dl.target_table AND ol.target_id = dl.target_id AND ol.op = @update AND ol.field_path <> @learned) " +
            "GROUP BY dl.batch_id, dl.target_id) dated " +
            "ON cl.session_id IS NULL AND cl.op = @update AND dated.batch_id = cl.batch_id AND dated.target_id = cl.target_id " +
            "WHERE cl.campaign_id = @campaignId AND cl.target_table = @table AND coalesce(s.number, dated.number) > @session " +
            "ORDER BY cl.seq DESC",
            new
            {
                campaignId = Campaign.Id,
                table = meta.Name,
                update = CampaignValues.ChangeOps.Update,
                learned = "learned_session_id",
                session,
            },
            _transaction).ToList();
        var touched = changes.Select(c => c.TargetId).ToHashSet(StringComparer.Ordinal);
        var createdIn = changes.Where(c => c.Op == CampaignValues.ChangeOps.Create)
            .GroupBy(c => c.TargetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => checked((int)g.Min(c => c.Number)), StringComparer.Ordinal);
        var asOf = new Dictionary<string, KnowledgeRow?>(StringComparer.Ordinal);
        foreach (var group in changes.GroupBy(c => c.TargetId, StringComparer.Ordinal))
        {
            using var command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = $"SELECT {meta.ColumnList} FROM {meta.Name} WHERE {meta.KeyPredicate}";
            ChangeRecorder.BindKey(command, meta.ParseTargetId(group.Key));
            var then = ChangeReplay.Rewind(meta, ChangeRecorder.ReadRow(meta, command),
                group.Select(c => new ChangeReplay.ReplayChange(c.Op, c.FieldPath, c.OldValue)));
            asOf[group.Key] = then is null ? null : CampaignRows.FromValues<KnowledgeRow>(then);
        }

        var history = new KnowledgeHistory(touched, asOf, createdIn);
        _histories[session] = history;
        return history;
    }

    private List<KnowledgeRow> Rows(string targetColumn, IReadOnlyList<string> wanted)
    {
        var rows = new List<KnowledgeRow>();
        foreach (var chunk in wanted.Chunk(500))
        {
            rows.AddRange(_connection.Query<KnowledgeRow>(
                $"SELECT {KnowledgeRow.Columns} FROM knowledge WHERE campaign_id = @campaignId AND {targetColumn} IN @ids " +
                "ORDER BY knower_kind, knower_id, created_at, id",
                new { campaignId = Campaign.Id, ids = chunk }, _transaction));
        }

        return rows;
    }

    private Dictionary<string, string> ViaNames(IReadOnlyCollection<KnowledgeRow> rows)
    {
        var viaIds = rows.Select(r => r.ViaEntityId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return viaIds.Count == 0
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : _connection.Query<(string Id, string Name)>("SELECT id, name FROM entity WHERE id IN @viaIds", new { viaIds }, _transaction)
                .ToDictionary(v => v.Id, v => v.Name, StringComparer.Ordinal);
    }

    private KnowledgeEntry Entry(KnowledgeRow row, IReadOnlyDictionary<string, string> viaNames) =>
        new(row.KnowerKind,
            row.KnowerId,
            row.State,
            row.KnownAs,
            SessionNumber(row.LearnedSessionId),
            SessionNumber(row.ValidUntilSessionId),
            row.How,
            row.ViaEntityId is { } via && viaNames.TryGetValue(via, out var name) ? name : null,
            row.Note);

    private static string? Target(string targetColumn, KnowledgeRow row) => targetColumn == "fact_id" ? row.FactId : row.EntityId;

    private static IReadOnlyDictionary<string, IReadOnlyList<KnowledgeEntry>> Frozen(Dictionary<string, List<KnowledgeEntry>> result) =>
        result.ToDictionary(p => p.Key, p => (IReadOnlyList<KnowledgeEntry>)p.Value, StringComparer.Ordinal);

    /// <summary>
    /// The knowledge rows later sessions touched: their ids, each as it stood at the end of the session (null: it did not
    /// exist then), and the session each one created after it was created in.
    /// </summary>
    private sealed record KnowledgeHistory(
        IReadOnlySet<string> Touched,
        IReadOnlyDictionary<string, KnowledgeRow?> AsOf,
        IReadOnlyDictionary<string, int> CreatedIn);

    // Characters the party knows by their own names: public or party visibility, in play, and no party/public knowledge row
    // that renames or hides them. Printed as character:slug only when the slug spells that name, else e:<n>. Not in play
    // (proposed, planned, lean, struck, superseded) is hidden from every non-author read (ReadScope), so suggesting one
    // named an invention, or a struck character, to whoever typed a near miss (review L03).
    private Func<EntityRow, SuggestionView?> PartyKnownView()
    {
        var disguised = _connection.Query<string>(
            "SELECT DISTINCT entity_id FROM knowledge WHERE campaign_id = @campaignId AND entity_id IS NOT NULL " +
            "AND knower_kind IN (@party, @public) AND (known_as IS NOT NULL OR state IN (@unrecognized, @unaware, @forgot))",
            new
            {
                campaignId = Campaign.Id,
                party = CampaignValues.KnowerKinds.Party,
                @public = CampaignValues.KnowerKinds.Public,
                unrecognized = CampaignValues.KnowledgeStates.Unrecognized,
                unaware = CampaignValues.KnowledgeStates.Unaware,
                forgot = CampaignValues.KnowledgeStates.Forgot,
            },
            _transaction).ToHashSet(StringComparer.Ordinal);
        return entity =>
        {
            if (entity.Visibility is not (CampaignValues.Visibilities.Public or CampaignValues.Visibilities.Party) ||
                NotInPlayCanonStatuses.Contains(entity.CanonStatus) || disguised.Contains(entity.Id))
            {
                return null;
            }

            var handle = entity.Slug == CampaignSlugs.From(entity.Name, entity.Kind) ? entity.Handle : entity.SeqHandle;
            return new SuggestionView(entity.Name, handle);
        };
    }

    private static string SearchText(CampaignHandle handle) => handle switch
    {
        CampaignHandle.EntityBySlug bySlug => bySlug.Slug.Replace('-', ' '),
        _ => handle.Text,
    };
}

/// <summary>
/// <see cref="IAttendance"/> over session_attendance for one campaign: <see cref="AttendanceAnswer.NotRecorded"/> when the
/// session has no attendance rows at all (the common case: many campaigns never record it; the verdict then counts the
/// party row with a note), <see cref="AttendanceAnswer.NotListed"/> when others are listed but not this character (never
/// read as present: that would hand an absent character everything learned that night), else Present or Absent.
/// </summary>
public sealed class SessionAttendance : IAttendance
{
    private readonly Dictionary<int, Dictionary<string, bool>> _bySession;

    private SessionAttendance(Dictionary<int, Dictionary<string, bool>> bySession) => _bySession = bySession;

    /// <summary>Loads every attendance row of the campaign's sessions.</summary>
    public static SessionAttendance Load(SqliteConnection connection, string campaignId, SqliteTransaction? transaction = null)
    {
        CampaignDatabase.EnsureDapperConfigured();
        var bySession = new Dictionary<int, Dictionary<string, bool>>();
        foreach (var (number, characterId, present) in connection.Query<(long Number, string CharacterId, long Present)>(
                     "SELECT s.number, a.character_id, a.present FROM session_attendance a " +
                     "JOIN session s ON s.entity_id = a.session_id WHERE s.campaign_id = @campaignId",
                     new { campaignId }, transaction))
        {
            var key = checked((int)number);
            if (!bySession.TryGetValue(key, out var characters))
            {
                bySession[key] = characters = new Dictionary<string, bool>(StringComparer.Ordinal);
            }

            characters[characterId] = present != 0;
        }

        return new SessionAttendance(bySession);
    }

    public AttendanceAnswer Of(string characterId, int sessionNumber)
    {
        if (!_bySession.TryGetValue(sessionNumber, out var characters) || characters.Count == 0)
        {
            return AttendanceAnswer.NotRecorded;
        }

        return characters.TryGetValue(characterId, out var present)
            ? present ? AttendanceAnswer.Present : AttendanceAnswer.Absent
            : AttendanceAnswer.NotListed;
    }
}
