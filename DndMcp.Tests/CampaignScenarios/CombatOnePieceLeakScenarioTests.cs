using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignCombat;
using DndMcp.Tests.CampaignRead;
using Xunit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Fixture B's leak rules at the Repository (FIX §5.2 as amended by contract §16; §6.10, §6.12, §7.4, D10, D20) for every
/// non-author view of the One Piece DM campaign (the player views, the NPC characters with knowledge of their own, and
/// fixture B's monk), each read serialised WHOLE (<see cref="LeakAssert"/>). Across the WHOLE fight: every view's board
/// after every step (B-L2: the Aboleth is "Aboleth", never The Nester, its secret or its handle; B-L3: one HP word for it,
/// "bloodied" from B15, never its numbers, AC, resistance or legendary counts; B-L6: no lair, XP, difficulty or setting;
/// no author text). B-L1: session 13 shows the players only the open potion roll; the two DM rolls stay behind the
/// screen. B-L4: after the end nothing of the proposal, Eldritch Restoration, the outcome, the loot or the coins reaches a
/// player view (session, board, sheets, every Phase 6 reader); an until-removed effect naming the Nester rides the
/// write-back onto the sheet and reads "an effect" on every player's line. B-L7 / P1-P3: the disguised Protector fights as "the advisor
/// in Serret" with an <c>e:</c> ref, its stun ends with round 2, its roll is "a combatant", and the custom "Wraith Blade"
/// (a word only hidden text holds) is "an unknown creature" to the party.
/// </summary>
public sealed class CombatOnePieceLeakScenarioTests(CombatOnePiecePlay play) : IClassFixture<CombatOnePiecePlay>
{
    private static readonly Perspective Party = Perspective.Parse("party");

    public static TheoryData<string> Views() => new(CombatOnePiecePlay.NonAuthorPerspectives);

    private static bool IsPlayerView(string view) => CombatOnePiecePlay.PlayerViews.Contains(view);

    /// <summary>
    /// A view's secrets: FIX §5.2's list for the players; for the NPC characters Phase 6's baseline for them (the answer
    /// words none of them knows) and the Nester, which nobody knows.
    /// </summary>
    private static IReadOnlyList<string> Secrets(string view) =>
        IsPlayerView(view) ? CombatScenarioLeaks.OnePiece : [.. CombatScenarioLeaks.OnePieceNpcBaseline, .. CombatScenarioLeaks.OnePieceNester];

    private static IReadOnlyList<string> Forbidden(string view) => [.. Secrets(view), .. CombatScenarioLeaks.OnePieceNester, .. CombatScenarioLeaks.OnePieceFight];

