using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using Microsoft.Data.Sqlite;

namespace DndMcp.Repository.Campaign.Read;

/// <summary>Why a text failed the view-text check (<see cref="ViewTextFinding.Kind"/>).</summary>
public static class ViewTextFindingKinds
{
    /// <summary>A name the view does not use: unknown_entity, other_name or cross_campaign (<see cref="NameClasses"/>).</summary>
    public const string Name = "name";

    /// <summary>A word an active gate or reveal rule forbids for the view.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>A capitalised word of a name the view does not use, said on its own (a partial name).</summary>
    public const string PartialName = "partial_name";

    /// <summary>
    /// A distinctive word, in any case, that occurs in text the view cannot see and in no text it can (the hidden-word rule,
    /// contract §6.12 v2.1): "Void-touched" when only the Nester's secret says "Void-touched", "keras" when only a secret
    /// and an author-only question say "Keras".
    /// </summary>
    public const string HiddenWord = "hidden_word";

    /// <summary>
    /// NOT a failure (F3, review F2R02): a word that starts with a POSSIBLE name, a word capitalised mid-sentence in text the
    /// view cannot see ("Baal" in "every eaten fruit thins Baal's seal") that is no recorded name. The text is printed; the
    /// author gets a D20b warning and decides ("'Baalite cultist' starts with 'Baal', a name in hidden text"). Prose is
    /// warn-only because DM notes are ordinary English with capitals in it ("the Red Dragon", "the Hunt"), whose words as
    /// hard stems hid Dragonborn and Hunter from every party view (reviews UR01, F2R01).
    /// </summary>
    public const string PossibleName = "possible_name";
}

/// <summary>
/// One reason a text failed. AUTHOR-FACING ONLY (it names the entity or rule the text gives away): a non-author model
/// never carries one; it carries only the outcome (print the text, or its stand-in).
/// </summary>
/// <param name="Kind">One of <see cref="ViewTextFindingKinds"/>.</param>
/// <param name="Matched">The words as the text spells them.</param>
/// <param name="Classification">
/// For a name, its <see cref="NameClasses"/> value; for a possible name, the hidden word it starts with, as the hidden text
/// spells it ("Baal"); for the others the kind again.
/// </param>
/// <param name="Sources">
/// The author refs the words belong to (<c>character:the-nester</c>: its name, summary, body, an alias or its secret), the
/// fact the view does not know or the gate forbidding them (<c>f:12</c>), the rule forbidding them
/// (<c>rule:old-king-no-name</c>), another campaign's entity (<c>one-piece/item:axiom-cage</c>).
/// </param>
public sealed record ViewTextFinding(string Kind, string Matched, string Classification, IReadOnlyList<string> Sources);

/// <summary>The check's outcome for one text.</summary>
/// <param name="Text">The text as given.</param>
/// <param name="Passes">
/// The view may print it: every name in it is one the view uses, no partial name, no forbidden word, and no hidden word
/// (a distinctive word only text the view cannot see holds).
/// </param>
/// <param name="HasPossibleInventions">
/// It holds a proper noun that matches no name in the campaign (the check's possible inventions). Not a failure on its
/// own: a custom combatant's name in a dice label also needs none (§6.10), since a name the campaign has never recorded is
/// one no reader can be shown to know.
/// </param>
/// <param name="Findings">Why it failed (author-facing; empty when it passes).</param>
public sealed record ViewTextVerdict(string Text, bool Passes, bool HasPossibleInventions, IReadOnlyList<ViewTextFinding> Findings)
{
    /// <summary>
    /// The words of a text that passes which start with a possible name of hidden prose (kind
    /// <see cref="ViewTextFindingKinds.PossibleName"/>): printed, but worth a D20b warning to the author (F3, review F2R02).
    /// Author-facing, like <see cref="Findings"/>; empty when the text fails (its findings say why) or holds none.
    /// </summary>
    public IReadOnlyList<ViewTextFinding> PossibleNames { get; init; } = [];
}

/// <summary>The check's outcome for every text of one render, in the order given.</summary>
public sealed record ViewTextResult(string View, IReadOnlyList<ViewTextVerdict> Texts)
{
    /// <summary>Whether text <paramref name="index"/> may be printed in the view.</summary>
    public bool Passes(int index) => Texts[index].Passes;

