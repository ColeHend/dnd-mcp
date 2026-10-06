using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Formatting;
using DndMcp.Hosting;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>encounter_difficulty</c>: how hard a fight is for a party, by the 2014 DMG's thresholds and multipliers, the 2024
/// XP budget, or both side by side. Thin by design: validate, resolve each monster to a CR and XP, call
/// <see cref="Encounter2014"/> / <see cref="Encounter2024"/>, render with <see cref="EncounterMarkdown"/>.
///
/// <para>
/// <b>CR and XP come from srd.db</b> (<see cref="SrdMonsterChallenge"/>), which carries the curated corrections, never
/// from the vendored files. A monster that is not in the SRD (most of the Monster Manual) is given by <c>cr</c>; the
/// not-found message says so, because a model told only "no monster is named Beholder" guesses a CR from memory and
/// reports it as found.
/// </para>
/// <para>
/// <b>Editions.</b> A name is looked up in each edition the answer needs. When one edition lacks it, the other edition's
/// recorded counterpart is used, and failing that the other edition's stat block, with a note: a 2014 stat block in a
/// 2024 game is ordinary play, and "not in the 2024 SRD" must not stop the 2024 maths. An explicit ref is used as given
/// for a single edition; for both, the other side is its counterpart. A <c>ref</c> with no slash ("ogre") is looked up as
/// a name: models pass a name as ref often enough, and the table shows the ref that was used.
/// </para>
/// <para>
/// The index is opened only when an item names an SRD monster, so an encounter given entirely by CR works even while
/// srd.db is still building or cannot be built.
/// </para>
/// <para>
/// <b>The lookup is shared</b> (<see cref="SrdMonsterLookup"/>): <c>balance_simulate</c> and <c>balance_dpr</c>'s monster
/// targets resolve names exactly as this tool does, through <see cref="StatBlockService"/>; only the wording around it
/// (<see cref="Wording"/>: "monsters item N", a CR as the fallback) is this tool's.
/// </para>
/// <para>
/// <b>Campaign defaults</b> (contract §3.7, §9): an omitted <c>edition</c> is the active campaign's ruleset (2014 or 2024;
/// a mixed campaign gives none), else 2024; an omitted <c>effective_level_offset</c> is the campaign's
/// <c>settings.effective_level_offset</c>, else 0. Each value the campaign supplied gets a note naming the campaign
/// (<see cref="CampaignDefaultNotes"/>), because the call alone no longer says which rules or levels the answer used; when
/// campaigns.db exists but could not be read, one note says so and what was used instead. An
/// explicit value always wins, 0 included: "book levels only" must stay expressible in a campaign whose table runs a
/// level hot. The schema says <c>"default": null</c> for both, since a published default cannot follow a campaign that
/// changes between calls.
/// </para>
/// <para>
/// <b><c>party: "campaign"</c></b> (contract D8, §15 H1): the levels are the campaign's current party's, from their sheets
/// (<see cref="PartyRoster"/>: linked <c>member_of</c> the party now, alive and not departed), and <c>campaign</c> names
/// the campaign (default: the current one, else the active one, else the only one: <see cref="CampaignService.Resolve"/>,
/// which may migrate the file, as every campaign read does). A party size that is wrong changes the 2014 multiplier, so
/// nobody is dropped silently: an empty party is refused with the link that adds members, a party larger than a list of
/// levels may be is refused, members without a sheet level are refused together, each with the update call that gives
/// one, and a dead or departed member left out is named in a note, beside the note that says whose levels were used.
/// Whenever a campaign is resolved (<c>campaign</c> given, or <c>party: "campaign"</c>), the edition and offset defaults
/// come from THAT campaign, never from the active one (<see cref="CampaignArgumentDefaults"/>, which balance_simulate's
/// <c>campaign</c> shares): one campaign's party judged under another's ruleset is a wrong answer that looks right. Their
/// notes are every tool's ("the active campaign's (belmakor) ruleset") when that campaign is also the current or active
/// one, and name it otherwise ("the sky campaign's ruleset"). campaigns.db failures on that path are mapped to the
/// store's own message (<see cref="CampaignDatabase.TryMapUnavailable"/>, through
/// <see cref="CampaignArgumentDefaults.Mapped{T}"/>): this is not a campaign tool to the host's filter, whose
/// SqliteException mapping for this tool would wrongly blame srd.db's failures on campaigns.db. The word is accepted by the argument guard through <see cref="CheckedAsAttribute.Or"/>, so a list of
/// levels is checked exactly as before.
/// </para>
/// </summary>
public sealed class EncounterTools
{
    /// <summary>The <c>name</c> of a CR-only monster is shown in the result; longer text is not a label.</summary>
    public const int MaxLabelLength = 60;

