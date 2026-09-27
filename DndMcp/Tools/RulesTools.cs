using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DndMcp.Domain.Core;
using DndMcp.Formatting;
using DndMcp.Formatting.Srd;
using DndMcp.Hosting;
using DndMcp.Repository.Sqlite;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using DndMcp.Resources;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>rules_search</c> and <c>rules_get</c>: find SRD entries by words, then read one (or one in both editions). Thin by
/// design: validate the arguments, ask <see cref="SrdIndex"/>, render with <see cref="SrdMarkdown"/>.
///
/// <para>
/// Every argument that can be checked without the index is checked before waiting for it (query and name words, and the
/// index's length and word caps, included), so a malformed call gets its correction at once even while the index is
/// still building or cannot be built. Every "not found" answer says where to look next (close names, the other edition,
/// a search, how level refs are formed), and every ref it offers has been looked up, because the model's only recovery
/// from a miss is what the message tells it: a bare "not found" makes it guess, and advice that cannot work costs a call
/// and a second error.
/// </para>
/// <para>
/// <b>Never a false "does not exist".</b> A comparison says an edition has nothing only after its recorded counterparts
/// and the same name (in the same kind, then any kind) have both failed, and then says the lookup found nothing, not
/// that the SRD lacks the rule: models relay "No 2014 equivalent in the SRD" as "2014 has no grappling rules", and the
/// 2014 SRD keeps Grappling inside Melee Attacks. A side found only by name is flagged as a guess to check.
/// </para>
/// <para>
/// <b>Say how a name reached an entry.</b> A name can reach an entry it is not the title of: a 2014 subsection heading
/// ("Grappling" in Melee Attacks), a 2024 name a 2014 section covers ("Unarmed Strike"), a curated everyday name
/// ("Shove"). The answer then says so (<see cref="HeadingNote"/>), and never claims a subsection the text does not have.
/// </para>
/// <para>
/// A query that finds srd.db deleted, replaced or damaged under the running server reopens the index and runs once more
/// (see <see cref="SrdIndexService.QueryAsync"/>), rather than failing every call until a restart the model cannot perform.
/// </para>
/// </summary>
public sealed partial class RulesTools
{
    public const string Both = "both";

    public const int DefaultLimit = 10;

    /// <summary>How many other matches an "Also named" line lists before summarising the rest.</summary>
    public const int MaxOtherMatches = 10;

    /// <summary>The ref <c>rules_get</c> answers with the SRD licence and attribution text (<see cref="RulesResources"/>).</summary>
    public const string AttributionRef = RulesResources.AttributionUri;

    // Longer than any SRD name; a ref slug past this is not worth reading as a name for suggestions.
    private const int MaxSlugAsNameLength = 100;

    private const int MaxEchoLength = 80;

    /// <summary>The <c>edition</c> values both tools accept, in the order their descriptions list them.</summary>
    public static IReadOnlyList<string> EditionValues { get; } = [SrdEdition.Edition2014, SrdEdition.Edition2024, Both];

    /// <summary>
    /// The 2024 SRD chapters whose text is not in the data, as the tool descriptions and server instructions name them. A
    /// model told only that "Playing the Game" was missing kept searching for multiclassing, travel or the one-slot-per-turn
    /// spell rule, then answered from memory as if quoting.
    /// </summary>
    public static IReadOnlyList<string> Missing2024Chapters { get; } =
        ["Playing the Game", "Character Creation", "Gameplay Toolbox", "Spells chapter's casting rules", "Equipment chapter's prose"];

    private const string EditionsText = "\"2014\", \"2024\" or \"both\"";

    private const string GetExamples = "Example: {\"ref\":\"2024/spell/fireball\"} or {\"name\":\"Fireball\"}.";

    // Names that mean the attribution text rather than an SRD entry (compared by SrdNames.Key).
    private static readonly HashSet<string> AttributionNames = new(StringComparer.Ordinal) { "attribution", "srd attribution" };

    private readonly SrdIndexService _indexService;
    private readonly DndMcpServerOptions _options;

    // Optional string arguments are nullable even where they have a default: models send null for "use the default", the
    // binder accepts it, and a non-nullable schema would make ToolArgumentGuard refuse the call for nothing.

    public RulesTools(SrdIndexService indexService, DndMcpServerOptions options)
    {
        _indexService = indexService;
        _options = options;
    }

    // Idempotent and closed-world: the answers are a pure function of the vendored content this binary ships.
    [McpServerTool(Name = "rules_search", Title = "Search the SRD rules", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Search the D&D 5e System Reference Document by words: 2014 rules (SRD 5.1) and 2024 rules (SRD 5.2.1). Covers spells, " +
        "monsters, classes and their features, subclasses, species/races, feats, backgrounds, equipment, magic items, conditions " +
        "and rules. Returns ranked entries, each with a ref and a snippet of the matching text; pass a ref to rules_get for the " +
        "whole entry. Use it to find entries on a topic (\"grapple\", \"difficult terrain\") or when unsure of a name; for a known " +
        "name call rules_get with name directly. Not in the data (so no search finds them): the 2024 SRD's Playing the Game, " +
        "Character Creation and Gameplay Toolbox chapters, its Spells chapter's casting rules and Equipment chapter's prose, and " +
        "the multiclassing rules in either edition. The rules tables (XP by CR, both editions' encounter budgets and " +
        "thresholds) are not searched either: rules_get ref \"rules://tables\" lists them.\n" +
        "- query: the words to find. Every word must match; end a word with * for a prefix (fire*). An entry whose name is the " +
        "query comes first. If no entry has every word, entries matching any word are returned and the result says so.\n" +
        "- edition: \"2024\" (default), \"2014\", or \"both\" to search both SRDs.\n" +
        "- kinds: optional filter, e.g. [\"spell\"] or [\"rule\",\"condition\"]. Kinds: ability-score, alignment, background, " +
        "class, condition, damage-type, equipment, equipment-category, feat, feature, language, magic-item, magic-school, " +
        "monster, poison (2024), proficiency, race (2014; species in 2024), rule, skill, species, spell, subclass, subrace, " +
        "subspecies, trait, weapon-mastery (2024), weapon-property. Plurals work too.\n" +
        "- limit: how many entries to return, 1-50. Default 10.\n" +
        "Example: {\"query\":\"grapple escape\",\"kinds\":[\"rule\",\"condition\"]}")]
    public async Task<string> Search(
        [Description("Words to find, e.g. \"fireball\", \"grapple escape\" or \"fire*\".")] string query,
        [Description("\"2024\" (default), \"2014\" or \"both\".")] string? edition = SrdEdition.Edition2024,
        [Description("Optional kinds to search within, e.g. [\"spell\", \"monster\"]. Omit to search everything.")] string[]? kinds = null,
        [Description("Most entries to return, 1-50. Default 10.")] int limit = DefaultLimit,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var editions = SearchEditions(edition);
        if (limit is < 1 or > SrdIndex.MaxSearchLimit)
        {
            throw new DndInputException($"limit must be between 1 and {SrdIndex.MaxSearchLimit} (got {limit}).");
        }

        var kindFilter = KindFilter(kinds, editions);
        CheckQueryBounds(query);
        if (Fts5Query.Terms(query) is null)
        {
            throw new DndInputException(
                $"The query needs at least one word to search for (letters or digits); \"{Echo(query)}\" has none. " +
                "Example: \"fireball\", \"grapple escape\" or a prefix like \"fire*\".");
        }

        return await QueryAsync(
            index => FormatSearch(query, editions, kindFilter, limit, index.Search(query, editions, kindFilter, limit)),
            progress,
            cancellationToken);
    }

