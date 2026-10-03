namespace DndMcp.Domain.Campaign;

/// <summary>
/// One candidate proper noun (<see cref="ProperNouns.Runs"/>): the run as the text first has it, and whether it opens a
/// sentence or a line everywhere the text has it. When it does, its first word's capital may be the sentence's, not a
/// name's: "Hail Belmakor", "Sing of the Crumbling Statue" and "Beware the Crumbling Statue" are a verb before a name
/// (<see cref="KnownNames"/> reads them so). Anywhere else the capital was typed on purpose ("we met Rolf of the
/// Crumbling Statue", "And Rolf …" once the stoplisted "And" is dropped).
/// </summary>
/// <param name="Surface">The run as written, a trailing possessive dropped ("Crumbling Statue" of "Crumbling Statue's").</param>
/// <param name="OpensSentence">Whether every appearance of the run opens a sentence or a line.</param>
public sealed record ProperNounRun(string Surface, bool OpensSentence);

/// <summary>
/// Picks out what looks like a proper noun in prose: the candidates for "possible inventions" (a name in a recap or a
/// lyric that matches nothing in the campaign) and for the session-end checklist.
///
/// <para>
/// <b>Heuristic, and deliberately generous:</b> a run of capitalised words, allowing a few lower-case connectors inside
/// ("Isle of Craftsmen", "Temple of the Moon", "Maria de la Cruz"). A common word ("The", "When", "Come", from a
/// stoplist) is dropped from the front of a run that opens a sentence or a line, and is never a candidate on its own
/// unless an article marks it (below), since every lyric line starts with a capital. "I" is never a candidate. Any other capitalised word is ("Rock on,
/// Silverwind" gives "Rock"): the stoplist leaves out words that are also names ("Will", "May", "Hope") and words a
/// fantasy world names places after ("the Below", "the Watch", "the Nine"). The caller matches candidates against the
/// campaign's names, aliases and known_as; a false candidate costs the author one glance, a missed one is an invented
/// name that becomes canon by accident.
/// </para>
/// <para>
/// <b>Sentence openers are common words too</b> (review U04): the adverbs and connectives a recap or a lyric opens a
/// sentence with ("Afterwards", "Meanwhile", "Worse", "Finally", "That"), and the contractions and possessives of any
/// stoplisted word ("That's", "We'll", "Didn't", "Everyone's"), whose folded key ("thats") is no stoplisted word. The
/// session checklist listed "Afterwards" as an unknown name to add or strike from the recap, and a model rewrote the
/// user's recap to clear it; the check listed "Worse" and "That" (from "That's") as possible inventions, and later
/// "Somewhere" (review UR5), which a model reported to the user as a checker bug. A participle that opens a sentence
/// ("Defeated, we went home") is still a candidate: the class is open, and a world names things after them.
/// </para>
/// <para>
/// <b>But a name stays a name.</b> Only a contraction's ending makes the word in front of an apostrophe count ('s, 'd,
/// 'll, 're, 've, 'm, n't): "Do'Urden", "An'Kahet" and "No'Ruk" are names, not "do", "an" and "no". And an article
/// marks what follows it as a name, stoplist or not: "the Within", "the One", "the First" are places, powers and
/// titles, while the same words open sentences ("Within the walls", "One more round"). A word a world might name
/// something after alone, with no article ("Will", "Hope", "Mine"), is left out of the stoplist instead.
/// </para>
/// </summary>
public static class ProperNouns
{
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal)
    {
        "of", "the", "de", "von", "van", "du", "da", "del", "della", "der", "den", "la", "le",
    };

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "i", "im", "ive", "ill", "id", "he", "she", "they", "we", "it", "its", "this", "that", "these", "those",
        "but", "and", "or", "so", "then", "when", "while", "if", "yes", "no", "not", "now", "here", "there", "his", "her",
        "their", "our", "my", "your", "what", "who", "whom", "whose", "why", "how", "where", "after", "before", "as", "at",
        "in", "on", "of", "for", "with", "from", "to", "by", "all", "some", "one", "every", "each", "once", "still", "even",
        "just", "only", "oh", "well", "you", "me", "us", "him", "them", "let", "lets", "maybe", "perhaps", "also", "again",
        "because", "since", "until", "though", "although", "nobody", "nothing", "someone", "something", "everyone",
        "everything", "hey", "ok", "okay", "do", "dont", "did", "didnt", "is", "are", "was", "were", "be", "been",
        "would", "can", "cant", "could", "should", "shall", "might", "must", "have", "has", "had", "never",
        "always", "come", "go", "get", "take", "make", "see", "look", "keep", "bring", "tell", "give", "hold", "stay",
        "old", "new", "down", "up", "out", "over", "under", "into", "back", "more", "most", "much", "many", "few", "other",
        "another", "any", "both", "such", "than", "too", "very", "yet", "which", "whatever", "wherever", "whenever",
        "sometimes", "today", "tonight", "tomorrow", "yesterday", "first", "last", "next", "please", "thank", "thanks",

        // Sentence-opening adverbs and connectives (FD5, review U04).
        "afterwards", "afterward", "meanwhile", "worse", "worst", "better", "best", "later", "earlier", "finally", "suddenly",
        "eventually", "instead", "soon", "besides", "unfortunately", "fortunately", "luckily", "unluckily", "somehow",
        "anyway", "anyhow", "thus", "hence", "therefore", "moreover", "furthermore", "nevertheless", "nonetheless",
        "otherwise", "however", "indeed", "apparently", "clearly", "obviously", "ultimately", "initially", "originally",
        "previously", "recently", "lately", "already", "almost", "nearly", "probably", "possibly", "certainly", "surely",
        "definitely", "absolutely", "actually", "really", "truly", "honestly", "frankly", "seriously", "basically",
        "essentially", "naturally", "sadly", "happily", "thankfully", "hopefully", "regrettably", "admittedly", "evidently",
        "seemingly", "presumably", "undoubtedly", "inevitably", "unexpectedly", "surprisingly", "unsurprisingly",
        "interestingly", "ironically", "oddly", "strangely", "curiously", "notably", "especially", "particularly", "mostly",
        "mainly", "simply", "merely", "quickly", "slowly", "quietly", "gradually", "immediately", "instantly", "abruptly",
        "briefly", "shortly", "often", "usually", "generally", "typically", "occasionally", "frequently", "rarely",
        "seldom", "together", "alone", "firstly", "secondly", "thirdly", "lastly", "consequently", "accordingly",
        "regardless", "likewise", "similarly", "conversely", "alternatively", "overall", "additionally", "beforehand",
        "thereafter", "whereupon", "whereas", "whether", "unless", "till", "during", "despite", "through", "throughout",
        "across", "upon", "without", "within", "like", "unlike", "along", "around", "against", "toward", "towards", "about",
        "whoever", "whichever", "either", "neither", "nor", "none", "anyone", "anybody", "anything", "somebody", "everybody",
        "yours", "ours", "theirs", "hers", "myself", "yourself", "himself", "herself", "itself", "ourselves",
        "yourselves", "themselves", "does", "doing", "done", "being", "having", "ought", "sorry", "ah", "aha", "alas", "aye",
        "nay", "yeah", "yep", "yup", "nope", "hmm", "huh", "wow", "whoa", "hello", "hi", "goodbye", "farewell", "behold",
        "listen", "wait", "remember", "imagine", "hear", "know", "say",

        // The rest of the sentence-opening adverbs (review UR5): "Somewhere a bell rang" listed "Somewhere" as a possible
        // invention, and a model told the user the checker was wrong. "Second" and "Third" join "First" ("the Second" is
        // still a name: an article marks one). Participles ("Defeated, we went home") stay candidates: an open class, and a
        // world names things after them.
        "somewhere", "nowhere", "everywhere", "anywhere", "elsewhere", "somewhat", "altogether", "second", "third",
        "carefully", "silently",

        // Contractions as folded keys, for text typed without the apostrophe ("Thats"); with one, IsCommon reads the
        // stoplisted word in front of it ("That's", "Couldn't").
        "thats", "whats", "theres", "heres", "wheres", "whos", "hows", "youre", "theyre", "youve", "theyve", "weve", "youll",
        "theyll", "itll", "isnt", "wasnt", "arent", "werent", "doesnt", "wont", "aint", "hasnt", "havent", "hadnt",
        "couldnt", "wouldnt", "shouldnt", "mustnt", "neednt", "mightnt", "shant", "yall", "tis", "twas", "oer", "neer", "eer",
    };

    // Words that are common only with their opening apostrophe ("'Cause we can", "'Til the end"): "the Cause" may be a
    // faction and "Til" a name, so the bare words stay candidates, and so does one quoted in apostrophes ("'Cause'").
    private static readonly HashSet<string> Aphetic = new(StringComparer.Ordinal) { "cause", "til", "em", "round", "neath" };

    // What follows the apostrophe of a contraction or a possessive ("That's", "You'd", "We'll", "They're", "We've", "I'm",
    // "Can't"): only then is the word in front of it the word that counts. Any other ending makes a name ("Do'Urden").
    private static readonly HashSet<string> ContractionEndings = new(StringComparer.OrdinalIgnoreCase) { "s", "d", "ll", "re", "ve", "m", "t" };

    /// <summary>The candidate proper nouns of <paramref name="text"/>, in order of first appearance, each once (by <see cref="CampaignText.Key"/>).</summary>
    public static IReadOnlyList<string> Candidates(string? text) => Runs(text).Select(run => run.Surface).ToList();

    /// <summary>
    /// The candidates of <see cref="Candidates"/>, each with whether it opens a sentence or a line wherever the text has it
    /// (<see cref="ProperNounRun"/>): once anywhere else, its first capital was typed on purpose.
    /// </summary>
    public static IReadOnlyList<ProperNounRun> Runs(string? text)
    {
        var candidates = new List<ProperNounRun>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return candidates;
        }

        var words = CampaignWords.Split(text);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var i = 0;
        while (i < words.Count)
        {
            if (!words[i].Capitalised)
            {
                i++;
                continue;
            }

            var first = i;
            var last = i;
            var k = i + 1;
            while (k < words.Count && Joins(text, words, k))
            {
                if (words[k].Capitalised)
                {
                    last = k;
                    k++;
                    continue;
                }

                var m = k;
                while (m < words.Count && Joins(text, words, m) && !words[m].Capitalised && Connectors.Contains(words[m].Key))
                {
                    m++;
                }

                if (m > k && m < words.Count && Joins(text, words, m) && words[m].Capitalised)
                {
                    last = m;
                    k = m + 1;
                    continue;
                }

                break;
            }

            // Words in front that are not part of a name: a common word opening a sentence or line ("When Keras came" →
            // "Keras"), a common word standing alone anywhere (a line start the punctuation did not show), "I" and its
            // contractions, and connectors left in front once those are gone. A word right after an article is never
            // dropped as common: "the Within", "The One" (class summary).
            var sentenceStart = IsSentenceStart(text, words, i);
            var afterArticle = i > 0 && CampaignWords.IsArticle(words[i - 1].Key) && Joins(text, words, i);
            while (first <= last)
            {
                var word = words[first];
                var pronoun = word.Key is "i" or "im" or "ive" or "ill" or "id";
                var common = !afterArticle && IsCommon(text, word) && (first == last || (first == i && sentenceStart));
                if (pronoun || !word.Capitalised || common)
                {
                    afterArticle = CampaignWords.IsArticle(word.Key);
                    first++;
                    continue;
                }

                break;
            }

            if (first <= last)
            {
                var surface = StripPossessive(text[words[first].Start..words[last].End]);
                var key = CampaignText.Key(surface);
                // It opens the sentence only when nothing was dropped from its front: "And Rolf" without "And" is a typed capital.
                var opens = first == i && sentenceStart;
                if (key.Length > 0 && seen.TryGetValue(key, out var at))
                {
                    candidates[at] = candidates[at] with { OpensSentence = candidates[at].OpensSentence && opens };
                }
                else if (key.Length > 0)
                {
                    seen.Add(key, candidates.Count);
                    candidates.Add(new ProperNounRun(surface, opens));
                }
            }

            i = last + 1;
        }

        return candidates;
    }

    /// <summary>Whether <paramref name="key"/> (a folded word) is a lower-case word a name may hold inside ("of", "the", "de").</summary>
    internal static bool IsConnector(string key) => Connectors.Contains(key);

    /// <summary>
    /// Whether a word of <paramref name="text"/> is a stoplisted common word, or a contraction or possessive of one: it
    /// ends in a contraction's ending ('s, 'd, 'll, 're, 've, 'm, 't) after a stoplisted word ("That's", "We'll",
    /// "Everyone's"), or in "n't" after one ("Couldn't", "Oughtn't"), or it is a clipped word after an apostrophe
    /// ("'Cause", "'Til") that is not quoted in apostrophes ("'Cause'" may be a name). A name with an apostrophe is not:
    /// "O'Brien" and "Keras's" have no stoplisted word in front, "Do'Urden" and "An'Kahet" no contraction ending.
    /// </summary>
    internal static bool IsCommon(string text, TextWord word)
    {
        if (Common.Contains(word.Key) ||
            (Aphetic.Contains(word.Key) && IsApostropheAt(text, word.Start - 1) && !IsApostropheAt(text, word.End)))
        {
            return true;
        }

        var surface = text[word.Start..word.End];
        var apostrophe = surface.IndexOfAny(CampaignWords.Apostrophes);
        if (apostrophe <= 0)
        {
            return false;
        }

        var head = CampaignText.Key(surface[..apostrophe]);
        var ending = surface[(apostrophe + 1)..];
        if (!ContractionEndings.Contains(ending))
        {
            return false;
        }

        var negation = head.Length > 1 && head[^1] == 'n' && ending is "t" or "T";
        return Common.Contains(head) || (negation && Common.Contains(head[..^1]));
    }

    private static bool IsApostropheAt(string text, int index) =>
        index >= 0 && index < text.Length && CampaignWords.Apostrophes.Contains(text[index]);

    /// <summary>Whether word <paramref name="k"/> continues a run from the word before it: only spaces (or an initial's full stop) between them.</summary>
    private static bool Joins(string text, IReadOnlyList<TextWord> words, int k)
    {
        var gap = text.AsSpan(words[k - 1].End, words[k].Start - words[k - 1].End);
        if (gap.Length == 0)
        {
            return true;
        }

        // Initials: "G.O.D.S. Co." and "J. R. Smith" run on through a full stop after a single capital letter.
        var initial = words[k - 1].End - words[k - 1].Start == 1 && words[k - 1].Capitalised;
        foreach (var c in gap)
        {
            if (c == '.' && initial)
            {
                continue;
            }

            if (c is not (' ' or '\t' or '-' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first word of the text, or the first after a sentence end, a line break or an opening quote.</summary>
    private static bool IsSentenceStart(string text, IReadOnlyList<TextWord> words, int k)
    {
        var from = k == 0 ? 0 : words[k - 1].End;
        var gap = text.AsSpan(from, words[k].Start - from);
        if (k == 0)
        {
            return true;
        }

        var initial = words[k - 1].End - words[k - 1].Start == 1 && words[k - 1].Capitalised;
        foreach (var c in gap)
        {
            if (c is '!' or '?' or '\n' or '\r' or '…' or '"' or '“' or '«' or '(' or '[' or '*' or '#' or '>' or '/' or '|' || (c == '.' && !initial))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripPossessive(string surface)
    {
        foreach (var suffix in (ReadOnlySpan<string>)["'s", "’s", "ʼs"])
        {
            if (surface.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && surface.Length > suffix.Length)
            {
                return surface[..^suffix.Length];
            }
        }

        return surface;
    }
}
