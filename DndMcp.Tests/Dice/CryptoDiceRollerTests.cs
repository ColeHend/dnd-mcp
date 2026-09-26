using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: <see cref="CryptoDiceRoller"/> only ever returns a face in [1, sides], can return every face
/// including the highest, and refuses a die with fewer than one side.
///
/// <para>
/// The classic mistakes here are silent: an exclusive upper bound passed as <c>sides</c> instead of
/// <c>sides + 1</c> means a d20 never rolls a natural 20, and a zero-based range produces 0s that still sum into
/// a plausible-looking total. Range and coverage are asserted separately so each mistake fails its own test.
/// Distribution quality (chi-square over 10^6 rolls) is a Phase 1 slow test.
/// </para>
/// </summary>
public sealed class CryptoDiceRollerTests
{
    private readonly CryptoDiceRoller _roller = new();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(1000)]
    public void Roll_ManyRolls_NeverLeavesOneToSides(int sides)
    {
        for (var i = 0; i < 10_000; i++)
        {
            Assert.InRange(_roller.Roll(sides), 1, sides);
        }
    }

    [Fact]
    public void Roll_D20Rolled20000Times_ShowsEveryFaceIncludingNatural20()
    {
        // A given face is missing from 20,000 fair d20 rolls with probability (19/20)^20000 ≈ 1e-445.
        var seen = new HashSet<int>();
        for (var i = 0; i < 20_000; i++)
        {
            seen.Add(_roller.Roll(20));
        }

        Assert.Equal(Enumerable.Range(1, 20), seen.Order());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Roll_SidesBelowOne_ThrowsArgumentOutOfRange(int sides)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => _roller.Roll(sides));

        Assert.Equal("sides", ex.ParamName);
    }

    [Fact]
    public void Source_Always_StatesCryptographicProvenance()
    {
        // Echoed with every roll so a reproducible seeded roll is never mistaken for a real one.
        Assert.Contains("cryptographic", _roller.Source, StringComparison.OrdinalIgnoreCase);
    }
}