    [McpServerTool(Name = "rules_get", Title = "Get an SRD entry", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "The full text of one D&D 5e SRD entry as markdown (2014 rules: SRD 5.1; 2024 rules: SRD 5.2.1): a monster stat block, " +
        "a spell, a class with its level table, a subclass, species/race, feat, background, item, condition or rule (2024 " +
        "rules are the SRD 5.2.1 Rules Glossary). With edition \"both\" it shows the entry from each edition side by side, " +
        "with a comparison table for spells and monsters. Quote rules from here rather than from memory. Not in the data: the " +
        "2024 SRD's Playing the Game, Character Creation and Gameplay Toolbox chapters (apart from its encounter budget, a " +
        "rules table), its Spells chapter's casting rules and Equipment chapter's prose, and the multiclassing rules in either " +
        "edition (a class shows only its multiclassing " +
        "prerequisites); say a rule is not in this server's data rather than quoting it from memory.\n" +
        "Give exactly one of:\n" +
        "- ref: an entry's ref from rules_search or an earlier result, e.g. \"2024/spell/fireball\"; \"spell/fireball\" uses " +
        "edition; an API URL like \"/api/2014/monsters/goblin\" works too. \"rules://attribution\" gives the SRD licence text; " +
        "\"rules://tables\" lists the rules tables (XP by CR, encounter budgets and thresholds, DMG monster statistics by CR), " +
        "each also by its name, e.g. name \"XP Budget per Character\".\n" +
        "- name: the entry's name, e.g. \"Fireball\", \"Adult Red Dragon\", \"Grappled\". Case and punctuation don't matter, and " +
        "the other edition's name for a renamed entry works (\"Thug\" finds the 2024 Tough).\n" +
        "Optional:\n" +
        "- kind: which kind of entry a name means when several share it, e.g. name \"Shield\" with kind \"magic-item\". Without " +
        "it the likeliest is shown (the spell) and the others are listed.\n" +
        "- edition: \"2024\" (default), \"2014\", or \"both\" to compare. A ref's own edition is used when edition is omitted.\n" +
        "- format: \"concise\" (default) or \"full\", which adds the raw SRD JSON.\n" +
        "When nothing matches, the error lists close names and whether the other edition has the entry.\n" +
        "Example: {\"name\":\"grappled\",\"edition\":\"both\"}")]
    public async Task<string> Get(
        [Description("An entry's ref, e.g. \"2024/spell/fireball\" or \"spell/fireball\". Give ref or name, not both.")] string? @ref = null,
        [Description("An entry's name, e.g. \"Fireball\". Give ref or name, not both.")] string? name = null,
        [Description("Optional kind for name, e.g. \"spell\", \"monster\", \"magic-item\", \"rule\".")] string? kind = null,
        [Description("\"2024\" (default), \"2014\" or \"both\" (compare the editions).")] string? edition = null,
        [Description("\"concise\" (default) or \"full\" (adds the raw SRD JSON).")] string? format = SrdMarkdown.Concise,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var hasRef = !string.IsNullOrWhiteSpace(@ref);
        var hasName = !string.IsNullOrWhiteSpace(name);
        if (hasRef == hasName)
        {
            throw new DndInputException(hasRef
                ? $"Give ref or name, not both. {GetExamples}"
                : $"Give either ref or name. {GetExamples}");
        }

        var chosenFormat = Format(format);
        var chosenEdition = GetEdition(edition);
        var kindText = string.IsNullOrWhiteSpace(kind) ? null : kind;

        // The licence text and the rules tables are resources, which Claude Desktop only attaches by hand; every resource
        // is also reachable through a tool. A table answers to its URI and to its names (none of which any SRD entry has;
        // RulesTablesTests pins that), and needs no index, so it works while srd.db is still building.
        if (hasRef ? IsAttributionRef(@ref!) : kindText is null && AttributionNames.Contains(SrdNames.Key(name!)))
        {
            return new RulesResources(_options).Attribution();
        }

        if (hasRef && RulesTables.IsTablesUri(@ref!))
        {
            return RulesTables.Answer(@ref!);
        }

        if (!hasRef && kindText is null && RulesTables.FindByName(name!) is { } table)
        {
            return table.Render();
        }

        return hasRef
            ? await GetByRefAsync(@ref!, kindText, chosenEdition, chosenFormat, progress, cancellationToken)
            : await GetByNameAsync(name!.Trim(), kindText, chosenEdition, chosenFormat, progress, cancellationToken);
    }

    private async Task<string> GetByRefAsync(
        string refText,
        string? kind,
        string? edition,
        string format,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        SrdRef reference;
        bool editionGiven;
        try
        {
            reference = ParseRef(refText, edition is SrdEdition.Edition2014 ? SrdEdition.Edition2014 : SrdEdition.Edition2024, out editionGiven);
        }
        catch (RefKindInOtherEditionException moved)
        {
            throw await MovedRefAsync(moved, progress, cancellationToken);
        }

        if (kind is not null && SrdKindNames.Normalize(kind, reference.Edition) is var kindInEdition && kindInEdition != reference.Kind)
        {
            throw new DndInputException(
                $"kind \"{Echo(kind.Trim())}\" does not match ref `{Echo(reference.ToString())}`, which is a {reference.Kind}. A ref " +
                "already names its kind; kind only narrows a name.");
        }

        if (editionGiven && edition is SrdEdition.Edition2014 or SrdEdition.Edition2024 && edition != reference.Edition)
        {
            throw await EditionConflictAsync(reference, edition, progress, cancellationToken);
        }

        return await QueryAsync(
            index =>
            {
                var doc = index.Get(reference) ?? throw RefNotFound(index, reference);
                return edition == Both
                    ? Compare(index, doc, format, alsoNamed: null, typedName: null)
                    : SrdMarkdown.Format(doc, format, index);
            },
            progress,
            cancellationToken);
    }

    private async Task<string> GetByNameAsync(
        string name,
        string? kind,
        string? edition,
        string format,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken)
    {
        CheckNameBounds(name);
        if (SrdNames.Key(name).Length == 0)
        {
            throw new DndInputException(
                $"The name needs at least one letter or digit; \"{Echo(name)}\" has none. Example: name \"Fireball\".");
        }

        // A name lookup runs in one edition. "both" looks in 2024 first; FindByName's other-edition matches then supply the
        // 2014 entry when 2024 has none, so a 2014-only entry still compares.
        var lookupEdition = edition == SrdEdition.Edition2014 ? SrdEdition.Edition2014 : SrdEdition.Edition2024;
        var kindInEdition = kind is null ? null : KindForNameLookup(kind, lookupEdition, edition == Both);

        return await QueryAsync(
            index =>
            {
                var lookup = index.FindByName(name, lookupEdition, kindInEdition);
                IReadOnlyList<SrdNameMatch> matches = lookup.Matches;
                if (matches.Count == 0 && edition == Both)
                {
                    matches = lookup.OtherEdition;
                }

                if (matches.Count == 0)
                {
                    throw NameNotFound(index, name, lookupEdition, kindInEdition, edition == Both, lookup);
                }

                var best = matches[0];
                var others = matches.Skip(1).Select(m => m.Document).Where(d => d.Ref != best.Document.Ref).DistinctBy(d => d.Ref).ToList();
                var alsoNamed = AlsoNamed(name, best.Document, others, kindGiven: kind is not null);

                return edition == Both
                    ? Compare(index, best.Document, format, alsoNamed, typedName: best.MatchedAlias ?? name)
                    : SrdMarkdown.Format(best.Document, format, index, alsoNamed, HeadingNote(best.Document, best.MatchedAlias ?? name));
            },
            progress,
            cancellationToken);
    }

