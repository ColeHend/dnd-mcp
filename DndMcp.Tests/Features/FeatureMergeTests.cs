using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: baseline + feature is the baseline with the feature's attacks appended (a same-named attack replaced in
/// place), its modifiers appended, the abilities it gives SET, and its fighting style in place of the baseline's; the
/// feature is checked on its own numbering first; the baseline is never changed.
/// </summary>
public sealed class FeatureMergeTests
{
    private const string NoFeat2024Json = """
        { "name": "2024 L5 Fighter", "edition": "2024", "level": 5,
          "abilities": {"str": 18, "con": 16}, "fighting_style": "gwf",
          "attacks": [{ "name": "Greatsword", "count": {"1": 1, "5": 2}, "damage": "2d6", "damage_type": "slashing",
                        "properties": ["melee", "heavy", "two-handed"], "mastery": "graze" }] }
        """;

    private const string GwmFeatureJson = """
        { "name": "Great Weapon Master", "abilities": {"str": 19},
          "modifiers": [
            { "kind": "bonus_damage", "name": "Great Weapon Master", "amount": "pb", "attack_action_only": true },
            { "kind": "extra_attack", "name": "Hew", "attack": "Greatsword", "action": "bonus_action", "trigger": "crit" } ] }
        """;

    [Fact]
    public void Merge_Gwm2024OntoTheNoFeatFighter_IsTheGoldenGwmBuild()
    {
        var variant = FeatureMerge.Merge(Build(NoFeat2024Json), Feature(GwmFeatureJson));

        var merged = BuildResolver.Resolve(variant, 5);
        var golden = BuildResolver.Resolve(Build(Fighter2024GwmJson), 5);
        Assert.Equal("2024 L5 Fighter + Great Weapon Master", merged.Name);
        Assert.Equal(golden.Attacks[0].ToHitParts, merged.Attacks[0].ToHitParts);
        Assert.Equal(golden.Attacks[0].DamageParts, merged.Attacks[0].DamageParts);
        Assert.Equal(golden.Attacks[0].WeaponRemap, merged.Attacks[0].WeaponRemap);
        Assert.Equal(golden.ExtraAttacks.Single() with { Source = merged.ExtraAttacks[0].Source }, merged.ExtraAttacks.Single());
        Assert.Equal(16, merged.Abilities.Con);
    }

    [Fact]
    public void Merge_AttackWithABaselineName_ReplacesItInPlaceAndOthersAreAppended()
    {
        var baseline = With("""[{ "name": "Greatsword", "damage": "2d6" }, { "name": "Longbow", "damage": "1d8", "properties": ["ranged"] }]""");
        var feature = Feature("""
            { "name": "Flame blade", "attacks": [{ "name": "GREATSWORD", "damage": "2d6+1d4" }, { "name": "Flame burst", "damage": "2d6", "properties": ["spell"] }] }
            """);

        var variant = FeatureMerge.Merge(baseline, feature);

        Assert.Equal(["GREATSWORD", "Longbow", "Flame burst"], variant.Attacks!.Select(a => a.Name));
        Assert.Equal("2d6+1d4", BuildResolver.Resolve(variant, 5).Attacks[0].Damage.Text);
        Assert.Equal(["Greatsword", "Longbow"], baseline.Attacks!.Select(a => a.Name));
    }

    [Fact]
    public void Merge_ModifiersAreAppendedAfterTheBaselines()
    {
        var baseline = With(modifiersJson: """[{ "kind": "lucky", "name": "Lucky" }]""");

        var variant = FeatureMerge.Merge(baseline, Feature("""{ "name": "Bless", "modifiers": [{ "kind": "to_hit", "name": "Bless", "dice": "1d4" }] }"""));

        Assert.Equal(["Lucky", "Bless"], variant.Modifiers!.Select(m => m.Name));
        Assert.Single(baseline.Modifiers!);
    }

    [Fact]
    public void Merge_Abilities_SetOnlyTheGivenScores()
    {
        var baseline = With(extra: "\"abilities\": {\"str\": {\"1\": 16, \"4\": 18}, \"dex\": 14},");

        var variant = FeatureMerge.Merge(baseline, Feature("""{ "name": "Resilient", "abilities": {"con": 13} }"""));
        var build = BuildResolver.Resolve(variant, 4);

        Assert.Equal((18, 14, 13), (build.Abilities.Str, build.Abilities.Dex, build.Abilities.Con));
        Assert.Equal(16, BuildResolver.Resolve(variant, 3).Abilities.Str);
    }

