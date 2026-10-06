using System.Globalization;
using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Sqlite;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>What to check.</summary>
/// <param name="Text">The draft: a lyric, a journal entry, an in-character line (at most 50,000 characters).</param>
/// <param name="Speaker">Whose mouth it is in (default author).</param>
/// <param name="Diegetic">It is said in the world, to an audience (a song is a public statement): names and secrets are also checked against the audience.</param>
/// <param name="Audience">Who hears it when diegetic (default party).</param>
/// <param name="AsOfSession">Check as of the end of a session (which gates were active then).</param>
public sealed record CheckRequest(string Text, Perspective? Speaker = null, bool Diegetic = false, Perspective? Audience = null, int? AsOfSession = null);

/// <summary>How a name in the text reads for the speaker (or the audience).</summary>
public static class NameClasses
{
    /// <summary>
    /// A name the speaker uses (<see cref="EntityView.UsedNames"/>): the name its view shows, the aliases that view may
    /// see (public ones; party ones for the party, the table, the dm and current members), and under a known_as disguise
    /// the known_as and the party aliases the disguise shares.
    /// </summary>
    public const string Ok = "ok";

    /// <summary>The speaker knows the entity, by another name (<see cref="NameCandidate.SpeakerName"/>).</summary>
    public const string OtherName = "other_name";

    /// <summary>The speaker has no Knows verdict on the entity.</summary>
    public const string UnknownEntity = "unknown_entity";

    /// <summary>A name from another campaign (a cross-linked entity): the firewall between campaigns.</summary>
    public const string CrossCampaign = "cross_campaign";

    /// <summary>Diegetic only: the audience does not know or does not use this name.</summary>
    public const string RevealsToAudience = "reveals_to_audience";

    /// <summary>
    /// A capitalised word of the text that is a word of a name, alias or forbidden term the speaker (or the audience) does
    /// not use, said on its own (<see cref="DndMcp.Repository.Campaign.Read.PartialName"/>): "Cage" from "Axiom Cage".
    /// </summary>
    public const string PartialName = "partial_name";
}

/// <summary>What a <see cref="PartialName"/> is a word of (<see cref="PartialName.SourceKind"/>).</summary>
public static class PartialNameSources
{
    /// <summary>An entity's own name.</summary>
    public const string Name = "name";

    /// <summary>One of an entity's aliases.</summary>
    public const string Alias = "alias";

    /// <summary>A term an active gate or reveal rule forbids.</summary>
    public const string ForbiddenTerm = "forbidden_term";
}

/// <summary>
/// A capitalised word of the text that gives away a name the perspective does not use, said on its own: a hard flag
/// (review U02). The name scanner matches whole names only, so "we'll haul your Cage back" passed for a speaker who has
/// never heard of "the Axiom Cage", listing "Cage" only as a possible invention.
/// </summary>
/// <param name="Matched">The word as written ("Cage").</param>
/// <param name="Start">Where in the text.</param>
/// <param name="Length">How long.</param>
/// <param name="Perspective">Whose name it is not: the speaker, or (diegetic) the audience.</param>
/// <param name="FullName">The name, alias or term it is a word of ("Axiom Cage").</param>
/// <param name="Source">The entity it names (author ref, <c>item:axiom-cage</c>), or the gate or rule forbidding the term (<c>f:5</c>).</param>
/// <param name="SourceKind">One of <see cref="PartialNameSources"/>.</param>
/// <param name="Visibility">An alias's visibility; null otherwise.</param>
/// <param name="KnownAs">The name that perspective uses for the entity, when it knows the entity at all; null otherwise.</param>
public sealed record PartialName(
    string Matched,
    int Start,
    int Length,
    string Perspective,
    string FullName,
    string Source,
    string SourceKind,
    string? Visibility,
    string? KnownAs);

/// <summary>How a scanned name matched an entity (<see cref="NameCandidate.MatchedAs"/>).</summary>
public static class NameMatchKinds
{
    /// <summary>The entity's own name.</summary>
    public const string Name = "name";

    /// <summary>One of its aliases (any visibility: an author alias in the text is what the check exists to catch).</summary>
    public const string Alias = "alias";

    /// <summary>A knower's name for it (a knowledge row's known_as).</summary>
    public const string KnownAs = "known_as";
}

/// <summary>Why a secret is at risk in a diegetic text.</summary>
public static class RiskReasons
{
    /// <summary>The fact is related to the text (about an entity it names, or it shares its words): a hard flag.</summary>
    public const string RelatedToText = "related_to_text";

    /// <summary>The fact is about the speaker's own character but not related to the text: one to keep in mind.</summary>
    public const string AboutSpeaker = "about_speaker";
}

