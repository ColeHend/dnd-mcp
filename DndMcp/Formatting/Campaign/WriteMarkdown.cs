using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// <c>campaign_write</c>'s result, and the parts every write result shares (<c>campaign_knowledge</c> record, reveal and
/// retract; <c>campaign_session</c>'s writes): the batch id with how to undo it, what each op did, the warnings and the
/// consequences.
///
/// <para>
/// <b>The batch line comes first and is never cut.</b> The batch id is the only handle on an undo; a result that lost it
/// to the output cap would leave a mistaken batch with no way back but reading the history. So the header, the batch id
/// and the undo call are written before anything that can grow, and every list after them is capped on its own.
/// </para>
/// <para>
/// <b>A dry run says so before anything else.</b> Its result looks like a real one (the same ops, warnings and the codes it
/// would assign, because the writer ran everything inside a transaction it then rolled back), so the title and the first
/// line both say nothing was written. A model skimming the table must not report the batch as applied, and there is no
/// batch id to undo.
/// </para>
/// <para>
/// <b>Warnings are applied-anyway news, not errors.</b> A gated reveal is written and warned about (the table is the source
/// of truth), so the warnings carry what the author must weigh: which gate condition is unmet, its severity (an
/// advisory is the gate's "probably"), and for a supersession the facts that rest on it with their depth and the fact
/// they rest on, so they can be re-checked. Messages name things by handle; W never quotes a statement in one.
/// </para>
/// <para>
/// <b>The undo call names the campaign.</b> campaign_history looks a batch up in the campaign the call resolves to, so an
/// undo printed without <c>campaign</c> fails ("No batch … in this campaign") as soon as another campaign is current: a
/// write that named its campaign, or a <c>use</c> since. The call is <see cref="HistoryMarkdown.UndoCall"/>, the one every
/// campaign result prints, and it must work when sent exactly as printed.
/// </para>
/// <para>
/// Write results are the author's (only the author writes), so refs are author refs (<c>kind:slug</c>).
/// </para>
/// </summary>
internal static class WriteMarkdown
{
    /// <summary>Rows of the applied table shown; a knowledge write can reach 1,500 rows (50 targets × 30 knowers).</summary>
    public const int MaxRows = 60;

    /// <summary>Warnings shown; the rest are counted.</summary>
    public const int MaxWarnings = 40;

    /// <summary>Consequences shown; the rest are counted.</summary>
    public const int MaxConsequences = 30;

    /// <summary>Dependents listed under one supersession warning.</summary>
    public const int MaxDependents = 25;

    /// <summary>
    /// Characters of one consequence line. A filled clock's consequence carries its on-fill markdown, which may be a
    /// 50,000-character body: printed whole it would push every later consequence past the output cap.
    /// </summary>
    public const int MaxConsequenceLength = 1_000;

    /// <summary>
    /// Characters of an op's "Changed" cell before the rest are counted. An upsert of a data object can change hundreds of
    /// <c>data.&lt;key&gt;</c> paths (20,000 characters of keys), and one such row must not fill the result on its own.
    /// </summary>
    public const int MaxChangedLength = 300;

    /// <summary>
    /// Where a list stops adding entries even under its own count limit, so the "… and N more" line and the lists after it
    /// still fit under <see cref="CampaignMarkdownText.MaxChars"/> (40 warnings with long gate notes run past it on their own).
    /// </summary>
    public const int Budget = CampaignMarkdownText.MaxChars - 3_000;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>campaign_write's result for <paramref name="opCount"/> ops.</summary>
    public static string Format(CampaignRow campaign, WriteResult result, int opCount)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(result);
        var b = new StringBuilder();
        var ops = Count(opCount, "op", "ops");
        b.Append("# ")
            .Append(result.DryRun ? $"Dry run: campaign_write, {ops} ({campaign.Slug}): nothing written" : $"campaign_write: {ops} applied ({campaign.Slug})")
            .Append("\n\n");
        AppendBatch(b, campaign.Slug, result.BatchId, result.DryRun, result.SessionNumber, Changed(result));

        b.Append("\n| # | Op | Ref | Outcome | Changed |\n|---|---|---|---|---|\n");
        var rows = 0;
        foreach (var applied in result.Applied.Take(MaxRows).TakeWhile(_ => b.Length < Budget / 2))
        {
            rows++;
            b.Append("| ").Append((applied.OpIndex + 1).ToString(Invariant))
                .Append(" | ").Append(applied.Op)
                .Append(" | ").Append(CampaignMarkdownText.Cell(RefWithCode(applied)))
                .Append(" | ").Append(applied.Outcome)
                .Append(" | ").Append(CampaignMarkdownText.Cell(ChangedCell(applied.ChangedFields)))
                .Append(" |\n");
        }

