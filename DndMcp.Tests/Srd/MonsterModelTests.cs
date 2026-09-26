using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins that the typed monster models cover the vendored data completely and read it correctly, for both editions.
///
/// <list type="number">
/// <item><b>Complete:</b> every monster reads with unknown fields DISALLOWED and no exception. A field the models do
/// not cover fails here, not silently in production.</item>
/// <item><b>Lossless:</b> every monster, serialized back out, equals its vendored JSON. A converter that collapses a
/// Choice, a count that changes type or a dropped nested field all fail with the path that differs.</item>
/// <item><b>Correct mapping:</b> spot checks on stat blocks whose numbers are known (adult red dragon in both
/// editions, djinni, archmage, brass wyrmling), including the edition-specific meanings the per-edition models
/// exist to keep apart.</item>
/// </list>
/// </summary>
public sealed class MonsterModelTests
{
    [Fact]
    public void ReadArray_Monsters2014_StrictOptions_ReadsEveryMonster()
    {
        var monsters = SrdJson.ReadArray<Monster2014>(
            SrdTestContent.FilePath(SrdEdition.Edition2014, SrdFileNames.Monsters), SrdTestContent.StrictOptions);

        Assert.Equal(334, monsters.Count);
    }

    [Fact]
    public void ReadArray_Monsters2024_StrictOptions_ReadsEveryMonster()
    {
        var monsters = SrdJson.ReadArray<Monster2024>(
            SrdTestContent.FilePath(SrdEdition.Edition2024, SrdFileNames.Monsters), SrdTestContent.StrictOptions);

        Assert.Equal(341, monsters.Count);
    }

    [Fact]
    public void Serialize_EveryMonster2014_RoundTripsToVendoredJson()
    {
        JsonDiff.AssertRecordsRoundTrip(SrdTestContent.Raw(SrdEdition.Edition2014, SrdFileNames.Monsters), SrdTestContent.Monsters2014, m => m.Index);
    }

    [Fact]
    public void Serialize_EveryMonster2024_RoundTripsToVendoredJson()
    {
        JsonDiff.AssertRecordsRoundTrip(SrdTestContent.Raw(SrdEdition.Edition2024, SrdFileNames.Monsters), SrdTestContent.Monsters2024, m => m.Index);
    }

    [Fact]
    public void AdultRedDragon2014_StatBlock_ReadsKnownValues()
    {
        var dragon = Monster2014("adult-red-dragon");

        var ac = Assert.Single(dragon.ArmorClass);
        Assert.Equal("natural", ac.Type);
        Assert.Equal(19, ac.Value);
        Assert.Equal(256, dragon.HitPoints);
        Assert.Equal("19d12+133", dragon.HitPointsRoll);
        Assert.Equal(17, dragon.ChallengeRating);
        Assert.Equal(18000, dragon.Xp);
        Assert.Equal(6, dragon.ProficiencyBonus);
        Assert.Equal("80 ft.", dragon.Speed.Fly);
        Assert.Equal(23, dragon.Senses.PassivePerception);

        var multiattack = Action(dragon.Actions, "Multiattack");
        Assert.Equal(MultiattackTypes.Actions, multiattack.MultiattackType);
        Assert.Equal(
            ["Frightful Presence x1", "Bite x1", "Claw x2"],
            multiattack.Actions!.Select(a => $"{a.ActionName} x{a.Count.Value}"));

        var breath = Action(dragon.Actions, "Fire Breath");
        Assert.Equal(UsageTypes.RechargeOnRoll, breath.Usage!.Type);
        Assert.Equal("1d6", breath.Usage.Dice);
        Assert.Equal(5, breath.Usage.MinValue);
        Assert.Equal(21, breath.Dc!.DcValue);
        Assert.Equal("18d6", Assert.Single(breath.Damage!).Damage!.DamageDice);

        var bite = Action(dragon.Actions, "Bite");
        Assert.Equal(14, bite.AttackBonus);
        Assert.Equal(["2d10+8", "2d6"], bite.Damage!.Select(d => d.Damage!.DamageDice));

        Assert.Contains(dragon.LegendaryActions!, a => a.Name == "Wing Attack (Costs 2 Actions)");
    }

    [Fact]
    public void AdultRedDragon2024_StatBlock_ReadsKnownValues()
    {
        var dragon = Monster2024("adult-red-dragon");

        Assert.Equal(19, Assert.Single(dragon.ArmorClass).Value);
        Assert.Equal("19d12 + 133", dragon.HitPointsRoll);
        Assert.Equal(18000, dragon.Xp);
        Assert.Equal(20000, dragon.XpInLair);

        var legendaryResistance = Action(dragon.SpecialAbilities, "Legendary Resistance");
        Assert.Equal(3, legendaryResistance.Usage!.Times);
        Assert.Equal(4, legendaryResistance.Usage.TimesInLair);

        // "Makes three Rend attacks. It can replace one attack with Scorching Ray": 2 fixed + choose 1.
        var multiattack = Action(dragon.Actions, "Multiattack");
        var fixedStep = Assert.Single(multiattack.Actions!);
        Assert.Equal("Rend", fixedStep.ActionName);
        Assert.Equal(2, fixedStep.Count.Value);
        Assert.Equal(1, multiattack.ActionOptions!.Choose);
        Assert.Equal(ChoiceTypes.Action, multiattack.ActionOptions.Type);
        Assert.Equal(["Rend", "Scorching Ray"], multiattack.ActionOptions.From.Options.Select(o => o.ActionName));
        Assert.All(multiattack.ActionOptions.From.Options, o => Assert.Equal(1, o.Count!.Value));

        var breath = Action(dragon.Actions, "Fire Breath");
        Assert.Equal(SaveSuccessTypes.Half, breath.Dc!.SuccessType);
        Assert.Equal("17d6", Assert.Single(breath.Damage!).DamageDice);
    }

