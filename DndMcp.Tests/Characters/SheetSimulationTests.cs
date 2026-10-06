using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using DndMcp.Domain.Simulation;
using DndMcp.Tests.Simulation;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant (contract D7): a sheet with a sim_profile becomes a build entry at the sheet's level with the sheet's
/// effective maximum, AC, save proficiencies and initiative (refused without level, AC or max HP); without one, the
/// archetype of the class with the most levels (ties: the first listed) at the total level in the sheet's ruleset, with
/// whatever the sheet has laid over it; no classes, a non-archetype main class or a dead sheet (three failed death saves,
/// exhaustion 6, a maximum of 0) is refused naming the character and the fix. Every entry it makes passes the simulator's
/// own validation.
/// </summary>
public sealed class SheetSimulationTests
{
    private const string BelmakorProfile = """
        {"name":"Belmakor L12 Bladesinger (fixture)","edition":"2014","level":12,
         "abilities":{"str":11,"dex":20,"con":16,"int":20,"wis":13,"cha":12},
         "attacks":[
          {"name":"Scimitar","count":2,"to_hit":{"ability":"dex"},"damage":"1d6","damage_type":"slashing","properties":["melee","finesse","light","magical"]},
          {"name":"Offhand scimitar","action":"bonus_action","offhand":true,"to_hit":{"ability":"dex"},"damage":"1d6","damage_type":"slashing","properties":["melee","finesse","light","magical"]}],
         "modifiers":[{"kind":"ac","name":"Bladesong","amount":"int","setup":"bonus_action","resource":{"uses":4,"per":"long_rest"}}]}
        """;

    private static BuildSpec Profile(string json = BelmakorProfile) => DslJson.Deserialize<BuildSpec>(json, "sim_profile");

    private static string Refusal(CharacterSheet sheet, string name = "Serif") =>
        Assert.Throws<DndInputException>(() => SheetSimulation.Entry(sheet, name, E2014)).Message;

    [Fact]
    public void Entry_SheetWithProfile_IsTheBuildWithTheSheetsNumbers()
    {
        var sheet = Create(BelmakorJson, E2014, Profile());

        var (entry, assumptions) = SheetSimulation.Entry(sheet, "Belmakor Silverwind", E2014);

        Assert.Equal("Belmakor Silverwind", entry.Name);
        Assert.Equal("Belmakor L12 Bladesinger (fixture)", entry.Build!.Name);
        Assert.Equal(12, entry.Level);
        Assert.Equal(110, entry.Hp);
        Assert.Equal(17, entry.Ac);
        Assert.Equal(["int", "wis", "con"], entry.SaveProficiencies);
        Assert.Equal(5, entry.InitiativeBonus);
        Assert.Null(entry.Archetype);
        Assert.Null(entry.Edition);
        Assert.Equal(
            "Belmakor Silverwind: the sheet's sim_profile (\"Belmakor L12 Bladesinger (fixture)\") at level 12, with the sheet's HP 110, AC 17, " +
            "save proficiencies Int, Wis, Con, initiative +5.",
            Assert.Single(assumptions));
        Accepted(entry);
    }

    [Fact]
    public void Entry_ProfileWithoutEdition_TakesTheSheets()
    {
        var sheet = Create(BelmakorJson, E2024, Profile(BelmakorProfile.Replace("\"edition\":\"2014\",", string.Empty, StringComparison.Ordinal)));

        Assert.Equal(E2014, SheetSimulation.Entry(sheet, "Belmakor", E2024).Entry.Build!.Edition);
    }

    [Fact]
    public void Entry_ProfileWithItsOwnEdition_KeepsIt()
    {
        var sheet = Create(BelmakorJson, E2014, Profile(BelmakorProfile.Replace("\"edition\":\"2014\"", "\"edition\":\"2024\"", StringComparison.Ordinal)));

        var (entry, _) = SheetSimulation.Entry(sheet, "Belmakor", E2014);

        Assert.Equal(E2024, entry.Build!.Edition);
        Accepted(entry);
    }

    [Fact]
    public void Entry_EffectiveMaximum_IsTheHp()
    {
        var sheet = Create(BelmakorJson, E2014, Profile()) with { MaxHpReduction = 10, Exhaustion = 4 };

        Assert.Equal(50, SheetSimulation.Entry(sheet, "Belmakor", E2014).Entry.Hp);
    }

