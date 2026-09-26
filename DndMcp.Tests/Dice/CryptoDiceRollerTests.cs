using DndMcp.Domain.Dice;
using Xunit;

namespace DndMcp.Tests.Dice;

/// <summary>
/// Invariant: <see cref="CryptoDiceRoller"/> only ever returns a face in [1, sides], can return every face
/// including the highest, and refuses a die with fewer than one side.
///
/// <para>
/// The classic mistakes here are silent: a range that stops one short means a d20 never rolls a natural 20, and a
/// zero-based range produces 0s that still sum into a plausible-looking total. Range and coverage are asserted
/// separately so each mistake fails its own test, and a χ² test over 10^6 d20 rolls (Category=Slow) catches a die
/// that is merely lopsided.
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

    [Fact]
    public void Roll_IntMaxValueSides_DoesNotOverflow()
    {
        // GetInt32(1, sides + 1) overflowed here; GetInt32(sides) + 1 cannot.
        for (var i = 0; i < 1_000; i++)
        {
            Assert.InRange(_roller.Roll(int.MaxValue), 1, int.MaxValue);
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Roll_OneMillionD20s_PassesChiSquare()
    {
        var counts = new long[21];
        const int Rolls = 1_000_000;
        for (var i = 0; i < Rolls; i++)
        {
            counts[_roller.Roll(20)]++;
        }

        var statistic = ChiSquare.Statistic(counts.AsSpan(1), Rolls);
        Assert.True(statistic < ChiSquare.Critical(19), $"χ² over 10^6 d20 rolls is {statistic:F1}.");
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
