using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Dice;
using DndMcp.Repository.Campaign.Read;
using Microsoft.Data.Sqlite;
using CV = DndMcp.Domain.Campaign.CampaignValues;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;
using S = DndMcp.Domain.Campaign.CampaignValues.CombatSides;

namespace DndMcp.Repository.Campaign.Combat;

/// <summary>
/// What every possible roll subject of one combat call may be called in a dice label, and which linked entities the
/// party is SHOWN (contract §6.10), computed on a read connection BEFORE the step's transaction: both need a
/// <see cref="ReadScope"/>, which cannot run inside a write transaction (CRIT Q2). The names come from C's
/// <see cref="SessionSafeNames"/> in one pass over every combatant of the encounter (and every new <c>add</c> entry,
/// keyed <c>entry:&lt;i&gt;</c>); the step picks its subject's inside the transaction. The same names are the
/// <c>source</c> a persisted condition carries at <c>end</c> (§6.7).
/// </summary>
/// <remarks>
/// A subject the step meets that was not here (a combatant another process added between this read and the
/// transaction) is named <see cref="SessionSafeNames.CombatantFallback"/> and counted as not shown: the safe side.
/// </remarks>
internal sealed class CombatSubjects
{
    private readonly IReadOnlyDictionary<string, string> _names;
    private readonly IReadOnlySet<string> _shown;

    private CombatSubjects(IReadOnlyDictionary<string, string> names, IReadOnlySet<string> shown)
    {
        _names = names;
        _shown = shown;
    }

    /// <summary>No subjects: every name is the fallback.</summary>
    public static CombatSubjects None { get; } = new(new Dictionary<string, string>(), new HashSet<string>());

    /// <summary>Every safe name, by key (combatant id or <c>entry:&lt;i&gt;</c>).</summary>
    public IReadOnlyDictionary<string, string> Names => _names;

    /// <summary>The safe name of a subject (the fallback when it was not computed).</summary>
    public string Name(string? key) => key is not null && _names.TryGetValue(key, out var name) ? name : SessionSafeNames.CombatantFallback;

    /// <summary>Whether the party is shown this entity (visible, not disguised).</summary>
    public bool Shown(string? entityId) => entityId is not null && _shown.Contains(entityId);

    /// <summary>The key of the <paramref name="index"/>th (0-based) add entry.</summary>
    public static string EntryKey(int index) => "entry:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The names and shown flags of the encounter's combatants (when <paramref name="state"/> is given) and of the
    /// <paramref name="extra"/> subjects (add entries), on <paramref name="connection"/> (a read connection).
    /// </summary>
    public static CombatSubjects Compute(SqliteConnection connection, CampaignRow campaign, EncounterState? state, IReadOnlyList<SafeNameSubject> extra)
    {
        var subjects = new List<SafeNameSubject>(extra);
        if (state is not null)
        {
            subjects.AddRange(state.Combatants.Select(Subject));
        }

        if (subjects.Count == 0)
        {
            return None;
        }

        var names = SessionSafeNames.For(connection, campaign, subjects);
        var entities = subjects.Select(s => s.EntityId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var shown = new HashSet<string>(StringComparer.Ordinal);
        if (entities.Count > 0)
        {
            var party = ReadScope.Open(connection, campaign, Perspective.Parse(CV.PerspectiveKinds.Party), null);
            party.LoadEntities(entities);
            foreach (var id in entities.Where(id => party.Entity(id) is { Shown: true }))
            {
                shown.Add(id);
            }
        }

        return new CombatSubjects(names, shown);
    }

    /// <summary>A combatant as a label subject (§6.10): entity-linked, a stat block's monster, or a custom name and side.</summary>
    public static SafeNameSubject Subject(CombatantState c) => new(
        c.Id,
        EntityId: c.EntityId,
        MonsterName: c.StatBlock?.Name,
        Name: c.EntityId is null && c.StatBlock is null ? c.Name : null,
        Side: c.Side);
}

/// <summary>
/// The secrecy of one roll subject (§6.10): its side, whether it is hidden, and the entity it is linked to.
/// </summary>
internal sealed record RollSubject(string Key, string Side, bool Hidden, string? EntityId)
{
    public static RollSubject Of(CombatantState c) => new(c.Id, c.Side, c.Hidden, c.EntityId);
}

/// <summary>
/// The server's combat rolls (contract D11, §6.10): each <see cref="RollNeed"/> a step declares is rolled here with the
/// caller's <see cref="IDiceRoller"/> (<see cref="DiceEvaluator"/>, never the host) and logged as one <c>dice_roll</c> row
/// with <see cref="DiceRollLog.Append"/> INSIDE the step's transaction: <c>encounter_id</c> set, <c>session_id</c> the live
/// session, else the encounter's, else NULL (combat rolls are logged with no session too), the label and secrecy below,
/// <c>expression</c> the dice actually rolled ("4d6+5" for a critical "2d6+5", "2d20kl1" for disadvantage; never a name),
/// and the detail as <see cref="DiceLogDetail.Json"/> with source <see cref="Source"/>. The id is chosen here, so the
/// step's combat_log rows cite it (<c>roll_id</c>). A value the caller GAVE is no roll: no row (the tracker's combat_log
/// detail says <c>"given": true</c>).
///
/// <para>
/// <b>Secret</b> (the call's <c>secret</c> overrides) when any of: the subject is hidden; the purpose is "hit points" and
/// the subject is not party-side; the subject is not party-side and its linked entity is not Shown to the party; in a DM
/// campaign, the subject is enemy- or neutral-side; there is no subject and any target alone would make it secret.
/// Otherwise open. "Party-side" is the <c>party</c> side (an ally is not: §6.12 shows only party rows' numbers).
/// </para>
/// <para>
/// <b>Label</b> <c>&lt;name&gt;: &lt;purpose&gt;</c> with the subject's session-safe name (<see cref="CombatSubjects"/>),
/// or the purpose alone with no subject. A non-secret label is printed to every player view of the session, which is why
/// it never carries a tracker name (A-L2: "Keras" typed over a lich is "Lich: damage").
/// </para>
/// </summary>
internal static class CombatDice
{
    /// <summary>The <c>DiceLogDetail</c> source of the tracker's rolls.</summary>
    public const string Source = "combat";

