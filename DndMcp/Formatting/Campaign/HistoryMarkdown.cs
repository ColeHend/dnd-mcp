using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// campaign_history's markdown: pages of batches (<c>since</c>, <c>entity</c>), one batch in full (<c>batch</c>), and
/// what an undo did (<c>undo</c>). Author-only by construction: change_log records every edit, secrets included, so the
/// tool takes no perspective and nothing here is ever rendered for another view (the leak rule's "no history").
///
/// <para>
/// <b>Batch ids are printed in full, always.</b> They are the one internal id the model is shown (contract §0), because
/// undo takes one; a shortened id would make the model retype a prefix that may stop being unique. Every page ends with
/// the undo call's exact shape and the dry-run advice, so a model reading history can act on it without another lookup.
/// </para>
/// <para>
/// <b>Every call printed names its campaign.</b> campaign_history looks a batch up inside the campaign the call resolves
/// to, so an undo, redo or "lists them all" call without <c>campaign</c> fails ("No batch … in this campaign") as soon as
/// the batch's campaign is not the current one: a write that named another campaign, or a <c>use</c> in between. A call
/// the result tells the model to make must work when sent exactly as printed.
/// </para>
/// <para>
/// <b>Bounded.</b> A page lists at most <see cref="MaxChangesPerBatch"/> changes per batch (a 50-op import logs hundreds of
/// rows) and says how to see the rest; the whole result is capped by <see cref="CampaignMarkdownText.Cap"/>.
/// </para>
/// </summary>
internal static class HistoryMarkdown
{
    /// <summary>Changes listed per batch on a page (the <c>batch</c> action lists every one, up to the output cap).</summary>
    public const int MaxChangesPerBatch = 25;

    private const string CapHint = "pass a smaller limit, targets, or read one batch with action \"batch\"";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>A page of batches of <paramref name="campaignSlug"/> under <paramref name="title"/>; <paramref name="emptyText"/> when it is empty.</summary>
    public static string Page(HistoryPage page, string title, string emptyText, string campaignSlug)
    {
        ArgumentNullException.ThrowIfNull(page);
        var b = new StringBuilder();
        b.Append("# ").Append(title).Append('\n');
        if (page.Batches.Count == 0)
        {
            b.Append('\n').Append(emptyText).Append('\n');
            return b.ToString();
        }

        b.Append('\n').Append(page.Total == page.Batches.Count
            ? $"{Number(page.Total)} batch{(page.Total == 1 ? string.Empty : "es")} (one tool call that wrote is one batch)."
            : $"{Number(page.Batches.Count)} of {Number(page.Total)} batches on this page (one tool call that wrote is one batch).").Append('\n');
        foreach (var batch in page.Batches)
        {
            b.Append('\n');
            BatchLines(b, batch, MaxChangesPerBatch, campaignSlug);
        }

        if (page.NextCursor is not null)
        {
            b.Append("\n_More on the next page: pass cursor \"").Append(page.NextCursor).Append("\" with the same arguments._\n");
        }

        b.Append('\n').Append(UndoHint(campaignSlug)).Append('\n');
        return CampaignMarkdownText.Cap(b.ToString(), CapHint);
    }

