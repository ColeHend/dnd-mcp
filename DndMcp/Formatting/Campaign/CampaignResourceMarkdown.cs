using System.Globalization;
using System.Text;
using DndMcp.Domain.Campaign;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// The markdown of the <c>campaign://</c> resources that no tool result already shares: the campaign list, one session
/// (<c>/session/&lt;n&gt;</c>, the author's view) and what one perspective knows (<c>/knowledge/&lt;perspective&gt;</c>).
///
/// <para>
/// <b>The knowledge page is the one resource read for a non-author view</b>, and it is what a model loads before writing
/// in a character's voice (the <c>in_character</c> prompt reads it). So it is built only from
/// <see cref="KnowledgeLedger.KnownTo"/>, which is filtered like every non-author read (display names, perspective-safe
/// refs, the knower's own phrasings), under the banner, and never from the author-facing ledger. Its "known by" column is
/// printed only when the reader filled it, which it does for the author view alone.
/// </para>
/// <para>
/// <b>The session page budgets its long parts</b> as campaign_session get does: the recap (up to 50,000 characters, often one
/// paragraph) at <see cref="SessionMarkdown.MaxRecap"/>, the prep at <see cref="SessionMarkdown.MaxPrep"/>, each live-log note
/// at a line, each cut saying how much of how much is shown. Appended whole, a one-paragraph recap longer than the output cap
/// was one line the cap could only drop, so the page showed no recap at all.
/// </para>
/// </summary>
internal static class CampaignResourceMarkdown
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary><c>campaign://list</c>: every campaign with the URIs it can be read at.</summary>
    public static string List(IReadOnlyList<CampaignRow> campaigns, string? defaultId)
    {
        ArgumentNullException.ThrowIfNull(campaigns);
        var b = new StringBuilder("# Campaigns\n");
        if (campaigns.Count == 0)
        {
            b.Append("\nNo campaigns yet. The campaign tool creates one (action \"create\").\n");
            return b.ToString();
        }

        foreach (var c in campaigns)
        {
            b.Append("\n## ").Append(c.Name).Append(" (`").Append(c.Slug).Append("`)").Append(c.Id == defaultId ? " · the current campaign" : string.Empty).Append('\n');
            b.Append(c.Role).Append(" campaign · ").Append(c.Ruleset).Append(" rules · ").Append(c.Status).Append('\n');
            b.Append("- `campaign://").Append(c.Slug).Append("/summary`: the state of play\n");
            b.Append("- `campaign://").Append(c.Slug).Append("/threads`: quests and threads\n");
            b.Append("- `campaign://").Append(c.Slug).Append("/entity/<ref>`, `/session/<n>`, `/knowledge/<perspective>` (e.g. `/knowledge/party`)\n");
        }

        return CampaignMarkdownText.Cap(b.ToString(), "the campaign tool lists campaigns too (action \"list\")");
    }

    /// <summary>What one perspective knows, from every page the reader gave (<paramref name="more"/>: it was cut).</summary>
    public static string Knowledge(string campaignName, CampaignView view, IReadOnlyList<KnownEntity> entities, IReadOnlyList<KnownFact> facts, bool more)
    {
        ArgumentNullException.ThrowIfNull(view);
        var b = new StringBuilder();
        b.Append("# ").Append(campaignName).Append(": what ").Append(view.Perspective.Text).Append(" knows\n");
        if (view.Banner is not null)
        {
            b.Append(view.Banner).Append('\n');
        }

        if (entities.Count == 0 && facts.Count == 0)
        {
            b.Append("\nNothing is recorded yet.\n");
            return b.ToString();
        }

        if (entities.Count > 0)
        {
            b.Append("\n## Who and what\n");
            foreach (var e in entities)
            {
                b.Append("- ").Append(e.Name).Append(" (`").Append(e.Ref).Append("`) · ").Append(e.Kind);
                if (e.State is not null)
                {
                    b.Append(" · ").Append(e.State);
                }

                if (e.LearnedSession is { } learned)
                {
                    b.Append(" · S").Append(Number(learned));
                }

                if (e.KnownBy is not null)
                {
                    b.Append(" · known by ").Append(e.KnownBy.Count == 0 ? "no one yet" : string.Join(", ", e.KnownBy));
                }

                b.Append('\n');
            }
        }

        if (facts.Count > 0)
        {
            b.Append("\n## Facts\n");
            foreach (var f in facts)
            {
                b.Append("- ").Append(EntityMarkdown.FactRef(f.Ref, f.Code)).Append(": ").Append(CampaignMarkdownText.Excerpt(OneLine(f.Text), 400));
                if (f.State is not null)
                {
                    b.Append(" · ").Append(f.State);
                }

                if (f.LearnedSession is { } learned)
                {
                    b.Append(" · S").Append(Number(learned));
                }

                if (f.KnownBy is not null)
                {
                    b.Append(" · known by ").Append(f.KnownBy.Count == 0 ? "no one yet" : string.Join(", ", f.KnownBy));
                }

                b.Append('\n');
            }
        }

        if (more)
        {
            b.Append("\n_More is recorded than fits here; campaign_search with this perspective finds the rest by words._\n");
        }

        return CampaignMarkdownText.Cap(b.ToString(), "campaign_search with this perspective finds the rest by words");
    }

    /// <summary>One session of campaign <paramref name="campaignSlug"/> as the author reads it (the <c>/session/&lt;n&gt;</c> resource).</summary>
    public static string Session(string campaignName, string campaignSlug, SessionDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var s = detail.Session;
        var b = new StringBuilder();
        b.Append("# ").Append(campaignName).Append(": ").Append(s.Title).Append(" (`").Append(s.Ref).Append("`)\n\n");
        b.Append(s.Status);
        if (!string.IsNullOrWhiteSpace(s.PlayedOn))
        {
            b.Append(" · ").Append(s.PlayedOn).Append(s.PlayedOnPrecision is CampaignValues.DatePrecisions.Day or CampaignValues.DatePrecisions.Unknown ? string.Empty : $" ({s.PlayedOnPrecision})");
        }

        if (s.Arc is not null)
        {
            b.Append(" · arc ").Append(EntityMarkdown.Link(s.Arc));
        }

        if (!string.IsNullOrWhiteSpace(detail.IngameStart) || !string.IsNullOrWhiteSpace(detail.IngameEnd))
        {
            b.Append(" · in game ").Append(detail.IngameStart ?? "?").Append(" → ").Append(detail.IngameEnd ?? "?");
        }

        b.Append('\n');
        if (detail.Attendance.Count > 0)
        {
            b.Append("- **Attendance:** ").Append(string.Join(", ", detail.Attendance.Select(a =>
                EntityMarkdown.Link(a.Character) + (a.Present ? string.Empty : " (absent)") +
                (string.IsNullOrWhiteSpace(a.Note) ? string.Empty : $" ({OneLine(a.Note)})")))).Append('\n');
        }
        else if (!detail.AttendanceRecorded)
        {
            b.Append("- **Attendance:** not recorded\n");
        }

        var getCall = $"campaign_session {{\"action\": \"get\", \"session\": {s.Number.ToString(Invariant)}, \"campaign\": \"{campaignSlug}\"}}";
        if (!string.IsNullOrWhiteSpace(detail.Recap))
        {
            b.Append("\n## Recap\n").Append(SessionMarkdown.Bounded(detail.Recap, SessionMarkdown.MaxRecap,
                $"campaign_session {{\"action\": \"recap\", \"session\": {s.Number.ToString(Invariant)}, \"campaign\": \"{campaignSlug}\"}} shows more")).Append('\n');
        }

        if (detail.Author is { } author)
        {
            if (!string.IsNullOrWhiteSpace(author.PrepMd))
            {
                b.Append("\n## Prep\n").Append(SessionMarkdown.Bounded(author.PrepMd, SessionMarkdown.MaxPrep, "the prep is the session's prep_md")).Append('\n');
            }

            if (author.LiveLog.Count > 0)
            {
                b.Append("\n## Live log\n");
                foreach (var note in author.LiveLog.TakeLast(100))
                {
                    b.Append("- ").Append(note.At is null ? string.Empty : note.At + " ").Append(CampaignMarkdownText.Excerpt(OneLine(note.Text), 400)).Append('\n');
                }
            }

            if (author.Changes.Count > 0)
            {
                b.Append("\n## What changed in this session\n");
                foreach (var batch in author.Changes.Take(20))
                {
                    b.Append('\n');
                    HistoryMarkdown.BatchLines(b, batch, maxChanges: 8, campaignSlug, heading: false);
                }

                if (CampaignMarkdownText.More(20, author.Changes.Count,
                        $"campaign_history {{\"action\": \"since\", \"session\": {s.Number.ToString(Invariant)}, \"campaign\": \"{campaignSlug}\"}} " +
                        "lists them all") is { } more)
                {
                    b.Append(more).Append('\n');
                }
            }
        }

        if (detail.DiceRolls.Count > 0)
        {
            b.Append("\n## Dice\n");
            foreach (var roll in detail.DiceRolls.TakeLast(50))
            {
                b.Append("- ").Append(roll.Label is null ? string.Empty : roll.Label + ": ").Append('`').Append(roll.Expression).Append("` = ")
                    .Append(Number(roll.Total)).Append(roll.Success is { } success ? success ? " (success)" : " (failure)" : string.Empty)
                    .Append(roll.Secret == true ? " (secret)" : string.Empty).Append('\n');
            }

            if (CampaignMarkdownText.More(Math.Min(50, detail.DiceRolls.Count), detail.DiceRollsTotal, "the earlier rolls are in campaigns.db") is { } more)
            {
                b.Append(more).Append('\n');
            }
        }

        return CampaignMarkdownText.Cap(b.ToString(), getCall + " reads the session too");
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Number(long value) => value.ToString("N0", Invariant);
}
