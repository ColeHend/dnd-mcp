using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using DndMcp.Repository.Campaign;
using DndMcp.Repository.Campaign.Combat;
using DndMcp.Repository.Campaign.Read;
using DndMcp.Tests.CampaignCombat;
using Xunit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.CampaignScenarios;

/// <summary>
/// The exit criterion, fixture A (FIX §2 as amended by contract §16): Belmakor's 2014 player campaign fights the crypt
/// through the Repository's services (K's <see cref="CombatService"/> over C's fixture sheets on the Phase 6 world), every
/// call its own transaction, and every numbered step pins what the STORE holds afterwards (read back on a fresh
/// connection), the reminder tokens the author result carries, and the combat_log rows the step appended. A32: no step
/// writes change_log or touches a sheet. A33 and §2.3: <c>end</c> is one batch (tool combat/end, session 4) that writes
/// exactly the "After" column: Belmakor 82 HP, temp 0, one 5th-level slot and one Bladesong used, Circle of Power kept
/// with 98 rounds left; Torch 1 HP; the four sheets with no HP untouched; no XP (no sheet tracks it), worth 14,400.
/// §6.3's turn context holds at EVERY step of the stored fight, not only where FIX names a token: the Mummy Lord is offered
/// a legendary action after every other creature's turn with the uses it has left, never in its own turn or once dead;
/// every other dying creature has exactly one dying line; the turn-holder's conditions and what ends with its turn are
/// said; "Round N" exactly when the round advances; "all enemies are defeated: end the combat?" exactly when the last enemy
/// falls (A31) and never while one stands. Variants that write (an end after Belmakor has acted: 97 rounds) build their
/// own world.
/// What breaks if these fail: the exit criterion "a full scripted combat round-trips to the sheet correctly".
/// </summary>
public sealed class CombatBelmakorScenarioTests(CombatBelmakorPlay play) : IClassFixture<CombatBelmakorPlay>
{
    private const string Belmakor = "Belmakor Silverwind";
    private const string Torch = "Lieutenant James Torch";
    private const string Lord = "Mummy Lord";
    private const string Mummy = "Mummy";
    private const string Mummy2 = "Mummy 2";
    private const string Aiden = "Aiden Ironstar";
    private const string Vars = "Vars Nocturne";

    private CombatStepRecord At(string step) => play[step];

    private string Id(string name) => play.Steps[^1].Named(name).Id;

    // ------------------------------------------------------------------------------------------------------------------
    // The whole script: every step round-trips through the store; no step logs or touches a sheet (A32).

    [Fact]
    public void Belmakor_EveryStep_TheStoredFightReadsBackAsTheTrackerLeftIt()
    {
        Assert.Equal(38, play.Steps.Count);
        foreach (var step in play.Steps)
        {
            Assert.True(CombatPlay.Comparable(step.Persisted) == CombatPlay.Comparable(step.Outcome.Encounter.State), $"{step.Step}: the stored fight differs from the tracker's");
            Assert.Equal(CampaignValues.EncounterStatuses.Active, step.Persisted.Status);
        }
    }

    [Fact]
    public void Belmakor_A32_NoStepWritesChangeLog_NorTouchesASheet_NorRollsADie()
    {
        Assert.All(play.Steps, s => Assert.Equal(play.N0, s.ChangeRows));
        Assert.All(play.Steps, s => Assert.Equal(play.SheetTablesBefore, s.SheetTables));
        Assert.All(play.Steps, s => Assert.Empty(s.Dice));
        Assert.All(play.Steps, s => Assert.Empty(s.Outcome.Rolls));
        Assert.All(play.Steps, s => Assert.NotEmpty(s.Log));
    }

