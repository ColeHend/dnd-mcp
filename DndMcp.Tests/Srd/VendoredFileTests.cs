using System.Text.Json;
using DndMcp.Repository.Srd;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins the shape and size of every vendored file. Each is a JSON array of objects with exactly the element count
/// recorded below, and every record reads as an <see cref="SrdEntry"/> whose URL names its own edition.
///
/// <para>
/// The counts are the Phase 2 import-count oracle, and they are how an upstream data change becomes visible. The
/// 2024 data is weeks old and still changing, so a re-vendor that adds or drops records fails here and has to be
/// accepted deliberately by editing this table. Counts were taken from the data and cross-checked against the
/// dnd5eapi research (§2 live list counts). All 47 files the research covers agree; it has no figure for Levels.
/// </para>
/// </summary>
public sealed class VendoredFileTests
{
    public static TheoryData<string, string, int> PinnedCounts => new()
    {
        { SrdEdition.Edition2014, "5e-SRD-Ability-Scores.json", 6 },
        { SrdEdition.Edition2014, "5e-SRD-Alignments.json", 9 },
        { SrdEdition.Edition2014, "5e-SRD-Backgrounds.json", 1 },
        { SrdEdition.Edition2014, "5e-SRD-Classes.json", 12 },
        { SrdEdition.Edition2014, "5e-SRD-Conditions.json", 15 },
        { SrdEdition.Edition2014, "5e-SRD-Damage-Types.json", 13 },
        { SrdEdition.Edition2014, "5e-SRD-Equipment-Categories.json", 39 },
        { SrdEdition.Edition2014, "5e-SRD-Equipment.json", 237 },
        { SrdEdition.Edition2014, "5e-SRD-Feats.json", 1 },
        { SrdEdition.Edition2014, "5e-SRD-Features.json", 407 },
        { SrdEdition.Edition2014, "5e-SRD-Languages.json", 16 },
        { SrdEdition.Edition2014, "5e-SRD-Levels.json", 290 },            // not in research §2
        { SrdEdition.Edition2014, "5e-SRD-Magic-Items.json", 362 },
        { SrdEdition.Edition2014, "5e-SRD-Magic-Schools.json", 8 },
        { SrdEdition.Edition2014, "5e-SRD-Monsters.json", 334 },
        { SrdEdition.Edition2014, "5e-SRD-Proficiencies.json", 117 },
        { SrdEdition.Edition2014, "5e-SRD-Races.json", 9 },
        { SrdEdition.Edition2014, "5e-SRD-Rules.json", 137 },             // v7 heading tree (rule-sections merged in)
        { SrdEdition.Edition2014, "5e-SRD-Skills.json", 18 },
        { SrdEdition.Edition2014, "5e-SRD-Spells.json", 319 },
        { SrdEdition.Edition2014, "5e-SRD-Subclasses.json", 12 },
        { SrdEdition.Edition2014, "5e-SRD-Subraces.json", 4 },
        { SrdEdition.Edition2014, "5e-SRD-Traits.json", 38 },
        { SrdEdition.Edition2014, "5e-SRD-Weapon-Properties.json", 11 },

        { SrdEdition.Edition2024, "5e-SRD-Ability-Scores.json", 6 },
        { SrdEdition.Edition2024, "5e-SRD-Alignments.json", 10 },
        { SrdEdition.Edition2024, "5e-SRD-Backgrounds.json", 4 },
        { SrdEdition.Edition2024, "5e-SRD-Classes.json", 12 },
        { SrdEdition.Edition2024, "5e-SRD-Conditions.json", 15 },
        { SrdEdition.Edition2024, "5e-SRD-Damage-Types.json", 13 },
        { SrdEdition.Edition2024, "5e-SRD-Equipment-Categories.json", 30 },
        { SrdEdition.Edition2024, "5e-SRD-Equipment.json", 182 },
        { SrdEdition.Edition2024, "5e-SRD-Feats.json", 17 },
        { SrdEdition.Edition2024, "5e-SRD-Features.json", 232 },
        { SrdEdition.Edition2024, "5e-SRD-Languages.json", 19 },
        { SrdEdition.Edition2024, "5e-SRD-Levels.json", 287 },            // not in research §2
        { SrdEdition.Edition2024, "5e-SRD-Magic-Items.json", 262 },
        { SrdEdition.Edition2024, "5e-SRD-Magic-Schools.json", 8 },
        { SrdEdition.Edition2024, "5e-SRD-Monsters.json", 341 },
        { SrdEdition.Edition2024, "5e-SRD-Poisons.json", 14 },
        { SrdEdition.Edition2024, "5e-SRD-Proficiencies.json", 74 },
        { SrdEdition.Edition2024, "5e-SRD-Skills.json", 18 },
        { SrdEdition.Edition2024, "5e-SRD-Species.json", 9 },
        { SrdEdition.Edition2024, "5e-SRD-Spells.json", 339 },
        { SrdEdition.Edition2024, "5e-SRD-Subclasses.json", 12 },
        { SrdEdition.Edition2024, "5e-SRD-Subspecies.json", 24 },
        { SrdEdition.Edition2024, "5e-SRD-Traits.json", 67 },
        { SrdEdition.Edition2024, "5e-SRD-Weapon-Mastery-Properties.json", 8 },
        { SrdEdition.Edition2024, "5e-SRD-Weapon-Properties.json", 10 },
    };

