using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using CV = DndMcp.Domain.Campaign.CampaignValues;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// campaign_knowledge's writing actions: record, reveal and retract (contract §6). Each call is one batch (dry-run
/// capable) that writes through the same <see cref="KnowledgeRows"/> as campaign_write's <c>known_by</c>, so the reveal
/// checks of §3.4 (after, with, routes, prefer; reachable before the gate), the author-visibility warning and the
/// secret-status derivation are identical whichever tool wrote the row.
///
/// <para>
/// <b>Reveal</b> is the "it happened at the table" shortcut: state <c>knows</c>, learned in the session context, to the
/// party unless <c>to</c> says otherwise. A secret reveals its gated facts (its <c>about</c> facts that have a gate; all
/// its <c>about</c> facts when none is gated) and makes the knowers aware of the secret itself; a handout reveals the
/// facts it is about and becomes <c>delivered</c>. <b>Retract</b> deletes rows (a mistaken record); a row that is not
/// there is a warning. To record that someone does NOT know, record state <c>unaware</c> instead: a missing row and an
/// unaware row mean different things to the verdicts.
/// </para>
/// </summary>
public sealed class KnowledgeWriter
{
    /// <summary>Targets and knowers one call takes.</summary>
    public const int MaxTargets = 50;

    private readonly CampaignDatabase _database;

