using System.Text.Json;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation.Archetypes;
using Xunit;

namespace DndMcp.Tests.Simulation.Archetypes;

/// <summary>
/// Invariant: every party archetype (12 classes × 2 editions × levels 1–20) is a valid simulator build whose HP, AC and
/// saves follow the stated rules — max hit die at 1 then the average plus Con per level, armour by the DMG wealth tiers,
/// class saves plus proficiency — and bad names, levels and editions are refused with the list of archetypes.
/// </summary>
public sealed class ArchetypeCatalogTests
{
    public static IEnumerable<object[]> AllMembers() => ArchetypeTestKit.AllMembers();

    public static IEnumerable<object[]> AllArchetypes() => ArchetypeTestKit.AllArchetypes();

    // Hit dice and saving throws from the SRD class tables (rules_get, both editions agree), written out independently of
    // the definitions so a slip in either shows.
    private static readonly Dictionary<string, (int HitDie, string[] Saves, string Position)> Classes = new()
    {
        ["fighter"] = (10, ["str", "con"], "front"),
        ["barbarian"] = (12, ["str", "con"], "front"),
        ["paladin"] = (10, ["wis", "cha"], "front"),
        ["ranger"] = (10, ["str", "dex"], "back"),
        ["rogue"] = (8, ["dex", "int"], "front"),
        ["monk"] = (8, ["str", "dex"], "front"),
        ["cleric"] = (8, ["wis", "cha"], "front"),
        ["druid"] = (8, ["int", "wis"], "back"),
        ["wizard"] = (6, ["int", "wis"], "back"),
        ["sorcerer"] = (6, ["con", "cha"], "back"),
        ["warlock"] = (8, ["wis", "cha"], "back"),
        ["bard"] = (8, ["dex", "cha"], "back"),
    };

    [Fact]
    public void Names_AreTheTwelveClasses_InTheContractsOrder()
    {
        Assert.Equal(
            ["fighter", "barbarian", "paladin", "ranger", "rogue", "monk", "cleric", "druid", "wizard", "sorcerer", "warlock", "bard"],
            ArchetypeCatalog.Names);
        Assert.Equal(string.Join(", ", ArchetypeCatalog.Names), ArchetypeCatalog.NamesList);
    }

    [Theory]
    [MemberData(nameof(AllMembers))]
    public void Build_EveryArchetypeEditionAndLevel_ResolvesForTheSimulator(string name, string edition, int level)
    {
        var member = ArchetypeCatalog.Build(name, level, edition);
        var resolved = ArchetypeTestKit.Resolve(member);

        Assert.Equal(name, member.Archetype);
        Assert.Equal(edition, member.Edition);
        Assert.Equal(level, member.Level);
        Assert.Equal(level, member.Build.Level);
        Assert.Equal(edition, member.Build.Edition);
        Assert.Equal(edition, resolved.Edition);
        Assert.Equal(member.Abilities, resolved.Abilities);
        Assert.NotEmpty(resolved.Attacks.Concat<object>(resolved.SaveEffects));
    }

    [Theory]
    [MemberData(nameof(AllArchetypes))]
    public void Build_EveryArchetype_ValidatesAsOneCurveOverAllTwentyLevels(string name, string edition)
    {
        // One spec must validate for 1-20 at once (a level curve), not only level by level.
        var spec = ArchetypeCatalog.Build(name, 1, edition).Build;

        var builds = BuildResolver.Resolve(spec, Enumerable.Range(1, 20).ToList(), use: BuildUse.Simulation);

        Assert.Equal(20, builds.Count);
        Assert.True(BuildResolver.ScalesWithLevel(spec));
    }

    [Theory]
    [MemberData(nameof(AllArchetypes))]
    public void Build_TheSameArchetypeAtEveryLevel_DiffersOnlyInLevel(string name, string edition)
    {
        var first = ArchetypeCatalog.Build(name, 1, edition).Build;
        var json = JsonSerializer.Serialize(first, DslJson.Options);

        for (var level = 2; level <= 20; level++)
        {
            var build = ArchetypeCatalog.Build(name, level, edition).Build;
            Assert.Equal(json.Replace("\"level\":1,", $"\"level\":{level},"), JsonSerializer.Serialize(build, DslJson.Options));
        }
    }

