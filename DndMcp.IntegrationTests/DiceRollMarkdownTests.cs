using DndMcp.Domain.Dice;
using DndMcp.Formatting;
using Xunit;

namespace DndMcp.IntegrationTests;

/// <summary>
/// Invariant: when a long reroll or compounding chain inside one die is shortened, the text says exactly how many
/// faces were left out, so "rolled 1, rerolled 5 times, kept 6" is never shown as a different number of rerolls.
///
/// <para>
/// These need exact faces, which the MCP tool cannot be asked for, so they call the formatter directly (the host's
/// internals are visible to this project) with a scripted roller.
/// </para>
/// </summary>
public sealed class DiceRollMarkdownTests
{
    [Theory]
    // Eight compounded faces: the first four, then "3 more", then the last.
    [InlineData("1d6!!", new[] { 6, 6, 6, 6, 6, 6, 6, 2 }, "[6!+6!+6!+6!+…3 more…+2=44]")]
    // Five rerolled faces before the 6: the first and the last rerolled face, "3 more" between.
    [InlineData("1d6r<=5", new[] { 1, 2, 3, 4, 5, 6 }, "[1⟳…3 more…⟳5⟳6]")]
    [InlineData("1d6r<=5", new[] { 1, 2, 6 }, "[1⟳2⟳6]")]
    public void Format_LongChainInsideOneDie_ElidesTheMiddleAndCountsItExactly(string expression, int[] faces, string expected)
    {
        var parsed = DiceExpression.Parse(expression);
        var roll = DiceEvaluator.Roll(parsed, new FixedRoller(faces));

        Assert.Contains(expected, DiceRollMarkdown.Format(parsed, [roll], null, "fixed"), StringComparison.Ordinal);
    }

    private sealed class FixedRoller : IDiceRoller
    {
        private readonly int[] _faces;
        private int _next;

        public FixedRoller(int[] faces)
        {
            _faces = faces;
        }

        public string Source => "fixed (test fake)";

        public int Roll(int sides) => _faces[_next++];
    }
}
