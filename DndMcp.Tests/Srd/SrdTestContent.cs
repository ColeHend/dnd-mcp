using System.Text.Json;
using System.Text.Json.Serialization;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Access to the REAL vendored 5e-database files, which the test project copies to <c>&lt;output&gt;/content/</c>.
/// Nothing here is a fixture or a sample. The tests pin the bytes the server will actually index.
///
/// <para>
/// <see cref="StrictOptions"/> is the production <see cref="SrdJson.Options"/> with unknown fields disallowed. Every
/// typed read in the tests goes through it, so a field the models do not cover fails a test when the data is
/// re-vendored, instead of being skipped quietly in production.
/// </para>
/// </summary>
internal static class SrdTestContent
{
    public const string PinnedTag = "5e-database-v7.0.0";

    public static string ContentRoot { get; } = Path.Combine(AppContext.BaseDirectory, "content", "5e-database");

    public static JsonSerializerOptions StrictOptions { get; } = CreateStrictOptions();

    private static readonly Lazy<ContentManifest> LazyManifest =
        new(() => ContentManifest.Load(Path.Combine(ContentRoot, ContentManifest.FileName)));

    private static readonly Lazy<IReadOnlyList<Monster2014>> LazyMonsters2014 =
        new(() => SrdJson.ReadArray<Monster2014>(FilePath(SrdEdition.Edition2014, SrdFileNames.Monsters), StrictOptions));

    private static readonly Lazy<IReadOnlyList<Monster2024>> LazyMonsters2024 =
        new(() => SrdJson.ReadArray<Monster2024>(FilePath(SrdEdition.Edition2024, SrdFileNames.Monsters), StrictOptions));

    private static readonly Lazy<IReadOnlyList<Spell2014>> LazySpells2014 =
        new(() => SrdJson.ReadArray<Spell2014>(FilePath(SrdEdition.Edition2014, SrdFileNames.Spells), StrictOptions));

    private static readonly Lazy<IReadOnlyList<Spell2024>> LazySpells2024 =
        new(() => SrdJson.ReadArray<Spell2024>(FilePath(SrdEdition.Edition2024, SrdFileNames.Spells), StrictOptions));

    private static readonly Dictionary<string, JsonElement> RawCache = new(StringComparer.Ordinal);

    public static ContentManifest Manifest => LazyManifest.Value;

    public static IReadOnlyList<Monster2014> Monsters2014 => LazyMonsters2014.Value;

    public static IReadOnlyList<Monster2024> Monsters2024 => LazyMonsters2024.Value;

    public static IReadOnlyList<Spell2014> Spells2014 => LazySpells2014.Value;

    public static IReadOnlyList<Spell2024> Spells2024 => LazySpells2024.Value;

    /// <summary>The version directory the pinned tag vendors into (<c>5e-database-v7.0.0</c> → <c>v7.0.0</c>).</summary>
    public static string VersionDirectory => PinnedTag["5e-database-".Length..];

    public static string FilePath(string edition, string fileName) =>
        Path.Combine(ContentRoot, VersionDirectory, edition, fileName);

    /// <summary>A vendored file as an untyped JSON array, for facts about the data that no model should mediate.</summary>
    public static JsonElement Raw(string edition, string fileName)
    {
        var key = edition + "/" + fileName;
        lock (RawCache)
        {
            if (!RawCache.TryGetValue(key, out var root))
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath(edition, fileName)));
                root = document.RootElement.Clone();
                RawCache[key] = root;
            }

            return root;
        }
    }

    private static JsonSerializerOptions CreateStrictOptions()
    {
        var options = new JsonSerializerOptions(SrdJson.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };

        options.MakeReadOnly();
        return options;
    }
}
