using System.Text.Json.Nodes;
using DndMcp.Domain.Characters;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: every JSON column of <c>character_sheet</c> reads and writes in contract §4's shape, a write of what was read
/// gives the same text, keys this version does not know survive a rewrite (inside entries and at the top of the map
/// columns), entries it cannot read (no name, a class level below 1) are written back verbatim in their place, and a
/// value of the wrong JSON or CLR type is refused naming the column instead of being read as a default that the next
/// write would store.
/// </summary>
public sealed class SheetJsonTests
{
    // Contract §4, verbatim where it gives the shape.
    [Theory]
    [InlineData(SheetColumns.Classes, """[{"class":"wizard","subclass":"bladesinger","level":12,"hit_die":6}]""")]
    [InlineData(SheetColumns.Abilities, """{"str":11,"dex":20,"con":16,"int":20,"wis":13,"cha":12}""")]
    [InlineData(SheetColumns.Saves, """{"proficient":["int","wis","con"],"bonus":{"con":1}}""")]
    [InlineData(SheetColumns.Defenses, """{"resist":["fire"],"immune":["poison"],"condition_immune":["poisoned"]}""")]
    [InlineData(SheetColumns.HitDice, """{"d6":{"max":12,"used":0}}""")]
    [InlineData(SheetColumns.SpellSlots, """{"1":{"max":4,"used":1},"6":{"max":1,"used":0},"pact":{"level":3,"max":2,"used":1}}""")]
    [InlineData(SheetColumns.Resources, """{"bladesong":{"name":"Bladesong","max":4,"used":1,"recharge":"long_rest"},"contingency":{"name":"Contingency","state":"set","note":"Polymorph → T-rex at low HP"}}""")]
    [InlineData(SheetColumns.Conditions, """[{"name":"cursed (Mucus Cloud)","source":"Aboleth","duration":"until_removed","note":"from The dark station, round 1"},{"name":"Circle of Power","effect":{},"duration":"rounds","remaining_rounds":98,"note":"…"}]""")]
    [InlineData(SheetColumns.Concentration, """{"spell":"Circle of Power","level":5,"remaining_rounds":98,"note":"from The crypt (fixture), round 1"}""")]
    [InlineData(SheetColumns.DeathSaves, """{"successes":0,"failures":0,"stable":false}""")]
    [InlineData(SheetColumns.Feats, """[{"name":"Tough"},{"name":"Shield","ref":"2014/spell/shield","source":"class","prepared":true}]""")]
    [InlineData(SheetColumns.Skills, """{"perception":{"proficient":true}}""")]
    [InlineData(SheetColumns.Movement, """{"fly":60}""")]
    [InlineData(SheetColumns.Senses, """{"darkvision":60}""")]
    public void Column_ContractShape_ReadsAndWritesBackTheSameText(string column, string json)
    {
        var sheet = SheetJson.FromColumns(Row(column, json));

        Assert.Equal(json, SheetJson.Column(sheet, column));
    }

    [Fact]
    public void ReadResources_ContractShape_ParsesCountedAndTrackerEntries()
    {
        var (resources, extra) = SheetJson.ReadResources(
            """{"bladesong":{"name":"Bladesong","max":4,"used":1,"recharge":"long_rest"},"contingency":{"name":"Contingency","state":"set","note":"x"}}""");

        Assert.Null(extra);
        Assert.Equal(["bladesong", "contingency"], resources.Keys);
        Assert.Equal(new SheetResource("Bladesong", 4, 1, "long_rest", null, null), resources["bladesong"]);
        Assert.True(resources["bladesong"].IsCounted);
        Assert.Equal(3, resources["bladesong"].Left);
        Assert.Equal(new SheetResource("Contingency", null, null, null, "set", "x"), resources["contingency"]);
        Assert.False(resources["contingency"].IsCounted);
    }

    [Fact]
    public void ReadSpellSlots_PactKey_CarriesItsLevel()
    {
        var (slots, _) = SheetJson.ReadSpellSlots("""{"pact":{"level":3,"max":2,"used":1},"1":{"max":4,"used":1}}""");

        Assert.Equal(new SpellSlotEntry(2, 1, 3), slots[SpellSlotEntry.PactKey]);
        Assert.Equal(new SpellSlotEntry(4, 1), slots["1"]);
        Assert.Equal(["1", "pact"], slots.Keys);
    }

