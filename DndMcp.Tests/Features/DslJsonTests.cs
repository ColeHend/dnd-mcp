using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: DSL JSON binds in Domain exactly as the host binds it (snake_case names, step values as JSON elements,
/// numbers readable from strings), refuses unknown fields rather than ignoring them, and writes back compactly in the same
/// shape, so a stored build means what it meant when it was written.
/// </summary>
public sealed class DslJsonTests
{
    [Theory]
    [InlineData(EmberEdgeJson)]
    [InlineData(Fighter2014GwmJson)]
    [InlineData(Fighter2024GwmJson)]
    [InlineData(PaladinSmiteJson)]
    [InlineData(FireballJson)]
    public void Deserialize_ContractExampleBuilds_BindAndValidate(string json)
    {
        var spec = Build(json);

        Assert.False(string.IsNullOrWhiteSpace(spec.Name));
        Assert.Equal(5, spec.Level);
        BuildResolver.Validate(spec, [1, 5, 11, 20]);
    }

    [Fact]
    public void Deserialize_StepValues_BindAsJsonElements()
    {
        var spec = Build(Fighter2014GwmJson);

        var count = Assert.IsType<JsonElement>(spec.Attacks![0].Count);
        Assert.Equal(JsonValueKind.Object, count.ValueKind);
        Assert.Equal(JsonValueKind.String, Assert.IsType<JsonElement>(spec.Attacks[0].Damage).ValueKind);
        Assert.Equal(JsonValueKind.Number, Assert.IsType<JsonElement>(spec.Abilities!.Str).ValueKind);
        Assert.Null(spec.Abilities.Dex);
    }

    [Fact]
    public void Deserialize_SnakeCaseNamesAndNumericStrings_BindLikeTheHost()
    {
        var spec = Build("""
            { "name": "X", "level": "5", "proficiency_bonus": "3", "fighting_style": "gwf",
              "attacks": [{ "name": "A", "damage": "1d8", "ability_to_damage": false, "damage_type": "fire",
                            "to_hit": { "ability": "dex", "total": "7" }, "from_level": "1" }],
              "modifiers": [{ "kind": "extra_attack", "attack": "A", "action": "reaction", "trigger_probability": "0.25" }] }
            """);

        Assert.Equal(5, spec.Level);
        Assert.Equal(3, spec.ProficiencyBonus);
        Assert.Equal(7, spec.Attacks![0].ToHit!.Total);
        Assert.False(spec.Attacks[0].AbilityToDamage);
        Assert.Equal(0.25, spec.Modifiers![0].TriggerProbability);
    }

    [Fact]
    public void Deserialize_UnknownField_IsRefusedWithWhere()
    {
        var ex = Assert.Throws<DndInputException>(() => Build("""{ "name": "X", "level": 5, "attacks": [{ "name": "A", "damage": "1d8", "cuont": 2 }] }"""));

        Assert.StartsWith("build: could not be read at '$.attacks[0]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cuont", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown fields are refused", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "name": "X", "level": "five" }""")]
    [InlineData("""{ "name": "X", "attacks": {} }""")]
    [InlineData("""{ "name": "X", """)]
    [InlineData("""[1, 2]""")]
    public void Deserialize_Malformed_IsAnInputError(string json)
    {
        var ex = Assert.Throws<DndInputException>(() => Build(json));

        Assert.StartsWith("build: could not be read", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("LineNumber", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_Null_IsAnInputError()
    {
        var ex = Assert.Throws<DndInputException>(() => Build("null"));

        Assert.Equal("build: is null; give a JSON object.", ex.Message);
    }

    [Fact]
    public void Serialize_RoundTrip_IsSnakeCaseCompactAndResolvesTheSame()
    {
        var spec = Build(Fighter2024GwmJson);

        var json = JsonSerializer.Serialize(spec, DslJson.Options);
        var again = Build(json);

        Assert.Contains("\"attack_action_only\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"count\":{\"1\":1,\"5\":2}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"proficiency_bonus\"", json, StringComparison.Ordinal);
        var before = BuildResolver.Resolve(spec, 5);
        var after = BuildResolver.Resolve(again, 5);
        Assert.Equal(before.Attacks[0].DamageParts, after.Attacks[0].DamageParts);
        Assert.Equal(before.Attacks[0].ToHitParts, after.Attacks[0].ToHitParts);
        Assert.Equal(JsonSerializer.Serialize(again, DslJson.Options), json);
    }

    [Fact]
    public void Options_KeepApostrophesAndNonAsciiUnescaped()
    {
        var json = JsonSerializer.Serialize(new ModifierSpec { Kind = "extra_damage", Name = "Hunter's Mark — ½" }, DslJson.Options);

        Assert.Contains("Hunter's Mark — ½", json, StringComparison.Ordinal);
        Assert.True(DslJson.Options.IsReadOnly);
    }
}
