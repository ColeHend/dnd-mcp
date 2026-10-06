using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Combat;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Tests.CampaignRead;
using DndMcp.Tests.CampaignScenarios;
using Xunit;

namespace DndMcp.Tests.CampaignCombat;

/// <summary>
/// Invariant: the party board (contract §6.12) is a whitelist. It shows the active fight ("current") or an ended one, never
/// a planned or paused one, and says one thing (<see cref="CombatBoard.Nothing"/>) for every fight it may not show; hidden
/// and left combatants leave no trace (rows numbered 1..k over what is shown, copies renumbered per view, a hidden
/// turn-holder's marker on the last shown row before it); only party-side rows shown under their own name carry numbers,
/// every other row (a party-side stand-in included) one HP word;
/// names, effects and spells pass the view-text check or read as the monster's name, "an unknown creature" or "an effect";
/// an entity the view does not see is its stat block's name, a disguised one its known name with an <c>e:</c> ref. The
/// whole model, serialised, carries none of the fixtures' forbidden strings for any non-author view (B-L2, B-L3, B-L7,
/// A-L1, A-L2, A-L5, A-L7).
/// </summary>
public sealed class CombatBoardTests
{
    private static readonly Perspective Party = Perspective.Parse("party");

    /// <summary>FIX §5.2's list for One Piece's player views, plus fixture B's Nester.</summary>
    private static readonly IReadOnlyList<string> OnePieceForbidden =
    [
        "simulacrum", "founders", "fleet", "en masse", "seal", "Baal", "Keras", "Cage", "fragment", "Protector", "Peaceful",
        "Mistaken", "Dutiful", "lineage", "Sky-world", "return visit", "effective_level_offset", "Nester", "unmourned", "Void-touched",
    ];