    [Theory]
    [MemberData(nameof(AllArchetypes))]
    public void Build_RoundTripsThroughTheToolsJson_AndResolvesTheSame(string name, string edition)
    {
        // An archetype must be indistinguishable from a build the model sends: same JSON in, same build out.
        foreach (var level in new[] { 1, 5, 11, 20 })
        {
            var member = ArchetypeCatalog.Build(name, level, edition);
            var json = JsonSerializer.Serialize(member.Build, DslJson.Options);
            var reread = DslJson.Deserialize<BuildSpec>(json, "build");

            var direct = ArchetypeTestKit.Resolve(member);
            var viaJson = BuildResolver.Resolve(reread, level, use: BuildUse.Simulation);

            Assert.Equal(json, JsonSerializer.Serialize(reread, DslJson.Options));
            Assert.Equal(direct.Attacks.Select(a => (a.Name, a.Count, a.AttackBonus, a.Damage.Text)), viaJson.Attacks.Select(a => (a.Name, a.Count, a.AttackBonus, a.Damage.Text)));
            Assert.Equal(direct.Riders.Count + direct.SaveEffects.Count + direct.Heals.Count, viaJson.Riders.Count + viaJson.SaveEffects.Count + viaJson.Heals.Count);
        }
    }

    [Theory]
    [MemberData(nameof(AllMembers))]
    public void Hp_IsTheHitDieMaximumThenItsAveragePlusConPerLevel(string name, string edition, int level)
    {
        var member = ArchetypeCatalog.Build(name, level, edition);
        var die = Classes[name].HitDie;
        var con = DslLimits.AbilityModifier(member.Abilities.Con);

        Assert.Equal(die + (level - 1) * (die / 2 + 1) + level * con, member.Hp);
    }

    [Theory]
    [MemberData(nameof(AllArchetypes))]
    public void HpAndAc_RiseWithLevel_HpEveryLevelAcNeverFalls(string name, string edition)
    {
        var members = Enumerable.Range(1, 20).Select(l => ArchetypeCatalog.Build(name, l, edition)).ToList();

        for (var i = 1; i < members.Count; i++)
        {
            Assert.True(members[i].Hp > members[i - 1].Hp, $"{name} {edition}: HP at {i + 1} is not above HP at {i}.");
            Assert.True(members[i].Ac >= members[i - 1].Ac, $"{name} {edition}: AC falls at {i + 1}.");
        }

        Assert.All(members, m => Assert.InRange(m.Ac, 12, 24));
    }

    [Theory]
    [MemberData(nameof(AllMembers))]
    public void Saves_AreTheModifierPlusProficiencyForTheClassSaves(string name, string edition, int level)
    {
        var member = ArchetypeCatalog.Build(name, level, edition);
        var pb = DslLimits.ProficiencyBonus(level);
        var aura = name == "paladin" && level >= 6 ? Math.Max(1, DslLimits.AbilityModifier(member.Abilities.Cha)) : 0;

        Assert.Equal(Classes[name].Position, member.Position);
        Assert.Subset(member.SaveProficiencies.ToHashSet(), Classes[name].Saves.ToHashSet());
        Assert.Equal(DslValues.Abilities.All, member.Saves.Keys.ToList());
        foreach (var ability in DslValues.Abilities.All)
        {
            var expected = DslLimits.AbilityModifier(member.Abilities.Score(ability)) + (member.SaveProficiencies.Contains(ability) ? pb : 0) + aura;
            Assert.Equal(expected, member.Saves[ability]);
        }
    }