    // ------------------------------------------------------------------------------------------------------------------
    // The whole fight, every view

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL2_BL6_TheBoardAfterEveryStep_HoldsNoSecretNoSettingAndNoAuthorText(string view)
    {
        foreach (var step in play.Steps)
        {
            LeakAssert.Clean(step.Boards[view], Forbidden(view), $"the {view} board after {step.Step}");
        }

        // "last" is the ended fight itself (never the board of no fight, which would pass any sweep): shown, ended, the
        // round it ended in, a row for every combatant.
        var last = play.LastBoards[view];
        Assert.Equal((true, true, 3, play.Steps[^1].Persisted.Combatants.Count), (last.Shown, last.Ended, last.Round, last.Rows.Count));
        LeakAssert.Clean(last, Forbidden(view), $"the {view} board of the ended fight");
        Assert.Equal(CombatBoard.Nothing, play.CurrentBoards[view]);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL2_TheAbolethIsAboleth_NoRef_InEveryViewAtEveryStep(string view)
    {
        foreach (var step in play.Steps.SkipWhile(s => s.Step != "B2"))
        {
            var aboleth = Assert.Single(step.Boards[view].Rows, r => r.Name == "Aboleth");
            Assert.Equal(((string?)null, false), (aboleth.Ref, aboleth.PartySide));
            Assert.Equal(((int?)null, (int?)null, (int?)null, (int?)null), (aboleth.Hp, aboleth.MaxHp, aboleth.Ac, aboleth.Exhaustion));
        }
    }

    [Fact]
    public void OnePiece_BL3_ThePartySeesTheAbolethUnhurtHurtBloodiedFromB15Down_NeverANumber()
    {
        string Word(string step) => play[step].Boards["party"].Rows.Single(r => r.Name == "Aboleth").HpWord!;

        Assert.Equal(
            [BoardHpWords.Unhurt, BoardHpWords.Hurt, BoardHpWords.Hurt, BoardHpWords.Hurt, BoardHpWords.Bloodied, BoardHpWords.Bloodied, BoardHpWords.Bloodied, BoardHpWords.Bloodied, BoardHpWords.Down],
            new[] { "B4", "B5", "B10", "B12", "B15", "B16", "B19", "B23", "B26" }.Select(Word));
        Assert.All(play.Steps.TakeWhile(s => s.Step != "B15"), s => Assert.NotEqual(BoardHpWords.Bloodied, s.Boards["party"].Rows.SingleOrDefault(r => r.Name == "Aboleth")?.HpWord));
    }

    [Fact]
    public void OnePiece_BL3_ThePartysOwnRows_HaveTheirNumbers_ExhaustionDeathSavesConditionsByName()
    {
        var b15 = play["B15"].Boards["party"];
        Assert.Equal(["The fishman monk", "Aboleth", "The amethyst Dragon Slayer", "Björn Mountainfell"], b15.Rows.Select(r => r.Name));
        var bjorn = b15.Rows[3];
        Assert.Equal(("character:bjorn-mountainfell", 67, 85, 15, 1), (bjorn.Ref, bjorn.Hp!.Value, bjorn.MaxHp!.Value, bjorn.Ac!.Value, bjorn.Exhaustion!.Value));
        Assert.Equal(["Rage", "grappled"], bjorn.Conditions.Order(StringComparer.Ordinal));
        Assert.Equal(["cursed (Mucus Cloud)", "grappled"], b15.Rows[0].Conditions.Order(StringComparer.Ordinal));

        var monk = play["B20"].Boards["party"].Rows.Single(r => r.Name == "The fishman monk");
        Assert.Equal((0, new DeathSaveTally(0, 2, false)), (monk.Hp!.Value, monk.DeathSaves));
        var healed = play["B22"].Boards["party"].Rows.Single(r => r.Name == "The fishman monk");
        Assert.Equal((7, (DeathSaveTally?)null), (healed.Hp!.Value, healed.DeathSaves));
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_TheBoardAfterEveryStep_IsTheWhitelist(string view)
    {
        foreach (var step in play.Steps)
        {
            var board = step.Boards[view];
            Assert.Equal((true, step.Persisted.Round, false), (board.Shown, board.Round, board.Ended));
            Assert.Equal(Enumerable.Range(1, board.Rows.Count), board.Rows.Select(r => r.Number));
            Assert.Equal(TurnRow(step), board.Rows.ToList().FindIndex(r => r.Turn));
            Assert.All(board.Rows.Where(r => !r.PartySide), r => Assert.Equal(((int?)null, (int?)null, (int?)null, (int?)null), (r.Hp, r.MaxHp, r.TempHp, r.Ac)));
            Assert.All(board.Rows.Where(r => !r.PartySide), r => Assert.NotNull(r.HpWord));
            Assert.All(board.Rows.Where(r => r.PartySide), r => Assert.Null(r.HpWord));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B-L1, B-L4: session 13 and everything after the end

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL1_Session13_TheDmsRollsStayBehindTheScreen_OnlyThePotionRollIsShown(string view)
    {
        SessionDetail session;
        try
        {
            session = new SessionReader(play.World.Database).Get(play.World.Campaign, "13", Perspective.Parse(view));
        }
        catch (DndInputException ex)
        {
            // Outside the party (the public, the NPCs) the session itself is not shown: the refusal names nothing.
            Assert.False(IsPlayerView(view) && view != "public", $"{view} should see session 13");
            LeakAssert.CleanMessage(ex.Message, "13", Forbidden(view), $"{view}: the refusal of session 13");
            return;
        }

        var roll = Assert.Single(session.DiceRolls);
        Assert.Equal(("2d4+2", "Björn Mountainfell: heal", 7L, (bool?)null, (string?)null), (roll.Expression, roll.Label, roll.Total, roll.Secret, roll.Detail));
        Assert.Equal(1, session.DiceRollsTotal);
        Assert.Null(session.Author);
        LeakAssert.Clean(session, Forbidden(view), $"session 13 as {view}");
        LeakAssert.Clean(new SessionReader(play.World.Database).List(play.World.Campaign, perspective: Perspective.Parse(view)), Forbidden(view), $"the sessions as {view}");
    }

    [Fact]
    public void OnePiece_BL1_TheAuthorsSession13_ListsTheThreeRollsSecretMarked_AndTheWriteBack()
    {
        var session = new SessionReader(play.World.Database).Get(play.World.Campaign, "13");

        Assert.Equal(
            [("Aboleth: heal", true), ("Aboleth: damage (critical)", true), ("Björn Mountainfell: heal", false)],
            session.DiceRolls.Select(d => (d.Label!, d.Secret!.Value)));
        Assert.Empty(session.Author!.LiveLog);
        Assert.Equal([play.SessionStart.BatchId, play.End.BatchId], session.Author.Changes.Select(c => c.BatchId));
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL4_EverySheetReadAfterTheEnd_NothingOfTheFightsAuthorText(string view)
    {
        var forbidden = new List<string>(Secrets(view));
        forbidden.AddRange(CombatScenarioLeaks.OnePieceNester);
        forbidden.AddRange(CombatScenarioLeaks.OnePieceSheet);
        var reader = new SheetReader(play.World.Database);
        var entities = new ScenarioReads(play.World.Database);
        var reads = new List<object>();
        foreach (var slug in new[] { "bjorn-mountainfell", "fishman-monk", "dragon-slayer", "the-nester", "protector" })
        {
            var handle = play.World.F.Entity(play.World.Campaign, "character:" + slug).SeqHandle;
            if (entities.TryGet(play.World.Campaign, view, handle, new EntityIncludes(Sheet: true)) is { } get)
            {
                reads.Add(get);
            }

            try
            {
                reads.Add(reader.Get(play.World.Campaign, handle, Perspective.Parse(view)));
            }
            catch (DndInputException ex)
            {
                LeakAssert.CleanMessage(ex.Message, handle, forbidden, $"{view}: the refusal for {handle}");
            }
        }

        reads.Add(reader.Get(play.World.Campaign, null, Perspective.Parse(view)));
        LeakAssert.Clean(reads, forbidden, $"{view} reads the sheets after the fight");
    }

    [Fact]
    public void OnePiece_BL4_ThePartysLines_TheWrittenBackHpExhaustionAndTheCurseByNameOnly()
    {
        var lines = new SheetReader(play.World.Database).Get(play.World.Campaign, null, Party).Characters.Select(c => c.Line!).ToDictionary(l => l.Name);

        Assert.Equal((51, 85, 1), (lines["Björn Mountainfell"].Hp!.Value, lines["Björn Mountainfell"].EffectiveMaxHp!.Value, lines["Björn Mountainfell"].Exhaustion));
        Assert.Empty(lines["Björn Mountainfell"].Conditions);
        Assert.Equal((7, 59), (lines["The fishman monk"].Hp!.Value, lines["The fishman monk"].EffectiveMaxHp!.Value));
        Assert.Equal(["cursed (Mucus Cloud)"], lines["The fishman monk"].Conditions);
        Assert.Equal((56, 68), (lines["The amethyst Dragon Slayer"].Hp!.Value, lines["The amethyst Dragon Slayer"].EffectiveMaxHp!.Value));
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void OnePiece_BL4_EverythingThePhase6ReadersReachAfterTheFight_HoldsNothingOfIt(string view)
    {
        var reach = PerspectiveReach.Collect(new ScenarioReads(play.World.Database), play.World.F, play.World.Campaign, view);

        foreach (var result in reach.Results)
        {
            LeakAssert.Clean(result, [.. Secrets(view), .. CombatScenarioLeaks.OnePieceNester, .. CombatScenarioLeaks.OnePieceReach], $"{result.GetType().Name} as {view} after the fight");
        }
    }

    [Fact]
    public void OnePiece_TheSweepsOwnLists_EveryFightSheetReachAndNpcStringIsHeldByTheAuthorOrTheStore()
    {
        // A forbidden string nothing could ever contain proves nothing in a sweep: each of these is in the author's own
        // results or the store after the fight, so its absence from every view above is the whitelist at work.
        var sheets = new SheetReader(play.World.Database);
        var held = string.Join("\n",
        [
            .. play.Steps.Select(s => LeakAssert.Serialize(s.Outcome)),
            LeakAssert.Serialize(play.End),
            LeakAssert.Serialize(play.EndRows),
            LeakAssert.Serialize(new SessionReader(play.World.Database).Get(play.World.Campaign, "13")),
            .. CombatOnePiecePlay.Present.Select(handle => LeakAssert.Serialize(sheets.Get(play.World.Campaign, handle))),
            LeakAssert.Serialize(play.SheetRowsAfter),
            play.DumpAfterEnd,
        ]);

        string[][] lists =
        [
            [.. CombatScenarioLeaks.OnePieceFight], [.. CombatScenarioLeaks.OnePieceSheet], [.. CombatScenarioLeaks.OnePieceReach],
            [.. CombatScenarioLeaks.OnePieceNester], [.. CombatScenarioLeaks.OnePieceNpcBaseline],
        ];
        foreach (var word in lists.SelectMany(l => l))
        {
            Assert.True(held.Contains(word, StringComparison.OrdinalIgnoreCase), $"\"{word}\" is held nowhere, so no sweep can find it");
        }
    }

    [Fact]
    public void OnePiece_BL2_AnUntilRemovedEffectNamingTheNester_IsWrittenToTheSheet_EveryPlayerLineReadsAnEffect()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w, until: "B9");
        var marked = w.Combat.Condition(w.Campaign, null, new ConditionOp(["fishman-monk"]) { Add = ["Nester's mark"], Source = "aboleth", Duration = "until removed" });
        Assert.Contains(marked.Warnings, x => x.Contains("Nester's mark", StringComparison.Ordinal));
        var during = w.Reader.Board(w.Campaign, Party).Rows.Single(r => r.Name == "The fishman monk");
        Assert.Contains(BoardNames.Effect, during.Conditions);

        w.Combat.End(w.Campaign, null, new EndRequest { Outcome = "The Nester withdrew (probe)." });

        var mark = w.SheetOf("character:fishman-monk").Conditions.Single(c => c.Name == "Nester's mark");
        Assert.Equal(("Aboleth", "until_removed"), (mark.Source, mark.Duration));
        var reader = new SheetReader(w.Database);
        Assert.Contains(reader.Get(w.Campaign, "character:fishman-monk").Characters.Single().Author!.Sheet.Conditions, c => c.Name == "Nester's mark");
        foreach (var view in CombatOnePiecePlay.NonAuthorPerspectives)
        {
            var read = reader.Get(w.Campaign, null, Perspective.Parse(view));
            LeakAssert.Clean(read, [.. Secrets(view), .. CombatScenarioLeaks.OnePieceNester], $"{view} reads the sheets after the mark");
            if (read.Characters.SingleOrDefault(c => c.Name == "The fishman monk")?.Line is { } line)
            {
                Assert.Equal(["cursed (Mucus Cloud)", BoardNames.Effect], line.Conditions);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B-L7 / P1-P3: the disguised Protector, in a world of its own

    [Fact]
    public void OnePiece_P1_P3_BL7_TheDisguisedProtectorIsTheAdvisorInSerret_StunnedUntilRound2Ends_WraithBladesUnknown()
    {
        using var w = CombatWorld.OnePiece();
        w.StartSession(13);
        w.Reload();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Serret rooftop (probe)" });
        var p1 = w.Combat.Add(w.Campaign, null,
        [
            new CombatantRequest { Character = "character:protector", Side = "ally" },
            new CombatantRequest { Name = "Wraith Blade", Ac = 15, Hp = HpChoice.Of(33), Count = 3, Side = "enemy" },
        ]);
        var p2 = w.Combat.Condition(w.Campaign, null, new ConditionOp(["protector"]) { Add = ["stunned"], Duration = "end of round 2" });
        w.Roller.Push(9, 14, 6);
        var initiative = w.Combat.Initiative(w.Campaign, null, new InitiativeOp
        {
            Rolls = [new("fishman-monk", Total: 19), new("dragon-slayer", Total: 11), new("bjorn-mountainfell", Total: 8)],
        });

        // P1: the author's tracker names the Protector; a typed name the party view would not show is warned about.
        Assert.Equal(["Björn Mountainfell", "The amethyst Dragon Slayer", "The fishman monk", "The Protector", "Wraith Blade", "Wraith Blade 2", "Wraith Blade 3"],
            w.State("Serret rooftop (probe)").Combatants.Select(c => c.Name));
        Assert.Contains(p1.Warnings, x => x.Contains("'Wraith Blade'", StringComparison.Ordinal));
        Assert.Equal(new ConditionExpiry(2, CombatValues.ExpiryPoints.RoundEnd, null), w.Combatant("Serret rooftop (probe)", "The Protector").Conditions.Single().Expires);

        // The two server rolls (the Wraith Blades' group; the stunned Protector's, with 2024 Disadvantage for the
        // Incapacitated) are secret and name neither.
        Assert.Equal([("1d20", "a combatant: initiative", 1L), ("2d20kl1", "a combatant: initiative", 1L)], w.Dice().Select(d => (d.Expression, d.Label!, d.Secret)).Order());
        Assert.Equal(2, initiative.Rolls.Count);
        Assert.Equal(0, w.Roller.Left);

        var boards = new List<(string Step, string View, CombatBoard Board)>();
        void Read(string step)
        {
            foreach (var view in CombatOnePiecePlay.NonAuthorPerspectives)
            {
                boards.Add((step, view, w.Reader.Board(w.Campaign, Perspective.Parse(view))));
            }
        }

        Read("P2");
        var turns = new List<CombatOutcome>();
        while (w.State("Serret rooftop (probe)").Round < 3)
        {
            turns.Add(w.Combat.Next(w.Campaign, null));
            Read($"next {turns.Count}");
        }

        // P2: the stun ends as round 2 ends, said once, at the wrap into round 3.
        var wrap = turns[^1];
        Assert.Equal(3, wrap.Encounter.Round);
        Assert.Single(turns.SelectMany(t => t.Reminders), r => r.Kind == K.Expired && r.Text.Contains("stunned ended on The Protector", StringComparison.Ordinal));
        Assert.Contains(wrap.Reminders, r => r.Kind == K.Expired && r.Text.Contains("stunned", StringComparison.Ordinal));
        Assert.Empty(w.Combatant("Serret rooftop (probe)", "The Protector").Conditions);

        // P3 / B-L7: the party knows him only as the advisor, by an e: ref; the Wraith Blades are unknown creatures.
        var party = boards.First(b => b.Step == "P2" && b.View == "party").Board;
        var advisor = Assert.Single(party.Rows, r => r.Name == "the advisor in Serret");
        Assert.StartsWith("e:", advisor.Ref, StringComparison.Ordinal);
        Assert.Equal((false, BoardHpWords.Unhurt), (advisor.PartySide, advisor.HpWord));
        Assert.Equal(["stunned"], advisor.Conditions);
        Assert.Equal(["an unknown creature", "an unknown creature 2", "an unknown creature 3"], party.Rows.Where(r => r.Ref is null).Select(r => r.Name));
        Assert.Empty(boards.Last(b => b.View == "party").Board.Rows.Single(r => r.Name == "the advisor in Serret").Conditions);
        Assert.Contains(w.Reader.State(w.Campaign).Encounter!.Rows, r => r.Name == "The Protector");

        foreach (var (step, view, board) in boards)
        {
            LeakAssert.Clean(board, [.. Secrets(view), .. CombatScenarioLeaks.OnePieceNester, "Wraith", "Serret rooftop", "(probe)", "end of round", "combat {"], $"the {view} board at {step}");
        }

        foreach (var view in CombatOnePiecePlay.PlayerViews.Where(v => v != "public"))
        {
            var session = new SessionReader(w.Database).Get(w.Campaign, "13", Perspective.Parse(view));
            Assert.Empty(session.DiceRolls);
            LeakAssert.Clean(session, [.. CombatScenarioLeaks.OnePiece, "Wraith", "a combatant"], $"session 13 as {view} during the probe");
        }

        Assert.Empty(p2.Warnings);
    }

    /// <summary>
    /// The board row the turn marker belongs on (§6.12): no fight shows a hidden combatant here, so it is the turn-holder's
    /// place in the order the rows follow; before initiative (round 0) there is none (-1).
    /// </summary>
    private static int TurnRow(CombatStepRecord step) =>
        step.Persisted.TurnHolder is { } holder ? step.Persisted.Order.ToList().FindIndex(c => c.Id == holder.Id) : -1;
}
