using System.Numerics;

namespace DndMcp.Domain.Rng;

/// <summary>
/// Blackman and Vigna's xoshiro256** (public domain): the seeded generator behind reproducible rolls and, from
/// Phase 5, the Monte Carlo simulator.
///
/// <para>
/// Why not <c>new Random(seed)</c>: in .NET 10 a seeded <c>System.Random</c> is the legacy Knuth subtractive
/// generator (<c>CompatSeedImpl</c>), kept only for backward compatibility, and its sequence is an implementation
/// detail rather than a published algorithm. This one has published reference output, so "seed 42" means the same
/// dice on every machine and every future runtime, which is the whole promise of a seed. Pinned by
/// <c>SeededRandomnessTests</c> against the reference implementation's output, including what seed 42 rolls.
/// </para>
/// <para>
/// A mutable struct: copying it forks the sequence. Hold it in a field, never pass it by value to something that
/// draws from it.
/// </para>
/// </summary>
public struct Xoshiro256StarStar
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;

    /// <summary>Seeds the 256-bit state from one 64-bit seed through <see cref="SplitMix64"/>.</summary>
    public Xoshiro256StarStar(ulong seed)
    {
        var mixer = new SplitMix64(seed);
        _s0 = mixer.Next();
        _s1 = mixer.Next();
        _s2 = mixer.Next();
        _s3 = mixer.Next();
    }

    /// <summary>
    /// Uses <paramref name="s0"/>..<paramref name="s3"/> as the raw state, for reference-vector tests. An all-zero
    /// state is a fixed point that returns 0 forever, so it is refused.
    /// </summary>
    public Xoshiro256StarStar(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        if ((s0 | s1 | s2 | s3) == 0)
        {
            throw new ArgumentException("xoshiro256** state must not be all zero.");
        }

        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    public ulong NextUInt64()
    {
        var result = BitOperations.RotateLeft(_s1 * 5, 7) * 9;
        var t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = BitOperations.RotateLeft(_s3, 45);

        return result;
    }

    /// <summary>
    /// A uniform integer in [0, <paramref name="range"/>) by Lemire's multiply-and-reject method.
    /// </summary>
    /// <remarks>
    /// <c>Next() % range</c> over-weights low results whenever range does not divide 2^32 — a d6 would favour 1-4.
    /// Lemire's method rejects exactly the biased products, so every result is equally likely, and it usually needs no
    /// division. Takes the high 32 bits, the generator's strongest.
    /// </remarks>
    public uint NextBounded(uint range)
    {
        if (range == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(range), range, "The range must be at least 1.");
        }

        var product = (ulong)NextUInt32() * range;
        var low = (uint)product;

        if (low < range)
        {
            var threshold = (0u - range) % range;
            while (low < threshold)
            {
                product = (ulong)NextUInt32() * range;
                low = (uint)product;
            }
        }

        return (uint)(product >> 32);
    }

    private uint NextUInt32() => (uint)(NextUInt64() >> 32);
}
