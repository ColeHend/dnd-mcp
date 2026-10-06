using System.Text.Json.Nodes;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using D = DndMcp.Domain.Combat.CombatValues.Durations;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: damage and healing go through the shared rules (contract §5.1-§5.10) with the combatant's own adjustments
/// (stat block or sheet defences, active effects, Petrified), and every outcome becomes state and reminders: a drop
/// (unconscious and prone for a death-save maker, death for a monster), a knock-out per edition, massive damage, failures
/// at 0, death interceptors that hold a monster defeated but alive, Bloodied at each 2024 crossing, a concentration save
/// owed or broken, the grapples a dropped grappler held. Unknown hit points accumulate <c>damage_taken</c> (D17). Dice are
/// rolled once for every target; a critical doubles a server roll's dice, never a given amount.
/// </summary>
public sealed class CombatHitPointTests
{
    /// <summary>P (party, 30 HP, makes death saves) and M (an enemy), P to act.</summary>
    private static EncounterState PartyAndFoe(string edition = E2024) => Fight(edition, ["P"], ("P", 20), ("M", 10));

    // ------------------------------------------------------------------------------------------------------------------
    // Rolls

    [Fact]
    public void Dice_RolledOnceForEveryTarget_SubjectTheSource()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        var needs = CombatTracker.Needs(s, new DamageOp(["B", "C"]) { Dice = "8d6", DamageType = "fire", Source = "A" });
        Assert.Equal([new RollNeed("damage", s.Named("A").Id, P.Damage, "8d6")], needs);
        var result = Step(s, new DamageOp(["B", "C"]) { Dice = "8d6", DamageType = "fire", Source = "A" }, 1, 2, 3, 4, 5, 6, 1, 2);
        Assert.Equal(6, result.Next.Named("B").Hp);
        Assert.Equal(6, result.Next.Named("C").Hp);
        Assert.All(result.Changes.Where(c => c.Kind == L.Damage), c => Assert.Equal("damage", c.RollKey));
    }

    [Theory]
    [InlineData(new[] { "B" }, null, "A")]
    [InlineData(new[] { "A" }, null, "A")]
    [InlineData(new[] { "B", "C" }, null, "A")]
    [InlineData(new[] { "B", "C" }, "C", "C")]
    public void Dice_DamageSubject_SourceElseTheTurnHolderWhileATurnRuns(string[] targets, string? source, string subject)
    {
        // L01 (§6.10 amended): the damage roll is the actor's (§6.4), who rolls it: a hidden or enemy attacker's no-source
        // roll on its own turn is its own (secret), never the party target's (open), and the reverse for a PC's swing.
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        var need = Assert.Single(CombatTracker.Needs(s, new DamageOp(targets) { Dice = "1d6", Source = source }));
        Assert.Equal(s.Named(subject).Id, need.CombatantId);
        var onB = s.Next();
        Assert.Equal(onB.Named("B").Id, Assert.Single(CombatTracker.Needs(onB, new DamageOp(["A"]) { Dice = "1d6" })).CombatantId);
    }

    [Theory]
    [InlineData(new[] { "B" }, null, "B")]
    [InlineData(new[] { "B", "C" }, null, null)]
    [InlineData(new[] { "B", "C" }, "A", "A")]
    public void Dice_DamageSubject_BeforeInitiative_SourceElseTheSingleTargetElseNone(string[] targets, string? source, string? subject)
    {
        var s = Add(Encounter(), [.. new[] { "A", "B", "C" }.Select(n => new AddEntry { Name = n, Hp = HpChoice.Of(30) })]).Next;
        var need = Assert.Single(CombatTracker.Needs(s, new DamageOp(targets) { Dice = "1d6", Source = source }));
        Assert.Equal(subject is null ? null : s.Named(subject).Id, need.CombatantId);
    }

    [Theory]
    [InlineData(new[] { "B" }, null, false, "B")]
    [InlineData(new[] { "B" }, null, true, "B")]
    [InlineData(new[] { "B", "C" }, null, false, null)]
    [InlineData(new[] { "B" }, "C", false, "C")]
    public void Dice_HealSubject_SourceElseTheSingleTarget_NeverTheTurnHolder(string[] targets, string? source, bool temp, string? subject)
    {
        // A heal is usually the healed creature's side: fixture B's B16 heals the Aboleth on the monk's turn with no
        // source and stays secret as "Aboleth: heal" (§16), so the turn-holder default is for damage only.
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        var need = Assert.Single(CombatTracker.Needs(s, new HealOp(targets) { Dice = "1d6", Source = source, Temp = temp }));
        Assert.Equal(subject is null ? null : s.Named(subject).Id, need.CombatantId);
    }

    [Fact]
    public void Critical_DoublesAServerRollsDice_NotAGivenAmount()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        var need = Assert.Single(CombatTracker.Needs(s, new DamageOp(["B"]) { Dice = "2d6+5", Critical = true }));
        Assert.Equal(("4d6+5", P.DamageCritical), (need.Expression, need.Purpose));
        Assert.Equal(20, Step(s, new DamageOp(["B"]) { Amount = 10, Critical = true }).Next.Named("B").Hp);
    }

    [Fact]
    public void Parts_EachDicePartRolledUnderItsOwnKey()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        var op = new DamageOp(["B"]) { Parts = [new(null, "1d6", "slashing"), new(null, "2d4", "fire"), new(3, null, "cold")] };
        var needs = CombatTracker.Needs(s, op);
        Assert.Equal(["damage:1", "damage:2"], needs.Select(n => n.Key));
        Assert.Equal(30 - (4 + 2 + 3 + 3), Step(s, op, 4, 2, 3).Next.Named("B").Hp);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(5, "1d6")]
    public void Amounts_NotExactlyOne_IsRefused(int? amount, string? dice)
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        var ex = Assert.Throws<DndInputException>(() => Step(s, new DamageOp(["B"]) { Amount = amount, Dice = dice }));
        Assert.Contains("exactly one of amount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DamageType_Unknown_IsRefusedListingTheTypes()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        var ex = Assert.Throws<DndInputException>(() => Step(s, new DamageOp(["B"]) { Amount = 3, DamageType = "sonic" }));
        Assert.Contains("thunder", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Adjustments

    [Fact]
    public void Half_OnlyTheNamedTargetsHalve()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        s = Step(s, new DamageOp(["B", "C"]) { Amount = 9, DamageType = "fire", Half = ["C"] }).Next;
        Assert.Equal((21, 26), (s.Named("B").Hp!.Value, s.Named("C").Hp!.Value));
    }

    [Fact]
    public void Half_ATargetNotAmongTheTargets_IsRefused()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        Assert.Throws<DndInputException>(() => Step(s, new DamageOp(["B"]) { Amount = 9, Half = ["C"] }));
    }

    [Fact]
    public void StatBlockQualifiers_NonmagicalResisted_MagicalNot_RawSkipsEverything()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "mummy"), hp: HpChoice.Avg)).Next;
        Assert.Equal(58 - 4, Step(s, new DamageOp(["mummy"]) { Amount = 8, DamageType = "slashing" }).Next.Named("Mummy").Hp);
        Assert.Equal(58 - 8, Step(s, new DamageOp(["mummy"]) { Amount = 8, DamageType = "slashing", Magical = true }).Next.Named("Mummy").Hp);
        Assert.Equal(58 - 8, Step(s, new DamageOp(["mummy"]) { Amount = 8, DamageType = "fire", Raw = true }).Next.Named("Mummy").Hp);
        Assert.Equal(58 - 16, Step(s, new DamageOp(["mummy"]) { Amount = 8, DamageType = "fire" }).Next.Named("Mummy").Hp);
    }

    [Fact]
    public void Effect_ResistAllExceptPsychic_HalvesBludgeoningNotPsychic()
    {
        var s = Fight(E2024, ["P"], ("P", 20), ("M", 10));
        s = Step(s, new ConditionOp(["P"]) { Add = ["Rage"], Effect = new EffectInput(Resist: ["all"], Except: ["psychic"]) }).Next;
        s = Step(s, new DamageOp(["P"]) { Amount = 12, DamageType = "bludgeoning" }).Next;
        Assert.Equal(24, s.Named("P").Hp);
        s = Step(s, new DamageOp(["P"]) { Amount = 10, DamageType = "psychic" }).Next;
        Assert.Equal(14, s.Named("P").Hp);
    }

    [Fact]
    public void Petrified_ResistsAllDamage_And2024ImmuneToPoisoned()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        s = Step(s, new ConditionOp(["B"]) { Add = ["petrified"] }).Next;
        Assert.Equal(25, Step(s, new DamageOp(["B"]) { Amount = 10 }).Next.Named("B").Hp);
        var poisoned = Step(s, new ConditionOp(["B"]) { Add = ["poisoned"] });
        Assert.False(poisoned.Next.Named("B").Has("poisoned"));
        Assert.Contains(poisoned.Notes, n => n.Contains("no effect: B is immune to poisoned", StringComparison.Ordinal));
    }

    [Fact]
    public void SheetDefenses_ApplyToASheetSeededCombatant()
    {
        var sheet = Sheet("e-ward", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 40, "defenses": { "resist": ["fire"] } }""", E2024);
        var s = Add(Encounter(), Pc("e-ward", "Ward", "character:ward", sheet)).Next;
        Assert.Equal(35, Step(s, new DamageOp(["ward"]) { Amount = 10, DamageType = "fire" }).Next.Named("Ward").Hp);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Drops, death, knock-outs

    [Fact]
    public void Monster_DropsToZero_Dies_Defeated_DefeatRow()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        var result = Step(s, new DamageOp(["B"]) { Amount = 30 });
        var b = result.Next.Named("B");
        Assert.True(b.Dead);
        Assert.True(b.Defeated);
        Assert.Contains(result.Changes, c => c.Kind == L.Defeat);
        Assert.True(result.Says(K.Died, "B dies"));
    }

    [Fact]
    public void Death_OfAnEnemy_IsOneLine_TheDefeatRowStillWritten_APartyMemberOnlyDies()
    {
        // U13: "B dies (…)" and "B is defeated (dead)" were two lines for one event.
        var result = Step(Fight(E2024, ("A", 20), ("B", 15)), new DamageOp(["B"]) { Amount = 30 });
        var b = result.Next.Named("B");
        Assert.Equal(["B dies (dropped to 0 hit points) and is defeated."], result.Reminders.Where(r => r.CombatantId == b.Id).Select(r => r.Text));
        Assert.Equal(K.Died, result.Reminders.Single(r => r.CombatantId == b.Id).Kind);
        Assert.Contains(result.Changes, c => c.Kind == L.Defeat && c.TargetId == b.Id);

        var pc = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 60 });
        Assert.Equal(["P dies (massive damage: the damage left over was at least its hit point maximum)."], pc.Reminders.Where(r => r.Kind is K.Died or K.Defeated).Select(r => r.Text));

        // Already defeated (held at 0 by Undead Fortitude), then killed: it only dies now.
        var zombie = Step(Add(Encounter(E2014), Monster(Block(E2014, "zombie"), hp: HpChoice.Avg)).Next, new DamageOp(["zombie"]) { Amount = 30, DamageType = "slashing" }).Next;
        var killed = Step(zombie, new DamageOp(["zombie"]) { Amount = 1, Raw = true });
        Assert.Equal(["Zombie dies (damage at 0 hit points)."], killed.Reminders.Where(r => r.Kind is K.Died or K.Defeated).Select(r => r.Text));
    }

    [Theory]
    [InlineData(E2014, "P drops to 0 HP: unconscious, dying (0 successes, 0 failures). Death saving throws at the start of each of its turns: " +
        "10 or more succeeds (with Disadvantage from exhaustion 3); 3 successes, stable; 3 failures, dead; a 20 regains 1 hit point; a 1 counts as two failures; " +
        "damage at 0 HP is a failure, a critical hit two (and damage of at least its hit point maximum kills).")]
    [InlineData(E2024, "P drops to 0 HP: unconscious, dying (0 successes, 0 failures). Death Saving Throws at the start of each of its turns: " +
        "10 or more succeeds (−2 per Exhaustion level); 3 successes, Stable; 3 failures, dead; a 20 regains 1 Hit Point; a 1 counts as two failures; " +
        "damage at 0 Hit Points is a failure, a Critical Hit two (and damage of at least its Hit Point maximum kills).")]
    public void PartyMember_Drops_TheReminderStatesTheDeathSaveProcedureTheTrackerApplies_PerEdition(string edition, string text)
    {
        // U05: the 2024 procedure is in a chapter this server's data lacks; the tracker applies it, so it says it.
        var dropped = Step(PartyAndFoe(edition), new DamageOp(["P"]) { Amount = 35 });
        Assert.Equal(text, Assert.Single(dropped.Of(K.Dropped)).Text);

        var set = Step(PartyAndFoe(edition), new SetOp([new SetEntry("P") { Hp = 0 }]));
        Assert.Equal(text.Replace("drops to 0 HP: unconscious, dying (0 successes, 0 failures)", "is at 0 HP: unconscious, dying", StringComparison.Ordinal), Assert.Single(set.Of(K.Dropped)).Text);
    }

    [Fact]
    public void PartyMember_DropsToZero_UnconsciousProneDying_NotDefeated()
    {
        var s = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 35 }).Next;
        var p = s.Named("P");
        Assert.Equal(0, p.Hp);
        Assert.True(p.Dying);
        Assert.False(p.Defeated);
        Assert.True(p.Has("unconscious"));
        Assert.True(p.Has("prone"));
    }

    [Fact]
    public void MassiveDamage_LeftoverAtLeastTheMaximum_Dies()
    {
        var result = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 60 });
        Assert.True(result.Next.Named("P").Dead);
        Assert.Equal(new DeathSaveTally(0, 3, false), result.Next.Named("P").DeathSaves);
        Assert.True(result.Says(K.Died, "massive damage"));
    }

    [Fact]
    public void DamageAtZero_OneFailure_CriticalTwo_AtLeastTheMaximumKills()
    {
        var down = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 30 }).Next;
        Assert.Equal(new DeathSaveTally(0, 1, false), Step(down, new DamageOp(["P"]) { Amount = 3 }).Next.Named("P").DeathSaves);
        Assert.Equal(new DeathSaveTally(0, 2, false), Step(down, new DamageOp(["P"]) { Amount = 3, Critical = true }).Next.Named("P").DeathSaves);
        Assert.True(Step(down, new DamageOp(["P"]) { Amount = 30 }).Next.Named("P").Dead);
        var third = Step(Step(down, new DamageOp(["P"]) { Amount = 3, Critical = true }).Next, new DamageOp(["P"]) { Amount = 1 });
        Assert.True(third.Next.Named("P").Dead);
    }

    [Theory]
    [InlineData(E2014, 0, true)]
    [InlineData(E2024, 1, false)]
    public void KnockOut_PerEdition(string edition, int hp, bool stable)
    {
        var s = Fight(edition, ("A", 20), ("B", 15));
        var result = Step(s, new DamageOp(["B"]) { Amount = 40, KnockOut = true });
        var b = result.Next.Named("B");
        Assert.Equal(hp, b.Hp);
        Assert.False(b.Dead);
        Assert.True(b.Defeated);
        Assert.True(b.Has("unconscious"));
        Assert.Equal(stable, b.DeathSaves.Stable);
        Assert.Equal(edition == E2024, b.KnockedOut);
        Assert.True(result.Says(K.Dropped, "knocked out"));
    }

    [Fact]
    public void KnockOut2024_EndsOnAnyHealingOrFirstAid_ProneStays()
    {
        var s = Fight(E2024, ["B"], ("A", 20), ("B", 15));
        s = Step(s, new DamageOp(["B"]) { Amount = 40, KnockOut = true }).Next;
        var healed = Step(s, new HealOp(["B"]) { Amount = 1 }).Next.Named("B");
        Assert.False(healed.Has("unconscious"));
        Assert.True(healed.Has("prone"));
        var aided = Step(s, new DeathSaveOp(["B"]) { Stable = true });
        Assert.False(aided.Next.Named("B").Has("unconscious"));
        Assert.True(aided.Says(K.Revived, "first aid"));
    }

    [Fact]
    public void Heal_FromZero_WakesResetsTalliesAndUndefeats_DeadRefused()
    {
        var s = Fight(E2024, ("A", 20), ("Foe", 15));
        s = Step(s, new SetOp([new SetEntry("Foe") { DeathSaves = true }])).Next;
        s = Step(s, new DamageOp(["Foe"]) { Amount = 31 }).Next;
        Assert.True(s.Named("Foe").Defeated);
        var healed = Step(s, new HealOp(["Foe"]) { Amount = 5 });
        Assert.Equal(5, healed.Next.Named("Foe").Hp);
        Assert.False(healed.Next.Named("Foe").Defeated);
        Assert.True(healed.Says(K.Revived, "Foe"));
        var dead = Step(Fight(E2024, ("A", 20), ("B", 15)), new DamageOp(["B"]) { Amount = 99 }).Next;
        Assert.Contains(Step(dead, new HealOp(["B"]) { Amount = 5 }).Notes, n => n.Contains("no effect: B is dead", StringComparison.Ordinal));
    }

    [Fact]
    public void TempHp_KeepsTheHigher_AbsorbsFirst_RefusedOnTheDead()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        s = Step(s, new HealOp(["B"]) { Amount = 8, Temp = true }).Next;
        s = Step(s, new HealOp(["B"]) { Amount = 5, Temp = true }).Next;
        Assert.Equal(8, s.Named("B").TempHp);
        s = Step(s, new DamageOp(["B"]) { Amount = 10 }).Next;
        Assert.Equal((0, 28), (s.Named("B").TempHp, s.Named("B").Hp!.Value));
        var temp = Step(Fight(E2024, ("A", 20), ("B", 15)), new HealOp(["B"]) { Amount = 8, Temp = true });
        Assert.Equal(L.TempHp, temp.Changes.Single().Kind);
    }

    [Fact]
    public void DeadOrLeftTarget_NoEffectForThatTarget_NeverTheWholeCall()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        s = Step(s, new DamageOp(["B"]) { Amount = 99 }).Next;
        s = Step(s, new LeaveOp(["A"])).Next;
        var result = Step(s, new DamageOp(["A", "B", "C"]) { Amount = 5 });
        Assert.Equal(25, result.Next.Named("C").Hp);
        Assert.Contains("no effect: A left", result.Notes);
        Assert.Contains("no effect: B is dead", result.Notes);
        Assert.Single(result.Changes, c => c.Kind == L.Damage);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Death interceptors (D16)

    [Fact]
    public void UndeadFortitude_DropsDefeatedNotDead_ReminderWithDcAndBothCalls()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "zombie"), hp: HpChoice.Avg)).Next;
        var result = Step(s, new DamageOp(["zombie"]) { Amount = 30, DamageType = "slashing" });
        var zombie = result.Next.Named("Zombie");
        Assert.Equal(0, zombie.Hp);
        Assert.False(zombie.Dead);
        Assert.True(zombie.Defeated);
        var reminder = Assert.Single(result.Of(K.DeathInterceptor));
        Assert.Contains("Undead Fortitude", reminder.Text, StringComparison.Ordinal);
        Assert.Contains("DC 35", reminder.Text, StringComparison.Ordinal);
        Assert.Equal("combat {\"action\": \"set\", \"combatants\": [{\"name\": \"Zombie\", \"hp\": 1}]}", reminder.Call);
        Assert.Contains("combat {\"action\": \"damage\", \"targets\": [\"zombie\"], \"amount\": 1, \"raw\": true}", reminder.Text, StringComparison.Ordinal);

        // C13: the text says what each call is for, and the call that resolves it (it holds) follows it once.
        Assert.DoesNotContain(reminder.Call!, reminder.Text, StringComparison.Ordinal);
        Assert.EndsWith("; if it holds:", reminder.Text, StringComparison.Ordinal);

        var holds = Step(result.Next, new SetOp([new SetEntry("Zombie") { Hp = 1 }])).Next.Named("Zombie");
        Assert.Equal(1, holds.Hp);
        Assert.False(holds.Defeated);
        Assert.True(Step(result.Next, new DamageOp(["zombie"]) { Amount = 1, Raw = true }).Next.Named("Zombie").Dead);
    }

    [Fact]
    public void UndeadFortitude_RadiantOrCritical_DiesOutright()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "zombie"), hp: HpChoice.Avg)).Next;
        Assert.True(Step(s, new DamageOp(["zombie"]) { Amount = 30, DamageType = "radiant" }).Next.Named("Zombie").Dead);
        Assert.True(Step(s, new DamageOp(["zombie"]) { Amount = 30, Critical = true }).Next.Named("Zombie").Dead);
    }

    [Fact]
    public void Relentless_HeldOnce_SetToOneMarksItUsed_TheNextDropKills()
    {
        // The 2014 boar (11 HP): "If the boar takes 7 damage or less that would reduce it to 0 hit points, it is reduced to 1".
        var s = Add(Encounter(E2014), Monster(Block(E2014, "boar"), hp: HpChoice.Avg)).Next;
        Assert.True(Step(s, new DamageOp(["boar"]) { Amount = 12 }).Next.Named("Boar").Dead);
        s = Step(s, new DamageOp(["boar"]) { Amount = 5 }).Next;
        s = Step(s, new DamageOp(["boar"]) { Amount = 7 }).Next;
        Assert.False(s.Named("Boar").Dead);
        Assert.True(s.Named("Boar").Defeated);
        s = Step(s, new SetOp([new SetEntry("Boar") { Hp = 1 }])).Next;
        Assert.True(CombatTracker.RelentlessUsed(s.Named("Boar")));
        Assert.True(Step(s, new DamageOp(["boar"]) { Amount = 3 }).Next.Named("Boar").Dead);
    }

    [Fact]
    public void Troll_HeldByRegenerationAtZero_NotSkipped_RegenerationReminderAtItsTurn()
    {
        var s = Add(Encounter(E2014), new AddEntry { Name = "Hero", Hp = HpChoice.Of(40), Side = "party" }, Monster(Block(E2014, "troll"), hp: HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("Hero", Total: 20), new("Troll", Total: 5)] }).Next;
        s = Step(s, new DamageOp(["troll"]) { Amount = 200 }).Next;
        Assert.False(s.Named("Troll").Dead);
        var turn = Step(s, new NextOp());
        Assert.Equal("Troll", turn.Next.TurnHolder!.Name);
        var regeneration = Assert.Single(turn.Of(K.Regeneration));
        Assert.Equal("combat {\"action\": \"heal\", \"targets\": [\"troll\"], \"amount\": 10}", regeneration.Call);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Concentration, grapples, Bloodied

    [Fact]
    public void Concentration_DamageOwesASaveAtTheDc_DroppingBreaksIt()
    {
        var s = Step(PartyAndFoe(), new ConcentrationOp(["P"]) { Spell = "Bless" }).Next;
        var hit = Step(s, new DamageOp(["P"]) { Amount = 24 });
        Assert.Equal([12], hit.Next.Named("P").Concentration!.Pending);
        var dropped = Step(s, new DamageOp(["P"]) { Amount = 31 });
        Assert.Null(dropped.Next.Named("P").Concentration);
        Assert.True(dropped.Says(K.ConcentrationBroken, "Bless"));
    }

    [Fact]
    public void Grapple_EndsWhenTheGrapplerIsKnockedOut()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15), ("C", 10));
        s = Step(s, new ConditionOp(["C"]) { Add = ["grappled"], Source = "A", Dc = 12 }).Next;
        var result = Step(s, new DamageOp(["A"]) { Amount = 40, KnockOut = true });
        Assert.False(result.Next.Named("C").Has("grappled"));
        Assert.True(result.Says(K.GrappleEnded, "A is knocked out"));
    }

    [Fact]
    public void Bloodied2024_AtEachCrossingFromAbove_NotWhileAlreadyBloodied_Not2014()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        var first = Step(s, new DamageOp(["B"]) { Amount = 15 });
        Assert.True(first.Says(K.Bloodied, "B is Bloodied (15/30)"));
        Assert.Empty(Step(first.Next, new DamageOp(["B"]) { Amount = 2 }).Of(K.Bloodied));
        var healed = Step(first.Next, new HealOp(["B"]) { Amount = 10 }).Next;
        Assert.Single(Step(healed, new DamageOp(["B"]) { Amount = 12 }).Of(K.Bloodied));
        Assert.Empty(Step(Fight(E2014, ("A", 20), ("B", 15)), new DamageOp(["B"]) { Amount = 15 }).Of(K.Bloodied));
    }

    [Fact]
    public void Actor_TheTurnHoldersLinesOnce_ASourceOutsideItsTurnGetsItsOwn()
    {
        var s = Fight(E2014, ("A", 20), ("B", 15));
        s = Step(s, new ConditionOp(["A"]) { Add = ["poisoned"] }).Next;
        s = Step(s, new ConditionOp(["A"]) { Add = ["exhaustion"], Level = 3 }).Next;
        var hit = Step(s, new DamageOp(["B"]) { Amount = 3 });
        Assert.Single(hit.Of(K.ConditionEffects), r => r.Text.Contains("A is poisoned", StringComparison.Ordinal) && r.Text.Contains("disadvantage on attack rolls", StringComparison.Ordinal));
        Assert.Single(hit.Of(K.ExhaustionEffects), r => r.Text.Contains("Disadvantage on attack rolls and saving throws", StringComparison.Ordinal));

        // B (poisoned, exhaustion 3) strikes back during A's turn: its own lines, which the context (A's) does not carry.
        var b = Step(Step(s, new ConditionOp(["B"]) { Add = ["poisoned"] }).Next, new ConditionOp(["B"]) { Add = ["exhaustion"], Level = 3 }).Next;
        var retort = Step(b, new DamageOp(["A"]) { Amount = 2, Source = "B" });
        Assert.True(retort.Says(K.ConditionEffects, "B is poisoned", "disadvantage on attack rolls"));
        Assert.True(retort.Says(K.ExhaustionEffects, "B", "Disadvantage on attack rolls and saving throws"));
        Assert.DoesNotContain(Step(Fight(E2014, ("A", 20), ("B", 15)), new DamageOp(["A"]) { Amount = 2, Source = "B" }).Reminders, r => r.Kind == K.ConditionEffects);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Unknown hit points (D17)

    [Fact]
    public void UnknownHp_AccumulatesDamageTaken_NeverDefeated_HealLowersItNotBelowZero()
    {
        var s = Add(Encounter(E2024, player: true), Monster(Block(E2024, "ogre"))).Next;
        var ogre = s.Named("Ogre");
        Assert.Null(ogre.Hp);
        s = Step(s, new DamageOp(["ogre"]) { Amount = 500 }).Next;
        Assert.Equal(500, s.Named("Ogre").DamageTaken);
        Assert.False(s.Named("Ogre").Defeated);
        s = Step(s, new HealOp(["ogre"]) { Amount = 600 }).Next;
        Assert.Equal(0, s.Named("Ogre").DamageTaken);
    }

    [Theory]
    [InlineData(10, "fire", false, false, false, 20)] // vulnerable
    [InlineData(9, "fire", true, false, false, 8)] // half on a save (4), then vulnerable
    [InlineData(10, "fire", false, true, false, 10)] // raw skips every adjustment
    [InlineData(8, "slashing", false, false, false, 4)] // resists nonmagical slashing
    [InlineData(8, "slashing", false, false, true, 8)] // not magical slashing
    [InlineData(8, "necrotic", false, false, false, 0)] // immune
    public void UnknownHp_TheWholePipeline_DamageTakenIsTheAdjustedTotal(int amount, string type, bool half, bool raw, bool magical, int taken)
    {
        // D17 with §6.4: an unknown-HP 2014 mummy (a player campaign's default) takes damage as any mummy would.
        var s = Add(Encounter(E2014, player: true), Monster(Block(E2014, "mummy"))).Next;
        Assert.False(s.Named("Mummy").HpKnown);
        var result = Step(s, new DamageOp(["mummy"]) { Amount = amount, DamageType = type, Half = half ? ["mummy"] : null, Raw = raw, Magical = magical });
        Assert.Equal(taken, result.Next.Named("Mummy").DamageTaken);
        var row = Assert.Single(result.Changes, c => c.Kind == L.Damage);
        Assert.Equal(taken, row.Amount);
        Assert.Equal(taken, JsonNode.Parse(row.Detail!)!["damage_taken"]!.GetValue<int>());
        Assert.False(result.Next.Named("Mummy").Defeated);
    }

    [Fact]
    public void UnknownHp_TheDcComesFromTheAdjustedTotal_NoReminderNamesItsDefences_TheAuthorLineShowsThem()
    {
        var s = Add(Encounter(E2014, player: true), Monster(Block(E2014, "mummy"))).Next;
        s = Step(s, new ConcentrationOp(["mummy"]) { Spell = "Hold Person" }).Next;
        var result = Step(s, new DamageOp(["mummy"]) { Amount = 30, DamageType = "fire" });
        Assert.Equal(60, result.Next.Named("Mummy").DamageTaken);
        Assert.Equal([30], result.Next.Named("Mummy").Concentration!.Pending);
        Assert.True(result.Says(K.ConcentrationSave, "Mummy", "DC 30", "Hold Person"));
        Assert.DoesNotContain(result.Reminders, r => r.Text.Contains("vulnerab", StringComparison.OrdinalIgnoreCase) || r.Text.Contains("resist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Notes, n => n.StartsWith("Mummy: 30 fire ×2 (vulnerable", StringComparison.Ordinal) && n.Contains("60 damage taken so far", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Notes, n => n.Contains(int.MaxValue / 2 + " →", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownHp_ConcentratingPcWithAnEffectsResistance_HalvesTheDamageAndTheDc()
    {
        // Fixture A's Aiden has a sheet without hit points: his own effects still apply (the checker's P01b).
        var s = Add(Encounter(E2014, "Crypt", player: true), PartyA()[0], Monster(Block(E2014, "mummy"), hp: HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("aiden-ironstar", Total: 20), new("mummy", Total: 5)] }).Next;
        s = Step(s, new ConcentrationOp(["aiden-ironstar"]) { Spell = "Bless" }).Next;
        s = Step(s, new ConditionOp(["aiden-ironstar"]) { Add = ["Stoneskin"], Effect = new EffectInput(Resist: ["bludgeoning"]) }).Next;
        var result = Step(s, new DamageOp(["aiden-ironstar"]) { Amount = 30, DamageType = "bludgeoning", Source = "mummy" });
        var aiden = result.Next.Named("Aiden Ironstar");
        Assert.Equal(15, aiden.DamageTaken);
        Assert.Equal([10], aiden.Concentration!.Pending);
        var reminder = Assert.Single(result.Of(K.ConcentrationSave));
        Assert.Contains("DC 10", reminder.Text, StringComparison.Ordinal);
        Assert.Equal("combat {\"action\": \"concentration\", \"targets\": [\"aiden-ironstar\"], \"total\": …}", reminder.Call);
    }

    [Fact]
    public void UnknownHp_TempHpAbsorbsFirst_TheDcFromThePreTempTotal_TheSpentTempHpIsWrittenBack()
    {
        var vars = Sheet("e-vars", """{ "classes": [{ "class": "ranger", "level": 12 }], "temp_hp": 5 }""", E2014);
        Assert.Null(vars.MaxHp);
        var s = Add(Encounter(E2014, "Crypt"), Pc("e-vars", "Vars Nocturne", "character:vars", vars), Monster(Block(E2014, "mummy"), hp: HpChoice.Avg)).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("vars", Total: 20), new("mummy", Total: 5)] }).Next;
        s = Step(s, new ConcentrationOp(["vars"]) { Spell = "Hunter's Mark" }).Next;
        var result = Step(s, new DamageOp(["vars"]) { Amount = 30, DamageType = "slashing", Source = "mummy" });
        var c = result.Next.Named("Vars Nocturne");
        Assert.Equal((0, 25), (c.TempHp, c.DamageTaken));
        Assert.Equal([15], c.Concentration!.Pending);
        var detail = JsonNode.Parse(result.Changes.Single(x => x.Kind == L.Damage).Detail!)!;
        Assert.Equal(5, detail["temp_absorbed"]!.GetValue<int>());
        Assert.Contains(RulingFlags.ConcentrationOnPreTempDamage, detail["rulings"]!.AsArray().Select(r => r!.GetValue<string>()));

        var plan = CombatEnd.Plan(result.Next, new EndOptions(Xp: 0), new EndInputs { Sheets = new Dictionary<string, CharacterSheet> { ["e-vars"] = vars } });
        var back = Assert.Single(plan.WriteBacks);
        Assert.Equal(0, back.After.TempHp);
        Assert.Contains(SheetColumns.TempHp, back.Diff.Columns.Keys);
        Assert.DoesNotContain(SheetColumns.Hp, back.Diff.Columns.Keys);
    }

    [Fact]
    public void UnknownHp_DeathSaveRefusedWithTheFix()
    {
        var s = Add(Encounter(E2024, player: true), Monster(Block(E2024, "ogre"))).Next;
        var ex = Assert.Throws<DndInputException>(() => Step(s, new DeathSaveOp(["ogre"]) { Face = 10 }));
        Assert.Contains("not tracked", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Death saves (§5.6)

    [Fact]
    public void DeathSave_ServerRoll_2024ExhaustionInTheExpression_FaceDecidesNaturals()
    {
        var down = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 30 }).Next;
        down = Step(down, new ConditionOp(["P"]) { Add = ["exhaustion"], Level = 2 }).Next;
        var need = Assert.Single(CombatTracker.Needs(down, new DeathSaveOp(["P"])));
        Assert.Equal(("1d20-4", P.DeathSave), (need.Expression, need.Purpose));
        Assert.Equal(new DeathSaveTally(0, 1, false), Step(down, new DeathSaveOp(["P"]), 13).Next.Named("P").DeathSaves);
        Assert.Equal(new DeathSaveTally(1, 0, false), Step(down, new DeathSaveOp(["P"]), 14).Next.Named("P").DeathSaves);
        Assert.Equal(new DeathSaveTally(0, 2, false), Step(down, new DeathSaveOp(["P"]) { Face = 1 }).Next.Named("P").DeathSaves);
    }

    [Fact]
    public void DeathSave_2014ExhaustionThree_RollsWithDisadvantage()
    {
        var down = Step(PartyAndFoe(E2014), new DamageOp(["P"]) { Amount = 30 }).Next;
        down = Step(down, new ConditionOp(["P"]) { Add = ["exhaustion"], Level = 3 }).Next;
        Assert.Equal("2d20kl1", Assert.Single(CombatTracker.Needs(down, new DeathSaveOp(["P"]))).Expression);
        Assert.Equal(new DeathSaveTally(0, 1, false), Step(down, new DeathSaveOp(["P"]), 18, 4).Next.Named("P").DeathSaves);
    }

    [Fact]
    public void DeathSave_ThirdSuccessStable_ThirdFailureDies_NotDyingRefused()
    {
        var down = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 30 }).Next;
        var p = down.Named("P");
        var nearly = down with { Combatants = down.Combatants.Select(c => c.Id == p.Id ? c with { DeathSaves = new DeathSaveTally(2, 2, false) } : c).ToList() };
        var stable = Step(nearly, new DeathSaveOp(["P"]) { Total = 12 });
        Assert.Equal(DeathSaveTally.Stabilized, stable.Next.Named("P").DeathSaves);
        Assert.True(stable.Says(K.Stable, "P is stable"));
        var died = Step(nearly, new DeathSaveOp(["P"]) { Face = 5 });
        Assert.True(died.Next.Named("P").Dead);
        Assert.Throws<DndInputException>(() => Step(PartyAndFoe(), new DeathSaveOp(["P"]) { Face = 10 }));
        Assert.Throws<DndInputException>(() => Step(stable.Next, new DeathSaveOp(["P"]) { Face = 10 }));
    }

    [Fact]
    public void DeathSave_StableIsFirstAid()
    {
        var down = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 30 }).Next;
        var result = Step(down, new DeathSaveOp(["P"]) { Stable = true });
        Assert.Equal(DeathSaveTally.Stabilized, result.Next.Named("P").DeathSaves);
        Assert.False(result.Next.Named("P").Dying);
        Assert.Equal(["P: stabilised (first aid): death saves 0 successes, 0 failures → 0 successes, 0 failures, stable."], result.Notes);
    }

    [Theory]
    [InlineData(2, 20, null, "P: death save succeeds (a natural 20): hp 0 → 1; death saves 2 successes, 2 failures → 0 successes, 0 failures.")]
    [InlineData(2, null, 12, "P: death save succeeds (12): death saves 2 successes, 2 failures → 0 successes, 0 failures, stable.")]
    [InlineData(2, 5, null, "P: death save fails (5): death saves 2 successes, 2 failures → 0 successes, 3 failures; dead.")]
    [InlineData(2, 1, null, "P: death save fails twice (a natural 1): death saves 2 successes, 2 failures → 0 successes, 3 failures; dead.")]
    [InlineData(1, 14, null, "P: death save succeeds (14): 2 successes, 2 failures.")]
    public void DeathSave_EveryOutcomeSaysWhatChanged_TheOnesThatEndDyingToo(int successes, int? face, int? total, string line)
    {
        // U06: a death, a natural 20 and stability printed only a reminder; every step says what it changed.
        var down = Step(PartyAndFoe(), new DamageOp(["P"]) { Amount = 30 }).Next;
        var p = down.Named("P");
        var nearly = down with { Combatants = down.Combatants.Select(c => c.Id == p.Id ? c with { DeathSaves = new DeathSaveTally(successes, 2, false) } : c).ToList() };

        var result = Step(nearly, new DeathSaveOp(["P"]) { Face = face, Total = total });

        Assert.Equal([line], result.Notes);
    }


    // ------------------------------------------------------------------------------------------------------------------
    // Rolls are cited, items are spent only on a heal that healed, the dead get no temporary hit points

    [Fact]
    public void Rolls_EachDicePartIsCited_TheRowsTheFirst_ANoteRowEachOther()
    {
        var s = Fight(E2024, ("A", 20), ("B", 15));
        var op = new DamageOp(["B"]) { Parts = [new(null, "1d6", "slashing"), new(null, "2d4", "fire"), new(3, null, "cold")] };
        var result = Step(s, op, 4, 2, 3);
        var damage = Assert.Single(result.Changes, c => c.Kind == L.Damage);
        Assert.Equal("damage:1", damage.RollKey);
        Assert.Equal(
            """[{"amount":4,"type":"slashing","roll":"damage:1"},{"amount":5,"type":"fire","roll":"damage:2"},{"amount":3,"type":"cold"}]""",
            JsonNode.Parse(damage.Detail!)!["parts"]!.ToJsonString());
        var note = Assert.Single(result.Changes, c => c.Kind == L.Note);
        Assert.Equal(("damage:2", 5), (note.RollKey, note.Amount));
        Assert.Equal("2d4", JsonNode.Parse(note.Detail!)!["expression"]!.GetValue<string>());
    }

    public static TheoryData<string> RollScenarios => ["parts", "damage on the dead", "heal on the dead", "heal on a leaver", "initiative", "hit points", "death save", "concentration save"];

    [Theory]
    [MemberData(nameof(RollScenarios))]
    public void Rolls_EveryServerRoll_IsCitedByARow(string scenario)
    {
        var abc = Fight(E2024, ["P"], ("P", 20), ("B", 15), ("C", 10));
        var deadB = Step(abc, new DamageOp(["B"]) { Amount = 99 }).Next;
        var (state, op, faces) = scenario switch
        {
            "parts" => (abc, (CombatOp)new DamageOp(["B", "C"]) { Parts = [new(null, "1d6", "fire"), new(null, "1d4", "cold")] }, new[] { 3, 2 }),
            "damage on the dead" => (deadB, new DamageOp(["B"]) { Dice = "2d6" }, [3, 4]),
            "heal on the dead" => (deadB, new HealOp(["B"]) { Dice = "1d8" }, [5]),
            "heal on a leaver" => (Step(abc, new LeaveOp(["C"])).Next, new HealOp(["C"]) { Dice = "1d8", Temp = true }, [5]),
            "initiative" => (Add(Encounter(), new AddEntry { Name = "X", Hp = HpChoice.Of(5) }, new AddEntry { Name = "Y", Hp = HpChoice.Of(5) }).Next, new InitiativeOp(), [10, 12]),
            "hit points" => (Encounter(E2014), new AddOp([Monster(Block(E2014, "goblin"), 2, HpChoice.Roll)], ["m1", "m2"]), [4, 4, 4, 4]),
            "death save" => (Step(abc, new DamageOp(["P"]) { Amount = 30 }).Next, new DeathSaveOp(["P"]), [12]),
            _ => (Step(Step(abc, new ConcentrationOp(["P"]) { Spell = "Bless" }).Next, new DamageOp(["P"]) { Amount = 4 }).Next, new ConcentrationOp(["P"]), [12]),
        };
        var needs = CombatTracker.Needs(state, op);
        Assert.NotEmpty(needs);
        var result = Step(state, op, faces);
        Assert.All(needs, n => Assert.Contains(result.Changes, c => c.RollKey == n.Key));
    }

    [Fact]
    public void HealItem_OnADeadTarget_IsNotUsed_OnALivingOneItIs()
    {
        var s = Add(Encounter(E2024), Pc("e-bjorn", "Björn", "character:bjorn", Bjorn()), new AddEntry { Name = "Corpse", Hp = HpChoice.Of(5), Side = "ally" }).Next;
        s = Step(s, new InitiativeOp { Rolls = [new("bjorn", Total: 20), new("Corpse", Total: 5)] }).Next;
        var dead = Step(s, new DamageOp(["Corpse"]) { Amount = 20 }).Next;
        var poured = Step(dead, new HealOp(["Corpse"]) { Amount = 7, Source = "bjorn", Item = "Potion of Healing", Holdings = HoldingsB() });
        Assert.Contains("no effect: Corpse is dead (a dead creature regains no hit points until it is revived)", poured.Notes);
        Assert.False(poured.Next.Named("Björn").Resources.ContainsKey(CombatValues.ResourceKeys.Item("h-potion")));
        Assert.DoesNotContain(poured.Changes, c => c.Kind == L.Resource);
        var drunk = Step(s, new HealOp(["Corpse"]) { Amount = 7, Source = "bjorn", Item = "Potion of Healing", Holdings = HoldingsB() });
        Assert.Equal(1, drunk.Next.Named("Björn").Resources[CombatValues.ResourceKeys.Item("h-potion")].Used);
    }

    [Fact]
    public void TempHp_OnTheDead_NoEffect_NoRow()
    {
        var dead = Step(Fight(E2024, ("A", 20), ("B", 15)), new DamageOp(["B"]) { Amount = 99 }).Next;
        var result = Step(dead, new HealOp(["B"]) { Amount = 8, Temp = true });
        Assert.Contains("no effect: B is dead", result.Notes);
        Assert.Equal(0, result.Next.Named("B").TempHp);
        Assert.DoesNotContain(result.Changes, c => c.Kind == L.TempHp);
    }

    [Theory]
    [InlineData("unconscious")]
    [InlineData("paralyzed")]
    public void UnconsciousCrit_AParalyzedOrUnconsciousTarget(string condition)
    {
        var s = Step(Fight(E2024, ("A", 20), ("B", 15)), new ConditionOp(["B"]) { Add = [condition] }).Next;
        var result = Step(s, new DamageOp(["B"]) { Amount = 2 });
        Assert.True(result.Says(K.UnconsciousCrit, $"B is {condition}", "a hit from within 5 ft is a Critical Hit"));
        Assert.Empty(Step(Fight(E2024, ("A", 20), ("B", 15)), new DamageOp(["B"]) { Amount = 2 }).Of(K.UnconsciousCrit));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Actor_2014ExhaustionOutsideItsTurn_ALineFromLevel3Only(int level, bool line)
    {
        var s = Fight(E2014, ("A", 20), ("B", 15));
        s = Step(s, new ConditionOp(["B"]) { Add = ["exhaustion"], Level = level }).Next;
        var retort = Step(s, new DamageOp(["A"]) { Amount = 2, Source = "B" });
        Assert.Equal(line, retort.Of(K.ExhaustionEffects).Any(r => r.CombatantId == s.Named("B").Id));
    }

    [Fact]
    public void Heal_AnEnemyStillUnconsciousFromACondition_StaysDefeated()
    {
        var s = Step(Fight(E2024, ("A", 20), ("B", 15)), new ConditionOp(["B"]) { Add = ["unconscious"] }).Next;
        s = Step(s, new DamageOp(["B"]) { Amount = 5 }).Next;
        Assert.True(s.Named("B").Defeated);
        var healed = Step(s, new HealOp(["B"]) { Amount = 3 }).Next.Named("B");
        Assert.Equal(28, healed.Hp);
        Assert.True(healed.Defeated);
    }

    [Fact]
    public void UndeadFortitude_ADeathSaverWhoseTraitFails_IsDropUnconscious_HealingWakesIt()
    {
        var s = Add(Encounter(E2014), Monster(Block(E2014, "zombie"), hp: HpChoice.Avg) with { DeathSaves = true }).Next;
        var held = Step(s, new DamageOp(["zombie"]) { Amount = 30, DamageType = "slashing" });
        var reminder = Assert.Single(held.Of(K.DeathInterceptor));
        const string fails = "combat {\"action\": \"condition\", \"targets\": [\"zombie\"], \"add\": [\"unconscious\"]}";
        Assert.Contains(fails, reminder.Text, StringComparison.Ordinal);

        // The reminder's "if it fails" call: the drop's own Unconscious (and Prone), dying.
        var failed = Step(held.Next, new ConditionOp(["zombie"]) { Add = ["unconscious"] }).Next.Named("Zombie");
        Assert.Equal(D.ZeroHp, failed.Conditions.Single(c => c.Name == "unconscious").Duration);
        Assert.True(failed.Has("prone"));
        Assert.True(failed.Dying);
        var healed = Step(Step(held.Next, new ConditionOp(["zombie"]) { Add = ["unconscious"] }).Next, new HealOp(["zombie"]) { Amount = 5 }).Next.Named("Zombie");
        Assert.False(healed.Has("unconscious"));
        Assert.True(healed.Has("prone"));
        Assert.False(healed.Defeated);
    }
}
