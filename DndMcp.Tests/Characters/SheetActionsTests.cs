using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: <c>use</c> spends or restores exactly one slot level, the pact slots or one counted resource and refuses
/// beyond what is left or used; <c>condition</c> adds SRD conditions canonically and other names as effects (default
/// until_removed), refuses immunities (exhaustion included) and duplicates, moves exhaustion as a column by levels with
/// 6 = dead, and ends concentration when it incapacitates or kills (§5.8); <c>xp</c> adds signed XP, starts a sheet with no XP
/// total at 0, and reminds (never levels) when a threshold is reached, with the call that resolves it for that sheet.
/// </summary>
public sealed class SheetActionsTests
{
    [Fact]
    public void Use_SlotLevel_SpendsOneAndSaysWhatIsLeft()
    {
        var result = SheetUse.Apply(Belmakor(), slotLevel: 1);

        Assert.Equal(1, result.Sheet.SpellSlots["1"].Used);
        Assert.Equal(["1st-level slots: 1 used, 3/4 left."], result.Notes);
        Assert.Equal("""{"1":{"used":1}}""", result.Diff.Patches[SheetColumns.SpellSlots].ToJsonString());
    }

    [Fact]
    public void Use_NegativeAmount_Restores()
    {
        var sheet = SheetUse.Apply(Belmakor(), resource: "Bladesong", amount: 2).Sheet;

        var result = SheetUse.Apply(sheet, resource: "bladesong", amount: -1);

        Assert.Equal(1, result.Sheet.Resources["bladesong"].Used);
        Assert.Equal(["Bladesong: 1 restored, 3/4 left."], result.Notes);
    }

    [Theory]
    [InlineData("Arcane Recovery")]
    [InlineData("arcane-recovery")]
    [InlineData("ARCANE recovery")]
    [InlineData("arcane")]
    public void Use_ResourceByNameSlugOrUniquePrefix_IsFound(string name)
    {
        Assert.Equal(1, SheetUse.Apply(Belmakor(), resource: name).Sheet.Resources["arcane-recovery"].Used);
    }

    [Fact]
    public void Use_Pact_SpendsPactSlots()
    {
        var sheet = Create("""{ "classes": [{ "class": "warlock", "level": 5 }] }""");

        var result = SheetUse.Apply(sheet, pact: true);

        Assert.Equal(new SpellSlotEntry(2, 1, 3), result.Sheet.SpellSlots["pact"]);
        Assert.Equal(["Pact Magic slots: 1 used, 1/2 left."], result.Notes);
    }

