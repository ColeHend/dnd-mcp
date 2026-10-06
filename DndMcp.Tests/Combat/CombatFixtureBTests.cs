using System.Text.Json.Nodes;
using DndMcp.Domain.Campaign;
using DndMcp.Domain.Characters;
using DndMcp.Domain.Combat;
using DndMcp.Domain.Rules;
using Xunit;
using static DndMcp.Tests.Combat.CombatKit;
using K = DndMcp.Domain.Combat.CombatValues.ReminderKinds;
using L = DndMcp.Domain.Campaign.CampaignValues.CombatLogKinds;
using P = DndMcp.Domain.Combat.CombatValues.Purposes;

namespace DndMcp.Tests.Combat;

/// <summary>
/// Invariant: fixture B (FIX §4, the One Piece 2024 DM campaign, as amended by contract §16) runs as pure state
/// transitions: the in-lair Aboleth (Legendary Resistance and actions 4/4, no lair-action reminder in 2024), Björn's
/// exhaustion as a column and its −2 on initiative (B4: 8 + 2 − 2 = 8), grapples until escape that end with the grappler,
/// Rage's "all but psychic" resistance (B13), Bloodied at each crossing, the three server rolls (B16 heal 1d10 = 5, B20
/// critical "4d6+5" = 19 and two failures at 0, B22 potion 2d4+2 = 7), and the §4.3 write-back: Björn 51 HP, Rage 1
/// used, exhaustion 1, XP 36,400; the monk 7 HP, Focus 4 used, the curse kept with source "Aboleth"; the Dragon Slayer
/// 56; the potion 2 → 1; 2,400 XP each; the Nester's status proposal.
/// </summary>
public sealed class CombatFixtureBTests
{
    private static readonly Lazy<Script> Played = new(() => new Script());