/// <summary>The check's findings. Author-facing: it names what the speaker must not say (that is its job).</summary>
/// <param name="Speaker">The speaker perspective.</param>
/// <param name="Audience">The audience perspective, when diegetic.</param>
/// <param name="Pass">
/// No hard flag: every name is one the speaker uses (and, diegetic, the audience too), no active forbidden vocabulary, and
/// no secret at risk that the text touches. Unknown facts, mistaken beliefs, stale facts, secrets about the speaker,
/// possible partial names and possible inventions are for review, not failures (the check retrieves; the model judges).
/// </param>
/// <param name="Names">Every name found, classified.</param>
/// <param name="Forbidden">Active gate vocabulary and reveal-rule hits (non-author speakers only).</param>
/// <param name="UnknownFacts">
/// Related facts the speaker does not know (explicitly unaware, uncertain or no record), with who does; for a non-author
/// speaker also related facts that are not in play (standing <see cref="Standings.NotInPlay"/>: planned, proposed, lean,
/// struck or superseded), which no player-side view knows whatever its rows say, and author-only facts knowledge rows say
/// the speaker knows or may know (standing <see cref="Standings.AuthorOnly"/>: author visibility is absolute). Listed for review:
/// like the other retrieved facts they do not fail <see cref="CheckResult.Pass"/>, because "related" is a word overlap,
/// not a claim the text makes (the model judges whether the line states it).
/// </param>
/// <param name="MistakenBeliefs">Related facts the speaker believes or misbelieves whose truth is partial or false.</param>
/// <param name="SecretsAtRisk">
/// Diegetic, character speaker: facts the speaker knows and the audience does not, each judged as that view's own reads
/// judge it (<see cref="ReadScope.Shows"/>): an author-only fact is known to neither, so it is never a secret at risk.
/// </param>
/// <param name="Stale">Related facts that are superseded or rest on a superseded fact.</param>
/// <param name="Related">Related facts the speaker knows (context for judging whether a line is oblique enough).</param>
/// <param name="PossibleInventions">
/// Proper nouns that match no name, alias or known_as in the campaign, are no word of a name found in
/// <see cref="PartialNames"/> or <see cref="PossiblePartialNames"/>, and are not made of a name the speaker uses or the
/// text names (<see cref="KnownNames"/>: a whole-word run inside one, or such names with nothing beside them but
/// connectors, titles and a sentence's opening word), the rule the session checklist's unknown names follow too.
/// </param>
public sealed record CheckResult(
    string Speaker,
    string? Audience,
    bool Pass,
    IReadOnlyList<NameMention> Names,
    IReadOnlyList<ForbiddenFinding> Forbidden,
    IReadOnlyList<FactFinding> UnknownFacts,
    IReadOnlyList<FactFinding> MistakenBeliefs,
    IReadOnlyList<SecretAtRisk> SecretsAtRisk,
    IReadOnlyList<StaleFinding> Stale,
    IReadOnlyList<FactFinding> Related,
    IReadOnlyList<string> PossibleInventions)
{
    /// <summary>
    /// Hard flags: capitalised words that are a word of a name the speaker (or, diegetic, the audience) does not use
    /// (<see cref="PartialName"/>); <see cref="Pass"/> is false while there is one.
    /// </summary>
    public IReadOnlyList<PartialName> PartialNames { get; init; } = [];

    /// <summary>
    /// For review, not hard flags: the same finding for a word whose capital marks nothing, because it opens a line or a
    /// sentence ("Peaceful days ahead", "Silver moon over the sea"), so it may be the ordinary word; the model judges.
    /// </summary>
    public IReadOnlyList<PartialName> PossiblePartialNames { get; init; } = [];
}

/// <summary>One name found in the text.</summary>
/// <param name="Matched">The words as written.</param>
/// <param name="Start">Where in the text.</param>
/// <param name="Length">How long.</param>
/// <param name="Classification">For the speaker: ok when any reading is ok, else other_name, unknown_entity, cross_campaign (in that order).</param>
/// <param name="AudienceClassification">Diegetic only: ok, or reveals_to_audience.</param>
/// <param name="Candidates">Every entity the name may be (two may share a name), each classified.</param>
public sealed record NameMention(string Matched, int Start, int Length, string Classification, string? AudienceClassification, IReadOnlyList<NameCandidate> Candidates);

/// <summary>One reading of a name.</summary>
/// <param name="Ref">The entity (author ref; another campaign's as <c>one-piece/character:keras</c>).</param>
/// <param name="Name">The entity's true name.</param>
/// <param name="MatchedAs">One of <see cref="NameMatchKinds"/>.</param>
/// <param name="MatchedVisibility">For an alias its visibility; for a known_as whose name it is (<c>party</c>, <c>character:belmakor</c>).</param>
/// <param name="Classification">For the speaker (<see cref="NameClasses"/>).</param>
/// <param name="SpeakerName">The name the speaker knows it by, when it knows it at all.</param>
/// <param name="AudienceClassification">Diegetic only.</param>
/// <param name="AudienceName">The name the audience knows it by, when it does.</param>
/// <param name="Campaign">The other campaign's slug, for a cross-campaign name.</param>
public sealed record NameCandidate(
    string Ref,
    string Name,
    string MatchedAs,
    string? MatchedVisibility,
    string Classification,
    string? SpeakerName,
    string? AudienceClassification,
    string? AudienceName,
    string? Campaign);

/// <summary>A forbidden word or pattern in the text.</summary>
/// <param name="Source">The gated fact (<c>f:12</c>) or reveal rule (<c>rule:old-king-no-name</c>).</param>
/// <param name="TermOrPattern">As the rule spells it.</param>
/// <param name="Matched">As the text spells it.</param>
/// <param name="Start">Where.</param>
/// <param name="Length">How long.</param>
/// <param name="Until">The facts whose being in play lifts the rule (a gate without forbidden_until lifts when the party knows its fact).</param>
/// <param name="PreferredTerms">What to say instead.</param>
/// <param name="Note">Why.</param>
public sealed record ForbiddenFinding(
    string Source,
    string TermOrPattern,
    string Matched,
    int Start,
    int Length,
    IReadOnlyList<string> Until,
    IReadOnlyList<string> PreferredTerms,
    string? Note);

/// <summary>A related fact and where the speaker stands on it.</summary>
/// <param name="Ref"><c>f:&lt;n&gt;</c>.</param>
/// <param name="Code">Its code.</param>
/// <param name="Statement">The statement.</param>
/// <param name="Standing">The speaker's standing (<see cref="Standings"/>).</param>
/// <param name="State">The deciding row's state (an explicit <c>unaware</c> is shown as such).</param>
/// <param name="Truth">true, false, partial, unknown.</param>
/// <param name="CanonStatus">canon, played, superseded, …</param>
/// <param name="KnownBy">Who holds it in an aware state.</param>
/// <param name="Why">How it is related: <c>about character:old-king</c> or <c>shares words with the text</c>.</param>
/// <param name="Explanation">The verdict's explanation.</param>
public sealed record FactFinding(
    string Ref,
    string? Code,
    string Statement,
    string Standing,
    string? State,
    string Truth,
    string CanonStatus,
    IReadOnlyList<string> KnownBy,
    string Why,
    string Explanation);

