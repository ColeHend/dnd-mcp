using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Probability;
using Xunit;

namespace DndMcp.Tests.Probability;

/// <summary>
/// Invariant: <see cref="AttackRoll.Odds"/> reproduces the Phase 4 contract's d20 goldens (§2, re-verified with exact
/// arithmetic; the exact fraction is in each row's comment) to 1e-12, never lets the hit chance fall below the crit
/// chance, lets a natural 1 miss and a natural 20 hit whatever the numbers, and refuses a crit range it cannot mean.
/// </summary>
public sealed class AttackRollTests
{
    private const double Tolerance = 1e-12;

    // +7 vs AC 15, crit 20: single-die p = 13/20.
    [Theory]
    [InlineData(D20Mode.Normal, false, false, 0.65)] // 13/20
    [InlineData(D20Mode.Advantage, false, false, 0.8775)] // 351/400
    [InlineData(D20Mode.Disadvantage, false, false, 0.4225)] // 169/400
    [InlineData(D20Mode.Advantage, false, true, 0.957125)] // 7657/8000
    [InlineData(D20Mode.Normal, true, false, 0.6825)] // 273/400
    [InlineData(D20Mode.Advantage, true, false, 0.898625)] // 7189/8000
    [InlineData(D20Mode.Disadvantage, true, false, 0.46475)] // 1859/4000 = 1.1·p²; dndMath.ts's 1.0975·p² is its bug 2
    public void Odds_Plus7VsAc15_IsTheGoldenHitChance(D20Mode mode, bool lucky, bool elvenAccuracy, double expected)
    {
        var odds = AttackRoll.Odds(7, 15, 20, new D20Options(mode, lucky, elvenAccuracy));

        Assert.Equal(expected, odds.Hit, Tolerance);
    }

    [Theory]
    [InlineData(D20Mode.Normal, false, 0.05)] // 1/20
    [InlineData(D20Mode.Advantage, false, 0.0975)] // 39/400
    [InlineData(D20Mode.Disadvantage, false, 0.0025)] // 1/400
    [InlineData(D20Mode.Advantage, true, 0.142625)] // 1141/8000
    public void Odds_Plus7VsAc15_IsTheGoldenCritChance(D20Mode mode, bool elvenAccuracy, double expected)
    {
        var odds = AttackRoll.Odds(7, 15, 20, new D20Options(mode, ElvenAccuracy: elvenAccuracy));

        Assert.Equal(expected, odds.Crit, Tolerance);
    }

    [Theory]
    [InlineData(D20Mode.Normal, 0.775)] // 31/40
    [InlineData(D20Mode.Advantage, 0.94625)] // 757/800
    [InlineData(D20Mode.Disadvantage, 0.60375)] // 483/800
    public void Odds_BlessPlus7VsAc15_IsTheGoldenHitChance(D20Mode mode, double expected)
    {
        var odds = AttackRoll.Odds(7, 15, 20, new D20Options(mode), BonusDice.Parse("1d4", "test dice"));

        Assert.Equal(expected, odds.Hit, Tolerance);
    }

    [Theory]
    [InlineData(D20Mode.Normal)]
    [InlineData(D20Mode.Advantage)]
    [InlineData(D20Mode.Disadvantage)]
    public void Odds_BlessOrBane_LeavesTheCritChanceExactlyAlone(D20Mode mode)
    {
        var plain = AttackRoll.Odds(7, 15, 19, new D20Options(mode));

        var bless = AttackRoll.Odds(7, 15, 19, new D20Options(mode), BonusDice.Parse("1d4", "test dice"));
        var bane = AttackRoll.Odds(7, 15, 19, new D20Options(mode), BonusDice.Parse("-1d4", "test dice"));

        Assert.Equal(plain.Crit, bless.Crit);
        Assert.Equal(plain.Crit, bane.Crit);
        Assert.True(bless.Hit > plain.Hit && plain.Hit > bane.Hit);
    }

    [Fact]
    public void Odds_Plus5VsAc30Crit19_HitIsFlooredAtTheCritChance()
    {
        // The crit-floor regression (dndMath.ts bug 1): only a natural 19 or 20 hits, and both crit.
        var odds = AttackRoll.Odds(5, 30, 19, D20Options.Normal);

        Assert.Equal(0.10, odds.Hit, Tolerance); // 1/10
        Assert.Equal(0.10, odds.Crit, Tolerance); // 1/10
        Assert.Equal(0.0, odds.NormalHit);

        // 1d8+3: normal hit 7.5, crit 2d8+3 = 12. The engine's golden 1.2; dndMath.ts gives 0.825.
        Assert.Equal(1.2, (odds.NormalHit * 7.5) + (odds.Crit * 12), Tolerance);
    }