    private const int MaxEchoLength = 80;

    private const string Examples =
        "Example: {\"party\": [5, 5, 5, 5], \"monsters\": [{\"name\": \"Ogre\", \"count\": 3}], \"edition\": \"both\"}.";

    private readonly SrdIndexService _indexService;
    private readonly CampaignService _campaigns;

    public EncounterTools(SrdIndexService indexService, CampaignService campaigns)
    {
        _indexService = indexService;
        _campaigns = campaigns;
    }

    // Read-only, idempotent and closed-world: the answer follows from the arguments and the vendored content this binary
    // ships and, for party "campaign" or campaign, from campaigns.db, which it only reads (Resolve may migrate the file, as
    // every campaign read does); repeating the call changes nothing.
    [McpServerTool(Name = "encounter_difficulty", Title = "Encounter difficulty", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "How hard a combat encounter is for a party, computed by the rules rather than estimated. 2014 rules: the DMG's " +
        "XP thresholds (Easy, Medium, Hard, Deadly) with the group multiplier and the party-size shift. 2024 rules: the SRD " +
        "5.2.1 XP budget (Low, Moderate, High), no multiplier, plus the SRD's troubleshooting warnings. edition \"both\" shows " +
        "the two side by side and how their bands compare at the party's levels. Also gives the XP the party earns.\n" +
        "- party: one level per character, e.g. [5, 5, 5, 5] for four level 5 characters, or [6, 5, 5, 4]; or \"campaign\": the " +
        "campaign's current party, its levels from their sheets.\n" +
        "- monsters: one item per kind of monster, each with exactly one of ref, name or cr:\n" +
        "  - ref: an SRD monster's ref (\"2014/monster/ogre\"); name: an SRD monster's name (\"Ogre\", \"Adult Red Dragon\"), " +
        "looked up in both editions;\n" +
        "  - cr: for a monster not in the SRD (most of the Monster Manual), \"1/4\" or \"13\", with name as its label;\n" +
        "  - count (default 1); exclude (2014: leave a far weaker monster out of the multiplier's count); lair (2024: " +
        "the stat block's in-lair XP; with cr, the next CR's XP).\n" +
        "- edition: \"2014\", \"2024\" or \"both\"; default: the active campaign's ruleset, else 2024.\n" +
        "- effective_level_offset: optional whole number, e.g. 1 for a party that fights like one level higher (a strong " +
        "party or house rules); the result then shows both the book label and the effective-level label. Default: the " +
        "active campaign's effective_level_offset setting, else 0.\n" +
        "- campaign: the campaign whose party (party \"campaign\") and defaults to use; default: the current campaign.\n" +
        "The tables themselves: rules_get with ref \"rules://tables\" lists them (XP by CR, 2024 budget, 2014 thresholds, " +
        "multipliers, adventuring-day XP, DMG monster statistics by CR).\n" +
        Examples)]
    public async Task<string> Difficulty(
        [Description("One level (1-20) per character, e.g. [5, 5, 5, 5]; or campaign, for the campaign's current party from their sheets.")]
        [CheckedAs(typeof(int[]), Or = CampaignParty)] object party,
        [Description(
            "The monsters: [{\"name\": \"Ogre\", \"count\": 3}], [{\"ref\": \"2014/monster/goblin\", \"count\": 6}] or " +
            "[{\"cr\": \"5\", \"name\": \"Homebrew brute\"}].")]
        EncounterMonsterInput[] monsters,
        [Description("\"2014\", \"2024\" or \"both\". Default: the active campaign's ruleset, else 2024.")] string? edition = null,
        [Description("Optional levels to add to every character for an effective-level reading, -10 to 10, e.g. 1. Default: the active campaign's effective_level_offset setting, else 0.")]
        [AIParameterName("effective_level_offset")] int? effectiveLevelOffset = null,
        [Description("The campaign's slug for party campaign and the defaults. Default: the current campaign.")] string? campaign = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var campaignNotes = new List<string>();
        var editionGiven = !string.IsNullOrWhiteSpace(edition);
        var editions = Editions(editionGiven ? edition : null);
        var fromCampaign = IsCampaignParty(party);
        IReadOnlyList<int> levels = fromCampaign ? [] : Levels(party);
        if (!fromCampaign)
        {
            EncounterLimits.ValidateParty(levels);
        }

        if (effectiveLevelOffset is { } given)
        {
            EncounterLimits.ValidateOffset(given);
        }

        var requests = Requests(monsters);

        // Read campaigns.db only for what the call left out, after every argument check.
        var offset = effectiveLevelOffset ?? 0;
        if (fromCampaign || !string.IsNullOrWhiteSpace(campaign))
        {
            // A campaign the call chose (by name, or by asking for its party): its party and its defaults.
            var (chosen, roster) = ReadCampaign(campaign, fromCampaign);
            if (roster is not null)
            {
                levels = PartyLevels(chosen.Row, roster, campaignNotes);
            }

            if (!editionGiven && chosen.Edition is { } campaignEdition)
            {
                editions = Editions(campaignEdition);
                campaignNotes.Add(chosen.EditionNote(campaignEdition));
            }

            if (effectiveLevelOffset is null && chosen.LevelOffset is { } campaignOffset)
            {
                offset = campaignOffset;
                campaignNotes.Add(chosen.LevelOffsetNote(campaignOffset));
            }
        }
        else if (!editionGiven || effectiveLevelOffset is null)
        {
            var reading = _campaigns.ReadDefaults();
            if (reading.Values is { } defaults)
            {
                if (!editionGiven && reading.Edition is { } campaignEdition)
                {
                    editions = Editions(campaignEdition);
                    campaignNotes.Add(CampaignDefaultNotes.Edition(campaignEdition, defaults.Slug));
                }

                if (effectiveLevelOffset is null && defaults.EffectiveLevelOffset is { } campaignOffset and not 0)
                {
                    offset = campaignOffset;
                    campaignNotes.Add(CampaignDefaultNotes.LevelOffset(campaignOffset, defaults.Slug));
                }
            }
            else if (reading.UnreadablePath is { } path)
            {
                // campaigns.db exists but failed: say what was used for each value the campaign would have decided.
                campaignNotes.Add(CampaignDefaultNotes.Unreadable(path,
                    !editionGiven && effectiveLevelOffset is null ? "2024 and no effective_level_offset"
                    : !editionGiven ? SrdEdition.Edition2024
                    : "no effective_level_offset"));
            }
        }

        var entries = requests.Any(r => r.NeedsIndex)
            ? await _indexService.QueryAsync(index => Resolve(requests, editions, index), progress, cancellationToken)
            : Resolve(requests, editions, index: null);

        var effective = offset == 0 ? null : EffectiveParty.Of(levels, offset);

        Edition2014Report? for2014 = null;
        Edition2024Report? for2024 = null;
        if (editions.Contains(SrdEdition.Edition2014))
        {
            var monsters2014 = Monsters(entries, SrdEdition.Edition2014);
            for2014 = new Edition2014Report(
                monsters2014,
                Encounter2014.Assess(levels, monsters2014),
                effective is null ? null : Encounter2014.Assess(effective.Levels, monsters2014));
        }

        if (editions.Contains(SrdEdition.Edition2024))
        {
            var monsters2024 = Monsters(entries, SrdEdition.Edition2024);
            for2024 = new Edition2024Report(
                Encounter2024.Assess(levels, monsters2024),
                effective is null ? null : Encounter2024.Assess(effective.Levels, monsters2024),
                Encounter2024.Troubleshoot(levels, monsters2024));
        }

        return EncounterMarkdown.Format(new EncounterReport(levels, effective, editions, entries, for2014, for2024) { CampaignNotes = campaignNotes });
    }

