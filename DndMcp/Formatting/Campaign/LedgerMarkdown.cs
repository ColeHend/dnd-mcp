using System.Globalization;
using System.Text;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// <c>campaign_knowledge ledger</c>: who knows what, as a table (rows: entities, then facts; columns: perspectives).
///
/// <para>
/// <b>The kinds of "no".</b> The cells keep apart what the verdicts keep apart, because they call for different things
/// at the table: <c>no record</c> (nobody wrote down whether they know: ask, or record it), <c>not met</c> (an entity with
/// no record: the party has not met it), <c>not in play (planned)</c> (the fact has not happened yet, whatever its rows
/// say), <c>author only</c> (rows say that view knows a row the author keeps from every other view: author visibility is
/// absolute, so no read of that view shows it; change the visibility, or the rows) and an explicit <c>unaware</c> (it IS
/// recorded that they do not know). A ledger that printed them all as "no", or the author-only one as "knows", would hide
/// the difference between an unrecorded session and a secret still holding. The cell text is the reader's
/// (<see cref="LedgerCell.Text"/>), so the ledger and every other read word a verdict the same way, and the legend names
/// each of them.
/// </para>
/// <para>
/// <b>Author-facing</b>: rows carry true names and statements; the ledger is the author's view of every perspective at
/// once. <b>Bounded</b>: up to 100 rows by 12 columns can pass the output cap, so rows are added while they fit and the
/// rest are counted (with the reader's own total when it cut the ledger first), never cut mid-row.
/// </para>
/// </summary>
internal static class LedgerMarkdown
{
    /// <summary>Characters of a row label (an entity name or a fact's statement).</summary>
    public const int MaxLabel = 90;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The ledger as a markdown table.</summary>
    public static string Format(CampaignRow campaign, Ledger ledger)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(ledger);
        var b = new StringBuilder();
        b.Append("# Knowledge ledger: ").Append(campaign.Slug);
        if (ledger.AsOfSession is { } asOf)
        {
            b.Append(", as of session ").Append(asOf.ToString(Invariant));
        }

        b.Append("\n\nRows use true names (author view). A cell is that perspective's verdict: its state, the name it uses and ")
            .Append("the session it learned it; \"no record\" means nothing is recorded, \"not met\" an entity with no record, ")
            .Append("\"not in play\" a fact or entity that has not happened yet, \"author only\" a row only the author view ")
            .Append("sees, whatever that perspective's knowledge rows say.\n\n");
        if (ledger.Rows.Count == 0)
        {
            b.Append("Nothing to show: no rows.\n");
            return b.ToString();
        }

        b.Append("| Row | ").Append(string.Join(" | ", ledger.Columns.Select(CampaignMarkdownText.Cell))).Append(" |\n")
            .Append("|---|").Append(string.Concat(Enumerable.Repeat("---|", ledger.Columns.Count))).Append('\n');

        // Leave room for the "and N more" line and the cap's own note.
        var budget = CampaignMarkdownText.MaxChars - 400;
        var shown = 0;
        foreach (var row in ledger.Rows)
        {
            var line = Row(row, ledger.Columns);
            if (b.Length + line.Length > budget)
            {
                break;
            }

            b.Append(line);
            shown++;
        }

        var total = Math.Max(ledger.TotalRows, ledger.Rows.Count);
        if (total > shown)
        {
            b.Append("\n_… and ").Append((total - shown).ToString(Invariant))
                .Append(" more rows; narrow with about, facts or perspectives._\n");
        }

        return CampaignMarkdownText.Cap(b.ToString(), "narrow with about, facts or perspectives");
    }

    private static string Row(LedgerRow row, IReadOnlyList<string> columns)
    {
        var label = CampaignMarkdownText.Excerpt(row.Label, MaxLabel);
        var head = row.IsEntity ? $"**{row.Ref}** {label}" : $"{row.Ref} {label}";
        var line = new StringBuilder("| ").Append(CampaignMarkdownText.Cell(head));
        foreach (var column in columns)
        {
            var cell = row.Cells.FirstOrDefault(c => c.Perspective == column);
            line.Append(" | ").Append(CampaignMarkdownText.Cell(cell?.Text));
        }

        return line.Append(" |\n").ToString();
    }
}
