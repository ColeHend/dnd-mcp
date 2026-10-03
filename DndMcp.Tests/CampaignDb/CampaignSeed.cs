using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using Microsoft.Data.Sqlite;

namespace DndMcp.Tests.CampaignDb;

/// <summary>A seeded campaign: its id and slug, and its party faction when one was made.</summary>
public sealed record SeededCampaign(string Id, string Slug, SeededEntity? Party);

/// <summary>A seeded entity: id, <c>e:</c> seq, slug, kind and name.</summary>
public sealed record SeededEntity(string Id, long Seq, string Slug, string Kind, string Name)
{
    /// <summary><c>kind:slug</c>.</summary>
    public string Handle => Kind + ":" + Slug;

    /// <summary><c>e:&lt;seq&gt;</c>.</summary>
    public string SeqHandle => "e:" + Seq.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A seeded fact: id, <c>f:</c> seq and code.</summary>
public sealed record SeededFact(string Id, long Seq, string? Code)
{
    /// <summary><c>f:&lt;seq&gt;</c>.</summary>
    public string SeqHandle => "f:" + Seq.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A seeded session: its entity (id, seq) and number.</summary>
public sealed record SeededSession(string EntityId, long Seq, int Number);

/// <summary>
/// Raw-SQL insert helpers for campaigns.db test fixtures, bypassing change_log: the read path's tests seed with these, so
/// they depend on the schema and nothing else (not on the write path, built in parallel). Every helper has sensible
/// defaults and returns the ids and seqs later assertions need; override what a test is about with named arguments.
///
/// <para>
/// The rows go through the real schema, so its CHECKs, foreign keys and FTS triggers apply: a fixture that could not be
/// written by the tools fails here too (a knowledge row with both targets, an unknown kind). Timestamps are the fixed
/// <c>at</c> given to the constructor. Nothing here writes change_log: history-dependent tests (undo, replay) write
/// through <see cref="ChangeRecorder"/> instead.
/// </para>
/// </summary>
public sealed class CampaignSeed
{
    private readonly SqliteConnection _connection;

    /// <param name="connection">An open campaigns.db connection (no transaction; each insert commits).</param>
    /// <param name="at">created_at / updated_at of every row.</param>
    public CampaignSeed(SqliteConnection connection, string at = "2026-09-01T12:00:00.000Z")
    {
        _connection = connection;
        At = at;
        CampaignDatabase.EnsureDapperConfigured();
    }

    public string At { get; }

    /// <summary>
    /// A campaign, with its party faction (subtype party, visibility party) set as <c>party_id</c> unless
    /// <paramref name="withParty"/> is false.
    /// </summary>
    public SeededCampaign Campaign(
        string name = "Test Campaign",
        string role = CampaignValues.Roles.Dm,
        string ruleset = CampaignValues.Rulesets.R2024,
        string? slug = null,
        string? dmName = null,
        string settings = "{}",
        bool withParty = true,
        string partyName = "The Party",
        string status = CampaignValues.CampaignStatuses.Active)
    {
        var id = CampaignDatabase.NewId();
        slug ??= CampaignSlugs.From(name, "campaign");
        _connection.Execute(
            "INSERT INTO campaign(id, slug, name, role, ruleset, status, dm_name, settings, created_at, updated_at) " +
            "VALUES (@id, @slug, @name, @role, @ruleset, @status, @dmName, @settings, @at, @at)",
            new { id, slug, name, role, ruleset, status, dmName, settings, at = At });
        SeededEntity? party = null;
        if (withParty)
        {
            party = Entity(id, CampaignValues.Kinds.Faction, partyName, subtype: CampaignValues.Subtypes.PartyFaction);
            _connection.Execute("UPDATE campaign SET party_id = @party WHERE id = @id", new { party = party.Id, id });
        }

        return new SeededCampaign(id, slug, party);
    }

    /// <summary>The campaign row, for loaders that take a <see cref="CampaignRow"/>.</summary>
    public CampaignRow LoadCampaign(string campaignId) =>
        _connection.QuerySingle<CampaignRow>($"SELECT {CampaignRow.Columns} FROM campaign WHERE id = @campaignId", new { campaignId });

    /// <summary>Sets campaign.my_character_id (a player campaign's PC).</summary>
    public void SetMyCharacter(string campaignId, string characterId) =>
        _connection.Execute("UPDATE campaign SET my_character_id = @characterId WHERE id = @campaignId", new { characterId, campaignId });