    /// <summary>The word <c>party</c> takes for the campaign's current party (contract D8).</summary>
    public const string CampaignParty = "campaign";

    // The CheckedAs attribute of party: the guard's test for the word is the tool's, so the two never disagree on a spelling.
    private static readonly CheckedAsAttribute PartyArgument = new(typeof(int[])) { Or = CampaignParty };

    // party as the word "campaign" (any case, surrounding spaces ignored).
    private static bool IsCampaignParty(object? party) => party is JsonElement element && PartyArgument.IsLiteral(element);

    /// <summary>
    /// party as levels: the guard has checked it as an <c>int[]</c> (items, ranges), so this binds it as the SDK would have
    /// bound the typed parameter. Null (sent explicitly) is refused with what to send.
    /// </summary>
    private static int[] Levels(object? party)
    {
        if (party is not JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            throw new DndInputException(
                "party is required: one level per character, e.g. [5, 5, 5, 5], or \"campaign\" for the campaign's current party.");
        }

        try
        {
            return element.Deserialize<int[]>(McpJson.Options) ?? [];
        }
        catch (JsonException ex)
        {
            throw new DndInputException("party: give one level (1-20) per character, e.g. [5, 5, 5, 5], or \"campaign\".", ex);
        }
    }

    /// <summary>
    /// The campaign a call chose (<paramref name="campaign"/>, else the current one, else the active one, else the only one)
    /// with its defaults and their notes' wording (<see cref="CampaignArgumentDefaults"/>, which balance_simulate's
    /// <c>campaign</c> shares), and, for <c>party: "campaign"</c>, its party, read from the same campaign. A campaigns.db
    /// failure on the way is the store's message (class summary), and "no campaigns yet" creates no file.
    /// </summary>
    private (ResolvedCampaignDefaults Campaign, PartyRosterResult? Roster) ReadCampaign(string? campaign, bool party) =>
        CampaignArgumentDefaults.Mapped(_campaigns, () =>
        {
            var chosen = CampaignArgumentDefaults.Read(_campaigns, campaign);
            return (chosen, party ? PartyRoster.Read(_campaigns.Database, chosen.Row) : null);
        }, party ? "the campaign's party was not read" : "the campaign's settings were not read");