    /// <summary>Whether every text may be printed.</summary>
    public bool AllPass => Texts.All(t => t.Passes);
}

/// <summary>
/// The view-text check (contract §6.12, D20b): may a free text the author typed (a combatant's name, an effect's or a
/// concentration's name, a sheet's class, subclass, species or condition, a custom name in a dice label) be printed in a
/// non-author view? Phase 7 filters player-readable text at READ time with it, since a typed name ("The Nester", "sealed
/// by the Nester", a "Void-touched" subclass) can name what the view must not know.
///
/// <para>
/// <b>Two rules, both must pass.</b> First the knowledge check's (<see cref="KnowledgeCheck"/>), not a second scanner:
/// <c>KnowledgeCheck.Check(connection, campaign, new CheckRequest(text, Speaker: view))</c> raises no hard flag for the
/// text: every name it contains is one the view uses (unknown_entity, other_name and cross_campaign fail; names match
/// in any case, so "keras" is the author alias "Keras"), no partial name (a capitalised word of a name the view does not
/// use: "Cage" for the Axiom Cage), no word or pattern an active gate or reveal rule forbids. So "what the party may
/// read" means what the party's own reads and the author's <c>campaign_knowledge check</c> say it means.
/// </para>
/// <para>
/// <b>Then the hidden-word rule</b> (contract §6.12 as ruled after stage 2's check found "Void-touched" printed to the
/// party: the knowledge check finds only names the campaign RECORDS, and a word the author wrote only in a secret is no
/// name, just a "possible invention", which passes). A text fails when it holds, in any case, a distinctive word
/// (<see cref="CampaignWords.DistinctiveWords"/>: four letters or more, not common English; a plural or possessive s on
/// either side matches, as the name scanner's last word does) that occurs in text the view cannot see and in no text it
/// can, or a word that STARTS with such a word taken from a hidden NAME (F1, review L02: "Kerasian", "Baalite",
/// "Nesterling" give the name away as the name does) or with a one-word active forbidden term ("Sealbreaker"), unless that
/// word is itself one the view can read. The prefix rule's stems are names only (F2, reviews UR01/RR02/CR02/LR01; F3,
/// reviews F2R01/F2R02): every word of the true name of a person, place, faction, thing, legend, work or event the view
/// does not see, sees disguised or knows under another name; every word of an alias it does not use; every name and alias
/// of a cross-linked entity in another campaign (the knowledge check's cross_campaign set); and one-word forbidden terms.
/// Never a title's words (a question's, a quest's, a secret's, a rule's): titles are ordinary words, and in Title Case
/// ("Slay the Red Dragon before the Hunt") they hid Hunter and Halfling again. A word of a secret, a summary, a body or a
/// fact statement keeps the whole-word rule: DM notes are ordinary English ("they hunt the half-blood heirs", "a storm
/// spirit bound in stone"), and as stems their words hid Dragonborn, Halfling, Hunter's Mark, Moonbeam and Spirit
/// Guardians from every party view. A word such prose capitalises mid-sentence ("Baal" in "thins Baal's seal") is only a
/// POSSIBLE name: a word starting with it is printed, and the author's step gets a D20b warning
/// (<see cref="ViewTextVerdict.PossibleNames"/>), so the author decides. A start that is a plural or possessive (an s
/// stripped) is tested for being a common word after the stripping: "Longstrider" starts with "longs", which is "long",
/// no giveaway.
/// The text it cannot see: every entity's <c>secret_md</c>; the true name, summary and body of every entity it
/// does not see or sees disguised (a secret's body IS its secret: Belmakor's ambition "Reclaim the blighted surface…");
/// every alias the view does not use (an author or restricted alias always; a party alias for the public); the statement
/// of every non-public fact it does not know, gated or not (One Piece's restricted clues hold "the Cage"); every active
/// forbidden term; and the names and aliases of other campaigns' entities cross-linked to this one's (the firewall the
/// name scanner keeps for whole names). The text it can see: the name, summary and body of every entity it is shown, the
/// name it knows a disguised one by, the aliases it uses, and the text of every fact it knows (its own phrasing, when it
/// has one). So "Void-touched" (the Nester's secret) and "Oath of the Axiom Cage" (the clues) fail for the party, and
/// "Order of the Deep Sea" passes ("order" is in an unknown clue, but also in the party alias of G.O.D.S. Co.); a word
/// nobody wrote anywhere ("Goblin Boss") still passes here, as a possible invention, except in a dice label, which also
/// refuses those (<see cref="SessionSafeNames"/>). What the rule gives up, by the contract's choice: a mundane word that
/// only hidden text holds ("battle" in One Piece's Protector secret, "temple" in a clue) is left out with it, and a
/// word the view can read somewhere ("Cage", once the party's own words for the testimony say it) passes wherever it
/// stands.
/// </para>
/// <para>
/// <b>One check per render</b>, over the texts joined by newlines, with each finding mapped back to its text by offset
/// (never one check per text), and the two word sets built once from the render's <see cref="ReadScope"/>: both load
/// every entity, alias, knowledge row, fact and gate of the campaign, and a board of twenty rows would otherwise load them
/// twenty times. The one exception: a name the scanner read across the join (one text ending "Old", the next opening
/// "King") may have hidden a name inside either text (the longest match wins), so a text such a match touches is checked
/// again on its own; that is rare, and it never lets a text pass that would fail alone. When the texts of one render
/// together pass the check's length cap they are checked in as few groups as fit; a single text past the cap fails
/// unchecked (no name is that long).
/// </para>
/// <para>
/// <b>Stricter than the knowledge check's pass in one more place, on purpose:</b> a partial name the check lists only "for
/// review" because its word opens a line counts as a failure here. Joined by newlines, every text opens a line, and a
/// label's first word is no sentence's capital: "Cage-bound" as a subclass gives the Cage away exactly as "the
/// Cage-bound" would.
/// </para>
/// <para>
/// <b>On a read connection, never inside a write transaction</b> (the check's <see cref="ReadScope"/> runs without one).
/// The combat layer computes what it needs before its transaction opens.
/// </para>
/// </summary>
public static class ViewTextCheck
{
    // The most sources one finding names (an author warning, not a ledger).
    private const int MaxSources = 5;

