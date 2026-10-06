using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// The leak probes of the exit fights through the MCP tools (FIX §5 as amended by contract §16; §15 X2's list), each in a
/// world of its own because each writes: A-L1/A-L2/A-L3 (the old king linked is "The Old King", a lich typed "Keras" is
/// "Lich" to every view and every dice label); A-L5 (the ambition typed into an effect, a spell, an outcome and a reason
/// reaches only the two views that know it); the Nester's lair probe (a fight named for the Nester, an Aboleth typed "The
/// Nester", an effect "sealed by the Nester" that rides the write-back onto a sheet, a hidden turn-holder, the hidden first
/// Mummy's numbering, a planned fight asked for by name); a combatant that leaves after initiative and the review's
/// literal hidden-third-Mummy attack, in every view; P1-P3 / B-L7 / B-L1 (the disguised Protector is the advisor in
/// Serret, the Wraith Blades unknown creatures, and their rolls, the custom enemy's among them, secret); a visible NPC with
/// a sheet and a party member's "Void-touched" subclass (§7.4); long tracker names whose printed calls work verbatim in
/// a fight too big for one result, whose table-cut note sits before the reminders; and the damage call a legendary action
/// prints, whose roll is the Aboleth's and secret, never the PC's whose turn it is (review LR03).
/// What breaks if these fail: a name, a roll or an effect the author typed shows a player what only the author knows.
/// </summary>
public sealed class ScenarioCombatProbeTests
{
    private static string Board(string view, string? encounter = null) =>
        encounter is null
            ? $$"""{"action": "state", "perspective": "{{view}}"}"""
            : $$"""{"action": "state", "perspective": "{{view}}", "encounter": {{JsonSerializer.Serialize(encounter)}}}""";

    private static async Task<Dictionary<string, string>> BoardsAsync(ScenarioCombatServer world, IEnumerable<string> views, string? encounter = null)
    {
        var boards = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var view in views)
        {
            boards[view] = await world.Combat(Board(view, encounter));
        }