    public KnowledgeWriter(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>
    /// Records who knows each target (facts or entities, by handle): one row per target per knower, inserted or updated.
    /// </summary>
    /// <exception cref="DndInputException">A target or knower is invalid or not found; nothing was written.</exception>
    public WriteResult Record(CampaignRow campaign, IReadOnlyList<string> targets, IReadOnlyList<KnowerSpec?> knowers, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problems = TargetProblems("targets", targets, required: true).ToList();
        if (knowers is null || knowers.Count == 0)
        {
            problems.Add("knowers is required: who knows it, e.g. [{\"who\": \"party\", \"state\": \"knows\"}].");
        }
        else
        {
            problems.AddRange(KnowerSpecs.Validate(knowers, "knowers"));
        }

        DslProblems.ThrowIfAny(problems, "knowledge");
        return WriteBatch.Run(_database, campaign, context, "campaign_knowledge/record", batch =>
        {
            for (var t = 0; t < targets.Count; t++)
            {
                var where = $"targets item {WriteBatch.Number(t + 1)} ({WriteBatch.Echo(targets[t])})";
                var target = ResolveTarget(batch, where, targets[t]);
                for (var k = 0; k < knowers!.Count; k++)
                {
                    var (outcome, changed, knower) = batch.Knowledge.Write("knowledge", $"{where}: knowers item {WriteBatch.Number(k + 1)}", target, knowers[k]!, "record", t);
                    batch.Applied.Add(new AppliedOp(t, "record", target.Ref, outcome, changed, Knower: knower.Text));
                }
            }
        });
    }

    /// <summary>
    /// Reveals facts (and a secret's gated facts, and a handout's facts) to <paramref name="to"/> (default the party):
    /// state knows, learned in the session context.
    /// </summary>
    /// <param name="facts">Fact handles, or null.</param>
    /// <param name="secret">A secret entity's handle, or null.</param>
    /// <param name="handout">A handout entity's handle, or null.</param>
    /// <param name="to">Knowers ("party", "character:belmakor"); null or empty means the party.</param>
    /// <param name="how">How they learned it (told, read, witnessed…).</param>
    /// <exception cref="DndInputException">Nothing to reveal, or a handle or knower is invalid or not found; nothing was written.</exception>
    public WriteResult Reveal(
        CampaignRow campaign,
        IReadOnlyList<string>? facts,
        string? secret,
        string? handout,
        IReadOnlyList<string>? to,
        string? how,
        WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problems = TargetProblems("facts", facts, required: false).ToList();
        if ((facts is null || facts.Count == 0) && string.IsNullOrWhiteSpace(secret) && string.IsNullOrWhiteSpace(handout))
        {
            problems.Add("give facts (fact handles), secret (a secret entity) or handout (a handout entity) to reveal, e.g. {\"facts\": [\"f:12\"], \"to\": [\"party\"]}.");
        }

        var knowers = (to is null || to.Count == 0 ? [CV.KnowerKinds.Party] : to)
            .Select(who => new KnowerSpec { Who = who, State = CV.KnowledgeStates.Knows, How = how })
            .ToList();
        problems.AddRange(KnowerSpecs.Validate(knowers, "to"));

        DslProblems.ThrowIfAny(problems, "knowledge");
        return WriteBatch.Run(_database, campaign, context, "campaign_knowledge/reveal", batch =>
        {
            var targets = new List<(KnowledgeTarget Target, string Where)>();
            for (var i = 0; i < (facts?.Count ?? 0); i++)
            {
                var where = $"facts item {WriteBatch.Number(i + 1)} ({WriteBatch.Echo(facts![i])})";
                targets.Add((KnowledgeTarget.Of(batch.RequireFact("knowledge", where, "fact", facts[i])), where));
            }

            EntityRow? secretEntity = null;
            if (!string.IsNullOrWhiteSpace(secret))
            {
                secretEntity = batch.RequireEntity("knowledge", "secret", "handle", secret, CV.Kinds.Secret);
                foreach (var fact in SecretFacts(batch, secretEntity))
                {
                    targets.Add((KnowledgeTarget.Of(fact), $"secret {secretEntity.Handle}"));
                }
            }

            EntityRow? handoutEntity = null;
            if (!string.IsNullOrWhiteSpace(handout))
            {
                handoutEntity = batch.RequireEntity("knowledge", "handout", "handle", handout, CV.Kinds.Handout);
                foreach (var fact in LinkedFacts(batch, handoutEntity, CV.FactLinkRoles.About))
                {
                    targets.Add((KnowledgeTarget.Of(fact), $"handout {handoutEntity.Handle}"));
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var (target, where) in targets.Where(t => seen.Add(t.Target.FactId!)))
            {
                WriteAll(batch, where, target, knowers, "reveal", index++);
            }

            if (secretEntity is not null)
            {
                var awareness = knowers.Select(k => new KnowerSpec { Who = k.Who, State = CV.KnowledgeStates.Aware, How = how }).ToList();
                WriteAll(batch, $"secret {secretEntity.Handle}", KnowledgeTarget.Of(secretEntity), awareness, "reveal", index++);
            }

            if (handoutEntity is not null)
            {
                var changed = batch.Update("entity", handoutEntity.Id, new Dictionary<string, object?> { ["status"] = CV.Statuses.HandoutDelivered }, "reveal");
                batch.Applied.Add(new AppliedOp(index, "reveal", WriteBatch.Ref(handoutEntity), changed.Count == 0 ? WriteOutcomes.Unchanged : WriteOutcomes.Updated, changed));
            }
        });
    }

    /// <summary>Deletes the rows of <paramref name="who"/> on each target; a row that is not there is a warning.</summary>
    /// <exception cref="DndInputException">A target or knower is invalid or not found; nothing was written.</exception>
    public WriteResult Retract(CampaignRow campaign, IReadOnlyList<string> targets, IReadOnlyList<string> who, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problems = TargetProblems("targets", targets, required: true).ToList();
        if (who is null || who.Count == 0)
        {
            problems.Add("who is required: whose rows to delete, e.g. [\"party\"] or [\"character:belmakor\"].");
        }
        else
        {
            problems.AddRange(KnowerSpecs.Validate(who.Select(w => new KnowerSpec { Who = w }).ToList(), "who"));
        }

        DslProblems.ThrowIfAny(problems, "knowledge");
        return WriteBatch.Run(_database, campaign, context, "campaign_knowledge/retract", batch =>
        {
            for (var t = 0; t < targets.Count; t++)
            {
                var where = $"targets item {WriteBatch.Number(t + 1)} ({WriteBatch.Echo(targets[t])})";
                var target = ResolveTarget(batch, where, targets[t]);
                for (var k = 0; k < who!.Count; k++)
                {
                    var knower = batch.Knowledge.ResolveKnower("knowledge", $"{where}: who item {WriteBatch.Number(k + 1)}", who[k]);
                    var deleted = batch.Knowledge.Delete(target, knower, "retract", t);
                    if (!deleted)
                    {
                        batch.Warnings.Add(new WriteWarning(WarningKinds.Nothing, WarningSeverities.Warning,
                            $"{knower.Text} has no row on {target.Ref}; nothing to retract.", t));
                    }

                    batch.Applied.Add(new AppliedOp(t, "retract", target.Ref, deleted ? WriteOutcomes.Deleted : WriteOutcomes.Unchanged,
                        deleted ? ["knowledge"] : [], Knower: knower.Text));
                }
            }
        });
    }

    private static void WriteAll(WriteBatch batch, string where, KnowledgeTarget target, IReadOnlyList<KnowerSpec> knowers, string action, int index)
    {
        for (var k = 0; k < knowers.Count; k++)
        {
            var (outcome, changed, knower) = batch.Knowledge.Write("knowledge", $"{where}: to item {WriteBatch.Number(k + 1)}", target, knowers[k], action, index);
            batch.Applied.Add(new AppliedOp(index, action, target.Ref, outcome, changed, Knower: knower.Text));
        }
    }

    /// <summary>A fact (f:n, a fact code) or an entity (any other handle; a code on both is refused by the resolver).</summary>
    private static KnowledgeTarget ResolveTarget(WriteBatch batch, string where, string text)
    {
        CampaignHandle.TryParse(text, out var handle, out _);
        EntityRow? entity;
        FactRow? fact;
        try
        {
            (entity, fact) = batch.Resolver.TryEntityOrFact(handle);
        }
        catch (DndInputException ex)
        {
            throw WriteBatch.Problem("knowledge", $"{where}: {ex.Message}");
        }

        if (fact is not null)
        {
            return KnowledgeTarget.Of(fact);
        }

        if (entity is not null)
        {
            if (entity.Kind == CV.Kinds.Session)
            {
                throw WriteBatch.Problem("knowledge", $"{where}: a session is not something a knower learns; record the facts established in it.");
            }

            return KnowledgeTarget.Of(entity);
        }

        return handle is CampaignHandle.FactBySeq
            ? KnowledgeTarget.Of(batch.RequireFact("knowledge", where, "fact", text))
            : KnowledgeTarget.Of(batch.RequireEntity("knowledge", where, "target", text));
    }

    /// <summary>A secret's gated facts (its live about-facts with a gate), else all its live about-facts.</summary>
    private static IReadOnlyList<FactRow> SecretFacts(WriteBatch batch, EntityRow secret)
    {
        var about = LinkedFacts(batch, secret, CV.FactLinkRoles.About);
        var gated = about.Where(f => f.Gate is not null && FactGates.TryParse(f.Gate, out var g) && g is not null).ToList();
        return gated.Count > 0 ? gated : about;
    }

    private static IReadOnlyList<FactRow> LinkedFacts(WriteBatch batch, EntityRow entity, string role) =>
        Dapper.SqlMapper.Query<FactRow>(batch.Connection,
            $"SELECT {CampaignRows.Prefixed(FactRow.Columns, "f")} FROM fact_link fl JOIN fact f ON f.id = fl.fact_id " +
            "WHERE fl.entity_id = @entityId AND fl.role = @role AND f.deleted_at IS NULL ORDER BY f.seq",
            new { entityId = entity.Id, role }, batch.Transaction).ToList();

    private static IEnumerable<string> TargetProblems(string field, IReadOnlyList<string>? targets, bool required)
    {
        if (targets is null || targets.Count == 0)
        {
            if (required)
            {
                yield return $"{field} is required: fact or entity handles, e.g. [\"f:12\", \"character:old-king\"].";
            }

            yield break;
        }

        if (targets.Count > MaxTargets)
        {
            yield return $"{field} has {WriteBatch.Number(targets.Count)} items; at most {WriteBatch.Number(MaxTargets)} per call.";
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < targets.Count; i++)
        {
            var where = $"{field} item {WriteBatch.Number(i + 1)}";
            if (!CampaignHandle.TryParse(targets[i], out var handle, out var problem))
            {
                yield return $"{where}: {problem}";
            }
            else if (handle is CampaignHandle.CrossCampaign)
            {
                yield return $"{where}: {handle.Text} is another campaign's; knowledge is recorded in the campaign it belongs to.";
            }
            else if (handle is CampaignHandle.SessionByNumber or CampaignHandle.SessionLive or CampaignHandle.SessionLast)
            {
                yield return $"{where}: {handle.Text} is a session; record the facts established in it.";
            }
            else if (!seen.Add(handle.Text))
            {
                yield return $"{where}: {handle.Text} is listed twice.";
            }
        }
    }
}