    /// <summary>
    /// An entity. Slug defaults to <see cref="CampaignSlugs.From"/> of the name; status to the kind's default
    /// (<see cref="CampaignValues.Statuses.Defaults"/>).
    /// </summary>
    public SeededEntity Entity(
        string campaignId,
        string kind,
        string name,
        string? slug = null,
        string? subtype = null,
        string? status = null,
        string visibility = CampaignValues.Visibilities.Party,
        string summary = "",
        string body = "",
        string secret = "",
        string? code = null,
        string canonStatus = CampaignValues.CanonStatuses.Canon,
        string confidence = CampaignValues.Confidences.Confirmed,
        string? parentId = null,
        double? sortKey = null,
        string data = "{}",
        string? introducedSessionId = null,
        string? source = null,
        string? deletedAt = null)
    {
        var id = CampaignDatabase.NewId();
        slug ??= CampaignSlugs.From(name, kind);
        status ??= CampaignValues.Statuses.Defaults.GetValueOrDefault(kind);
        var seq = _connection.ExecuteScalar<long>(
            "INSERT INTO entity(id, campaign_id, kind, subtype, slug, code, name, summary, body_md, secret_md, status, visibility, " +
            "canon_status, confidence, parent_id, sort_key, data, introduced_session_id, source, created_at, updated_at, deleted_at) " +
            "VALUES (@id, @campaignId, @kind, @subtype, @slug, @code, @name, @summary, @body, @secret, @status, @visibility, " +
            "@canonStatus, @confidence, @parentId, @sortKey, @data, @introducedSessionId, @source, @at, @at, @deletedAt) RETURNING seq",
            new
            {
                id, campaignId, kind, subtype, slug, code, name, summary, body, secret, status, visibility, canonStatus, confidence,
                parentId, sortKey, data, introducedSessionId, source, at = At, deletedAt,
            });
        return new SeededEntity(id, seq, slug, kind, name);
    }

    /// <summary>An alias (visibility public / party are searchable by everyone; restricted / author only by the author).</summary>
    public void Alias(string entityId, string alias, string visibility = CampaignValues.Visibilities.Party) =>
        _connection.Execute("INSERT INTO entity_alias(entity_id, alias, visibility) VALUES (@entityId, @alias, @visibility)",
            new { entityId, alias, visibility });

    /// <summary>Tags an entity, creating the campaign's tag on first use; returns the tag id.</summary>
    public string Tag(string campaignId, string entityId, string tagName)
    {
        var tagId = _connection.ExecuteScalar<string?>(
            "SELECT id FROM tag WHERE campaign_id = @campaignId AND name = @tagName", new { campaignId, tagName });
        if (tagId is null)
        {
            tagId = CampaignDatabase.NewId();
            _connection.Execute("INSERT INTO tag(id, campaign_id, name) VALUES (@tagId, @campaignId, @tagName)",
                new { tagId, campaignId, tagName });
        }

        _connection.Execute("INSERT INTO entity_tag(entity_id, tag_id) VALUES (@entityId, @tagId)", new { entityId, tagId });
        return tagId;
    }

    /// <summary>A relation; returns its id.</summary>
    public string Relation(
        string campaignId,
        string fromId,
        string rel,
        string toId,
        string visibility = CampaignValues.Visibilities.Party,
        string status = CampaignValues.RelationStatuses.Current,
        string? label = null,
        long? attitude = null,
        bool symmetric = false,
        string? sinceSessionId = null,
        string? untilSessionId = null,
        string data = "{}")
    {
        var id = CampaignDatabase.NewId();
        _connection.Execute(
            "INSERT INTO relation(id, campaign_id, from_id, rel, to_id, label, attitude, symmetric, visibility, status, " +
            "since_session_id, until_session_id, data, created_at, updated_at) VALUES (@id, @campaignId, @fromId, @rel, @toId, " +
            "@label, @attitude, @symmetric, @visibility, @status, @sinceSessionId, @untilSessionId, @data, @at, @at)",
            new
            {
                id, campaignId, fromId, rel, toId, label, attitude, symmetric = symmetric ? 1L : 0L, visibility, status,
                sinceSessionId, untilSessionId, data, at = At,
            });
        return id;
    }

