using System.ComponentModel;
using System.Text;
using DndMcp.Domain.Dice;
using ModelContextProtocol.Server;

namespace DndMcp.Tools;

/// <summary>
/// Dice tools. Phase 0 exposes <c>dice_roll</c> over a stub parser; Phase 1 swaps in the full grammar and
/// adds <c>dice_odds</c> without changing the tool's name or its <c>expression</c> parameter.
/// </summary>
public sealed class DiceTools
{
    private readonly IDiceRoller _roller;

    public DiceTools(IDiceRoller roller)
    {
        _roller = roller;
    }

    // Annotations are set explicitly on every tool: when they are left unset the MCP spec reads them as
    // destructive = true and openWorld = true, which is false for everything this server does locally.
    // ReadOnly and not Idempotent: a roll changes nothing, but two identical calls give different answers.
    [McpServerTool(Name = "dice_roll", Title = "Roll dice", ReadOnly = true, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Roll dice with cryptographically secure randomness and show every die face. " +
                 "Use this whenever the user asks for a roll. Accepts sums of NdM and whole numbers, " +
                 "e.g. \"1d20+5\", \"2d6+1d4+3\", \"8d6\".")]
    public string Roll(
        [Description("Dice expression, e.g. \"2d6+3\".")] string expression,
        [Description("How many times to roll the whole expression (1-100). Default 1.")] int times = 1)
    {
        if (times is < 1 or > 100)
        {
            throw new Domain.Core.DndInputException($"times must be between 1 and 100 (got {times}).");
        }

        var output = new StringBuilder();

        for (var i = 0; i < times; i++)
        {
            var result = StubDiceExpression.Roll(expression, _roller);
            var faces = string.Join(", ", result.Dice.Select(d => $"d{d.Sides}:{d.Face}"));
            var modifier = result.Modifier == 0 ? string.Empty : $" {(result.Modifier > 0 ? "+" : "-")} {Math.Abs(result.Modifier)}";

            output.Append(times > 1 ? $"#{i + 1}: " : string.Empty)
                  .Append($"**{result.Total}**  ({expression} → [{faces}]{modifier})")
                  .AppendLine();
        }

        output.Append($"_Randomness: {_roller.Source}._");
        return output.ToString();
    }
}
