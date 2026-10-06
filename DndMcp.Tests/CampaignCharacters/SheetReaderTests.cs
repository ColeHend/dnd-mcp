using Dapper;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <see cref="SheetReader"/> (contract §7.1 get, §7.3, §7.4): the author's whole sheet with what is derived from it; the
/// public line for a current member another view is shown, built from the whitelist and the view-text check; every other
/// character read as one with no sheet; characters named the way the view may name them.
/// </summary>
public sealed class SheetReaderTests
{
    private const string Wizard = """
        { "player": "Cole", "species": "High Elf", "lineage": "Moon", "background": "Sage",
          "classes": [{ "class": "wizard", "subclass": "Bladesinger", "level": 5 }],
          "abilities": { "dex": 16, "con": 14, "int": 18 }, "ac": 13, "xp": 6500,
          "resources": [{ "name": "Contingency", "state": "set" }], "spells": ["Fly"], "notes": "planning a heist",
          "sheet_source": "fixture" }
        """;

    private static SheetGetResult Get(SheetWorld world, string? character, string perspective = "author") =>
        world.Reader.Get(world.Campaign, character, Perspective.Parse(perspective));

    [Fact]
    public void Get_HoldingsGainedTogether_AreInTheOrderOfTheirNamesWhateverTheirIds()
    {
        // Holdings of one call share created_at and their UUIDv7 ids are random within a millisecond: the name breaks the
        // tie, so a sheet reads the same every time. The ids here sort against the names (Zither's first).
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        using (var connection = world.Open())
        {
            var seed = new CampaignSeed(connection);
            var holder = world.Id("character:aria-vale");
            var zither = seed.Holding(world.Campaign.Id, holder, "Zither");
            var apple = seed.Holding(world.Campaign.Id, holder, "apple");
            connection.Execute("UPDATE holding SET id = @to WHERE id = @from", new { from = zither, to = "00000000-0000-7000-8000-000000000001" });
            connection.Execute("UPDATE holding SET id = @to WHERE id = @from", new { from = apple, to = "00000000-0000-7000-8000-000000000002" });
        }

        var inventory = Assert.Single(Get(world, null).Characters).Author!.Inventory;

        Assert.Equal(["apple", "Zither"], inventory.Select(h => h.Name));
    }

    [Fact]
    public void Get_AuthorDefault_IsMyCharactersWholeSheetWithWhatIsDerived()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Inventory(world.Campaign, null, [new InventoryItem { Item = "Spellbook" }], WriteContext.Default);
        world.Writer.Currency(world.Campaign, null, new Coins(Gp: 30, Sp: 4), WriteContext.Default);
        world.Writer.Currency(world.Campaign, null, new Coins(Gp: -5), WriteContext.Default);
        using (var connection = world.Open())
        {
            new CampaignSeed(connection).Holding(world.Campaign.Id, world.Id("character:aria-vale"), "Spent scroll", quantity: 0);
        }

        var result = Get(world, null);