    [Fact]
    public void ReadConditions_StringItem_ReadsAsAConditionOfThatName()
    {
        var conditions = SheetJson.ReadConditions("""["poisoned",{"name":"cursed","duration":"until_removed"},{"note":"no name"}]""");

        Assert.Equal(["poisoned", "cursed"], conditions.Select(c => c.Name));
        Assert.Equal(SheetValues.Durations.UntilRemoved, conditions[0].DurationOrDefault);
        Assert.False(conditions[1].IsTimed);
    }

    [Fact]
    public void ReadEntries_StringItems_BecomeNamedEntries()
    {
        Assert.Equal("""[{"name":"Tough"},{"name":"War Caster"}]""", SheetJson.WriteEntries(SheetJson.ReadEntries("""["Tough",{"name":"War Caster"}]""")));
    }

    [Theory]
    [InlineData(SheetColumns.Classes, """[{"class":"wizard","level":12,"hit_die":6,"class_ref":"2014/class/wizard"}]""")]
    [InlineData(SheetColumns.Saves, """{"proficient":["int"],"expertise":["wis"]}""")]
    [InlineData(SheetColumns.Defenses, """{"resist":["fire"],"note":"while raging"}""")]
    [InlineData(SheetColumns.HitDice, """{"d6":{"max":12,"used":0,"note":"x"}}""")]
    [InlineData(SheetColumns.SpellSlots, """{"1":{"max":4,"used":1,"created":1}}""")]
    [InlineData(SheetColumns.Resources, """{"bladesong":{"name":"Bladesong","max":4,"used":1,"recharge":"long_rest","pwa_key":"Bladesong"}}""")]
    [InlineData(SheetColumns.Conditions, """[{"name":"frightened","duration":"rounds","remaining_rounds":20,"save":{"ability":"wis","dc":14}}]""")]
    [InlineData(SheetColumns.Concentration, """{"spell":"Haste","spell_ref":"2024/spell/haste"}""")]
    [InlineData(SheetColumns.DeathSaves, """{"successes":1,"failures":2,"stable":false,"since":"round 3"}""")]
    [InlineData(SheetColumns.Feats, """[{"name":"Tough","level":4}]""")]
    [InlineData(SheetColumns.Abilities, """{"str":11,"luck":3}""")]
    [InlineData(SheetColumns.HitDice, """{"d6":{"max":12,"used":0},"note":"homebrew"}""")]
    [InlineData(SheetColumns.SpellSlots, """{"1":{"max":4,"used":1},"cantrips":4}""")]
    [InlineData(SheetColumns.Resources, """{"rage":{"name":"Rage","max":4,"used":0,"recharge":"long_rest"},"version":2}""")]
    public void Column_UnknownKeys_AreKeptWhenTheColumnIsRewritten(string column, string json)
    {
        var sheet = SheetJson.FromColumns(Row(column, json));

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse((string)SheetJson.Column(sheet, column)!)));
    }

    [Fact]
    public void Column_UnknownKeysThroughAnOperation_SurviveTheChange()
    {
        var sheet = SheetJson.FromColumns(Row(
            SheetColumns.Resources, """{"bladesong":{"name":"Bladesong","max":4,"used":0,"recharge":"long_rest","pwa_key":"Bladesong"},"version":2}"""));

        var used = SheetUse.Apply(sheet, resource: "Bladesong").Sheet;

        Assert.Equal(
            """{"bladesong":{"name":"Bladesong","max":4,"used":1,"recharge":"long_rest","pwa_key":"Bladesong"},"version":2}""",
            SheetJson.Column(used, SheetColumns.Resources));
    }

    [Theory]
    [InlineData(SheetColumns.Abilities, """{"cha":12,"str":11,"dex":20}""", """{"str":11,"dex":20,"cha":12}""")]
    [InlineData(SheetColumns.HitDice, """{"d6":{"used":1,"max":4},"d10":{"max":6,"used":0}}""", """{"d10":{"max":6,"used":0},"d6":{"max":4,"used":1}}""")]
    [InlineData(SheetColumns.SpellSlots, """{"pact":{"max":2,"used":0,"level":3},"2":{"max":3,"used":0},"1":{"max":4,"used":0}}""", """{"1":{"max":4,"used":0},"2":{"max":3,"used":0},"pact":{"level":3,"max":2,"used":0}}""")]
    [InlineData(SheetColumns.Saves, """{"bonus":{"wis":1,"con":2},"proficient":["wis"]}""", """{"proficient":["wis"],"bonus":{"con":2,"wis":1}}""")]
    [InlineData(SheetColumns.DeathSaves, """{"stable":true}""", """{"successes":0,"failures":0,"stable":true}""")]
    [InlineData(SheetColumns.Defenses, """{"vulnerable":[],"resist":["fire"]}""", """{"resist":["fire"]}""")]
    public void Column_AnyKeyOrder_WritesTheCanonicalOrder(string column, string json, string expected)
    {
        Assert.Equal(expected, SheetJson.Column(SheetJson.FromColumns(Row(column, json)), column));
    }

    // Rulings 5 and 6: what cannot be read is either kept verbatim (an entry) or refused (a value of the wrong type),
    // never read as a default the next write would store over it.
    [Theory]
    [InlineData(SheetColumns.Classes, "not json", "it is not JSON")]
    [InlineData(SheetColumns.Classes, "", "it is not JSON")]
    [InlineData(SheetColumns.Classes, """{"class":"wizard"}""", "it holds an object, not an array")]
    [InlineData(SheetColumns.Classes, """[{"class":"bard","level":"5"}]""", "item 1's \"level\" is text, not a whole number")]
    [InlineData(SheetColumns.Classes, """[{"class":"bard","level":5},"wizard"]""", "item 2 is text, not an object")]
    [InlineData(SheetColumns.Abilities, "[1,2]", "it holds a list, not an object")]
    [InlineData(SheetColumns.Saves, "\"int\"", "it holds text, not an object")]
    [InlineData(SheetColumns.Saves, """{"proficient":"int"}""", "\"proficient\" is text, not a list")]
    [InlineData(SheetColumns.Saves, """{"bonus":{"con":"1"}}""", "a \"bonus\" value is text, not a whole number")]
    [InlineData(SheetColumns.Defenses, """{"resist":["fire",3]}""", "item 2 of \"resist\" is a number, not text")]
    [InlineData(SheetColumns.DeathSaves, "{oops", "it is not JSON")]
    [InlineData(SheetColumns.DeathSaves, """{"successes":0,"failures":"3","stable":false}""", "\"failures\" is text, not a whole number")]
    [InlineData(SheetColumns.DeathSaves, """{"stable":"yes"}""", "\"stable\" is text, not true or false")]
    [InlineData(SheetColumns.Concentration, "[]", "it holds a list, not an object")]
    [InlineData(SheetColumns.Concentration, """{"spell":5}""", "\"spell\" is a number, not text")]
    [InlineData(SheetColumns.HitDice, """{"d8":{"max":5,"used":1.5}}""", "an entry's \"used\" is not a whole number")]
    [InlineData(SheetColumns.SpellSlots, """{"1":{"max":"four","used":0}}""", "an entry's \"max\" is text, not a whole number")]
    [InlineData(SheetColumns.Resources, """{"rage":{"name":"Rage","max":4,"recharge":["long_rest"]}}""", "an entry's \"recharge\" is a list, not text")]
    [InlineData(SheetColumns.Conditions, """["poisoned",3]""", "item 2 is a number, not an object")]
    [InlineData(SheetColumns.Conditions, """[{"name":"cursed","effect":"ac 2"}]""", "item 1's \"effect\" is text, not an object")]
    [InlineData(SheetColumns.Feats, """[{"name":"Tough","prepared":"yes"}]""", "item 1's \"prepared\" is text, not true or false")]
    [InlineData(SheetColumns.Languages, """[{"name":5}]""", "item 1's \"name\" is a number, not text")]
    [InlineData(SheetColumns.Skills, "[]", "it holds a list, not an object")]
    [InlineData(SheetColumns.Movement, "60", "it holds a number, not an object")]
    [InlineData(SheetColumns.SimProfile, "not json", "it is not JSON")]
    public void FromColumns_ValueOfTheWrongType_IsRefusedNamingTheColumn(string column, string json, string problem)
    {
        var message = Assert.Throws<InvalidDataException>(() => SheetJson.FromColumns(Row(column, json))).Message;

        Assert.StartsWith($"The character sheet's {column} column is not readable: {problem}.", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromColumns_KnownKeyOfTheWrongType_IsRefusedWithoutQuotingIt()
    {
        var message = Assert.Throws<InvalidDataException>(
            () => SheetJson.FromColumns(Row(SheetColumns.Resources, """{"the-nester":{"name":"The Nester's mark","max":"four","used":1}}"""))).Message;

        Assert.Contains("resources", message, StringComparison.Ordinal);
        Assert.DoesNotContain("four", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nester", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SheetColumns.Classes, """[{"class":"wizard"},{"level":3},{"class":"bard","level":5},{"class":"rogue","level":0,"note":"keep me"},null]""")]
    [InlineData(SheetColumns.Conditions, """[{"source":"Aboleth","note":"no name"},{"name":"poisoned"},""]""")]
    [InlineData(SheetColumns.Feats, """[{"ref":"2014/feat/grappler"},{"name":"Tough"}]""")]
    [InlineData(SheetColumns.Spells, """[{"name":"Shield"},{"name":"","ref":"2014/spell/fly"}]""")]
    [InlineData(SheetColumns.Concentration, """{"level":3,"note":"no spell"}""")]
    [InlineData(SheetColumns.Resources, """{"x":{"name":"X","used":2,"state":"set"}}""")]
    [InlineData(SheetColumns.Saves, """{"proficient":["int","Wisdom","int"],"bonus":{"con":1,"luck":2}}""")]
    [InlineData(SheetColumns.Defenses, """{"resist":["fire","Fire",""]}""")]
    public void FromColumns_EntryItCannotRead_IsWrittenBackVerbatimInItsPlace(string column, string json)
    {
        Assert.Equal(json, SheetJson.Column(SheetJson.FromColumns(Row(column, json)), column));
    }

    [Fact]
    public void FromColumns_UnreadableClassItems_AreNotClassesButAreKept()
    {
        var sheet = SheetJson.FromColumns(Row(SheetColumns.Classes, """[{"class":"wizard"},{"class":"bard","level":5},{"class":"rogue","level":0}]"""));

        Assert.Equal([new SheetClass("bard", null, 5, null)], sheet.Classes);
        Assert.Equal([new SheetRawItem(0, """{"class":"wizard"}"""), new SheetRawItem(2, """{"class":"rogue","level":0}""")], sheet.Unreadable[SheetColumns.Classes]);
    }

    [Fact]
    public void FromColumns_ConcentrationWithoutASpell_IsStillAConcentration()
    {
        var sheet = SheetJson.FromColumns(Row(SheetColumns.Concentration, """{"level":3}"""));

        Assert.Equal(new SheetConcentration(string.Empty, 3, null, null), sheet.Concentration);
        Assert.Equal("an unnamed spell", sheet.Concentration!.Display);
    }

    [Fact]
    public void Column_UnreadableEntriesThroughAnOperation_SurviveInTheirPlaces()
    {
        var sheet = SheetJson.FromColumns(Row(SheetColumns.Conditions, """[{"name":"cursed"},{"note":"no name"},{"name":"poisoned"}]"""));

        var after = SheetConditions.Apply(sheet, ["blinded"], ["cursed"], null, E2014).Sheet;

        Assert.Equal("""[{"name":"poisoned"},{"note":"no name"},{"name":"blinded","duration":"until_removed"}]""", SheetJson.Column(after, SheetColumns.Conditions));
    }

    [Theory]
    [InlineData(SheetColumns.Hp, 5.0)]
    [InlineData(SheetColumns.Player, 3.5)]
    [InlineData(SheetColumns.Conditions, true)]
    public void FromColumns_ValueNotAStoredType_IsAHostBugNamingTheColumn(string column, object value)
    {
        var row = new Dictionary<string, object?> { [SheetColumns.EntityId] = "e1", [column] = value };

        Assert.Contains($"The {column} column was given a ", Assert.Throws<ArgumentException>(() => SheetJson.FromColumns(row)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromColumns_SnapshotJsonElement_IsAHostBugNamingTheColumn()
    {
        var row = new Dictionary<string, object?> { [SheetColumns.EntityId] = "e1", [SheetColumns.Conditions] = System.Text.Json.JsonDocument.Parse("[]").RootElement };

        Assert.Contains("The conditions column was given a JsonElement", Assert.Throws<ArgumentException>(() => SheetJson.FromColumns(row)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SheetColumns.Hp, "12", "it holds text, not an integer")]
    [InlineData(SheetColumns.Hp, 5_000_000_000L, "it holds a number beyond the integer range")]
    [InlineData(SheetColumns.Classes, 3L, "it holds a number, not text")]
    public void FromColumns_StorageClassMismatch_IsRefusedNamingTheColumn(string column, object value, string problem)
    {
        var row = new Dictionary<string, object?> { [SheetColumns.EntityId] = "e1", [column] = value };

        Assert.StartsWith($"The character sheet's {column} column is not readable: {problem}.", Assert.Throws<InvalidDataException>(() => SheetJson.FromColumns(row)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadResources_NoName_TakesTheKeyAsTheName()
    {
        var (resources, _) = SheetJson.ReadResources("""{"wild-shape":{"max":2,"used":1},"blank":{"name":"","max":1}}""");

        Assert.Equal("wild-shape", resources["wild-shape"].Name);
        Assert.Equal("blank", resources["blank"].Name);
    }

    [Theory]
    [InlineData(SheetColumns.Concentration, "null")]
    [InlineData(SheetColumns.SimProfile, "null")]
    public void FromColumns_JsonNull_ReadsAsNone(string column, string json)
    {
        Assert.Null(SheetJson.Column(SheetJson.FromColumns(Row(column, json)), column));
    }

    [Fact]
    public void FromColumns_WholeNumberWrittenAsDecimal_ReadsAsTheInteger()
    {
        var sheet = SheetJson.FromColumns(Row(SheetColumns.HitDice, """{"d8":{"max":5.0,"used":2}}"""));

        Assert.Equal(new HitDiceEntry(5, 2), sheet.HitDice["d8"]);
    }

    [Fact]
    public void ToColumns_FromColumns_RoundTripsEveryColumn()
    {
        var sheet = Belmakor() with
        {
            CreatedAt = "2026-10-03T10:00:00.000Z",
            UpdatedAt = "2026-10-03T11:00:00.000Z",
            Size = "Medium",
            Lineage = "High Elf",
            Speed = 30,
            PassivePerception = 11,
            Inspiration = true,
            Exhaustion = 2,
            MaxHpReduction = 4,
            Concentration = new SheetConcentration("Circle of Power", 5, 98, "from The crypt (fixture), round 1"),
            Conditions = [new SheetCondition("cursed", "Aboleth", "until_removed", null, "n")],
            DeathSaves = new SheetDeathSaves(1, 2, false),
            NotesMd = "Björn's \"friend\"",
        };

        var columns = SheetJson.ToColumns(sheet);
        var back = SheetJson.FromColumns(columns);

        Assert.Equal(SheetColumns.All, columns.Keys);
        foreach (var column in SheetColumns.All)
        {
            Assert.Equal(SheetJson.Column(sheet, column), SheetJson.Column(back, column));
        }

        Assert.Equal(1L, columns[SheetColumns.Inspiration]);
        Assert.Equal(110L, columns[SheetColumns.MaxHp]);
        Assert.Equal("Björn's \"friend\"", columns[SheetColumns.NotesMd]);
    }

    [Fact]
    public void FromColumns_IntegerAsInt_IsAcceptedLikeSqlitesLong()
    {
        var sheet = SheetJson.FromColumns(new Dictionary<string, object?> { [SheetColumns.EntityId] = "e1", [SheetColumns.Level] = 12, [SheetColumns.Hp] = 5L });

        Assert.Equal(12, sheet.Level);
        Assert.Equal(5, sheet.Hp);
    }

    [Fact]
    public void FromColumns_MissingColumns_ReadAsTheTableDefaults()
    {
        var sheet = SheetJson.FromColumns(new Dictionary<string, object?> { [SheetColumns.EntityId] = "e1" });
        var fresh = CharacterSheet.New("e1");

        foreach (var column in SheetColumns.All)
        {
            Assert.Equal(SheetJson.Column(fresh, column), SheetJson.Column(sheet, column));
        }

        Assert.Equal("""{"successes":0,"failures":0,"stable":false}""", SheetJson.Column(sheet, SheetColumns.DeathSaves));
        Assert.Equal("[]", SheetJson.Column(sheet, SheetColumns.Conditions));
        Assert.Equal("{}", SheetJson.Column(sheet, SheetColumns.Resources));
        Assert.Equal(0L, SheetJson.Column(sheet, SheetColumns.Exhaustion));
        Assert.Null(SheetJson.Column(sheet, SheetColumns.Concentration));
    }

    [Fact]
    public void FromColumns_NoEntityId_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => SheetJson.FromColumns(new Dictionary<string, object?>()));
    }

    [Fact]
    public void Column_UnknownColumn_IsAHostBug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SheetJson.Column(CharacterSheet.New("e1"), "is_mine"));
    }

    [Fact]
    public void Column_RelaxedEscaping_KeepsNamesReadable()
    {
        var sheet = CharacterSheet.New("e1") with { Conditions = [new SheetCondition("cursed (Mucus Cloud)", "Björn's \"axe\"", null, null, "<b>")] };

        Assert.Equal(
            """[{"name":"cursed (Mucus Cloud)","source":"Björn's \"axe\"","note":"<b>"}]""",
            SheetJson.Column(sheet, SheetColumns.Conditions));
    }

    [Fact]
    public void Columns_Lists_AreTheMigrationsColumnsInOrder()
    {
        // 0002_characters_combat.sql's character_sheet, column for column (stage 2 pins this against CampaignTables too).
        Assert.Equal(
            [
                "entity_id", "player", "ruleset", "species", "lineage", "background", "size", "classes", "level", "xp", "abilities",
                "saves", "skills", "ac", "max_hp", "max_hp_reduction", "hp", "temp_hp", "speed", "movement", "senses",
                "initiative_bonus", "passive_perception", "spell_save_dc", "spell_attack", "defenses", "hit_dice", "spell_slots",
                "resources", "conditions", "concentration", "death_saves", "exhaustion", "inspiration", "feats", "features", "spells",
                "languages", "sim_profile", "notes_md", "sheet_source", "created_at", "updated_at",
            ],
            SheetColumns.All);
        Assert.Equal(43, SheetColumns.All.Count);
        Assert.Equal(SheetColumns.All.Count, SheetColumns.All.Distinct().Count());
        Assert.Equal(SheetColumns.EntityId, SheetColumns.All[0]);
        Assert.Equal(SheetColumns.UpdatedAt, SheetColumns.All[^1]);
        Assert.All(SheetColumns.PerKey.Concat(SheetColumns.Integers).Concat(SheetColumns.Json).Concat(SheetColumns.CombatOwned),
            c => Assert.Contains(c, SheetColumns.All));
        Assert.Equal([SheetColumns.Abilities, SheetColumns.HitDice, SheetColumns.SpellSlots, SheetColumns.Resources], SheetColumns.PerKey);
    }

    [Theory]
    [InlineData("d6", 6)]
    [InlineData("d12", 12)]
    [InlineData("d", null)]
    [InlineData("6", null)]
    [InlineData("dx", null)]
    [InlineData("d-6", null)]
    public void DieOf_Key_IsTheSidesOrNull(string key, int? sides)
    {
        Assert.Equal(sides, SheetJson.DieOf(key));
    }

    private static Dictionary<string, object?> Row(string column, string json) =>
        new() { [SheetColumns.EntityId] = "e1", [column] = json };
}