    // SrdIndexService.QueryAsync reopens the index once when srd.db was deleted or replaced under the running server.
    private Task<T> QueryAsync<T>(
        Func<SrdIndex, T> query, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken) =>
        _indexService.QueryAsync(query, progress, cancellationToken);

    /// <summary>
    /// The edition-conflict error, offering a ref in the asked edition only when one exists: the ref's recorded
    /// counterparts, or failing those an entry of the same name, flagged as a name match (in 2014 "Darkness" is the spell,
    /// not the 2024 lighting rule). Swapping the edition into the ref blindly offered refs that did not exist for a third
    /// of all entries (2024/monster/thug), and a kind the asked edition lacks (poison in 2014) has no ref to offer. A ref
    /// that does not exist at all gets the not-found answer with its did-you-mean, not "is a 2014 entry".
    /// </summary>
    private async Task<Exception> EditionConflictAsync(
        SrdRef reference, string edition, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var head = $"ref `{reference}` is a {reference.Edition} entry but edition is \"{edition}\". Leave edition out to use the ref's own";
        var withoutRef = $"{head}, or pass \"{Both}\" to compare the editions.";

        var kindThere = KindIn(edition, reference.Kind);
        if (!SrdKinds.ExistsIn(kindThere, edition))
        {
            return new DndInputException($"{withoutRef} The {edition} SRD has no {reference.Kind} entries.");
        }

        OtherSideMatch? offered;
        try
        {
            offered = await QueryAsync(
                index => (index.Get(reference) ?? throw RefNotFound(index, reference)) is var doc ? OtherSide(index, doc, typedName: null) : null,
                progress,
                cancellationToken);
        }
        catch (SrdIndexUnavailableException)
        {
            // The conflict is worth reporting even when no index can be opened; only a verified ref needs one.
            return new DndInputException(withoutRef);
        }

        var offer = $"{head}, pass \"{Both}\" to compare the editions, or use";
        return offered switch
        {
            null => new DndInputException(
                $"{withoutRef} No {edition} entry matches it by a known rename or by name; rules_search with edition \"{edition}\" " +
                "may find it under another heading."),
            { MatchedBy: MatchedBy.Name } side => new DndInputException(
                $"{offer} the ref {side.Best.Ref}, which was matched by name only{KindDiffers(reference.Kind, side.Best)}: check it " +
                "is the same thing."),
            { Others.Count: 0 } side => new DndInputException($"{offer} the ref {side.Best.Ref}."),
            var side => new DndInputException(
                $"{offer} one of the refs {string.Join(", ", new[] { side.Best }.Concat(side.Others).Select(d => d.Ref))}."),
        };

        static string KindDiffers(string kind, SrdDocument found) =>
            found.Kind == kind ? string.Empty : $" (a {found.Kind} there, not a {kind})";
    }

    /// <summary>
    /// The answer to a ref whose kind only the other edition has ("2014/poison/serpent-venom",
    /// "/api/2014/poisons/serpent-venom"): the parser's advice, "pass edition 2024", cannot work, because a ref's own edition
    /// wins over the edition argument, so the same error came back whatever edition the model passed. The ref in the
    /// edition that has the kind is offered only when that entry exists; otherwise the not-found answer for it.
    /// </summary>
    private async Task<Exception> MovedRefAsync(
        RefKindInOtherEditionException moved, IProgress<ProgressNotificationValue>? progress, CancellationToken cancellationToken)
    {
        var head = $"ref `{moved.Given}` names the {moved.GivenEdition} SRD, which has no {moved.Ref.Kind} entries; only the " +
                   $"{moved.Ref.Edition} SRD has them.";
        try
        {
            return await QueryAsync(
                index => index.Get(moved.Ref) is { } doc
                    ? new DndInputException($"{head} Use the ref {doc.Ref}.", moved)
                    : RefNotFound(index, moved.Ref, head),
                progress,
                cancellationToken);
        }
        catch (SrdIndexUnavailableException)
        {
            return new DndInputException($"{head} Write {moved.Ref.Edition} in the ref instead of {moved.GivenEdition}.", moved);
        }
    }

    /// <summary>
    /// <paramref name="doc"/> beside the other edition's matching entry (<see cref="OtherSide"/>). Where the SRD split one
    /// entry into several (2014 Succubus/Incubus is two 2024 monsters) the comparison uses the one the caller named, or
    /// else the first, and the rest are listed so the model can compare those too.
    /// </summary>
    /// <param name="typedName">The name the caller asked for (or the alias it matched), looked up in the other edition too.</param>
    private static string Compare(SrdIndex index, SrdDocument doc, string format, string? alsoNamed, string? typedName)
    {
        var side = OtherSide(index, doc, typedName);
        var other = side?.Best;
        var (doc2014, doc2024) = doc.Edition == SrdEdition.Edition2014 ? (doc, other) : (other, doc);

        string? moreCounterparts = null;
        if (side is { MatchedBy: MatchedBy.Counterpart, Others.Count: > 0 })
        {
            moreCounterparts =
                $"{doc.Name} (`{doc.Ref}`) also corresponds to {LinkList(side.Others)} in the {side.Best.Edition} SRD; " +
                "pass one as ref with edition \"both\" to compare it.";
        }

        // Notes sit with the side they qualify: how the other side was found, and which subsection of either side covers
        // the topic asked about (2014 Grappling is a heading inside Melee Attacks). A side nothing matched gets, for a
        // level record, where that edition's levels are instead of a search that cannot return levels.
        var docNote = typedName is null ? null : HeadingNote(doc, typedName);
        var otherNote = other is null
            ? (doc.Kind == SrdKinds.Level ? MissingLevelHint(index, doc) : null)
            : Lines(
                side is { MatchedBy: MatchedBy.Name } ? NameMatchNote(doc, side) : null,
                HeadingNote(other, typedName ?? doc.Name) ?? (typedName is null ? null : HeadingNote(other, doc.Name)));
        var (note2014, note2024) = doc.Edition == SrdEdition.Edition2014 ? (docNote, otherNote) : (otherNote, docNote);

        return SrdMarkdown.Compare(doc2014, doc2024, format, index, Notes(moreCounterparts, alsoNamed), note2014, note2024);
    }

