using Dapper;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <c>inventory</c> and <c>currency</c> (contract §7.1): holdings matched by name, a holding at 0 deleted (logged, so undo
/// brings it back), taking more than is held refused; one ledger row per currency call, its note the reason, a negative
/// balance warned.
/// </summary>
public sealed class CharacterWriterItemsTests
{
    private static IReadOnlyList<HoldingRow> Holdings(SheetWorld world, string handle)
    {
        using var connection = world.Open();
        return connection.Query<HoldingRow>($"SELECT {HoldingRow.Columns} FROM holding WHERE holder_id = @id ORDER BY created_at",
            new { id = world.Id(handle) }).ToList();
    }

    [Fact]
    public void Inventory_AddThenAddMore_KeepsOneHoldingMatchedByName()
    {
        using var world = SheetWorld.Dm();

        var first = world.Writer.Inventory(world.Campaign, "character:hero",
            [new InventoryItem { Item = "Potion of Healing", Qty = 2, Srd = "2024/equipment/potion-of-healing" }], WriteContext.Default);
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "potion of healing" }], WriteContext.Default);

        var holding = Assert.Single(Holdings(world, "character:hero"));
        Assert.Equal("Potion of Healing", holding.Name);
        Assert.Equal(3, holding.Quantity);
        Assert.Equal("2024/equipment/potion-of-healing", holding.SrdRef);
        var create = Assert.Single(world.Log(first.BatchId));
        Assert.Equal(("create", "holding", "campaign_character/inventory"), (create.Op, create.TargetTable, create.Tool));
        Assert.Equal(world.Id("character:hero"), create.EntityId);
        Assert.Equal([new CharacterChange("inventory", "Potion of Healing", null, "2")], first.Changes);
    }

    [Fact]
    public void Inventory_TakeToZero_DeletesTheHoldingAndUndoBringsItBack()
    {
        using var world = SheetWorld.Dm();
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Rope", Qty = 1 }], WriteContext.Default);
        var dump = world.F.Dump();

        var take = world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "rope", Qty = -1 }], WriteContext.Default);

        Assert.Empty(Holdings(world, "character:hero"));
        Assert.Equal("delete", Assert.Single(world.Log(take.BatchId)).Op);
        Assert.Contains("Rope: none left (removed).", take.Notes);
        world.F.History.Undo(world.Campaign, take.BatchId!, WriteContext.Default);
        Assert.Equal(dump, world.F.Dump());
    }

    [Fact]
    public void Inventory_FlagsOnlyWithQtyZero_ChangesThoseFields()
    {
        using var world = SheetWorld.Dm();
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Longsword", Qty = 1 }], WriteContext.Default);

        var result = world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Longsword", Qty = 0, Equipped = true }], WriteContext.Default);

        Assert.Equal(1, Holdings(world, "character:hero")[0].Equipped);
        Assert.Equal([new CharacterChange("inventory", "Longsword equipped", "no", "yes")], result.Changes);
        Assert.Equal("equipped", Assert.Single(world.Log(result.BatchId)).FieldPath);
    }

    /// <summary>
    /// C11: an entry for an item already held that gives equipped, attuned, notes or srd and no qty changes only those
    /// (unequipping a ring never gives a second ring); a new item's qty defaults to 1, and a held item named alone adds one.
    /// </summary>
    [Theory]
    [InlineData("equipped", "Ring of Protection equipped", "yes", "no")]
    [InlineData("attuned", "Ring of Protection attuned", "yes", "no")]
    [InlineData("notes", "Ring of Protection notes", null, "loose on the finger")]
    [InlineData("srd", "Ring of Protection srd_ref", null, "2024/magic-item/ring-of-protection")]
    public void Inventory_HeldItemWithAFieldAndNoQty_ChangesOnlyThatField(string field, string change, string? before, string after)
    {
        using var world = SheetWorld.Dm();
        var added = world.Writer.Inventory(world.Campaign, "character:hero",
            [new InventoryItem { Item = "Ring of Protection", Equipped = true, Attuned = true }], WriteContext.Default);
        var entry = field switch
        {
            "equipped" => new InventoryItem { Item = "ring of protection", Equipped = false },
            "attuned" => new InventoryItem { Item = "ring of protection", Attuned = false },
            "notes" => new InventoryItem { Item = "ring of protection", Notes = after },
            _ => new InventoryItem { Item = "ring of protection", Srd = after },
        };

        var result = world.Writer.Inventory(world.Campaign, "character:hero", [entry], WriteContext.Default);
        var more = world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Ring of Protection" }], WriteContext.Default);

        Assert.Equal([new CharacterChange("inventory", "Ring of Protection", null, "1")], added.Changes);
        Assert.Equal([new CharacterChange("inventory", change, before, after)], result.Changes);
        Assert.Equal([new CharacterChange("inventory", "Ring of Protection", "1", "2")], more.Changes);
    }

    [Fact]
    public void Inventory_FlagsOnlyOnAHoldingAtZero_KeepsTheHolding()
    {
        // combat end writes a used-up holding as 0 and keeps the row (views hide it): changing its flags must not delete it.
        using var world = SheetWorld.Dm();
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing" }], WriteContext.Default);
        world.F.Db.WriteBehindTheServer("UPDATE holding SET quantity = 0");

        var result = world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Potion of Healing", Notes = "the empty vial" }], WriteContext.Default);

        var holding = Assert.Single(Holdings(world, "character:hero"));
        Assert.Equal((0.0, "the empty vial"), (holding.Quantity, holding.Notes));
        Assert.Equal([new CharacterChange("inventory", "Potion of Healing notes", null, "the empty vial")], result.Changes);
    }

    /// <summary>R03: text in a holding's quantity (a table whose STRICT was lost) is the store message, never the generic error.</summary>
    [Fact]
    public void Inventory_TextInAHoldingsQuantity_IsTheStoreMessage_ForTheWriterAndTheSheetRead()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3 }""");
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Rope" }], WriteContext.Default);
        world.F.Db.DropStrict("holding");
        world.F.Db.WriteBehindTheServer("UPDATE holding SET quantity = 'lots'");

        var refusals = new[]
        {
            Assert.Throws<CampaignStoreUnavailableException>(() => world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Rope" }], WriteContext.Default)),
            Assert.Throws<CampaignStoreUnavailableException>(() => world.Reader.Get(world.Campaign, "character:hero")),
        };

        Assert.All(refusals, ex => Assert.StartsWith("A holding row in ", ex.Message, StringComparison.Ordinal));
        Assert.All(refusals, ex => Assert.DoesNotContain("lots", ex.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void Inventory_ItemHandle_LinksTheItemEntity()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "item", Name = "The Sunblade", Slug = "sunblade", Visibility = "party" });

        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "item:sunblade" }], WriteContext.Default);

        var holding = Assert.Single(Holdings(world, "character:hero"));
        Assert.Equal("The Sunblade", holding.Name);
        Assert.Equal(world.Id("item:sunblade"), holding.ItemId);
    }

    [Theory]
    [InlineData("Rope", -3.0, "holds 1 Rope; cannot take 3")]
    [InlineData("Rope", -2.0, "holds 1 Rope; cannot take 2")]
    [InlineData("Rope", -1.5, "holds 1 Rope; cannot take 1.5")]
    [InlineData("Lantern", -1.0, "holds no \"Lantern\" to take")]
    [InlineData("Lantern", 0.0, "qty 0 changes nothing")]
    [InlineData("", 1.0, "item must be the item's name")]
    [InlineData("Rope", double.NaN, "qty must be a number")]
    [InlineData("item:nothing", 1.0, "no item item:nothing")]
    [InlineData("!!!", 1.0, "items item 1: give item a name with a letter or digit.")]
    [InlineData("—", 1.0, "items item 1: give item a name with a letter or digit.")]
    [InlineData("???", 1.0, "items item 1: give item a name with a letter or digit.")]
    [InlineData("…", -1.0, "items item 1: give item a name with a letter or digit.")]
    public void Inventory_Refused_WritesNothing(string item, double qty, string expected)
    {
        using var world = SheetWorld.Dm();
        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Rope", Qty = 1 }], WriteContext.Default);
        var rows = world.ChangeRows();

        var ex = Assert.Throws<DndInputException>(() =>
            world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = item, Qty = qty }], WriteContext.Default));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(rows, world.ChangeRows());
    }

    [Fact]
    public void Inventory_DuringALiveSession_RecordsTheSessionItWasGainedIn()
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 1);
        world.F.Sessions.Start(world.Campaign, 3);
        world.Reload();

        world.Writer.Inventory(world.Campaign, "character:hero", [new InventoryItem { Item = "Rope" }], WriteContext.Default);
        world.Writer.Inventory(world.Campaign, "character:sidekick", [new InventoryItem { Item = "Lantern" }], WriteContext.For(1));

        Assert.Equal(world.F.Entity(world.Campaign, "session:3").Id, Assert.Single(Holdings(world, "character:hero")).AcquiredSessionId);
        Assert.Equal(world.F.Entity(world.Campaign, "session:1").Id, Assert.Single(Holdings(world, "character:sidekick")).AcquiredSessionId);
    }

    [Fact]
    public void Currency_OneLedgerRowWithTheReasonAsItsNote()
    {
        using var world = SheetWorld.Dm();

        var result = world.Writer.Currency(world.Campaign, "character:hero", new Coins(Gp: 120, Sp: 5), new WriteContext { Reason = "loot from the station" });

        var create = Assert.Single(world.Log(result.BatchId));
        Assert.Equal(("create", "currency_txn"), (create.Op, create.TargetTable));
        using var connection = world.Open();
        var row = connection.QuerySingle<CurrencyTxnRow>($"SELECT {CurrencyTxnRow.Columns} FROM currency_txn");
        Assert.Equal((120L, 5L, "loot from the station"), (row.Gp, row.Sp, row.Note));
        Assert.Equal(["Coins: 120 gp, 5 sp."], result.Notes);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Currency_NoReason_UsesTheDefaultNote()
    {
        using var world = SheetWorld.Dm();

        world.Writer.Currency(world.Campaign, "character:hero", new Coins(Cp: 7), WriteContext.Default);

        Assert.Equal(CharacterWriter.CurrencyNote, world.F.Scalar<string>("SELECT note FROM currency_txn"));
    }

    [Fact]
    public void Currency_SpendingMoreThanHeld_IsAppliedWithAWarning()
    {
        using var world = SheetWorld.Dm();
        world.Writer.Currency(world.Campaign, "character:hero", new Coins(Gp: 10), WriteContext.Default);

        var result = world.Writer.Currency(world.Campaign, "character:hero", new Coins(Gp: -15), WriteContext.Default);

        Assert.NotNull(result.BatchId);
        Assert.Equal(CharacterWriter.NegativeBalanceWarning, Assert.Single(result.Warnings).Kind);
        Assert.Equal([new CharacterChange("coins", "gp", "10", "-5")], result.Changes);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(-100_000_001L)]
    [InlineData(100_000_001L)]
    public void Currency_CoinPastTheCap_IsRefusedNamingIt(long cp)
    {
        using var world = SheetWorld.Dm();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Currency(world.Campaign, "character:hero", new Coins(Cp: cp), WriteContext.Default));

        Assert.Contains("coins.cp is " + cp.ToString(System.Globalization.CultureInfo.InvariantCulture) + "; at most 100,000,000 either way.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0L, world.F.Count("SELECT count(*) FROM currency_txn"));
    }

    [Theory]
    [InlineData(100_000_000L)]
    [InlineData(-100_000_000L)]
    public void Currency_CoinAtTheCap_IsApplied(long gp)
    {
        using var world = SheetWorld.Dm();

        var result = world.Writer.Currency(world.Campaign, "character:hero", new Coins(Gp: gp), WriteContext.Default);

        Assert.NotNull(result.BatchId);
    }

    [Theory]
    [InlineData(long.MaxValue - 10, 100L)]
    [InlineData(long.MinValue + 10, -100L)]
    public void Currency_BalancePastAWholeNumber_IsRefusedNotWrapped(long held, long cp)
    {
        // A ledger written by something else (the writer's cap keeps its own rows far from the edge).
        using var world = SheetWorld.Dm();
        using (var connection = world.Open())
        {
            new DndMcp.Tests.CampaignDb.CampaignSeed(connection).CurrencyTxn(world.Campaign.Id, world.Id("character:hero"), cp: held);
        }

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Currency(world.Campaign, "character:hero", new Coins(Cp: cp), WriteContext.Default));

        Assert.Contains("coin balance would be larger than a whole number holds", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1L, world.F.Count("SELECT count(*) FROM currency_txn"));
    }

    [Fact]
    public void Currency_NoCoins_IsRefused()
    {
        using var world = SheetWorld.Dm();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Currency(world.Campaign, "character:hero", new Coins(), WriteContext.Default));

        Assert.Contains("coins is required", ex.Message, StringComparison.Ordinal);
    }
}
