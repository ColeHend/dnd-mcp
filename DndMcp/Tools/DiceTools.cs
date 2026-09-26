using System.ComponentModel;
using DndMcp.Domain.Core;
using DndMcp.Domain.Dice;
using DndMcp.Formatting;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// <c>dice_roll</c> and <c>dice_odds</c>: one grammar (<see cref="DiceExpression"/>), two questions — "roll it"
/// and "how likely is it". Thin by design: validate the arguments, call Domain, render markdown.
/// </summary>
public sealed class DiceTools
{
    public const int MaxTimes = 100;

    /// <summary>The roll's own label (the <c>label</c> argument), not a term label like 2d6[fire].</summary>
    public const int MaxRollLabelLength = 100;

    private readonly IDiceRoller _roller;

    public DiceTools(IDiceRoller roller)
    {
        _roller = roller;
    }

    // Annotations are set explicitly on every tool: when they are left unset the MCP spec reads them as
    // destructive = true and openWorld = true, which is false for everything this server does locally.
    // ReadOnly and not Idempotent: a roll changes nothing, but two identical calls give different answers.
    [McpServerTool(Name = "dice_roll", Title = "Roll dice", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Roll dice for the user with cryptographically secure randomness. The result shows the dice, including ones dropped, " +
        "rerolled or exploded, so it can be checked (very large rolls list the first dice and a subtotal). Use it for any roll " +
        "the user wants made; never make up results. " +
        "For probabilities use dice_odds rather than rolling many times.\n" +
        "Syntax: NdM (d20, 4d6, d% = d100), + and - between terms, * or / by a whole number (/ rounds down), parentheses. " +
        "Shorthands: adv = 2d20kh1, dis = 2d20kl1, ea = 3d20kh1 (Elven Accuracy).\n" +
        "Modifiers go straight after the dice, no space:\n" +
        "- kh3 / kl1 keep highest / lowest (k3 = kh3); dh1 / dl1 drop highest / lowest\n" +
        "- r1 or r<3 reroll until it no longer matches; ro<=2 reroll once\n" +
        "- ! explode on the highest face (!>=5 on 5 or more), !! compounding, !p penetrating\n" +
        "- min10 / max5 raise or lower each die\n" +
        "- cs>=8 count successes instead of summing; cf=1 also subtracts failures\n" +
        "Label a term with [text]: 8d6[fire]+2d6[radiant]. A trailing comparison checks the total: 1d20+7>=15 answers yes or no.\n" +
        "Examples: 4d6dl1 (ability score), adv+7, 2d6ro<=2+5 (2014 Great Weapon Fighting), 1d20min10+9 (Reliable Talent), " +
        "10d10cs>=8, 1d6!.\n" +
        "Limits: 1000 dice and 1000 sides per expression, 100 rolls per call.")]
    public string Roll(
        [Description("Dice expression, e.g. \"4d6kh3\", \"adv+5\", \"8d6[fire]\" or \"1d20+7>=15\".")] string expression,
        [Description("How many times to roll the whole expression (1-100). Default 1.")] int times = 1,
        [Description("Optional name shown with the result, e.g. \"Stealth\" or \"Fireball damage\".")] string? label = null,
        [Description("Optional whole-number seed for reproducible pseudo-random rolls (tests, replays). Omit for real, cryptographically random rolls.")] long? seed = null)
    {
        if (times is < 1 or > MaxTimes)
        {
            throw new DndInputException($"times must be between 1 and {MaxTimes} (got {times}).");
        }

        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (label is not null && (label.Length > MaxRollLabelLength || label.Any(char.IsControl)))
        {
            throw new DndInputException($"label must be one line of at most {MaxRollLabelLength} characters, e.g. \"Stealth\".");
        }

        var parsed = DiceExpression.Parse(expression);
        IDiceRoller roller = seed is { } s ? new SeededDiceRoller(s) : _roller;

        // One budget for the whole call, so times = 100 cannot multiply a pathological expression's cost.
        var budget = new DiceRollBudget();
        var rolls = new List<DiceRoll>(times);
        for (var i = 0; i < times; i++)
        {
            rolls.Add(DiceEvaluator.Roll(parsed, roller, budget));
        }

        return DiceRollMarkdown.Format(parsed, rolls, label, roller.Source);
    }

    // Idempotent: exact answers are pure functions of the expression, and the Monte Carlo fallback uses a fixed seed.
    [McpServerTool(Name = "dice_odds", Title = "Dice odds", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Exact probabilities for a dice expression, computed rather than rolled. Use it for \"what are the odds…\", \"how likely…\", " +
        "\"what's the average…\" questions instead of estimating.\n" +
        "Same syntax as dice_roll: keep/drop, rerolls, exploding dice, min/max, success counting, adv/dis/ea.\n" +
        "- With a trailing comparison it answers that question: 8d6>=30 gives P(total ≥ 30); 1d20+5>=15 is the chance to hit " +
        "AC 15 with +5; adv+5>=15 the same with advantage. The exact fraction is shown when it is short.\n" +
        "- Without one it gives the mean, standard deviation, range, percentiles and, when there are at most 60 possible totals, " +
        "a table of P(=), P(≥) and P(≤).\n" +
        "Examples: 4d6kh3, 2d20kl1+3>=12, 8d6>=30, 10d10cs>=8>=3 (at least 3 successes), 1d6!.\n" +
        "It is dice arithmetic only: table rules such as a natural 20 always hitting are not applied.\n" +
        "Very large pools, and ! or !p exploding dice combined with keep/drop, are estimated by seeded Monte Carlo instead; " +
        "the result says so and gives 95% intervals.")]
    public string Odds(
        [Description("Dice expression, optionally ending in a comparison: \"8d6>=30\", \"4d6kh3\", \"adv+5>=15\".")] string expression,
        CancellationToken cancellationToken)
    {
        var parsed = DiceExpression.Parse(expression);
        var odds = DiceDistribution.Compute(parsed, cancellationToken: cancellationToken);
        return DiceOddsMarkdown.Format(odds);
    }
}
