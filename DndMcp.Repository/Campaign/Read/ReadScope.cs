using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;
using K = DndMcp.Domain.Campaign.CampaignValues.Kinds;
using SS = DndMcp.Domain.Campaign.CampaignValues.SessionStatuses;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>
/// One entity as one perspective sees it at one point in time: the row (as of the session, when one was asked for), its
/// aliases, its knowledge rows, the perspective's verdict and view, and the ref to print.
/// </summary>
/// <param name="Row">The entity as of the scope's session (current when none).</param>
/// <param name="Aliases">Its aliases as of that session, every visibility (filter with <see cref="EntityViews.ShownAliases"/>).</param>
/// <param name="Entries">Every knowledge row about it as stored (author-only content: never print a via name or note from these).</param>
/// <param name="Verdict">The perspective's verdict on it.</param>
/// <param name="View">Visible / disguised / display name.</param>
/// <param name="Ref">The perspective-safe ref; null when the entity is hidden.</param>
internal sealed record EntityState(
    EntityRow Row,
    IReadOnlyList<AliasRow> Aliases,
    IReadOnlyList<KnowledgeEntry> Entries,
    KnowledgeVerdict Verdict,
    EntityView View,
    string? Ref)
{
    /// <summary>
    /// The knowledge rows every verdict on it is judged with: <see cref="Entries"/>, or in a read as of a session those rows
    /// as they stood then (<see cref="KnowledgeLoader.EntriesForEntitiesAsOf"/>).
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> RowsForVerdicts { get; init; } = Entries;

    public bool Visible => View.Visible;

    /// <summary>Visible and not disguised: the full detail (summary, body, aliases, tags, relations, status) may be shown.</summary>
    public bool Shown => View.Visible && !View.Disguised;

    public string Name => View.DisplayName;

    /// <summary>The link other results print for it (only call on a visible entity).</summary>
    public EntityLink Link => new(Ref ?? throw new InvalidOperationException("A hidden entity has no link."), Row.Kind, View.DisplayName);
}

/// <summary>One fact as one perspective sees it: visible or not, and the text it may print.</summary>
/// <param name="Row">The fact as of the scope's session.</param>
/// <param name="Entries">Every knowledge row about it as stored.</param>
/// <param name="Verdict">The perspective's verdict.</param>
/// <param name="Visible">The perspective may see it (<see cref="ReadScope.Shows"/>: contract §3.2, plus the not-in-play rule).</param>
/// <param name="Text">What to print: the statement for the author; the deciding row's known_as, else the statement, otherwise.</param>
internal sealed record FactState(FactRow Row, IReadOnlyList<KnowledgeEntry> Entries, KnowledgeVerdict Verdict, bool Visible, string Text)
{
    /// <summary>
    /// The knowledge rows every verdict on it is judged with: <see cref="Entries"/>, or in a read as of a session those rows
    /// as they stood then (<see cref="KnowledgeLoader.EntriesForFactsAsOf"/>).
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> RowsForVerdicts { get; init; } = Entries;

    public string Ref => Row.SeqHandle;
}

/// <summary>
/// How a read as of a session names its session argument and the way back to today's read, in its refusals (review CR01):
/// every caller's own words. campaign_get and the ledger take <c>as_of_session</c>, and leaving it out reads today;
/// campaign_history's as_of takes <c>session</c> (it has no as_of_session, so "leave out as_of_session" was refused as an
/// unknown argument, and leaving out session as a missing one), and today's read is campaign_get's.
/// </summary>
/// <param name="Argument">The argument that holds the session ("as_of_session is -1; …").</param>
/// <param name="ReadNow">The sentence that says how to read the entry as it is now.</param>
internal sealed record AsOfWording(string Argument, string ReadNow)
{
    /// <summary>campaign_get, campaign_search and the ledger: <c>as_of_session</c>, left out to read today.</summary>
    public static AsOfWording AsOfSession { get; } = new("as_of_session", "Leave out as_of_session to read it as it is now.");

    /// <summary>campaign_history as_of: <c>session</c>; today's read is campaign_get's.</summary>
    public static AsOfWording History { get; } = new("session", "Read it as it is now with campaign_get, or give a later session.");
}

