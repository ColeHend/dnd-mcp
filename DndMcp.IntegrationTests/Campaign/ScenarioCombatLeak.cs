using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The leak rule (contract §0, §6.12, §7.4, D10, D20) for the exit fights' non-author TEXT: the forbidden strings per world,
/// per view and per surface, and the checks that apply them.
///
/// <para>
/// Three kinds of string, as at the Repository (X's CombatScenarioLeaks), but on the text the model reads: the world's
/// secrets (FIX §5.1/§5.2: Phase 6's lists, fixture B's Nester); the fight's AUTHOR-ONLY text (its name and outcome, the
/// stat blocks' traits and actions, the arithmetic, the reminders and their calls, the markdown sections only the author's
/// results carry, the lair, XP, loot, difficulty and settings); and the sheet's author fields. The lists are per surface,
/// because a board legitimately shows what a sheet line must not (a party row's concentration, "Mummy Lord") and a session
/// list legitimately shows a played session's title. Every fight and sheet entry occurs in what the author read during
/// the play (the tests check it), so its absence from a view is the whitelist at work, not luck.
/// </para>
/// </summary>
internal static class ScenarioCombatLeak
{
    /// <summary>
    /// The views of the Belmakor campaign that cannot see the party (orchestrator note on fixture A): every PC is "an unknown
    /// creature N" to them, with an HP word and no number, ref or name.
    /// </summary>
    public static readonly IReadOnlyList<string> BelmakorStandInViews = ["public", "character:old-king"];

    /// <summary>The words of the six living PCs' names: none may reach a view that sees them as stand-ins.</summary>
    public static readonly IReadOnlyList<string> BelmakorPcNameWords =
        ["Belmakor", "Silverwind", "Vars", "Nocturne", "Ignis", "Serif", "Torch", "Lieutenant", "Aiden", "Ironstar"];

    /// <summary>
    /// The views of the One Piece campaign that cannot see the party (the public and the seven NPCs: none of them is shown
    /// the PCs), so every PC is "an unknown creature N" to them, with an HP word and no number, ref or name. The tests check
    /// the list against the boards themselves (a view is here iff its first board shows a stand-in).
    /// </summary>
    public static readonly IReadOnlyList<string> OnePieceStandInViews =
    [
        "public", "character:the-nester", "character:protector", "character:peaceful-one", "character:mistaken-one", "character:dutiful-one",
        "character:nadar", "character:arch-mage",
    ];

    /// <summary>The words of the three PCs' names and handles: none may reach a view that sees them as stand-ins.</summary>
    public static readonly IReadOnlyList<string> OnePiecePcNameWords =
        ["Björn", "Bjorn", "Mountainfell", "fishman", "monk", "amethyst", "Dragon", "Slayer"];

    /// <summary>What a stand-in row may never carry beside its name: a ref, a number, a party row's other fields.</summary>
    public static readonly IReadOnlyList<string> StandInRowWords =
        ["character:", "e:", "HP ", "AC ", "concentrating", "death saves", "exhaustion", "temp"];

    /// <summary>The four HP words of a row without numbers (§6.12).</summary>
    public static readonly IReadOnlyList<string> HpWords = ["unhurt", "hurt", "bloodied", "down"];

    /// <summary>
    /// Fixture A's author-only fight text: the fight's name and outcome, the calls and arithmetic, the stat blocks' traits and
    /// actions, the legendary bookkeeping, the damage types, the DCs, the write-back and the author results' sections.
    /// </summary>
    public static readonly IReadOnlyList<string> BelmakorFight =
    [
        "crypt", "fixture", "cleared", "combat {", "combat end", "campaign_", "→", "Rejuvenation", "Rotting Fist", "heart is intact", "legendary",
        "vulnerable", "necrotic", "bludgeoning", "radiant", "piercing", "DC 1", "## ", "Batch", "undo", "session", "2014 rules", "given", "remaining_rounds",
        "Written back", "Reminders", "What changed",
    ];

    /// <summary>Fixture A's sheet secrets (A-L4, §7.4): the sheet's author fields, its resources, spells and notes, and what the write-back put there.</summary>
    public static readonly IReadOnlyList<string> BelmakorSheet =
    [
        "Cole", "Contingency", "Bladesong", "Arcane Recovery", "Polymorph", "Circle of Power", "War Caster", "Tough", "Noble", "Elvish", "Scimitar",
        "Hold Monster", "Wall of Force", "Forcecage", "T-rex", "fixture", "Spell slots", "spell_slots", "player", "Resources", "Concentration",
        "concentrating", "Abilities", "Saves", "Hit Dice", "XP", "Coins", "sim_profile", "Feats", "Languages", "crypt", "remaining_rounds", "round 1",
    ];

