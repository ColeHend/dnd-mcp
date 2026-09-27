using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: a step value is a scalar (the same at every level) or a map whose value at level L is the one under the
/// largest key ≤ L; keys are exactly the levels 1–20 written as whole numbers, once each; and a level below the first key
/// has no value (which validation turns into a message, and <see cref="LevelValue{T}.At"/> treats as a bug).
/// </summary>
public sealed class LevelValueTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Scalar_IsTheSameAtEveryLevel()
    {
        var value = LevelValue.ParseInt(Json("3"), "count", 1, 10)!;

        Assert.All(Enumerable.Range(1, 20), level => Assert.Equal(3, value.At(level)));
        Assert.False(value.IsStepMap);
        Assert.False(value.Scales);
        Assert.Equal(1, value.FirstLevel);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(10, 2)]
    [InlineData(11, 3)]
    [InlineData(20, 3)]
    public void StepMap_ValueHoldsFromItsLevelOn(int level, int expected)
    {
        var value = LevelValue.ParseInt(Json("""{"11": 3, "1": 1, "5": 2}"""), "count", 1, 10)!;

        Assert.Equal(expected, value.At(level));
        Assert.True(value.IsStepMap);
        Assert.True(value.Scales);
        Assert.Equal([1, 5, 11], value.Steps.Select(s => s.Level));
    }

    [Fact]
    public void StepMap_StartingAboveLevel1_HasNoValueBelowItsFirstKey()
    {
        var value = LevelValue.ParseInt(Json("""{"5": 2}"""), "count", 1, 10)!;

        Assert.False(value.TryAt(4, out _));
        Assert.True(value.TryAt(5, out var five));
        Assert.Equal(2, five);
        Assert.Equal(5, value.FirstLevel);
        Assert.False(value.Scales);
        var ex = Assert.Throws<InvalidOperationException>(() => value.At(4));
        Assert.Contains("level 4", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("21")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1.0")]
    [InlineData("one")]
    [InlineData("")]
    [InlineData("99999999999")]
    public void StepMap_KeyNotALevel_IsRefusedWithTheKeyAndAnExample(string key)
    {
        var ex = Assert.Throws<DndInputException>(() => LevelValue.ParseInt(Json($$"""{"{{key}}": 1}"""), "count", 1, 10));

        Assert.Equal(
            $"count: step map key \"{key}\" is not a level; keys are levels 1 to 20 written as whole numbers, e.g. {{\"1\": 1, \"5\": 2}}.",
            ex.Message);
    }

    [Fact]
    public void StepMap_DuplicateOrEmpty_IsRefused()
    {
        Assert.Equal(
            "count: step map gives level 5 twice; give each level once, e.g. {\"1\": 1, \"5\": 2}.",
            Assert.Throws<DndInputException>(() => LevelValue.ParseInt(Json("""{"1": 1, "5": 2, "5": 3}"""), "count", 1, 10)).Message);
        Assert.Equal(
            "count: step map is empty; give a value, or levels as keys, e.g. {\"1\": 1, \"5\": 2}.",
            Assert.Throws<DndInputException>(() => LevelValue.ParseInt(Json("{}"), "count", 1, 10)).Message);
    }

    [Theory]
    [InlineData("0", "count is 0; it is 1 to 10.")]
    [InlineData("99999999999", "count is 99999999999; it is 1 to 10.")]
    [InlineData("-3", "count is -3; it is 1 to 10.")]
    [InlineData("2.5", "count must be a whole number 1 to 10, or a step map by level such as {\"1\": 1, \"5\": 2}, but was the number 2.5.")]
    [InlineData("2.0", "but was the number 2.0.")]
    [InlineData("1e1", "but was the number 1e1.")]
    [InlineData("\"2.0\"", "but was the string \"2.0\".")]
    [InlineData("true", "but was the boolean true.")]
    [InlineData("[1]", "but was an array.")]
    [InlineData("""{"1": 1, "5": 11}""", "count at level 5 is 11; it is 1 to 10.")]
    [InlineData("""{"1": 1, "5": "x"}""", "count at level 5 must be a whole number 1 to 10, but was the string \"x\".")]
    public void ParseInt_BadValue_SaysWhatIsAccepted(string json, string why)
    {
        var ex = Assert.Throws<DndInputException>(() => LevelValue.ParseInt(Json(json), "count", 1, 10));

        Assert.Contains(why, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"2\"", 2)]
    [InlineData("\" 7 \"", 7)]
    [InlineData("\"+3\"", 3)]
    public void ParseInt_NumericString_IsAcceptedAsTheHostBinderWould(string json, int expected)
    {
        Assert.Equal(expected, LevelValue.ParseInt(Json(json), "count", 1, 10)!.At(1));
    }

    [Fact]
    public void Parse_NotGiven_IsNull()
    {
        Assert.Null(LevelValue.ParseInt(null, "count", 1, 10));
        Assert.Null(LevelValue.ParseInt(Json("null"), "count", 1, 10));
        Assert.False(LevelValue.IsGiven(null));
        Assert.False(LevelValue.IsGiven(Json("null")));
        Assert.True(LevelValue.IsGiven(Json("0")));
        Assert.True(LevelValue.IsGiven(3));
    }

    [Fact]
    public void Parse_ClrValues_AreReadLikeTheirJson()
    {
        Assert.Equal(4, LevelValue.ParseInt(4, "count", 1, 10)!.At(1));
        Assert.Equal(2, LevelValue.ParseInt(new Dictionary<string, int> { ["1"] = 1, ["5"] = 2 }, "count", 1, 10)!.At(5));
        Assert.Equal(2, LevelValue.ParseInt(new Dictionary<int, int> { [1] = 1, [5] = 2 }, "count", 1, 10)!.At(5));
        Assert.Equal("2d6", LevelValue.ParseDamage("2d6", "damage")!.At(1).Text);
    }

    [Theory]
    [InlineData("3", null, 3)]
    [InlineData("-100", null, -100)]
    [InlineData("\"4\"", null, 4)]
    [InlineData("\"pb\"", "pb", 0)]
    [InlineData("\"PB\"", "pb", 0)]
    [InlineData("\"Proficiency Bonus\"", "pb", 0)]
    [InlineData("\"cha\"", "cha", 0)]
    [InlineData("\"Charisma\"", "cha", 0)]
    [InlineData("\"STR\"", "str", 0)]
    public void ParseAmount_Forms_AreNumberPbOrAbility(string json, string? reference, int value)
    {
        var amount = LevelValue.ParseAmount(Json(json), "amount")!.At(1);

        Assert.Equal(reference, amount.Reference);
        Assert.Equal(value, amount.Value);
    }

    [Fact]
    public void DslAmount_Resolve_UsesProficiencyBonusAndAbilityModifiers()
    {
        var abilities = new ResolvedAbilities(18, 14, 12, 8, 10, 20);

        Assert.Equal(3, DslAmount.ProficiencyBonus.Resolve(3, abilities));
        Assert.Equal(5, DslAmount.AbilityModifier("cha").Resolve(3, abilities));
        Assert.Equal(-1, DslAmount.AbilityModifier("int").Resolve(3, abilities));
        Assert.Equal(-2, DslAmount.Of(-2).Resolve(3, abilities));
        Assert.Equal(["pb", "cha", "-2"], new[] { DslAmount.ProficiencyBonus, DslAmount.AbilityModifier("cha"), DslAmount.Of(-2) }.Select(a => a.ToString()));
        Assert.Throws<ArgumentOutOfRangeException>(() => DslAmount.AbilityModifier("luck"));
    }

    [Fact]
    public void ParseDamage_StepMapOfFormulasAndNumbers_IsReadPerLevel()
    {
        var value = LevelValue.ParseDamage(Json("""{"1": "1d6", "5": 3, "11": "2d6+1"}"""), "damage")!;

        Assert.Equal(["1d6", "3", "2d6+1"], value.Steps.Select(s => s.Value.Text));
    }

    [Fact]
    public void Select_MapsEveryStep()
    {
        var value = LevelValue<int>.StepMap((1, 1), (5, 2)).Select(v => v * 10);

        Assert.Equal([10, 20], value.Steps.Select(s => s.Value));
        Assert.True(value.IsStepMap);
    }

    [Fact]
    public void MissingAt_NamesFieldLevelFirstKeyAndTheFix()
    {
        Assert.Equal(
            "count has no value at level 3: its step map starts at level 5. Add a key \"3\" or lower (e.g. \"1\"), or give the attack from_level 5.",
            LevelValue.MissingAt("count", 3, 5, "attack"));
        Assert.Equal(
            "abilities str has no value at level 1: its step map starts at level 4. Add a key \"1\" or lower (e.g. \"1\").",
            LevelValue.MissingAt("abilities str", 1, 4, owner: null));
    }
}
