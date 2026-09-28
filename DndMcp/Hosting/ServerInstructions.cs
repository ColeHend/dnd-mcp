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
/// estimate. <c>balance_simulate</c> is named with what it answers (win, defeat and death odds) and what a party may be
/// made of (class archetypes), and <c>rules_get</c>'s combatant format with what it shows, so a surprising simulation
/// result leads the model to the stat block as the simulator read it. The later-builds line promises only what does not
/// exist yet: a promise of something already here tells the model it is missing.
/// </para>
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "D&D 5e helper for the 2014 and 2024 rules. Use these tools instead of guessing or doing the maths by hand.\n" +
        "- rules_search: find SRD text by words (2014 SRD 5.1, 2024 SRD 5.2.1): spells, monsters, classes, feats, items, " +
        "conditions, rules and more. Returns refs such as 2024/spell/fireball.\n" +
        "- rules_get: one entry by ref or name (\"Fireball\", \"Grappled\"); edition \"both\" compares 2014 and 2024; format " +
        "\"combatant\" shows a monster as the simulator reads it. Rules default to 2024; pass edition \"2014\" for a 2014 game. " +
        "Quote rules from these tools, not from memory. The 2024 rules are the SRD 5.2.1 Rules Glossary. Not in this server's " +
        "data: the 2024 SRD's Playing the Game, Character Creation and Gameplay Toolbox chapters (but see encounter_difficulty " +
        "for its encounter budget), its Spells chapter's casting rules and Equipment chapter's prose, and multiclassing rules " +
        "in either edition. Say such a rule is not in this server's data rather than searching again or quoting from memory.\n" +
        "- dice_roll: any roll the user wants made (cryptographic, dice shown): adv, 4d6kh3, 2d6ro<=2, 1d6!, 1d20+5>=15.\n" +
        "- dice_odds: exact probabilities for the same expressions (8d6>=30, adv+5>=15). Prefer it to rolling many times.\n" +
        "- encounter_difficulty: the books' difficulty of a fight: 2014 DMG thresholds and multipliers, the 2024 XP budget, or " +
        "both. Party levels plus SRD monsters by name or ref, others by CR. The tables: rules_get ref \"rules://tables\".\n" +
        "- balance_dpr: exact damage per round of a build vs a target; one level or a curve.\n" +
        "- balance_compare: what a homebrew feature adds to a baseline build: ΔDPR and a level-equivalent with its balance band " +
        "(Under, On budget, Creeping, Over, Breaking). Use it before judging homebrew damage; for a verdict, baseline = the " +
        "official option (a feat: the ASI it replaces).\n" +
        "- balance_simulate: Monte Carlo fights of a party (class archetypes, builds, SRD monsters) vs enemies: win, defeat " +
        "and death odds with CIs, rounds, damage per combatant.\n" +
        "More tools arrive in later builds: campaign tracking.";
}
