using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Domain.Probability;
using Xunit;

namespace DndMcp.Tests.Probability;

/// <summary>
/// Invariant: <see cref="SavingThrow.FailChance"/> is P(d20 + bonus (+ dice) &lt; DC) with no natural 1 or 20 rule,
/// Advantage and Disadvantage and bonus dice combined per face (never by squaring a finished fail chance), matching the
/// contract's goldens and an independent enumeration; <see cref="SavingThrow.ExpectedCastsToLand"/> is (L + 1) / F.
/// </summary>
public sealed class SavingThrowTests
{
    private const double Tolerance = 1e-12;

    [Theory]
    [InlineData(15, 2, D20Mode.Normal, 0.60)] // 3/5: fails on 1-12
    [InlineData(15, 2, D20Mode.Advantage, 0.36)] // 9/25 = 0.6²: Magic Resistance
    [InlineData(15, 2, D20Mode.Disadvantage, 0.84)] // 21/25 = 1 − 0.4²
    [InlineData(15, 20, D20Mode.Normal, 0.0)] // a natural 1 still totals 21
    [InlineData(15, -20, D20Mode.Normal, 1.0)] // a natural 20 still totals 0
    [InlineData(10, 9, D20Mode.Normal, 0.0)] // no natural-1 failure on saves
    [InlineData(30, 9, D20Mode.Normal, 1.0)] // no natural-20 success on saves
    [InlineData(29, 9, D20Mode.Normal, 0.95)] // 19/20: only a 20 saves
    public void FailChance_Examples_AreTheGoldenValues(int dc, int saveBonus, D20Mode mode, double expected)
    {
        Assert.Equal(expected, SavingThrow.FailChance(dc, saveBonus, mode), Tolerance);
    }

    [Theory]
    [InlineData("-1d4", D20Mode.Normal, 0.725)] // 29/40: fails on 1..12+b, b = 1..4
    [InlineData("-1d4", D20Mode.Advantage, 0.52875)] // 423/800 = avg of ((12+b)/20)²; squaring 0.725 would give 0.525625
    [InlineData("-1d4", D20Mode.Disadvantage, 0.92125)] // 737/800 = avg of 1 − ((8−b)/20)²
    [InlineData("1d4", D20Mode.Normal, 0.475)] // 19/40: fails on 1..12−b
    public void FailChance_BonusDiceOnDc15AtPlus2_AreCombinedPerFace(string dice, D20Mode mode, double expected)
    {
        Assert.Equal(expected, SavingThrow.FailChance(15, 2, mode, BonusDice.Parse(dice, "test dice")), Tolerance);
    }

    [Theory]
    [InlineData(D20Mode.Normal)]
    [InlineData(D20Mode.Advantage)]
    [InlineData(D20Mode.Disadvantage)]
    public void FailChance_AutoFail_IsCertainWhateverTheBonus(D20Mode mode)
    {
        Assert.Equal(1.0, SavingThrow.FailChance(15, 20, mode, autoFail: true));
        Assert.Equal(1.0, SavingThrow.FailChance(1, 30, mode, BonusDice.Parse("1d4", "test dice"), autoFail: true));
    }