        AppendMore(b, rows, result.Applied.Count, "rows (every op above and below the cut was applied as reported)");
        AppendGates(b, result.Applied);
        AppendWarnings(b, result.Warnings, opLabel: "ops item");
        AppendConsequences(b, result.Consequences);
        return CampaignMarkdownText.Cap(b.ToString().TrimEnd() + "\n", "the batch itself is complete; split the ops across calls for a shorter result");
    }

    /// <summary>
    /// True when the batch changed anything: an op with an outcome other than unchanged, or a secret status it re-derived.
    /// A batch that changed nothing logged nothing, so there is nothing to undo and the undo hint would be a dead end.
    /// </summary>
    public static bool Changed(WriteResult result) =>
        result.Applied.Any(a => a.Outcome != WriteOutcomes.Unchanged) ||
        result.Consequences.Any(c => c.Kind == ConsequenceKinds.SecretStatus);

    /// <summary>
    /// The batch paragraph: for a dry run, that nothing was written; otherwise the full batch id, its session, and the
    /// undo call naming <paramref name="campaignSlug"/> (or that nothing changed and there is nothing to undo).
    /// </summary>
    public static void AppendBatch(StringBuilder b, string campaignSlug, string? batchId, bool dryRun, int? sessionNumber, bool changed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignSlug);
        var session = sessionNumber is { } n ? $" Session context: session {n.ToString(Invariant)}." : string.Empty;
        if (dryRun)
        {
            b.Append("**Dry run: nothing was written and no batch exists.** This is exactly what the call would do, including the codes it ")
                .Append("would assign; send the same call without dry_run to apply it.").Append(session).Append('\n');
            return;
        }

        if (!changed || batchId is null)
        {
            b.Append("Nothing changed: everything already matched what is stored, so there is no batch to undo.").Append(session).Append('\n');
            return;
        }

        b.Append("Batch `").Append(batchId).Append("`.").Append(session)
            .Append(" To undo it: ").Append(HistoryMarkdown.UndoCall(batchId, campaignSlug)).Append(".\n");
    }

    /// <summary>
    /// The warnings, warnings before advisories, each with its severity and kind; a gate warning also names the condition,
    /// the knower and (under the first warning about that gate) the gate's note; a supersession lists its dependents with
    /// depth and via.
    /// </summary>
    /// <param name="opLabel">"ops item" for campaign_write (op positions are the model's own); null to leave positions out.</param>
    public static void AppendWarnings(StringBuilder b, IReadOnlyList<WriteWarning> warnings, string? opLabel)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        b.Append("\n## Warnings (").Append(warnings.Count.ToString(Invariant)).Append(")\n");
        b.Append("Applied anyway; read them before the table does.\n");
        var ordered = warnings
            .Select((w, i) => (Warning: w, Index: i))
            .OrderBy(p => p.Warning.Severity == WarningSeverities.Advisory ? 1 : 0)
            .ThenBy(p => p.Index)
            .Select(p => p.Warning)
            .ToList();
        var shown = 0;
        var notesShown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var warning in ordered.Take(MaxWarnings).TakeWhile(_ => b.Length < Budget))
        {
            shown++;
            b.Append("- **").Append(warning.Severity).Append("** · ").Append(Label(warning));
            if (opLabel is not null && warning.OpIndex is { } index)
            {
                b.Append(" · ").Append(opLabel).Append(' ').Append((index + 1).ToString(Invariant));
            }

            switch (warning)
            {
                case SupersessionWarning supersession when supersession.Dependents.Count > 0:
                    AppendSupersession(b, supersession);
                    break;
                case GateWarning gate:
                    b.Append(" · ").Append(gate.Fact).Append(" → ").Append(gate.Knower).Append(": ").Append(OneLine(gate.Message)).Append('\n');
                    // One gate, one note: its after and with warnings share it, so it is printed under the first.
                    if (!string.IsNullOrWhiteSpace(gate.Note) && notesShown.Add(gate.Fact))
                    {
                        b.Append("  - Gate note: ").Append(OneLine(gate.Note)).Append('\n');
                    }

                    break;
                default:
                    b.Append(": ").Append(OneLine(warning.Message)).Append('\n');
                    break;
            }
        }

        AppendMore(b, shown, warnings.Count, "warnings");
    }

    /// <summary>What followed from the batch: secret statuses, filled clocks, beats now reachable.</summary>
    public static void AppendConsequences(StringBuilder b, IReadOnlyList<Consequence> consequences)
    {
        if (consequences.Count == 0)
        {
            return;
        }

        b.Append("\n## Consequences (").Append(consequences.Count.ToString(Invariant)).Append(")\n");
        var shown = 0;
        foreach (var consequence in consequences.Take(MaxConsequences).TakeWhile(_ => b.Length < Budget))
        {
            shown++;
            b.Append("- ").Append(consequence.Kind.Replace('_', ' ')).Append(": ").Append(Shorten(OneLine(consequence.Message), MaxConsequenceLength)).Append('\n');
        }

        AppendMore(b, shown, consequences.Count, "consequences");
    }

    /// <summary>
    /// The changed fields of one op as a cell: "summary, tags, data.dm_pronouns", or when they run past
    /// <see cref="MaxChangedLength"/>, as many as fit then "… and 212 more".
    /// </summary>
    public static string ChangedCell(IReadOnlyList<string> fields)
    {
        var cell = new StringBuilder();
        var shown = 0;
        foreach (var field in fields)
        {
            if (shown > 0 && cell.Length + field.Length + 2 > MaxChangedLength)
            {
                break;
            }

            cell.Append(shown == 0 ? string.Empty : ", ").Append(field);
            shown++;
        }

        return shown < fields.Count ? $"{cell}, … and {(fields.Count - shown).ToString(Invariant)} more" : cell.ToString();
    }

    /// <summary>"_… and 12 more rows._" when a list was cut.</summary>
    public static void AppendMore(StringBuilder b, int shown, int total, string what)
    {
        if (total > shown)
        {
            b.Append("_… and ").Append((total - shown).ToString(Invariant)).Append(" more ").Append(what).Append("._\n");
        }
    }

    /// <summary>"1 op", "3 ops".</summary>
    public static string Count(int count, string one, string many) => $"{count.ToString(Invariant)} {(count == 1 ? one : many)}";

    /// <summary>A gate as one line with fact handles: "after f:1, f:2 · with f:12 · routes testimonies (4 of f:4, …) · …".</summary>
    public static string GateLine(GateSpec gate)
    {
        var parts = new List<string>();
        AddList(parts, "after", gate.After);
        AddList(parts, "with", gate.With);
        AddList(parts, "prefer", gate.Prefer);
        AddList(parts, "seeds", gate.Seeds);
        if (gate.Routes is { Count: > 0 } routes)
        {
            parts.Add("routes " + string.Join("; ", routes.OfType<RouteSpec>().Select(r =>
            {
                var clues = r.Clues ?? [];
                var needed = r.MinClues ?? clues.Count;
                return $"{r.Id} ({needed.ToString(Invariant)} of {string.Join(", ", clues)})";
            })));
        }

        if (gate.MinRoutes is { } minRoutes)
        {
            parts.Add("min_routes " + minRoutes.ToString(Invariant));
        }

        AddList(parts, "forbidden_terms", gate.ForbiddenTerms);
        AddList(parts, "forbidden_patterns", gate.ForbiddenPatterns);
        AddList(parts, "forbidden_until", gate.ForbiddenUntil);
        AddList(parts, "preferred_terms", gate.PreferredTerms);
        if (!string.IsNullOrWhiteSpace(gate.Note))
        {
            parts.Add("note: " + OneLine(gate.Note));
        }

        return parts.Count == 0 ? "(none)" : string.Join(" · ", parts);
    }

    private static void AppendGates(StringBuilder b, IReadOnlyList<AppliedOp> applied)
    {
        var gated = applied.Where(a => a.Gate is not null).ToList();
        if (gated.Count == 0)
        {
            return;
        }

        b.Append("\nGates as stored:\n");
        foreach (var op in gated.Take(MaxRows).TakeWhile(_ => b.Length < Budget / 2))
        {
            b.Append("- ").Append(op.Ref).Append(": ").Append(CampaignMarkdownText.Excerpt(GateLine(op.Gate!), 600)).Append('\n');
        }
    }

    private static void AppendSupersession(StringBuilder b, SupersessionWarning warning)
    {
        var by = warning.SupersededBy is null ? string.Empty : " by " + warning.SupersededBy;
        b.Append(": ").Append(warning.Superseded).Append(" is superseded").Append(by).Append("; ")
            .Append(Count(warning.Dependents.Count, "fact rests", "facts rest")).Append(" on it, left unchanged for you to re-check:\n");
        foreach (var dependent in warning.Dependents.Take(MaxDependents))
        {
            b.Append("  - ").Append(dependent.Fact).Append(": depth ").Append(dependent.Depth.ToString(Invariant))
                .Append(", via ").Append(dependent.Via).Append('\n');
        }

        if (warning.Dependents.Count > MaxDependents)
        {
            b.Append("  - … and ").Append((warning.Dependents.Count - MaxDependents).ToString(Invariant)).Append(" more\n");
        }

        if (warning.EntitiesToRecheck.Count > 0)
        {
            b.Append("  - Entities to re-check: ").Append(string.Join(", ", warning.EntitiesToRecheck.Take(MaxDependents)))
                .Append(warning.EntitiesToRecheck.Count > MaxDependents ? ", …" : string.Empty).Append('\n');
        }
    }

    // "gate (after)", "reachable before gate", "name twin": the kind, readable, with a gate's condition.
    private static string Label(WriteWarning warning) => warning switch
    {
        GateWarning gate => $"gate ({gate.Gate})",
        _ => warning.Kind.Replace('_', ' '),
    };

    private static string RefWithCode(AppliedOp applied)
    {
        var knower = applied.Knower is null ? string.Empty : " → " + applied.Knower;
        if (applied.Code is { Length: > 0 } code && !applied.Ref.EndsWith(":" + code.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return $"{applied.Ref} · {code}{knower}";
        }

        return applied.Ref + knower;
    }

    private static void AddList(List<string> parts, string name, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            parts.Add($"{name} {string.Join(", ", values)}");
        }
    }

    private static string OneLine(string text) => text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
