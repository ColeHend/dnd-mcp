using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// campaign_history reads Phase 7's logged tables as what they are about (the history labels of contract §13): the
/// character's sheet, its holdings, its coins and its awards, with handles where change_log holds entity ids.
/// </summary>
public sealed class CharacterHistoryTests
{
    private static IReadOnlyList<string> Lines(SheetWorld world, string handle) =>
        new HistoryReader(world.Database).Entity(world.Campaign, handle).Batches.SelectMany(b => b.Changes).Select(c => c.Text).ToList();

    [Fact]
    public void History_SheetWrites_ReadAsTheCharactersSheet()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3, "max_hp": 24 }""");
        world.Writer.Damage(world.Campaign, "character:hero", 4, null, WriteContext.Default);

        var lines = Lines(world, "character:hero");

        Assert.Contains("character:hero sheet hp: 24 → 20", lines);
        Assert.Contains(lines, l => l.StartsWith("character:hero sheet created: level 3, max_hp 24", StringComparison.Ordinal));
    }

    [Fact]
    public void History_HoldingsAndCoins_NameTheHolderAndTheItem()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "item", Name = "The Sunblade", Slug = "sunblade", Visibility = "party" });
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "item:sunblade" }, new InventoryItem { Item = "Rope", Qty = 2.5 }], WriteContext.Default);
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Rope", Qty = -1 }], WriteContext.Default);
        world.Writer.Currency(world.Campaign, "character:hero", new Coins(Gp: 12, Cp: -3), new WriteContext { Reason = "a toll" });

        var lines = Lines(world, "character:hero");

        Assert.Contains("character:hero holding \"The Sunblade\" created: quantity 1, item_id item:sunblade", lines);
        Assert.Contains("character:hero holding \"Rope\" created: quantity 2.5", lines);
        Assert.Contains("character:hero holding \"Rope\" quantity: 2.5 → 1.5", lines);
        Assert.Contains("character:hero coins created: +12 gp -3 cp, note \"a toll\"", lines);
        Assert.Contains(lines, l => l.StartsWith("character:hero holding \"The Sunblade\"", StringComparison.Ordinal));
        Assert.Contains("character:hero holding \"The Sunblade\" created: quantity 1, item_id item:sunblade", Lines(world, "item:sunblade"));
    }

    [Fact]
    public void History_Award_NamesTheRecipientKindAndSource()
    {
        using var world = SheetWorld.Dm();
        var hero = world.Id("character:hero");
        world.F.Db.Batch(world.Campaign.Id, r => r.Insert("award", new Dictionary<string, object?>
        {
            ["campaign_id"] = world.Campaign.Id,
            ["recipient_id"] = hero,
            ["kind"] = "xp",
            ["amount"] = 2400L,
            ["source"] = "encounter: The sixth station",
        }, "award"));

        Assert.Contains("character:hero award (xp) created: amount 2400, source \"encounter: The sixth station\"", Lines(world, "character:hero"));
    }
}