/// <summary>
/// The perspective filter of one read (contract §3.2), over one connection: resolves the perspective, loads rows as of
/// the requested session, and decides for each entity and fact whether it is visible, disguised, what it is called and
/// which ref is printed. Every reader goes through this one class, so search, get, the ledger, summaries and sessions can
/// never disagree about what a perspective sees.
///
/// <para>
/// <b>The rules</b>: an entity or fact is visible to a non-author view when its visibility is not <c>author</c> and the
/// verdict (<see cref="KnowledgeVerdicts"/>) is Knows; an entity is disguised when the perspective knows it only under
/// another name or does not recognise it (<see cref="EntityViews"/>); refs are <c>kind:slug</c> only when the slug spells
/// nothing the perspective does not already see. On top of the contract, a non-author view never sees a row whose
/// canon status is not in play (<see cref="KnowledgeLoader.NotInPlayCanonStatuses"/>: proposed, planned, lean, struck,
/// superseded): a planned fact that is party-visible ("the axe is assembled") would otherwise be shown to the party as
/// something they know before it has happened, and a struck invention as canon. For the same reason a session entity is
/// seen outside the author view only once it is played or live (<see cref="HiddenOutsideAuthorView"/>): a planned
/// session's title ("The betrayal at the lighthouse") is prep, and sessions are party-visible by default, so a reader
/// that judged sessions by visibility alone would print the author's plans in a listing, a search hit, a get or another
/// entity's session links. The rule lives here, not in the session reader, so every reader applies it.
/// </para>
/// <para>
/// <b>Author visibility is absolute</b> (contract §3.2), for every non-author view and every reader, the author-facing
/// check and ledger included: knowledge rows never make an author-only fact or entity known outside the author view.
/// <see cref="Shows"/> is the one test of "this view knows this fact" (the scope's own reads, the check's speaker and
/// audience), and <see cref="RowsClaimAuthorOnly"/> names the case where rows say otherwise (the check's and the ledger's
/// "author only").
/// </para>
/// <para>
/// <b>Every verdict is dated</b> (review L02): the target's session (an entity's introduced session or, when none is
/// recorded, the session it was written in; a session entity's own number; a fact's established session) goes to
/// <see cref="KnowledgeVerdicts.Evaluate"/>, which reads a party-visible
/// target like a party row learned in that session: a member absent from it, not listed, joined later or gone by then is
/// uncertain, and does not see it. Sessions are party-visible with no knowledge rows, so without the date a member who
/// missed session 1 read its recap of Tristan's death, and one who had left read every session, place and fact after.
/// </para>
/// <para>
/// <b>Point in time</b> (<see cref="AsOf"/>): entity and fact rows (visibility, name, canon status, deletion), aliases,
/// tags, session statuses and the knowledge rows the verdicts read (<see cref="EntityState.RowsForVerdicts"/>: state,
/// known_as and sessions as they stood then; a row recorded later stops the visibility default, see
/// <see cref="KnowledgeLoader"/>) are replayed with <see cref="ChangeReplay"/>, and the verdict compares the rows'
/// learned / valid-until sessions with the session asked for. Tags matter as much as names: an as_of read of an entity that was visible then and
/// was tagged "crown-spy" when it was made author-only later must print the tags it had then, not today's. Visibility
/// must be the replayed one: a fact that became party-visible in session 2 was not known to the table as of session 1
/// (Belmakor row 17), and a current visibility would say it was. And a fact established in a session after the one asked
/// for had not happened at the table yet, so its visibility default grants no knowledge then (<see cref="VerdictVisibility"/>),
/// nor does a group's row that records no session (<see cref="VerdictEntries"/>): only a character's own row that applied
/// then (backstory), or a row whose learned session is at or before the read, counts. The author view still shows such a
/// fact (the author sees every row that existed then, including one recorded ahead of the session it is set in); its
/// author detail carries the established session (campaign_get prints "established S3" under an as_of 2 read), which is
/// what marks it as later than the read.
/// </para>
/// <para>
/// <b>Handles typed by a non-author view</b> resolve only when they are what that view would be shown: <c>e:&lt;n&gt;</c>
/// of a visible entity, or <c>kind:slug</c> / a code of a visible, non-disguised entity whose printed ref is that slug.
/// Anything else fails exactly like a handle that names nothing, and suggestions match only names the view knows.
/// Otherwise typing <c>character:protector</c> as the party and getting back "the advisor in Serret" would confirm the
/// secret the disguise exists to keep.
/// </para>
/// </summary>
internal sealed class ReadScope
{
    private readonly Dictionary<string, EntityState?> _entities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FactState?> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _tags = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _sessionStatuses;
    private PerspectiveContext? _party;
    private bool _allEntitiesLoaded;

    private ReadScope(SqliteConnection connection, CampaignRow campaign, KnowledgeLoader loader, PerspectiveContext who, int? asOf, AsOfWording wording)
    {
        Connection = connection;
        Campaign = campaign;
        Loader = loader;
        Who = who;
        AsOf = asOf;
        Wording = wording;
    }

    public SqliteConnection Connection { get; }

    public CampaignRow Campaign { get; }

    public KnowledgeLoader Loader { get; }

    public PerspectiveContext Who { get; }

    /// <summary>The session number the read is as of; null for now.</summary>
    public int? AsOf { get; }

    /// <summary>How the caller's own arguments name the session and the way back to today's read, for refusals.</summary>
    public AsOfWording Wording { get; }

    public bool IsAuthorView => Who.IsAuthorView;

    /// <summary>The party's context (for gates, audiences and secret status, which are about what the party knows).</summary>
    public PerspectiveContext Party => _party ??= PerspectiveContext.For(Perspective.Parse(CampaignValues.PerspectiveKinds.Party), Campaign.Role);

    /// <summary>
    /// Opens the filter for <paramref name="perspective"/> (resolving a character perspective as it stood at that session,
    /// <see cref="KnowledgeLoader.Resolve"/>) as of a session. <paramref name="wording"/> names the caller's arguments in
    /// its refusals (default: campaign_get's <c>as_of_session</c>).
    /// </summary>
    /// <exception cref="DndInputException">An unknown character, or an as_of_session out of range.</exception>
    public static ReadScope Open(SqliteConnection connection, CampaignRow campaign, Perspective? perspective, int? asOfSession, AsOfWording? wording = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        wording ??= AsOfWording.AsOfSession;
        CheckSession(asOfSession, wording.Argument);
        var loader = new KnowledgeLoader(connection, campaign);
        var who = loader.Resolve(perspective ?? Perspective.Author, asOfSession);
        return new ReadScope(connection, campaign, loader, who, asOfSession, wording);
    }

