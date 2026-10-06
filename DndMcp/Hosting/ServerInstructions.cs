namespace DndMcp.Hosting;

/// <summary>
/// Sent once at the MCP handshake. Claude Code loads only tool NAMES plus these instructions at session
/// start (tool search defers the definitions), so this text is what makes the model reach for the right
/// tool at all. Claude Code truncates it at 2,048 characters — ServerSurfaceTests pins the length, and that every
/// tool is named here, as a whole word, at the head of the line or sentence that says what it is for.
///
/// <para>
/// The balance tools are named with the words a user asks with ("damage per round", "homebrew", the band scale from Under
/// to Breaking), since a request like "is this homebrew feat overtuned?" must lead the model to <c>balance_compare</c>
/// rather than to an estimate. <c>balance_simulate</c> is named with what it answers (win, defeat and death odds), what a
/// party may be made of (class archetypes, builds, monsters, sheets) and that it fights "a campaign's fight": without the
/// last two, "how would our party fare in the fight I prepared?" or "who wins from here?" in the middle of a fight reaches
/// the encounter, from_state and character forms only if the model happens to open <c>balance_simulate</c>'s description.
/// <c>rules_get</c>'s combatant format is named with what it shows, so a surprising simulation result leads the model to
/// the stat block as the simulator read it. The later-builds line promises only what does not exist yet (Phase 8's
/// markdown export and import): a promise of something already here tells the model it is missing, which is why it no
/// longer names character sheets or combat tracking.
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
/// <c>campaign_character</c> and <c>combat</c> are named with what a user says at the table: a character's HP, damage,
/// slots, rests and inventory ("Belmakor takes 14 fire damage", "we take a long rest"), and a live fight's initiative,
/// damage, conditions and death saves, with "end updates sheets" so the model knows the fight's HP reaches the sheet only
/// at its end. "damage" is on both lines on purpose: a hit to a character is always right through
/// <c>campaign_character</c>, which applies it to the live fight when the character is in one and to the sheet when no
/// fight runs, while <c>combat</c> with no fight running refuses and offers to start one, a fight nobody wanted for one
/// trap. Without these lines a model keeps HP and initiative in the conversation, where nothing tracks durations or
/// concentration and nothing reaches the campaign.
/// </para>
/// <para>
/// Seventeen tools in 2,048 characters: <c>rules_get</c>'s "Not in this server's data" sentence keeps every chapter it
/// names and the two parts of those chapters the server does have, the Character Creation chapter's advancement table
/// and the Gameplay Toolbox's encounter budget (without those exceptions "how much XP for level 5?" or a 2024
/// encounter-building question is answered "not in this server's data", though rules://tables/character-advancement,
/// <c>encounter_difficulty</c> and rules://tables/xp-budget-2024 serve them); ServerSurfaceTests pins each. It says "this
/// server's data", not "the data": the model repeats the phrase to the user ("Say so"), and "not in the data" reads as
/// "not in the SRD", which is false for the 2024 chapters (they are in SRD 5.2.1, only not served here). Every other tool
/// gets one short line, because its own description, loaded when the model picks the tool, carries the arguments and
/// the examples: <c>rules_search</c>'s kinds of entry, <c>rules_get</c>'s edition "both", <c>encounter_difficulty</c>'s
/// party "campaign" and <c>balance_simulate</c>'s argument names are left to those descriptions. The text is 2,045
/// characters, so 3 are left: Phase 8 must drop the later-builds line (63 characters with its newline) before it adds its
/// tool's line, and to fit more than that it must move the missing-chapters list into the rules tools' descriptions
/// (with the RulesScope pins that read it here).
/// </para>
/// </summary>
internal static class ServerInstructions
{
    public const string Text =
        "D&D 5e (2014 and 2024): use these tools, not guesses or mental maths. Editions default to the active campaign's " +
        "ruleset, else 2024.\n" +
        "- rules_search: find SRD text by words.\n" +
        "- rules_get: an entry by ref or name; format \"combatant\" shows a monster as the simulator reads it. 2024 rules " +
        "are the SRD 5.2.1 Rules Glossary. Not in this server's data: 2024's Playing the Game, Character Creation " +
        "(except its advancement table) and Gameplay Toolbox chapters (except its encounter budget), Spells chapter's " +
        "casting rules and Equipment chapter's prose; multiclassing rules in either edition. Say so; never search again " +
        "or quote from memory.\n" +
        "- dice_roll: any roll the user wants made, logged to a live campaign session. dice_odds: exact odds.\n" +
        "- encounter_difficulty: how hard a fight is (2014 DMG, 2024 XP budget or both). Tables: rules_get ref " +
        "\"rules://tables\".\n" +
        "- balance_dpr: a build's exact damage per round. balance_compare: judge homebrew: ΔDPR vs a baseline, " +
        "level-equivalent, band (Under to Breaking); for a verdict, baseline = the official option (a feat: the ASI it " +
        "replaces).\n" +
        "- balance_simulate: Monte Carlo fights of a party (class archetypes, builds, monsters, sheets) vs enemies, or a " +
        "campaign's fight: win, defeat and death odds.\n" +
        "- campaign: create, use, summary.\n" +
        "- campaign_search, campaign_get: find and read entries and facts; perspective \"character:<slug>\" shows only " +
        "what they know, by the names they know.\n" +
        "- campaign_write: changes as one batch of ops; dry_run first; inventions get F-codes.\n" +
        "- campaign_knowledge: record, reveal; check a draft (\"does X know this?\": names, gates, forbidden words); " +
        "ledger (who knows what).\n" +
        "- campaign_session: plan, start, end (saves the recap), record_past (a past night), recap (what was learned).\n" +
        "- campaign_history: since, as_of, undo a batch.\n" +
        "- campaign_character: sheets: HP, damage, slots, resources, conditions, rests, XP, inventory.\n" +
        "- combat: a live fight: initiative, damage, conditions, death saves, reminders; end updates sheets.\n" +
        "More tools arrive in later builds: markdown export and import.";
}
