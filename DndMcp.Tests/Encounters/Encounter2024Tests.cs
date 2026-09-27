using DndMcp.Domain.Encounters;
using Xunit;

namespace DndMcp.Tests.Encounters;

/// <summary>
/// Invariant: the 2024 method sums per-character budgets, applies no multiplier, and names a fight by the lowest budget
/// its XP fits — which is how the SRD's own worked examples name theirs. Its troubleshooting advice fires exactly at
/// the SRD's stated limits.
/// </summary>
public sealed class Encounter2024Tests
{
    private static EncounterMonster Monster(string name, string cr, int count = 1) =>
        new(name, name, ChallengeRating.Parse(cr), ChallengeRatingTables.Xp(ChallengeRating.Parse(cr)), count);

    public static TheoryData<int[], EncounterMonster[], long, string> WorkedExamples => new()
    {
        // SRD 5.2.1 Gameplay Toolbox › Step 3, Example 1: four level 1 characters, Low, budget 200.
        { [1, 1, 1, 1], [Monster("Bugbear Warrior", "1")], 200, EncounterDifficulty.Low },
        { [1, 1, 1, 1], [Monster("Giant Wasp", "1/2", 2)], 200, EncounterDifficulty.Low },
        { [1, 1, 1, 1], [Monster("Giant Rat", "1/8", 6)], 150, EncounterDifficulty.Low },

        // Example 2: five level 3 characters, Moderate, budget 1,125 (Low is 750).
        { [3, 3, 3, 3, 3], [Monster("Druid", "2", 2), Monster("Stirge", "1/8", 9)], 1_125, EncounterDifficulty.Moderate },
        { [3, 3, 3, 3, 3], [Monster("Wight", "3"), Monster("Warhorse Skeleton", "1/2"), Monster("Skeleton", "1/4", 6)], 1_100, EncounterDifficulty.Moderate },

        // Example 3: six level 15 characters, High, budget 46,800 (Moderate is 32,400).
        { [15, 15, 15, 15, 15, 15], [Monster("Adult Red Dragon", "17", 2), Monster("Fire Giant", "9", 2)], 46_000, EncounterDifficulty.High },
    };

    [Theory]
    [MemberData(nameof(WorkedExamples))]
    public void Assess_SrdWorkedExample_HasTheSrdsXpAndDifficulty(int[] party, EncounterMonster[] monsters, long xp, string difficulty)
    {
        var result = Encounter2024.Assess(party, monsters);

        Assert.Equal(xp, result.MonsterXp);
        Assert.Equal(difficulty, result.Difficulty);
    }

    [Fact]
    public void Assess_WorkedExampleBudgets_AreTheSrds()
    {
        Assert.Equal(200, Encounter2024.Assess([1, 1, 1, 1], [Monster("x", "1")]).Budget.Low);
        Assert.Equal(1_125, Encounter2024.Assess([3, 3, 3, 3, 3], [Monster("x", "1")]).Budget.Moderate);
        Assert.Equal(46_800, Encounter2024.Assess([15, 15, 15, 15, 15, 15], [Monster("x", "1")]).Budget.High);
    }

    [Theory]
    [InlineData(0, EncounterDifficulty.Low)]
    [InlineData(200, EncounterDifficulty.Low)]
    [InlineData(201, EncounterDifficulty.Moderate)]
    [InlineData(300, EncounterDifficulty.Moderate)]
    [InlineData(301, EncounterDifficulty.High)]
    [InlineData(400, EncounterDifficulty.High)]
    [InlineData(401, EncounterDifficulty.BeyondHigh)]
    public void Classify_Boundaries_TheLowestBudgetTheXpFits(long xp, string expected)
    {
        Assert.Equal(expected, Encounter2024.Classify(xp, new XpBudget2024(200, 300, 400)));
    }

    [Fact]
    public void Assess_MixedLevels_SumEachCharactersBudget()
    {
        var result = Encounter2024.Assess([5, 4, 3], [Monster("x", "1")]);

        Assert.Equal(new XpBudget2024(500 + 250 + 150, 750 + 375 + 225, 1_100 + 500 + 400), result.Budget);
    }

    [Fact]
    public void Assess_ManyMonsters_NoMultiplier()
    {
        Assert.Equal(15 * 25, Encounter2024.Assess([5, 5], [Monster("Kobold", "1/8", 15)]).MonsterXp);
    }

