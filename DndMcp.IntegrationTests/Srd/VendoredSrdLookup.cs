using System.Globalization;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.IntegrationTests.Srd;

/// <summary>
/// An <see cref="ISrdLookup"/> read straight from the vendored JSON that ships in the test output's <c>content/</c>,
/// without building srd.db. It lets formatter tests render every real record of every kind while the index is a
/// separate concern with its own tests.
///
/// <para>
/// It follows the same document rules the index does (slug = upstream index, glossary slug =
/// <see cref="SrdSlug.FromName"/>, 2014 level names derived from class/subclass name and level), so a formatter that
/// renders every document here renders every document the index serves. Curated corrections
/// (<see cref="SrdCorrections"/>) are applied the same way. Aliases are left empty; they come from the index's alias
/// builder.
/// </para>
/// </summary>
internal sealed class VendoredSrdLookup : ISrdLookup
{
    // Declared before Instance: static initializers run in textual order, and the constructor reads it.
    public static string ContentRoot { get; } = Path.Combine(AppContext.BaseDirectory, "content");

    public static VendoredSrdLookup Instance { get; } = new();

    private readonly SrdCorrections _corrections;
    private readonly Dictionary<(string Edition, string Kind), List<SrdDocument>> _byKind = new();
    private readonly Dictionary<(string Edition, string Kind, string Slug), SrdDocument> _bySlug = new();

    private VendoredSrdLookup()
    {
        var manifest = ContentManifest.Load(Path.Combine(ContentRoot, "5e-database", ContentManifest.FileName));
        var version = manifest.Tag["5e-database-".Length..];
        _corrections = SrdCorrections.Load(ContentRoot);

        foreach (var edition in SrdEdition.All)
        {
            foreach (var kind in SrdKinds.All)
            {
                if (kind.FileFor(edition) is not { } file)
                {
                    continue;
                }

                var path = Path.Combine(ContentRoot, "5e-database", version, edition, file);
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var record in document.RootElement.EnumerateArray())
                {
                    var slug = record.GetProperty("index").GetString()!;
                    Add(Corrected(new SrdRef(edition, kind.Name, slug), NameOf(record, kind.Name), record));
                }
            }
        }

        using (var glossary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContentRoot, SrdKinds.RulesGlossary2024FileName))))
        {
            foreach (var record in glossary.RootElement.EnumerateArray())
            {
                var name = record.GetProperty("name").GetString()!;
                Add(Corrected(new SrdRef(SrdEdition.Edition2024, SrdKinds.Rule, SrdSlug.FromName(name)), name, record));
            }
        }
    }

    /// <summary>Every document, grouped by edition then kind, in file order.</summary>
    public IEnumerable<SrdDocument> All() => _byKind.Values.SelectMany(list => list);

    /// <summary>Every document of one kind in one edition, in file order.</summary>
    public IReadOnlyList<SrdDocument> OfKind(string edition, string kind) =>
        _byKind.TryGetValue((edition, kind), out var list) ? list : [];

    /// <summary>The document at <paramref name="reference"/> (<c>2024/spell/fireball</c>); throws when there is none.</summary>
    public SrdDocument Require(string reference)
    {
        var parts = reference.Split('/');
        return Get(parts[0], parts[1], parts[2]) ?? throw new KeyNotFoundException($"No vendored document {reference}.");
    }

    public SrdDocument? Get(string edition, string kind, string slug) =>
        _bySlug.GetValueOrDefault((edition, kind, slug));

    public IReadOnlyList<SrdDocument> ClassLevels(string edition, string classSlug) =>
        OfKind(edition, SrdKinds.Level)
            .Where(l => !l.Root.TryGetProperty("subclass", out _) &&
                        l.Root.GetProperty("class").GetProperty("index").GetString() == classSlug)
            .OrderBy(l => l.Root.GetProperty("level").GetInt32())
            .ToList();

    public IReadOnlyList<SrdDocument> SubclassLevels(string edition, string subclassSlug) =>
        OfKind(edition, SrdKinds.Level)
            .Where(l => l.Root.TryGetProperty("subclass", out var subclass) &&
                        subclass.GetProperty("index").GetString() == subclassSlug)
            .OrderBy(l => l.Root.GetProperty("level").GetInt32())
            .ToList();

    // The same overlay the index applies (content/srd-corrections.json), so formatter tests see the corrected text the
    // server serves, labelled the same way.
    private SrdDocument Corrected(SrdRef reference, string name, JsonElement record)
    {
        var corrected = _corrections.Apply(reference, record);
        return new SrdDocument
        {
            Edition = reference.Edition,
            Kind = reference.Kind,
            Slug = reference.Slug,
            Name = name,
            Json = corrected ?? record.GetRawText(),
            Corrections = corrected is null ? [] : [_corrections.For(reference)!.Reason],
        };
    }

    private void Add(SrdDocument doc)
    {
        if (!_byKind.TryGetValue((doc.Edition, doc.Kind), out var list))
        {
            _byKind[(doc.Edition, doc.Kind)] = list = [];
        }

        list.Add(doc);
        _bySlug.Add((doc.Edition, doc.Kind, doc.Slug), doc);
    }

    // 2014 level records have no name; the index names them "Fighter 5" / "Champion 3" the same way.
    private static string NameOf(JsonElement record, string kind)
    {
        if (record.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString()!;
        }

        if (kind == SrdKinds.Level)
        {
            var owner = record.TryGetProperty("subclass", out var subclass) ? subclass : record.GetProperty("class");
            return owner.GetProperty("name").GetString() + " " +
                   record.GetProperty("level").GetInt32().ToString(CultureInfo.InvariantCulture);
        }

        return record.GetProperty("index").GetString()!;
    }
}