    /// <summary>
    /// The party's levels in roster order (contract D8), with the notes that say whose they are and who was left out; the
    /// two refusals when they cannot be known: no current members, or members with no sheet level (all of them at once,
    /// each with the call that gives one).
    /// </summary>
    private static IReadOnlyList<int> PartyLevels(CampaignRow campaign, PartyRosterResult roster, List<string> notes)
    {
        if (roster.Members.Count == 0)
        {
            throw new DndInputException(roster.PartyRef is { } partyRef
                ? $"the {campaign.Slug} campaign has no current party members: link characters member_of {partyRef}, e.g. campaign_write " +
                  $"{{\"ops\": [{{\"op\": \"link\", \"from\": \"character:…\", \"rel\": \"member_of\", \"to\": \"{partyRef}\"}}], \"campaign\": \"{campaign.Slug}\"}}." +
                  LeftOut(roster) + " Or give party as levels, e.g. [5, 5, 5, 5]."
                : $"the {campaign.Slug} campaign has no party, so no current party members: give party as levels, e.g. [5, 5, 5, 5].");
        }

        // The bound a list of levels has (EncounterLimits.MaxCharacters), checked before the sheets are: a roster that large
        // is refused whatever its sheets say, so nobody is asked to give levels to members the call cannot use.
        if (roster.Members.Count > EncounterLimits.MaxCharacters)
        {
            throw new DndInputException(
                $"party \"campaign\": the {campaign.Slug} campaign's party has {Number(roster.Members.Count)} current members; at most " +
                $"{Number(EncounterLimits.MaxCharacters)} characters are accepted. Give party as the levels of the characters in this fight, e.g. [5, 5, 5, 5].");
        }

        // The fix call is the contract's (D8) with the campaign appended, as the sheet's own reminders name it: the call that
        // fixes a named campaign's sheet must not land in whichever campaign is current.
        var missing = roster.WithoutLevel;
        if (missing.Count > 0)
        {
            throw new DndInputException(
                $"party \"campaign\": the {campaign.Slug} campaign's party levels come from the sheets, and " +
                $"{(missing.Count == 1 ? "1 member has" : $"{Number(missing.Count)} members have")} no level on one: " +
                string.Join("; ", missing.Select(m =>
                    $"{m.Name} ({(m.Sheet is null ? "no sheet" : "a sheet with no level")}): campaign_character {{\"action\": \"update\", " +
                    $"\"character\": \"{m.Handle}\", \"sheet\": {{\"level\": <n>}}, \"campaign\": \"{campaign.Slug}\"}}")) +
                ". Or give party as levels, e.g. [5, 5, 5, 5].");
        }

        notes.Add($"Party: the {campaign.Slug} campaign's {Number(roster.Members.Count)} current " +
                  $"{(roster.Members.Count == 1 ? "member" : "members")} ({PartyRoster.Names(roster.Members.Select(PartyRoster.NameAndLevel))}).");
        if (roster.Excluded.Count > 0)
        {
            notes.Add(LeftOut(roster).TrimStart());
        }

        // Each level is 1-20 already: the sheet rules validate it and the column's CHECK holds it there.
        return roster.Members.Select(m => m.Level!.Value).ToList();
    }

