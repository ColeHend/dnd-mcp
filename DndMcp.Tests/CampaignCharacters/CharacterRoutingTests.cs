using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.Dice;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// The routing seam (contract D5) with a hand-written router: a sheet-seeded combatant's damage, heal, temp_hp, use and
/// condition go to the fight (nothing logged, no batch id, the sheet untouched), a routed condition keeps
/// <c>until_removed</c>, a rest is refused during the fight, a character in it from a stat block is acted on as out of
/// combat with a note, and without a router (or for a character not in the fight) every action acts on the sheet.
/// </summary>
public sealed class CharacterRoutingTests
{
    private const string Sheet = """{ "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14 } }""";

    private static (SheetWorld World, FakeRouter Router) InFight(string outcome = RouteOutcomes.Applied)
    {
        var world = SheetWorld.Player();
        world.Update(null, Sheet);
        var router = new FakeRouter(world.Id("character:aria-vale"), outcome);
        world.Route(router);
        return (world, router);
    }

    public static TheoryData<string> Routable() => new() { "damage", "heal", "temp_hp", "use", "condition" };

    private static CharacterWriteResult Act(SheetWorld world, string action, WriteContext? context = null)
    {
        context ??= WriteContext.Default;
        return action switch
        {
            "damage" => world.Writer.Damage(world.Campaign, null, 5, "fire", context),
            "heal" => world.Writer.Heal(world.Campaign, null, 5, context),
            "temp_hp" => world.Writer.TempHp(world.Campaign, null, 5, context),
            "use" => world.Writer.Use(world.Campaign, null, 1, false, null, 1, context),
            "condition" => world.Writer.Condition(world.Campaign, null, ["poisoned"], null, null, context),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [Theory]
    [MemberData(nameof(Routable))]
    public void Act_SheetSeededInTheFight_GoesToTheFightWithNoBatch(string action)
    {
        var (world, router) = InFight();
        using var _ = world;
        var sheet = world.Required("character:aria-vale");
        var rows = world.ChangeRows();

        var result = Act(world, action);

        Assert.Null(result.BatchId);
        Assert.Empty(result.Changes);
        Assert.True(result.Changed);
        Assert.Equal(RouteOutcomes.Applied, result.Routed!.Outcome);
        Assert.Equal("Applied to the live fight The crypt; written to the sheet when it ends.", result.Notes[0]);
        Assert.Equal(rows, world.ChangeRows());
        Assert.Equal(action, Assert.Single(router.Calls).Kind);
        Assert.Equal(DndMcp.Domain.Characters.SheetJson.ToColumns(sheet), DndMcp.Domain.Characters.SheetJson.ToColumns(world.Required("character:aria-vale")));
    }

    [Fact]
    public void Damage_Routed_PassesTheCheckedArgumentsAndTheFightsReminders()
    {
        var (world, router) = InFight();
        using var _ = world;

        var result = world.Writer.Damage(world.Campaign, null, 12, "Fire", WriteContext.Default);

        var call = Assert.Single(router.Calls);
        Assert.Equal((12, "fire"), (call.Amount, call.DamageType));
        var reminder = Assert.Single(result.Reminders);
        // The fight's call, with the campaign named last (a printed call goes to whichever campaign is current when sent).
        Assert.Equal(("concentration_save", $"combat {{\"action\": \"concentration\", \"campaign\": \"{world.Campaign.Slug}\"}}"), (reminder.Kind, reminder.Call));
    }

    [Fact]
    public void Condition_Routed_KeepsUntilRemoved()
    {
        var (world, router) = InFight();
        using var _ = world;

        world.Writer.Condition(world.Campaign, null, ["cursed (Mucus Cloud)"], null, null, WriteContext.Default);

        var call = Assert.Single(router.Calls);
        Assert.Equal("until_removed", call.Duration);
        Assert.Equal(["cursed (Mucus Cloud)"], call.Add);
    }

    [Fact]
    public void Rest_InTheFight_IsRefusedNamingItAndWritesNothing()
    {
        var (world, router) = InFight();
        using var _ = world;
        var rows = world.ChangeRows();

        var ex = Assert.Throws<DndInputException>(() =>
            world.Writer.Rest(world.Campaign, null, "short", null, null, ScriptedDiceRoller.Sequence(), WriteContext.Default));

        Assert.Equal("Aria Vale is in the live fight \"The crypt\" as Belmakor Silverwind: rest once it ends (combat {\"action\": \"end\", \"campaign\": \"sky\"} ends it).", ex.Message);
        Assert.Equal(CharacterActions.Rest, Assert.Single(router.Calls).Kind);
        Assert.Equal(rows, world.ChangeRows());
    }

    [Fact]
    public void Rest_InTheFightFromAStatBlock_IsRefusedToo()
    {
        var (world, _) = InFight(RouteOutcomes.StatBlock);
        using var _w = world;

        Assert.Throws<DndInputException>(() =>
            world.Writer.Rest(world.Campaign, null, "long", null, null, ScriptedDiceRoller.Sequence(), WriteContext.Default));
    }

    [Theory]
    [MemberData(nameof(Routable))]
    public void Act_InTheFightFromAStatBlock_ChangesTheSheetWithTheNote(string action)
    {
        var (world, _) = InFight(RouteOutcomes.StatBlock);
        using var _w = world;
        if (action == "heal")
        {
            world.Writer.Damage(world.Campaign, null, 8, null, WriteContext.Default);
        }

        var result = Act(world, action);

        Assert.NotNull(result.BatchId);
        Assert.NotEmpty(result.Changes);
        Assert.Contains("Aria Vale is in the live fight as Belmakor Silverwind from a stat block: this changed the sheet, not the fight (use combat to change the fight).",
            result.Notes);
    }

    [Fact]
    public void Act_NotInTheFight_ActsOnTheSheet()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Sheet);
        world.Update("character:bram", Sheet);
        var router = new FakeRouter(world.Id("character:aria-vale"));
        world.Route(router);

        var result = world.Writer.Damage(world.Campaign, "character:bram", 5, null, WriteContext.Default);

        Assert.NotNull(result.BatchId);
        Assert.Null(result.Routed);
        Assert.Single(router.Calls);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("xp")]
    [InlineData("level_up")]
    [InlineData("inventory")]
    [InlineData("currency")]
    public void Act_NotRoutable_NeverAsksTheRouter(string action)
    {
        var (world, router) = InFight();
        using var _ = world;

        var result = action switch
        {
            "update" => world.Update(null, """{ "ac": 12 }"""),
            "xp" => world.Writer.Xp(world.Campaign, null, 100, WriteContext.Default),
            "level_up" => world.Writer.LevelUp(world.Campaign, null, null, null, WriteContext.Default),
            "inventory" => world.Writer.Inventory(world.Campaign, null, [new InventoryItem { Item = "Rope" }], WriteContext.Default),
            _ => world.Writer.Currency(world.Campaign, null, new Coins(Gp: 1), WriteContext.Default),
        };

        Assert.Empty(router.Calls);
        Assert.NotNull(result.BatchId);
    }

    [Fact]
    public void Act_RoutedDryRun_RollsTheFightsWritesBack()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Sheet);
        world.Route(new FakeRouter(world.Id("character:aria-vale")) { Write = true });

        var result = world.Writer.Damage(world.Campaign, null, 5, null, new WriteContext { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Null(result.BatchId);
        Assert.Empty(world.Dice());

        world.Writer.Damage(world.Campaign, null, 5, null, WriteContext.Default);
        Assert.Single(world.Dice());
    }

    [Fact]
    public void Act_RoutedRefusedShape_IsRefusedBeforeTheFightIsAsked()
    {
        var (world, router) = InFight();
        using var _ = world;

        Assert.Throws<DndInputException>(() => world.Writer.Use(world.Campaign, null, null, false, null, 1, WriteContext.Default));
        Assert.Throws<DndInputException>(() => world.Writer.Damage(world.Campaign, null, null, null, WriteContext.Default));
        Assert.Throws<DndInputException>(() => world.Writer.Condition(world.Campaign, null, null, null, null, WriteContext.Default));
        Assert.Empty(router.Calls);
    }
}
