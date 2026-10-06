using System.Text.Json;
using DndMcp.Domain.Characters;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: the slot tables equal the vendored 5e data's class level records (<c>spellcasting.spell_slots_level_*</c>)
/// for every class at every level in both editions: full casters identical, half casters with no slots at 2014 level 1
/// and two at 2024 level 1, the warlock's one non-zero column read as its Pact Magic slots, and nothing for the classes
/// that do not cast.
/// </summary>
public sealed class SpellSlotTablesTests
{
    public static TheoryData<string, string, int> EveryClassLevel()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var edition in new[] { E2014, E2024 })
        {
            foreach (var c in ClassTable.All)
            {
                for (var level = 1; level <= 20; level++)
                {
                    data.Add(edition, c.Index, level);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryClassLevel))]
    public void For_EveryClassLevel_MatchesTheVendoredLevelRecord(string edition, string classIndex, int level)
    {
        var data = DataSlots(edition, classIndex, level);
        var row = SpellSlotTables.For(ClassTable.Find(classIndex)!.CasterKind, level, edition);

        if (classIndex == "warlock")
        {
            var nonZero = data.Select((count, i) => (Level: i + 1, Count: count)).Where(s => s.Count > 0).ToList();
            var single = Assert.Single(nonZero);
            Assert.Equal(new PactSlots(single.Level, single.Count), row.Pact);
            Assert.Empty(row.Slots);
        }
        else
        {
            Assert.Null(row.Pact);
            for (var slot = 1; slot <= 9; slot++)
            {
                Assert.Equal(data[slot - 1], row.At(slot));
            }
        }
    }

    [Fact]
    public void Half_LevelOne_IsNoneIn2014AndTwoIn2024()
    {
        Assert.Empty(SpellSlotTables.Half(1, E2014));
        Assert.Equal([2], SpellSlotTables.Half(1, E2024));
        Assert.Equal(SpellSlotTables.Half(2, E2014), SpellSlotTables.Half(2, E2024));
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(10, 5, 2)]
    [InlineData(11, 5, 3)]
    [InlineData(16, 5, 3)]
    [InlineData(17, 5, 4)]
    public void Pact_WarlockLevel_GivesThePactTable(int level, int slotLevel, int count)
    {
        Assert.Equal(new PactSlots(slotLevel, count), SpellSlotTables.Pact(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void Full_LevelOutsideOneToTwenty_IsAHostBug(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpellSlotTables.Full(level));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpellSlotTables.Pact(level));
    }

    [Fact]
    public void For_UnknownCasterKind_IsAHostBug()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SpellSlotTables.For("third", 3, E2024));
    }

    [Fact]
    public void ArchetypeSlots_ForwardToTheTables()
    {
        // The archetypes' slot helpers read these tables (behaviour unchanged): a wizard 12's slots of 3rd level or
        // higher are 3+3+2+1 = 9, a paladin 5's slots are 4 + 2.
        Assert.Equal(9, Domain.Simulation.Archetypes.SpellSlots.FullAtOrAbove(12, 3));
        Assert.Equal(2, Domain.Simulation.Archetypes.SpellSlots.FullExactly(12, 5));
        Assert.Equal(0, Domain.Simulation.Archetypes.SpellSlots.FullExactly(12, 7));
        Assert.Equal(6, Domain.Simulation.Archetypes.SpellSlots.HalfTotal(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Domain.Simulation.Archetypes.SpellSlots.HalfTotal(1));
    }

    /// <summary>The record's slot counts for levels 1-9 (missing columns are 0: half casters store only 1-5).</summary>
    private static int[] DataSlots(string edition, string classIndex, int level)
    {
        var record = Records(edition).Single(r =>
            !r.TryGetProperty("subclass", out _) &&
            r.GetProperty("class").GetProperty("index").GetString() == classIndex &&
            r.GetProperty("level").GetInt32() == level);
        var slots = new int[9];
        if (record.TryGetProperty("spellcasting", out var spellcasting))
        {
            for (var slot = 1; slot <= 9; slot++)
            {
                slots[slot - 1] = spellcasting.TryGetProperty($"spell_slots_level_{slot}", out var count) ? count.GetInt32() : 0;
            }
        }

        return slots;
    }

    private static readonly Dictionary<string, JsonElement[]> Cache = [];

    internal static JsonElement[] Records(string edition)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(edition, out var records))
            {
                var path = Path.Combine(AppContext.BaseDirectory, "content", "5e-database", "v7.0.0", edition, "5e-SRD-Levels.json");
                records = JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateArray().ToArray();
                Cache[edition] = records;
            }

            return records;
        }
    }
}
