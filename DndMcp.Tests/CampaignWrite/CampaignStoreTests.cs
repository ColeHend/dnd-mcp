using System.Text.Json;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Core;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignDb;
using Xunit;

namespace DndMcp.Tests.CampaignWrite;

/// <summary>
/// Invariant: a campaign is created as one batch with its party faction (and, for a player campaign, the player's
/// character as a party member), settings are validated per §3.7, and every campaign tool resolves the same campaign
/// from the same inputs (argument → process current → active → the only one → a refusal listing them). A wrong
/// resolution writes to the wrong campaign; a party faction missing breaks every party verdict.
/// </summary>
public sealed class CampaignStoreTests : IDisposable
{
    private readonly WriteFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void Create_PlayerCampaign_WritesCampaignPartyAndCharacterAsOneBatch()
    {
        var result = _f.Store.Create("Belmakor — sky-world", "Player", "2014", slug: "belmakor", myCharacter: "Belmakor Silverwind",
            settings: Op.Data("{\"dm_pronouns\": \"she/her\", \"table_style\": \"near-TPK\"}"));

        var campaign = result.Campaign;
        Assert.Equal("belmakor", campaign.Slug);
        Assert.Equal(CampaignValues.Roles.Player, campaign.Role);
        Assert.Equal(CampaignValues.Rulesets.R2014, campaign.Ruleset);
        Assert.Equal("faction:the-party", result.Party);
        Assert.Equal("character:belmakor-silverwind", result.MyCharacter);
        var party = _f.Entity(campaign, "faction:the-party");
        Assert.Equal((CampaignValues.Subtypes.PartyFaction, CampaignValues.Visibilities.Party), (party.Subtype, party.Visibility));
        Assert.Equal(party.Id, campaign.PartyId);
        var pc = _f.Entity(campaign, "character:belmakor-silverwind");
        Assert.Equal((CampaignValues.Subtypes.Pc, CampaignValues.Visibilities.Party), (pc.Subtype, pc.Visibility));
        Assert.Equal(pc.Id, campaign.MyCharacterId);
        Assert.Equal(1, _f.Count("SELECT count(*) FROM relation WHERE from_id = @pc AND rel = 'member_of' AND to_id = @party AND status = 'current'",
            new { pc = pc.Id, party = party.Id }));
        var batch = Assert.Single(_f.Log().Select(r => r.BatchId).Distinct());
        Assert.Equal(result.BatchId, batch);
        Assert.All(_f.Log(), r => Assert.Equal(campaign.Id, r.CampaignId));
        Assert.Equal("{\"dm_pronouns\":\"she/her\",\"table_style\":\"near-TPK\"}", campaign.Settings);
    }

    [Fact]
    public void Create_DmCampaign_HasAPartyAndNoCharacter()
    {
        var result = _f.Store.Create("One Piece", "dm", "2024", partyName: "The Straw Hats");

        Assert.Null(result.MyCharacter);
        Assert.Null(result.Campaign.MyCharacterId);
        Assert.Equal("faction:the-straw-hats", result.Party);
        // The campaign, the party faction, then the campaign's party_id: one batch.
        Assert.Equal(["create", "create", "update"], _f.Log().Select(r => r.Op));
    }

    [Theory]
    [InlineData("", "player", "2024", null, "name is required")]
    [InlineData("X", "gm", "2024", null, "role \"gm\" is not a role")]
    [InlineData("X", "dm", "5e", null, "ruleset \"5e\" is not a ruleset")]
    [InlineData("X", "dm", "2024", "Keras", "my_character is for a player campaign")]
    public void Create_InvalidArgument_IsRefusedAndWritesNothing(string name, string role, string ruleset, string? myCharacter, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Store.Create(name, role, ruleset, myCharacter: myCharacter));

