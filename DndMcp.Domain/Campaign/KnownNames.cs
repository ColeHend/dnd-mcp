namespace DndMcp.Domain.Campaign;

/// <summary>
/// The names a text is read against when its proper-noun candidates (<see cref="ProperNouns.Runs"/>) are sorted into names
/// already recorded and names that match nothing: the one rule the session-end checklist ("Names that match no entry") and
/// the knowledge check's list of capitalised words that match no name share (review UR01).
///
/// <para>
/// <b>A candidate is known</b> when it is a known name (a leading article optional on either side), or a whole-word run
/// inside one ("Silverwind" of "Belmakor Silverwind", "King" of "the old king"), or when it holds known names (the name
/// scanner finds them inside it) and nothing else but connectors ("of", "the"), titles and stoplisted words ("Captain
/// Varro", "Old Belmakor") and, when the run opens a sentence or a line, its first word. The candidate finder is generous on purpose (a missed name is an invention made canon by
/// accident), so it joins a line's opening word to the name after it: "Hail Belmakor", "Sing of the Crumbling Statue",
/// "Beware the Crumbling Statue". Judged by equality alone, each of those "matches nothing" while the same output names
/// the name it holds; the check listed them as possible inventions to strike while the checklist of the same text listed
/// none, and a model reported the check as a bug (or would strike the PC's own name from a song).
/// </para>
/// <para>
/// <b>But a new name built on a known one stays unknown.</b> Any other word beside a known name is one the text typed
/// with a capital on purpose: "Belmakor Shadowfang" (a new character), "Old King Zanzibar", "we met Rolf of the Crumbling
/// Statue". Read as "holds a known name, so known", the check stopped listing them once it shared the checklist's rule
/// (UR01's recheck), and neither listed a name about to become canon by accident.
/// </para>
/// <para>
/// <b>What it gives up:</b> a new one-word name that opens a sentence in front of a known one ("Rolf of the Crumbling
/// Statue sang.") reads as known: nothing tells it from "Sing of the Crumbling Statue" without a dictionary of verbs. The
/// other way, a verb capitalised inside a sentence ("we cried Hail Belmakor") is listed: a false flag costs a glance.
/// </para>
/// </summary>
public sealed class KnownNames
{
    // Titles a text puts in front of a name it already has ("Captain Varro", "Lord Belmakor"): capitalised, but no new name.
    // Listed, a title before a known name was the commonest false flag, and a model rewrites a recap to clear a flag.
    private static readonly HashSet<string> Titles = new(StringComparer.Ordinal)
    {
        "captain", "lieutenant", "commander", "admiral", "general", "colonel", "sergeant", "marshal", "chief", "lord", "lady",
        "sir", "dame", "king", "queen", "prince", "princess", "duke", "duchess", "baron", "baroness", "count", "countess",
        "earl", "emperor", "empress", "master", "mistress", "father", "mother", "brother", "sister", "saint", "doctor",
        "professor", "uncle", "aunt", "elder", "mister", "mr", "mrs", "ms", "dr", "st",
    };

    private readonly HashSet<string> _keys;
    private readonly NameScanner _scanner;

    /// <summary>Indexes <paramref name="names"/> (names, aliases, known_as: as stored or as keys; blanks are ignored).</summary>
    public KnownNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _keys = names.Select(CampaignText.KeyWithoutArticle).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        _scanner = new NameScanner(_keys.Select(k => new NameEntry(k, k)));
    }

    /// <summary>The candidates of <paramref name="text"/> that match no name (class summary), in order of first appearance.</summary>
    public IReadOnlyList<string> Unknown(string? text) =>
        ProperNouns.Runs(text).Where(run => !Covers(run)).Select(run => run.Surface).ToList();

    /// <summary>
    /// Whether <paramref name="candidate"/> is a known name, a whole-word run inside one, or known names with nothing beside
    /// them but connectors, titles, stoplisted words and a sentence's opening word (class summary). A candidate with no
    /// letters or digits names nothing, and is known.
    /// </summary>
    public bool Covers(ProperNounRun candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var key = CampaignText.KeyWithoutArticle(candidate.Surface);
        if (key.Length == 0 || _keys.Contains(key) || _keys.Any(name => (" " + name + " ").Contains(" " + key + " ", StringComparison.Ordinal)))
        {
            return true;
        }

        var hits = _scanner.Scan(candidate.Surface);
        if (hits.Count == 0)
        {
            return false;
        }

        // What is left once the known names, connectors, titles and stoplisted words are taken out: nothing, or the opening
        // word of a run that opens a sentence ("Hail" of "Hail Belmakor"), is a known name in a sentence; any other word is
        // a new name.
        var words = CampaignWords.Split(candidate.Surface);
        var left = words.Where(w => !hits.Any(h => w.Start >= h.Start && w.End <= h.Start + h.Length) &&
                                    !ProperNouns.IsConnector(w.Key) && !CampaignWords.IsArticle(w.Key) && !Titles.Contains(w.Key) &&
                                    !ProperNouns.IsCommon(candidate.Surface, w))
            .ToList();
        return left.Count == 0 || (left.Count == 1 && candidate.OpensSentence && left[0].Start == words[0].Start);
    }
}
