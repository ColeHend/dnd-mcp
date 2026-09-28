using System.Diagnostics;
using DndMcp.Domain.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// The report's statistics (Wilson score intervals, CLT means, histogram percentiles) against hand-computed values, and
/// the contract's speed target for a typical run.
/// </summary>
public sealed class SimulationStatisticsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0, 10, 0.0, 0.0, 0.277533)]
    [InlineData(5, 10, 0.5, 0.236593, 0.763407)]
    [InlineData(10, 10, 1.0, 0.722467, 1.0)]
    [InlineData(9_604, 19_208, 0.5, 0.492930, 0.507070)]
    public void Wilson_MatchesHandComputedIntervals(long count, long total, double estimate, double low, double high)
    {
        var p = SimulationStatistics.Wilson(count, total);
        Assert.Equal(estimate, p.Estimate, 12);
        Assert.Equal(low, p.Low, 6);
        Assert.Equal(high, p.High, 6);
    }

    [Fact]
    public void Wilson_NineThousandSixHundredFourAtOneHalf_IsAboutPlusMinusOnePercent()
    {
        // Research §C: n = 1.96² × 0.25 / 0.01² = 9,604 gives ±1% at the worst case p = 1/2.
        var p = SimulationStatistics.Wilson(4_802, 9_604);
        Assert.InRange(p.HalfWidth, 0.0099, 0.0101);
    }

    [Fact]
    public void Mean_FromIntegerSums_GivesTheSampleMeanAndStandardError()
    {
        // Samples 2, 4, 4, 4, 5, 5, 7, 9: mean 5, sample variance 32/7, SE √(32/7/8).
        long[] samples = [2, 4, 4, 4, 5, 5, 7, 9];
        var m = SimulationStatistics.Mean(samples.Sum(), samples.Sum(x => x * x), samples.Length);
        Assert.Equal(5.0, m.Mean, 12);
        Assert.Equal(Math.Sqrt(32.0 / 7 / 8), m.StandardError, 12);
        Assert.Equal(5.0 - (SimulationStatistics.Z95 * m.StandardError), m.Low, 12);

        var perRound = SimulationStatistics.Mean(samples.Sum(), samples.Sum(x => x * x), samples.Length, scale: 2);
        Assert.Equal(2.5, perRound.Mean, 12);
    }

    [Theory]
    [InlineData(0.5, 2)]
    [InlineData(0.9, 3)]
    [InlineData(0.1, 1)]
    public void Percentile_IsTheSmallestValueReachingTheFraction(double fraction, long expected)
    {
        // Values 1..3 (offset 1) with counts 4, 4, 2: cumulative 0.4, 0.8, 1.0.
        Assert.Equal(expected, SimulationStatistics.Percentile([4, 4, 2], fraction, offset: 1));
    }

    [Fact]
    public void Speed_Typical4v3Times10000_TakesUnderTwoSeconds()
    {
        var spec = SimKit.Spec([SimKit.Pc(SimKit.Fighter2024, hp: 44, ac: 18, name: "Fighter", count: 4)], [SimKit.Monster(TestStatBlocks.Ogre, count: 3)], iterations: 10_000);
        Simulator.Run(spec, 1); // warm-up (JIT)
        var watch = Stopwatch.StartNew();
        var report = Simulator.Run(spec, 2);
        watch.Stop();
        output.WriteLine($"4 fighters vs 3 ogres × {report.Iterations:N0}: {watch.Elapsed.TotalMilliseconds:0} ms ({report.Iterations / watch.Elapsed.TotalSeconds:N0} fights/s)");
        Assert.True(watch.Elapsed.TotalSeconds < 2, $"{watch.Elapsed.TotalSeconds:0.00} s");
    }
}