        Assert.Contains(expected, ex.Message);
        Assert.Empty(_f.Store.List());
    }

    [Fact]
    public void Create_DerivedSlugTaken_GetsASuffixAndAWarning()
    {
        _f.Campaign("One Piece");

        var second = _f.Store.Create("One Piece", "dm", "2024");

        Assert.Equal("one-piece-2", second.Campaign.Slug);
        Assert.Contains(second.Warnings, w => w.Kind == WarningKinds.SlugCollision && w.Message.Contains("one-piece-2"));
    }

    [Fact]
    public void Create_ExplicitSlugTaken_IsRefused()
    {
        _f.Campaign("One Piece");

        var ex = Assert.Throws<DndInputException>(() => _f.Store.Create("Other", "dm", "2024", slug: "one-piece"));

        Assert.Contains("slug one-piece is taken", ex.Message);
    }

    // The offset's range is -10..10 inclusive (§3.7): both ends are stored (review M20, mutant W02 refused them).
    [Theory]
    [InlineData("{\"effective_level_offset\": 1}", "{\"effective_level_offset\":1}")]
    [InlineData("{\"effective_level_offset\": 10}", "{\"effective_level_offset\":10}")]
    [InlineData("{\"effective_level_offset\": -10}", "{\"effective_level_offset\":-10}")]
    [InlineData("{\"default_visibility\": \"Party\"}", "{\"default_visibility\":\"party\"}")]
    [InlineData("{\"homebrew_ok\": true, \"pace\": 2.5}", "{\"homebrew_ok\":true,\"pace\":2.5}")]
    [InlineData("{\"dropped\": null}", "{}")]
    public void Create_Settings_AreStoredCanonically(string settings, string stored)
    {
        var campaign = _f.Campaign(settingsJson: settings);

        Assert.Equal(stored, campaign.Settings);
    }

    [Theory]
    [InlineData("{\"effective_level_offset\": 11}", "effective_level_offset must be a whole number from -10 to 10")]
    [InlineData("{\"effective_level_offset\": -11}", "effective_level_offset must be a whole number from -10 to 10")]
    [InlineData("{\"effective_level_offset\": 1.5}", "effective_level_offset must be a whole number")]
    [InlineData("{\"effective_level_offset\": \"1\"}", "effective_level_offset must be a whole number")]
    [InlineData("{\"default_visibility\": \"secret\"}", "default_visibility must be public, party, restricted or author")]
    [InlineData("{\"house\": {\"a\": 1}}", "settings house must be a string, number or boolean")]
    [InlineData("{\"a.b\": 1}", "settings key \"a.b\" must be letters")]
    public void Create_InvalidSettings_AreRefused(string settings, string expected)
    {
        var ex = Assert.Throws<DndInputException>(() => _f.Campaign(settingsJson: settings));

        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Create_DryRun_ReturnsTheCampaignAndWritesNothing()
    {
        var result = _f.Store.Create("Dry", "dm", "2024", context: new WriteContext { DryRun = true });

        Assert.True(result.DryRun);
        Assert.Null(result.BatchId);
        Assert.Equal("dry", result.Campaign.Slug);
        Assert.Empty(_f.Store.List());
        Assert.Equal(0, _f.Count("SELECT count(*) FROM change_log"));
    }

    [Fact]
    public void Update_SettingsMergePatch_ChangesKeysAndNullRemovesOne()
    {
        var campaign = _f.Campaign(settingsJson: "{\"effective_level_offset\": 1, \"pace\": \"slow\"}");

        var result = _f.Store.Update(campaign, new CampaignUpdate
        {
            Settings = Op.Data("{\"effective_level_offset\": 2, \"pace\": null, \"default_visibility\": \"author\"}"),
        });

        Assert.Equal("{\"effective_level_offset\":2,\"default_visibility\":\"author\"}", result.Campaign.Settings);
        Assert.Equal(["settings.effective_level_offset", "settings.pace", "settings.default_visibility"], result.ChangedFields);
        var rows = _f.Log(result.BatchId);
        Assert.Equal(["settings.effective_level_offset", "settings.pace", "settings.default_visibility"], rows.Select(r => r.FieldPath));
        Assert.Null(rows[1].NewValue);
    }

    [Fact]
    public void Update_Fields_ChangeAndAreLogged()
    {
        var campaign = _f.Campaign(role: "player", myCharacter: "Belmakor");
        _f.Apply(campaign, Op.Upsert("location", "Flotsam"), Op.Upsert("character", "Vars", "pc"));

        var result = _f.Store.Update(campaign, new CampaignUpdate
        {
            Name = "Renamed",
            Status = "Hiatus",
            Ruleset = "Mixed",
            DmName = "Her",
            CurrentLocation = "location:flotsam",
            CurrentIngame = "day 3",
            MyCharacter = "character:vars",
        });

        Assert.Equal(("Renamed", "hiatus", "mixed", "Her", "day 3"), (result.Campaign.Name, result.Campaign.Status, result.Campaign.Ruleset, result.Campaign.DmName, result.Campaign.CurrentIngame));
        Assert.Equal(_f.Entity(campaign, "location:flotsam").Id, result.Campaign.CurrentLocationId);
        Assert.Equal(_f.Entity(campaign, "character:vars").Id, result.Campaign.MyCharacterId);
        Assert.Equal(7, _f.Log(result.BatchId).Count);
    }

    [Fact]
    public void Update_Nothing_WritesNoRows()
    {
        var campaign = _f.Campaign("Same");

        var result = _f.Store.Update(campaign, new CampaignUpdate { Name = "Same" });

        Assert.Empty(result.ChangedFields);
        Assert.Empty(_f.Log(result.BatchId));
    }

    [Fact]
    public void List_NoDatabase_IsEmptyAndCreatesNoFile()
    {
        using var empty = new CampaignTestDb(create: false);
        var store = new CampaignStore(empty.Database);

        Assert.Empty(store.List());
        Assert.Null(store.ActiveCampaignId());
        Assert.False(File.Exists(empty.DatabasePath));
        var ex = Assert.Throws<DndInputException>(() => store.Resolve(null));
        Assert.Contains("no campaigns yet", ex.Message);
        Assert.False(File.Exists(empty.DatabasePath));
    }

    [Fact]
    public void Resolve_OnlyCampaign_IsChosenWithoutAnArgument()
    {
        var only = _f.Campaign("Only");

        Assert.Equal(only.Id, _f.Store.Resolve(null).Id);
    }

    [Theory]
    [InlineData("belmakor", "belmakor")]
    [InlineData("BELMAKOR", "belmakor")]
    [InlineData("Belmakor Sky World", "belmakor")]
    [InlineData("one-piece", "one-piece")]
    public void Resolve_Argument_BySlugOrExactName(string argument, string expected)
    {
        _f.Store.Create("Belmakor Sky World", "player", "2014", slug: "belmakor");
        _f.Store.Create("One Piece", "dm", "2024");

        Assert.Equal(expected, _f.Store.Resolve(argument).Slug);
    }

    [Fact]
    public void Resolve_Order_ArgumentThenProcessCurrentThenActiveThenRefusal()
    {
        var a = _f.Campaign("Alpha");
        var b = _f.Campaign("Beta");
        var c = _f.Campaign("Gamma");

        var none = Assert.Throws<DndInputException>(() => _f.Store.Resolve(null));
        Assert.Contains("alpha (dm, 2024), beta (dm, 2024), gamma (dm, 2024)", none.Message);
        Assert.Contains("\"action\": \"use\"", none.Message);

        _f.Store.SetActive(b.Id);
        Assert.Equal(b.Id, _f.Store.ActiveCampaignId());
        Assert.Equal(b.Id, _f.Store.Resolve(null).Id);
        Assert.Equal(c.Id, _f.Store.Resolve(null, processCurrentCampaignId: c.Id).Id);
        Assert.Equal(a.Id, _f.Store.Resolve("alpha", processCurrentCampaignId: c.Id).Id);
        Assert.Equal(b.Id, _f.Store.Resolve(null, processCurrentCampaignId: "gone").Id);
    }

    [Fact]
    public void Resolve_UnknownArgument_ListsTheCampaigns()
    {
        _f.Campaign("Alpha");

        var ex = Assert.Throws<DndInputException>(() => _f.Store.Resolve("zeta"));

        Assert.Contains("No campaign \"zeta\"", ex.Message);
        Assert.Contains("alpha (dm, 2024)", ex.Message);
    }

    [Fact]
    public void SetActive_IsNotLoggedAndUnknownCampaignIsRefused()
    {
        var campaign = _f.Campaign();
        var before = _f.Count("SELECT count(*) FROM change_log");

        _f.Store.SetActive(campaign.Id);

        Assert.Equal(before, _f.Count("SELECT count(*) FROM change_log"));
        Assert.Throws<DndInputException>(() => _f.Store.SetActive("0199aaaa-0000-7000-8000-000000000000"));
    }

    [Fact]
    public void Create_ThenUndo_RemovesEverythingItCreated()
    {
        var before = _f.Dump();
        var created = _f.Store.Create("Undo Me", "player", "2024", myCharacter: "Hero");

        var undo = _f.History.Undo(created.Campaign, created.BatchId!, WriteContext.Default);

        Assert.Equal(created.BatchId, undo.UndoneBatchId);
        Assert.Equal(before, _f.Dump());
    }
}