    /// <summary>The rolls of one step: values for the tracker, ids for combat_log, and the author's view of each.</summary>
    public sealed record Rolled(IReadOnlyDictionary<string, RolledValue> Values, IReadOnlyDictionary<string, string> Ids, IReadOnlyList<CombatRoll> Views)
    {
        public static Rolled Empty { get; } = new(new Dictionary<string, RolledValue>(), new Dictionary<string, string>(), []);
    }

    /// <summary>
    /// Rolls and logs every need. <paramref name="subjectOf"/> gives a need's subject (a combatant or a new add entry);
    /// <paramref name="targets"/> are the step's targets, for the no-subject rule.
    /// </summary>
    public static Rolled Roll(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignRow campaign,
        EncounterRow encounter,
        IReadOnlyList<RollNeed> needs,
        Func<string?, RollSubject?> subjectOf,
        IReadOnlyList<RollSubject> targets,
        CombatSubjects subjects,
        bool? secret,
        IDiceRoller roller,
        string at)
    {
        if (needs.Count == 0)
        {
            return Rolled.Empty;
        }

        var session = SessionFor(connection, transaction, campaign.Id, encounter);
        var values = new Dictionary<string, RolledValue>(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var views = new List<CombatRoll>();
        for (var i = 0; i < needs.Count; i++)
        {
            var need = needs[i];
            var roll = DiceEvaluator.Roll(DiceExpression.Parse(need.Expression), roller);
            var subject = subjectOf(need.CombatantId);
            var isSecret = secret ?? (subject is not null
                ? Secret(campaign, need.Purpose, subject, subjects)
                : targets.Any(t => Secret(campaign, need.Purpose, t, subjects)));
            var label = subject is null ? need.Purpose : $"{subjects.Name(subject.Key)}: {need.Purpose}";
            var id = CampaignDatabase.NewId();
            DiceRollLog.Append(connection, transaction, new DiceRollRow(0, id, campaign.Id, session, need.Expression, label, roll.Total, null,
                DiceLogDetail.Json(roll, i, needs.Count, Source), isSecret ? 1 : 0, at, EncounterId: encounter.Id));
            var value = RolledValue.From(roll);
            values[need.Key] = value;
            ids[need.Key] = id;
            views.Add(new CombatRoll(need.Key, need.Purpose, need.Expression, value.Faces, roll.Total, label, isSecret, id, need.CombatantId));
        }

        return new Rolled(values, ids, views);
    }

    /// <summary>§6.10's secrecy of one subject's roll (class summary).</summary>
    public static bool Secret(CampaignRow campaign, string purpose, RollSubject subject, CombatSubjects subjects)
    {
        var partySide = subject.Side == S.Party;
        return subject.Hidden ||
               (purpose == P.HitPoints && !partySide) ||
               (!partySide && subject.EntityId is not null && !subjects.Shown(subject.EntityId)) ||
               (campaign.Role == CV.Roles.Dm && subject.Side is S.Enemy or S.Neutral);
    }

    /// <summary>The session a combat roll is filed under (D11): the live session, else the encounter's, else none.</summary>
    public static string? SessionFor(SqliteConnection connection, SqliteTransaction transaction, string campaignId, EncounterRow encounter) =>
        connection.QueryFirstOrDefault<string?>(
            "SELECT entity_id FROM session WHERE campaign_id = @campaignId AND status = @live LIMIT 1",
            new { campaignId, live = CV.SessionStatuses.Live }, transaction) ?? encounter.SessionId;
}
