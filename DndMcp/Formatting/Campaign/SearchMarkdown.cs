using System.Globalization;
using System.Text;
using DndMcp.Repository.Campaign.Read;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// campaign_search's markdown: one numbered line per entity (name, kind, status, ref) with the snippet under it, then the
/// facts, then how to get the next page.
///
/// <para>
/// <b>No trace of what a view cannot see.</b> <see cref="CampaignSearch"/> counts and pages only what the perspective may
/// see, and its snippets are built from fields the perspective may see. This formatter adds nothing on top: an empty result
/// is "Nothing matches" for every view (never "nothing visible to this view", which says hidden things exist), the totals
/// are the reader's, and the author-only columns (truth, canon status, known by) are printed only when the reader filled
/// them, which it does for the author view alone.
/// </para>
/// <para>
/// <b>The footer reads the same view.</b> It says how to read the hits in full: for the author, campaign_get with the refs;
/// for every other view, campaign_get with this perspective and the campaign named, and with the session when the search
/// was as of one (review LR02). campaign_get without a perspective is the author's view, so a model drafting for the party
/// that followed a bare "campaign_get with refs" on a disguised hit would get the true name and the secret text; one
/// without the session reads today's view, holding what the view learned after that session.
/// </para>
/// </summary>
internal static class SearchMarkdown
{
    private const string CapHint = "pass a smaller limit, or narrow with kinds, statuses or tags";

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The search's result, titled by the query (or the filters of a listing).</summary>
    /// <param name="result">The reader's page.</param>
    /// <param name="view">Whose view (banner).</param>
    /// <param name="campaignSlug">The campaign searched.</param>
    /// <param name="query">The query as given (echoed shortened); null for a listing.</param>
    /// <param name="filters">"kinds character, quest" and the like, for the title; empty for none.</param>
    public static string Format(SearchResult result, CampaignView view, string campaignSlug, string? query, string filters)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(view);
        var b = new StringBuilder();
        b.Append("# ").Append(query is null ? $"{campaignSlug}: listing" : $"Search {campaignSlug}: \"{CampaignMarkdownText.Echo(query)}\"");
        if (filters.Length > 0)
        {
            b.Append(" (").Append(filters).Append(')');
        }

        b.Append('\n');
        if (view.Banner is not null)
        {
            b.Append(view.Banner).Append('\n');
        }

        if (view.AsOfSession is { } session)
        {
            b.Append($"_As of the end of session {Number(session)}; words are matched against today's text._").Append('\n');
        }

        if (result.Entities.Count == 0 && result.Facts.Count == 0)
        {
            b.Append('\n').Append(query is null
                ? "Nothing matches these filters."
                : "Nothing matches. Try fewer or different words, a prefix such as \"sorcer*\", or leave query out to list by kinds.").Append('\n');
            return b.ToString();
        }

        if (result.PartialMatch)
        {
            b.Append("\n_Nothing has every word, so these match any of them._\n");
        }

        var number = 0;
        if (result.Entities.Count > 0)
        {
            b.Append("\n## Entities\n");
            foreach (var e in result.Entities)
            {
                number++;
                b.Append(Number(number)).Append(". **").Append(e.DisplayName).Append("** · ").Append(e.Kind);
                if (e.Status is not null)
                {
                    b.Append(" · ").Append(e.Status);
                }

                b.Append(" · `").Append(e.Ref).Append("`\n");
                if (!string.IsNullOrWhiteSpace(e.Snippet))
                {
                    b.Append("   ").Append(OneLine(e.Snippet));
                    if (e.SnippetFrom is not null)
                    {
                        b.Append(" _(").Append(e.SnippetFrom.Replace('_', ' ')).Append(")_");
                    }

                    b.Append('\n');
                }
            }
        }

        if (result.Facts.Count > 0)
        {
            b.Append("\n## Facts\n");
            foreach (var f in result.Facts)
            {
                number++;
                b.Append(Number(number)).Append(". ").Append(EntityMarkdown.FactRef(f.Ref, f.Code)).Append(": ")
                    .Append(CampaignMarkdownText.Excerpt(OneLine(f.Text), 400)).Append('\n');
                var author = new List<string>();
                if (f.Truth is not null)
                {
                    author.Add("truth " + f.Truth);
                }

                if (f.CanonStatus is not null)
                {
                    author.Add("canon " + f.CanonStatus);
                }

                if (f.KnownBy is not null)
                {
                    author.Add(f.KnownBy.Count == 0 ? "known by no one yet" : "known by " + string.Join(", ", f.KnownBy));
                }

                if (author.Count > 0)
                {
                    b.Append("   ").Append(string.Join(" · ", author)).Append('\n');
                }
            }
        }

        b.Append('\n').Append(
            result.NextCursor is not null
                ? $"This page shows {Number(number)} of {Number(result.Total)} results; next page: the same call with cursor \"{result.NextCursor}\"."
                : number < result.Total
                    ? $"The last page: {Number(number)} of {Number(result.Total)} results."
                    : $"{Number(result.Total)} result{(result.Total == 1 ? string.Empty : "s")}.").Append('\n');
        b.Append(view.AuthorView
            ? "campaign_get with refs reads any of these in full.\n"
            : $"campaign_get {{\"refs\": [...], {view.ViewArguments}, \"campaign\": \"{campaignSlug}\"}} reads any of these in full.\n");
        return CampaignMarkdownText.Cap(b.ToString(), CapHint);
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Number(long value) => value.ToString("N0", Invariant);
}
