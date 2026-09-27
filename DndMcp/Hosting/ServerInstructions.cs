namespace DndMcp.Hosting;

/// <summary>
/// Sent once at the MCP handshake. Claude Code loads only tool NAMES plus these instructions at session
/// start (tool search defers the definitions), so this text is what makes the model reach for the right
/// tool at all. Claude Code truncates it at 2,048 characters — ServerSurfaceTests pins the length, and that every
/// tool is named here.
///
/// <para>
/// The balance tools are named with the words a user asks with ("damage per round", "homebrew", the band names), since
/// a request like "is this homebrew feat overtuned?" must lead the model to <c>balance_compare</c> rather than to an
/// estimate. The later-builds line promises only what does not exist yet: a promise of something already here tells the
/// model it is missing.
/// </para>
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "D&D 5e helper for both the 2014 and 2024 rules. Use these tools instead of guessing or doing the maths by hand.\n" +
        "- rules_search: find rules text by words in the SRD (2014 SRD 5.1, 2024 SRD 5.2.1): spells, monsters, classes, " +
        "subclasses, species, feats, backgrounds, items, conditions and rules. Returns refs such as 2024/spell/fireball.\n" +
        "- rules_get: one entry in full by ref or name (\"Fireball\", \"Grappled\"): stat block, spell, class table, rule. " +
        "edition \"both\" compares 2014 and 2024. Rules default to 2024; pass edition \"2014\" for a 2014 game. Quote rules from " +
        "these tools, not from memory. The 2024 rules are the SRD 5.2.1 Rules Glossary. Not in this server's data: the 2024 SRD's " +
        "Playing the Game, Character Creation and Gameplay Toolbox chapters (but see encounter_difficulty for its encounter " +
        "budget), its Spells chapter's casting rules and Equipment chapter's prose, and multiclassing rules in either edition (a " +
        "class shows only its multiclassing prerequisites). Say such a rule is not in this server's data rather than searching " +
        "again or quoting it from memory.\n" +
        "- dice_roll: any roll the user wants made (cryptographic, dice shown): adv, 4d6kh3, 2d6ro<=2, 1d6!, 8d6[fire], 1d20+5>=15.\n" +
        "- dice_odds: exact probabilities for the same expressions (8d6>=30, adv+5>=15). Prefer it to rolling many times.\n" +
        "- encounter_difficulty: how hard a fight is for a party: 2014 DMG thresholds and multipliers, the 2024 XP budget, or both. " +
        "Party levels plus SRD monsters by name or ref, others by CR. The tables: rules_get ref \"rules://tables\".\n" +
        "- balance_dpr: exact damage per round of a build (attacks, riders, feats, save spells) against a target, per attack; one " +
        "level or a curve; round 1, a fight or an adventuring day.\n" +
        "- balance_compare: what a homebrew feature adds to a baseline build: ΔDPR and a level-equivalent with its balance band " +
        "(Under, On budget, Creeping, Over, Breaking). Use it before judging homebrew damage.\n" +
        "More tools arrive in later builds: Monte Carlo combat simulation and campaign tracking.";
}
