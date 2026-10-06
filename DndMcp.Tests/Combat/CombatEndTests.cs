using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Core;
using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: <see cref="CombatEnd.Plan"/> writes back only the combat-owned fields a fight changed (contract §6.7, D6),
/// refuses drift naming each field and both values unless forced, persists exactly the conditions and concentration that
/// outlive the fight (until_removed; rounds with more than 10 left, counted as <c>expires.round − round − (1 if the anchor
/// has acted)</c>), writes deaths and the dying as they are, consumes holdings as current − used, and splits XP equally
/// (floor) among the party-side sheet-seeded combatants that stayed, reporting the remainder; left enemies count, left
/// party members are written back without a share.
/// </summary>
public sealed class CombatEndTests
{
    private static Dictionary<string, CharacterSheet> SheetsB() => PartyB().ToDictionary(p => p.EntityId!, p => p.Sheet!);

    /// <summary>Fixture B's party against an Aboleth (in or out of its lair), round 1, the monk to act.</summary>
    private static EncounterState FightB(bool lair = false)
    {
        var s = Add(Encounter(E2024, "Station", lair: lair), [.. PartyB()]).Next;
        s = Add(s, Monster(Block(E2024, "aboleth"), hp: HpChoice.Avg)).Next;
        return Step(s, new InitiativeOp
        {
            Rolls = [new("fishman-monk", Total: 19), new("aboleth", Total: 13), new("dragon-slayer", Total: 11), new("bjorn-mountainfell", Total: 8)],
        }).Next;
    }

    private static EndPlan Plan(EncounterState s, EndOptions? options = null, Dictionary<string, CharacterSheet>? sheets = null, IReadOnlyList<CombatItem>? holdings = null) =>
        CombatEnd.Plan(s, options ?? new EndOptions(), new EndInputs
        {
            Sheets = sheets ?? SheetsB(),
            Holdings = holdings ?? HoldingsB(),
            SafeNames = s.Combatants.ToDictionary(c => c.Id, c => c.Name),
        });

    private static EncounterState KillAboleth(EncounterState s) => Step(s, new DamageOp(["aboleth"]) { Amount = 999 }).Next;

    // ------------------------------------------------------------------------------------------------------------------
    // XP

    [Fact]
    public void Xp_OutOfLair5900For3_1966EachAndTwoUndistributed()
    {
        var plan = Plan(KillAboleth(FightB()));
        Assert.Equal((5_900, 5_900, 1_966, 2, 3), (plan.Xp.Worth, plan.Xp.Awarded, plan.Xp.Each, plan.Xp.Remainder, plan.Xp.Recipients));
        Assert.Contains("2 XP undistributed", plan.Xp.Text, StringComparison.Ordinal);
        Assert.All(plan.WriteBacks, w => Assert.Equal(35_966, w.After.Xp));
    }

    [Fact]
    public void Xp_Zero_AwardsNone_N_AwardsN()
    {
        var s = KillAboleth(FightB());
        Assert.Empty(Plan(s, new EndOptions(Xp: 0)).Awards);
        Assert.Contains("xp: 0", Plan(s, new EndOptions(Xp: 0)).Xp.Text, StringComparison.Ordinal);
        Assert.All(Plan(s, new EndOptions(Xp: 300)).Awards, a => Assert.Equal(100, a.Amount));
    }

