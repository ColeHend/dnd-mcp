using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// The <c>campaign</c> tool's markdown: the campaign list, one campaign's record, and what create, update and use did.
///
/// <para>
/// <b>Writes end the same way everywhere:</b> the batch id in full and the exact undo call (contract §3.10), or for a dry
/// run a plain "nothing was written". A model that has just created the wrong campaign then has the fix in the same
/// result, and a batch id is never shortened (a prefix can stop being unique as batches accumulate). The undo call names
/// the campaign (<see cref="HistoryMarkdown.UndoCall"/>): an update of a campaign that is not the current one would
/// otherwise print an undo that fails when sent as printed.
/// </para>
/// <para>
/// <b>Changed fields are named as the tool's arguments</b> (<c>current_location</c>, not the column
/// <c>current_location_id</c>), so the model can match what it sent to what changed.
/// </para>
/// <para>
/// <b>The record is the author's.</b> A campaign's settings and its summary text are the author's notes, so a non-author
/// <c>get</c> shows only the public facts of the campaign (name, role, ruleset, status, DM) and the party, character and
/// location links the reader filtered for that view.
/// </para>
/// </summary>
internal static class CampaignMarkdown
{
    // What a cut write result says. A write result is a handful of lines (a campaign's warnings are one or two), so the cap
    // is a backstop that keeps every result bounded, not a limit any real result reaches. Like every call a campaign result
    // prints, it names the campaign (and the batch, when there is one): "campaign_history {"action": "batch"}" alone has no
    // batch to list and goes to whichever campaign is current.
    private static string WriteCapHint(string campaignSlug, string? batchId) => batchId is null
        ? $"campaign {{\"action\": \"get\", \"campaign\": \"{campaignSlug}\"}} shows the campaign"
        : $"campaign_history {{\"action\": \"batch\", \"batch_id\": \"{batchId}\", \"campaign\": \"{campaignSlug}\"}} lists what the batch changed";

    // What a cut get says: the summary of the same campaign, for the same view (a player view's summary is its own, filtered
    // like the get; without campaign the call reads whichever campaign is current).
    private static string GetCapHint(string campaignSlug, CampaignView view) => view.AuthorView
        ? $"use campaign {{\"action\": \"summary\", \"campaign\": \"{campaignSlug}\"}} for the state of play"
        : $"use campaign {{\"action\": \"summary\", \"perspective\": \"{view.Perspective.Text}\", \"campaign\": \"{campaignSlug}\"}} for the state of play";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Every campaign; <paramref name="defaultId"/> is the one a call without <c>campaign</c> uses (§3.7), marked so the
    /// model knows which campaign its next call will touch. <paramref name="characters"/> maps a campaign's id to its player
    /// character's handle (<c>my_character</c>), printed in its own column.
    ///
    /// <para>
    /// <b>Why the character's handle is listed.</b> A character perspective is a handle (<c>character:&lt;slug&gt;</c>), and
    /// the slug comes from the name given at create: "Belmakor Silverwind" is <c>character:belmakor-silverwind</c>. A fresh
    /// session that guessed <c>character:belmakor</c> spent its first perspective call on a refusal; the list is the first
    /// thing such a session reads, so the handle is there.
    /// </para>
    /// </summary>
    public static string List(IReadOnlyList<CampaignRow> campaigns, string? defaultId, IReadOnlyDictionary<string, string>? characters = null)
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        var b = new StringBuilder();
        b.Append("# Campaigns");
        if (campaigns.Count == 0)
        {
            b.Append("\n\nNo campaigns yet. Create one: campaign {\"action\": \"create\", \"name\": \"…\", \"role\": \"player\" or \"dm\", ")
                .Append("\"ruleset\": \"2014\", \"2024\" or \"mixed\"}.\n");
            return b.ToString();
        }

        b.Append(" (").Append(Number(campaigns.Count)).Append(")\n\n");
        b.Append("| Campaign | Name | Role | My character | Ruleset | Status | |\n|---|---|---|---|---|---|---|\n");
        foreach (var c in campaigns)
        {
            b.Append("| `").Append(c.Slug).Append("` | ").Append(CampaignMarkdownText.Cell(c.Name)).Append(" | ").Append(c.Role).Append(" | ")
                .Append(characters is not null && characters.TryGetValue(c.Id, out var character) ? $"`{character}`" : "—").Append(" | ")
                .Append(c.Ruleset).Append(" | ").Append(c.Status).Append(" | ").Append(c.Id == defaultId ? "calls use this one" : string.Empty)
                .Append(" |\n");
        }

