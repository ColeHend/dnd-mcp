using System.Text.Json.Nodes;
using DndMcp.Domain.Characters;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: a diff names exactly the columns that changed; the four per-key columns come as merge patches that, applied
/// to the old object by RFC 7396, give exactly the new one (a removed key or nested key included), so the Repository logs
/// and undoes one key at a time; the managed columns never appear.
/// </summary>
public sealed class SheetDiffTests
{
    [Fact]
    public void Between_SameSheet_IsEmpty()
    {
        var sheet = Belmakor();

        var diff = SheetDiff.Between(sheet, sheet with { UpdatedAt = "later", CreatedAt = "earlier" });

        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Columns);
        Assert.Empty(diff.Patches);
        Assert.Empty(diff.ChangedColumns);
    }

    [Fact]
    public void Between_SameEntriesInAnotherOrder_IsEmpty()
    {
        var sheet = Belmakor() with { Skills = """{"perception":{"proficient":true},"stealth":{"expertise":true}}""" };
        var reordered = sheet with
        {
            Skills = """{"stealth":{"expertise":true},"perception":{"proficient":true}}""",
            Resources = SheetMaps.Of(sheet.Resources.Reverse()),
        };

        var diff = SheetDiff.Between(sheet, reordered);

        Assert.NotEqual(SheetJson.Column(sheet, SheetColumns.Resources), SheetJson.Column(reordered, SheetColumns.Resources));
        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Columns);
        Assert.Empty(diff.Patches);
    }

    [Fact]
    public void Between_PlainColumns_GiveTheNewStoredValues()
    {
        var sheet = Belmakor();

        var diff = SheetDiff.Between(sheet, sheet with { Hp = 82, TempHp = 0, Player = null });

        Assert.Equal([SheetColumns.Player, SheetColumns.Hp, SheetColumns.TempHp], diff.ChangedColumns);
        Assert.Equal(82L, diff.Columns[SheetColumns.Hp]);
        Assert.Equal(0L, diff.Columns[SheetColumns.TempHp]);
        Assert.Null(diff.Columns[SheetColumns.Player]);
        Assert.Contains(new SheetFieldChange(SheetColumns.Hp, null, "110", "82"), diff.Fields);
        Assert.Contains(new SheetFieldChange(SheetColumns.Player, null, "Cole", null), diff.Fields);
    }

    [Fact]
    public void Between_OneSlotLevelUsed_PatchesThatKeyOnly()
    {
        var sheet = Belmakor();
        var after = SheetUse.Apply(sheet, slotLevel: 5).Sheet;

        var diff = SheetDiff.Between(sheet, after);

        Assert.Equal([SheetColumns.SpellSlots], diff.ChangedColumns);
        Assert.Equal("""{"5":{"used":1}}""", diff.Patches[SheetColumns.SpellSlots].ToJsonString());
        Assert.Equal(new SheetFieldChange(SheetColumns.SpellSlots, "5", """{"max":2,"used":0}""", """{"max":2,"used":1}"""), Assert.Single(diff.Fields));
    }

    [Fact]
    public void Between_ResourceRemoved_PatchesItAsNull()
    {
        var sheet = Belmakor();
        var after = Update(sheet, """{ "resources": [{ "name": "Contingency", "remove": true }] }""");

        var diff = SheetDiff.Between(sheet, after);

        Assert.Equal("""{"contingency":null}""", diff.Patches[SheetColumns.Resources].ToJsonString());
        Assert.Null(Assert.Single(diff.Fields).After);
    }

    [Theory]
    [MemberData(nameof(PerKeyChanges))]
    public void Between_PerKeyColumn_PatchMergedIntoTheOldValueGivesTheNew(string column, string before, string after)
    {
        var old = SheetJson.FromColumns(new Dictionary<string, object?> { [SheetColumns.EntityId] = "e1", [column] = before });
        var now = SheetJson.FromColumns(new Dictionary<string, object?> { [SheetColumns.EntityId] = "e1", [column] = after });

        var diff = SheetDiff.Between(old, now);

        var merged = Merge(JsonNode.Parse(before), diff.Patches[column]);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(after), merged), merged?.ToJsonString());
        Assert.DoesNotContain(column, diff.Columns.Keys);
    }

    public static TheoryData<string, string, string> PerKeyChanges() => new()
    {
        { SheetColumns.SpellSlots, """{"1":{"max":4,"used":1,"note":"x"},"2":{"max":3,"used":0}}""", """{"1":{"max":4,"used":0},"3":{"max":2,"used":0}}""" },
        { SheetColumns.Resources, """{"rage":{"name":"Rage","max":4,"used":2,"recharge":"long_rest","note":"n"}}""", """{"rage":{"name":"Rage","max":5,"used":2,"recharge":"long_rest"}}""" },
        { SheetColumns.HitDice, """{"d10":{"max":6,"used":2},"d8":{"max":2,"used":0}}""", """{"d10":{"max":7,"used":2}}""" },
        { SheetColumns.Abilities, """{"str":10,"con":14}""", """{"str":10,"con":16,"wis":12}""" },
        { SheetColumns.Resources, """{"a":{"name":"A","state":"set","x":{"deep":1,"gone":2}}}""", """{"a":{"name":"A","state":"set","x":{"deep":3}}}""" },
    };

    [Fact]
    public void Between_NewSheet_DiffsAgainstTheEmptySheet()
    {
        var sheet = Belmakor();

        var diff = SheetDiff.Between(null, sheet);

        Assert.Contains(SheetColumns.Player, diff.Columns.Keys);
        Assert.Contains(SheetColumns.Resources, diff.Patches.Keys);
        Assert.DoesNotContain(SheetColumns.EntityId, diff.ChangedColumns);
        Assert.DoesNotContain(SheetColumns.DeathSaves, diff.ChangedColumns);
    }

    [Fact]
    public void Between_TwoEntities_IsAHostBug()
    {
        Assert.Throws<ArgumentException>(() => SheetDiff.Between(CharacterSheet.New("a"), CharacterSheet.New("b")));
    }

    [Fact]
    public void MergeDiff_EqualValues_IsAnEmptyPatchOrNull()
    {
        Assert.Equal("{}", SheetDiff.MergeDiff(JsonNode.Parse("""{"a":{"b":1}}"""), JsonNode.Parse("""{"a":{"b":1}}"""))!.ToJsonString());
        Assert.Null(SheetDiff.MergeDiff(JsonValue.Create(3), JsonValue.Create(3)));
        Assert.Equal("4", SheetDiff.MergeDiff(JsonValue.Create(3), JsonValue.Create(4))!.ToJsonString());
    }

    /// <summary>RFC 7396 MergePatch, written out independently of the code under test.</summary>
    private static JsonNode? Merge(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject p)
        {
            return patch?.DeepClone();
        }

        var result = target is JsonObject t ? (JsonObject)t.DeepClone() : new JsonObject();
        foreach (var (name, value) in p)
        {
            if (value is null)
            {
                result.Remove(name);
            }
            else
            {
                result[name] = Merge(result[name], value);
            }
        }

        return result;
    }
}
