namespace DndMcp.Hosting;

/// <summary>
/// Sent once at the MCP handshake. Claude Code loads only tool NAMES plus these instructions at session
/// start (tool search defers the definitions), so this text is what makes the model reach for the right
/// tool at all. Claude Code truncates it at 2,048 characters — ServerSurfaceTests pins the length.
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "D&D 5e helper for both the 2014 and 2024 rules. Use these tools instead of guessing or doing the maths by hand.\n" +
        "- dice_roll: any die roll the user wants made. Cryptographically random, with the dice shown. Handles advantage " +
        "(adv), keep/drop (4d6kh3), rerolls (2d6ro<=2), exploding dice (1d6!), min/max (1d20min10), success counting " +
        "(10d10cs>=8), labels (8d6[fire]) and pass/fail checks (1d20+5>=15).\n" +
        "- dice_odds: exact probabilities for the same expressions: the chance that 8d6 >= 30, the average of 4d6 drop " +
        "lowest, the odds to hit AC 15 with advantage and +5 (adv+5>=15). Prefer it to rolling many times.\n" +
        "More tools arrive in later builds: rules lookup, encounter difficulty, damage-per-round and Monte Carlo balance " +
        "maths for homebrew, and campaign tracking.";
}