    /// <summary>Refuses a session number below 0 or above the largest a session may have.</summary>
    public static void CheckSession(int? session, string name)
    {
        if (session is < 0 or > CampaignLimits.MaxSessionNumber)
        {
            throw new DndInputException(
                $"{name} is {session!.Value.ToString(CultureInfo.InvariantCulture)}; give a session number from 0 to " +
                $"{CampaignLimits.MaxSessionNumber.ToString(CultureInfo.InvariantCulture)}, e.g. 3.");
        }
    }

    /// <summary>The state of an entity of this campaign (loaded on first use); null when it did not exist then, or is deleted.</summary>
    public EntityState? Entity(string? id)
    {
        if (id is null)
        {
            return null;
        }

        LoadEntities([id]);
        return _entities[id];
    }

    /// <summary>The state of an entity when the perspective may see it; null otherwise.</summary>
    public EntityState? VisibleEntity(string? id) => Entity(id) is { Visible: true } state ? state : null;

    /// <summary>Loads the states of many entities in a few queries (call before iterating over them).</summary>
    public void LoadEntities(IEnumerable<string> ids)
    {
        var wanted = ids.Where(id => !_entities.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var rows = new Dictionary<string, EntityRow>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(400))
        {
            foreach (var row in Connection.Query<EntityRow>(
                         $"SELECT {EntityRow.Columns} FROM entity WHERE campaign_id = @campaignId AND id IN @ids",
                         new { campaignId = Campaign.Id, ids = chunk }))
            {
                rows[row.Id] = row;
            }
        }

        if (AsOf is { } session)
        {
            rows = ChangeReplay.RowsAsOf(Connection, CampaignTables.Entity.Name, rows.Keys, session)
                .Where(p => p.Value is not null)
                .Select(p => CampaignRows.FromValues<EntityRow>(p.Value!))
                .ToDictionary(r => r.Id, StringComparer.Ordinal);
        }

        var live = rows.Values.Where(r => r.DeletedAt is null && r.CampaignId == Campaign.Id).ToList();
        var liveIds = live.Select(r => r.Id).ToList();
        Loader.LoadCreationSessions(live.Where(r => r.IntroducedSessionId is null && r.Kind != K.Session).Select(r => r.Id));
        var aliases = Aliases(liveIds);
        var entries = Loader.EntriesForEntities(liveIds);
        var verdictRows = AsOf is { } asOf ? Loader.EntriesForEntitiesAsOf(liveIds, asOf) : entries;
        foreach (var id in wanted)
        {
            _entities[id] = null;
        }

        foreach (var row in live)
        {
            _entities[row.Id] = Compute(row, aliases.GetValueOrDefault(row.Id) ?? [], entries[row.Id], verdictRows[row.Id]);
        }
    }

    /// <summary>Loads every entity of the campaign (for listings and suggestions); returns their ids.</summary>
    public IReadOnlyList<string> LoadAllEntities()
    {
        var ids = Connection.Query<string>(
            "SELECT id FROM entity WHERE campaign_id = @campaignId" + (AsOf is null ? " AND deleted_at IS NULL" : string.Empty) +
            " ORDER BY seq", new { campaignId = Campaign.Id }).ToList();
        if (!_allEntitiesLoaded)
        {
            LoadEntities(ids);
            _allEntitiesLoaded = true;
        }

        return ids;
    }

    /// <summary>The state of a fact of this campaign; null when it did not exist then, or is deleted.</summary>
    public FactState? Fact(string? id)
    {
        if (id is null)
        {
            return null;
        }

        LoadFacts([id]);
        return _facts[id];
    }