    /// <summary>
    /// The other edition's entry for <paramref name="doc"/>, or null when nothing there matches. In order:
    /// <list type="number">
    /// <item>Its recorded counterparts, the one the caller named first (<see cref="SrdIndex.Counterparts(SrdDocument, string?)"/>:
    /// "Swarm of Wasps" compares the wasps, not the first of 2024 Swarm of Insects' five 2014 counterparts); when the name
    /// reached <paramref name="doc"/> as part of it and no counterpart answers to it, the counterpart whose text uses it
    /// most (2014 "Food" in Food and Water compares 2024 Malnutrition, not Dehydration).</item>
    /// <item>The same name, in the same kind and then in any kind (2014's Divine Smite is a feature, 2024's a spell). A
    /// subsection heading or a section a 2024 name is covered by is not the other edition's entry, so those matches are
    /// skipped (2024 Armor Class once compared against 2014 Dexterity, whose "Armor Class" heading is Dexterity's part in
    /// it).</item>
    /// </list>
    /// The index pairs every same-kind, same-slug document, so a slug match needs no step of its own. Only when all of these
    /// fail may the comparison say nothing matched: "No 2014 equivalent" for an entry the 2014 SRD has under the same name
    /// was relayed by models as a rules fact.
    /// </summary>
    private static OtherSideMatch? OtherSide(SrdIndex index, SrdDocument doc, string? typedName)
    {
        var counterparts = index.Counterparts(doc, typedName);
        if (counterparts.Count > 0)
        {
            var ordered = MentionedFirst(doc, counterparts, typedName);
            return new OtherSideMatch(ordered[0], [.. ordered.Skip(1)], MatchedBy.Counterpart);
        }

        var otherEdition = Other(doc.Edition);
        var otherKind = KindIn(otherEdition, doc.Kind);
        var kindExists = SrdKinds.ExistsIn(otherKind, otherEdition);
        string[] names =
            [.. new[] { typedName, doc.Name }.OfType<string>().Where(n => SrdNames.Key(n).Length > 0).DistinctBy(SrdNames.Key)];
        foreach (var kind in kindExists ? new[] { otherKind, null } : [null])
        {
            foreach (var name in names)
            {
                if (SameName(index, name, otherEdition, kind) is { Count: > 0 } found)
                {
                    // An entry already paired with another one has its own counterpart; prefer one that is free.
                    var ordered = found.Take(MaxOtherMatches + 1).OrderBy(d => index.Counterparts(d).Count > 0 ? 1 : 0)
                        .Concat(found.Skip(MaxOtherMatches + 1)).ToList();
                    return new OtherSideMatch(ordered[0], [.. ordered.Skip(1)], MatchedBy.Name);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="counterparts"/> with the one whose text mentions <paramref name="typedName"/> most first, when that
    /// name reached <paramref name="doc"/> as a part of it (a heading, a section name) and no counterpart answers to it;
    /// otherwise unchanged. 2014 Food and Water pairs with 2024 Dehydration and Malnutrition: "Food" means Malnutrition,
    /// which no alias records, and index order put Dehydration first.
    /// </summary>
    private static IReadOnlyList<SrdDocument> MentionedFirst(SrdDocument doc, IReadOnlyList<SrdDocument> counterparts, string? typedName)
    {
        if (typedName is null || counterparts.Count < 2 || counterparts.Any(c => c.AnswersTo(typedName)) ||
            SrdNames.Key(typedName) is var key && (key.Length == 0 || key == SrdNames.Key(doc.Name)))
        {
            return counterparts;
        }

        var counts = counterparts.Select(d => (Doc: d, Count: Mentions(d.Root, key))).ToList();
        return counts.Any(c => c.Count > 0)
            ? [.. counts.OrderByDescending(c => c.Count).Select(c => c.Doc)]
            : counterparts;
    }

    // How often the name key (or its plural) occurs as whole words in the record's text.
    private static int Mentions(JsonElement element, string key) => element.ValueKind switch
    {
        JsonValueKind.String => Occurrences($" {SrdNames.Key(element.GetString() ?? string.Empty)} ", key),
        JsonValueKind.Array => element.EnumerateArray().Sum(e => Mentions(e, key)),
        JsonValueKind.Object => element.EnumerateObject().Sum(p => Mentions(p.Value, key)),
        _ => 0,
    };

    private static int Occurrences(string text, string key)
    {
        var count = 0;
        foreach (var form in new[] { $" {key} ", $" {key}s " })
        {
            for (var at = text.IndexOf(form, StringComparison.Ordinal); at >= 0; at = text.IndexOf(form, at + 1, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static List<SrdDocument> SameName(SrdIndex index, string name, string edition, string? kind)
    {
        try
        {
            return index.FindByName(name, edition, kind).Matches
                .Where(m => m.Source is not (SrdAliasSources.Heading or SrdAliasSources.Section))
                .Select(m => m.Document)
                .DistinctBy(d => d.Ref)
                .ToList();
        }
        catch (DndInputException)
        {
            // A name the index will not look up (no letters, too long) matches nothing.
            return [];
        }
    }

    private static string NameMatchNote(SrdDocument doc, OtherSideMatch side)
    {
        var matched = side.Best;
        var note = new StringBuilder(
            $"*Matched to the {doc.Edition} entry by name only (no recorded counterpart links them): check that they are the same thing.");
        if (matched.Kind != doc.Kind)
        {
            var (kind2014, kind2024) = doc.Edition == SrdEdition.Edition2014 ? (doc.Kind, matched.Kind) : (matched.Kind, doc.Kind);
            note.Append($" Its kind differs: {kind2014} in 2014, {kind2024} in 2024.");
        }

        if (side.Others.Count > 0)
        {
            note.Append($" Other {matched.Edition} entries with this name: ").Append(RefList(side.Others)).Append('.');
        }

        return note.Append('*').ToString();
    }

    /// <summary>
    /// Why <paramref name="name"/> answers with <paramref name="doc"/> when it is not the entry's title, or null when it
    /// is (or reached it as the other edition's name or a bare glossary name, which the meta line already explains).
    /// <list type="bullet">
    /// <item>A 2014 subsection heading (<see cref="SrdAliasSources.Heading"/>): "*Grappling is covered by this entry's
    /// #### Grappling subsection.*"</item>
    /// <item>A 2024 name a 2014 section covers (<see cref="SrdAliasSources.Section"/>) or a curated everyday name
    /// (<see cref="SrdAliasSources.Curated"/>): "*Unarmed Strike is covered by this entry.*", naming the heading or run-in
    /// paragraph that holds it when there is one ("Climbing" is in "#### Climbing, Swimming, and Crawling", "Long Jump"
    /// in the "Long Jump paragraph", "Critical Hit" in "#### Critical Hits").</item>
    /// </list>
    /// Without it, "Grappling" answered with "# Melee Attacks" reads as the wrong entry: the model distrusts a correct
    /// answer, or quotes the whole section as the grappling rule. It never names a subsection the text does not have: a
    /// section alias said "this entry's Unarmed Strike subsection" of a rule that mentions unarmed strikes in one sentence.
    /// Singular, plural and "-ing" forms count as the same word.
    /// </summary>
    internal static string? HeadingNote(SrdDocument doc, string name)
    {
        var key = SrdNames.Key(name);
        if (key.Length == 0)
        {
            return null;
        }

        var located = doc.Aliases
            .Where(a => a.Source is SrdAliasSources.Heading or SrdAliasSources.Section or SrdAliasSources.Curated)
            .ToList();
        var stems = Stems(key);
        var alias = located.FirstOrDefault(a => SrdNames.Key(a.Name) == key) ??
                    located.FirstOrDefault(a => Stems(SrdNames.Key(a.Name)).SequenceEqual(stems));
        if (alias is null || Stems(SrdNames.Key(alias.Name)).SequenceEqual(Stems(SrdNames.Key(doc.Name))))
        {
            // Not a part of this entry, or an everyday form of its own name ("Grapple" for Grappling).
            return null;
        }

        var where = Subsection(doc.Root, Stems(SrdNames.Key(alias.Name)));
        return where is null
            ? $"*{alias.Name} is covered by this entry.*"
            : $"*{alias.Name} is covered by this entry's {where}.*";
    }

    // Where in the record's text the words are: "#### Grappling subsection" for a markdown heading, "Serpent Venom
    // (Injury) paragraph" for a run-in one ("***Serpent Venom (Injury).*** A creature…", whose trailing parenthetical the
    // name may leave out). A heading that is the words comes first, then one that contains them ("Climbing" is in
    // "#### Climbing, Swimming, and Crawling"); null when neither exists.
    private static string? Subsection(JsonElement root, IReadOnlyList<string> stems)
    {
        var headings = new List<(string Display, string[] Stems, string[]? Short)>();
        CollectHeadings(root, headings);
        return headings.FirstOrDefault(h => h.Stems.SequenceEqual(stems) || h.Short?.SequenceEqual(stems) == true).Display ??
               headings.FirstOrDefault(h => ContainsRun(h.Stems, stems)).Display;
    }

    private static void CollectHeadings(JsonElement element, List<(string Display, string[] Stems, string[]? Short)> headings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                foreach (Match heading in MarkdownHeading().Matches(text))
                {
                    var title = heading.Groups["text"].Value;
                    headings.Add(($"{heading.Groups["marks"].Value} {title} subsection", Stems(SrdNames.Key(title)), null));
                }

                foreach (Match runIn in RunInHeading().Matches(text))
                {
                    var title = runIn.Groups["text"].Value.Trim();
                    var shorter = TrailingParenthetical().Replace(title, string.Empty);
                    headings.Add(($"{title} paragraph", Stems(SrdNames.Key(title)), shorter == title ? null : Stems(SrdNames.Key(shorter))));
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectHeadings(item, headings);
                }

                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectHeadings(property.Value, headings);
                }

                break;
        }
    }

    // A name key's words reduced to a crude stem, so "Critical Hit" is "Critical Hits", "Grapple" is "Grappling" and
    // "Shove" is "Shoving". Only for finding where a name sits in one entry's text, never for matching names.
    private static string[] Stems(string key) =>
        key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToArray();

    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            word = word[..^3];
        }
        else if (word.Length > 3 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
        {
            word = word[..^1];
        }

        return word.Length > 3 && word.EndsWith('e') ? word[..^1] : word;
    }

    private static bool ContainsRun(string[] words, IReadOnlyList<string> run)
    {
        for (var start = 0; start + run.Count <= words.Length; start++)
        {
            if (words.Skip(start).Take(run.Count).SequenceEqual(run))
            {
                return true;
            }
        }

        return false;
    }

    private static DndInputException RefNotFound(SrdIndex index, SrdRef reference, string? lead = null)
    {
        var message = new StringBuilder();
        if (lead is not null)
        {
            message.Append(lead).Append(' ');
        }

        message.Append($"No {reference.Edition} {reference.Kind} has the slug \"{Echo(reference.Slug)}\" (`{Echo(reference.ToString())}`).");

        // The slug read as a name: "fire-ball" is within a typo of Fireball, and a real name's slug finds it exactly. Level
        // records are not searchable, so no name lookup can suggest one; a slug too long to be a name is not tried at all.
        var asName = reference.Slug.Replace('-', ' ');
        if (reference.Kind != SrdKinds.Level && asName.Length <= MaxSlugAsNameLength && SrdNames.Key(asName).Length > 0)
        {
            try
            {
                var lookup = index.FindByName(asName, reference.Edition, reference.Kind);
                var close = lookup.Matches.Count > 0
                    ? lookup.Matches.DistinctBy(m => m.Document.Ref).ToList()
                    : lookup.SuggestionMatches;
                if (close.Count > 0)
                {
                    message.Append(" Did you mean ").Append(string.Join(" or ", close.Select(MatchLink))).Append('?');
                }
            }
            catch (DndInputException)
            {
                // The index refused the slug as a name (too many words); the other hints still apply.
            }
        }

        var otherEdition = Other(reference.Edition);
        var otherKind = KindIn(otherEdition, reference.Kind);
        if (SrdKinds.ExistsIn(otherKind, otherEdition) && index.Get(otherEdition, otherKind, reference.Slug) is { } other)
        {
            message.Append($" The {otherEdition} SRD has {Link(other)} — pass that ref, or edition \"{Both}\" with it to compare.");
        }

        message.Append(reference.Kind == SrdKinds.Level ? " " + LevelHint(index, reference) : " rules_search finds entries by words.");
        return new DndInputException(message.ToString());
    }

    /// <summary>
    /// How level refs are formed, instead of the usual "rules_search finds entries by words": level records are left out
    /// of the search index, so that advice could only fail. When the slug names a real class or subclass, its actual
    /// levels are given (2014/level/fighter-21 → Fighter runs 1–20), including a 2024 subclass whose level slugs use a
    /// short name ("berserker-3" for Path of the Berserker, "devotion-3" for Oath of Devotion).
    /// </summary>
    private static string LevelHint(SrdIndex index, SrdRef reference)
    {
        var edition = reference.Edition;
        if (LevelSlug().Match(reference.Slug) is { Success: true } slug && OwnerLevels(index, edition, slug.Groups["owner"].Value) is { } hint)
        {
            return hint;
        }

        return $"Level refs are {{class}}-{{level}} (e.g. `{edition}/level/fighter-5`) or a subclass's level slug (e.g. " +
               $"`{edition}/level/{(edition == SrdEdition.Edition2014 ? "champion-3" : "berserker-3")}`); rules_get with a " +
               "class's or subclass's name shows its levels.";
    }

    // "Fighter levels in the 2014 SRD run 1–20, …" for a class slug; "Path of the Berserker levels in the 2024 SRD are 3, 6,
    // 10 and 14, …" for a subclass, by its slug or the short name its level slugs start with; null for neither.
    private static string? OwnerLevels(SrdIndex index, string edition, string owner)
    {
        if (index.ClassLevels(edition, owner) is { Count: > 0 } classLevels)
        {
            var name = index.Get(edition, SrdKinds.Class, owner)?.Name ?? owner;
            var levels = classLevels.Select(LevelOf).OfType<int>().ToList();
            return $"{name} levels in the {edition} SRD run {levels.Min()}–{levels.Max()}, e.g. `{classLevels[^1].Ref}`; rules_get " +
                   $"name \"{name}\" shows the whole table.";
        }

        var subclass = index.Get(edition, SrdKinds.Subclass, owner) ?? ShortSubclassName(index, edition, owner);
        return subclass is not null && index.SubclassLevels(edition, subclass.Slug) is { Count: > 0 } subclassLevels
            ? SubclassLevelsText(subclass, subclassLevels)
            : null;
    }

    // The subclass whose level slugs start with this short name: 2024 "berserker-3" is Path of the Berserker's, found by
    // its 2014 name "Berserker".
    private static SrdDocument? ShortSubclassName(SrdIndex index, string edition, string owner)
    {
        var name = owner.Replace('-', ' ');
        if (name.Length > MaxSlugAsNameLength || SrdNames.Key(name).Length == 0)
        {
            return null;
        }

        try
        {
            return index.FindByName(name, edition, SrdKinds.Subclass).Matches
                .Select(m => m.Document)
                .FirstOrDefault(s => index.SubclassLevels(edition, s.Slug).Any(l => l.Slug.StartsWith(owner + "-", StringComparison.Ordinal)));
        }
        catch (DndInputException)
        {
            return null;
        }
    }

    private static string SubclassLevelsText(SrdDocument subclass, IReadOnlyList<SrdDocument> levels)
    {
        var numbers = levels.Select(LevelOf).OfType<int>().Select(l => l.ToString(CultureInfo.InvariantCulture)).ToList();
        return $"{subclass.Name} levels in the {subclass.Edition} SRD are {AndList(numbers)}, e.g. `{levels[0].Ref}`; rules_get " +
               $"name \"{subclass.Name}\" shows them all.";
    }

    /// <summary>
    /// For a level record the other edition has no match for (2014 draconic-1: 2024 Draconic Sorcery starts at 3), where
    /// that edition's levels of the same class or subclass are, replacing "try rules_search", which never returns levels.
    /// </summary>
    private static string MissingLevelHint(SrdIndex index, SrdDocument level)
    {
        var otherEdition = Other(level.Edition);
        var root = level.Root;
        if (root.Obj("subclass")?.Str("index") is { } subclassSlug &&
            index.Get(level.Edition, SrdKinds.Subclass, subclassSlug) is { } subclass &&
            index.Counterparts(subclass).FirstOrDefault() is { } otherSubclass &&
            index.SubclassLevels(otherEdition, otherSubclass.Slug) is { Count: > 0 } levels)
        {
            return $"*{SubclassLevelsText(otherSubclass, levels)}*";
        }

        return root.Obj("class")?.Str("index") is { } classSlug && OwnerLevels(index, otherEdition, classSlug) is { } classHint
            ? $"*{classHint}*"
            : $"*Level records are not searchable; rules_get with a class's or subclass's name and edition {otherEdition} shows its levels.*";
    }

    private static int? LevelOf(SrdDocument level) =>
        level.Root.TryGetProperty("level", out var value) && value.TryGetInt32(out var number) ? number : null;

    private static DndInputException NameNotFound(SrdIndex index, string name, string edition, string? kind, bool bothEditions, SrdNameLookup lookup)
    {
        var editions = bothEditions ? "the 2014 or 2024 SRD" : $"the {edition} SRD";
        var what = kind is null ? editions : $"{editions}'s {kind} entries";
        var message = new StringBuilder($"Nothing in {what} is named \"{Echo(name)}\"");

        // "Nothing … is named", never "Not in the 2014 SRD": a name lookup cannot know the SRD lacks the rule under another
        // heading, and the model relays whichever it is told. A match inside another entry says so ("Instant Death (in
        // Dropping to 0 Hit Points, …)"): "the 2014 SRD has Creating Sentient Magic Items" for "Senses" read as the answer.
        if (lookup.OtherEdition.Count > 0)
        {
            var other = lookup.OtherEdition[0].Document.Edition;
            message.Append($"; the {other} SRD has ")
                .Append(string.Join(", ", lookup.OtherEdition.DistinctBy(m => m.Document.Ref).Select(MatchLink)))
                .Append($" — pass edition {other} or {Both}.");
        }
        else
        {
            message.Append('.');
        }

        // A ref passed as name ("2024/spell/fireball") gets only the "pass it as ref" advice: its words ("spell",
        // "fireball") make close names look relevant, and a list of them buries the one fix that works.
        var refShaped = name.Contains('/');
        if (!refShaped && lookup.SuggestionMatches.Count > 0)
        {
            message.Append($" Close names in the {edition} SRD: ").Append(string.Join(", ", lookup.SuggestionMatches.Select(MatchLink))).Append('.');
        }

        // With both, the lookup ran in 2024; a typo of a 2014-only entry ("Duergr") is close only to 2014 names.
        if (bothEditions && !refShaped)
        {
            var kind2014 = kind is null ? null : SrdCounterparts.KindIn2014(kind);
            if (kind2014 is null || SrdKinds.ExistsIn(kind2014, SrdEdition.Edition2014))
            {
                var lookup2014 = index.FindByName(name, SrdEdition.Edition2014, kind2014);
                var close = lookup2014.Matches.Count > 0
                    ? lookup2014.Matches.DistinctBy(m => m.Document.Ref).ToList()
                    : lookup2014.SuggestionMatches;
                if (close.Count > 0)
                {
                    message.Append(" Close names in the 2014 SRD: ").Append(string.Join(", ", close.Select(MatchLink))).Append('.');
                }
            }
        }

        if (refShaped)
        {
            message.Append($" \"{Echo(name)}\" looks like a ref: pass it as ref instead of name.");
        }
        else if (kind == SrdKinds.Level)
        {
            message.Append(" Level records are not searchable; rules_get with a class's name shows its whole level table.");
        }
        else
        {
            message.Append(bothEditions
                ? $" rules_search with edition \"{Both}\" finds entries by words."
                : " rules_search finds entries by words.");
        }

        return new DndInputException(message.ToString());
    }

    /// <summary>
    /// A matched or suggested entry as the reader must see it: "Fireball (`2024/spell/fireball`)" for its own name, and
    /// for another name what that name is. "Grappling (in Melee Attacks, `2014/rule/melee-attacks`)" is a part of that
    /// entry; "Thug (2014 name of Tough, `2024/monster/tough`)" is a rename. Shown only as "Melee Attacks", a suggestion
    /// for "Grapling" looked unrelated, and "the 2014 SRD has Creating Sentient Magic Items" for "Senses" looked like the
    /// answer.
    /// </summary>
    private static string MatchLink(SrdNameMatch match) => (match.Source, match.MatchedAlias) switch
    {
        (SrdAliasSources.Heading or SrdAliasSources.Section or SrdAliasSources.Curated, { } alias) =>
            $"{alias} (in {match.Document.Name}, `{match.Document.Ref}`)",
        (SrdAliasSources.Counterpart, { } alias) =>
            $"{alias} ({Other(match.Document.Edition)} name of {match.Document.Name}, `{match.Document.Ref}`)",
        _ => Link(match.Document),
    };

    /// <summary>
    /// The ref <paramref name="text"/> names. A kind only the other edition has ("2014/poison/…", "/api/2014/poisons/…")
    /// is <see cref="RefKindInOtherEditionException"/>, for <see cref="MovedRefAsync"/> to answer with a looked-up ref.
    /// </summary>
    private static SrdRef ParseRef(string text, string defaultEdition, out bool editionGiven)
    {
        try
        {
            return SrdRefParser.Parse(text, defaultEdition, out editionGiven);
        }
        catch (DndInputException ex) when (!text.Contains('/'))
        {
            // Text with no slash was almost certainly meant as a name ("Fireball"), and the fix is a different argument.
            throw new DndInputException($"{ex.Message} To look an entry up by its name, pass name \"{Echo(text.Trim())}\" instead.", ex);
        }
        catch (DndInputException ex) when (InTheOtherEdition(text) is { } moved)
        {
            throw new RefKindInOtherEditionException(moved.Given, moved.GivenEdition, moved.Ref, ex);
        }
    }

    // A ref whose kind exists only in the other edition, re-read in that edition: an edition/kind/slug ref, or an API URL
    // ("/api/2014/poisons/serpent-venom", with or without the host). Null otherwise.
    private static (string Given, string GivenEdition, SrdRef Ref)? InTheOtherEdition(string text)
    {
        var trimmed = text.Trim().Trim('`', '"', '\'').Trim();
        string given;
        string swapped;
        var apiStart = trimmed.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        if (apiStart >= 0 || trimmed.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
        {
            var editionStart = apiStart >= 0 ? apiStart + "/api/".Length : "api/".Length;
            var editionEnd = trimmed.IndexOf('/', editionStart);
            if (editionEnd < 0)
            {
                return null;
            }

            given = trimmed[editionStart..editionEnd];
            if (!SrdEdition.All.Contains(given))
            {
                return null;
            }

            swapped = trimmed[..editionStart] + Other(given) + trimmed[editionEnd..];
        }
        else
        {
            var parts = trimmed.Split('/');
            if (parts.Length != 3 || !SrdEdition.All.Contains(parts[0].Trim()))
            {
                return null;
            }

            given = parts[0].Trim();
            swapped = $"{parts[1]}/{parts[2]}";
        }

        try
        {
            return (Echo(trimmed), given, SrdRefParser.Parse(swapped, Other(given), out _));
        }
        catch (DndInputException)
        {
            return null;
        }
    }

    private static bool IsAttributionRef(string text) =>
        string.Equals(text.Trim().Trim('`', '"', '\'').Trim(), AttributionRef, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <see cref="SrdIndex.Search"/>'s length and word caps, checked before waiting for the index (with its wording), so an
    /// over-long query is corrected at once, not after a first-run build or an index error.
    /// </summary>
    private static void CheckQueryBounds(string? query)
    {
        query ??= string.Empty;
        if (query.Length > SrdIndex.MaxQueryLength)
        {
            throw new DndInputException(
                $"The query is {query.Length.ToString("N0", CultureInfo.InvariantCulture)} characters long; search with at most " +
                $"{SrdIndex.MaxQueryLength} characters of key words, e.g. \"grapple escape\" or \"fire*\". To read one entry, use " +
                "rules_get with its name.");
        }

        var wordCount = Fts5Query.WordCount(query);
        if (wordCount > SrdIndex.MaxQueryWords)
        {
            throw new DndInputException(
                $"The query has {wordCount} different words; search with at most {SrdIndex.MaxQueryWords} key words, e.g. " +
                "\"grapple escape\" or \"fire*\".");
        }
    }

    /// <summary><see cref="SrdIndex.FindByName"/>'s length cap, checked before waiting for the index (with its wording).</summary>
    private static void CheckNameBounds(string name)
    {
        if (name.Length > SrdIndex.MaxNameLength)
        {
            throw new DndInputException(
                $"The name is {name.Length.ToString("N0", CultureInfo.InvariantCulture)} characters long; names are at most " +
                $"{SrdIndex.MaxNameLength} characters. Pass just the entry's name, e.g. \"Fireball\", or search for longer text " +
                "with rules_search.");
        }
    }

    // The kind in the edition being searched. For "both" the name is looked up in 2024, so a kind only 2014 has maps to its
    // 2024 name (race → species), and a kind only 2024 has (poison) is fine; for one edition a kind it lacks is refused,
    // with the edition that has it.
    private static string KindForNameLookup(string kind, string lookupEdition, bool bothEditions)
    {
        if (!bothEditions)
        {
            return SrdKindNames.Normalize(kind, lookupEdition);
        }

        var kinds = SrdKindNames.Normalize(kind, SrdEdition.All);
        return kinds.FirstOrDefault(k => SrdKinds.ExistsIn(k, lookupEdition)) ?? kinds[0];
    }

    private static IReadOnlyList<string>? KindFilter(string[]? kinds, IReadOnlyList<string> editions)
    {
        if (kinds is not { Length: > 0 })
        {
            return null;
        }

        var normalized = kinds.SelectMany(k => SrdKindNames.Normalize(k, editions)).Distinct(StringComparer.Ordinal).ToList();
        if (normalized.Contains(SrdKinds.Level))
        {
            throw new DndInputException(
                "Level records are not searchable (a Fighter 5 row would match every search). Get a class's level table with " +
                "rules_get name \"Fighter\", or one level with ref \"2014/level/fighter-5\".");
        }

        return normalized;
    }

    private static IReadOnlyList<string> SearchEditions(string? edition) =>
        GetEdition(edition) switch
        {
            null or SrdEdition.Edition2024 => [SrdEdition.Edition2024],
            SrdEdition.Edition2014 => [SrdEdition.Edition2014],
            _ => SrdEdition.All,
        };

    /// <summary>The edition argument, trimmed and lower-cased; null when omitted.</summary>
    private static string? GetEdition(string? edition)
    {
        if (string.IsNullOrWhiteSpace(edition))
        {
            return null;
        }

        var value = edition.Trim().ToLowerInvariant();
        return EditionValues.Contains(value)
            ? value
            : throw new DndInputException($"edition must be {EditionsText} (got \"{Echo(edition)}\").");
    }

    private static string Format(string? format)
    {
        var value = string.IsNullOrWhiteSpace(format) ? SrdMarkdown.Concise : format.Trim().ToLowerInvariant();
        return SrdMarkdown.Formats.Contains(value)
            ? value
            : throw new DndInputException(
                $"format must be {string.Join(" or ", SrdMarkdown.Formats.Select(f => $"\"{f}\""))} (got \"{Echo(format!)}\").");
    }

    private static string FormatSearch(
        string query,
        IReadOnlyList<string> editions,
        IReadOnlyList<string>? kinds,
        int limit,
        SrdSearchResult result)
    {
        var where = editions.Count == 1 ? $"the {editions[0]} SRD" : "the 2014 and 2024 SRDs";
        var filter = kinds is null ? string.Empty : $" (kinds: {string.Join(", ", kinds)})";
        var hits = result.Hits;

        if (hits.Count == 0)
        {
            return $"No entries match \"{query.Trim()}\" in {where}{filter}. Try {OrList(ZeroResultTips(query, editions, kinds).ToList())}.";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"**{hits.Count} {(hits.Count == 1 ? "entry" : "entries")}** for \"{query.Trim()}\" in {where}{filter}");
        text.Append(result.MatchedAllWords ? ":" : ". No entry has every word, so these match some of them:").Append("\n\n");

        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var marker = string.Create(CultureInfo.InvariantCulture, $"{i + 1}. ");
            text.Append(marker).Append(CultureInfo.InvariantCulture, $"**{hit.Name}** — {hit.Kind} · {hit.Edition} · `{hit.Ref}`\n");
            if (hit.Snippet.Length > 0)
            {
                // Indented to the marker's width, so markdown keeps the snippet inside its item ("10. " needs four spaces).
                text.Append(' ', marker.Length).Append(hit.Snippet).Append('\n');
            }
        }

        text.Append('\n');
        if (hits.Count == limit)
        {
            text.Append(CultureInfo.InvariantCulture, $"Showing the first {limit}; there may be more (limit goes up to {SrdIndex.MaxSearchLimit}). ");
        }

        return text.Append("Pass a ref to rules_get for the whole entry.").ToString();
    }

    private static IEnumerable<string> ZeroResultTips(string query, IReadOnlyList<string> editions, IReadOnlyList<string>? kinds)
    {
        if (editions.Count == 1)
        {
            yield return $"edition \"{Both}\" (the {Other(editions[0])} SRD may have it)";
        }

        if (kinds is not null)
        {
            yield return "no kinds filter";
        }

        if (Fts5Query.WordCount(query) > 1)
        {
            yield return "fewer words";
        }

        var firstWord = new string(query.Trim().TakeWhile(char.IsLetterOrDigit).ToArray());
        yield return firstWord.Length > 4 && !query.Contains('*')
            ? $"a prefix such as {firstWord[..4].ToLowerInvariant()}*"
            : "other words for the same thing";
    }

    // "a", "a or b", "a, b or c".
    private static string OrList(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " or " + items[^1];

    /// <summary>
    /// "Also named …" for the other entries a name matched. Offers "add kind" only when it would narrow the list: not when
    /// the caller already gave a kind, and not when every other match is the best match's kind (Ability Score Improvement
    /// is 63 features). Features, where same-name duplicates are common, carry their class and level so the model can
    /// pick the one it means.
    /// </summary>
    private static string? AlsoNamed(string name, SrdDocument best, IReadOnlyList<SrdDocument> others, bool kindGiven)
    {
        if (others.Count == 0)
        {
            return null;
        }

        var shown = string.Join(", ", others.Take(MaxOtherMatches).Select(d => $"`{d.Ref}`{FeatureWhere(d)}"));
        var more = others.Count > MaxOtherMatches ? $" and {others.Count - MaxOtherMatches} more" : string.Empty;
        var addKind = !kindGiven && others.Any(d => d.Kind != best.Kind) ? ", or add kind," : string.Empty;
        return $"Also named \"{Echo(name)}\": {shown}{more}. Pass one as ref{addKind} to get it instead.";
    }

    // " (Paladin 3)" for a feature: 2024 records name their level ("Paladin 3"); 2014 ones give the class or subclass and
    // the level number.
    private static string FeatureWhere(SrdDocument doc)
    {
        if (doc.Kind != SrdKinds.Feature)
        {
            return string.Empty;
        }

        var root = doc.Root;
        if (root.Obj("level") is { } level)
        {
            return level.Str("name") is { } levelName ? $" ({levelName})" : string.Empty;
        }

        var owner = root.Obj("subclass")?.Str("name") ?? root.Obj("class")?.Str("name");
        return owner is not null && root.Int("level") is { } number
            ? string.Create(CultureInfo.InvariantCulture, $" ({owner} {number})")
            : string.Empty;
    }

    /// <summary>The notes that apply, as one trailer for SrdMarkdown to keep whole under its cap; null when none do.</summary>
    private static string? Notes(params string?[] notes)
    {
        var present = notes.Where(n => n is not null).ToList();
        return present.Count == 0 ? null : string.Join("\n\n", present);
    }

    private static string Link(SrdDocument doc) => $"{doc.Name} (`{doc.Ref}`)";

    private static string LinkList(IEnumerable<SrdDocument> docs, string separator = ", ") => string.Join(separator, docs.Select(Link));

    private static string RefList(IReadOnlyList<SrdDocument> docs)
    {
        var shown = string.Join(", ", docs.Take(MaxOtherMatches).Select(d => $"`{d.Ref}`"));
        return docs.Count > MaxOtherMatches ? $"{shown} and {docs.Count - MaxOtherMatches} more" : shown;
    }

    // "a", "a and b", "a, b and c".
    private static string AndList(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    // The notes that apply, one per line; null when none do.
    private static string? Lines(params string?[] notes)
    {
        var present = notes.Where(n => n is not null).ToList();
        return present.Count == 0 ? null : string.Join("\n", present);
    }

    /// <summary>
    /// Input as echoed back in a message, cut to <see cref="MaxEchoLength"/> characters: a pasted page as a "name" must not
    /// come back doubled in an error the size of the page.
    /// </summary>
    private static string Echo(string text) => text.Length <= MaxEchoLength ? text : text[..MaxEchoLength] + "…";

    private static string KindIn(string edition, string kind) =>
        edition == SrdEdition.Edition2024 ? SrdCounterparts.KindIn2024(kind) : SrdCounterparts.KindIn2014(kind);

    private static string Other(string edition) =>
        edition == SrdEdition.Edition2014 ? SrdEdition.Edition2024 : SrdEdition.Edition2014;

    // "#### Grappling" at the start of a line: the marks and the heading text.
    [GeneratedRegex(@"^(?<marks>#{2,6})[ \t]+(?<text>.+?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeading();

    // "***Serpent Venom (Injury).*** A creature…" at the start of a line: the run-in heading's text, without its period.
    [GeneratedRegex(@"^\*\*\*(?<text>[^*\n]+?)\.?\*\*\*", RegexOptions.Multiline)]
    private static partial Regex RunInHeading();

    // " (Injury)" at the end of a run-in heading.
    [GeneratedRegex(@"\s*\([^()]*\)$")]
    private static partial Regex TrailingParenthetical();

    // "fighter-5", "berserker-14": the class or subclass slug and the level.
    [GeneratedRegex(@"^(?<owner>.+)-(?<level>\d+)$")]
    private static partial Regex LevelSlug();

    /// <summary>How the other edition's side of a comparison was found; only a name match is a guess worth flagging.</summary>
    private enum MatchedBy
    {
        Counterpart,
        Name,
    }

    /// <summary>The other edition's entry to compare against, the further candidates, and how they were found.</summary>
    private sealed record OtherSideMatch(SrdDocument Best, IReadOnlyList<SrdDocument> Others, MatchedBy MatchedBy);

    /// <summary>
    /// A ref naming a kind its edition lacks, with the ref it would be in the edition that has the kind. Never reaches the
    /// model: <see cref="GetByRefAsync"/> turns it into a <see cref="DndInputException"/> once the ref is looked up.
    /// </summary>
    private sealed class RefKindInOtherEditionException(string given, string givenEdition, SrdRef moved, Exception inner)
        : Exception(inner.Message, inner)
    {
        public string Given { get; } = given;

        public string GivenEdition { get; } = givenEdition;

        public SrdRef Ref { get; } = moved;
    }
}