        b.Append('\n').Append(defaultId is null
            ? "No campaign is chosen: pass campaign (a slug) to each call, or choose one with campaign {\"action\": \"use\", \"campaign\": \"<slug>\"}."
            : "Calls without campaign use the marked one; switch with campaign {\"action\": \"use\", \"campaign\": \"<slug>\"}.").Append('\n');
        b.Append("Each campaign is also a resource: campaign://<slug>/summary and campaign://<slug>/threads.\n");
        return CampaignMarkdownText.Cap(b.ToString(), "the list is long; pass campaign to the other tools directly");
    }

    /// <summary>One campaign's record for one view.</summary>
    /// <param name="campaign">The stored row.</param>
    /// <param name="links">The party, character and location links as the view may see them (from the summary reader).</param>
    /// <param name="view">Whose view.</param>
    /// <param name="isDefault">Calls without <c>campaign</c> use this one.</param>
    public static string Get(CampaignRow campaign, CampaignSummaryView links, CampaignView view, bool isDefault)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(view);
        var b = new StringBuilder();
        b.Append("# ").Append(campaign.Name).Append(" (`").Append(campaign.Slug).Append("`)\n");
        if (view.Banner is not null)
        {
            b.Append(view.Banner).Append('\n');
        }

        b.Append('\n');
        b.Append("- **Role:** ").Append(campaign.Role).Append(campaign.Role == CampaignValues.Roles.Dm ? " (you run it)" : " (you play in it)").Append('\n');
        b.Append("- **Ruleset:** ").Append(campaign.Ruleset).Append(" · **Status:** ").Append(campaign.Status).Append('\n');
        if (!string.IsNullOrWhiteSpace(campaign.DmName))
        {
            b.Append("- **DM:** ").Append(campaign.DmName).Append('\n');
        }

        if (links.Party is not null)
        {
            b.Append("- **Party:** ").Append(EntityMarkdown.Link(links.Party)).Append('\n');
        }

        if (links.MyCharacter is not null)
        {
            b.Append("- **My character:** ").Append(EntityMarkdown.Link(links.MyCharacter)).Append('\n');
        }

        if (links.CurrentLocation is not null)
        {
            b.Append("- **Where:** ").Append(EntityMarkdown.Link(links.CurrentLocation)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(campaign.CurrentIngame))
        {
            b.Append("- **In-game date:** ").Append(campaign.CurrentIngame).Append('\n');
        }

        if (view.AuthorView)
        {
            if (!string.IsNullOrWhiteSpace(campaign.Settings) && campaign.Settings.Trim() != "{}")
            {
                b.Append("- **Settings:** `").Append(CampaignMarkdownText.Excerpt(campaign.Settings, 600)).Append("`\n");
            }

            b.Append("- **Created:** ").Append(campaign.CreatedAt).Append(" · **Updated:** ").Append(campaign.UpdatedAt).Append('\n');
            b.Append("- **Default for calls without campaign:** ").Append(isDefault ? "yes" : "no").Append('\n');
            if (!string.IsNullOrWhiteSpace(campaign.SummaryMd))
            {
                b.Append("\n## Summary\n").Append(CampaignMarkdownText.Excerpt(campaign.SummaryMd.Replace("\n", " ", StringComparison.Ordinal), 4_000)).Append('\n');
            }
        }

        // The summary, threads, entity and session resources are the author's view; another view is pointed only at its own
        // knowledge page, so a model drafting in character is never sent to read the author's pages.
        b.Append('\n').Append(view.AuthorView
            ? ResourcesLine(campaign.Slug)
            : $"What this view knows, on one page: `campaign://{campaign.Slug}/knowledge/{view.Perspective.Text}`.").Append('\n');
        return CampaignMarkdownText.Cap(b.ToString(), GetCapHint(campaign.Slug, view));
    }

    /// <summary>
    /// What a create did (or, dry run, would do). <paramref name="savedAsActive"/> is false when the campaign became this
    /// session's current campaign but could not be saved as the one later sessions start with (the create itself stands).
    /// </summary>
    public static string Created(CampaignCreateResult result, bool savedAsActive = true)
    {
        ArgumentNullException.ThrowIfNull(result);
        var c = result.Campaign;
        var b = new StringBuilder();
        b.Append("# ").Append(result.DryRun ? "Dry run: would create campaign " : "Created campaign ").Append(c.Name).Append(" (`").Append(c.Slug).Append("`)\n\n");
        b.Append(c.Role).Append(" campaign · ").Append(c.Ruleset).Append(" rules");
        if (!string.IsNullOrWhiteSpace(c.DmName))
        {
            b.Append(" · DM: ").Append(c.DmName);
        }

        b.Append('\n');
        b.Append("- **Party:** `").Append(result.Party).Append("` (party knowledge and membership hang on it)\n");
        if (result.MyCharacter is not null)
        {
            b.Append("- **My character:** `").Append(result.MyCharacter).Append("`, a member of the party\n");
        }

        Warnings(b, result.Warnings);
        if (result.DryRun)
        {
            b.Append("\nNothing was written. Run the same call without dry_run to create it.\n");
            return CampaignMarkdownText.Cap(b.ToString(), "nothing was written; run the call without dry_run to create it");
        }

        b.Append(savedAsActive
            ? "\nIt is now the current campaign: campaign tools use it when campaign is omitted.\n"
            : "\nIt is now the current campaign in this session: campaign tools use it when campaign is omitted. It could not be saved as " +
              $"the campaign later sessions start with; campaign {{\"action\": \"use\", \"campaign\": \"{c.Slug}\"}} saves it.\n");
        Batch(b, result.BatchId, c.Slug);
        b.Append("\nNext: add people, places and facts with campaign_write, and sessions with campaign_session.\n");
        b.Append(ResourcesLine(c.Slug)).Append('\n');
        return CampaignMarkdownText.Cap(b.ToString(), WriteCapHint(c.Slug, result.BatchId));
    }

    /// <summary>What an update did (or, dry run, would do).</summary>
    public static string Updated(CampaignUpdateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var c = result.Campaign;
        var b = new StringBuilder();
        b.Append("# ").Append(result.DryRun ? "Dry run: would update campaign " : "Updated campaign ").Append(c.Name).Append(" (`").Append(c.Slug).Append("`)\n\n");
        if (result.ChangedFields.Count == 0)
        {
            b.Append("Nothing changed: the values given are the ones already stored.\n");
            Warnings(b, result.Warnings);
            return CampaignMarkdownText.Cap(b.ToString(), WriteCapHint(c.Slug, null));
        }

        b.Append("Changed: ").Append(string.Join(", ", result.ChangedFields.Select(WireName))).Append(".\n");
        if (result.ChangedFields.Contains("name"))
        {
            b.Append("The slug stays `").Append(c.Slug).Append("` (slugs never change, so handles and resource URIs keep working).\n");
        }

        Warnings(b, result.Warnings);
        if (result.DryRun)
        {
            b.Append("\nNothing was written. Run the same call without dry_run to apply it.\n");
            return CampaignMarkdownText.Cap(b.ToString(), WriteCapHint(c.Slug, null));
        }

        Batch(b, result.BatchId, c.Slug);
        return CampaignMarkdownText.Cap(b.ToString(), WriteCapHint(c.Slug, result.BatchId));
    }

    // The campaign columns an update changes, by the argument that changes them.
    private static string WireName(string field) => field switch
    {
        "current_location_id" => "current_location",
        "my_character_id" => "my_character",
        _ => field,
    };

    /// <summary>What <c>use</c> did.</summary>
    public static string Used(CampaignRow campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        var b = new StringBuilder();
        b.Append("# Using campaign ").Append(campaign.Name).Append(" (`").Append(campaign.Slug).Append("`)\n\n");
        b.Append(campaign.Role).Append(" campaign · ").Append(campaign.Ruleset).Append(" rules · ").Append(campaign.Status).Append('\n');
        b.Append("\nCampaign tools now use it when campaign is omitted, in this session and in sessions started later");
        if (campaign.Ruleset is CampaignValues.Rulesets.R2014 or CampaignValues.Rulesets.R2024)
        {
            b.Append("; tools that take an edition default to its ").Append(campaign.Ruleset).Append(" rules");
        }

        b.Append(".\ncampaign {\"action\": \"summary\", \"campaign\": \"").Append(campaign.Slug).Append("\"} shows where things stand.\n");
        return CampaignMarkdownText.Cap(b.ToString(), WriteCapHint(campaign.Slug, null));
    }

    /// <summary>"Resources: campaign://belmakor/summary, …" with the deep forms.</summary>
    public static string ResourcesLine(string slug) =>
        $"Resources: `campaign://{slug}/summary`, `campaign://{slug}/threads`, `campaign://{slug}/party`; also readable by URI: `campaign://{slug}/entity/<ref>`, " +
        $"`campaign://{slug}/session/<n>`, `campaign://{slug}/knowledge/<perspective>`.";

    /// <summary>"Batch `…`. To undo it: campaign_history {…, "campaign": "…"}."</summary>
    public static void Batch(StringBuilder b, string? batchId, string campaignSlug)
    {
        if (batchId is null)
        {
            return;
        }

        b.Append("\nBatch `").Append(batchId).Append("`. To undo it: ").Append(HistoryMarkdown.UndoCall(batchId, campaignSlug)).Append(".\n");
    }

    /// <summary>The warnings of a write, one line each, advisories marked.</summary>
    public static void Warnings(StringBuilder b, IReadOnlyList<WriteWarning> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        b.Append("\n## Warnings\n");
        foreach (var warning in warnings)
        {
            b.Append("- ").Append(warning.Severity == WarningSeverities.Advisory ? "(advisory) " : string.Empty)
                .Append(warning.Message.Replace('\n', ' ')).Append('\n');
        }
    }

    private static string Number(long value) => value.ToString("N0", Invariant);
}