    [Theory]
    // archetype, edition, level, saves proficient (Slippery Mind at 15; Diamond Soul / Disciplined Survivor at 14)
    [InlineData("rogue", "2014", 14, "dex,int")]
    [InlineData("rogue", "2014", 15, "dex,int,wis")]
    [InlineData("rogue", "2024", 15, "dex,int,wis,cha")]
    [InlineData("monk", "2014", 13, "str,dex")]
    [InlineData("monk", "2014", 14, "str,dex,con,int,wis,cha")]
    [InlineData("monk", "2024", 14, "str,dex,con,int,wis,cha")]
    [InlineData("fighter", "2024", 20, "str,con")]
    public void SaveProficiencies_GainedAtLaterLevels_FollowTheClassTable(string name, string edition, int level, string saves)
    {
        Assert.Equal(saves.Split(','), ArchetypeCatalog.Build(name, level, edition).SaveProficiencies);
    }

    [Theory]
    // Hand-computed from the stated rules (see each archetype's doc).
    // archetype, edition, level, HP, AC, AC source
    [InlineData("fighter", "2024", 5, 49, 17, "splint")]              // Con 16: 10 + 4×6 + 5×3; splint (200 gp) at 5–10
    [InlineData("fighter", "2014", 1, 12, 16, "chain mail")]          // Con 15: 10 + 2
    [InlineData("fighter", "2014", 11, 114, 18, "plate")]             // Con 18: 10 + 10×6 + 11×4
    [InlineData("barbarian", "2014", 1, 14, 13, "Unarmored Defense")] // 12 + 2; 10 + Dex 1 + Con 2
    [InlineData("barbarian", "2014", 5, 55, 15, "scale mail")]        // Con 16: 12 + 4×7 + 5×3; 14 + 1 beats UD 14 (ties breastplate: cheaper)
    [InlineData("barbarian", "2014", 16, 197, 16, "Unarmored Defense")] // Con 20: 10 + 1 + 5 ties half plate 15 + 1: the free formula wins
    [InlineData("barbarian", "2014", 20, 285, 19, "Unarmored Defense")] // Primal Champion: Con 24 (+7): 12 + 19×7 + 20×7; 10 + Dex 2 + 7
    [InlineData("barbarian", "2024", 20, 285, 18, "Unarmored Defense")] // Dex stays 13 (no ASI at 19 in 2024): 10 + 1 + 7
    [InlineData("paladin", "2024", 1, 11, 18, "chain mail + shield")] // Con 13 (Cha comes second): 10 + 1
    [InlineData("paladin", "2014", 11, 81, 20, "plate + shield")]     // Con 13 until 19: 10 + 10×6 + 11×1
    [InlineData("ranger", "2014", 1, 12, 16, "scale mail")]           // 14 + Dex (max 2)
    [InlineData("ranger", "2024", 1, 12, 15, "studded leather")]      // 12 + Dex 3
    [InlineData("rogue", "2024", 8, 67, 17, "studded leather")]       // Dex 20: 12 + 5; Con 16: 8 + 7×5 + 8×3
    [InlineData("monk", "2014", 1, 9, 15, "Unarmored Defense")]       // 10 + Dex 3 + Wis 2
    [InlineData("monk", "2024", 20, 123, 24, "Unarmored Defense")]    // Body and Mind: Dex 24, Wis 24; Con 13: 8 + 19×5 + 20
    [InlineData("cleric", "2014", 1, 10, 17, "scale mail + shield")]  // 14 + 1 + 2
    [InlineData("cleric", "2024", 5, 43, 19, "splint + shield")]      // Protector: heavy armour
    [InlineData("druid", "2014", 5, 43, 15, "hide + shield")]         // non-metal medium armour
    [InlineData("druid", "2024", 5, 43, 15, "studded leather + shield")]
    [InlineData("wizard", "2014", 1, 8, 14, "Mage Armor")]            // 6 + 2; 13 + Dex 1
    [InlineData("wizard", "2014", 19, 173, 15, "Mage Armor")]         // 2014 ASI at 19: Dex 15
    [InlineData("sorcerer", "2024", 20, 182, 14, "Mage Armor")]       // Con 20: 6 + 19×4 + 20×5
    [InlineData("warlock", "2014", 1, 10, 12, "leather")]
    [InlineData("warlock", "2014", 2, 17, 14, "Armor of Shadows")]    // the invocation from 2
    [InlineData("warlock", "2024", 1, 10, 14, "Armor of Shadows")]    // 2024: an invocation at 1
    [InlineData("bard", "2024", 5, 43, 13, "studded leather")]
    public void Member_HitPointsAndArmourClass_ArePinned(string name, string edition, int level, int hp, int ac, string source)
    {
        var member = ArchetypeCatalog.Build(name, level, edition);

        Assert.Equal(hp, member.Hp);
        Assert.Equal(ac, member.Ac);
        Assert.Equal(source, member.AcSource);
    }