    /// <summary>
    /// Party membership: a <c>member_of</c> relation from the character to the party (status former when
    /// <paramref name="untilSessionId"/> is given, unless set).
    /// </summary>
    public string MemberOf(string campaignId, string characterId, string partyId, string? sinceSessionId = null, string? untilSessionId = null, string? status = null) =>
        Relation(campaignId, characterId, CampaignValues.Rels.MemberOf, partyId,
            status: status ?? (untilSessionId is null ? CampaignValues.RelationStatuses.Current : CampaignValues.RelationStatuses.Former),
            sinceSessionId: sinceSessionId, untilSessionId: untilSessionId);

    /// <summary>A cross-campaign link (stored with a_id &lt; b_id).</summary>
    public void CrossLink(string entityId, string otherEntityId, string? note = null)
    {
        var (a, b) = string.CompareOrdinal(entityId, otherEntityId) < 0 ? (entityId, otherEntityId) : (otherEntityId, entityId);
        _connection.Execute("INSERT INTO cross_link(a_id, b_id, note, created_at) VALUES (@a, @b, @note, @at)", new { a, b, note, at = At });
    }

    /// <summary>A fact (visibility restricted unless given).</summary>
    public SeededFact Fact(
        string campaignId,
        string statement,
        string visibility = CampaignValues.Visibilities.Restricted,
        string? code = null,
        string factType = CampaignValues.FactTypes.Canon,
        string truth = CampaignValues.Truths.True,
        string canonStatus = CampaignValues.CanonStatuses.Canon,
        string confidence = CampaignValues.Confidences.Confirmed,
        string? gate = null,
        string? establishedSessionId = null,
        string? source = null,
        string? supersededBy = null,
        string? deletedAt = null)
    {
        var id = CampaignDatabase.NewId();
        var seq = _connection.ExecuteScalar<long>(
            "INSERT INTO fact(id, campaign_id, code, statement, fact_type, truth, canon_status, confidence, visibility, gate, " +
            "established_session_id, source, superseded_by, created_at, updated_at, deleted_at) VALUES (@id, @campaignId, @code, " +
            "@statement, @factType, @truth, @canonStatus, @confidence, @visibility, @gate, @establishedSessionId, @source, " +
            "@supersededBy, @at, @at, @deletedAt) RETURNING seq",
            new
            {
                id, campaignId, code, statement, factType, truth, canonStatus, confidence, visibility, gate, establishedSessionId,
                source, supersededBy, at = At, deletedAt,
            });
        return new SeededFact(id, seq, code);
    }

    /// <summary>Links a fact to an entity (role about unless given).</summary>
    public void FactLink(string factId, string entityId, string role = CampaignValues.FactLinkRoles.About) =>
        _connection.Execute("INSERT INTO fact_link(fact_id, entity_id, role) VALUES (@factId, @entityId, @role)", new { factId, entityId, role });

    /// <summary><paramref name="factId"/> depends on <paramref name="dependsOn"/>.</summary>
    public void FactDependency(string factId, string dependsOn) =>
        _connection.Execute("INSERT INTO fact_dependency(fact_id, depends_on) VALUES (@factId, @dependsOn)", new { factId, dependsOn });

    /// <summary>
    /// A knowledge row about a fact OR an entity (exactly one of <paramref name="factId"/> / <paramref name="entityId"/>);
    /// <paramref name="knowerId"/> is the character for a character knower, null otherwise. Returns its id.
    /// </summary>
    public string Knowledge(
        string campaignId,
        string knowerKind,
        string? knowerId = null,
        string state = CampaignValues.KnowledgeStates.Knows,
        string? factId = null,
        string? entityId = null,
        string? knownAs = null,
        string? learnedSessionId = null,
        string? learnedIngame = null,
        string? viaEntityId = null,
        string? how = null,
        string? note = null,
        string? validUntilSessionId = null)
    {
        var id = CampaignDatabase.NewId();
        _connection.Execute(
            "INSERT INTO knowledge(id, campaign_id, fact_id, entity_id, knower_kind, knower_id, state, known_as, learned_session_id, " +
            "learned_ingame, via_entity_id, how, note, valid_until_session_id, created_at, updated_at) VALUES (@id, @campaignId, " +
            "@factId, @entityId, @knowerKind, @knowerId, @state, @knownAs, @learnedSessionId, @learnedIngame, @viaEntityId, @how, " +
            "@note, @validUntilSessionId, @at, @at)",
            new
            {
                id, campaignId, factId, entityId, knowerKind, knowerId, state, knownAs, learnedSessionId, learnedIngame, viaEntityId,
                how, note, validUntilSessionId, at = At,
            });
        return id;
    }

