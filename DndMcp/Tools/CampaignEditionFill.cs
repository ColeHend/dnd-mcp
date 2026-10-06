using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Formatting;
using DndMcp.Hosting;

namespace DndMcp.Tools;

/// <summary>
/// One balance call's use of the active campaign's ruleset (contract §9): fills the edition into the builds and archetype
/// entries the call left without one, and remembers whether it did, so the result can say so once.
///
/// <para>
/// <b>Why the host fills the spec</b> rather than the Domain taking a default: builds and archetypes fall back to 2024 deep
/// in the Domain (<c>CompiledBuild</c>, <c>ArchetypeCatalog</c>), past every call site that could know the campaign, and
/// the Domain's own 2024 fallback is pinned by its tests. Giving the spec an explicit edition before the Domain sees it
/// changes no Domain rule and threads nothing through <c>BuildResolver</c>; <see cref="BuildSpec"/> and
/// <see cref="CombatantSpec"/> are records for exactly this copy.
/// </para>
/// <para>
/// <b>Lazy and explicit-first.</b> campaigns.db is read only when some spec lacks an edition, at most once per call; a spec
/// that names its edition is returned as it is (the same instance), so explicit arguments always win and a call that names
/// every edition never touches the file. With no campaign active (or a mixed one) nothing is filled and no note is
/// written, so the spec reaches the Domain exactly as before. When campaigns.db exists but could not be read, nothing is
/// filled either, but the note says so (<see cref="CampaignDefaultNotes.Unreadable"/>): the 2024 rules the Domain then
/// falls back to may not be the campaign's.
/// </para>
/// <para>
/// <b>The call's own editions before the campaign's.</b> With a campaign active, a spec that names no edition takes the
/// edition the call named elsewhere (<c>given</c>: balance_compare's baseline for its variant; balance_simulate's fight
/// edition, else its first party entry's) and only then the campaign's ruleset. Filling it from the campaign alone would
/// mix editions the call never mixed: a 2014 campaign's variant beside a baseline that says 2024 makes the comparison's Δ
/// include an edition change, and a build under an explicit 2014 fight would follow 2024 rules while the same call without
/// the edition gives 2014. Only the campaign's own ruleset is noted; an edition taken from the call is the call's.
/// </para>
/// </summary>
internal sealed class CampaignEditionFill
{
    private readonly CampaignService? _campaigns;
    private readonly ResolvedCampaignDefaults? _chosen;
    private bool _read;
    private string? _edition;
    private string? _campaignSlug;
    private string? _unreadablePath;

    /// <summary>The AMBIENT campaign's ruleset (the current one, else the active one), read lazily: a call that chose none.</summary>
    public CampaignEditionFill(CampaignService campaigns)
    {
        _campaigns = campaigns;
    }

    /// <summary>
    /// The ruleset of the campaign the call CHOSE (<see cref="CampaignArgumentDefaults"/>: named with <c>campaign</c>, or
    /// resolved for an encounter or a <c>character</c> entry), never the active one: an encounter of one campaign filled
    /// with another's rules is a wrong answer that looks right. Its note is that campaign's
    /// (<see cref="ResolvedCampaignDefaults.EditionNote"/>: the shared wording when it is also the ambient campaign, else
    /// naming it); a mixed campaign fills nothing, as the ambient form does.
    /// </summary>
    public CampaignEditionFill(ResolvedCampaignDefaults chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        _chosen = chosen;
    }

    /// <summary>The campaign supplied at least one edition in this call.</summary>
    public bool Used { get; private set; }

    /// <summary>
    /// The note naming the campaign, when it supplied an edition; the note that its settings could not be read, when the
    /// call asked for them (some spec named no edition) and campaigns.db failed; else null.
    /// </summary>
    public string? Note => Used ? (_chosen is { } chosen ? chosen.EditionNote(_edition!) : CampaignDefaultNotes.Edition(_edition!, _campaignSlug!))
        : _unreadablePath is { } path ? CampaignDefaultNotes.Unreadable(path, DslValues.Editions.Default)
        : null;

    /// <summary>
    /// <paramref name="build"/> with an edition when it names none and a campaign is active: <paramref name="given"/> (an
    /// edition the call named elsewhere, canonical), else the campaign's; otherwise the same instance.
    /// </summary>
    public BuildSpec Fill(BuildSpec build, string? given = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        return build.Edition is null && EditionFor(given) is { } edition ? build with { Edition = edition } : build;
    }

    /// <summary>
    /// The entries with an edition filled in where they name none (as <see cref="Fill(BuildSpec, string?)"/>): a build's
    /// goes inside the build (the Domain refuses an entry edition beside a build), an archetype's on the entry. Monster
    /// entries are left alone: their lookup falls back to the campaign through <see cref="MonsterFallback"/>, after the
    /// fight's and the party's editions.
    /// </summary>
    public CombatantSpec[] Fill(CombatantSpec[] entries, string? given = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Select(entry => entry switch
        {
            null => entry!,
            { Build: { Edition: null } build } when EditionFor(given) is { } edition => entry with { Build = build with { Edition = edition } },
            { Archetype: not null, Build: null, Monster: null, Edition: null } when EditionFor(given) is { } edition => entry with { Edition = edition },
            _ => entry,
        }).ToArray();
    }

    /// <summary>
    /// The edition a monster is looked up in when neither its entry, the fight nor the party names one: the campaign's
    /// (noted), else 2024.
    /// </summary>
    public string MonsterFallback() => Campaign() is { } edition ? Use(edition) : DslValues.Editions.Default;

    /// <summary><paramref name="notes"/> with the campaign's note first when it supplied an edition; else the same list.</summary>
    public IReadOnlyList<string> WithNote(IReadOnlyList<string> notes) => Note is { } note ? [note, .. notes] : notes;

    /// <summary>
    /// The edition for a spec that names none: null with no campaign active (nothing is filled, so the no-campaign answer
    /// is untouched); else <paramref name="given"/>; else the campaign's ruleset, which marks the note as due.
    /// </summary>
    private string? EditionFor(string? given) => Campaign() is { } campaign ? given ?? Use(campaign) : null;

    private string? Campaign()
    {
        if (!_read && _chosen is { } chosen)
        {
            (_edition, _campaignSlug) = (chosen.Edition, chosen.Row.Slug);
            _read = true;
        }

        if (!_read)
        {
            var reading = _campaigns!.ReadDefaults();
            if (reading.Edition is { } edition)
            {
                (_edition, _campaignSlug) = (edition, reading.Values!.Slug);
            }

            _unreadablePath = reading.UnreadablePath;
            _read = true;
        }

        return _edition;
    }

    private T Use<T>(T value)
    {
        Used = true;
        return value;
    }
}
