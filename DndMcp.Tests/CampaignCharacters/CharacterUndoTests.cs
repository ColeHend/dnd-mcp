using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.Dice;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// Every <c>campaign_character</c> write is one undoable batch (contract §7.1): undoing it puts every logged table back
/// exactly, redo reapplies it, the per-key columns conflict per key (another slot level or resource changed later does
/// not block the undo; the same one does), and a sheet reads as of a session.
/// </summary>
public sealed class CharacterUndoTests
{
    private const string Wizard = """
        { "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14 }, "xp": 6500,
          "resources": [{ "name": "Arcane Recovery", "max": 1 }, { "name": "Bladesong", "max": 3 }] }
        """;

    public static TheoryData<string> Actions() => new()
    {
        "update", "damage", "heal", "temp_hp", "use", "rest", "condition", "level_up", "xp", "inventory", "currency",
    };

    private static CharacterWriteResult Act(SheetWorld world, string action) => action switch
    {
        "update" => world.Update(null, """{ "ac": 15, "abilities": { "dex": 16 } }"""),
        "damage" => world.Writer.Damage(world.Campaign, null, 25, null, WriteContext.Default),
        "heal" => world.Writer.Heal(world.Campaign, null, 3, WriteContext.Default),
        "temp_hp" => world.Writer.TempHp(world.Campaign, null, 6, WriteContext.Default),
        "use" => world.Writer.Use(world.Campaign, null, 2, false, null, 1, WriteContext.Default),
        "rest" => world.Writer.Rest(world.Campaign, null, "short", 1, null, ScriptedDiceRoller.Sequence(4), WriteContext.Default),
        "condition" => world.Writer.Condition(world.Campaign, null, ["blinded", "exhaustion"], null, null, WriteContext.Default),
        "level_up" => world.Writer.LevelUp(world.Campaign, null, null, null, WriteContext.Default),
        "xp" => world.Writer.Xp(world.Campaign, null, 7500, WriteContext.Default),
        "inventory" => world.Writer.Inventory(world.Campaign, null, [new InventoryItem { Item = "Spellbook" }], WriteContext.Default),
        "currency" => world.Writer.Currency(world.Campaign, null, new Coins(Gp: 25), WriteContext.Default),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static SheetWorld World()
    {
        var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, 10, null, WriteContext.Default);
        return world;
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void Undo_EachAction_PutsEveryLoggedTableBackAndRedoReappliesIt(string action)
    {
        using var world = World();
        var before = world.F.Dump();

        var result = Act(world, action);
        Assert.NotNull(result.BatchId);
        var after = world.F.Dump();
        Assert.NotEqual(before, after);

        var undo = world.F.History.Undo(world.Campaign, result.BatchId!, WriteContext.Default);
        Assert.Equal(before, world.F.Dump());

        world.F.History.Undo(world.Campaign, undo.UndoBatchId!, WriteContext.Default);
        Assert.Equal(after, world.F.Dump());
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public void DryRun_EachAction_ReportsTheChangeAndKeepsNothing(string action)
    {
        using var world = World();
        var before = world.F.Dump();
        var rows = world.ChangeRows();
        var dry = new WriteContext { DryRun = true };

        var result = action switch
        {
            "update" => world.Writer.Update(world.Campaign, null, SheetWorld.Spec("""{ "ac": 15 }"""), null, dry),
            "damage" => world.Writer.Damage(world.Campaign, null, 5, null, dry),
            "heal" => world.Writer.Heal(world.Campaign, null, 3, dry),
            "temp_hp" => world.Writer.TempHp(world.Campaign, null, 6, dry),
            "use" => world.Writer.Use(world.Campaign, null, 2, false, null, 1, dry),
            "rest" => world.Writer.Rest(world.Campaign, null, "long", null, null, ScriptedDiceRoller.Sequence(), dry),
            "condition" => world.Writer.Condition(world.Campaign, null, ["blinded"], null, null, dry),
            "level_up" => world.Writer.LevelUp(world.Campaign, null, null, null, dry),
            "xp" => world.Writer.Xp(world.Campaign, null, 100, dry),
            "inventory" => world.Writer.Inventory(world.Campaign, null, [new InventoryItem { Item = "Spellbook" }], dry),
            _ => world.Writer.Currency(world.Campaign, null, new Coins(Gp: 25), dry),
        };

        Assert.True(result.DryRun);
        Assert.Null(result.BatchId);
        Assert.NotEmpty(result.Changes);
        Assert.Equal(before, world.F.Dump());
        Assert.Equal(rows, world.ChangeRows());
    }

    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, false)]
    public void Undo_SlotUse_ConflictsOnlyWithTheSameSlotLevel(int first, int later, bool undoes)
    {
        using var world = World();
        var spend = world.Writer.Use(world.Campaign, null, first, false, null, 1, WriteContext.Default);
        var other = world.Writer.Use(world.Campaign, null, later, false, null, 1, WriteContext.Default);

        if (undoes)
        {
            world.F.History.Undo(world.Campaign, spend.BatchId!, WriteContext.Default);
            var sheet = world.Required("character:aria-vale");
            Assert.Equal(0, sheet.SpellSlots[first.ToString(System.Globalization.CultureInfo.InvariantCulture)].Used);
            Assert.Equal(1, sheet.SpellSlots[later.ToString(System.Globalization.CultureInfo.InvariantCulture)].Used);
            return;
        }

        var ex = Assert.Throws<DndInputException>(() => world.F.History.Undo(world.Campaign, spend.BatchId!, WriteContext.Default));
        Assert.Contains(other.BatchId!, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bladesong", "Arcane Recovery", true)]
    [InlineData("Bladesong", "Bladesong", false)]
    public void Undo_ResourceUse_ConflictsOnlyWithTheSameResource(string first, string later, bool undoes)
    {
        using var world = World();
        var spend = world.Writer.Use(world.Campaign, null, null, false, first, 1, WriteContext.Default);
        world.Writer.Use(world.Campaign, null, null, false, later, 1, WriteContext.Default);

        if (undoes)
        {
            world.F.History.Undo(world.Campaign, spend.BatchId!, WriteContext.Default);
            Assert.Equal(0, world.Required("character:aria-vale").Resources["bladesong"].Used);
            return;
        }

        Assert.Throws<DndInputException>(() => world.F.History.Undo(world.Campaign, spend.BatchId!, WriteContext.Default));
    }

    [Fact]
    public void Undo_DamageThenALaterHeal_IsRefusedUntilTheHealIsUndone()
    {
        using var world = World();
        var damage = world.Writer.Damage(world.Campaign, null, 5, null, WriteContext.Default);
        var heal = world.Writer.Heal(world.Campaign, null, 2, WriteContext.Default);

        Assert.Throws<DndInputException>(() => world.F.History.Undo(world.Campaign, damage.BatchId!, WriteContext.Default));
        world.F.History.Undo(world.Campaign, heal.BatchId!, WriteContext.Default);
        world.F.History.Undo(world.Campaign, damage.BatchId!, WriteContext.Default);
        Assert.Equal(22, world.Required("character:aria-vale").Hp);
    }

    [Fact]
    public void Undo_SheetCreate_DeletesTheSheet()
    {
        using var world = SheetWorld.Dm();
        var before = world.F.Dump();

        var created = world.Update("character:hero", """{ "level": 3 }""");
        world.F.History.Undo(world.Campaign, created.BatchId!, WriteContext.Default);

        Assert.Null(world.Sheet("character:hero"));
        Assert.Equal(before, world.F.Dump());
    }

    [Fact]
    public void AsOf_SheetThroughTheReader_ReadsEachSessionsValues()
    {
        using var world = SheetWorld.Player();
        world.F.Played(world.Campaign, 1);
        world.F.Played(world.Campaign, 2);
        world.Update(null, Wizard, WriteContext.For(1));
        world.Writer.Use(world.Campaign, null, 1, false, null, 2, WriteContext.For(2));
        world.Writer.Inventory(world.Campaign, null, [new InventoryItem { Item = "Spellbook" }], WriteContext.For(2));

        var reader = new EntityReader(world.Database);
        var asOf1 = reader.Get(world.Campaign, ["character:aria-vale"], new EntityIncludes(Sheet: true), null, 1).Entities.Single().Sheet!.Author!;
        var now = reader.Get(world.Campaign, ["character:aria-vale"], new EntityIncludes(Sheet: true)).Entities.Single().Sheet!.Author!;

        Assert.Equal(0, asOf1.Sheet.SpellSlots["1"].Used);
        Assert.Empty(asOf1.Inventory);
        Assert.Equal(2, now.Sheet.SpellSlots["1"].Used);
        Assert.Equal("Spellbook", Assert.Single(now.Inventory).Name);
    }
}
