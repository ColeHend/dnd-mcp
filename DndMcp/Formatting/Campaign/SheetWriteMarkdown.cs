using System.Text;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// What a <c>campaign_character</c> write did (contract §7.1), for the author (the only writer): the title, the batch
/// paragraph, each field with its value before and after, the notes, the reminders with the calls that resolve them, the
/// dice the call rolled and the warnings.
///
/// <para>
/// <b>The batch paragraph only when a batch was logged</b> (§7.1, §15 H1). A routed action (applied to the live fight,
/// unlogged until the fight's write-back) and a call that changed nothing have no batch: an undo hint there would name a
/// batch that does not exist and fail. Their notes already say which ("Applied to the live fight …", "Nothing changed.").
/// A dry run says so first, as every write result does (<see cref="WriteMarkdown.AppendBatch"/>'s sentence): nothing was
/// written and no batch exists. When a batch was logged its id and the undo call (naming the campaign) come right after
/// the title, before anything that can grow, so the output cap can never cut the only handle on the undo.
/// </para>
/// <para>
/// <b>Values are shown as stored</b> (a slot's <c>{"max":4,"used":2}</c>, a resource's object), each cut at
/// <see cref="MaxValueChars"/>: the change list is for checking what the call did, and the sheet itself is one
/// <c>get</c> away.
/// </para>
/// </summary>
internal static class SheetWriteMarkdown
{
    /// <summary>Characters of one value before and after a change; a new sheet's notes or a long list are cut here.</summary>
    public const int MaxValueChars = 200;

    /// <summary>Changes listed before "… and N more" (a new sheet writes every column it has).</summary>
    public const int MaxChanges = 60;

    private const string CapHint = "the change is complete as reported; campaign_character get shows the sheet";

    /// <summary>The result of one <c>campaign_character</c> write action.</summary>
    public static string Format(CampaignRow campaign, CharacterWriteResult result)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(result);
        var b = new StringBuilder("# ");
        b.Append(result.DryRun ? "Dry run: " : string.Empty)
            .Append("campaign_character ").Append(result.Action).Append(": ").Append(result.Name)
            .Append(" (`").Append(result.Ref).Append("`, ").Append(campaign.Slug).Append(')')
            .Append(result.DryRun ? ": nothing written" : string.Empty)
            .Append("\n\n");
        if (result.DryRun || result.BatchId is not null)
        {
            WriteMarkdown.AppendBatch(b, campaign.Slug, result.BatchId, result.DryRun, result.SessionNumber, changed: true);
            b.Append('\n');
        }

        if (result.Created)
        {
            b.Append(result.DryRun ? "Would create the sheet.\n" : "Created the sheet.\n");
        }

        if (result.Changes.Count > 0)
        {
            foreach (var change in result.Changes.Take(MaxChanges))
            {
                b.Append("- ").Append(change.Key is null ? change.Field : $"{change.Field} {OneLine(change.Key)}").Append(": ")
                    .Append(Value(change.Before, "none")).Append(" → ").Append(Value(change.After, "removed")).Append('\n');
            }

            if (result.Changes.Count > MaxChanges)
            {
                b.Append("- … and ").Append((result.Changes.Count - MaxChanges).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" more fields\n");
            }

            b.Append('\n');
        }

        foreach (var note in result.Notes)
        {
            b.Append(OneLine(note)).Append('\n');
        }

        if (result.Rolls.Count > 0)
        {
            b.Append("\n## Rolls\n");
            foreach (var roll in result.Rolls)
            {
                b.Append("- `").Append(roll.Expression).Append("` [").Append(string.Join(", ", roll.Faces)).Append("] = ")
                    .Append(roll.Total.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" · logged as \"").Append(roll.Label).Append('"').Append(roll.Secret ? " (secret)" : string.Empty).Append('\n');
            }
        }

        if (result.Reminders.Count > 0)
        {
            b.Append("\n## Reminders\n");
            foreach (var reminder in result.Reminders)
            {
                b.Append("- ").Append(OneLine(reminder.Text));
                if (reminder.Call is { } call)
                {
                    b.Append(' ').Append(call);
                }

                b.Append('\n');
            }
        }

        WriteMarkdown.AppendWarnings(b, result.Warnings, opLabel: null);
        return CampaignMarkdownText.Cap(b.ToString().TrimEnd() + "\n", CapHint);
    }

    // A stored value, one line, cut at MaxValueChars; "none"/"removed" for no value (an empty text column, notes_md's
    // default, is no value too: "notes_md:  → …" read as a typo).
    private static string Value(string? value, string absent)
    {
        if (string.IsNullOrEmpty(value))
        {
            return absent;
        }

        var line = OneLine(value);
        return line.Length <= MaxValueChars ? line : line[..CampaignMarkdownText.WholeCharacters(line, MaxValueChars)] + "…";
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();
}