    [Fact]
    public void Belmakor_A32_TheCombatLogHoldsEveryChange_GivenValuesNoRollIds()
    {
        var log = play.Steps.SelectMany(s => s.Log).ToList();

        Assert.All(log, r => Assert.Null(r.RollId));
        Assert.All(log, r => Assert.True(CampaignValues.CombatLogKinds.Set.Contains(r.Kind), r.Kind));
        foreach (var kind in new[] { L.Start, L.Add, L.Initiative, L.Turn, L.Damage, L.Condition, L.Concentration, L.DeathSave, L.Legendary, L.Resource, L.Defeat })
        {
            Assert.Contains(log, r => r.Kind == kind);
        }

        var damage = log.Where(r => r.Kind == L.Damage).ToList();
        Assert.All(damage, r => Assert.True(JsonNode.Parse(r.Detail!)!["given"]!.GetValue<bool>()));
        Assert.All(damage, r => Assert.NotNull(r.TargetId));
        Assert.Equal(12, damage.Count);
        Assert.Equal(3, log.Count(r => r.Kind == L.Defeat));
        Assert.All(log.Where(r => r.Kind is L.Initiative), r => Assert.True(JsonNode.Parse(r.Detail!)!["given"]!.GetValue<bool>()));
        Assert.All(log, r => Assert.Equal(play.Encounter.Id, r.EncounterId));

        // Each row carries the round it happened in: 0 before initiative, 1-3 after.
        Assert.Equal([0L, 1L, 2L, 3L], log.Select(r => r.Round).Distinct().Order());
        Assert.All(At("A31").Log, r => Assert.Equal(3L, r.Round));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // §6.3's turn context, as a property of every step of the stored fight

    [Fact]
    public void Belmakor_EveryStep_TheMummyLordMayActAfterEveryOtherTurn_WithItsUsesLeft_NeverInItsOwnTurnOrDead()
    {
        foreach (var step in play.Steps)
        {
            var lord = step.Persisted.Combatants.SingleOrDefault(c => c.Name == Lord);
            var holder = step.Persisted.TurnHolder;
            var available = step.Of(K.LegendaryAvailable);
            if (lord is null || holder is null || lord.Dead || holder.Id == lord.Id || lord.Legendary!.ActionsLeft == 0)
            {
                Assert.True(available.Count == 0, $"{step.Step}: {string.Join(" / ", available.Select(r => r.Text))}");
                continue;
            }

            var reminder = Assert.Single(available);
            Assert.Equal(lord.Id, reminder.CombatantId);
            Assert.Contains($"when {holder.Name}'s turn ends, {Lord} may take a legendary action ({lord.Legendary.ActionsLeft}/3)", reminder.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Belmakor_EveryStep_EachOtherDyingCombatantHasExactlyOneDyingLine_TheTurnHolderNone()
    {
        foreach (var step in play.Steps.Where(s => s.Persisted.TurnHolder is not null))
        {
            var holder = step.Persisted.TurnHolder!;
            foreach (var dying in step.Persisted.Combatants.Where(c => c.Dying && c.Id != holder.Id))
            {
                // §6.3: ONE dying line per other dying combatant (a step's own line about it replaces the context's).
                var line = Assert.Single(step.Of(K.Dying), r => r.CombatantId == dying.Id);
                Assert.True(line.Text.StartsWith($"{dying.Name}: 0 HP, {dying.DeathSaves.Successes} successes, {dying.DeathSaves.Failures} failures", StringComparison.Ordinal), $"{step.Step}: {line.Text}");
            }

            Assert.DoesNotContain(step.Of(K.Dying), r => r.CombatantId == holder.Id);
            Assert.All(step.Of(K.Dying), r => Assert.True(step.Persisted.Find(r.CombatantId)!.Dying, step.Step));
        }

        Assert.Equal(
            ["A18", "A19-aiden", "A19", "A20", "A21", "A22", "A23", "A24-serif", "A24-ignis"],
            play.Steps.Where(s => s.Of(K.Dying).Count > 0).Select(s => s.Step));
    }

    [Fact]
    public void Belmakor_EveryStep_AllEnemiesDefeatedIsSaidOnlyAtA31_WhenTheLastEnemyFalls()
    {
        foreach (var step in play.Steps)
        {
            var enemies = step.Persisted.Combatants.Where(c => c.Side == CampaignValues.CombatSides.Enemy).ToList();
            var allDown = enemies.Count > 0 && enemies.All(e => e.Defeated || e.Removed);
            Assert.True(allDown == (step.Of(K.AllEnemiesDown).Count > 0), $"{step.Step}: {string.Join(" / ", step.Of(K.AllEnemiesDown).Select(r => r.Text))}");
        }

        Assert.Equal(["A31"], play.Steps.Where(s => s.Of(K.AllEnemiesDown).Count > 0).Select(s => s.Step));
    }

    [Fact]
    public void Belmakor_EveryStep_NoReminderIsAboutTheContingency()
    {
        // FIX A8: "Not a reminder: the Contingency" (its threshold is not recorded), here or at any other step.
        Assert.DoesNotContain(play.Steps.SelectMany(s => s.Outcome.Reminders), r => r.Text.Contains("Contingency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Belmakor_EveryStep_TheTurnHoldersConditionsAndWhatEndsWithItsTurn_AreSaid()
    {
        foreach (var step in play.Steps.Where(s => s.Persisted.TurnHolder is not null))
        {
            var holder = step.Persisted.TurnHolder!;
            foreach (var condition in holder.Conditions.Where(c => c.IsSrdCondition))
            {
                Assert.True(step.Says(K.ConditionEffects, $"{holder.Name} is {condition.Name}"), $"{step.Step}: {holder.Name} {condition.Name}");
            }

            var ending = step.Persisted.Combatants.SelectMany(c => c.Conditions.Select(x => (Target: c, Condition: x)))
                .Where(x => !x.Condition.SkipEnd &&
                            ((x.Condition.Duration == CombatValues.Durations.UntilEndOfSourceTurn && x.Condition.Source == holder.Id) ||
                             (x.Condition.Duration == CombatValues.Durations.UntilEndOfTargetTurn && x.Target.Id == holder.Id)))
                .ToList();
            Assert.Equal(ending.Count, step.Of(K.Expiring).Count);
            foreach (var (target, condition) in ending)
            {
                Assert.True(step.Says(K.Expiring, condition.Name, target.Name, "ends at the end of this turn"), $"{step.Step}: {condition.Name} on {target.Name}");
            }
        }

        Assert.Equal(["A27", "A28a-prev"], play.Steps.Where(s => s.Of(K.Expiring).Count > 0).Select(s => s.Step));
    }

    [Fact]
    public void Belmakor_EveryStep_RoundIsSaidExactlyWhenTheRoundAdvances()
    {
        var round = 0;
        foreach (var step in play.Steps)
        {
            var advanced = step.Persisted.Round > round;
            Assert.True(advanced == step.Says(K.Round, $"Round {step.Persisted.Round}"), step.Step);
            round = step.Persisted.Round;
        }

        Assert.Equal(["A3", "A19", "A30"], play.Steps.Where(s => s.Of(K.Round).Count > 0).Select(s => s.Step));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // A0-A3

    [Fact]
    public void Belmakor_A0_Session4IsLive_TheSixLivingPcsAttend()
    {
        var session = new SessionReader(play.World.Database).Get(play.World.Campaign, "4");

        Assert.Equal(("live", 4), (session.Session.Status, session.Session.Number));
        Assert.Equal(6, session.Attendance.Count(a => a.Present));
        Assert.DoesNotContain(session.Attendance, a => a.Character.Ref == "character:tristan");
        Assert.Equal(play.N0, play.Steps[0].ChangeRows);
        Assert.NotNull(play.SessionStart.BatchId);
    }

    [Fact]
    public void Belmakor_A1_StartFilesAnActive2014FightUnderSession4_ThePartyFromSheetsInNameOrder_TristanLeftOut()
    {
        var a1 = At("A1");
        var fight = a1.Outcome.Encounter;

        Assert.True(a1.Outcome.Created);
        Assert.Equal((CampaignValues.EncounterStatuses.Active, "2014", 0, 4, false), (fight.Status, fight.Ruleset, fight.Round, fight.SessionNumber, fight.Lair));
        Assert.Equal(play.SessionId, play.Encounter.SessionId);
        Assert.Equal([Aiden, Belmakor, "Ignis", Torch, "Serif", Vars], a1.Persisted.Combatants.Select(c => c.Name));
        Assert.All(a1.Persisted.Combatants, c => Assert.True(c.IsSheetSeeded, c.Name));
        Assert.All(a1.Persisted.Combatants, c => Assert.Equal(CampaignValues.CombatSides.Party, c.Side));
        Assert.Contains("Not added: Tristan (dead).", a1.Outcome.Lines);
        Assert.Equal([L.Start, L.Add, L.Add, L.Add, L.Add, L.Add, L.Add], a1.Kinds);
    }

    [Fact]
    public void Belmakor_A1_BelmakorFromHisSheet_110Of110Temp7Ac17Init5_FirstSlotSpentBeforeTheFight_Bladesong4Of4_ContingencySet()
    {
        var belmakor = At("A1").Named(Belmakor);

        Assert.Equal((110, 110, 7, 17, 5), (belmakor.Hp, belmakor.MaxHp, belmakor.TempHp, belmakor.Ac, belmakor.InitBonus));
        Assert.Equal((4, 1, 3), (belmakor.Resources["slot:1"].Max, belmakor.Resources["slot:1"].Used, belmakor.Resources["slot:1"].Left));
        Assert.Equal((4, 0), (belmakor.Resources["bladesong"].Max, belmakor.Resources["bladesong"].Used));
        Assert.Equal("set", belmakor.Resources["contingency"].State);
        Assert.Equal(DeathSaveTally.Zero, belmakor.DeathSaves);
        Assert.Empty(belmakor.Conditions);
        Assert.Null(belmakor.Concentration);
        Assert.Equal(110L, belmakor.SheetSnapshot!.Columns["hp"]);
        Assert.Equal(7L, belmakor.SheetSnapshot.Columns["temp_hp"]);
    }

    [Fact]
    public void Belmakor_A1_Torch74Of74_FourSheetsWithoutHp_EachRemindedWithTheSetCall()
    {
        var a1 = At("A1");

        Assert.Equal((74, 74), (a1.Named(Torch).Hp, a1.Named(Torch).MaxHp));
        foreach (var (name, handle) in new[] { (Aiden, "aiden-ironstar"), ("Ignis", "ignis"), ("Serif", "serif"), (Vars, "vars") })
        {
            Assert.Equal(((int?)null, (int?)null), (a1.Named(name).Hp, a1.Named(name).MaxHp));
            var noHp = Assert.Single(a1.Of(K.NoHp), r => r.CombatantId == a1.Named(name).Id);
            Assert.Contains(name, noHp.Text, StringComparison.Ordinal);
            Assert.Equal($"combat {{\"action\": \"set\", \"combatants\": [{{\"character\": \"character:{handle}\", \"hp\": …}}]}}", noHp.Call);
        }

        Assert.Equal(4, a1.Of(K.NoHp).Count);
        Assert.True(a1.Line(Belmakor, "110/110 HP (+7 temp), AC 17, initiative +5, from its sheet"));
    }

    [Fact]
    public void Belmakor_A2_MummyLord97Ac17Legendary3_TwoMummies58Ac11InOneInitiativeGroup_SnapshotsStored()
    {
        var a2 = At("A2");
        var lord = a2.Named(Lord);
        var mummy = a2.Named(Mummy);
        var mummy2 = a2.Named(Mummy2);

        Assert.Equal((97, 97, 17, 0), (lord.Hp, lord.MaxHp, lord.Ac, lord.InitBonus));
        Assert.Equal(new LegendaryState(3, 0, 0, 0), lord.Legendary);
        Assert.Equal(("2014/monster/mummy-lord", "Mummy Lord"), (lord.SrdRef, lord.StatBlock!.Name));
        Assert.Null(lord.InitGroup);
        Assert.Equal((58, 58, 11, -1), (mummy.Hp, mummy.MaxHp, mummy.Ac, mummy.InitBonus));
        Assert.Equal((58, 11, -1), (mummy2.Hp, mummy2.Ac, mummy2.InitBonus));
        Assert.Equal(("2014/monster/mummy", "Mummy"), (mummy2.SrdRef, mummy2.StatBlock!.Name));
        Assert.NotNull(mummy.InitGroup);
        Assert.Equal(mummy.InitGroup, mummy2.InitGroup);
        Assert.All(new[] { lord, mummy, mummy2 }, c => Assert.Equal((CampaignValues.CombatSides.Enemy, false), (c.Side, c.IsSheetSeeded)));
        Assert.True(a2.Line(Lord, "97/97 HP, AC 17, initiative +0, legendary actions 3/3"));
        Assert.Equal([L.Add, L.Add, L.Add], a2.Kinds);
    }

    [Fact]
    public void Belmakor_A3_FacesAndTotals_Order25_22_18_16_14_12_9_9_7_RoundOneVarsToAct_NoTieInTheGroup()
    {
        var a3 = At("A3");

        Assert.Equal([Vars, Belmakor, Lord, "Serif", "Ignis", Torch, Mummy, Mummy2, Aiden], a3.Persisted.Order.Select(c => c.Name));
        Assert.Equal([25.0, 22, 18, 16, 14, 12, 9, 9, 7], a3.Persisted.Order.Select(c => c.Initiative!.Value));
        Assert.Equal((1, Vars), (a3.Persisted.Round, a3.Persisted.TurnHolder!.Name));
        Assert.True(a3.Line(Belmakor, "initiative 22 (17 + 5)"));
        Assert.True(a3.Line(Lord, "initiative 18 (18 + 0)"));
        Assert.True(a3.Line("Mummy 2: initiative 9 (10 − 1)"));
        Assert.True(a3.Line(Vars, "initiative 25 (given)"));
        Assert.Empty(a3.Of(K.Tie));
        Assert.Equal(9, a3.Log.Count(r => r.Kind == L.Initiative));
        Assert.Equal(L.Turn, a3.Kinds[^1]);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Round 1

    [Fact]
    public void Belmakor_A4_NextIsBelmakorsTurn()
    {
        Assert.Equal((Belmakor, 1), (At("A4").Persisted.TurnHolder!.Name, At("A4").Persisted.Round));
        Assert.Equal([L.Turn], At("A4").Kinds);
    }

    [Fact]
    public void Belmakor_A5_BladesongSpendsAUse_3Of4Left_Ac17To22_ATenRoundEffect()
    {
        var a5 = At("A5");
        var belmakor = a5.Named(Belmakor);

        Assert.Equal((1, 3), (belmakor.Resources["bladesong"].Used, belmakor.Resources["bladesong"].Left));
        Assert.Equal((17, 22), (belmakor.Ac, belmakor.DisplayedAc));
        var bladesong = Assert.Single(belmakor.Conditions);
        Assert.Equal(("Bladesong", CombatValues.Durations.Rounds, 5), (bladesong.Name, bladesong.Duration, bladesong.Effect!.Ac));
        Assert.Equal(new ConditionExpiry(11, CombatValues.ExpiryPoints.Start, belmakor.Id), bladesong.Expires);
        Assert.True(a5.Line("Bladesong is not an SRD condition: tracked as an effect"));
        Assert.Equal([L.Resource, L.Condition], a5.Kinds);
    }

    [Fact]
    public void Belmakor_A6_CircleOfPowerSpendsAFifthLevelSlot_1Of2Left_ConcentrationUntilRound101()
    {
        var a6 = At("A6");
        var belmakor = a6.Named(Belmakor);

        Assert.Equal((1, 1), (belmakor.Resources["slot:5"].Used, belmakor.Resources["slot:5"].Left));
        Assert.Equal(("Circle of Power", 5), (belmakor.Concentration!.Spell, belmakor.Concentration.Level));
        Assert.Equal(new ConditionExpiry(101, CombatValues.ExpiryPoints.Start, belmakor.Id), belmakor.Concentration.Expires);
        Assert.Equal([L.Resource, L.Concentration], a6.Kinds);
    }

    [Fact]
    public void Belmakor_A7_MummyLordsTurn_LegendaryActions3Of3()
    {
        var a7 = At("A7");

        Assert.Equal(Lord, a7.Persisted.TurnHolder!.Name);
        Assert.True(a7.Says(K.LegendaryReset, Lord, "3/3"));
        Assert.Empty(a7.Of(K.LegendaryAvailable));
    }

    [Fact]
    public void Belmakor_A8_TempHpAbsorbs7_HpIs82_ConcentrationDc17()
    {
        var a8 = At("A8");
        var belmakor = a8.Named(Belmakor);

        Assert.Equal((82, 0), (belmakor.Hp, belmakor.TempHp));
        Assert.Equal([17], belmakor.Concentration!.Pending);
        Assert.True(a8.Says(K.ConcentrationSave, Belmakor, "concentration", "Circle of Power", "DC 17"));
        var save = Assert.Single(a8.Of(K.ConcentrationSave));
        Assert.Equal("Belmakor Silverwind: concentration save DC 17 to keep Circle of Power (Con save +7)", save.Text);
        Assert.Equal("combat {\"action\": \"concentration\", \"targets\": [\"belmakor\"], \"total\": …}", save.Call);
        // FIX A8: "Not a reminder: the Contingency" (its threshold is not recorded).
        Assert.DoesNotContain(a8.Outcome.Reminders, r => r.Text.Contains("Contingency", StringComparison.OrdinalIgnoreCase));
        Assert.True(a8.Line(Belmakor, "14 bludgeoning + 21 necrotic = 35", "temporary HP 7 → 0", "110 → 82"));
        var row = Assert.Single(a8.Log);
        Assert.Equal((L.Damage, 35L, belmakor.Id, Id(Lord)), (row.Kind, row.Amount, row.TargetId, row.ActorId));
        var detail = JsonNode.Parse(row.Detail!)!;
        Assert.Equal(7, detail["temp_absorbed"]!.GetValue<int>());
        Assert.Contains("concentration_on_pre_temp_damage", detail["rulings"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void Belmakor_A9_ConcentrationTotal20AgainstDc17_Kept_NoSaveLeftPending()
    {
        var a9 = At("A9");

        Assert.Equal("Circle of Power", a9.Named(Belmakor).Concentration!.Spell);
        Assert.Empty(a9.Named(Belmakor).Concentration!.Pending);
        Assert.True(a9.Line(Belmakor, "Circle of Power", "20 against DC 17"));
        var row = Assert.Single(a9.Log);
        Assert.Equal((L.Concentration, 20L, (string?)null), (row.Kind, row.Amount, row.RollId));
    }

    [Fact]
    public void Belmakor_A10_A11_SerifThenIgnis_DuringIgnissTurnTheMummyLordMayTakeALegendaryAction()
    {
        Assert.Equal("Serif", At("A10").Persisted.TurnHolder!.Name);
        var a11 = At("A11");
        Assert.Equal("Ignis", a11.Persisted.TurnHolder!.Name);
        Assert.True(a11.Says(K.LegendaryAvailable, "when Ignis's turn ends", Lord, "(3/3)"));
        Assert.StartsWith("combat {\"action\": \"legendary\", \"source\": \"mummy-lord\"", a11.Of(K.LegendaryAvailable).Single().Call, StringComparison.Ordinal);
    }

    [Fact]
    public void Belmakor_A12_LegendaryAttackDuringIgnissTurn_TwoLeft_TorchTo39_NoConcentrationToSave()
    {
        var legendary = At("A12-legendary");
        var a12 = At("A12");

        Assert.Equal("Ignis", legendary.Persisted.TurnHolder!.Name);
        Assert.Equal((2, 1), (legendary.Named(Lord).Legendary!.ActionsLeft, legendary.Named(Lord).Legendary!.Used));
        var row = Assert.Single(legendary.Log);
        Assert.Equal((L.Legendary, "Attack (Rotting Fist)"), (row.Kind, JsonNode.Parse(row.Detail!)!["name"]!.GetValue<string>()));
        Assert.Equal(39, a12.Named(Torch).Hp);
        Assert.Empty(a12.Of(K.ConcentrationSave));
        Assert.Equal(Id(Lord), Assert.Single(a12.Log).ActorId);
    }

    [Fact]
    public void Belmakor_A13_A14_TorchsFireballDoubledByEachSnapshotsVulnerability_41_2_2()
    {
        Assert.Equal(Torch, At("A13").Persisted.TurnHolder!.Name);
        var a14 = At("A14");

        Assert.Equal((41, 2, 2), (a14.Named(Lord).Hp, a14.Named(Mummy).Hp, a14.Named(Mummy2).Hp));
        Assert.Equal(
            ["Mummy Lord: 28 fire ×2 (vulnerable) = 56; 97 → 41", "Mummy: 28 fire ×2 (vulnerable) = 56; 58 → 2", "Mummy 2: 28 fire ×2 (vulnerable) = 56; 58 → 2"],
            a14.Outcome.Lines);
        Assert.Equal([L.Damage, L.Damage, L.Damage], a14.Kinds);
        Assert.Equal([Id(Lord), Id(Mummy), Id(Mummy2)], a14.Log.Select(r => r.TargetId));
    }

    [Fact]
    public void Belmakor_A15_A16_FrightenedFromTheMummyUntilTheEndOfItsNextTurn_ThisTurnsEndSkipped()
    {
        Assert.Equal(Mummy, At("A15").Persisted.TurnHolder!.Name);
        var a16 = At("A16");
        var frightened = Assert.Single(a16.Named(Torch).Conditions);

        Assert.Equal(("frightened", CombatValues.Durations.UntilEndOfSourceTurn, Id(Mummy), true), (frightened.Name, frightened.Duration, frightened.Source, frightened.SkipEnd));
        Assert.Equal(new AppliedAt(1, Id(Mummy)), frightened.Applied);
        Assert.True(a16.Line(Torch, "frightened (Mummy), until the end of Mummy's next turn"));
        Assert.Empty(a16.Of(K.Expiring));
        Assert.Empty(At("A17").Of(K.Expiring));
        Assert.Equal([L.Condition], a16.Kinds);
    }

    [Fact]
    public void Belmakor_A17_TheMummysFistTorchTo19()
    {
        Assert.Equal(19, At("A17").Named(Torch).Hp);
        Assert.True(At("A17").Line(Torch, "10 bludgeoning + 10 necrotic = 20", "39 → 19"));
    }

    [Fact]
    public void Belmakor_A18_Mummy2sFist_TorchDropsToZero_UnconsciousAndProne_DyingWithNoTallies_NotInstantDeath()
    {
        var turn = At("A18-next");
        Assert.Equal(Mummy2, turn.Persisted.TurnHolder!.Name);
        Assert.False(Assert.Single(turn.Named(Torch).Conditions).SkipEnd);

        var a18 = At("A18");
        var torch = a18.Named(Torch);

        Assert.Equal((0, false, true), (torch.Hp, torch.Dead, torch.Dying));
        Assert.Equal(DeathSaveTally.Zero, torch.DeathSaves);
        Assert.True(torch.Has("unconscious"));
        Assert.True(torch.Has("prone"));
        Assert.True(torch.Has("frightened"));
        Assert.True(a18.Says(K.Dropped, Torch, "0 HP", "unconscious"));
        Assert.Empty(a18.Of(K.Died));
        Assert.Equal(Id(Mummy2), Assert.Single(a18.Log).ActorId);
    }

    [Fact]
    public void Belmakor_A19_AidenThenTheOrderWraps_Round2_VarsToAct_TorchsDyingLine()
    {
        Assert.Equal(Aiden, At("A19-aiden").Persisted.TurnHolder!.Name);
        var a19 = At("A19");

        Assert.Equal((2, Vars), (a19.Persisted.Round, a19.Persisted.TurnHolder!.Name));
        Assert.True(a19.Says(K.Round, "Round 2"));
        Assert.True(a19.Says(K.Dying, Torch, "0 HP", "0 successes, 0 failures"));
        Assert.Equal(2L, Assert.Single(a19.Log).Round);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Round 2

    [Fact]
    public void Belmakor_A20_A21_BelmakorsMagicalScimitar_Mummy2DiesAtZero_Defeated()
    {
        Assert.Equal(Belmakor, At("A20").Persisted.TurnHolder!.Name);
        var a21 = At("A21");
        var mummy2 = a21.Named(Mummy2);

        Assert.Equal((0, true, true), (mummy2.Hp, mummy2.Dead, mummy2.Defeated));
        Assert.True(a21.Says(K.Died, "Mummy 2 dies (dropped to 0 hit points) and is defeated."));
        Assert.Equal(mummy2.Id, Assert.Single(a21.Of(K.Died)).CombatantId);
        Assert.Equal([L.Damage, L.Defeat], a21.Kinds);
        Assert.Equal(mummy2.Id, a21.Log[1].TargetId);
        Assert.True(JsonNode.Parse(a21.Log[0].Detail!)!["magical"]!.GetValue<bool>());
    }

    [Fact]
    public void Belmakor_A22_MummyLordsTurn_LegendaryActionsReset3Of3_ItHad2()
    {
        var a22 = At("A22");

        Assert.Equal(Lord, a22.Persisted.TurnHolder!.Name);
        Assert.Equal(new LegendaryState(3, 0, 0, 0), a22.Named(Lord).Legendary);
        Assert.True(a22.Says(K.LegendaryReset, Lord, "reset: 3/3", "2/3"));
    }

    [Fact]
    public void Belmakor_A23_CriticalOnUnconsciousTorch_TwoDeathSaveFailures_NotInstantDeath()
    {
        var a23 = At("A23");
        var torch = a23.Named(Torch);

        Assert.Equal((0, false), (torch.Hp, torch.Dead));
        Assert.Equal(new DeathSaveTally(0, 2, false), torch.DeathSaves);
        Assert.True(a23.Says(K.Dying, Torch, "two death save failures"));

        // §6.3: one dying line for Torch, the step's own (the turn context's "… 2 failures" for him is dropped).
        var dying = Assert.Single(a23.Of(K.Dying));
        Assert.Equal((torch.Id, "Lieutenant James Torch: 0 HP, 0 successes, 2 failures (a critical hit: two death save failures)"), (dying.CombatantId, dying.Text));
        Assert.True(a23.Says(K.UnconsciousCrit, Torch, "a hit from within 5 ft is a critical hit"));
        var row = Assert.Single(a23.Log);
        Assert.Equal(67L, row.Amount);
        Assert.True(JsonNode.Parse(row.Detail!)!["critical"]!.GetValue<bool>());
    }

    [Fact]
    public void Belmakor_A24_SerifIgnisThenTorch_DeathSavingThrowDue_0Successes2Failures()
    {
        Assert.Equal("Serif", At("A24-serif").Persisted.TurnHolder!.Name);
        Assert.Equal("Ignis", At("A24-ignis").Persisted.TurnHolder!.Name);
        var a24 = At("A24");

        Assert.Equal(Torch, a24.Persisted.TurnHolder!.Name);
        Assert.True(a24.Says(K.DeathSaveDue, Torch, "death saving throw", "0 successes, 2 failures"));
        Assert.Equal("combat {\"action\": \"death_save\", \"targets\": [\"torch\"], \"face\": …}", a24.Of(K.DeathSaveDue).Single().Call);
    }

    [Fact]
    public void Belmakor_A25_Natural20_TorchRegains1Hp_Conscious_TalliesReset_StillFrightenedAndProne()
    {
        var a25 = At("A25");
        var torch = a25.Named(Torch);

        Assert.Equal(1, torch.Hp);
        Assert.Equal(DeathSaveTally.Zero, torch.DeathSaves);
        Assert.False(torch.Has("unconscious"));
        Assert.True(torch.Has("frightened"));
        Assert.True(torch.Has("prone"));
        Assert.True(a25.Says(K.Revived, Torch));
        var row = Assert.Single(a25.Log);
        Assert.Equal((L.DeathSave, 20L), (row.Kind, row.Amount));
        Assert.True(JsonNode.Parse(row.Detail!)!["given"]!.GetValue<bool>());
    }

    [Fact]
    public void Belmakor_A26_FrightenedTorchAttacks_TheActorsFrightenedLine_FireBoltDoubled_MummyLordTo9()
    {
        var a26 = At("A26");

        Assert.True(a26.Says(K.ConditionEffects, "Lieutenant James Torch is frightened (Mummy): disadvantage on attack rolls and ability checks while its source is in sight"));
        Assert.Equal(9, a26.Named(Lord).Hp);
        Assert.True(a26.Line(Lord, "16 fire ×2 (vulnerable) = 32", "41 → 9"));
    }

    [Fact]
    public void Belmakor_A27_MummysTurn_FrightenedOnTorchEndsAtTheEndOfThisTurn()
    {
        var a27 = At("A27");

        Assert.Equal(Mummy, a27.Persisted.TurnHolder!.Name);
        Assert.True(a27.Says(K.Expiring, "frightened (Mummy)", Torch, "ends at the end of this turn"));
    }

    [Fact]
    public void Belmakor_A28_FrightenedEndsWithTheMummysTurn_DeadMummy2Skipped_AidensTurn()
    {
        var a28 = At("A28");

        Assert.Equal((Aiden, 2), (a28.Persisted.TurnHolder!.Name, a28.Persisted.Round));
        Assert.False(a28.Named(Torch).Has("frightened"));
        Assert.True(a28.Named(Torch).Has("prone"));
        Assert.Single(a28.Of(K.Expired), r => r.Text.Contains("frightened (Mummy) ended on " + Torch, StringComparison.Ordinal));
        Assert.Equal(Id(Aiden), JsonNode.Parse(Assert.Single(a28.Log).Detail!)!["to"]!.GetValue<string>());
    }

    [Fact]
    public void Belmakor_A28a_PrevRestoresTheStoredFightBeforeTheNext_NextEndsFrightenedAgainOnce()
    {
        var prev = At("A28a-prev");
        var again = At("A28a-next");

        Assert.Equal((Mummy, 2), (prev.Persisted.TurnHolder!.Name, prev.Persisted.Round));
        Assert.Equal(CombatJson.WriteConditions(At("A27").Named(Torch).Conditions), CombatJson.WriteConditions(prev.Named(Torch).Conditions));
        Assert.Equal(CombatPlay.Comparable(At("A27").Persisted), CombatPlay.Comparable(prev.Persisted));
        var row = CombatJson.ReadTurn(Assert.Single(prev.Log).Detail)!;
        Assert.True(row.Prev);
        Assert.True(row.Exact);
        Assert.Equal(Aiden, again.Persisted.TurnHolder!.Name);
        Assert.False(again.Named(Torch).Has("frightened"));
        Assert.Single(again.Of(K.Expired));
        Assert.Equal(CombatPlay.Comparable(At("A28").Persisted), CombatPlay.Comparable(again.Persisted));
    }

    [Fact]
    public void Belmakor_A29_AidensSmite_MummyLordDefeated_RejuvenationQuotedFromItsSnapshot()
    {
        var a29 = At("A29");
        var lord = a29.Named(Lord);

        Assert.Equal((0, true, true), (lord.Hp, lord.Dead, lord.Defeated));
        Assert.True(a29.Says(K.Trait, Lord, "Rejuvenation", "gains a new body in 24 hours if its heart is intact"));
        Assert.Equal([L.Damage, L.Defeat], a29.Kinds);
        Assert.Empty(a29.Of(K.LegendaryAvailable));
    }

    [Fact]
    public void Belmakor_A30_Round3_VarsToAct()
    {
        var a30 = At("A30");

        Assert.Equal((3, Vars), (a30.Persisted.Round, a30.Persisted.TurnHolder!.Name));
        Assert.True(a30.Says(K.Round, "Round 3"));
    }

    [Fact]
    public void Belmakor_A31_LastMummyDown_AllEnemiesAreDefeated_EndTheCombat()
    {
        var a31 = At("A31");

        Assert.True(a31.Named(Mummy).Dead);
        Assert.True(a31.Says(K.AllEnemiesDown, "all enemies are defeated: end the combat?"));
        Assert.StartsWith("combat {\"action\": \"end\"", a31.Of(K.AllEnemiesDown).Single().Call, StringComparison.Ordinal);
        Assert.All(a31.Persisted.Combatants.Where(c => c.Side == CampaignValues.CombatSides.Enemy), c => Assert.True(c.Defeated, c.Name));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // A33 and §2.3: the write-back.

    [Fact]
    public void Belmakor_A33_EndIsOneBatch_ToolCombatEnd_FiledUnderSession4_OnlySheetRows()
    {
        var end = play.End;

        Assert.NotNull(end.BatchId);
        Assert.Equal(play.ChangeRowsAfterEnd - play.N0, play.EndRows.Count);
        Assert.Single(play.EndRows.Select(r => r.BatchId).Distinct());
        Assert.All(play.EndRows, r => Assert.Equal((CampaignTables.CharacterSheet.Name, "claude", "combat/end", play.SessionId), (r.TargetTable, r.Actor, r.Tool, r.SessionId)));
        Assert.Equal(
            [("character:belmakor", "concentration"), ("character:belmakor", "hp"), ("character:belmakor", "resources.bladesong"),
             ("character:belmakor", "spell_slots.5"), ("character:belmakor", "temp_hp"), ("character:torch", "hp")],
            play.EndRows.Select(r => (Handle(r.EntityId!), r.FieldPath!)).Order());
        Assert.Equal((4, false, false), (end.SessionNumber, end.DryRun, end.Discarded));
    }

    [Fact]
    public void Belmakor_A33_Section23_Belmakor_Hp82_Temp0_Slot5Used1_Bladesong1_CircleOfPowerKept98Rounds_ConditionsEmpty()
    {
        var before = play.SheetRowsBefore["character:belmakor"];
        var after = play.SheetRowsAfter["character:belmakor"];

        Assert.Equal(["concentration", "hp", "resources", "spell_slots", "temp_hp", "updated_at"], CombatPlay.ChangedColumns(before, after));
        Assert.Equal(("110", "82"), (CombatPlay.Text(before, "hp"), CombatPlay.Text(after, "hp")));
        Assert.Equal(("7", "0"), (CombatPlay.Text(before, "temp_hp"), CombatPlay.Text(after, "temp_hp")));
        Assert.Null(CombatPlay.Text(before, "concentration"));
        Assert.Equal("""{"spell":"Circle of Power","level":5,"remaining_rounds":98,"note":"from The crypt (fixture), round 1"}""", CombatPlay.Text(after, "concentration"));
        Assert.Equal("[]", CombatPlay.Text(after, "conditions"));
        var slots = JsonNode.Parse(CombatPlay.Text(after, "spell_slots")!)!.AsObject();
        var slotsBefore = JsonNode.Parse(CombatPlay.Text(before, "spell_slots")!)!.AsObject();
        Assert.Equal((0, 1), (slotsBefore["5"]!["used"]!.GetValue<int>(), slots["5"]!["used"]!.GetValue<int>()));
        Assert.Equal(1, slots["1"]!["used"]!.GetValue<int>());
        Assert.All(slots.Where(s => s.Key != "5"), s => Assert.Equal(slotsBefore[s.Key]!.ToJsonString(), s.Value!.ToJsonString()));
        var resources = JsonNode.Parse(CombatPlay.Text(after, "resources")!)!.AsObject();
        var resourcesBefore = JsonNode.Parse(CombatPlay.Text(before, "resources")!)!.AsObject();
        Assert.Equal((0, 1), (resourcesBefore["bladesong"]!["used"]!.GetValue<int>(), resources["bladesong"]!["used"]!.GetValue<int>()));
        Assert.Equal("set", resources["contingency"]!["state"]!.GetValue<string>());
        Assert.All(resources.Where(r => r.Key != "bladesong"), r => Assert.Equal(resourcesBefore[r.Key]!.ToJsonString(), r.Value!.ToJsonString()));
    }

    [Fact]
    public void Belmakor_A33_Section23_Torch_Hp74To1_NoConditionOrDeathSaveWritten_TheFourWithoutHpUntouched()
    {
        Assert.Equal(["hp", "updated_at"], CombatPlay.ChangedColumns(play.SheetRowsBefore["character:torch"], play.SheetRowsAfter["character:torch"]));
        Assert.Equal(("74", "1"), (CombatPlay.Text(play.SheetRowsBefore["character:torch"], "hp"), CombatPlay.Text(play.SheetRowsAfter["character:torch"], "hp")));
        foreach (var handle in new[] { "character:aiden-ironstar", "character:ignis", "character:serif", "character:vars" })
        {
            Assert.Empty(CombatPlay.ChangedColumns(play.SheetRowsBefore[handle], play.SheetRowsAfter[handle]));
        }

        Assert.Contains(play.End.Summary, s => s.StartsWith("No hit points tracked", StringComparison.Ordinal) && s.Contains("Aiden Ironstar, Ignis, Serif, Vars Nocturne", StringComparison.Ordinal));
    }

    [Fact]
    public void Belmakor_A33_Xp_NoAwardNoSheetChange_TheSummarySaysWorth14400_2400EachFor6()
    {
        Assert.Empty(play.End.Awards);
        Assert.Equal(0, play.World.F.Count("SELECT count(*) FROM award"));
        Assert.Equal((14_400, 0), (play.End.Xp.Worth, play.End.Xp.Awarded));
        Assert.Contains("14,400 XP, 2,400 each for 6", play.End.Xp.Text, StringComparison.Ordinal);
        Assert.Contains(play.End.Summary, s => s.Contains("14,400 XP, 2,400 each for 6", StringComparison.Ordinal));
        Assert.DoesNotContain(play.EndRows, r => r.FieldPath == "xp");
    }

    [Fact]
    public void Belmakor_A33_TheSummarySaysWhatEnded_BladesongAndProne_RejuvenationRepeated_NoProposal()
    {
        Assert.Contains("Ended with the fight: Bladesong (Belmakor Silverwind); prone (Lieutenant James Torch).", play.End.Summary);
        Assert.Contains(play.End.Summary, s => s.Contains("Circle of Power kept (98 rounds left)", StringComparison.Ordinal));
        Assert.Contains(play.End.Reminders, r => r.Kind == K.Trait && r.Text.Contains("Rejuvenation", StringComparison.Ordinal));
        Assert.Empty(play.End.Proposals);
        Assert.Empty(play.End.Overwritten);
    }

    [Fact]
    public void Belmakor_A33_TheFightEnds_OutcomeAndBatchOnTheRow_AnEndRow_NoDie()
    {
        Assert.Equal((CampaignValues.EncounterStatuses.Ended, "The crypt is cleared.", play.End.BatchId), (play.Encounter.Status, play.Encounter.OutcomeMd, play.Encounter.WritebackBatchId));
        Assert.NotNull(play.Encounter.EndedAt);
        Assert.Equal([L.End], play.EndLog.Select(r => r.Kind));
        Assert.Empty(play.EndDice);
        Assert.Equal(WritebackStatuses.Applied, play.World.Reader.State(play.World.Campaign, EncounterResolver.Last).Encounter!.WritebackStatus);
    }

    [Fact]
    public void Belmakor_A33_TheWriteBackIsSession4s_AsOfSession3ThePartyReadsTheSheetsAsTheyWere()
    {
        var reads = new ScenarioReads(play.World.Database);

        var before = reads.Get(play.World.Campaign, "party", new EntityIncludes(Sheet: true), 3, "character:belmakor", "character:torch").Entities;
        var after = reads.Get(play.World.Campaign, "party", new EntityIncludes(Sheet: true), 4, "character:belmakor", "character:torch").Entities;
        var now = reads.Get(play.World.Campaign, "party", new EntityIncludes(Sheet: true), null, "character:belmakor", "character:torch").Entities;

        Assert.Equal([(110, 7), (74, 0)], before.Select(e => (e.Sheet!.Line!.Hp!.Value, e.Sheet.Line.TempHp)));
        Assert.Equal([(82, 0), (1, 0)], after.Select(e => (e.Sheet!.Line!.Hp!.Value, e.Sheet.Line.TempHp)));
        Assert.Equal([(82, 0), (1, 0)], now.Select(e => (e.Sheet!.Line!.Hp!.Value, e.Sheet.Line.TempHp)));
    }

    [Fact]
    public void Belmakor_A33_EndedAfterBelmakorActsInRound3_CircleOfPowerKeeps97Rounds()
    {
        using var w = CombatWorld.Belmakor();
        CombatScripts.FixtureA(w);
        Assert.Equal(Belmakor, w.Combat.Next(w.Campaign, null).Encounter.TurnName);
        Assert.Equal("Serif", w.Combat.Next(w.Campaign, null).Encounter.TurnName);

        CombatScripts.EndA(w);

        // §6.7: expires.round − round − 1 once the anchor has acted this round: 101 − 3 − 1.
        var concentration = w.SheetOf("character:belmakor").Concentration!;
        Assert.Equal(("Circle of Power", 97, "from The crypt (fixture), round 1"), (concentration.Spell, concentration.RemainingRounds, concentration.Note));
    }

    private string Handle(string entityId) =>
        play.World.F.Scalar<string>("SELECT kind || ':' || slug FROM entity WHERE id = @entityId", new { entityId });
}
