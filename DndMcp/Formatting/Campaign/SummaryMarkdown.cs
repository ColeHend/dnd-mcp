using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// A campaign at a glance (<c>campaign {action: "summary"}</c> and <c>campaign://&lt;slug&gt;/summary</c>), and its
/// quests and threads (<c>campaign://&lt;slug&gt;/threads</c>).
///
/// <para>
/// <b>The author's part is its own block.</b> <see cref="CampaignSummaryView.Author"/> (secrets and their statuses,
/// proposed inventions, lean and withheld question counts, settings, changes since the last session) is null for every
/// other view, and only then are those headings printed: a player view never gets a "Secrets" heading, a "withheld"
/// count or a "0 secrets" line, any of which would tell the table that the author holds something back. The open-question
/// count a player view sees already counts withheld questions as open (the reader's rule), so it cannot be subtracted from
/// anything to find them.
/// </para>
/// <para>
/// <b>"No sessions played" is the author's line only.</b> Another view gets the sessions it may see; when it may see none
/// (the public, in a campaign whose sessions are party-visible) saying "none played yet" would be false, and there is no
/// true wording for "played, but not for you" that says nothing about what is hidden, so it says nothing.
/// </para>
/// <para>
/// <b>A view's hints read the same view.</b> Where a list is cut, the author is pointed at the threads resource and a
/// search; every other view only at a search with its own perspective, named in full with the campaign (<see cref="ViewSearch"/>).
/// The threads resource is the author's view, and a search or get without a perspective is too: a model drafting for the
/// party that followed its own summary's hint would read author-only quests, true names and secret text.
/// </para>
/// </summary>
internal static class SummaryMarkdown
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Party members named on the party line. The line is one markdown line, so without a limit a large roster would be cut
    /// whole by the output cap, taking everything after it (threads, clocks) with it.
    /// </summary>
    public const int MaxMembersListed = 30;

    /// <summary>Running clocks, secrets and inventions listed before "… and N more": each list is unbounded in the data.</summary>
    public const int MaxListed = 30;

    /// <summary>
    /// The quest and thread statuses listed first (open work). Spelled here because the vocabulary names them only inside
    /// its per-kind sets; CampaignResourceTests pins each (and <see cref="DormantStatus"/>) to both kinds' sets, so a renamed
    /// status cannot silently move every open thread into "Closed".
    /// </summary>
    public static readonly IReadOnlyList<string> OpenStatuses = ["active", "open", "blocked"];

    /// <summary>The quest and thread status listed on its own, between open and closed work.</summary>
    public const string DormantStatus = "dormant";

    /// <summary>The summary for one view.</summary>
    public static string Format(CampaignSummaryView s, CampaignView view)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(view);
        var b = new StringBuilder();
        b.Append("# ").Append(s.Name).Append(" (`").Append(s.Slug).Append("`)\n");
        if (view.Banner is not null)
        {
            b.Append(view.Banner).Append('\n');
        }

        b.Append('\n').Append(s.Role).Append(" campaign · ").Append(s.Ruleset == CampaignValues.Rulesets.Mixed ? "mixed rules" : s.Ruleset + " rules").Append(" · ").Append(s.Status);
        if (!string.IsNullOrWhiteSpace(s.DmName))
        {
            b.Append(" · DM: ").Append(s.DmName);
        }

        b.Append('\n');
        if (s.MyCharacter is not null)
        {
            b.Append("- **My character:** ").Append(EntityMarkdown.Link(s.MyCharacter)).Append('\n');
        }

        if (s.Party is not null)
        {
            b.Append("- **Party:** ").Append(EntityMarkdown.Link(s.Party));
            if (s.PartyMembers.Count > 0)
            {
                b.Append(": ").Append(string.Join(", ", s.PartyMembers.Take(MaxMembersListed).Select(EntityMarkdown.Link)));
                if (s.PartyMembers.Count > MaxMembersListed)
                {
                    b.Append(", … and ").Append(Number(s.PartyMembers.Count - MaxMembersListed))
                        .Append(" more (").Append(view.AuthorView ? "campaign_search with kinds [\"character\"]" : ViewSearch(s.Slug, view, "\"character\""))
                        .Append(" lists them)");
                }
            }

            b.Append('\n');
        }

        if (s.CurrentLocation is not null)
        {
            b.Append("- **Where:** ").Append(EntityMarkdown.Link(s.CurrentLocation)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(s.CurrentIngame))
        {
            b.Append("- **In-game date:** ").Append(s.CurrentIngame).Append('\n');
        }

        if (s.LiveSession is not null)
        {
            b.Append("- **Live now:** ").Append(Session(s.LiveSession)).Append('\n');
        }

        if (s.LastPlayed is not null)
        {
            b.Append("- **Last played:** ").Append(Session(s.LastPlayed)).Append('\n');
            if (!string.IsNullOrWhiteSpace(s.LastRecapOpening))
            {
                foreach (var line in s.LastRecapOpening.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
                {
                    b.Append("  > ").Append(line.Trim()).Append('\n');
                }
            }
        }
        else if (s.LiveSession is null && view.AuthorView)
        {
            b.Append("- **Sessions:** none played yet\n");
        }

        b.Append("\n## Open quests and threads\n");
        if (s.OpenThreads.Count == 0)
        {
            b.Append("None open.\n");
        }
        else
        {
            foreach (var thread in s.OpenThreads)
            {
                b.Append("- ").Append(EntityMarkdown.Link(thread.Entity)).Append(thread.Status is null ? string.Empty : " · " + thread.Status).Append('\n');
            }

            if (CampaignMarkdownText.More(s.OpenThreads.Count, s.OpenThreadsTotal, view.AuthorView
                    ? $"campaign://{s.Slug}/threads or campaign_search with kinds [\"quest\", \"thread\"] lists them all"
                    : ViewSearch(s.Slug, view, "\"quest\", \"thread\"") + " lists them all") is { } more)
            {
                b.Append(more).Append('\n');
            }
        }

        b.Append("\n**Open questions:** ").Append(Number(s.OpenQuestions)).Append('\n');
        if (s.RunningClocks.Count > 0)
        {
            b.Append("\n## Running clocks\n");
            foreach (var clock in s.RunningClocks.Take(MaxListed))
            {
                b.Append("- ").Append(EntityMarkdown.Link(clock.Clock)).Append(": ").Append(Number(clock.Filled)).Append('/')
                    .Append(Number(clock.Segments)).Append(' ').Append(clock.Unit).Append(clock.Segments == 1 ? string.Empty : "s").Append('\n');
            }

            if (CampaignMarkdownText.More(MaxListed, s.RunningClocks.Count,
                    (view.AuthorView ? "campaign_search with kinds [\"clock\"]" : ViewSearch(s.Slug, view, "\"clock\"")) + " lists them all") is { } more)
            {
                b.Append(more).Append('\n');
            }
        }

        if (s.Author is { } author)
        {
            Author(b, s, author);
        }

        return CampaignMarkdownText.Cap(b.ToString(), view.AuthorView
            ? $"read campaign://{s.Slug}/threads or campaign_search for the full lists"
            : $"campaign_search {{\"campaign\": \"{s.Slug}\", \"perspective\": \"{view.Perspective.Text}\"}} with kinds gives the full lists");
    }

    /// <summary>
    /// The search a non-author view is pointed at for the rest of a list: its own perspective and the campaign named, so it
    /// reads exactly what this view may see of this campaign, whatever campaign is current when the model sends it.
    /// </summary>
    private static string ViewSearch(string campaignSlug, CampaignView view, string kinds) =>
        $"campaign_search {{\"campaign\": \"{campaignSlug}\", \"kinds\": [{kinds}], \"perspective\": \"{view.Perspective.Text}\"}}";

    private static void Author(StringBuilder b, CampaignSummaryView s, AuthorCampaignSummary a)
    {
        b.Append("\n## Author\n");
        b.Append("- **Questions:** ").Append(Number(a.LeanQuestions)).Append(" lean, ").Append(Number(a.WithheldQuestions)).Append(" withheld\n");
        if (!string.IsNullOrWhiteSpace(a.Settings) && a.Settings.Trim() != "{}")
        {
            b.Append("- **Settings:** `").Append(CampaignMarkdownText.Excerpt(a.Settings, 600)).Append("`\n");
        }

        if (a.Secrets.Count > 0)
        {
            b.Append("\n### Secrets\n");
            foreach (var secret in a.Secrets.Take(MaxListed))
            {
                b.Append("- ").Append(EntityMarkdown.Link(secret.Entity)).Append(" · ").Append(secret.Status ?? "no status").Append('\n');
            }

            if (CampaignMarkdownText.More(MaxListed, a.Secrets.Count, "campaign_search with kinds [\"secret\"] lists them all") is { } more)
            {
                b.Append(more).Append('\n');
            }
        }

        if (a.ProposedInventions.Count > 0)
        {
            b.Append("\n### Inventions (accept / strike)\n");
            foreach (var invention in a.ProposedInventions.Take(MaxListed))
            {
                b.Append("- ").Append(invention.Code is null ? string.Empty : invention.Code + " ").Append('`').Append(invention.Ref).Append("`: ")
                    .Append(CampaignMarkdownText.Excerpt(invention.Label, 160)).Append('\n');
            }

            if (CampaignMarkdownText.More(MaxListed, a.ProposedInventions.Count, "campaign_get reads any of them by its code") is { } more)
            {
                b.Append(more).Append('\n');
            }
        }

        b.Append("\n### Since the last session\n");
        if (a.ChangesSinceLastSession == 0)
        {
            b.Append("No changes since ").Append(s.LastPlayed is null ? "the campaign began" : $"session {Number(s.LastPlayed.Number)} ended").Append(".\n");
            return;
        }

        b.Append(Number(a.ChangesSinceLastSession)).Append(" batch").Append(a.ChangesSinceLastSession == 1 ? string.Empty : "es")
            .Append(" since ").Append(s.LastPlayed is null ? "the campaign began" : $"session {Number(s.LastPlayed.Number)} ended")
            .Append("; the newest:\n");
        foreach (var batch in a.RecentChanges)
        {
            b.Append('\n');
            HistoryMarkdown.BatchLines(b, batch, maxChanges: 6, s.Slug, heading: false);
        }

        // With no played session every batch counts, and "since" without a session lists them all; with one, the feed from
        // that session on also holds the session's own batches, which the count above leaves out, so the line says so.
        b.Append('\n').Append(s.LastPlayed is null
            ? $"campaign_history {{\"action\": \"since\", \"campaign\": \"{s.Slug}\"}} lists every change."
            : $"campaign_history {{\"action\": \"since\", \"session\": {s.LastPlayed.Number.ToString(Invariant)}, \"campaign\": \"{s.Slug}\"}} lists " +
              $"every change from session {Number(s.LastPlayed.Number)} on, that session's own included.").Append('\n');
    }

    /// <summary>
    /// The quests and threads of a campaign, open work first (the <c>threads</c> resource), from the reader's listing;
    /// <paramref name="more"/> is true when the listing was cut before its end.
    /// </summary>
    public static string Threads(string campaignName, string campaignSlug, IReadOnlyList<EntityHit> threads, bool more)
    {
        ArgumentNullException.ThrowIfNull(threads);
        var b = new StringBuilder();
        b.Append("# ").Append(campaignName).Append(": quests and threads\n");
        if (threads.Count == 0)
        {
            b.Append("\nNo quests or threads yet. Add one with campaign_write (upsert, kind \"quest\" or \"thread\").\n");
            return b.ToString();
        }

        var groups = new[]
        {
            ("Open", threads.Where(t => t.Status is not null && OpenStatuses.Contains(t.Status)).ToList()),
            ("Dormant", threads.Where(t => t.Status == DormantStatus).ToList()),
            ("Closed", threads.Where(t => t.Status is not null && !OpenStatuses.Contains(t.Status) && t.Status != DormantStatus).ToList()),
            ("No status", threads.Where(t => t.Status is null).ToList()),
        };
        foreach (var (title, list) in groups)
        {
            if (list.Count == 0)
            {
                continue;
            }

            b.Append("\n## ").Append(title).Append(" (").Append(Number(list.Count)).Append(")\n");
            foreach (var t in list)
            {
                b.Append("- ").Append(t.DisplayName).Append(" (`").Append(t.Ref).Append("`) · ").Append(t.Kind)
                    .Append(t.Status is null ? string.Empty : " · " + t.Status).Append('\n');
            }
        }

        if (more)
        {
            b.Append("\n_More quests and threads exist; campaign_search with kinds [\"quest\", \"thread\"] pages through all of them._\n");
        }

        b.Append("\ncampaign_get with a ref shows a quest's objectives, relations and facts.\n");
        return CampaignMarkdownText.Cap(b.ToString(), $"campaign_search with kinds [\"quest\", \"thread\"] on campaign {campaignSlug} pages through them");
    }

    // "Session 3: The Kraken (`session:3`) · 2026-08 (approx) · arc The Iron Guts job (`arc:iron-guts-job`)"
    private static string Session(SessionSummary session)
    {
        var b = new StringBuilder();
        b.Append(session.Title).Append(" (`").Append(session.Ref).Append("`)");
        if (!string.IsNullOrWhiteSpace(session.PlayedOn))
        {
            b.Append(" · ").Append(session.PlayedOn);
            if (session.PlayedOnPrecision is not (CampaignValues.DatePrecisions.Day or CampaignValues.DatePrecisions.Unknown))
            {
                b.Append(" (").Append(session.PlayedOnPrecision).Append(')');
            }
        }

        if (session.Arc is not null)
        {
            b.Append(" · arc ").Append(EntityMarkdown.Link(session.Arc));
        }

        return b.ToString();
    }

    private static string Number(long value) => value.ToString("N0", Invariant);
}