/// <summary>A fact the speaker knows and the audience does not.</summary>
public sealed record SecretAtRisk(FactFinding Fact, string Reason, string AudienceStanding);

/// <summary>A related fact that is superseded, or rests on one.</summary>
/// <param name="Ref">The fact.</param>
/// <param name="Statement">Its statement.</param>
/// <param name="Superseded">The superseded fact (itself when it is the one superseded).</param>
/// <param name="Depth">0 when it is superseded itself; else how many dependency steps away.</param>
public sealed record StaleFinding(string Ref, string Statement, string Superseded, int Depth);

/// <summary>
/// The knowledge check (contract §7): does this text put in the speaker's mouth something the speaker cannot know, a name
/// the speaker does not use, a forbidden word, a secret the audience must not hear, a stale fact or an invented name?
///
/// <para>
/// <b>An author-side tool.</b> Its result names the things the speaker does not know, their true names and who does know
/// them: that is its purpose, and the host renders it to the author only. What it must get right is the classification,
/// which runs through the same verdicts and views as every read (<see cref="ReadScope.ViewFor"/>), so "Belmakor knows
/// the old king" means exactly what his campaign_get shows. That includes the not-in-play rule of
/// <see cref="ReadScope"/>: a party-visible planned fact ("The War God's axe is fully assembled.") is hidden from the
/// party's reads, so the check must not call it known to the party either, or a line stating it before it happens
/// would come back as consistent with what the party knows. It includes author visibility too: an author-only fact is
/// known to no player-side view whatever its knowledge rows say (contract §3.2), so the check never lists it as known
/// to the speaker or the audience, and never as a secret the speaker could give away; it is an unknown fact, "author
/// only". And as of a session it includes <see cref="ReadScope.VerdictVisibility"/> and <see cref="ReadScope.VerdictEntries"/>:
/// a fact established later was not known then by its visibility, nor by a group's row that records no session.
/// </para>
/// <para>
/// <b>Names</b> come from D's <see cref="NameScanner"/> over every entity name, every alias (any visibility: an author
/// alias in the text is exactly what to catch), every known_as, and the names and aliases of other campaigns' entities
/// cross-linked to this one's (the firewall). <b>Forbidden vocabulary</b> is checked only for a non-author speaker (the
/// author may use any word), with the gates and reveal rules active at the point in time. <b>Related facts</b> are the
/// facts linked <c>about</c> a named entity plus the top 20 <c>fact_fts</c> matches for the text's significant words
/// (function words dropped, or "it was" would relate every fact containing "was").
/// </para>
/// <para>
/// <b>Partial names</b> (review U02): a capitalised word that is a word of a name the speaker (or audience) does not use,
/// said alone ("haul your Cage back" for the Axiom Cage), is a hard flag too (<see cref="PartialName"/>); the scanner above
/// finds whole names only. One that opens a line or a sentence, where the capital may be the line's, is for review
/// (<see cref="CheckResult.PossiblePartialNames"/>).
/// </para>
/// </summary>
public sealed class KnowledgeCheck
{
    private const int MaxFtsWords = 64;
    private const int MaxFtsFacts = 20;

