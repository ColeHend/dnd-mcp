using System.Globalization;
using V = DndMcp.Domain.Campaign.CampaignValues.Visibilities;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// How one entity appears to one perspective: whether it appears at all, whether it is disguised, the name it goes by
/// there, and the names that perspective uses for it.
/// </summary>
/// <param name="Visible">The perspective may see the entity. When false nothing about it may be rendered, not even a count.</param>
/// <param name="Disguised">
/// The perspective knows the entity only under a name that is not one of its own (or does not recognise it): it shows
/// only <see cref="DisplayName"/>, the kind, an <c>e:&lt;seq&gt;</c> ref, the party aliases it shares with this
/// perspective (none when it is not recognised; see <see cref="EntityViews"/>) and the facts the perspective knows about it.
/// </param>
/// <param name="DisplayName">The name to print; empty when not <see cref="Visible"/>.</param>
/// <param name="AuthorView">The view is the author's: the true name and <c>kind:slug</c> are always shown.</param>
public sealed record EntityView(bool Visible, bool Disguised, string DisplayName, bool AuthorView = false)
{
    /// <summary>The view of an entity the perspective may not see.</summary>
    public static EntityView Hidden { get; } = new(false, false, string.Empty);

    /// <summary>
    /// Whether the view's perspective may see party-visible aliases (<see cref="Audience.Sees"/> of <c>party</c>): the
    /// party, the table, the dm and current members do; the public, a character outside the party and one who left do
    /// not. <see cref="EntityViews.For"/> sets it; a view made by hand sees public aliases only, the strictest reading.
    /// </summary>
    public bool SeesPartyAliases { get; init; }

    /// <summary>
    /// The names this view uses for the entity, as <see cref="CampaignText.KeyWithoutArticle"/> keys, each once, in order:
    /// the name it is shown (never the "an unrecognized …" stand-in) and the aliases it may print
    /// (<see cref="EntityViews.ShownAliases"/>). The read path's search and knowledge check use it as "a name this
    /// perspective uses": a disguised entity is found by, and may be sung under, its known_as and the party aliases it
    /// shares, never its true name, a public epithet of who it really is, or (unrecognised) any alias at all. Empty for a
    /// hidden entity.
    /// </summary>
    public IReadOnlyList<string> UsedNames { get; init; } = [];

