using Xunit;

namespace DndMcp.IntegrationTests.Campaign;

/// <summary>
/// Fixture A's leak rules through the MCP tools (FIX §5.1 as amended by contract §16; §6.12, §7.4, D10), for EVERY
/// non-author view of the Belmakor player campaign (party, table, dm, public and each of the eight characters): every
/// view's board after every step of the fight is the §6.12 whitelist and holds none of the world's secrets and none of the
/// fight's author-only text; the public and the old king, who cannot see the party, see every PC as "an unknown creature
/// N" with an HP word only; after the end, every board ("current", "last", by name), every sheet read
/// (<c>campaign_get include:["sheet"]</c> and <c>campaign_character get</c> of each character, and the default form), session 4
/// and the session list, and every refusal (a perspective on an action that takes none, the old king's true name, a prefix
/// of his handle; made during the fight and after it, each pinned exactly) hold none of it either, refusals once the
/// caller's own typed text is taken out.
/// What breaks if these fail: a player's view of the fight, the sheet or the session shows the DM's notes, the old king's
/// true name, Belmakor's player or his planned Contingency, or the fight's bookkeeping.
/// </summary>
[Collection(ScenarioCombatBelmakorCollection.Name)]
public sealed class ScenarioCombatBelmakorLeakTests(ScenarioCombatBelmakorPlay play)
{
    /// <summary>Every non-author view of the Belmakor campaign.</summary>
    public static TheoryData<string> Views() => new(ScenarioCombatBelmakorPlay.NonAuthorViews);

    private static bool StandIns(string view) => ScenarioCombatLeak.BelmakorStandInViews.Contains(view);

    private const string Search = " campaign_search finds entities and facts by name.";

    // The fight's own data (its name, outcome, stat blocks, effects): what a refusal could leak beyond its static text.
    private static readonly string[] FightData = ["crypt", "fixture", "cleared", "Mummy", "Rejuvenation", "Rotting Fist", "Circle of Power", "Bladesong"];

    // The refusal of a perspective on a combat action, up to its static Example (an example call of the tool's own, which
    // the fix round rewrites: F1 X2-N; not pinned here).
    private const string DamagePerspective =
        "An error occurred invoking 'combat': combat damage does not take \"perspective\"; damage takes targets, amount, dice, parts, damage_type, critical, " +
        "magical, half, raw, knock_out, source, secret, campaign, encounter. Example: ";

