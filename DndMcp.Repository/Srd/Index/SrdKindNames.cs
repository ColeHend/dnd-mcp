using System.Globalization;
using DndMcp.Domain.Core;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// Turns the kind a person or the model typed into a wire kind (<see cref="SrdKinds"/>) that exists in the edition
/// being asked about, or explains, in a <see cref="DndInputException"/>, what would have been accepted.
///
/// <para>
/// Accepted: the wire names ("magic-item"), plurals ("spells", "proficiencies"), upstream's API resource names
/// ("weapon-mastery-properties"), spaces or underscores for hyphens ("magic item"), <c>glossary</c> for
/// <c>rule</c>, and <c>mastery</c> / <c>weapon-mastery-property</c> for <c>weapon-mastery</c>. <c>race</c> and
/// <c>species</c> (and <c>subrace</c> / <c>subspecies</c>) name the same thing in different editions, so each maps to
/// whichever the edition has: "race" in 2024 means species, because refusing it would only make the model retry.
/// </para>
/// <para>
/// A kind that exists only in the other edition (poison in 2014) is refused with the edition that has it, since the
/// fix is to change the edition, not the kind. An unknown kind is refused with the full list for the edition.
/// </para>
/// </summary>
public static class SrdKindNames
{
    /// <summary>
    /// Kind order for name lookups and for the exact-name tier of search when several documents share a name within one
    /// name-match tier (<see cref="SrdNameMatch.Tier"/>): the likelier thing a person means comes first. Conditions and
    /// spells beat everything ("Prone", "Shield"); then classes and subclasses, which players ask about far more often
    /// than the NPC stat blocks sharing their names ("Druid"); then creatures and the other character options; and the
    /// vocabulary kinds (skills, damage types, languages…) whose names collide with everything else come last. Every kind
    /// is listed; a kind missing here would sort after all of them.
    /// </summary>
    public static IReadOnlyList<string> LookupPriority { get; } =
    [
        SrdKinds.Condition, SrdKinds.Spell, SrdKinds.Class, SrdKinds.Subclass, SrdKinds.Monster, SrdKinds.Species,
        SrdKinds.Race, SrdKinds.Subspecies, SrdKinds.Subrace, SrdKinds.Feat, SrdKinds.Background, SrdKinds.MagicItem,
        SrdKinds.Equipment, SrdKinds.WeaponMastery, SrdKinds.WeaponProperty, SrdKinds.Poison, SrdKinds.Rule,
        SrdKinds.Feature, SrdKinds.Trait, SrdKinds.Skill, SrdKinds.AbilityScore, SrdKinds.DamageType,
        SrdKinds.MagicSchool, SrdKinds.Alignment, SrdKinds.Language, SrdKinds.Proficiency, SrdKinds.EquipmentCategory,
        SrdKinds.Level,
    ];

