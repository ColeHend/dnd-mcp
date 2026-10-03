using System.Text.RegularExpressions;

namespace DndMcp.Domain.Campaign;

/// <summary>
/// One active vocabulary rule: words no non-author text may use while it holds, from a fact's gate or a
/// <c>reveal_rule</c> entity.
/// </summary>
/// <param name="Source">What the rule came from, as a handle for the report (<c>f:12</c>, <c>rule:old-king-no-name</c>).</param>
/// <param name="Terms">Forbidden words or word sequences ("seal", "Axiom Cage").</param>
/// <param name="Patterns">Forbidden word patterns with <c>&lt;number&gt;</c> ("&lt;number&gt;-year-old").</param>
/// <param name="PreferredTerms">What to say instead ("shell", "what keeps it in").</param>
/// <param name="Note">Why, in the author's words.</param>
public sealed record ForbiddenRule(
    string Source,
    IReadOnlyList<string> Terms,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> PreferredTerms,
    string? Note);

/// <summary>A forbidden term or pattern found in a text.</summary>
/// <param name="Rule">The rule it breaks.</param>
/// <param name="TermOrPattern">The term or pattern as the rule spells it.</param>
/// <param name="Surface">The words as written in the text ("Sealed", "nine-hundred-year-old").</param>
/// <param name="Start">Index of the first character in the text.</param>
/// <param name="Length">Characters from <paramref name="Start"/> to the end of the last word.</param>
public sealed record ForbiddenHit(ForbiddenRule Rule, string TermOrPattern, string Surface, int Start, int Length);

/// <summary>
/// Finds forbidden vocabulary in text (contract §3.4): the One Piece "no one calls it a seal until the axe is assembled"
/// rule and the Belmakor "no name, no timespan for the old king" rule.
///
/// <para>
/// <b>Matching.</b> Case, diacritics and apostrophes are ignored; terms match whole words only, a multi-word term as a
/// word sequence; the last word takes English inflections: -s, -ed, -ing, -er, -ers always, -es after s/x/z/ch/sh/o,
/// -d after a final e, a final e dropped before -ing/-ed/-er (bake → baking), a final consonant-y as -ies/-ied/-ier, and
/// a doubled final consonant after a short vowel (stab → stabbed). So "seal" matches seal, seals, Sealed and SEALING
/// but not sealskin or unsealed. The -d and -es restrictions are deliberate: an unrestricted "-d" would make "win" match
/// "wind" and "ban" match "band", and "-es" would make "tim" match "times".
/// </para>
/// <para>
/// <b>Patterns.</b> <c>&lt;number&gt;</c> (or <c>&lt;n&gt;</c>) matches digits (with thousands commas) or English
/// number words, hyphenated or spaced: "900", "1,000", "nine-hundred", "four hundred and twenty", "a thousand". It is
/// how "no specific timespan" is checked without listing every number.
/// </para>
/// <para>
/// <b>Overlaps.</b> At each position the longest match wins and the scan resumes after it, so "the seal of Baal" is one
/// hit for a rule forbidding that phrase, not also a hit for "seal". When two rules match the same longest words both are
/// reported, because each has its own preferred terms and lift condition.
/// </para>
/// <para>
/// <b>Heuristic by design:</b> the scan retrieves and the model judges. "Not seal it" (said of Baal, not the white
/// lines) is still a hit: there is no referent analysis, and a false positive costs one look where a false negative is a
/// leaked secret.
/// </para>
/// </summary>
public static partial class ForbiddenVocabulary
{
    /// <summary>The number placeholder of a pattern.</summary>
    public const string NumberPlaceholder = "<number>";

