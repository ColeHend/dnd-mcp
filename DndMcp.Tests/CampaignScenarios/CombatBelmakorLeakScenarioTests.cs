using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignCombat;
using DndMcp.Tests.CampaignRead;
using Xunit;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// Fixture A's leak rules at the Repository (FIX §5.1 as amended by contract §16; §6.10, §6.12, §7.4, D10) for every
/// non-author view of the Belmakor PLAYER campaign (the dm is one): each read serialises the WHOLE model the reader
/// returned (<see cref="LeakAssert"/>) and none of FIX's strings, the fight's author-only text or the sheets' author fields
/// may occur in it. Across the WHOLE fight: the board of every view after every step (A-L1/A-L7: no "Keras", nothing of One
/// Piece; A-L5: no author text; §6.12's whitelist: monsters by name with one HP word, numbers only on party rows shown
/// under their own name; the public and the old king, who do not see the party, read every PC as "an unknown creature N"
/// and never a PC's name or handle). After the end: the ended board (shown, ended, every row), session 4 (A-L6: combat
/// appended nothing, no dice: every value was given; nine views read it), the sheets (A-L4: never the player, the
/// concentration it wrote or any author field) and everything the Phase 6 readers reach. The probes (A-L1, A-L2, A-L3, A-L5) run in their own world after the fixture's end (D13: one
/// active fight): the old king linked to a lich is "The Old King"; a lich the author TYPED as "Keras" is not linked and is
/// "Lich" to every view, its rolls "Lich: initiative"; the ambition typed into an effect, an outcome and a reason reaches
/// only the views that know it.
/// </summary>
public sealed class CombatBelmakorLeakScenarioTests(CombatBelmakorPlay play) : IClassFixture<CombatBelmakorPlay>
{
    private static readonly Perspective Party = Perspective.Parse("party");

    /// <summary>The views session 4 is not shown to: the public, dead Tristan (absent) and the old king.</summary>
    private static readonly string[] NotReadingSession4 = ["public", "character:tristan", "character:old-king"];

    public static TheoryData<string> Views() => new(BelmakorScenario.NonAuthorPerspectives);

    private static IReadOnlyList<string> Forbidden(string view) => [.. CombatScenarioLeaks.Belmakor(view), .. CombatScenarioLeaks.BelmakorFight];

    // ------------------------------------------------------------------------------------------------------------------
    // The whole fight, every view

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AL1_AL5_AL7_TheBoardAfterEveryStep_HoldsNoSecretAndNoAuthorText(string view)
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

    /// <summary>The views that do not see the party: every PC is a stand-in there ("an unknown creature N", K's ruling 6).</summary>
    public static TheoryData<string> StandInViews() => new("public", "character:old-king");

