using System.Text.Json.Nodes;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: <c>condition</c>, <c>concentration</c> and <c>use</c> (contract §6.5, §6.6) change only what they say: an SRD
/// condition is matched forgivingly and anything else is a named effect; a duplicate from the same source is refused, an
/// immunity is "no effect" for that target; Unconscious brings Prone; an incapacitating condition breaks concentration and
/// ends the grapples its bearer holds; exhaustion is a column changed by levels (the maximum and death follow);
/// <c>resource</c> spends one use while adding. A concentration starts (ending the previous and what it held), drops,
/// or resolves the OLDEST pending save, given or rolled with the Con bonus of the sheet or the stat block.
/// </summary>
public sealed class CombatConditionTests
{
    private static EncounterState Abc(string edition = E2024) => Fight(edition, ("A", 20), ("B", 15), ("C", 10));

    // ------------------------------------------------------------------------------------------------------------------
    // condition

    [Fact]
    public void Add_SrdNameMatchedForgivingly_OtherNamesAreEffects()
    {
        var result = Step(Abc(), new ConditionOp(["C"]) { Add = ["Frightened ", "Hex"], Source = "A" });
        var names = result.Next.Named("C").Conditions.Select(c => c.Name).ToList();
        Assert.Equal(["frightened", "Hex"], names);
        Assert.Contains(result.Notes, n => n == "Hex is not an SRD condition: tracked as an effect.");
        var row = result.Changes.Single(c => c.Kind == L.Condition);
        Assert.Equal(result.Next.Named("A").Id, row.ActorId);
    }