    [Fact]
    public void Troubleshoot_MoreThanTwoCreaturesPerCharacter_Warns()
    {
        Assert.DoesNotContain(Encounter2024.Troubleshoot([3, 3], [Monster("Goblin", "1/4", 4)]), w => w.Code == Encounter2024.Warnings.ManyCreatures);

        var warning = Assert.Single(Encounter2024.Troubleshoot([3, 3], [Monster("Goblin", "1/4", 5)]), w => w.Code == Encounter2024.Warnings.ManyCreatures);
        Assert.Contains("5 creatures against 2 characters", warning.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("level 1 or 2", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Troubleshoot_ManyCreaturesAtLevelTwo_SaysItMattersMost()
    {
        var warning = Assert.Single(Encounter2024.Troubleshoot([2, 3], [Monster("Rat", "1/8", 5)]), w => w.Code == Encounter2024.Warnings.ManyCreatures);

        Assert.Contains("level 1 or 2", warning.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 1, 1, 1, 1 }, "2", "the party's level (1)")]
    [InlineData(new[] { 1, 2 }, "3", "every character's level")]
    [InlineData(new[] { 1, 3, 3 }, "2", "the level of 1 of the 3 characters (lowest 1)")]
    public void Troubleshoot_CrAboveACharactersLevel_Warns(int[] party, string cr, string whom)
    {
        var warning = Assert.Single(Encounter2024.Troubleshoot(party, [Monster("Ogre", cr)]), w => w.Code == Encounter2024.Warnings.PowerfulCreature);

        Assert.Contains($"Ogre is CR {cr}, above {whom}", warning.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 1 }, "1")]
    [InlineData(new[] { 1 }, "1/2")]
    [InlineData(new[] { 5, 5 }, "5")]
    public void Troubleshoot_CrAtOrBelowEveryLevel_DoesNotWarn(int[] party, string cr)
    {
        Assert.DoesNotContain(Encounter2024.Troubleshoot(party, [Monster("M", cr)]), w => w.Code == Encounter2024.Warnings.PowerfulCreature);
    }

    [Fact]
    public void Troubleshoot_SameStatBlockOnTwoLines_WarnsOnceAndCountsOnce()
    {
        var ogres = new EncounterMonster("Ogre", "2024/monster/ogre", ChallengeRating.Parse("2"), 450, 1);
        var warnings = Encounter2024.Troubleshoot([1, 1, 1, 1], [ogres, ogres, Monster("A", "1/8"), Monster("B", "1/8")]);

        Assert.Single(warnings, w => w.Code == Encounter2024.Warnings.PowerfulCreature);
        Assert.DoesNotContain(warnings, w => w.Code == Encounter2024.Warnings.ManyStatBlocks);
    }

    [Fact]
    public void Troubleshoot_OneStatBlockAtTwoCrs_JudgesTheHigher()
    {
        var low = new EncounterMonster("Bandit", "cr-bandit", ChallengeRating.Parse("1"), 200, 1);
        var high = low with { ChallengeRating = ChallengeRating.Parse("8"), Xp = 3_900 };

        var warning = Assert.Single(Encounter2024.Troubleshoot([5, 5], [low, high]), w => w.Code == Encounter2024.Warnings.PowerfulCreature);
        Assert.Contains("Bandit is CR 8", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Troubleshoot_MoreThanThreeStatBlocks_Warns()
    {
        EncounterMonster[] three = [Monster("A", "1"), Monster("B", "1"), Monster("C", "1")];

        Assert.DoesNotContain(Encounter2024.Troubleshoot([5, 5], three), w => w.Code == Encounter2024.Warnings.ManyStatBlocks);
        var warning = Assert.Single(Encounter2024.Troubleshoot([5, 5], [.. three, Monster("D", "1")]), w => w.Code == Encounter2024.Warnings.ManyStatBlocks);
        Assert.StartsWith("4 different stat blocks", warning.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Troubleshoot_CrZero_WarnsForManyOrForZeroXp()
    {
        var cat = new EncounterMonster("Cat", "cat", ChallengeRating.Zero, 10, 2);
        var frog = new EncounterMonster("Frog", "frog", ChallengeRating.Zero, 0, 1);

        Assert.DoesNotContain(Encounter2024.Troubleshoot([5, 5], [cat]), w => w.Code == Encounter2024.Warnings.CrZero);
        Assert.Single(Encounter2024.Troubleshoot([5, 5], [cat with { Count = 3 }]), w => w.Code == Encounter2024.Warnings.CrZero);

        var zeroXp = Assert.Single(Encounter2024.Troubleshoot([5, 5], [frog]), w => w.Code == Encounter2024.Warnings.CrZero);
        Assert.StartsWith("1 creature of CR 0 worth 0 XP, which the budget cannot see:", zeroXp.Text, StringComparison.Ordinal);

        var mixed = Assert.Single(Encounter2024.Troubleshoot([5, 5], [frog, cat]), w => w.Code == Encounter2024.Warnings.CrZero);
        Assert.StartsWith("3 creatures of CR 0, 1 of them worth 0 XP, which the budget cannot see:", mixed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Troubleshoot_OrdinaryEncounter_HasNoWarnings()
    {
        Assert.Empty(Encounter2024.Troubleshoot([5, 5, 5, 5], [Monster("Ogre", "2", 3)]));
    }
}