    /// <summary>Loads the states of many facts in a few queries.</summary>
    public void LoadFacts(IEnumerable<string> ids)
    {
        var wanted = ids.Where(id => !_facts.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var rows = new Dictionary<string, FactRow>(StringComparer.Ordinal);
        foreach (var chunk in wanted.Chunk(400))
        {
            foreach (var row in Connection.Query<FactRow>(
                         $"SELECT {FactRow.Columns} FROM fact WHERE campaign_id = @campaignId AND id IN @ids",
                         new { campaignId = Campaign.Id, ids = chunk }))
            {
                rows[row.Id] = row;
            }
        }

        if (AsOf is { } session)
        {
            rows = ChangeReplay.RowsAsOf(Connection, CampaignTables.Fact.Name, rows.Keys, session)
                .Where(p => p.Value is not null)
                .Select(p => CampaignRows.FromValues<FactRow>(p.Value!))
                .ToDictionary(r => r.Id, StringComparer.Ordinal);
        }

        var live = rows.Values.Where(r => r.DeletedAt is null && r.CampaignId == Campaign.Id).ToList();
        var liveIds = live.Select(r => r.Id).ToList();
        var entries = Loader.EntriesForFacts(liveIds);
        var verdictRows = AsOf is { } asOf ? Loader.EntriesForFactsAsOf(liveIds, asOf) : entries;
        foreach (var id in wanted)
        {
            _facts[id] = null;
        }

        foreach (var row in live)
        {
            var judged = verdictRows[row.Id];
            var verdict = FactVerdict(Who, row, judged);
            var visible = Shows(Who, row, verdict);
            var text = IsAuthorView || string.IsNullOrWhiteSpace(verdict.KnownAs) ? row.Statement : verdict.KnownAs.Trim();
            _facts[row.Id] = new FactState(row, entries[row.Id], verdict, visible, text) { RowsForVerdicts = judged };
        }
    }

    /// <summary>The verdict of another perspective (the party, an audience) on an entity, at the scope's point in time.</summary>
    public KnowledgeVerdict VerdictFor(PerspectiveContext other, EntityState entity) =>
        KnowledgeVerdicts.Evaluate(other, entity.RowsForVerdicts, entity.Row.Visibility, Loader.Attendance, AsOf, SinceSession(entity.Row));

    /// <summary>
    /// The session an entity entered the party's knowledge in by its visibility, for the dated party default (class
    /// summary): a session entity's own number, else its introduced session, else the session it was written in
    /// (<see cref="KnowledgeLoader.IntroducedSession"/>); null when it has none (backstory, prep between sessions).
    /// </summary>
    public int? SinceSession(EntityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Kind == K.Session ? Loader.SessionNumber(row.Id) : Loader.IntroducedSession(row);
    }

    /// <summary>
    /// The verdict of another perspective on a fact, at the scope's point in time (with <see cref="VerdictEntries"/> and
    /// <see cref="VerdictVisibility"/>, as the scope's own verdicts: the check's speaker and audience, the ledger's columns
    /// and the gates' "the party knows" must agree with each view's reads).
    /// </summary>
    public KnowledgeVerdict VerdictFor(PerspectiveContext other, FactState fact) => FactVerdict(other, fact.Row, fact.RowsForVerdicts);

    /// <summary>
    /// Whether <paramref name="who"/>'s reads show a fact, given its verdict at the scope's point in time: always for the
    /// author view; for any other view only when the fact is not author-only (absolute, whatever its rows say), the verdict
    /// is Knows, and its canon status is in play. The scope's own visibility, the knowledge check's "the speaker knows it"
    /// (its related facts and mistaken beliefs) and "the audience knows it" (its secrets at risk) are this one test, so a
    /// secret at risk can never be a fact the speaker's own reads hide (an author-only fact a stray row says the speaker
    /// knows) or one the audience's reads already show.
    /// </summary>
    public static bool Shows(PerspectiveContext who, FactRow row, KnowledgeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(verdict);
        return who.IsAuthorView || (row.Visibility != V.Author && verdict.Knows && !NotInPlay(row.CanonStatus));
    }

    /// <summary>
    /// True when <paramref name="who"/> is not the author view, the row is author-only, and yet its verdict says Knows or
    /// Uncertain (a knowledge row claims it, or claims it may be known: a party row with a member absent, unlisted or not
    /// yet joined): the case the author-facing check and ledger report as "author only" instead of the verdict. Author
    /// visibility is absolute (contract §3.2), so printing "knows" or "uncertain: … absent" there would tell the author a
    /// player-side view knows, or may know, what none of its reads will ever show; a verdict that already says the view does
    /// not know (unaware, no record) is left as the record has it (Belmakor rows 1, 5 and 8).
    /// </summary>
    public static bool RowsClaimAuthorOnly(PerspectiveContext who, string visibility, KnowledgeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(verdict);
        return !who.IsAuthorView && visibility == V.Author && verdict.Standing is KnowledgeStanding.Knows or KnowledgeStanding.Uncertain;
    }

    /// <summary>
    /// The visibility a fact's verdicts are judged with at the scope's point in time: its own (replayed) visibility, except
    /// that a public or party fact established in a session after the one the read is as of counts as restricted, known
    /// only to the knowers whose rows applied then (<see cref="VerdictEntries"/> says which rows those are). Visibility says
    /// who knows a fact once it is part of the story; before the session it was established in, it was not, and nobody
    /// learned it by being there. Judged by its party visibility, the errand the old king gave in session 3 was "known" to
    /// the party as of session 2, and a party search as of 2 found it.
    /// </summary>
    public string VerdictVisibility(FactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return EstablishedAfterAsOf(row) is not null && row.Visibility is (V.Public or V.Party) ? V.Restricted : row.Visibility;
    }

    /// <summary>
    /// The knowledge rows a fact's verdicts are judged with at the scope's point in time: its rows as stored, except on a
    /// fact established after the session the read is as of (<see cref="EstablishedAfterAsOf"/>), where a group knower's
    /// row (party, table, public, dm) that records no learned session counts from the established session, so it does not
    /// apply yet. A batch with no session (prep or world-building between sessions, and any campaign_write or
    /// campaign_knowledge call made while no session is live) writes rows with no learned session; on a fact the story
    /// establishes in a later session such a row says only that the group knows it now. Nothing at the table can have
    /// taught a group a fact before the session it happened in, and read as timeless the row showed the party, as of
    /// session 2, the bridge that fell in session 3 (a fact op with established_session 3 and known_by party: the stage-3a
    /// defect, reached through the tools). Kept as stored: a character's own row with no session, which is how backstory
    /// is recorded (Belmakor knew his own Contingency, established in session 2, before it fired: Belmakor row 19; a
    /// character can know what the table has not seen yet), and any row with a learned session, which says when the knower
    /// learned it, even before the story established it.
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> VerdictEntries(FactRow row, IReadOnlyList<KnowledgeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(entries);
        if (EstablishedAfterAsOf(row) is not { } established)
        {
            return entries;
        }

        return entries
            .Select(e => e.KnowerKind != CampaignValues.KnowerKinds.Character && e.LearnedSession is null ? e with { LearnedSession = established } : e)
            .ToList();
    }

    /// <summary>
    /// The number of the session a fact was established in, when that session comes after the one the read is as of (the
    /// fact was not in play then, contract §3.4); null otherwise, and always null for a read of now.
    /// </summary>
    public int? EstablishedAfterAsOf(FactRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return AsOf is { } asOf && Loader.EstablishedSession(row) is { } established && established > asOf ? established : null;
    }

    // Every fact verdict of the scope goes through here, so its own reads and VerdictFor can never judge one fact apart. A
    // fact entered the party's knowledge by its visibility in the session it was established in (class summary).
    private KnowledgeVerdict FactVerdict(PerspectiveContext who, FactRow row, IReadOnlyList<KnowledgeEntry> entries) =>
        KnowledgeVerdicts.Evaluate(who, VerdictEntries(row, entries), VerdictVisibility(row), Loader.Attendance, AsOf,
            Loader.EstablishedSession(row));

    /// <summary>
    /// The view another perspective has of an entity (the knowledge check classifies names for a speaker and an audience
    /// that may differ from the scope's own perspective), with the same not-in-play rule as the scope's own views.
    /// </summary>
    public EntityView ViewFor(PerspectiveContext other, EntityState entity)
    {
        var view = EntityViews.For(other, entity.Row.Kind, entity.Row.Name, entity.Row.Visibility,
            entity.Aliases.Select(a => (a.Alias, a.Visibility)).ToList(), VerdictFor(other, entity));
        return view.Visible && !other.IsAuthorView && HiddenOutsideAuthorView(entity.Row)
            ? EntityView.Hidden
            : view;
    }

    /// <summary>Whether a canon status is one under which a row is not in play (not yet, or no longer, part of the story).</summary>
    public static bool NotInPlay(string canonStatus) => KnowledgeLoader.NotInPlayCanonStatuses.Contains(canonStatus);

    /// <summary>
    /// Whether an entity is out of every non-author view whatever its visibility and knowledge rows say: its canon status
    /// is not in play, or it is a session that is not played or live (as of the scope's session). The class summary says
    /// why; every view of an entity (the scope's own, <see cref="ViewFor"/>, the ledger's cells) goes through this.
    /// </summary>
    public bool HiddenOutsideAuthorView(EntityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return NotInPlay(row.CanonStatus) || (row.Kind == K.Session && SessionStatus(row.Id) is not (SS.Played or SS.Live));
    }

    /// <summary>
    /// Why an entity is out of every non-author view (<see cref="HiddenOutsideAuthorView"/>): its canon status, or its
    /// session status; null when it is in play. Author-facing wording only (the ledger's "not in play (planned)").
    /// </summary>
    public string? NotInPlayReason(EntityRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (NotInPlay(row.CanonStatus))
        {
            return row.CanonStatus;
        }

        return row.Kind == K.Session && SessionStatus(row.Id) is var status && status is not (SS.Played or SS.Live)
            ? status ?? SS.Planned
            : null;
    }

    /// <summary>
    /// A session entity's status as of the scope's session (the session row replayed, since "played" is written in the
    /// session it was played in); null when it has no session row then.
    /// </summary>
    public string? SessionStatus(string sessionEntityId)
    {
        if (_sessionStatuses is null)
        {
            var rows = Connection.Query<(string EntityId, string Status)>(
                "SELECT entity_id, status FROM session WHERE campaign_id = @campaignId", new { campaignId = Campaign.Id }).ToList();
            _sessionStatuses = rows.ToDictionary(r => r.EntityId, r => r.Status, StringComparer.Ordinal);
            if (AsOf is { } session)
            {
                // Rows deleted since are not in the table; a session is an entity, so its entity replay decides whether it
                // existed then, and a missing row here only means "no status" (hidden outside the author view).
                _sessionStatuses = ChangeReplay.RowsAsOf(Connection, CampaignTables.Session.Name, rows.Select(r => r.EntityId), session)
                    .Where(p => p.Value?.GetValueOrDefault("status") is string)
                    .ToDictionary(p => p.Key, p => (string)p.Value!["status"]!, StringComparer.Ordinal);
            }
        }

        return _sessionStatuses.GetValueOrDefault(sessionEntityId);
    }

    /// <summary>
    /// The status to print: a question's <c>withheld</c> and <c>lean</c> are <c>open</c> to a non-author view (either one
    /// would tell the players the author has an answer or a direction), and a disguised entity has no status at all.
    /// </summary>
    public string? DisplayStatus(EntityState entity)
    {
        if (IsAuthorView)
        {
            return entity.Row.Status;
        }

        if (!entity.Shown)
        {
            return null;
        }

        return entity.Row.Kind == K.Question &&
               entity.Row.Status is CampaignValues.Statuses.QuestionWithheld or ReadStatuses.QuestionLean
            ? CampaignValues.Statuses.QuestionOpen
            : entity.Row.Status;
    }

    /// <summary>
    /// A knowledge state as a non-author view may print it: <c>unrecognized</c> is <c>met</c> and <c>misbelieves</c> is
    /// <c>believes</c>. Both true states are the author's judgement about the knower ("met without knowing who", "holds
    /// it and is wrong"); printed to that knower's own view they would say there is a hidden identity or that the belief
    /// is false, the same trace a <c>withheld</c> status leaves. The author view gets the state as stored.
    /// </summary>
    public string? ShownState(string? state) =>
        IsAuthorView ? state : state switch
        {
            CampaignValues.KnowledgeStates.Unrecognized => CampaignValues.KnowledgeStates.Met,
            CampaignValues.KnowledgeStates.Misbelieves => CampaignValues.KnowledgeStates.Believes,
            _ => state,
        };

    /// <summary>A verdict's explanation with its state word shown as <see cref="ShownState"/> prints it.</summary>
    public string ShownExplanation(KnowledgeVerdict verdict)
    {
        if (IsAuthorView || verdict.State is null || ShownState(verdict.State) == verdict.State)
        {
            return verdict.Explanation;
        }

        return verdict.Explanation.Replace(": " + verdict.State, ": " + ShownState(verdict.State), StringComparison.Ordinal);
    }

    /// <summary>
    /// The aliases a view may print (<see cref="EntityViews.ShownAliases"/>): all of them for the author; otherwise the
    /// public ones and, for a view that sees party rows (the party, the table, the dm, current members), the party ones; of
    /// a disguised entity only the party aliases it shares with that view. A party alias printed to the public or to a
    /// character outside the party was a name they do not know (review L11).
    /// </summary>
    public IReadOnlyList<(string Alias, string Visibility)> ShownAliases(EntityState entity) =>
        EntityViews.ShownAliases(entity.View, entity.Aliases.Select(a => (a.Alias, a.Visibility)).ToList());

    /// <summary>Tag names of an entity as of the scope's session.</summary>
    public IReadOnlyList<string> Tags(string entityId)
    {
        if (!_tags.TryGetValue(entityId, out var tags))
        {
            LoadTags([entityId]);
            tags = _tags[entityId];
        }

        return tags;
    }

    /// <summary>
    /// Loads tag names for many entities. As of a session both the links (entity_tag) and the tag rows (a renamed tag) are
    /// replayed: tags are loggable rows like aliases, and today's tags on an as_of read would print a tag added when the
    /// entity was made author-only ("crown-spy") in a view of the time when the party could see it.
    /// </summary>
    public void LoadTags(IEnumerable<string> entityIds)
    {
        var wanted = entityIds.Where(id => !_tags.ContainsKey(id)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var id in wanted)
        {
            _tags[id] = [];
        }

        var links = new List<EntityTagRow>();
        foreach (var chunk in wanted.Chunk(400))
        {
            links.AddRange(Connection.Query<EntityTagRow>(
                $"SELECT {EntityTagRow.Columns} FROM entity_tag WHERE entity_id IN @ids", new { ids = chunk }));
        }

        Dictionary<string, string> names;
        if (AsOf is { } session)
        {
            var table = CampaignTables.EntityTag;
            var asked = wanted.ToHashSet(StringComparer.Ordinal);
            links = AsOfRows.ForEntities<EntityTagRow>(Connection, table, links.Select(l => table.TargetId([l.EntityId, l.TagId])), wanted, session)
                .Where(l => asked.Contains(l.EntityId))
                .ToList();
            names = ChangeReplay.RowsAsOf(Connection, CampaignTables.Tag.Name, links.Select(l => l.TagId), session)
                .Where(p => p.Value?.GetValueOrDefault("name") is string)
                .ToDictionary(p => p.Key, p => (string)p.Value!["name"]!, StringComparer.Ordinal);
        }
        else
        {
            names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var chunk in links.Select(l => l.TagId).Distinct(StringComparer.Ordinal).Chunk(400))
            {
                foreach (var (id, name) in Connection.Query<(string Id, string Name)>("SELECT id, name FROM tag WHERE id IN @ids", new { ids = chunk }))
                {
                    names[id] = name;
                }
            }
        }

        foreach (var group in links.Where(l => names.ContainsKey(l.TagId)).GroupBy(l => l.EntityId, StringComparer.Ordinal))
        {
            _tags[group.Key] = group.Select(l => names[l.TagId]).Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>The session number of a session entity id, or null.</summary>
    public int? SessionNumber(string? sessionEntityId) => Loader.SessionNumber(sessionEntityId);

    /// <summary>
    /// The author's ref of any entity id, even a deleted one or one of another campaign (<c>one-piece/character:keras</c>);
    /// for author-facing output only (history, the knowledge check, the ledger).
    /// </summary>
    public string AuthorRef(string? entityId)
    {
        if (entityId is null)
        {
            return "(none)";
        }

        var row = Connection.QueryFirstOrDefault<(string? CampaignId, string? Kind, string? Slug, string? Slugged)>(
            "SELECT e.campaign_id, e.kind, e.slug, c.slug FROM entity e JOIN campaign c ON c.id = e.campaign_id WHERE e.id = @entityId",
            new { entityId });
        if (row.Kind is null)
        {
            return "(deleted entity)";
        }

        var handle = row.Kind == K.Session && Loader.SessionNumber(entityId) is { } number
            ? "session:" + number.ToString(CultureInfo.InvariantCulture)
            : row.Kind + ":" + row.Slug;
        return row.CampaignId == Campaign.Id ? handle : row.Slugged + "/" + handle;
    }

    /// <summary>
    /// The entity a handle names, as this perspective may address it (class summary); null when there is none, it is
    /// hidden, or the handle spells what the perspective must not see.
    /// </summary>
    public EntityState? ResolveEntity(CampaignHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle is CampaignHandle.CrossCampaign or CampaignHandle.FactBySeq)
        {
            return null;
        }

        var resolver = new HandleResolver(Connection, Campaign.Id);
        var row = resolver.TryEntity(handle, includeDeleted: AsOf is not null);
        if (row is null || VisibleEntity(row.Id) is not { } state)
        {
            return null;
        }

        if (IsAuthorView)
        {
            return state;
        }

        var addressable = handle switch
        {
            CampaignHandle.EntityBySeq => true,
            CampaignHandle.SessionByNumber or CampaignHandle.SessionLive or CampaignHandle.SessionLast => state.Shown,
            CampaignHandle.ByCode code when row.Code == code.Code => state.Shown,
            CampaignHandle.ByCode => state.Shown && state.Ref == row.Kind + ":" + row.Slug,
            CampaignHandle.EntityBySlug => state.Shown && state.Ref == row.Kind + ":" + row.Slug,
            _ => false,
        };
        return addressable ? state : null;
    }

    /// <summary>A fact a handle names (<c>f:n</c> or a fact code), when the perspective may see it; null otherwise.</summary>
    public FactState? ResolveFact(CampaignHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var row = new HandleResolver(Connection, Campaign.Id).TryFact(handle, includeDeleted: AsOf is not null);
        return row is not null && Fact(row.Id) is { Visible: true } state ? state : null;
    }

    /// <summary>
    /// A handle that may name an entity or a fact. A code that names both is refused for the author (naming both
    /// <c>e:</c>/<c>f:</c> handles); for any other view only what it may see counts, so the refusal never reveals a hidden row.
    /// </summary>
    /// <exception cref="DndInputException">The author typed a code that names both an entity and a fact.</exception>
    public (EntityState? Entity, FactState? Fact) ResolveItem(CampaignHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return handle switch
        {
            CampaignHandle.FactBySeq => (null, ResolveFact(handle)),
            CampaignHandle.ByCode code => ResolveCode(code),
            _ => (ResolveEntity(handle), null),
        };
    }

    /// <summary>
    /// The "not found" refusal for a handle, identical whether nothing has that handle or the perspective may not see it,
    /// with suggestions drawn only from names this perspective knows.
    /// </summary>
    public string NotFoundProblem(string handleText, string? kind)
    {
        var typed = handleText.Length <= 60 ? handleText : handleText[..60] + "…";
        var suggestions = Suggest(kind, SuggestText(handleText));
        var hint = suggestions.Count == 0
            ? " campaign_search finds entities and facts by name."
            : $" Did you mean {string.Join(", ", suggestions)}?";
        return IsAuthorView
            ? $"\"{typed}\": nothing in this campaign has that handle.{hint}"
            : $"\"{typed}\": nothing by that handle for this perspective.{hint}";
    }

    /// <summary>
    /// The refusal of an author's read as of a session for an entry that exists now but was made after that session
    /// (review C08): "did not exist as of S1: it was made in S2", where the plain not-found ("nothing in this campaign has
    /// that handle. Did you mean character:bram?") was false, and suggested the very handle typed. Null when the handle
    /// names nothing made later, and always outside the author view: there a hidden entry and a missing one must read
    /// alike, and "made later" would confirm that a hidden one exists. The way back to today's read is the caller's own
    /// (<see cref="Wording"/>, review CR01).
    /// </summary>
    public string? MadeLaterProblem(CampaignHandle handle, string typed)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!IsAuthorView || AsOf is not { } session || handle is CampaignHandle.CrossCampaign)
        {
            return null;
        }

