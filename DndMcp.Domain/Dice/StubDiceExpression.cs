using System.Text.RegularExpressions;
using DndMcp.Domain.Core;

namespace DndMcp.Domain.Dice;

/// <summary>
/// PHASE 0 STUB — replaced wholesale by the Phase 1 parser (keep/drop, rerolls, exploding dice, exact
/// distributions). It exists only so the server has one real tool to prove the MCP pipeline end to end:
/// registration, argument binding, cryptographic rolling, and the input-error path the model sees.
///
/// <para>Accepts a sum of <c>NdM</c> and integer terms, e.g. <c>2d6+1d4+3</c> or <c>1d20-1</c>.</para>
/// </summary>
public static partial class StubDiceExpression
{
    public const int MaxDice = 1000;
    public const int MaxSides = 1000;
    public const int MaxConstant = 1_000_000;

    /// <summary>
    /// Totals are plain <c>int</c> sums, and nothing else bounds how many constant terms an expression holds:
    /// 2,148 terms of "+1000000" wrap to a negative total that is reported as the roll. Capping the length
    /// caps the terms (at most one per two characters), so the worst case, MaxExpressionLength / 2 x MaxConstant
    /// + MaxDice x MaxSides, stays far inside int. It also bounds the expression echoed on every result line.
    /// </summary>
    public const int MaxExpressionLength = 256;

    public sealed record Term(int Sign, int Count, int Sides, int Constant)
    {
        public bool IsDice => Sides > 0;
    }

    public sealed record DieResult(int Sides, int Face);

    public sealed record RollResult(string Expression, int Total, IReadOnlyList<DieResult> Dice, int Modifier, string Source);

    [GeneratedRegex(@"^(?<sign>[+-]?)(?:(?<count>\d*)d(?<sides>\d+)|(?<constant>\d+))", RegexOptions.IgnoreCase)]
    private static partial Regex TermRegex();

    [GeneratedRegex(@"\s*([+-])\s*")]
    private static partial Regex OperatorSpacingRegex();

    public static IReadOnlyList<Term> Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new DndInputException("The dice expression is empty. Example: \"2d6+3\".");
        }

        if (expression.Length > MaxExpressionLength)
        {
            throw new DndInputException(
                $"The dice expression is {expression.Length} characters long; the limit is {MaxExpressionLength}. Example: \"2d6+1d4+3\".");
        }

        // Whitespace is only legal around operators. Stripping it everywhere would turn "2d6 3" into "2d63"
        // and roll the wrong die without complaint.
        var text = OperatorSpacingRegex().Replace(expression.Trim(), "$1");
        if (text.Any(char.IsWhiteSpace))
        {
            throw new DndInputException(
                $"Could not read the dice expression \"{expression}\": terms must be joined with + or -, e.g. \"2d6 + 3\".");
        }

        var terms = new List<Term>();
        var position = 0;

        while (position < text.Length)
        {
            var match = TermRegex().Match(text[position..]);

            // Every term after the first must carry an explicit sign, otherwise "2d63" style typos would
            // silently parse as something else.
            if (!match.Success || (terms.Count > 0 && match.Groups["sign"].Value.Length == 0))
            {
                throw new DndInputException(
                    $"Could not read the dice expression \"{expression}\" at \"{text[position..]}\". " +
                    "This build accepts sums of NdM and whole numbers, e.g. \"2d6+1d4+3\" or \"1d20-1\".");
            }

            var sign = match.Groups["sign"].Value == "-" ? -1 : 1;

            if (match.Groups["sides"].Success)
            {
                var count = match.Groups["count"].Value.Length == 0 ? 1 : ParseBounded(match.Groups["count"].Value, MaxDice, expression);
                var sides = ParseBounded(match.Groups["sides"].Value, MaxSides, expression);

                if (count < 1 || sides < 1)
                {
                    throw new DndInputException($"\"{match.Value}\" needs at least one die with at least one side.");
                }

                terms.Add(new Term(sign, count, sides, 0));
            }
            else
            {
                terms.Add(new Term(sign, 0, 0, ParseBounded(match.Groups["constant"].Value, MaxConstant, expression)));
            }

            position += match.Length;
        }

        if (terms.Sum(t => t.Count) > MaxDice)
        {
            throw new DndInputException($"\"{expression}\" rolls more than {MaxDice} dice.");
        }

        return terms;
    }

    public static RollResult Roll(string expression, IDiceRoller roller)
    {
        var terms = Parse(expression);
        var dice = new List<DieResult>();
        var total = 0;
        var modifier = 0;

        foreach (var term in terms)
        {
            if (term.IsDice)
            {
                for (var i = 0; i < term.Count; i++)
                {
                    var face = roller.Roll(term.Sides);
                    dice.Add(new DieResult(term.Sides, face));
                    total += term.Sign * face;
                }
            }
            else
            {
                modifier += term.Sign * term.Constant;
                total += term.Sign * term.Constant;
            }
        }

        return new RollResult(expression, total, dice, modifier, roller.Source);
    }

    private static int ParseBounded(string digits, int max, string expression)
    {
        if (!int.TryParse(digits, out var value) || value > max)
        {
            throw new DndInputException($"\"{digits}\" in \"{expression}\" is too large (limit {max}).");
        }

        return value;
    }
}