    [Fact]
    public void Member_Fighter2024Level5_IsPinnedWhole()
    {
        var member = ArchetypeCatalog.Build("fighter", 5, "2024");

        Assert.Equal(new ResolvedAbilities(Str: 18, Dex: 13, Con: 16, Int: 8, Wis: 12, Cha: 10), member.Abilities);
        Assert.Equal(49, member.Hp);
        Assert.Equal(17, member.Ac);
        Assert.Equal(["str", "con"], member.SaveProficiencies);
        Assert.Equal(new Dictionary<string, int> { ["str"] = 7, ["dex"] = 1, ["con"] = 6, ["int"] = -1, ["wis"] = 1, ["cha"] = 0 }, member.Saves);
        Assert.Equal("front", member.Position);
        Assert.Equal("Fighter archetype (2024)", member.Build.Name);
        Assert.StartsWith("Fighter (2024) level 5, no subclass: greatsword 2d6", member.Assumptions[0]);
        Assert.Contains(member.Assumptions, a => a.StartsWith("HP 49: d10 maximum at level 1, 6 per later level, Con +3", StringComparison.Ordinal));
        Assert.Contains(member.Assumptions, a => a.StartsWith("AC 17 (splint)", StringComparison.Ordinal));
    }

    [Theory]
    // Aura of Protection from 6: the Cha modifier, minimum +1, on all six of the paladin's OWN saves.
    // Scores (Cha is the second ability): Str 17/18 at 4/20 at 8, Cha 15/16 at 4/18 at 12/20 at 16, Con 13 (15 at 19 in
    // 2014), Wis 12, Dex 10, Int 8.
    // edition, level, saves str,dex,con,int,wis,cha
    [InlineData("2024", 5, "4,0,1,-1,4,6")]     // PB 3, no aura yet
    [InlineData("2024", 6, "7,3,4,2,7,9")]      // aura +3 (Cha 16)
    [InlineData("2024", 12, "9,4,5,3,9,12")]    // PB 4, aura +4 (Cha 18)
    [InlineData("2024", 16, "10,5,6,4,11,15")]  // PB 5, aura +5 (Cha 20)
    [InlineData("2014", 19, "10,5,7,4,12,16")]  // PB 6, aura +5 (Cha 20), Con 15 from the 2014 ASI at 19
    public void Saves_PaladinAuraOfProtection_AddsToItsOwnSaves(string edition, int level, string saves)
    {
        var member = ArchetypeCatalog.Build("paladin", level, edition);

        Assert.Equal(saves.Split(',').Select(int.Parse), DslValues.Abilities.All.Select(a => member.Saves[a]));
        Assert.Equal(["wis", "cha"], member.SaveProficiencies);
    }

    [Fact]
    public void Build_NamesMatchForgivingly_AndEditionDefaultsTo2024()
    {
        Assert.Equal("wizard", ArchetypeCatalog.Build(" WIZARD ", 3).Archetype);
        Assert.Equal("2024", ArchetypeCatalog.Build("Fighter", 3).Edition);
        Assert.Equal("2014", ArchetypeCatalog.Build("sorcerer", 3, "2014").Edition);
        Assert.True(ArchetypeCatalog.TryMatch("Bard", out var bard));
        Assert.Equal("bard", bard);
        Assert.False(ArchetypeCatalog.TryMatch("artificer", out _));
    }

