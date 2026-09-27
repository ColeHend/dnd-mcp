using System.Globalization;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;

namespace DndMcp.Domain.Probability;

/// <summary>
/// Dice added to (or subtracted from) a d20 roll: Bless "1d4", Bane "-1d4", both at once "1d4-1d4", Bardic
/// Inspiration "1d8". Their distribution is what <see cref="AttackRoll.Odds"/> and <see cref="SavingThrow.FailChance"/>
/// sum over, so each outcome of the dice shifts the whole d20 threshold — "+2.5 on average" is exact only for a normal
/// roll far from the 2 and 20 clamps.
///
/// <para>
/// <b>Plain dice and whole numbers only.</b> A keep, reroll, explosion, clamp or success count changes what a die means,
/// and none of them is a real effect on an attack roll or save; a label would read as a damage type; a comparison or a
/// multiplication has no meaning for an amount added to a d20. Each is refused with the construct named, rather than
/// parsed by the full dice grammar and quietly evaluated: "1d4kh1" parses as a plain 1d4 there (keeping every die is
/// normalised away), and "2d6kh1" as a single d6, so accepting the grammar would silently change the build.
/// </para>
/// <para>
/// <b>Signs are kept on dice terms.</b> "1d8-1d4" subtracts the d4. The TypeScript port target dropped the sign (its
/// bug 5) and read it as +1d4; the parse goes through <see cref="DiceExpression.Parse"/>, whose tree keeps it.
/// </para>
/// </summary>
public static class BonusDice
{
    /// <summary>Dice in one text, all terms together; matches the build DSL's limit on any dice string.</summary>
    public const int MaxDice = 50;

    /// <summary>Sides on one die; matches the build DSL's limit.</summary>
    public const int MaxSides = 100;

    /// <summary>The largest whole number one term may add or subtract; matches the build DSL's flat limit.</summary>
    public const int MaxWholeNumber = 100;

    /// <summary>
    /// The largest |value| a bonus distribution may hold: the dice grammar's own static bound, so any parsed expression
    /// passes, and threshold arithmetic in <c>long</c> (an int AC minus an int bonus minus this) cannot overflow.
    /// </summary>
    public const long MaxMagnitude = DiceLimits.MaxMagnitude;

    /// <summary>
    /// Work units for compiling the distribution. The limits above keep the worst case (50d100, about 10^7 units) far
    /// below it; it exists because a Domain computation never runs unmetered in production (Phase 1 carry-forward).
    /// </summary>
    private const long WorkBudget = 100_000_000;

    private const string Accepted =
        "Bonus dice are plain dice and whole numbers joined by + or -, e.g. \"1d4\" (Bless), \"-1d4\" (Bane) or \"1d4+1d6\".";