    /// <summary>What no session read of the Belmakor campaign may show after fixture A (D10: combat appends nothing to a session; A-L6: no rolls were made).</summary>
    public static readonly IReadOnlyList<string> BelmakorSession =
        ["crypt", "fixture", "cleared", "Rejuvenation", "Mummy", "Circle of Power", "Bladesong", "remaining_rounds", "combat", "## Dice", "## Author", "What changed"];

    /// <summary>What no Phase 6 reader (search, get with every include, sessions, the summary, the knowledge resource) may show a Belmakor view after fixture A.</summary>
    public static readonly IReadOnlyList<string> BelmakorReach =
        ["crypt", "cleared", "combat end", "Rejuvenation", "Circle of Power", "remaining_rounds", "Bladesong", "Mummy", "sheet_source", "fixture"];

    /// <summary>What no Phase 6 reader may show a One Piece view after fixture B.</summary>
    public static readonly IReadOnlyList<string> OnePieceReach =
        ["dark station", "sixth station", "Eldritch", "Aboleth", "ledger", "Water Breathing", "Potion of Healing", "sheet_source", "fixture"];

    /// <summary>The world's secrets for one Belmakor view: Phase 6's list (the ambition too, but for Belmakor and the dm).</summary>
    public static IReadOnlyList<string> Belmakor(string view) => ScenarioLeak.ForbiddenFor(view);

    /// <summary>
    /// The One Piece player views' secrets: Phase 6's <see cref="ScenarioOnePieceLeakTests.Forbidden"/> verbatim (its
    /// markdown section words are checked on host text here, where they can occur) and fixture B's Nester.
    /// </summary>
    public static readonly IReadOnlyList<string> OnePiecePlayer = [.. ScenarioOnePieceLeakTests.Forbidden];

    /// <summary>Phase 6's baseline for the One Piece NPC views: the answer words none of them knows.</summary>
    public static readonly IReadOnlyList<string> OnePieceNpc = ["rope en masse", "founders", "fleet"];

    /// <summary>Fixture B's NPC, its handle and every distinctive word of its secret: nobody knows it, so no non-author view may meet any of it.</summary>
    public static readonly IReadOnlyList<string> Nester = ["Nester", "the-nester", "unmourned", "Void-touched", "drowned", "Wraith"];

    /// <summary>
    /// Fixture B's author-only fight text, B-L6's difficulty words among them (contract §15 X2: swept on host text, where
    /// encounter_difficulty prints them): the fight's name, the loot and coins, the proposal, the stat block's actions and
    /// traits, the legendary bookkeeping, the lair, the XP, the arithmetic, the calls and the settings.
    /// </summary>
    public static readonly IReadOnlyList<string> OnePieceFight =
    [
        "dark station", "sixth station", "fixture", "Eldritch", "Restoration", "Proposed", "campaign_", "Lash", "Psychic Drain", "Consume Memories",
        "Dominate Mind", "lair", "7,200", "2,400", "Awarded", "legendary", "resistant", "DC 14", "escape", "until removed",
        "2024/monster", "combat {", "→", "ledger", "Water Breathing", "Potion", "120 gp", "given", "## ", "Batch", "session", "2024 rules", "Bloodied (", "/150",
    ];

    /// <summary>
    /// B-L6 on host text (contract §15 X2: X could not): the fight's difficulty as encounter_difficulty prints it for the
    /// author, and the campaign's level offset. Swept on every board, sheet and session read of fixture B.
    /// </summary>
    public static readonly IReadOnlyList<string> OnePieceDifficulty =
        ["Moderate", "budget", "Beyond High", "difficulty", "5,900", "adjusted XP", "effective level", "effective_level_offset", "offset"];

    /// <summary>Fixture B's sheet secrets (§7.4, D20): resources, inventory, coins, XP, the sim_profile, the write-back's notes and sources.</summary>
    public static readonly IReadOnlyList<string> OnePieceSheet =
    [
        "Rage", "Focus", "Potion", "34000", "34,000", "36400", "36,400", "XP", "Amethyst rider", "Draconic Strike", "fixture", "dark station", "round 1",
        "until_removed", "until removed", "Aboleth", "Inventory", "Coins", "Resources", "Hit Dice", "sim_profile", "ledger", "Water Breathing",
    ];

    /// <summary>What no session read of the One Piece campaign may show after fixture B: the fight, its loot, and the DM's rolls.</summary>
    public static readonly IReadOnlyList<string> OnePieceSession =
        ["dark station", "sixth station", "fixture", "Aboleth", "ledger", "Water Breathing", "1d10", "4d6+5", "(secret)", "a character", "## Author", "What changed"];

    /// <summary>The world's secrets for one One Piece view: FIX §5.2 for the players, Phase 6's NPC baseline for the NPCs; the Nester for all.</summary>
    public static IReadOnlyList<string> OnePiece(string view) =>
        ScenarioCombatOnePiecePlay.PlayerViews.Contains(view) ? [.. OnePiecePlayer, .. Nester] : [.. OnePieceNpc, .. Nester];