        var resolver = new HandleResolver(Connection, Campaign.Id);
        var candidates = new List<(string Table, string Id)>();
        if (handle is not CampaignHandle.FactBySeq && resolver.TryEntity(handle, includeDeleted: true) is { } entity && entity.CampaignId == Campaign.Id)
        {
            candidates.Add((CampaignTables.Entity.Name, entity.Id));
        }

        if ((handle is CampaignHandle.FactBySeq or CampaignHandle.ByCode) && resolver.TryFact(handle, includeDeleted: true) is { } fact)
        {
            candidates.Add((CampaignTables.Fact.Name, fact.Id));
        }

        foreach (var (table, id) in candidates)
        {
            if (ChangeReplay.RowAsOf(Connection, table, id, session) is not null)
            {
                continue;
            }

            var made = Connection.QueryFirstOrDefault<long?>(
                "SELECT min(s.number) FROM change_log cl JOIN session s ON s.entity_id = cl.session_id " +
                "WHERE cl.target_table = @table AND cl.target_id = @id AND cl.op = @create AND s.number > @session",
                new { table, id, create = CampaignValues.ChangeOps.Create, session });
            var shown = typed.Length <= 60 ? typed : typed[..60] + "…";
            var when = made is { } n ? $": it was made in S{n.ToString(CultureInfo.InvariantCulture)}" : ": it was made in a later session";
            return $"\"{shown}\" did not exist as of S{session.ToString(CultureInfo.InvariantCulture)}{when}. " + Wording.ReadNow;
        }

