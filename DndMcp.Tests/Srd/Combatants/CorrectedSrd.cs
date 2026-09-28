using System.Text.Json;
using DndMcp.Domain.Simulation;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Combatants;
using DndMcp.Repository.Srd.Index;

namespace DndMcp.Tests.Srd.Combatants;

/// <summary>
/// The vendored monsters and spells of both editions as srd.db documents: each record through
/// <see cref="SrdCorrections.Apply"/> exactly as <c>SrdIndexBuilder</c> does, served by an in-memory
/// <see cref="ISrdLookup"/>. The normalizer's tests need every monster many times; building srd.db per test class would
/// cost seconds and prove nothing more (<c>CorrectedSrdTests</c> pins that these documents normalize exactly as the
/// index's own do).
/// </summary>
public sealed class CorrectedSrd : ISrdLookup
{
    private static readonly Lazy<CorrectedSrd> LazyShipped = new(() => new CorrectedSrd(DefaultContentRoot));

    private readonly Dictionary<(string Edition, string Kind, string Slug), SrdDocument> _documents = new();

    public CorrectedSrd(string contentRoot)
    {
        ContentRoot = contentRoot;
        var corrections = SrdCorrections.Load(contentRoot);
        var manifest = ContentManifest.Load(Path.Combine(contentRoot, "5e-database", ContentManifest.FileName));
        foreach (var edition in SrdEdition.All)
        {
            foreach (var (kind, fileName) in new[] { (SrdKinds.Monster, SrdFileNames.Monsters), (SrdKinds.Spell, SrdFileNames.Spells) })
            {
                var file = manifest.Files.Single(f => f.Path.EndsWith($"/{edition}/{fileName}", StringComparison.Ordinal));
                using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(contentRoot, "5e-database", file.Path)));
                foreach (var record in document.RootElement.EnumerateArray())
                {
                    var slug = record.GetProperty("index").GetString()!;
                    var json = corrections.Apply(new SrdRef(edition, kind, slug), record) ?? record.GetRawText();
                    _documents[(edition, kind, slug)] = new SrdDocument
                    {
                        Edition = edition,
                        Kind = kind,
                        Slug = slug,
                        Name = record.GetProperty("name").GetString()!,
                        Json = json,
                    };
                }
            }
        }

        Normalizer = new MonsterNormalizer(MonsterOverrides.Load(contentRoot), SpellOverlay.Load(contentRoot));
    }

    /// <summary>The <c>content/</c> directory the test project copies next to itself.</summary>
    public static string DefaultContentRoot => Path.Combine(AppContext.BaseDirectory, "content");

    /// <summary>The shipped content, read once per test run.</summary>
    public static CorrectedSrd Shipped => LazyShipped.Value;

    public string ContentRoot { get; }

    /// <summary>A normalizer with the shipped overrides and spell overlay.</summary>
    public MonsterNormalizer Normalizer { get; }

    /// <summary>Every monster of an edition, in data order.</summary>
    public IReadOnlyList<SrdDocument> Monsters(string edition) =>
        _documents.Where(d => d.Key.Edition == edition && d.Key.Kind == SrdKinds.Monster).Select(d => d.Value).ToList();

    /// <summary>A monster by slug.</summary>
    public SrdDocument Monster(string edition, string slug) =>
        Get(edition, SrdKinds.Monster, slug) ?? throw new ArgumentException($"No {edition} monster {slug}.");

    /// <summary>The stat block of a monster with the shipped normalizer.</summary>
    public StatBlock StatBlock(string edition, string slug) => Normalizer.Normalize(Monster(edition, slug), this);

    private readonly Dictionary<string, IReadOnlyList<StatBlock>> _all = new(StringComparer.Ordinal);

    /// <summary>Every stat block of an edition with the shipped normalizer, computed once.</summary>
    public IReadOnlyList<StatBlock> StatBlocks(string edition)
    {
        lock (_all)
        {
            if (!_all.TryGetValue(edition, out var blocks))
            {
                blocks = Monsters(edition).Select(m => Normalizer.Normalize(m, this)).ToList();
                _all[edition] = blocks;
            }

            return blocks;
        }
    }

    public SrdDocument? Get(string edition, string kind, string slug) => _documents.GetValueOrDefault((edition, kind, slug));

    public IReadOnlyList<SrdDocument> ClassLevels(string edition, string classSlug) => [];

    public IReadOnlyList<SrdDocument> SubclassLevels(string edition, string subclassSlug) => [];
}
