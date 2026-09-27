using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: a target resolves to concrete numbers with their sources — with nothing given, the DMG 2014 row for
/// CR = level and the typical save bonus for that CR (cited as The Finished Book's, never as the DMG's); a given cr picks
/// its row; a given ac keeps saves on the CR = level row and says so; cover stays separate from AC and saves.
/// </summary>
public sealed class TargetResolverTests
{
    [Theory]
    [InlineData(1, 13, 0)]
    [InlineData(2, 13, 1)]
    [InlineData(4, 14, 2)]
    [InlineData(5, 15, 2)]
    [InlineData(8, 16, 4)]
    [InlineData(10, 17, 5)]
    [InlineData(13, 18, 6)]
    [InlineData(14, 18, 7)]
    [InlineData(16, 18, 7)]
    [InlineData(17, 19, 8)]
    [InlineData(20, 19, 9)]
    public void Resolve_NoTarget_IsTheDmgRowForCrEqualsLevel(int level, int ac, int save)
    {
        var target = TargetResolver.Resolve(null, level);

        Assert.Equal(ac, target.ArmorClass);
        Assert.Equal($"DMG 2014 row for CR {level} (CR = level {level})", target.ArmorClassSource);
        Assert.All(DslValues.Abilities.All, a => Assert.Equal(save, target.SaveBonus(a)));
        Assert.Contains("The Finished Book", target.SaveBonusSource, StringComparison.Ordinal);
        Assert.Contains("not a DMG table", target.SaveBonusSource, StringComparison.Ordinal);
        Assert.Equal(ChallengeRating.Parse(level.ToString(System.Globalization.CultureInfo.InvariantCulture)), target.ChallengeRating);
        Assert.Equal(ac, target.Row!.ArmorClass);
        Assert.Null(target.HitPoints);
        Assert.Empty(target.Notes);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1/8", 0)]
    [InlineData("1/2", 0)]
    [InlineData("1", 0)]
    [InlineData("2", 1)]
    [InlineData("3", 1)]
    [InlineData("4", 2)]
    [InlineData("5", 2)]
    [InlineData("6", 3)]
    [InlineData("9", 4)]
    [InlineData("11", 5)]
    [InlineData("12", 6)]
    [InlineData("13", 6)]
    [InlineData("14", 7)]
    [InlineData("16", 7)]
    [InlineData("17", 8)]
    [InlineData("18", 8)]
    [InlineData("19", 9)]
    [InlineData("21", 10)]
    [InlineData("24", 11)]
    [InlineData("25", 12)]
    [InlineData("28", 13)]
    [InlineData("29", 14)]
    [InlineData("30", 14)]
    public void TypicalSaveBonus_IsTheFinishedBooksColumn(string cr, int bonus)
    {
        Assert.Equal(bonus, TypicalSaveBonus.For(ChallengeRating.Parse(cr)));
    }

    [Fact]
    public void TypicalSaveBonus_NeverDecreasesAndCitesItsSource()
    {
        var bonuses = ChallengeRating.All.Select(TypicalSaveBonus.For).ToList();

        Assert.True(bonuses.Zip(bonuses.Skip(1)).All(p => p.First <= p.Second));
        Assert.Contains("tomedunn", TypicalSaveBonus.Source, StringComparison.Ordinal);
        Assert.StartsWith("https://tomedunn.github.io/", TypicalSaveBonus.SourceUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "cr": "1/2" }""", 13, 0, "1/2")]
    [InlineData("""{ "cr": 0.5 }""", 13, 0, "1/2")]
    [InlineData("""{ "cr": "CR 12" }""", 17, 6, "12")]
    [InlineData("""{ "cr": 12 }""", 17, 6, "12")]
    public void Resolve_Cr_GivesItsRowsAcAndTypicalSaves(string json, int ac, int save, string cr)
    {
        var target = TargetResolver.Resolve(Target(json), 5);

        Assert.Equal(ac, target.ArmorClass);
        Assert.Equal($"DMG 2014 row for CR {cr}", target.ArmorClassSource);
        Assert.Equal(save, target.SaveBonus("wis"));
        Assert.Equal(cr, target.ChallengeRating.ToString());
        Assert.Empty(target.Notes);
    }

    [Fact]
    public void Resolve_AcOnly_KeepsSavesOnTheCrEqualsLevelRowWithANote()
    {
        var target = TargetResolver.Resolve(Target("""{ "ac": 18 }"""), 8);

        Assert.Equal(18, target.ArmorClass);
        Assert.Equal("given", target.ArmorClassSource);
        Assert.Equal(4, target.SaveBonus("dex"));
        Assert.Equal(
            "Only ac was given, so the saves use CR 8 (the CR = level row): +4 on every save not given. Give save_bonus or saves to set them.",
            Assert.Single(target.Notes));
    }

    [Fact]
    public void Resolve_AcAndCr_AcFromAcSavesFromCr()
    {
        var target = TargetResolver.Resolve(Target("""{ "ac": 20, "cr": "3" }"""), 10);

        Assert.Equal(20, target.ArmorClass);
        Assert.Equal(1, target.SaveBonus("con"));
        Assert.Empty(target.Notes);
    }

    [Fact]
    public void Resolve_SaveBonusAndSaves_OverrideTheTypicalBonus()
    {
        var all = TargetResolver.Resolve(Target("""{ "save_bonus": 3 }"""), 5);
        var some = TargetResolver.Resolve(Target("""{ "saves": {"dex": 7, "wis": -1} }"""), 5);
        var both = TargetResolver.Resolve(Target("""{ "save_bonus": 3, "saves": {"dex": 7} }"""), 5);
        var every = TargetResolver.Resolve(Target("""{ "ac": 15, "saves": {"str": 1, "dex": 2, "con": 3, "int": 4, "wis": 5, "cha": 6} }"""), 5);

        Assert.All(DslValues.Abilities.All, a => Assert.Equal(3, all.SaveBonus(a)));
        Assert.Equal("given", all.SaveBonusSource);
        Assert.Equal((7, -1, 2), (some.SaveBonus("dex"), some.SaveBonus("wis"), some.SaveBonus("str")));
        Assert.StartsWith("given per ability; the rest +2, the typical save bonus", some.SaveBonusSource, StringComparison.Ordinal);
        Assert.Equal((7, 3), (both.SaveBonus("dex"), both.SaveBonus("cha")));
        Assert.Equal("given per ability; the rest save_bonus +3", both.SaveBonusSource);
        Assert.Equal([1, 2, 3, 4, 5, 6], DslValues.Abilities.All.Select(every.SaveBonus));
        Assert.Equal("given", every.SaveBonusSource);
        Assert.Null(every.ChallengeRating);
        Assert.Null(every.Row);
    }

    [Fact]
    public void Resolve_CrZero_NotesItsAcIsACeiling()
    {
        var target = TargetResolver.Resolve(Target("""{ "cr": "0" }"""), 1);

        Assert.Equal(13, target.ArmorClass);
        Assert.Contains("The CR 0 row's AC 13 is a ceiling (\"13 or lower\"), used as the AC.", target.Notes);
    }

    [Fact]
    public void Resolve_EveryOtherField_IsCarriedCanonically()
    {
        var target = TargetResolver.Resolve(Target("""
            { "hp": 7, "resistances": ["Fire", "cold", "fire"], "vulnerabilities": ["radiant"], "immunities": ["poison", "cold"],
              "magic_resistance": true, "evasion": true, "condition": "Prone", "cover": "three-quarters", "legendary_resistance": 3,
              "save_dice": "-1d4", "second_target_rate": 0.4 }
            """), 5);

        Assert.Equal(7, target.HitPoints);
        Assert.Equal(["cold", "fire"], target.Resistances);
        Assert.Equal(["radiant"], target.Vulnerabilities);
        Assert.Equal(["cold", "poison"], target.Immunities);
        Assert.True(target.IsResistant("fire"));
        Assert.True(target.IsVulnerable("radiant"));
        Assert.True(target.IsImmune("poison"));
        Assert.False(target.IsResistant(null));
        Assert.False(target.IsImmune(null));
        Assert.True(target.MagicResistance);
        Assert.True(target.Evasion);
        Assert.Equal("prone", target.Condition);
        Assert.Equal(("three_quarters", 5), (target.Cover, target.CoverBonus));
        Assert.Equal(15, target.ArmorClass);
        Assert.Equal(3, target.LegendaryResistance);
        Assert.Equal("-1d4", target.SaveDice!.Text);
        Assert.Equal(0.4, target.SecondTargetRate);
        Assert.Contains("cold is both an immunity and a resistance or vulnerability; immunity wins (no cold damage).", target.Notes);
    }

    [Fact]
    public void Resolve_Defaults_AreNoneAndZero()
    {
        var target = TargetResolver.Resolve(new TargetSpec(), 5);

        Assert.Equal((false, false, null, null, 0, 0, 0.0), (target.MagicResistance, target.Evasion, target.Condition, target.Cover, target.CoverBonus, target.LegendaryResistance, target.SecondTargetRate));
        Assert.Null(target.SaveDice);
        Assert.Empty(target.Resistances);
    }

    [Theory]
    [InlineData("""{ "ac": 0 }""", "ac is 0; it is 1 to 40.")]
    [InlineData("""{ "ac": 41 }""", "ac is 41; it is 1 to 40.")]
    [InlineData("""{ "cr": "31" }""", "cr: \"31\" is not a Challenge Rating.")]
    [InlineData("""{ "cr": "1/3" }""", "cr: \"1/3\" is not a Challenge Rating.")]
    [InlineData("""{ "cr": 1e-400 }""", "cr: \"1e-400\" is not a Challenge Rating.")]
    [InlineData("""{ "cr": true }""", "cr: must be a string such as \"1/2\" or \"5\", or a number, but was the boolean true.")]
    [InlineData("""{ "save_bonus": -6 }""", "save_bonus is -6; it is -5 to 20.")]
    [InlineData("""{ "save_bonus": 21 }""", "save_bonus is 21; it is -5 to 20.")]
    [InlineData("""{ "saves": {"dex": 25} }""", "saves dex is 25; it is -5 to 20.")]
    [InlineData("""{ "hp": 0 }""", "hp is 0; it is 1 to 5000.")]
    [InlineData("""{ "hp": 5001 }""", "hp is 5001; it is 1 to 5000.")]
    [InlineData("""{ "legendary_resistance": 6 }""", "legendary_resistance is 6; it is 0 to 5.")]
    [InlineData("""{ "second_target_rate": 1.5 }""", "second_target_rate is 1.5; it is a number 0 to 1.")]
    [InlineData("""{ "resistances": ["sonic"] }""", "resistances has \"sonic\", which is not a damage type; they are acid, bludgeoning")]
    [InlineData("""{ "immunities": ["fire", "holy"] }""", "immunities has \"holy\", which is not a damage type")]
    [InlineData("""{ "condition": "charmed" }""", "condition \"charmed\" is not a condition; give prone, restrained, blinded, stunned, paralyzed, unconscious or dodging.")]
    [InlineData("""{ "cover": "full" }""", "cover \"full\" is not a cover; give half or three_quarters.")]
    [InlineData("""{ "save_dice": "1d4+1" }""", "save_dice \"1d4+1\" has a whole number (+1); these are dice only: put a flat change in save_bonus.")]
    [InlineData("""{ "save_dice": "2d4kh1" }""", "save_dice \"2d4kh1\" keeps or drops dice")]
    public void Validate_BadTarget_IsRefusedWithWhy(string json, string why)
    {
        var ex = Assert.Throws<DndInputException>(() => TargetResolver.Resolve(Target(json), 5));

        Assert.StartsWith("Invalid target: " + why, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ManyProblems_AreOneException()
    {
        var ex = Assert.Throws<DndInputException>(() => TargetResolver.Validate(Target("""{ "ac": 50, "hp": -1, "cover": "total" }""")));

        Assert.StartsWith("Invalid target (3 problems):\n- ac is 50", ex.Message, StringComparison.Ordinal);
        TargetResolver.Validate(null);
    }

    [Fact]
    public void Validate_NonFiniteRate_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => TargetResolver.Validate(new TargetSpec { SecondTargetRate = double.PositiveInfinity }));

        Assert.Equal("Invalid target: second_target_rate is not a finite number; it is a number 0 to 1.", ex.Message);
    }

    [Fact]
    public void Warnings_TypelessDamageAgainstAResistingTarget_IsWarned()
    {
        var build = BuildResolver.Resolve(With(
            """[{ "name": "Force of will", "damage": "1d8" }, { "name": "Sword", "damage": "1d8", "damage_type": "slashing" }]""",
            """[{ "kind": "extra_damage", "name": "Mystery", "dice": "1d4" }]"""), 5);

        var warnings = TargetResolver.Warnings(build, TargetResolver.Resolve(Target("""{ "resistances": ["slashing"] }"""), 5));

        Assert.Equal(
            "Force of will, Mystery have no damage type, so the target's resistances, vulnerabilities and immunities never apply to " +
            "them; give damage_type (or type) to model them.",
            Assert.Single(warnings));
        Assert.Empty(TargetResolver.Warnings(build, TargetResolver.Resolve(null, 5)));
        Assert.Empty(TargetResolver.Warnings(BuildResolver.Resolve(Build(Fighter2014GwmJson), 5), TargetResolver.Resolve(Target("""{ "immunities": ["fire"] }"""), 5)));
    }
}