    /// <summary>
    /// Checks <paramref name="texts"/> for <paramref name="view"/> (as of <paramref name="asOfSession"/>, when given). The
    /// author view (and <c>dm</c> in a DM campaign) passes everything without a check. Blank texts pass (nothing in them).
    /// </summary>
    /// <exception cref="DndInputException">An unknown character perspective, or an as_of_session out of range.</exception>
    public static ViewTextResult Check(SqliteConnection connection, CampaignRow campaign, Perspective view, IReadOnlyList<string?> texts, int? asOfSession = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(texts);
        if (PerspectiveContext.For(view, campaign.Role).IsAuthorView || texts.All(string.IsNullOrWhiteSpace))
        {
            return Unchecked(view.Text, texts);
        }

        return Check(ReadScope.Open(connection, campaign, view, asOfSession), texts);
    }

    /// <summary>Checks one render's texts on a read connection of its own (<see cref="Check(SqliteConnection, CampaignRow, Perspective, IReadOnlyList{string?}, int?)"/>).</summary>
    public static ViewTextResult Check(CampaignDatabase database, CampaignRow campaign, Perspective view, IReadOnlyList<string?> texts, int? asOfSession = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var connection = ReadConnection.Open(database);
        return Check(connection, campaign, view, texts, asOfSession);
    }

