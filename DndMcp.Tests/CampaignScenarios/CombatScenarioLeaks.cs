namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The forbidden strings of the exit-criteria fights' leak sweeps (FIX §5 as amended by contract §16), per world and per
/// surface. A sweep serialises the WHOLE model a non-author read returned (<see cref="CampaignRead.LeakAssert"/>: every
/// public property at every depth) and asserts none of these occurs, in any casing. Three kinds of string:
/// <list type="bullet">
/// <item>the world's secrets (FIX §5.1/§5.2: Phase 6's lists plus fixture B's Nester): never in any non-author output;</item>
/// <item>the fight's AUTHOR-ONLY text (contract §6.12: the board never prints the encounter's name, outcome, lair,
/// write-back, XP, loot, reminders, arithmetic, calls or stat-block internals; D10: combat writes nothing a session shows
/// a player; §7.4: a sheet line never prints concentration, resources or notes): typed by the author or the fixtures,
/// so its absence proves the whitelist, not luck;</item>
/// <item>the sheet's author fields (A-L4, §7.4) on the sheet surfaces.</item>
/// </list>
/// The lists are per surface because a board legitimately shows what a sheet line must not (a party row's concentration
/// spell, "Mummy Lord"), and the Phase 6 readers legitimately show words the fight also uses ("Contingency" is a played,
/// party-visible fact of the Belmakor world).
/// </summary>
internal static class CombatScenarioLeaks
{
    /// <summary>FIX §5.1's strings for one non-author view of the Belmakor campaign (Phase 6's list, the import words, the item's true name).</summary>
    public static IReadOnlyList<string> Belmakor(string view) =>
        [.. BelmakorScenario.ForbiddenFor(view), "One Piece", "The thing the old king wants", "item:thing-he-wants"];

    /// <summary>
    /// Fixture A's author-only fight text: the encounter's name and outcome, the write-back's reason and tool, the stat
    /// blocks' traits and actions, the legendary bookkeeping, the damage arithmetic, the calls of the reminders, and the
    /// author state's own records (a combatant's sheet snapshot and stat block, by the property names a nested author
    /// record would carry into a serialised model). Every entry occurs in what the store or the author holds for the fight
    /// (<c>CombatBelmakorLeakScenarioTests</c> checks it), so its absence from a view is the whitelist at work, not luck.
    /// </summary>
    public static readonly IReadOnlyList<string> BelmakorFight =
    [
        "crypt", "cleared", "combat end", "combat/end", "Rejuvenation", "Rotting Fist", "heart is intact", "legendary", "vulnerable",
        "necrotic", "bludgeoning", "DC 1", "combat {", "→", "remaining_rounds", "SheetSnapshot", "StatBlock", "2014/monster", "given",
    ];

    /// <summary>
    /// Fixture A's sheet secrets (C's sweep list, A-L4) plus what the write-back put on Belmakor's sheet that only the author
    /// may read: the concentration (its spell and the "from &lt;encounter&gt;, round 1" note), the fight's name, and the
    /// sheet's notes and the Contingency's note (the spells he plans, the T-rex).
    /// </summary>
    public static readonly IReadOnlyList<string> BelmakorSheet =
    [
        "Cole", "Contingency", "Bladesong", "Arcane Recovery", "Polymorph", "Circle of Power", "War Caster", "Tough", "Noble", "Elvish",
        "Scimitar", "Hold Monster", "fixture", "SpellSlots", "spell_slots", "Lineage", "Wall of Force", "Forcecage", "T-rex", "crypt",
        "remaining_rounds", "round 1",
    ];

    /// <summary>
    /// What no Phase 6 reader (campaign_get with every include, search, the knowledge resource, sessions, the summary) may
    /// show any non-author view of the Belmakor campaign after fixture A: none of it is in the Phase 6 world, so any of it
    /// there came from the fight or the write-back.
    /// </summary>
    public static readonly IReadOnlyList<string> BelmakorReach =
        ["crypt", "cleared", "combat end", "Rejuvenation", "Circle of Power", "remaining_rounds", "Bladesong", "Mummy", "sheet_source", "fixture"];

    /// <summary>
    /// FIX §5.2's strings for One Piece's player views (Phase 6's ScenarioOnePieceLeakTests.Forbidden, verbatim: its
    /// markdown section words are the host's, kept so the list stays Phase 6's baseline) plus fixture B's NPC (its name and
    /// secret). The NPC perspectives may know some of the Phase 6 words (the Protector its own name), so they get
    /// <see cref="OnePieceNpcBaseline"/> and <see cref="OnePieceNester"/> instead.
    /// </summary>
    public static readonly IReadOnlyList<string> OnePiece =
    [
        "[!secret]", "## Author", "## History", "What changed", "simulacrum", "founders", "fleet", "en masse", "seal", "Baal", "Keras", "Cage",
        "fragment", "Protector", "Peaceful", "Mistaken", "Dutiful", "lineage", "Sky-world", "return visit", "effective_level_offset",
        "status withheld", "Nester", "unmourned", "Void-touched",
    ];

    /// <summary>
    /// Phase 6's baseline for the One Piece NPC views (LeakPropertyScenarioTests.ForbiddenFor outside the party): the
    /// answer words none of the Mistaken One, Nadar, the Protector or the arch mage knows.
    /// </summary>
    public static readonly IReadOnlyList<string> OnePieceNpcBaseline = ["rope en masse", "founders", "fleet"];

    /// <summary>Fixture B's NPC: its name, its handle and its secret. Nobody knows it, so no non-author view may meet any of it.</summary>
    public static readonly IReadOnlyList<string> OnePieceNester = ["Nester", "the-nester", "unmourned", "Void-touched", "drowned"];

    /// <summary>
    /// Fixture B's author-only fight text: the encounter's name and outcome, the lair and its XP and awards, the proposal,
    /// the stat block's actions and traits, the legendary bookkeeping, the arithmetic, the calls, the loot and the coins
    /// (D20), and the settings. Every entry occurs in what the store or the author holds for the fight
    /// (<c>CombatOnePieceLeakScenarioTests</c> checks it); B-L6's difficulty words ("Moderate", the budget) exist only in
    /// the host's encounter_difficulty text, which X2 sweeps.
    /// </summary>
    public static readonly IReadOnlyList<string> OnePieceFight =
    [
        "dark station", "sixth station", "Eldritch", "Restoration", "Proposed", "campaign_write", "Lash", "Psychic Drain", "Consume Memories",
        "Tentacle", "Multiattack", "Dominate Mind", "Probing Telepathy", "lair", "7,200", "2,400", "Awarded", "legendary", "resistant", "DC 14",
        "escape", "until removed", "2024/monster", "combat {", "→", "ledger", "Water Breathing", "Potion", "120 gp", "given",
        "effective_level_offset", "offset",
    ];

    /// <summary>Fixture B's sheet secrets (C's sweep list) beyond the world's: the resources, the inventory, the XP, the sim_profile.</summary>
    public static readonly IReadOnlyList<string> OnePieceSheet =
        ["Rage", "Focus", "Potion", "34000", "36400", "Amethyst rider", "Draconic Strike", "fixture", "dark station", "round 1", "until_removed"];

    /// <summary>What no Phase 6 reader may show a One Piece non-author view after fixture B (none of it is in the Phase 6 world).</summary>
    public static readonly IReadOnlyList<string> OnePieceReach =
        ["dark station", "sixth station", "Eldritch", "Aboleth", "ledger", "Water Breathing", "Potion of Healing", "sheet_source", "fixture"];
}