    [Fact]
    public void Add_DuplicateFromTheSameSource_IsRefused_AnotherSourceIsNot()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["frightened"], Source = "A" }).Next;
        var ex = Assert.Throws<DndInputException>(() => Step(s, new ConditionOp(["C"]) { Add = ["frightened"], Source = "A" }));
        Assert.Contains("C already has frightened from A", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, Step(s, new ConditionOp(["C"]) { Add = ["frightened"], Source = "B" }).Next.Named("C").Conditions.Count);
    }

    [Fact]
    public void Add_ConditionImmunity_NoEffectForThatTargetOnly()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "zombie"), hp: HpChoice.Avg), new AddEntry { Name = "Hero", Hp = HpChoice.Of(20) }).Next;
        var result = Step(s, new ConditionOp(["zombie", "hero"]) { Add = ["poisoned"] });
        Assert.False(result.Next.Named("Zombie").Has("poisoned"));
        Assert.True(result.Next.Named("Hero").Has("poisoned"));
        Assert.Contains("no effect: Zombie is immune to poisoned", result.Notes);
    }

    [Fact]
    public void Add_TheSheetsConditionImmunity_NoEffectForASheetSeededCombatant()
    {
        var sheet = Sheet("e-ward", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 40, "defenses": { "condition_immune": ["poisoned"] } }""", E2024);
        var s = Add(Encounter(), Pc("e-ward", "Ward", "character:ward", sheet)).Next;
        var result = Step(s, new ConditionOp(["ward"]) { Add = ["poisoned", "frightened"], Source = "a cursed idol" });
        Assert.Equal(["frightened"], result.Next.Named("Ward").Conditions.Select(c => c.Name));
        Assert.Contains("no effect: Ward is immune to poisoned", result.Notes);
    }

    [Fact]
    public void Add_ExhaustionToAMonsterImmuneToIt_NoEffect_TheLevelStays()
    {
        // The 2014 mummy is immune to exhaustion.
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy"), hp: HpChoice.Avg)).Next;
        var result = Step(s, new ConditionOp(["mummy"]) { Add = ["exhaustion"], Level = 2 });
        Assert.Equal(0, result.Next.Named("Mummy").Exhaustion);
        Assert.Contains("no effect: Mummy is immune to exhaustion", result.Notes);
        Assert.DoesNotContain(result.Changes, c => c.Kind == L.Condition);
    }

    [Fact]
    public void Add_Hidden_IsRefusedPointingAtSet()
    {
        var ex = Assert.Throws<DndInputException>(() => Step(Abc(), new ConditionOp(["C"]) { Add = ["Hidden"] }));
        Assert.Contains("set {\"hidden\": true}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_Unconscious_BringsProne_AndDefeatsAnEnemy()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["unconscious"] }).Next;
        Assert.True(s.Named("C").Has("prone"));
        Assert.True(s.Named("C").Defeated);
    }

    [Fact]
    public void Add_Incapacitating_BreaksTheBearersConcentration()
    {
        var s = Step(Abc(), new ConcentrationOp(["B"]) { Spell = "Bless" }).Next;
        var result = Step(s, new ConditionOp(["B"]) { Add = ["stunned"] });
        Assert.Null(result.Next.Named("B").Concentration);
        Assert.True(result.Says(K.ConcentrationBroken, "Bless", "stunned"));
        var poisoned = Step(s, new ConditionOp(["B"]) { Add = ["poisoned"] });
        Assert.NotNull(poisoned.Next.Named("B").Concentration);
    }

    [Fact]
    public void Add_RageWhileConcentrating_RemindsToDropIt_NeverEndsItItself()
    {
        var s = Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Bless" }).Next;
        var raging = Step(s, new ConditionOp(["A"]) { Add = ["Rage"] });
        Assert.NotNull(raging.Next.Named("A").Concentration);
        var reminder = Assert.Single(raging.Of(K.ConcentrationBroken));
        Assert.Equal("combat {\"action\": \"concentration\", \"targets\": [\"a\"], \"drop\": true}", reminder.Call);
        Assert.Empty(Step(Abc(), new ConditionOp(["A"]) { Add = ["Rage"] }).Of(K.ConcentrationBroken));
    }

    [Fact]
    public void Add_ExhaustionByLevels_ACall_CarriesTheEffectsLine_SixKills()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["exhaustion"], Level = 2 });
        Assert.Equal(2, s.Next.Named("C").Exhaustion);
        Assert.Empty(s.Next.Named("C").Conditions);
        Assert.True(s.Says(K.ExhaustionEffects, "C", "Exhaustion 2: D20 Tests −4; Speed −10 ft"));
        var removed = Step(s.Next, new ConditionOp(["C"]) { Remove = ["exhaustion"] });
        Assert.Equal(1, removed.Next.Named("C").Exhaustion);
        var dead = Step(s.Next, new ConditionOp(["C"]) { Add = ["exhaustion"], Level = 4 });
        Assert.True(dead.Next.Named("C").Dead);
        Assert.True(dead.Says(K.Died, "exhaustion 6"));
    }

    [Fact]
    public void Add_2014ExhaustionFour_HalvesTheMaximum_HpDropsToIt()
    {
        var s = Step(Abc(E2014), new ConditionOp(["C"]) { Add = ["exhaustion"], Level = 4 });
        Assert.Equal(15, s.Next.Named("C").Hp);
        Assert.Equal(15, s.Next.Named("C").EffectiveMaxHp(E2014));
    }

    [Fact]
    public void Add_WithResource_SpendsOneUseOfEachTarget_RefusedWhenNoneLeft()
    {
        var sheet = Sheet("e-x", """{ "classes": [{ "class": "barbarian", "level": 3 }], "max_hp": 30, "resources": [{ "name": "Rage", "max": 1 }] }""", E2024);
        var s = Add(Encounter(), Pc("e-x", "X", "character:x", sheet)).Next;
        s = Step(s, new ConditionOp(["x"]) { Add = ["Rage"], Resource = "Rage" }).Next;
        Assert.Equal(1, s.Named("X").Resources["rage"].Used);
        s = Step(s, new ConditionOp(["x"]) { Remove = ["Rage"] }).Next;
        var ex = Assert.Throws<DndInputException>(() => Step(s, new ConditionOp(["x"]) { Add = ["Rage"], Resource = "Rage" }));
        Assert.Contains("only 0 of 1 left", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_SourceNamingNoCombatant_KeptAsItsNote()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["exhaustion"], Source = "water pressure below the fifth station" });
        Assert.Equal(1, s.Next.Named("C").Exhaustion);
        var curse = Step(Abc(), new ConditionOp(["C"]) { Add = ["cursed"], Source = "the old shrine", Duration = "until removed" }).Next.Named("C").Conditions.Single();
        Assert.Equal((null, "the old shrine"), (curse.Source, curse.SourceNote));
    }

    [Fact]
    public void Add_SourceDefaultsToTheTurnHolder()
    {
        var curse = Step(Abc().Next(), new ConditionOp(["C"]) { Add = ["charmed"] }).Next.Named("C").Conditions.Single();
        Assert.Equal(Abc().Next().Named("B").Id, curse.Source);
    }

    [Fact]
    public void Add_TimedWithDcAndAbility_AddsTheSave_SaveEndsHasIt()
    {
        var timed = Step(Abc(), new ConditionOp(["C"]) { Add = ["frightened"], Duration = "1 minute", Dc = 14, Ability = "Wisdom" }).Next.Named("C").Conditions.Single();
        Assert.Equal(new ConditionSave("wis", 14), timed.Save);
        var plain = Step(Abc(), new ConditionOp(["C"]) { Add = ["frightened"], Duration = "1 minute" }).Next.Named("C").Conditions.Single();
        Assert.Null(plain.Save);
    }

    [Fact]
    public void Add_EffectWithExceptButNoAll_IsRefused()
    {
        var ex = Assert.Throws<DndInputException>(() => Step(Abc(), new ConditionOp(["C"]) { Add = ["Ward"], Effect = new EffectInput(Resist: ["fire"], Except: ["cold"]) }));
        Assert.Contains("narrows an \"all\" entry", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_EffectAc_RaisesTheDisplayedAc()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["Shield of Faith"], Effect = new EffectInput(Ac: 2) }).Next;
        Assert.Equal((12, 14), (s.Named("C").Ac!.Value, s.Named("C").DisplayedAc!.Value));
    }

    [Fact]
    public void AddAndRemove_Together_IsRefused()
    {
        Assert.Throws<DndInputException>(() => Step(Abc(), new ConditionOp(["C"]) { Add = ["prone"], Remove = ["prone"] }));
        Assert.Throws<DndInputException>(() => Step(Abc(), new ConditionOp(["C"])));
    }

    [Fact]
    public void Remove_BySource_OnlyThatSourcesEntry_NothingToRemoveIsANote()
    {
        var s = Step(Abc(), new ConditionOp(["C"]) { Add = ["frightened"], Source = "A" }).Next;
        s = Step(s, new ConditionOp(["C"]) { Add = ["frightened"], Source = "B" }).Next;
        var removed = Step(s, new ConditionOp(["C"]) { Remove = ["frightened"], Source = "B" }).Next;
        Assert.Equal(s.Named("A").Id, removed.Named("C").Conditions.Single().Source);
        Assert.Contains("no effect: C has no blinded", Step(s, new ConditionOp(["C"]) { Remove = ["blinded"] }).Notes);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // concentration

    [Fact]
    public void Concentration_StartingAnother_EndsThePreviousAndWhatItHeld()
    {
        var s = Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Hold Person" }).Next;
        s = Step(s, new ConditionOp(["C"]) { Add = ["paralyzed"], Duration = "concentration" }).Next;
        var swapped = Step(s, new ConcentrationOp(["A"]) { Spell = "Bless", Duration = "1 minute" });
        Assert.Equal("Bless", swapped.Next.Named("A").Concentration!.Spell);
        Assert.False(swapped.Next.Named("C").Has("paralyzed"));
        Assert.True(swapped.Says(K.ConcentrationBroken, "Hold Person", "started concentrating on Bless"));
    }

    [Fact]
    public void Concentration_NoDuration_EndsWithTheFight_TheResultSaysGiveDuration()
    {
        var result = Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Bless" });
        Assert.Null(result.Next.Named("A").Concentration!.Expires);
        Assert.Contains(result.Notes, n => n.Contains("give duration", StringComparison.Ordinal));
    }

    [Fact]
    public void Concentration_ATimedDuration_EndsAtTheStartOfItsAnchorsTurn()
    {
        var s = Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Bless", Duration = "1 round" }).Next;
        s = Step(s, new ConditionOp(["C"]) { Add = ["Bless"], Duration = "concentration" }).Next;
        (s, _) = NextUntil(s, "C");
        Assert.NotNull(s.Named("A").Concentration);
        var wrap = Step(s, new NextOp());
        Assert.Null(wrap.Next.Named("A").Concentration);
        Assert.False(wrap.Next.Named("C").Has("Bless"));
    }

    [Fact]
    public void Concentration_NotADuration_IsRefused()
    {
        Assert.Throws<DndInputException>(() => Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Bless", Duration = "until the end of your next turn" }));
    }

    [Fact]
    public void Concentration_SaveResolvesTheOldestPendingFirst()
    {
        var s = Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Bless" }).Next;
        s = Step(s, new DamageOp(["A"]) { Amount = 4 }).Next; // DC 10
        s = Step(s, new DamageOp(["A"]) { Amount = 24, Source = "B" }).Next; // DC 12
        Assert.Equal([10, 12], s.Named("A").Concentration!.Pending);
        var first = Step(s, new ConcentrationOp(["A"]) { Total = 11 });
        Assert.Equal([12], first.Next.Named("A").Concentration!.Pending);
        var second = Step(first.Next, new ConcentrationOp(["A"]) { Total = 11 });
        Assert.Null(second.Next.Named("A").Concentration);
        Assert.True(second.Says(K.ConcentrationBroken, "failed the save (11 against DC 12)"));
    }

    [Fact]
    public void Concentration_NoSaveDue_IsRefused_NotConcentrating_IsRefused()
    {
        var s = Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Bless" }).Next;
        Assert.Throws<DndInputException>(() => Step(s, new ConcentrationOp(["A"]) { Total = 15 }));
        Assert.Throws<DndInputException>(() => Step(Abc(), new ConcentrationOp(["A"]) { Total = 15 }));
        Assert.Throws<DndInputException>(() => Step(Abc(), new ConcentrationOp(["A"]) { Drop = true }));
    }

    [Fact]
    public void Concentration_ServerSave_SheetConBonusAnd2014ExhaustionDisadvantage()
    {
        var a = Encounter(E2014);
        a = Add(a, Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", Belmakor())).Next;
        a = Step(a, new ConcentrationOp(["belmakor"]) { Spell = "Circle of Power" }).Next;
        a = Step(a, new DamageOp(["belmakor"]) { Amount = 10 }).Next;
        var need = Assert.Single(CombatTracker.Needs(a, new ConcentrationOp(["belmakor"])));
        Assert.Equal(("1d20+7", P.ConcentrationSave), (need.Expression, need.Purpose));
        var kept = Step(a, new ConcentrationOp(["belmakor"]), 3);
        Assert.NotNull(kept.Next.Named("Belmakor Silverwind").Concentration);
        Assert.Equal("concentration:e-belmakor", kept.Changes.Single().RollKey);

        var tired = Step(a, new ConditionOp(["belmakor"]) { Add = ["exhaustion"], Level = 3 }).Next;
        Assert.Equal("2d20kl1+7", Assert.Single(CombatTracker.Needs(tired, new ConcentrationOp(["belmakor"]))).Expression);
    }

    [Fact]
    public void Concentration_MonsterSave_UsesTheStatBlocksConSave()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "lich"), hp: HpChoice.Avg)).Next;
        s = Step(s, new ConcentrationOp(["lich"]) { Spell = "Dominate Monster" }).Next;
        s = Step(s, new DamageOp(["lich"]) { Amount = 10 }).Next;
        Assert.Equal("1d20+10", Assert.Single(CombatTracker.Needs(s, new ConcentrationOp(["lich"]))).Expression);
    }

    [Fact]
    public void Concentration_SlotLevel_SpendsTheSheetsSlot_RefusedWithoutSlots()
    {
        var a = Add(Encounter(E2014), Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", Belmakor())).Next;
        var started = Step(a, new ConcentrationOp(["belmakor"]) { Spell = "Circle of Power", SlotLevel = 5 }).Next;
        Assert.Equal(1, started.Named("Belmakor Silverwind").Resources["slot:5"].Used);
        Assert.Throws<DndInputException>(() => Step(Abc(), new ConcentrationOp(["A"]) { Spell = "Bless", SlotLevel = 1 }));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // use

    [Fact]
    public void Use_SheetSlotsPactAndResources_NegativeRestores_RefusedBeyond()
    {
        var a = Add(Encounter(E2014), Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", Belmakor())).Next;
        a = Step(a, new UseOp(["belmakor"]) { SlotLevel = 6 }).Next;
        Assert.Equal(1, a.Named("Belmakor Silverwind").Resources["slot:6"].Used);
        Assert.Throws<DndInputException>(() => Step(a, new UseOp(["belmakor"]) { SlotLevel = 6 }));
        a = Step(a, new UseOp(["belmakor"]) { SlotLevel = 6, Amount = -1 }).Next;
        Assert.Equal(0, a.Named("Belmakor Silverwind").Resources["slot:6"].Used);
        Assert.Throws<DndInputException>(() => Step(a, new UseOp(["belmakor"]) { SlotLevel = 6, Amount = -1 }));
        a = Step(a, new UseOp(["belmakor"]) { Resource = "blade" }).Next;
        Assert.Equal(1, a.Named("Belmakor Silverwind").Resources["bladesong"].Used);
        var tracker = Assert.Throws<DndInputException>(() => Step(a, new UseOp(["belmakor"]) { Resource = "Contingency" }));
        Assert.Contains("tracker", tracker.Message, StringComparison.Ordinal);
        Assert.Throws<DndInputException>(() => Step(a, new UseOp(["belmakor"]) { Pact = true }));
    }

    [Fact]
    public void Use_MonsterPerDayAndPool()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "lich"), hp: HpChoice.Avg)).Next;
        var lich = s.Named("Lich");
        Assert.Contains("pool:slot:9", lich.Resources.Keys);
        s = Step(s, new UseOp(["lich"]) { SlotLevel = 9 }).Next;
        Assert.Equal(1, s.Named("Lich").Resources["pool:slot:9"].Used);
        Assert.Throws<DndInputException>(() => Step(s, new UseOp(["lich"]) { SlotLevel = 9 }));
    }

    [Fact]
    public void Use_ExactlyOneThing_AndOneTarget()
    {
        var s = Abc();
        Assert.Throws<DndInputException>(() => Step(s, new UseOp(["A"])));
        Assert.Throws<DndInputException>(() => Step(s, new UseOp(["A"]) { SlotLevel = 1, Pact = true }));
        var ex = Assert.Throws<DndInputException>(() => Step(s, new UseOp(["A", "B"]) { SlotLevel = 1 }));
        Assert.Contains("exactly one target", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Use_ItemFromItsOwnInventory_SheetSeededOnly_LeftAfterTheFightsUses()
    {
        var s = Add(Encounter(), [.. PartyB()]).Next;
        s = Step(s, new UseOp(["bjorn-mountainfell"]) { Item = "potion", Holdings = HoldingsB() }).Next;
        Assert.Equal(1, s.Named("Björn Mountainfell").Resources["item:h-potion"].Used);
        s = Step(s, new UseOp(["bjorn-mountainfell"]) { Item = "Potion of Healing", Holdings = HoldingsB() }).Next;
        var ex = Assert.Throws<DndInputException>(() => Step(s, new UseOp(["bjorn-mountainfell"]) { Item = "Potion of Healing", Holdings = HoldingsB() }));
        Assert.Contains("has 0 Potion of Healing left", ex.Message, StringComparison.Ordinal);
        var other = Assert.Throws<DndInputException>(() => Step(s, new UseOp(["fishman-monk"]) { Item = "Potion of Healing", Holdings = HoldingsB() }));
        Assert.Contains("not in The fishman monk's inventory", other.Message, StringComparison.Ordinal);
        var row = Step(Add(Encounter(), [.. PartyB()]).Next, new UseOp(["bjorn-mountainfell"]) { Item = "potion", Holdings = HoldingsB() }).Changes.Single();
        Assert.Equal("item:h-potion", JsonNode.Parse(row.Detail!)!["key"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(int.MinValue, "slot")]
    [InlineData(int.MinValue, "resource")]
    [InlineData(int.MinValue, "item")]
    [InlineData(int.MaxValue, "slot")]
    [InlineData(-21, "resource")]
    [InlineData(0, "item")]
    public void Use_AnAmountOutOfRange_IsRefusedWithTheRange_NeverAnOverflow(int amount, string what)
    {
        // C01: int.MinValue has no positive counterpart; |amount| is compared as a long, as campaign_character's use does.
        var s = Add(Encounter(E2014), Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", Belmakor()), PartyB()[0]).Next;
        UseOp op = what switch
        {
            "slot" => new UseOp(["belmakor"]) { SlotLevel = 1, Amount = amount },
            "resource" => new UseOp(["belmakor"]) { Resource = "Bladesong", Amount = amount },
            _ => new UseOp(["bjorn-mountainfell"]) { Item = "Potion of Healing", Holdings = HoldingsB(), Amount = amount },
        };

        var ex = Assert.Throws<DndInputException>(() => Step(s, op));

        Assert.Equal($"amount is {amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}; give 1 to 20 uses spent, or a negative number to restore.", ex.Message);
    }

    [Theory]
    [InlineData(5, "Belmakor Silverwind: 5th-level slot 1 used, 1/2 left.")]
    [InlineData(1, "Belmakor Silverwind: 1st-level slot 1 used, 2/4 left.")]
    [InlineData(2, "Belmakor Silverwind: 2nd-level slot 1 used, 2/3 left.")]
    [InlineData(3, "Belmakor Silverwind: 3rd-level slot 1 used, 2/3 left.")]
    public void Use_ASheetSlot_TheLineNamesItsLevelAsAnOrdinal_NeverTheKey(int level, string line)
    {
        // U13: "slot:3" is the resources key; the author reads "3rd-level slot".
        var s = Add(Encounter(E2014), Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", Belmakor())).Next;

        var result = Step(s, new UseOp(["belmakor"]) { SlotLevel = level });

        Assert.Equal([line], result.Notes);
    }

    [Fact]
    public void Use_AMonsterPoolAndPact_TheLinesNameTheSlotsAsTheAuthorReadsThem_TheRefusalListsOrdinals()
    {
        var lich = Add(Encounter(E2014), Monster(Block(E2014, "lich"), hp: HpChoice.Avg)).Next;
        Assert.Equal(["Lich: 9th-level slot 1 used, 0/1 left."], Step(lich, new UseOp(["lich"]) { SlotLevel = 9 }).Notes);
        var warlock = Sheet("e-hex", """{ "classes": [{ "class": "warlock", "level": 5 }], "max_hp": 38, "pact": { "level": 3, "max": 2 } }""", E2024);
        var w = Add(Encounter(), Pc("e-hex", "Hex", "character:hex", warlock)).Next;
        Assert.Equal(["Hex: 3rd-level Pact Magic slot 1 used, 1/2 left."], Step(w, new UseOp(["hex"]) { Pact = true }).Notes);

        var none = Assert.Throws<DndInputException>(() => Step(w, new UseOp(["hex"]) { SlotLevel = 4 })).Message;
        Assert.DoesNotContain("slot:", none, StringComparison.Ordinal);
        var some = Assert.Throws<DndInputException>(() => Step(lich, new UseOp(["lich"]) { SlotLevel = 9, Amount = -1 })).Message;
        Assert.Equal("Lich's 9th-level slot: only 0 used; cannot restore 1.", some);
        var belmakor = Add(Encounter(E2014), Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", Belmakor())).Next;
        var missing = Assert.Throws<DndInputException>(() => Step(belmakor, new UseOp(["belmakor"]) { SlotLevel = 8 })).Message;
        Assert.Equal(
            "Belmakor Silverwind has no 8th-level slots tracked (its slots: 1st-level, 2nd-level, 3rd-level, 4th-level, 5th-level, 6th-level); give the sheet its slots, or leave slot_level out.",
            missing);
    }

    [Theory]
    [InlineData("!!!", 1)]
    [InlineData("—", 1)]
    [InlineData("…", 2)]
    [InlineData("???", 2)]
    public void Add_AnEffectNameWithNoLetterOrDigit_IsRefused_ItWouldShareItsKeyWithEveryOtherSuchName(string name, int item)
    {
        // C02: every name with no letter or digit has the empty key, so "!!!" and "???" would be one effect to remove.
        string[] add = item == 1 ? [name] : ["poisoned", name];

        var ex = Assert.Throws<DndInputException>(() => Step(Abc(), new ConditionOp(["C"]) { Add = add }));

        Assert.Equal($"add item {item} \"{name}\" has no letter or digit; give add item {item} a name with a letter or digit.", ex.Message);
    }

    [Fact]
    public void Remove_AnEffectNameWithNoLetterOrDigit_RemovesOnlyTheEffectSpelledSo()
    {
        // Such an effect can still come in from a sheet (the sheet keeps names as typed); removing it touches no other.
        var sheet = Sheet("e-odd", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 40 }""", E2024);
        sheet = sheet with { Conditions = [new SheetCondition("!!!", null, "until_removed", null, null), new SheetCondition("???", null, "until_removed", null, null)] };
        var s = Add(Encounter(), Pc("e-odd", "Odd", "character:odd", sheet)).Next;

        var removed = Step(s, new ConditionOp(["odd"]) { Remove = ["???"] }).Next;

        Assert.Equal(["!!!"], removed.Named("Odd").Conditions.Select(c => c.Name));
    }

    [Theory]
    [InlineData("concentration")]
    [InlineData("Concentration")]
    public void Add_Concentration_IsRefusedPointingAtTheConcentrationAction(string name)
    {
        // C06: an effect named "concentration" persists to the sheet, where remove ["concentration"] means the column.
        var ex = Assert.Throws<DndInputException>(() => Step(Abc(), new ConditionOp(["C"]) { Add = [name] }));

        Assert.Equal(
            "concentration is not a condition: start one with combat {\"action\": \"concentration\", \"targets\": [\"c\"], \"spell\": …} " +
            "and end it with combat {\"action\": \"concentration\", \"targets\": [\"c\"], \"drop\": true}.",
            ex.Message);
    }

    [Theory]
    [InlineData("end of round 2", 5, "duration \"end of round 2\" names round 2, but round is 5; give the round once.")]
    [InlineData("end_of_round", 2147483647, "round is 2147483647; it is 1 to 10000.")]
    [InlineData("end of round 10001", null, "round is 10001; it is 1 to 10000.")]
    [InlineData("end_of_round", 0, "round is 0; it is 1 to 10000.")]
    public void Add_EndOfRound_ARoundThatDisagreesOrIsOutOfRange_IsRefused(string duration, int? round, string message)
    {
        // C16: the phrase's round and round must say the same; the round is capped as rounds_left is.
        var ex = Assert.Throws<DndInputException>(() => Step(Abc(), new ConditionOp(["C"]) { Add = ["deafened"], Duration = duration, Round = round }));

        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("end of round 2", null, 2)]
    [InlineData("end of round 2", 2, 2)]
    [InlineData("end_of_round", 10000, 10000)]
    public void Add_EndOfRound_ThePhraseAndRoundAgree_IsAccepted(string duration, int? round, int expires)
    {
        var condition = Step(Abc(), new ConditionOp(["C"]) { Add = ["deafened"], Duration = duration, Round = round }).Next.Named("C").Conditions.Single();

        Assert.Equal(expires, condition.Expires!.Round);
    }

    [Fact]
    public void Heal_Item_FromTheSourcesInventoryElseTheTargets_OneTargetOnly()
    {
        var s = Add(Encounter(), [.. PartyB()]).Next;
        var own = Step(s, new HealOp(["bjorn-mountainfell"]) { Amount = 7, Item = "Potion of Healing", Holdings = HoldingsB() }).Next;
        Assert.Equal(1, own.Named("Björn Mountainfell").Resources["item:h-potion"].Used);
        Assert.Throws<DndInputException>(() => Step(s, new HealOp(["bjorn-mountainfell", "fishman-monk"]) { Amount = 7, Item = "Potion of Healing", Holdings = HoldingsB() }));
        Assert.Throws<DndInputException>(() => Step(s, new HealOp(["fishman-monk"]) { Amount = 7, Item = "Potion of Healing", Holdings = HoldingsB() }));
    }
}
