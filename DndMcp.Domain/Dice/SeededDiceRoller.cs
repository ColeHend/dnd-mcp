using System.Globalization;
using DndMcp.Domain.Rng;

namespace DndMcp.Domain.Dice;

/// <summary>
/// Reproducible rolls: the same seed gives the same faces in the same order, on any machine.
///
/// <para>
/// For testing a build, replaying a fight or showing someone "the" roll again; never the default. Its
/// <see cref="Source"/> says "pseudo-random, reproducible" so a seeded result is never mistaken for a table roll
/// from the CSPRNG. Not thread-safe (the generator is mutable state); create one per call.
/// </para>
/// </summary>
public sealed class SeededDiceRoller : IDiceRoller
{
    private Xoshiro256StarStar _generator;

    public SeededDiceRoller(long seed)
    {
        Seed = seed;

        // Two's-complement reinterpretation, so every long the tool accepts (negatives included) is a distinct seed.
        _generator = new Xoshiro256StarStar(unchecked((ulong)seed));
    }

    public long Seed { get; }

    public string Source => string.Create(CultureInfo.InvariantCulture, $"pseudo-random, reproducible (xoshiro256**, seed {Seed})");

    public int Roll(int sides)
    {
        if (sides < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sides), sides, "A die needs at least one side.");
        }

        return (int)_generator.NextBounded((uint)sides) + 1;
    }
}
