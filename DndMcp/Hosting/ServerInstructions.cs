namespace DndMcp.Hosting;

/// <summary>
/// Sent once at the MCP handshake. Claude Code loads only tool NAMES plus these instructions at session
/// start (tool search defers the definitions), so this text is what makes the model reach for the right
/// tool at all. Claude Code truncates it at 2,048 characters — ServerSurfaceTests pins the length, and that every
/// tool is named here.
///
/// <para>
/// The balance tools are named with the words a user asks with ("damage per round", "homebrew", the band scale from Under
/// to Breaking), since a request like "is this homebrew feat overtuned?" must lead the model to <c>balance_compare</c>
/// rather than to an estimate. <c>balance_simulate</c> is named with what it answers (win, defeat and death odds) and what
/// a party may be made of (class archetypes), and <c>rules_get</c>'s combatant format with what it shows, so a surprising
/// simulation result leads the model to the stat block as the simulator read it. The later-builds line promises only what
/// does not exist yet: a promise of something already here tells the model it is missing.
/// </para>
/// <para>
/// The campaign tools are named with the jobs they do, in the words a user brings to them: "does X know this?", names,
/// gates and forbidden words (<c>campaign_knowledge</c>'s check), a view that shows only what a character knows "by the
/// names they know" (a perspective on <c>campaign_search</c> / <c>campaign_get</c>), the session steps, undo. Told only
/// "campaign tracking", a model answers "would Belmakor know the old king's name?" from the conversation, which holds the
/// author's view, true names included, and the perspective filter that exists to keep a secret out of a character's mouth
/// is never asked. The actions whose names collide with a user's words are glossed: "record what happened tonight" is
/// <c>campaign_session</c>'s end (it saves the recap), not <c>campaign_knowledge</c>'s record; "what did the party learn
/// last session?" is its recap; "who knows that?" is the knowledge ledger. <c>dice_roll</c> says it logs to a live
/// campaign session, so rolls at the table go through the tool (and into the session's record) instead of being narrated.
/// The edition default is said once, for every tool that takes an edition: the active campaign's ruleset, else 2024. A
/// model told "2024" would pass 2024 in a 2014 campaign.
/// </para>
/// <para>
/// Fifteen tools in 2,048 characters: <c>rules_get</c>'s "not in this server's data" sentences keep every chapter they
/// name and the one part of the Gameplay Toolbox the server does have, its encounter budget (without that exception a
/// 2024 encounter-building question is answered "not in this server's data", though <c>encounter_difficulty</c> and
/// rules://tables/xp-budget-2024 serve it); ServerSurfaceTests pins each. Every other tool gets one short line, because its
/// own description, loaded when the model picks the tool, carries the arguments and the examples.
/// </para>
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "D&D 5e, 2014 and 2024 rules: use these tools instead of guessing or doing maths by hand. Editions default to the " +
        "active campaign's ruleset, else 2024.\n" +
        "- rules_search: find SRD text by words: spells, monsters, classes, feats, items, conditions, rules.\n" +
        "- rules_get: one entry by ref or name; edition \"both\" compares; format \"combatant\" shows a monster as the simulator " +
        "reads it. Quote rules from these tools. The 2024 rules are the SRD 5.2.1 Rules Glossary. Not in this server's data: " +
        "the 2024 SRD's Playing the Game, Character Creation and Gameplay Toolbox chapters (except its encounter budget), its " +
        "Spells chapter's casting rules and Equipment chapter's prose, and multiclassing rules in either edition. Say such a " +
        "rule is not in this server's data rather than searching again or quoting from memory.\n" +
        "- dice_roll: any roll the user wants made, logged to a live campaign session. dice_odds: exact odds, not many rolls.\n" +
        "- encounter_difficulty: how hard a fight is (2014 DMG, 2024 XP budget or both). Tables: rules_get ref " +
        "\"rules://tables\".\n" +
        "- balance_dpr: a build's exact damage per round. balance_compare: judge homebrew: ΔDPR vs a baseline, " +
        "level-equivalent, band (Under to Breaking); for a verdict, baseline = the official option (a feat: the ASI it replaces).\n" +
        "- balance_simulate: Monte Carlo fights of a party (class archetypes, builds, monsters) vs enemies: win, defeat and " +
        "death odds.\n" +
        "- campaign: create, use, summary.\n" +
        "- campaign_search, campaign_get: find and read entries and facts; perspective \"character:<slug>\" shows only what " +
        "they know, by the names they know.\n" +
        "- campaign_write: changes as one batch of ops; dry_run first; inventions get F-codes.\n" +
        "- campaign_knowledge: record, reveal; check a draft (\"does X know this?\": names, gates, forbidden words); ledger " +
        "(who knows what).\n" +
        "- campaign_session: plan, start, end (saves the recap), record_past (a past night), recap (what was learned).\n" +
        "- campaign_history: since, as_of, undo a batch.\n" +
        "More tools arrive in later builds: character sheets, combat tracking, markdown export and import.";
}
