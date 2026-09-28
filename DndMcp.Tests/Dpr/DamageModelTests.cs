using DndMcp.Domain.Dice;
using DndMcp.Domain.Dpr;
using DndMcp.Domain.Features;
using Xunit;

namespace DndMcp.Tests.Dpr;

/// <summary>
/// Invariant: the damage building blocks are exact per value — each remap acts on one die (research A2), a crit and
/// Savage Attacker keep the support, a successful save and resistance each round down on the whole number (never the
/// x/2 − 0.25 shortcut), typeless damage is never adjusted, and the distribution summary reads percentiles the way a
/// discrete distribution must.
/// </summary>
public sealed class DamageModelTests
{
    private static Dictionary<long, double> Probabilities(Pmf<double> pmf)
    {
        var result = new Dictionary<long, double>();
        for (var i = 0; i < pmf.Count; i++)
        {
            result[pmf.Values[i]] = pmf.Weights[i] / pmf.Total;
        }

        return result;
    }

    [Fact]
    public void Die_Gwf2014_RerollsOneAndTwoOnceAndKeepsTheNewRoll()
    {
        var d6 = Probabilities(DamageDice.Die(6, DslValues.Remaps.Gwf2014));

        // A face f ≥ 3: 1/6 directly + 2/6 · 1/6 after a reroll; a 1 or 2 only after a reroll: 2/36.
        Assert.Equal(2.0 / 36, d6[1], 1e-15);
        Assert.Equal(2.0 / 36, d6[2], 1e-15);
        Assert.Equal((1.0 / 6) + (2.0 / 36), d6[6], 1e-15);
        Assert.Equal(25.0 / 6, DamageDice.ExpectedValue(6, DslValues.Remaps.Gwf2014), 1e-12);
    }

    [Fact]
    public void Die_Gwf2024_MovesOneAndTwoOntoThree()
    {
        var d6 = Probabilities(DamageDice.Die(6, DslValues.Remaps.Gwf2024));

        Assert.Equal([3L, 4, 5, 6], d6.Keys.Order());
        Assert.Equal(3.0 / 6, d6[3], 1e-15);
    }

    [Fact]
    public void Die_ElementalAdept_CountsAOneAsATwo()
    {
        var d6 = Probabilities(DamageDice.Die(6, elementalAdept: true));

        Assert.Equal([2L, 3, 4, 5, 6], d6.Keys.Order());
        Assert.Equal(2.0 / 6, d6[2], 1e-15);

        // GWF 2014 first, then Elemental Adept on the face that results: the rerolled 1 still becomes a 2.
        var both = Probabilities(DamageDice.Die(6, DslValues.Remaps.Gwf2014, elementalAdept: true));
        Assert.False(both.ContainsKey(1));
        Assert.Equal(4.0 / 36, both[2], 1e-15);
    }

    [Fact]
    public void Sum_SignedTerms_KeepsTheSign()
    {
        // Bane's −1d4 is −4..−1; Bless and Bane together −3..3 (they must not cancel: dndMath.ts bug 5).
        var bane = DamageDice.Sum([new DiceTerm(1, 4, Negative: true)], WorkMeter.Unlimited);
        var both = DamageDice.Sum([new DiceTerm(1, 4), new DiceTerm(1, 4, Negative: true)], WorkMeter.Unlimited);

        Assert.Equal([-4L, -3, -2, -1], bane.Values.ToArray());
        Assert.Equal(-3, both.Min);
        Assert.Equal(3, both.Max);
        Assert.Equal(4.0 / 16, Probabilities(both)[0], 1e-15);
    }

    [Fact]
    public void Sum_CritTimesTwo_RollsEveryDieTwice()
    {
        var crit = DamageDice.Sum([new DiceTerm(2, 6)], WorkMeter.Unlimited, times: 2);

        Assert.Equal(4, crit.Min);
        Assert.Equal(24, crit.Max);
        Assert.Equal(14, DamageDice.Mean(crit), 1e-12);
    }

    [Fact]
    public void MaxOfTwo_OneD8_IsTheSavageAttackerGolden()
    {
        var best = DamageDice.MaxOfTwo(DamageDice.Die(8));

        Assert.Equal(93.0 / 16, DamageDice.Mean(best), 1e-12); // 5.8125
        Assert.Equal(1.0 / 64, Probabilities(best)[1], 1e-15); // both show 1
        Assert.Equal(15.0 / 64, Probabilities(best)[8], 1e-15); // F(8)² − F(7)²
        Assert.Equal(1, best.Min);
    }

