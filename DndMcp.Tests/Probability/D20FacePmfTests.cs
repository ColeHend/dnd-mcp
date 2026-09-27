using DndMcp.Domain.Probability;
using Xunit;

namespace DndMcp.Tests.Probability;

/// <summary>
/// Invariant: <see cref="D20.FacePmf"/> is the exact distribution of the kept natural face — twenty entries summing to 1,
/// equal to the textbook per-face formulas, the same cached read-only instance for equal options, and blind to Elven
/// Accuracy unless there is Advantage for it to act on.
/// </summary>
public sealed class D20FacePmfTests
{
    public static TheoryData<D20Mode, bool, bool> AllOptions()
    {
        var data = new TheoryData<D20Mode, bool, bool>();
        foreach (var mode in Enum.GetValues<D20Mode>())
        {
            foreach (var lucky in new[] { false, true })
            {
                foreach (var elvenAccuracy in new[] { false, true })
                {
                    data.Add(mode, lucky, elvenAccuracy);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllOptions))]
    public void FacePmf_AnyOptions_HasTwentyNonNegativeFacesSummingToOne(D20Mode mode, bool lucky, bool elvenAccuracy)
    {
        var pmf = D20.FacePmf(new D20Options(mode, lucky, elvenAccuracy));

        Assert.Equal(20, pmf.Count);
        Assert.All(pmf, p => Assert.InRange(p, 0.0, 1.0));
        Assert.Equal(1.0, pmf.Sum(), 1e-14);
    }

    [Theory]
    [MemberData(nameof(AllOptions))]
    public void FacePmf_AnyOptions_MatchesRawDiceEnumerationExactly(D20Mode mode, bool lucky, bool elvenAccuracy)
    {
        var kept = RawD20Enumerator.Enumerate(mode, lucky, elvenAccuracy);

        var pmf = D20.FacePmf(new D20Options(mode, lucky, elvenAccuracy));

        for (var face = 1; face <= 20; face++)
        {
            Assert.Equal((double)kept.Counts[face] / kept.Total, pmf[face - 1]);
        }
    }

    [Fact]
    public void FacePmf_Normal_IsOneTwentiethEach()
    {
        Assert.All(D20.FacePmf(D20Options.Normal), p => Assert.Equal(1.0 / 20, p));
    }

    [Fact]
    public void FacePmf_Advantage_IsTwoFMinusOneOver400()
    {
        var pmf = D20.FacePmf(D20Options.Advantage);

        // P(max of 2 = f) = (f² − (f−1)²)/400 = (2f − 1)/400.
        for (var f = 1; f <= 20; f++)
        {
            Assert.Equal((2.0 * f - 1) / 400, pmf[f - 1]);
        }
    }

    [Fact]
    public void FacePmf_Disadvantage_IsFortyOneMinusTwoFOver400()
    {
        var pmf = D20.FacePmf(D20Options.Disadvantage);

        for (var f = 1; f <= 20; f++)
        {
            Assert.Equal((41.0 - 2 * f) / 400, pmf[f - 1]);
        }
    }

    [Fact]
    public void FacePmf_ElvenAccuracy_IsHighestOfThree()
    {
        var pmf = D20.FacePmf(new D20Options(D20Mode.Advantage, ElvenAccuracy: true));

        for (var f = 1; f <= 20; f++)
        {
            Assert.Equal((double)((f * f * f) - ((f - 1) * (f - 1) * (f - 1))) / 8000, pmf[f - 1]);
        }
    }

    [Fact]
    public void FacePmf_Lucky_MovesTheOneOntoTheReroll()
    {
        var pmf = D20.FacePmf(new D20Options(D20Mode.Normal, Lucky: true));

        // A 1 stays only when the reroll is a 1 too: 1/400. Every other face: 1/20 + (1/20)(1/20) = 21/400.
        Assert.Equal(1.0 / 400, pmf[0]);
        for (var f = 2; f <= 20; f++)
        {
            Assert.Equal(21.0 / 400, pmf[f - 1]);
        }
    }

    [Fact]
    public void FacePmf_LuckyWithDisadvantage_RerollsOnlyOneOfADoubleOne()
    {
        var pmf = D20.FacePmf(new D20Options(D20Mode.Disadvantage, Lucky: true));

        // A double 1 (1/400) keeps a 1 whatever the one reroll shows; exactly one 1 (38/400) keeps a 1 only if the
        // reroll is a 1 (×1/20). 1/400 + 38/8000 = 29/4000. Rerolling both 1s would give 799/160000 instead.
        Assert.Equal(29.0 / 4000, pmf[0]);
    }

    [Theory]
    [InlineData(D20Mode.Normal, false)]
    [InlineData(D20Mode.Normal, true)]
    [InlineData(D20Mode.Disadvantage, false)]
    [InlineData(D20Mode.Disadvantage, true)]
    public void FacePmf_ElvenAccuracyWithoutAdvantage_IsIgnored(D20Mode mode, bool lucky)
    {
        var without = D20.FacePmf(new D20Options(mode, lucky));

        var with = D20.FacePmf(new D20Options(mode, lucky, ElvenAccuracy: true));

        Assert.Same(without, with);
    }

    [Fact]
    public void FacePmf_ElvenAccuracyWithAdvantage_ChangesTheDistribution()
    {
        Assert.NotEqual(D20.FacePmf(D20Options.Advantage), D20.FacePmf(new D20Options(D20Mode.Advantage, ElvenAccuracy: true)));
    }

    [Theory]
    [MemberData(nameof(AllOptions))]
    public void FacePmf_EqualOptions_ReturnTheSameCachedInstance(D20Mode mode, bool lucky, bool elvenAccuracy)
    {
        Assert.Same(D20.FacePmf(new D20Options(mode, lucky, elvenAccuracy)), D20.FacePmf(new D20Options(mode, lucky, elvenAccuracy)));
    }

    [Fact]
    public void FacePmf_CachedInstance_CannotBeChanged()
    {
        var pmf = (IList<double>)D20.FacePmf(D20Options.Advantage);

        Assert.Throws<NotSupportedException>(() => pmf[19] = 1.0);
    }

    [Fact]
    public void FacePmf_UndefinedMode_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => D20.FacePmf(new D20Options((D20Mode)7)));
    }

    [Fact]
    public void FacePmf_NullOptions_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => D20.FacePmf(null!));
    }

    [Theory]
    [InlineData(false, false, D20Mode.Normal)]
    [InlineData(true, false, D20Mode.Advantage)]
    [InlineData(false, true, D20Mode.Disadvantage)]
    [InlineData(true, true, D20Mode.Normal)]
    public void Resolve_AdvantageAndDisadvantage_CancelToNormal(bool advantage, bool disadvantage, D20Mode expected)
    {
        Assert.Equal(expected, D20.Resolve(advantage, disadvantage));
    }

    [Theory]
    [InlineData(D20Mode.Normal, false)]
    [InlineData(D20Mode.Advantage, true)]
    [InlineData(D20Mode.Disadvantage, false)]
    public void ElvenAccuracyApplies_OnlyWithAdvantage(D20Mode mode, bool expected)
    {
        Assert.Equal(expected, new D20Options(mode, ElvenAccuracy: true).ElvenAccuracyApplies);
        Assert.False(new D20Options(mode).ElvenAccuracyApplies);
    }
}
