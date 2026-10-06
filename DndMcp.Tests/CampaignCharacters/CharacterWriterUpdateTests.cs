using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Core;
using DndMcp.Domain.Characters;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignScenarios;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// <c>campaign_character update</c> through <see cref="CharacterWriter.Update"/>: one logged batch per call, the sheet
/// created on first use and patched after, P's derivations, the batch id only when something was logged, dry runs, and
/// which character a call names.
/// </summary>
public sealed class CharacterWriterUpdateTests
{
    [Fact]
    public void Update_FirstUse_CreatesTheSheetInOneBatchWithTheActionsTool()
    {
        using var world = SheetWorld.Dm();

        var result = world.Update("character:hero", FixtureSheets.BjornJson);

        Assert.True(result.Created);
        Assert.NotNull(result.BatchId);
        var log = world.Log(result.BatchId);
        var create = Assert.Single(log);
        Assert.Equal("create", create.Op);
        Assert.Equal("character_sheet", create.TargetTable);
        Assert.Equal("campaign_character/update", create.Tool);
        Assert.Equal(world.Id("character:hero"), create.EntityId);
        Assert.Contains(result.Changes, c => c.Field == "max_hp" && c.After == "85");
        Assert.Contains(result.Notes, n => n.Contains("derived", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(85, world.Required("character:hero").Hp);
    }

    [Fact]
    public void Update_ExistingSheet_LogsOnlyTheChangedFieldsAndPerKeyPatches()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", FixtureSheets.BjornJson);

        var result = world.Update("character:hero", """{ "ac": 16, "abilities": { "str": 20 }, "resources": [{ "name": "Rage", "used": 1 }] }""");

        Assert.False(result.Created);
        var fields = world.Log(result.BatchId).Select(r => r.FieldPath).ToList();
        Assert.Equal(["ac", "abilities.str", "resources.rage"], fields);
        var sheet = world.Required("character:hero");
        Assert.Equal(16, sheet.Ac);
        Assert.Equal(20, sheet.Abilities["str"]);
        Assert.Equal(14, sheet.Abilities["dex"]);
        Assert.Equal(1, sheet.Resources["rage"].Used);
    }

    [Fact]
    public void Update_SameValuesAgain_ChangesNothingAndHasNoBatch()
    {
        using var world = SheetWorld.Dm();
        world.Update("character:hero", """{ "level": 3, "ac": 14 }""");
        var rows = world.ChangeRows();

        var result = world.Update("character:hero", """{ "ac": 14 }""");

        Assert.Null(result.BatchId);
        Assert.Empty(result.Changes);
        Assert.Contains("Nothing changed.", result.Notes);
        Assert.Equal(rows, world.ChangeRows());
    }

    [Fact]
    public void Update_DryRun_ReportsTheChangeAndKeepsNothing()
    {
        using var world = SheetWorld.Dm();
        var rows = world.ChangeRows();

        var result = world.Writer.Update(world.Campaign, "character:hero", SheetWorld.Spec("""{ "level": 5 }"""), null, new WriteContext { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Null(result.BatchId);
        Assert.True(result.Created);
        Assert.Contains(result.Changes, c => c.Field == "level" && c.After == "5");
        Assert.Null(world.Sheet("character:hero"));
        Assert.Equal(rows, world.ChangeRows());
    }

    [Fact]
    public void Update_SimProfile_IsStoredCanonicalAndNeedsALevel()
    {
        using var world = SheetWorld.Player();
        var noLevel = Assert.Throws<DndInputException>(() =>
            world.Writer.Update(world.Campaign, null, null, FixtureSheets.Profile(FixtureSheets.BelmakorSimProfile), WriteContext.Default));
        Assert.Contains("sim_profile needs the sheet's level", noLevel.Message, StringComparison.Ordinal);

        var result = world.Writer.Update(world.Campaign, null, SheetWorld.Spec("""{ "classes": [{ "class": "wizard", "level": 12 }] }"""),
            FixtureSheets.Profile(FixtureSheets.BelmakorSimProfile), WriteContext.Default);

        Assert.Equal("character:aria-vale", result.Ref);
        var stored = world.Required("character:aria-vale").SimProfile!;
        Assert.Contains("\"Scimitar\"", stored, StringComparison.Ordinal);
        Assert.Equal(SimProfile.Prepare(FixtureSheets.Profile(FixtureSheets.BelmakorSimProfile), 12).Json, stored);
    }

    [Fact]
    public void Update_NothingGiven_IsRefused()
    {
        using var world = SheetWorld.Dm();

        var ex = Assert.Throws<DndInputException>(() => world.Writer.Update(world.Campaign, "character:hero", null, null, WriteContext.Default));

        Assert.Contains("update needs sheet", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hero", "character:hero")]
    [InlineData("character:hero", "character:hero")]
    [InlineData("side", null)]
    [InlineData("SIDEKICK", "character:sidekick")]
    public void Update_CharacterHandle_ResolvesExactlyOrByAWholeWordPrefix(string typed, string? expected)
    {
        using var world = SheetWorld.Dm();

        if (expected is null)
        {
            var ex = Assert.Throws<DndInputException>(() => world.Update(typed, """{ "level": 2 }"""));
            Assert.Contains("no character by that handle", ex.Message, StringComparison.Ordinal);
            return;
        }

        Assert.Equal(expected, world.Update(typed, """{ "level": 2 }""").Ref);
    }

    [Fact]
    public void Update_ShortSlugBeginningTwoCharacters_IsRefusedNotGuessed()
    {
        // §7.1: a short slug resolves when it begins exactly ONE character's slug; "bram" begins two.
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Bram Ash", Subtype = "pc", Visibility = "party" },
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Bram Oak", Subtype = "pc", Visibility = "party" });

        var ex = Assert.Throws<DndInputException>(() => world.Update("bram", """{ "level": 2 }"""));

        Assert.Contains("character \"bram\": no character by that handle", ex.Message, StringComparison.Ordinal);
        Assert.Null(world.Sheet("character:bram-ash"));
        Assert.Null(world.Sheet("character:bram-oak"));
        Assert.Equal("character:bram-oak", world.Update("bram-oak", """{ "level": 2 }""").Ref);
    }

    [Fact]
    public void Update_ShortSlugOfOneCharacter_Completes()
    {
        using var world = OnePieceScenario.Build();

        var result = new CharacterWriter(world.F.Db.Database).Update(world.Campaign, "bjorn", FixtureSheets.Spec(FixtureSheets.BjornJson), null, WriteContext.Default);

        Assert.Equal("character:bjorn-mountainfell", result.Ref);
    }

    [Fact]
    public void Update_DmCampaignWithoutCharacter_IsRefusedListingTheParty()
    {
        using var world = SheetWorld.Dm();

        var ex = Assert.Throws<DndInputException>(() => world.Update(null, """{ "level": 2 }"""));

        // D8's party (PartyRoster): Fallen is linked member_of the party but dead, so it is not "one of the party".
        Assert.EndsWith("character is required in a DM campaign: give the character's handle, one of the party: character:hero, character:sidekick.",
            ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("villain", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_HandleThatNamesNothingInADmCampaign_ListsTheCurrentPartyOnly()
    {
        using var world = SheetWorld.Dm();

        var ex = Assert.Throws<DndInputException>(() => world.Update("character:nobody", """{ "level": 2 }"""));

        Assert.EndsWith(" The party: character:hero, character:sidekick.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_PlayerCampaignWithoutCharacter_IsMyCharacter()
    {
        using var world = SheetWorld.Player();

        Assert.Equal("character:aria-vale", world.Update(null, """{ "level": 2 }""").Ref);
    }

    [Theory]
    [InlineData("character:nobody", "Create the character first: campaign_write {\"ops\": [{\"op\": \"upsert\", \"kind\": \"character\", \"name\": \"…\"}], \"campaign\": \"sea\"}")]
    [InlineData("location:the-harbor", "is a location, not a character")]
    [InlineData("other/character:hero", "names another campaign's entity")]
    [InlineData("sea/character:hero", "character \"sea/character:hero\": sea is this campaign's own slug; give the handle without it: \"character:hero\".")]
    [InlineData("SEA/hero", "SEA/hero\": sea is this campaign's own slug; give the handle without it: \"hero\".")]
    public void Update_NotACharacterOfThisCampaign_IsRefusedWithTheFix(string handle, string expected)
    {
        using var world = SheetWorld.Dm();
        var rows = world.ChangeRows();

        var ex = Assert.Throws<DndInputException>(() => world.Update(handle, """{ "level": 2 }"""));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(rows, world.ChangeRows());
    }

    [Fact]
    public void Update_DeletedCharacter_IsRefusedWithTheRestoreCall()
    {
        using var world = SheetWorld.Dm();
        world.F.Apply(world.Campaign, new DndMcp.Domain.Campaign.Ops.CampaignOpSpec { Op = "delete", Ref = "character:sidekick" });

        var ex = Assert.Throws<DndInputException>(() => world.Update("character:sidekick", """{ "level": 2 }"""));

        Assert.Contains("is deleted", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"op\": \"restore\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_WithSession_FilesTheBatchUnderIt()
    {
        using var world = SheetWorld.Dm();
        world.F.Played(world.Campaign, 3);

        var result = world.Update("character:hero", """{ "level": 2 }""", WriteContext.For(3, "after the fight"));

        Assert.Equal(3, result.SessionNumber);
        var row = Assert.Single(world.Log(result.BatchId));
        Assert.Equal("after the fight", row.Reason);
        Assert.NotNull(row.SessionId);
    }
}
