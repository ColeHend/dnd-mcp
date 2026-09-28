using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using Xunit;
using static DndMcp.Tests.Features.FeatureTestBuilds;

namespace DndMcp.Tests.Features;

/// <summary>
/// Invariant: a target resolves to concrete numbers with their sources — with nothing given, the DMG 2014 row for
/// CR = level and the typical save bonus for that CR (cited as The Finished Book's, never as the DMG's); a given cr picks
/// its row; a given ac keeps saves on the CR = level row and says so; cover stays separate from AC and saves. A stat block
/// target takes every number from the stat block (qualified adjustments whole, the non-lair Legendary Resistance), each
/// field given overrides it in one note, and a monster named without its stat block is refused. The empirical profiles
/// are the edition's SRD medians for the CR, rounded half up, with a note giving the unrounded medians and the DMG row.
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
        Assert.Equal("dmg2014", target.Profile);
        Assert.Null(target.Monster);
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

    [Fact]
    public void Warnings_UntypedRiderOnTypedAttacksOnly_IsNotWarned()
    {
        // The rider deals each attack's own type (settled), so the resistance applies to it: nothing to warn about.
        var build = BuildResolver.Resolve(With(
            """[{ "name": "Sword", "damage": "1d8", "damage_type": "slashing" }, { "name": "Bow", "damage": "1d8", "damage_type": "piercing", "properties": ["ranged"] }]""",
            """[{ "kind": "extra_damage", "name": "Hunter's Mark", "dice": "1d6" }]"""), 5);

        Assert.Empty(TargetResolver.Warnings(build, TargetResolver.Resolve(Target("""{ "resistances": ["slashing"] }"""), 5)));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Stat block targets
    // ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Resolve_StatBlock_TakesEveryNumberFromIt()
    {
        var target = TargetResolver.Resolve(Target("""{ "monster": "werewolf" }"""), 5, TargetStatBlocks.WerewolfLike());

        Assert.Equal((11, "Werewolf stat block"), (target.ArmorClass, target.ArmorClassSource));
        Assert.Equal([2, 1, 2, 0, 0, 0], DslValues.Abilities.All.Select(target.SaveBonus));
        Assert.Equal("Werewolf stat block", target.SaveBonusSource);
        Assert.Equal(58, target.HitPoints);
        Assert.Equal(["cold"], target.Resistances);
        Assert.Equal(["bludgeoning", "piercing", "slashing"], target.QualifiedResistances.Select(r => r.DamageType));
        Assert.Equal("Werewolf (2014 SRD stat block)", target.MonsterLabel);
        Assert.Equal("3", target.ChallengeRating.ToString());
        Assert.Null(target.Row);
        Assert.Null(target.Profile);
        Assert.False(target.MagicResistance);
        Assert.Equal(0, target.LegendaryResistance);
        Assert.Contains("Werewolf's stat block also gives AC 12 in wolf or hybrid form; its first AC, 11, is used (give ac for another).", target.Notes);
        Assert.Contains(
            "Werewolf is CR 3, below level 5: the level's reference target (CR = level, the DMG 2014 row for CR 5) has AC 15 and +2 on every save.",
            target.Notes);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, false)]
    public void IsResistant_QualifiedEntry_AppliesOnlyToWhatTheQualifierAdmits(bool magical, bool silvered, bool adamantine, bool resisted)
    {
        var target = TargetResolver.Resolve(null, 5, TargetStatBlocks.WerewolfLike());
        var properties = new DamageProperties(magical, silvered, adamantine);

        Assert.Equal(resisted, target.IsResistant("slashing", properties));
        Assert.True(target.IsResistant("cold", properties)); // unqualified: always
        Assert.False(target.IsResistant("fire", properties));
        Assert.False(target.IsResistant(null, properties));
        Assert.True(target.IsResistant("piercing")); // the one-argument form asks about plain damage
    }

    [Fact]
    public void Resolve_LegendaryCaster_ReadsTraitsAndTheNonLairLegendaryResistance()
    {
        var target = TargetResolver.Resolve(null, 9, TargetStatBlocks.LegendaryCaster());

        Assert.True(target.MagicResistance);
        Assert.False(target.Evasion);
        Assert.Equal(3, target.LegendaryResistance);
        Assert.Equal(["frightened"], target.ConditionImmunities);
        Assert.True(target.IsImmuneToCondition("frightened"));
        Assert.False(target.IsImmuneToCondition("stunned"));
        Assert.Contains("Archlich's Legendary Resistance is 3 a day (4 in its lair); the target is not in its lair here, so it has 3.", target.Notes);
        Assert.Contains(target.Notes, n => n.StartsWith("Archlich is CR 17, above level 9:", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_EvasionTrait_GivesEvasion()
    {
        var rogue = TargetStatBlocks.Create(
            "Spy", "2014", "1", ac: 12, hp: 27, abilities: new ResolvedAbilities(10, 15, 10, 12, 14, 16),
            traits: [new StatBlockTrait { Name = "Evasion", Kind = StatBlockValues.TraitKinds.Evasion, Text = "Evasion." }]);

        var target = TargetResolver.Resolve(null, 1, rogue);

        Assert.True(target.Evasion);
        Assert.DoesNotContain(target.Notes, n => n.Contains("is CR", StringComparison.Ordinal)); // CR 1 at level 1: no reference note
    }

    [Fact]
    public void Resolve_ExplicitFields_OverrideTheStatBlockInOneNote()
    {
        var target = TargetResolver.Resolve(Target("""
            { "monster": "werewolf", "ac": 13, "save_bonus": 3, "saves": {"dex": 7}, "hp": 100, "resistances": ["fire"],
              "immunities": ["poison"], "magic_resistance": true, "evasion": true, "legendary_resistance": 2 }
            """), 3, TargetStatBlocks.WerewolfLike());

        Assert.Equal((13, "given"), (target.ArmorClass, target.ArmorClassSource));
        Assert.Equal((7, 3), (target.SaveBonus("dex"), target.SaveBonus("wis")));
        Assert.Equal("given per ability; the rest save_bonus +3", target.SaveBonusSource);
        Assert.Equal(100, target.HitPoints);
        Assert.Equal(["fire"], target.Resistances);
        Assert.Empty(target.QualifiedResistances);
        Assert.False(target.IsResistant("slashing"));
        Assert.Equal(["poison"], target.Immunities);
        Assert.Equal((true, true, 2), (target.MagicResistance, target.Evasion, target.LegendaryResistance));
        Assert.Contains(
            "Given, overriding the Werewolf stat block: ac 13 (stat block 11); save_bonus +3 (stat block Str +2, Dex +1, Con +2, Int +0, " +
            "Wis +0, Cha +0); saves dex +7 (stat block +1); hp 100 (stat block 58); resistances fire (stat block cold; bludgeoning, " +
            "piercing and slashing from nonmagical attacks that aren't silvered); immunities poison (stat block none); " +
            "magic_resistance true (stat block false); evasion true (stat block false); legendary_resistance 2 (stat block 0).",
            target.Notes);
        Assert.DoesNotContain(target.Notes, n => n.StartsWith("Werewolf's stat block also gives AC", StringComparison.Ordinal)); // ac given
        Assert.DoesNotContain(target.Notes, n => n.Contains("is CR 3", StringComparison.Ordinal)); // CR 3 at level 3
    }

    [Fact]
    public void Resolve_StartingConditionTheStatBlockIsImmuneTo_IsKeptWithANote()
    {
        var target = TargetResolver.Resolve(Target("""{ "condition": "stunned" }"""), 4, TargetStatBlocks.StunImmune());

        Assert.Equal("stunned", target.Condition);
        Assert.Contains(target.Notes, n => n.StartsWith("Helmed Horror is immune to the stunned condition, but condition stunned was given", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_MonsterWithoutItsStatBlock_IsRefusedNotDefaulted()
    {
        var ex = Assert.Throws<DndInputException>(() => TargetResolver.Resolve(Target("""{ "monster": "ogre", "ac": 11 }"""), 5));

        Assert.StartsWith("target monster \"ogre\" was not looked up: this call has no stat block for it", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_StatBlockPassedWithCrButNoMonsterText_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => TargetResolver.Resolve(Target("""{ "cr": 5 }"""), 5, TargetStatBlocks.Ogre()));

        Assert.Equal("Invalid target: the Ogre stat block replaces the cr row; give ac, saves or save_bonus to change its numbers instead.", ex.Message);
    }

    [Theory]
    [InlineData("""{ "monster": "ogre", "cr": 2 }""", "give monster or cr, not both: the stat block has its own CR")]
    [InlineData("""{ "monster": "ogre", "profile": "dmg2014" }""", "give monster or profile, not both")]
    [InlineData("""{ "monster": "" }""", "monster \"\" is not a monster ref or name; give one line of at most 100 characters")]
    [InlineData("""{ "monster": "ogre\nand a note" }""", "monster \"ogre\\nand a note\" is not a monster ref or name")]
    [InlineData("""{ "profile": "mm2025" }""", "profile \"mm2025\" is not a profile; give dmg2014, mm2014 or mm2024.")]
    public void Validate_MonsterAndProfile_AreRefusedWithWhy(string json, string why)
    {
        var ex = Assert.Throws<DndInputException>(() => TargetResolver.Validate(Target(json)));

        Assert.StartsWith("Invalid target: " + why, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dmg2014")]
    [InlineData("DMG-2014")]
    public void Resolve_Dmg2014Profile_IsTheDefaultTarget(string profile)
    {
        var given = TargetResolver.Resolve(Target($$"""{ "profile": "{{profile}}" }"""), 7);
        var none = TargetResolver.Resolve(null, 7);

        Assert.Equal((none.ArmorClass, none.ArmorClassSource, none.SaveBonusSource), (given.ArmorClass, given.ArmorClassSource, given.SaveBonusSource));
        Assert.Equal("dmg2014", given.Profile);
        Assert.Equal(none.Row, given.Row);
    }

    [Theory]
    [InlineData("mm2024", 5, 15, 1, "2024 SRD monster medians for CR 5 (25 monsters; CR = level 5)", "the median of the 2024 SRD's CR 5 monsters' mean save bonuses (0.83, rounded)")]
    [InlineData("mm2014", 5, 15, 1, "2014 SRD monster medians for CR 5 (25 monsters; CR = level 5)", "the median of the 2014 SRD's CR 5 monsters' mean save bonuses (0.83, rounded)")]
    [InlineData("mm2014", 4, 12, 1, "2014 SRD monster medians for CR 4 (11 monsters; CR = level 4)", "the median of the 2014 SRD's CR 4 monsters' mean save bonuses (0.67, rounded)")]
    [InlineData("mm2014", 1, 12, 0, "2014 SRD monster medians for CR 1 (25 monsters; CR = level 1)", "the median of the 2014 SRD's CR 1 monsters' mean save bonuses (0.33, rounded)")]
    [InlineData("mm2024", 20, 20, 6, "2024 SRD monster medians for CR 20 (3 monsters; CR = level 20)", "the median of the 2024 SRD's CR 20 monsters' mean save bonuses (6.33, rounded)")]
    [InlineData("mm2024", 18, 19, 7, "2024 SRD monster medians for CR 18 (no 2024 SRD monster has CR 18: interpolated between CR 17 and CR 19; CR = level 18)", "the 2024 SRD monsters' median mean save bonus interpolated to CR 18 (6.75, rounded)")]
    public void Resolve_EmpiricalProfile_IsTheEditionsMediansRounded(string profile, int level, int ac, int save, string acSource, string saveText)
    {
        var target = TargetResolver.Resolve(Target($$"""{ "profile": "{{profile}}" }"""), level);

        Assert.Equal((ac, acSource), (target.ArmorClass, target.ArmorClassSource));
        Assert.All(DslValues.Abilities.All, a => Assert.Equal(save, target.SaveBonus(a)));
        Assert.Equal($"{(save >= 0 ? "+" : "")}{save}, {saveText}", target.SaveBonusSource);
        Assert.Equal(profile, target.Profile);
        Assert.Null(target.Row); // no DMG row: results cite the empirical table instead
        Assert.NotNull(target.ProfileRow!.EmpiricalRow);
        Assert.True(target.CrFollowsLevel);
        Assert.Equal(level.ToString(System.Globalization.CultureInfo.InvariantCulture), target.ChallengeRating.ToString());
    }

    [Fact]
    public void Resolve_EmpiricalProfile_MatchesTheTableForEveryCr()
    {
        foreach (var edition in new[] { "2014", "2024" })
        {
            foreach (var cr in ChallengeRating.All)
            {
                var row = MonsterStatsEmpirical.MonsterStats(edition, cr);
                var target = TargetResolver.Resolve(Target($$"""{ "profile": "mm{{edition}}", "cr": "{{cr}}" }"""), 1);

                Assert.Equal(TargetProfiles.RoundMedian(row.ArmorClass), target.ArmorClass);
                Assert.Equal(TargetProfiles.RoundMedian(row.MeanSaveBonus), target.SaveBonus("wis"));
                Assert.Equal(row.IsInterpolated, target.ProfileRow!.Basis!.StartsWith("no ", StringComparison.Ordinal));
                Assert.False(target.CrFollowsLevel);
            }
        }
    }

    [Theory]
    [InlineData(14.5, 15)]
    [InlineData(14.49, 14)]
    [InlineData(0.5, 1)]
    [InlineData(-0.5, 0)]
    [InlineData(-0.51, -1)]
    [InlineData(0.8333, 1)]
    [InlineData(-1.3333, -1)]
    [InlineData(12.0, 12)]
    public void RoundMedian_HalfRoundsTowardTheHarderTarget(double median, int expected)
    {
        Assert.Equal(expected, TargetProfiles.RoundMedian(median));
    }

    [Fact]
    public void Resolve_EmpiricalProfile_NotesTheUnroundedMediansAndTheDmgRow()
    {
        var target = TargetResolver.Resolve(Target("""{ "profile": "mm2014" }"""), 4);

        Assert.Equal(
            "Target profile mm2014: AC 12 (median 12) and +1 on every save (median mean save bonus 0.67), from the 2014 SRD monster " +
            "medians for CR 4 (11 monsters), each rounded to a whole number with a half rounded up. The default profile, the DMG 2014 " +
            "row for CR 4, gives AC 14 and +2 on saves.",
            Assert.Single(target.Notes));
    }

    [Fact]
    public void Resolve_EmpiricalProfileWithAcGiven_TakesOnlyTheSavesFromTheTable()
    {
        var target = TargetResolver.Resolve(Target("""{ "profile": "mm2024", "ac": 17, "saves": {"dex": 4} }"""), 9);

        Assert.Equal((17, "given"), (target.ArmorClass, target.ArmorClassSource));
        Assert.Equal((4, 4), (target.SaveBonus("dex"), target.SaveBonus("wis"))); // CR 9 median mean save bonus 4.33
        Assert.StartsWith("given per ability; the rest +4, the median of the 2024 SRD's CR 9 monsters' mean save bonuses (4.33, rounded)", target.SaveBonusSource, StringComparison.Ordinal);
        Assert.Contains(
            "Target profile mm2024: +4 on the saves not given (median mean save bonus 4.33), from the 2024 SRD monster medians for CR 9 " +
            "(8 monsters), each rounded to a whole number with a half rounded up. The default profile, the DMG 2014 row for CR 9, gives +4 on saves.",
            target.Notes);
        Assert.Contains(target.Notes, n => n.StartsWith("Only ac was given, so the saves use CR 9", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_Dmg2014Profile_HasNoProfileNote()
    {
        Assert.Empty(TargetResolver.Resolve(Target("""{ "profile": "dmg2014" }"""), 4).Notes);
    }

    [Fact]
    public void Resolve_EmpiricalProfileWithEverythingGiven_NeedsNoTable()
    {
        var target = TargetResolver.Resolve(Target("""{ "profile": "mm2024", "ac": 15, "save_bonus": 2 }"""), 5);

        Assert.Equal((15, 2), (target.ArmorClass, target.SaveBonus("con")));
        Assert.Null(target.Profile);
        Assert.Null(target.ChallengeRating);
    }

    [Fact]
    public void DamageQualifierText_Describe_GroupsTypesByQualifier()
    {
        IReadOnlyList<DamageAdjustment> qualified =
        [
            new("bludgeoning", StatBlockValues.DamageQualifiers.Nonmagical, "b"),
            new("piercing", StatBlockValues.DamageQualifiers.Nonmagical, "p"),
            new("slashing", StatBlockValues.DamageQualifiers.NonmagicalNotAdamantine, "s"),
            new("fire", StatBlockValues.DamageQualifiers.Other, "fire while in dim light"),
        ];

        Assert.Equal(
            "cold and lightning; bludgeoning and piercing from nonmagical attacks; slashing from nonmagical attacks that aren't adamantine; " +
            "fire (\"fire while in dim light\", applied always)",
            DamageQualifierText.Describe(["cold", "lightning"], qualified));
        Assert.Equal("none", DamageQualifierText.Describe([], []));
    }

    [Fact]
    public void Resolve_OtherQualifier_AlwaysAppliesAndIsNoted()
    {
        var odd = TargetStatBlocks.Create(
            "Shade", "2014", "2", ac: 12, hp: 20, abilities: new ResolvedAbilities(10, 10, 10, 10, 10, 10),
            resistances: [new DamageAdjustment("slashing", StatBlockValues.DamageQualifiers.Other, "slashing while in dim light")]);
        var build = BuildResolver.Resolve(Build(Fighter2014GwmJson), 2);

        var target = TargetResolver.Resolve(null, 2, odd);

        Assert.True(target.IsResistant("slashing", new DamageProperties(true, true, true)));
        Assert.Equal(
            "Resistance to slashing (\"slashing while in dim light\", applied always): its condition is not modelled, so it applies to Greatsword every time.",
            Assert.Single(TargetResolver.AdjustmentNotes(build, target)));
    }
}