    private static readonly HashSet<string> ModifierWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "k", "kh", "kl", "dh", "dl", "r", "ro", "min", "max", "cs", "cf",
    };

    private static readonly HashSet<string> ModeWords = new(StringComparer.OrdinalIgnoreCase) { "adv", "dis", "ea" };

    /// <summary>
    /// The distribution of the dice's total, normalised (Total = 1), with its exact support: "1d4" → 1..4, "-1d4" →
    /// −4..−1, "1d4+1d6" → 2..10, "-1d4+1d4" → −3..3.
    /// </summary>
    /// <param name="text">The dice as the caller wrote them.</param>
    /// <param name="where">
    /// Names the field the way the argument guard counts it, e.g. <c>modifiers item 3 (to_hit): dice</c>. Every message
    /// starts with it and then quotes the text: <c>modifiers item 3 (to_hit): dice "1d4kh1" cannot be used: …</c>.
    /// </param>
    /// <exception cref="DndInputException">The text is empty, malformed, uses anything but plain dice and whole numbers, or passes a limit.</exception>
    public static Pmf<double> Parse(string text, string where)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DndInputException($"{where} is empty. {Accepted}");
        }

        RefuseUnsupportedConstructs(text, where);

        DiceExpression expression;
        try
        {
            expression = DiceExpression.Parse(text);
        }
        catch (DndInputException ex)
        {
            // The parser's own hint lists its full grammar ("4d6kh3", "8d6>=30"); the closing sentence narrows it.
            throw new DndInputException($"{where}: {ex.Message} {Accepted}", ex);
        }

        var dice = 0L;
        CheckTerms(expression.Root, text, where, ref dice);
        if (dice > MaxDice)
        {
            throw Refused(text, where, $"it rolls {dice.ToString(CultureInfo.InvariantCulture)} dice; the limit is {MaxDice}");
        }

        return new DistributionCompiler<double>(DoubleArithmetic.Instance, new WorkMeter(WorkBudget), DiceCaps.Default)
            .Compile(expression.Root);
    }

    /// <summary>
    /// Refuses a distribution that cannot be bonus dice. Only code builds a <see cref="Pmf{T}"/> (callers parse text
    /// with <see cref="Parse"/>), so a failure here is a bug, reported as an <see cref="ArgumentException"/> rather than
    /// an input error the model could act on: weights that do not sum to the total would make the hit chance a
    /// weighted sum of the wrong size, and a value beyond <see cref="MaxMagnitude"/> could overflow the thresholds.
    /// </summary>
    internal static void CheckDistribution(Pmf<double> dice, string paramName)
    {
        if (!double.IsFinite(dice.Total) || dice.Total <= 0)
        {
            throw new ArgumentException($"The bonus dice distribution's total must be positive and finite; it is {dice.Total}.", paramName);
        }

        if (dice.Min < -MaxMagnitude || dice.Max > MaxMagnitude)
        {
            throw new ArgumentException($"The bonus dice distribution reaches {dice.Min}..{dice.Max}, beyond ±{MaxMagnitude}.", paramName);
        }

        var sum = 0.0;
        foreach (var weight in dice.Weights)
        {
            if (!double.IsFinite(weight) || weight < 0)
            {
                throw new ArgumentException($"The bonus dice distribution has the weight {weight}; weights must be finite and not negative.", paramName);
            }

            sum += weight;
        }

        if (Math.Abs((sum / dice.Total) - 1.0) > 1e-9)
        {
            throw new ArgumentException($"The bonus dice distribution's weights sum to {sum / dice.Total} of its total, not 1.", paramName);
        }
    }

    /// <summary>
    /// Refuses, by name, every construct beyond plain dice and whole numbers, before the grammar parses (and possibly
    /// normalises) it. Scanning the text rather than the parsed tree is deliberate: the tree no longer shows "kh1" on a
    /// single die, or "kh2" on 2d6. Only digits, <c>d</c>, <c>%</c>, signs, parentheses and spaces get past this, and
    /// with those the grammar can only express sums.
    /// </summary>
    private static void RefuseUnsupportedConstructs(string text, string where)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsLetter(c) && c is not ('d' or 'D'))
            {
                var word = LetterRun(text, i);
                throw Refused(text, where, ModeWords.Contains(word)
                    ? $"\"{word}\" rolls the d20 itself with advantage or disadvantage, and bonus dice are added to that roll; give advantage or disadvantage as its own setting"
                    : ModifierWords.Contains(word)
                        ? $"\"{word}\" is a dice modifier (keep/drop, reroll, min/max or success counting), and bonus dice take none"
                        : $"\"{word}\" is not part of a dice expression");
            }

            var reason = c switch
            {
                '!' => "\"!\" makes dice explode, and bonus dice take no modifiers",
                '[' or ']' => "labels such as \"[bless]\" are not accepted on bonus dice",
                '<' or '>' or '=' => $"\"{c}\" compares, but bonus dice are only added to the d20 roll (the AC or DC is given separately)",
                '*' or '/' => $"\"{c}\" {(c == '*' ? "multiplies" : "divides")}, and bonus dice can only be added or subtracted",
                _ => null,
            };

            if (reason is not null)
            {
                throw Refused(text, where, reason);
            }
        }
    }

    /// <summary>
    /// Walks the parsed sum, counting dice and checking the per-term limits. After the scan above only these node kinds
    /// can occur; anything else means the grammar grew a construct the scan does not know, which is a bug here.
    /// </summary>
    private static void CheckTerms(DiceNode node, string text, string where, ref long dice)
    {
        switch (node)
        {
            case ConstantNode constant:
                if (constant.Value > MaxWholeNumber)
                {
                    throw Refused(text, where, $"the whole number {constant.Value.ToString(CultureInfo.InvariantCulture)} is above {MaxWholeNumber}");
                }

                break;
            case DiceGroupNode { Group: var group }:
                if (group.Keep is not null || group.Reroll is not null || group.Explode is not null || group.Min is not null ||
                    group.Max is not null || group.Success is not null || group.Label is not null)
                {
                    throw new InvalidOperationException($"\"{group.Text}\" carries a modifier the bonus dice scan did not catch.");
                }

                if (group.Sides > MaxSides)
                {
                    throw Refused(text, where, $"\"{group.Text}\" has {group.Sides.ToString(CultureInfo.InvariantCulture)} sides; the limit is {MaxSides}");
                }

                dice += group.Count;
                break;
            case NegateNode negate:
                CheckTerms(negate.Operand, text, where, ref dice);
                break;
            case GroupingNode grouping:
                CheckTerms(grouping.Inner, text, where, ref dice);
                break;
            case SumNode sum:
                CheckTerms(sum.Left, text, where, ref dice);
                CheckTerms(sum.Right, text, where, ref dice);
                break;
            default:
                throw new InvalidOperationException($"A {node.GetType().Name} got past the bonus dice scan.");
        }
    }

    private static string LetterRun(string text, int index)
    {
        var start = index;
        while (start > 0 && char.IsLetter(text[start - 1]))
        {
            start--;
        }

        var end = index;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        return text[start..end];
    }

    private static DndInputException Refused(string text, string where, string reason) =>
        new($"{where} \"{Echo(text)}\" cannot be used: {reason}. {Accepted}");

    private static string Echo(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
    }
}