    // " Left out: Tristan (dead); a dead or departed member is not in the party." (empty when nobody was).
    private static string LeftOut(PartyRosterResult roster) => roster.Excluded.Count == 0
        ? string.Empty
        : $" Left out: {PartyRoster.Names(roster.Excluded.Select(e => $"{e.Name} ({e.Reason})"))}; a dead or departed member is not in the party.";

    // A blank or omitted edition is 2024 here: the campaign's default is applied by the caller, after the argument checks.
    private static IReadOnlyList<string> Editions(string? edition)
    {
        var value = string.IsNullOrWhiteSpace(edition) ? SrdEdition.Edition2024 : edition.Trim().ToLowerInvariant();
        return value switch
        {
            SrdEdition.Edition2014 => [SrdEdition.Edition2014],
            SrdEdition.Edition2024 => [SrdEdition.Edition2024],
            RulesTools.Both => [SrdEdition.Edition2014, SrdEdition.Edition2024],
            _ => throw new DndInputException($"edition must be \"2014\", \"2024\" or \"both\" (got \"{Echo(edition!)}\")."),
        };
    }

    /// <summary>
    /// Each item checked on its own terms before any lookup: exactly one of ref, name and cr (a name beside cr is its
    /// label), a label that fits on one line, and a count in range. Messages count items as the argument guard does
    /// ("monsters item 2: …"), so the model reads one vocabulary whichever layer caught the mistake.
    /// </summary>
    private static List<MonsterRequest> Requests(EncounterMonsterInput[]? monsters)
    {
        monsters ??= [];
        EncounterLimits.ValidateMonsterCount(monsters.Length, Examples);

        var requests = new List<MonsterRequest>(monsters.Length);
        for (var i = 0; i < monsters.Length; i++)
        {
            var position = i + 1;
            var where = $"monsters item {position.ToString(CultureInfo.InvariantCulture)}";
            var input = monsters[i] ?? throw new DndInputException($"{where}: is null; give an object. {Examples}");
            var refText = Blank(input.Ref);
            var name = Blank(input.Name);
            var cr = input.Cr is JsonElement crValue ? ParseCr(crValue, where) : (ChallengeRating?)null;
            EncounterLimits.ValidateCount(position, input.Count);

            if (refText is not null && (name is not null || cr is not null))
            {
                throw new DndInputException(
                    $"{where}: has both ref and {(name is not null ? "name" : "cr")}; give one: ref or name for an SRD monster, or cr " +
                    "(with name as its label) for any other.");
            }

            if (refText is null && name is null && cr is null)
            {
                throw new DndInputException(
                    $"{where}: names no monster; give ref or name for an SRD monster, or cr for any other, e.g. {{\"name\": \"Ogre\", " +
                    "\"count\": 2} or {\"cr\": \"3\", \"name\": \"Bandit boss\"}.");
            }

            if (cr is not null && name is not null && (name.Length > MaxLabelLength || name.Any(char.IsControl)))
            {
                throw new DndInputException(
                    $"{where}: with cr, name is only a label, one line of at most {MaxLabelLength} characters (got \"{Echo(Printable(name))}\").");
            }

            requests.Add(new MonsterRequest(position, refText, cr is null ? name : null, cr, cr is null ? null : name, input.Count, input.Exclude, input.Lair));
        }

        return requests;
    }