    /// <summary>
    /// The vocabulary kinds: short reference records (a language, a skill, a damage type, a category list, a level row)
    /// whose names collide with the entries people actually ask for. In name lookups an alias of a content kind outranks
    /// a reference kind's own name (2024 "Goblin" is the monster Goblin Warrior, by its 2014 name, before the Goblin
    /// language), while a content kind's own name outranks every alias (2024 "Acolyte" is the background, not the
    /// monster whose 2014 name it was). See <see cref="SrdNameMatch.Tier"/>.
    /// </summary>
    public static IReadOnlySet<string> ReferenceKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        SrdKinds.Language, SrdKinds.Proficiency, SrdKinds.Skill, SrdKinds.AbilityScore, SrdKinds.Alignment,
        SrdKinds.DamageType, SrdKinds.MagicSchool, SrdKinds.EquipmentCategory, SrdKinds.Level,
    };

    // The longest kind name is 26 characters (weapon-mastery-properties).
    private const int MaxQuotedLength = 40;

    private static readonly Dictionary<string, int> Priority =
        LookupPriority.Select((kind, i) => (kind, i)).ToDictionary(p => p.kind, p => p.i, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> Synonyms = BuildSynonyms();

    /// <summary>Position of <paramref name="kind"/> in <see cref="LookupPriority"/>; unknown kinds sort last.</summary>
    public static int PriorityOf(string kind) => Priority.GetValueOrDefault(kind, int.MaxValue);

    /// <summary>The wire kinds that have documents in <paramref name="edition"/>, alphabetical.</summary>
    public static IReadOnlyList<string> KindsIn(string edition) =>
        SrdKinds.All.Select(k => k.Name).Where(k => SrdKinds.ExistsIn(k, edition)).ToList();

    /// <summary>The wire kind <paramref name="input"/> names in <paramref name="edition"/>.</summary>
    /// <exception cref="DndInputException">The kind is empty, unknown, or not in this edition.</exception>
    public static string Normalize(string? input, string edition)
    {
        RequireEdition(edition);
        var kind = Resolve(input, [edition]);
        var inEdition = InEdition(kind, edition);
        if (SrdKinds.ExistsIn(inEdition, edition))
        {
            return inEdition;
        }

        var other = edition == SrdEdition.Edition2014 ? SrdEdition.Edition2024 : SrdEdition.Edition2014;
        throw new DndInputException(
            $"Kind '{kind}' is not in the {edition} SRD; only the {other} SRD has it. Pass edition {other} (or both).");
    }

    /// <summary>
    /// The wire kinds <paramref name="input"/> names across <paramref name="editions"/>: one per edition that has it
    /// ("race" over both editions is <c>race</c> and <c>species</c>), distinct. For <c>edition: both</c> searches, where
    /// a kind only one edition has (poison) is still a valid filter.
    /// </summary>
    /// <exception cref="DndInputException">The kind is empty, unknown, or in none of the editions.</exception>
    public static IReadOnlyList<string> Normalize(string? input, IReadOnlyCollection<string> editions)
    {
        ArgumentNullException.ThrowIfNull(editions);
        if (editions.Count == 0)
        {
            throw new ArgumentException("At least one edition is required.", nameof(editions));
        }

        foreach (var edition in editions)
        {
            RequireEdition(edition);
        }

        var distinct = editions.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 1)
        {
            return [Normalize(input, distinct[0])];
        }

        // Both editions: every known kind exists in at least one of them, so this is never empty.
        var kind = Resolve(input, distinct);
        return distinct
            .Select(edition => (Edition: edition, Kind: InEdition(kind, edition)))
            .Where(p => SrdKinds.ExistsIn(p.Kind, p.Edition))
            .Select(p => p.Kind)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    // The wire kind for the text, before edition mapping; unknown or empty text is refused with the kinds of the editions.
    private static string Resolve(string? input, IReadOnlyCollection<string> editions)
    {
        var canonical = Canonical(input);
        if (canonical.Length > 0 && Synonyms.TryGetValue(canonical, out var kind))
        {
            return kind;
        }

        var valid = editions.SelectMany(KindsIn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var where = editions.Count == 1 ? $"the {editions.First()} SRD" : "the SRD";
        var what = canonical.Length == 0 ? "The kind is empty." : $"Unknown kind {Quoted(input!.Trim())}.";
        throw new DndInputException($"{what} Kinds in {where}: {string.Join(", ", valid)}.");
    }

    // The unknown kind as typed, quoted, but only as far as a kind name could go: a pasted paragraph is not repeated
    // back into the error the model reads.
    private static string Quoted(string input) =>
        input.Length <= MaxQuotedLength
            ? $"'{input}'"
            : $"'{input[..MaxQuotedLength]}…' ({input.Length.ToString("N0", CultureInfo.InvariantCulture)} characters)";

    // race ⇄ species and subrace ⇄ subspecies, towards whichever the edition has.
    private static string InEdition(string kind, string edition) =>
        edition == SrdEdition.Edition2024 ? SrdCounterparts.KindIn2024(kind) : SrdCounterparts.KindIn2014(kind);

    private static string Canonical(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var chars = input.Trim().ToLowerInvariant().Select(ch => ch is ' ' or '_' ? '-' : ch).ToArray();
        var text = new string(chars);
        while (text.Contains("--", StringComparison.Ordinal))
        {
            text = text.Replace("--", "-", StringComparison.Ordinal);
        }

        return text.Trim('-');
    }

    private static Dictionary<string, string> BuildSynonyms()
    {
        // Upstream's API resource names are the plurals ("spells", "classes", "proficiencies", "species"), so every
        // kind but two gets its plural from SrdKinds.All.
        var synonyms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kind in SrdKinds.All)
        {
            synonyms[kind.Name] = kind.Name;
            if (kind.ApiResource is { } resource)
            {
                synonyms[resource] = kind.Name;
            }
        }

        synonyms["levels"] = SrdKinds.Level;
        synonyms["weapon-masteries"] = SrdKinds.WeaponMastery;
        synonyms["weapon-mastery-property"] = SrdKinds.WeaponMastery;
        synonyms["mastery"] = SrdKinds.WeaponMastery;
        synonyms["masteries"] = SrdKinds.WeaponMastery;
        synonyms["mastery-property"] = SrdKinds.WeaponMastery;
        synonyms["glossary"] = SrdKinds.Rule;
        synonyms["rules-glossary"] = SrdKinds.Rule;
        return synonyms;
    }

    private static void RequireEdition(string edition)
    {
        if (!SrdEdition.All.Contains(edition))
        {
            throw new ArgumentException($"edition must be one of {string.Join(", ", SrdEdition.All)} (got \"{edition}\").", nameof(edition));
        }
    }
}