    /// <summary>
    /// Checks one render's texts for the view of a <see cref="ReadScope"/> the reader already opened (its perspective, its
    /// session): the hidden and visible words are built from that scope, once.
    /// </summary>
    internal static ViewTextResult Check(ReadScope scope, IReadOnlyList<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(texts);
        var view = scope.Who.Perspective;
        if (scope.IsAuthorView || texts.All(string.IsNullOrWhiteSpace))
        {
            return Unchecked(view.Text, texts);
        }

        var verdicts = texts.Select(t => new ViewTextVerdict(t ?? string.Empty, true, false, [])).ToArray();
        for (var i = 0; i < texts.Count; i++)
        {
            if (texts[i] is { Length: > CampaignLimits.MaxCheckTextLength } tooLong)
            {
                // Past the check's cap nothing is checked, so nothing is passed: no name a view may read is that long.
                verdicts[i] = new ViewTextVerdict(tooLong, false, false, []);
            }
        }

        var (connection, campaign, asOfSession) = (scope.Connection, scope.Campaign, scope.AsOf);
        foreach (var chunk in Chunks(texts))
        {
            var joined = string.Join('\n', chunk.Select(i => texts[i]));
            var result = KnowledgeCheck.Check(connection, campaign, new CheckRequest(joined, Speaker: view, AsOfSession: asOfSession));
            var offsets = new List<(int Index, int Start, int End)>();
            var at = 0;
            foreach (var index in chunk)
            {
                var length = texts[index]!.Length;
                offsets.Add((index, at, at + length));
                at += length + 1;
            }

            var findings = Findings(result);
            foreach (var (index, start, end) in offsets)
            {
                var text = texts[index]!;
                if (result.Names.Any(m => m.Start < end && m.Start + m.Length > start && (m.Start < start || m.Start + m.Length > end)))
                {
                    // A name the scanner read across the join ("Old" ending one text, "King" opening the next): its longest
                    // match may have swallowed a name this text holds on its own, so this text is checked alone.
                    verdicts[index] = Alone(connection, campaign, view, text, asOfSession);
                    continue;
                }

                var own = findings.Where(f => f.Start < end && f.Start + f.Length > start).Select(f => f.Finding).ToList();
                var inventions = result.PossibleInventions.Any(candidate => Contains(text, candidate));
                verdicts[index] = new ViewTextVerdict(text, own.Count == 0, inventions, own);
            }
        }

        // The hidden-word rule (class summary), over every text the knowledge check read, with the render's word sets.
        var checkedTexts = Chunks(texts).SelectMany(c => c).ToList();
        if (checkedTexts.Count > 0)
        {
            var words = WordSets.For(scope);
            foreach (var index in checkedTexts)
            {
                var (giveaways, possible) = words.Giveaways(texts[index]!);
                var hidden = giveaways
                    .Select(g => new ViewTextFinding(ViewTextFindingKinds.HiddenWord, g.Matched, ViewTextFindingKinds.HiddenWord, g.Sources))
                    .ToList();
                var verdict = verdicts[index];
                if (hidden.Count > 0)
                {
                    verdicts[index] = verdict with { Passes = false, Findings = [.. verdict.Findings, .. hidden] };
                }
                else if (verdict.Passes && possible.Count > 0)
                {
                    verdicts[index] = verdict with
                    {
                        PossibleNames = possible.Select(p => new ViewTextFinding(ViewTextFindingKinds.PossibleName, p.Matched, p.Stem, p.Sources)).ToList(),
                    };
                }
            }
        }

        return new ViewTextResult(view.Text, verdicts);
    }

    // Every text passes, with no finding: the author view, or nothing to read.
    private static ViewTextResult Unchecked(string view, IReadOnlyList<string?> texts) =>
        new(view, texts.Select(t => new ViewTextVerdict(t ?? string.Empty, true, false, [])).ToList());

    // One text checked on its own: the fallback when the joined check read a name across the join.
    private static ViewTextVerdict Alone(SqliteConnection connection, CampaignRow campaign, Perspective view, string text, int? asOfSession)
    {
        var result = KnowledgeCheck.Check(connection, campaign, new CheckRequest(text, Speaker: view, AsOfSession: asOfSession));
        var own = Findings(result).Select(f => f.Finding).ToList();
        return new ViewTextVerdict(text, own.Count == 0, result.PossibleInventions.Count > 0, own);
    }

    // The hard flags of the check, plus the partial names it lists for review (class summary), with their offsets.
    private static List<(int Start, int Length, ViewTextFinding Finding)> Findings(CheckResult result)
    {
        var findings = new List<(int, int, ViewTextFinding)>();
        foreach (var mention in result.Names.Where(m => m.Classification != NameClasses.Ok))
        {
            findings.Add((mention.Start, mention.Length,
                new ViewTextFinding(ViewTextFindingKinds.Name, mention.Matched, mention.Classification, mention.Candidates.Select(c => c.Ref).Distinct().ToList())));
        }

        foreach (var forbidden in result.Forbidden)
        {
            findings.Add((forbidden.Start, forbidden.Length,
                new ViewTextFinding(ViewTextFindingKinds.Forbidden, forbidden.Matched, ViewTextFindingKinds.Forbidden, [forbidden.Source])));
        }

        foreach (var partial in result.PartialNames.Concat(result.PossiblePartialNames))
        {
            findings.Add((partial.Start, partial.Length,
                new ViewTextFinding(ViewTextFindingKinds.PartialName, partial.Matched, ViewTextFindingKinds.PartialName, [partial.Source])));
        }

        return findings;
    }

