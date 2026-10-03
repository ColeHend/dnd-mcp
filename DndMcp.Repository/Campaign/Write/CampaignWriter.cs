using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using O = DndMcp.Domain.Campaign.CampaignValues.OpKinds;

namespace DndMcp.Repository.Campaign.Write;

/// <summary>
/// campaign_write: applies a batch of ops (upsert, delete, restore, link, unlink, fact, status, objective, tick, answer)
/// as ONE batch, one transaction and one undo unit (contract §6).
///
/// <para>
/// <b>Order of work:</b> D's <see cref="CampaignOpValidation.Validate"/> first, before anything is opened, so every
/// problem visible without the database comes back at once. Then, inside the transaction, each op in order: its handles
/// resolved (an op may name what an earlier op of the call created), D's kind-dependent checks re-run with the resolved
/// kinds (<see cref="CampaignOpValidation.ResolvedProblems"/>: a status that does not fit an <c>e:12</c>'s kind, a tick
/// of something that is not a clock), then the write. Finally the reveal checks and secret statuses of the whole batch
/// (<see cref="RevealChecks"/>).
/// </para>
/// <para>
/// <b>All or nothing.</b> An op that fails (not found, a taken code, a cycle) fails the whole call with
/// <c>ops item N (…): …</c> and nothing is written: the model fixes item N and resends the same call. A dry run runs every
/// op and every check exactly as the real run would (same F-codes, same warnings) and rolls back.
/// </para>
/// </summary>
public sealed class CampaignWriter
{
    private readonly CampaignDatabase _database;

    public CampaignWriter(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Applies <paramref name="ops"/> to <paramref name="campaign"/> as one batch.</summary>
    /// <exception cref="DndInputException">
    /// The batch is invalid (every problem D's validator sees, up to five) or an op failed against the database
    /// (<c>ops item N (…): …</c>). Nothing was written.
    /// </exception>
    /// <exception cref="CampaignStoreUnavailableException">campaigns.db cannot be written (the batch was rolled back).</exception>
    public WriteResult Apply(CampaignRow campaign, IReadOnlyList<CampaignOpSpec?> ops, WriteContext context)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(context);
        CampaignOpValidation.Validate(ops);
        var kinds = ops.Select(op => O.Set.TryMatch(op!.Op, out var k) ? k : op.Op!).Distinct(StringComparer.Ordinal).ToList();
        var tool = kinds.Count == 1 ? $"campaign_write/{kinds[0]}" : "campaign_write";
        return WriteBatch.Run(_database, campaign, context, tool, batch =>
        {
            for (var i = 0; i < ops.Count; i++)
            {
                var spec = ops[i]!;
                O.Set.TryMatch(spec.Op, out var op);
                var scope = new OpScope(i, spec, op!, $"ops item {WriteBatch.Number(i + 1)} ({Label(op!, spec)})");
                switch (op)
                {
                    case O.Upsert:
                        EntityOps.Upsert(batch, scope);
                        break;
                    case O.Delete:
                        EntityOps.DeleteOrRestore(batch, scope, delete: true);
                        break;
                    case O.Restore:
                        EntityOps.DeleteOrRestore(batch, scope, delete: false);
                        break;
                    case O.Link:
                        LinkOps.Link(batch, scope);
                        break;
                    case O.Unlink:
                        LinkOps.Unlink(batch, scope);
                        break;
                    case O.Fact:
                        FactOps.Fact(batch, scope);
                        break;
                    case O.Status:
                        EntityOps.Status(batch, scope);
                        break;
                    case O.Objective:
                        EntityOps.Objective(batch, scope);
                        break;
                    case O.Tick:
                        EntityOps.Tick(batch, scope);
                        break;
                    case O.Answer:
                        EntityOps.Answer(batch, scope);
                        break;
                    default:
                        throw new InvalidOperationException($"The validator passed an op it does not know: {op}.");
                }
            }
        });
    }

    /// <summary>
    /// "upsert character:iron-guts", "upsert character \"Iron Guts\"", "link belmakor member_of the-party", "fact f:12":
    /// the same item label D's validator prints, so a refusal from the database reads like one from the validator.
    /// Echoes only the caller's own input (handles, and at most 40 characters of an upsert's name).
    /// </summary>
    internal static string Label(string op, CampaignOpSpec spec)
    {
        switch (op)
        {
            case O.Upsert when spec.Ref is not null:
                return $"upsert {WriteBatch.Echo(spec.Ref)}";
            case O.Upsert when spec.Kind is not null:
                var kind = CampaignValues.Kinds.Set.TryMatch(spec.Kind, out var k) ? k : WriteBatch.Echo(spec.Kind);
                var name = string.IsNullOrWhiteSpace(spec.Name) ? string.Empty : $" \"{Short(spec.Name.Trim())}\"";
                return $"upsert {kind}{name}";
            case O.Link or O.Unlink:
                var parts = new[] { spec.From, spec.Rel, spec.To }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => WriteBatch.Echo(p));
                return string.Join(' ', new[] { op }.Concat(parts));
            default:
                return spec.Ref is null ? op : $"{op} {WriteBatch.Echo(spec.Ref)}";
        }
    }

    private static string Short(string text) => text.Length <= 40 ? text : text[..40] + "…";
}

/// <summary>One op being applied: its position, spec, canonical op name and message location.</summary>
internal sealed record OpScope(int Index, CampaignOpSpec Spec, string Op, string Where)
{
    /// <summary>The subject every campaign_write refusal is reported under ("Invalid ops: …").</summary>
    public const string Subject = "ops";

    /// <summary>A refusal located at this op.</summary>
    public DndInputException Fail(string message) => WriteBatch.Problem(Subject, $"{Where}: {message}");

    /// <summary>
    /// Re-runs D's kind-dependent checks now that the handles are resolved (a status or subtype that does not fit the
    /// kind of an <c>e:12</c>, a tick of something that is not a clock, a link's shape), with the validator's wording.
    /// </summary>
    public void Check(ResolvedKinds kinds) =>
        DslProblems.ThrowIfAny(CampaignOpValidation.ResolvedProblems(Index, Spec, kinds), Subject);
}

/// <summary>Canonical spellings of wire values the validator already accepted.</summary>
internal static class Canonical
{
    /// <summary>The canonical value of <paramref name="text"/> in <paramref name="set"/> (validated before, so a miss is a bug).</summary>
    public static string Of(DslValueSet set, string text) =>
        set.TryMatch(text, out var canonical)
            ? canonical
            : throw new InvalidOperationException($"\"{text}\" passed validation but is not a {set.What}.");

    /// <summary>As <see cref="Of"/>, or null for null.</summary>
    public static string? OrNull(DslValueSet set, string? text) => text is null ? null : Of(set, text);
}
