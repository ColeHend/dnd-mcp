using System.Reflection;
using DndMcp.Domain.Features;
using Xunit;
using V = DndMcp.Domain.Features.DslValues;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: every DSL vocabulary is a closed set of canonical strings whose forgiving match (case, spaces, hyphens,
/// underscores, a few aliases) can never turn one value into another, and the small tables beside them (cantrip tiers,
/// DMG area targets, cover, proficiency bonus, ability modifiers) are the rules' numbers.
/// </summary>
public sealed class DslValuesTests
{
    public static TheoryData<string, DslValueSet> AllSets()
    {
        var data = new TheoryData<string, DslValueSet>();
        foreach (var type in typeof(DslValues).GetNestedTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(DslValueSet)))
            {
                data.Add($"{type.Name}.{field.Name}", (DslValueSet)field.GetValue(null)!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSets))]
    public void EverySet_CanonicalValuesAreDistinctUnderTheMatchAndMatchThemselves(string name, DslValueSet set)
    {
        Assert.NotEmpty(set.Values);
        Assert.Equal(set.Values.Count, set.Values.Select(v => v.ToLowerInvariant().Replace("-", "").Replace("_", "").Replace(" ", "")).Distinct().Count());
        foreach (var value in set.Values)
        {
            Assert.True(set.TryMatch(value, out var canonical), $"{name}: {value}");
            Assert.Equal(value, canonical);
            Assert.True(set.Contains(value));
            Assert.Equal(value.Trim().ToLowerInvariant(), value);
        }
    }

    [Fact]
    public void EveryConstant_IsInItsSets()
    {
        foreach (var type in typeof(DslValues).GetNestedTypes())
        {
            var sets = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(DslValueSet))
                .Select(f => (DslValueSet)f.GetValue(null)!)
                .ToList();
            var constants = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != "Default")
                .Select(f => (string)f.GetRawConstantValue()!)
                .ToList();
            Assert.All(constants, c => Assert.Contains(c, sets.SelectMany(s => s.Values)));
        }
    }

    [Theory]
    [InlineData("Two Handed", V.Properties.TwoHanded)]
    [InlineData("TWO_HANDED", V.Properties.TwoHanded)]
    [InlineData("twohanded", V.Properties.TwoHanded)]
    [InlineData(" melee ", V.Properties.Melee)]
    public void Properties_MatchIgnoresCaseAndSeparators(string text, string expected)
    {
        Assert.True(V.Properties.Set.TryMatch(text, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("First-Hit-Per-Turn", V.When.FirstHitPerTurn)]
    [InlineData("every hit", V.When.EveryHit)]
    public void When_MatchIgnoresSeparators(string text, string expected)
    {
        Assert.True(V.When.ExtraDamageSet.TryMatch(text, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("Strength", V.Abilities.Str)]
    [InlineData("DEX", V.Abilities.Dex)]
    [InlineData("charisma", V.Abilities.Cha)]
    public void Abilities_AcceptFullNames(string text, string expected)
    {
        Assert.True(V.Abilities.Set.TryMatch(text, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("Great Weapon Fighting", V.FightingStyles.Gwf)]
    [InlineData("two-weapon fighting", V.FightingStyles.Twf)]
    [InlineData("GWF", V.FightingStyles.Gwf)]
    public void FightingStyles_AcceptTheirNames(string text, string expected)
    {
        Assert.True(V.FightingStyles.Set.TryMatch(text, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("none")]
    [InlineData("strong")]
    public void Abilities_SixOnly_RefuseTheRest(string? text)
    {
        Assert.False(V.Abilities.Set.TryMatch(text, out _));
    }

    [Fact]
    public void ToHitAbilities_AlsoTakeNone()
    {
        Assert.True(V.Abilities.ToHitSet.TryMatch("None", out var canonical));
        Assert.Equal(V.Abilities.None, canonical);
    }

    [Fact]
    public void DslValueSet_CollidingValuesOrBadAliases_AreRefusedAtConstruction()
    {
        Assert.Throws<InvalidOperationException>(() => new DslValueSet("x", ["two-handed", "two_handed"]));
        Assert.Throws<InvalidOperationException>(() => new DslValueSet("x", ["a"], new Dictionary<string, string> { ["b"] = "c" }));
        Assert.Throws<InvalidOperationException>(() => new DslValueSet("x", ["a", "b"], new Dictionary<string, string> { ["B"] = "a" }));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    [InlineData(16, 3)]
    [InlineData(17, 4)]
    [InlineData(20, 4)]
    public void CantripMultiplier_IsByCharacterLevelTier(int level, int multiplier)
    {
        Assert.Equal(multiplier, V.Cantrips.Multiplier(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void CantripMultiplier_OutsideLevels_IsABug(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => V.Cantrips.Multiplier(level));
    }

    [Theory]
    [InlineData(V.Shapes.Sphere, 20, 4)]
    [InlineData(V.Shapes.Sphere, 5, 1)]
    [InlineData(V.Shapes.Sphere, 1, 1)]
    [InlineData(V.Shapes.Sphere, 21, 5)]
    [InlineData(V.Shapes.Cone, 15, 2)]
    [InlineData(V.Shapes.Cone, 60, 6)]
    [InlineData(V.Shapes.Cube, 10, 2)]
    [InlineData(V.Shapes.Cylinder, 10, 2)]
    [InlineData(V.Shapes.Line, 30, 1)]
    [InlineData(V.Shapes.Line, 100, 4)]
    [InlineData(V.Shapes.Line, 120, 4)]
    public void ShapeTargets_FollowTheDmgTableRoundingUp(string shape, int size, int targets)
    {
        Assert.Equal(targets, V.Shapes.Targets(shape, size));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(V.Cover.Half, 2)]
    [InlineData(V.Cover.ThreeQuarters, 5)]
    public void CoverBonus_IsTwoOrFive(string? cover, int bonus)
    {
        Assert.Equal(bonus, V.Cover.Bonus(cover));
    }

    [Theory]
    [InlineData("2014", V.Remaps.Gwf2014)]
    [InlineData("2024", V.Remaps.Gwf2024)]
    public void GreatWeaponFighting_IsTheEditionsRemap(string edition, string remap)
    {
        Assert.Equal(remap, V.Remaps.GreatWeaponFighting(edition));
        Assert.True(V.Remaps.IsGreatWeaponFighting(remap));
        Assert.False(V.Remaps.IsGreatWeaponFighting(V.Remaps.ElementalAdept));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(8, 3)]
    [InlineData(9, 4)]
    [InlineData(13, 5)]
    [InlineData(17, 6)]
    [InlineData(20, 6)]
    public void ProficiencyBonus_IsByLevel(int level, int pb)
    {
        Assert.Equal(pb, DslLimits.ProficiencyBonus(level));
    }

    [Theory]
    [InlineData(1, -5)]
    [InlineData(8, -1)]
    [InlineData(9, -1)]
    [InlineData(10, 0)]
    [InlineData(11, 0)]
    [InlineData(19, 4)]
    [InlineData(20, 5)]
    [InlineData(30, 10)]
    public void AbilityModifier_RoundsDown(int score, int modifier)
    {
        Assert.Equal(modifier, DslLimits.AbilityModifier(score));
    }

    [Fact]
    public void AbilityDisplay_IsTheShortName()
    {
        Assert.Equal(["Str", "Dex", "Con", "Int", "Wis", "Cha"], V.Abilities.All.Select(V.Abilities.Display));
        Assert.Throws<ArgumentOutOfRangeException>(() => V.Abilities.Display("luck"));
    }
}
