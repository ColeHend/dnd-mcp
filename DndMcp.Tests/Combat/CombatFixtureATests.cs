using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: fixture A (FIX §2, Belmakor's 2014 player campaign, as amended by contract §16) runs as pure state
/// transitions of the tracker, step for step, with every value given: the arithmetic and the reminders each step must carry
/// (A1-A31), <c>prev</c>'s exact revert (A28a), and the §2.3 write-back out of <see cref="CombatEnd.Plan"/> (A33):
/// Belmakor 82 HP, temp 0, one 5th-level slot and one Bladesong spent, Circle of Power kept with 98 rounds left;
/// Torch at 1 HP; nothing else written; no XP (no party sheet tracks it), worth 14,400 / 2,400 each for 6.
/// </summary>
public sealed class CombatFixtureATests
{
    private static readonly Lazy<Script> Played = new(() => new Script());

    /// <summary>The whole script, run once; each step's result kept by its FIX number.</summary>
    private sealed class Script
    {
        public Dictionary<string, CombatStepResult> At { get; } = new(StringComparer.Ordinal);

        public List<CombatChange> Log { get; } = [];

        public EncounterState Final { get; }

        public EndPlan End { get; }

        public Script()
        {
            var s = Encounter(E2014, "The crypt (fixture)", player: true);
            s = Do("A1", CombatKit.Add(s, [.. PartyA()]));
            s = Do("A2", CombatKit.Add(s, Monster(Block(E2014, "mummy-lord"), hp: HpChoice.Avg), Monster(Block(E2014, "mummy"), 2, HpChoice.Avg)));
            s = Do("A3", Step(s, new InitiativeOp
            {
                Rolls =
                [
                    new("belmakor", Face: 17), new("mummy-lord", Face: 18), new("mummy", Face: 10), new("vars", Total: 25),
                    new("serif", Total: 16), new("ignis", Total: 14), new("torch", Total: 12), new("aiden-ironstar", Total: 7),
                ],
            }));
            s = Do("A4", Step(s, new NextOp()));
            s = Do("A5", Step(s, new ConditionOp(["belmakor"]) { Add = ["Bladesong"], Effect = new EffectInput(Ac: 5), Duration = "1 minute", Resource = "Bladesong" }));
            s = Do("A6", Step(s, new ConcentrationOp(["belmakor"]) { Spell = "Circle of Power", SlotLevel = 5, Duration = "10 minutes" }));
            s = Do("A7", Step(s, new NextOp()));
            s = Do("A8", Step(s, new DamageOp(["belmakor"]) { Parts = [new(14, null, "bludgeoning"), new(21, null, "necrotic")], Source = "mummy-lord" }));
            s = Do("A9", Step(s, new ConcentrationOp(["belmakor"]) { Total = 20 }));
            s = Do("A10", Step(s, new NextOp()));
            s = Do("A11", Step(s, new NextOp()));
            s = Do("A12-legendary", Step(s, new LegendaryOp("mummy-lord") { Amount = 1, Name = "Attack (Rotting Fist)" }));
            s = Do("A12", Step(s, new DamageOp(["torch"]) { Parts = [new(14, null, "bludgeoning"), new(21, null, "necrotic")], Source = "mummy-lord" }));
            s = Do("A13", Step(s, new NextOp()));
            s = Do("A14", Step(s, new DamageOp(["mummy-lord", "mummy", "mummy-2"]) { Parts = [new(28, null, "fire")] }));
            s = Do("A15", Step(s, new NextOp()));
            s = Do("A16", Step(s, new ConditionOp(["torch"]) { Add = ["frightened"], Source = "mummy", Duration = "until_end_of_source_turn" }));
            s = Do("A17", Step(s, new DamageOp(["torch"]) { Parts = [new(10, null, "bludgeoning"), new(10, null, "necrotic")], Source = "mummy" }));
            s = Do("A18-next", Step(s, new NextOp()));
            s = Do("A18", Step(s, new DamageOp(["torch"]) { Parts = [new(10, null, "bludgeoning"), new(10, null, "necrotic")], Source = "mummy-2" }));
            s = Do("A19-aiden", Step(s, new NextOp()));
            s = Do("A19", Step(s, new NextOp()));
            s = Do("A20", Step(s, new NextOp()));
            s = Do("A21", Step(s, new DamageOp(["mummy-2"]) { Parts = [new(8, null, "slashing")], Magical = true }));
            s = Do("A22", Step(s, new NextOp()));
            s = Do("A23", Step(s, new DamageOp(["torch"]) { Parts = [new(25, null, "bludgeoning"), new(42, null, "necrotic")], Critical = true, Source = "mummy-lord" }));
            s = Do("A24-serif", Step(s, new NextOp()));
            s = Do("A24-ignis", Step(s, new NextOp()));
            s = Do("A24", Step(s, new NextOp()));
            s = Do("A25", Step(s, new DeathSaveOp(["torch"]) { Face = 20 }));
            s = Do("A26", Step(s, new DamageOp(["mummy-lord"]) { Parts = [new(16, null, "fire")] }));
            s = Do("A27", Step(s, new NextOp()));
            s = Do("A28", Step(s, new NextOp()));
            var turnRow = At["A28"].Changes.Last(c => c.Kind == L.Turn);
            s = Do("A28a-prev", Step(s, new PrevOp(new CombatLogEntry(turnRow.Kind, turnRow.Detail))));
            s = Do("A28a-next", Step(s, new NextOp()));
            s = Do("A29", Step(s, new DamageOp(["mummy-lord"]) { Parts = [new(12, null, "radiant")] }));
            s = Do("A30", Step(s, new NextOp()));
            s = Do("A31", Step(s, new DamageOp(["mummy"]) { Parts = [new(10, null, "piercing")], Magical = true }));
            Final = s;
            var sheets = PartyA().ToDictionary(p => p.EntityId!, p => p.Sheet!);
            End = CombatEnd.Plan(s, new EndOptions(), new EndInputs
            {
                Sheets = sheets,
                SafeNames = s.Combatants.ToDictionary(c => c.Id, c => c.Name),
                CampaignSlug = "belmakor",
            });
        }

        private EncounterState Do(string step, CombatStepResult result)
        {
            At[step] = result;
            Log.AddRange(result.Changes);
            return result.Next;
        }
    }

    private static Script A => Played.Value;

    private static CombatantState In(string step, string name) => A.At[step].Next.Named(name);

    // ------------------------------------------------------------------------------------------------------------------
    // A1-A3: the party from sheets, the mummies, initiative.

    [Fact]
    public void A1_PartyFromSheets_BelmakorAndTorchSeeded_FourWithoutHitPoints()
    {
        var s = A.At["A1"].Next;
        Assert.Equal(["Aiden Ironstar", "Belmakor Silverwind", "Ignis", "Lieutenant James Torch", "Serif", "Vars Nocturne"], s.Combatants.Select(c => c.Name));
        Assert.All(s.Combatants, c => Assert.True(c.IsSheetSeeded));
        Assert.All(s.Combatants, c => Assert.Equal(CampaignValues.CombatSides.Party, c.Side));
        var belmakor = s.Named("Belmakor Silverwind");
        Assert.Equal((110, 110, 7, 17, 5), (belmakor.Hp, belmakor.MaxHp, belmakor.TempHp, belmakor.Ac, belmakor.InitBonus));
        Assert.Equal(1, belmakor.Resources["slot:1"].Used);
        Assert.Equal((4, 0), (belmakor.Resources["bladesong"].Max, belmakor.Resources["bladesong"].Used));
        Assert.Equal("set", belmakor.Resources["contingency"].State);
        Assert.Equal(74, s.Named("Lieutenant James Torch").Hp);
        var noHp = A.At["A1"].Of(K.NoHp).Select(r => s.Find(r.CombatantId)!.Name).ToList();
        Assert.Equal(["Aiden Ironstar", "Ignis", "Serif", "Vars Nocturne"], noHp);
        Assert.Contains(A.At["A1"].Of(K.NoHp), r => r.Call == "combat {\"action\": \"set\", \"combatants\": [{\"character\": \"character:vars\", \"hp\": …}]}");
        Assert.Equal(0, s.Round);
    }

    [Fact]
    public void A2_MummyLordAndTwoMummies_AverageHpAndOneInitGroup()
    {
        var s = A.At["A2"].Next;
        var lord = s.Named("Mummy Lord");
        Assert.Equal((97, 97, 17, 0), (lord.Hp, lord.MaxHp, lord.Ac, lord.InitBonus));
        Assert.Equal(new LegendaryState(3, 0, 0, 0), lord.Legendary);
        Assert.Equal("2014/monster/mummy-lord", lord.SrdRef);
        var mummy = s.Named("Mummy");
        var mummy2 = s.Named("Mummy 2");
        Assert.Equal((58, 11, -1), (mummy.Hp, mummy.Ac, mummy.InitBonus));
        Assert.Equal(58, mummy2.Hp);
        Assert.NotNull(mummy.InitGroup);
        Assert.Equal(mummy.InitGroup, mummy2.InitGroup);
        Assert.Null(lord.InitGroup);
    }

    [Fact]
    public void A3_Initiative_OrderAndRoundOneStartsWithVars()
    {
        var s = A.At["A3"].Next;
        Assert.Equal(
            ["Vars Nocturne", "Belmakor Silverwind", "Mummy Lord", "Serif", "Ignis", "Lieutenant James Torch", "Mummy", "Mummy 2", "Aiden Ironstar"],
            s.Order.Select(c => c.Name));
        Assert.Equal(22, s.Named("Belmakor Silverwind").Initiative);
        Assert.Equal(18, s.Named("Mummy Lord").Initiative);
        Assert.Equal(9, s.Named("Mummy").Initiative);
        Assert.Equal(9, s.Named("Mummy 2").Initiative);
        Assert.Equal(1, s.Round);
        Assert.Equal("Vars Nocturne", s.TurnHolder!.Name);
        Assert.Empty(A.At["A3"].Of(K.Tie));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Round 1

    [Fact]
    public void A5_Bladesong_SpendsAUseAndRaisesTheAcTo22ForTenRounds()
    {
        var belmakor = In("A5", "Belmakor Silverwind");
        Assert.Equal(1, belmakor.Resources["bladesong"].Used);
        Assert.Equal(22, belmakor.DisplayedAc);
        var bladesong = Assert.Single(belmakor.Conditions);
        Assert.Equal(("Bladesong", CombatValues.Durations.Rounds), (bladesong.Name, bladesong.Duration));
        Assert.Equal(new ConditionExpiry(11, CombatValues.ExpiryPoints.Start, belmakor.Id), bladesong.Expires);
        Assert.Contains(A.At["A5"].Notes, n => n.Contains("not an SRD condition: tracked as an effect", StringComparison.Ordinal));
        Assert.Contains(A.At["A5"].Changes, c => c.Kind == L.Resource);
    }

    [Fact]
    public void A6_CircleOfPower_SpendsAFifthLevelSlot_ExpiresAtRound101()
    {
        var belmakor = In("A6", "Belmakor Silverwind");
        Assert.Equal(1, belmakor.Resources["slot:5"].Used);
        Assert.Equal("Circle of Power", belmakor.Concentration!.Spell);
        Assert.Equal(5, belmakor.Concentration.Level);
        Assert.Equal(new ConditionExpiry(101, CombatValues.ExpiryPoints.Start, belmakor.Id), belmakor.Concentration.Expires);
    }

    [Fact]
    public void A7_MummyLordsTurn_LegendaryActions3Of3()
    {
        Assert.Equal("Mummy Lord", A.At["A7"].Next.TurnHolder!.Name);
        Assert.True(A.At["A7"].Says(K.LegendaryReset, "Mummy Lord", "3/3"));
        Assert.DoesNotContain(A.At["A7"].Reminders, r => r.Kind == K.LegendaryAvailable);
    }

    [Fact]
    public void A8_TempHpAbsorbs7_HpIs82_ConcentrationDc17()
    {
        var belmakor = In("A8", "Belmakor Silverwind");
        Assert.Equal((82, 0), (belmakor.Hp, belmakor.TempHp));
        Assert.Equal([17], belmakor.Concentration!.Pending);
        Assert.True(A.At["A8"].Says(K.ConcentrationSave, "Belmakor Silverwind", "concentration", "Circle of Power", "DC 17"));
        var save = A.At["A8"].Of(K.ConcentrationSave).Single();
        Assert.Equal("combat {\"action\": \"concentration\", \"targets\": [\"belmakor\"], \"total\": …}", save.Call);
        var row = A.At["A8"].Changes.Single(c => c.Kind == L.Damage);
        Assert.Equal(35, row.Amount);
        Assert.Equal(belmakor.Id, row.TargetId);
        Assert.Equal(A.At["A8"].Next.Named("Mummy Lord").Id, row.ActorId);
        var detail = JsonNode.Parse(row.Detail!)!;
        Assert.True(detail["given"]!.GetValue<bool>());
        Assert.Equal(7, detail["temp_absorbed"]!.GetValue<int>());
        Assert.Contains(RulingFlags.ConcentrationOnPreTempDamage, detail["rulings"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void A9_ConcentrationTotal20_KeepsIt()
    {
        var belmakor = In("A9", "Belmakor Silverwind");
        Assert.Equal("Circle of Power", belmakor.Concentration!.Spell);
        Assert.Empty(belmakor.Concentration.Pending);
        var row = A.At["A9"].Changes.Single();
        Assert.Equal(L.Concentration, row.Kind);
        Assert.Equal(20, row.Amount);
        Assert.Null(row.RollKey);
    }

    [Fact]
    public void A12_LegendaryDuringIgnissTurn_TwoLeft_TorchTo39()
    {
        Assert.Equal("Ignis", A.At["A12-legendary"].Next.TurnHolder!.Name);
        Assert.Equal(2, In("A12-legendary", "Mummy Lord").Legendary!.ActionsLeft);
        Assert.Equal(39, In("A12", "Lieutenant James Torch").Hp);
        Assert.Empty(A.At["A12"].Of(K.ConcentrationSave));
    }

    [Fact]
    public void A11_IgnissTurn_RemindsTheMummyLordMayTakeALegendaryAction()
    {
        Assert.True(A.At["A11"].Says(K.LegendaryAvailable, "when Ignis's turn ends", "Mummy Lord", "(3/3)"));
        Assert.Contains(A.At["A11"].Of(K.LegendaryAvailable), r => r.Call!.StartsWith("combat {\"action\": \"legendary\", \"source\": \"mummy-lord\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A14_FireballDoubledByVulnerability_41_2_2()
    {
        Assert.Equal(41, In("A14", "Mummy Lord").Hp);
        Assert.Equal(2, In("A14", "Mummy").Hp);
        Assert.Equal(2, In("A14", "Mummy 2").Hp);
        Assert.Contains(A.At["A14"].Notes, n => n.StartsWith("Mummy Lord: 28 fire ×2 (vulnerable) = 56; 97 → 41", StringComparison.Ordinal));
        Assert.Equal(3, A.At["A14"].Changes.Count(c => c.Kind == L.Damage));
    }

    [Fact]
    public void A16_FrightenedUntilTheEndOfTheMummysTurn_SkipsTheTurnItWasAppliedIn()
    {
        var frightened = Assert.Single(In("A16", "Lieutenant James Torch").Conditions);
        Assert.Equal(("frightened", CombatValues.Durations.UntilEndOfSourceTurn), (frightened.Name, frightened.Duration));
        Assert.Equal(A.At["A16"].Next.Named("Mummy").Id, frightened.Source);
        Assert.True(frightened.SkipEnd);
        Assert.Equal(new AppliedAt(1, A.At["A16"].Next.Named("Mummy").Id), frightened.Applied);

        // This turn's end is skipped, so nothing is said to end at it (A27 says it, a round later).
        Assert.Empty(A.At["A16"].Of(K.Expiring));
        Assert.Empty(A.At["A17"].Of(K.Expiring));
    }

    [Fact]
    public void A18_MummysTurnEnds_FrightenedStays_TorchDropsToZeroUnconsciousDying()
    {
        var afterNext = In("A18-next", "Lieutenant James Torch");
        var frightened = Assert.Single(afterNext.Conditions);
        Assert.False(frightened.SkipEnd);
        var torch = In("A18", "Lieutenant James Torch");
        Assert.Equal(0, torch.Hp);
        Assert.True(torch.Dying);
        Assert.Equal(DeathSaveTally.Zero, torch.DeathSaves);
        Assert.True(torch.Has("unconscious"));
        Assert.True(torch.Has("prone"));
        Assert.True(A.At["A18"].Says(K.Dropped, "Lieutenant James Torch", "0 HP", "unconscious"));
        Assert.False(torch.Dead);
    }

    [Fact]
    public void A19_TheOrderWraps_Round2_VarsToAct()
    {
        Assert.Equal("Aiden Ironstar", A.At["A19-aiden"].Next.TurnHolder!.Name);
        var s = A.At["A19"].Next;
        Assert.Equal(2, s.Round);
        Assert.Equal("Vars Nocturne", s.TurnHolder!.Name);
        Assert.True(A.At["A19"].Says(K.Round, "Round 2"));
        Assert.True(A.At["A19"].Says(K.Dying, "Lieutenant James Torch", "0 HP", "0 successes, 0 failures"));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Round 2

    [Fact]
    public void A21_MagicalScimitar_Mummy2Dies_Defeated()
    {
        var mummy2 = In("A21", "Mummy 2");
        Assert.Equal(0, mummy2.Hp);
        Assert.True(mummy2.Dead);
        Assert.True(mummy2.Defeated);

        // One line says both (U13); the defeat row is still written.
        Assert.Equal(["Mummy 2 dies (dropped to 0 hit points) and is defeated."], A.At["A21"].Reminders.Where(r => r.CombatantId == mummy2.Id).Select(r => r.Text));
        Assert.True(A.At["A21"].Says(K.Died, "Mummy 2 dies", "and is defeated"));
        Assert.Empty(A.At["A21"].Of(K.Defeated));
        Assert.Contains(A.At["A21"].Changes, c => c.Kind == L.Defeat && c.TargetId == mummy2.Id);
    }

    [Fact]
    public void A22_MummyLordsTurn_LegendaryActionsReset3Of3()
    {
        Assert.Equal(new LegendaryState(3, 0, 0, 0), In("A22", "Mummy Lord").Legendary);
        Assert.True(A.At["A22"].Says(K.LegendaryReset, "Mummy Lord", "reset: 3/3", "2/3"));
    }

    [Fact]
    public void A23_CriticalAtZero_TwoDeathSaveFailures()
    {
        var torch = In("A23", "Lieutenant James Torch");
        Assert.Equal(new DeathSaveTally(0, 2, false), torch.DeathSaves);
        Assert.False(torch.Dead);
        Assert.True(A.At["A23"].Says(K.Dying, "Lieutenant James Torch", "two death save failures"));
        Assert.True(A.At["A23"].Says(K.UnconsciousCrit, "Lieutenant James Torch", "a hit from within 5 ft is a critical hit"));
    }

    [Fact]
    public void A24_TorchsTurnStarts_DeathSaveDueWithTallies()
    {
        Assert.Equal("Lieutenant James Torch", A.At["A24"].Next.TurnHolder!.Name);
        Assert.True(A.At["A24"].Says(K.DeathSaveDue, "Lieutenant James Torch", "death saving throw", "0 successes, 2 failures"));
        Assert.Equal("combat {\"action\": \"death_save\", \"targets\": [\"torch\"], \"face\": …}", A.At["A24"].Of(K.DeathSaveDue).Single().Call);
        Assert.DoesNotContain(A.At["A24"].Reminders, r => r.Kind == K.Dying);
    }

    [Fact]
    public void A25_Natural20_OneHpConscious_TalliesReset_ProneAndFrightenedStay()
    {
        var torch = In("A25", "Lieutenant James Torch");
        Assert.Equal(1, torch.Hp);
        Assert.Equal(DeathSaveTally.Zero, torch.DeathSaves);
        Assert.False(torch.Has("unconscious"));
        Assert.True(torch.Has("prone"));
        Assert.True(torch.Has("frightened"));
        Assert.True(A.At["A25"].Says(K.Revived, "Lieutenant James Torch"));
    }

    [Fact]
    public void A26_TorchFrightenedAttacks_ActorLineAndMummyLordTo9()
    {
        Assert.True(A.At["A26"].Says(K.ConditionEffects, "Lieutenant James Torch is frightened (Mummy)", "disadvantage on attack rolls and ability checks while its source is in sight"));
        Assert.Equal(9, In("A26", "Mummy Lord").Hp);
    }

    [Fact]
    public void A27_MummysTurn_FrightenedOnTorchEndsAtTheEndOfThisTurn()
    {
        Assert.Equal("Mummy", A.At["A27"].Next.TurnHolder!.Name);
        Assert.True(A.At["A27"].Says(K.Expiring, "frightened (Mummy)", "Lieutenant James Torch", "ends at the end of this turn"));
    }

    [Fact]
    public void A28_FrightenedEnds_DeadMummy2Skipped_AidensTurn()
    {
        Assert.Equal("Aiden Ironstar", A.At["A28"].Next.TurnHolder!.Name);
        Assert.False(In("A28", "Lieutenant James Torch").Has("frightened"));
        Assert.Single(A.At["A28"].Of(K.Expired), r => r.Text.Contains("frightened (Mummy) ended on Lieutenant James Torch", StringComparison.Ordinal));
    }

    [Fact]
    public void A28a_PrevRestoresFrightenedExactly_NextEndsItAgainOnce()
    {
        var back = A.At["A28a-prev"].Next;
        Assert.Equal("Mummy", back.TurnHolder!.Name);
        Assert.Equal(2, back.Round);
        Assert.True(back.Named("Lieutenant James Torch").Has("frightened"));
        Assert.Equal(CombatJson.WriteConditions(In("A27", "Lieutenant James Torch").Conditions), CombatJson.WriteConditions(back.Named("Lieutenant James Torch").Conditions));
        var prevRow = Assert.Single(A.At["A28a-prev"].Changes);
        Assert.Equal(L.Turn, prevRow.Kind);
        var record = CombatJson.ReadTurn(prevRow.Detail)!;
        Assert.True(record.Prev);
        Assert.True(record.Exact);

        var again = A.At["A28a-next"];
        Assert.Equal("Aiden Ironstar", again.Next.TurnHolder!.Name);
        Assert.False(again.Next.Named("Lieutenant James Torch").Has("frightened"));
        Assert.Single(again.Of(K.Expired), r => r.Text.Contains("frightened", StringComparison.Ordinal));
    }

    [Fact]
    public void A29_DivineSmite_MummyLordDefeated_RejuvenationQuoted()
    {
        var lord = In("A29", "Mummy Lord");
        Assert.True(lord.Dead);
        Assert.True(lord.Defeated);
        Assert.True(A.At["A29"].Says(K.Trait, "Rejuvenation"));
    }

    [Fact]
    public void A30_Round3_VarsToAct()
    {
        Assert.Equal(3, A.At["A30"].Next.Round);
        Assert.Equal("Vars Nocturne", A.At["A30"].Next.TurnHolder!.Name);
    }

    [Fact]
    public void A31_LastMummyDown_AllEnemiesAreDefeated()
    {
        Assert.True(In("A31", "Mummy").Dead);
        Assert.True(A.At["A31"].Says(K.AllEnemiesDown, "all enemies are defeated"));
    }

    [Fact]
    public void A32_EveryValueGiven_NoRollsAndOnlyCombatKinds()
    {
        Assert.All(A.Log, c => Assert.Null(c.RollKey));
        Assert.All(A.Log, c => Assert.True(CampaignValues.CombatLogKinds.Set.Contains(c.Kind)));
        var kinds = A.Log.Select(c => c.Kind).ToHashSet();
        foreach (var kind in new[] { L.Turn, L.Damage, L.Condition, L.Concentration, L.DeathSave, L.Legendary, L.Resource, L.Defeat, L.Initiative, L.Add })
        {
            Assert.Contains(kind, kinds);
        }

        Assert.Equal(3, A.Log.Count(c => c.Kind == L.Defeat));
        Assert.All(A.Log.Where(c => c.Kind == L.Damage), c => Assert.True(JsonNode.Parse(c.Detail!)!["given"]!.GetValue<bool>()));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // A33 and §2.3: the write-back.

    [Fact]
    public void A33_WriteBack_OnlyBelmakorAndTorch()
    {
        Assert.Equal(["Belmakor Silverwind", "Lieutenant James Torch"], A.End.WriteBacks.Select(w => w.Name).Order(StringComparer.Ordinal));
        Assert.Empty(A.End.Items);
        Assert.Empty(A.End.Awards);
        Assert.Empty(A.End.Proposals);
        Assert.Empty(A.End.Overwritten);
    }

    [Fact]
    public void A33_Belmakor_Hp82Temp0_FifthSlotAndBladesongOneUsed_CircleOfPower98RoundsLeft()
    {
        var write = A.End.WriteBacks.Single(w => w.Name == "Belmakor Silverwind");
        Assert.Equal(82, write.After.Hp);
        Assert.Equal(0, write.After.TempHp);
        Assert.Equal(1, write.After.SpellSlots["5"].Used);
        Assert.Equal(1, write.After.SpellSlots["1"].Used);
        Assert.Equal(1, write.After.Resources["bladesong"].Used);
        Assert.Equal("set", write.After.Resources["contingency"].State);
        Assert.Empty(write.After.Conditions);
        Assert.Equal(
            """{"spell":"Circle of Power","level":5,"remaining_rounds":98,"note":"from The crypt (fixture), round 1"}""",
            SheetJson.WriteConcentration(write.After.Concentration));

        // The diff the Repository logs: hp, temp_hp, concentration whole; spell_slots and resources as patches of the changed keys only.
        Assert.Equal([SheetColumns.Hp, SheetColumns.TempHp, SheetColumns.Concentration], write.Diff.Columns.Keys);
        Assert.Equal("""{"5":{"used":1}}""", SheetJson.Serialize(write.Diff.Patches[SheetColumns.SpellSlots]));
        Assert.Equal("""{"bladesong":{"used":1}}""", SheetJson.Serialize(write.Diff.Patches[SheetColumns.Resources]));
    }

    [Fact]
    public void A33_Torch_Hp1_NoConditionOrDeathSaveRow()
    {
        var write = A.End.WriteBacks.Single(w => w.Name == "Lieutenant James Torch");
        Assert.Equal(1, write.After.Hp);
        Assert.Equal([SheetColumns.Hp], write.Diff.Columns.Keys);
        Assert.Empty(write.Diff.Patches);
    }

    [Fact]
    public void A33_Xp_NotAwarded_WorthFourteenThousandFourHundred_TwoThousandFourHundredEachForSix()
    {
        Assert.Equal(0, A.End.Xp.Awarded);
        Assert.Equal(14_400, A.End.Xp.Worth);
        Assert.Contains("14,400 XP, 2,400 each for 6", A.End.Xp.Text, StringComparison.Ordinal);
        Assert.Contains("no party sheet tracks XP", A.End.Xp.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A33_Summary_BladesongAndProneEnded_RejuvenationRepeated_NoHpListed()
    {
        var ended = A.End.Summary.Single(s => s.StartsWith("Ended with the fight:", StringComparison.Ordinal));
        Assert.Contains("Bladesong (Belmakor Silverwind)", ended, StringComparison.Ordinal);
        Assert.Contains("prone (Lieutenant James Torch)", ended, StringComparison.Ordinal);
        Assert.Contains(A.End.Summary, s => s.Contains("No hit points tracked", StringComparison.Ordinal) && s.Contains("Aiden Ironstar, Ignis, Serif, Vars Nocturne", StringComparison.Ordinal));
        Assert.Contains(A.End.Reminders, r => r.Kind == K.Trait && r.Text.Contains("Rejuvenation", StringComparison.Ordinal));
        Assert.False(A.End.IsEmpty);
    }
}