    [Theory]
    [MemberData(nameof(StandInViews))]
    public void Belmakor_TheViewsThatCannotSeeTheParty_EveryPcIsAStandIn_NoNameNoHandleNoNumber_AtEveryStepAndOnTheEndedBoard(string view)
    {
        var boards = play.Steps.Select(s => (s.Step, Board: s.Boards[view])).Append(("the end", play.LastBoards[view]));
        var party = play.Steps[^1].Persisted.Combatants.Where(c => c.Side == CampaignValues.CombatSides.Party).ToList();
        Assert.Equal(6, party.Count);

        // Every stand-in's real name (each word of it) and handle, and any handle at all: none of them may be on the board.
        string[] forbidden = [.. party.SelectMany(c => c.Name.Split(' ')), .. party.Select(c => c.EntityHandle!), "character:"];
        foreach (var (step, board) in boards)
        {
            var persisted = step == "the end" ? play.Steps[^1].Persisted : play[step].Persisted;
            var shown = Shown(persisted);
            Assert.Equal(shown.Count, board.Rows.Count);
            for (var i = 0; i < shown.Count; i++)
            {
                var row = board.Rows[i];
                if (shown[i].Side != CampaignValues.CombatSides.Party)
                {
                    continue;
                }

                Assert.StartsWith(BoardNames.UnknownCreature, row.Name, StringComparison.Ordinal);
                Assert.Equal((false, (string?)null, (int?)null, (int?)null, (int?)null, (int?)null, (DeathSaveTally?)null, false, (string?)null),
                    (row.PartySide, row.Ref, row.Hp, row.MaxHp, row.TempHp, row.Ac, row.DeathSaves, row.Concentrating, row.Concentration));
                Assert.NotNull(row.HpWord);
            }

            Assert.Equal(6, board.Rows.Count(r => r.Name.StartsWith(BoardNames.UnknownCreature, StringComparison.Ordinal)));
            LeakAssert.Clean(board, forbidden, $"the {view} board at {step}");
        }
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_TheBoardAfterEveryStep_IsTheWhitelist_MonstersOneHpWord_NumbersOnlyUnderTheirOwnName(string view)
    {
        foreach (var step in play.Steps)
        {
            var board = step.Boards[view];
            Assert.True(board.Shown, $"{view} after {step.Step}");
            Assert.Equal((step.Persisted.Round, false), (board.Round, board.Ended));
            Assert.Equal(Enumerable.Range(1, board.Rows.Count), board.Rows.Select(r => r.Number));
            Assert.Equal(TurnRow(step), board.Rows.ToList().FindIndex(r => r.Turn));
            Assert.Equal(step.Persisted.Combatants.Count, board.Rows.Count);
            foreach (var row in board.Rows)
            {
                if (row.PartySide)
                {
                    Assert.Null(row.HpWord);
                    Assert.DoesNotContain(BoardNames.UnknownCreature, row.Name, StringComparison.Ordinal);
                }
                else
                {
                    Assert.NotNull(row.HpWord);
                    Assert.Equal(((int?)null, (int?)null, (int?)null, (int?)null, (int?)null, (DeathSaveTally?)null),
                        (row.Hp, row.MaxHp, row.TempHp, row.Ac, row.Exhaustion, row.DeathSaves));
                }

                if (row.Name.StartsWith("Mummy", StringComparison.Ordinal))
                {
                    Assert.Equal((false, (string?)null), (row.PartySide, row.Ref));
                }
            }
        }
    }

    [Fact]
    public void Belmakor_AL3_PartyBoard_TheMonstersReadUnhurtBloodiedDown_NeverTheirNumbers()
    {
        string[] Words(string step) => [.. play[step].Boards["party"].Rows.Where(r => r.Name.StartsWith("Mummy", StringComparison.Ordinal)).Select(r => r.Name + ": " + r.HpWord)];

        Assert.Equal(["Mummy Lord: unhurt", "Mummy: unhurt", "Mummy 2: unhurt"], Words("A2"));
        Assert.Equal(["Mummy Lord: bloodied", "Mummy: bloodied", "Mummy 2: bloodied"], Words("A14"));
        Assert.Equal(["Mummy Lord: bloodied", "Mummy: bloodied", "Mummy 2: down"], Words("A21"));
        Assert.Equal(["Mummy Lord: down", "Mummy: bloodied", "Mummy 2: down"], Words("A29"));
        Assert.Equal(["Mummy Lord: down", "Mummy: down", "Mummy 2: down"], Words("A31"));
    }

    [Fact]
    public void Belmakor_PartyBoard_ThePartysOwnNumbers_BladesongAc_CircleOfPower_TorchsTalliesOnlyWhileDying()
    {
        var a8 = play["A8"].Boards["party"].Rows.Single(r => r.Name == "Belmakor Silverwind");
        Assert.Equal((true, "character:belmakor", 82, 110, 0, 22), (a8.PartySide, a8.Ref, a8.Hp!.Value, a8.MaxHp!.Value, a8.TempHp!.Value, a8.Ac!.Value));
        Assert.Equal((true, "Circle of Power"), (a8.Concentrating, a8.Concentration));
        Assert.Equal(["Bladesong"], a8.Conditions);

        var a23 = play["A23"].Boards["party"].Rows.Single(r => r.Name == "Lieutenant James Torch");
        Assert.Equal((0, new DeathSaveTally(0, 2, false)), (a23.Hp!.Value, a23.DeathSaves));
        Assert.Contains("unconscious", a23.Conditions);
        var a25 = play["A25"].Boards["party"].Rows.Single(r => r.Name == "Lieutenant James Torch");
        Assert.Equal((1, (DeathSaveTally?)null), (a25.Hp!.Value, a25.DeathSaves));
        Assert.Equal(["frightened", "prone"], a25.Conditions.Order(StringComparer.Ordinal));

        // The four sheets with no HP are party rows with no numbers to show, never an HP word.
        var vars = play["A3"].Boards["party"].Rows.Single(r => r.Name == "Vars Nocturne");
        Assert.Equal((true, (int?)null, (string?)null), (vars.PartySide, vars.Hp, vars.HpWord));
        Assert.True(vars.Turn);
    }

    [Fact]
    public void Belmakor_PublicBoard_ThePartyItCannotSeeIsUnknownCreatures_WithHpWordsOnly()
    {
        var rows = play["A25"].Boards["public"].Rows;

        Assert.Equal(6, rows.Count(r => r.Name.StartsWith(BoardNames.UnknownCreature, StringComparison.Ordinal)));
        Assert.All(rows, r => Assert.False(r.PartySide));
        Assert.All(rows, r => Assert.Null(r.Ref));
        Assert.Equal(["Mummy Lord", "Mummy", "Mummy 2"], rows.Where(r => r.Name.StartsWith("Mummy", StringComparison.Ordinal)).Select(r => r.Name));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // After the end: session 4, the sheets, everything the Phase 6 readers reach

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AL4_AL6_Session4_NoDiceNoLiveLogNoAuthorPart_NothingOfTheFightOrTheSheets(string view)
    {
        var sessions = new SessionReader(play.World.Database);
        var perspective = Perspective.Parse(view);

        SessionDetail? four = null;
        try
        {
            four = sessions.Get(play.World.Campaign, "4", perspective);
        }
        catch (DndInputException ex)
        {
            // Only the views that did not attend (and know nothing of it) are refused: the refusal names nothing.
            Assert.True(NotReadingSession4.Contains(view), $"{view} should read session 4");
            LeakAssert.CleanMessage(ex.Message, "4", Forbidden(view), $"{view}: the refusal of session 4");
        }

        if (four is not null)
        {
            Assert.DoesNotContain(view, NotReadingSession4);
            Assert.Equal(6, four.Attendance.Count(a => a.Present));
            Assert.Empty(four.DiceRolls);
            Assert.Equal(0, four.DiceRollsTotal);
            Assert.Null(four.Author);
            Assert.Null(four.Recap);
            LeakAssert.Clean(four, [.. Forbidden(view), .. CombatScenarioLeaks.BelmakorReach], $"session 4 as {view}");
        }

        LeakAssert.Clean(sessions.List(play.World.Campaign, perspective: perspective), [.. Forbidden(view), .. CombatScenarioLeaks.BelmakorReach], $"the sessions as {view}");
    }

    [Fact]
    public void Belmakor_AL6_Session4_NineViewsReadIt_ThePublicTristanAndTheOldKingAreRefused()
    {
        // The sweep above is meaningful only while views can read the session: count them, so a reader that refused every
        // non-author view (and so passed the sweep on its refusals alone) shows here.
        var sessions = new SessionReader(play.World.Database);
        var readers = BelmakorScenario.NonAuthorPerspectives.Where(view =>
        {
            try
            {
                sessions.Get(play.World.Campaign, "4", Perspective.Parse(view));
                return true;
            }
            catch (DndInputException)
            {
                return false;
            }
        }).ToList();

        Assert.Equal(9, readers.Count);
        Assert.Equal(BelmakorScenario.NonAuthorPerspectives.Except(NotReadingSession4), readers);
    }

    [Fact]
    public void Belmakor_AL6_TheAuthorsSession4_TheLiveLogIsEmpty_TheChangesAreTheStartAndTheWriteBack()
    {
        var four = new SessionReader(play.World.Database).Get(play.World.Campaign, "4");

        Assert.Empty(four.Author!.LiveLog);
        Assert.Null(four.Recap);
        Assert.Empty(four.DiceRolls);
        Assert.Equal([play.SessionStart.BatchId, play.End.BatchId], four.Author.Changes.Select(c => c.BatchId));
        Assert.Equal("[]", play.World.F.Scalar<string>("SELECT live_log FROM session WHERE entity_id = @id", new { id = play.SessionId }));
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_AL4_EverySheetReadAfterTheWriteBack_NeverThePlayerTheConcentrationNorAnAuthorField(string view)
    {
        var forbidden = new List<string>(CombatScenarioLeaks.Belmakor(view)) { "remaining_rounds" };
        forbidden.AddRange(CombatScenarioLeaks.BelmakorSheet);
        var reader = new SheetReader(play.World.Database);
        var entities = new ScenarioReads(play.World.Database);
        var reads = new List<object>();
        foreach (var slug in new[] { "belmakor", "vars", "ignis", "serif", "torch", "aiden-ironstar", "tristan", "old-king" })
        {
            var handle = play.World.F.Entity(play.World.Campaign, "character:" + slug).SeqHandle;
            if (entities.TryGet(play.World.Campaign, view, handle, new EntityIncludes(Sheet: true)) is { } get)
            {
                reads.Add(get);
            }

            if (entities.TryGet(play.World.Campaign, view, handle, new EntityIncludes(Sheet: true), asOf: 4) is { } asOf)
            {
                reads.Add(asOf);
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

        reads.Add(reader.Party(play.World.Campaign, Perspective.Parse(view)));
        LeakAssert.Clean(reads, forbidden, $"{view} reads the sheets after the fight");
    }

    [Fact]
    public void Belmakor_AL4_ThePartysLines_TheWrittenBackHp_NoConcentrationNoResourceNoCondition()
    {
        var lines = new SheetReader(play.World.Database).Party(play.World.Campaign, Party).Characters.Select(c => c.Line!).ToDictionary(l => l.Name);

        Assert.Equal((82, 110, 0, 17), (lines["Belmakor Silverwind"].Hp!.Value, lines["Belmakor Silverwind"].EffectiveMaxHp!.Value, lines["Belmakor Silverwind"].TempHp, lines["Belmakor Silverwind"].Ac!.Value));
        Assert.Empty(lines["Belmakor Silverwind"].Conditions);
        Assert.Equal((1, 74), (lines["Lieutenant James Torch"].Hp!.Value, lines["Lieutenant James Torch"].EffectiveMaxHp!.Value));
        Assert.DoesNotContain("Tristan", lines.Keys);
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void Belmakor_EverythingThePhase6ReadersReachAfterTheFight_HoldsNothingOfIt(string view)
    {
        var reach = PerspectiveReach.Collect(new ScenarioReads(play.World.Database), play.World.F, play.World.Campaign, view);

        foreach (var result in reach.Results)
        {
            LeakProbe.CleanExceptToldFacts(result, [.. CombatScenarioLeaks.Belmakor(view), .. CombatScenarioLeaks.BelmakorReach],
                BelmakorScenario.ToldFacts(view), $"{result.GetType().Name} as {view} after the fight");
        }
    }

    [Fact]
    public void Belmakor_TheSweepsOwnLists_EveryFightSheetAndReachStringIsHeldByTheAuthorOrTheStore()
    {
        // A forbidden string nothing could ever contain proves nothing in a sweep: each of these is in the author's own
        // results or the store after the fight, so its absence from every view above is the whitelist at work.
        var held = string.Join("\n",
        [
            .. play.Steps.Select(s => LeakAssert.Serialize(s.Outcome)),
            LeakAssert.Serialize(play.End),
            LeakAssert.Serialize(play.EndRows),
            LeakAssert.Serialize(new SessionReader(play.World.Database).Get(play.World.Campaign, "4")),
            LeakAssert.Serialize(new SheetReader(play.World.Database).Get(play.World.Campaign, "character:belmakor")),
            LeakAssert.Serialize(play.SheetRowsAfter),
            play.DumpAfterEnd,
        ]);

        foreach (var word in CombatScenarioLeaks.BelmakorFight.Concat(CombatScenarioLeaks.BelmakorSheet).Concat(CombatScenarioLeaks.BelmakorReach))
        {
            Assert.True(held.Contains(word, StringComparison.OrdinalIgnoreCase), $"\"{word}\" is held nowhere, so no sweep can find it");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // The probes, after the fixture's end, in a world of their own

    [Fact]
    public void Belmakor_AL1_AL2_AL3_TheOldKingLinkedIsTheOldKing_ALichTypedKerasIsLich_ToEveryViewAndEveryLabel()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w);
        CombatScripts.EndA(w);
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Old king rematch (probe)", AddParty = false });

        var linked = w.Combat.Add(w.Campaign, null, [CombatWorld.Monster("2014", "lich", character: "character:old-king", hp: HpChoice.Avg)]);
        var typed = w.Combat.Add(w.Campaign, null, [CombatWorld.Monster("2014", "lich", name: "Keras")]);
        w.Roller.Push(11, 6, 3, 4);
        var initiative = w.Combat.Initiative(w.Campaign, null, new InitiativeOp());
        w.Combat.Damage(w.Campaign, null, new DamageOp(["keras"]) { Dice = "1d6", DamageType = "necrotic", Source = "character:old-king" });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["character:old-king"]) { Dice = "1d6", DamageType = "cold", Source = "keras" });

        // A-L1: linked through the handle, named for the stat block in the author's tracker.
        var state = w.State("Old king rematch (probe)");
        var king = state.Combatants.Single(c => c.EntityId == w.Id("character:old-king"));
        Assert.Equal(("Lich", "character:old-king"), (king.Name, king.EntityHandle));
        Assert.Empty(linked.Warnings);

        // A-L2: a typed name is never matched to an entity; the author is warned what the party sees instead.
        var keras = state.Combatants.Single(c => c.Name == "Keras");
        Assert.Null(keras.EntityId);
        var warning = Assert.Single(typed.Warnings);
        Assert.Contains("'Keras'", warning, StringComparison.Ordinal);
        Assert.Contains("'Lich'", warning, StringComparison.Ordinal);

        // A-L3: the labels are the view-safe names; both rolls are open (the old king is known to the party).
        Assert.Equal(
            [("The Old King: initiative", 0L), ("Lich: initiative", 0L), ("The Old King: damage", 0L), ("Lich: damage", 0L)],
            w.Dice().Select(d => (d.Label!, d.Secret)));
        Assert.All(w.Dice(), d => Assert.Equal(w.Id("session:4"), d.SessionId));
        Assert.Equal(["The Old King: initiative", "Lich: initiative"], initiative.Rolls.Select(r => r.Label));

        foreach (var view in BelmakorScenario.NonAuthorPerspectives)
        {
            var board = w.Reader.Board(w.Campaign, Perspective.Parse(view));
            var session = TrySession(w, view, "4");
            LeakAssert.Clean(board, Forbidden(view), $"the {view} board of the probe");
            LeakAssert.Clean(session, CombatScenarioLeaks.Belmakor(view), $"session 4 as {view} after the probe's rolls");
            Assert.Contains(board.Rows, r => r.Name == "Lich" && r.Ref is null);
        }

        var party = w.Reader.Board(w.Campaign, Party);
        Assert.Equal([("The Old King", "character:old-king"), ("Lich", null)], party.Rows.Select(r => (r.Name, r.Ref)));
        var mine = w.Reader.Board(w.Campaign, Perspective.Parse("character:belmakor"));
        Assert.Equal([("The Old King", "character:old-king"), ("Lich", null)], mine.Rows.Select(r => (r.Name, r.Ref)));
        Assert.Equal(
            ["The Old King: initiative", "Lich: initiative", "The Old King: damage", "Lich: damage"],
            TrySession(w, "party", "4")!.DiceRolls.Select(d => d.Label));
    }

    [Fact]
    public void Belmakor_AL5_TheAmbitionTypedIntoAnEffectAnOutcomeAndAReason_ReachesOnlyTheViewsThatKnowIt()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w);
        CombatScripts.EndA(w);
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Reclaim the blighted surface (probe)",
            Combatants = [CombatWorld.Monster("2014", "lich", character: "character:old-king", hp: HpChoice.Avg)],
        });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["character:old-king"]) { Add = ["reclaim the blighted surface"], Source = "belmakor" });
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["belmakor"]) { Spell = "Outstrips Ward" });
        var boards = BelmakorScenario.NonAuthorPerspectives.ToDictionary(v => v, v => w.Reader.Board(w.Campaign, Perspective.Parse(v)));
        var combatants = w.State("Reclaim the blighted surface (probe)").Combatants.Count;
        w.Combat.End(w.Campaign, null, new EndRequest { Outcome = "He means to reclaim the blighted surface; he outstrips them all.", Reason = "reclaim (probe)" });

        // The fight's name, its outcome and the batch's reason are the author's in every view; the effect and the spell the
        // two views that know the ambition may read (FIX §5.1: the ambition is excluded "except for character:belmakor and dm").
        string[] authorOnly = ["(probe)", "he means to", "them all"];
        foreach (var view in BelmakorScenario.NonAuthorPerspectives)
        {
            var forbidden = CombatScenarioLeaks.Belmakor(view);
            LeakAssert.Clean(boards[view], [.. forbidden, .. authorOnly], $"the {view} board of the probe");
            var ended = w.Reader.Board(w.Campaign, Perspective.Parse(view), EncounterResolver.Last);
            Assert.Equal((true, true, 0, combatants), (ended.Shown, ended.Ended, ended.Round, ended.Rows.Count));
            LeakAssert.Clean(ended, [.. forbidden, .. authorOnly], $"the {view} board of the ended probe");
            LeakAssert.Clean(TrySession(w, view, "4"), [.. forbidden, .. authorOnly, "surface", "Outstrips Ward"], $"session 4 as {view}");
        }

        var party = boards["party"].Rows;
        Assert.Equal(["an effect"], party.Single(r => r.Name == "The Old King").Conditions);
        Assert.Equal((true, (string?)null), (party.Single(r => r.Name == "Belmakor Silverwind").Concentrating, party.Single(r => r.Name == "Belmakor Silverwind").Concentration));
        var mine = boards["character:belmakor"].Rows;
        Assert.Equal(["reclaim the blighted surface"], mine.Single(r => r.Name == "The Old King").Conditions);
    }

    private static SessionDetail? TrySession(CombatWorld w, string view, string session)
    {
        try
        {
            return new SessionReader(w.Database).Get(w.Campaign, session, Perspective.Parse(view));
        }
        catch (DndInputException)
        {
            return null;
        }
    }

    /// <summary>
    /// The board row the turn marker belongs on (§6.12): no fight shows a hidden combatant here, so it is the turn-holder's
    /// place in the order the rows follow; before initiative (round 0) there is none (-1).
    /// </summary>
    private static int TurnRow(CombatStepRecord step) =>
        step.Persisted.TurnHolder is { } holder ? step.Persisted.Order.ToList().FindIndex(c => c.Id == holder.Id) : -1;

    /// <summary>
    /// The combatants in the order the board's rows follow (§6.12): the initiative order, then those still without an
    /// initiative in the order they were added (fixture A hides and removes nobody).
    /// </summary>
    private static List<CombatantState> Shown(EncounterState state) =>
        [.. state.Order, .. state.Combatants.Where(c => c.Initiative is null).OrderBy(c => c.OrderKey)];
}
