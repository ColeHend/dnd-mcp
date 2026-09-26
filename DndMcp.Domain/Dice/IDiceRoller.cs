namespace DndMcp.Domain.Dice;

/// <summary>
/// Source of individual die faces. Everything that rolls dice for the table goes through this, so the
/// choice between cryptographic and seeded randomness is made in one place and is visible in results.
/// </summary>
public interface IDiceRoller
{
    /// <summary>A uniformly distributed face in [1, <paramref name="sides"/>].</summary>
    int Roll(int sides);

    /// <summary>
    /// Human-readable provenance echoed with every result ("cryptographic" vs "pseudo-random, seed N"),
    /// so a reproducible seeded roll is never mistaken for a real one.
    /// </summary>
    string Source { get; }
}
