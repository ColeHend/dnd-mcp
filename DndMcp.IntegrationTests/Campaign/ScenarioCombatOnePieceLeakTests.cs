using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Fixture B's leak rules through the MCP tools (FIX §5.2 as amended by contract §16; §6.10, §6.12, §7.4, D10, D20), for
/// EVERY non-author view of the One Piece DM campaign (party, table, public and each of the ten characters, the Nester's own
/// view among them): B-L2 the Aboleth is "Aboleth", never The Nester, its handle or its secret, at every step and in every
/// view; B-L3 one HP word for it ("bloodied" from B15), never its numbers; B-L6 no lair, XP, loot, difficulty or setting
/// (the difficulty words checked on host text, where encounter_difficulty prints them); B-L1 the DM's two rolls stay behind
/// the screen in session 13 (only the potion's open roll reaches the players) and the Nester's and the visible arch
/// mage's rest rolls in session 14 (only Björn's); B-L4 nothing of the proposal, Eldritch Restoration, the outcome, the
/// loot or the coins reaches any view after the end; the eight views that cannot see the party see stand-ins at every
/// step; the Nester's NPC sheet (subclass "Void-touched") reads as no sheet; and every refusal, made during the fight and
/// after it, is its own refusal exactly and clean once the caller's own typed text is out. The views are swept against Phase 6's One Piece list (player views) or Phase 6's NPC
/// baseline (the NPCs, who know some of its words), and all against the Nester.
/// What breaks if these fail: the party learns the Nester's name, a DM roll, the monster's numbers or the DM's notes from
/// the tracker, a sheet or a session.
/// </summary>
[Collection(ScenarioCombatOnePieceCollection.Name)]
public sealed class ScenarioCombatOnePieceLeakTests(ScenarioCombatOnePiecePlay play)
{
    /// <summary>Every non-author view of the One Piece campaign.</summary>
    public static TheoryData<string> Views() => new(ScenarioCombatOnePiecePlay.NonAuthorViews);

    // The no-fight text with the view's own banner name (a character view that knows itself prints it) taken out.
    private static string NoCombat(string view) =>
        $"# No combat to show for this perspective.\n\n_Perspective: {view}. Names are the ones this view knows; author-only text is withheld._\n";

    private static string Banner(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"^(_Perspective: character:[a-z0-9-]+) \([^)]+\)\.", "$1.", System.Text.RegularExpressions.RegexOptions.Multiline);

    private static bool Reads13(string view) => view is "party" or "table" or "character:bjorn-mountainfell" or "character:dragon-slayer" or "character:fishman-monk";

    private static bool StandIns(string view) => ScenarioCombatLeak.OnePieceStandInViews.Contains(view);

    /// <summary>The views that see the party as stand-ins (contract §6.12: not visible → "an unknown creature").</summary>
    public static TheoryData<string> StandInViews() => new(ScenarioCombatLeak.OnePieceStandInViews);

    private const string Search = " campaign_search finds entities and facts by name.";

    // The refusal of a perspective on a combat action, up to its static Example (the fix round rewrites the example: F1 X2-N).
    private const string DamagePerspective =
        "An error occurred invoking 'combat': combat damage does not take \"perspective\"; damage takes targets, amount, dice, parts, damage_type, critical, " +
        "magical, half, raw, knock_out, source, secret, campaign, encounter. Example: ";