    /// <summary>
    /// The kinds whose own names are in-world proper names, so a word of one said alone can give it away (a person, a
    /// place, a faction, a thing, a legend, a made thing, an event, a work). The other kinds (quests, threads, questions,
    /// secrets, sessions, rules, notes, arcs, beats, clocks, fronts, scenes, handouts) carry titles in ordinary words; their
    /// aliases still count (<see cref="PartialNames"/>). The view-text check's prefix rule takes its stems by the same line
    /// (<see cref="ViewTextCheck"/>).
    /// </summary>
    internal static readonly IReadOnlySet<string> ProperNameKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        CampaignValues.Kinds.Character,
        CampaignValues.Kinds.Location,
        CampaignValues.Kinds.Faction,
        CampaignValues.Kinds.Item,
        CampaignValues.Kinds.Lore,
        CampaignValues.Kinds.Homebrew,
        CampaignValues.Kinds.Event,
        CampaignValues.Kinds.Work,
    };

    /// <summary>
    /// Titles and ranks, which name a person only together with a name ("Captain Vexmoor", "Lady Morrow"): one said alone
    /// ("my Captain", "Lady luck") gives no name away, unless it is all the name has ("The Old King").
    /// </summary>
    private static readonly IReadOnlySet<string> TitleWords = new HashSet<string>(StringComparer.Ordinal)
    {
        "captain", "lady", "lord", "king", "queen", "prince", "princess", "duke", "duchess", "baron", "baroness", "count",
        "countess", "earl", "emperor", "empress", "dame", "master", "mistress", "madam", "madame", "mister", "father", "mother",
        "brother", "sister", "uncle", "aunt", "doctor", "professor", "general", "admiral", "commander", "lieutenant",
        "sergeant", "colonel", "major", "marshal", "chief", "elder", "saint", "bishop", "priest", "priestess", "warden",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "but", "nor", "so", "yet", "if", "then", "than", "as", "at", "by", "for", "from", "in",
        "into", "of", "off", "on", "onto", "out", "over", "to", "up", "with", "without", "about", "after", "before", "under",
        "i", "me", "my", "mine", "we", "us", "our", "ours", "you", "your", "yours", "he", "him", "his", "she", "her", "hers",
        "it", "its", "they", "them", "their", "theirs", "this", "that", "these", "those", "who", "whom", "whose", "which",
        "what", "when", "where", "why", "how", "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did",
        "done", "have", "has", "had", "will", "would", "shall", "should", "can", "could", "may", "might", "must", "not", "no",
        "yes", "all", "any", "some", "each", "every", "both", "either", "neither", "one", "ones", "just", "only", "also",
        "too", "very", "here", "there", "now", "oh", "ll", "ve", "re", "d", "s", "t", "m", "don", "won", "wasn", "isn",
        "aren", "weren", "didn", "doesn", "hasn", "haven", "hadn", "couldn", "wouldn", "shouldn", "ain", "let", "lets",
        "someone", "something", "somebody", "anyone", "anything", "nobody", "nothing", "everyone", "everything", "again",
        "still", "even", "back", "down", "away", "while", "because", "since", "until", "though", "although", "ever", "never",
        "thats", "im", "ive", "ill", "id", "youre", "theyre",
    };

    private readonly CampaignDatabase _database;

    public KnowledgeCheck(CampaignDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    /// <summary>Checks a text for one speaker (and audience, when diegetic).</summary>
    /// <exception cref="DndInputException">Empty or over-long text, an unknown character perspective, an as_of_session out of range.</exception>
    public CheckResult Check(CampaignRow campaign, CheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(request);
        using var connection = ReadConnection.Open(_database);
        return Check(connection, campaign, request);
    }

    internal static CheckResult Check(SqliteConnection connection, CampaignRow campaign, CheckRequest request)
    {
        var text = request.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DndInputException("text is required: the draft to check, e.g. a lyric or an in-character line.");
        }

        if (text.Length > CampaignLimits.MaxCheckTextLength)
        {
            throw new DndInputException(
                $"text is {text.Length.ToString("N0", CultureInfo.InvariantCulture)} characters; check at most " +
                $"{CampaignLimits.MaxCheckTextLength.ToString("N0", CultureInfo.InvariantCulture)} at a time (split a long text into parts).");
        }

        var scope = ReadScope.Open(connection, campaign, Perspective.Author, request.AsOfSession);
        var speaker = scope.Loader.Resolve(request.Speaker ?? Perspective.Author, scope.AsOf);
        var audience = request.Diegetic
            ? scope.Loader.Resolve(request.Audience ?? Perspective.Parse(CampaignValues.PerspectiveKinds.Party), scope.AsOf)
            : null;

        scope.LoadAllEntities();
        var (scanner, knownKeys) = Names(scope);
        var hits = scanner.Scan(text);
        var mentions = hits.Select(hit => Mention(scope, hit, speaker, audience)).ToList();

        var forbidden = new List<ForbiddenFinding>();
        IReadOnlyList<ForbiddenRule> rules = [];
        if (!speaker.IsAuthorView)
        {
            var until = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            rules = ReadGates.ActiveVocabulary(scope, until);
            forbidden.AddRange(ForbiddenVocabulary.Scan(text, rules).Select(h => new ForbiddenFinding(
                h.Rule.Source, h.TermOrPattern, h.Surface, h.Start, h.Length, until.GetValueOrDefault(h.Rule.Source) ?? [],
                h.Rule.PreferredTerms, h.Rule.Note)));
        }

        var (partials, possiblePartials) = PartialNames(scope, text, hits, forbidden, rules, speaker, audience);

        var named = hits.SelectMany(h => h.Entries).Select(e => (Reading)e.Payload).Where(r => r.Campaign is null)
            .Select(r => r.EntityId).Distinct(StringComparer.Ordinal).ToList();
        var related = RelatedFacts(scope, text, named);
        var unknown = new List<FactFinding>();
        var beliefs = new List<FactFinding>();
        var known = new List<FactFinding>();
        foreach (var (fact, why) in related)
        {
            // Known or not is exactly what the speaker's own reads show (ReadScope.Shows: the author view sees all). What
            // they do not show is listed with the most decisive reason: not in play (whatever the rows say), then author
            // only (rows claim it, but author visibility is absolute), then the verdict itself (unaware, uncertain, no record).
            var verdict = scope.VerdictFor(speaker, fact);
            if (!ReadScope.Shows(speaker, fact.Row, verdict))
            {
                unknown.Add(ReadScope.NotInPlay(fact.Row.CanonStatus) ? NotInPlayFinding(scope, fact, verdict, why)
                    : ReadScope.RowsClaimAuthorOnly(speaker, fact.Row.Visibility, verdict) ? AuthorOnlyFinding(scope, fact, verdict, why)
                    : Finding(scope, fact, verdict, why));
                continue;
            }

            var finding = Finding(scope, fact, verdict, why);
            if (verdict.State is CampaignValues.KnowledgeStates.Believes or CampaignValues.KnowledgeStates.Misbelieves &&
                fact.Row.Truth is CampaignValues.Truths.Partial or CampaignValues.Truths.False)
            {
                beliefs.Add(finding);
            }
            else
            {
                known.Add(finding);
            }
        }

        var risks = audience is not null && speaker.CharacterId is not null
            ? SecretsAtRisk(scope, speaker, audience, related)
            : [];
        var stale = Stale(scope, related.Select(r => r.Fact).ToList());

        // A word flagged as part of a name is not an invention: it is a word of something the campaign has. Compared with
        // and without a possessive s on either side, as the known names are ("Cage's" is flagged, "Cage" is the candidate).
        // Nor is a candidate made of a name the speaker uses or the scanner found (KnownNames, the session checklist's
        // rule, review UR01): "Hail Belmakor" holds a name the result lists as used and only a sentence's opening word
        // beside it (or a title: "Captain Belmakor"), and "King" is a word of the party's "the old king"; "Belmakor
        // Shadowfang" holds a new word, and is listed. Only the speaker's own names and the names in the text count for that: a word of a name the speaker does
        // not use is the partial-name rule's to judge, not a reason to call the word known.
        var partialKeys = partials.Concat(possiblePartials).Select(p => CampaignText.KeyWithoutArticle(p.Matched))
            .SelectMany(k => new[] { k, WithoutPossessive(k) })
            .ToHashSet(StringComparer.Ordinal);
        var used = new KnownNames(UsedNames(scope, speaker).Concat(hits.SelectMany(h => h.Entries).Select(e => e.Surface)));
        var inventions = used.Unknown(text)
            .Where(c => CampaignText.KeyWithoutArticle(c) is var key && !knownKeys.Contains(key) && !knownKeys.Contains(WithoutPossessive(key)) &&
                        !partialKeys.Contains(key) && !partialKeys.Contains(WithoutPossessive(key)))
            .ToList();

        var pass = mentions.All(m => m.Classification == NameClasses.Ok && (audience is null || m.AudienceClassification == NameClasses.Ok)) &&
                   forbidden.Count == 0 &&
                   partials.Count == 0 &&
                   risks.All(r => r.Reason != RiskReasons.RelatedToText);
        return new CheckResult(speaker.Perspective.Text, audience?.Perspective.Text, pass, mentions, forbidden, unknown, beliefs, risks,
            stale, known, inventions)
        {
            PartialNames = partials,
            PossiblePartialNames = possiblePartials,
        };
    }

    // Every name to look for, and the folded names an invention is checked against. As of a session the names are those of
    // the entities that existed then (review C10): an entity deleted since is still a name of that time, not a possible
    // invention. Known_as rows are today's, whatever their session: a name the party learned later is an anachronism in a
    // text set before, which the classification should flag, not pass as unknown words.
    private static (NameScanner Scanner, HashSet<string> Keys) Names(ReadScope scope)
    {
        var entries = new List<NameEntry>();
        var entityIds = scope.LoadAllEntities();
        foreach (var id in entityIds)
        {
            if (scope.Entity(id) is not { } entity)
            {
                continue;
            }

            entries.Add(new NameEntry(entity.Row.Name, new Reading(id, NameMatchKinds.Name, null, null)));
            entries.AddRange(entity.Aliases.Select(a => new NameEntry(a.Alias, new Reading(id, NameMatchKinds.Alias, a.Visibility, null))));
            foreach (var row in entity.Entries.Where(e => !string.IsNullOrWhiteSpace(e.KnownAs)))
            {
                var whose = row.KnowerKind == CampaignValues.KnowerKinds.Character && row.KnowerId is not null
                    ? scope.AuthorRef(row.KnowerId)
                    : row.KnowerKind;
                entries.Add(new NameEntry(row.KnownAs!, new Reading(id, NameMatchKinds.KnownAs, whose, null)));
            }
        }

        // The other side of every cross-link that touches this campaign: its name and all its aliases.
        foreach (var chunk in entityIds.Chunk(400))
        {
            var others = scope.Connection.Query<(string OtherId, string Name, string CampaignSlug)>(
                "SELECT e.id, e.name, c.slug FROM cross_link x JOIN entity e ON e.id = CASE WHEN x.a_id IN @ids THEN x.b_id ELSE x.a_id END " +
                "JOIN campaign c ON c.id = e.campaign_id WHERE (x.a_id IN @ids OR x.b_id IN @ids) AND e.campaign_id <> @campaignId",
                new { ids = chunk, campaignId = scope.Campaign.Id }).ToList();
            foreach (var (otherId, name, slug) in others)
            {
                entries.Add(new NameEntry(name, new Reading(otherId, NameMatchKinds.Name, null, slug)));
                entries.AddRange(scope.Connection.Query<(string Alias, string Visibility)>(
                        "SELECT alias, visibility FROM entity_alias WHERE entity_id = @otherId", new { otherId })
                    .Select(a => new NameEntry(a.Alias, new Reading(otherId, NameMatchKinds.Alias, a.Visibility, slug))));
            }
        }

        var keys = entries.Select(e => CampaignText.KeyWithoutArticle(e.Surface)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        return (new NameScanner(entries), keys);
    }

    // Every name the perspective uses (its views' UsedNames: for the author every name and alias), for KnownNames.
    private static IEnumerable<string> UsedNames(ReadScope scope, PerspectiveContext who) =>
        scope.LoadAllEntities().Select(scope.Entity).OfType<EntityState>().SelectMany(entity => scope.ViewFor(who, entity).UsedNames);

    private static NameMention Mention(ReadScope scope, NameHit hit, PerspectiveContext speaker, PerspectiveContext? audience)
    {
        var candidates = new List<NameCandidate>();
        foreach (var group in hit.Entries.Select(e => (e.Surface, Reading: (Reading)e.Payload))
                     .GroupBy(e => (e.Reading.EntityId, e.Reading.Campaign)))
        {
            var first = group.First();
            var matchedKey = CampaignText.KeyWithoutArticle(first.Surface);
            if (first.Reading.Campaign is { } otherCampaign)
            {
                var otherName = scope.Connection.QueryFirstOrDefault<string?>("SELECT name FROM entity WHERE id = @id", new { id = first.Reading.EntityId });
                candidates.Add(new NameCandidate(scope.AuthorRef(first.Reading.EntityId), otherName ?? first.Surface, first.Reading.MatchedAs,
                    first.Reading.Detail, speaker.IsAuthorView ? NameClasses.Ok : NameClasses.CrossCampaign, null,
                    audience is null ? null : NameClasses.RevealsToAudience, null, otherCampaign));
                continue;
            }

            var entity = scope.Entity(first.Reading.EntityId)!;
            var (speakerClass, speakerName) = Classify(scope, speaker, entity, matchedKey);
            string? audienceClass = null;
            string? audienceName = null;
            if (audience is not null)
            {
                var (cls, name) = Classify(scope, audience, entity, matchedKey);
                audienceClass = cls == NameClasses.Ok ? NameClasses.Ok : NameClasses.RevealsToAudience;
                audienceName = name;
            }

            var best = group.OrderBy(g => g.Reading.MatchedAs == NameMatchKinds.Name ? 0 : g.Reading.MatchedAs == NameMatchKinds.Alias ? 1 : 2).First();
            candidates.Add(new NameCandidate(entity.Ref ?? scope.AuthorRef(entity.Row.Id), entity.Row.Name, best.Reading.MatchedAs,
                best.Reading.Detail, speakerClass, speakerName, audienceClass, audienceName, null));
        }

        var classification = candidates.Any(c => c.Classification == NameClasses.Ok) ? NameClasses.Ok
            : candidates.Any(c => c.Classification == NameClasses.OtherName) ? NameClasses.OtherName
            : candidates.Any(c => c.Classification == NameClasses.UnknownEntity) ? NameClasses.UnknownEntity
            : NameClasses.CrossCampaign;
        string? audienceClassification = audience is null ? null
            : candidates.Any(c => c.AudienceClassification == NameClasses.Ok) ? NameClasses.Ok
            : NameClasses.RevealsToAudience;
        return new NameMention(hit.Matched, hit.Start, hit.Length, classification, audienceClassification, candidates);
    }

    // ok when the matched name is one the perspective uses for the entity (its view's UsedNames: the name it is shown, the
    // aliases it may see, and under a disguise its known_as and the party aliases it shares, review U01/L11); other_name
    // when it knows the entity by another. A party alias is no name of the public's, or of a character outside the party,
    // so a crowd that hears one is told something (reveals_to_audience); a party alias of an entity the party knows by a
    // known_as is the party's own name, and "the old king" is fine to sing to it.
    private static (string Class, string? Name) Classify(ReadScope scope, PerspectiveContext who, EntityState entity, string matchedKey)
    {
        if (who.IsAuthorView)
        {
            return (NameClasses.Ok, entity.Row.Name);
        }

        var view = scope.ViewFor(who, entity);
        if (!view.Visible)
        {
            return (NameClasses.UnknownEntity, null);
        }

        return view.UsedNames.Contains(matchedKey, StringComparer.Ordinal)
            ? (NameClasses.Ok, view.DisplayName)
            : (NameClasses.OtherName, view.DisplayName);
    }

    /// <summary>
    /// The partial-name flags (review U02, fix FQ4): every capitalised word of the text, outside the names and forbidden
    /// words already found, that is a distinctive word (<see cref="CampaignWords.DistinctiveWords"/>: four letters or more,
    /// not a common word) of a name the speaker does not use, and is no word of a name the speaker does use; then the
    /// same for a diegetic text's audience, for the words not flagged for the speaker. The names a perspective does not use
    /// are the names of the in-world things it does not know by them (<see cref="ProperNameKinds"/>), every alias outside
    /// its view's used names (an author or restricted alias always; a party alias for the public), and, for a speaker the
    /// vocabulary rules apply to, every term an active gate or reveal rule forbids. Returns the hard flags, and apart the
    /// words that open a line or a sentence, for review.
    ///
    /// <para>
    /// <b>Why capitalised words only, and why those kinds:</b> a capital is how a lyric marks a name ("haul your Cage back
    /// up the mountain"); the same word in lower case is ordinary English ("a cage of ribs"). And a quest, thread, secret,
    /// session or rule is titled with ordinary words ("The old king's errand", "No name, no timespan"), whose capitalised
    /// "Name" at the start of a line gives nothing away; flagged, they would fail every draft and teach the model to ignore
    /// the check. An alias or a forbidden term is a name by definition, whatever the entity's kind.
    /// </para>
    /// <para>
    /// <b>Why a word that opens a line or a sentence is only for review:</b> there the capital is the line's, not the
    /// name's. Every line of a lyric starts with one, so "Peaceful days ahead, my friends" failed against "The Peaceful One",
    /// "Silver moon over the sea" against an author-only "Silver Harbor", and "Lady luck is on our side" against "Lady
    /// Morrow": hard flags on plain English teach the model to ignore the check, which is the failure it exists to prevent.
    /// Listed for review with the name it may give away, the model still sees it. Written in capitals throughout
    /// ("AXIOM rising") it is marked as a name wherever it stands, and stays a hard flag.
    /// </para>
    /// <para>
    /// <b>Why a title alone gives nothing away:</b> "Captain Vexmoor" is given away by "Vexmoor"; "my Captain" names any
    /// captain. A title or rank (<see cref="TitleWords"/>) counts as a name's word only when it is the name's only
    /// distinctive word ("The Old King" is given away by "King").
    /// </para>
    /// </summary>
    private static (List<PartialName> Hard, List<PartialName> ForReview) PartialNames(
        ReadScope scope,
        string text,
        IReadOnlyList<NameHit> hits,
        IReadOnlyList<ForbiddenFinding> forbidden,
        IReadOnlyList<ForbiddenRule> rules,
        PerspectiveContext speaker,
        PerspectiveContext? audience)
    {
        var taken = hits.Select(h => (h.Start, End: h.Start + h.Length)).Concat(forbidden.Select(f => (f.Start, End: f.Start + f.Length))).ToList();
        var words = CampaignWords.Split(text).Where(w => w.Capitalised && !taken.Any(t => w.Start < t.End && w.End > t.Start)).ToList();
        var found = new List<(PartialName Name, bool Hard)>();
        if (words.Count == 0)
        {
            return ([], []);
        }

        foreach (var who in new[] { speaker, audience })
        {
            if (who is null || who.IsAuthorView)
            {
                continue;
            }

            var (used, unused) = NamesOf(scope, who, who == speaker ? rules : []);
            foreach (var word in words)
            {
                if (found.Any(f => f.Name.Start == word.Start) || Contains(used, word.Key))
                {
                    continue;
                }

                if (unused.FirstOrDefault(n => Contains(n.Words, word.Key)) is { } name)
                {
                    var surface = text[word.Start..word.End];
                    found.Add((new PartialName(surface, word.Start, word.End - word.Start, who.Perspective.Text,
                        name.FullName, name.Source, name.SourceKind, name.Visibility, name.KnownAs), AllCapitals(surface) || !OpensASentence(text, word.Start)));
                }
            }
        }

        var ordered = found.OrderBy(f => f.Name.Start).ToList();
        return (ordered.Where(f => f.Hard).Select(f => f.Name).ToList(), ordered.Where(f => !f.Hard).Select(f => f.Name).ToList());

        // A word of the text against a name's words, as the name scanner compares a last word: equal, or with a plural or
        // possessive s on either side ("Cages" and "Cage's" are words of "Axiom Cage").
        static bool Contains(IReadOnlyCollection<string> nameWords, string key) =>
            nameWords.Contains(key) || (key.Length > 1 && key.EndsWith('s') && nameWords.Contains(key[..^1])) || nameWords.Contains(key + "s");

        static bool AllCapitals(string word) => word.Count(char.IsLetter) >= 2 && word.Where(char.IsLetter).All(char.IsUpper);
    }

    /// <summary>
    /// Whether the word starting at <paramref name="start"/> opens a line or a sentence: between it and the start of the
    /// text, a line break or a sentence end (<c>. ! ? … :</c>) stand only spaces, opening quotes and brackets, and list,
    /// quote or heading marks (<c>- * # &gt;</c>, dashes). Its capital is then the line's (see <see cref="PartialNames"/>).
    /// </summary>
    internal static bool OpensASentence(string text, int start)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var i = start - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '.' or '!' or '?' or '…' or ':')
            {
                return true;
            }

            if (!char.IsWhiteSpace(c) && c is not ('"' or '\'' or '“' or '‘' or '«' or '(' or '[' or '{' or '*' or '_' or '#' or '>' or '-' or '–' or '—' or '•' or '~'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// For one perspective: the distinctive words of every name it uses, and the names it does not use that a word can give
    /// away (see <see cref="PartialNames"/>), entity names first, then aliases, then forbidden terms.
    /// </summary>
    private static (HashSet<string> Used, List<UnusedName> Unused) NamesOf(ReadScope scope, PerspectiveContext who, IReadOnlyList<ForbiddenRule> rules)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<UnusedName>();
        var aliases = new List<UnusedName>();
        foreach (var entity in scope.LoadAllEntities().Select(scope.Entity).OfType<EntityState>())
        {
            var view = scope.ViewFor(who, entity);
            var surfaces = new List<string> { entity.Row.Name };
            surfaces.AddRange(entity.Aliases.Select(a => a.Alias));
            surfaces.AddRange(entity.Entries.Select(e => e.KnownAs).OfType<string>());
            foreach (var surface in surfaces.Where(s => view.UsedNames.Contains(CampaignText.KeyWithoutArticle(s), StringComparer.Ordinal)))
            {
                used.UnionWith(CampaignWords.DistinctiveWords(surface));
            }

            var knownAs = view.Visible && view.UsedNames.Count > 0 ? view.DisplayName : null;
            var source = scope.AuthorRef(entity.Row.Id);
            if (ProperNameKinds.Contains(entity.Row.Kind) && !view.UsedNames.Contains(CampaignText.KeyWithoutArticle(entity.Row.Name), StringComparer.Ordinal))
            {
                names.Add(new UnusedName(entity.Row.Name, GiveawayWords(entity.Row.Name), source, PartialNameSources.Name, null, knownAs));
            }

            aliases.AddRange(entity.Aliases
                .Where(a => !view.UsedNames.Contains(CampaignText.KeyWithoutArticle(a.Alias), StringComparer.Ordinal))
                .Select(a => new UnusedName(a.Alias, GiveawayWords(a.Alias), source, PartialNameSources.Alias, a.Visibility, knownAs)));
        }

        var terms = rules.SelectMany(r => r.Terms.Select(t => new UnusedName(t, GiveawayWords(t), r.Source, PartialNameSources.ForbiddenTerm, null, null)));
        return (used, names.Concat(aliases).Concat(terms).Where(n => n.Words.Count > 0).ToList());
    }

    // The words of a name that give it away alone (CampaignWords.DistinctiveWords), without a title or rank when the name
    // has another distinctive word: "Captain Vexmoor" is given away by "Vexmoor", not by "Captain" (PartialNames).
    private static IReadOnlyList<string> GiveawayWords(string name)
    {
        var words = CampaignWords.DistinctiveWords(name);
        return words.Any(w => !TitleWords.Contains(w)) ? words.Where(w => !TitleWords.Contains(w)).ToList() : words;
    }

    // Facts about a named entity (this campaign), then the top fact_fts matches for the text's significant words.
    private static List<(FactState Fact, string Why)> RelatedFacts(ReadScope scope, string text, IReadOnlyList<string> namedEntityIds)
    {
        var related = new List<(string FactId, string Why)>();
        foreach (var entityId in namedEntityIds)
        {
            var reference = scope.AuthorRef(entityId);
            related.AddRange(scope.Connection.Query<string>(
                    "SELECT l.fact_id FROM fact_link l JOIN fact f ON f.id = l.fact_id WHERE l.entity_id = @entityId AND l.role = @about " +
                    "ORDER BY f.seq", new { entityId, about = CampaignValues.FactLinkRoles.About })
                .Select(f => (f, "about " + reference)));
        }

        var words = ReadText.Words(text).Select(w => w.Key)
            .Where(w => (w.Length > 1 || w.All(char.IsDigit)) && !StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFtsWords)
            .ToList();
        if (words.Count > 0 && Fts5Query.AnyTerms(string.Join(' ', words)) is { } match)
        {
            related.AddRange(scope.Connection.Query<string>(
                    "SELECT f.id FROM fact_fts JOIN fact f ON f.seq = fact_fts.rowid WHERE fact_fts MATCH @match " +
                    $"AND f.campaign_id = @campaignId AND f.deleted_at IS NULL ORDER BY bm25(fact_fts), f.seq LIMIT {MaxFtsFacts}",
                    new { match, campaignId = scope.Campaign.Id })
                .Select(f => (f, "shares words with the text")));
        }

        scope.LoadFacts(related.Select(r => r.FactId));
        return related.DistinctBy(r => r.FactId, StringComparer.Ordinal)
            .Select(r => (Fact: scope.Fact(r.FactId), r.Why))
            .Where(r => r.Fact is not null)
            .Select(r => (r.Fact!, r.Why))
            .ToList();
    }

    private static List<SecretAtRisk> SecretsAtRisk(ReadScope scope, PerspectiveContext speaker, PerspectiveContext audience, List<(FactState Fact, string Why)> related)
    {
        var candidates = related.Select(r => (r.Fact, r.Why, Reason: RiskReasons.RelatedToText)).ToList();
        var aboutSpeaker = scope.Connection.Query<string>(
            "SELECT l.fact_id FROM fact_link l JOIN fact f ON f.id = l.fact_id WHERE l.entity_id = @id AND l.role = @about ORDER BY f.seq",
            new { id = speaker.CharacterId, about = CampaignValues.FactLinkRoles.About }).ToList();
        scope.LoadFacts(aboutSpeaker);
        var speakerRef = scope.AuthorRef(speaker.CharacterId);
        foreach (var factId in aboutSpeaker)
        {
            if (scope.Fact(factId) is { } fact && candidates.All(c => c.Fact.Row.Id != factId))
            {
                candidates.Add((fact, "about " + speakerRef, RiskReasons.AboutSpeaker));
            }
        }

        // Both sides are judged as their own reads judge them (ReadScope.Shows): a fact is at risk only when the speaker's
        // reads show it and the audience's do not. An author-only fact is shown to neither, whatever rows it has, so it is
        // never "known to the speaker" here (and the audience's standing can never read "knows" for a fact at risk).
        var risks = new List<SecretAtRisk>();
        foreach (var (fact, why, reason) in candidates)
        {
            var speakerVerdict = scope.VerdictFor(speaker, fact);
            var audienceVerdict = scope.VerdictFor(audience, fact);
            if (ReadScope.Shows(speaker, fact.Row, speakerVerdict) && !ReadScope.Shows(audience, fact.Row, audienceVerdict))
            {
                risks.Add(new SecretAtRisk(Finding(scope, fact, speakerVerdict, why), reason, Standings.Of(audienceVerdict.Standing)));
            }
        }

        return risks;
    }

    private static List<StaleFinding> Stale(ReadScope scope, IReadOnlyList<FactState> related)
    {
        var edges = scope.Connection.Query<(string FactId, string DependsOn)>(
            "SELECT d.fact_id, d.depends_on FROM fact_dependency d JOIN fact f ON f.id = d.fact_id WHERE f.campaign_id = @campaignId",
            new { campaignId = scope.Campaign.Id }).ToList();
        var factIds = scope.Connection.Query<string>(
            "SELECT id FROM fact WHERE campaign_id = @campaignId" + (scope.AsOf is null ? " AND deleted_at IS NULL" : string.Empty),
            new { campaignId = scope.Campaign.Id }).ToList();
        scope.LoadFacts(factIds);
        var superseded = factIds.Where(id => scope.Fact(id)?.Row.CanonStatus == CampaignValues.CanonStatuses.Superseded).ToList();
        var staleBy = Supersession.StaleFacts(superseded, edges).ToDictionary(s => s.FactId, StringComparer.Ordinal);
        var findings = new List<StaleFinding>();
        foreach (var fact in related)
        {
            if (fact.Row.CanonStatus == CampaignValues.CanonStatuses.Superseded)
            {
                findings.Add(new StaleFinding(fact.Ref, fact.Row.Statement, fact.Ref, 0));
            }
            else if (staleBy.TryGetValue(fact.Row.Id, out var stale))
            {
                findings.Add(new StaleFinding(fact.Ref, fact.Row.Statement, ReadGates.FactHandle(scope, stale.Superseded), stale.Depth));
            }
        }

        return findings;
    }

    private static FactFinding Finding(ReadScope scope, FactState fact, KnowledgeVerdict verdict, string why) =>
        new(fact.Ref, fact.Row.Code, fact.Row.Statement, Standings.Of(verdict.Standing), verdict.State, fact.Row.Truth, fact.Row.CanonStatus,
            CampaignSearch.KnownByLabels(fact.Entries, scope.AuthorRef, scope.AsOf), why, verdict.Explanation);

    // A related fact outside the story for every player-side view: its verdict's state is kept (author-facing), but its
    // standing says why it is not known, whatever that state claims.
    private static FactFinding NotInPlayFinding(ReadScope scope, FactState fact, KnowledgeVerdict verdict, string why) =>
        new(fact.Ref, fact.Row.Code, fact.Row.Statement, Standings.NotInPlay, verdict.State, fact.Row.Truth, fact.Row.CanonStatus,
            CampaignSearch.KnownByLabels(fact.Entries, scope.AuthorRef, scope.AsOf), why,
            $"not in play ({fact.Row.CanonStatus}): outside the author view nobody knows it as part of the story");

    // An author-only fact a knowledge row says the speaker knows: the row's state is kept (author-facing), the standing says
    // why it is not known anyway. The explanation follows the standing ("author only: …"), so it does not repeat it.
    private static FactFinding AuthorOnlyFinding(ReadScope scope, FactState fact, KnowledgeVerdict verdict, string why) =>
        new(fact.Ref, fact.Row.Code, fact.Row.Statement, Standings.AuthorOnly, verdict.State, fact.Row.Truth, fact.Row.CanonStatus,
            CampaignSearch.KnownByLabels(fact.Entries, scope.AuthorRef, scope.AsOf), why,
            "its knowledge rows do not make it known outside the author view");

    private static string WithoutPossessive(string key) =>
        key.Length > 1 && key.EndsWith('s') ? key[..^1] : key;

    /// <summary>What a scanned name stands for: an entity (this campaign's, or another's when <see cref="Campaign"/> is set).</summary>
    private sealed record Reading(string EntityId, string MatchedAs, string? Detail, string? Campaign);

    /// <summary>A name a perspective does not use, with the words of it that give it away alone (<see cref="PartialNames"/>).</summary>
    private sealed record UnusedName(
        string FullName,
        IReadOnlyList<string> Words,
        string Source,
        string SourceKind,
        string? Visibility,
        string? KnownAs);
}
