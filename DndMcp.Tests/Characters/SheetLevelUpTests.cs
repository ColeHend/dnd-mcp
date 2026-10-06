using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using DndMcp.Domain.Features;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: a level-up adds one level to one class, the given HP or the fixed value (2024: at least 1) to the maximum
/// and to current HP (never above the effective maximum), Hit Dice and a single-class caster's slots from the tables, a
/// warlock's Pact Magic by its warlock level, never multiclass per-level slots (a reminder instead), never touches the
/// slots of a class that does not cast, and reminds of the Ability Score Improvement / Epic Boon levels per edition, of
/// resources every time, and of a sim_profile that no longer validates.
/// </summary>
public sealed class SheetLevelUpTests
{
    [Fact]
    public void Apply_SingleClassFixedHp_RaisesEverythingTheTableGives()
    {
        var sheet = SheetUse.Apply(Belmakor(), slotLevel: 1).Sheet with { Hp = 100 };

        var result = SheetLevelUp.Apply(sheet, null, null, E2014);
        var after = result.Sheet;

        Assert.Equal(13, after.Level);
        Assert.Equal(13, after.Classes[0].Level);
        Assert.Equal(117, after.MaxHp);
        Assert.Equal(107, after.Hp);
        Assert.Equal("""{"d6":{"max":13,"used":0}}""", Text(after, SheetColumns.HitDice));
        Assert.Equal(1, after.SpellSlots["7"].Max);
        Assert.Equal(1, after.SpellSlots["1"].Used);
        Assert.Contains("Level 12 → 13: Wizard 13.", result.Notes);
        Assert.Contains("Hit point maximum 110 → 117 (the fixed d6 value 4, Con +3).", result.Notes);
        Assert.Contains("Proficiency bonus +4 → +5.", result.Notes);
        Assert.Equal([SheetValues.ReminderKinds.UpdateResources], result.Reminders.Select(r => r.Kind));
    }

    [Fact]
    public void Apply_2014ExhaustionHalvesTheMaximum_CurrentHpStaysAtTheEffectiveMaximum()
    {
        var sheet = Belmakor() with { Exhaustion = 4, Hp = 55 };

        var result = SheetLevelUp.Apply(sheet, null, null, E2014);

        Assert.Equal(117, result.Sheet.MaxHp);
        Assert.Equal(58, result.Sheet.EffectiveMaxHp(E2014));
        Assert.Equal(58, result.Sheet.Hp);
    }

    [Fact]
    public void Apply_ReducedMaximum_CurrentHpRisesByTheGainWithinIt()
    {
        var sheet = Belmakor() with { MaxHpReduction = 10, Hp = 100 };

        var result = SheetLevelUp.Apply(sheet, null, null, E2014);

        Assert.Equal(107, result.Sheet.Hp);
        Assert.Equal(107, result.Sheet.EffectiveMaxHp(E2014));
    }

    [Theory]
    [InlineData("fighter", "Eldritch Knight")]
    [InlineData("rogue", "Arcane Trickster")]
    public void Apply_NonCasterWithGivenSlots_KeepsThem(string className, string subclass)
    {
        var sheet = Create($$"""{ "classes": [{ "class": "{{className}}", "subclass": "{{subclass}}", "level": 3 }], "slots": [2], "pact": { "level": 1, "max": 1 } }""");

        var result = SheetLevelUp.Apply(sheet, null, null, E2014);

        Assert.Equal("""{"1":{"max":2,"used":0},"pact":{"level":1,"max":1,"used":0}}""", Text(result.Sheet, SheetColumns.SpellSlots));
        Assert.DoesNotContain(SheetColumns.SpellSlots, result.Diff.ChangedColumns);
    }

