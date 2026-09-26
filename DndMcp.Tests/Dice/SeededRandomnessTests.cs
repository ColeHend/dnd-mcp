using DndMcp.Domain.Dice;
using DndMcp.Domain.Rng;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: the seeded generator is the published xoshiro256** (reference output, not merely "deterministic"),
/// seeded through SplitMix64, and its bounded draws are unbiased — so a seed means the same dice everywhere, forever,
/// and the dice it produces are fair.
///
/// <para>
/// A test that only checks "same seed, same output" passes for any deterministic function, including a broken
/// one. The reference vectors come from the reference C algorithms, re-computed independently in Python.
/// </para>
/// </summary>
public sealed class SeededRandomnessTests
{
    [Fact]
    public void SplitMix64_SeedZero_MatchesReferenceOutput()
    {
        var mixer = new SplitMix64(0);

        Assert.Equal(0xE220A8397B1DCDAFUL, mixer.Next());
        Assert.Equal(0x6E789E6AA1B965F4UL, mixer.Next());
        Assert.Equal(0x06C45D188009454FUL, mixer.Next());
    }

    [Fact]
    public void Xoshiro_StateOneTwoThreeFour_MatchesReferenceOutput()
    {
        var generator = new Xoshiro256StarStar(1, 2, 3, 4);

        Assert.Equal(
            [11520UL, 0UL, 1509978240UL, 1215971899390074240UL, 1216172134540287360UL, 607988272756665600UL],
            Enumerable.Range(0, 6).Select(_ => generator.NextUInt64()).ToArray());
    }

    [Fact]
    public void Xoshiro_Seed42ThroughSplitMix_MatchesReferenceOutput()
    {
        var generator = new Xoshiro256StarStar(42);

        Assert.Equal(
            [1546998764402558742UL, 6990951692964543102UL, 12544586762248559009UL, 17057574109182124193UL],
            Enumerable.Range(0, 4).Select(_ => generator.NextUInt64()).ToArray());
    }

    [Fact]
    public void NextBounded_RangeAboveHalfOfUInt_RejectsBiasedDrawsExactlyAsReference()
    {
        // With range 3e9, 29% of raw draws are biased and must be rejected; skipping the rejection changes the first
        // result. Reference: Lemire's method over the high 32 bits, recomputed in Python.
        var generator = new Xoshiro256StarStar(42);

        Assert.Equal(
            [1136940751u, 2040130232u, 2774078835u, 2157775733u, 2284123142u],
            Enumerable.Range(0, 5).Select(_ => generator.NextBounded(3_000_000_000u)).ToArray());
    }

    [Fact]
    public void SeededRoller_Seed42_RollsTheSameD20sForever()
    {
        // The promise of a seed: "seed 42" gives these d20s on every machine and every future build.
        var roller = new SeededDiceRoller(42);

        Assert.Equal([2, 8, 14, 19, 20, 16, 15, 18, 16, 12], Enumerable.Range(0, 10).Select(_ => roller.Roll(20)).ToArray());
    }

    [Fact]
    public void Xoshiro_AllZeroState_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new Xoshiro256StarStar(0, 0, 0, 0));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(6u)]
    [InlineData(7u)]
    [InlineData(20u)]
    [InlineData(1000u)]
    [InlineData(uint.MaxValue)]
    public void NextBounded_AnyRange_StaysBelowRange(uint range)
    {
        var generator = new Xoshiro256StarStar(7);

        for (var i = 0; i < 10_000; i++)
        {
            Assert.True(generator.NextBounded(range) < range);
        }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(20)]
    [Trait("Category", "Slow")]
    public void SeededRoller_OneMillionRolls_PassesChiSquare(int sides)
    {
        var roller = new SeededDiceRoller(12345);
        var counts = new long[sides + 1];
        const int Rolls = 1_000_000;
        for (var i = 0; i < Rolls; i++)
        {
            counts[roller.Roll(sides)]++;
        }

        Assert.Equal(0, counts[0]);
        Assert.True(ChiSquare.Statistic(counts.AsSpan(1), Rolls) < ChiSquare.Critical(sides - 1),
            $"χ² for d{sides} is {ChiSquare.Statistic(counts.AsSpan(1), Rolls):F1}.");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(42L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void SeededRoller_Source_SaysPseudoRandomReproducibleAndEchoesSeed(long seed)
    {
        var source = new SeededDiceRoller(seed).Source;

        Assert.Contains("pseudo-random", source, StringComparison.Ordinal);
        Assert.Contains("reproducible", source, StringComparison.Ordinal);
        Assert.Contains($"seed {seed}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SeededRoller_DifferentSeeds_GiveDifferentSequences()
    {
        var a = new SeededDiceRoller(1);
        var b = new SeededDiceRoller(2);

        Assert.NotEqual(
            Enumerable.Range(0, 20).Select(_ => a.Roll(1000)).ToArray(),
            Enumerable.Range(0, 20).Select(_ => b.Roll(1000)).ToArray());
    }
}

/// <summary>Pearson's χ² against a uniform die, with a deliberately loose critical value (p ≈ 10⁻⁶).</summary>
internal static class ChiSquare
{
    public static double Statistic(ReadOnlySpan<long> counts, long total)
    {
        var expected = (double)total / counts.Length;
        var sum = 0.0;
        foreach (var count in counts)
        {
            sum += (count - expected) * (count - expected) / expected;
        }

        return sum;
    }

    /// <summary>
    /// Wilson–Hilferty approximation of the χ² quantile at z = 4.75 (one-sided p ≈ 1e-6): loose enough that a fair
    /// generator essentially never fails. It catches gross faults — a face that never comes up, a range shifted by one,
    /// a stuck or short-cycled generator, a crude modulo over a small range — not the ~1e-9 bias of a 32-bit modulo,
    /// which no feasible sample can see (Lemire's rejection is there to remove it by construction).
    /// </summary>
    public static double Critical(int degreesOfFreedom)
    {
        const double Z = 4.75;
        var k = (double)degreesOfFreedom;
        var term = 1 - (2 / (9 * k)) + (Z * Math.Sqrt(2 / (9 * k)));
        return k * term * term * term;
    }
}