    [Theory]
    [InlineData(D20Mode.Normal)]
    [InlineData(D20Mode.Advantage)]
    [InlineData(D20Mode.Disadvantage)]
    public void Odds_CritFloorWithBonusDice_NormalHitIsNeverNegative(D20Mode mode)
    {
        // Every Bless outcome still leaves only the crit faces hitting; summing four rounded terms must not dip below.
        var odds = AttackRoll.Odds(5, 30, 19, new D20Options(mode, Lucky: true), BonusDice.Parse("1d4-1d4", "test dice"));

        Assert.Equal(odds.Crit, odds.Hit);
        Assert.Equal(0.0, odds.NormalHit);
    }

    [Theory]
    [InlineData(D20Mode.Normal, 0.95)] // 19/20
    [InlineData(D20Mode.Advantage, 0.9975)] // 399/400
    [InlineData(D20Mode.Disadvantage, 0.9025)] // 361/400
    public void Odds_HugeBonus_NaturalOneStillMisses(D20Mode mode, double expected)
    {
        Assert.Equal(expected, AttackRoll.Odds(30, 5, 20, new D20Options(mode)).Hit, Tolerance);
        Assert.Equal(expected, AttackRoll.Odds(30, 5, 20, new D20Options(mode), BonusDice.Parse("1d4", "test dice")).Hit, Tolerance);
    }

    [Theory]
    [InlineData(D20Mode.Normal, 0.05)] // 1/20
    [InlineData(D20Mode.Advantage, 0.0975)] // 39/400
    [InlineData(D20Mode.Disadvantage, 0.0025)] // 1/400
    public void Odds_ImpossibleAc_NaturalTwentyStillHitsAndCrits(D20Mode mode, double expected)
    {
        var odds = AttackRoll.Odds(-10, 40, 20, new D20Options(mode), BonusDice.Parse("-1d4", "test dice"));

        Assert.Equal(expected, odds.Hit, Tolerance);
        Assert.Equal(expected, odds.Crit, Tolerance);
    }

    [Fact]
    public void Odds_CritRangeTwo_EveryFaceButOneHitsAndCrits()
    {
        var odds = AttackRoll.Odds(0, 40, 2, D20Options.Normal);

        Assert.Equal(0.95, odds.Hit, Tolerance);
        Assert.Equal(0.95, odds.Crit, Tolerance);
    }

    [Theory]
    [InlineData(D20Mode.Normal, false)]
    [InlineData(D20Mode.Advantage, false)]
    [InlineData(D20Mode.Advantage, true)]
    public void Odds_AutoCrit_EveryHitIsACritAndTheHitChanceIsUnchanged(D20Mode mode, bool bless)
    {
        var dice = bless ? BonusDice.Parse("1d4", "test dice") : null;
        var plain = AttackRoll.Odds(7, 15, 20, new D20Options(mode), dice);

        var odds = AttackRoll.Odds(7, 15, 20, new D20Options(mode), dice, autoCrit: true);

        Assert.Equal(plain.Hit, odds.Hit);
        Assert.Equal(odds.Hit, odds.Crit);
        Assert.Equal(0.0, odds.NormalHit);
    }

    [Fact]
    public void Odds_AdvantageAutoCrit_IsTheGoldenAdvantageHitChance()
    {
        // Paralyzed target within 5 ft: Advantage from the condition, every hit a crit.
        var odds = AttackRoll.Odds(7, 15, 20, D20Options.Advantage, autoCrit: true);

        Assert.Equal(0.8775, odds.Crit, Tolerance); // 351/400
    }

