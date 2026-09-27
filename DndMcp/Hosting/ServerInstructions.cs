namespace DndMcp.Hosting;

/// <summary>
/// Sent once at the MCP handshake. Claude Code loads only tool NAMES plus these instructions at session
/// start (tool search defers the definitions), so this text is what makes the model reach for the right
/// tool at all. Claude Code truncates it at 2,048 characters — ServerSurfaceTests pins the length, and that every
/// tool is named here.
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "D&D 5e helper for both the 2014 and 2024 rules. Use these tools instead of guessing or doing the maths by hand.\n" +
        "- rules_search: find rules text by words in the SRD (2014 SRD 5.1, 2024 SRD 5.2.1): spells, monsters, classes, " +
        "subclasses, species, feats, backgrounds, items, conditions and rules. Returns refs such as 2024/spell/fireball.\n" +
        "- rules_get: one entry in full by ref or by name (\"Fireball\", \"Adult Red Dragon\", \"Grappled\"): stat block, spell, " +
        "class level table, rule text. edition \"both\" compares 2014 and 2024 side by side. Rules default to 2024; pass " +
        "edition \"2014\" for a 2014 game. Quote rules from these tools, not from memory. The 2024 rules are the SRD 5.2.1 " +
        "Rules Glossary. Not in this server's data: the 2024 SRD's Playing the Game, Character Creation and Gameplay Toolbox " +
        "chapters (but see encounter_difficulty for its encounter budget), its Spells chapter's casting rules and Equipment chapter's prose, and multiclassing rules in either edition " +
        "(a class shows only its multiclassing prerequisites). Say such a rule is not in this server's data rather than " +
        "searching again or quoting it from memory.\n" +
        "- dice_roll: any die roll the user wants made. Cryptographically random, with the dice shown. Handles advantage " +
        "(adv), keep/drop (4d6kh3), rerolls (2d6ro<=2), exploding dice (1d6!), min/max (1d20min10), success counting " +
        "(10d10cs>=8), labels (8d6[fire]) and pass/fail checks (1d20+5>=15).\n" +
        "- dice_odds: exact probabilities for the same expressions: the chance that 8d6 >= 30, the average of 4d6 drop " +
        "lowest, the odds to hit AC 15 with advantage and +5 (adv+5>=15). Prefer it to rolling many times.\n" +
        "- encounter_difficulty: how hard a fight is for a party: 2014 DMG thresholds and multipliers, the 2024 XP budget, " +
        "or both. Party levels plus SRD monsters by name or ref, or any other monster by CR; also the XP earned. The tables " +
        "themselves: rules_get ref \"rules://tables\".\n" +
        "More tools arrive in later builds: damage-per-round and Monte Carlo balance maths for homebrew, and campaign " +
        "tracking.";
}