        return null;
    }

    /// <summary>Up to five handles close to the text, among entities this perspective may see, by the name it knows.</summary>
    public IReadOnlyList<string> Suggest(string? kind, string text)
    {
        var resolver = new HandleResolver(Connection, Campaign.Id);
        if (IsAuthorView)
        {
            return resolver.Suggest(kind, text, (Func<EntityRow, bool>)(_ => true));
        }

        LoadAllEntities();
        return resolver.Suggest(kind, text, e =>
            _entities.GetValueOrDefault(e.Id) is { Visible: true } s ? new SuggestionView(s.Name, s.Ref!) : null);
    }

    private (EntityState? Entity, FactState? Fact) ResolveCode(CampaignHandle.ByCode code)
    {
        var entity = ResolveEntity(code);
        var factRow = new HandleResolver(Connection, Campaign.Id).TryFact(code, includeDeleted: AsOf is not null);
        var fact = factRow is not null && Fact(factRow.Id) is { Visible: true } f ? f : null;
        if (entity is not null && fact is not null)
        {
            throw new DndInputException(
                $"{code.Code} names both {entity.Ref} and {fact.Ref}; use one of those handles.");
        }

        return (entity, fact);
    }

    private static string SuggestText(string handleText)
    {
        var text = handleText.Trim();
        var colon = text.LastIndexOf(':');
        return (colon >= 0 ? text[(colon + 1)..] : text).Replace('-', ' ');
    }

    private EntityState Compute(EntityRow row, IReadOnlyList<AliasRow> aliases, IReadOnlyList<KnowledgeEntry> entries,
        IReadOnlyList<KnowledgeEntry> verdictRows)
    {
        var verdict = KnowledgeVerdicts.Evaluate(Who, verdictRows, row.Visibility, Loader.Attendance, AsOf, SinceSession(row));
        var view = EntityViews.For(Who, row.Kind, row.Name, row.Visibility, aliases.Select(a => (a.Alias, a.Visibility)).ToList(), verdict);
        if (view.Visible && !IsAuthorView && HiddenOutsideAuthorView(row))
        {
            view = EntityView.Hidden;
        }

        return new EntityState(row, aliases, entries, verdict, view, view.Visible ? RefOf(row, view, aliases) : null)
        {
            RowsForVerdicts = verdictRows,
        };
    }

    // Sessions print as session:<n> (the handle form tools take), unless disguised; everything else per EntityViews.Ref,
    // widened for a shown entity to a slug that spells one of its shown aliases or its code: the contract's test
    // (the slug derived from the display name) is one case of its rule "the slug spells nothing the perspective does not
    // already see", and a party alias ("Belmakor" for character:belmakor) or a shown code ("Q7" for question:q7) is
    // seen. A disguised entity shows neither, so it keeps e:<n>.
    private string RefOf(EntityRow row, EntityView view, IReadOnlyList<AliasRow> aliases)
    {
        if (row.Kind == K.Session && !view.Disguised && Loader.SessionNumber(row.Id) is { } number)
        {
            return "session:" + number.ToString(CultureInfo.InvariantCulture);
        }

        var reference = EntityViews.Ref(view, row.Kind, row.Slug, row.Seq);
        if (view.AuthorView || view.Disguised || !reference.StartsWith("e:", StringComparison.Ordinal))
        {
            return reference;
        }

        var seen = EntityViews.ShownAliases(view, aliases.Select(a => (a.Alias, a.Visibility)).ToList()).Select(a => a.Alias).ToList();
        if (row.Code is not null)
        {
            seen.Add(row.Code);
        }

        return seen.Any(name => row.Slug == CampaignSlugs.From(name, row.Kind) ||
                                row.Slug == CampaignSlugs.From(CampaignText.KeyWithoutArticle(name), row.Kind))
            ? row.Kind + ":" + row.Slug
            : reference;
    }

    private Dictionary<string, IReadOnlyList<AliasRow>> Aliases(IReadOnlyList<string> entityIds)
    {
        var rows = new List<AliasRow>();
        foreach (var chunk in entityIds.Chunk(400))
        {
            rows.AddRange(Connection.Query<AliasRow>(
                $"SELECT {AliasRow.Columns} FROM entity_alias WHERE entity_id IN @ids ORDER BY alias COLLATE NOCASE", new { ids = chunk }));
        }

        if (AsOf is { } session)
        {
            var table = CampaignTables.EntityAlias;
            rows = AsOfRows.ForEntities<AliasRow>(Connection, table, rows.Select(a => table.TargetId([a.EntityId, a.Alias])), entityIds, session)
                .OrderBy(a => a.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return rows.GroupBy(a => a.EntityId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AliasRow>)g.ToList(), StringComparer.Ordinal);
    }
}