    private static readonly HashSet<string> Units = new(StringComparer.Ordinal)
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen",
        "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty", "thirty", "forty", "fifty", "sixty",
        "seventy", "eighty", "ninety",
    };

    private static readonly HashSet<string> Scales = new(StringComparer.Ordinal) { "hundred", "thousand", "million", "billion", "dozen" };

    /// <summary>Every forbidden term and pattern of <paramref name="rules"/> found in <paramref name="text"/>, in text order.</summary>
    /// <remarks>
    /// A term or pattern that cannot be used (blank, or a malformed pattern) is skipped rather than thrown: the rules come
    /// from stored data validated when written, and a read must never fail on one bad rule.
    /// </remarks>
    public static IReadOnlyList<ForbiddenHit> Scan(string? text, IReadOnlyList<ForbiddenRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var hits = new List<ForbiddenHit>();
        if (string.IsNullOrEmpty(text) || rules.Count == 0)
        {
            return hits;
        }

        var compiled = rules.Select(r => (Rule: r, Items: Compile(r))).Where(r => r.Items.Count > 0).ToList();
        var words = CampaignWords.Split(text);
        var i = 0;
        while (i < words.Count)
        {
            var best = 0;
            var winners = new List<(ForbiddenRule Rule, string Item)>();
            foreach (var (rule, items) in compiled)
            {
                var ruleBest = 0;
                string? ruleItem = null;
                foreach (var item in items)
                {
                    var length = MatchLength(words, i, item.Elements, 0);
                    if (length > ruleBest)
                    {
                        ruleBest = length;
                        ruleItem = item.Original;
                    }
                }

                if (ruleBest > best)
                {
                    best = ruleBest;
                    winners.Clear();
                }

                if (ruleBest > 0 && ruleBest == best)
                {
                    winners.Add((rule, ruleItem!));
                }
            }

            if (best == 0)
            {
                i++;
                continue;
            }

            var start = words[i].Start;
            var end = words[i + best - 1].End;
            foreach (var (rule, item) in winners)
            {
                hits.Add(new ForbiddenHit(rule, item, text[start..end], start, end - start));
            }

            i += best;
        }

        return hits;
    }

    /// <summary>Whether <paramref name="term"/> can be a forbidden or preferred term; <paramref name="problem"/> says why not.</summary>
    public static bool IsUsableTerm(string? term, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(term))
        {
            problem = "is blank; give a word such as \"seal\"";
            return false;
        }

        if (term.Length > CampaignLimits.MaxTermLength)
        {
            problem = $"is longer than {CampaignLimits.MaxTermLength} characters";
            return false;
        }

        if (term.Contains('<') || term.Contains('>'))
        {
            problem = $"contains < or >, but a term has no placeholders; put patterns such as \"{NumberPlaceholder}-year-old\" in forbidden_patterns";
            return false;
        }

        if (CampaignWords.Split(term).Count == 0)
        {
            problem = "has no letters or digits";
            return false;
        }

        return true;
    }

    /// <summary>Whether <paramref name="pattern"/> is a usable forbidden pattern; <paramref name="problem"/> says why not.</summary>
    public static bool IsUsablePattern(string? pattern, out string problem)
    {
        problem = string.Empty;
        if (string.IsNullOrWhiteSpace(pattern))
        {
            problem = $"is blank; give a pattern such as \"{NumberPlaceholder} years\"";
            return false;
        }

        if (pattern.Length > CampaignLimits.MaxTermLength)
        {
            problem = $"is longer than {CampaignLimits.MaxTermLength} characters";
            return false;
        }

        return TryParsePattern(pattern, out _, out problem);
    }

    /// <summary>
    /// Whether <paramref name="stem"/> (a folded word) inflects to <paramref name="word"/> under the rules in the class
    /// summary. Shared with the name scanner's plural and possessive rule, which is the -s case alone.
    /// </summary>
    internal static bool InflectsTo(string stem, string word)
    {
        if (word == stem)
        {
            return true;
        }

        if (stem.Length == 0 || word.Length <= stem.Length)
        {
            return false;
        }

        if (word.StartsWith(stem, StringComparison.Ordinal))
        {
            var suffix = word[stem.Length..];
            switch (suffix)
            {
                case "s" or "ed" or "ing" or "er" or "ers":
                    return true;
                case "es":
                    return stem.EndsWith('s') || stem.EndsWith('x') || stem.EndsWith('z') || stem.EndsWith("ch", StringComparison.Ordinal) ||
                           stem.EndsWith("sh", StringComparison.Ordinal) || stem.EndsWith('o');
                case "d":
                    return stem.EndsWith('e');
            }

            // A doubled final consonant after a short vowel: stab → stabbed, stabbing.
            if (IsDoublingStem(stem) && suffix.Length > 1 && suffix[0] == stem[^1] && suffix[1..] is "ed" or "ing" or "er" or "ers")
            {
                return true;
            }
        }

        // A final e dropped: bake → baking, baked, baker.
        if (stem.Length > 2 && stem.EndsWith('e'))
        {
            var root = stem[..^1];
            if (word.StartsWith(root, StringComparison.Ordinal) && word[root.Length..] is "ing" or "ed" or "er" or "ers")
            {
                return true;
            }
        }

        // A final consonant-y as -i-: spy → spies, spied.
        if (stem.Length > 1 && stem.EndsWith('y') && !IsVowel(stem[^2]))
        {
            var root = stem[..^1] + "i";
            if (word.StartsWith(root, StringComparison.Ordinal) && word[root.Length..] is "es" or "ed" or "er" or "ers")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The lengths (in words) of every number that starts at <paramref name="at"/>, longest first: "four hundred years"
    /// gives 2 ("four hundred") and 1 ("four").
    /// </summary>
    internal static IReadOnlyList<int> NumberLengths(IReadOnlyList<TextWord> words, int at)
    {
        var lengths = new List<int>();
        if (at >= words.Count)
        {
            return lengths;
        }

        var i = at;
        var first = words[i].Key;
        if (first.All(char.IsAsciiDigit))
        {
            lengths.Add(1);
            i++;
        }
        else if (first is "a" or "an")
        {
            if (i + 1 >= words.Count || !Scales.Contains(words[i + 1].Key))
            {
                return lengths;
            }

            lengths.Add(2);
            i += 2;
        }
        else if (Units.Contains(first) || Scales.Contains(first))
        {
            lengths.Add(1);
            i++;
        }
        else
        {
            return lengths;
        }

        while (i < words.Count)
        {
            var key = words[i].Key;
            if (Units.Contains(key) || Scales.Contains(key))
            {
                i++;
                lengths.Add(i - at);
            }
            else if (key == "and" && i + 1 < words.Count && (Units.Contains(words[i + 1].Key) || Scales.Contains(words[i + 1].Key)))
            {
                i += 2;
                lengths.Add(i - at);
            }
            else
            {
                break;
            }
        }

        lengths.Reverse();
        return lengths;
    }

    private static List<(string Original, Element[] Elements)> Compile(ForbiddenRule rule)
    {
        var items = new List<(string, Element[])>();
        foreach (var term in rule.Terms ?? [])
        {
            if (IsUsableTerm(term, out _))
            {
                items.Add((term, CampaignWords.Split(term).Select(w => Element.Word(w.Key)).ToArray()));
            }
        }

        foreach (var pattern in rule.Patterns ?? [])
        {
            if (pattern is not null && TryParsePattern(pattern, out var elements, out _))
            {
                items.Add((pattern, elements));
            }
        }

        return items;
    }

    /// <summary>The number of words matched from <paramref name="at"/>, or 0; the longest alternative wins.</summary>
    private static int MatchLength(IReadOnlyList<TextWord> words, int at, Element[] elements, int k)
    {
        if (k == elements.Length)
        {
            return 0;
        }

        if (at >= words.Count)
        {
            return -1;
        }

        var element = elements[k];
        var last = k == elements.Length - 1;
        if (!element.IsNumber)
        {
            var matches = last ? InflectsTo(element.Key, words[at].Key) : words[at].Key == element.Key;
            if (!matches)
            {
                return k == 0 ? 0 : -1;
            }

            if (last)
            {
                return 1;
            }

            var rest = MatchLength(words, at + 1, elements, k + 1);
            return rest < 0 ? (k == 0 ? 0 : -1) : 1 + rest;
        }

        foreach (var length in NumberLengths(words, at))
        {
            if (last)
            {
                return length;
            }

            var rest = MatchLength(words, at + length, elements, k + 1);
            if (rest >= 0)
            {
                return length + rest;
            }
        }

        return k == 0 ? 0 : -1;
    }

    private static bool TryParsePattern(string pattern, out Element[] elements, out string problem)
    {
        elements = [];
        problem = string.Empty;
        var result = new List<Element>();
        var position = 0;
        foreach (Match placeholder in Placeholder().Matches(pattern))
        {
            result.AddRange(CampaignWords.Split(pattern[position..placeholder.Index]).Select(w => Element.Word(w.Key)));
            var name = placeholder.Groups["name"].Value.Trim().ToLowerInvariant();
            if (name is not ("number" or "n"))
            {
                problem = $"has \"{placeholder.Value}\", which is not a placeholder; the one placeholder is {NumberPlaceholder}";
                return false;
            }

            result.Add(Element.Number);
            position = placeholder.Index + placeholder.Length;
        }

        var tail = pattern[position..];
        var outsidePlaceholders = Placeholder().Replace(pattern, " ");
        if (outsidePlaceholders.Contains('<') || outsidePlaceholders.Contains('>'))
        {
            problem = $"has an unclosed \"<\" or a stray \">\"; the one placeholder is {NumberPlaceholder}";
            return false;
        }

        result.AddRange(CampaignWords.Split(tail).Select(w => Element.Word(w.Key)));
        if (!result.Any(e => !e.IsNumber))
        {
            problem = $"needs at least one word beside {NumberPlaceholder}, e.g. \"{NumberPlaceholder} years\"";
            return false;
        }

        if (result.Count > CampaignLimits.MaxPatternElements)
        {
            problem = $"has more than {CampaignLimits.MaxPatternElements} words and placeholders";
            return false;
        }

        elements = result.ToArray();
        return true;
    }

    private static bool IsDoublingStem(string stem) =>
        stem.Length >= 3 && !IsVowel(stem[^1]) && stem[^1] is not ('w' or 'x' or 'y') && IsVowel(stem[^2]) && !IsVowel(stem[^3]);

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    [GeneratedRegex("<(?<name>[^<>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private readonly record struct Element(string Key, bool IsNumber)
    {
        public static Element Number => new(string.Empty, true);

        public static Element Word(string key) => new(key, false);
    }
}
