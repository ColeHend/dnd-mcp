using System.Text;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// <c>campaign_knowledge</c>'s writing actions (record, reveal, retract): one row per knowledge row written, with who it
/// is for, then the batch, warnings and consequences exactly as <see cref="WriteMarkdown"/> renders them for
/// <c>campaign_write</c>.
///
/// <para>
/// <b>Why one row per knower and target.</b> A reveal to the party of a secret writes a row for each of its gated facts
/// and one for the secret itself; the author must see each of them (and that a re-reveal was "unchanged") to trust what
/// the table now knows. The gate warnings are what matter most here: <c>reveal</c> is where a gated fact reaches the party,
/// and the warnings say which condition was unmet when it did (it is applied anyway).
/// </para>
/// </summary>
internal static class KnowledgeMarkdown
{
    /// <summary>The result of record, reveal or retract.</summary>
    public static string FormatWrite(CampaignRow campaign, string action, WriteResult result)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(result);
        var b = new StringBuilder();
        var count = WriteMarkdown.Count(result.Applied.Count, "row", "rows");
        b.Append("# ")
            .Append(result.DryRun
                ? $"Dry run: campaign_knowledge {action}, {count} ({campaign.Slug}): nothing written"
                : $"campaign_knowledge {action}: {count} ({campaign.Slug})")
            .Append("\n\n");
        WriteMarkdown.AppendBatch(b, campaign.Slug, result.BatchId, result.DryRun, result.SessionNumber, WriteMarkdown.Changed(result));

        b.Append("\n| Target | Knower | Outcome | Changed |\n|---|---|---|---|\n");
        var rows = 0;
        foreach (var applied in result.Applied.Take(WriteMarkdown.MaxRows).TakeWhile(_ => b.Length < WriteMarkdown.Budget / 2))
        {
            rows++;
            b.Append("| ").Append(CampaignMarkdownText.Cell(applied.Code is { Length: > 0 } code ? $"{applied.Ref} · {code}" : applied.Ref))
                .Append(" | ").Append(CampaignMarkdownText.Cell(applied.Knower))
                .Append(" | ").Append(applied.Outcome)
                .Append(" | ").Append(CampaignMarkdownText.Cell(WriteMarkdown.ChangedCell(applied.ChangedFields)))
                .Append(" |\n");
        }

        WriteMarkdown.AppendMore(b, rows, result.Applied.Count, "rows (all written as reported)");
        WriteMarkdown.AppendWarnings(b, result.Warnings, opLabel: null);
        WriteMarkdown.AppendConsequences(b, result.Consequences);
        return CampaignMarkdownText.Cap(b.ToString().TrimEnd() + "\n",
            $"the {action} itself is complete; give fewer targets or knowers per call for a shorter result");
    }
}