    [Theory]
    [InlineData("artificer")]
    [InlineData("figher")]
    [InlineData("champion fighter")]
    public void Build_UnknownArchetype_IsRefusedWithEveryName(string name)
    {
        var ex = Assert.Throws<DndInputException>(() => ArchetypeCatalog.Build(name, 5));

        Assert.StartsWith($"archetype \"{name}\" is not a party archetype; archetypes are fighter, barbarian, paladin, ranger, rogue, monk, cleric, druid, wizard, sorcerer, warlock, bard", ex.Message);
        Assert.Contains(ArchetypeCatalog.Example, ex.Message);
        Assert.Equal(ArchetypeCatalog.Refusal(name), ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_EmptyArchetype_IsRefusedWithEveryName(string? name)
    {
        var ex = Assert.Throws<DndInputException>(() => ArchetypeCatalog.Build(name, 5));

        Assert.StartsWith("archetype is empty; archetypes are fighter, barbarian,", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(-3)]
    public void Build_LevelOutsideOneToTwenty_IsRefused(int level)
    {
        var ex = Assert.Throws<DndInputException>(() => ArchetypeCatalog.Build("fighter", level));

        Assert.Equal($"level {level} is not a character level; archetypes exist at levels 1-20.", ex.Message);
    }

    [Fact]
    public void Build_NoLevel_IsRefusedWithAnExample()
    {
        var ex = Assert.Throws<DndInputException>(() => ArchetypeCatalog.Build("fighter", null));

        Assert.Equal($"level is required with an archetype: a character level 1-20, e.g. {ArchetypeCatalog.Example}.", ex.Message);
    }

    [Theory]
    [InlineData("5e")]
    [InlineData("2020")]
    public void Build_UnknownEdition_IsRefused(string edition)
    {
        var ex = Assert.Throws<DndInputException>(() => ArchetypeCatalog.Build("fighter", 5, edition));

        // What leaving it out means, in the order balance_simulate applies it: never a bare "(default 2024)", which a 2014
        // campaign would contradict.
        Assert.Equal($"edition \"{edition}\" is not an edition; give \"2014\" or \"2024\" (left out: the fight's edition; else, with a 2014 " +
                     "or 2024 campaign active, the first party entry's, else the campaign's; else \"2024\").", ex.Message);
    }

    [Fact]
    public void Build_SeveralProblems_AreReportedTogether()
    {
        var ex = Assert.Throws<DndInputException>(() => ArchetypeCatalog.Build("necromancer", 30, "3.5"));

        Assert.Contains("archetype \"necromancer\" is not a party archetype", ex.Message);
        Assert.Contains("level 30 is not a character level", ex.Message);
        Assert.Contains("edition \"3.5\" is not an edition", ex.Message);
    }

    [Theory]
    [MemberData(nameof(AllArchetypes))]
    public void Assumptions_SayWhatWasSimulated(string name, string edition)
    {
        foreach (var level in new[] { 1, 5, 20 })
        {
            var member = ArchetypeCatalog.Build(name, level, edition);

            Assert.StartsWith($"{char.ToUpperInvariant(name[0])}{name[1..]} ({edition}) level {level}, no subclass: ", member.Assumptions[0]);
            Assert.Contains(member.Assumptions, a => a.StartsWith("Abilities ", StringComparison.Ordinal) && a.Contains("standard array", StringComparison.Ordinal));
            Assert.Contains(member.Assumptions, a => a.StartsWith($"HP {member.Hp}: d{Classes[name].HitDie} maximum", StringComparison.Ordinal));
            Assert.Contains(member.Assumptions, a => a.StartsWith($"AC {member.Ac} ({member.AcSource})", StringComparison.Ordinal));
            Assert.Contains(member.Assumptions, a => a.StartsWith("Not modelled: ", StringComparison.Ordinal));
            Assert.All(member.Assumptions, a => Assert.DoesNotContain("\n", a));
        }
    }

    [Theory]
    [InlineData("cleric")]
    [InlineData("druid")]
    [InlineData("wizard")]
    [InlineData("sorcerer")]
    [InlineData("bard")]
    public void Assumptions_FullCasters_SayThatUpcastingIsNotModelled(string name)
    {
        Assert.Contains(ArchetypeCatalog.Build(name, 17).Assumptions, a => a.Contains("no upcasting", StringComparison.Ordinal));
    }
}
