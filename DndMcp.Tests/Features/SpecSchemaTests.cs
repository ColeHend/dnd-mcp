using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: the spec classes are a schema the model reads and a type the host binds straight into — every property is
/// described, init-only, optional (nullable, so "not given" survives binding), step values are untyped (so a scalar or a
/// step map both bind), and the whole tree exports as a snake_case JSON schema with the DSL's options.
/// </summary>
public sealed class SpecSchemaTests
{
    private static readonly Type[] SpecTypes =
    [
        typeof(BuildSpec), typeof(AbilitiesSpec), typeof(AttackSpec), typeof(ToHitSpec), typeof(ModifierSpec), typeof(ResourceSpec),
        typeof(TargetSpec), typeof(SavesSpec), typeof(RulingsSpec), typeof(FeatureSpec),
    ];

    /// <summary>Descriptions are per-property help, not documentation: a long one crowds the tool's schema.</summary>
    private const int MaxDescriptionLength = 320;

    public static TheoryData<string, PropertyInfo> AllProperties()
    {
        var data = new TheoryData<string, PropertyInfo>();
        foreach (var property in SpecTypes.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            data.Add($"{property.DeclaringType!.Name}.{property.Name}", property);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllProperties))]
    public void EveryProperty_HasAShortSentenceDescription(string name, PropertyInfo property)
    {
        var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;

        Assert.False(string.IsNullOrWhiteSpace(description), name);
        Assert.True(description!.Length <= MaxDescriptionLength, $"{name}: {description.Length} characters");
        Assert.EndsWith(".", description, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", description, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllProperties))]
    public void EveryProperty_IsPublicInitOnly(string name, PropertyInfo property)
    {
        var setter = property.SetMethod;

        Assert.NotNull(setter);
        Assert.True(setter!.IsPublic, name);
        Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
    }

    [Theory]
    [MemberData(nameof(AllProperties))]
    public void EveryValueTypeProperty_IsNullableSoTheResolverAppliesDefaults(string name, PropertyInfo property)
    {
        if (property.DeclaringType == typeof(RulingsSpec))
        {
            // Rulings are plain flags: false is both the default and the rules as written.
            Assert.Equal(typeof(bool), property.PropertyType);
            return;
        }

        Assert.True(!property.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) is not null, name);
    }

    [Fact]
    public void StepValueProperties_AreUntypedSoScalarsAndStepMapsBothBind()
    {
        var untyped = SpecTypes
            .SelectMany(t => t.GetProperties().Where(p => p.PropertyType == typeof(object)).Select(p => $"{t.Name}.{p.Name}"))
            .Order()
            .ToList();

        Assert.Equal(
            [
                "AbilitiesSpec.Cha", "AbilitiesSpec.Con", "AbilitiesSpec.Dex", "AbilitiesSpec.Int", "AbilitiesSpec.Str", "AbilitiesSpec.Wis",
                "AttackSpec.Count", "AttackSpec.Damage", "ModifierSpec.Amount", "ModifierSpec.Count", "ModifierSpec.Dice", "ModifierSpec.Min",
                "ResourceSpec.Uses", "TargetSpec.Cr",
            ],
            untyped);
    }

    [Theory]
    [InlineData(typeof(BuildSpec), "fighting_style", "proficiency_bonus", "attacks", "modifiers")]
    [InlineData(typeof(AttackSpec), "to_hit", "ability_to_damage", "damage_type", "from_level", "until_level")]
    [InlineData(typeof(ModifierSpec), "trigger_probability", "use_value", "attack_action_only", "dc_ability", "on_success")]
    [InlineData(typeof(TargetSpec), "save_bonus", "magic_resistance", "legendary_resistance", "second_target_rate", "save_dice")]
    [InlineData(typeof(RulingsSpec), "hew_gets_pb", "cleave_part_of_attack_action", "gwf_on_riders", "savage_attacker_on_crit_dice")]
    [InlineData(typeof(FeatureSpec), "name", "attacks", "modifiers", "abilities", "fighting_style")]
    public void Schema_ExportsWithSnakeCaseNames(Type type, params string[] names)
    {
        var schema = DslJson.Options.GetJsonSchemaAsNode(type);

        var properties = Assert.IsType<JsonObject>(schema["properties"]);
        Assert.All(names, n => Assert.True(properties.ContainsKey(n), n));
    }

    [Fact]
    public void Descriptions_TotalStaysWithinATightBudget()
    {
        // The balance tools repeat these classes (a build, a feature, a target): keep the schema the model reads compact.
        var total = SpecTypes.SelectMany(t => t.GetProperties()).Sum(p => p.GetCustomAttribute<DescriptionAttribute>()!.Description.Length);

        Assert.True(total <= 12_000, $"{total} characters of descriptions");
    }
}
