using DndMcp.Domain.Core;
using DndMcp.Domain.Encounters;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// Invariant: every bad party, monster list or offset is refused with a message that names the bad value and what is
/// accepted, and an effective-level offset stays within the tables' 1–20 and says how many characters it held there.
/// </summary>
public sealed class EncounterLimitsTests
{
    private static readonly EncounterMonster Ogre = new("Ogre", "ogre", ChallengeRating.Parse("2"), 450, 1);

    [Theory]
    [InlineData(new int[0], "at least one character's level")]
    [InlineData(new[] { 5, 0 }, "character 2's level is 0; character levels are 1 to 20")]
    [InlineData(new[] { 21 }, "character 1's level is 21")]
    public void ValidateParty_Bad_IsRefusedWithWhy(int[] levels, string why)
    {
        var ex = Assert.Throws<DndInputException>(() => EncounterLimits.ValidateParty(levels));

        Assert.Contains(why, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateParty_TooMany_IsRefused()
    {
        EncounterLimits.ValidateParty(Enumerable.Repeat(5, EncounterLimits.MaxCharacters).ToArray());

        var ex = Assert.Throws<DndInputException>(() => EncounterLimits.ValidateParty(Enumerable.Repeat(5, EncounterLimits.MaxCharacters + 1).ToArray()));
        Assert.Contains("at most 50", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_001)]
    public void ValidateMonsters_CountOutOfRange_IsRefused(int count)
    {
        var ex = Assert.Throws<DndInputException>(() => EncounterLimits.ValidateMonsters([Ogre with { Count = count }]));

        Assert.Equal($"monsters item 1: count is {count}; it is 1 to 1,000.", ex.Message);
    }

    [Fact]
    public void ValidateMonsters_EmptyOrTooMany_IsRefusedWithWhy()
    {
        var empty = Assert.Throws<DndInputException>(() => EncounterLimits.ValidateMonsters([]));
        Assert.Equal("monsters needs at least one item. Example: [{\"name\": \"Ogre\", \"count\": 3}].", empty.Message);

        EncounterLimits.ValidateMonsters(Enumerable.Repeat(Ogre, EncounterLimits.MaxMonsterEntries).ToArray());
        var tooMany = Assert.Throws<DndInputException>(() => EncounterLimits.ValidateMonsters(Enumerable.Repeat(Ogre, EncounterLimits.MaxMonsterEntries + 1).ToArray()));
        Assert.Equal("monsters has 51 items; at most 50 are accepted. Put identical creatures on one item with count.", tooMany.Message);
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    [InlineData(0)]
    public void ValidateOffset_AtOrInsideTheBounds_IsAccepted(int offset)
    {
        EncounterLimits.ValidateOffset(offset);
    }

    [Fact]
    public void ValidateMonsters_NegativeXp_Throws()
    {
        // XP comes from the tables and srd.db, never from the caller, so a negative one is a bug, not input.
        Assert.Throws<ArgumentOutOfRangeException>(() => EncounterLimits.ValidateMonsters([Ogre with { Xp = -1 }]));
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(11)]
    public void ValidateOffset_OutOfRange_IsRefused(int offset)
    {
        var ex = Assert.Throws<DndInputException>(() => EncounterLimits.ValidateOffset(offset));

        Assert.Contains("-10 to +10", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveParty_Offset_AddsToEachLevelAndHoldsToOneToTwenty()
    {
        var party = EffectiveParty.Of([1, 5, 19, 20], 2);

        Assert.Equal([3, 7, 20, 20], party.Levels);
        Assert.Equal(2, party.ClampedCount);
        Assert.Equal(2, party.Offset);
    }

    [Fact]
    public void EffectiveParty_NegativeOffset_HoldsAtOne()
    {
        var party = EffectiveParty.Of([1, 2, 5], -2);

        Assert.Equal([1, 1, 3], party.Levels);
        Assert.Equal(2, party.ClampedCount);
    }

    [Fact]
    public void EffectiveParty_ValidatesItsInputs()
    {
        Assert.Throws<DndInputException>(() => EffectiveParty.Of([0], 1));
        Assert.Throws<DndInputException>(() => EffectiveParty.Of([5], 11));
    }
}