    private sealed class Script
    {
        public Dictionary<string, CombatStepResult> At { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyList<RollNeed>> Needs { get; } = new(StringComparer.Ordinal);

        public List<CombatChange> Log { get; } = [];

        public EncounterState Final { get; }

        public EndPlan End { get; }

        public Script()
        {
            var s = Encounter(E2024, "The dark station (fixture)", lair: true);
            s = Do("B1", CombatKit.Add(s, [.. PartyB()]));
            s = Do("B2", CombatKit.Add(s, new AddEntry
            {
                Monster = Block(E2024, "aboleth"),
                Hp = HpChoice.Avg,
                Side = "enemy",
                EntityId = "e-nester",
                EntityName = "The Nester",
                EntityHandle = "character:the-nester",
                EntitySubtype = "npc",
            }));
            s = Do("B3", Step(s, new ConditionOp(["bjorn-mountainfell"]) { Add = ["exhaustion"], Level = 1 }));
            s = Do("B4", Step(s, new InitiativeOp
            {
                Rolls = [new("fishman-monk", Face: 16), new("dragon-slayer", Face: 9), new("bjorn-mountainfell", Face: 8), new("aboleth", Total: 13)],
            }));
            s = Do("B5-focus", Step(s, new UseOp(["fishman-monk"]) { Resource = "Focus", Amount = 2 }));
            s = Do("B5", Step(s, new DamageOp(["aboleth"]) { Amount = 18, DamageType = "bludgeoning" }));
            s = Do("B5-resistance", Step(s, new LegendaryOp("aboleth") { Resistance = true }));
            s = Do("B6-legendary", Step(s, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
            s = Do("B6", Step(s, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B7", Step(s, new NextOp()));
            s = Do("B8-monk", Step(s, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B8-grapple-monk", Step(s, new ConditionOp(["fishman-monk"]) { Add = ["grappled"], Source = "aboleth", Dc = 14 }));
            s = Do("B8-bjorn", Step(s, new DamageOp(["bjorn-mountainfell"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B8-grapple-bjorn", Step(s, new ConditionOp(["bjorn-mountainfell"]) { Add = ["grappled"], Source = "aboleth", Dc = 14 }));
            s = Do("B8", Step(s, new DamageOp(["fishman-monk"]) { Amount = 10, DamageType = "psychic", Source = "aboleth" }));
            s = Do("B9", Step(s, new ConditionOp(["fishman-monk"]) { Add = ["cursed (Mucus Cloud)"], Source = "aboleth", Duration = "until removed" }));
            s = Do("B10-next", Step(s, new NextOp()));
            s = Do("B10", Step(s, new DamageOp(["aboleth"]) { Parts = [new(22, null, "bludgeoning"), new(4, null, "psychic")] }));
            s = Do("B11-legendary", Step(s, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
            s = Do("B11", Step(s, new DamageOp(["dragon-slayer"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B12-next", Step(s, new NextOp()));
            s = Do("B12-rage", Step(s, new ConditionOp(["bjorn-mountainfell"]) { Add = ["Rage"], Effect = new EffectInput(Resist: ["all"], Except: ["psychic"]), Resource = "Rage" }));
            s = Do("B12", Step(s, new DamageOp(["aboleth"]) { Amount = 24, DamageType = "slashing" }));
            s = Do("B13-legendary", Step(s, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
            s = Do("B13", Step(s, new DamageOp(["bjorn-mountainfell"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B14", Step(s, new NextOp()));
            s = Do("B15-focus", Step(s, new UseOp(["fishman-monk"]) { Resource = "Focus", Amount = 2 }));
            s = Do("B15", Step(s, new DamageOp(["aboleth"]) { Amount = 20, DamageType = "bludgeoning" }));
            s = Do("B15-resistance", Step(s, new LegendaryOp("aboleth") { Resistance = true }));
            s = Do("B16-legendary", Step(s, new LegendaryOp("aboleth") { Amount = 1, Name = "Psychic Drain" }));
            s = Do("B16-damage", Step(s, new DamageOp(["fishman-monk"]) { Amount = 10, DamageType = "psychic", Source = "aboleth" }));
            s = Do("B16", Step(s, new HealOp(["aboleth"]) { Dice = "1d10" }, 5));
            s = Do("B17", Step(s, new NextOp()));
            s = Do("B18-first", Step(s, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B18-second", Step(s, new DamageOp(["fishman-monk"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B18", Step(s, new DamageOp(["bjorn-mountainfell"]) { Amount = 10, DamageType = "psychic", Source = "aboleth" }));
            s = Do("B19-next", Step(s, new NextOp()));
            s = Do("B19", Step(s, new DamageOp(["aboleth"]) { Parts = [new(24, null, "bludgeoning"), new(4, null, "psychic")] }));
            s = Do("B20-legendary", Step(s, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
            s = Do("B20", Step(s, new DamageOp(["fishman-monk"]) { Dice = "2d6+5", DamageType = "bludgeoning", Critical = true, Source = "aboleth" }, 4, 5, 2, 3));
            s = Do("B21", Step(s, new NextOp()));
            s = Do("B22", Step(s, new HealOp(["fishman-monk"]) { Dice = "2d4+2", Source = "bjorn-mountainfell", Item = "Potion of Healing", Holdings = HoldingsB() }, 3, 2));
            s = Do("B23", Step(s, new DamageOp(["aboleth"]) { Amount = 26, DamageType = "slashing" }));
            s = Do("B24-legendary", Step(s, new LegendaryOp("aboleth") { Amount = 1, Name = "Lash" }));
            s = Do("B24", Step(s, new DamageOp(["bjorn-mountainfell"]) { Amount = 12, DamageType = "bludgeoning", Source = "aboleth" }));
            s = Do("B25", Step(s, new NextOp()));
            s = Do("B26", Step(s, new DamageOp(["aboleth"]) { Amount = 18, DamageType = "bludgeoning" }));
            Final = s;
            End = CombatEnd.Plan(s, new EndOptions(), new EndInputs
            {
                Sheets = PartyB().ToDictionary(p => p.EntityId!, p => p.Sheet!),
                Holdings = HoldingsB(),
                SafeNames = s.Combatants.ToDictionary(c => c.Id, c => c.Name),
                CampaignSlug = "one-piece",
            });
        }

        private EncounterState Do(string step, CombatStepResult result)
        {
            At[step] = result;
            Log.AddRange(result.Changes);
            return result.Next;
        }
    }

    private static Script B => Played.Value;

    private static CombatantState In(string step, string name) => B.At[step].Next.Named(name);

    private const string Monk = "The fishman monk";
    private const string Bjorn = "Björn Mountainfell";
    private const string Slayer = "The amethyst Dragon Slayer";

    [Fact]
    public void B1_PartyFromSheets_85_68_59()
    {
        var s = B.At["B1"].Next;
        Assert.Equal((85, 85), (s.Named(Bjorn).Hp, s.Named(Bjorn).MaxHp));
        Assert.Equal(68, s.Named(Slayer).Hp);
        Assert.Equal(59, s.Named(Monk).Hp);
        Assert.Empty(B.At["B1"].Of(K.NoHp));
    }

    [Fact]
    public void B2_InLairAboleth_LegendaryAndResistance4Of4_InitiativePlus3_Linked()
    {
        var aboleth = In("B2", "Aboleth");
        Assert.Equal((150, 17, 3), (aboleth.Hp, aboleth.Ac, aboleth.InitBonus));
        Assert.Equal(new LegendaryState(4, 0, 4, 0), aboleth.Legendary);
        Assert.Equal("e-nester", aboleth.EntityId);
        Assert.Equal(CampaignValues.CombatSides.Enemy, aboleth.Side);
        Assert.False(aboleth.IsSheetSeeded);
        Assert.Equal("aboleth", aboleth.Address);
    }

    [Fact]
    public void B_NoLairActionReminderIn2024()
    {
        Assert.DoesNotContain(B.At.Values, r => r.Of(K.LairAction).Count > 0);
    }

    [Fact]
    public void B3_ExhaustionIsAColumn_RemindedAtRoundZero()
    {
        var bjorn = In("B3", Bjorn);
        Assert.Equal(1, bjorn.Exhaustion);
        Assert.Empty(bjorn.Conditions);
        Assert.True(B.At["B3"].Says(K.ExhaustionEffects, Bjorn, "Exhaustion 1: D20 Tests −2; Speed −5 ft"));
        Assert.Equal(0, B.At["B3"].Next.Round);
    }

    [Fact]
    public void B4_Initiative_BjornsExhaustionTakesTwo_OrderMonkAbolethSlayerBjorn()
    {
        var s = B.At["B4"].Next;
        Assert.Equal(19, s.Named(Monk).Initiative);
        Assert.Equal(11, s.Named(Slayer).Initiative);
        Assert.Equal(8, s.Named(Bjorn).Initiative);
        Assert.Equal(13, s.Named("Aboleth").Initiative);
        Assert.Equal([Monk, "Aboleth", Slayer, Bjorn], s.Order.Select(c => c.Name));
        Assert.Equal(Monk, s.TurnHolder!.Name);
    }

    [Fact]
    public void B5_FocusSpent_AbolethTo132_LegendaryResistanceThreeLeft()
    {
        Assert.Equal(2, In("B5-focus", Monk).Resources["focus"].Used);
        Assert.Equal(132, In("B5", "Aboleth").Hp);
        Assert.Equal(3, In("B5-resistance", "Aboleth").Legendary!.ResistanceLeft);
        Assert.Equal(4, In("B5-resistance", "Aboleth").Legendary!.ActionsLeft);
    }

    [Fact]
    public void B6_LashAfterTheMonksTurn_ThreeLeft_MonkTo47()
    {
        Assert.Equal(3, In("B6-legendary", "Aboleth").Legendary!.ActionsLeft);
        Assert.Equal(47, In("B6", Monk).Hp);
    }

    [Fact]
    public void B7_AbolethsTurn_LegendaryActionsReset4Of4()
    {
        Assert.Equal(4, In("B7", "Aboleth").Legendary!.ActionsLeft);
        Assert.True(B.At["B7"].Says(K.LegendaryReset, "Aboleth", "reset: 4/4"));
    }

    [Fact]
    public void B8_GrappledUntilEscapeDc14_MonkBloodiedAt25()
    {
        var grapple = In("B8-grapple-monk", Monk).Conditions.Single();
        Assert.Equal(("grappled", CombatValues.Durations.UntilEscape, 14), (grapple.Name, grapple.Duration, grapple.EscapeDc));
        Assert.Equal(In("B8", "Aboleth").Id, grapple.Source);
        Assert.Equal(73, In("B8-bjorn", Bjorn).Hp);
        Assert.Equal(25, In("B8", Monk).Hp);
        Assert.True(B.At["B8"].Says(K.Bloodied, Monk, "Bloodied"));
        Assert.Empty(B.At["B8-monk"].Of(K.Bloodied));
    }

    [Fact]
    public void B9_MucusCloudCurse_UntilRemovedFromTheAboleth()
    {
        var curse = In("B9", Monk).Conditions.Single(c => c.Name == "cursed (Mucus Cloud)");
        Assert.Equal(CombatValues.Durations.UntilRemoved, curse.Duration);
        Assert.Equal(new AppliedAt(1, In("B9", "Aboleth").Id), curse.Applied);
    }

    [Fact]
    public void B10_B11_SlayerHitsFor26_LashTo56()
    {
        Assert.Equal(106, In("B10", "Aboleth").Hp);
        Assert.Equal(56, In("B11", Slayer).Hp);
    }

    [Fact]
    public void B12_Rage_SpendsAUse_LastsTheFight_ExhaustionAndGrappleRemindedOnBjornsTurn()
    {
        var bjorn = In("B12-rage", Bjorn);
        Assert.Equal(1, bjorn.Resources["rage"].Used);
        var rage = bjorn.Conditions.Single(c => c.Name == "Rage");
        Assert.Equal(CombatValues.Durations.Fight, rage.Duration);
        Assert.Equal(["all"], rage.Effect!.Resist);
        Assert.Equal(["psychic"], rage.Effect.Except);
        Assert.True(B.At["B12-rage"].Says(K.ExhaustionEffects, Bjorn, "Exhaustion 1"));
        Assert.True(B.At["B12-rage"].Says(K.ConditionEffects, Bjorn, "grappled (Aboleth)", "Disadvantage on attacks against targets other than the grappler"));
        Assert.Equal(82, In("B12", "Aboleth").Hp);
    }

    [Fact]
    public void B13_RageHalvesTheLash_BjornTo67_LegendaryTwoLeft()
    {
        Assert.Equal(67, In("B13", Bjorn).Hp);
        Assert.Equal(2, In("B13-legendary", "Aboleth").Legendary!.ActionsLeft);
        Assert.Contains(B.At["B13"].Notes, n => n.Contains("½ (resistant: Rage)", StringComparison.Ordinal));
    }

    [Fact]
    public void B14_Round2_TheMonk()
    {
        Assert.Equal(2, B.At["B14"].Next.Round);
        Assert.Equal(Monk, B.At["B14"].Next.TurnHolder!.Name);
    }

    [Fact]
    public void B15_AbolethBloodiedOnItsFirstCrossing_ResistanceTwoLeft()
    {
        Assert.Equal(4, In("B15-focus", Monk).Resources["focus"].Used);
        Assert.Equal(62, In("B15", "Aboleth").Hp);
        Assert.True(B.At["B15"].Says(K.Bloodied, "Aboleth"));
        Assert.Equal(2, In("B15-resistance", "Aboleth").Legendary!.ResistanceLeft);
    }

    [Fact]
    public void B16_PsychicDrainHeal_ServerRolls1d10_SubjectTheAboleth()
    {
        Assert.Equal(1, In("B16-legendary", "Aboleth").Legendary!.ActionsLeft);
        Assert.Equal(15, In("B16-damage", Monk).Hp);
        Assert.Equal(67, In("B16", "Aboleth").Hp);
        var heal = B.At["B16"].Changes.Single(c => c.Kind == L.Heal);
        Assert.Equal(5, heal.Amount);
        Assert.Equal("heal", heal.RollKey);
        var need = Assert.Single(CombatTracker.Needs(B.At["B16-damage"].Next, new HealOp(["aboleth"]) { Dice = "1d10" }));
        Assert.Equal(new RollNeed("heal", In("B16", "Aboleth").Id, P.Heal, "1d10"), need);
    }

    [Fact]
    public void B17_AbolethsTurn_ResetFromOneTo4Of4()
    {
        Assert.True(B.At["B17"].Says(K.LegendaryReset, "Aboleth", "reset: 4/4", "1/4"));
    }

    [Fact]
    public void B18_MonkDropsWithNineLeftOver_UnconsciousDying_PsychicNotResisted()
    {
        Assert.Equal(3, In("B18-first", Monk).Hp);
        var monk = In("B18-second", Monk);
        Assert.Equal(0, monk.Hp);
        Assert.True(monk.Dying);
        Assert.True(monk.Has("unconscious"));
        Assert.Equal(57, In("B18", Bjorn).Hp);
    }

    [Fact]
    public void B20_CriticalLashRolled4d6Plus5_19_TwoFailures_UnconsciousCrit()
    {
        var need = Assert.Single(CombatTracker.Needs(B.At["B20-legendary"].Next,
            new DamageOp(["fishman-monk"]) { Dice = "2d6+5", DamageType = "bludgeoning", Critical = true, Source = "aboleth" }));
        Assert.Equal(new RollNeed("damage", In("B20", "Aboleth").Id, P.DamageCritical, "4d6+5"), need);
        var monk = In("B20", Monk);
        Assert.Equal(new DeathSaveTally(0, 2, false), monk.DeathSaves);
        var row = B.At["B20"].Changes.Single(c => c.Kind == L.Damage);
        Assert.Equal((19, "damage"), (row.Amount, row.RollKey));
        Assert.False(JsonNode.Parse(row.Detail!)!["given"]!.GetValue<bool>());
        Assert.True(B.At["B20"].Says(K.UnconsciousCrit, Monk, "a hit from within 5 ft is a Critical Hit"));
        Assert.Equal(3, In("B20-legendary", "Aboleth").Legendary!.ActionsLeft);
    }

    [Fact]
    public void B21_BjornsTurn_TheMonksTalliesAndBjornsExhaustion()
    {
        Assert.True(B.At["B21"].Says(K.Dying, Monk, "0 HP", "0 successes, 2 failures"));
        Assert.True(B.At["B21"].Says(K.ExhaustionEffects, Bjorn, "Exhaustion 1"));
    }

    [Fact]
    public void B22_PotionFromBjorn_Rolled2d4Plus2_MonkTo7Conscious_PotionPending()
    {
        var monk = In("B22", Monk);
        Assert.Equal(7, monk.Hp);
        Assert.Equal(DeathSaveTally.Zero, monk.DeathSaves);
        Assert.False(monk.Has("unconscious"));
        Assert.True(monk.Has("prone"));
        var potion = In("B22", Bjorn).Resources["item:h-potion"];
        Assert.Equal(("Potion of Healing", 1), (potion.Name, potion.Used));
        var heal = B.At["B22"].Changes.Single(c => c.Kind == L.Heal);
        Assert.Equal((7, "heal", In("B22", Bjorn).Id), (heal.Amount, heal.RollKey, heal.ActorId));
    }

    [Fact]
    public void B24_LashResisted_BjornTo51()
    {
        Assert.Equal(13, In("B23", "Aboleth").Hp);
        Assert.Equal(51, In("B24", Bjorn).Hp);
        Assert.Equal(2, In("B24-legendary", "Aboleth").Legendary!.ActionsLeft);
    }

    [Fact]
    public void B26_AbolethDies_BothGrapplesEnd_EldritchRestoration_AllEnemiesDown()
    {
        Assert.Equal(3, B.At["B25"].Next.Round);
        var s = B.At["B26"].Next;
        Assert.True(s.Named("Aboleth").Dead);
        Assert.False(s.Named(Monk).Has("grappled"));
        Assert.False(s.Named(Bjorn).Has("grappled"));
        Assert.Equal(2, B.At["B26"].Of(K.GrappleEnded).Count);
        Assert.True(B.At["B26"].Says(K.Trait, "Eldritch Restoration"));
        Assert.True(B.At["B26"].Says(K.AllEnemiesDown, "all enemies are defeated"));
    }

    [Fact]
    public void B27_ExactlyThreeServerRolls_EachCitedWithItsTotal()
    {
        var cited = B.Log.Where(c => c.RollKey is not null).ToList();
        Assert.Equal([(L.Heal, 5), (L.Damage, 19), (L.Heal, 7)], cited.Select(c => (c.Kind, c.Amount!.Value)));
    }

    // ------------------------------------------------------------------------------------------------------------------
    // B28 and §4.3

    [Fact]
    public void B28_Bjorn_Hp51_Rage1Used_Exhaustion1Column_Xp36400_NoConditionsKept()
    {
        var write = B.End.WriteBacks.Single(w => w.Name == Bjorn);
        Assert.Equal((51, 1, 36_400), (write.After.Hp, write.After.Exhaustion, write.After.Xp));
        Assert.Equal(1, write.After.Resources["rage"].Used);
        Assert.Empty(write.After.Conditions);
        Assert.False(write.Diff.Columns.ContainsKey(SheetColumns.Conditions));
        Assert.Equal("""{"rage":{"used":1}}""", SheetJson.Serialize(write.Diff.Patches[SheetColumns.Resources]));
    }

    [Fact]
    public void B28_Monk_Hp7_Focus4Used_TheCurseKeptWithItsPartySafeSource()
    {
        var write = B.End.WriteBacks.Single(w => w.Name == Monk);
        Assert.Equal((7, 36_400), (write.After.Hp, write.After.Xp));
        Assert.Equal(4, write.After.Resources["focus"].Used);
        Assert.Equal(
            """[{"name":"cursed (Mucus Cloud)","source":"Aboleth","duration":"until_removed","note":"from The dark station (fixture), round 1"}]""",
            SheetJson.WriteConditions(write.After.Conditions));
        Assert.False(write.Diff.Columns.ContainsKey(SheetColumns.DeathSaves));
    }

    [Fact]
    public void B28_Slayer_Hp56_Xp36400()
    {
        var write = B.End.WriteBacks.Single(w => w.Name == Slayer);
        Assert.Equal((56, 36_400), (write.After.Hp, write.After.Xp));
    }

    [Fact]
    public void B28_PotionWrittenAsCurrentMinusUsed_TwoToOne()
    {
        var item = Assert.Single(B.End.Items);
        Assert.Equal(new ItemConsumption("h-potion", "e-bjorn", "Potion of Healing", 2, 1, 1), item);
    }

    [Fact]
    public void B28_Xp_InLair7200_2400EachForThree()
    {
        Assert.Equal((7_200, 7_200, 2_400, 0, 3), (B.End.Xp.Worth, B.End.Xp.Awarded, B.End.Xp.Each, B.End.Xp.Remainder, B.End.Xp.Recipients));
        Assert.Equal(3, B.End.Awards.Count);
        Assert.All(B.End.Awards, a => Assert.Equal((2_400, "encounter: The dark station (fixture)", true), (a.Amount, a.Source, a.SheetTracksXp)));
        Assert.DoesNotContain(B.End.Reminders, r => r.Kind == K.LevelUp);
    }

    [Fact]
    public void B28_TheNesterProposedDead_EldritchRestorationSaysConsiderUnknown_NotApplied()
    {
        var proposal = Assert.Single(B.End.Proposals);
        Assert.Equal(("character:the-nester", "dead"), (proposal.EntityHandle, proposal.Status));
        Assert.Equal(
            "campaign_write {\"ops\": [{\"op\": \"status\", \"ref\": \"character:the-nester\", \"status\": \"dead\"}], \"dry_run\": true, \"campaign\": \"one-piece\"}",
            proposal.Call);
        Assert.Contains("Eldritch Restoration", proposal.Text, StringComparison.Ordinal);
        Assert.Contains("consider \"unknown\"", proposal.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void B28_Summary_RageGrapplesProneEnded()
    {
        var ended = B.End.Summary.Single(s => s.StartsWith("Ended with the fight:", StringComparison.Ordinal));
        Assert.Contains("Rage (Björn Mountainfell)", ended, StringComparison.Ordinal);
        Assert.Contains("prone (The fishman monk)", ended, StringComparison.Ordinal);
        Assert.DoesNotContain("cursed", ended, StringComparison.Ordinal);
    }
}
