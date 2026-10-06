using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Campaign.Ops;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Characters;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Repository.Campaign.Write;
using DndMcp.Tests.CampaignCharacters;
using DndMcp.Tests.CampaignCombat;
using Xunit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The exit criterion, fixture B (FIX §4 as amended by contract §16): One Piece's 2024 DM campaign fights the in-lair
/// Aboleth linked to The Nester through the Repository's services, with the three server rolls of FIX §4.1 (scripted
/// faces 5; 4, 5, 2, 3; 3, 2). Every numbered step pins what the STORE holds afterwards, the reminder tokens of the author
/// result, and the combat_log and dice_roll rows it appended. B27: no step logs or touches a sheet, holding, coin or
/// award; exactly three dice rows, linked to the fight and session 13, secret 1, 1, 0, each cited by one combat_log row of
/// its total. B28 and §4.3: one batch writes Björn 51 HP, Rage 1, exhaustion 1 (the column), XP 36,400; the monk 7 HP,
/// Focus 4, the curse exactly as persisted JSON, XP 36,400; the Dragon Slayer 56 HP, XP 36,400; the potion 2 → 1 (current
/// − used); the ledger, the water-breathing potion, 120 gp and three awards of 2,400 (in lair, 7,200 / 3), all filed under
/// session 13; The Nester's death a proposal, never applied. §6.3's turn context holds at every step (the in-lair 4
/// legendary uses offered after every other turn, Björn's exhaustion through his turns, the dying monk's one line, "Round
/// N" at each wrap), Bloodied is said once per first crossing (B8, B15) and "all enemies are defeated: end the combat?"
/// only when the Aboleth falls (B26). Variants that write build their own world: loot of one end reads back by name key
/// after what was held before; the potion is the CURRENT quantity − 1 (a bottle bought mid-fight is kept); a session ended
/// before the fight still files the write-back; the proposal applied is a batch of its own with Phase 6's warning. What
/// breaks if these fail: the exit criterion.
/// </summary>
public sealed class CombatOnePieceScenarioTests(CombatOnePiecePlay play) : IClassFixture<CombatOnePiecePlay>
{
    private const string Monk = "The fishman monk";
    private const string Bjorn = "Björn Mountainfell";
    private const string Slayer = "The amethyst Dragon Slayer";
    private const string Aboleth = "Aboleth";

    private CombatStepRecord At(string step) => play[step];

    private string Id(string name) => play.Steps[^1].Named(name).Id;

    // ------------------------------------------------------------------------------------------------------------------
    // The whole script (B27)

    [Fact]
    public void OnePiece_EveryStep_TheStoredFightReadsBackAsTheTrackerLeftIt()
    {
        Assert.Equal(47, play.Steps.Count);
        foreach (var step in play.Steps)
        {
            Assert.True(CombatPlay.Comparable(step.Persisted) == CombatPlay.Comparable(step.Outcome.Encounter.State), $"{step.Step}: the stored fight differs from the tracker's");
        }
    }

    [Fact]
    public void OnePiece_B27_NoStepWritesChangeLog_NorTouchesASheetHoldingCoinOrAward()
    {
        Assert.All(play.Steps, s => Assert.Equal(play.N0, s.ChangeRows));
        Assert.All(play.Steps, s => Assert.Equal(play.SheetTablesBefore, s.SheetTables));
        Assert.Equal(2.0, play.End.Items.Single().Before);
    }

    [Fact]
    public void OnePiece_B27_ExactlyThreeDiceRows_B16_B20_B22_LinkedToTheFightAndSession13_Secret110()
    {
        var rolled = play.Steps.Where(s => s.Dice.Count > 0).ToList();

        Assert.Equal(["B16", "B20", "B22"], rolled.Select(s => s.Step));
        var dice = rolled.SelectMany(s => s.Dice).ToList();
        Assert.Equal(
            [("1d10", "Aboleth: heal", 5L, 1L), ("4d6+5", "Aboleth: damage (critical)", 19L, 1L), ("2d4+2", "Björn Mountainfell: heal", 7L, 0L)],
            dice.Select(d => (d.Expression, d.Label!, d.Total, d.Secret)));
        Assert.All(dice, d => Assert.Equal((play.Encounter.Id, play.SessionId), (d.EncounterId, d.SessionId)));
        Assert.All(dice, d => Assert.Equal("combat", JsonNode.Parse(d.Detail)!["source"]!.GetValue<string>()));
        Assert.Empty(play.EndDice);
        Assert.Equal(0, play.World.Roller.Left);
    }

    [Fact]
    public void OnePiece_B27_EachRollIsCitedByExactlyOneRowOfItsTotal_EveryOtherRowGivenNoRollId()
    {
        var log = play.Steps.SelectMany(s => s.Log).ToList();
        var dice = play.Steps.SelectMany(s => s.Dice).ToList();

        foreach (var roll in dice)
        {
            var citing = Assert.Single(log, r => r.RollId == roll.Id);
            Assert.Equal(roll.Total, citing.Amount);
        }

        Assert.Equal(3, log.Count(r => r.RollId is not null));
        Assert.Equal([L.Heal, L.Damage, L.Heal], log.Where(r => r.RollId is not null).Select(r => r.Kind));
        Assert.All(log.Where(r => r.Kind == L.Damage && r.RollId is null), r => Assert.True(JsonNode.Parse(r.Detail!)!["given"]!.GetValue<bool>()));
    }

