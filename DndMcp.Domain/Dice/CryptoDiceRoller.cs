using System.Security.Cryptography;

namespace DndMcp.Domain.Dice;

/// <summary>
/// Table rolls from the operating system's CSPRNG.
///
/// <para>
/// <see cref="RandomNumberGenerator.GetInt32(int)"/> uses mask-and-reject sampling, so every face is
/// exactly equally likely — unlike <c>Next() % sides</c>, which over-weights low faces whenever the range
/// does not divide the generator's range. It is static and thread-safe, so one instance serves every
/// concurrent tool call. This is the "true dice rolling" the server promises; the Monte Carlo simulator
/// deliberately does NOT use it, because simulations must be reproducible from a seed.
/// </para>
/// </summary>
public sealed class CryptoDiceRoller : IDiceRoller
{
    public string Source => "cryptographic (OS CSPRNG)";

    public int Roll(int sides)
    {
        if (sides < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sides), sides, "A die needs at least one side.");
        }

        // [0, sides) shifted up, rather than GetInt32(1, sides + 1): sides + 1 overflows when sides is int.MaxValue.
        return RandomNumberGenerator.GetInt32(sides) + 1;
    }
}
