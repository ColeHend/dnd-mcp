using DndMcp.Domain.Campaign.Ops;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignWrite;
using Xunit;

namespace DndMcp.Tests.CampaignCharacters;

/// <summary>
/// The campaign_write advisory of contract §7.2: an upsert whose data on a character sets a key a sheet owns (level, hp,
/// max_hp, ac, xp, classes, spell_slots) is applied with a warning pointing at campaign_character; nothing else warns.
/// </summary>
public sealed class SheetShadowWarningTests
{
    [Theory]
    [InlineData("""{"level": 5}""", "data.level")]
    [InlineData("""{"HP": 30, "mood": "grim"}""", "data.hp")]
    [InlineData("""{"classes": ["wizard"], "spell_slots": {"1": 4}}""", "data.classes, data.spell_slots")]
    public void Upsert_CharacterDataShadowingTheSheet_WarnsAndApplies(string data, string keys)
    {
        using var world = SheetWorld.Dm();

        var result = world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Ref = "character:hero", Data = Op.Data(data) });

        var warning = Assert.Single(result.Warnings, w => w.Kind == WarningKinds.SheetShadow);
        Assert.Equal(WarningSeverities.Advisory, warning.Severity);
        Assert.StartsWith(keys + " on character:hero shadow", warning.Message, StringComparison.Ordinal);
        Assert.Contains("campaign_character {\"action\": \"update\", \"character\": \"character:hero\", \"sheet\": {\"", warning.Message, StringComparison.Ordinal);
        Assert.EndsWith("\": …}, \"campaign\": \"sea\"}.", warning.Message, StringComparison.Ordinal);
        Assert.Equal(0, warning.OpIndex);
        Assert.Contains("data", Assert.Single(result.Applied).ChangedFields.First(), StringComparison.Ordinal);
    }

    [Fact]
    public void Upsert_NewCharacterWithShadowingData_Warns()
    {
        using var world = SheetWorld.Dm();

        var result = world.F.Apply(world.Campaign,
            new CampaignOpSpec { Op = "upsert", Kind = "character", Name = "Mira", Data = Op.Data("""{"ac": 15}""") });

        Assert.Contains(result.Warnings, w => w.Kind == WarningKinds.SheetShadow && w.Message.StartsWith("data.ac on character:mira", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("character", """{"level": null}""")]
    [InlineData("character", """{"mood": "grim"}""")]
    [InlineData("location", """{"level": 3}""")]
    public void Upsert_NothingShadowed_DoesNotWarn(string kind, string data)
    {
        using var world = SheetWorld.Dm();

        var result = world.F.Apply(world.Campaign, new CampaignOpSpec { Op = "upsert", Kind = kind, Name = "Mira", Data = Op.Data(data) });

        Assert.DoesNotContain(result.Warnings, w => w.Kind == WarningKinds.SheetShadow);
    }
}