        return boards;
    }

    private static void AssertCampaignLast(IEnumerable<string> texts, string campaign)
    {
        foreach (var call in texts.SelectMany(ScenarioCombatText.PrintedCalls))
        {
            Assert.True(call.EndsWith($", \"campaign\": \"{campaign}\"}}", StringComparison.Ordinal), call);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Fixture A's probes

    [Fact]
    public async Task Belmakor_AL1_AL2_AL3_TheOldKingLinkedIsTheOldKing_ALichTypedKerasIsLich_ToEveryViewAndEveryDiceLabel()
    {
        await using var world = await ScenarioCombatServer.BelmakorAsync();
        await world.Call("campaign_session", ScenarioCombatFixtures.SessionA);
        await world.Combat("""{"action": "start", "campaign": "belmakor", "name": "Old king rematch (probe)", "add_party": false}""");

        var linked = await world.Combat("""{"action": "add", "combatants": [{"character": "character:old-king", "srd": "2014/monster/lich", "hp": "avg"}]}""");
        var typed = await world.Combat("""{"action": "add", "combatants": [{"srd": "2014/monster/lich", "name": "Keras"}]}""");
        world.Dice.Enqueue(11, 6);
        var initiative = await world.Combat("""{"action": "initiative"}""");
        world.Dice.Enqueue(3);
        await world.Combat("""{"action": "damage", "targets": ["keras"], "dice": "1d6", "damage_type": "necrotic", "source": "character:old-king"}""");
        world.Dice.Enqueue(4);
        await world.Combat("""{"action": "damage", "targets": ["character:old-king"], "dice": "1d6", "damage_type": "cold", "source": "keras"}""");

        // A-L1: linked through its handle, the tracker names the stat block (the author view adds the handle); no warning.
        Assert.Equal(["Lich (enemy): 135/135 HP, AC 17, initiative +3, legendary actions 3/3, Legendary Resistance 3/3."], ScenarioCombatText.Section(linked, "What changed"));
        Assert.Empty(ScenarioCombatText.Section(linked, "Warnings"));
        Assert.Contains("| Lich (character:old-king) | enemy | 135/135 | 17 |", linked, StringComparison.Ordinal);

        // A-L2: a typed name is never linked by its text; the author is told what the party sees instead. In a player
        // campaign an srd monster's HP is unknown (D17).
        Assert.Equal(["Keras (enemy): HP not tracked, AC 17, initiative +3, legendary actions 3/3, Legendary Resistance 3/3."], ScenarioCombatText.Section(typed, "What changed"));
        var warning = Assert.Single(ScenarioCombatText.Section(typed, "Warnings"));
        Assert.StartsWith("'Keras' is a name the party does not use (character:old-king", warning, StringComparison.Ordinal);
        Assert.EndsWith("): party views show this combatant as 'Lich'; add it with character:old-king to show the name the party knows.", warning, StringComparison.Ordinal);

        // A-L3: the server's rolls are labelled by the view-safe names, open in a player campaign (the party knows the old
        // king), with the player campaign's note on an enemy's initiative.
        Assert.Equal(
            ["initiative: `1d20+3` [11] = 14 · logged as \"The Old King: initiative\"", "initiative: `1d20+3` [6] = 9 · logged as \"Lich: initiative\""],
            ScenarioCombatText.Section(initiative, "Rolls"));
        Assert.Contains("- Lich: initiative 14 (rolled 1d20+3: 11) — rolled here; give the DM's order as totals.\n", initiative, StringComparison.Ordinal);
        Assert.Equal(
            [("The Old King: initiative", false), ("Lich: initiative", false), ("The Old King: damage", false), ("Lich: damage", false)],
            world.Store.Dice("belmakor").Select(d => (d.Label!, d.Secret)));
        Assert.All(world.Store.Dice("belmakor"), d => Assert.Equal(4L, d.SessionNumber));
        Assert.Equal([(20, 11), (20, 6), (6, 3), (6, 4)], world.Dice.Rolled);

        var boards = await BoardsAsync(world, ScenarioCombatBelmakorPlay.NonAuthorViews);
        foreach (var (view, board) in boards)
        {
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(board, view), [.. ScenarioCombatLeak.Belmakor(view), "(probe)", "rematch"], $"the {view} board of the probe");
            var rows = ScenarioCombatText.Board(board);
            Assert.Contains(rows, r => r.Name.StartsWith("Lich", StringComparison.Ordinal) && r.Ref is null);
            // Who never met the old king (the public, he himself, Tristan who died before) sees two liches; the rest the old king.
            Assert.True(
                (view is "public" or "character:old-king" or "character:tristan"
                    ? [("Lich", null), ("Lich 2", null)]
                    : new[] { ("The Old King", "character:old-king"), ("Lich", (string?)null) })
                .SequenceEqual(rows.Select(r => (r.Name, r.Ref))), $"{view}: {string.Join(", ", rows.Select(r => (r.Name, r.Ref)))}");
            Assert.All(rows, r => Assert.Equal("hurt", r.Status));

            var (session, refused) = await world.Read("campaign_session", $$"""{"action": "get", "campaign": "belmakor", "session": 4, "perspective": "{{view}}"}""");
            ScenarioCombatLeak.Clean(new ScenarioCombatRead(ScenarioCombatRead.Session, "session 4", session, refused, "4"), view, ScenarioCombatLeak.Belmakor(view), view);
            if (!refused)
            {
                Assert.Equal(["The Old King: initiative", "Lich: initiative", "The Old King: damage", "Lich: damage"],
                    Regex.Matches(session, @"^\| [0-9T:.Z-]+ \| 1d[0-9+]+ \| ([^|]+) \| \d+ \|$", RegexOptions.Multiline).Select(m => m.Groups[1].Value));
            }
        }

        AssertCampaignLast([linked, typed, initiative], "belmakor");
    }

    [Fact]
    public async Task Belmakor_AL5_TheAmbitionTypedIntoAnEffectASpellAnOutcomeAndAReason_ReachesOnlyTheViewsThatKnowIt()
    {
        await using var world = await ScenarioCombatServer.BelmakorAsync();
        await world.Call("campaign_session", ScenarioCombatFixtures.SessionA);
        await world.Combat("""
            {"action": "start", "campaign": "belmakor", "name": "Reclaim the blighted surface (probe)",
             "combatants": [{"character": "character:old-king", "srd": "2014/monster/lich", "hp": "avg"}]}
            """);
        await world.Combat("""{"action": "condition", "targets": ["character:old-king"], "add": ["reclaim the blighted surface"], "source": "belmakor"}""");
        await world.Combat("""{"action": "concentration", "targets": ["belmakor"], "spell": "Outstrips Ward"}""");
        var during = await BoardsAsync(world, ScenarioCombatBelmakorPlay.NonAuthorViews);
        var end = await world.Combat("""{"action": "end", "outcome": "He means to reclaim the blighted surface; he outstrips them all.", "reason": "reclaim (probe)"}""");
        var after = await BoardsAsync(world, ScenarioCombatBelmakorPlay.NonAuthorViews, "last");

        // The fight's name, its outcome and the batch's reason are the author's in every view; the effect and the spell the
        // two views that know the ambition may read (FIX §5.1: "except for character:belmakor and dm").
        string[] authorOnly = ["(probe)", "he means to", "them all", "surface;"];
        foreach (var view in ScenarioCombatBelmakorPlay.NonAuthorViews)
        {
            var forbidden = (IReadOnlyList<string>)[.. ScenarioCombatLeak.Belmakor(view), .. authorOnly];
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(during[view], view), forbidden, $"the {view} board of the probe");
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(after[view], view), forbidden, $"the {view} board of the ended probe");
            Assert.StartsWith("# Combat — ended in round 0\n", after[view], StringComparison.Ordinal);
            var (session, refused) = await world.Read("campaign_session", $$"""{"action": "get", "campaign": "belmakor", "session": 4, "perspective": "{{view}}"}""");
            ScenarioCombatLeak.Clean(new ScenarioCombatRead(ScenarioCombatRead.Session, "session 4", session, refused, "4"), view,
                [.. forbidden, "surface", "Outstrips Ward", "## Dice"], view);
            var (sheet, sheetRefused) = await world.Read("campaign_character", $$"""{"action": "get", "campaign": "belmakor", "character": "character:belmakor", "perspective": "{{view}}"}""");
            ScenarioCombatLeak.Clean(new ScenarioCombatRead(ScenarioCombatRead.Sheet, "Belmakor's sheet", sheet, sheetRefused, "character:belmakor"), view,
                [.. forbidden, "surface", "Outstrips Ward"], view);
        }

        var party = ScenarioCombatText.Board(during["party"]);
        Assert.Equal("an effect", party.Single(r => r.Name == "The Old King").Conditions);
        Assert.Equal("HP 110/110 (+7 temp) · AC 17 · concentrating", party.Single(r => r.Name == "Belmakor Silverwind").Status);
        var mine = ScenarioCombatText.Board(during["character:belmakor"]);
        Assert.Equal("reclaim the blighted surface", mine.Single(r => r.Name == "The Old King").Conditions);
        Assert.Equal("HP 110/110 (+7 temp) · AC 17 · concentrating on Outstrips Ward", mine.Single(r => r.Name == "Belmakor Silverwind").Status);
        Assert.Contains("Outcome: He means to reclaim the blighted surface; he outstrips them all.",
            await world.Combat("""{"action": "state", "encounter": "last"}"""), StringComparison.Ordinal);
        Assert.Contains("Ended with the fight:", end, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Fixture B's probes

    [Fact]
    public async Task OnePiece_X2Probes_TheNestersLair_TheNesterTyped_SealedByTheNester_AHiddenTurnHolder_TheHiddenMummysNumbering()
    {
        const string Lair = "The Nester's lair (probe)";
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        await world.Call("campaign_session", ScenarioCombatFixtures.SessionB);
        var views = ScenarioCombatOnePiecePlay.NonAuthorViews;

        var prepared = await world.Combat($$"""
            {"action": "prepare", "campaign": "one-piece", "name": "{{Lair}}",
             "combatants": [{"srd": "2024/monster/aboleth", "name": "The Nester"}, {"srd": "2024/monster/mummy", "hidden": true}, {"srd": "2024/monster/mummy", "count": 2}]}
            """);
        Assert.Equal(
            ["'The Nester' is a name the party does not use (character:the-nester): party views show this combatant as 'Aboleth'; add it with character:the-nester to show the " +
             "name the party knows."],
            ScenarioCombatText.Section(prepared, "Warnings"));
        Assert.Equal(["The Nester", "Mummy", "Mummy 2", "Mummy 3"], ScenarioCombatText.Table(prepared).Select(r => r.Name));

        // prepare + state {perspective, encounter: <planned name>}: a planned fight is never shown, and the text names nothing.
        foreach (var (view, board) in await BoardsAsync(world, views, Lair))
        {
            Assert.StartsWith("# No combat to show for this perspective.\n\n_Perspective: " + view, board, StringComparison.Ordinal);
            Assert.DoesNotContain("| Turn |", board, StringComparison.Ordinal);
        }

        // The start call the prepare printed, sent verbatim (the party joins by default).
        var start = Assert.Single(ScenarioCombatText.PrintedCalls(prepared));
        Assert.Equal($$"""combat {"action": "start", "encounter": "{{Lair}}", "campaign": "one-piece"}""", start);
        await world.Combat(ScenarioCombatText.Arguments(start));
        var sealedBy = await world.Combat("""{"action": "condition", "targets": ["fishman-monk"], "add": ["sealed by the Nester"], "duration": "until removed", "source": "the-nester"}""");
        var warning = Assert.Single(ScenarioCombatText.Section(sealedBy, "Warnings"));
        Assert.StartsWith("'sealed by the Nester' is a name the party does not use (character:the-nester", warning, StringComparison.Ordinal);
        Assert.EndsWith("): party views show this effect as 'an effect'.", warning, StringComparison.Ordinal);
        await world.Combat("""{"action": "add", "combatants": [{"name": "Lurker", "hp": 20, "ac": 12, "hidden": true}]}""");
        world.Dice.Enqueue(20);
        var initiative = await world.Combat("""
            {"action": "initiative", "rolls": [{"combatant": "fishman-monk", "total": 19}, {"combatant": "dragon-slayer", "total": 11},
              {"combatant": "bjorn-mountainfell", "total": 8}, {"combatant": "the-nester", "total": 13}, {"combatant": "mummy", "total": 5}, {"combatant": "mummy-2", "total": 4}]}
            """);

        // The hidden Lurker's roll is the server's, secret and unnamed (§6.10: a hidden subject).
        Assert.Equal(["initiative: `1d20` [20] = 20 · logged as \"a combatant: initiative\" (secret)"], ScenarioCombatText.Section(initiative, "Rolls"));
        Assert.Equal("Lurker", ScenarioCombatText.Table(initiative).Single(r => r.Turn).Name);

        var first = await BoardsAsync(world, views);
        var turns = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            turns.Add(await world.Combat("""{"action": "next"}"""));
        }

        Assert.StartsWith("# The Nester's lair (probe) — round 1 · Mummy's turn\n", turns[^1], StringComparison.Ordinal);
        var hiddenMummy = await BoardsAsync(world, views);

        foreach (var view in views)
        {
            foreach (var (when, board) in new[] { ("the hidden Lurker's turn", first[view]), ("the hidden Mummy's turn", hiddenMummy[view]) })
            {
                ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(board, view),
                    [.. ScenarioCombatLeak.OnePiece(view), "Lurker", "lair", "(probe)", "sealed", "Mummy 3", "hidden"], $"the {view} board at {when}");
                var rows = ScenarioCombatText.Board(board);
                Assert.Equal(6, rows.Count);
                Assert.Equal("Aboleth", rows[1].Name);
                Assert.Equal(["Mummy", "Mummy 2"], rows.Skip(4).Select(r => r.Name));
            }

            // A hidden turn-holder: no marker when nothing shown comes before it this round; else on the last shown row before it.
            Assert.DoesNotContain(ScenarioCombatText.Board(first[view]), r => r.Turn);
            Assert.Equal(3, ScenarioCombatText.Board(hiddenMummy[view]).ToList().FindIndex(r => r.Turn));
        }

        var party = ScenarioCombatText.Board(first["party"]);
        Assert.Equal(("The fishman monk", "an effect"), (party[0].Name, party[0].Conditions));

        // The effect rides the write-back onto the sheet (until removed), its source the Aboleth's party-safe name; every
        // view's line reads "an effect".
        var end = await world.Combat("""{"action": "end", "outcome": "The Nester withdrew (probe)."}""");
        Assert.Contains("- The fishman monk: conditions kept on the sheet: sealed by the Nester.\n", end, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"sealed by the Nester\",\"source\":\"Aboleth\",\"duration\":\"until_removed\"", world.Store.SheetColumn("one-piece", "fishman-monk", "conditions"),
            StringComparison.Ordinal);
        foreach (var view in views)
        {
            var (list, refused) = await world.Read("campaign_character", $$"""{"action": "get", "campaign": "one-piece", "perspective": "{{view}}"}""");
            ScenarioCombatLeak.Clean(new ScenarioCombatRead(ScenarioCombatRead.Sheet, "list", list, refused, string.Empty), view,
                [.. ScenarioCombatLeak.OnePiece(view), "sealed", "(probe)", "withdrew"], view);
            var (last, _) = await world.Read("combat", Board(view, "last"));
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(last, view), [.. ScenarioCombatLeak.OnePiece(view), "Lurker", "sealed", "withdrew", "(probe)"], view);
        }

        Assert.EndsWith("- **The fishman monk** (`character:fishman-monk`) — level 8 Monk (Order of the Deep Sea) · HP 59/59 · AC 16 · an effect\n",
            (await world.Read("campaign_character", """{"action": "get", "campaign": "one-piece", "perspective": "party"}""")).Text, StringComparison.Ordinal);
        var (session, _) = await world.Read("campaign_session", """{"action": "get", "campaign": "one-piece", "session": 13, "perspective": "party"}""");
        Assert.DoesNotContain("## Dice", session, StringComparison.Ordinal);
        AssertCampaignLast([prepared, sealedBy, initiative, end, .. turns], "one-piece");
    }

    /// <summary>
    /// §6.12 "hidden and left combatants are left out entirely", the LEFT half: a combatant that leaves after initiative
    /// (on its own turn, so the turn moves to the next in the order, D15) is on no view's board: no row, no gap in the
    /// numbering, no word of its name, and the turn marker on the new turn-holder's row. Before it left, every view had its
    /// row and its marker, so the probe sees the rule work. Initiative matters: before it, rows come from another branch.
    /// </summary>
    [Fact]
    public async Task OnePiece_ACombatantThatLeavesAfterInitiative_IsOnNoBoard_NoRowNoGapNoMarker()
    {
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        await world.Call("campaign_session", ScenarioCombatFixtures.SessionB);
        var views = ScenarioCombatOnePiecePlay.NonAuthorViews;
        await world.Combat("""
            {"action": "start", "campaign": "one-piece", "name": "Leave (probe)", "add_party": true,
             "combatants": [{"srd": "2024/monster/aboleth"}, {"name": "Lurker", "hp": 20, "ac": 12}]}
            """);
        await world.Combat("""
            {"action": "initiative", "rolls": [{"combatant": "fishman-monk", "total": 19}, {"combatant": "lurker", "total": 15}, {"combatant": "aboleth", "total": 13},
              {"combatant": "dragon-slayer", "total": 11}, {"combatant": "bjorn-mountainfell", "total": 8}]}
            """);
        Assert.StartsWith("# Leave (probe) — round 1 · Lurker's turn\n", await world.Combat("""{"action": "next"}"""), StringComparison.Ordinal);
        var before = await BoardsAsync(world, views);

        var left = await world.Combat("""{"action": "leave", "targets": ["lurker"]}""");
        var after = await BoardsAsync(world, views);

        Assert.StartsWith("# Leave (probe) — round 1 · Aboleth's turn\n", left, StringComparison.Ordinal);
        foreach (var view in views)
        {
            var was = ScenarioCombatText.Board(before[view]);
            Assert.Equal(5, was.Count);
            Assert.Equal(1, was.ToList().FindIndex(r => r.Turn));

            var rows = ScenarioCombatText.Board(after[view]);
            var standIns = ScenarioCombatLeak.OnePieceStandInViews.Contains(view);
            ScenarioCombatLeak.AssertBoardShape(after[view], "# Combat — round 1", view, [!standIns, false, !standIns, !standIns], $"the {view} board once the Lurker left");
            Assert.Equal(2, Assert.Single(rows, r => r.Turn).Number);
            Assert.Equal("Aboleth", rows[1].Name);
            Assert.Equal(new[] { was[0], was[2], was[3], was[4] }.Select(r => (r.Ref, r.Status, r.Conditions)), rows.Select(r => (r.Ref, r.Status, r.Conditions)));
            Assert.DoesNotContain("Lurker", after[view], StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The review's literal Mummy-3 attack (contract-review-leaks F3.3): Mummy and Mummy 2, then a hidden third copy, then a
    /// fourth. Every view renumbers the copies it sees ("Mummy", "Mummy 2", "Mummy 3"), so the fourth reads "Mummy 3" and
    /// no board shows a "Mummy 4" that would count the hidden one.
    /// </summary>
    [Fact]
    public async Task OnePiece_TheThirdOfFourMummiesHidden_TheFourthReadsMummy3_InEveryView()
    {
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        await world.Combat("""{"action": "start", "campaign": "one-piece", "name": "Mummies (probe)", "add_party": false, "combatants": [{"srd": "2024/monster/mummy", "count": 2}]}""");
        await world.Combat("""{"action": "add", "combatants": [{"srd": "2024/monster/mummy", "hidden": true}]}""");
        var fourth = await world.Combat("""{"action": "add", "combatants": [{"srd": "2024/monster/mummy"}]}""");

        Assert.Equal(["Mummy", "Mummy 2", "Mummy 3", "Mummy 4"], ScenarioCombatText.Table(fourth).Select(r => r.Name));
        foreach (var (view, board) in await BoardsAsync(world, ScenarioCombatOnePiecePlay.NonAuthorViews))
        {
            Assert.Equal(["Mummy", "Mummy 2", "Mummy 3"], ScenarioCombatText.Board(board).Select(r => r.Name));
            Assert.DoesNotContain("Mummy 4", board, StringComparison.Ordinal);
            ScenarioCombatLeak.AssertBoardShape(board, "# Combat — not started", view, [false, false, false], $"the {view} board");
        }
    }

    [Fact]
    public async Task OnePiece_P1_P3_BL7_BL1_TheDisguisedProtectorIsTheAdvisorInSerret_StunnedUntilRound2Ends_TheWraithBladesAndTheirRollsUnknown()
    {
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        await world.Call("campaign_session", ScenarioCombatFixtures.SessionB);
        await world.Combat("""{"action": "start", "campaign": "one-piece", "name": "Serret rooftop (probe)", "add_party": true}""");
        var p1 = await world.Combat("""
            {"action": "add", "combatants": [{"character": "character:protector", "side": "ally"}, {"name": "Wraith Blade", "ac": 15, "hp": 33, "count": 3, "side": "enemy"}]}
            """);
        var p2 = await world.Combat("""{"action": "condition", "targets": ["protector"], "add": ["stunned"], "duration": "end of round 2"}""");
        world.Dice.Enqueue(9, 14, 6);
        var initiative = await world.Combat("""
            {"action": "initiative", "rolls": [{"combatant": "fishman-monk", "total": 19}, {"combatant": "dragon-slayer", "total": 11}, {"combatant": "bjorn-mountainfell", "total": 8}]}
            """);

        // P1: the author's tracker names the Protector; the typed custom name the party view would not show is warned about.
        Assert.Equal(
            ["The Protector (ally): HP not tracked, initiative +0.", "Wraith Blade (enemy): 33/33 HP, AC 15, initiative +0.", "Wraith Blade 2 (enemy): 33/33 HP, AC 15, initiative +0.",
             "Wraith Blade 3 (enemy): 33/33 HP, AC 15, initiative +0."],
            ScenarioCombatText.Section(p1, "What changed"));
        Assert.Equal(["'Wraith Blade' holds a word only text the party cannot see holds (character:the-nester): party views show this combatant as 'an unknown creature'."],
            ScenarioCombatText.Section(p1, "Warnings"));
        Assert.Equal(["The Protector: stunned, until the end of round 2."], ScenarioCombatText.Section(p2, "What changed"));

        // B-L1: both server rolls are secret and name neither: the stunned (incapacitated) Protector's 2d20kl1, and the custom
        // enemies' group roll, secret by the DM campaign's own rule (a custom combatant has no entity to hide).
        Assert.Equal(
            ["initiative: `2d20kl1` [9] = 9 · logged as \"a combatant: initiative\" (secret)", "initiative: `1d20` [6] = 6 · logged as \"a combatant: initiative\" (secret)"],
            ScenarioCombatText.Section(initiative, "Rolls"));
        Assert.Equal([("2d20kl1", "a combatant: initiative", true), ("1d20", "a combatant: initiative", true)], world.Store.Dice("one-piece").Select(d => (d.Expression, d.Label!, d.Secret)));
        Assert.Equal([(20, 9), (20, 14), (20, 6)], world.Dice.Rolled);

        var views = ScenarioCombatOnePiecePlay.NonAuthorViews;
        var boards = new List<(string When, string View, string Board)>();
        foreach (var (view, board) in await BoardsAsync(world, views))
        {
            boards.Add(("P2", view, board));
        }

        var turns = new List<string>();
        while (!turns.LastOrDefault("").StartsWith("# Serret rooftop (probe) — round 3", StringComparison.Ordinal))
        {
            turns.Add(await world.Combat("""{"action": "next"}"""));
            foreach (var (view, board) in await BoardsAsync(world, views))
            {
                boards.Add(($"next {turns.Count}", view, board));
            }

            Assert.True(turns.Count < 20, "the fight never reached round 3");
        }

        // P2: the stun ends as round 2 ends, said once, at the wrap into round 3.
        Assert.Single(turns.SelectMany(t => ScenarioCombatText.Section(t, "Reminders")), r => r.StartsWith("stunned ended on The Protector", StringComparison.Ordinal));
        Assert.Contains(ScenarioCombatText.Section(turns[^1], "Reminders"), r => r.StartsWith("stunned ended on The Protector", StringComparison.Ordinal));

        // P3 / B-L7: the party knows him only as the advisor in Serret, by an e: ref; the Wraith Blades are unknown creatures.
        var party = ScenarioCombatText.Board(boards.First(b => b is { When: "P2", View: "party" }).Board);
        var advisor = Assert.Single(party, r => r.Name == "the advisor in Serret");
        Assert.Matches("^e:[0-9]+$", advisor.Ref);
        Assert.Equal(("unhurt", "stunned"), (advisor.Status, advisor.Conditions));
        Assert.Equal(["an unknown creature", "an unknown creature 2", "an unknown creature 3"], party.Where(r => r.Ref is null).Select(r => r.Name));
        Assert.Equal("—", ScenarioCombatText.Board(boards.Last(b => b.View == "party").Board).Single(r => r.Name == "the advisor in Serret").Conditions);
        foreach (var (when, view, board) in boards)
        {
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(board, view),
                [.. ScenarioCombatLeak.OnePiece(view), "Wraith", "Serret rooftop", "(probe)", "end of round", "combat {"], $"the {view} board at {when}");
        }

        // The DM's rolls stay behind the screen: no player view of session 13 shows a die.
        foreach (var view in views)
        {
            var (session, refused) = await world.Read("campaign_session", $$"""{"action": "get", "campaign": "one-piece", "session": 13, "perspective": "{{view}}"}""");
            ScenarioCombatLeak.Clean(new ScenarioCombatRead(ScenarioCombatRead.Session, "session 13", session, refused, "13"), view,
                [.. ScenarioCombatLeak.OnePiece(view), "Wraith", "a combatant", "## Dice"], view);
        }

        AssertCampaignLast([p1, p2, initiative, .. turns], "one-piece");
    }

    [Fact]
    public async Task OnePiece_74_AVisibleNpcWithASheet_ReadsExactlyAsOneWithout_APartyMembersVoidTouchedSubclassIsLeftOutOfItsLine()
    {
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        var views = ScenarioCombatOnePiecePlay.NonAuthorViews;
        async Task<List<string>> ReadsAsync()
        {
            var texts = new List<string>();
            foreach (var view in views)
            {
                texts.Add((await world.Read("campaign_get", $$"""{"campaign": "one-piece", "refs": ["character:arch-mage"], "include": ["sheet"], "perspective": "{{view}}"}""")).Text);
                texts.Add((await world.Read("campaign_character", $$"""{"action": "get", "campaign": "one-piece", "character": "character:arch-mage", "perspective": "{{view}}"}""")).Text);
                texts.Add((await world.Read("campaign_character", $$"""{"action": "get", "campaign": "one-piece", "perspective": "{{view}}"}""")).Text);
            }

            return texts;
        }

        var before = await ReadsAsync();
        await world.Call("campaign_character", """
            {"action": "update", "campaign": "one-piece", "character": "character:arch-mage",
             "sheet": {"classes": [{"class": "wizard", "subclass": "Void-touched", "level": 9}], "ac": 12, "max_hp": 50}}
            """);

        // §7.4: an NPC with a sheet reads exactly as a character with no sheet, in every view (no hint that one exists).
        Assert.Equal(before, await ReadsAsync());
        Assert.StartsWith("# Arch mage — no sheet yet\n", before[views.ToList().IndexOf("party") * 3 + 1], StringComparison.Ordinal);

        // A party member's subclass that names hidden text ("Void-touched" is in the Nester's secret) is left out of its line.
        await world.Call("campaign_character", """
            {"action": "update", "campaign": "one-piece", "character": "character:fishman-monk", "sheet": {"classes": [{"class": "monk", "subclass": "Void-touched", "level": 8}]}}
            """);
        Assert.Contains("level 8 Monk (Void-touched), 2024",
            await world.Call("campaign_character", """{"action": "get", "campaign": "one-piece", "character": "character:fishman-monk"}"""), StringComparison.Ordinal);
        foreach (var view in views)
        {
            var (list, _) = await world.Read("campaign_character", $$"""{"action": "get", "campaign": "one-piece", "perspective": "{{view}}"}""");
            var (page, refused) = await world.Read("campaign_get", $$"""{"campaign": "one-piece", "refs": ["character:fishman-monk"], "include": ["sheet"], "perspective": "{{view}}"}""");
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(list, view), [.. ScenarioCombatLeak.OnePiece(view), "wizard"], $"{view}: the list after the sheets");
            ScenarioCombatLeak.Clean(new ScenarioCombatRead(ScenarioCombatRead.Sheet, "the monk", page, refused, "character:fishman-monk"), view, ScenarioCombatLeak.OnePiece(view), view);
        }

        Assert.Contains("- **The fishman monk** (`character:fishman-monk`) — level 8 Monk · HP 59/59 · AC 16\n",
            (await world.Read("campaign_character", """{"action": "get", "campaign": "one-piece", "perspective": "party"}""")).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnePiece_LongTrackerNames_PrintedCallsWorkVerbatim_EvenInAFightTooBigForOneResult_TheCutNoteSitsBeforeTheReminders()
    {
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        var aboleth = "The ancient one who nests below the sixth station " + new string('x', 26);
        var a = "A" + new string('a', 69);
        var b = "B" + new string('b', 69);
        Assert.Equal(76, aboleth.Length);
        await world.Combat($$"""
            {"action": "start", "campaign": "one-piece", "name": "Long names (probe)", "add_party": true, "combatants": [
              {"srd": "2024/monster/aboleth", "name": "{{aboleth}}"}, {"name": "{{a}}", "count": 18, "hp": 400, "ac": 15, "side": "ally"},
              {"name": "{{b}}", "count": 18, "hp": 400, "ac": 15}]}
            """);
        await world.Combat($$"""
            {"action": "initiative", "rolls": [{"combatant": "{{aboleth}}", "total": 20}, {"combatant": "fishman-monk", "total": 19}, {"combatant": "dragon-slayer", "total": 11},
              {"combatant": "bjorn-mountainfell", "total": 8}, {"combatant": "{{a}}", "total": 15}, {"combatant": "{{b}}", "total": 10}]}
            """);
        foreach (var n in Enumerable.Range(1, 5))
        {
            await world.Combat($$"""{"action": "condition", "targets": ["{{a}}*", "{{b}}*"], "add": ["Effect {{n}} {{new string((char)('c' + n), 40)}}"], "source": "{{a}}"}""");
        }

        await world.Combat($$"""{"action": "concentration", "targets": ["{{aboleth}}"], "spell": "Dominate Monster", "duration": "1 minute"}""");
        var next = await world.Combat("""{"action": "next"}""");
        var step = await world.Combat($$"""{"action": "damage", "targets": ["{{aboleth}}", "{{b}}*"], "amount": 12}""");

        // The step passes the cap: table rows go, the note says so right before the reminders, and no reminder goes.
        Assert.True(step.Length is > 20_000 and <= 24_000, $"{step.Length} characters");
        Assert.Matches(@"\n_Initiative table cut to keep this result within 24,000 characters: \d+ rows of 40 shown; [^\n]+\._\n\n## Reminders\n", step);
        var saveCall = Assert.Single(ScenarioCombatText.PrintedCalls(step), c => c.Contains("\"action\": \"concentration\"", StringComparison.Ordinal));
        var legendaryCall = Assert.Single(ScenarioCombatText.PrintedCalls(next), c => c.Contains("\"action\": \"legendary\"", StringComparison.Ordinal));
        Assert.EndsWith(", \"campaign\": \"one-piece\"}", saveCall, StringComparison.Ordinal);
        Assert.EndsWith(", \"campaign\": \"one-piece\"}", legendaryCall, StringComparison.Ordinal);

        // Both calls, sent as printed with their placeholder filled, reach the long-named Aboleth and no other combatant.
        var kept = await world.Combat(ScenarioCombatText.Arguments(saveCall).Replace("\"total\": …", "\"total\": 25", StringComparison.Ordinal));
        Assert.Contains($"- {aboleth} keeps concentrating on Dominate Monster (25 against DC 10).\n", kept, StringComparison.Ordinal);
        var lash = await world.Combat(ScenarioCombatText.Arguments(legendaryCall).Replace("\"name\": …", "\"name\": \"Lash\"", StringComparison.Ordinal));
        Assert.Contains($"- {aboleth}: legendary action Lash (1), 2/3 left.\n", lash, StringComparison.Ordinal);
        AssertCampaignLast([next, step, kept, lash], "one-piece");
    }

    [Fact]
    public async Task OnePiece_LR03_ALegendaryActionsDamageCall_SentAsPrinted_IsTheAbolethsSecretRoll_NeverThePcsOpenOne()
    {
        // Fix F2, review LR03: the Aboleth's Lash after Björn's turn, sent with no source, was Björn's roll — open in this DM
        // campaign and labelled "Björn Mountainfell: damage" in the party's session log. The legendary step prints the
        // damage call naming the Aboleth; sent as printed (its placeholders filled), the roll is the Aboleth's, secret.
        await using var world = await ScenarioCombatServer.OnePieceAsync();
        await world.Call("campaign_session", ScenarioCombatFixtures.SessionB);
        await world.Combat("""{"action": "start", "campaign": "one-piece", "name": "Lash (probe)", "combatants": [{"srd": "2024/monster/aboleth", "hp": "avg"}]}""");
        await world.Combat("""
            {"action": "initiative", "rolls": [{"combatant": "bjorn-mountainfell", "total": 22}, {"combatant": "aboleth", "total": 18},
              {"combatant": "fishman-monk", "total": 15}, {"combatant": "dragon-slayer", "total": 8}]}
            """);

        var legendary = await world.Combat("""{"action": "legendary", "source": "aboleth", "amount": 1, "name": "Lash"}""");

        Assert.Contains(
            "- Aboleth acts outside its turn (legendary action Lash): give its damage \"source\", or the roll is Björn Mountainfell's: " +
            "combat {\"action\": \"damage\", \"targets\": […], \"dice\": …, \"source\": \"aboleth\", \"campaign\": \"one-piece\"}\n",
            legendary, StringComparison.Ordinal);
        var call = Assert.Single(ScenarioCombatText.PrintedCalls(legendary), c => c.Contains("\"action\": \"damage\"", StringComparison.Ordinal));
        world.Dice.Enqueue(3, 2);
        var rolled = await world.Combat(ScenarioCombatText.Arguments(call)
            .Replace("\"targets\": […]", "\"targets\": [\"fishman-monk\"]", StringComparison.Ordinal)
            .Replace("\"dice\": …", "\"dice\": \"2d6+5\", \"damage_type\": \"bludgeoning\"", StringComparison.Ordinal));

        Assert.Contains("logged as \"Aboleth: damage\" (secret)", rolled, StringComparison.Ordinal);
        var row = Assert.Single(world.Store.Dice("one-piece"), r => r.Expression == "2d6+5");
        Assert.Equal(("Aboleth: damage", true), (row.Label, row.Secret));
        var party = await world.Call("campaign_session", """{"action": "get", "campaign": "one-piece", "perspective": "party", "session": 13}""");
        Assert.DoesNotContain("2d6+5", party, StringComparison.Ordinal);
        Assert.DoesNotContain("Björn Mountainfell: damage", party, StringComparison.Ordinal);
    }
}
