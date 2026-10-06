using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <c>use</c>, <c>condition</c>, <c>level_up</c> and <c>xp</c> through <see cref="DndMcp.Repository.Campaign.Characters.CharacterWriter"/>:
/// each one logged batch with the per-key rows of the columns it changes, reminders with their calls naming the campaign,
/// and P's refusals passed through with nothing written.
/// </summary>
public sealed class CharacterWriterActionTests
{
    private const string Wizard = """
        { "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14, "int": 18 }, "xp": 6400,
          "resources": [{ "name": "Arcane Recovery", "max": 1, "recharge": "long_rest" }] }
        """;

    private static SheetWorld World()
    {
        var world = SheetWorld.Player();
        world.Update(null, Wizard);
        return world;
    }

    [Fact]
    public void Use_SpellSlot_LogsThatSlotLevelsKeyOnly()
    {
        using var world = World();

        var result = world.Writer.Use(world.Campaign, null, 3, false, null, 1, WriteContext.Default);

        var row = Assert.Single(world.Log(result.BatchId));
        Assert.Equal("spell_slots.3", row.FieldPath);
        Assert.Equal(1, world.Required("character:aria-vale").SpellSlots["3"].Used);
        Assert.Contains(result.Notes, n => n.StartsWith("3rd-level slots: 1 used", StringComparison.Ordinal));
    }

    [Fact]
    public void Use_ResourceThenRestore_RoundTrips()
    {
        using var world = World();

        world.Writer.Use(world.Campaign, null, null, false, "arcane recovery", 1, WriteContext.Default);
        Assert.Equal(1, world.Required("character:aria-vale").Resources["arcane-recovery"].Used);
        var restore = world.Writer.Use(world.Campaign, null, null, false, "Arcane Recovery", -1, WriteContext.Default);

        Assert.Equal("resources.arcane-recovery", Assert.Single(world.Log(restore.BatchId)).FieldPath);
        Assert.Equal(0, world.Required("character:aria-vale").Resources["arcane-recovery"].Used);
    }

    [Theory]
    [InlineData(null, false, null, 1, "give exactly one of slot_level")]
    [InlineData(1, true, null, 1, "give exactly one of slot_level")]
    [InlineData(1, false, null, 0, "amount is 0")]
    [InlineData(1, false, null, int.MinValue, "amount is -2147483648; give 1 to 999 uses spent")]
    [InlineData(9, false, null, 1, "the sheet has no 9th-level slots")]
    [InlineData(1, false, null, 5, "4")]
    public void Use_Refused_WritesNothing(int? slot, bool pact, string? resource, int amount, string expected)
    {
        using var world = World();
        var rows = world.ChangeRows();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Use(world.Campaign, null, slot, pact, resource, amount, WriteContext.Default));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(rows, world.ChangeRows());
    }

    [Fact]
    public void Condition_AddAndRemove_StoresUntilRemoved()
    {
        using var world = World();

        var add = world.Writer.Condition(world.Campaign, null, ["Poisoned", "cursed (Mucus Cloud)"], null, null, WriteContext.Default);

        var sheet = world.Required("character:aria-vale");
        Assert.Equal(["poisoned", "cursed (Mucus Cloud)"], sheet.Conditions.Select(c => c.Name));
        Assert.All(sheet.Conditions, c => Assert.Equal("until_removed", c.Duration));
        Assert.Equal("conditions", Assert.Single(world.Log(add.BatchId)).FieldPath);

        world.Writer.Condition(world.Campaign, null, null, ["poisoned"], null, WriteContext.Default);
        Assert.Equal(["cursed (Mucus Cloud)"], world.Required("character:aria-vale").Conditions.Select(c => c.Name));
    }

    [Fact]
    public void Condition_Exhaustion_ChangesTheColumnByLevels()
    {
        using var world = World();

        var result = world.Writer.Condition(world.Campaign, null, ["exhaustion"], null, 2, WriteContext.Default);

        Assert.Equal(2, world.Required("character:aria-vale").Exhaustion);
        Assert.Equal("exhaustion", Assert.Single(world.Log(result.BatchId)).FieldPath);
    }

    [Theory]
    [InlineData(new string[0], new string[0], "give add or remove")]
    [InlineData(new string[0], new[] { "concentration" }, "holds no concentration")]
    [InlineData(new string[0], new[] { "blinded" }, "the sheet has no \"blinded\"")]
    public void Condition_Refused_WritesNothing(string[] add, string[] remove, string expected)
    {
        using var world = World();
        var rows = world.ChangeRows();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Condition(world.Campaign, null, add, remove, null, WriteContext.Default));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(rows, world.ChangeRows());
    }

    [Fact]
    public void LevelUp_AddsTheLevelAndHitPointsWithReminders()
    {
        using var world = World();
        var before = world.Required("character:aria-vale");

        var result = world.Writer.LevelUp(world.Campaign, null, null, null, WriteContext.Default);

        var after = world.Required("character:aria-vale");
        Assert.Equal(6, after.Level);
        Assert.Equal(before.MaxHp + 6, after.MaxHp);
        Assert.Contains(result.Reminders, r => r.Kind == "update_resources");
        Assert.Contains(world.Log(result.BatchId), r => r.FieldPath == "hit_dice.d6");
        Assert.Contains(world.Log(result.BatchId), r => r.FieldPath == "spell_slots.3");
    }

    [Fact]
    public void Xp_ReachingALevel_RemindsWithTheLevelUpCallNamingTheCampaign()
    {
        using var world = World();

        var result = world.Writer.Xp(world.Campaign, null, 7600, WriteContext.Default);

        Assert.Equal(14000, world.Required("character:aria-vale").Xp);
        var due = Assert.Single(result.Reminders, r => r.Kind == "level_due");
        Assert.Equal("campaign_character {\"action\": \"level_up\", \"character\": \"character:aria-vale\", \"campaign\": \"sky\"}", due.Call);
        Assert.Equal("xp", Assert.Single(world.Log(result.BatchId)).FieldPath);
        Assert.Equal(0L, world.F.Count("SELECT count(*) FROM award"));
    }

    [Fact]
    public void Xp_NoAmount_IsRefused()
    {
        using var world = World();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Xp(world.Campaign, null, null, WriteContext.Default));

        Assert.Contains("amount is required for xp", ex.Message, StringComparison.Ordinal);
    }
}
