using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: a combatant row round-trips through <see cref="CombatJson"/> column for column (the stat block snapshot, the
/// sheet snapshot with its facts, every condition shape, concentration, legendary, resources), unknown keys inside every
/// object are kept and written back, a stored value of the wrong type is refused naming the column without quoting stored
/// text, and the writes are deterministic (the same state writes the same text, which <see cref="EncounterState.ChangedCombatants"/>
/// compares).
/// </summary>
public sealed class CombatJsonTests
{
    [Fact]
    public void Columns_EveryCombatantOfBothFixtures_RoundTripsByteForByte()
    {
        var a = Add(Encounter(E2014), [.. PartyA()]).Next;
        a = Add(a, Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), Monster(Block(E2014, "lich"), hp: HpChoice.Avg)).Next;
        a = Step(a, new ConditionOp(["torch"]) { Add = ["frightened"], Duration = "1 minute", Source = "lich", Dc = 13, Ability = "wis", Effect = new EffectInput(Ac: -2, Resist: ["all"], Except: ["fire"]) }).Next;
        a = Step(a, new ConcentrationOp(["belmakor"]) { Spell = "Circle of Power", SlotLevel = 5, Duration = "10 minutes" }).Next;
        a = Step(a, new DamageOp(["belmakor"]) { Amount = 12 }).Next;
        foreach (var c in a.Combatants)
        {
            var row = CombatJson.Columns(c, a.Id);
            Assert.Equal(CombatantColumns.All, row.Keys);
            var back = CombatJson.FromColumns(row, c.EntityHandle, c.EntitySubtype);
            Assert.True(CombatJson.SameState(c, back), c.Name);
            Assert.Equal(row, CombatJson.Columns(back, a.Id));
        }
    }

    [Fact]
    public void Conditions_UnknownKeysAtEveryDepth_AreKeptOnRewrite()
    {
        const string stored = """[{"id":"c1","name":"frightened","duration":"rounds","expires":{"round":11,"at":"start","of":"x","tz":1},"save":{"ability":"wis","dc":13,"adv":true},"effect":{"ac":2,"glow":"blue"},"applied":{"round":1,"turn_of":"x","by":"dm"},"later":{"a":1}}]""";
        var read = CombatJson.ReadConditions(stored);
        Assert.Equal(stored, CombatJson.WriteConditions(read));
        var changed = read.Select(c => c with { SkipEnd = true }).ToList();
        Assert.Contains("\"later\":{\"a\":1}", CombatJson.WriteConditions(changed), StringComparison.Ordinal);
    }

    [Fact]
    public void Resources_LegendaryConcentration_UnknownKeysKept()
    {
        var resources = CombatJson.ReadResources("""{"limited:Fire Breath":{"name":"Fire Breath","kind":"recharge","min":5,"ready":false,"later":2}}""");
        Assert.Contains("\"later\":2", CombatJson.WriteResources(resources), StringComparison.Ordinal);
        var legendary = CombatJson.ReadLegendary("""{"actions":3,"used":1,"resistance":0,"resistance_used":0,"lair_used":true}""")!;
        Assert.Equal(2, legendary.ActionsLeft);
        Assert.Contains("\"lair_used\":true", CombatJson.WriteLegendary(legendary), StringComparison.Ordinal);
        var concentration = CombatJson.ReadConcentration("""{"spell":"Bless","pending":[12,10],"tag":"x"}""")!;
        Assert.Equal([12, 10], concentration.Pending);
        Assert.Contains("\"tag\":\"x\"", CombatJson.WriteConcentration(concentration), StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_NestedJsonColumnsAndFacts_ReadBackAsStoredValues_TheSheetCanBeRebuilt()
    {
        var sheet = Belmakor();
        var c = CombatantFactory.FromSheet(sheet, "x", "Belmakor", E2014);
        var text = CombatJson.WriteSnapshot(c.SheetSnapshot)!;
        Assert.Contains("\"spell_slots\":{\"1\":{\"max\":4,\"used\":1}", text, StringComparison.Ordinal);
        Assert.Contains("\"facts\":{\"defenses\":{},\"con_save\":7}", text, StringComparison.Ordinal);
        var back = CombatJson.ReadSnapshot(text)!;
        foreach (var column in SheetSnapshot.Captured)
        {
            Assert.Equal(SheetJson.Column(sheet, column), back.Column(column));
        }

        Assert.Equal(7, back.ConSaveBonus);
        var rebuilt = SheetJson.FromColumns(new Dictionary<string, object?>(back.Columns) { [SheetColumns.EntityId] = "e-belmakor" });
        Assert.Equal(SheetJson.Column(sheet, SheetColumns.Resources), SheetJson.Column(rebuilt, SheetColumns.Resources));
    }

    [Theory]
    [InlineData(CombatantColumns.Conditions, "{}")]
    [InlineData(CombatantColumns.Conditions, "[{\"name\":\"x\"}]")]
    [InlineData(CombatantColumns.Conditions, "[{\"id\":\"c1\",\"name\":\"secret plan\",\"duration\":7}]")]
    [InlineData(CombatantColumns.Resources, "[]")]
    [InlineData(CombatantColumns.Legendary, "{\"used\":\"two\"}")]
    [InlineData(CombatantColumns.Concentration, "{\"spell\":\"Bless\",\"pending\":\"soon\"}")]
    [InlineData(CombatantColumns.DeathSaves, "not json")]
    [InlineData(CombatantColumns.SheetSnapshot, "{\"facts\":3}")]
    public void FromColumns_AWrongType_IsRefusedNamingTheColumnWithoutQuotingIt(string column, string value)
    {
        var row = new Dictionary<string, object?>(CombatJson.Columns(Fight(E2024, ("A", 1)).Combatants[0], "enc")) { [column] = value };
        var ex = Assert.Throws<InvalidDataException>(() => CombatJson.FromColumns(row));
        Assert.Contains(column, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret plan", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromColumns_AJsonElementOrAMissingId_IsAHostBug()
    {
        var row = new Dictionary<string, object?>(CombatJson.Columns(Fight(E2024, ("A", 1)).Combatants[0], "enc"));
        Assert.Throws<ArgumentException>(() => CombatJson.FromColumns(new Dictionary<string, object?>(row) { [CombatantColumns.Id] = null }));
        Assert.Throws<ArgumentException>(() => CombatJson.FromColumns(new Dictionary<string, object?>(row) { [CombatantColumns.Hp] = 3.5 }));
    }

    [Fact]
    public void DeathSaves_MissingKeysReadAsReset_WrittenWithAllThree()
    {
        Assert.Equal(new DeathSaveTally(0, 2, false), CombatJson.ReadDeathSaves("""{"failures":2}"""));
        Assert.Equal("""{"successes":0,"failures":0,"stable":false}""", CombatJson.WriteDeathSaves(DeathSaveTally.Zero));
    }

    [Fact]
    public void Turn_RoundTripsWithItsRestore_AndANonTurnDetailReadsAsNull()
    {
        var turn = new TurnRecord
        {
            From = "a",
            To = "b",
            RoundBefore = 1,
            Round = 2,
            Wrapped = true,
            Restore = [new TurnRestore("c", [new CombatCondition("c1", "prone", CombatValues.Durations.UntilStands)], new CombatConcentration("Bless"), new LegendaryState(3, 1, 0, 0), true, false)],
            Changes = ["Round 2"],
        };
        var back = CombatJson.ReadTurn(CombatJson.WriteTurn(turn))!;
        Assert.Equal(CombatJson.WriteTurn(turn), CombatJson.WriteTurn(back));
        Assert.Null(CombatJson.ReadTurn("""{"parts":[]}"""));
        Assert.Null(CombatJson.ReadTurn(null));
        Assert.Null(CombatJson.ReadTurn("{nope"));
    }

    [Fact]
    public void Columns_AreTheRepositoryRowsColumnsInOrder()
    {
        Assert.Equal(DndMcp.Repository.Campaign.CombatantRow.Columns.Split(", "), CombatantColumns.All);
    }

    [Fact]
    public void ItemPrefix_IsTheOneTheUndoGuardMatches()
    {
        var field = typeof(DndMcp.Repository.Campaign.UndoEngine).GetField("ItemResourcePrefix",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.Equal(CombatValues.ResourceKeys.ItemPrefix, (string)field!.GetValue(null)!);
        Assert.Equal("item:h-1", CombatValues.ResourceKeys.Item("h-1"));
    }

    [Fact]
    public void ChangedCombatants_OnlyTheRowsAStepChanged()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        var result = Step(s, new DamageOp(["B"]) { Amount = 3 });
        Assert.Equal([s.Named("B").Id], result.ChangedCombatants);
        Assert.Empty(EncounterState.ChangedCombatants(s, s));
    }
}