    // The fight's own data (its name, outcome, loot, stat block): what a refusal could leak beyond its static text.
    private static readonly string[] FightData = ["dark station", "sixth station", "fixture", "Aboleth", "ledger", "Water Breathing", "Mucus", "Eldritch"];

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL2_BL3_BL6_TheBoardAfterEveryStep_IsTheWhitelist_NoSecretNoAuthorText(string view)
    {
        foreach (var step in play.Steps)
        {
            var board = step.Boards[view];
            var table = ScenarioCombatText.Table(step.Text);
            var round = System.Text.RegularExpressions.Regex.Match(step.Text, @"— round (\d+)").Groups[1].Value;
            ScenarioCombatLeak.AssertBoardShape(board, round == "0" ? "# Combat — not started" : $"# Combat — round {round}", view,
                ScenarioCombatLeak.Numbers(table, StandIns(view)), $"{view} after {step.Step}");
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(board, view),
                [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceFight, .. ScenarioCombatLeak.OnePieceDifficulty], $"the {view} board after {step.Step}");

            var rows = ScenarioCombatText.Board(board);
            Assert.Equal(table.Count, rows.Count);
            Assert.Equal(table.FindIndex(r => r.Turn), rows.ToList().FindIndex(r => r.Turn));
            if (step.Step != "B1")
            {
                var aboleth = Assert.Single(rows, r => r.Name == "Aboleth");
                Assert.Null(aboleth.Ref);
                Assert.Contains(aboleth.Status, new[] { "unhurt", "hurt", "bloodied", "down" });
                Assert.Equal("—", aboleth.Conditions);
            }
        }
    }

    [Fact]
    public void OnePiece_BL3_ThePartySeesTheAbolethUnhurtHurtBloodiedFromB15Down_NeverANumber_AndItsOwnRowsNumbers()
    {
        string Word(string step) => ScenarioCombatText.Board(play[step].Boards["party"]).Single(r => r.Name == "Aboleth").Status;

        Assert.Equal(["unhurt", "hurt", "hurt", "hurt", "bloodied", "bloodied", "bloodied", "bloodied", "down"],
            new[] { "B4", "B5", "B10", "B12", "B15", "B16", "B19", "B23", "B26" }.Select(Word));
        Assert.All(play.Steps.TakeWhile(s => s.Step != "B15").Skip(1), s => Assert.NotEqual("bloodied", Word(s.Step)));

        Assert.Equal(
            [
                ("The fishman monk", "character:fishman-monk", "HP 25/59 · AC 16", "grappled, cursed (Mucus Cloud)"),
                ("Aboleth", null, "bloodied", "—"),
                ("The amethyst Dragon Slayer", "e:3", "HP 56/68 · AC 16", "—"),
                ("Björn Mountainfell", "character:bjorn-mountainfell", "HP 67/85 · AC 15 · exhaustion 1", "grappled, Rage"),
            ],
            ScenarioCombatText.Board(play["B15"].Boards["party"]).Select(r => (r.Name, r.Ref, r.Status, r.Conditions)));
        Assert.Equal("HP 0/59 · AC 16 · death saves: 0 successes, 2 failures", ScenarioCombatText.Board(play["B20"].Boards["party"])[0].Status);
        Assert.Equal("HP 7/59 · AC 16", ScenarioCombatText.Board(play["B22"].Boards["party"])[0].Status);
    }

    [Fact]
    public void OnePiece_TheStandInViews_AreExactlyTheViewsWhoseFirstBoardShowsAnUnknownCreature()
    {
        Assert.Equal(
            ScenarioCombatLeak.OnePieceStandInViews,
            ScenarioCombatOnePiecePlay.NonAuthorViews.Where(v => ScenarioCombatText.Board(play["B1"].Boards[v]).Any(r => r.Name.StartsWith("an unknown creature", StringComparison.Ordinal))));
    }

    /// <summary>
    /// The public and the seven NPCs cannot see the party (§6.12): at EVERY step and on the ended board, every PC is "an
    /// unknown creature", "… 2", "… 3" in the order's places, with one HP word, no ref, no number and no word of a PC's
    /// name; the Aboleth is its stat block's name. A sheet's numbers or a PC's name on such a row would show a stranger
    /// the party it cannot see.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandInViews))]
    public void OnePiece_ViewsThatCannotSeeTheParty_SeeStandInsWithHpWordsAtEveryStep_NoNameNoRefNoNumber(string view)
    {
        var boards = play.Steps.Select(s => (s.Step, Board: s.Boards[view])).Append(("last", play.ReadsAfterEnd[view].Single(r => r.Label == "board last").Text));
        foreach (var (step, board) in boards)
        {
            var rows = ScenarioCombatText.Board(board);
            Assert.True(rows.Count == (step == "B1" ? 3 : 4), $"{step}: {rows.Count} rows");
            Assert.Equal(["an unknown creature", "an unknown creature 2", "an unknown creature 3"], rows.Where(r => r.Name != "Aboleth").Select(r => r.Name));
            Assert.All(rows, r => Assert.Null(r.Ref));
            Assert.All(rows, r => Assert.Contains(r.Status, ScenarioCombatLeak.HpWords));
            ScenarioLeak.AssertClean(board[board.IndexOf("| Turn |", StringComparison.Ordinal)..],
                [.. ScenarioCombatLeak.OnePiecePcNameWords, .. ScenarioCombatLeak.StandInRowWords], $"the {view} board at {step}");
        }

        var b15 = ScenarioCombatText.Board(play["B15"].Boards[view]);

        Assert.Equal(
            [
                ("an unknown creature", "bloodied", "grappled, cursed (Mucus Cloud)"), ("Aboleth", "bloodied", "—"), ("an unknown creature 2", "hurt", "—"),
                ("an unknown creature 3", "hurt", "grappled, Rage"),
            ],
            b15.Select(r => (r.Name, r.Status, r.Conditions)));
        Assert.All(b15, r => Assert.Null(r.Ref));
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL4_EveryReadAfterTheEnd_BoardsSheetsSessionAndRefusals_HoldsNothingItMayNotSee(string view)
    {
        foreach (var read in play.ReadsAfterEnd[view])
        {
            IReadOnlyList<string> forbidden = read.Surface switch
            {
                ScenarioCombatRead.Board => [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceFight, .. ScenarioCombatLeak.OnePieceDifficulty],
                ScenarioCombatRead.Sheet => [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceSheet, .. ScenarioCombatLeak.OnePieceDifficulty],
                ScenarioCombatRead.Session => [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceSession, .. ScenarioCombatLeak.OnePieceDifficulty],
                _ => ScenarioCombatLeak.OnePiece(view),
            };
            ScenarioCombatLeak.Clean(read, view, forbidden, $"{view} after the end");
        }

        Assert.All(play.ReadsAfterEnd[view].Where(r => r.Surface == ScenarioCombatRead.Probe), r => Assert.True(r.Refused, $"{view}: {r.Label} succeeded:\n{r.Text}"));
    }

    /// <summary>
    /// The refusal probes made DURING the fight (after B15) and again after the end, each refused with its own refusal,
    /// exactly: while the fight runs, a perspective on <c>damage</c> can only be refused for the perspective (§6.0, D10), so
    /// its refusal proves that rule; prefixes of the Nester's handle and the handle itself are not found and echo only what
    /// was typed. None spells "Nester" in any other way than typed, and none holds a word of the world's secrets or of the
    /// fight's data (the perspective refusal's static Example, the fix round's, and Phase 6's session call, whose argument
    /// order the fix round changes, are not pinned).
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_TheRefusalProbes_DuringTheFightAndAfterTheEnd_AreEachTheirOwnRefusal_Exactly(string view)
    {
        var after = play.ReadsAfterEnd[view].Where(r => r.Surface == ScenarioCombatRead.Probe).ToList();
        foreach (var (when, probes) in new[] { ("during the fight", play.ProbesDuringFight[view]), ("after the end", after) })
        {
            Assert.Equal(
                ["combat damage with a perspective", "board of a prefix of the Nester", "sheet of the Nester by a prefix", "campaign_get of the Nester", "session 14, not played yet"],
                probes.Select(r => r.Label));
            Assert.All(probes, r => Assert.True(r.Refused, $"{view} {when}: {r.Label} succeeded:\n{r.Text}"));
            Assert.StartsWith(DamagePerspective, probes[0].Text, StringComparison.Ordinal);
            Assert.Equal(
                "An error occurred invoking 'combat': perspective \"character:the-nest\": no character the-nest in this campaign. Characters are named " +
                "character:<slug>; campaign_search with kinds [\"character\"] lists them.",
                probes[1].Text);
            Assert.Equal("An error occurred invoking 'campaign_character': character: \"the-nest\": nothing by that handle for this perspective." + Search, probes[2].Text);
            Assert.Equal(
                "An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"character:the-nester\": nothing by that handle for this perspective." + Search,
                probes[3].Text);
            Assert.StartsWith("An error occurred invoking 'campaign_session': No session session:14 for this perspective. ", probes[4].Text, StringComparison.Ordinal);
            foreach (var probe in probes)
            {
                ScenarioCombatLeak.CleanMessage(ScenarioCombatLeak.WithoutView(probe.Text, view), probe.Typed, [.. ScenarioCombatLeak.OnePiece(view), .. FightData],
                    $"{view} {when}: {probe.Label}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_AfterTheEnd_TheBoardsOfLastAndOfItsName_AreTheEndedFight_CurrentIsNothing(string view)
    {
        var reads = play.ReadsAfterEnd[view];
        var last = reads.Single(r => r.Label == "board last").Text;

        Assert.Equal(NoCombat(view), Banner(reads.Single(r => r.Label == "board current").Text));
        ScenarioCombatLeak.AssertBoardShape(last, "# Combat — ended in round 3", view, ScenarioCombatLeak.Numbers(ScenarioCombatText.Table(play.LastAuthor), StandIns(view)),
            $"the {view} board of the ended fight");
        Assert.Equal(4, ScenarioCombatText.Board(last).Count);
        Assert.Equal("down", ScenarioCombatText.Board(last).Single(r => r.Name == "Aboleth").Status);
        Assert.Equal(last, reads.Single(r => r.Label == "board by name").Text);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL1_Session13_ThePlayersSeeOnlyThePotionsOpenRoll_TheRestAreRefusedCleanly(string view)
    {
        var session = play.ReadsAfterEnd[view].Single(r => r.Label == "session 13");

        Assert.Equal(!Reads13(view), session.Refused);
        if (session.Refused)
        {
            ScenarioCombatLeak.CleanMessage(ScenarioCombatLeak.WithoutView(session.Text, view), "13",
                [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceSession, .. ScenarioCombatLeak.OnePieceDifficulty], $"{view}: session 13");
            return;
        }

        Assert.StartsWith("# Session 13 (live)\n\n", session.Text, StringComparison.Ordinal);
        var dice = session.Text[session.Text.IndexOf("## Dice (1)\n| At | Roll | Label | Total |\n|---|---|---|---|\n", StringComparison.Ordinal)..];
        Assert.Matches(@"^## Dice \(1\)\n\| At \| Roll \| Label \| Total \|\n\|---\|---\|---\|---\|\n\| [0-9T:.Z-]+ \| 2d4\+2 \| Björn Mountainfell: heal \| 7 \|\n", dice);
    }

    [Fact]
    public void OnePiece_BL1_TheAuthorsSession13_ListsTheThreeRolls_TheDmsMarkedSecret()
    {
        Assert.Contains("## Dice (3)\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains("| 1d10 | Aboleth: heal (secret) | 5 |\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains("| 4d6+5 | Aboleth: damage (critical) (secret) | 19 |\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains("| 2d4+2 | Björn Mountainfell: heal | 7 |\n", play.SessionAuthor, StringComparison.Ordinal);

        // D10: combat appends nothing to the session's live log or recap; its only batch there is the write-back.
        Assert.DoesNotContain("### Live log", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains("## Recap\n_No recap yet._\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains("### What changed (2 batches)\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains($"- `{play.EndBatch}` combat/end: combat end: The dark station (fixture)\n", play.SessionAuthor, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_D11_TheSessionWithTheRestRolls_OnlyBjornsOpenRollReachesThePlayers_TheNestersStaysSecretAndUnnamed(string view)
    {
        var session = play.RestSession[view];

        Assert.Equal(!Reads13(view), session.Refused);
        ScenarioCombatLeak.Clean(session, view, [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceSession, "1d8", "1d6", "Arch mage: hit dice"],
            $"{view}: session 14");
        if (!session.Refused)
        {
            Assert.Matches(@"\n## Dice \(1\)\n\| At \| Roll \| Label \| Total \|\n\|---\|---\|---\|---\|\n\| [0-9T:.Z-]+ \| 1d12 \| Björn Mountainfell: hit dice \| 7 \|\n", session.Text);
        }
    }

    /// <summary>
    /// D11: a rest roll is secret unless its character is a current party member Shown to the party. Björn is one (open,
    /// by his name); the Nester is restricted (secret, "a character"); the arch mage is an NPC the party SEES but not a
    /// member, so only the roster clause makes its roll secret (stage-5 check: without it, the roll reached every player
    /// view of the session).
    /// </summary>
    [Fact]
    public void OnePiece_D11_TheRests_TheServerRollsTheHitDice_BjornsOpen_TheNestersAndTheVisibleArchMagesSecret()
    {
        Assert.Contains("Hit Dice spent: d12 7, +3 Con each → +10 HP; HP 51 → 61.\n", play.Rests.Bjorn, StringComparison.Ordinal);
        Assert.Contains("## Rolls\n- `1d12` [7] = 7 · logged as \"Björn Mountainfell: hit dice\"\n", play.Rests.Bjorn, StringComparison.Ordinal);
        Assert.Contains("## Rolls\n- `1d8` [5] = 5 · logged as \"a character: hit dice\" (secret)\n", play.Rests.Nester, StringComparison.Ordinal);
        Assert.Contains("## Rolls\n- `1d6` [4] = 4 · logged as \"Arch mage: hit dice\" (secret)\n", play.Rests.ArchMage, StringComparison.Ordinal);
        Assert.Contains("- hp: 47 → 37\n", play.ArchMage.Damage, StringComparison.Ordinal);
        Assert.Contains("Hit Dice spent: d6 4, +1 Con each → +5 HP; HP 37 → 42.\n", play.Rests.ArchMage, StringComparison.Ordinal);
        Assert.Contains("| 1d12 | Björn Mountainfell: hit dice | 7 |\n", play.RestSessionAuthor, StringComparison.Ordinal);
        Assert.Contains("| 1d8 | a character: hit dice (secret) | 5 |\n", play.RestSessionAuthor, StringComparison.Ordinal);
        Assert.Contains("| 1d6 | Arch mage: hit dice (secret) | 4 |\n", play.RestSessionAuthor, StringComparison.Ordinal);

        var rests = play.World.Store.Dice("one-piece").Where(d => d.SessionNumber == 14).ToList();
        Assert.Equal(
            [("1d12", "Björn Mountainfell: hit dice", false, (string?)null), ("1d8", "a character: hit dice", true, null), ("1d6", "Arch mage: hit dice", true, null)],
            rests.Select(d => (d.Expression, d.Label!, d.Secret, d.EncounterId)));
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_74_TheNestersNpcSheet_VoidTouched_ReadsAsNoSheet_InEveryView(string view)
    {
        Assert.Contains("- classes: [] → [{\"class\":\"warlock\",\"subclass\":\"Void-touched\",\"level\":10,\"hit_die\":8}]\n", play.NesterSheet, StringComparison.Ordinal);
        foreach (var read in play.NesterSheetReads[view])
        {
            ScenarioCombatLeak.Clean(read, view, [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceSheet, "warlock", "level 10", "90"], $"{view} after the Nester's sheet");
        }

        // No view sees the Nester: its page and its sheet are the Phase 6 not-found, by handle, naming nothing.
        Assert.All(play.NesterSheetReads[view].Where(r => r.Label != "campaign_character get (no character)"), r => Assert.True(r.Refused, $"{view}: {r.Label}"));
        Assert.Equal(play.ReadsAfterEnd[view].Single(r => r.Label == "campaign_character get (no character)").Text,
            play.NesterSheetReads[view].Single(r => r.Label == "campaign_character get (no character)").Text);
    }

    [Fact]
    public void OnePiece_74_ThePartysListForm_IsTheThreeMembersLines_NothingElse()
    {
        Assert.Equal(
            "# One Piece: the party's sheets\n_Perspective: party. Names are the ones this view knows; author-only text is withheld._\n\n" +
            "- **Björn Mountainfell** (`character:bjorn-mountainfell`) — level 8 Barbarian (Path of the Totem Warrior) · HP 51/85 · AC 15 · Exhaustion 1\n" +
            "- **The amethyst Dragon Slayer** (`e:3`) — level 8 dragon slayer (Amethyst) · HP 56/68 · AC 16\n" +
            "- **The fishman monk** (`character:fishman-monk`) — level 8 Monk (Order of the Deep Sea) · HP 7/59 · AC 16 · cursed (Mucus Cloud)\n",
            play.ReadsAfterEnd["party"].Single(r => r.Label == "campaign_character get (no character)").Text);
        foreach (var view in new[] { "public", "character:the-nester", "character:protector", "character:nadar" })
        {
            Assert.EndsWith("_\n\nNothing to show.\n", play.ReadsAfterEnd[view].Single(r => r.Label == "campaign_character get (no character)").Text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_X2Probe_APlannedFightByItsName_IsNeverShown_NoTextNamesIt(string view)
    {
        Assert.Equal(NoCombat(view), Banner(play.PlannedBoards[view]));
    }

    /// <summary>
    /// B-L4 on the Phase 6 readers: after the fight (and the rests), nothing any non-author view reaches through them (the
    /// listing, searches for the fight's words, a get of every handle with every include, the summary, the sessions played,
    /// the knowledge resource) carries the fight, its loot, the Nester or the sheets (D10, D20).
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public async Task OnePiece_BL4_EverythingThePhase6ReadersReachAfterTheFight_HoldsNothingOfIt(string view)
    {
        var outputs = await ScenarioReach.ReadAllAsync(play.World.Server, "one-piece", view, ["aboleth", "nester", "station", "potion", "ledger"], [], [1, 5, 6, 7, 13, 14]);

        Assert.True(outputs.Count > 10, $"{outputs.Count} reads");
        foreach (var (label, text) in outputs)
        {
            ScenarioLeak.AssertClean(ScenarioCombatLeak.WithoutView(text, view),
                [.. ScenarioCombatLeak.OnePiece(view), .. ScenarioCombatLeak.OnePieceReach, .. ScenarioCombatLeak.OnePieceDifficulty], $"{label} as {view} after the fight");
        }
    }

    [Fact]
    public void OnePiece_TheSweepsOwnLists_EveryFightSheetAndSessionStringIsHeldByTheAuthor()
    {
        var held = string.Join("\n",
        [
            .. play.Steps.Select(s => s.Text), play.End, play.EndDryRun, play.LastAuthor, play.SheetsAfter, play.SessionAuthor, play.Difficulty,
            play.LairSimulation, play.RestSessionAuthor, play.Rests.Nester, play.SessionStart, ScenarioCombatFixtures.NesterSecret, ScenarioCombatFixtures.OnePieceEntities,
        ]);

        ScenarioCombatLeak.AssertHeld(held,
            [.. ScenarioCombatLeak.OnePieceFight, .. ScenarioCombatLeak.OnePieceSheet, .. ScenarioCombatLeak.OnePieceSession, .. ScenarioCombatLeak.Nester,
             .. ScenarioCombatLeak.OnePieceReach.Except(["sheet_source"]), .. ScenarioCombatLeak.OnePieceDifficulty],
            "fixture B's author results");
    }

    [Fact]
    public void OnePiece_EveryReadRefusalAndExampleOfThePlay_NamesTheCampaignLast()
    {
        // Fix F1, "one order everywhere": OnePiece_EveryPrintedCall_NamesTheCampaignLastAndOnce checks the author's step results;
        // this one every other text the play recorded: every view's reads and refusals (the probes' examples, the session lists
        // and their not-found refusals) and the author's own session, sheets and goldens. A call naming a campaign names it last.
        var texts = play.ReadsAfterEnd.Values.SelectMany(reads => reads).Concat(play.ProbesDuringFight.Values.SelectMany(reads => reads))
            .Select(r => (r.Label, r.Text))
            .Append(("session (author)", play.SessionAuthor)).Append(("sheets before", play.SheetsBefore)).Append(("sheets after", play.SheetsAfter))
            .Concat(new (string, string)[]
            {
                ("difficulty", play.Difficulty), ("book-only difficulty", play.DifficultyBookOnly), ("the rest session", play.RestSessionAuthor),
                ("the Nester after end", play.NesterAfterEnd), ("the proposal's dry run", play.ProposalDryRun),
            });

        var calls = texts.Sum(t => CampaignPrintedCallTests.AssertCampaignLast(t.Item2, t.Item1, "one-piece"));

        Assert.True(calls > 0, "the play printed no call naming a campaign outside the steps");
    }
}