    [Theory]
    [InlineData("""{ "level": 5 }""", "Belmakor's sheet has a sim_profile but no ac or max_hp")]
    [InlineData("""{ "level": 5, "ac": 15 }""", "Belmakor's sheet has a sim_profile but no max_hp")]
    [InlineData("""{ "level": 5, "max_hp": 40 }""", "Belmakor's sheet has a sim_profile but no ac")]
    public void Entry_ProfileWithoutTheSheetsNumbers_IsRefusedWithTheFix(string json, string expected)
    {
        var sheet = Create(json, E2014, Profile(BelmakorProfile.Replace("\"level\":12", "\"level\":5", StringComparison.Ordinal)));

        var message = Refusal(sheet, "Belmakor");

        Assert.StartsWith(expected, message, StringComparison.Ordinal);
        Assert.EndsWith("with campaign_character update, or leave Belmakor out.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_SingleClassWithoutProfile_IsTheArchetypeWithTheSheetOverlaid()
    {
        var sheet = Create("""{ "classes": [{ "class": "wizard", "level": 12 }], "max_hp": 74, "abilities": { "con": 14 } }""");

        var (entry, assumptions) = SheetSimulation.Entry(sheet, "Lieutenant James Torch", E2014);

        Assert.Equal("wizard", entry.Archetype);
        Assert.Equal(12, entry.Level);
        Assert.Equal(E2014, entry.Edition);
        Assert.Equal(74, entry.Hp);
        Assert.Null(entry.Ac);
        Assert.Equal(["int", "wis"], entry.SaveProficiencies);
        Assert.Null(entry.InitiativeBonus);
        Assert.Null(entry.Build);
        Assert.StartsWith("Lieutenant James Torch: no sim_profile, simulated as the level 12 wizard archetype (2014), with the sheet's HP 74", Assert.Single(assumptions), StringComparison.Ordinal);
        Accepted(entry);
    }

    [Fact]
    public void Entry_ArchetypeWithAReducedMaximum_IsGivenTheEffectiveMaximum()
    {
        var sheet = Create("""{ "classes": [{ "class": "wizard", "level": 12 }], "max_hp": 74 }""") with { MaxHpReduction = 4, Exhaustion = 4 };

        Assert.Equal(35, SheetSimulation.Entry(sheet, "Torch", E2014).Entry.Hp);
        Assert.Equal(70, SheetSimulation.Entry(sheet, "Torch", E2024).Entry.Hp);
    }

    [Fact]
    public void Entry_MinimalSheet_IsThePlainArchetype()
    {
        var sheet = Minimal("""[{ "class": "Bard", "level": 12 }]""") with { Saves = SheetSaves.None };

        var (entry, assumptions) = SheetSimulation.Entry(sheet, "Ignis", E2024);

        Assert.Equal(new CombatantSpec { Name = "Ignis", Archetype = "bard", Level = 12, Edition = E2024 }, entry);
        Assert.Null(entry.Hp);
        Assert.Contains("with the archetype's own numbers", assumptions[0], StringComparison.Ordinal);
        Accepted(entry);
    }

    [Theory]
    [InlineData("""[{ "class": "ranger", "level": 6 }, { "class": "rogue", "level": 6 }]""", "ranger", "the first listed on a tie")]
    [InlineData("""[{ "class": "paladin", "level": 6 }, { "class": "sorcerer", "level": 6 }]""", "paladin", "the first listed on a tie")]
    [InlineData("""[{ "class": "rogue", "level": 3 }, { "class": "fighter", "level": 9 }]""", "fighter", "the class with the most levels, at the total level")]
    [InlineData("""[{ "class": "artificer", "level": 2, "hit_die": 8 }, { "class": "cleric", "level": 10 }]""", "cleric", "the class with the most levels")]
    public void Entry_Multiclass_IsTheMostLevelsArchetypeAtTheTotalLevel(string classes, string archetype, string assumption)
    {
        var (entry, assumptions) = SheetSimulation.Entry(Minimal(classes), "Vars Nocturne", E2014);

        Assert.Equal(archetype, entry.Archetype);
        Assert.Equal(12, entry.Level);
        Assert.Contains(assumption, assumptions[0], StringComparison.Ordinal);
        Accepted(entry);
    }

    [Fact]
    public void Entry_HomebrewMainClass_IsRefusedNamingTheFix()
    {
        var message = Refusal(Minimal("""[{ "class": "artificer", "level": 12, "hit_die": 8 }]"""));

        Assert.Equal(
            "Serif (artificer 12) has no sim_profile and artificer is not a party archetype (fighter, barbarian, paladin, ranger, rogue, " +
            "monk, cleric, druid, wizard, sorcerer, warlock, bard): give the sheet a sim_profile or leave Serif out.",
            message);
    }

    [Fact]
    public void Entry_LevelWithoutClasses_IsRefused()
    {
        Assert.Equal(
            "Serif's sheet has no classes (only level 5), so there is no archetype to simulate: give the sheet a sim_profile (and classes) or leave Serif out.",
            Refusal(Create("""{ "level": 5 }""")));
    }

    [Fact]
    public void Entry_EmptySheet_IsRefused()
    {
        Assert.Contains("has no classes and no level", Refusal(CharacterSheet.New("e1")), StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_MaximumOfZero_IsRefusedAsDead()
    {
        var sheet = Belmakor() with { MaxHpReduction = 110 };

        Assert.Equal("Belmakor has a hit point maximum of 0 (dead): leave Belmakor out.", Refusal(sheet, "Belmakor"));
    }

    [Theory]
    [InlineData(3, 0, "Belmakor is dead (three failed death saves): leave Belmakor out.")]
    [InlineData(1, 6, "Belmakor is dead (exhaustion 6): leave Belmakor out.")]
    public void Entry_DeadSheet_IsRefusedByName(int failures, int exhaustion, string expected)
    {
        var sheet = Belmakor() with { Hp = 0, DeathSaves = new SheetDeathSaves(0, failures, false), Exhaustion = exhaustion };

        Assert.Equal(expected, Refusal(sheet, "Belmakor"));
        Assert.Equal(expected, Refusal(Create(BelmakorJson, E2014, Profile()) with { Hp = 0, DeathSaves = new SheetDeathSaves(0, failures, false), Exhaustion = exhaustion }, "Belmakor"));
    }

    [Fact]
    public void Entry_DyingSheet_IsStillSimulated()
    {
        var sheet = Belmakor() with { Hp = 0, DeathSaves = new SheetDeathSaves(1, 2, false) };

        Assert.Equal("wizard", SheetSimulation.Entry(sheet, "Belmakor", E2014).Entry.Archetype);
    }

    [Fact]
    public void Entry_SaveProficienciesStoredWithALaterWord_TakesTheAbilitiesOnly()
    {
        var sheet = Belmakor() with { Saves = new SheetSaves(["int", "luck", "wis", "int"], SheetMaps.Empty<int>()) };

        var (entry, assumptions) = SheetSimulation.Entry(sheet, "Belmakor", E2014);

        Assert.Equal(["int", "wis"], entry.SaveProficiencies);
        Assert.Contains("save proficiencies Int, Wis,", assumptions[0], StringComparison.Ordinal);
        Accepted(entry);
    }

    [Fact]
    public void Entry_SheetRulesetFirst_ThenTheCampaigns()
    {
        Assert.Equal(E2024, SheetSimulation.Entry(Minimal("""[{ "class": "fighter", "level": 5 }]""", E2024), "F", E2024).Entry.Edition);
        Assert.Equal(E2014, SheetSimulation.Entry(Bjorn() with { Ruleset = E2014 }, "Björn", E2024).Entry.Edition);
    }

    [Fact]
    public void Entry_FieldsTheCallGives_ReplaceTheSheetsAndTheLineNamesThemAsTheCalls()
    {
        // Torch: wizard 12, 74 HP, Int and Wis saves; the call says level 5, 20 HP, AC 18, back row.
        var sheet = Create("""{ "classes": [{ "class": "wizard", "level": 12 }], "max_hp": 74, "initiative_bonus": 2, "abilities": { "con": 14 } }""");
        var given = new CombatantSpec { Character = "character:torch", Level = 5, Hp = 20, Ac = 18, Position = "back" };

        var (entry, assumptions) = SheetSimulation.Entry(sheet, "T", E2014, given);

        Assert.Equal((5, 20, 18, "back"), (entry.Level, entry.Hp, entry.Ac, entry.Position));
        Assert.Equal(["int", "wis"], entry.SaveProficiencies);
        Assert.Equal(2, entry.InitiativeBonus);
        Assert.Null(entry.Character);
        Assert.Equal(
            "T: no sim_profile, simulated as the level 5 wizard archetype (2014), with the call's level 5, HP 20, AC 18, position back; the sheet's " +
            "save proficiencies Int, Wis, initiative +2; its attacks follow the archetype's ability plan (store a sim_profile for the character's own).",
            Assert.Single(assumptions));
        Assert.DoesNotContain("74", assumptions[0], StringComparison.Ordinal);
        Assert.DoesNotContain("level 12", assumptions[0], StringComparison.Ordinal);
        Accepted(entry);
    }

    [Fact]
    public void Entry_EveryFieldTheCallGives_IsNamedAsTheCalls_NoneAsTheSheets()
    {
        var sheet = Create(BelmakorJson, E2014, Profile());
        var given = new CombatantSpec
        {
            Level = 3,
            Hp = 30,
            Ac = 13,
            SaveProficiencies = ["dexterity"],
            Saves = new SavesSpec { Wis = 4, Str = -1 },
            InitiativeBonus = -1,
            DeathSaves = true,
        };

        var (entry, assumptions) = SheetSimulation.Entry(sheet, "Belmakor", E2014, given);

        Assert.Equal((3, 30, 13, -1), (entry.Level, entry.Hp, entry.Ac, entry.InitiativeBonus));
        Assert.Equal(["dexterity"], entry.SaveProficiencies);
        Assert.Equal((-1, 4), (entry.Saves!.Str, entry.Saves.Wis));
        Assert.True(entry.DeathSaves);
        Assert.Equal(
            "Belmakor: the sheet's sim_profile (\"Belmakor L12 Bladesinger (fixture)\") at level 3, with the call's level 3, HP 30, AC 13, " +
            "save proficiencies Dex, saves Str -1, Wis +4, initiative -1, death saves.",
            Assert.Single(assumptions));
        Accepted(entry);
    }

    [Theory]
    [InlineData("""{ "level": 5 }""")]
    [InlineData("""{ "level": 5, "ac": 15 }""")]
    [InlineData("""{ "level": 5, "max_hp": 40 }""")]
    public void Entry_ProfileTheCallCompletes_IsNotRefusedForWhatTheCallGives(string json)
    {
        var sheet = Create(json, E2014, Profile(BelmakorProfile.Replace("\"level\":12", "\"level\":5", StringComparison.Ordinal)));

        var (entry, assumptions) = SheetSimulation.Entry(sheet, "Belmakor", E2014, new CombatantSpec { Level = 5, Ac = 15, Hp = 40 });

        Assert.Equal((5, 40, 15), (entry.Level, entry.Hp, entry.Ac));
        Assert.Contains("with the call's level 5, HP 40, AC 15", Assert.Single(assumptions), StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_MulticlassWithTheCallsLevel_DoesNotClaimTheTotalLevel()
    {
        var (entry, assumptions) = SheetSimulation.Entry(Minimal("""[{ "class": "rogue", "level": 3 }, { "class": "fighter", "level": 9 }]"""), "Vars", E2014,
            new CombatantSpec { Level = 4 });

        Assert.Equal(("fighter", 4), (entry.Archetype, entry.Level));
        Assert.Contains("(multiclass rogue 3 / fighter 9: the class with the most levels), with the call's level 4", assumptions[0], StringComparison.Ordinal);
        Assert.DoesNotContain("total level", assumptions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_NothingGiven_IsTheSheetsEntryAsBefore()
    {
        var sheet = Create(BelmakorJson, E2014, Profile());

        var plain = SheetSimulation.Entry(sheet, "Belmakor", E2014);
        var given = SheetSimulation.Entry(sheet, "Belmakor", E2014, new CombatantSpec { Name = "X", Character = "character:belmakor" });

        Assert.Equal(plain.Assumptions, given.Assumptions);
        Assert.Equal(
            (plain.Entry.Name, plain.Entry.Level, plain.Entry.Hp, plain.Entry.Ac, plain.Entry.InitiativeBonus, plain.Entry.Position, plain.Entry.DeathSaves),
            (given.Entry.Name, given.Entry.Level, given.Entry.Hp, given.Entry.Ac, given.Entry.InitiativeBonus, given.Entry.Position, given.Entry.DeathSaves));
        Assert.Equal(plain.Entry.SaveProficiencies, given.Entry.SaveProficiencies);
        Assert.Null(given.Entry.Saves);
        Assert.Null(given.Entry.Character);
    }

    /// <summary>The entry passes the simulator's own preparation (its ranges, overlays and archetype expansion).</summary>
    private static void Accepted(CombatantSpec entry)
    {
        var spec = SimKit.Spec([new SimulationCombatant(entry)], [SimKit.Pc(SimKit.Commoner, hp: 4, ac: 10)], iterations: 10);
        Assert.NotNull(Simulator.Prepare(spec));
    }
}