    [Fact]
    public void Apply_MulticlassWarlock_RaisesThePactSlotsByTheWarlockLevel()
    {
        var sheet = Minimal("""[{ "class": "fighter", "level": 2 }, { "class": "warlock", "level": 2 }]""");
        Assert.Equal("""{"pact":{"level":1,"max":2,"used":0}}""", Text(sheet, SheetColumns.SpellSlots));

        var result = SheetLevelUp.Apply(SheetUse.Apply(sheet, pact: true).Sheet, "warlock", null, E2014);

        Assert.Equal("""{"pact":{"level":2,"max":2,"used":1}}""", Text(result.Sheet, SheetColumns.SpellSlots));
        Assert.DoesNotContain(result.Reminders, r => r.Kind == SheetValues.ReminderKinds.GiveSlots);
    }

    [Fact]
    public void Apply_GivenAmount_IsAddedAsGiven()
    {
        var result = SheetLevelUp.Apply(Belmakor(), "wizard", 2, E2014);

        Assert.Equal(112, result.Sheet.MaxHp);
        Assert.Contains("Hit point maximum 110 → 112 (as given).", result.Notes);
    }

    [Theory]
    [InlineData(E2014, -2, 108)]
    [InlineData(E2014, -10, 100)]
    public void Apply_2014NegativeRoll_LowersTheMaximum(string edition, int amount, int max)
    {
        Assert.Equal(max, SheetLevelUp.Apply(Belmakor(), null, amount, edition).Sheet.MaxHp);
    }

