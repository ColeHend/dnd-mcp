using System.Globalization;
using System.Text;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Formatting.Srd;

/// <summary>
/// Choices, option lists and the small shared pieces the class and origin formatters build from: the typed Choice
/// trees upstream uses for proficiency, equipment, language and feature options.
///
/// <para>
/// A choice with a <c>desc</c> is shown as that sentence: it is the SRD's own wording and carries qualifiers the tree
/// drops ("(if proficient)", "Musical Instrument of your choice"). When the sentence does not name its options
/// ("Choose any three", "Choose a size when you select this species."), the options follow it, so the reader always
/// learns what can be chosen. A choice without a <c>desc</c> is walked: "Choose 2 from: …", nested choices and bundles
/// included. A choice of "anything in an equipment category" is always "any one of Holy Symbols (<c>ref</c>)": its desc
/// ("holy symbol") says less than the linked category, which lists the items.
/// </para>
/// <para>
/// Options that are proficiencies, skills, languages or ability scores are named without refs: their records add
/// nothing to the name, and eighteen linked skills would triple the line. Options that are equipment, spells, features
/// or traits carry their refs, because what those do is the reader's next question.
/// </para>
/// </summary>
internal static class SrdChoiceMarkdown
{
    private static readonly HashSet<string> NameOnlyKinds =
        new([SrdKinds.Proficiency, SrdKinds.Skill, SrdKinds.Language, SrdKinds.AbilityScore], StringComparer.Ordinal);

    private static readonly HashSet<string> LowerCaseWords =
        new(["a", "an", "and", "at", "by", "for", "from", "in", "of", "on", "or", "the", "to"], StringComparer.Ordinal);

    /// <summary>One choice as one line, or null when <paramref name="choice"/> is not a choice object.</summary>
    public static string? Describe(JsonElement choice)
    {
        if (choice.ValueKind != JsonValueKind.Object || choice.Obj("from") is not { } from)
        {
            return null;
        }

        var desc = choice.Str("desc")?.Trim();
        var count = choice.Int("choose") ?? 1;
        switch (from.Str("option_set_type"))
        {
            case "equipment_category":
                var category = from.Obj("equipment_category") is { } c ? SrdMarkdownText.Link(c) : "?";
                return count == 1 ? $"any one of {category}" : $"any {count.ToString(CultureInfo.InvariantCulture)} of {category}";
            case "resource_list":
                return $"any {count.ToString(CultureInfo.InvariantCulture)} {choice.Str("type") ?? "options"}";
        }

        var options = OptionsOf(choice);
        if (!string.IsNullOrWhiteSpace(desc) && (options.Count == 0 || NamesEveryOption(desc, options)))
        {
            return desc;
        }

        var list = OptionList(options);
        return string.IsNullOrWhiteSpace(desc)
            ? $"Choose {count.ToString(CultureInfo.InvariantCulture)} from: {list}"
            : $"{Lead(desc, count)}: {list}";
    }

    // Some descs are bare noun phrases ("skill", "one enemy type"); they become "Choose 1 skill", "Choose one enemy
    // type", so a line never reads "skill: Skill: Acrobatics, …". Sentences that already choose are left alone.
    private static string Lead(string desc, long count)
    {
        var lead = desc.TrimEnd('.', ':', ' ');
        var folded = Fold(lead);
        if (folded.Contains("choose", StringComparison.Ordinal) || folded.Contains("choice", StringComparison.Ordinal))
        {
            return lead;
        }

        var firstWord = folded.Split(' ', 2)[0];
        return firstWord is "a" or "an" or "any" or "one" or "two" or "three" or "four" || char.IsDigit(firstWord.FirstOrDefault())
            ? $"Choose {lead}"
            : $"Choose {count.ToString(CultureInfo.InvariantCulture)} {lead}";
    }

    /// <summary>The options of an <c>options_array</c> choice; empty for any other shape.</summary>
    public static IReadOnlyList<JsonElement> OptionsOf(JsonElement choice) =>
        choice.Obj("from") is { } from ? from.Arr("options") : [];

    // Bundles and nested choices contain commas themselves, so a list holding one is lettered: "(a) …; (b) …".
    private static string OptionList(IReadOnlyList<JsonElement> options)
    {
        var texts = options.Select(Option).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        var lettered = options.Any(o => o.Str("option_type") is "multiple" or "choice");
        return lettered
            ? string.Join("; ", texts.Select((t, i) => $"({(char)('a' + i)}) {t}"))
            : string.Join(", ", texts);
    }