    // The reason MonsterSpell2024 names the field CastLevel: 2024 "level" is the level the monster casts at, not the
    // spell's own level. The spells file is the independent source for the base level.
    [Theory]
    [InlineData("adult-red-dragon", "command", 2, 1)]
    [InlineData("adult-red-dragon", "fireball", 3, 3)]
    public void MonsterSpell2024_CastLevel_DiffersFromSpellBaseLevel(string monster, string spell, int castLevel, int baseLevel)
    {
        var spellcasting = Action(Monster2024(monster).Actions, "Spellcasting").Spellcasting!;

        var entry = Assert.Single(spellcasting.Spells, s => s.Index == spell);

        Assert.Equal(castLevel, entry.CastLevel);
        Assert.Equal(baseLevel, SrdTestContent.Spells2024.Single(s => s.Index == spell).Level);
    }

    [Fact]
    public void Archmage2014_Spellcasting_IsCasterLevelWithSlotsAndBaseSpellLevels()
    {
        var spellcasting = Action(Monster2014("archmage").SpecialAbilities, "Spellcasting").Spellcasting!;

        Assert.Equal(18, spellcasting.Level);
        Assert.Equal("wizard", spellcasting.School);
        Assert.Equal(17, spellcasting.Dc);
        Assert.Equal(9, spellcasting.Modifier);
        Assert.Equal(4, spellcasting.Slots![1]);
        Assert.Equal(1, spellcasting.Slots[9]);

        var disguiseSelf = Assert.Single(spellcasting.Spells, s => s.Name == "Disguise Self");
        Assert.Equal(1, disguiseSelf.Level);
        Assert.Equal(UsageTypes.AtWill, disguiseSelf.Usage!.Type);
        Assert.Equal("/api/2014/spells/disguise-self", disguiseSelf.Url);
    }

    [Fact]
    public void Djinni2014_ScimitarRider_IsADamageChoice()
    {
        var scimitar = Action(Monster2014("djinni").Actions, "Scimitar");

        Assert.Collection(
            scimitar.Damage!,
            first =>
            {
                Assert.False(first.IsChoice);
                Assert.Equal("2d6+5", first.Damage!.DamageDice);
                Assert.Equal("slashing", first.Damage.DamageType.Index);
            },
            second =>
            {
                Assert.True(second.IsChoice);
                Assert.Null(second.Damage);
                Assert.Equal(1, second.Choice!.Choose);
                Assert.Equal(ChoiceTypes.Damage, second.Choice.Type);
                Assert.Equal(["lightning", "thunder"], second.Choice.From.Options.Select(o => o.DamageType.Index));
                Assert.All(second.Choice.From.Options, o => Assert.Equal("1d6", o.DamageDice));
            });
    }

    [Fact]
    public void BrassDragonWyrmling2014_BreathWeapons_AreAChoiceOfBreaths()
    {
        var breaths = Action(Monster2014("brass-dragon-wyrmling").Actions, "Breath Weapons");

        Assert.Equal(UsageTypes.RechargeOnRoll, breaths.Usage!.Type);
        Assert.Equal(1, breaths.Options!.Choose);
        Assert.Equal(ChoiceTypes.Attack, breaths.Options.Type);

        var fire = Assert.Single(breaths.Options.From.Options, o => o.Name == "Fire Breath");
        Assert.Equal("dex", fire.Dc.DcType.Index);
        Assert.Equal(11, fire.Dc.DcValue);
        Assert.Equal(SaveSuccessTypes.Half, fire.Dc.SuccessType);
        Assert.Equal("4d6", Assert.Single(fire.Damage!).DamageDice);

        var sleep = Assert.Single(breaths.Options.From.Options, o => o.Name == "Sleep Breath");
        Assert.Null(sleep.Damage);
    }

    [Theory]
    [InlineData("goblin", 0.25, 50)]
    [InlineData("rat", 0, 10)]
    [InlineData("tarrasque", 30, 155000)]
    public void Monster2014_ChallengeRating_ReadsFractionalAndWholeValues(string index, double challengeRating, int xp)
    {
        var monster = Monster2014(index);

        Assert.Equal(challengeRating, monster.ChallengeRating);
        Assert.Equal(xp, monster.Xp);
    }

    private static Monster2014 Monster2014(string index) => SrdTestContent.Monsters2014.Single(m => m.Index == index);

    private static Monster2024 Monster2024(string index) => SrdTestContent.Monsters2024.Single(m => m.Index == index);

    private static MonsterAction2014 Action(IReadOnlyList<MonsterAction2014>? actions, string name) =>
        Assert.Single(actions!, a => a.Name == name);

    private static MonsterAction2024 Action(IReadOnlyList<MonsterAction2024>? actions, string name) =>
        Assert.Single(actions!, a => a.Name == name);
}