    /// <summary>Value equality, <see cref="UsedNames"/> compared item by item (a record compares a list by reference).</summary>
    public bool Equals(EntityView? other) =>
        other is not null &&
        Visible == other.Visible &&
        Disguised == other.Disguised &&
        AuthorView == other.AuthorView &&
        SeesPartyAliases == other.SeesPartyAliases &&
        string.Equals(DisplayName, other.DisplayName, StringComparison.Ordinal) &&
        UsedNames.SequenceEqual(other.UsedNames, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Visible);
        hash.Add(Disguised);
        hash.Add(AuthorView);
        hash.Add(SeesPartyAliases);
        hash.Add(DisplayName, StringComparer.Ordinal);
        foreach (var name in UsedNames)
        {
            hash.Add(name, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// The perspective filter for one entity (contract §3.2): visibility, the disguised view, the aliases a perspective may
/// see and perspective-safe refs.
///
/// <para>
/// <b>Why a disguised view exists:</b> the column filter keeps secret text out of search, but the entity's own name,
/// summary and slug are in player-visible columns. The Protector is party-visible as "the advisor in Serret"; his true
/// name, his summary ("a Keras fragment") and his slug (<c>character:protector</c>) all say what the party must not
/// know. So an entity the perspective knows under another name (or does not recognise) shows only that name, its kind,
/// <c>e:&lt;seq&gt;</c> and the aliases the perspective may see. A known name that matches the entity's own (after
/// dropping a leading article: "the old king" is "The Old King") is not a disguise. A known name that matches only one of
/// its public or party aliases still is: an entity named "Keras" that the party knows by its party alias "the old king"
/// would otherwise print "Keras", its summary and <c>character:keras</c> to the party, though the alias is all they know
/// (orchestrator decision on stage-1 finding 13).
/// </para>
/// <para>
/// <b>Aliases are per perspective</b> (review L11). An alias is shown to the views <see cref="Audience.Sees"/> admits
/// for its visibility: a public alias to everyone, a party alias to the party, the table, the dm and current members, a
/// restricted or author alias to none. Treating every non-author view alike put a public entity's party-only alias
/// ("Duke Orsino" for The Masked Duke) on the public's page, into its search and into a <c>kind:slug</c> ref, and told
/// the check that a crowd knew it.
/// </para>
/// <para>
/// <b>A disguise keeps the party's own names, and only those</b> (review U01). A party alias is a name the record says
/// the party itself uses for the entity, like the known_as of its row; hiding it made the check flag "the old king", a
/// party alias of an entity the party knows as "the ancient sorcerer king", as a name the party does not use (a model
/// deleted the alias because of it). So a view that sees party aliases keeps them under a known_as disguise. Three things
/// stay hidden, each because it would tell the view who the disguised figure is: a public alias (the world's name for
/// who she really is: "the Lich Queen" beside "the veiled woman" unmasks her), every alias of an entity the view does
/// not recognise (<c>unrecognized</c>: it met the figure without knowing who it was, so no name of the entity is its),
/// and any alias that contains the true name ("King Keras", "Keras's ghost": the leak rule forbids the true name
/// anywhere).
/// </para>
/// <para>
/// <b>Why refs change:</b> <c>kind:slug</c> spells the true name (<c>character:keras</c>). A non-author view gets it only
/// when the slug spells nothing but the name it already shows (the slug derived from that name, with or without its
/// article: "The Old King" admits <c>the-old-king</c> and <c>old-king</c>); otherwise <c>e:&lt;seq&gt;</c>, which says
/// nothing. A collision suffix (<c>keras-2</c>) also falls back to <c>e:</c>, since it reveals that another entity shares
/// the name.
/// </para>
/// </summary>
public static class EntityViews
{
    /// <summary>
    /// The view of an entity for <paramref name="who"/>, given the verdict (<see cref="KnowledgeVerdicts.Evaluate"/>)
    /// on the entity itself.
    /// </summary>
    /// <param name="aliases">Every alias of the entity with its own visibility.</param>
    public static EntityView For(
        PerspectiveContext who,
        string kind,
        string name,
        string visibility,
        IReadOnlyList<(string Alias, string Visibility)> aliases,
        KnowledgeVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(verdict);
        if (who.IsAuthorView)
        {
            return new EntityView(true, false, name, AuthorView: true)
            {
                SeesPartyAliases = true,
                UsedNames = Keys([name, .. aliases.Select(a => a.Alias)]),
            };
        }

        if (visibility == V.Author || !verdict.Knows)
        {
            return EntityView.Hidden;
        }

        var knownAs = string.IsNullOrWhiteSpace(verdict.KnownAs) ? null : verdict.KnownAs.Trim();
        var unrecognised = verdict.State == CampaignValues.KnowledgeStates.Unrecognized;
        var otherName = knownAs is not null && !IsOwnName(knownAs, name);
        var seesParty = Audience.Sees(who, V.Party);
        if (unrecognised || otherName)
        {
            // The stand-in "an unrecognized character" is nobody's name. Only a recognised entity shares aliases, and only
            // party ones that do not contain its true name (class summary).
            IEnumerable<string> shownName = knownAs is null ? [] : [knownAs];
            var shared = unrecognised || !seesParty ? [] : SharedAliases(name, aliases);
            return new EntityView(true, true, knownAs ?? "an unrecognized " + kind)
            {
                SeesPartyAliases = seesParty,
                UsedNames = Keys([.. shownName, .. shared]),
            };
        }

        return new EntityView(true, false, name)
        {
            SeesPartyAliases = seesParty,
            UsedNames = Keys([name, .. aliases.Where(a => Admits(seesParty, a.Visibility)).Select(a => a.Alias)]),
        };
    }

    /// <summary>
    /// The ref to print for an entity in <paramref name="view"/>: <c>kind:slug</c> for the author view and for a
    /// non-disguised entity whose slug spells only its shown name; otherwise <c>e:&lt;seq&gt;</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The view is hidden: a hidden entity has no ref in that perspective.</exception>
    public static string Ref(EntityView view, string kind, string slug, long seq)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!view.Visible)
        {
            throw new InvalidOperationException("A hidden entity has no ref for this perspective; it must not be rendered.");
        }

        var bySlug = kind + ":" + slug;
        if (view.AuthorView)
        {
            return bySlug;
        }

        var safe = !view.Disguised &&
                   (slug == CampaignSlugs.From(view.DisplayName, kind) ||
                    slug == CampaignSlugs.From(CampaignText.KeyWithoutArticle(view.DisplayName), kind));
        return safe ? bySlug : "e:" + seq.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The aliases a view may print: all of them (with their visibility) for the author view; for any other view the
    /// ones its perspective may see (public ones always, party ones when <see cref="EntityView.SeesPartyAliases"/>; never
    /// restricted or author ones); of a disguised entity only the party aliases it shares (see the class summary: none
    /// when unrecognised, never one containing its true name, never the name it is already shown under); none of a hidden
    /// one. Pass the aliases <see cref="For"/> was given: of a disguised entity, only those among its
    /// <see cref="EntityView.UsedNames"/> are shown.
    /// </summary>
    public static IReadOnlyList<(string Alias, string Visibility)> ShownAliases(
        EntityView view,
        IReadOnlyList<(string Alias, string Visibility)> aliases)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(aliases);
        if (view.AuthorView)
        {
            return aliases;
        }

        if (!view.Visible)
        {
            return [];
        }

        if (!view.Disguised)
        {
            return aliases.Where(a => Admits(view.SeesPartyAliases, a.Visibility)).ToList();
        }

        var shownName = CampaignText.KeyWithoutArticle(view.DisplayName);
        return aliases
            .Where(a => a.Visibility == V.Party && view.SeesPartyAliases &&
                        CampaignText.KeyWithoutArticle(a.Alias) is var key && key != shownName &&
                        view.UsedNames.Contains(key, StringComparer.Ordinal))
            .ToList();
    }

    // Only the entity's own name un-disguises: an alias, even a party one, is not the name the entity's own text uses.
    private static bool IsOwnName(string knownAs, string name) =>
        CampaignText.KeyWithoutArticle(knownAs) == CampaignText.KeyWithoutArticle(name);

    // The aliases a recognised disguised entity shares with a view that sees party aliases: its party ones that do not
    // contain its true name as a whole (in any case, with or without a possessive: "King Keras", "Keras's ghost"). The
    // name scanner's own matching, so this never disagrees with the knowledge check about whether a text says the name.
    private static IEnumerable<string> SharedAliases(string name, IReadOnlyList<(string Alias, string Visibility)> aliases)
    {
        var trueName = new NameScanner([new NameEntry(name, name)]);
        return aliases.Where(a => a.Visibility == V.Party && trueName.Scan(a.Alias).Count == 0).Select(a => a.Alias);
    }

    // Audience.Sees for an alias's visibility, given whether the perspective sees party rows: an unknown value is hidden.
    private static bool Admits(bool seesParty, string visibility) => visibility == V.Public || (visibility == V.Party && seesParty);

    // Names as keys without their article, each once, in order; a name with no letters or digits names nothing.
    private static IReadOnlyList<string> Keys(IEnumerable<string> names)
    {
        var keys = new List<string>();
        foreach (var name in names)
        {
            var key = CampaignText.KeyWithoutArticle(name);
            if (key.Length > 0 && !keys.Contains(key, StringComparer.Ordinal))
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}
