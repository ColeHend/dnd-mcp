using System.Text.Json;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: <c>update</c> creates a sheet or patches only the given fields, derives max HP, HP, Hit Dice, a single-class
/// caster's slots and the starting class's saves when they are absent (and only then), never derives AC, initiative or
/// resources, refuses what needs the existing sheet with the validator's message style, and stores a sim_profile only
/// validated at the sheet's level and canonical.
/// </summary>
public sealed class SheetUpdateTests
{
    private static SheetUpdateResult Apply(CharacterSheet? sheet, string json, string edition = E2014, BuildSpec? profile = null) =>
        SheetUpdate.Apply(sheet, Spec(json), profile, edition, sheet is null ? "e1" : null);

    private static string Refusal(CharacterSheet? sheet, string json, string edition = E2014, BuildSpec? profile = null) =>
        Assert.Throws<DndInputException>(() => Apply(sheet, json, edition, profile)).Message;

    [Fact]
    public void Apply_BelmakorFixture_CreatesTheContractsSheet()
    {
        var result = Apply(null, BelmakorJson);
        var sheet = result.Sheet;

        Assert.True(result.Created);
        Assert.Equal("e1", sheet.EntityId);
        Assert.Equal("""[{"class":"wizard","subclass":"Bladesinger","level":12,"hit_die":6}]""", Text(sheet, SheetColumns.Classes));
        Assert.Equal(12, sheet.Level);
        Assert.Equal("""{"str":11,"dex":20,"con":16,"int":20,"wis":13,"cha":12}""", Text(sheet, SheetColumns.Abilities));
        Assert.Equal(110, sheet.MaxHp);
        Assert.Equal(110, sheet.Hp);
        Assert.Equal(7, sheet.TempHp);
        Assert.Equal("""{"d6":{"max":12,"used":0}}""", Text(sheet, SheetColumns.HitDice));
        Assert.Equal(
            """{"1":{"max":4,"used":0},"2":{"max":3,"used":0},"3":{"max":3,"used":0},"4":{"max":3,"used":0},"5":{"max":2,"used":0},"6":{"max":1,"used":0}}""",
            Text(sheet, SheetColumns.SpellSlots));
        Assert.Equal(
            """{"bladesong":{"name":"Bladesong","max":4,"used":0,"recharge":"long_rest"},"arcane-recovery":{"name":"Arcane Recovery","max":1,"used":0,"recharge":"long_rest"},"contingency":{"name":"Contingency","state":"set","note":"Polymorph (T-rex) at low HP"}}""",
            Text(sheet, SheetColumns.Resources));
        Assert.Equal("""{"proficient":["int","wis","con"]}""", Text(sheet, SheetColumns.Saves));
        Assert.Equal("""[{"name":"War Caster"},{"name":"Resilient (Constitution)"},{"name":"Fey Touched"},{"name":"Tough"}]""", Text(sheet, SheetColumns.Feats));
        Assert.Equal("fixture", sheet.SheetSource);
        Assert.Equal("Cole", sheet.Player);
        Assert.Equal(17, sheet.Ac);
        Assert.Equal(5, sheet.InitiativeOrDex);
        Assert.Contains(result.Notes, n => n.Contains("Spell slots from the Wizard table at level 12 (2014)", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, n => n.Contains("max_hp", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_BjornFixture_DerivesMaxHpAndSavesFromTheClass()
    {
        var result = Apply(null, BjornJson, E2024);
        var sheet = result.Sheet;

        Assert.Equal(85, sheet.MaxHp);
        Assert.Equal(85, sheet.Hp);
        Assert.Equal(["str", "con"], sheet.Saves.Proficient);
        Assert.Equal("""{"d12":{"max":8,"used":0}}""", Text(sheet, SheetColumns.HitDice));
        Assert.Equal("{}", Text(sheet, SheetColumns.SpellSlots));
        Assert.Equal("""{"rage":{"name":"Rage","max":4,"used":0,"recharge":"short_rest_one"}}""", Text(sheet, SheetColumns.Resources));
        Assert.Contains(result.Notes, n => n.StartsWith("max_hp 85 derived", StringComparison.Ordinal) && n.Contains("give max_hp to set it", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n == "Saving throw proficiencies from the starting class (Barbarian): str, con.");
        Assert.Contains(result.Notes, n => n == "hp set to the maximum, 85.");
    }

    [Theory]
    [InlineData("""[{ "class": "ranger", "level": 6 }, { "class": "rogue", "level": 6 }]""")]
    [InlineData("""[{ "class": "paladin", "level": 6 }, { "class": "sorcerer", "level": 6 }]""")]
    [InlineData("""[{ "class": "bard", "level": 12 }]""")]
    [InlineData("""[{ "class": "artificer", "level": 12, "hit_die": 8 }]""")]
    public void Apply_MinimalSheetWithoutCon_HasNoHitPoints(string classes)
    {
        var result = Apply(null, $$"""{ "classes": {{classes}} }""");

        Assert.Null(result.Sheet.MaxHp);
        Assert.Null(result.Sheet.Hp);
        Assert.Equal(12, result.Sheet.Level);
        Assert.Contains(result.Notes, n => n == "max_hp is not derived without a Con score: give abilities.con, or max_hp.");
    }

    [Fact]
    public void Apply_MulticlassCasters_KeepsNoSlotsAndRemindsToGiveThem()
    {
        var result = Apply(null, """{ "classes": [{ "class": "paladin", "level": 6 }, { "class": "sorcerer", "level": 6 }] }""");

        Assert.Empty(result.Sheet.SpellSlots);
        var reminder = Assert.Single(result.Reminders, r => r.Kind == SheetValues.ReminderKinds.GiveSlots);
        Assert.Contains("the multiclassing rules are not in this server's data", reminder.Text, StringComparison.Ordinal);
        Assert.Equal(
            """campaign_character {"action": "update", "character": "character:aiden-ironstar", "sheet": {"slots": [4, 3, …]}}""",
            reminder.CallFor("character:aiden-ironstar"));
        Assert.Equal("""{"d10":{"max":6,"used":0},"d6":{"max":6,"used":0}}""", Text(result.Sheet, SheetColumns.HitDice));
        Assert.Equal(["wis", "cha"], result.Sheet.Saves.Proficient);
    }

    [Theory]
    [InlineData("""[{ "class": "warlock", "level": 3 }, { "class": "fighter", "level": 2 }]""", """{"pact":{"level":2,"max":2,"used":0}}""", false)]
    [InlineData("""[{ "class": "fighter", "level": 2 }, { "class": "warlock", "level": 11 }]""", """{"pact":{"level":5,"max":3,"used":0}}""", false)]
    [InlineData("""[{ "class": "warlock", "level": 3 }, { "class": "sorcerer", "level": 2 }]""", """{"pact":{"level":2,"max":2,"used":0}}""", true)]
    public void Apply_MulticlassWarlock_DerivesPactFromTheWarlockLevelAlone(string classes, string slots, bool remindsOfTheOthers)
    {
        var result = Apply(null, $$"""{ "classes": {{classes}} }""");

        Assert.Equal(slots, Text(result.Sheet, SheetColumns.SpellSlots));
        Assert.Contains(result.Notes, n => n.StartsWith("Pact Magic slots from the Warlock table at warlock level", StringComparison.Ordinal));
        var reminder = result.Reminders.SingleOrDefault(r => r.Kind == SheetValues.ReminderKinds.GiveSlots);
        Assert.Equal(remindsOfTheOthers, reminder is not null);
        if (reminder is not null)
        {
            Assert.EndsWith("give the slots of levels 1-9 (the Pact Magic slots follow the warlock level).", reminder.Text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("fighter", "Eldritch Knight")]
    [InlineData("rogue", "Arcane Trickster")]
    [InlineData("barbarian", null)]
    public void Apply_NonCasterWithGivenSlots_KeepsThemThroughAClassLevelChange(string className, string? subclass)
    {
        var subclassJson = subclass is null ? "null" : "\"" + subclass + "\"";
        var sheet = Apply(null, $$"""{ "classes": [{ "class": "{{className}}", "subclass": {{subclassJson}}, "level": 3 }], "slots": [2], "pact": { "level": 1, "max": 1 } }""").Sheet;
        sheet = SheetUse.Apply(sheet, slotLevel: 1).Sheet;

        var result = Apply(sheet, $$"""{ "classes": [{ "class": "{{className}}", "level": 4 }] }""");

        Assert.Equal("""{"1":{"max":2,"used":1},"pact":{"level":1,"max":1,"used":0}}""", Text(result.Sheet, SheetColumns.SpellSlots));
        Assert.DoesNotContain(SheetColumns.SpellSlots, result.Diff.ChangedColumns);
        Assert.DoesNotContain(result.Reminders, r => r.Kind == SheetValues.ReminderKinds.GiveSlots);
    }

    [Fact]
    public void Apply_MulticlassGivenSlots_AreStoredAsGiven()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "paladin", "level": 6 }, { "class": "warlock", "level": 3 }], "slots": [4, 2], "pact": { "level": 2, "max": 2 } }""").Sheet;

        Assert.Equal("""{"1":{"max":4,"used":0},"2":{"max":2,"used":0},"pact":{"level":2,"max":2,"used":0}}""", Text(sheet, SheetColumns.SpellSlots));
    }

    [Theory]
    [InlineData(E2014, "{}")]
    [InlineData(E2024, """{"1":{"max":2,"used":0}}""")]
    public void Apply_PaladinLevelOne_SlotsByEdition(string edition, string slots)
    {
        Assert.Equal(slots, Text(Apply(null, """{ "classes": [{ "class": "paladin", "level": 1 }] }""", edition).Sheet, SheetColumns.SpellSlots));
    }

    [Fact]
    public void Apply_SheetRulesetOverridesTheCampaigns_ForDerivations()
    {
        var sheet = Apply(null, """{ "ruleset": "2014", "classes": [{ "class": "ranger", "level": 1 }] }""", E2024).Sheet;

        Assert.Empty(sheet.SpellSlots);
    }

    [Fact]
    public void Apply_Warlock_DerivesPactSlots()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "warlock", "level": 11 }] }""").Sheet;

        Assert.Equal("""{"pact":{"level":5,"max":3,"used":0}}""", Text(sheet, SheetColumns.SpellSlots));
    }

    [Fact]
    public void Apply_SingleClassHomebrew_DerivesNoSlotsAndNoSaves()
    {
        var result = Apply(null, """{ "classes": [{ "class": "Dragon Slayer", "subclass": "Amethyst", "level": 8, "hit_die": 10 }], "abilities": { "con": 14 } }""", E2024);

        Assert.Equal("""[{"class":"Dragon Slayer","subclass":"Amethyst","level":8,"hit_die":10}]""", Text(result.Sheet, SheetColumns.Classes));
        Assert.Equal(68, result.Sheet.MaxHp);
        Assert.Empty(result.Sheet.Saves.Proficient);
        Assert.Empty(result.Sheet.SpellSlots);
        Assert.Empty(result.Reminders);
    }

    [Fact]
    public void Apply_PatchesOnlyGivenFields()
    {
        var sheet = Belmakor();

        var result = Apply(sheet, """{ "ac": 18, "abilities": { "wis": 14 } }""");

        Assert.False(result.Created);
        Assert.Equal([SheetColumns.Abilities, SheetColumns.Ac], result.Diff.ChangedColumns);
        Assert.Equal("""{"wis":14}""", result.Diff.Patches[SheetColumns.Abilities].ToJsonString());
        Assert.Equal(18L, result.Diff.Columns[SheetColumns.Ac]);
        Assert.Equal(20, result.Sheet.Score("dex"));
    }

    [Fact]
    public void Apply_NothingGiven_ChangesNothing()
    {
        var result = SheetUpdate.Apply(Belmakor(), null, null, E2014);

        Assert.True(result.Diff.IsEmpty);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Apply_EmptyText_ClearsTheField()
    {
        var sheet = Apply(Belmakor(), """{ "player": "", "background": "  " }""").Sheet;

        Assert.Null(sheet.Player);
        Assert.Null(sheet.Background);
    }

    [Fact]
    public void Apply_RulesetChanged_ReDerivesTheSlotsOfTheNewEdition()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "paladin", "level": 1 }] }""").Sheet;

        var result = Apply(sheet, """{ "ruleset": "2024" }""");

        Assert.Equal("""{"1":{"max":2,"used":0}}""", Text(result.Sheet, SheetColumns.SpellSlots));
        Assert.Contains(result.Notes, n => n.StartsWith("Spell slots from the Paladin table at level 1 (2024)", StringComparison.Ordinal));
        Assert.Equal("{}", Text(Apply(result.Sheet, """{ "ruleset": "2014" }""").Sheet, SheetColumns.SpellSlots));
    }

    [Fact]
    public void Apply_RulesetGivenAsTheEditionItAlreadyFollows_DerivesNothing()
    {
        var sheet = Apply(SheetUse.Apply(Belmakor(), slotLevel: 1).Sheet, """{ "slots": [4, 3, 3, 3, 2, 1, 1] }""").Sheet;

        var result = Apply(sheet, """{ "ruleset": "2014" }""");

        Assert.True(result.Diff.IsEmpty);
        Assert.Equal(1, result.Sheet.SpellSlots["7"].Max);
    }

    [Fact]
    public void Apply_ClassLevelsChangedWithoutMaxHp_RemindsItWasNotRecomputed()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "wizard", "level": 11 }], "abilities": { "con": 16 } }""").Sheet;
        Assert.Equal(79, sheet.MaxHp);

        var result = Apply(sheet, """{ "classes": [{ "class": "wizard", "level": 12 }] }""");

        Assert.Equal(79, result.Sheet.MaxHp);
        var reminder = Assert.Single(result.Reminders, r => r.Kind == SheetValues.ReminderKinds.MaxHpNotRecomputed);
        Assert.Equal(
            "The classes changed (wizard 11 → wizard 12), but max_hp stays 79: it is not recomputed (it may have been given, and feats " +
            "such as Tough are not known). level_up adds a level's hit points; give max_hp to set it.",
            reminder.Text);
        Assert.Equal("""campaign_character {"action": "update", "character": "character:x", "sheet": {"max_hp": …}}""", reminder.CallFor("character:x"));
    }

    [Theory]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 12 }], "max_hp": 85 }""")]
    [InlineData("""{ "classes": [{ "class": "wizard", "subclass": "Evoker", "level": 11 }] }""")]
    [InlineData("""{ "ac": 12 }""")]
    public void Apply_MaxHpGivenOrLevelsUnchanged_HasNoMaxHpReminder(string json)
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "wizard", "level": 11 }], "abilities": { "con": 16 } }""").Sheet;

        Assert.DoesNotContain(Apply(sheet, json).Reminders, r => r.Kind == SheetValues.ReminderKinds.MaxHpNotRecomputed);
    }

    [Fact]
    public void Apply_LevelOnlySheetLevelChanged_RemindsMaxHpWasNotRecomputed()
    {
        var sheet = Apply(null, """{ "level": 5, "max_hp": 40 }""").Sheet;

        var reminder = Assert.Single(Apply(sheet, """{ "level": 6 }""").Reminders);

        Assert.StartsWith("The level changed (5 → 6), but max_hp stays 40", reminder.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ClassLevelsLowered_CapsUsedHitDice()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "wizard", "level": 12 }] }""").Sheet;
        sheet = sheet with { HitDice = SheetMaps.Of(new[] { new KeyValuePair<string, HitDiceEntry>("d6", new HitDiceEntry(12, 10)) }) };

        var after = Apply(sheet, """{ "classes": [{ "class": "wizard", "level": 5 }] }""").Sheet;

        Assert.Equal("""{"d6":{"max":5,"used":5}}""", Text(after, SheetColumns.HitDice));
    }

    [Fact]
    public void Apply_HomebrewClassWithoutHitDie_KeepsTheStoredOne()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "Dragon Slayer", "level": 8, "hit_die": 10 }], "abilities": { "con": 14 } }""", E2024).Sheet;

        var after = Apply(sheet, """{ "classes": [{ "class": "dragon slayer", "subclass": "Amethyst", "level": 9 }] }""", E2024).Sheet;

        Assert.Equal("""[{"class":"dragon slayer","subclass":"Amethyst","level":9,"hit_die":10}]""", Text(after, SheetColumns.Classes));
        Assert.Equal("""{"d10":{"max":9,"used":0}}""", Text(after, SheetColumns.HitDice));
    }

    [Fact]
    public void Apply_EmptySaveProficiencies_StayEmptyThroughLaterUpdates()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "wizard", "level": 3 }], "save_proficiencies": [] }""").Sheet;

        var after = Apply(sheet, """{ "ac": 12, "classes": [{ "class": "wizard", "level": 4 }] }""").Sheet;

        Assert.Empty(after.Saves.Proficient);
    }

    [Fact]
    public void Apply_StartingClassGivenLater_DerivesItsSaves()
    {
        var sheet = Apply(null, """{ "level": 3 }""").Sheet;

        var result = Apply(sheet, """{ "classes": [{ "class": "cleric", "level": 3 }] }""");

        Assert.Equal(["wis", "cha"], result.Sheet.Saves.Proficient);
    }

    [Fact]
    public void Apply_ListReplaced_KeepsTheEntriesItCannotRead()
    {
        var stored = SheetJson.FromColumns(new Dictionary<string, object?>
        {
            [SheetColumns.EntityId] = "e1",
            [SheetColumns.Feats] = """[{"name":"Alert"},{"ref":"2014/feat/grappler"}]""",
        });

        var sheet = Apply(stored, """{ "feats": ["Tough", "Lucky"] }""").Sheet;

        Assert.Equal("""[{"name":"Tough"},{"ref":"2014/feat/grappler"},{"name":"Lucky"}]""", Text(sheet, SheetColumns.Feats));
    }

    [Fact]
    public void Apply_TrackerUpdated_KeepsTheUsedItWasStoredWith()
    {
        var stored = SheetJson.FromColumns(new Dictionary<string, object?>
        {
            [SheetColumns.EntityId] = "e1",
            [SheetColumns.Resources] = """{"contingency":{"name":"Contingency","used":1,"state":"set"}}""",
        });

        var sheet = Apply(stored, """{ "resources": [{ "name": "Contingency", "state": "fired" }] }""").Sheet;

        Assert.Equal("""{"contingency":{"name":"Contingency","used":1,"state":"fired"}}""", Text(sheet, SheetColumns.Resources));
    }

    [Fact]
    public void Apply_XpOnALevelOnlySheet_RemindsWithTheUpdateThatGivesTheLevel()
    {
        var sheet = Apply(null, """{ "level": 5, "xp": 6500 }""").Sheet;

        var reminder = Assert.Single(Apply(sheet, """{ "xp": 14500 }""").Reminders);

        Assert.Equal(SheetValues.ReminderKinds.LevelDue, reminder.Kind);
        Assert.Equal("""campaign_character {"action": "update", "character": "character:vars", "sheet": {"level": 6}}""", reminder.CallFor("character:vars"));
    }

    [Fact]
    public void Apply_ExistingMaxHp_IsNeverReDerived()
    {
        var sheet = Apply(Belmakor(), """{ "classes": [{ "class": "wizard", "subclass": "Bladesinger", "level": 13 }] }""").Sheet;

        Assert.Equal(110, sheet.MaxHp);
        Assert.Equal(13, sheet.Level);
        Assert.Equal("""{"d6":{"max":13,"used":0}}""", Text(sheet, SheetColumns.HitDice));
        Assert.Equal(1, sheet.SpellSlots["7"].Max);
    }

    [Fact]
    public void Apply_SubclassOnlyChange_KeepsGivenSlots()
    {
        var sheet = Apply(Belmakor(), """{ "slots": [4, 3, 3, 3, 2, 1, 1] }""").Sheet;

        var after = Apply(sheet, """{ "classes": [{ "class": "wizard", "subclass": "Bladesinging", "level": 12 }] }""").Sheet;

        Assert.Equal(1, after.SpellSlots["7"].Max);
        Assert.Equal("Bladesinging", after.Classes[0].Subclass);
    }

    [Fact]
    public void Apply_ClassesReplaced_KeepTheirUnknownKeysAndUsedHitDice()
    {
        var stored = SheetJson.FromColumns(new Dictionary<string, object?>
        {
            [SheetColumns.EntityId] = "e1",
            [SheetColumns.Classes] = """[{"class":"wizard","level":12,"hit_die":6,"class_ref":"2014/class/wizard"}]""",
            [SheetColumns.HitDice] = """{"d6":{"max":12,"used":5}}""",
            [SheetColumns.Level] = 12L,
        });

        var sheet = Apply(stored, """{ "classes": [{ "class": "Wizard", "level": 11 }, { "class": "fighter", "level": 1 }] }""").Sheet;

        Assert.Equal(
            """[{"class":"wizard","level":11,"hit_die":6,"class_ref":"2014/class/wizard"},{"class":"fighter","level":1,"hit_die":10}]""",
            Text(sheet, SheetColumns.Classes));
        Assert.Equal("""{"d10":{"max":1,"used":0},"d6":{"max":11,"used":5}}""", Text(sheet, SheetColumns.HitDice));
    }

    [Fact]
    public void Apply_ResourcesMerge_ByTheSlugOfTheirName()
    {
        var sheet = SheetUse.Apply(Belmakor(), resource: "bladesong").Sheet;

        var result = Apply(sheet, """
            { "resources": [
                { "name": "BLADESONG", "max": 5 },
                { "name": "Contingency", "state": "fired", "note": "" },
                { "name": "Arcane Recovery", "remove": true },
                { "name": "Lucky coin", "max": 3, "recharge": "none" } ] }
            """);

        Assert.Equal(
            """{"bladesong":{"name":"BLADESONG","max":5,"used":1,"recharge":"long_rest"},"contingency":{"name":"Contingency","state":"fired"},"lucky-coin":{"name":"Lucky coin","max":3,"used":0,"recharge":"none"}}""",
            Text(result.Sheet, SheetColumns.Resources));
        Assert.Equal(["arcane-recovery", "bladesong", "contingency", "lucky-coin"], result.Diff.Patches[SheetColumns.Resources].Select(p => p.Key).Order());
    }

    [Fact]
    public void Apply_ResourceMaxLowered_CapsUsed()
    {
        var sheet = Belmakor();
        sheet = SheetUse.Apply(sheet, resource: "Bladesong", amount: 3).Sheet;

        Assert.Equal(2, Apply(sheet, """{ "resources": [{ "name": "Bladesong", "max": 2 }] }""").Sheet.Resources["bladesong"].Used);
    }

    [Theory]
    [InlineData("""{ "resources": [{ "name": "Bladesong", "used": 5 }] }""", "resources item 1 (Bladesong): used is 5, more than max 4.")]
    [InlineData("""{ "resources": [{ "name": "Bladesong", "state": "on" }] }""", "resources item 1 (Bladesong): it is a counted resource (max, used, recharge); to change its kind, remove it first")]
    [InlineData("""{ "resources": [{ "name": "Contingency", "max": 1 }] }""", "resources item 1 (Contingency): it is a tracker (state); to change its kind")]
    [InlineData("""{ "resources": [{ "name": "Wild Shape", "used": 1 }] }""", "resources item 1 (Wild Shape): a new resource needs max (counted")]
    [InlineData("""{ "resources": [{ "name": "Wild Shape", "remove": true }] }""", "resources item 1 (Wild Shape): there is no resource of that name to remove.")]
    [InlineData("""{ "level": 13 }""", "level: this sheet has classes (wizard 12), so its level is their sum; give classes with the new levels, or use level_up.")]
    [InlineData("""{ "hp": 111 }""", "hp is 111, above the hit point maximum 110.")]
    [InlineData("""{ "hp": 101, "max_hp_reduction": 10 }""", "hp is 101, above the hit point maximum 100 (after its reduction).")]
    [InlineData("""{ "classes": [{ "class": "artificer", "level": 12 }] }""", "classes item 1 (artificer): hit_die is required for a class that is not an SRD class")]
    public void Apply_NeedsTheSheet_IsRefused(string json, string expected)
    {
        Assert.Contains(expected, Refusal(Belmakor(), json), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RefusedUpdate_ReportsEveryProblemTogether()
    {
        var message = Refusal(Belmakor(), """{ "hp": 200, "resources": [{ "name": "Bladesong", "used": 9 }] }""");

        Assert.StartsWith("Invalid sheet (2 problems):", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_MaxHpLowered_CapsHpWithANote()
    {
        var result = Apply(Belmakor(), """{ "max_hp": 100 }""");

        Assert.Equal(100, result.Sheet.Hp);
        Assert.Contains("hp lowered to the new maximum, 100.", result.Notes);
    }

    [Fact]
    public void Apply_MaxHpGivenLater_SetsHpWhenNoneWasTracked()
    {
        var sheet = Minimal("""[{ "class": "bard", "level": 12 }]""");

        var after = Apply(sheet, """{ "max_hp": 80 }""").Sheet;

        Assert.Equal(80, after.Hp);
    }

    [Fact]
    public void Apply_LevelOnlySheet_HasALevelAndNoDerivations()
    {
        var result = Apply(null, """{ "level": 5 }""");

        Assert.Equal(5, result.Sheet.Level);
        Assert.Empty(result.Sheet.Classes);
        Assert.Empty(result.Sheet.HitDice);
        Assert.Null(result.Sheet.MaxHp);
        Assert.Equal(6, Apply(result.Sheet, """{ "level": 6 }""").Sheet.Level);
    }

    [Fact]
    public void Apply_ConScoreChanged_RemindsOfTheRetroactiveHitPoints()
    {
        var result = Apply(Belmakor(), """{ "abilities": { "con": 18 } }""");

        Assert.Equal(110, result.Sheet.MaxHp);
        var reminder = Assert.Single(result.Reminders);
        Assert.Equal(SheetValues.ReminderKinds.ConChanged, reminder.Kind);
        Assert.Contains("from +3 to +4", reminder.Text, StringComparison.Ordinal);
        Assert.Contains("changes by +12 (1 per level), to 122", reminder.Text, StringComparison.Ordinal);
        Assert.Equal("""campaign_character {"action": "update", "character": "character:belmakor", "sheet": {"max_hp": 122}}""", reminder.CallFor("character:belmakor"));
    }

    [Fact]
    public void Apply_XpReachingALevel_RemindsToLevelUp()
    {
        var result = Apply(Bjorn(), """{ "xp": 48000 }""", E2024);

        var reminder = Assert.Single(result.Reminders);
        Assert.Equal(SheetValues.ReminderKinds.LevelDue, reminder.Kind);
        Assert.Equal("""campaign_character {"action": "level_up", "character": "character:bjorn"}""", reminder.CallFor("character:bjorn"));
    }

    [Fact]
    public void Apply_DefensesAndSaves_AreCanonical()
    {
        var sheet = Apply(Belmakor(), """{ "defenses": { "resist": ["Fire", "fire", "COLD"], "condition_immune": ["Charmed"] }, "save_bonus": { "con": 1, "wis": 0 } }""").Sheet;

        Assert.Equal("""{"resist":["fire","cold"],"condition_immune":["charmed"]}""", Text(sheet, SheetColumns.Defenses));
        Assert.Equal("""{"proficient":["int","wis","con"],"bonus":{"con":1}}""", Text(sheet, SheetColumns.Saves));
    }

    [Fact]
    public void Apply_ListsReplaced_KeepWhatTheSheetHeldAboutAnEntry()
    {
        var stored = SheetJson.FromColumns(new Dictionary<string, object?>
        {
            [SheetColumns.EntityId] = "e1",
            [SheetColumns.Spells] = """[{"name":"Shield","ref":"2014/spell/shield","prepared":true},{"name":"Fly"}]""",
        });

        var sheet = Apply(stored, """{ "spells": ["shield", "Circle of Power"] }""").Sheet;

        Assert.Equal("""[{"name":"shield","ref":"2014/spell/shield","prepared":true},{"name":"Circle of Power"}]""", Text(sheet, SheetColumns.Spells));
    }

    [Fact]
    public void Apply_PactMaxZero_RemovesPactSlots()
    {
        var sheet = Apply(null, """{ "classes": [{ "class": "warlock", "level": 5 }] }""").Sheet;

        Assert.Empty(Apply(sheet, """{ "pact": { "max": 0 } }""").Sheet.SpellSlots);
    }

    [Fact]
    public void Apply_SlotsLowered_KeepUsedCappedAndDropEmptyLevels()
    {
        var sheet = SheetUse.Apply(Belmakor(), slotLevel: 2, amount: 3).Sheet;

        var after = Apply(sheet, """{ "slots": [4, 2] }""").Sheet;

        Assert.Equal("""{"1":{"max":4,"used":0},"2":{"max":2,"used":2}}""", Text(after, SheetColumns.SpellSlots));
    }

    [Fact]
    public void Apply_SimProfile_IsStoredCanonicalAtTheSheetsLevel()
    {
        var profile = DslJson.Deserialize<BuildSpec>("""
            { "name": " Belmakor L12 Bladesinger (fixture) ", "edition": "2014", "level": 10,
              "abilities": { "str": 11, "dex": 20, "con": 16, "int": 20, "wis": 13, "cha": 12 },
              "attacks": [
                { "name": "Scimitar", "count": 2, "to_hit": { "ability": "Dexterity" }, "damage": "1d6", "damage_type": "Slashing",
                  "properties": ["Melee", "finesse", "LIGHT", "magical"] },
                { "name": "Offhand scimitar", "action": "Bonus Action", "offhand": true, "to_hit": { "ability": "dex" }, "damage": "1d6",
                  "damage_type": "slashing", "properties": ["melee", "finesse", "light", "magical"] } ],
              "modifiers": [{ "kind": "AC", "name": "Bladesong", "amount": "Intelligence", "setup": "bonus-action",
                              "resource": { "uses": 4, "per": "Long Rest" } }] }
            """, "sim_profile");

        var result = Apply(Belmakor(), "{}", profile: profile);

        var stored = JsonDocument.Parse(result.Sheet.SimProfile!).RootElement;
        Assert.Equal("Belmakor L12 Bladesinger (fixture)", stored.GetProperty("name").GetString());
        Assert.Equal("dex", stored.GetProperty("attacks")[0].GetProperty("to_hit").GetProperty("ability").GetString());
        Assert.Equal("slashing", stored.GetProperty("attacks")[0].GetProperty("damage_type").GetString());
        Assert.Equal("melee,finesse,light,magical", string.Join(",", stored.GetProperty("attacks")[0].GetProperty("properties").EnumerateArray().Select(p => p.GetString())));
        Assert.Equal("bonus_action", stored.GetProperty("attacks")[1].GetProperty("action").GetString());
        Assert.Equal("ac", stored.GetProperty("modifiers")[0].GetProperty("kind").GetString());
        Assert.Equal("int", stored.GetProperty("modifiers")[0].GetProperty("amount").GetString());
        Assert.Equal("long_rest", stored.GetProperty("modifiers")[0].GetProperty("resource").GetProperty("per").GetString());
        Assert.Equal([SheetColumns.SimProfile], result.Diff.ChangedColumns);
        Assert.Contains("sim_profile is written for level 10; simulations resolve it at the sheet's level (12).", result.Notes);
        Assert.Equal(result.Sheet.SimProfile, JsonSerializer.Serialize(SimProfile.Read(result.Sheet.SimProfile!), DslJson.Options));
    }

    [Fact]
    public void Apply_SimProfileOnASheetWithoutALevel_IsRefusedWithTheFix()
    {
        var message = Refusal(null, """{ "player": "Cole" }""", profile: Profile());

        Assert.Equal(
            "Invalid sheet: sim_profile needs the sheet's level (the simulator resolves the build at it): give sheet.classes or sheet.level first, or with it.",
            message);
    }

    [Fact]
    public void Apply_SimProfileGivenWithTheLevel_IsAccepted()
    {
        var sheet = Apply(null, """{ "level": 5 }""", profile: Profile()).Sheet;

        Assert.NotNull(sheet.SimProfile);
    }

    [Fact]
    public void Apply_InvalidSimProfile_IsRefusedAsSimProfile()
    {
        var bad = Profile() with { Attacks = [new AttackSpec { Name = "Club", Damage = JsonSerializer.SerializeToElement("2d6kh1") }] };

        Assert.StartsWith("Invalid sim_profile:", Refusal(Belmakor(), "{}", profile: bad), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_LevelChangeBreaksTheStoredProfile_Reminds()
    {
        var steps = Profile() with
        {
            Attacks = [new AttackSpec { Name = "Club", Damage = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["1"] = "1d4" }), FromLevel = 1, UntilLevel = 5 }],
            Modifiers = [new ModifierSpec { Kind = "extra_damage", Dice = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["5"] = "1d6" }) }],
        };
        var sheet = Apply(null, """{ "level": 5 }""", profile: steps).Sheet;

        var result = Apply(sheet, """{ "level": 4 }""");

        var reminder = Assert.Single(result.Reminders);
        Assert.Equal(SheetValues.ReminderKinds.SimProfile, reminder.Kind);
        Assert.Contains("does not work at level 4", reminder.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_NewSheetWithoutAnEntityId_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => SheetUpdate.Apply(null, Spec("{}"), null, E2014));
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("2020")]
    public void Apply_FallbackEditionNotAnEdition_IsAHostBug(string edition)
    {
        Assert.Throws<ArgumentException>(() => SheetUpdate.Apply(null, Spec("{}"), null, edition, "e1"));
    }

    private static BuildSpec Profile() => DslJson.Deserialize<BuildSpec>(
        """{ "name": "Club", "level": 5, "attacks": [{ "name": "Club", "damage": "1d4", "damage_type": "bludgeoning", "properties": ["melee"] }] }""",
        "sim_profile");
}