    [Fact]
    public void Merge_FightingStyle_ReplacesOrKeepsTheBaselines()
    {
        var baseline = With(extra: "\"fighting_style\": \"gwf\",");

        Assert.Equal("dueling", FeatureMerge.Merge(baseline, Feature("""{ "name": "Swap", "fighting_style": "dueling" }""")).FightingStyle);
        Assert.Equal("gwf", FeatureMerge.Merge(baseline, Feature("""{ "name": "Lucky", "modifiers": [{ "kind": "lucky" }] }""")).FightingStyle);
    }

    [Fact]
    public void Merge_PresetBaseline_IsExpandedFirst()
    {
        var baseline = new BuildSpec { Name = "EB", Preset = "warlock_baseline", Level = 5 };

        var variant = FeatureMerge.Merge(baseline, Feature("""{ "name": "Bless", "modifiers": [{ "kind": "to_hit", "name": "Bless", "dice": "1d4", "attacks": ["eldritch blast"] }] }"""));

        Assert.Null(variant.Preset);
        Assert.Equal(3, variant.Modifiers!.Count);
        var build = BuildResolver.Resolve(variant, 5);
        Assert.Equal("Bless", Assert.Single(build.Attacks[0].ToHitDice).Label);
    }

    [Fact]
    public void Merge_LongNames_AreCutToTheBuildNameLimit()
    {
        var baseline = new BuildSpec { Name = new string('b', 70), Level = 5, Attacks = With().Attacks };

        var variant = FeatureMerge.Merge(baseline, Feature($$"""{ "name": "{{new string('f', 70)}}", "modifiers": [{ "kind": "lucky" }] }"""));

        Assert.Equal(DslLimits.MaxBuildNameLength, variant.Name!.Length);
        Assert.EndsWith("…", variant.Name, StringComparison.Ordinal);
        BuildResolver.Validate(variant);
    }

    [Theory]
    [InlineData("""{ "modifiers": [{ "kind": "lucky" }] }""", "Invalid feature: name is required")]
    [InlineData("""{ "name": "Nothing" }""", "Invalid feature: the feature changes nothing: give attacks, modifiers, abilities or fighting_style")]
    [InlineData("""{ "name": "X", "fighting_style": "defense" }""", "Invalid feature: fighting_style \"defense\" is not a fighting style")]
    [InlineData("""{ "name": "X", "abilities": {"str": 31} }""", "Invalid feature: abilities str is 31; it is 1 to 30.")]
    [InlineData("""{ "name": "X", "modifiers": [{ "kind": "bonus_damage", "amount": 2, "attacks": ["Club"] }] }""", "Invalid feature: modifiers item 1 (bonus_damage): attacks names \"Club\", which is not an attack of this build; its attacks are \"Greatsword\", \"Longbow\".")]
    [InlineData("""{ "name": "X", "attacks": [{ "name": "Axe", "damage": "2d12kh1" }] }""", "Invalid feature: attacks item 1 (Axe): damage \"2d12kh1\" keeps or drops dice")]
    [InlineData("""{ "name": "X", "attacks": [{ "name": "Axe", "damage": "1d12" }, { "name": "axe", "damage": "1d6" }] }""", "Invalid feature: attacks item 2 (axe): name \"axe\" is also attacks item 1's")]
    public void Merge_BadFeature_IsRefusedWithTheFeaturesOwnNumbering(string json, string why)
    {
        var ex = Assert.Throws<DndInputException>(() => FeatureMerge.Merge(With(), Feature(json)));

        Assert.StartsWith(why, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_FeatureModifierOnAFeatureAttack_IsAccepted()
    {
        var variant = FeatureMerge.Merge(With(), Feature("""
            { "name": "Shadow blade", "attacks": [{ "name": "Shadow Blade", "damage": "2d8", "damage_type": "psychic", "properties": ["melee", "finesse", "light"] }],
              "modifiers": [{ "kind": "advantage", "name": "Dim light", "rate": 0.5, "attacks": ["Shadow Blade"] }] }
            """));

        var build = BuildResolver.Resolve(variant, 5);
        Assert.Equal(["Shadow Blade"], build.AdvantageSources[0].Attacks);
    }

    [Fact]
    public void Merge_VariantTooBig_IsCaughtWhenTheVariantIsValidated()
    {
        var attacks = "[" + string.Join(",", Enumerable.Range(1, 10).Select(i => $$"""{ "name": "A{{i}}", "damage": "1d4" }""")) + "]";
        var variant = FeatureMerge.Merge(With(attacks), Feature("""{ "name": "More", "attacks": [{ "name": "B", "damage": "1d4" }] }"""));

        var ex = Assert.Throws<DndInputException>(() => BuildResolver.Validate(variant, subject: "variant"));

        Assert.Equal("Invalid variant: attacks has 11 items; at most 10 are accepted.", ex.Message);
    }
}
