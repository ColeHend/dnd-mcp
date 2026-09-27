using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DndMcp.Repository.Srd.Index;

/// <summary>
/// One SRD record as the rules index stores it: which edition and kind, its slug and display name, and the record's
/// original JSON, unchanged. Formatters read <see cref="Root"/>; nothing upstream of them reshapes the data, so what the
/// model is shown can always be traced back to the vendored bytes.
///
/// <para>
/// <see cref="Json"/> is the vendored record re-serialized compactly, not a projection. For 2024 rules it is the Rules
/// Glossary record. For 2014 level records, which have no <c>name</c>, <see cref="Name"/> is derived ("Fighter 5",
/// "Champion 3") and the JSON is left as it is.
/// </para>
/// </summary>
public sealed class SrdDocument
{
    // A reference type, published once: a JsonElement? field is a multi-word struct, and two threads racing to fill it
    // can tear it, so a shared document (the index hands the same one to parallel tool calls) reads back as Undefined.
    private StrongBox<JsonElement>? _root;

    public required string Edition { get; init; }

    public required string Kind { get; init; }

    /// <summary>Unique within (edition, kind). Upstream's <c>index</c>; for the 2024 glossary, <see cref="SrdSlug.FromName"/>.</summary>
    public required string Slug { get; init; }

    public required string Name { get; init; }

    public required string Json { get; init; }

    /// <summary>
    /// Other names this document answers to in name lookups and search, each with where it came from (see
    /// <see cref="SrdAliasSources"/>). The source matters to whoever shows them: "Thug" is the 2014 name of 2024
    /// <c>tough</c>, not another 2024 name for it, and a reader told "also known as Thug" would think the 2024 SRD says so.
    /// </summary>
    public IReadOnlyList<SrdAlias> Aliases { get; init; } = [];

    /// <summary>
    /// Why <see cref="Json"/> differs from the vendored record: one reason per curated correction applied from
    /// <c>content/srd-corrections.json</c> (upstream text spliced from another entry, a flattened table, a category list
    /// that contradicts its items). Empty for the vast majority of documents. Shown with the document, so corrected text
    /// never passes as the untouched upstream record.
    /// </summary>
    public IReadOnlyList<string> Corrections { get; init; } = [];

    /// <summary>
    /// The class level a feature is gained at (2014 <c>level</c>; 2024 the number of its <c>level</c> reference), or a
    /// level record's level; null for every other kind. Graded 2014 features share a bare name ("Bardic Inspiration" is
    /// the d6 at level 1 and the d10 at level 10), and name lookups and counterparts put the lower level first.
    /// </summary>
    public int? Level { get; init; }

    public SrdRef Ref => new(Edition, Kind, Slug);

    /// <summary>
    /// True when <paramref name="name"/> is this document's own name or one of its <see cref="Aliases"/>, compared by
    /// <see cref="SrdNames.Key"/>: what edition "both" asks of each counterpart to find the one the caller typed
    /// ("Swarm of Wasps" among 2024 Swarm of Insects' five 2014 counterparts).
    /// </summary>
    public bool AnswersTo(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var key = SrdNames.Key(name);
        return key.Length > 0 &&
               (SrdNames.Key(Name) == key || Aliases.Any(a => SrdNames.Key(a.Name) == key));
    }

    /// <summary>The parsed <see cref="Json"/>. Parsed once, on first use, and detached from any JsonDocument.</summary>
    public JsonElement Root
    {
        get
        {
            var box = Volatile.Read(ref _root);
            if (box is null)
            {
                using var document = JsonDocument.Parse(Json);
                box = new StrongBox<JsonElement>(document.RootElement.Clone());
                box = Interlocked.CompareExchange(ref _root, box, null) ?? box;
            }

            return box.Value;
        }
    }

    public override string ToString() => Ref.ToString();
}

/// <summary>A name a document answers to besides its own, and why (<see cref="SrdAliasSources"/>).</summary>
public sealed record SrdAlias(string Name, string Source);

/// <summary>
/// Where an alias comes from. Stored in srd.db's alias table, so these are wire strings, not an enum.
/// </summary>
public static class SrdAliasSources
{
    /// <summary>A glossary name without its qualifier: "Finesse" for "Finesse (Weapon Property)". Same edition, same thing.</summary>
    public const string Qualifier = "qualifier";

    /// <summary>The other edition's name for the same entry: 2024 <c>tough</c> answers to "Thug".</summary>
    public const string Counterpart = "counterpart";

    /// <summary>
    /// A <c>####</c>/<c>#####</c> subsection heading of a 2014 rule, word for word: 2014 <c>melee-attacks</c> answers to
    /// "Grappling", because the 2014 SRD has no separate grappling entry and a name lookup must still land on the rules
    /// that cover it. Only headings that name a rule a player looks up by that name are aliases
    /// (<see cref="SrdCuratedAliases.Headings"/>); a caller may say "covered by this entry's #### Grappling subsection".
    /// </summary>
    public const string Heading = "heading";

    /// <summary>
    /// The name of a 2024 entry that this 2014 rules section covers without a heading of that name
    /// (<see cref="SrdCounterparts.Sections"/>): Melee Attacks answers to "Unarmed Strike" (one sentence of its text),
    /// Special Types of Movement to "Climbing" (its heading is "Climbing, Swimming, and Crawling"), Damage Rolls to
    /// "Critical Hit" (its heading is the plural). A caller may say the entry covers it, never that a subsection of
    /// that name exists.
    /// </summary>
    public const string Section = "section";

    /// <summary>
    /// An everyday name no record carries, curated with evidence (<see cref="SrdCuratedAliases.Names"/>): 2024
    /// <c>unarmed-strike</c> answers to "Shove", 2024 <c>resistance</c> to "Damage Resistance". Ranked like the other
    /// edition's name: it never beats a document that owns the name.
    /// </summary>
    public const string Curated = "curated";
}

/// <summary>
/// What a formatter may ask the index while rendering one document: the records a document links to but does not
/// embed. A class's level table lives in level records; a species' traits are trait records; a 2014 subclass's
/// features are feature records reached through its level records.
///
/// <para>
/// Deliberately small and synchronous so formatters stay pure functions of (document, lookup) and can be tested
/// against the vendored files without building srd.db.
/// </para>
/// </summary>
public interface ISrdLookup
{
    /// <summary>The document with exactly this (edition, kind, slug), or null.</summary>
    SrdDocument? Get(string edition, string kind, string slug);

    /// <summary>A class's own level records (never subclass levels), ordered by level. Empty when there are none.</summary>
    IReadOnlyList<SrdDocument> ClassLevels(string edition, string classSlug);

    /// <summary>A subclass's level records, ordered by level. Empty when there are none.</summary>
    IReadOnlyList<SrdDocument> SubclassLevels(string edition, string subclassSlug);
}