    [Fact]
    public void Xp_GivenWithNoPartySheet_IsRefused_NegativeIsRefused()
    {
        var s = KillAboleth(Fight(E2024, ("A", 20), ("aboleth-stand-in", 10)));
        Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 100)));
        Assert.Throws<DndInputException>(() => Plan(FightB(), new EndOptions(Xp: -1)));
    }

    [Fact]
    public void Xp_ALeftEnemyCounts_AnEnemyStillStandingDoesNot()
    {
        var s = Add(FightB(), Monster(Block(E2024, "goblin-warrior"), 2, HpChoice.Avg)).Next;
        s = Step(s, new LeaveOp(["goblin-warrior"])).Next;
        var plan = Plan(s);
        Assert.Equal(Block(E2024, "goblin-warrior").Xp, plan.Xp.Worth);
    }

    [Fact]
    public void Xp_ALeftPartyMember_IsWrittenBackWithoutAShare()
    {
        var s = Step(FightB(), new DamageOp(["dragon-slayer"]) { Amount = 10 }).Next;
        s = Step(s, new LeaveOp(["dragon-slayer"])).Next;
        var plan = Plan(KillAboleth(s));
        Assert.Equal(2, plan.Xp.Recipients);
        Assert.Equal(2_950, plan.Xp.Each);
        var slayer = plan.WriteBacks.Single(w => w.EntityId == "e-slayer");
        Assert.Equal((58, 34_000), (slayer.After.Hp!.Value, slayer.After.Xp!.Value));
    }

    [Fact]
    public void Xp_AMilestoneSheet_RecordsTheAward_LeavesTheSheetUnchanged()
    {
        var sheets = SheetsB();
        sheets["e-monk"] = sheets["e-monk"] with { Xp = null };
        var plan = Plan(KillAboleth(FightB()), sheets: sheets);
        var monk = plan.Awards.Single(a => a.EntityId == "e-monk");
        Assert.False(monk.SheetTracksXp);
        Assert.DoesNotContain(plan.WriteBacks, w => w.EntityId == "e-monk");
        Assert.Contains(plan.Summary, s => s.StartsWith("XP not written to The fishman monk: the sheet has no XP total", StringComparison.Ordinal));

        // U02: a sheet with no XP is "no XP total", never a milestone policy nobody chose.
        Assert.DoesNotContain(plan.Summary, s => s.Contains("milestone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Xp_ReachingALevel_RemindsLevelUpWithTheCall()
    {
        var sheets = SheetsB();
        sheets["e-bjorn"] = sheets["e-bjorn"] with { Xp = 46_500 };
        var plan = Plan(KillAboleth(FightB()), sheets: sheets);
        var reminder = Assert.Single(plan.Reminders, r => r.Kind == K.LevelUp);
        Assert.Equal("campaign_character {\"action\": \"level_up\", \"character\": \"character:bjorn-mountainfell\"}", reminder.Call);
        Assert.Contains("level 9", reminder.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Xp_NoEnemyDefeatedOrLeft_NotAwarded_SaysWhatItsEnemiesAreWorth_AndTheCallsThatGiveIt()
    {
        // U02: "worth 0 XP" with no call read as "there was nothing to split"; the enemies are worth 5,900 and the end or
        // the sheets can still get it. A real end has ended the fight, so its calls are the sheets'; a dry run's is the end's.
        var real = Plan(FightB());
        var preview = Plan(FightB(), new EndOptions(DryRun: true));

        Assert.Empty(real.Awards);
        Assert.Equal(
            "XP not awarded: no enemy was defeated or left. By the 2024 rules its enemies are worth 5,900 XP, 1,966 each for 3. If the party beat them, " +
            "add each share to its sheet: campaign_character {\"action\": \"xp\", \"character\": \"character:bjorn-mountainfell\", \"amount\": 1966}; " +
            "campaign_character {\"action\": \"xp\", \"character\": \"character:dragon-slayer\", \"amount\": 1966}; " +
            "campaign_character {\"action\": \"xp\", \"character\": \"character:fishman-monk\", \"amount\": 1966} " +
            "(each adds to that sheet's XP total and records no award).",
            real.Xp.Text);
        Assert.Equal(
            "XP not awarded: no enemy was defeated or left. By the 2024 rules its enemies are worth 5,900 XP, 1,966 each for 3. If the party beat them, " +
            "award it: combat {\"action\": \"end\", \"xp\": 5900}.",
            preview.Xp.Text);
        Assert.Equal(0, real.Xp.Worth);
    }

    [Fact]
    public void Xp_NoEnemyDefeatedOrLeft_NoSheetTracksXp_SaysBoth_EnemiesWithNoStatBlockAreNamed()
    {
        var sheets = SheetsB().ToDictionary(p => p.Key, p => p.Value with { Xp = null });
        var s = Add(FightB(), new AddEntry { Name = "Crab", Hp = HpChoice.Of(5) }).Next;

        var plan = Plan(s, new EndOptions(DryRun: true), sheets);

        Assert.Equal(
            "XP not awarded: no enemy was defeated or left, and no party sheet tracks XP. By the 2024 rules its enemies are worth 5,900 XP, 1,966 each for 3 " +
            "(Crab has no stat block, so no XP). If the party beat them, award it: combat {\"action\": \"end\", \"xp\": 5900}.",
            plan.Xp.Text);
    }

    [Fact]
    public void Xp_NoEnemyDefeatedOrLeft_EnemiesWorthNothing_NoCall()
    {
        var s = Add(Add(Encounter(E2024, "Reef"), [.. PartyB()]).Next, new AddEntry { Name = "Shark", Hp = HpChoice.Of(30) }).Next;

        var plan = Plan(s);

        Assert.Equal("XP not awarded (no enemy was defeated or left). By the 2024 rules this fight is worth 0 XP, 0 each for 3.", plan.Xp.Text);
    }

    [Fact]
    public void Xp_NoEnemyDefeatedOrLeft_ASheetWithNoXpTotal_GetsTheStartATotalLine_NeverAPlainCall()
    {
        // RR03: a plain xp call on a sheet with no XP total silently started tracking it ("none → 1,966"); it is offered
        // only as what it is, the start of a total, saying it records no award.
        var sheets = SheetsB();
        sheets["e-monk"] = sheets["e-monk"] with { Xp = null };

        var plan = Plan(FightB(), sheets: sheets);

        Assert.Equal(
            "XP not awarded: no enemy was defeated or left. By the 2024 rules its enemies are worth 5,900 XP, 1,966 each for 3. If the party beat them, " +
            "add each share to its sheet: campaign_character {\"action\": \"xp\", \"character\": \"character:bjorn-mountainfell\", \"amount\": 1966}; " +
            "campaign_character {\"action\": \"xp\", \"character\": \"character:dragon-slayer\", \"amount\": 1966} " +
            "(each adds to that sheet's XP total and records no award).",
            plan.Xp.Text);
        Assert.Contains(
            "XP not written to The fishman monk: the sheet has no XP total; start one with: " +
            "campaign_character {\"action\": \"xp\", \"character\": \"character:fishman-monk\", \"amount\": 1966} (it sets the sheet's XP total and records no award).",
            plan.Summary);

        // Nobody tracks XP: the worth, then one start-a-total line per sheet.
        var none = Plan(FightB(), sheets: SheetsB().ToDictionary(p => p.Key, p => p.Value with { Xp = null }));
        Assert.Equal(
            "XP not awarded: no enemy was defeated or left, and no party sheet tracks XP. By the 2024 rules its enemies are worth 5,900 XP, 1,966 each for 3. " +
            "If the party beat them, start each sheet's XP total with its own call.",
            none.Xp.Text);
        Assert.Equal(3, none.Summary.Count(l => l.StartsWith("XP not written to ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Xp_EveryPrintedCall_SentAsPrinted_DoesWhatItsTextSays()
    {
        // RR03: "adds to that sheet's XP total" on a sheet with one, "sets the sheet's XP total" on one without.
        var sheets = SheetsB();
        sheets["e-monk"] = sheets["e-monk"] with { Xp = null };
        var plan = Plan(FightB(), sheets: sheets);
        var byHandle = new Dictionary<string, CharacterSheet>
        {
            ["character:bjorn-mountainfell"] = sheets["e-bjorn"],
            ["character:dragon-slayer"] = sheets["e-slayer"],
            ["character:fishman-monk"] = sheets["e-monk"],
        };

        var calls = plan.Summary
            .SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l, @"campaign_character (\{[^}]*\})").Select(m => (Line: l, Call: m.Groups[1].Value)))
            .ToList();

        Assert.Equal(3, calls.Count);
        foreach (var (line, call) in calls)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(call)!;
            Assert.Equal("xp", json["action"]!.GetValue<string>());
            var sheet = byHandle[json["character"]!.GetValue<string>()];
            var amount = json["amount"]!.GetValue<int>();
            var after = SheetXp.Apply(sheet, amount).Sheet;
            if (line.Contains("(each adds to that sheet's XP total and records no award)", StringComparison.Ordinal))
            {
                Assert.Equal(sheet.Xp + amount, after.Xp);
            }
            else
            {
                Assert.Contains("(it sets the sheet's XP total and records no award)", line, StringComparison.Ordinal);
                Assert.Null(sheet.Xp);
                Assert.Equal(amount, after.Xp);
            }
        }
    }

    [Fact]
    public void Xp_ASheetWithNoXpTotal_TheAwardIsRecorded_TheLineSaysNotWrittenAndHowToStartOne()
    {
        // U02: "the sheet levels by milestone … To track XP on this sheet instead" read as a policy the call would overturn,
        // so the model declined to run it; the sheet simply has no XP total.
        var sheets = SheetsB();
        sheets["e-monk"] = sheets["e-monk"] with { Xp = null };

        var plan = Plan(KillAboleth(FightB()), sheets: sheets);

        Assert.Contains(
            "XP not written to The fishman monk: the sheet has no XP total, so its 1,966 XP share is recorded as an award only; start one with: " +
            "campaign_character {\"action\": \"xp\", \"character\": \"character:fishman-monk\", \"amount\": 1966} (it sets the sheet's XP total and records no award).",
            plan.Summary);
    }

    [Fact]
    public void Xp_DefeatedEnemies_NoSheetTracksXp_EachSheetGetsTheStartATotalLine()
    {
        // U02 (the deferred defeated-enemies path): "split the XP" left every sheet without XP and no way to give it.
        var plan = Plan(KillAboleth(FightB()), sheets: SheetsB().ToDictionary(p => p.Key, p => p.Value with { Xp = null }));

        Assert.Equal("XP not awarded (no party sheet tracks XP). By the 2024 rules this fight is worth 5,900 XP, 1,966 each for 3.", plan.Xp.Text);
        Assert.Equal(
            [
                "XP not written to Björn Mountainfell: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
                "\"character:bjorn-mountainfell\", \"amount\": 1966} (it sets the sheet's XP total and records no award).",
                "XP not written to The amethyst Dragon Slayer: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
                "\"character:dragon-slayer\", \"amount\": 1966} (it sets the sheet's XP total and records no award).",
                "XP not written to The fishman monk: the sheet has no XP total; start one with: campaign_character {\"action\": \"xp\", \"character\": " +
                "\"character:fishman-monk\", \"amount\": 1966} (it sets the sheet's XP total and records no award).",
            ],
            plan.Summary.Where(l => l.StartsWith("XP not written to ", StringComparison.Ordinal)));
        Assert.Empty(plan.Awards);

        // xp: 0 says none is given: no start-a-total line.
        Assert.DoesNotContain(Plan(KillAboleth(FightB()), new EndOptions(Xp: 0), SheetsB().ToDictionary(p => p.Key, p => p.Value with { Xp = null })).Summary,
            l => l.StartsWith("XP not written to ", StringComparison.Ordinal));
    }

    [Fact]
    public void Xp_TheDefeatedAreWorthNothing_NotAwardedSayingSo()
    {
        var s = Add(FightB(), new AddEntry { Name = "Crab", Hp = HpChoice.Of(5), Ac = 10 }).Next;
        s = Step(s, new DamageOp(["crab"]) { Amount = 5 }).Next;

        var plan = Plan(s);

        Assert.Equal(
            "XP not awarded (the defeated and left enemies are worth no XP). By the 2024 rules this fight is worth 0 XP, 0 each for 3 (Crab has no stat " +
            "block, so no XP).",
            plan.Xp.Text);
    }

    [Fact]
    public void Xp_DryRun_SaysWhatWouldBeAwarded_NeverAwarded()
    {
        var sheets = SheetsB();
        sheets["e-bjorn"] = sheets["e-bjorn"] with { Xp = 46_500 };
        sheets["e-monk"] = sheets["e-monk"] with { Xp = null };
        var s = KillAboleth(FightB());

        var preview = Plan(s, new EndOptions(DryRun: true), sheets);
        var real = Plan(s, new EndOptions(), sheets);

        Assert.StartsWith("Awarded 5,900 XP: 1,966 each to ", real.Xp.Text, StringComparison.Ordinal);
        Assert.Equal("Would award" + real.Xp.Text["Awarded".Length..], preview.Xp.Text);
        Assert.Contains("XP not written to The fishman monk: the sheet has no XP total, so its 1,966 XP share would be recorded as an award only.", preview.Summary);
        Assert.Contains("The real end prints the call to start its XP total.", preview.Summary);
        Assert.Contains("XP would reach level 9: level up", Assert.Single(preview.Reminders, r => r.Kind == K.LevelUp).Text, StringComparison.Ordinal);
        Assert.All(preview.Summary.Concat(preview.Reminders.Select(r => r.Text)), line =>
        {
            Assert.DoesNotContain("Awarded", line, StringComparison.Ordinal);
            Assert.DoesNotContain("XP recorded", line, StringComparison.Ordinal);
            Assert.DoesNotContain("reaches level", line, StringComparison.Ordinal);
        });

        // Only the words differ: the same awards are planned (the Repository rolls the dry run back).
        Assert.Equal(real.Awards, preview.Awards);
        Assert.Equal(real.Xp with { Text = string.Empty }, preview.Xp with { Text = string.Empty });
    }

    /// <summary>
    /// F2R06: a dry run prints no campaign_character xp call. Sent before the real end (the dry run's own advice read as a
    /// step to take), each call started a sheet's total at its share, and the real end, finding a total, added the share
    /// again: counted twice. The dry run says the real end prints the calls; the real end does, one per sheet.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Xp_DryRun_PrintsNoXpCall_ItSaysTheRealEndPrintsThem(bool award)
    {
        var none = SheetsB().ToDictionary(p => p.Key, p => p.Value with { Xp = null });
        var options = award ? new EndOptions(Xp: 300) : new EndOptions();
        var s = KillAboleth(FightB());

        var preview = Plan(s, options with { DryRun = true }, none);
        var real = Plan(s, options, none);

        Assert.DoesNotContain(preview.Summary, l => l.Contains("campaign_character", StringComparison.Ordinal));
        Assert.Equal(3, preview.Summary.Count(l => l.StartsWith("XP not written to ", StringComparison.Ordinal) && l.EndsWith('.')));
        Assert.Contains("The real end prints the calls to start XP totals.", preview.Summary);
        Assert.Equal(3, real.Summary.Count(l => l.Contains("campaign_character {\"action\": \"xp\"", StringComparison.Ordinal)));
        Assert.DoesNotContain(real.Summary, l => l.StartsWith("The real end", StringComparison.Ordinal));
    }

    [Fact]
    public void Xp_NoPartySheetTracksIt_NotAwarded_ButTheWorthIsSaid()
    {
        var sheets = SheetsB().ToDictionary(p => p.Key, p => p.Value with { Xp = null });
        var plan = Plan(KillAboleth(FightB(lair: true)), sheets: sheets);
        Assert.Empty(plan.Awards);
        Assert.Equal("XP not awarded (no party sheet tracks XP). By the 2024 rules this fight is worth 7,200 XP, 2,400 each for 3.", plan.Xp.Text);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // What is written

    [Fact]
    public void NothingChanged_EmptyPlan()
    {
        var sheets = SheetsB().ToDictionary(p => p.Key, p => p.Value with { Xp = null });
        var plan = Plan(FightB(), sheets: sheets);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Discard_WritesNothing()
    {
        var plan = Plan(Step(KillAboleth(FightB()), new DamageOp(["bjorn-mountainfell"]) { Amount = 5 }).Next, new EndOptions(Discard: true));
        Assert.True(plan.IsEmpty);
        Assert.Contains("discard", plan.Summary.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Died_WrittenWithHpZeroAndThreeFailures_ExhaustionSix_AndAStatusProposal()
    {
        var s = Step(FightB(), new ConditionOp(["bjorn-mountainfell"]) { Add = ["exhaustion"], Level = 6 }).Next;
        Assert.True(s.Named("Björn Mountainfell").Dead);
        var plan = Plan(s, new EndOptions(Xp: 0));
        var bjorn = plan.WriteBacks.Single(w => w.EntityId == "e-bjorn");
        Assert.Equal((0, 6, 3), (bjorn.After.Hp!.Value, bjorn.After.Exhaustion, bjorn.After.DeathSaves.Failures));
        var proposal = Assert.Single(plan.Proposals);
        Assert.Equal(("character:bjorn-mountainfell", "dead"), (proposal.EntityHandle, proposal.Status));
    }

    [Fact]
    public void Dying_WrittenWithHpZeroItsTallies_AndUnconsciousUntilRemoved_NeverProne_RemindedWithTheWayOut()
    {
        // C08: the same shape campaign_character damage writes for a drop to 0; the next fight seeds it back as down.
        // UR03: never Prone, which nothing on the sheet would end (a heal ends Unconscious only, a rest keeps it).
        var s = Step(FightB(), new DamageOp(["fishman-monk"]) { Amount = 60 }).Next;
        s = Step(s, new DamageOp(["fishman-monk"]) { Amount = 2 }).Next;
        Assert.Equal(["unconscious", "prone"], s.Named("The fishman monk").Conditions.Select(c => c.Name));
        var plan = Plan(s, new EndOptions(Xp: 0));
        var monk = plan.WriteBacks.Single(w => w.EntityId == "e-monk");
        Assert.Equal((0, 1), (monk.After.Hp!.Value, monk.After.DeathSaves.Failures));
        Assert.Equal("""[{"name":"unconscious","duration":"until_removed"}]""", SheetJson.WriteConditions(monk.After.Conditions));

        // The drop's Prone is the down state the sheet's Unconscious seeds again, not something that ended.
        Assert.DoesNotContain(plan.Summary, x => x.StartsWith("Ended with the fight", StringComparison.Ordinal));

        // U13: the heal call follows "or heal on the sheet:", never the sentence about damage calls.
        var reminder = Assert.Single(plan.Reminders, r => r.Kind == K.DyingAtEnd);
        Assert.Equal(
            "The fishman monk is dying at 0 HP (0 successes, 1 failure): continue with combat {\"action\": \"start\", …} and death_save (out of combat, a " +
            "critical at 0 HP is two damage calls, each adding one failure), or heal on the sheet:",
            reminder.Text);
        Assert.Equal("campaign_character {\"action\": \"heal\", \"character\": \"character:fishman-monk\", \"amount\": …}", reminder.Call);
    }

    [Fact]
    public void Dying_WrittenBack_TheNextFightSeedsItDown_UnconsciousAndProne_WithItsReminders()
    {
        // C08: the end's own way out ("continue with combat start and death_save") must give a dying, unconscious PC.
        var s = Step(FightB(), new DamageOp(["fishman-monk"]) { Amount = 60 }).Next;
        var written = Plan(s, new EndOptions(Xp: 0)).WriteBacks.Single(w => w.EntityId == "e-monk").After;

        var next = Add(Encounter(E2024, "Second"), PartyB()[2] with { Sheet = written }, new AddEntry { Name = "Orc", Hp = HpChoice.Of(15) }).Next;
        next = Step(next, new InitiativeOp { Rolls = [new("Orc", Total: 12), new("fishman-monk", Total: 5)] }).Next;

        var monk = next.Named("The fishman monk");
        Assert.Equal(["unconscious", "prone"], monk.Conditions.Select(c => c.Name));
        Assert.Equal([CombatValues.Durations.ZeroHp, CombatValues.Durations.UntilStands], monk.Conditions.Select(c => c.Duration));
        Assert.True(monk.Dying);
        Assert.True(Step(next, new DamageOp(["fishman-monk"]) { Amount = 2, Source = "orc" }).Says(K.UnconsciousCrit, "The fishman monk is unconscious"));
        var turn = Step(next, new NextOp());
        Assert.True(turn.Says(K.DeathSaveDue, "The fishman monk: death saving throw due"));
        Assert.True(turn.Says(K.ConditionEffects, "The fishman monk is unconscious"));
    }

    [Fact]
    public void Dying_WrittenBack_TheNextFightsFirstHeal_LeavesItProneOnlyUntilItStands_ThatFightWritesNoProne()
    {
        // UR03: the next fight's Prone is the tracker's until_stands (the rules' "prone until it stands"), so it ends with
        // that fight; the sheet never carries it into a third one.
        var sheets = SheetsB();
        var s = Step(FightB(), new DamageOp(["fishman-monk"]) { Amount = 60 }).Next;
        var written = Plan(s, new EndOptions(Xp: 0), sheets).WriteBacks.Single(w => w.EntityId == "e-monk").After;
        sheets["e-monk"] = written;

        var next = Add(Encounter(E2024, "Second"), PartyB()[2] with { Sheet = written }, new AddEntry { Name = "Orc", Hp = HpChoice.Of(15) }).Next;
        next = Step(next, new InitiativeOp { Rolls = [new("Orc", Total: 12), new("fishman-monk", Total: 5)] }).Next;
        var healed = Step(next, new HealOp(["fishman-monk"]) { Amount = 6 });

        Assert.True(healed.Says(K.Revived, "The fishman monk regains consciousness at 6 HP (still prone)"));
        var prone = Assert.Single(healed.Next.Named("The fishman monk").Conditions);
        Assert.Equal(("prone", CombatValues.Durations.UntilStands), (prone.Name, prone.Duration));

        var end = CombatEnd.Plan(healed.Next, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets });
        var after = end.WriteBacks.Single().After;
        Assert.Equal(6, after.Hp);
        Assert.Empty(after.Conditions);
        Assert.Contains("Ended with the fight: prone (The fishman monk).", end.Summary);
    }

    [Fact]
    public void Dying_WrittenBack_HealedOnTheSheet_TheNextFightFindsItStanding()
    {
        // UR03's reproduction: a heal on the sheet ends Unconscious and keeps Prone (campaign_character heal), and a rest
        // keeps it too, so a Prone written at the end stayed for good: the next day's fight seeded it prone, reminded
        // Disadvantage every turn, and wrote it back again.
        var sheets = SheetsB();
        var s = Step(FightB(), new DamageOp(["fishman-monk"]) { Amount = 60 }).Next;
        var written = Plan(s, new EndOptions(Xp: 0), sheets).WriteBacks.Single(w => w.EntityId == "e-monk").After;
        var healed = written with
        {
            Hp = 30,
            DeathSaves = SheetDeathSaves.Reset,
            Conditions = written.Conditions.Where(c => !string.Equals(c.Name, "unconscious", StringComparison.OrdinalIgnoreCase)).ToList(),
        };
        sheets["e-monk"] = healed;

        var next = Add(Encounter(E2024, "Next day"), PartyB()[2] with { Sheet = healed }, new AddEntry { Name = "Orc", Hp = HpChoice.Of(15) }).Next;
        next = Step(next, new InitiativeOp { Rolls = [new("fishman-monk", Total: 12), new("Orc", Total: 5)] }).Next;

        Assert.Empty(next.Named("The fishman monk").Conditions);
        Assert.DoesNotContain(CombatContext.Reminders(next), r => r.Kind == K.ConditionEffects);
        Assert.True(CombatEnd.Plan(next, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets }).IsEmpty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DroppedOutOfCombat_HealedInTheFight_WritesBackConscious_AStoredProneStaysAsTheSheetHasIt(bool storedProne)
    {
        // CV01: the heal in the fight ends the sheet's Unconscious; the write-back says so (the seeded Prone ends with the
        // fight). UR03: a Prone the sheet already holds (an author set it) is the sheet's, kept as it is, never doubled.
        var down = SheetsB()["e-monk"] with
        {
            Hp = 0,
            DeathSaves = new SheetDeathSaves(0, 1, false),
            Conditions = storedProne
                ? [new SheetCondition("unconscious", null, "until_removed", null, null), new SheetCondition("prone", null, "until_removed", null, null)]
                : [new SheetCondition("unconscious", null, "until_removed", null, null)],
        };
        var s = Add(Encounter(E2024, "Station"), PartyB()[2] with { Sheet = down }).Next;
        Assert.Single(s.Named("The fishman monk").Conditions, c => c.Name == "prone");
        s = Step(s, new InitiativeOp { Rolls = [new("fishman-monk", Total: 5)] }).Next;
        s = Step(s, new HealOp(["fishman-monk"]) { Amount = 5 }).Next;

        var plan = CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = new Dictionary<string, CharacterSheet> { ["e-monk"] = down } });

        var after = plan.WriteBacks.Single().After;
        Assert.Equal((5, 0), (after.Hp!.Value, after.DeathSaves.Failures));
        Assert.Equal(storedProne ? [down.Conditions[1]] : [], after.Conditions.ToList());
        Assert.Equal(!storedProne, plan.Summary.Contains("Ended with the fight: prone (The fishman monk)."));
    }

    [Fact]
    public void DownAtEnd_ASheetThatAlreadyHasUnconscious_AFightThatChangedNothing_WritesNothing()
    {
        // CR08: a dying sheet whose Prone the author removed (or that never had one) is already the down state the sheet
        // keeps; a fight in which nothing happened writes no batch for it.
        var down = SheetsB()["e-monk"] with
        {
            Hp = 0,
            DeathSaves = new SheetDeathSaves(0, 1, false),
            Conditions = [new SheetCondition("unconscious", null, "until_removed", null, null)],
        };
        var sheets = SheetsB();
        sheets["e-monk"] = down;
        var s = Add(Encounter(E2024, "Station"), PartyB()[0], PartyB()[1], PartyB()[2] with { Sheet = down }).Next;

        var plan = CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets });

        Assert.True(plan.IsEmpty);
        Assert.DoesNotContain(plan.Summary, l => l.Contains("Unconscious", StringComparison.Ordinal) || l.Contains("prone", StringComparison.Ordinal));
    }

    [Fact]
    public void DownAtEnd_ASheetAtZeroWithoutUnconscious_GetsIt_ACorrectionTheSummarySays()
    {
        // CR08: update {hp: 0} stores no Unconscious; the fight's write-back adds it (one representation of "down at 0
        // HP") and says it was a correction, not something the fight did. Still no Prone (UR03).
        var down = SheetsB()["e-monk"] with { Hp = 0, DeathSaves = new SheetDeathSaves(0, 1, false) };
        var sheets = SheetsB();
        sheets["e-monk"] = down;
        var s = Add(Encounter(E2024, "Station"), PartyB()[2] with { Sheet = down }).Next;

        var plan = CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets });

        var monk = Assert.Single(plan.WriteBacks);
        Assert.Equal([SheetColumns.Conditions], monk.Diff.Columns.Keys);
        Assert.Equal("""[{"name":"unconscious","duration":"until_removed"}]""", SheetJson.WriteConditions(monk.After.Conditions));
        Assert.Contains("The fishman monk was at 0 HP on its sheet without Unconscious: the sheet gets it now (a heal ends it).", plan.Summary);
    }

    [Fact]
    public void KnockedOut2024_StillOutAtTheEnd_WritesUnconsciousNotedKnockedOut_TheSummarySaysWhatEndsIt()
    {
        // CR06: SRD 5.2.1 Knocking Out a Creature: it "remains Unconscious until it regains any Hit Points" (or first aid);
        // "ended with the fight" left it conscious at 1 HP with nothing regained. F2R08: the summary quotes that text (the
        // server's rules_get "2024/rule/knocking-out-a-creature"), first aid included, and says the sheet's heal and rest
        // as the sheet's convenience, never "or finishes a Short Rest", which the SRD does not say.
        var s = Step(FightB(), new DamageOp(["fishman-monk"]) { Amount = 70, KnockOut = true, Source = "aboleth" }).Next;
        Assert.True(s.Named("The fishman monk").KnockedOut);

        var plan = Plan(s, new EndOptions(Xp: 0));

        var monk = plan.WriteBacks.Single(w => w.EntityId == "e-monk").After;
        Assert.Equal(1, monk.Hp);
        Assert.Equal("""[{"name":"unconscious","duration":"until_removed","note":"knocked out"}]""", SheetJson.WriteConditions(monk.Conditions));
        Assert.Contains(
            "The fishman monk is still knocked out: the sheet keeps Unconscious (knocked out). By the SRD 5.2.1 rules it remains Unconscious until it " +
            "regains any Hit Points or until someone uses an action to administer first aid to it, which requires a successful DC 10 Wisdom (Medicine) " +
            "check (then remove it with campaign_character condition); on the sheet, a heal or a rest also ends it.",
            plan.Summary);
        Assert.DoesNotContain(plan.Summary, x => x.Contains("Short Rest", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Summary, x => x.StartsWith("Ended with the fight", StringComparison.Ordinal));

        // Healed before the end, it wakes, and the sheet is written conscious (the Prone it kept ends with the fight).
        var woke = Plan(Step(s, new HealOp(["fishman-monk"]) { Amount = 4 }).Next, new EndOptions(Xp: 0)).WriteBacks.Single(w => w.EntityId == "e-monk").After;
        Assert.Equal(5, woke.Hp);
        Assert.Empty(woke.Conditions);
    }

    [Fact]
    public void KnockedOut2024_WrittenBack_TheNextFightSeedsTheKnockOut_AHealWakesIt()
    {
        // CR06: the sheet's knocked-out Unconscious comes back as the tracker's knock-out, so a heal in the next fight
        // wakes it, as the rules say, and that fight then writes it back conscious.
        var sheets = SheetsB();
        var s = Step(FightB(), new DamageOp(["fishman-monk"]) { Amount = 70, KnockOut = true, Source = "aboleth" }).Next;
        var written = Plan(s, new EndOptions(Xp: 0), sheets).WriteBacks.Single(w => w.EntityId == "e-monk").After;
        sheets["e-monk"] = written;

        var next = Add(Encounter(E2024, "Second"), PartyB()[2] with { Sheet = written }, new AddEntry { Name = "Orc", Hp = HpChoice.Of(15) }).Next;
        var monk = next.Named("The fishman monk");
        Assert.True(monk.KnockedOut);
        Assert.Equal(["unconscious", "prone"], monk.Conditions.Select(c => c.Name));
        Assert.True(CombatEnd.Plan(next, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets }).IsEmpty);

        next = Step(next, new InitiativeOp { Rolls = [new("Orc", Total: 12), new("fishman-monk", Total: 5)] }).Next;
        var healed = Step(next, new HealOp(["fishman-monk"]) { Amount = 3 }).Next;
        Assert.False(healed.Named("The fishman monk").Has("unconscious"));
        var after = CombatEnd.Plan(healed, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets }).WriteBacks.Single().After;
        Assert.Equal(4, after.Hp);
        Assert.Empty(after.Conditions);
    }

    [Fact]
    public void DownAtEnd_TheSheetsOwnUnconsciousAndProne_WrittenBackAsStored_AStableOneToo()
    {
        // A sheet that came in down (dropped out of combat) and is still down writes nothing for its conditions.
        var down = SheetsB()["e-monk"] with
        {
            Hp = 0,
            DeathSaves = new SheetDeathSaves(1, 0, false),
            Conditions = [new SheetCondition("Unconscious", null, null, null, "dropped by a trap"), new SheetCondition("prone", null, "until_removed", null, null)],
        };
        var sheets = SheetsB();
        sheets["e-monk"] = down;
        var s = Add(Encounter(E2024, "Station"), PartyB()[0], PartyB()[1], PartyB()[2] with { Sheet = down }).Next;
        Assert.True(CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets }).IsEmpty);

        var stable = Step(Step(s, new InitiativeOp { Rolls = [new("fishman-monk", Total: 3), new("bjorn-mountainfell", Total: 9), new("dragon-slayer", Total: 8)] }).Next,
            new DeathSaveOp(["fishman-monk"]) { Stable = true }).Next;
        var written = CombatEnd.Plan(stable, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets }).WriteBacks.Single();
        Assert.Equal(down.Conditions.ToList(), written.After.Conditions.ToList());
        Assert.True(written.After.DeathSaves.Stable);
    }

    [Fact]
    public void Died_ASheetAlreadyDeadWhenItJoined_GetsANote_NoDeathLineAndNoProposal()
    {
        // CV02: add_party seeds a dead sheet (the roster checks the entity, not the sheet); it did not die in this fight.
        var dead = SheetsB()["e-slayer"] with { Hp = 0, DeathSaves = new SheetDeathSaves(0, 3, false) };
        var sheets = SheetsB();
        sheets["e-slayer"] = dead;
        var s = Add(Encounter(E2024, "Station"), PartyB()[0], PartyB()[1] with { Sheet = dead }, PartyB()[2]).Next;
        s = Add(s, Monster(Block(E2024, "aboleth"), hp: HpChoice.Avg)).Next;
        s = Step(s, new DamageOp(["bjorn-mountainfell"]) { Amount = 5 }).Next;

        var plan = CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets });

        Assert.Empty(plan.Proposals);
        Assert.DoesNotContain(plan.Summary, l => l.Contains("died", StringComparison.Ordinal));
        Assert.Contains("The amethyst Dragon Slayer was already dead when it joined the fight: no death to record here.", plan.Summary);
        Assert.DoesNotContain(plan.WriteBacks, w => w.EntityId == "e-slayer");
    }

    [Fact]
    public void Drift_AHoldingUsedInTheFight_TheRefusalSaysUsedNInTheFight()
    {
        // U13: "1 used in the fight when it joined" read as nonsense.
        var s = Step(FightB(), new UseOp(["bjorn-mountainfell"]) { Item = "Potion of Healing", Holdings = HoldingsB(), Amount = 2 }).Next;

        var missing = Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), holdings: [])).Message;
        var short1 = Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), holdings: [new CombatItem("h-potion", "e-bjorn", "Potion of Healing", 1)])).Message;

        Assert.EndsWith(": Björn Mountainfell holding Potion of Healing: used 2 in the fight; the holding no longer exists.", missing, StringComparison.Ordinal);
        Assert.EndsWith(": Björn Mountainfell holding Potion of Healing: used 2 in the fight; only 1 is left.", short1, StringComparison.Ordinal);
    }

    [Fact]
    public void TempHpAndMaxHpReduction_Persist()
    {
        var s = Step(FightB(), new HealOp(["bjorn-mountainfell"]) { Amount = 6, Temp = true }).Next;
        s = Step(s, new SetOp([new SetEntry("bjorn-mountainfell") { MaxHpReduction = 10 }])).Next;
        var bjorn = Plan(s, new EndOptions(Xp: 0)).WriteBacks.Single(w => w.EntityId == "e-bjorn");
        Assert.Equal((6, 10, 75), (bjorn.After.TempHp, bjorn.After.MaxHpReduction, bjorn.After.Hp!.Value));
    }

    [Fact]
    public void Rounds_MoreThanTenLeftPersistWithRemainingAndNote_TenOrFewerEnd()
    {
        var s = FightB(); // the monk's turn, round 1
        s = Step(s, new ConditionOp(["dragon-slayer"]) { Add = ["Haste"], Duration = "11 rounds", Effect = new EffectInput(Ac: 2) }).Next;
        s = Step(s, new ConditionOp(["dragon-slayer"]) { Add = ["Bless"], Duration = "10 rounds" }).Next;
        var slayer = Plan(s, new EndOptions(Xp: 0)).WriteBacks.Single(w => w.EntityId == "e-slayer");
        Assert.Equal(
            """[{"name":"Haste","source":"The fishman monk","effect":{"ac":2},"duration":"rounds","remaining_rounds":11,"note":"from Station, round 1"}]""",
            SheetJson.WriteConditions(slayer.After.Conditions));
    }

    [Fact]
    public void RoundsLeft_TheAnchorHasActed_OneLess_BeforeInitiative_CountsFromRound1()
    {
        var s = FightB(); // monk 19, aboleth 13, slayer 11, björn 8: the monk acts
        var monk = s.Named("The fishman monk").Id;
        var bjorn = s.Named("Björn Mountainfell").Id;
        var aboleth = s.Named("Aboleth").Id;
        Assert.Equal(100, CombatEnd.RoundsLeft(s, new ConditionExpiry(101, CombatValues.ExpiryPoints.Start, monk)));
        var slayersTurn = s.Next().Next();
        Assert.Equal(99, CombatEnd.RoundsLeft(slayersTurn, new ConditionExpiry(101, CombatValues.ExpiryPoints.Start, aboleth)));
        Assert.Equal(100, CombatEnd.RoundsLeft(slayersTurn, new ConditionExpiry(101, CombatValues.ExpiryPoints.Start, bjorn)));
        Assert.Equal(10, CombatEnd.RoundsLeft(Encounter(), new ConditionExpiry(11, CombatValues.ExpiryPoints.Start, null)));
        Assert.True(CombatEnd.AnchorActed(slayersTurn, monk));
        Assert.False(CombatEnd.AnchorActed(slayersTurn, slayersTurn.TurnCombatantId!));
    }

    [Fact]
    public void SheetConditions_ThatCameInUnchanged_AreNotRewritten()
    {
        var sheets = SheetsB();
        sheets["e-monk"] = sheets["e-monk"] with
        {
            Conditions = [new SheetCondition("cursed (Mucus Cloud)", "Aboleth", "until_removed", null, "from The dark station (fixture), round 1")],
        };
        var s = Add(Encounter(E2024, "Station"), Pc("e-monk", "The fishman monk", "character:fishman-monk", sheets["e-monk"])).Next;
        s = Add(s, Monster(Block(E2024, "aboleth"), hp: HpChoice.Avg)).Next;
        s = Step(s, new DamageOp(["fishman-monk"]) { Amount = 3 }).Next;
        var monk = Plan(s, new EndOptions(Xp: 0), sheets).WriteBacks.Single();
        Assert.Equal([SheetColumns.Hp], monk.Diff.Columns.Keys);
    }

    [Fact]
    public void D17_SheetWithoutMaxHp_SetGaveIt_WritesMaxHpAndHp_OtherwiseNoHp()
    {
        var sheet = Sheet("e-vars", """{ "classes": [{ "class": "ranger", "level": 6 }, { "class": "rogue", "level": 6 }], "xp": 100 }""", E2014);
        var sheets = new Dictionary<string, CharacterSheet> { ["e-vars"] = sheet };
        var s = Add(Encounter(E2014, "Crypt"), Pc("e-vars", "Vars Nocturne", "character:vars", sheet)).Next;
        Assert.True(Plan(Step(s, new DamageOp(["vars"]) { Amount = 5 }).Next, new EndOptions(Xp: 0), sheets, []).IsEmpty);
        var set = Step(s, new SetOp([new SetEntry("character:vars") { Hp = 80 }])).Next;
        set = Step(set, new DamageOp(["vars"]) { Amount = 5 }).Next;
        var vars = Plan(set, new EndOptions(Xp: 0), sheets, []).WriteBacks.Single();
        Assert.Equal((80, 75), (vars.After.MaxHp!.Value, vars.After.Hp!.Value));
        Assert.Equal([SheetColumns.MaxHp, SheetColumns.Hp], vars.Diff.Columns.Keys);
    }

    /// <summary>
    /// M02 (contract §6.2, D17): a sheet with hp but no max_hp (update takes one alone) joins with unknown hit points;
    /// when set gives it some, end writes max_hp WITH hp, as for a sheet with neither. Written alone, hp left the sheet
    /// "HP 25/—" with no maximum.
    /// </summary>
    [Theory]
    [InlineData(null, "Kaz: the sheet had no hit points; the fight gave it 25, written as max_hp with hp.", "Kaz: hp none → 25.")]
    [InlineData(30, "Kaz: the sheet had no max_hp; the fight gave it 25, written as max_hp with hp.", "Kaz: hp 30 → 25.")]
    public void D17_SheetWithoutMaxHp_HpOrNot_SetGaveIt_WritesMaxHpWithHp(int? hp, string maxNote, string hpNote)
    {
        var sheet = Sheet("e-kaz", """{ "level": 3 }""", E2024) with { Hp = hp };
        var sheets = new Dictionary<string, CharacterSheet> { ["e-kaz"] = sheet };
        var s = Add(Encounter(E2024, "Reef"), Pc("e-kaz", "Kaz", "character:kaz", sheet)).Next;
        Assert.False(s.Named("Kaz").HpKnown);
        s = Step(s, new SetOp([new SetEntry("character:kaz") { Hp = 25 }])).Next;

        var kaz = Plan(s, new EndOptions(Xp: 0), sheets, []).WriteBacks.Single();

        Assert.Equal((25, 25), (kaz.After.MaxHp!.Value, kaz.After.Hp!.Value));
        Assert.Equal([SheetColumns.MaxHp, SheetColumns.Hp], kaz.Diff.Columns.Keys);
        Assert.Equal([maxNote, hpNote], kaz.Notes.Take(2));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Drift (D6)

    [Fact]
    public void Drift_TheSheetsHpChangedMeanwhile_RefusedNamingBothValues_ForceWritesOver()
    {
        var s = Step(FightB(), new DamageOp(["bjorn-mountainfell"]) { Amount = 5 }).Next;
        var sheets = SheetsB();
        sheets["e-bjorn"] = sheets["e-bjorn"] with { Hp = 70 };
        var ex = Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), sheets));
        Assert.Contains("Björn Mountainfell hp: 85 when it joined, 70 now", ex.Message, StringComparison.Ordinal);
        var forced = Plan(s, new EndOptions(Xp: 0, Force: true), sheets);
        Assert.Equal(80, forced.WriteBacks.Single(w => w.EntityId == "e-bjorn").After.Hp);
        Assert.Single(forced.Overwritten);
    }

    [Fact]
    public void Drift_AFieldTheFightDidNotChange_IsNotDrift()
    {
        var sheets = SheetsB();
        sheets["e-bjorn"] = sheets["e-bjorn"] with { Hp = 70 };
        var plan = Plan(Step(FightB(), new DamageOp(["fishman-monk"]) { Amount = 5 }).Next, new EndOptions(Xp: 0), sheets);
        Assert.Single(plan.WriteBacks);
        Assert.Equal("e-monk", plan.WriteBacks[0].EntityId);
    }

    [Fact]
    public void Drift_AResourcesUsedChangedMeanwhile_Refused_ForceWritesTheKey()
    {
        var s = Step(FightB(), new UseOp(["fishman-monk"]) { Resource = "Focus", Amount = 2 }).Next;
        var sheets = SheetsB();
        var monk = sheets["e-monk"];
        sheets["e-monk"] = monk with { Resources = SheetMaps.With(monk.Resources, "focus", monk.Resources["focus"] with { Used = 1 }) };
        var ex = Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), sheets));
        Assert.Contains("resources focus: 0 used when it joined, 1 used now", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, Plan(s, new EndOptions(Xp: 0, Force: true), sheets).WriteBacks.Single().After.Resources["focus"].Used);
    }

    [Fact]
    public void Drift_ASheetThatNoLongerExists_Refused_ForceSkipsIt()
    {
        var s = Step(FightB(), new DamageOp(["bjorn-mountainfell"]) { Amount = 5 }).Next;
        var sheets = SheetsB();
        sheets.Remove("e-bjorn");
        var ex = Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), sheets));
        Assert.Contains("Björn Mountainfell sheet", ex.Message, StringComparison.Ordinal);
        var forced = Plan(s, new EndOptions(Xp: 0, Force: true), sheets);
        Assert.DoesNotContain(forced.WriteBacks, w => w.EntityId == "e-bjorn");
    }

    [Fact]
    public void Items_ConsumedAsCurrentMinusUsed_AMissingOrShortHoldingIsDrift_ForceSkipsIt()
    {
        var s = Step(FightB(), new UseOp(["bjorn-mountainfell"]) { Item = "Potion of Healing", Holdings = HoldingsB() }).Next;
        Assert.Equal(new ItemConsumption("h-potion", "e-bjorn", "Potion of Healing", 3, 2, 1),
            Plan(s, new EndOptions(Xp: 0), holdings: [new CombatItem("h-potion", "e-bjorn", "Potion of Healing", 3)]).Items.Single());
        Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), holdings: []));
        Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), holdings: [new CombatItem("h-potion", "e-bjorn", "Potion of Healing", 0)]));
        Assert.Empty(Plan(s, new EndOptions(Xp: 0, Force: true), holdings: []).Items);
        Assert.Equal(0, Plan(s, new EndOptions(Xp: 0), holdings: [new CombatItem("h-potion", "e-bjorn", "Potion of Healing", 1)]).Items.Single().After);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Proposals

    [Fact]
    public void Proposals_ADefeatedLinkedNpcThatDied_GetsOne_OneStillAliveIsANote()
    {
        var s = Add(FightB(), new AddEntry
        {
            Monster = Block(E2024, "goblin-warrior"), Hp = HpChoice.Avg, EntityId = "e-gob", EntityName = "Snik", EntityHandle = "character:snik", EntitySubtype = "npc",
        }).Next;
        var dead = Plan(Step(s, new DamageOp(["goblin-warrior"]) { Amount = 99 }).Next, new EndOptions(Xp: 0));
        Assert.Equal("character:snik", Assert.Single(dead.Proposals).EntityHandle);
        var knocked = Plan(Step(s, new DamageOp(["goblin-warrior"]) { Amount = 99, KnockOut = true }).Next, new EndOptions(Xp: 0));
        Assert.Empty(knocked.Proposals);
        Assert.Contains(knocked.Summary, x => x.Contains("defeated but not killed", StringComparison.Ordinal));
    }


    [Fact]
    public void Proposals_ALinkedMonsterStillHeldAtZeroByATrait_IsDefeated_AndProposedWithTheTrait()
    {
        var torch = Torch();
        var s = Add(Encounter(E2014, "Crypt"), Pc("e-torch", "Lieutenant James Torch", "character:torch", torch), new AddEntry
        {
            Monster = Block(E2014, "zombie"), Hp = HpChoice.Avg, EntityId = "e-zed", EntityName = "Zed", EntityHandle = "character:zed", EntitySubtype = "npc",
        }).Next;
        s = Step(s, new DamageOp(["zombie"]) { Amount = 30, DamageType = "slashing" }).Next;
        var zombie = s.Named("Zombie");
        Assert.Equal((0, false, true), (zombie.Hp!.Value, zombie.Dead, zombie.Defeated));
        Assert.Equal(["Undead Fortitude"], CombatTracker.HeldAtZeroBy(zombie).Select(t => t.Name));

        var plan = CombatEnd.Plan(s, new EndOptions(), new EndInputs { Sheets = new Dictionary<string, CharacterSheet> { ["e-torch"] = torch } });
        var proposal = Assert.Single(plan.Proposals);
        Assert.Equal(("character:zed", "dead"), (proposal.EntityHandle, proposal.Status));
        Assert.Contains("held by Undead Fortitude", proposal.Text, StringComparison.Ordinal);
        Assert.Contains("\"ref\": \"character:zed\", \"status\": \"dead\"}], \"dry_run\": true", proposal.Call, StringComparison.Ordinal);
        Assert.Equal(Block(E2014, "zombie").Xp, plan.Xp.Worth);
        Assert.Contains(plan.Summary, x => x.Contains("still held there by Undead Fortitude", StringComparison.Ordinal) && x.Contains("counted as defeated", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Summary, x => x.Contains("defeated but not killed", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // What persists, exactly

    [Fact]
    public void Persisted_TheNoteIsFromTheEncounterAndRound_ThenTheConditionsOwnNote_TheSourceItsSafeName()
    {
        var s = FightB(); // the monk's turn, round 1
        s = Step(s, new ConditionOp(["dragon-slayer"]) { Add = ["Haste"], Duration = "11 rounds" }).Next;
        var slayer = s.Named("The amethyst Dragon Slayer");
        s = s with
        {
            Combatants = s.Combatants.Select(c => c.Id == slayer.Id ? c with { Conditions = c.Conditions.Select(x => x with { Note = "Polymorph backup" }).ToList() } : c).ToList(),
        };
        var written = Plan(s, new EndOptions(Xp: 0)).WriteBacks.Single(w => w.EntityId == "e-slayer");
        Assert.Equal(
            """[{"name":"Haste","source":"The fishman monk","duration":"rounds","remaining_rounds":11,"note":"from Station, round 1; Polymorph backup"}]""",
            SheetJson.WriteConditions(written.After.Conditions));

        // A source with no party-safe name is "a combatant", never its id.
        var unnamed = CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = SheetsB(), Holdings = HoldingsB() });
        Assert.Equal("a combatant", unnamed.WriteBacks.Single(w => w.EntityId == "e-slayer").After.Conditions.Single().Source);
    }

    [Fact]
    public void SheetConditions_StoredWithoutADurationOrSpelledOtherwise_AreNotRewritten()
    {
        var sheet = Sheet("e-p", """{ "classes": [{ "class": "fighter", "level": 5 }], "max_hp": 44, "ac": 18 }""", E2024);
        sheet = sheet with { Conditions = [new SheetCondition("cursed", "a hag", null, null, "old curse"), new SheetCondition("Poisoned", null, null, null, null)] };
        var sheets = new Dictionary<string, CharacterSheet> { ["e-p"] = sheet };
        var s = Add(Encounter(E2024, "Probe"), Pc("e-p", "P", "character:p", sheet), Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg)).Next;
        Assert.Equal(["cursed", "poisoned"], s.Named("P").Conditions.Select(c => c.Name));
        Assert.True(CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets }).IsEmpty);

        // A real change rewrites the column, and the two that came in are written back as the sheet stored them.
        s = Step(s, new ConditionOp(["p"]) { Add = ["mummy rot"], Source = "goblin-warrior", Duration = "until removed" }).Next;
        var after = CombatEnd.Plan(s, new EndOptions(Xp: 0), new EndInputs { Sheets = sheets, SafeNames = s.Combatants.ToDictionary(c => c.Id, c => c.Name) })
            .WriteBacks.Single().After.Conditions;
        Assert.Equal(sheet.Conditions.ToList(), after.Take(2).ToList());
        Assert.Equal(("mummy rot", "until_removed"), (after[2].Name, after[2].Duration));
    }

    [Fact]
    public void Drift_ASpellSlotsUsedChangedMeanwhile_Refused_ForceWritesTheKey()
    {
        var belmakor = Belmakor();
        var s = Add(Encounter(E2014, "Crypt"), Pc("e-belmakor", "Belmakor Silverwind", "character:belmakor", belmakor)).Next;
        s = Step(s, new ConcentrationOp(["belmakor"]) { Spell = "Circle of Power", SlotLevel = 5 }).Next;
        var sheets = new Dictionary<string, CharacterSheet>
        {
            ["e-belmakor"] = belmakor with { SpellSlots = SheetMaps.With(belmakor.SpellSlots, "5", belmakor.SpellSlots["5"] with { Used = 2 }) },
        };
        var ex = Assert.Throws<DndInputException>(() => Plan(s, new EndOptions(Xp: 0), sheets, []));
        Assert.Contains("Belmakor Silverwind spell_slots 5: 0 used when it joined, 2 used now", ex.Message, StringComparison.Ordinal);
        var forced = Plan(s, new EndOptions(Xp: 0, Force: true), sheets, []);
        Assert.Equal(1, forced.WriteBacks.Single().After.SpellSlots["5"].Used);
        Assert.Equal(("spell_slots", "5"), (Assert.Single(forced.Overwritten).Field, forced.Overwritten[0].Key));
    }

    [Fact]
    public void Xp_DefeatedAlliesAndNeutralsAreNotWorthAnything_OnlyEnemies()
    {
        var s = Add(FightB(), Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg, name: "Pet") with { Side = "ally" },
            Monster(Block(E2024, "goblin-warrior"), hp: HpChoice.Avg, name: "Bystander") with { Side = "neutral" }).Next;
        s = Step(s, new DamageOp(["Pet", "Bystander"]) { Amount = 99 }).Next;
        Assert.True(s.Named("Bystander").Defeated);
        Assert.Equal(5_900, Plan(KillAboleth(s)).Xp.Worth);
    }
}