    [Fact]
    public void FailChance_AutoFailWithAnUndefinedMode_IsStillRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SavingThrow.FailChance(15, 2, (D20Mode)3, autoFail: true));
    }

    [Fact]
    public void FailChance_AutoFailWithAMalformedDistribution_IsStillRefused()
    {
        var half = Pmf<double>.FromPairs([KeyValuePair.Create(1L, 0.5)], 1.0);

        var ex = Assert.Throws<ArgumentException>(() => SavingThrow.FailChance(15, 2, D20Mode.Normal, half, autoFail: true));

        Assert.Equal("bonusDice", ex.ParamName);
    }

    [Theory]
    [InlineData(int.MaxValue, int.MinValue, 1.0)]
    [InlineData(int.MinValue, int.MaxValue, 0.0)]
    public void FailChance_ExtremeIntegers_DoNotOverflow(int dc, int saveBonus, double expected)
    {
        Assert.Equal(expected, SavingThrow.FailChance(dc, saveBonus));
        Assert.Equal(expected, SavingThrow.FailChance(dc, saveBonus, D20Mode.Advantage, BonusDice.Parse("1d4-1d4", "test dice")), 1e-15);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(0)]
    [InlineData(3)]
    public void FailChance_PointBonus_IsTheSameAsAddingItToTheSaveBonus(int bonus)
    {
        Assert.Equal(SavingThrow.FailChance(15, 2 + bonus, D20Mode.Disadvantage), SavingThrow.FailChance(15, 2, D20Mode.Disadvantage, Pmf<double>.Point(bonus)));
    }

    public static TheoryData<D20Mode, string> BruteForceCases()
    {
        var data = new TheoryData<D20Mode, string>();
        foreach (var mode in Enum.GetValues<D20Mode>())
        {
            foreach (var dice in new[] { "none", "1d4", "-1d4", "1d4-1d4" })
            {
                data.Add(mode, dice);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BruteForceCases))]
    public void FailChance_EveryDcAndBonus_MatchesRawDiceEnumeration(D20Mode mode, string dice)
    {
        var kept = RawD20Enumerator.Enumerate(mode, lucky: false, elvenAccuracy: false);
        int[] outcomes = dice switch
        {
            "none" => [0],
            "1d4" => [1, 2, 3, 4],
            "-1d4" => [-1, -2, -3, -4],
            _ => [.. from bless in Enumerable.Range(1, 4) from bane in Enumerable.Range(1, 4) select bless - bane],
        };
        var bonusDice = dice == "none" ? null : BonusDice.Parse(dice, "test dice");
        var denominator = kept.Total * outcomes.Length;
        var failures = new List<string>();

        for (var dc = 1; dc <= 35; dc++)
        {
            for (var saveBonus = -10; saveBonus <= 20; saveBonus++)
            {
                long fails = 0;
                for (var face = 1; face <= 20; face++)
                {
                    foreach (var bonus in outcomes)
                    {
                        fails += face + saveBonus + bonus < dc ? kept.Counts[face] : 0;
                    }
                }

                var expected = (double)fails / denominator;
                var actual = SavingThrow.FailChance(dc, saveBonus, mode, bonusDice);
                if (Math.Abs(actual - expected) > (bonusDice is null ? 0 : 1e-15))
                {
                    failures.Add(string.Create(CultureInfo.InvariantCulture, $"DC {dc} at {saveBonus:+0;-0}: {actual:R}, expected {fails}/{denominator}"));
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} cases differ; first: {string.Join(" | ", failures.Take(5))}");
    }

    [Theory]
    [InlineData(0.6, 3, 20.0 / 3)] // 6.6667: needs 4 failures at 0.6 each
    [InlineData(1.0, 0, 1.0)]
    [InlineData(1.0, 3, 4.0)]
    [InlineData(0.5, 0, 2.0)]
    [InlineData(0.25, 2, 12.0)]
    public void ExpectedCastsToLand_Examples_AreLPlusOneOverF(double failChance, int legendaryResistances, double expected)
    {
        Assert.Equal(expected, SavingThrow.ExpectedCastsToLand(failChance, legendaryResistances), Tolerance);
    }

    [Fact]
    public void ExpectedCastsToLand_FromFailChanceDc15AtPlus2WithThreeUses_IsSixAndTwoThirds()
    {
        var failChance = SavingThrow.FailChance(15, 2);

        Assert.Equal(20.0 / 3, SavingThrow.ExpectedCastsToLand(failChance, 3), Tolerance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ExpectedCastsToLand_SaveNeverFails_IsPositiveInfinity(int legendaryResistances)
    {
        Assert.Equal(double.PositiveInfinity, SavingThrow.ExpectedCastsToLand(0.0, legendaryResistances));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void ExpectedCastsToLand_FailChanceNotAProbability_IsRefused(double failChance)
    {
        var ex = Assert.Throws<DndInputException>(() => SavingThrow.ExpectedCastsToLand(failChance, 3));

        Assert.Contains("probability from 0 to 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("E.g. 0.6", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ExpectedCastsToLand_NegativeLegendaryResistance_IsRefused(int legendaryResistances)
    {
        var ex = Assert.Throws<DndInputException>(() => SavingThrow.ExpectedCastsToLand(0.6, legendaryResistances));

        Assert.Contains("0 or more", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"got {legendaryResistances}", ex.Message, StringComparison.Ordinal);
    }
}