    // The non-blank texts within the check's cap, in groups whose joined length stays within it (a longer text fails
    // unchecked, above).
    private static IEnumerable<List<int>> Chunks(IReadOnlyList<string?> texts)
    {
        var chunk = new List<int>();
        var length = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]) || texts[i]!.Length > CampaignLimits.MaxCheckTextLength)
            {
                continue;
            }

            var size = texts[i]!.Length + (chunk.Count == 0 ? 0 : 1);
            if (chunk.Count > 0 && length + size > CampaignLimits.MaxCheckTextLength)
            {
                yield return chunk;
                chunk = [];
                length = 0;
                size = texts[i]!.Length;
            }

            chunk.Add(i);
            length += size;
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }

    // A possible invention belongs to a text when the text holds it as written (candidates are runs of one line).
    private static bool Contains(string text, string candidate) =>
        candidate.Length > 0 && text.Contains(candidate, StringComparison.Ordinal);

    /// <summary>
    /// The words of the text one view cannot see (each with where it occurs, for the author) and of the text it can, from
    /// the render's <see cref="ReadScope"/> (class summary). Words are compared by their folded keys
    /// (<see cref="CampaignWords.Split"/>), so case, diacritics and apostrophes never tell a word apart.
    /// </summary>
    private sealed class WordSets
    {
        // The shortest hidden word a longer word is matched by its start against (Giveaways): Phase 6's distinctive length.
        private const int MinPrefix = 4;

        private readonly ReadScope _scope;
        private readonly Dictionary<string, List<Source>> _hidden = new(StringComparer.Ordinal);
        private readonly HashSet<string> _visible = new(StringComparer.Ordinal);

        // The hidden words that are words of NAMES (the prefix rule's only stems, class summary): the words of the true names
        // the view does not see, of the aliases it does not use, of cross-linked names and aliases, and the one-word forbidden
        // terms; never the words of a title (a question's, a quest's, a secret's, a rule's) or of prose.
        private readonly Dictionary<string, List<Source>> _names = new(StringComparer.Ordinal);

        // The POSSIBLE names (F3, review F2R02): words capitalised mid-sentence in prose the view cannot see — secrets,
        // summaries and bodies, fact statements, a cross-linked entity's text — with the spelling first seen. A word that
        // starts with one is printed, with a warning to the author (Giveaways' second list), never hidden.
        private readonly Dictionary<string, List<Source>> _possible = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _spelling = new(StringComparer.Ordinal);

        // The keys of the active forbidden terms that are one word (Giveaways' prefix rule: forbidden whatever is visible).
        private readonly HashSet<string> _forbidden = new(StringComparer.Ordinal);

        private WordSets(ReadScope scope)
        {
            _scope = scope;
        }

        public static WordSets For(ReadScope scope)
        {
            var words = new WordSets(scope);
            foreach (var id in scope.LoadAllEntities())
            {
                if (scope.Entity(id) is { } entity)
                {
                    words.Entity(entity);
                }
            }

            var facts = scope.Connection.Query<string>(
                "SELECT id FROM fact WHERE campaign_id = @campaignId" + (scope.AsOf is null ? " AND deleted_at IS NULL" : string.Empty) + " ORDER BY seq",
                new { campaignId = scope.Campaign.Id }).ToList();
            scope.LoadFacts(facts);
            foreach (var id in facts)
            {
                if (scope.Fact(id) is not { } fact)
                {
                    continue;
                }

                if (fact.Visible)
                {
                    words.Show(fact.Text);
                }
                else if (fact.Row.Visibility != CampaignValues.Visibilities.Public)
                {
                    words.HideProse(fact.Row.Statement, new Source(null, fact.Ref));
                }
            }

            foreach (var rule in ReadGates.ActiveVocabulary(scope, new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)))
            {
                foreach (var term in rule.Terms)
                {
                    var source = new Source(null, rule.Source);
                    words.Hide(term, source);
                    if (CampaignWords.Split(term) is [var one] && one.Key.Length >= MinPrefix)
                    {
                        words._forbidden.Add(one.Key);
                        Add(words._names, one.Key, source);
                    }
                }
            }

            words.CrossCampaign();
            return words;
        }

        /// <summary>
        /// The distinctive words of <paramref name="text"/> that give something away, and those that only might. A
        /// giveaway (the first list) occurs in hidden text and in no visible text, each once, as the text spells it, with
        /// the author refs of where it is hidden; or it STARTS with a hidden word of four letters or more of a NAME (review
        /// L02: a demonym or compound of a hidden name, "Kerasian", "Kerasborn", "Nesterling", is one word that is itself
        /// hidden nowhere, so the whole-word rule let it through to every view) or with a one-word active forbidden term
        /// ("Sealbreaker"), unless the word itself is visible: a name the view reads ("Hollowmere", a place the party knows)
        /// never fails for the hidden word it starts with. Only names start giveaways (F2, review UR01; F3, review F2R01: not
        /// a title's words either): with every word of hidden prose a stem, one DM note ("They hunt the half-blood heirs")
        /// hid Hunter, Halfling and Hunter's Mark from every party view, and an author-only quest "Slay the Red Dragon
        /// before the Hunt" did it again. A word that starts with a POSSIBLE name (the second list: a word capitalised
        /// mid-sentence in hidden prose, "Baal" of "thins Baal's seal") is printed, and the author is warned (F3, review
        /// F2R02). A plural or possessive s stripped from a word or a start never leaves a common word to match (review
        /// LR01: "longs" is "long").
        /// </summary>
        public (IReadOnlyList<(string Matched, IReadOnlyList<string> Sources)> Giveaways, IReadOnlyList<(string Matched, string Stem, IReadOnlyList<string> Sources)> Possible) Giveaways(string text)
        {
            var distinctive = CampaignWords.DistinctiveWords(text).ToHashSet(StringComparer.Ordinal);
            var found = new List<(string, IReadOnlyList<string>)>();
            var possible = new List<(string, string, IReadOnlyList<string>)>();
            if (distinctive.Count == 0)
            {
                return (found, possible);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var word in CampaignWords.Split(text))
            {
                // DistinctiveWords drops a possessive's s ("Nadar's" gives "nadar"); Split keeps it ("nadars").
                var key = distinctive.Contains(word.Key) ? word.Key
                    : word.Key.Length > 1 && word.Key.EndsWith('s') && distinctive.Contains(word.Key[..^1]) ? word.Key[..^1]
                    : null;
                if (key is null || !seen.Add(key) || Variants(key).Any(_visible.Contains))
                {
                    continue;
                }

                var sources = Variants(key).Where(v => v == key || Distinctive(v)).SelectMany(v => _hidden.GetValueOrDefault(v) ?? []).Distinct().ToList();
                if (sources.Count == 0)
                {
                    sources = HiddenStart(key);
                }

                if (sources.Count > 0)
                {
                    found.Add((text[word.Start..word.End], sources.Select(Ref).Distinct(StringComparer.Ordinal).Take(MaxSources).ToList()));
                }
                else if (PossibleStart(key) is { } start)
                {
                    possible.Add((text[word.Start..word.End], start.Stem, start.Sources.Select(Ref).Distinct(StringComparer.Ordinal).Take(MaxSources).ToList()));
                }
            }

            return (found, possible);
        }

        // Where the hidden name word a longer word starts with is hidden (Giveaways' prefix rule), or nothing: the longest
        // such start of four letters or more that is a distinctive word of a hidden NAME no visible text holds, or a one-word
        // forbidden term. The common-word test runs on each spelling actually matched, after a plural or possessive s is
        // stripped (review LR01): "Longstrider"'s start "longs" is the common "long", which gives nothing away.
        private List<Source> HiddenStart(string key)
        {
            for (var length = key.Length - 1; length >= MinPrefix; length--)
            {
                var start = key[..length];
                var forbidden = _forbidden.Contains(start);
                if (!forbidden && Variants(start).Any(_visible.Contains))
                {
                    continue;
                }

                var sources = Variants(start)
                    .Where(v => (forbidden && v == start) || Distinctive(v))
                    .SelectMany(v => _names.GetValueOrDefault(v) ?? [])
                    .Distinct()
                    .ToList();
                if (sources.Count > 0)
                {
                    return sources;
                }
            }

            return [];
        }

        // The longest POSSIBLE name the word is or starts with (four letters or more, distinctive, read by no visible text),
        // with its spelling and where it is hidden, or null. The word itself counts: a possible name only a cross-linked
        // entity's text holds is in no hidden-word set of this campaign, so it is warned about whole as well.
        private (string Stem, List<Source> Sources)? PossibleStart(string key)
        {
            for (var length = key.Length; length >= MinPrefix; length--)
            {
                var start = key[..length];
                if (Variants(start).Any(_visible.Contains))
                {
                    continue;
                }

                foreach (var v in Variants(start).Where(v => Distinctive(v) && _possible.ContainsKey(v)))
                {
                    return (_spelling[v], _possible[v]);
                }
            }

            return null;
        }

        // A word that gives something away on its own (CampaignWords.DistinctiveWords): four letters or more, not common.
        private static bool Distinctive(string key) => CampaignWords.DistinctiveWords(key).Count > 0;

        // One entity's text: its secret always hidden; its name, summary and body hidden unless the view is shown it, then
        // visible; the name it knows a disguised one by visible; each alias visible when the view uses it, hidden otherwise.
        // A hidden true name and a hidden alias are also NAMES, the prefix rule's stems (HideName); hidden prose gives
        // possible names (HideProse).
        private void Entity(EntityState entity)
        {
            var source = new Source(entity.Row.Id, null);
            HideProse(entity.Row.SecretMd, source);
            if (entity.Shown)
            {
                Show(entity.Row.Name);
                Show(entity.Row.Summary);
                Show(entity.Row.BodyMd);
            }
            else
            {
                Hide(entity.Row.Name, source);
                HideName(entity.Row.Name, entity.Row.Kind, source);
                HideProse(entity.Row.Summary, source);
                HideProse(entity.Row.BodyMd, source);
            }

            if (entity.Visible)
            {
                Show(entity.View.DisplayName);
            }

            foreach (var alias in entity.Aliases)
            {
                if (entity.View.UsedNames.Contains(CampaignText.KeyWithoutArticle(alias.Alias), StringComparer.Ordinal))
                {
                    Show(alias.Alias);
                }
                else
                {
                    Hide(alias.Alias, source);
                    HideName(alias.Alias, null, source);
                }
            }
        }

        // The names and aliases of other campaigns' entities cross-linked to this one's (the set the knowledge check's
        // cross_campaign class reads): no view of this campaign sees them. Their summaries, bodies and secrets give possible
        // names only (F3: prose is warn-only, here too), and no hidden words: the contract hides another campaign's names,
        // not its every word.
        private void CrossCampaign()
        {
            var others = _scope.Connection.Query<(string Id, string Name, string Kind, string? Summary, string? BodyMd, string? SecretMd)>(
                "SELECT DISTINCT e.id, e.name, e.kind, e.summary, e.body_md AS BodyMd, e.secret_md AS SecretMd FROM cross_link x " +
                "JOIN entity mine ON mine.id IN (x.a_id, x.b_id) AND mine.campaign_id = @campaignId " +
                "JOIN entity e ON e.id = CASE WHEN x.a_id = mine.id THEN x.b_id ELSE x.a_id END WHERE e.campaign_id <> @campaignId",
                new { campaignId = _scope.Campaign.Id }).ToList();
            foreach (var (id, name, kind, summary, body, secret) in others)
            {
                var source = new Source(id, null);
                Hide(name, source);
                HideName(name, kind, source);
                foreach (var alias in _scope.Connection.Query<string>("SELECT alias FROM entity_alias WHERE entity_id = @id", new { id }))
                {
                    Hide(alias, source);
                    HideName(alias, null, source);
                }

                foreach (var prose in new[] { summary, body, secret })
                {
                    PossibleNames(prose, source);
                }
            }
        }

        // The words of a hidden name, as prefix stems (class summary): an alias (kind null) and the name of a person, place,
        // faction, thing, legend, work or event give every word; the title of any other kind (a question, a secret, a rule, a
        // quest) gives none (F3, review F2R01: Title Case titles, "Slay the Red Dragon before the Hunt", hid Hunter and
        // Halfling and turned the ally "Huntsman Joss" into "an unknown creature" again).
        private void HideName(string? name, string? kind, Source source)
        {
            if (kind is not null && !KnowledgeCheck.ProperNameKinds.Contains(kind))
            {
                return;
            }

            foreach (var word in CampaignWords.Split(name))
            {
                Add(_names, word.Key, source);
            }
        }

        // Hidden prose (a secret, a hidden summary or body, a fact statement): every word hidden (the whole-word rule), and
        // each word capitalised mid-sentence a possible name.
        private void HideProse(string? text, Source source)
        {
            Hide(text, source);
            PossibleNames(text, source);
        }

        // The words of prose capitalised mid-sentence (not a sentence's or a line's first word: "Baal" in "every eaten fruit
        // thins Baal's seal", never "Keras" of "Keras was barely too slow"), distinctive ones only, as possible names.
        private void PossibleNames(string? text, Source source)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            foreach (var word in CampaignWords.Split(text))
            {
                if (!word.Capitalised || OpensSentence(text, word.Start))
                {
                    continue;
                }

                // A possessive's s is the possessive's ("Baal's" is "Baal"), as DistinctiveWords reads it.
                var surface = text[word.Start..word.End];
                var possessive = surface.Length > 2 && surface[^1] == 's' && surface[^2] is '\'' or '’' or 'ʼ';
                var key = possessive ? word.Key[..^1] : word.Key;
                if (key.Length < MinPrefix || !Distinctive(key))
                {
                    continue;
                }

                Add(_possible, key, source);
                _spelling.TryAdd(key, possessive ? surface[..^2] : surface);
            }
        }

        // Whether the word at start opens a sentence or a line: nothing before it but white space and openers (quotes,
        // brackets, list marks), or a full stop, question or exclamation mark, or a line break before those.
        private static bool OpensSentence(string text, int start)
        {
            for (var i = start - 1; i >= 0; i--)
            {
                var c = text[i];
                if (c is '\n' or '\r' or '.' or '!' or '?')
                {
                    return true;
                }

                if (char.IsWhiteSpace(c) || c is '"' or '\'' or '“' or '‘' or '(' or '[' or '*' or '_' or '#' or '-' or '>' or '•')
                {
                    if (c == '-' && i > 0 && !char.IsWhiteSpace(text[i - 1]) && text[i - 1] is not ('\n' or '\r'))
                    {
                        return false; // a hyphen inside a word ("Sky-World"): mid-word, so mid-sentence
                    }

                    continue;
                }

                return false;
            }

            return true;
        }

        // One more place a word is hidden (at most MaxSources, each once).
        private static void Add(Dictionary<string, List<Source>> set, string key, Source source)
        {
            if (!set.TryGetValue(key, out var sources))
            {
                sources = [];
                set.Add(key, sources);
            }

            if (sources.Count < MaxSources && !sources.Contains(source))
            {
                sources.Add(source);
            }
        }

        private void Hide(string? text, Source source)
        {
            foreach (var word in CampaignWords.Split(text))
            {
                Add(_hidden, word.Key, source);
            }
        }

        private void Show(string? text)
        {
            foreach (var word in CampaignWords.Split(text))
            {
                _visible.Add(word.Key);
            }
        }

        private string Ref(Source source) => source.Ref ?? _scope.AuthorRef(source.EntityId);

        // A key with a plural or possessive s on either side ("cage", "cages"), as the name scanner compares a last word.
        private static IEnumerable<string> Variants(string key)
        {
            yield return key;
            yield return key + "s";
            if (key.Length > 1 && key.EndsWith('s'))
            {
                yield return key[..^1];
            }
        }

        /// <summary>Where a hidden word occurs: an entity (its ref is read only when the word is found) or a printed ref.</summary>
        private readonly record struct Source(string? EntityId, string? Ref);
    }
}