    /// <summary>
    /// Asserts a refusal carries none of <paramref name="forbidden"/> once the caller's own typed text is taken out: the
    /// tool-level form of DndMcp.Tests' <c>LeakAssert.CleanMessage</c> (the integration project cannot reference it), one
    /// step stricter: only the text EXACTLY as typed is removed (its letter case too), never a word of it elsewhere and
    /// never another casing of it. A caller who typed "keras" may read "keras" echoed back; a refusal that answered with
    /// "Keras" would be the server's own spelling of the true name, and the check (any letter case) catches it.
    /// </summary>
    public static void CleanMessage(string message, string typed, IEnumerable<string> forbidden, string because)
    {
        var stripped = typed.Length == 0 ? message : message.Replace(typed, string.Empty, StringComparison.Ordinal);
        foreach (var word in forbidden)
        {
            Assert.True(stripped.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0, $"{because}: the message contains \"{word}\": {message}");
        }
    }

    /// <summary>
    /// A read's check for <paramref name="view"/>: the view's own perspective is what the caller typed (the banner and a
    /// refusal's suggested call echo it), so it is taken out first, exactly as typed, as a refusal's echo of the handle
    /// the caller asked for is; then a refusal as <see cref="CleanMessage"/>, a page as <see cref="ScenarioLeak.AssertClean"/>.
    /// </summary>
    public static void Clean(ScenarioCombatRead read, string view, IEnumerable<string> forbidden, string because)
    {
        var text = WithoutView(read.Text, view);
        if (read.Refused)
        {
            Assert.StartsWith("An error occurred invoking '", read.Text, StringComparison.Ordinal);
            CleanMessage(text, read.Typed, forbidden, $"{because}: the refusal of {read.Label}");
        }
        else
        {
            ScenarioLeak.AssertClean(text, forbidden, $"{because}: {read.Label}");
        }
    }

    /// <summary>A board's or page's text with the perspective the caller typed taken out (its banner and its echoes).</summary>
    public static string WithoutView(string text, string view) =>
        view.StartsWith("character:", StringComparison.Ordinal) ? text.Replace(view, "<view>", StringComparison.Ordinal) : text;

    /// <summary>
    /// Asserts every entry of <paramref name="lists"/> occurs somewhere in <paramref name="held"/> (any letter case): a
    /// forbidden string nothing could ever contain proves nothing in a sweep.
    /// </summary>
    public static void AssertHeld(string held, IEnumerable<string> lists, string where)
    {
        foreach (var word in lists)
        {
            Assert.True(held.Contains(word, StringComparison.OrdinalIgnoreCase), $"\"{word}\" is held nowhere in {where}, so no sweep can find it");
        }
    }

    /// <summary>
    /// A board's whitelist shape (§6.12): its heading, the banner, rows numbered 1..k, and nothing after the table; and per
    /// row, by <paramref name="numbers"/> (one flag per row, in order: may this row carry numbers?), either a party row's
    /// numbers ("HP …") or exactly one HP word ("unhurt", "hurt", "bloodied", "down") and never "HP …". Numbers belong
    /// to a party-side row the view is shown, nothing else: an enemy, an ally, or a PC the view only sees as a stand-in
    /// gets the word.
    /// </summary>
    public static void AssertBoardShape(string board, string heading, string view, IReadOnlyList<bool> numbers, string because)
    {
        Assert.StartsWith($"{heading}\n\n_Perspective: {view}", board, StringComparison.Ordinal);
        var rows = ScenarioCombatText.Board(board);
        Assert.Equal(Enumerable.Range(1, rows.Count), rows.Select(r => r.Number));
        Assert.True(numbers.Count == rows.Count, $"{because}: {rows.Count} rows, {numbers.Count} expected");
        foreach (var (row, withNumbers) in rows.Zip(numbers))
        {
            Assert.True(
                withNumbers ? row.Status.StartsWith("HP ", StringComparison.Ordinal) : HpWords.Contains(row.Status),
                $"{because}: row {row.Number} ({row.Name}) has the status \"{row.Status}\", but {(withNumbers ? "a party row the view is shown carries its numbers" : "this row may carry one HP word only")}");
        }

        Assert.EndsWith(rows.Count == 0 ? "\n" : " |\n", board, StringComparison.Ordinal);
    }

    /// <summary>
    /// The numbers flags of <see cref="AssertBoardShape"/> for a board whose rows are the author's table rows one for one
    /// (no combatant hidden or left): a party-side row carries numbers unless the view sees the party as stand-ins.
    /// </summary>
    public static IReadOnlyList<bool> Numbers(IReadOnlyList<ScenarioCombatText.TableRow> table, bool standIns) =>
        [.. table.Select(r => r.Side == "party" && !standIns)];
}
