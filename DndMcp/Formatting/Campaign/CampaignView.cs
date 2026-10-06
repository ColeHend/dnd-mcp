using System.Globalization;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;

namespace DndMcp.Formatting.Campaign;

/// <summary>
/// Whose view one campaign read is rendered for: the parsed perspective, whether it is the author's view, and the banner
/// line a non-author result starts with. Resolved once per call, before any reader runs, by every campaign tool and
/// resource that takes a perspective.
///
/// <para>
/// <b>Why resolve here and not only in the readers.</b> The readers filter by perspective, but the banner has to name the
/// perspective's own character ("character:belmakor (Belmakor Silverwind)"), and only the repository's
/// <see cref="KnowledgeLoader.Resolve"/> knows that name. Resolving through the same loader the readers use means an unknown
/// or deleted character is refused here with the loader's own message (suggesting only characters the party knows by their
/// own names), before any output exists, and the banner can never name a different entity than the view was filtered for.
/// </para>
/// <para>
/// <b>What breaks without it:</b> a banner built from the caller's text alone would say "character:e:7" with no name, and
/// one built from an author read of that entity could print a disguised entity's true name; <c>dm</c> in a DM campaign is
/// the author's view (<see cref="PerspectiveContext.IsAuthorView"/>) and must get no banner and no filtering.
/// </para>
/// <para>
/// <b>A read as of a session is a view too</b> (review LR01, LR02): the view carries the session, its character is
/// resolved as it stood then (the name it knew itself by, and whether a typed prefix completes), and every call a
/// non-author result prints to read more of the same view keeps the session. A hint without it read today's view, which
/// holds what the view learned after that session: the anachronism an as_of read exists to prevent.
/// </para>
/// </summary>
/// <param name="Perspective">The perspective as parsed (author when none was given).</param>
/// <param name="AuthorView">The author's view: everything, secret text included.</param>
/// <param name="Banner">The first line of a non-author result; null for the author's view.</param>
/// <param name="AsOfSession">The session the read is as of; null for now.</param>
internal sealed record CampaignView(Perspective Perspective, bool AuthorView, string? Banner, int? AsOfSession = null)
{
    /// <summary>The author's view (the default).</summary>
    public static CampaignView Author { get; } = new(Perspective.Author, true, null);

    /// <summary>
    /// The live-fight line of each character in the active fight from its sheet, by handle (<see cref="SheetLiveFight"/>):
    /// set by the tools that read an AUTHOR sheet now (fix F1, U04), so every sheet the read prints carries its line; null
    /// for every other read (another view, a past session, no sheet asked for).
    /// </summary>
    public IReadOnlyDictionary<string, string>? LiveFight { get; init; }

    /// <summary>
    /// This view with the live-fight lines of <paramref name="campaign"/> when it is the author's view of now; itself
    /// otherwise (a non-author view never reads the fight's numbers, contract §7.4, and a past read has no live fight).
    /// </summary>
    public CampaignView WithLiveFight(CampaignDatabase database, CampaignRow campaign) =>
        AuthorView && AsOfSession is null ? this with { LiveFight = SheetLiveFight.Read(database, campaign) } : this;

    /// <summary>
    /// The arguments that read this view again, for the calls a non-author result prints: the perspective, and the session
    /// when the read is as of one (<c>"perspective": "party", "as_of_session": 2</c>).
    /// </summary>
    public string ViewArguments =>
        $"\"perspective\": \"{Perspective.Text}\"" +
        (AsOfSession is { } session ? ", \"as_of_session\": " + session.ToString(CultureInfo.InvariantCulture) : string.Empty);

    /// <summary>
    /// Parses and resolves <paramref name="perspectiveText"/> against <paramref name="campaign"/>, as of
    /// <paramref name="asOfSession"/> when given. Null or blank is the author's view and opens nothing.
    /// </summary>
    /// <exception cref="DndInputException">A perspective that does not parse, or a character this campaign does not have (then).</exception>
    public static CampaignView Resolve(CampaignDatabase database, CampaignRow campaign, string? perspectiveText, int? asOfSession = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(campaign);
        var perspective = Perspective.Parse(perspectiveText);
        if (perspective.IsAuthor)
        {
            return Author with { AsOfSession = asOfSession };
        }

        // No file path in the message: a caller error reaches whoever drives the client, and the path says nothing it can act on.
        using var connection = database.TryOpenExisting() ?? throw new DndInputException(
            "There are no campaigns yet, so there is nothing to read. Create one with campaign {\"action\": \"create\"}.");
        var context = new KnowledgeLoader(connection, campaign).Resolve(perspective, asOfSession);
        return new CampaignView(perspective, context.IsAuthorView,
            CampaignMarkdownText.Banner(context.Perspective, context.IsAuthorView, context.CharacterName, typed: perspective), asOfSession);
    }
}