    [Fact]
    public void OnePiece_B27_FocusRageResistanceLegendaryExhaustionCurseAndGrapples_AreCombatLogRowsOnly()
    {
        var log = play.Steps.SelectMany(s => s.Log).ToList();
        var resources = log.Where(r => r.Kind == L.Resource).Select(r => JsonNode.Parse(r.Detail!)!).ToList();

        Assert.Equal(4, resources.Where(d => d["key"]!.GetValue<string>() == "focus").Sum(d => d["after"]!.GetValue<int>() - d["before"]!.GetValue<int>()));
        Assert.Single(resources, d => d["key"]!.GetValue<string>() == "rage");
        var legendary = log.Where(r => r.Kind == L.Legendary).Select(r => (r.Round, Detail: JsonNode.Parse(r.Detail!)!)).ToList();
        Assert.Equal(2, legendary.Count(l => l.Detail["resistance"]?.GetValue<bool>() == true));
        Assert.Equal([1L, 1L, 1L, 2L, 2L, 2L], legendary.Where(l => l.Detail["name"] is not null).Select(l => l.Round));
        Assert.Equal(["Lash", "Lash", "Lash", "Psychic Drain", "Lash", "Lash"], legendary.Where(l => l.Detail["name"] is not null).Select(l => l.Detail["name"]!.GetValue<string>()));
        var conditions = log.Where(r => r.Kind == L.Condition).Select(r => r.Detail!).ToList();
        Assert.Contains(conditions, d => d.Contains("\"exhaustion\":1", StringComparison.Ordinal));
        Assert.Contains(conditions, d => d.Contains("cursed (Mucus Cloud)", StringComparison.Ordinal));
        Assert.Equal(2, conditions.Count(d => d.Contains("\"grappled\"", StringComparison.Ordinal)));
        Assert.Equal(play.N0, play.Steps[^1].ChangeRows);
    }

