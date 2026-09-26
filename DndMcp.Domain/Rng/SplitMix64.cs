namespace DndMcp.Domain.Rng;

/// <summary>
/// Vigna's SplitMix64 (public domain), used only to expand one 64-bit seed into xoshiro's 256-bit state.
///
/// <para>
/// xoshiro must never start from a state that is all zero, and seeds people actually type (0, 1, 42) are
/// nearly all-zero bit patterns that give correlated early output if copied into the state directly. SplitMix64
/// scrambles each seed into well-mixed words, which is the seeding its authors recommend. The Phase 5 simulator
/// seeds each iteration as <c>SplitMix64(master ⊕ i·φ)</c> through the same type.
/// </para>
/// </summary>
public struct SplitMix64
{
    /// <summary>2^64 / φ, the Weyl-sequence increment.</summary>
    public const ulong GoldenGamma = 0x9E3779B97F4A7C15UL;

    private ulong _state;

    public SplitMix64(ulong seed)
    {
        _state = seed;
    }

    public ulong Next()
    {
        _state += GoldenGamma;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
