namespace DndMcp.Hosting;

/// <summary>
/// Sent once at the MCP handshake. Claude Code loads only tool NAMES plus these instructions at session
/// start (tool search defers the definitions), so this text is what makes the model reach for the right
/// tool at all. Claude Code truncates it at 2,048 characters — ServerSurfaceTests pins the length.
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "D&D 5e helper for both the 2014 and 2024 rules. " +
        "Use these tools instead of guessing: dice_roll for any die roll the user wants made (cryptographically random, " +
        "every face shown). More tools arrive in later builds: rules lookup, exact dice odds, encounter difficulty, " +
        "damage-per-round and Monte Carlo balance maths for homebrew, and campaign tracking.";
}
