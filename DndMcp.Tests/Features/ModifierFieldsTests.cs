using System.Reflection;
using System.Text.Json;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: the per-kind field table covers every field of <see cref="ModifierSpec"/> under its wire name, so a new
/// field cannot be added without deciding which kinds take it, and a field given to a kind that does not take it is
/// always refused (never silently ignored).
/// </summary>
public sealed class ModifierFieldsTests
{
    [Fact]
    public void All_IsEveryModifierSpecPropertyUnderItsSnakeCaseName()
    {
        var properties = typeof(ModifierSpec).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Name))
            .ToList();

        Assert.Equal(properties, ModifierFields.All.Select(f => f.Name));
    }

    [Fact]
    public void ByKind_CoversEveryKindAndNamesOnlyRealFields()
    {
        Assert.Equal(V.Kinds.Set.Values.Order(), ModifierFields.ByKind.Keys.Order());
        var names = ModifierFields.All.Select(f => f.Name).ToHashSet();
        Assert.All(ModifierFields.ByKind.Values.SelectMany(f => f).Concat(ModifierFields.Common), f => Assert.Contains(f, names));
        Assert.All(ModifierFields.ByKind.Values, fields => Assert.Equal(fields.Count, fields.Distinct().Count()));
    }

    [Fact]
    public void IsGiven_SeesEveryFieldOfAFullSpecAndNoneOfAnEmptyOne()
    {
        var full = DslJson.Deserialize<ModifierSpec>("""
            { "kind": "x", "name": "x", "attacks": [], "from_level": 1, "until_level": 1, "resource": {}, "concentration": false,
              "setup": "x", "amount": 0, "dice": "x", "type": "x", "when": "x", "policy": "x", "use_value": 0, "crit_doubles": false,
              "attack_action_only": false, "action_cost": "x", "min": 0, "mode": "x", "rate": 0, "remap": "x", "attack": "x", "count": 0,
              "action": "x", "trigger": "x", "trigger_probability": 0, "penalty": 0, "bonus": 0, "ability": "x", "dc": 0, "dc_ability": "x",
              "dc_bonus": 0, "on_success": "x", "targets": 0, "shape": "x", "size": 0, "magical": false, "condition": "x", "cantrip": false }
            """, "modifier");

        Assert.All(ModifierFields.All, f => Assert.True(f.IsGiven(full), f.Name));
        Assert.All(ModifierFields.All, f => Assert.False(f.IsGiven(new ModifierSpec()), f.Name));
        Assert.All(ModifierFields.All, f => Assert.False(f.IsGiven(DslJson.Deserialize<ModifierSpec>("""{ "amount": null, "dice": null, "min": null, "count": null }""", "m")), f.Name));
    }

    [Fact]
    public void Describe_ListsTheKindsFieldsThenTheCommonOnes()
    {
        Assert.Equal("amount, dice, attacks, name, from_level, until_level, concentration, setup", ModifierFields.Describe(V.Kinds.ToHit));
        Assert.Equal("name, from_level, until_level, concentration, setup", ModifierFields.Describe(V.Kinds.Lucky)[9..]);
    }

    public static TheoryData<string, string> EveryRefusedField()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in V.Kinds.Set.Values)
        {
            foreach (var field in ModifierFields.All.Select(f => f.Name).Where(f => !ModifierFields.Takes(kind, f)))
            {
                data.Add(kind, field);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRefusedField))]
    public void Validate_FieldTheKindDoesNotTake_IsAlwaysRefused(string kind, string field)
    {
        var value = field switch
        {
            "attacks" => "[\"Greatsword\"]",
            "resource" => """{"uses": 1, "per": "long_rest"}""",
            "concentration" or "crit_doubles" or "attack_action_only" or "magical" or "cantrip" => "true",
            "amount" or "min" or "count" or "from_level" or "until_level" or "dc" or "dc_bonus" or "targets" or "size" or "penalty" or "bonus" => "2",
            "use_value" or "rate" or "trigger_probability" => "0.5",
            _ => "\"x\"",
        };

        var ex = Assert.Throws<Domain.Core.DndInputException>(() => BuildResolver.Validate(WithModifier($$"""{ "kind": "{{kind}}", "{{field}}": {{value}} }""")));

        Assert.Contains($"does not take \"{field}\"", ex.Message, StringComparison.Ordinal);
    }
}
