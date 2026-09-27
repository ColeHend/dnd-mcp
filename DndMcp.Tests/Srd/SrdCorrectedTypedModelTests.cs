using System.Text.Json;
using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Index;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Invariant: every corrected monster and spell still reads STRICTLY into its typed model, and the typed model then
/// holds the corrected values.
///
/// <para>
/// Why: the corrections overlay is applied when srd.db is built, but Phase 5's normalizer and simulator are planned
/// around the typed models (<see cref="Monster2024"/>, <see cref="Spell2014"/> …). A correction that adds a property the
/// models do not map, or gives an existing one a shape they cannot read, would pass every srd.db test and then fail — or,
/// read leniently, silently drop the fix — the first time a combatant is built from it. And a normalizer that reads the
/// vendored files directly would simulate the uncorrected creature: the 2024 Mule with the Octopus's tentacles.
/// </para>
/// </summary>
public sealed class SrdCorrectedTypedModelTests
{
    private static readonly string ContentRoot = Path.GetDirectoryName(SrdTestContent.ContentRoot)!;

    private static readonly Lazy<SrdCorrections> Corrections = new(() => SrdCorrections.Load(ContentRoot));

    public static TheoryData<string> CorrectedMonstersAndSpells()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Corrections.Value.Entries.Where(e => e.Target.Kind is SrdKinds.Monster or SrdKinds.Spell))
        {
            data.Add(entry.Ref);
        }

        return data;
    }

    [Fact]
    public void CorrectedMonstersAndSpells_Exist()
    {
        // Guards the theory below against passing vacuously if the overlay stopped touching these kinds.
        Assert.Contains(Corrections.Value.Entries, e => e.Target.Kind == SrdKinds.Monster);
        Assert.Contains(Corrections.Value.Entries, e => e.Target.Kind == SrdKinds.Spell);
    }

    [Theory]
    [MemberData(nameof(CorrectedMonstersAndSpells))]
    public void Apply_CorrectedRecord_ReadsStrictlyIntoItsTypedModelWithTheCorrectedValues(string reference)
    {
        var entry = Corrections.Value.Entries.Single(e => e.Ref == reference);
        var target = entry.Target;
        var file = SrdKinds.Find(target.Kind)!.FileFor(target.Edition)!;
        var record = SrdTestContent.Raw(target.Edition, file).EnumerateArray()
            .Single(r => r.GetProperty("index").GetString() == target.Slug);

        var corrected = Corrections.Value.Apply(target, record);
        Assert.NotNull(corrected);

        var modelType = (target.Edition, target.Kind) switch
        {
            (SrdEdition.Edition2014, SrdKinds.Monster) => typeof(Monster2014),
            (SrdEdition.Edition2024, SrdKinds.Monster) => typeof(Monster2024),
            (SrdEdition.Edition2014, SrdKinds.Spell) => typeof(Spell2014),
            _ => typeof(Spell2024),
        };

        // Strict: an unmapped property (a correction's "add" the model lacks) fails here, not in Phase 5.
        var model = JsonSerializer.Deserialize(corrected, modelType, SrdTestContent.StrictOptions);
        Assert.NotNull(model);

        // Round-tripped through the model, every corrected property still carries the corrected value.
        using var roundTripped = JsonDocument.Parse(JsonSerializer.Serialize(model, modelType, SrdTestContent.StrictOptions));
        using var expected = JsonDocument.Parse(corrected);
        foreach (var property in entry.Set.Keys.Concat(entry.Add.Keys))
        {
            Assert.True(
                roundTripped.RootElement.TryGetProperty(property, out var actual),
                $"{reference}: the typed model dropped corrected property \"{property}\".");
            Assert.True(
                JsonElement.DeepEquals(expected.RootElement.GetProperty(property), actual),
                $"{reference}: the typed model changed corrected property \"{property}\".");
        }
    }
}