        Assert.True(result.AuthorView);
        Assert.False(result.IsList);
        var read = Assert.Single(result.Characters);
        Assert.Equal(("character:aria-vale", "Aria Vale"), (read.Ref, read.Name));
        Assert.Null(read.Line);
        var sheet = read.Author!;
        Assert.Equal("2014", sheet.Edition);
        Assert.Equal(32, sheet.EffectiveMaxHp);
        Assert.Equal(3, sheet.ProficiencyBonus);
        Assert.Equal(3, sheet.InitiativeBonus);
        Assert.Equal(14000, sheet.NextXpThreshold);
        Assert.Equal("Cole", sheet.Sheet.Player);
        Assert.Equal("Spellbook", Assert.Single(sheet.Inventory).Name);
        Assert.Equal(new CoinsView(0, 4, 0, 25, 0), sheet.Coins);
        Assert.False(sheet.Dying || sheet.Dead);
    }

    [Fact]
    public void Get_AuthorNoSheet_ReadsAsNoSheetYet()
    {
        using var world = SheetWorld.Dm();

        var read = Assert.Single(Get(world, "character:sidekick").Characters);

        Assert.False(read.HasSheet);
        Assert.Equal("character:sidekick", read.Ref);
    }

    [Fact]
    public void Get_DmCampaignNoCharacter_ListsEveryCurrentMemberAndTheExcluded()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3 }""");

        var result = Get(world, null);

        Assert.True(result.IsList);
        Assert.Equal(["character:hero", "character:sidekick"], result.Characters.Select(c => c.Ref));
        Assert.Equal([true, false], result.Characters.Select(c => c.HasSheet));
        Assert.Equal("character:fallen", Assert.Single(result.Excluded).Handle);
    }

    [Fact]
    public void Party_PlayerCampaign_IsTheListFormToo()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);

        var author = world.Reader.Party(world.Campaign);
        var party = world.Reader.Party(world.Campaign, Perspective.Parse("party"));

        Assert.True(author.IsList);
        Assert.Equal(["character:aria-vale", "character:bram"], author.Characters.Select(c => c.Ref));
        Assert.Equal("Cole", author.Characters[0].Author!.Sheet.Player);
        Assert.Equal("character:aria-vale", Assert.Single(party.Characters).Ref);
        Assert.Null(party.Characters[0].Author);
    }

    [Fact]
    public void Get_PartyViewOfAMember_IsThePublicLineOnly()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Damage(world.Campaign, null, 5, null, WriteContext.Default);
        world.Writer.Condition(world.Campaign, null, ["poisoned"], null, null, WriteContext.Default);

        var read = Assert.Single(Get(world, "character:aria-vale", "party").Characters);

        Assert.Null(read.Author);
        var line = read.Line!;
        Assert.Equal(("character:aria-vale", "Aria Vale", 5, "High Elf"), (line.Ref, line.Name, line.Level, line.Species));
        Assert.Equal((27, 32, 0, 13, 0), (line.Hp, line.EffectiveMaxHp, line.TempHp, line.Ac, line.Exhaustion));
        Assert.Equal([new PublicClassLine("Wizard", 5, "Bladesinger")], line.Classes);
        Assert.Equal(["poisoned"], line.Conditions);
    }

    [Theory]
    [InlineData("party")]
    [InlineData("table")]
    [InlineData("dm")]
    [InlineData("character:bram")]
    public void Get_NonAuthorViews_NeverCarryAuthorFields(string perspective)
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);
        world.Writer.Use(world.Campaign, null, 1, false, null, 1, WriteContext.Default);
        world.Writer.Inventory(world.Campaign, null, [new InventoryItem { Item = "Ring of Spell Storing" }], WriteContext.Default);
        world.Writer.Currency(world.Campaign, null, new Coins(Gp: 77), WriteContext.Default);

        var result = Get(world, "character:aria-vale", perspective);

        Assert.True(Assert.Single(result.Characters).HasSheet);
        DndMcp.Tests.CampaignRead.LeakAssert.Clean(result,
            ["Cole", "Moon", "Lineage", "Sage", "Contingency", "Fly", "heist", "fixture", "6500", "Ring of Spell Storing", "77", "spell_slots", "player", "xp"],
            $"{perspective} reads the public line only");
    }

    [Fact]
    public void Get_PublicLine2024_ShowsExhaustionAndTheEffectiveMaximum()
    {
        // The line's maximum is HitPointMath's effective one (max_hp − max_hp_reduction), not the stored max_hp.
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 5, "max_hp": 40, "max_hp_reduction": 10, "hp": 25 }""");
        world.Writer.Condition(world.Campaign, "character:hero", ["exhaustion"], null, 2, WriteContext.Default);

        var line = Assert.Single(Get(world, world.F.Entity(world.Campaign, "character:hero").SeqHandle, "party").Characters).Line!;

        Assert.Equal((25, 30, 2), (line.Hp, line.EffectiveMaxHp, line.Exhaustion));
    }

    [Fact]
    public void Get_PublicLine2014AtExhaustion4_ShowsTheHalvedMaximum()
    {
        using var world = SheetWorld.Player();
        world.Update("character:bram", """{ "level": 5, "max_hp": 40 }""");
        world.Writer.Condition(world.Campaign, "character:bram", ["exhaustion"], null, 4, WriteContext.Default);

        var line = Assert.Single(Get(world, "character:bram", "party").Characters).Line!;

        Assert.Equal((20, 4), (line.EffectiveMaxHp, line.Exhaustion));
    }

    [Theory]
    [InlineData("""{ "level": 3 }""", null)]
    [InlineData("""{ "level": 3, "xp": 0 }""", 2700)]
    [InlineData("""{ "level": 20, "xp": 400000 }""", null)]
    public void Get_Author_NextXpThresholdOnlyWhenTheSheetTracksXp(string json, int? next)
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", json);

        Assert.Equal(next, Assert.Single(Get(world, "character:hero").Characters).Author!.NextXpThreshold);
    }

    [Theory]
    [InlineData("location:the-harbor")]
    [InlineData("faction:the-party")]
    public void Get_NonAuthorNamingAVisibleNonCharacter_IsTheNotFound(string handle)
    {
        // A sheet belongs to a character: another kind the view can see reads as a handle that names no character, never
        // as a character with no sheet yet.
        using var world = SheetWorld.Dm();

        var ex = Assert.Throws<DndInputException>(() => Get(world, handle, "party"));

        Assert.Contains("nothing by that handle for this perspective", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_PartyViewOfAnNpcsSheet_ReadsExactlyAsNoSheet()
    {
        using var world = SheetWorld.Player();
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Ref = "character:old-guard", Visibility = "party" });
        world.Update("character:old-guard", Wizard);
        world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Plain Npc", Subtype = "npc", Visibility = "party" });

        var withSheet = Assert.Single(Get(world, "character:old-guard", "party").Characters);
        var withoutSheet = Assert.Single(Get(world, "character:plain-npc", "party").Characters);

        Assert.False(withSheet.HasSheet);
        Assert.Equal(DndMcp.Tests.CampaignRead.LeakAssert.Serialize(withoutSheet with { Ref = "x", Name = "x" }),
            DndMcp.Tests.CampaignRead.LeakAssert.Serialize(withSheet with { Ref = "x", Name = "x" }));
    }

    [Fact]
    public void Get_PartyViewOfAHiddenCharacter_IsTheSameNotFoundAsNoSuchHandle()
    {
        using var world = SheetWorld.Player();
        world.Update("character:old-guard", Wizard);

        var hidden = Assert.Throws<DndInputException>(() => Get(world, "character:old-guard", "party"));
        var missing = Assert.Throws<DndInputException>(() => Get(world, "character:old-gard", "party"));

        Assert.Equal(missing.Message.Replace("old-gard", "X", StringComparison.Ordinal), hidden.Message.Replace("old-guard", "X", StringComparison.Ordinal));
        Assert.DoesNotContain("Old Guard", hidden.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_DmCampaignPartyList_PrintsOnlyTheLinesItGives()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3 }""");
        world.Update("character:fallen", """{ "level": 3 }""");

        var result = Get(world, null, "party");

        Assert.True(result.IsList);
        var hero = Assert.Single(result.Characters);
        Assert.Equal((world.F.Entity(world.Campaign, "character:hero").SeqHandle, "Hero Prime"), (hero.Ref, hero.Name));
        Assert.Empty(result.Excluded);
    }

    [Fact]
    public void Get_DeadMembersSheet_ReadsAsNoSheetOutsideTheAuthorView()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:fallen", """{ "level": 3 }""");

        Assert.False(Assert.Single(Get(world, "character:fallen", "party").Characters).HasSheet);
        Assert.False(Assert.Single(Get(world, "fallen", "party").Characters).HasSheet);
        Assert.True(Assert.Single(Get(world, "character:fallen").Characters).HasSheet);
    }

    [Fact]
    public void Get_NonAuthorShortSlug_CompletesAsAPerspectiveDoes()
    {
        using var world = SheetWorld.Player();
        world.Update(null, Wizard);

        Assert.Equal("character:aria-vale", Assert.Single(Get(world, "aria", "party").Characters).Ref);
    }

    [Fact]
    public void Get_HomebrewClassAndConditionNamingAnUnknownEntity_AreLeftOutOrAnEffect()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """
            { "classes": [{ "class": "Villain Hunter", "subclass": "Oath of the Villain", "level": 3, "hit_die": 10 },
                          { "class": "fighter", "subclass": "Champion", "level": 1 }], "species": "Halfling" }
            """);
        world.Writer.Condition(world.Campaign, "character:hero", ["marked by The Villain", "Inspired"], null, null, WriteContext.Default);

        var line = Assert.Single(Get(world, world.F.Entity(world.Campaign, "character:hero").SeqHandle, "party").Characters).Line!;

        Assert.Equal([new PublicClassLine("Fighter", 1, "Champion")], line.Classes);
        Assert.Equal("Halfling", line.Species);
        Assert.Equal(["an effect", "Inspired"], line.Conditions);
        Assert.Equal(4, line.Level);
        DndMcp.Tests.CampaignRead.LeakAssert.Clean(line, ["Villain"], "the party has not met the villain");

        var author = Assert.Single(Get(world, "character:hero").Characters).Author!;
        Assert.Contains(author.Sheet.Classes, c => c.Class == "Villain Hunter");
    }
}
