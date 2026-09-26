using DndMcp.Repository.Srd;
using DndMcp.Repository.Srd.Models;
using Xunit;

namespace DndMcp.Tests.Srd;

/// <summary>
/// Pins that the typed spell models cover the vendored data completely (strict read), losslessly (round trip) and
/// correctly (spot checks), for both editions. The spot checks cover the shape differences the per-edition models
/// exist for: 2014 damage tables keyed by slot or character level, and 2024's single base-slot entry with its
/// scaling left in prose.
/// </summary>
public sealed class SpellModelTests
{
    [Fact]
    public void ReadArray_Spells2014_StrictOptions_ReadsEverySpell()
    {
        var spells = SrdJson.ReadArray<Spell2014>(
            SrdTestContent.FilePath(SrdEdition.Edition2014, SrdFileNames.Spells), SrdTestContent.StrictOptions);

        Assert.Equal(319, spells.Count);
    }

    [Fact]
    public void ReadArray_Spells2024_StrictOptions_ReadsEverySpell()
    {
        var spells = SrdJson.ReadArray<Spell2024>(
            SrdTestContent.FilePath(SrdEdition.Edition2024, SrdFileNames.Spells), SrdTestContent.StrictOptions);

        Assert.Equal(339, spells.Count);
    }

    [Fact]
    public void Serialize_EverySpell2014_RoundTripsToVendoredJson()
    {
        JsonDiff.AssertRecordsRoundTrip(SrdTestContent.Raw(SrdEdition.Edition2014, SrdFileNames.Spells), SrdTestContent.Spells2014, s => s.Index);
    }

    [Fact]
    public void Serialize_EverySpell2024_RoundTripsToVendoredJson()
    {
        JsonDiff.AssertRecordsRoundTrip(SrdTestContent.Raw(SrdEdition.Edition2024, SrdFileNames.Spells), SrdTestContent.Spells2024, s => s.Index);
    }

    [Fact]
    public void Fireball2014_ReadsSlotTableSaveAndArea()
    {
        var fireball = Spell2014("fireball");

        Assert.Equal(3, fireball.Level);
        Assert.Equal("1 action", fireball.CastingTime);
        Assert.Equal(["V", "S", "M"], fireball.Components);
        var damage = Assert.Single(fireball.Damage!);
        Assert.Equal("fire", damage.DamageType!.Index);
        Assert.Equal(Enumerable.Range(3, 7), damage.DamageAtSlotLevel!.Keys.Order());
        Assert.Equal("8d6", damage.DamageAtSlotLevel[3]);
        Assert.Equal("14d6", damage.DamageAtSlotLevel[9]);
        Assert.Equal("dex", fireball.Dc!.DcType.Index);
        Assert.Equal(SaveSuccessTypes.Half, fireball.Dc.DcSuccess);
        Assert.Equal("sphere", fireball.AreaOfEffect!.Type);
        Assert.Equal(20, fireball.AreaOfEffect.Size);
        Assert.Single(fireball.HigherLevel!);
        Assert.Contains(fireball.Subclasses, s => s.Index == "lore");
    }

    [Theory]
    [InlineData(1, "1d10")]
    [InlineData(5, "2d10")]
    [InlineData(11, "3d10")]
    [InlineData(17, "4d10")]
    public void FireBolt2014_CantripScaling_IsKeyedByCharacterLevel(int characterLevel, string dice)
    {
        var damage = Assert.Single(Spell2014("fire-bolt").Damage!);

        Assert.Null(damage.DamageAtSlotLevel);
        Assert.Equal(dice, damage.DamageAtCharacterLevel![characterLevel]);
    }

    [Fact]
    public void CureWounds2014_HealingTable_KeepsTheModifierToken()
    {
        var cureWounds = Spell2014("cure-wounds");

        Assert.Null(cureWounds.Damage);
        Assert.Equal("1d8 + MOD", cureWounds.HealAtSlotLevel![1]);
        Assert.Equal("9d8 + MOD", cureWounds.HealAtSlotLevel[9]);
    }

    [Fact]
    public void FlameStrike2014_HasTwoDamageComponents_FireAndRadiant()
    {
        Assert.Equal(["fire", "radiant"], Spell2014("flame-strike").Damage!.Select(d => d.DamageType!.Index));
    }

    [Fact]
    public void Fireball2024_KeepsOnlyTheBaseSlotAndProseScaling()
    {
        var fireball = Spell2024("fireball");

        Assert.Equal(3, fireball.Level);
        Assert.Equal("Action", fireball.CastingTime);
        Assert.Equal("fire", fireball.Damage!.DamageType.Index);
        var slot = Assert.Single(fireball.Damage.DamageAtSlotLevel);
        Assert.Equal(3, slot.Key);
        Assert.Equal("8d6", slot.Value);
        Assert.Equal("The damage increases by 1d6 for each spell slot level above 3.", fireball.HigherLevel);
        Assert.StartsWith("A bright streak flashes", fireball.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void FireBolt2024_CantripScaling_LivesOnlyInDescription()
    {
        var fireBolt = Spell2024("fire-bolt");

        Assert.Equal("1d10", fireBolt.Damage!.DamageAtSlotLevel[0]);
        Assert.Null(fireBolt.HigherLevel);
        Assert.Contains("Cantrip Upgrade. The damage increases by 1d10 when you reach levels 5 (2d10), 11 (3d10), and 17 (4d10).",
            fireBolt.Description, StringComparison.Ordinal);
    }

    // 2024 lost the damage data these spells had in 2014; the Phase 5 overlay has to restore it.
    [Theory]
    [InlineData("magic-missile")]
    [InlineData("cure-wounds")]
    public void Spell2024_NoStructuredDice_WhereTheRulesHaveDice(string index)
    {
        Assert.Null(Spell2024(index).Damage);
    }

    [Fact]
    public void FlameStrike2024_SingleDamageObject_LosesTheRadiantHalf()
    {
        var damage = Spell2024("flame-strike").Damage!;

        Assert.Equal("fire", damage.DamageType.Index);
        Assert.Equal("5d6", Assert.Single(damage.DamageAtSlotLevel).Value);
    }

    private static Spell2014 Spell2014(string index) => SrdTestContent.Spells2014.Single(s => s.Index == index);

    private static Spell2024 Spell2024(string index) => SrdTestContent.Spells2024.Single(s => s.Index == index);
}
