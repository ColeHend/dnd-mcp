using DndMcp.Domain.Characters;
using Xunit;
using static DndMcp.Tests.Characters.CharacterSheets;

namespace DndMcp.Tests.Characters;

/// <summary>
/// Invariant: the derived readings later layers act on are exact. Initiative is the stored bonus, else the Dex modifier,
/// else 0 (the tracker seeds every sheet's initiative from it). Dead is three failed death saves, exhaustion 6 or an
/// effective maximum of 0; dying is 0 HP, not stable and not dead (damage, healing and rests branch on these).
/// </summary>
public sealed class CharacterSheetTests
{
    [Theory]
    [InlineData(7, 14, 7)]
    [InlineData(-1, 18, -1)]
    [InlineData(null, 14, 2)]
    [InlineData(null, 7, -2)]
    [InlineData(null, null, 0)]
    public void InitiativeOrDex_StoredBonusThenDexThenZero(int? bonus, int? dex, int expected)
    {
        var sheet = CharacterSheet.New("e1") with
        {
            InitiativeBonus = bonus,
            Abilities = dex is { } score ? SheetMaps.Of(new[] { new KeyValuePair<string, int>("dex", score) }) : SheetMaps.Empty<int>(),
        };

        Assert.Equal(expected, sheet.InitiativeOrDex);
    }

    [Theory]
    [InlineData(0, 0, 0, false, 0, 0, E2014, false, true)] // 0 HP, not stable: dying
    [InlineData(0, 1, 2, false, 0, 0, E2024, false, true)]
    [InlineData(0, 0, 0, true, 0, 0, E2014, false, false)] // stable
    [InlineData(0, 0, 3, false, 0, 0, E2014, true, false)] // three failures: dead, not dying
    [InlineData(40, 0, 0, false, 6, 0, E2024, true, false)] // exhaustion 6
    [InlineData(40, 0, 0, false, 5, 0, E2024, false, false)]
    [InlineData(0, 0, 0, false, 0, 110, E2014, true, false)] // the reduction takes the whole maximum
    [InlineData(1, 0, 0, false, 0, 109, E2014, false, false)]
    [InlineData(20, 0, 0, false, 0, 0, E2014, false, false)]
    public void IsDeadAndIsDying_FollowHpTalliesStableExhaustionAndTheEffectiveMaximum(
        int hp, int successes, int failures, bool stable, int exhaustion, int reduction, string edition, bool dead, bool dying)
    {
        var sheet = Belmakor() with
        {
            Hp = hp,
            DeathSaves = new SheetDeathSaves(successes, failures, stable),
            Exhaustion = exhaustion,
            MaxHpReduction = reduction,
            Ruleset = edition,
        };

        Assert.Equal(dead, sheet.IsDead(edition));
        Assert.Equal(dying, sheet.IsDying(edition));
    }

    [Fact]
    public void IsDying_NoHitPointsTracked_IsNotDying()
    {
        Assert.False(Minimal("""[{ "class": "bard", "level": 12 }]""").IsDying(E2014));
    }
}
