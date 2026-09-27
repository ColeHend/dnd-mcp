using System.Security.Cryptography;
using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins <c>content/rules-glossary-2024.json</c>, the only source of 2024 rules text (5e-database has none): its exact
/// bytes, its size, and the properties the index relies on to turn it into <c>2024/rule/…</c> documents.
///
/// <para>
/// The file sits outside the 5e-database manifest, so nothing else pins it. Its sha256 is part of srd.db's staleness
/// key, so an edit already forces a rebuild; this test makes the edit a deliberate, reviewed change as well (update the
/// hash here and in <c>content/rules-glossary-2024.md</c>), the same way re-vendoring 5e-database is. Slugs come from
/// names (<see cref="SrdSlug.FromName"/>), so two names that slug alike would make one entry unreachable; the index
/// build would refuse such a file, and this says why before it gets that far.
/// </para>
/// </summary>
public sealed class RulesGlossaryTests
{
    public const string PinnedSha256 = "f64a894fd3ab99d0e3e5600a72dcbf43c30eb32716cc3b7a7e8645adbb0f776a";

    private static readonly string GlossaryPath =
        Path.Combine(AppContext.BaseDirectory, "content", SrdKinds.RulesGlossary2024FileName);

    [Fact]
    public void File_Bytes_MatchThePinnedSha256()
    {
        Assert.Equal(PinnedSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(GlossaryPath))));
    }

    [Fact]
    public void File_IsAnArrayOf174Objects()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(GlossaryPath));

        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(174, document.RootElement.GetArrayLength());
        Assert.All(document.RootElement.EnumerateArray(), record => Assert.Equal(JsonValueKind.Object, record.ValueKind));
    }

    [Fact]
    public void Records_EachHaveANonEmptyNameAndDescription()
    {
        foreach (var record in Records())
        {
            Assert.Equal(JsonValueKind.String, record.GetProperty("name").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("name").GetString()));
            Assert.Equal(JsonValueKind.String, record.GetProperty("description").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("description").GetString()), record.GetProperty("name").GetString());
        }
    }

    [Fact]
    public void Records_NameSlugs_AreUniqueAndNonEmpty()
    {
        var slugs = Records().Select(r => SrdSlug.FromName(r.GetProperty("name").GetString()!)).ToList();

        Assert.All(slugs, slug => Assert.NotEmpty(slug));
        Assert.Equal(174, slugs.Distinct(StringComparer.Ordinal).Count());
    }

    // The formatter links "related" names to 2024/rule/{slug}; every one resolves today, so a dangling one is new.
    [Fact]
    public void Records_EveryRelatedName_IsAnotherGlossaryEntry()
    {
        var slugs = Records().Select(r => SrdSlug.FromName(r.GetProperty("name").GetString()!)).ToHashSet(StringComparer.Ordinal);
        var related = Records()
            .Where(r => r.TryGetProperty("related", out _))
            .SelectMany(r => r.GetProperty("related").EnumerateArray().Select(name => name.GetString()!))
            .ToList();

        Assert.Equal(78, related.Count);
        Assert.All(related, name => Assert.Contains(SrdSlug.FromName(name), slugs));
    }

    // These 18 are the names the index gives a bare-name alias ("Finesse"), so the count is part of what search sees.
    [Fact]
    public void Records_QualifiedNames_AreTheWeaponAndMasteryProperties()
    {
        var qualified = Records().Select(r => r.GetProperty("name").GetString()!).Where(n => n.EndsWith(')')).ToList();

        Assert.Equal(18, qualified.Count);
        Assert.All(qualified, name => Assert.True(
            name.EndsWith(" (Weapon Property)", StringComparison.Ordinal) || name.EndsWith(" (Mastery Property)", StringComparison.Ordinal), name));
    }

    private static List<JsonElement> Records()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(GlossaryPath));
        return document.RootElement.EnumerateArray().Select(r => r.Clone()).ToList();
    }
}