    /// <summary>One batch of <paramref name="campaignSlug"/> with every change (the <c>batch</c> action).</summary>
    public static string Batch(HistoryBatch batch, string campaignSlug)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var b = new StringBuilder();
        b.Append("# Batch ").Append(batch.BatchId).Append("\n\n");
        BatchLines(b, batch, int.MaxValue, campaignSlug, heading: false);
        b.Append('\n').Append(UndoHint(campaignSlug)).Append('\n');
        // A page lists at most MaxChangesPerBatch changes of a batch, so the way to the rest of an oversized batch is to
        // narrow it to the entries wanted: since with targets keeps only their changes.
        return CampaignMarkdownText.Cap(b.ToString(),
            $"campaign_history {{\"action\": \"since\", \"targets\": [\"<handle>\"], \"campaign\": \"{campaignSlug}\"}} lists the changes to the entries you name");
    }

    /// <summary>The undo call every history result ends with, naming the campaign (class summary).</summary>
    public static string UndoHint(string campaignSlug) =>
        $"_To reverse one batch: {UndoCall("<batch id>", campaignSlug, dryRun: true)} shows what it would change; run it again " +
        "without dry_run to apply it. An undo is itself a batch (undo it to redo)._";

    /// <summary>The exact undo call for one batch: <c>campaign_history {"action": "undo", "batch_id": "…", "campaign": "…"}</c>.</summary>
    public static string UndoCall(string batchId, string campaignSlug, bool dryRun = false) =>
        $"campaign_history {{\"action\": \"undo\", \"batch_id\": \"{batchId}\", \"campaign\": \"{campaignSlug}\"" +
        (dryRun ? ", \"dry_run\": true}" : "}");

    /// <summary>Whether a batch created the campaign itself (its undo removes the campaign, so it has no redo).</summary>
    public static bool CreatedTheCampaign(HistoryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.Changes.Any(c => c.Table == CampaignTables.Campaign.Name && c.Op == CampaignValues.ChangeOps.Create);
    }

    /// <summary>
    /// "### 2026-09-29T20:00:00.000Z · campaign_write/upsert · S3" then the batch id, actor, reason and one line per change
    /// (at most <paramref name="maxChanges"/>, then the call that lists the rest in <paramref name="campaignSlug"/>).
    /// </summary>
    public static void BatchLines(StringBuilder b, HistoryBatch batch, int maxChanges, string campaignSlug, bool heading = true)
    {
        var parts = new List<string> { batch.At };
        if (batch.Tool is not null)
        {
            parts.Add(batch.Tool);
        }

        if (batch.Session is { } session)
        {
            parts.Add("S" + Number(session));
        }

        if (heading)
        {
            b.Append("### ").Append(string.Join(" · ", parts)).Append('\n');
        }
        else
        {
            b.Append(string.Join(" · ", parts)).Append('\n');
        }

        b.Append("batch `").Append(batch.BatchId).Append("` · by ").Append(batch.Actor);
        if (batch.UndoOf is not null)
        {
            b.Append(" · undoes `").Append(batch.UndoOf).Append('`');
        }

        b.Append('\n');
        if (!string.IsNullOrWhiteSpace(batch.Reason))
        {
            b.Append("reason: ").Append(OneLine(batch.Reason)).Append('\n');
        }

        foreach (var change in batch.Changes.Take(maxChanges))
        {
            b.Append("- ").Append(OneLine(change.Text)).Append('\n');
        }

        if (batch.Changes.Count > maxChanges)
        {
            b.Append("- _… and ").Append(Number(batch.Changes.Count - maxChanges))
                .Append(" more changes; campaign_history {\"action\": \"batch\", \"batch_id\": \"").Append(batch.BatchId)
                .Append("\", \"campaign\": \"").Append(campaignSlug).Append("\"} lists them all._\n");
        }
    }

    /// <summary>
    /// What an undo in <paramref name="campaignSlug"/> did, or (dry run) would do. <paramref name="reversed"/> is the batch
    /// it reverses, rendered as history renders it (old and new values by handle), because the engine's own list says only
    /// which rows it reversed ("campaign row name (update reversed)"), and a dry run exists so the user can see what will
    /// change back before it does.
    ///
    /// <para>
    /// The undo of the batch that created the campaign removes the campaign itself, and with it every place a redo could be
    /// looked up in; printing the usual redo call there would promise a call that can only fail, so the result says the
    /// campaign is gone and how to make it again instead.
    /// </para>
    /// <para>
    /// An undo that makes text readable to the players (a forbidden word, a true name they do not use) carries the same
    /// <c>## Warnings (n)</c> section a write's result does, dry run included, ahead of the reversed batch, so the author
    /// reads it before the table does (and can undo the undo) even when the output cap cuts the batch's list.
    /// </para>
    /// </summary>
    public static string Undo(UndoWriteResult result, HistoryBatch reversed, string campaignSlug)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(reversed);
        var b = new StringBuilder();
        var what = result.WasRedo ? "Redo (undo of an undo)" : "Undo";
        var changes = $"{Number(result.RowsReversed)} logged change{(result.RowsReversed == 1 ? string.Empty : "s")}";
        var removesCampaign = CreatedTheCampaign(reversed);
        b.Append("# ").Append(result.DryRun ? "Dry run: " + what.ToLowerInvariant() : what).Append(" of batch ").Append(result.UndoneBatchId).Append("\n\n");
        if (result.DryRun)
        {
            b.Append("Nothing was changed. Without dry_run this reverses the batch's ").Append(changes).Append(" as a new batch.\n");
            if (removesCampaign)
            {
                b.Append("That batch created the campaign ").Append(campaignSlug).Append(" itself, so the undo removes the campaign; it cannot be ")
                    .Append("redone afterwards, only created again with campaign {\"action\": \"create\"}.\n");
            }
        }
        else if (removesCampaign)
        {
            b.Append("Reversed the batch's ").Append(changes).Append(" as a new batch `").Append(result.UndoBatchId).Append("`.\n")
                .Append("That batch created the campaign ").Append(campaignSlug).Append(" itself, so the campaign is gone and there is no ")
                .Append("campaign left to redo it in. To have it back, create it again with campaign {\"action\": \"create\"}.\n");
        }
        else
        {
            b.Append("Reversed the batch's ").Append(changes).Append(" as a new batch `").Append(result.UndoBatchId).Append("`.\n")
                .Append("To put them back (redo): ").Append(UndoCall(result.UndoBatchId!, campaignSlug)).Append(".\n");
        }

        // The player-text check's warnings, rendered exactly as every write result renders them (an undo can show the
        // players text as any write can: see HistoryWriter). An undo has no ops, so no "ops item" positions. They come
        // before the reversed batch, which can run to MaxChangesPerBatch lines: the output cap cuts from the end.
        WriteMarkdown.AppendWarnings(b, result.Warnings, opLabel: null);

        b.Append(result.DryRun ? "\n## The batch it would reverse\n" : "\n## The batch it reversed\n");
        BatchLines(b, reversed, MaxChangesPerBatch, campaignSlug, heading: false);

        if (result.Consequences.Count > 0)
        {
            b.Append(result.DryRun ? "\n## What would follow\n" : "\n## What followed\n");
            foreach (var consequence in result.Consequences)
            {
                b.Append("- ").Append(OneLine(consequence.Message)).Append('\n');
            }
        }

        return CampaignMarkdownText.Cap(b.ToString(),
            $"campaign_history {{\"action\": \"batch\", \"batch_id\": \"{result.UndoneBatchId}\", \"campaign\": \"{campaignSlug}\"}} lists the reversed batch's changes");
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Number(long value) => value.ToString("N0", Invariant);
}