    /// <summary>One option of a choice as text, or null for an option shape this does not know.</summary>
    public static string? Option(JsonElement option)
    {
        if (option.ValueKind == JsonValueKind.String)
        {
            return option.GetString();
        }

        switch (option.Str("option_type"))
        {
            case "reference":
                return option.Obj("item") is { } item ? OptionReference(item) : null;
            case "counted_reference":
                if (option.Obj("of") is not { } of)
                {
                    return null;
                }

                var counted = option.Int("count") is { } n && n != 1
                    ? $"{n.ToString(CultureInfo.InvariantCulture)} × {OptionReference(of)}"
                    : OptionReference(of);
                var needs = option.Arr("prerequisites").Select(p => p.Obj("proficiency")).OfType<JsonElement>()
                    .Select(p => p.Str("name")).Where(p => p is not null).ToList();
                return needs.Count > 0 ? $"{counted} (if proficient with {string.Join(", ", needs)})" : counted;
            case "multiple":
                // A nested choice inside a bundle is bracketed, or its own comma list would swallow the bundle's next
                // item: "[Choose 1 from: Skill: Acrobatics, …], Thieves' Tools" (2014 rogue Expertise).
                return string.Join(", ", option.Arr("items")
                    .Select(item => item.Str("option_type") == "choice" && Option(item) is { } nested ? $"[{nested}]" : Option(item))
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
            case "choice":
                return option.Obj("choice") is { } nested ? Describe(nested) : null;
            case "money":
                return option.Int("count") is { } amount
                    ? $"{amount.ToString(CultureInfo.InvariantCulture)} {option.Str("unit") ?? string.Empty}".TrimEnd()
                    : null;
            case "score_prerequisite":
                return ScorePrerequisite(option);
            case "ability_bonus":
                return option.Obj("ability_score") is { } ability && option.Int("bonus") is { } bonus
                    ? $"{AbilityName(ability)} {SrdMarkdownText.Signed(bonus)}"
                    : null;
            case "string":
                return option.Str("string");
            case "ideal":
                var alignments = SrdMarkdownText.NameList(option.Arr("alignments"));
                return alignments.Length > 0 ? $"{option.Str("desc")} ({alignments})" : option.Str("desc");
            case "size":
                return option.Str("size");
            default:
                return option.Str("desc") ?? option.Str("name");
        }
    }

    /// <summary>"STR 13+" from <c>{ability_score, minimum_score}</c>; null when either is missing.</summary>
    public static string? ScorePrerequisite(JsonElement prerequisite) =>
        prerequisite.Obj("ability_score") is { } ability && prerequisite.Int("minimum_score") is { } minimum
            ? $"{AbilityName(ability)} {minimum.ToString(CultureInfo.InvariantCulture)}+"
            : null;

    private static string AbilityName(JsonElement ability) => ability.Str("name") ?? ability.Str("index") ?? "?";

    private static string OptionReference(JsonElement reference)
    {
        var kind = SrdRef.FromApiUrl(reference.Str("url"))?.Kind;
        return NamedLink(reference, withRef: kind is not null && !NameOnlyKinds.Contains(kind));
    }

    private static string NamedLink(JsonElement reference, bool withRef)
    {
        var name = reference.Str("name") ?? reference.Str("index") ?? "?";
        if (reference.Str("note") is { } note && !string.IsNullOrWhiteSpace(note))
        {
            name += $" ({note.Trim()})";
        }

        return withRef && SrdRef.FromApiUrl(reference.Str("url")) is { } target ? $"{name} (`{target}`)" : name;
    }

    /// <summary>
    /// True when <paramref name="desc"/> names every option. Reference, size and string options must appear by name
    /// ("Skill: Acrobatics" by "Acrobatics"); a nested choice by its own desc. Bundles, money and score options are
    /// taken as named: equipment and prerequisite sentences always spell them out, in words the tree cannot match
    /// ("20 arrows" for 20 × Arrow).
    /// </summary>
    private static bool NamesEveryOption(string desc, IReadOnlyList<JsonElement> options)
    {
        var haystack = Fold(desc);
        return options.All(option =>
        {
            var name = option.ValueKind == JsonValueKind.String
                ? option.GetString()
                : option.Str("option_type") switch
                {
                    "reference" => option.Obj("item")?.Str("name"),
                    "size" => option.Str("size"),
                    "string" => option.Str("string"),
                    "choice" => option.Obj("choice")?.Str("desc") ?? string.Empty,
                    _ => null,
                };

            if (name is null)
            {
                return true;
            }

            var shortName = name.LastIndexOf(": ", StringComparison.Ordinal) is var colon and >= 0 ? name[(colon + 2)..] : name;
            return shortName.Length > 0 && haystack.Contains(Fold(shortName), StringComparison.Ordinal);
        });
    }

    private static string Fold(string text) => text.Replace('’', '\'').ToLowerInvariant();

    /// <summary>
    /// The options of a choice when every one is a reference to a record of <paramref name="kind"/> (feature or trait
    /// sub-options); null otherwise.
    /// </summary>
    public static IReadOnlyList<JsonElement>? RecordOptions(JsonElement choice, string kind)
    {
        var options = OptionsOf(choice);
        var items = options.Select(o => o.Str("option_type") == "reference" ? o.Obj("item") : null).ToList();
        return items.Count > 0 && items.All(i => i is { } item && SrdRef.FromApiUrl(item.Str("url"))?.Kind == kind)
            ? items.Select(i => i!.Value).ToList()
            : null;
    }

    /// <summary>
    /// "**Label** (choose N)" and one bullet per option: its link, an optional qualifier sentence (a prerequisite, a
    /// trait's damage type), then its own text. An option whose text repeats <paramref name="parentParagraphs"/> word
    /// for word is listed without it: upstream copies the parent's text into every 2014 Dragon Ancestor and Draconic
    /// Ancestry option, and ten copies would say nothing new.
    /// </summary>
    public static string? OptionBullets(string label, long? choose, IReadOnlyList<JsonElement> references, ISrdLookup lookup,
        IReadOnlyList<string> parentParagraphs, Func<SrdDocument, string?> qualifier)
    {
        if (references.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        builder.Append("**").Append(label).Append("**");
        if (choose is { } n)
        {
            builder.Append(" (choose ").Append(n.ToString(CultureInfo.InvariantCulture)).Append(')');
        }

        foreach (var reference in references)
        {
            var option = Resolve(reference.Str("url"), lookup);
            var paragraphs = option?.Root.Description() ?? [];
            if (paragraphs.SequenceEqual(parentParagraphs))
            {
                paragraphs = [];
            }

            var lead = new List<string>();
            if (option is not null && qualifier(option) is { } q && !string.IsNullOrWhiteSpace(q))
            {
                lead.Add($"*{q.Trim()}*");
            }

            if (paragraphs.Count > 0)
            {
                lead.Add(paragraphs[0].Trim());
            }

            builder.Append("\n- ").Append(SrdMarkdownText.Link(reference));
            if (lead.Count > 0)
            {
                builder.Append(": ").Append(string.Join(" ", lead));
            }

            // Later paragraphs stay inside the bullet: a blank line, then indented, as markdown continues a list item.
            foreach (var paragraph in paragraphs.Skip(1).Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                builder.Append("\n\n  ").Append(paragraph.Trim().Replace("\n", "\n  ", StringComparison.Ordinal));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// "**Label** item" for one item, "**Label**" and a bullet per item for several, null for none. Choice lines are
    /// long and contain commas, so several of them do not share one line.
    /// </summary>
    public static string? Bullets(string label, IEnumerable<string?> items)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i!.Trim()).ToList();
        return list.Count switch
        {
            0 => null,
            1 => $"**{label}** {list[0]}",
            _ => $"**{label}**\n" + string.Join("\n", list.Select(i => "- " + i)),
        };
    }

    /// <summary>Fixed equipment <c>[{equipment, quantity}]</c>: "Explorer's Pack (<c>ref</c>), 4 × Javelin (<c>ref</c>)".</summary>
    public static string EquipmentList(IEnumerable<JsonElement> items) =>
        string.Join(", ", items
            .Where(i => i.Obj("equipment") is not null)
            .Select(i => i.Int("quantity") is { } q && q != 1
                ? $"{q.ToString(CultureInfo.InvariantCulture)} × {SrdMarkdownText.Link(i.Obj("equipment")!.Value)}"
                : SrdMarkdownText.Link(i.Obj("equipment")!.Value)));

    /// <summary>
    /// "*Corrected from the upstream data: …*", one line per curated correction of <paramref name="record"/>
    /// (<see cref="SrdDocument.Corrections"/>); null when there is none. For a body that inlines another record's text (a
    /// subclass's features, a race's traits): the record's own page says so in its meta line, and the page that borrows
    /// the text must too, or corrected text passes as the untouched upstream record. Same words as the meta line.
    /// </summary>
    public static string? CorrectionNotes(SrdDocument record) =>
        record.Corrections.Count == 0
            ? null
            : string.Join("\n", record.Corrections.Select(reason => $"*Corrected from the upstream data: {reason.Trim()}*"));

    /// <summary>The document an upstream URL points at, or null when it names no record the lookup has.</summary>
    public static SrdDocument? Resolve(string? url, ISrdLookup lookup) =>
        SrdRef.FromApiUrl(url) is { } reference ? lookup.Get(reference.Edition, reference.Kind, reference.Slug) : null;

    /// <summary>
    /// "Name (<c>ref</c>)" for a bare URL (2014 feature prerequisites and references carry no name), with the name
    /// taken from the record. A URL that resolves to no record shows its last segment, so nothing claims a ref that
    /// <c>rules_get</c> would reject.
    /// </summary>
    public static string? ResolveLink(string url, ISrdLookup lookup)
    {
        if (Resolve(url, lookup) is { } doc)
        {
            return $"{doc.Name} (`{doc.Ref}`)";
        }

        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : null;
    }

    /// <summary>
    /// An upstream key as a label: <c>enemy_type_options</c> → "Enemy Type Options", <c>wild_shape_max_cr</c> →
    /// "Wild Shape Max CR", <c>song_of_rest_die</c> → "Song of Rest Die".
    /// </summary>
    public static string Humanize(string key)
    {
        var words = key.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", words.Select((word, i) => word switch
        {
            "cr" => "CR",
            _ when i > 0 && LowerCaseWords.Contains(word) => word,
            _ => char.ToUpperInvariant(word[0]) + word[1..],
        }));
    }
}