    [Theory]
    [InlineData(E2024, 0, "amount is 0; the hit points gained this level are 1 to 100 (2024: at least 1).")]
    [InlineData(E2014, -11, "amount is -11; the hit points gained this level are -10 to 100.")]
    [InlineData(E2024, 101, "amount is 101")]
    public void Apply_AmountOutOfRange_IsRefused(string edition, int amount, string expected)
    {
        var sheet = edition == E2024 ? Bjorn() : Belmakor();

        Assert.Contains(expected, Assert.Throws<DndInputException>(() => SheetLevelUp.Apply(sheet, null, amount, edition)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_2024LowConFixedValue_IsAtLeastOne()
    {
        var sheet = Create("""{ "classes": [{ "class": "wizard", "level": 2 }], "abilities": { "con": 1 } }""", E2024);

        Assert.Equal(sheet.MaxHp + 1, SheetLevelUp.Apply(sheet, null, null, E2024).Sheet.MaxHp);
    }

    [Theory]
    [InlineData(E2014, 3, SheetValues.ReminderKinds.AbilityScoreImprovement)]
    [InlineData(E2014, 18, SheetValues.ReminderKinds.AbilityScoreImprovement)]
    [InlineData(E2024, 18, SheetValues.ReminderKinds.EpicBoon)]
    [InlineData(E2024, 15, SheetValues.ReminderKinds.AbilityScoreImprovement)]
    public void Apply_ImprovementLevel_RemindsPerEdition(string edition, int from, string kind)
    {
        var sheet = Create($$"""{ "classes": [{ "class": "wizard", "level": {{from}} }] }""", edition);

        var reminders = SheetLevelUp.Apply(sheet, null, null, edition).Reminders;

        Assert.Equal([kind, SheetValues.ReminderKinds.UpdateResources], reminders.Select(r => r.Kind));
    }

    [Fact]
    public void Apply_FighterLevelSix_IsAnImprovementLevel()
    {
        var sheet = Create("""{ "classes": [{ "class": "fighter", "level": 5 }] }""", E2024);

        var reminder = SheetLevelUp.Apply(sheet, null, null, E2024).Reminders[0];

        Assert.Equal("Fighter 6: an Ability Score Improvement (or a feat): update abilities or feats.", reminder.Text);
        Assert.Equal(
            """campaign_character {"action": "update", "character": "character:x", "sheet": {"abilities": {"…": …}} or {"feats": ["…"]}}""",
            reminder.CallFor("character:x"));
    }

    [Fact]
    public void Apply_Multiclass_NeedsTheClassAndNeverComputesSlots()
    {
        var sheet = Minimal("""[{ "class": "paladin", "level": 6 }, { "class": "sorcerer", "level": 6 }]""");

        var refusal = Assert.Throws<DndInputException>(() => SheetLevelUp.Apply(sheet, null, null, E2014)).Message;
        var result = SheetLevelUp.Apply(sheet, "Sorcerer", null, E2014);

        Assert.Equal("The sheet has several classes (paladin 6 / sorcerer 6): give class, the one that gains the level.", refusal);
        Assert.Equal(7, result.Sheet.Classes[1].Level);
        Assert.Equal(13, result.Sheet.Level);
        Assert.Empty(result.Sheet.SpellSlots);
        Assert.Contains(result.Reminders, r => r.Kind == SheetValues.ReminderKinds.GiveSlots);
        Assert.Equal("""{"d10":{"max":6,"used":0},"d6":{"max":7,"used":0}}""", Text(result.Sheet, SheetColumns.HitDice));
        Assert.Contains("No hit point maximum on the sheet: give max_hp with update.", result.Notes);
    }

    [Fact]
    public void Apply_NewSrdClass_IsAddedAtLevelOneWithTheFixedValue()
    {
        var result = SheetLevelUp.Apply(Belmakor(), "fighter", null, E2014);

        Assert.Equal("""[{"class":"wizard","subclass":"Bladesinger","level":12,"hit_die":6},{"class":"fighter","level":1,"hit_die":10}]""", Text(result.Sheet, SheetColumns.Classes));
        Assert.Equal(119, result.Sheet.MaxHp);
        Assert.Equal("""{"d10":{"max":1,"used":0},"d6":{"max":12,"used":0}}""", Text(result.Sheet, SheetColumns.HitDice));
        Assert.Equal(1, result.Sheet.SpellSlots["6"].Max);
        Assert.Contains(result.Reminders, r => r.Kind == SheetValues.ReminderKinds.GiveSlots);
    }

    [Theory]
    [InlineData("""{ "level": 5 }""", null, "level_up needs the sheet's classes")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 20 }] }""", null, "The character is level 20, the highest level.")]
    [InlineData("""{ "classes": [{ "class": "wizard", "level": 3 }] }""", "Blood Hunter", "class \"Blood Hunter\" is not on the sheet (wizard 3) and is not an SRD class")]
    public void Apply_Impossible_IsRefused(string json, string? className, string expected)
    {
        var message = Assert.Throws<DndInputException>(() => SheetLevelUp.Apply(Create(json), className, null, E2014)).Message;

        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_HomebrewClassOnTheSheet_UsesItsHitDie()
    {
        var sheet = Create("""{ "classes": [{ "class": "Dragon Slayer", "level": 8, "hit_die": 10 }], "abilities": { "con": 14 } }""", E2024);

        var result = SheetLevelUp.Apply(sheet, "dragon slayer", null, E2024);

        Assert.Equal(76, result.Sheet.MaxHp);
        Assert.Equal(["update_resources"], result.Reminders.Select(r => r.Kind));
    }

    [Fact]
    public void Apply_ZeroHp_RaisesTheMaximumOnly()
    {
        var result = SheetLevelUp.Apply(Belmakor() with { Hp = 0, DeathSaves = new SheetDeathSaves(0, 0, true) }, null, null, E2014);

        Assert.Equal(0, result.Sheet.Hp);
        Assert.Equal(117, result.Sheet.MaxHp);
    }

    [Fact]
    public void Apply_StoredProfileNoLongerValid_Reminds()
    {
        var profile = DslJson.Deserialize<BuildSpec>(
            """{ "name": "Club", "level": 3, "attacks": [{ "name": "Club", "damage": {"1": "1d4"}, "until_level": 3 }], "modifiers": [{ "kind": "extra_damage", "dice": {"1": "1d6"}, "until_level": 3 }] }""",
            "sim_profile");
        var sheet = Create("""{ "classes": [{ "class": "fighter", "level": 3 }] }""", E2014, profile);

        var result = SheetLevelUp.Apply(sheet, null, null, E2014);

        var reminder = Assert.Single(result.Reminders, r => r.Kind == SheetValues.ReminderKinds.SimProfile);
        Assert.StartsWith("The stored sim_profile does not work at level 4: Invalid sim_profile", reminder.Text, StringComparison.Ordinal);
    }
}