    /// <summary>
    /// Each refusal probe exactly as the view reads it: what the caller typed is echoed as typed and nothing else is named.
    /// A prefix of the old king's handle earns a "did you mean" only in the views that know him by that handle.
    /// </summary>
    private static string ProbeRefusal(string label, string view) => label switch
    {
        "board of the true name" =>
            "An error occurred invoking 'combat': perspective \"character:keras\": no character keras in this campaign. Characters are named character:<slug>; " +
            "campaign_search with kinds [\"character\"] lists them.",
        "board of a prefix of the old king" =>
            "An error occurred invoking 'combat': perspective \"character:old\": no character old in this campaign. Characters are named character:<slug>; " +
            "campaign_search with kinds [\"character\"] lists them.",
        "sheet of the true name" => "An error occurred invoking 'campaign_character': character: \"keras\": nothing by that handle for this perspective." + Search,
        "campaign_get of the true name" =>
            "An error occurred invoking 'campaign_get': Invalid refs: refs item 1: \"character:keras\": nothing by that handle for this perspective." + Search,
        "sheet by a prefix" => "An error occurred invoking 'campaign_character': character: \"old\": nothing by that handle for this perspective." +
            (view is "public" or "character:old-king" or "character:tristan" ? Search : " Did you mean character:old-king?"),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "no such probe"),
    };


    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AL1_AL7_TheBoardAfterEveryStep_IsTheWhitelist_NoSecretNoAuthorText(string view)
    {
        foreach (var step in play.Steps)
        {
            var board = step.Boards[view];
            var table = ScenarioCombatText.Table(step.Text);
            var round = System.Text.RegularExpressions.Regex.Match(step.Text, @"— round (\d+)").Groups[1].Value;
            ScenarioCombatLeak.AssertBoardShape(board, round == "0" ? "# Combat — not started" : $"# Combat — round {round}", view,
                ScenarioCombatLeak.Numbers(table, StandIns(view)), $"{view} after {step.Step}");
            var shown = ScenarioCombatLeak.WithoutView(board, view);
            ScenarioLeak.AssertClean(shown, ScenarioCombatLeak.Belmakor(view), $"the {view} board after {step.Step}");
            ScenarioLeak.AssertClean(shown, ScenarioCombatLeak.BelmakorFight, $"the {view} board after {step.Step}");

            // No combatant is hidden or left: the rows are the author's table, in its order, the turn on the same row.
            var rows = ScenarioCombatText.Board(board);
            Assert.Equal(table.Count, rows.Count);
            Assert.Equal(table.FindIndex(r => r.Turn), rows.ToList().FindIndex(r => r.Turn));
            Assert.Equal(step.Step == "A1" ? [] : ["Mummy Lord", "Mummy", "Mummy 2"], rows.Where(r => r.Name.StartsWith("Mummy", StringComparison.Ordinal)).Select(r => r.Name));
            Assert.All(rows.Where(r => r.Name.StartsWith("Mummy", StringComparison.Ordinal)), r => Assert.Null(r.Ref));
        }
    }

    [Fact]
    public void Belmakor_AL4_ThePartyBoard_PartyRowsHaveTheirNumbersAndRefs_TheMummiesOneHpWord()
    {
        var a8 = ScenarioCombatText.Board(play["A8"].Boards["party"]);
        Assert.Equal(
            [
                ("Vars Nocturne", "e:6", "HP unknown"), ("Belmakor Silverwind", "character:belmakor", "HP 82/110 · AC 22 · concentrating on Circle of Power"),
                ("Mummy Lord", null, "unhurt"), ("Serif", "character:serif", "HP unknown"), ("Ignis", "character:ignis", "HP unknown"),
                ("Lieutenant James Torch", "character:torch", "HP 74/74"), ("Mummy", null, "unhurt"), ("Mummy 2", null, "unhurt"),
                ("Aiden Ironstar", "character:aiden-ironstar", "HP unknown"),
            ],
            a8.Select(r => (r.Name, r.Ref, r.Status)));
        Assert.Equal("Bladesong", a8[1].Conditions);

        var a23 = ScenarioCombatText.Board(play["A23"].Boards["party"]);
        Assert.Equal(("HP 0/74 · death saves: 0 successes, 2 failures", "frightened, unconscious, prone"), (a23[5].Status, a23[5].Conditions));
        Assert.Equal(["bloodied", "bloodied", "down"], new[] { a23[2], a23[6], a23[7] }.Select(r => r.Status));

        // One HP word per enemy, never a number: the Mummy Lord unhurt, then at 41/97 and 9/97 bloodied (≤ 48), then down.
        string Lord(string step) => ScenarioCombatText.Board(play[step].Boards["party"])[2].Status;
        Assert.Equal(["unhurt", "unhurt", "bloodied", "bloodied", "bloodied", "down"], new[] { "A3", "A12", "A14", "A23", "A26", "A29" }.Select(Lord));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("character:old-king")]
    public void Belmakor_StandInViews_EveryPcIsAnUnknownCreature_AnHpWordOnly_NoNameNoRefNoNumber(string view)
    {
        var boards = play.Steps.Select(s => (s.Step, Board: s.Boards[view]))
            .Append(("last", play.ReadsAfterEnd[view].Single(r => r.Label == "board last").Text));
        foreach (var (step, board) in boards)
        {
            var rows = ScenarioCombatText.Board(board);
            var standIns = rows.Where(r => !r.Name.StartsWith("Mummy", StringComparison.Ordinal)).ToList();
            Assert.True(rows.Count is 6 or 9, $"{step}: {rows.Count} rows");
            Assert.Equal(["an unknown creature", "an unknown creature 2", "an unknown creature 3", "an unknown creature 4", "an unknown creature 5", "an unknown creature 6"],
                standIns.Select(r => r.Name));
            Assert.All(rows, r => Assert.Null(r.Ref));
            Assert.All(rows, r => Assert.Contains(r.Status, new[] { "unhurt", "hurt", "bloodied", "down" }));
            ScenarioLeak.AssertClean(board[board.IndexOf("| Turn |", StringComparison.Ordinal)..],
                [.. ScenarioCombatLeak.BelmakorPcNameWords, "character:", "e:", "HP ", "AC ", "concentrating", "death saves", "Circle of Power"], $"the {view} board at {step}");
        }

        // Belmakor's damage and Torch's fall show as words: hurt at A8, Torch bloodied once he is up again at 1 HP.
        Assert.Equal("hurt", ScenarioCombatText.Board(play["A8"].Boards[view])[1].Status);
        Assert.Equal("bloodied", ScenarioCombatText.Board(play["A25"].Boards[view])[5].Status);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AL5_AL6_AL7_EveryReadAfterTheEnd_BoardsSheetsSessionAndRefusals_HoldsNothingItMayNotSee(string view)
    {
        foreach (var read in play.ReadsAfterEnd[view])
        {
            var forbidden = read.Surface switch
            {
                ScenarioCombatRead.Board => [.. ScenarioCombatLeak.Belmakor(view), .. ScenarioCombatLeak.BelmakorFight],
                ScenarioCombatRead.Sheet => [.. ScenarioCombatLeak.Belmakor(view), .. ScenarioCombatLeak.BelmakorSheet],
                ScenarioCombatRead.Session => [.. ScenarioCombatLeak.Belmakor(view), .. ScenarioCombatLeak.BelmakorSession],
                _ => ScenarioCombatLeak.Belmakor(view),
            };
            ScenarioCombatLeak.Clean(read, view, forbidden, $"{view} after the end");
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AfterTheEnd_TheBoardsOfLastAndOfItsName_AreTheEndedFight_CurrentIsNothing(string view)
    {
        var reads = play.ReadsAfterEnd[view];
        var last = reads.Single(r => r.Label == "board last").Text;

        Assert.Matches($"^# No combat to show for this perspective\\.\n\n_Perspective: {System.Text.RegularExpressions.Regex.Escape(view)}( \\([^)]+\\))?\\. " +
                       "Names are the ones this view knows; author-only text is withheld\\._\n$", reads.Single(r => r.Label == "board current").Text);
        ScenarioCombatLeak.AssertBoardShape(last, "# Combat — ended in round 3", view, ScenarioCombatLeak.Numbers(ScenarioCombatText.Table(play.LastAuthor), StandIns(view)),
            $"the {view} board of the ended fight");
        Assert.Equal(9, ScenarioCombatText.Board(last).Count);
        Assert.DoesNotContain(ScenarioCombatText.Board(last), r => r.Turn);
        Assert.Equal(last, reads.Single(r => r.Label == "board by name").Text);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AL6_Session4_NineViewsReadIt_NoDiceNoLiveLogNoFight_TheRestAreRefusedCleanly(string view)
    {
        var session = play.ReadsAfterEnd[view].Single(r => r.Label == "session 4");
        var readers = new[] { "party", "table", "dm", "character:belmakor", "character:vars", "character:ignis", "character:serif", "character:torch", "character:aiden-ironstar" };

        Assert.Equal(!readers.Contains(view), session.Refused);
        if (session.Refused)
        {
            Assert.Equal(
                $"An error occurred invoking 'campaign_session': No session session:4 for this perspective. campaign_session {{\"action\": \"list\", " +
                $"\"perspective\": \"{view}\", \"campaign\": \"belmakor\"}} lists the sessions it can see.",
                session.Text);
            return;
        }

        Assert.StartsWith("# Session 4 (live)\n\n", session.Text, StringComparison.Ordinal);
        Assert.Equal(6, ScenarioCombatText.Section(session.Text, "Attendance").Count(l => l.EndsWith(": present", StringComparison.Ordinal)));
        Assert.DoesNotContain("## Dice", session.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Tristan", session.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Belmakor_AL6_TheAuthorsSession4_NoDiceNoLiveLogNoRecap_TheWriteBackItsOnlyBatchBesideTheStart()
    {
        // D10 / A-L6: combat appends nothing to the session's live log or recap, and fixture A rolls no die.
        Assert.StartsWith("# Session 4 (live)\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.DoesNotContain("## Dice", play.SessionAuthor, StringComparison.Ordinal);
        Assert.DoesNotContain("### Live log", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains("## Recap\n_No recap yet._\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains("### What changed (2 batches)\n", play.SessionAuthor, StringComparison.Ordinal);
        Assert.Contains($"- `{play.EndBatch}` combat/end: combat end: The crypt (fixture)\n", play.SessionAuthor, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AL4_TheSheetLines_OnlyCurrentPartyMembersTheViewIsShown_HpAcLevelClassSpecies(string view)
    {
        var lines = play.ReadsAfterEnd[view].Where(r => r.Surface == ScenarioCombatRead.Sheet && !r.Refused)
            .SelectMany(r => r.Text.Split('\n')).Where(l => l.StartsWith("**", StringComparison.Ordinal)).Distinct().ToList();

        // Belmakor's line is the only one with numbers the fixture gives both of (Torch has no AC): exactly §7.4's fields.
        if (lines.FirstOrDefault(l => l.StartsWith("**Belmakor Silverwind**", StringComparison.Ordinal)) is { } belmakor)
        {
            Assert.Equal("**Belmakor Silverwind** (`character:belmakor`) — level 12 Wizard (Bladesinger), High Elf · HP 82/110 · AC 17", belmakor);
        }

        if (lines.FirstOrDefault(l => l.StartsWith("**Lieutenant James Torch**", StringComparison.Ordinal)) is { } torch)
        {
            Assert.Equal("**Lieutenant James Torch** (`character:torch`) — level 12 Wizard · HP 1/74", torch);
        }

        // Tristan (dead) and the old king (an NPC) never get a line; the stand-in views see no member at all.
        Assert.DoesNotContain(lines, l => l.Contains("Tristan", StringComparison.Ordinal) || l.Contains("Old King", StringComparison.Ordinal));
        Assert.Equal(ScenarioCombatLeak.BelmakorStandInViews.Contains(view), lines.Count == 0);
    }

    [Fact]
    public void Belmakor_AL4_ThePartysSheetReads_AreTheLines_AndTheOldKingReadsAsNoSheet()
    {
        var reads = play.ReadsAfterEnd["party"];

        Assert.Equal(
            "# Belmakor Silverwind\n_Perspective: party. Names are the ones this view knows; author-only text is withheld._\n\n" +
            "**Belmakor Silverwind** (`character:belmakor`) — level 12 Wizard (Bladesinger), High Elf · HP 82/110 · AC 17\n",
            reads.Single(r => r.Label == "campaign_character get character:belmakor").Text);
        Assert.Contains("## Sheet\n**Lieutenant James Torch** (`character:torch`) — level 12 Wizard · HP 1/74\n", reads.Single(r => r.Label == "campaign_get character:torch").Text,
            StringComparison.Ordinal);

        // The party knows Vars by e:6 (his slug does not spell his name): his author handle is refused, naming nothing.
        Assert.True(reads.Single(r => r.Label == "campaign_get character:vars").Refused);
        Assert.StartsWith("# The Old King — no sheet yet\n", reads.Single(r => r.Label == "campaign_character get character:old-king").Text, StringComparison.Ordinal);
        Assert.DoesNotContain("### Sheet", reads.Single(r => r.Label == "campaign_get character:old-king").Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_EveryRefusalProbe_IsRefused(string view)
    {
        Assert.All(play.ReadsAfterEnd[view].Where(r => r.Surface == ScenarioCombatRead.Probe), r => Assert.True(r.Refused, $"{view}: {r.Label} succeeded:\n{r.Text}"));
        Assert.All(play.ProbesDuringFight[view], r => Assert.True(r.Refused, $"{view}: {r.Label} succeeded during the fight:\n{r.Text}"));
    }

    /// <summary>
    /// The refusal probes made DURING the fight (after A8) and again after the end, each refused with its own refusal,
    /// exactly: while the fight runs, a perspective on <c>damage</c> can only be refused for the perspective (§6.0, D10), so
    /// its refusal proves that rule; the old king's true name typed in lower case, and a prefix of his handle, are not
    /// found and echo only what was typed. No refusal spells "Keras" (the server's own capitalised spelling of the true name
    /// would be a leak a case-blind strip of the typed "keras" could hide), and none holds a word of the world's secrets or of
    /// this fight's data (the perspective refusal's static Example, the fix round's, is not pinned).
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_TheRefusalProbes_DuringTheFightAndAfterTheEnd_AreEachTheirOwnRefusal_Exactly(string view)
    {
        var after = play.ReadsAfterEnd[view].Where(r => r.Surface == ScenarioCombatRead.Probe).ToList();
        foreach (var (when, probes) in new[] { ("during the fight", play.ProbesDuringFight[view]), ("after the end", after) })
        {
            Assert.Equal(
                ["combat damage with a perspective", "board of the true name", "board of a prefix of the old king", "sheet of the true name", "campaign_get of the true name",
                 "sheet by a prefix"],
                probes.Select(r => r.Label));
            foreach (var probe in probes)
            {
                Assert.DoesNotContain("Keras", probe.Text, StringComparison.Ordinal);
                if (probe.Label == "combat damage with a perspective")
                {
                    // Its static Example names a fixture call's targets and damage types; nothing of THIS fight's data may ride along.
                    Assert.StartsWith(DamagePerspective, probe.Text, StringComparison.Ordinal);
                    ScenarioLeak.AssertClean(probe.Text, [.. ScenarioCombatLeak.Belmakor(view), .. FightData], $"{view} {when}: {probe.Label}");
                }
                else
                {
                    Assert.Equal(ProbeRefusal(probe.Label, view), probe.Text);
                }
            }
        }
    }

    /// <summary>
    /// A-L6/A-L7 on the Phase 6 readers: after the fight, nothing any non-author view reaches through them (the listing,
    /// searches for the fight's words, a get of every handle with every include, the summary, the sessions, the knowledge
    /// resource; <see cref="ScenarioReach.ReadAllAsync"/>) carries the fight, the write-back or the sheet: combat appends
    /// nothing to a session or a page (D10), and the sheet is not an include any of them prints. The dm meets "Keras" only in
    /// the fact she was told, as in Phase 6.
    /// </summary>
    [Theory]
    [MemberData(nameof(Views))]
    public async Task Belmakor_AL6_AL7_EverythingThePhase6ReadersReachAfterTheFight_HoldsNothingOfIt(string view)
    {
        var outputs = await ScenarioReach.ReadAllAsync(play.World.Server, "belmakor", view, ["crypt", "mummy", "circle of power", "bladesong"], [], [1, 2, 3, 4]);

        Assert.True(outputs.Count > 10, $"{outputs.Count} reads");
        foreach (var (label, text) in outputs)
        {
            ScenarioLeak.AssertBelmakorClean(ScenarioCombatLeak.WithoutView(text, view), view, play.World.Belmakor!.NameFact, $"{label} as {view} after the fight");
            ScenarioLeak.AssertClean(text, ScenarioCombatLeak.BelmakorReach, $"{label} as {view} after the fight");
        }
    }

    [Fact]
    public void Belmakor_TheSweepsOwnLists_EveryFightSheetAndSessionStringIsHeldByTheAuthor()
    {
        var held = string.Join("\n",
        [
            .. play.Steps.Select(s => s.Text), play.End, play.EndDryRun, play.LastAuthor, play.SheetsAfter, play.SessionAuthor, play.Simulation,
            play.SessionStart,
        ]);

        ScenarioCombatLeak.AssertHeld(held,
            [.. ScenarioCombatLeak.BelmakorFight, .. ScenarioCombatLeak.BelmakorSheet, .. ScenarioCombatLeak.BelmakorSession.Except(["## Dice"]),
             .. ScenarioCombatLeak.BelmakorReach.Except(["sheet_source"])],
            "fixture A's author results");
    }

    [Fact]
    public void Belmakor_EveryReadRefusalAndExampleOfThePlay_NamesTheCampaignLast()
    {
        // Fix F1, "one order everywhere": Belmakor_EveryPrintedCall_NamesTheCampaignLastAndOnce checks the author's step results;
        // this one every other text the play recorded: every view's reads and refusals (the probes' examples, the session lists
        // and their not-found refusals) and the author's own session, sheets and goldens. A call naming a campaign names it last.
        var texts = play.ReadsAfterEnd.Values.SelectMany(reads => reads).Concat(play.ProbesDuringFight.Values.SelectMany(reads => reads))
            .Select(r => (r.Label, r.Text))
            .Append(("session (author)", play.SessionAuthor)).Append(("sheets before", play.SheetsBefore)).Append(("sheets after", play.SheetsAfter))
            .Concat(new (string, string)[] { ("difficulty", play.Difficulty), ("simulation", play.SimulationExplicit) });

        var calls = texts.Sum(t => CampaignPrintedCallTests.AssertCampaignLast(t.Item2, t.Item1, "belmakor"));

        Assert.True(calls > 0, "the play printed no call naming a campaign outside the steps");
    }
}
