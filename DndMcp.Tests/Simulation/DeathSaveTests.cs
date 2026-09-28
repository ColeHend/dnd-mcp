using DndMcp.Domain.Simulation;
using Xunit;

namespace DndMcp.Tests.Simulation;

/// <summary>
/// Exit criterion (contract §5.8 test 2, research §C): a creature at 0 HP left alone dies with probability 0.404875, ends
/// stable with 0.41375 and wakes on a natural 20 with 0.181375 (exact; they sum to 1). Checked twice: exactly, by
/// enumerating the Markov chain of <see cref="DeathSaves.Step"/> (to 1e-12); and statistically, through the engine's own
/// turn loop (start-of-turn death saves on a dying creature nobody touches), each proportion inside its 99.9% Wilson
/// interval at 400,000 runs (seed 20260927) — tight enough (±0.0026) that a wrong threshold (DC 11: P(death) 0.46) or a
/// natural 1 counted once (P(death) 0.36) is far outside.
/// </summary>
public sealed class DeathSaveTests
{
    private const double Death = 0.404875;
    private const double Stable = 0.41375;
    private const double Revive = 0.181375;

    [Fact]
    public void Goldens_SumToOne() => Assert.Equal(1.0, Death + Stable + Revive, 12);

    [Fact]
    public void Step_EnumeratedMarkovChain_GivesTheExactGoldens()
    {
        var death = 0.0;
        var stable = 0.0;
        var revive = 0.0;
        var states = new Dictionary<(int S, int F), double> { [(0, 0)] = 1.0 };
        while (states.Count > 0)
        {
            var next = new Dictionary<(int, int), double>();
            foreach (var ((successes, failures), p) in states)
            {
                for (var face = 1; face <= 20; face++)
                {
                    var q = p / 20;
                    var (s, f, state) = DeathSaves.Step(successes, failures, face);
                    switch (state)
                    {
                        case DeathSaveState.Dead:
                            death += q;
                            break;
                        case DeathSaveState.Stable:
                            stable += q;
                            break;
                        case DeathSaveState.Revived:
                            revive += q;
                            break;
                        default:
                            next[(s, f)] = next.GetValueOrDefault((s, f)) + q;
                            break;
                    }
                }
            }

            states = next;
        }

        Assert.Equal(Death, death, 12);
        Assert.Equal(Stable, stable, 12);
        Assert.Equal(Revive, revive, 12);
    }

    [Theory]
    [InlineData(0, 0, 1, 0, 2, (int)DeathSaveState.Dying)]
    [InlineData(0, 1, 1, 0, 3, (int)DeathSaveState.Dead)]
    [InlineData(0, 0, 9, 0, 1, (int)DeathSaveState.Dying)]
    [InlineData(0, 0, 10, 1, 0, (int)DeathSaveState.Dying)]
    [InlineData(2, 2, 10, 3, 2, (int)DeathSaveState.Stable)]
    [InlineData(2, 2, 9, 2, 3, (int)DeathSaveState.Dead)]
    [InlineData(1, 2, 20, 0, 0, (int)DeathSaveState.Revived)]
    public void Step_Faces_FollowTheRule(int successes, int failures, int face, int s, int f, int state) =>
        Assert.Equal((s, f, (DeathSaveState)state), DeathSaves.Step(successes, failures, face));

    [Fact]
    public void Engine_DyingCreatureLeftAlone_MatchesTheGoldensWithinTheWilsonInterval()
    {
        const int runs = 400_000;
        var (build, target) = SimKit.Resolve(SimKit.Fighter2024, """{ "ac": 15 }""");
        var setup = DummyDpr.Setup(build, target, rounds: 5);
        setup.Templates[0].StartsDown = true;
        var fight = new Fight(setup);
        long dead = 0, stable = 0, revived = 0;
        for (var i = 0; i < runs; i++)
        {
            fight.Run(Simulator.IterationSeed(20260927, i));
            var c = fight.Creatures[0];
            if (c.Dead)
            {
                dead++;
            }
            else if (c.Stable)
            {
                stable++;
            }
            else if (c.Up)
            {
                revived++;
            }
        }

        Assert.Equal(runs, dead + stable + revived); // five turns always settle it
        foreach (var (count, golden) in new[] { (dead, Death), (stable, Stable), (revived, Revive) })
        {
            var interval = SimulationStatistics.Wilson(count, runs, SimulationStatistics.Z999);
            Assert.InRange(golden, interval.Low, interval.High);
        }
    }
}