    private static ResolvedTarget Target(string json) => TargetResolver.Resolve(DprTestKit.Target(json), 5);

    [Theory]
    [InlineData(7, null, false, 7)]
    [InlineData(7, "fire", false, 3)] // resisted: floor(7/2)
    [InlineData(7, "fire", true, 1)] // half on a save, then resisted: floor(floor(7/2)/2) = floor(7/4)
    [InlineData(7, "cold", false, 14)] // vulnerable
    [InlineData(7, "cold", true, 6)] // half, then doubled
    [InlineData(7, "poison", false, 0)] // immune
    [InlineData(-3, null, false, 0)] // a negative total deals nothing
    [InlineData(-3, "fire", true, 0)]
    [InlineData(1, "fire", false, 0)]
    public void Apply_DamageOfAType_HalvesThenResistsThenDoublesAndFloorsAtZero(long rolled, string? type, bool halve, long expected)
    {
        var target = Target("""{ "resistances": ["fire"], "vulnerabilities": ["cold"], "immunities": ["poison"] }""");

        Assert.Equal(expected, DamageAdjustment.Apply(rolled, type, target, halve));
    }

    [Theory]
    [InlineData(false, false, 4)] // mundane: the qualified resistance applies, floor(9/2)
    [InlineData(true, false, 9)] // magical: it does not
    [InlineData(false, true, 9)] // silvered: it does not
    public void Apply_QualifiedResistance_ReadsTheDamagesProperties(bool magical, bool silvered, long expected)
    {
        var target = TargetResolver.Resolve(null, 5, Features.TargetStatBlocks.WerewolfLike());

        Assert.Equal(expected, DamageAdjustment.Apply(9, "slashing", target, properties: new DamageProperties(magical, silvered, false)));
        Assert.Equal(4, DamageAdjustment.Apply(9, "cold", target, properties: new DamageProperties(magical, silvered, false))); // unqualified
        Assert.Equal(4, DamageAdjustment.Apply(9, "slashing", target)); // left out: plain damage
    }

    [Fact]
    public void Apply_TypelessDamage_IsNeverResisted()
    {
        var target = Target("""{ "resistances": ["acid", "bludgeoning", "cold", "fire", "force", "lightning", "necrotic", "piercing", "poison", "psychic", "radiant", "slashing", "thunder"] }""");

        Assert.Equal(9, DamageAdjustment.Apply(9, null, target));
        Assert.Equal(4, DamageAdjustment.Apply(9, null, target, halve: true));
    }

    [Fact]
    public void Round1_HalfOnSuccessOfAFlatAmount_IsNotTheMeanShortcut()
    {
        // A flat 5 halved on a success is 2, not 5/2 − 0.25 = 2.25 (dndMath.ts bug 4): DC 11 vs +0 succeeds half the time.
        var build = """{ "name": "Caster", "level": 5, "modifiers": [{ "kind": "save_effect", "name": "Burst", "ability": "con", "dc": 11, "amount": 5 }] }""";

        var result = DprTestKit.Evaluate(build, """{ "saves": {"con": 0} }""");

        Assert.Equal((0.5 * 5) + (0.5 * 2), result.DamagePerRound, 1e-12);
    }

    [Fact]
    public void Percentile_DiscreteDistribution_IsTheSmallestTotalReachingTheFraction()
    {
        var distribution = DamageDistribution.From(Pmf<double>.FromPairs([new(0, 0.5), new(10, 0.25), new(20, 0.25)], 1.0), hitPoints: 10);

        Assert.Equal(0, distribution.Percentile(0.5));
        Assert.Equal(10, distribution.Percentile(0.51));
        Assert.Equal(10, distribution.Percentile(0.75));
        Assert.Equal(20, distribution.Percentile(0.9));
        Assert.Equal(20, distribution.Percentile(1));
        Assert.Equal([0L, 0, 0, 10, 20], distribution.Quantiles);
        Assert.Equal(0.5, distribution.ZeroChance);
        Assert.Equal(0.5, distribution.AtLeastHitPointsChance);
        Assert.Equal(7.5, distribution.Mean);
        Assert.Throws<ArgumentOutOfRangeException>(() => distribution.Percentile(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => distribution.Percentile(double.NaN));
    }
}
