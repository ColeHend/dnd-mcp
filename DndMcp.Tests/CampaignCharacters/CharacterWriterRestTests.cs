using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.Dice;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <c>rest</c> (contract §5.15, D11): the Hit Dice a short rest spends are rolled by the writer with the caller's roller and
/// logged in the same transaction, one dice_roll row per die size, labelled "&lt;name&gt;: hit dice" with the name every
/// reader of the session's dice may be shown, and secret unless the character is a current party member the party sees
/// undisguised. Given faces log nothing.
/// </summary>
public sealed class CharacterWriterRestTests
{
    private const string Wizard = """{ "classes": [{ "class": "wizard", "level": 5 }], "abilities": { "con": 14 } }""";

    [Fact]
    public void Rest_ShortRestRollingHitDice_LogsTheRollOpenWithTheMembersName()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, 20, null, WriteContext.Default);
        var roller = ScriptedDiceRoller.Sequence(4, 6);

        var result = world.Writer.Rest(world.Campaign, null, "short", 2, null, roller, WriteContext.Default);

        Assert.Equal([6, 6], roller.RequestedSides);
        Assert.Equal(12 + 4 + 2 + 6 + 2, world.Required("character:aria-vale").Hp);
        var roll = Assert.Single(world.Dice());
        Assert.Equal("2d6", roll.Expression);
        Assert.Equal("Aria Vale: hit dice", roll.Label);
        Assert.Equal(10, roll.Total);
        Assert.Equal(0, roll.Secret);
        Assert.Null(roll.SessionId);
        Assert.Null(roll.EncounterId);
        Assert.Contains("\"source\":\"campaign_character\"", roll.Detail, StringComparison.Ordinal);
        var logged = Assert.Single(result.Rolls);
        Assert.Equal([4, 6], logged.Faces);
        Assert.False(logged.Secret);
        Assert.Equal(2, world.Required("character:aria-vale").HitDice["d6"].Used);
        Assert.Contains(world.Log(result.BatchId), r => r.FieldPath == "hit_dice.d6");
    }

    [Fact]
    public void Rest_DuringALiveSession_FilesTheRollUnderIt()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, 20, null, WriteContext.Default);
        world.F.Sessions.Start(world.Campaign, 7);

        world.Writer.Rest(world.Campaign, null, "short", 1, null, ScriptedDiceRoller.Sequence(3), WriteContext.Default);

        Assert.Equal(world.F.Entity(world.Campaign, "session:7").Id, Assert.Single(world.Dice()).SessionId);
    }

    [Fact]
    public void Rest_NpcSheet_IsSecretAndNamedAsACharacter()
    {
        using var world = SheetWorld.Player();
        world.Update("character:old-guard", Wizard);
        world.Writer.Damage(world.Campaign, "character:old-guard", 20, null, WriteContext.Default);

        world.Writer.Rest(world.Campaign, "character:old-guard", "short", 1, null, ScriptedDiceRoller.Sequence(3), WriteContext.Default);

        var roll = Assert.Single(world.Dice());
        Assert.Equal(1, roll.Secret);
        Assert.Equal("a character: hit dice", roll.Label);
    }

    [Fact]
    public void Rest_AMembersOwnDisguisedName_FallsBackButStaysOpen()
    {
        using var world = SheetWorld.Player();
        world.Update("character:bram", Wizard);
        world.Writer.Damage(world.Campaign, "character:bram", 20, null, WriteContext.Default);
        // Aria knows Bram only as "the stranger": a reader of the session's dice that must not learn "Bram" from a label.
        world.F.Knowledge.Record(world.Campaign, ["character:bram"],
            [new KnowerSpec { Who = "character:aria-vale", State = "met", KnownAs = "the stranger" }], WriteContext.Default);

        world.Writer.Rest(world.Campaign, "character:bram", "short", 1, null, ScriptedDiceRoller.Sequence(3), WriteContext.Default);

        var roll = Assert.Single(world.Dice());
        Assert.Equal("a character: hit dice", roll.Label);
        Assert.Equal(0, roll.Secret);
    }

    [Fact]
    public void Rest_AMemberThePartyKnowsDisguised_IsSecretAndNamedAsACharacter()
    {
        // D11: open only for a current member the party is SHOWN (undisguised); one the party knows as "the stranger" is not.
        using var world = SheetWorld.Player();
        world.Update("character:bram", Wizard);
        world.Writer.Damage(world.Campaign, "character:bram", 20, null, WriteContext.Default);
        world.F.Knowledge.Record(world.Campaign, ["character:bram"],
            [new KnowerSpec { Who = "party", State = "unrecognized", KnownAs = "the stranger" }], WriteContext.Default);

        var result = world.Writer.Rest(world.Campaign, "character:bram", "short", 1, null, ScriptedDiceRoller.Sequence(3), WriteContext.Default);

        var roll = Assert.Single(world.Dice());
        Assert.Equal(("a character: hit dice", 1), (roll.Label, roll.Secret));
        Assert.True(Assert.Single(result.Rolls).Secret);
    }

    [Fact]
    public void Rest_GivenFaces_LogNoRoll()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, 20, null, WriteContext.Default);
        var roller = ScriptedDiceRoller.Sequence();

        var result = world.Writer.Rest(world.Campaign, null, "short", null, [5, 1], roller, WriteContext.Default);

        Assert.Empty(roller.RequestedSides);
        Assert.Empty(world.Dice());
        Assert.Empty(result.Rolls);
        Assert.Equal(12 + 7 + 3, world.Required("character:aria-vale").Hp);
    }

    [Fact]
    public void Rest_DryRun_RollsButKeepsNeitherTheRollNorTheSheet()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, 20, null, WriteContext.Default);

        var result = world.Writer.Rest(world.Campaign, null, "short", 1, null, ScriptedDiceRoller.Sequence(3), new WriteContext { DryRun = true });

        Assert.Null(result.BatchId);
        Assert.Single(result.Rolls);
        Assert.Empty(world.Dice());
        Assert.Equal(12, world.Required("character:aria-vale").Hp);
    }

    [Fact]
    public void Rest_Long_RestoresEverythingWithoutDice()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, 20, null, WriteContext.Default);
        world.Writer.Use(world.Campaign, null, 1, false, null, 2, WriteContext.Default);

        var result = world.Writer.Rest(world.Campaign, null, "long rest", null, null, ScriptedDiceRoller.Sequence(), WriteContext.Default);

        var sheet = world.Required("character:aria-vale");
        Assert.Equal(sheet.MaxHp, sheet.Hp);
        Assert.Equal(0, sheet.SpellSlots["1"].Used);
        Assert.Empty(world.Dice());
        Assert.Equal("campaign_character/rest", world.Log(result.BatchId)[0].Tool);
    }

    /// <summary>
    /// CR06 (F2): a stored Unconscious on a sheet above 0 HP (a 2024 knock-out written back at 1 HP: Unconscious until the
    /// creature regains hit points or finishes the Short Rest that follows, SRD 5.2.1 "Knocking Out a Creature") ends with
    /// a short or a long rest, with a note and no "stays (until removed)" reminder; another until_removed condition stays.
    /// </summary>
    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    public void Rest_AboveZeroWithAStoredUnconscious_EndsIt(string kind)
    {
        using var world = SheetWorld.Player("2024");
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, world.Required("character:aria-vale").MaxHp!.Value - 1, null, WriteContext.Default);
        world.Writer.Condition(world.Campaign, null, ["unconscious", "cursed"], null, null, WriteContext.Default);

        var result = world.Writer.Rest(world.Campaign, null, kind, null, null, ScriptedDiceRoller.Sequence(), WriteContext.Default);

        Assert.Equal(["cursed"], world.Required("character:aria-vale").Conditions.Select(c => c.Name));
        Assert.Contains("Unconscious ended.", result.Notes);
        Assert.DoesNotContain(result.Reminders, r => r.Text.StartsWith("unconscious", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(world.Log(result.BatchId), r => r.FieldPath == "conditions");
    }

    /// <summary>
    /// CR06: at 0 HP (2014, stable) the stored Unconscious stays through a short rest that spends no Hit Die, and ends with
    /// one whose Hit Dice bring the sheet above 0.
    /// </summary>
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 5, false)]
    public void Rest_ShortFromZeroAndStable_EndsTheUnconsciousOnlyAboveZero(int hitDice, int face, bool stillUnconscious)
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, world.Required("character:aria-vale").MaxHp!.Value, null, WriteContext.Default);
        using (var connection = world.Open())
        {
            Dapper.SqlMapper.Execute(connection, "UPDATE character_sheet SET death_saves = '{\"successes\":0,\"failures\":0,\"stable\":true}' WHERE entity_id = @id",
                new { id = world.Id("character:aria-vale") });
        }

        var result = world.Writer.Rest(world.Campaign, null, "short", hitDice, hitDice == 0 ? null : [face], ScriptedDiceRoller.Sequence(), WriteContext.Default);

        var sheet = world.Required("character:aria-vale");
        Assert.Equal(stillUnconscious ? new[] { "unconscious" } : [], sheet.Conditions.Select(c => c.Name));
        Assert.Equal(!stillUnconscious, result.Notes.Contains("Unconscious ended."));
    }

    [Theory]
    [InlineData(null, "kind is required")]
    [InlineData("nap", "kind is \"nap\"")]
    public void Rest_BadKind_IsRefused(string? kind, string expected)
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Rest(world.Campaign, null, kind, null, null, ScriptedDiceRoller.Sequence(), WriteContext.Default));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }
}