    [Fact]
    public void FixtureB_PartyBoardInRound2_TheAbolethIsBloodiedWithNoNumbers_ThePcsHaveTheirs()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w, until: "B15-resistance");

        var board = w.Reader.Board(w.Campaign, Party);

        Assert.True(board.Shown);
        Assert.Equal((2, false), (board.Round, board.Ended));
        Assert.Equal([1, 2, 3, 4], board.Rows.Select(r => r.Number));
        Assert.Equal(["The fishman monk", "Aboleth", "The amethyst Dragon Slayer", "Björn Mountainfell"], board.Rows.Select(r => r.Name));
        var aboleth = board.Rows[1];
        Assert.Equal((false, (string?)null, BoardHpWords.Bloodied), (aboleth.PartySide, aboleth.Ref, aboleth.HpWord));
        Assert.Equal((null, null, null, null), (aboleth.Hp, aboleth.MaxHp, aboleth.Ac, aboleth.Exhaustion));
        var bjorn = board.Rows[3];
        Assert.Equal(("character:bjorn-mountainfell", 67, 85, 15, 1), (bjorn.Ref, bjorn.Hp, bjorn.MaxHp, bjorn.Ac, bjorn.Exhaustion));
        Assert.Contains("Rage", bjorn.Conditions);
        Assert.Contains("grappled", bjorn.Conditions);
        Assert.Contains("cursed (Mucus Cloud)", board.Rows[0].Conditions);
        Assert.True(board.Rows[0].Turn);
        foreach (var view in new[] { "party", "table", "public", "character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk" })
        {
            LeakAssert.Clean(w.Reader.Board(w.Campaign, Perspective.Parse(view)), OnePieceForbidden, $"the {view} board");
        }
    }

    [Fact]
    public void FixtureA_EveryNonAuthorView_TheBoardIsClean_AndLastShowsTheEndedFight()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w);
        foreach (var view in BelmakorScenario.NonAuthorPerspectives)
        {
            var board = w.Reader.Board(w.Campaign, Perspective.Parse(view));
            Assert.True(board.Shown, view);
            LeakAssert.Clean(board, BelmakorScenario.ForbiddenFor(view), $"the {view} board");
            Assert.DoesNotContain(board.Rows, r => r.Name.Contains("one-piece", StringComparison.OrdinalIgnoreCase));

            // A party member this view cannot see is a stand-in: an HP word, never the numbers of the sheet behind it.
            Assert.All(board.Rows.Where(r => r.Name.StartsWith(BoardNames.UnknownCreature, StringComparison.Ordinal)),
                r => Assert.Equal((false, (int?)null, (int?)null, (int?)null), (r.PartySide, r.Hp, r.MaxHp, r.Ac)));
        }

        Assert.Contains(w.Reader.Board(w.Campaign, Perspective.Parse("public")).Rows, r => r.Name.StartsWith(BoardNames.UnknownCreature, StringComparison.Ordinal));

        CombatScripts.EndA(w);
        var ended = w.Reader.Board(w.Campaign, Party, "last");

        Assert.Equal((true, true, 3), (ended.Shown, ended.Ended, ended.Round));
        Assert.DoesNotContain(ended.Rows, r => r.Turn);
        Assert.Equal(CombatBoard.Nothing, w.Reader.Board(w.Campaign, Party));
    }

    [Fact]
    public void PlannedPausedMissingOrNoFight_OneWording_TheSameNothing()
    {
        using var w = CombatWorld.Dm();
        Assert.Equal(CombatBoard.Nothing, w.Reader.Board(w.Campaign, Party));
        w.Combat.Prepare(w.Campaign, new PrepareRequest("The Villain's lair (probe)") { Combatants = [CombatWorld.Monster("2024", "ogre")] });

        Assert.Equal(CombatBoard.Nothing, w.Reader.Board(w.Campaign, Party, "The Villain's lair (probe)"));
        Assert.Equal(CombatBoard.Nothing, w.Reader.Board(w.Campaign, Party, "No such fight"));
        Assert.Equal(CombatBoard.Nothing, w.Reader.Board(w.Campaign, Party, "last"));
        using (var connection = w.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE encounter SET status = 'paused'";
            command.ExecuteNonQuery();
        }

        Assert.Equal(CombatBoard.Nothing, w.Reader.Board(w.Campaign, Party, "The Villain's lair (probe)"));
        Assert.Equal("No combat to show for this perspective.", CombatBoard.NothingText);
    }

    [Theory]
    [InlineData(false, new[] { "Mummy", "Mummy 2" })]
    [InlineData(true, new[] { "Mummy", "Mummy 2" })]
    public void HiddenCopy_LeavesNoTrace_RowsAndCopiesRenumbered(bool firstHidden, string[] expected)
    {
        using var w = CombatWorld.Dm("2014");
        var entries = firstHidden
            ? new[] { CombatWorld.Monster("2014", "mummy", hidden: true), CombatWorld.Monster("2014", "mummy", 2) }
            : new[] { CombatWorld.Monster("2014", "mummy", 2), CombatWorld.Monster("2014", "mummy", hidden: true) };
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Tomb", AddParty = false, Combatants = entries });
        Assert.Equal(["Mummy", "Mummy 2", "Mummy 3"], w.State("Tomb").Combatants.Select(c => c.Name));

        var board = w.Reader.Board(w.Campaign, Party);

        Assert.Equal(expected, board.Rows.Select(r => r.Name));
        Assert.Equal([1, 2], board.Rows.Select(r => r.Number));
        Assert.DoesNotContain("Mummy 3", LeakAssert.Serialize(board), StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenTurnHolder_TheMarkerIsOnTheLastShownRowBeforeIt_ElseOnNone()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Shadows",
            AddParty = false,
            Combatants =
            [
                new CombatantRequest { Name = "Scout", Hp = HpChoice.Of(10), Side = "ally" },
                new CombatantRequest { Name = "Assassin", Hp = HpChoice.Of(10), Hidden = true },
                new CombatantRequest { Name = "Guard", Hp = HpChoice.Of(10) },
            ],
        });
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("assassin", Total: 20), new("scout", Total: 15), new("guard", Total: 10)] });

        var first = w.Reader.Board(w.Campaign, Party);
        Assert.Equal("Assassin", w.State("Shadows").TurnHolder!.Name);
        Assert.DoesNotContain(first.Rows, r => r.Turn);
        Assert.Equal(["Scout", "Guard"], first.Rows.Select(r => r.Name));

        w.Combat.Set(w.Campaign, null, new SetOp([new SetEntry("assassin") { Hidden = false }]));
        w.Combat.Next(w.Campaign, null);
        w.Combat.Next(w.Campaign, null);
        w.Combat.Set(w.Campaign, null, new SetOp([new SetEntry("guard") { Hidden = true }]));

        var later = w.Reader.Board(w.Campaign, Party);
        Assert.Equal("Guard", w.State("Shadows").TurnHolder!.Name);
        Assert.Equal(["Assassin", "Scout"], later.Rows.Select(r => r.Name));
        Assert.True(later.Rows.Single(r => r.Name == "Scout").Turn);
        Assert.Single(later.Rows, r => r.Turn);
    }

    [Fact]
    public void Names_TypedOrCustom_PassTheCheckOrFallBack_EffectsLikewise()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Names",
            AddParty = false,
            Combatants =
            [
                CombatWorld.Monster("2024", "goblin-warrior", name: "Goblin Boss"),
                CombatWorld.Monster("2024", "ogre", name: "The Villain"),
                new CombatantRequest { Name = "Cultist", Hp = HpChoice.Of(9) },
                new CombatantRequest { Name = "The Villain's shadow", Hp = HpChoice.Of(9) },
            ],
        });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["cultist"]) { Add = ["Hex", "sealed by The Villain", "poisoned"], Source = "goblin-boss" });

        var board = w.Reader.Board(w.Campaign, Party);

        Assert.Equal(["Goblin Boss", "Ogre", "Cultist", "an unknown creature"], board.Rows.Select(r => r.Name));
        Assert.Equal(["Hex", "an effect", "poisoned"], board.Rows[2].Conditions);
        LeakAssert.Clean(board, ["Villain"], "the party board");
    }

    /// <summary>
    /// L02: a derived form or compound of a hidden name ("Kerasian", "Nesterling", "Nesterborn") or of a forbidden term
    /// ("Sealbreaker") reads like the name itself on the party board (the monster's name, "an unknown creature", "an
    /// effect", no spell name), and the author gets the D20b warning for each. F3 (review F2R02): One Piece records Baal in
    /// no name, only in fact statements (and q22's title, which gives no stem), so "Baal" is a possible name: "Baalite
    /// cultist" and "Baalward" are shown, and the author's step warns that the party may read the name in them.
    /// </summary>
    [Fact]
    public void Names_DerivedFormsOfHiddenNames_FallBackOnTheBoard_AndWarnTheAuthor_BothWorlds()
    {
        using var belmakor = CombatWorld.Belmakor();
        var start = belmakor.Combat.Start(belmakor.Campaign, new StartRequest
        {
            Name = "L02",
            AddParty = false,
            Combatants =
            [
                new CombatantRequest { Character = "character:torch" },
                new CombatantRequest { Name = "Kerasian sentinel", Hp = HpChoice.Of(20), Ac = 12, Side = "ally" },
                CombatWorld.Monster("2014", "lich", name: "Kerasian lich"),
            ],
        });
        var hex = belmakor.Combat.Condition(belmakor.Campaign, null, new ConditionOp(["torch"]) { Add = ["Kerasian hex"] });
        var ward = belmakor.Combat.Concentration(belmakor.Campaign, null, new ConcentrationOp(["torch"]) { Spell = "Kerasian Ward" });
        using var onePiece = CombatWorld.OnePiece();
        var onePieceStart = onePiece.Combat.Start(onePiece.Campaign, new StartRequest
        {
            Name = "L02",
            AddParty = false,
            Combatants =
            [
                new CombatantRequest { Character = "character:bjorn-mountainfell" },
                new CombatantRequest { Name = "Baalite cultist", Hp = HpChoice.Of(20), Ac = 12, Side = "ally" },
                new CombatantRequest { Name = "Nesterling", Hp = HpChoice.Of(20), Ac = 12, Side = "party" },
                CombatWorld.Monster("2024", "ogre", name: "Sealbreaker"),
            ],
        });
        onePiece.Combat.Condition(onePiece.Campaign, null, new ConditionOp(["bjorn-mountainfell"]) { Add = ["Nesterborn"] });
        var onePieceWard = onePiece.Combat.Concentration(onePiece.Campaign, null, new ConcentrationOp(["bjorn-mountainfell"]) { Spell = "Baalward" });

        var a = belmakor.Reader.Board(belmakor.Campaign, Party);
        var b = onePiece.Reader.Board(onePiece.Campaign, Party);

        Assert.Equal(["Lieutenant James Torch", "an unknown creature", "Lich"], a.Rows.Select(r => r.Name));
        Assert.Equal((true, (string?)null), (a.Rows[0].Concentrating, a.Rows[0].Concentration));
        Assert.Equal(["an effect"], a.Rows[0].Conditions);
        Assert.Equal(["Björn Mountainfell", "Baalite cultist", "an unknown creature", "Ogre"], b.Rows.Select(r => r.Name));
        Assert.Equal((true, "Baalward"), (b.Rows[0].Concentrating, b.Rows[0].Concentration));
        Assert.Equal(["an effect"], b.Rows[0].Conditions);
        LeakAssert.Clean(a, ["Keras", "Baal", "Nester", "Sealbreaker"], "the Belmakor party board");
        LeakAssert.Clean(b, ["Keras", "Nester", "Sealbreaker"], "the One Piece party board");
        Assert.Equal(2, start.Warnings.Count);
        Assert.Contains("'Kerasian sentinel' holds a word only text the party cannot see holds", start.Warnings[0], StringComparison.Ordinal);
        Assert.Single(hex.Warnings);
        Assert.Single(ward.Warnings);
        Assert.Equal(3, onePieceStart.Warnings.Count);
        var baal = Assert.Single(onePieceStart.Warnings, w => w.StartsWith("'Baalite cultist'", StringComparison.Ordinal));
        Assert.Matches(@"^'Baalite cultist' starts with 'Baal', a name in hidden text \(f:\d+(, f:\d+)*\): the party may read it as that name; party views show this combatant as written\.$", baal);
        Assert.StartsWith("'Baalward' starts with 'Baal', a name in hidden text (f:", Assert.Single(onePieceWard.Warnings), StringComparison.Ordinal);
    }

    /// <summary>
    /// UR01, CR02 (F2): the ordinary words of a DM's secret note ("dragon", "half", "hunt", "guard", "mill", "wild") start no
    /// giveaway. With reviews UR01's and CR02's notes in the campaign, the party board shows the custom names and spells
    /// they used to hide (as "an unknown creature", "an effect", a concentration with no spell name) and the author gets
    /// no D20b warning for them, while a derived form of the note's entity's hidden NAME ("Vextharian") still falls back
    /// and warns.
    /// </summary>
    [Fact]
    public void Names_StartingWithOrdinaryWordsOfASecretNote_ShowOnTheBoard_WithNoWarning()
    {
        using var w = CombatWorld.Dm();
        w.Apply(
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "faction", Name = "The Ashen Court", Visibility = "author", Summary = "A cult that serves the red dragon.",
                SecretMd = "They hunt the half-blood heirs. Their oath is sworn to the dragon in the mill; the blade is poisoned, and the wild hills hide the lair.",
            },
            new CampaignOpSpec
            {
                Op = "upsert", Kind = "character", Name = "Vexthar", Subtype = "npc", Visibility = "author", Summary = "An ancient dragon asleep under the mountain.",
                SecretMd = "Half the town guard is in its pay; it means to hunt the heirs down.",
            });
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Sheet("character:sidekick", CombatLifecycleTests.SidekickJson);
        var start = w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Hill fight",
            Combatants =
            [
                new CombatantRequest { Name = "Wildland Bandit", Hp = HpChoice.Of(11), Ac = 12 },
                new CombatantRequest { Name = "Millworker Thug", Hp = HpChoice.Of(32), Ac = 11 },
                new CombatantRequest { Name = "Guardsman Pell", Hp = HpChoice.Of(16), Ac = 16, Side = "ally" },
                new CombatantRequest { Name = "Huntsman Joss", Hp = HpChoice.Of(16), Ac = 14, Side = "ally" },
                new CombatantRequest { Name = "Vextharian zealot", Hp = HpChoice.Of(9), Ac = 12 },
            ],
        });
        var mark = w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["hero"]) { Spell = "Hunter's Mark" });
        var guardians = w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["sidekick"]) { Spell = "Spirit Guardians" });
        var marked = w.Combat.Condition(w.Campaign, null, new ConditionOp(["wildland-bandit"]) { Add = ["Hunter's Mark"], Source = "hero" });

        var board = w.Reader.Board(w.Campaign, Party);

        Assert.Equal(["Hero Prime", "Sidekick", "Wildland Bandit", "Millworker Thug", "Guardsman Pell", "Huntsman Joss", "an unknown creature"],
            board.Rows.Select(r => r.Name));
        Assert.Equal(("Hunter's Mark", "Spirit Guardians"), (board.Rows[0].Concentration, board.Rows[1].Concentration));
        Assert.Equal(["Hunter's Mark"], board.Rows[2].Conditions);
        Assert.Equal("'Vextharian zealot' holds a word only text the party cannot see holds (character:vexthar): party views show this combatant as 'an unknown creature'.",
            Assert.Single(start.Warnings));
        Assert.Empty(mark.Warnings.Concat(guardians.Warnings).Concat(marked.Warnings));
    }

    /// <summary>
    /// F2R01 (F3): an author-only quest and question titled in Title Case ("Slay the Red Dragon before the Hunt", "Who
    /// poisoned the Half-Blood Heir?") give the prefix rule no stem: the ally "Huntsman Joss" and the concentration on
    /// Hunter's Mark show on the party board, with no D20b warning (it turned the ally into "an unknown creature").
    /// </summary>
    [Fact]
    public void Names_TitleCaseAuthorOnlyTitles_HideNothingTheirWordsOnlyStart_NoWarning()
    {
        using var w = CombatWorld.Dm();
        w.Apply(
            new CampaignOpSpec { Op = "upsert", Kind = "quest", Name = "Slay the Red Dragon before the Hunt", Visibility = "author" },
            new CampaignOpSpec { Op = "upsert", Kind = "question", Name = "Who poisoned the Half-Blood Heir?", Visibility = "author" });
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        var start = w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Hill fight",
            AddParty = false,
            Combatants =
            [
                new CombatantRequest { Character = "character:hero" },
                new CombatantRequest { Name = "Huntsman Joss", Hp = HpChoice.Of(16), Ac = 14, Side = "ally" },
                new CombatantRequest { Name = "Halfling Scout", Hp = HpChoice.Of(9), Ac = 12 },
            ],
        });
        var mark = w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["hero"]) { Spell = "Hunter's Mark" });

        var board = w.Reader.Board(w.Campaign, Party);

        Assert.Equal(["Hero Prime", "Huntsman Joss", "Halfling Scout"], board.Rows.Select(r => r.Name));
        Assert.Equal("Hunter's Mark", board.Rows[0].Concentration);
        Assert.Empty(start.Warnings.Concat(mark.Warnings));
    }

    [Fact]
    public void Numbers_OnlyOnPartyRows_HpWordsForTheRest_UnknownHpOnlyUnhurtOrHurt()
    {
        using var w = CombatWorld.Dm();
        w.Sheet("character:hero", CombatLifecycleTests.HeroJson);
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Words",
            Combatants =
            [
                new CombatantRequest { Name = "Bandit", Hp = HpChoice.Of(20) },
                new CombatantRequest { Name = "Thug", Hp = HpChoice.Of(20) },
                new CombatantRequest { Name = "Brute", Hp = HpChoice.Of(20) },
                new CombatantRequest { Name = "Ghost", Hp = HpChoice.Unknown },
                new CombatantRequest { Name = "Hireling", Hp = HpChoice.Of(10), Side = "ally" },
            ],
        });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["thug"]) { Amount = 5 });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["brute"]) { Amount = 10 });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["bandit"]) { Amount = 25 });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["ghost"]) { Amount = 3 });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["hero"]) { Amount = 4 });

        var rows = w.Reader.Board(w.Campaign, Party).Rows.ToDictionary(r => r.Name);

        Assert.Equal((true, 40, 44, 16, 0), (rows["Hero Prime"].PartySide, rows["Hero Prime"].Hp, rows["Hero Prime"].MaxHp, rows["Hero Prime"].Ac, rows["Hero Prime"].Exhaustion));
        Assert.Equal((BoardHpWords.Down, BoardHpWords.Hurt, BoardHpWords.Bloodied, BoardHpWords.Hurt, BoardHpWords.Unhurt),
            (rows["Bandit"].HpWord, rows["Thug"].HpWord, rows["Brute"].HpWord, rows["Ghost"].HpWord, rows["Hireling"].HpWord));
        Assert.All(rows.Values.Where(r => !r.PartySide), r => Assert.Equal((null, null, null, null, null), (r.Hp, r.MaxHp, r.TempHp, r.Ac, r.Exhaustion)));
        Assert.False(rows["Sidekick"].Hp.HasValue);
    }

    [Fact]
    public void PartySideRows_NumbersOnlyUnderTheirOwnName_AStandInGetsTheHpWord()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Turncoats",
            AddParty = false,
            Combatants =
            [
                new CombatantRequest { Name = "Cultist", Hp = HpChoice.Of(9), Ac = 12, Side = "party" },
                new CombatantRequest { Name = "The Villain's shadow", Hp = HpChoice.Of(9), Ac = 12, Side = "party" },
                CombatWorld.Monster("2024", "goblin-warrior", hp: HpChoice.Avg, side: "party"),
                CombatWorld.Monster("2024", "ogre", name: "The Villain's ogre", hp: HpChoice.Avg, side: "party"),
                new CombatantRequest { Character = "character:villain", Side = "party" },
            ],
        });
        w.Combat.Damage(w.Campaign, null, new DamageOp(["Cultist", "The Villain's shadow", "Goblin Warrior", "The Villain's ogre"]) { Amount = 2 });

        var board = w.Reader.Board(w.Campaign, Party);

        Assert.Equal(["Cultist", "an unknown creature", "Goblin Warrior", "Ogre", "an unknown creature 2"], board.Rows.Select(r => r.Name));
        Assert.Equal([true, false, true, false, false], board.Rows.Select(r => r.PartySide));
        Assert.Equal((7, 9, 12), (board.Rows[0].Hp!.Value, board.Rows[0].MaxHp!.Value, board.Rows[0].Ac!.Value));
        Assert.NotNull(board.Rows[2].Hp);
        foreach (var standIn in new[] { board.Rows[1], board.Rows[3], board.Rows[4] })
        {
            Assert.Equal((null, null, null, null, null, false), (standIn.Hp, standIn.MaxHp, standIn.TempHp, standIn.Ac, standIn.Exhaustion, standIn.Concentrating));
            Assert.NotNull(standIn.HpWord);
        }

        Assert.Equal([BoardHpWords.Hurt, BoardHpWords.Hurt, BoardHpWords.Unhurt], new[] { board.Rows[1], board.Rows[3], board.Rows[4] }.Select(r => r.HpWord));
        LeakAssert.Clean(board, ["Villain"], "the party board");
    }

    [Fact]
    public void DisguisedPartyMember_ReadsAsItsKnownName_WithTheHpWord_NoNumbers()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Serret rooftop (probe)", AddParty = false, Combatants = [new CombatantRequest { Character = "character:protector", Side = "party", Hp = HpChoice.Of(40), Ac = 15 }] });

        var row = Assert.Single(w.Reader.Board(w.Campaign, Party).Rows);

        Assert.Equal(("the advisor in Serret", false, (int?)null, (int?)null, BoardHpWords.Unhurt), (row.Name, row.PartySide, row.Hp, row.Ac, row.HpWord));
        Assert.StartsWith("e:", row.Ref, StringComparison.Ordinal);
    }

    [Fact]
    public void ShownEntityRow_KeepsItsName_OtherRowsOfThatNameAreNumberedAmongThemselves()
    {
        using var w = CombatWorld.Dm();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Doubles",
            AddParty = false,
            Combatants =
            [
                new CombatantRequest { Character = "character:hero" },
                new CombatantRequest { Name = "Hero Prime", Hp = HpChoice.Of(9) },
                new CombatantRequest { Name = "Hero Prime", Hp = HpChoice.Of(9) },
            ],
        });
        Assert.Equal(["Hero Prime", "Hero Prime 2", "Hero Prime 3"], w.State("Doubles").Combatants.Select(c => c.Name));
        w.Combat.Initiative(w.Campaign, null, new InitiativeOp { Rolls = [new("hero-prime-2", Total: 10), new("hero-prime-3", Total: 5), new("character:hero", Total: 1)] });

        var rows = w.Reader.Board(w.Campaign, Party).Rows;

        Assert.Equal([("Hero Prime", false), ("Hero Prime 2", false), ("Hero Prime", true)], rows.Select(r => (r.Name, r.Ref is not null)));
    }

    [Fact]
    public void DisguisedAlly_TheKnownNameAndAnERef_NeverTheTrueName()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Serret rooftop (probe)",
            Combatants = [new CombatantRequest { Character = "character:protector", Side = "ally" }, new CombatantRequest { Name = "Wraith Blade", Ac = 15, Hp = HpChoice.Of(33), Count = 3 }],
        });
        w.Combat.Condition(w.Campaign, null, new ConditionOp(["protector"]) { Add = ["stunned"], Duration = "end of round 2" });

        var board = w.Reader.Board(w.Campaign, Party);

        var protector = Assert.Single(board.Rows, r => r.Name == "the advisor in Serret");
        Assert.StartsWith("e:", protector.Ref, StringComparison.Ordinal);
        Assert.Contains("stunned", protector.Conditions);
        // "Wraith" is a word only hidden text holds in this world (the Nester's secret, the Wraiths' unmet entries): the
        // custom name fails the check, and the three copies are renumbered under the stand-in.
        Assert.Equal(["an unknown creature", "an unknown creature 2", "an unknown creature 3"], board.Rows.Where(r => r.Ref is null).Select(r => r.Name));
        LeakAssert.Clean(board, OnePieceForbidden, "the party board");
        Assert.Contains(w.Reader.State(w.Campaign).Encounter!.Rows, r => r.Name == "The Protector");
    }

    [Fact]
    public void LinkedStatBlockTheViewDoesNotSee_IsTheMonstersName_TypedTrueNameIsTheMonstersToo()
    {
        using var w = CombatWorld.Belmakor();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Old king rematch (probe)",
            AddParty = false,
            Combatants = [CombatWorld.Monster("2014", "lich", character: "character:old-king", hp: HpChoice.Avg), CombatWorld.Monster("2014", "lich", name: "Keras", hp: HpChoice.Avg)],
        });

        foreach (var view in BelmakorScenario.NonAuthorPerspectives)
        {
            var board = w.Reader.Board(w.Campaign, Perspective.Parse(view));
            LeakAssert.Clean(board, ["Keras"], $"the {view} board");
            Assert.Equal(2, board.Rows.Count);
        }

        var party = w.Reader.Board(w.Campaign, Party);
        Assert.Equal(("The Old King", "character:old-king"), (party.Rows[0].Name, party.Rows[0].Ref));
        Assert.Equal(("Lich", (string?)null), (party.Rows[1].Name, party.Rows[1].Ref));
    }

    [Fact]
    public void LinkedEntityTheViewDoesNotSee_ATypedNameOverItIsNeverShown_TheMonstersNameIs()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest
        {
            Name = "Probe",
            AddParty = false,
            Combatants = [CombatWorld.Monster("2024", "aboleth", name: "The Nester", character: "character:the-nester")],
        });
        Assert.Equal("The Nester", w.State("Probe").Combatants.Single().Name);

        var board = w.Reader.Board(w.Campaign, Party);

        Assert.Equal(("Aboleth", (string?)null), (board.Rows.Single().Name, board.Rows.Single().Ref));
        LeakAssert.Clean(board, OnePieceForbidden, "the party board");
    }

    [Fact]
    public void AuthorPerspective_IsNotABoard()
    {
        using var w = CombatWorld.Dm();

        Assert.Throws<ArgumentException>(() => w.Reader.Board(w.Campaign, Perspective.Author));
        Assert.Throws<ArgumentException>(() => w.Reader.Board(w.Campaign, Perspective.Parse("dm")));
    }

    [Fact]
    public void DamagedEntityPage_FailsWithTheStoreMessage_WhateverTheFight()
    {
        using var w = CombatWorld.Dm();
        w.F.Db.ScrambleRootPage("entity");

        Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.Board(w.Campaign, Perspective.Parse("character:hero"), "No such fight"));
    }

    /// <summary>
    /// R04: an unreadable combatant refuses the board with the store message, which names neither the combatant nor the
    /// fight (both author text) and ends the fight it shows by "current", not by its name.
    /// </summary>
    [Fact]
    public void UnreadableCombatant_TheBoardsRefusalNamesNeitherTheCombatantNorTheFight()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Nester's lair (probe)", AddParty = false, Combatants = [CombatWorld.Monster("2024", "aboleth", name: "The Nester")] });
        w.F.Db.WriteBehindTheServer("UPDATE combatant SET conditions = 'garbage'");

        var refused = Assert.Throws<CampaignStoreUnavailableException>(() => w.Reader.Board(w.Campaign, Party));

        Assert.StartsWith($"A combatant of the fight this board shows in campaign {w.Campaign.Slug} cannot be read (", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"combat {{\"action\": \"end\", \"discard\": true, \"campaign\": \"{w.Campaign.Slug}\"}}", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nester", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("probe", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PartyConcentration_TheSpellWhenItPasses_ElseOnlyThatItConcentrates()
    {
        using var w = CombatWorld.OnePiece();
        w.Combat.Start(w.Campaign, new StartRequest { Name = "Spells" });
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["dragon-slayer"]) { Spell = "Bless" });
        w.Combat.Concentration(w.Campaign, null, new ConcentrationOp(["fishman-monk"]) { Spell = "Nester's Ward" });

        var rows = w.Reader.Board(w.Campaign, Party).Rows.ToDictionary(r => r.Name);

        Assert.Equal((true, "Bless"), (rows["The amethyst Dragon Slayer"].Concentrating, rows["The amethyst Dragon Slayer"].Concentration));
        Assert.Equal((true, (string?)null), (rows["The fishman monk"].Concentrating, rows["The fishman monk"].Concentration));
        Assert.Equal((false, (string?)null), (rows["Björn Mountainfell"].Concentrating, rows["Björn Mountainfell"].Concentration));
    }
}