    /// <summary>
    /// A session: the session entity (slug <c>session-&lt;n&gt;</c>, name = title or "Session n", body = recap) and its
    /// session row.
    /// </summary>
    public SeededSession Session(
        string campaignId,
        int number,
        string status = CampaignValues.SessionStatuses.Played,
        string? title = null,
        string recap = "",
        string? playedOn = null,
        string visibility = CampaignValues.Visibilities.Party,
        string prepMd = "",
        string liveLog = "[]")
    {
        var entity = Entity(campaignId, CampaignValues.Kinds.Session,
            title ?? "Session " + number.ToString(CultureInfo.InvariantCulture),
            slug: CampaignSlugs.ForSession(number), body: recap, visibility: visibility);
        _connection.Execute(
            "INSERT INTO session(entity_id, campaign_id, number, status, played_on, prep_md, live_log) " +
            "VALUES (@entityId, @campaignId, @number, @status, @playedOn, @prepMd, @liveLog)",
            new { entityId = entity.Id, campaignId, number, status, playedOn, prepMd, liveLog });
        return new SeededSession(entity.Id, entity.Seq, number);
    }

    /// <summary>A character's attendance at a session.</summary>
    public void Attendance(string sessionId, string characterId, bool present = true, string? note = null) =>
        _connection.Execute(
            "INSERT INTO session_attendance(session_id, character_id, present, note) VALUES (@sessionId, @characterId, @present, @note)",
            new { sessionId, characterId, present = present ? 1L : 0L, note });

    /// <summary>A clock's mechanics for a clock entity.</summary>
    public void Clock(
        string entityId,
        int segments,
        int filled = 0,
        string unit = CampaignValues.ClockUnits.Segment,
        string? frontId = null,
        bool shownToPlayers = false,
        string onFillMd = "") =>
        _connection.Execute(
            "INSERT INTO clock(entity_id, segments, filled, unit, front_id, shown_to_players, on_fill_md) " +
            "VALUES (@entityId, @segments, @filled, @unit, @frontId, @shown, @onFillMd)",
            new { entityId, segments, filled, unit, frontId, shown = shownToPlayers ? 1L : 0L, onFillMd });

    /// <summary>An objective of a quest or thread (ordinal defaults to after the last); returns its id.</summary>
    public string Objective(
        string questId,
        string text,
        double? ordinal = null,
        string status = CampaignValues.ObjectiveStatuses.Open,
        long? progress = null,
        long? progressMax = null,
        string visibility = CampaignValues.Visibilities.Party,
        string? resolvedSessionId = null)
    {
        var id = CampaignDatabase.NewId();
        ordinal ??= _connection.ExecuteScalar<double>("SELECT coalesce(max(ordinal), 0) + 1 FROM objective WHERE quest_id = @questId", new { questId });
        _connection.Execute(
            "INSERT INTO objective(id, quest_id, ordinal, text, status, progress, progress_max, visibility, resolved_session_id, updated_at) " +
            "VALUES (@id, @questId, @ordinal, @text, @status, @progress, @progressMax, @visibility, @resolvedSessionId, @at)",
            new { id, questId, ordinal, text, status, progress, progressMax, visibility, resolvedSessionId, at = At });
        return id;
    }

    /// <summary>A story-web edge between two beats; returns its id.</summary>
    public string BeatEdge(string campaignId, string fromBeatId, string toBeatId, string mode = CampaignValues.BeatEdgeModes.AllOf)
    {
        var id = CampaignDatabase.NewId();
        _connection.Execute(
            "INSERT INTO beat_edge(id, campaign_id, from_beat_id, to_beat_id, mode) VALUES (@id, @campaignId, @fromBeatId, @toBeatId, @mode)",
            new { id, campaignId, fromBeatId, toBeatId, mode });
        return id;
    }

    /// <summary>A dice roll (not change-logged, like the real ones); returns its seq.</summary>
    public long DiceRoll(
        string campaignId,
        string? sessionId,
        string expression = "1d20",
        long total = 10,
        string? label = null,
        long? outcome = null,
        string detail = "{}",
        bool secret = false) =>
        DiceRollLog.Append(_connection, null,
            new DiceRollRow(0, string.Empty, campaignId, sessionId, expression, label, total, outcome, detail, secret ? 1L : 0L, At));
}