    [Theory]
    [InlineData(null, false, null, 1, "give exactly one of slot_level (1-9), pact (true) or resource")]
    [InlineData(1, true, null, 1, "give exactly one of slot_level")]
    [InlineData(1, false, null, 0, "amount is 0; give 1 to 999 uses spent")]
    // Math.Abs(int.MinValue) has no int: the refusal, never an OverflowException.
    [InlineData(1, false, null, int.MinValue, "amount is -2147483648; give 1 to 999 uses spent")]
    [InlineData(7, false, null, 1, "the sheet has no 7th-level slots (its slots: 1st 4, 2nd 3, 3rd 3, 4th 3, 5th 2, 6th 1); give the slots with update first.")]
    [InlineData(10, false, null, 1, "slot_level is 10; it is 1 to 9.")]
    [InlineData(null, true, null, 1, "the sheet has no Pact Magic slots")]
    [InlineData(6, false, null, 2, "6th-level slots: only 1 of 1 left; cannot spend 2.")]
    [InlineData(6, false, null, -1, "6th-level slots: only 0 used; cannot restore 1.")]
    [InlineData(null, false, "Contingency", 1, "Contingency is a tracker (state \"set\"), not counted uses: change its state with update.")]
    [InlineData(null, false, "Rage", 1, "resource \"Rage\" is not on the sheet (its resources: Bladesong, Arcane Recovery, Contingency); add it with update.")]
    public void Use_Impossible_IsRefused(int? slotLevel, bool pact, string? resource, int amount, string expected)
    {
        var message = Assert.Throws<DndInputException>(() => SheetUse.Apply(Belmakor(), slotLevel, pact, resource, amount)).Message;

        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_ResourceStoredUnderAnotherKey_IsFoundByItsName()
    {
        var sheet = SheetJson.FromColumns(new Dictionary<string, object?>
        {
            [SheetColumns.EntityId] = "e1",
            [SheetColumns.Resources] = """{"r1":{"name":"Rage","max":3,"used":0,"recharge":"long_rest"},"r2":{"name":"Second Wind","max":1,"used":0}}""",
        });

        var result = SheetUse.Apply(sheet, resource: "rage");

        Assert.Equal(1, result.Sheet.Resources["r1"].Used);
        Assert.Equal("""{"r1":{"used":1}}""", result.Diff.Patches[SheetColumns.Resources].ToJsonString());
    }

    [Fact]
    public void Use_AmbiguousPrefix_ListsTheMatches()
    {
        var sheet = Update(Belmakor(), """{ "resources": [{ "name": "Arcane Ward", "max": 1 }] }""");

        var message = Assert.Throws<DndInputException>(() => SheetUse.Apply(sheet, resource: "arcane")).Message;

        Assert.Equal("resource \"arcane\" matches several: Arcane Recovery, Arcane Ward; give the full name.", message);
    }

    [Fact]
    public void Conditions_AddSrdAndEffect_StoresUntilRemoved()
    {
        var result = SheetConditions.Apply(Belmakor(), ["Poisoned", "Mummy rot"], null, null, E2014);

        Assert.Equal(
            """[{"name":"poisoned","duration":"until_removed"},{"name":"Mummy rot","duration":"until_removed"}]""",
            Text(result.Sheet, SheetColumns.Conditions));
        Assert.Contains("\"Mummy rot\" is not an SRD condition: tracked as an effect.", result.Notes);
        Assert.Equal([SheetColumns.Conditions], result.Diff.ChangedColumns);
    }

    [Theory]
    [InlineData("concentration")]
    [InlineData("Concentration")]
    [InlineData(" CONCENTRATION ")]
    public void Conditions_AddConcentration_IsRefusedPointingAtTheColumn_NothingIsStored(string name)
    {
        // C06: stored as an effect, "concentration" could never be removed: remove ["concentration"] means the column.
        var ex = Assert.Throws<DndInputException>(() => SheetConditions.Apply(Belmakor(), ["poisoned", name], null, null, E2014));

        Assert.Equal(
            "Invalid condition: add item 2: \"concentration\" is not a condition: the sheet holds a concentration in its own column " +
            "(a fight's write-back sets it; {\"remove\": [\"concentration\"]} ends it).",
            ex.Message);
    }

    [Fact]
    public void Conditions_Remove_MatchesForgivingly()
    {
        var sheet = SheetConditions.Apply(Belmakor(), ["poisoned", "Cursed (Mucus Cloud)"], null, null, E2014).Sheet;

        var after = SheetConditions.Apply(sheet, null, ["POISONED", "cursed mucus cloud"], null, E2014).Sheet;

        Assert.Empty(after.Conditions);
    }

    [Fact]
    public void Conditions_RemoveThenAdd_AppliesRemovalsFirst()
    {
        var sheet = SheetConditions.Apply(Belmakor(), ["poisoned"], null, null, E2014).Sheet;

        var after = SheetConditions.Apply(sheet, ["poisoned"], ["poisoned"], null, E2014).Sheet;

        Assert.Single(after.Conditions);
    }

    [Fact]
    public void Conditions_Exhaustion_IsTheColumnByLevels()
    {
        var result = SheetConditions.Apply(Bjorn(), ["exhaustion"], null, 2, E2024);

        Assert.Equal(2, result.Sheet.Exhaustion);
        Assert.Empty(result.Sheet.Conditions);
        Assert.Contains("Exhaustion 0 → 2.", result.Notes);
        Assert.Equal(1, SheetConditions.Apply(result.Sheet, null, ["Exhaustion"], null, E2024).Sheet.Exhaustion);
    }

    [Theory]
    [InlineData(3, 2, 1)]
    [InlineData(3, 5, 0)]
    [InlineData(6, 1, 5)]
    public void Conditions_RemoveExhaustionByLevels_DropsThatMany(int from, int levels, int expected)
    {
        var result = SheetConditions.Apply(Bjorn() with { Exhaustion = from }, null, ["exhaustion"], levels, E2024);

        Assert.Equal(expected, result.Sheet.Exhaustion);
        Assert.Contains($"Exhaustion {from} → {expected}.", result.Notes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Conditions_ImmuneToExhaustion_IsRefusedBeforeAnythingElse(int exhaustion)
    {
        var sheet = Update(Bjorn(), """{ "defenses": { "condition_immune": ["Exhaustion"] } }""", E2024) with { Exhaustion = exhaustion };

        var message = Assert.Throws<DndInputException>(() => SheetConditions.Apply(sheet, ["exhaustion"], null, 2, E2024)).Message;

        Assert.Equal("Invalid condition: add item 1: the sheet is immune to exhaustion (defenses.condition_immune).", message);
    }

    [Theory]
    [InlineData("Unconscious", "unconscious (incapacitated)")]
    [InlineData("incapacitated", "incapacitated")]
    [InlineData("stunned", "stunned (incapacitated)")]
    [InlineData("Paralyzed", "paralyzed (incapacitated)")]
    [InlineData("petrified", "petrified (incapacitated)")]
    public void Conditions_Incapacitating_EndsConcentration(string condition, string reason)
    {
        var sheet = Belmakor() with { Concentration = new SheetConcentration("Circle of Power", 5, 98, null) };

        var result = SheetConditions.Apply(sheet, ["poisoned", condition], null, null, E2014);

        Assert.Null(result.Sheet.Concentration);
        Assert.Contains($"Concentration on Circle of Power ended: {reason}.", result.Notes);
        Assert.Equal([SheetColumns.Conditions, SheetColumns.Concentration], result.Diff.ChangedColumns);
    }

    [Theory]
    [InlineData("poisoned")]
    [InlineData("prone")]
    [InlineData("Mummy rot")]
    public void Conditions_NotIncapacitating_KeepsConcentration(string condition)
    {
        var sheet = Belmakor() with { Concentration = new SheetConcentration("Circle of Power", 5, 98, null) };

        Assert.Equal("Circle of Power", SheetConditions.Apply(sheet, [condition], null, null, E2014).Sheet.Concentration?.Spell);
    }

    [Fact]
    public void Conditions_ExhaustionSix_EndsConcentration()
    {
        var sheet = Bjorn() with { Exhaustion = 5, Concentration = new SheetConcentration("Hex", 1, null, null) };

        var result = SheetConditions.Apply(sheet, ["exhaustion"], null, null, E2024);

        Assert.Null(result.Sheet.Concentration);
        Assert.Contains("Concentration on Hex ended: exhaustion 6, the character died.", result.Notes);
        Assert.NotNull(SheetConditions.Apply(sheet with { Exhaustion = 4 }, ["exhaustion"], null, null, E2024).Sheet.Concentration);
    }

    [Fact]
    public void Conditions_ExhaustionSix_CapsAndSaysTheCharacterDies()
    {
        var result = SheetConditions.Apply(Bjorn() with { Exhaustion = 5 }, ["exhaustion"], null, 3, E2024);

        Assert.Equal(6, result.Sheet.Exhaustion);
        Assert.True(result.Sheet.IsDead(E2024));
        Assert.Equal(SheetValues.ReminderKinds.Died, Assert.Single(result.Reminders).Kind);
    }

    [Fact]
    public void Conditions_2014ExhaustionFour_HalvesTheMaximumAndLowersHp()
    {
        var result = SheetConditions.Apply(Belmakor() with { Exhaustion = 3 }, ["exhaustion"], null, null, E2014);

        Assert.Equal(55, result.Sheet.Hp);
        Assert.Contains("Hit point maximum now 55: hp 110 → 55.", result.Notes);
    }

    [Theory]
    [InlineData(new[] { "poisoned" }, null, null, "the sheet is immune to poisoned (defenses.condition_immune).")]
    [InlineData(new[] { "stunned" }, null, null, "the sheet already has \"stunned\".")]
    [InlineData(new[] { "frightened" }, null, 2, "level is for exhaustion only")]
    [InlineData(new[] { "exhaustion" }, null, 7, "level is 7; exhaustion levels are 1 to 6.")]
    [InlineData(null, new[] { "blinded" }, null, "remove item 1: the sheet has no \"blinded\" (its conditions: stunned).")]
    [InlineData(null, new[] { "exhaustion" }, null, "remove: the sheet has no exhaustion.")]
    [InlineData(new[] { "" }, null, null, "add item 1 must be a condition or effect name")]
    public void Conditions_Impossible_IsRefused(string[]? add, string[]? remove, int? level, string expected)
    {
        var sheet = Update(Belmakor(), """{ "defenses": { "condition_immune": ["poisoned"] } }""");
        sheet = SheetConditions.Apply(sheet, ["stunned"], null, null, E2014).Sheet;

        var message = Assert.Throws<DndInputException>(() => SheetConditions.Apply(sheet, add, remove, level, E2014)).Message;

        Assert.StartsWith("Invalid condition", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conditions_NothingGiven_IsRefused()
    {
        Assert.Throws<DndInputException>(() => SheetConditions.Apply(Belmakor(), [], null, null, E2014));
    }

    [Fact]
    public void Xp_ASheetWithNoXpTotal_StartsAtZero_NeverCalledMilestone()
    {
        // U02: a sheet with no XP is "no XP total"; "milestone levelling" read as a policy the DM had chosen.
        var result = SheetXp.Apply(Belmakor(), 2_400);

        Assert.Equal(2_400, result.Sheet.Xp);
        Assert.Equal("The sheet had no XP total; it starts at 0.", result.Notes[0]);
        Assert.Contains("XP 0 → 2,400 (+2,400).", result.Notes);
        Assert.Equal(2_400L, result.Diff.Columns[SheetColumns.Xp]);
    }

    [Fact]
    public void Xp_ReachingTheNextLevel_RemindsWithTheLevelUpCall()
    {
        var result = SheetXp.Apply(Bjorn(), 14_000);

        Assert.Equal(48_000, result.Sheet.Xp);
        Assert.Equal(8, result.Sheet.Level);
        var reminder = Assert.Single(result.Reminders);
        Assert.Equal("48,000 XP reaches level 9 (48,000 XP): level up with level_up.", reminder.Text);
        Assert.Equal("""campaign_character {"action": "level_up", "character": "character:bjorn-mountainfell"}""", reminder.CallFor("character:bjorn-mountainfell"));
    }

    [Fact]
    public void Xp_SeveralLevelsOnAMulticlass_SaysSoAndAsksForAClass()
    {
        var sheet = Update(Minimal("""[{ "class": "ranger", "level": 1 }, { "class": "rogue", "level": 1 }]"""), """{ "xp": 300 }""");

        var reminder = Assert.Single(SheetXp.Apply(sheet, 2_400).Reminders);

        Assert.Equal("2,700 XP reaches level 4 (2,700 XP): level up with level_up, once per level (2 levels).", reminder.Text);
        Assert.Equal("""campaign_character {"action": "level_up", "character": "character:vars", "class": "…"}""", reminder.CallFor("character:vars"));
    }

    [Fact]
    public void Xp_LevelOnlySheet_RemindsWithTheUpdateThatGivesTheLevel()
    {
        var sheet = Create("""{ "level": 5 }""");

        var reminder = Assert.Single(SheetXp.Apply(sheet, 23_000).Reminders);

        Assert.Equal(
            "23,000 XP reaches level 7 (23,000 XP): the sheet has a level and no classes, so give the new level with update (or its classes, for level_up).",
            reminder.Text);
        Assert.Equal("""campaign_character {"action": "update", "character": "character:serif", "sheet": {"level": 7}}""", reminder.CallFor("character:serif"));
    }

    [Fact]
    public void Xp_BelowTheNextThreshold_SaysWhereItIs()
    {
        Assert.Contains("Level 9 at 48,000 XP.", SheetXp.Apply(Bjorn(), 100).Notes);
    }

    [Theory]
    [InlineData(0, "amount is 0")]
    [InlineData(-34_001, "amount is -34,001, but the sheet has 34,000 XP: XP cannot go below 0.")]
    [InlineData(10_000_001, "amount is 10,000,001; give the XP gained")]
    public void Xp_Impossible_IsRefused(int amount, string expected)
    {
        Assert.Contains(expected, Assert.Throws<DndInputException>(() => SheetXp.Apply(Bjorn(), amount)).Message, StringComparison.Ordinal);
    }
}