    [Theory]
    [MemberData(nameof(PinnedCounts))]
    public void File_ParsesAsArrayOfObjects_WithPinnedCount(string edition, string fileName, int expectedCount)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(SrdTestContent.FilePath(edition, fileName)));
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.All(root.EnumerateArray(), element => Assert.Equal(JsonValueKind.Object, element.ValueKind));
        Assert.Equal(expectedCount, root.GetArrayLength());
    }

    // Without this, a file added upstream would be vendored, pinned by sha256, and never have its count checked.
    [Fact]
    public void PinnedCounts_CoverExactlyTheManifestFiles()
    {
        var pinned = PinnedCounts
            .Select(row => $"{SrdTestContent.VersionDirectory}/{row[0]}/{row[1]}")
            .Order(StringComparer.Ordinal)
            .ToList();
        var manifest = SrdTestContent.Manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(manifest, pinned);
    }

    [Theory]
    [MemberData(nameof(PinnedCounts))]
    public void File_EveryRecord_ReadsAsSrdEntryWithUniqueIndexAndOwnEditionUrl(string edition, string fileName, int expectedCount)
    {
        var entries = SrdJson.ReadArray<SrdEntry>(SrdTestContent.FilePath(edition, fileName));

        Assert.Equal(expectedCount, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.Index).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e => Assert.StartsWith($"/api/{edition}/", e.Url, StringComparison.Ordinal));
    }

    // The description converter must not drop text: every record that has desc/description in the raw JSON has
    // non-empty paragraphs in the typed entry, and the paragraph text matches the raw value.
    [Theory]
    [MemberData(nameof(PinnedCounts))]
    public void File_DescriptionText_SurvivesTheStringOrArrayConverter(string edition, string fileName, int expectedCount)
    {
        var raw = SrdTestContent.Raw(edition, fileName);
        var entries = SrdJson.ReadArray<SrdEntry>(SrdTestContent.FilePath(edition, fileName));
        Assert.Equal(expectedCount, entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            AssertSameText(raw[i], "desc", entries[i].Desc);
            AssertSameText(raw[i], "description", entries[i].Description);
        }
    }

    // SrdJson rejects null PROPERTIES, but RespectNullableAnnotations does not reach list elements, so "classes":
    // [null] would read as a list holding null. The vendored data has no null anywhere, and this pins that: a re-vendor
    // that introduces one fails here, naming where, before any model can hand a null to the simulator.
    [Theory]
    [MemberData(nameof(PinnedCounts))]
    public void File_ContainsNoJsonNullAnywhere(string edition, string fileName, int expectedCount)
    {
        var raw = SrdTestContent.Raw(edition, fileName);
        Assert.Equal(expectedCount, raw.GetArrayLength());

        Assert.Null(FirstNullPath(raw, "$"));
    }

    // The walker above must find a null at any depth, or the vendored-data check passes without looking.
    [Theory]
    [InlineData("""null""", "$")]
    [InlineData("""[1,"a",null]""", "$[2]")]
    [InlineData("""[{"a":[1,{"b":null}]}]""", "$[0].a[1].b")]
    [InlineData("""[{"a":{"b":{"c":[[null]]}}}]""", "$[0].a.b.c[0][0]")]
    [InlineData("""[{"a":[1,{"b":false}],"c":""}]""", null)]
    public void FirstNullPath_NullAtAnyDepth_IsFoundWithItsPath(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, FirstNullPath(document.RootElement, "$"));
    }

    [Fact]
    public void SrdEntry_2014Levels_HaveNoName()
    {
        var levels = SrdJson.ReadArray<SrdEntry>(SrdTestContent.FilePath(SrdEdition.Edition2014, "5e-SRD-Levels.json"));

        Assert.All(levels, level => Assert.Null(level.Name));
        Assert.Contains(levels, level => level.Index == "fighter-5");
    }

    private static string? FirstNullPath(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return path;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (FirstNullPath(property.Value, $"{path}.{property.Name}") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (FirstNullPath(item, $"{path}[{index++}]") is { } found)
                    {
                        return found;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    private static void AssertSameText(JsonElement record, string property, IReadOnlyList<string>? paragraphs)
    {
        if (!record.TryGetProperty(property, out var value))
        {
            Assert.Null(paragraphs);
            return;
        }

        var expected = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(p => p.GetString()!).ToList()
            : [value.GetString()!];

        Assert.NotNull(paragraphs);
        Assert.Equal(expected, paragraphs);
    }
}