    [Fact]
    public void OnePiece_B27_TheMonksDeathSaves_MoveOnlyAtTheCombatLogRowsThatMovedThem_TheDropTheCriticalAndThePotion()
    {
        // B27: the death saves live in combat_log only. The monk's dying state and tallies change at exactly three steps,
        // each of which wrote the one row that says why: B18's tentacle drops him (dying, 0/0), B20's critical at 0 HP adds
        // two failures (0/2: the row is a critical hit at 0 → 0), B22's potion revives him (the tallies reset). Nothing of
        // it reaches change_log or the write-back.
        var moved = new List<string>();
        var tallies = DeathSaveTally.Zero;
        var dying = false;
        foreach (var step in play.Steps.SkipWhile(s => s.Step != "B1"))
        {
            var monk = step.Named(Monk);
            if (monk.DeathSaves != tallies || monk.Dying != dying)
            {
                moved.Add($"{step.Step} {monk.DeathSaves.Successes}/{monk.DeathSaves.Failures}{(monk.Dying ? " dying" : string.Empty)}");
            }

            (tallies, dying) = (monk.DeathSaves, monk.Dying);
        }

        Assert.Equal(["B18-second 0/0 dying", "B20 0/2 dying", "B22 0/0"], moved);
        var monkId = Id(Monk);
        var drop = Assert.Single(At("B18-second").Log);
        Assert.Equal((L.Damage, monkId, 12L, "12 bludgeoning; 3 → 0"), (drop.Kind, drop.TargetId, drop.Amount, JsonNode.Parse(drop.Detail!)!["arithmetic"]!.GetValue<string>()));
        var critical = Assert.Single(At("B20").Log);
        var criticalDetail = JsonNode.Parse(critical.Detail!)!;
        Assert.Equal((L.Damage, monkId, 19L, true, "19 bludgeoning; 0 → 0"),
            (critical.Kind, critical.TargetId, critical.Amount, criticalDetail["critical"]!.GetValue<bool>(), criticalDetail["arithmetic"]!.GetValue<string>()));
        var potion = Assert.Single(At("B22").Log, r => r.Kind == L.Heal);
        Assert.Equal((monkId, 7L, 7), (potion.TargetId, potion.Amount, JsonNode.Parse(potion.Detail!)!["regained"]!.GetValue<int>()));
        Assert.All(new[] { "B18-second", "B20", "B22" }, s => Assert.Equal(play.N0, At(s).ChangeRows));
        Assert.DoesNotContain(play.EndRows, r => r.FieldPath == "death_saves");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // §6.3's turn context, as a property of every step of the stored fight

    [Fact]
    public void OnePiece_EveryStep_TheAbolethMayActAfterEveryOtherTurn_WithItsInLairUsesLeft_NeverInItsOwnTurnOrDead()
    {
        foreach (var step in play.Steps)
        {
            var aboleth = step.Persisted.Combatants.SingleOrDefault(c => c.Name == Aboleth);
            var holder = step.Persisted.TurnHolder;
            var available = step.Of(K.LegendaryAvailable);
            if (aboleth is null || holder is null || aboleth.Dead || holder.Id == aboleth.Id || aboleth.Legendary!.ActionsLeft == 0)
            {
                Assert.True(available.Count == 0, $"{step.Step}: {string.Join(" / ", available.Select(r => r.Text))}");
                continue;
            }

            var reminder = Assert.Single(available);
            Assert.Contains($"when {holder.Name}'s turn ends, {Aboleth} may take a legendary action ({aboleth.Legendary.ActionsLeft}/4)", reminder.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OnePiece_EveryStep_DuringBjornsTurnsHisExhaustionIsSaid_TheOtherDyingHaveTheirLines()
    {
        foreach (var step in play.Steps.Where(s => s.Persisted.TurnHolder is not null))
        {
            var holder = step.Persisted.TurnHolder!;
            Assert.Equal(holder.Exhaustion > 0, step.Says(K.ExhaustionEffects, holder.Name, $"Exhaustion {holder.Exhaustion}"));
            foreach (var condition in holder.Conditions.Where(c => c.IsSrdCondition))
            {
                Assert.True(step.Says(K.ConditionEffects, $"{holder.Name} is {condition.Name}"), $"{step.Step}: {holder.Name} {condition.Name}");
            }

            foreach (var dying in step.Persisted.Combatants.Where(c => c.Dying && c.Id != holder.Id))
            {
                // §6.3: ONE dying line per other dying combatant (a step's own line about it replaces the context's).
                var line = Assert.Single(step.Of(K.Dying), r => r.CombatantId == dying.Id);
                Assert.True(line.Text.StartsWith($"{dying.Name}: 0 HP, {dying.DeathSaves.Successes} successes, {dying.DeathSaves.Failures} failures", StringComparison.Ordinal), $"{step.Step}: {line.Text}");
            }

            Assert.DoesNotContain(step.Of(K.Dying), r => r.CombatantId == holder.Id);
        }

        Assert.Equal(
            ["B12-next", "B12-rage", "B12", "B13-legendary", "B13", "B21", "B22", "B23", "B24-legendary", "B24"],
            play.Steps.Where(s => s.Persisted.TurnHolder is not null && s.Of(K.ExhaustionEffects).Count > 0).Select(s => s.Step));
        Assert.Equal(
            ["B18-second", "B18", "B19-next", "B19", "B20-legendary", "B20", "B21"],
            play.Steps.Where(s => s.Of(K.Dying).Count > 0).Select(s => s.Step));
        Assert.All(play.Steps, s => Assert.Empty(s.Of(K.Expiring)));
    }

    [Fact]
    public void OnePiece_EveryStep_AllEnemiesDefeatedIsSaidOnlyAtB26_WhenTheAbolethFalls()
    {
        foreach (var step in play.Steps)
        {
            var enemies = step.Persisted.Combatants.Where(c => c.Side == CampaignValues.CombatSides.Enemy).ToList();
            var allDown = enemies.Count > 0 && enemies.All(e => e.Defeated || e.Removed);
            Assert.True(allDown == (step.Of(K.AllEnemiesDown).Count > 0), $"{step.Step}: {string.Join(" / ", step.Of(K.AllEnemiesDown).Select(r => r.Text))}");
        }

        Assert.Equal(["B26"], play.Steps.Where(s => s.Of(K.AllEnemiesDown).Count > 0).Select(s => s.Step));
    }

    [Fact]
    public void OnePiece_EveryStep_BloodiedIsSaidOnceForEachFirstCrossing_TheMonkAtB8_TheAbolethAtB15_NeverOnALaterHit()
    {
        // FIX B8/B15 (2024 Bloodied): said when a creature first crosses half, not on the hits that keep it there (the monk
        // at B16 and B18, the Aboleth at B19 and B23).
        Assert.Equal(
            [("B8", Id(Monk)), ("B15", Id(Aboleth))],
            play.Steps.SelectMany(s => s.Of(K.Bloodied).Select(r => (s.Step, r.CombatantId!))));
    }

    [Fact]
    public void OnePiece_EveryStep_RoundIsSaidExactlyWhenTheRoundAdvances()
    {
        var round = 0;
        foreach (var step in play.Steps)
        {
            Assert.True(step.Persisted.Round > round == step.Says(K.Round, $"Round {step.Persisted.Round}"), step.Step);
            round = step.Persisted.Round;
        }

        Assert.Equal(["B4", "B14", "B25"], play.Steps.Where(s => s.Of(K.Round).Count > 0).Select(s => s.Step));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B0-B4

    [Fact]
    public void OnePiece_B0_Session13IsLive_TheThreePcsAttend()
    {
        var session = new SessionReader(play.World.Database).Get(play.World.Campaign, "13");

        Assert.Equal(("live", 13), (session.Session.Status, session.Session.Number));
        Assert.Equal(
            ["character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk"],
            session.Attendance.Where(a => a.Present).Select(a => a.Character.Ref).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void OnePiece_B1_StartInTheLair_ThePartyFromSheets_85_68_59_NoChangeLog()
    {
        var b1 = At("B1");
        var fight = b1.Outcome.Encounter;

        Assert.Equal((CampaignValues.EncounterStatuses.Active, "2024", 0, true, 13), (fight.Status, fight.Ruleset, fight.Round, fight.Lair, fight.SessionNumber));
        Assert.Equal([Bjorn, Slayer, Monk], b1.Persisted.Combatants.Select(c => c.Name));
        Assert.Equal([(85, 85, 15), (68, 68, 16), (59, 59, 16)], b1.Persisted.Combatants.Select(c => (c.Hp!.Value, c.MaxHp!.Value, c.Ac!.Value)));
        Assert.All(b1.Persisted.Combatants, c => Assert.True(c.IsSheetSeeded, c.Name));
        Assert.Empty(b1.Of(K.NoHp));
        Assert.Equal([L.Start, L.Add, L.Add, L.Add], b1.Kinds);
        Assert.True(JsonNode.Parse(b1.Log[0].Detail!)!["lair"]!.GetValue<bool>());
    }

    [Fact]
    public void OnePiece_B2_TheAbolethLinkedToTheNester_150Ac17InitPlus3_InLairLegendaryAndResistance4Of4()
    {
        var b2 = At("B2");
        var aboleth = b2.Named(Aboleth);

        Assert.Equal((150, 150, 17, 3), (aboleth.Hp, aboleth.MaxHp, aboleth.Ac, aboleth.InitBonus));
        Assert.Equal(new LegendaryState(4, 0, 4, 0), aboleth.Legendary);
        Assert.Equal((play.World.Id("character:the-nester"), "character:the-nester", "2024/monster/aboleth"), (aboleth.EntityId, aboleth.EntityHandle, aboleth.SrdRef));
        Assert.Equal((CampaignValues.CombatSides.Enemy, false), (aboleth.Side, aboleth.IsSheetSeeded));
        Assert.True(b2.Line(Aboleth, "150/150 HP, AC 17, initiative +3, legendary actions 4/4, Legendary Resistance 4/4"));
        Assert.Empty(b2.Outcome.Warnings);
    }

    [Fact]
    public void OnePiece_B_A2024LairNeverPromptsALairAction()
    {
        Assert.All(play.Steps, s => Assert.Empty(s.Of(K.LairAction)));
        Assert.DoesNotContain(play.Steps.SelectMany(s => s.Outcome.Reminders), r => r.Text.Contains("initiative 20", StringComparison.Ordinal));
    }

    [Fact]
    public void OnePiece_B3_ExhaustionIsTheColumn_NotACondition_Exhaustion1EffectsAtRoundZero()
    {
        var b3 = At("B3");
        var bjorn = b3.Named(Bjorn);

        Assert.Equal((1, 0), (bjorn.Exhaustion, bjorn.Conditions.Count));
        Assert.True(b3.Says(K.ExhaustionEffects, Bjorn, "Exhaustion 1: D20 Tests −2; Speed −5 ft"));
        Assert.Equal(0, b3.Persisted.Round);
        var row = Assert.Single(b3.Log);
        Assert.Equal((L.Condition, 1L, (string?)null, bjorn.Id), (row.Kind, row.Amount, row.ActorId, row.TargetId));
    }

    [Fact]
    public void OnePiece_B4_Initiative_BjornsExhaustionTakesTwo_OrderMonk19Aboleth13Slayer11Bjorn8()
    {
        var b4 = At("B4");

        Assert.Equal([Monk, Aboleth, Slayer, Bjorn], b4.Persisted.Order.Select(c => c.Name));
        Assert.Equal([19.0, 13, 11, 8], b4.Persisted.Order.Select(c => c.Initiative!.Value));
        Assert.True(b4.Line(Bjorn, "initiative 8 (8 + 2 − 2 exhaustion)"));
        Assert.True(b4.Line(Aboleth, "initiative 13 (given)"));
        Assert.Equal((1, Monk), (b4.Persisted.Round, b4.Persisted.TurnHolder!.Name));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Round 1

    [Fact]
    public void OnePiece_B5_FocusTwoSpent_AbolethTo132_LegendaryResistanceFourToThree()
    {
        Assert.Equal((2, 6), (At("B5-focus").Named(Monk).Resources["focus"].Used, At("B5-focus").Named(Monk).Resources["focus"].Left));
        Assert.Equal(132, At("B5").Named(Aboleth).Hp);
        var legendary = At("B5-resistance").Named(Aboleth).Legendary!;
        Assert.Equal((3, 4), (legendary.ResistanceLeft, legendary.ActionsLeft));
        Assert.True(At("B5-resistance").Line(Aboleth, "Legendary Resistance", "3/4 left"));
    }

    [Fact]
    public void OnePiece_B6_LashAfterTheMonksTurn_ThreeLeft_MonkTo47()
    {
        Assert.Equal(Monk, At("B6-legendary").Persisted.TurnHolder!.Name);
        Assert.Equal(3, At("B6-legendary").Named(Aboleth).Legendary!.ActionsLeft);
        Assert.Equal(47, At("B6").Named(Monk).Hp);
        Assert.Equal(Id(Aboleth), Assert.Single(At("B6").Log).ActorId);
    }

    [Fact]
    public void OnePiece_B7_AbolethsTurn_LegendaryActionsReset4Of4()
    {
        Assert.Equal(Aboleth, At("B7").Persisted.TurnHolder!.Name);
        Assert.Equal(4, At("B7").Named(Aboleth).Legendary!.ActionsLeft);
        Assert.True(At("B7").Says(K.LegendaryReset, Aboleth, "reset: 4/4", "3/4"));
    }

    [Fact]
    public void OnePiece_B8_TentaclesGrappleUntilEscapeDc14_MonkTo25IsBloodied_BjornTo73()
    {
        var grapple = Assert.Single(At("B8-grapple-monk").Named(Monk).Conditions);
        Assert.Equal(("grappled", CombatValues.Durations.UntilEscape, 14, Id(Aboleth)), (grapple.Name, grapple.Duration, grapple.EscapeDc, grapple.Source));
        Assert.Equal(35, At("B8-monk").Named(Monk).Hp);
        Assert.Equal(73, At("B8-bjorn").Named(Bjorn).Hp);
        Assert.Equal((CombatValues.Durations.UntilEscape, 14), (At("B8-grapple-bjorn").Named(Bjorn).Conditions.Single().Duration, At("B8-grapple-bjorn").Named(Bjorn).Conditions.Single().EscapeDc!.Value));
        Assert.Equal(25, At("B8").Named(Monk).Hp);
        Assert.True(At("B8").Says(K.Bloodied, "fishman monk is Bloodied"));
        Assert.Empty(At("B8-monk").Of(K.Bloodied));
    }

    [Fact]
    public void OnePiece_B9_MucusCloudCurse_UntilRemoved_FromTheAboleth_KeptAfterTheFight()
    {
        var b9 = At("B9");
        var curse = b9.Named(Monk).Conditions.Single(c => c.Name == "cursed (Mucus Cloud)");

        Assert.Equal((CombatValues.Durations.UntilRemoved, Id(Aboleth)), (curse.Duration, curse.Source));
        Assert.Equal(new AppliedAt(1, Id(Aboleth)), curse.Applied);
        Assert.True(b9.Line(Monk, "cursed (Mucus Cloud) (Aboleth), until removed (it stays on the sheet after the fight)"));
    }

    [Fact]
    public void OnePiece_B10_B11_TheSlayerHitsFor26_AbolethTo106_LashTo56()
    {
        Assert.Equal(Slayer, At("B10-next").Persisted.TurnHolder!.Name);
        Assert.Equal(106, At("B10").Named(Aboleth).Hp);
        Assert.True(At("B10").Line(Aboleth, "22 bludgeoning + 4 psychic = 26", "132 → 106"));
        Assert.Equal(3, At("B11-legendary").Named(Aboleth).Legendary!.ActionsLeft);
        Assert.Equal(56, At("B11").Named(Slayer).Hp);
    }

    [Fact]
    public void OnePiece_B12_RageSpendsAUse_ResistsAllButPsychic_LastsTheFight_GrappledAndExhaustionReminded()
    {
        var rage = At("B12-rage");
        var bjorn = rage.Named(Bjorn);
        var effect = bjorn.Conditions.Single(c => c.Name == "Rage");

        Assert.Equal(Bjorn, At("B12-next").Persisted.TurnHolder!.Name);
        Assert.Equal((1, 3), (bjorn.Resources["rage"].Used, bjorn.Resources["rage"].Left));
        Assert.Equal((CombatValues.Durations.Fight, "all", "psychic"), (effect.Duration, effect.Effect!.Resist.Single(), effect.Effect.Except.Single()));
        Assert.True(rage.Says(K.ExhaustionEffects, Bjorn, "Exhaustion 1", "−2"));
        Assert.True(rage.Says(K.ConditionEffects, Bjorn, "grappled (Aboleth)", "Disadvantage on attacks against targets other than the grappler"));
        Assert.Equal([L.Resource, L.Condition], rage.Kinds);
        Assert.Equal(82, At("B12").Named(Aboleth).Hp);
    }

    [Fact]
    public void OnePiece_B13_RageHalvesTheLash_BjornTo67_LegendaryTwoLeft()
    {
        Assert.Equal(2, At("B13-legendary").Named(Aboleth).Legendary!.ActionsLeft);
        Assert.Equal(67, At("B13").Named(Bjorn).Hp);
        Assert.True(At("B13").Line(Bjorn, "12 bludgeoning ½ (resistant: Rage) = 6", "73 → 67"));
        Assert.Equal(6L, Assert.Single(At("B13").Log).Amount);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Round 2

    [Fact]
    public void OnePiece_B14_Round2_TheMonksTurn()
    {
        Assert.Equal((2, Monk), (At("B14").Persisted.Round, At("B14").Persisted.TurnHolder!.Name));
        Assert.True(At("B14").Says(K.Round, "Round 2"));
    }

    [Fact]
    public void OnePiece_B15_FocusTo4Used_AbolethTo62_BloodiedOnItsFirstCrossing_ResistanceTwoLeft()
    {
        Assert.Equal(4, At("B15-focus").Named(Monk).Resources["focus"].Used);
        Assert.Equal(62, At("B15").Named(Aboleth).Hp);
        Assert.True(At("B15").Says(K.Bloodied, "Aboleth is Bloodied"));
        Assert.Equal(2, At("B15-resistance").Named(Aboleth).Legendary!.ResistanceLeft);
        Assert.DoesNotContain(play.Steps.TakeWhile(s => s.Step != "B15"), s => s.Says(K.Bloodied, Aboleth));
    }

    [Fact]
    public void OnePiece_B16_PsychicDrain_MonkTo15_TheAbolethRegains1d10Rolled5_SecretLabelledAboleth_CitedByTheHealRow()
    {
        Assert.Equal(1, At("B16-legendary").Named(Aboleth).Legendary!.ActionsLeft);
        Assert.Equal(15, At("B16-damage").Named(Monk).Hp);
        var b16 = At("B16");

        Assert.Equal(67, b16.Named(Aboleth).Hp);
        var roll = Assert.Single(b16.Dice);
        Assert.Equal(("1d10", 5L, 1L, "Aboleth: heal"), (roll.Expression, roll.Total, roll.Secret, roll.Label!));
        var heal = Assert.Single(b16.Log);
        Assert.Equal((L.Heal, 5L, roll.Id), (heal.Kind, heal.Amount, heal.RollId));
        var shown = Assert.Single(b16.Outcome.Rolls);
        Assert.Equal((roll.Id, true), (shown.RollId, shown.Secret));
        Assert.Equal([5], shown.Faces);
        Assert.Equal([5], CombatPlay.Faces(roll.Detail));
    }

    [Fact]
    public void OnePiece_B17_AbolethsTurn_ResetFromOneTo4Of4()
    {
        Assert.True(At("B17").Says(K.LegendaryReset, Aboleth, "reset: 4/4", "1/4"));
    }

    [Fact]
    public void OnePiece_B18_TwoTentaclesDropTheMonk_NineLeftOverIsNotInstantDeath_PsychicNotResisted()
    {
        Assert.Equal(3, At("B18-first").Named(Monk).Hp);
        var monk = At("B18-second").Named(Monk);

        Assert.Equal((0, false, true), (monk.Hp, monk.Dead, monk.Dying));
        Assert.Equal(DeathSaveTally.Zero, monk.DeathSaves);
        Assert.True(monk.Has("unconscious"));
        Assert.True(At("B18-second").Says(K.Dropped, Monk, "0 HP", "unconscious"));
        Assert.Equal(57, At("B18").Named(Bjorn).Hp);
        Assert.True(At("B18").Line(Bjorn, "10 psychic", "67 → 57"));
    }

    [Fact]
    public void OnePiece_B19_TheSlayer_AbolethTo39()
    {
        Assert.Equal(39, At("B19").Named(Aboleth).Hp);
    }

    [Fact]
    public void OnePiece_B20_CriticalLashRolled4d6Plus5_Faces4523_19_TwoFailures_SecretLabelledAboleth()
    {
        Assert.Equal(3, At("B20-legendary").Named(Aboleth).Legendary!.ActionsLeft);
        var b20 = At("B20");
        var monk = b20.Named(Monk);

        Assert.Equal((0, new DeathSaveTally(0, 2, false), false), (monk.Hp, monk.DeathSaves, monk.Dead));
        var roll = Assert.Single(b20.Dice);
        Assert.Equal(("4d6+5", 19L, 1L, "Aboleth: damage (critical)"), (roll.Expression, roll.Total, roll.Secret, roll.Label!));
        Assert.Equal([4, 5, 2, 3], Assert.Single(b20.Outcome.Rolls).Faces);
        Assert.Equal([4, 5, 2, 3], CombatPlay.Faces(roll.Detail));
        var row = Assert.Single(b20.Log);
        Assert.Equal((L.Damage, 19L, roll.Id), (row.Kind, row.Amount, row.RollId));
        Assert.True(JsonNode.Parse(row.Detail!)!["critical"]!.GetValue<bool>());
        Assert.True(b20.Says(K.UnconsciousCrit, Monk, "a hit from within 5 ft is a Critical Hit"));
        Assert.True(b20.Says(K.Dying, Monk, "two death save failures"));

        // §6.3: one dying line for the monk, the step's own (the turn context's "… 2 failures" for him is dropped).
        var dying = Assert.Single(b20.Of(K.Dying));
        Assert.Equal((monk.Id, "The fishman monk: 0 HP, 0 successes, 2 failures (a critical hit: two death save failures)"), (dying.CombatantId, dying.Text));
    }

    [Fact]
    public void OnePiece_B21_BjornsTurn_TheMonksTalliesAndBjornsExhaustion()
    {
        var b21 = At("B21");

        Assert.Equal(Bjorn, b21.Persisted.TurnHolder!.Name);
        Assert.True(b21.Says(K.Dying, Monk, "0 HP", "0 successes, 2 failures"));
        Assert.True(b21.Says(K.ExhaustionEffects, Bjorn, "Exhaustion 1: D20 Tests −2"));
        Assert.Empty(b21.Of(K.DeathSaveDue));
    }

    [Fact]
    public void OnePiece_B22_BjornsPotion2d4Plus2Rolled7_MonkConsciousAt7_TalliesReset_OpenLabel_PotionPendingUntilEnd()
    {
        var b22 = At("B22");
        var monk = b22.Named(Monk);

        Assert.Equal((7, false), (monk.Hp, monk.Has("unconscious")));
        Assert.Equal(DeathSaveTally.Zero, monk.DeathSaves);
        Assert.True(monk.Has("prone"));
        Assert.True(b22.Says(K.Revived, Monk));
        var roll = Assert.Single(b22.Dice);
        Assert.Equal(("2d4+2", 7L, 0L, "Björn Mountainfell: heal"), (roll.Expression, roll.Total, roll.Secret, roll.Label!));
        Assert.Equal([3, 2], Assert.Single(b22.Outcome.Rolls).Faces);
        Assert.Equal([3, 2], CombatPlay.Faces(roll.Detail));
        var heal = b22.Log.Single(r => r.Kind == L.Heal);
        Assert.Equal((7L, roll.Id, b22.Named(Bjorn).Id), (heal.Amount, heal.RollId, heal.ActorId));
        var potion = b22.Named(Bjorn).Resources.Single(r => r.Key.StartsWith(CombatValues.ResourceKeys.ItemPrefix, StringComparison.Ordinal)).Value;
        Assert.Equal(("Potion of Healing", 1), (potion.Name, potion.Used));
        Assert.True(b22.Line(Bjorn, "Potion of Healing used (1 left; the inventory is written when the fight ends)"));
        Assert.Equal(play.SheetTablesBefore, b22.SheetTables);
    }

    [Fact]
    public void OnePiece_B23_B24_AbolethTo13_TheResistedLashBjornTo51_LegendaryTwoLeft()
    {
        Assert.Equal(13, At("B23").Named(Aboleth).Hp);
        Assert.Equal(2, At("B24-legendary").Named(Aboleth).Legendary!.ActionsLeft);
        Assert.Equal(51, At("B24").Named(Bjorn).Hp);
    }

    [Fact]
    public void OnePiece_B25_Round3_TheMonk_StillGrappledAndProne()
    {
        var b25 = At("B25");

        Assert.Equal((3, Monk), (b25.Persisted.Round, b25.Persisted.TurnHolder!.Name));
        Assert.True(b25.Named(Monk).Has("grappled"));
        Assert.True(b25.Says(K.ConditionEffects, Monk, "prone"));
    }

    [Fact]
    public void OnePiece_B26_AbolethDies_BothGrapplesEndWithIt_EldritchRestoration_AllEnemiesDown()
    {
        var b26 = At("B26");

        Assert.True(b26.Named(Aboleth).Dead);
        Assert.True(b26.Named(Aboleth).Defeated);
        Assert.False(b26.Named(Monk).Has("grappled"));
        Assert.False(b26.Named(Bjorn).Has("grappled"));
        Assert.Equal(2, b26.Of(K.GrappleEnded).Count);
        Assert.True(b26.Says(K.GrappleEnded, Bjorn, "Aboleth is dead"));
        Assert.True(b26.Says(K.GrappleEnded, Monk, "Aboleth is dead"));
        Assert.True(b26.Says(K.Trait, Aboleth, "Eldritch Restoration", "new body in 5d10 days"));
        Assert.True(b26.Says(K.AllEnemiesDown, "all enemies are defeated: end the combat?"));
        Assert.Equal([L.Damage, L.Defeat], b26.Kinds);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B28 and §4.3

    [Fact]
    public void OnePiece_B28_EndIsOneBatch_OnlySheetHoldingCoinAndAwardRows_FiledUnderSession13()
    {
        Assert.NotNull(play.End.BatchId);
        Assert.Equal(play.ChangeRowsAfterEnd - play.N0, play.EndRows.Count);
        Assert.Single(play.EndRows.Select(r => r.BatchId).Distinct());
        Assert.Equal(["award", "character_sheet", "currency_txn", "holding"], play.EndRows.Select(r => r.TargetTable).Distinct().Order(StringComparer.Ordinal));
        Assert.All(play.EndRows, r => Assert.Equal(("claude", "combat/end", play.SessionId), (r.Actor, r.Tool, r.SessionId)));
        Assert.Equal(13, play.End.SessionNumber);
    }

    [Fact]
    public void OnePiece_B28_Section43_Bjorn_Hp51_Rage1_Exhaustion1TheColumn_Xp36400_NoConditionKept()
    {
        var before = play.SheetRowsBefore["character:bjorn-mountainfell"];
        var after = play.SheetRowsAfter["character:bjorn-mountainfell"];

        Assert.Equal(["exhaustion", "hp", "resources", "updated_at", "xp"], CombatPlay.ChangedColumns(before, after));
        Assert.Equal(("85", "51"), (CombatPlay.Text(before, "hp"), CombatPlay.Text(after, "hp")));
        Assert.Equal(("0", "1"), (CombatPlay.Text(before, "exhaustion"), CombatPlay.Text(after, "exhaustion")));
        Assert.Equal(("34000", "36400"), (CombatPlay.Text(before, "xp"), CombatPlay.Text(after, "xp")));
        Assert.Equal("[]", CombatPlay.Text(after, "conditions"));
        Assert.Equal("""{"rage":{"name":"Rage","max":4,"used":1,"recharge":"short_rest_one"}}""", CombatPlay.Text(after, "resources"));
    }

    [Fact]
    public void OnePiece_B28_Section43_Monk_Hp7_Focus4_TheCurseAsPersistedJson_Xp36400_DeathSavesUnwritten()
    {
        var before = play.SheetRowsBefore["character:fishman-monk"];
        var after = play.SheetRowsAfter["character:fishman-monk"];

        Assert.Equal(["conditions", "hp", "resources", "updated_at", "xp"], CombatPlay.ChangedColumns(before, after));
        Assert.Equal(("59", "7"), (CombatPlay.Text(before, "hp"), CombatPlay.Text(after, "hp")));
        Assert.Equal(
            """[{"name":"cursed (Mucus Cloud)","source":"Aboleth","duration":"until_removed","note":"from The dark station (fixture), round 1"}]""",
            CombatPlay.Text(after, "conditions"));
        Assert.Equal(4, JsonNode.Parse(CombatPlay.Text(after, "resources")!)!["focus"]!["used"]!.GetValue<int>());
        Assert.Equal("36400", CombatPlay.Text(after, "xp"));
        Assert.DoesNotContain(play.EndRows, r => r.FieldPath == "death_saves");
    }

    [Fact]
    public void OnePiece_B28_Section43_Slayer_Hp56_Xp36400()
    {
        var before = play.SheetRowsBefore["character:dragon-slayer"];
        var after = play.SheetRowsAfter["character:dragon-slayer"];

        Assert.Equal(["hp", "updated_at", "xp"], CombatPlay.ChangedColumns(before, after));
        Assert.Equal(("68", "56", "36400"), (CombatPlay.Text(before, "hp"), CombatPlay.Text(after, "hp"), CombatPlay.Text(after, "xp")));
    }

    [Fact]
    public void OnePiece_B28_ThePotionIsWrittenAsCurrentMinusUsed_TwoToOne_TheRowKept()
    {
        var item = Assert.Single(play.End.Items);

        Assert.Equal(("Potion of Healing", 2.0, 1.0, 1), (item.Name, item.Before, item.After, item.Used));
        Assert.Equal(1.0, play.World.F.Scalar<double>("SELECT quantity FROM holding WHERE id = @id", new { id = item.HoldingId }));
        var row = Assert.Single(play.EndRows, r => r.TargetId == item.HoldingId);
        Assert.Equal(("update", "quantity"), (row.Op, row.FieldPath));
        Assert.Contains(play.End.Summary, s => s == "Björn Mountainfell: Potion of Healing 2 → 1.");
    }

    [Fact]
    public void OnePiece_B28_TheLootTheCoinsAndThreeAwardsOf2400_AllSession13s()
    {
        var party = play.World.Id("faction:the-party");
        var bjorn = play.World.Id("character:bjorn-mountainfell");
        var session = play.SessionId;

        Assert.Equal(1L, play.World.F.Count(
            "SELECT count(*) FROM holding WHERE name = 'The sixth station''s ledger' AND holder_id = @party AND quantity = 1 AND srd_ref IS NULL AND acquired_session_id = @session",
            new { party, session }));
        Assert.Equal(1L, play.World.F.Count(
            "SELECT count(*) FROM holding WHERE name = 'Potion of Water Breathing' AND holder_id = @bjorn AND quantity = 1 AND srd_ref = '2024/magic-item/potion-of-water-breathing' AND acquired_session_id = @session",
            new { bjorn, session }));
        Assert.Equal(1L, play.World.F.Count(
            "SELECT count(*) FROM currency_txn WHERE holder_id = @party AND gp = 120 AND cp = 0 AND sp = 0 AND ep = 0 AND pp = 0 AND note = 'The dark station (fixture)' AND session_id = @session",
            new { party, session }));
        Assert.Equal(
            ["character:bjorn-mountainfell", "character:dragon-slayer", "character:fishman-monk"],
            play.World.F.Query<string>(
                "SELECT e.kind || ':' || e.slug FROM award a JOIN entity e ON e.id = a.recipient_id WHERE a.kind = 'xp' AND a.amount = 2400 AND a.source = 'encounter: The dark station (fixture)' AND a.session_id = @session ORDER BY e.slug",
                new { session }));
        Assert.Equal(3L, play.World.F.Count("SELECT count(*) FROM award"));
    }

    [Fact]
    public void OnePiece_B28_XpInLair_7200Over3_2400Each_NoRemainder_NoLevelDue()
    {
        Assert.Equal((7_200, 7_200, 2_400, 0, 3), (play.End.Xp.Worth, play.End.Xp.Awarded, play.End.Xp.Each, play.End.Xp.Remainder, play.End.Xp.Recipients));
        Assert.Equal(3, play.End.Awards.Count);
        Assert.All(play.End.Awards, a => Assert.Equal((2_400, "encounter: The dark station (fixture)", true), (a.Amount, a.Source, a.SheetTracksXp)));
        Assert.DoesNotContain(play.End.Reminders, r => r.Kind == K.LevelUp);
    }

    [Fact]
    public void OnePiece_B28_GrapplesRageProneUnconsciousEnded_TheCurseKept_TheSummarySaysWhich()
    {
        Assert.Contains("Ended with the fight: Rage (Björn Mountainfell); prone (The fishman monk).", play.End.Summary);
        Assert.Contains("The fishman monk: conditions kept on the sheet: cursed (Mucus Cloud).", play.End.Summary);
        Assert.DoesNotContain(play.EndRows, r => r.EntityId == play.World.Id("character:bjorn-mountainfell") && r.FieldPath == "conditions");
    }

    [Fact]
    public void OnePiece_B28_TheNestersDeathIsAProposal_EldritchRestoration_NeverApplied_NotInTheBatch()
    {
        var proposal = Assert.Single(play.End.Proposals);

        Assert.Equal(("character:the-nester", "dead"), (proposal.EntityHandle, proposal.Status));
        Assert.Equal(
            "campaign_write {\"ops\": [{\"op\": \"status\", \"ref\": \"character:the-nester\", \"status\": \"dead\"}], \"dry_run\": true, \"campaign\": \"one-piece\"}",
            proposal.Call);
        Assert.Contains("Eldritch Restoration", proposal.Text, StringComparison.Ordinal);
        Assert.Contains("consider \"unknown\"", proposal.Text, StringComparison.Ordinal);
        Assert.Equal("alive", play.World.F.Entity(play.World.Campaign, "character:the-nester").Status);
        Assert.DoesNotContain(play.EndRows, r => r.TargetTable == CampaignTables.Entity.Name || r.EntityId == play.World.Id("character:the-nester"));
        Assert.Contains(play.End.Reminders, r => r.Kind == K.Trait && r.Text.Contains("Eldritch Restoration", StringComparison.Ordinal));
    }

    [Fact]
    public void OnePiece_B28_TheFightEnds_OutcomeAndBatchOnTheRow_AnEndRow_AppliedStatus()
    {
        Assert.Equal((CampaignValues.EncounterStatuses.Ended, "The sixth station is clear.", play.End.BatchId), (play.Encounter.Status, play.Encounter.OutcomeMd, play.Encounter.WritebackBatchId));
        Assert.Equal([L.End], play.EndLog.Select(r => r.Kind));
        Assert.Equal(WritebackStatuses.Applied, play.World.Reader.State(play.World.Campaign, EncounterResolver.Last).Encounter!.WritebackStatus);
    }

    [Fact]
    public void OnePiece_B28_BjornsInventory_TheOldPotionThenTheLoot_InTheOrderGained()
    {
        var sheet = new SheetReader(play.World.Database).Get(play.World.Campaign, "character:bjorn-mountainfell").Characters.Single().Author!;

        Assert.Equal([("Potion of Healing", 1.0), ("Potion of Water Breathing", 1.0)], sheet.Inventory.Select(h => (h.Name, h.Quantity)));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B28 variants, each on a world of its own (they write).

    [Fact]
    public void OnePiece_B28_LootGainedInOneEnd_ReadsBackByNameKeyAfterWhatWasHeldBefore()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        w.F.Db.Time.Advance(TimeSpan.FromMinutes(5));

        w.Combat.End(w.Campaign, null, new EndRequest
        {
            Outcome = "The sixth station is clear.",
            Loot =
            [
                new LootRequest("Zircon shard (probe)") { To = "character:bjorn-mountainfell" },
                new LootRequest("Amber shard (probe)") { To = "character:bjorn-mountainfell" },
                new LootRequest("Moonstone shard (probe)") { Qty = 2, To = "character:bjorn-mountainfell" },
            ],
        });

        var inventory = new SheetReader(w.Database).Get(w.Campaign, "character:bjorn-mountainfell").Characters.Single().Author!.Inventory;
        Assert.Equal(
            [("Potion of Healing", 1.0), ("Amber shard (probe)", 1.0), ("Moonstone shard (probe)", 2.0), ("Zircon shard (probe)", 1.0)],
            inventory.Select(h => (h.Name, h.Quantity)));
    }

    [Fact]
    public void OnePiece_B28_TheProposalAppliedIsABatchOfItsOwn_WithThePhase6DeadWarning()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var end = CombatScripts.EndB(w);

        var applied = w.F.Apply(w.Campaign, new CampaignOpSpec { Op = "status", Ref = "character:the-nester", Status = "dead" });

        Assert.NotEqual(end.BatchId, applied.BatchId);
        Assert.Contains(applied.Warnings, x => x.Message ==
            "character:the-nester is dead now: review its member_of relation (status former, until) and what it knows (its knowledge rows are kept).");
        Assert.Equal("dead", w.F.Entity(w.Campaign, "character:the-nester").Status);
        Assert.All(w.F.Log(end.BatchId!), r => Assert.NotEqual(CampaignTables.Entity.Name, r.TargetTable));
    }

    [Fact]
    public void OnePiece_B28_ThePotionIsTheCurrentQuantityMinusOne_ABottleGainedMidFightIsKept()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        var bought = w.Characters.Inventory(w.Campaign, "character:bjorn-mountainfell",
            [new InventoryItem { Item = FixtureSheets.PotionOfHealing, Qty = 1 }], WriteContext.Default);
        Assert.NotNull(bought.BatchId);
        Assert.Equal(3.0, w.F.Scalar<double>("SELECT quantity FROM holding WHERE name = 'Potion of Healing'"));

        var end = CombatScripts.EndB(w);

        var item = Assert.Single(end.Items);
        Assert.Equal((3.0, 2.0, 1), (item.Before, item.After, item.Used));
        Assert.Equal(2.0, w.F.Scalar<double>("SELECT quantity FROM holding WHERE name = 'Potion of Healing'"));
        Assert.Empty(end.Overwritten);
    }

    [Fact]
    public void OnePiece_B28_TheSessionEndedBeforeTheFight_TheWriteBackIsStillSession13s()
    {
        using var w = CombatWorld.OnePiece();
        CombatScripts.FixtureB(w);
        w.F.Sessions.End(w.Campaign, "The sixth station.");
        w.Reload();

        var end = CombatScripts.EndB(w);

        var session = w.Id("session:13");
        Assert.Equal(13, end.SessionNumber);
        Assert.All(w.F.Log(end.BatchId!), r => Assert.Equal(session, r.SessionId));
        Assert.Equal(3L, w.F.Count("SELECT count(*) FROM award WHERE session_id = @session", new { session }));
        Assert.Equal(2L, w.F.Count("SELECT count(*) FROM holding WHERE acquired_session_id = @session", new { session }));
    }
}