    [Theory]
    [MemberData(nameof(D20FacePmfTests.AllOptions), MemberType = typeof(D20FacePmfTests))]
    public void Odds_Outcomes_AreProbabilitiesThatSumToOne(D20Mode mode, bool lucky, bool elvenAccuracy)
    {
        var options = new D20Options(mode, lucky, elvenAccuracy);
        foreach (var dice in new[] { null, BonusDice.Parse("1d4", "test dice"), BonusDice.Parse("-1d4", "test dice") })
        {
            for (var ac = 5; ac <= 30; ac += 5)
            {
                var odds = AttackRoll.Odds(6, ac, 19, options, dice);

                Assert.InRange(odds.NormalHit, 0.0, 1.0);
                Assert.InRange(odds.Crit, 0.0, 1.0);
                Assert.InRange(odds.Miss, 0.0, 1.0);
                Assert.Equal(1.0, odds.NormalHit + odds.Crit + odds.Miss, 1e-15);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-3)]
    public void Odds_PointBonus_IsTheSameAsAddingItToTheAttackBonus(int bonus)
    {
        var shifted = AttackRoll.Odds(7 + bonus, 15, 20, D20Options.Advantage);

        var point = AttackRoll.Odds(7, 15, 20, D20Options.Advantage, Pmf<double>.Point(bonus));

        Assert.Equal(shifted, point);
    }

    [Theory]
    [InlineData(int.MinValue, int.MaxValue, 0.05)]
    [InlineData(int.MaxValue, int.MinValue, 0.95)]
    public void Odds_ExtremeIntegers_DoNotOverflow(int attackBonus, int targetAc, double expected)
    {
        Assert.Equal(expected, AttackRoll.Odds(attackBonus, targetAc, 20, D20Options.Normal).Hit, Tolerance);
        Assert.Equal(expected, AttackRoll.Odds(attackBonus, targetAc, 20, D20Options.Normal, BonusDice.Parse("-1d4", "test dice")).Hit, Tolerance);
    }

    [Theory]
    [InlineData(7, 15, 20, 8)]
    [InlineData(30, 5, 20, 2)]
    [InlineData(5, 30, 19, 19)]
    [InlineData(0, 21, 20, 20)]
    [InlineData(-10, 40, 20, 20)]
    [InlineData(-10, 40, 18, 18)]
    [InlineData(int.MinValue, int.MaxValue, 20, 20)]
    [InlineData(int.MaxValue, int.MinValue, 20, 2)]
    public void LowestHittingFace_Examples_AreTheFaceThatMeetsTheAcWithinTheNaturalRules(int attackBonus, int targetAc, int critMin, int expected)
    {
        Assert.Equal(expected, AttackRoll.LowestHittingFace(attackBonus, targetAc, critMin));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Odds_CritRangeOutside2To20_IsRefusedWithTheAcceptedRange(int critMin)
    {
        var ex = Assert.Throws<DndInputException>(() => AttackRoll.Odds(7, 15, critMin, D20Options.Normal));
        var face = Assert.Throws<DndInputException>(() => AttackRoll.LowestHittingFace(7, 15, critMin));

        Assert.Contains("2 to 20", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"got {critMin}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("19 for Improved Critical", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, face.Message);
    }

    [Fact]
    public void Odds_NullOptions_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => AttackRoll.Odds(7, 15, 20, null!));
    }

    [Fact]
    public void Odds_UndefinedMode_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AttackRoll.Odds(7, 15, 20, new D20Options((D20Mode)(-1))));
    }

    public static TheoryData<string, Pmf<double>> MalformedDistributions => new()
    {
        { "weights sum to half the total", Pmf<double>.FromPairs([KeyValuePair.Create(1L, 0.25), KeyValuePair.Create(2L, 0.25)], 1.0) },
        { "a NaN weight", Pmf<double>.FromPairs([KeyValuePair.Create(1L, double.NaN), KeyValuePair.Create(2L, 1.0)], 1.0) },
        { "a negative weight", Pmf<double>.FromPairs([KeyValuePair.Create(1L, -0.5), KeyValuePair.Create(2L, 1.5)], 1.0) },
        { "an infinite total", Pmf<double>.FromPairs([KeyValuePair.Create(1L, 1.0)], double.PositiveInfinity) },
        { "a zero total", Pmf<double>.FromPairs([KeyValuePair.Create(1L, 0.0)], 0.0) },
        { "a value beyond the dice limit", Pmf<double>.FromPairs([KeyValuePair.Create(long.MaxValue, 1.0)], 1.0) },
        { "a value below the dice limit", Pmf<double>.FromPairs([KeyValuePair.Create(-BonusDice.MaxMagnitude - 1, 1.0)], 1.0) },
    };

    [Theory]
    [MemberData(nameof(MalformedDistributions))]
    public void Odds_BonusDiceThatAreNotADistribution_IsRefusedAsABug(string what, Pmf<double> dice)
    {
        var ex = Assert.Throws<ArgumentException>(() => AttackRoll.Odds(7, 15, 20, D20Options.Normal, dice));

        Assert.Equal("bonusDice", ex.ParamName);
        Assert.False(string.IsNullOrEmpty(what));
    }

    [Fact]
    public void Odds_UnnormalisedButConsistentDistribution_IsAccepted()
    {
        // Weights over a total other than 1 are a valid Pmf: P = weight / Total.
        var dice = Pmf<double>.FromPairs(Enumerable.Range(1, 4).Select(b => KeyValuePair.Create((long)b, 1.0)), 4.0);

        Assert.Equal(0.775, AttackRoll.Odds(7, 15, 20, D20Options.Normal, dice).Hit, Tolerance);
    }
}
