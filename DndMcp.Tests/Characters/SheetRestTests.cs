using DndMcp.Domain.Characters;
using DndMcp.Domain.Core;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: rests follow contract §5.15 per edition. Short: Hit Dice spent largest first, each face + Con (2014 floored
/// at 0, 2024 at least 1), capped at the effective maximum; resources by recharge kind; Pact Magic back; an hour passes
/// for timed effects. Long: exhaustion −1 first (2014 only with food and drink), then the reduction cleared and HP to the
/// effective maximum; temp HP 0; Hit Dice 2014 half the total (at least 1) largest first, 2024 all; every slot and
/// resource but <c>none</c>; timed effects and concentration end; until_removed conditions stay, each with a reminder.
/// The rolls are inputs: the needs say what to roll.
/// </summary>
public sealed class SheetRestTests
{
    private static CharacterSheet Fighter(string edition = E2014, string extra = "") => Create($$"""
        { "classes": [{ "class": "fighter", "level": 5 }], "abilities": { "con": 14 }, "hp": 20 {{extra}},
          "resources": [
            { "name": "Second Wind", "max": 1, "used": 1, "recharge": "short_rest" },
            { "name": "Indomitable", "max": 1, "used": 1, "recharge": "long_rest" },
            { "name": "Lucky coin", "max": 3, "used": 2, "recharge": "none" },
            { "name": "Ring", "max": 1, "used": 1, "recharge": "dawn" },
            { "name": "Contingency", "state": "set" } ] }
        """, edition);

    private static SheetRestResult Short(CharacterSheet sheet, string edition = E2014, int? hitDice = null, int[]? rolls = null, int[]? rolled = null) =>
        SheetRest.Apply(sheet, new RestRequest("short", hitDice, rolls), edition, rolled);

    private static SheetRestResult Long(CharacterSheet sheet, string edition = E2014, bool ate = true) =>
        SheetRest.Apply(sheet, new RestRequest("long", AteAndDrank: ate), edition);

    private static string Refusal(CharacterSheet sheet, RestRequest request, string edition = E2014) =>
        Assert.Throws<DndInputException>(() => SheetRest.Apply(sheet, request, edition, [1, 1, 1, 1, 1, 1, 1, 1])).Message;

    [Fact]
    public void Short_2014HitDiceGiven_HealFacePlusConEachAndRecoverShortRestResources()
    {
        var sheet = Fighter();
        Assert.Equal(44, sheet.MaxHp);

        var result = Short(sheet, hitDice: 2, rolls: [1, 10]);

        Assert.Equal(35, result.Sheet.Hp);
        Assert.Equal(15, result.HitPointsRegained);
        Assert.Equal([new HitDieRoll(10, 1, 3), new HitDieRoll(10, 10, 12)], result.HitDice);
        Assert.Equal("""{"d10":{"max":5,"used":2}}""", Text(result.Sheet, SheetColumns.HitDice));
        Assert.Equal(0, result.Sheet.Resources["second-wind"].Used);
        Assert.Equal(1, result.Sheet.Resources["indomitable"].Used);
        Assert.Equal(2, result.Sheet.Resources["lucky-coin"].Used);
        Assert.Equal(1, result.Sheet.Resources["ring"].Used);
        Assert.Contains("Hit Dice spent: d10 1, d10 10, +2 Con each → +15 HP; HP 20 → 35.", result.Notes);
        Assert.Contains("Second Wind: 0/1 → 1/1.", result.Notes);
    }

    [Theory]
    [InlineData(E2014, 0)]
    [InlineData(E2024, 1)]
    public void Short_FacePlusConBelowOne_IsFlooredByEdition(string edition, int gain)
    {
        var sheet = Create("""{ "classes": [{ "class": "wizard", "level": 3 }], "abilities": { "con": 3 }, "max_hp": 10, "hp": 5 }""", edition);

        var result = Short(sheet, edition, rolls: [1]);

        Assert.Equal(gain, result.HitDice[0].Gain);
        Assert.Equal(5 + gain, result.Sheet.Hp);
    }

    [Fact]
    public void Short_HealingPastTheEffectiveMaximum_IsCapped()
    {
        var sheet = Fighter(extra: """, "max_hp_reduction": 4""");

        var result = Short(sheet, rolls: [10, 10, 10]);

        Assert.Equal(40, result.Sheet.Hp);
        Assert.Equal(20, result.HitPointsRegained);
        Assert.Contains(result.Notes, n => n.EndsWith("HP 20 → 40 (max).", StringComparison.Ordinal));
    }

    [Fact]
    public void Needs_MixedDice_OneNeedPerDieSizeLargestFirst()
    {
        var sheet = Create("""{ "classes": [{ "class": "wizard", "level": 3 }, { "class": "fighter", "level": 2 }], "max_hp": 30, "hp": 10 }""");

        var needs = SheetRest.Needs(sheet, new RestRequest("short", HitDice: 3), E2014);

        Assert.Equal(
            [new SheetRollNeed("hit-dice-d10", "hit dice", "2d10", 10, 2), new SheetRollNeed("hit-dice-d6", "hit dice", "1d6", 6, 1)],
            needs);

        var result = Short(sheet, hitDice: 3, rolled: [4, 7, 6]);
        Assert.Equal([10, 10, 6], result.HitDice.Select(d => d.Sides));
        Assert.Equal(27, result.Sheet.Hp);
        Assert.Equal("""{"d10":{"max":2,"used":2},"d6":{"max":3,"used":1}}""", Text(result.Sheet, SheetColumns.HitDice));
    }

    [Fact]
    public void Needs_FacesGivenOrNoDiceOrLongRest_AreEmpty()
    {
        var sheet = Fighter();

        Assert.Empty(SheetRest.Needs(sheet, new RestRequest("short", Rolls: [3]), E2014));
        Assert.Empty(SheetRest.Needs(sheet, new RestRequest("short"), E2014));
        Assert.Empty(SheetRest.Needs(sheet, new RestRequest("Long Rest"), E2014));
    }

    [Fact]
    public void Apply_HitDiceWithoutFaces_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => SheetRest.Apply(Fighter(), new RestRequest("short", HitDice: 2), E2014));
    }

    [Theory]
    [InlineData(6, null, "hit_dice is 6, but only 5 Hit Dice are left (5d10).")]
    [InlineData(2, new[] { 3 }, "hit_dice is 2 but rolls has 1 faces; give one face per Hit Die spent.")]
    [InlineData(null, new[] { 11 }, "rolls item 1 is 11; it is the face of a d10, 1 to 10 (Hit Dice are spent largest first: d10).")]
    [InlineData(null, new[] { 5, 0 }, "rolls item 2 is 0; it is the face of a d10, 1 to 10")]
    [InlineData(-1, null, "hit_dice is -1; spend 0 to 20 Hit Dice.")]
    public void Short_BadHitDice_IsRefused(int? hitDice, int[]? rolls, string expected)
    {
        Assert.Contains(expected, Refusal(Fighter(), new RestRequest("short", hitDice, rolls)), StringComparison.Ordinal);
    }

    [Fact]
    public void Short_NoHitDiceLeft_SaysNone()
    {
        var sheet = Short(Fighter(), rolls: [1, 1, 1, 1, 1]).Sheet;

        Assert.Contains("only 0 Hit Dice are left (none)", Refusal(sheet, new RestRequest("short", 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Short_HitPointsNotTracked_RefusesHitDiceButStillRests()
    {
        var sheet = Minimal("""[{ "class": "fighter", "level": 5 }]""") with
        {
            Resources = SheetMaps.Of([new KeyValuePair<string, SheetResource>("x", new SheetResource("X", 1, 1, "short_rest", null, null))]),
        };

        Assert.Contains("The sheet tracks no hit points", Refusal(sheet, new RestRequest("short", 1)), StringComparison.Ordinal);
        Assert.Equal(0, Short(sheet).Sheet.Resources["x"].Used);
    }

    [Fact]
    public void Short_2024AtZeroHp_IsRefused()
    {
        var sheet = Bjorn() with { Hp = 0, DeathSaves = new SheetDeathSaves(0, 0, true) };

        Assert.Equal("A short rest needs at least 1 hit point (2024); the character has 0.", Refusal(sheet, new RestRequest("short"), E2024));
    }

    [Fact]
    public void Short_2014DyingAtZero_IsRefused()
    {
        var sheet = Fighter() with { Hp = 0, DeathSaves = new SheetDeathSaves(1, 1, false) };

        Assert.Equal("The character is dying at 0 hit points: stabilise it first.", Refusal(sheet, new RestRequest("short")));
    }

    [Fact]
    public void Short_2014StableAtZero_SpendsHitDiceAndWakes()
    {
        var sheet = Fighter() with { Hp = 0, DeathSaves = new SheetDeathSaves(0, 0, true) };

        var result = Short(sheet, rolls: [4]);

        Assert.Equal(6, result.Sheet.Hp);
        Assert.True(result.Sheet.DeathSaves.IsReset);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    public void Rest_Dead_IsRefused(string kind)
    {
        Assert.Equal("The character is dead: it cannot rest.", Refusal(Fighter() with { DeathSaves = new SheetDeathSaves(0, 3, false), Hp = 0 }, new RestRequest(kind)));
        Assert.Equal("The character is dead: it cannot rest.", Refusal(Fighter() with { Exhaustion = 6 }, new RestRequest(kind)));
    }

    [Fact]
    public void Short_ShortRestOneResourceAndPact_ComeBack()
    {
        var sheet = SheetUse.Apply(Bjorn(), resource: "Rage", amount: 3).Sheet;
        var warlock = SheetUse.Apply(Create("""{ "classes": [{ "class": "warlock", "level": 5 }] }"""), pact: true, amount: 2).Sheet;

        Assert.Equal(2, Short(sheet, E2024).Sheet.Resources["rage"].Used);
        Assert.Equal(0, Short(warlock).Sheet.SpellSlots["pact"].Used);
        Assert.Contains("Pact Magic slots restored.", Short(warlock).Notes);
    }

    [Fact]
    public void Short_AnHourPasses_EndsShortEffectsAndShortensLongOnes()
    {
        var sheet = Fighter() with
        {
            Conditions =
            [
                new SheetCondition("Bless", null, "rounds", 9, null),
                new SheetCondition("Aid", null, "rounds", 600, null),
                new SheetCondition("Mage Armor", null, "rounds", 4800, null),
                new SheetCondition("cursed", "Aboleth", "until_removed", null, null),
            ],
            Concentration = new SheetConcentration("Circle of Power", 5, 98, null),
        };

        var result = Short(sheet);

        Assert.Equal(
            """[{"name":"Mage Armor","duration":"rounds","remaining_rounds":4200},{"name":"cursed","source":"Aboleth","duration":"until_removed"}]""",
            Text(result.Sheet, SheetColumns.Conditions));
        Assert.Null(result.Sheet.Concentration);
        Assert.Contains("Concentration on Circle of Power ended.", result.Notes);
        Assert.Contains("Bless ended.", result.Notes);
    }

    [Fact]
    public void Short_LongConcentration_LosesAnHour()
    {
        var sheet = Fighter() with { Concentration = new SheetConcentration("Hunter's Mark", 1, 601, null) };

        Assert.Equal(1, Short(sheet).Sheet.Concentration!.RemainingRounds);
    }

    [Fact]
    public void Long_2014_RestoresEverythingButKeepsUntilRemovedWithAReminder()
    {
        var sheet = SheetUse.Apply(Belmakor(), slotLevel: 5).Sheet;
        sheet = SheetUse.Apply(sheet, resource: "Bladesong").Sheet with
        {
            Hp = 30,
            MaxHpReduction = 10,
            Exhaustion = 2,
            HitDice = SheetMaps.Of([new KeyValuePair<string, HitDiceEntry>("d6", new HitDiceEntry(12, 10))]),
            Conditions = [new SheetCondition("mummy rot", "Mummy Lord", "until_removed", null, null), new SheetCondition("Mage Armor", null, "rounds", 4000, null)],
            Concentration = new SheetConcentration("Circle of Power", 5, 98, null),
            DeathSaves = new SheetDeathSaves(1, 0, false),
        };

        var result = Long(sheet);
        var after = result.Sheet;

        Assert.Equal(1, after.Exhaustion);
        Assert.Equal(0, after.MaxHpReduction);
        Assert.Equal(110, after.Hp);
        Assert.Equal(0, after.TempHp);
        Assert.Equal(4, after.HitDice["d6"].Used);
        Assert.All(after.SpellSlots.Values, s => Assert.Equal(0, s.Used));
        Assert.Equal(0, after.Resources["bladesong"].Used);
        Assert.Equal("set", after.Resources["contingency"].State);
        Assert.Equal(["mummy rot"], after.Conditions.Select(c => c.Name));
        Assert.Null(after.Concentration);
        Assert.True(after.DeathSaves.IsReset);
        var reminder = Assert.Single(result.Reminders);
        Assert.Equal(SheetValues.ReminderKinds.CheckCondition, reminder.Kind);
        Assert.Contains("mummy rot stays (until removed): check whether it stops this rest's recovery", reminder.Text, StringComparison.Ordinal);
        Assert.Contains("Hit Dice regained: 6 of 10 spent.", result.Notes);
        Assert.Equal(80, result.HitPointsRegained);
    }

    [Fact]
    public void Long_2014Exhaustion4_DropsFirstSoTheMaximumIsNoLongerHalved()
    {
        var sheet = Belmakor() with { Exhaustion = 4, Hp = 40 };

        Assert.Equal(55, sheet.EffectiveMaxHp(E2014));
        var after = Long(sheet).Sheet;

        Assert.Equal(3, after.Exhaustion);
        Assert.Equal(110, after.Hp);
    }

    [Fact]
    public void Long_2014WithoutFoodAndDrink_KeepsExhaustion()
    {
        var result = Long(Belmakor() with { Exhaustion = 4, Hp = 40 }, ate: false);

        Assert.Equal(4, result.Sheet.Exhaustion);
        Assert.Equal(55, result.Sheet.Hp);
        Assert.Contains("Exhaustion unchanged: no food and drink (2014).", result.Notes);
    }

    [Fact]
    public void Long_2024_ExhaustionDropsAnywayAndEveryHitDieComesBack()
    {
        var sheet = Bjorn() with
        {
            Exhaustion = 1,
            HitDice = SheetMaps.Of([new KeyValuePair<string, HitDiceEntry>("d12", new HitDiceEntry(8, 8))]),
        };

        var after = Long(sheet, E2024, ate: false).Sheet;

        Assert.Equal(0, after.Exhaustion);
        Assert.Equal(0, after.HitDice["d12"].Used);
    }

    [Fact]
    public void Long_2014HalfTheTotal_LargestDieFirst()
    {
        var sheet = Create("""{ "classes": [{ "class": "rogue", "level": 6 }, { "class": "ranger", "level": 6 }], "max_hp": 80 }""") with
        {
            HitDice = SheetMaps.Of(
            [
                new KeyValuePair<string, HitDiceEntry>("d10", new HitDiceEntry(6, 4)),
                new KeyValuePair<string, HitDiceEntry>("d8", new HitDiceEntry(6, 6)),
            ]),
        };

        var after = Long(sheet).Sheet;

        Assert.Equal(0, after.HitDice["d10"].Used);
        Assert.Equal(4, after.HitDice["d8"].Used);
    }

    [Fact]
    public void Long_2014LevelOne_RegainsAtLeastOneDie()
    {
        var sheet = Create("""{ "classes": [{ "class": "wizard", "level": 1 }], "max_hp": 6 }""") with
        {
            HitDice = SheetMaps.Of([new KeyValuePair<string, HitDiceEntry>("d6", new HitDiceEntry(1, 1))]),
        };

        Assert.Equal(0, Long(sheet).Sheet.HitDice["d6"].Used);
    }

    [Theory]
    [InlineData(E2014)]
    [InlineData(E2024)]
    public void Long_AtZeroHp_IsRefusedInBothEditions(string edition)
    {
        var sheet = Fighter(edition) with { Hp = 0, DeathSaves = new SheetDeathSaves(0, 0, true) };

        Assert.Equal("A long rest needs at least 1 hit point; the character has 0.", Refusal(sheet, new RestRequest("long"), edition));
    }

    [Fact]
    public void Long_WithHitDice_IsRefused()
    {
        Assert.StartsWith("hit_dice and rolls are for a short rest", Refusal(Fighter(), new RestRequest("long", 1)), StringComparison.Ordinal);
    }

    [Fact]
    public void Rest_UnknownKind_IsRefused()
    {
        Assert.Equal("kind \"nap\" is not a rest; give \"short\" or \"long\".", Refusal(Fighter(), new RestRequest("nap")));
    }

    [Fact]
    public void Long_UntrackedHitPoints_StayUntracked()
    {
        var sheet = Minimal("""[{ "class": "bard", "level": 12 }]""");

        var after = Long(sheet).Sheet;

        Assert.Null(after.Hp);
        Assert.Null(after.MaxHp);
    }

    [Fact]
    public void Long_ResourcesByRecharge_NoneStaysSpent()
    {
        var after = Long(Fighter()).Sheet;

        Assert.Equal(0, after.Resources["second-wind"].Used);
        Assert.Equal(0, after.Resources["indomitable"].Used);
        Assert.Equal(0, after.Resources["ring"].Used);
        Assert.Equal(2, after.Resources["lucky-coin"].Used);
    }
}