    /// <summary>
    /// The cr argument as a CR: a string ("1/2", "5", "0.5") or a JSON number (0.5, 5). The SDK binds the untyped
    /// property as a <see cref="JsonElement"/>. A number is read by its JSON text, like a string: read as a double, 1e-400
    /// underflowed to CR 0 and 1e1 passed as 10 while "1e1" was refused.
    /// </summary>
    private static ChallengeRating? ParseCr(JsonElement value, string where)
    {
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => ChallengeRating.Parse(value.GetString()),
                JsonValueKind.Number => ChallengeRating.Parse(value.GetRawText()),
                _ => throw new DndInputException($"cr must be a string such as \"1/2\" or \"5\", or a number, but was {Describe(value)}."),
            };
        }
        catch (DndInputException ex)
        {
            throw new DndInputException($"{where}: {ex.Message}", ex);
        }
    }

    private static List<EncounterEntry> Resolve(IReadOnlyList<MonsterRequest> requests, IReadOnlyList<string> editions, SrdIndex? index) =>
        requests.Select(request => Resolve(request, editions, index)).ToList();

    private static EncounterEntry Resolve(MonsterRequest request, IReadOnlyList<string> editions, SrdIndex? index)
    {
        if (request.Cr is { } cr)
        {
            return ByCr(request, cr, editions);
        }

        ArgumentNullException.ThrowIfNull(index);
        var docs = SrdMonsterLookup.Resolve(index, request.RefText ?? request.NameText!, editions, Wording(request.Position));

        var notes = new List<string>(docs.Notes);
        var resolved = new Dictionary<string, EncounterEntrySide>(StringComparer.Ordinal);
        foreach (var edition in editions)
        {
            var doc = docs.ByEdition[edition];
            var challenge = SrdMonsterChallenge.Read(doc);
            var xp = challenge.Xp;
            string? lairNote = null;
            if (doc.Edition == SrdEdition.Edition2024 && challenge.ChallengeRating == ChallengeRating.Zero && xp == ChallengeRatingTables.DefaultCrZeroXp)
            {
                // 26 of the 28 SRD 5.2.1 CR 0 stat blocks print "XP 0 or 10"; upstream stores 10.
                notes.Add($"{doc.Name} (`{doc.Ref}`): its SRD 5.2.1 stat block gives XP 0 or 10; {xp} is used.");
            }

            if (request.Lair)
            {
                if (challenge.XpInLair is { } inLair)
                {
                    xp = inLair;
                    lairNote = "in lair";
                }
                else
                {
                    notes.Add($"{doc.Name} (`{doc.Ref}`): its stat block gives no in-lair XP, so lair changes nothing; its normal XP is used.");
                }
            }

            resolved[edition] = new EncounterEntrySide(doc.Name, doc.Ref.ToString(), challenge.ChallengeRating, xp, lairNote);
        }

        return new EncounterEntry(resolved[editions[0]].Name, request.Count, request.Exclude, resolved, notes);
    }

    /// <summary>
    /// encounter_difficulty's words around the shared monster lookup (<see cref="SrdMonsterLookup"/>): items are "monsters
    /// item N", the stat block serves "this 2024 encounter" or "the 2024 maths", and a monster the SRD lacks is given by
    /// its CR, which only this tool takes.
    /// </summary>
    private static MonsterLookupWording Wording(int position) => new(
        $"monsters item {position.ToString(CultureInfo.InvariantCulture)}",
        "encounter",
        edition => $"for the {edition} maths too",
        "Give a monster's ref (e.g. \"2024/monster/ogre\"), its name, or a cr.",
        "For a monster not in the SRD, give its cr instead.",
        name => "For a monster the SRD does not have (it has only some of the Monster Manual), give the CR from its stat block, " +
                $"with name as a label: {{\"name\": \"{name}\", \"cr\": \"<its CR>\"}}.");

    /// <summary>
    /// A monster given by CR: the XP table's value in every edition, labelled with its name or "CR n monster". In its lair
    /// a 2024 monster is worth the next CR's XP, as every SRD 5.2.1 stat block with an in-lair XP gives it; its CR stays.
    /// 2014 stat blocks have no in-lair XP, and CR 30 has no row above.
    /// </summary>
    private static EncounterEntry ByCr(MonsterRequest request, ChallengeRating cr, IReadOnlyList<string> editions)
    {
        var label = request.Label ?? $"CR {cr} monster";
        var notes = new List<string>();
        if (cr == ChallengeRating.Zero)
        {
            notes.Add($"{label}: CR 0 is worth 0 or 10 XP by its stat block; {ChallengeRatingTables.DefaultCrZeroXp} is used.");
        }

        var sides = new Dictionary<string, EncounterEntrySide>(StringComparer.Ordinal);
        foreach (var edition in editions)
        {
            var xp = ChallengeRatingTables.Xp(cr);
            string? xpNote = null;
            if (request.Lair && edition == SrdEdition.Edition2024 && cr.Next is { } next)
            {
                xp = ChallengeRatingTables.Xp(next);
                xpNote = "in lair";
                notes.Add(
                    $"{label}: in its lair a CR {cr} monster is worth the next CR's XP ({Number(xp)}), as every SRD 5.2.1 stat block " +
                    "with an in-lair XP gives it; its CR stays the same.");
            }
            else if (request.Lair)
            {
                notes.Add(edition == SrdEdition.Edition2024
                    ? $"{label}: CR 30 has no row above it, so lair changes nothing; its normal XP is used."
                    : $"{label}: 2014 stat blocks give no in-lair XP, so lair changes nothing for 2014.");
            }

            sides[edition] = new EncounterEntrySide(label, null, cr, xp, xpNote);
        }

        return new EncounterEntry(label, request.Count, request.Exclude, sides, notes);
    }

    private static List<EncounterMonster> Monsters(IReadOnlyList<EncounterEntry> entries, string edition) =>
        entries.Select(e =>
        {
            var side = e.Sides[edition];
            // A CR-only monster is its own stat block per (CR, label): two "Bandit" entries at CR 1 and CR 8 are two blocks,
            // and keying by the label alone merged them and hid the CR 8 one from the troubleshooting checks.
            var statBlock = side.Ref ?? $"cr:{side.ChallengeRating}:{side.Name}";
            return new EncounterMonster(side.Name, statBlock, side.ChallengeRating, side.Xp, e.Count, e.Exclude);
        }).ToList();

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => $"the boolean {value.GetRawText()}",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        JsonValueKind.Object => "an object",
        _ => value.GetRawText(),
    };

    private static string Echo(string text) => text.Length <= MaxEchoLength ? text : text[..MaxEchoLength] + "…";

    // Control characters written as escapes, so a refused label with a newline comes back on one line.
    private static string Printable(string text) =>
        string.Concat(text.Select(c => c switch
        {
            '\n' => "\\n",
            '\r' => "\\r",
            '\t' => "\\t",
            _ when char.IsControl(c) => $"\\u{(int)c:x4}",
            _ => c.ToString(),
        }));

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>One monsters item, checked: which of ref, name or cr it gives, and the rest of its settings.</summary>
/// <param name="Position">1-based, as messages count items.</param>
/// <param name="Label">A CR-only monster's display name, from its name argument.</param>
internal sealed record MonsterRequest(
    int Position, string? RefText, string? NameText, ChallengeRating? Cr, string? Label, int Count, bool Exclude, bool Lair)
{
    public bool NeedsIndex => Cr is null;
}
